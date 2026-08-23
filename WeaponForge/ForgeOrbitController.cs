using System.Collections.Generic;
using UnityEngine;

namespace WeaponForge
{
    // Runs the orbit: keeps N orb visuals evenly spaced on a circle around
    // the ship, spins them, and (optionally) damages enemies on contact,
    // blocks enemy shots, and pushes enemies. Orbs can be destructible -
    // dying when they hit an enemy and/or terrain - and regenerate by a
    // timer and/or on fire. Orb count and per-hit damage are read LIVE from
    // the equipped weapon, so +projectile/+damage modules scale the ring.
    public class ForgeOrbitController : MonoBehaviour
    {
        private class Orb
        {
            public GameObject go;
            // The (disabled) Projectile on the orb art. We never let it fly,
            // but we DO reuse it as the IProjectile for a hit, so contact
            // goes through the game's real projectile-hit path.
            public Projectile proj;
            public bool alive = true;
            public float regenAt;

            // Terrain digging: last position (for a travel direction, used as
            // the "velocity" of the fake hit) and a per-orb cell cooldown.
            public Vector3 lastPos;
            public bool hasLastPos;
            public float nextTerrainHit;

            // Growth state. bornAt is when this orb last came into existence
            // (spawn OR regeneration), which is what GrowMode.OrbLife counts
            // from. baseScale is the art's own scale, captured once, so growth
            // is a multiplier of it rather than an absolute size.
            public float bornAt;
            public Vector3 baseScale = Vector3.one;
            public bool hasBaseScale;

            // Sprite renderers on the art, cached once so the release fade
            // does not GetComponentsInChildren every frame for every orb.
            public SpriteRenderer[] renderers;
            public bool hasRenderers;

            // Spiral state (unused when cfg.spiral == Off).
            public float cycleStart;   // when this spiral-out cycle began
            public bool launched;      // Launch mode: detached, flying free
            public Vector3 launchPos;  // integrated position while launched
            public Vector3 launchVel;  // world velocity while launched
            public Vector3 prevPos;    // last spiral position (for launch dir)
            public bool hasPrev;
        }

        private ForgeOrbit.Config cfg;

        // The controller doesn't care whether it's driven by a primary/
        // secondary Shooter or a gadget - it just needs the owner ship and a
        // way to read the CURRENT weapon (for live orb count + damage).
        private Unit unit;
        private System.Func<WeaponBase> getWeapon;

        // Driven by ForgeOrbitPatch for Toggle / Fire modes.
        public bool toggledOn;
        public float fireActiveUntil;

        // Gadgets have no per-frame Shooter driver, so a gadget controller
        // decides its own active state from mode + toggledOn / fireActiveUntil.
        // (Primary/secondary keep being driven externally each frame.)
        public bool selfDriven;

        private bool active;
        private float angleDeg;
        private float[] ringAngle;   // one spin accumulator per ring
        private float activeSince;
        private float flingUntil;

        // While this is in the future the ring is switched off but still
        // fading out, so the orbs keep being drawn and moved.
        private float releaseUntil;

        // False while the ring is still fading in, and for exactly one frame
        // after. The trailing frame is what writes full alpha and full scale:
        // the last appearing frame lands on 0.98-ish rather than 1, and a ring
        // that never grows writes its scale nowhere else, so without a settle
        // pass the orbs would sit permanently a hair dim and a hair small.
        private bool appearSettled = true;

        // Set when a held/toggled ring could not pay its upkeep. It locks the
        // ring off briefly rather than for good, so a regenerating tank brings
        // it back on its own - and, more importantly, so the drain does not
        // retry sixty times a second while the trigger is still down, firing
        // the "out of resource" cue on every one of those frames.
        public float dryUntil;

        private WeaponBase weapon;   // cached each frame from getWeapon()

        private readonly List<Orb> orbs = new List<Orb>();
        private readonly Dictionary<HealthBase, float> lastHit =
            new Dictionary<HealthBase, float>();

        private ContactFilter2D enemyFilter;
        private ContactFilter2D projFilter;
        private ContactFilter2D groundFilter;
        private readonly List<Collider2D> buffer = new List<Collider2D>();
        private bool _filtersReady;

        public void Init(
            ForgeOrbit.Config config, Unit owner, System.Func<WeaponBase> weaponGetter)
        {
            cfg = config;
            unit = owner;
            getWeapon = weaponGetter;
        }

