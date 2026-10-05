using System;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;

namespace WeaponForge
{
    // Two separate things that both answer "make the moment of impact land":
    //
    //   "timeSlow"  bends time for EVERYTHING, you included - a hit-stop punch.
    //   "slow"      is a debuff on the thing you hit; you stay at full speed.
    //
    // They share no mechanism at all, which is why they are separate keys.
    //
    // WHY THE ENEMY SLOW DOES NOT TOUCH THE MOVEMENT COMPONENTS, which is the
    // obvious implementation and a trap. A unit carries one UnitMovement per AI
    // state, and `MovementAction.OnEnable` SNAPSHOTS that component's current
    // values while `OnDisable` restores the snapshot wholesale. So a slow
    // written into `pushForce` mid-state has two failure modes: it is reverted
    // the moment the AI changes state, and - worse - if a state happens to
    // begin while the unit is slowed, the slowed numbers become that state's
    // "original" and get restored forever after. A temporary debuff would
    // become permanent, at random, depending on AI timing.
    //
    // So everything here is applied to the RIGIDBODY instead, which nothing
    // snapshots:
    //
    //   move speed   linearDamping   terminal speed under a constant force is
    //                                force/damping, so damping/amount gives
    //                                exactly `amount` times the speed
    //   turn speed   angularDamping  the same trick for AddTorque
    //   knockback    mass            PushMovement multiplies its OWN force by
    //                                mass, so lowering mass leaves a unit's
    //                                self-propulsion untouched while making
    //                                every EXTERNAL shove - your shots, your
    //                                explosions - move it further
    //
    // and the fire rate goes on the weapon instance, which is per-unit.
    //
    // This is also the game's own idiom for slowing something down:
    // `DragCellBehaviourTarget.FixedUpdate` is
    // `rigidbody.linearVelocity *= 1f - drag`.
    public static class ForgeSlow
    {
        private static readonly ManualLogSource Log =
            ForgeLog.Source("WeaponForge.Slow");

        // How a second hit on an already-slowed target behaves.
        public const int Refresh = 0;   // reset the timer (default)
        public const int Stack = 1;     // slow harder, up to a floor
        public const int Ignore = 2;    // first hit wins until it expires

        public static int ParseMode(string s)
        {
            switch ((s ?? "").Trim().ToLowerInvariant())
            {
                case "stack":
                case "stacking":
                    return Stack;
                case "ignore":
                case "first":
                case "once":
                    return Ignore;
                default:
                    return Refresh;
            }
        }

        public static string ModeWord(int mode)
        {
            switch (mode)
            {
                case Stack: return "stack";
                case Ignore: return "ignore repeats";
                default: return "refresh";
            }
        }

        // ---- what a weapon asks for --------------------------------------
        public class Config
        {
            // ---- the debuff on what you hit ----
            public bool hasSlow;
            public float amount = 0.5f;      // target fraction of normal speed
            public float duration = 2f;
            public int mode = Refresh;
            public float cooldown;           // 0 = none, which is the default
            public int maxStacks = 3;
            public float floor = 0.2f;       // strongest a stack can ever get

            public bool slowMove = true;
            public bool slowTurn = true;
            public bool slowFire = true;
            public bool slowKnockback = true;

            // Shots a slowed enemy fires come out slow too.
            //
            // `slowFire` already halves how OFTEN it shoots; this is how fast
            // the bullets travel, which is the half you can actually see and
            // dodge. Separate toggles because they are separately useful: a
            // sniper that fires at a normal cadence but whose shots crawl is a
            // different weapon from one that fires half as often at full speed.
            public bool slowShots = true;

            public bool hasTint;             // off by default, on request
            public Color tint = Color.white;

            // See ForgeUnitTint - 0..1, 1 is the full colour (today's look).
            public float tintStrength = 1f;

            // multiply (the default, and what every existing file was tuned
            // against) or replace, which moves the sprite TOWARD the colour so
            // it can brighten a dark enemy. See ForgeUnitTint.
            public int tintMode = ForgeUnitTint.Multiply;

            // ---- bending time itself ----
            public bool hasTimeSlow;
            public float timeScale = 0.35f;
            public float timeDuration = 0.25f;
            public float timeCooldown;       // 0 = none, his chosen default
            public int timeMode = Refresh;
            public bool timeEase = true;     // ride back up instead of snapping
            public bool timeUnscaled = true; // duration in REAL seconds

