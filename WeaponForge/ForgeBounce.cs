using UnityEngine;

namespace WeaponForge
{
    // Bouncing GRENADES - the lobbed (usePhysics) half of "make it bounce".
    //
    // This is a different mechanic from `ricochet`, which is why it is a
    // different key. Ricochet shapes the game's SCRIPTED bounce: a plain
    // bullet reflecting off a wall through ProjectileBounceData, a perfect
    // mirror with no gravity involved. A lobbed shot has no scripted bounce at
    // all - it is a real Rigidbody2D, and it bounces because Box2D bounces it.
    //
    // The game's own "bouncing bomb" is nothing more than switching
    // impactBehaviour OFF so the shot stops destroying itself on contact -
    // eight stock weapons do exactly that (Item Bomb, Item Matchbox, Caps
    // Vial, Purple ClusterCube, Queen Bomb, both Bomber bombs, Fly Alfa) and
    // then rely on a fuse. Because the damage call sits INSIDE that same
    // `if (impactBehaviour.enabled)`, those eight also do no contact damage at
    // all: they roll around your feet completely harmlessly until they blow.
    //
    // So this feature does three things the game cannot:
    //
    //   1. Take restitution off Box2D. The stock grenade material is
    //      bounciness 0.3, shared by a dozen ammo prefabs, and Unity's global
    //      bounce threshold makes every contact slower than 1 unit/s perfectly
    //      inelastic. Computing the rebound ourselves gets past both, and it
    //      is the only way to have bounciness depend on ANYTHING - including
    //      how far the thing fell.
    //   2. Count bounces, which nothing in the game does, and end the shot on
    //      the last one.
    //   3. Fire effects per bounce while keeping the shot alive.
    public class ForgeBounce : MonoBehaviour
    {
        // --- how bouncy ---------------------------------------------------
        //
        // Restitution is the fraction of impact speed that comes back out.
        // 0.3 is the stock grenade, 1 is a perfect arcade bounce, above 1 is
        // super-elastic and will climb.
        //
        // Left alone this is a flat dial. Set `to` and it becomes a RAMP
        // driven by how hard the shot landed: `restitution` at a gentle touch,
        // `to` after falling `overHeight` world units. Impact speed and drop
        // height are the same fact - v = sqrt(2 g h) - so this is literally
        // "the further it fell, the bouncier it is".
        public float restitution = 0.6f;
        public float to = -1f;            // <0 = flat, no ramp
        public float overHeight = 20f;
        public float curve = 1f;

        // The impact speed a fall of overHeight units ACTUALLY produces, for
        // this shot's own gravity scale and damping. Solved once at build time
        // by FallSpeed() below rather than taken from sqrt(2gh), because
        // physics projectiles have linear damping and the textbook formula
        // overstates the speed badly: at Derbis settings a 20-unit drop lands
        // at 21.5, not the 26.6 the formula predicts. Using the formula would
        // have meant "overHeight: 20" quietly needing a 35-unit fall to reach
        // full - a number that lies by three quarters.
        public float fullSpeed = 1f;

        // Sideways friction, as a fresh PhysicsMaterial2D. Negative keeps the
        // stock 0.4. This is the difference between a grenade that rolls and
        // one that skids to a stop.
        public float friction = -1f;

        // --- how long ------------------------------------------------------
        //
        // max <= 0 is unlimited, which is what the game does. A super-elastic
        // shot NEEDS a cap or it climbs out of the level.
        public int max;

        // Impact speed below which a contact simply is not a bounce. Without
        // this a shot settling on the floor jitters forever on micro-contacts,
        // burning its bounce budget and firing per-bounce effects while it
        // does. Unity's own threshold for this is 1.
        public float minSpeed = 1f;

        // Degrees of random turn added to each rebound. Besides looking less
        // mechanical, this is the escape from a shot trapped bouncing between
        // two parallel walls forever.
        public float scatter;

        // --- what a bounce does --------------------------------------------
        //
        // Contact damage is OFF by default because that is what the stock
        // bouncing bombs do, and because a bouncing shot that also hits on
        // every contact is strictly better than one that does not.
        public bool damageOnBounce;

        // Fraction of damage LOST per bounce. 0.2 = 100%, 80%, 64%...
        public float damageFalloff;

        public bool explodeOnBounce;
        public bool dischargeOnBounce;

        // What happens when the shot runs out of bounces: the weapon's own
        // impactBehaviour, captured at build time. We have to switch that off
        // for the shot to survive contact at all, so it is stored here and
        // replayed at the end instead - which means "impactBehaviour" keeps
        // meaning what it always meant, just at the last bounce rather than
        // the first.
        public ProjectileImpactBehaviour ending;
        public bool hasEnding;

        // --- per-shot state ------------------------------------------------
        [System.NonSerialized] public int used;
        [System.NonSerialized] public bool captured;
        [System.NonSerialized] public float baseDamage;

        // Restitution for a contact that arrived at this speed.
        public float Restitution(float impactSpeed)
        {
            if (to < 0f)
                return restitution;

            float t = Mathf.Clamp01(impactSpeed / Mathf.Max(0.01f, fullSpeed));

            if (curve != 1f)
                t = Mathf.Pow(t, curve);

            return Mathf.LerpUnclamped(restitution, to, t);
        }

        // How fast this shot is moving after falling `height` units, replaying
        // Box2D's actual step: gravity, then damping, then position. Build
        // time only - a few hundred iterations, once per weapon.
        //
        // Same reasoning as ForgeBallistics: the game's own no-air-resistance
        // formula is wrong here by up to a quarter, and a launch or a rebound
        // computed from it misses by metres.
        public static float FallSpeed(
            float height, float gravityScale, float damping, float dt)
        {
            if (height <= 0f)
                return 0f;

            if (dt <= 0f)
                dt = 0.02f;

            float y = height;
            float v = 0f;

            // 20 seconds of falling. A shot that has not landed by then is
            // being held up by absurd damping, and the speed has converged on
            // its terminal velocity anyway.
            for (int i = 0; i < 1000 && y > 0f; i++)
            {
                v += -9.81f * gravityScale * dt;
                v *= 1f / (1f + dt * Mathf.Max(0f, damping));
                y += v * dt;
            }

            return -v;
        }

        public bool Unlimited
        {
            get { return max <= 0; }
        }
    }
}
