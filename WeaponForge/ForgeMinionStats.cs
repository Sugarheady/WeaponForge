using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using BepInEx.Logging;
using HarmonyLib;
using MyBox;
using UnityEngine;

namespace WeaponForge
{
    // Change what a spawned minion IS - how tough, how fast, how far it sees.
    //
    // THE ONE FACT THE WHOLE FILE IS BUILT AROUND, and it is not obvious:
    // **a unit's health is not a field, it is a resource tank, and its capacity
    // is rebuilt from scratch every time stats recalculate.**
    // `Unit.Data.RecalculateStats` sets `Capacity = 0` on every non-shared tank
    // and then asks the module grid to re-accumulate it. So the public and
    // inviting `Unit.IncreaseCapacity(resource, amount)` is not a way to give a
    // minion more health - it is a value that gets zeroed within a frame or two.
    //
    // Worse than useless, in fact: the recalculation rebuilds `Capacity` but
    // does NOT refill `Value`, so a raw write leaves `Value > Capacity`, and
    // `ResourceRecharger` only runs when a tank is NOT full - so nothing ever
    // corrects it. The failure mode is a corrupt tank, not a no-op.
    //
    // The durable route is to change the INPUT to that sum rather than its
    // output: the `ModifyResourceCapacity` effects on the module the minion
    // already carries. Every recalculation then reproduces our number instead
    // of eating it, and no repeated re-application is needed.
    //
    // That is also exactly what the game does to itself. `Enemy.Initialize`
    // scales `modifyResourceCapacity.delta.baseValue` by `coopResourceMultiplier`
    // on its DeepCopy, and `ModuleGridOwner.Data.RestoreFromMemento` does it
    // again after a load. Per-instance effect editing is the blessed pattern,
    // not a workaround.
    //
    // WHY THE EFFECTS ARE SAFE TO EDIT. `Module`'s constructor Clones every
    // effect off the ModuleData and points `.Module` at itself, and `FloatSeries`
    // is a struct. So the effects hanging off an installed Module are private to
    // that one unit. The ScriptableObject behind them is NOT - `ModuleData.effects`
    // is a public List on a shared asset, and `Module Embedded CrossJock` alone
    // is referenced by seven prefabs, so a write there would buff unrelated
    // enemies for the rest of the process.
    public static class ForgeMinionStats
    {
        private static readonly ManualLogSource Log =
            ForgeLog.Source("WeaponForge.Minion");

        public const int Absolute = 0;   // "health": 60 means 60 (default)
        public const int Multiply = 1;   // "health": 2 means twice the prefab's

        // A minion fast enough to leave the loaded area deletes itself: every
        // drone prefab ships isUnloadable + destroyWhenUnloaded, and the loaded
        // region is a fixed block of segments around the CAMERA. Past its edge
        // the entity is unloaded and destroyed, which reads exactly like "my
        // minion expired for no reason". So the factor is clamped, loudly.
        private const float MaxSpeedFactor = 5f;
        private const float MinSpeedFactor = 0.1f;

        // Above this push delay a mover is LUNGING, not cruising: it spends most
        // of its time coasting between shoves. Raising the shove makes it jump
        // further rather than travel faster, so for those we shorten the gap
        // instead. Either way the ratio changes by exactly the same factor.
        private const float LungeDelay = 1f;

        public static int ParseMode(string s)
        {
            switch ((s ?? "").Trim().ToLowerInvariant())
            {
                case "multiply":
                case "multiplier":
                case "scale":
                case "relative":
                    return Multiply;
                default:
                    return Absolute;
            }
        }

        public static string ModeWord(int mode)
        {
            return mode == Multiply ? "multiplier" : "absolute";
        }

        // One authored number. `set` matters because 0 is a meaningful value for
        // several of these (iFrames 0, speed 0) and "absent" has to differ from
        // "zero" or a file that mentions nothing would flatten everything.
        public struct Tweak
        {
            public bool set;
            public float value;
            public float perLevel;

            public bool Scales { get { return set && perLevel != 0f; } }
        }

        // ------------------------------------------------------------------
        // `spawnerLevel` is the level of the module that MADE this minion - the
        // gadget or the swarm gun - not the minion's own. That distinction is
        // the whole point: a BoosterCore raises the spawner's level, and the
        // minion's internal module knows nothing about it. Reading the minion's
        // own level would make every per-level setting a silent no-op.
        // A RECALCULATION IS EXPENSIVE AND THE CALLER MAY WANT TO OWN IT.
        //
        // `RecalculateStats` zeroes and rebuilds every tank from the module
        // effects, and the spawn path used to run it TWICE per minion: once at
        // the end of `ForgeSwarm.InheritGrid` and again here, microseconds
        // later, with the first result thrown away. That is dead work by
        // construction rather than by measurement.
        //
        // It cannot simply be deleted from one of them, because both have
        // early returns - `Apply` bails immediately when a config has no stats
        // at all (Kamikaze), and then InheritGrid's is the only one that runs.
        // So the honest fix is for whoever calls both to do it once, and
        // `ForgeMinionPatch.Prepare` does.
        public static void Recalculate(Unit minion)
        {
            Recalculate(minion, true);
        }

