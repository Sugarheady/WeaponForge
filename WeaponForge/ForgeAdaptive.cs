using System;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;

namespace WeaponForge
{
    // ADAPTIVE DAMAGE TYPE - "prism rounds". Each hit switches the shot's
    // damage type to whichever one this particular victim is worst against.
    //
    // HOW DAMAGE TYPE ACTUALLY WORKS, verified rather than recalled:
    //
    //     TakeDamage(Damage) -> shields -> Damage(GetDamageAmount(damage))
    //     GetDamageAmount:  amount *= damage.damageType.GetDamageMultiplierOn(this.resource)
    //
    // So it is the ATTACKER's `damageType` looked up against the DEFENDER's own
    // `DamagableResource.resource`. Nothing in the game and nothing in either
    // mod has ever varied that per target - a weapon picks one type and keeps
    // it - which is exactly the gap this fills.
    //
    // THE REAL TABLE, decoded from the assets (my earlier note said "up to 20x",
    // which was wrong - 20 is the SWING between the best and worst choice, and
    // the largest single multiplier is 5):
    //
    //     attacker    vs Purple  vs Caps  vs Electron  vs Tech
    //     Electron       x5        x5         x1        x0.5
    //     Caps           x4        x1         x3        x0.25
    //     White          x4        x4         x3        x0.25
    //     Tech           x4        x4         x3        x0.25
    //     Purple         x4        x4         x3        x0.25
    //     Health         x1        x4         x3        x0.25
    //     Fuel / Money   x1        x1         x1        x1
    //
    // A ROW WITH NO GUID IN THE YAML IS A SELF-REFERENCE, not a null - my first
    // decode read Tech's and Purple's self entries as "(none)" and got two cells
    // wrong. Tech resists ITSELF at x0.25; Purple takes x4 from itself. Neither
    // changes the conclusions below, but the table above is now the real one.
    //
    // TWO CONSEQUENCES WORTH KNOWING BEFORE BUILDING A WEAPON AROUND THIS:
    //
    //   * vs a TECH defender the best available is x1, from Fuel or Money.
    //     Everything else is x0.25. So against Tech this feature is not a
    //     damage boost at all - it is the removal of a 4x PENALTY, which is
    //     why Fuel belongs in the candidate list even though it never wins
    //     anywhere else.
    //   * MOST THINGS DO NOT CARE. Counted across the game's prefabs: 100 of
    //     129 damageable components defend with Health, 13 with Fuel and 4 with
    //     Money - and none of those three is a key in any table, so they are
    //     always x1. Only 11 of 129 (about 8%) have any damage-type
    //     relationship at all. This is a sharp tool for specific enemies, not a
    //     general damage increase, and the build log says so.
    public static class ForgeAdaptive
    {
        private static readonly ManualLogSource Log =
            ForgeLog.Source("WeaponForge.Adaptive");

        // The full set worth trying by default: every resource that is either a
        // strong attacker somewhere, or a NEUTRAL one that dodges the Tech
        // penalty. Money is left out deliberately - it behaves like Fuel here
        // and the game manages it run-wide.
        public static readonly string[] DefaultTypes =
        {
            "Resource Electron",
            "Resource White",
            "Resource Caps",
            "Resource Tech",
            "Resource Purple",
            "Resource Fuel",
        };

        public class Config
        {
            public List<string> typeNames = new List<string>();

            // Resolved once at build time; a name that does not resolve is
            // dropped with a warning rather than failing the weapon.
            [NonSerialized] public Resource[] types;

            // Never pick something WORSE than the weapon's own type. On by
            // default: "adaptive" should be a floor as well as a ceiling.
            public bool neverWorse = true;

            // Flash the victim in the chosen type's own colour, so a switch is
            // visible. The game announces nothing, same problem crit had.
            public bool tint = true;
            public float tintSeconds = 0.18f;

            // See ForgeUnitTint. The COLOUR here is the damage type's own, so
            // this is the only control over how loud it is.
            public float tintStrength = 1f;

            // multiply (the default, and what every existing file was tuned
            // against) or replace, which moves the sprite TOWARD the colour so
            // it can brighten a dark enemy. See ForgeUnitTint.
            public int tintMode = ForgeUnitTint.Multiply;
        }

        private static readonly Dictionary<WeaponData, Config> _weapons =
            new Dictionary<WeaponData, Config>();

        public static bool Any { get; private set; }

        public static void Register(WeaponData weapon, Config config)
        {
            if (weapon == null || config == null ||
                config.types == null || config.types.Length == 0)
            {
                return;
            }

            _weapons[weapon] = config;
            Any = true;
        }

        public static bool TryGet(WeaponData weapon, out Config config)
        {
            config = null;

            return weapon != null && _weapons.TryGetValue(weapon, out config) &&
                   config != null;
        }

        // Which candidate hits `defender` hardest?
        //
        // Returns null when nothing beats what the shot already had, so the
        // caller can leave the Damage struct completely alone - which keeps
        // this a no-op on the ~91% of things that have no table entry.
        public static Resource Best(
            Config cfg, Resource defender, Resource current, out float gain)
        {
            gain = 1f;

            if (cfg == null || cfg.types == null || defender == null)
                return null;

            // What the shot would do as-is. A null type is x1 by the game's own
            // rule (Damage.GetDamageMultiplier returns 1 for a null type).
            float now = (current != null)
                ? current.GetDamageMultiplierOn(defender) : 1f;

            Resource best = null;
            float bestMul = cfg.neverWorse ? now : float.MinValue;

            for (int i = 0; i < cfg.types.Length; i++)
            {
                Resource r = cfg.types[i];

                if (r == null || ReferenceEquals(r, current))
                    continue;

                float m = r.GetDamageMultiplierOn(defender);

                if (m > bestMul)
                {
                    bestMul = m;
                    best = r;
                }
            }

            if (best == null)
                return null;

            gain = (now > 0.0001f) ? (bestMul / now) : bestMul;
            return best;
        }

        public static void Describe(Config cfg, Resource own, string fileName)
        {
            if (cfg == null || cfg.types == null)
                return;

            var names = new List<string>();

            for (int i = 0; i < cfg.types.Length; i++)
            {
                if (cfg.types[i] != null)
                    names.Add(cfg.types[i].name);
            }

            Log.LogInfo(
                fileName + ": adaptive damage type - each hit switches to " +
                "whichever of " + names.Count + " types the victim takes worst " +
                "(" + string.Join(", ", names.ToArray()) + ")." +
                (cfg.neverWorse
                    ? " Never picks worse than the weapon's own type."
                    : " \"neverWorse\": false - it may pick a WORSE type than " +
                      "the weapon's own, which is only useful for testing."));

            // THE REACH WARNING. Without this the feature looks broken on
            // almost everything, because almost everything is immune to it.
            Log.LogWarning(
                fileName + ": adaptive damage type only does something against " +
                "enemies whose own health resource appears in the damage table " +
                "- Caps, Electron, Purple and Tech. Counted across the game's " +
                "prefabs that is about 11 of 129; the other ~91% defend with " +
                "Health, Fuel or Money, which are always x1 whatever hits them. " +
                "Expect this to be a specialist weapon, not a general damage " +
                "increase. The biggest single multiplier available is x5 " +
                "(Electron into Caps or Purple), and against TECH the best any " +
                "type manages is x1 - there it removes a x0.25 penalty rather " +
                "than adding damage.");
        }

        public static void Reset() { }
    }
}
