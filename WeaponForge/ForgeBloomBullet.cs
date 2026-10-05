using System;
using BepInEx.Logging;
using UnityEngine;

namespace WeaponForge
{
    // BULLET MODE - `"when": "bullet"` (2026-10-02, his design).
    //
    // *"a mode where they don't grow or pop but just as normal projectiles...
    // whatever the prefab of the projectile is (Like rocket, blade, popper,
    // etc)."* So the shot is PLACED like a bloom - on the ring round your
    // crosshair, clamped to the reach - and then FLIES the drift's path as the
    // gun's own projectile: its own size, art and trail, and the engine's own
    // hits, piercing and impact. Rides the prefab like ForgeBloomShot, on a
    // plain Projectile or on a missile's PhysicsProjectile.
    //
    // ---- WHAT STILL MEANS SOMETHING DIFFERENT HERE -----------------------
    //
    // ONE number keeps its bloom meaning: `rangeData.range` is how far out the
    // pattern can be PLACED. So the engine's own range handler stays off - a
    // range of 22 would otherwise delete a three-lap spiral partway round -
    // and the reach comes back as a flight budget only where a shot has no
    // centre to end at (outward, along, spin, or the leg out after `through`).
    // Everything else is the gun's: `projectileSpeed` is the speed along the
    // path, `projectileRadius` the hit size, `lifetimeData` its life.
    //
    // ---- THE FOUR SHAPES ---------------------------------------------------
    //
    //   Line    inward / staggered: straight to the centre. Its heading is set
    //           once, like a bubble's, so wave, homing, chain and the rest
    //           compose with it as usual.
    //   Spiral  spiral: re-aimed every step at the next point on the path, so
    //           it owns its direction outright (ForgeSteer.Drive).
    //   Orbit   spin: put exactly on the circle every step - a bullet is far
    //           too fast for spin's re-aimed tangent, which drifts outward.
    //   Away    outward / along: straight out, never reaching the centre.
    //
    // ANYTHING THAT TURNS A BLOOM BULLET TAKES IT OFF THE PATH. Homing, guided,
    // chain, ricochet and a bounce all publish a new heading through ForgeSteer
    // (or the engine's Reflect); when the heading is no longer the one bloom
    // set, the shot has been turned, and it flies on as an ordinary bullet
    // within the gun's reach. Wave does not count - it weaves round the heading
    // without changing it.
    //
    // ---- A ROCKET (gravityScale 0) -----------------------------------------
    //
    //   fly    the mod writes its velocity every physics step and skips the
    //          rocket's own FixedUpdate (which is only `Homing()`: thrust,
    //          torque and wobble), so it holds the exact path at a steady
    //          projectileSpeed. Its collisions are still real physics.
    //   chase  the rocket's own engine chases a point moving along the path
    //          (`targetPosition`, the seam `guided` uses). It feels like a
    //          rocket and cuts the laps it cannot turn tightly enough for.
    //
    // A grenade is refused at build time: steering one along a path would stop
    // it falling, and a grenade that does not fall is not one.
    public class ForgeBloomBullet : MonoBehaviour
    {
        private static readonly ManualLogSource Log =
            ForgeLog.Source("WeaponForge.Bloom");

        private static bool _saidNoSpeed;
        private static bool _saidHookFail;

        // ---- serialized (survives Instantiate) - an int, never the Config;
        // see ForgeBloomShot's header for the trap this avoids.
        public int cfgId = -1;

        // The template's numbers, for a shot that could not read the live gun.
        public float fallbackRate = 20f;
        public float fallbackRange = 40f;

        // ---- runtime -----------------------------------------------------
        [NonSerialized] public ForgeBloom.Config cfg;
        [NonSerialized] public bool live;
        [NonSerialized] public bool placed;

        private enum Kind { Line, Spiral, Orbit, Away }

        // Path: still on bloom's path. Through: past the centre, flying out.
        // Free: turned off the path, or never on one that ends.
        private enum Leg { Path, Through, Free }

        private Kind _kind;
        private Leg _leg;
        private bool _ending;
        private bool _arriving;

