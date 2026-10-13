using System;
using System.Collections.Generic;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace WeaponForge
{
    // The hooks that wire `wingman` to the game.
    //
    //   OnContainingClusterRefreshed  remember which cluster a gadget is in,
    //                                 so its wingmen inherit the modules
    //                                 slotted around it
    //   Activate                      the gadget key: deploy / toggle / burst /
    //                                 recall, INSTEAD of the gadget firing
    //   OnUninstalled                 tear the clones AND the companions down
    //   WeaponBase.DoShoot            the mirror - the whole point
    //   ModuleCluster.RefreshPowered  any grid change, anywhere, invalidates
    //                                 the built copies
    //   MinionsWidget.UpdateWidget    the "2/4" readout on the HUD
    //
    // THE RECURSION GUARD IS LOAD-BEARING, not defensive. A wingman fires by
    // calling `weapon.Fire`, which reaches `DoShoot`, which is the same hook
    // that decides to mirror a shot. Without the frame-scoped flag below, one
    // trigger pull would mirror itself forever inside a single frame. The
    // design document lists this under risks; it is the first thing that would
    // have happened in play.
    public static class ForgeWingmanPatch
    {
        private static readonly ManualLogSource Log =
            ForgeLog.Source("WeaponForge.Wingman");

        // Which cluster each equipped wingman gadget sits in. The game hands
        // this to the module and nowhere else, so it has to be caught here.
        private static readonly Dictionary<object, IModuleCluster> _clusters =
            new Dictionary<object, IModuleCluster>();

        // True while wingman shots are being fired. See the class note.
        private static bool _mirroring;

        private static bool _saidActive;

        // Bumped whenever ANY cluster on ANY grid recalculates. A controller
        // caches the number it last built its weapon instances against and
        // rebuilds them when it changes.
        //
        // This is a counter rather than an event because the thing that needs
        // to know is not the cluster that changed. A module dropped beside your
        // PRIMARY has to rebuild the copies a GADGET's wingmen fire, and those
        // two clusters have no relationship the game exposes. Refreshes happen
        // when the grid is edited, not per frame, so an int compared once per
        // volley is the whole cost.
        public static int Generation { get; private set; }

        // "Something the copies were built from has changed" from outside a
        // cluster refresh - a ranked gun's weapon gaining an unlock.
        public static void Invalidate()
        {
            Generation++;
        }

        // ------------------------------------------------------------------

        // The cluster whose main module IS this gadget, found by walking the
        // grid. Null when it cannot be found, which callers treat as "slot not
        // known yet".
        private static IModuleCluster ClusterOf(Unit owner, object module)
        {
            try
            {
                var gridOwner = owner.GetComponentInChildren<ModuleGridOwner>(true);

                if (gridOwner == null || gridOwner.ModuleGrid == null)
                    return null;

                foreach (IModuleCluster c in gridOwner.ModuleGrid.GetAllClusters())
                {
                    if (c != null && c.HasMainModule &&
                        ReferenceEquals(c.MainModule, module))
                    {
                        return c;
                    }
                }
            }
            catch (Exception)
            {
            }

            return null;
        }

        private static ForgeWingman.Config ConfigOf(object module)
        {
            var m = module as WeaponBasedActiveModule;

            if (m == null)
                return null;

            ForgeWingman.Config cfg;

            return ForgeWingman.TryGet(m.WeaponData, out cfg) ? cfg : null;
        }

        [HarmonyPatch(typeof(WeaponBasedActiveModule), "OnContainingClusterRefreshed")]
        public class OnCluster
        {
            static void Postfix(
                WeaponBasedActiveModule __instance, IModuleCluster cluster)
            {
                if (ConfigOf(__instance) == null)
                    return;

                _clusters[__instance] = cluster;
            }
        }

        // ANY cluster refreshing means the grid has been edited, so every built
        // copy is now built against a stale module list.
        [HarmonyPatch(typeof(ModuleCluster), "RefreshPoweredModules")]
        public class OnClusterPowered
        {
            static void Postfix()
            {
                Generation++;
            }
        }

        [HarmonyPatch(typeof(WeaponBasedActiveModule), "OnUninstalled")]
        public class OnUninstall
        {
            static void Postfix(WeaponBasedActiveModule __instance)
            {
                ForgeWingman.Config cfg = ConfigOf(__instance);

                if (cfg == null)
                    return;

                IModuleCluster cluster;
                _clusters.TryGetValue(__instance, out cluster);
                _clusters.Remove(__instance);

                // TAKE THE COMPANIONS WITH IT. R16: *"when i unequipe the
                // wingman gadget the orbs are still present and still able to
                // shoot ... otherwise they could just stack the effects
                // elsewhere and use a different gadget for free pretty much."*
                //
                // Releasing the weapon clones (below) was never enough: the
                // bodies live on a MonoBehaviour attached to the ship, and
                // nothing here pointed at it. This hook is handed a `Unit.Data`
                // rather than a `Unit`, so the controller keeps a static list
                // of itself and is found by config instead - and by SLOT, since
                // R19 267: two copies of one file are two gadgets now.
                ForgeWingmanController.Remove(cfg, __instance, cluster);

                // The weapon clones are cached per CONFIG and shared by every
                // slot running it, so they go only with the LAST copy. Released
                // while another copy still flies, that gadget's companions
                // would be firing a destroyed asset.
                if (!ForgeWingmanController.AnyFor(cfg))
                    ForgeWingmanWeapon.Release(cfg);
            }
        }

        // The gadget key. Returns FALSE so the gadget does not fire its own
        // placeholder weapon at the cursor - a wingman gadget has no shot of
        // its own, only the copies its wingmen fire.
        //
        // The cooldown is NOT ours to enforce: `ModuleActivator.Update` checks
        // `ActiveModule.Cooldown` BEFORE calling this and stamps its own clock
        // after, so a skipped Activate still consumes the cooldown. The
        // resource cost IS ours, because the code that would have charged it is
        // the code being skipped.
        [HarmonyPatch(typeof(WeaponBasedActiveModule), "Activate")]
        public class OnActivate
        {
            static bool Prefix(WeaponBasedActiveModule __instance, Unit owner)
            {
                ForgeWingman.Config cfg = ConfigOf(__instance);

                if (cfg == null || owner == null)
                    return true;

                try
                {
                    IModuleCluster cluster;
                    _clusters.TryGetValue(__instance, out cluster);

                    // Which slot was pressed decides which gadget answers. If
                    // the cluster callback has not been seen yet, find it: with
                    // two copies of one file installed, a press with no slot
                    // would land on whichever controller was made first.
                    if (cluster == null)
                        cluster = ClusterOf(owner, __instance);

                    var ctl = ForgeWingmanController.For(owner, cfg, cluster);

                    if (ctl == null)
                        return false;

                    // Ask before charging: a refused press (at the cap, on
                    // `refuse`) must cost nothing.
                    if (!ctl.OnKey())
                        return false;

                    Charge(__instance, owner);
                }
                catch (Exception e)
                {
                    Log.LogError("wingman: the gadget key failed: " + e);
                }

                return false;
            }

            private static void Charge(ActiveModule module, Unit owner)
            {
                Resource res = module.ResourceUsed;
                float cost = module.ActivationCost;

                if (res == null || cost <= 0f)
                    return;

                if (owner.GetResource(res) < cost)
                {
                    owner.TriggerResourceInsufficient(res, true);
                    return;
                }

                ResourceTank tank = owner.GetTank(res);

                if (tank != null)
                    tank.Value -= cost;
            }
        }

        // ------------------------------------------------------------------
        // The mirror
        // ------------------------------------------------------------------

        // `DoShoot` is the one method provably on the path of every shot - the
        // shoot SFX and the muzzle flash both fire from it - which is why seven
        // other features here hook it too.
        [HarmonyPatch(typeof(WeaponBase), "DoShoot")]
        public class OnDoShoot
        {
            static void Prefix(WeaponBase __instance)
            {
                // A COURIER DRONE FIRING is not a pull: the wingmen already
                // fired with the courier gun's launch (ForgeCourierDrone calls
                // Mirror then), and would otherwise fire again with every
                // shot the drone takes.
                if (ForgeCourier.Firing > 0)
                    return;

                Mirror(__instance);
            }
        }

        // One pull of `__instance`: every wingman mirroring it fires. Public
        // for the courier launch, whose pull never reaches DoShoot.
        public static void Mirror(WeaponBase __instance)
        {
            if (!ForgeWingman.Any || _mirroring || __instance == null)
                return;

            Unit owner = __instance.Owner;

            if (owner == null)
                return;

            try
            {
                Ensure(owner);

                var controllers =
                    owner.GetComponentsInChildren<ForgeWingmanController>(true);

                if (controllers == null || controllers.Length == 0)
                    return;

                _mirroring = true;

                try
                {
                    for (int i = 0; i < controllers.Length; i++)
                        if (controllers[i].Mirrors(__instance))
                            controllers[i].OnMirroredShot(__instance);
                }
                finally
                {
                    _mirroring = false;
                }
            }
            catch (Exception e)
            {
                _mirroring = false;
                Log.LogError("wingman: mirroring a shot failed: " + e);
            }
        }

        // Make sure every wingman gadget this unit carries has a controller,
        // even when its `key` is `none` and `Activate` therefore never runs.
        //
        // Walks the grid rather than being told, because there is no event that
        // says "this unit now has a gadget" with the live Unit attached - the
        // install callback carries `Unit.Data`, which is a different object.
        private static void Ensure(Unit owner)
        {
            var gridOwner = owner.GetComponentInChildren<ModuleGridOwner>(true);

            if (gridOwner == null || gridOwner.ModuleGrid == null)
                return;

            foreach (IModuleCluster cluster in gridOwner.ModuleGrid.GetAllClusters())
            {
                if (cluster == null || !cluster.HasMainModule)
                    continue;

                ForgeWingman.Config cfg = ConfigOf(cluster.MainModule);

                if (cfg == null)
                    continue;

                var ctl = ForgeWingmanController.For(owner, cfg, cluster);

                if (ctl == null)
                    continue;

                // BEFORE the auto-deploy below, and that ordering is the whole
                // of the restore. A gadget you had stowed when you saved must
                // not be deployed for you the moment you load, and a partial
                // complement must not be quietly topped up to full - both of
                // which is what `AutoDeploys` would do to a fresh controller
                // that had not been told what the save said yet.
                ForgeWingmanSave.Apply(owner, cfg, ctl);

                if (!_saidActive)
                {
                    _saidActive = true;

                    Log.LogInfo(
                        cfg.fileName + ": wingmen are live. " +
                        (cfg.AutoDeploys
                            ? "The full complement of " + ctl.Capacity +
                              " is deployed for you; the gadget key " +
                              (cfg.key == ForgeWingman.Key.Burst
                                  ? "fires them all at once"
                                  : cfg.key == ForgeWingman.Key.Recall
                                      ? "stows them (press again to send them " +
                                        "back out)"
                                      : "does nothing")
                            : cfg.key == ForgeWingman.Key.Toggle
                                ? "Press the gadget key to send the whole " +
                                  "complement of " + ctl.Capacity +
                                  " out, and again to stow them"
                                : "Press the gadget key to deploy one (each " +
                                  "press adds one, up to " + ctl.Capacity + ")") +
                        ". Said once per run.");
                }

                // `none`, `burst` and `recall` are never deployed by hand, so
                // deploy them here - otherwise the gadget would do nothing at
                // all. Until R16 only `none` did this, which is why `burst`
                // could not put a companion on screen: its handler fired a
                // volley from a complement that was never spawned.
                if (cfg.AutoDeploys && ctl.Deployed == 0)
                    ctl.OnKeyless();
            }
        }

        // ------------------------------------------------------------------
        // Putting a loaded save's companions back
        // ------------------------------------------------------------------
        //
        // `Ensure` is otherwise only reached from the DoShoot mirror, which
        // means "when you next fire". That is fine for a gadget that deploys
        // on a key press and useless for a restore: your companions would be
        // missing from the moment you loaded until the moment you pulled a
        // trigger, which reads as the feature not persisting at all.
        //
        // `ModuleGridOwner.Update` is the seam because it is a MonoBehaviour
        // Update on the ship itself, so it carries a LIVE Unit - the thing
        // `OnInstalled` and `OnUninstalled` cannot give us, since both are
        // handed a `Unit.Data`. It also runs for every unit in the world with
        // a grid, enemies included, which is why the gate is the first line
        // and is two static reads: once the record is empty (or nobody claimed
        // it within the patience window) this costs a branch.
        [HarmonyPatch(typeof(ModuleGridOwner), "Update")]
        public class OnGridOwnerUpdate
        {
            static void Postfix(ModuleGridOwner __instance)
            {
                if (!ForgeWingman.Any || __instance == null)
                    return;

                // ★ A DRONE-BODIED GADGET HAS NOTHING PENDING AND STILL NEEDS
                // THIS SEAM, which is the opposite of how the orb restore
                // works and nearly shipped as a hole.
                //
                // An orb complement is rebuilt from a sidecar COUNT, so
                // `HasPending` is exactly the right gate: no record, nothing to
                // do. A drone complement is restored by the GAME - the bodies
                // are real saved entities and come back by themselves - so
                // there is deliberately no record to find, and the gate above
                // would have returned before a controller was ever built. The
                // companions would have been on screen, owned, damageable, and
                // steered by nobody, with their own AI switched back on by the
                // reload. Nothing would have created the controller until the
                // next time the player fired.
                //
                // So a drone file sweeps for a fixed window after every run
                // entry instead. Same patience as the sidecar's, for the same
                // reason, and it costs two static reads a frame once it lapses.
                if (!ForgeWingmanSave.HasPending && !SweepingForBodies)
                    return;

                try
                {
                    Unit unit = __instance.GetComponentInParent<Unit>();

                    if (unit == null)
                        unit = __instance.GetComponentInChildren<Unit>(true);

                    if (unit != null)
                        Ensure(unit);
                }
                catch (Exception e)
                {
                    Log.LogError(
                        "wingman: restoring a saved complement failed: " + e);
                }
            }
        }

        // ------------------------------------------------------------------
        // The "2/4" readout
        // ------------------------------------------------------------------
        //
        // R16: *"i would like a count of how many orbs/drones are spawned out
        // of the total. The normal module active drones ... has that count."*
        //
        // It does, and this borrows it rather than drawing a second one. The
        // game already instantiates a `MinionCountWidget` for all five
        // connection types at `Start` and then hides the ones whose grid slot
        // does not hold a `SpawnMinionModule`. A wingman gadget is a
        // `WeaponBasedActiveModule`, so `UpdateWidget` reaches its final line
        // and switches the widget off - which is why his gadget had no count
        // while a stock drone in the same slot did.
        //
        // A postfix is the right shape here: the game has already decided
        // "nothing to show", and we are supplying the case it does not know
        // about. `SetIcon` takes a base `Module`, so it needs no coaxing.
        [HarmonyPatch(typeof(MinionsWidget), "UpdateWidget")]
        public class OnMinionWidget
        {
            static void Postfix(
                MinionsWidget __instance, Unit.OwnerConnectionType connectionType)
            {
                if (!ForgeWingman.Any)
                    return;

                try
                {
                    Show(__instance, connectionType);
                }
                catch
                {
                    // A HUD readout must never cost the gadget. Silent
                    // deliberately: this runs every frame for five slots, so
                    // one bad frame would otherwise fill the log.
                }
            }

            private static void Show(
                MinionsWidget widget, Unit.OwnerConnectionType type)
            {
                var grid = Traverse.Create(widget).Field("grid")
                    .GetValue<ModuleGrid>();

                if (grid == null)
                    return;

                Vector2Int at = PositionOf(type);
                Module module = grid.IsEmpty(at) ? null : grid[at];

                var gadget = module as WeaponBasedActiveModule;

                if (gadget == null)
                    return;

                ForgeWingman.Config cfg;

                if (!ForgeWingman.TryGet(gadget.WeaponData, out cfg))
                    return;

                var widgets = Traverse.Create(widget).Field("widgets")
                    .GetValue<Dictionary<Unit.OwnerConnectionType,
                        MinionCountWidget>>();

                MinionCountWidget row;

                if (widgets == null || !widgets.TryGetValue(type, out row) ||
                    row == null)
                {
                    return;
                }

                // The cap is read the way the controller reads it, so the two
                // numbers cannot disagree: a BoosterCore that raises the cap
                // raises the readout in the same frame.
                int level = Mathf.Max(1, gadget.Level);
                int cap = Mathf.Max(
                    0, cfg.count + (level - 1) * Mathf.Max(0, cfg.capPerLevel));

                int deployed = 0;
                bool refused = false;

                // THIS SLOT's controller, not every copy of the file. Summing
                // them was invisible with one copy and showed two gadgets'
                // companions as one count with two.
                ForgeWingmanController.EachInSlot(cfg, type, c =>
                {
                    deployed += c.Deployed;
                    cap = c.Capacity;

                    if (c.ConsumeRefusal())
                        refused = true;
                });

                row.gameObject.SetActive(true);
                row.SetIcon(gadget);
                row.UpdateText(deployed, cap);

                // The game's own "you are full" flash, for a `refuse` press at
                // the cap. Same feedback a drone gadget gives, for the same
                // event.
                if (refused)
                    row.PlayMaximumMinionsReached();
            }

            private static Vector2Int PositionOf(Unit.OwnerConnectionType type)
            {
                switch (type)
                {
                    case Unit.OwnerConnectionType.PrimaryWeapon:
                        return ModuleGrid.PrimaryWeaponGridPosition;

                    case Unit.OwnerConnectionType.SecondaryWeapon:
                        return ModuleGrid.SecondaryWeaponGridPosition;

                    case Unit.OwnerConnectionType.Active1:
                        return ModuleGrid.Active1GridPosition;

                    case Unit.OwnerConnectionType.Active2:
                        return ModuleGrid.Active2GridPosition;

                    default:
                        return ModuleGrid.Active3GridPosition;
                }
            }
        }

        // How long after a run entry a drone-bodied gadget keeps looking for
        // the ship. The ship's own `ModuleGridOwner.Update` starts the frame it
        // exists, so this is generous rather than tight - and the alternative
        // is a second timer owned by somewhere else.
        private const float BodySweepSeconds = 30f;

        private static float _sweepUntil = -1f;

        private static bool SweepingForBodies
        {
            get
            {
                return ForgeWingman.AnyDrone &&
                       _sweepUntil > 0f &&
                       Time.unscaledTime < _sweepUntil;
            }
        }

        // ---- the few seconds after a LOAD --------------------------------
        //
        // R19 test 268: an always-out ring came back with fresh companions
        // orbiting the hurt ones, and the hurt ones flying on the stock drone
        // AI, shooting on their own. The auto-deploy is the one thing here
        // that CREATES bodies during a load, so for a short window after one it
        // waits until the bodies the game restored for its slot are on screen
        // and adopted (ForgeWingmanController.WaitingForRestored). Bounded, so
        // a body that never appears cannot stop the gadget working.
        private const float LoadSettleSeconds = 8f;

        private static float _loadedAt = -1f;

        public static void NoteLoad()
        {
            _loadedAt = Time.unscaledTime;
        }

        public static bool LoadSettling
        {
            get
            {
                return _loadedAt > 0f &&
                       Time.unscaledTime - _loadedAt < LoadSettleSeconds;
            }
        }

        public static bool LoadedThisRun
        {
            get { return _loadedAt > 0f; }
        }

        public static void Reset()
        {
            // A new run is not a load; the Load POSTFIX sets it again after
            // this prefix has cleared it, on the continue path.
            _loadedAt = -1f;

            // Set here rather than on the load hook because this method is the
            // one thing BOTH run entries already go through - `RunData
            // .Initialize` for a new run and `GameSaver.Load` for a continue.
            // On the continue path it is a prefix, so the window opens before
            // the entities are restored, which is the order that works.
            _sweepUntil = ForgeWingman.AnyDrone
                ? Time.unscaledTime + BodySweepSeconds
                : -1f;

            _clusters.Clear();
            _mirroring = false;
            _saidActive = false;

            ForgeWingmanController.Reset();
            ForgeWingmanArt.Reset();
        }
    }
}
