using System;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;

namespace WeaponForge
{
    // HOLD GROUND. The other half of the pair whose escort half is
    // ForgeMinionEscort: *"I could see a weapon where you spawn a bunch in a
    // spot and run away."*
    //
    // Opt-in, and mutually exclusive with `follow` - they are opposite answers
    // to one question, so `hold` makes the escort stand down rather than the two
    // of them pulling the same body to two different places.
    //
    // ---- WHY THE LEASH IS ENFORCED BY MOVEMENT AND NOT BY TARGETING --------
    //
    // The obvious way to stop a minion chasing something out of its post is to
    // take its target away. That cannot work, and the reason is worth writing
    // down because it looks like it should:
    //
    //     AIAgent.Update:
    //         RefreshEnemyAndFriendLists();
    //         if (HasTarget && IsTargetVisible) { ...; return; }
    //         SelectTargetFromVisibleEnemies();      <-- every frame
    //
    // `SelectTargetFromVisibleEnemies` takes `visibleEnemies.First()`, and it is
    // reached on any frame the agent has no visible target. So `ForgetTarget()`
    // on a target that is still in Vision is undone on the same frame it is
    // called - a per-frame thrash, not a leash. (Blinding it instead would stop
    // it fighting at all, which is not what a held minion is for.)
    //
    // ---- SO: SUSPEND ITS MOVEMENT, AND WALK IT HOME WITH ITS OWN LEGS ------
    //
    // Two facts make this clean rather than a tug of war.
    //
    // 1. `MovementAction.Update` is the ONLY thing steering a unit -
    //    `movement.MoveTo(path.vectorPath[currentWaypoint])`, every frame while
    //    that state is active. Disable the action and nothing is steering.
    //    (`ChargerHead`, the beak on a Larva or Maggot, was worth checking here
    //    and does NOT propel: it casts for damage and applies a recoil impulse
    //    to itself, nothing more. So suspending the movement action really does
    //    stop a charge.)
    // 2. `UnitMovement.MoveTo` is public, and the mover's own FixedUpdate then
    //    carries the body at the creature's own speed. See ForgeMinionMove.
    //
    // So while a held minion is outside its post it has its own steering
    // suspended and is told to walk to the post; inside, it is released and
    // behaves EXACTLY as it always did - it wanders, charges, rams and shoots
    // with nothing of ours in the way. That is the whole rule, and it is the
    // reason a held Larva is still dangerous rather than a statue.
    //
    // ---- THE ONE UNCONDITIONAL SUPPRESSION --------------------------------
    //
    // `MoveAroundOwnerAction` (the Regroup state, drones only) is disabled for
    // as long as hold is on, not merely when outside the post. It is the exact
    // opposite behaviour, and leaving it to the radius rule would oscillate:
    // regroup drags the drone toward you, it leaves the post, we walk it back,
    // we release it, regroup drags it toward you again. On a drone that is a
    // visible shudder several times a second.
    public class ForgeMinionHold : MonoBehaviour
    {
        private static readonly ManualLogSource Log =
            ForgeLog.Source("WeaponForge.Minion");

        private Rigidbody2D _body;

        private float _radius = 8f;
        private float _speed = 9f;

        // Where the post is, and when it gets stamped.
        //
        // THE POST IS WHERE IT COMES TO REST, not where it appeared. A spawner
        // throws its minions at `projectileSpeed` (60 +/- 40 on every stock
        // template), so the barrel is not the spot the player meant - the spot
        // they meant is roughly where the throw lands. Stamping the post a
        // moment after spawn gets that for free, and costs nothing on a minion
        // that is placed rather than thrown, where the two are the same point.
        // Same number and the same reason as the escort's `followDelay`.
        private Vector2 _post;
        private bool _posted;
        private float _armAt;
        private float _delay;

        // Actions we have switched off and must switch back on. Two lists,
        // because they come back at different times: the radius ones the moment
        // it is home, the owner-follow ones not until the minion dies.
        private readonly List<MovementAction> _suspended =
            new List<MovementAction>();

        private readonly List<MovementAction> _ownerFollow =
            new List<MovementAction>();

        // Cached ONCE. The set of actions on a prefab never changes, so this is
        // an allocation at spawn instead of one per minion per frame - which at
        // a cap of twenty would be twenty GetComponentsInChildren calls every
        // frame, each of which allocates an array.
        private MovementAction[] _actions;
        private UnitMovement[] _movers;

