using System;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace WeaponForge
{
    // A WEAPON FIRED FROM A GADGET KEY AIMS DUE EAST. Always has.
    //
    // R10 test 49 passed with a caveat: *"it does work, but only attacks in one
    // direction to the east (right). I think it needs to be hooked onto the
    // aimer of the player somehow."* That reading was exactly right, and the
    // cause is one literal in the game's own code:
    //
    //     // WeaponBasedActiveModule.Activate
    //     this.weapon.Fire(new FakeBarrel(owner.transform.position, Vector2.right));
    //
    // A gadget does not go through `Shooter`, and `Shooter` is the thing that
    // normally hands a weapon a real barrel. So the gadget path fabricates one -
    // and fabricates it pointing along +X, which in a 2D game pointing that way
    // renders as due east.
    //
    // "DUE EAST" IS THIS CODEBASE'S FINGERPRINT FOR A MISSING DIRECTION, not a
    // wrong one. `transform.right` is the fallback wherever a heading is not
    // available: the split feature had the identical bug (a shot that died at
    // zero velocity had no heading, fell through to `transform.right`, and
    // fanned every fragment east). Whenever something in this game insists on
    // going right, look for the place a direction was never supplied.
    //
    // WHY THE FIX SITS ON `WeaponBase.Fire` RATHER THAN ON `Activate`.
    // `Activate` builds the barrel inline and the weapon it fires is a PRIVATE
    // field, so a prefix would have to reimplement the whole method through
    // reflection just to change one vector. But `FakeBarrel` is a **class**
    // with a public settable `Direction` - so the barrel can simply be
    // corrected on its way into Fire.
    //
    // The gate is a frame-scoped static set around `Activate`, which is the
    // same idiom the kill-attribution uses and is exact for the same reason:
    // Activate calls Fire synchronously, so the flag is only ever set for the
    // one call it means. That precision matters, because **three other places
    // build a FakeBarrel** - `Projectile`, `PhysicsProjectile` (both for
    // sub-emitters) and `WeaponBasedConsumable` - and in those the direction is
    // the shot's own and must not be touched.
    public static class ForgeGadgetAim
    {
        private static readonly ManualLogSource Log =
            ForgeLog.Source("WeaponForge");

        // The unit whose gadget is mid-activation, or null. Set and cleared
        // around one synchronous call.
        private static Unit _activating;

        private static bool _said;

        [HarmonyPatch(typeof(WeaponBasedActiveModule), "Activate")]
        public class OnActivate
        {
            static void Prefix(Unit owner)
            {
                _activating = owner;
            }

            // A finalizer rather than a postfix would be tidier, but a postfix
            // runs after a normal return and Activate cannot throw past the
            // resource check. Cleared unconditionally either way.
            static void Postfix()
            {
                _activating = null;
            }
        }

        [HarmonyPatch(typeof(WeaponBase), "Fire")]
        public class OnFire
        {
            static void Prefix(IBarrel barrel)
            {
                if (_activating == null)
                    return;

                var fake = barrel as FakeBarrel;

                if (fake == null)
                    return;

                try
                {
                    Vector2 dir = Aim(_activating, fake.Position);

                    if (dir.sqrMagnitude <= 0.000001f)
                        return;

                    fake.Direction = dir;

                    if (!_said)
                    {
                        _said = true;

                        Log.LogInfo(
                            "gadget aim: a weapon fired from a gadget key now " +
                            "points at your cursor. The game hands one a " +
                            "fabricated barrel aimed at Vector2.right (due " +
                            "east) because a gadget bypasses Shooter, which is " +
                            "what normally supplies the aim. Logged once.");
                    }
                }
                catch (Exception e)
                {
                    // Never break a gadget over a cosmetic direction.
                    Log.LogError("Gadget aim failed, leaving it east: " + e);
                }
            }
        }

        // The crosshair. `Aimer.TargetPosition` is the RAW mouse position - the
        // aim assist lives further along and would pull a spawner towards
        // whatever it thinks you meant, which is not what "spawn where I am
        // pointing" should do.
        private static Vector2 Aim(Unit owner, Vector2 from)
        {
            if (owner == null)
                return Vector2.zero;

            var aimer = owner.GetComponentInChildren<Aimer>(true);

            if (aimer == null)
                return Vector2.zero;

            Vector2 to = aimer.TargetPosition;
            Vector2 d = to - from;

            return (d.sqrMagnitude > 0.000001f) ? d.normalized : Vector2.zero;
        }
    }
}
