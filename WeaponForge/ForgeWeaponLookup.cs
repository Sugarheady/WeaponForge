using System.Collections.Generic;
using System.Text;
using BepInEx.Logging;
using UnityEngine;

namespace WeaponForge
{
    // "Find me the weapon called X" - one implementation, shared by every
    // feature that lets you NAME another weapon (split, minion, turret).
    //
    // WHY THIS EXISTS. A Forge weapon's WeaponData asset is named
    // `"Forge Weapon " + name` (WeaponBuilder), but nothing about that is
    // guessable from the outside, and the three features that take a weapon
    // name each did a bare exact-name lookup. So a weapon whose file says
    // `"name": "Boomerang"` could not be found by "Boomerang" - the very
    // string the file contains - and the log said it was "neither one of your
    // weapons nor a weapon module this game has", which reads as "you typed it
    // wrong" when the name was right. Four different spellings were tried
    // against `split` in one test round and every one of them failed.
    //
    // Worse, the two names a person actually has in front of them are the
    // *file's* `name` and its `displayName`, and until now NEITHER worked. So
    // this accepts all of them:
    //
    //     "Boomerang"                    the file's own "name"
    //     "Forge Weapon Boomerang"       the real asset name
    //     "BOOMERANG"                    the file's "displayName"
    //     "Forge Boomerang"              the near-miss guess
    //     "Weapon Caps Flame"            a stock asset, unchanged
    //
    // And when it still fails it LISTS what is available, because a name lookup
    // that fails silently costs a whole test round and a name lookup that
    // prints the candidates costs one glance.
    public static class ForgeWeaponLookup
    {
        private const string Prefix = "Forge Weapon ";

        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("WeaponForge");

        // Cached per name, including failures (null), so a per-shot resolve
        // never rescans every loaded object.
        private static readonly Dictionary<string, WeaponData> _cache =
            new Dictionary<string, WeaponData>();

        public static WeaponData Find(string name)
        {
            if (string.IsNullOrEmpty(name))
                return null;

            string key = name.Trim();

            if (key.Length == 0)
                return null;

            WeaponData hit;

            if (_cache.TryGetValue(key, out hit))
                return hit;

            hit = Search(key);
            _cache[key] = hit;
            return hit;
        }

        private static WeaponData Search(string name)
        {
            // 1. Exactly what was typed - a stock asset, or the full
            //    "Forge Weapon ..." spelling.
            var direct =
                JsonFieldMapper.FindAsset(typeof(WeaponData), name) as WeaponData;

            if (direct != null)
                return direct;

            // 2. The file's own "name" field, which is what people have in
            //    front of them and the commonest thing to type.
            if (!name.StartsWith(Prefix, System.StringComparison.OrdinalIgnoreCase))
            {
                var prefixed = JsonFieldMapper.FindAsset(
                    typeof(WeaponData), Prefix + name) as WeaponData;

                if (prefixed != null)
                    return prefixed;
            }

            // 3. A near miss: "Forge CANISTER" instead of
            //    "Forge Weapon CANISTER". Strip the stray word and retry.
            if (name.StartsWith("Forge ", System.StringComparison.OrdinalIgnoreCase) &&
                !name.StartsWith(Prefix, System.StringComparison.OrdinalIgnoreCase))
            {
                string bare = name.Substring("Forge ".Length).Trim();

                var recovered = JsonFieldMapper.FindAsset(
                    typeof(WeaponData), Prefix + bare) as WeaponData;

                if (recovered != null)
                    return recovered;
            }

            // 4. The DISPLAY name - the all-caps thing on the loadout card, and
            //    the only name a player who has not opened the file has seen
            //    ("CANISTER" for a file whose name is "Gas Grenade"). It lives
            //    on the MODULE, not on WeaponData, so this goes through the
            //    mod's own registry. Checked after the asset names so a display
            //    name can never shadow a real asset.
            WeaponData byDisplay = null;
            int displayHits = 0;

            foreach (ForgeEntry entry in ForgeRegistry.Entries)
            {
                if (entry == null)
                    continue;

                var wm = entry.module as WeaponModuleData;

                if (wm == null || wm.weapon == null)
                    continue;

                if (Matches(entry.displayName, name) ||
                    Matches(entry.loadoutName, name))
                {
                    byDisplay = wm.weapon;
                    displayHits++;
                }
            }

            if (displayHits == 1)
                return byDisplay;

            if (displayHits > 1)
            {
                Log.LogWarning(
                    "weapon lookup: '" + name + "' is the display name of " +
                    displayHits + " different weapons, so it is ambiguous. " +
                    "Use the \"name\" field instead.");
                return null;
            }

            return null;
        }

        private static bool Matches(string candidate, string name)
        {
            return !string.IsNullOrEmpty(candidate) &&
                   string.Equals(candidate.Trim(), name,
                       System.StringComparison.OrdinalIgnoreCase);
        }

        // The message to print when a lookup fails. Names the feature, and
        // lists the Forge weapons that DO exist - a failed name lookup with no
        // candidates is a guessing game, and it cost four wrong guesses in one
        // round.
        public static void LogMiss(string feature, string name)
        {
            Log.LogWarning(
                feature + ": no weapon named '" + name + "' was found. " +
                "Accepted spellings are the weapon's own \"name\", its " +
                "\"displayName\", the full \"Forge Weapon <name>\", or a stock " +
                "asset name. " + Available());
        }

        public static string Available()
        {
            var names = new List<string>();

            foreach (Object asset in
                     Resources.FindObjectsOfTypeAll(typeof(WeaponData)))
            {
                if (asset == null || asset.name == null)
                    continue;

                if (asset.name.StartsWith(
                        Prefix, System.StringComparison.OrdinalIgnoreCase))
                {
                    names.Add(asset.name.Substring(Prefix.Length));
                }
            }

            if (names.Count == 0)
            {
                return "No Weapon Forge weapons are loaded yet, so only stock " +
                       "asset names can resolve.";
            }

            names.Sort(System.StringComparer.OrdinalIgnoreCase);

            var sb = new StringBuilder();
            sb.Append("Your weapons, by \"name\": ");

            for (int i = 0; i < names.Count; i++)
            {
                if (i > 0)
                    sb.Append(", ");

                sb.Append(names[i]);
            }

            sb.Append('.');
            return sb.ToString();
        }
    }
}
