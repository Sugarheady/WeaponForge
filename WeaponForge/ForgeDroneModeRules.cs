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
            Interceptor,

            // R21 part 3 (his answers, 2026-10-09). Added at the END: the
            // save file writes a mode by its NAME, so nothing saved moves.
            InterceptFront,   // INTERCEPTOR resting in the PROTECTOR row
            InterceptRear,    // INTERCEPTOR resting in the REAR GUARD row
            Striker,          // dive at the nearest enemies, fire point blank
            Vengeance         // dive at the enemies shooting at you
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
        // (answer C) - and it did, R21 part 3 test 315: "Pass absolute pass!".
        // The four modes of R21 part 3 (INTERCEPT FRONT / REAR, STRIKER,
        // VENGEANCE) join the same way, once they pass; until then a file
        // that joins one gets it on its OWN gadget's steps (SlotCycle), and
        // the Cycle line can name them.
        public static readonly Mode[] DefaultCycle =
        {
            Mode.Orbit, Mode.Drag, Mode.Protector, Mode.Interceptor, Mode.RearGuard,
            Mode.Seeker, Mode.Sentry
        };

        public const string DefaultCycleText =
            "Orbit, Drag, Protector, Interceptor, RearGuard, Seeker, Sentry";

        // Every mode's name, for the messages that list them.
        public const string AllModesText =
            "Orbit, Drag, Protector, RearGuard, Seeker, Sentry, Interceptor, InterceptFront, " +
            "InterceptRear, Striker and Vengeance";

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
                case Mode.InterceptFront: return "INTERCEPT FRONT";
                case Mode.InterceptRear: return "INTERCEPT REAR";
                case Mode.Striker: return "STRIKER";
                case Mode.Vengeance: return "VENGEANCE";
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
                case "interceptfront":
                case "interceptorfront":
                case "frontintercept":
                    mode = Mode.InterceptFront;
                    return true;
                case "interceptrear":
                case "interceptorrear":
                case "rearintercept":
                case "interceptback":
                    mode = Mode.InterceptRear;
                    return true;
                case "striker":
                case "strike":
                    mode = Mode.Striker;
                    return true;
                case "vengeance":
                case "revenge":
                case "avenger":
                    mode = Mode.Vengeance;
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
                                     AllModesText + ".");
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
        // an orb deletes the shots it meets. INTERCEPT FRONT / REAR are
        // INTERCEPTOR resting in a row, so the same rule. STRIKER and
        // VENGEANCE (R21 part 3) are for every companion: an orb or a body
        // dives in and fires point blank, an AI drone is flown in and fights.
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
                case Mode.Striker:
                case Mode.Vengeance:
                    return true;

                case Mode.Seeker:
                case Mode.Sentry:
                    return kind == Kind.AiDrone;

                case Mode.Interceptor:
                case Mode.InterceptFront:
                case Mode.InterceptRear:
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

        // The three that go for enemy SHOTS (their rest shape differs).
        public static bool IsGuard(Mode m)
        {
            return m == Mode.Interceptor || m == Mode.InterceptFront || m == Mode.InterceptRear;
        }

        // The two that go for enemy UNITS.
        public static bool IsStrike(Mode m)
        {
            return m == Mode.Striker || m == Mode.Vengeance;
        }

        // Every mode whose companions are PUT somewhere by the formation
        // roster (as opposed to SEEKER and SENTRY, which steer an AI drone
        // round its own AI).
        public static bool IsPlaced(Mode m)
        {
            return IsFormation(m) || IsGuard(m) || IsStrike(m);
        }

        // ★ HIS R21 PART 3 ASK (315): "if the drone or wingman weapon.json
        // that has modes.join with interceptor ... it gets added to the drone
        // modes list for that gadget? Maybe not for the global y button but
        // for the gadget cycle (like Y+1 key)." A gadget's OWN steps are the
        // Cycle, then any mode its file joins that the Cycle leaves out, in
        // the order the modes are listed here. The shared key (Y alone) still
        // reads the Cycle only.
        public static List<Mode> SlotCycle(IList<Mode> cycle, HashSet<Mode> join)
        {
            var list = new List<Mode>();

            if (cycle != null)
                list.AddRange(cycle);

            if (join == null)
                return list;

            foreach (Mode m in Enum.GetValues(typeof(Mode)))
            {
                if (m != Mode.Base && join.Contains(m) && !list.Contains(m))
                    list.Add(m);
            }

            return list;
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

            // R21 part 3 (his 315 ask: "can we somehow bump up the rate of how
            // quickly they snap ... what if i wanted a extreme version of
            // this? Can you make a slider for that?"). A percent: 100 is how
            // INTERCEPTOR has always snapped, 400 the extreme. See
            // InterceptTuning for what it turns up.
            public float interceptSpeed = 100f;

            // True when a FILE named its own interceptSpeed. Otherwise the
            // gadget follows the .cfg's (and the settings tab's) live value,
            // not the copy it took when the file was read.
            public bool interceptSpeedSet;

            // STRIKER / VENGEANCE: how far from YOU an enemy may be to be
            // dived at, and how close a companion sits to the one it is on.
            public float strikeRange = 20f;
            public float strikeDistance = 2.5f;

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
                    interceptSpeed = interceptSpeed,
                    interceptSpeedSet = interceptSpeedSet,
                    strikeRange = strikeRange,
                    strikeDistance = strikeDistance,
                    seekRange = seekRange,
                    sentryRadius = sentryRadius,
                    join = join == null ? null : new HashSet<Mode>(join)
                };
            }
        }

        // The ranges the .cfg and a file's `modes` block are clamped to.
        public const float InterceptSpeedMin = 50f;
        public const float InterceptSpeedMax = 400f;
        public const float StrikeRangeMin = 6f;
        public const float StrikeRangeMax = 40f;
        public const float StrikeDistanceMin = 1f;
        public const float StrikeDistanceMax = 8f;

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

        // Where an interceptor waits for a shot, by mode: INTERCEPTOR on its
        // still ring, INTERCEPT FRONT in the PROTECTOR row, INTERCEPT REAR in
        // the REAR GUARD row (his "Two modes" answer, R21 part 3) - each with
        // that row's own distance and spacing.
        public static P GuardRest(Mode m, P ship, P aim, int i, int n, Shape sh)
        {
            if (sh == null)
                sh = new Shape();

            switch (m)
            {
                case Mode.InterceptFront:
                    return Screen(ship, aim, i, n, sh.protectorDistance, sh.spacing);

                case Mode.InterceptRear:
                    return Screen(ship, aim * -1f, i, n, sh.rearGuardDistance,
                                  sh.spacing * RearGuardWiden);

                default:
                    return GuardIdle(ship, aim, i, n, sh.interceptRadius);
            }
        }

        // ------------------------------------------------------------------
        // INTERCEPTOR - which shots, and where to meet them
        // ------------------------------------------------------------------

        // ★ WHAT "HOW QUICKLY THEY SNAP" IS MADE OF (his R21 part 3 question:
        // "What determines how frequently they snap to projectiles?"). They
        // re-decide EVERY frame - there is no rate. What decides how often a
        // shot gets blocked is four numbers, and the slider scales all four:
        //
        //   horizon   how far ahead a shot is noticed (2.5 s at 100%)
        //   corridor  how close to you its line must pass (2.2 units) - wider
        //             takes near misses too
        //   reach     how fast an interceptor is assumed to get there (it is
        //             sent only to shots it can reach in time)
        //   ease      how long a wingman takes to settle on its spot (0.1 s)
        //
        // Square roots on the first two, so 400% is twice the warning and
        // twice the width, not four times - a 10-second horizon would send
        // drones after shots that will never arrive.
        public struct Tuning
        {
            public float horizon;
            public float corridor;
            public float reachScale;
            public float ease;
        }

        public static Tuning InterceptTuning(float speedPercent)
        {
            float k = Math.Max(InterceptSpeedMin, Math.Min(InterceptSpeedMax, speedPercent)) / 100f;
            float root = (float)Math.Sqrt(k);

            return new Tuning
            {
                horizon = 2.5f * root,
                corridor = 2.2f * root,
                reachScale = k,
                ease = 0.1f / k
            };
        }

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

        // ------------------------------------------------------------------
        // STRIKER / VENGEANCE - which enemy each companion dives at
        // ------------------------------------------------------------------
        //
        // His shoot-em-up idea (R21 part 3, 315): "snap to nearby enemies ...
        // with them super close and sort of locking on ... firing point
        // blank." Two modes, his names: STRIKER takes the nearest enemies,
        // VENGEANCE the ones shooting at you. WHICH enemies is the game side's
        // question (it needs the units); everything after the list is here.
        //
        // `targets` is nearest-to-you first. Pass 1: each target, in that
        // order, takes the closest companion still free - so with as many
        // companions as enemies, everybody gets one. Pass 2: a companion left
        // over joins the target nearest to IT, so a lone enemy gets the whole
        // squad. Returns, per member, the index of its target (-1 = none, only
        // when there are no targets), and in `rank` / `count` its place among
        // the companions on that target, so they fan round it instead of
        // stacking.
        public static int[] AssignStrike(IList<P> members, IList<P> targets, out int[] rank, out int[] count)
        {
            int n = members == null ? 0 : members.Count;
            var on = new int[n];
            rank = new int[n];
            count = new int[n];

            for (int i = 0; i < n; i++)
                on[i] = -1;

            if (n == 0 || targets == null || targets.Count == 0)
                return on;

            for (int t = 0; t < targets.Count; t++)
            {
                int best = -1;
                float bestD = float.MaxValue;

                for (int m = 0; m < n; m++)
                {
                    if (on[m] >= 0)
                        continue;

                    float d = (members[m] - targets[t]).Length;

                    if (d < bestD)
                    {
                        bestD = d;
                        best = m;
                    }
                }

                if (best < 0)
                    break;

                on[best] = t;
            }

            for (int m = 0; m < n; m++)
            {
                if (on[m] >= 0)
                    continue;

                int best = 0;
                float bestD = float.MaxValue;

                for (int t = 0; t < targets.Count; t++)
                {
                    float d = (members[m] - targets[t]).Length;

                    if (d < bestD)
                    {
                        bestD = d;
                        best = t;
                    }
                }

                on[m] = best;
            }

            var per = new int[targets.Count];

            for (int m = 0; m < n; m++)
                rank[m] = per[on[m]]++;

            for (int m = 0; m < n; m++)
                count[m] = per[on[m]];

            return on;
        }

        // Where companion `rank` of `count` on one target sits: `distance` from
        // it on the side facing you, the others fanned round it 40 degrees
        // apart - close enough to fire point blank, never inside it.
        public static P StrikeSpot(P target, P ship, int rank, int count, float distance)
        {
            P toShip = ship - target;
            P f = toShip.Length > 1e-4f ? toShip.Unit : new P(-1f, 0f);

            float off = (rank - (Math.Max(1, count) - 1) * 0.5f) * 40f;
            double a = Math.Atan2(f.y, f.x) + off * Math.PI / 180.0;

            return target + new P((float)Math.Cos(a), (float)Math.Sin(a)) * Math.Max(0.5f, distance);
        }

        // Where a STRIKER / VENGEANCE companion waits with nothing to dive at:
        // close round you on a still ring, so the mode reads as "ready".
        public static P StrikeRest(P ship, P aim, int i, int n, Shape sh)
        {
            return GuardIdle(ship, aim, i, n, Math.Max(1.5f, (sh ?? new Shape()).orbitRadius * 0.7f));
        }

        // ------------------------------------------------------------------
        // SEEKER - hunt, and wander when there is nothing to hunt
        // ------------------------------------------------------------------
        //
        // His answer (R21 part 3, 312: "Hunt + wander"). The hunt is the game
        // side's (it needs the enemies); the wander is a slow loop round you
        // at about half the seek range, each drone on its own point of it.
        public const float WanderDegreesPerSecond = 24f;

        public static float WanderRadius(float seekRange)
        {
            return Math.Max(8f, Math.Min(30f, seekRange * 0.5f));
        }

        public static P WanderPoint(P ship, float seekRange, float seconds, int i, int n)
        {
            float r = WanderRadius(seekRange);
            double a = (seconds * WanderDegreesPerSecond + i * 360.0 / Math.Max(1, n)) * Math.PI / 180.0;

            return ship + new P((float)Math.Cos(a), (float)Math.Sin(a)) * r;
        }
    }
}
