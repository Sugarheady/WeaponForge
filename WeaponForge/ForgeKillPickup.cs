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
    // The template is LEARNED from whatever spawns during the run rather than
    // resolved from a guid. Nine ship with the game (one per resource, plus Fuel
    // and three Money denominations), and learning means a Forge-only resource
    // still gets a working pickup.
    public static class ForgeKillPickup
    {
        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("WeaponForge.KillPickup");

        private static ResourcePickup _any;
        private static bool _warned;

        private static readonly Dictionary<Resource, ResourcePickup> _byResource =
            new Dictionary<Resource, ResourcePickup>();

        public static void Learn(ResourcePickup pickup)
        {
            if (pickup == null)
                return;

            if (_any == null)
                _any = pickup;

            if (pickup.resource != null &&
                !_byResource.ContainsKey(pickup.resource))
            {
                _byResource[pickup.resource] = pickup;
            }
        }

        public static void Drop(Vector2 where, Resource resource, float amount)
        {
            if (resource == null || amount <= 0f)
                return;

            ResourcePickup template;

            // Prefer one that already belongs to this resource - its art and
            // sound will be right without overriding anything.
            if (!_byResource.TryGetValue(resource, out template) ||
                template == null)
            {
                template = _any;
            }

            if (template == null)
            {
                if (!_warned)
                {
                    _warned = true;
                    Log.LogWarning(
                        "onKill wanted to drop a pickup, but no ResourcePickup " +
                        "has spawned yet to copy, so nothing was dropped. This " +
                        "sorts itself out once any pickup appears in the run - " +
                        "if it never does, use \"asPickup\": false.");
                }

                return;
            }

            try
            {
                ResourcePickup drop = UnityEngine.Object.Instantiate(
                    template, where, Quaternion.identity);

                drop.resource = resource;
                drop.amount = amount;
                drop.gameObject.SetActive(true);

                // A little scatter, so several kills at once do not stack into
                // one pile you cannot see.
                Rigidbody2D rb = drop.Rigidbody;

                if (rb != null)
                {
                    rb.AddForce(
                        new Vector2(
                            UnityEngine.Random.Range(-1.5f, 1.5f),
                            UnityEngine.Random.Range(0.5f, 2f)),
                        ForceMode2D.Impulse);
                }
            }
            catch (Exception e)
            {
                Log.LogError("Could not drop an onKill pickup: " + e);
            }
        }

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
