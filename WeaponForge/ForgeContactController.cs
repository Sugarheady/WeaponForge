using System;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;

namespace WeaponForge
{
    // Rides a unit and makes it dangerous to touch.
    //
    // A DIRECT COPY OF ChargerHead's SHAPE, which is the game's own answer to
    // fast-moving contact damage: a swept CircleCast along the rigidbody's
    // velocity every FixedUpdate rather than a collision callback. The reason
    // is in ForgeContact - every unit rigidbody is DISCRETE, so at the speeds a
    // shove weapon produces a callback simply never fires.
    //
    // ONE DIFFERENCE FROM ChargerHead, and it matters: the stock one rate-
    // limits GLOBALLY (one `lastDamageTime` for the whole component), so a
    // charging Maggot that ploughs through three enemies only hurts the first.
    // This keys the delay PER TARGET, so one unit flung through a crowd hits
    // each of them once - which is the whole point of the feature.
    public class ForgeContactController : MonoBehaviour
    {
        private static readonly ManualLogSource Log =
            ForgeLog.Source("WeaponForge.Contact");

        private ForgeContact.Config cfg;
        private Unit self;
        private Unit attacker;
        private WeaponBase weapon;
        private Rigidbody2D body;

        private float expiresAt;        // 0 = permanent
        private float radius;
        private int mask;
        private bool ready;

        // Granted by the always-on sweep rather than by a hit or a lunge. Only
        // these are torn off when the weapon carrying the rule is unequipped -
        // a marked hit and a lunge's contact window own their own lifetimes and
        // must survive it.
        private bool fromWorld;

        // ⚠ A KINEMATIC BODY HAS NO `linearVelocity`, AND THE SWEEP IS BUILT ON
        // ONE. `DetectSwept` reads `rigidbody.linearVelocity` every FixedUpdate
        // and returns early below 0.01 - and a body moved by its transform
        // (every wingman drone under the default `physics: false`) reports
        // exactly 0. So contact damage on one would have done NOTHING AT ALL,
        // silently, with every number in the build log correct.
        //
        // Whoever is driving such a body knows where it was last frame, so it
        // supplies the velocity instead. That fixes the sweep AND makes
        // `scaleBySpeed` meaningful there, which is the whole appeal of a
        // tackle: a fast-spinning ring hits harder than a parked one.
        //
        // `recoil` still does nothing on a kinematic body - there is no
        // simulation to push back - and that is said where the block is built
        // rather than swallowed here.
        public Vector2 drivenVelocity;
        public bool hasDrivenVelocity;

        private readonly Dictionary<HealthBase, float> lastHit =
            new Dictionary<HealthBase, float>();

        private static readonly RaycastHit2D[] _hits = new RaycastHit2D[16];
        private static ContactFilter2D _filter = new ContactFilter2D();

        // Once per run each: these sit on per-hit and per-unit paths.
        private static bool _saidNoType;
        private static bool _saidRadius;

        // ------------------------------------------------------------------
        public static void Give(
            Unit target, ForgeContact.Config cfg,
            Unit attacker, WeaponBase weapon, float seconds,
            bool fromWorld = false)
        {
            if (target == null || cfg == null)
                return;

            try
            {
                var c = target.gameObject.GetComponent<ForgeContactController>();

                if (c == null)
                    c = target.gameObject.AddComponent<ForgeContactController>();

                c.cfg = cfg;
                c.self = target;
                c.attacker = attacker;
                c.weapon = weapon;
                c.ready = false;
                c.fromWorld = fromWorld;

                // Refreshed rather than stacked, the same rule the slam mark
                // uses: being hit twice keeps you dangerous, it does not make
                // you more dangerous.
                c.expiresAt = (seconds > 0f) ? Time.time + seconds : 0f;

                if (cfg.hasTint)
                {
                    ForgeUnitTint.Set(
                        target, ForgeUnitTint.Contact, cfg.tint,
                        seconds, cfg.tintPulse, cfg.tintStrength,
                        cfg.tintMode);
                }
            }
            catch (Exception e)
            {
                Log.LogError("Giving a unit contact damage failed: " + e);
            }
        }

        // WHO GETS THE CREDIT, supplied late.
        //
        // `Give` takes an attacker and a weapon, and for every caller but one
        // both are known at that moment. A WINGMAN DRONE is the exception: the
        // body exists from the moment it is deployed, and the weapon it would
        // credit - the runtime copy of whatever you are carrying - is not built
        // until the first shot, and is rebuilt whenever the module grid
        // changes. Without this, a tackle kill would credit nobody and no
        // `onKill` reward would ever pay for one, silently.
        //
        // Two field writes, so the driver can call it every frame rather than
        // tracking whether the weapon has changed. `Give` is the wrong tool
        // for that - it re-runs Setup and re-publishes the tint layer.
        public void Attribute(Unit by, WeaponBase with)
        {
            attacker = by;
            weapon = with;
        }

