using System;
using System.Collections.Generic;

namespace WeaponForge
{
    // THE RULES OF DRONE MODES, with no Unity in them.
    //
    // Everything here is arithmetic and bookkeeping: which mode comes next,
    // who can do which mode, what one key press means, and where a companion
    // sits in each formation. It is kept free of UnityEngine on purpose, so
    // `Harness/dronemodeprobe.cs` can compile THIS FILE with the stock C#
    // compiler and run it - the cycle, the act-on-release key and the shapes
    // are proved by running them, not by reading them. The game side
    // (ForgeDroneModes, ForgeDroneBrain, the wingman controller) only ever asks
    // these questions; it never answers them a second way.
    //
    // The design is `DRONE MODES - CONCEPT.md`, decided with Sugarheady over
    // 2026-09-20 .. 2026-10-01.
    public static class ForgeDroneModeRules
    {
        public enum Mode
        {
            Base,
            Orbit,
            Drag,
            Protector,
            RearGuard,
            Seeker,
            Sentry,
            Interceptor
        }

        // What a gadget slot holds, as far as modes care. Two kinds of
        // companion move in two different ways (concept section 3): a wingman
        // is PLACED by Weapon Forge's controller, an AI drone MOVES ITSELF and
        // has its steering taken for a formation.
        public enum Kind
        {
            None,
            AiDrone,     // the game's five drone gadgets, and Forge `drone` files
            WingOrbs,    // a `wingman` file with orb companions
            WingBodies   // a `wingman` file with `body: "drone"`
        }

        // His cycle (answer 3, 2026-09-29) plus SENTRY (answer B).
        // INTERCEPTOR joins after PROTECTOR once it has passed its tests
        // (answer C); until then it is in the cycle only if he adds it.
        public static readonly Mode[] DefaultCycle =
        {
            Mode.Orbit, Mode.Drag, Mode.Protector, Mode.RearGuard,
            Mode.Seeker, Mode.Sentry
        };

        public const string DefaultCycleText =
            "Orbit, Drag, Protector, RearGuard, Seeker, Sentry";

        // The word on the alert: "DRONES: ORBIT", "GADGET 2: NORMAL".
        public static string Word(Mode m)
        {
            switch (m)
            {
                case Mode.Orbit: return "ORBIT";
                case Mode.Drag: return "DRAG";
                case Mode.Protector: return "PROTECTOR";
                case Mode.RearGuard: return "REAR GUARD";
                case Mode.Seeker: return "SEEKER";
                case Mode.Sentry: return "SENTRY";
                case Mode.Interceptor: return "INTERCEPTOR";
                default: return "NORMAL";
            }
        }

        // For the save file and the log - one token, no spaces.
        public static string Token(Mode m)
        {
            return m.ToString();
        }

        // A cycle / join / save word to a mode. Spaces, dashes and underscores
        // are ignored and case does not matter, so "Rear Guard", "rear_guard"
        // and "REARGUARD" are one name. A few plain-English aliases too, since
        // this is typed by hand into a .cfg.
        public static bool TryParse(string word, out Mode mode)
        {
            mode = Mode.Base;

            if (word == null)
                return false;

            string w = word.Trim().ToLowerInvariant()
                .Replace(" ", "").Replace("-", "").Replace("_", "");

            switch (w)
            {
                case "base":
                case "normal":
                case "none":
                case "default":
                    mode = Mode.Base;
                    return true;
                case "orbit":
                case "circle":
                    mode = Mode.Orbit;
                    return true;
                case "drag":
                case "line":
                case "trail":
                    mode = Mode.Drag;
                    return true;
                case "protector":
                case "protect":
                case "front":
                    mode = Mode.Protector;
                    return true;
                case "rearguard":
                case "rear":
                case "back":
                    mode = Mode.RearGuard;
                    return true;
                case "seeker":
                case "seek":
                case "hunt":
                    mode = Mode.Seeker;
                    return true;
                case "sentry":
                case "post":
                    mode = Mode.Sentry;
                    return true;
                case "interceptor":
                case "intercept":
                    mode = Mode.Interceptor;
                    return true;
            }

            return false;
        }

