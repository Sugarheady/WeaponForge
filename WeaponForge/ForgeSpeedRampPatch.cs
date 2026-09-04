using System;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace WeaponForge
{
    // Drives ForgeSpeedRamp. Same skeleton as the damage ramp - Shoot captures
    // the starting state, FixedUpdate advances it - with one important
    // difference in WHERE it hooks, and that difference is the whole design.
    //
    // ================================================================
    // IT MUST BE A POSTFIX, AND ONLY ON THE PLAIN PROJECTILE
    // ================================================================
    //
    // `Projectile.FixedUpdate` writes Velocity twice before it does anything
    // else, and both writes would eat a speed change made in a prefix:
    //
    //     if (RangeData.enabled && RangeData.slowDown)
    //         Velocity = Velocity.normalized *
    //             Lerp(StartSpeed, 0, elapsed / timeToReachRange);   // sets magnitude
    //
    //     if (MovementNoiseData.enabled)
    //         Velocity = Quaternion.AngleAxis(a, back) *
    //             idealDirection * Velocity.magnitude;               // keeps magnitude
    //
    // The first REPLACES the magnitude outright, so a prefix ramp would be
    // overwritten every frame on any weapon with `slowDown` on - which the stock
    // White Popper, the most-copied template in this mod, ships ON. The damage
    // ramp gets away with a prefix because nothing in that method touches
    // Damage.
    //
    // So this runs AFTER the game has had its say, and writes only the
    // MAGNITUDE - `Velocity.normalized * want`. Direction is left exactly as the
    // game (or a steering feature) left it, which is the same contract every
    // other feature here obeys from the other side: steer with
    // `idealDirection`, never with Velocity. Steering owns the direction, this
    // owns the length, and the two cannot fight.
    //
    // **A lobbed shot is refused rather than hooked.** A PhysicsProjectile's
    // speed is the result of gravity and drag on a real rigidbody; overwriting
    // its velocity magnitude every physics step would flatten the arc into a
    // straight line and quietly break the thing that makes it a lobbed shot.
    // The builder says so rather than doing it badly.
    public static class ForgeSpeedRampPatch
    {
        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("WeaponForge");

        [HarmonyPatch(typeof(Projectile), "Shoot")]
        public class OnShoot
        {
            static void Postfix(Projectile __instance)
            {
                var r = __instance.GetComponent<ForgeSpeedRamp>();

                if (r == null)
                    return;

                try
                {
                    r.origin = __instance.transform.position;
                    r.startTime = Time.time;
                    r.events = 0;
                    r.resolvedSpan = r.span;

                    // The speed the shot was actually fired at, which is the
                    // only correct base: `projectileSpeed` in the file is a
                    // template value and a module may well have changed it.
                    r.launchSpeed = __instance.Velocity.magnitude;

                    ProjectileRangeData range = __instance.RangeData;

                    // "over the weapon's range" is the natural default for a
                    // railgun, and it is only knowable here - the prefab is
                    // shared, the range is per shot.
                    if (r.resolvedSpan <= 0f &&
                        r.over == ForgeSpeedRamp.Over.Distance &&
                        range.enabled && range.range > 0f)
                    {
                        r.resolvedSpan = range.range;
                    }

                    if (r.resolvedSpan <= 0f)
                        r.resolvedSpan =
                            (r.over == ForgeSpeedRamp.Over.Distance)
                                ? 12f
                                : 1f;

                    r.shot = true;

                    // Apply immediately so `from` is true at the muzzle rather
                    // than one physics step later. A ramp starting at 0.2 that
                    // spends its first frame at full speed is visibly wrong on
                    // a slow-start weapon.
                    Drive(__instance, r);
                }
                catch (Exception e)
                {
                    r.shot = false;
                    Log.LogError("Speed ramp failed to start: " + e);
                }
            }
        }

        [HarmonyPatch(typeof(Projectile), "FixedUpdate")]
        public class OnFixedUpdate
        {
            // POSTFIX. See the note at the top of this file - a prefix is
            // silently overwritten by the game's own slowDown write.
            static void Postfix(Projectile __instance)
            {
                var r = __instance.GetComponent<ForgeSpeedRamp>();

                if (r == null || !r.shot)
                    return;

                try
                {
                    Drive(__instance, r);
                }
                catch (Exception e)
                {
                    r.shot = false;
                    Log.LogError("Speed ramp failed: " + e);
                }
            }
        }

        // Each landed hit is one "pierce". Copied from the damage ramp
        // deliberately, including the reason: TryHit runs every frame the shot
        // overlaps an enemy and its own re-hit delay swallows all but the first,
        // but a postfix runs on the swallowed ones too - so without the Landed
        // test the ramp would count FRAMES OF OVERLAP and reach full speed
        // inside its first victim.
        [HarmonyPatch(typeof(Projectile), "TryHit")]
        public class OnTryHit
        {
            static void Postfix(
                Projectile __instance, IProjectileListener listener)
            {
                var r = __instance.GetComponent<ForgeSpeedRamp>();

                if (r == null || !r.shot ||
                    r.over != ForgeSpeedRamp.Over.Pierce)
                {
                    return;
                }

                if (!ForgeProjectileHit.Landed(__instance, listener))
                    return;

                r.events++;
            }
        }

        [HarmonyPatch(typeof(Projectile), "Reflect")]
        public class OnReflect
        {
            static void Postfix(Projectile __instance)
            {
                var r = __instance.GetComponent<ForgeSpeedRamp>();

                if (r == null || !r.shot ||
                    r.over != ForgeSpeedRamp.Over.Bounce)
                {
                    return;
                }

                r.events++;
            }
        }

        // ---- lobbed / physics shots ------------------------------------
        //
        // SUPPORTED, after a second look. The first version refused these on
        // the grounds that forcing the speed "would flatten the arc into a
        // straight line" - which was an overstatement. Scaling the velocity's
        // LENGTH while leaving its direction alone does not stop gravity
        // bending the shot: gravity adds its downward push every step and this
        // runs afterwards, so the path still curves. What changes is that the
        // shot stops trading speed for height - it becomes a POWERED arc rather
        // than a parabola, which for a ramp UP means it flies flatter and
        // further, and for a ramp DOWN means it drops hard.
        //
        // That is a real behaviour change and the builder says so, but it is a
        // weapon, not a bug - and refusing it meant `speedRamp` could not be
        // used with any of the lobbed templates (Derbis and friends), which is
        // exactly the coupling this block is supposed to avoid.
        //
        // WORTH KNOWING: the game has its own thrust for these, and it is
        // stranger than it looks. `PhysicsProjectile.FixedUpdate` calls
        // `Homing()` UNCONDITIONALLY - there is no `HomingData.enabled` test
        // anywhere in it - and the last thing Homing does is
        //
        //     if (rigidbody.linearVelocity.magnitude < HomingData.maxSpeed)
        //         rigidbody.AddForce(transform.right * HomingData.acceleration);
        //
        // so `homingData.acceleration` + `maxSpeed` accelerate a lobbed shot
        // along its FACING with no target and no homing switched on. That is
        // constant thrust; this block is a curve. Both are legitimate and this
        // one runs afterwards, so it wins the magnitude if you set both.
        [HarmonyPatch(typeof(PhysicsProjectile), "Shoot")]
        public class OnShootPhysics
        {
            static void Postfix(PhysicsProjectile __instance)
            {
                var r = __instance.GetComponent<ForgeSpeedRamp>();

                if (r == null)
                    return;

                // FireSingle calls Shoot() TWICE on a lobbed shot and the first
                // one is a blank - capturing there would record a launch speed
                // of zero and every multiplier would then resolve to nothing.
                if (ForgePhysicsShot.NotReady(__instance))
                    return;

                try
                {
                    r.origin = __instance.transform.position;
                    r.startTime = Time.time;
                    r.events = 0;
                    r.resolvedSpan = r.span;
                    r.launchSpeed = __instance.Velocity.magnitude;

                    // A lobbed shot has no usable range to borrow - the game
                    // computes a range TIME for one at Shoot and the field is
                    // not a distance the shot reliably travels - so fall back
                    // to an arena-sized run-up, the same number the damage ramp
                    // uses for exactly this case.
                    if (r.resolvedSpan <= 0f)
                        r.resolvedSpan =
                            (r.over == ForgeSpeedRamp.Over.Distance)
                                ? 12f
                                : 1f;

                    r.shot = true;
                }
                catch (Exception e)
                {
                    r.shot = false;
                    Log.LogError("Speed ramp failed to start (lobbed): " + e);
                }
            }
        }

        [HarmonyPatch(typeof(PhysicsProjectile), "FixedUpdate")]
        public class OnFixedUpdatePhysics
        {
            // POSTFIX, so it runs after the game's own `Homing()` thrust.
            static void Postfix(PhysicsProjectile __instance)
            {
                var r = __instance.GetComponent<ForgeSpeedRamp>();

                if (r == null || !r.shot)
                    return;

                try
                {
                    float want = r.SpeedAt(__instance.transform.position);

                    if (want < 0f)
                        return;

                    Vector2 v = __instance.Velocity;
                    float have = v.magnitude;

                    if (have <= 0.0001f)
                        return;

                    // Length only. Gravity has already had its say this step
                    // and set the direction, so the arc survives.
                    __instance.Velocity = (v / have) * want;
                }
                catch (Exception e)
                {
                    r.shot = false;
                    Log.LogError("Speed ramp failed (lobbed): " + e);
                }
            }
        }

        private static void Drive(Projectile p, ForgeSpeedRamp r)
        {
            float want = r.SpeedAt(p.transform.position);

            if (want < 0f)
                return;

            Vector2 v = p.Velocity;
            float have = v.magnitude;

            // A stationary shot has no direction to preserve, so there is
            // nothing meaningful to scale - and normalising it would hand back
            // a zero vector and freeze the shot permanently.
            if (have <= 0.0001f)
                return;

            p.Velocity = (v / have) * want;
        }
    }
}
