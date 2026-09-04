using System;
using System.Collections.Generic;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace WeaponForge
{
    // Drives ForgeBoomerang.
    //
    //   Projectile.Shoot        postfix -> remember the launch
    //   Projectile.FixedUpdate  prefix  -> pivot, steer home, catch
    //   Projectile.OnObjectHit  prefix  -> optional turn on hitting terrain
    //
    // Steering happens in a PREFIX for the same reason homing does: the game
    // rebuilds its collision sweep from Velocity later in that very method, so
    // writing the velocity first means this frame's hitbox already follows the
    // new heading.
    public static class ForgeBoomerangPatch
    {
        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("WeaponForge.Boomerang");

        // How often a retrace breadcrumb is dropped. 20/sec is plenty to
        // retrace a curve smoothly and keeps the list tiny.
        private const float SampleInterval = 0.05f;
        private const int MaxCrumbs = 256;

        // The game's own "base heading" for a shot. Everything that curves a
        // projectile - our wave motion, the game's own noise wobble - rebuilds
        // Velocity each frame by rotating THIS, not by reading Velocity. So a
        // boomerang that only wrote Velocity got overwritten every frame and
        // flew off along its ORIGINAL heading, still visibly waving. Writing
        // the return heading here instead makes the two compose: wave
        // oscillates around wherever the boomerang currently points, and the
        // shot waves its way home.
        private static bool _ready;

        // The reflection itself lives in ForgeSteer - one owner for a field
        // four features write. This keeps only the boomerang-specific warning,
        // because what breaks differs per feature.
        private static void Ensure()
        {
            if (_ready)
                return;

            _ready = true;

            if (!ForgeSteer.Available)
            {
                Log.LogWarning(
                    "Could not reach Projectile.idealDirection - a boomerang " +
                    "combined with wave motion will fly off along its " +
                    "original heading. Boomerang on its own is unaffected.");
            }
        }

        // The single place a return direction is decided: sets the shot's
        // heading AND the shared base heading together, so they cannot drift
        // apart.
        private static void SetHeading(Projectile p, Vector2 dir, float speed)
        {
            if (dir.sqrMagnitude < 0.000001f)
                return;

            Vector2 unit = dir.normalized;

            Ensure();

            ForgeSteer.SetHeading(p, unit, speed);
        }

        // The boomerang's own heading, free of whatever wave motion did to
        // Velocity this frame. Steering from Velocity instead would fold the
        // wave's offset back in every frame and widen into a spiral.
        private static Vector2 Heading(Projectile p)
        {
            Ensure();

            Vector2 ideal;

            if (ForgeSteer.TryGetIdeal(p, out ideal))
                return ideal.normalized;

            return (p.Velocity.sqrMagnitude > 0.0001f)
                ? p.Velocity.normalized
                : Vector2.zero;
        }

        [HarmonyPatch(typeof(Projectile), "Shoot")]
        public class OnShoot
        {
            static void Postfix(Projectile __instance)
            {
                var b = __instance.GetComponent<ForgeBoomerang>();

                if (b == null)
                    return;

                b.shot = true;
                b.startTime = Time.time;
                b.startSpeed = __instance.Velocity.magnitude;
                b.origin = __instance.transform.position;
                b.fireDir = __instance.Velocity.normalized;
                b.returning = false;
                b.pass = 0;
                b.damageApplied = false;
                b.nextSample = 0f;
                b.crumbIndex = 0;

                // Record the damage as fired, so the turn can publish
                // returnDamage as a multiplier instead of writing Damage
                // directly. See the note at the turn itself. Gated on the key
                // being set, because the capture allocates a component and
                // most boomerangs come back at the same damage they left at.
                if (b.returnDamage != 1f)
                {
                    ForgeDamageStack.For(__instance)
                        .Capture(__instance.Damage.amount);
                }

                if (b.retrace)
                {
                    if (b.crumbs == null)
                        b.crumbs = new List<Vector2>(64);
                    else
                        b.crumbs.Clear();
                }

                // Whether the same enemy may be hit twice is just the
                // per-target damage cooldown. Set once, here, so the rule
                // holds on BOTH legs.
                //
                // 0.3s rather than 0: a boomerang overlaps an enemy for
                // several frames as it passes through, and a zero delay would
                // let it re-damage every single frame - melting whatever it
                // flew through. 0.3 is comfortably shorter than any round trip
                // but longer than one pass-through.
                try
                {
                    PiercingData pd = __instance.PiercingData;
                    pd.damageRepeatDelay = b.rehit ? 0.3f : 9999f;
                    __instance.PiercingData = pd;
                }
                catch
                {
                    // Not fatal - it just means the re-hit rule is whatever
                    // the weapon already had.
                }
            }
        }

        [HarmonyPatch(typeof(Projectile), "FixedUpdate")]
        public class OnFixedUpdate
        {
            static void Prefix(Projectile __instance)
            {
                var b = __instance.GetComponent<ForgeBoomerang>();

                if (b == null || !b.shot)
                    return;

                try
                {
                    Tick(__instance, b);
                }
                catch (Exception e)
                {
                    b.shot = false;
                    Log.LogError(
                        "Boomerang failed, shot carries on straight: " + e);
                }
            }
        }

        // Turning around on terrain. Enemies never stop a boomerang - piercing
        // handles those - so this is really "bounce off a wall and head home".
        // A prefix, because the original would otherwise destroy the shot.
        [HarmonyPatch(typeof(Projectile), "OnObjectHit")]
        public class OnHit
        {
            static bool Prefix(Projectile __instance, RaycastHit2D hit)
            {
                var b = __instance.GetComponent<ForgeBoomerang>();

                if (b == null || !b.shot || !b.returnOnHit || b.returning)
                    return true;

                if (hit.collider == null)
                    return true;

                // Only terrain. Anything else falls through to the game's
                // normal handling so damage still lands.
                if (hit.collider.gameObject.layer !=
                    LayerMask.NameToLayer("Ground"))
                {
                    return true;
                }

                try
                {
                    // Step off the wall so the next sweep does not
                    // immediately re-hit it.
                    __instance.transform.position =
                        (Vector2)hit.point +
                        hit.normal * (__instance.Radius + 0.05f);

                    BeginReturn(__instance, b);
                }
                catch (Exception e)
                {
                    Log.LogError("Boomerang wall turn failed: " + e);
                    return true;
                }

                return false;   // skip the destroy
            }
        }

        private static void Tick(Projectile p, ForgeBoomerang b)
        {
            if (Time.time - b.startTime > b.maxLife)
            {
                UnityEngine.Object.Destroy(p.gameObject);
                return;
            }

            if (!b.returning)
            {
                if (b.retrace && Time.time >= b.nextSample &&
                    b.crumbs != null && b.crumbs.Count < MaxCrumbs)
                {
                    b.nextSample = Time.time + SampleInterval;
                    b.crumbs.Add(p.transform.position);
                }

                // Later laps: RangeData was switched off at the first pivot,
                // so nothing decelerates them any more and the speed test
                // below would never fire. They turn by DISTANCE instead,
                // reusing how far the first throw actually got.
                if (b.pass > 0 && b.outRange > 0f)
                {
                    if (Vector2.Distance(b.origin, p.transform.position) >=
                        b.outRange)
                    {
                        BeginReturn(p, b);
                    }

                    return;
                }

                // The pivot: rangeData.slowDown has brought it to a halt.
                // Testing the SPEED rather than a timer means it also works
                // when something else slowed the shot, and it is exactly the
                // moment the outbound leg has no momentum left.
                if (p.Velocity.sqrMagnitude <= 0.01f)
                    BeginReturn(p, b);

                return;
            }

            Steer(p, b);
        }

        private static void BeginReturn(Projectile p, ForgeBoomerang b)
        {
            b.returning = true;
            b.crumbIndex = (b.crumbs != null) ? b.crumbs.Count - 1 : -1;

            if (b.outRange <= 0f)
            {
                b.outRange =
                    Vector2.Distance(b.origin, p.transform.position);
            }

            // Hand ourselves the velocity. While RangeData is enabled the
            // game rewrites the magnitude every frame and, past
            // timeToReachRange, pins it to zero - so the trip home is
            // impossible until this is off. It also stops HandleRange from
            // destroying the shot.
            ProjectileRangeData rd = p.RangeData;
            rd.enabled = false;
            p.RangeData = rd;

            if (!b.damageApplied && b.returnDamage != 1f)
            {
                b.damageApplied = true;

                // Through the shared stack rather than straight onto Damage.
                // This is a one-shot rather than the per-bounce compounding
                // ricochet does, but it failed the same way: growth and the
                // ramp rebuild the amount from their captured base every
                // FixedUpdate, so the trip home was back at full damage one
                // frame later on any boomerang that also grew.
                //
                // The slot is set rather than multiplied, so the guard above
                // is now belt-and-braces rather than load-bearing - setting it
                // twice would be harmless.
                var stack = ForgeDamageStack.For(p);

                stack.Capture(p.Damage.amount);
                stack.boomerang = b.returnDamage;
                stack.Apply(p);
            }

            // Point it home immediately so the pivot reads as a turn rather
            // than a pause.
            Vector2 target = ReturnTarget(p, b);
            Vector2 to = target - (Vector2)p.transform.position;

            SetHeading(
                p,
                (to.sqrMagnitude > 0.0001f) ? to : -b.fireDir,
                b.startSpeed * b.returnSpeed);
        }

        // Where the return leg is aiming right now.
        private static Vector2 ReturnTarget(Projectile p, ForgeBoomerang b)
        {
            if (b.retrace && b.crumbs != null && b.crumbIndex >= 0 &&
                b.crumbIndex < b.crumbs.Count)
            {
                return b.crumbs[b.crumbIndex];
            }

            // Out of breadcrumbs, or homing: the ship itself. Falling back to
            // the launch point keeps a shot whose owner died from stalling.
            if (!b.retrace && p.Owner != null)
                return p.Owner.transform.position;

            return b.origin;
        }

        private static void Steer(Projectile p, ForgeBoomerang b)
        {
            float speed = b.startSpeed * b.returnSpeed;

            if (speed <= 0.0001f)
                speed = b.startSpeed;

            Vector2 here = p.transform.position;

            // Walk the breadcrumb trail backwards as each point is reached.
            if (b.retrace && b.crumbs != null)
            {
                while (b.crumbIndex >= 0 &&
                       Vector2.Distance(here, b.crumbs[b.crumbIndex]) <
                           Mathf.Max(0.4f, speed * Time.fixedDeltaTime * 1.5f))
                {
                    b.crumbIndex--;
                }
            }

            Vector2 target = ReturnTarget(p, b);
            Vector2 to = target - here;

            if (to.sqrMagnitude > 0.000001f)
            {
                Vector2 from = Heading(p);

                if (from == Vector2.zero)
                    from = to.normalized;

                float current = Mathf.Atan2(from.y, from.x) * Mathf.Rad2Deg;
                float desired = Mathf.Atan2(to.y, to.x) * Mathf.Rad2Deg;
                float step = b.turnRate * Time.fixedDeltaTime;
                float delta =
                    Mathf.Clamp(
                        Mathf.DeltaAngle(current, desired), -step, step);

                Vector2 dir =
                    Quaternion.AngleAxis(delta, Vector3.forward) * from;

                SetHeading(p, dir, speed);
            }

            // Caught?
            Vector2 shipAt =
                (p.Owner != null)
                    ? (Vector2)p.Owner.transform.position
                    : b.origin;

            if (Vector2.Distance(here, shipAt) <= b.catchRadius)
                Catch(p, b);
        }

        private static void Catch(Projectile p, ForgeBoomerang b)
        {
            // Loop: send it out again along the original throw.
            if (b.onCatch == 2 && b.pass + 1 < Mathf.Max(1, b.passes))
            {
                b.pass++;
                b.returning = false;
                b.damageApplied = false;
                b.crumbIndex = 0;

                if (b.crumbs != null)
                    b.crumbs.Clear();

                b.nextSample = 0f;
                b.startTime = Time.time;
                b.origin = p.transform.position;
                SetHeading(p, b.fireDir, b.startSpeed);

                // No RangeData any more, so the outward leg no longer
                // decelerates - give it a fresh pivot by distance instead.
                return;
            }

            if (b.onCatch == 1)
                Refund(p, b);

            UnityEngine.Object.Destroy(p.gameObject);
        }

        private static void Refund(Projectile p, ForgeBoomerang b)
        {
            if (b.refundResource == null || b.refundAmount <= 0f ||
                p.Owner == null)
            {
                return;
            }

            try
            {
                Unit.Data data = p.Owner.ComponentData;

                if (data == null || !data.HasTank(b.refundResource))
                    return;

                ResourceTank tank = data.GetTank(b.refundResource);

                if (tank != null)
                    tank.Value += b.refundAmount * b.refundFraction;
            }
            catch (Exception e)
            {
                Log.LogError("Boomerang refund failed: " + e);
            }
        }
    }
}
