using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;

namespace WeaponForge
{
    // A SWARM GUN: a minion spawner on the trigger rather than a gadget key.
    //
    // The frame already existed - `MinionSpawnerWeaponData` is a real WeaponData
    // and the ship happily builds a `MinionSpawnerWeapon` from it - but three
    // things were missing, and the first one explains every symptom of the R8
    // test at once.
    //
    // 1. THE MINIONS HAD NO OWNER. The only thing in the game that calls
    //    SetOwner for a spawner weapon is `MinionOwnerSetter`, a MonoBehaviour
    //    carrying a serialized reference to its owning unit - and it exists on
    //    exactly THREE prefabs: Enemy_CrossQueen and the two Floater Officers.
    //    **It is not on the player ship.** So a spawner weapon in a player slot
    //    produced ownerless minions, which is why they had no follow AI ("goes
    //    to one spot to stay forever"), and why there was no cap: a cap needs
    //    the owner's minion list to count.
    //
    // 2. THERE IS NO CAP AT ALL. `MinionSpawnerWeapon.FireSingle` has no count
    //    check of any kind - the cap logic lives only in `SpawnMinionModule`,
    //    the gadget. Hence "spawns like 12 drones or something". So the cap here
    //    is entirely ours, including deciding what happens when you hit it.
    //
    // 3. NO GRID MODULES. The gadget path installs the cluster's other modules
    //    on each drone as augmentations; the spawner weapon does nothing of the
    //    sort. Replicated here behind `inheritGrid`, off by default - twenty
    //    minions each carrying a full grid is a different weapon entirely.
    public static class ForgeSwarm
    {
        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("WeaponForge.Swarm");

        // What happens at the cap. He asked for all four, defaulting to killing
        // the oldest.
        public const int CapKillOldest = 0;  // default: make room
        public const int CapBlock = 1;       // refuse to fire, keep the resource
        public const int CapFizzle = 2;      // fire, pay, spawn nothing
        public const int CapNone = 3;        // no cap - a firehose

        public static int ParseCapMode(string s)
        {
            switch ((s ?? "").Trim().ToLowerInvariant())
            {
                case "block":
                case "refuse":
                case "stop":
                    return CapBlock;
                case "fizzle":
                case "waste":
                case "nothing":
                    return CapFizzle;
                case "none":
                case "off":
                case "unlimited":
                    return CapNone;
                default:
                    return CapKillOldest;
            }
        }

        public static string CapWord(int mode)
        {
            switch (mode)
            {
                case CapBlock: return "block";
                case CapFizzle: return "fizzle";
                case CapNone: return "none";
                default: return "killOldest";
            }
        }

        // Our own live list per weapon, rather than reading the game's minion
        // list back.
        //
        // Two reasons. It is pruned and ORDERED, so "the oldest" is exact -
        // whereas the game's own trim does `Minions.First()`, which is why
        // going over the cap on one drone gadget can kill a drone belonging to
        // another. And it counts only what THIS weapon made, so a swarm gun and
        // a drone gadget never fight over each other's minions.
        private static readonly Dictionary<WeaponBase, List<Unit>> _live =
            new Dictionary<WeaponBase, List<Unit>>();

        public static void Track(WeaponBase weapon, Unit minion)
        {
            if (weapon == null || minion == null)
                return;

            List<Unit> list;

            if (!_live.TryGetValue(weapon, out list))
            {
                list = new List<Unit>();
                _live[weapon] = list;
            }

            list.Add(minion);
        }

        public static int Count(WeaponBase weapon)
        {
            List<Unit> list;

            if (weapon == null || !_live.TryGetValue(weapon, out list))
                return 0;

            Prune(list);
            return list.Count;
        }

