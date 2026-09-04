using System;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace WeaponForge
{
    // Finishes a victim that a weapon with an `execute` block has brought below
    // its threshold. See ForgeExecute for why this hook, and why the kill
    // credits itself.
    public static class ForgeExecutePatch
    {
        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("WeaponForge.Execute");

        // Carried prefix -> postfix through Harmony's `__state`, never a static.
        //
        // `Damage` NESTS: it can call `Die()`, whose spawn-on-death explosion
        // reaches `OnExplosion` and back into `Damage` before the outer call has
        // returned. A static would be overwritten by the inner hit and the outer
        // one would measure the wrong thing. `__state` is per-invocation and
        // cannot be. Same reasoning as ForgeLeechPatch, which shares this method.
        private class Scope
        {
            public ForgeExecute.Config cfg;
            public WeaponBase weapon;
            public float before;
        }

        [HarmonyPatch(typeof(DamagableResource), "Damage", new[] { typeof(float) })]
        public class OnDamage
        {
            static void Prefix(
                DamagableResource __instance, float amount, ref object __state)
            {
                __state = null;

                if (!ForgeExecute.Any || __instance == null || amount <= 0f)
                    return;

                try
                {
                    // No weapon behind this damage means a burn tick, a gas
                    // cloud or a cell collision. Those cannot execute, and that
                    // falls out rather than being a rule: `ForgeKillsPatch.Source`
                    // is only live inside the three real damage routes.
                    WeaponBase source = ForgeKillsPatch.Source;

                    if (source == null || source.TemplateData == null)
                        return;

                    ForgeExecute.Config cfg;

                    if (!ForgeExecute.TryGet(source.TemplateData, out cfg))
                        return;

                    if (__instance.IsDead || __instance.IsInvincible)
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
                    Log.LogError("Execute prefix failed: " + e);
                }
            }

            static void Postfix(DamagableResource __instance, object __state)
            {
                var scope = __state as Scope;

                if (scope == null || __instance == null)
                    return;

                try
                {
                    // Already killed by the damage itself. The game's own branch
                    // (`if (!IsDead && tank.Value <= 0f) Die();`) runs inside
                    // Damage and returns early - but a postfix still runs after
                    // an early return, so this guard is doing real work.
                    if (__instance.IsDead || __instance.IsInvincible)
                        return;

                    float now = __instance.CurrentHealth;

                    // THE HIT HAS TO HAVE LANDED. `Damage` refuses damage three
                    // silent ways - a damage blocker, no tank of that type, or
                    // i-frames - and on any of them the tank is untouched.
                    // Without this test a weapon could execute something it did
                    // not actually hurt, which reads as the threshold firing at
                    // random.
                    if (now >= scope.before)
                        return;

                    float max = __instance.MaxHealth;

                    // MaxHealth is 0 when the unit has no tank of this resource,
                    // and CurrentHealth would then read 0 too - which would look
                    // like "already dead" and execute everything it touched.
                    if (max <= 0f)
                        return;

                    if (!ForgeExecute.ShouldFinish(scope.cfg, now, max))
                        return;

                    Unit victim = __instance.GetComponentInParent<Unit>();
                    Unit owner = (scope.weapon != null) ? scope.weapon.Owner : null;

                    if (victim != null && owner != null)
                    {
                        // Never yourself, and never your own side - a minion
                        // caught in your own blast must not be executed by your
                        // gun. `IsFriendsWith` is the game's own question, the
                        // same one the targeting finders ask.
                        if (ReferenceEquals(victim, owner) ||
                            owner.IsFriendsWith(victim))
                        {
                            return;
                        }
                    }

                    ForgeExecute.Feedback(scope.cfg, __instance);

                    // Through the game's own Die(), so `IsDead` is set before
                    // control returns to ProjectileCollided / OnHitByHitscanWeapon
                    // / OnExplosion - each of which then credits the kill itself.
                    __instance.Die();
                }
                catch (Exception e)
                {
                    Log.LogError("Execute postfix failed: " + e);
                }
            }
        }
    }
}
