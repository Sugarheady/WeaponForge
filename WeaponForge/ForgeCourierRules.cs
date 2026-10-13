using System;
using System.Collections.Generic;

namespace WeaponForge
{
    // THE COURIER'S DECISIONS, AS PURE ARITHMETIC (2026-10-09).
    //
    // A courier gun launches a drone instead of its shot; the drone flies to
    // your cursor, fires the gun there, and fades. Design and every decision:
    // `Docs/CURSOR DRONE - CONCEPT.md`. This file is every question that has
    // an answer without the game: the words a file may use, the angle each
    // shot leaves at (aim, snap, pattern, turn), the reach clamp, a step of
    // flight, and the gamepad's "first enemy along the stick".
    //
    // NO UNITY IN THIS FILE, ON PURPOSE. `Harness/courierprobe.cs` compiles it
    // on its own and runs it, the way `bloompathprobe.cs` runs ForgeBloomPath.
    // Change a rule here and nowhere else, and keep `UnityEngine` out of it or
    // the probe cannot build. Written to C# 5 for the same reason.
    public static class ForgeCourierRules
    {
        // Where it fires (his answer: accepted as proposed).
        //   Outward - the line from your ship through the drone, carried on:
        //             his "angle from the player to the cursor outward".
        //   Enemy   - at the nearest enemy in the gun's range, else outward.
        //   Back    - toward your ship.
        //   Launch  - the line it flew along, fixed when you fired.
        //   Centre  - at the middle of its ring (the ring formation's default).
        public enum Aim { Outward, Enemy, Back, Launch, Centre }

        // How many ways each shot goes at once.
        public enum Pattern { Single, Sides, Cross, X, Star }

        // One drone per shot, or one per pellet on a ring round the cursor
        // (his answer: "a separate setting/option").
        public enum Formation { One, Ring }

        public enum Entry { Fly, Appear }

        public enum Exit { Fade, Recoil }

        // His answer twice over: untouchable by default; shootable when fire
        // comes its way; or a decoy enemies choose to aim at.
        public enum Shootable { Off, Hit, Decoy }

        // ---- the words ---------------------------------------------------

        private static string Word(string s)
        {
            return s.Trim().ToLowerInvariant().Replace("-", "").Replace(" ", "")
                    .Replace("_", "");
        }

        public static bool ParseAim(string s, out Aim a)
        {
            a = Aim.Outward;

            if (string.IsNullOrEmpty(s))
                return true;

            switch (Word(s))
            {
                case "outward": case "out": case "away": case "forward":
                    a = Aim.Outward; return true;
                case "enemy": case "enemies": case "nearest": case "target":
                    a = Aim.Enemy; return true;
                case "back": case "backward": case "home": case "ship":
                    a = Aim.Back; return true;
                case "launch": case "flight": case "fixed":
                    a = Aim.Launch; return true;
                case "centre": case "center": case "inward": case "middle":
                    a = Aim.Centre; return true;
            }

            return false;
        }

        public static bool ParsePattern(string s, out Pattern p)
        {
            p = Pattern.Single;

            if (string.IsNullOrEmpty(s))
                return true;

            switch (Word(s))
            {
                case "single": case "one": case "line":
                    p = Pattern.Single; return true;
                case "sides": case "side": case "t": case "both":
                    p = Pattern.Sides; return true;
                case "cross": case "plus": case "+":
                    p = Pattern.Cross; return true;
                case "x": case "diagonal": case "diagonals":
                    p = Pattern.X; return true;
                case "star": case "eight": case "all":
                    p = Pattern.Star; return true;
            }

            return false;
        }

        public static bool ParseFormation(string s, out Formation f)
        {
            f = Formation.One;

            if (string.IsNullOrEmpty(s))
                return true;

            switch (Word(s))
            {
                case "one": case "single": case "none":
                    f = Formation.One; return true;
                case "ring": case "surround": case "circle": case "pincer":
                    f = Formation.Ring; return true;
            }

            return false;
        }

