using System;
using System.Collections.Generic;
using UnityEngine;

namespace WeaponForge
{
    // Where the beam actually goes.
    //
    // ONE INTEGRATOR, THREE ROUTERS. Every mode walks the same loop - step
    // forward by `segment`, turn toward what you want, never turning faster
    // than `turnRate` degrees per unit travelled. The routing mode only decides
    // what "what you want" points at:
    //
    //   steer          straight at the target, nudged aside by whatever
    //                  surface is in the way
    //   path           at the next waypoint of a corridor found through the
    //                  cell grid
    //   steerThenPath  the first, and if that fails to arrive, the second
    //
    // Because the turn limit is applied AFTER the router speaks, even a
    // pathfound route comes out as a smooth curve rather than a chain of
    // corners. And because everything downstream just gets a list of points,
    // the chain renderer and the damage pass never learn which router ran.
    //
    // WHY DEGREES PER UNIT AND NOT PER SECOND. A beam is a shape, not a
    // motion. Per-unit makes the tightest circle it can draw - 360/turnRate
    // units of circumference - independent of frame rate, of how fast the ship
    // is moving, and of the weapon's fire rate. Per-second would have made the
    // same weapon curve differently on a better computer.
    public static class ForgeCurveBeamPath
    {
        // Hard cap on links. At the default 0.4 segment this is 64 units of
        // beam, comfortably past every stock laser's range. It exists so a
        // silly `segment: 0.01` shortens the beam instead of costing frames.
        public const int MaxPoints = 160;

        public class Result
        {
            public readonly List<Vector2> points = new List<Vector2>();

            // Did it get where it was going? False means it ran out of length,
            // ran out of links, or dead-ended against terrain.
            public bool reached;

            // Dead-ended specifically (as opposed to simply running out of
            // beam). This is what triggers the pathfinding fallback.
            public bool stuck;

            public Vector2 End
            {
                get
                {
                    return points.Count > 0
                        ? points[points.Count - 1]
                        : Vector2.zero;
                }
            }

            public Vector2 EndHeading
            {
                get
                {
                    if (points.Count < 2)
                        return Vector2.right;

                    Vector2 d = points[points.Count - 1] - points[points.Count - 2];

                    return d.sqrMagnitude > 0.000001f
                        ? d.normalized
                        : Vector2.right;
                }
            }

            public void Clear()
            {
                points.Clear();
                reached = false;
                stuck = false;
            }
        }

        // Scratch, reused. The solver runs at most a few times per frame and
        // never reentrantly (Unity is single-threaded here).
        private static readonly List<Vector2> _waypoints = new List<Vector2>();

        // ------------------------------------------------------------------
        // The entry point.
        //
        // `target` is a world position, or null for "no lock" - in which case
        // the beam simply keeps going the way it was pointed, still sliding
        // around anything in the way. `seed` is last frame's heading, used for
        // smoothing; pass the aim direction on the first frame.
        // ------------------------------------------------------------------
        public static void Build(
            Result result,
            ForgeCurveBeam.Config cfg,
            Vector2 origin,
            Vector2 aim,
            Vector2? target,
            float maxLength,
            int groundMask,
            Vector2 seed)
        {
            Build(result, cfg, origin, aim, target, maxLength, groundMask,
                  seed, null);
        }

