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
            BepInEx.Logging.Logger.CreateLogSource("WeaponForge.Slow");

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

            public bool hasTint;             // off by default, on request
            public Color tint = Color.white;

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

        public static bool Any { get { return _weapons.Count > 0; } }

        // ---- applying the debuff -----------------------------------------
        public static void Apply(Unit victim, Config cfg)
        {
            if (victim == null || cfg == null || !cfg.hasSlow)
                return;

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
            BepInEx.Logging.Logger.CreateLogSource("WeaponForge.Slow");

        private Rigidbody2D _rb;
        private Shooter _shooter;
        private WeaponBase _weapon;

        private float _damping;
        private float _angularDamping;
        private float _mass;
        private float _fireRate;

        private ForgeSlow.Config _cfg;
        private int _stacks = 1;
        private float _endsAt;
        private bool _applied;
        private float _lastHitAt = -999f;

        private readonly List<SpriteRenderer> _tinted =
            new List<SpriteRenderer>();
        private readonly List<Color> _tintWas = new List<Color>();

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

            if (_shooter != null && _shooter.Weapon != null)
            {
                _weapon = _shooter.Weapon;
                _fireRate = _weapon.FireRate;
            }

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

            if (_cfg.slowFire && _weapon != null)
                _weapon.FireRate = _fireRate * f;

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

                // Only if it is still the SAME weapon object. A cluster
                // refresh builds a new WeaponBase, and writing our remembered
                // rate onto that one would be writing a stale number over a
                // fresh weapon.
                if (_cfg.slowFire && _weapon != null && _shooter != null &&
                    ReferenceEquals(_shooter.Weapon, _weapon))
                {
                    _weapon.FireRate = _fireRate;
                }

                Untint();
            }
            catch (Exception e)
            {
                Log.LogError("Un-slowing a unit failed: " + e);
            }
        }

        // Scoped to the prefab's Visual subtree for the same reason the minion
        // recolour is: a unit's AI state indicators are SpriteRenderers too and
        // are deliberately colour-coded, so tinting everything would paint over
        // the AI's own tells.
        private void Tint()
        {
            Transform visual = ForgeMinionLook.FindVisual(transform);

            if (visual == null)
                return;

            foreach (SpriteRenderer sr in
                     visual.GetComponentsInChildren<SpriteRenderer>(true))
            {
                if (sr == null)
                    continue;

                _tinted.Add(sr);
                _tintWas.Add(sr.color);

                Color had = sr.color;

                sr.color = new Color(
                    had.r * _cfg.tint.r,
                    had.g * _cfg.tint.g,
                    had.b * _cfg.tint.b,
                    had.a);
            }
        }

        private void Untint()
        {
            for (int i = 0; i < _tinted.Count; i++)
            {
                if (_tinted[i] != null)
                    _tinted[i].color = _tintWas[i];
            }

            _tinted.Clear();
            _tintWas.Clear();
        }
    }
}