        // `[Drone modes] Cycle` -> the modes the key steps through, in order.
        //
        // ★ HIS ANSWER 3: "with that config can we even omit options for the
        // modes?" The list IS the whole list - a mode not in it is never
        // offered. Base is not written in it: it is always the start and the
        // end of the cycle. Blank (or nothing usable) falls back to the
        // default, said in `problems`, because a key that steps through
        // nothing would read as a broken key.
        public static List<Mode> ParseCycle(string text, List<string> problems)
        {
            var list = new List<Mode>();

            if (string.IsNullOrEmpty(text) || text.Trim().Length == 0)
            {
                if (problems != null)
                    problems.Add("Cycle is blank - using the default (" + DefaultCycleText + ").");

                list.AddRange(DefaultCycle);
                return list;
            }

            foreach (string raw in text.Split(new[] { ',', ';', '|', '>' }))
            {
                string part = raw.Trim();

                if (part.Length == 0)
                    continue;

                Mode m;

                if (!TryParse(part, out m))
                {
                    if (problems != null)
                        problems.Add("Cycle: '" + part + "' is not a mode - left out. The modes are " +
                                     "Orbit, Drag, Protector, RearGuard, Seeker, Sentry and Interceptor.");
                    continue;
                }

                if (m == Mode.Base)
                {
                    if (problems != null)
                        problems.Add("Cycle: '" + part + "' is not needed - normal is always the " +
                                     "start and the end of the cycle.");
                    continue;
                }

                if (list.Contains(m))
                {
                    if (problems != null)
                        problems.Add("Cycle: " + Word(m) + " is listed twice - the second is left out.");
                    continue;
                }

                list.Add(m);
            }

            if (list.Count == 0)
            {
                if (problems != null)
                    problems.Add("Cycle has no mode in it - using the default (" + DefaultCycleText + ").");

                list.AddRange(DefaultCycle);
            }

            return list;
        }

        // `modes.join` in a wingman / drone file: which modes that gadget
        // takes part in. Null means all of them.
        public static HashSet<Mode> ParseJoin(IEnumerable<string> words, List<string> problems)
        {
            var set = new HashSet<Mode>();
            bool any = false;

            foreach (string word in words)
            {
                if (word == null)
                    continue;

                string w = word.Trim();

                if (w.Length == 0)
                    continue;

                any = true;

                if (string.Equals(w, "all", StringComparison.OrdinalIgnoreCase))
                    return null;

                Mode m;

                if (!TryParse(w, out m))
                {
                    if (problems != null)
                        problems.Add("modes.join: '" + w + "' is not a mode - ignored.");
                    continue;
                }

                // "none" / "normal" parse to Base: a gadget that joins no mode
                // at all keeps its own behaviour whatever the key says.
                if (m != Mode.Base)
                    set.Add(m);
            }

            return any ? set : null;
        }

        // ------------------------------------------------------------------
        // Who can do what
        // ------------------------------------------------------------------
        //
        // Concept 5.1 / 4.1 / 12.1: wingmen get the four formations;
        // SEEKER and SENTRY are AI-drone only in the first build (a wingman
        // ring is fixed to the ship); INTERCEPTOR needs a body to block with,
        // so wingman ORBS take part only with the no-damage test option, where
        // an orb deletes the shots it meets.
        public static bool Supports(Kind kind, Mode mode, bool orbsIntercept)
        {
            if (kind == Kind.None)
                return false;

            switch (mode)
            {
                case Mode.Base:
                case Mode.Orbit:
                case Mode.Drag:
                case Mode.Protector:
                case Mode.RearGuard:
                    return true;

                case Mode.Seeker:
                case Mode.Sentry:
                    return kind == Kind.AiDrone;

                case Mode.Interceptor:
                    return kind == Kind.AiDrone || kind == Kind.WingBodies ||
                           (kind == Kind.WingOrbs && orbsIntercept);
            }

            return false;
        }

        // A slot can do a mode: its companions support it AND its file joins
        // it (no `modes.join` = joins everything).
        public static bool Can(Kind kind, HashSet<Mode> join, Mode mode, bool orbsIntercept)
        {
            if (mode == Mode.Base)
                return kind != Kind.None;

            if (!Supports(kind, mode, orbsIntercept))
                return false;

            return join == null || join.Contains(mode);
        }

        public static bool IsFormation(Mode m)
        {
            return m == Mode.Orbit || m == Mode.Drag || m == Mode.Protector ||
                   m == Mode.RearGuard;
        }