        public void SetActive(bool a)
        {
            if (a == active)
                return;
            active = a;

            // The one place a ring's state actually changes, so the on-screen
            // indicator is reported from here rather than polled. Keyed on this
            // controller, so two rings on one ship show as two entries.
            ForgeBuffStates.OrbitActive(this, active);
            if (active)
            {
                activeSince = Time.time;

                // Coming back on cancels any dissolve in progress and undoes
                // what it did - otherwise re-firing during the fade would
                // leave the orbs stuck part-faded and part-shrunk.
                releaseUntil = 0f;

                // A fresh wind-up. Without this the orbs would keep whatever
                // alpha and scale the last appear left them on, and the ring
                // would only ever fade in once.
                appearSettled = false;

                // Restart each orb's spiral from the CURRENT ship position.
                // While inactive, Time.time keeps advancing and the orbs kept
                // stale cycle timing + world positions from the last time the
                // ring ran; without this, a Launch-spiral orb would sling off
                // in the wrong direction (or pop in far away) on reactivation.
                for (int i = 0; i < orbs.Count; i++)
                {
                    Orb orb = orbs[i];

                    orb.cycleStart = Time.time;
                    orb.hasPrev = false;
                    orb.hasLastPos = false;
                    orb.launched = false;

                    // Rebuild the charge. OrbLife counts growth from each
                    // orb's own birth, and orbs are only hidden when the ring
                    // switches off rather than destroyed - so without this
                    // they came straight back at full size and a "charge"
                    // ring only ever wound up once. See Config.growReset.
                    if (cfg != null && cfg.growReset)
                        orb.bornAt = Time.time;

                    Restore(orb);
                }
            }
            else if (cfg != null && cfg.releaseFade > 0f)
            {
                releaseUntil = Time.time + cfg.releaseFade;
            }
        }

        // Put an orb back to full opacity and its own scale. Called when the
        // ring comes on, because a dissolve leaves both part-way.
        private static void Restore(Orb orb)
        {
            if (orb.go == null)
                return;

            if (orb.hasBaseScale)
                orb.go.transform.localScale = orb.baseScale;

            SetAlpha(orb, 1f);
        }

        private static void SetAlpha(Orb orb, float alpha)
        {
            if (!orb.hasRenderers || orb.renderers == null)
                return;

            for (int i = 0; i < orb.renderers.Length; i++)
            {
                SpriteRenderer r = orb.renderers[i];

                if (r == null)
                    continue;

                // Alpha only, never the rgb: an orb can be carrying a rainbow
                // animator that rewrites its colour every frame, and stomping
                // the hue here would fight it.
                Color c = r.color;
                c.a = alpha;
                r.color = c;
            }
        }

        public void TriggerFling()
        {
            if (cfg != null && cfg.fling)
                flingUntil = Time.time + cfg.flingDuration;
        }

        // Called when the weapon fires / re-toggles: regenerate dead orbs
        // if regen is fire-based.
        public void RegenOnFire()
        {
            if (cfg == null)
                return;
            if (cfg.regenMode != ForgeOrbit.RegenMode.Fire &&
                cfg.regenMode != ForgeOrbit.RegenMode.Both)
                return;

            // Only revive orbs that are actually DEAD. Reviving live orbs
            // would reset their spiral (cycleStart = now) on every shot, and
            // because a suppressed-fire orbit fires every frame while held,
            // that would freeze spiraling orbs at the inner radius forever.
            for (int i = 0; i < orbs.Count; i++)
                if (!orbs[i].alive)
                    Revive(orbs[i]);
        }

        private void EnsureFilters()
        {
            if (_filtersReady)
                return;
            _filtersReady = true;

            enemyFilter = new ContactFilter2D();
            enemyFilter.useTriggers = true;
            enemyFilter.SetLayerMask(LayerMask.GetMask("Entities", "Fruits"));

            projFilter = new ContactFilter2D();
            projFilter.useTriggers = true;
            projFilter.SetLayerMask(LayerMask.GetMask("EnemyProjectiles"));

            groundFilter = new ContactFilter2D();
            groundFilter.useTriggers = true;
            groundFilter.SetLayerMask(LayerMask.GetMask("Ground"));
        }

