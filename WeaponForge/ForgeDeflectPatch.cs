using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace WeaponForge
{
    // Drives ForgeDeflect: sweep for enemy bullets near the shot and either
    // destroy them or turn them around.
    //
    // Turning one around is more than reversing its velocity. A projectile's
    // FACTION is its GameObject layer, and the game bakes that into a private
    // collisionLayerMask at Shoot time - so a bullet whose layer is changed
    // afterwards would keep colliding with whatever it was originally told to.
    // Both have to move together, plus Owner, or the reflected shot either
    // passes through everything or comes back and hits you.
    public static class ForgeDeflectPatch
    {
        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("WeaponForge.Deflect");

        // Reused so a sweep does not allocate. Component, because enemies fire
        // both Projectile and PhysicsProjectile and these share no base class.
        private static readonly List<Component> _caught =
            new List<Component>(16);

        // The reflection-heavy parts of turning a bullet around - the private
        // collisionLayerMask, PhysicsProjectile's private steering target, and
        // the PlayerProjectiles/EnemyProjectiles layer lookup - all live in
        // ForgeBulletTurn now, shared with `shield`.

        [HarmonyPatch(typeof(Projectile), "Shoot")]
        public class OnShoot
        {
            static void Postfix(Projectile __instance)
            {
                var d = __instance.GetComponent<ForgeDeflect>();

                if (d == null)
                    return;

                d.shot = true;
                d.used = 0;
                d.nextSweep = 0f;
            }
        }

        [HarmonyPatch(typeof(Projectile), "FixedUpdate")]
        public class OnFixedUpdate
        {
            static void Prefix(Projectile __instance)
            {
                var d = __instance.GetComponent<ForgeDeflect>();

                if (d == null || !d.shot)
                    return;

                if (Time.time < d.nextSweep)
                    return;

                d.nextSweep = Time.time + Mathf.Max(0.01f, d.interval);

                try
                {
                    Sweep(__instance, d);
                }
                catch (Exception e)
                {
                    d.shot = false;
                    Log.LogError("Deflect failed, shot flies on: " + e);
                }
            }
        }

        private static void Sweep(Projectile p, ForgeDeflect d)
        {
            if (d.maxTotal > 0 && d.used >= d.maxTotal)
                return;

            int room = (d.maxTotal > 0) ? (d.maxTotal - d.used) : 0;

            _caught.Clear();

            int n = ForgeProjectileTracker.CollectNear(
                p.transform.position, d.radius, room, _caught);

            if (n == 0)
                return;

            d.used += n;

            for (int i = 0; i < _caught.Count; i++)
            {
                Component bullet = _caught[i];

                if (bullet == null)
                    continue;

                if (d.mode == 0)
                {
                    UnityEngine.Object.Destroy(bullet.gameObject);
                    continue;
                }

                Reflect(bullet, p, d);
            }

            _caught.Clear();
        }

        // Turn an enemy bullet into one of ours.
        //
        // The mechanics of that - layer, private collision mask, Owner, and
        // repointing a missile's private steering target - live in
        // ForgeBulletTurn, because `shield`'s held arc has to do exactly the
        // same job and two copies of code that subtle is how you get a bullet
        // that passes through everything or comes back and kills you.
        private static void Reflect(
            Component bullet, Projectile source, ForgeDeflect d)
        {
            try
            {
                // `aim` is a plain int, which is why deflect gets the cursor
                // and mirror modes even though it cannot have a rally: an int
                // serializes onto a prefab component, a config reference does
                // not (it would arrive null on every clone).
                ForgeBulletTurn.Turn(
                    bullet,
                    (source != null) ? source.Owner : null,
                    d.aim,
                    d.aimRange,
                    d.speedMultiplier,
                    d.damageMultiplier,
                    null,
                    (source != null)
                        ? (Vector2)source.transform.position
                        : Vector2.zero);
            }
            catch (Exception e)
            {
                // A bullet we half-converted is worse than one we simply
                // removed.
                Log.LogError(
                    "Could not reflect a shot, destroying it instead: " + e);

                if (bullet != null)
                    UnityEngine.Object.Destroy(bullet.gameObject);
            }
        }

    }
}
