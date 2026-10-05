using System;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace WeaponForge
{
    // Recolors the chain-lightning beam. The beam's LineRenderers are
    // re-colored from a baked gradient on a timer, so a one-shot tint gets
    // reverted; this component re-applies the color every LateUpdate
    // (after the beam's own Update), for a solid color or an RGB cycle.
    // The color is global (the player beam prefab is shared), driven by
    // ForgeElectric's stored lightning color.
    public class ForgeLightningColor : MonoBehaviour
    {
        private LineRenderer[] _lines;

        private void Awake()
        {
            _lines = GetComponentsInChildren<LineRenderer>(true);
        }

        private void LateUpdate()
        {
            if (!ForgeElectric.HasLightningColor)
                return;

            if (_lines == null || _lines.Length == 0)
                _lines = GetComponentsInChildren<LineRenderer>(true);

            if (_lines == null)
                return;

            Color c = ForgeElectric.LightningRgb ? Rgb() : ForgeElectric.LightningColor;

            for (int i = 0; i < _lines.Length; i++)
            {
                if (_lines[i] == null)
                    continue;

                _lines[i].startColor = c;
                _lines[i].endColor = c;
            }
        }

        private static Color Rgb()
        {
            float h = Time.time * ForgeElectric.LightningRgbSpeed;
            h -= Mathf.Floor(h);
            return Color.HSVToRGB(h, 1f, 1f);
        }
    }

    // Runs once the electricity manager wakes, applying (a) a lightning
    // COLOR by attaching ForgeLightningColor to the PLAYER beam prefab so
    // every spawned player-side beam carries it, and (b) a lightning RANGE
    // override by writing the PLAYER subsystem's private beamRange. Both
    // affect only player-side electricity; enemy beams keep their stock
    // color and reach. Each part runs only if a weapon requested it.
    [HarmonyPatch(typeof(ElectricityManager), "Awake")]
    public class ForgeLightningColorPatch
    {
        private static readonly ManualLogSource Log =
            ForgeLog.Source("WeaponForge");

        private static AccessTools.FieldRef<
            ElectricityManager, ElectricityBeam> _beamRef;
        private static bool _ready;

        static void Postfix(ElectricityManager __instance)
        {
            if (!ForgeElectric.HasLightningColor && !ForgeElectric.HasLightningRange)
                return;

            if (ForgeElectric.HasLightningColor)
            {
                try
                {
                    if (!_ready)
                    {
                        _beamRef = AccessTools.FieldRefAccess<
                            ElectricityManager, ElectricityBeam>("_beamPrefab");
                        _ready = true;
                    }

                    ElectricityBeam beam =
                        _beamRef != null ? _beamRef(__instance) : null;

                    if (beam != null &&
                        beam.gameObject.GetComponent<ForgeLightningColor>() == null)
                    {
                        beam.gameObject.AddComponent<ForgeLightningColor>();
                    }
                }
                catch (Exception e)
                {
                    Log.LogError("Forge lightning color patch failed: " + e);
                }
            }

            if (ForgeElectric.HasLightningRange)
            {
                try
                {
                    // Override the PLAYER subsystem's beamRange only (enemy
                    // subsystem keeps its own copy, so enemy electric
                    // attacks aren't extended).
                    var subField = AccessTools.Field(
                        typeof(ElectricityManager), "playerSubsystem");
                    var brField = AccessTools.Field(
                        typeof(ElectricitySubSystem), "beamRange");

                    if (subField != null && brField != null)
                    {
                        var sub = subField.GetValue(__instance);
                        if (sub != null)
                            brField.SetValue(sub, ForgeElectric.LightningRange);
                    }
                }
                catch (Exception e)
                {
                    Log.LogError("Forge lightning range patch failed: " + e);
                }
            }
        }
    }
}
