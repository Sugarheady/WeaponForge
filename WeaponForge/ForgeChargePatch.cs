using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using MyBox;
using UnityEngine;

namespace WeaponForge
{
    // Drives ForgeCharge.
    //
    //   Shooter.Update             postfix - run the meter, the charging
    //                                        sound and the release.
    //   Shooter.Shoot              prefix  - suppress the normal fire while a
    //                                        Release charge is building; arm
    //                                        the big shot in Auto mode.
    //   Shooter.PlayContinousSound prefix  - and suppress the held FIRING
    //                                        loop with it, or a weapon that
    //                                        is not shooting still sounds
    //                                        like it is.
    //   WeaponBase.DoShoot         pre/post - scale the weapon for exactly
    //                                        one volley, then put it back.
    //   Projectile.Shoot           prefix  - size, colour and pierce, which
    //   PhysicsProjectile.Shoot              live on the shot rather than on
    //                                        the weapon.
    //
    // Why the scaling is split in two: DoShoot is where the game reads
    // ProjectileCount, Spread, Damage, Explosion and speed, and it is
    // SYNCHRONOUS (FireSingle instantiates and calls Shoot() before it
    // returns), so a prefix/postfix pair around it can borrow the weapon and
    // hand it back with nothing else able to observe the change. That is also
    // what makes it burst-safe: a burst weapon calls DoShoot once per shot in
    // the burst and each one gets its own scoped copy.
    public static class ForgeChargePatch
    {
        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("WeaponForge.Charge");

        private class State
        {
            public WeaponBase weapon;
            public ForgeChargeMeter meter;

            public float held;          // seconds the trigger has been down
            public bool wasHeld;
            public bool readyPlayed;

            // A charged volley that has been fired but may still have burst
            // shots to come. armedShots counts DoShoot calls, not frames.
            //
            // armedUntil is a belt to that braces: BurstSize is our reading of
            // how many times Fire() reaches DoShoot, and if a future version
            // of the game ever bursts differently, an over-count would leave
            // the weapon armed and hand out a free charged shot later. The
            // deadline closes the window regardless of the count.
            public float armedLevel;
            public int armedShots;
            public float armedUntil;
        }

        private static readonly Dictionary<Shooter, State> _states =
            new Dictionary<Shooter, State>();

        // How many states currently have a volley armed. DoShoot runs for
        // every weapon fired by anything in the level, so it has to be able to
        // rule itself out in one integer compare.
        private static int _armed;

        // Set to true only while we are calling Shooter.Shoot ourselves, so
        // our own suppression prefix lets that one through.
        private static bool _firing;

        // --- what a charged shot does to the individual projectile ---------
        // Statics rather than a component because the whole window is one
        // synchronous DoShoot, exactly like ForgeVolley.
        public static bool ShotPending;
        public static float ShotSize = 1f;
        public static int ShotPierce;
        public static bool ShotHasColor;
        public static Color ShotColor = Color.white;

        // ------------------------------------------------------------------

        private static FieldInfo _blockers;
        private static bool _blockersLookedUp;

        // Shooter.Update bails out completely while something is blocking it
        // (a shop, a cutscene, a dead ship). Our postfix still runs, so ask
        // directly rather than charging up behind a menu. Fails OPEN: if the
        // field ever goes away, charging keeps working.
        private static bool Blocked(Shooter shooter)
        {
            if (!_blockersLookedUp)
            {
                _blockersLookedUp = true;
                _blockers = AccessTools.Field(typeof(Shooter), "blockers");
            }

            if (_blockers == null)
                return false;

            try
            {
                var set = _blockers.GetValue(shooter) as
                    System.Collections.ICollection;

                return set != null && set.Count > 0;
            }
            catch
            {
                return false;
            }
        }

