using System;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace WeaponForge
{
    // Drives ForgeGrowth.
    //
    //   Projectile.Shoot       postfix -> capture the shot's normal size
    //   Projectile.FixedUpdate prefix  -> resize art, hitbox and damage
    //
    // A prefix again, and for the usual reason: Projectile.FixedUpdate builds
    // this frame's CircleCast from Radius further down the same method, so
    // setting Radius first means the sweep already uses the new size. Do it in
    // a postfix and the hitbox would trail the art by a frame.
    public static class ForgeGrowthPatch
    {
        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("WeaponForge.Growth");

        [HarmonyPatch(typeof(Projectile), "Shoot")]
        public class OnShoot
        {
            static void Postfix(Projectile __instance)
            {
                var g = __instance.GetComponent<ForgeGrowth>();

                if (g == null)
                    return;

                g.shot = true;
                g.startTime = Time.time;
                g.origin = __instance.transform.position;

                // The shot's NORMAL size, whatever produced it - the prefab,
                // projectileScale, projectileRadius. Everything below is
                // measured from these, so growth composes with them instead of
                // fighting them.
                g.baseScale = __instance.transform.localScale;
                g.baseRadius = __instance.Radius;
                g.baseDamage = __instance.Damage.amount;

                g.resolvedSpan = g.span;

                if (g.resolvedSpan <= 0f)
                {
                    // Borrow the weapon's own reach so the shot peaks exactly
                    // as it expires. RangeData is on the instance and is set
                    // before Shoot runs.
                    ProjectileRangeData rd = __instance.RangeData;

                    if (!g.overTime && rd.enabled && rd.range > 0f)
                        g.resolvedSpan = rd.range;
                    else if (g.overTime && __instance.LifetimeData.enabled &&
                             __instance.LifetimeData.time > 0f)
                        g.resolvedSpan = __instance.LifetimeData.time;
                    else
                        g.resolvedSpan = g.overTime ? 2f : 10f;
                }

                Apply(__instance, g);
            }
        }

        [HarmonyPatch(typeof(Projectile), "FixedUpdate")]
        public class OnFixedUpdate
        {
            static void Prefix(Projectile __instance)
            {
                var g = __instance.GetComponent<ForgeGrowth>();

                if (g == null || !g.shot)
                    return;

                try
                {
                    Apply(__instance, g);
                }
                catch (Exception e)
                {
                    g.shot = false;
                    Log.LogError(
                        "Growth failed, shot keeps its size: " + e);
                }
            }
        }

        // ---- lobbed / usePhysics shots -------------------------------------
        //
        // A PhysicsProjectile is a different class with a different collision
        // model, so it needs its own hooks - but the growth MATH is identical,
        // and both sides share Progress()/ApplyDamage() below.
        //
        // It is also simpler in one important way: a PhysicsProjectile has no
        // Radius. It collides with a REAL Collider2D driven by a Rigidbody2D,
        // and a real collider scales with its transform - so growing the art
        // grows the true hitbox for free, with nothing to keep in sync.

        [HarmonyPatch(typeof(PhysicsProjectile), "Shoot")]
        public class OnShootPhysics
        {
            static void Postfix(PhysicsProjectile __instance)
            {
                var g = __instance.GetComponent<ForgeGrowth>();

                if (g == null)
                    return;

                g.shot = true;
                g.startTime = Time.time;
                g.origin = __instance.transform.position;
                g.baseScale = __instance.transform.localScale;
                g.baseRadius = 0f;              // no Radius on this class
                g.baseDamage = __instance.Damage.amount;

                g.resolvedSpan = g.span;

                if (g.resolvedSpan <= 0f)
                {
                    ProjectileRangeData rd = __instance.RangeData;

                    if (!g.overTime && rd.enabled && rd.range > 0f)
                        g.resolvedSpan = rd.range;
                    else if (g.overTime && __instance.LifetimeData.enabled &&
                             __instance.LifetimeData.time > 0f)
                        g.resolvedSpan = __instance.LifetimeData.time;
                    else
                        g.resolvedSpan = g.overTime ? 2f : 10f;
                }

                ApplyPhysics(__instance, g);
            }
        }

        [HarmonyPatch(typeof(PhysicsProjectile), "FixedUpdate")]
        public class OnFixedUpdatePhysics
        {
            static void Prefix(PhysicsProjectile __instance)
            {
                var g = __instance.GetComponent<ForgeGrowth>();

                if (g == null || !g.shot)
                    return;

                try
                {
                    ApplyPhysics(__instance, g);
                }
                catch (Exception e)
                {
                    g.shot = false;
                    Log.LogError(
                        "Growth failed, shot keeps its size: " + e);
                }
            }
        }

        private static void ApplyPhysics(PhysicsProjectile p, ForgeGrowth g)
        {
            float e;
            float scale = Progress(g, p.transform.position, out e);

            // Scaling the transform scales the Collider2D with it, so the
            // hitbox follows whether or not "hitbox" was asked for - there is
            // no separate radius to leave behind. A visual-only grow is not
            // possible on this class; the builder warns instead of pretending.
            p.transform.localScale = g.baseScale * scale;

            if (g.damageAtFull != 1f)
            {
                Damage d = p.Damage;
                d.amount =
                    g.baseDamage * Mathf.LerpUnclamped(1f, g.damageAtFull, e);
                p.Damage = d;
            }
        }

        private static void Apply(Projectile p, ForgeGrowth g)
        {
            float e;
            float scale = Progress(g, p.transform.position, out e);

            p.transform.localScale = g.baseScale * scale;

            if (g.hitbox)
                p.Radius = g.baseRadius * scale;

            if (g.damageAtFull != 1f)
            {
                Damage d = p.Damage;
                d.amount =
                    g.baseDamage * Mathf.LerpUnclamped(1f, g.damageAtFull, e);
                p.Damage = d;
            }
        }

        // How far along the growth is, and the eased 0..1 that produced it.
        private static float Progress(
            ForgeGrowth g, Vector2 here, out float eased)
        {
            float span = (g.resolvedSpan > 0f) ? g.resolvedSpan : 1f;

            float t = g.overTime
                ? (Time.time - g.startTime) / span
                : Vector2.Distance(g.origin, here) / span;

            if (g.clamp)
                t = Mathf.Clamp01(t);
            else
                t = Mathf.Max(0f, t);

            // Pow only when asked: it is the difference between "swells
            // steadily" and "stays small then blows up at the last moment".
            eased = (g.curve == 1f) ? t : Mathf.Pow(t, g.curve);

            // Unclamped so "clamp: false" really can carry on past "to".
            return Mathf.LerpUnclamped(g.from, g.to, eased);
        }
    }
}