        // Put the world back: strip every controller the always-on sweep handed
        // out, and nothing else. Returns how many, for the log.
        //
        // Only called on the transition to "no world-rule weapon equipped", so
        // the FindObjectsByType is a one-off rather than a per-tick cost.
        public static int RevokeWorld()
        {
            int n = 0;

            try
            {
                ForgeContactController[] all =
                    UnityEngine.Object.FindObjectsByType<ForgeContactController>(
                        FindObjectsSortMode.None);

                for (int i = 0; i < all.Length; i++)
                {
                    ForgeContactController c = all[i];

                    if (c == null || !c.fromWorld)
                        continue;

                    // The tint is a separate layer and has to be released by
                    // hand, or an enemy keeps the contact colour for the rest of
                    // the run - exactly the permanent-tint trap ForgeUnitTint
                    // exists to prevent.
                    if (c.cfg != null && c.cfg.hasTint && c.self != null)
                        ForgeUnitTint.Clear(c.self, ForgeUnitTint.Contact);

                    UnityEngine.Object.Destroy(c);
                    n++;
                }
            }
            catch (Exception e)
            {
                Log.LogError("Revoking world contact damage failed: " + e);
            }

            return n;
        }

        // ------------------------------------------------------------------
        private void Setup()
        {
            ready = true;

            body = GetComponent<Rigidbody2D>();

            if (body == null)
                body = GetComponentInChildren<Rigidbody2D>();

            // The unit's own collider is almost always the right radius, and
            // taking it from there means one setting fits a Larva and a Queen.
            radius = cfg.radius;

            if (radius <= 0f)
            {
                var col = GetComponentInChildren<Collider2D>();

                radius = (col != null)
                    ? Mathf.Max(0.1f, col.bounds.extents.magnitude * 0.7f)
                    : 0.5f;

                // Printed because he asked what the default actually is ("for
                // lunge.radius, can you list the default ship's collider?").
                // It is derived per unit rather than being a number anyone could
                // look up, so the honest answer is to show what it resolved to.
                if (!_saidRadius)
                {
                    _saidRadius = true;

                    Log.LogInfo(
                        "contact radius resolved to " +
                        radius.ToString("0.##") + " on '" + gameObject.name +
                        "' from its own collider" +
                        ((col != null)
                            ? (" (bounds extents " +
                               col.bounds.extents.ToString("0.##") + ")")
                            : " (none found - using 0.5)") +
                        ". Set \"radius\" yourself to override it. Said once " +
                        "per run.");
                }
            }

            int m = 0;

            if (cfg.hitsEnemies)
                m |= LayerMask.GetMask("Entities", "Fruits");

            if (cfg.hitsPlayer)
                m |= LayerMask.GetMask("Player");

            mask = m;
        }

        private bool Live
        {
            get { return expiresAt <= 0f || Time.time < expiresAt; }
        }

        private void FixedUpdate()
        {
            if (cfg == null)
                return;

            if (!Live)
            {
                Destroy(this);
                return;
            }

            if (cfg.detect != ForgeContact.DetectSwept)
                return;

            try
            {
                if (!ready)
                    Setup();

                if ((body == null && !hasDrivenVelocity) || mask == 0)
                    return;

                Vector2 v = hasDrivenVelocity
                    ? drivenVelocity
                    : body.linearVelocity;

                float speed = v.magnitude;

                if (speed < 0.01f)
                    return;

                // Exactly ChargerHead's sweep: how far this body will travel
                // before the next step. That is the gap a discrete collision
                // would fall into.
                float distance = speed * Time.fixedDeltaTime;

                _filter.useTriggers = Physics2D.queriesHitTriggers;
                _filter.SetLayerMask(mask);

                int n = Physics2D.CircleCast(
                    transform.position, radius, v / speed, _filter, _hits,
                    distance);

                for (int i = 0; i < n && i < _hits.Length; i++)
                    Consider(_hits[i].collider, _hits[i].point, v / speed, speed);
            }
            catch (Exception e)
            {
                Log.LogError("A contact sweep failed: " + e);
                enabled = false;
            }
        }

        // The other detection mode, for comparison and for anything slow
        // enough that a callback is honest. Same handling either way.
        private void OnCollisionEnter2D(Collision2D collision)
        {
            if (cfg == null || !Live ||
                cfg.detect != ForgeContact.DetectCollision)
            {
                return;
            }

            try
            {
                if (!ready)
                    Setup();

                if (collision == null || collision.collider == null)
                    return;

                Vector2 v = hasDrivenVelocity
                    ? drivenVelocity
                    : (body != null) ? body.linearVelocity : Vector2.zero;

                float speed = v.magnitude;

                Vector2 dir = (speed > 0.01f) ? v / speed : Vector2.right;

                Vector2 at = (collision.contactCount > 0)
                    ? collision.GetContact(0).point
                    : (Vector2)transform.position;

                Consider(collision.collider, at, dir, speed);
            }
            catch (Exception e)
            {
                Log.LogError("A contact collision failed: " + e);
            }
        }

