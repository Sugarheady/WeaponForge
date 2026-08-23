using System;
using System.Collections.Generic;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace WeaponForge
{
    // Parents a muzzle flash emitter to the barrel that fires it, so it rides
    // the ship instead of hanging in the air where the last shot went off.
    //
    // Runs as a DoShoot POSTFIX, after the game has positioned the emitter
    // itself - so the shot still stamps the flash at
    // barrel.Position + aim * barrelLength exactly as before, and the parent
    // only decides what happens BETWEEN shots.
    //
    // Off unless some weapon actually asks for it, so every other weapon in
    // the game pays nothing.
    public static class ForgeMuzzleFollowPatch
    {
        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("WeaponForge");

        // Set by WeaponBuilder when any weapon enables muzzleFollow.
        public static bool Enabled;

        private static AccessTools.FieldRef<
            WeaponBase, List<ParticleSystem>> _instances;

        private static bool _ready;

        private static void Ensure()
        {
            if (_ready)
                return;

            _ready = true;

            try
            {
                _instances = AccessTools.FieldRefAccess<
                    WeaponBase, List<ParticleSystem>>(
                        "muzzleParticleSystemInstances");
            }
            catch (Exception e)
            {
                _instances = null;

                Log.LogWarning(
                    "Could not reach WeaponBase.muzzleParticleSystemInstances (" +
                    e.Message + ") - \"muzzleFollow\" will do nothing. The " +
                    "flash still fires, it just stays where it was shot.");
            }
        }

        [HarmonyPatch(typeof(WeaponBase), "DoShoot")]
        public class OnDoShoot
        {
            static void Postfix(WeaponBase __instance, IBarrel barrel)
            {
                if (!Enabled)
                    return;

                // A FakeBarrel - what sub-emitters and consumables fire
                // through - is a plain class, not a Component, so there is no
                // transform to ride. Those keep the stamped behaviour.
                var anchor = barrel as Component;

                if (anchor == null)
                    return;

                try
                {
                    Ensure();

                    if (_instances == null)
                        return;

                    List<ParticleSystem> live = _instances(__instance);

                    if (live == null)
                        return;

                    for (int i = 0; i < live.Count; i++)
                    {
                        ParticleSystem ps = live[i];

                        if (ps == null)
                            continue;

                        Transform t = ps.transform;

                        if (t.parent == anchor.transform)
                            continue;

                        if (ps.GetComponent<ForgeMuzzleFollow>() == null)
                            continue;

                        // worldPositionStays: the emitter keeps the position,
                        // rotation AND SCALE the game just gave it. Without
                        // that it would inherit the ship's scale, and these
                        // systems use local scaling mode - so a scaled ship
                        // would silently resize every flash.
                        t.SetParent(anchor.transform, true);
                    }
                }
                catch (Exception e)
                {
                    Log.LogError("Muzzle follow failed: " + e);
                }
            }
        }
    }
}