        // ------------------------------------------------------------------
        // Stepping
        // ------------------------------------------------------------------
        //
        // The SHARED step (the mode key alone): the ship keeps one index into
        // the cycle, -1 for normal. The next press moves to the next mode that
        // at least ONE installed companion can do, so a ship with only wingman
        // orbs never spends a press on SEEKER; after the last mode it goes back
        // to normal. Every slot is then put into that mode (a slot that cannot
        // do it stays on its normal behaviour). That is the shoot-em-up case,
        // and it also lines the slots back up after combos split them.
        public static int NextShared(IList<Mode> cycle, int index, Func<Mode, bool> anyCan)
        {
            if (cycle == null || cycle.Count == 0)
                return -1;

            int from = (index < -1 || index >= cycle.Count) ? -1 : index;

            for (int i = from + 1; i < cycle.Count; i++)
            {
                if (anyCan == null || anyCan(cycle[i]))
                    return i;
            }

            return -1;
        }

        // The SLOT step (the mode key + a gadget's own button): that slot
        // alone, from wherever it is, to the next mode IT can do - "Y + 2
        // again and again walks slot 2 through ORBIT, DRAG, PROTECTOR ...
        // SENTRY and back to its base". A slot sitting on a mode that is not
        // in the cycle (the .cfg changed under a save) starts again from base.
        public static Mode NextForSlot(IList<Mode> cycle, Mode current, Func<Mode, bool> can)
        {
            if (cycle == null || cycle.Count == 0)
                return Mode.Base;

            int at = current == Mode.Base ? -1 : IndexOf(cycle, current);

            for (int i = at + 1; i < cycle.Count; i++)
            {
                if (can == null || can(cycle[i]))
                    return cycle[i];
            }

            return Mode.Base;
        }

        public static int IndexOf(IList<Mode> cycle, Mode m)
        {
            if (cycle == null)
                return -1;

            for (int i = 0; i < cycle.Count; i++)
                if (cycle[i] == m)
                    return i;

            return -1;
        }

        // ------------------------------------------------------------------
        // One press of the key
        // ------------------------------------------------------------------
        //
        // ★ THE SHARED KEY ACTS WHEN IT IS RELEASED, NOT PRESSED (concept 2).
        // Y alone - pressed and let go with no gadget button in between - steps
        // every slot. Y used as the first half of a combo does nothing on its
        // own. Holding Y about a second with nothing else pressed puts every
        // slot back on normal (12.2 M, `HoldForNormal`). The back-to-normal key
        // (U) works at any time.
        //
        // One machine per ship, ticked once a frame with what that ship's
        // player is doing. It returns at most one thing to do per frame.
        public enum Act { None, StepAll, StepSlot, Normal }

        public sealed class KeyMachine
        {
            public bool holdForNormal = true;
            public double holdSeconds = 1.0;

            private bool _down;
            private bool _combo;
            private bool _held;
            private double _downAt;

            public bool Down { get { return _down; } }

            // `flying` false (a menu, the map, a shop, a pause) cancels a
            // press in progress without acting, so a Y held into the ship
            // menu does not step the drones when it is let go there.
            public Act Tick(
                bool flying, bool modeHeld, int comboSlot, bool normalPressed,
                double now, out int slot)
            {
                slot = 0;

                if (!flying)
                {
                    _down = false;
                    _combo = false;
                    _held = false;
                    return Act.None;
                }

                if (normalPressed)
                {
                    // A normal press during a hold also uses the hold up, so
                    // letting go of Y afterwards does not step everything.
                    if (_down)
                        _combo = true;

                    return Act.Normal;
                }

                if (modeHeld && !_down)
                {
                    _down = true;
                    _combo = false;
                    _held = false;
                    _downAt = now;
                }

                if (_down && comboSlot >= 1 && comboSlot <= 3)
                {
                    _combo = true;
                    slot = comboSlot;

                    if (!modeHeld)
                        _down = false;

                    return Act.StepSlot;
                }

                if (_down && modeHeld)
                {
                    if (holdForNormal && !_combo && !_held && now - _downAt >= holdSeconds)
                    {
                        _held = true;
                        return Act.Normal;
                    }

                    return Act.None;
                }

                if (_down && !modeHeld)
                {
                    _down = false;

                    if (!_combo && !_held)
                        return Act.StepAll;
                }

                return Act.None;
            }
        }

        // ------------------------------------------------------------------
        // The shapes
        // ------------------------------------------------------------------
        //
        // ★ HIS RULE, 2026-10-01 (12.2 L): alert only - "Ideally the player
        // should be able to tell what mode the drones are in base on the drone
        // behavior." So the four formations are four DIFFERENT shapes, not
        // four clumps:
        //
        //   ORBIT       a full ring round you that keeps turning
        //   DRAG        a single-file line along the path you flew
        //   PROTECTOR   a short, tight row close in FRONT of where you aim
        //   REAR GUARD  a longer, wider row further BEHIND where you aim
        //
        // DRAG follows where you FLY and the two screens follow where you AIM
        // (answer 3), so DRAG is a column and REAR GUARD is a row even when
        // you fly the way you aim.
        public struct P
        {
            public float x;
            public float y;

