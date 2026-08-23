using System;
using System.Collections.Generic;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace WeaponForge
{
    // Drives orbit weapons off the ship's Shooter:
    //   Update  -> ensure a ForgeOrbitController exists for the equipped
    //              orbit weapon and set whether it's active (by mode).
    //   Shoot   -> handle mode-specific fire behaviour (toggle on/off,
    //              (re)summon a timed ring, fling), and suppress the
    //              weapon's normal forward fire so it's a pure orbital.
    public static class ForgeOrbitPatch
    {
        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("WeaponForge");

        private static readonly Dictionary<Shooter, ForgeOrbitController> _ctrls =
            new Dictionary<Shooter, ForgeOrbitController>();

        private static ForgeOrbitController GetOrCreate(
            Shooter shooter, ForgeOrbit.Config cfg)
        {
            ForgeOrbitController ctrl;
            if (_ctrls.TryGetValue(shooter, out ctrl) && ctrl != null)
                return ctrl;

            var go = new GameObject("Forge Orbit");
            if (shooter.Unit != null)
                go.transform.SetParent(shooter.Unit.transform, false);

            ctrl = go.AddComponent<ForgeOrbitController>();
            // Primary/secondary: driven each frame by OnUpdate below, so the
            // weapon is read live off the Shooter (it can be recreated).
            ctrl.Init(cfg, shooter.Unit, () => shooter.Weapon);
            _ctrls[shooter] = ctrl;
            return ctrl;
        }

        private static void Remove(Shooter shooter)
        {
            ForgeOrbitController ctrl;
            if (_ctrls.TryGetValue(shooter, out ctrl))
            {
                if (ctrl != null)
                    UnityEngine.Object.Destroy(ctrl.gameObject);
                _ctrls.Remove(shooter);
            }
        }

        [HarmonyPatch(typeof(Shooter), "Update")]
        public class OnUpdate
        {
            static void Postfix(Shooter __instance)
            {
                try
                {
                    WeaponBase weapon = __instance.Weapon;
                    ForgeOrbit.Config cfg;

                    if (weapon == null ||
                        !ForgeOrbit.TryGet(weapon.TemplateData, out cfg))
                    {
                        Remove(__instance);
                        return;
                    }

                    ForgeOrbitController ctrl = GetOrCreate(__instance, cfg);

                    bool active;
                    switch (cfg.mode)
                    {
                        case ForgeOrbit.Mode.Hold:
                            active = __instance.IsShooting;
                            break;
                        case ForgeOrbit.Mode.Toggle:
                            active = ctrl.toggledOn;
                            break;
                        case ForgeOrbit.Mode.Fire:
                            active = Time.time < ctrl.fireActiveUntil;
                            break;
                        default: // Passive
                            active = true;
                            break;
                    }

                    // Upkeep, BEFORE SetActive, so a ring that cannot pay goes
                    // down in the same frame and gets its release fade rather
                    // than being switched off a frame later.
                    if (active && Upkeep(cfg))
                    {
                        if (Time.time < ctrl.dryUntil)
                        {
                            active = false;
                        }
                        else
                        {
                            // cost x fireRate is exactly what a normal weapon
                            // spends per second with the trigger held, so a
                            // ring priced this way costs what the weapon says
                            // it costs - and it keeps tracking if a module
                            // changes either number.
                            float perSecond = cfg.holdDrainFromCost
                                ? weapon.Cost * Mathf.Max(0f, weapon.FireRate)
                                : cfg.holdDrainPerSecond;

                            if (!Pay(__instance, weapon,
                                     perSecond * Time.deltaTime))
                            {
                                active = false;

                                // Half a second, not forever: the tank may be
                                // regenerating, and a ring needing a fresh
                                // trigger pull after every dip would be
                                // miserable on a weapon you hold down. It also
                                // stops the empty cue firing sixty times a
                                // second while the trigger is still held.
                                ctrl.dryUntil = Time.time + 0.5f;
                                ctrl.toggledOn = false;
                                ctrl.fireActiveUntil = 0f;

                                Insufficient(__instance, weapon);
                            }
                        }
                    }

                    ctrl.SetActive(active);
                }
                catch (Exception e)
                {
                    Log.LogError("Orbit update failed: " + e);
                }
            }
        }

        [HarmonyPatch(typeof(Shooter), "Shoot")]
        public class OnShoot
        {
            static bool Prefix(Shooter __instance)
            {
                try
                {
                    WeaponBase weapon = __instance.Weapon;
                    ForgeOrbit.Config cfg;

                    if (weapon == null ||
                        !ForgeOrbit.TryGet(weapon.TemplateData, out cfg))
                    {
                        return true; // normal weapon - fire as usual
                    }

                    ForgeOrbitController ctrl = GetOrCreate(__instance, cfg);

                    if (cfg.mode == ForgeOrbit.Mode.Toggle)
                        ctrl.toggledOn = !ctrl.toggledOn;
                    else if (cfg.mode == ForgeOrbit.Mode.Fire)
                        ctrl.fireActiveUntil = Time.time + cfg.fireDuration;

                    ctrl.TriggerFling();
                    ctrl.RegenOnFire();

                    // We're skipping the game's Shoot (to suppress forward
                    // fire), so charge the cost ourselves for active modes.
                    if (cfg.suppressFire &&
                        (cfg.mode == ForgeOrbit.Mode.Toggle ||
                         cfg.mode == ForgeOrbit.Mode.Fire))
                    {
                        Drain(__instance, weapon, weapon.Cost);
                    }

                    return !cfg.suppressFire;
                }
                catch (Exception e)
                {
                    Log.LogError("Orbit shoot failed: " + e);
                    return true;
                }
            }
        }

        private static void Drain(Shooter shooter, WeaponBase weapon, float amount)
        {
            try
            {
                if (amount <= 0f || shooter.Unit == null || weapon.ResourceUsed == null)
                    return;
                if (shooter.Unit.HasTank(weapon.ResourceUsed))
                    shooter.Unit.GetTank(weapon.ResourceUsed).Value -= amount;
            }
            catch { }
        }

        // Which modes have an ongoing price. Fire mode is bounded by its own
        // duration, so it pays once through the normal Shoot path instead.
        private static bool Upkeep(ForgeOrbit.Config cfg)
        {
            if (!cfg.holdDrainFromCost && cfg.holdDrainPerSecond <= 0f)
                return false;

            return cfg.mode == ForgeOrbit.Mode.Hold ||
                   cfg.mode == ForgeOrbit.Mode.Toggle;
        }

        // Spend `amount`, or report that it could not be spent.
        //
        // Clamping at zero is not a nicety: ResourceTank.Value has no floor of
        // its own - the setter assigns whatever it is given - so the old drain
        // pushed the tank NEGATIVE and the ring carried on running for free.
        // A cost that cannot stop anything is not a cost.
        private static bool Pay(
            Shooter shooter, WeaponBase weapon, float amount)
        {
            try
            {
                if (amount <= 0f)
                    return true;

                if (shooter.Unit == null || weapon.ResourceUsed == null)
                    return true;

                if (shooter.Unit.HasInfiniteResource)
                    return true;

                if (!shooter.Unit.HasTank(weapon.ResourceUsed))
                    return true;   // nothing to charge against - stay free

                ResourceTank tank = shooter.Unit.GetTank(weapon.ResourceUsed);

                if (tank.Value < amount)
                {
                    tank.Value = 0f;
                    return false;
                }

                tank.Value -= amount;
                return true;
            }
            catch
            {
                // Never let an accounting slip switch someone's weapon off.
                return true;
            }
        }

        // The game's own "you cannot afford that" cue, so an orbit weapon
        // running dry feels like every other weapon running dry.
        private static void Insufficient(Shooter shooter, WeaponBase weapon)
        {
            try
            {
                if (shooter.Unit != null && weapon.ResourceUsed != null)
                    shooter.Unit.TriggerResourceInsufficient(
                        weapon.ResourceUsed, false);
            }
            catch { }
        }
    }
}
