using System.Collections.Generic;
using UnityEngine;

namespace WeaponForge
{
    // Drives one equipped `shield` weapon: holds the arc up, catches what
    // flies into it, and turns a dash or a sprint into a ram.
    //
    // Deliberately NOT parented to the ship. Same reason the charge meter is
    // not: a child inherits the ship's scale, and the arc would grow and shrink
    // with it. Position is written every frame instead.
    public class ForgeShieldController : MonoBehaviour
    {
        public ForgeShield.Config cfg;
        public Unit unit;
        public WeaponBase weapon;
        public Shooter shooter;
        public ShipMovement ship;

        // ---- arc state -------------------------------------------------
        private bool up;                 // arc currently raised
        private bool toggleOn;           // Toggle activation latch
        private bool wasShooting;
        private float raisedAt = -999f;
        private Vector2 frozenFacing = Vector2.right;
        private Vector2 facing = Vector2.right;
        private float flashUntil = -999f;
        private int blockedThisSecond;
        private float secondStartedAt;

        // ---- ram state -------------------------------------------------
        private bool dashing;
        private float dashStartedAt = -999f;
        private bool ramming;
        private float ramIFrameUntil = -999f;

        // ---- rendering -------------------------------------------------
        private GameObject art;
        private SpriteRenderer sr;
        private Sprite builtArc;

        // ---- damage bookkeeping ----------------------------------------
        private readonly Dictionary<HealthBase, float> lastRam =
            new Dictionary<HealthBase, float>();

        private readonly List<Component> caught = new List<Component>();
        private readonly Collider2D[] buffer = new Collider2D[64];
        private ContactFilter2D enemyFilter;

        // The invulnerability token. DamagableResource.damageBlockers blocks
        // damage while every entry in the list is active, so a single object
        // toggled on and off is the whole mechanism.
        private GameObject iFrameToken;
        private DamagableResource health;
        private Aimer aimer;

        // Live registry, so the hitscan patch can ask "is any shield in the
        // way of this beam" without hunting the scene every shot.
        private static readonly List<ForgeShieldController> _live =
            new List<ForgeShieldController>();

        public static IList<ForgeShieldController> Live { get { return _live; } }

        public bool ArcUp { get { return up; } }
        public Vector2 Facing { get { return facing; } }

        private void OnEnable()
        {
            if (!_live.Contains(this))
                _live.Add(this);
        }

        private void OnDisable()
        {
            _live.Remove(this);
        }

        private void Start()
        {
            enemyFilter = new ContactFilter2D();
            enemyFilter.useTriggers = true;
            enemyFilter.SetLayerMask(LayerMask.GetMask("Entities", "Fruits"));

            if (unit != null)
                health = unit.GetComponent<DamagableResource>();

            Subscribe();
            BuildArt();
        }

        // ShipMovement exposes DashStarted / DashEnded / BoostStarted as public
        // Action FIELDS, so this needs no Harmony patch at all. There is no
        // BoostEnded - IsBoosted is polled for that.
        private void Subscribe()
        {
            if (ship == null || cfg == null || !cfg.WantsRam)
                return;

            ship.DashStarted += OnDashStarted;
            ship.DashEnded += OnDashEnded;
        }

        private void Unsubscribe()
        {
            if (ship == null)
                return;

            ship.DashStarted -= OnDashStarted;
            ship.DashEnded -= OnDashEnded;
        }

        private void OnDashStarted()
        {
            if (cfg == null || !cfg.ReactsToDash)
                return;

            dashing = true;
            dashStartedAt = Time.time;

            // Freeze the facing at the moment of the dash. Dash() fires along
            // flyDirection - the movement stick - and it does not change for
            // the length of the dash, so a shield that kept tracking the
            // crosshair would swing off the thing you are charging into.
            frozenFacing = SafeDir(ship.FlyDirection, facing);

            if (cfg.ramIFrames > 0f)
                ramIFrameUntil = Time.time + cfg.ramIFrames;

            if (!string.IsNullOrEmpty(cfg.ramSfx))
                AudioManager.PlaySfx(cfg.ramSfx, transform.position);
        }

        private void OnDashEnded()
        {
            dashing = false;
        }

        private void OnDestroy()
        {
            Unsubscribe();
            ReleaseIFrames();
            RestoreCrashDamage();

            if (art != null)
                Destroy(art);

            if (builtArc != null)
            {
                if (builtArc.texture != null)
                    Destroy(builtArc.texture);

                Destroy(builtArc);
            }

            _live.Remove(this);
        }

