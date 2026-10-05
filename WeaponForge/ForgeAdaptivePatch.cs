using System;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace WeaponForge
{
    // Swaps the damage type on its way in.
    //
    // HOOKED ON `TakeDamage(Damage)`, NOT ON `Damage(float)`. Every other
    // behaviour in this mod that touches damage - crit, leech, execute, charm -
    // hangs off `Damage(float)`, because that is the single chokepoint where the
    // amount is finally applied. This one cannot: by the time a float arrives,
    // the type has already been resolved into a number and thrown away.
    // `TakeDamage` is one step earlier and still holds the whole `Damage`
    // struct, which is the only place the type is still a live decision.
    //
    // `ref Damage damage` is what makes it work at all - Damage is a STRUCT, so
    // a by-value prefix parameter would modify a copy and change nothing.
    public static class ForgeAdaptivePatch
    {
        private static readonly ManualLogSource Log =
            ForgeLog.Source("WeaponForge.Adaptive");

        [HarmonyPatch(typeof(DamagableResource), "TakeDamage",
            new[] { typeof(Damage) })]
        public class OnTakeDamage
        {
            static void Prefix(DamagableResource __instance, ref Damage damage)
            {
                if (!ForgeAdaptive.Any || __instance == null ||
                    damage.amount <= 0f)
                {
                    return;
                }

                try
                {
                    // No weapon behind it (a burn tick, a gas cloud, a cell
                    // collision) means nothing to look a config up on, so those
                    // keep whatever type they had. Same rule as crit.
                    WeaponBase source = ForgeKillsPatch.Source;

                    if (source == null || source.TemplateData == null)
                        return;

                    ForgeAdaptive.Config cfg;

                    if (!ForgeAdaptive.TryGet(source.TemplateData, out cfg))
                        return;

                    if (__instance.IsDead)
                        return;

                    // The defender's own tank is what the table is keyed on.
                    Resource defender = __instance.resource;

                    if (defender == null)
                        return;

                    float gain;
                    Resource pick =
                        ForgeAdaptive.Best(cfg, defender, damage.damageType, out gain);

                    if (pick == null)
                        return;   // nothing better - leave the struct alone

                    damage.damageType = pick;

                    if (cfg.tint)
                        Flash(__instance, pick, cfg.tintSeconds,
                              cfg.tintStrength, cfg.tintMode);
                }
                catch (Exception e)
                {
                    Log.LogError("Adaptive damage type failed: " + e);
                }
            }
        }

        // Flash the victim in the chosen type's own colour, so a switch is
        // legible. `Resource.color` is a ColorAsset with an implicit cast, and
        // it can be null on a resource that never shows in the HUD - hence the
        // guard rather than assuming.
        private static void Flash(
            DamagableResource victim, Resource type, float seconds,
            float strength, int mode)
        {
            try
            {
                Unit unit = victim.GetComponentInParent<Unit>();

                if (unit == null || type == null || type.color == null)
                    return;

                // Its own tint layer, so a prism flash and a crit flash on the
                // same hit compose instead of one restoring over the other.
                ForgeUnitTint.Set(
                    unit, "adaptive", type.color,
                    Mathf.Max(0.05f, seconds), 0f, strength, mode);
            }
            catch (Exception e)
            {
                Log.LogWarning("Adaptive tint failed: " + e.Message);
            }
        }
    }
}
