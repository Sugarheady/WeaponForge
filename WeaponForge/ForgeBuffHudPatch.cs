using System;
using BepInEx.Logging;
using HarmonyLib;

namespace WeaponForge
{
    // Catches the live ship HUD so ForgeBuffHud has something to draw into.
    //
    // ShipHud.AssignShip is the right hook and not just a convenient one: it is
    // where the game itself wires up every part of the HUD that needs the ship -
    // the resource bars, the ability panel, the minion widget, and
    // ShipLogDisplay.Assign, which is what subscribes to the log events. So by
    // the time this postfix runs, the log has a subscriber and the feed is safe
    // to write to. (ShipLogOutput.Clear invokes LogRemoved with no null check,
    // so "has a subscriber" is not a detail.)
    public static class ForgeBuffHudPatch
    {
        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("WeaponForge.BuffHud");

        [HarmonyPatch(typeof(ShipHud), "AssignShip")]
        public class OnAssignShip
        {
            static void Postfix(ShipHud __instance, Ship ship)
            {
                try
                {
                    if (ship != null)
                        ForgeBuffHud.Bind(__instance, ship);
                }
                catch (Exception e)
                {
                    Log.LogError("Binding the buff HUD failed: " + e);
                }
            }
        }

        // NOTE: there is deliberately no ShipHud.OnDestroy hook. **ShipHud does
        // not declare one**, and Harmony throws at PatchAll when asked to patch
        // a method that does not exist - which would take the whole mod down, not
        // just this feature. (Exactly the trap ResourcePickup.Awake set once.)
        //
        // It is not needed anyway: Unity's == operator reports a destroyed object
        // as null, so ForgeBuffHud.FeedReady stops being true on its own the
        // moment the HUD goes away, and the next AssignShip rebinds and clears
        // the stale entries.
    }
}
