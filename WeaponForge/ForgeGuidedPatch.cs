using System;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace WeaponForge
{
    // Two patches: arm the shot when it is fired, steer it every physics step.
    // See ForgeGuided for the design and for why this is much smaller than
    // homing despite feeling like more.
    public static class ForgeGuidedPatch
    {
        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("WeaponForge.Guided");

        private static bool _warned;

        [HarmonyPatch(typeof(Projectile), "Shoot")]
        public class OnShoot
        {
            static void Postfix(Projectile __instance)
            {
                if (!ForgeGuided.Any)
                    return;

                var g = __instance.GetComponent<ForgeGuided>();

                if (g == null)
                    return;

                // Every field reset by hand. `Shoot` runs on a pooled/cloned
                // object and a stale `released` or `shooter` from a previous
                // life would silently disable guidance on this one.
                g.shot = true;
                g.startTime = Time.time;
                g.released = false;
                g.shooter = null;
                g.lookedForShooter = false;
            }
        }

        // PREFIX, so the new heading is in place before the game's own
        // FixedUpdate rebuilds this frame's collision sweep from Velocity. The
        // damage volume then follows the curve rather than lagging a frame
        // behind the sprite.
        [HarmonyPatch(typeof(Projectile), "FixedUpdate")]
        public class OnFixedUpdate
        {
            static void Prefix(Projectile __instance)
            {
                if (!ForgeGuided.Any)
                    return;

                var g = __instance.GetComponent<ForgeGuided>();

                if (g == null || !g.shot)
                    return;

                try
                {
                    Steer(__instance, g);
                }
                catch (Exception e)
                {
                    if (!_warned)
                    {
                        _warned = true;
                        Log.LogError(
                            "guided steering failed, and is switched off for " +
                            "this shot: " + e);
                    }

                    // One bad shot must not spam the log every frame of every
                    // shot for the rest of the run.
                    g.shot = false;
                }
            }
        }

        private static void Steer(Projectile p, ForgeGuided g)
        {
            float now = Time.time;

            if (now < g.startTime + g.delay)
                return;

            if (g.duration > 0f && now > g.startTime + g.delay + g.duration)
                return;

            if (g.whileHeld && !Held(p, g))
                return;

            // Unity's overloaded == : a destroyed owner compares equal to null,
            // which is the test wanted here. No owner means no crosshair, so an
            // enemy-fired or sub-emitter shot simply flies straight.
            Unit owner = p.Owner;

            if (owner == null)
                return;

            Vector2 cursor;

            if (!ForgeVolley.TryCursor(owner, out cursor))
                return;

            Vector2 here = p.transform.position;
            Vector2 toCursor = cursor - here;
            float gap = toCursor.magnitude;

            // ARRIVED. Steering on past this point would make the shot turn as
            // hard as it can forever and orbit the cursor.
            if (gap <= Mathf.Max(0.01f, g.arrive))
                return;

            float speed = p.Velocity.magnitude;

            if (speed <= 0.0001f)
                return;

            // Steer from the BASE HEADING, not from Velocity. Rotating Velocity
            // would fold any wave/noise offset back in each frame and widen
            // into a spiral - and writing Velocity alone would be overwritten
            // by those same features. ForgeSteer owns both halves.
            Vector2 heading;

            if (!ForgeSteer.TryGetIdeal(p, out heading) ||
                heading.sqrMagnitude < 0.000001f)
            {
                heading = p.Velocity;
            }

            if (heading.sqrMagnitude < 0.000001f)
                return;

            float current = Mathf.Atan2(heading.y, heading.x) * Mathf.Rad2Deg;
            float desired = Mathf.Atan2(toCursor.y, toCursor.x) * Mathf.Rad2Deg;

            float delta = Mathf.DeltaAngle(current, desired);
            float limit = g.turnRate * Time.fixedDeltaTime;
            float step = Mathf.Clamp(delta, -limit, limit);

            if (Mathf.Abs(step) < 0.0001f)
                return;

            // About +Z, so a positive step turns counter-clockwise and matches
            // the sign convention of the angles measured above.
            Vector2 steered =
                Quaternion.AngleAxis(step, Vector3.forward) * heading.normalized;

            ForgeSteer.SetHeading(p, steered, speed);

            if (g.faceTravel)
            {
                p.transform.rotation =
                    Quaternion.FromToRotation(Vector3.right, p.Velocity);
            }
        }

        // Is the trigger that fired this shot still down?
        //
        // The shot knows which WEAPON fired it - ForgeKillsPatch stamps that at
        // Shoot for kill attribution - and a Shooter knows which weapon it
        // holds, so matching the two finds the right trigger even on a ship
        // carrying a primary and a secondary. Resolved once and cached.
        //
        // A GADGET has no Shooter at all: it calls `weapon.Fire` directly, so
        // there is no trigger to hold. Rather than guess, that case reports
        // "not held" once the shot is fired, which with `whileHeld` means a
        // gadget shot flies straight - documented, not silent.
        private static bool Held(Projectile p, ForgeGuided g)
        {
            if (g.released)
                return false;

            if (!g.lookedForShooter)
            {
                g.lookedForShooter = true;
                g.shooter = FindShooter(p);
            }

            if (g.shooter == null)
            {
                g.released = true;
                return false;
            }

            if (!g.shooter.IsShooting)
            {
                // Latched: letting go commits the shot, and a re-press fires a
                // new one rather than reclaiming this one.
                g.released = true;
                return false;
            }

            return true;
        }

        // ==================================================================
        // THE SAME FEATURE ON A ROCKET.
        //
        // See ForgeGuidedPhysics for why the old refusal was wrong and why this
        // is one field write rather than a steering loop. The shape below is
        // deliberately the same as the plain-projectile half above so the two
        // read as one feature, but note what is NOT here: no heading maths, no
        // turn-rate clamp, no `SetHeading`. The engine does all of that - we
        // only say where to point.
        // ==================================================================

        [HarmonyPatch(typeof(PhysicsProjectile), "Shoot")]
        public class OnPhysicsShoot
        {
            static void Postfix(PhysicsProjectile __instance)
            {
                if (!ForgeGuidedPhysics.Any)
                    return;

                // `ProjectileWeapon.FireSingle` CALLS Shoot() TWICE on a lobbed
                // shot and the first call is against a blank projectile - no
                // Owner, no velocity, no damage. Arming on that one would stamp
                // `startTime` a frame early and, worse, latch a `released` from
                // a shot with no Shooter to ask. NotReady is the house test.
                if (ForgePhysicsShot.NotReady(__instance))
                    return;

                var g = __instance.GetComponent<ForgeGuidedPhysics>();

                if (g == null)
                    return;

                g.shot = true;
                g.startTime = Time.time;
                g.released = false;
                g.committed = false;
                g.shooter = null;
                g.lookedForShooter = false;

                try
                {
                    Calibrate(__instance, g);
                }
                catch (Exception e)
                {
                    if (!_warnedTorque)
                    {
                        _warnedTorque = true;
                        Log.LogWarning(
                            "guided could not check a rocket's steering " +
                            "torque, so it keeps the value the weapon shipped " +
                            "with. Guidance still works; it may just turn " +
                            "slower than \"turnRate\" asked for. " + e.Message);
                    }
                }
            }
        }

        private static bool _warnedTorque;

        // MAKE `turnRate` MEAN SOMETHING - AND STOP THE SHOT CHATTERING.
        //
        // `maxAngularVelocity` is only a ceiling; `torque` is what climbs to
        // it, and where a torque settles depends on the body's inertia and
        // angular damping - neither knowable at build time, because inertia is
        // computed from the colliders when the shot exists. So the sums are
        // done here, once per shot, where both are readable.
        //
        // TWO BOUNDS, AND THE UPPER ONE IS THE ONE I DID NOT EXPECT.
        //
        // The first version of this method only ever RAISED the torque, on the
        // reasoning that "the cap already limits the top rate, so a strong
        // torque simply reaches it sooner". A simulation of the engine's own
        // loop showed that is false, and why:
        //
        //     if (abs(angularVelocity) < maxAngularVelocity) AddTorque(...)
        //
        // The gate is checked BEFORE the add, so one step can push the spin
        // past the cap by a whole step's worth of torque - and while it is over
        // the cap **no torque is applied at all, including no reversing
        // torque.** The controller goes deaf exactly when it needs to correct.
        //
        // The stock rocket ships `torque: 2` where holding 200 deg/s needs
        // 0.07 - **twenty-nine times more than necessary** - and the measured
        // result is a missile whose heading swings 43 degrees either side of
        // the bearing in a slow limit cycle. A sweep put the cliff between 16x
        // and 18x needed; at 12x the worst heading error is 5.5 degrees and a
        // 90-degree turn still takes 0.38s.
        //
        // So the ceiling is expressed on the thing that actually causes it -
        // the per-step increment as a fraction of the cap - which makes it
        // independent of damping:
        //
        //     increment = torque / inertia * Rad2Deg * fixedDeltaTime
        //     require   increment <= 0.25 * cap
        //
        // The prefab's own torque is KEPT when it falls inside the window, so
        // an author who tuned one is respected; it is raised when too weak to
        // hold the requested rate, and lowered when strong enough to chatter.
        private const float ChatterFraction = 0.25f;

        private static void Calibrate(
            PhysicsProjectile p, ForgeGuidedPhysics g)
        {
            if (g.turnRate <= 0f)
                return;

            var body = p.GetComponent<Rigidbody2D>();

            if (body == null)
                body = p.GetComponentInChildren<Rigidbody2D>();

            if (body == null)
                return;

            float damping = body.angularDamping;
            float inertia = body.inertia;

            if (inertia <= 0.0000001f)
                return;

            float step = Mathf.Max(0.0001f, Time.fixedDeltaTime);

            // What it takes to HOLD the requested rate against damping. With no
            // damping the torque integrates freely, so any torque holds it.
            float needed = (damping > 0.0001f)
                ? g.turnRate * inertia * damping / Mathf.Rad2Deg
                : 0f;

            // What it takes to stay off the cap's dead zone.
            float ceiling =
                ChatterFraction * g.turnRate * inertia /
                (Mathf.Rad2Deg * step);

            ProjectileHomingData h = p.HomingData;

            if (h.torque <= 0f && !_warnedNoTorque)
            {
                _warnedNoTorque = true;
                Log.LogWarning(
                    "guided - this weapon's homingData \"torque\" is 0, so " +
                    "nothing could turn the shot and it would fly straight " +
                    "whatever you point at. One has been supplied; set " +
                    "homingData \"torque\" yourself to choose.");
            }

            float want = Mathf.Clamp(
                h.torque, needed, Mathf.Max(needed, ceiling));

            if (Mathf.Abs(want - h.torque) < 0.0001f)
                return;

            bool lowered = want < h.torque;

            if (!_saidTorque)
            {
                _saidTorque = true;

                Log.LogInfo(
                    "guided - steering torque " +
                    h.torque.ToString("0.####") + " -> " +
                    want.ToString("0.####") + " on this shot (" +
                    (lowered
                        ? "LOWERED: it was strong enough to overshoot its own " +
                          "maxAngularVelocity cap in a single physics step, and " +
                          "the engine applies NO torque at all while over that " +
                          "cap - so the shot went deaf exactly when it needed " +
                          "to correct, and its heading swung wide either side " +
                          "of your cursor"
                        : "RAISED: it was too weak to reach the " +
                          g.turnRate.ToString("0.#") +
                          " deg/s you asked for, so \"turnRate\" would have " +
                          "meant nothing") +
                    "). inertia " + inertia.ToString("0.####") +
                    ", angular damping " + damping.ToString("0.##") +
                    ". Said once per run.");
            }

            h.torque = want;
            p.HomingData = h;
        }

        private static bool _warnedNoTorque;
        private static bool _saidTorque;

        public static void ResetDiagnostics()
        {
            _warnedNoTorque = false;
            _saidTorque = false;
            _warnedPhysics = false;
            _warnedTorque = false;
            _warned = false;
        }

        // A POSTFIX ON Update, and both halves of that are load-bearing.
        //
        // POSTFIX: the game's own `Update` writes `targetPosition` from
        // `Target` while homing is enabled, and its FromLockOnly branch can
        // reset it to "10,000 units dead ahead". A prefix would therefore be
        // overwritten on exactly the weapons whose homing is switched ON -
        // which is every rocket. Running after that body makes ours the last
        // word on every physics weapon rather than on most of them.
        //
        // Update RATHER THAN FixedUpdate: the field is only READ by `Homing()`
        // in FixedUpdate, which Unity runs before the next Update, so a value
        // written here is always fresh when it is used - and the cursor only
        // moves once per rendered frame anyway, so steering it per physics step
        // would be the same number written more often.
        [HarmonyPatch(typeof(PhysicsProjectile), "Update")]
        public class OnPhysicsUpdate
        {
            static void Postfix(PhysicsProjectile __instance)
            {
                if (!ForgeGuidedPhysics.Any)
                    return;

                var g = __instance.GetComponent<ForgeGuidedPhysics>();

                if (g == null || !g.shot)
                    return;

                try
                {
                    SteerPhysics(__instance, g);
                }
                catch (Exception e)
                {
                    if (!_warnedPhysics)
                    {
                        _warnedPhysics = true;
                        Log.LogError(
                            "guided steering failed on a physics shot, and is " +
                            "switched off for it: " + e);
                    }

                    g.shot = false;
                }
            }
        }

        private static bool _warnedPhysics;

        private static void SteerPhysics(
            PhysicsProjectile p, ForgeGuidedPhysics g)
        {
            float now = Time.time;

            if (now < g.startTime + g.delay)
                return;

            bool expired =
                g.duration > 0f && now > g.startTime + g.delay + g.duration;

            if (expired || (g.whileHeld && !HeldPhysics(p, g)))
            {
                // COMMITTING IS A ONE-SHOT, not a per-frame write. Repeating it
                // every frame would re-aim the shot along its CURRENT nose
                // forever, which is a slow curve rather than a straight line -
                // the bang-bang torque never settles exactly on the bearing, so
                // each re-aim would chase its own overshoot.
                if (!g.committed)
                {
                    g.committed = true;
                    ForgeGuidedPhysics.Commit(p);
                }

                return;
            }

            Unit owner = p.Owner;

            if (owner == null)
                return;

            Vector2 cursor;

            if (!ForgeVolley.TryCursor(owner, out cursor))
                return;

            Vector2 here = p.transform.position;

            // ARRIVED. A torque-steered missile that keeps being told to turn
            // toward a point it is already on top of spins on the spot, which
            // is the physics twin of the orbit the plain version guards against.
            if ((cursor - here).sqrMagnitude <=
                Mathf.Max(0.01f, g.arrive) * Mathf.Max(0.01f, g.arrive))
            {
                return;
            }

            ForgeGuidedPhysics.Aim(p, cursor);
        }

        // The physics twin of `Held`. Same reasoning, different shot type -
        // `ForgeKillsPatch.WeaponOf` takes a Component, so one helper covers
        // both.
        private static bool HeldPhysics(
            PhysicsProjectile p, ForgeGuidedPhysics g)
        {
            if (g.released)
                return false;

            if (!g.lookedForShooter)
            {
                g.lookedForShooter = true;
                g.shooter = FindShooterFor(p, p.Owner);
            }

            if (g.shooter == null || !g.shooter.IsShooting)
            {
                g.released = true;
                return false;
            }

            return true;
        }

        private static Shooter FindShooter(Projectile p)
        {
            return FindShooterFor(p, (p != null) ? p.Owner : null);
        }

        private static Shooter FindShooterFor(Component shot, Unit owner)
        {
            try
            {
                WeaponBase weapon = ForgeKillsPatch.WeaponOf(shot);

                if (weapon == null || owner == null)
                    return null;

                Shooter[] shooters =
                    owner.GetComponentsInChildren<Shooter>(true);

                if (shooters == null)
                    return null;

                for (int i = 0; i < shooters.Length; i++)
                {
                    if (shooters[i] != null && shooters[i].Weapon == weapon)
                        return shooters[i];
                }

                return null;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
