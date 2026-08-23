using System;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace WeaponForge
{
    // Drives ForgeBounce.
    //
    //   PhysicsProjectile.Shoot            postfix - capture the pristine
    //                                                damage, once.
    //   PhysicsProjectile.OnCollisionEnter2D postfix - the bounce itself.
    //
    // A POSTFIX on the collision, and it is only safe because the builder has
    // already switched impactBehaviour OFF for this weapon. With it on, the
    // original method destroys the projectile before a postfix could run - and
    // that destruction is the whole reason a stock grenade does not bounce.
    // With it off the original does nothing but apply push force, so we get a
    // solved contact and a live projectile.
    //
    // The important thing about running AFTER the solver: Box2D has already
    // applied its own restitution and friction. Friction we keep - it is what
    // makes a grenade roll rather than skid, and the material owns it. The
    // NORMAL component we throw away and rebuild, which is what lets
    // bounciness depend on how hard the shot landed and what gets past
    // Physics2D's global bounce threshold (contacts under 1 unit/s are
    // perfectly inelastic no matter what the material says).
    public static class ForgeBouncePatch
    {
        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("WeaponForge.Bounce");

        [HarmonyPatch(typeof(PhysicsProjectile), "Shoot")]
        public class OnShoot
        {
            static void Postfix(PhysicsProjectile __instance)
            {
                var b = __instance.GetComponent<ForgeBounce>();

                if (b == null || b.damageFalloff <= 0f)
                    return;

                // FireSingle calls Shoot() twice on a lobbed shot and the first
                // one is against a blank projectile, before Damage exists.
                // Capturing there would latch a base of 0 and the shot would
                // do nothing at all for the rest of its life.
                if (ForgePhysicsShot.NotReady(__instance))
                    return;

                if (b.captured)
                    return;

                b.captured = true;
                b.baseDamage = __instance.Damage.amount;

                // Through the shared stack rather than straight onto Damage: a
                // bouncing shot can also be growing or ramping, and two
                // features assigning the same field from their own captured
                // bases overwrite each other in whichever order Harmony
                // happens to run them.
                ForgeDamageStack.For(__instance).Capture(b.baseDamage);
            }
        }

        [HarmonyPatch(typeof(PhysicsProjectile), "OnCollisionEnter2D")]
        public class OnCollision
        {
            static void Postfix(PhysicsProjectile __instance, Collision2D collision)
            {
                if (__instance == null || collision == null)
                    return;

                var b = __instance.GetComponent<ForgeBounce>();

                if (b == null)
                    return;

                try
                {
                    Bounce(__instance, b, collision);
                }
                catch (Exception e)
                {
                    Log.LogError(
                        "Bounce failed - the shot keeps whatever the physics " +
                        "engine gave it: " + e);
                }
            }
        }

        private static void Bounce(
            PhysicsProjectile p, ForgeBounce b, Collision2D collision)
        {
            if (collision.contactCount <= 0)
                return;

            ContactPoint2D contact = collision.GetContact(0);
            Vector2 n = contact.normal;

            // The closing speed along the normal - how hard this landed. The
            // game's own idiom, used by ImpactDamage, DestroyOnImpact and
            // ImpactParticle. On a floor contact it IS the fall speed, which
            // is why "bouncier the further it fell" is exactly expressible.
            float closing = Vector2.Dot(collision.relativeVelocity, n);

            // A contact too gentle to be a bounce. Without this a shot
            // settling on the ground jitters on micro-contacts, spending its
            // bounce budget and firing per-bounce effects the whole time.
            if (closing < Mathf.Max(0f, b.minSpeed))
                return;

            // otherRigidbody is OURS. Collision2D names them from the point of
            // view of the contact, not the listener: collision.collider and
            // collision.rigidbody are the thing we hit - which is why the
            // game's own code pushes collision.collider.attachedRigidbody.
            Rigidbody2D rb = collision.otherRigidbody != null
                ? collision.otherRigidbody
                : p.GetComponentInParent<Rigidbody2D>();

            if (rb == null)
                return;

            // Out of bounces: replay the impactBehaviour the builder had to
            // take away, and end the shot.
            if (!b.Unlimited && b.used >= b.max)
            {
                End(p, b, contact.point, n);
                return;
            }

            b.used++;

            float e = b.Restitution(closing);

            // Keep the tangential half exactly as the solver left it - that is
            // friction doing its job, and it is what makes a grenade roll.
            // Replace only the normal half.
            Vector2 v = rb.linearVelocity;
            Vector2 tangent = v - Vector2.Dot(v, n) * n;
            Vector2 outgoing = tangent + n * (closing * e);

            if (b.scatter > 0f)
                outgoing = Scatter(outgoing, n, b.scatter);

            rb.linearVelocity = outgoing;

            Effects(p, b, collision, contact.point, n);
        }

        // Turn the rebound by a random angle, without ever turning it back
        // into the surface it just left - which would push the shot through
        // the wall rather than off it.
        private static Vector2 Scatter(Vector2 v, Vector2 n, float degrees)
        {
            float a = UnityEngine.Random.Range(-degrees, degrees);
            Vector2 turned = Quaternion.Euler(0f, 0f, a) * v;

            if (Vector2.Dot(turned, n) <= 0f)
            {
                // Mirror the offending normal component back out rather than
                // discarding the scatter entirely, so a glancing bounce still
                // gets some of the variation it asked for.
                turned -= 2f * Vector2.Dot(turned, n) * n;
            }

            return turned;
        }

        private static void Effects(
            PhysicsProjectile p, ForgeBounce b, Collision2D collision,
            Vector2 point, Vector2 normal)
        {
            // The impact sound. ForgeImpactPatch normally plays this from
            // PhysicsProjectile.Impact - which no longer runs at all on a
            // bouncing weapon, because impactBehaviour is off. So "impactSfx"
            // keeps working, and on a bouncing weapon it is the bounce sound.
            var impact = p.GetComponent<ForgeImpact>();

            if (impact != null && collision.collider != null &&
                impact.Wants(collision.collider.gameObject.layer) &&
                impact.Ready())
            {
                impact.PlayAt(point);
            }

            if (b.damageFalloff > 0f && b.captured)
            {
                // used is already incremented, so the FIRST bounce lands at
                // full damage and the falloff applies to the one after it.
                var stack = ForgeDamageStack.For(p);
                stack.Capture(b.baseDamage);
                stack.bounce = Mathf.Pow(
                    Mathf.Max(0f, 1f - b.damageFalloff), b.used - 1);
                stack.Apply(p);
            }

            if (b.damageOnBounce && collision.collider != null)
                Damage(p, collision.collider, point, normal);

            if (b.explodeOnBounce)
                Explode(p);

            if (b.dischargeOnBounce)
                Discharge(p);

            // A lobbed shot bounces because it is a real rigidbody, so there is
            // no Reflect call to hook the way a plain projectile has. This is
            // the equivalent point.
            ForgeSplit.Fire(p, ForgeSplit.OnBounce);
        }

        // What the game calls TriggerCollisionOnObject, which lives inside the
        // `if (impactBehaviour.enabled)` we had to switch off - so a bouncing
        // shot does no contact damage at all unless we do this ourselves.
        // Routing through the listener rather than reaching for health
        // directly is what gets terrain digging, cell shake, burn and kill
        // credit for free.
        private static void Damage(
            PhysicsProjectile p, Collider2D col, Vector2 point, Vector2 normal)
        {
            IProjectileListener[] listeners =
                col.GetComponents<IProjectileListener>();

            if (listeners != null && listeners.Length > 0)
            {
                for (int i = 0; i < listeners.Length; i++)
                {
                    if (listeners[i] != null)
                        listeners[i].ProjectileCollided(p, point, normal);
                }
            }
            else
            {
                IProjectileListener parent =
                    col.GetComponentInParent<IProjectileListener>();

                if (parent != null)
                    parent.ProjectileCollided(p, point, normal);
            }

            ImpactVisualizer vis;

            if (col.TryGetComponent<ImpactVisualizer>(out vis))
                vis.VisualizeImpact(point, normal);
        }

        private static void Explode(PhysicsProjectile p)
        {
            Explosion ex = p.Explosion;

            // SpawnExplosion dereferences damages[0].damageType for the blast
            // prefab and throws on an empty list, which is how an unconfigured
            // explosion turns into an invisible shot that never dies.
            if (ex.damages == null || ex.damages.Count == 0 || ex.radius <= 0f)
                return;

            try
            {
                ex.Owner = p.Owner;
                ex.Burn = p.Burn;

                ServiceLocator.Get<ExplosionManager>()
                    .SpawnExplosion(p.transform.position, ex);
            }
            catch { }
        }

        private static void Discharge(PhysicsProjectile p)
        {
            DischargeData d = p.DischargeData;

            if (d.chainLength <= 0)
                return;

            try
            {
                ElectricityManager em;

                if (ServiceLocator.TryGet<ElectricityManager>(out em))
                    em.SpawnDischarge(d, p.transform.position);
            }
            catch { }
        }

        // The last bounce. Replays the weapon's original impactBehaviour -
        // destroy effect, explosion, sub-emitter, discharge - and removes the
        // shot, so "impactBehaviour" still means what it always meant, just at
        // the END of the bouncing rather than the start of it.
        private static void End(
            PhysicsProjectile p, ForgeBounce b, Vector2 point, Vector2 normal)
        {
            try
            {
                if (b.hasEnding && b.ending.enabled)
                {
                    if (b.ending.destroyEffect != null)
                    {
                        Vector2 away = Vector2.Reflect(p.Velocity, normal);

                        UnityEngine.Object.Instantiate(
                            b.ending.destroyEffect,
                            point,
                            Quaternion.FromToRotation(Vector3.right, away));
                    }

                    if (b.ending.spawnExplosion)
                        Explode(p);

                    if (b.ending.fireSub)
                        FireSub(p);

                    if (b.ending.discharge)
                        Discharge(p);
                }
            }
            catch (Exception e)
            {
                Log.LogError("Bounce ending failed: " + e);
            }

            UnityEngine.Object.Destroy(p.gameObject);
        }

        private static void FireSub(PhysicsProjectile p)
        {
            if (p.SubEmitters == null)
                return;

            try
            {
                foreach (WeaponBase sub in p.SubEmitters)
                {
                    if (sub != null)
                    {
                        // Vector2.up because that is what the game does - a
                        // sub has no barrel and no heading of its own.
                        sub.Fire(new FakeBarrel(p.transform.position, Vector2.up))
                            .Forget();
                    }
                }
            }
            catch { }
        }
    }
}
