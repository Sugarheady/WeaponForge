using System;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace WeaponForge
{
    // ★ A LOBBED OR ROCKET WINGMAN WOULD DETONATE ON ITS OWN SQUADMATES, and
    // the reason is a disagreement between the two projectile classes that has
    // been there since the beginning.
    //
    // `Projectile.FixedUpdate` has ALWAYS skipped a hit whose rigidbody carries
    // a `Unit` friendly to `Owner`. **`PhysicsProjectile` has no equivalent
    // anywhere** - its `OnCollisionEnter2D` goes straight to `Impact`, and
    // `Impact` calls `TriggerCollisionOnObject` before it even consults
    // `safetyDistance`. `PlayerProjectiles` collides with `Player`, and a drone
    // body sits on `Player`.
    //
    // Nobody noticed because a friendly body is rarely in your own line of
    // fire: the ship has exactly the same exposure today - it fires rockets
    // from a barrel beside its own collider - and the barrel offset covers it.
    // **A wingman ring puts four to eight friendly bodies there permanently**,
    // and under `aim: "cursor"` every shot crosses the ring.
    //
    // > **Solving it for one consumer was never solving it.** Same shape as the
    // > damage/kill attribution split: the plain projectile got the rule and
    // > the physics one never did.
    //
    // ★ AND IT IS SCOPED TO WINGMAN BODIES RATHER THAN APPLIED GLOBALLY, which
    // is a deliberate narrowing rather than caution. Giving every
    // `PhysicsProjectile` the plain projectile's friendly skip would also stop
    // ENEMY rockets detonating on enemy units - defensible, arguably a bug fix,
    // and a change to how the base game plays that nobody asked for. The case
    // that actually matters is bounded and nameable: a shot whose owner has
    // wingman bodies out, hitting one of that owner's own bodies.
    //
    // ⚠ ONE NUANCE THAT COULD NOT BE DESIGNED AROUND IN ADVANCE. Skipping
    // `Impact` stops the detonation but NOT the collision - the solver has
    // already resolved the contact by the time this runs, so the shot takes a
    // nudge off the first touch. `Physics2D.IgnoreCollision` on the pair is
    // what makes the rest of the flight clean, and doing it here rather than at
    // fire time keeps it self-scoping: the pair dies with the projectile, and
    // it costs one call per shot per body actually touched instead of `count`
    // calls per shot fired.
    public static class ForgeWingmanShotPatch
    {
        private static readonly ManualLogSource Log =
            ForgeLog.Source("WeaponForge.Wingman");

        private static bool _said;

        [HarmonyPatch(typeof(PhysicsProjectile), "OnCollisionEnter2D")]
        public class OnHit
        {
            static bool Prefix(
                PhysicsProjectile __instance, Collision2D collision)
            {
                // ★ `AnyDrone`, NOT `Any`. This runs on EVERY physics-shot
                // collision in the world - enemy rockets into terrain
                // included - and the line below it walks a hierarchy. Gating
                // on "is there a wingman file at all" made every orb-only
                // author pay a `GetComponentInParent` per rocket impact for a
                // question whose answer is always no. `AnyDrone` is latched at
                // registration for exactly this kind of per-event gate, and
                // was already used on the other one.
                if (!ForgeWingman.AnyDrone || __instance == null ||
                    collision == null || collision.collider == null)
                {
                    return true;
                }

                try
                {
                    Unit hit = collision.collider.GetComponentInParent<Unit>();

                    if (!ForgeWingmanController.OwnsBody(__instance.Owner, hit))
                        return true;

                    // Pass through for the rest of the flight. The contact that
                    // got us here is already resolved, so this is about the
                    // NEXT one - without it a rocket flown into the ring would
                    // rattle around inside it.
                    if (collision.otherCollider != null)
                    {
                        Physics2D.IgnoreCollision(
                            collision.otherCollider, collision.collider, true);
                    }

                    Say();

                    return false;
                }
                catch (Exception e)
                {
                    Log.LogError("wingman: friendly shot check failed: " + e);
                    return true;
                }
            }
        }

        // The SHIELD route, which is a separate method and a separate layer.
        // A drone body can carry one (`DamagableResource.shieldPrefab` is set
        // on every drone prefab, and `firstShieldSize` is a real 4), the shield
        // object sits on `Default`, and `PlayerProjectiles` collides with
        // `Default` - so a rocket would pop on your own companion's bubble even
        // with the collision above handled. Two entry points, one rule.
        [HarmonyPatch(typeof(PhysicsProjectile), "OnTriggerEnter2D")]
        public class OnTrigger
        {
            static bool Prefix(PhysicsProjectile __instance, Collider2D other)
            {
                // `AnyDrone` rather than `Any`, for the reason the collision
                // prefix above sets out: this is a per-event gate.
                if (!ForgeWingman.AnyDrone || __instance == null || other == null)
                    return true;

                try
                {
                    // Only the shield branch is ours to refuse. The slime and
                    // pass-through branches in that method are about terrain
                    // and about deliberate see-through objects, and a wingman
                    // body is neither.
                    if (other.GetComponent<Shield>() == null)
                        return true;

                    Unit hit = other.GetComponentInParent<Unit>();

                    if (!ForgeWingmanController.OwnsBody(__instance.Owner, hit))
                        return true;

                    Say();

                    return false;
                }
                catch (Exception e)
                {
                    Log.LogError("wingman: friendly shield check failed: " + e);
                    return true;
                }
            }
        }

        private static void Say()
        {
            if (_said)
                return;

            _said = true;

            Log.LogInfo(
                "wingman - a lobbed or rocket shot touched one of your own " +
                "drone companions and did NOT detonate on it. Plain bullets " +
                "have always skipped a friendly unit; physics shots never did, " +
                "which nobody noticed because a friendly body is rarely in " +
                "your own line of fire - a wingman ring puts several there " +
                "permanently. Note this is about TRIGGERING a shot, not about " +
                "being caught by one: an explosion that already exists still " +
                "hits everything in range, companions included. Said once per " +
                "run.");
        }

        public static void Reset()
        {
            _said = false;
        }
    }
}
