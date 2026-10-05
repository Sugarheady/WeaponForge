using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;

namespace WeaponForge
{
    // Turns a projectile into a DEPLOYABLE TURRET / MINE: while it is alive
    // it repeatedly fires another weapon, and (optionally) damages anything
    // touching it.
    //
    // Why this exists: the game has NO "while alive" hook on a projectile -
    // subEmitter only fires on death (range end / lifetime end / impact).
    // The game's own ProjectileDispenser fires a weapon from a live object,
    // but its weapon field is private and it only fires on Start, so it
    // can't be driven from JSON. This component is the same idea with full
    // control: it ticks on an interval, aims (rotating sweep or nearest
    // enemy), and follows the projectile because each volley builds a fresh
    // FakeBarrel at the CURRENT position.
    //
    // It is a plain MonoBehaviour on the projectile prefab, so Unity drives
    // Start/Update for us - no Harmony patch needed. Start() runs after the
    // game's FireSingle has set Owner/Damage, so those are safe to read.
    //
    // Firing costs NO resource: WeaponBase.Fire() only spawns projectiles;
    // the resource deduction lives in Shooter.Shoot, which we never touch.
    public class ForgeTurret : MonoBehaviour
    {
        public const int AimRotate = 0;   // sweep around at rotationSpeed
        public const int AimNearest = 1;  // aim at the closest enemy

        // ---- configured by WeaponBuilder on the cloned prefab ----
        public string weaponName;           // weapon to fire each volley
        public float interval = 0.5f;       // seconds between volleys
        public int aimMode = AimRotate;
        public float rotationSpeed = 90f;   // deg/sec (AimRotate)
        public bool clockwise = true;
        public float startAngle;            // first volley's angle (AimRotate)
        public float searchRange = 12f;     // target hunt radius (AimNearest)
        public float startDelay;            // wait before the first volley
        public bool contactDamage = true;
        public float contactRadius = 0.5f;
        public float contactRepeatDelay = 0.4f;

        // Backstop for a turret whose weapon somehow spawns more turrets
        // (a cycle the build-time guard didn't catch), and for a carrier
        // that was configured with no death condition at all.
        private const int MaxLive = 64;
        private const float HardLifetime = 30f;

        // ---- runtime ----
        private Projectile proj;
        private float nextFire;
        private float angle;
        private float bornAt;
        private Damage contactHit;   // damage we deal on touch
        private bool ownsDamage;

        private static int _live;

        // One WeaponBase per weapon NAME, shared by every turret using it.
        // Resolved lazily at runtime (not at build time) so the turret's
        // weapon does NOT have to load before this one - unlike subEmitter,
        // file naming/load order doesn't matter here. A null value caches a
        // failed/refused lookup so we don't rescan every volley.
        private static readonly Dictionary<string, WeaponBase> _weapons =
            new Dictionary<string, WeaponBase>();

        private static readonly ManualLogSource Log =
            ForgeLog.Source("WeaponForge");

        private ContactFilter2D enemyFilter;
        private bool filtersReady;
        private readonly List<Collider2D> buffer = new List<Collider2D>();
        private readonly Dictionary<HealthBase, float> lastHit =
            new Dictionary<HealthBase, float>();

        private void Start()
        {
            proj = GetComponent<Projectile>();
            angle = startAngle;
            nextFire = Time.time + Mathf.Max(0f, startDelay);
            bornAt = Time.time;
            _live++;

            // Take sole ownership of contact damage. The projectile's own
            // collision would ALSO damage whatever it touches, on its own
            // (often uncapped) schedule, so the two would stack. Zeroing
            // the carrier's damage leaves exactly one damage path with one
            // cooldown - and it works whether or not a parked projectile's
            // zero-length collision sweep still registers hits.
            if (contactDamage && proj != null)
            {
                contactHit = proj.Damage;
                Damage none = proj.Damage;
                none.amount = 0f;
                proj.Damage = none;
                ownsDamage = true;
            }
        }

        private void OnDestroy()
        {
            _live--;
        }

        private void EnsureFilters()
        {
            if (filtersReady)
                return;
            filtersReady = true;

            enemyFilter = new ContactFilter2D();
            enemyFilter.useTriggers = true;
            enemyFilter.SetLayerMask(LayerMask.GetMask("Entities", "Fruits"));
        }

        private void Update()
        {
            EnsureFilters();

            // Never let a turret outlive its welcome, even if its carrier
            // ended up with no death condition (the build step tries to
            // guarantee one, but clones carry HideAndDontSave so a stray
            // immortal projectile would survive scene changes too).
            if (Time.time - bornAt > HardLifetime)
            {
                Destroy(gameObject);
                return;
            }

            if (contactDamage)
                DamageAround(transform.position);

            // Accumulate instead of rebasing on Time.time, so the real rate
            // doesn't drift slower than configured; the guard stops a lag
            // spike from dumping a pile of volleys in one frame.
            float step = Mathf.Max(0.02f, interval);
            int guard = 0;
            while (Time.time >= nextFire && guard++ < 4)
            {
                nextFire += step;
                FireVolley();
            }
            if (nextFire < Time.time)
                nextFire = Time.time + step;
        }

