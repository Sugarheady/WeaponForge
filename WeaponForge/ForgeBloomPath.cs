using System;

namespace WeaponForge
{
    // THE SHAPES A BLOOM'S SHOTS FLY, AS PURE ARITHMETIC.
    //
    // NO UNITY IN THIS FILE, ON PURPOSE. `Harness/bloompathprobe.cs` compiles
    // it on its own and runs it - the path lengths, arriving exactly on the
    // fuse, which way round it turns, the order the staggered shots land in -
    // the same way `dronemodeprobe.cs` runs `ForgeDroneModeRules`. The game
    // side only ever asks these questions, so the probe tests the real decision
    // code rather than a copy of it. Change a shape here and nowhere else, and
    // keep `UnityEngine` out of it or the probe cannot build.
    //
    // Everything is in doubles: a spiral is solved for an arc length every
    // physics step, and a float's 7 digits are not much once a few laps of
    // arithmetic have gone through them. The callers convert at the edge.
    public static class ForgeBloomPath
    {
        public const double TwoPi = Math.PI * 2.0;

        // ---- the even spiral --------------------------------------------
        //
        // AN EVEN (ARCHIMEDEAN) SPIRAL: the distance in from the rim is the same
        // for every lap, so the arms are evenly spaced - which is what his
        // reference picture is. In polar form round the centre:
        //
        //     r     = a * phi            phi runs from phiMax (the start) to 0
        //     theta = theta0 + sign * (phiMax - phi)
        //     phiMax = 2 * pi * turns,   a = r0 / phiMax
        //
        // `sign` is +1 counter-clockwise and -1 clockwise (Unity's world is y-up,
        // so a positive angle turns counter-clockwise on screen).
        public struct Spiral
        {
            public double cx, cy;       // the centre it winds into
            public double r0;           // how far out it starts
            public double theta0;       // the angle it starts at (radians)
            public double sign;         // +1 counter-clockwise, -1 clockwise
            public double phiMax;       // 2 * pi * turns
            public double a;            // r0 / phiMax
            public double length;       // the whole trip, rim to centre

            // No laps, or nothing to wind in from: a straight line in. The
            // maths above divides by phiMax, so this case is kept apart rather
            // than allowed to approach it.
            public bool straight;
        }

        public static Spiral Make(
            double sx, double sy, double cx, double cy,
            double turns, bool clockwise)
        {
            var s = new Spiral();

            s.cx = cx;
            s.cy = cy;

            double dx = sx - cx;
            double dy = sy - cy;

            s.r0 = Math.Sqrt(dx * dx + dy * dy);
            s.theta0 = Math.Atan2(dy, dx);
            s.sign = clockwise ? -1.0 : 1.0;
            s.phiMax = TwoPi * Math.Max(0.0, turns);

            if (s.r0 < 1e-6 || s.phiMax < 1e-4)
            {
                s.straight = true;
                s.a = 0.0;
                s.length = s.r0;
                return s;
            }

            s.a = s.r0 / s.phiMax;
            s.length = ArcFromCentre(s.a, s.phiMax);

            return s;
        }

        // The arc length from the centre out to `phi`:
        //     (a / 2) * (phi * sqrt(1 + phi^2) + asinh(phi))
        // (asinh written out, because .NET Framework 4.7.2 has no Math.Asinh.)
        public static double ArcFromCentre(double a, double phi)
        {
            double q = Math.Sqrt(1.0 + phi * phi);

            return 0.5 * a * (phi * q + Math.Log(phi + q));
        }

        // How much longer the trip in is than going straight: about 1.9 times
        // for half a lap, 3.4 for one lap, 9.5 for three. Printed by the build
        // log, because a longer path in the same fuse is a faster shot.
        public static double LengthFactor(double turns)
        {
            return Make(1.0, 0.0, 0.0, 0.0, turns, true).length;
        }

        // Where a shot is when `phi` is the spiral's parameter.
        public static void At(
            ref Spiral s, double phi, out double x, out double y)
        {
            if (s.straight)
            {
                double f = (s.phiMax > 1e-4) ? phi / s.phiMax : 1.0;

                x = s.cx + Math.Cos(s.theta0) * s.r0 * f;
                y = s.cy + Math.Sin(s.theta0) * s.r0 * f;
                return;
            }

            double r = s.a * phi;
            double th = s.theta0 + s.sign * (s.phiMax - phi);

            x = s.cx + r * Math.Cos(th);
            y = s.cy + r * Math.Sin(th);
        }

        // STEADY PACE: where a shot is once it has travelled `along` of the
        // way in. `phi` is a warm start the caller keeps between steps - pass
        // `s.phiMax` the first time - and it only ever goes down, because the
        // shot only ever moves in.
        public static void AtDistance(
            ref Spiral s, double along, ref double phi,
            out double x, out double y)
        {
            if (along >= s.length)
            {
                phi = 0.0;
                x = s.cx;
                y = s.cy;
                return;
            }

            if (along <= 0.0)
            {
                phi = s.phiMax;
                StartPoint(ref s, out x, out y);
                return;
            }

            if (s.straight)
            {
                double f = 1.0 - along / s.length;

                x = s.cx + Math.Cos(s.theta0) * s.r0 * f;
                y = s.cy + Math.Sin(s.theta0) * s.r0 * f;
                return;
            }

            phi = SolvePhi(s.a, s.length - along, phi, s.phiMax);

            At(ref s, phi, out x, out y);
        }