        // ------------------------------------------------------------------
        private void Consider(
            Collider2D col, Vector2 at, Vector2 dir, float speed)
        {
            if (col == null)
                return;

            Unit other = col.GetComponentInParent<Unit>();

            if (other == null || ReferenceEquals(other, self))
                return;

            // Never the thing that made this dangerous in the first place.
            if (attacker != null && ReferenceEquals(other, attacker) &&
                !cfg.hitsPlayer)
            {
                return;
            }

            var hb = other.GetComponentInChildren<HealthBase>();

            // IsDead lives on DamagableResource, not on HealthBase - so a
            // corpse has to be spotted by its health instead. Worth doing:
            // ploughing through corpses would burn the per-target budget on
            // things that cannot be hurt.
            if (hb == null || hb.CurrentHealth <= 0f)
                return;

            float last;

            if (lastHit.TryGetValue(hb, out last) &&
                Time.time - last < Mathf.Max(0.02f, cfg.repeatDelay))
            {
                return;
            }

            if (lastHit.Count > 32)
                Prune();

            lastHit[hb] = Time.time;

            float amount = cfg.damage;

            if (cfg.scaleBySpeed)
            {
                if (speed < cfg.minSpeed)
                    return;

                amount = Mathf.Min(
                    cfg.maxDamage, (speed - cfg.minSpeed) * cfg.damagePerSpeed);
            }

            if (amount > 0f)
                Hurt(hb, other, amount);

            Rigidbody2D rb = col.attachedRigidbody;

            if (cfg.push != 0f && rb != null)
                rb.AddForceAtPosition(dir * cfg.push, at, ForceMode2D.Impulse);

            // The carrier bounces off, exactly as ChargerHead does. Without it
            // a shoved unit ploughs straight on and the impact reads as
            // nothing having happened.
            //
            // A KINEMATIC carrier cannot bounce - it is not simulated, so there
            // is nothing to push back - and asking anyway is a silent no-op
            // rather than an error. Tested explicitly so the omission is a
            // decision in the code rather than a fact about Unity that has to
            // be remembered.
            if (cfg.recoil != 0f && body != null &&
                body.bodyType == RigidbodyType2D.Dynamic)
            {
                body.AddForceAtPosition(-dir * cfg.recoil, at, ForceMode2D.Impulse);
            }
        }

        private void Hurt(HealthBase hb, Unit victim, float amount)
        {
            try
            {
                Damage d = new Damage();
                d.amount = amount;

                d.damageType = cfg.damageType
                    ?? ((weapon != null) ? weapon.Damage.damageType : null);

                // A BARE `return` HERE COST TWO TEST ROUNDS. The always-on
                // modes arrive with no weapon, so this used to resolve to null
                // and swallow every point of contact damage without a word -
                // "they bump into each other and doesnt seem to hurt one
                // another". The builder now resolves a type at build time so it
                // cannot happen from a weapon file, but the lunge builds a
                // Config in code too, so the guard stays - and says so.
                if (d.damageType == null)
                {
                    if (!_saidNoType)
                    {
                        _saidNoType = true;
                        Log.LogWarning(
                            "contact damage has no damageType and no weapon to " +
                            "borrow one from, so it cannot deal damage. Name a " +
                            "\"damageType\" in the block that set it up. Said " +
                            "once per run.");
                    }

                    return;
                }

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

                // The game credits a kill from exactly three places and none of
                // them is ours - the same reason the swipe and the gas clouds
                // have to do this. Note the game's OWN hazards do not, so a
                // Larva's beak kill credits nobody either.
                if (attacker != null && weapon != null &&
                    ForgeKillsPatch.JustDied(hb))
                {
                    ForgeKillsPatch.Credit(attacker, victim, weapon);
                }
            }
            catch (Exception e)
            {
                Log.LogError("Contact damage failed: " + e);
            }
        }

        private void Prune()
        {
            var dead = new List<HealthBase>();

            foreach (var kv in lastHit)
            {
                if (kv.Key == null || Time.time - kv.Value > 3f)
                    dead.Add(kv.Key);
            }

            for (int i = 0; i < dead.Count; i++)
                lastHit.Remove(dead[i]);
        }

        private void OnDestroy()
        {
            if (self != null && cfg != null && cfg.hasTint)
                ForgeUnitTint.Clear(self, ForgeUnitTint.Contact);
        }
    }
}