        private void FireVolley()
        {
            if (_live > MaxLive)
                return;

            // Without an owner the spawned shots lose their friend/foe
            // guard, so sit this one out rather than risk hitting the ship.
            if (proj == null || proj.Owner == null)
                return;

            WeaponBase weapon = Resolve();
            if (weapon == null)
                return;

            Vector2 direction;
            if (aimMode == AimNearest)
            {
                // Nothing hostile in range -> hold fire this tick.
                if (!TryFindNearest(transform.position, out direction))
                    return;
            }
            else
            {
                direction = Quaternion.Euler(0f, 0f, angle) * Vector3.right;

                // Advance AFTER firing so startAngle is exactly the first
                // volley's bearing (it must not drift during startDelay).
                angle += (clockwise ? -1f : 1f) *
                    rotationSpeed * Mathf.Max(0.02f, interval);
            }

            // The WeaponBase is shared by every turret using this name, so
            // stamp OUR owner each volley. Reference-compare: a destroyed
            // Unit from a previous run is Unity-fake-null but not null.
            if (!ReferenceEquals(weapon.Owner, proj.Owner))
                weapon.Equip(proj.Owner);

            weapon.Fire(new FakeBarrel(transform.position, direction));
        }

        private bool TryFindNearest(Vector3 pos, out Vector2 direction)
        {
            direction = Vector2.up;

            int n = Physics2D.OverlapCircle(pos, searchRange, enemyFilter, buffer);
            float best = float.MaxValue;
            Vector3 bestPos = Vector3.zero;
            bool found = false;

            for (int i = 0; i < n; i++)
            {
                Collider2D c = buffer[i];
                if (c == null)
                    continue;

                // Only aim at something actually hostile - otherwise the
                // turret happily locks onto fruit and friendly minions.
                Unit u = c.GetComponentInParent<Unit>();
                if (u == null || !proj.Owner.IsEnemiesWith(u))
                    continue;

                float d = ((Vector3)c.transform.position - pos).sqrMagnitude;
                if (d < best)
                {
                    best = d;
                    bestPos = c.transform.position;
                    found = true;
                }
            }

            if (!found)
                return false;

            Vector2 v = bestPos - pos;
            if (v.sqrMagnitude < 1e-6f)
                return false;

            direction = v.normalized;
            return true;
        }

        private void DamageAround(Vector3 pos)
        {
            if (proj == null)
                return;

            int n = Physics2D.OverlapCircle(pos, contactRadius, enemyFilter, buffer);
            for (int i = 0; i < n; i++)
            {
                if (buffer[i] == null)
                    continue;

                // Never chew on the ship that planted us, or its friends.
                // (Destructibles like fruit have no Unit and still count.)
                Unit u = buffer[i].GetComponentInParent<Unit>();
                if (u != null && proj.Owner != null &&
                    (ReferenceEquals(u, proj.Owner) || proj.Owner.IsFriendsWith(u)))
                {
                    continue;
                }

                HealthBase hb = buffer[i].GetComponentInParent<HealthBase>();
                if (hb == null)
                    continue;

                float last;
                if (lastHit.TryGetValue(hb, out last) &&
                    Time.time - last < contactRepeatDelay)
                {
                    continue;
                }

                hb.TakeDamage(ownsDamage ? contactHit : proj.Damage);
                lastHit[hb] = Time.time;
            }
        }

        private WeaponBase Resolve()
        {
            if (string.IsNullOrEmpty(weaponName))
                return null;

            WeaponBase weapon;
            if (_weapons.TryGetValue(weaponName, out weapon))
                return weapon;   // may be null = previously failed/refused

            WeaponData data = ForgeWeaponLookup.Find(weaponName);

            if (data == null)
            {
                ForgeWeaponLookup.LogMiss("turret \"turretWeapon\"", weaponName);
                _weapons[weaponName] = null;
                return null;
            }

            weapon = new WeaponFactory().Create(data, null);

            // Refuse a weapon that is itself a turret - that's a cycle and
            // would multiply projectiles without bound.
            var projWeapon = weapon as ProjectileWeapon;
            if (projWeapon != null && projWeapon.ProjectilePrefab != null &&
                projWeapon.ProjectilePrefab.GetComponent<ForgeTurret>() != null)
            {
                Log.LogWarning(
                    "Turret: '" + weaponName + "' is itself a turret weapon " +
                    "- refusing to fire it (it would spawn turrets endlessly).");
                _weapons[weaponName] = null;
                return null;
            }

            _weapons[weaponName] = weapon;
            return weapon;
        }
    }
}
