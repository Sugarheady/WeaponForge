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
            ForgeLog.Source("WeaponForge.Slam");

        // ------------------------------------------------------------------
        // "slam" as a top-level block on ANY weapon.
        //
        // The machinery below was written for the tractor beam, which is the
        // only thing that could reach it - and that was a shame, because
        // "shove them and let the wall do the work" is a whole weapon idea and
        // the tractor is a held beam. A projectile that pushes is already a
        // thing the game does (`pushForce`, an impulse applied at the hit
        // point); the only missing piece was marking what it hit as slammable.
        //
        // Registered against the WeaponData, the same shape every other block
        // uses, and resolved at the hit rather than carried on the prefab -
        // which sidesteps the config-reference-on-a-prefab trap entirely.
        public class Spec
        {
            public float minSpeed = 6f;
            public float damagePerSpeed = 1.5f;
            public float maxDamage = 999f;
            public float window = 1.5f;
            public bool hitsUnits = true;
            public bool hitsPlayer;
            public Resource damageType;

            // Must the hit that marks them also SHOVE them?
            //
            // OFF by default, and that is the whole point of the block being
            // standalone: "this one is brittle for the next second and a half"
            // is a debuff, and what throws them into the wall afterwards does
            // not have to be the same weapon - or a weapon at all. A gravity
            // well, an explosion, a tractor beam, another gun, their own
            // charge, or just being flung by something else all count.
            //
            // Turn it on for a weapon where the shove IS the point and a
            // grazing hit should not count.
            public bool requiresPush;

            // ---- the tell -------------------------------------------------
            //
            // A colour worn by whatever is currently slammable, so you can SEE
            // who is worth throwing. Without it the whole mechanic is
            // invisible: "brittle for the next second and a half" has no tell
            // at all, and a player cannot aim at a state they cannot see.
            //
            // Off unless asked for. It goes through ForgeUnitTint, so it
            // composes with the `slow` tint instead of fighting it - which is
            // the reason this was offered and not built until now.
            public Color tint = Color.white;
            public bool hasTint;

            // Throbs per second. A slammable unit is a TEMPORARY state, and a
            // pulse reads as a timer running out in a way a steady colour
            // cannot. 0 is steady.
            public float tintPulse = 3f;

            // HOW HARD THE TINT COMES THROUGH, 0..1. 1 is the full colour and
            // is what this has always done; 0.35 is a hint of it. Read from
            // either "tintStrength" or "colorStrength" so the two naming
            // styles in this mod's blocks both work. See ForgeUnitTint.
            public float tintStrength = 1f;

            // multiply (the default, and what every existing file was tuned
            // against) or replace, which moves the sprite TOWARD the colour so
            // it can brighten a dark enemy. See ForgeUnitTint.
            public int tintMode = ForgeUnitTint.Multiply;

            // ---- how the arrival is detected ------------------------------
            //
            // OFF by default, deliberately: this is what shipped and it is not
            // being changed underneath anything already tested.
            //
            // The reason it exists: **every unit rigidbody in this game uses
            // DISCRETE collision detection** (only ammo prefabs use
            // Continuous) and the fixed timestep is 0.02s, so a body moving
            // faster than about 50 units/s skips a 1-unit object between
            // steps. A pushForce of 220 on a common Cross is 44 u/s - already
            // marginal - and `slow`'s knockback HALVES the mass, taking it to
            // 88 u/s, or 1.76 units per step. So the hardest, most spectacular
            // throws are exactly the ones OnCollisionEnter2D can miss.
            //
            // On, arrival is found the way the game's own charging enemies
            // find it: a swept CircleCast along the velocity each physics
            // step. Expect it to trigger MORE often, including on grazes the
            // callback never saw.
            public bool swept;
        }

        private static readonly System.Collections.Generic.Dictionary
            <WeaponData, Spec> _weapons =
                new System.Collections.Generic.Dictionary<WeaponData, Spec>();

        public static void Register(WeaponData weapon, Spec spec)
        {
            if (weapon != null && spec != null)
                _weapons[weapon] = spec;
        }

        public static bool TryGet(WeaponData weapon, out Spec spec)
        {
            spec = null;
            return weapon != null && _weapons.TryGetValue(weapon, out spec);
        }

        public static bool Any { get { return _weapons.Count > 0; } }

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

        // Find the arrival by sweeping rather than by waiting for a collision
        // callback. See Spec.swept for why - in one line: every unit rigidbody
        // is DISCRETE, so the hardest throws skip right through.
        public bool swept;

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

            return Mark(target, attacker, weapon, damageType,
                        cfg.slamMinSpeed, cfg.slamDamagePerSpeed,
                        cfg.slamMaxDamage, cfg.slamWindow,
                        cfg.slamHitsUnits, cfg.slamHitsPlayer,
                        cfg.slamHasTint, cfg.slamTint, cfg.slamTintPulse,
                        cfg.slamTintStrength,
                        // The tractor's slam tint has no mode of its own, so it
                        // keeps the multiply every existing file was tuned
                        // against. Adding one here would be a fourth place to
                        // read the same key for a block that never asked.
                        ForgeUnitTint.Multiply,
                        // The tractor's own slam predates the standalone block
                        // and keeps the collision route; `swept` is offered on
                        // the `slam` block only, which is where it was asked
                        // for.
                        false);
        }

        // The projectile / beam route. Same component, same window semantics -
        // only the numbers arrive from a different block.
        public static ForgeSlam Mark(
            Unit target, Spec spec, Unit attacker,
            WeaponBase weapon, Resource damageType)
        {
            if (target == null || spec == null)
                return null;

            return Mark(target, attacker, weapon,
                        spec.damageType ?? damageType,
                        spec.minSpeed, spec.damagePerSpeed, spec.maxDamage,
                        spec.window, spec.hitsUnits, spec.hitsPlayer,
                        spec.hasTint, spec.tint, spec.tintPulse,
                        spec.tintStrength, spec.tintMode, spec.swept);
        }

        private static ForgeSlam Mark(
            Unit target, Unit attacker, WeaponBase weapon,
            Resource damageType,
            float minSpeed, float damagePerSpeed, float maxDamage,
            float window, bool hitsUnits, bool hitsPlayer,
            bool hasTint, Color tint, float tintPulse, float tintStrength,
            int tintMode, bool swept)
        {
            if (target == null)
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

                s.minSpeed = minSpeed;
                s.damagePerSpeed = damagePerSpeed;
                s.maxDamage = maxDamage;
                s.hitsUnits = hitsUnits;
                s.hitsPlayer = hitsPlayer;
                s.damageType = damageType;
                s.attacker = attacker;
                s.weapon = weapon;
                s.swept = swept;
                float life = Mathf.Max(0.1f, window);

                s.expiresAt = Time.time + life;

                // THE TELL. Timed to the same window rather than cleared when
                // the marker component dies, because the marker deliberately
                // lingers a second past expiry before tidying itself up - a
                // colour that outlived the state it advertises would be worse
                // than no colour at all.
                //
                // Re-marking refreshes it: ForgeUnitTint replaces a layer with
                // the same key rather than adding a second one, so being held
                // in a tractor beam keeps the tint steady instead of stacking
                // it darker and darker.
                if (hasTint)
                {
                    ForgeUnitTint.Set(
                        target, ForgeUnitTint.Slam, tint, life, tintPulse,
                        tintStrength, tintMode);
                }

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

                // Opt this damage into crit. Off unless the weapon's file says
                // "crit": { "onExtras": true } - see ForgeCrit.
                WeaponBase critPrev = ForgeCrit.Begin(weapon);
                try
                {
                    hb.TakeDamage(d);
                }
                finally
                {
                    ForgeCrit.End(critPrev);
                }

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

        // ------------------------------------------------------------------
        // The swept route, off unless `slam.swept` asked for it.
        //
        // Copied from the game's own ChargerHead, which is what the four
        // charging enemies use INSTEAD of collision callbacks - because a
        // discrete rigidbody moving at charge speed steps straight over the
        // thing it is charging at. Same problem, same answer.
        // ------------------------------------------------------------------
        private Rigidbody2D sweptBody;
        private int sweptMask;
        private float sweptRadius;
        private bool sweptReady;

        private static readonly RaycastHit2D[] _sweep = new RaycastHit2D[16];
        private static ContactFilter2D _sweepFilter = new ContactFilter2D();

        private void FixedUpdate()
        {
            if (!swept || !Live)
                return;

            try
            {
                if (!sweptReady)
                {
                    sweptReady = true;

                    sweptBody = GetComponent<Rigidbody2D>();

                    var col = GetComponentInChildren<Collider2D>();

                    sweptRadius = (col != null)
                        ? Mathf.Max(0.1f, col.bounds.extents.magnitude * 0.7f)
                        : 0.5f;

                    sweptMask = LayerMask.GetMask("Ground", "Entities", "Fruits");

                    if (hitsPlayer)
                        sweptMask |= LayerMask.GetMask("Player");
                }

                if (sweptBody == null || Time.time < nextHitAt)
                    return;

                Vector2 v = sweptBody.linearVelocity;
                float speed = v.magnitude;

                if (speed < minSpeed)
                    return;

                _sweepFilter.useTriggers = Physics2D.queriesHitTriggers;
                _sweepFilter.SetLayerMask(sweptMask);

                int n = Physics2D.CircleCast(
                    transform.position, sweptRadius, v / speed, _sweepFilter,
                    _sweep, speed * Time.fixedDeltaTime);

                for (int i = 0; i < n && i < _sweep.Length; i++)
                {
                    Collider2D col = _sweep[i].collider;

                    if (col == null)
                        continue;

                    Unit other = col.GetComponentInParent<Unit>();

                    if (other != null && ReferenceEquals(other, self))
                        continue;

                    Land(other, speed);
                    return;
                }
            }
            catch (Exception e)
            {
                Log.LogError("A swept slam failed: " + e);
                swept = false;
            }
        }

        // The shared payout, so the swept route and the collision route can
        // never drift apart on what an arrival costs.
        private void Land(Unit other, float speed)
        {
            nextHitAt = Time.time + 0.15f;

            float amount = Mathf.Min(
                maxDamage, (speed - minSpeed) * damagePerSpeed);

            if (amount <= 0f)
                return;

            Hurt(self, amount);

            if (!hitsUnits || other == null || ReferenceEquals(other, self))
                return;

            if (!hitsPlayer && attacker != null && ReferenceEquals(attacker, other))
                return;

            Hurt(other, amount);
        }
    }
}
