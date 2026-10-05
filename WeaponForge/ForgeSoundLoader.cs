using System;
using System.Collections;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.Networking;

namespace WeaponForge
{
    // Runs the coroutine that decodes a compressed audio file (ogg / mp3).
    //
    // WAV is decoded synchronously by ForgeSoundLibrary itself, but Unity has
    // no synchronous decoder for anything else - the only runtime route is
    // UnityWebRequestMultimedia, which needs a frame to complete. So this
    // exists purely to own that coroutine.
    //
    // It has to live on an ACTIVE GameObject: coroutines do not run on
    // inactive ones, which rules out the hidden holder VisualCustomizer uses
    // for prefab clones (that one is inactive on purpose, to keep Awake from
    // firing on templates).
    public class ForgeSoundLoader : MonoBehaviour
    {
        private static readonly ManualLogSource Log =
            ForgeLog.Source("WeaponForge.Sounds");

        private static ForgeSoundLoader _instance;

        public static ForgeSoundLoader Instance()
        {
            if (_instance != null)
                return _instance;

            var go = new GameObject("Forge Sound Loader");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;

            _instance = go.AddComponent<ForgeSoundLoader>();
            return _instance;
        }

        public void Load(
            string path,
            AudioType type,
            string clipName,
            Action<AudioClip> done)
        {
            StartCoroutine(LoadRoutine(path, type, clipName, done));
        }

        private IEnumerator LoadRoutine(
            string path,
            AudioType type,
            string clipName,
            Action<AudioClip> done)
        {
            // AbsoluteUri, not "file://" + path: it escapes the spaces and
            // drive colon that a Windows path is full of.
            string uri;

            try
            {
                uri = new Uri(path).AbsoluteUri;
            }
            catch (Exception e)
            {
                Log.LogError(
                    "Could not turn '" + path + "' into a URL: " + e.Message);
                done(null);
                yield break;
            }

            using (UnityWebRequest req =
                UnityWebRequestMultimedia.GetAudioClip(uri, type))
            {
                // Not streamed: streaming is where the platform limits on mp3
                // bite, and a local file is small enough to decode whole.
                var handler = req.downloadHandler as DownloadHandlerAudioClip;

                if (handler != null)
                    handler.streamAudio = false;

                yield return req.SendWebRequest();

                if (req.result != UnityWebRequest.Result.Success)
                {
                    Log.LogError(
                        "Could not decode '" + clipName + "' (" + type +
                        "): " + req.error +
                        ". If this is an mp3, converting it to .wav or .ogg " +
                        "is the reliable fix - mp3 support depends on the " +
                        "platform's decoder.");
                    done(null);
                    yield break;
                }

                AudioClip clip = DownloadHandlerAudioClip.GetContent(req);

                if (clip == null)
                {
                    Log.LogError(
                        "'" + clipName + "' decoded to nothing. Try " +
                        "converting it to .wav.");
                    done(null);
                    yield break;
                }

                clip.name = clipName;
                clip.hideFlags = HideFlags.HideAndDontSave;
                done(clip);
            }
        }
    }
}
