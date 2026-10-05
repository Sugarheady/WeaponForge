using System;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace WeaponForge
{
    // Make a minion follow you when its prefab has no idea how to.
    //
    // WHY THIS CANNOT BE DONE WITH THE PREFAB'S OWN AI. Following an owner is a
    // whole AI STATE, not a number. Compare the state children:
    //
    //   Enemy_Fly Variant Drone   Idle / Aggro / Seek / **Regroup**
    //   Enemy_Larva               Idle / Aggro
    //   Enemy_Maggot              Idle / Aggro / Charge / Seek
    //
    // `Regroup` is the come-back-to-your-owner state, and only the drone
    // prefabs - the five the game itself uses as minions - have one. It carries
    // a `MoveAroundOwnerAction` plus the conditions that enter and leave it.
    // So `followDistance` and `leash`, which tune exactly those, are honest
    // no-ops on every other prefab: there is nothing there to tune. That is why
    // "only the original player drones seem to continue to follow" - the others
    // were never able to.
    //
    // Building the state at runtime would mean creating a State child, a
    // MovementAction, the transition conditions and their serialized
    // cross-references, on a StateMachine that has already Awoken. This does
    // the same job from outside instead: when the minion has nothing to fight,
    // lean on its rigidbody in your direction.
    //
    // IT DELIBERATELY DOES NOT DRIVE. It adds force and lets the prefab's own
    // movement keep doing whatever it does, for the reason recorded in
    // ForgeMinionStats: `MovementAction.OnEnable` snapshots the movement
    // component's values and `OnDisable` restores them, so anything written
    // into a mover mid-state is reverted at state exit - or worse, snapshotted
    // while modified and made permanent. A rigidbody force is outside all of
    // that, the same reason the slow debuff works that way.
    //
    // FORCE IS COMPENSATED FOR MASS AND DAMPING. A constant force settles at
    // `force / (mass * damping)`, and PUNK's bodies range from mass 1 damping
    // 0.5 (the ship) to mass 200 damping 4 (the Queen) - a factor of 1600. So
    // the config asks for a SPEED and the component works out the force, the
    // same approach ForgeGravityController takes.
    public class ForgeMinionEscort : MonoBehaviour
    {
        private static readonly ManualLogSource Log =
            ForgeLog.Source("WeaponForge.Minion");

        private Unit _owner;
        private Rigidbody2D _body;
        private AIAgent _agent;
        private bool _lookedForAgent;

        private float _close = 6f;     // stop closing inside this
        private float _recall = 24f;   // come back even mid-fight past this
        private float _speed = 9f;     // cruise speed of the lean

        // Nothing at all to do until this many seconds after spawn: a minion is
        // launched by the spawner at `projectileSpeed`, and pulling it back
        // during that flight would cancel the throw.
        private float _freeUntil;

        // Prefab names already reported, so a swarm of twenty logs once.
        private static readonly System.Collections.Generic.HashSet<string> _said =
            new System.Collections.Generic.HashSet<string>();

        public static void Attach(
            Unit minion, Unit owner, ForgeMinion.Config cfg)
        {
            if (minion == null || owner == null || cfg == null || !cfg.follow)
                return;

            // HOLD WINS OUTRIGHT. The two are opposite answers to one question
            // - keep to me, or keep to a spot - so running both would lean one
            // body toward two different points and the minion would sit on the
            // line between them. Enforced here rather than in the builder so it
            // covers the drone gadget and the spawner weapon in one place, and
            // silently, because the builder has already said which stance won.
            if (cfg.hold)
                return;

            // DOES ITS PREFAB ALREADY KNOW HOW TO DO THIS? A Regroup state
            // carries a MoveAroundOwnerAction, and on a prefab that has one,
            // leaning on the rigidbody as well means two things propelling the
            // same body toward the same point - it overshoots and orbits.
            //
            // So unless the file said `"follow": true` outright, leave the five
            // prefabs the game itself built for this job alone. This is why the
            // default is three-state rather than a plain bool.
            if (!cfg.followSet && CanFollowItself(minion))
            {
                if (_said.Add(minion.name))
                {
                    Log.LogInfo(
                        "minion follow: '" + minion.name + "' has a Regroup AI " +
                        "state of its own (a MoveAroundOwnerAction), so it is " +
                        "left to follow you the way the game intended - pushing " +
                        "it as well would make it overshoot and orbit. Set " +
                        "\"follow\": true to force the push on anyway.");
                }

                return;
            }

            try
            {
                var e = minion.gameObject.GetComponent<ForgeMinionEscort>();

                if (e == null)
                    e = minion.gameObject.AddComponent<ForgeMinionEscort>();

                e._owner = owner;
                e._body = minion.RigidBody;

                if (e._body == null)
                    e._body = minion.GetComponentInChildren<Rigidbody2D>(true);

                // Reuse the numbers the author already has in front of them
                // rather than inventing a second vocabulary for the same idea.
                e._close = cfg.followDistance.set
                    ? Mathf.Max(0.5f, cfg.followDistance.value)
                    : 6f;

                e._recall = cfg.leash.set
                    ? Mathf.Max(e._close + 1f, cfg.leash.value)
                    : e._close * 4f;

                e._speed = cfg.followSpeed > 0f ? cfg.followSpeed : 9f;

                e._freeUntil = Time.time + Mathf.Max(0f, cfg.followDelay);

                if (e._body == null)
                {
                    Log.LogWarning(
                        "minion \"follow\": '" + minion.name + "' has no " +
                        "rigidbody, so it cannot be pulled along. Turrets and " +
                        "beacons are like this - they are anchored by design.");
                }
            }
            catch (Exception ex)
            {
                Log.LogError("Attaching a minion escort failed: " + ex);
            }
        }

        private void FixedUpdate()
        {
            // Unity's overloaded == : a destroyed owner compares equal to null,
            // which is exactly the test wanted here.
            if (_body == null || _owner == null)
                return;

            if (Time.time < _freeUntil)
                return;

            try
            {
                Lean();
            }
            catch (Exception e)
            {
                Log.LogError("Minion escort failed: " + e);
                enabled = false;
            }
        }

        private void Lean()
        {
            Vector2 here = _body.position;
            Vector2 there = _owner.transform.position;
            Vector2 gap = there - here;
            float distance = gap.magnitude;

            if (distance <= _close)
                return;

            // Busy fighting something? Leave it alone, unless it has wandered
            // past the leash - then bring it home whatever it is doing, which
            // is what a leash is for.
            if (distance < _recall && Fighting())
                return;

            Vector2 dir = gap / distance;

            // Ease in over the last stretch so it settles beside you instead
            // of overshooting and orbiting.
            float t = Mathf.Clamp01((distance - _close) / Mathf.Max(1f, _close));

            // ForgeMinionMove owns the mass/damping compensation, because
            // ForgeMinionHold needs the identical arithmetic for its own
            // fallback and two copies of it would drift apart.
            ForgeMinionMove.Lean(_body, dir, _speed * t);
        }

        // Shared with ForgeMinionHold, which has to switch that same Regroup
        // state OFF for as long as it is holding a post. See
        // ForgeMinionMove.FollowsOwnerItself for why it goes by type name.
        private static bool CanFollowItself(Unit minion)
        {
            return ForgeMinionMove.FollowsOwnerItself(minion);
        }

        private bool Fighting()
        {
            if (!_lookedForAgent)
            {
                _lookedForAgent = true;
                _agent = GetComponentInChildren<AIAgent>(true);
            }

            // No AI at all - a crate, a box - so it is never busy and always
            // gets escorted.
            return _agent != null && _agent.HasTarget;
        }
    }
}