        private void Update()
        {
            if (cfg == null || unit == null || unit.transform == null)
                return;

            weapon = (getWeapon != null) ? getWeapon() : null;

            // Gadgets self-manage their active state (no Shooter driving us).
            if (selfDriven)
            {
                bool a;
                switch (cfg.mode)
                {
                    case ForgeOrbit.Mode.Toggle:
                        a = toggledOn;
                        break;
                    case ForgeOrbit.Mode.Fire:
                        a = Time.time < fireActiveUntil;
                        break;
                    default: // passive / hold: on once first activated
                        a = toggledOn;
                        break;
                }
                SetActive(a);
            }

            // A ring that has just been let go keeps running for a moment so
            // it can fade out, instead of the orbs blinking off mid-orbit.
            // It goes through the SAME loop below rather than a private copy,
            // so they keep orbiting, pulsing and spiralling exactly as they
            // were - only the parts that touch the world are switched off.
            bool dissolving = false;
            float fade = 1f;

            if (!active)
            {
                if (releaseUntil > Time.time && cfg.releaseFade > 0f)
                {
                    dissolving = true;
                    fade = Mathf.Clamp01(
                        (releaseUntil - Time.time) / cfg.releaseFade);
                }
                else
                {
                    for (int i = 0; i < orbs.Count; i++)
                        if (orbs[i].go != null)
                            orbs[i].go.SetActive(false);
                    return;
                }
            }

            // Coming UP. The mirror of the dissolve above, and it runs through
            // the same loop for the same reason: the orbs orbit, pulse and
            // spiral exactly as normal while they arrive, and only opacity,
            // size and whether they touch anything are different.
            bool appearing = false;
            float appear = 1f;

            if (active && cfg.appearFade > 0f)
            {
                float since = Time.time - activeSince;

                if (since < cfg.appearFade)
                {
                    appearing = true;
                    appear = Mathf.Clamp01(since / cfg.appearFade);
                }
            }

            // 1 while the ring is live; shrinks toward nothing as it goes, and
            // swells out of nothing as it arrives. Multiplied rather than
            // chosen between, so the two can never be in force at once anyway
            // (one needs active, the other needs !active) but the arithmetic
            // stays obvious.
            float releaseScale =
                (dissolving && cfg.releaseShrink)
                    ? Mathf.Lerp(0.15f, 1f, fade)
                    : 1f;

            if (appearing && cfg.appearGrow)
                releaseScale *= Mathf.Lerp(0.15f, 1f, appear);

            // How solid the orbs are drawn this frame.
            float alpha = dissolving ? fade : (appearing ? appear : 1f);

            // Orbs on their way out are being dismissed and orbs on their way
            // in have not arrived, so neither touches the world unless the
            // file explicitly asks. Both default off, and together they are
            // what stops tapping being better than holding.
            bool live =
                (!dissolving || cfg.releaseDamage) &&
                (!appearing || cfg.appearDamage);

            EnsureFilters();

            int count = 4;
            if (weapon != null)
                count = Mathf.Max(1, Mathf.RoundToInt(weapon.ProjectileCount));

            // Concentric rings. "full count" gives every ring the weapon's
            // whole projectileCount; otherwise that count is split across
            // them, so adding rings re-arranges the orbs you already have
            // instead of multiplying them.
            int rings = Mathf.Max(1, cfg.rings);
            int total = cfg.ringsFullCount ? count * rings : count;
            SetOrbCount(total);

            float spinMul = (cfg.spinUpSeconds > 0f)
                ? Mathf.Clamp01((Time.time - activeSince) / cfg.spinUpSeconds)
                : 1f;

            // Each ring spins on its own accumulator so it can run at its
            // own speed and (optionally) the opposite way.
            if (ringAngle == null || ringAngle.Length < rings)
                ringAngle = new float[rings];

            for (int r = 0; r < rings; r++)
            {
                bool flip = cfg.ringAlternate && (r % 2 == 1);
                float rdir = (cfg.clockwise ^ flip) ? -1f : 1f;
                float rspeed = cfg.speed *
                    ((cfg.ringSpeedStep == 1f) ? 1f : Mathf.Pow(cfg.ringSpeedStep, r));
                ringAngle[r] += rdir * rspeed * spinMul * Time.deltaTime;
            }
            angleDeg = ringAngle[0];

            // Fixed-ring radius (with pulse/fling). Spiral modes drive their
            // own per-orb radius instead, so pulse/fling don't apply there.
            float ringRadius = cfg.radius;
            if (cfg.spiral == ForgeOrbit.SpiralMode.Off)
            {
                if (cfg.pulseAmount > 0f)
                    ringRadius += Mathf.Sin(Time.time * cfg.pulseSpeed) * cfg.pulseAmount;
                if (cfg.fling && Time.time < flingUntil)
                {
                    float p = 1f - (flingUntil - Time.time) / cfg.flingDuration;
                    ringRadius += Mathf.Sin(p * Mathf.PI) * cfg.flingReach;
                }
            }

            Vector3 center = unit.transform.position;

            for (int i = 0; i < orbs.Count; i++)
            {
                Orb orb = orbs[i];

                // The orb list only ever grows; if the live count shrank
                // (e.g. a +projectile module lost power), hide and skip the
                // extras so they don't render or keep dealing contact damage.
                if (i >= total)
                {
                    if (orb.go != null)
                        orb.go.SetActive(false);
                    continue;
                }

                if (!orb.alive)
                {
                    // Timer regen. Not while dissolving: an orb popping back
                    // into a ring that is on its way out would undo the whole
                    // effect.
                    if (!dissolving &&
                        (cfg.regenMode == ForgeOrbit.RegenMode.Timer ||
                         cfg.regenMode == ForgeOrbit.RegenMode.Both) &&
                        Time.time >= orb.regenAt)
                    {
                        Revive(orb);
                    }
                    else
                    {
                        if (orb.go != null)
                            orb.go.SetActive(false);
                        continue;
                    }
                }

                // Which ring this orb belongs to, and its slot in that ring.
                // Split mode deals orbs out round-robin so the rings stay as
                // even as possible when the count doesn't divide cleanly.
                int ring, slot, slotsInRing;
                if (cfg.ringsFullCount)
                {
                    ring = i / count;
                    slot = i % count;
                    slotsInRing = count;
                }
                else
                {
                    ring = i % rings;
                    slot = i / rings;
                    slotsInRing = (count - ring + rings - 1) / rings;
                    if (slotsInRing < 1)
                        slotsInRing = 1;
                }
                if (ring >= rings)
                    ring = rings - 1;

                float orbRadius = ringRadius + ring * cfg.ringSpacing;
                float stepInRing = 360f / slotsInRing;

                // Auto stagger splits one slot evenly between the rings, so
                // the orbs interleave instead of lining up as spokes. (Using
                // a flat half-slot would make ring 2 of 3 wrap back onto the
                // aligned position.)
                float stagger = (cfg.ringStagger < 0f)
                    ? stepInRing * ((float)ring / rings)
                    : cfg.ringStagger * ring;

                float a = (ringAngle[ring] + slot * stepInRing + stagger) * Mathf.Deg2Rad;
                Vector3 pos = (cfg.spiral == ForgeOrbit.SpiralMode.Off)
                    ? center + new Vector3(Mathf.Cos(a) * orbRadius,
                                           Mathf.Sin(a) * orbRadius, 0f)
                    : SpiralPos(orb, a, center, orbRadius);

                // Size first: everything below measures against the grown
                // radius, so the damage area matches what is drawn.
                float growT = GrowProgress(orb);
                float sizeMul = GrowSize(growT);
                float hitR = cfg.hitRadius *
                    ((cfg.grow != ForgeOrbit.GrowMode.Off && cfg.growHitbox)
                        ? sizeMul : 1f);

                if (orb.go != null)
                {
                    orb.go.SetActive(true);
                    orb.go.transform.position = pos;

                    // Captured unconditionally now, not just when growth is
                    // on: the release shrink needs the orb's own scale to
                    // measure against even on a ring that never grows.
                    if (!orb.hasBaseScale)
                    {
                        orb.baseScale = orb.go.transform.localScale;
                        orb.hasBaseScale = true;
                    }

                    if (!orb.hasRenderers)
                    {
                        orb.renderers =
                            orb.go.GetComponentsInChildren<SpriteRenderer>(true);
                        orb.hasRenderers = true;
                    }

                    // The !appearSettled term is the settle pass: one frame
                    // after the fade-in ends, so a ring that never grows still
                    // gets written back to its true size exactly once.
                    if (cfg.grow != ForgeOrbit.GrowMode.Off || dissolving ||
                        appearing || !appearSettled)
                    {
                        orb.go.transform.localScale =
                            orb.baseScale * sizeMul * releaseScale;
                    }

                    if (dissolving || appearing || !appearSettled)
                        SetAlpha(orb, alpha);
                }

                bool hitEnemy = false;
                if (live && (cfg.contactDamage || cfg.destroyOnEnemy))
                    hitEnemy = DamageAt(orb, pos, hitR, growT);
                if (live && cfg.pushForce > 0f)
                    PushAt(pos, center, hitR);
                if (live && cfg.blockProjectiles)
                    BlockAt(pos, hitR);

                // One terrain query serves both digging and destroy-on-terrain.
                bool hitTerrain = false;
                if (live && (cfg.damageTerrain || cfg.destroyOnTerrain))
                    hitTerrain = TerrainAt(orb, pos, hitR);

                orb.lastPos = pos;
                orb.hasLastPos = true;

                bool destroy = false;
                if (cfg.destroyOnEnemy && hitEnemy)
                    destroy = true;
                if (!destroy && cfg.destroyOnTerrain && hitTerrain)
                    destroy = true;

                // Never kill an orb mid-dissolve - it would pop its explosion
                // and vanish out of a ring that is already leaving. Same while
                // it is still arriving: an orb destroyed before it has finished
                // fading in never gets seen at all.
                if (destroy && !dissolving && !appearing)
                    Kill(orb, pos);
            }

            // The settle pass has now run for every orb, so stop repeating it.
            if (!appearing && !dissolving)
                appearSettled = true;
        }

