using System;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace WeaponForge
{
    // Fires "slow" and "timeSlow" at the moment a weapon connects.
    //
    // WHY THESE THREE HOOKS AND NOT A SINGLE ONE. "On hit" is three different
    // code paths in this game and they arrive with different information: a
    // projectile knows its own weapon, a hitscan beam is handed the weapon
    // directly, and an explosion is a struct with no route back to anything.
    // ForgeKillsPatch already solved all three - including the awkward one,
    // where the only place to learn the weapon is while the projectile that
    // owns the explosion is still on the stack - so this reads its answer
    // rather than building a fourth version of the same plumbing.
    //
    // These are POSTFIXES on purpose. ForgeKillsPatch sets its credit in a
    // prefix and clears it in a finalizer on these same methods, and Harmony
    // runs prefixes, then the original, then postfixes, then finalizers. A
    // postfix is therefore inside that window; a prefix would be racing it.
    //
    // Postfix also means the damage has already been applied, so a unit killed
    // by this hit is already dead and slowing its corpse is harmless.
    public static class ForgeSlowPatch
    {
        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("WeaponForge.Slow");

        [HarmonyPatch(typeof(DamagableResource), "ProjectileCollided")]
        public class OnProjectile
        {
            static void Postfix(DamagableResource __instance)
            {
                Hit(__instance);
            }
        }

        [HarmonyPatch(typeof(DamagableResource), "OnHitByHitscanWeapon")]
        public class OnHitscan
        {
            static void Postfix(DamagableResource __instance)
            {
                Hit(__instance);
            }
        }

        [HarmonyPatch(typeof(DamagableResource), "OnExplosion")]
        public class OnExplosion
        {
            static void Postfix(DamagableResource __instance)
            {
                Hit(__instance);
            }
        }

        private static void Hit(DamagableResource victim)
        {
            if (!ForgeSlow.Any || victim == null)
                return;

            try
            {
                WeaponBase source = ForgeKillsPatch.Source;

                if (source == null || source.TemplateData == null)
                    return;

                ForgeSlow.Config cfg;

                if (!ForgeSlow.TryGet(source.TemplateData, out cfg))
                    return;

                bool dead = victim.IsDead;

                // onKill and onHit are independent, so a weapon can do a small
                // slow on every hit and a big time-bend only on a kill.
                if (dead)
                {
                    if (!cfg.onKill)
                        return;
                }
                else if (!cfg.onHit)
                {
                    return;
                }

                ForgeSlow.BendTime(cfg);

                // No point debuffing something that just died, and its
                // rigidbody may already be on its way out.
                if (!dead)
                    Slow(victim, cfg);
            }
            catch (Exception e)
            {
                Log.LogError("Slow-on-hit failed: " + e);
            }
        }

        private static void Slow(DamagableResource victim, ForgeSlow.Config cfg)
        {
            if (!cfg.hasSlow)
                return;

            // DamagableResource keeps its own Unit reference private, and on
            // several prefabs health sits on a child - so walk up.
            Unit unit = victim.GetComponentInParent<Unit>();

            if (unit == null)
                return;

            // NEVER SLOW THE THING HOLDING THE GUN. Reachable in practice:
            // explosions in this game have no owner filter at all, so your own
            // blast hits you like anyone else's, and a self-slow is never what
            // anyone meant by "slow on hit".
            //
            // Worth knowing that the movement half would have been a no-op on
            // the player anyway - `ShipMovement` rewrites `rb.linearDamping`
            // every frame in three separate branches, so a damping-based slow
            // cannot stick to the ship. The mass and fire-rate halves would have
            // landed though, which is exactly the sort of half-working effect
            // that costs a round to work out.
            WeaponBase source = ForgeKillsPatch.Source;

            if (source != null && source.Owner != null &&
                ReferenceEquals(unit, source.Owner))
            {
                return;
            }

            ForgeSlow.Apply(unit, cfg);
        }
    }
}
