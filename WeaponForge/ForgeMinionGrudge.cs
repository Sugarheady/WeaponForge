using System;
using BepInEx.Logging;
using UnityEngine;

namespace WeaponForge
{
    // A MINION OF YOURS MUST NOT HOLD A GRUDGE AGAINST YOU.
    //
    // R11 test 8, reported as *"Minions do not follow you, and now attack
    // you"* - and the two halves of that sentence may well be one behaviour
    // seen twice. A minion that has blacklisted you does not wander off; it
    // COMES STRAIGHT AT YOU, which reads as "not following" and as "attacking"
    // at the same time.
    //
    // HOW YOU END UP ON YOUR OWN MINION'S ENEMY LIST, proven from source rather
    // than guessed:
    //
    //   1. `ExplosionComponent.Start` builds its `Explosion` struct and never
    //      copies its own `Owner` property into it - so all thirteen prefabs
    //      that use it explode OWNERLESS, including the six `Explosion Ammo *`
    //      ones behind your own rockets and shards.
    //   2. `ExplosionManager` applies that blast to every non-trigger collider
    //      in radius with no owner test of any kind. Your minions qualify.
    //   3. The damage fires the minion's `onGotAttacked`, `AggroWhenHit` calls
    //      `AIAgent.HandleHitBy(you)`, and that adds you to its
    //      `enemyBlackList`.
    //   4. That list is checked BEFORE faction and is never cleaned up. You are
    //      its enemy for the rest of its life.
    //
    // So one stray rocket permanently turns your own swarm against you, and
    // nothing in any log says so. `ForgeMinionBlast` now fixes (1) for minion
    // deaths; this fixes (3) and (4) for every route at once, which is why it
    // is worth having as well as the explosion fix - contact damage, a beam
    // clipping a minion, a gas cloud, or anything added later all land here.
    //
    // WHY IT IS A PER-FRAME CHECK AND NOT A ONE-OFF. The grudge is added by the
    // hit, not at spawn, so there is no single moment to clean up. It is one
    // HashSet lookup on a set that is almost always empty.
    //
    // WHAT IT DELIBERATELY DOES NOT DO: restore anything. A charm is temporary
    // and must put the grudge back ([[ForgeCharmed]] records what it took); a
    // minion belongs to you for its whole life, so there is nothing to hand
    // back and no memento to keep honest.
    public class ForgeMinionGrudge : MonoBehaviour
    {
        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("WeaponForge.Minion");

        private Unit _self;
        private Unit _owner;
        private AIAgent _agent;
        private bool _haveOwnerId;
        private int _ownerId;

        private static bool _said;

        public static void Attach(Unit minion, Unit owner)
        {
            if (minion == null || owner == null)
                return;

            try
            {
                var g = minion.gameObject.GetComponent<ForgeMinionGrudge>();

                if (g == null)
                    g = minion.gameObject.AddComponent<ForgeMinionGrudge>();

                g._self = minion;
                g._owner = owner;
                g._agent = ForgeGrudge.AgentOf(minion);
                g._haveOwnerId = ForgeGrudge.TryId(owner, out g._ownerId);
            }
            catch (Exception e)
            {
                Log.LogError("Attaching a minion grudge guard failed: " + e);
            }
        }

        private void Update()
        {
            if (_self == null || _owner == null || _agent == null)
                return;

            try
            {
                // The id can be unavailable on the frame a minion spawns, so it
                // is retried rather than resolved once and given up on.
                if (!_haveOwnerId)
                    _haveOwnerId = ForgeGrudge.TryId(_owner, out _ownerId);

                if (!_haveOwnerId)
                    return;

                if (ForgeGrudge.Drop(_agent, _ownerId) && !_said)
                {
                    _said = true;

                    Log.LogInfo(
                        "minion GRUDGE: '" + _self.name + "' had put YOU on " +
                        "its personal enemy list - something of yours damaged " +
                        "it, and the game's AI reads that list before it reads " +
                        "faction, so it had started hunting you and would have " +
                        "done for the rest of its life. Withdrawn. Explosions " +
                        "in this game carry no owner filter, so your own blast " +
                        "radius is the usual way this happens. Logged once per " +
                        "run.");
                }

                ForgeGrudge.ForgetFriendlyTarget(_agent, _self);

                WatchHealth();
            }
            catch (Exception e)
            {
                Log.LogError("A minion grudge check failed: " + e);
                enabled = false;
            }
        }

        // DID IT ACTUALLY GET HURT? - the question the grudge line above cannot
        // answer, and the one he asked.
        //
        // R13 test 148: *"This one is hard to tell. It looks like the explosion
        // knocks them back but im not sure if they're getting hurt. they keep
        // on fighting enemies and ignoring me."* Both halves of the pass
        // condition were satisfied and neither was VISIBLE: the grudge line
        // only prints when a grudge was found and withdrawn, so a run where
        // nothing ever hurt the minion looks identical to a run where the guard
        // is broken - and PUNK draws no damage numbers, so "did that blast do
        // anything" has no answer on screen either.
        //
        // So this reports the health tank going DOWN, which is the fact both
        // questions hang off. Raw numbers, no verdict about the cause: a
        // minion's health IS a resource tank and stats recalculation rebuilds
        // it, so the honest claim is "it dropped by this much", not "you hit
        // it for this much". (A recalculation refills, so it moves the other
        // way and cannot be mistaken for damage.)
        //
        // No new Harmony patch. `DamagableResource.Damage` is one of the
        // hottest methods in the game and this needs nothing from inside it -
        // a per-frame read of one tank on a handful of minions is cheaper and
        // cannot get the nesting wrong.
        private void WatchHealth()
        {
            if (_saidHurt)
                return;

            float now;

            if (!TryHealth(out now))
                return;

            if (_lastHealth < 0f)
            {
                _lastHealth = now;
                return;
            }

            if (now >= _lastHealth - 0.001f)
            {
                _lastHealth = now;
                return;
            }

            float drop = _lastHealth - now;
            _lastHealth = now;
            _saidHurt = true;

            bool grudge = false;

            try
            {
                grudge = _haveOwnerId &&
                         ForgeGrudge.Holds(_agent, _ownerId);
            }
            catch (Exception) { }

            Log.LogInfo(
                "minion HURT: '" + _self.name + "' lost " +
                drop.ToString("0.##") + " health (now " +
                now.ToString("0.##") + "). So YES, things really are damaging " +
                "your own minions - the game draws no damage numbers, which is " +
                "why this is hard to see. At this moment it " +
                (grudge
                    ? "IS holding a grudge against you, which the guard will " +
                      "withdraw on the next frame - if you see it come at you, " +
                      "quote this line."
                    : "is NOT holding a grudge against you, so the guard is " +
                      "doing its job and it will keep fighting enemies.") +
                " Note \"hurtsAllies\" and the blast-safety filter only cover " +
                "the MINION'S own explosions; your own guns and blasts are a " +
                "separate question and are not filtered. Logged once per run.");
        }

        private bool TryHealth(out float value)
        {
            value = 0f;

            try
            {
                var hb = _self.GetComponent<HealthBase>();

                if (hb == null)
                    hb = _self.GetComponentInChildren<HealthBase>();

                if (hb == null)
                    return false;

                value = hb.CurrentHealth;
                return true;
            }
            catch (Exception)
            {
                // A diagnostic must be the most defensive code in the file.
                return false;
            }
        }

        private float _lastHealth = -1f;
        private static bool _saidHurt;

        public static void ResetDiagnostics()
        {
            _said = false;
            _saidHurt = false;
        }
    }
}
