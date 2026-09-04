using System;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace WeaponForge
{
    // The hook for "leech". One prefix/postfix pair on
    // `DamagableResource.Damage(float)` covers projectiles, lobbed shots, beams,
    // explosions and missiles at once, because every damage route in the game
    // funnels into that one method.
    //
    // WHY A PREFIX **AND** A POSTFIX, when crit needs only a prefix. Crit
    // changes the damage on its way in, so it works on the number. Leech has to
    // know what the number ACHIEVED, and `Damage` can refuse it three ways
    // without saying so (damage blockers, no tank of that resource, i-frames).
    // So the prefix snapshots the victim's tank and the postfix measures what
    // came off. That also makes leech honest about overkill and free of any
    // ordering worry: whatever crit or anything else did to `amount`, the tank
    // delta is the truth.
    public static class ForgeLeechPatch
    {
        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("WeaponForge.Leech");

        // Carried from prefix to postfix through Harmony's __state rather than a
        // static. That is not tidiness: `Damage` NESTS - it can call `Die()`,
        // whose spawn-on-death explosion reaches `OnExplosion` and back into
        // `Damage` before the outer call returns. A static snapshot would be
        // overwritten by the inner hit and the outer one would measure the wrong
        // delta. __state is per-invocation and cannot be.
        private class Scope
        {
            public WeaponBase weapon;
            public ForgeLeech.Config cfg;
            public float fraction;
            public float flat;
            public float cap;
            public float perEnemyDelay;
            public ResourceTank tank;
            public Unit victim;
            public float before;
        }

        [HarmonyPatch(typeof(DamagableResource), "Damage", new[] { typeof(float) })]
        public class LeechDamage
        {
            static void Prefix(
                DamagableResource __instance, float amount, ref object __state)
            {
                __state = null;

                // One bool test in the common case. This runs on every point of
                // damage anything in the game takes.
                if (!ForgeLeech.Any || amount <= 0f)
                    return;

                try
                {
                    __state = Open(__instance);
                }
                catch (Exception e)
                {
                    Log.LogWarning("leech snapshot skipped: " + e.Message);
                }
            }

            static void Postfix(DamagableResource __instance, object __state)
            {
                var scope = __state as Scope;

                if (scope == null)
                    return;

                try
                {
                    Close(__instance, scope);
                }
                catch (Exception e)
                {
                    // A leech must never be the thing that breaks damage.
                    Log.LogWarning("leech skipped: " + e.Message);
                }
            }
        }

        private static Scope Open(DamagableResource victim)
        {
            // Which weapon is dealing this? Source covers the three ordinary
            // routes and is null for a burn tick, a gas cloud or a cell
            // collision - the free gate that keeps damage-over-time out.
            WeaponBase weapon = ForgeKillsPatch.Source;
            bool viaExtras = false;

            if (weapon == null)
            {
                weapon = ForgeLeech.ExtraWeapon;
                viaExtras = weapon != null;

                if (weapon == null)
                    return null;
            }

            // The weapon's own file PLUS every module contribution, so a leech
            // module can grant leech to a weapon that never asked for it.
            ForgeLeech.Config cfg;
            float fraction, flat, cap, perEnemyDelay;

            if (!ForgeLeech.Resolve(weapon, out cfg, out fraction, out flat,
                                    out cap, out perEnemyDelay))
                return null;

            if (viaExtras && !cfg.onExtras)
                return null;

            // You cannot leech off yourself, and a weapon that heals its owner
            // for shooting their own hull would be a strange kind of infinite.
            Unit owner = weapon.Owner;

            if (owner == null)
                return null;

            Unit hurt = victim.GetComponentInParent<Unit>();

            if (hurt == null || hurt == owner)
                return null;

            // `DamagableResource.Tank` resolves through Unit.GetTank, which is
            // not guarded - ask HasTank first, exactly as Damage() itself does
            // before touching the tank.
            if (!hurt.HasTank(victim.resource))
                return null;

            ResourceTank tank = hurt.GetTank(victim.resource);

            if (tank == null)
                return null;

            return new Scope
            {
                weapon = weapon,
                cfg = cfg,
                fraction = fraction,
                flat = flat,
                cap = cap,
                perEnemyDelay = perEnemyDelay,
                tank = tank,
                victim = hurt,
                before = tank.Value
            };
        }

        private static void Close(DamagableResource victim, Scope scope)
        {
            if (scope.tank == null)
                return;

            float dealt = scope.before - scope.tank.Value;

            // Nothing landed: blocked, i-framed, or the wrong element entirely.
            if (dealt <= 0f)
                return;

            // OVERKILL DOES NOT PAY. `ResourceTank.Value` has no floor - the
            // game writes `tank.Value -= amount` - so a 200-damage rocket into a
            // 5-health fly leaves the tank at -195 and the raw delta reads 200.
            // Clamped to what the victim actually had, so a leech weapon cannot
            // full-heal you off a gnat.
            if (scope.before > 0f)
                dealt = Mathf.Min(dealt, scope.before);

            ForgeLeech.Config cfg = scope.cfg;

            if (!cfg.EnemyReady(scope.victim, scope.perEnemyDelay))
                return;

            float wanted = dealt * scope.fraction + scope.flat;

            if (wanted <= 0f)
                return;

            float allowed = cfg.Take(wanted, scope.cap);

            if (allowed <= 0f)
                return;

            Unit player = scope.weapon.Owner;

            Resource resource = (cfg.resourceAsset != null)
                ? cfg.resourceAsset
                : ForgeLeech.HealthOf(player);

            if (resource == null)
                return;

            bool onArrival =
                cfg.EffectiveHealOn == ForgeLeech.HealOn.Arrival;

            // The orb, when there is room for one. Over the cap the healing is
            // granted immediately rather than dropped: a firehose should cost
            // you the flourish, never the health.
            bool flew = false;

            if (cfg.orb && ForgeLeech.ClaimOrb(cfg.maxOrbs))
            {
                var orb = ForgeLeechOrb.Spawn(
                    victim.transform.position,
                    player,
                    cfg,
                    resource,
                    onArrival ? allowed : 0f,
                    victim);

                if (orb != null)
                    flew = true;
                else
                    ForgeLeech.ReleaseOrb();
            }

            if (!flew || !onArrival)
            {
                ForgeLeech.Heal(player, resource, allowed);

                // With no orb to carry it, the sound belongs to the hit. When an
                // orb IS flying, the orb plays it on arrival instead, so the cue
                // lands with the health rather than ahead of it.
                if (!flew && !string.IsNullOrEmpty(cfg.sfxGuid))
                {
                    try
                    {
                        AudioManager.PlaySfx(
                            cfg.sfxGuid, victim.transform.position);
                    }
                    catch (Exception)
                    {
                    }
                }
            }
        }
    }
}
