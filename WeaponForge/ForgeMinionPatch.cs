using System;
using System.Collections.Generic;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace WeaponForge
{
    // Subscribes to the game's OWN spawn event rather than intercepting a spawn.
    //
    // MinionSpawnerWeapon exposes `public event Action<MinionSpawnerWeapon,
    // Unit> OnPrepareMinion`, fired at the end of FireSingle with the freshly
    // created minion. The game's own MinionOwnerSetter uses it to call SetOwner,
    // so this is a supported extension point, not a hook found by prying.
    //
    // Why a constructor patch is the right place to subscribe: the weapon object
    // is rebuilt whenever its module cluster refreshes, so subscribing once at
    // startup would leave a stale weapon holding the subscription. Attaching in
    // the constructor means every rebuilt instance carries it.
    //
    // A CORRECTION TO WHAT THIS FILE USED TO CLAIM. It said ownership was
    // already set by the time a minion reached us, because MinionOwnerSetter
    // subscribes on WeaponChanged. That is true for the Queen and **false for
    // the player**: MinionOwnerSetter is a MonoBehaviour carrying a serialized
    // reference to its owner, and it exists on exactly three prefabs -
    // Enemy_CrossQueen and the two Floater Officers. It is NOT on the ship.
    //
    // So a spawner weapon in a player slot produced OWNERLESS minions, which is
    // the single cause of every symptom reported: no owner means nothing for the
    // follow AI to follow (they parked in a corner), and no owner means no
    // minion list, which is why no cap was possible. Setting the owner here is
    // the fix, and it is what makes the cap and the grid inheritance work at
    // all.
    public static class ForgeMinionPatch
    {
        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("WeaponForge.Minion");

        [HarmonyPatch(typeof(MinionSpawnerWeapon), MethodType.Constructor,
                      new Type[] { typeof(MinionSpawnerWeaponData) })]
        public class OnCreated
        {
            static void Postfix(MinionSpawnerWeapon __instance)
            {
                if (!ForgeMinion.Any)
                    return;

                try
                {
                    ForgeMinion.Config cfg;

                    if (!ForgeMinion.TryGet(__instance.TemplateData, out cfg))
                        return;

                    __instance.OnPrepareMinion += Prepare;
                }
                catch (Exception e)
                {
                    Log.LogError("Hooking a minion spawner failed: " + e);
                }
            }
        }

        private static void Prepare(MinionSpawnerWeapon weapon, Unit minion)
        {
            try
            {
                ForgeMinion.Config cfg;

                if (weapon == null ||
                    !ForgeMinion.TryGet(weapon.TemplateData, out cfg))
                    return;

                // Side FIRST, then the weapon. The faction decides who the AI
                // treats as an enemy, and the weapon rebuild reads the unit;
                // doing it in this order means a minion never exists, even for a
                // frame, as a thing shooting the wrong way.
                // OWN IT FIRST. Everything else depends on this: the follow
                // AI, the minion list, the cap, and kill attribution.
                // Timed, worst-first, once per weapon. See ForgeMinionCost -
                // "the Larva stutters and the drone does not" points at this
                // work rather than at the spawn, but pointing is not proving,
                // and the last thing that looked obvious in this file's log
                // turned out to be 68 ordinary spawns.
                string costName = (weapon.TemplateData != null)
                    ? weapon.TemplateData.name : "(unnamed)";

                ForgeMinionCost.Begin(costName);

                ForgeMinionCost.Stage("own", delegate { Own(weapon, minion); });

                // Side, then the weapon. The faction decides who the AI treats
                // as an enemy, and the weapon rebuild reads the unit; doing it
                // in this order means a minion never exists, even for a frame,
                // as a thing shooting the wrong way.
                ForgeMinionCost.Stage("side", delegate
                {
                    ForgeMinion.SetSide(minion, weapon.Owner, cfg);
                });

                // Right after the side, and before anything reads its AI: a
                // prefab's Vision watches ONE layer, and on any side but its
                // original that is the wrong one. Without this a minion of
                // yours can never see an enemy to shoot at.
                ForgeMinionCost.Stage("eyes", delegate
                {
                    ForgeMinionAllegiance.Eyes(minion, cfg);
                });

                // The RAM, for prefabs whose attack is a headbutt rather than
                // a gun (Larva, Maggot, Child). Same class of bug as the eyes
                // above and the ammo layer below - one serialized mask that
                // does not follow the unit's faction.
                ForgeMinionCost.Stage("ram", delegate
                {
                    ForgeMinionAllegiance.Ram(minion, cfg);
                });

                ForgeMinionCost.Stage("equip", delegate
                {
                    ForgeMinion.Equip(minion, cfg);

                    // Its own gun, tuned - before the grid inheritance, so any
                    // inherited augmentations decorate the weapon we meant.
                    ForgeMinion.EquipDefault(minion, cfg);
                });

                if (cfg.inheritGrid)
                {
                    ForgeMinionCost.Stage("inheritGrid", delegate
                    {
                        // false: ONE recalculation for the whole spawn, at the
                        // end. This used to recalculate here and again in the
                        // stats pass a moment later, discarding the first -
                        // two full tank rebuilds per minion where one does.
                        ForgeSwarm.InheritGrid(weapon, minion, false);
                    });
                }

                if (cfg.lifetime > 0f)
                    ForgeMinionLife.Attach(minion, cfg.lifetime);

                // Last, so nothing downstream rebuilds over it.
                ForgeMinionCost.Stage("look", delegate
                {
                    ForgeMinionLook.Apply(minion, cfg);
                });

                // After the look, because scaling changes the rigidbody mass
                // and the movement maths reads it. Its trailing stat
                // recalculation also repairs whatever InheritGrid disturbed.
                ForgeMinionCost.Stage("stats", delegate
                {
                    ForgeMinionStats.Apply(
                        minion, weapon.Owner, cfg, ForgeSwarm.LevelOf(weapon),
                        false);
                });

                // THE ONE RECALCULATION. Both passes above used to do their
                // own, and the first was always thrown away by the second -
                // and neither could simply drop it, because both have early
                // returns (a config with no stats never reaches the stats
                // pass at all). So the caller owns it.
                //
                // Gated, so a minion that changed NEITHER its grid nor its
                // stats pays for no rebuild at all - which is exactly what
                // happened before, and moving the call up here must not
                // quietly add work to the cheap case.
                if (cfg.inheritGrid || cfg.HasStats)
                {
                    ForgeMinionCost.Stage("recalc", delegate
                    {
                        ForgeMinionStats.Recalculate(minion);
                    });
                }

                ForgeMinionCost.Stage("blast", delegate
                {
                    ForgeMinionBlast.Attach(minion, weapon.Owner, cfg);
                    // And stop it holding a grudge against you: your own
                    // explosions carry no owner filter, so one stray blast
                    // would otherwise make it hunt you for life. See
                    // ForgeMinionGrudge.
                    ForgeMinionGrudge.Attach(minion, weapon.Owner);
                });

                // After the stats, because the escort reads followDistance and
                // leash and the stats pass is what puts the author's numbers
                // there - and after the scaling, which changes the mass the
                // force compensation divides by.
                ForgeMinionCost.Stage("escort", delegate
                {
                    ForgeMinionEscort.Attach(minion, weapon.Owner, cfg);

                    // The other stance. Only one of the two ever does anything
                    // - the escort stands down when `hold` is on - but both are
                    // offered the spawn so the choice lives in the config
                    // rather than in the order of these two lines.
                    ForgeMinionHold.Attach(minion, cfg);
                });

                // After "look", because scaling a minion replaces nothing about
                // its collider identity but a swapped-in visual child can carry
                // one - so pairing last means every collider that exists has
                // been created by the time we walk for them.
                ForgeMinionCost.Stage("solid", delegate
                {
                    ForgeMinionSolid.Apply(
                        minion, weapon.Owner, cfg, costName);
                });

                ForgeMinionCost.Stage("track", delegate
                {
                    ForgeSwarm.Track(weapon, minion);
                    ForgeSwarm.Sweep();
                });

                ForgeMinionCost.End(costName);
            }
            catch (Exception e)
            {
                Log.LogError("Preparing a minion failed: " + e);
            }
        }

        // Give the minion an owner, which nothing on the player's side does.
        //
        // Unit.SetOwner takes the owner's SavableEntity, so that is what has to
        // be found - and the connection type decides which slot the minion
        // belongs to, which matters because a drone gadget trims minions
        // matching ITS slot. ForgeSwarm.ConnectionFor falls back to Undefined,
        // which no gadget uses.
        private static void Own(MinionSpawnerWeapon weapon, Unit minion)
        {
            if (weapon.Owner == null || minion == null)
                return;

            // Already owned - the Queen's own setter got here first, or this is
            // a rebuild. Do not overwrite it.
            if (minion.Owner != null)
                return;

            var entity = weapon.Owner.GetComponent<SavableEntity>();

            if (entity == null)
                entity = weapon.Owner.GetComponentInChildren<SavableEntity>();

            if (entity == null)
            {
                Log.LogWarning(
                    "swarm: the owner has no SavableEntity, so its minions " +
                    "cannot be owned - they will have no follow AI and no cap.");
                return;
            }

            minion.SetOwner(entity, ForgeSwarm.ConnectionFor(weapon));
        }

        // ---- the cap ----
        //
        // Two hook points on purpose. Block is checked at the Shooter so the
        // RESOURCE IS NEVER TAKEN (paying for a shot that cannot spawn anything
        // reads as a bug), while KillOldest and Fizzle act at the spawn itself -
        // which also covers a spawner weapon in a GADGET slot, since those
        // bypass Shooter entirely.
        [HarmonyPatch(typeof(Shooter), "Shoot")]
        public class OnShoot
        {
            static bool Prefix(Shooter __instance)
            {
                if (!ForgeMinion.Any)
                    return true;

                try
                {
                    var spawner = __instance.Weapon as MinionSpawnerWeapon;

                    if (spawner == null)
                        return true;

                    ForgeMinion.Config cfg;

                    if (!ForgeMinion.TryGet(spawner.TemplateData, out cfg))
                        return true;

                    // THE PULL LOG, and this is the measurement R13 test 143
                    // needs rather than another guess.
                    //
                    // The R13 instrument reported a correct average rate AND a
                    // 0.017-second gap between two spawns - one frame - and I
                    // could not source that from reading the code: the cap
                    // consumes a refused pull, `Shooter.Shoot` stamps its clock
                    // on the way out, and `DoShoot` runs exactly BurstSize
                    // times. Every path I can see is once per interval.
                    //
                    // Which means the useful thing to record is the ONE
                    // distinction that splits the remaining candidates in half:
                    //
                    //   pulls 1.25s apart, spawns 0.017s apart
                    //       -> the doubling is INSIDE one pull (burst,
                    //          projectileCount, or something firing FireSingle
                    //          twice)
                    //   pulls themselves 0.017s apart
                    //       -> the fire-rate clock is not holding, and the
                    //          refusal-consumes-the-pull fix is not covering
                    //          whatever path this is
                    //
                    // Guessing between those two has cost a round each time.
                    // Recording both costs one line.
                    bool blocked = ForgeSwarm.Blocked(spawner, cfg);

                    Pull(__instance, spawner, blocked);

                    if (!blocked)
                        return true;

                    // A REFUSED PULL STILL COSTS THE PULL. Without this the
                    // swarm can spawn far faster than its own fireRate.
                    //
                    // `Shooter.FireRatePassed` is `Time.time > lastShootTime +
                    // 1/FireRate`, and only the real `Shoot` stamps
                    // lastShootTime. A prefix that returns false never reaches
                    // it - so while the cap holds, `Update` calls `Shoot()`
                    // again on EVERY frame, and the first frame a minion dies
                    // and frees a slot, one spawns immediately. That is how a
                    // fireRate of 0.8 (a pull every 1.25s) produced two spawns
                    // 0.017 seconds apart in the R12 test 3 instrument - one
                    // frame.
                    //
                    // Stamping it here makes the refusal consume the interval,
                    // so a capped swarm refills at the rate the file says and
                    // the build log's "one pull every 1.25s" is true in play.
                    //
                    // AND NOTE WHAT THE INSTRUMENT GOT WRONG: it reported "so
                    // pulls are happening faster than fireRate should allow",
                    // which is true, and pointed at fireRate, which was
                    // innocent - every value reached the game correctly. The
                    // cap was the clock. Same lesson as the last RATE CHECK:
                    // report the numbers, and be careful which mechanism the
                    // sentence blames.
                    ForgeSwarm.ConsumePull(__instance);

                    return false;
                }
                catch (Exception e)
                {
                    Log.LogError("Swarm cap check failed: " + e);
                    return true;
                }
            }
        }

        [HarmonyPatch(typeof(MinionSpawnerWeapon), "FireSingle")]
        public class OnFireSingle
        {
            static bool Prefix(MinionSpawnerWeapon __instance)
            {
                if (!ForgeMinion.Any)
                    return true;

                try
                {
                    ForgeMinion.Config cfg;

                    if (!ForgeMinion.TryGet(__instance.TemplateData, out cfg))
                        return true;

                    // MEASURE THE DECISION, NOT THE ATTEMPT.
                    //
                    // This called `Meter(__instance)` here, BEFORE the gate
                    // below - so every refused fizzle counted as a spawn and
                    // the report's own words, "N minion(s) actually spawned",
                    // were false whenever the cap refused one. Worse for the
                    // burst statistics than for the rate: a refusal costs no
                    // fire-rate interval, so two refusals a frame apart would
                    // report a 0.017s "gap between spawns" with nothing having
                    // spawned at all.
                    //
                    // Same family as the RATE CHECK measuring the template's
                    // fireRate instead of the live one. An instrument that
                    // counts the wrong event is worse than none, because the
                    // number looks like evidence.
                    bool allowed = ForgeSwarm.AllowSpawn(__instance, cfg);

                    Meter(__instance, allowed);

                    return allowed;
                }
                catch (Exception e)
                {
                    Log.LogError("Swarm spawn check failed: " + e);
                    return true;
                }
            }
        }

        // ---- an instrument, not a feature ----
        //
        // Counts what a spawner ACTUALLY produces in its first two seconds of
        // firing and prints that next to what the file asked for, once per
        // weapon per session.
        //
        // This exists because "it spawns 5 at a time and rapidly regardless of
        // what I did" is unanswerable from the code alone: the build log can
        // only report the numbers the mod wrote, and the question is precisely
        // whether the game is honouring them. One line comparing intent with
        // observation settles it, and settles which side of the fence to look
        // on. Same approach as the grapnel diagnostic.
        private class Meter_
        {
            public float started;
            public int spawns;
            public bool reported;

            // AN AVERAGE HID THE ACTUAL COMPLAINT, so these measure the burst.
            //
            // Round 11 test 3: he tapped the trigger for "a nano second", three
            // minions came out at once, and this instrument answered "4 minion(s)
            // actually spawned in 7.3s of firing ... which matches the file." All
            // of that was arithmetically true and completely failed to describe
            // what he saw - because 3-at-once followed by six idle seconds
            // averages out to exactly the right rate.
            //
            // A rate is the wrong statistic for "several pop out together". The
            // right ones are the SHORTEST gap between two spawns and the biggest
            // clump inside one frame; both are invisible to a mean.
            public float lastSpawn;
            public float shortestGap = float.MaxValue;

            // Spawns the cap turned down. Counted so the report can say so
            // rather than quietly folding them into the spawn count.
            public int refused;

            // The trigger side, recorded separately on purpose - see Pull.
            public int pulls;
            public int pullsBlocked;
            public float lastPull;
            public float shortestPullGap = float.MaxValue;
            public int sameFrame;
            public int worstSameFrame;
            public int lastFrame = -1;
        }

        private static readonly Dictionary<WeaponBase, Meter_> _meters =
            new Dictionary<WeaponBase, Meter_>();

        private const float MeterWindow = 2f;

        // Every trigger pull that reached the spawner, with its cadence.
        //
        // Deliberately a SEPARATE record from the spawn meter above, keyed to
        // the same weapon, because the whole point is to compare the two. It
        // also reports the fire-rate clock's own numbers, so a disagreement
        // between "one pull per interval" and what happened is visible rather
        // than inferred.
        private static void Pull(
            Shooter shooter, MinionSpawnerWeapon weapon, bool blocked)
        {
            try
            {
                Meter_ m;

                if (!_meters.TryGetValue(weapon, out m))
                {
                    m = new Meter_();
                    m.started = Time.time;
                    _meters[weapon] = m;
                }

                if (m.reported)
                    return;

                m.pulls++;

                if (blocked)
                    m.pullsBlocked++;

                if (m.lastPull > 0f)
                {
                    float gap = Time.time - m.lastPull;

                    if (gap < m.shortestPullGap)
                        m.shortestPullGap = gap;
                }

                m.lastPull = Time.time;
            }
            catch (Exception)
            {
                // A WARNING-ONLY READ MUST NEVER BE THE THING THAT THROWS.
                // Standing rule here since a diagnostic took five tests down
                // with it: validation is the most defensive code in a method.
            }
        }

        private static void Meter(MinionSpawnerWeapon weapon, bool allowed)
        {
            Meter_ m;

            if (!_meters.TryGetValue(weapon, out m))
            {
                m = new Meter_();
                m.started = Time.time;
                _meters[weapon] = m;
            }

            if (m.reported)
                return;

            if (!allowed)
            {
                m.refused++;
                return;
            }

            m.spawns++;

            // Same-frame clumping, and the tightest real gap.
            if (Time.frameCount == m.lastFrame)
            {
                m.sameFrame++;
            }
            else
            {
                m.sameFrame = 1;
                m.lastFrame = Time.frameCount;

                if (m.lastSpawn > 0f)
                {
                    float gap = Time.time - m.lastSpawn;

                    if (gap < m.shortestGap)
                        m.shortestGap = gap;
                }
            }

            if (m.sameFrame > m.worstSameFrame)
                m.worstSameFrame = m.sameFrame;

            m.lastSpawn = Time.time;

            if (Time.time - m.started < MeterWindow)
                return;

            m.reported = true;

            var data = weapon.TemplateData as MinionSpawnerWeaponData;

            if (data == null)
                return;

            // COMPARE AGAINST THE LIVE WEAPON, NOT THE TEMPLATE. This
            // instrument accused the spawner of ignoring fireRate - "7 spawned
            // in 2.08s where 0.4 were expected" - by measuring real spawns
            // against `TemplateData.fireRate`, which is the number in the FILE
            // before any module touched it.
            //
            // `WeaponBase.FireRate` starts as `data.fireRate` and is then
            // rewritten by every module's IWeaponModifier, so a single
            // fire-rate module makes the two disagree by design. An instrument
            // that cries wolf is worse than no instrument, and this one nearly
            // sent a whole session chasing a bug that may not exist.
            //
            // Both numbers are now printed, so the next log settles it either
            // way instead of raising the same question again.
            float liveRate = weapon.FireRate;
            float fileRate = data.fireRate;

            int perPull =
                Mathf.Max(1, Mathf.RoundToInt(weapon.ProjectileCount)) *
                Mathf.Max(1, weapon.BurstSize);

            // AND THE SAME COMPARISON FOR MINIONS-PER-PULL, which is the
            // "why did three pop out at once" question.
            //
            // A module's `IWeaponModifier.Modify` runs against whatever weapon
            // the cluster built - and on a spawner that weapon is a
            // MinionSpawnerWeapon, where `ProjectileCount` is MINIONS PER PULL
            // rather than pellets. So a "+1 projectile" module on a spawner's
            // grid does not give the drones more bullets; it gives you more
            // DRONES. (With `inheritGrid` on it does both, because the same
            // module is also installed on the drone.)
            //
            // That is a completely reasonable thing to want and a completely
            // baffling thing to hit by accident, so the instrument names it
            // rather than leaving the number unexplained.
            int filePerPull =
                Mathf.Max(1, Mathf.RoundToInt(data.projectileCount)) *
                Mathf.Max(1, data.burstSize);

            float elapsed = Mathf.Max(0.001f, Time.time - m.started);
            float expected = liveRate > 0f
                ? liveRate * elapsed * perPull
                : 0f;

            string verdict;

            if (liveRate <= 0f)
            {
                verdict =
                    "the weapon's LIVE fireRate is " +
                    liveRate.ToString("0.###") +
                    ", which cannot produce a sane interval at all";
            }
            else if (m.spawns > expected * 2f + 1f)
            {
                verdict =
                    "THAT IS FAR MORE THAN THE LIVE WEAPON ALLOWS, which is a " +
                    "real problem rather than a module doing its job - please " +
                    "report this line";
            }
            else if (perPull != filePerPull)
            {
                verdict =
                    "which matches the weapon AS MODIFIED. NOTE: your file asks " +
                    "for " + filePerPull + " minion(s) per pull and a MODULE " +
                    "has raised it to " + perPull +
                    ". On a spawner, \"projectileCount\" is MINIONS per pull - " +
                    "so a +projectiles module gives you more DRONES, not more " +
                    "bullets. That is modules working, and it is the usual " +
                    "answer to \"why did several pop out at once\"";
            }
            else if (Mathf.Abs(liveRate - fileRate) > 0.0001f)
            {
                verdict =
                    "which matches the weapon AS MODIFIED. Note the file says " +
                    fileRate.ToString("0.###") + " and a module has changed it " +
                    "to " + liveRate.ToString("0.###") +
                    " - that is modules working, not a bug";
            }
            else
            {
                verdict = "which matches the file";
            }

            Log.LogInfo(
                "minion RATE CHECK on '" + weapon.TemplateData.name + "': " +
                m.spawns + " minion(s) actually spawned in " +
                elapsed.ToString("0.##") + "s of firing. " +
                perPull + " per pull at a LIVE fireRate of " +
                liveRate.ToString("0.###") +
                (Mathf.Abs(liveRate - fileRate) > 0.0001f
                    ? " (the FILE says " + fileRate.ToString("0.###") +
                      " - a module changed it)"
                    : "") +
                " (a pull every " +
                (liveRate > 0f
                    ? (1f / liveRate).ToString("0.###")
                    : "?") +
                "s), so about " + expected.ToString("0.#") +
                " were expected - " + verdict + "." +

                // THE BURST NUMBERS, which are the ones that answer "several
                // popped out at once". Printed unconditionally and separately
                // from the verdict above, because the verdict is about the RATE
                // and can be entirely correct while the burst is the complaint.
                " BURST: biggest clump in a single frame " + m.worstSameFrame +
                ", shortest gap between spawns " +
                (m.shortestGap < float.MaxValue
                    ? m.shortestGap.ToString("0.###") + "s"
                    : "n/a (only one spawn event)") +
                ". " +
                (m.worstSameFrame > perPull
                    ? "THAT CLUMP IS BIGGER THAN " + perPull +
                      " PER PULL, so something is firing the spawner more than " +
                      "once per trigger pull - that is the bug, and it is mine."
                    : (m.shortestGap < (liveRate > 0f ? (1f / liveRate) * 0.5f : 0f)
                        ? "THE GAP IS LESS THAN HALF THE FIRE INTERVAL, so " +
                          "something is happening faster than fireRate should " +
                          "allow - see the PULLS line for which half."
                        : "Both are within what one pull per interval allows.")));

            // ---- and the half the last instrument could not see -----------
            //
            // Printed as its own line rather than appended, because it answers
            // a different question and it is the one to quote back.
            float interval = (liveRate > 0f) ? 1f / liveRate : 0f;

            bool pullsTooFast =
                interval > 0f &&
                m.shortestPullGap < float.MaxValue &&
                m.shortestPullGap < interval * 0.5f;

            Log.LogInfo(
                "minion PULLS on '" + weapon.TemplateData.name + "': " +
                m.pulls + " trigger pull(s) reached the spawner (" +
                m.pullsBlocked + " refused by the cap, " + m.refused +
                " refused at the spawn), producing " + m.spawns +
                " minion(s). Shortest gap between PULLS " +
                (m.shortestPullGap < float.MaxValue
                    ? m.shortestPullGap.ToString("0.###") + "s"
                    : "n/a") +
                " against a fire interval of " +
                (interval > 0f ? interval.ToString("0.###") + "s" : "?") +
                ". " +
                (pullsTooFast
                    ? "THE PULLS THEMSELVES ARE TOO CLOSE, so the fire-rate " +
                      "clock is not holding on this path - that is the bug and " +
                      "it is mine. Quote this line."
                    : (m.worstSameFrame > perPull ||
                       (m.shortestGap < interval * 0.5f && interval > 0f)
                        ? "The pulls are correctly spaced, so the doubling is " +
                          "INSIDE one pull - burstSize, projectileCount, or " +
                          "something calling the spawner twice per shot. That " +
                          "narrows it to my side too, and to a different half. " +
                          "Quote this line."
                        : "Pulls and spawns both match the fire rate.")));
        }
    }
}
