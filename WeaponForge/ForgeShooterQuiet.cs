using System;
using BepInEx.Logging;
using UnityEngine;

namespace WeaponForge
{
    // EVERYTHING THAT HAS TO HAPPEN WHEN WE BLOCK A SHOOTER, in one place.
    //
    // `Shooter.Block(object)` is public and `Shooter.Update` returns the moment
    // `blockers.Count > 0`, which is what makes a jam one call. The cost is
    // everything that sits BELOW that return and now never runs - and this mod
    // has paid it three times, once per thing it stopped doing:
    //
    //   1. `StopContinousSound` - a stunned gun kept roaring (fixed in R11).
    //   2. `weapon.IsTriggerPulled = false` - the held-trigger flag froze true.
    //   3. **`weapon.OnBarrelMoved(...)` - and this is the one that was still
    //      live.** R15 test 188: *"after overheating the beam stays in place
    //      firing from the original place that it died out at. I move away and
    //      the beam is still firing from that spot."*
    //
    // Exactly right, and (2) is why (3) was missed. `HitscanWeaponVisual.Firing`
    // is only ever written from `HitscanWeapon.OnBarrelMoved`, in the `else`
    // branch of `if (IsTriggerPulled)`. Clearing the flag is necessary and does
    // nothing on its own: the branch that READS it is upstream of the guard, so
    // nobody ever looks at the value we set. The visual keeps its sprite, its
    // Light2D and its impact particle, at the position and rotation of the last
    // frame the gun was allowed to fire.
    //
    // > **Clearing a flag is not the same as applying it.** A flag is applied by
    // > whatever reads it, and if that reader is above the return you just added
    // > yourself to, nothing has happened at all. Twin of "read every `return`
    // > above the line you are appending" - this is the version where the line
    // > you wrote is correct and still does nothing.
    //
    // One honest note on the rest of his report: the beam is **not** still doing
    // damage. The damage cast lives in `HitscanWeapon.FireSingle`, which needs
    // `Shooter.Shoot()`, which returns on the same blocker list. What keeps
    // going is the beam sprite, the light and the impact-spark particle playing
    // on whatever the beam was last pointed at - which is indistinguishable from
    // damage while you are looking at it.
    public static class ForgeShooterQuiet
    {
        private static readonly ManualLogSource Log =
            ForgeLog.Source("WeaponForge");

        private static bool _warned;

        // Call immediately after `Shooter.Block(...)`. Safe to call when the
        // shooter has no weapon, and safe to call twice.
        public static void Silence(Shooter shooter)
        {
            if (shooter == null)
                return;

            try
            {
                shooter.StopContinousSound();
                shooter.StopWarmupSound();
            }
            catch (Exception e)
            {
                Warn("silencing a blocked weapon", e);
            }

            WeaponBase weapon = shooter.Weapon;

            if (weapon == null)
                return;

            try
            {
                weapon.IsTriggerPulled = false;

                // The game's own minigun gate, zeroed the way releasing the
                // trigger would zero it - otherwise a blocked warmed-up gun
                // comes back already spun up.
                weapon.CoolDown();
            }
            catch (Exception e)
            {
                Warn("releasing a blocked weapon's trigger", e);
            }

            try
            {
                IBarrel barrel = shooter.Barrel;

                if (barrel == null)
                    return;

                // THE LINE THE WHOLE FILE EXISTS FOR. One more call, with the
                // trigger flag already false, so the weapon pushes "not firing"
                // into its visuals before the blocker locks Update out.
                //
                // No more dangerous than the game's own call: Shooter.Update
                // does this every frame with the same two arguments. The one
                // thing it can throw on is `visualsInstances[num]`, which
                // HitscanWeapon indexes with no bounds check - so anything that
                // changed ProjectileCount without calling InitializeVisuals
                // would already be throwing every frame before we got here.
                weapon.OnBarrelMoved(barrel.Position, barrel.Direction);
            }
            catch (Exception e)
            {
                Warn("clearing a blocked beam's visuals", e);
            }
        }

        private static void Warn(string what, Exception e)
        {
            if (_warned)
                return;

            _warned = true;

            Log.LogError(
                "A weapon was blocked (by overheat, a jam or a stun) and " +
                what + " failed: " + e + " - the gun is blocked correctly, " +
                "but a BEAM may keep drawing where it stopped. Quote this line.");
        }
    }
}