        // `weave` is an optional corridor of enemy positions to collect on the
        // way, supplied by the caller (which is the half that needs to know
        // about factions and targets). It is used as waypoints, with the
        // locked target appended last - so the turn limit, the terrain
        // avoidance and the damage pass all behave exactly as before.
        public static void Build(
            Result result,
            ForgeCurveBeam.Config cfg,
            Vector2 origin,
            Vector2 aim,
            Vector2? target,
            float maxLength,
            int groundMask,
            Vector2 seed,
            List<Vector2> weave)
        {
            result.Clear();

            if (cfg == null)
                return;

            if (aim.sqrMagnitude < 0.000001f)
                aim = Vector2.right;

            aim = aim.normalized;

            // Smoothing blends the START of the curve toward where it was last
            // frame. Applied to the seed rather than to every point, because
            // the turn limit already carries that continuity down the length -
            // smoothing each point separately would fight the limit and put a
            // kink in the middle of the beam.
            float s = Mathf.Clamp01(cfg.smoothing);

            Vector2 heading =
                (s > 0f && seed.sqrMagnitude > 0.000001f)
                    ? Vector2.Lerp(aim, seed.normalized, s).normalized
                    : aim;

            _waypoints.Clear();

            // WEAVING WINS OVER PATHFINDING when both are asked for, and that
            // is a deliberate choice rather than a limitation: routing a
            // corridor to each enemy in turn would be several A* searches per
            // frame, and the terrain avoidance in the integrator already keeps
            // the detours out of walls. The `steerThenPath` rescue below still
            // applies if a weave genuinely dead-ends.
            bool weaving = weave != null && weave.Count > 0 && target.HasValue;

            if (weaving)
            {
                _waypoints.AddRange(weave);

                // The lock target is always last, so the beam still ENDS where
                // you aimed it however many detours it took on the way.
                _waypoints.Add(target.Value);
            }
            else if (target.HasValue &&
                     cfg.route == ForgeCurveBeam.RoutePath)
            {
                Route(origin, target.Value, cfg, _waypoints);
            }

            Integrate(
                result, cfg, origin, heading, target, maxLength, groundMask,
                _waypoints.Count > 0 ? _waypoints : null);

            // The fallback. Only worth doing when steering actually dead-ended
            // - running out of beam length is not a routing failure, and
            // pathfinding would not lengthen the beam.
            if (!result.reached && result.stuck &&
                target.HasValue &&
                cfg.route == ForgeCurveBeam.RouteSteerThenPath)
            {
                _waypoints.Clear();

                if (Route(origin, target.Value, cfg, _waypoints))
                {
                    Integrate(
                        result, cfg, origin, heading, target, maxLength,
                        groundMask, _waypoints);
                }
            }
        }

        // ------------------------------------------------------------------
        // The integrator
        // ------------------------------------------------------------------
        private static void Integrate(
            Result result,
            ForgeCurveBeam.Config cfg,
            Vector2 origin,
            Vector2 heading,
            Vector2? target,
            float maxLength,
            int groundMask,
            List<Vector2> waypoints)
        {
            result.Clear();

            float step = cfg.Segment;
            float maxTurn = cfg.Turn * step;      // degrees allowed per link
            float clearance = Mathf.Max(0.05f, cfg.clearance);
            float look = Mathf.Max(step, cfg.lookAhead);

            // "Arrived" has to be generous enough that a beam turning at its
            // limit can actually close the last bit rather than orbiting just
            // outside forever. One link plus the clearance is the smallest
            // honest answer.
            float arrive = step + clearance;

            Vector2 here = origin;

            result.points.Add(here);

            int waypoint = 0;
            int onWaypoint = 0;
            float travelled = 0f;

            // Links the beam may spend chasing ONE waypoint before giving up on
            // it and moving to the next. A weave detour it cannot quite reach -
            // an enemy tucked just inside its turning circle - would otherwise
            // orbit it until the whole beam is used up, and the beam would stop
            // short of the thing you actually locked. A full tight circle at
            // the default turn rate is about 22 links, so 30 is "it had its
            // chance".
            const int WaypointBudget = 30;

            // Dead-end detection. A beam pressed into a corner stops making
            // progress long before it runs out of length, and without this it
            // would spend all 160 links scribbling in the same spot.
            float bestDistance = target.HasValue
                ? Vector2.Distance(here, target.Value)
                : float.MaxValue;

            int sinceProgress = 0;

            while (travelled < maxLength && result.points.Count < MaxPoints)
            {
                // ---- what do we want to do from here --------------------
                Vector2 desired = heading;

                if (waypoints != null && waypoints.Count > 0)
                {
                    // Advance through the corridor. The test is generous on
                    // purpose: a waypoint the beam cannot quite reach must not
                    // stall the whole path, and the turn limit means it will
                    // often cut a corner slightly.
                    onWaypoint++;

                    // Spent long enough on this one - take the next. See
                    // WaypointBudget above.
                    if (waypoint < waypoints.Count - 1 &&
                        onWaypoint > WaypointBudget)
                    {
                        waypoint++;
                        onWaypoint = 0;
                    }

                    while (waypoint < waypoints.Count - 1 &&
                           Vector2.Distance(here, waypoints[waypoint]) <
                               arrive * 1.5f)
                    {
                        onWaypoint = 0;
                        waypoint++;
                    }

                    Vector2 to = waypoints[waypoint] - here;

                    if (to.sqrMagnitude > 0.000001f)
                        desired = to.normalized;
                }
                else if (target.HasValue)
                {
                    Vector2 to = target.Value - here;

                    if (to.sqrMagnitude > 0.000001f)
                        desired = to.normalized;
                }

                // ---- is anything in the way -----------------------------
                bool blocked;

                desired = Slide(
                    here, heading, desired, look, clearance, groundMask,
                    out blocked);

                // `blocked` is load-bearing for performance, not just for
                // logic. When Slide found nothing within lookAhead along the
                // direction we want, the heading it returns is that direction
                // unchanged - so the shorter Obstructed() cast below is
                // provably clear and can be skipped. That is the difference
                // between one cast per link in open space and three.

                // ---- turn, but only so fast -----------------------------
                float delta = Vector2.SignedAngle(heading, desired);

                if (maxTurn > 0f)
                    delta = Mathf.Clamp(delta, -maxTurn, maxTurn);
                else
                    delta = 0f;

                heading = Rotate(heading, delta);

                Vector2 next = here + heading * step;

                // Walking INTO a surface rather than along it. One last try at
                // sliding from the new heading; if that is blocked too, this
                // is a genuine dead end and the beam stops at the wall - which
                // is exactly what a normal beam would have done anyway.
                if (blocked &&
                    Obstructed(here, heading, step, clearance, groundMask))
                {
                    bool again;

                    Vector2 escape = Slide(
                        here, heading, heading, look, clearance, groundMask,
                        out again);

                    float d2 = Mathf.Clamp(
                        Vector2.SignedAngle(heading, escape), -maxTurn, maxTurn);

                    Vector2 retry = Rotate(heading, d2);

                    if (Obstructed(here, retry, step, clearance, groundMask))
                    {
                        result.stuck = true;
                        break;
                    }

                    heading = retry;
                    next = here + heading * step;
                }

                here = next;
                travelled += step;

                result.points.Add(here);

                if (target.HasValue)
                {
                    float d = Vector2.Distance(here, target.Value);

                    if (d <= arrive)
                    {
                        result.reached = true;
                        break;
                    }

                    // Progress is measured against the BEST distance so far,
                    // not the last one. A beam swinging wide to get around a
                    // wall legitimately moves away for a while; what marks a
                    // dead end is never getting closer than it already was.
                    if (d < bestDistance - 0.01f)
                    {
                        bestDistance = d;
                        sinceProgress = 0;
                    }
                    else
                    {
                        sinceProgress++;

                        // Generous: a full tight circle at the default turn
                        // rate is about 22 links, and going right around
                        // something is a legitimate way to reach it.
                        if (sinceProgress > 48)
                        {
                            result.stuck = true;
                            break;
                        }
                    }
                }
            }
        }

