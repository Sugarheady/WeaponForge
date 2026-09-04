using System;
using BepInEx.Logging;
using UnityEngine;

namespace WeaponForge
{
    // WALL RIDING - a shot that follows terrain instead of ignoring it.
    //
    // The last of the movement behaviours off his 2026-08-27 list, and the one
    // nothing in the game or the mod did: homing steers in open space, bounce
    // and the curve beam go AROUND terrain, and nothing went ALONG it.
    //
    // IT RIDES THE PROJECTILE PREFAB, so every field here is a float, a bool or
    // an int. A config-class reference on a prefab-attached component arrives
    // NULL on every clone - the trap that silently disabled the whole of gas and
    // catalyst once - and plain value types serialize fine, which is why this
    // needs no ForgeConfigTable entry. Same shape as ForgeFade.
    //
    // THE WALL IS FOUND IN THE GRID, NOT BY RAYCAST. Terrain here is a cell grid
    // with cells exactly ONE world unit across (the gas cloud and the swipe both
    // convert with a bare `Mathf.FloorToInt`), so sampling the grid gives an
    // exact answer with no cast, no layer mask, and no dependence on collider
    // shape. A cell counts as wall when its `CellType.colliderType` is
    // `NonTrigger` - which correctly excludes Fog, whose whole trick is having
    // no collider at all.
    //
    // IT NEVER TOUCHES THE WALL, and that is deliberate rather than lucky.
    // Holding a `standoff` gap means the shot's own swept CircleCast never
    // reaches terrain, so it cannot be destroyed by it - no editing the private
    // collisionLayerMask, no forcing piercing on, and terrain damage, digging
    // and cell behaviours all keep working exactly as they did.
    public class ForgeWallRide : MonoBehaviour
    {
        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("WeaponForge.WallRide");

        // ---- tuning, all serialize-safe ---------------------------------
        public float grabRange = 1.6f;   // how near a wall must be to catch
        public float standoff = 0.7f;    // gap held from the surface
        public float turnRate = 540f;    // degrees per second while riding
        public float hug = 2.5f;         // how hard it corrects back to standoff
        public float speedMultiplier = 1f;
        public float rideSeconds;        // 0 = as long as it can
        public int side;                 // 0 auto, -1 left, +1 right
        public bool leaveOnLostWall = true;

        // ---- state -------------------------------------------------------
        [NonSerialized] private Projectile _p;
        [NonSerialized] private Level _level;
        [NonSerialized] private bool _ready;
        [NonSerialized] private bool _riding;
        [NonSerialized] private float _rideStarted;
        [NonSerialized] private float _baseSpeed;
        [NonSerialized] private int _turn;      // the side actually chosen

        private static bool _saidNoLevel;

        // How far ahead to check, and how hard to turn away when it is blocked.
        // 0.12s is about two cells at a typical shot speed - far enough to see a
        // corner, near enough that it does not refuse to enter a doorway.
        private const float LookaheadSeconds = 0.12f;
        private const int LookaheadTries = 5;
        private const float LookaheadStep = 20f * Mathf.Deg2Rad;

        private void Start()
        {
            _p = GetComponent<Projectile>();

            if (_p == null)
                _p = GetComponentInParent<Projectile>();

            if (_p == null)
                return;

            if (!ServiceLocator.TryGet<Level>(out _level) || _level == null)
            {
                if (!_saidNoLevel)
                {
                    _saidNoLevel = true;
                    Log.LogWarning(
                        "wallRide: there is no Level to read terrain from, so " +
                        "shots will fly normally. Said once per run.");
                }

                return;
            }

            _baseSpeed = _p.Velocity.magnitude;
            _turn = side;
            _ready = true;
        }

        private void FixedUpdate()
        {
            if (!_ready || _p == null)
                return;

            try
            {
                Tick();
            }
            catch (Exception e)
            {
                // A steering feature must never strand a shot mid-air with no
                // heading. Give up quietly and let it fly straight.
                Log.LogError("wallRide failed, letting the shot fly: " + e);
                _ready = false;
            }
        }