        // How far through its growth this orb is, 0..1. The clock depends on
        // the mode; everything else about growth is shared.
        private float GrowProgress(Orb orb)
        {
            if (cfg.grow == ForgeOrbit.GrowMode.Off)
                return 0f;

            if (cfg.grow == ForgeOrbit.GrowMode.Breathe)
            {
                // A full cycle per growBreatheSpeed seconds, starting at the
                // SMALL end so a ring visibly swells when it comes on rather
                // than appearing mid-breath.
                float phase = Time.time * cfg.growBreatheSpeed * Mathf.PI * 2f;
                return (1f - Mathf.Cos(phase)) * 0.5f;
            }

            float span = Mathf.Max(0.01f, cfg.growSpan);

            float since = (cfg.grow == ForgeOrbit.GrowMode.OrbLife)
                ? Time.time - orb.bornAt
                : Time.time - activeSince;

            return Mathf.Clamp01(since / span);
        }

        // Progress -> size multiplier, with the same easing exponent the
        // projectile "grow" feature uses so the two behave alike.
        private float GrowSize(float t)
        {
            if (cfg.grow == ForgeOrbit.GrowMode.Off)
                return 1f;

            float eased = (cfg.growCurve == 1f)
                ? t
                : Mathf.Pow(Mathf.Clamp01(t), Mathf.Max(0.01f, cfg.growCurve));

            return Mathf.LerpUnclamped(cfg.growFrom, cfg.growTo, eased);
        }

