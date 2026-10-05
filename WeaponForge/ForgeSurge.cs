using System;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;

namespace WeaponForge
{
    // SURGE - the overload window. `overheat` run backwards.
    //
    // His framing: *"the bonus effect happens as it cools down, like the
    // inverse of spinUp where it builds to a point and then bam you get the
    // bonus during the cooldown phase... the payout only comes from hitting the
    // charge limit and during the drain."*
    //
    // ★ THE SHARP WAY TO SAY IT: **the vent lockout and the surge are the same
    // phase with opposite signs.** `overheat`'s `response: vent` locks the gun
    // out *while the meter drains*; a surge rewards you *while the meter
    // drains*. Same threshold, same clock, same phase - only the sign of the
    // payload changes. That is why this feature is mostly wiring:
    //
    //   - `overheat` already owns the meter (`max`, `perShot`, `perSecond`,
    //     `coolPerSecond`, `coolDelay`, `ventTo`) and already ships a
    //     `response: none` documented as *"heat is only a trigger for the
    //     extras"*. This block is those extras.
    //   - `ForgeWeaponStats` already owns live tuning, so the rewards are
    //     published multipliers that compose with `spinUp`, the onKill buff and
    //     overheat's own `slow` penalty instead of fighting them.
    //   - `ForgeChargePatch.ArmExternally` already knows how to make a shot a
    //     charged one.
    //
    // **SO IT REQUIRES AN `overheat` BLOCK, and that is a feature rather than a
    // limitation** - one meter, one owner, one set of numbers to tune. The
    // builder checks the REGISTRATION rather than the JSON, which gets the
    // gadget refusal and `"enabled": false` for free.
    //
    // ⚠ THE ONE COMBINATION THAT WOULD SHIP IT DEAD, and it is the DEFAULT.
    // `ForgeOverheat.ParseResponse` returns `Vent` for anything it does not
    // recognise, INCLUDING a missing key - and `ForgeHeat.Blow` blocks the
    // Shooter until the meter is back under `ventTo`, which is exactly the
    // window a surge runs in. The reward would apply perfectly to a gun that
    // cannot fire, and both features would report themselves working. So the
    // builder switches the response to `none` and says so, loudly when the
    // author wrote "vent" on purpose and quietly when it only came from the
    // default. See ApplySurge.
    public static class ForgeSurge
    {
        private static readonly ManualLogSource Log =
            ForgeLog.Source("WeaponForge.Surge");

        // How the reward behaves across the window.
        public const int Fade = 0;   // full at the blow, tapering to nothing
        public const int Flat = 1;   // full for the whole window, then gone

        public static int ParseRamp(string s)
        {
            switch ((s ?? "").Trim().ToLowerInvariant())
            {
                case "flat":
                case "hold":
                case "full":
                case "steady":
                case "window":
                    return Flat;
                default:
                    return Fade;
            }
        }

        public static string RampWord(int r)
        {
            return (r == Flat)
                ? "at FULL strength for the whole window, then stopping dead"
                : "at full strength the instant it blows, fading to nothing as " +
                  "the meter empties";
        }

        public class Config
        {
            public bool enabled = true;

            // ---- the window ----
            //
            // The surge ends when the meter falls to `endAt`. 0 means it runs
            // the whole way down.
            public float endAt;
            public int ramp = Fade;

            // ---- what FILLS the meter ----
            //
            // `fire` is what `overheat` already does (perShot + perSecond) and
            // is the default, so a surge block that says nothing about filling
            // changes nothing about filling. Naming any of the others REPLACES
            // the set rather than adding to it, which is what makes "this gun
            // only heats up when you actually land hits" expressible.
            public bool fillOnFire = true;
            public bool fillOnHit;
            public bool fillOnDamage;
            public float perHit = 5f;
            public float perDamage = 0.5f;

            // ---- what DRAINS it during the surge ----
            public bool drainOnCool = true;
            public bool drainOnFire;
            public float drainPerShot;

            // ⚠ CAN THE METER REFILL DURING THE SURGE? His words: *"you
            // shouldn't be able to build up during that phase, but an option to
            // do so could be interesting. Cause it would make the weapon more
            // of an active in combat sort of thing."* Off by default, and the
            // builder does the arithmetic against the drain - see ApplySurge,
            // because the two switches fight over one number and a net gain is
            // a permanent surge.
            public bool refill;

            // ---- the rewards ----
            //
            // SHARED WITH `spinUp` - see ForgeRewards for why that is a class
            // rather than two copies. Every entry is a multiplier at full
            // published to ForgeWeaponStats; `rate` and `accuracy` default on.
            public ForgeRewards rewards = new ForgeRewards();

            // Every shot fired in the window is a charged shot. Routed through
            // the `charge` block, because that block IS the set of multipliers
            // a charged shot scales by - the same reasoning as overheat's
            // `ventShot`, and the same refusal when it is missing.
            public bool chargedShots;
            public float chargeLevel = 1f;

