using System.Collections.Generic;
using UnityEngine;

namespace WeaponForge
{
    // One cloud of gas.
    //
    // Deliberately its OWN object holding its OWN config reference, rather
    // than anything shared or static - his instinct was right, and two gas
    // weapons in the same run (or the same weapon at two levels) would
    // otherwise read each other's numbers. The only shared state here is the
    // live list, and every lookup in it filters by config.
    public class ForgeGasCloud : MonoBehaviour
    {
        private ForgeGas.Config cfg;
        private Unit owner;
        private float bornAt;
        private float endsAt;
        private bool retiring;
        private float retireWindow;
        private float nextTick;
        private Vector2 velocity;
        private float seed;

        private readonly List<Transform> puffs = new List<Transform>();
        private readonly List<SpriteRenderer> puffArt = new List<SpriteRenderer>();

        private static readonly List<ForgeGasCloud> _live =
            new List<ForgeGasCloud>();

        private static readonly Collider2D[] _buffer = new Collider2D[64];
        private static ContactFilter2D _hitFilter;
        private static bool _filterReady;

        // One soft radial sprite shared by every cloud in the game - the
        // SpriteRenderer tints it per cloud, so colour costs nothing.
        private static Sprite _puffArt;

        // Clouds already on their way out do not count against the budget, so
        // a new one can take the retiring one's place immediately rather than
        // waiting for it to finish fading.
        public static int CountFor(ForgeGas.Config c)
        {
            int n = 0;

            for (int i = 0; i < _live.Count; i++)
                if (_live[i] != null && _live[i].cfg == c && !_live[i].retiring)
                    n++;

            return n;
        }

        // Retire the oldest cloud belonging to this weapon, so a spray weapon
        // rolls its budget forward instead of simply refusing to make more.
        //
        // It is asked to FADE rather than destroyed outright. Under sustained
        // fire the cap binds long before `duration` does - a weapon throwing 18
        // puffs a second with a budget of 22 recycles every ~1.2s - so an
        // instant Destroy would pop clouds out of existence in front of the
        // player several times a second, which reads as a bug rather than as a
        // budget.
        public static void TrimOldest(ForgeGas.Config c)
        {
            ForgeGasCloud oldest = null;

            for (int i = 0; i < _live.Count; i++)
            {
                ForgeGasCloud g = _live[i];

                if (g == null || g.cfg != c || g.retiring)
                    continue;

                if (oldest == null || g.bornAt < oldest.bornAt)
                    oldest = g;
            }

            if (oldest != null)
                oldest.Retire();
        }

        public static ForgeGasCloud Spawn(
            ForgeGas.Config c, Vector2 where, Vector2 inherited, Unit owner)
        {
            if (c == null)
                return null;

            if (c.max > 0 && CountFor(c) >= c.max)
                TrimOldest(c);

            var go = new GameObject("Forge Gas");
            go.transform.position = where;

            var cloud = go.AddComponent<ForgeGasCloud>();
            cloud.cfg = c;
            cloud.owner = owner;
            cloud.velocity = (c.cloud == ForgeGas.Cloud.Drift)
                ? inherited.normalized * c.drift
                : Vector2.zero;

            return cloud;
        }

        private void Awake()
        {
            _live.Add(this);
            bornAt = Time.time;

            // Varies the churn per cloud without Random, so two clouds born in
            // the same frame do not move as one.
            seed = (GetInstanceID() & 1023) * 0.0141f;
        }

        private void OnDestroy()
        {
            _live.Remove(this);
        }

        private void Start()
        {
            // Not in Awake: cfg is assigned by the caller straight after
            // AddComponent, which is AFTER Awake has already run.
            endsAt = bornAt + cfg.duration;

            EnsureFilter();
            BuildArt();

            if (!string.IsNullOrEmpty(cfg.sfxGuid))
                AudioManager.PlaySfx(cfg.sfxGuid, transform.position);
        }

        private static void EnsureFilter()
        {
            if (_filterReady)
                return;

            _filterReady = true;
            _hitFilter = new ContactFilter2D();
            _hitFilter.useTriggers = true;
            _hitFilter.SetLayerMask(
                LayerMask.GetMask("Entities", "Fruits", "Player"));
        }

        private float Age { get { return Time.time - bornAt; } }

        // Cut this cloud's life short and let it fade out, rather than
        // vanishing. Idempotent - being asked twice does not re-start the fade.
        public void Retire()
        {
            if (retiring)
                return;

            retiring = true;

            // Long enough to read as thinning out, short enough that the budget
            // genuinely frees up. Never longer than the cloud had left anyway.
            retireWindow = Mathf.Max(0.15f, Mathf.Min(cfg.fade, 0.35f));
            endsAt = Mathf.Min(endsAt, Time.time + retireWindow);
        }

        private float Radius
        {
            get
            {
                if (cfg.cloud != ForgeGas.Cloud.Grow)
                    return cfg.radius;

                float t = (cfg.growTime <= 0f)
                    ? 1f
                    : Mathf.Clamp01(Age / cfg.growTime);

                return Mathf.Lerp(cfg.radius, cfg.growTo, t);
            }
        }

