using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace WeaponForge
{
    // Keeps a list of live ENEMY projectiles so orbit orbs can shoot them
    // down.
    //
    // Why this is needed: PUNK projectiles resolve their own collisions with
    // a CircleCast, so their Collider2D is DISABLED on the prefab
    // (m_Enabled: 0). That makes them completely invisible to
    // Physics2D.OverlapCircle - which is why "orbitBlockProjectiles" never
    // blocked anything. Instead we register each enemy projectile as it is
    // shot and do a plain distance test.
    //
    // Tracking is off unless some weapon actually asks for blocking, so
    // there's zero cost (and no list growth) for everyone else.
    public static class ForgeProjectileTracker
    {
        // Set by WeaponBuilder when any orbit weapon enables blockProjectiles.
        public static bool Enabled;

        // Set separately, by `catalyst`, because tracking the PLAYER's shots is
        // a different job with a different cost and almost nothing needs it.
        //
        // Why it is needed at all: a catalyst node has to notice one of YOUR
        // bullets arriving, and there is no physics route to that. Player
        // projectiles do not collide with each other (PlayerProjectiles is
        // absent from its own row in the layer matrix), and a plain Projectile
        // ships with its Collider2D DISABLED because it resolves hits by
        // CircleCast - so an overlap query finds nothing at any radius, exactly
        // as with enemy fire.
        public static bool TrackPlayer;

        // Component, not Projectile: the game has TWO projectile classes and
        // enemies use both. Straight shots are Projectile; lobbed shots and
        // missiles are PhysicsProjectile, a separate class that shares no base
        // with it. Both are Components and both implement IProjectile, which is
        // all a caller needs - position and gameObject from the Component,
        // Velocity/Owner/Damage from the interface.
        private static readonly List<Component> _live = new List<Component>();
        private static readonly List<Component> _mine = new List<Component>();
        private static int _enemyLayer = -2;
        private static int _playerLayer = -2;

        private static int EnemyLayer
        {
            get
            {
                if (_enemyLayer == -2)
                    _enemyLayer = LayerMask.NameToLayer("EnemyProjectiles");
                return _enemyLayer;
            }
        }

        private static int PlayerLayer
        {
            get
            {
                if (_playerLayer == -2)
                    _playerLayer = LayerMask.NameToLayer("PlayerProjectiles");
                return _playerLayer;
            }
        }

        public static void Register(Component projectile)
        {
            if (projectile == null)
                return;

            if (TrackPlayer && PlayerLayer >= 0 &&
                projectile.gameObject.layer == PlayerLayer)
            {
                if (_mine.Count > 256)
                    PruneMine();

                _mine.Add(projectile);
                return;
            }

            if (!Enabled)
                return;
            if (EnemyLayer < 0 || projectile.gameObject.layer != EnemyLayer)
                return;

            // Destroyed entries are pruned lazily; keep that bounded even if
            // nothing is calling DestroyNear for a while.
            if (_live.Count > 256)
                Prune();

            _live.Add(projectile);
        }

        private static void Prune()
        {
            for (int i = _live.Count - 1; i >= 0; i--)
                if (_live[i] == null)
                    _live.RemoveAt(i);
        }

        // Destroys tracked enemy shots within `radius` of `pos`.
        public static int DestroyNear(Vector2 pos, float radius)
        {
            if (_live.Count == 0)
                return 0;

            float sqr = radius * radius;
            int killed = 0;

            for (int i = _live.Count - 1; i >= 0; i--)
            {
                Component p = _live[i];
                if (p == null)
                {
                    _live.RemoveAt(i);
                    continue;
                }

                if (((Vector2)p.transform.position - pos).sqrMagnitude <= sqr)
                {
                    _live.RemoveAt(i);
                    Object.Destroy(p.gameObject);
                    killed++;
                }
            }

            return killed;
        }

        // Hand back the tracked enemy shots within `radius`, REMOVING them from
        // the list - the caller has taken responsibility for them, whether that
        // means destroying them or turning them around.
        //
        // Separate from DestroyNear because a deflector does not want them
        // dead: it wants to re-aim them back at whoever fired them, and that
        // needs the actual Projectile, not a body count.
        public static int CollectNear(
            Vector2 pos, float radius, int max, List<Component> into)
        {
            if (into == null || _live.Count == 0)
                return 0;

            float sqr = radius * radius;
            int taken = 0;

            for (int i = _live.Count - 1; i >= 0; i--)
            {
                if (max > 0 && taken >= max)
                    break;

                Component p = _live[i];

                if (p == null)
                {
                    _live.RemoveAt(i);
                    continue;
                }

                if (((Vector2)p.transform.position - pos).sqrMagnitude <= sqr)
                {
                    _live.RemoveAt(i);
                    into.Add(p);
                    taken++;
                }
            }

            return taken;
        }

        // The same hand-off as CollectNear, but limited to a WEDGE: within
        // `radius` of `pos` AND within `halfAngle` degrees of `facing`.
        //
        // This exists because the shield arc covers a slice, not a circle, and
        // CollectNear REMOVES everything it returns. Collecting a circle and
        // then filtering by angle would quietly untrack every bullet that
        // missed the wedge - they would sail on and hit the player, and nothing
        // could ever catch them again. Filter inside the walk, so only what is
        // genuinely taken is removed.
        //
        // A whole sector rather than a thin shell, deliberately: a bullet
        // moving 40 units/second crosses a half-unit band between two frames,
        // so a shell-shaped test would let fast shots tunnel straight through.
        // Anything inside the slice is fair game.
        public static int CollectInSector(
            Vector2 pos,
            float radius,
            Vector2 facing,
            float halfAngle,
            int max,
            List<Component> into)
        {
            if (into == null || _live.Count == 0)
                return 0;

            if (facing.sqrMagnitude < 0.000001f)
                return 0;

            float sqr = radius * radius;
            float cosLimit = Mathf.Cos(Mathf.Clamp(halfAngle, 0f, 180f) *
                                       Mathf.Deg2Rad);
            Vector2 f = facing.normalized;
            int taken = 0;

            for (int i = _live.Count - 1; i >= 0; i--)
            {
                if (max > 0 && taken >= max)
                    break;

                Component p = _live[i];

                if (p == null)
                {
                    _live.RemoveAt(i);
                    continue;
                }

                Vector2 to = (Vector2)p.transform.position - pos;

                if (to.sqrMagnitude > sqr)
                    continue;

                // Dot against the normalised offset is the same test as
                // Vector2.Angle without the acos. A shot sitting exactly on the
                // ship has no direction to judge, so it counts as covered.
                if (to.sqrMagnitude > 0.000001f &&
                    Vector2.Dot(f, to.normalized) < cosLimit)
                    continue;

                _live.RemoveAt(i);
                into.Add(p);
                taken++;
            }

            return taken;
        }

        // The player's own shots in a SECTOR, for the rally.
        //
        // Destructive like CollectInSector and unlike MineNear, because a
        // reflector genuinely takes the shot: it turns it around and then
        // re-registers it. The caller hands back anything it decides not to
        // keep, which is the same idiom the shield already uses when it cannot
        // afford a block.
        public static int CollectMineInSector(
            Vector2 pos,
            float radius,
            Vector2 facing,
            float halfAngle,
            int max,
            List<Component> into)
        {
            if (into == null || _mine.Count == 0)
                return 0;

            if (facing.sqrMagnitude < 0.000001f)
                return 0;

            float sqr = radius * radius;
            float cosLimit = Mathf.Cos(Mathf.Clamp(halfAngle, 0f, 180f) *
                                       Mathf.Deg2Rad);
            Vector2 f = facing.normalized;
            int taken = 0;

            for (int i = _mine.Count - 1; i >= 0; i--)
            {
                if (max > 0 && taken >= max)
                    break;

                Component p = _mine[i];

                if (p == null)
                {
                    _mine.RemoveAt(i);
                    continue;
                }

                Vector2 to = (Vector2)p.transform.position - pos;

                if (to.sqrMagnitude > sqr)
                    continue;

                if (to.sqrMagnitude > 0.000001f &&
                    Vector2.Dot(f, to.normalized) < cosLimit)
                    continue;

                _mine.RemoveAt(i);
                into.Add(p);
                taken++;
            }

            return taken;
        }

        private static void PruneMine()
        {
            for (int i = _mine.Count - 1; i >= 0; i--)
                if (_mine[i] == null)
                    _mine.RemoveAt(i);
        }

        // The player's own shots near a point.
        //
        // DELIBERATELY NON-DESTRUCTIVE, unlike CollectNear and DestroyNear. A
        // catalyst node inspects your bullets and usually hands them straight
        // back - it is not taking ownership of them the way a deflector takes
        // ownership of enemy fire, and quietly removing a live player bullet
        // from tracking would break every later node.
        //
        // `exclude` skips one component, which is how a node avoids triggering
        // on itself.
        public static int MineNear(
            Vector2 pos, float radius, Component exclude, List<Component> into)
        {
            if (into == null || _mine.Count == 0)
                return 0;

            float sqr = radius * radius;
            int found = 0;

            for (int i = _mine.Count - 1; i >= 0; i--)
            {
                Component p = _mine[i];

                if (p == null)
                {
                    _mine.RemoveAt(i);
                    continue;
                }

                if (ReferenceEquals(p, exclude))
                    continue;

                if (((Vector2)p.transform.position - pos).sqrMagnitude <= sqr)
                {
                    into.Add(p);
                    found++;
                }
            }

            return found;
        }

        // Every tracked enemy shot within `radius` of `pos`, NON-destructively.
        // For drone modes' INTERCEPTOR, which only looks: it picks which shots
        // to stand in front of, and the game's own hit test does the blocking
        // when a body is there (concept 4.1). Removing them from the list here
        // would hide them from every deflector and shield in the room.
        public static int EnemyNear(Vector2 pos, float radius, List<Component> into)
        {
            if (into == null || _live.Count == 0)
                return 0;

            float sqr = radius * radius;
            int found = 0;

            for (int i = _live.Count - 1; i >= 0; i--)
            {
                Component p = _live[i];

                if (p == null)
                {
                    _live.RemoveAt(i);
                    continue;
                }

                if (((Vector2)p.transform.position - pos).sqrMagnitude <= sqr)
                {
                    into.Add(p);
                    found++;
                }
            }

            return found;
        }

        public static void Forget(Component projectile)
        {
            if (projectile != null)
                _mine.Remove(projectile);
        }

        public static void Reset()
        {
            _live.Clear();
            _mine.Clear();
        }

        [HarmonyPatch(typeof(Projectile), "Shoot")]
        public class OnShoot
        {
            static void Postfix(Projectile __instance)
            {
                try
                {
                    Register(__instance);
                }
                catch { }
            }
        }

        // Enemy missiles and lobbed shots are PhysicsProjectile, an unrelated
        // class - without this hook a deflector simply never sees them, which
        // reads as "it ignores missiles".
        [HarmonyPatch(typeof(PhysicsProjectile), "Shoot")]
        public class OnShootPhysics
        {
            static void Postfix(PhysicsProjectile __instance)
            {
                try
                {
                    // FireSingle calls Shoot() twice on a lobbed shot; without
                    // this the same missile goes into the list twice and a
                    // deflector could "collect" one shot as two.
                    if (ForgePhysicsShot.NotReady(__instance))
                        return;

                    Register(__instance);
                }
                catch { }
            }
        }
    }
}