        // Nudge `desired` aside if there is a surface in front. Returns the
        // direction to want; `blocked` reports whether anything was found.
        //
        // The rule is the classic one: take the surface tangent whose sign
        // agrees with where we were already going, then lean a little along the
        // normal so the beam does not graze. Blended by how close the surface
        // is, so distant walls bend the beam gently and near ones hard.
        private static Vector2 Slide(
            Vector2 here,
            Vector2 heading,
            Vector2 desired,
            float look,
            float clearance,
            int groundMask,
            out bool blocked)
        {
            blocked = false;

            if (groundMask == 0)
                return desired;

            RaycastHit2D hit = Physics2D.CircleCast(
                here, clearance, desired, look, groundMask);

            if (hit.collider == null)
                return desired;

            blocked = true;

            Vector2 n = hit.normal;

            if (n.sqrMagnitude < 0.000001f)
                return desired;

            // Both tangents of the surface; pick the one we are already
            // closest to travelling along, so the beam keeps its handedness
            // instead of flip-flopping around a pillar.
            Vector2 t1 = new Vector2(-n.y, n.x);
            Vector2 t2 = -t1;

            Vector2 tangent =
                Vector2.Dot(t1, heading) >= Vector2.Dot(t2, heading) ? t1 : t2;

            Vector2 escape = (tangent + n * 0.45f).normalized;

            // 0 at the far end of the look-ahead, 1 right against the surface.
            float urgency = 1f - Mathf.Clamp01(hit.distance / Mathf.Max(0.0001f, look));

            return Vector2.Lerp(desired, escape, urgency).normalized;
        }

        private static bool Obstructed(
            Vector2 here,
            Vector2 dir,
            float step,
            float clearance,
            int groundMask)
        {
            if (groundMask == 0)
                return false;

            return Physics2D.CircleCast(
                here, clearance * 0.6f, dir, step, groundMask).collider != null;
        }

