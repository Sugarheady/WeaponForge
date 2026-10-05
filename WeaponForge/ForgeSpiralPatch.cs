using System;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace WeaponForge
{
    // Three patches, and each one is small because the mechanic lives in
    // ForgeSpiral. See that file for why `AngleOffset` is the seam and why the
    // write is scoped rather than accumulated.
    public static class ForgeSpiralPatch
    {
        private static readonly ManualLogSource Log =
            ForgeLog.Source("WeaponForge.Spiral");

        // ---- the shot itself ---------------------------------------------
        //
        // `DoShoot` is where `GetDirections` is walked, it is public, and it is
        // overridden nowhere - so this one hook covers projectile, hitscan,
        // lobbed, minion-spawner and gadget weapons alike.
        //
        // ForgeVolley also patches this method. The two do not interact: it
        // reads `barrel.Direction`, which is untouched here, and both prefixes
        // run before the original body, so there is no ordering to arrange.
        [HarmonyPatch(typeof(WeaponBase), "DoShoot")]
        public class OnDoShoot
        {
            // `__state` rather than a field, per the house rule about nesting on
            // patched methods: a sub-emitter fires through a DIFFERENT
            // WeaponBase's DoShoot from inside this one, so a shared slot could
            // be overwritten before this frame's finalizer restored it.
            static void Prefix(
                WeaponBase __instance, out ForgeSpiralPatch.Scope __state)
            {
                __state = null;

                if (!ForgeSpiral.Any || __instance == null)
                    return;

                try
                {
                    ForgeSpiral.Config cfg;

                    if (!ForgeSpiral.TryGet(__instance.TemplateData, out cfg))
                        return;

                    ForgeSpiral.State s = ForgeSpiral.Get(__instance, cfg);

                    // Remember what the weapon and its modules say, apply the
                    // spin on top, and hand the base to the finalizer.
                    __state = new Scope(__instance, __instance.AngleOffset);

                    __instance.AngleOffset = __state.baseOffset + s.spin;

                    // AFTER using it, so the first shot leaves on aim.
                    ForgeSpiral.Advance(cfg, s);
                }
                catch (Exception e)
                {
                    Log.LogError("spiral failed to aim a shot: " + e);
                    __state = null;
                }
            }

            // A FINALIZER, not a postfix: a postfix is skipped when the
            // original throws, and the one thing that must never happen here is
            // a weapon left permanently rotated off its own aim.
            static void Finalizer(ForgeSpiralPatch.Scope __state)
            {
                if (__state == null)
                    return;

                try { __state.Restore(); }
                catch (Exception e) { Log.LogError("spiral restore: " + e); }
            }
        }

        // ---- A BEAM DRAWS ITSELF FROM A SECOND `GetDirections` CALL -------
        //
        // R13 test 153 part 2: on the Caps Laser the two arms sat at their
        // spread and never rotated. `DoShoot` was hooked, `GetDirections` is
        // where AngleOffset is read, and `DoShoot` is overridden nowhere - so
        // the reasoning that one hook covers every weapon type was sound and
        // still wrong, because HITSCAN READS THE DIRECTIONS TWICE:
        //
        //     HitscanWeapon.OnBarrelMoved   walks GetDirections to place each
        //                                   beam VISUAL, every frame
        //     HitscanWeapon.FireSingle      does the damage CircleCast, from
        //                                   DoShoot
        //
        // So the spiral was rotating the damage ray while the drawn beam stayed
        // put - not "not working" but silently DESYNCED, which is worse. He saw
        // the visual, which is the honest thing to look at.
        //
        // > A patch on the method that consumes a value is not a patch on every
        // > READER of that value. `OnBarrelMoved` is the only other override in
        // > the game (checked, not assumed - it is the sole
        // > `override void OnBarrelMoved` in the assembly), and it is where a
        // > beam's shape comes from.
        //
        // ADVANCES NOTHING. This runs every frame while the trigger is held, so
        // stepping the spin here would rotate at frame rate rather than at fire
        // rate. It applies the CURRENT spin only; `DoShoot` still owns the step.
        // Frame order makes that exact rather than approximate: `Shooter.Update`
        // calls `OnBarrelMoved` before it reaches `Shoot()`, so within one frame
        // the beam is drawn along the same angle the damage cast is about to
        // use.
        [HarmonyPatch(typeof(HitscanWeapon), "OnBarrelMoved")]
        public class OnBeamDrawn
        {
            static void Prefix(
                HitscanWeapon __instance, out ForgeSpiralPatch.Scope __state)
            {
                __state = null;

                if (!ForgeSpiral.Any || __instance == null)
                    return;

                try
                {
                    ForgeSpiral.Config cfg;

                    if (!ForgeSpiral.TryGet(__instance.TemplateData, out cfg))
                        return;

                    ForgeSpiral.State s = ForgeSpiral.Get(__instance, cfg);

                    __state = new Scope(__instance, __instance.AngleOffset);

                    __instance.AngleOffset = __state.baseOffset + s.spin;
                }
                catch (Exception e)
                {
                    Log.LogError("spiral failed to aim a beam: " + e);
                    __state = null;
                }
            }

            // A finalizer for the same reason as the shot's: a weapon left
            // permanently rotated off its own aim is the one outcome that must
            // not survive an exception - and this method runs every frame, so
            // it would be a permanent one.
            static void Finalizer(ForgeSpiralPatch.Scope __state)
            {
                if (__state == null)
                    return;

                try { __state.Restore(); }
                catch (Exception e) { Log.LogError("spiral restore: " + e); }
            }
        }

        // ---- resetting on a real trigger-up ------------------------------
        //
        // `Shooter.IsShooting` is the public trigger state and a falling edge is
        // a genuine release - the same test `charge` uses, from the same place,
        // because nothing in the game reacts to a trigger coming up.
        //
        // Gated twice so a run with no spiralling weapon pays almost nothing:
        // `Any` is false unless some file asked for a spiral at all, and the
        // per-weapon lookup then has to opt in to `reset`.
        [HarmonyPatch(typeof(Shooter), "Update")]
        public class OnShooterUpdate
        {
            static void Postfix(Shooter __instance)
            {
                if (!ForgeSpiral.Any || __instance == null)
                    return;

                try
                {
                    WeaponBase weapon = __instance.Weapon;

                    if (weapon == null)
                        return;

                    ForgeSpiral.Config cfg;

                    if (!ForgeSpiral.TryGet(weapon.TemplateData, out cfg) ||
                        !cfg.resetOnPull)
                        return;

                    ForgeSpiral.Trigger(weapon, cfg, __instance.IsShooting);
                }
                catch (Exception e)
                {
                    Log.LogError("spiral trigger tracking failed: " + e);
                }
            }
        }

        // ---- not leaving dead weapons in the table -----------------------
        //
        // A weapon is a fresh `WeaponBase` whenever its module cluster is
        // rebuilt, so without this the spin table would accumulate one dead key
        // per rebuild for the whole run.
        [HarmonyPatch(typeof(WeaponBase), "Dispose")]
        public class OnDispose
        {
            static void Postfix(WeaponBase __instance)
            {
                if (!ForgeSpiral.Any)
                    return;

                try { ForgeSpiral.Forget(__instance); }
                catch (Exception e) { Log.LogError("spiral cleanup: " + e); }
            }
        }

        // The scoped write, as an object so `__state` carries both halves and
        // the restore cannot be handed the wrong weapon.
        public class Scope
        {
            public readonly WeaponBase weapon;
            public readonly float baseOffset;

            public Scope(WeaponBase weapon, float baseOffset)
            {
                this.weapon = weapon;
                this.baseOffset = baseOffset;
            }

            public void Restore()
            {
                if (weapon != null)
                    weapon.AngleOffset = baseOffset;
            }
        }
    }
}
