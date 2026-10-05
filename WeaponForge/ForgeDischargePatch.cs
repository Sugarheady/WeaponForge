using System;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace WeaponForge
{
    // Makes a hitscan weapon fire a chain-lightning DISCHARGE (hitscan
    // weapons never do this in vanilla - only projectiles). For any Forge
    // weapon flagged "dischargeOnFire", this spawns a discharge source on
    // the ship from the weapon's own DischargeData, telegraphs for
    // buildupSeconds (see ForgeBeamChargePatch), then strikes. Generalized
    // from the standalone WhiteTeslaMod's TeslaDischargePatch.
    [HarmonyPatch(typeof(HitscanWeapon), "FireSingle")]
    public class ForgeDischargePatch
    {
        private static readonly ManualLogSource Log =
            ForgeLog.Source("WeaponForge");

        static void Postfix(
            HitscanWeapon __instance,
            Vector2 position,
            Vector2 direction)
        {
            try
            {
                ForgeElectric.Config cfg;

                if (!ForgeElectric.TryGet(__instance.TemplateData, out cfg))
                    return;

                if (!cfg.dischargeOnFire || __instance.Owner == null)
                    return;

                ElectricityManager electricityManager;

                if (!ServiceLocator.TryGet<ElectricityManager>(
                    out electricityManager))
                {
                    return;
                }

                // Source rides on the ship, so buildup beams radiate from
                // (and follow) the ship and the strike hits whatever is in
                // chain range when the telegraph completes.
                var dischargeObject = new GameObject("Forge Discharge");

                dischargeObject.transform.SetParent(
                    __instance.Owner.transform, false);
                dischargeObject.transform.localPosition = Vector3.zero;

                var marker =
                    dischargeObject.AddComponent<ForgeDischargeMarker>();
                marker.buildupSeconds = cfg.buildupSeconds;

                var conductor =
                    dischargeObject.AddComponent<ElectricityConductor>();
                conductor.Setup(__instance.DischargeData);

                // Lifetime = telegraph + a short strike window.
                UnityEngine.Object.Destroy(
                    dischargeObject, cfg.buildupSeconds + 0.5f);
            }
            catch (Exception e)
            {
                Log.LogError("Forge discharge failed: " + e);
            }
        }
    }
}
