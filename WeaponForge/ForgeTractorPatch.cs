using System;
using System.Collections.Generic;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace WeaponForge
{
    // Drives a tractor beam off the ship's Shooter, the same way orbit's Hold
    // mode does - Shooter is the only path in the game with a held trigger, and
    // `IsShooting` is that flag. `Shooter.Barrel` is public and exposes Position
    // and Direction live, which is exactly what an aimed continuous effect needs
    // and what a fired shot cannot give us (nothing is ever fired here).
    //
    // Note gadget weapons bypass Shooter entirely, so a tractor beam has to live
    // in a normal weapon slot.
    public static class ForgeTractorPatch
    {
        private static readonly ManualLogSource Log =
            ForgeLog.Source("WeaponForge.Tractor");

        private static readonly List<Collider2D> _hits = new List<Collider2D>(48);
        private static readonly List<Unit> _caught = new List<Unit>(16);

        [HarmonyPatch(typeof(Shooter), "Update")]
        public class OnUpdate
        {
            static void Postfix(Shooter __instance)
            {
                if (!ForgeTractor.Any)
                    return;

                try
                {
                    WeaponBase weapon = __instance.Weapon;
                    ForgeTractor.Config cfg;

                    if (weapon == null ||
                        !ForgeTractor.TryGet(weapon.TemplateData, out cfg))
                        return;

                    if (!__instance.IsShooting)
                        return;

                    Drive(__instance, weapon, cfg);
                }
                catch (Exception e)
                {
                    Log.LogError("Tractor beam failed: " + e);
                }
            }
        }

        // A held beam never fires a shot, so the weapon's `cost` is never taken
        // - exactly the problem the orbit ring has. Charged per second instead.
        private static bool Pay(Shooter shooter, WeaponBase weapon, float amount)
        {
            if (amount <= 0f)
                return true;

            Unit unit = shooter.Unit;

            if (unit == null || weapon.ResourceUsed == null)
                return true;

            if (!unit.HasTank(weapon.ResourceUsed))
                return true;

            ResourceTank tank = unit.GetTank(weapon.ResourceUsed);

            if (tank == null)
                return true;

            // ResourceTank.Value has NO floor - its setter assigns whatever it
            // is given - so anything draining per frame has to clamp itself or
            // the tank goes negative and the beam runs for free.
            if (tank.Value < amount)
                return false;

            tank.Value = Mathf.Max(0f, tank.Value - amount);
            return true;
        }

        private static void Drive(
            Shooter shooter, WeaponBase weapon, ForgeTractor.Config cfg)
        {
            IBarrel barrel = shooter.Barrel;

            if (barrel == null)
                return;

            if (!Pay(shooter, weapon, cfg.drainPerSecond * Time.deltaTime))
                return;

            Vector2 origin = barrel.Position;
            Vector2 aim = barrel.Direction;

            if (aim.sqrMagnitude < 0.000001f)
                return;

            aim = aim.normalized;

            Unit owner = shooter.Unit;

            if (cfg.mode == ForgeTractor.Mode.Grapple)
            {
                Grapple(origin, aim, cfg, owner, weapon);
                return;
            }

            Collect(origin, aim, cfg, owner);

            // Drawn to the hold point in Hold mode (that is the spot you are
            // steering to, so it is the useful thing to see) and to the end of
            // the beam otherwise.
            Vector2 tip = (cfg.mode == ForgeTractor.Mode.Hold)
                ? origin + aim * cfg.holdDistance
                : origin + aim * cfg.range;

            Beam(cfg, origin, tip, _caught.Count > 0);

            for (int i = 0; i < _caught.Count; i++)
                Move(_caught[i], origin, aim, cfg, owner, weapon);
        }

        // GRAPPLE: the beam anchors on what it is pointed at and reels YOU in.
        //
        // A RAYCAST rather than the overlap sweep the other modes use, because
        // the anchor is normally terrain - and terrain is not a Unit, so the
        // sweep would never find it. Ground is the layer the level's collision
        // segments live on, the same surface the ship already flies into.
        //
        // The force goes through the same mass/damping compensation as
        // everything else, which lands nicely here: the compensation makes the
        // settling speed `force / 30` for ANY body, so one `force` number pulls
        // the ship at the same rate it drags a Cross. No separate tuning.
        private static void Grapple(
            Vector2 origin, Vector2 aim, ForgeTractor.Config cfg,
            Unit owner, WeaponBase weapon)
        {
            if (owner == null)
                return;

            int mask = cfg.grappleToUnits
                ? LayerMask.GetMask("Ground", "Entities")
                : LayerMask.GetMask("Ground");

            RaycastHit2D hit = Physics2D.Raycast(origin, aim, cfg.range, mask);

            if (hit.collider == null)
            {
                // Nothing to bite. Still draw, faint and full length, so it is
                // obvious the beam is live and simply has no anchor - the
                // difference between "not firing" and "found no wall" was
                // impossible to see before.
                Beam(cfg, origin, origin + aim * cfg.range, false);
                Say(cfg, "grapple: no anchor within " + cfg.range +
                    " units on layer " + (cfg.grappleToUnits
                        ? "Ground/Entities" : "Ground") +
                    " - aim at terrain, or set \"grappleToUnits\": true.");
                return;
            }

            Beam(cfg, origin, hit.point, true);

            // The ship's Rigidbody2D and its Unit are both on the prefab ROOT
            // (checked on ShipKBM), so searching from the Unit finds it - but
            // say so if it ever does not, because a silent null here looks
            // exactly like a physics problem.
            Rigidbody2D rb = owner.GetComponentInChildren<Rigidbody2D>();

            if (rb == null)
            {
                Say(cfg, "grapple: anchored on '" + hit.collider.name +
                    "' but found no Rigidbody2D on " + owner.name +
                    " to pull.");
                return;
            }

            Vector2 to = hit.point - (Vector2)owner.transform.position;
            float d = to.magnitude;

            // Arrived. Stopping the pull rather than the ship, so momentum
            // carries you the last bit and you can swing past a corner.
            if (d <= cfg.grappleStopDistance)
            {
                Say(cfg, "grapple: already inside grappleStopDistance (" +
                    cfg.grappleStopDistance + ") of the anchor, so there is " +
                    "nothing to reel in. Lower it if the beam feels dead up " +
                    "close.");
                return;
            }

            float comp = Compensation(rb);

            Say(cfg, "grapple: anchored on '" + hit.collider.name + "' at " +
                d.ToString("0.#") + " units. Pulling " + owner.name +
                " (mass " + rb.mass.ToString("0.##") + ", damping " +
                rb.linearDamping.ToString("0.##") + ") with force " +
                (cfg.force * comp).ToString("0.##") +
                ", which settles at about " + (cfg.force / 30f).ToString("0.#") +
                " units/sec, capped at maxSpeed " + cfg.maxSpeed + ".");

            rb.AddForce(to / d * cfg.force * comp);

            if (rb.linearVelocity.magnitude > cfg.maxSpeed)
                rb.linearVelocity = rb.linearVelocity.normalized * cfg.maxSpeed;

            // THE PIECE THAT COMPLETES PLAYER SLAM DAMAGE. Off by default,
            // because being reeled into a wall and hurt for it makes a grapple
            // miserable - but this is the one thing in the mod that can throw
            // the PLAYER, so it is the only way that half can ever fire.
            if (cfg.slam && cfg.grappleSlamsYou)
            {
                ForgeSlam.Mark(
                    owner, cfg, owner, weapon,
                    (weapon != null) ? weapon.Damage.damageType : null);
            }
        }

        // Everything the beam is holding this frame.
        //
        // One OverlapCircle at `range` and then a shape test, rather than a
        // cone-shaped physics query, because Physics2D has no cone - and the
        // circle is the same query the gravity well already uses.
        private static void Collect(
            Vector2 origin, Vector2 aim, ForgeTractor.Config cfg, Unit owner)
        {
            _caught.Clear();

            var filter = new ContactFilter2D();
            filter.useTriggers = true;

            // The ship is on `Player`, NOT `Entities` - an Entities-only mask is
            // how the gravity well's pullPlayer came to be dead code.
            filter.SetLayerMask(
                LayerMask.GetMask("Entities", "Fruits", "Player", "Items"));

            int count = Physics2D.OverlapCircle(origin, cfg.range, filter, _hits);

            float cos = Mathf.Cos(Mathf.Clamp(cfg.cone, 0f, 180f) *
                                  Mathf.Deg2Rad);

            for (int i = 0; i < count && i < _hits.Count; i++)
            {
                Collider2D col = _hits[i];

                if (col == null)
                    continue;

                Unit unit = col.GetComponentInParent<Unit>();

                if (unit == null || unit == owner)
                    continue;

                if (!cfg.affectsPlayer && owner != null &&
                    owner.IsFriendsWith(unit))
                    continue;

                if (Contains(_caught, unit))
                    continue;

                Vector2 to = (Vector2)unit.transform.position - origin;
                float d = to.magnitude;

                if (d < 0.001f || d > cfg.range)
                    continue;

                Vector2 dir = to / d;

                if (cfg.IsLine)
                {
                    // Distance from the ray, and only ahead of the barrel.
                    float along = Vector2.Dot(to, aim);

                    if (along <= 0f)
                        continue;

                    float off = (to - aim * along).magnitude;

                    if (off > cfg.beamWidth)
                        continue;
                }
                else if (Vector2.Dot(aim, dir) < cos)
                {
                    continue;
                }

                _caught.Add(unit);

                if (cfg.maxTargets > 0 && _caught.Count >= cfg.maxTargets)
                    break;
            }
        }

        private static bool Contains(List<Unit> list, Unit u)
        {
            for (int i = 0; i < list.Count; i++)
                if (list[i] == u)
                    return true;

            return false;
        }

        private static void Move(
            Unit unit, Vector2 origin, Vector2 aim, ForgeTractor.Config cfg,
            Unit owner, WeaponBase weapon)
        {
            Rigidbody2D rb = unit.GetComponentInChildren<Rigidbody2D>();

            if (rb == null)
                return;

            Vector2 to = (Vector2)unit.transform.position - origin;
            float d = to.magnitude;

            if (d < 0.001f)
                return;

            Vector2 dir = to / d;
            Vector2 push;

            switch (cfg.mode)
            {
                case ForgeTractor.Mode.Push:
                    push = dir;
                    break;

                case ForgeTractor.Mode.Hold:
                {
                    // A CRITICALLY-DAMPED SPRING toward the aim point, not a
                    // constant force plus a fixed velocity bleed.
                    //
                    // The old version pushed at full force however close the
                    // target already was and then scrubbed 8% of its speed per
                    // frame. That is a weak, mass-dependent spring: a heavy
                    // enemy lagged well behind the crosshair and a light one
                    // wallowed past it, which is why lining one up with a wall
                    // was "rough" and "needs to be more stiff and rigid to the
                    // cursor point". A spring whose force falls off with
                    // distance and whose damping is matched to it arrives once
                    // and stays, and it tracks the crosshair while it does.
                    Vector2 anchor = origin + aim * cfg.holdDistance;
                    Vector2 toAnchor = anchor - (Vector2)unit.transform.position;

                    float k = Mathf.Clamp01(cfg.holdStiffness);

                    if (k <= 0f)
                    {
                        // The old soft behaviour, kept reachable on purpose.
                        push = (toAnchor.sqrMagnitude < 0.01f)
                            ? Vector2.zero : toAnchor.normalized;
                        rb.linearVelocity *= 0.92f;
                        break;
                    }

                    // omega is the spring's natural frequency; at critical
                    // damping the response is 2*omega on velocity, which is
                    // what removes the overshoot rather than merely shrinking
                    // it. Scaled by mass because AddForce is not.
                    float omega = Mathf.Lerp(3f, 14f, k);

                    Vector2 accel =
                        toAnchor * (omega * omega) -
                        rb.linearVelocity * (2f * omega);

                    rb.AddForce(accel * rb.mass);

                    if (rb.linearVelocity.magnitude > cfg.maxSpeed)
                    {
                        rb.linearVelocity =
                            rb.linearVelocity.normalized * cfg.maxSpeed;
                    }

                    if (cfg.slam)
                    {
                        ForgeSlam.Mark(
                            unit, cfg, owner, weapon,
                            (weapon != null) ? weapon.Damage.damageType : null);
                    }

                    return;
                }

                default:
                    push = -dir;
                    break;
            }

            if (push.sqrMagnitude < 0.000001f)
                return;

            rb.AddForce(push * cfg.force * Compensation(rb));

            if (rb.linearVelocity.magnitude > cfg.maxSpeed)
                rb.linearVelocity = rb.linearVelocity.normalized * cfg.maxSpeed;

            // Slammable while held and for a moment after - so letting go at
            // speed still counts as having thrown it.
            if (cfg.slam)
            {
                ForgeSlam.Mark(
                    unit, cfg, owner, weapon,
                    (weapon != null) ? weapon.Damage.damageType : null);
            }
        }

        private static void Beam(
            ForgeTractor.Config cfg, Vector2 from, Vector2 to, bool anchored)
        {
            if (!cfg.drawBeam)
                return;

            try
            {
                ForgeTractorBeam.Instance.Draw(
                    from, to, cfg.color, cfg.beamWidth, anchored);
            }
            catch (Exception e)
            {
                Log.LogWarning("Tractor beam visual failed: " + e.Message);
                cfg.drawBeam = false;
            }
        }

        // One line per distinct message, at most every two seconds - enough to
        // diagnose from a log without drowning it at 60 frames a second.
        private static string _lastSaid;
        private static float _lastSaidAt;

        private static void Say(ForgeTractor.Config cfg, string message)
        {
            if (!cfg.grappleDebug)
                return;

            if (message == _lastSaid && Time.time - _lastSaidAt < 2f)
                return;

            _lastSaid = message;
            _lastSaidAt = Time.time;
            Log.LogInfo(message);
        }

        // AddForce fights BOTH mass and damping, and PUNK's bodies differ wildly
        // in both: ship 1/0.5, a common Cross 5/6, a Jock 50/4, the Queen 200/4.
        // A constant force settles at force/(mass*damping), so the same number
        // moves the ship 60x faster than a common enemy and over 1000x faster
        // than the Queen. Normalising to a Cross (5 x 6 = 30) makes `force`
        // mean roughly the same drag speed on everything - the same fix the
        // gravity well needed after R6.
        private static float Compensation(Rigidbody2D rb)
        {
            float damping = Mathf.Max(0.05f, rb.linearDamping);
            float mass = Mathf.Max(0.01f, rb.mass);

            return (mass * damping) / 30f;
        }
    }
}
