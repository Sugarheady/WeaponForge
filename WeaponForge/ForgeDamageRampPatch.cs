using System;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace WeaponForge
{
    // Drives ForgeDamageRamp. Mirrors ForgeGrowthPatch: Shoot captures the
    // starting state, FixedUpdate advances it, and both the plain and the
    // physics projectile classes are hooked because they share no base type.
    //
    // Damage is published through ForgeDamageStack rather than written
    // directly, so a shot that both grows and ramps gets both instead of
    // whichever patch happened to run last.
    public static class ForgeDamageRampPatch
    {
        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("WeaponForge");

        // ---- plain projectiles -----------------------------------------

        [HarmonyPatch(typeof(Projectile), "Shoot")]
        public class OnShoot
        {
            static void Postfix(Projectile __instance)
            {
                var r = __instance.GetComponent<ForgeDamageRamp>();

                if (r == null)
                    return;

                try
                {
                    Begin(r, __instance.transform.position,
                          __instance.RangeData);

                    var stack = ForgeDamageStack.For(__instance);
                    stack.Capture(__instance.Damage.amount);
                    stack.ramp = r.Multiplier(__instance.transform.position);
                    stack.Apply(__instance);
                }
                catch (Exception e)
                {
                    r.shot = false;
                    Log.LogError("Damage ramp failed to start: " + e);
                }
            }
        }

        [HarmonyPatch(typeof(Projectile), "FixedUpdate")]
        public class OnFixedUpdate
        {
            static void Prefix(Projectile __instance)
            {
                var r = __instance.GetComponent<ForgeDamageRamp>();

                if (r == null || !r.shot)
                    return;

                try
                {
                    var stack = ForgeDamageStack.For(__instance);
                    stack.ramp = r.Multiplier(__instance.transform.position);
                    stack.Apply(__instance);
                }
                catch (Exception e)
                {
                    r.shot = false;
                    Log.LogError("Damage ramp failed: " + e);
                }
            }
        }

        // Each damaging hit is one "pierce" for the Pierce mode. TryHit only
        // reaches its body when the game's own re-hit delay allows it, so
        // this counts enemies punched through rather than frames overlapped.
        [HarmonyPatch(typeof(Projectile), "TryHit")]
        public class OnTryHit
        {
            static void Postfix(
                Projectile __instance, IProjectileListener listener)
            {
                var r = __instance.GetComponent<ForgeDamageRamp>();

                if (r == null || !r.shot ||
                    r.over != ForgeDamageRamp.Over.Pierce)
                {
                    return;
                }

                // Only a hit that actually landed counts as a pierce. TryHit
                // is called every frame the shot overlaps an enemy and its own
                // re-hit delay swallows all but the first - but a postfix runs
                // on the swallowed ones too, so without this the ramp counted
                // FRAMES OF OVERLAP and a slug reached full damage inside its
                // first victim. See ForgeProjectileHit.
                if (!ForgeProjectileHit.Landed(__instance, listener))
                    return;

                // Counted AFTER the hit lands, so the enemy that triggered
                // the step is damaged at the old value and the NEXT one feels
                // the ramp. A railgun that got its own bonus from the target
                // it just killed would be strictly better than one that
                // didn't, for no readable reason.
                r.events++;
            }
        }

        [HarmonyPatch(typeof(Projectile), "Reflect")]
        public class OnReflect
        {
            static void Postfix(Projectile __instance)
            {
                var r = __instance.GetComponent<ForgeDamageRamp>();

                if (r == null || !r.shot ||
                    r.over != ForgeDamageRamp.Over.Bounce)
                {
                    return;
                }

                r.events++;
            }
        }

        // ---- lobbed / physics shots ------------------------------------

        [HarmonyPatch(typeof(PhysicsProjectile), "Shoot")]
        public class OnShootPhysics
        {
            static void Postfix(PhysicsProjectile __instance)
            {
                var r = __instance.GetComponent<ForgeDamageRamp>();

                if (r == null)
                    return;

                // FireSingle calls Shoot() twice on a lobbed shot, and the
                // first one lands before Damage is assigned. Capturing there
                // would lock the shared stack's base to 0, and every ramp
                // multiplier would then resolve to 0 - a railgun grenade that
                // does no damage whatsoever. See ForgePhysicsShot.
                if (ForgePhysicsShot.NotReady(__instance))
                    return;

                try
                {
                    // A PhysicsProjectile has no RangeData, so a Distance
                    // ramp with no span cannot borrow the weapon's range -
                    // the builder resolves a default for it instead.
                    Begin(r, __instance.transform.position, null);

                    var stack = ForgeDamageStack.For(__instance);
                    stack.Capture(__instance.Damage.amount);
                    stack.ramp = r.Multiplier(__instance.transform.position);
                    stack.Apply(__instance);
                }
                catch (Exception e)
                {
                    r.shot = false;
                    Log.LogError("Damage ramp failed to start: " + e);
                }
            }
        }

        [HarmonyPatch(typeof(PhysicsProjectile), "FixedUpdate")]
        public class OnFixedUpdatePhysics
        {
            static void Prefix(PhysicsProjectile __instance)
            {
                var r = __instance.GetComponent<ForgeDamageRamp>();

                if (r == null || !r.shot)
                    return;

                try
                {
                    var stack = ForgeDamageStack.For(__instance);
                    stack.ramp = r.Multiplier(__instance.transform.position);
                    stack.Apply(__instance);
                }
                catch (Exception e)
                {
                    r.shot = false;
                    Log.LogError("Damage ramp failed: " + e);
                }
            }
        }

        // Shared start-of-flight state. rangeData is null for a physics shot.
        private static void Begin(
            ForgeDamageRamp r, Vector2 here, ProjectileRangeData? rangeData)
        {
            r.origin = here;
            r.startTime = Time.time;
            r.events = 0;
            r.resolvedSpan = r.span;

            // "over the weapon's range" is the natural default for a railgun,
            // and it is only knowable here - the prefab is shared, the range
            // is per shot.
            if (r.resolvedSpan <= 0f &&
                r.over == ForgeDamageRamp.Over.Distance &&
                rangeData.HasValue && rangeData.Value.enabled &&
                rangeData.Value.range > 0f)
            {
                r.resolvedSpan = rangeData.Value.range;
            }

            if (r.resolvedSpan <= 0f)
                r.resolvedSpan = (r.over == ForgeDamageRamp.Over.Distance)
                    ? 12f    // a sensible arena-sized run-up
                    : 1f;    // seconds, or one pierce/bounce per step

            r.shot = true;
        }
    }
}
