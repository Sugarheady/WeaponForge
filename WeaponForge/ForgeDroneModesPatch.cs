using System;
using System.Collections.Generic;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace WeaponForge
{
    // Where drone modes meets the game. See ForgeDroneModes for the design.
    public static class ForgeDroneModesPatch
    {
        private static readonly ManualLogSource Log =
            ForgeLog.Source("WeaponForge.DroneModes");

        // ---- a new drone ---------------------------------------------------
        //
        // `Unit.SetOwner` is the one public seam a gadget-spawned drone passes
        // through (`SpawnMinionModule.Activate` raises no event and the unit is
        // a local). New drones deployed while a mode is on join that mode
        // (concept 7).
        [HarmonyPatch(typeof(Unit), "SetOwner")]
        public class OnSetOwner
        {
            static void Postfix(Unit __instance, Unit.OwnerConnectionType connectionType)
            {
                if (ForgeDroneModes.SlotOf(connectionType) == 0)
                    return;

                ForgeDroneBrain.Attach(__instance);
            }
        }

        // ---- a drone coming back ------------------------------------------
        //
        // A load replays the owner link through `Unit.Data.SetOwner`, not the
        // component's, so the patch above never sees a restored drone.
        // `SavableEntity.Bind` with `isFirstTime` false is the moment the
        // GameObject is attached to data that outlived it (a load, or the
        // segment streaming back in) - the same seam ForgeMinionRestore uses.
        [HarmonyPatch(typeof(SavableEntity), "Bind")]
        public class OnBind
        {
            static void Postfix(SavableEntity __instance, bool isFirstTime)
            {
                if (isFirstTime || __instance == null)
                    return;

                try
                {
                    Unit minion = __instance.GetComponent<Unit>();

                    if (minion == null || minion.ComponentData == null ||
                        ForgeDroneModes.SlotOf(minion.ComponentData.ConnectionToOwner) == 0)
                    {
                        return;
                    }

                    ForgeDroneBrain.Attach(minion);
                }
                catch (Exception)
                {
                }
            }
        }

        // ---- the gadget buttons ---------------------------------------------
        //
        // `ShipInput.HandleAction` sets `ModuleActivator.IsActivated` while a
        // gadget button is held, and `ModuleActivator.Update` fires the gadget
        // whenever it is set and the cooldown has passed. So reading it here
        // covers every binding the game gives a gadget (1 / Ctrl, 2 / middle
        // mouse, 3 / T, the pad's West / North / East) without binding any of
        // them again - and skipping the original is what keeps Y + 1 from also
        // deploying a drone.
        private static AccessTools.FieldRef<ModuleActivator, Unit> _unitRef;
        private static AccessTools.FieldRef<ModuleActivator, int> _indexRef;
        private static bool _refsLooked;
        private static bool _refsBroken;

        [HarmonyPatch(typeof(ModuleActivator), "Update")]
        public class OnActivatorUpdate
        {
            static bool Prefix(ModuleActivator __instance)
            {
                if (_refsBroken || !ForgeDroneModes.KeyOn || __instance == null)
                    return true;

                try
                {
                    if (!_refsLooked)
                    {
                        _refsLooked = true;
                        _unitRef = AccessTools.FieldRefAccess<ModuleActivator, Unit>("unit");
                        _indexRef = AccessTools.FieldRefAccess<ModuleActivator, int>("moduleIndex");
                    }

                    Unit unit = _unitRef(__instance);
                    int index = _indexRef(__instance);

                    return ForgeDroneModes.ActivatorGate(unit, index, __instance.IsActivated);
                }
                catch (Exception e)
                {
                    // A gadget must never stop working because of us.
                    _refsBroken = true;

                    Log.LogWarning(
                        "drone modes: the gadget buttons could not be read (" + e.GetType().Name +
                        ": " + e.Message + ") - Y + 1/2/3 is off this launch and the gadgets " +
                        "fire as normal. The mode key alone still works.");

                    return true;
                }
            }
        }

        // ---- the gadget's card ----------------------------------------------
        //
        // "The gadget's card names the key, the way a ranked gun's card names
        // H" (concept 6). On a drone gadget (the game's five and Forge `drone`
        // files) and a wingman gadget.
        private static AccessTools.FieldRef<HoveredModuleInfo, List<DisplayableProperty>> _props;
        private static bool _propsLooked;

        [HarmonyPatch(typeof(HoveredModuleInfo), "AddModuleOrWeaponProperties")]
        public class OnModuleCard
        {
            static void Postfix(HoveredModuleInfo __instance, Module module)
            {
                if (!ForgeDroneModes.KeyOn || module == null)
                    return;

                try
                {
                    bool companions = module is SpawnMinionModule;

                    if (!companions)
                    {
                        var wb = module as WeaponBasedActiveModule;
                        ForgeWingman.Config cfg;

                        companions = wb != null && ForgeWingman.TryGet(wb.WeaponData, out cfg);
                    }

                    if (!companions)
                        return;

                    if (!_propsLooked)
                    {
                        _propsLooked = true;
                        _props = AccessTools.FieldRefAccess<HoveredModuleInfo, List<DisplayableProperty>>(
                            "moduleProperties");
                    }

                    if (_props == null)
                        return;

                    List<DisplayableProperty> list = _props(__instance);
                    string line = ForgeDroneModes.CardLine();

                    if (list != null && line != null)
                        list.Add(new DisplayableProperty("Drone modes", line));
                }
                catch (Exception)
                {
                    // A card line must never stop a card drawing.
                }
            }
        }
    }
}
