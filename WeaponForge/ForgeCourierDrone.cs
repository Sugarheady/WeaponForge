using System;
using System.Collections.Generic;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace WeaponForge
{
    // ONE COURIER DRONE, start to finish (see ForgeCourier's header):
    //
    //   Waiting  - a burst's later drones leave at the gun's burstDelay
    //   Flying   - from the barrel to where the cursor was (or `appear`)
    //   Winding  - facing its aim; a BEAM shows its warm-up (the Gaster tell)
    //   Firing   - `shots` times, `interval` apart; a beam holds `beamTime`
    //   Leaving  - fades, or is pushed back while it fades (`recoil`)
    //
    // It is a plain GameObject the mod made at runtime, never a prefab, so it
    // holds its Config directly (the ForgeConfigTable trap is about prefabs).
    //
    // ★ ITS BODY, THREE WAYS (his answers): untouchable (no collider - your
    // own shots cannot reach it and enemy fire passes through); `hit` (a
    // trigger circle on the Player layer carrying the game's three hit
    // interfaces, so enemy bullets, lasers and explosions that reach it
    // damage it - and it BLOCKS them); `decoy` (a real game unit, the stock
    // drone, which enemy AI chooses to aim at; kept out of saves).
    public class ForgeCourierDrone : MonoBehaviour,
        IProjectileListener, IHitscanWeaponListener, IExplosionListener
    {
        private static readonly ManualLogSource Log =
            ForgeLog.Source("WeaponForge.Courier");

        private enum State { Waiting, Flying, Appearing, Winding, Firing, Leaving }

        private static bool _saidDecoyFail;
        private static bool _saidDecoyHealth;
        private static bool _saidNoNamed;

        // ---- set by Begin ----------------------------------------------------
        private ForgeCourier.Config _cfg;
        private WeaponBase _launched;
        private WeaponData _data;
        private IBarrel _barrel;
        private Unit _owner;
        private bool _ring;
        private int _index;
        private int _count;
        private bool _first;

        private State _state = State.Waiting;
        private float _stateAt;
        private float _leaveAt;

        // ---- the trip ----------------------------------------------------------
        private Vector2 _origin;
        private Vector2 _target;
        private Vector2 _centre;
        // Where this drone sits from the centre: its place on the ring, or on
        // the firing line of a boosted pull, plus a wingman's own offset.
        private Vector2 _ringOffset;
        private Vector2 _shift;
        private Vector2 _launchDir = Vector2.right;
        private float _reach;

        // ---- firing ------------------------------------------------------------
        private int _shot;
        private float _nextShot;
        private Vector2 _lastDir = Vector2.right;
        private readonly List<Vector2> _dirs = new List<Vector2>();

        // A beam: one copy per direction, held for beamTime.
        private readonly List<HitscanWeapon> _beams = new List<HitscanWeapon>();
        private bool _beam;
        private float _beamUntil;
        private float _nextTick;
        private int _hum = -1;
        private int _warm = -1;

        // ---- the body ----------------------------------------------------------
        private GameObject _art;
        private Transform _head;
        private Unit _decoy;
        private BarrelTransform _decoyBarrel;
        private Rigidbody2D _decoyBody;
        private float _health;
        private float _flashUntil;

        private readonly List<SpriteRenderer> _renderers = new List<SpriteRenderer>();
        private readonly List<Color> _bases = new List<Color>();
        private float _alpha = 1f;

        private readonly Dictionary<HealthBase, float> _touched =
            new Dictionary<HealthBase, float>();
        private readonly List<Collider2D> _hits = new List<Collider2D>();
        private ContactFilter2D _contactFilter;
        private bool _contactReady;

        private static readonly List<Unit> _enemies = new List<Unit>();

        public Unit Owner { get { return _owner; } }

        public bool IsDecoy
        {
            get { return _cfg != null && _cfg.shootable == ForgeCourierRules.Shootable.Decoy; }
        }

        // Still on its way or working - what the per-ship backstop counts.
        public bool Counts { get { return _state != State.Leaving; } }

        // ======================================================================
        // Start
        // ======================================================================

        public void Begin(
            ForgeCourier.Config cfg, WeaponBase gun, IBarrel barrel, Unit owner,
            float delay, bool ring, int index, int count, bool first, Vector2 shift)
        {
            _cfg = cfg;
            _launched = gun;
            _data = gun.TemplateData;
            _barrel = barrel;
            _owner = owner;
            _ring = ring;
            _index = index;
            _count = count;
            _first = first;
            _shift = shift;

            _reach = ForgeCourier.ReachOf(gun, cfg);
            _leaveAt = Time.time + Mathf.Max(0f, delay);

            // Nothing to see until it leaves the barrel.
            _state = State.Waiting;

            if (delay <= 0f)
                Leave0();
        }

        // The moment it leaves the barrel: where from, where to, its body.
        private void Leave0()
        {
            Vector2 barrelPos = BarrelPosition();
            Vector2 barrelDir = BarrelDirection();

            _origin = barrelPos;
            _centre = Destination(barrelPos, barrelDir);

            if (_ring)
            {
                float radius = ForgeBloom.RingRadius(
                    _launched.Spread, Vector2.Distance(_origin, _centre));

                _ringOffset = ForgeBloom.RingOffset(
                    _index, _count, radius, _launched.AngleOffset,
                    _launched.AngleVariance);
            }
            else if (_count > 1)
            {
                // A BOOSTED pull: side by side across your aim (his answer) -
                // square to the line from the ship to the cursor.
                Vector2 ship = (_owner != null) ? (Vector2)_owner.transform.position : _origin;
                Vector2 aim = _centre - ship;

                if (aim.sqrMagnitude < 0.0001f)
                    aim = barrelDir;

                double ox, oy;

                ForgeCourierRules.LineOffset(
                    _index, _count, Mathf.Max(0f, _cfg.spacing), aim.x, aim.y, out ox, out oy);

                _ringOffset = new Vector2((float)ox, (float)oy);
            }

            _ringOffset += _shift;
            _target = _centre + _ringOffset;

            Vector2 to = _target - _origin;
            _launchDir = (to.sqrMagnitude > 0.0001f) ? to.normalized : barrelDir;
            _lastDir = _launchDir;

            bool appear = _cfg.entry == ForgeCourierRules.Entry.Appear;

            transform.position = appear ? (Vector3)_target : (Vector3)_origin;

            BuildBody();

            if (_first)
            {
                if (!string.IsNullOrEmpty(_cfg.launchSfx))
                {
                    try { AudioManager.PlaySfx(_cfg.launchSfx, _origin); }
                    catch (Exception) { }
                }

                // Wingmen fire with a courier gun's LAUNCH, not with each drone
                // shot (the mirror stands down while a drone fires).
                try { ForgeWingmanPatch.Mirror(_launched); }
                catch (Exception) { }
            }

            if (appear)
            {
                _alpha = 0f;
                Enter(State.Appearing);
            }
            else
            {
                Enter(State.Flying);
            }
        }

        private Vector2 BarrelPosition()
        {
            var c = _barrel as Component;

            if (_barrel != null && (!(_barrel is Component) || c != null))
                return _barrel.Position;

            return (_owner != null) ? (Vector2)_owner.transform.position : (Vector2)transform.position;
        }

        private Vector2 BarrelDirection()
        {
            var c = _barrel as Component;

            if (_barrel != null && (!(_barrel is Component) || c != null))
            {
                Vector2 d = _barrel.Direction;

                if (d.sqrMagnitude > 0.0001f)
                    return d.normalized;
            }

            return Vector2.right;
        }

        // Where it is going: the cursor clamped to its reach; on a GAMEPAD, the
        // first enemy along the stick within reach, else the full reach (his
        // answer); with no cursor at all (an enemy or minion carrying one),
        // straight ahead to the reach.
        //
        // ★ THE REACH IS MEASURED FROM THE SHIP, not the barrel, because the
        // reach ring is drawn round the ship: the circle on screen has to be
        // exactly where a drone can go, not half a barrel's length off it.
        // The drone still LEAVES from the barrel.
        private Vector2 Destination(Vector2 from, Vector2 barrelDir)
        {
            Vector2 cursor;
            bool has = ForgeVolley.TryCursor(_owner, out cursor);
            Vector2 ship = (_owner != null) ? (Vector2)_owner.transform.position : from;

            if (has && UsesGamepad())
            {
                Vector2 dir = cursor - ship;

                if (dir.sqrMagnitude < 0.0001f)
                    dir = barrelDir;

                dir.Normalize();

                Vector2 found;

                if (FirstEnemyAlong(ship, dir, out found))
                    return found;

                return ship + dir * _reach;
            }

            if (has)
            {
                double x, y;

                ForgeCourierRules.ClampToReach(
                    ship.x, ship.y, cursor.x, cursor.y, _reach, out x, out y);

                return new Vector2((float)x, (float)y);
            }

            return from + barrelDir * _reach;
        }

        private bool UsesGamepad()
        {
            try
            {
                var input = _owner.GetComponentInChildren<ShipInput>(true);
                return input != null && input.UsesGamepad;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private bool FirstEnemyAlong(Vector2 from, Vector2 dir, out Vector2 at)
        {
            at = from;

            ForgeDroneModes.Enemies(_owner, from, _reach, false, _enemies);

            if (_enemies.Count == 0)
                return false;

            var xs = new double[_enemies.Count];
            var ys = new double[_enemies.Count];

            for (int i = 0; i < _enemies.Count; i++)
            {
                Vector2 p = _enemies[i].transform.position;
                xs[i] = p.x;
                ys[i] = p.y;
            }

            int pick = ForgeCourierRules.FirstAlongLine(
                from.x, from.y, dir.x, dir.y, _reach, 1.5, xs, ys);

            if (pick < 0)
                return false;

            at = _enemies[pick].transform.position;
            return true;
        }

        // ======================================================================
        // The body
        // ======================================================================

        private void BuildBody()
        {
            gameObject.name = "Forge Courier (" + _cfg.fileName + ")";

            if (IsDecoy && SpawnDecoy())
            {
                // The stock drone IS the look; a file's own art sits on it.
                if (!IsDefaultLook())
                    AttachArt(_decoy.transform, hideBody: true);
            }
            else
            {
                AttachArt(transform, hideBody: false);

                if (_cfg.shootable != ForgeCourierRules.Shootable.Off)
                    MakeHittable();
            }

            _health = Mathf.Max(0.1f, _cfg.health);
            CollectRenderers();
        }

        private bool IsDefaultLook()
        {
            return _cfg.art == null ||
                   string.Equals(_cfg.art.visual, ForgeWingman.DefaultBody,
                                 StringComparison.OrdinalIgnoreCase);
        }

        private void AttachArt(Transform parent, bool hideBody)
        {
            if (_cfg.art == null)
                return;

            try
            {
                _art = ForgeWingmanArt.Build(_cfg.art, parent.position);

                if (_art == null)
                    return;

                _art.name = "Courier Art";
                _art.transform.SetParent(parent, true);
                _art.transform.localPosition = Vector3.zero;

                _head = FindHead(_art.transform);

                if (hideBody)
                {
                    foreach (SpriteRenderer sr in
                             parent.GetComponentsInChildren<SpriteRenderer>(true))
                    {
                        if (sr != null && !sr.transform.IsChildOf(_art.transform))
                            sr.enabled = false;
                    }
                }
            }
            catch (Exception e)
            {
                Log.LogWarning(_cfg.fileName + ": courier art failed, the drone " +
                               "flies unseen: " + e.Message);
            }
        }

        // A drone's gun is its "Head" (the stock drone's BarrelTransform child),
        // so the gun turns to its aim rather than the whole drone tilting.
        private static Transform FindHead(Transform root)
        {
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
            {
                if (t != root && t.name.IndexOf("Head", StringComparison.OrdinalIgnoreCase) >= 0)
                    return t;
            }

            return null;
        }

        private void MakeHittable()
        {
            int layer = LayerMask.NameToLayer("Player");

            if (layer >= 0)
                gameObject.layer = layer;

            var rb = gameObject.AddComponent<Rigidbody2D>();
            rb.bodyType = RigidbodyType2D.Kinematic;
            rb.gravityScale = 0f;

            var col = gameObject.AddComponent<CircleCollider2D>();
            col.isTrigger = true;
            col.radius = 0.5f * Mathf.Max(0.2f, _cfg.art != null ? _cfg.art.scale : 1f);
        }

        // ---- decoy ---------------------------------------------------------------

        private static SavableEntity _decoyPrefab;
        private static bool _lookedPrefab;

        private static System.Reflection.MethodInfo _entitySetter;
        private static System.Reflection.FieldInfo _drainedDeath;
        private static bool _lookedReflection;

        private static readonly object GunLock = new object();

        private bool SpawnDecoy()
        {
            try
            {
                if (!_lookedPrefab)
                {
                    _lookedPrefab = true;

                    foreach (SavableEntity s in Resources.FindObjectsOfTypeAll<SavableEntity>())
                    {
                        if (s != null && !s.gameObject.scene.IsValid() &&
                            s.name == ForgeWingman.DefaultBody)
                        {
                            _decoyPrefab = s;
                            break;
                        }
                    }
                }

                EntityGameObjectManager manager;

                if (_decoyPrefab == null ||
                    !ServiceLocator.TryGet<EntityGameObjectManager>(out manager) ||
                    manager == null)
                {
                    SayDecoyFail("the stock drone (" + ForgeWingman.DefaultBody +
                                 ") or the entity manager could not be found");
                    return false;
                }

                SavableEntity made = manager.CreateEntity(_decoyPrefab, transform.position);

                if (made == null)
                    return false;

                _decoy = made.GetComponent<Unit>();

                if (_decoy == null)
                    _decoy = made.GetComponentInChildren<Unit>(true);

                if (_decoy == null)
                {
                    Destroy(made.gameObject);
                    return false;
                }

                _decoy.name = "Forge Courier Decoy";

                Silence(_decoy);
                Health(_decoy);

                _decoyBarrel = _decoy.GetComponentInChildren<BarrelTransform>(true);
                _decoyBody = _decoy.GetComponent<Rigidbody2D>();

                if (_decoyBody == null)
                    _decoyBody = _decoy.GetComponentInChildren<Rigidbody2D>(true);

                if (_decoyBody != null)
                {
                    _decoyBody.linearVelocity = Vector2.zero;
                    _decoyBody.angularVelocity = 0f;
                    _decoyBody.bodyType = RigidbodyType2D.Kinematic;
                    _decoyBody.constraints |= RigidbodyConstraints2D.FreezeRotation;
                }

                return true;
            }
            catch (Exception e)
            {
                SayDecoyFail(e.Message);
                return false;
            }
        }

        private void SayDecoyFail(string why)
        {
            if (_saidDecoyFail)
                return;

            _saidDecoyFail = true;

            Log.LogWarning(_cfg.fileName + ": a decoy courier drone could not " +
                           "be made (" + why + "), so it flew as one enemies " +
                           "only hit by accident. Said once per run.");
        }

        // Its brain off (the wingman body's way: the shallowest state machine
        // child, set inactive), its own gun locked, its death blast stripped.
        private static void Silence(Unit body)
        {
            Type sm = AccessTools.TypeByName("StateMachine");
            GameObject root = null;
            int best = int.MaxValue;

            if (sm != null)
            {
                foreach (Component c in body.GetComponentsInChildren(sm, true))
                {
                    if (c == null || c.gameObject == body.gameObject)
                        continue;

                    int depth = 0;

                    for (Transform t = c.transform; t != null && t != body.transform; t = t.parent)
                        depth++;

                    if (depth < best)
                    {
                        best = depth;
                        root = c.gameObject;
                    }
                }
            }

            if (root != null)
            {
                root.SetActive(false);
            }
            else
            {
                foreach (MovementAction a in body.GetComponentsInChildren<MovementAction>(true))
                    if (a != null) a.enabled = false;

                var agent = body.GetComponentInChildren<AIAgent>(true);

                if (agent != null)
                    agent.enabled = false;
            }

            var shooter = body.GetComponentInChildren<Shooter>(true);

            if (shooter != null)
            {
                try { shooter.Block(GunLock); } catch (Exception) { }
            }

            foreach (DamagableResource dr in body.GetComponentsInChildren<DamagableResource>(true))
                if (dr != null) dr.spawnOnDeath = null;

            if (!_lookedReflection)
            {
                _lookedReflection = true;

                try
                {
                    _drainedDeath = AccessTools.Field(typeof(DestroyWhenResourceDrained), "spawnOnDeath");
                    _entitySetter = AccessTools.PropertySetter(typeof(SavableEntity), "EntityData");
                }
                catch (Exception) { }
            }

            if (_drainedDeath != null)
            {
                foreach (DestroyWhenResourceDrained dw in
                         body.GetComponentsInChildren<DestroyWhenResourceDrained>(true))
                {
                    try { if (dw != null) _drainedDeath.SetValue(dw, null); }
                    catch (Exception) { }
                }
            }
        }

        private void Health(Unit body)
        {
            var dr = body.GetComponentInChildren<DamagableResource>(true);

            if (dr == null || dr.resource == null || !body.HasTank(dr.resource))
                return;

            ResourceTank tank = body.GetTank(dr.resource);

            if (tank == null)
                return;

            float want = Mathf.Max(1f, _cfg.health);

            if (want > tank.Capacity && !_saidDecoyHealth)
            {
                _saidDecoyHealth = true;

                Log.LogInfo(_cfg.fileName + ": a decoy is the stock drone, whose " +
                            "health tops out at " + tank.Capacity + ", so its " +
                            "\"health\" of " + want + " became " + tank.Capacity +
                            ". Said once per run.");
            }

            tank.Value = Mathf.Min(want, tank.Capacity);
        }

        // Before a save: out of the world now, not at the end of the frame.
        public void DropFromSave()
        {
            if (_decoy != null)
            {
                try
                {
                    var savable = _decoy.GetComponent<SavableEntity>();

                    if (savable != null && savable.EntityData != null)
                    {
                        EntityData data = savable.EntityData;

                        data.Destroy();

                        // So its own OnDestroy does not destroy it a second time.
                        if (_entitySetter != null)
                            _entitySetter.Invoke(savable, new object[] { null });
                    }
                }
                catch (Exception e)
                {
                    Log.LogWarning("courier: a decoy could not be taken out of " +
                                   "the save: " + e.Message);
                }

                Destroy(_decoy.gameObject);
                _decoy = null;
            }

            Destroy(gameObject);
        }

        // ======================================================================
        // Every frame
        // ======================================================================

        private void Enter(State s)
        {
            _state = s;
            _stateAt = Time.time;
        }

        private void Update()
        {
            try
            {
                Tick(Time.deltaTime);
            }
            catch (Exception e)
            {
                Log.LogError((_cfg != null ? _cfg.fileName : "courier") +
                             ": a drone failed and was removed: " + e);
                Destroy(gameObject);
            }
        }

        private void Tick(float dt)
        {
            if (_cfg == null)
            {
                Destroy(gameObject);
                return;
            }

            if (_state == State.Waiting)
            {
                if (Time.time >= _leaveAt)
                    Leave0();

                return;
            }

            // The decoy died, or its segment unloaded: nothing left to fly.
            // (A destroyed Unity object compares equal to null.)
            if (_madeDecoy && _decoy == null)
            {
                Destroy(gameObject);
                return;
            }

            if (_owner == null && _state != State.Leaving)
                Retire();

            switch (_state)
            {
                case State.Flying: Fly(dt); break;
                case State.Appearing: Appear(); break;
                case State.Winding: Wind(dt); break;
                case State.Firing: Fire(dt); break;
                case State.Leaving: Leaving(); break;
            }

            Paint();
            MoveBody();
        }

        private bool _madeDecoy;

        private void LateUpdate()
        {
            // AFTER motion, so a beam is drawn from where the drone ended up
            // this frame (the wingman beam pass, for the same reason).
            if (_state == State.Firing && _beam && Time.time < _beamUntil)
                DrawBeams(true);
        }

        // ---- flying ----------------------------------------------------------------

        private void Fly(float dt)
        {
            if (_cfg.follow)
            {
                Vector2 c;

                if (ForgeVolley.TryCursor(_owner, out c) && !UsesGamepad())
                {
                    double x, y;
                    Vector2 ship = _owner.transform.position;

                    ForgeCourierRules.ClampToReach(ship.x, ship.y, c.x, c.y, _reach, out x, out y);
                    _centre = new Vector2((float)x, (float)y);
                    _target = _centre + _ringOffset;
                }
            }

            Vector2 pos = transform.position;
            double nx, ny;

            bool arrived = ForgeCourierRules.StepToward(
                pos.x, pos.y, _target.x, _target.y,
                Mathf.Max(0.1f, _cfg.speed) * dt, out nx, out ny);

            Vector2 next = new Vector2((float)nx, (float)ny);

            // `overWalls: false` - stops at the first rock and fires from there.
            if (!_cfg.overWalls && ForgeDroneModes.Solid(next, 0.25f))
            {
                arrived = true;
                next = pos;
            }

            Vector2 step = next - pos;

            if (step.sqrMagnitude > 0.000001f)
                Face(step.normalized);

            transform.position = next;

            if (_cfg.contact)
                Contact(next);

            if (arrived)
                StartWinding();
        }

        private void Appear()
        {
            float t = (Time.time - _stateAt) / 0.2f;
            _alpha = Mathf.Clamp01(t);

            if (t >= 1f)
                StartWinding();
        }

        // ---- winding up ------------------------------------------------------------

        private void StartWinding()
        {
            _alpha = 1f;
            Enter(State.Winding);

            Vector2 aim = AimNow();
            Face(aim);

            if (UsesBeam())
            {
                BuildBeams();

                if (!string.IsNullOrEmpty(_cfg.beamWarmup) && _cfg.windup > 0f)
                {
                    try { _warm = AudioManager.PlaySfx(_cfg.beamWarmup, transform); }
                    catch (Exception) { _warm = -1; }
                }
            }
        }

        private void Wind(float dt)
        {
            Vector2 aim = AimNow();
            Face(aim);

            if (_beam)
            {
                // The game's own warm-up art: thin and pulsing while the
                // progress is below the copy's WarmupTime (= windup).
                SetDirections(aim, _shot);

                for (int i = 0; i < _beams.Count && i < _dirs.Count; i++)
                {
                    HitscanWeapon b = _beams[i];

                    if (b == null)
                        continue;

                    b.IsTriggerPulled = true;
                    b.Warmup(dt);
                    b.OnBarrelMoved(transform.position, _dirs[i]);
                }
            }

            if (Time.time - _stateAt >= Mathf.Max(0f, _cfg.windup))
            {
                StopSound(ref _warm);
                Enter(State.Firing);
                _nextShot = Time.time;
            }
        }

        // ---- firing -----------------------------------------------------------------

        private void Fire(float dt)
        {
            if (_beam && _beamUntil > 0f)
            {
                if (Time.time < _beamUntil)
                {
                    Ticks();
                    return;
                }

                EndBeam();

                if (_shot >= Mathf.Max(1, _cfg.shots))
                {
                    Retire();
                    return;
                }
            }

            if (Time.time < _nextShot)
            {
                Face(AimNow());
                return;
            }

            if (_shot >= Mathf.Max(1, _cfg.shots))
            {
                Retire();
                return;
            }

            WeaponBase gun = ForgeCourier.LiveGun(_launched, _data, _owner, _cfg);

            if (gun == null)
            {
                Retire();
                return;
            }

            _launched = gun;

            Vector2 aim = AimNow();
            SetDirections(aim, _shot);

            if (_dirs.Count > 0)
                _lastDir = _dirs[0];

            Face(_lastDir);

            if (_beam)
            {
                StartBeam(gun);
            }
            else
            {
                Volley(gun);
            }

            _shot++;

            double interval = ForgeCourierRules.Interval(_cfg.interval, gun.FireRate);

            _nextShot = (_beam ? _beamUntil : Time.time) + (float)interval;

            if (!_beam && _shot >= Mathf.Max(1, _cfg.shots))
                Retire();
        }

        // A bullet payload: the gun's own (or the named weapon) fired once per
        // direction, through its public DoShoot - so every one of this mod's
        // DoShoot hooks sees one real volley, fired from here.
        private void Volley(WeaponBase gun)
        {
            WeaponBase w = gun;
            WeaponData named = ForgeCourier.NamedData(_cfg);

            if (!string.IsNullOrEmpty(_cfg.weapon))
            {
                if (named == null)
                {
                    SayNoNamed();
                }
                else
                {
                    w = ForgeCourier.Payload(gun, named);
                }
            }

            Vector2 pos = transform.position;

            for (int i = 0; i < _dirs.Count; i++)
                Shoot(w, pos, _dirs[i], _ring);
        }

        private void SayNoNamed()
        {
            if (_saidNoNamed)
                return;

            _saidNoNamed = true;

            Log.LogWarning(_cfg.fileName + ": courier \"weapon\" '" + _cfg.weapon +
                           "' was not found, so the drone fires the gun's own " +
                           "shot instead. Said once per run.");
        }

        // ★ ONE VOLLEY, IN SCOPE. `Firing` tells the launch intercept and the
        // wingman mirror that this is a drone firing, not a pull. A ring
        // drone fires ONE pellet - the ring is the pellets - so the count is
        // set for this one synchronous call and put back in a finally, the
        // way charge scopes Cost: nothing else can run in between.
        private static void Shoot(WeaponBase w, Vector2 pos, Vector2 dir, bool onePellet)
        {
            ForgeCourier.Firing++;
            float keep = w.ProjectileCount;

            try
            {
                if (onePellet)
                    w.ProjectileCount = 1f;

                w.DoShoot(new FakeBarrel(pos, dir));
            }
            finally
            {
                if (onePellet)
                    w.ProjectileCount = keep;

                ForgeCourier.Firing--;
            }
        }

        // ---- beams ---------------------------------------------------------------------

        private bool UsesBeam()
        {
            if (!string.IsNullOrEmpty(_cfg.weapon))
            {
                WeaponData named = ForgeCourier.NamedData(_cfg);

                if (named != null)
                    return named is HitscanWeaponData;
            }

            return _launched is HitscanWeapon;
        }

        private void BuildBeams()
        {
            ReleaseBeams();

            WeaponBase gun = ForgeCourier.LiveGun(_launched, _data, _owner, _cfg);

            if (gun == null)
                return;

            WeaponData named = string.IsNullOrEmpty(_cfg.weapon) ? null : ForgeCourier.NamedData(_cfg);
            bool own = named == null;
            WeaponData data = own ? gun.TemplateData : named;

            int ways = ForgeCourierRules.PatternOffsets(_cfg.pattern).Length;

            for (int i = 0; i < ways; i++)
            {
                HitscanWeapon b = ForgeCourier.Beam(data, gun, own, _ring, _cfg.windup);

                if (b != null)
                    _beams.Add(b);
            }

            _beam = _beams.Count > 0;
        }

        private void StartBeam(WeaponBase gun)
        {
            if (_beams.Count == 0)
                BuildBeams();

            if (_beams.Count == 0)
            {
                _beam = false;
                Volley(gun);
                return;
            }

            _beamUntil = Time.time + Mathf.Max(0.05f, _cfg.beamTime);
            _nextTick = Time.time;

            for (int i = 0; i < _beams.Count; i++)
            {
                HitscanWeapon b = _beams[i];

                if (b == null)
                    continue;

                // Fully warmed: the firing art from the first frame.
                b.Warmup(Mathf.Max(1f, b.WarmupTime + 1f));
                b.IsTriggerPulled = true;
            }

            if (!string.IsNullOrEmpty(_cfg.beamHum))
            {
                try { _hum = AudioManager.PlaySfx(_cfg.beamHum, transform); }
                catch (Exception) { _hum = -1; }
            }

            Ticks();
        }

        // Damage `beamRate` times a second while the beam is held - NOT the
        // gun's fireRate, which on a courier gun is the launch rate.
        private void Ticks()
        {
            if (Time.time < _nextTick)
                return;

            _nextTick = Time.time + 1f / Mathf.Max(1f, _cfg.beamRate);

            Vector2 pos = transform.position;

            for (int i = 0; i < _beams.Count && i < _dirs.Count; i++)
            {
                if (_beams[i] != null)
                    Shoot(_beams[i], pos, _dirs[i], false);
            }
        }

        private void DrawBeams(bool on)
        {
            Vector2 pos = transform.position;

            for (int i = 0; i < _beams.Count && i < _dirs.Count; i++)
            {
                HitscanWeapon b = _beams[i];

                if (b == null)
                    continue;

                try
                {
                    b.IsTriggerPulled = on;
                    b.OnBarrelMoved(pos, _dirs[i]);
                }
                catch (Exception)
                {
                    // A beam that cannot draw must not stop the drone.
                }
            }
        }

        private void EndBeam()
        {
            DrawBeams(false);
            _beamUntil = 0f;

            if (_hum >= 0)
            {
                StopSound(ref _hum);

                if (!string.IsNullOrEmpty(_cfg.beamRelease))
                {
                    try { AudioManager.PlaySfx(_cfg.beamRelease, transform.position); }
                    catch (Exception) { }
                }
            }

            // The next shot warms up again only if there is a windup between.
            for (int i = 0; i < _beams.Count; i++)
                if (_beams[i] != null) _beams[i].CoolDown();
        }

        private void ReleaseBeams()
        {
            for (int i = 0; i < _beams.Count; i++)
            {
                HitscanWeapon b = _beams[i];

                if (b == null)
                    continue;

                try
                {
                    b.IsTriggerPulled = false;
                    b.OnBarrelMoved(transform.position, _lastDir);
                }
                catch (Exception) { }

                ForgeCourier.Release(b);
            }

            _beams.Clear();
        }

        private static void StopSound(ref int handle)
        {
            if (handle < 0)
                return;

            try { AudioManager.Stop(handle); } catch (Exception) { }
            handle = -1;
        }

        // ---- aim ---------------------------------------------------------------------

        private Vector2 AimNow()
        {
            Vector2 pos = transform.position;
            Vector2 ship = (_owner != null) ? (Vector2)_owner.transform.position : _origin;

            // A firing line (a boosted pull, or a wingman's mirrored drone)
            // fires PARALLEL - "they all fire the same way" - so outward is
            // measured from the line's centre, not from each drone's own spot.
            // With no offset (one drone) this is the drone's own position.
            Vector2 outward = (_ring ? pos : pos - _ringOffset) - ship;

            if (outward.sqrMagnitude < 0.0001f)
                outward = _launchDir;

            switch (_cfg.AimFor(_ring))
            {
                case ForgeCourierRules.Aim.Enemy:
                {
                    WeaponBase gun = _launched;
                    // The gun's own SHOT range (no file reach): how far its
                    // shot carries from here, not how far the drone flies.
                    float range = (gun != null) ? ForgeCourier.ReachOf(gun, null) : _reach;

                    ForgeDroneModes.Enemies(_owner, pos, range, false, _enemies);

                    if (_enemies.Count > 0)
                    {
                        Vector2 to = (Vector2)_enemies[0].transform.position - pos;

                        if (to.sqrMagnitude > 0.0001f)
                            return to.normalized;
                    }

                    return outward.normalized;
                }

                case ForgeCourierRules.Aim.Back:
                    return (-outward).normalized;

                case ForgeCourierRules.Aim.Launch:
                    return _launchDir;

                case ForgeCourierRules.Aim.Centre:
                {
                    Vector2 to = _centre - pos;
                    return (to.sqrMagnitude > 0.0001f) ? to.normalized : outward.normalized;
                }

                default:
                    return outward.normalized;
            }
        }

        // Every direction shot `shot` leaves in: snap, turn, pattern.
        private void SetDirections(Vector2 aim, int shot)
        {
            _dirs.Clear();

            double deg = ForgeCourierRules.AngleOf(aim.x, aim.y);

            foreach (double a in ForgeCourierRules.Angles(
                         deg, _cfg.snap, _cfg.turn, shot, _cfg.pattern))
            {
                float r = (float)(a * Math.PI / 180.0);
                _dirs.Add(new Vector2(Mathf.Cos(r), Mathf.Sin(r)));
            }
        }

        private void Face(Vector2 dir)
        {
            if (!_cfg.faceAim || dir.sqrMagnitude < 0.0001f)
                return;

            float deg = Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg;

            if (_decoyBarrel != null)
                _decoyBarrel.Direction = dir;

            if (_head != null)
            {
                _head.rotation = Quaternion.Euler(0f, 0f, deg);
            }
            else if (_art != null && _decoy == null)
            {
                _art.transform.rotation = Quaternion.Euler(0f, 0f, deg);
            }
        }

        // ---- contact -------------------------------------------------------------------

        private void Contact(Vector2 pos)
        {
            if (!_contactReady)
            {
                _contactReady = true;
                _contactFilter = new ContactFilter2D();
                _contactFilter.useTriggers = true;
                _contactFilter.SetLayerMask(LayerMask.GetMask("Entities", "Fruits"));
            }

            WeaponBase gun = _launched;

            if (gun == null || _owner == null)
                return;

            float radius = 0.6f * Mathf.Max(0.2f, _cfg.art != null ? _cfg.art.scale : 1f);
            int n = Physics2D.OverlapCircle(pos, radius, _contactFilter, _hits);

            for (int i = 0; i < n; i++)
            {
                Collider2D c = _hits[i];

                if (c == null)
                    continue;

                Unit u = c.GetComponentInParent<Unit>();

                if (u != null && (ReferenceEquals(u, _owner) || _owner.IsFriendsWith(u)))
                    continue;

                HealthBase hb = c.GetComponentInParent<HealthBase>();

                if (hb == null)
                    continue;

                float last;

                if (_touched.TryGetValue(hb, out last) && Time.time - last < 0.4f)
                    continue;

                _touched[hb] = Time.time;

                bool wasDead = ForgeKillsPatch.JustDied(hb);

                hb.TakeDamage(gun.Damage);

                if (!wasDead && u != null && ForgeKillsPatch.JustDied(hb))
                    ForgeKillsPatch.Credit(_owner, u, gun);
            }
        }

        // ---- being shot (`shootable: hit`) ------------------------------------------------

        public void ProjectileCollided(IProjectile projectile, Vector2 position, Vector2 normal)
        {
            if (projectile == null || Friendly(projectile.Owner))
                return;

            TakeHit(projectile.Damage.amount);
        }

        public void OnHitByHitscanWeapon(HitscanWeapon weapon, RaycastHit2D hit)
        {
            if (weapon == null || Friendly(weapon.Owner))
                return;

            TakeHit(weapon.Damage.amount);
        }

        public void OnExplosion(Explosion explosion)
        {
            if (_cfg == null || _cfg.shootable != ForgeCourierRules.Shootable.Hit)
                return;

            // Your own blasts never - an explosion reaches every collider in
            // its radius on any layer, the drone's included.
            if (Friendly(explosion.Owner) && explosion.Owner != null)
                return;

            float sum = 0f;

            if (explosion.damages != null)
                foreach (Damage d in explosion.damages)
                    sum += d.amount;

            TakeHit(sum);
        }

        private bool Friendly(Unit shooter)
        {
            if (shooter == null || _owner == null)
                return false;

            return ReferenceEquals(shooter, _owner) || _owner.IsFriendsWith(shooter);
        }

        private void TakeHit(float amount)
        {
            if (_cfg == null || _cfg.shootable != ForgeCourierRules.Shootable.Hit ||
                _state == State.Leaving || amount <= 0f)
            {
                return;
            }

            _health -= amount;
            _flashUntil = Time.time + 0.1f;

            if (_health <= 0f)
            {
                // Shot down: it never fires again. A quick fade, not a blast.
                Retire(0.15f);
            }
        }

        // ======================================================================
        // Leaving
        // ======================================================================

        private float _fadeFor = -1f;

        public void Retire()
        {
            Retire(-1f);
        }

        private void Retire(float fade)
        {
            if (_state == State.Leaving)
                return;

            if (_state == State.Waiting)
            {
                Destroy(gameObject);
                return;
            }

            StopSound(ref _warm);

            if (_beamUntil > 0f)
                EndBeam();

            ReleaseBeams();

            _fadeFor = (fade >= 0f) ? fade : Mathf.Max(0f, _cfg.fadeTime);
            _recoilFrom = transform.position;
            Enter(State.Leaving);

            // Hittable no more on the way out.
            foreach (Collider2D c in GetComponents<Collider2D>())
                if (c != null) c.enabled = false;
        }

        private Vector2 _recoilFrom;

        private void Leaving()
        {
            float t = Time.time - _stateAt;
            float dur = Mathf.Max(0.0001f, _fadeFor);

            _alpha = Mathf.Clamp01(1f - t / dur);

            if (_cfg.exit == ForgeCourierRules.Exit.Recoil && _fadeFor > 0f)
            {
                float d = (float)ForgeCourierRules.RecoilDistance(8f, t, dur);
                transform.position = _recoilFrom - _lastDir * d;
            }

            if (t >= _fadeFor)
                Destroy(gameObject);
        }

        // ======================================================================
        // Paint and the decoy's body
        // ======================================================================

        private void CollectRenderers()
        {
            _renderers.Clear();
            _bases.Clear();

            Transform root = (_decoy != null) ? _decoy.transform : transform;

            foreach (SpriteRenderer sr in root.GetComponentsInChildren<SpriteRenderer>(true))
            {
                if (sr == null)
                    continue;

                // A renderer held at alpha 0 (a highlight silhouette) is a
                // visibility decision, not a colour - never brought up.
                if (sr.name.IndexOf("highlight", StringComparison.OrdinalIgnoreCase) >= 0)
                    continue;

                _renderers.Add(sr);
                _bases.Add(sr.color);
            }

            _madeDecoy = _decoy != null;
        }

        // From the captured base every frame - never a read-modify-write of
        // its own last output (the CLAUDE.md colour rule).
        private void Paint()
        {
            bool flash = Time.time < _flashUntil;

            for (int i = 0; i < _renderers.Count; i++)
            {
                SpriteRenderer sr = _renderers[i];

                if (sr == null)
                    continue;

                Color c = _bases[i];

                if (flash)
                {
                    c.g *= 0.45f;
                    c.b *= 0.45f;
                }

                c.a *= _alpha;
                sr.color = c;
            }
        }

        private void MoveBody()
        {
            if (_decoy == null)
                return;

            Vector3 p = transform.position;

            _decoy.transform.position = p;

            if (_decoyBody != null)
                _decoyBody.position = p;
        }

        private void OnDestroy()
        {
            StopSound(ref _warm);
            StopSound(ref _hum);

            try { ReleaseBeams(); } catch (Exception) { }

            ForgeCourier.Forget(this);

            if (_decoy != null)
            {
                try { Destroy(_decoy.gameObject); } catch (Exception) { }
                _decoy = null;
            }
        }
    }
}
