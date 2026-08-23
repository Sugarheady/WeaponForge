using System;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace WeaponForge
{
    // Optional protection from your own minions' explosions.
    //
    // THE SITUATION, WHICH IS THE GAME'S AND NOT OURS. Explosions in PUNK have
    // no owner filter anywhere in the delivery path.
    // `ExplosionManager.DoExplosionLogic` walks every collider in the radius and
    // calls `OnExplosion` on each listener; the only test is
    // `!isTrigger || layer == Ground`. `explosion.Owner` is set, but it is read
    // only to fire the got-attacked event and to credit a kill - never to decide
    // whether the damage lands. The ship's hull colliders are non-trigger, so
    // they qualify like anything else.
    //
    // So an explosive minion hurts you by default, and that is left as the
    // default deliberately: it is what the engine does, and a mod that quietly
    // makes explosions behave differently from every other explosion in the
    // game is harder to reason about than one that warns you.
    //
    // `"hurtsOwner": false` and `"hurtsAllies": false` turn the filter on.
    //
    // WHERE THE FILTER SITS. Not on DoExplosionLogic - that would mean
    // reimplementing the game's collider walk to drop one entry from it. It
    // sits on `DamagableResource.OnExplosion`, which is the per-victim entry
    // point, so the decision is made once per candidate with no loop to
    // duplicate. Skipping it also skips the burn, which matters: switching an
    // explosion on MOVES the burn off contact and into the blast, so filtering
    // only the damage would still set the player on fire.
    public static class ForgeMinionBlast
    {
        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("WeaponForge.Minion");

        // Set once a minion with a protective setting exists, so the patch does
        // nothing at all in the overwhelmingly common case.
        public static bool Any { get; private set; }

        // Marks a live minion with the config that made it. Cheap to read from
        // the explosion path, and it means the filter never has to walk a grid
        // while damage is being resolved.
        public class Tag : MonoBehaviour
        {
            public ForgeMinion.Config cfg;
            public Unit owner;
        }

        public static void Attach(
            Unit minion, Unit owner, ForgeMinion.Config cfg)
        {
            if (minion == null || cfg == null)
                return;

            // Only worth tagging when something actually asks for protection.
            if (cfg.hurtsOwner && cfg.hurtsAllies)
                return;

            try
            {
                var tag = minion.gameObject.GetComponent<Tag>();

                if (tag == null)
                    tag = minion.gameObject.AddComponent<Tag>();

                tag.cfg = cfg;
                tag.owner = owner;

                Any = true;
            }
            catch (Exception e)
            {
                Log.LogError("Tagging a minion for blast safety failed: " + e);
            }
        }

        [HarmonyPatch(typeof(DamagableResource), "OnExplosion")]
        public class OnExplosion
        {
            static bool Prefix(DamagableResource __instance, Explosion explosion)
            {
                if (!Any || __instance == null)
                    return true;

                try
                {
                    return !Protected(__instance, explosion);
                }
                catch (Exception e)
                {
                    Log.LogError("Blast-safety check failed: " + e);
                    return true;
                }
            }
        }

        private static bool Protected(
            DamagableResource victim, Explosion explosion)
        {
            Unit blamed = explosion.Owner;

            if (blamed == null)
                return false;

            var tag = blamed.gameObject.GetComponent<Tag>();

            if (tag == null || tag.cfg == null)
                return false;

            // DamagableResource keeps its own Unit reference private, so find
            // it the same way it does - upward, since health sits on a child on
            // some prefabs.
            Unit hurt = victim.GetComponentInParent<Unit>();

            if (hurt == null)
                return false;

            // Never filter the minion's own target-facing damage - only the two
            // relationships that were opted out of.
            if (!tag.cfg.hurtsOwner && tag.owner != null &&
                ReferenceEquals(hurt, tag.owner))
            {
                return true;
            }

            if (tag.cfg.hurtsAllies)
                return false;

            // A sibling: another minion of the same summoner. Checked by owner
            // rather than by faction, so this cannot accidentally protect an
            // enemy that happens to share a side.
            if (ReferenceEquals(hurt, blamed))
                return false;

            var sibling = hurt.gameObject.GetComponent<Tag>();

            if (sibling != null && sibling.owner != null && tag.owner != null &&
                ReferenceEquals(sibling.owner, tag.owner))
            {
                return true;
            }

            // Anything else on the summoner's own side. `IsFriendsWith` reads
            // the faction's ally list and does NOT count a faction as its own
            // ally, so the owner's own units need the explicit check above.
            if (tag.owner != null && hurt != tag.owner)
            {
                try
                {
                    if (tag.owner.IsFriendsWith(hurt))
                        return true;
                }
                catch (Exception)
                {
                    // A missing faction asset is not a reason to drop damage.
                }
            }

            return false;
        }
    }
}