        // Arm the next volley as a charged shot on behalf of a DIFFERENT
        // feature - overheat's vent shot is the first.
        //
        // Deliberately routed through the same Arm/_states machinery as the
        // hold-to-charge trigger rather than duplicating the multiplier set:
        // every scaling the charge block already supports (damage, size, speed,
        // range, knockback, push, explosion, burn, spread, cost) comes along,
        // and the burst-tail bookkeeping that keeps the rest of a burst charged
        // is bookkeeping we do not want to write twice.
        //
        // Returns false when the weapon has no `charge` block, because the
        // multipliers ARE that block - there is nothing to scale by without it.
        // FIRE a charged shot on somebody else's behalf, right now.
        //
        // Used by overheat's `ventShot`. It used to only ARM one and that was
        // the bug: arming marks the NEXT shot as charged, and the caller then
        // locked the weapon for seconds while the arming expires in 0.35s - so
        // nothing ever claimed it. See ForgeHeat.Blow.
        //
        // Three things this has to do that a bare `shooter.Shoot()` would not:
        //
        //   * `_firing` - a release-mode weapon with `holdFire` prefixes Shoot
        //     and returns false, so without this the charge block swallows the
        //     very shot it is sizing.
        //   * Cost 0 - a vent is the gun discharging itself, not a shot the
        //     player bought. `Shooter.Shoot` subtracts Cost with no check at
        //     all (the check lives in Update), so leaving it would quietly
        //     drive the tank negative.
        //   * restore both in a `finally`, because Cost lives on the shared
        //     WeaponBase.
        public static bool FireExternally(Shooter shooter, float level)
        {
            if (shooter == null || shooter.Weapon == null)
                return false;

            try
            {
                WeaponBase weapon = shooter.Weapon;
                ForgeCharge.Config cfg;

                if (!ForgeCharge.TryGet(weapon.TemplateData, out cfg))
                    return false;

                Arm(Get(shooter, weapon), weapon, Mathf.Clamp01(level));

                float cost = weapon.Cost;

                _firing = true;
                weapon.Cost = 0f;

                try
                {
                    shooter.Shoot();
                }
                finally
                {
                    _firing = false;
                    weapon.Cost = cost;
                }

                return true;
            }
            catch (Exception e)
            {
                Log.LogError("Firing an external charged shot failed: " + e);
                return false;
            }
        }

        private static State Get(Shooter shooter, WeaponBase weapon)
        {
            State s;

            if (_states.TryGetValue(shooter, out s))
            {
                if (s.weapon == weapon)
                    return s;

                // The weapon changed under us - a grid refresh rebuilds the
                // WeaponBase, and carrying a half-built charge across that
                // would mean a swap could hand you a free charged shot.
                Reset(s);
                s.weapon = weapon;
                return s;
            }

            // A ship that died takes its Shooter with it, and its Update stops
            // running - so nothing would ever come back to clean that entry up
            // or destroy the meter object hanging in the world. Sweep on the
            // way in. New states only happen on equip, so this costs nothing.
            Sweep();

            s = new State { weapon = weapon };
            _states[shooter] = s;
            return s;
        }

        private static void Sweep()
        {
            List<Shooter> dead = null;

            foreach (var pair in _states)
            {
                // Unity's overloaded == is the point here: a destroyed Shooter
                // compares equal to null while still being a usable key.
                if (pair.Key != null)
                    continue;

                if (dead == null)
                    dead = new List<Shooter>();

                dead.Add(pair.Key);
            }

            if (dead == null)
                return;

            for (int i = 0; i < dead.Count; i++)
            {
                State s;

                if (!_states.TryGetValue(dead[i], out s))
                    continue;

                Reset(s);

                if (s.meter != null)
                    UnityEngine.Object.Destroy(s.meter.gameObject);

                _states.Remove(dead[i]);
            }
        }

        private static void Reset(State s)
        {
            if (s.armedShots > 0)
                _armed--;

            s.held = 0f;
            s.wasHeld = false;
            s.readyPlayed = false;
            s.armedLevel = 0f;
            s.armedShots = 0;
            s.armedUntil = 0f;

            if (s.meter != null)
                s.meter.Hide();
        }

        private static void Disarm(State s)
        {
            if (s.armedShots <= 0)
                return;

            s.armedShots = 0;
            s.armedLevel = 0f;
            s.armedUntil = 0f;
            _armed--;
        }

        private static void Drop(Shooter shooter)
        {
            State s;

            if (!_states.TryGetValue(shooter, out s))
                return;

            Reset(s);

            if (s.meter != null)
                UnityEngine.Object.Destroy(s.meter.gameObject);

            _states.Remove(shooter);
        }

