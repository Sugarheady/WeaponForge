using System;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.Audio;

namespace WeaponForge
{
    // Registers a custom sound as a real Sfx in the game's own AudioDatabase.
    //
    // A weapon's sound fields (shootSfx, reloadSfx, ...) are plain strings
    // holding an Sfx GUID. AudioManager.PlaySfxInternal looks that guid up in
    // audioDatabase.sfxs and plays the Sfx's clip on a pooled AudioSource. So
    // the whole job is: build an Sfx around our AudioClip, add it to that
    // list, and write its guid into the weapon field.
    //
    // Registering rather than intercepting playback is what makes everything
    // else free - pooling, 3D position, the volume sliders, repeat throttling,
    // stop-by-handle for looping sounds. A custom sound becomes a real game
    // sound instead of a bolt-on that misses half the plumbing.
    //
    // The one setting that MUST be inherited is mixerGroup. Leave it null and
    // the AudioSource bypasses the Effects mixer group, so the sound ignores
    // the player's SFX volume slider and plays at full blast - which reads as
    // "the mod is broken" rather than "a field was missed".
    public static class ForgeSfxRegistry
    {
        private static readonly ManualLogSource Log =
            ForgeLog.Source("WeaponForge.Sounds");

        private static AudioDatabase _database;
        private static bool _searched;

        private static AudioMixerGroup _fallbackGroup;
        private static bool _fallbackSearched;

        // One Sfx per (sound, inherited settings) pair - several weapons
        // naming the same sound in the same slot share one entry, because
        // PlaySfxInternal scans the list linearly on every sound played.
        private static readonly Dictionary<string, string> _guids =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Turn a custom sound name into an Sfx guid a weapon field can hold.
        //
        // inheritFrom is the guid already in that slot (the template's own
        // sound). Everything the sidecar does not specify is copied from it,
        // so a replacement behaves like the sound it replaced.
        //
        // forceLoop is for continousShootSfx / warmupSfx: the game starts
        // those, keeps the handle, and stops them later - which only works if
        // the Sfx loops. A one-shot there would play once and never restart.
        //
        // Returns null if the sound is unknown, so the caller can leave the
        // field alone.
        public static string Resolve(
            string soundName,
            string inheritFrom,
            bool forceLoop,
            string fileName)
        {
            if (string.IsNullOrEmpty(soundName))
                return null;

            string name = soundName.Trim();

            // CUSTOM FIRST, THEN THE GAME'S OWN - the same order every sprite
            // key uses, and for the same reason it had to be added there.
            //
            // Until 2026-09-02 this returned null for anything that was not a
            // custom file, and the only other thing a sound field would accept
            // was a raw Sfx **guid** (`Find` matches `s.guid` and nothing
            // else). So all 259 of the game's own sounds were reachable only by
            // typing a string like
            // "0db9a68c-7055-43bf-8490-dd42f1409f4c", and nothing anywhere
            // mapped a name to one. That is why he asked *"what is the SFX for
            // popping the bubble that provides fuel?"* - there was no way to
            // find out and no way to say it.
            //
            // Exactly the shape of the seven sprite keys that could not see
            // stock art: **a lookup that can only search half its namespace.**
            // See SOUNDS.txt for the list.
            if (!ForgeSoundLibrary.Has(name))
                return StockGuid(name, fileName);

            AudioDatabase db = Database();

            if (db == null)
            {
                Log.LogWarning(
                    fileName + ": the game's audio database could not be " +
                    "found, so custom sound '" + name + "' was skipped. " +
                    "The weapon keeps the template's sound.");
                return null;
            }

            Sfx template = Find(db, inheritFrom);

            string key =
                name + "|" +
                (template != null ? template.guid : "-") + "|" +
                (forceLoop ? "loop" : "once");

            string cached;

            if (_guids.TryGetValue(key, out cached))
                return cached;

            var sfx = new Sfx();
            sfx.guid = "forge:" + name.ToLowerInvariant() +
                       (forceLoop ? ":loop" : string.Empty) +
                       (template != null ? ":" + template.name : string.Empty);
            sfx.name = "Forge " + name;

            // Odin validation is an editor-time concern, but say so anyway -
            // this Sfx was never authored in the inspector.
            sfx.ignoreValidation = true;

            ApplySettings(sfx, template, name, forceLoop);

            ForgeSoundLibrary.Bind(name, sfx);

            db.sfxs.Add(sfx);
            _guids[key] = sfx.guid;

            Log.LogInfo(
                fileName + ": sound '" + name + "' registered as " +
                sfx.guid +
                (template != null
                    ? " (settings copied from '" + template.name + "')"
                    : " (no sound in that slot to copy settings from)") +
                (sfx.looping ? " [looping]" : string.Empty));

            return sfx.guid;
        }

