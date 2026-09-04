using UnityEngine;

namespace WeaponForge
{
    // A shot that CHANGES SPEED while it flies - the other half of the railgun
    // idea, asked for as *"do we have an option to increase the projectile speed
    // as it travels?"*
    //
    // THE ANSWER WAS NO, AND NOT JUST IN THIS MOD. Nothing in the game
    // accelerates a projectile. Speed only ever goes down, in exactly one place:
    //
    //     if (RangeData.enabled && RangeData.slowDown)
    //         Velocity = Velocity.normalized *
    //             Lerp(StartSpeed, 0, elapsed / timeToReachRange);
    //
    // The single exception is a LOBBED shot, where the game's own
    // `homingData.acceleration` speeds one up - but only while it is homing at a
    // target, so it is not a general answer.
    //
    // This is deliberately the same shape as `damageRamp`: same modes, same
    // `from` / `to` / `span` / `curve` / `clamp`, and the values are
    // MULTIPLIERS of the shot's normal speed so 1 is unchanged. `from` above
    // `to` is a decelerating shot, which is a legitimate weapon rather than a
    // mistake - and unlike `rangeData.slowDown` it can stop at a floor instead
    // of coasting to zero.
    //
    // WHY THIS IS A SEPARATE COMPONENT FROM THE DAMAGE ONE. They share the
    // curve maths and nothing else: damage is published into
    // `ForgeDamageStack` so several features can multiply the same number,
    // while speed is written straight onto `Velocity` and there is only ever one
    // writer. Merging them would put a stack in front of a value that does not
    // need one.
    public class ForgeSpeedRamp : MonoBehaviour
    {
        public enum Over { Distance, Time, Pierce, Bounce }

        public Over over = Over.Distance;

        // Multipliers of the shot's launch speed.
        public float from = 1f;
        public float to = 3f;

        // Distance in world units / seconds / number of pierces or bounces to
        // reach "to". 0 on a Distance ramp means "the weapon's own range",
        // resolved at Shoot.
        public float span;

        // 1 = steady. Above 1 holds low then rushes at the end, which is the
        // winding-up feel a railgun wants; below 1 jumps early.
        public float curve = 1f;

        // Stop at "to" rather than continuing past it.
        public bool clamp = true;

        // A hard floor and ceiling in WORLD UNITS PER SECOND, not multipliers,
        // because the interesting failures are absolute: a shot that ramps down
        // to nothing hangs in the air forever, and one that ramps up past about
        // 56 u/s starts tunnelling through 1-unit terrain because every
        // projectile in this game uses Discrete collision detection.
        //
        // 0 on either means "no limit", which is the honest default - but the
        // builder warns when the maths says the shot will cross that line.
        public float minSpeed;
        public float maxSpeed;

        [System.NonSerialized] public bool shot;
        [System.NonSerialized] public float startTime;
        [System.NonSerialized] public Vector2 origin;
        [System.NonSerialized] public float resolvedSpan;
        [System.NonSerialized] public float launchSpeed;

        // Event counters, for the Pierce / Bounce modes.
        [System.NonSerialized] public int events;

        // 0..1 through the ramp. Identical to the damage ramp's, deliberately -
        // "over": "distance" must mean the same thing in both blocks or the two
        // are impossible to reason about together.
        public float Progress(Vector2 here)
        {
            float span = Mathf.Max(0.0001f, resolvedSpan);
            float t;

            switch (over)
            {
                case Over.Time:
                    t = (Time.time - startTime) / span;
                    break;

                case Over.Pierce:
                case Over.Bounce:
                    t = events / span;
                    break;

                default:
                    t = Vector2.Distance(origin, here) / span;
                    break;
            }

            if (t < 0f)
                t = 0f;

            if (clamp && t > 1f)
                t = 1f;

            return t;
        }

        public float Multiplier(Vector2 here)
        {
            float t = Progress(here);

            float eased = (curve == 1f)
                ? t
                : Mathf.Pow(Mathf.Max(0f, t), Mathf.Max(0.01f, curve));

            return Mathf.LerpUnclamped(from, to, eased);
        }

        // The speed this shot should be travelling at right now, or -1 when
        // there is nothing to say (not yet shot, or no launch speed captured).
        public float SpeedAt(Vector2 here)
        {
            if (!shot || launchSpeed <= 0f)
                return -1f;

            float want = launchSpeed * Multiplier(here);

            if (maxSpeed > 0f && want > maxSpeed)
                want = maxSpeed;

            if (minSpeed > 0f && want < minSpeed)
                want = minSpeed;

            // Never below a crawl, or the shot stops dead and sits there: the
            // range clock keeps running but a stationary projectile covers no
            // distance, so a Distance ramp would freeze at its current value.
            return Mathf.Max(0.05f, want);
        }
    }
}