        [HarmonyPatch(typeof(Shooter), "Update")]
        public class OnUpdate
        {
            static void Postfix(Shooter __instance)
            {
                if (!ForgeCharge.Any)
                    return;

                try
                {
                    WeaponBase weapon = __instance.Weapon;
                    ForgeCharge.Config cfg;

                    if (weapon == null ||
                        !ForgeCharge.TryGet(weapon.TemplateData, out cfg))
                    {
                        Drop(__instance);
                        return;
                    }

                    State s = Get(__instance, weapon);

                    bool trigger = __instance.IsShooting;

                    // Charging is free, but it is not free of the weapon's
                    // cost: an empty tank stalls the meter at zero, so the
                    // weapon visibly refuses instead of filling up and then
                    // eating the release.
                    bool charging =
                        trigger &&
                        !Blocked(__instance) &&
                        __instance.OwnerHasResource;

                    if (charging)
                    {
                        Hold(__instance, weapon, cfg, s);
                    }
                    else if (s.wasHeld && !trigger)
                    {
                        // A real trigger-up. Only this fires.
                        Release(__instance, weapon, cfg, s);
                    }
                    else
                    {
                        // Charging was INTERRUPTED with the trigger still
                        // down - a shop opened, the tank ran dry. Letting
                        // that fire would put a charged shot into whatever
                        // just blocked the weapon, so the charge is simply
                        // dropped and rebuilds when the trigger is next
                        // genuinely free.
                        Cancel(s);
                    }

                    s.wasHeld = charging;
                }
                catch (Exception e)
                {
                    Log.LogError("Charge update failed: " + e);
                }
            }
        }

        private static void Hold(
            Shooter shooter, WeaponBase weapon,
            ForgeCharge.Config cfg, State s)
        {
            // A new press ends the previous shot's business, whatever state
            // the burst left behind.
            if (!s.wasHeld)
                Disarm(s);

            s.held += Time.deltaTime;

            float t = ForgeCharge.Level(s.held, cfg);

            // THE LUNGE'S WIND-UP. Slowing the ship while the meter fills is
            // the Maggot's telegraph - it charges by almost stopping, not by
            // running a timer. Renewed every frame on purpose: the controller
            // gives the speed back on its own if this stops arriving, so none
            // of the several ways a charge can be abandoned needs to know
            // about it.
            if (ForgeLunge.Any)
                ForgeLungeController.Wind(shooter, weapon, t);

            if (t >= 1f && !s.readyPlayed)
            {
                s.readyPlayed = true;

                if (!string.IsNullOrEmpty(cfg.readySfx))
                    AudioManager.PlaySfx(cfg.readySfx, shooter.transform.position);
            }

            // The looping charge sound is the weapon's own warmupSfx slot.
            // Shooter already owns starting it, keeping the handle and
            // stopping it - and its own Update stops it for us the moment the
            // trigger comes up. In Auto mode the weapon is genuinely firing,
            // so the continuous SHOOTING loop is already playing and a second
            // held sound on top of it would just be noise.
            if (cfg.mode == ForgeCharge.Mode.Release &&
                !string.IsNullOrEmpty(weapon.WarmupSfx))
            {
                shooter.PlayWarmupSound();
            }

            if (!cfg.meter)
                return;

            if (s.meter == null)
                s.meter = ForgeChargeMeter.Create(cfg);

            if (s.meter != null)
            {
                IBarrel barrel = shooter.Barrel;

                if (barrel != null)
                {
                    s.meter.Show(
                        barrel.Position + barrel.Direction * weapon.BarrelLength,
                        barrel.Direction,
                        t,
                        cfg);
                }
            }
        }

        // The charge is abandoned without firing.
        private static void Cancel(State s)
        {
            if (s.meter != null)
                s.meter.Hide();

            s.held = 0f;
            s.readyPlayed = false;
        }

