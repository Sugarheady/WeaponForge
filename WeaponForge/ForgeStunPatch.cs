using System;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace WeaponForge
{
    // Stun on hit.
    //
    // The same three hooks `slow` uses, and for the same reason: a bullet, a
    // beam and an explosion are three separate damage routes and all three
    // should be able to freeze something. `ForgeKillsPatch.Source` is live
    // during them, so "which weapon did this" needs no extra patch.
    public static class ForgeStunPatch
    {
        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("WeaponForge.Stun");

        [HarmonyPatch(typeof(DamagableResource), "ProjectileCollided")]
        public class OnProjectile
        {
            static void Postfix(DamagableResource __instance) { Hit(__instance); }
        }

        [HarmonyPatch(typeof(DamagableResource), "OnHitByHitscanWeapon")]
        public class OnBeam
        {
            static void Postfix(DamagableResource __instance) { Hit(__instance); }
        }

        [HarmonyPatch(typeof(DamagableResource), "OnExplosion")]
        public class OnBlast
        {
            static void Postfix(DamagableResource __instance) { Hit(__instance); }
        }

        private static void Hit(DamagableResource victim)
        {
            if (!ForgeStun.Any || victim == null)
                return;

            try
            {
                WeaponBase source = ForgeKillsPatch.Source;

                if (source == null || source.TemplateData == null)
                    return;

                ForgeStun.Config cfg;

                if (!ForgeStun.TryGet(source.TemplateData, out cfg))
                    return;

                // No point freezing something that just died, and its
                // rigidbody may already be on its way out. There is no
                // `onKill` counterpart here on purpose - a stun on a corpse is
                // not a design anybody wants.
                if (victim.IsDead)
                    return;

                // DamagableResource keeps its Unit reference private, and on
                // several prefabs health sits on a child - so walk up.
                Unit unit = victim.GetComponentInParent<Unit>();

                if (unit == null)
                    return;

                // NEVER STUN THE THING HOLDING THE GUN, and this is reachable
                // in practice rather than theoretical: explosions in this game
                // have no owner filter at all, so your own blast hits you like
                // anyone else's. Freezing your own ship - unable to move OR
                // shoot - would be the worst bug in the mod.
                if (source.Owner != null && ReferenceEquals(unit, source.Owner))
                    return;

                // The boss guard, before the dice: a size check that only
                // applied on a winning roll would be a size check that
                // sometimes did not.
                if (cfg.maxTargetHealth > 0f &&
                    victim.Unit_HasBiggerTankThan(cfg.maxTargetHealth))
                    return;

                // Cooldown before the dice too, so a refused hit does not
                // consume a roll and make `chance` read low.
                if (ForgeStunned.OnCooldown(unit, cfg))
                    return;

                if (cfg.chance < 1f &&
                    UnityEngine.Random.value > Mathf.Clamp01(cfg.chance))
                    return;

                ForgeStun.Apply(unit, cfg);
            }
            catch (Exception e)
            {
                Log.LogError("Stun-on-hit failed: " + e);
            }
        }
    }

    // Small extension so the boss guard reads as one condition at the call
    // site. `DamagableResource.Tank` resolves `Unit.GetTank(resource)` and must
    // be guarded with `HasTank` exactly as the game's own `Damage` does - some
    // enemies have no tank of the type being asked about at all.
    public static class ForgeStunTargetSize
    {
        public static bool Unit_HasBiggerTankThan(
            this DamagableResource victim, float cap)
        {
            try
            {
                Unit unit = victim.GetComponentInParent<Unit>();

                if (unit == null || victim.resource == null ||
                    !unit.HasTank(victim.resource))
                    return false;

                ResourceTank tank = unit.GetTank(victim.resource);

                return tank != null && tank.Capacity > cap;
            }
            catch (Exception)
            {
                // A guard that cannot read the size must not become a guard
                // that blocks everything, so an unreadable tank means "not too
                // big" and the stun goes ahead.
                return false;
            }
        }
    }
}
