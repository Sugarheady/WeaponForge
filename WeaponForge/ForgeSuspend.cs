using System;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;

namespace WeaponForge
{
    // Switching a unit's OWN steering off, and putting it back.
    //
    // Extracted from ForgeMinionHold when `stun` needed the identical thing, so
    // there is one implementation rather than two that drift. The fact that
    // makes it work is worth restating here because it is not obvious:
    //
    //   `MovementAction.Update` is the ONLY thing that steers a unit -
    //   `movement.MoveTo(path.vectorPath[currentWaypoint])`, every frame while
    //   that state is active. Disable the action and nothing is steering.
    //
    // And `ChargerHead` - the beak on a Larva, Child or Maggot - does NOT
    // propel: it casts for damage and applies a recoil impulse to itself,
    // nothing more. So suspending the movement action really does stop a
    // charge, which is why this is enough to freeze a rammer.
    //
    // TWO THINGS EVERY CALLER MUST HONOUR:
    //
    //  * **Re-suspend every frame.** A state change activates a different
    //    state's action, and that one is enabled. Suspending once freezes the
    //    unit until its next transition and no longer.
    //  * **Always release.** A unit left with its steering off and nothing
    //    running to switch it back on is permanently paralysed, which is a
    //    worse outcome than the feature not existing. Release on expiry, on
    //    disable, on destroy, and on the error path.
    //
    // Re-enabling costs a pathfinder request (`MovementAction.OnEnable` calls
    // `FindPath`), so anything that toggles repeatedly wants hysteresis. A stun
    // releases once, at the end, so it does not need any.
    public static class ForgeSuspend
    {
        private static readonly ManualLogSource Log =
            ForgeLog.Source("WeaponForge.Suspend");

        // includeInactive is MANDATORY, not defensive: `StateMachine.Awake`
        // deactivates every state child, and a unit carries one mover and one
        // action set per state - the stock drone has five. Without the flag
        // this finds almost none of them and logs nothing at all.
        public static MovementAction[] ActionsOf(Unit unit)
        {
            if (unit == null)
                return null;

            try
            {
                return unit.GetComponentsInChildren<MovementAction>(true);
            }
            catch (Exception e)
            {
                Log.LogWarning("Could not read movement actions: " + e.Message);
                return null;
            }
        }

        // Switch off every action that is CURRENTLY driving, remembering each
        // one so it can be handed back. Already-suspended actions are skipped,
        // so calling this every frame cannot double-register anything.
        public static int Suspend(
            MovementAction[] actions, List<MovementAction> into)
        {
            if (actions == null || into == null)
                return 0;

            int took = 0;

            for (int i = 0; i < actions.Length; i++)
            {
                MovementAction a = actions[i];

                if (a == null || !a.enabled || !a.gameObject.activeInHierarchy)
                    continue;

                a.enabled = false;
                into.Add(a);
                took++;
            }

            return took;
        }

        public static void Release(List<MovementAction> held)
        {
            if (held == null)
                return;

            for (int i = 0; i < held.Count; i++)
            {
                if (held[i] != null)
                    held[i].enabled = true;
            }

            held.Clear();
        }
    }
}
