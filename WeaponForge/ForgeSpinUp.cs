using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace WeaponForge
{
    // SUSTAINED FIRE REWARDS YOU - the mirror of `overheat`.
    //
    // The author's own framing: *"So instead of it overheating/stopping firing,
    // it fires quicker and is more accurate? YES."* So the meter fills while you
    // lean on the trigger and the gun gets better, rather than worse.
    //
    // WHAT MAKES THIS CHEAP. `WeaponBase` exposes every knob involved as a
    // public get/set - `FireRate`, `AngleVariance`, `Spread`, `Damage`,
    // `ProjectileCount`, `Cost` - and the two that matter are re-read at exactly
    // the right moment:
    //
    //   - `Shooter.FireRatePassed` is `Time.time > lastShootTime + 1f/FireRate`,
    //     computed FRESH every frame. So raising the rate mid-hold shortens the
    //     current gap rather than the next one; there is nothing to re-arm.
    //   - `AngleVariance` and `Spread` are read inside `GetDirections`, which is
    //     walked by `DoShoot` for the damage AND by `HitscanWeapon.OnBarrelMoved`
    //     for a beam's visual. So one write steadies both, and a spun-up beam
    //     cannot desync from its own damage ray the way `spiral` once did.
    //
    // IT DELIBERATELY DOES NOT MODEL A METER THE WAY OVERHEAT DOES. Heat needs
    // one (a max, a rate per shot, a rate per second, a cool rate, a cool delay,
    // a vent threshold) because the interesting part is *where the ceiling is*.
    // Spin-up has no ceiling to discover: it is at 0 or on its way to 1. So it
    // is authored as TIMES - "two seconds of fire to wind up, one to wind down"
    // - which is the number the author actually has in mind. Blindly mirroring
    // overheat's five-number model would have made an easier feature harder to
    // write, which is the trap in "copy the feature that already works".
    //
    // WHAT COUNTS AS SUSTAINED FIRE. `Shooter.IsShooting` is the raw input and
    // `OwnerHasResource` is public, so "trigger down AND able to pay" is exactly
    // the signal the game itself gates firing on - an empty gun does not wind
    // up. A BLOCKED shooter does not either: `overheat`'s jam and `stun` both go
    // through `Shooter.Block`, and losing the wind-up when the gun seizes is
    // both the honest reading and the more interesting one.
    public static class ForgeSpinUp
    {
        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("WeaponForge.SpinUp");

        // How the reward arrives between empty and full.
        public const int Smooth = 0;   // every frame is slightly better
        public const int Stepped = 1;  // discrete stages, each announced
        public const int Click = 2;    // smooth, but full has a moment

        public static int ParseRamp(string s)
        {
            switch ((s ?? "").Trim().ToLowerInvariant())
            {
                case "stepped":
                case "steps":
                case "tiers":
                case "tiered":
                    return Stepped;
                case "click":
                case "clicked":
                case "snap":
                    return Click;
                default:
                    return Smooth;
            }
        }

        public static string RampWord(int r, int steps)
        {
            switch (r)
            {
                case Stepped:
                    return "in " + steps + " steps";
                case Click:
                    return "smoothly, with a click at full";
                default:
                    return "smoothly";
            }
        }

        public class Config
        {
            public bool enabled = true;

            // The clocks. `grace` is the trigger-discipline knob, exactly as
            // overheat's `coolDelay` is: with the wind-down starting the instant
            // you let go, tapping keeps the bonus for free.
            public float spinUpTime = 1.5f;
            public float spinDownTime = 1f;
            public float grace = 0.25f;

            public int ramp = Smooth;
            public int steps = 3;

            // ---- the rewards ----
            //
            // Each is opt-in and carries its own value at full, by request. Fire
            // rate and accuracy are the two that default ON, being the two he
            // asked for; the other three are off until named.
            public bool rate = true;
            public float fireRateAtFull = 2f;

            public bool accuracy = true;
            public float accuracyAtFull = 1f;   // fraction of variance removed
            public float spreadAtFull = 1f;     // multiplier on the fan, 1 = keep

            public bool damage;
            public float damageAtFull = 1.5f;

            public bool pellets;
            public float pelletsAtFull = 2f;

            public bool cheaper;
            public float costAtFull = 0.5f;

            // ---- feedback ----
            public bool tintMuzzle = true;
            public Color color = new Color(0.55f, 0.85f, 1f, 1f);
            public float tintStrength = 1f;
            public string readySfx = "";
            public string stepSfx = "";
            public string lostSfx = "";
            public bool showSpin;
        }

        private static readonly Dictionary<WeaponData, Config> _weapons =
            new Dictionary<WeaponData, Config>();

        public static void Register(WeaponData weapon, Config config)
        {
            if (weapon != null && config != null)
                _weapons[weapon] = config;
        }

        public static bool TryGet(WeaponData weapon, out Config config)
        {
            config = null;
            return weapon != null && _weapons.TryGetValue(weapon, out config);
        }

        public static bool Any { get { return _weapons.Count > 0; } }

        // ------------------------------------------------------------------
        // Is this Shooter blocked?
        // ------------------------------------------------------------------
        //
        // `Block(object)` and `Unblock(object)` are public but there is no
        // `IsBlocked`, and `blockers` is a private `HashSet<object>`. Worth the
        // one reflected read: a jam (overheat) or a freeze (stun) should cost
        // you the wind-up, and asking either feature directly would mean naming
        // them here and missing the next one.
        //
        // House rule on reflection - resolve once, latch either way, and degrade
        // to doing nothing. A gun that keeps winding up while jammed is a small
        // wrongness; a gun that throws every frame is not.
        private static FieldInfo _blockers;
        private static bool _blockersLooked;

        public static bool IsBlocked(Shooter shooter)
        {
            if (shooter == null)
                return false;

            if (!_blockersLooked)
            {
                _blockersLooked = true;
                _blockers = AccessTools.Field(typeof(Shooter), "blockers");

                if (_blockers == null)
                {
                    Log.LogInfo(
                        "Could not reach Shooter.blockers, so a jammed or " +
                        "stunned weapon will keep its spin-up instead of " +
                        "losing it. Everything else about spinUp works.");
                }
            }

            if (_blockers == null)
                return false;

            try
            {
                var set = _blockers.GetValue(shooter) as ICollection;
                return set != null && set.Count > 0;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
