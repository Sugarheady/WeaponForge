using System;
using BepInEx.Logging;
using UnityEngine;

namespace WeaponForge
{
    // "minion": { "solidToOwner": false } - your own minions stop bumping into
    // you.
    //
    // WHY THIS IS ITS OWN THING AND NOT A DAMAGE FLAG. `hurtsOwner` already
    // stops a minion's explosion hurting you, and the ram's faction guard stops
    // it headbutting you - but neither touches the COLLISION. A pile of Larvae
    // at `speed: 220` shoving the ship across the room is pure rigidbody
    // physics: no damage is dealt, so no damage flag can reach it.
    //
    // WHY IgnoreCollision AND NOT A LAYER. The obvious fix - move minions to a
    // layer the Player layer does not collide with - is the wrong tool twice
    // over in this game. A unit's GameObject layer is load-bearing: `Vision`
    // scans BY layer, a projectile's faction IS its layer, and the collision
    // matrix decides who its bullets can reach. Moving a minion off Entities to
    // dodge the ship would quietly change who can see it, who can shoot it and
    // who its own shots hit. `Physics2D.IgnoreCollision` is surgical by
    // comparison: it suppresses collisions and trigger callbacks between exactly
    // one pair of colliders and nothing else.
    //
    // AND IT DOES NOT AFFECT QUERIES, which is the property that makes it safe
    // here. `OverlapCircle`, `CircleCast` and friends are queries against the
    // world rather than collisions between a pair, so an ignored pair is still
    // returned by them. So the minion's `Vision` still sees you (it needs to -
    // that is how a friendly one decides you are a friend), the ram's swept
    // cast still finds you, and your shots can still hit it. Only the shoving
    // stops.
    //
    // DEFAULT IS SOLID, i.e. no change from the game. Sugarheady called the
    // shoving "quite funny" before asking for the switch, so the surfable swarm
    // stays the default and the file opts out.
    public static class ForgeMinionSolid
    {
        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("WeaponForge.Minion");

        // One line per weapon rather than per minion - a cap of 12 would
        // otherwise print this twelve times a trigger pull.
        private static readonly System.Collections.Generic.HashSet<string> _said =
            new System.Collections.Generic.HashSet<string>();

        public static void Apply(
            Unit minion, Unit owner, ForgeMinion.Config cfg, string label)
        {
            // The whole feature is opt-out, so the common path is one bool test.
            if (cfg == null || cfg.solidToOwner)
                return;

            if (minion == null || owner == null)
                return;

            try
            {
                // includeInactive: a prefab can ship a collider on a disabled
                // child (the beak GameObjects do), and an inactive collider
                // still counts once its state machine switches the child on.
                Collider2D[] mine =
                    minion.GetComponentsInChildren<Collider2D>(true);

                Collider2D[] theirs =
                    owner.GetComponentsInChildren<Collider2D>(true);

                if (mine.Length == 0 || theirs.Length == 0)
                {
                    Warn(label,
                        "\"solidToOwner\": false found no colliders to " +
                        "separate (" + mine.Length + " on the minion, " +
                        theirs.Length + " on the ship), so the minions will " +
                        "still bump into you. Nothing is broken by this - it " +
                        "just did not apply.");
                    return;
                }

                int pairs = 0;

                for (int i = 0; i < mine.Length; i++)
                {
                    if (mine[i] == null)
                        continue;

                    for (int j = 0; j < theirs.Length; j++)
                    {
                        if (theirs[j] == null)
                            continue;

                        Physics2D.IgnoreCollision(mine[i], theirs[j], true);
                        pairs++;
                    }
                }

                if (_said.Add(label))
                {
                    Log.LogInfo(
                        label + ": minion \"solidToOwner\": false - " + pairs +
                        " collider pair(s) separated (" + mine.Length +
                        " on the minion x " + theirs.Length + " on the ship), " +
                        "so your own minions no longer shove the ship. They " +
                        "still block enemies, still hit terrain, and you can " +
                        "still shoot them - only the push between you and them " +
                        "is gone.");
                }
            }
            catch (Exception e)
            {
                // A cosmetic comfort setting must never cost a spawn. See
                // ForgeMinionStats - a warning-only read once killed five
                // tests in one round.
                Warn(label,
                    "\"solidToOwner\" could not be applied (" + e.Message +
                    "), so the minions stay solid. The spawn itself is fine.");
            }
        }

        private static void Warn(string label, string message)
        {
            Log.LogWarning(label + ": " + message);
        }

        // Test hook: the once-per-weapon log gate would otherwise make a second
        // run in the same session silent.
        public static void Reset()
        {
            _said.Clear();
        }
    }
}
