using System;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace WeaponForge
{
    // Counts and shapes bounces for ForgeRicochet.
    //
    // Projectile.Reflect(normal) is the single choke point - it is public, it is
    // tiny, and it is the ONLY place a projectile's heading gets mirrored - so
    // both the counting and the shaping hang off it:
    //
    //   prefix  -> out of bounces? finish the shot and skip the reflect.
    //              otherwise count it and apply the damage falloff.
    //   postfix -> the game has just mirrored Velocity (and idealDirection);
    //              now apply speed loss, scatter and seek to the new heading.
    //
    // Splitting it that way lets the GAME own the actual mirror maths and the
    // idealDirection bookkeeping, so wave and wobble keep working off a heading
    // that still means what they think it means.
    //
    // One caveat worth knowing: Reflect is also called by the impactBehaviour
    // safety-distance path (a shot that hits something within safetyDistance of
    // the muzzle is bounced instead of exploding, so weapons do not blow up in
    // your face). Those consume a bounce too. That is a rare edge case and
    // arguably correct, but it is why a point-blank shot can look like it lost
    // one early.
    public static class ForgeRicochetPatch
    {
        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("WeaponForge.Ricochet");

        // idealDirection is what movementNoiseData and the wave patch measure
        // from, so scatter/seek have to move it in step with Velocity or a
        // ricocheting wave beam would weave around a heading it no longer has.
        // That field is reached through `ForgeSteer`, which owns it - this file
        // carried its own copy of the reflection until the steering audit.

        // SpawnExplosion / FireSub are private on Projectile, and re-creating
        // what they do would mean duplicating explosion + sub-emitter wiring
        // that already works.
        private static MethodInfo _spawnExplosion;
        private static MethodInfo _fireSub;

        // Where the shot was fired from, so a bounce right on top of the player
        // can be kept quiet. Private on Projectile, hence the FieldInfo.
        private static FieldInfo _shotPosition;
        private static bool _ready;

        private static void Ensure()
        {
            if (_ready)
                return;

            _ready = true;

            // ForgeSteer has already said WHY it could not reach the field;
            // this says what it costs ricochet specifically, which is the
            // division of labour that file's header asks for.
            if (!ForgeSteer.Available)
            {
                Log.LogWarning(
                    "Ricochet cannot reach Projectile.idealDirection - " +
                    "bounces still work, but a ricocheting wave/wobble shot " +
                    "will weave around its pre-bounce heading.");
            }

            _spawnExplosion =
                AccessTools.Method(typeof(Projectile), "SpawnExplosion");

            _fireSub = AccessTools.Method(typeof(Projectile), "FireSub");

            _shotPosition = AccessTools.Field(typeof(Projectile), "shotPosition");
        }

        // Is this bounce close enough to the muzzle that its effects would go off
        // in the player's face?
        //
        // THE TRAP THIS FIXES, because it is not obvious from either side.
        // `impactBehaviour.safetyDistance` exists precisely so a point-blank shot
        // does not detonate on you - but look at the order in
        // Projectile.OnObjectHit: the BOUNCE branch runs first and **returns
        // before the impactBehaviour block is reached at all**. So on a weapon
        // whose bounce mask covers what it just hit, safetyDistance is never
        // consulted; every hit is a real bounce, the "point-blank bounces are
        // excluded" guard never has anything to exclude, and `explodeOnBounce`
        // happily fires a full explosion one unit from the ship. Firing at a wall
        // beside you then stacked four blasts on the player in four frames, which
        // read as a weird white fountain doing enormous damage.
        //
        // So the safety radius has to be re-applied here, on our own effects,
        // using the number the weapon already carries for exactly this purpose.
        private static bool TooCloseToMuzzle(Projectile p, Vector2 pos)
        {
            ProjectileImpactBehaviour ib = p.ImpactBehaviour;

            if (ib.safetyDistance <= 0f || _shotPosition == null)
                return false;

            try
            {
                var origin = (Vector2)_shotPosition.GetValue(p);
                return Vector2.Distance(pos, origin) < ib.safetyDistance;
            }
            catch
            {
                return false;
            }
        }

        // What the shot just ran into. Reflect(normal) is handed a normal and
        // nothing else, so the per-bounce effects need the collision captured
        // one level up - in OnObjectHit, which calls Reflect synchronously
        // further down the same method. Same reasoning as ForgeVolley: one
        // synchronous window, so a plain static is exactly right, and the
        // owner check is what stops a stale capture being read by a different
        // shot on a later frame.
        private static Projectile _hitOwner;
        private static int _hitLayer = -1;
        private static Vector2 _hitPoint;
        private static bool _realBounce;

        [HarmonyPatch(typeof(Projectile), "OnObjectHit")]
        public class OnHit
        {
            static void Prefix(Projectile __instance, RaycastHit2D hit)
            {
                _hitOwner = null;

                try
                {
                    var ric = __instance.GetComponent<ForgeRicochet>();

                    if (ric == null || !ric.WantsEffects)
                        return;

                    if (hit.collider == null)
                        return;

                    _hitOwner = __instance;
                    _hitLayer = hit.collider.gameObject.layer;
                    _hitPoint = hit.point;

                    // Was this going to be a REAL bounce, or the impact
                    // safety-distance one?
                    //
                    // That distinction matters more than it looks. A weapon
                    // with a safetyDistance bounces instead of exploding when
                    // you are point-blank, precisely so it does not blow up in
                    // your face - and "explode on every bounce" would undo
                    // exactly that. So the same condition the game is about to
                    // test is tested here, and the effects only fire on a
                    // genuine bounce.
                    ProjectileBounceData bd = __instance.ProjectileBounceData;

                    _realBounce =
                        bd != null && bd.enabled &&
                        ((bd.layerMask.value & (1 << _hitLayer)) != 0);
                }
                catch
                {
                    _hitOwner = null;
                }
            }

            static void Postfix()
            {
                _hitOwner = null;
            }
        }

        // Capture the shot's damage as fired, the way growth and the ramp do.
        //
        // Capturing lazily at the first bounce would usually work and would
        // occasionally be wrong: if anything else has already scaled Damage by
        // then, that scaled figure becomes the "base" and the other feature's
        // contribution is counted twice. Shoot is the one moment the number is
        // unambiguously the weapon's own.
        //
        // Gated on the multiplier actually being set, because Capture goes
        // through ForgeDamageStack.For, which AddComponents on a miss - and
        // most ricochets never touch damage at all. No reason to give every
        // bouncing bullet in a firefight a component it will never read.
        [HarmonyPatch(typeof(Projectile), "Shoot")]
        public class OnShoot
        {
            static void Postfix(Projectile __instance)
            {
                try
                {
                    var ric = __instance.GetComponent<ForgeRicochet>();

                    if (ric == null || ric.damageMultiplier == 1f)
                        return;

                    ForgeDamageStack.For(__instance)
                        .Capture(__instance.Damage.amount);
                }
                catch (Exception e)
                {
                    // A shot that cannot record its base damage should still
                    // be a shot. Worst case the multiplier does nothing, which
                    // is exactly what it did before this existed.
                    Log.LogError("Ricochet damage capture failed: " + e);
                }
            }
        }

        [HarmonyPatch(typeof(Projectile), "Reflect")]
        public class OnReflect
        {
            static bool Prefix(Projectile __instance, Vector2 normal)
            {
                var ric = __instance.GetComponent<ForgeRicochet>();

                if (ric == null)
                    return true;

                ric.bouncing = false;

                try
                {
                    Ensure();

                    if (!ric.Unlimited && ric.used >= ric.bounces)
                    {
                        Finish(__instance);
                        return false;
                    }

                    ric.used++;
                    ric.bouncing = true;

                    if (ric.damageMultiplier != 1f)
                    {
                        // Through the shared stack, never straight onto
                        // Damage. A ricocheting shot can also be growing or
                        // ramping, and those rebuild the amount from their
                        // captured base every FixedUpdate - so a direct write
                        // here survived only until the next frame, silently.
                        //
                        // `used` was incremented just above, so the first
                        // bounce is already multiplied once. That is what this
                        // has always done, and it is deliberately NOT the same
                        // convention as the lobbed `bounce` block, whose
                        // falloff starts on the second bounce. Matching them
                        // up would quietly rebalance every ricochet weapon
                        // that already exists.
                        var stack = ForgeDamageStack.For(__instance);

                        stack.Capture(__instance.Damage.amount);
                        stack.ricochet =
                            Mathf.Pow(ric.damageMultiplier, ric.used);
                        stack.Apply(__instance);
                    }
                }
                catch (Exception e)
                {
                    // Let the plain bounce happen rather than stranding the
                    // shot mid-air with no heading.
                    Log.LogError("Ricochet bookkeeping failed: " + e);
                    return true;
                }

                return true;
            }

            static void Postfix(Projectile __instance)
            {
                var ric = __instance.GetComponent<ForgeRicochet>();

                // Harmony runs postfixes even when a prefix skipped the
                // original, so this flag is what tells us the bounce really
                // happened.
                if (ric == null || !ric.bouncing)
                    return;

                ric.bouncing = false;

                try
                {
                    Shape(__instance, ric);
                }
                catch (Exception e)
                {
                    Log.LogError("Ricochet shaping failed: " + e);
                }

                // After Shape, so the destroy effect is oriented along the
                // heading the shot is actually leaving on.
                try
                {
                    BounceEffects(__instance, ric);
                }
                catch (Exception e)
                {
                    Log.LogError(
                        "Ricochet bounce effects failed, the shot keeps " +
                        "bouncing: " + e);
                }
            }
        }

        private static void Shape(Projectile p, ForgeRicochet ric)
        {
            Vector2 v = p.Velocity;
            float speed = v.magnitude;

            if (speed <= 0.0001f)
                return;

            if (ric.speedMultiplier != 1f)
                speed *= ric.speedMultiplier;

            Vector2 dir = v.normalized;

            // Seek first, then scatter, so the scatter reads as imprecision in
            // the redirect rather than being swallowed by it.
            if (ric.seek)
            {
                int mask =
                    Physics2D.GetLayerCollisionMask(p.gameObject.layer);

                AimAssistTarget found =
                    ForgeHomingPatch.FindTarget(
                        p, p.transform.position, dir,
                        ric.seekRange, ric.seekCone, mask);

                if (found != null)
                {
                    Vector2 to =
                        (Vector2)found.transform.position -
                        (Vector2)p.transform.position;

                    if (to.sqrMagnitude > 0.000001f)
                        dir = to.normalized;
                }
            }

            if (ric.scatter > 0f)
            {
                float jitter =
                    UnityEngine.Random.Range(-ric.scatter, ric.scatter);

                dir = Quaternion.AngleAxis(jitter, Vector3.forward) * dir;
            }

            ForgeSteer.SetHeading(p, dir, speed);

            // Keep the sprite pointing where it is now going. Projectile only
            // sets its rotation at Shoot, so without this a ricocheted shot
            // flies sideways for the rest of its life.
            p.transform.rotation =
                Quaternion.FromToRotation(Vector3.right, (Vector3)p.Velocity);
        }

        // Everything the game would have fired on a terminal hit, fired on a
        // bounce instead - with the shot living on afterwards.
        private static void BounceEffects(Projectile p, ForgeRicochet ric)
        {
            if (!ric.WantsEffects)
                return;

            // No capture means Reflect was reached from somewhere that is not
            // a collision (the deflector re-aiming an enemy shot, say). Firing
            // an explosion there would be an explosion from nowhere.
            if (!ReferenceEquals(_hitOwner, p))
                return;

            if (!_realBounce || !ric.EffectsWant(_hitLayer))
                return;

            Vector2 pos = _hitPoint;

            // The shot still bounces - it just does not detonate while it is
            // this close to you. Mirrors what the game does inside
            // safetyDistance on a non-bouncing weapon: reflect, no effects.
            if (TooCloseToMuzzle(p, pos))
                return;

            if (ric.effectOnBounce)
            {
                ProjectileImpactBehaviour ib = p.ImpactBehaviour;

                if (ib.destroyEffect != null)
                {
                    UnityEngine.Object.Instantiate(
                        ib.destroyEffect, pos, p.transform.rotation);
                }
            }

            if (ric.explodeOnBounce && _spawnExplosion != null)
                _spawnExplosion.Invoke(p, new object[] { pos });

            if (ric.subOnBounce && _fireSub != null)
                _fireSub.Invoke(p, null);

            if (ric.dischargeOnBounce)
            {
                ElectricityManager em;

                if (ServiceLocator.TryGet<ElectricityManager>(out em))
                    em.SpawnDischarge(p.DischargeData, pos);
            }
        }

        // Out of bounces: run the weapon's own impact behaviour if it has one,
        // then destroy. This is the same set of effects the game fires on a
        // normal terminal hit, so a ricochet weapon's last bounce explodes /
        // fires its sub-emitter exactly like its first hit would have.
        private static void Finish(Projectile p)
        {
            Vector2 pos = p.transform.position;

            try
            {
                ProjectileImpactBehaviour ib = p.ImpactBehaviour;

                // Same guard as a bounce: the last bounce can also happen right
                // beside you, and the game's own terminal hit tests
                // safetyDistance before it explodes. The shot is still destroyed
                // below either way.
                if (ib.enabled && !TooCloseToMuzzle(p, pos))
                {
                    if (ib.destroyEffect != null)
                    {
                        UnityEngine.Object.Instantiate(
                            ib.destroyEffect, pos, p.transform.rotation);
                    }

                    if (ib.spawnExplosion && _spawnExplosion != null)
                        _spawnExplosion.Invoke(p, new object[] { pos });

                    if (ib.fireSub && _fireSub != null)
                        _fireSub.Invoke(p, null);

                    if (ib.discharge)
                    {
                        ElectricityManager em;

                        if (ServiceLocator.TryGet<ElectricityManager>(out em))
                            em.SpawnDischarge(p.DischargeData, pos);
                    }
                }
            }
            catch (Exception e)
            {
                Log.LogError(
                    "Ricochet could not run the final impact effects: " + e);
            }

            UnityEngine.Object.Destroy(p.gameObject);
        }
    }
}