        // Damage multiplier for an orb at this much growth. 1 unless
        // growDamageAtFull was set, in which case it ramps 1 -> that value.
        private float GrowDamageMul(float t)
        {
            if (cfg.grow == ForgeOrbit.GrowMode.Off ||
                cfg.growDamageAtFull <= 0f)
            {
                return 1f;
            }

            float eased = (cfg.growCurve == 1f)
                ? t
                : Mathf.Pow(Mathf.Clamp01(t), Mathf.Max(0.01f, cfg.growCurve));

            return Mathf.LerpUnclamped(1f, cfg.growDamageAtFull, eased);
        }

        // Per-orb position for the spiral modes. Grows the radius from
        // spiralInner to the outer radius over spiralOutTime, then either
        // detaches and flies straight off (Launch) or snaps back to the
        // center and repeats (Sweep). Mutates the orb's spiral state.
        private Vector3 SpiralPos(Orb orb, float angleRad, Vector3 center, float outerRadius)
        {
            // A launched orb flies straight until it's far enough away,
            // then recycles back to a fresh spiral from the inner radius.
            if (orb.launched)
            {
                orb.launchPos += orb.launchVel * Time.deltaTime;
                if ((orb.launchPos - center).magnitude <= cfg.spiralKillDistance)
                    return orb.launchPos;

                orb.launched = false;
                orb.hasPrev = false;
                orb.cycleStart = Time.time;
                // fall through: place it back at the inner radius this frame
            }

            float dur = Mathf.Max(0.01f, cfg.spiralOutTime);
            float frac = (Time.time - orb.cycleStart) / dur;

            if (frac >= 1f)
            {
                Vector3 outerPos = Polar(center, angleRad, outerRadius);

                if (cfg.spiral == ForgeOrbit.SpiralMode.Launch)
                {
                    // Detach: keep drifting the way the spiral was carrying
                    // it (mostly tangential + outward) so it slings away.
                    Vector3 fly = orb.hasPrev
                        ? (outerPos - orb.prevPos)
                        : (outerPos - center);
                    if (fly.sqrMagnitude < 1e-5f)
                        fly = (outerPos - center);

                    orb.launched = true;
                    orb.launchVel = fly.normalized * cfg.spiralLaunchSpeed;
                    orb.launchPos = outerPos;
                    return outerPos;
                }

                // Sweep: restart the spiral from the inner radius.
                orb.cycleStart = Time.time;
                frac = 0f;
            }

            Vector3 pos = Polar(center, angleRad,
                Mathf.Lerp(cfg.spiralInner, outerRadius, frac));
            orb.prevPos = pos;
            orb.hasPrev = true;
            return pos;
        }