            // ---- feedback ----
            //
            // THE COLOUR IS A SWAP, NOT A LAYER. `overheat` already tints the
            // muzzle (and a beam) toward `hotColor` as the meter fills, and a
            // second layer on top would blend into something that says neither
            // "hot" nor "surging". The whole point of the feedback here is that
            // *filling and draining look identical on a bar* - so during the
            // window `ForgeHeat.Paint` publishes THIS colour on the same layer
            // and the phase is unmistakable.
            public bool tintMuzzle = true;
            public Color color = new Color(1f, 0.85f, 0.25f, 1f);
            public float tintStrength = 1f;

            public string startSfx = "";
            public string endSfx = "";
            public bool showSurge;

            public bool DrainsAtAll
            {
                get { return drainOnCool || (drainOnFire && drainPerShot > 0f); }
            }

            public bool FillsOnHitOrDamage
            {
                get { return fillOnHit || fillOnDamage; }
            }

            // Does any reward actually do something? A window with nothing in
            // it is a valid file and a dead feature. `chargedShots` counts:
            // it is not a stat, but it is unmistakably a payout.
            public bool PaysAnything
            {
                get { return rewards.PaysAnything || chargedShots; }
            }
        }

        private static readonly Dictionary<WeaponData, Config> _weapons =
            new Dictionary<WeaponData, Config>();

        // A SEPARATE GATE FOR THE DAMAGE HOOK. `DamagableResource.Damage` runs
        // on every point of damage anything in the game takes, so the prefix
        // there must cost one bool in the common case - and almost no surge
        // weapon fills on hits. `Any` is not a tight enough test.
        public static bool AnyHitFill { get; private set; }

        public static void Register(WeaponData weapon, Config config)
        {
            if (weapon == null || config == null)
                return;

            _weapons[weapon] = config;

            if (config.FillsOnHitOrDamage)
                AnyHitFill = true;
        }

        public static bool TryGet(WeaponData weapon, out Config config)
        {
            config = null;
            return weapon != null && _weapons.TryGetValue(weapon, out config);
        }

        public static bool Any { get { return _weapons.Count > 0; } }

        // ------------------------------------------------------------------
        // Parsing helpers shared with the builder
        // ------------------------------------------------------------------

        // `fill` and `drain` are both "one or several of a small set", which is
        // `split.on`'s shape - so they take a string, a comma-separated string
        // or an array, exactly as the readout `show` lists do.
        public static void ParseFill(
            IEnumerable<string> words, Config cfg, string fileName,
            out List<string> unknown)
        {
            unknown = new List<string>();

            bool sawAny = false;
            bool fire = false, hit = false, dmg = false;

            foreach (string raw in words)
            {
                string n = (raw ?? "").Trim().ToLowerInvariant();

                if (n.Length == 0)
                    continue;

                switch (n)
                {
                    case "fire":
                    case "firing":
                    case "shots":
                    case "shooting":
                        fire = true; sawAny = true; break;

                    case "hit":
                    case "hits":
                    case "hitting":
                        hit = true; sawAny = true; break;

                    case "damage":
                    case "dealt":
                    case "damagedealt":
                        dmg = true; sawAny = true; break;

                    case "all":
                    case "both":
                    case "everything":
                        fire = true; hit = true; dmg = true; sawAny = true;
                        break;

                    default:
                        unknown.Add(raw);
                        break;
                }
            }

            if (!sawAny)
                return;

            cfg.fillOnFire = fire;
            cfg.fillOnHit = hit;
            cfg.fillOnDamage = dmg;
        }

        public static void ParseDrain(
            IEnumerable<string> words, Config cfg, out List<string> unknown)
        {
            unknown = new List<string>();

            bool sawAny = false;
            bool cool = false, fire = false;

            foreach (string raw in words)
            {
                string n = (raw ?? "").Trim().ToLowerInvariant();

                if (n.Length == 0)
                    continue;

                switch (n)
                {
                    case "cool":
                    case "cooling":
                    case "coolpersecond":
                    case "time":
                        cool = true; sawAny = true; break;

                    case "fire":
                    case "firing":
                    case "shots":
                    case "shooting":
                        fire = true; sawAny = true; break;

                    case "all":
                    case "both":
                        cool = true; fire = true; sawAny = true; break;

                    default:
                        unknown.Add(raw);
                        break;
                }
            }

            if (!sawAny)
                return;

            cfg.drainOnCool = cool;
            cfg.drainOnFire = fire;
        }

        public static string FillWords(Config c)
        {
            var parts = new List<string>();

            if (c.fillOnFire) parts.Add("firing");
            if (c.fillOnHit) parts.Add("landing hits");
            if (c.fillOnDamage) parts.Add("damage dealt");

            return (parts.Count == 0)
                ? "NOTHING"
                : string.Join(" + ", parts.ToArray());
        }

        public static string DrainWords(Config c)
        {
            var parts = new List<string>();

            if (c.drainOnCool) parts.Add("the meter's own cooling");
            if (c.drainOnFire) parts.Add("firing");

            return (parts.Count == 0)
                ? "NOTHING"
                : string.Join(" + ", parts.ToArray());
        }
    }
}
