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

        // Component, not Projectile: the game has TWO projectile classes and
        // enemies use both. Straight shots are Projectile; lobbed shots and
        // missiles are PhysicsProjectile, a separate class that shares no base
        // with it. Both are Components and both implement IProjectile, which is
        // all a caller needs - position and gameObject from the Component,
        // Velocity/Owner/Damage from the interface.
        private static readonly List<Component> _live = new List<Component>();
        private static int _enemyLayer = -2;

        private static int EnemyLayer
        {
            get
            {
                if (_enemyLayer == -2)
                    _enemyLayer = LayerMask.NameToLayer("EnemyProjectiles");
                return _enemyLayer;
            }
        }

        public static void Register(Component projectile)
        {
            if (!Enabled || projectile == null)
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

        public static void Reset()
        {
            _live.Clear();
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
                    Register(__instance);
                }
                catch { }
            }
        }
    }
}
