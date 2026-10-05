using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using BepInEx.Logging;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace WeaponForge
{
    // The live half of `rank`: earning XP, ranking up, and paying out.
    //
    // XP IS DAMAGE ACTUALLY REMOVED FROM A TANK, read from `ForgeDamageDealt`
    // rather than measured again here. That matters more than it looks:
    // `DamagableResource.Damage(float)` refuses damage three silent ways before
    // it touches a tank, so the number passed in is not the damage dealt - and
    // a rank that counted it would level fastest against things immune to it.
    //
    // ★ EVERYTHING HERE IS PER COPY OF THE GUN (ForgeRankCopies). The XP lands
    // on the copy that fired, the stats go on that copy's live gun, an unlock
    // edits that copy's weapon, and the card reads that copy's rank.
    //
    // ★ THE PAYOUT IS TWO DIFFERENT MECHANISMS AND ONLY ONE OF THEM IS
    // EXPENSIVE.
    //
    //   - **Stats** are per-rank multipliers published to `ForgeWeaponStats`
    //     under the key "rank". One dictionary write per stat. No rebuild.
    //   - **Features** are unlocked by switching a dormant block ON in the
    //     copy's own JSON and re-running `WeaponBuilder.ApplyFeaturesTo` on the
    //     copy's own weapon.
    //
    // So the chain is re-run ONLY when the JSON actually changed. A rank that
    // pays stats alone costs nothing but the writes, which is what makes
    // `maxRank: 9999` survivable.
    public static class ForgeRankPatch
    {
        private static readonly ManualLogSource Log =
            ForgeLog.Source("WeaponForge.Rank");

        private const string StatKey = "rank";

        public static void Register()
        {
            ForgeDamageDealt.Subscribe("rank", () => ForgeRank.Any, OnHit);
        }

        // ------------------------------------------------------------------
        // Earning
        // ------------------------------------------------------------------
        private static void OnHit(ForgeDamageDealt.Hit hit)
        {
            try
            {
                Earn(hit);
            }
            catch (Exception e)
            {
                Log.LogWarning("rank XP skipped: " + e.Message);
            }
        }

        private static void Earn(ForgeDamageDealt.Hit hit)
        {
            ForgeRank.Config cfg;

            if (!ForgeRank.TryGet(hit.weapon, out cfg) || cfg == null ||
                !cfg.enabled)
            {
                return;
            }

            // ★ HIS ANSWER, 2026-09-18: damage-over-time earns XP like any
            // other damage, so a burn or gas build levels at the same speed.
            // Both channels are the same switch - a gas cloud and a burn tick
            // are the same kind of thing to the person holding the gun.
            if ((hit.viaExtras || hit.viaBurn) && !cfg.fromDamageOverTime)
                return;

            // ★ A WINGMAN'S SHOT RANKS THE GUN IT MIRRORS. The controller
            // builds each copy with `Equip(shipUnit)`, so `hit.attacker` is the
            // PLAYER, and ForgeRankCopies.TryGet follows a wingman's weapon back
            // to the copy of the gun it was cloned from. `fromWingmen` defaults
            // to true and this is the only place that has to ask.
            if (!cfg.fromWingmen &&
                ForgeWingmanWeapon.IsCopy(hit.weapon.TemplateData))
            {
                return;
            }

            // CRATES SELF-LIMIT AND STILL HAVE A SWITCH. With damage as the
            // metric a crate pays in proportion to its (small) health, so
            // scenery can never be a grind - but he stated a preference for
            // "only actual threatening enemies", so the discriminator is here.
            // All 60 fighting units carry an AIAgent; none of the 13 containers
            // does.
            if (!cfg.countObjects &&
                hit.victim.GetComponentInParent<AIAgent>() == null)
            {
                return;
            }

            ForgeRankCopies.Copy copy;

            // A ranked weapon with no copy is one this mod never saw being
            // built - an enemy carrying a Forge gun, say. It has no rank.
            if (!ForgeRankCopies.TryGet(hit.weapon, out copy))
                return;

            Award(hit.attacker, copy, hit.dealt);
        }

        // The one door into progress, shared with the station and pickup XP.
        public static void Award(
            Unit ship, ForgeRankCopies.Copy copy, float amount)
        {
            if (ship == null || copy == null || copy.cfg == null ||
                copy.p == null || amount <= 0f)
            {
                return;
            }

            ForgeRank.Config cfg = copy.cfg;
            ForgeRank.Progress p = copy.p;

            int id = ForgeWingmanSave.IdOf(ship);

            // Remembered here so a pickup draw can find the ship again without
            // walking every unit in the level.
            ForgeRank.Seen(id, ship);
            copy.shipId = id;

            if (p.rank >= cfg.maxRank)
                return;

            p.xp += amount;
            p.dirty = true;

            int gained = 0;

            // ★ MANY RANK-UPS IN ONE FRAME BATCH INTO ONE PAYOUT. A flood of XP
            // - a rocket into a crowd, a gas cloud on a dozen enemies - can
            // cross several thresholds in a single Damage call, and paying each
            // one separately would re-run the ~50-call feature chain once per
            // rank and stack that many rows in the ship log.
            while (p.rank < cfg.maxRank)
            {
                float need = ForgeRank.Required(cfg, p.rank);

                // A percent curve at a high maxRank eventually needs more XP
                // than a float can hold. It stops here rather than looping.
                if (float.IsInfinity(need) || need <= 0f || p.xp < need)
                    break;

                p.xp -= need;
                p.rank++;
                gained++;

                // One owed draw per rank. The draw itself may be deferred (the
                // pickup mode only spawns when none are outstanding), which is
                // why this is a counter rather than an immediate choice.
                ForgeRankDraw.Owe(copy);
            }

            if (gained <= 0)
                return;

            Apply(copy, ship);
            Announce(copy, gained);
        }

        // ------------------------------------------------------------------
        // Paying out
        // ------------------------------------------------------------------
        //
        // Stats onto every live gun built from this copy, then the unlocks
        // onto its weapon. The gun that EARNED the rank may be a wingman's
        // copy, so the stats are not put on "the weapon that hit" - they go on
        // the gun you are holding.
        public static void Apply(ForgeRankCopies.Copy copy, Unit ship)
        {
            if (copy == null || copy.cfg == null || copy.p == null)
                return;

            try
            {
                foreach (WeaponBase gun in ForgeRankCopies.LiveGuns(copy, ship))
                    Stats(gun, copy.cfg, copy.p);
            }
            catch (Exception e)
            {
                Log.LogWarning("rank stats skipped: " + e.Message);
            }

            // The unlock, NEXT FRAME. A rank up usually arrives from inside the
            // game's own damage call - a beam's cast, a projectile's hit - and
            // the unlock rebuilds the gun that is still on the stack firing:
            // disposing a HitscanWeapon from inside its own FireSingle is an
            // exception in the game's code on the way back out. A frame later
            // nothing is mid-shot.
            ForgeRankCopies.RebuildNextFrame(copy);
        }

        public static void Stats(
            WeaponBase weapon, ForgeRank.Config cfg, ForgeRank.Progress p)
        {
            if (weapon == null || cfg == null || p == null)
                return;

            foreach (var pair in cfg.stats)
            {
                float m = ForgeRank.AtRank(pair.Value, p.rank);

                // A MULTIPLIER OF 1 WITHDRAWS rather than being published,
                // exactly as `ForgeRewards` does. A pinned base is invisible in
                // play and stops a legitimate change to the weapon - a module
                // augmentation between engagements - from being picked up.
                if (Mathf.Abs(m - 1f) > 0.0001f)
                    ForgeWeaponStats.Set(weapon, pair.Key, StatKey, m);
                else
                    ForgeWeaponStats.Clear(weapon, pair.Key, StatKey);
            }

            // Raising the projectile count behind a beam's back throws every
            // frame the barrel moves - `HitscanWeapon.OnBarrelMoved` indexes
            // `visualsInstances[num]` with NO bounds guard - and leaving
            // orphaned visuals above the count is a beam frozen in mid-air.
            if (cfg.stats.ContainsKey(ForgeWeaponStats.Pellets))
            {
                try
                {
                    weapon.InitializeVisuals();
                }
                catch (Exception)
                {
                    // A weapon that cannot rebuild its visuals still fires.
                }
            }
        }

        // Turn on everything this copy has drawn, and apply every repeat-draw
        // deepening, in THIS COPY's JSON. Returns true when the JSON changed.
        public static bool Features(ForgeRank.Config cfg, ForgeRank.Progress p)
        {
            if (cfg == null || p == null || p.json == null || p.draws.Count == 0)
                return false;

            bool moved = false;

            foreach (var pair in p.draws)
            {
                string feature = pair.Key;
                int times = pair.Value;

                var block = p.json[feature] as JObject;

                if (block == null)
                    continue;

                // THE UNLOCK ITSELF. A pooled block is authored normally and
                // left dormant; drawing it is one boolean.
                if ((bool?)block["enabled"] != true)
                {
                    block["enabled"] = true;
                    moved = true;
                }

                // ...and every repeat past the first deepens it.
                if (times > 1 && Deepen(cfg, block, feature, times - 1))
                    moved = true;
            }

            return moved;
        }

        // ★ REPEAT DRAWS DEEPEN THE FEATURE'S OWN NUMBERS, and they are applied
        // from the AUTHORED value every time rather than compounded off the
        // last result. That is not a style choice: percentages must SUM (three
        // 20% draws are +60%, not 1.2 cubed - `ForgeKillBuff`'s rule), and a
        // read-modify-write against the live value cannot do that. It is also
        // the read-your-own-previous-output trap that made overheat's muzzle
        // tint converge on fully-hot in a second.
        private static bool Deepen(
            ForgeRank.Config cfg, JObject block, string feature, int repeats)
        {
            Dictionary<string, ForgeRank.Delta> fields;

            if (!cfg.onRepeat.TryGetValue(feature, out fields) || fields == null)
                return false;

            bool moved = false;

            foreach (var f in fields)
            {
                JToken authored = Authored(cfg, feature, f.Key);

                if (authored == null)
                    continue;

                float baseValue;

                try
                {
                    baseValue = (float)authored;
                }
                catch (Exception)
                {
                    continue;
                }

                float want = f.Value.percent
                    ? baseValue * (1f + f.Value.amount * repeats / 100f)
                    : baseValue + f.Value.amount * repeats;

                var now = block[f.Key];

                if (now != null && Mathf.Abs((float)now - want) < 0.0001f)
                    continue;

                block[f.Key] = want;
                moved = true;
            }

            return moved;
        }

        // The value the FILE was written with, kept separately because the live
        // JSON is what we have been editing.
        private static readonly Dictionary<string, JToken> _authored =
            new Dictionary<string, JToken>();

        public static void Remember(
            string file, string feature, string field, JToken value)
        {
            if (value == null)
                return;

            _authored[file + "\u0001" + feature + "\u0001" + field] =
                value.DeepClone();
        }

        public static JToken Authored(
            ForgeRank.Config cfg, string feature, string field)
        {
            JToken t;

            return _authored.TryGetValue(
                cfg.fileName + "\u0001" + feature + "\u0001" + field, out t)
                ? t
                : null;
        }

        // ------------------------------------------------------------------
        // Feedback
        // ------------------------------------------------------------------
        private static void Announce(ForgeRankCopies.Copy copy, int gained)
        {
            ForgeRank.Config cfg = copy.cfg;

            if (!cfg.log)
                return;

            try
            {
                // Through `ForgeBuffHud`, which already owns the ship-log row
                // and rewrites it IN PLACE - as a NOTICE, which takes itself
                // down. R19 252: "the alert for the rank number is still
                // permenant. It needs to go away. Have it stay for like 3 to 4
                // seconds ... No point for it to say the rank forever since you
                // can look at it on the card itself."
                ForgeBuffHud.Notice(
                    copy.Key,
                    new Color(1f, 0.85f, 0.35f),
                    ShownName(copy) + " RANK " +
                        copy.p.rank.ToString(CultureInfo.InvariantCulture),
                    gained,
                    NoticeSeconds);
            }
            catch (Exception)
            {
                // A readout must never be able to stop a gun ranking up.
            }

            if (!string.IsNullOrEmpty(cfg.sfx))
            {
                try
                {
                    AudioManager.PlaySfx(cfg.sfx);
                }
                catch (Exception)
                {
                }
            }
        }

        // "3 to 4 seconds".
        private const float NoticeSeconds = 4f;

        private static string ShownName(ForgeRankCopies.Copy copy)
        {
            try
            {
                string n = copy.module != null ? copy.module.DisplayName : null;

                return string.IsNullOrEmpty(n) ? copy.cfg.fileName : n.ToUpperInvariant();
            }
            catch (Exception)
            {
                return copy.cfg.fileName;
            }
        }

        // ------------------------------------------------------------------
        // Putting a rank back onto a rebuilt weapon
        // ------------------------------------------------------------------
        [HarmonyPatch(typeof(WeaponBase), "Equip")]
        public class OnEquip
        {
            static void Postfix(WeaponBase __instance)
            {
                if (!ForgeRank.Any)
                    return;

                try
                {
                    ForgeRankCopies.Copy copy;

                    if (!ForgeRankCopies.TryGet(__instance, out copy) ||
                        copy.cfg == null || !copy.cfg.enabled)
                    {
                        return;
                    }

                    // A WINGMAN's copy of the gun is built from it, and is
                    // equipped to the ship too - it is not the gun, and the
                    // stats belong on the gun.
                    if (ForgeWingmanWeapon.IsCopy(__instance.TemplateData))
                        return;

                    Unit owner = __instance.Owner;

                    if (owner != null)
                    {
                        copy.shipId = ForgeWingmanSave.IdOf(owner);
                        ForgeRank.Seen(copy.shipId, owner);
                    }

                    if (copy.p.rank > 0)
                        Stats(__instance, copy.cfg, copy.p);

                    // Choices owed from before a save come back with the gun.
                    ForgeRankDraw.Resume(copy);
                }
                catch (Exception e)
                {
                    Log.LogWarning("rank re-apply skipped: " + e.Message);
                }
            }
        }

        // ------------------------------------------------------------------
        // The weapon card
        // ------------------------------------------------------------------
        //
        // `WeaponBase.GetPropertyList` builds the stat list for BOTH the
        // equipped card and the shop, and it is where `ForgeWeaponStatsPatch`
        // already appends - including the merged line when Module Forge is
        // installed, which is exactly his *"unless its an upgrade module from
        // ModuleForge"* case, handled for free.
        //
        // R19 252: "The weapon card with the stats, should list all the stats
        // and features the rank weapon has." So besides the rank itself it
        // lists what the rank has done (the stat multipliers it is paying
        // right now) and what it has unlocked - and, before anything is drawn,
        // what it CAN unlock, so a new copy's card is not blank about it.
        [HarmonyPatch(typeof(WeaponBase), "GetPropertyList")]
        public class OnPropertyList
        {
            static void Postfix(
                WeaponBase __instance, List<DisplayableProperty> results)
            {
                if (!ForgeRank.Any || results == null)
                    return;

                try
                {
                    ForgeRank.Config cfg;

                    if (!ForgeRank.TryGet(__instance, out cfg) ||
                        !cfg.enabled || !cfg.show)
                    {
                        return;
                    }

                    ForgeRankCopies.Copy copy;
                    ForgeRank.Progress p =
                        ForgeRankCopies.TryGet(__instance, out copy) ? copy.p : null;

                    Card(cfg, p, results);
                }
                catch (Exception)
                {
                    // A card line must never stop a card drawing.
                }
            }
        }

        private static void Card(
            ForgeRank.Config cfg, ForgeRank.Progress p,
            List<DisplayableProperty> results)
        {
            int rank = (p != null) ? p.rank : 0;

            string text =
                rank.ToString(CultureInfo.InvariantCulture) + " / " +
                cfg.maxRank.ToString(CultureInfo.InvariantCulture);

            // AT THE CAP THE PROGRESS NUMBER IS A LIE, so it is not printed -
            // "400 / 400" on a gun that can never rank again reads as one hit
            // away.
            if (rank < cfg.maxRank)
            {
                float need = ForgeRank.Required(cfg, rank);

                if (!float.IsInfinity(need))
                {
                    text += "   " +
                        Mathf.FloorToInt(p != null ? p.xp : 0f).ToString(
                            CultureInfo.InvariantCulture) + " / " +
                        Mathf.CeilToInt(need).ToString(
                            CultureInfo.InvariantCulture) + " xp";
                }
            }

            results.Add(new DisplayableProperty("Rank", text));

            // What the rank is paying RIGHT NOW. The stats above already
            // include it (Fire Rate, Damage...), so this says how much of each
            // number is the rank.
            var bonus = new List<string>();

            foreach (var pair in cfg.stats)
            {
                float m = ForgeRank.AtRank(pair.Value, rank);

                if (Mathf.Abs(m - 1f) > 0.0001f)
                    bonus.Add(ForgeRank.StatLabel(pair.Key) + " x" +
                              m.ToString("0.##", CultureInfo.InvariantCulture));
            }

            if (bonus.Count > 0)
                results.Add(new DisplayableProperty(
                    "Rank bonus", string.Join(", ", bonus.ToArray())));

            if (cfg.unlocks.Count == 0)
                return;

            var taken = new List<string>();

            if (p != null)
            {
                foreach (var d in p.draws)
                    taken.Add(d.Key.ToUpperInvariant() +
                              (d.Value > 1 ? " x" + d.Value : ""));
            }

            if (taken.Count > 0)
            {
                results.Add(new DisplayableProperty(
                    "Unlocked", string.Join(", ", taken.ToArray())));
            }

            // A drawn feature never leaves the pool (a repeat deepens it), so
            // the pool is always the whole list - and it is the answer to
            // "what can this gun still become?".
            if (rank < cfg.maxRank)
            {
                results.Add(new DisplayableProperty(
                    "Can unlock",
                    string.Join(", ", cfg.unlocks.ToArray()).ToUpperInvariant() +
                    (cfg.draw == ForgeRank.Draw.Pickup
                        ? "  (you pick)"
                        : cfg.draw == ForgeRank.Draw.Random
                            ? "  (random)"
                            : "  (in order)")));
            }

            // ★ THE KEY, ON THE GUN (R20 252): "I would make a note on the
            // weapon itself ... of the key to use to move the choices." Only
            // on a gun whose rank puts choices out at all.
            if (cfg.draw == ForgeRank.Draw.Pickup)
            {
                results.Add(new DisplayableProperty(
                    "Choices",
                    "take with USE; " + ForgeRankKeys.Describe() +
                    " puts them away / brings them back"));
            }
        }
    }
}
