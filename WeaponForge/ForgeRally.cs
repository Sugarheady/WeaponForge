using System;
using BepInEx.Logging;
using UnityEngine;

namespace WeaponForge
{
    // Bat the SAME bullet back and forth, and make it meaner every time.
    //
    // WHAT ACTUALLY BLOCKED THIS, because it was not the buffing. Reflecting a
    // shot converts it to your layer, your collision mask and your ownership
    // (see ForgeBulletTurn) - which is exactly right for a one-off deflect and
    // exactly wrong for a rally, because your shield only ever looks at ENEMY
    // fire. The instant you reflected a bullet it stopped being something you
    // could catch. So the same shot could never be caught twice, no matter what
    // multiplier anyone hung off it.
    //
    // The fix is not to leave it hostile - a hostile-layer bullet cannot damage
    // enemies at all, so a "rally" of those would be a toy. Instead a rallied
    // shot is marked, re-registered with the projectile tracker, and the
    // reflectors are taught to look at marked shots of YOUR OWN as well as
    // enemy fire. It stays a real player bullet the whole time; it is just
    // also catchable.
    //
    // So the loop is: reflect it, chase it down, get in front of it, reflect it
    // again. `bounce` is there for when you would rather the room did the
    // returning for you.
    public static class ForgeRally
    {
        private static readonly ManualLogSource Log =
            ForgeLog.Source("WeaponForge.Rally");

        // When a rallied shot is dangerous to its owner.
        public const int HostileNever = 0;   // default
        public const int HostileAlways = 1;
        public const int HostileAfter = 2;   // once it is charged enough

        public static int ParseHostile(string s)
        {
            switch ((s ?? "").Trim().ToLowerInvariant())
            {
                case "always":
                case "true":
                case "yes":
                    return HostileAlways;
                case "after":
                case "threshold":
                case "charged":
                    return HostileAfter;
                default:
                    return HostileNever;
            }
        }

        // What happens when it reaches the cap.
        public const int CapStop = 0;      // stops getting stronger (default
                                           // shape, but maxStacks is 0 = none)
        public const int CapDetonate = 1;  // blows up where it is

        public static int ParseCap(string s)
        {
            switch ((s ?? "").Trim().ToLowerInvariant())
            {
                case "detonate":
                case "explode":
                case "burst":
                    return CapDetonate;
                default:
                    return CapStop;
            }
        }

        public class Config
        {
            public bool enabled = true;

            // 0 = NO CAP, which is the default he asked for. A long rally
            // compounds without limit; the builder says so out loud.
            public int maxStacks;
            public int atCap = CapStop;

            public float damagePerReflect = 1.6f;
            public float speedPerReflect = 1f;
            public float sizePerReflect = 1f;
            public int piercePerReflect;

            // Without this a rallied shot expires mid-rally, which reads as the
            // feature randomly failing rather than as a timer running out.
            public float lifePerReflect = 1.5f;

            public bool bounce;            // let the room return it for you
            public int hostile = HostileNever;
            public int hostileAfter = 3;

            public string sfx = "";
            public bool tint;
            public Color hotColor = new Color(1f, 0.6f, 0.15f, 1f);
        }

        // Rides the bullet. A plain component on a LIVE object, which is the
        // safe shape - a Config reference on a PREFAB-attached component would
        // arrive null on every clone (see ForgeConfigTable and the gas/catalyst
        // disaster), but nothing here is ever on a prefab.
        public class ForgeRallied : MonoBehaviour
        {
            public int stacks;
            public Config cfg;
            public Unit owner;
        }

        // Called immediately after a bullet has been turned around.
        public static void OnReflected(
            Component bullet, Unit owner, Config cfg)
        {
            if (bullet == null || cfg == null || !cfg.enabled)
                return;

            try
            {
                var ip = bullet as IProjectile;

                if (ip == null)
                    return;

                var mark = bullet.gameObject.GetComponent<ForgeRallied>();

                if (mark == null)
                    mark = bullet.gameObject.AddComponent<ForgeRallied>();

                mark.cfg = cfg;
                mark.owner = owner;

                bool capped =
                    cfg.maxStacks > 0 && mark.stacks >= cfg.maxStacks;

                if (!capped)
                    mark.stacks++;

                Buff(bullet, ip, mark);

                if (cfg.bounce)
                    GiveBounce(bullet);

                Faction(bullet, mark);

                if (!string.IsNullOrEmpty(cfg.sfx))
                    AudioManager.PlaySfx(cfg.sfx, bullet.transform.position);

                // Back into tracking so it can be caught again. Turn() has
                // already remapped it to the player layer, so Register routes
                // it to the player list - which is why `rally` switches
                // TrackPlayer on at build time.
                ForgeProjectileTracker.Register(bullet);

                if (capped && cfg.atCap == CapDetonate)
                    Detonate(bullet, ip);
            }
            catch (Exception e)
            {
                Log.LogError("Rallying a shot failed: " + e);
            }
        }