        // ★ `refill: false` IS FOR A UNIT THAT HAS JUST BEEN LOADED, and the
        // distinction is the whole point of persisting a body rather than a
        // count.
        //
        // `Unit.Data.Memento` stores `resourceValues` for every non-shared
        // tank, so a saved drone comes back AT THE HEALTH IT HAD - which is
        // exactly what "load the state we were in" means and what a count
        // rebuild can never do. Recalculating with a refill immediately after
        // that throws the restored value away and hands back a full-health
        // companion, so the persistence would be indistinguishable from a
        // respawn.
        //
        // Capacities still have to be rebuilt, because the effects we edit do
        // NOT survive a load: `Module.Memento.Restore()` resolves the module
        // data out of the registry and `DeepCopy()`s it, so a restored module
        // carries the ASSET's effects and not ours. That is the same reason
        // `ForgeMinionRestore` exists at all.
        //
        // The clamp is the third thing the refill was quietly doing.
        // `ResourceTank.Value` has no ceiling of its own, and a file that
        // LOWERS a capacity between saving and loading would otherwise leave
        // `Value > Capacity` - which `ResourceRecharger` never corrects,
        // because it only runs on a tank that is not full.
        public static void Recalculate(Unit minion, bool refill)
        {
            if (minion == null)
                return;

            try
            {
                var gridOwner =
                    minion.GetComponentInChildren<ModuleGridOwner>(true);

                if (gridOwner == null || gridOwner.ModuleGrid == null)
                    return;

                minion.ComponentData.RecalculateStats(gridOwner.ModuleGrid);

                if (refill)
                {
                    minion.ComponentData.RefillResources();
                    return;
                }

                foreach (ResourceTank tank in minion.GetNotSharedTanks())
                {
                    if (tank != null && tank.Value > tank.Capacity)
                        tank.Value = tank.Capacity;
                }
            }
            catch (Exception e)
            {
                Log.LogError(
                    "minion stats: recalculating '" + minion.name +
                    "' failed, so its tanks may be inconsistent: " + e);
            }
        }

        public static void Apply(
            Unit minion, Unit owner, ForgeMinion.Config cfg, int spawnerLevel)
        {
            Apply(minion, owner, cfg, spawnerLevel, true);
        }

        public static void Apply(
            Unit minion, Unit owner, ForgeMinion.Config cfg, int spawnerLevel,
            bool recalculate)
        {
            if (minion == null || cfg == null || !cfg.HasStats)
                return;

            try
            {
                // Movement first, because its factor is also what keeps the
                // pathfinder honest (see Arrival).
                float k = Movement(minion, cfg);

                if (k != 1f)
                    Arrival(minion, k);

                Ai(minion, cfg, k);
                Body(minion, cfg);
                Tanks(minion, cfg, Mathf.Max(1, spawnerLevel), recalculate);
            }
            catch (Exception e)
            {
                Log.LogError("Applying a minion's stats failed: " + e);
            }
        }

        // ---- MOVEMENT ----------------------------------------------------
        //
        // No reflection here, and that is worth saying because the fields are
        // all private: `UnitMovement` exposes `GetCurrentValues()` and
        // `ApplyOverride(object)` publicly, and the ValueOverride structs they
        // trade in are public with public fields. Reading the current values,
        // editing the struct and applying it back is the game's own supported
        // route - the AI state machine uses precisely this to give each state
        // its own speed.
        //
        // WHICH MEANS THE TIMING IS ALREADY RIGHT. `MovementAction.OnEnable`
        // snapshots `GetCurrentValues()` and `OnDisable` restores that snapshot
        // wholesale. A write made BEFORE the first AI state activates is
        // therefore adopted as the baseline and survives every transition; a
        // write made mid-state is reverted when that state exits. Both spawn
        // hooks sit in the safe window - Awake has run, `StateMachine.Awake` has
        // disabled every state child, and no Start has happened yet. This is
        // why stats must never be applied from a coroutine or a per-frame patch.
        private static float Movement(Unit minion, ForgeMinion.Config cfg)
        {
            // turnSpeed on its own has to get in here too. It used to be
            // unreachable without `speed`, which made it a silent no-op - the
            // exact failure this file is full of warnings about.
            if (!cfg.speed.set && !cfg.turnSpeed.set)
                return 1f;

            UnitMovement[] movers =
                minion.GetComponentsInChildren<UnitMovement>(true);

            // includeInactive is MANDATORY, not defensive. The state machine
            // deactivates every state child during Awake, and a unit carries one
            // mover per state - the stock drone has five. Without the flag this
            // finds none of them and logs nothing at all.
            if (movers == null || movers.Length == 0)
            {
                Log.LogWarning(
                    "minion " +
                    (cfg.speed.set ? "\"speed\"" : "\"turnSpeed\"") +
                    ": '" + minion.name + "' has no movement component, so it " +
                    "cannot be made faster or slower. The crates, boxes, " +
                    "turrets and beacons are all like this - they are " +
                    "stationary by design. Every other stat still applied.");

                return 1f;
            }

            float k = 1f;

            if (!cfg.speed.set)
            {
                // turnSpeed only. Fall through to the sweep with no speed
                // change, so the swimming branch still gets its torque.
                Steer(minion, cfg, movers);
                return 1f;
            }

            if (cfg.statMode == Multiply)
            {
                k = cfg.speed.value;
            }
            else
            {
                float cruise = Cruise(movers);

                if (cruise <= 0f)
                {
                    Log.LogWarning(
                        "minion \"speed\": could not read a base speed off '" +
                        minion.name + "', so an absolute target has nothing to " +
                        "aim at. Use \"statMode\": \"multiply\" for this one.");

                    return 1f;
                }

                k = cfg.speed.value / cruise;
            }

            if (k <= 0f)
            {
                Log.LogWarning(
                    "minion \"speed\": a value of " +
                    cfg.speed.value.ToString("0.##") + " works out as a " +
                    "factor of zero or less. Ignored.");

                return 1f;
            }

            float wanted = k;
            k = Mathf.Clamp(k, MinSpeedFactor, MaxSpeedFactor);

            if (!Mathf.Approximately(wanted, k))
            {
                Log.LogWarning(
                    "minion \"speed\": x" + wanted.ToString("0.##") +
                    " clamped to x" + k.ToString("0.##") + ". Past roughly " +
                    "this point a minion outruns the loaded area around the " +
                    "camera, and the game UNLOADS AND DESTROYS it - which looks " +
                    "like your minion randomly expiring. It also outruns its " +
                    "own pathfinder.");
            }

            int pushed = 0;
            int swum = 0;
            int lunged = 0;

            foreach (UnitMovement mover in movers)
            {
                if (mover == null)
                    continue;

                // Switch on the concrete type: both ApplyOverride
                // implementations THROW ArgumentException when handed the other
                // one's struct, so a blanket sweep would take the whole spawn
                // down with it.
                var push = mover as PushMovement;

                if (push != null)
                {
                    try
                    {
                        var v = (PushMovement.ValueOverride)
                            push.GetCurrentValues();

                        if (Mean(v.pushDelay) >= LungeDelay)
                        {
                            // A lunger: shove no harder, just more often.
                            v.pushDelay = Scale(v.pushDelay, 1f / k);
                            lunged++;
                        }
                        else
                        {
                            v.pushForce = Scale(v.pushForce, k);
                        }

                        push.ApplyOverride(v);
                        pushed++;
                    }
                    catch (Exception e)
                    {
                        Log.LogWarning(
                            "minion \"speed\": one PushMovement on '" +
                            minion.name + "' refused the change (" + e.Message +
                            "). The others still applied.");
                    }

                    continue;
                }

                var swim = mover as SwimmingMovement;

                if (swim != null)
                {
                    try
                    {
                        var v = (SwimmingMovement.ValueOverride)
                            swim.GetCurrentValues();

                        v.movementForce = v.movementForce * k;

                        // maxSpeed IS NOT A SPEED. `Move()` compares it against
                        // `linearVelocity.sqrMagnitude`, so it is in squared
                        // units - k times the speed needs k SQUARED here. Scaling
                        // it linearly would quietly cap a "3x faster" minion at
                        // about 1.7x.
                        v.maxSpeed = v.maxSpeed * k * k;

                        swim.ApplyOverride(v);
                        swum++;
                    }
                    catch (Exception e)
                    {
                        Log.LogWarning(
                            "minion \"speed\": one SwimmingMovement on '" +
                            minion.name + "' refused the change (" + e.Message +
                            "). The others still applied.");
                    }
                }
            }

            Log.LogInfo(
                "minion speed x" + k.ToString("0.##") + " on '" + minion.name +
                "' - " + pushed + " push mover(s)" +
                (lunged > 0
                    ? " (" + lunged + " of them lunge, so their push DELAY was " +
                      "shortened instead of their force raised)"
                    : "") +
                ", " + swum + " swimming mover(s). Every state was scaled by " +
                "the same factor, so the prefab keeps its own fast/slow spread.");

            Steer(minion, cfg, movers);

            return k;
        }

