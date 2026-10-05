using System;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;

namespace WeaponForge
{
    // STUN - it cannot move and it cannot shoot.
    //
    // Third of the four he approved. *"YES - Seems easy."* It was, but only
    // because of work done the same day: the movement half is the machinery
    // built for `hold`, and without that this would have been the hard one.
    //
    // ---- WHY THIS IS NOT `slow` WITH amount 0 -----------------------------
    //
    // The first thing to check, and the answer is that `slow` **cannot** reach
    // zero and would be the wrong mechanism even if it could:
    //
    //   `ForgeSlowed.Factor()` ends with
    //   `Mathf.Max(Mathf.Clamp(amount, 0.01f, 1f), Mathf.Clamp(floor, 0.01f, 1f))`
    //
    // so the strongest slow available is 1% speed, by construction. And its
    // three levers are all the wrong shape for a freeze:
    //
    //  * movement is `linearDamping = baseline / factor`, which fights a force
    //    rather than removing it - so a hard-charging lunger still lurches, and
    //    at factor 0 it would mean writing an infinite damping value;
    //  * `slowKnockback` makes the victim LIGHTER, so a "frozen" unit would be
    //    flung further by your shots than an unfrozen one;
    //  * `slowFire` sets `FireRate` to 0, which does stop shots
    //    (`FireRatePassed` divides by it) but leaves the gun's looping sound
    //    running, because that is played earlier in `Shooter.Update`.
    //
    // A stun instead uses the game's own switches, and they are cleaner than
    // anything a multiplier can do.
    //
    // ---- THE GUN: `Shooter.Block` IS EXACTLY THIS FEATURE -----------------
    //
    //     private void Update()
    //     {
    //         this.PlayReloadParticleIfNeeded();
    //         if (this.weapon == null || this.blockers.Count > 0) return;
    //         this.weapon.OnBarrelMoved(...);
    //         ... IsTriggerPulled, Warmup, warmup sound, continuous sound, Shoot()
    //     }
    //
    // `blockers` is a `HashSet<object>` and `Block(object)` / `Unblock(object)`
    // are public, so a stun is one call with this component as the token - and
    // because several blockers coexist, two stuns and the overheat jam cannot
    // clobber each other's release.
    //
    // **BUT THE EARLY RETURN SKIPS THE SOUND CLEANUP TOO.** `StopContinousSound`
    // is only reached further down that same method, so blocking a unit that is
    // mid-burst leaves its loop playing for the whole stun. Both stop calls are
    // public and are made explicitly here. That is the detail that separates
    // "the gun stops" from "the gun stops and goes quiet".
    //
    // ---- THE LEGS: SUSPEND THE MOVEMENT ACTION ----------------------------
    //
    // See ForgeSuspend. `MovementAction.Update` is the only thing that steers a
    // unit, and `ChargerHead` does not propel - so suspending it stops a
    // Larva's charge as well as a walker's wander. Re-suspended every frame,
    // because a state change activates a different state's action.
    public static class ForgeStun
    {
        private static readonly ManualLogSource Log =
            ForgeLog.Source("WeaponForge.Stun");

        public class Config
        {
            public float duration = 1.2f;

            // Probability per qualifying hit. 1 = always.
            public float chance = 1f;

            // Minimum gap between stuns ON THE SAME VICTIM, measured from when
            // the last one STARTED. 0 = none, which is the default because
            // `duration` should mean what it says - but a weapon that can
            // permanently lock a target is warned about at build time with the
            // arithmetic, rather than being quietly overridden.
            public float cooldown;

            // Refreshing an existing stun extends it. Off means the first stun
            // runs its course and further hits are ignored until it ends,
            // which is the honest way to say "no lock-down".
            public bool refresh = true;

            // ---- what a stun actually switches off ---------------------
            public bool stopGun = true;         // Shooter.Block
            public bool stopMovement = true;    // suspend the AI's steering

            // Zero its velocity so it stops DEAD rather than coasting on for
            // another second. Also what stops a ram: ChargerHead sweeps along
            // `rigidbody.linearVelocity`, so a still unit does no ram damage.
            public bool stopMotion = true;

            // Skip anything whose health capacity is above this - the boss
            // guard, same idea and same default as `execute`. 0 = no limit.
            public float maxTargetHealth;

