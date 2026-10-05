using System;
using System.Collections.Generic;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace WeaponForge
{
    // Makes orbit weapons work in GADGET slots (the 1/2/3 keys).
    //
    // Gadgets don't go through the player's Shooter - a WeaponBasedActiveModule
    // fires its own weapon directly in Activate(owner) - so ForgeOrbitPatch
    // (which hooks Shooter) never sees them. Here we hook the gadget's Activate
    // instead: when its weapon is a registered orbit weapon we spin up a
    // ForgeOrbitController tied to the owner ship, map the key press to the
    // orbit's activation mode, and (for a pure orbit) suppress the gadget's
    // normal forward shot.
    //
    // Activation on a gadget (press the gadget key):
    //   toggle          -> press toggles the ring on/off
    //   fire            -> press summons the ring for orbitFireDuration
    //   passive / hold  -> press turns the ring on and it stays on (a gadget
    //                      can't be "held" or truly passive, so first press
    //                      starts it). Toggle is the natural gadget mode.
    public static class ForgeOrbitGadgetPatch
    {
        private static readonly ManualLogSource Log =
            ForgeLog.Source("WeaponForge");

        private static readonly Dictionary<WeaponBasedActiveModule, ForgeOrbitController> _ctrls =
            new Dictionary<WeaponBasedActiveModule, ForgeOrbitController>();

        private static AccessTools.FieldRef<WeaponBasedActiveModule, WeaponBase> _weaponRef;
        private static bool _ready;

        private static void Ensure()
        {
            if (_ready)
                return;
            _weaponRef = AccessTools.FieldRefAccess<WeaponBasedActiveModule, WeaponBase>(
                "weapon");
            _ready = true;
        }

        private static ForgeOrbitController GetOrCreate(
            WeaponBasedActiveModule module, Unit owner, ForgeOrbit.Config cfg)
        {
            ForgeOrbitController ctrl;
            if (_ctrls.TryGetValue(module, out ctrl) && ctrl != null)
                return ctrl;

            var go = new GameObject("Forge Orbit (gadget)");
            if (owner != null)
                go.transform.SetParent(owner.transform, false);

            ctrl = go.AddComponent<ForgeOrbitController>();
            ctrl.selfDriven = true;
            // Read the gadget's current weapon live (it's recreated when the
            // module cluster refreshes), so orb count/damage stay in sync.
            ctrl.Init(cfg, owner, () =>
                (_weaponRef != null) ? _weaponRef(module) : null);
            _ctrls[module] = ctrl;
            return ctrl;
        }

        [HarmonyPatch(typeof(WeaponBasedActiveModule), "Activate")]
        public class OnActivate
        {
            static bool Prefix(WeaponBasedActiveModule __instance, Unit owner)
            {
                try
                {
                    Ensure();

                    WeaponData wd = __instance.WeaponData;
                    ForgeOrbit.Config cfg;
                    if (wd == null || !ForgeOrbit.TryGet(wd, out cfg))
                        return true; // normal gadget - activate as usual

                    ForgeOrbitController ctrl = GetOrCreate(__instance, owner, cfg);

                    switch (cfg.mode)
                    {
                        case ForgeOrbit.Mode.Fire:
                            ctrl.fireActiveUntil = Time.time + cfg.fireDuration;
                            break;
                        default:
                            // A gadget is press-driven, so every non-"fire"
                            // mode behaves as a toggle: press the key to turn
                            // the ring on, press again to turn it off. (This
                            // is what "toggle" always did; "passive"/"hold"
                            // get it too, since a gadget can't be truly
                            // always-on - it needs the first press anyway.)
                            ctrl.toggledOn = !ctrl.toggledOn;
                            break;
                    }

                    ctrl.TriggerFling();
                    ctrl.RegenOnFire();

                    // suppressFire (default) -> skip the gadget's own Fire so
                    // it's a pure orbit; the key press just drives the ring.
                    return !cfg.suppressFire;
                }
                catch (Exception e)
                {
                    Log.LogError("Orbit gadget activate failed: " + e);
                    return true;
                }
            }
        }

        // Tear the ring down when the gadget is removed from the ship.
        [HarmonyPatch(typeof(WeaponBasedActiveModule), "OnUninstalled")]
        public class OnUninstalled
        {
            static void Postfix(WeaponBasedActiveModule __instance)
            {
                try
                {
                    ForgeOrbitController ctrl;
                    if (_ctrls.TryGetValue(__instance, out ctrl))
                    {
                        if (ctrl != null)
                            UnityEngine.Object.Destroy(ctrl.gameObject);
                        _ctrls.Remove(__instance);
                    }
                }
                catch (Exception e)
                {
                    Log.LogError("Orbit gadget uninstall failed: " + e);
                }
            }
        }
    }
}
