using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx.Logging;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace WeaponForge
{
    // Loads the "sounds" folder that sits next to the DLL and turns each
    // audio file into an AudioClip.
    //
    // The game's audio is NOT middleware - no FMOD, no Wwise. AudioManager
    // holds a plain AudioDatabase of Sfx objects, each wrapping a weighted
    // list of ordinary Unity AudioClips, played through a pooled AudioSource.
    // That is what makes custom sound possible at all: an AudioClip built at
    // runtime is indistinguishable from one the game shipped. See
    // ForgeSfxRegistry for the registration half.
    //
    // Two decode routes, for a deliberate reason:
    //   WAV is parsed HERE, synchronously. It is raw PCM in a trivial
    //        container, so decoding it ourselves means no Unity decoder, no
    //        coroutine and no waiting - the clip exists the instant the file
    //        is read.
    //   OGG / MP3 have no synchronous route in Unity at all; the only runtime
    //        decoder is UnityWebRequestMultimedia, which needs a frame. Those
    //        load in the background and are attached to their Sfx when they
    //        arrive (see Bind). Weapons are built at startup and fired much
    //        later, so the delay is invisible in practice.
    public static class ForgeSoundLibrary
    {
        private static readonly ManualLogSource Log =
            ForgeLog.Source("WeaponForge.Sounds");

        // Per-sound settings from an optional "<name>.json" beside the audio
        // file. Nullable where "inherit from the sound being replaced" is a
        // better default than any value we could pick.
        public class Options
        {
            public float? volume;
            public bool? looping;
            public bool? is3d;
            public int? priority;
            public float? repeatMinDelay;
            public bool? cancelPrevious;
            public string[] variants;
        }

        private class Entry
        {
            public string name;
            public Options options = new Options();

            // Clips that have finished decoding.
            public readonly List<AudioClip> clips = new List<AudioClip>();

            // Sfx objects already handed to a weapon that are still waiting
            // for a clip. An ogg can land after the weapon was built, so the
            // binding has to work in both directions.
            public readonly List<Sfx> waiting = new List<Sfx>();

            public int pending;
        }

        private static readonly Dictionary<string, Entry> _entries =
            new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

        private static bool _loaded;

        public static string SoundsFolder()
        {
            return Path.Combine(
                Path.GetDirectoryName(
                    Assembly.GetExecutingAssembly().Location),
                "sounds");
        }

        public static int Count
        {
            get { return _entries.Count; }
        }

        public static IEnumerable<string> Names
        {
            get { return _entries.Keys; }
        }

        public static bool Has(string name)
        {
            return !string.IsNullOrEmpty(name) &&
                   _entries.ContainsKey(name.Trim());
        }

        public static Options OptionsFor(string name)
        {
            Entry e;

            if (name != null && _entries.TryGetValue(name.Trim(), out e))
                return e.options;

            return null;
        }

        // Attach this sound's clips to an Sfx, now and as they arrive.
        public static void Bind(string name, Sfx sfx)
        {
            Entry e;

            if (sfx == null || name == null ||
                !_entries.TryGetValue(name.Trim(), out e))
            {
                return;
            }

            foreach (AudioClip clip in e.clips)
                sfx.audioClips.Add(clip, 1f);

            // Still decoding: remember the Sfx so the clip reaches it later.
            if (e.pending > 0)
                e.waiting.Add(sfx);
        }

        public static void LoadAll()
        {
            if (_loaded)
                return;

            _loaded = true;

            string folder = SoundsFolder();

            if (!Directory.Exists(folder))
            {
                Directory.CreateDirectory(folder);
                WriteReadme(folder);
                return;
            }

            // Sidecars first, so an entry's options exist before its clips
            // start arriving - and so a sidecar can declare variants that
            // stop those files from also becoming sounds in their own right.
            var claimed =
                new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string file in
                Directory.GetFiles(folder, "*.json").OrderBy(x => x))
            {
                try
                {
                    ReadSidecar(file, claimed);
                }
                catch (Exception e)
                {
                    Log.LogError(
                        "Failed to read sound settings " +
                        Path.GetFileName(file) + ": " + e.Message);
                }
            }

            foreach (string file in
                Directory.GetFiles(folder).OrderBy(x => x))
            {
                string ext = (Path.GetExtension(file) ?? string.Empty)
                    .ToLowerInvariant();

                if (ext == ".json" || ext == ".txt" || ext == ".md")
                    continue;

                if (claimed.Contains(Path.GetFileName(file)))
                    continue;

                string name = Path.GetFileNameWithoutExtension(file);

                Entry entry = EntryFor(name);
                AddFile(entry, file);
            }

            // Sidecar variants, added after the plain files so a sidecar
            // naming its own audio file does not queue it twice.
            foreach (Entry entry in _entries.Values.ToArray())
            {
                if (entry.options.variants == null)
                    continue;

                foreach (string variant in entry.options.variants)
                {
                    if (string.IsNullOrEmpty(variant))
                        continue;

                    string path = Path.Combine(folder, variant.Trim());

                    if (!File.Exists(path))
                    {
                        Log.LogWarning(
                            entry.name + ": variant '" + variant +
                            "' does not exist in the sounds folder - " +
                            "skipped.");
                        continue;
                    }

                    AddFile(entry, path);
                }
            }

            if (_entries.Count == 0)
            {
                Log.LogInfo(
                    "No sound files found in " + folder + ".");
                return;
            }

            // An entry with nothing to play would silently swallow a weapon's
            // sound: it resolves to a real Sfx id, but HasSound is false so
            // the game plays nothing and reports nothing. Almost always a
            // sidecar .json whose audio file is missing or misnamed.
            foreach (Entry e in _entries.Values)
            {
                if (e.clips.Count == 0 && e.pending == 0)
                {
                    Log.LogWarning(
                        "'" + e.name + "' has no audio to play - there is a " +
                        e.name + ".json but no matching audio file (and no " +
                        "usable \"variants\"). A weapon using it would be " +
                        "silent.");
                }
            }

            Log.LogInfo(
                "Loaded " + _entries.Count + " custom sound(s): " +
                string.Join(", ", _entries.Keys.ToArray()));
        }

        private static Entry EntryFor(string name)
        {
            Entry entry;

            if (_entries.TryGetValue(name, out entry))
                return entry;

            entry = new Entry { name = name };
            _entries[name] = entry;
            return entry;
        }

        private static void AddFile(Entry entry, string path)
        {
            string ext = (Path.GetExtension(path) ?? string.Empty)
                .ToLowerInvariant();

            string clipName = "Forge " + entry.name;

            if (ext == ".wav" || ext == ".wave")
            {
                try
                {
                    AudioClip clip =
                        WavDecoder.Decode(
                            File.ReadAllBytes(path), clipName);

                    if (clip != null)
                    {
                        entry.clips.Add(clip);
                        return;
                    }
                }
                catch (Exception e)
                {
                    Log.LogError(
                        "Could not read " + Path.GetFileName(path) +
                        ": " + e.Message);
                    return;
                }

                Log.LogError(
                    Path.GetFileName(path) +
                    " is a WAV this reader does not understand. Re-export " +
                    "it as 16-bit PCM WAV and it will work.");
                return;
            }

            AudioType type;

            if (ext == ".ogg")
                type = AudioType.OGGVORBIS;
            else if (ext == ".mp3")
                type = AudioType.MPEG;
            else if (ext == ".aif" || ext == ".aiff")
                type = AudioType.AIFF;
            else
            {
                Log.LogWarning(
                    "Ignoring " + Path.GetFileName(path) +
                    " - supported types are .wav (best), .ogg, .mp3 and " +
                    ".aiff.");
                return;
            }

            entry.pending++;

            Entry captured = entry;

            ForgeSoundLoader.Instance().Load(
                path, type, clipName,
                clip =>
                {
                    captured.pending--;

                    if (clip == null)
                        return;

                    captured.clips.Add(clip);

                    // Anything already handed to a weapon gets it now.
                    foreach (Sfx sfx in captured.waiting)
                        sfx.audioClips.Add(clip, 1f);
                });
        }

        private static void ReadSidecar(string file, HashSet<string> claimed)
        {
            JObject root = JObject.Parse(File.ReadAllText(file));

            // The sidecar's own file name IS the sound name - there is
            // deliberately no "name" key to override it. One rule ("a sound is
            // named after its file") is worth more than the flexibility, and
            // an override would let the settings and the audio drift apart
            // into two half-entries.
            Entry entry =
                EntryFor(Path.GetFileNameWithoutExtension(file).Trim());

            entry.options = new Options
            {
                volume = (float?)root["volume"],
                looping = (bool?)root["looping"],
                is3d = (bool?)root["is3d"],
                priority = (int?)root["priority"],
                repeatMinDelay = (float?)root["repeatMinDelay"],
                cancelPrevious = (bool?)root["cancelPrevious"]
            };

            var variants = root["variants"] as JArray;

            if (variants != null)
            {
                var list = new List<string>();

                foreach (JToken t in variants)
                {
                    string v = (string)t;

                    if (string.IsNullOrEmpty(v))
                        continue;

                    list.Add(v);
                    claimed.Add(v.Trim());
                }

                entry.options.variants = list.ToArray();
            }
        }

        private static void WriteReadme(string folder)
        {
            File.WriteAllText(
                Path.Combine(folder, "README.txt"),
@"CUSTOM SOUNDS
=============

Drop an audio file in this folder and its FILE NAME (without the
extension) becomes the sound's name. Then use that name in any of a
weapon's sound fields:

  ""shootSfx"": ""mylaser""

The six weapon slots are shootSfx, reloadSfx, continousShootSfx,
startSfx, releaseSfx and warmupSfx, plus explosion.sfx inside the
explosion block. Anything you don't override keeps the template's
own sound.

FILE TYPES
----------
  .wav   BEST. Decoded directly, always works, ready instantly.
         Any bit depth (8/16/24/32-bit PCM or 32-bit float).
  .ogg   Works. Decoded by the engine one frame after startup.
  .mp3   Usually works, but the decoder is platform-dependent. If a
         file stays silent, convert it to .wav.
  .aiff  Same route as ogg.

The volume slider, 3D positioning and the audio mixer all work
normally - a custom sound is a real game sound, not a bolt-on.

OPTIONAL SETTINGS
-----------------
Add a .json file with the SAME NAME as the audio file to tune it:

  mylaser.wav
  mylaser.json   ->  { ""volume"": 0.6 }

Keys (leave any of them out to inherit from the sound you replaced):
  volume          0 to 1. Start here if your sound is too loud -
                  exported audio is usually much hotter than the
                  game's.
  looping         true/false. Set automatically for
                  continousShootSfx and warmupSfx, which the game
                  holds open and stops by handle.
  is3d            true = positioned in the world (default),
                  false = flat/UI.
  priority        0-256, lower wins when many sounds compete.
  repeatMinDelay  seconds the same sound refuses to retrigger.
                  Raise it if a fast weapon sounds like a buzzsaw.
  cancelPrevious  true = a new play stops the old one.
  variants        several files, picked at RANDOM per shot - which is
                  how the game's own gunshots avoid sounding
                  mechanical:
                    { ""variants"": [""shot1.wav"", ""shot2.wav""] }
                  Files listed here don't become sounds of their own.

NOTE there is no pitch control: the game never sets AudioSource.pitch,
so pitch variation has to be baked into your files (which is what
variants are for).
");
        }

        // --- WAV ------------------------------------------------------
        // A minimal RIFF/WAVE reader. Worth writing rather than handing the
        // file to Unity because it is synchronous: the clip is ready the
        // moment the file is read, so nothing downstream has to wait or
        // re-bind.
        private static class WavDecoder
        {
            public static AudioClip Decode(byte[] bytes, string clipName)
            {
                if (bytes == null || bytes.Length < 44)
                    return null;

                if (Tag(bytes, 0) != "RIFF" || Tag(bytes, 8) != "WAVE")
                    return null;

                int channels = 0;
                int sampleRate = 0;
                int bits = 0;
                int format = 0;
                int dataOffset = -1;
                int dataLength = 0;

                // Chunk walk: a WAV is a chunk list, and real files put all
                // sorts between "fmt " and "data" (LIST, fact, cue, junk
                // padding from editors), so the offsets cannot be assumed.
                int pos = 12;

                while (pos + 8 <= bytes.Length)
                {
                    string id = Tag(bytes, pos);
                    int size = BitConverter.ToInt32(bytes, pos + 4);

                    if (size < 0)
                        break;

                    int body = pos + 8;

                    if (id == "fmt " && body + 16 <= bytes.Length)
                    {
                        format = BitConverter.ToUInt16(bytes, body);
                        channels = BitConverter.ToUInt16(bytes, body + 2);
                        sampleRate = BitConverter.ToInt32(bytes, body + 4);
                        bits = BitConverter.ToUInt16(bytes, body + 14);

                        // WAVE_FORMAT_EXTENSIBLE hides the real format in a
                        // SubFormat GUID whose first two bytes are the tag.
                        if (format == 0xFFFE && size >= 40 &&
                            body + 26 <= bytes.Length)
                        {
                            format =
                                BitConverter.ToUInt16(bytes, body + 24);
                        }
                    }
                    else if (id == "data")
                    {
                        dataOffset = body;
                        dataLength =
                            Math.Min(size, bytes.Length - body);
                    }

                    // Chunks are word-aligned: an odd size carries a pad
                    // byte that is not counted in the size field.
                    pos = body + size + (size & 1);
                }

                if (dataOffset < 0 || channels <= 0 || sampleRate <= 0)
                    return null;

                float[] samples = ToFloats(
                    bytes, dataOffset, dataLength, bits, format);

                if (samples == null || samples.Length == 0)
                    return null;

                int perChannel = samples.Length / channels;

                if (perChannel <= 0)
                    return null;

                AudioClip clip =
                    AudioClip.Create(
                        clipName, perChannel, channels, sampleRate, false);

                if (!SetData(clip, samples))
                    return null;

                clip.hideFlags = HideFlags.HideAndDontSave;
                return clip;
            }

            private static string Tag(byte[] b, int at)
            {
                if (at + 4 > b.Length)
                    return string.Empty;

                return string.Concat(
                    (char)b[at], (char)b[at + 1],
                    (char)b[at + 2], (char)b[at + 3]);
            }

            private static float[] ToFloats(
                byte[] b, int at, int length, int bits, int format)
            {
                // format 3 = IEEE float, 1 = PCM integer.
                if (format == 3)
                {
                    if (bits == 32)
                    {
                        int n = length / 4;
                        var outp = new float[n];

                        for (int i = 0; i < n; i++)
                            outp[i] =
                                BitConverter.ToSingle(b, at + i * 4);

                        return outp;
                    }

                    if (bits == 64)
                    {
                        int n = length / 8;
                        var outp = new float[n];

                        for (int i = 0; i < n; i++)
                            outp[i] =
                                (float)BitConverter.ToDouble(
                                    b, at + i * 8);

                        return outp;
                    }

                    return null;
                }

                switch (bits)
                {
                    case 8:
                    {
                        // 8-bit WAV is UNSIGNED, unlike every other depth.
                        var outp = new float[length];

                        for (int i = 0; i < length; i++)
                            outp[i] = (b[at + i] - 128) / 128f;

                        return outp;
                    }

                    case 16:
                    {
                        int n = length / 2;
                        var outp = new float[n];

                        for (int i = 0; i < n; i++)
                            outp[i] =
                                BitConverter.ToInt16(b, at + i * 2) /
                                32768f;

                        return outp;
                    }

                    case 24:
                    {
                        int n = length / 3;
                        var outp = new float[n];

                        for (int i = 0; i < n; i++)
                        {
                            int o = at + i * 3;
                            int v = (b[o] << 8) | (b[o + 1] << 16) |
                                    (b[o + 2] << 24);
                            outp[i] = (v >> 8) / 8388608f;
                        }

                        return outp;
                    }

                    case 32:
                    {
                        int n = length / 4;
                        var outp = new float[n];

                        for (int i = 0; i < n; i++)
                            outp[i] =
                                BitConverter.ToInt32(b, at + i * 4) /
                                2147483648f;

                        return outp;
                    }
                }

                return null;
            }

            private static MethodInfo _setData;
            private static bool _setDataLooked;

            // Resolved by reflection for the same reason Texture2D.LoadImage
            // and Gradient.SetKeys were: Unity 6 added ReadOnlySpan<float>
            // overloads, and .NET Framework 4.7.2 has no such type, so a
            // direct call risks binding to an overload that cannot compile.
            // Reflection picks the float[] one by signature at runtime.
            private static bool SetData(AudioClip clip, float[] samples)
            {
                if (!_setDataLooked)
                {
                    _setDataLooked = true;

                    foreach (MethodInfo m in
                        typeof(AudioClip).GetMethods(
                            BindingFlags.Public | BindingFlags.Instance))
                    {
                        if (m.Name != "SetData")
                            continue;

                        ParameterInfo[] p = m.GetParameters();

                        if (p.Length == 2 &&
                            p[0].ParameterType == typeof(float[]) &&
                            p[1].ParameterType == typeof(int))
                        {
                            _setData = m;
                            break;
                        }
                    }

                    if (_setData == null)
                    {
                        Log.LogError(
                            "This Unity build has no AudioClip.SetData" +
                            "(float[], int), so WAV files cannot be " +
                            "uploaded. Use .ogg instead.");
                    }
                }

                if (_setData == null)
                    return false;

                object ok =
                    _setData.Invoke(clip, new object[] { samples, 0 });

                return !(ok is bool) || (bool)ok;
            }
        }
    }
}
