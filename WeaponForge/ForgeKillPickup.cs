using System;
using System.Collections.Generic;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace WeaponForge
{
    // Drops a resource pickup on the corpse instead of granting the resource
    // straight into the tank.
    //
    // `ResourcePickup` already does the work and its `resource`, `amount` and
    // `pickupSfx` are all public - it charges the collector's tank on pickup and
    // refuses collectors that have no tank for that resource. So this only has to
    // find one to copy.
    //
    // NINE ship with the game, and they are PREFABS. Verified against the asset
    // dump 2026-08-26 by looking for the ResourcePickup COMPONENT rather than
    // for a name pattern - which is what two earlier versions of this comment
    // did, and both got it wrong in different directions:
    //
    //   prefab                    resource          amount   look
    //   ------------------------  ----------------  ------   ---------------
    //   FuelPickup                Resource Fuel       1      blue orb
    //   ResourcePickup Caps       Resource Caps       1      orange orb
    //   ResourcePickup_Purple     Resource Purple     1      pink orb
    //   ResourcePickup_White      Resource White      1      white
    //   ResourcePickup_Red        Resource Health     1      red
    //   ResourcePickup Tech       Resource Tech       1      purple squares
    //   ResourcePickup_Money      Resource Money     10      yellow
    //   ResourcePickup_Money_20   Resource Money     20      yellow, bigger
    //   ResourcePickup_Money_50   Resource Money     50      yellow, bigger
    //
    // **THE ONLY RESOURCE IN THE GAME WITH NO PICKUP IS `Resource Electron`.**
    // Eight resources exist; seven have art, and Money has three denominations.
    //
    // Two naming traps, and both cost a round:
    //
    //   - **Health's pickup is `ResourcePickup_Red`.** `DropTable Box Health`
    //     drops it and its `resource` is `Resource Health`.
    //   - **Fuel's pickup is `FuelPickup`** - it does NOT start with
    //     "ResourcePickup", so any list built by name misses it. The type scan
    //     below always found it; only the documentation was ever wrong.
    //
    // Fuel, Caps and Purple all share ONE orb sprite and differ only by the
    // SpriteRenderer's tint, which is what makes re-tinting a sensible fallback
    // for a resource with no pickup of its own - see `OrbPickups` below.
    //
    // ================================================================
    // WHY EVERY DROP WORE THE WRONG ART (fixed 2026-08-26)
    // ================================================================
    //
    // The template was learned ONLY from pickups that had already spawned, via
    // the `Pickup.Start` postfix at the bottom of this file. So the cache held
    // whatever happened to be lying around in the room, and a reward for a
    // resource that had not spawned fell back to `_any` - the first pickup of
    // the run - and wore its art for the rest of that run while paying the right
    // resource.
    //
    // Same wrong art all run; a different wrong art next run, because which
    // crate opened first changes. That is the fingerprint of this bug.
    //
    // The fix is to cache the PREFABS, found with
    // `Resources.FindObjectsOfTypeAll` - which includes assets that are loaded
    // but not in a scene, and `FindObjectsByType` does NOT. The eight are in
    // memory because the drop tables reference them, so the scan finds all eight
    // before anything has ever dropped. A prefab is never destroyed and never
    // needs re-finding, which also fixes the second bug here: a pickup DESTROYS
    // itself when collected, and a destroyed Unity object compares equal to
    // null, so a cached live one silently went null the moment it was picked up.
    //
    // The scan flag is set ONLY when the scan actually finds prefabs - never by
    // the live-learning path. Module Forge's twin of this file was gated on
    // "have we got anything yet" instead, which the `Pickup.Start` postfix makes
    // true the moment any crate drops anything, so its scan never ran and it
    // regressed to exactly the behaviour described above. Do not reintroduce
    // that guard here.
    public static class ForgeKillPickup
    {
        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("WeaponForge.KillPickup");

        // The fallback art for a resource with no pickup of its own, in
        // preference order. All three of these are the SAME orb sprite with a
        // different SpriteRenderer tint - so cloning one and re-tinting it with
        // the resource's own colour produces art the game itself would have
        // drawn, rather than an obviously borrowed one.
        //
        // Pinned rather than "whatever we found first" for the same reason the
        // scan is pinned: an arbitrary fallback varies run to run and is
        // impossible to recognise in a bug report.
        private static readonly string[] OrbPickups =
        {
            "ResourcePickup_Purple",
            "ResourcePickup Caps",
            "FuelPickup",
            "ResourcePickup_White",
        };

        private static int OrbRank(ResourcePickup p)
        {
            if (p == null)
                return int.MaxValue;

            for (int i = 0; i < OrbPickups.Length; i++)
            {
                if (string.Equals(p.name, OrbPickups[i],
                        StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }

            return OrbPickups.Length;
        }

        private static ResourcePickup _any;

        // Set ONLY when the prefab scan actually found something.
        private static bool _scanned;
        private static bool _warned;

        private static readonly Dictionary<Resource, ResourcePickup> _byResource =
            new Dictionary<Resource, ResourcePickup>();

        // One log line per resource, so a mismatch is diagnosable without
        // spamming a line per kill.
        private static readonly HashSet<Resource> _reported =
            new HashSet<Resource>();

        // A prefab asset has no scene. A scene object does. This is the only
        // thing separating the two once `FindObjectsOfTypeAll` has handed them
        // to you in one array.
        private static bool IsPrefab(ResourcePickup p)
        {
            return p != null && !p.gameObject.scene.IsValid();
        }

        public static void Learn(ResourcePickup pickup)
        {
            if (pickup == null)
                return;

            bool isPrefab = IsPrefab(pickup);

            // A prefab REPLACES a live instance here, it does not merely fill a
            // gap. Otherwise the first pickup to spawn owns the slot for the
            // run. Between two prefabs, the better orb wins.
            if (_any == null ||
                (isPrefab && !IsPrefab(_any)) ||
                (isPrefab && OrbRank(pickup) < OrbRank(_any)))
            {
                _any = pickup;
            }

            if (pickup.resource == null)
                return;

            // Overwrite a DEAD entry, don't skip it. A collected pickup compares
            // equal to null while still being a perfectly good dictionary key,
            // so `ContainsKey` alone keeps it in the cache forever.
            ResourcePickup had;
            bool have = _byResource.TryGetValue(pickup.resource, out had) &&
                        had != null;

            if (!have || (isPrefab && !IsPrefab(had)))
                _byResource[pickup.resource] = pickup;
        }

        // Returns false when nothing could be dropped, so the caller pays the
        // resource into the tank instead. **A cosmetic failure must not become a
        // payout failure** - this used to return void, so `asPickup` with no
        // usable template silently paid the player nothing at all.
        public static bool Drop(Vector2 where, Resource resource, float amount)
        {
            if (resource == null || amount <= 0f)
                return false;

            bool exact;
            ResourcePickup template = Template(resource, out exact);

            if (template == null)
            {
                if (!_warned)
                {
                    _warned = true;
                    Log.LogWarning(
                        "onKill wanted to drop a pickup and found no " +
                        "ResourcePickup prefab at all to copy for " +
                        (resource.name ?? "that resource") +
                        " - the resource is being granted straight into the " +
                        "tank instead, so the reward still pays. Nine pickup " +
                        "prefabs ship with the game, so seeing this means none " +
                        "of them were loaded yet.");
                }

                return false;
            }

            try
            {
                ResourcePickup drop = UnityEngine.Object.Instantiate(
                    template, where, Quaternion.identity);

                drop.resource = resource;
                drop.amount = amount;

                // Borrowed art gets the resource's OWN colour. An exact match
                // is left alone - its tint is already the right one, and
                // repainting it would flatten the deliberate differences
                // between, say, the three Money denominations.
                if (!exact)
                    Tint(drop, resource);

                drop.gameObject.SetActive(true);

                // A little scatter, so several kills at once do not stack into
                // one pile you cannot see.
                Rigidbody2D rb = drop.Rigidbody;

                if (rb != null)
                {
                    // Cloning a LIVE pickup copies its velocity too, so clear it
                    // before adding the scatter or the copy inherits the flight
                    // of whatever it was cloned from. Harmless on a prefab.
                    rb.linearVelocity = Vector2.zero;
                    rb.angularVelocity = 0f;

                    rb.AddForce(
                        new Vector2(
                            UnityEngine.Random.Range(-1.5f, 1.5f),
                            UnityEngine.Random.Range(0.5f, 2f)),
                        ForceMode2D.Impulse);
                }

                return true;
            }
            catch (Exception e)
            {
                Log.LogError("Could not drop an onKill pickup: " + e);
                return false;
            }
        }

        // Paint a borrowed pickup in the resource's own colour, so "no prefab
        // for this resource" still looks like something the game would draw.
        // `Resource.color` is a `ColorAsset` (a ScriptableObject wrapping one
        // Color); `resourceBarUnitColorFull` is a plain Color on the resource
        // itself and covers anything that has no ColorAsset.
        private static void Tint(ResourcePickup drop, Resource resource)
        {
            try
            {
                SpriteRenderer sr =
                    drop.GetComponentInChildren<SpriteRenderer>();

                if (sr == null)
                    return;

                Color c = (resource.color != null)
                    ? resource.color.color
                    : resource.resourceBarUnitColorFull;

                // An all-zero Color is "nobody filled this in", not black - and
                // painting a pickup transparent would be worse than the wrong
                // colour. Leave the prefab's own tint alone in that case.
                if (c.a <= 0f || (c.r <= 0f && c.g <= 0f && c.b <= 0f))
                    return;

                c.a = 1f;
                sr.color = c;
            }
            catch { }
        }

        // Prefer a pickup that already belongs to this resource - its art and
        // sound will be right without overriding anything. Otherwise borrow the
        // orb and re-tint it, and SAY SO: the payout is correct either way, but
        // the art is a substitution and there was previously no way to tell.
        private static ResourcePickup Template(Resource resource, out bool exact)
        {
            Search();

            ResourcePickup match;
            exact = _byResource.TryGetValue(resource, out match) &&
                    match != null;

            if (exact)
            {
                if (_reported.Add(resource))
                {
                    Log.LogInfo(
                        "onKill reward for " + resource.name + " drops '" +
                        match.name + "' - the prefab that already belongs to " +
                        "that resource, so the art is right.");
                }

                return match;
            }

            if (_any != null && _reported.Add(resource))
            {
                Log.LogWarning(
                    "onKill reward for " + resource.name + " has no pickup " +
                    "prefab of its own, so it borrows '" + _any.name +
                    "' and re-tints it to that resource's colour. It pays " +
                    resource.name + " correctly; only the SHAPE is borrowed. " +
                    "Every stock resource except Electron has its own pickup, " +
                    "so this normally means a modded resource.");
            }

            return (_any != null) ? _any : null;
        }

        // Retried rather than latched: if it genuinely finds nothing (a scene
        // with no loot tables loaded yet), the next kill asks again. A flag set
        // on the first attempt regardless of the result is how the Module Forge
        // twin disabled itself for a whole run.
        private static void Search()
        {
            if (_scanned)
                return;

            try
            {
                var all = Resources.FindObjectsOfTypeAll<ResourcePickup>();

                int prefabs = 0;

                for (int i = 0; i < all.Length; i++)
                {
                    if (IsPrefab(all[i]))
                        prefabs++;

                    Learn(all[i]);
                }

                if (prefabs == 0)
                    return;

                _scanned = true;

                var names = new List<string>();

                foreach (var pair in _byResource)
                {
                    if (pair.Key != null && pair.Value != null)
                        names.Add(pair.Value.name + " = " + pair.Key.name);
                }

                Log.LogInfo(
                    "onKill pickups: scanned " + all.Length +
                    " ResourcePickup(s), " + prefabs + " of them prefabs, " +
                    "covering " + _byResource.Count + " resource(s): " +
                    (names.Count > 0
                        ? string.Join(", ", names.ToArray())
                        : "none named") +
                    ". Anything not in that list borrows '" +
                    (_any != null ? _any.name : "none") +
                    "' re-tinted to its own colour. Expect 9 prefabs and 7 " +
                    "resources - Money has three denominations, and Electron " +
                    "is the one stock resource with no pickup.");
            }
            catch (Exception e)
            {
                Log.LogError("Looking for pickup prefabs failed: " + e);
            }
        }

        // Kept, and still useful: a pickup the game spawns at runtime for a
        // resource no prefab covers (a Forge resource, say) teaches us a shape
        // the prefab scan cannot. It can only ADD to what the scan found - a
        // live instance never displaces a prefab, see `Learn`.
        //
        // Patched on `Pickup.Start`, NOT on ResourcePickup: ResourcePickup has no
        // lifecycle method of its own, and Harmony throws at PatchAll when asked
        // for a method that does not exist. Start is private on the base, which
        // Harmony patches happily.
        [HarmonyPatch(typeof(Pickup), "Start")]
        public class OnPickupStart
        {
            static void Postfix(Pickup __instance)
            {
                try
                {
                    Learn(__instance as ResourcePickup);
                }
                catch { }
            }
        }
    }
}
