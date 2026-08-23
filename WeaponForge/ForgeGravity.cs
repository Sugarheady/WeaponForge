using UnityEngine;

namespace WeaponForge
{
    // GRAVITY WELL - a shot that drags everything nearby into one point, then
    // bursts. The Mass Effect singularity.
    //
    // The game has no field forces of any kind. What it does have is the same
    // call this needs pointed the other way: explosions and orbit rings both
    // shove rigidbodies with AddForce, so pulling is that with the sign
    // flipped. That is why this was cheap, and it is the whole trick.
    //
    // The two genuinely new parts are the SHAPE of the force - a real well
    // falls off with distance, and something that pulls equally hard at any
    // range reads as a magnet rather than gravity - and the hold radius, which
    // is what stops a captured crowd vibrating on top of the centre.
    public class ForgeGravity : MonoBehaviour
    {
        // Where the well forms.
        //   Impact  it opens where the shot lands, and the shot parks there.
        //           The thrown-singularity version.
        //   Flight  it travels with the shot from the moment it is fired, so
        //           the whole flight drags things along behind it. A vacuum
        //           cleaner rather than a trap.
        public enum Where { Impact, Flight }

        // How the pull changes with distance.
        //   Linear    full strength at the middle, nothing at the rim. Reads
        //             most like gravity and is the default.
        //   Constant  the same everywhere inside the radius. A tractor beam -
        //             predictable, and much stronger at long range.
        //   Inverse   viciously strong close in and weak at the edge. Things
        //             drift, then snap.
        public enum Falloff { Linear, Constant, Inverse }

        public Where where = Where.Impact;
        public Falloff falloff = Falloff.Linear;

        public float radius = 6f;
        public float force = 30f;
        public float duration = 2.5f;

        // Inside this the pull stops. Without it everything caught piles onto
        // one pixel and jitters, because each frame overshoots the centre and
        // gets yanked back.
        public float holdRadius = 0.7f;

        // Cap on how fast the well can fling something. A heavy pull with no
        // cap turns a light enemy into a bullet.
        public float maxSpeed = 14f;

        // Per-second velocity damping on anything caught, 0-1. Makes the well
        // read as thick rather than as a slope.
        public float drag;

        public float damagePerSecond;

        // Whether it drags YOU too. Off by default - a weapon that yanks its
        // own user into the middle of a crowd is a decision, not a default.
        public bool pullPlayer;

        // Make `force` mean the same thing on every body.
        //
        // Without this a single number cannot serve both an enemy (mass 5-200,
        // damping 4-6) and the ship (mass 1, damping 0.5) - the same force
        // produces a 60x to 1000x spread in the speed it actually drags at,
        // which is exactly why `force: 34` moved nothing and `force: 9000` was
        // needed. On, terminal speed is force / 30 for everything, normalised
        // to a common Cross enemy so numbers tuned against those keep working.
        public bool massCompensated = true;

        // Whether it eats ENEMY FIRE. Needs ForgeProjectileTracker, because
        // enemy shots resolve their own collisions by raycast and ship with
        // their Collider2D DISABLED - they are invisible to OverlapCircle at
        // any radius, so there is no other way to find them.
        public bool pullShots;

        // Blast when it expires. The "then bursts" half.
        public bool burst = true;

        public int groundLayer = -1;

        // ---- runtime -----------------------------------------------------
        [System.NonSerialized] public bool open;
        [System.NonSerialized] public float openedAt;
        [System.NonSerialized] public Unit owner;

        // Force at distance d from the centre.
        public float ForceAt(float d)
        {
            float r = Mathf.Max(0.01f, radius);
            float t = Mathf.Clamp01(1f - d / r);

            switch (falloff)
            {
                case Falloff.Constant:
                    return force;

                case Falloff.Inverse:
                    // Clamped at half a unit so the middle is strong rather
                    // than infinite - an unclamped inverse launches anything
                    // that touches the centre into the next room.
                    return force / Mathf.Max(0.5f, d);

                default:
                    return force * t;
            }
        }
    }
}
