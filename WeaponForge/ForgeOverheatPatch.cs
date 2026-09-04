using System;
using System.Collections.Generic;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace WeaponForge
{
    // Adds heat when the weapon fires. Everything else - cooling, the lockout,
    // the feedback - lives on ForgeHeat, which rides the Shooter.
    //
    // Heat is added in a POSTFIX on `Shooter.Shoot`, which is once per trigger
    // pull rather than once per projectile, so it is multiplied by BurstSize.
    // The alternative (a `WeaponBase.DoShoot` hook, which really is once per
    // shot) has no route back to the Shooter, and the Shooter is what has to be
    // blocked - so this way the two halves stay in one place.
    //
    // A postfix, not a prefix: `Shoot` early-returns when something is already
    // blocking, and a prefix would charge heat for a shot that never happened.
    //
    // WHICH A POSTFIX DOES TOO, AND THAT WAS A REAL BUG - it made overheat plus
    // charge into a weapon that never fired at all. **A Harmony postfix still
    // runs when a prefix returned false and skipped the original.** Three of
    // this mod's own features prefix `Shooter.Shoot` and return false: the
    // charge block (release mode holds fire while you charge), the swarm cap,
    // and anything else that lands there later. In release mode `Shooter.Update`
    // calls `Shoot()` at the full fire rate the whole time you hold the trigger
    // - the charge prefix swallows every one of them - so a weapon at fireRate
    // 8 with `perShot: 8` took 64 heat a second while firing NOTHING, hit its
    // limit in a second and a half, vented, and cancelled the charge it was
    // building. Hold: nothing. Release: nothing. Every value in the file
    // correct, and no line in the log.
    //
    // `FireRatePassed` is the fix and it needs no cooperation from any of them:
    // `Shooter.Update` only calls `Shoot()` when that property is true, and the
    // last thing the real `Shoot` does is set `lastShootTime = Time.time`,
    // which makes it false. So reading it in the postfix answers exactly the
    // question "did the shot actually happen" - true means something skipped
    // the original. Public property, no reflection, and it covers every
    // suppressor at once rather than naming them.
    public static class ForgeOverheatPatch
    {
        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("WeaponForge.Heat");

        [HarmonyPatch(typeof(Shooter), "Shoot")]
        public class OnShoot
        {
            static void Postfix(Shooter __instance)
            {
                if (!ForgeOverheat.Any || __instance == null)
                    return;

                try
                {
                    WeaponBase weapon = __instance.Weapon;

                    if (weapon == null || weapon.TemplateData == null)
                        return;

                    // The shot did not happen - a prefix skipped the original,
                    // so `lastShootTime` was never stamped. No shot, no heat.
                    if (__instance.FireRatePassed)
                        return;

                    ForgeOverheat.Config cfg;

                    if (!ForgeOverheat.TryGet(weapon.TemplateData, out cfg) ||
                        !cfg.enabled)
                    {
                        return;
                    }

                    ForgeHeat.For(__instance, cfg)
                        .Add(cfg.perShot * Mathf.Max(1, weapon.BurstSize));
                }
                catch (Exception e)
                {
                    Log.LogError("Adding heat failed: " + e);
                }
            }
        }
    }

    // The heat itself, and the lockout.
    public class ForgeHeat : MonoBehaviour
    {
        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("WeaponForge.Heat");

        private Shooter _shooter;
        private ForgeOverheat.Config _cfg;

        private float _heat;
        private float _lastFiredAt = -999f;
        private bool _locked;
        private float _unlockAt;

        private WeaponBase _rateWeapon;
        private bool _rateChanged;

        // Whoever we last tinted, so a weapon swapped out mid-run gets its
        // flash handed back rather than keeping the heat colour it happened to
        // be wearing.
        private WeaponBase _tintWeapon;

        public static ForgeHeat For(Shooter shooter, ForgeOverheat.Config cfg)
        {
            var heat = shooter.gameObject.GetComponent<ForgeHeat>();

            if (heat == null)
                heat = shooter.gameObject.AddComponent<ForgeHeat>();

            heat._shooter = shooter;
            heat._cfg = cfg;

            return heat;
        }

        // True only while the blowout's own vent shot is being fired. See Vent.
        private bool _venting;

        public bool Venting { get { return _venting; } }

        public void Add(float amount)
        {
            // The vent shot must not charge heat. We are already at max, so
            // Blow() would re-enter itself from inside itself.
            if (_cfg == null || amount <= 0f || _venting)
                return;

            _lastFiredAt = Time.time;
            _heat = Mathf.Min(_cfg.max, _heat + amount);

            // Repaint where the heat CHANGED, not only on the next frame's
            // tick. This is called from the Shoot postfix, and the muzzle flash
            // for that same shot is emitted in the same frame - so painting
            // here is what makes the flash you are looking at wear the heat
            // that shot just added, rather than the previous frame's.
            Paint();

            if (_heat >= _cfg.max && !_locked)
                Blow();
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
                Log.LogError("Heat tick failed: " + e);
            }
        }

        private void Tick()
        {
            // THE FEEDBACK RUNS FIRST, AND THAT ORDERING IS THE WHOLE FIX.
            //
            // R13 test 142: *"holding fire keeps the original color until the
            // very last shot when it shoots orange ... if you tap the fire the
            // muzzle flair color changes throughout firing. Theres a build up
            // tapping thats missing while holding it."*
            //
            // `Paint()` used to sit at the BOTTOM of this method, below the
            // cool-delay early return - and that return is taken on every
            // frame you are firing, because `_lastFiredAt` was just stamped.
            // So the heat colour was only ever repainted once you STOPPED
            // shooting. Tapping worked (the delay expires between taps); the
            // one thing the ramp exists for did not.
            //
            // > An early return added for ONE concern silently swallowed a
            // > second concern appended below it. That is the same shape as
            // > `Shooter.Update` returning before its own `StopContinousSound`,
            // > which is what made a stunned gun keep roaring - two different
            // > classes, one week apart. When appending to a method, check what
            // > returns above the line you are adding to.
            //
            // Painting is a pure read of `_heat`, so it is safe anywhere and
            // belongs where nothing can skip it.
            Paint();

            // Held weapons: charge by the second as well as by the shot, so a
            // beam or a hold-to-fire ring can overheat at all.
            if (_cfg.perSecond > 0f && !_locked && _shooter.IsShooting)
                Add(_cfg.perSecond * Time.deltaTime);

            if (_locked)
            {
                Cool();

                bool done =
                    _cfg.response == ForgeOverheat.Jam
                        ? Time.time >= _unlockAt
                        : _heat <= Mathf.Max(0f, _cfg.ventTo);

                if (done)
                    Recover();

                return;
            }

            // The delay is what makes trigger discipline worth anything. With
            // cooling starting the instant you release, tapping is free and the
            // whole mechanic barely registers.
            if (Time.time - _lastFiredAt < _cfg.coolDelay)
                return;

            Cool();
            Degrade();
        }

        private void Cool()
        {
            if (_heat <= 0f)
                return;

            _heat = Mathf.Max(0f, _heat - _cfg.coolPerSecond * Time.deltaTime);
            Paint();
        }

        private float Fraction()
        {
            return _cfg.max > 0f ? Mathf.Clamp01(_heat / _cfg.max) : 0f;
        }

        // ---- the blowout --------------------------------------------------
        private void Blow()
        {
            // THE VENT SHOT GOES FIRST, BEFORE THE LOCKOUT, AND IT HAS TO
            // ACTUALLY FIRE.
            //
            // This was ordered the other way round and it made the vent shot
            // structurally impossible - reported for R12 as *"the charge shot
            // doesn't fire"*, with a file whose `charge` block was present and
            // correct, so nothing warned.
            //
            // Two independent reasons it could never happen:
            //
            //   1. `Vent()` only ARMED the next shot. ForgeChargePatch.Arm sets
            //      `armedShots`/`armedLevel` and marks the next DoShoot as the
            //      charged one - it fires nothing itself.
            //   2. The lines below then blocked the Shooter and cleared
            //      `IsTriggerPulled`. In vent mode the gun stays blocked until
            //      fully cool (~2.9s on his file) while `armedUntil` expires
            //      after 0.35s, so the arming was always dead before a shot
            //      could claim it.
            //
            // So the promise in the build log - "Blowout fires a charged shot" -
            // could only be kept by firing one here.
            if (_cfg.ventShot)
                Vent();

            _locked = _cfg.response != ForgeOverheat.Slow &&
                      _cfg.response != ForgeOverheat.None;

            if (_locked)
            {
                _unlockAt = Time.time + Mathf.Max(0.05f, _cfg.jamSeconds);

                _shooter.Block(this);

                // MANDATORY, and easy to miss. Shooter.Update returns as soon
                // as anything is blocking, and StopContinousSound lives after
                // that return - so a weapon mid-loop would keep looping its
                // firing sound for the rest of the run.
                _shooter.StopContinousSound();
                _shooter.StopWarmupSound();

                if (_shooter.Weapon != null)
                    _shooter.Weapon.IsTriggerPulled = false;
            }

            if (!string.IsNullOrEmpty(_cfg.overheatSfx))
                Sound(_cfg.overheatSfx);

            if (_cfg.shake)
                Shake();

            if (_cfg.selfDamage > 0f)
                Burn();

            Paint();
            Say();
        }

        private void Recover()
        {
            _locked = false;
            _shooter.Unblock(this);

            if (!string.IsNullOrEmpty(_cfg.recoverSfx))
                Sound(_cfg.recoverSfx);

            Paint();
            Say();
        }

        // ---- the fire-rate penalty ---------------------------------------
        //
        // PUBLISHED TO `ForgeWeaponStats`, NOT WRITTEN ONTO THE WEAPON. This
        // used to remember the original rate and assign `_rateWas * f` every
        // frame, which is correct for exactly one writer and silently wrong
        // with two - and there were three:
        //
        //   - `ForgeKillBuff` also multiplies `FireRate` from its own captured
        //     base. This method writes EVERY FRAME while there is heat, so
        //     whatever the kill buff wrote was overwritten on the next frame:
        //     **the onKill fire-rate buff did nothing at all on an overheating
        //     weapon**, and the buff's own drift check then adopted this
        //     feature's degraded number as the weapon's "real" rate and wrote
        //     it back on expiry.
        //   - `spinUp` is a fourth, and the author asked for it to compose with
        //     overheat - a gun that winds up to a screaming rate and then jams.
        //     Two features multiplying the same field cannot be ordered.
        //
        // The stack keeps one captured base per weapon and multiplies every
        // contribution, so this is now a statement of intent ("I want the rate
        // at f of normal") rather than a claim on the field. The weapon-identity
        // check that used to live here is free: a cluster refresh builds a new
        // `WeaponBase`, which is simply a key the stack has never seen.
        private void Degrade()
        {
            if (_cfg.response != ForgeOverheat.Slow)
                return;

            WeaponBase weapon = _shooter.Weapon;

            if (!ReferenceEquals(weapon, _rateWeapon))
            {
                RestoreRate();
                _rateWeapon = weapon;
            }

            if (weapon == null)
                return;

            float f = Mathf.Lerp(
                1f, Mathf.Clamp(_cfg.slowestFireRate, 0.05f, 1f), Fraction());

            if (f < 0.999f)
            {
                ForgeWeaponStats.Set(
                    weapon, ForgeWeaponStats.FireRate, "overheat", f);

                _rateChanged = true;
            }
            else if (_rateChanged)
            {
                // Fully cool: withdraw rather than publishing a multiplier of
                // 1, so the stack can forget its captured base and pick up any
                // legitimate change to the weapon's own rate.
                ForgeWeaponStats.Clear(
                    weapon, ForgeWeaponStats.FireRate, "overheat");

                _rateChanged = false;
            }
        }

        private void RestoreRate()
        {
            if (!_rateChanged || _rateWeapon == null)
                return;

            ForgeWeaponStats.Clear(
                _rateWeapon, ForgeWeaponStats.FireRate, "overheat");

            _rateChanged = false;
        }

        // ---- feedback -----------------------------------------------------
        //
        // ONE CAPTURED BASE PER SYSTEM, and never a read-back.
        //
        // This shipped reading `main.startColor` and lerping THAT toward
        // hotColor every frame, which is a read-modify-write of the thing it
        // was using as its own reference point. Two consequences, and he
        // reported the second: the colour converges on fully-hot within about a
        // second no matter how small the heat fraction is, and once the base is
        // gone `Lerp(now, hot, 0)` returns `now`, so **cooling to zero heat can
        // never restore anything**. Reported for R12 as *"the muzzle color
        // never reverts back to it's base color"*.
        //
        // This is the same mistake ForgeUnitTint exists to prevent, in a
        // different subsystem. The rule generalises past sprite colour: **if a
        // per-frame write reads its own previous output, it has no base to
        // return to.** ForgeBurnColorTerrainPatch and RgbAnimator both capture
        // the whole MinMaxGradient up front, which is the house idiom.
        //
        // AND THE CAPTURE NOW LIVES IN `ForgeMuzzleTint` RATHER THAN HERE, for
        // the reason the fire rate moved to `ForgeWeaponStats`: `spinUp` tints
        // the same flash toward its own colour, and the author asked for the two
        // blocks to compose on one weapon. Two private captures of one field is
        // the permanent-bake-in bug - whichever captures second records the
        // first one's output as the base, and restores to it. One captured base
        // plus named layers has no ordering to get wrong. With overheat as the
        // only layer the result is identical to what this method used to
        // compute, which is why existing weapons look unchanged.
        private void Paint()
        {
            if (!_cfg.tintMuzzle)
                return;

            WeaponBase weapon = _shooter.Weapon;

            if (!ReferenceEquals(weapon, _tintWeapon))
            {
                RestoreMuzzle();
                _tintWeapon = weapon;
            }

            if (weapon == null)
                return;

            ForgeMuzzleTint.Set(
                weapon, ForgeMuzzleTint.Heat, _cfg.hotColor,
                Fraction() * _cfg.tintStrength);
        }

        // Put the muzzle back exactly as it was found.
        //
        // The prefab clone is shared with the weapon, and a weapon swapped out
        // mid-run would otherwise keep whatever heat colour it had at the
        // moment it left your hands.
        private void RestoreMuzzle()
        {
            if (_tintWeapon == null)
                return;

            ForgeMuzzleTint.Clear(_tintWeapon, ForgeMuzzleTint.Heat);
        }

        private void Say()
        {
            if (!_cfg.showHeat)
                return;

            // The buff indicator is globally OFF by default, so this shows
            // nothing unless it has been switched on. ShowState is a no-op in
            // that case rather than an error.
            ForgeBuffHud.ShowState(
                this,
                _locked ? Color.red : _cfg.hotColor,
                _locked ? "OVERHEATED" : "HEAT " +
                    Mathf.RoundToInt(Fraction() * 100f) + "%",
                ForgeBuffHud.Kind.State);
        }

        private void Sound(string sfx)
        {
            try
            {
                AudioManager.PlaySfx(sfx, transform.position);
            }
            catch (Exception)
            {
            }
        }

        private void Shake()
        {
            try
            {
                WeaponData data = _shooter.Weapon != null
                    ? _shooter.Weapon.TemplateData
                    : null;

                // The same call the game uses everywhere it shakes the camera
                // (ShakeOnStart, ShipCameraShaker, the anim-event player) - but
                // reached by reflection, because ProCamera2D is a third-party
                // assembly this project does not reference. If it is not there,
                // the shake simply does not happen; nothing else is affected.
                if (data == null)
                    return;

                // Read the preset reflectively as well: its type lives in the
                // same third-party assembly, so naming it would drag the
                // reference back in through the field access.
                var field = AccessTools.Field(typeof(WeaponData), "shakePreset");

                if (field == null)
                    return;

                ForgeOverheat.Shake(field.GetValue(data));
            }
            catch (Exception)
            {
            }
        }

        private void Burn()
        {
            try
            {
                Unit unit = _shooter.Unit;

                if (unit == null)
                    return;

                var health = unit.GetComponentInChildren<DamagableResource>(true);

                if (health == null || health.resource == null)
                    return;

                // Through TakeDamage rather than writing the tank, so shields
                // and i-frames apply exactly as they would to any other damage.
                health.TakeDamage(
                    new Damage(_cfg.selfDamage, health.resource));
            }
            catch (Exception e)
            {
                Log.LogError("Overheat self-damage failed: " + e);
            }
        }

        // The reward for holding it to the limit: the blowout arms a charged
        // shot. Routed through the charge feature so it inherits every one of
        // that block's multipliers instead of growing a second set.
        private void Vent()
        {
            // FireExternally, not ArmExternally - arming alone was the bug.
            //
            // `_venting` is mandatory around it: this runs from the `Shoot`
            // POSTFIX, so firing another shot re-enters that postfix, and heat
            // is already at max - Add would call Blow from inside Blow. Without
            // the guard this is an immediate stack overflow rather than a
            // subtle misbehaviour.
            _venting = true;

            try
            {
                if (!ForgeChargePatch.FireExternally(
                        _shooter, _cfg.ventShotLevel))
                {
                    Log.LogWarning(
                        "overheat \"ventShot\" needs a \"charge\" block on " +
                        "the same weapon - that block IS the set of " +
                        "multipliers the vent shot scales by, so there is " +
                        "nothing to scale without it. No vent shot fired.");
                }
            }
            catch (Exception e)
            {
                Log.LogError("Firing a vent shot failed: " + e);
            }
            finally
            {
                _venting = false;
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

        // Never leave a weapon blocked. A ship that dies mid-jam, or a Shooter
        // disabled by a screen change, would otherwise come back unable to fire
        // with nothing in the log to explain it.
        private void Clear()
        {
            try
            {
                RestoreRate();
                RestoreMuzzle();

                if (_locked && _shooter != null)
                    _shooter.Unblock(this);

                _locked = false;
            }
            catch (Exception)
            {
            }
        }
    }
}