        private static void Release(
            Shooter shooter, WeaponBase weapon,
            ForgeCharge.Config cfg, State s)
        {
            float t = ForgeCharge.Level(s.held, cfg);

            Cancel(s);

            // Auto mode never fires on release - it has been firing all along.
            if (cfg.mode == ForgeCharge.Mode.Auto)
                return;

            // THE LAUNCH. Deliberately after the Auto check: an Auto weapon
            // has no release to launch on, so a lunge on one would fire at
            // some arbitrary moment. Placed before the fire-rate gate below so
            // the movement always happens even when the shot itself is
            // refused - a lunge that silently did nothing because the weapon
            // was still on cooldown would be maddening to diagnose.
            if (ForgeLunge.Any)
                ForgeLungeController.Launch(shooter, weapon, t);

            // The weapon's own cadence still applies. A charge that took
            // longer than one shot's worth of time has already passed this;
            // what it stops is tap-tap-tap outrunning the fire rate.
            if (!shooter.FireRatePassed)
                return;

            bool charged = t >= cfg.min;

            if (!charged && !cfg.tapFire)
                return;

            float knockback = weapon.KnockbackForce;
            float cost = weapon.Cost;

            if (charged)
            {
                weapon.KnockbackForce =
                    knockback * ForgeCharge.Mul(cfg.knockback, t, cfg);
                weapon.Cost = cost * ForgeCharge.Mul(cfg.cost, t, cfg);

                // OwnerHasResource reads weapon.Cost live, so asking it AFTER
                // the scaling is what tests the price of the shot actually
                // about to be fired. This matters: Shooter.Shoot subtracts the
                // cost without checking anything (the check lives in Update,
                // which we are bypassing), so an unaffordable charged shot
                // would otherwise just drive the tank negative.
                //
                // Falling back to a normal shot rather than firing nothing is
                // deliberate. Charging already required affording the base
                // shot, so this one is payable - and a second of holding that
                // produces silence is the worst thing a charge weapon can do.
                if (!shooter.OwnerHasResource)
                {
                    weapon.KnockbackForce = knockback;
                    weapon.Cost = cost;
                    charged = false;
                }
            }

            if (!charged && (!cfg.tapFire || !shooter.OwnerHasResource))
                return;

            if (charged)
                Arm(s, weapon, t);

            _firing = true;

            try
            {
                // Shooter.Shoot reads Cost and KnockbackForce itself, both
                // synchronously, so scoping them around this one call is
                // enough. Everything the WEAPON reads is scoped around
                // DoShoot instead, because a burst reaches that more than once.
                shooter.Shoot();
            }
            finally
            {
                _firing = false;
                weapon.KnockbackForce = knockback;
                weapon.Cost = cost;
            }
        }

        private static void Arm(State s, WeaponBase weapon, float level)
        {
            if (s.armedShots <= 0)
                _armed++;

            s.armedLevel = level;

            // One DoShoot per shot in the burst. Fire() is async, so the tail
            // of a burst lands frames after the release call has returned -
            // the count is what keeps those shots charged too, and what stops
            // the arming leaking into whatever the weapon does next.
            int shots = Mathf.Max(1, weapon.BurstSize);

            s.armedShots = shots;
            s.armedUntil =
                Time.time +
                Mathf.Max(0f, weapon.BurstDelay) * (shots - 1) + 0.35f;
        }

        [HarmonyPatch(typeof(Shooter), "Shoot")]
        public class OnShoot
        {
            static bool Prefix(Shooter __instance)
            {
                if (_firing || !ForgeCharge.Any)
                    return true;

                try
                {
                    WeaponBase weapon = __instance.Weapon;
                    ForgeCharge.Config cfg;

                    if (weapon == null ||
                        !ForgeCharge.TryGet(weapon.TemplateData, out cfg))
                    {
                        return true;
                    }

                    if (cfg.mode == ForgeCharge.Mode.Auto)
                    {
                        State s = Get(__instance, weapon);

                        // Meter full: this shot - the one the game is about to
                        // fire anyway - is the charged one, and the meter
                        // starts again from empty.
                        if (s.armedShots <= 0 &&
                            ForgeCharge.Level(s.held, cfg) >= 1f)
                        {
                            Arm(s, weapon, 1f);
                            s.held = 0f;
                            s.readyPlayed = false;
                        }

                        return true;
                    }

                    // Release mode: holding the trigger builds a shot, it does
                    // not fire one.
                    return !cfg.holdFire;
                }
                catch (Exception e)
                {
                    Log.LogError("Charge shoot failed: " + e);
                    return true;
                }
            }
        }

