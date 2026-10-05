using System;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace WeaponForge
{
    // Recolors a unit's burn aura (enemies only). Only active when
    // WeaponForge owns the burn engine (ModuleForge absent).
    [HarmonyPatch(typeof(StatusEffectParticleManager), "EmitForUnit")]
    public class ForgeBurnColorPatch
    {
        private static readonly ManualLogSource Log =
            ForgeLog.Source("WeaponForge");

        static bool Prefix(StatusEffectParticleManager __instance, Unit.Data unit)
        {
            if (!ForgeBurnCompat.OwnsPatches)
                return true;

            if (!ForgeBurn.HasColor)
                return true;

            if (unit == null || unit.entity == null)
                return true;

            if (ForgeBurn.IsExcluded(unit))
                return true;

            ParticleSystem[] systems;
            Color c;
            Vector3 pos;

            try
            {
                systems = ForgeBurnParticles.GetSystems(__instance);
                if (systems == null || systems.Length == 0)
                    return true;

                c = ForgeBurn.GetEmitColor();
                pos = unit.entity.position;
            }
            catch (Exception e)
            {
                Log.LogError("Burn color setup failed: " + e);
                return true;
            }

            var ep = default(ParticleSystem.EmitParams);
            ep.position = new Vector3(pos.x, pos.y, 0f);
            ep.applyShapeToPosition = true;
            ep.startColor = c;

            for (int i = 0; i < systems.Length; i++)
            {
                if (systems[i] == null)
                    continue;

                try { systems[i].Emit(ep, 1); }
                catch (Exception e) { Log.LogError("Burn color emit failed: " + e); }
            }

            return false;
        }
    }

    // Shared access to the private particleSystems array on the manager.
    internal static class ForgeBurnParticles
    {
        private static AccessTools.FieldRef<
            StatusEffectParticleManager, ParticleSystem[]> _ref;
        private static bool _ready;

        internal static ParticleSystem[] GetSystems(
            StatusEffectParticleManager m)
        {
            if (!_ready)
            {
                try
                {
                    _ref = AccessTools.FieldRefAccess<
                        StatusEffectParticleManager, ParticleSystem[]>(
                        "particleSystems");
                }
                catch
                {
                    _ref = null;
                }
                _ready = true;
            }

            return _ref != null ? _ref(m) : null;
        }
    }
}
