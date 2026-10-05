using System;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace WeaponForge
{
    // The two hooks `surge` needs that `overheat` does not have.
    //
    //   1. FILLING THE METER BY HITTING THINGS. `overheat` patches exactly one
    //      method - `Shooter.Shoot` - and nothing on the damage path, so "the
    //      meter fills when you land hits" had nowhere to live.
    //   2. ARMING EVERY SHOT IN THE WINDOW as a charged one, which has to
    //      happen before `DoShoot` reads the arming.
    //
    // ★ WHY THE FILL HOOK IS ON `DamagableResource.Damage` AND NOT ON
    // `Projectile.TryHit`.
    //
    // The design notes named `TryHit` plus `OnHitByHitscanWeapon` - two hooks,
    // one per weapon shape. Reading the code settled on one hook instead, and
    // it is strictly better on three counts:
    //
    //   - **Every damage route funnels through `Damage(float)`** - projectiles,
    //     lobbed shots, beams, explosions, missiles - so one patch covers what
    //     two would have covered, plus the two they would have missed.
    //   - **`fill: "hits"` and `fill: "damage"` become the same measurement.**
    //     A hit is a Damage call that achieved something; damage is how much it
    //     achieved. Counting `TryHit` calls would count hits the victim
    //     REFUSED - `Damage` refuses three silent ways (an active damage
    //     blocker, no tank of that resource type, i-frames) - so a gun shooting
    //     something immune to it would have surged on contact.
    //   - It is `ForgeLeechPatch`'s measurement exactly, already proven, and
    //     the same one `rank` wanted.
    //
    // **That last bullet is why the measurement no longer lives here.** It was
    // a verbatim copy of leech's, and rank was about to make a third; it is
    // `ForgeDamageDealt` now and this is a reader of it.
    //
    // ★ WHAT THIS READER DELIBERATELY IGNORES, so that the move changed no
    // behaviour: `viaExtras` and `viaBurn`. The old `Open` read
    // `ForgeKillsPatch.Source` and nothing else, so the mod's own direct damage
    // (gas, swipe, ram, slam) and burn ticks have never filled a surge meter.
    // Passing them through now would silently change every surge weapon already
    // written - and a meter that fills from a gas cloud you left behind is a
    // different feature, not a better one.
    public static class ForgeSurgePatch
    {
        private static readonly ManualLogSource Log =
            ForgeLog.Source("WeaponForge.Surge");

        public static void Register()
        {
            ForgeDamageDealt.Subscribe(
                "surge", () => ForgeSurge.AnyHitFill, OnHit);
        }

        private static void OnHit(ForgeDamageDealt.Hit hit)
        {
            // See the header. Only a real shot fills a surge meter.
            if (hit.viaExtras || hit.viaBurn)
                return;

            try
            {
                Fill(hit);
            }
            catch (Exception e)
            {
                // A meter must never be the thing that breaks damage.
                Log.LogWarning("surge fill skipped: " + e.Message);
            }
        }

        private static void Fill(ForgeDamageDealt.Hit hit)
        {
            ForgeSurge.Config cfg;

            if (!ForgeSurge.TryGet(hit.weapon.TemplateData, out cfg) ||
                cfg == null || !cfg.enabled || !cfg.FillsOnHitOrDamage)
            {
                return;
            }

            ForgeHeat heat = ForgeHeat.FindFor(hit.weapon);

            if (heat == null)
                return;

            // NOTHING TO GAIN DURING THE WINDOW unless the file asked for a
            // refill. This used to sit above the tank snapshot to skip it;
            // with one shared snapshot serving every reader there is nothing
            // left to skip, so it is simply the first thing asked.
            if (heat.Surging && !cfg.refill)
                return;

            float gain = 0f;

            if (cfg.fillOnHit)
                gain += cfg.perHit;

            if (cfg.fillOnDamage)
                gain += hit.dealt * cfg.perDamage;

            if (gain > 0f)
                heat.AddFromHit(gain);
        }

        // ------------------------------------------------------------------
        // Every shot in the window is a charged shot
        // ------------------------------------------------------------------
        //
        // A PREFIX, because `Shooter.Shoot` calls `weapon.Fire` synchronously
        // for the first shot of a burst and the arming has to be in place
        // before `DoShoot` looks for it. It returns void, so it cannot suppress
        // anything and cannot interact with the three features that prefix this
        // same method and DO return false.
        [HarmonyPatch(typeof(Shooter), "Shoot")]
        public class ArmSurgeShot
        {
            static void Prefix(Shooter __instance)
            {
                if (!ForgeSurge.Any || __instance == null)
                    return;

                try
                {
                    ForgeHeat heat = __instance.GetComponent<ForgeHeat>();

                    if (heat == null || !heat.Surging)
                        return;

                    float level;

                    if (!heat.WantsChargedShot(out level))
                        return;

                    if (!ForgeChargePatch.ArmExternally(__instance, level))
                        WarnNoCharge();
                }
                catch (Exception e)
                {
                    Log.LogError("Arming a surge shot failed: " + e);
                }
            }
        }

        // A once-per-session gate, named to the convention
        // `ForgeDiagnosticGates` sweeps - so the second run of a session can
        // still produce the warning.
        private static bool _saidNoCharge;

        private static void WarnNoCharge()
        {
            if (_saidNoCharge)
                return;

            _saidNoCharge = true;

            Log.LogWarning(
                "surge \"chargedShots\" needs a \"charge\" block on the same " +
                "weapon - that block IS the set of multipliers a charged shot " +
                "scales by (damage, size, speed, explosion and the rest), so " +
                "there is nothing to scale without it. The surge's other " +
                "rewards are unaffected.");
        }
    }
}
