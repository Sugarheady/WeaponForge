using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace WeaponForge
{
    // ★ A STOCK BUG: THE DRONE-GADGET CAP TRIM DESTROYS THE WRONG MINION.
    //
    // `SpawnMinionModule.OnUpdate` counts the minions belonging to ITS OWN
    // connection type:
    //
    //     int num = unit.Minions.Count(m =>
    //                   m.ConnectionToOwner == this.spawnMinionsConnectionType);
    //
    // and then, if that count is over the module's Level, trims the excess with
    //
    //     this.DestroyUnit(unit.Minions.First());
    //
    // **`Minions` is a `HashSet<Unit.Data>`**, so `.First()` is neither ordered
    // nor filtered by connection type. Every other line in that method is about
    // one slot's minions; the line that acts is about all of them.
    //
    // Two consequences, and the first has been on record since 2026-08-20:
    //
    //   * Equip two drone gadgets. Going over the cap on one - by losing a
    //     BoosterCore, say - can delete a drone belonging to the OTHER.
    //   * Wingman drone bodies are registered as minions (they have to be: the
    //     owner link is what gives them persistence and carries them through a
    //     fast travel), so a stock drone gadget in another Active slot could
    //     eat your companions, and nothing on screen could explain it.
    //
    // THE FIX IS ONE PREDICATE, and it is a NARROWING rather than a new
    // behaviour: every minion it now spares was one the module had no business
    // destroying. It is done as a PREFIX that performs the correct trim itself
    // rather than as a skip, so `ActiveModule.OnUpdate` - which the original
    // calls through `base` before any of this - still runs untouched.
    //
    // ⚠ WHAT MAKES THAT WORK IS THE `RemoveMinion` IN `Trim`, NOT THE `Die()`.
    // Killing the right minions is only half of it; the original then counts
    // the set again, and `Die()` does not leave it this frame. See the note on
    // that line - it is the difference between this patch fixing the bug and
    // merely adding correct kills alongside it.
    //
    // Scoped to the trim and nothing else. `OnUninstalled` already filters by
    // connection type correctly and is not touched, and WHEN the trim fires is
    // unchanged - only which minion it picks.
    public static class ForgeMinionTrimPatch
    {
        private static readonly ManualLogSource Log =
            ForgeLog.Source("WeaponForge.Drone");

        private static bool _said;

        [HarmonyPatch(typeof(SpawnMinionModule), "OnUpdate")]
        public class OnUpdate
        {
            static void Prefix(SpawnMinionModule __instance, Unit.Data unit)
            {
                if (__instance == null || unit == null)
                    return;

                try
                {
                    Trim(__instance, unit);
                }
                catch (Exception e)
                {
                    // Never let a correction to the game's own housekeeping be
                    // the thing that throws - the original still runs after
                    // this and will do its (wrong) trim, which is exactly the
                    // behaviour every build before this one had.
                    Log.LogError("drone cap trim: " + e);
                }
            }
        }

        private static void Trim(SpawnMinionModule module, Unit.Data unit)
        {
            IReadOnlyCollection<Unit.Data> minions = unit.Minions;

            if (minions == null || minions.Count == 0)
                return;

            int level = Mathf.Max(0, module.Level);

            // THE CHEAP EARLY-OUT, and it is exact rather than approximate:
            // the per-type count can never exceed the total, so a total within
            // the cap means there is nothing to trim whatever the types are.
            // This runs every frame for every equipped drone gadget, so the
            // common path has to be one comparison.
            if (minions.Count <= level)
                return;

            Unit.OwnerConnectionType want = ConnectionOf(module);

            // Undefined is what a spawner WEAPON's minions get when their slot
            // cannot be resolved, so trimming on it would be the same
            // indiscriminate cut this patch exists to remove. Leave the
            // original to do whatever it was going to do.
            if (want == Unit.OwnerConnectionType.Undefined)
                return;

            var mine = new List<Unit.Data>();

            foreach (Unit.Data m in minions)
            {
                if (m != null && m.ConnectionToOwner == want)
                    mine.Add(m);
            }

            int over = mine.Count - level;

            if (over <= 0)
                return;

            for (int i = 0; i < over && i < mine.Count; i++)
            {
                Kill(mine[i]);

                // ★★ AND UNREGISTER IT HERE, BECAUSE `Die()` DOES NOT.
                //
                // This is what makes the prefix a fix rather than an
                // addition, and the first version of this patch got it wrong
                // under a comment asserting the opposite: *"by the time the
                // original's own loop is reached the count is already within
                // the cap, so its buggy line never executes."*
                //
                // It executes. `DamagableResource.Die()` ends at
                // `Object.Destroy(gameObject)`, which Unity DEFERS to the end
                // of the frame - and the minion leaves `unit.Minions` from
                // `Unit.Data.OnDestroy`, later still. So the original
                // recomputes `num` from a set we have not changed, `num >
                // level` is still true, and `DestroyUnit(unit.Minions
                // .First())` runs exactly as before. Our correct kills were
                // ADDED TO the bug instead of replacing it, and `.First()` is
                // stable within a frame, so it would often re-Die a body we
                // had just destroyed - `TryGetComponent` on a destroyed
                // object, inside the game's own OnUpdate.
                //
                // `Unit.Data.RemoveMinion` is public and is the same call the
                // minion's own teardown eventually makes, so doing it now is
                // early rather than extra: a `HashSet.Remove` of something
                // already gone is a no-op. With the set corrected
                // synchronously the original's count is right and its loop
                // never enters, which is what the comment claimed all along.
                //
                // > **"Destroyed" is a decision, not an event.** A kill that
                // > resolves at end of frame is invisible to anything reading
                // > the collection this frame, and a prefix's whole job is to
                // > be read by the original.
                try { unit.RemoveMinion(mine[i]); }
                catch (Exception) { }
            }

            if (_said)
                return;

            _said = true;

            Log.LogInfo(
                "drone cap: a gadget was " + over + " minion(s) over its cap " +
                "of " + level + ", so that many of ITS OWN were destroyed. The " +
                "game's own code destroys the first entry in the owner's whole " +
                "minion set instead, which is neither ordered nor of the right " +
                "slot - so two drone gadgets could delete each other's drones, " +
                "and a stock gadget could delete your wingmen. Weapon Forge " +
                "corrects that. Said once per run.");
        }

        // `HealthBase.Die()`, exactly as `SpawnMinionModule.DestroyUnit` does -
        // a raw Destroy would skip the death effects and drops the game expects
        // from a trimmed minion. (A wingman RECALL is the opposite case and
        // deliberately uses Destroy; see ForgeWingmanBody.Take.)
        private static void Kill(Unit.Data minion)
        {
            if (minion == null || minion.entity == null)
                return;

            EntityGameObjectManager manager;

            if (!ServiceLocator.TryGet<EntityGameObjectManager>(out manager) ||
                manager == null)
            {
                return;
            }

            SavableEntity found;

            if (!manager.TryGetSavableEntity(
                    minion.entity.instanceId, out found) || found == null)
            {
                return;
            }

            HealthBase hb = found.GetComponent<HealthBase>();

            if (hb == null)
                hb = found.GetComponentInChildren<HealthBase>(true);

            if (hb != null)
                hb.Die();
        }

        // `spawnMinionsConnectionType` is private and set in `OnInstalled`.
        // Plain FieldInfo rather than Harmony's FieldRefAccess<T,F>, which
        // throws on a value-type mismatch - and this field IS a value type.
        private static FieldInfo _slot;
        private static bool _looked;

        private static Unit.OwnerConnectionType ConnectionOf(
            SpawnMinionModule module)
        {
            if (!_looked)
            {
                _looked = true;

                _slot = AccessTools.Field(
                    typeof(SpawnMinionModule), "spawnMinionsConnectionType");

                if (_slot == null)
                {
                    Log.LogWarning(
                        "drone cap: could not reach " +
                        "SpawnMinionModule.spawnMinionsConnectionType, so the " +
                        "stock cap trim is left as it is - two drone gadgets " +
                        "may delete each other's drones, and a stock gadget " +
                        "may delete a wingman. Nothing else is affected.");
                }
            }

            if (_slot == null)
                return Unit.OwnerConnectionType.Undefined;

            try
            {
                return (Unit.OwnerConnectionType)_slot.GetValue(module);
            }
            catch (Exception)
            {
                return Unit.OwnerConnectionType.Undefined;
            }
        }

        public static void Reset()
        {
            _said = false;
        }
    }
}
