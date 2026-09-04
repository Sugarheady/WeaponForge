using System;
using System.Collections.Generic;
using UnityEngine;

namespace WeaponForge
{
    // THE CHARGE LUNGE - wind up, then launch yourself.
    //
    // Modelled on the Maggot, which is where he pointed me. Its charge is a
    // three-state machine and the NUMBERS are the whole design:
    //
    //     state          movementForce   maxSpeed   maxAngularVelocity
    //     Aim Move             400          1              1
    //     Charge Move         2000        850            0.3
    //     Move Away            900         50             25
    //
    // THE WIND-UP IS NOT A TIMER, IT IS `maxSpeed: 1`. The Maggot telegraphs by
    // almost STOPPING while it slowly turns to face you. And the commitment is
    // `maxAngularVelocity: 0.3` - once launched it can barely steer, which is
    // exactly what makes it dodgeable rather than unfair. Those two facts are
    // copied here; the damage is incidental by comparison.
    //
    // IT RIDES THE `charge` BLOCK rather than growing its own hold detection.
    // That block already owns the meter, the minimum charge, the response
    // curve, the ready sound and tap-vs-hold - so a lunge gets all of it free,
    // and `charge` keeps being the one owner of "how charged am I". The cost:
    // **a lunge needs a `charge` block, in Release mode.** Auto mode fires
    // continuously and never has a release to launch on; the builder says so.
    //
    // THE SHIP'S MASS IS EXACTLY 1, which makes this unusually easy to tune:
    // an impulse IS the resulting speed in units per second. For scale, the
    // ship's own `maxSpeed` is 12.5 and its dash peaks around 43.
    public static class ForgeLunge
    {
        // ---- which way it goes -------------------------------------------
        public const int AimCursor = 0;   // default
        public const int AimMove = 1;     // the movement stick, like Dash
        public const int AimBarrel = 2;   // where the weapon is pointing

        public static int ParseAim(string s)
        {
            switch ((s ?? "").Trim().ToLowerInvariant())
            {
                case "move":
                case "movement":
                case "stick":
                case "dash":
                    return AimMove;
                case "barrel":
                case "facing":
                case "weapon":
                case "aim":
                    return AimBarrel;
                default:
                    return AimCursor;
            }
        }

        public static string AimWord(int a)
        {
            switch (a)
            {
                case AimMove: return "along the movement stick (like the game's dash)";
                case AimBarrel: return "wherever the weapon is pointing";
                default: return "toward the cursor";
            }
        }

        public class Config
        {
            public bool enabled = true;

            // ---- the wind-up ----------------------------------------------
            //
            // Multipliers on the ship's own movement while charging, reached
            // at FULL charge and eased in from 1 as the meter fills. The
            // ship ships `maxSpeed: 12.5` and `acceleration: 45`, so 0.25
            // leaves you crawling at about 3 - the Maggot's "almost stopped".
            public float windSpeed = 0.3f;
            public float windAccel = 0.4f;

            // Above 1 the slow arrives late (you keep your speed until the
            // charge is nearly full); below 1 it bites immediately.
            public float windCurve = 1f;

            // ---- the launch ------------------------------------------------
            //
            // An impulse. The ship's mass is 1, so this is literally the speed
            // in units/second - and its normal top speed is 12.5.
            public float force = 60f;

            // Force at the MINIMUM charge the `charge` block allows. Below
            // that the charge block will not fire at all.
            public float minForce;

            public int aim = AimCursor;

            // ---- the commitment --------------------------------------------
            //
            // Seconds during which your engines are throttled, so you fly the
            // arc you committed to. This is the Maggot's maxAngularVelocity
            // 0.3, expressed the way a ship can express it.
            public float commit = 0.3f;

            // What fraction of your normal acceleration you keep while
            // committed. 0 is a pure ballistic arc; 0.3 lets you nudge it.
            public float commitSteer;

            // ---- what it does ----------------------------------------------
            //
            // Does the charged shot still go off? On, this is a shoulder-barge
            // with a gun. Off sets projectileCount to 0 - the same route
            // SWIPE's pureMelee takes, so cost, fire rate and the shoot sound
            // are all still taken.
            public bool fires = true;

            // 0 means "leave the hurting to a `contactDamage` block". Any
            // value above 0 gives the ship its own contact damage for the
            // length of the lunge, built from the keys below - which is why
            // this does not duplicate a line of the damage code.
            public float damage;
            public Resource damageType;
            public float push = 40f;
            public float recoil;
            public float repeatDelay = 0.3f;
            public float radius;
            public bool hitsPlayer;

            // Crashing into terrain at the end of a lunge. 0 is free, which is
            // what the game does today - the ship's own ImpactDamage is off
            // unless you are sprinting.
            public float selfDamage;

            public string sfx = "";

            public bool DoesAnything { get { return force > 0f; } }

            // Eased charge level -> how much of the wind-up applies.
            public float WindAt(float level)
            {
                float t = Mathf.Clamp01(level);

                if (Mathf.Abs(windCurve - 1f) > 0.001f)
                    t = Mathf.Pow(t, Mathf.Max(0.05f, windCurve));

                return t;
            }
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

            if (weapon == null || !_weapons.TryGetValue(weapon, out config))
                return false;

            return config != null && config.enabled;
        }

        public static bool Any { get { return _weapons.Count > 0; } }
    }
}
