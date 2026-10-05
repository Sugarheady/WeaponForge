using System;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace WeaponForge
{
    // THE END-OF-FLIGHT HOOKS A BLOOM FIRES ITSELF, in both modes.
    //
    // A shot that bloom ends - a bubble at its pop, a bullet at the centre or
    // at the end of its reach - never reaches the engine's own HandleRange,
    // HandleLifetime or OnObjectHit, because bloom destroys it first. So the
    // spawn / explode / sub-emitter / discharge hooks those three would have
    // fired are fired here, in the order the game fires them.
    //
    // ★ THE EXPLOSION GOES THROUGH THE SHOT'S OWN (private) SpawnExplosion.
    //
    // Until 2026-10-02 the bubble called ExplosionManager directly. That makes
    // the same blast, owned by the same unit, so the KILL was still yours -
    // but ForgeKillsPatch learns WHICH WEAPON exploded only while the
    // projectile's own SpawnExplosion is on the stack (an Explosion is a struct
    // with no route back to a weapon). So a bloom's explosion kills paid no
    // per-weapon onKill reward and no rank XP, with nothing in the log.
    // Reflection reaches the patched method, so the credit comes free; the
    // manager is kept as the fallback, and saying so is once per run.
    public static class ForgeBloomHooks
    {
        private static readonly ManualLogSource Log =
            ForgeLog.Source("WeaponForge.Bloom");

        private static MethodInfo _plainBoom;
        private static MethodInfo _physicsBoom;
        private static bool _looked;
        private static bool _saidBoomFallback;

        private static void Look()
        {
            if (_looked)
                return;

            // Latched BEFORE the lookups that can throw, so a failure is
            // looked for once rather than every pop.
            _looked = true;

            try
            {
                _plainBoom = AccessTools.Method(
                    typeof(Projectile), "SpawnExplosion",
                    new[] { typeof(Vector2) });
            }
            catch (Exception)
            {
                _plainBoom = null;
            }

            try
            {
                _physicsBoom = AccessTools.Method(
                    typeof(PhysicsProjectile), "SpawnExplosion",
                    Type.EmptyTypes);
            }
            catch (Exception)
            {
                _physicsBoom = null;
            }
        }

        // `shot` is the Projectile or PhysicsProjectile itself.
        public static void Explode(Component shot, IProjectile ip, Vector2 at)
        {
            if (ip == null)
                return;

            Look();

            try
            {
                var plain = shot as Projectile;

                if (plain != null && _plainBoom != null)
                {
                    _plainBoom.Invoke(plain, new object[] { at });
                    return;
                }

                // A physics shot explodes at its own position, which is where
                // the caller has just brought it.
                var physics = shot as PhysicsProjectile;

                if (physics != null && _physicsBoom != null)
                {
                    _physicsBoom.Invoke(physics, null);
                    return;
                }
            }
            catch (Exception e)
            {
                if (!_saidBoomFallback)
                {
                    _saidBoomFallback = true;

                    Log.LogWarning(
                        "bloom: the shot's own SpawnExplosion could not be " +
                        "called (" + e.GetType().Name + "), so the explosion " +
                        "is made directly. The kill is still yours; only the " +
                        "per-weapon credit (onKill rewards, rank XP) is lost " +
                        "for those kills. Said once per run.");
                }
            }

            Explosion ex = ip.Explosion;
            ex.Owner = ip.Owner;
            ex.Burn = ip.Burn;

            ExplosionManager em;

            if (ServiceLocator.TryGet<ExplosionManager>(out em) && em != null)
                em.SpawnExplosion(at, ex);
        }

        // Same shape as the game's own FireSub, and Vector2.up for the same
        // reason: a sub-emitter has no barrel and no heading of its own.
        public static void Sub(IProjectile ip, Vector2 at)
        {
            if (ip == null || ip.SubEmitters == null)
                return;

            try
            {
                foreach (WeaponBase sub in ip.SubEmitters)
                {
                    if (sub != null)
                        sub.Fire(new FakeBarrel(at, Vector2.up)).Forget();
                }
            }
            catch { }
        }

        public static void Discharge(IProjectile ip, Vector2 at)
        {
            ElectricityManager elec;

            if (ip != null &&
                ServiceLocator.TryGet<ElectricityManager>(out elec) &&
                elec != null)
            {
                elec.SpawnDischarge(ip.DischargeData, at);
            }
        }

        // THE FLIGHT RAN OUT HERE: what HandleRange and HandleLifetime would
        // have fired, deduped the way the bubble's pop dedupes them (an effect
        // and an explosion at most once each, even if both blocks ask).
        //
        // `range` and `life` are the COPIES taken when the shot was placed -
        // bloom switches the live ones off, so the live ones say nothing.
        // HandleRange fires its hooks only when `destroyWhenReached` is on, and
        // that gate is kept.
        public static void FlightEnd(
            Component shot, IProjectile ip,
            ProjectileRangeData range, ProjectileLifetimeData life,
            Vector2 at, Quaternion rot)
        {
            bool r = range.enabled && range.destroyWhenReached;
            bool l = life.enabled;

            GameObject fx = (l && life.spawnOnTimeDown != null)
                ? life.spawnOnTimeDown
                : (r ? range.destroyEffect : null);

            if (fx != null)
                UnityEngine.Object.Instantiate(fx, at, rot);

            if ((l && life.spawnExplosion) || (r && range.spawnExplosion))
                Explode(shot, ip, at);

            if ((l && life.fireSub) || (r && range.fireSub))
                Sub(ip, at);

            if (l && life.discharge)
                Discharge(ip, at);
        }

        // IT WENT OFF HERE as if it had hit something: what OnObjectHit (a
        // bullet) or Impact (a rocket) fires past its safety distance. Nothing
        // was hit, so there is no damage here beyond what an explosion does.
        public static void Impact(
            Component shot, IProjectile ip, ProjectileImpactBehaviour impact,
            Vector2 at, Vector2 heading)
        {
            if (!impact.enabled)
                return;

            if (impact.destroyEffect != null)
            {
                Vector2 v = (heading.sqrMagnitude > 0.000001f)
                    ? heading : Vector2.right;

                UnityEngine.Object.Instantiate(
                    impact.destroyEffect, at,
                    Quaternion.FromToRotation(Vector3.right, v));
            }

            if (impact.spawnExplosion)
                Explode(shot, ip, at);

            if (impact.fireSub)
                Sub(ip, at);

            if (impact.discharge)
                Discharge(ip, at);
        }
    }
}
