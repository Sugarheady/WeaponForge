using System;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;

namespace WeaponForge
{
    // The swing. Rides the ship, one per unit, re-armed on every trigger pull.
    //
    // THE HIT TEST IS AN ANGULAR BAND, NOT A POINT. Each frame the blade is
    // somewhere between last frame's angle and this frame's, so anything whose
    // bearing falls in that band gets hit. Testing only the blade's current
    // position would let a fast swing step straight over a target between two
    // frames - and a 120-degree sweep in 0.18s moves about 11 degrees per frame
    // at 60fps, which is easily a whole enemy.
    //
    // ONE OVERLAP PER FRAME, then a bearing test per candidate. The overlap is
    // the expensive half and it does not care about the angle, so there is no
    // reason to do it more than once.
    //
    // ONCE PER TARGET PER SWING. `HealthBase` keys a set that is cleared when
    // the swing starts, which is the rule a blade wants - unlike the ram's
    // time-based repeat delay, because a swing has a definite beginning and
    // end and "the edge passed through you once" is the honest count.
    public class ForgeSwipeController : MonoBehaviour
    {
        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("WeaponForge.Swipe");

        private Unit unit;
        private ForgeSwipe.Config cfg;
        private WeaponBase weapon;

        private ContactFilter2D enemyFilter;
        private readonly Collider2D[] buffer = new Collider2D[64];
        private readonly List<Component> caught = new List<Component>();
        private readonly HashSet<HealthBase> struck = new HashSet<HealthBase>();

        // ONCE PER BULLET PER SWING, the same rule `struck` gives units.
        //
        // Needed only since the rally sweep arrived, and it is not optional.
        // `Shots` runs on every frame of the swing, and a rallied shot is put
        // straight back into tracking by ForgeRally.OnReflected - so without
        // this a single bullet sitting inside the wedge is re-caught every
        // frame. A 0.12s swing at 60fps is seven frames, which would apply
        // seven compounding rally stacks for one swing and blow straight past
        // `maxDeflect` in the only mode where the cap is per swing.
        private readonly HashSet<Component> batted = new HashSet<Component>();

        private bool filtersReady;

        // Swing state.
        private bool swinging;
        private float startedAt;
        private Vector2 axis = Vector2.right;   // the aim at the moment of the pull
        private float fromAngle;
        private float toAngle;
        private float lastAngle;
        private bool flip;                      // which way the LAST swing went
        private int deflected;

        private GameObject art;
        private SpriteRenderer artRenderer;
        private float artFadeUntil;

        // ------------------------------------------------------------------
        public static void Swing(
            Unit owner, ForgeSwipe.Config cfg, WeaponBase weapon, Vector2 aim)
        {
            if (owner == null || cfg == null)
                return;

            try
            {
                var c = owner.gameObject.GetComponent<ForgeSwipeController>();

                if (c == null)
                    c = owner.gameObject.AddComponent<ForgeSwipeController>();

                c.unit = owner;
                c.cfg = cfg;
                c.weapon = weapon;
                c.Begin(aim);
            }
            catch (Exception e)
            {
                Log.LogError("Starting a swipe failed: " + e);
            }
        }

        private void Begin(Vector2 aim)
        {
            if (aim.sqrMagnitude < 0.000001f)
                aim = transform.right;

            axis = aim.normalized;

            float half = cfg.Half;

            bool rightward;

            switch (cfg.direction)
            {
                case ForgeSwipe.Rightward: rightward = true; break;
                case ForgeSwipe.Leftward: rightward = false; break;
                case ForgeSwipe.Random:
                    rightward = UnityEngine.Random.value < 0.5f; break;
                default:
                    // Alternate. `flip` remembers the last one, so holding the
                    // trigger reads as a proper left-right-left rhythm rather
                    // than the same stroke over and over.
                    rightward = flip;
                    flip = !flip;
                    break;
            }

            // Rightward means starting on the left and sweeping to the right,
            // i.e. from +half down to -half in Unity's counter-clockwise
            // degrees.
            fromAngle = rightward ? half : -half;
            toAngle = rightward ? -half : half;

            lastAngle = fromAngle;
            startedAt = Time.time;
            swinging = true;
            deflected = 0;
            struck.Clear();
            batted.Clear();

            if (!string.IsNullOrEmpty(cfg.sfx))
            {
                try { AudioManager.PlaySfx(cfg.sfx, transform.position); }
                catch (Exception) { }
            }

            ShowArt(rightward);
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
            if (cfg == null)
                return;

            try
            {
                if (swinging)
                    Step();

                Paint();
            }
            catch (Exception e)
            {
                Log.LogError("A swipe failed: " + e);
                swinging = false;
            }
        }

