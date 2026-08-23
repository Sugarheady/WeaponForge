using System;
using BepInEx.Logging;
using HarmonyLib;

namespace WeaponForge
{
    // Slam-into-terrain, using a dispatch the game already runs.
    //
    // LevelSegmentComponent.OnCollisionEnter2D fires for anything with a
    // rigidbody that touches terrain, works out which CELL was hit, and hands
    // every ICellCollisionListener on the colliding body a CellCollision
    // carrying the FULL Collision2D. HealthBase implements that interface - it
    // is how CellType.contactDamage hurts you on a hazard cell - so a postfix
    // here gets the victim, the impact and the cell type for nothing.
    //
    // That also settles a question worth recording: enemies really do collide
    // with terrain (Entities collides with Ground in the layer matrix), so
    // throwing one at a wall is a physical event and not just a visual one.
    public static class ForgeSlamPatch
    {
        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("WeaponForge.Slam");

        [HarmonyPatch(typeof(HealthBase), "OnCellCollision")]
        public class OnCellCollision
        {
            static void Postfix(HealthBase __instance, CellCollision cellCollision)
            {
                try
                {
                    // Cheap gate first: almost nothing is ever slammable, and
                    // this runs for every unit-terrain contact in the level.
                    var slam =
                        __instance.GetComponentInParent<ForgeSlam>() ??
                        __instance.GetComponentInChildren<ForgeSlam>();

                    if (slam == null || !slam.Live)
                        return;

                    slam.Resolve(cellCollision.collision);
                }
                catch (Exception e)
                {
                    Log.LogError("Slam-into-terrain failed: " + e);
                }
            }
        }
    }
}
