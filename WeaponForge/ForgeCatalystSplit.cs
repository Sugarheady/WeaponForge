using System;
using BepInEx.Logging;
using UnityEngine;

namespace WeaponForge
{
    // The two things a triggered shot needs doing to it - boosting and aiming -
    // plus the SPLIT redirect, which is the only genuinely new mechanic in the
    // catalyst.
    public static class ForgeCatalystSplit
    {
        private static readonly ManualLogSource Log =
            ForgeLog.Source("WeaponForge.Catalyst");

        // "With more damage." Goes through the shared stack rather than onto
        // Damage directly - growth, the damage ramp and a chain's falloff all
        // write there too, and three features assigning the same field would
        // leave whichever ran last as the only one that counted.
        public static void Boost(Component shot, ForgeCatalyst.Config cfg)
        {
            var ip = shot as IProjectile;

            if (ip == null)
                return;

            if (cfg.damageMultiplier != 1f)
            {
                var plain = shot as Projectile;

                if (plain != null)
                {
                    var stack = ForgeDamageStack.For(plain);

                    if (stack != null)
                    {
                        stack.catalyst *= cfg.damageMultiplier;
                        stack.Apply(plain);
                    }
                }
                else
                {
                    // No stack for a physics shot - assign, which is what the
                    // other physics-capable features do.
                    Damage d = ip.Damage;
                    d.amount *= cfg.damageMultiplier;
                    ip.Damage = d;
                }
            }

            if (cfg.speedMultiplier != 1f && ip.Velocity.sqrMagnitude > 0.0001f)
                ip.Velocity = ip.Velocity * cfg.speedMultiplier;
        }

        // Point a shot at the nearest enemy. Works for BOTH projectile classes,
        // which is the whole reason this is separate:
        //
        //   - a plain Projectile is steered by writing Velocity AND
        //     idealDirection (the game rebuilds Velocity from that field every
        //     frame, so writing only Velocity gets silently overwritten);
        //   - a PhysicsProjectile steers ITSELF - its Homing() runs every
        //     FixedUpdate regardless of the homing flag - so the way to aim one
        //     is to move its private targetPosition, exactly as a reflected
        //     missile is repointed.
        public static void AimAtNearest(
            Component shot, Unit owner, ForgeCatalyst.Config cfg)
        {
            var ip = shot as IProjectile;

            if (ip == null)
                return;

            int mask = Physics2D.GetLayerCollisionMask(shot.gameObject.layer);

            AimAssistTarget found = ForgeHomingPatch.FindNearestTarget(
                owner, shot.transform.position, cfg.range, mask, null,
                cfg.targetObjects, cfg.targeting);

            if (found == null)
                return;

            Aim(shot, (Vector2)found.transform.position, found.gameObject);
            Lock(shot, found.gameObject, cfg);
        }

        public static void Aim(Component shot, Vector2 at, GameObject target)
        {
            var ip = shot as IProjectile;

            if (ip == null)
                return;

            Vector2 to = at - (Vector2)shot.transform.position;

            if (to.sqrMagnitude < 0.000001f)
                return;

            Vector2 dir = to.normalized;
            float speed = ip.Velocity.magnitude;

            if (speed <= 0.01f)
                speed = 12f;

            ip.Velocity = dir * speed;

            shot.transform.rotation =
                Quaternion.FromToRotation(Vector3.right, (Vector3)dir);

            var phys = shot as PhysicsProjectile;

            if (phys != null)
            {
                phys.Target = target;
                ForgeBulletTurn.PointAt(phys, at);
                return;
            }

            var plain = shot as Projectile;

            if (plain != null)
                ForgeSteer.SetHeading(plain, dir, speed);
        }