            public P(float x, float y)
            {
                this.x = x;
                this.y = y;
            }

            public static P operator +(P a, P b) { return new P(a.x + b.x, a.y + b.y); }
            public static P operator -(P a, P b) { return new P(a.x - b.x, a.y - b.y); }
            public static P operator *(P a, float k) { return new P(a.x * k, a.y * k); }

            public float Length { get { return (float)Math.Sqrt(x * x + y * y); } }

            public P Unit
            {
                get
                {
                    float l = Length;
                    return l > 1e-5f ? new P(x / l, y / l) : new P(1f, 0f);
                }
            }

            // A quarter turn anticlockwise.
            public P Left { get { return new P(-y, x); } }
        }

        // The numbers behind the shapes. `[Drone modes]` in the .cfg sets the
        // defaults; a wingman or drone file's `modes` block can set its own.
        public sealed class Shape
        {
            public float orbitRadius = 3.5f;
            public float dragSpacing = 1.6f;
            public float protectorDistance = 2.6f;
            public float rearGuardDistance = 4.5f;
            public float spacing = 1.3f;
            public float interceptRadius = 3f;

            // Drone files only (wingmen have neither mode). <= 0 = the .cfg's.
            public float seekRange = -1f;
            public float sentryRadius = -1f;

            // Null = joins every mode.
            public HashSet<Mode> join;

            public Shape Copy()
            {
                return new Shape
                {
                    orbitRadius = orbitRadius,
                    dragSpacing = dragSpacing,
                    protectorDistance = protectorDistance,
                    rearGuardDistance = rearGuardDistance,
                    spacing = spacing,
                    interceptRadius = interceptRadius,
                    seekRange = seekRange,
                    sentryRadius = sentryRadius,
                    join = join == null ? null : new HashSet<Mode>(join)
                };
            }
        }

        // Companions closer than this round a ring are a clump, so a crowded
        // ring widens to fit (12.2 fill): three slots of boosted drones on one
        // ORBIT get a bigger circle rather than overlapping bodies.
        public const float MinGap = 1.3f;

        // How far the first DRAG companion trails the ship.
        public const float DragLead = 2.2f;

        public static float RingRadius(float radius, int n)
        {
            float need = n * MinGap / (2f * (float)Math.PI);
            return Math.Max(radius, need);
        }

        // ORBIT: member i of n, evenly round one ring that turns as a whole.
        public static P Orbit(P ship, float phaseDegrees, int i, int n, float radius)
        {
            if (n <= 0)
                return ship;

            float r = RingRadius(radius, n);
            double a = (phaseDegrees + i * 360.0 / n) * Math.PI / 180.0;

            return ship + new P((float)Math.Cos(a), (float)Math.Sin(a)) * r;
        }

        // PROTECTOR / REAR GUARD: a row across `facing`, `distance` out from
        // the ship, `spacing` apart, its ends bowed back toward the ship so it
        // reads as a shield rather than a ruler. PROTECTOR passes the aim and
        // REAR GUARD the reverse of it.
        public static P Screen(P ship, P facing, int i, int n, float distance, float spacing)
        {
            P f = facing.Unit;
            P across = f.Left;

            float off = (i - (n - 1) * 0.5f) * spacing;
            float bow = 0.1f * off * off;

            return ship + f * distance + across * off - f * bow;
        }

        // The row widths, so the two screens differ in size as well as side.
        public const float RearGuardWiden = 1.35f;

        // DRAG: the point `back` units behind the ship ALONG THE PATH IT FLEW,
        // so the line follows your turns like a tail. `path` is oldest first,
        // newest last; past the end of what has been recorded the line carries
        // straight on from its last direction.
        public static P AlongPath(IList<P> path, P ship, float back)
        {
            P here = ship;
            float left = back;

            if (path != null)
            {
                for (int k = path.Count - 1; k >= 0; k--)
                {
                    P step = path[k] - here;
                    float len = step.Length;

                    if (len < 1e-5f)
                        continue;

                    if (len >= left)
                        return here + step * (left / len);

                    left -= len;
                    here = path[k];
                }

                // Ran out of path: carry on in the direction of its last leg.
                if (path.Count >= 2)
                {
                    P dir = (path[0] - path[1]).Unit;
                    return here + dir * left;
                }
            }

            return here + new P(-1f, 0f) * left;
        }

