using UnityEngine;

namespace WeaponForge
{
    // The one thing allowed to write a shot's Damage while it flies.
    //
    // Two features now want to change damage mid-flight - growth ("bigger
    // hits harder") and the damage ramp ("further hits harder") - and both
    // run as FixedUpdate prefixes. If each assigned Damage.amount from its
    // OWN captured base, the last one to run each frame would win and the
    // other would appear to do nothing, with the winner varying by patch
    // order. That is the same failure as two features both writing Velocity,
    // which is why steering publishes idealDirection instead.
    //
    // So the rule here matches that one: nobody writes Damage directly.
    // Features publish a MULTIPLIER, this multiplies them together against
    // one captured base, and the result is order-independent - a shot that
    // is both fully grown and fully ramped gets both.
    public class ForgeDamageStack : MonoBehaviour
    {
        // The shot's damage as the weapon fired it, captured once. Whichever
        // feature reaches Shoot first captures; the rest see captured == true
        // and leave it alone.
        [System.NonSerialized] public float baseAmount;
        [System.NonSerialized] public bool captured;

        // One slot per contributing feature. Named fields rather than a list
        // or a dictionary because this is read every FixedUpdate for every
        // shot in flight, and either would allocate there.
        [System.NonSerialized] public float growth = 1f;
        [System.NonSerialized] public float ramp = 1f;
        [System.NonSerialized] public float chain = 1f;
        [System.NonSerialized] public float bounce = 1f;
        [System.NonSerialized] public float catalyst = 1f;
        [System.NonSerialized] public float split = 1f;
        [System.NonSerialized] public float rally = 1f;

        // These two arrived late, and both were writing Damage directly until
        // 2026-08-25 - which is invisible on their own and silently wrong the
        // moment the same shot also grows or ramps, because Apply() rebuilds
        // the amount from baseAmount and erases anything written behind its
        // back. `bounce` is the lobbed twin of `ricochet`; they never share a
        // shot (one is PhysicsProjectile, the other plain), but they get their
        // own slots anyway rather than one "bounce-ish" slot that would need a
        // comment explaining why it is safe.
        [System.NonSerialized] public float ricochet = 1f;
        [System.NonSerialized] public float boomerang = 1f;

        public void Capture(float amount)
        {
            if (captured)
                return;

            baseAmount = amount;
            captured = true;
        }

        public float Total
        {
            get
            {
                return baseAmount * growth * ramp * chain * bounce *
                       catalyst * split * rally * ricochet * boomerang;
            }
        }

        // Push the combined result onto the shot. Safe to call from either
        // feature, in any order, as many times per frame as you like.
        public void Apply(Projectile p)
        {
            if (!captured || p == null)
                return;

            Damage d = p.Damage;
            d.amount = Total;
            p.Damage = d;
        }

        public void Apply(PhysicsProjectile p)
        {
            if (!captured || p == null)
                return;

            Damage d = p.Damage;
            d.amount = Total;
            p.Damage = d;
        }

        // Find or add the stack on whatever object carries the projectile.
        public static ForgeDamageStack For(Component projectile)
        {
            if (projectile == null)
                return null;

            var s = projectile.GetComponent<ForgeDamageStack>();

            if (s == null)
                s = projectile.gameObject.AddComponent<ForgeDamageStack>();

            return s;
        }
    }
}