        // turnSpeed, which only the swimming movers have. Its own method so it
        // is reachable with or without a speed change.
        private static void Steer(
            Unit minion, ForgeMinion.Config cfg, UnitMovement[] movers)
        {
            if (!cfg.turnSpeed.set)
                return;

            int done = 0;

            foreach (UnitMovement mover in movers)
            {
                var swim = mover as SwimmingMovement;

                if (swim == null)
                    continue;

                try
                {
                    var v = (SwimmingMovement.ValueOverride)
                        swim.GetCurrentValues();

                    v.torque = cfg.statMode == Multiply
                        ? v.torque * cfg.turnSpeed.value
                        : cfg.turnSpeed.value;

                    swim.ApplyOverride(v);
                    done++;
                }
                catch (Exception)
                {
                }
            }

            if (done > 0)
            {
                Log.LogInfo(
                    "minion turnSpeed applied to " + done +
                    " swimming mover(s) on '" + minion.name + "'.");
                return;
            }

            Log.LogWarning(
                "minion \"turnSpeed\": '" + minion.name + "' steers with " +
                "PushMovement, which has no turn rate - it shoves itself " +
                "toward the target and its \"maxAngle\" is random scatter, " +
                "not a turn speed. Ignored. turnSpeed only affects the " +
                "swimming units (the Maggot, Fish and Queen Child family).");
        }

        // The slowest sustained state is what a person means by "how fast is
        // it" - the sprint is the exception, not the identity. So the absolute
        // target is measured against the smallest force we can find, and every
        // state is then scaled by the one factor, which keeps the prefab's
        // deliberate fast/slow spread intact.
        private static float Cruise(UnitMovement[] movers)
        {
            float best = 0f;

            foreach (UnitMovement mover in movers)
            {
                var push = mover as PushMovement;

                if (push != null)
                {
                    try
                    {
                        var v = (PushMovement.ValueOverride)
                            push.GetCurrentValues();

                        float mean = Mean(v.pushForce);

                        if (mean > 0f && (best == 0f || mean < best))
                            best = mean;
                    }
                    catch (Exception)
                    {
                        // A mover we cannot read is one we cannot use as a
                        // reference. Not an error - the others may work.
                    }
                }
            }

            if (best > 0f)
                return best;

            // No push movers - fall back to the swimming thrust.
            foreach (UnitMovement mover in movers)
            {
                var swim = mover as SwimmingMovement;

                if (swim != null)
                {
                    try
                    {
                        var v = (SwimmingMovement.ValueOverride)
                            swim.GetCurrentValues();

                        if (v.movementForce > 0f &&
                            (best == 0f || v.movementForce < best))
                        {
                            best = v.movementForce;
                        }
                    }
                    catch (Exception)
                    {
                    }
                }
            }

            return best;
        }

