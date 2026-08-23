using System;
using System.Collections.Generic;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace WeaponForge
{
    // The split triggers that need a hook into the game rather than a Unity
    // callback: "hit" and "bounce". ("death", "time" and "distance" are driven
    // by ForgeSplitter itself.)
    //
    // Every one of these is a one-liner into ForgeSplit.Fire, which is the
    // point: a future feature that wants its own trigger adds a call and a
    // name, and nothing in ForgeSplit changes.
    public static class ForgeSplitPatch
    {
        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("WeaponForge.Split");

        // Projectile.TryHit is the right hook for "hit an enemy": it is called
        // once per LISTENER rather than once per frame of an overlap, and the
        // game's own re-hit delay is tested inside it. OnObjectHit would fire
        // every FixedUpdate a piercing shot spent inside someone and burn the
        // whole budget in a single enemy.
        //
        // The re-hit test has to be mirrored rather than assumed, because
        // TryHit returns early when it fails and a postfix cannot tell. It is
        // exactly CanDamage: a private Dictionary<IProjectileListener, float>
        // of last damage times against PiercingData.damageRepeatDelay.
        [HarmonyPatch(typeof(Projectile), "TryHit")]
        public class OnTryHit
        {
            private static AccessTools.FieldRef<
                Projectile, Dictionary<IProjectileListener, float>> _times;

            static void Prefix(Projectile __instance, IProjectileListener listener)
            {
                if (!ForgeSplit.Any || __instance == null || listener == null)
                    return;

                try
                {
                    if (!WouldDamage(__instance, listener))
                        return;

                    ForgeSplit.Fire(__instance, ForgeSplit.OnHit);
                }
                catch (Exception e)
                {
                    Log.LogError("Split on hit failed: " + e);
                }
            }

            private static bool WouldDamage(
                Projectile p, IProjectileListener listener)
            {
                if (_times == null)
                {
                    _times = AccessTools.FieldRefAccess<
                        Projectile, Dictionary<IProjectileListener, float>>(
                            "lastDamageTimes");
                }

                Dictionary<IProjectileListener, float> times = _times(p);

                if (times == null)
                    return true;

                float last;

                if (!times.TryGetValue(listener, out last))
                    return true;

                return Time.time - last > p.PiercingData.damageRepeatDelay;
            }
        }

        // A plain projectile's scripted bounce. Reflect is public and is called
        // from exactly one place - the bounce branch of OnObjectHit - so this
        // is precisely "it just bounced".
        [HarmonyPatch(typeof(Projectile), "Reflect")]
        public class OnReflect
        {
            static void Postfix(Projectile __instance)
            {
                if (!ForgeSplit.Any || __instance == null)
                    return;

                try
                {
                    ForgeSplit.Fire(__instance, ForgeSplit.OnBounce);
                }
                catch (Exception e)
                {
                    Log.LogError("Split on bounce failed: " + e);
                }
            }
        }

        // A lobbed shot's contact. There is no TryHit on this class - a
        // PhysicsProjectile resolves collisions through the physics engine - so
        // the collision itself is the hit.
        //
        // A lobbed shot that is NOT bouncing normally dies on this contact, and
        // its "death" trigger fires a moment later; that is why "hit" and
        // "death" together are documented as giving one burst each rather than
        // being treated as a mistake.
        [HarmonyPatch(typeof(PhysicsProjectile), "OnCollisionEnter2D")]
        public class OnPhysicsCollision
        {
            static void Postfix(PhysicsProjectile __instance, Collision2D collision)
            {
                if (!ForgeSplit.Any || __instance == null || collision == null)
                    return;

                try
                {
                    // Terrain is a bounce, not a hit. The bounce trigger has
                    // its own route (ForgeBouncePatch), so anything without a
                    // unit or a health component on it is left alone here.
                    if (collision.collider == null)
                        return;

                    if (collision.collider.GetComponentInParent<HealthBase>() == null)
                        return;

                    ForgeSplit.Fire(__instance, ForgeSplit.OnHit);
                }
                catch (Exception e)
                {
                    Log.LogError("Split on lobbed hit failed: " + e);
                }
            }
        }
    }
}
