using System;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace WeaponForge
{
    // Arms a catalyst node once its shot is genuinely live, and halts it when it
    // lands.
    public static class ForgeCatalystPatch
    {
        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("WeaponForge.Catalyst");

        private static void Arm(Component shot)
        {
            try
            {
                var node = shot.GetComponentInChildren<ForgeCatalystNode>(true);

                if (node == null)
                    return;

                // Awake normally has this in hand already; this is the safety net
                // for a node component added after Instantiate.
                node.Resolve();

                if (node.cfg == null)
                    return;

                var ip = shot as IProjectile;

                if (ip == null)
                    return;

                node.Arm(ip, ForgeKillsPatch.WeaponOf(shot), ip.Owner);
            }
            catch (Exception e)
            {
                Log.LogError("Arming a catalyst node failed: " + e);
            }
        }

        private static void Land(Component shot)
        {
            try
            {
                var node = shot.GetComponentInChildren<ForgeCatalystNode>(true);

                if (node != null)
                    node.Halt();
            }
            catch { }
        }

        [HarmonyPatch(typeof(Projectile), "Shoot")]
        public class OnShoot
        {
            static void Postfix(Projectile __instance) { Arm(__instance); }
        }

        [HarmonyPatch(typeof(PhysicsProjectile), "Shoot")]
        public class OnShootPhysics
        {
            static void Postfix(PhysicsProjectile __instance)
            {
                // FireSingle calls Shoot() TWICE on a lobbed shot and the first
                // is a blank - Owner and Velocity are not assigned yet, so
                // arming there would record a null owner and a zero heading.
                if (ForgePhysicsShot.NotReady(__instance))
                    return;

                Arm(__instance);
            }
        }

        // Halting on contact as well as on the timer, so a node fired at a wall
        // stops AT the wall rather than sailing through the arm time and
        // stopping past it.
        [HarmonyPatch(typeof(Projectile), "OnObjectHit")]
        public class OnHit
        {
            static void Postfix(Projectile __instance) { Land(__instance); }
        }

        [HarmonyPatch(typeof(PhysicsProjectile), "OnCollisionEnter2D")]
        public class OnHitPhysics
        {
            static void Postfix(PhysicsProjectile __instance)
            {
                Land(__instance);
            }
        }
    }
}