            // Triggers. Hitting is the default; killing is opt-in.
            public bool onHit = true;
            public bool onKill;

            [NonSerialized] public float lastTimeSlowAt = -999f;
        }

        private static readonly Dictionary<WeaponData, Config> _weapons =
            new Dictionary<WeaponData, Config>();

        public static void Register(WeaponData weapon, Config config)
        {
            if (weapon != null && config != null)
                _weapons[weapon] = config;
        }

        public static bool TryGet(WeaponData weapon, out Config config)
        {
            config = null;
            return weapon != null && _weapons.TryGetValue(weapon, out config);
        }

        // ★ A GRANTED SLOW HAS TO FLIP THE GATE, OR HALF THE FEATURE IS MISSING
        // IN EXACTLY ONE INSTALLATION.
        //
        // `Any` used to be `_weapons.Count > 0` - "some weapon FILE asked for a
        // slow". Module Forge's slow modules reach `Apply` directly (it owns
        // the rigidbody when both mods are installed, because two engines each
        // capturing a unit's original speed makes the debuff permanent), and
        // that path touches no weapon file at all. So with this mod installed
        // and no weapon carrying a `slow` block, `ForgeSlowShotPatch` returned
        // on this gate and a module-granted slow moved the enemy slowly while
        // its bullets came out at full speed - working, and visibly missing a
        // half nobody could account for.
        //
        // `ForgeCrit` has had the answer since the day it shipped: `SetBonus`
        // sets `Any = true` beside `Register`. **The gate on a shared mechanic
        // belongs to every way in, not to the one its author happened to build
        // first.** Reset per run with the rest of the per-run state.
        private static bool _granted;

        public static bool Any { get { return _weapons.Count > 0 || _granted; } }

        // ---- applying the debuff -----------------------------------------
        public static void Apply(Unit victim, Config cfg)
        {
            if (victim == null || cfg == null || !cfg.hasSlow)
                return;

            // Set here rather than at the module's registration, because this
            // is the public door the other mod comes through and it is the only
            // one that cannot be bypassed. See the note on `Any`.
            _granted = true;

            try
            {
                var live = victim.gameObject.GetComponent<ForgeSlowed>();

                if (live == null)
                {
                    live = victim.gameObject.AddComponent<ForgeSlowed>();
                    live.Begin(victim, cfg);
                    return;
                }

                live.Again(cfg);
            }
            catch (Exception e)
            {
                Log.LogError("Slowing a unit failed: " + e);
            }
        }

        // ---- bending time -------------------------------------------------
        public static void BendTime(Config cfg)
        {
            if (cfg == null || !cfg.hasTimeSlow)
                return;

            try
            {
                if (cfg.timeCooldown > 0f &&
                    Time.unscaledTime - cfg.lastTimeSlowAt < cfg.timeCooldown)
                {
                    return;
                }

                // Ignore-repeats: while a previous one is still running, do
                // nothing at all rather than laying another on top.
                if (cfg.timeMode == Ignore &&
                    Time.unscaledTime - cfg.lastTimeSlowAt < cfg.timeDuration)
                {
                    return;
                }

                TimeManager time;

                if (!ServiceLocator.TryGet<TimeManager>(out time) || time == null)
                    return;

                var modifier = new TimeManager.TimeScaleModifier();

                modifier.timeScale = cfg.timeScale;

                // DURATION 0 MEANS FOREVER. `TimeScaleModifier.IsIndefinit` is
                // `duration <= 0f`, and an indefinite modifier is never
                // evaluated for expiry - it just sits in the list holding the
                // game in slow motion until something calls RemoveAllModifiers.
                // The builder refuses a non-positive duration, and this is the
                // second belt.
                modifier.duration = Mathf.Max(0.01f, cfg.timeDuration);

                // UNSCALED OR THE SLOW EXTENDS ITSELF. With unscaled off the
                // elapsed check uses Time.time, which is what we just slowed
                // down - so a 0.25s slow at scale 0.2 would last 1.25 real
                // seconds, and a heavy slow would feel like it had stuck.
                modifier.unscaled = cfg.timeUnscaled;

                if (cfg.timeEase)
                {
                    // Ride back up to normal across the duration rather than
                    // snapping, which is most of what makes hit-stop feel good.
                    modifier.useCurve = true;
                    modifier.curve =
                        AnimationCurve.EaseInOut(
                            0f, cfg.timeScale, 1f, 1f);
                }

                time.AddModifier(modifier, TimeOwner);
                cfg.lastTimeSlowAt = Time.unscaledTime;
            }
            catch (Exception e)
            {
                Log.LogError("Bending time failed: " + e);
            }
        }