        // Make the aim STICK.
        //
        // Aim() turns a shot toward where the target is standing right now and
        // nothing more, so against anything that moves the redirect points
        // beautifully and then misses. This grants the shot the mod's own
        // homing, pre-loaded with the target the node already chose, so it
        // follows it in.
        //
        // If the weapon already had homing of its own, only the TARGET is
        // replaced - the tuning is the player's and stays theirs.
        public static void Lock(
            Component shot, GameObject target, ForgeCatalyst.Config cfg)
        {
            if (!cfg.lockOn || target == null)
                return;

            // Plain projectiles only. A PhysicsProjectile has already been
            // handed the target by Aim (its own Homing() reads targetPosition
            // every FixedUpdate), and bolting a second steering model onto a
            // rigidbody missile would fight the one the game runs.
            var plain = shot as Projectile;

            if (plain == null)
                return;

            var h = plain.GetComponent<ForgeHoming>();
            bool fresh = (h == null);

            if (fresh)
                h = plain.gameObject.AddComponent<ForgeHoming>();

            if (fresh)
            {
                h.turnRate = Mathf.Max(1f, cfg.lockOnTurnRate);
                h.range = cfg.range;
                h.cone = 180f;      // it has just been turned; let it turn again
                h.retarget = true;  // target died? find another rather than sail on
                h.delay = 0f;
                h.predict = true;
                h.faceTravel = true;

                // A budget rather than no limit: a shot that overshoots must
                // eventually give up instead of orbiting the arena forever.
                h.maxTurn = 540f;
            }

            h.shot = true;
            h.startTime = Time.time;
            h.turned = 0f;
            h.nextScan = 0f;
            h.mask = Physics2D.GetLayerCollisionMask(plain.gameObject.layer);
            h.target = target.transform;
            h.targetAim = target.GetComponentInParent<AimAssistTarget>();
        }

        // SPLIT: the shot becomes one copy per target.
        //
        // The original is re-aimed at the first target and each extra target
        // gets a clone of the same GameObject, so every copy keeps the weapon's
        // art, trail, damage type and impact behaviour without any of it being
        // rebuilt by hand.
        public static void Burst(
            Component shot, ForgeCatalystNode node, ForgeCatalyst.Config cfg)
        {
            var ip = shot as IProjectile;

            if (ip == null)
                return;

            int want = Mathf.Max(1, cfg.targets);

            Vector2 here = shot.transform.position;
            int mask = Physics2D.GetLayerCollisionMask(shot.gameObject.layer);

            var targets = new System.Collections.Generic.List<AimAssistTarget>();
            var seen = new System.Collections.Generic.HashSet<Transform>();

            // NEAREST, not most-forward. FindTarget picks the smallest ANGLE
            // off the shot's heading, which is right for a homing shot steering
            // toward something ahead of it and wrong here: it would send a
            // split at an enemy 10 units dead ahead while ignoring the one
            // standing a unit to the side of the coin. Excluding what has
            // already been picked then walks outward through the crowd.
            for (int i = 0; i < want; i++)
            {
                AimAssistTarget t = ForgeHomingPatch.FindNearestTarget(
                    node.owner, here, cfg.range, mask, seen, cfg.targetObjects,
                    cfg.targeting);

                if (t == null)
                    break;

                seen.Add(t.transform);
                targets.Add(t);
            }

            Boost(shot, cfg);

            if (targets.Count == 0)
            {
                // Nothing to split toward. Boosting it and letting it fly on is
                // a far better outcome than deleting the player's shot.
                return;
            }

            Aim(shot, targets[0].transform.position, targets[0].gameObject);
            Lock(shot, targets[0].gameObject, cfg);

            WeaponBase from = ForgeKillsPatch.WeaponOf(shot);

            for (int i = 1; i < targets.Count; i++)
            {
                try
                {
                    Component c = Clone(shot);

                    if (c == null)
                        continue;

                    // So a kill by a split copy pays the weapon that fired the
                    // shot, exactly as the original would have.
                    ForgeKillsPatch.Attribute(c, from);

                    // Born inside this node's own catch radius, so it would set
                    // it off again on the very next frame.
                    node.Ignore(c);

                    Aim(c, targets[i].transform.position,
                        targets[i].gameObject);

                    Lock(c, targets[i].gameObject, cfg);
                }
                catch (Exception e)
                {
                    Log.LogError("Catalyst split failed: " + e);
                }
            }
        }

