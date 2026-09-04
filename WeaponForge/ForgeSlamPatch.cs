using System;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace WeaponForge
{
    // Slam-into-terrain, using a dispatch the game already runs.
    //
    // LevelSegmentComponent.OnCollisionEnter2D fires for anything with a
    // rigidbody that touches terrain, works out which CELL was hit, and hands
    // every ICellCollisionListener on the colliding body a CellCollision
    // carrying the FULL Collision2D. HealthBase implements that interface - it
    // is how CellType.contactDamage hurts you on a hazard cell - so a postfix
    // here gets the victim, the impact and the cell type for nothing.
    //
    // That also settles a question worth recording: enemies really do collide
    // with terrain (Entities collides with Ground in the layer matrix), so
    // throwing one at a wall is a physical event and not just a visual one.
    public static class ForgeSlamPatch
    {
        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("WeaponForge.Slam");

        [HarmonyPatch(typeof(HealthBase), "OnCellCollision")]
        public class OnCellCollision
        {
            static void Postfix(HealthBase __instance, CellCollision cellCollision)
            {
                try
                {
                    // Cheap gate first: almost nothing is ever slammable, and
                    // this runs for every unit-terrain contact in the level.
                    var slam =
                        __instance.GetComponentInParent<ForgeSlam>() ??
                        __instance.GetComponentInChildren<ForgeSlam>();

                    if (slam == null || !slam.Live)
                        return;

                    slam.Resolve(cellCollision.collision);
                }
                catch (Exception e)
                {
                    Log.LogError("Slam-into-terrain failed: " + e);
                }
            }
        }

        // ------------------------------------------------------------------
        // MARKING a target. FOUR ROUTES, because "I hit it" is four different
        // code paths in this game.
        //
        // `slam` is a STANDALONE PROPERTY, in the same sense as `discharge` or
        // `aimAssistData`: bolt it onto any weapon and whatever that weapon
        // hits becomes brittle for a moment. **It does NOT have to be the
        // thing that throws them.** A marker gun plus a gravity well is two
        // weapons cooperating; a marker gun plus an explosion is one weapon
        // doing it in two stages; a marker gun plus nothing at all still pays
        // off when an enemy charges you and clips a wall. `requiresPush` exists
        // for the weapons where the shove really is the point.
        //
        // THE TWO PROJECTILE ROUTES are hooked AT THE CONTACT rather than at
        // the damage, and that matters: a shove weapon deals almost no damage
        // on purpose, and a hit that deals none may never reach the damage
        // pipeline at all. `Projectile.OnObjectHit` fires for every contact.
        // They are PREFIXES so the owner and the weapon are read before
        // anything later in the method can destroy the shot.
        //
        // THE THREE DAMAGE ROUTES cover everything the projectile hooks cannot
        // see - a hitscan beam, an explosion, a discharge arc - and read the
        // weapon from ForgeKillsPatch, which already solved the awkward one
        // (an Explosion is a struct with no route back to anything). They are
        // POSTFIXES for the reason recorded in ForgeSlowPatch: ForgeKillsPatch
        // sets its credit in a prefix and clears it in a finalizer on these
        // same methods, so a postfix sits inside that window and a prefix
        // would be racing it.
        //
        // Marking twice is free - `ForgeSlam.Mark` refreshes rather than
        // stacks, deliberately, because being held in a beam should keep you
        // slammable rather than make you more slammable.
        //
        // TWO THINGS WORTH RECORDING, both found in the decompile and both
        // load-bearing for the preset:
        //
        //   - `lastKnockbackTimes` is a field on the PROJECTILE INSTANCE, so
        //     `knockBackRepeatDelay` only stops one pellet shoving the same
        //     body twice. A fan of nine pellets applies nine impulses, which
        //     is exactly what makes a close-range shove hit hard.
        //   - **A PIERCING SHOT NEVER PUSHES ANYTHING.** The
        //     `if (PiercingData.enabled && layer != ground) { MoveForward();
        //     return; }` branch sits ABOVE the push, so pierce and pushForce
        //     are mutually exclusive on a plain projectile. The builder warns.
        [HarmonyPatch(typeof(Projectile), "OnObjectHit")]
        public class OnProjectileHit
        {
            static void Prefix(Projectile __instance, RaycastHit2D hit)
            {
                if (!ForgeSlam.Any || __instance == null)
                    return;

                try
                {
                    Consider(__instance, __instance.PushForce,
                             hit.collider, __instance.Owner,
                             __instance.Damage.damageType);
                }
                catch (Exception e)
                {
                    Log.LogError("Marking a shoved target failed: " + e);
                }
            }
        }

        // The lobbed route. No knockback delay at all on this one - it pushes
        // on every collision it has.
        [HarmonyPatch(typeof(PhysicsProjectile), "OnCollisionEnter2D")]
        public class OnLobbedHit
        {
            static void Prefix(PhysicsProjectile __instance, Collision2D collision)
            {
                if (!ForgeSlam.Any || __instance == null || collision == null)
                    return;

                try
                {
                    Consider(__instance, __instance.PushForce,
                             collision.collider, __instance.Owner,
                             __instance.Damage.damageType);
                }
                catch (Exception e)
                {
                    Log.LogError("Marking a shoved target failed: " + e);
                }
            }
        }

        // ---- the damage routes: hitscan, explosions, discharge ---------
        [HarmonyPatch(typeof(DamagableResource), "ProjectileCollided")]
        public class OnDamagedByProjectile
        {
            static void Postfix(DamagableResource __instance) { Hit(__instance); }
        }

        [HarmonyPatch(typeof(DamagableResource), "OnHitByHitscanWeapon")]
        public class OnDamagedByBeam
        {
            static void Postfix(DamagableResource __instance) { Hit(__instance); }
        }

        [HarmonyPatch(typeof(DamagableResource), "OnExplosion")]
        public class OnDamagedByExplosion
        {
            static void Postfix(DamagableResource __instance) { Hit(__instance); }
        }

        private static void Hit(DamagableResource victim)
        {
            if (!ForgeSlam.Any || victim == null)
                return;

            try
            {
                WeaponBase source = ForgeKillsPatch.Source;

                if (source == null || source.TemplateData == null)
                    return;

                ForgeSlam.Spec spec;

                if (!ForgeSlam.TryGet(source.TemplateData, out spec))
                    return;

                // Requiring a shove is meaningless on a beam or a blast: the
                // beam's push is the weapon's own pushForce, and an
                // explosion's is derived from its radius by the game, so
                // neither is a per-hit fact this route can see. A weapon that
                // insists on a shove is a projectile weapon by definition, and
                // the contact hooks above already handle it.
                if (spec.requiresPush)
                    return;

                // No point marking a corpse, and its rigidbody may already be
                // on its way out.
                if (victim.IsDead)
                    return;

                Unit target = victim.GetComponentInParent<Unit>();

                if (target == null)
                    return;

                // Never mark whoever is holding the gun. Reachable in
                // practice: explosions in this game have no owner filter at
                // all, so your own blast reaches you like anyone else's.
                if (source.Owner != null && ReferenceEquals(target, source.Owner))
                    return;

                ForgeSlam.Mark(
                    target, spec, source.Owner, source,
                    source.Damage.damageType);
            }
            catch (Exception e)
            {
                Log.LogError("Marking a damaged target slammable failed: " + e);
            }
        }

        private static void Consider(
            Component shot, float pushForce, Collider2D hitCollider,
            Unit owner, Resource damageType)
        {
            if (hitCollider == null)
                return;

            ForgeSlam.Spec spec;

            // The weapon is the authority on whether this shot slams, and
            // ForgeKillsPatch already knows which weapon fired it - the same
            // lookup that credits its kills.
            WeaponBase weapon = ForgeKillsPatch.WeaponOf(shot);

            if (weapon == null || weapon.TemplateData == null ||
                !ForgeSlam.TryGet(weapon.TemplateData, out spec))
            {
                return;
            }

            // Only a weapon that says "the shove IS the point" needs one.
            // Everything else marks on contact and lets something else do the
            // throwing, which is what makes this a property rather than a
            // weapon.
            if (spec.requiresPush && pushForce <= 0f)
                return;

            Unit target = hitCollider.GetComponentInParent<Unit>();

            if (target == null || (owner != null && target == owner))
                return;

            ForgeSlam.Mark(target, spec, owner, weapon, damageType);
        }
    }
}
