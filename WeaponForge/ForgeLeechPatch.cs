using System;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace WeaponForge
{
    // The payout half of "leech". **The MEASUREMENT half now lives in
    // `ForgeDamageDealt`** - this registers as a reader of it.
    //
    // It used to own its own prefix/postfix pair on
    // `DamagableResource.Damage(float)`, snapshotting the victim's tank and
    // diffing it, because the number passed IN is not the damage dealt
    // (`Damage` refuses it three silent ways). `surge` later copied that code
    // verbatim, and when `rank` wanted the same number there were about to be
    // three byte-identical copies on the game's hottest method. One owner now;
    // the reasoning is all in `ForgeDamageDealt`.
    //
    // ★ WHAT THIS READER DELIBERATELY IGNORES, so that moving the measurement
    // changed no behaviour at all:
    //
    //   - **`viaBurn`.** Burn ticks were previously unattributable - nothing
    //     was on the stack to say which weapon lit the fire - so leech has
    //     never healed off burn. `ForgeIgnition` can now name that weapon, and
    //     handing those hits to leech would silently start healing every burn
    //     weapon already written. Rank opts in; leech does not, and turning it
    //     on would be a new key rather than a quiet change.
    public static class ForgeLeechPatch
    {
        private static readonly ManualLogSource Log =
            ForgeLog.Source("WeaponForge.Leech");

        public static void Register()
        {
            ForgeDamageDealt.Subscribe(
                "leech", () => ForgeLeech.Any, OnHit);
        }

        private static void OnHit(ForgeDamageDealt.Hit hit)
        {
            // See the header: burn is out, exactly as it was when a burn tick
            // could not be attributed at all.
            if (hit.viaBurn)
                return;

            try
            {
                Pay(hit);
            }
            catch (Exception e)
            {
                // A leech must never be the thing that breaks damage.
                Log.LogWarning("leech skipped: " + e.Message);
            }
        }

        private static void Pay(ForgeDamageDealt.Hit hit)
        {
            DamagableResource victim = hit.part;

            // The weapon's own file PLUS every module contribution, so a leech
            // module can grant leech to a weapon that never asked for it.
            ForgeLeech.Config cfg;
            float fraction, flat, cap, perEnemyDelay;

            if (!ForgeLeech.Resolve(hit.weapon, out cfg, out fraction, out flat,
                                    out cap, out perEnemyDelay))
                return;

            // The mod's own direct damage (gas, swipe, ram, slam...) is opt-in,
            // and that is unchanged - it is just asked one step later now.
            if (hit.viaExtras && !cfg.onExtras)
                return;

            float dealt = hit.dealt;

            if (!cfg.EnemyReady(hit.victim, perEnemyDelay))
                return;

            float wanted = dealt * fraction + flat;

            if (wanted <= 0f)
                return;

            float allowed = cfg.Take(wanted, cap);

            if (allowed <= 0f)
                return;

            Unit player = hit.attacker;

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
