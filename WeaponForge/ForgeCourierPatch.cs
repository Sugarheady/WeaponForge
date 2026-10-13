using System;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace WeaponForge
{
    // Drives ForgeCourier (see its header for why the seam is Fire):
    //
    //   WeaponBase.Fire              PREFIX  a courier gun launches drones
    //                                        and skips its own shot
    //   WeaponBase.Dispose           PREFIX  a rebuilt or dropped gun is dead
    //                                        to the drones it launched
    //   HitscanWeapon.OnBarrelMoved  PREFIX  a courier BEAM gun draws no beam
    //                                        from your ship
    //   GameSaver.Save               PREFIX  decoys leave the world before a
    //                                        save can keep them
    public static class ForgeCourierPatch
    {
        private static readonly ManualLogSource Log =
            ForgeLog.Source("WeaponForge.Courier");

        private static bool _saidLaunchError;

        // EVERY PULL ENTERS A GUN HERE: Shooter.Shoot, a gadget's Activate,
        // a wingman's copy, a sub-emitter, a turret. Last, so the gadget aim
        // fix (also a Fire prefix) has already turned a gadget's barrel; the
        // drone reads the cursor anyway, so the order only matters for the
        // blind fallback.
        [HarmonyPatch(typeof(WeaponBase), "Fire")]
        public class OnFire
        {
            [HarmonyPriority(Priority.Last)]
            static bool Prefix(WeaponBase __instance, IBarrel barrel)
            {
                if (!ForgeCourier.Any || __instance == null)
                    return true;

                // A Fire reached from inside a drone's own volley is not a
                // pull; and a weapon object we built is never a launcher.
                if (ForgeCourier.Firing > 0 || ForgeCourier.IsOurs(__instance))
                    return true;

                ForgeCourier.Config cfg;

                if (!ForgeCourier.TryGet(__instance.TemplateData, out cfg))
                    return true;

                try
                {
                    return !ForgeCourier.Launch(__instance, barrel, cfg);
                }
                catch (Exception e)
                {
                    // A broken launch fires the gun normally: a lost shot is a
                    // worse failure than an ordinary one.
                    if (!_saidLaunchError)
                    {
                        _saidLaunchError = true;
                        Log.LogError(cfg.fileName + ": a courier launch failed, " +
                                     "so the gun fired normally: " + e);
                    }

                    return true;
                }
            }
        }

        [HarmonyPatch(typeof(WeaponBase), "Dispose")]
        public class OnDispose
        {
            static void Prefix(WeaponBase __instance)
            {
                if (!ForgeCourier.Any || __instance == null)
                    return;

                try { ForgeCourier.MarkDead(__instance); }
                catch (Exception e) { Log.LogError("courier dispose: " + e); }
            }
        }

        // ★ A HELD BEAM DRAWS ITSELF FROM YOUR SHIP every frame:
        // Shooter.Update calls OnBarrelMoved unconditionally, and with the
        // trigger held the visual shows. A courier beam gun's shot happens at
        // the DRONE, so the launcher's own beam is held off here. The drones'
        // copies are ours (ForgeCourier.IsOurs) and are drawn by the drone.
        [HarmonyPatch(typeof(HitscanWeapon), "OnBarrelMoved")]
        public class OnBarrelMoved
        {
            [HarmonyPriority(Priority.First)]
            static bool Prefix(HitscanWeapon __instance)
            {
                if (!ForgeCourier.Any || __instance == null)
                    return true;

                return !ForgeCourier.IsLauncher(__instance);
            }
        }

        [HarmonyPatch(typeof(Punk.SaveLoad.GameSaver), "Save", new Type[] { typeof(string) })]
        public class OnSave
        {
            static void Prefix()
            {
                if (!ForgeCourier.Any)
                    return;

                try { ForgeCourier.BeforeSave(); }
                catch (Exception e) { Log.LogError("courier: before save: " + e); }
            }
        }
    }
}
