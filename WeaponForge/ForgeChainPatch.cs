using System;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace WeaponForge
{
    // Turns a shot toward its next victim the moment it damages one.
    //
    // Hooked on Projectile.TryHit rather than OnObjectHit because TryHit only
    // reaches its body when the game's own re-hit delay allows the damage -
    // so this counts ENEMIES HIT, not frames spent overlapping one. Hooking
    // the collision entry point instead would fire many times per enemy and
    // burn the whole chain on the first target.
    //
    // The redirect itself is the same three writes ricochet makes: Velocity,
    // idealDirection, and the transform rotation. idealDirection is the
    // load-bearing one - wave motion and the game's own noise rebuild
    // Velocity from it every frame, so a chain that only set Velocity would
    // hop and then curve straight back onto its old heading.
    public static class ForgeChainPatch
    {
        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("WeaponForge");

        private static AccessTools.FieldRef<Projectile, Vector2> _idealRef;
        private static MethodInfo _spawnExplosion;
        private static bool _ready;

        private static void Ensure()
        {
            if (_ready)
                return;

            _ready = true;

            try
            {
                _idealRef =
                    AccessTools.FieldRefAccess<Projectile, Vector2>(
                        "idealDirection");
            }
            catch (Exception e)
            {
                Log.LogWarning(
                    "Could not reach Projectile.idealDirection (" +
                    e.Message + ") - chained shots will still turn, but a " +
                    "chain weapon that also uses wave will drift back onto " +
                    "its old heading.");
            }

            _spawnExplosion =
                AccessTools.Method(typeof(Projectile), "SpawnExplosion");
        }

        [HarmonyPatch(typeof(Projectile), "TryHit")]
        public class OnTryHit
        {
            static void Postfix(
                Projectile __instance, IProjectileListener listener)
            {
                var chain = __instance.GetComponent<ForgeChain>();

                if (chain == null)
                    return;

                try
                {
                    Ensure();
                    RegisterHit(__instance, chain, listener);
                }
                catch (Exception e)
                {
                    Log.LogError("Chain jump failed: " + e);
                }
            }
        }

        // Services a jump that was owed but delayed. Also the only reason this
        // feature needs a per-frame hook at all.
        [HarmonyPatch(typeof(Projectile), "FixedUpdate")]
        public class OnFixedUpdate
        {
            static void Prefix(Projectile __instance)
            {
                var chain = __instance.GetComponent<ForgeChain>();

                if (chain == null || !chain.pending)
                    return;

                if (Time.time < chain.pendingAt)
                {
                    // Hold station while the beat plays out. Re-asserted every
                    // frame because the game's own range slow-down would
                    // otherwise push the shot back up to speed.
                    if (chain.hoverDuringDelay)
                        __instance.Velocity = Vector2.zero;

                    return;
                }

                chain.pending = false;

                try
                {
                    Ensure();
                    Jump(__instance, chain);
                }
                catch (Exception e)
                {
                    Log.LogError("Delayed chain jump failed: " + e);
                }
            }
        }

        private static void RegisterHit(
            Projectile p, ForgeChain chain, IProjectileListener listener)
        {
            // THE gate. A piercing shot re-enters the hit path every frame it
            // overlaps an enemy, and TryHit's own re-hit delay silently
            // swallows all but the first of those - but a Harmony postfix runs
            // on the swallowed ones too. Without this check the chain spent
            // two or three jumps on the very first enemy it touched.
            if (!ForgeProjectileHit.Landed(p, listener))
                return;

            // Already owe a jump: a hovering blade sitting inside its victim
            // will land another legitimate hit once the re-hit delay expires,
            // and that must not queue a second jump.
            if (chain.pending)
                return;

            // Remember what we just hit, even when out of jumps: a shot that
            // has finished chaining still should not turn around and re-hit
            // something on its way past.
            Transform hitRoot = RootOf(listener);

            if (hitRoot != null)
            {
                if (chain.revisit)
                {
                    // Ping-pong mode: no history, just "not the one I am
                    // standing in". Reusing the same set keeps this
                    // allocation-free and means one search path below.
                    chain.visited.Clear();
                    chain.visited.Add(hitRoot);
                }
                else
                {
                    chain.visited.Add(hitRoot);
                }
            }

            if (!chain.CanJump)
                return;

            if (chain.jumpDelay > 0f)
            {
                chain.pending = true;
                chain.pendingAt = Time.time + chain.jumpDelay;

                if (chain.hoverDuringDelay)
                {
                    chain.hoverSpeed = p.Velocity.magnitude;
                    p.Velocity = Vector2.zero;
                }

                return;
            }

            Jump(p, chain);
        }

        private static void Jump(Projectile p, ForgeChain chain)
        {
            Vector2 here = p.transform.position;

            int mask = Physics2D.GetLayerCollisionMask(p.gameObject.layer);

            // Always pass the exclude set. In revisit mode it holds exactly
            // one entry - the enemy just hit - so the shot cannot pick the
            // body it is currently inside and "jump" nowhere. That single
            // line is what makes ping-pong actually ping-pong.
            AimAssistTarget next =
                ForgeHomingPatch.FindNearestTarget(
                    p, here, chain.range, mask, chain.visited);

            if (next == null)
            {
                // Nothing left within reach. Carrying on straight is the
                // gentler default - the shot behaves like a normal piercing
                // round for the rest of its life.
                if (chain.vanishWhenNoTarget)
                    Finish(p);

                return;
            }

            Vector2 to = (Vector2)next.transform.position - here;

            if (to.sqrMagnitude <= 0.000001f)
                return;

            Vector2 dir = to.normalized;

            float speed = p.Velocity.magnitude;

            // A hovering blade has had its velocity zeroed on purpose, so the
            // speed it should leave with is the one it arrived with.
            if (speed <= 0.0001f && chain.hoverSpeed > 0.0001f)
                speed = chain.hoverSpeed;

            if (speed <= 0.0001f)
                speed = to.magnitude;   // a stalled shot still needs to move

            if (chain.speedMultiplier != 1f)
                speed *= chain.speedMultiplier;

            p.Velocity = dir * speed;

            if (_idealRef != null)
                _idealRef(p) = dir;

            // Projectile only sets its rotation at Shoot, so without this a
            // chained shot flies sideways for the rest of its life.
            p.transform.rotation =
                Quaternion.FromToRotation(Vector3.right, (Vector3)dir);

            chain.used++;

            // Damage decay goes through the shared stack, never straight onto
            // Damage - growth and the damage ramp write there too, and three
            // features assigning the same field would leave whichever ran
            // last as the only one that counted.
            if (chain.damageMultiplier != 1f)
            {
                var stack = ForgeDamageStack.For(p);
                stack.Capture(p.Damage.amount);
                stack.chain = Mathf.Pow(chain.damageMultiplier, chain.used);
                stack.Apply(p);
            }
        }

        // The thing we just hit, as the transform FindNearestTarget will
        // report for it - so the exclude list and the search agree.
        private static Transform RootOf(IProjectileListener listener)
        {
            var component = listener as Component;

            if (component == null)
                return null;

            var aim = component.GetComponentInParent<AimAssistTarget>();

            return (aim != null) ? aim.transform : null;
        }

        // Out of targets and asked to vanish: fire the weapon's own impact
        // behaviour first, so a chain weapon's last hop explodes exactly like
        // a normal terminal hit would.
        private static void Finish(Projectile p)
        {
            try
            {
                if (p.ImpactBehaviour.enabled &&
                    p.ImpactBehaviour.spawnExplosion &&
                    _spawnExplosion != null)
                {
                    _spawnExplosion.Invoke(
                        p, new object[] { (Vector2)p.transform.position });
                }
            }
            catch (Exception e)
            {
                Log.LogWarning("Chain finish explosion failed: " + e);
            }

            UnityEngine.Object.Destroy(p.gameObject);
        }
    }
}
