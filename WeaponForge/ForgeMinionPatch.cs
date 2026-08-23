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
                Own(weapon, minion);

                // Side, then the weapon. The faction decides who the AI treats
                // as an enemy, and the weapon rebuild reads the unit; doing it
                // in this order means a minion never exists, even for a frame,
                // as a thing shooting the wrong way.
                ForgeMinion.SetSide(minion, weapon.Owner, cfg);
                ForgeMinion.Equip(minion, cfg);

                // Its own gun, tuned - before the grid inheritance, so any
                // inherited augmentations decorate the weapon we meant.
                ForgeMinion.EquipDefault(minion, cfg);

                if (cfg.inheritGrid)
                    ForgeSwarm.InheritGrid(weapon, minion);

                if (cfg.lifetime > 0f)
                    ForgeMinionLife.Attach(minion, cfg.lifetime);

                // Last, so nothing downstream rebuilds over it.
                ForgeMinionLook.Apply(minion, cfg);

                // After the look, because scaling changes the rigidbody mass
                // and the movement maths reads it. Its trailing stat
                // recalculation also repairs whatever InheritGrid disturbed.
                ForgeMinionStats.Apply(
                    minion, weapon.Owner, cfg, ForgeSwarm.LevelOf(weapon));
                ForgeMinionBlast.Attach(minion, weapon.Owner, cfg);

                ForgeSwarm.Track(weapon, minion);
                ForgeSwarm.Sweep();
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

                    return !ForgeSwarm.Blocked(spawner, cfg);
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

                    Meter(__instance);

                    return ForgeSwarm.AllowSpawn(__instance, cfg);
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
        }

        private static readonly Dictionary<WeaponBase, Meter_> _meters =
            new Dictionary<WeaponBase, Meter_>();

        private const float MeterWindow = 2f;

        private static void Meter(MinionSpawnerWeapon weapon)
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

            m.spawns++;

            if (Time.time - m.started < MeterWindow)
                return;

            m.reported = true;

            var data = weapon.TemplateData as MinionSpawnerWeaponData;

            if (data == null)
                return;

            int perPull =
                Mathf.Max(1, Mathf.RoundToInt(data.projectileCount)) *
                Mathf.Max(1, data.burstSize);

            float elapsed = Mathf.Max(0.001f, Time.time - m.started);
            float expected = data.fireRate > 0f
                ? data.fireRate * elapsed * perPull
                : 0f;

            string verdict;

            if (data.fireRate <= 0f)
            {
                verdict =
                    "the weapon's fireRate is " +
                    data.fireRate.ToString("0.###") +
                    ", which cannot produce a sane interval at all";
            }
            else if (m.spawns > expected * 2f + 1f)
            {
                verdict =
                    "THAT IS FAR MORE THAN THE FILE ASKS FOR. The weapon's " +
                    "own values are not being honoured at spawn time - please " +
                    "report this line";
            }
            else
            {
                verdict = "which matches the file";
            }

            Log.LogInfo(
                "minion RATE CHECK on '" + weapon.TemplateData.name + "': " +
                m.spawns + " minion(s) actually spawned in " +
                elapsed.ToString("0.##") + "s of firing. The file asks for " +
                perPull + " per pull at fireRate " +
                data.fireRate.ToString("0.###") + " (a pull every " +
                (data.fireRate > 0f
                    ? (1f / data.fireRate).ToString("0.###")
                    : "?") +
                "s), so about " + expected.ToString("0.#") +
                " were expected - " + verdict + ".");
        }
    }
}