        public static bool ParseEntry(string s, out Entry e)
        {
            e = Entry.Fly;

            if (string.IsNullOrEmpty(s))
                return true;

            switch (Word(s))
            {
                case "fly": case "flies": case "travel":
                    e = Entry.Fly; return true;
                case "appear": case "spawn": case "fadein": case "blink":
                    e = Entry.Appear; return true;
            }

            return false;
        }

        public static bool ParseExit(string s, out Exit e)
        {
            e = Exit.Fade;

            if (string.IsNullOrEmpty(s))
                return true;

            switch (Word(s))
            {
                case "fade": case "vanish":
                    e = Exit.Fade; return true;
                case "recoil": case "pullback": case "back": case "kick":
                    e = Exit.Recoil; return true;
            }

            return false;
        }

        // `true` is the incidental mode, `false` is off - so a file that
        // writes the switch the obvious way gets the default kind.
        public static bool ParseShootable(string s, out Shootable k)
        {
            k = Shootable.Off;

            if (string.IsNullOrEmpty(s))
                return true;

            switch (Word(s))
            {
                case "false": case "off": case "no": case "none":
                    k = Shootable.Off; return true;
                case "true": case "on": case "yes": case "hit": case "incidental":
                    k = Shootable.Hit; return true;
                case "decoy": case "target": case "targeted": case "bait":
                    k = Shootable.Decoy; return true;
            }

            return false;
        }

        // ---- the angles ----------------------------------------------------

        // Round to the nearest multiple of `snap` degrees (0 = untouched).
        // 90 is the four straight lines, 45 adds the diagonals - the Gaster
        // look: the beams line up with the screen.
        public static double Snap(double degrees, double snap)
        {
            if (snap <= 0.0001)
                return degrees;

            return Math.Round(degrees / snap) * snap;
        }

        // Degrees each way of a pattern goes, relative to the aim.
        public static double[] PatternOffsets(Pattern p)
        {
            switch (p)
            {
                case Pattern.Sides: return new[] { 90.0, -90.0 };
                case Pattern.Cross: return new[] { 0.0, 90.0, 180.0, 270.0 };
                case Pattern.X: return new[] { 45.0, 135.0, 225.0, 315.0 };
                case Pattern.Star:
                    return new[] { 0.0, 45.0, 90.0, 135.0, 180.0, 225.0, 270.0, 315.0 };
                default: return new[] { 0.0 };
            }
        }

        // Every angle shot `shot` (0-based) leaves at: the aim snapped, then
        // turned `turn` degrees per shot already fired, then each way of the
        // pattern. Snap first, so a turn of 90 on a 90 snap stays on the grid
        // and a turn that is not a multiple of the snap leaves it, which is
        // the author's call.
        public static List<double> Angles(
            double aimDegrees, double snap, double turn, int shot, Pattern p)
        {
            double baseAngle = Snap(aimDegrees, snap) + turn * Math.Max(0, shot);

            var list = new List<double>();

            foreach (double o in PatternOffsets(p))
                list.Add(Normalize(baseAngle + o));

            return list;
        }

        public static double Normalize(double degrees)
        {
            double d = degrees % 360.0;

            if (d < 0.0)
                d += 360.0;

            return d;
        }

        public static double AngleOf(double x, double y)
        {
            return Math.Atan2(y, x) * 180.0 / Math.PI;
        }

        // ---- the flight ----------------------------------------------------

        // A target past `reach` from `origin` is pulled in to the reach along
        // the same line - it lands nearer rather than wasting the shot, as
        // bloom's placement does.
        public static void ClampToReach(
            double ox, double oy, double tx, double ty, double reach,
            out double x, out double y)
        {
            double dx = tx - ox, dy = ty - oy;
            double d = Math.Sqrt(dx * dx + dy * dy);

            if (reach > 0.0 && d > reach && d > 1e-9)
            {
                x = ox + dx * (reach / d);
                y = oy + dy * (reach / d);
                return;
            }

            x = tx;
            y = ty;
        }

        // One step of flight: `step` units toward the target, never past it.
        public static bool StepToward(
            double px, double py, double tx, double ty, double step,
            out double x, out double y)
        {
            double dx = tx - px, dy = ty - py;
            double d = Math.Sqrt(dx * dx + dy * dy);

            if (d <= step || d < 1e-6)
            {
                x = tx;
                y = ty;
                return true;
            }

            x = px + dx * (step / d);
            y = py + dy * (step / d);
            return false;
        }

