using System.Collections.Generic;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace WeaponForge
{
    // The living half of a gravity well. Unity drives it, like ForgeTurret and
    // the sticky/tether controllers.
    //
    // FixedUpdate for the force (rigidbodies want a fixed step), Update for
    // the timer, the bleed and the captured shots.
    public class ForgeGravityController : MonoBehaviour
    {
        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("WeaponForge.Gravity");

        private static AccessTools.FieldRef<Projectile, Vector2> _idealRef;
        private static bool _ready;

        private ForgeGravity g;
        private ContactFilter2D filter;
        private bool filtersReady;
        private readonly List<Collider2D> buffer = new List<Collider2D>();
        private readonly Dictionary<HealthBase, float> lastHit =
            new Dictionary<HealthBase, float>();

        // Enemy shots this well has taken ownership of. The tracker HANDS them
        // over rather than lending them - CollectNear removes what it returns -
        // so once captured they are ours to steer until they die.
        private readonly List<Component> caught = new List<Component>();

        private const float HardLife = 20f;

        private static void Ensure()
        {
            if (_ready)
                return;

            _ready = true;

            try
            {
                _idealRef = AccessTools.FieldRefAccess<Projectile, Vector2>(
                    "idealDirection");
            }
            catch
            {
                _idealRef = null;
            }
        }

        public void Begin(ForgeGravity well, Unit owner)
        {
            g = well;
            g.open = true;
            g.openedAt = Time.time;
            g.owner = owner;

            Ensure();
        }

        private void EnsureFilters()
        {
            if (filtersReady)
                return;

            filtersReady = true;

            filter = new ContactFilter2D();
            filter.useTriggers = true;

            // "Player" is in here for a reason, and leaving it out is what made
            // `pullPlayer: true` do nothing at all for a whole test round.
            //
            // The comment that used to sit here said "player and enemies share
            // these layers" - they do NOT. Enemies are on **Entities** (7); the
            // ship is on **Player** (6). So the overlap never returned the ship
            // and the pullPlayer check downstream was never reached to say yes.
            // Friend/foe is still decided per hit, in Allowed() - this mask only
            // decides who is even considered.
            filter.SetLayerMask(
                LayerMask.GetMask("Entities", "Fruits", "Player"));
        }

        private void Update()
        {
            if (g == null)
            {
                Destroy(this);
                return;
            }

            float age = Time.time - g.openedAt;

            if (age > HardLife || (g.duration > 0f && age > g.duration))
            {
                Close();
                return;
            }

            if (g.pullShots)
                Swallow();

            if (g.damagePerSecond > 0f)
                Bleed();
        }

        // Take any new enemy fire in range, then steer everything held toward
        // the middle and eat what arrives.
        private void Swallow()
        {
            ForgeProjectileTracker.CollectNear(
                transform.position, g.radius, 0, caught);

            Vector2 centre = transform.position;

            for (int i = caught.Count - 1; i >= 0; i--)
            {
                Component c = caught[i];

                if (c == null)
                {
                    caught.RemoveAt(i);
                    continue;
                }

                Vector2 to = centre - (Vector2)c.transform.position;

                // Arrived - the well has eaten it.
                if (to.sqrMagnitude <= g.holdRadius * g.holdRadius)
                {
                    caught.RemoveAt(i);
                    Destroy(c.gameObject);
                    continue;
                }

                var p = c as IProjectile;

                if (p == null)
                    continue;

                float speed = p.Velocity.magnitude;

                if (speed <= 0.01f)
                    speed = 6f;

                Vector2 dir = to.normalized;

                p.Velocity = dir * speed;

                // idealDirection too, not just Velocity: the game's own wobble
                // rebuilds Velocity from that field every frame and would drag
                // the shot straight back onto its original heading.
                var plain = c as Projectile;

                if (plain != null && _idealRef != null)
                    _idealRef(plain) = dir;
            }
        }

        private void Bleed()
        {
            EnsureFilters();

            int n = Physics2D.OverlapCircle(
                transform.position, g.radius, filter, buffer);

            for (int i = 0; i < n; i++)
            {
                if (buffer[i] == null)
                    continue;

                if (!Hostile(buffer[i]))
                    continue;

                var hb = buffer[i].GetComponentInParent<HealthBase>();

                if (hb == null)
                    continue;

                float last;

                // Once a second, not every frame: TakeDamage drives the hit
                // flash, the sound and the aggro event.
                if (lastHit.TryGetValue(hb, out last) &&
                    Time.time - last < 1f)
                {
                    continue;
                }

                lastHit[hb] = Time.time;

                var proj = GetComponent<Projectile>();
                var phys = GetComponent<PhysicsProjectile>();

                IProjectile p = (proj != null)
                    ? (IProjectile)proj
                    : (IProjectile)phys;

                if (p == null)
                    return;

                Damage d = p.Damage;
                d.amount = g.damagePerSecond;

                hb.TakeDamage(d);
            }
        }

        private bool Hostile(Collider2D col)
        {
            Unit u = col.GetComponentInParent<Unit>();

            if (u == null)
                return true;   // fruit and other destructibles

            if (g.owner == null)
                return true;

            if (ReferenceEquals(u, g.owner))
                return g.pullPlayer;

            return g.pullPlayer || !g.owner.IsFriendsWith(u);
        }

        private void FixedUpdate()
        {
            if (g == null || !g.open)
                return;

            try
            {
                Haul();
            }
            catch (System.Exception e)
            {
                Log.LogError("Gravity well failed: " + e);
                g.open = false;
            }
        }

        // The reason `force: 34` looked broken and `force: 9000` looked
        // necessary.
        //
        // AddForce fights TWO things, and PUNK's bodies differ wildly in both:
        //
        //     ship                 mass 1    damping 0.5
        //     Enemy_Cross          mass 5    damping 6
        //     Enemy_Cross Jock     mass 50   damping 4
        //     Enemy_CrossQueen     mass 200  damping 4
        //
        // A constant force settles at terminal speed force / (mass * damping),
        // so the SAME number produces 68 units/second on the ship and 1.1 on a
        // Cross - a 60x spread, and over 1000x against the Queen. That is why
        // no single value ever worked on everything.
        //
        // Compensating by mass * damping makes `force` mean one thing on every
        // body: terminal speed is force / 30, normalised to the common Cross
        // (5 * 6 = 30) so numbers tuned against ordinary enemies keep their
        // feel. `maxSpeed` still caps the result.
        private float Compensation(Rigidbody2D rb)
        {
            if (!g.massCompensated)
                return 1f;

            float damping = Mathf.Max(0.05f, rb.linearDamping);
            float mass = Mathf.Max(0.01f, rb.mass);

            return (mass * damping) / 30f;
        }

        private void Haul()
        {
            EnsureFilters();

            Vector2 centre = transform.position;

            int n = Physics2D.OverlapCircle(centre, g.radius, filter, buffer);

            for (int i = 0; i < n; i++)
            {
                Collider2D col = buffer[i];

                if (col == null)
                    continue;

                Rigidbody2D rb = col.attachedRigidbody;

                if (rb == null)
                    continue;

                if (!Hostile(col))
                    continue;

                Vector2 to = centre - rb.position;
                float d = to.magnitude;

                // In the eye of it. Pulling here would overshoot the centre
                // every frame and yank back, which reads as a seizure rather
                // than a crowd being held.
                if (d <= g.holdRadius)
                {
                    if (g.drag > 0f)
                    {
                        rb.linearVelocity *=
                            Mathf.Max(0f, 1f - g.drag * Time.fixedDeltaTime);
                    }

                    continue;
                }

                rb.AddForce(to / d * g.ForceAt(d) * Compensation(rb));

                if (g.drag > 0f)
                {
                    rb.linearVelocity *=
                        Mathf.Max(0f, 1f - g.drag * Time.fixedDeltaTime);
                }

                // A heavy pull with no ceiling turns a light enemy into a
                // bullet aimed at whatever is on the far side.
                if (g.maxSpeed > 0f &&
                    rb.linearVelocity.sqrMagnitude > g.maxSpeed * g.maxSpeed)
                {
                    rb.linearVelocity =
                        rb.linearVelocity.normalized * g.maxSpeed;
                }
            }
        }

        private void Close()
        {
            g.open = false;

            try
            {
                if (g.burst)
                    Explode();
            }
            catch (System.Exception e)
            {
                Log.LogError("Gravity well burst failed: " + e);
            }

            Destroy(gameObject);
        }

        private void Explode()
        {
            var proj = GetComponent<Projectile>();
            var phys = GetComponent<PhysicsProjectile>();

            IProjectile p = (proj != null)
                ? (IProjectile)proj
                : (IProjectile)phys;

            if (p == null)
                return;

            Explosion ex = p.Explosion;

            // SpawnExplosion dereferences damages[0].damageType and throws on
            // an empty list. The builder only allows burst when there is one.
            if (ex.damages == null || ex.damages.Count == 0 || ex.radius <= 0f)
                return;

            ex.Owner = p.Owner;
            ex.Burn = p.Burn;

            ServiceLocator.Get<ExplosionManager>()
                .SpawnExplosion(transform.position, ex);
        }
    }
}
