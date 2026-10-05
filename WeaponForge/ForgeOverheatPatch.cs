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
            ForgeLog.Source("WeaponForge.Heat");

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

                    // NOT `Add`. A trigger pull can ADD heat, DRAIN heat or do
                    // neither, and which of those it is belongs to the surge
                    // block rather than to this patch - `surge.fill` can take
                    // firing out of the set entirely, and `surge.drain` can put
                    // shots on the other side of the ledger. One owner.
                    ForgeHeat.For(__instance, cfg)
                        .OnShotFired(cfg.perShot * Mathf.Max(1, weapon.BurstSize));
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
            ForgeLog.Source("WeaponForge.Heat");

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

        // ---- the surge --------------------------------------------------
        //
        // The overload window: the meter blows and the gun gets BETTER as it
        // drains. It lives here rather than in a component of its own because
        // it is a phase of this meter - it needs `_heat`, `_locked` and the
        // blow, all of which are private, and "one owner per mechanic" is the
        // rule that has saved this project the most.
        public const string SurgeKey = "surge";

        private ForgeSurge.Config _surge;
        private bool _surging;
        private WeaponBase _surgeWeapon;   // whoever we last published to
        private int _surgePellets = -1;

        // EVERY LIVE METER, so the damage hook can find the one belonging to a
        // weapon without a `GetComponentsInChildren` per point of damage dealt
        // in the game. There are never more than a handful.
        private static readonly List<ForgeHeat> _all = new List<ForgeHeat>();

        public static ForgeHeat For(Shooter shooter, ForgeOverheat.Config cfg)
        {
            var heat = shooter.gameObject.GetComponent<ForgeHeat>();

            if (heat == null)
                heat = shooter.gameObject.AddComponent<ForgeHeat>();

            heat._shooter = shooter;
            heat._cfg = cfg;

            return heat;
        }

        // The meter belonging to this exact weapon, or null. Used by the surge
        // fill hook, which knows a `WeaponBase` and nothing else.
        public static ForgeHeat FindFor(WeaponBase weapon)
        {
            if (weapon == null)
                return null;

            for (int i = 0; i < _all.Count; i++)
            {
                ForgeHeat h = _all[i];

                if (h == null || h._cfg == null || h._shooter == null)
                    continue;

                if (ReferenceEquals(h._shooter.Weapon, weapon))
                    return h;
            }

            return null;
        }

        private void OnEnable()
        {
            if (!_all.Contains(this))
                _all.Add(this);
        }

        // True only while the blowout's own vent shot is being fired. See Vent.
        private bool _venting;

        public bool Venting { get { return _venting; } }

        // ONE TRIGGER PULL, and the surge decides what it means.
        //
        // Without a surge this is what it has always been: add `perShot`. With
        // one, a pull can add nothing (`fill` no longer lists firing), drain
        // (`drain` lists firing), or both (`refill` on) - and the arithmetic of
        // the last case is checked at build time, because an add that beats the
        // drain is a surge that never ends.
        public void OnShotFired(float perShot)
        {
            if (_cfg == null)
                return;

            if (_surge != null)
            {
                if (_surging)
                {
                    if (_surge.drainOnFire && _surge.drainPerShot > 0f)
                        Remove(_surge.drainPerShot);

                    if (_surge.refill && _surge.fillOnFire)
                        Add(perShot);

                    return;
                }

                if (!_surge.fillOnFire)
                    return;
            }

            Add(perShot);
        }

        public void Add(float amount)
        {
            // The vent shot must not charge heat. We are already at max, so
            // Blow() would re-enter itself from inside itself.
            if (_cfg == null || amount <= 0f || _venting)
                return;

            // NO REFILL DURING THE WINDOW unless the file asked for one. Note
            // it returns BEFORE stamping `_lastFiredAt`, which is deliberate:
            // that stamp is what `coolDelay` reads, and a surge whose drain
            // paused every time you pulled the trigger would last for as long
            // as you kept shooting.
            if (_surging && (_surge == null || !_surge.refill))
                return;

            _lastFiredAt = Time.time;
            _heat = Mathf.Min(_cfg.max, _heat + amount);

            // Repaint where the heat CHANGED, not only on the next frame's
            // tick. This is called from the Shoot postfix, and the muzzle flash
            // for that same shot is emitted in the same frame - so painting
            // here is what makes the flash you are looking at wear the heat
            // that shot just added, rather than the previous frame's.
            Paint();

            // `!_surging` matters only with `surge.refill` on, and without it
            // that setting is a bug rather than a feature: the meter is pinned
            // at `max` for the whole window, so every shot would re-enter
            // `Blow` - re-firing the vent shot, replaying the overheat sound,
            // shaking the camera and re-applying `selfDamage` once per pull.
            // The window is already open; topping the meter up is all a refill
            // is for.
            if (_heat >= _cfg.max && !_locked && !_surging)
                Blow();
        }

        // TAKE HEAT OFF, from outside. `onKill`'s `coolHeat` and Module Forge's
        // kill-reward modules both come through here.
        //
        // It does NOT unlock by itself, deliberately: `Tick` already decides a
        // vent is over when the heat is at or below `ventTo`, so dropping the
        // meter under that line ends the lock on the next frame through the one
        // path that knows how to recover (sound, repaint, rate restore). A
        // second unlock route here would be a second owner of that decision.
        public void Remove(float amount)
        {
            if (_cfg == null || amount <= 0f)
                return;

            _heat = Mathf.Max(0f, _heat - amount);
            Paint();
        }

        // Every heat meter on a unit, so a caller does not have to know how
        // many Shooters a ship has. `weapon` narrows it to the gun that scored
        // the kill; pass null for "whatever is hot".
        public static int CoolOn(Unit unit, WeaponBase weapon, float amount)
        {
            if (unit == null || amount <= 0f)
                return 0;

            ForgeHeat[] all = unit.GetComponentsInChildren<ForgeHeat>(true);
            int cooled = 0;

            for (int i = 0; i < all.Length; i++)
            {
                ForgeHeat h = all[i];

                if (h == null || h._cfg == null)
                    continue;

                if (weapon != null &&
                    (h._shooter == null ||
                     !ReferenceEquals(h._shooter.Weapon, weapon)))
                {
                    continue;
                }

                h.Remove(amount);
                cooled++;
            }

            return cooled;
        }

        // THE OTHER DIRECTION: a kill scored by SOMETHING ELSE, cooling every
        // meter on this unit whose own weapon opted in with
        // `onKill.coolHeatFrom: "any"`.
        //
        // `CoolOn` above cannot express this. It is told an amount and asked
        // which meter to apply it to; here the amount is a property of each
        // meter's own weapon, so the walk has to be the other way round - find
        // the meters, ask each one's weapon what it wanted.
        //
        // `credit` is the weapon that actually scored the kill, skipped because
        // the normal per-weapon payout has already handled it. Without that a
        // gun with both `coolHeat` and `coolHeatFrom: "any"` would cool twice
        // for its own kills.
        public static void CoolFromAnyKill(Unit unit, WeaponBase credit)
        {
            if (unit == null || !ForgeOnKill.Any)
                return;

            ForgeHeat[] all = unit.GetComponentsInChildren<ForgeHeat>(true);

            for (int i = 0; i < all.Length; i++)
            {
                ForgeHeat h = all[i];

                if (h == null || h._cfg == null || h._shooter == null)
                    continue;

                WeaponBase mine = h._shooter.Weapon;

                if (mine == null || ReferenceEquals(mine, credit))
                    continue;

                ForgeOnKill.Config cfg;

                if (!ForgeOnKill.TryGet(mine.TemplateData, out cfg))
                    continue;

                if (cfg == null || !cfg.coolFromAnyWeapon || cfg.coolHeat <= 0f)
                    continue;

                h.Remove(cfg.coolHeat);
            }
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

            // Which gun are we looking at? A cluster refresh builds a new
            // `WeaponBase` and a weapon swap is a different gun entirely, so
            // this re-resolves the surge config and hands back anything
            // published to the old one. It is above the guards for the same
            // reason the feedback is - and it has to be above `Paint`, which
            // asks whether a surge is running.
            ResolveWeapon();

            Paint();

            // THE FIRE-RATE PENALTY BELONGS UP HERE TOO, and it did not used to
            // be - it sat at the very bottom, below the cool-delay return.
            //
            // That return is taken on every frame you are firing, because
            // `_lastFiredAt` was just stamped. So `response: "slow"` only ever
            // recomputed its penalty once you STOPPED shooting, which is the
            // one moment sustained fire is not happening. On a held beam with
            // `perSecond` it could never run at all: `Add` stamps the clock
            // every frame the trigger is down.
            //
            // The same shape as `Paint` two lines above, found the same way,
            // and it is the house rule verbatim - **a pure read of state
            // belongs above every guard.** `Degrade` reads `Fraction()` and
            // publishes a multiplier; it has nothing to do with cooling and was
            // only ever there because `Cool()` is.
            Degrade();

            // Held weapons: charge by the second as well as by the shot, so a
            // beam or a hold-to-fire ring can overheat at all. `surge.fill` can
            // take firing out of the set, in which case this is not a source
            // either - it is the same "you pulled the trigger" question.
            if (_cfg.perSecond > 0f && !_locked && _shooter.IsShooting &&
                (_surge == null || _surge.fillOnFire))
            {
                Add(_cfg.perSecond * Time.deltaTime);
            }

            if (_locked)
            {
                // A SURGE RUNS THROUGH A JAM rather than starting after it.
                // That is his design and it is the interesting one: *"a fixed
                // lockout at the blow, then the surge for the rest of the
                // drain"* - a beat of vulnerability bought by a payoff. The
                // build log prints how much of the window the jam eats, because
                // nobody works that out from `jamSeconds` and `coolPerSecond`.
                if (_surging)
                {
                    SurgeTick();
                }
                else
                {
                    Cool();
                }

                bool done =
                    _cfg.response == ForgeOverheat.Jam
                        ? Time.time >= _unlockAt
                        : _heat <= Mathf.Max(0f, _cfg.ventTo);

                if (done)
                    Recover();

                return;
            }

            if (_surging)
            {
                // The window owns its own draining, and it ignores `coolDelay`
                // outright - a payout you could pause by holding the trigger
                // would be a permanent buff on any weapon with a refill.
                SurgeTick();
                return;
            }

            // The delay is what makes trigger discipline worth anything. With
            // cooling starting the instant you release, tapping is free and the
            // whole mechanic barely registers.
            if (Time.time - _lastFiredAt < _cfg.coolDelay)
                return;

            Cool();
        }

        // Which weapon are we publishing to? Same check `ForgeSpin.Tick` makes,
        // and for the same reason: a `WeaponBase` is rebuilt whenever its module
        // cluster changes, so "the weapon" is an instance rather than a slot.
        private void ResolveWeapon()
        {
            WeaponBase weapon = _shooter.Weapon;

            if (ReferenceEquals(weapon, _surgeWeapon))
                return;

            // Hand the old one back before forgetting it.
            WithdrawSurge();

            _surgeWeapon = weapon;
            _surging = false;
            _surge = null;

            if (weapon == null || weapon.TemplateData == null)
                return;

            ForgeSurge.Config found;

            if (ForgeSurge.TryGet(weapon.TemplateData, out found) &&
                found != null && found.enabled)
            {
                _surge = found;
            }
        }

        private void Cool()
        {
            if (_heat <= 0f)
                return;

            _heat = Mathf.Max(0f, _heat - _cfg.coolPerSecond * Time.deltaTime);
            Paint();
        }

        public float Fraction()
        {
            return _cfg.max > 0f ? Mathf.Clamp01(_heat / _cfg.max) : 0f;
        }

        // ---- the surge ----------------------------------------------------

        // Is the overload window open right now? Public for the `gauge` and
        // `bubble` readouts, and for the build-time diagnostics.
        public bool Surging { get { return _surging; } }

        // HOW MUCH OF THE WINDOW IS LEFT, 0..1, **after the ramp shape is
        // applied** - so a caller (the bar, the tint, the rewards) never has to
        // know which ramp this weapon uses.
        //
        //   fade  the remaining meter between `max` and `endAt`. Full at the
        //         instant it blows, nothing at the moment it closes. The
        //         literal inverse of spinUp, and his default.
        //   flat  1 for the whole window. The reward does not taper; it stops.
        public float SurgeAmount()
        {
            if (!_surging || _surge == null || _cfg == null)
                return 0f;

            if (_surge.ramp == ForgeSurge.Flat)
                return 1f;

            float end = Mathf.Clamp(_surge.endAt, 0f, _cfg.max);
            float span = Mathf.Max(0.01f, _cfg.max - end);

            return Mathf.Clamp01((_heat - end) / span);
        }

        private void StartSurge()
        {
            if (_surge == null || !_surge.enabled)
                return;

            // Re-blowing during a window (only possible with `refill` on) tops
            // the meter up rather than announcing a second surge - the sound
            // and the log line belong to the phase, not to the threshold.
            bool fresh = !_surging;

            _surging = true;

            if (fresh && !string.IsNullOrEmpty(_surge.startSfx))
                Sound(_surge.startSfx);
        }

        private void SurgeTick()
        {
            if (_surge == null)
            {
                _surging = false;
                return;
            }

            // The drain. `coolDelay` is deliberately not consulted - see Tick.
            if (_surge.drainOnCool && _heat > 0f)
            {
                _heat = Mathf.Max(
                    0f, _heat - _cfg.coolPerSecond * Time.deltaTime);
            }

            float end = Mathf.Clamp(_surge.endAt, 0f, _cfg.max);

            if (_heat <= end + 0.0001f)
            {
                EndSurge();
                return;
            }

            ApplySurge();
            Say();
        }

        private void EndSurge()
        {
            if (!_surging)
                return;

            _surging = false;

            WithdrawSurge();

            // TAKE THE READOUT DOWN. `Say` is driven per frame while the window
            // is open, so without this the status line would keep whatever
            // percentage it was last given for the rest of the run - a readout
            // frozen on a number is worse than no readout, because it reads as
            // a window that never closed.
            if (_surge != null && _surge.showSurge)
                ForgeBuffHud.Clear(this);

            if (_surge != null && !string.IsNullOrEmpty(_surge.endSfx))
                Sound(_surge.endSfx);

            // Repaint immediately: the colour is a phase indicator, and leaving
            // the surge colour up for a frame after the window shut would be
            // the readout lying about the one thing it exists to say.
            Paint();
        }

        // EVERY REWARD IS A PUBLISHED MULTIPLIER, never a write onto the weapon.
        // Same rule `spinUp` follows and the same reason: overheat's own `slow`
        // penalty, the onKill buff and Module Forge all write these fields, and
        // two features capturing one field cannot be ordered. A `response:
        // "slow"` weapon therefore surges and degrades at the same time and the
        // result is the PRODUCT, which is a real weapon rather than a bug - the
        // build log prints that number.
        private void ApplySurge()
        {
            WeaponBase weapon = _surgeWeapon;

            if (weapon == null)
                return;

            float f = SurgeAmount();

            if (f <= 0.0001f)
            {
                WithdrawSurge();
                return;
            }

            if (_surge.rewards.Publish(weapon, SurgeKey, f))
                Redress(weapon);
        }

        // CHANGING `ProjectileCount` AT RUNTIME IS COSMETIC ON A BULLET AND A
        // CRASH ON A BEAM: `HitscanWeapon.OnBarrelMoved` indexes
        // `visualsInstances[num]` with no bounds guard, every frame the barrel
        // moves. `InitializeVisuals` is public and resizes exactly that list.
        // Called on every change of the ROUNDED count in BOTH directions -
        // orphaned visuals left above the count keep their last state, which is
        // a beam frozen in mid-air with `Firing` stuck true. Lifted from
        // ForgeSpin.Redress, which is where this was learned.
        private void Redress(WeaponBase weapon)
        {
            if (weapon == null)
                return;

            int now = Mathf.RoundToInt(weapon.ProjectileCount);

            if (now == _surgePellets)
                return;

            _surgePellets = now;

            try
            {
                weapon.InitializeVisuals();
            }
            catch (Exception e)
            {
                Log.LogError("Rebuilding the weapon visuals failed: " + e);
            }
        }

        private void WithdrawSurge()
        {
            if (_surgeWeapon == null)
                return;

            ForgeWeaponStats.Clear(_surgeWeapon, SurgeKey);

            if (_surge != null && _surge.rewards.pellets && _surgePellets >= 0)
            {
                _surgePellets = -1;
                Redress(_surgeWeapon);
            }

            _surgePellets = -1;
        }

        // Every shot in the window is a charged shot. Asked by the surge patch
        // from the `Shooter.Shoot` prefix, because arming has to happen before
        // `DoShoot` reads it.
        public bool WantsChargedShot(out float level)
        {
            level = 1f;

            if (!_surging || _surge == null || !_surge.chargedShots)
                return false;

            level = Mathf.Clamp01(_surge.chargeLevel);
            return true;
        }

        // The surge fill hooks (`fill: "hits"` / `"damage"`) come in here.
        public void AddFromHit(float amount)
        {
            if (_cfg == null || amount <= 0f)
                return;

            Add(amount);
        }

        // Locked out - venting or jammed. Public for the `gauge` and `bubble`
        // readouts, which treat a lockout as the one state that overrides
        // every other rule about when to show themselves.
        public bool Locked { get { return _locked; } }

        // WHAT THE BAR SHOWS. Normally the heat. During a LOCKOUT it becomes a
        // countdown to getting the gun back, and **the two lockout responses
        // need genuinely different numbers** - which is the trap here, because
        // one implementation reading `Fraction()` looks correct, tests fine on
        // a vent weapon, and is quietly wrong on every jam one.
        //
        //   vent  unlocks when the heat falls to `ventTo`. Cool() runs during
        //         the lockout, so the meter really is draining toward the
        //         unlock - but it unlocks at `ventTo`, not at zero, so a raw
        //         fraction would leave the bar sitting ~20% full at the exact
        //         moment you regain the gun. Remapped so EMPTY MEANS FIRE NOW.
        //   jam   unlocks on a fixed TIMER while the gun cools on its own
        //         unrelated schedule. Heat is simply the wrong number; this
        //         reads the timer.
        public float Recovery()
        {
            if (_cfg == null)
                return 0f;

            if (!_locked)
                return Fraction();

            if (_cfg.response == ForgeOverheat.Jam)
            {
                return Mathf.Clamp01(
                    (_unlockAt - Time.time) /
                    Mathf.Max(0.01f, _cfg.jamSeconds));
            }

            float vent = Mathf.Max(0f, _cfg.ventTo);

            return Mathf.Clamp01(
                (_heat - vent) / Mathf.Max(0.01f, _cfg.max - vent));
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

            // THE WINDOW OPENS HERE - at the threshold, before the lockout,
            // because with `response: "jam"` the two overlap on purpose and the
            // surge has to be running for the jam to eat into it.
            StartSurge();

            _locked = _cfg.response != ForgeOverheat.Slow &&
                      _cfg.response != ForgeOverheat.None;

            if (_locked)
            {
                _unlockAt = Time.time + Mathf.Max(0.05f, _cfg.jamSeconds);

                _shooter.Block(this);

                // MANDATORY, and easy to miss. Shooter.Update returns as soon
                // as anything is blocking, and everything that tells a weapon
                // it has stopped firing lives after that return - the sound
                // loop, the trigger flag, and (R15 test 188) the
                // `OnBarrelMoved` call that is the only thing in the game that
                // ever clears a BEAM's visual. Clearing the flag here without
                // that call left a vented beam drawing and sparking at the spot
                // it cut out, while the ship flew away from it.
                ForgeShooterQuiet.Silence(_shooter);
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
            bool surgeTint =
                _surging && _surge != null && _surge.tintMuzzle;

            if (!_cfg.tintMuzzle && !surgeTint)
                return;

            WeaponBase weapon = _shooter.Weapon;

            if (!ReferenceEquals(weapon, _tintWeapon))
            {
                RestoreMuzzle();
                _tintWeapon = weapon;
            }

            if (weapon == null)
                return;

            float amount = Fraction() * _cfg.tintStrength;
            Color colour = _cfg.hotColor;

            // ★ DURING THE WINDOW THE COLOUR IS SWAPPED, NOT LAYERED ON TOP.
            //
            // The readout problem this solves is specific: a heat bar filling
            // and a heat bar draining look **identical**, so the one thing the
            // player cannot see is which phase they are in - and that is the
            // only thing that matters once a surge exists. A second tint layer
            // would blend with the orange into something that says neither.
            // Publishing the surge colour on the SAME layer makes the phase
            // unmistakable at a glance, and it costs nothing: one key, one
            // captured base, and `ForgeMuzzleTint`'s weighted mix still gets to
            // compose this with `spinUp`.
            //
            // The strength follows the ramp, so a `fade` surge visibly bleeds
            // out with the reward while a `flat` one holds and then snaps off.
            if (surgeTint)
            {
                colour = _surge.color;
                amount = SurgeAmount() * _surge.tintStrength;
            }

            ForgeMuzzleTint.Set(
                weapon, ForgeMuzzleTint.Heat, colour, amount);

            // AND THE BEAM ITSELF, on a hitscan weapon. R15 test 188: *"the
            // muzzleflare changes to red not the beam or the beam effects
            // hitting the target"* - true, and on a beam the muzzle is a small
            // puff at the barrel with the whole weapon coming out of it. Set is
            // a no-op on anything that is not a HitscanWeapon, so this needs no
            // branch here.
            ForgeBeamTint.Set(
                weapon, ForgeBeamTint.Heat, _cfg.hotColor, amount);
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
            ForgeBeamTint.Clear(_tintWeapon, ForgeBeamTint.Heat);
        }

        private void Say()
        {
            // THE SURGE GETS ITS OWN SWITCH, and it is asked FIRST. A surging
            // weapon reporting "HEAT 84%" would be telling the truth about the
            // meter and the wrong thing about the gun - the number is falling
            // and the gun is at its best, which reads as the exact opposite of
            // what is happening.
            if (_surging && _surge != null && _surge.showSurge)
            {
                ForgeBuffHud.ShowState(
                    this,
                    _surge.color,
                    "SURGE " + Mathf.RoundToInt(SurgeAmount() * 100f) + "%",
                    ForgeBuffHud.Kind.Buff);

                return;
            }

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
            _all.Remove(this);
            Clear();
        }

        private void OnDestroy()
        {
            _all.Remove(this);
            Clear();
        }

        // Never leave a weapon blocked, and never leave it carrying a bonus it
        // has not earned. A ship that dies mid-jam, or a Shooter disabled by a
        // screen change, would otherwise come back unable to fire - or
        // permanently surging - with nothing in the log to explain it.
        private void Clear()
        {
            try
            {
                RestoreRate();
                RestoreMuzzle();

                if (_surging && _surge != null && _surge.showSurge)
                    ForgeBuffHud.Clear(this);

                _surging = false;
                WithdrawSurge();

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
