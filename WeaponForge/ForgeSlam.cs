using System;
using BepInEx.Logging;
using UnityEngine;

namespace WeaponForge
{
    // "Move enemies into walls at certain speeds to do damage."
    //
    // Rides on a unit that something of ours has thrown around, and hurts it
    // when it arrives somewhere hard. Removed by its own timer, so a unit only
    // stays slammable for a moment after it stops being pushed - otherwise
    // every enemy in the level would become permanently fragile on contact,
    // which is a different game.
    //
    // TWO DETECTION ROUTES, because the game gives us a good one for terrain
    // and none at all for units:
    //
    //   TERRAIN - LevelSegmentComponent.OnCollisionEnter2D already fires for
    //   anything with a rigidbody and hands every ICellCollisionListener on it a
    //   CellCollision carrying the FULL Collision2D. HealthBase implements that
    //   interface (it is how hazard cells do contact damage), so patching
    //   HealthBase.OnCellCollision gives us the unit, the impact and the cell
    //   for free - no component, no polling. See ForgeSlamPatch.
    //
    //   UNITS - nothing in this game does unit-to-unit contact damage, and there
    //   is no event for it. But the layer matrix does allow the collision
    //   (Entities collides with Entities AND with Player), so a plain
    //   OnCollisionEnter2D on the thrown unit is enough. That is this component.
    //
    // Impact hardness is the codebase's existing idiom -
    // Dot(relativeVelocity, contact.normal), the closing speed along the normal -
    // the same measure ImpactDamage, DestroyOnImpact and the bounce work use.
    //
    // NOTE this component is added to a LIVE unit at runtime, never to a prefab,
    // so holding real references here is safe. The config-reference trap that
    // broke gas and catalyst only applies to components that ride a prefab
    // through Instantiate.
    public class ForgeSlam : MonoBehaviour
    {
        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("WeaponForge.Slam");

        // Below this closing speed nothing happens. The game's own reference
        // point is 10: every ship prefab carries ImpactDamage at
        // minVelocity 10, which is what it considers a hard crash.
        public float minSpeed = 10f;

        // damage = (closing speed - minSpeed) * damagePerSpeed, capped.
        public float damagePerSpeed = 1f;
        public float maxDamage = 999f;

        public Resource damageType;

        // Does hitting another UNIT count, or only terrain?
        public bool hitsUnits;

        // May the thing it slams into be the PLAYER? Separate from hitsUnits
        // because "enemies crashing into each other" and "an enemy flung into
        // your hull hurts you" are different games.
        public bool hitsPlayer;

        [NonSerialized] public float expiresAt;
        [NonSerialized] public Unit attacker;
        [NonSerialized] public WeaponBase weapon;

        private Unit self;
        private float nextHitAt;

        public bool Live { get { return Time.time < expiresAt; } }

        private void Awake()
        {
            self = GetComponentInParent<Unit>();
        }

        // Refreshed rather than stacked: being held in a beam should keep you
        // slammable, not make you more slammable.
        public static ForgeSlam Mark(
            Unit target, ForgeTractor.Config cfg, Unit attacker,
            WeaponBase weapon, Resource damageType)
        {
            if (target == null || cfg == null || !cfg.slam)
                return null;

            try
            {
                // Must live on the object that carries the Rigidbody2D, or
                // OnCollisionEnter2D never fires for it.
                Rigidbody2D rb = target.GetComponentInChildren<Rigidbody2D>();
                GameObject host = (rb != null)
                    ? rb.gameObject : target.gameObject;

                ForgeSlam s = host.GetComponent<ForgeSlam>();

                if (s == null)
                    s = host.AddComponent<ForgeSlam>();

                s.minSpeed = cfg.slamMinSpeed;
                s.damagePerSpeed = cfg.slamDamagePerSpeed;
                s.maxDamage = cfg.slamMaxDamage;
                s.hitsUnits = cfg.slamHitsUnits;
                s.hitsPlayer = cfg.slamHitsPlayer;
                s.damageType = damageType;
                s.attacker = attacker;
                s.weapon = weapon;
                s.expiresAt = Time.time + Mathf.Max(0.1f, cfg.slamWindow);

                return s;
            }
            catch (Exception e)
            {
                Log.LogError("Marking a unit slammable failed: " + e);
                return null;
            }
        }

        // The shared measure, so terrain and unit impacts agree on what "hard"
        // means.
        public static float ClosingSpeed(Collision2D collision)
        {
            if (collision == null || collision.contactCount == 0)
                return collision != null
                    ? collision.relativeVelocity.magnitude : 0f;

            return Mathf.Abs(Vector2.Dot(
                collision.relativeVelocity, collision.GetContact(0).normal));
        }

        public void Resolve(Collision2D collision)
        {
            if (!Live)
                return;

            // A single arrival can report several contacts in the same frame.
            if (Time.time < nextHitAt)
                return;

            float speed = ClosingSpeed(collision);

            if (speed < minSpeed)
                return;

            nextHitAt = Time.time + 0.15f;

            float amount = Mathf.Min(
                maxDamage, (speed - minSpeed) * damagePerSpeed);

            if (amount <= 0f)
                return;

            Hurt(self, amount);

            // What it hit, if that is wanted and if it is a unit at all.
            if (!hitsUnits || collision.rigidbody == null)
                return;

            Unit other = collision.rigidbody.GetComponentInParent<Unit>();

            if (other == null || other == self)
                return;

            if (!hitsPlayer && attacker != null && attacker == other)
                return;

            Hurt(other, amount);
        }

        private void Hurt(Unit victim, float amount)
        {
            if (victim == null)
                return;

            try
            {
                var hb = victim.GetComponentInChildren<HealthBase>();

                if (hb == null)
                    return;

                Damage d = new Damage();
                d.amount = amount;
                d.damageType = damageType;

                hb.TakeDamage(d);

                // Nothing in the game credits a kill from mod-side damage, so
                // this has to register it properly - otherwise an onKill reward
                // silently never pays for a slam, and the music controller and
                // everything else listening never hears about it.
                if (attacker != null && weapon != null &&
                    ForgeKillsPatch.JustDied(hb))
                {
                    ForgeKillsPatch.Credit(attacker, victim, weapon);
                }
            }
            catch (Exception e)
            {
                Log.LogError("Slam damage failed: " + e);
            }
        }

        // Unit-to-unit. Terrain comes through ForgeSlamPatch instead, because
        // the game already dispatches that one per-unit with the collision
        // attached.
        private void OnCollisionEnter2D(Collision2D collision)
        {
            if (!Live || collision == null)
                return;

            // Terrain arrives here too, but the patch handles it - taking it in
            // both places would double the damage.
            if (collision.rigidbody == null)
                return;

            try
            {
                Resolve(collision);
            }
            catch (Exception e)
            {
                Log.LogError("Slam collision failed: " + e);
            }
        }

        private void Update()
        {
            // Tidy up rather than linger: an expired marker on a long-lived
            // enemy is dead weight on every collision it ever has.
            if (Time.time > expiresAt + 1f)
                Destroy(this);
        }
    }
}
