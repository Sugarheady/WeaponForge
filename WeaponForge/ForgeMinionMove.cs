using System;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace WeaponForge
{
    // The two ways to make a minion go somewhere, in one place because
    // ForgeMinionEscort and ForgeMinionHold both need both of them and a
    // second copy of either would drift.
    //
    // ROUTE ONE - `Walk`: hand the position to the prefab's OWN mover.
    // `UnitMovement.MoveTo(Vector2)` is public and abstract, and both
    // implementations do the same two things with it:
    //
    //     public override void MoveTo(Vector2 position)
    //     { this.targetPosition = position; this.isStopped = false; }
    //
    // and then each one's own `FixedUpdate` propels the body toward
    // `targetPosition` at its own speed, with its own cadence and its own
    // random angle jitter. So this is strictly better than pushing the body
    // ourselves whenever it is available: the creature walks home the way that
    // creature moves, and there is no force to calibrate.
    //
    // It is also SAFE FROM THE SNAPSHOT TRAP that shapes everything else in
    // this area. `MovementAction.OnEnable` records `movement.GetCurrentValues()`
    // and `OnDisable` writes it back wholesale, which is why nothing may edit a
    // mover's tuning mid-state (see ForgeMinionStats). `targetPosition` is not
    // in `ValueOverride` and not in `GetCurrentValues()` - it is not tuning, it
    // is a destination - so writing it is untouched by that cycle.
    //
    // ROUTE TWO - `Lean`: push the rigidbody directly. Needed when no mover is
    // active (a unit sitting in a `Wait` sub-state has none) and when the prefab
    // has no mover at all - turrets and beacons.
    public static class ForgeMinionMove
    {
        private static readonly ManualLogSource Log =
            ForgeLog.Source("WeaponForge.Minion");

        // ---- route one ---------------------------------------------------

        // BOTH movers open their FixedUpdate with `if (targetPosition ==
        // Vector2.zero) return;` - a sentinel for "never told where to go" that
        // a real destination can land on. A post stamped at the world origin
        // would silently never be walked to, so it gets nudged off it by a
        // fraction of a pixel rather than the caller having to know this.
        public static bool Walk(UnitMovement mover, Vector2 to)
        {
            if (mover == null || !mover.isActiveAndEnabled)
                return false;

            if (to.sqrMagnitude < 1e-6f)
                to = new Vector2(0.001f, 0.001f);

            try
            {
                mover.MoveTo(to);
                return true;
            }
            catch (Exception e)
            {
                Log.LogError("Walking a minion failed: " + e);
                return false;
            }
        }

        // ---- route two ---------------------------------------------------

        // FORCE IS COMPENSATED FOR MASS AND DAMPING. A constant force settles at
        // `force / (mass * damping)`, and PUNK's bodies range from mass 1 damping
        // 0.5 (the ship) to mass 200 damping 4 (the Queen) - a factor of 1600. So
        // callers ask for a SPEED and this works out the force, the same approach
        // ForgeGravityController takes.
        public static void Lean(Rigidbody2D body, Vector2 dir, float want)
        {
            if (body == null || want <= 0f)
                return;

            // Damping of zero would mean a body that never stops, so the lean
            // becomes a one-off nudge up to the cruise speed instead of a
            // continuous force that accumulates forever.
            float damping = body.linearDamping;

            if (damping > 0.01f)
            {
                body.AddForce(dir * (want * body.mass * damping));
                return;
            }

            float along = Vector2.Dot(body.linearVelocity, dir);

            if (along < want)
            {
                body.AddForce(
                    dir * ((want - along) * body.mass), ForceMode2D.Impulse);
            }
        }

        // ---- who can already come home by itself -------------------------

        // By TYPE NAME, because MoveAroundOwnerAction is only interesting for
        // its presence - nothing here reads a field on it - and a type lookup
        // keeps this working if the class is ever renamed or absent.
        //
        // A `Regroup` AI state carries one, and only the five drone prefabs the
        // game itself uses as minions have a Regroup state. That single fact is
        // why `followDistance` and `leash` are honest no-ops on everything else,
        // why escort defaults to three-state rather than a plain bool, and why
        // `hold` has to switch this off (a drone that regroups on YOU is the
        // exact opposite of holding a post).
        private static Type _ownerFollow;
        private static bool _looked;

        public static Type OwnerFollowType
        {
            get
            {
                if (!_looked)
                {
                    _looked = true;
                    _ownerFollow = AccessTools.TypeByName("MoveAroundOwnerAction");
                }

                return _ownerFollow;
            }
        }

        // includeInactive is MANDATORY, not defensive: StateMachine.Awake
        // disables every state child, so the Regroup subtree is switched off by
        // the time anything looks for it.
        public static bool FollowsOwnerItself(Unit minion)
        {
            try
            {
                Type t = OwnerFollowType;

                if (t == null || minion == null)
                    return false;

                Component[] all = minion.GetComponentsInChildren(t, true);

                return all != null && all.Length > 0;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