        private Projectile _p;
        private PhysicsProjectile _pp;
        private Rigidbody2D _rb;
        private IProjectile _ip;
        private float _damping;

        private Vector2 _centre;
        private Vector2 _start;
        private Vector2 _dir;
        private Vector2 _endDir;
        private Vector2 _lastIdeal;
        private Vector2 _lastPos;
        private float _speed;
        private float _r0;
        private float _reach;
        private float _along;
        private float _flown;

        private ForgeBloomPath.Spiral _spiral;
        private double _phi;
        private float _theta0;
        private float _sign;

        // chase
        private float _look;
        private float _minDist = float.MaxValue;
        private bool _final;
        private bool _committed;

        // The shot's own end-of-life settings, COPIED before bloom switches the
        // range off, so the hooks can still be fired at the end it chooses.
        private ProjectileRangeData _range;
        private ProjectileLifetimeData _life;
        private ProjectileImpactBehaviour _impact;

        // How close a chasing rocket must pass to count as at the centre.
        private const float ChaseArrive = 0.75f;

        // A heading turned by more than this, by something other than bloom,
        // means the shot has been turned off its path.
        private const float TurnedDegrees = 2f;

        private void Awake()
        {
            if (cfgId >= 0)
                cfg = ForgeConfigTable.Get<ForgeBloom.Config>(cfgId);

            _p = GetComponent<Projectile>();
            _pp = GetComponent<PhysicsProjectile>();

            if (_p != null)
                _ip = _p;
            else if (_pp != null)
                _ip = _pp;

            if (_pp != null)
            {
                _rb = GetComponent<Rigidbody2D>();

                if (_rb == null)
                    _rb = GetComponentInChildren<Rigidbody2D>();

                if (_rb != null)
                    _damping = _rb.linearDamping;
            }
        }