        // A held weapon that is not firing must not still sound like it is.
        // Shooter starts this loop in Update BEFORE it gets as far as Shoot(),
        // so suppressing the shot alone would leave the gun roaring away over
        // a silent charge.
        [HarmonyPatch(typeof(Shooter), "PlayContinousSound")]
        public class OnContinousSound
        {
            static bool Prefix(Shooter __instance)
            {
                if (!ForgeCharge.Any)
                    return true;

                try
                {
                    WeaponBase weapon = __instance.Weapon;
                    ForgeCharge.Config cfg;

                    if (weapon == null ||
                        !ForgeCharge.TryGet(weapon.TemplateData, out cfg))
                    {
                        return true;
                    }

                    return !(cfg.mode == ForgeCharge.Mode.Release &&
                             cfg.holdFire);
                }
                catch
                {
                    return true;
                }
            }
        }

        // ---- the volley itself ------------------------------------------

        private class Scope
        {
            public State state;
            public ForgeCharge.Config cfg;

            public Damage damage;
            public Explosion explosion;
            public MinMaxFloat burn;
            public float push;
            public float count;
            public float spread;

            public ProjectileWeapon pw;
            public float speed;
            public float range;

            public HitscanWeapon hw;
            public float beamRange;
            public float rayWidth;
        }

        [HarmonyPatch(typeof(WeaponBase), "DoShoot")]
        public class OnDoShoot
        {
            // FIRST, and it has to be. ForgeVolley prefixes this same method
            // and snapshots ProjectileCount for the volley about to fire;
            // Harmony gives no order between two prefixes on one method, so
            // without this a charged volley with extra pellets could hand
            // converge a pellet count that is one volley out of date - and the
            // pellets past that count would be steered as if they did not
            // exist.
            [HarmonyPriority(Priority.First)]
            static void Prefix(WeaponBase __instance, out Scope __state)
            {
                __state = null;

                if (_armed == 0)
                    return;

                try
                {
                    State found = null;

                    foreach (var pair in _states)
                    {
                        State s = pair.Value;

                        if (s.armedShots <= 0 || s.weapon != __instance)
                            continue;

                        if (Time.time > s.armedUntil)
                        {
                            Disarm(s);
                            continue;
                        }

                        found = s;
                        break;
                    }

                    if (found == null)
                        return;

                    ForgeCharge.Config cfg;

                    if (!ForgeCharge.TryGet(__instance.TemplateData, out cfg))
                        return;

                    __state = Apply(__instance, found, cfg);
                }
                catch (Exception e)
                {
                    __state = null;
                    Log.LogError("Charged volley failed to scale: " + e);
                }
            }

            static void Postfix(WeaponBase __instance, Scope __state)
            {
                if (__state == null)
                    return;

                try
                {
                    Restore(__instance, __state);
                }
                catch (Exception e)
                {
                    Log.LogError(
                        "Charged volley failed to restore the weapon - it " +
                        "may stay boosted until you re-equip it: " + e);
                }
            }
        }

