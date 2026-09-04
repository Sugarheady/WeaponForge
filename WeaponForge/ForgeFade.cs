using System;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;

namespace WeaponForge
{
    // Fade a shot in as it leaves the barrel and out as it dies.
    //
    // "If there isn't a way to add a physic and non physic projectile fade, id
    // like to add that feature." There wasn't: nothing in PUNK fades a
    // projectile. Every ammo prefab is a SpriteRenderer at full alpha for its
    // whole life and simply vanishes.
    //
    // WHY THIS IS A COMPONENT AND NOT A SETTING. The fade has to be driven per
    // frame against the shot's own clock, and the two projectile classes keep
    // completely different clocks:
    //
    //   Projectile         `ShotTime` and `StartSpeed` are public, `RangeData`
    //                      and `LifetimeData` are public - so how far through
    //                      its life a shot is can be computed exactly.
    //   PhysicsProjectile  bounded by `lifetimeData` only. `rangeData` is
    //                      completely inert on a lobbed shot - the game
    //                      computes `timeToReachRange` for one and then never
    //                      reads it again.
    //
    // So both get the same component and it asks whichever question that class
    // can answer. When neither bound exists (a lobbed mine with no lifetime),
    // the fade-out simply never triggers and the fade-in still works - which is
    // the honest answer rather than inventing a lifetime.
    //
    // IT RIDES THE PROJECTILE PREFAB, so it must not hold a Config reference -
    // Unity drops those on every clone and the whole feature would silently do
    // nothing on every shot. Two floats and a bool serialize fine, which is why
    // this needs no ForgeConfigTable entry.
    public class ForgeFade : MonoBehaviour
    {
        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("WeaponForge.Fade");

        // Seconds of fade at each end. Serialized, so plain fields.
        public float inSeconds;
        public float outSeconds;

        // Fade the TRAIL and glow children too, or only the bullet itself.
        public bool everything = true;

        // A fallback life for a shot with no range and no lifetime, so
        // `outSeconds` still means something on a lobbed mine. 0 = no fade-out.
        public float assumeLife;

        private readonly List<SpriteRenderer> _sprites =
            new List<SpriteRenderer>();
        private readonly List<float> _alpha = new List<float>();

        private IProjectile _shot;
        private float _born;
        private Vector2 _bornAt;
        private float _life;          // total expected, or 0 if unknowable
        private bool _ready;

        // Once per run: this runs on every shot of every fading weapon.
        private static bool _said;

        // SECOND INSTRUMENT, for round 12.
        //
        // The first one reports what the fade FOUND at spawn, and on round 11 it
        // said everything was fine - "1 sprite renderer(s), first alpha 1,
        // measured life 8s, in 3s out 3s. Both ends should be visible." - while
        // he reported *"it doesn't appear to be fading the projectile at all."*
        // So the setup is not the question any more; what happens per frame is.
        //
        // These three numbers separate every remaining candidate, and no
        // guess of mine does:
        //   ticks 0        -> Update is not running at all (component disabled,
        //                     or the object is destroyed before a second frame)
        //   ticks > 0 and
        //   readback == f  -> WE ARE PAINTING CORRECTLY and the fade is real, so
        //                     the problem is that it cannot be seen (too short,
        //                     too small, wrong renderer of several)
        //   readback != f  -> something else writes this renderer's colour after
        //                     us, which is the only hypothesis I could not rule
        //                     out by reading the code
        private static bool _saidTick;
        private int _ticks;
        private float _lastF = -1f;

