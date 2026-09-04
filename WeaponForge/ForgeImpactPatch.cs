using System;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace WeaponForge
{
    // Plays a weapon's impact sound at the point of contact, for all three
    // ways this game can hit something.
    //
    // Note this LAYERS over something that already exists: all 37 enemy Unit
    // prefabs wire HealthBase.onDamage to an SfxPlayer, so an enemy already
    // makes a noise when damaged. That is wired in the prefab as a UnityEvent,
    // not called from C#, which is why it appears at no PlaySfx call site.
    // What the game has no sound for is hitting TERRAIN - and the enemy's
    // sound belongs to the enemy, so every weapon hitting a Fly sounds
    // identical. ForgeImpact.Where lets a file target either case.
    //
    // Each hook is the narrowest place that still catches both enemies and
    // walls:
    //
    //   Projectile.OnObjectHit      the single entry point a plain shot's
    //                               FixedUpdate CircleCast funnels into.
    //   PhysicsProjectile.Impact    the lobbed equivalent, off a real
    //                               Unity collision.
    //   HitscanWeapon.FireSingle    a beam has no projectile at all, so
    //                               the shot itself is the event.
    //
    // Everything is guarded and swallowed: an impact sound must never be
    // able to stop a projectile from resolving its hit.
    public static class ForgeImpactPatch
    {
        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("WeaponForge");

        // ---- plain projectiles -----------------------------------------
        //
        // OnObjectHit runs at most once per FixedUpdate (one CircleCast, one
        // hit), so this does not multi-fire across colliders. It CAN repeat
        // frame after frame while a piercing shot overlaps something, which
        // is what ForgeImpact.minInterval exists to swallow.
        [HarmonyPatch(typeof(Projectile), "OnObjectHit")]
        public class OnProjectileHit
        {
            static void Prefix(Projectile __instance, RaycastHit2D hit)
            {
                try
                {
                    var impact = __instance.GetComponent<ForgeImpact>();

                    if (impact == null)
                        return;

                    // Filter BEFORE Ready(), which stamps the cooldown as a
                    // side effect - asking about a hit we do not want would
                    // silence the next one we do.
                    if (hit.collider != null &&
                        !impact.Wants(hit.collider.gameObject.layer))
                    {
                        return;
                    }

                    if (!impact.Ready())
                        return;

                    // hit.point is zero when the cast started already inside
                    // the collider; the shot's own position is the honest
                    // fallback and is never far off.
                    Vector2 at = (hit.point == Vector2.zero)
                        ? (Vector2)__instance.transform.position
                        : hit.point;

                    impact.PlayAt(at);
                }
                catch (Exception e)
                {
                    Log.LogError("Impact sound failed: " + e);
                }
            }
        }

        // ---- lobbed / physics shots ------------------------------------
        [HarmonyPatch(typeof(PhysicsProjectile), "Impact")]
        public class OnPhysicsImpact
        {
            static void Prefix(
                PhysicsProjectile __instance,
                Collider2D collider,
                Vector2 position)
            {
                try
                {
                    var impact = __instance.GetComponent<ForgeImpact>();

                    if (impact == null)
                        return;

                    if (collider != null &&
                        !impact.Wants(collider.gameObject.layer))
                    {
                        return;
                    }

                    if (!impact.Ready())
                        return;

                    impact.PlayAt(position);
                }
                catch (Exception e)
                {
                    Log.LogError("Impact sound failed: " + e);
                }
            }
        }

        // ---- hitscan / beams -------------------------------------------
        //
        // FireSingle keeps its hit in a local, so the cast is repeated here
        // to find out WHERE to play. One extra CircleCast per shot, and only
        // for weapons that asked for an impact sound.
        //
        // The cast comes BEFORE the cooldown check on purpose: Ready() stamps
        // the next allowed time as a side effect, so asking it about a shot
        // that turns out to have hit nothing would swallow the cooldown and
        // silence the next shot that DID connect.
        [HarmonyPatch(typeof(HitscanWeapon), "FireSingle")]
        public class OnHitscanFire
        {
            static void Postfix(
                HitscanWeapon __instance, Vector2 position, Vector2 direction)
            {
                try
                {
                    HitscanWeaponVisual visual = __instance.Visual;

                    if (visual == null)
                        return;

                    // A CURVE BEAM plays this itself, at the end of the actual
                    // curve. This postfix finds its position by re-casting the
                    // straight ray, which on a bent beam is a point the beam
                    // never visited - so the sound would land through a wall
                    // the beam went around. Note the prefix that suppresses the
                    // straight cast does NOT stop this method running: a
                    // Harmony postfix still runs when a prefix returns false.
                    if (ForgeCurveBeam.Owns(__instance.TemplateData))
                        return;

                    var impact = visual.GetComponent<ForgeImpact>();

                    if (impact == null)
                        return;

                    RaycastHit2D hit = Physics2D.CircleCast(
                        position,
                        __instance.RayWidth,
                        direction,
                        __instance.Range,
                        __instance.LayerMask);

                    if (hit.collider == null)
                        return;   // the beam hit nothing - no impact

                    if (!impact.Wants(hit.collider.gameObject.layer))
                        return;

                    if (!impact.Ready())
                        return;

                    impact.PlayAt(hit.point);
                }
                catch (Exception e)
                {
                    Log.LogError("Impact sound failed: " + e);
                }
            }
        }
    }
}