        // ---- the pathfinder guard ----------------------------------------
        //
        // Not exposed, because nobody would think to set it and it is not
        // optional. `MovementAction.Update` advances to the next waypoint only
        // once it is within `arrivalDistance`, and most Move actions build their
        // path in OnEnable and never again. A faster minion overshoots that
        // radius inside one physics step and then orbits waypoint one until some
        // timeout rescues it - which reads as "fast minions have broken AI".
        // Scaling the radius by the same factor costs nothing and removes the
        // whole failure mode.
        private static void Arrival(Unit minion, float k)
        {
            MovementAction[] actions =
                minion.GetComponentsInChildren<MovementAction>(true);

            if (actions == null || actions.Length == 0)
                return;

            int done = 0;

            foreach (MovementAction action in actions)
            {
                if (action == null)
                    continue;

                action.arrivalDistance = action.arrivalDistance * k;
                done++;
            }

            if (done > 0)
            {
                Log.LogInfo(
                    "minion speed - waypoint arrival distance scaled x" +
                    k.ToString("0.##") + " on " + done + " movement action(s), " +
                    "so a faster minion still follows its path instead of " +
                    "circling the first waypoint.");
            }
        }

        // ---- AI numbers ---------------------------------------------------
        private static void Ai(Unit minion, ForgeMinion.Config cfg, float k)
        {
            if (cfg.sight.set)
                Sight(minion, cfg);

            if (cfg.attackRange.set)
                AttackRange(minion, cfg);

            // Leash scales WITH speed even when the author did not ask. A
            // minion that travels further per second but is recalled at the
            // same distance flips between chase and regroup on the spot.
            if (cfg.leash.set || k > 1f)
                Leash(minion, cfg, k);

            if (cfg.followDistance.set)
                FollowDistance(minion, cfg);
        }

        private static void Sight(Unit minion, ForgeMinion.Config cfg)
        {
            var vision = minion.GetComponentInChildren<Vision>(true);

            if (vision == null)
            {
                Log.LogWarning(
                    "minion \"sight\": '" + minion.name + "' has no Vision " +
                    "component, so it has no detection range to change.");
                return;
            }

            float was = vision.Range;

            vision.Range = cfg.statMode == Multiply
                ? was * cfg.sight.value
                : cfg.sight.value;

            // Two AI conditions can each override the vision radius with their
            // own private number, in which case the Vision range we just set is
            // ignored for that check. Clearing them is what makes "sight"
            // actually mean sight.
            int cleared =
                ClearVisionOverride(minion, "EnemyVisibleCondition") +
                ClearVisionOverride(minion, "TargetVisibleCondition");

            Log.LogInfo(
                "minion sight " + was.ToString("0.##") + " -> " +
                vision.Range.ToString("0.##") + " on '" + minion.name + "'" +
                (cleared > 0
                    ? ", and " + cleared + " AI condition(s) that carried " +
                      "their own vision radius were cleared so they use it too"
                    : "") + ".");
        }

        private static int ClearVisionOverride(Unit minion, string typeName)
        {
            Type t = TypeOf(typeName);

            if (t == null)
                return 0;

            FieldInfo f = Field(t, "overrideVisionRadius");

            if (f == null || f.FieldType != typeof(float))
                return 0;

            int done = 0;

            foreach (Component c in minion.GetComponentsInChildren(t, true))
            {
                if (c == null)
                    continue;

                try
                {
                    if ((float)f.GetValue(c) != 0f)
                    {
                        f.SetValue(c, 0f);
                        done++;
                    }
                }
                catch (Exception)
                {
                }
            }

            return done;
        }

        private static void AttackRange(Unit minion, ForgeMinion.Config cfg)
        {
            Type t = TypeOf("ShootAction");
            FieldInfo f = t == null ? null : Field(t, "shootingDistance");

            if (f == null)
            {
                Warn("attackRange", "ShootAction.shootingDistance");
                return;
            }

            int done = 0;

            foreach (Component c in minion.GetComponentsInChildren(t, true))
            {
                if (c == null)
                    continue;

                try
                {
                    var range = (MinMaxFloat)f.GetValue(c);

                    // The MIN is a hold-fire floor on several prefabs (do not
                    // shoot closer than this), so scaling both ends keeps the
                    // shape of the window instead of turning it inside out.
                    f.SetValue(c, cfg.statMode == Multiply
                        ? Scale(range, cfg.attackRange.value)
                        : new MinMaxFloat(
                            range.Min <= 0f ? 0f : cfg.attackRange.value *
                                (range.Min / Mathf.Max(range.Max, 0.0001f)),
                            cfg.attackRange.value));

                    done++;
                }
                catch (Exception e)
                {
                    Log.LogWarning(
                        "minion \"attackRange\": one ShootAction refused the " +
                        "change (" + e.Message + ").");
                }
            }

            if (done > 0)
            {
                Log.LogInfo(
                    "minion attackRange applied to " + done +
                    " shoot action(s) on '" + minion.name + "'.");
            }
            else
            {
                Log.LogWarning(
                    "minion \"attackRange\": '" + minion.name + "' has no " +
                    "ShootAction, so it has no firing range to change (it may " +
                    "not shoot at all).");
            }
        }

