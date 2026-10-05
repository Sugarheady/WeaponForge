using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;

namespace WeaponForge
{
    // WHICH enemy a weapon's own targeting picks - opt-in, per weapon.
    //
    // Asked for as *"lets add the Target Priority feature... Keep the original
    // targeting option thats in the game. But give this as a side option that
    // the weapon can opt into. It shouldn't change any other behavior in the
    // game besides the weapon it's attached to."* All three of those are
    // structural here rather than promises:
    //
    //   OPT-IN      a weapon with no `targeting` block resolves to a NULL
    //               config, and every finder treats null as "score the way you
    //               always did". Not a branch that happens to match the old
    //               behaviour - literally the same code path.
    //   PER WEAPON  the registry is keyed on `WeaponData`, so the lookup can
    //               only ever return a config for the exact weapon asset that
    //               declared one. An enemy's gun, a stock gun and another Forge
    //               weapon all resolve to null.
    //   NOT GLOBAL  there is no ambient "current priority" static. The config
    //               travels from the shot (or the beam's weapon) to the finder
    //               as an argument, which is the one shape that cannot leak
    //               between weapons - unlike `ForgeContact`'s world rule, which
    //               was latched at build time and armed the whole game
    //               (see ForgeContact for that bug).
    //
    // WHAT IT DOES NOT TOUCH, AND MUST NEVER: the game's own aim assist. That
    // is what steers the BARREL - where your bullets actually go - and it is a
    // separate mechanism (`AssistedBarrel.GetTargetDirection` -> `AimAssist`).
    // This block only changes which enemy the mod's own features CHASE once a
    // shot is already in the air.
    //
    // Offered as a follow-up and DECLINED 2026-08-31: *"No. This shouldn't
    // touch aim assist at all. That would mess with the player and other
    // features that could be using that."* The second half is the load-bearing
    // reason, and it is verifiable rather than a worry:
    //
    //   `ForgeVolley.Axis` is assigned from `barrel.Direction`, which on the
    //   ship is the ASSIST-ADJUSTED direction. `ForgeConvergePatch` then takes
    //   its shared focal axis from `ForgeVolley.Axis` - so a converging volley's
    //   focus point is derived from wherever aim assist decided to point.
    //
    // So making assist prefer a low-health enemy would silently move a converge
    // weapon's focal point, which is a targeting feature reaching sideways into
    // an unrelated one. Do not build it.
    public static class ForgeTargeting
    {
        private static readonly ManualLogSource Log =
            ForgeLog.Source("WeaponForge.Targeting");

        // ------------------------------------------------------------------
        public const int Default = 0;      // whatever the finder already did
        public const int Nearest = 1;
        public const int Farthest = 2;
        public const int Lowest = 3;       // least CURRENT health
        public const int Highest = 4;
        public const int Weakest = 5;      // least health as a FRACTION
        public const int Toughest = 6;
        public const int Unshielded = 7;
        public const int Shielded = 8;
        public const int Forward = 9;      // smallest angle off the heading

        public static int Parse(string s, out bool known)
        {
            known = true;

            switch ((s ?? "").Trim().ToLowerInvariant())
            {
                case "":
                case "default":     return Default;
                case "nearest":
                case "closest":     return Nearest;
                case "farthest":
                case "furthest":    return Farthest;
                case "lowest":
                case "lowesthealth":
                case "finisher":    return Lowest;
                case "highest":
                case "highesthealth": return Highest;
                case "weakest":     return Weakest;
                case "toughest":
                case "strongest":   return Toughest;
                case "unshielded":  return Unshielded;
                case "shielded":    return Shielded;
                case "forward":
                case "angle":       return Forward;
            }

            known = false;
            return Default;
        }

        public static string Word(int mode)
        {
            switch (mode)
            {
                case Nearest:    return "nearest";
                case Farthest:   return "farthest";
                case Lowest:     return "lowest health";
                case Highest:    return "highest health";
                case Weakest:    return "weakest (lowest % health)";
                case Toughest:   return "toughest (highest % health)";
                case Unshielded: return "unshielded first";
                case Shielded:   return "shielded first";
                case Forward:    return "most forward";
            }

            return "default";
        }

        // ------------------------------------------------------------------
        public class Config
        {
            public int mode = Default;

            // Ignore crates, boxes and fruit when ranking - they are legitimate
            // AimAssistTargets and share the enemies' faction, so "lowest
            // health" will happily pick a crate otherwise. Off by default,
            // because bursting the crate beside you is usually wanted.
            public bool unitsOnly;
        }

        private static readonly Dictionary<WeaponData, Config> _weapons =
            new Dictionary<WeaponData, Config>();

        public static void Register(WeaponData weapon, Config cfg)
        {
            if (weapon == null || cfg == null || cfg.mode == Default)
                return;

            _weapons[weapon] = cfg;
        }

        public static bool Any { get { return _weapons.Count > 0; } }