        private static Scope Apply(
            WeaponBase w, State s, ForgeCharge.Config cfg)
        {
            float t = s.armedLevel;

            var sc = new Scope
            {
                state = s,
                cfg = cfg,
                damage = w.Damage,
                explosion = w.Explosion,
                burn = w.Burn,
                push = w.PushForce,
                count = w.ProjectileCount,
                spread = w.Spread,
            };

            float k;

            k = ForgeCharge.Mul(cfg.damage, t, cfg);

            if (k != 1f)
            {
                Damage d = w.Damage;
                d.amount *= k;
                w.Damage = d;
            }

            k = ForgeCharge.Mul(cfg.explosion, t, cfg);

            if (k != 1f)
            {
                // Duplicate FIRST. Explosion is a struct but its damages list
                // is a reference, so editing the list off a plain copy would
                // permanently rewrite the weapon's own numbers - and the
                // restore below, which puts the struct back, would not undo it.
                Explosion ex = w.Explosion.Duplicate();
                ex.radius *= k;

                if (ex.damages != null)
                {
                    for (int i = 0; i < ex.damages.Count; i++)
                    {
                        ex.damages[i] = new Damage(
                            ex.damages[i].amount * k,
                            ex.damages[i].damageType);
                    }
                }

                w.Explosion = ex;
            }

            k = ForgeCharge.Mul(cfg.burn, t, cfg);

            if (k != 1f)
                w.Burn = new MinMaxFloat(w.Burn.Min * k, w.Burn.Max * k);

            k = ForgeCharge.Mul(cfg.push, t, cfg);

            if (k != 1f)
                w.PushForce *= k;

            k = ForgeCharge.Mul(cfg.spread, t, cfg);

            if (k != 1f)
                w.Spread *= k;

            var hw = w as HitscanWeapon;

            // Extra pellets are skipped on a beam on purpose. HitscanWeapon
            // builds one visual per projectileCount at equip time and
            // OnBarrelMoved indexes that list without a bounds check, so a
            // count raised behind its back is an exception waiting for the
            // next frame the trigger is down.
            int extra = ForgeCharge.Add(cfg.count, t, cfg);

            // A WEAPON AT projectileCount 0 FIRES NOTHING BY DESIGN, and a
            // charge must not resurrect it.
            //
            // Round 11 test 80: *"when i hold the fire button and let go it
            // shoots out more projectiles ... between 3 to 5 bullets"* on BULL
            // RUSH, which is `"projectileCount": 0` with `"lunge"` and
            // `"charge": { "count": 4 }`. `Max(1f, 0 + 4)` is 4, so the release
            // fired a four-pellet shotgun blast out of a weapon whose whole
            // point is that it fires no bullets at all. `projectileCount: 0` is
            // this mod's own spelling for a pure melee or lunge-only gun - the
            // swipe block documents it as `pureMelee` - so it has to survive a
            // charge, and the `Max(1f, ...)` floor was fighting it too.
            //
            // Reported at BUILD time as well (see ApplyCharge), because a
            // runtime-only guard leaves the file looking correct.
            if (extra != 0 && hw == null && sc.count > 0f)
                w.ProjectileCount = Mathf.Max(1f, w.ProjectileCount + extra);

            var pw = w as ProjectileWeapon;

            if (pw != null)
            {
                sc.pw = pw;
                sc.speed = pw.ProjectileSpeed;
                sc.range = pw.Range;

                k = ForgeCharge.Mul(cfg.speed, t, cfg);

                if (k != 1f)
                    pw.ProjectileSpeed *= k;

                k = ForgeCharge.Mul(cfg.range, t, cfg);

                if (k != 1f)
                    pw.Range *= k;
            }
            else if (hw != null)
            {
                sc.hw = hw;
                sc.beamRange = hw.Range;
                sc.rayWidth = hw.RayWidth;

                k = ForgeCharge.Mul(cfg.range, t, cfg);

                if (k != 1f)
                    hw.Range *= k;

                // "size" on a beam is the only thing it can mean: how thick
                // the damage cast is. The beam does not LOOK thicker - the
                // visual is one stretched quad the weapon rebuilds from its
                // own numbers - so this widens what it hits, not what you see.
                k = ForgeCharge.Mul(cfg.size, t, cfg);

                if (k != 1f)
                    hw.RayWidth *= k;
            }

            // Handed to the projectile hooks below, which run inside this same
            // synchronous window.
            ShotPending = true;
            ShotSize = (hw != null) ? 1f : ForgeCharge.Mul(cfg.size, t, cfg);
            ShotPierce = ForgeCharge.Add(cfg.pierce, t, cfg);
            ShotHasColor = cfg.hasColor;
            ShotColor = cfg.color;

            return sc;
        }

        private static void Restore(WeaponBase w, Scope sc)
        {
            ShotPending = false;
            ShotSize = 1f;
            ShotPierce = 0;
            ShotHasColor = false;

            w.Damage = sc.damage;
            w.Explosion = sc.explosion;
            w.Burn = sc.burn;
            w.PushForce = sc.push;
            w.ProjectileCount = sc.count;
            w.Spread = sc.spread;

            if (sc.pw != null)
            {
                sc.pw.ProjectileSpeed = sc.speed;
                sc.pw.Range = sc.range;
            }

            if (sc.hw != null)
            {
                sc.hw.Range = sc.beamRange;
                sc.hw.RayWidth = sc.rayWidth;
            }

            State s = sc.state;

            if (s != null && s.armedShots > 0)
            {
                s.armedShots--;

                if (s.armedShots == 0)
                {
                    s.armedLevel = 0f;
                    s.armedUntil = 0f;
                    _armed--;
                }
            }
        }