        private static void Buff(
            Component bullet, IProjectile ip, ForgeRallied mark)
        {
            Config cfg = mark.cfg;
            int n = mark.stacks;

            // DAMAGE THROUGH THE STACK, never by writing Damage.
            //
            // Two features assigning Damage from their own captured bases means
            // whichever FixedUpdate prefix runs last wins, and Harmony orders
            // them arbitrarily - which is how grow and damageRamp would have
            // cancelled each other out. The stack keeps one captured base and
            // multiplies every contributor, so it is order-independent.
            if (cfg.damagePerReflect != 1f)
            {
                ForgeDamageStack stack = ForgeDamageStack.For(bullet);

                if (stack != null)
                {
                    stack.Capture(ip.Damage.amount);
                    stack.rally = Mathf.Pow(cfg.damagePerReflect, n);

                    var plain = bullet as Projectile;

                    if (plain != null)
                        stack.Apply(plain);
                    else
                        stack.Apply(bullet as PhysicsProjectile);
                }
            }

            if (cfg.speedPerReflect != 1f)
                ip.Velocity = ip.Velocity * cfg.speedPerReflect;

            // Radius is the REAL damage volume on a plain projectile - the
            // FixedUpdate circle cast is rebuilt from it every frame - so this
            // grows the hitbox, not just the look. The art is scaled to match.
            if (cfg.sizePerReflect != 1f)
            {
                var plain = bullet as Projectile;

                if (plain != null)
                    plain.Radius = plain.Radius * cfg.sizePerReflect;

                bullet.transform.localScale =
                    bullet.transform.localScale * cfg.sizePerReflect;
            }

            // PIERCING IS NOT A COUNT IN THIS GAME. `PiercingData` is
            // { enabled, damageRepeatDelay, knockBackRepeatDelay } and nothing
            // more - a piercing shot passes through everything, forever. The
            // COUNT is the mod's own, tracked per shot by ForgePierceCap, so
            // raising it means raising that cap rather than a game field.
            if (cfg.piercePerReflect > 0)
            {
                var plainShot = bullet as Projectile;

                if (plainShot != null)
                {
                    PiercingData pierce = plainShot.PiercingData;
                    pierce.enabled = true;

                    // Never 0: a zero repeat delay re-damages the same target
                    // every frame it overlaps. Stock weapons use about 0.3.
                    if (pierce.damageRepeatDelay <= 0.01f)
                        pierce.damageRepeatDelay = 0.3f;

                    plainShot.PiercingData = pierce;
                }

                var cap = bullet.GetComponent<ForgePierceCap>();

                if (cap == null)
                    cap = bullet.gameObject.AddComponent<ForgePierceCap>();

                cap.limit = Mathf.Max(
                    cap.limit, cfg.piercePerReflect * mark.stacks);
            }

            // Keep it alive long enough to be caught again. Without this a
            // rally ends because the shot timed out, which looks exactly like
            // the feature not working.
            if (cfg.lifePerReflect > 1f)
            {
                ProjectileLifetimeData life = ip.LifetimeData;

                if (life.enabled && life.time > 0f)
                {
                    life.time *= cfg.lifePerReflect;
                    ip.LifetimeData = life;
                }

                ProjectileRangeData range = ip.RangeData;

                if (range.enabled && range.range > 0f)
                {
                    range.range *= cfg.lifePerReflect;

                    // slowDown means the shot's velocity is EXACTLY zero at the
                    // moment it dies, and a stalled bullet cannot be rallied.
                    range.slowDown = false;
                    ip.RangeData = range;
                }
            }

            if (cfg.tint)
            {
                float f = Mathf.Clamp01(n / 5f);
                VisualCustomizer.Tint(
                    bullet.gameObject, Color.Lerp(Color.white, cfg.hotColor, f));
            }
        }

        // Terrain bounce, so the room hands the shot back to you.
        //
        // ProjectileBounceData is a CLASS, so a fresh instance every time -
        // mutating the one the weapon came with would make every stock shot
        // sharing that asset bounce too.
        private static void GiveBounce(Component bullet)
        {
            var plain = bullet as Projectile;

            if (plain == null)
                return;   // a PhysicsProjectile already bounces on its own

            var data = new ProjectileBounceData();
            data.enabled = true;
            data.layerMask = 1 << LayerMask.NameToLayer("Ground");

            plain.ProjectileBounceData = data;
        }

        // Who it can hurt. Faction IS the GameObject layer, so this is the same
        // four-part change ForgeBulletTurn documents - which is why it defers
        // to that rather than remapping by hand.
        private static void Faction(Component bullet, ForgeRallied mark)
        {
            Config cfg = mark.cfg;

            bool dangerous =
                cfg.hostile == HostileAlways ||
                (cfg.hostile == HostileAfter &&
                 mark.stacks >= Mathf.Max(1, cfg.hostileAfter));

            if (!dangerous)
                return;

            // Send it back to the enemy layer so it can hurt its wielder again.
            // Note the honest cost, which is why this is off by default: a shot
            // on the enemy layer cannot damage enemies at all, so a permanently
            // hostile rally is a hot potato rather than a weapon.
            int player = ForgeBulletTurn.PlayerLayer;
            int enemy = ForgeBulletTurn.EnemyLayer;

            if (player >= 0 && enemy >= 0)
                VisualCustomizer.RemapLayer(bullet.gameObject, player, enemy);
        }

        private static void Detonate(Component bullet, IProjectile ip)
        {
            try
            {
                Explosion ex = ip.Explosion;

                if (ex.damages == null || ex.damages.Count == 0)
                {
                    // Nothing authored to detonate with - destroying it
                    // silently would look like the shot vanished, so say so.
                    Log.LogWarning(
                        "rally \"atCap\": \"detonate\" needs the weapon to " +
                        "have an explosion set up (radius and a damage entry). " +
                        "This one has none, so the shot was left alone.");
                    return;
                }

                ExplosionManager manager;

                if (ServiceLocator.TryGet<ExplosionManager>(out manager) &&
                    manager != null)
                {
                    ex.Owner = ip.Owner;
                    manager.SpawnExplosion(bullet.transform.position, ex);
                }

                UnityEngine.Object.Destroy(bullet.gameObject);
            }
            catch (Exception e)
            {
                Log.LogError("Rally detonation failed: " + e);
            }
        }
    }
}