        private float Alpha
        {
            get
            {
                float left = endsAt - Time.time;

                // A short fade IN as well, so a cloud blooms rather than pops.
                float a = Mathf.Clamp01(Age / 0.25f);

                if (cfg.fade > 0f && left < cfg.fade)
                    a = Mathf.Min(a, Mathf.Clamp01(left / cfg.fade));

                // A cloud cut short by the budget fades over its own shorter
                // window, so it thins out from wherever it had got to.
                if (retiring && retireWindow > 0f)
                    a = Mathf.Min(a, Mathf.Clamp01(left / retireWindow));

                return a;
            }
        }

        private void Update()
        {
            if (cfg == null || Time.time >= endsAt)
            {
                Destroy(gameObject);
                return;
            }

            if (cfg.cloud == ForgeGas.Cloud.Drift)
            {
                transform.position +=
                    (Vector3)(velocity * Time.deltaTime);

                // Settles rather than sailing away forever.
                velocity *= Mathf.Max(
                    0f, 1f - cfg.driftDamping * Time.deltaTime);
            }

            DrawPuffs();
        }

        private void FixedUpdate()
        {
            if (cfg == null || !cfg.DoesAnything)
                return;

            if (Time.time < nextTick)
                return;

            float dt = Mathf.Max(0.02f, cfg.tickRate);
            nextTick = Time.time + dt;

            Affect(dt);
            Douse(dt);
        }

        // ------------------------------------------------------------------
        // What being inside it does
        // ------------------------------------------------------------------

        private void Affect(float dt)
        {
            float r = Radius;
            Vector2 here = transform.position;

            int n = Physics2D.OverlapCircle(here, r, _hitFilter, _buffer);

            for (int i = 0; i < n; i++)
            {
                Collider2D col = _buffer[i];

                if (col == null)
                    continue;

                var hb = col.GetComponentInParent<HealthBase>();

                if (hb == null)
                    continue;

                var unit = hb.GetComponent<Unit>();
                bool isOwner = unit != null && owner != null && unit == owner;

                // Whose gas this is decides who it is allowed to touch.
                if (isOwner && !cfg.hitPlayer)
                {
                    // Still allowed to put the owner's own fire out - that is
                    // the whole point of an extinguisher gas.
                    Extinguish(unit, dt);
                    continue;
                }

                if (!isOwner && !cfg.hitEnemies)
                    continue;

                // Damage is a plain {damageType, amount} struct. The type is
                // inherited from the weapon so the cloud is resisted and
                // coloured like the gun that made it - and skipped entirely
                // when there is none, because the resource comparisons
                // downstream have no null branch.
                if (cfg.damagePerSecond > 0f && _damageType != null)
                {
                    Damage dmg = new Damage(
                        cfg.damagePerSecond * dt, _damageType);

                    if (dmg.amount > 0f)
                    {
                        hb.TakeDamage(dmg);

                        if (_weapon != null && ForgeKillsPatch.JustDied(hb))
                        {
                            ForgeKillsPatch.Credit(
                                owner, hb.GetComponent<Unit>(), _weapon);
                        }
                    }
                }

                if (unit != null)
                {
                    if (cfg.burnPerSecond > 0f)
                    {
                        unit.ComponentData.BurnLevel +=
                            cfg.burnPerSecond * dt;
                    }

                    Extinguish(unit, dt);
                }

                Rigidbody2D rb = col.attachedRigidbody;

                if (rb == null)
                    continue;

                if (cfg.slow > 0f)
                {
                    float keep = Mathf.Clamp01(1f - cfg.slow * dt);
                    rb.linearVelocity *= keep;
                }

                if (cfg.push != 0f)
                {
                    Vector2 away = (Vector2)col.transform.position - here;

                    if (away.sqrMagnitude > 0.0001f)
                        rb.AddForce(away.normalized * cfg.push * dt,
                                    ForceMode2D.Impulse);
                }
            }
        }

        private void Extinguish(Unit unit, float dt)
        {
            if (cfg.extinguish <= 0f || unit == null)
                return;

            // The setter clamps at 0 itself, and only ADDS the catching-fire
            // bonus when crossing upward - so pulling it down is safe.
            unit.ComponentData.BurnLevel -= cfg.extinguish * dt;
        }

        [System.NonSerialized] private Resource _damageType;
        [System.NonSerialized] private WeaponBase _weapon;

        public void SetDamageType(Resource r)
        {
            _damageType = r;
        }

        // Carried so a kill by this cloud can be credited to the weapon that
        // made it - the game credits nothing that is not a projectile, beam or
        // explosion, and a cloud is none of those.
        public void SetWeapon(WeaponBase w)
        {
            _weapon = w;
        }