        private static void Prune(List<Unit> list)
        {
            for (int i = list.Count - 1; i >= 0; i--)
            {
                Unit u = list[i];

                // Unity reports a destroyed object as null, so this catches
                // anything that died for any reason.
                if (u == null)
                {
                    list.RemoveAt(i);
                    continue;
                }

                // IsDead lives on DamagableResource, not on HealthBase - so a
                // unit whose health component is a plainer HealthBase is judged
                // alive until Unity actually destroys it, which is fine.
                var dr = u.GetComponent<DamagableResource>();

                if (dr == null)
                    dr = u.GetComponentInChildren<DamagableResource>();

                if (dr != null && dr.IsDead)
                    list.RemoveAt(i);
            }
        }

        // The cap for this weapon right now, including any BoosterCore effect.
        //
        // Boost scaling is OPT-IN (capPerLevel defaults to 0) because a weapon's
        // level normally drives FloatSeries stats, and a spawner has none - so
        // levelling it would otherwise do nothing at all and quietly waste a
        // BoosterCore. With capPerLevel set, the builder also switches
        // canBeBoosted on, since without that the level can never move.
        public static int CapFor(WeaponBase weapon, ForgeMinion.Config cfg)
        {
            int cap = Mathf.Max(0, cfg.cap);

            if (cap <= 0 || cfg.capPerLevel <= 0)
                return cap;

            int level = LevelOf(weapon);

            // Level 1 is the baseline, so a level-1 module gets exactly `cap`.
            return cap + cfg.capPerLevel * Mathf.Max(0, level - 1);
        }

        // Also used for the minion's per-level stats, so a BoosterCore on
        // the spawner reaches the creatures it makes.
        internal static int LevelOf(WeaponBase weapon)
        {
            if (weapon == null || weapon.Owner == null)
                return 1;

            Module main = MainModuleFor(weapon);

            return (main != null) ? Mathf.Max(1, main.Level) : 1;
        }

        // Find the cluster this weapon was built from, by matching the weapon
        // asset on its main module. Needed for three things: the module's level,
        // the connection type, and the module list for `inheritGrid`.
        public static IModuleCluster ClusterFor(WeaponBase weapon)
        {
            if (weapon == null || weapon.Owner == null)
                return null;

            var gridOwner = weapon.Owner.GetComponent<ModuleGridOwner>();

            if (gridOwner == null || gridOwner.ModuleGrid == null)
                return null;

            foreach (IModuleCluster cluster in gridOwner.ModuleGrid.GetAllClusters())
            {
                if (cluster == null || !cluster.HasMainModule)
                    continue;

                var wm = cluster.MainModule as WeaponModule;

                if (wm == null)
                    continue;

                var data = wm.Data as WeaponModuleData;

                if (data != null &&
                    ReferenceEquals(data.weapon, weapon.TemplateData))
                {
                    return cluster;
                }
            }

            return null;
        }

        private static Module MainModuleFor(WeaponBase weapon)
        {
            IModuleCluster c = ClusterFor(weapon);
            return (c != null) ? c.MainModule : null;
        }

        // Which slot the minions belong to.
        //
        // This matters more than it looks: `SpawnMinionModule.OnUpdate` trims
        // minions whose ConnectionToOwner matches ITS slot, so tagging swarm
        // minions with an Active slot would let a drone gadget in that slot
        // destroy them. **Undefined is the safe fallback** - no gadget ever uses
        // it, and the game's own Queen tags her minions the same way.
        public static Unit.OwnerConnectionType ConnectionFor(WeaponBase weapon)
        {
            IModuleCluster c = ClusterFor(weapon);

            if (c == null)
                return Unit.OwnerConnectionType.Undefined;

            switch (c.ClusterType)
            {
                case ClusterType.PrimaryWeapon:
                    return Unit.OwnerConnectionType.PrimaryWeapon;
                case ClusterType.SecondaryWeapon:
                    return Unit.OwnerConnectionType.SecondaryWeapon;
                default:
                    return Unit.OwnerConnectionType.Undefined;
            }
        }

        // ------------------------------------------------------------------
        // The cap, applied
        // ------------------------------------------------------------------