        private void Start()
        {
            try
            {
                Collect();
                Measure();
                _born = Time.time;
                _bornAt = transform.position;
                _ready = true;

                // ONE LINE PER RUN, and it is here because guessing failed.
                // "fade does nothing" was reported twice (R10 tests 27 and 29)
                // with a build log that said the fade applied - so the question
                // is not whether it built but what it found on the shot. These
                // three numbers separate every candidate: 0 renderers means the
                // component landed somewhere with no sprite; a life of 0 with an
                // outSeconds set means nothing bounds the shot, so there is no
                // "dying" for it to fade into; and the alpha says whether
                // something else owns the colour.
                if (!_said)
                {
                    _said = true;

                    Log.LogInfo(
                        "fade RUNTIME on '" + gameObject.name + "': " +
                        _sprites.Count + " sprite renderer(s), first alpha " +
                        ((_alpha.Count > 0)
                            ? _alpha[0].ToString("0.##") : "n/a") +
                        ", measured life " +
                        ((_life > 0f) ? (_life.ToString("0.##") + "s")
                                      : "UNKNOWN (no lifetime and no range)") +
                        ", in " + inSeconds.ToString("0.##") + "s out " +
                        outSeconds.ToString("0.##") + "s. " +
                        (_sprites.Count == 0
                            ? "ZERO RENDERERS is the bug - there is nothing to fade."
                            : (_life <= 0f && outSeconds > 0f
                                ? "The fade-OUT cannot run without a life: give the weapon lifetimeData or rangeData, or set fade \"assumeLife\"."
                                : "Both ends should be visible.")) +
                        " Said once per run.");
                }

                // A fade-in starts from invisible, so paint frame zero here
                // rather than waiting for the first Update - otherwise every
                // shot flashes at full alpha for one frame.
                Paint();
            }
            catch (Exception e)
            {
                Log.LogError("Setting up a projectile fade failed: " + e);
                enabled = false;
            }
        }

        private void Collect()
        {
            _sprites.Clear();
            _alpha.Clear();

            SpriteRenderer[] all = everything
                ? GetComponentsInChildren<SpriteRenderer>(true)
                : GetComponents<SpriteRenderer>();

            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] == null)
                    continue;

                _sprites.Add(all[i]);

