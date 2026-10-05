using System;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace WeaponForge
{
    // Starts a swing on every trigger pull.
    //
    // ONE HOOK, BOTH SLOT KINDS, and that is worth spelling out because orbit
    // needed a whole second patch for the gadget path. `WeaponBase.DoShoot` is
    // on the path of every shot from either direction:
    //
    //   weapon slot   Shooter.Update -> Shoot -> weapon.Fire -> burst -> DoShoot
    //   gadget key    WeaponBasedActiveModule.Activate ->
    //                 weapon.Fire(new FakeBarrel(...)) -> burst -> DoShoot
    //
    // and `WeaponBase.Equip` is called from `Shooter.SetWeapon` AND
    // `WeaponBasedActiveModule.Activate`, so `Owner` is set either way. So a
    // swipe works on a gadget key with no extra code, and the gadget's own
    // cooldown and cost handling apply to it for free.
    //
    // DoShoot ALSO hands us the barrel, which is what the swing needs: it
    // carries the direction the shot is going, which is the aim-assisted
    // heading rather than the raw crosshair. That is the right one for a blade
    // - a swing should land where the gun would have shot.
    //
    // A POSTFIX, and it does not need the FireRatePassed trick the overheat fix
    // needed: DoShoot is inside the original Shoot, so if anything suppressed
    // the shot this never runs at all.
    //
    // Note a burst of 3 therefore swings three times. That is a flurry, and it
    // is the honest reading of burstSize on a melee weapon rather than
    // something to guard against.
    public static class ForgeSwipePatch
    {
        private static readonly ManualLogSource Log =
            ForgeLog.Source("WeaponForge.Swipe");

        [HarmonyPatch(typeof(WeaponBase), "DoShoot")]
        public class OnDoShoot
        {
            static void Postfix(WeaponBase __instance, IBarrel barrel)
            {
                if (!ForgeSwipe.Any || __instance == null)
                    return;

                try
                {
                    if (__instance.TemplateData == null)
                        return;

                    ForgeSwipe.Config cfg;

                    if (!ForgeSwipe.TryGet(__instance.TemplateData, out cfg))
                        return;

                    Unit owner = __instance.Owner;

                    if (owner == null)
                        return;

                    Vector2 aim = (barrel != null)
                        ? barrel.Direction
                        : (Vector2)owner.transform.right;

                    ForgeSwipeController.Swing(owner, cfg, __instance, aim);
                }
                catch (Exception e)
                {
                    Log.LogError("Swinging failed: " + e);
                }
            }
        }
    }
}
