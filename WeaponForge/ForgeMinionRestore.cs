using System;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace WeaponForge
{
    // Put a minion back the way you built it after a save is reloaded.
    //
    // THE PROBLEM. The save system stores an entity as a prefab id plus a list
    // of component mementos, and rebuilds it with `savablePrefab.CreateData()`
    // followed by a memento replay. Only what a memento carries survives. The
    // grid does have one - so the minion's WEAPON comes back, and so does its
    // owner - but faction, colour, scale, movement, vision, resource capacities
    // and its remaining lifetime are all just prefab values again.
    //
    // Which means every minion feature this mod has ever shipped quietly
    // reverted on load, colour and scale included. It presents as "my purple
    // drones went orange after I reloaded", with nothing in the log.
    //
    // THE HOOK. `SavableEntity.Bind(EntityData, bool isFirstTime)` runs whenever
    // a GameObject is attached to entity data, and `isFirstTime` is false
    // exactly when the data outlived the GameObject - a reload, or the segment
    // streaming back in. At that moment every component exists and the
    // mementos have already been replayed, so the owner link is readable. It is
    // the one place that is true.
    //
    // WHY THE CONFIG CAN BE FOUND AGAIN WITHOUT STORING ANYTHING. A minion
    // remembers two things across a load: who owns it, and which slot it came
    // from. That is enough - walk the owner's grid to the cluster matching that
    // slot, and its main module IS the spawner that made this minion. It works
    // identically for a gadget drone (a SpawnMinionModule) and a swarm gun (a
    // WeaponModule whose weapon we registered), so one walk serves both paths
    // and nothing has to be written into the save.
    public static class ForgeMinionRestore
    {
        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("WeaponForge.Minion");

        [HarmonyPatch(typeof(SavableEntity), "Bind")]
        public class OnBind
        {
            static void Postfix(
                SavableEntity __instance, EntityData entityData, bool isFirstTime)
            {
                // A first bind is an ordinary spawn. The spawn hooks own that
                // case, and they run later - re-applying here would double
                // every multiplier.
                if (isFirstTime || __instance == null || entityData == null)
                    return;

                if (!ForgeMinion.Any && !ForgeDrone.Any)
                    return;

                try
                {
                    Restore(__instance);
                }
                catch (Exception e)
                {
                    Log.LogError("Restoring a minion after a load failed: " + e);
                }
            }
        }

        private static void Restore(SavableEntity entity)
        {
            var minion = entity.GetComponent<Unit>();

            if (minion == null)
                minion = entity.GetComponentInChildren<Unit>();

            if (minion == null || minion.Owner == null)
                return;

            ForgeMinion.Config cfg;
            int level;

            if (!Origin(minion, out cfg, out level) || cfg == null)
                return;

            Unit owner = OwnerUnit(minion);

            // Faction is not in any memento, so a minion that was yours comes
            // back on the prefab's side - and four of the spawnables the
            // builder offers are genuinely hostile. This is the one that would
            // turn your own swarm against you after a reload.
            ForgeMinion.SetSide(minion, owner, cfg);

            ForgeMinionLook.Apply(minion, cfg);
            ForgeMinionStats.Apply(minion, owner, cfg, level);

            // The clock restarts rather than resuming. Storing the remaining
            // time would need a memento of our own, and a minion that survives
            // a reload with its full lifetime is the friendlier of the two
            // wrong answers.
            if (cfg.lifetime > 0f)
                ForgeMinionLife.Attach(minion, cfg.lifetime);

            ForgeMinionBlast.Attach(minion, owner, cfg);

            Log.LogInfo(
                "Restored '" + minion.name + "' after a load - side, colour, " +
                "scale and stats re-applied (its lifetime clock restarts).");
        }

        // Walk owner -> the slot this minion came from -> the module that made
        // it. Done entirely in DATA space, because the owner's GameObject may
        // not be loaded while the minion's is.
        public static bool Origin(
            Unit minion, out ForgeMinion.Config cfg, out int spawnerLevel)
        {
            cfg = null;
            spawnerLevel = 1;

            if (minion == null || minion.ComponentData == null)
                return false;

            EntityData ownerEntity = minion.Owner;

            if (ownerEntity == null)
                return false;

            ModuleGridOwner.Data gridData;

            if (!ownerEntity.TryGetComponent<ModuleGridOwner.Data>(out gridData))
                return false;

            IModuleGrid grid = gridData.ModuleGrid;

            if (grid == null)
                return false;

            Unit.OwnerConnectionType want =
                minion.ComponentData.ConnectionToOwner;

            foreach (IModuleCluster cluster in grid.GetAllClusters())
            {
                if (cluster == null || !cluster.HasMainModule ||
                    cluster.MainModule == null)
                {
                    continue;
                }

                if (ForgeDronePatch.Connection(cluster.ClusterType) != want)
                    continue;

                // A gadget drone.
                var spawner = cluster.MainModule as SpawnMinionModule;

                if (spawner != null &&
                    ForgeDrone.TryGet(spawner.Data, out cfg))
                {
                    spawnerLevel = Mathf.Max(1, spawner.Level);
                    return true;
                }

                // A swarm gun.
                var weapon = cluster.MainModule as WeaponModule;

                if (weapon != null && weapon.WeaponData != null &&
                    ForgeMinion.TryGet(weapon.WeaponData, out cfg))
                {
                    spawnerLevel = Mathf.Max(1, weapon.Level);
                    return true;
                }
            }

            return false;
        }

        // The owner's Unit, if its GameObject happens to be loaded. Only needed
        // for `"faction": "owner"`, which reads the summoner's own side rather
        // than naming one - so a null here is survivable, not an error.
        public static Unit OwnerUnit(Unit minion)
        {
            if (minion == null || minion.Owner == null)
                return null;

            try
            {
                EntityGameObjectManager manager;

                if (!ServiceLocator.TryGet<EntityGameObjectManager>(out manager))
                    return null;

                SavableEntity found;

                if (!manager.TryGetSavableEntity(
                        minion.Owner.instanceId, out found) || found == null)
                {
                    return null;
                }

                Unit unit = found.GetComponent<Unit>();

                return unit != null
                    ? unit
                    : found.GetComponentInChildren<Unit>();
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