        private bool _out;          // currently outside the post
        private float _nextToggle;  // hysteresis in time as well as distance

        private static readonly HashSet<string> _said = new HashSet<string>();

        public static void Attach(Unit minion, ForgeMinion.Config cfg)
        {
            if (minion == null || cfg == null || !cfg.hold)
                return;

            try
            {
                var h = minion.gameObject.GetComponent<ForgeMinionHold>();

                if (h == null)
                    h = minion.gameObject.AddComponent<ForgeMinionHold>();

                h._body = minion.RigidBody;

                if (h._body == null)
                    h._body = minion.GetComponentInChildren<Rigidbody2D>(true);

                h._radius = Mathf.Max(1f, cfg.holdRadius);
                h._speed = cfg.holdSpeed > 0f ? cfg.holdSpeed : 9f;
                h._delay = Mathf.Max(0f, cfg.holdDelay);
                h._armAt = Time.time + h._delay;

                // includeInactive is MANDATORY: StateMachine.Awake has already
                // disabled every state child by the time a spawn hook runs, so
                // without the flag this finds nothing and the feature silently
                // does nothing at all.
                h._actions =
                    minion.GetComponentsInChildren<MovementAction>(true);

                h._movers =
                    minion.GetComponentsInChildren<UnitMovement>(true);

                if (_said.Add(minion.name))
                    h.Report(minion);
            }
            catch (Exception e)
            {
                Log.LogError("Attaching a minion hold failed: " + e);
            }
        }

        private void Report(Unit minion)
        {
            int actions = (_actions != null) ? _actions.Length : 0;
            int movers = (_movers != null) ? _movers.Length : 0;

            if (_body == null)
            {
                Log.LogWarning(
                    "minion \"hold\": '" + minion.name + "' has no rigidbody, " +
                    "so it has no position to be pulled back to. Turrets and " +
                    "beacons are like this - they are anchored by design and " +
                    "already hold their ground for free.");

                return;
            }

            if (actions == 0 && movers == 0)
            {
                Log.LogInfo(
                    "minion \"hold\": '" + minion.name + "' has no movement " +
                    "components at all, so it cannot leave its post in the " +
                    "first place. Nothing to do, and nothing wrong.");

                return;
            }

            Log.LogInfo(
                "minion \"hold\": '" + minion.name + "' will keep to a post " +
                _radius.ToString("0.##") + " units across, stamped " +
                _delay.ToString("0.##") + "s after it spawns (wherever the " +
                "spawner's throw leaves it). Found " + movers + " mover(s) and " +
                actions + " movement action(s). Inside the post it behaves " +
                "exactly as normal - it wanders, charges and shoots; outside, " +
                "its own steering is suspended and it walks back using its own " +
                "legs. It will NOT follow you: \"hold\" and \"follow\" are " +
                "opposite answers to one question, so the escort stands down.");
        }

        private void FixedUpdate()
        {
            if (_body == null)
                return;

            try
            {
                Tick();
            }
            catch (Exception e)
            {
                Log.LogError("Minion hold failed: " + e);

                // Never leave a minion with its movement switched off because
                // our own code threw - that would be a permanently paralysed
                // unit, which is worse than no feature.
                Release();
                enabled = false;
            }
        }

        private void Tick()
        {
            if (!_posted)
            {
                if (Time.time < _armAt)
                    return;

                _post = _body.position;
                _posted = true;

                // Only now, because a drone regrouping during the throw is
                // harmless and this is the point from which it would fight us.
                SuspendOwnerFollow();
            }

            float distance = Vector2.Distance(_body.position, _post);

            if (_out)
            {
                // Hysteresis: come back well inside before handing control back,
                // or a minion sitting on the boundary re-enables and re-disables
                // its movement every frame - and each re-enable costs a
                // pathfinder request, because MovementAction.OnEnable calls
                // FindPath.
                if (distance <= ReleaseAt)
                {
                    _out = false;
                    _nextToggle = Time.time + 0.35f;
                    Release();
                }
                else
                {
                    Home();
                }

                return;
            }

            if (distance > _radius && Time.time >= _nextToggle)
            {
                _out = true;
                _nextToggle = Time.time + 0.35f;
                Home();
            }
        }

        private float ReleaseAt
        {
            get { return Mathf.Max(_radius * 0.75f, _radius - 2f); }
        }

