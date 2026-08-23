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
        private float _rateWas;
        private bool _rateChanged;

        public static ForgeHeat For(Shooter shooter, ForgeOverheat.Config cfg)
        {
            var heat = shooter.gameObject.GetComponent<ForgeHeat>();

            if (heat == null)
                heat = shooter.gameObject.AddComponent<ForgeHeat>();

            heat._shooter = shooter;
            heat._cfg = cfg;

            return heat;
        }

        public void Add(float amount)
        {
            if (_cfg == null || amount <= 0f)
                return;

            _lastFiredAt = Time.time;
            _heat = Mathf.Min(_cfg.max, _heat + amount);

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
            Paint();
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

            if (_cfg.ventShot)
                Vent();

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
        // Same shape as the enemy slow: remember the original, always recompute
        // from it, and only write it back if the weapon is still the same
        // object - a grid refresh builds a new WeaponBase and a stale rate
        // written onto that would be a permanent debuff.
        private void Degrade()
        {
            if (_cfg.response != ForgeOverheat.Slow)
                return;

            WeaponBase weapon = _shooter.Weapon;

            if (weapon == null)
                return;

            if (!ReferenceEquals(weapon, _rateWeapon))
            {
                _rateWeapon = weapon;
                _rateWas = weapon.FireRate;
                _rateChanged = false;
            }

            float f = Mathf.Lerp(
                1f, Mathf.Clamp(_cfg.slowestFireRate, 0.05f, 1f), Fraction());

            weapon.FireRate = _rateWas * f;
            _rateChanged = f < 0.999f;
        }

        private void RestoreRate()
        {
            if (!_rateChanged || _rateWeapon == null || _shooter == null)
                return;

            if (ReferenceEquals(_shooter.Weapon, _rateWeapon))
                _rateWeapon.FireRate = _rateWas;

            _rateChanged = false;
        }

        // ---- feedback -----------------------------------------------------
        private void Paint()
        {
            if (!_cfg.tintMuzzle || _shooter.Weapon == null)
                return;

            List<ParticleSystem> muzzles =
                ForgeOverheat.MuzzlesOf(_shooter.Weapon);

            if (muzzles == null)
                return;

            float f = Fraction();

            foreach (ParticleSystem ps in muzzles)
            {
                if (ps == null)
                    continue;

                // A muzzle flash is usually TWO stacked systems, so tint the
                // children as well or the stock puff shows through underneath.
                foreach (ParticleSystem one in
                         ps.GetComponentsInChildren<ParticleSystem>(true))
                {
                    if (one == null)
                        continue;

                    ParticleSystem.MainModule main = one.main;
                    Color now = main.startColor.color;

                    main.startColor = Color.Lerp(
                        new Color(now.r, now.g, now.b, now.a),
                        _cfg.hotColor,
                        f);
                }
            }
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
            if (!ForgeChargePatch.ArmExternally(_shooter, _cfg.ventShotLevel))
            {
                Log.LogWarning(
                    "overheat \"ventShot\" needs a \"charge\" block on the " +
                    "same weapon - that block IS the set of multipliers the " +
                    "vent shot scales by, so there is nothing to scale without " +
                    "it. No vent shot fired.");
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