        private static void Leash(Unit minion, ForgeMinion.Config cfg, float k)
        {
            Type t = TypeOf("OwnerIsWithinRangeCondition");
            FieldInfo f = t == null ? null : Field(t, "maxDistance");

            if (f == null)
                return;

            float factor = cfg.leash.set && cfg.statMode == Multiply
                ? cfg.leash.value
                : k;

            int done = 0;

            foreach (Component c in minion.GetComponentsInChildren(t, true))
            {
                if (c == null)
                    continue;

                try
                {
                    float was = (float)f.GetValue(c);

                    f.SetValue(c, cfg.leash.set && cfg.statMode == Absolute
                        ? cfg.leash.value
                        : was * factor);

                    done++;
                }
                catch (Exception)
                {
                }
            }

            if (done == 0)
            {
                if (cfg.leash.set)
                {
                    Log.LogWarning(
                        "minion \"leash\": '" + minion.name + "' does not " +
                        "follow its owner at all, so it has no leash. Only the " +
                        "drone family does - most spawnables just fight " +
                        "whatever is nearby.");
                }

                return;
            }

            Log.LogInfo(
                "minion leash applied to " + done + " condition(s) on '" +
                minion.name + "'" +
                (!cfg.leash.set
                    ? " - scaled with \"speed\" automatically, so a faster " +
                      "minion is not recalled mid-chase"
                    : "") + ".");
        }

        private static void FollowDistance(Unit minion, ForgeMinion.Config cfg)
        {
            Type t = TypeOf("MoveAroundOwnerAction");
            FieldInfo f = t == null ? null : Field(t, "distance");

            if (f == null)
            {
                Warn("followDistance", "MoveAroundOwnerAction.distance");
                return;
            }

            int done = 0;

            foreach (Component c in minion.GetComponentsInChildren(t, true))
            {
                if (c == null)
                    continue;

                try
                {
                    var d = (MinMaxFloat)f.GetValue(c);

                    f.SetValue(c, cfg.statMode == Multiply
                        ? Scale(d, cfg.followDistance.value)
                        : new MinMaxFloat(
                            cfg.followDistance.value,
                            cfg.followDistance.value +
                                Mathf.Max(0f, d.Max - d.Min)));

                    done++;
                }
                catch (Exception)
                {
                }
            }

            if (done > 0)
            {
                Log.LogInfo(
                    "minion followDistance applied to " + done +
                    " orbit action(s) on '" + minion.name + "'.");
            }
            else
            {
                Log.LogWarning(
                    "minion \"followDistance\": '" + minion.name + "' has no " +
                    "orbit-the-owner behaviour, so there is no follow distance " +
                    "to set. Only the drone family orbits its owner.");
            }
        }

        // ---- the body -----------------------------------------------------
        private static void Body(Unit minion, ForgeMinion.Config cfg)
        {
            if (cfg.iFrames.set)
            {
                var dmg = minion.GetComponentInChildren<DamagableResource>(true);

                if (dmg != null)
                {
                    dmg.iFrameDuration = Mathf.Max(0f, cfg.iFrames.value);

                    Log.LogInfo(
                        "minion iFrames " +
                        dmg.iFrameDuration.ToString("0.###") + "s on '" +
                        minion.name + "' - a hard cap on how often anything can " +
                        "damage it, which makes it tougher against fast weapons " +
                        "specifically rather than tougher overall.");
                }
            }

            if (cfg.hasUntargetable && minion.ComponentData != null)
            {
                minion.ComponentData.IsInvisible = cfg.untargetable;

                Log.LogInfo(
                    "minion untargetable=" + cfg.untargetable + " on '" +
                    minion.name + "' - enemy Vision skips it entirely, so it " +
                    "is never shot AT. It can still be caught by explosions " +
                    "and anything else that does not aim.");
            }

            if (cfg.fireproof.set && minion.ComponentData != null)
            {
                try
                {
                    Unit.Data.BurnProperties bp =
                        minion.ComponentData.burnProperties;

                    // Cooling is what resistance means here: the burn level
                    // falls faster than it is applied. The threshold is how
                    // much burn it takes to catch fire in the first place.
                    if (cfg.statMode == Multiply)
                    {
                        bp.coolingSpeed = bp.coolingSpeed * cfg.fireproof.value;
                        bp.fireThreshold =
                            bp.fireThreshold * cfg.fireproof.value;
                    }
                    else
                    {
                        bp.coolingSpeed = cfg.fireproof.value;
                    }

                    minion.ComponentData.burnProperties = bp;

                    Log.LogInfo(
                        "minion fireproof on '" + minion.name +
                        "' - cooling speed now " +
                        bp.coolingSpeed.ToString("0.##") + ".");
                }
                catch (Exception e)
                {
                    Log.LogWarning(
                        "minion \"fireproof\": could not set burn properties (" +
                        e.Message + ").");
                }
            }
        }