        private void Step()
        {
            float dur = Mathf.Max(0.02f, cfg.duration);
            float t = Mathf.Clamp01((Time.time - startedAt) / dur);

            float angle = Mathf.Lerp(fromAngle, toAngle, Eased(t));

            Sweep(lastAngle, angle);

            lastAngle = angle;

            if (t >= 1f)
            {
                swinging = false;
                artFadeUntil = Time.time + Mathf.Max(0f, cfg.fadeOut);
            }
        }

        // Smoothstep raised to `ease`: slow at both ends, quick through the
        // middle. At ease 1 it is a plain linear sweep.
        private float Eased(float t)
        {
            float e = Mathf.Max(0.1f, cfg.ease);

            if (Mathf.Abs(e - 1f) < 0.001f)
                return t;

            float s = t * t * (3f - 2f * t);

            return Mathf.Lerp(t, s, Mathf.Clamp01(e - 1f));
        }

        // ------------------------------------------------------------------
        // Everything the edge passes through between two angles.
        // ------------------------------------------------------------------
        private void Sweep(float a, float b)
        {
            if (Mathf.Abs(a - b) < 0.0001f)
                return;

            float lo = Mathf.Min(a, b);
            float hi = Mathf.Max(a, b);

            Vector2 here = transform.position;

            Units(here, lo, hi);
            Shots(here, lo, hi);
            Terrain(here, lo, hi);
        }

        // Bearing of a point relative to the swing axis, in degrees.
        private float Bearing(Vector2 here, Vector2 point)
        {
            Vector2 to = point - here;

            if (to.sqrMagnitude < 0.000001f)
                return 0f;

            return Vector2.SignedAngle(axis, to);
        }

        private void Units(Vector2 here, float lo, float hi)
        {
            if (!cfg.hasDamage && cfg.knockback == 0f && !cfg.marksSlammable)
                return;

            EnsureFilters();

            int n = Physics2D.OverlapCircle(here, cfg.reach, enemyFilter, buffer);

            for (int i = 0; i < n; i++)
            {
                Collider2D col = buffer[i];

                if (col == null)
                    continue;

                float bearing = Bearing(here, col.transform.position);

                if (bearing < lo || bearing > hi)
                    continue;

                var hb = col.GetComponentInParent<HealthBase>();

                if (hb == null || struck.Contains(hb))
                    continue;

                // Never cut yourself.
                if (unit != null && hb.GetComponentInParent<Unit>() == unit)
                    continue;

                struck.Add(hb);

                Hurt(hb, col, here);
            }
        }

        private void Hurt(HealthBase hb, Collider2D col, Vector2 here)
        {
            Unit victim = hb.GetComponentInParent<Unit>();

            if (cfg.hasDamage)
            {
                Damage d = (weapon != null)
                    ? weapon.Damage
                    : new Damage(0f, null);

                if (cfg.damageType != null)
                    d.damageType = cfg.damageType;

                // 0 means "use the weapon's own", which is why hasDamage exists
                // separately - a swipe that deliberately does no damage at all
                // is a legitimate thing (a pure deflector, or a shover).
                if (cfg.damage > 0f)
                    d.amount = cfg.damage;

                if (d.amount > 0f && d.damageType != null)
                {
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

                    // The game credits a kill from three places and a blade is
                    // none of them, so a swipe kill would otherwise be
                    // invisible to onKill rewards, the music controller and
                    // everything else listening.
                    if (unit != null && weapon != null &&
                        ForgeKillsPatch.JustDied(hb))
                    {
                        ForgeKillsPatch.Credit(unit, victim, weapon);
                    }
                }
            }

            Rigidbody2D rb = col.attachedRigidbody;

            if (cfg.knockback != 0f && rb != null)
            {
                // Along the SWING, not away from the ship - which is the point
                // of a swipe: it sends things sideways, so you can bat one
                // enemy into another. The tangent is the axis rotated by the
                // blade's current angle plus a quarter turn, signed by which
                // way the swing is going.
                float sign = (toAngle > fromAngle) ? 1f : -1f;

                Vector2 radial =
                    (Vector2)col.transform.position - (Vector2)transform.position;

                Vector2 dir = (radial.sqrMagnitude > 0.000001f)
                    ? new Vector2(-radial.y, radial.x).normalized * sign
                    : axis;

                rb.AddForce(dir * cfg.knockback, ForceMode2D.Impulse);
            }

            // Marking is free and composes with everything - see ForgeSlam.
            if (cfg.marksSlammable && victim != null && weapon != null)
            {
                ForgeSlam.Spec spec;

                if (ForgeSlam.TryGet(weapon.TemplateData, out spec))
                {
                    ForgeSlam.Mark(
                        victim, spec, unit, weapon,
                        (weapon != null) ? weapon.Damage.damageType : null);
                }
            }
        }

