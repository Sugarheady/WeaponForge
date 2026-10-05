using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace WeaponForge
{
    // Makes shop-enabled Forge weapons purchasable. PUNK gates the shop
    // by a station threshold: a counter (unlockedShopCount, also exposed
    // as UnlockedStationCount) is BOTH "stations unlocked" and the shop
    // tier index. Each station unlock rolls one tier of
    // ShopUpgradeData.perLevelData[N], so a weapon placed at tier N can
    // only appear after the player has unlocked N stations.
    //
    // We inject at a RunData.Initialize prefix - the single safe choke
    // point where both ShopUpgradeData and ShopItemsConfig are live and
    // populated, and which runs before the game's first shop roll.
    //
    // Two things are required per weapon:
    //   1. A ShopItemConfig (price) keyed by the module's Id. WITHOUT it
    //      the shop throws a NullReferenceException when it tries to
    //      create the item - so the config is done first and the pool
    //      entry is skipped if the config can't be built.
    //   2. A dedicated single-module PerLevelGroup (probablity 1) added
    //      to perLevelData[shopUnlockLevel], so the weapon appears
    //      deterministically at its tier rather than competing in the
    //      big weighted pool.
    [HarmonyPatch(typeof(RunData), "Initialize")]
    public class ForgeShopPatch
    {
        private static readonly ManualLogSource Log =
            ForgeLog.Source("WeaponForge");

        // Module ids already injected this session (the shop assets
        // persist across runs, so inject once).
        private static readonly HashSet<string> _injected =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private static int _nextLineNumber = 9000;

        static void Prefix()
        {
            try
            {
                ForgeRegistry.BuildAll();

                ShopUpgradeData shopData;
                ShopItemsConfig config;

                if (!ServiceLocator.TryGet<ShopUpgradeData>(out shopData) ||
                    shopData == null)
                {
                    return;
                }

                if (!ServiceLocator.TryGet<ShopItemsConfig>(out config) ||
                    config == null)
                {
                    return;
                }

                bool configChanged = false;

                foreach (ForgeEntry entry in ForgeRegistry.Entries)
                {
                    if (!entry.inShop || entry.module == null)
                        continue;

                    if (_injected.Contains(entry.module.Id))
                        continue;

                    // 1. Price config (mandatory) - build it first; if it
                    //    can't be made, skip the pool too (no price =
                    //    crash when the shop tries to show it).
                    //
                    // ★ THE CONFIG IS HANDED BACK, NOT LOOKED UP AGAIN.
                    // `config.Get(id)` reads a dictionary that is only rebuilt
                    // by `config.Initialize()` at the END of this loop, so for
                    // an item added this pass it answers null. Three R19 bugs
                    // were that one null: `Repeatable` returned early, so
                    // TOLLGATE vanished after one purchase and its rising
                    // price never showed (test 269); the summary line printed
                    // "for free" for every Forge item; and the "hidden until
                    // you have found" half of it never printed, which is why
                    // nothing in the log could say whether test 270's gate had
                    // been applied.
                    ShopItemConfig made;

                    if (!EnsureConfig(config, entry, ref configChanged, out made))
                        continue;

                    // ⚠ AN INCREMENT ON A ONE-SHOT ITEM IS A NUMBER NOBODY
                    // EVER READS. `Shop` removes a bought item from the list
                    // unless `ModuleData.repeatInShop` is set, and
                    // `IncreasePrice` runs on what is left - so a rising price
                    // on an item that cannot come back is a silent no-op, and
                    // the build log would happily print the escalation.
                    //
                    // Module Forge has done this since it shipped; Weapon
                    // Forge had no increment at all until now, so it had
                    // nothing to guard. Turned on rather than warned about,
                    // matching the twin - asking for a rising price IS asking
                    // for the item to come back.
                    Repeatable(made, entry);

                    bool gated = made.unlockRequirements != null &&
                                 made.unlockRequirements.Count > 0;

                    // 2. Pool entry at the chosen station tier - UNLESS THE
                    //    ITEM IS GATED.
                    //
                    // ★ THE POOL IGNORES THE GATE, which is R19 test 270.
                    // `RunData.RegisterShopUnlock` draws from the per-tier
                    // groups with no look at `unlockRequirements` at all, and
                    // it runs at run start - so a gated item in a pool was on
                    // sale at the first station whatever you had found. The
                    // gate is honoured in exactly one place: `Shop` calls
                    // `RunData.AddShopItemsWhereRequirementsMet` as it opens,
                    // which walks the price table (where this item already is)
                    // and adds anything whose ingredients you have ever owned.
                    // So a gated item needs the price and NOT the pool.
                    if (!gated)
                        InjectPool(shopData, entry.module, entry.shopUnlockLevel);

                    _injected.Add(entry.module.Id);

                    // Read the price back off the CONFIG rather than echoing
                    // `entry.shopPrice`. A currency that could not be resolved
                    // is dropped, and a line that printed the authored numbers
                    // would report a price the shop is not charging - the
                    // exact shape of a confirmation that overrules its own
                    // refusal two lines above.
                    Log.LogInfo(
                        "Added '" + entry.displayName + "' to the shop " +
                        (gated
                            ? "HIDDEN until you have found " +
                              Names(made.unlockRequirements) + " (then on " +
                              "sale at any station you open - a gated item " +
                              "is not tied to a station tier" +
                              (entry.shopUnlockLevel > 0
                                  ? ", so its shopUnlockLevel " +
                                    entry.shopUnlockLevel + " is not used"
                                  : "") + ")"
                            : "at unlock level " + entry.shopUnlockLevel) +
                        " for " + ForgeShopCost.Describe(made) + ".");
                }

                if (configChanged)
                {
                    // Rebuild the id -> config dictionary so Get() sees
                    // our additions (throws on duplicate keys - guarded
                    // by the _injected / Get(id) checks above).
                    config.Initialize();
                }
            }
            catch (Exception e)
            {
                Log.LogError("Shop injection failed: " + e);
            }
        }

        // Reads the BUILT config rather than the authored keys, so it fires
        // for a money increment and an ingredient one alike - and never for an
        // increment that was dropped on the way in.
        private static void Repeatable(ShopItemConfig made, ForgeEntry entry)
        {
            if (made == null || made.priceIncrement == null ||
                entry.module.repeatInShop)
            {
                return;
            }

            bool rises = false;

            for (int i = 0; i < made.priceIncrement.Count; i++)
            {
                if (made.priceIncrement[i].AmountFloored > 0)
                {
                    rises = true;
                    break;
                }
            }

            if (!rises)
                return;

            entry.module.repeatInShop = true;

            Log.LogInfo(
                "'" + entry.displayName + "' has a rising price, so it was " +
                "made repeatable in the shop - the game only re-stocks (and " +
                "therefore only re-prices) a repeatable item, so the " +
                "increment would otherwise be a number nothing ever reads.");
        }

        private static string Names(List<Ingredient> ingredients)
        {
            var parts = new List<string>();

            for (int i = 0; i < ingredients.Count; i++)
            {
                if (ingredients[i] == null)
                    continue;

                parts.Add(ingredients[i].displayName ?? ingredients[i].id);
            }

            return string.Join(" and ", parts.ToArray());
        }

        // Returns true if the module has (or now has) a price config, and hands
        // that config back - see the call site for why it must not be looked
        // up again before `config.Initialize()`.
        private static bool EnsureConfig(
            ShopItemsConfig config,
            ForgeEntry entry,
            ref bool configChanged,
            out ShopItemConfig made)
        {
            made = config.Get(entry.module.Id);

            if (made != null)
                return true;   // already present (e.g. a prior session)

            var money =
                JsonFieldMapper.FindAsset(
                    typeof(Resource), "Resource Money") as Resource;

            if (money == null)
            {
                Log.LogWarning(
                    "Can't add '" + entry.displayName + "' to the shop: " +
                    "'Resource Money' currency not found.");
                return false;
            }

            var itemConfig = new ShopItemConfig
            {
                id = entry.module.Id,
                lineNumber = _nextLineNumber++,
                price = new List<Price>
                {
                    new Price
                    {
                        currencyType = Price.CurrencyType.Resource,
                        resource = money,
                        amount = entry.shopPrice
                    }
                },
                priceIncrement = new List<Price>
                {
                    new Price
                    {
                        currencyType = Price.CurrencyType.Resource,
                        resource = money,

                        // Was a hard-coded 0, so a Forge WEAPON could never
                        // get more expensive while a Forge MODULE could -
                        // Module Forge has had `shopPriceIncrement` since it
                        // shipped. Found while researching ingredient costs
                        // and closed here, per duplicate-by-default.
                        amount = entry.shopPriceIncrement
                    }
                },

                // Filled below. Left empty here so the two lists are always
                // constructed, which is what `Apply` appends onto.
                unlockRequirements = new List<Ingredient>()
            };

            // EVERYTHING BEYOND MONEY. Appended rather than replacing, and
            // that is the stock convention rather than a compromise: all 62
            // items in the game's own shop table carry a money price, and 25
            // of them carry ingredients ON TOP of it. A file that wants an
            // ingredient-only cost writes `"shopPrice": 0`.
            ForgeShopCost.Apply(
                itemConfig, entry.shopCost, entry.shopCostIncrement,
                entry.displayName);

            itemConfig.unlockRequirements =
                ForgeShopCost.Unlocks(entry.shopUnlock, entry.displayName);

            // itemList is protected on the generic base
            // ConfigRegistry<ShopItemConfig,string>.
            FieldInfo itemListField =
                typeof(ShopItemsConfig).BaseType.GetField(
                    "itemList",
                    BindingFlags.NonPublic | BindingFlags.Instance);

            if (itemListField == null)
            {
                Log.LogError(
                    "ShopItemsConfig itemList field not found.");
                return false;
            }

            var itemList =
                itemListField.GetValue(config) as List<ShopItemConfig>;

            if (itemList == null)
                return false;

            itemList.Add(itemConfig);
            configChanged = true;
            made = itemConfig;
            return true;
        }

        // Add a dedicated probablity-1 single-module group at the given
        // station tier. Grows perLevelData if the tier is beyond the
        // stock range.
        private static void InjectPool(
            ShopUpgradeData shopData,
            ModuleData module,
            int level)
        {
            if (level < 0)
                level = 0;

            if (shopData.perLevelData == null)
                shopData.perLevelData =
                    new ShopUpgradeData.PerLevelData[0];

            if (level >= shopData.perLevelData.Length)
            {
                var resized =
                    new ShopUpgradeData.PerLevelData[level + 1];

                Array.Copy(
                    shopData.perLevelData, resized,
                    shopData.perLevelData.Length);

                for (int i = shopData.perLevelData.Length;
                     i <= level; i++)
                {
                    resized[i] = new ShopUpgradeData.PerLevelData
                    {
                        groups =
                            new ShopUpgradeData.PerLevelData.PerLevelGroup[0]
                    };
                }

                shopData.perLevelData = resized;
            }

            var group =
                ScriptableObject.CreateInstance<ShopItemGroup>();

            group.hideFlags = HideFlags.HideAndDontSave;
            group.moduleDistribution.Add(module, 1f);

            var plg =
                new ShopUpgradeData.PerLevelData.PerLevelGroup
                {
                    probablity = 1f,
                    group = group
                };

            ShopUpgradeData.PerLevelData tier =
                shopData.perLevelData[level];

            var existing = tier.groups
                ?? new ShopUpgradeData.PerLevelData.PerLevelGroup[0];

            var newGroups =
                new ShopUpgradeData.PerLevelData.PerLevelGroup[
                    existing.Length + 1];

            Array.Copy(existing, newGroups, existing.Length);
            newGroups[existing.Length] = plg;

            tier.groups = newGroups;
            shopData.perLevelData[level] = tier;   // write struct back
        }
    }
}