        // ---- from the Shoot PREFIX: the path, then the launch -------------
        //
        // Called with the placement ForgeBloomPatch worked out - the same one a
        // bubble gets. Nothing about the shot is changed until Launch, so a
        // failure in here leaves an ordinary bullet rather than a broken one.
        public void Setup(
            Vector2 centre, Vector2 at, Vector2 axis,
            float speed, float reach, int index, int count)
        {
            _centre = centre;
            _start = at;
            _reach = Mathf.Max(0.5f, reach);

            if (speed > 0.01f)
            {
                _speed = speed;
            }
            else
            {
                _speed = Mathf.Max(1f, fallbackRate);

                if (!_saidNoSpeed)
                {
                    _saidNoSpeed = true;

                    Log.LogWarning(
                        "bloom bullet: this weapon has no projectileSpeed, and " +
                        "a bullet needs one to fly its path - flying at " +
                        _speed.ToString("0.#") + " units/second instead. Set " +
                        "\"projectileSpeed\". Said once per run.");
                }
            }

            Vector2 out_ = at - centre;
            _r0 = out_.magnitude;

            Vector2 axisUnit = (axis.sqrMagnitude > 0.000001f)
                ? axis.normalized : Vector2.right;

            Vector2 inward = (_r0 > 0.05f) ? -out_ / _r0 : axisUnit;

            // No drift means a bullet that does not move, and a still bullet is
            // the IMMORTAL AURA: its zero-length sweep hits whatever it sits in
            // every step, and nothing ends it. The build log says inward.
            ForgeBloom.Drift d = (cfg.drift == ForgeBloom.Drift.None)
                ? ForgeBloom.Drift.Inward : cfg.drift;

            bool cw = cfg.Clockwise(d);

            _leg = Leg.Path;

            switch (d)
            {
                case ForgeBloom.Drift.Staggered:
                    _kind = Kind.Line;
                    _dir = inward;

                    // Same order as the bubbles: the first lands at the gun's
                    // own speed, the last `stagger` x its flight time later.
                    _speed /= (float)ForgeBloomPath.StaggerScale(
                        ForgeBloomPath.StaggerShare(index, count, cw),
                        cfg.stagger);
                    break;

                case ForgeBloom.Drift.Spiral:
                    _kind = Kind.Spiral;
                    _spiral = ForgeBloom.MakeSpiral(at, centre, cfg.turns, cw);
                    _phi = _spiral.phiMax;
                    _dir = (_r0 > 0.05f)
                        ? ForgeBloom.SpiralStartHeading(ref _spiral)
                        : axisUnit;

                    // A STAGGERED SPIRAL, when the file names `stagger`: the
                    // later shots wind the same arm more slowly and land one
                    // after another (StaggerFor is 0 otherwise - scale 1).
                    _speed /= (float)ForgeBloomPath.StaggerScale(
                        ForgeBloomPath.StaggerShare(index, count, cw),
                        cfg.StaggerFor(d));
                    break;

                case ForgeBloom.Drift.Spin:
                    if (_r0 > 0.05f)
                    {
                        _kind = Kind.Orbit;
                        _theta0 = Mathf.Atan2(out_.y, out_.x);
                        _sign = cw ? -1f : 1f;
                        _dir = cw
                            ? new Vector2(out_.y, -out_.x) / _r0
                            : new Vector2(-out_.y, out_.x) / _r0;
                    }
                    else
                    {
                        // A single bullet on the crosshair has no circle.
                        _kind = Kind.Away;
                        _leg = Leg.Free;
                        _dir = axisUnit;
                    }
                    break;

                case ForgeBloom.Drift.Outward:
                    _kind = Kind.Away;
                    _leg = Leg.Free;
                    _dir = (_r0 > 0.05f) ? out_ / _r0 : axisUnit;
                    break;

                case ForgeBloom.Drift.Along:
                    _kind = Kind.Away;
                    _leg = Leg.Free;
                    _dir = axisUnit;
                    break;

                default:
                    _kind = Kind.Line;
                    _dir = inward;
                    break;
            }

            _endDir = (_kind == Kind.Spiral && _r0 > 0.05f)
                ? ForgeBloom.SpiralEndHeading(ref _spiral)
                : _dir;

            // ALREADY ON THE CENTRE - one bullet, or spread 0. A drift that
            // ends at the centre has nowhere to come from, so the rule applies
            // on the first step: `end` and `burst` go off at once, `through`
            // flies out along your aim.
            if ((_kind == Kind.Line || _kind == Kind.Spiral) && _r0 <= 0.05f)
            {
                _arriving = true;
                _endDir = axisUnit;
            }

            _look = Mathf.Clamp(_r0 * 0.5f, 1.5f, 6f);
            _lastIdeal = _dir;
            _lastPos = at;
        }

        // ---- still from the prefix: move it, aim it, take over its ends ---
        public void Launch()
        {
            placed = true;

            // The copies first, before anything here switches them off.
            _range = _ip.RangeData;
            _life = _ip.LifetimeData;
            _impact = _ip.ImpactBehaviour;

            // RANGE IS PLACEMENT HERE, so the engine's own handler is switched
            // off on this shot. (HandleRange tests `enabled` every frame, so
            // this is enough on its own; a physics shot has no range handler
            // at all.)
            ProjectileRangeData rd = _ip.RangeData;
            rd.enabled = false;
            _ip.RangeData = rd;

            // THERE IS NO MUZZLE TO BE SAFE FROM. `safetyDistance` stops a shot
            // dying on something right at the barrel: inside it, a plain shot
            // REFLECTS instead of dying, and a bullet that appears inside an
            // enemy then hits it on every step - the stock Popper's
            // damageRepeatDelay is 0. A bloom bullet appears at the crosshair,
            // so anything it touches is a real hit: -1 is always passed.
            ProjectileImpactBehaviour ib = _ip.ImpactBehaviour;
            ib.safetyDistance = -1f;
            _ip.ImpactBehaviour = ib;

            transform.position = _start;

            if (_p != null)
            {
                // The game's own noise wobble rebuilds Velocity in the
                // ORIGINAL FixedUpdate, after every prefix - so on a path that
                // is re-aimed each step it would have the last word. No stock
                // player weapon has it on; an enemy's might.
                if (_kind == Kind.Spiral || _kind == Kind.Orbit)
                {
                    ProjectileMovementNoiseData nd = _p.MovementNoiseData;
                    nd.enabled = false;
                    _p.MovementNoiseData = nd;
                }

                // Shoot derives idealDirection and its WhenShot rotation from
                // this, so the shot leaves the ring already facing its path.
                ForgeSteer.Drive(_p, _dir * _speed);
            }
            else if (_rb != null)
            {
                _rb.position = _start;

                float deg = Mathf.Atan2(_dir.y, _dir.x) * Mathf.Rad2Deg;

                transform.rotation = Quaternion.Euler(0f, 0f, deg);
                _rb.rotation = deg;
                _rb.angularVelocity = 0f;
                _rb.linearVelocity = _dir * _speed;
            }
        }