        // ---- the gamepad ---------------------------------------------------
        //
        // MEASURED: on a pad the game's "cursor" is always the ship plus the
        // stick direction times 5 (ShipInput.gamepadAimDistance), so there is
        // no "where". His answer: the drone flies along the stick to the first
        // enemy that line crosses within its reach, else to its full reach.
        //
        // `xs`/`ys` are candidate enemy positions. Returns the index of the
        // one nearest along the line, within `width` of it and in front of
        // the ship within `reach`; -1 for none.
        public static int FirstAlongLine(
            double ox, double oy, double dirX, double dirY,
            double reach, double width, double[] xs, double[] ys)
        {
            double m = Math.Sqrt(dirX * dirX + dirY * dirY);

            if (m < 1e-9 || xs == null || ys == null)
                return -1;

            double ux = dirX / m, uy = dirY / m;
            int best = -1;
            double bestAlong = double.MaxValue;

            for (int i = 0; i < xs.Length && i < ys.Length; i++)
            {
                double rx = xs[i] - ox, ry = ys[i] - oy;
                double along = rx * ux + ry * uy;

                if (along <= 0.0 || along > reach)
                    continue;

                double side = Math.Abs(rx * uy - ry * ux);

                if (side > width)
                    continue;

                if (along < bestAlong)
                {
                    bestAlong = along;
                    best = i;
                }
            }

            return best;
        }

        // ---- timing --------------------------------------------------------

        // Seconds between shots: the file's own, else the gun's fire interval
        // (1 / fireRate), floored so a rate of 0 cannot stall a drone forever.
        public static double Interval(double fileInterval, double fireRate)
        {
            if (fileInterval > 0.0)
                return Math.Max(0.02, fileInterval);

            if (fireRate > 0.0001)
                return Math.Max(0.02, 1.0 / fireRate);

            return 0.5;
        }

        // How far the recoil has carried a drone after `t` seconds of a
        // `duration`-second exit: it starts at `speed` and slows to a stop,
        // so the distance is speed * (t - t^2 / (2 * duration)).
        public static double RecoilDistance(double speed, double t, double duration)
        {
            if (duration <= 0.0)
                return 0.0;

            double c = Math.Min(Math.Max(t, 0.0), duration);

            return speed * (c - c * c / (2.0 * duration));
        }

        // ---- boosting: more drones a pull (R21 part 4, his answers) --------

        // The most drones one pull sends side by side, however boosted. The
        // per-ship backstop (ForgeCourier.MaxPerShip) still applies on top.
        public const int MaxLine = 12;

        // Drones a pull at this module level: `baseCount` at level 1 and
        // `perLevel` more for each level above it ("+1 drone a level"),
        // never below 1 and never past `cap`.
        public static int DronesFor(int baseCount, int perLevel, int level, int cap)
        {
            int n = Math.Max(1, baseCount) + Math.Max(0, perLevel) * Math.Max(0, level - 1);

            return Math.Max(1, Math.Min(n, Math.Max(1, cap)));
        }

        // Where drone `index` of `count` sits on the firing line: SIDE BY SIDE
        // ACROSS YOUR AIM (his answer) - a short line through the cursor,
        // square to the line from the ship (dirX, dirY), `spacing` apart and
        // centred, so one drone sits on the cursor and two straddle it.
        public static void LineOffset(
            int index, int count, double spacing, double dirX, double dirY,
            out double ox, out double oy)
        {
            ox = 0.0;
            oy = 0.0;

            if (count <= 1)
                return;

            double m = Math.Sqrt(dirX * dirX + dirY * dirY);

            if (m < 1e-9)
            {
                dirX = 1.0;
                dirY = 0.0;
                m = 1.0;
            }

            // The left-hand normal of the aim.
            double nx = -dirY / m, ny = dirX / m;
            double along = (index - (count - 1) / 2.0) * spacing;

            ox = nx * along;
            oy = ny * along;
        }
    }
}
