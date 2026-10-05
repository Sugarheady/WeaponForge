using System;
using HarmonyLib;
using BepInEx.Logging;

namespace WeaponForge
{
    // Build and register the custom modules at startup, right after the
    // game installs its services. This must happen before any save is
    // loaded — the game restores an equipped module with
    // ModuleRegistry.Get(id).DeepCopy(), so an unregistered Forge
    // module makes "Continue" throw / hang. Registering here (rather
    // than only when the ship-select screen opens) means loading a save
    // works even if that screen is never visited.
    [HarmonyPatch(typeof(ServiceContainer), "InstallServices")]
    public class ForgeStartupPatch
    {
        private static readonly ManualLogSource Log =
            ForgeLog.Source("WeaponForge");

        static void Postfix()
        {
            try
            {
                ModuleRegistry registry;

                if (!ServiceLocator.TryGet<ModuleRegistry>(out registry) ||
                    registry == null)
                {
                    // Services not fully installed yet — a later call
                    // (or the loadout screen) will handle registration.
                    return;
                }

                ForgeRegistry.BuildAll();
                ForgeRegistry.RegisterInto(registry);
            }
            catch (Exception e)
            {
                Log.LogError(
                    "Weapon Forge startup registration failed: " + e);
            }
        }
    }
}