        private static Vector3 Polar(Vector3 center, float angleRad, float r)
        {
            return center + new Vector3(
                Mathf.Cos(angleRad) * r, Mathf.Sin(angleRad) * r, 0f);
        }

        private bool DamageAt(Orb orb, Vector3 pos, float hitR, float growT)
        {
            if (weapon == null)
                return false;

            bool hit = false;
            int n = Physics2D.OverlapCircle(pos, hitR, enemyFilter, buffer);
            for (int j = 0; j < n; j++)
            {
                var hb = buffer[j].GetComponentInParent<HealthBase>();
                if (hb == null)
                    continue;

                hit = true;

                if (!cfg.contactDamage)
                    continue;

                float last;
                if (lastHit.TryGetValue(hb, out last) &&
                    Time.time - last < cfg.damageRepeatDelay)
                {
                    continue;
                }

                Strike(orb, hb, pos, growT);
                lastHit[hb] = Time.time;
            }
            return hit;
        }

        // One orb "hit". Routed through the game's own projectile-hit path
        // when we can, so the victim gets everything a normal shot would
        // deliver - burn, the got-attacked/aggro event and kill credit -
        // not just raw damage. Then the weapon's explosion / discharge are
        // spawned, since an orbit weapon has no other way to deliver them.
        private void Strike(Orb orb, HealthBase hb, Vector3 pos, float growT)
        {
            var pw = weapon as ProjectileWeapon;

            // A grown orb can hit harder, if the weapon asked for it. Damage
            // is a STRUCT, so this copy leaves the weapon's own value alone -
            // mutating it in place would permanently buff the weapon.
            Damage dmg = weapon.Damage;
            float dmgMul = GrowDamageMul(growT);

            if (dmgMul != 1f)
                dmg.amount *= dmgMul;

            if (cfg.weaponEffects && orb != null && orb.proj != null)
            {
                // Stamp the live weapon's stats onto the stand-in projectile
                // (read fresh so +damage / +burn modules keep applying).
                Projectile p = orb.proj;
                p.Owner = (unit != null) ? unit : p.Owner;
                p.Damage = dmg;
                p.Burn = weapon.Burn;
                p.Explosion = weapon.Explosion;
                p.DischargeData = weapon.DischargeData;
                if (pw != null)
                {
                    // These two decide whether burn lands on contact or is
                    // left to the explosion - keep the weapon's own answer.
                    p.ImpactBehaviour = pw.ImpactBehaviour;
                    p.LifetimeData = pw.LifetimeData;
                }

                Vector2 normal = ((Vector2)(hb.transform.position - pos)).normalized;
                hb.ProjectileCollided(p, pos, normal);
            }
            else
            {
                hb.TakeDamage(dmg);
            }

            if (!cfg.weaponEffects)
                return;

            SpawnWeaponExplosion(orb, pos);
            SpawnWeaponDischarge(pos);
        }

        private void SpawnWeaponExplosion(Orb orb, Vector3 pos)
        {
            SpawnWeaponExplosionInner(pos);
        }

        private void SpawnWeaponExplosionInner(Vector3 pos)
        {
            Explosion explosion = weapon.Explosion;
            if (explosion.damages == null || explosion.damages.Count == 0 ||
                explosion.radius <= 0f)
            {
                return;   // nothing configured (SpawnExplosion errors on empty)
            }

            try
            {
                explosion.Owner = unit;
                explosion.Burn = weapon.Burn;
                ServiceLocator.Get<ExplosionManager>()
                    .SpawnExplosion(pos, explosion);
            }
            catch { }
        }

