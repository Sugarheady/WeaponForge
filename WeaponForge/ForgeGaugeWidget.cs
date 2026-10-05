using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace WeaponForge
{
    // The two cloned widgets: the filling bar and the flashing bubble.
    //
    // WE CLONE, WE DO NOT BORROW, and that decision buys three things at the
    // cost of one GameObject each:
    //
    //   1. NO CONTENTION. `Ship.Update` writes `SetProgress(false, 0f)` to the
    //      real bar sixty times a second whether it is showing or not, so
    //      anything writing the original is a coin flip on execution order.
    //      The fuel warning is worse, not better: the game writes its two bools
    //      only when the fuel LEVEL changes, so our writes would survive for a
    //      while and then be stomped at a moment nobody can predict.
    //   2. THE ORIGINALS KEEP WORKING. The self-destruct bar is the last thing
    //      a player sees before they die on purpose. It is not ours to reuse.
    //   3. COLOUR, SIZE AND POSITION BECOME OURS. Recolouring the original
    //      would recolour the self-destruct bar too - it is the same Image.
    //
    // The clone comes with the game's Animator, its canvas, its sorting layer
    // and its pop-in animation for free, which is most of the work.
    public static class ForgeGaugeWidget
    {
        internal static readonly ManualLogSource Log =
            ForgeLog.Source("WeaponForge.Gauge");

        // A readout must never cost the gun, and audio has one failure mode
        // that is not about us: `AudioManager.PlaySfx` resolves its service
        // through `ServiceLocator`, which can be empty during a scene change.
        // Said once rather than swallowed, following the house `_said*`
        // convention that ForgeDiagnosticGates sweeps at run entry.
        private static bool _saidSoundFailed;

        internal static void Complain(Exception e)
        {
            if (_saidSoundFailed)
                return;

            _saidSoundFailed = true;

            Log.LogWarning(
                "a readout sound could not be played, so the gauge and bubble " +
                "are running silent. Everything else about them still works. " +
                "Said once per run: " + e.Message);
        }

        // Fire and forget.
        internal static void Play(string guid)
        {
            if (string.IsNullOrEmpty(guid))
                return;

            try { AudioManager.PlaySfx(guid); }
            catch (Exception e) { Complain(e); }
        }

        // Start a held sound and keep its handle, -1 meaning nothing playing.
        internal static int Start(string guid)
        {
            if (string.IsNullOrEmpty(guid))
                return -1;

            try { return AudioManager.PlaySfx(guid); }
            catch (Exception e) { Complain(e); return -1; }
        }

        // ------------------------------------------------------------------
        // The bar
        // ------------------------------------------------------------------

        public class Bar
        {
            private readonly ShipSelfDestructVisual _visual;
            private readonly Image _fill;
            private readonly RectTransform _rect;

            // -1 is AudioManager's own "no voice" handle: `PlaySfx` returns it
            // for an unknown or clipless sound, and `Stop` on it is a
            // dictionary miss. So one field covers "not playing" and "asked
            // for a sound that does not exist" without a second flag.
            private int _loopHandle = -1;
            private string _loopGuid;
            private bool _showing;

            public Bar(ShipSelfDestructVisual visual, Image fill)
            {
                _visual = visual;
                _fill = fill;
                _rect = visual.transform as RectTransform;
            }

            public bool Alive { get { return _visual != null; } }

            public void Show(float value, ForgeGauge.Bar cfg)
            {
                if (_visual == null)
                    return;

                _visual.SetProgress(true, Mathf.Clamp01(value));

                if (_fill != null)
                {
                    _fill.color = cfg.hasHotColor
                        ? Color.Lerp(cfg.color, cfg.hotColor, Mathf.Clamp01(value))
                        : cfg.color;
                }

                Sound(cfg);
            }

            public void Hide()
            {
                if (_visual != null)
                    _visual.SetProgress(false, 0f);

                if (!_showing)
                    return;

                // ONLY ON THE FALLING EDGE. `Hide` is called every frame the
                // bar is off screen, and the game's own self-destruct code
                // calls `AudioManager.Stop` per frame in the same shape - but
                // there is no reason to copy that, and the edge is what makes
                // the one-shot on the way back up correct.
                _showing = false;

                StopLoop();
            }

            // Edge-triggered: a one-shot as the bar arrives, and a held loop
            // for as long as it stays. Called from Show, so it runs every
            // frame the bar is up and must do nothing on all but the first.
            private void Sound(ForgeGauge.Bar cfg)
            {
                bool rising = !_showing;

                _showing = true;

                if (rising && !string.IsNullOrEmpty(cfg.sound))
                {
                    // No position, exactly as `Ship.Update` plays the real
                    // meter's start sound: a readout belongs at the listener,
                    // not somewhere in the world.
                    Play(cfg.sound);
                }

                // A different weapon's block can take the bar over mid-hold,
                // so the loop follows the config rather than only the edge.
                if (!rising && _loopGuid == cfg.loopSound)
                    return;

                if (!rising)
                    StopLoop();

                if (string.IsNullOrEmpty(cfg.loopSound))
                {
                    _loopGuid = cfg.loopSound;
                    return;
                }

                _loopGuid = cfg.loopSound;
                _loopHandle = Start(cfg.loopSound);
            }

            private void StopLoop()
            {
                if (_loopHandle != -1)
                {
                    try { AudioManager.Stop(_loopHandle); }
                    catch (Exception e) { Complain(e); }
                }

                _loopHandle = -1;
                _loopGuid = null;
            }

            public void Place(ForgeGauge.Bar cfg)
            {
                if (_rect == null)
                    return;

                _rect.anchoredPosition = cfg.offset;

                // SCALE GOES ON THE ROOT, NOT ON `Visual`. The game's Animator
                // animates `Visual`'s localScale - that is the whole pop-in -
                // so scaling there would be overwritten every frame the clip
                // plays, and would fight the overshoot.
                _rect.localScale = Vector3.one * Mathf.Max(0.05f, cfg.scale);
            }

            public void Destroy()
            {
                // BEFORE the GameObject goes. The voice belongs to a pooled
                // AudioSource on the AudioManager, not to this widget, so
                // destroying the bar does NOT stop its loop - it would hold
                // one pooled source and keep whining for the rest of the run.
                StopLoop();

                if (_visual != null)
                    UnityEngine.Object.Destroy(_visual.gameObject);
            }
        }

        public static Bar MakeBar(Ship ship, ForgeGauge.Bar cfg)
        {
            if (ship == null || ship.selfDestructVisual == null)
                return null;

            GameObject src = ship.selfDestructVisual.gameObject;

            if (src.transform.parent == null)
                return null;

            GameObject go =
                UnityEngine.Object.Instantiate(src, src.transform.parent);

            go.name = "Forge Gauge";

            var visual = go.GetComponent<ShipSelfDestructVisual>();

            if (visual == null)
            {
                UnityEngine.Object.Destroy(go);
                return null;
            }

            // The Fill is the FILLED image; its parent is the plain track. Found
            // by draw type rather than by name so a renamed child cannot break
            // it silently.
            Image fill = null;

            foreach (Image img in go.GetComponentsInChildren<Image>(true))
            {
                if (img != null && img.type == Image.Type.Filled)
                {
                    fill = img;
                    break;
                }
            }

            var bar = new Bar(visual, fill);

            bar.Place(cfg);
            bar.Hide();

            return bar;
        }

        // ------------------------------------------------------------------
        // The bubble
        // ------------------------------------------------------------------

        public class Bubble
        {
            private readonly Animator _animator;
            private readonly Image _image;
            private readonly RectTransform _rect;
            private readonly GameObject _inner;
            private readonly FeedbackAnimEventPlayer _feedback;

            private int _lastLevel;

            public Bubble(
                Animator animator, Image image, GameObject inner,
                FeedbackAnimEventPlayer feedback)
            {
                _animator = animator;
                _image = image;
                _inner = inner;
                _feedback = feedback;
                _rect = animator.transform as RectTransform;
            }

            public bool Alive { get { return _animator != null; } }

            // level: 0 hidden, 1 slow, 2 fast.
            public void Set(int level, ForgeGauge.Bubble cfg)
            {
                if (_animator == null)
                    return;

                if (cfg.mode == ForgeGauge.Mode.Steady)
                {
                    Steady(level > 0);

                    // A STEADY BUBBLE CANNOT BEEP PER FLASH BECAUSE IT DOES
                    // NOT FLASH, and the Animator that carries the sound is
                    // switched off here anyway. So it pings once as it comes
                    // up, and again if it escalates from slow to fast. A
                    // build warning would have been the cheaper answer and
                    // the wrong one - a sound key that resolves, logs nothing
                    // and plays nothing is the silent-fallback shape.
                    Edge(level, cfg);
                    return;
                }

                if (!_animator.enabled)
                    _animator.enabled = true;

                _animator.SetBool("Slow", level == 1);
                _animator.SetBool("Fast", level >= 2);

                // Only if the clone somehow arrived without the component
                // that plays the clips' own events - then this is the whole
                // sound rather than a fallback nobody mentioned.
                if (_feedback == null)
                    Edge(level, cfg);
                else
                    _lastLevel = level;
            }

            private void Edge(int level, ForgeGauge.Bubble cfg)
            {
                if (level > _lastLevel && level > 0)
                    Play(level >= 2 ? cfg.fastSound : cfg.slowSound);

                _lastLevel = level;
            }

            // THE ANIMATOR HAS NO "ON, NOT FLASHING" STATE - only Disabled,
            // Slow and Fast. So steady mode switches it off and drives the
            // object directly.
            //
            // Resetting localScale is NOT optional. The clips animate both the
            // active flag and the scale of `Visual`, so whatever the last clip
            // left behind would stick and an always-on bubble would appear at
            // the wrong size.
            private void Steady(bool on)
            {
                if (_animator.enabled)
                    _animator.enabled = false;

                if (_inner == null)
                    return;

                if (_inner.activeSelf != on)
                    _inner.SetActive(on);

                if (on)
                    _inner.transform.localScale = Vector3.one;
            }

            public void Place(ForgeGauge.Bubble cfg)
            {
                if (_rect != null)
                {
                    _rect.anchoredPosition = cfg.offset;
                    _rect.localScale =
                        Vector3.one * Mathf.Max(0.05f, cfg.scale);
                }

                // Sounds travel with the rest of the look, so a bubble another
                // weapon's block takes over changes voice as well as colour.
                Voice(cfg);

                if (_image == null)
                    return;

                if (cfg.art != null)
                    _image.sprite = cfg.art;

                if (cfg.hasColor)
                    _image.color = cfg.color;
            }

            // POINT THE INHERITED FEEDBACK PLAYER AT OUR SOUNDS, OR AT NONE.
            //
            // The clip events are the game's, they fire at time 0 of a LOOPING
            // clip, and the component they call is on the object we cloned. So
            // this is a steering wheel on a machine that is already running -
            // the only question is what guid it reads. Null means silent, and
            // null is the default.
            //
            // Two things this does NOT do, both deliberate:
            //   - It does not delete the component. With no receiver, Unity
            //     logs "'PlayFeedback' AnimationEvent has no receiver" on
            //     every flash - trading an unwanted alarm for unwanted spam.
            //   - It does not empty the list. `PlayFeedback` warns by name
            //     when an id is missing, so an empty list is the same spam one
            //     layer in. The ids stay; only the payload changes.
            //
            // It also drops the shake and rumble presets, which is right: a
            // hot gun should not kick the camera. Both are null on the stock
            // fuel warning today, so nothing is lost now and nothing is
            // inherited later.
            private void Voice(ForgeGauge.Bubble cfg)
            {
                if (_feedback == null)
                    return;

                FieldInfo field = FeedbackField();

                if (field == null)
                    return;

                var fresh = new List<FeedbackAnimEventPlayer.Feedback>();

                // Keep the ids the CLIPS actually ask for rather than the two
                // we can read here today: `PlayFeedback` matches by id, so a
                // renamed state in a later build would otherwise turn every
                // flash into a warning.
                string[] ids = ExistingIds(field);

                for (int i = 0; i < ids.Length; i++)
                {
                    bool fast = ids[i] != null && (
                        ids[i].IndexOf("crit",
                            StringComparison.OrdinalIgnoreCase) >= 0 ||
                        ids[i].IndexOf("fast",
                            StringComparison.OrdinalIgnoreCase) >= 0);

                    var f = new FeedbackAnimEventPlayer.Feedback();

                    f.id = ids[i];
                    f.sfx = fast ? cfg.fastSound : cfg.slowSound;

                    fresh.Add(f);
                }

                // A NEW LIST, NEVER A MUTATION OF THE ONE WE FOUND. Unity
                // deep-copies a serialized List through Instantiate, so the
                // clone should already own its own - but "should" is doing
                // real work in that sentence, and the cost of being wrong is
                // silencing the player's REAL low-fuel alarm for the run.
                // Assigning a fresh list makes the original safe whether the
                // copy was deep or not.
                try { field.SetValue(_feedback, fresh); }
                catch (Exception e) { Complain(e); }
            }

            private string[] ExistingIds(FieldInfo field)
            {
                try
                {
                    var current =
                        field.GetValue(_feedback)
                            as List<FeedbackAnimEventPlayer.Feedback>;

                    if (current != null && current.Count > 0)
                    {
                        var ids = new string[current.Count];

                        for (int i = 0; i < current.Count; i++)
                            ids[i] = current[i].id;

                        return ids;
                    }
                }
                catch (Exception e)
                {
                    Complain(e);
                }

                // What the two stock clips pass today, as a floor.
                return new[] { "Low", "Critical" };
            }

            public void Destroy()
            {
                if (_animator != null)
                    UnityEngine.Object.Destroy(_animator.gameObject);
            }
        }

        // Latched BEFORE the call that can throw, per the house rule that cost
        // a per-frame retry in ForgeWavePatch.
        private static bool _feedbackFieldSearched;
        private static FieldInfo _feedbackField;

        private static FieldInfo FeedbackField()
        {
            if (_feedbackFieldSearched)
                return _feedbackField;

            _feedbackFieldSearched = true;

            try
            {
                _feedbackField = AccessTools.Field(
                    typeof(FeedbackAnimEventPlayer), "feedbacks");
            }
            catch (Exception e)
            {
                Complain(e);
                return null;
            }

            if (_feedbackField == null)
            {
                Log.LogWarning(
                    "bubble: the game's feedback list could not be found, so " +
                    "a cloned bubble keeps the low-fuel alarm it was copied " +
                    "from and \"slowSound\" / \"fastSound\" do nothing. " +
                    "Everything else about the bubble still works.");
            }

            return _feedbackField;
        }

        public static Bubble MakeBubble(Ship ship, ForgeGauge.Bubble cfg)
        {
            if (ship == null || ship.fuelWarningAnimator == null)
                return null;

            GameObject src = ship.fuelWarningAnimator.gameObject;

            if (src.transform.parent == null)
                return null;

            GameObject go =
                UnityEngine.Object.Instantiate(src, src.transform.parent);

            go.name = "Forge Bubble";

            var animator = go.GetComponent<Animator>();

            if (animator == null)
            {
                UnityEngine.Object.Destroy(go);
                return null;
            }

            Image image = go.GetComponentInChildren<Image>(true);

            // `Visual` is the child the clips switch on and off; steady mode
            // needs it directly. Taken as "the animator's only child" rather
            // than by name for the same reason the fill is found by type.
            GameObject inner = animator.transform.childCount > 0
                ? animator.transform.GetChild(0).gameObject
                : null;

            // AN ANIMATION EVENT IS DELIVERED TO THE ANIMATOR'S OWN
            // GAMEOBJECT, so this is the only place the receiver can be -
            // `GetComponent`, not `GetComponentInChildren`.
            var feedback = go.GetComponent<FeedbackAnimEventPlayer>();

            var bubble = new Bubble(animator, image, inner, feedback);

            animator.SetBool("Slow", false);
            animator.SetBool("Fast", false);

            // Place() rewrites the inherited sounds, and it runs in the same
            // frame as the Instantiate above - before the Animator has
            // evaluated anything - so the fuel alarm never gets one free
            // flash on the way past.
            bubble.Place(cfg);

            return bubble;
        }
    }
}
