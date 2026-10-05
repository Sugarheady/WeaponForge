using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
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
            ForgeLog.Source("WeaponForge.Swarm");

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

        // Stamp the Shooter's fire-rate clock as though a shot had happened.
        //
        // For a pull the cap REFUSED. `lastShootTime` is private and there is
        // no setter, but it is the only thing `FireRatePassed` reads - and
        // because a skipping prefix never reaches the line that stamps it, a
        // blocked weapon is asked to fire again every single frame. See the
        // long note at ForgeMinionPatch.OnShoot for what that did.
        //
        // Reflection is resolved once and cached. If the field ever moves this
        // degrades to the old behaviour rather than throwing on every frame -
        // and says so once, because a silent fallback on a timing path is
        // exactly the shape that costs a test round.
        private static FieldInfo _lastShootTime;
        private static bool _lookedForClock;
        private static bool _warnedNoClock;

        private static bool _warnedClockWrite;

        private static FieldInfo Clock()
        {
            if (!_lookedForClock)
            {
                _lookedForClock = true;
                _lastShootTime = AccessTools.Field(
                    typeof(Shooter), "lastShootTime");
            }

            if (_lastShootTime == null && !_warnedNoClock)
            {
                _warnedNoClock = true;
                Log.LogWarning(
                    "Shooter.lastShootTime was not found, so a swarm held at " +
                    "its cap will retry every frame and can spawn faster " +
                    "than its \"fireRate\" the moment a slot frees up. " +
                    "Harmless but not what the build log promises.");
            }

            return _lastShootTime;
        }

        // What the fire-rate clock reads right now, or null if unreadable.
        //
        // Needed as well as the write, because "did the shot actually consume
        // its interval?" is the only question that separates a spawner firing
        // too fast from a spawner being ASKED to fire too fast, and every
        // instrument before this one measured the second and reported the
        // first.
        public static float? ReadClock(Shooter shooter)
        {
            FieldInfo f = Clock();

            if (shooter == null || f == null)
                return null;

            try
            {
                return (float)f.GetValue(shooter);
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static void ConsumePull(Shooter shooter)
        {
            FieldInfo f = Clock();

            if (shooter == null || f == null)
                return;

            try
            {
                f.SetValue(shooter, Time.time);
            }
            catch (Exception e)
            {
                // NOT A SILENT CATCH ANY MORE, and the irony was sitting four
                // lines above it: the comment on this method already said "a
                // silent fallback on a timing path is exactly the shape that
                // costs a test round", and then swallowed one.
                //
                // If this ever throws, the swarm silently reverts to the
                // per-frame retry this whole method exists to prevent - which
                // is R14 test 170's symptom exactly, and was one of the two
                // candidates I could not rule out by reading the code.
                if (!_warnedClockWrite)
                {
                    _warnedClockWrite = true;
                    Log.LogWarning(
                        "Could not write Shooter.lastShootTime (" + e.Message +
                        "), so a spawner weapon will be asked to fire again " +
                        "on the very next frame instead of after its fire " +
                        "interval. THIS IS THE CAUSE if minions come out in " +
                        "a clump - quote this line.");
                }
            }
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
            InheritGrid(weapon, minion, true);
        }

        // `recalculate: false` when the caller is going to recalculate anyway.
        // The spawn path used to do it here AND again in the stats pass a
        // moment later, throwing the first result away - see
        // ForgeMinionStats.Recalculate.
        public static void InheritGrid(
            WeaponBase weapon, Unit minion, bool recalculate)
        {
            InheritGrid(ClusterFor(weapon), minion, recalculate);
        }

        // THE CLUSTER-TAKING OVERLOAD EXISTS FOR THE WINGMAN DRONE BODY, which
        // has no WeaponBase to derive one from - a wingman gadget's companions
        // are placed by a controller that already holds the cluster the gadget
        // sits in. Splitting it here rather than copying twenty lines keeps one
        // owner of "install this grid's augmentations into that unit", which is
        // the rule `SpawnMinionModule.Activate` sets and both minion paths
        // already follow.
        public static void InheritGrid(
            IModuleCluster cluster, Unit minion, bool recalculate)
        {
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

            if (!recalculate)
                return;

            minion.ComponentData.RecalculateStats(gridOwner.ModuleGrid);
            minion.ComponentData.RefillResources();
        }

        // ------------------------------------------------------------------
        // Keeping an ALREADY-SPAWNED body's grid in step with the gadget's
        // ------------------------------------------------------------------

        // ★ INHERIT IS A BIRTH EVENT; SYNC IS A STANDING PROMISE, and the two
        // need different code because installing twice STACKS.
        //
        // `InheritGrid` is right exactly once per body. A wingman drone body
        // outlives the grid it was born from - you pick up a BoosterCore, you
        // slot armour beside the gadget - and re-running Inherit would hand the
        // body a second copy of everything, then a third. So the companions
        // kept whatever they were born with, which is the same shape as R16's
        // *"I have to unequip and re-equip to get the stat upgrade"* one object
        // along: the weapon copies rebuild on a generation counter and the
        // BODIES had no equivalent.
        //
        // A SYNC is idempotent by construction: strip every augmentation we put
        // there and install the current set. That is also why it can be used on
        // a body handed back by a LOAD, where a plain Inherit could not - the
        // game restores the grid from a memento, so the modules are already
        // there and the only safe operation is one that does not care whether
        // they are.
        //
        // ⚠ THE REMOVAL HAS TO CALL `OnUninstalled` BY HAND. `SimpleModuleGrid`
        // has `InstallAugmentation` and NO uninstall counterpart - the only
        // thing that fires its `ModuleUninstalled` event is
        // `UninstallMainModule` - while `SimpleModuleCluster.RemoveModule` is
        // public and fires nothing. So a raw remove drops the module out of the
        // grid with its `OnInstalled` registrations still live, which for a
        // Module Forge card is a real leak. `ModuleGridOwner.Data
        // .OnModuleUninstalled` is one line (`module.OnUninstalled(unit)`) and
        // this does the same thing, so install and removal stay symmetrical.
        //
        // > **A missing API is not a missing capability.** The uninstall path
        // > exists one class down; what is missing is the grid-level wrapper
        // > that would have fired the event, and that is ours to supply.
        public static void SyncGrid(
            IModuleCluster cluster, Unit minion, bool recalculate)
        {
            if (cluster == null || minion == null)
                return;

            SimpleModuleGrid simple = GridOf(minion);

            if (simple == null)
                return;

            Strip(simple, minion);
            InheritGrid(cluster, minion, recalculate);
        }

        // Every augmentation, in every cluster - never a MainModule.
        //
        // Sweeping ALL clusters rather than the two `InheritGrid` writes into
        // is deliberate: a body restored from a memento had its grid rebuilt by
        // `SimpleModuleGrid.RestoreFromMemento`, which is the game's code and
        // not ours, so "where would we have put it" is the wrong question to
        // ask of a grid we did not fill in. The MainModule check is what keeps
        // the body's OWN module - the one carrying every stat edit
        // `ForgeMinionStats` made - and it is exact rather than positional:
        // `SimpleModuleCluster`'s MainModule setter inserts it at index 0 of
        // the same list `ConnectedAndPoweredModules` returns, so it is in
        // there, and identity is the only honest way to tell it apart.
        private static void Strip(SimpleModuleGrid grid, Unit minion)
        {
            Unit.Data data = minion.ComponentData;

            foreach (IModuleCluster c in grid.GetAllClusters())
            {
                var simple = c as SimpleModuleCluster;

                if (simple == null)
                    continue;

                Module main = simple.HasMainModule ? simple.MainModule : null;

                // Copied out first: RemoveModule mutates the list this is
                // walking.
                var doomed = new List<Module>();

                foreach (Module m in simple.ConnectedAndPoweredModules)
                    if (m != null && !ReferenceEquals(m, main))
                        doomed.Add(m);

                for (int i = 0; i < doomed.Count; i++)
                {
                    // What the grid would have done, had it had a door for it.
                    try
                    {
                        if (data != null)
                            doomed[i].OnUninstalled(data);
                    }
                    catch (Exception e)
                    {
                        Log.LogWarning(
                            "minion grid: a module objected to being removed " +
                            "from a companion (" + e.Message + "). Removing " +
                            "it anyway - leaving it installed would be worse.");
                    }

                    simple.RemoveModule(doomed[i]);
                }
            }
        }

        private static SimpleModuleGrid GridOf(Unit minion)
        {
            var gridOwner = minion.GetComponent<ModuleGridOwner>();

            if (gridOwner == null)
                gridOwner = minion.GetComponentInChildren<ModuleGridOwner>();

            return (gridOwner != null)
                ? gridOwner.ModuleGrid as SimpleModuleGrid
                : null;
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
