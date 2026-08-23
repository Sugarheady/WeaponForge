using UnityEngine;

namespace WeaponForge
{
    // "How hard do I have to throw this, along THIS heading, to land on THAT
    // point?"
    //
    // The game ships its own answer - ProjectileMath.LaunchSpeed - and it is
    // the classic textbook formula. It is also wrong here, because it assumes
    // no air resistance and every physics projectile in PUNK has linear
    // damping (Ammo Derbis: linearDamping 0.4, gravityScale 1.8). Measured
    // against the real integration, that formula lands a Derbis between 0.6
    // and 18 units SHORT of the point depending on launch angle - steeper
    // shots being worse - so a converging lobbed volley built on it would
    // consistently fall in front of the target.
    //
    // So instead of a formula we bisect the launch speed against a simulation
    // of the ACTUAL step Unity runs. That makes the answer exact regardless of
    // drag, gravity scale, or fixed timestep, and it keeps working for any
    // physics prefab rather than just the one this was measured on.
    //
    // Cost is a few thousand float ops per pellet, paid once at Shoot. A
    // ten-pellet volley is well under a millisecond and nothing runs per
    // frame.
    public static class ForgeBallistics
    {
        public enum Result
        {
            // Landed it: `speed` puts the shot on the point.
            Solved,

            // The point is further than this weapon can throw at that angle.
            // `speed` is the hardest throw allowed - "do its best", which
            // falls visibly short rather than silently doing nothing.
            OutOfReach,

            // No arc to solve: no gravity, the target is straight overhead, or
            // this pellet is travelling away from it. Leave the shot alone.
            NotApplicable,
        }

        private const int Iterations = 24;
        private const int MaxSteps = 400;   // 8 seconds of flight at 50Hz

        // Where does this throw cross the vertical line through targetX?
        //
        // Mirrors Box2D's dynamic-body step in the order Unity applies it -
        // gravity, then damping, then position - because being a step out of
        // phase with the engine is exactly the kind of small consistent error
        // that would put every pellet slightly off.
        private static bool HeightAtX(
            Vector2 from,
            Vector2 launch,
            float targetX,
            float gravity,
            float damping,
            float dt,
            out float y)
        {
            y = 0f;

            float k = 1f / (1f + damping * dt);
            float side = Mathf.Sign(targetX - from.x);

            Vector2 p = from;
            Vector2 v = launch;

            for (int i = 0; i < MaxSteps; i++)
            {
                Vector2 nv = new Vector2(v.x, v.y - gravity * dt) * k;
                Vector2 np = p + nv * dt;

                if ((np.x - targetX) * side >= 0f)
                {
                    float span = np.x - p.x;

                    float f = (Mathf.Abs(span) > 0.000001f)
                        ? (targetX - p.x) / span
                        : 1f;

                    y = p.y + (np.y - p.y) * f;
                    return true;
                }

                // Damping bleeds horizontal speed away, so a slow throw has a
                // hard ceiling on how far it can ever get. Bail rather than
                // grinding out all 400 steps.
                if (nv.x * side <= 0.0001f)
                    return false;

                p = np;
                v = nv;
            }

            return false;
        }

        // `dir` must be normalised. minSpeed/maxSpeed bound what we are willing
        // to fire at, so a far-off focal point cannot turn a grenade into a
        // railgun slug.
        public static Result Solve(
            Vector2 from,
            Vector2 to,
            Vector2 dir,
            float gravity,
            float damping,
            float dt,
            float minSpeed,
            float maxSpeed,
            out float speed)
        {
            speed = 0f;

            if (gravity <= 0.01f)
                return Result.NotApplicable;

            float dx = to.x - from.x;

            // Straight up or straight down: there is no horizontal crossing to
            // solve against, and a fan of arcs cannot meet over your own head.
            if (Mathf.Abs(dx) < 0.05f)
                return Result.NotApplicable;

            // This pellet is heading away from the point. Throwing harder only
            // makes it worse.
            if (dir.x * Mathf.Sign(dx) <= 0.01f)
                return Result.NotApplicable;

            float y;

            // Bracket high: if even the hardest allowed throw passes BELOW the
            // point, nothing in range solves it.
            if (!HeightAtX(from, dir * maxSpeed, to.x, gravity, damping, dt,
                           out y) || y < to.y)
            {
                speed = maxSpeed;
                return Result.OutOfReach;
            }

            // Bracket low: a point this close needs a gentler throw than we
            // allow, so take the gentlest and accept a slight overshoot.
            if (HeightAtX(from, dir * minSpeed, to.x, gravity, damping, dt,
                          out y) && y >= to.y)
            {
                speed = minSpeed;
                return Result.Solved;
            }

            float lo = minSpeed;
            float hi = maxSpeed;

            // Height at the crossing rises monotonically with launch speed for
            // a fixed angle, so plain bisection is well behaved. 24 halvings
            // of a 0.1x-4x band resolves far finer than anything visible.
            for (int i = 0; i < Iterations; i++)
            {
                float mid = (lo + hi) * 0.5f;

                if (HeightAtX(from, dir * mid, to.x, gravity, damping, dt,
                              out y) && y >= to.y)
                {
                    hi = mid;
                }
                else
                {
                    lo = mid;
                }
            }

            speed = (lo + hi) * 0.5f;
            return Result.Solved;
        }
    }
}