        private void SpawnWeaponDischarge(Vector3 pos)
        {
            DischargeData discharge = weapon.DischargeData;
            if (discharge.chainLength <= 0)
                return;   // no chain configured

            // HAVING discharge data is not the same as the weapon wanting it
            // fired, and this is the difference that mattered: White Worm ships
            // a discharge block with chainLength 1 and damage 0, but
            // impactBehaviour.discharge is 0 - so a normal Worm shot never
            // zaps. Checking only the data made orbs zap on every contact,
            // which looked exactly like the mod switching chain lightning on by
            // itself.
            //
            // So require what the game requires: a flag that would actually
            // trigger a discharge on a real shot. To opt IN to zapping orbs,
            // set "impactBehaviour": { "enabled": true, "discharge": true }.
            //
            // Note "dischargeOnFire" is deliberately NOT one of these. That is
            // the mod's own separate feature - it zaps from the GUN at the
            // moment of firing, through ForgeDischargePatch - and it is a
            // different event from an orb touching something.
            if (!WantsDischarge())
                return;

            try
            {
                ElectricityManager em;
                if (ServiceLocator.TryGet<ElectricityManager>(out em))
                    em.SpawnDischarge(discharge, pos);
            }
            catch { }
        }

        // Mirrors the game's own discharge triggers: on impact, at end of life,
        // or at end of range. (ProjectileRangeData has no discharge flag - only
        // spawnExplosion and fireSub - so there are just the two.)
        private bool WantsDischarge()
        {
            var pw = weapon as ProjectileWeapon;

            if (pw == null)
                return false;

            return (pw.ImpactBehaviour.enabled && pw.ImpactBehaviour.discharge)
                || (pw.LifetimeData.enabled && pw.LifetimeData.discharge);
        }

        // Is this orb touching terrain? Also does the digging when
        // damageTerrain is on, rate-limited per orb so an orb parked against a
        // wall chews at a sane pace instead of once per frame.
        private bool TerrainAt(Orb orb, Vector3 pos, float hitR)
        {
            int n = Physics2D.OverlapCircle(pos, hitR, groundFilter, buffer);
            if (n <= 0)
                return false;

            if (cfg.damageTerrain && Time.time >= orb.nextTerrainHit)
            {
                orb.nextTerrainHit =
                    Time.time + Mathf.Max(0.02f, cfg.terrainRepeatDelay);
                // One collider only - a real shot hits one wall, not every
                // level segment overlapping the orb.
                DigTerrain(orb, buffer[0], pos, hitR);
            }
            return true;
        }

        // Hand the level the same hit a real projectile would deliver, so the
        // cell takes damage / is destroyed / catches fire / shakes, and the
        // cell's own resistances still decide whether it actually breaks.
        private void DigTerrain(
            Orb orb, Collider2D col, Vector3 pos, float hitR)
        {
            if (col == null || weapon == null || orb == null || orb.proj == null)
                return;

            IProjectileListener listener = col.GetComponent<IProjectileListener>();
            if (listener == null)
                listener = col.GetComponentInParent<IProjectileListener>();
            if (listener == null)
                return;

            // The level derives the cell from point - normal * 0.5, so we need
            // a surface point plus a normal pointing back out at the orb.
            Vector2 p = pos;
            Vector2 point = p;
            Vector2 normal = Vector2.zero;   // zero => the cell we're inside
            Vector2 surface = col.ClosestPoint(p);
            Vector2 outward = p - surface;
            float d = outward.magnitude;
            if (d > 0.001f && d < hitR + 1f)
            {
                point = surface;
                normal = outward / d;
            }

            Projectile pr = orb.proj;
            if (unit != null)
                pr.Owner = unit;
            pr.Damage = weapon.Damage;
            pr.Burn = weapon.Burn;
            pr.PushForce = (cfg.pushForce > 0f) ? cfg.pushForce : weapon.PushForce;
            // Drives the cell-shake direction only.
            pr.Velocity = (orb.hasLastPos && Time.deltaTime > 0f)
                ? (Vector2)((pos - orb.lastPos) / Time.deltaTime)
                : Vector2.zero;

            // These two decide whether burn lands on contact (the level checks
            // ShouldApplyBurnOnContact), so keep the weapon's own answer.
            var pw = weapon as ProjectileWeapon;
            if (pw != null)
            {
                pr.ImpactBehaviour = pw.ImpactBehaviour;
                pr.LifetimeData = pw.LifetimeData;
            }

            try
            {
                listener.ProjectileCollided(pr, point, normal);
            }
            catch { }
        }