        public static P Drag(IList<P> path, P ship, int i, float spacing)
        {
            return AlongPath(path, ship, DragLead + i * Math.Max(0.3f, spacing));
        }

        // INTERCEPTOR, at rest: evenly round a guard ring that does NOT turn
        // (so it reads differently from ORBIT), the first one toward the aim.
        public static P GuardIdle(P ship, P aim, int i, int n, float radius)
        {
            if (n <= 0)
                return ship;

            P f = aim.Unit;
            double baseA = Math.Atan2(f.y, f.x);
            double a = baseA + i * 2.0 * Math.PI / n;
            float r = RingRadius(radius, n);

            return ship + new P((float)Math.Cos(a), (float)Math.Sin(a)) * r;
        }

        // ------------------------------------------------------------------
        // INTERCEPTOR - which shots, and where to meet them
        // ------------------------------------------------------------------

        // Is a shot at `from` moving at `v` heading AT the ship - passing
        // within `hitRadius` of it inside `horizon` seconds? `t` is when it
        // comes closest.
        public static bool Threat(P ship, float hitRadius, P from, P v, float horizon, out float t)
        {
            t = 0f;

            float vv = v.x * v.x + v.y * v.y;

            if (vv < 1e-4f)
                return false;

            P rel = ship - from;
            t = (rel.x * v.x + rel.y * v.y) / vv;

            if (t <= 0f || t > horizon)
                return false;

            P closest = from + v * t;
            return (closest - ship).Length <= hitRadius;
        }

        // Where a shot first crosses the circle of `radius` round `c`. A shot
        // already inside it is met where it is. False if it never reaches the
        // circle (moving away, or passing outside it).
        public static bool Crossing(P c, float radius, P from, P v, out P at, out float t)
        {
            at = from;
            t = 0f;

            P d = from - c;

            if (d.Length <= radius)
                return true;

            float a = v.x * v.x + v.y * v.y;

            if (a < 1e-6f)
                return false;

            float b = 2f * (d.x * v.x + d.y * v.y);
            float cc = d.x * d.x + d.y * d.y - radius * radius;
            float disc = b * b - 4f * a * cc;

            if (disc < 0f)
                return false;

            float root = (float)Math.Sqrt(disc);
            float t1 = (-b - root) / (2f * a);

            if (t1 < 0f)
                return false;

            t = t1;
            at = from + v * t1;
            return true;
        }

        // How far out an explosive shot has to be met: outside its own blast
        // reach of you, plus a margin (concept 4.1: "or it blows the rocket up
        // in your face"). A plain bullet is met on the guard ring.
        public static float MeetRadius(float guardRadius, float blastReach)
        {
            if (blastReach <= 0f)
                return guardRadius;

            return Math.Max(guardRadius, blastReach + 1.5f);
        }

        // One interceptor per shot, the soonest shot first, each to the
        // closest free interceptor that can get there in time. `reach` is how
        // far an interceptor can move per second. Returns, per member, the
        // index of the threat it takes, or -1.
        public struct Incoming
        {
            public P at;      // where to meet it
            public float t;   // seconds until it is there
        }

        public static int[] Assign(IList<P> members, IList<Incoming> shots, float reach)
        {
            var taken = new int[members == null ? 0 : members.Count];

            for (int i = 0; i < taken.Length; i++)
                taken[i] = -1;

            if (members == null || shots == null || shots.Count == 0)
                return taken;

            var order = new List<int>();

            for (int s = 0; s < shots.Count; s++)
                order.Add(s);

            order.Sort((x, y) => shots[x].t.CompareTo(shots[y].t));

            foreach (int s in order)
            {
                int best = -1;
                float bestD = float.MaxValue;

                for (int m = 0; m < members.Count; m++)
                {
                    if (taken[m] >= 0)
                        continue;

                    float d = (members[m] - shots[s].at).Length;

                    // Too far to get there before the shot does - leave it for
                    // a nearer one, or let it go. A small allowance, because a
                    // body only has to get its edge into the line.
                    if (d > reach * shots[s].t + 0.8f)
                        continue;

                    if (d < bestD)
                    {
                        bestD = d;
                        best = m;
                    }
                }

                if (best >= 0)
                    taken[best] = s;
            }

            return taken;
        }
    }
}