        // One shared owner object, so a future "cancel all of ours" is one
        // call. The game keys modifiers by owner and nothing else.
        public static readonly object TimeOwner = new object();

        public static void ClearTime()
        {
            try
            {
                TimeManager time;

                if (ServiceLocator.TryGet<TimeManager>(out time) && time != null)
                    time.RemoveAllModifiers(TimeOwner);
            }
            catch (Exception)
            {
            }
        }

        // Run entry. `_weapons` is deliberately NOT cleared - those configs are
        // registered once at startup from the weapon files, and dropping them
        // would unbuild every slow weapon for the rest of the session, which is
        // the trap `ForgeCrit.Reset` documents. Only the per-run state goes:
        // the live time modifiers, and the "a module granted a slow" gate.
        public static void Reset()
        {
            _granted = false;
            ClearTime();
        }
    }

    // The live debuff, riding on the unit it slowed.
    //
    // Everything it changes is stored as an ORIGINAL on the way in and written
    // back on the way out, and every re-application recomputes from that
    // original rather than compounding - so a hundred hits cannot drift the
    // numbers, and expiry always lands exactly where the unit started.
    public class ForgeSlowed : MonoBehaviour
    {
        private static readonly ManualLogSource Log =
            ForgeLog.Source("WeaponForge.Slow");

        private Rigidbody2D _rb;
        private Shooter _shooter;
        private WeaponBase _weapon;

        private float _damping;
        private float _angularDamping;
        private float _mass;

        private ForgeSlow.Config _cfg;
        private int _stacks = 1;
        private float _endsAt;
        private bool _applied;
        private float _lastHitAt = -999f;

        public void Begin(Unit victim, ForgeSlow.Config cfg)
        {
            _cfg = cfg;
            _stacks = 1;
            _endsAt = Time.time + cfg.duration;
            _lastHitAt = Time.time;

            _rb = victim.GetComponent<Rigidbody2D>();

            if (_rb == null)
                _rb = victim.GetComponentInChildren<Rigidbody2D>();

            _shooter = victim.GetComponentInChildren<Shooter>(true);

            if (_rb != null)
            {
                _damping = _rb.linearDamping;
                _angularDamping = _rb.angularDamping;
                _mass = _rb.mass;
            }

            // No `_fireRate` capture any more - ForgeWeaponStats owns the
            // baseline, and it captures once per weapon rather than once per
            // debuff. That is the part that a second slow landing on the same
            // enemy used to get wrong in principle.
            if (_shooter != null && _shooter.Weapon != null)
                _weapon = _shooter.Weapon;

            Push();
        }

        public void Again(ForgeSlow.Config cfg)
        {
            // A different weapon's slow taking over is fine; the stronger
            // settings simply become the live ones.
            _cfg = cfg;

            if (cfg.cooldown > 0f && Time.time - _lastHitAt < cfg.cooldown)
                return;

            _lastHitAt = Time.time;

            if (cfg.mode == ForgeSlow.Ignore)
                return;

            if (cfg.mode == ForgeSlow.Stack)
                _stacks = Mathf.Min(_stacks + 1, Mathf.Max(1, cfg.maxStacks));

            _endsAt = Time.time + cfg.duration;
            Push();
        }

        // How slow is this unit right now, as a fraction of normal? 1 means
        // "not slowed", which is also the answer for a unit with no debuff.
        //
        // Exists so the projectile hook can ask a question of the SHOOTER
        // rather than keep its own bookkeeping. `Apply` puts the component on
        // the Unit's own GameObject, and `Projectile.Owner` is that Unit, so
        // this is a single GetComponent on a path that already has one.
        public static float FactorFor(Unit unit)
        {
            if (unit == null)
                return 1f;

            var live = unit.gameObject.GetComponent<ForgeSlowed>();

            if (live == null || live._cfg == null || !live._cfg.slowShots)
                return 1f;

            // Expired but not yet cleaned up - Update removes it, and a shot
            // fired in between should not be slowed.
            if (Time.time >= live._endsAt)
                return 1f;

            return live.Factor();
        }

