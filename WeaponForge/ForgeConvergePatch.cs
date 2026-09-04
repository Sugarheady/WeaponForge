using System;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace WeaponForge
{
    // Drives ForgeConverge: at Shoot work out where this volley's pellets are
    // meant to meet, then bend each pellet along a circular arc that lands on
    // that point.
    //
    // THE MATH, because it is the whole feature and it is not obvious:
    //
    // From a position, along a heading, to a target point there is exactly ONE
    // circular arc. If theta is the angle between the current heading and the
    // straight line to the focus, that arc turns a total of 2*theta, has
    // radius len/(2*sin theta), and length len*theta/sin(theta). Flying it at
    // constant speed therefore needs a constant turn rate of
    //
    //     2*theta * speed / arcLength      degrees per second
    //
    // Two things fall out of that which make it the right choice here:
    //
    //   - The pellet ARRIVES at the focus travelling theta degrees off the
    //     other side, so it crosses and fans out symmetrically. The hourglass
    //     is free; nothing has to script the "after" half.
    //   - Recomputing it every frame is self-correcting rather than
    //     compounding, because theta and len shrink together along a circle -
    //     the recomputed rate stays put at speed/radius instead of drifting.
    //
    // Contrast with just aiming each pellet at the focus (turn rate infinite):
    // that collapses the cone instantly and is indistinguishable from a
    // weapon with no spread.
    //
    // Prefix on FixedUpdate, like every other steering feature in the mod, so
    // the CircleCast the game builds from Velocity this frame already follows
    // the curve - the hitbox bends with the art for free.
    public static class ForgeConvergePatch
    {
        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("WeaponForge.Converge");

        private static bool _ready;
        private static int _groundMask = -1;

        private static void Ensure()
        {
            if (_ready)
                return;

            _ready = true;

            // Reflection owned by ForgeSteer; only the converge-specific
            // consequence is worded here.
            if (!ForgeSteer.Available)
            {
                Log.LogWarning(
                    "Could not reach Projectile.idealDirection - pellets will " +
                    "still converge, but a converging weapon that also uses " +
                    "wave or movementNoise will drift back onto its launch " +
                    "heading.");
            }

            int ground = LayerMask.NameToLayer("Ground");
            _groundMask = (ground >= 0) ? (1 << ground) : 0;
        }

        [HarmonyPatch(typeof(Projectile), "Shoot")]
        public class OnShoot
        {
            static void Postfix(Projectile __instance)
            {
                var c = __instance.GetComponent<ForgeConverge>();

                if (c == null)
                    return;

                try
                {
                    Ensure();
                    Begin(__instance, c);
                }
                catch (Exception e)
                {
                    c.shot = false;
                    Log.LogError(
                        "Converge failed to start, pellets fly straight: " + e);
                }
            }
        }

        [HarmonyPatch(typeof(Projectile), "FixedUpdate")]
        public class OnFixedUpdate
        {
            static void Prefix(Projectile __instance)
            {
                var c = __instance.GetComponent<ForgeConverge>();

                if (c == null || !c.shot)
                    return;

                try
                {
                    Steer(__instance, c);
                }
                catch (Exception e)
                {
                    // Never let a steering slip kill the projectile's own
                    // FixedUpdate - that freezes the shot in mid-air instead
                    // of just letting it fly straight.
                    c.shot = false;
                    Log.LogError("Converge failed, pellet flies straight: " + e);
                }
            }
        }

        // ---- lobbed / physics shots ------------------------------------
        //
        // A lobbed shot cannot be steered onto a curve - it is a rigidbody
        // falling under gravity, and rewriting its velocity every frame would
        // cancel the fall and turn a grenade into a missile. So converging one
        // is a different job entirely: solve the launch, then let the physics
        // run untouched.
        //
        // Each pellet KEEPS the launch angle the weapon's spread gave it and
        // only its SPEED is changed, to whatever lands that particular arc on
        // the focal point. So a volley leaves as a fan of genuinely different
        // parabolas that all come down on one spot - and because a steeper arc
        // takes longer, they land staggered rather than together. It reads as
        // a rain of grenades onto a marked spot, not as a shotgun blast.
        [HarmonyPatch(typeof(PhysicsProjectile), "Shoot")]
        public class OnShootPhysics
        {
            // PREFIX, unlike the plain-projectile hook: FireSingle has already
            // set position and velocity by now, but Shoot has not yet recorded
            // startSpeed off them. Solving here means the range/slow-down
            // machinery sees the speed we actually fired at.
            static void Prefix(PhysicsProjectile __instance)
            {
                var c = __instance.GetComponent<ForgeConverge>();

                if (c == null)
                    return;

                // FireSingle calls Shoot() twice on a lobbed shot and the
                // first arrives before Velocity is set. Solving a launch from
                // a stationary blank would be meaningless, and it would also
                // burn this pellet's slot in the volley. See ForgePhysicsShot.
                if (ForgePhysicsShot.NotReady(__instance))
                    return;

                try
                {
                    Ensure();
                    BeginPhysics(__instance, c);
                }
                catch (Exception e)
                {
                    Log.LogError(
                        "Converge failed to solve a lobbed shot, firing it " +
                        "as aimed: " + e);
                }
            }
        }

        private static void BeginPhysics(PhysicsProjectile p, ForgeConverge c)
        {
            Vector2 v = p.Velocity;
            float speed = v.magnitude;

            if (speed <= 0.0001f)
                return;

            int slot, count;

            Resolve(
                p.transform, c, v / speed, p.gameObject.layer, p.Owner,
                ForgeTargeting.For(p), out slot, out count);

            var rb = p.GetComponent<Rigidbody2D>();

            float gravity = (rb != null)
                ? -Physics2D.gravity.y * rb.gravityScale
                : 0f;

            float damping = (rb != null) ? rb.linearDamping : 0f;

            Vector2 here = p.transform.position;

            // Spazer on a lobbed weapon: a rank of grenades thrown in
            // parallel, no ballistic solve. They still arc under gravity -
            // they just arc side by side.
            if (c.focus == ForgeConverge.Focus.None)
            {
                p.Velocity = c.axis * speed;

                if (c.faceTravel)
                {
                    p.transform.rotation =
                        Quaternion.FromToRotation(
                            Vector3.right, (Vector3)c.axis);
                }

                return;
            }

            // A physics projectile with gravity switched off has no arc to
            // solve. The honest reading of "converge" for one of those is
            // simply to point it at the spot.
            if (gravity <= 0.01f)
            {
                Vector2 straight = c.focusPoint - here;

                if (straight.sqrMagnitude > 0.000001f)
                    p.Velocity = straight.normalized * speed;

                return;
            }

            float dx = c.focusPoint.x - here.x;
            float dy = c.focusPoint.y - here.y;

            // Straight overhead: no horizontal crossing to solve against, and
            // a fan of arcs cannot meet over your own head. Leave it exactly
            // as the weapon aimed it.
            if (Mathf.Abs(dx) < 0.05f)
                return;

            // THE FIX for "two fly out fast and three fall short".
            //
            // The first version kept each pellet's launch ANGLE from the
            // weapon's spread and solved only its speed. That cannot work: a
            // grenade thrown at or below horizontal never reaches a target at
            // the same height no matter how hard it is thrown, so with a
            // level aim the whole downward half of the fan came back
            // "out of reach", got hurled at the 4x ceiling, and landed short
            // - while the upward half solved correctly. A volley split in two.
            //
            // So the fan angle is discarded here and the LOFT is spread
            // instead. Every pellet points horizontally at the focus and
            // takes a launch angle from the minimum-energy one upward, where
            // a solution always exists. Same volley, five different arcs, all
            // landing on the spot - and the lofted ones take longer, which is
            // what makes it read as a barrage rather than a shotgun.
            float side = Mathf.Sign(dx);
            float d = Mathf.Abs(dx);

            // Minimum-energy launch angle: 45 degrees for a level shot,
            // tilted by half the elevation angle when the target is higher or
            // lower. Every angle above this also reaches, for more speed.
            float thetaMin =
                45f + 0.5f * Mathf.Atan2(dy, d) * Mathf.Rad2Deg;

            float t = (count > 1) ? (float)slot / (count - 1) : 0f;

            float theta = thetaMin + c.loftSpread * t;

            // Never approach vertical - there is no ballistic solution at 90
            // degrees, and the last pellet of a lofted uphill volley is
            // exactly the one that would get there.
            theta = Mathf.Clamp(theta, 1f, c.loftMax);

            float rads = theta * Mathf.Deg2Rad;

            Vector2 dir =
                new Vector2(side * Mathf.Cos(rads), Mathf.Sin(rads));

            float solved;

            ForgeBallistics.Result r =
                ForgeBallistics.Solve(
                    here, c.focusPoint, dir, gravity, damping,
                    Time.fixedDeltaTime,
                    speed * 0.1f, speed * 4f, out solved);

            // NotApplicable means there was nothing sensible to do - leave the
            // weapon's own aim alone rather than mangling it. OutOfReach hands
            // back the hardest throw allowed, which is the "do its best and
            // fall short" case, and it still points at the target.
            if (r == ForgeBallistics.Result.NotApplicable)
                return;

            p.Velocity = dir * solved;

            // The art faces its launch direction, or a lofted grenade flies
            // out sideways.
            if (c.faceTravel)
            {
                p.transform.rotation =
                    Quaternion.FromToRotation(Vector3.right, (Vector3)dir);
            }
        }

        // ---- shared -----------------------------------------------------

        private static void Begin(Projectile p, ForgeConverge c)
        {
            c.shot = false;
            c.arrived = false;
            c.lastTurn = 0f;

            Vector2 v = p.Velocity;

            Vector2 heading = (v.sqrMagnitude > 0.000001f)
                ? v.normalized
                : (Vector2)p.transform.right;

            int slot, count;

            Resolve(
                p.transform, c, heading, p.gameObject.layer, p.Owner,
                ForgeTargeting.For(p), out slot, out count);

            // Spazer: no focal point, no steering. Resolve has already slid
            // this shot out to its place in the rank; all that is left is to
            // put it on the volley's heading so the rank stays a rank.
            //
            // c.shot stays FALSE, so the FixedUpdate steering never runs at
            // all - a parallel volley needs no per-frame work, which is why
            // this is the cheapest mode in the block.
            if (c.focus == ForgeConverge.Focus.None)
            {
                Face(p, c, c.axis, v.magnitude);
                return;
            }

            c.shot = true;
        }

        // Work out the volley's axis, apply any lateral muzzle offset, and
        // decide where this volley is meant to meet. Shared, because a lobbed
        // shot answers those three questions identically and only differs in
        // what it does with the answer.
        // `priority` is the firing weapon's target priority, or null for the
        // original behaviour. Passed IN rather than stamped on `c`, because
        // ForgeConverge is a MonoBehaviour riding the projectile PREFAB - a
        // config-class reference on one of those arrives null on every clone,
        // the trap that once silently disabled the whole of gas and catalyst.
        private static void Resolve(
            Transform t, ForgeConverge c, Vector2 heading, int layer,
            Unit owner, ForgeTargeting.Config priority,
            out int slot, out int count)
        {
            c.target = null;
            slot = 0;
            count = 1;

            // The volley's aim, captured when DoShoot started. Without it
            // there is no shared axis and therefore no shared focal point -
            // one pellet curving toward a point of its own is just a wonky
            // bullet - so fall back to this pellet's own heading, which makes
            // the feature a harmless no-op rather than a mess.
            bool haveVolley =
                ForgeVolley.Active && ForgeVolley.Axis.sqrMagnitude > 0.000001f;

            c.axis = haveVolley ? ForgeVolley.Axis : heading;

            Vector2 origin = haveVolley
                ? ForgeVolley.Origin
                : (Vector2)t.position;

            // Taken exactly once per pellet, here, because two features want
            // it now - the lateral muzzle offset and the lobbed loft - and
            // taking it twice would make every second pellet think it was
            // somewhere else in the rank.
            if (haveVolley)
            {
                count = ForgeVolley.Count;
                slot = ForgeVolley.TakeIndex();
            }

            if (c.width > 0f && count > 1)
                Offset(t, c, slot, count);

            // Wherever the player is actually pointing. Aimer.AimAt runs the
            // position through every IAimEffect before storing it - but there
            // are NO IAimEffect implementors anywhere in the game, so that
            // chain is empty and this is the exact mouse world position
            // (MouseWordPosition.Current, a plane raycast at z=0).
            //
            // That is true of the CROSSHAIR only. The game does have aim
            // assist, applied one stage later to the BARREL - see the note on
            // ForgeVolley.FindAimer. So this branch is the only focus mode
            // that honours the cursor exactly; the `distance` mode below
            // builds its point along c.axis, which is the assisted direction
            // and can sit up to maxAngle (15 deg, 45 on rockets) off the
            // crosshair whenever an enemy is near the line of fire.
            if (c.focus == ForgeConverge.Focus.Cursor &&
                haveVolley && ForgeVolley.HasCursor)
            {
                c.focusPoint = ForgeVolley.Cursor;
                return;
            }

            if (c.focus == ForgeConverge.Focus.Target)
            {
                // Same acquisition homing uses, asked from the BARREL rather
                // than from this pellet: every pellet in the volley has to
                // pick the same enemy or they converge on different points
                // and the pattern falls apart. A 90 degree cone keeps it to
                // things actually in front.
                int mask = Physics2D.GetLayerCollisionMask(layer);

                AimAssistTarget found =
                    ForgeHomingPatch.FindTarget(
                        owner, origin, c.axis, c.range, 90f, mask,
                        null, priority);

                if (found != null)
                    c.target = found.transform;
            }

            c.focusPoint = (c.target != null)
                ? (Vector2)c.target.position
                : origin + c.axis * Mathf.Max(0.01f, c.distance);
        }

        // Slide this pellet sideways so the volley leaves the muzzle as a rank
        // rather than from a single point.
        private static void Offset(
            Transform t, ForgeConverge c, int slot, int count)
        {
            // -0.5 .. +0.5 across the rank, so `width` is the full span from
            // one end pellet to the other.
            float across = ((float)slot / (count - 1)) - 0.5f;

            Vector2 from = t.position;
            Vector2 perp = new Vector2(-c.axis.y, c.axis.x);
            Vector2 want = from + perp * (c.width * across);

            // Don't drop a pellet inside a wall. A projectile resolves its own
            // collisions with a CircleCast, and a cast that STARTS inside a
            // collider counts as a hit, so a pellet spawned in terrain dies on
            // its first frame - which reads as "this shotgun loses pellets
            // near walls" rather than as a spawn bug. Walking the offset back
            // to just short of the wall keeps every pellet in the fight.
            if (_groundMask != 0)
            {
                RaycastHit2D hit = Physics2D.Linecast(from, want, _groundMask);

                if (hit.collider != null)
                {
                    t.position = Vector2.Lerp(from, hit.point, 0.9f);
                    return;
                }
            }

            t.position = want;
        }

        private static void Steer(Projectile p, ForgeConverge c)
        {
            float speed = p.Velocity.magnitude;

            if (speed <= 0.0001f)
                return;

            // Heading from the published field, speed from Velocity - the same
            // split `Face` below writes, and for the same reason. This read was
            // off Velocity until the steering audit: `Face` was already
            // publishing correctly, so the write half was right and the read
            // half quietly folded any wave offset back into the solve every
            // frame. Only the ANGLE of this is ever used, so a unit vector is
            // all it needs to be.
            Vector2 v;

            if (!ForgeSteer.TryGetIdeal(p, out v))
                v = p.Velocity.normalized;

            Vector2 here = p.transform.position;

            // A target can move, so re-read it. A dead one leaves the focal
            // point wherever it last was, which is the right call - the volley
            // is already committed to that spot.
            if (c.target != null)
                c.focusPoint = c.target.position;

            // Past the waist? Measured against the volley AXIS, not against
            // this pellet's own heading: once a pellet has crossed it is
            // flying away from the focus, and the axis test says so without
            // depending on which way that particular pellet ended up pointing.
            if (!c.arrived &&
                Vector2.Dot(here - c.focusPoint, c.axis) >= 0f)
            {
                c.arrived = true;
            }

            if (c.arrived)
            {
                if (c.after == ForgeConverge.After.Parallel)
                    Straighten(p, c, v, speed);

                return;
            }

            Vector2 chord = c.focusPoint - here;
            float len = chord.magnitude;

            if (len <= 0.0001f)
            {
                c.arrived = true;
                return;
            }

            float current = Mathf.Atan2(v.y, v.x) * Mathf.Rad2Deg;
            float desired = Mathf.Atan2(chord.y, chord.x) * Mathf.Rad2Deg;
            float theta = Mathf.DeltaAngle(current, desired);

            // Already pointed straight at it: this pellet is the middle of the
            // fan and has nothing to do.
            if (Mathf.Abs(theta) < 0.01f)
                return;

            float rad = theta * Mathf.Deg2Rad;

            float arc = len * Mathf.Abs(rad) /
                Mathf.Max(0.0001f, Mathf.Abs(Mathf.Sin(rad)));

            float rate = 2f * theta * speed / arc;

            if (c.maxTurnRate > 0f)
                rate = Mathf.Clamp(rate, -c.maxTurnRate, c.maxTurnRate);

            float step = rate * Time.fixedDeltaTime;

            // Never swing past pointing directly at the focus in a single
            // frame. On a slow frame that degrades to "aim at it and fly
            // straight in", which still lands - much better than a pellet
            // whipping round in a circle.
            if (Mathf.Abs(step) > Mathf.Abs(theta))
                step = theta;

            c.lastTurn = Mathf.Abs(rate);

            Face(p, c, Quaternion.AngleAxis(step, Vector3.forward) * v, speed);
        }

        // Past the focus, turn onto the volley's axis so the pellets settle
        // into a parallel lane instead of fanning out.
        private static void Straighten(
            Projectile p, ForgeConverge c, Vector2 v, float speed)
        {
            float current = Mathf.Atan2(v.y, v.x) * Mathf.Rad2Deg;
            float desired = Mathf.Atan2(c.axis.y, c.axis.x) * Mathf.Rad2Deg;
            float delta = Mathf.DeltaAngle(current, desired);

            if (Mathf.Abs(delta) < 0.01f)
                return;

            // Straighten at whatever rate it was already turning, so the
            // crossing reads as one continuous curve rather than a kink at the
            // focal point.
            float rate = (c.lastTurn > 0.01f) ? c.lastTurn : 360f;
            float step = rate * Time.fixedDeltaTime;

            step = Mathf.Clamp(delta, -step, step);

            Face(p, c, Quaternion.AngleAxis(step, Vector3.forward) * v, speed);
        }

        // One place that writes a new heading, because there are three things
        // to keep in step and missing the middle one is the classic bug:
        // Velocity moves the shot and builds its collision sweep,
        // idealDirection is what the game's own wobble and the mod's wave
        // rebuild Velocity FROM each frame, and the transform is what the
        // player actually sees.
        private static void Face(
            Projectile p, ForgeConverge c, Vector2 dir, float speed)
        {
            Vector2 n = dir.normalized;

            // Both writes in one call rather than side by side, so a converging
            // volley also gets ForgeSteer's rule about who owns the direction
            // when the shot is waving as well.
            ForgeSteer.SetHeading(p, n, speed);

            if (c.faceTravel)
            {
                p.transform.rotation =
                    Quaternion.FromToRotation(Vector3.right, (Vector3)n);
            }
        }
    }
}
