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

        // PhysicsProjectile.targetPosition - private, and the thing its Homing()
        // steers toward. Plain FieldInfo: it is a Vector2, but the habit of
        // avoiding FieldRefAccess on value types is what keeps this from
        // becoming the next phasing bug.
        private static FieldInfo _targetPosField;
        private static bool _targetPosLooked;

        private static void SetTargetPosition(
            PhysicsProjectile phys, Vector2 where)
        {
            if (!_targetPosLooked)
            {
                _targetPosLooked = true;
                _targetPosField = AccessTools.Field(
                    typeof(PhysicsProjectile), "targetPosition");

                if (_targetPosField == null)
                {
                    Log.LogWarning(
                        "PhysicsProjectile.targetPosition not found - " +
                        "reflected missiles will steer back toward whoever " +
                        "they were originally aimed at. Straight shots are " +
                        "unaffected.");
                }
            }

            if (_targetPosField != null)
                _targetPosField.SetValue(phys, where);
        }

        // collisionLayerMask is private AND typed LayerMask, not int. Plain
        // FieldInfo rather than Harmony's FieldRefAccess<T,F>, which throws on
        // exactly that kind of value-type mismatch (it is what broke phasing
        // once already).
        private static FieldInfo _maskField;
        private static bool _maskLooked;

        private static int _playerLayer = -2;
        private static int _enemyLayer = -2;

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
        // Takes a Component because enemies fire BOTH projectile classes:
        // straight shots are Projectile, missiles and lobbed shots are
        // PhysicsProjectile. Everything this needs - Velocity, Owner, Damage -
        // is on IProjectile, which both implement; the handful of genuine
        // differences are called out inline.
        private static void Reflect(
            Component bullet, Projectile source, ForgeDeflect d)
        {
            try
            {
                EnsureLayers();

                var ip = bullet as IProjectile;

                if (ip == null)
                    return;

                var phys = bullet as PhysicsProjectile;

                float speed = ip.Velocity.magnitude;

                if (speed <= 0.01f)
                    speed = 10f;

                speed *= Mathf.Max(0.05f, d.speedMultiplier);

                Vector2 dir = -ip.Velocity.normalized;

                AimAssistTarget aimed = null;

                if (d.aim == 1)
                {
                    // Same target-finder homing and ricochet use, so all three
                    // agree on what counts as a target and none of them can
                    // pick terrain or your own side.
                    int mask =
                        (_playerLayer >= 0)
                            ? Physics2D.GetLayerCollisionMask(_playerLayer)
                            : Physics2D.GetLayerCollisionMask(
                                bullet.gameObject.layer);

                    AimAssistTarget found =
                        ForgeHomingPatch.FindTarget(
                            source, bullet.transform.position, dir,
                            d.aimRange, 180f, mask);

                    if (found != null)
                    {
                        Vector2 to =
                            (Vector2)found.transform.position -
                            (Vector2)bullet.transform.position;

                        if (to.sqrMagnitude > 0.000001f)
                        {
                            dir = to.normalized;
                            aimed = found;
                        }
                    }
                }

                ip.Velocity = dir * speed;

                // Face the new heading, or a directional sprite flies
                // backwards for the rest of its life.
                bullet.transform.rotation =
                    Quaternion.FromToRotation(Vector3.right, (Vector3)dir);

                // A missile keeps STEERING after it is turned round, and that
                // steering ignores the velocity we just set:
                // PhysicsProjectile.Homing() runs every FixedUpdate - it is NOT
                // gated on HomingData.enabled - and torques the body toward a
                // private targetPosition while thrusting along transform.right.
                // That target is still the player, so a reflected missile would
                // calmly curve back into us. Repoint it, or destroy nothing and
                // achieve nothing.
                if (phys != null)
                {
                    Vector2 aimAt = (aimed != null)
                        ? (Vector2)aimed.transform.position
                        : (Vector2)bullet.transform.position + dir * 10000f;

                    // Keep Target in sync too: Update() re-reads it every frame
                    // while homing is on and would otherwise overwrite the
                    // repoint on the very next tick.
                    phys.Target = (aimed != null) ? aimed.gameObject : null;

                    SetTargetPosition(phys, aimAt);
                }

                // Whose side it is on. The layer is the faction.
                if (_playerLayer >= 0 && _enemyLayer >= 0)
                {
                    VisualCustomizer.RemapLayer(
                        bullet.gameObject, _enemyLayer, _playerLayer);

                    // Only the straight-projectile class bakes a private
                    // collision mask at fire time. A PhysicsProjectile collides
                    // through real colliders and Unity's own layer matrix, so
                    // remapping the layer is the whole job there - there is no
                    // mask to rewrite, and looking for one would throw.
                    if (phys == null)
                    {
                        SetMask(
                            (Projectile)bullet,
                            Physics2D.GetLayerCollisionMask(_playerLayer));
                    }
                }

                // So the game's friendly-fire check treats it as ours - and,
                // usefully, so a kill with a reflected bullet is credited to
                // the player.
                if (source.Owner != null)
                    ip.Owner = source.Owner;

                if (d.damageMultiplier != 1f)
                {
                    Damage dmg = ip.Damage;
                    dmg.amount *= d.damageMultiplier;
                    ip.Damage = dmg;
                }
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

        private static void EnsureLayers()
        {
            if (_playerLayer == -2)
                _playerLayer = LayerMask.NameToLayer("PlayerProjectiles");

            if (_enemyLayer == -2)
                _enemyLayer = LayerMask.NameToLayer("EnemyProjectiles");
        }

        private static void SetMask(Projectile p, LayerMask mask)
        {
            if (!_maskLooked)
            {
                _maskLooked = true;
                _maskField =
                    typeof(Projectile).GetField(
                        "collisionLayerMask",
                        BindingFlags.NonPublic | BindingFlags.Instance);
            }

            if (_maskField == null)
                return;

            // Boxed as LayerMask, matching the field's real type.
            _maskField.SetValue(p, mask);
        }
    }
}
