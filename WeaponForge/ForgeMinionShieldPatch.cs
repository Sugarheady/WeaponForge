using System;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace WeaponForge
{
    // Repaints a minion's shield bubble.
    //
    // WHY A PER-FRAME PATCH RATHER THAN A ONE-TIME WRITE, because it looks
    // like overkill until you read Shield.Update:
    //
    //     _material.SetColor("_Color", (Time.time - lastDamageTime <
    //         damgeHighlightDuration) ? damageHighlightColor
    //                                 : resource.shieldColor);
    //
    // The colour is re-read from the shared Resource asset EVERY FRAME. A
    // single write after Setup would survive exactly one frame.
    //
    // AND THE OBVIOUS SHORTCUT IS A TRAP: `resource.shieldColor` is a field on
    // a ScriptableObject, so writing it would repaint every shield of that
    // resource in the game at once - including the player's, and permanently,
    // because ScriptableObject edits persist for the session. Same rule as
    // ProjectileBounceData and PhysMatGrenade: never mutate a shared asset.
    //
    // So we let the game write whatever it likes and publish over the top,
    // which is the same "publish, don't overwrite" shape as ForgeUnitTint.
    public static class ForgeMinionShieldPatch
    {
        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("WeaponForge.MinionShield");

        [HarmonyPatch(typeof(Shield), "Update")]
        public class ShieldUpdate
        {
            static void Postfix(Shield __instance)
            {
                try
                {
                    if (__instance == null)
                        return;

                    // Almost every shield in the level is somebody else's -
                    // the player's, an enemy's - so this walk is the common
                    // path and has to be the cheap one. It is a parent walk of
                    // two or three links and stops at the first miss.
                    var tint =
                        __instance.GetComponentInParent<ForgeMinionShield>();

                    if (tint == null)
                        return;

                    // A unit can carry several shields. Only paint the one we
                    // were asked about.
                    if (tint.resource != null &&
                        __instance.Resource != tint.resource)
                        return;

                    var paint = __instance.GetComponent<ForgeShieldPaint>();

                    if (paint == null)
                    {
                        paint = __instance.gameObject
                            .AddComponent<ForgeShieldPaint>();
                    }

                    Material m = paint.Resolve(__instance);

                    if (m != null)
                        m.SetColor("_Color", tint.color);
                }
                catch (Exception e)
                {
                    // A shield that cannot be repainted must still be a
                    // shield. This runs every frame, so it says so once and
                    // then stays quiet rather than filling the log.
                    if (!_said)
                    {
                        _said = true;
                        Log.LogError("Minion shield repaint failed: " + e);
                    }
                }
            }

            private static bool _said;
        }
    }
}