        // ---- from the Shoot POSTFIX ---------------------------------------
        public void Begin()
        {
            live = true;

            // Shoot has just derived idealDirection from the launch velocity;
            // published again so `_lastIdeal` and the field agree exactly.
            if (_p != null)
                ForgeSteer.SetIdeal(_p, _dir);
        }

        // ==================================================================
        // A PLAIN BULLET - from the Projectile.FixedUpdate prefix, which runs
        // LAST of that method's prefixes (Priority.Last), so on a path this is
        // the final word on the step the engine is about to sweep and move.
        // Returns whether the engine's FixedUpdate should run.
        // ==================================================================
        public bool TickPlain(float dt)
        {
            if (_ending)
                return false;

            if (!live || _p == null || dt <= 0f)
                return true;

            Vector2 pos = transform.position;

            if (_leg != Leg.Path)
                return CountDown(pos);

            // Turned by something else: it has left the path.
            Vector2 ideal;

            if (ForgeSteer.TryGetIdeal(_p, out ideal) &&
                Vector2.Angle(ideal, _lastIdeal) > TurnedDegrees)
            {
                GoFree(pos);
                return true;
            }

            if (_arriving)
                return AtCentre(pos);

            switch (_kind)
            {
                case Kind.Line:
                {
                    float remaining = _r0 - Vector2.Dot(pos - _start, _dir);

                    if (remaining <= 0.0001f)
                        return AtCentre(pos);

                    // The last step is shortened to land ON the centre, so the
                    // sweep covers the line up to it and no further.
                    if (remaining <= _speed * dt)
                    {
                        ForgeSteer.SetHeading(_p, _dir, remaining / dt);
                        _arriving = true;
                    }

                    return true;
                }

                case Kind.Spiral:
                {
                    Vector2 target = NextOnSpiral(dt);

                    Aim(pos, target, dt);
                    return true;
                }

                case Kind.Orbit:
                {
                    _along += _speed * dt;

                    if (_along >= _reach)
                    {
                        Finish(pos, false);
                        return false;
                    }

                    Aim(pos, OrbitPoint(_along), dt);
                    return true;
                }
            }

            return true;
        }

        // ==================================================================
        // A MISSILE - from the PhysicsProjectile.FixedUpdate prefix. That
        // method's whole body is `Homing()`: thrust, torque and wobble. Fly
        // returns false to skip it; chase lets it run, aimed by us.
        // ==================================================================
        public bool TickPhysics(float dt)
        {
            if (_ending)
                return false;

            if (!live || _pp == null || _rb == null || dt <= 0f)
                return true;

            Vector2 pos = _rb.position;

            if (cfg.missile == ForgeBloom.Missile.Chase)
                return Chase(pos, dt);

            if (_leg != Leg.Path)
            {
                if (!CountDown(pos))
                    return false;

                Fly(pos, pos + _dir * (_speed * dt), dt);
                return false;
            }

            if (_arriving)
            {
                if (!AtCentre(pos))
                    return false;

                Fly(pos, pos + _dir * (_speed * dt), dt);
                return false;
            }

            switch (_kind)
            {
                case Kind.Line:
                {
                    // Driven to the point the line says, not along a heading:
                    // a rigidbody nudged by a contact does not drift off it.
                    _along = Mathf.Min(_r0, _along + _speed * dt);

                    if (_along >= _r0)
                        _arriving = true;

                    Fly(pos, _start + _dir * _along, dt);
                    break;
                }

                case Kind.Spiral:
                    Fly(pos, NextOnSpiral(dt), dt);
                    break;

                case Kind.Orbit:
                    _along += _speed * dt;

                    if (_along >= _reach)
                    {
                        Finish(pos, false);
                        return false;
                    }

                    Fly(pos, OrbitPoint(_along), dt);
                    break;
            }

            return false;
        }