                // Remember each renderer's OWN alpha and scale that, never
                // assume 1. Some ammo art is deliberately translucent, and the
                // same lesson as the minion recolour: alpha is a decision
                // somebody already made.
                _alpha.Add(all[i].color.a);
            }
        }

        // How long has this shot got? Read AFTER Shoot has run, which Start is
        // guaranteed to be: FireSingle instantiates, assigns, then calls
        // Shoot(), and Unity runs Start at the end of that frame.
        private void Measure()
        {
            _shot = GetComponent<IProjectile>();

            if (_shot == null)
                _shot = GetComponentInParent<IProjectile>();

            _life = 0f;

            var plain = _shot as Projectile;

            if (plain != null)
            {
                if (plain.LifetimeData.enabled)
                    _life = plain.LifetimeData.time;

                // Range converts to a time through the speed it was fired at.
                // `slowDown` doubles it, exactly as Projectile.Shoot does -
                // mirrored rather than guessed, because a shot that decelerates
                // is in the air twice as long for the same distance.
                if (plain.RangeData.enabled && plain.StartSpeed > 0.01f)
                {
                    float byRange = plain.RangeData.range / plain.StartSpeed;

                    if (plain.RangeData.slowDown)
                        byRange *= 2f;

                    _life = (_life > 0f) ? Mathf.Min(_life, byRange) : byRange;
                }
            }
            else
            {
                var phys = _shot as PhysicsProjectile;

                if (phys != null && phys.LifetimeData.enabled)
                    _life = phys.LifetimeData.time;
            }

            if (_life <= 0f)
                _life = assumeLife;
        }

        private void Update()
        {
            if (!_ready)
                return;

            try
            {
                Paint();

                _ticks++;

                // Read the alpha BACK off the renderer rather than trusting what
                // Paint just assigned. That is the whole point: if another
                // component writes this colour after us, the value we asked for
                // and the value on the renderer disagree, and nothing else in
                // the log can tell you that.
                if (!_saidTick && _ticks >= 2 &&
                    Time.time - _born > 0.4f && _sprites.Count > 0 &&
                    _sprites[0] != null)
                {
                    _saidTick = true;

                    float readback = _sprites[0].color.a;
                    float wanted = _alpha[0] * _lastF;
                    bool agrees = Mathf.Abs(readback - wanted) < 0.02f;

                    Log.LogInfo(
                        "fade PER-FRAME on '" + gameObject.name + "': " +
                        _ticks + " tick(s) in " +
                        (Time.time - _born).ToString("0.##") + "s, fade factor " +
                        _lastF.ToString("0.###") + ", so alpha should be " +
                        wanted.ToString("0.###") + " and the renderer actually " +
                        "reads " + readback.ToString("0.###") + ". " +
                        (agrees
                            ? "THEY AGREE, so the fade IS being applied - if it " +
                              "still looks wrong the cause is visibility, not " +
                              "painting (tell me the shot's size and how long it " +
                              "is on screen)."
                            : "THEY DISAGREE, so something else writes this " +
                              "renderer's colour after the fade does - that is " +
                              "the bug and it is mine to find.") +
                        " Said once per run.");
                }
            }
            catch (Exception e)
            {
                Log.LogError("Fading a projectile failed: " + e);
                enabled = false;
            }
        }

        // WHAT THE SHOT LOOKED LIKE WHEN IT DIED, which is the number that
        // answers the question the per-frame line above cannot.
        //
        // R13 test 144. The per-frame instrument reported *"fade factor 0.144,
        // so alpha should be 0.144 and the renderer actually reads 0.144. THEY
        // AGREE"* - correct, complete, and no help at all: he still could not
        // see a fade. It proves the alpha is PAINTED and says nothing about
        // whether the shot lives long enough for the fade to finish.
        //
        // A White Popper shot dies on its first contact. With a 3-second
        // fade-in, a shot that hits a wall after half a second spends its whole
        // visible life at 17% alpha and then vanishes - a dim bullet, never a
        // fading one. **The fade was working and the shot was too short-lived
        // to show it.** No build-time check can know that, because it depends
        // on what the shot hits; a death-time reading does.
        //
        // > The previous instrument answered "is the feature running?". The
        // > question was "why does it not look like anything?". Those need
        // > different measurements, and the second one is the one he asked.
        private void OnDestroy()
        {
            if (!_ready || _saidDeath || _sprites.Count == 0)
                return;

            _saidDeath = true;

            try
            {
                float age = Time.time - _born;
                float travel = Vector2.Distance(_bornAt, transform.position);

                // A shot that got most of the way through its fade-in is not
                // the interesting case; one that died dim is.
                string verdict;

                if (inSeconds > 0f && _lastF < 0.6f && age < inSeconds)
                {
                    verdict =
                        " THIS IS THE ANSWER IF THE FADE LOOKS ABSENT: the " +
                        "shot DIED before its fade-in finished, so it was " +
                        "never brighter than " +
                        (_lastF * 100f).ToString("0") + "% and there was no " +
                        "fade to see - just a faint bullet. It needs to live " +
                        "about " + inSeconds.ToString("0.##") + "s to reach " +
                        "full brightness and it lived " +
                        age.ToString("0.##") + "s. Shorten fade \"in\" to " +
                        "about " + Mathf.Max(0.05f, age * 0.5f).ToString("0.##") +
                        "s, or use \"inUnits\" to say the distance instead, or " +
                        "stop the shot dying on contact " +
                        "(\"impactBehaviour\": { \"enabled\": false }).";
                }
                else
                {
                    verdict =
                        " That is a full fade, so what you see is what was " +
                        "asked for.";
                }

                Log.LogInfo(
                    "fade AT DEATH on '" + gameObject.name + "': lived " +
                    age.ToString("0.##") + "s, travelled " +
                    travel.ToString("0.#") + " units, final alpha factor " +
                    _lastF.ToString("0.###") + " (1 is fully bright)." +
                    verdict + " Said once per run.");
            }
            catch (Exception)
            {
                // A diagnostic must never be the thing that throws during
                // teardown - Unity is mid-destroy here and a renderer may
                // already be gone.
            }
        }

        private static bool _saidDeath;

        // Both instruments claim "said once per run" in their own text, so
        // they are reset at a run entry rather than living for the session.
        // They were session-scoped statics, which made the SECOND run of a
        // session silent - and the second run is where someone re-tests.
        public static void ResetDiagnostics()
        {
            _saidDeath = false;
            _saidTick = false;
            _said = false;
        }

        private void Paint()
        {
            float age = Time.time - _born;
            float f = 1f;

            if (inSeconds > 0f && age < inSeconds)
                f = age / inSeconds;

            // The fade-out needs to know when the end IS, so it only happens
            // when something bounded this shot.
            if (outSeconds > 0f && _life > 0f)
            {
                float left = _life - age;

                if (left < outSeconds)
                    f = Mathf.Min(f, Mathf.Max(0f, left / outSeconds));
            }

            f = Mathf.Clamp01(f);
            _lastF = f;

            for (int i = 0; i < _sprites.Count; i++)
            {
                SpriteRenderer sr = _sprites[i];

                if (sr == null)
                    continue;

                Color c = sr.color;
                sr.color = new Color(c.r, c.g, c.b, _alpha[i] * f);
            }
        }
    }
}