        // True if the weapon should be prevented from firing at all. Only the
        // Block mode does this, and it is checked in Shooter.Shoot so the
        // resource is never taken - paying for a shot that cannot spawn anything
        // is the kind of thing that reads as a bug.
        public static bool Blocked(WeaponBase weapon, ForgeMinion.Config cfg)
        {
            if (cfg == null || cfg.capMode != CapBlock)
                return false;

            int cap = CapFor(weapon, cfg);

            return cap > 0 && Count(weapon) >= cap;
        }

        // Called just before a minion is created. Returns false to suppress the
        // spawn entirely (Fizzle).
        public static bool AllowSpawn(WeaponBase weapon, ForgeMinion.Config cfg)
        {
            if (cfg == null || cfg.capMode == CapNone)
                return true;

            int cap = CapFor(weapon, cfg);

            if (cap <= 0 || Count(weapon) < cap)
                return true;

            if (cfg.capMode == CapKillOldest)
            {
                KillOldest(weapon);
                return true;
            }

            // Block is handled at the Shooter, but a gadget-slot spawner weapon
            // bypasses Shooter entirely - so treat it as a fizzle there rather
            // than letting the cap be ignored.
            return false;
        }

        private static void KillOldest(WeaponBase weapon)
        {
            List<Unit> list;

            if (!_live.TryGetValue(weapon, out list))
                return;

            Prune(list);

            if (list.Count == 0)
                return;

            Unit oldest = list[0];
            list.RemoveAt(0);

            if (oldest == null)
                return;

            // Die() rather than Destroy, matching the game's own trim: it runs
            // death effects and drops, and leaves the owner's minion list
            // consistent instead of holding a destroyed reference.
            var hb = oldest.GetComponent<HealthBase>();

            if (hb == null)
                hb = oldest.GetComponentInChildren<HealthBase>();

            if (hb != null)
                hb.Die();
            else
                UnityEngine.Object.Destroy(oldest.gameObject);
        }

        // ------------------------------------------------------------------
        // Grid inheritance - what the GADGET path does, replicated
        // ------------------------------------------------------------------
        public static void InheritGrid(WeaponBase weapon, Unit minion)
        {
            IModuleCluster cluster = ClusterFor(weapon);

            if (cluster == null || minion == null)
                return;

            var gridOwner = minion.GetComponent<ModuleGridOwner>();

            if (gridOwner == null)
                gridOwner = minion.GetComponentInChildren<ModuleGridOwner>();

            if (gridOwner == null)
                return;

            var simple = gridOwner.ModuleGrid as SimpleModuleGrid;

            if (simple == null)
                return;

            Module main = cluster.MainModule;

            foreach (Module module in cluster.ConnectedAndPoweredModules)
            {
                if (module == null || ReferenceEquals(module, main))
                    continue;

                // DeepCopy: a Module carries live per-instance state (its Level,
                // its rolled PowerLevel), so handing every minion the same
                // object would make them share it.
                Module copy = module.DeepCopy();

                if (copy == null)
                    continue;

                simple.InstallAugmentation(
                    copy,
                    BelongsToWeapon(copy)
                        ? ClusterType.PrimaryWeapon
                        : ClusterType.Passive);
            }

            minion.ComponentData.RecalculateStats(gridOwner.ModuleGrid);
            minion.ComponentData.RefillResources();
        }

        private static bool BelongsToWeapon(Module module)
        {
            foreach (ModuleEffect effect in module.Effects)
                if (effect is IWeaponModifier)
                    return true;

            return false;
        }

        // Housekeeping: a weapon is rebuilt on every cluster refresh, so old
        // entries would otherwise accumulate for the whole run.
        public static void Forget(WeaponBase weapon)
        {
            if (weapon != null)
                _live.Remove(weapon);
        }

        public static void Sweep()
        {
            if (_live.Count < 32)
                return;

            var dead = new List<WeaponBase>();

            foreach (var kv in _live)
            {
                Prune(kv.Value);

                if (kv.Value.Count == 0)
                    dead.Add(kv.Key);
            }

            for (int i = 0; i < dead.Count; i++)
                _live.Remove(dead[i]);
        }
    }
}
