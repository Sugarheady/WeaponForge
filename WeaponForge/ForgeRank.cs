using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace WeaponForge
{
    // `rank` - a gun that levels on the damage it deals.
    //
    // NAMED `rank` AND NOT `level` on purpose. `Module.Level` already exists,
    // is raised by BoosterCores, and three features have shipped broken by
    // misreading it - most recently `wingman.capPerLevel`. A second meaning for
    // the same word in the same mod is a bug report waiting to be filed.
    //
    // ★ THE ARCHITECTURE, in one paragraph. Rank pays out in two currencies.
    // STATS are per-rank multiplier tables published through
    // `ForgeWeaponStats`, which already owns the ratchet problem. FEATURES are
    // unlocked by **editing the weapon's own JSON and re-running
    // `ApplyBehaviourFeatures`** - which is a re-runnable method with a public
    // door (`WeaponBuilder.ApplyFeaturesTo`) because the wingman feature needed
    // to build weapons at runtime. That choice is what makes `onRepeat` cheap:
    // deepening `chain.jumps` is an edit to a JSON number, not reflection into
    // twelve different config classes, and the author is already writing those
    // key names by hand.
    //
    // WHAT PERSISTS AND WHAT DOES NOT. Progress belongs to ONE COPY of a gun -
    // the Module instance, which the game never duplicates - and is written to
    // a sidecar in the SAVE FOLDER under where that copy is: its slot, its
    // place in the vault, or the pickup it is lying in. So it is slot-correct
    // for free, a deleted save takes it with it, and **"rank dies with the
    // run" needs no code at all**: death -> new run -> new save -> no sidecar.
    //
    // ★ IT USED TO BE KEYED `(ship, weapon FILE)`, and the note here called
    // that "a constraint rather than a choice" because a `WeaponBase` is
    // rebuilt whenever its cluster changes. The WeaponBase is rebuilt; the
    // MODULE is not, and its WeaponData is a settable per-instance property.
    // So it was a choice, and R19 252/256 showed it was the wrong one: two
    // copies of a file shared one climb, and a new gun arrived already ranked.
    // See ForgeRankCopies.
    public static class ForgeRank
    {
        // How much XP each successive rank costs.
        public enum Curve
        {
            Static,     // the same every time
            Step,       // xp, 2*xp, 3*xp ... his "100, 200, 300"
            Percent     // xp, then +N% of the previous requirement
        }

        // How a feature unlock is chosen.
        public enum Draw
        {
            Order,      // straight down the pool as authored
            Random,     // one at random from what is left
            Pickup      // spawn a few and fly into the one you want
        }

        // What counts as discovering a station.
        public enum Discover
        {
            Use,        // you interacted with it
            Map         // it was revealed on the map
        }

        public class Config
        {
            public bool enabled = true;
            public string fileName = "";

            // ---- the climb ----
            public int maxRank = 10;
            public Curve curve = Curve.Static;
            public float xp = 400f;
            public float xpPercent = 25f;

            // ---- what earns it ----
            public bool countObjects;        // crates, boxes, fruit
            public bool fromDamageOverTime = true;

            // ★ DEFAULT TRUE, AND THAT IS THE BEHAVIOUR THAT ALREADY SHIPPED
            // rather than a new choice. A wingman's weapon is built with
            // `Equip(shipUnit)`, so its shots already report the PLAYER as
            // `WeaponBase.Owner` and already bank into the player's own
            // `(shipId, file)` record - there is one progress record, not one
            // per copy. This key exists to turn that OFF for anyone who wants a
            // gun that only levels from damage they dealt themselves.
            public bool fromWingmen = true;
            public bool stations = true;
            public float stationXp = 500f;
            public Discover discover = Discover.Use;

            // R19 277, his follow-up: "Picking up other items like fiber and
            // powerstars dont give xp! Maybe make an option for that to be a
            // thing?" OFF by default (0), because the reason pickups paid
            // nothing is that they USED to pay a whole station's XP each (251).
            // Paid per ingredient, consumable or module picked up - not per
            // resource orb, which would turn every fuel bubble into XP.
            public float pickupXp;

            // ---- what it pays ----
            //
            // STAT -> one multiplier per rank. Index 0 is rank 0, so a table
            // normally starts at 1 (unchanged). A table shorter than `maxRank`
            // holds its last value, which is what makes `[1, 1.5, 2]` a
            // sensible thing to write on a `maxRank: 9999` weapon.
            public readonly Dictionary<int, float[]> stats =
                new Dictionary<int, float[]>();

            // The unlock pool: names of JSON blocks on this same weapon that
            // start dormant and switch on when drawn.
            public readonly List<string> unlocks = new List<string>();

            // Per-feature deepening for a repeat draw. feature -> field ->
            // delta, where a plain number is a flat add and a string ending in
            // '%' is a percentage. **Percentages SUM, they do not compound** -
            // three 20% draws are +60%, following `ForgeKillBuff`.
            public readonly Dictionary<string, Dictionary<string, Delta>>
                onRepeat =
                    new Dictionary<string, Dictionary<string, Delta>>();

            // ---- how a feature is chosen ----
            public Draw draw = Draw.Order;

            // ---- feedback ----
            public bool show = true;          // the RANK line on the weapon card
            public string sfx = "";
            public bool log = true;           // a ship-log line on rank up
            public int pickupCount = 3;

            // The line under each choice's name, in the author's own words:
            // `"describe": { "chain": "Shots arc to the nearest enemy" }`.
            // Anything not named here is described from its own block.
            public readonly Dictionary<string, string> describe =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            // THE WEAPON'S OWN JSON, deep-cloned at build time. This is what an
            // unlock edits and what the chain is re-run against. Cloned rather
            // than referenced because `WeaponBuilder` hands the same JObject to
            // fifty Apply* methods and one of them mutating it would be a
            // different bug entirely.
            [NonSerialized] public JObject source;
            [NonSerialized] public bool isGadget;
            [NonSerialized] public string slot = "primary";
        }

        // A repeat-draw delta: `+1` (flat) or `+20%` (of the value the file was
        // authored with, summed across draws).
        public struct Delta
        {
            public float amount;
            public bool percent;
        }

        // ---- the build-time registry -------------------------------------
        //
        // ★ DELIBERATELY NEVER RESET. These are registered once at startup from
        // the weapon files; clearing them at run entry would silently delete
        // rank from every weapon for the rest of the session. Only the PROGRESS
        // (below) is per-run. Same trap `ForgeCrit.Reset` documents.
        private static readonly Dictionary<WeaponData, Config> _weapons =
            new Dictionary<WeaponData, Config>();

        public static bool Any { get { return _weapons.Count > 0; } }

        public static void Register(WeaponData weapon, Config cfg)
        {
            if (weapon == null || cfg == null)
                return;

            _weapons[weapon] = cfg;

            // Burn attribution costs a per-frame bool on every damageable until
            // something asks for it. Nothing does unless a rank weapon wants
            // damage-over-time to pay.
            if (cfg.fromDamageOverTime)
                ForgeIgnition.Any = true;
        }

        public static bool TryGet(WeaponData weapon, out Config cfg)
        {
            cfg = null;

            return weapon != null && _weapons.TryGetValue(weapon, out cfg);
        }

        public static bool TryGet(WeaponBase weapon, out Config cfg)
        {
            cfg = null;

            return weapon != null &&
                   TryGet(weapon.TemplateData, out cfg);
        }

        // ---- the curve ----------------------------------------------------

        // What the step FROM `rank` TO `rank + 1` costs. `rank` is 0-based, so
        // Required(0) is the cost of reaching rank 1.
        public static float Required(Config cfg, int rank)
        {
            if (cfg == null || rank < 0)
                return float.PositiveInfinity;

            float baseXp = Mathf.Max(1f, cfg.xp);

            switch (cfg.curve)
            {
                case Curve.Step:
                    return baseXp * (rank + 1);

                case Curve.Percent:
                {
                    // ⚠ A PERCENT CURVE SILENTLY STOPS THE FEATURE at a high
                    // `maxRank`. 1.25^9999 is about 10^969 - past float (3.4e38)
                    // AND past double - so the requirement becomes Infinity and
                    // ranking stops with nothing reported. `PercentCeiling`
                    // below is what the build log prints so it is said at load
                    // rather than discovered at rank 400.
                    double step = 1.0 + Mathf.Max(0f, cfg.xpPercent) / 100.0;
                    double want = baseXp * Math.Pow(step, rank);

                    if (double.IsInfinity(want) || want > float.MaxValue)
                        return float.PositiveInfinity;

                    return (float)want;
                }

                default:
                    return baseXp;
            }
        }

        // The rank at which a percent curve leaves numbers a float can hold.
        // `int.MaxValue` means "never within this maxRank".
        public static int PercentCeiling(Config cfg)
        {
            if (cfg == null || cfg.curve != Curve.Percent)
                return int.MaxValue;

            for (int r = 0; r < cfg.maxRank; r++)
            {
                if (float.IsInfinity(Required(cfg, r)))
                    return r + 1;
            }

            return int.MaxValue;
        }

        // ---- the stat tables ----------------------------------------------

        // The multiplier this table gives at `rank`, holding the last authored
        // value once the table runs out.
        public static float AtRank(float[] table, int rank)
        {
            if (table == null || table.Length == 0)
                return 1f;

            if (rank < 0)
                rank = 0;

            if (rank >= table.Length)
                rank = table.Length - 1;

            return table[rank];
        }

        // What the gun looks like at the top of its climb, so the build log can
        // print it. "grow at 10%/rank over 9999 ranks is x1000" is legitimate if
        // intended and is not derivable from `0.1` and `9999` in a file.
        public static float Terminal(Config cfg, int stat)
        {
            float[] table;

            if (cfg == null || !cfg.stats.TryGetValue(stat, out table))
                return 1f;

            return AtRank(table, cfg.maxRank);
        }

        // ---- parsing -------------------------------------------------------

        public static bool ParseCurve(string s, out Curve curve)
        {
            curve = Curve.Static;

            if (string.IsNullOrEmpty(s))
                return true;

            switch (s.Trim().ToLowerInvariant())
            {
                case "static":
                case "flat":
                case "same": curve = Curve.Static; return true;

                case "step":
                case "increment":
                case "linear": curve = Curve.Step; return true;

                case "percent":
                case "percentage":
                case "compound": curve = Curve.Percent; return true;
            }

            return false;
        }

        public static bool ParseDraw(string s, out Draw draw)
        {
            draw = Draw.Order;

            if (string.IsNullOrEmpty(s))
                return true;

            switch (s.Trim().ToLowerInvariant())
            {
                case "order":
                case "inorder":
                case "list": draw = Draw.Order; return true;

                case "random":
                case "shuffle": draw = Draw.Random; return true;

                case "pickup":
                case "pickups":
                case "choice":
                case "choose": draw = Draw.Pickup; return true;
            }

            return false;
        }

        public static bool ParseDiscover(string s, out Discover d)
        {
            d = Discover.Use;

            if (string.IsNullOrEmpty(s))
                return true;

            switch (s.Trim().ToLowerInvariant())
            {
                case "use":
                case "used":
                case "interact": d = Discover.Use; return true;

                case "map":
                case "reveal":
                case "discovered": d = Discover.Map; return true;
            }

            return false;
        }

        // ★ ONE VOCABULARY FOR THE STATS. These are the same eighteen
        // `ForgeWeaponStats` carries and the same words `surge` and `spinUp`
        // already use, accepted in both spellings - the reward-block name
        // (`rate`, `pellets`, `cheaper`) and the plain one (`fireRate`,
        // `projectileCount`, `cost`). A per-rank table is a different SHAPE for
        // the same idea, so it must not be a different set of words.
        public static bool ParseStat(string s, out int stat)
        {
            stat = -1;

            if (string.IsNullOrEmpty(s))
                return false;

            switch (s.Trim().ToLowerInvariant())
            {
                case "rate":
                case "firerate": stat = ForgeWeaponStats.FireRate; return true;

                case "damage": stat = ForgeWeaponStats.Damage; return true;

                case "accuracy":
                case "anglevariance":
                case "scatter":
                    stat = ForgeWeaponStats.AngleVariance; return true;

                case "spread": stat = ForgeWeaponStats.Spread; return true;

                case "pellets":
                case "projectilecount":
                case "count": stat = ForgeWeaponStats.Pellets; return true;

                case "cheaper":
                case "cost": stat = ForgeWeaponStats.Cost; return true;

                case "range": stat = ForgeWeaponStats.Range; return true;

                case "speed":
                case "projectilespeed": stat = ForgeWeaponStats.Speed; return true;

                case "burst":
                case "burstsize": stat = ForgeWeaponStats.BurstSize; return true;

                case "burstdelay": stat = ForgeWeaponStats.BurstDelay; return true;

                case "burn": stat = ForgeWeaponStats.Burn; return true;

                case "push":
                case "pushforce": stat = ForgeWeaponStats.Push; return true;

                case "knockback": stat = ForgeWeaponStats.Knockback; return true;

                case "size":
                case "projectileradius":
                case "projectilesize": stat = ForgeWeaponStats.Size; return true;

                case "explosion":
                case "explosionradius":
                    stat = ForgeWeaponStats.ExplosionRadius; return true;

                case "explosiondamage":
                    stat = ForgeWeaponStats.ExplosionDamage; return true;

                case "warmup":
                case "warmuptime": stat = ForgeWeaponStats.Warmup; return true;

                case "lifetime":
                case "projectilelifetime":
                    stat = ForgeWeaponStats.Lifetime; return true;
            }

            return false;
        }

        // The word a card uses for a stat. Plain words, because the card is
        // read by the player, not by the author of the file.
        public static string StatLabel(int stat)
        {
            if (stat == ForgeWeaponStats.FireRate) return "fire rate";
            if (stat == ForgeWeaponStats.Damage) return "damage";
            if (stat == ForgeWeaponStats.AngleVariance) return "scatter";
            if (stat == ForgeWeaponStats.Spread) return "spread";
            if (stat == ForgeWeaponStats.Pellets) return "shots";
            if (stat == ForgeWeaponStats.Cost) return "cost";
            if (stat == ForgeWeaponStats.Range) return "range";
            if (stat == ForgeWeaponStats.Speed) return "speed";
            if (stat == ForgeWeaponStats.BurstSize) return "burst";
            if (stat == ForgeWeaponStats.BurstDelay) return "burst delay";
            if (stat == ForgeWeaponStats.Burn) return "burn";
            if (stat == ForgeWeaponStats.Push) return "push";
            if (stat == ForgeWeaponStats.Knockback) return "knockback";
            if (stat == ForgeWeaponStats.Size) return "size";
            if (stat == ForgeWeaponStats.ExplosionRadius) return "blast radius";
            if (stat == ForgeWeaponStats.ExplosionDamage) return "blast damage";
            if (stat == ForgeWeaponStats.Warmup) return "warm-up";
            if (stat == ForgeWeaponStats.Lifetime) return "lifetime";

            return "stat " + stat;
        }

        public static string StatNames()
        {
            return "rate, damage, accuracy, spread, pellets, cost, range, " +
                   "speed, burst, burstDelay, burn, push, knockback, size, " +
                   "explosion, explosionDamage, warmup, lifetime";
        }

        // A repeat-draw delta. `2` is a flat +2; `"+20%"` or `"20%"` is a
        // percentage of the authored value. Emitted as a TYPED number rather
        // than a string wherever it can be, which is the `split.damage` lesson.
        public static bool ParseDelta(JToken token, out Delta delta)
        {
            delta = new Delta();

            if (token == null || token.Type == JTokenType.Null)
                return false;

            if (token.Type == JTokenType.String)
            {
                string s = ((string)token ?? "").Trim();

                if (s.Length == 0)
                    return false;

                bool pct = s.EndsWith("%", StringComparison.Ordinal);

                if (pct)
                    s = s.Substring(0, s.Length - 1).Trim();

                if (s.StartsWith("+", StringComparison.Ordinal))
                    s = s.Substring(1).Trim();

                float v;

                if (!float.TryParse(s, NumberStyles.Float,
                                    CultureInfo.InvariantCulture, out v))
                    return false;

                delta.amount = v;
                delta.percent = pct;

                return true;
            }

            try
            {
                delta.amount = (float)token;
                delta.percent = false;

                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // ---- live progress -------------------------------------------------
        //
        // Keyed `(ship instanceId, weapon file)` - NOT the live WeaponBase,
        // which is rebuilt whenever its module cluster changes.
        public class Progress
        {
            public int rank;
            public float xp;

            // ★ DRAW COUNTS, NOT A LIST. `chain:3, split:1` is compact, is
            // exactly what `onRepeat` needs to know, and is the only form that
            // survives `maxRank: 9999` - a list would have been fine only for
            // small caps.
            public readonly Dictionary<string, int> draws =
                new Dictionary<string, int>();

            // Stations already paid for, so discovery pays ONCE PER STATION,
            // EVER - "only xp from that not from constant use of them".
            public readonly HashSet<int> paidStations = new HashSet<int>();

            // Set when something changed and the sidecar should be rewritten.
            public bool dirty;

            // Pickup draws earned and not yet taken. Saved, so ranking up and
            // then saving with the choices still out does not lose the rank's
            // reward - the set comes back when the gun is next equipped.
            public int owed;

            // THIS COPY's weapon JSON: the file's, with the draws switched on
            // and deepened. Per copy, because an unlock edits it - and editing
            // the file's shared clone is how one copy's unlock would have
            // reached every other copy of the gun. Rebuilt from the draws, so
            // it is never saved.
            [NonSerialized] public JObject json;

            public int DrawCount(string feature)
            {
                int n;

                return draws.TryGetValue(feature, out n) ? n : 0;
            }

            public void Draw(string feature)
            {
                draws[feature] = DrawCount(feature) + 1;
                dirty = true;
            }
        }

        // True while an unlock re-runs the feature chain on a copy's weapon.
        // `WeaponBuilder.ApplyRank` returns at once when it is set - see
        // ForgeRankCopies.Rebuild for the bug that made this necessary.
        public static bool Rebuilding;

        // ---- finding the ship again ---------------------------------------
        //
        // Progress is keyed by instance id because that is what a save file can
        // hold, but a draw needs the live `Unit` back. Remembered as it goes
        // past rather than searched for: `FindObjectsByType<Unit>()` would walk
        // every unit in the level, and the ship is right there in the hit that
        // earned the XP. A destroyed Unity object compares equal to null, so a
        // stale entry reports itself.
        private static readonly Dictionary<int, Unit> _ships =
            new Dictionary<int, Unit>();

        public static void Seen(int id, Unit ship)
        {
            if (ship != null)
                _ships[id] = ship;
        }

        public static Unit ShipById(int id)
        {
            Unit ship;

            if (!_ships.TryGetValue(id, out ship) || ship == null)
                return null;

            return ship;
        }

        // ★ PER-RUN STATE, so unlike `_weapons` this one IS cleared. Hooked from
        // `ForgeBurnResetPatch`, which fires at both run-entry points. The
        // progress itself lives on the copies now (ForgeRankCopies), and
        // clearing them releases the weapons made for them.
        public static void Reset()
        {
            _ships.Clear();
            Rebuilding = false;
            ForgeRankCopies.Reset();

            // The run's start station is per run; a continue reads it back
            // from the sidecar (the Load POSTFIX, after this prefix).
            ForgeRankStation.Reset();
        }
    }
}