        // ---- the individual shot ----------------------------------------

        [HarmonyPatch(typeof(Projectile), "Shoot")]
        public class OnProjectileShoot
        {
            // A PREFIX, and that matters. ForgeGrowth captures the shot's
            // "normal" size in a Shoot POSTFIX and then rewrites localScale
            // from it every frame. Running first means a charged shot is
            // already big by the time growth measures it, so the two compose -
            // a charged shot starts bigger and grows from there - instead of
            // racing for the same field in whatever order Harmony picked.
            static void Prefix(Projectile __instance)
            {
                if (!ShotPending)
                    return;

                try
                {
                    if (ShotSize != 1f)
                    {
                        __instance.transform.localScale *= ShotSize;
                        __instance.Radius *= ShotSize;
                    }

                    if (ShotPierce > 0)
                        Pierce(__instance);

                    if (ShotHasColor)
                        // .root, not .gameObject: the build-time colouring
                        // (VisualCustomizer.Recolor) is applied to the whole
                        // prefab, and on several ammo prefabs the Projectile
                        // component sits on a CHILD with art beside it - so
                        // tinting only the component's own object would leave
                        // half the shot its normal colour.
                        VisualCustomizer.Tint(
                            __instance.transform.root.gameObject, ShotColor);
                }
                catch (Exception e)
                {
                    Log.LogError("Charged shot failed: " + e);
                }
            }
        }

        [HarmonyPatch(typeof(PhysicsProjectile), "Shoot")]
        public class OnPhysicsShoot
        {
            static void Prefix(PhysicsProjectile __instance)
            {
                if (!ShotPending)
                    return;

                // FireSingle calls Shoot() twice on a lobbed shot and the
                // first one is against a blank projectile, before Damage,
                // Velocity and Owner exist. Scaling that one would double the
                // size on the real one a moment later.
                if (ForgePhysicsShot.NotReady(__instance))
                    return;

                try
                {
                    // No Radius on this class - it collides with a real
                    // Collider2D, which scales with the transform, so the
                    // hitbox grows for free.
                    if (ShotSize != 1f)
                        __instance.transform.localScale *= ShotSize;

                    if (ShotHasColor)
                        // .root, not .gameObject: the build-time colouring
                        // (VisualCustomizer.Recolor) is applied to the whole
                        // prefab, and on several ammo prefabs the Projectile
                        // component sits on a CHILD with art beside it - so
                        // tinting only the component's own object would leave
                        // half the shot its normal colour.
                        VisualCustomizer.Tint(
                            __instance.transform.root.gameObject, ShotColor);
                }
                catch (Exception e)
                {
                    Log.LogError("Charged lobbed shot failed: " + e);
                }
            }
        }

        // Piercing is all-or-nothing in the base game - PiercingData has an
        // enabled flag and two repeat delays, and no hit count at all - so a
        // limit means the ForgePierceCap component the pierce feature already
        // uses. Raising it on the INSTANCE is right in both worlds: standalone
        // ForgePiercePatch counts it, and when ModuleForge is installed it
        // reads the same component and adds its own module caps on top.
        private static void Pierce(Projectile p)
        {
            PiercingData pd = p.PiercingData;

            if (!pd.enabled)
            {
                pd.enabled = true;

                // Never 0: that re-damages every frame the shot overlaps
                // something, which is not piercing, it is a blender.
                if (pd.damageRepeatDelay <= 0f)
                    pd.damageRepeatDelay = 0.3f;

                p.PiercingData = pd;
            }

            var cap = p.GetComponent<ForgePierceCap>();

            if (cap == null)
            {
                cap = p.gameObject.AddComponent<ForgePierceCap>();
                cap.limit = ShotPierce;
            }
            else
            {
                cap.limit += ShotPierce;
            }
        }
    }
}
