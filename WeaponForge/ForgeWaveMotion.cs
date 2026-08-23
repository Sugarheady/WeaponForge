using UnityEngine;

namespace WeaponForge
{
    // Attached to a projectile prefab by WeaponBuilder.ApplyWave when a
    // weapon sets "wave": true. It makes the shot weave instead of flying a
    // straight line. The params live here; the per-instance runtime state
    // (start time + phase) is filled in by ForgeWavePatch when the projectile
    // is actually shot. ForgeWavePatch's FixedUpdate prefix reads these and
    // swings the projectile's heading each frame.
    public class ForgeWaveMotion : MonoBehaviour
    {
        // WHAT DRIVES THE HEADING, which is the whole of the path shape.
        //
        // The feature only ever does one thing: rotate the shot's base heading
        // by some angle each frame. A SMOOTH function of time gives a smooth
        // path; a function with corners gives a path with corners. So the
        // shape is a choice of function and nothing else.
        //
        //   Sine     the original wave beam. The heading eases from one side
        //            to the other, so the path is a clean rolling S.
        //   Zigzag   the heading SNAPS between the two extremes and holds
        //            there, so the shot flies a straight leg, turns hard, and
        //            flies another. Sharp corners - the Z.
        //   Saw      the heading sweeps one way across the whole cycle and
        //            then snaps back, so the shot curves gradually and jerks
        //            the other way. A repeating hook.
        public enum Shape { Sine, Zigzag, Saw }

        public Shape shape = Shape.Sine;

        // Zigzag only: how hard the corners are, 0..1.
        //
        // 1 is a true snap - the heading is at one extreme or the other and
        // never in between. Below that the flip is spread over part of the
        // cycle, which rounds the corner off; at 0 it degrades to a triangle
        // wave, which is a sine's shape with straighter sides.
        public float sharpness = 1f;

        // How far the heading swings off the fire direction, in degrees
        // (the "width"/sharpness of the weave).
        public float angleDeg = 30f;

        // Wiggles per second (with speed, this sets the wavelength).
        public float frequency = 2f;

        // 0 = single   : phase measured from THIS shot's fire time, so each
        //                shot's wave starts centered.
        // 1 = synced   : phase measured from a shared clock, so a rapid-fire
        //                stream forms one continuous ribbon.
        // 2 = helix    : like synced, but alternate projectiles start half a
        //                cycle out of phase - the woven "double helix" look.
        public int mode = 0;

        // Turn the sprite to face where the shot is actually going.
        //
        // OFF by default, which is what the wave has always done: Projectile
        // sets its rotation once at Shoot and never again. On a symmetric
        // bullet nobody notices. On a zigzag with drawn directional art the
        // shot visibly flies sideways out of every corner, so this exists -
        // but turning it on by default would change the look of every wave
        // weapon already tuned.
        public bool faceTravel;

        // Runtime state, set by ForgeWavePatch at Shoot.
        [System.NonSerialized] public bool shot;
        [System.NonSerialized] public float startTime;

        // A fraction of a CYCLE (0 or 0.5), not radians - the shape functions
        // below all work in cycles so they can share one phase.
        [System.NonSerialized] public float phaseOffset;

        // The heading offset at time t, as -1..1. Multiply by angleDeg.
        public float Offset(float t)
        {
            float p = t * frequency + phaseOffset;

            // Fraction through the current cycle. Floor rather than % so it
            // stays correct for negative t, which "synced" mode can produce
            // if the shared clock is ever rebased.
            p -= Mathf.Floor(p);

            switch (shape)
            {
                case Shape.Zigzag:
                {
                    // A trapezoid: the triangle wave amplified and clipped.
                    // One knob covers the whole family - at gain 1 it IS the
                    // triangle, and as the gain climbs the ramps get steeper
                    // until the flat tops meet and it is a square wave.
                    //
                    // The gain is 1/(1-sharpness) rather than a plain lerp
                    // because the fraction of the cycle spent on a STRAIGHT
                    // LEG works out to exactly 1 - 1/gain, so this makes the
                    // dial mean what it looks like: 0.5 is half straight legs
                    // and half turning, 0.9 is nine tenths straight. A lerp
                    // to 24 was simulated first and was useless - it was
                    // already 85% straight at 0.25, so the whole top of the
                    // range did nothing.
                    float s = Mathf.Clamp01(sharpness);
                    float gain = 1f / Mathf.Max(0.0001f, 1f - s);

                    return Mathf.Clamp(Triangle(p) * gain, -1f, 1f);
                }

                case Shape.Saw:
                {
                    // One long sweep across the cycle, then a snap back.
                    //
                    // Shifted half a cycle so the shot LEAVES ON YOUR AIM. The
                    // unshifted ramp starts at full deflection, which fires
                    // every shot out sideways and reads as a broken weapon
                    // rather than a hook.
                    float q = p + 0.5f;
                    q -= Mathf.Floor(q);

                    return 2f * q - 1f;
                }

                default:
                    return Mathf.Sin(p * 2f * Mathf.PI);
            }
        }

        // 0 -> 1 -> 0 -> -1 -> 0 across one cycle, so it lines up with the
        // sine's phase and switching shape does not also shift the weave.
        private static float Triangle(float p)
        {
            if (p < 0.25f)
                return 4f * p;

            if (p < 0.75f)
                return 2f - 4f * p;

            return 4f * p - 4f;
        }
    }
}