        private void Update()
        {
            if (cfg == null || unit == null)
                return;

            UpdateActivation();
            UpdateFacing();
            UpdateRamState();
            UpdateArt();
            UpdateIFrames();
        }

        private void FixedUpdate()
        {
            if (cfg == null || unit == null)
                return;

            if (up && cfg.WantsArc)
                Catch();

            if (ramming)
                Ram();
        }

        // ------------------------------------------------------------------
        // Activation
        // ------------------------------------------------------------------

        private void UpdateActivation()
        {
            if (!cfg.WantsArc)
            {
                up = false;
                return;
            }

            bool want;

            switch (cfg.activation)
            {
                case ForgeShield.Activation.Always:
                    want = true;
                    break;

                case ForgeShield.Activation.Toggle:
                    bool shooting = shooter != null && shooter.IsShooting;

                    if (shooting && !wasShooting)
                        toggleOn = !toggleOn;

                    wasShooting = shooting;
                    want = toggleOn;
                    break;

                default:
                    want = shooter != null && shooter.IsShooting;
                    break;
            }

            // Upkeep is charged BEFORE the arc counts as up, so a shield that
            // cannot pay never gets a free frame of protection.
            if (want && !PayUpkeep(Time.deltaTime))
            {
                want = false;
                toggleOn = false;
                Insufficient();
            }

            if (want && !up)
            {
                raisedAt = Time.time;
                frozenFacing = CurrentFacingSource();

                if (!string.IsNullOrEmpty(cfg.raiseSfx))
                    AudioManager.PlaySfx(cfg.raiseSfx, transform.position);
            }

            up = want;
        }

        private bool PayUpkeep(float dt)
        {
            float perSecond = cfg.drainPerSecond;

            if (cfg.drainFromCost && weapon != null)
                perSecond = weapon.Cost * weapon.FireRate;

            if (perSecond <= 0f)
                return true;

            return Spend(perSecond * dt);
        }

        // ------------------------------------------------------------------
        // Facing
        // ------------------------------------------------------------------

        private void UpdateFacing()
        {
            ForgeShield.Facing f = cfg.facing;

            if (f == ForgeShield.Facing.Auto)
            {
                // The point of Auto: an arc you angle at incoming fire wants
                // the crosshair, and a ram wants the direction you are
                // actually going, because that is what Dash() uses.
                f = ramming
                    ? ForgeShield.Facing.Travel
                    : ForgeShield.Facing.Aim;
            }

            if (ramming && dashing)
            {
                facing = frozenFacing;
                return;
            }

            switch (f)
            {
                case ForgeShield.Facing.Frozen:
                    facing = frozenFacing;
                    break;

                case ForgeShield.Facing.Travel:
                    facing = TravelDir();
                    break;

                default:
                    facing = AimDir();
                    break;
            }
        }

        private Vector2 CurrentFacingSource()
        {
            return cfg.facing == ForgeShield.Facing.Travel
                ? TravelDir()
                : AimDir();
        }

        // The crosshair. Aimer.TargetPosition is the RAW mouse - the assist
        // the game applies lives on AssistedBarrel, one stage further down, and
        // moves the firing direction rather than this. For a shield the raw
        // point is the honest one: it is where the player is looking.
        private Vector2 AimDir()
        {
            if (unit == null)
                return facing;

            // Cached: this runs every frame, and GetComponentInChildren walks
            // the whole ship every time it is called.
            if (aimer == null)
                aimer = unit.GetComponentInChildren<Aimer>();

            if (aimer == null)
                return TravelDir();

            Vector2 to = aimer.TargetPosition - (Vector2)unit.transform.position;

            return SafeDir(to, facing);
        }

        private Vector2 TravelDir()
        {
            if (ship == null)
                return facing;

            if (ship.Rigidbody != null &&
                ship.Rigidbody.linearVelocity.sqrMagnitude > 0.25f)
            {
                return ship.Rigidbody.linearVelocity.normalized;
            }

            return SafeDir(ship.FlyDirection, facing);
        }

        private static Vector2 SafeDir(Vector2 v, Vector2 fallback)
        {
            return v.sqrMagnitude > 0.000001f ? v.normalized : fallback;
        }

        // ------------------------------------------------------------------
        // Catching incoming fire
        // ------------------------------------------------------------------

