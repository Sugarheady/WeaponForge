using System;
using BepInEx.Logging;
using HarmonyLib;

namespace WeaponForge
{
    // Gives every enemy a passive (non-source) ElectricityConductor so
    // chain-lightning weapons can hop through them. Only active when at
    // least one loaded Forge weapon requests chaining (ForgeElectric.
    // AnyChainWeapon), so installs with no electric weapon don't touch
    // enemies. Enemies that already carry a conductor are left alone.
    // Generalized from the standalone WhiteTeslaMod's EnemyConductorPatch.
    [HarmonyPatch(typeof(Enemy), "Initialize", new[] { typeof(IModuleGrid) })]
    public class ForgeEnemyConductorPatch
    {
        private static readonly ManualLogSource Log =
            ForgeLog.Source("WeaponForge");

        static void Postfix(Enemy __instance)
        {
            try
            {
                if (!ForgeElectric.AnyChainWeapon || __instance == null)
                    return;

                var go = __instance.gameObject;

                if (go.GetComponent<ElectricityConductor>() != null)
                    return;

                var conductor = go.AddComponent<ElectricityConductor>();

                // Set serialized fields directly rather than Setup(): the
                // enemy may be inactive during level-gen (Awake hasn't run,
                // ElectricityManager not fetched), and Setup() would throw
                // and kill the load coroutine. OnEnable self-registers.
                var t = Traverse.Create(conductor);
                t.Field("isSource").SetValue(false);
                t.Field("emittedSystem").SetValue(
                    ElectricityManager.SubSystemType.None);
                t.Field("conductedSystem").SetValue(
                    ElectricityManager.SubSystemType.Player);
                t.Field("chainLength").SetValue(0);
                t.Field("conductivity").SetValue(20);
                t.Field("minConductivity").SetValue(0);
                t.Field("showPreviewBeam").SetValue(true);
                t.Field("showBeamParticles").SetValue(true);
                t.Field("limitedCharge").SetValue(false);
                t.Field("maxCharge").SetValue(0f);
                t.Field("damageRadius").SetValue(0f);
                t.Field("damageRepeatDelay").SetValue(0.1f);

                if (go.activeInHierarchy)
                {
                    conductor.enabled = false;
                    conductor.enabled = true;
                }
            }
            catch (Exception e)
            {
                Log.LogError("Forge enemy-conductor patch failed: " + e);
            }
        }
    }
}
