using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx.Logging;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace WeaponForge
{
    // LOADOUTS - a run that starts with several things already equipped.
    //
    // His idea, R19 part 1 (2026-09-25): "Instead of just loading with one the
    // player can load with 1 to 5 things already equipped... a separate .json
    // file maybe in the weapon folder or its own loadout folder."
    //
    // THE GAME ALREADY HAS EXACTLY THIS SHAPE. A `LoadoutTemplate` carries the
    // ship's embedded module plus five slots - primary, secondary, active1-3 -
    // and `LoadoutTemplate.Apply` installs whichever are set; the card
    // (`LoadoutCard.Display`) lists all five as well. No stock card fills more
    // than one, and every Forge weapon card deliberately clears the other four
    // (ForgeLoadoutPatch). So a loadout file is the same card with several
    // slots filled, and nothing downstream changes: the modules are installed
    // at run start like any card's, then saved and restored by the game's own
    // grid memento. No sidecar, no patch on the run.
    //
    // ITS OWN FOLDER, "loadouts" beside "weapons". The weapons folder is read
    // without recursion and every file in it is treated as a weapon, so a
    // loadout left there is caught by WeaponBuilder and pointed here instead of
    // failing as "missing template".
    //
    // ★ NAMES RESOLVE THROUGH THE MODULE REGISTRY, NOT A SCENE-WIDE SEARCH.
    // The save stores an equipped module by id and restores it with
    // `registry.Get(id).DeepCopy()` - no null check - so a module that is not
    // registered would start the run fine and then break Continue. Anything
    // found in the registry is loadable by construction.
    public static class ForgeLoadoutFiles
    {
        private static readonly ManualLogSource Log =
            ForgeLog.Source("WeaponForge");

        // The card's asset name. Weapon cards are "Forge_" + name, so a
        // loadout and a weapon can never collide however the files are named.
        public const string CardPrefix = "ForgeLoadout_";

        // Lends the ship core. All six stock cards carry the SAME embedded
        // module (measured 2026-09-25), so which one is borrowed only matters
        // if a game update changes that.
        private const string BaseLoadout = "Starter_Popper";

        // The five slots, in the order the game's card lists them.
        private static readonly string[] SlotKeys =
            { "primary", "secondary", "gadget1", "gadget2", "gadget3" };

        private class Definition
        {
            public string fileName;
            public string name;
            public string displayName;
            public string description;
            public readonly string[] picks = new string[5];
        }

        // Parsed once per session. There is no hot reload anyway, and
        // re-reading on every visit to the ship-select screen would repeat
        // every file's warnings each time it opens.
        private static List<Definition> _definitions;

        private static bool _saidNoSlotRules;

        public static string LoadoutsFolder()
        {
            return Path.Combine(
                Path.GetDirectoryName(
                    Assembly.GetExecutingAssembly().Location),
                "loadouts");
        }

        // True for a file carrying any of the five slot keys at the top level.
        // WeaponBuilder asks this about a file with no "template", so a loadout
        // left in the weapons folder is named for what it is.
        public static bool LooksLikeLoadout(JObject root)
        {
            if (root == null)
                return false;

            foreach (JProperty p in root.Properties())
            {
                if (SlotIndex(p.Name) >= 0)
                    return true;
            }

            return false;
        }

        private static int SlotIndex(string key)
        {
            if (key == null)
                return -1;

            string k = key.Trim().ToLowerInvariant();

            for (int i = 0; i < SlotKeys.Length; i++)
            {
                if (k == SlotKeys[i])
                    return i;
            }

            return -1;
        }

        // Called from ForgeLoadoutPatch BEFORE the weapon cards, so loadouts
        // sit straight after the stock cards instead of at the far end of a
        // long row of single-weapon cards. Idempotent: the pool is an asset
        // that outlives the screen, and a card already in it is left alone.
        public static void AddTo(LoadoutPool pool, ModuleRegistry registry)
        {
            try
            {
                if (_definitions == null)
                    _definitions = ReadAll();

                if (_definitions.Count == 0)
                    return;

                LoadoutTemplate baseLoadout = null;

                foreach (Definition def in _definitions)
                {
                    string cardName = CardPrefix + def.name;

                    if (pool.loadouts.Any(
                        x => x != null && x.name == cardName))
                    {
                        continue;
                    }

                    if (baseLoadout == null)
                    {
                        baseLoadout =
                            JsonFieldMapper.FindAsset(
                                typeof(LoadoutTemplate),
                                BaseLoadout) as LoadoutTemplate;

                        if (baseLoadout == null)
                        {
                            Log.LogError(
                                "loadouts: the game's '" + BaseLoadout +
                                "' card was not found, and every loadout " +
                                "borrows the ship itself from it - so no " +
                                "loadout cards this time.");
                            return;
                        }
                    }

                    LoadoutTemplate card = Build(def, baseLoadout, registry);

                    if (card != null)
                        pool.loadouts.Add(card);
                }
            }
            catch (Exception e)
            {
                Log.LogError("Weapon Forge loadout files failed: " + e);
            }
        }

        private static LoadoutTemplate Build(
            Definition def,
            LoadoutTemplate baseLoadout,
            ModuleRegistry registry)
        {
            var got = new ModuleData[SlotKeys.Length];
            int asked = 0;
            int filled = 0;

            for (int i = 0; i < SlotKeys.Length; i++)
            {
                string pick = def.picks[i];

                if (string.IsNullOrEmpty(pick))
                    continue;

                asked++;

                string why;
                ModuleData module = Find(pick, registry, out why);

                if (module == null)
                {
                    Log.LogWarning(
                        def.fileName + ": " + SlotKeys[i] + " '" + pick +
                        "' " + why + " That slot starts EMPTY.");
                    continue;
                }

                string misfit = Misfit(module, i);

                if (misfit != null)
                {
                    Log.LogWarning(
                        def.fileName + ": " + SlotKeys[i] + " '" + pick +
                        "' " + misfit + " That slot starts EMPTY.");
                    continue;
                }

                got[i] = module;
                filled++;
            }

            // A card with nothing on it would start a run with no gun at all,
            // and look like a working card until you pressed fire.
            if (filled == 0)
            {
                Log.LogError(
                    def.fileName + ": none of the " + asked + " thing(s) " +
                    "it names could be equipped (the warnings above say " +
                    "why), so there is no card for '" + def.displayName +
                    "'.");
                return null;
            }

            var card = ScriptableObject.Instantiate(baseLoadout);

            card.hideFlags = HideFlags.None;
            card.name = CardPrefix + def.name;
            card.displayName = def.displayName;
            card.description = def.description ?? AutoDescription(got);
            card.unlockingModules = new ModuleData[0];

            card.primary = got[0];
            card.secondary = got[1];
            card.active1 = got[2];
            card.active2 = got[3];
            card.active3 = got[4];

            Log.LogInfo(
                "Added loadout '" + def.displayName + "' from " +
                def.fileName + " - " + Summary(got) +
                (filled < asked
                    ? " (" + filled + " of " + asked + " slots filled - " +
                      "the warnings above say why)"
                    : ""));

            return card;
        }

        // Three spellings, in this order:
        //   1. one of your weapon files, by its "name"
        //   2. a registered module by asset name - stock
        //      ("Module Weapon White Shotgun", or "White Shotgun"), yours
        //      ("Forge Module <name>"), or Module Forge's
        //   3. the name ON THE CARD ("SHOTGUN", "AIR MINE", your displayName),
        //      when exactly one module carries it
        //
        // Step 3 matters most to someone who has never opened a file: the
        // on-screen name is the only one they have seen. The same lesson as
        // the ingredient lookup, where Coral displays as "Fiber".
        private static ModuleData Find(
            string text,
            ModuleRegistry registry,
            out string why)
        {
            why = null;

            string key = text.Trim();

            foreach (ForgeEntry entry in ForgeRegistry.Entries)
            {
                if (entry == null || entry.module == null)
                    continue;

                if (Same(entry.loadoutName, "Forge_" + key) ||
                    Same(entry.loadoutName, key) ||
                    Same(entry.module.name, key))
                {
                    return entry.module;
                }
            }

            if (registry == null)
            {
                why = "is not one of your weapon files, and the game's " +
                      "module list was not ready to look anything else up.";
                return null;
            }

            List<ModuleData> all =
                registry.AllItems.Where(m => m != null).ToList();

            foreach (string candidate in new[]
                     { key, "Module Weapon " + key, "Module Active " + key })
            {
                ModuleData hit = all.FirstOrDefault(m => Same(m.name, candidate));

                if (hit != null)
                    return hit;
            }

            // The tuned copies of a minion's own gun are registered too (so a
            // save with that minion alive loads), and each keeps the stock
            // module's displayName - so without this, "GEL MISSILE" would be
            // ambiguous the moment any minion carried one.
            var arms = new HashSet<ModuleData>(ForgeMinionArms.Made);

            List<ModuleData> shown =
                all.Where(m => !arms.Contains(m) && Same(m.displayName, key))
                    .ToList();

            if (shown.Count == 1)
                return shown[0];

            if (shown.Count > 1)
            {
                why = "is the name on " + shown.Count + " different cards (" +
                      string.Join(", ",
                          shown.Select(m => m.name).ToArray()) +
                      "), so it is ambiguous - use one of those instead.";
                return null;
            }

            why = "was not found. " + Candidates();
            return null;
        }

        // What a failed name could have been. A lookup that fails silently
        // costs a test round; one that prints the candidates costs a glance.
        private static string Candidates()
        {
            var yours = new List<string>();

            foreach (ForgeEntry entry in ForgeRegistry.Entries)
            {
                if (entry == null || entry.module == null ||
                    entry.loadoutName == null)
                {
                    continue;
                }

                yours.Add(
                    entry.loadoutName.StartsWith("Forge_")
                        ? entry.loadoutName.Substring("Forge_".Length)
                        : entry.loadoutName);
            }

            yours.Sort(StringComparer.OrdinalIgnoreCase);

            return "A slot takes one of your weapon files' \"name\" (" +
                   (yours.Count > 0
                       ? string.Join(", ", yours.ToArray())
                       : "none are loaded") +
                   "), a stock module's name like \"Module Weapon White " +
                   "Shotgun\" or \"Module Active Purple AirMines\", or the " +
                   "name on its card like \"SHOTGUN\". Loadout Builder.html " +
                   "lists every stock one.";
        }

        // The game's own rule: each slot's ModuleSlotType lists the module
        // types it takes (primary/secondary = Weapon, the three keys = Active).
        // Checked here because `ModuleGrid.Install` does not check at all - it
        // would happily put a gadget where the primary gun goes.
        private static string Misfit(ModuleData module, int slot)
        {
            bool gadgetSlot = slot >= 2;

            if (Fits(module.moduleType, gadgetSlot))
                return null;

            bool fitsOther = Fits(module.moduleType, !gadgetSlot);

            if (!fitsOther)
            {
                return "is not a weapon or a gadget (" +
                       (module.moduleType != null
                           ? module.moduleType.displayName
                           : "no module type") +
                       ") - a loadout fills the five weapon and gadget " +
                       "slots only, not the rest of the grid.";
            }

            string moveTo =
                gadgetSlot ? "primary or secondary" : "gadget1, gadget2 or gadget3";

            ForgeEntry entry =
                ForgeRegistry.Entries.FirstOrDefault(
                    e => e != null && e.module == module);

            if (entry != null)
            {
                return "is one of your weapons, and its own file builds it " +
                       "for \"slot\": \"" + entry.slot + "\", which makes it " +
                       (gadgetSlot ? "a weapon-slot module" : "a gadget") +
                       " - it cannot go in " + SlotKeys[slot] + ". Move it " +
                       "to " + moveTo + " here, or change the \"slot\" in " +
                       "its weapon file.";
            }

            return "is " + (gadgetSlot ? "a weapon" : "a gadget") +
                   ", and " + SlotKeys[slot] + " only takes " +
                   (gadgetSlot ? "gadgets" : "weapons") + " - move it to " +
                   moveTo + ".";
        }

        // Does a slot of this kind take a module of this type?
        //
        // ★ THE GAME'S RULE IS NOT LOADED YET AT SHIP SELECT, which is where
        // this runs. `ModuleSlotType.Weapon` / `.Active` are static properties
        // filled by `EnumsCollection.Initialize`, a service the GAME scene
        // installs - the main menu never does. So R19 test 282 met a null rule,
        // warned once, and waved every file through: a primary weapon named in
        // gadget2 was installed on key 2, where it can never fire. A check that
        // skips itself in the only place it is ever asked is not a check.
        //
        // Two fallbacks, in order. The slot-type ASSETS, if anything has loaded
        // them, picked by the same name rule `ModuleSlotType.SetValues` uses.
        // Then the module type's own asset name, measured off the exported
        // assets: the Weapon slot lists exactly `ModuleType Weapon` and the
        // Active slot exactly `ModuleType Active`. Module types are always
        // loaded here, because every ModuleData in the registry references one.
        private static bool Fits(ModuleType type, bool gadgetSlot)
        {
            List<ModuleType> rule = SlotRule(gadgetSlot);

            if (rule != null)
                return type != null && rule.Contains(type);

            if (!_saidNoSlotRules)
            {
                _saidNoSlotRules = true;

                Log.LogInfo(
                    "loadouts: the game's slot rules only load once a run " +
                    "starts, so at ship select the weapon/gadget check reads " +
                    "each module's type name instead (a weapon slot takes " +
                    "'ModuleType Weapon', a gadget slot 'ModuleType Active' - " +
                    "the same rule the game applies).");
            }

            string name = (type != null) ? (type.name ?? "").Trim() : "";

            if (string.Equals(name, WeaponTypeName, StringComparison.OrdinalIgnoreCase))
                return !gadgetSlot;

            if (string.Equals(name, ActiveTypeName, StringComparison.OrdinalIgnoreCase))
                return gadgetSlot;

            // Every other type (augmentations, passives, boosters, cores) goes
            // in neither - which is the game's answer too.
            return false;
        }

        private const string WeaponTypeName = "ModuleType Weapon";
        private const string ActiveTypeName = "ModuleType Active";

        private static List<ModuleType> SlotRule(bool gadgetSlot)
        {
            ModuleSlotType set =
                gadgetSlot ? ModuleSlotType.Active : ModuleSlotType.Weapon;

            if (set != null && set.compatibleModuleTypes != null)
                return set.compatibleModuleTypes;

            try
            {
                string want = gadgetSlot ? "active" : "weapon";

                foreach (ModuleSlotType t in
                         Resources.FindObjectsOfTypeAll<ModuleSlotType>())
                {
                    if (t == null || t.compatibleModuleTypes == null)
                        continue;

                    if ((t.name ?? "").IndexOf(
                            want, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return t.compatibleModuleTypes;
                    }
                }
            }
            catch (Exception)
            {
                // Fall through to the type names; a lookup must never cost
                // the card.
            }

            return null;
        }

        private static List<Definition> ReadAll()
        {
            var list = new List<Definition>();

            string folder = LoadoutsFolder();

            if (!Directory.Exists(folder))
                Directory.CreateDirectory(folder);

            string[] files =
                Directory.GetFiles(folder, "*.json")
                    .OrderBy(x => x)
                    .ToArray();

            if (files.Length == 0)
            {
                Log.LogInfo(
                    "loadouts: none. A .json in the \"loadouts\" folder " +
                    "next to WeaponForge.dll starts a run with up to five " +
                    "weapons and gadgets equipped - Loadout Builder.html " +
                    "makes one.");
                return list;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string file in files)
            {
                try
                {
                    Definition def = Read(file);

                    if (def == null)
                        continue;

                    if (!seen.Add(def.name))
                    {
                        Log.LogWarning(
                            def.fileName + ": another loadout file is " +
                            "already called '" + def.name + "', so this " +
                            "one is skipped. Give each loadout its own " +
                            "\"name\".");
                        continue;
                    }

                    list.Add(def);
                }
                catch (Exception e)
                {
                    Log.LogError(
                        "Failed to read loadout loadouts/" +
                        Path.GetFileName(file) + ": " + e.Message);
                }
            }

            return list;
        }

        private static Definition Read(string file)
        {
            string fileName = "loadouts/" + Path.GetFileName(file);

            JObject root = JObject.Parse(File.ReadAllText(file));

            // A file in the wrong folder, named for what it is.
            if (root["template"] != null)
            {
                Log.LogWarning(
                    fileName + " looks like a WEAPON file - it has a " +
                    "\"template\". It belongs in the \"weapons\" folder; " +
                    "this is \"loadouts\". Skipped.");
                return null;
            }

            if (root["effects"] != null)
            {
                Log.LogWarning(
                    fileName + " looks like a MODULE FORGE module - it has " +
                    "\"effects\". It belongs in the \"modules\" folder next " +
                    "to ModuleForge.dll. Skipped.");
                return null;
            }

            var def = new Definition { fileName = fileName };
            string name = null;
            var unknown = new List<string>();

            foreach (JProperty p in root.Properties())
            {
                int slot = SlotIndex(p.Name);
                string key = p.Name.Trim().ToLowerInvariant();

                if (slot >= 0)
                {
                    if (p.Value.Type == JTokenType.Null)
                        continue;

                    if (p.Value.Type != JTokenType.String)
                    {
                        Log.LogWarning(
                            fileName + ": \"" + p.Name + "\" should be one " +
                            "name in quotes, like \"Module Weapon White " +
                            "Popper\" - that slot is left empty.");
                        continue;
                    }

                    string value = ((string)p.Value).Trim();

                    if (value.Length == 0)
                        continue;

                    if (def.picks[slot] != null)
                    {
                        Log.LogWarning(
                            fileName + ": \"" + SlotKeys[slot] + "\" is " +
                            "named twice - the second one ('" + value +
                            "') wins.");
                    }

                    def.picks[slot] = value;
                }
                else if (key == "name")
                {
                    name = Text(p.Value);
                }
                else if (key == "displayname")
                {
                    def.displayName = Text(p.Value);
                }
                else if (key == "description")
                {
                    def.description = Text(p.Value);
                }
                else
                {
                    unknown.Add(p.Name);
                }
            }

            if (unknown.Count > 0)
            {
                Log.LogWarning(
                    fileName + ": ignored " +
                    (unknown.Count == 1 ? "a key" : unknown.Count + " keys") +
                    " a loadout does not read: " +
                    string.Join(", ",
                        unknown.Select(u => "\"" + u + "\"").ToArray()) +
                    ". A loadout file takes \"name\", \"displayName\", " +
                    "\"description\" and the five slots \"primary\", " +
                    "\"secondary\", \"gadget1\", \"gadget2\", \"gadget3\" - " +
                    "each slot is ONE name.");
            }

            if (string.IsNullOrEmpty(name) || name.Trim().Length == 0)
            {
                Log.LogError(
                    fileName + ": missing required \"name\" (the loadout's " +
                    "own id). Skipped.");
                return null;
            }

            if (name != name.Trim())
            {
                Log.LogWarning(
                    fileName + ": \"name\" had whitespace at the start or " +
                    "end ('" + name + "'). Trimmed to '" + name.Trim() + "'.");
            }

            def.name = name.Trim();

            if (string.IsNullOrEmpty(def.displayName))
                def.displayName = def.name.ToUpperInvariant();

            if (def.picks.All(string.IsNullOrEmpty))
            {
                Log.LogError(
                    fileName + ": names nothing to equip - fill at least one " +
                    "of \"primary\", \"secondary\", \"gadget1\", \"gadget2\", " +
                    "\"gadget3\". Skipped.");
                return null;
            }

            return def;
        }

        private static string Text(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null)
                return null;

            return token.Type == JTokenType.String
                ? (string)token
                : token.ToString();
        }

        // With no "description", the card says what it carries. The icons
        // show it too, but a name reads faster than five small pictures.
        private static string AutoDescription(ModuleData[] got)
        {
            string[] names =
                got.Where(m => m != null)
                    .Select(Shown)
                    .ToArray();

            return "Starts with " + string.Join(" + ", names) + ".";
        }

        private static string Summary(ModuleData[] got)
        {
            var parts = new List<string>();

            for (int i = 0; i < got.Length; i++)
            {
                if (got[i] != null)
                {
                    parts.Add(
                        SlotKeys[i] + " = " + Shown(got[i]) +
                        " [" + got[i].name + "]");
                }
            }

            return string.Join(", ", parts.ToArray());
        }

        private static string Shown(ModuleData m)
        {
            return string.IsNullOrEmpty(m.displayName) ? m.name : m.displayName;
        }

        private static bool Same(string a, string b)
        {
            return a != null && b != null &&
                   string.Equals(a.Trim(), b.Trim(),
                       StringComparison.OrdinalIgnoreCase);
        }
    }
}