        public static Vector2 Rotate(Vector2 v, float degrees)
        {
            float r = degrees * Mathf.Deg2Rad;
            float cos = Mathf.Cos(r);
            float sin = Mathf.Sin(r);

            return new Vector2(v.x * cos - v.y * sin, v.x * sin + v.y * cos);
        }

        // ------------------------------------------------------------------
        // The router - A* across the cell grid
        //
        // CELL COORDINATES ARE WORLD COORDINATES in this game (the explosion
        // code compares a world position straight against a cell index), so
        // there is no transform to get wrong. A cell is walkable when it is
        // out of bounds - the level edges are open sky - or empty.
        // ------------------------------------------------------------------
        private static readonly Dictionary<int, int> _cameFrom =
            new Dictionary<int, int>();

        private static readonly Dictionary<int, float> _cost =
            new Dictionary<int, float>();

        private static readonly List<int> _open = new List<int>();

        // Membership as a set as well as a list. Without this, the
        // "have I already queued this node" test is a linear scan inside the
        // neighbour loop - O(n^2) over the whole search, and this runs every
        // frame that a beam is firing.
        private static readonly HashSet<int> _openSet = new HashSet<int>();

        private static readonly List<Vector2> _raw = new List<Vector2>();

        // THE ROUTE IS CACHED, and it has to be. A* across a few thousand
        // cells is fine once; sixty times a second, per beam, it is not. The
        // corridor only changes when one of its ENDS moves to a different
        // cell, so that plus a short timer (for terrain that gets blown up
        // underneath it) is enough.
        private static int _cacheStart = int.MinValue;
        private static int _cacheGoal = int.MinValue;
        private static float _cacheAt = -999f;
        private static bool _cacheOk;

        private static readonly List<Vector2> _cached = new List<Vector2>();

        private const int Origin = 1 << 14;   // keeps packed keys positive

        private static int Key(int x, int y)
        {
            return (x + Origin) * (Origin * 2) + (y + Origin);
        }

        private static void UnKey(int key, out int x, out int y)
        {
            y = (key % (Origin * 2)) - Origin;
            x = (key / (Origin * 2)) - Origin;
        }

        public static bool Route(
            Vector2 from,
            Vector2 to,
            ForgeCurveBeam.Config cfg,
            List<Vector2> into)
        {
            into.Clear();

            Level level;

            if (!ServiceLocator.TryGet<Level>(out level) || level == null)
                return false;

            int sx = Mathf.RoundToInt(from.x);
            int sy = Mathf.RoundToInt(from.y);
            int gx = Mathf.RoundToInt(to.x);
            int gy = Mathf.RoundToInt(to.y);

            // Standing in a wall, or the target is inside one. Nothing to
            // route; the steering fallback reads better than a refusal.
            if (!Walkable(level, sx, sy) || !Walkable(level, gx, gy))
                return false;

            int start = Key(sx, sy);
            int goal = Key(gx, gy);

            if (start == goal)
                return false;

            if (start == _cacheStart && goal == _cacheGoal &&
                Time.time - _cacheAt < 0.25f)
            {
                into.AddRange(_cached);
                return _cacheOk;
            }

            _cacheStart = start;
            _cacheGoal = goal;
            _cacheAt = Time.time;
            _cacheOk = false;
            _cached.Clear();

            _cameFrom.Clear();
            _cost.Clear();
            _open.Clear();
            _openSet.Clear();

            _cost[start] = 0f;
            _open.Add(start);
            _openSet.Add(start);

            // A budget rather than a radius: the interesting failures are long
            // thin mazes, where a radius would give up while a node count
            // finishes. 2000 covers a very large area - a 60-unit beam is at
            // most about 120 cells of corridor - and it bounds the one part of
            // this search that is still quadratic, the linear scan for the best
            // open node. A heap would remove that; at this budget, behind the
            // cache above, it is not worth the extra moving part.
            const int Budget = 2000;

            int expanded = 0;
            bool found = false;

            while (_open.Count > 0 && expanded < Budget)
            {
                // Linear scan for the best open node. A heap would be faster
                // in theory; at this budget the scan is not measurable, and it
                // keeps this readable.
                int bestAt = 0;
                float bestScore = float.MaxValue;

                for (int i = 0; i < _open.Count; i++)
                {
                    int k = _open[i];
                    int kx, ky;
                    UnKey(k, out kx, out ky);

                    float g;
                    _cost.TryGetValue(k, out g);

                    float f = g + Heuristic(kx, ky, gx, gy);

                    if (f < bestScore)
                    {
                        bestScore = f;
                        bestAt = i;
                    }
                }

                int current = _open[bestAt];
                _open.RemoveAt(bestAt);
                _openSet.Remove(current);

                if (current == goal)
                {
                    found = true;
                    break;
                }

                expanded++;

                int cx, cy;
                UnKey(current, out cx, out cy);

                float baseCost;
                _cost.TryGetValue(current, out baseCost);

                for (int dx = -1; dx <= 1; dx++)
                {
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        if (dx == 0 && dy == 0)
                            continue;

                        int nx = cx + dx;
                        int ny = cy + dy;

                        if (!Walkable(level, nx, ny))
                            continue;

                        // No cutting corners diagonally through a wall
                        // join - a beam that squeezed through the touching
                        // corner of two blocks would look like it clipped.
                        if (dx != 0 && dy != 0 &&
                            (!Walkable(level, cx + dx, cy) ||
                             !Walkable(level, cx, cy + dy)))
                        {
                            continue;
                        }

                        float stepCost = (dx != 0 && dy != 0) ? 1.41421f : 1f;
                        float g = baseCost + stepCost;

                        int nk = Key(nx, ny);

                        float known;

                        if (_cost.TryGetValue(nk, out known) && known <= g)
                            continue;

                        _cost[nk] = g;
                        _cameFrom[nk] = current;

                        if (_openSet.Add(nk))
                            _open.Add(nk);
                    }
                }
            }

