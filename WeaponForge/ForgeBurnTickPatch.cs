using System;
using BepInEx.Logging;
using HarmonyLib;

namespace WeaponForge
{
    // Applies WeaponForge's own burn tick-rate boost to enemies. Only
    // active when WeaponForge owns the burn engine (ModuleForge absent);
    // otherwise it no-ops and ModuleForge's identical patch does the work.
    [HarmonyPatch(typeof(DamagableResource), "Update")]
    public class ForgeBurnTickPatch
    {
        private static readonly ManualLogSource Log =
            ForgeLog.Source("WeaponForge");

        static void Prefix(DamagableResource __instance)
        {
            if (!ForgeBurnCompat.OwnsPatches)
                return;

            if (ForgeBurn.Delta <= 0f && !ForgeBurn.EverModified)
                return;

            try
            {
                Unit unit = __instance.GetComponent<Unit>();
                if (unit == null)
                    return;

                ForgeBurn.ApplyTo(unit.ComponentData);
            }
            catch (Exception e)
            {
                Log.LogError("Burn tick patch failed: " + e);
            }
        }
    }
}
