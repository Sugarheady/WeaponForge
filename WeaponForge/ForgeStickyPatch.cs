using System;
using System.Collections.Generic;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace WeaponForge
{
    // The two moments a sticky bomb needs the game for: landing, and the
    // trigger pull that sets the salvo off. Everything between those is
    // ForgeStickyController, driven by Unity.
    //
    //   Projectile.OnObjectHit         postfix - a plain shot has landed.
    //   PhysicsProjectile.OnCollisionEnter2D postfix - a lobbed one has.
    //   Shooter.Shoot                  prefix  - refire = detonate.
    //
    // Both landing hooks are POSTFIXES and only safe because the builder has
    // already switched impactBehaviour OFF for a sticky weapon. With it on,
    // either shot destroys itself on contact before a postfix could run - and
    // that destruction is the entire reason a stock shot cannot stick.
    public static class ForgeStickyPatch
    {
        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("WeaponForge.Sticky");

        // Reused rather than allocated per detonation - this runs from a
        // trigger pull, which can be sixty times a second on a held weapon.
        private static readonly List<ForgeSticky> _scratch =
            new List<ForgeSticky>();

        // ---- landing -----------------------------------------------------

        [HarmonyPatch(typeof(Projectile), "OnObjectHit")]
        public class OnPlainHit
        {
            static void Postfix(Projectile __instance, RaycastHit2D hit)
            {
                if (__instance == null || hit.collider == null)
                    return;

                var s = __instance.GetComponent<ForgeSticky>();

                if (s == null || s.stuck)
                    return;

                try
                {
                    if (!s.Wants(hit.collider.gameObject.layer))
                        return;

                    // A plain shot parks by simply not moving. OnObjectHit has
                    // already snapped it onto the surface, so zeroing the
                    // velocity leaves it exactly where it landed - and stops
                    // the CircleCast re-hitting the same wall every frame.
                    __instance.Velocity = Vector2.zero;

                    Stick(s, __instance.Owner, __instance.gameObject,
                          hit.collider, hit.point);

                    // Its own contact damage has to go. A parked shot
                    // re-enters the game's hit path on the piercing repeat
                    // delay forever, so without this the "bomb" quietly grinds
                    // whatever it landed on to death and never needs to blow.
                    Damage d = __instance.Damage;
                    d.amount = 0f;
                    __instance.Damage = d;
                }
                catch (Exception e)
                {
                    Log.LogError("Sticky landing failed: " + e);
                }
            }
        }

        [HarmonyPatch(typeof(PhysicsProjectile), "OnCollisionEnter2D")]
        public class OnPhysicsHit
        {
            static void Postfix(
                PhysicsProjectile __instance, Collision2D collision)
            {
                if (__instance == null || collision == null)
                    return;

                var s = __instance.GetComponent<ForgeSticky>();

                if (s == null || s.stuck)
                    return;

                try
                {
                    if (collision.collider == null ||
                        !s.Wants(collision.collider.gameObject.layer))
                    {
                        return;
                    }

                    Vector2 where = (collision.contactCount > 0)
                        ? collision.GetContact(0).point
                        : (Vector2)__instance.transform.position;

                    // Take the physics away entirely. simulated=false rather
                    // than a kinematic body or a zeroed velocity: it stops
                    // gravity, stops it being shoved, and stops it generating
                    // any further contacts - so a bomb stuck to an enemy can
                    // never push that enemy around.
                    var rb = collision.otherRigidbody != null
                        ? collision.otherRigidbody
                        : __instance.GetComponentInParent<Rigidbody2D>();

                    if (rb != null)
                    {
                        rb.linearVelocity = Vector2.zero;
                        rb.angularVelocity = 0f;
                        rb.simulated = false;
                    }

                    Stick(s, __instance.Owner, __instance.gameObject,
                          collision.collider, where);

                    Damage d = __instance.Damage;
                    d.amount = 0f;
                    __instance.Damage = d;
                }
                catch (Exception e)
                {
                    Log.LogError("Sticky landing failed: " + e);
                }
            }
        }

        private static void Stick(
            ForgeSticky s, Unit owner, GameObject go,
            Collider2D on, Vector2 point)
        {
            s.stuck = true;
            s.stuckAt = Time.time;
            s.owner = owner;
            s.blowAt = -1f;

            // Ride whatever it landed on, if that thing can move. Terrain has
            // no rigidbody, so a wall bomb simply stays put.
            if (s.follow && on != null && on.attachedRigidbody != null)
            {
                s.host = on.transform;
                s.hostOffset = s.host.InverseTransformPoint(go.transform.position);
            }

            s.Register();

            var ctrl = go.GetComponent<ForgeStickyController>();

            if (ctrl == null)
                ctrl = go.AddComponent<ForgeStickyController>();

            ctrl.Begin(s);

            Cull(s, owner);
        }

        // Keep one owner's field of bombs to `max`, oldest first. Detonating
        // the oldest rather than refusing the shot: a weapon that silently
        // stops working is worse than one that tidies up after itself, and it
        // also gives an unattended Refire weapon a natural ceiling.
        private static void Cull(ForgeSticky s, Unit owner)
        {
            if (s.max <= 0)
                return;

            ForgeSticky.Collect(owner, _scratch);

            int over = _scratch.Count - s.max;

            for (int i = 0; i < _scratch.Count && over > 0; i++)
            {
                if (ReferenceEquals(_scratch[i], s))
                    continue;   // never cull the one that just landed

                ForgeStickyController.Detonate(_scratch[i]);
                over--;
            }
        }

        // ---- the trigger -------------------------------------------------

        [HarmonyPatch(typeof(Shooter), "Shoot")]
        public class OnShoot
        {
            static bool Prefix(Shooter __instance)
            {
                if (ForgeSticky.Live.Count == 0)
                    return true;

                try
                {
                    WeaponBase weapon = __instance.Weapon;

                    if (weapon == null)
                        return true;

                    ForgeStickySpec.Spec spec;

                    if (!ForgeStickySpec.TryGet(weapon.TemplateData, out spec))
                        return true;

                    if (spec.trigger != ForgeSticky.Trigger.Refire)
                        return true;

                    ForgeSticky.Collect(__instance.Unit, _scratch);

                    // Below the limit: this press lays another one.
                    if (_scratch.Count < spec.max)
                        return true;

                    // At the limit: this press is the detonator, and it does
                    // NOT also fire. That is the whole rhythm of the weapon -
                    // load, load, load, bang - and firing as well would mean
                    // the field never actually empties.
                    int n = ForgeStickyController.DetonateAll(
                        __instance.Unit, _scratch);

                    return n == 0;
                }
                catch (Exception e)
                {
                    Log.LogError("Sticky trigger failed: " + e);
                    return true;
                }
            }
        }
    }
}
