using BepInEx.Logging;
using UnityEngine;

namespace WeaponForge
{
    // The living half of a harpoon: the rope, the force, and the ways it can
    // end. Unity drives it, so no patch is involved - the same shape as
    // ForgeStickyController.
    //
    // The force model is the game's own Hook, deliberately: pull only past the
    // rest length, and stop pulling once the gap is already closing fast
    // enough. Those two rules together are what make a rope read as taut
    // rather than magnetic, and they are already tuned to this game's masses.
    public class ForgeTetherController : MonoBehaviour
    {
        private static readonly ManualLogSource Log =
            ForgeLog.Source("WeaponForge.Tether");

        private ForgeTether t;
        private LineRenderer rope;
        private float lastDamage;

        // A rope with no way to end would survive the room it was fired in.
        private const float HardLife = 30f;

        public void Begin(ForgeTether tether)
        {
            t = tether;
            t.restLength = Mathf.Max(0.25f, t.length);

            BuildRope();
        }

        private void BuildRope()
        {
            if (t.ropeWidth <= 0f)
                return;

            var go = new GameObject("Forge Tether Rope");
            rope = go.AddComponent<LineRenderer>();

            rope.useWorldSpace = true;
            rope.positionCount = 2;
            rope.widthMultiplier = t.ropeWidth;
            rope.numCapVertices = 2;

            if (t.ropeMaterial != null)
                rope.sharedMaterial = t.ropeMaterial;

            if (t.hasRopeColor)
            {
                rope.startColor = t.ropeColor;
                rope.endColor = t.ropeColor;
            }
        }

        private void OnDestroy()
        {
            if (rope != null)
                Destroy(rope.gameObject);
        }

        // The two ends of the rope, which are NOT the same pair in every mode.
        // Anchor pins the victim to the spot it was struck, so its rope runs
        // from that spot; everything else runs from the ship.
        private bool Ends(out Vector2 from, out Vector2 to)
        {
            from = Vector2.zero;
            to = transform.position;

            if (t.pull == ForgeTether.Pull.Anchor)
            {
                from = t.anchorPoint;
                to = (t.host != null) ? (Vector2)t.host.position : to;
                return true;
            }

            if (t.owner == null)
                return false;

            from = t.owner.transform.position;
            return true;
        }

        private void Update()
        {
            if (t == null)
            {
                Destroy(gameObject);
                return;
            }

            // Ride the victim, except in Anchor - there the head IS the peg
            // and staying put is the whole mechanic.
            if (t.pull != ForgeTether.Pull.Anchor && t.host != null)
                transform.position = t.host.TransformPoint(t.hostOffset);

            Vector2 a, b;

            if (!Ends(out a, out b))
            {
                Destroy(gameObject);
                return;
            }

            if (rope != null)
            {
                rope.SetPosition(0, a);
                rope.SetPosition(1, b);
            }

            // Ends: the victim died, the timer ran out, or it was stretched
            // past breaking.
            if (t.host == null && t.pull != ForgeTether.Pull.Self)
            {
                Destroy(gameObject);
                return;
            }

            float age = Time.time - t.latchedAt;

            if (age > HardLife ||
                (t.duration > 0f && age > t.duration))
            {
                Destroy(gameObject);
                return;
            }

            if (t.breakDistance > 0f &&
                Vector2.Distance(a, b) > t.breakDistance)
            {
                Destroy(gameObject);
                return;
            }

            if (t.reelSpeed > 0f)
            {
                t.restLength = Mathf.Max(
                    0.25f, t.restLength - t.reelSpeed * Time.deltaTime);
            }

            if (t.damagePerSecond > 0f)
                Bleed();
        }

        private void Bleed()
        {
            // Once a second rather than continuously: HealthBase.TakeDamage
            // drives hit flashes, sounds and the aggro event, and running that
            // every frame is a strobe with a headache.
            if (Time.time - lastDamage < 1f)
                return;

            lastDamage = Time.time;

            if (t.host == null)
                return;

            var hb = t.host.GetComponentInParent<HealthBase>();

            if (hb == null)
                return;

            var proj = GetComponent<Projectile>();
            var phys = GetComponent<PhysicsProjectile>();

            IProjectile p = (proj != null)
                ? (IProjectile)proj
                : (IProjectile)phys;

            if (p == null)
                return;

            Damage d = p.Damage;
            d.amount = t.damagePerSecond;

            hb.TakeDamage(d);
        }

        private void FixedUpdate()
        {
            if (t == null || !t.latched)
                return;

            try
            {
                Haul();
            }
            catch (System.Exception e)
            {
                Log.LogError("Tether force failed: " + e);
                t.latched = false;
            }
        }

        private void Haul()
        {
            Rigidbody2D ship =
                (t.owner != null) ? t.owner.RigidBody : null;

            Rigidbody2D victim = t.hostBody;

            Vector2 anchor;
            Rigidbody2D pulled;

            if (t.pull == ForgeTether.Pull.Anchor)
            {
                // Pinned to the ground it was shot into, not to the ship.
                anchor = t.anchorPoint;
                pulled = victim;
            }
            else
            {
                if (ship == null)
                    return;

                anchor = ship.position;
                pulled = victim;
            }

            if (pulled == null && t.pull != ForgeTether.Pull.Self)
                return;

            Vector2 head = (pulled != null)
                ? pulled.position
                : (Vector2)transform.position;

            Vector2 span = head - anchor;
            float rest = t.restLength;

            // Inside the rope's length there is no rope - that is the whole
            // difference between a tether and a tractor beam.
            if (span.sqrMagnitude <= rest * rest)
                return;

            Vector2 shipVel = (ship != null) ? ship.linearVelocity : Vector2.zero;
            Vector2 headVel = (pulled != null) ? pulled.linearVelocity : shipVel;

            // Already closing fast enough? Leave it alone, or the rope
            // over-corrects and the pair shudders.
            Vector2 closing = headVel - shipVel;

            if (Vector2.Dot(closing, (anchor - head).normalized) >=
                t.maxClosingSpeed)
            {
                return;
            }

            // Only the OVERSTRETCH pulls, so the force grows with how far past
            // the rope's length you are - a real spring, not a constant tug.
            Vector2 over = span - span.normalized * rest;

            switch (t.pull)
            {
                case ForgeTether.Pull.Target:
                case ForgeTether.Pull.Anchor:
                    if (pulled != null)
                        pulled.AddForce(-over * t.stiffness);
                    break;

                case ForgeTether.Pull.Self:
                    if (ship != null)
                        ship.AddForce(over * t.stiffness);
                    break;

                case ForgeTether.Pull.Both:
                    if (pulled != null)
                        pulled.AddForce(-over * t.stiffness);

                    if (ship != null)
                        ship.AddForce(over * t.stiffness * t.selfPull);
                    break;
            }
        }
    }
}