        // CHASE: the rocket's own engine follows a point running ahead of it.
        //
        // How far along the path the rocket has got is dead-reckoned from its
        // own speed, and the aim point is `_look` beyond that - so a rocket
        // slower than the path (a stock rocket cruises near 6 units/second) is
        // not left chasing a point that ran away. Once the aim reaches the
        // centre, the rocket counts as there at its CLOSEST PASS: a rigidbody
        // with a turning circle cannot stop on a point, it flies through it
        // (CLAUDE.md, "arrive cannot stop a rigidbody circling a fixed point").
        // One that never gets close enough circles until its own lifetime or
        // the reach ends it.
        private bool Chase(Vector2 pos, float dt)
        {
            if (_leg != Leg.Path)
            {
                // Point it far along its nose, once - the engine's own "commit
                // straight ahead".
                if (!_committed)
                {
                    _committed = true;
                    ForgeGuidedPhysics.Commit(_pp);
                }

                return CountDown(pos);
            }

            if (_arriving)
                return AtCentre(pos);

            float moving = _rb.linearVelocity.magnitude;

            _along += moving * dt;

            float ahead = _along + _look;
            Vector2 aim = _centre;

            switch (_kind)
            {
                case Kind.Line:
                    if (ahead >= _r0)
                        _final = true;
                    else
                        aim = _start + _dir * ahead;
                    break;

                case Kind.Spiral:
                    if (ahead >= (float)_spiral.length)
                    {
                        _final = true;
                    }
                    else
                    {
                        aim = ForgeBloom.SpiralAt(ref _spiral, ahead, ref _phi);
                    }
                    break;

                case Kind.Orbit:
                    if (_along >= _reach)
                    {
                        Finish(pos, false);
                        return false;
                    }

                    aim = OrbitPoint(ahead);
                    break;
            }

            ForgeGuidedPhysics.Aim(_pp, aim);

            if (_final)
            {
                float dc = Vector2.Distance(pos, _centre);

                if (dc < _minDist)
                    _minDist = dc;

                if (dc <= ChaseArrive ||
                    (dc > _minDist + 0.5f && _minDist <= _look + 1f))
                {
                    _arriving = true;
                }
            }

            return true;
        }

        // ---- the centre rule ------------------------------------------------
        //
        // Returns whether the shot is still flying.
        private bool AtCentre(Vector2 pos)
        {
            _arriving = false;

            switch (cfg.atCentre)
            {
                case ForgeBloom.Centre.Through:
                {
                    _leg = Leg.Through;
                    _flown = 0f;
                    _lastPos = pos;
                    _committed = false;

                    _dir = (_endDir.sqrMagnitude > 0.000001f)
                        ? _endDir.normalized : _dir;

                    if (_p != null)
                    {
                        ForgeSteer.SetHeading(_p, _dir, _speed);
                        _lastIdeal = _dir;
                    }

                    return true;
                }

                case ForgeBloom.Centre.Burst:
                    Finish(pos, true);
                    return false;

                default:
                    Finish(pos, false);
                    return false;
            }
        }

        // A shot with no centre to end at - past it, turned off its path, or
        // never on one that ends - flies as far as the gun reaches, the way
        // its range would have ended it. Its own lifetime still applies too:
        // the engine runs that, and it is left alone.
        private bool CountDown(Vector2 pos)
        {
            _flown += (pos - _lastPos).magnitude;
            _lastPos = pos;

            if (_flown >= _reach)
            {
                Finish(pos, false);
                return false;
            }

            return true;
        }