            if (!found)
                return false;

            // Walk it back.
            _raw.Clear();

            int node = goal;
            int guard = 0;

            while (guard++ < 4096)
            {
                int nx, ny;
                UnKey(node, out nx, out ny);

                _raw.Add(new Vector2(nx, ny));

                if (node == start)
                    break;

                if (!_cameFrom.TryGetValue(node, out node))
                    break;
            }

            _raw.Reverse();

            Simplify(level, _raw, cfg, into);

            _cached.AddRange(into);
            _cacheOk = into.Count > 0;

            return _cacheOk;
        }

        private static float Heuristic(int x, int y, int gx, int gy)
        {
            // Octile - the honest distance for 8-connected movement. Manhattan
            // would over-estimate diagonals and make the route hug axes.
            float dx = Mathf.Abs(x - gx);
            float dy = Mathf.Abs(y - gy);

            return (dx + dy) + (1.41421f - 2f) * Mathf.Min(dx, dy);
        }

        private static bool Walkable(Level level, int x, int y)
        {
            // Out of bounds is open sky, not a wall.
            if (!level.ContainsCell(x, y))
                return true;

            return level.IsEmpty(x, y);
        }

        // String-pull: keep a waypoint only where the corridor actually turns.
        // Without this the beam would be told to aim at a point one cell ahead
        // every step, which the turn limit renders as a wobble rather than a
        // curve.
        private static void Simplify(
            Level level,
            List<Vector2> raw,
            ForgeCurveBeam.Config cfg,
            List<Vector2> into)
        {
            into.Clear();

            if (raw.Count == 0)
                return;

            int anchor = 0;

            into.Add(raw[0]);

            for (int i = 2; i < raw.Count; i++)
            {
                if (Clear(level, raw[anchor], raw[i]))
                    continue;

                // raw[i] is not reachable in a straight line from the anchor,
                // so the last one that WAS becomes a corner.
                anchor = i - 1;
                into.Add(raw[anchor]);
            }

            into.Add(raw[raw.Count - 1]);
        }

        // Is the straight line between two cells free of terrain? Sampled
        // rather than raycast, because the router works in cells and a
        // Physics2D cast would also stop on units, which must not shape the
        // corridor - enemies are what the beam is trying to reach.
        private static bool Clear(Level level, Vector2 a, Vector2 b)
        {
            Vector2 d = b - a;
            float len = d.magnitude;

            if (len < 0.0001f)
                return true;

            int steps = Mathf.CeilToInt(len * 2f);

            for (int i = 1; i < steps; i++)
            {
                Vector2 p = a + d * (i / (float)steps);

                if (!Walkable(
                        level,
                        Mathf.RoundToInt(p.x),
                        Mathf.RoundToInt(p.y)))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