        private void Catch()
        {
            if (Time.time - secondStartedAt >= 1f)
            {
                secondStartedAt = Time.time;
                blockedThisSecond = 0;
            }

            int room = 0;

            if (cfg.maxPerSecond > 0)
            {
                room = cfg.maxPerSecond - blockedThisSecond;

                if (room <= 0)
                    return;
            }

            caught.Clear();

            int n = ForgeProjectileTracker.CollectInSector(
                unit.transform.position, cfg.Reach, facing, cfg.HalfAngle,
                room, caught);

            // ALSO your own rallied shots, which is the whole point of a
            // rally: reflecting turns a bullet into YOURS, so without this the
            // shield stops being able to see it and the same shot can never be
            // caught twice.
            //
            // Only marked ones. Everything else you have fired is handed
            // straight back, so a shield never eats your own ordinary fire.
            if (cfg.rally != null && cfg.rally.enabled)
            {
                int before = caught.Count;

                ForgeProjectileTracker.CollectMineInSector(
                    unit.transform.position, cfg.Reach, facing, cfg.HalfAngle,
                    0, caught);

                for (int i = caught.Count - 1; i >= before; i--)
                {
                    Component mine = caught[i];

                    if (mine != null &&
                        mine.GetComponent<ForgeRally.ForgeRallied>() != null)
                    {
                        continue;
                    }

                    // Not a rallied shot - give it back untouched.
                    if (mine != null)
                        ForgeProjectileTracker.Register(mine);

                    caught.RemoveAt(i);
                }

                n = caught.Count;
            }

            if (n == 0)
                return;

            for (int i = 0; i < caught.Count; i++)
            {
                Component bullet = caught[i];

                if (bullet == null)
                    continue;

                if (!PayForBlock(bullet))
                {
                    // Could not afford this one. Let it through rather than
                    // eating it for free - and hand it BACK to the tracker,
                    // because CollectInSector already removed it and an
                    // untracked bullet is invisible to every other feature
                    // that catches fire.
                    ForgeProjectileTracker.Register(bullet);
                    Insufficient();
                    continue;
                }

                blockedThisSecond++;
                flashUntil = Time.time + 0.08f;

                if (!string.IsNullOrEmpty(cfg.blockSfx))
                    AudioManager.PlaySfx(
                        cfg.blockSfx, bullet.transform.position);

                if (cfg.onBlock == ForgeShield.OnBlock.Destroy ||
                    cfg.onBlock == ForgeShield.OnBlock.Absorb)
                {
                    Destroy(bullet.gameObject);
                    continue;
                }

                ForgeBulletTurn.Turn(
                    bullet,
                    unit,
                    cfg.onBlock == ForgeShield.OnBlock.ReflectAtNearest
                        ? ForgeBulletTurn.AimNearest
                        : (cfg.onBlock == ForgeShield.OnBlock.ReflectAtCursor
                            ? ForgeBulletTurn.AimCursor
                            : (cfg.onBlock == ForgeShield.OnBlock.ReflectMirror
                                ? ForgeBulletTurn.AimMirror
                                : ForgeBulletTurn.AimBack)),
                    cfg.aimRange,
                    cfg.reflectSpeed,
                    cfg.reflectDamage,
                    cfg.rally,
                    // The arc is centred on the ship, so the outward direction
                    // from the ship to the bullet IS the surface normal where
                    // it struck - which makes the arc a convex mirror and makes
                    // WHERE on it you catch something matter.
                    unit.transform.position);
            }

            caught.Clear();
        }

        // Two pricing models, both optional.
        //
        // `effectiveness` is the game's own Shield formula: absorbing d damage
        // costs d / effectiveness resource. `costPerBlock` is a flat charge.
        // Absorb mode REFUNDS instead, which is what makes it a battery.
        private bool PayForBlock(Component bullet)
        {
            if (cfg.onBlock == ForgeShield.OnBlock.Absorb)
            {
                float gain = cfg.costPerBlock;

                if (cfg.effectiveness > 0f)
                    gain = IncomingDamage(bullet) / cfg.effectiveness;

                if (gain > 0f)
                    Refund(gain);

                return true;
            }

            float price = cfg.costPerBlock;

            if (cfg.effectiveness > 0f)
                price = IncomingDamage(bullet) / cfg.effectiveness;

            if (price <= 0f)
                return true;

            return Spend(price);
        }

        private static float IncomingDamage(Component bullet)
        {
            var ip = bullet as IProjectile;

            if (ip == null)
                return 0f;

            return Mathf.Max(0f, ip.Damage.amount);
        }

        // ------------------------------------------------------------------
        // Ramming
        // ------------------------------------------------------------------

