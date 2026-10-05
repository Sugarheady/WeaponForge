using System;
using System.Collections.Generic;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace WeaponForge
{
    // The second XP source: finding a station.
    //
    // ★ HIS "SAVE/SHOP STATIONS" ARE CALLED `Instrument` IN THE CODE, which is
    // why they could not be found by searching for the obvious word - the same
    // vocabulary mismatch that hid the fuel-bubble sound behind `Cells/Fuel`.
    // There are two prefabs, and they really are called Station:
    // `Station.prefab` (`entityId: Station`) and `Station Scanner.prefab`
    // (`entityId: Station_Scanner`).
    //
    // ★ ONE HOOK COVERS BOTH, and every other interactable with it.
    // `Interactable.Activate(Interactor)` is a plain public method firing a
    // `UnityEvent<Interactor>` **wired in the prefab** - so it appears at no
    // call site in the decompile at all. Exactly seven prefabs carry the
    // component: the two stations, `Scanner`, `Object Instrument A` and the
    // three pickups. Same shape as the 83 prefabs wiring `HealthBase.onDamage`
    // to `SfxPlayer.Play`: **search the prefab YAML as well as the code.**
    //
    // ★ DISCOVERY PAYS ONCE PER STATION, EVER - *"only xp from that not from
    // constant use of them"*. Both station prefabs carry a `SavableEntity` with
    // `isUnloadable: 1` and `destroyWhenUnloaded: 0`, so each placed station
    // has a runtime `instanceId` that survives the object being unloaded. The
    // paid ids go in the rank sidecar, which already keys on that identity.
    //
    // The game solves the reload half by itself where `Instrument` is
    // concerned - `Instrument.Data` is memento-backed and a used one stays used
    // across save/load - but the ledger is kept anyway, because the hook covers
    // interactables that are not Instruments.
    public static class ForgeRankStation
    {
        private static readonly ManualLogSource Log =
            ForgeLog.Source("WeaponForge.Rank");

        // ★ THE STATION YOU START BESIDE PAYS NOTHING (his call, R20 close).
        // R20 256/285: *"when i start a new game and just move away from my
        // station, both weapons automatically get to rank 2"* - using its grid
        // was the first use of a station, so it paid every ranked gun. Station
        // XP is for stations you REACH.
        //
        // Which one it is: `GameController.StartGame` calls
        // `Station.PlayStartSequence` on it for a NEW run only (a continue
        // takes the other branch), so a prefix there names it exactly. The id
        // goes in the rank sidecar, so a continue still knows it.
        private static int _startStation;
        private static bool _saidStartSkip;

        public static int StartStation
        {
            get { return _startStation; }
            set { _startStation = value; }
        }

        public static void Reset()
        {
            _startStation = 0;
        }

        [HarmonyPatch(typeof(Station), "PlayStartSequence")]
        public class OnStartSequence
        {
            static void Prefix(Station __instance)
            {
                try
                {
                    var savable = (__instance != null)
                        ? __instance.GetComponentInParent<SavableEntity>()
                        : null;

                    if (savable != null && savable.EntityData != null)
                        _startStation = savable.EntityData.instanceId;
                }
                catch (Exception)
                {
                    // Not knowing it costs one station's XP, nothing else.
                }
            }
        }

        [HarmonyPatch(typeof(Interactable), "Activate")]
        public class OnActivate
        {
            static void Postfix(Interactable __instance, Interactor interactor)
            {
                if (!ForgeRank.Any)
                    return;

                try
                {
                    Pay(__instance, interactor);
                }
                catch (Exception e)
                {
                    Log.LogWarning("station XP skipped: " + e.Message);
                }
            }
        }

        private static void Pay(Interactable it, Interactor who)
        {
            if (it == null || who == null)
                return;

            Unit ship = who.GetComponentInParent<Unit>();

            if (ship == null)
                return;

            int id = Identify(it);

            if (id == 0)
                return;

            if (id == _startStation)
            {
                if (!_saidStartSkip)
                {
                    _saidStartSkip = true;

                    Log.LogInfo(
                        "rank: this is the station the run started at, so it " +
                        "pays no rank XP - station XP is for stations you fly " +
                        "to. Said once per run.");
                }

                return;
            }

            int shipId = ForgeWingmanSave.IdOf(ship);

            ForgeRank.Seen(shipId, ship);

            // EVERY RANKED GUN ON THE SHIP IS PAID, not just the one in hand.
            // Discovering a place is not something one of your two guns did,
            // and paying only the equipped one would make the reward depend on
            // which trigger you happened to be holding when you docked. Each
            // COPY keeps its own ledger, so a second copy of the same gun
            // found later still earns a station you had already found with
            // the first - it has not been there.
            foreach (ForgeRankCopies.Copy copy in Carried(ship))
            {
                ForgeRank.Config cfg = copy.cfg;

                if (cfg == null || !cfg.enabled || !cfg.stations ||
                    cfg.stationXp <= 0f)
                {
                    continue;
                }

                // ONCE PER STATION, EVER.
                if (!copy.p.paidStations.Add(id))
                    continue;

                copy.p.dirty = true;

                ForgeRankPatch.Award(ship, copy, cfg.stationXp);
            }
        }

        // The ranked copies this ship is carrying in its weapon and gadget
        // slots - the guns that would fire if you pulled the trigger.
        public static List<ForgeRankCopies.Copy> Carried(Unit ship)
        {
            var found = new List<ForgeRankCopies.Copy>();

            if (ship == null)
                return found;

            foreach (Shooter s in ship.GetComponentsInChildren<Shooter>(true))
            {
                WeaponBase w = (s != null) ? s.Weapon : null;
                ForgeRankCopies.Copy copy;

                if (w != null && ForgeRankCopies.TryGet(w, out copy) &&
                    !found.Contains(copy))
                {
                    found.Add(copy);
                }
            }

            // Gadgets fire without a Shooter, so ask the grid for them.
            try
            {
                var owner = ship.GetComponentInChildren<ModuleGridOwner>(true);

                if (owner != null && owner.ModuleGrid != null)
                {
                    foreach (IModuleCluster c in owner.ModuleGrid.GetAllClusters())
                    {
                        ForgeRankCopies.Copy copy;

                        if (c != null && c.HasMainModule &&
                            c.MainModule is WeaponBasedActiveModule &&
                            ForgeRankCopies.TryGet(c.MainModule, out copy) &&
                            !found.Contains(copy))
                        {
                            found.Add(copy);
                        }
                    }
                }
            }
            catch (Exception)
            {
            }

            return found;
        }

        // ------------------------------------------------------------------
        // R19 277: an OPT-IN pickup reward
        // ------------------------------------------------------------------
        //
        // "Picking up other items like fiber and powerstars dont give xp! Maybe
        // make an option for that to be a thing?" `rank.pickupXp`, off (0) by
        // default. The three pickups the player takes with the use key - an
        // ingredient, a consumable, a module - each pay it; a resource orb
        // does not, or every fuel bubble would be XP. They are the three
        // `InteractiblePickup` subclasses, and each declares its own
        // `OnPickedUp(Ship)`, so each is patched by name.
        [HarmonyPatch]
        public class OnPickedUp
        {
            static IEnumerable<System.Reflection.MethodBase> TargetMethods()
            {
                foreach (Type t in new[]
                         { typeof(IngredientPickup), typeof(ConsumablePickup),
                           typeof(ModulePickup) })
                {
                    System.Reflection.MethodInfo m =
                        AccessTools.Method(t, "OnPickedUp");

                    if (m != null)
                        yield return m;
                }
            }

            static void Postfix(Ship ship)
            {
                if (!ForgeRank.Any || ship == null)
                    return;

                try
                {
                    Unit unit = ship.Unit;

                    foreach (ForgeRankCopies.Copy copy in Carried(unit))
                    {
                        if (copy.cfg != null && copy.cfg.enabled &&
                            copy.cfg.pickupXp > 0f)
                        {
                            ForgeRankPatch.Award(unit, copy, copy.cfg.pickupXp);
                        }
                    }
                }
                catch (Exception e)
                {
                    Log.LogWarning("pickup XP skipped: " + e.Message);
                }
            }
        }

        // A stable id for the PLACE that was interacted with, or 0 for anything
        // that is not a place.
        //
        // ★ AN ALLOW-LIST OF PLACES, NOT "HAS A SAVABLEENTITY". The first
        // version assumed a pickup has no `SavableEntity` and so would fall out
        // on its own - and every one of the three does: `IngredientPickup`,
        // `ConsumablePickup` and `ModulePickup` persist in the level, so each
        // carries an entity id, and each one picked up paid a whole station's
        // XP. R19 test 251: *"picking up resources instantly levels the weapon
        // by 1. I picked up 2 fiber and got my weapon from 5 to 7."* Same
        // family as the borrowed-prefab deny-list: a rule written as "whatever
        // is not one of these" admits everything nobody thought of.
        //
        // Measured off the seven prefabs that carry an `Interactable`: both
        // station prefabs (`Station`, `Station Scanner`) route their use to a
        // `Station` component; `Scanner` and `Object Instrument A` route to
        // `Scanner` / `Instrument`; the three pickups route to
        // `InteractiblePickup`. His ask was XP for discovering EITHER STATION
        // (2026-09-15), so the allow-list is exactly `Station` - the scanner
        // and the instrument were never part of it.
        private static int Identify(Interactable it)
        {
            if (it.GetComponentInParent<Station>() == null)
                return 0;

            var savable = it.GetComponentInParent<SavableEntity>();

            if (savable == null)
                return 0;

            try
            {
                return savable.EntityData != null
                    ? savable.EntityData.instanceId
                    : 0;
            }
            catch (Exception)
            {
                return 0;
            }
        }
    }
}
