using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace WeaponForge
{
    // Make a minion able to FIGHT for the side it is on.
    //
    // THE DISCOVERY THIS FILE EXISTS FOR, and it explains four separate
    // "the AI is broken" reports at once:
    //
    //   **A unit's eyes are hard-wired to one layer, and it is not the layer
    //   its enemies are on once you change whose side it is on.**
    //
    // `Vision` derives from `ComponentScanner<Unit>`, whose `Scan()` is a
    // `Physics2D.OverlapCircleNonAlloc` filtered by a **serialized private
    // `targetLayers` LayerMask**. The prefabs split cleanly in two:
    //
    //   Enemy_Larva, Enemy_Maggot, ...  targetLayers = 64  = layer 6, Player
    //   Enemy_Fly Variant Drone, ...    targetLayers = 128 = layer 7, Entities
    //
    // An enemy is built to look for you and nothing else; a player drone is
    // built to look for enemies and nothing else. `AIAgent.Update` then sorts
    // whatever Vision handed it into friends and enemies by faction and
    // targets the first enemy - so the faction decides who it FIGHTS but the
    // layer mask decides who it can SEE, and nothing reconciles the two.
    //
    // Consequences, all four of them observed in play:
    //
    //   - A hostile prefab put on your side sees only you, decides you are a
    //     friend, and never finds a target at all. It drifts. ("None of the
    //     enemy minions will follow or seek enemies.")
    //   - A drone prefab left hostile (`"faction": "keep"`) sees only the
    //     Entities layer, and you are not on it, so it cannot attack you
    //     however much it would like to. ("Funny enough doesn't attack the
    //     player.")
    //   - Either way the AI's state indicators flicker between Idle and
    //     nothing, which reads as "enemy ai seems messed up".
    //   - And it is invisible in every log, because every value the mod wrote
    //     was written correctly.
    //
    // The fix is one field: give a Forge minion eyes that cover **both**
    // layers and let the faction sort it out, which is what `AIAgent` was
    // already written to do.
    //
    // SECOND HALF: ITS BULLETS. `Projectile.Shoot` builds its collision mask
    // with `Physics2D.GetLayerCollisionMask(gameObject.layer)`, so a shot's
    // faction is the layer of the prefab it came from. An enemy gun fires
    // `EnemyProjectiles`, which the matrix collides with Player / Ground /
    // Fruits and NOT with Entities. So a minion of yours carrying its own
    // stock gun shoots bullets that can only ever hurt YOU - which is exactly
    // what was reported ("their weapons dont appear to hurt enemies but hurt
    // the player"). Moving the cloned ammo to `PlayerProjectiles` fixes it,
    // and the mask follows for free because it is rebuilt from the layer at
    // Shoot. A hitscan gun needs its `layerMask` swapped instead.
    public static class ForgeMinionAllegiance
    {
        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("WeaponForge.Minion");

        // ------------------------------------------------------------------
        // ITS EYES - per spawn.
        // ------------------------------------------------------------------

        private static FieldInfo _targetLayers;
        private static bool _looked;

        private static FieldInfo TargetLayers
        {
            get
            {
                if (!_looked)
                {
                    _looked = true;

                    // Declared on the generic base, not on Vision, so it has
                    // to be asked for by the closed generic type. AccessTools
                    // walks the hierarchy, which is exactly what is wanted.
                    _targetLayers =
                        AccessTools.Field(typeof(Vision), "targetLayers");

                    if (_targetLayers == null)
                    {
                        Log.LogWarning(
                            "Could not reach Vision.targetLayers, so minions " +
                            "keep the one layer their prefab was built to " +
                            "watch. A minion of yours may then never find an " +
                            "enemy to shoot at.");
                    }
                }

                return _targetLayers;
            }
        }

        // Prefab names already reported, so a swarm of twenty logs once.
        private static readonly HashSet<string> _said = new HashSet<string>();

        public static void Eyes(Unit minion, ForgeMinion.Config cfg)
        {
            if (minion == null || cfg == null || !cfg.seeEverything)
                return;

            FieldInfo f = TargetLayers;

            if (f == null)
                return;

            try
            {
                int player = LayerMask.NameToLayer("Player");
                int entities = LayerMask.NameToLayer("Entities");

                // Defensive: a renamed layer would otherwise produce a mask of
                // 1 (Default), which is worse than leaving it alone.
                if (player < 0 || entities < 0)
                    return;

                int want = (1 << player) | (1 << entities);
                int changed = 0;
                int was = 0;

                foreach (Vision v in
                         minion.GetComponentsInChildren<Vision>(true))
                {
                    if (v == null)
                        continue;

                    var had = (LayerMask)f.GetValue(v);

                    if ((had.value | want) == had.value)
                        continue;

                    was = had.value;
                    f.SetValue(v, (LayerMask)(had.value | want));
                    changed++;
                }

                if (changed == 0 || _said.Contains(minion.name))
                    return;

                _said.Add(minion.name);

                Log.LogInfo(
                    "minion eyes on '" + minion.name + "': Vision.targetLayers " +
                    was + " -> " + (was | want) + " (Player + Entities). Its " +
                    "prefab was built to watch exactly ONE of those layers, so " +
                    "on any side but its original it could never SEE a target - " +
                    "the faction decides who it fights, the layer mask decides " +
                    "who it can see, and nothing in the game reconciles the two. " +
                    "Set \"seek\": false to keep the prefab's own eyes.");
            }
            catch (Exception e)
            {
                Log.LogError("Retargeting a minion's vision failed: " + e);
            }
        }

        // ------------------------------------------------------------------
        // THE RAM. Third member of the same family as Eyes above, and the one
        // that made a friendly Larva look completely broken:
        //
        //   "they run into enemies with their heads... they dont shoot
        //    projectiles at all. They do seek the enemies but not hurt them."
        //
        // A Larva HAS NO GUN. Its attack is `ChargerHead` - a swept CircleCast
        // along its own velocity every FixedUpdate - so the ammo-layer retarget
        // that fixed shooting minions never applied to it at all.
        //
        // And ChargerHead casts against its own serialized mask:
        //
        //   Enemy_Larva / Maggot / Child   collisionLayerMask = 1088
        //                                  = 64 (Player) | 1024 (Ground)
        //
        // No Entities bit - the layer every enemy is on. So a Larva flipped to
        // your side could seek an enemy, connect with it, and do nothing, while
        // remaining perfectly able to ram YOU. Same sentence as the other two:
        // faction decides who a unit fights, a layer mask decides what it can
        // touch, and nothing in the game reconciles them.
        //
        // Ground is deliberately KEPT: the cast is also how the thing stops
        // itself against walls, and dropping it would send Larvae through
        // terrain.
        private static FieldInfo _ramMask;
        private static bool _ramLooked;

        private static FieldInfo RamMask
        {
            get
            {
                if (_ramLooked)
                    return _ramMask;

                _ramLooked = true;

                try
                {
                    var t = AccessTools.TypeByName("ChargerHead");

                    if (t != null)
                        _ramMask = AccessTools.Field(t, "collisionLayerMask");

                    if (_ramMask == null)
                    {
                        Log.LogWarning(
                            "Could not reach ChargerHead.collisionLayerMask, " +
                            "so a ramming minion (Larva, Maggot, Child) will " +
                            "still be unable to hurt anything but you. " +
                            "Everything else about it still applies.");
                    }
                }
                catch (Exception e)
                {
                    Log.LogError("Looking up ChargerHead failed: " + e);
                }

                return _ramMask;
            }
        }

        private static readonly HashSet<string> _saidRam =
            new HashSet<string>();

        public static void Ram(Unit minion, ForgeMinion.Config cfg)
        {
            if (minion == null || cfg == null || !cfg.friendlyShots)
                return;

            FieldInfo f = RamMask;

            if (f == null)
                return;

            try
            {
                int player = LayerMask.NameToLayer("Player");
                int entities = LayerMask.NameToLayer("Entities");

                if (player < 0 || entities < 0)
                    return;

                int changed = 0;
                int was = 0;
                int now = 0;

                // Not GetComponentsInChildren<ChargerHead> - the type is the
                // game's and only resolves by name here, so walk Components.
                foreach (Component c in
                         minion.GetComponentsInChildren<Component>(true))
                {
                    if (c == null || c.GetType() != f.DeclaringType)
                        continue;

                    var had = (LayerMask)f.GetValue(c);

                    // ADD Entities, and DROP Player. Dropping matters as much
                    // as adding: a friendly minion that can still ram you is
                    // the other half of what he reported.
                    int want = (had.value | (1 << entities)) & ~(1 << player);

                    if (want == had.value)
                        continue;

                    was = had.value;
                    now = want;
                    f.SetValue(c, (LayerMask)want);
                    changed++;
                }

                // WIDENING THE MASK IS ONLY HALF THE JOB. Adding Entities points
                // the ram at every unit in the game, and ChargerHead has no
                // faction test - so without this the swarm eats itself. Mark it
                // so the guard's prefix knows this one is ours.
                if (changed > 0)
                    ForgeMinionRamGuard.Mark(minion);

                if (changed == 0 || _saidRam.Contains(minion.name))
                    return;

                _saidRam.Add(minion.name);

                Log.LogInfo(
                    "minion RAM on '" + minion.name +
                    "': ChargerHead.collisionLayerMask " + was + " -> " + now +
                    " (added Entities, removed Player, kept Ground). This " +
                    "prefab has NO GUN - its attack is the ram - and that cast " +
                    "was wired to hit the PLAYER and terrain only, so on your " +
                    "side it could seek an enemy, connect, and do nothing while " +
                    "still hurting you. Ground is kept deliberately: the same " +
                    "cast is how it stops against walls. Set " +
                    "\"friendlyShots\": false to leave the prefab's own ram " +
                    "alone.");
            }
            catch (Exception e)
            {
                Log.LogError("Retargeting a minion's ram failed: " + e);
            }
        }

        // Forget the "logged already" set between runs, so a second run in the
        // same process still explains itself.
        public static void Forget()
        {
            _said.Clear();
        }

        // ------------------------------------------------------------------
        // ITS BULLETS - build time, on the cloned weapon.
        // ------------------------------------------------------------------

        // Returns true when something was changed, so the caller can log a
        // combined line rather than this printing one per weapon.
        public static bool Ammo(
            WeaponData wd, ForgeMinion.Config cfg, string fileName)
        {
            if (wd == null || cfg == null || !cfg.friendlyShots)
                return false;

            try
            {
                var pw = wd as ProjectileWeaponData;

                if (pw != null)
                    return Bullets(pw, fileName);

                var hw = wd as HitscanWeaponData;

                if (hw != null)
                    return Beam(hw, fileName);

                return false;
            }
            catch (Exception e)
            {
                Log.LogError(
                    fileName + ": retargeting a minion's ammo failed, so its " +
                    "shots can still only hurt you: " + e);
                return false;
            }
        }

        private static bool Bullets(ProjectileWeaponData pw, string fileName)
        {
            int player = LayerMask.NameToLayer("PlayerProjectiles");

            if (player < 0)
                return false;

            bool did = false;

            // Both prefab slots, because usePhysics decides which one is set
            // and the other is null - the guard that has broken eight features
            // in this codebase before.
            did |= Move(
                pw.projectilePrefab != null
                    ? pw.projectilePrefab.gameObject
                    : null,
                player, pw, false, fileName);

            did |= Move(
                pw.physicsProjectilePrefab != null
                    ? pw.physicsProjectilePrefab.gameObject
                    : null,
                player, pw, true, fileName);

            return did;
        }

        private static bool Move(
            GameObject prefab,
            int layer,
            ProjectileWeaponData pw,
            bool physics,
            string fileName)
        {
            if (prefab == null || prefab.layer == layer)
                return false;

            // Clone, because the ammo prefab is SHARED. `Ammo Cross` is the
            // bullet of eight enemy prefabs; moving the original to the
            // player's layer would make every Cross in the run harmless to
            // you and lethal to its own side, permanently.
            GameObject clone = VisualCustomizer.ClonePrefab(prefab);

            if (clone == null)
                return false;

            // The whole hierarchy, not just the root: a trail child on the
            // enemy layer would be filtered by the same matrix.
            foreach (Transform t in clone.GetComponentsInChildren<Transform>(true))
            {
                if (t != null)
                    t.gameObject.layer = layer;
            }

            if (physics)
            {
                var comp = clone.GetComponentInChildren<PhysicsProjectile>(true);

                if (comp == null)
                    return false;

                pw.physicsProjectilePrefab = comp;
            }
            else
            {
                var comp = clone.GetComponentInChildren<Projectile>(true);

                if (comp == null)
                    return false;

                pw.projectilePrefab = comp;
            }

            return true;
        }

        private static bool Beam(HitscanWeaponData hw, string fileName)
        {
            int entities = LayerMask.NameToLayer("Entities");
            int ground = LayerMask.NameToLayer("Ground");
            int fruits = LayerMask.NameToLayer("Fruits");
            int player = LayerMask.NameToLayer("Player");

            if (entities < 0)
                return false;

            int had = hw.layerMask.value;

            // Swap who it can hit while leaving the scenery alone: a beam that
            // stops passing through walls would be a different weapon.
            int want = (1 << entities);

            if (ground >= 0 && (had & (1 << ground)) != 0)
                want |= 1 << ground;

            if (fruits >= 0 && (had & (1 << fruits)) != 0)
                want |= 1 << fruits;

            if (player >= 0)
                want &= ~(1 << player);

            if (want == had)
                return false;

            hw.layerMask = want;
            return true;
        }
    }
}