        private void UpdateRamState()
        {
            if (!cfg.WantsRam)
            {
                if (ramming)
                {
                    ramming = false;
                    RestoreCrashDamage();
                }
                return;
            }

            bool byDash = false;

            if (cfg.ReactsToDash && dashing)
            {
                byDash = cfg.ramDashWindow <= 0f ||
                         Time.time - dashStartedAt <= cfg.ramDashWindow;
            }

            bool bySprint = cfg.ReactsToSprint && ship != null && ship.IsBoosted;

            bool want = byDash || bySprint;

            if (want != ramming)
            {
                ramming = want;

                if (!ramming)
                    RestoreCrashDamage();
            }

            // ShipMovement.SetBoosted rewrites impactDamage.enabled every time
            // it runs, so this has to be reasserted rather than set once.
            if (ramming && cfg.ramSelfProtect)
                SuppressCrashDamage();
        }

        private void Ram()
        {
            if (ship == null || weapon == null)
                return;

            float speed = ship.Rigidbody != null
                ? ship.Rigidbody.linearVelocity.magnitude
                : 0f;

            if (speed < cfg.ramSpeedThreshold)
                return;

            Vector2 here = unit.transform.position;

            int n = Physics2D.OverlapCircle(
                here, cfg.Reach, enemyFilter, buffer);

            if (n == 0)
                return;

            float cosLimit = Mathf.Cos(cfg.HalfAngle * Mathf.Deg2Rad);

            // A sprint ram is continuous, so it is scaled down against the
            // one-off commitment of a dash.
            float mul = (dashing || !ship.IsBoosted)
                ? 1f
                : Mathf.Max(0f, cfg.ramSprintMultiplier);

            for (int i = 0; i < n; i++)
            {
                Collider2D col = buffer[i];

                if (col == null)
                    continue;

                Vector2 to = (Vector2)col.transform.position - here;

                if (to.sqrMagnitude > 0.000001f &&
                    Vector2.Dot(facing, to.normalized) < cosLimit)
                    continue;

                var hb = col.GetComponentInParent<HealthBase>();

                if (hb == null)
                    continue;

                // Never ram yourself.
                if (health != null && hb == (HealthBase)health)
                    continue;

                float last;
                if (lastRam.TryGetValue(hb, out last) &&
                    Time.time - last < cfg.ramRepeatDelay)
                    continue;

                lastRam[hb] = Time.time;

                Damage dmg = weapon.Damage;
                dmg.amount = cfg.ramDamage * mul;

                if (dmg.amount > 0f)
                {
                    hb.TakeDamage(dmg);

                    // The game credits a kill from exactly three places, all of
                    // them projectile/beam/explosion paths - a ram goes through
                    // none of them, so a ram kill was invisible to onKill and to
                    // everything else listening. Which would have made "dash
                    // through enemies to win fuel back" silently never pay.
                    if (ForgeKillsPatch.JustDied(hb))
                        ForgeKillsPatch.Credit(unit, hb.GetComponent<Unit>(),
                                               weapon);
                }

                if (cfg.ramKnockback > 0f)
                {
                    Rigidbody2D rb = col.attachedRigidbody;

                    if (rb != null)
                    {
                        Vector2 push = to.sqrMagnitude > 0.000001f
                            ? to.normalized
                            : facing;

                        rb.AddForce(
                            push * cfg.ramKnockback, ForceMode2D.Impulse);
                    }
                }
            }

            Prune();
        }

        private void Prune()
        {
            if (lastRam.Count < 32)
                return;

            var stale = new List<HealthBase>();

            foreach (var kv in lastRam)
                if (kv.Key == null || Time.time - kv.Value > 5f)
                    stale.Add(kv.Key);

            for (int i = 0; i < stale.Count; i++)
                lastRam.Remove(stale[i]);
        }

        // ------------------------------------------------------------------
        // Self-protection
        // ------------------------------------------------------------------

        // The ship's ImpactDamage is SELF damage - it hurts you for crashing,
        // and ShipMovement switches it on for exactly as long as you are
        // boosting. Ramming something at speed while sprinting would therefore
        // cost you health every time it worked.
        private void SuppressCrashDamage()
        {
            if (ship == null || ship.impactDamage == null)
                return;

            if (ship.impactDamage.enabled)
                ship.impactDamage.enabled = false;
        }

        private void RestoreCrashDamage()
        {
            if (ship == null || ship.impactDamage == null)
                return;

            // Hand it back to whatever ShipMovement thinks it should be, so we
            // never leave the player permanently immune to crash damage.
            if (ship.IsBoosted && !ship.impactDamage.enabled)
                ship.impactDamage.enabled = true;
        }