        // The one factor everything is derived from. Recomputed from scratch
        // every time, never accumulated.
        private float Factor()
        {
            float f = Mathf.Clamp(_cfg.amount, 0.01f, 1f);

            if (_cfg.mode == ForgeSlow.Stack && _stacks > 1)
                f = Mathf.Pow(f, _stacks);

            return Mathf.Max(f, Mathf.Clamp(_cfg.floor, 0.01f, 1f));
        }

        private void Push()
        {
            float f = Factor();

            if (_rb != null)
            {
                // Terminal speed under a constant force is force/damping, so
                // dividing damping by the factor multiplies the speed by it.
                // A unit with no damping at all has no terminal speed to
                // lower, so give it some.
                if (_cfg.slowMove)
                {
                    float baseline = _damping > 0.01f ? _damping : 1f;
                    _rb.linearDamping = baseline / f;
                }

                if (_cfg.slowTurn)
                {
                    float baseline =
                        _angularDamping > 0.01f ? _angularDamping : 1f;
                    _rb.angularDamping = baseline / f;
                }

                // Lighter, so YOUR shots and blasts throw it further. Its own
                // locomotion is unaffected: PushMovement multiplies its force
                // by mass, so that cancels out exactly.
                if (_cfg.slowKnockback)
                    _rb.mass = Mathf.Max(0.05f, _mass * f);
            }

            // PUBLISHED, NOT ASSIGNED. This was the third feature writing
            // `FireRate` from its own captured base; it only ever touches an
            // ENEMY'S weapon, so it never collided in play the way overheat and
            // the onKill buff did - but leaving one direct writer standing means
            // the next feature gets copied from the wrong example, which is how
            // this project's bugs have historically spread. See
            // ForgeWeaponStats.
            if (_cfg.slowFire && _weapon != null)
            {
                ForgeWeaponStats.Set(
                    _weapon, ForgeWeaponStats.FireRate, "slow", f);
            }

            if (_cfg.hasTint && !_applied)
                Tint();

            _applied = true;
        }

        private void Update()
        {
            if (Time.time < _endsAt)
                return;

            Restore();
            Destroy(this);
        }

        private void OnDestroy()
        {
            Restore();
        }

        private void Restore()
        {
            if (!_applied)
                return;

            _applied = false;

            try
            {
                if (_rb != null)
                {
                    if (_cfg.slowMove)
                        _rb.linearDamping = _damping;

                    if (_cfg.slowTurn)
                        _rb.angularDamping = _angularDamping;

                    if (_cfg.slowKnockback)
                        _rb.mass = _mass;
                }

                // The weapon-identity check this used to do by hand is free
                // now: the stack keys on the `WeaponBase` instance, so a
                // cluster refresh is a key it has never seen and a withdrawal
                // against the old one cannot reach the new gun.
                if (_cfg.slowFire && _weapon != null)
                {
                    ForgeWeaponStats.Clear(
                        _weapon, ForgeWeaponStats.FireRate, "slow");
                }

                Untint();
            }
            catch (Exception e)
            {
                Log.LogError("Un-slowing a unit failed: " + e);
            }
        }

        // GOES THROUGH ForgeUnitTint RATHER THAN PAINTING DIRECTLY, and the
        // change is not cosmetic. This used to remember each `sr.color` and
        // write it back on expiry, which is correct for exactly one tint at a
        // time: a second feature tinting the same unit would remember the
        // ALREADY-SLOWED colour and bake the blue in permanently when it
        // expired. That is precisely why the slam tint could not be built
        // until there was one owner. See ForgeUnitTint for the full shape.
        //
        // The scoping and the alpha rule now live there too, so there is one
        // place that knows which renderers a tint may touch.
        private void Tint()
        {
            Unit unit = GetComponent<Unit>();

            if (unit == null)
                unit = GetComponentInParent<Unit>();

            // Duration is left open and cleared on Restore, rather than timed
            // here: a slow can be refreshed or re-stacked, and a layer that
            // expired on its own schedule would flicker off mid-debuff.
            ForgeUnitTint.Set(
                unit, ForgeUnitTint.Slow, _cfg.tint, 0f, 0f,
                _cfg.tintStrength, _cfg.tintMode);
        }

        private void Untint()
        {
            Unit unit = GetComponent<Unit>();

            if (unit == null)
                unit = GetComponentInParent<Unit>();

            ForgeUnitTint.Clear(unit, ForgeUnitTint.Slow);
        }
    }
}