            // ---- cues, because the game announces nothing --------------
            public string sfx = "";
            [NonSerialized] public string sfxGuid = "";

            public bool hasTint = true;
            public Color tint = new Color(0.55f, 0.8f, 1f, 1f);
            public float tintPulse;

            // HOW HARD THE TINT COMES THROUGH, 0..1. 1 is the full colour and
            // is what this has always done; 0.35 is a hint of it. Read from
            // either "tintStrength" or "colorStrength" so the two naming
            // styles in this mod's blocks both work. See ForgeUnitTint.
            public float tintStrength = 1f;

            // multiply (the default, and what every existing file was tuned
            // against) or replace, which moves the sprite TOWARD the colour so
            // it can brighten a dark enemy. See ForgeUnitTint.
            public int tintMode = ForgeUnitTint.Multiply;
        }

        private static readonly Dictionary<WeaponData, Config> _weapons =
            new Dictionary<WeaponData, Config>();

        public static bool Any { get { return _weapons.Count > 0; } }

        public static void Register(WeaponData weapon, Config config)
        {
            if (weapon == null || config == null)
                return;

            _weapons[weapon] = config;
        }

        public static bool TryGet(WeaponData weapon, out Config config)
        {
            config = null;

            return weapon != null && _weapons.TryGetValue(weapon, out config) &&
                   config != null;
        }

        public static void Apply(Unit victim, Config cfg)
        {
            if (victim == null || cfg == null)
                return;

            try
            {
                var live = victim.gameObject.GetComponent<ForgeStunned>();

                if (live != null)
                {
                    live.Again(cfg);
                    return;
                }

                victim.gameObject.AddComponent<ForgeStunned>().Begin(victim, cfg);
            }
            catch (Exception e)
            {
                Log.LogError("Applying a stun failed: " + e);
            }
        }

        public static void Describe(Config cfg, string fileName, float fireRate)
        {
            if (cfg == null)
                return;

            var parts = new List<string>();

            if (cfg.stopGun)
                parts.Add("its gun is blocked (and silenced)");

            if (cfg.stopMovement)
                parts.Add("its own steering is suspended");

            if (cfg.stopMotion)
                parts.Add("it is stopped dead rather than left coasting");

            Log.LogInfo(
                fileName + ": stun - a hit freezes what it hits for " +
                cfg.duration.ToString("0.##") + "s" +
                (cfg.chance < 1f
                    ? ", " + (cfg.chance * 100f).ToString("0.#") +
                      "% of the time"
                    : "") +
                ". " +
                (parts.Count > 0
                    ? char.ToUpper(parts[0][0]) + parts[0].Substring(1) +
                      (parts.Count > 1
                          ? ", " + string.Join(", ",
                                parts.GetRange(1, parts.Count - 1).ToArray())
                          : "") + "."
                    : "NOTHING is switched off - stopGun, stopMovement and " +
                      "stopMotion are all false, so this stun does nothing.") +
                (cfg.maxTargetHealth > 0f
                    ? " Skips anything with more than " +
                      cfg.maxTargetHealth.ToString("0.##") +
                      " maximum health, so bosses are immune."
                    : " No size limit, so it applies to bosses too - set " +
                      "\"maxTargetHealth\" if that is not what you want."));

            // THE LOCK-DOWN SUM. A stun is the one debuff where "it works" and
            // "it is unplayable" are the same setting at different numbers, so
            // the arithmetic is done here rather than discovered in a fight.
            if (fireRate > 0f && cfg.cooldown <= 0f && cfg.refresh)
            {
                float gap = 1f / fireRate;
                float effective = gap / Mathf.Max(0.01f, cfg.chance);

                if (effective < cfg.duration)
                {
                    Log.LogWarning(
                        fileName + ": stun - at fireRate " +
                        fireRate.ToString("0.##") + " you land a stun about " +
                        "every " + effective.ToString("0.##") +
                        "s, and each one lasts " +
                        cfg.duration.ToString("0.##") +
                        "s - so anything you keep shooting is frozen " +
                        "PERMANENTLY. That may be exactly what you want; if " +
                        "not, add a \"cooldown\" (a minimum gap per victim), " +
                        "lower \"chance\", or set \"refresh\": false so the " +
                        "first stun has to run out before another can land.");
                }
            }

            if (cfg.cooldown > 0f)
            {
                Log.LogInfo(
                    fileName + ": stun \"cooldown\": " +
                    cfg.cooldown.ToString("0.##") + "s per victim, measured " +
                    "from when a stun STARTS - so a target can be frozen at " +
                    "most " +
                    Mathf.Clamp01(cfg.duration / cfg.cooldown).ToString("0%") +
                    " of the time.");
            }
        }