        // ---- health, tanks, regen, shields -------------------------------
        private static void Tanks(
            Unit minion, ForgeMinion.Config cfg, int spawnerLevel,
            bool recalculate)
        {
            if (!cfg.health.set && cfg.resources == null && cfg.regen == null &&
                !cfg.hasShield && !cfg.fuelForLifetime)
            {
                return;
            }

            var gridOwner = minion.GetComponentInChildren<ModuleGridOwner>(true);

            if (gridOwner == null || gridOwner.ModuleGrid == null)
            {
                Log.LogWarning(
                    "minion stats: '" + minion.name + "' has no module grid, " +
                    "so its health and resources cannot be changed - in this " +
                    "game those ARE module effects, there is no health field to " +
                    "write. (The crates and boxes are like this.)");
                return;
            }

            Module carrier = FindCarrier(gridOwner.ModuleGrid);

            if (carrier == null)
            {
                Log.LogWarning(
                    "minion stats: '" + minion.name + "' carries no module to " +
                    "hang effects on, so health and resources cannot be " +
                    "changed.");
                return;
            }

            if (carrier.Effects == null)
                carrier.Effects = new List<ModuleEffect>();

            // ONCE PER MODULE INSTANCE, and this guard is load-bearing.
            //
            // A minion's GameObject and its DATA have different lifetimes. When
            // the minion drifts outside the loaded region the GameObject is
            // destroyed, and when it comes back a fresh one is built from the
            // prefab and bound to the SAME ComponentData - which still holds
            // the effects we edited. Movement and colour genuinely do need
            // re-applying then (that state died with the GameObject), but the
            // effects do not: `"health": 2` in multiply mode would double
            // again, and every trip across the boundary would double it once
            // more. The shield, which appends, would stack a new one each time.
            //
            // A reload is the other case and it is fine: the save rebuilds the
            // ComponentData from scratch, so the Module is a new instance
            // carrying prefab values, and it is not in this table.
            //
            // Weak keys, so a dead minion's module is still collectable.
            object seen;

            if (_tuned.TryGetValue(carrier, out seen))
                return;

            _tuned.Add(carrier, Sentinel);

            if (cfg.health.set)
                Health(minion, cfg, carrier, spawnerLevel);

            if (cfg.fuelForLifetime)
                Fuel(minion, cfg, carrier);

            if (cfg.resources != null)
            {
                foreach (KeyValuePair<Resource, float> pair in cfg.resources)
                    Capacity(carrier, pair.Key, pair.Value, cfg, "resources");
            }

            if (cfg.regen != null)
            {
                foreach (KeyValuePair<Resource, float> pair in cfg.regen)
                    Regen(carrier, pair.Key, pair.Value, cfg);
            }

            if (cfg.hasShield)
                Shield(minion, cfg, carrier);

            // Now make the game read it. RecalculateStats is what turns those
            // effects into tank capacities; RefillResources tops the tanks up so
            // a minion given more health starts with it rather than at its old
            // value. Setting MainModule (which is how the weapon gets swapped)
            // raises only ModulesRefreshed, NOT ModuleInstalled, so nothing else
            // schedules this for us on the weapon path.
            if (!recalculate)
                return;      // the caller is doing it once, after everything

            try
            {
                minion.ComponentData.RecalculateStats(gridOwner.ModuleGrid);
                minion.ComponentData.RefillResources();
            }
            catch (Exception e)
            {
                Log.LogError(
                    "minion stats: recalculating '" + minion.name +
                    "' failed, so its tanks may be inconsistent: " + e);
            }
        }

        // Health is whichever resource this unit's DamagableResource points at,
        // and that is NOT "Resource Health" on most of them - the stock drones
        // use Caps, Electron, Purple and Tech. Looking up a resource by name
        // would be a silent no-op on four of the five, and creating a Health
        // tank on a Caps drone gives it a real second pool that the healthbar
        // never shows.
        private static void Health(
            Unit minion, ForgeMinion.Config cfg, Module carrier,
            int spawnerLevel)
        {
            var dmg = minion.GetComponentInChildren<DamagableResource>(true);

            if (dmg == null || dmg.resource == null)
            {
                Log.LogWarning(
                    "minion \"health\": '" + minion.name + "' has no " +
                    "resource-backed health, so there is nothing to scale.");
                return;
            }

            Resource res = dmg.resource;
            int touched = 0;
            float total = 0f;

            // Per-level is folded in HERE, as a flat number, rather than left
            // to the effect's own FloatSeries.
            //
            // The series would be read against the MINION's module level, which
            // is always 1 and which a BoosterCore never touches - the level
            // being raised belongs to the gadget or gun that spawned it, on a
            // completely different unit's grid. So "healthPerLevel" through the
            // series would have been a silent no-op forever.
            float bonus = cfg.health.perLevel * Mathf.Max(0, spawnerLevel - 1);

            foreach (ModuleEffect effect in carrier.Effects)
            {
                var cap = effect as ModifyResourceCapacity;

                if (cap == null || cap.resource != res)
                    continue;

                FloatSeries d = cap.delta;

                if (cfg.statMode == Multiply)
                {
                    d.baseValue = d.baseValue * cfg.health.value + bonus;
                    d.change = d.change * cfg.health.value;
                }
                else
                {
                    d.baseValue = cfg.health.value + bonus;
                }

                cap.delta = d;
                total += d.baseValue;
                touched++;
            }

            if (touched == 0)
            {
                // No existing capacity effect for the health resource - add
                // one. Absolute mode knows what it wants; multiply mode has
                // nothing to multiply.
                if (cfg.statMode == Multiply)
                {
                    Log.LogWarning(
                        "minion \"health\": '" + minion.name + "' has no " +
                        "health-capacity effect to multiply, so a multiplier " +
                        "has nothing to work from. Use an absolute value.");
                    return;
                }

                Capacity(carrier, res, cfg.health.value + bonus, cfg, "health");
                total = cfg.health.value + bonus;
                touched = 1;
            }

            Log.LogInfo(
                "minion health -> " + total.ToString("0.#") + " " + res.name +
                " on '" + minion.name + "' (" + touched + " effect(s), " +
                ModeWord(cfg.statMode) +
                (bonus != 0f
                    ? ", including +" + bonus.ToString("0.#") +
                      " from the spawner being level " + spawnerLevel
                    : "") +
                "). Health in this game IS a resource tank, and this unit's is " +
                res.name + " - not Health.");
        }