        private void UpdateIFrames()
        {
            bool want = Time.time < ramIFrameUntil;

            if (want)
                TakeIFrames();
            else
                ReleaseIFrames();
        }

        private void TakeIFrames()
        {
            if (health == null || health.damageBlockers == null)
                return;

            if (iFrameToken == null)
            {
                iFrameToken = new GameObject("Forge Ram IFrames");
                iFrameToken.transform.SetParent(unit.transform, false);
            }

            if (!iFrameToken.activeSelf)
                iFrameToken.SetActive(true);

            if (!health.damageBlockers.Contains(iFrameToken))
                health.damageBlockers.Add(iFrameToken);
        }

        private void ReleaseIFrames()
        {
            if (iFrameToken == null)
                return;

            // IsDamageBlocked() returns false the moment ANY entry is inactive
            // or null, so deactivating is enough - but the entry is removed too
            // rather than left as litter on the ship's list.
            if (health != null && health.damageBlockers != null)
                health.damageBlockers.Remove(iFrameToken);

            if (iFrameToken.activeSelf)
                iFrameToken.SetActive(false);
        }

        // ------------------------------------------------------------------
        // Resource plumbing
        // ------------------------------------------------------------------

        private Resource PayResource()
        {
            if (cfg.resourceAsset != null)
                return cfg.resourceAsset;

            return weapon != null ? weapon.ResourceUsed : null;
        }

        // ResourceTank.Value has no floor of its own, so this clamps at zero.
        // Without that the tank goes negative and the shield runs for free -
        // exactly the bug the orbit hold-drain had.
        private bool Spend(float amount)
        {
            try
            {
                if (amount <= 0f)
                    return true;

                Resource res = PayResource();

                if (unit == null || res == null)
                    return true;

                if (unit.HasInfiniteResource)
                    return true;

                if (!unit.HasTank(res))
                    return true;

                ResourceTank tank = unit.GetTank(res);

                if (tank.Value < amount)
                {
                    tank.Value = 0f;
                    return false;
                }

                tank.Value -= amount;
                return true;
            }
            catch
            {
                return true;
            }
        }

        private void Refund(float amount)
        {
            try
            {
                Resource res = PayResource();

                if (unit == null || res == null || !unit.HasTank(res))
                    return;

                ResourceTank tank = unit.GetTank(res);

                tank.Value = Mathf.Min(tank.Capacity, tank.Value + amount);
            }
            catch { }
        }

        private void Insufficient()
        {
            try
            {
                Resource res = PayResource();

                if (unit != null && res != null)
                    unit.TriggerResourceInsufficient(res, false);
            }
            catch { }
        }

        // ------------------------------------------------------------------
        // Art
        // ------------------------------------------------------------------

        private void BuildArt()
        {
            art = new GameObject("Forge Shield Arc");
            sr = art.AddComponent<SpriteRenderer>();

            if (cfg.art != null)
            {
                sr.sprite = cfg.art;
            }
            else
            {
                builtArc = ForgeShieldArt.Build(
                    cfg.angle,
                    Mathf.Max(0.05f, cfg.thickness),
                    Mathf.Max(0.2f, cfg.radius),
                    cfg.WantsRam && !cfg.WantsArc);

                sr.sprite = builtArc;
            }

            sr.color = cfg.color;

            if (cfg.material != null)
                sr.material = cfg.material;

            if (cfg.sortingLayerId != 0)
                sr.sortingLayerID = cfg.sortingLayerId;

            sr.sortingOrder = cfg.sortingOrder;
            art.SetActive(false);
        }

        private void UpdateArt()
        {
            if (art == null)
                return;

            bool show = up || ramming;

            if (!show)
            {
                if (art.activeSelf)
                    art.SetActive(false);

                return;
            }

            if (!art.activeSelf)
                art.SetActive(true);

            art.transform.position = unit.transform.position;
            art.transform.rotation =
                Quaternion.FromToRotation(Vector3.right, (Vector3)facing);

            float a = cfg.color.a;

            if (cfg.appearFade > 0f && up)
            {
                float t = Mathf.Clamp01(
                    (Time.time - raisedAt) / cfg.appearFade);
                a *= t;
            }

            if (cfg.flashOnBlock && Time.time < flashUntil)
                a = Mathf.Min(1f, a * 2.2f);

            Color c = cfg.color;
            c.a = a;
            sr.color = c;
        }
    }
}