        // The weapon's priority, or NULL for "behave exactly as before".
        public static Config For(WeaponBase weapon)
        {
            if (weapon == null || _weapons.Count == 0)
                return null;

            Config c;
            return (weapon.TemplateData != null &&
                    _weapons.TryGetValue(weapon.TemplateData, out c)) ? c : null;
        }

        // From a shot. `ForgeKillsPatch` stamps every projectile with the weapon
        // that fired it - including ones this mod creates mid-flight, which go
        // through `Attribute` - so a split fragment or a chained shot keeps its
        // parent weapon's priority. A shot from anything unregistered (an enemy
        // gun, a stock gun, a sub-emitter with no owner) returns null.
        public static Config For(Component shot)
        {
            if (shot == null || _weapons.Count == 0)
                return null;

            return For(ForgeKillsPatch.WeaponOf(shot));
        }

        // ------------------------------------------------------------------
        // SCORE A CANDIDATE. Lower wins, compared as (primary, secondary).
        //
        // Two numbers rather than one blended score, because every mode except
        // the distance ones needs a tie-break and blending needs a magic
        // constant big enough to dominate - which is only safe until someone
        // sets a range that breaks the assumption. Lexicographic comparison has
        // no such assumption: `unshielded` puts every unshielded target ahead of
        // every shielded one, and orders each group by distance.
        //
        // A null config returns (baseScore, 0), which is bit-for-bit what the
        // finders did before this existed.
        public static void Rank(
            Config cfg,
            AimAssistTarget t,
            Vector2 here,
            float baseScore,
            out float primary,
            out float secondary)
        {
            primary = baseScore;
            secondary = 0f;

            if (cfg == null || cfg.mode == Default || t == null)
                return;

            float dist =
                ((Vector2)t.transform.position - here).sqrMagnitude;

            secondary = dist;

            switch (cfg.mode)
            {
                case Nearest:
                    primary = dist;
                    secondary = 0f;
                    return;

                case Farthest:
                    primary = -dist;
                    secondary = 0f;
                    return;

                case Forward:
                    primary = baseScore;   // the angle the caller measured
                    return;

                case Unshielded:
                    primary = HasActiveShield(t) ? 1f : 0f;
                    return;

                case Shielded:
                    primary = HasActiveShield(t) ? 0f : 1f;
                    return;
            }

            // The health modes. A target with no health component cannot be
            // ranked by health, so it sorts LAST rather than being dropped -
            // it is still selectable when it is all there is, which keeps a
            // crate-only room from making a homing shot fly straight.
            HealthBase hp = t.GetComponentInParent<HealthBase>();

            if (hp == null)
            {
                primary = float.MaxValue;
                return;
            }

            float cur = hp.CurrentHealth;
            float max = hp.MaxHealth;

            switch (cfg.mode)
            {
                case Lowest:  primary = cur; return;
                case Highest: primary = -cur; return;

                case Weakest:
                case Toughest:
                    float frac = (max > 0.0001f) ? (cur / max) : 1f;
                    primary = (cfg.mode == Weakest) ? frac : -frac;
                    return;
            }
        }

        // Does this candidate pass the config's own filter? Separate from Rank
        // because a rejected target must not be picked at ANY score.
        public static bool Allowed(Config cfg, AimAssistTarget t)
        {
            if (cfg == null || !cfg.unitsOnly || t == null)
                return true;

            // Same discriminator the finders already use for `objectsToo`: all
            // 60 fighting units carry an AIAgent and none of the 13 containers
            // do.
            return t.GetComponentInParent<AIAgent>() != null;
        }

        // ------------------------------------------------------------------
        private static bool HasActiveShield(AimAssistTarget t)
        {
            Unit u = t.GetComponentInParent<Unit>();

            if (u == null)
                return false;

            // Shields are children of the unit, one per ShieldData, and
            // `IsActive` is purely "Charge > 0" - so a shield that has been
            // broken this fight correctly reads as absent.
            Shield[] shields = u.GetComponentsInChildren<Shield>(false);

            for (int i = 0; i < shields.Length; i++)
            {
                if (shields[i] != null && shields[i].IsActive)
                    return true;
            }

            return false;
        }

        // ------------------------------------------------------------------
        // Compare two (primary, secondary) pairs. Kept here so every finder
        // orders candidates identically.
        public static bool Beats(
            float primary, float secondary,
            float bestPrimary, float bestSecondary)
        {
            if (primary < bestPrimary)
                return true;

            if (primary > bestPrimary)
                return false;

            return secondary < bestSecondary;
        }

        public static void Describe(WeaponData weapon, Config cfg, string fileName)
        {
            if (cfg == null || cfg.mode == Default)
                return;

            Log.LogInfo(
                fileName + ": targeting priority \"" + Word(cfg.mode) +
                "\"" + (cfg.unitsOnly ? " (units only - crates and fruit are " +
                                        "ignored)" : "") +
                ". This changes which enemy THIS weapon's homing, chain, " +
                "ricochet, split, catalyst, converge and curve-beam lock pick " +
                "- nothing else in the game, and no other weapon. It does NOT " +
                "change the game's own aim assist, which is what steers the " +
                "barrel.");
        }
    }
}