        // ------------------------------------------------------------------
        private void Shots(Vector2 here, float lo, float hi)
        {
            if (!cfg.deflects || deflected >= Mathf.Max(1, cfg.maxDeflect))
                return;

            // A wedge centred on the middle of the band, wide enough to cover
            // it. CollectInSector is destructive - it hands ownership over -
            // which is right here: a batted shot becomes ours.
            float mid = (lo + hi) * 0.5f;
            float halfBand = Mathf.Max((hi - lo) * 0.5f, 4f);

            Vector2 facing = Rotate(axis, mid);

            caught.Clear();

            int room = Mathf.Max(1, cfg.maxDeflect) - deflected;

            ForgeProjectileTracker.CollectInSector(
                here, cfg.reach, facing, halfBand, room, caught);

            // RALLY: sweep our OWN shots too, and this is the whole reason a
            // rally works on a blade at all.
            //
            // Batting a bullet makes it YOURS - Turn remaps its layer - so the
            // tracker files it under _mine from then on, and CollectInSector
            // only ever reads the ENEMY list. Without this the blade buffs a
            // shot exactly once and can never find it again, which is not a
            // rally, it is a one-off damage multiplier. Reported for R12 test
            // 101 as *"it rallys the projectile once but doesn't let me do it a
            // second time to the same bullet"* - and it is the third time this
            // same omission has shipped: the shield had it, then the deflector
            // had it, and the blade was written from the shield's ENEMY-side
            // loop rather than from the fixed one.
            if (cfg.rally != null && cfg.rally.enabled)
            {
                int before = caught.Count;

                ForgeProjectileTracker.CollectMineInSector(
                    here, cfg.reach, facing, halfBand, 0, caught);

                // Only MARKED shots. Everything else you have fired is handed
                // straight back untouched, or the blade would start batting
                // your own ordinary fire around the room.
                for (int i = caught.Count - 1; i >= before; i--)
                {
                    Component mine = caught[i];

                    if (mine != null &&
                        mine.GetComponent<ForgeRally.ForgeRallied>() != null)
                    {
                        continue;
                    }

                    if (mine != null)
                        ForgeProjectileTracker.Register(mine);

                    caught.RemoveAt(i);
                }
            }

            for (int i = 0; i < caught.Count; i++)
            {
                Component bullet = caught[i];

                if (bullet == null)
                    continue;

                // Out of budget, or already batted this swing. Either way it
                // was collected DESTRUCTIVELY, so it has to be handed back or
                // it sails on with nothing able to find it again.
                //
                // The budget is tested before `batted` is written, so a bullet
                // turned away for want of budget is still eligible on the next
                // frame of the swing.
                if (deflected >= Mathf.Max(1, cfg.maxDeflect) ||
                    !batted.Add(bullet))
                {
                    ForgeProjectileTracker.Register(bullet);
                    continue;
                }

                // MIRROR by default: a shot batted by a moving edge should
                // leave at the angle it arrived. The ship's position is the
                // surface origin, so the blade reads as a convex mirror and
                // WHERE on the arc you catch something decides where it goes.
                if (ForgeBulletTurn.Turn(
                        bullet, unit, cfg.aimMode, cfg.reach,
                        cfg.deflectSpeed, cfg.deflectDamage, cfg.rally,
                        transform.position))
                {
                    deflected++;
                }
            }
        }

