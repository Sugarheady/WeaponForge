using System;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace WeaponForge
{
    // Drives ForgeBloomShot.
    //
    //   Projectile.Shoot        PREFIX   -> place it, and stop it dead
    //   Projectile.Shoot        POSTFIX  -> start the swell, and the drift
    //   Projectile.FixedUpdate  PREFIX   -> our own motion and damage, and
    //                                       SKIP the original
    //
    // ---- WHY THE PLACEMENT IS A PREFIX AND THE DRIFT IS A POSTFIX ---------
    //
    // This looks like fussiness and it is the single load-bearing ordering in
    // the feature. `Projectile.Shoot` derives three things from Velocity that
    // are never recomputed afterwards:
    //
    //     startSpeed        = Velocity.magnitude
    //     idealDirection    = Velocity.normalized
    //     timeToReachRange  = range / startSpeed          (when rangeData is on)
    //
    // Zero the velocity BEFORE it runs and all three agree on a shot that is
    // not going anywhere - and `timeToReachRange` becomes a division by zero,
    // i.e. Infinity, so HandleRange can never fire. That is what makes
    // rangeData inert on a bloom, which is exactly what we want: `range` is
    // being read as a PLACEMENT number here, and a live rangeData would also
    // try to use it as a flight distance and destroy the bubble early.
    //
    // Apply the drift in the same prefix and that protection is lost - Shoot
    // would capture the drift speed as `startSpeed`, `timeToReachRange` would
    // become finite, and a drifting bloom would be deleted mid-swell by the
    // engine's own range handler. So the drift goes in the POSTFIX, after the
    // three numbers are already committed to zero.
    //
    // ---- WHY FixedUpdate IS SKIPPED --------------------------------------
    //
    // A bloom does its own damage, and it has to: the engine's swept cast is
    // `CircleCast(pos, Radius, Velocity, Velocity.magnitude * dt, mask)`, which
    // at rest is zero-length. Physics2DSettings ships
    // `m_QueriesStartInColliders: 1`, so that DOES still return things the
    // circle is already inside, at fraction 0 - but it returns exactly ONE of
    // them, with no defined ordering. "Everything inside the circle" and
    // "whatever is closest to the centre" are both unanswerable from it.
    //
    // And letting it run as well would be worse than useless: the shot would
    // take the engine's hit AND ours, and on the non-piercing path `OnObjectHit`
    // teleports the shot to `hit.point + hit.normal * Radius` (a start-inside
    // hit has a ZERO normal) and then, because a bloom has never travelled
    // further than its own safety distance, calls Reflect on a zero velocity
    // instead of destroying it. That is an immortal damage aura parked on your
    // crosshair.
    //
    // Skipping it costs three things, all of which a bloom wants gone:
    // rangeData.slowDown rewriting the speed, the noise wobble, and MoveForward
    // (we move it ourselves, from Velocity, so anything that steers still
    // steers). Other PREFIXES on FixedUpdate still run - Harmony only skips the
    // original - so `wave`, `homing` and `guided` reach a drifting bloom
    // normally.
    //
    // ---- AND IT RUNS LAST OF THEM (2026-10-02) ----------------------------
    //
    // Every other prefix on Projectile.FixedUpdate is a void one, so Priority
    // .Last skips nothing and only fixes an order that used to be arbitrary:
    // a bubble now always moves by the velocity wave and homing wrote THIS
    // step, and a bullet on a spiral is the last word on the step the engine is
    // about to sweep.
    //
    // ---- BULLET MODE ("when": "bullet") -------------------------------------
    //
    // Same placement, a different component (ForgeBloomBullet) and the
    // opposite answer on FixedUpdate: the engine's sweep RUNS, because that is
    // what makes it an ordinary bullet - its hits, piercing and impact are the
    // game's own. The range handler is switched off in the prefix all the same
    // (range is still placement). A missile gets the same pair of patches on
    // PhysicsProjectile, below, and skips the blank first Shoot that
    // FireSingle makes on every physics shot (ForgePhysicsShot.NotReady) - or
    // it would take two ring slots per rocket.
    public static class ForgeBloomPatch
    {
        private static readonly ManualLogSource Log =
            ForgeLog.Source("WeaponForge.Bloom");

        private static bool _saidNoCursor;

        // The player's crosshair, for a shot fired by something that has no
        // barrel of its own to find one from - a wingman companion being the
        // case this exists for. Re-found whenever it goes null, which is what a
        // destroyed Unity object compares as.
        private static Aimer _lastResort;
        private static float _nextScan;

        // Is this crosshair one the firing unit is allowed to borrow? Yes for
        // the owner of it, and for anything on its side - which is exactly the
        // wingman case and nothing else.
        private static bool Friendly(Unit owner, Aimer aimer)
        {
            if (owner == null || aimer == null)
                return false;

            Unit theirs = aimer.GetComponentInParent<Unit>();

            if (theirs == null)
                return false;

            return ReferenceEquals(theirs, owner) || owner.IsFriendsWith(theirs);
        }

        [HarmonyPatch(typeof(Projectile), "Shoot")]
        public class OnShoot
        {
            [HarmonyPriority(Priority.First)]
            static void Prefix(Projectile __instance)
            {
                if (!ForgeBloom.Any)
                    return;

                var b = __instance.GetComponent<ForgeBloomShot>();

                if (b == null || b.cfg == null)
                {
                    PlaceBullet(__instance, __instance);
                    return;
                }

                try
                {
                    Place(__instance, b);
                }
                catch (Exception e)
                {
                    // A bloom that cannot be placed is left as an ordinary
                    // bullet rather than as a broken one.
                    b.cfg = null;

                    Log.LogError("bloom: placement failed, the shot flies " +
                                 "normally instead: " + e);
                }
            }

            static void Postfix(Projectile __instance)
            {
                if (!ForgeBloom.Any)
                    return;

                var b = __instance.GetComponent<ForgeBloomShot>();

                if (b == null || b.cfg == null)
                {
                    BeginBullet(__instance);
                    return;
                }

                if (b.live)
                    return;

                try
                {
                    b.Begin(_centre, _radius, _rate, _axis, _index, _count);
                }
                catch (Exception e)
                {
                    // DESTROYED, NOT RELEASED, and the difference matters. By
                    // the time Begin runs, Place has already switched OFF both
                    // rangeData and lifetimeData on this shot - so handing it
                    // back to the engine gives a projectile with no range, no
                    // lifetime and nothing driving it: it would hang in the
                    // world forever. A missing shot is a bug; an immortal one
                    // is a bug that accumulates.
                    b.cfg = null;

                    Log.LogError(
                        "bloom: could not start, so the shot is dropped " +
                        "rather than left in the world: " + e);

                    UnityEngine.Object.Destroy(__instance.gameObject);
                }
            }
        }

        // Carried from prefix to postfix. A plain static is exact here for the
        // same reason ForgeVolley's are: FireSingle instantiates the shot and
        // calls Shoot() synchronously before it returns, so nothing can run
        // between the two halves.
        private static Vector2 _centre;
        private static Vector2 _axis;
        private static float _radius;
        private static float _rate;
        private static int _index;
        private static int _count = 1;

        // WHERE A SHOT GOES IN THE PATTERN - one answer for a bubble and a
        // bullet, so the two modes cannot place differently.
        private struct Spot
        {
            public Vector2 centre;   // of the pattern
            public Vector2 at;       // this shot's slot on the ring
            public Vector2 axis;     // the volley's aim
            public float rate;       // the gun's live projectileSpeed
            public float reach;      // how far out the pattern may be placed
            public int index;        // this shot's slot, of `count`
            public int count;
        }

        private static void Place(Projectile p, ForgeBloomShot b)
        {
            Spot s = Locate(
                p.gameObject, p.Owner, p.Velocity, p.RangeData.range,
                b.fallbackRange, b.fallbackRate, b.cfg.blind);

            _radius = (p.Radius > 0.001f) ? p.Radius : b.fallbackRadius;
            _rate = s.rate;
            _centre = s.centre;
            _axis = s.axis;
            _index = s.index;
            _count = s.count;

            p.transform.position = s.at;

            StopDead(p, b);
        }

        // A BULLET: the same placement, then ForgeBloomBullet takes it from
        // there. `shot` is the Projectile or the PhysicsProjectile itself.
        //
        // Worked out BEFORE anything on the shot changes, so a failure in the
        // arithmetic leaves an ordinary bullet. A failure after the launch has
        // started (its range is already off by then) drops the shot instead:
        // a bullet with no range and nothing driving it would fly forever.
        private static void PlaceBullet(Component shot, IProjectile ip)
        {
            var bb = shot.GetComponent<ForgeBloomBullet>();

            if (bb == null || bb.cfg == null || bb.placed)
                return;

            try
            {
                Spot s = Locate(
                    shot.gameObject, ip.Owner, ip.Velocity, ip.RangeData.range,
                    bb.fallbackRange, bb.fallbackRate, bb.cfg.blind);

                bb.Setup(s.centre, s.at, s.axis, s.rate, s.reach,
                         s.index, s.count);

                bb.Launch();
            }
            catch (Exception e)
            {
                bool started = bb.placed;

                bb.cfg = null;

                if (started)
                {
                    Log.LogError(
                        "bloom: a bullet failed while launching, so the shot " +
                        "is dropped rather than left flying with no range: " + e);

                    UnityEngine.Object.Destroy(shot.gameObject);
                }
                else
                {
                    Log.LogError(
                        "bloom: a bullet could not be placed, so it flies " +
                        "normally instead: " + e);
                }
            }
        }

        private static void BeginBullet(Component shot)
        {
            var bb = shot.GetComponent<ForgeBloomBullet>();

            if (bb == null || bb.cfg == null || !bb.placed || bb.live)
                return;

            try
            {
                bb.Begin();
            }
            catch (Exception e)
            {
                bb.cfg = null;

                Log.LogError(
                    "bloom: a bullet could not start, so the shot is dropped " +
                    "rather than left with no range: " + e);

                UnityEngine.Object.Destroy(shot.gameObject);
            }
        }

        private static Spot Locate(
            GameObject go, Unit shotOwner, Vector2 v, float range,
            float fallbackRange, float fallbackRate, ForgeBloom.Blind blind)
        {
            Spot s = new Spot();

            // EVERY NUMBER COMES OFF THE SHOT, not off the template. FireSingle
            // has already loaded Radius, RangeData and Velocity with the LIVE
            // weapon's values, so a +Range or +Speed module is already in them.
            // The fallbacks on the component are only for a shot that arrived
            // some other way.
            if (range <= 0f)
                range = fallbackRange;

            s.reach = range;

            // ★ THE INFLATE RATE IS THE GUN'S projectileSpeed, NOT THE SHOT'S
            // VELOCITY - and until R19 it was the velocity.
            //
            // By the time Shoot runs, `ProjectileVelocityInfluencer` (hooked on
            // `OnPrepareProjectile`, the last thing FireSingle does before
            // Shoot) has ADDED YOUR SHIP'S VELOCITY to the shot. So the
            // magnitude read here was projectileSpeed plus however fast you
            // were flying along the aim line: moving toward the crosshair
            // swelled the bubble fast, moving away swelled it slowly, and a
            // ship speed that cancelled the launch speed gave a rate near ZERO
            // - a bubble that never finished swelling and never popped. That is
            // all three of his R19 reports (247 "the burst timing appears to be
            // random... if i move in the opposite direction its slow", 248
            // "sometimes the projectile has no movement speed and it stays
            // around forever", 249 "the growth speed is dependent if the player
            // is moving").
            //
            // `IHasSpeedProperty.Speed` is the live weapon's projectileSpeed -
            // modules included, ship motion not - and it is what the build log
            // derives its fuse from, so the log and the game now agree. The
            // velocity is kept only as a fallback for a shot that did not come
            // from a speed-bearing weapon. A BULLET flies at the same number,
            // for the same reason: at the speed of the gun, not of the ship.
            float speed = v.magnitude;

            WeaponBase firing = ForgeVolley.Active ? ForgeVolley.Weapon : null;
            IHasSpeedProperty speedy = firing as IHasSpeedProperty;
            float configured = (speedy != null) ? speedy.Speed : 0f;

            if (configured > 0.001f)
                s.rate = configured;
            else
                s.rate = (speed > 0.001f) ? speed : fallbackRate;

            // A plain Projectile is NEVER ROTATED unless rotationData asks for
            // it, so `transform.right` is world EAST rather than the way the
            // gun was pointing - the trap that made a catalyst `split` fan its
            // fragments due east regardless of aim. It is reachable here
            // because `projectileSpeed` is the INFLATE RATE on a bloom, so an
            // author who sets an explicit `fuse` may legitimately leave the
            // speed at 0. Take the volley's real axis when there is one.
            //
            // THE VOLLEY AXIS FIRST, for the same reason as the rate: the
            // velocity's direction carries your ship's sideways motion too, so
            // a blind bloom fired while strafing landed off the aim line.
            Vector2 dir;

            if (ForgeVolley.Active)
                dir = ForgeVolley.Axis;
            else if (speed > 0.001f)
                dir = v / speed;
            else
                dir = go.transform.right;

            Vector2 origin = go.transform.position;

            WeaponBase weapon = ForgeVolley.Active ? ForgeVolley.Weapon : null;

            if (weapon != null)
                origin = ForgeVolley.Origin;

            // ---- where the pattern is centred ---------------------------
            Vector2 aim;

            if (TryCursor(shotOwner, weapon, out aim))
            {
                Vector2 reach = aim - origin;
                float d = reach.magnitude;

                // CLAMP rather than refuse. Refusing would cost the shot - the
                // resource, the fire-rate interval and the trigger pull - for
                // pointing slightly too far.
                if (d > range && d > 0.001f)
                    aim = origin + reach * (range / d);

                s.centre = aim;
            }
            else
            {
                // Nothing is pointing at a spot: a minion, a turret, an enemy,
                // or a wingman in one of its direction-aiming modes. Two honest
                // answers and the file picks.
                s.centre = Blind(go.layer, origin, dir, range, blind);
            }

            s.axis = (weapon != null && ForgeVolley.Active)
                ? ForgeVolley.Axis
                : dir;

            // ---- where THIS bloom sits in the pattern -------------------
            int count = 1;
            int index = 0;
            float spread = 0f;
            float variance = 0f;
            float offset = 0f;

            if (weapon != null && ForgeVolley.Active)
            {
                count = Mathf.Max(1, ForgeVolley.Count);
                index = ForgeVolley.TakeIndex();
                spread = weapon.Spread;
                variance = weapon.AngleVariance;
                offset = weapon.AngleOffset;
            }

            float ringRadius = ForgeBloom.RingRadius(
                spread, Vector2.Distance(origin, s.centre));

            Vector2 slot = ForgeBloom.RingOffset(
                index, count, ringRadius, offset, variance);

            s.at = s.centre + slot;
            s.index = index;
            s.count = count;

            return s;
        }

        // A BUBBLE: now stop it dead, and take over the end of its life.
        private static void StopDead(Projectile p, ForgeBloomShot b)
        {
            p.Velocity = Vector2.zero;

            // rangeData is already unreachable once startSpeed is zero (see the
            // header), but saying it out loud costs one line and means a reader
            // does not have to re-derive the division by zero to believe it.
            ProjectileRangeData rd = p.RangeData;
            rd.enabled = false;
            p.RangeData = rd;

            // TAKE OVER THE END OF ITS LIFE, both halves.
            //
            // lifetimeData is a RACE if left live: the fuse is clamped to its
            // time, so the two fire on the same frame - HandleLifetime from
            // Update, the pop from FixedUpdate - and if the engine's wins, the
            // object is destroyed and THE DAMAGE NEVER HAPPENS. Copy it, switch
            // it off, and fire its hooks ourselves at the pop.
            //
            // impactBehaviour is copied for the opposite reason: a bloom never
            // reaches OnObjectHit, so its hooks would never fire at all - and
            // `impactBehaviour.spawnExplosion` is the natural way to say "this
            // shot explodes". Left alone, adding an explosion to a bloom weapon
            // would build cleanly and do nothing.
            b.life = p.LifetimeData;
            b.impact = p.ImpactBehaviour;

            ProjectileLifetimeData ld = p.LifetimeData;
            ld.enabled = false;
            p.LifetimeData = ld;

            // Piercing forced ON. Nothing in the engine reaches OnObjectHit on
            // a bloom today, because FixedUpdate is skipped - but this is the
            // guard that stops a future caller from taking the destroy-and-
            // teleport path described in the header, and it costs nothing.
            PiercingData pd = p.PiercingData;
            pd.enabled = true;
            p.PiercingData = pd;
        }

        // Straight out along the firing direction: either the full reach, or
        // the first thing in the way.
        private static Vector2 Blind(
            int layer, Vector2 origin, Vector2 dir, float range,
            ForgeBloom.Blind blind)
        {
            if (blind == ForgeBloom.Blind.FirstHit)
            {
                int mask = Physics2D.GetLayerCollisionMask(layer);

                RaycastHit2D hit = Physics2D.Raycast(origin, dir, range, mask);

                if (hit.collider != null)
                    return hit.point;
            }

            return origin + dir * range;
        }

        // The raw crosshair, and the fallback that makes a wingman aim where
        // YOU are aiming.
        //
        // ForgeVolley reads the Aimer that drives the firing BARREL, which is
        // right for the ship and finds nothing for a companion - a wingman
        // fires from its own body, which has no aimer on it. So the fallbacks
        // walk outward: the firing unit's own aimer, then the only aimer in the
        // scene. That last one is the player's, and it is the answer he asked
        // for - a companion's bloom should land where the player is pointing.
        //
        // Deliberately UNASSISTED. Aim assist bends the BARREL by up to 15
        // degrees on every stock White weapon, and a weapon whose whole promise
        // is "it lands where I point" cannot be built off the barrel.
        private static bool TryCursor(
            Unit shotOwner, WeaponBase weapon, out Vector2 point)
        {
            point = Vector2.zero;

            if (ForgeVolley.Active && ForgeVolley.HasCursor)
            {
                point = ForgeVolley.Cursor;
                return true;
            }

            Unit owner = (weapon != null) ? weapon.Owner : shotOwner;

            if (owner != null)
            {
                var own = owner.GetComponentInChildren<Aimer>(true);

                if (own != null)
                {
                    point = own.TargetPosition;
                    return true;
                }
            }

            // RATE-LIMITED, because the failure case is the expensive one.
            // FindObjectsByType walks the scene, and when it finds nothing (no
            // ship yet) or two (co-op) the result is null - so an unlatched
            // version would re-scan on EVERY SHOT for the rest of the run.
            if (_lastResort == null && Time.time >= _nextScan)
            {
                _nextScan = Time.time + 1f;

                Aimer[] all = UnityEngine.Object.FindObjectsByType<Aimer>(
                    FindObjectsSortMode.None);

                // Exactly one, or we would be guessing whose crosshair to use.
                // In co-op there are two ships, and handing one player's aim to
                // the other player's companion is worse than falling back to a
                // direction.
                _lastResort = (all != null && all.Length == 1) ? all[0] : null;
            }

            // ONLY FOR YOUR OWN SIDE, and this is the guard that matters. The
            // fallback exists so a WINGMAN blooms where you are pointing. An
            // enemy carrying a bloom weapon has no Aimer either - it aims
            // through AimAction - so without this test it would fall through to
            // the same branch and drop its bloom on YOUR CROSSHAIR. Correct for
            // a companion, absurd for an enemy, and it would read as the
            // feature being broken rather than as a missing check.
            if (_lastResort != null && Friendly(owner, _lastResort))
            {
                point = _lastResort.TargetPosition;
                return true;
            }

            if (!_saidNoCursor)
            {
                _saidNoCursor = true;

                Log.LogInfo(
                    "bloom: this shot had no crosshair to read, so it was " +
                    "placed by direction instead (\"blind\"). That is normal " +
                    "for a minion, a turret or an enemy carrying a bloom " +
                    "weapon.");
            }

            return false;
        }

        [HarmonyPatch(typeof(Projectile), "FixedUpdate")]
        public class OnFixedUpdate
        {
            // LAST of this method's prefixes - see the header. Every other one
            // is void, so this skips nothing but the original.
            [HarmonyPriority(Priority.Last)]
            static bool Prefix(Projectile __instance)
            {
                if (!ForgeBloom.Any)
                    return true;

                var b = __instance.GetComponent<ForgeBloomShot>();

                if (b == null)
                    return TickBullet(__instance);

                // GATED ON THE CONFIG, NOT ON `live`, and the difference is a
                // real window. Pop() clears `live` and then calls Destroy, and
                // Destroy does not remove the component until the end of the
                // frame - so a `!live` test here would hand a popped bloom
                // straight back to the engine's own sweep, at FULL radius, with
                // whatever drift velocity it was carrying. That is one extra
                // set of hits on the way out.
                if (b.cfg == null)
                    return true;

                try
                {
                    if (b.live)
                        b.Tick(Time.deltaTime);
                }
                catch (Exception e)
                {
                    b.cfg = null;

                    Log.LogError(
                        "bloom: failed mid-swell, the bubble is dropped: " + e);

                    UnityEngine.Object.Destroy(__instance.gameObject);
                }

                // The original never runs on a bloom. See the header.
                return false;
            }
        }

        // A plain bloom BULLET's step. The engine's own FixedUpdate runs unless
        // the bullet ended this step, so its hits are the game's own.
        private static bool TickBullet(Projectile p)
        {
            var bb = p.GetComponent<ForgeBloomBullet>();

            if (bb == null || bb.cfg == null)
                return true;

            try
            {
                return bb.TickPlain(Time.deltaTime);
            }
            catch (Exception e)
            {
                bb.cfg = null;

                Log.LogError(
                    "bloom: a bullet failed mid-flight and is dropped (its " +
                    "range is off, so it would otherwise fly forever): " + e);

                UnityEngine.Object.Destroy(p.gameObject);
                return false;
            }
        }

        // ==================================================================
        // A MISSILE (a PhysicsProjectile with no gravity) in bullet mode.
        // ==================================================================

        [HarmonyPatch(typeof(PhysicsProjectile), "Shoot")]
        public class OnPhysicsShoot
        {
            [HarmonyPriority(Priority.First)]
            static void Prefix(PhysicsProjectile __instance)
            {
                if (!ForgeBloom.Any)
                    return;

                // FireSingle calls Shoot TWICE on a physics shot and the first
                // is against a BLANK one - no Owner, no velocity, no range. A
                // placement there would latch the blanks AND take a ring slot
                // (TakeIndex is once per pellet), so the real call would land
                // on the next slot. Skipped; `placed` guards a third.
                if (ForgePhysicsShot.NotReady(__instance))
                    return;

                PlaceBullet(__instance, __instance);
            }

            static void Postfix(PhysicsProjectile __instance)
            {
                if (!ForgeBloom.Any || ForgePhysicsShot.NotReady(__instance))
                    return;

                BeginBullet(__instance);
            }
        }

        // That method's whole body is Homing() - thrust, torque and wobble.
        // `fly` skips it; `chase` aims it and lets it run.
        [HarmonyPatch(typeof(PhysicsProjectile), "FixedUpdate")]
        public class OnPhysicsFixedUpdate
        {
            [HarmonyPriority(Priority.Last)]
            static bool Prefix(PhysicsProjectile __instance)
            {
                if (!ForgeBloom.Any)
                    return true;

                var bb = __instance.GetComponent<ForgeBloomBullet>();

                if (bb == null || bb.cfg == null)
                    return true;

                try
                {
                    return bb.TickPhysics(Time.deltaTime);
                }
                catch (Exception e)
                {
                    bb.cfg = null;

                    Log.LogError(
                        "bloom: a missile failed mid-flight and is dropped: " + e);

                    UnityEngine.Object.Destroy(__instance.gameObject);
                    return false;
                }
            }
        }

        public static void Reset()
        {
            // The Aimer is a scene object, so it does not survive a run
            // boundary - and a destroyed Unity object compares equal to null,
            // which would re-find it anyway. Cleared here so the re-find is
            // deterministic rather than relying on that.
            _lastResort = null;
            _nextScan = 0f;
        }
    }
}
