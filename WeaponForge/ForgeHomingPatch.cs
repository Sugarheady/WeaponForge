using System;
using System.Collections.Generic;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace WeaponForge
{
    // Drives ForgeHoming: acquire a target when the shot is fired, then swing
    // its Velocity toward that target every FixedUpdate.
    //
    // Two patches, mirroring ForgeWavePatch:
    //   Projectile.Shoot       postfix -> stamp the fire time, grab a target
    //   Projectile.FixedUpdate prefix  -> rotate Velocity before the game
    //                                    sweeps and moves along it
    //
    // The prefix ordering matters and is the whole trick: Projectile.FixedUpdate
    // reads Velocity to build its CircleCast, so writing Velocity in a PREFIX
    // means this frame's collision sweep already follows the new heading. The
    // hitbox curves with the shot for free.
    public static class ForgeHomingPatch
    {
        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("WeaponForge.Homing");

        // Reused for every acquisition scan so a 20-shots-per-second stream
        // does not allocate a fresh list 20 times a second. Physics2D fills it
        // in place; the List form is the one Unity 6 wants (the older
        // OverlapCircleNonAlloc array overload is deprecated).
        private static readonly List<Collider2D> _hits =
            new List<Collider2D>(48);

        [HarmonyPatch(typeof(Projectile), "Shoot")]
        public class OnShoot
        {
            static void Postfix(Projectile __instance)
            {
                var homing = __instance.GetComponent<ForgeHoming>();

                if (homing == null)
                    return;

                homing.shot = true;
                homing.startTime = Time.time;
                homing.turned = 0f;
                homing.target = null;
                homing.targetAim = null;
                homing.nextScan = 0f;

                // The same mask the projectile itself collides with, computed
                // the same way the game computes it (Projectile.Shoot does
                // Physics2D.GetLayerCollisionMask on its own layer). Deriving
                // it rather than reading the private collisionLayerMask field
                // sidesteps the FieldRefAccess value-type trap - that field is
                // a LayerMask, not an int, and asking Harmony for the wrong one
                // throws.
                homing.mask =
                    Physics2D.GetLayerCollisionMask(
                        __instance.gameObject.layer);

                Acquire(__instance, homing);
            }
        }

        [HarmonyPatch(typeof(Projectile), "FixedUpdate")]
        public class OnFixedUpdate
        {
            static void Prefix(Projectile __instance)
            {
                var homing = __instance.GetComponent<ForgeHoming>();

                if (homing == null || !homing.shot)
                    return;

                try
                {
                    Steer(__instance, homing);
                }
                catch (Exception e)
                {
                    // Never let a steering slip kill the projectile's own
                    // FixedUpdate - that would freeze the shot in mid-air
                    // instead of just flying straight.
                    homing.shot = false;
                    Log.LogError("Homing failed, shot flies straight: " + e);
                }
            }
        }

        private static void Steer(Projectile p, ForgeHoming h)
        {
            if (h.delay > 0f && Time.time - h.startTime < h.delay)
                return;

            if (h.maxTurn > 0f && h.turned >= h.maxTurn)
                return;

            // Lost the target (died, despawned) - look for another.
            if (h.target == null ||
                !h.target.gameObject.activeInHierarchy)
            {
                h.target = null;
                h.targetAim = null;

                if (!h.retarget)
                    return;

                if (Time.time < h.nextScan)
                    return;

                Acquire(p, h);

                if (h.target == null)
                    return;
            }

            Vector2 velocity = p.Velocity;
            float speed = velocity.magnitude;

            if (speed <= 0.0001f)
                return;

            // TURN FROM THE PUBLISHED HEADING, not from Velocity.
            //
            // Velocity is what anything else curving this shot has already
            // written to - the mod's `wave`, the game's own noise wobble - and
            // both of them rebuild it each frame by rotating `idealDirection`
            // without ever reading Velocity back. Measuring the turn off
            // Velocity therefore folds the other feature's offset into the
            // pursuit heading and then hands it back for the offset to be
            // added again next frame, which widens into a spiral instead of
            // closing on the target. Speed still comes from Velocity: that is
            // the one thing the other two preserve rather than own.
            Vector2 heading;

            if (!ForgeSteer.TryGetIdeal(p, out heading))
                heading = velocity.normalized;

            Vector2 here = p.transform.position;
            Vector2 aim = h.target.position;

            if (h.predict && h.targetAim != null)
            {
                // Lead the target: where will it be when we get there? One
                // pass is enough - iterating would chase its own tail.
                float eta = Vector2.Distance(here, aim) / speed;
                aim += h.targetAim.Velocity * eta;
            }

            Vector2 toTarget = aim - here;

            if (toTarget.sqrMagnitude <= 0.000001f)
                return;

            float current =
                Mathf.Atan2(heading.y, heading.x) * Mathf.Rad2Deg;

            float desired =
                Mathf.Atan2(toTarget.y, toTarget.x) * Mathf.Rad2Deg;

            float delta = Mathf.DeltaAngle(current, desired);
            float step = h.turnRate * Time.fixedDeltaTime;

            step = Mathf.Clamp(delta, -step, step);

            if (h.maxTurn > 0f)
            {
                float left = h.maxTurn - h.turned;

                if (Mathf.Abs(step) > left)
                    step = Mathf.Sign(step) * left;

                h.turned += Mathf.Abs(step);
            }

            if (Mathf.Abs(step) < 0.0001f)
                return;

            // Rotate about +Z so a positive step turns counter-clockwise,
            // matching the sign convention of the angles measured above.
            Vector2 steered =
                Quaternion.AngleAxis(step, Vector3.forward) * heading;

            // PUBLISH the new heading rather than just flying it.
            //
            // This wrote Velocity alone until the steering audit. Everything
            // that curves a shot rebuilds Velocity from `idealDirection` every
            // frame, so a turn that only reaches Velocity is erased before it
            // moves anything - homing did nothing whatever on a shot that also
            // waved. The build-time guard in `ApplyHoming` hid it for the
            // game's own noise, by switching that off; nothing hid it for the
            // mod's own `wave`, which is the same overwriter.
            //
            // SetHeading also re-normalises to the original speed. Rotating a
            // vector should not change its length, but the quaternion
            // round-trip drifts by a hair and thousands of frames of it would
            // visibly change the speed.
            ForgeSteer.SetHeading(p, steered, speed);

            if (h.faceTravel)
            {
                p.transform.rotation =
                    Quaternion.FromToRotation(Vector3.right, p.Velocity);
            }
        }

        private static void Acquire(Projectile p, ForgeHoming h)
        {
            h.nextScan = Time.time + 0.1f;

            // The published heading, for the same reason the turn uses it: the
            // cone is measured off "forward", and on a shot that also waves,
            // Velocity is up to the full wave angle away from where the shot is
            // actually pointed. A 30-degree cone read off a 30-degree weave can
            // miss a target dead ahead.
            Vector2 forward;

            if (!ForgeSteer.TryGetIdeal(p, out forward))
                forward = p.Velocity;

            if (forward.sqrMagnitude <= 0.000001f)
                forward = p.transform.right;

            AimAssistTarget found =
                FindTarget(
                    p, p.transform.position, forward,
                    h.range, h.cone, h.mask);

            h.target = found != null ? found.transform : null;
            h.targetAim = found;
        }

        // Nearest-to-straight-ahead target inside the cone, which is how the
        // game's own PhysicsProjectile.TryFindTarget chooses: smallest angle
        // off the current heading wins, not smallest distance. That keeps a
        // stream sweeping smoothly instead of snapping between enemies.
        //
        // Public because ricochet's "seek" asks exactly the same question -
        // "what should this shot turn toward from here" - and one
        // implementation means both features agree on what counts as a target.
        // Nearest legitimate target, skipping any already visited.
        //
        // Deliberately NOT the same choice as FindTarget below, which takes
        // the smallest ANGLE from the current heading - right for a shot
        // steering toward something ahead of it, wrong for a chain jump,
        // where the classic behaviour is "hop to whatever is closest" and a
        // target directly behind is a perfectly good next link.
        //
        // Shares FindTarget's rules about what counts: an AimAssistTarget
        // (the game's own "legitimate thing to shoot at" marker, which also
        // excludes terrain), and never something friendly to the owner.
        public static AimAssistTarget FindNearestTarget(
            Projectile p,
            Vector2 here,
            float range,
            int mask,
            HashSet<Transform> exclude)
        {
            // The shot-taking overloads resolve the weapon's priority
            // THEMSELVES, so chain, ricochet, homing and split inherit it with
            // no change at their call sites - and a shot from a weapon that did
            // not opt in resolves to null, which is the old behaviour exactly.
            return FindNearestTarget(
                (p != null) ? p.Owner : null, here, range, mask, exclude, true,
                ForgeTargeting.For(p));
        }

        // Owner-taking overload, for the same reason FindTarget has one: a
        // lobbed shot is a PhysicsProjectile and not a Projectile at all.
        //
        // `objectsToo` is the one behavioural difference. Crates and boxes are
        // legitimate AimAssistTargets - verified in the prefabs, all thirteen
        // Box*/Crate* carry the component - and they are even on the SAME
        // faction as the enemies (guid 7b5e0303...), so nothing in the friendly
        // check excludes them. That is usually wanted: a split that also bursts
        // the crate beside you is the behaviour ULTRAKILL's does. When it is
        // not, the discriminator is `AIAgent`: all 60 fighting units have one
        // and none of the 13 containers do.
        public static AimAssistTarget FindNearestTarget(
            Unit owner,
            Vector2 here,
            float range,
            int mask,
            HashSet<Transform> exclude,
            bool objectsToo,
            ForgeTargeting.Config priority = null)
        {
            var filter = new ContactFilter2D();
            filter.useTriggers = true;
            filter.SetLayerMask(mask);

            int count = Physics2D.OverlapCircle(here, range, filter, _hits);

            // (primary, secondary), compared lexicographically. With no
            // priority config the primary IS the squared distance and the
            // secondary is always 0, so this is exactly the old "nearest wins".
            float bestPrimary = float.MaxValue;
            float bestSecondary = float.MaxValue;
            AimAssistTarget best = null;

            for (int i = 0; i < count && i < _hits.Count; i++)
            {
                Collider2D col = _hits[i];

                if (col == null)
                    continue;

                var aimTarget = col.GetComponentInParent<AimAssistTarget>();

                if (aimTarget == null)
                    continue;

                if (exclude != null && exclude.Contains(aimTarget.transform))
                    continue;

                if (!objectsToo &&
                    aimTarget.GetComponentInParent<AIAgent>() == null)
                    continue;

                if (!ForgeTargeting.Allowed(priority, aimTarget))
                    continue;

                if (owner != null)
                {
                    var unit = col.GetComponentInParent<Unit>();

                    if (unit != null && owner.IsFriendsWith(unit))
                        continue;
                }

                float d =
                    ((Vector2)aimTarget.transform.position - here).sqrMagnitude;

                float primary, secondary;
                ForgeTargeting.Rank(
                    priority, aimTarget, here, d, out primary, out secondary);

                if (ForgeTargeting.Beats(
                        primary, secondary, bestPrimary, bestSecondary))
                {
                    bestPrimary = primary;
                    bestSecondary = secondary;
                    best = aimTarget;
                }
            }

            return best;
        }

        public static AimAssistTarget FindTarget(
            Projectile p,
            Vector2 here,
            Vector2 forward,
            float range,
            float cone,
            int mask)
        {
            // Resolves the firing weapon's priority itself - see the note on
            // the FindNearestTarget overload above.
            return FindTarget(
                (p != null) ? p.Owner : null, here, forward, range, cone, mask,
                null, ForgeTargeting.For(p));
        }

        // Owner-taking overload, because the game has two unrelated projectile
        // classes and a lobbed shot is not a Projectile. Without this a
        // PhysicsProjectile would have to pass null and would then happily
        // pick a friendly unit to aim at.
        public static AimAssistTarget FindTarget(
            Unit owner,
            Vector2 here,
            Vector2 forward,
            float range,
            float cone,
            int mask)
        {
            return FindTarget(owner, here, forward, range, cone, mask, null);
        }

        // `exclude` skips targets already chosen, which is how the catalyst's
        // SPLIT walks outward through a crowd instead of picking the same
        // nearest enemy N times.
        public static AimAssistTarget FindTarget(
            Unit owner,
            Vector2 here,
            Vector2 forward,
            float range,
            float cone,
            int mask,
            System.Collections.Generic.HashSet<Transform> exclude,
            ForgeTargeting.Config priority = null)
        {
            // useTriggers on: an enemy's hurtbox is often a trigger collider,
            // and skipping those would make half the targets invisible here.
            // A default ContactFilter2D filters nothing until a use* flag is
            // set, so this ends up as "these layers, triggers included".
            // SetLayerMask flips useLayerMask on for us.
            var filter = new ContactFilter2D();
            filter.useTriggers = true;
            filter.SetLayerMask(mask);

            int count = Physics2D.OverlapCircle(here, range, filter, _hits);

            // THE CONE IS NOW A HARD FILTER, not the starting best score.
            //
            // It used to be both: `bestAngle` started AT `cone`, so
            // `angle < bestAngle` rejected anything outside the cone and picked
            // the smallest angle in one comparison. Neat, and it stops working
            // the moment the score is something other than the angle - a
            // "lowest health" target 100 degrees off the heading would win on
            // score and silently escape a 45 degree cone. Splitting them keeps
            // the cone meaning exactly what it always meant.
            float bestPrimary = float.MaxValue;
            float bestSecondary = float.MaxValue;
            AimAssistTarget best = null;

            for (int i = 0; i < count && i < _hits.Count; i++)
            {
                Collider2D col = _hits[i];

                if (col == null)
                    continue;

                // AimAssistTarget is the game's own "this is a legitimate
                // thing to shoot at" marker, so reusing it means homing picks
                // exactly what the player's aim assist would - and never
                // terrain, which the collision mask alone would include.
                var aimTarget = col.GetComponentInParent<AimAssistTarget>();

                if (aimTarget == null)
                    continue;

                // Already taken by this caller. Without this the catalyst's
                // SPLIT picks the same nearest enemy for every copy - the
                // clones all fly at one target and it reads as a single fat
                // bullet rather than a burst.
                if (exclude != null && exclude.Contains(aimTarget.transform))
                    continue;

                if (!ForgeTargeting.Allowed(priority, aimTarget))
                    continue;

                // Don't chase our own side.
                if (owner != null)
                {
                    var unit = col.GetComponentInParent<Unit>();

                    if (unit != null && owner.IsFriendsWith(unit))
                        continue;
                }

                Vector2 to = (Vector2)aimTarget.transform.position - here;

                if (to.sqrMagnitude <= 0.000001f)
                    continue;

                float angle = Vector2.Angle(forward, to);

                if (angle >= cone)
                    continue;

                float primary, secondary;
                ForgeTargeting.Rank(
                    priority, aimTarget, here, angle, out primary, out secondary);

                if (ForgeTargeting.Beats(
                        primary, secondary, bestPrimary, bestSecondary))
                {
                    bestPrimary = primary;
                    bestSecondary = secondary;
                    best = aimTarget;
                }
            }

            return best;
        }
    }
}