        // ------------------------------------------------------------------
        private void Terrain(Vector2 here, float lo, float hi)
        {
            if (cfg.cutTerrain <= 0f && cfg.igniteTerrain <= 0f)
                return;

            Level level;

            if (!ServiceLocator.TryGet<Level>(out level) || level == null)
                return;

            float r = cfg.reach;

            int minX = Mathf.FloorToInt(here.x - r);
            int maxX = Mathf.CeilToInt(here.x + r);
            int minY = Mathf.FloorToInt(here.y - r);
            int maxY = Mathf.CeilToInt(here.y + r);

            float sqr = r * r;

            for (int x = minX; x <= maxX; x++)
            {
                for (int y = minY; y <= maxY; y++)
                {
                    if (!level.ContainsCell(x, y) || level.IsEmpty(x, y))
                        continue;

                    float dx = x - here.x;
                    float dy = y - here.y;

                    if (dx * dx + dy * dy > sqr)
                        continue;

                    float bearing = Bearing(here, new Vector2(x, y));

                    if (bearing < lo || bearing > hi)
                        continue;

                    if (cfg.cutTerrain > 0f)
                    {
                        CellType type = level.GetCellType(x, y);

                        // THE SAME HARDNESS GATE EXPLOSIONS USE. GetDamageAmount
                        // returns what this cell type actually takes from that
                        // damage, so a soft cell carves and Solid shrugs unless
                        // the number is big. Without this a swipe would cut
                        // everything equally, which would let a starter weapon
                        // dig through the level.
                        if (type != null &&
                            type.GetDamageAmount(
                                new Damage(cfg.cutTerrain, null)) > 0f)
                        {
                            level.DestroyCell(x, y, 0);
                            continue;
                        }
                    }

                    if (cfg.igniteTerrain > 0f)
                    {
                        level.SetBurnLevel(
                            x, y,
                            level.GetBurnLevel(x, y) + cfg.igniteTerrain);
                    }
                }
            }
        }

        // ------------------------------------------------------------------
        // The blade itself
        // ------------------------------------------------------------------
        private void ShowArt(bool rightward)
        {
            Sprite s = Art();

            if (s == null)
                return;

            if (art == null)
            {
                art = new GameObject("Forge Swipe Blade");
                art.transform.SetParent(transform, false);
                artRenderer = art.AddComponent<SpriteRenderer>();

                // Borrow the ship's own sorting slot so the blade cannot land
                // behind the terrain - the same lesson as the charge meter,
                // where a sprite drawn into the wrong layer is invisible in a
                // way that looks exactly like a feature that did not run.
                var donor = GetComponentInChildren<SpriteRenderer>();

                if (donor != null)
                {
                    artRenderer.sortingLayerID = donor.sortingLayerID;
                    artRenderer.sortingOrder = donor.sortingOrder + 2;
                }
            }

            artRenderer.sprite = s;
            artRenderer.enabled = true;

            // Idempotent, so calling it per swing is free - and it has to be
            // here rather than in the creation block above, because a swing
            // that reuses the blade object still wants the animator present.
            //
            // restart: TRUE. The blade is ONE GameObject switched on for a
            // fraction of a second per swing, so a continuously-running
            // animation would have the second swing catch it at an arbitrary
            // phase - and "a fast run over a short swing draws the slash" only
            // holds if each swing starts at frame 0.
            VisualCustomizer.AnimateRenderer(
                artRenderer, cfg.artFrames, cfg.artFps, false, true);

            // The GENERATED crescent has the colour baked into its pixels, so
            // tinting it again would square it. A custom sprite is somebody
            // else's art, so `color` tints it the way every other art key in
            // the mod does.
            if (!string.IsNullOrEmpty(cfg.sprite) && cfg.hasColor)
            {
                artRenderer.color = cfg.color;
            }
            else if (artRenderer.color != Color.white &&
                     string.IsNullOrEmpty(cfg.sprite))
            {
                artRenderer.color = Color.white;
            }

            // ALTERNATING SWINGS MIRROR THE ART - and WHICH AXIS depends on
            // which art, because the two modes do not share a local frame.
            //
            // This shipped mirroring Y for both, and Y is the wrong axis for a
            // custom sprite. Reported for R12 test 48 as *"the first swing is
            // correct and the second swing is flipped... like PLAYER-) first
            // swipe, PLAYER-( second swipe"* - the bend pointing away from the
            // ship on one stroke and back into it on the next.
            //
            // Read `Paint` alongside this. The two frames are 90 degrees apart:
            //
            //   generated crescent  +X is RADIAL (rotated to `facing`, drawn at
            //                       the ship) and +Y is the tangent
            //   custom sprite       +X is the TANGENT (rotated to the tangent,
            //                       drawn out at the blade) and +Y is radial
            //
            // A mirror is only correct across the RADIAL axis - that is what
            // reverses the stroke while leaving "away from the ship" alone. For
            // the crescent that axis is X, so scaling Y is right; for a custom
            // sprite it is Y, so scaling Y flips the bend inward, which is
            // exactly what he saw.
            //
            // And the give-away that nobody could have spotted from the
            // symptom: ForgeSwipeArt.Sample uses |DeltaAngle(0, ang)|, so the
            // crescent is MIRROR-SYMMETRIC about its own X axis. Flipping Y was
            // a no-op there. **The flip only ever affected custom art, and on
            // custom art it was the wrong axis** - so this line has never once
            // done what it says.
            bool custom = !string.IsNullOrEmpty(cfg.sprite);
            float mirror = rightward ? 1f : -1f;

            art.transform.localScale = custom
                ? new Vector3(mirror, 1f, 1f)
                : new Vector3(1f, mirror, 1f);
        }

