using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace WeaponForge
{
    // The game's module drop pools, and the friendly names a weapon file uses
    // to pick between them.
    //
    // Mapped from the assets: a crate's DropTable rolls exactly one
    // DropTableWeightedGroup, and those groups are split by element already -
    // which is what makes per-crate targeting possible at all. Only FIVE pools
    // are ever actually rolled by the game; two more exist but are wired with
    // useGroup false, so nothing draws from them.
    public static class ForgeLootPools
    {
        public const string White = "DropGroup Modules Crate White";
        public const string Caps = "DropGroup Modules Crate Caps";
        public const string Purple = "DropGroup Modules Crate Purple";
        public const string Tech = "DropGroup Modules Crate Tech";
        public const string Generic = "DropGroup Modules Crate";

        // Referenced by a DropTable but with useGroup FALSE, so the game never
        // draws from them on its own.
        public const string Level2 = "DropGroup Modules Crate Level 2";
        public const string Box = "DropGroup Box";

        // Crate Money has no module pool of ANY kind, so there is nothing to
        // revive - we create this one and graft it on.
        //
        // *** This name is shared with Module Forge on purpose. *** Both mods
        // create-or-find a pool by this name, so when both are installed they
        // fill ONE pool that the crate rolls once. Rename it here and a player
        // with both mods gets two module drops out of every Money crate.
        public const string Money = "Forge Modules Crate Money";

        // Pools the game will not roll by itself, and the DropTable that has to
        // gain an extra item before they can drop. Grafting is ADDITIVE: a new
        // DropTableItem is appended, and nothing already in the table is
        // touched, so these crates keep every one of their normal contents and
        // simply gain a module on top.
        private static readonly Dictionary<string, string> _graftInto =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { Level2, "DropTable Crate Level2" },
                { Money, "DropTable Crate Money" }
            };

        public static bool NeedsGraft(string pool, out string tableName)
        {
            tableName = null;

            return pool != null &&
                   _graftInto.TryGetValue(pool, out tableName);
        }

        // Is this a pool that only exists because it was grafted? Such a pool
        // can legitimately be EMPTY of stock module entries, so the injector
        // must not mistake it for a resource-only pool and skip it.
        //
        // Asked by name rather than "did I create it", because Module Forge
        // does the same grafting and may have created it first.
        public static bool IsGraftPool(string poolName)
        {
            string ignored;
            return NeedsGraft(poolName, out ignored);
        }

        // Which pool should this table gain, if any weapon asked for it?
        public static string GraftPoolFor(string tableName)
        {
            foreach (var pair in _graftInto)
            {
                if (string.Equals(
                        pair.Value, tableName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return pair.Key;
                }
            }

            return null;
        }

        // friendly name -> canonical asset name
        private static readonly Dictionary<string, string> _alias =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "white", White },
                { "stamina", White },
                { "caps", Caps },
                { "orange", Caps },
                { "purple", Purple },
                { "tech", Tech },
                { "generic", Generic },
                { "queen", Generic },
                { "level2", Level2 },
                { "level 2", Level2 },
                { "money", Money },
                { "box", Box },
            };

        public static readonly string[] Live =
            { White, Caps, Purple, Tech, Generic };

        public static bool IsLive(string canonical)
        {
            for (int i = 0; i < Live.Length; i++)
            {
                if (string.Equals(
                        Live[i], canonical, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        // Usable as a lootFrom target: either the game already rolls it, or we
        // can graft it on.
        public static bool IsSupported(string canonical)
        {
            string ignored;
            return IsLive(canonical) || NeedsGraft(canonical, out ignored);
        }

        // Accepts a friendly name ("white") or the full asset name
        // ("DropGroup Modules Crate White").
        public static bool TryResolve(string text, out string canonical)
        {
            canonical = null;

            if (string.IsNullOrEmpty(text))
                return false;

            string key = text.Trim();

            if (_alias.TryGetValue(key, out canonical))
                return true;

            if (key.StartsWith("DropGroup", StringComparison.OrdinalIgnoreCase))
            {
                canonical = key;
                return true;
            }

            return false;
        }

        public static string FriendlyList()
        {
            return "white, caps, purple, tech, queen (the Queen's own pool), " +
                   "money or level2 (both get a module roll added), " +
                   "or \"all\"";
        }
    }

    // Makes loot-enabled Forge weapons drop from crates. There is no
    // global "droppable" flag in the game: a module only drops if it's
    // a member of a DropTableWeightedGroup that a crate's DropTable
    // references. So just before any table-based loot roll, we add our
    // loot weapons into that table's MODULE groups (the crate module
    // pools). We only touch groups that already contain module entries,
    // so resource/prefab-only tables (e.g. enemy drops) are untouched.
    //
    // Hooking SelectLoot (rather than editing group assets at startup)
    // guarantees the groups are loaded - they arrive as the argument.
    [HarmonyPatch(typeof(LootSelector), "SelectLoot")]
    public class ForgeLootPatch
    {
        private static readonly ManualLogSource Log =
            ForgeLog.Source("WeaponForge");

        // Groups we've already augmented, so we add our modules once.
        private static readonly HashSet<DropTableWeightedGroup> _done =
            new HashSet<DropTableWeightedGroup>();

        private static FieldInfo _groupField;
        private static FieldInfo _useGroupField;

        static void Prefix(DropTable dropTable)
        {
            try
            {
                if (dropTable == null || dropTable.items == null)
                    return;

                // Crates the game gives no module roll at all (Money, Level 2)
                // gain one here - but only if a weapon actually asked for that
                // crate, so an untargeted crate is left exactly as the game
                // shipped it. Must run BEFORE the loop below: it appends to the
                // very list that loop walks.
                Graft(dropTable);

                if (_groupField == null)
                {
                    _groupField =
                        typeof(DropTableItem).GetField(
                            "group",
                            BindingFlags.NonPublic |
                            BindingFlags.Instance);

                    _useGroupField =
                        typeof(DropTableItem).GetField(
                            "useGroup",
                            BindingFlags.NonPublic |
                            BindingFlags.Instance);
                }

                if (_groupField == null)
                    return;

                foreach (DropTableItem item in dropTable.items)
                {
                    // useGroup is the flag that decides whether the group is
                    // ROLLED. Unity serializes the group reference even when it
                    // is off (it is a ConditionalField), so plenty of tables
                    // point at a pool they never draw from - the Level 2 crate,
                    // every Box, and ten enemy tables all do. Injecting into
                    // those achieved nothing and made the log claim otherwise.
                    if (_useGroupField != null)
                    {
                        object flag = _useGroupField.GetValue(item);

                        if (flag is bool && !(bool)flag)
                            continue;
                    }

                    var group =
                        _groupField.GetValue(item)
                            as DropTableWeightedGroup;

                    if (group != null)
                        Augment(group);
                }
            }
            catch (Exception e)
            {
                Log.LogError("Loot injection failed: " + e);
            }
        }

        // Tables we have already extended, and the pools we created.
        private static readonly HashSet<DropTable> _grafted =
            new HashSet<DropTable>();

        private static readonly Dictionary<string, DropTableWeightedGroup>
            _madePools =
                new Dictionary<string, DropTableWeightedGroup>(
                    StringComparer.OrdinalIgnoreCase);

        // Give a crate a module roll it does not normally have.
        //
        // ADDITIVE ON PURPOSE. Crate Money is four fixed prefab drops with no
        // group anywhere, and Crate Level 2's one group slot is already spent
        // on an Ingredient - so there is nothing to simply "switch on" in
        // either. Rather than repurposing an existing entry (which would take
        // something away), a brand new DropTableItem is appended: every normal
        // drop survives and the crate just also yields a module.
        private static void Graft(DropTable dropTable)
        {
            if (_grafted.Contains(dropTable))
                return;

            string pool = ForgeLootPools.GraftPoolFor(dropTable.name);

            if (pool == null)
                return;

            // EXPLICIT opt-in only. Targets() treats "no lootFrom" as "every
            // pool", which is right for injecting but wrong here: it would mean
            // any loot weapon at all silently altered Money and Level 2 crates
            // for the whole run. Grafting has to be something the file asked
            // for by name.
            bool wanted = false;

            foreach (ForgeEntry entry in ForgeRegistry.Entries)
            {
                if (!entry.inLoot || entry.module == null ||
                    entry.lootGroups == null || entry.lootGroups.Length == 0)
                {
                    continue;
                }

                if (Targets(entry, pool))
                {
                    wanted = true;
                    break;
                }
            }

            if (!wanted)
                return;

            _grafted.Add(dropTable);

            DropTableWeightedGroup group = ResolvePool(pool);

            if (group == null)
                return;

            // With Module Forge installed, IT may have grafted this very table
            // already - both mods target the same pool names on purpose. A
            // second DropTableItem would make the crate roll modules TWICE,
            // which reads as a duplicate-drop bug. Share the existing roll
            // instead; Augment below still adds our weapons to the same pool.
            if (TableAlreadyRolls(dropTable, group))
            {
                Log.LogInfo(
                    "'" + dropTable.name + "' already has a module roll from '" +
                    group.name + "' (Module Forge got there first) - sharing it " +
                    "rather than adding a second one.");
                return;
            }

            if (!AppendGroupRoll(dropTable, group))
                return;

            Log.LogInfo(
                "Gave '" + dropTable.name + "' a module roll from '" +
                group.name + "' - the crate keeps everything it normally " +
                "drops and gains a module on top." +
                (pool == ForgeLootPools.Level2
                    ? " Side effect: this also revives the 5 stock " +
                      "regen/generator modules in that pool, which the game " +
                      "otherwise never rolls."
                    : string.Empty));
        }

        // Does this table already roll the given pool? Covers both a stock
        // entry and one the other mod appended a moment ago.
        private static bool TableAlreadyRolls(
            DropTable dropTable, DropTableWeightedGroup group)
        {
            if (_groupField == null)
            {
                _groupField =
                    typeof(DropTableItem).GetField(
                        "group",
                        BindingFlags.NonPublic | BindingFlags.Instance);

                _useGroupField =
                    typeof(DropTableItem).GetField(
                        "useGroup",
                        BindingFlags.NonPublic | BindingFlags.Instance);
            }

            if (_groupField == null)
                return false;

            foreach (DropTableItem item in dropTable.items)
            {
                if (_useGroupField != null)
                {
                    object flag = _useGroupField.GetValue(item);

                    if (flag is bool && !(bool)flag)
                        continue;
                }

                var existing =
                    _groupField.GetValue(item) as DropTableWeightedGroup;

                if (existing == group)
                    return true;
            }

            return false;
        }

        // The existing asset where there is one, otherwise a pool of our own.
        //
        // FindObjectsOfTypeAll sees HideAndDontSave objects, so if Module Forge
        // created the Money pool first this finds THAT instance and both mods
        // end up filling one pool - which is the whole point of sharing the
        // name.
        private static DropTableWeightedGroup ResolvePool(string pool)
        {
            var existing =
                JsonFieldMapper.FindAsset(
                    typeof(DropTableWeightedGroup), pool)
                    as DropTableWeightedGroup;

            if (existing != null)
                return existing;

            DropTableWeightedGroup made;

            if (_madePools.TryGetValue(pool, out made) && made != null)
                return made;

            made = ScriptableObject.CreateInstance<DropTableWeightedGroup>();
            made.name = pool;
            made.hideFlags = HideFlags.HideAndDontSave;

            _madePools[pool] = made;
            return made;
        }

        // Build and append the DropTableItem. Its fields are private
        // [SerializeField] on a STRUCT, so it is boxed, filled, then unboxed
        // back into the list.
        private static bool AppendGroupRoll(
            DropTable dropTable, DropTableWeightedGroup group)
        {
            try
            {
                var t = typeof(DropTableItem);
                var f = BindingFlags.NonPublic | BindingFlags.Instance;

                object boxed = new DropTableItem();

                // Probability + 1 = "always exactly one", which is how every
                // crate that DOES roll modules is set up.
                t.GetField("countSource", f)
                    .SetValue(boxed, DropTableItemCountSource.Probability);
                t.GetField("probability", f).SetValue(boxed, 1f);
                t.GetField("useGroup", f).SetValue(boxed, true);
                t.GetField("group", f).SetValue(boxed, group);

                dropTable.items.Add((DropTableItem)boxed);
                return true;
            }
            catch (Exception e)
            {
                Log.LogError(
                    "Could not add a module roll to '" + dropTable.name +
                    "': " + e);
                return false;
            }
        }

        private static bool Targets(ForgeEntry entry, string groupName)
        {
            if (entry.lootGroups == null || entry.lootGroups.Length == 0)
                return true;

            for (int i = 0; i < entry.lootGroups.Length; i++)
            {
                if (string.Equals(
                        entry.lootGroups[i],
                        groupName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static void Augment(DropTableWeightedGroup group)
        {
            if (!_done.Add(group))
                return;   // already processed this group

            var dist = group.itemDistribution;

            if (dist == null)
                return;

            // Only inject into groups that are MODULE pools, and skip
            // any of our modules already present.
            bool hasModuleEntry = false;
            var present =
                new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var distItem in dist.Items)
            {
                if (distItem.Value.droppableType !=
                    DroppabbleType.Module)
                {
                    continue;
                }

                hasModuleEntry = true;

                if (distItem.Value.module != null &&
                    distItem.Value.module.Id != null)
                {
                    present.Add(distItem.Value.module.Id);
                }
            }

            // A grafted pool starts EMPTY, so it has no module entry to
            // recognise yet - it exists precisely to be filled here. Asked by
            // pool name rather than "did I create it", because with both mods
            // installed Module Forge may have created it. Every other
            // empty-of-modules pool is a resource/prefab one and is left alone.
            if (!hasModuleEntry && !ForgeLootPools.IsGraftPool(group.name))
                return;   // resource/prefab pool - leave it alone

            int added = 0;

            foreach (ForgeEntry entry in ForgeRegistry.Entries)
            {
                if (!entry.inLoot || entry.module == null)
                    continue;

                if (present.Contains(entry.module.Id))
                    continue;

                // No list = every pool, which is what "loot" meant before
                // lootFrom existed.
                if (!Targets(entry, group.name))
                    continue;

                dist.Add(
                    new DroppabbleItem
                    {
                        droppableType = DroppabbleType.Module,
                        module = entry.module
                    },
                    entry.lootWeight);

                added++;
            }

            if (added > 0)
            {
                Log.LogInfo(
                    "Added " + added +
                    " Forge weapon(s) to drop group '" +
                    group.name + "'");
            }
        }
    }
}
