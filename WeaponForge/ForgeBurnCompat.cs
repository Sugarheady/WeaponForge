using System;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace WeaponForge
{
    // Coexistence bridge for the burn tick-rate / burn-color mechanic.
    //
    // ModuleForge (com.sugarheady.moduleforge) already owns a global burn engine
    // by patching DamagableResource.Update + the burn-particle emit
    // methods. If WeaponForge ALSO patched them, the two would fight
    // (stuck tick rates, permanently miscolored fire). So:
    //
    //   * ModuleForge PRESENT  -> WeaponForge does NOT run its own burn
    //     patches (they early-return, see OwnsPatches). WeaponForge feeds
    //     ModuleForge's accumulator by reflection (no assembly reference),
    //     so both mods compose under ONE owner.
    //   * ModuleForge ABSENT   -> WeaponForge runs its own bundled engine
    //     (ForgeBurn + its patches) and owns everything itself.
    //
    // All reflection is resolved once and guarded; if ModuleForge is
    // present but its API can't be resolved, WeaponForge's burn options
    // simply do nothing (rather than risk a conflict).
    public static class ForgeBurnCompat
    {
        private static readonly ManualLogSource Log =
            ForgeLog.Source("WeaponForge");

        private static bool _init;
        private static bool _moduleForgePresent;
        private static bool _bridgeReady;

        // Reflected ModuleForge API.
        private static MethodInfo _addBooster;      // (Unit.Data, float)
        private static MethodInfo _removeBooster;   // (Unit.Data, float)
        private static MethodInfo _addColor;        // (Unit.Data, BurnColorEffect)
        private static MethodInfo _removeColor;     // (Unit.Data, BurnColorEffect)
        private static Type _mfColorType;           // ModuleForge.BurnColorEffect
        private static FieldInfo _fRgb, _fColor, _fLabel, _fSpeed,
            _fSat, _fBright, _fTerrain;

        // True when THIS mod should run its own burn Harmony patches
        // (i.e. ModuleForge isn't the owner).
        public static bool OwnsPatches
        {
            get
            {
                EnsureInit();
                return !_moduleForgePresent;
            }
        }

        private static void EnsureInit()
        {
            if (_init)
                return;
            _init = true;

            Type burn;

            try
            {
                // Presence is decided by whether ModuleForge's types are
                // loaded (its assembly is loaded at BepInEx startup if the
                // mod is installed) - no BepInEx-version-specific API.
                burn = AccessTools.TypeByName("ModuleForge.ModuleForgeBurn");
                _mfColorType = AccessTools.TypeByName("ModuleForge.BurnColorEffect");
                _moduleForgePresent = burn != null;
            }
            catch
            {
                burn = null;
                _moduleForgePresent = false;
            }

            if (!_moduleForgePresent)
            {
                Log.LogInfo(
                    "ModuleForge not detected - WeaponForge runs its own " +
                    "burn engine.");
                return;
            }

            try
            {
                if (burn != null && _mfColorType != null)
                {
                    _addBooster = AccessTools.Method(
                        burn, "AddBooster",
                        new[] { typeof(Unit.Data), typeof(float) });
                    _removeBooster = AccessTools.Method(
                        burn, "RemoveBooster",
                        new[] { typeof(Unit.Data), typeof(float) });
                    _addColor = AccessTools.Method(
                        burn, "AddColor",
                        new[] { typeof(Unit.Data), _mfColorType });
                    _removeColor = AccessTools.Method(
                        burn, "RemoveColor",
                        new[] { typeof(Unit.Data), _mfColorType });

                    _fRgb = AccessTools.Field(_mfColorType, "rgb");
                    _fColor = AccessTools.Field(_mfColorType, "color");
                    _fLabel = AccessTools.Field(_mfColorType, "colorLabel");
                    _fSpeed = AccessTools.Field(_mfColorType, "rgbSpeed");
                    _fSat = AccessTools.Field(_mfColorType, "saturation");
                    _fBright = AccessTools.Field(_mfColorType, "brightness");
                    _fTerrain = AccessTools.Field(_mfColorType, "includeTerrain");
                }

                _bridgeReady =
                    _addBooster != null && _removeBooster != null &&
                    _addColor != null && _removeColor != null &&
                    _mfColorType != null && _fRgb != null &&
                    _fColor != null && _fSpeed != null &&
                    _fTerrain != null;

                if (_bridgeReady)
                {
                    Log.LogInfo(
                        "ModuleForge detected - WeaponForge burn options " +
                        "will feed ModuleForge's engine (no conflict).");
                }
                else
                {
                    Log.LogWarning(
                        "ModuleForge detected but its burn API couldn't be " +
                        "resolved - WeaponForge burn options are disabled to " +
                        "avoid a conflict. (Update both mods to matching " +
                        "versions.)");
                }
            }
            catch (Exception e)
            {
                _bridgeReady = false;
                Log.LogWarning("ModuleForge burn bridge failed: " + e);
            }
        }

        // --- Tick rate --------------------------------------------------
        public static void AddRate(Unit.Data owner, float amount)
        {
            EnsureInit();

            if (!_moduleForgePresent)
            {
                ForgeBurn.AddBooster(owner, amount);
                return;
            }

            if (_bridgeReady)
                Invoke(_addBooster, new object[] { owner, amount });
        }

        public static void RemoveRate(Unit.Data owner, float amount)
        {
            EnsureInit();

            if (!_moduleForgePresent)
            {
                ForgeBurn.RemoveBooster(owner, amount);
                return;
            }

            if (_bridgeReady)
                Invoke(_removeBooster, new object[] { owner, amount });
        }

        // --- Color ------------------------------------------------------
        // Returns an opaque handle to pass back to RemoveColor. For the
        // standalone path that's the effect itself; for the ModuleForge
        // path it's a reflected ModuleForge.BurnColorEffect proxy.
        public static object AddColor(Unit.Data owner, ForgeBurnColorEffect effect)
        {
            EnsureInit();

            if (!_moduleForgePresent)
            {
                ForgeBurn.AddColor(owner, effect);
                return effect;
            }

            if (!_bridgeReady)
                return null;

            try
            {
                object proxy = Activator.CreateInstance(_mfColorType);
                _fRgb.SetValue(proxy, effect.rgb);
                _fColor.SetValue(proxy, effect.color);
                if (_fLabel != null)
                    _fLabel.SetValue(proxy, effect.colorLabel ?? "");
                _fSpeed.SetValue(proxy, effect.rgbSpeed);
                if (_fSat != null)
                    _fSat.SetValue(proxy, effect.saturation);
                if (_fBright != null)
                    _fBright.SetValue(proxy, effect.brightness);
                _fTerrain.SetValue(proxy, effect.includeTerrain);

                Invoke(_addColor, new object[] { owner, proxy });
                return proxy;
            }
            catch (Exception e)
            {
                Log.LogWarning("Feeding burn color to ModuleForge failed: " + e);
                return null;
            }
        }

        public static void RemoveColor(
            Unit.Data owner, ForgeBurnColorEffect effect, object handle)
        {
            EnsureInit();

            if (!_moduleForgePresent)
            {
                ForgeBurn.RemoveColor(owner, effect);
                return;
            }

            if (_bridgeReady && handle != null)
                Invoke(_removeColor, new object[] { owner, handle });
        }

        private static void Invoke(MethodInfo m, object[] args)
        {
            try
            {
                if (m != null)
                    m.Invoke(null, args);
            }
            catch (Exception e)
            {
                Log.LogWarning("ModuleForge burn call failed: " + e);
            }
        }
    }
}