        private Sprite Art()
        {
            if (cfg.artTried)
                return cfg.art;

            cfg.artTried = true;

            if (!string.IsNullOrEmpty(cfg.sprite))
            {
                ForgeSpriteLibrary.LoadAll();

                ForgeSpriteLibrary.Art found;

                if (ForgeSpriteLibrary.TryGetAnyArt(cfg.sprite, out found) &&
                    found != null)
                {
                    cfg.art = found.sprite;

                    cfg.artFrames =
                        ForgeSpriteLibrary.OptInFrames(found, out cfg.artFps);

                    return cfg.art;
                }

                Log.LogWarning(
                    "swipe \"sprite\": '" + cfg.sprite + "' is not a sprite I " +
                    "can find, so the generated blade is used instead." +
                    ForgeSpriteLibrary.NotFoundHint());
            }

            cfg.art = ForgeSwipeArt.Build(
                cfg.arc * Mathf.Clamp01(cfg.trail),
                cfg.thickness,
                cfg.reach,
                cfg.hasColor ? cfg.color : new Color(0.85f, 0.95f, 1f, 0.9f));

            return cfg.art;
        }

        private void Paint()
        {
            if (artRenderer == null || !artRenderer.enabled)
                return;

            bool custom = !string.IsNullOrEmpty(cfg.sprite);

            if (swinging)
            {
                Vector2 facing = Rotate(axis, lastAngle);

                if (custom)
                {
                    // A hand-drawn swoosh is placed AT the blade and turned to
                    // the tangent, because that is how anyone would draw one -
                    // as a slash, centred on itself. Only the generated
                    // crescent is authored around the ship.
                    art.transform.localPosition = facing * cfg.reach;
                    art.transform.localRotation = Quaternion.FromToRotation(
                        Vector3.right, new Vector3(-facing.y, facing.x, 0f));
                }
                else
                {
                    art.transform.localPosition = Vector3.zero;
                    art.transform.localRotation =
                        Quaternion.FromToRotation(Vector3.right, facing);
                }

                Color c = artRenderer.color;
                artRenderer.color = new Color(c.r, c.g, c.b, 1f);
                return;
            }

            // Fading out after the stroke.
            float left = artFadeUntil - Time.time;

            if (left <= 0f)
            {
                artRenderer.enabled = false;
                return;
            }

            float f = Mathf.Clamp01(left / Mathf.Max(0.001f, cfg.fadeOut));
            Color col = artRenderer.color;
            artRenderer.color = new Color(col.r, col.g, col.b, f);
        }

        private static Vector2 Rotate(Vector2 v, float degrees)
        {
            float r = degrees * Mathf.Deg2Rad;
            float cos = Mathf.Cos(r);
            float sin = Mathf.Sin(r);

            return new Vector2(v.x * cos - v.y * sin, v.x * sin + v.y * cos);
        }
    }
}