        public static void Reset()
        {
            // Registrations are per-file and made once at startup; live stuns
            // are components on units that no longer exist by the time this
            // runs. Kept for symmetry with the other reset hooks so the list
            // does not develop a hole.
        }
    }

    // The live stun, one per victim. Mirrors ForgeSlowed's shape deliberately:
    // same lifecycle, same restore-on-destroy discipline.
    public class ForgeStunned : MonoBehaviour
    {
        private static readonly ManualLogSource Log =
            ForgeLog.Source("WeaponForge.Stun");

        private ForgeStun.Config _cfg;
        private float _endsAt;
        private float _startedAt = -999f;
        private bool _applied;

        // THE COOLDOWN HAS TO OUTLIVE THE FREEZE, and the first version of this
        // did not - which made the build log tell a quantitative lie.
        //
        // It destroyed the component the moment the stun expired, so the next
        // hit found no component, `OnCooldown` returned false, and a fresh stun
        // landed immediately. The cooldown therefore only ever applied WHILE a
        // stun was already running - which is the one case `refresh` already
        // covers - and "a target can be frozen at most duration/cooldown of the
        // time" was simply not true. A simulation caught it; no log line ever
        // would have, because every value was written correctly.
        //
        // So the component now has two phases: frozen until `_endsAt`, then a
        // dormant "recently stunned" marker until this, and only then destroyed.
        // With no cooldown the two coincide and it dies immediately as before.
        private float _cooldownUntil = -999f;

        private Rigidbody2D _rb;
        private Shooter[] _shooters;
        private MovementAction[] _actions;

        private readonly List<MovementAction> _suspended =
            new List<MovementAction>();

        public void Begin(Unit victim, ForgeStun.Config cfg)
        {
            _cfg = cfg;
            _endsAt = Time.time + cfg.duration;
            _startedAt = Time.time;
            _cooldownUntil = Time.time + Mathf.Max(0f, cfg.cooldown);

            _rb = victim.GetComponent<Rigidbody2D>();

            if (_rb == null)
                _rb = victim.GetComponentInChildren<Rigidbody2D>(true);

            _shooters = victim.GetComponentsInChildren<Shooter>(true);
            _actions = ForgeSuspend.ActionsOf(victim);

            if (cfg.hasTint)
            {
                // Its own layer key, so a stun tint and a crit or execute
                // flash on the same frame compose instead of one restoring
                // over the other. Duration matches the stun so the colour is
                // the readout.
                ForgeUnitTint.Set(
                    victim, "stun", cfg.tint,
                    Mathf.Max(0.05f, cfg.duration), cfg.tintPulse,
                    cfg.tintStrength, cfg.tintMode);
            }

            if (!string.IsNullOrEmpty(cfg.sfxGuid))
                AudioManager.PlaySfx(cfg.sfxGuid, transform.position);

            Freeze();
            _applied = true;
        }

        public void Again(ForgeStun.Config cfg)
        {
            // A different weapon's stun taking over is fine; its settings
            // simply become the live ones.
            _cfg = cfg;

            if (Time.time < _cooldownUntil)
                return;

            // `refresh: false` means the current freeze runs its course. Once
            // it has ended, a later hit is a NEW stun and is allowed.
            if (_applied && !cfg.refresh)
                return;

            _endsAt = Time.time + cfg.duration;
            _startedAt = Time.time;
            _cooldownUntil = Time.time + Mathf.Max(0f, cfg.cooldown);

            // RE-FREEZE if the last one had already worn off. The first version
            // only moved the timers, so a re-stun landing on a dormant marker
            // set a fresh expiry and blocked nothing at all - the unit read as
            // stunned in the log and acted normally in the game.
            if (!_applied)
            {
                Freeze();
                _applied = true;
            }

            if (cfg.hasTint)
            {
                Unit u = GetComponent<Unit>();

                if (u != null)
                {
                    ForgeUnitTint.Set(
                        u, "stun", cfg.tint,
                        Mathf.Max(0.05f, cfg.duration), cfg.tintPulse,
                        cfg.tintStrength, cfg.tintMode);
                }
            }
        }

