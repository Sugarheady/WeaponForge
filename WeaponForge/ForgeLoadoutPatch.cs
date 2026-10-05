using System;
using System.Linq;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace WeaponForge
{
    // When the ship-select screen populates, make sure every custom
    // weapon is built + registered, then add each as a starting loadout
    // card. Building/registering is idempotent (ForgeRegistry), so this
    // is safe alongside the startup patch and also covers the case where
    // startup registration hadn't run yet.
    [HarmonyPatch(typeof(LoadoutSelector), "Populate")]
    public class ForgeLoadoutPatch
    {
        private static readonly ManualLogSource Log =
            ForgeLog.Source("WeaponForge");

        static void Prefix(LoadoutSelector __instance)
        {
            try
            {
                ForgeRegistry.BuildAll();

                ModuleRegistry registry;

                if (ServiceLocator.TryGet<ModuleRegistry>(out registry))
                {
                    ForgeRegistry.RegisterInto(registry);
                }

                var field =
                    typeof(LoadoutSelector).GetField(
                        "loadoutPool",
                        BindingFlags.NonPublic |
                        BindingFlags.Instance);

                if (field == null)
                    return;

                var pool =
                    field.GetValue(__instance) as LoadoutPool;

                if (pool == null)
                    return;

                // Loadout files first, so their cards sit straight after the
                // stock ones instead of past every single-weapon card. It
                // catches its own failures - a broken loadout file must not
                // cost the weapon cards below.
                ForgeLoadoutFiles.AddTo(pool, registry);

                foreach (ForgeEntry entry in ForgeRegistry.Entries)
                {
                    if (entry.module == null)
                        continue;

                    // Loot-only weapons don't appear as starting picks.
                    if (!entry.inStarter)
                        continue;

                    if (pool.loadouts.Any(
                        x => x != null && x.name == entry.loadoutName))
                    {
                        continue;
                    }

                    var baseLoadout =
                        JsonFieldMapper.FindAsset(
                            typeof(LoadoutTemplate),
                            entry.baseLoadoutName) as LoadoutTemplate;

                    if (baseLoadout == null)
                    {
                        Log.LogError(
                            "Base loadout '" + entry.baseLoadoutName +
                            "' not found for '" + entry.displayName +
                            "'");
                        continue;
                    }

                    var loadout =
                        ScriptableObject.Instantiate(baseLoadout);

                    loadout.hideFlags = HideFlags.None;
                    loadout.name = entry.loadoutName;
                    loadout.displayName = entry.displayName;
                    loadout.description = entry.description;
                    loadout.unlockingModules = new ModuleData[0];

                    // The loadout should carry ONLY the created weapon
                    // (plus the ship's embedded module). Clear all the
                    // base loadout's weapon/gadget slots so a gadget
                    // doesn't drag the base popper along on left-click.
                    loadout.primary = null;
                    loadout.secondary = null;
                    loadout.active1 = null;
                    loadout.active2 = null;
                    loadout.active3 = null;

                    // Place our module in the slot it was built for.
                    switch (entry.slot)
                    {
                        case "secondary":
                            loadout.secondary = entry.module;
                            break;
                        case "gadget1":
                            loadout.active1 = entry.module;
                            break;
                        case "gadget2":
                            loadout.active2 = entry.module;
                            break;
                        case "gadget3":
                            loadout.active3 = entry.module;
                            break;
                        default:
                            loadout.primary = entry.module;
                            break;
                    }

                    pool.loadouts.Add(loadout);

                    Log.LogInfo(
                        "Added loadout '" + entry.displayName + "'");
                }
            }
            catch (Exception e)
            {
                Log.LogError(
                    "Weapon Forge loadout injection failed: " + e);
            }
        }
    }
}