        // Duplicate a live shot into a second, equally real one.
        //
        // **A cloned projectile is BLANK, and this is not obvious.** Neither
        // projectile class serializes any of its state: every one of
        // Projectile's fifteen properties - Damage, Owner, Radius, RangeData,
        // LifetimeData, PiercingData, ImpactBehaviour, the lot - is a plain
        // auto-property, and shotTime / startSpeed / idealDirection /
        // collisionLayerMask are private fields with no [SerializeField].
        // Instantiate copies serialized state only, so a copy of a bullet in
        // flight arrives with damage 0, radius 0, no lifetime, and a collision
        // mask of ZERO - it passes through the world hitting nothing and, with
        // no lifetime enabled, never despawns either.
        //
        // So the copy has to be handed the original's state and then genuinely
        // SHOT: Shoot() is what stamps shotTime, startSpeed, idealDirection and
        // the collision mask, and it is also what registers the copy with the
        // projectile tracker so later nodes can catch it.
        private static Component Clone(Component source)
        {
            GameObject copy = UnityEngine.Object.Instantiate(
                source.gameObject, source.transform.position,
                source.transform.rotation);

            // A clone must never be a node itself, or two nodes would trigger
            // each other forever. Destroy is deferred to the end of the frame,
            // so clearing cfg matters too - the Shoot below would otherwise
            // find the pending copy and arm it.
            foreach (var n in
                     copy.GetComponentsInChildren<ForgeCatalystNode>(true))
            {
                n.cfg = null;
                n.cfgId = -1;   // or Resolve() would hand it straight back
                UnityEngine.Object.Destroy(n);
            }

            var plainFrom = source as Projectile;

            if (plainFrom != null)
            {
                var to = copy.GetComponentInChildren<Projectile>(true);

                if (to == null)
                {
                    UnityEngine.Object.Destroy(copy);
                    return null;
                }

                to.Owner = plainFrom.Owner;
                to.Radius = plainFrom.Radius;
                to.Damage = plainFrom.Damage;
                to.Explosion = plainFrom.Explosion;
                to.DischargeData = plainFrom.DischargeData;
                to.Burn = plainFrom.Burn;
                to.ImpactBehaviour = plainFrom.ImpactBehaviour;
                to.RangeData = plainFrom.RangeData;
                to.LifetimeData = Bounded(
                    plainFrom.LifetimeData, plainFrom.RangeData);
                to.RotationData = plainFrom.RotationData;
                to.PushForce = plainFrom.PushForce;
                to.PiercingData = plainFrom.PiercingData;
                to.MovementNoiseData = plainFrom.MovementNoiseData;
                to.ProjectileBounceData = plainFrom.ProjectileBounceData;
                to.SubEmitters = plainFrom.SubEmitters;
                to.Velocity = plainFrom.Velocity;

                to.Shoot();
                return to;
            }

            var physFrom = source as PhysicsProjectile;

            if (physFrom != null)
            {
                var to = copy.GetComponentInChildren<PhysicsProjectile>(true);

                if (to == null)
                {
                    UnityEngine.Object.Destroy(copy);
                    return null;
                }

                to.Owner = physFrom.Owner;
                to.Damage = physFrom.Damage;
                to.Explosion = physFrom.Explosion;
                to.DischargeData = physFrom.DischargeData;
                to.Burn = physFrom.Burn;
                to.ImpactBehaviour = physFrom.ImpactBehaviour;
                to.RangeData = physFrom.RangeData;
                to.LifetimeData = Bounded(
                    physFrom.LifetimeData, physFrom.RangeData);
                to.HomingData = physFrom.HomingData;
                to.PushForce = physFrom.PushForce;
                to.SubEmitters = physFrom.SubEmitters;
                to.CollidesWithSlime = physFrom.CollidesWithSlime;
                to.Velocity = physFrom.Velocity;

                to.Shoot();
                return to;
            }

            UnityEngine.Object.Destroy(copy);
            return null;
        }

        // A copy has to die of something. Normally it inherits whatever bounded
        // the original, but a shot with BOTH range and lifetime switched off
        // relies on hitting the world - and multiplying that by `targets` every
        // time a node fires is how you end up with a level full of invisible
        // bullets. Only touches the case where nothing else would.
        private static ProjectileLifetimeData Bounded(
            ProjectileLifetimeData lifetime, ProjectileRangeData range)
        {
            if (lifetime.enabled || range.enabled)
                return lifetime;

            lifetime.enabled = true;
            lifetime.time = 6f;

            // This bound is ours, not the weapon's, so it must expire quietly -
            // firing an explosion or a sub-emitter the weapon never scheduled
            // would be a worse surprise than the leak.
            lifetime.spawnOnTimeDown = null;
            lifetime.spawnExplosion = false;
            lifetime.fireSub = false;
            lifetime.discharge = false;
            return lifetime;
        }
    }
}
