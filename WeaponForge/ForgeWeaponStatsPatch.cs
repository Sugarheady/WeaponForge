using System;
using System.Collections.Generic;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace WeaponForge
{
    // Shows a weapon's OWN (JSON-baked) phasing / pierce cap on its stat
    // card: "PHASING ON" and "PIERCE n". Keyed off WeaponBase.TemplateData
    // via ForgeWeaponInfo, which WeaponBuilder fills in as it applies each
    // weapon's phasing / pierceLimit. (ModuleForge separately shows the
    // phasing/pierce coming from installed MODULES.)
    //
    // WeaponBase.GetPropertyList builds the stat list for both the equipped
    // weapon and the weapon-module preview, so one postfix covers both.
    [HarmonyPatch(typeof(WeaponBase), "GetPropertyList")]
    public class ForgeWeaponStatsPatch
    {
        private static readonly ManualLogSource Log =
            ForgeLog.Source("WeaponForge");

        static void Postfix(WeaponBase __instance, List<DisplayableProperty> results)
        {
            if (results == null)
                return;

            try
            {
                WeaponData td = __instance.TemplateData;
                if (td == null)
                    return;

                Relabel(td, results);

                // When ModuleForge is installed it shows a single COMBINED
                // phasing/pierce line (weapon-baked + modules), reading our
                // baked values via reflection - so we stay quiet to avoid a
                // second line.
                //
                // Scoped to these two lines on purpose. It used to guard the
                // whole method, which would have silently swallowed every stat
                // line added here later - ModuleForge knows nothing about
                // minions, so it cannot cover for us.
                if (!ForgePierceCompat.ModuleForgePresent)
                {
                    if (ForgeWeaponInfo.IsPhasing(td))
                    {
                        results.Add(new DisplayableProperty(
                            TextFormatter.ColoredText(
                                TextFormatter.electronColor, "PHASING"),
                            "ON"));
                    }

                    int limit;
                    if (ForgeWeaponInfo.TryGetPierce(td, out limit))
                    {
                        results.Add(new DisplayableProperty(
                            TextFormatter.ColoredText(
                                TextFormatter.capsColor, "PIERCE"),
                            limit.ToString()));
                    }
                }

                Minion(td, results);
                Courier(__instance, td, results);
            }
            catch (Exception e)
            {
                Log.LogError("Weapon phasing/pierce stat failed: " + e);
            }
        }

        // "Contact Damage" reads "Damage" on a FORGE weapon's card.
        //
        // His call, R19 252: "Right now it says contact damage, when it should
        // just say damage... Can you see why its confusing? Contact damage is
        // the player ship diving into the enemy." "Contact Damage" is the GAME'S
        // label for a gun's hit damage, on every gun (`WeaponBase
        // .GetPropertyList`) - and this mod has a `contactDamage` block that
        // means exactly the other thing, which is the confusion. Stock cards
        // keep the game's own word: also his call, so a stock gun reads as the
        // game wrote it.
        //
        // A card line is a single `text`, "label value", so the relabel is a
        // prefix swap that leaves the number - and the upgrade-preview form
        // "label +delta  old > new" - exactly as the game formatted it.
        private const string GameDamageLabel = "Contact Damage";
        private const string ForgeDamageLabel = "Damage";

        private static void Relabel(
            WeaponData td, List<DisplayableProperty> results)
        {
            if (!IsForgeMade(td))
                return;

            for (int i = 0; i < results.Count; i++)
            {
                string text = results[i].text;

                if (text != null &&
                    text.StartsWith(GameDamageLabel, StringComparison.Ordinal))
                {
                    results[i] = new DisplayableProperty(
                        ForgeDamageLabel + text.Substring(GameDamageLabel.Length));
                }
            }
        }

        // Every weapon asset this mod makes is named "Forge ..." - a file's
        // weapon is "Forge Weapon <name>", and the runtime copies (wingman,
        // rank, minion guns) are "Forge <kind> ...". A card label is the one
        // place a name test is the right tool: getting it wrong costs a word,
        // and it must also cover copies made after the registry was built.
        private static bool IsForgeMade(WeaponData td)
        {
            return td != null && td.name != null &&
                   td.name.StartsWith("Forge ", StringComparison.Ordinal);
        }

        // What a swarm gun's minions actually are, on the gun's own card.
        //
        // Without this, a weapon whose entire point is the creatures it makes
        // shows nothing about them - you would have to read the log to find out
        // whether your health setting took. The standing rule is that a stat
        // you can set is a stat you can see.
        // A courier's drones a pull, boost included (R21 part 4): "+1 drone a
        // level" is a number you can only see working if the card says it.
        private static void Courier(
            WeaponBase w, WeaponData td, List<DisplayableProperty> results)
        {
            ForgeCourier.Config cfg;

            if (!ForgeCourier.TryGet(td, out cfg) || cfg == null)
                return;

            bool ring = cfg.formation == ForgeCourierRules.Formation.Ring;
            int baseCount = ring ? Mathf.Max(1, Mathf.RoundToInt(w.ProjectileCount)) : 1;
            int now = ForgeCourierRules.DronesFor(
                baseCount, cfg.dronesPerLevel, ForgeSwarm.LevelOf(w),
                ring ? Mathf.Max(baseCount, ForgeCourierRules.MaxLine) : ForgeCourierRules.MaxLine);

            results.Add(new DisplayableProperty(
                TextFormatter.ColoredText(TextFormatter.capsColor, "DRONES A PULL"),
                now.ToString() +
                (cfg.dronesPerLevel > 0 ? " +" + cfg.dronesPerLevel + "/lvl" : "")));
        }

        private static void Minion(
            WeaponData td, List<DisplayableProperty> results)
        {
            ForgeMinion.Config cfg;

            if (!ForgeMinion.TryGet(td, out cfg) || cfg == null)
                return;

            if (cfg.cap > 0)
            {
                results.Add(new DisplayableProperty(
                    TextFormatter.ColoredText(TextFormatter.capsColor, "MINIONS"),
                    cfg.cap.ToString() +
                    (cfg.capPerLevel > 0 ? " +" + cfg.capPerLevel + "/lvl" : "")));
            }

            if (cfg.lifetime > 0f)
            {
                results.Add(new DisplayableProperty(
                    "Minion lifetime",
                    cfg.lifetime.ToString("0.#") + "s"));
            }

            string mode = cfg.statMode == ForgeMinionStats.Multiply ? "x" : "";

            if (cfg.health.set)
            {
                results.Add(new DisplayableProperty(
                    "Minion health",
                    mode + cfg.health.value.ToString("0.#") +
                    (cfg.health.perLevel != 0f
                        ? " +" + cfg.health.perLevel.ToString("0.#") + "/lvl"
                        : "")));
            }

            if (cfg.speed.set)
            {
                results.Add(new DisplayableProperty(
                    "Minion speed",
                    mode + cfg.speed.value.ToString("0.#")));
            }

            if (cfg.sight.set)
            {
                results.Add(new DisplayableProperty(
                    "Minion sight",
                    mode + cfg.sight.value.ToString("0.#")));
            }

            if (cfg.hasShield)
            {
                results.Add(new DisplayableProperty(
                    "Minion shield",
                    cfg.shieldEffectiveness.ToString("0.#")));
            }

            if (cfg.untargetable)
            {
                results.Add(new DisplayableProperty(
                    "Minions", "untargetable"));
            }

            if (cfg.defaultWeaponModule != null &&
                cfg.defaultWeaponModule.weapon != null)
            {
                WeaponData gun = cfg.defaultWeaponModule.weapon;

                if (gun.explosion.radius > 0f &&
                    gun.explosion.damages != null &&
                    gun.explosion.damages.Count > 0)
                {
                    results.Add(new DisplayableProperty(
                        "Minion explosion",
                        gun.explosion.radius.ToString("0.#") + "r"));
                }

                if (gun.discharge.chainLength > 0)
                {
                    results.Add(new DisplayableProperty(
                        TextFormatter.ColoredText(
                            TextFormatter.electronColor, "Minion chain"),
                        gun.discharge.chainLength.ToString()));
                }

                if (gun.burn.Max > 0f)
                {
                    results.Add(new DisplayableProperty(
                        TextFormatter.ColoredText(
                            TextFormatter.capsColor, "Minion BURN"),
                        gun.burn.Max.ToString("0.#")));
                }
            }
        }
    }
}
