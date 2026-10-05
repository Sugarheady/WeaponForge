using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx.Logging;
using UnityEngine;

namespace WeaponForge
{
    // One built weapon: the configured module (already carrying its
    // weapon + overrides) plus the info needed to make a loadout card.
    // The module is a WeaponModuleData for primary/secondary weapons or
    // a WeaponBasedActiveModuleData for gadgets (1/2/3 slots).
    public class ForgeEntry
    {
        public string loadoutName;
        public string displayName;
        public string description;
        public string baseLoadoutName;
        public ModuleData module;
        public string slot;   // primary | secondary | gadget1|2|3
        public bool inStarter; // show in the new-game loadout list
        public bool inLoot;    // can drop from crates/loot
        public float lootWeight;

        // Which module pools this weapon may drop from, as canonical
        // DropGroup asset names. null or empty = every pool (the old
        // all-or-nothing behaviour, still the default).
        public string[] lootGroups;
        public bool inShop;    // can be bought from the shop
        public float shopPrice;

        // Flat money ADDED to the price each time it is bought - the game's
        // own escalation model (`ShopItem.IncreasePrice`). 0 = a fixed price.
        //
        // Module Forge has had this since it shipped and Weapon Forge wrote a
        // hard-coded 0, so a Forge WEAPON could never get more expensive while
        // a Forge MODULE could. Found while researching ingredient costs and
        // closed in the same change, per the duplicate-by-default rule.
        public float shopPriceIncrement;

        // "shopCost" / "shopCostIncrement": currencies BEYOND money, kept as
        // the authored name and amount and resolved at injection time, when
        // the game's assets are live. See ForgeShopCost.
        public ForgeShopCost.Cost[] shopCost;
        public ForgeShopCost.Cost[] shopCostIncrement;

        // "shopUnlock": ingredients that must have been picked up at least
        // once before this appears in the shop at all. Independent of the
        // price, which was his call - the stock game always pairs the two.
        public string[] shopUnlock;

        public int shopUnlockLevel; // stations to unlock first
    }

    // Central store for Weapon Forge. Builds each JSON weapon's module
    // ONCE and keeps the canonical instance, then makes sure that
    // instance is registered in the game's ModuleRegistry.
    //
    // Registration is the fix for the save/load crash: the game saves
    // an equipped module by its string Id, and on load resolves it with
    // ModuleRegistry.Get(id).DeepCopy(). If our module isn't registered,
    // Get returns null and the load throws / hangs. We must register the
    // FULLY-CONFIGURED module (restored weapons are DeepCopy'd from it),
    // and we must do it at startup so "Continue" works without ever
    // opening the ship-select screen.
    public static class ForgeRegistry
    {
        private static readonly ManualLogSource Log =
            ForgeLog.Source("WeaponForge");

        private static readonly List<ForgeEntry> _entries =
            new List<ForgeEntry>();

        private static readonly HashSet<string> _builtNames =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public static IEnumerable<ForgeEntry> Entries
        {
            get { return _entries; }
        }

        public static string WeaponsFolder()
        {
            return Path.Combine(
                Path.GetDirectoryName(
                    Assembly.GetExecutingAssembly().Location),
                "weapons");
        }

        // Scan the weapons folder and build any weapon not built yet.
        // Idempotent and re-runnable: already-built weapons are skipped,
        // so calling this from both startup and the loadout screen is
        // safe (and lets a weapon build later if assets weren't ready
        // the first time).
        public static void BuildAll()
        {
            // Custom art has to exist before any weapon asks for it by
            // name. Loads once; cheap when the folder is empty.
            ForgeSpriteLibrary.LoadAll();

            // Same for custom audio. WAV clips are ready immediately; ogg/mp3
            // decode in the background and attach themselves to their
            // registered Sfx when they land, so weapon building never waits.
            ForgeSoundLibrary.LoadAll();

            string folder = WeaponsFolder();

            if (!Directory.Exists(folder))
            {
                Directory.CreateDirectory(folder);
                StarterFiles.Write(folder);
            }

            string[] files =
                Directory.GetFiles(folder, "*.json")
                    .OrderBy(x => x)
                    .ToArray();

            foreach (string file in files)
            {
                try
                {
                    ForgeEntry entry =
                        WeaponBuilder.BuildModule(file, _builtNames);

                    if (entry != null)
                    {
                        _entries.Add(entry);
                        _builtNames.Add(entry.loadoutName);
                    }
                }
                catch (Exception e)
                {
                    Log.LogError(
                        "Failed to build weapon from " +
                        Path.GetFileName(file) + ": " + e);
                }
            }

            // Now that every weapon exists, hook up the subEmitter
            // references. Doing this last means a sub can live in a file
            // that sorts after the weapon using it.
            WeaponBuilder.ResolvePendingSubEmitters();
        }

        // Make sure every built module is present in the registry the
        // save system reads. Cheap; safe to call on every startup /
        // menu open (handles the game swapping registry instances).
        public static void RegisterInto(ModuleRegistry registry)
        {
            if (registry == null)
                return;

            FieldInfo itemListField =
                typeof(ScriptableObjectRegistry<ModuleData, string>)
                    .GetField(
                        "itemList",
                        BindingFlags.NonPublic |
                        BindingFlags.Instance);

            if (itemListField == null)
            {
                Log.LogError(
                    "ModuleRegistry itemList field not found - " +
                    "game version may have changed.");
                return;
            }

            IList itemList =
                itemListField.GetValue(registry) as IList;

            if (itemList == null)
                return;

            // Build a set of ids already present (read itemList
            // directly rather than Get(), which needs the dictionary
            // to have been initialized first).
            var presentIds =
                new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (object existing in itemList)
            {
                var md = existing as ModuleData;

                if (md != null && md.Id != null)
                    presentIds.Add(md.Id);
            }

            bool changed = false;

            foreach (ForgeEntry entry in _entries)
            {
                if (entry.module == null)
                    continue;

                if (presentIds.Contains(entry.module.Id))
                    continue;

                itemList.Add(entry.module);
                presentIds.Add(entry.module.Id);
                changed = true;

                Log.LogInfo(
                    "Registered module '" + entry.module.Id +
                    "' in ModuleRegistry");
            }

            // Modules that are not weapons anyone can equip, but which DO end
            // up installed on a unit - the tuned copies of a minion prefab's
            // own gun. They need registering for the same save-load reason and
            // no other: a minion is a savable entity, its grid is stored as a
            // list of module ids, and `Module.Memento.Restore` does
            // `registry.Get(id).DeepCopy()` with no null check. An unregistered
            // id is a NullReferenceException inside the game's load path, which
            // presents as a save that will not open.
            //
            // Kept out of _entries deliberately, so these never appear in loot
            // pools, the shop, or the loadout screen.
            foreach (ModuleData extra in ForgeMinionArms.Made)
            {
                if (extra == null || presentIds.Contains(extra.Id))
                    continue;

                itemList.Add(extra);
                presentIds.Add(extra.Id);
                changed = true;

                Log.LogInfo(
                    "Registered minion weapon '" + extra.Id +
                    "' in ModuleRegistry (needed so a save with that minion " +
                    "alive can be loaded)");
            }

            if (changed)
            {
                // Rebuild the id -> module dictionary so Get() sees
                // our additions.
                registry.Initialize();
            }
        }
    }
}
