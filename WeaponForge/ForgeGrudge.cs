using System;
using BepInEx.Logging;
using UnityEngine;

namespace WeaponForge
{
    // THE GAME'S PERSONAL GRUDGE LIST, and the primitives for reading it.
    //
    // Extracted 2026-09-02 because a SECOND feature needed it and duplicating
    // the charm version is the exact mistake recorded in this project's notes:
    // copy a working feature and you copy its bugs. One owner for the awkward
    // parts, two callers.
    //
    // WHY THIS EXISTS AT ALL. `AIAgent.Data.enemyBlackList` is a HashSet of
    // entity instance ids, and `RefreshEnemyAndFriendLists` tests it BEFORE any
    // question about faction:
    //
    //     if (enemyBlackList.Contains(id))   -> ENEMY      <-- wins outright
    //     else if (friendWhiteList...)       -> friend
    //     else if (IsFriendsWith(unit))      -> friend
    //     else if (IsEnemiesWith(unit))      -> enemy
    //
    // And `AIAgent.HandleHitBy(other)` - called by `AggroWhenHit` from the
    // victim's own `onGotAttacked` - adds the attacker and NEVER removes it.
    //
    // So **anything that damages a unit becomes its permanent enemy, whatever
    // side it is on.** That broke charm (R12 test 134) and it breaks friendly
    // minions, and the two arrive at it differently:
    //
    //   charm    the act of charming IS a hit, so the player is blacklisted on
    //            the very frame the charm starts
    //   minions  a stray blast of your own does it later, and explosions in
    //            this game carry no owner filter at all - so your rocket
    //            clipping your own Larva makes it hunt you for the rest of its
    //            life
    //
    // Everything needed is public: `SavableComponent<T>.ComponentData` has a
    // public getter, `AIAgent.Data.enemyBlackList` is a public field, and
    // `ForgetTarget` / `Target` / `HasTarget` / `SeesUnit` are public methods.
    // No reflection anywhere in this file.
    //
    // ONE WARNING THAT SHAPES EVERY CALLER: the list is SAVE-PERSISTED through
    // `AIAgent.Data.Memento`. Removing an entry outlives the run, so a feature
    // that is temporary (charm) must record what it took and put it back, and
    // must never put back an id the unit did not hold. A feature that is
    // permanent for the unit's whole life (a minion) has nothing to restore.
    public static class ForgeGrudge
    {
        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("WeaponForge.Grudge");

        // The entity instance id the game's own lists are keyed by.
        //
        // Both hops are nullable on a unit that is not bound to save data yet,
        // and this gets called on the frame a hit lands - so neither is
        // assumed.
        public static bool TryId(Unit unit, out int id)
        {
            id = 0;

            if (unit == null)
                return false;

            try
            {
                var data = unit.ComponentData;

                if (data == null || data.entity == null)
                    return false;

                id = data.entity.instanceId;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static AIAgent AgentOf(Unit unit)
        {
            return (unit != null)
                ? unit.GetComponentInChildren<AIAgent>()
                : null;
        }

        public static AIAgent.Data DataOf(AIAgent agent)
        {
            try
            {
                return (agent != null) ? agent.ComponentData : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        // Take one id off a unit's grudge list. Returns true if it was really
        // there - which is the only thing a caller may record, because
        // restoring an id the unit never held would INVENT a permanent grudge.
        public static bool Drop(AIAgent agent, int id)
        {
            AIAgent.Data data = DataOf(agent);

            if (data == null || data.enemyBlackList == null)
                return false;

            return data.enemyBlackList.Remove(id);
        }

        public static bool Add(AIAgent agent, int id)
        {
            AIAgent.Data data = DataOf(agent);

            if (data == null || data.enemyBlackList == null)
                return false;

            return data.enemyBlackList.Add(id);
        }

        public static bool Holds(AIAgent agent, int id)
        {
            AIAgent.Data data = DataOf(agent);

            return data != null && data.enemyBlackList != null &&
                   data.enemyBlackList.Contains(id);
        }

        // Would the agent's own sorting call `target` an enemy?
        //
        // Asked in the game's own order - grudge first, then faction - so a
        // deliberate grudge (charm's `infighting`) still counts as a legitimate
        // target while an accidental one does not survive the caller's Drop.
        public static bool CountsAsEnemy(AIAgent agent, Unit self, Unit target)
        {
            if (target == null)
                return false;

            int id;

            if (TryId(target, out id) && Holds(agent, id))
                return true;

            try
            {
                return self != null && self.IsEnemiesWith(target);
            }
            catch (Exception)
            {
                return false;
            }
        }

        // If the unit has settled on something its own AI would call a FRIEND,
        // make it pick again.
        //
        // Needed because dropping a grudge is not enough on its own:
        // `AIAgent.Update` returns early while `HasTarget && IsTargetVisible`,
        // so a stale target survives until it leaves the unit's sight. Without
        // this the fix appears to work only when you break line of sight.
        //
        // Returns true if a target was dropped, so a caller can log it once.
        public static bool ForgetFriendlyTarget(AIAgent agent, Unit self)
        {
            if (agent == null)
                return false;

            try
            {
                if (!agent.HasTarget)
                    return false;

                Unit target = agent.Target;

                if (target == null)
                    return false;

                // Only what it can currently see: anything else is re-picked by
                // the agent's own code on the next frame anyway.
                if (!agent.SeesUnit(target))
                    return false;

                if (CountsAsEnemy(agent, self, target))
                    return false;

                agent.ForgetTarget();
                return true;
            }
            catch (Exception e)
            {
                Log.LogError("Clearing a friendly target failed: " + e);
                return false;
            }
        }
    }
}