        // Three of the weapon-path minions are killed by a fuel drain long
        // before any lifetime we set, so "lifetime" could only ever shorten
        // their life. Scaling the fuel pool by the same ratio makes one key mean
        // one thing on both paths.
        private static void Fuel(
            Unit minion, ForgeMinion.Config cfg, Module carrier)
        {
            var drain = minion.GetComponentInChildren<
                DestroyWhenResourceDrained>(true);

            if (drain == null)
                return;

            Resource fuel = DrainedResource(drain);

            if (fuel == null)
                return;

            float perSecond = 0f;
            float capacity = 0f;

            foreach (ModuleEffect effect in carrier.Effects)
            {
                var used = effect as DrainResourceEffect;

                if (used != null)
                {
                    if (used.resource == fuel)
                        perSecond += used.drainRate.baseValue;

                    continue;
                }

                var cap = effect as ModifyResourceCapacity;

                if (cap != null && cap.resource == fuel)
                    capacity += cap.delta.baseValue;
            }

            if (perSecond <= 0f || capacity <= 0f)
                return;

            float lasts = capacity / perSecond;

            if (lasts >= cfg.lifetime)
            {
                Log.LogInfo(
                    "minion lifetime " + cfg.lifetime.ToString("0.##") +
                    "s - the prefab's own " + fuel.name + " clock already " +
                    "lasts " + lasts.ToString("0.##") + "s, so no change needed.");
                return;
            }

            float need = cfg.lifetime * perSecond;
            float factor = need / capacity;

            foreach (ModuleEffect effect in carrier.Effects)
            {
                var cap = effect as ModifyResourceCapacity;

                if (cap == null || cap.resource != fuel)
                    continue;

                FloatSeries d = cap.delta;
                d.baseValue = d.baseValue * factor;
                cap.delta = d;
            }

            Log.LogInfo(
                "minion lifetime " + cfg.lifetime.ToString("0.##") + "s - this " +
                "prefab burns " + fuel.name + " and dies when it runs out, " +
                "which would have killed it after " + lasts.ToString("0.##") +
                "s. Its " + fuel.name + " pool was scaled x" +
                factor.ToString("0.##") + " so the lifetime you asked for is " +
                "the one you get.");
        }

        private static void Capacity(
            Module carrier, Resource res, float amount,
            ForgeMinion.Config cfg, string what)
        {
            if (res == null)
                return;

            // Prefer editing an existing effect for this resource - two effects
            // for one resource would SUM, so appending would add to the
            // prefab's pool rather than set it.
            foreach (ModuleEffect effect in carrier.Effects)
            {
                var cap = effect as ModifyResourceCapacity;

                if (cap == null || cap.resource != res)
                    continue;

                FloatSeries d = cap.delta;

                d.baseValue = cfg.statMode == Multiply
                    ? d.baseValue * amount
                    : amount;

                cap.delta = d;
                return;
            }

            var added = new ModifyResourceCapacity();
            added.resource = res;

            var series = new FloatSeries();
            series.baseValue = amount;
            series.increaseMethod = FloatSeries.IncreaseMethod.Add;
            added.delta = series;

            // .Module OR AN NRE ON THE FIRST RECALCULATION. Every level-scaled
            // effect reads `Module.Level`, and that back-reference is only ever
            // set by Module's own constructor - a hand-built effect has it null.
            added.Module = carrier;

            carrier.Effects.Add(added);

            Log.LogInfo(
                "minion " + what + ": added a " + res.name + " pool of " +
                amount.ToString("0.#") + ".");
        }

        private static void Regen(
            Module carrier, Resource res, float perSecond,
            ForgeMinion.Config cfg)
        {
            if (res == null)
                return;

            foreach (ModuleEffect effect in carrier.Effects)
            {
                var charge = effect as ResourceAutoChargeEffect;

                if (charge == null || charge.resource != res)
                    continue;

                FloatSeries r = charge.rechargeRate;

                r.baseValue = cfg.statMode == Multiply
                    ? r.baseValue * perSecond
                    : perSecond;

                charge.rechargeRate = r;
                return;
            }

            var added = new ResourceAutoChargeEffect();
            added.resource = res;

            var series = new FloatSeries();
            series.baseValue = perSecond;
            series.increaseMethod = FloatSeries.IncreaseMethod.Add;
            added.rechargeRate = series;
            added.Module = carrier;

            carrier.Effects.Add(added);

            Log.LogInfo(
                "minion regen: " + res.name + " refills at " +
                perSecond.ToString("0.##") + "/s. Note the DELAY before " +
                "recharging starts lives on the shared Resource asset, so it " +
                "is the same as everything else that uses " + res.name + ".");
        }

        private static void Shield(
            Unit minion, ForgeMinion.Config cfg, Module carrier)
        {
            if (cfg.shieldResource == null)
                return;

            var dmg = minion.GetComponentInChildren<DamagableResource>(true);

            // A shield spawns a visual, unchecked. One stock prefab ships that
            // reference NULL, and the game would Instantiate(null) on the first
            // stat recalculation - which is a throw inside the game's own code,
            // not ours, so it would be hard to trace back here.
            if (dmg == null || dmg.shieldPrefab == null)
            {
                Log.LogWarning(
                    "minion \"shield\": '" + minion.name + "' has no shield " +
                    "visual on its prefab, and the game instantiates that " +
                    "without checking - it would throw on the first stat " +
                    "recalculation. Refusing to add the shield. Everything " +
                    "else still applied.");
                return;
            }

            if (cfg.shieldEffectiveness <= 0f)
            {
                Log.LogWarning(
                    "minion \"shield\": effectiveness must be above 0 - the " +
                    "game divides by it. Ignored.");
                return;
            }

            // ---- the ART -----------------------------------------------------
            //
            // Shield.Setup does _material.SetFloat("_Radius", firstShieldSize +
            // index), and firstShieldSize is 0 on 70 of the game's 84 shield
            // prefabs. A radius-0 bubble draws nothing while absorbing damage
            // perfectly, because TakeDamage never looks at radius - which is
            // exactly the "the shield works but I never saw it" report.
            //
            // Left alone unless asked, because the handful that DO ship a
            // radius (the flying drones at 4, the ships at 4, Fly Zapper 8,
            // Turret_Seeker 15) already look right and should not change.
            if (cfg.shieldShow.HasValue)
            {
                dmg.firstShieldSize =
                    cfg.shieldShow.Value ? Mathf.Max(0.01f, cfg.shieldSize) : 0f;
            }

            if (cfg.hasShieldColor)
            {
                // On the UNIT, not the shield: OnStatsRecalculated destroys
                // and rebuilds every Shield, so anything held on one is gone
                // by the next recalculation.
                var tint = minion.GetComponent<ForgeMinionShield>();

                if (tint == null)
                    tint = minion.gameObject.AddComponent<ForgeMinionShield>();

                tint.resource = cfg.shieldResource;
                tint.color = cfg.shieldColor;
            }

            var added = new AddShieldEffect();
            added.resource = cfg.shieldResource;

            var series = new FloatSeries();
            series.baseValue = cfg.shieldEffectiveness;
            series.increaseMethod = FloatSeries.IncreaseMethod.Add;
            added.effectiveness = series;
            added.Module = carrier;

            carrier.Effects.Add(added);

            Log.LogInfo(
                "minion shield: " + cfg.shieldResource.name + " at " +
                cfg.shieldEffectiveness.ToString("0.##") + " effectiveness. A " +
                "shield needs a pool of that resource to spend, so set " +
                "\"resources\" for it too or the shield is decoration.");
        }

