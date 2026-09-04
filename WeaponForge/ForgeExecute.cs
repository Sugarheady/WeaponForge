using System;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;

namespace WeaponForge
{
    // EXECUTE - finish anything already below a health threshold.
    //
    // The second of the behaviours off his 2026-08-27 list. Opt-in and scoped to
    // one weapon, the same shape as `targeting`: the registry is keyed on
    // `WeaponData`, so a weapon without an `execute` block can only ever resolve
    // to null and nothing about the damage path changes for it.
    //
    // WHY IT HOOKS `DamagableResource.Damage(float)`. That is the single
    // chokepoint every damage route funnels into - a bullet, a beam and an
    // explosion all arrive there - so one patch covers all three rather than
    // three patches agreeing with each other. Crit and leech already live there
    // for the same reason.
    //
    // THE KILL CREDITS ITSELF, AND THAT IS THE WHOLE REASON THIS IS CHEAP.
    // All three routes have the same shape:
    //
    //     base.ProjectileCollided(...)          // -> ... -> Damage(float)
    //     ...
    //     if (this.IsDead && projectile.Owner != null)
    //         projectile.Owner.RegisterKill(this.Unit);
    //
    // The `IsDead` test runs AFTER the damage call returns. So a postfix on
    // `Damage` that calls `Die()` is seen by the game's own credit line, and the
    // execute pays out on-kill rewards, Module Forge's KillRewardEffect, leech
    // and the music controller with no code from us. Killing the unit any other
    // way - `HealthBase.TakeDamage` behind the game's back - would credit
    // nobody, which is the mistake `ForgeKillsPatch.Credit` exists to undo for
    // the gas cloud and the ram.
    public static class ForgeExecute
    {
        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("WeaponForge.Execute");

        public class Config
        {
            // Below this FRACTION of the tank's capacity, finish it. 0 disables
            // the percentage test and leaves only `flat`.
            public float percent = 0.15f;

            // Below this ABSOLUTE value, finish it. 0 = unused. Whichever of the
            // two is satisfied first wins, so a weapon can say "under 15% OR
            // under 20 health" and catch both the chaff and the big one.
            public float flat;

            // Skip anything whose tank capacity is ABOVE this - the boss guard.
            // 0 = no limit. Without it a 15% execute on a 2000-health boss is
            // 300 free damage, which is a different weapon than the one you
            // thought you were making.
            public float maxTargetHealth;

            // ---- cues, because the game announces nothing ------------------
            public string sfx = "";
            [NonSerialized] public string sfxGuid = "";

            public bool hasColor;
            public Color color = new Color(1f, 0.3f, 0.3f, 1f);
            public float colorSeconds = 0.25f;
            // Pulse rate in Hz, matching crit. 0 = a steady flash.
            public float colorPulse;

            // HOW HARD THE TINT COMES THROUGH, 0..1. 1 is the full colour and
            // is what this has always done; 0.35 is a hint of it. Read from
            // either "tintStrength" or "colorStrength" so the two naming
            // styles in this mod's blocks both work. See ForgeUnitTint.
            public float tintStrength = 1f;
        }

        private static readonly Dictionary<WeaponData, Config> _weapons =
            new Dictionary<WeaponData, Config>();

        public static bool Any { get; private set; }

        public static void Register(WeaponData weapon, Config config)
        {
            if (weapon == null || config == null)
                return;

            _weapons[weapon] = config;
            Any = true;
        }

        public static bool TryGet(WeaponData weapon, out Config config)
        {
            config = null;

            return weapon != null && _weapons.TryGetValue(weapon, out config) &&
                   config != null;
        }

        // Does this victim qualify? Split out so the patch reads as a list of
        // reasons NOT to fire, which is the shape that keeps a guard from being
        // quietly dropped later.
        public static bool ShouldFinish(
            Config cfg, float current, float max)
        {
            if (cfg == null || max <= 0f)
                return false;

            // A capacity above the cap means "too big to execute".
            if (cfg.maxTargetHealth > 0f && max > cfg.maxTargetHealth)
                return false;

            if (cfg.flat > 0f && current <= cfg.flat)
                return true;

            return cfg.percent > 0f && current <= cfg.percent * max;
        }

        public static void Feedback(Config cfg, Component victim)
        {
            if (cfg == null || victim == null)
                return;

            try
            {
                if (!string.IsNullOrEmpty(cfg.sfxGuid))
                    AudioManager.PlaySfx(cfg.sfxGuid, victim.transform.position);

                if (cfg.hasColor)
                {
                    Unit hurt = victim.GetComponentInParent<Unit>();

                    if (hurt != null)
                    {
                        // Its own layer key, so an execute flash and a crit
                        // flash on the same frame compose instead of one
                        // restoring over the other.
                        ForgeUnitTint.Set(
                            hurt, "execute", cfg.color,
                            Mathf.Max(0.05f, cfg.colorSeconds),
                            cfg.colorPulse, cfg.tintStrength);
                    }
                }
            }
            catch (Exception e)
            {
                // Decoration must never cost the kill that earned it.
                Log.LogWarning("execute feedback failed: " + e.Message);
            }
        }

        public static void Describe(Config cfg, string fileName)
        {
            if (cfg == null)
                return;

            string when;

            if (cfg.flat > 0f && cfg.percent > 0f)
            {
                when = "under " + (cfg.percent * 100f).ToString("0.#") +
                       "% of its health OR under " + cfg.flat.ToString("0.##") +
                       " left";
            }
            else if (cfg.flat > 0f)
            {
                when = "under " + cfg.flat.ToString("0.##") + " health left";
            }
            else
            {
                when = "under " + (cfg.percent * 100f).ToString("0.#") +
                       "% of its health";
            }

            Log.LogInfo(
                fileName + ": execute - a hit from THIS weapon finishes anything " +
                when + "." +
                (cfg.maxTargetHealth > 0f
                    ? " Skips anything with more than " +
                      cfg.maxTargetHealth.ToString("0.##") +
                      " maximum health, so bosses are safe."
                    : " No size limit, so it applies to bosses too - set " +
                      "\"maxTargetHealth\" if that is not what you want.") +
                " The kill is credited normally, so on-kill rewards, leech and " +
                "crit-style effects all still pay out.");
        }

        public static void Reset()
        {
            // Registrations are per-file and made once at startup; only per-run
            // state would go here. Kept for symmetry with crit and leech so the
            // reset list does not develop a hole.
        }
    }
}
