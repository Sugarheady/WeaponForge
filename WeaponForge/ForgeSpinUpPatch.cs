using System;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace WeaponForge
{
    // Attaches the wind-up meter to the Shooter that is firing. Everything else
    // - the clocks, the rewards, the feedback - lives on `ForgeSpin`.
    //
    // A POSTFIX ON `Shooter.Shoot` AND DELIBERATELY WITHOUT THE
    // `FireRatePassed` DISCRIMINATOR THAT OVERHEAT NEEDS. Overheat charges heat
    // here, so it has to know whether the shot really happened - a prefix that
    // returned false (the charge block's hold-fire, the swarm cap) leaves the
    // postfix running for a shot that never left the barrel, and overheat once
    // charged 64 heat a second while firing nothing.
    //
    // This postfix does not charge anything. Its only job is to make sure the
    // meter EXISTS and knows its config; the meter then decides for itself,
    // every frame, whether the trigger is down, whether the owner can pay, and
    // whether the gun is blocked. So a suppressed shot attaching the component
    // is harmless, and it is actively useful: a `charge` weapon in release mode
    // fires nothing at all while you hold, and this is what lets it still wind
    // up so the released shot arrives with the bonus.
    public static class ForgeSpinUpPatch
    {
        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("WeaponForge.SpinUp");

        [HarmonyPatch(typeof(Shooter), "Shoot")]
        public class OnShoot
        {
            static void Postfix(Shooter __instance)
            {
                if (!ForgeSpinUp.Any || __instance == null)
                    return;

                try
                {
                    WeaponBase weapon = __instance.Weapon;

                    if (weapon == null || weapon.TemplateData == null)
                        return;

                    ForgeSpinUp.Config cfg;

                    if (!ForgeSpinUp.TryGet(weapon.TemplateData, out cfg) ||
                        !cfg.enabled)
                    {
                        return;
                    }

                    ForgeSpin.For(__instance, cfg);
                }
                catch (Exception e)
                {
                    Log.LogError("Attaching the spin-up meter failed: " + e);
                }
            }
        }
    }

    // The wind-up itself.
    public class ForgeSpin : MonoBehaviour
    {
        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("WeaponForge.SpinUp");

        public const string Key = "spinUp";

        private Shooter _shooter;
        private ForgeSpinUp.Config _cfg;

        private float _spin;               // 0..1, the raw wind-up
        private float _lastFiringAt = -999f;

        private WeaponBase _weapon;        // whoever we last published to
        private int _lastStep = -1;
        private int _lastPellets = -1;
        private bool _wasFull;
        private bool _painted;

        // True while we are holding contributions. Without it an idle weapon
        // publishes-then-withdraws every frame for the rest of the run: an
        // Entry, a Layer and a muzzle re-scan, allocated and torn down sixty
        // times a second on a gun nobody is firing.
        private bool _active;

        // "It has been fully wound since it was last empty." `lostSfx` cannot
        // be driven off `_wasFull`, and that was a real bug: `_wasFull` goes
        // false on the frame the meter leaves full, which is the frame the
        // wind-down STARTS - so by the time it reaches zero there is nothing
        // left saying it had ever been full, and the sound never played.
        private bool _reachedFull;

        public static ForgeSpin For(Shooter shooter, ForgeSpinUp.Config cfg)
        {
            var spin = shooter.gameObject.GetComponent<ForgeSpin>();

            if (spin == null)
                spin = shooter.gameObject.AddComponent<ForgeSpin>();

            spin._shooter = shooter;
            spin._cfg = cfg;

            return spin;
        }

        // Exposed for the diagnostics and for anything that wants to know how
        // wound up a gun is (0..1, after the ramp shape is applied).
        public float Fraction()
        {
            if (_cfg == null)
                return 0f;

            if (_cfg.ramp != ForgeSpinUp.Stepped || _cfg.steps <= 1)
                return Mathf.Clamp01(_spin);

            // Floor, so a step is only awarded once it is fully reached - and
            // at exactly 1 this still lands on 1 (Floor(1 * 3) / 3 == 1).
            return Mathf.Floor(Mathf.Clamp01(_spin) * _cfg.steps) / _cfg.steps;
        }

        private void Update()
        {
            if (_cfg == null || _shooter == null)
                return;

            try
            {
                Tick();
            }
            catch (Exception e)
            {
                Log.LogError("Spin-up tick failed: " + e);
            }
        }

        private void Tick()
        {
            WeaponBase weapon = _shooter.Weapon;

            // A CLUSTER REFRESH BUILDS A NEW `WeaponBase`, and a weapon swap
            // mid-run is a different gun entirely - so withdraw from the old one
            // and start again rather than leaving a contribution crediting a
            // weapon nobody is holding.
            if (!ReferenceEquals(weapon, _weapon))
            {
                Withdraw();
                _weapon = weapon;
                _spin = 0f;
                _lastStep = -1;
                _lastPellets = -1;
                _wasFull = false;
                _reachedFull = false;
            }

            if (weapon == null)
                return;

            // SUSTAINED FIRE, as the game itself defines firing: the trigger is
            // down, the owner can pay, and nothing is blocking the Shooter. An
            // empty gun does not wind up, and a jam or a stun costs you the
            // wind-up you had.
            bool firing =
                _shooter.IsShooting &&
                _shooter.OwnerHasResource &&
                !ForgeSpinUp.IsBlocked(_shooter);

            if (firing)
            {
                _lastFiringAt = Time.time;

                _spin = (_cfg.spinUpTime > 0.0001f)
                    ? _spin + Time.deltaTime / _cfg.spinUpTime
                    : 1f;
            }
            else if (Time.time - _lastFiringAt >= _cfg.grace)
            {
                _spin = (_cfg.spinDownTime > 0.0001f)
                    ? _spin - Time.deltaTime / _cfg.spinDownTime
                    : 0f;
            }

            _spin = Mathf.Clamp01(_spin);

            // A COLD, IDLE WEAPON DOES NOTHING AT ALL. This is the one guard
            // that is allowed above the feedback, because there is nothing to
            // feed back: the meter is empty and nothing is published, so the
            // only work left would be publishing zero and withdrawing it again.
            // `_active` is still true on the frame the meter REACHES zero, which
            // is what lets the wind-down finish - the tint gets released and
            // `lostSfx` gets its chance - before this starts skipping.
            if (_spin <= 0.0001f && !_active)
                return;

            // FEEDBACK FIRST, ABOVE EVERY GUARD BELOW IT. House rule, and it is
            // the rule this exact subsystem taught: overheat's `Paint` sat below
            // a cool-delay early return, which is taken on every frame you are
            // firing - so the heat colour only ever repainted once you STOPPED,
            // and the one thing the ramp exists for was the one thing that did
            // not show. Painting is a pure read of `_spin`.
            Paint(weapon);
            Announce();

            Apply(weapon);
        }

        // ------------------------------------------------------------------
        // The rewards
        // ------------------------------------------------------------------
        //
        // EVERY ONE GOES THROUGH `ForgeWeaponStats`, never straight onto the
        // weapon. Three features already wrote `FireRate` from their own private
        // captures before this one existed and overheat was silently erasing the
        // onKill buff; a fourth direct writer would have made that unfixable.
        // See ForgeWeaponStats for the full account.
        private void Apply(WeaponBase weapon)
        {
            float f = Fraction();

            // Nothing left to give: hand the weapon back completely. This is
            // not the same as publishing a multiplier of 1 - withdrawing lets
            // the stack forget its captured base, so a module augmentation that
            // legitimately changes the rate between engagements is picked up
            // next time instead of being overwritten by a stale number.
            if (f <= 0.0001f)
            {
                Withdraw();
                return;
            }

            _active = true;

            if (_cfg.rate)
            {
                Publish(weapon, ForgeWeaponStats.FireRate,
                        Mathf.Lerp(1f, _cfg.fireRateAtFull, f));
            }

            if (_cfg.accuracy)
            {
                // `accuracyAtFull` is the FRACTION OF THE SCATTER REMOVED, so 1
                // is pinpoint and 0.5 halves it. Expressed that way round
                // because "how much better does it get" is the question an
                // author is asking; the multiplier is the inverse.
                Publish(weapon, ForgeWeaponStats.AngleVariance,
                        Mathf.Lerp(
                            1f, Mathf.Max(0f, 1f - _cfg.accuracyAtFull), f));

                // Spread is the DELIBERATE fan, so it is left alone unless the
                // author asked - `spreadAtFull` defaults to 1, and Publish
                // withdraws rather than pinning a base at a multiplier of 1.
                // Tightening a shotgun's spread to nothing turns it into a slug
                // gun, which is a fine weapon and a terrible surprise.
                Publish(weapon, ForgeWeaponStats.Spread,
                        Mathf.Lerp(1f, _cfg.spreadAtFull, f));
            }

            if (_cfg.damage)
            {
                Publish(weapon, ForgeWeaponStats.Damage,
                        Mathf.Lerp(1f, _cfg.damageAtFull, f));
            }

            if (_cfg.pellets)
            {
                Publish(weapon, ForgeWeaponStats.Pellets,
                        Mathf.Lerp(1f, _cfg.pelletsAtFull, f));

                Redress(weapon);
            }

            if (_cfg.cheaper)
            {
                Publish(weapon, ForgeWeaponStats.Cost,
                        Mathf.Lerp(1f, _cfg.costAtFull, f));
            }
        }

        // A MULTIPLIER OF 1 WITHDRAWS RATHER THAN BEING PUBLISHED. Same rule as
        // the onKill buff, and it matters for two configurations that would
        // otherwise pin a captured base for nothing: `accuracyAtFull: 0` (a
        // reward switched on but set to zero effect) and the default
        // `spreadAtFull: 1`. A pinned base is not visible in play, but it stops
        // a legitimate change to the weapon being picked up.
        private void Publish(WeaponBase weapon, int stat, float multiplier)
        {
            if (Mathf.Abs(multiplier - 1f) > 0.0001f)
                ForgeWeaponStats.Set(weapon, stat, Key, multiplier);
            else
                ForgeWeaponStats.Clear(weapon, stat, Key);
        }

        // CHANGING `ProjectileCount` AT RUNTIME IS ONLY COSMETIC ON A BULLET AND
        // IS A CRASH ON A BEAM. Both weapon types keep a list of visuals sized
        // to the pellet count, and they read it differently:
        //
        //   - `WeaponBase.DoShoot` indexes `muzzleParticleSystemInstances` and
        //     GUARDS it (`Count > num`), so extra pellets simply get no muzzle
        //     flash. The shot is there; the gun looks half-dressed.
        //   - **`HitscanWeapon.OnBarrelMoved` indexes `visualsInstances[num]`
        //     with no guard at all**, and it runs every frame the barrel moves.
        //     So a beam weapon whose count rises without its visuals being
        //     rebuilt throws an index-out-of-range SIXTY TIMES A SECOND.
        //
        // `InitializeVisuals` is public, is overridden by `HitscanWeapon` to
        // resize exactly that list, and rebuilds from the weapon's own visual
        // prefab - which is the clone this mod has already customised, so custom
        // beam art and muzzle sprites survive the rebuild.
        //
        // It is called on every change of the ROUNDED count, in BOTH directions.
        // Downward matters as much as upward: `OnBarrelMoved` only walks as far
        // as the current count, so orphaned beam visuals left above it keep
        // whatever state they last had - a beam frozen in mid-air with `Firing`
        // stuck true. Rounding is what keeps this to a handful of calls over a
        // ramp rather than one per frame.
        private void Redress(WeaponBase weapon)
        {
            if (weapon == null)
                return;

            int now = Mathf.RoundToInt(weapon.ProjectileCount);

            if (now == _lastPellets)
                return;

            _lastPellets = now;

            try
            {
                weapon.InitializeVisuals();
            }
            catch (Exception e)
            {
                Log.LogError("Rebuilding the weapon visuals failed: " + e);
            }
        }

        private void Withdraw()
        {
            if (_weapon == null)
                return;

            ForgeWeaponStats.Clear(_weapon, Key);
            ForgeMuzzleTint.Clear(_weapon, Key);

            // The count is back to the weapon's own, so the visual list has to
            // come back with it - see Redress for why the downward direction is
            // the one that leaves a beam stuck on screen.
            if (_cfg != null && _cfg.pellets && _lastPellets >= 0)
                Redress(_weapon);

            if (_painted)
            {
                _painted = false;
                ForgeBuffHud.Clear(this);
            }

            _active = false;
        }

        // ------------------------------------------------------------------
        // Feedback
        // ------------------------------------------------------------------

        private void Paint(WeaponBase weapon)
        {
            if (!_cfg.tintMuzzle)
                return;

            float f = Fraction();
            float amount = f * _cfg.tintStrength;

            // A CLICK RAMP HOLDS THE COLOUR BACK AND THEN SNAPS. The point of
            // that mode is that "fully wound" is a moment you can see and hear,
            // which a colour arriving gradually does not give you.
            if (_cfg.ramp == ForgeSpinUp.Click)
                amount = (f >= 0.9999f) ? _cfg.tintStrength : f * 0.5f *
                    _cfg.tintStrength;

            // Release the layer outright rather than publishing a zero one, so
            // the flash is handed back to whoever else is tinting it (overheat)
            // or to its own captured colour.
            if (amount <= 0.0001f)
            {
                ForgeMuzzleTint.Clear(weapon, Key);
                return;
            }

            ForgeMuzzleTint.Set(weapon, Key, _cfg.color, amount);
        }

        private void Announce()
        {
            float f = Fraction();
            bool full = f >= 0.9999f;

            // Stepped mode announces each stage it climbs to. Only upward: a gun
            // winding down should not chirp its way back to nothing.
            if (_cfg.ramp == ForgeSpinUp.Stepped && _cfg.steps > 1)
            {
                int step = Mathf.RoundToInt(f * _cfg.steps);

                if (step > _lastStep && _lastStep >= 0 && !full)
                    Sound(_cfg.stepSfx);

                _lastStep = step;
            }

            if (full && !_wasFull)
                Sound(_cfg.readySfx);

            if (full)
                _reachedFull = true;

            // THE LATCH, NOT `_wasFull`. `_wasFull` is false from the first
            // frame of the wind-down onwards, so testing it here meant the
            // condition could never be true at the moment the meter hit zero -
            // `lostSfx` was unreachable. `_reachedFull` survives the whole
            // descent and is cleared by the sound it fires.
            if (_reachedFull && f <= 0.0001f)
            {
                _reachedFull = false;
                Sound(_cfg.lostSfx);
            }

            _wasFull = full;

            Say(f);
        }

        private void Say(float f)
        {
            if (!_cfg.showSpin)
                return;

            // The buff indicator is globally OFF by default, so this shows
            // nothing unless it has been switched on - ShowState is a no-op in
            // that case rather than an error.
            if (f <= 0.0001f)
            {
                if (_painted)
                {
                    _painted = false;
                    ForgeBuffHud.Clear(this);
                }

                return;
            }

            _painted = true;

            ForgeBuffHud.ShowState(
                this,
                _cfg.color,
                (f >= 0.9999f
                    ? "SPUN UP"
                    : "SPIN " + Mathf.RoundToInt(f * 100f) + "%"),
                ForgeBuffHud.Kind.State);
        }

        private void Sound(string sfx)
        {
            if (string.IsNullOrEmpty(sfx))
                return;

            try
            {
                AudioManager.PlaySfx(sfx, transform.position);
            }
            catch (Exception)
            {
            }
        }

        private void OnDisable()
        {
            Clear();
        }

        private void OnDestroy()
        {
            Clear();
        }

        // Never leave a weapon carrying a bonus it has not earned. A ship that
        // dies mid-burst, or a Shooter disabled by a screen change, would
        // otherwise come back permanently spun up with nothing in the log.
        private void Clear()
        {
            try
            {
                Withdraw();
                _spin = 0f;
            }
            catch (Exception)
            {
            }
        }
    }
}
