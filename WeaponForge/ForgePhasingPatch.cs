using System;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace WeaponForge
{
    // Makes a projectile phase through terrain. Projectile.Shoot sets
    // collisionLayerMask = the layer's physics collision matrix (which
    // includes Ground). For a projectile tagged ForgePhasing we strip the
    // Ground bit afterward, so its CircleCast never detects terrain - it
    // passes through walls but still collides with enemies. Stripping a
    // bit is idempotent, so this is safe even if another mod also patches
    // Projectile.Shoot for phasing.
    [HarmonyPatch(typeof(Projectile), "Shoot")]
    public class ForgePhasingPatch
    {
        private static readonly ManualLogSource Log =
            ForgeLog.Source("WeaponForge");

        private static AccessTools.FieldRef<Projectile, LayerMask> _maskRef;
        private static bool _ready;
        private static int _groundBit = -2;

        static void Postfix(Projectile __instance)
        {
            try
            {
                if (__instance.GetComponent<ForgePhasing>() == null)
                    return;

                if (!_ready)
                {
                    // The field is a LayerMask struct, NOT an int. Asking
                    // FieldRefAccess for <Projectile,int> throws (value-type
                    // mismatch), which silently broke phasing - so read it as
                    // a LayerMask and strip the Ground bit off its .value.
                    _maskRef = AccessTools.FieldRefAccess<Projectile, LayerMask>(
                        "collisionLayerMask");
                    int g = LayerMask.NameToLayer("Ground");
                    _groundBit = (g >= 0) ? (1 << g) : 0;
                    _ready = true;
                }

                if (_maskRef == null || _groundBit == 0)
                    return;

                LayerMask mask = _maskRef(__instance);
                mask.value &= ~_groundBit;
                _maskRef(__instance) = mask;
            }
            catch (Exception e)
            {
                Log.LogError("Phasing patch failed: " + e);
            }
        }
    }
}
