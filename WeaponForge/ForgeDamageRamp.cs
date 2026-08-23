using UnityEngine;

namespace WeaponForge
{
    // A shot whose damage CHANGES while it flies - the railgun idea: the
    // longer it travels, the harder it lands.
    //
    // The game's own model is falloff, and the mod already had two pieces of
    // it (pierce falloff, and growth tying damage to size). What was missing
    // was a ramp of its own: damage as a function of distance, time, enemies
    // pierced or walls bounced off, going UP as happily as down.
    //
    // Values are MULTIPLIERS of the shot's normal damage, so 1 is unchanged
    // and this composes with +damage modules rather than overriding them.
    // from > to is a falloff instead of a ramp, which is a legitimate weapon
    // (a shotgun that fades with range) rather than a mistake.
    public class ForgeDamageRamp : MonoBehaviour
    {
        public enum Over { Distance, Time, Pierce, Bounce }

        public Over over = Over.Distance;

        public float from = 1f;
        public float to = 3f;

        // Distance in world units / seconds / number of pierces or bounces
        // to reach "to". 0 on a Distance ramp means "the weapon's own range",
        // resolved at Shoot.
        public float span;

        // 1 = steady. Above 1 holds low then rushes at the end, which is the
        // charging-up feel; below 1 jumps early.
        public float curve = 1f;

        // Stop at "to" rather than continuing past it. Off lets a long-range
        // shot keep climbing, which is a choice rather than a bug.
        public bool clamp = true;

        [System.NonSerialized] public bool shot;
        [System.NonSerialized] public float startTime;
        [System.NonSerialized] public Vector2 origin;
        [System.NonSerialized] public float resolvedSpan;

        // Event counters, for the Pierce / Bounce modes.
        [System.NonSerialized] public int events;

        // 0..1 through the ramp.
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

        // The multiplier right now.
        public float Multiplier(Vector2 here)
        {
            float t = Progress(here);

            float eased = (curve == 1f)
                ? t
                : Mathf.Pow(Mathf.Max(0f, t), Mathf.Max(0.01f, curve));

            return Mathf.LerpUnclamped(from, to, eased);
        }
    }
}