        // ------------------------------------------------------------------
        // Putting terrain fires out
        // ------------------------------------------------------------------
        //
        // This is the OTHER burn system - per-cell burn levels, simulated and
        // spread by the game's own job. Nothing else in the mod touches it.
        private void Douse(float dt)
        {
            if (cfg.extinguishTerrain <= 0f && cfg.igniteTerrain <= 0f)
                return;

            Level level;

            if (!ServiceLocator.TryGet<Level>(out level) || level == null)
                return;

            float r = Radius;
            Vector2 here = transform.position;

            int minX = Mathf.FloorToInt(here.x - r);
            int maxX = Mathf.CeilToInt(here.x + r);
            int minY = Mathf.FloorToInt(here.y - r);
            int maxY = Mathf.CeilToInt(here.y + r);

            float drop = cfg.extinguishTerrain * dt;
            float raise = cfg.igniteTerrain * dt;
            float sqr = r * r;

            for (int x = minX; x <= maxX; x++)
            {
                for (int y = minY; y <= maxY; y++)
                {
                    if (!level.ContainsCell(x, y))
                        continue;

                    float dx = x - here.x;
                    float dy = y - here.y;

                    if (dx * dx + dy * dy > sqr)
                        continue;

                    float burn = level.GetBurnLevel(x, y);

                    if (raise > 0f && !level.IsEmpty(x, y))
                    {
                        // Setting the ground alight from the air. The game only
                        // ever does this on a projectile COLLISION, so a cloud
                        // has no other route to it.
                        level.SetBurnLevel(x, y, burn + raise);
                        continue;
                    }

                    if (drop <= 0f || burn <= 0f)
                        continue;

                    // Clamped at zero deliberately - and note Level.SetBurnLevel
                    // REMOVES the cell's entry entirely at 0 rather than storing
                    // a zero, which is what fully puts it out.
                    level.SetBurnLevel(x, y, Mathf.Max(0f, burn - drop));
                }
            }
        }

        // ------------------------------------------------------------------
        // Art
        // ------------------------------------------------------------------

        private void BuildArt()
        {
            int count = Mathf.Clamp(cfg.puffs, 1, 24);

            Sprite art = cfg.art != null ? cfg.art : SharedPuff();

            for (int i = 0; i < count; i++)
            {
                var go = new GameObject("Puff");
                go.transform.SetParent(transform, false);

                var sr = go.AddComponent<SpriteRenderer>();
                sr.sprite = art;
                sr.color = cfg.color;

                if (cfg.material != null)
                    sr.material = cfg.material;

                if (cfg.sortingLayerId != 0)
                    sr.sortingLayerID = cfg.sortingLayerId;

                sr.sortingOrder = cfg.sortingOrder;

                puffs.Add(go.transform);
                puffArt.Add(sr);
            }
        }

        private void DrawPuffs()
        {
            float r = Radius;
            float a = Alpha;
            int n = puffs.Count;

            for (int i = 0; i < n; i++)
            {
                Transform t = puffs[i];

                if (t == null)
                    continue;

                // Puffs sit on a ring inside the cloud and rotate slowly, which
                // is what stops a cloud reading as one flat disc.
                float phase = (i / (float)n) * Mathf.PI * 2f + seed;
                float spin = (Age * cfg.swirl) * Mathf.Deg2Rad;
                float ang = phase + spin;

                // Alternate rings so the middle is not hollow.
                float ringR = (i % 2 == 0) ? r * 0.45f : r * 0.75f;

                t.localPosition = new Vector3(
                    Mathf.Cos(ang) * ringR, Mathf.Sin(ang) * ringR, 0f);

                // Each puff covers rather more than its share of the radius so
                // the ring reads as one mass rather than beads on a string.
                float size = r * 1.05f;
                t.localScale = new Vector3(size, size, 1f);

                SpriteRenderer sr = puffArt[i];

                if (sr == null)
                    continue;

                Color c = cfg.color;
                c.a *= a;
                sr.color = c;
            }
        }

        // A soft radial blob, built once. Generated rather than borrowed
        // because every stock particle sprite is packed tight in the shared
        // 1024x2048 atlas and its neighbours bleed in when it is scaled up.
        private static Sprite SharedPuff()
        {
            if (_puffArt != null)
                return _puffArt;

            const int S = 64;
            var tex = new Texture2D(S, S, TextureFormat.RGBA32, false);
            tex.filterMode = FilterMode.Bilinear;
            tex.wrapMode = TextureWrapMode.Clamp;

            var px = new Color[S * S];

            for (int y = 0; y < S; y++)
            {
                for (int x = 0; x < S; x++)
                {
                    float dx = (x + 0.5f) / S * 2f - 1f;
                    float dy = (y + 0.5f) / S * 2f - 1f;
                    float d = Mathf.Sqrt(dx * dx + dy * dy);

                    // Smooth falloff to nothing at the rim, squared so the
                    // centre stays dense and the edge is genuinely wispy.
                    float a = Mathf.Clamp01(1f - d);
                    a = a * a;

                    px[y * S + x] = new Color(1f, 1f, 1f, a);
                }
            }

            tex.SetPixels(px);
            tex.Apply();

            // 1 world unit across at scale 1, so localScale IS the diameter.
            _puffArt = Sprite.Create(
                tex, new Rect(0f, 0f, S, S), new Vector2(0.5f, 0.5f),
                S, 0, SpriteMeshType.FullRect);

            _puffArt.name = "Forge Gas Puff";
            return _puffArt;
        }
    }
}