        // EVEN PACE: `share` is the part of the trip's TIME that has gone (0 to
        // 1). The radius falls and the angle turns at constant rates, so the
        // shot moves fastest on the outer lap and eases into the middle.
        public static void AtShare(
            ref Spiral s, double share, out double x, out double y)
        {
            if (share >= 1.0)
            {
                x = s.cx;
                y = s.cy;
                return;
            }

            if (share < 0.0)
                share = 0.0;

            if (s.straight)
            {
                double f = 1.0 - share;

                x = s.cx + Math.Cos(s.theta0) * s.r0 * f;
                y = s.cy + Math.Sin(s.theta0) * s.r0 * f;
                return;
            }

            At(ref s, s.phiMax * (1.0 - share), out x, out y);
        }

        public static void StartPoint(ref Spiral s, out double x, out double y)
        {
            x = s.cx + Math.Cos(s.theta0) * s.r0;
            y = s.cy + Math.Sin(s.theta0) * s.r0;
        }

        // The way a shot is moving as it LEAVES the rim: the spiral's tangent
        // there, as a unit vector. With phi falling, the motion is minus the
        // derivative of the position with respect to phi.
        public static void StartHeading(
            ref Spiral s, out double hx, out double hy)
        {
            if (s.straight || s.r0 < 1e-6)
            {
                InwardAt(ref s, s.theta0, out hx, out hy);
                return;
            }

            Heading(ref s, s.phiMax, out hx, out hy);
        }

        // The way it crosses the middle. At phi = 0 the tangent is purely
        // radial, so an even spiral always arrives straight in along its last
        // radius - which is the line a `through` shot carries on along.
        public static void EndHeading(
            ref Spiral s, out double hx, out double hy)
        {
            double th = s.straight ? s.theta0 : s.theta0 + s.sign * s.phiMax;

            InwardAt(ref s, th, out hx, out hy);
        }

        // The direction of travel at `phi`: -(dP/dphi), normalised.
        //     dP/dphi = a (cos t, sin t) + a phi (-sin t, cos t) * dt/dphi
        //     dt/dphi = -sign
        public static void Heading(
            ref Spiral s, double phi, out double hx, out double hy)
        {
            double th = s.theta0 + s.sign * (s.phiMax - phi);
            double c = Math.Cos(th);
            double sn = Math.Sin(th);

            double dx = s.a * c + s.a * phi * (-sn) * (-s.sign);
            double dy = s.a * sn + s.a * phi * c * (-s.sign);

            Unit(-dx, -dy, out hx, out hy);
        }

        // Straight at the centre from the angle `theta`.
        private static void InwardAt(
            ref Spiral s, double theta, out double hx, out double hy)
        {
            hx = -Math.Cos(theta);
            hy = -Math.Sin(theta);
        }

        // NEWTON FROM ABOVE ON A CONVEX, RISING FUNCTION, so it never lands
        // below the answer and never needs a bracket.
        //
        // ArcFromCentre rises (its slope is a * sqrt(1 + phi^2) > 0) and curves
        // up (the slope itself rises), so a tangent line drawn from any point
        // ABOVE the root crosses zero at or above the root as well: every step
        // moves down and none overshoots. A warm start from the last step is
        // always above, because the shot only moves inward; anything else falls
        // back to the rim, which is above every root there is.
        private static double SolvePhi(
            double a, double target, double guess, double phiMax)
        {
            double phi = Math.Min(Math.Max(guess, 0.0), phiMax);

            if (ArcFromCentre(a, phi) < target)
                phi = phiMax;

            for (int i = 0; i < 64; i++)
            {
                double f = ArcFromCentre(a, phi) - target;

                if (f <= 1e-10 * Math.Max(1.0, target))
                    break;

                double next = phi - f / (a * Math.Sqrt(1.0 + phi * phi));

                if (next < 0.0)
                    next = 0.0;

                if (Math.Abs(next - phi) < 1e-13)
                {
                    phi = next;
                    break;
                }

                phi = next;
            }

            return phi;
        }

        // ---- the circle (spin, in bullet mode) --------------------------
        //
        // `along` units round a circle of radius r from the angle theta0. A
        // bubble's spin is re-aimed along its tangent every step instead and
        // drifts slowly outward; that is how it has always moved, and files
        // already written depend on it. A BULLET is far faster, and the same
        // tangent re-aim at 100 units a second would spiral off the ring within
        // a lap - so a bullet is put on the circle exactly.
        public static void OnCircle(
            double cx, double cy, double r, double theta0, double sign,
            double along, out double x, out double y)
        {
            double th = (r > 1e-6) ? theta0 + sign * along / r : theta0;

            x = cx + r * Math.Cos(th);
            y = cy + r * Math.Sin(th);
        }

        // ---- the staggered close ----------------------------------------
        //
        // 0 for the shot that arrives FIRST, 1 for the one that arrives LAST.
        // The ring's slots go counter-clockwise as the index rises
        // (ForgeBloom.RingOffset adds 360/N per index), so counter-clockwise
        // arrivals walk the index upward and clockwise ones walk it down.
        public static double StaggerShare(int index, int count, bool clockwise)
        {
            if (count <= 1)
                return 0.0;

            int i = ((index % count) + count) % count;
            int k = clockwise ? (count - 1 - i) : i;

            return (double)k / (count - 1);
        }

        // How much longer than the first one a shot takes: the last one takes
        // (1 + stagger) times as long, everything between in even steps.
        public static double StaggerScale(double share, double stagger)
        {
            return 1.0 + Math.Max(0.0, stagger) * share;
        }

        private static void Unit(double x, double y, out double ux, out double uy)
        {
            double m = Math.Sqrt(x * x + y * y);

            if (m < 1e-12)
            {
                ux = 1.0;
                uy = 0.0;
                return;
            }

            ux = x / m;
            uy = y / m;
        }
    }
}