        // Suspend whatever is steering, then walk home. The ORDER matters:
        // `MovementAction.OnDisable` calls `movement.Stop()` (its `stopOnExit`
        // defaults true), and `Stop()` sets the mover's `isStopped`, whose only
        // other writer is `MoveTo`. Walking first and suspending second would
        // therefore stop the walk we just asked for, every frame, and the minion
        // would drift home on momentum alone or not at all.
        private void Home()
        {
            Suspend();

            Vector2 gap = _post - _body.position;
            float distance = gap.magnitude;

            if (distance < 0.001f)
                return;

            Vector2 dir = gap / distance;

            if (Walked())
                return;

            // No mover available - a unit sitting in a `Wait` sub-state has
            // none active, and a few prefabs have none at all. Push the body
            // ourselves, easing off over the last stretch so it settles on the
            // post instead of overshooting and pacing back and forth across it.
            float t = Mathf.Clamp01(distance / Mathf.Max(1f, _radius));

            ForgeMinionMove.Lean(_body, dir, _speed * t);
        }

        // Prefer the mover the ACTIVE action is wired to - `MovementAction.
        // movement` is a public field, so this is the game's own pairing rather
        // than a guess. A unit carries one mover per AI state (the stock drone
        // has five), so picking the wrong one writes a destination into a
        // component whose FixedUpdate is not even running.
        private bool Walked()
        {
            if (_actions != null)
            {
                for (int i = 0; i < _actions.Length; i++)
                {
                    MovementAction a = _actions[i];

                    if (a == null || a.movement == null)
                        continue;

                    // Its GameObject being active is what says "this is the
                    // current state"; we disabled the component itself a moment
                    // ago, so `isActiveAndEnabled` would now be false on
                    // exactly the one we want.
                    if (!a.gameObject.activeInHierarchy)
                        continue;

                    if (ForgeMinionMove.Walk(a.movement, _post))
                        return true;
                }
            }

            if (_movers != null)
            {
                for (int i = 0; i < _movers.Length; i++)
                {
                    if (ForgeMinionMove.Walk(_movers[i], _post))
                        return true;
                }
            }

            return false;
        }

        // Both halves live in ForgeSuspend, because `stun` needs the identical
        // thing and two copies of it would drift apart. The semantics are
        // unchanged: suspend only what is currently driving, remember it, hand
        // it all back on release.
        private void Suspend()
        {
            ForgeSuspend.Suspend(_actions, _suspended);
        }

        private void Release()
        {
            ForgeSuspend.Release(_suspended);
        }

        private void SuspendOwnerFollow()
        {
            Type owner = ForgeMinionMove.OwnerFollowType;

            if (owner == null || _actions == null)
                return;

            for (int i = 0; i < _actions.Length; i++)
            {
                MovementAction a = _actions[i];

                if (a == null || !owner.IsInstanceOfType(a))
                    continue;

                a.enabled = false;
                _ownerFollow.Add(a);
            }

            if (_ownerFollow.Count > 0)
            {
                Log.LogInfo(
                    "minion \"hold\": suspended " + _ownerFollow.Count +
                    " come-back-to-your-owner action(s) on '" + name + "' for " +
                    "as long as it is held. That is the Regroup state the five " +
                    "drone prefabs have, and it is the exact opposite of " +
                    "holding a post - left running it would drag the drone " +
                    "toward you, we would walk it back, and it would shudder " +
                    "between the two several times a second.");
            }
        }

        // A minion with its steering switched off and nothing left running to
        // switch it back on is a paralysed unit, which is a worse outcome than
        // the feature not existing. So the suspension is released here too, and
        // `_out` is cleared so a re-enable re-measures from scratch.
        //
        // `_ownerFollow` is deliberately NOT released here. It is suspended once
        // when the post is stamped and never re-suspended, so handing it back on
        // a disable would permanently un-hold a drone that came back. OnDestroy
        // covers it, and OnDestroy runs whether or not this did.
        private void OnDisable()
        {
            Release();
            _out = false;
        }

        // Both lists come back, whether this ended by the unit dying, by the
        // component being torn off, or by the error path in FixedUpdate.
        private void OnDestroy()
        {
            Release();

            for (int i = 0; i < _ownerFollow.Count; i++)
            {
                if (_ownerFollow[i] != null)
                    _ownerFollow[i].enabled = true;
            }

            _ownerFollow.Clear();
        }
    }
}
