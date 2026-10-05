using System;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace WeaponForge
{
    // Opens a gravity well, either the moment the shot is fired (Flight) or
    // the moment it lands (Impact).
    //
    // The landing hooks are postfixes for the same reason sticky and tether
    // use postfixes: the builder has already switched impactBehaviour off, so
    // the shot survives contact instead of deleting itself before we run.
    public static class ForgeGravityPatch
    {
        private static readonly ManualLogSource Log =
            ForgeLog.Source("WeaponForge.Gravity");

        // ---- Flight: the well travels with the shot ----------------------

        [HarmonyPatch(typeof(Projectile), "Shoot")]
        public class OnPlainShoot
        {
            static void Postfix(Projectile __instance)
            {
                Open(__instance, __instance.Owner, ForgeGravity.Where.Flight);
            }
        }

        [HarmonyPatch(typeof(PhysicsProjectile), "Shoot")]
        public class OnPhysicsShoot
        {
            static void Postfix(PhysicsProjectile __instance)
            {
                // FireSingle calls Shoot twice on a lobbed shot, and the first
                // is a blank with no Owner and no velocity. Opening a well on
                // that one would attach it to a projectile that is about to be
                // configured from scratch.
                if (ForgePhysicsShot.NotReady(__instance))
                    return;

                Open(__instance, __instance.Owner, ForgeGravity.Where.Flight);
            }
        }

        // ---- Impact: the well opens where it lands -----------------------

        [HarmonyPatch(typeof(Projectile), "OnObjectHit")]
        public class OnPlainHit
        {
            static void Postfix(Projectile __instance, RaycastHit2D hit)
            {
                if (__instance == null || hit.collider == null)
                    return;

                var g = __instance.GetComponent<ForgeGravity>();

                if (g == null || g.open ||
                    g.where != ForgeGravity.Where.Impact)
                {
                    return;
                }

                try
                {
                    // Park it. OnObjectHit has already snapped it onto the
                    // surface, so this leaves it exactly where it landed.
                    __instance.Velocity = Vector2.zero;

                    Damage d = __instance.Damage;
                    d.amount = 0f;
                    __instance.Damage = d;

                    Open(__instance, __instance.Owner,
                         ForgeGravity.Where.Impact);
                }
                catch (Exception e)
                {
                    Log.LogError("Gravity well failed to open: " + e);
                }
            }
        }

        [HarmonyPatch(typeof(PhysicsProjectile), "OnCollisionEnter2D")]
        public class OnPhysicsHit
        {
            static void Postfix(
                PhysicsProjectile __instance, Collision2D collision)
            {
                if (__instance == null || collision == null)
                    return;

                var g = __instance.GetComponent<ForgeGravity>();

                if (g == null || g.open ||
                    g.where != ForgeGravity.Where.Impact)
                {
                    return;
                }

                try
                {
                    var rb = collision.otherRigidbody != null
                        ? collision.otherRigidbody
                        : __instance.GetComponentInParent<Rigidbody2D>();

                    // Take the shot's own physics away so the well sits still
                    // instead of being shoved around by what it is pulling.
                    if (rb != null)
                    {
                        rb.linearVelocity = Vector2.zero;
                        rb.angularVelocity = 0f;
                        rb.simulated = false;
                    }

                    Damage d = __instance.Damage;
                    d.amount = 0f;
                    __instance.Damage = d;

                    Open(__instance, __instance.Owner,
                         ForgeGravity.Where.Impact);
                }
                catch (Exception e)
                {
                    Log.LogError("Gravity well failed to open: " + e);
                }
            }
        }

        private static void Open(
            Component shot, Unit owner, ForgeGravity.Where when)
        {
            if (shot == null)
                return;

            var g = shot.GetComponent<ForgeGravity>();

            if (g == null || g.open || g.where != when)
                return;

            try
            {
                var ctrl = shot.gameObject.GetComponent<ForgeGravityController>();

                if (ctrl == null)
                    ctrl = shot.gameObject.AddComponent<ForgeGravityController>();

                ctrl.Begin(g, owner);
            }
            catch (Exception e)
            {
                Log.LogError("Gravity well failed to open: " + e);
            }
        }
    }
}
