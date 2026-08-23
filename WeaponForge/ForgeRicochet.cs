using UnityEngine;

namespace WeaponForge
{
    // Bullet ricochet - bounce limit, falloff, scatter and smart redirection.
    //
    // The game ALREADY bounces. ProjectileBounceData { enabled, layerMask } is
    // checked in Projectile.OnObjectHit, and because the damage call (TryHit)
    // runs BEFORE that branch, a shot that ricochets off an enemy has already
    // hurt it. Terrain is layer bit 1024 ("Ground"), enemies 128 ("Entities"),
    // so "which things bounce" is just that mask - WeaponBuilder sets it from a
    // plain-English "targets" word.
    //
    // What the game has NO concept of is a bounce COUNT: nothing anywhere
    // tracks how many times a shot has reflected, so a stock bouncer bounces
    // forever until range or lifetime kills it. That is what this component
    // exists for, plus the per-bounce shaping that only makes sense once you
    // are counting.
    public class ForgeRicochet : MonoBehaviour
    {
        // How many bounces are allowed. NEGATIVE = unlimited (what the game
        // does on its own).
        public int bounces = 3;

        // Fraction of speed kept per bounce. 1 = arcade, no loss.
        public float speedMultiplier = 1f;

        // Fraction of damage kept per bounce. 1 = arcade, no loss.
        public float damageMultiplier = 1f;

        // Degrees of random scatter added to each reflection. Besides feeling
        // less mechanical, this is the practical escape from a shot trapped
        // between two parallel walls, which a perfect mirror bounce would ping
        // between forever.
        public float scatter;

        // Aim each bounce at the nearest valid enemy instead of reflecting off
        // the surface - what makes a ricochet reliably "hit something else"
        // rather than sailing off into empty space.
        public bool seek;
        public float seekRange = 20f;

        // How far off the true reflected heading a seek is allowed to pull.
        // 180 = anywhere, including back the way it came.
        public float seekCone = 180f;

        // --- effects on EVERY bounce, with the shot carrying on -----------
        //
        // The game makes these mutually exclusive by design: in
        // Projectile.OnObjectHit the bounce branch RETURNS before it ever
        // reaches the impactBehaviour block, so a scripted bounce can never
        // fire an explosion, a discharge or a sub-emitter. A ricochet that
        // blasts its way around a room only exists from here.
        //
        // Note what is NOT in this list, because it already works: DAMAGE,
        // BURN and terrain DIGGING all happen on a bounce today. TryHit runs
        // at the top of OnObjectHit, before either branch, so a shot that
        // ricochets off an enemy has already hurt it and one that bounces off
        // a wall has already chewed it.
        public bool explodeOnBounce;
        public bool dischargeOnBounce;
        public bool subOnBounce;
        public bool effectOnBounce;   // the impactBehaviour destroyEffect art

        // Which surfaces the above fire on. A ricochet that detonates off
        // walls but passes cleanly through enemies (or the reverse) is a real
        // design rather than a detail.
        public enum Where { Any, Terrain, Enemy }

        public Where effectsOn = Where.Any;

        // Resolved once at build time - NameToLayer is a string lookup and
        // this is read inside a collision path.
        public int groundLayer = -1;

        public bool WantsEffects
        {
            get
            {
                return explodeOnBounce || dischargeOnBounce ||
                       subOnBounce || effectOnBounce;
            }
        }

        public bool EffectsWant(int layer)
        {
            if (effectsOn == Where.Any)
                return true;

            bool isGround = (groundLayer >= 0 && layer == groundLayer);

            return (effectsOn == Where.Terrain) ? isGround : !isGround;
        }

        // Runtime state.
        [System.NonSerialized] public int used;

        // Set by the Reflect prefix, read by the postfix, so the postfix knows
        // this bounce was actually allowed to happen.
        [System.NonSerialized] public bool bouncing;

        public bool Unlimited
        {
            get { return this.bounces < 0; }
        }
    }
}