        // Was this victim stunned recently enough that a fresh stun should be
        // refused? Asked by the patch BEFORE the dice are rolled, so a refused
        // hit does not consume a `chance` roll. Static and tolerant of a
        // missing component, which is the normal case.
        public static bool OnCooldown(Unit victim, ForgeStun.Config cfg)
        {
            if (victim == null || cfg == null || cfg.cooldown <= 0f)
                return false;

            var live = victim.gameObject.GetComponent<ForgeStunned>();

            return live != null && Time.time < live._cooldownUntil;
        }

        private void Freeze()
        {
            if (_cfg.stopGun && _shooters != null)
            {
                for (int i = 0; i < _shooters.Length; i++)
                {
                    Shooter s = _shooters[i];

                    if (s == null)
                        continue;

                    s.Block(this);

                    // THE EARLY RETURN IN Shooter.Update SKIPS ITS OWN SOUND
                    // CLEANUP, so a unit blocked mid-burst would keep its loop
                    // running for the whole stun.
                    //
                    // This used to do the sound and the trigger flag here, with
                    // a comment predicting the beam case ("left true, a beam's
                    // renderer can keep drawing its last frame") - and clearing
                    // the flag does not fix that, because the only thing that
                    // reads it is `OnBarrelMoved`, which is ALSO above the
                    // return. A stunned enemy beam froze on exactly the way
                    // overheat's did in R15 test 188. One owner for all of it
                    // now, so the two cannot drift apart again.
                    ForgeShooterQuiet.Silence(s);
                }
            }
        }

        private void FixedUpdate()
        {
            if (!_applied)
                return;

            try
            {
                if (_cfg.stopMotion && _rb != null)
                {
                    // Every frame, not once: its own movement action is
                    // suspended but a shove from anything else still moves it,
                    // and a stunned unit sliding across the room does not read
                    // as stunned.
                    _rb.linearVelocity = Vector2.zero;
                    _rb.angularVelocity = 0f;
                }

                if (_cfg.stopMovement)
                {
                    // RE-SUSPEND EVERY FRAME. A state transition activates a
                    // different state's action, and that one is enabled -
                    // suspending once would freeze the unit only until its
                    // next transition.
                    ForgeSuspend.Suspend(_actions, _suspended);
                }
            }
            catch (Exception e)
            {
                Log.LogError("Stun upkeep failed, releasing: " + e);
                Restore();
                Destroy(this);
            }
        }

        // TWO PHASES. The freeze is released at `_endsAt`; the component then
        // lingers as a "recently stunned" marker until `_cooldownUntil` so the
        // cooldown can outlive it, and only then goes away. With no cooldown
        // the two instants coincide and this behaves as a single phase.
        private void Update()
        {
            if (_applied && Time.time >= _endsAt)
                Restore();

            if (!_applied && Time.time >= _cooldownUntil)
                Destroy(this);
        }

        // A unit left blocked, or with its steering off and nothing running to
        // switch it back on, is permanently disabled - a worse outcome than the
        // feature not existing. So this runs on expiry, on destroy, and on the
        // error path above.
        private void OnDestroy()
        {
            Restore();
        }

        private void Restore()
        {
            if (!_applied)
                return;

            _applied = false;

            try
            {
                ForgeSuspend.Release(_suspended);

                if (_shooters != null)
                {
                    for (int i = 0; i < _shooters.Length; i++)
                    {
                        if (_shooters[i] != null)
                            _shooters[i].Unblock(this);
                    }
                }

                // The tint expires on its own timer, which was set to the
                // stun's duration - and ForgeUnitTint owns the "never restore
                // somebody else's colour" rule, so there is nothing to undo
                // here by hand.
            }
            catch (Exception e)
            {
                Log.LogError("Restoring after a stun failed: " + e);
            }
        }
    }
}
