using System;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace WeaponForge
{
    // Optional: recolor burning terrain/world fire too. Only active when
    // WeaponForge owns the burn engine (ModuleForge absent). Briefly tints
    // the shared particle systems for the world-fire emit and restores
    // them in a Finalizer (always runs), reverting exactly what was tinted.
    [HarmonyPatch(typeof(StatusEffectParticleManager), "Emit",
        new Type[] { typeof(Vector2Int) })]
    public class ForgeBurnColorTerrainPatch
    {
        private static readonly ManualLogSource Log =
            ForgeLog.Source("WeaponForge");

        private static ParticleSystem.MinMaxGradient[] _saved;
        private static ParticleSystem[] _tintedSystems;
        private static int _tintedCount;

        static void Prefix(StatusEffectParticleManager __instance)
        {
            _tintedSystems = null;
            _tintedCount = 0;

            if (!ForgeBurnCompat.OwnsPatches)
                return;

            if (!ForgeBurn.HasColor || !ForgeBurn.ColorTerrain)
                return;

            try
            {
                ParticleSystem[] systems =
                    ForgeBurnParticles.GetSystems(__instance);
                if (systems == null || systems.Length == 0)
                    return;

                Color c = ForgeBurn.GetEmitColor();

                if (_saved == null || _saved.Length != systems.Length)
                    _saved = new ParticleSystem.MinMaxGradient[systems.Length];

                _tintedSystems = systems;

                for (int i = 0; i < systems.Length; i++)
                {
                    if (systems[i] == null)
                        continue;

                    var main = systems[i].main;
                    _saved[i] = main.startColor;
                    main.startColor = new ParticleSystem.MinMaxGradient(c);
                    _tintedCount = i + 1;
                }
            }
            catch (Exception e)
            {
                Log.LogError("Burn color terrain setup failed: " + e);
            }
        }

        static void Finalizer()
        {
            if (_tintedSystems == null)
                return;

            try
            {
                int n = _tintedCount;
                if (n > _tintedSystems.Length) n = _tintedSystems.Length;
                if (_saved != null && n > _saved.Length) n = _saved.Length;

                for (int i = 0; i < n; i++)
                {
                    if (_tintedSystems[i] == null)
                        continue;

                    var main = _tintedSystems[i].main;
                    main.startColor = _saved[i];
                }
            }
            catch (Exception e)
            {
                Log.LogError("Burn color terrain restore failed: " + e);
            }
            finally
            {
                _tintedSystems = null;
                _tintedCount = 0;
            }
        }
    }
}
