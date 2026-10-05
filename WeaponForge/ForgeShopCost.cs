using System;
using System.Collections.Generic;
using BepInEx.Logging;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace WeaponForge
{
    // PAYING FOR A WEAPON WITH SOMETHING OTHER THAN MONEY.
    //
    // ★★ THE ENTIRE MECHANISM WAS ALREADY THERE, INCLUDING THE UI, and that
    // is the whole reason this feature is small. Everything below is width
    // this mod was declining to use rather than anything new:
    //
    //   * `ShopItemConfig.price` is a **`List<Price>`**, not a number, and a
    //     `Price` is either an `Ingredient` (held in the run's `Vault`) or a
    //     `Resource` (a tank on the ship).
    //   * `Shop` deducts EVERY entry on purchase - `vault.Remove` per
    //     ingredient, a tank subtraction per resource - and `CanAfford`
    //     requires all of them.
    //   * `ShopItemConfig.priceIncrement` is a second list, and
    //     `ShopItem.IncreasePrice` matches each increment to the price of the
    //     SAME currency. So escalation is per-currency for free.
    //   * **`ShopItemWidget` already does `foreach (Price price in
    //     shopItem.price)`** and instantiates one `PriceWidget` each, which
    //     picks `ingredient.iconSmall` or `resource.icon` and reddens the
    //     number when you cannot afford it. **A multi-currency cost renders
    //     correctly with no UI work at all**, which is normally the expensive
    //     half of a feature like this.
    //
    // Both mods were building that object with a hard-coded one-element list
    // of `Resource Money`. This is the part that stops flattening it.
    //
    // Duplicated in Module Forge rather than shared, per the standing rule:
    // each mod must build and run with the other absent.
    public static class ForgeShopCost
    {
        private static readonly ManualLogSource Log =
            ForgeLog.Source("WeaponForge");

        // One authored line: a currency name and how much of it.
        public class Cost
        {
            public string name;
            public float amount;
        }

        // ------------------------------------------------------------------
        // Reading the JSON
        // ------------------------------------------------------------------

        // `"shopCost": { "Fiber": 2, "Chip": 1 }` - an OBJECT keyed by
        // currency name rather than the game's list-of-structs, because that
        // is what the builder page can hold: one numeric field per currency,
        // at a `data-path` of `shopCost.Fiber`. A list would need repeater UI
        // the page has nowhere else.
        public static Cost[] Read(JObject root, string key, string fileName)
        {
            var obj = root[key] as JObject;

            if (obj == null)
            {
                if (root[key] != null)
                {
                    Log.LogWarning(
                        fileName + ": \"" + key + "\" has to be an object " +
                        "like { \"Fiber\": 2, \"Chip\": 1 } - a currency name " +
                        "and how many of it. Ignored.");
                }

                return null;
            }

            var made = new List<Cost>();

            foreach (KeyValuePair<string, JToken> pair in obj)
            {
                float amount;

                try { amount = (float?)pair.Value ?? 0f; }
                catch (Exception) { amount = 0f; }

                made.Add(new Cost { name = pair.Key, amount = amount });
            }

            return (made.Count > 0) ? made.ToArray() : null;
        }

        // `"shopUnlock": [ "Powerstar" ]`, or a bare string for one.
        //
        // DELIBERATELY INDEPENDENT OF `shopCost`, which was his call when
        // asked. The stock game always pairs the two - the Fiber item is
        // hidden until you have found Fiber - but they answer different
        // questions, and tying them would remove the ability to gate on one
        // thing while charging another.
        public static string[] ReadUnlock(JObject root, string fileName)
        {
            JToken tok = root["shopUnlock"];

            if (tok == null)
                return null;

            if (tok.Type == JTokenType.String)
            {
                string one = ((string)tok ?? "").Trim();

                return (one.Length > 0) ? new[] { one } : null;
            }

            var arr = tok as JArray;

            if (arr == null)
            {
                Log.LogWarning(
                    fileName + ": \"shopUnlock\" has to be an ingredient " +
                    "name or a list of them. Ignored.");

                return null;
            }

            var made = new List<string>();

            foreach (JToken t in arr)
            {
                string one = ((string)t ?? "").Trim();

                if (one.Length > 0)
                    made.Add(one);
            }

            return (made.Count > 0) ? made.ToArray() : null;
        }

        // ------------------------------------------------------------------
        // Turning authored names into the game's Price list
        // ------------------------------------------------------------------

        // Appends to a `ShopItemConfig` that already carries its money price.
        // `increment` may be null; a currency named there but not in `cost` is
        // dropped, because `ShopItem.IncreasePrice` only raises a price that
        // already exists and an increment with nothing to raise is a number
        // the game will never read.
        public static void Apply(
            ShopItemConfig cfg, Cost[] cost, Cost[] increment, string label)
        {
            if (cfg == null || cost == null)
                return;

            for (int i = 0; i < cost.Length; i++)
            {
                Price price;

                if (!Build(cost[i], label, out price))
                    continue;

                cfg.price.Add(price);

                // The increment list is positional in no way at all - it is
                // matched by CURRENCY - so it only has to exist and name the
                // same thing.
                float step = AmountOf(increment, cost[i].name);

                Price bump;

                if (step > 0f && Build(
                        new Cost { name = cost[i].name, amount = step },
                        label, out bump))
                {
                    cfg.priceIncrement.Add(bump);
                }
            }
        }

        public static List<Ingredient> Unlocks(string[] names, string label)
        {
            var made = new List<Ingredient>();

            if (names == null)
                return made;

            for (int i = 0; i < names.Length; i++)
            {
                Ingredient ing = Ingredients.Find(names[i]);

                if (ing == null)
                {
                    Log.LogWarning(
                        label + ": \"shopUnlock\" names '" + names[i] +
                        "', which is not one of the game's ingredients. " +
                        Ingredients.Hint() + " Ignored, so this weapon is " +
                        "visible in the shop from the start.");

                    continue;
                }

                Warn(ing, label, "shopUnlock");
                made.Add(ing);
            }

            return made;
        }

        // ------------------------------------------------------------------
        // One authored line -> one Price
        // ------------------------------------------------------------------

        // ★★ AN AMOUNT BELOW 1 IS REFUSED, AND IT IS A CRASH GUARD RATHER
        // THAN TIDINESS.
        //
        // `Price.CanAfford` asks `unit.GetResource(r)`, which returns **0**
        // for a resource the ship has no tank for. `Shop`'s purchase line is
        // `this.ship.Unit.GetTank(price.resource).Value -= amount` with **no
        // null check**, and `Unit.Data.GetTank` is
        // `resourceTanks.GetValueOrDefault(resource)` - so it returns null.
        //
        // Those two disagree in exactly one place: an amount of 0 makes
        // `0 >= 0` true, the check passes, and the buy dereferences null
        // **inside the game's own Shop, with nothing pointing at us**. The
        // amount is floored (`Price.AmountFloored` is `FloorToInt`), so 0.5
        // reaches it as 0 as well.
        //
        // > **A check and its action can disagree, and the gap is where the
        // > crash lives.** Same asymmetry as `DamagableResource.Damage`
        // > guarding with `HasTank` while the `.Tank` property does not.
        //
        // Requiring a floored 1 closes it for every currency at once, and it
        // is also what the author meant: a cost of nothing is not a cost.
        private static bool Build(Cost cost, string label, out Price price)
        {
            price = new Price();

            string name = (cost.name ?? "").Trim();

            if (name.Length == 0)
                return false;

            int amount = Mathf.FloorToInt(cost.amount);

            if (amount < 1)
            {
                Log.LogWarning(
                    label + ": \"shopCost\" asks for " +
                    cost.amount.ToString("0.##") + " of '" + name +
                    "', and the shop counts in whole units - so that is " +
                    "zero, and a zero cost is dropped. (It is not merely " +
                    "pointless: the game's own purchase code subtracts a " +
                    "resource without checking the ship can hold it, and a " +
                    "zero passes the affordability check that would " +
                    "otherwise stop it. Use 1 or more.)");

                return false;
            }

            Ingredient ing = Ingredients.Find(name);

            if (ing != null)
            {
                Warn(ing, label, "shopCost");

                price = new Price
                {
                    currencyType = Price.CurrencyType.Ingredient,
                    ingredient = ing,
                    amount = amount
                };

                return true;
            }

            // Not an ingredient - try a resource, because he asked for "other
            // resources" and the game's Price struct takes either.
            var res = JsonFieldMapper.FindAsset(
                typeof(Resource), name) as Resource;

            if (res == null && !name.StartsWith(
                    "Resource ", StringComparison.OrdinalIgnoreCase))
            {
                res = JsonFieldMapper.FindAsset(
                    typeof(Resource), "Resource " + name) as Resource;
            }

            if (res != null)
            {
                price = new Price
                {
                    currencyType = Price.CurrencyType.Resource,
                    resource = res,
                    amount = amount
                };

                return true;
            }

            Log.LogWarning(
                label + ": \"shopCost\" names '" + name + "', which is " +
                "neither one of the game's ingredients nor one of its " +
                "resources. " + Ingredients.Hint() + " Dropped - the rest of " +
                "the price still applies.");

            return false;
        }

        // Does this list ask for ANY rise at all? Used to decide whether an
        // item has to be made repeatable, because the game only re-prices
        // something it re-stocks.
        public static bool Rises(Cost[] costs)
        {
            if (costs == null)
                return false;

            for (int i = 0; i < costs.Length; i++)
                if (Mathf.FloorToInt(costs[i].amount) >= 1)
                    return true;

            return false;
        }

        private static float AmountOf(Cost[] costs, string name)
        {
            if (costs == null)
                return 0f;

            for (int i = 0; i < costs.Length; i++)
            {
                if (string.Equals(costs[i].name, name,
                                  StringComparison.OrdinalIgnoreCase))
                {
                    return costs[i].amount;
                }
            }

            return 0f;
        }

        // ⚠ FOUR OF THE NINE INGREDIENTS ARE NOT IN CIRCULATION, and pricing
        // in one makes a weapon permanently unbuyable - which reads as the
        // shop being broken rather than as the file being wrong.
        //
        // Measured rather than assumed: the five the stock shop charges in
        // (Chip, Coral/Fiber, Gland, Powerstar, Shell/Generator) are each
        // referenced by 3-17 other assets, while Bond, Ex and Face are
        // referenced by exactly ONE - their own registry - and Strange Ball
        // by none. So nothing in the game hands them out today.
        //
        // WARNED, NOT REFUSED. A refusal would be wrong the day a game patch
        // starts dropping them, and this project's rule is that a questionable
        // file is reported and kept rather than silently dropped.
        private static void Warn(Ingredient ing, string label, string key)
        {
            if (ing == null || !Ingredients.IsUncirculated(ing))
                return;

            Log.LogWarning(
                label + ": \"" + key + "\" names '" +
                Ingredients.Describe(ing) + "', and NOTHING IN THE GAME " +
                "HANDS THAT OUT at present - it is declared but never " +
                "dropped or spawned. " +
                (key == "shopCost"
                    ? "So this weapon would sit in the shop at a price that " +
                      "can never be paid."
                    : "So this weapon would never become visible.") +
                " The five the game actually trades in are Chip, Fiber, " +
                "Gland, Powerstar and Generator. Kept anyway, in case a game " +
                "update starts dropping it.");
        }

        // ------------------------------------------------------------------
        // Finding an Ingredient by ANY of the names a person might type
        // ------------------------------------------------------------------

        // ★ THE ID AND THE NAME ON SCREEN ARE DIFFERENT FOR TWO OF THE NINE,
        // and they are two of the three he named from memory.
        //
        //     id `Coral` -> displayName "Fiber"
        //     id `Shell` -> displayName "Generator"
        //
        // An author types what the game showed them. A lookup that only knew
        // ids would fail for the only spelling anyone will ever try, and it
        // would fail while naming the input ("no ingredient called 'Fiber'")
        // rather than the capability - which sends the reader to check their
        // typing instead of the feature.
        //
        // Same family as the 259 sounds that could only be reached by GUID and
        // the seven sprite keys that could only search half their namespace.
        public static class Ingredients
        {
            private static Dictionary<string, Ingredient> _byName;

            private static readonly HashSet<string> Uncirculated =
                new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "Bond", "Ex", "Face", "strange_ball"
                };

            public static Ingredient Find(string name)
            {
                if (string.IsNullOrEmpty(name))
                    return null;

                Build();

                Ingredient found;

                return _byName.TryGetValue(name.Trim(), out found)
                    ? found
                    : null;
            }

            public static bool IsUncirculated(Ingredient ing)
            {
                return ing != null && Uncirculated.Contains(ing.id ?? "");
            }

            public static string Describe(Ingredient ing)
            {
                if (ing == null)
                    return "(none)";

                string dn = (ing.displayName ?? "").Trim();
                string id = (ing.id ?? "").Trim();

                return (dn.Length > 0 && !string.Equals(
                            dn, id, StringComparison.OrdinalIgnoreCase))
                    ? dn + "' (stored as '" + id + "')"
                    : id;
            }

            // Names a COUNT and a handful of examples rather than the whole
            // list - enough to tell "the game is loaded" from "it is not",
            // which is all a full list ever answered.
            public static string Hint()
            {
                Build();

                var names = new List<string>();

                foreach (Ingredient ing in All())
                {
                    string dn = (ing.displayName ?? ing.id ?? "").Trim();

                    if (dn.Length > 0 && !names.Contains(dn))
                        names.Add(dn);
                }

                names.Sort(StringComparer.OrdinalIgnoreCase);

                return "The game has " + names.Count + " ingredients: " +
                       string.Join(", ", names.ToArray()) +
                       " - and any ship resource by name works too.";
            }

            private static IEnumerable<Ingredient> All()
            {
                return Resources.FindObjectsOfTypeAll<Ingredient>();
            }

            // `FindObjectsOfTypeAll` sees assets that have never been
            // instantiated, which `FindObjectsByType` does not - and an
            // Ingredient is a ScriptableObject that only ever exists as one.
            // Cached because this is called per key per file at build time.
            private static void Build()
            {
                if (_byName != null)
                    return;

                _byName = new Dictionary<string, Ingredient>(
                    StringComparer.OrdinalIgnoreCase);

                try
                {
                    foreach (Ingredient ing in All())
                    {
                        if (ing == null)
                            continue;

                        Add(ing.id, ing);
                        Add(ing.displayName, ing);
                        Add(ing.name, ing);

                        // The assets are named "Ingredient Chip"; accept the
                        // bare tail as well, since that is what a person
                        // copying an asset name would trim to.
                        if (!string.IsNullOrEmpty(ing.name) &&
                            ing.name.StartsWith(
                                "Ingredient ",
                                StringComparison.OrdinalIgnoreCase))
                        {
                            Add(ing.name.Substring(11), ing);
                        }
                    }
                }
                catch (Exception e)
                {
                    Log.LogWarning(
                        "Could not scan the game's ingredients (" + e.Message +
                        "), so \"shopCost\" can only resolve resources.");
                }
            }

            // First writer wins: an id must never be shadowed by another
            // ingredient's display name.
            private static void Add(string key, Ingredient ing)
            {
                key = (key ?? "").Trim();

                if (key.Length > 0 && !_byName.ContainsKey(key))
                    _byName[key] = ing;
            }
        }

        // ------------------------------------------------------------------
        // The build-log line
        // ------------------------------------------------------------------

        // Printed as part of the shop summary, because a price the author
        // cannot see is a price they will get wrong. Reads back the RESOLVED
        // config rather than the authored strings, so a dropped entry is
        // visibly absent instead of being reported as though it applied.
        public static string Describe(ShopItemConfig cfg)
        {
            if (cfg == null || cfg.price == null || cfg.price.Count == 0)
                return "free";

            var parts = new List<string>();

            for (int i = 0; i < cfg.price.Count; i++)
            {
                Price p = cfg.price[i];

                string who = (p.currencyType == Price.CurrencyType.Ingredient)
                    ? ((p.ingredient != null)
                        ? (p.ingredient.displayName ?? p.ingredient.id)
                        : "?")
                    : ((p.resource != null) ? p.resource.name : "?");

                string step = "";

                if (cfg.priceIncrement != null)
                {
                    for (int j = 0; j < cfg.priceIncrement.Count; j++)
                    {
                        if (cfg.priceIncrement[j].HasSameCurrency(p) &&
                            cfg.priceIncrement[j].AmountFloored > 0)
                        {
                            step = " (+" +
                                   cfg.priceIncrement[j].AmountFloored +
                                   " per buy)";
                            break;
                        }
                    }
                }

                parts.Add(p.AmountFloored + " " + who + step);
            }

            return string.Join(" + ", parts.ToArray());
        }
    }
}
