using System;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace WeaponForge
{
    // Two rules, on the one method every damage route funnels into.
    public static class ForgeCharmPatch
    {
        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("WeaponForge.Charm");

        private class Scope
        {
            public ForgeCharm.Config cfg;
            public WeaponBase weapon;
            public float before;
        }

        // ------------------------------------------------------------------
        // 1. A CHARMED UNIT CANNOT HURT THE SIDE THAT CHARMED IT.
        //
        // Kept even now that its bullets are adopted, as the belt to that
        // braces: the adoption fixes what its gun can REACH, this fixes what
        // happens if anything gets through anyway - a contact hit, an
        // explosion with no owner filter, or a shot fired the instant before
        // the charm landed.
        //
        // It covers the half that would make charm actively bad: a unit you just
        // turned to your side shooting you in the back. Cancelling at the
        // damage chokepoint works regardless of how it attacks - bullet, beam,
        // explosion or headbutt - because they all arrive here.
        // ------------------------------------------------------------------
        [HarmonyPatch(typeof(DamagableResource), "Damage", new[] { typeof(float) })]
        public class Protect
        {
            static bool Prefix(DamagableResource __instance, float amount)
            {
                if (!ForgeCharm.Any || __instance == null || amount <= 0f)
                    return true;

                try
                {
                    WeaponBase source = ForgeKillsPatch.Source;
                    Unit attacker = (source != null) ? source.Owner : null;

                    if (attacker == null)
                        return true;

                    ForgeCharmed charmed = attacker.GetComponent<ForgeCharmed>();

                    if (charmed == null || charmed.Cfg == null ||
                        !charmed.Cfg.harmless || charmed.Owner == null)
                    {
                        return true;
                    }

                    Unit victim = __instance.GetComponentInParent<Unit>();

                    if (victim == null)
                        return true;

                    // INFIGHTING IS AN EXPLICIT EXEMPTION FROM `harmless`, and
                    // it has to be checked before the ally test rather than
                    // after it.
                    //
                    // This is the SECOND of the two things that made R13 test
                    // 140 fail, and it is why fixing the bullet alone would
                    // have been worse than leaving it broken: with only the
                    // friendly-fire skip relaxed, shots would visibly connect
                    // and deal nothing. `harmless` defaults to TRUE, and
                    // another charmed unit is on the charmer's side by
                    // construction - so this guard swallowed every shot of an
                    // infighting swarm.
                    //
                    // The two rules genuinely mean different things and both
                    // are wanted: "cannot hurt the side that charmed it" is
                    // about YOU and your own minions, and infighting is about
                    // its fellow converts. Naming the exemption keeps that
                    // distinction instead of forcing `harmless: false`, which
                    // would also let the swarm shoot the player.
                    if (ForgeCharm.AreInfightingPeers(attacker, victim))
                        return true;

                    // The charmer, or anything on the charmer's side.
                    //
                    // RawFriends, not IsFriendsWith: `playerCanHit` rewrites
                    // that answer for charmed units, and these two rules run in
                    // opposite directions. Using the rewritten answer here
                    // would let one charmed unit shoot another of yours as a
                    // side effect of a switch about YOUR fire.
                    if (ReferenceEquals(victim, charmed.Owner) ||
                        ForgeCharm.RawFriends(charmed.Owner, victim))
                    {
                        return false;   // swallow the damage
                    }
                }
                catch (Exception e)
                {
                    Log.LogError("Charm protection failed: " + e);
                }

                return true;
            }
        }

        // ------------------------------------------------------------------
        // 2. A CHARMED UNIT'S BULLETS JOIN YOUR SIDE TOO.
        //
        // Without this, charm is a pacifier: flipping a unit's faction does not
        // move its bullets, and an enemy shot is on a layer that cannot collide
        // with Entities at all - so a charmed shooter waves its gun at things it
        // physically cannot touch.
        //
        // `Projectile.Shoot` is the moment the shot exists and its collision
        // mask has just been baked from its layer, which is exactly when both
        // can be rewritten. ForgeKillsPatch already postfixes this same method,
        // so the timing is proven rather than hopeful.
        //
        // Owner is left alone on purpose - see ForgeBulletTurn.Adopt.
        // ------------------------------------------------------------------
        [HarmonyPatch(typeof(Projectile), "Shoot")]
        public class OnShoot
        {
            static void Postfix(Projectile __instance)
            {
                if (!ForgeCharm.Any || __instance == null)
                    return;

                try
                {
                    Unit shooter = __instance.Owner;

                    if (shooter == null || !ForgeCharm.IsCharmed(shooter))
                        return;

                    ForgeBulletTurn.Adopt(__instance);
                }
                catch (Exception e)
                {
                    Log.LogError("Adopting a charmed unit's shot failed: " + e);
                }
            }
        }

        // A lobbed shot from a charmed unit needs the same treatment. It has no
        // private mask to rewrite - it collides through Unity's own matrix - so
        // the layer remap is the whole job, which Adopt already handles.
        [HarmonyPatch(typeof(PhysicsProjectile), "Shoot")]
        public class OnShootPhysics
        {
            static void Postfix(PhysicsProjectile __instance)
            {
                if (!ForgeCharm.Any || __instance == null)
                    return;

                try
                {
                    Unit shooter = __instance.Owner;

                    if (shooter == null || !ForgeCharm.IsCharmed(shooter))
                        return;

                    ForgeBulletTurn.Adopt(__instance);
                }
                catch (Exception e)
                {
                    Log.LogError("Adopting a charmed lobbed shot failed: " + e);
                }
            }
        }

        // ------------------------------------------------------------------
        // 3. "playerCanHit" - YOUR OWN FIRE STOPS PASSING THROUGH THEM.
        //
        // A charmed unit is on your faction, and `Projectile.FixedUpdate` skips
        // a hit outright when `Owner.IsFriendsWith(victim)` - so by default your
        // bullets sail straight through one. That is what he ran into asking for
        // *"an option for the player to still hit charmed units"*.
        //
        // Off unless a weapon asks, and the whole patch costs one static bool
        // read when nothing does. See ForgeCharm.PiercesFriendly for what else
        // this reaches and why the narrower designs were worse.
        //
        // Worth knowing: a LOBBED weapon could always hit them. PhysicsProjectile
        // has no friendly-fire test at all - it collides through Unity's own
        // matrix - so this switch only changes straight shots and the mod
        // features that ask the same question.
        // ------------------------------------------------------------------
        [HarmonyPatch(typeof(Unit), "IsFriendsWith")]
        public class LetYourOwnFireThrough
        {
            static void Postfix(Unit __instance, Unit unit, ref bool __result)
            {
                if (!__result || !ForgeCharm.RewritesFriendship ||
                    ForgeCharm.AskingRaw)
                {
                    return;
                }

                try
                {
                    if (ForgeCharm.PiercesFriendly(__instance, unit))
                        __result = false;
                }
                catch (Exception e)
                {
                    Log.LogError("Charm friendly-fire check failed: " + e);
                }
            }
        }

        // ------------------------------------------------------------------
        // 4. THE ROLL. Postfix, so it only fires on damage that actually landed.
        // ------------------------------------------------------------------
        [HarmonyPatch(typeof(DamagableResource), "Damage", new[] { typeof(float) })]
        public class OnDamage
        {
            static void Prefix(
                DamagableResource __instance, float amount, ref object __state)
            {
                __state = null;

                if (!ForgeCharm.Any || __instance == null || amount <= 0f)
                    return;

                try
                {
                    WeaponBase source = ForgeKillsPatch.Source;

                    if (source == null || source.TemplateData == null)
                        return;

                    ForgeCharm.Config cfg;

                    if (!ForgeCharm.TryGet(source.TemplateData, out cfg))
                        return;

                    if (__instance.IsDead)
                        return;

                    __state = new Scope
                    {
                        cfg = cfg,
                        weapon = source,
                        before = __instance.CurrentHealth
                    };
                }
                catch (Exception e)
                {
                    Log.LogError("Charm prefix failed: " + e);
                }
            }

            static void Postfix(DamagableResource __instance, object __state)
            {
                var scope = __state as Scope;

                if (scope == null || __instance == null)
                    return;

                try
                {
                    // Dead things cannot change sides, and a hit that was
                    // refused - blocked, i-framed, or the wrong damage type -
                    // should not roll. Same reasoning as execute.
                    if (__instance.IsDead ||
                        __instance.CurrentHealth >= scope.before)
                    {
                        return;
                    }

                    Unit victim = __instance.GetComponentInParent<Unit>();
                    Unit owner = (scope.weapon != null) ? scope.weapon.Owner : null;

                    if (victim == null || owner == null)
                        return;

                    if (ReferenceEquals(victim, owner))
                        return;

                    // ALREADY CHARMED IS CHECKED BEFORE THE ALLY GUARD, and the
                    // order is the whole point: a charmed enemy IS on your side
                    // now, so an ally test placed first swallows it and the
                    // refresh below becomes unreachable code. Caught by
                    // simulating the state machine, not by reading it.
                    if (ForgeCharm.IsCharmed(victim))
                    {
                        ForgeCharm.Apply(victim, owner, scope.cfg);   // refreshes
                        return;
                    }

                    // Never something already on your side - charming your own
                    // minion would flip it to the faction it is already on and
                    // then "restore" it later to the same, which is pointless
                    // work and a chance to get it wrong.
                    if (owner.IsFriendsWith(victim))
                        return;

                    // The boss guard, same shape as execute's.
                    if (scope.cfg.maxTargetHealth > 0f &&
                        __instance.MaxHealth > scope.cfg.maxTargetHealth)
                    {
                        return;
                    }

                    if (UnityEngine.Random.value > scope.cfg.chance)
                        return;

                    ForgeCharm.Apply(victim, owner, scope.cfg);
                }
                catch (Exception e)
                {
                    Log.LogError("Charm postfix failed: " + e);
                }
            }
        }
    }
}
