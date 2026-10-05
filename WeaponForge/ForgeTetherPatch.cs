using System;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace WeaponForge
{
    // The one moment a harpoon needs the game: landing. Everything after that
    // is ForgeTetherController, driven by Unity.
    //
    // Both hooks are POSTFIXES, and only safe because the builder has already
    // switched impactBehaviour OFF for a tether weapon - with it on, either
    // shot destroys itself on contact before a postfix could run. Same
    // arrangement, and same reason, as the sticky bombs.
    public static class ForgeTetherPatch
    {
        private static readonly ManualLogSource Log =
            ForgeLog.Source("WeaponForge.Tether");

        [HarmonyPatch(typeof(Projectile), "OnObjectHit")]
        public class OnPlainHit
        {
            static void Postfix(Projectile __instance, RaycastHit2D hit)
            {
                if (__instance == null || hit.collider == null)
                    return;

                var t = __instance.GetComponent<ForgeTether>();

                if (t == null || t.latched)
                    return;

                try
                {
                    if (!t.Wants(hit.collider.gameObject.layer))
                        return;

                    // A plain shot parks by not moving; OnObjectHit has
                    // already snapped it onto the surface.
                    __instance.Velocity = Vector2.zero;

                    Latch(t, __instance.Owner, __instance.gameObject,
                          hit.collider, hit.point);

                    // The head is a rope end from here on, not a bullet. Left
                    // alone it would re-enter the game's hit path on the
                    // piercing repeat delay for as long as the rope lasted.
                    Damage d = __instance.Damage;
                    d.amount = 0f;
                    __instance.Damage = d;
                }
                catch (Exception e)
                {
                    Log.LogError("Harpoon landing failed: " + e);
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

                var t = __instance.GetComponent<ForgeTether>();

                if (t == null || t.latched)
                    return;

                try
                {
                    if (collision.collider == null ||
                        !t.Wants(collision.collider.gameObject.layer))
                    {
                        return;
                    }

                    Vector2 where = (collision.contactCount > 0)
                        ? collision.GetContact(0).point
                        : (Vector2)__instance.transform.position;

                    // Take the head's own physics away so it stops behaving
                    // like a grenade. The VICTIM keeps its physics - that is
                    // the entire point, since the rope moves it by force.
                    var rb = collision.otherRigidbody != null
                        ? collision.otherRigidbody
                        : __instance.GetComponentInParent<Rigidbody2D>();

                    if (rb != null)
                    {
                        rb.linearVelocity = Vector2.zero;
                        rb.angularVelocity = 0f;
                        rb.simulated = false;
                    }

                    Latch(t, __instance.Owner, __instance.gameObject,
                          collision.collider, where);

                    Damage d = __instance.Damage;
                    d.amount = 0f;
                    __instance.Damage = d;
                }
                catch (Exception e)
                {
                    Log.LogError("Harpoon landing failed: " + e);
                }
            }
        }

        private static void Latch(
            ForgeTether t, Unit owner, GameObject go,
            Collider2D on, Vector2 point)
        {
            t.latched = true;
            t.latchedAt = Time.time;
            t.owner = owner;
            t.anchorPoint = point;

            if (on != null)
            {
                t.host = on.transform;
                t.hostBody = on.attachedRigidbody;
                t.hostOffset = t.host.InverseTransformPoint(go.transform.position);
            }

            // Latching onto terrain is only meaningful for Self - there is no
            // rigidbody to drag, so every other mode would just hang a rope on
            // a wall and do nothing. Say so rather than look broken.
            if (t.hostBody == null && t.pull != ForgeTether.Pull.Self &&
                t.pull != ForgeTether.Pull.Anchor)
            {
                Log.LogInfo(
                    "Harpoon hit something with no rigidbody (terrain), so " +
                    "there is nothing for the rope to drag. Only " +
                    "\"pull\": \"self\" does anything against a wall.");
            }

            t.Register();

            var ctrl = go.GetComponent<ForgeTetherController>();

            if (ctrl == null)
                ctrl = go.AddComponent<ForgeTetherController>();

            ctrl.Begin(t);

            ForgeTether.Cull(owner, t, t.max);
        }
    }
}
