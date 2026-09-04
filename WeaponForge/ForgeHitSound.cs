using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace WeaponForge
{
    // THE HIT "TICK" - the little sound something makes when you damage it.
    //
    // WHAT IT ACTUALLY IS, because almost none of this was guessable:
    //
    //   * ONE SOUND, SHARED BY 83 THINGS. Every damageable prefab wires
    //     `HealthBase.onDamage` to an `SfxPlayer` as a UnityEvent IN THE
    //     PREFAB - so it appears at no PlaySfx call site and cannot be found
    //     by reading code - and every single one of them points at the same
    //     guid: `Enemies/Hit`, volume 0.22, 3D, a single clip with no
    //     variation. Not just enemies: crates, boxes, fruit and the Dummy too.
    //
    //   * THE PLAYER IS NOT ONE OF THEM. All four ship prefabs carry no
    //     SfxPlayer at all and wire `onDamage` to `Ship.OnDamage` instead. So
    //     this sound is unambiguously a HITMARKER - changing or silencing it
    //     can never affect the sound of you being hurt.
    //
    //   * THERE IS NO THROTTLE ON IT. `onDamage` only fires when damage really
    //     lands, which sounds like a gate until you check `iFrameDuration` -
    //     it is **0 on all 84 damageable prefabs**. The only limiter is the
    //     Sfx's own `repeatMinDelay: 0.01`, and that is a GLOBAL 100-per-second
    //     cap rather than a per-victim one. So a shotgun plays one tick per
    //     pellet that connects, and a piercing beam plays one per enemy per
    //     damage tick - six enemies at the Caps Laser's rate is ~75 a second.
    //
    // DEFAULTS ARE THE GAME'S OWN BEHAVIOUR, at his explicit request: "leave
    // the in game sound as is as default". So an absent `hitSound` block, or
    // one that only sets `scope`, changes nothing at all. **The buzz above is
    // therefore still present out of the box** - `minInterval` is the key that
    // fixes it, and the builder page and how-to both say so rather than
    // quietly turning it on.
    public static class ForgeHitSound
    {
        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("WeaponForge.HitSound");

        // `Enemies/Hit`. Verified identical across all 83 prefabs that have it.
        public const string HitGuid = "ce93fadd-7bb2-4493-801d-7184299255a2";

        // ---- scope -------------------------------------------------------
        public const int ScopeAll = 0;      // default: everything that takes damage
        public const int ScopeMine = 1;     // only hits caused by this weapon
        public const int ScopeUnits = 2;    // everything except crates and boxes

        public static int ParseScope(string s)
        {
            switch ((s ?? "").Trim().ToLowerInvariant())
            {
                case "mine":
                case "weapon":
                case "own":
                    return ScopeMine;
                case "units":
                case "enemies":
                case "nocrates":
                    return ScopeUnits;
                default:
                    return ScopeAll;
            }
        }

        public static string ScopeWord(int s)
        {
            switch (s)
            {
                case ScopeMine: return "only this weapon's own hits";
                case ScopeUnits: return "every unit, but not crates or boxes";
                default: return "everything that takes damage";
            }
        }

        // ---- pitch -------------------------------------------------------
        public const int PitchNone = 0;     // default
        public const int PitchDamage = 1;
        public const int PitchHealth = 2;

        public static int ParsePitch(string s)
        {
            switch ((s ?? "").Trim().ToLowerInvariant())
            {
                case "damage":
                case "hit":
                case "weight":
                    return PitchDamage;
                case "health":
                case "hp":
                case "kill":
                case "remaining":
                    return PitchHealth;
                default:
                    return PitchNone;
            }
        }

        public static string PitchWord(int p)
        {
            switch (p)
            {
                case PitchDamage: return "by damage dealt";
                case PitchHealth: return "by how close to death they are";
                default: return "off";
            }
        }

        public class Config
        {
            public bool enabled = true;

            public int scope = ScopeAll;

            // "" keeps the game's own sound. A name or guid replaces it.
            public string sfx = "";
            public bool silence;

            // Multiplier on the Sfx's own 0.22. 1 = untouched.
            public float volume = 1f;

            // Seconds between ticks. 0 is the game's behaviour, which is no
            // throttle at all.
            public float minInterval;
            public bool perTarget;

            public int pitchMode = PitchNone;
            public float pitchLow = 0.85f;
            public float pitchHigh = 1.25f;

            // The damage that reaches the far end of the pitch range.
            public float damageAt = 10f;

            // Random jitter either side, applied on top of any mode. This
            // alone fixes most of the grating, because the game ships ONE
            // clip and identical repeats are what the ear objects to.
            public float vary;

            public bool DoesAnything
            {
                get
                {
                    return silence ||
                           !string.IsNullOrEmpty(sfx) ||
                           minInterval > 0f ||
                           pitchMode != PitchNone ||
                           vary > 0f ||
                           Mathf.Abs(volume - 1f) > 0.001f;
                }
            }
        }

        private static readonly Dictionary<WeaponData, Config> _weapons =
            new Dictionary<WeaponData, Config>();

        // The config that applies to hits NOT caused by a registered weapon -
        // a burn tick, a gas cloud, one enemy shooting another, or a crate.
        // Only a non-"mine" config can claim this.
        private static Config _ambient;
        private static WeaponData _ambientOwner;

        public static void Register(
            WeaponData weapon, Config config, string fileName)
        {
            if (weapon == null || config == null)
                return;

            _weapons[weapon] = config;

            if (config.scope == ScopeMine)
                return;

            if (_ambient != null && _ambientOwner != weapon)
            {
                Log.LogWarning(
                    fileName + ": another weapon already set a game-wide " +
                    "\"hitSound\" (" +
                    (_ambientOwner != null ? _ambientOwner.name : "?") +
                    "), and only one can win - this one takes over. Give one " +
                    "of them \"scope\": \"mine\" so it only changes its own " +
                    "hits, or make them agree.");
            }

            _ambient = config;
            _ambientOwner = weapon;
        }

        public static bool Any { get { return _weapons.Count > 0; } }

        // Does any registered config need to reach the AudioSource itself?
        // Gates the AudioManager patch, which otherwise touches every sound in
        // the game - so a weapon that only silences or throttles never puts
        // that patch on the hot path at all.
        private static bool _touches;
        private static bool _touchesKnown;

        public static bool TouchesVoice
        {
            get
            {
                if (!_touchesKnown)
                {
                    _touchesKnown = true;
                    _touches = false;

                    foreach (var kv in _weapons)
                    {
                        Config c = kv.Value;

                        if (c != null && c.enabled &&
                            (c.pitchMode != PitchNone || c.vary > 0f ||
                             Mathf.Abs(c.volume - 1f) > 0.001f))
                        {
                            _touches = true;
                            break;
                        }
                    }
                }

                return _touches;
            }
        }

        public static void Invalidate() { _touchesKnown = false; }

        // ------------------------------------------------------------------
        // Which config governs the hit being resolved right now.
        //
        // ForgeKillsPatch.Source is exactly the seam for this and needs no new
        // patch: it is set in a PREFIX on each of the three damage routes and
        // cleared in a FINALIZER, and `onDamage.Invoke()` happens inside the
        // original method body - so it is live at the moment the tick plays,
        // and it is null for a burn tick, a gas cloud or a cell collision.
        // ------------------------------------------------------------------
        public static Config For(HealthBase victim)
        {
            WeaponBase source = ForgeKillsPatch.Source;

            // The weapon that caused this hit wins, whatever its scope - a
            // weapon's own settings always govern its own hits. That is what
            // makes "mine" mean "ONLY mine" rather than "mine as well".
            if (source != null && source.TemplateData != null)
            {
                Config own;

                if (_weapons.TryGetValue(source.TemplateData, out own) &&
                    own != null && own.enabled)
                {
                    // "units" still means "not crates", even for the weapon's
                    // own hits. Shooting a supply box is not a fight.
                    if (own.scope == ScopeUnits && !IsFighter(victim))
                        return null;

                    return own;
                }
            }

            // Nothing of ours caused it - a burn tick, a gas cloud, a cell
            // collision, or one enemy shooting another. Only a weapon that
            // asked for a game-wide scope gets a say here; Register never
            // records a "mine" config as the ambient one.
            if (_ambient == null || !_ambient.enabled)
                return null;

            if (_ambient.scope == ScopeUnits && !IsFighter(victim))
                return null;

            return _ambient;
        }

        // The discriminator between a fighting unit and a container, and it is
        // the game's own: all 60 combatants carry an AIAgent and none of the
        // 13 crates and boxes do. Turrets are the edge case worth knowing -
        // they have one, so they count as fighters.
        private static bool IsFighter(HealthBase victim)
        {
            return victim != null &&
                   victim.GetComponentInParent<AIAgent>() != null;
        }

        // ------------------------------------------------------------------
        // Throttling
        // ------------------------------------------------------------------
        private static float _lastGlobal = -999f;

        private static readonly Dictionary<HealthBase, float> _lastPerTarget =
            new Dictionary<HealthBase, float>();

        public static bool Ready(Config cfg, HealthBase victim)
        {
            if (cfg.minInterval <= 0f)
                return true;

            if (!cfg.perTarget)
            {
                if (Time.unscaledTime - _lastGlobal < cfg.minInterval)
                    return false;

                _lastGlobal = Time.unscaledTime;
                return true;
            }

            if (victim == null)
                return true;

            float last;

            if (_lastPerTarget.TryGetValue(victim, out last) &&
                Time.unscaledTime - last < cfg.minInterval)
            {
                return false;
            }

            if (_lastPerTarget.Count > 128)
                PruneTargets();

            _lastPerTarget[victim] = Time.unscaledTime;
            return true;
        }

        private static void PruneTargets()
        {
            var dead = new List<HealthBase>();

            foreach (var kv in _lastPerTarget)
            {
                if (kv.Key == null ||
                    Time.unscaledTime - kv.Value > 5f)
                {
                    dead.Add(kv.Key);
                }
            }

            for (int i = 0; i < dead.Count; i++)
                _lastPerTarget.Remove(dead[i]);

            if (_lastPerTarget.Count > 128)
                _lastPerTarget.Clear();
        }

        // ------------------------------------------------------------------
        // Pitch
        // ------------------------------------------------------------------
        public static float PitchFor(
            Config cfg, HealthBase victim, float damage)
        {
            float p = 1f;

            if (cfg.pitchMode == PitchDamage)
            {
                // A HEAVY hit sounds LOWER. That is the way round every game
                // that does this picks, and it matches the physical intuition
                // that a big impact is a bass thump.
                float t = Mathf.Clamp01(
                    damage / Mathf.Max(0.01f, cfg.damageAt));

                p = Mathf.Lerp(cfg.pitchHigh, cfg.pitchLow, t);
            }
            else if (cfg.pitchMode == PitchHealth)
            {
                float frac = 1f;

                if (victim != null && victim.MaxHealth > 0f)
                    frac = Mathf.Clamp01(victim.CurrentHealth / victim.MaxHealth);

                // Rises as they get closer to death, so a nearly-dead enemy is
                // audible without looking at a health bar.
                p = Mathf.Lerp(cfg.pitchHigh, cfg.pitchLow, frac);
            }

            if (cfg.vary > 0f)
                p += UnityEngine.Random.Range(-cfg.vary, cfg.vary);

            return Mathf.Clamp(p, 0.25f, 3f);
        }

        // ------------------------------------------------------------------
        // Applying a pitch to the voice we just started.
        //
        // THE GAME NEVER SETS `audioSource.pitch` ANYWHERE, and its sources
        // come from a POOL. So the moment we set one, that source keeps it -
        // and the next unrelated sound to borrow it inherits our pitch. That
        // is why the AudioManager postfix writes a pitch on EVERY play once
        // this feature is in use: ours where we asked for one, and an explicit
        // 1 everywhere else. Without that reset the first pitched hit would
        // quietly detune a random explosion later.
        // ------------------------------------------------------------------
        private static FieldInfo _fPlaying;
        private static FieldInfo _fSource;
        private static bool _reflectionTried;

        // Volume needs no such guard, note: PlaySfx assigns
        // `audioSource.volume = sfx.volume` on every single play, so a volume
        // we write cannot survive into the next sound the way a pitch can.
        public static void SetVoice(
            object manager, int handle, float pitch, float volumeMul)
        {
            if (handle < 0 || manager == null)
                return;

            try
            {
                if (!_reflectionTried)
                {
                    _reflectionTried = true;

                    _fPlaying = AccessTools.Field(
                        typeof(AudioManager), "playingAudios");

                    Type playing = AccessTools.Inner(
                        typeof(AudioManager), "PlayingAudio");

                    if (playing != null)
                        _fSource = AccessTools.Field(playing, "audioSource");
                }

                if (_fPlaying == null || _fSource == null)
                    return;

                var map = _fPlaying.GetValue(manager) as
                    System.Collections.IDictionary;

                if (map == null || !map.Contains(handle))
                    return;

                var src = _fSource.GetValue(map[handle]) as AudioSource;

                if (src != null)
                {
                    src.pitch = pitch;

                    if (Mathf.Abs(volumeMul - 1f) > 0.001f)
                        src.volume = src.volume * volumeMul;
                }
            }
            catch (Exception e)
            {
                Log.LogWarning(
                    "Could not set the hit sound's pitch (" + e.Message +
                    ") - it will play at normal pitch. Everything else about " +
                    "the hit sound still works.");

                _fPlaying = null;
                _fSource = null;
            }
        }

        // What the next PlaySfx call should use, handed over rather than
        // recomputed. Consumed once, so it can never apply to a later sound
        // even if something goes wrong between here and there.
        private static float _pendingPitch = 1f;
        private static float _pendingVolume = 1f;
        private static bool _hasPending;

        public static void Expect(float pitch, float volumeMul)
        {
            _pendingPitch = pitch;
            _pendingVolume = volumeMul;
            _hasPending = true;
        }

        public static bool Take(out float pitch, out float volumeMul)
        {
            pitch = _pendingPitch;
            volumeMul = _pendingVolume;

            bool had = _hasPending;

            _hasPending = false;
            _pendingPitch = 1f;
            _pendingVolume = 1f;

            return had;
        }
    }
}
