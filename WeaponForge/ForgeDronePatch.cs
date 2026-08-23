using System;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace WeaponForge
{
    // Makes `minion.weapon` / `minion.faction` / `minion.lifetime` work on the
    // GADGET path.
    //
    // THE PROBLEM: `SpawnMinionModule.Activate` has no OnPrepareMinion event -
    // that belongs to MinionSpawnerWeapon - and the unit it creates is a **local
    // variable**, so a plain postfix on Activate cannot reach it.
    //
    // THE HOOK: `Unit.SetOwner(SavableEntity, OwnerConnectionType)` is public,
    // is called on the freshly created minion, and is handed the connection type
    // - which is the slot it came from. That is enough to work backwards to the
    // module responsible, and every step of that walk is public: the owner's
    // ModuleGridOwner.ModuleGrid, GetAllClusters(), IModuleCluster.ClusterType,
    // IModuleCluster.MainModule and Module.Data. No reflection at all.
    //
    // THE TIMING IS A GIFT. Activate calls SetOwner BEFORE it installs the
    // cluster's other modules as augmentations and before RecalculateStats. So
    // setting the minion's MainModule here means the game then layers its own
    // augmentations on top of OUR weapon and recalculates once - which is
    // exactly the order you would want and none of it had to be arranged.
    public static class ForgeDronePatch
    {
        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("WeaponForge.Drone");

        [HarmonyPatch(typeof(Unit), "SetOwner")]
        public class OnSetOwner
        {
            static void Postfix(
                Unit __instance,
                SavableEntity entity,
                Unit.OwnerConnectionType connectionType)
            {
                if (!ForgeDrone.Any || __instance == null || entity == null)
                    return;

                try
                {
                    Apply(__instance, entity, connectionType);
                }
                catch (Exception e)
                {
                    Log.LogError("Preparing a gadget drone failed: " + e);
                }
            }
        }

        private static void Apply(
            Unit minion,
            SavableEntity ownerEntity,
            Unit.OwnerConnectionType connectionType)
        {
            Unit owner = ownerEntity.GetComponent<Unit>();

            if (owner == null)
                return;

            var gridOwner = owner.GetComponent<ModuleGridOwner>();

            if (gridOwner == null || gridOwner.ModuleGrid == null)
                return;

            SpawnMinionModule spawner = null;

            foreach (IModuleCluster cluster in
                     gridOwner.ModuleGrid.GetAllClusters())
            {
                if (cluster == null || !cluster.HasMainModule)
                    continue;

                if (Connection(cluster.ClusterType) != connectionType)
                    continue;

                spawner = cluster.MainModule as SpawnMinionModule;
                break;
            }

            // Not a drone gadget. A minion from a spawner WEAPON also passes
            // through here, and its cluster's main module is a WeaponModule -
            // that path is handled by ForgeMinionPatch's own event hook, so
            // there is nothing to do and nothing double-applied.
            if (spawner == null)
                return;

            ForgeMinion.Config cfg;

            if (!ForgeDrone.TryGet(spawner.Data, out cfg))
                return;

            // Side FIRST, then the weapon - the same order the spawner-weapon
            // path uses, so a friendly-fire check inside weapon setup sees the
            // final faction rather than the prefab's.
            ForgeMinion.SetSide(minion, owner, cfg);
            ForgeMinion.Equip(minion, cfg);
            ForgeMinion.EquipDefault(minion, cfg);

            if (cfg.lifetime > 0f)
                ForgeMinionLife.Attach(minion, cfg.lifetime);

            ForgeMinionLook.Apply(minion, cfg);

            // Stats last. Nothing after this point needs repeating: health and
            // resources live in the module effects now, so the game's own
            // recalculation at the end of Activate - and the deferred one on the
            // minion's next Update - reproduce our numbers instead of erasing
            // them. That is the whole reason the effect route was chosen over
            // writing the tanks.
            ForgeMinionStats.Apply(minion, owner, cfg, spawner.Level);
            ForgeMinionBlast.Attach(minion, owner, cfg);
        }

        // The same mapping SpawnMinionModule.GetConnectionType uses. Mirrored
        // rather than called because that method is private, and it is four
        // lines. Shared with ForgeMinionRestore, which walks the same
        // slot-to-cluster relationship in the other direction after a load.
        internal static Unit.OwnerConnectionType Connection(ClusterType type)
        {
            switch (type)
            {
                case ClusterType.PrimaryWeapon:
                    return Unit.OwnerConnectionType.PrimaryWeapon;
                case ClusterType.SecondaryWeapon:
                    return Unit.OwnerConnectionType.SecondaryWeapon;
                case ClusterType.Active1:
                    return Unit.OwnerConnectionType.Active1;
                case ClusterType.Active2:
                    return Unit.OwnerConnectionType.Active2;
                case ClusterType.Active3:
                    return Unit.OwnerConnectionType.Active3;
                default:
                    return Unit.OwnerConnectionType.Undefined;
            }
        }
    }

    // "Minions that expire after a certain amount of time" - a swarm you throw
    // out rather than a pet you keep.
    //
    // Kills through `HealthBase.Die()` rather than Destroy, deliberately: Die
    // is what the game's own cap enforcer uses (SpawnMinionModule.DestroyUnit
    // calls exactly this), so death effects, drops, the minion list and
    // anything else listening all behave as though it died normally. A raw
    // Destroy would leave the owner's minion list holding a dead reference.
    public class ForgeMinionLife : MonoBehaviour
    {
        private float diesAt;
        private bool done;

        public static void Attach(Unit minion, float seconds)
        {
            if (minion == null || seconds <= 0f)
                return;

            var life = minion.gameObject.GetComponent<ForgeMinionLife>();

            if (life == null)
                life = minion.gameObject.AddComponent<ForgeMinionLife>();

            life.diesAt = Time.time + seconds;
            life.done = false;
        }

        private void Update()
        {
            if (done || Time.time < diesAt)
                return;

            done = true;

            var hb = GetComponent<HealthBase>();

            if (hb == null)
                hb = GetComponentInChildren<HealthBase>();

            if (hb != null)
                hb.Die();
            else
                Destroy(gameObject);
        }
    }
}