        private static void ApplySettings(
            Sfx sfx,
            Sfx template,
            string soundName,
            bool forceLoop)
        {
            // Start from the sound being replaced.
            if (template != null)
            {
                sfx.volume = template.volume;
                sfx.priority = template.priority;
                sfx.is3d = template.is3d;
                sfx.mixerGroup = template.mixerGroup;
                sfx.looping = template.looping;
                sfx.repeatMinDelay = template.repeatMinDelay;
                sfx.cancelPrevious = template.cancelPrevious;
            }
            else
            {
                sfx.volume = 1f;
                sfx.priority = 128;
                sfx.is3d = true;
                sfx.mixerGroup = FallbackGroup();
                sfx.looping = false;
                sfx.repeatMinDelay = 0.01f;
                sfx.cancelPrevious = false;
            }

            if (sfx.mixerGroup == null)
                sfx.mixerGroup = FallbackGroup();

            // The slot wins over anything inherited: the game holds these
            // open by handle, so they have to loop.
            if (forceLoop)
                sfx.looping = true;

            // The file's own sidecar wins over everything.
            ForgeSoundLibrary.Options o =
                ForgeSoundLibrary.OptionsFor(soundName);

            if (o == null)
                return;

            if (o.volume.HasValue)
                sfx.volume = Mathf.Clamp01(o.volume.Value);

            if (o.priority.HasValue)
                sfx.priority = Mathf.Clamp(o.priority.Value, 0, 256);

            if (o.is3d.HasValue)
                sfx.is3d = o.is3d.Value;

            if (o.repeatMinDelay.HasValue)
                sfx.repeatMinDelay = Mathf.Max(0f, o.repeatMinDelay.Value);

            if (o.cancelPrevious.HasValue)
                sfx.cancelPrevious = o.cancelPrevious.Value;

            if (o.looping.HasValue)
            {
                sfx.looping = o.looping.Value;

                if (forceLoop && !o.looping.Value)
                {
                    Log.LogWarning(
                        soundName + ": this slot is a HELD sound (the game " +
                        "starts it, then stops it by handle), and the " +
                        "sidecar set looping false - it will play once and " +
                        "not restart. Remove \"looping\" unless that is " +
                        "what you want.");
                }
            }
        }

        // Does the game already know this sound id? Used to tell a real game
        // sound apart from a typo, so only the typo gets a warning.
        //
        // Answers TRUE when the database cannot be reached: without it there
        // is no way to tell the two apart, and crying wolf about every stock
        // sound in every file would be worse than staying quiet.
        public static bool KnownGuid(string guid)
        {
            AudioDatabase db = Database();

            if (db == null)
                return true;

            return Find(db, guid) != null;
        }

        // One of the GAME's own sounds, named the way the audio database names
        // it - "Cells/Fuel", "Weapons/Popper/Shoot", "UI/OK". Returns its
        // guid, which is what a weapon field actually holds.
        //
        // Case-insensitive, and a raw guid still works, so nothing already
        // written changes. A "/" in the name is the giveaway that somebody
        // meant a game sound rather than a file.
        public static string StockGuid(string name, string fileName)
        {
            AudioDatabase db = Database();

            if (db == null || string.IsNullOrEmpty(name))
                return null;

            Sfx found = null;

            foreach (Sfx s in db.sfxs)
            {
                if (s == null || string.IsNullOrEmpty(s.name))
                    continue;

                if (string.Equals(s.name, name,
                                  StringComparison.OrdinalIgnoreCase))
                {
                    found = s;
                    break;
                }
            }

            if (found == null)
                return null;

            // 70 OF THE 259 ENTRIES HAVE NO CLIP AT ALL. They are declared in
            // the database and silent - `Items/Resources/Fuel`,
            // `Weapons/Popper/Impact` and 68 others. Naming one resolves
            // perfectly and plays nothing, which is the silent-fallback shape
            // that costs a test round, so it is called out here rather than
            // discovered in game.
            if (found.audioClips == null || found.audioClips.Items == null ||
                found.audioClips.Items.Count == 0)
            {
                Log.LogWarning(
                    fileName + ": '" + found.name + "' is a real sound in the " +
                    "game's audio database but it has NO CLIP - 70 of the " +
                    "259 entries are declared and empty like this, so it " +
                    "will be silent. See SOUNDS.txt, which marks every empty " +
                    "one.");
            }

            return found.guid;
        }

        private static Sfx Find(AudioDatabase db, string guid)
        {
            if (db == null || string.IsNullOrEmpty(guid))
                return null;

            foreach (Sfx s in db.sfxs)
            {
                if (s != null && s.guid == guid)
                    return s;
            }

            return null;
        }

        private static AudioDatabase Database()
        {
            if (_searched && _database != null)
                return _database;

            _searched = true;

            UnityEngine.Object[] all =
                Resources.FindObjectsOfTypeAll(typeof(AudioDatabase));

            // Prefer the fullest one: FindObjectsOfTypeAll can turn up an
            // empty placeholder asset alongside the real database.
            AudioDatabase best = null;

            foreach (UnityEngine.Object obj in all)
            {
                var db = obj as AudioDatabase;

                if (db == null || db.sfxs == null)
                    continue;

                if (best == null || db.sfxs.Count > best.sfxs.Count)
                    best = db;
            }

            _database = best;
            return _database;
        }

        // Any stock Sfx's mixer group will do - they all route through the
        // same Effects bus, which is what makes the volume slider work.
        private static AudioMixerGroup FallbackGroup()
        {
            if (_fallbackSearched)
                return _fallbackGroup;

            _fallbackSearched = true;

            AudioDatabase db = Database();

            if (db != null)
            {
                foreach (Sfx s in db.sfxs)
                {
                    if (s != null && s.mixerGroup != null)
                    {
                        _fallbackGroup = s.mixerGroup;
                        return _fallbackGroup;
                    }
                }
            }

            Log.LogWarning(
                "No audio mixer group could be found to route custom " +
                "sounds through, so they will ignore the in-game SFX " +
                "volume slider.");

            return null;
        }
    }
}