        private void GoFree(Vector2 pos)
        {
            _leg = Leg.Free;
            _arriving = false;
            _flown = Vector2.Distance(pos, _start);
            _lastPos = pos;
        }

        // ---- steering --------------------------------------------------------

        private Vector2 NextOnSpiral(float dt)
        {
            float next = _along + _speed * dt;

            if (next >= (float)_spiral.length)
            {
                _along = (float)_spiral.length;
                _arriving = true;
                return _centre;
            }

            _along = next;

            return ForgeBloom.SpiralAt(ref _spiral, next, ref _phi);
        }

        private Vector2 OrbitPoint(float along)
        {
            double x, y;

            ForgeBloomPath.OnCircle(
                _centre.x, _centre.y, _r0, _theta0, _sign, along,
                out x, out y);

            return new Vector2((float)x, (float)y);
        }

        // A plain shot: the velocity that lands it on `target` this step.
        // Through ForgeSteer.Drive, which owns direction outright.
        private void Aim(Vector2 pos, Vector2 target, float dt)
        {
            Vector2 v = (target - pos) / dt;

            ForgeSteer.Drive(_p, v);

            if (v.sqrMagnitude > 0.000001f)
                _lastIdeal = v.normalized;
        }

        // A missile: write the velocity that lands the body on `target` this
        // step, and turn its nose along it.
        //
        // ★ DAMPING IS APPLIED AFTER THE WRITE. Box2D damps a body each step as
        // v *= 1 / (1 + dt * linearDamping) before it integrates the position
        // (ForgeBallistics replicates the same step), and the stock rocket's
        // damping is 3 - so a bare write would land 6% short every step and the
        // rocket would trail behind its own path. Writing the damped-UP value
        // lands it exactly. A rocket has no gravity, which is why this whole
        // mode is allowed for it and refused for a grenade.
        private void Fly(Vector2 pos, Vector2 target, float dt)
        {
            Vector2 v = (target - pos) / dt;

            _rb.linearVelocity = v * (1f + dt * _damping);
            _rb.angularVelocity = 0f;

            if (v.sqrMagnitude > 0.000001f)
                _rb.rotation = Mathf.Atan2(v.y, v.x) * Mathf.Rad2Deg;
        }

        // ---- the end ----------------------------------------------------------
        //
        // `burst`: the impact's hooks. Otherwise the flight's end: what its
        // range or lifetime would have fired - a rocket's explosion, a Popper's
        // nothing at all.
        private void Finish(Vector2 pos, bool burst)
        {
            if (_ending)
                return;

            _ending = true;
            live = false;

            Vector2 heading = (_rb != null)
                ? _rb.linearVelocity
                : ((_p != null) ? _p.Velocity : _dir);

            if (heading.sqrMagnitude < 0.000001f)
                heading = _dir;

            try
            {
                // Nothing else may end it a SECOND time on the way out: the
                // engine's lifetime handler runs in Update, and a rocket left
                // simulated would still collide in the physics step to come.
                ProjectileLifetimeData ld = _ip.LifetimeData;
                ld.enabled = false;
                _ip.LifetimeData = ld;

                if (_rb != null)
                {
                    _rb.linearVelocity = Vector2.zero;
                    _rb.angularVelocity = 0f;
                    _rb.simulated = false;
                }

                Component shot = (_p != null) ? (Component)_p : _pp;

                if (burst)
                {
                    ForgeBloomHooks.Impact(shot, _ip, _impact, pos, heading);
                }
                else
                {
                    ForgeBloomHooks.FlightEnd(
                        shot, _ip, _range, _life, pos, transform.rotation);
                }
            }
            catch (Exception e)
            {
                // A cosmetic hook must never cost the end itself.
                if (!_saidHookFail)
                {
                    _saidHookFail = true;
                    Log.LogWarning(
                        "bloom bullet: an end-of-flight hook failed: " +
                        e.Message + ". Said once per run.");
                }
            }

            Destroy(gameObject);
        }
    }
}