        private void PushAt(Vector3 pos, Vector3 center, float hitR)
        {
            int n = Physics2D.OverlapCircle(pos, hitR, enemyFilter, buffer);
            for (int j = 0; j < n; j++)
            {
                var rb = buffer[j].attachedRigidbody;
                if (rb == null)
                    continue;
                Vector2 away = ((Vector2)rb.transform.position - (Vector2)center).normalized;
                rb.AddForce(away * cfg.pushForce);
            }
        }

        private void BlockAt(Vector3 pos, float hitR)
        {
            // Primary path: tracked enemy projectiles. Their colliders are
            // DISABLED on the prefab (the game moves them by CircleCast), so
            // a physics overlap can never find them - see
            // ForgeProjectileTracker.
            ForgeProjectileTracker.DestroyNear(pos, hitR);

            // Fallback for anything that does carry a live collider on the
            // EnemyProjectiles layer (e.g. lobbed/physics enemy shots).
            int n = Physics2D.OverlapCircle(pos, hitR, projFilter, buffer);
            for (int j = 0; j < n; j++)
                if (buffer[j] != null)
                    Destroy(buffer[j].transform.root.gameObject);
        }

        private void Kill(Orb orb, Vector3 pos)
        {
            orb.alive = false;
            if (orb.go != null)
                orb.go.SetActive(false);

            if (cfg.popExplosion)
                Pop(pos);

            // Timer-based regen schedules a respawn; fire-only waits for a
            // shot (RegenOnFire), so park it far in the future.
            orb.regenAt = (cfg.regenMode == ForgeOrbit.RegenMode.Fire)
                ? float.MaxValue
                : Time.time + cfg.regenTime;
        }

        private void Pop(Vector3 pos)
        {
            if (weapon == null)
                return;

            int n = Physics2D.OverlapCircle(pos, cfg.popRadius, enemyFilter, buffer);
            for (int j = 0; j < n; j++)
            {
                var hb = buffer[j].GetComponentInParent<HealthBase>();
                if (hb != null)
                    hb.TakeDamage(weapon.Damage);
            }
        }

        private void Revive(Orb orb)
        {
            orb.alive = true;
            // A regenerated orb is a NEW orb as far as GrowMode.OrbLife is
            // concerned, so it starts small and swells again.
            orb.bornAt = Time.time;
            orb.regenAt = 0f;
            orb.nextTerrainHit = 0f;
            // Restart its spiral cleanly from the inner radius.
            orb.launched = false;
            orb.hasPrev = false;
            orb.hasLastPos = false;
            orb.cycleStart = Time.time;
        }

        private void SetOrbCount(int count)
        {
            float dur = Mathf.Max(0.01f, cfg.spiralOutTime);
            while (orbs.Count < count)
            {
                // Stagger each orb's spiral cycle so they bloom/launch in
                // sequence rather than all at once (harmless when Off).
                int idx = orbs.Count;
                GameObject newGo = SpawnOrb();
                orbs.Add(new Orb
                {
                    go = newGo,
                    proj = (newGo != null)
                        ? newGo.GetComponentInChildren<Projectile>(true) : null,
                    alive = true,
                    bornAt = Time.time,
                    cycleStart = Time.time - dur * ((idx % count) / (float)count)
                });
            }

            for (int i = 0; i < orbs.Count; i++)
                if (i >= count && orbs[i].go != null)
                    orbs[i].go.SetActive(false);
        }

        private GameObject SpawnOrb()
        {
            GameObject orb;

            if (cfg.visualPrefab != null)
            {
                orb = Instantiate(cfg.visualPrefab);
                var proj = orb.GetComponentInChildren<Projectile>(true);
                if (proj != null)
                    proj.enabled = false;
                // An orb is pure art. A turret component ticks itself, so
                // it would keep firing from the orb if the chosen visual
                // came from a turret weapon.
                foreach (var t in orb.GetComponentsInChildren<ForgeTurret>(true))
                    Destroy(t);
                foreach (var rb in orb.GetComponentsInChildren<Rigidbody2D>(true))
                    rb.simulated = false;
                foreach (var col in orb.GetComponentsInChildren<Collider2D>(true))
                    col.enabled = false;
            }
            else
            {
                orb = new GameObject("Forge Orb");
            }

            orb.name = "Forge Orb";
            return orb;
        }

        private void OnDestroy()
        {
            for (int i = 0; i < orbs.Count; i++)
                if (orbs[i].go != null)
                    Destroy(orbs[i].go);
            orbs.Clear();
        }
    }
}
