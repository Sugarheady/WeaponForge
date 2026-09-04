using System;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace WeaponForge
{
    // The hooks for "crit". See ForgeCrit for why the damage path is the right
    // place for this and why it cannot collide with ForgeDamageStack.
    public static class ForgeCritPatch
    {
        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("WeaponForge.Crit");

        // The shot currently delivering damage, so the damage prefix can reach
        // it. Set in a prefix and cleared in a finalizer on the same method -
        // the same contract ForgeKillsPatch.Source uses, and for the same
        // reason: Harmony guarantees the finalizer runs even if the original
        // throws, so a stale value can never leak into the next hit.
        private static IProjectile _shot;

        // Only logged once, because this sits on every point of damage in the
        // game and a per-hit warning would bury the log.
        private static bool _saidNoImpact;

        // ---- publish the shot ------------------------------------------------

        [HarmonyPatch(typeof(DamagableResource), "ProjectileCollided")]
        public class ShotScope
        {
            static void Prefix(IProjectile projectile)
            {
                if (ForgeCrit.Any)
                    _shot = projectile;
            }

            static void Finalizer()
            {
                _shot = null;
            }
        }

        // ---- the crit itself -------------------------------------------------

        // Priority.First is deliberate, not cosmetic. ForgeHitSoundPatch
        // prefixes this same method and READS `amount` to pitch the hit sound by
        // damage. Running first means the pitched tick reflects the CRIT rather
        // than the base hit - so "hitSound": { "pitch": "damage" } becomes crit
        // feedback for free, through machinery that already exists. Reversing
        // the order would silently take that away.
        [HarmonyPatch(typeof(DamagableResource), "Damage", new[] { typeof(float) })]
        [HarmonyPriority(Priority.First)]
        public class CritDamage
        {
            static void Prefix(DamagableResource __instance, ref float amount)
            {
                // One bool test in the common case. This runs on every point of
                // damage anything in the game takes.
                if (!ForgeCrit.Any || amount <= 0f)
                    return;

                try
                {
                    Apply(__instance, ref amount);
                }
                catch (Exception e)
                {
                    // A crit must never be the thing that stops damage landing.
                    Log.LogWarning("crit skipped: " + e.Message);
                }
            }
        }

        private static void Apply(DamagableResource victim, ref float amount)
        {
            // Which weapon is dealing this? Source covers the three ordinary
            // routes (projectile / beam / explosion) and is null for a burn
            // tick, a gas cloud or a cell collision - which is exactly the gate
            // we want, so damage-over-time never crits.
            WeaponBase weapon = ForgeKillsPatch.Source;
            bool viaExtras = false;

            if (weapon == null)
            {
                // One of this mod's OWN damage sources, if it opted in by
                // wrapping its call in ForgeCrit.Begin/End.
                weapon = ForgeCrit.ExtraWeapon;
                viaExtras = weapon != null;

                if (weapon == null)
                    return;
            }

            ForgeCrit.Config cfg;
            float chance, mult;

            if (!ForgeCrit.Resolve(weapon, out cfg, out chance, out mult))
                return;

            // Gas, swipe, ram, contact and the rest are off unless the file
            // says otherwise.
            if (viaExtras && !cfg.onExtras)
                return;

            int stacks = Decide(cfg, weapon, chance);

            if (stacks <= 0)
                return;

            float factor = ForgeCrit.Factor(stacks, mult);

            if (factor <= 1f)
                return;

            amount *= factor;

            // Feedback. The bigger impact burst needs the shot, so it only
            // happens on a projectile hit; a beam or an explosion gets the
            // sound, the tint and the shake.
            ForgeCrit.Feedback(cfg, weapon, victim, stacks);

            Projectile plain = _shot as Projectile;

            if (cfg.impact)
            {
                if (plain != null)
                {
                    ForgeCrit.BigImpact(cfg, plain, victim.transform.position);
                }
                else if (!_saidNoImpact)
                {
                    _saidNoImpact = true;
                    Log.LogInfo(
                        "crit \"impact\" is a bigger copy of the weapon's own " +
                        "impact burst, which only exists on a projectile hit - " +
                        "a beam's spark and an explosion's flash are not " +
                        "per-hit prefabs. This crit landed by another route, so " +
                        "use \"sfx\", \"color\" or \"shake\" to see those. Said " +
                        "once per run.");
                }
            }
        }

        // Which roll mode, and what did it say?
        private static int Decide(
            ForgeCrit.Config cfg, WeaponBase weapon, float chance)
        {
            if (cfg.roll == ForgeCrit.Roll.Pull)
            {
                int pull;

                if (ForgeCrit.TryGetPull(weapon, out pull))
                    return pull;

                // No pull on record - an explosion or a mine going off long
                // after the trigger. Roll rather than silently never critting.
                return ForgeCrit.Stacks(chance, cfg.overflow);
            }

            if (cfg.roll == ForgeCrit.Roll.Shot)
            {
                var plain = _shot as Component;

                if (plain != null)
                {
                    var mark = plain.gameObject
                        .GetComponent<ForgeCrit.ShotMark>();

                    if (mark == null)
                    {
                        mark = plain.gameObject
                            .AddComponent<ForgeCrit.ShotMark>();
                    }

                    if (!mark.rolled)
                    {
                        mark.rolled = true;
                        mark.stacks = ForgeCrit.Stacks(chance, cfg.overflow);
                    }

                    return mark.stacks;
                }

                // No shot to hang the decision on (a beam, an explosion). Per
                // hit is the only sane fallback.
                return ForgeCrit.Stacks(chance, cfg.overflow);
            }

            return ForgeCrit.Stacks(chance, cfg.overflow);
        }

        // ---- the per-pull roll -----------------------------------------------

        [HarmonyPatch(typeof(WeaponBase), "DoShoot")]
        public class PullRoll
        {
            static void Prefix(WeaponBase __instance)
            {
                if (!ForgeCrit.Any || __instance == null)
                    return;

                try
                {
                    ForgeCrit.Config cfg;
                    float chance, mult;

                    if (!ForgeCrit.Resolve(
                            __instance, out cfg, out chance, out mult))
                        return;

                    if (cfg.roll != ForgeCrit.Roll.Pull)
                        return;

                    ForgeCrit.RollPull(
                        __instance, ForgeCrit.Stacks(chance, cfg.overflow));
                }
                catch (Exception e)
                {
                    Log.LogWarning("crit pull roll skipped: " + e.Message);
                }
            }
        }
    }
}