        private void Tick()
        {
            Vector2 here = transform.position;

            Vector2 away;
            float gap;

            if (!Probe(here, out away, out gap))
            {
                // No wall in reach.
                if (_riding && leaveOnLostWall)
                    _riding = false;

                return;
            }

            if (!_riding)
            {
                _riding = true;
                _rideStarted = Time.time;

                if (_baseSpeed <= 0.01f)
                    _baseSpeed = Mathf.Max(0.01f, _p.Velocity.magnitude);
            }
            else if (rideSeconds > 0f &&
                     Time.time - _rideStarted > rideSeconds)
            {
                // Time is up. Stop steering but do not re-grab, or a shot with
                // a ride limit would stutter along the same wall forever.
                _ready = false;
                return;
            }

            Vector2 heading = Heading();

            if (heading.sqrMagnitude < 0.000001f)
                return;

            // Along the wall: the two tangents are the normal turned either way.
            Vector2 left = new Vector2(-away.y, away.x);
            Vector2 right = new Vector2(away.y, -away.x);

            // WHICH WAY ROUND IS DECIDED ONCE, on the frame it grabs, and then
            // held. Re-deciding per frame makes a shot reverse along the wall
            // the instant its heading crosses the perpendicular - which reads
            // as the shot getting stuck rattling in one spot.
            if (_turn == 0)
                _turn = (Vector2.Dot(heading, left) >= Vector2.Dot(heading, right))
                    ? -1 : 1;

            Vector2 along = (_turn < 0) ? left : right;

            // Correct back toward the standoff band. Positive gap error means
            // too close, so push out along the normal; negative pulls in.
            //
            // The clamp is 0.6, not the 1.5 this first shipped with, and the
            // default hug is 2.5 rather than 4. Simulated against a flat floor,
            // the aggressive pair RANG - the gap wobbled by 0.41 units, which is
            // most of the standoff, and reads as a shot vibrating along the
            // wall. Gentler settles inside 0.11.
            float error = Mathf.Clamp((standoff - gap) * hug, -0.6f, 0.6f);

            Vector2 want = (along + away * error).normalized;

            if (want.sqrMagnitude < 0.000001f)
                return;

            // LOOK WHERE THIS ACTUALLY LEADS, and turn away until it is clear.
            //
            // THE LOCAL NORMAL CANNOT SEE A CORNER, and this is the bug the
            // simulation caught before the feature ever ran: at an INSIDE corner
            // the blended normal points diagonally out of the corner, so the
            // tangent perpendicular to it points diagonally INTO the far face.
            // The shot drove straight through a wall. A single lookahead step
            // fixes it, because the place the shot is heading is inside terrain
            // even though the place it is standing is not.
            float ahead = Mathf.Max(0.05f, _baseSpeed * LookaheadSeconds);

            // Which way is "away from the wall" to rotate.
            float sign =
                Vector2.Dot(Rotate(want, LookaheadStep), away) >
                Vector2.Dot(Rotate(want, -LookaheadStep), away) ? 1f : -1f;

            for (int i = 0; i < LookaheadTries; i++)
            {
                Vector2 probeAt = here + want * ahead;

                Vector2 ignoredNormal;
                float aheadGap;

                if (!Probe(probeAt, out ignoredNormal, out aheadGap) ||
                    aheadGap >= standoff * 0.55f)
                {
                    break;
                }

                want = Rotate(want, sign * LookaheadStep);
            }

            float maxTurn = Mathf.Max(1f, turnRate) * Time.fixedDeltaTime;
            Vector2 next = Vector3.RotateTowards(
                heading, want, maxTurn * Mathf.Deg2Rad, 0f);

            float speed = _baseSpeed * Mathf.Max(0.05f, speedMultiplier);

            // Through ForgeSteer, so the heading and the velocity are published
            // together - writing Velocity alone is overwritten by `wave` and the
            // game's own noise on the very next frame.
            ForgeSteer.SetHeading(_p, next, speed);
        }

        private static Vector2 Rotate(Vector2 v, float radians)
        {
            float c = Mathf.Cos(radians);
            float s = Mathf.Sin(radians);

            return new Vector2(v.x * c - v.y * s, v.x * s + v.y * c);
        }

        // The shot's own base heading, free of whatever wave did to Velocity
        // this frame.
        private Vector2 Heading()
        {
            Vector2 ideal;

            if (ForgeSteer.TryGetIdeal(_p, out ideal))
                return ideal.normalized;

            return (_p.Velocity.sqrMagnitude > 0.000001f)
                ? _p.Velocity.normalized
                : Vector2.zero;
        }

        // Nearest wall within grabRange.
        //
        // `away` is a smoothed "which way is open", built by summing the
        // direction from every solid cell in reach weighted by 1/d^2. A single
        // nearest-cell normal snaps between the four axes and makes a shot
        // judder along a flat wall; the weighted sum is continuous, and it
        // rounds outside corners and eases into inside ones for free.
        private bool Probe(Vector2 here, out Vector2 away, out float gap)
        {
            away = Vector2.zero;
            gap = float.MaxValue;

            float r = Mathf.Max(0.6f, grabRange);
            int minX = Mathf.FloorToInt(here.x - r);
            int maxX = Mathf.CeilToInt(here.x + r);
            int minY = Mathf.FloorToInt(here.y - r);
            int maxY = Mathf.CeilToInt(here.y + r);

            Vector2 sum = Vector2.zero;
            bool found = false;

            for (int x = minX; x <= maxX; x++)
            {
                for (int y = minY; y <= maxY; y++)
                {
                    if (!_level.ContainsCell(x, y) || _level.IsEmpty(x, y))
                        continue;

                    CellType type = _level.GetCellType(x, y);

                    // Only a real collider is a wall. Fog and the other
                    // pass-through types are scenery to ride past, not along.
                    if (type == null ||
                        type.colliderType != CellType.ColliderType.NonTrigger)
                        continue;

                    // Cell (x,y) occupies the unit square with that corner, so
                    // its centre is +0.5 on both axes.
                    Vector2 centre = new Vector2(x + 0.5f, y + 0.5f);
                    Vector2 off = here - centre;
                    float d = off.magnitude;

                    if (d > r)
                        continue;

                    found = true;

                    // Surface distance, not centre distance: half a cell of the
                    // square is between the centre and the face.
                    float surface = Mathf.Max(0f, d - 0.5f);

                    if (surface < gap)
                        gap = surface;

                    float w = 1f / Mathf.Max(0.04f, d * d);
                    sum += (d > 0.0001f ? off / d : Vector2.up) * w;
                }
            }

            if (!found || sum.sqrMagnitude < 0.000001f)
                return false;

            away = sum.normalized;
            return true;
        }
    }
}
