using System;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace WeaponForge
{
    // STOPS A FRIENDLY RAMMER KILLING ITS OWN SIDE.
    //
    // ================================================================
    // THIS FIXES A BUG THIS MOD CAUSED, IN THE ROUND BEFORE THIS ONE
    // ================================================================
    //
    // R10 test 6 was "a friendly Larva cannot damage what it headbutts". The
    // cause was `ChargerHead.collisionLayerMask`, which on Larva / Maggot /
    // Child is 1088 = Player | Ground with no Entities bit - so the ram could
    // only ever hit the player. `ForgeMinionAllegiance.Ram` fixed that by adding
    // Entities and removing Player: 1088 -> 1152.
    //
    // **And EVERY unit in the game is on Entities, including the other minions
    // standing right next to it.** `ChargerHead.OnObjectHit` has no faction test
    // of any kind - it damages whatever the CircleCast returned:
    //
    //     Hazard.IHazardSensor[] components =
    //         hit.collider.GetComponents<Hazard.IHazardSensor>();
    //     for (...) components[i].OnHazardTouched(...);
    //
    // So a swarm of friendly Larvae became a swarm that eats itself. Which is
    // exactly what was reported in R10 part 3: *"I think there's a health drain
    // on them somehow? I tried setting their health to 1 and they just die as
    // soon as I fire the gun."*
    //
    // Every detail of that report fits and none of it fits a "drain":
    //
    //   - `health: 1` dies instantly, `health: 40` looks fine. A minion's health
    //     is a tank with that capacity, so at 1 the FIRST sibling contact kills.
    //     The fratricide was happening at 40 too - it was just survivable.
    //   - `followSpeed`, `followDelay` and `turnSpeed` all made it worse, and
    //     all three do the same thing: push the bodies together harder. More
    //     contacts, more rams.
    //   - The sweep is `velocity.magnitude * deltaTime` long, so a FASTER minion
    //     reaches further ahead and finds a sibling sooner. `speed: 220` was
    //     unplayable and `speed: 90` was not.
    //   - It started "as soon as I fire the gun" - i.e. as soon as there was
    //     more than one of them.
    //
    // THE FIX: only let the ram damage something the rammer's own faction
    // considers an ENEMY. `Unit.IsEnemiesWith` is the game's own test and the
    // same one the AI uses to pick a target, so a ram now hurts exactly what the
    // minion was already trying to attack.
    //
    // Three things are deliberately left alone:
    //
    //   - **Terrain.** A Ground collider has no `Unit`, so it falls straight
    //     through to the original. Dropping that would let Larvae swim through
    //     walls - the same cast is what stops them.
    //   - **Stock enemies.** The guard only looks at prefabs whose mask WE
    //     widened, marked at spawn. An enemy Larva ramming you never enters the
    //     interesting branch.
    //   - **The ram cooldown.** Skipping the original also skips its
    //     `lastDamageTime` write, so bumping a sibling does not consume the ram
    //     the minion was saving for an enemy. That is the behaviour you want and
    //     it comes for free.
    //
    // WHY THIS IS PATCHED BY HAND. `ChargerHead` is not visible to this
    // assembly at compile time - `ForgeMinionAllegiance` already has to reach it
    // through `AccessTools.TypeByName` and walk `Component` rather than calling
    // `GetComponentsInChildren<ChargerHead>()`. So there is no `typeof()` to
    // hang a `[HarmonyPatch]` attribute on and `PatchAll` cannot see it.
    public static class ForgeMinionRamGuard
    {
        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("WeaponForge.Minion");

        // Marks a minion whose ram mask THIS MOD widened. Without it the guard
        // would also sit in front of every stock charger in the game, and a
        // stock charger's mask was never the problem.
        public class Widened : MonoBehaviour { }

        private static bool _patched;
        private static bool _saidSpare;

        // Only true once a minion has actually had its mask widened, so the
        // prefix costs a bool test in every other game.
        public static bool Any { get; private set; }

        public static void Mark(Unit minion)
        {
            if (minion == null)
                return;

            try
            {
                if (minion.gameObject.GetComponent<Widened>() == null)
                    minion.gameObject.AddComponent<Widened>();

                Any = true;
            }
            catch (Exception e)
            {
                Log.LogError("Marking a minion's ram failed: " + e);
            }
        }

        // Called once from the plugin, straight after PatchAll.
        public static void Apply(Harmony harmony)
        {
            if (_patched || harmony == null)
                return;

            _patched = true;

            try
            {
                Type charger = AccessTools.TypeByName("ChargerHead");

                if (charger == null)
                {
                    Log.LogWarning(
                        "ChargerHead was not found, so friendly rammers cannot " +
                        "be stopped from hurting their own side. Minion \"ram\" " +
                        "weapons (Larva, Maggot, Child) may kill each other.");
                    return;
                }

                MethodInfo hit = AccessTools.Method(charger, "OnObjectHit");

                if (hit == null)
                {
                    Log.LogWarning(
                        "ChargerHead.OnObjectHit was not found - friendly " +
                        "rammers may kill each other.");
                    return;
                }

                harmony.Patch(
                    hit,
                    prefix: new HarmonyMethod(
                        typeof(ForgeMinionRamGuard),
                        nameof(SkipFriendly)));
            }
            catch (Exception e)
            {
                // A failed patch must never take the mod down with it: without
                // it minions ram their allies, which is bad; with a throw here
                // nothing loads at all, which is worse.
                Log.LogError("Could not guard the minion ram: " + e);
            }
        }

        // Positional `__0` rather than a named parameter: the argument name
        // comes from a decompile and is not something to bet a patch on.
        // `object __instance` because the type does not exist at compile time.
        private static bool SkipFriendly(object __instance, RaycastHit2D __0)
        {
            if (!Any)
                return true;

            try
            {
                var head = __instance as Component;

                if (head == null)
                    return true;

                // Only prefabs whose mask we widened. The marker sits on the
                // Unit, and ChargerHead can be on a child, so search upward.
                if (head.GetComponentInParent<Widened>() == null)
                    return true;

                Unit rammer = head.GetComponentInParent<Unit>();

                if (rammer == null)
                    return true;

                Collider2D col = __0.collider;

                if (col == null)
                    return true;

                // No Unit means terrain or scenery. Let the game have it, or
                // rammers stop stopping at walls.
                Unit hurt = col.GetComponentInParent<Unit>();

                if (hurt == null || ReferenceEquals(hurt, rammer))
                    return true;

                // The game's own question, and the same one the AI asks when it
                // chooses what to chase - so the ram can only ever hurt what the
                // minion was already hunting.
                if (rammer.IsEnemiesWith(hurt))
                    return true;

                if (!_saidSpare)
                {
                    _saidSpare = true;

                    Log.LogInfo(
                        "minion RAM guard: '" + rammer.name + "' just declined " +
                        "to headbutt '" + hurt.name + "' because its faction " +
                        "does not consider that an enemy. ChargerHead has no " +
                        "faction test of its own, so widening its layer mask to " +
                        "reach enemies also pointed it at every other minion in " +
                        "the swarm - this is what stops a swarm eating itself. " +
                        "Logged once per run.");
                }

                return false;
            }
            catch (Exception e)
            {
                Log.LogError("Ram guard failed, letting the hit through: " + e);
                return true;
            }
        }
    }
}