        // The unit's own module is the Passive main module on every combat
        // prefab (that is where Enemy.Initialize puts its embeddedModule), but
        // fall back to anything installed rather than refusing.
        private static Module FindCarrier(IModuleGrid grid)
        {
            var simple = grid as SimpleModuleGrid;

            if (simple != null)
            {
                IModuleCluster passive = simple.GetCluster(ClusterType.Passive);

                if (passive != null && passive.HasMainModule &&
                    passive.MainModule != null)
                {
                    return passive.MainModule;
                }
            }

            foreach (IModuleCluster cluster in grid.GetAllClusters())
            {
                if (cluster != null && cluster.HasMainModule &&
                    cluster.MainModule != null)
                {
                    return cluster.MainModule;
                }
            }

            return null;
        }

        // ---- small shared helpers ----------------------------------------
        private static float Mean(MinMaxFloat v)
        {
            return (v.Min + v.Max) * 0.5f;
        }

        private static MinMaxFloat Scale(MinMaxFloat v, float k)
        {
            return new MinMaxFloat(v.Min * k, v.Max * k);
        }

        // Modules whose effects we have already edited. See the comment at the
        // guard in Tanks() for why this is not optional.
        private static readonly ConditionalWeakTable<Module, object> _tuned =
            new ConditionalWeakTable<Module, object>();

        private static readonly object Sentinel = new object();

        private static readonly Dictionary<string, FieldInfo> _fields =
            new Dictionary<string, FieldInfo>();

        // TYPE LOOKUPS ARE CACHED FOR THE SAME REASON FieldInfo IS, and this
        // one was measured rather than guessed.
        //
        // `AccessTools.TypeByName` SCANS EVERY LOADED ASSEMBLY. BepInEx reports
        // "202 assemblies discovered" on this machine, and this file called it
        // FIVE times per minion spawn - EnemyVisibleCondition,
        // TargetVisibleCondition, ShootAction, OwnerIsWithinRangeCondition and
        // MoveAroundOwnerAction - none of them cached.
        //
        // The SPAWN COST instrument put 150-229 ms per spawn in the `stats`
        // stage while `recalc` next to it measured 0.06 ms, which is what
        // pointed here: the expensive thing was never the game's stat rebuild,
        // it was five assembly scans. That is the whole of the "huge lag spike
        // every time i spawn a larva" report.
        //
        // A null result is cached too - a type the game does not have must not
        // re-scan 202 assemblies on every future spawn just to fail again.
        private static readonly Dictionary<string, Type> _types =
            new Dictionary<string, Type>();

        private static Type TypeOf(string name)
        {
            if (string.IsNullOrEmpty(name))
                return null;

            Type found;

            if (_types.TryGetValue(name, out found))
                return found;

            try
            {
                found = AccessTools.TypeByName(name);
            }
            catch
            {
                found = null;
            }

            _types[name] = found;

            return found;
        }

        private static readonly HashSet<string> _warned =
            new HashSet<string>();

        // Plain FieldInfo, deliberately NOT Harmony's FieldRefAccess<T,F>:
        // several of these are MinMaxFloat, and a value-type mismatch there
        // throws rather than returning null. That has bitten this codebase.
        private static FieldInfo Field(Type owner, string name)
        {
            string key = owner.FullName + "." + name;
            FieldInfo found;

            if (_fields.TryGetValue(key, out found))
                return found;

            found = AccessTools.Field(owner, name);
            _fields[key] = found;

            return found;
        }

        private static void Warn(string jsonKey, string member)
        {
            if (!_warned.Add(jsonKey))
                return;

            Log.LogWarning(
                "minion \"" + jsonKey + "\": could not reach " + member +
                " - the game version may have changed. This stat is ignored; " +
                "everything else still applies.");
        }

        // The public door for diagnostics elsewhere (the wingman body
        // trace, R19 test 259).
        public static Resource DrainedResourceOf(DestroyWhenResourceDrained drain)
        {
            return drain != null ? DrainedResource(drain) : null;
        }

        private static Resource DrainedResource(
            DestroyWhenResourceDrained drain)
        {
            FieldInfo f =
                Field(typeof(DestroyWhenResourceDrained), "resource");

            if (f == null)
                return null;

            try
            {
                return f.GetValue(drain) as Resource;
            }
            catch (Exception)
            {
                return null;
            }
        }

    }
}
