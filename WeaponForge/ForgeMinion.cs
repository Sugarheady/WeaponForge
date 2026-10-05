using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using BepInEx.Logging;
using UnityEngine;

namespace WeaponForge
{
    // Give a spawned minion a weapon of YOUR design.
    //
    // THE FACT THIS RESTS ON, and it is not the one I first assumed. A drone
    // already receives the modules from its spawning cluster - that is why
    // adding projectiles or health to the grid visibly reaches the drone - but
    // it receives them through `SimpleModuleGrid.InstallAugmentation`, which
    // calls `AddModule`, which does exactly `modules.Add(module)` and **never
    // touches mainModule**. Meanwhile `ModuleSlotWeaponHolder.RefreshWeapon`
    // builds the weapon from `MainModule as WeaponModule`.
    //
    // So handing a drone a weapon module makes it a MODIFIER on the drone's
    // existing gun, not a replacement. To actually change what a minion fires,
    // the PrimaryWeapon cluster's `MainModule` has to be set - and it is a
    // public settable property, so that is exactly what this does.
    public static class ForgeMinion
    {
        private static readonly ManualLogSource Log =
            ForgeLog.Source("WeaponForge.Minion");

        public class Config
        {
            // The name of another Forge weapon, or a stock weapon asset.
            public string weapon = "";

            // Whose side the spawned minion is on.
            //
            //   "owner" (default) - the faction of whoever summoned it, so a
            //                       minion is always YOURS
            //   "keep"            - leave the prefab's own faction alone
            //   any faction name  - "Cross", "Fly", "Maggot", "Crawler",
            //                       "Player"
            //
            // THIS MATTERS MORE THAN IT LOOKS. `Unit.SetOwner` sets Owner and
            // ConnectionToOwner and adds the minion to your list - and does NOT
            // touch faction. The five player drones are player-faction assets
            // despite their "Enemy_" name prefix, but four of the prefabs the
            // builder offers (the Mine, Cross Pin, OneShot 4, Queen Child) are
            // genuinely hostile. Spawning one of those counted against your
            // minion cap and then shot at you.
            //
            // Hence "owner" as the default: a thing you summoned should be
            // yours. It only changes behaviour in the case that was a bug.
            public string faction = "owner";

            // Seconds before a spawned minion dies of its own accord. 0 = it
            // lives until something kills it, which is the stock behaviour.
            //
            // This is what makes a SWARM possible rather than a pet: a high cap
            // and a short lifetime give you a cloud of short-lived helpers
            // instead of a permanent escort.
            public float lifetime;

            // ---- how it LOOKS -------------------------------------------
            //
            // Scoped to the prefab's `Visual*` subtree, because a unit's AI
            // state indicators are SpriteRenderers too and are deliberately
            // colour-coded. See ForgeMinionLook.
            public Color color = Color.white;
            public bool hasColor;
            public int colorMode = ForgeMinionLook.ColorReplace;

            public float scale = 1f;
            public int scaleMode = ForgeMinionLook.ScaleFull;

            // ---- what it IS ---------------------------------------------
            //
            // Absolute by default: "health": 60 means 60. `statMode:
            // "multiply"` switches every stat in this block to a factor
            // instead. See ForgeMinionStats for why health and resources have
            // to travel through the module-effect pipeline rather than being
            // written directly.
            public int statMode = ForgeMinionStats.Absolute;

            public ForgeMinionStats.Tweak health;
            public ForgeMinionStats.Tweak speed;
            public ForgeMinionStats.Tweak turnSpeed;
            public ForgeMinionStats.Tweak sight;
            public ForgeMinionStats.Tweak attackRange;
            public ForgeMinionStats.Tweak leash;
            public ForgeMinionStats.Tweak followDistance;
            public ForgeMinionStats.Tweak iFrames;
            public ForgeMinionStats.Tweak fireproof;

            public bool untargetable;
            public bool hasUntargetable;

            // Resource pools and their refill rates, resolved at build time so
            // a bad resource name warns once in the log rather than on every
            // spawn.
            public Dictionary<Resource, float> resources;
            public Dictionary<Resource, float> regen;

            // The little smear a unit leaves as it moves. Separate from
            // `color` on purpose - a red drone with a cyan trail is a
            // perfectly reasonable thing to want, and the body colour was
            // already right.
            public Color trailColor = Color.white;
            public bool hasTrailColor;
            public bool trailMatchesBody;   // "match" - follow `color`

            public Resource shieldResource;
            public float shieldEffectiveness;

            // Whether to DRAW the shield. Three states on purpose, and the
            // absent one is the default: null leaves the prefab exactly as it
            // ships, so nothing that already works changes appearance. true
            // gives the bubble a radius on the 70-odd prefabs that ship 0
            // (which is why minion shields have always been invisible); false
            // hides one on the handful that ship a real radius already - the
            // flying drones and the ships.
            public bool? shieldShow;

            // The shader's _Radius. Ships and drones use 4, Fly Zapper 8,
            // Turret_Seeker 15, everything else 0 - so 4 is "looks like the
            // one you have already seen".
            public float shieldSize = 4f;

            // Four of the eight resources ship shieldColor rgba(0,0,0,0) -
            // White, Fuel, Health and Money - because the game has no shield
            // module for them. This paints over that.
            public Color shieldColor = Color.white;
            public bool hasShieldColor;
            public bool hasShield;

            // Three of the weapon-path prefabs burn a fuel resource and die
            // when it runs out, well before any lifetime you ask for. Set when
            // `lifetime` is used, so the fuel pool is scaled to match instead
            // of the lifetime silently doing nothing.
            public bool fuelForLifetime;

            public bool HasStats
            {
                get
                {
                    return health.set || speed.set || turnSpeed.set ||
                           sight.set || attackRange.set || leash.set ||
                           followDistance.set || iFrames.set ||
                           fireproof.set || hasUntargetable ||
                           hasShield || fuelForLifetime ||
                           resources != null || regen != null;
                }
            }

            // ---- its OWN gun, tuned ------------------------------------
            //
            // Built at load time by ForgeMinionArms: a private clone of
            // whatever weapon the prefab already carries, with explosion /
            // lightning / burn and any stat tweaks applied to the clone. Null
            // means the file asked for none of that, or the prefab could not
            // support it.
            [NonSerialized] public WeaponModuleData defaultWeaponModule;
            [NonSerialized] public bool defaultWeaponFailed;

            // Whether this minion's explosions can hurt the player who
            // summoned it, and its siblings.
            //
            // BOTH DEFAULTED TO TRUE UNTIL 2026-09-02, on the reasoning that
            // matching the engine is easier to reason about than quietly
            // differing from it. That reasoning does not survive contact: the
            // opt-out **could not work at all** on a death explosion, because
            // ExplosionComponent never gives its blast an owner and the filter
            // bails on a null one (see ForgeMinionBlast). So "your own kamikaze
            // swarm blows you up" was not a default anybody chose - it was the
            // only available behaviour, and it was reported as a bug.
            //
            // Now FALSE by default: your own minions do not blow you or each
            // other up. Nothing can have been relying on the old opt-out
            // because the old opt-out did nothing. Set either to true for the
            // risky version, which is a real design (a swarm you have to keep
            // your distance from) and is now genuinely selectable.
            public bool hurtsOwner;
            public bool hurtsAllies;

            // Whether this minion's COLLIDER still collides with the ship's.
            // Nothing to do with damage: `hurtsOwner` covers the explosion and
            // the ram guard covers the headbutt, but a fast swarm shoves the
            // ship around by pure physics and no damage flag can reach that.
            // Defaults TRUE, which is what the game does. See ForgeMinionSolid.
            public bool solidToOwner = true;

            // ---- being able to FIGHT for the side it is on ---------------
            //
            // All three default ON, and all three are fixing a bug rather than
            // adding a behaviour - see ForgeMinionAllegiance and
            // ForgeMinionEscort for what each one is worth. They are switches
            // only so a deliberately blind, harmless or stationary minion is
            // still possible.

            // Widen its Vision to cover Player AND Entities. A prefab watches
            // exactly one of those, which is why a minion on any side but its
            // original could never see a target.
            public bool seeEverything = true;

            // Move its own gun's ammo onto the player's projectile layer, so
            // its shots hurt what it is aiming at instead of only you.
            public bool friendlyShots = true;

            // Lean it toward its owner when it has nothing to fight. Only the
            // five drone prefabs have a follow AI state at all; every other
            // spawnable simply cannot come back to you without this.
            //
            // THREE-STATE, and the middle one is the default. Unset means
            // "escort it only if its prefab cannot do this itself" - detected
            // by looking for the MoveAroundOwnerAction that a Regroup state
            // carries. That matters: on a drone that ALREADY regroups, adding a
            // force on top of its own movement would make it overshoot and
            // orbit, so the sane default is to leave the five prefabs the game
            // built for this job alone. Saying `"follow": true` forces it on
            // anyway; false forces it off.
            public bool follow = true;
            public bool followSet;

            // Cruise speed of that lean, and how long after spawn to leave it
            // alone (so the spawner's throw is not cancelled).
            public float followSpeed;
            public float followDelay = 0.6f;

            // ---- or HOLD GROUND instead --------------------------------
            //
            // The opposite stance, and OPT-IN: "spawn a bunch in a spot and run
            // away". A held minion keeps to a post rather than to you.
            //
            // MUTUALLY EXCLUSIVE WITH `follow`, and the exclusion is enforced in
            // ForgeMinionEscort.Attach rather than here, so it covers the drone
            // gadget and the spawner weapon in one place. They are opposite
            // answers to one question; running both would pull one body toward
            // two points.
            //
            // Default off for the obvious reason and one less obvious one:
            // every minion weapon already written expects the escort, and a
            // stance is exactly the kind of thing that must not change under a
            // file nobody edited. See ForgeMinionHold.
            public bool hold;
            public bool holdSet;

            // How far from the post it may roam. Inside this it behaves
            // completely normally; outside, its own steering is suspended and it
            // walks back. Clamped to a floor of 1 by the builder - a post
            // narrower than the creature is a unit vibrating on a point.
            public float holdRadius = 8f;

            // When the post is stamped, in seconds after spawn. Same default and
            // the same reason as `followDelay`: a spawner THROWS its minions at
            // `projectileSpeed`, so the post the player meant is where the throw
            // lands, not the barrel it left.
            public float holdDelay = 0.6f;

            // Only used on a minion with no mover of its own to walk home with -
            // a unit in a `Wait` sub-state, or a prefab with no movement
            // components at all. Everything else walks back under its own power
            // at its own speed, which is the whole point of the design.
            public float holdSpeed;

            // ---- the SWARM cap (spawner WEAPONS only) -------------------
            //
            // The gadget has a cap built in - `level` - but
            // MinionSpawnerWeapon.FireSingle has NO count check of any kind, so
            // a spawner weapon is a firehose. This cap is entirely ours,
            // including what happens when you reach it.
            public int cap;                              // 0 = no cap
            public int capMode = ForgeSwarm.CapKillOldest;

            // Extra cap per module level above 1. **Opt-in (0 = off)** on
            // purpose: a weapon's level normally drives FloatSeries stats and a
            // spawner has none, so levelling one would otherwise do nothing at
            // all and quietly waste a BoosterCore. Set this and the builder
            // switches canBeBoosted on too, since the level cannot move without
            // it.
            public int capPerLevel;

            // Hand each minion the spawning cluster's other modules, the way the
            // drone GADGET does. OFF by default: a swarm is meant to be cheap,
            // and twenty minions each carrying your full grid is a different
            // weapon.
            public bool inheritGrid;

            // Resolved at build time to the module that carries it - a weapon
            // reaches a grid as a WeaponModule, never as bare WeaponData.
            [NonSerialized] public WeaponModuleData weaponModule;

            // So a bad name warns once rather than on every drone spawned.
            [NonSerialized] public bool resolveFailed;

            // Once per config: a swarm equips one weapon per spawn, and
            // this warning is about the FILE, not about the creature.
            [NonSerialized] public bool warnedNoAmmo;

            [NonSerialized] public Faction factionAsset;
            [NonSerialized] public bool factionFailed;
        }

        private static readonly Dictionary<WeaponData, Config> _weapons =
            new Dictionary<WeaponData, Config>();

        public static void Register(WeaponData spawner, Config config)
        {
            if (spawner != null && config != null)
                _weapons[spawner] = config;
        }

        public static bool TryGet(WeaponData spawner, out Config config)
        {
            config = null;
            return spawner != null && _weapons.TryGetValue(spawner, out config);
        }

        public static bool Any { get { return _weapons.Count > 0; } }

        // Find the module that carries the named weapon, once.
        //
        // Deferred to first use on purpose: the name is usually another Forge
        // weapon, and at build time file order decides whether that one exists
        // yet. Resolving here means it never matters.
        private static bool Resolve(Config cfg)
        {
            if (cfg.weaponModule != null)
                return true;

            if (cfg.resolveFailed || string.IsNullOrEmpty(cfg.weapon))
                return false;

            // A Forge weapon first - it is the common case, and it is already
            // registered as a WeaponModuleData.
            foreach (ForgeEntry entry in ForgeRegistry.Entries)
            {
                if (entry == null || entry.module == null)
                    continue;

                // Matched on either name a person might reasonably type: the
                // loadout name the file declared, or the display name.
                if (string.Equals(entry.loadoutName, cfg.weapon,
                                  StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(entry.displayName, cfg.weapon,
                                  StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(entry.module.name, cfg.weapon,
                                  StringComparison.OrdinalIgnoreCase))
                {
                    cfg.weaponModule = entry.module as WeaponModuleData;
                    break;
                }
            }

            // The loop above matches loadoutName / displayName / module.name,
            // and NONE of those is the weapon's own "name" field - which is the
            // string sitting in front of anyone reading their own .json, and so
            // the first thing they type. `"weapon": "Boomerang"` failed for a
            // weapon whose file says exactly that, and the warning blamed the
            // name. So: resolve the WeaponData through the shared lookup (which
            // knows "name", "displayName" and "Forge Weapon <name>"), then find
            // the entry that owns it.
            if (cfg.weaponModule == null)
            {
                WeaponData wanted = ForgeWeaponLookup.Find(cfg.weapon);

                if (wanted != null)
                {
                    foreach (ForgeEntry entry in ForgeRegistry.Entries)
                    {
                        if (entry == null)
                            continue;

                        var wm = entry.module as WeaponModuleData;

                        if (wm != null && ReferenceEquals(wm.weapon, wanted))
                        {
                            cfg.weaponModule = wm;
                            break;
                        }
                    }
                }
            }

            // Otherwise a stock module asset by name.
            if (cfg.weaponModule == null)
            {
                cfg.weaponModule =
                    JsonFieldMapper.FindAsset(
                        typeof(WeaponModuleData), cfg.weapon)
                    as WeaponModuleData;
            }

            if (cfg.weaponModule == null)
            {
                cfg.resolveFailed = true;

                Log.LogWarning(
                    "minion \"weapon\": '" + cfg.weapon + "' is neither one of " +
                    "your weapons nor a weapon module this game has. The drone " +
                    "keeps its stock weapon. " + ForgeWeaponLookup.Available());

                return false;
            }

            return true;
        }

        // `faction` is private and serialized on Unit, so it takes a plain
        // FieldInfo. Deliberately NOT Harmony's FieldRefAccess<T,F>, which
        // throws on a value-type mismatch and has bitten this codebase before.
        private static FieldInfo _factionField;
        private static bool _factionLooked;

        private static FieldInfo FactionField
        {
            get
            {
                if (!_factionLooked)
                {
                    _factionLooked = true;
                    _factionField = AccessTools.Field(typeof(Unit), "faction");

                    if (_factionField == null)
                    {
                        Log.LogWarning(
                            "Could not reach Unit.faction - minions will keep " +
                            "whatever side their prefab is on. Watch out for " +
                            "the hostile spawnables.");
                    }
                }

                return _factionField;
            }
        }

        // PUBLIC ACCESS TO THE FACTION FIELD, so charm does not grow a second
        // copy of this reflection. Same reasoning as ForgeSteer owning
        // idealDirection: three copies of one private-field lookup is how they
        // drift apart.
        public static Faction GetFaction(Unit unit)
        {
            FieldInfo f = FactionField;

            return (unit == null || f == null) ? null : f.GetValue(unit) as Faction;
        }

        public static bool SetFaction(Unit unit, Faction side)
        {
            FieldInfo f = FactionField;

            if (unit == null || f == null || side == null)
                return false;

            f.SetValue(unit, side);
            return true;
        }

        // Will this minion end up on the summoner's side?
        //
        // Needed at BUILD time, before any minion exists, because retargeting
        // its ammo means cloning a prefab and that has to happen once - not per
        // spawn. "owner" is the default, so the answer is normally yes.
        public static bool SidesWithOwner(Config cfg)
        {
            if (cfg == null)
                return false;

            string want = (cfg.faction ?? "owner").Trim();

            return want.Length == 0 ||
                   want.Equals("owner", StringComparison.OrdinalIgnoreCase) ||
                   want.Equals("player", StringComparison.OrdinalIgnoreCase) ||
                   want.Equals("friendly", StringComparison.OrdinalIgnoreCase);
        }

        // Put the minion on the right side.
        public static void SetSide(Unit minion, Unit owner, Config cfg)
        {
            if (minion == null || cfg == null)
                return;

            string want = (cfg.faction ?? "owner").Trim();

            if (want.Length == 0 ||
                want.Equals("keep", StringComparison.OrdinalIgnoreCase))
                return;

            FieldInfo f = FactionField;

            if (f == null)
                return;

            try
            {
                object side;

                if (want.Equals("owner", StringComparison.OrdinalIgnoreCase) ||
                    want.Equals("player", StringComparison.OrdinalIgnoreCase) ||
                    want.Equals("friendly", StringComparison.OrdinalIgnoreCase))
                {
                    // Read it off the summoner rather than naming a faction, so
                    // this stays correct in co-op and for anything else that
                    // ever spawns minions.
                    if (owner == null)
                        return;

                    side = f.GetValue(owner);
                }
                else
                {
                    if (cfg.factionAsset == null && !cfg.factionFailed)
                    {
                        cfg.factionAsset =
                            JsonFieldMapper.FindAsset(typeof(Faction), want)
                                as Faction;

                        if (cfg.factionAsset == null)
                        {
                            cfg.factionFailed = true;

                            Log.LogWarning(
                                "minion \"faction\": '" + want + "' is not a " +
                                "faction this game has - try \"owner\", " +
                                "\"keep\", \"Cross\", \"Fly\", \"Maggot\", " +
                                "\"Crawler\" or \"Player\". Leaving the " +
                                "prefab's own side alone.");
                        }
                    }

                    if (cfg.factionAsset == null)
                        return;

                    side = cfg.factionAsset;
                }

                if (side != null)
                    f.SetValue(minion, side);
            }
            catch (Exception e)
            {
                Log.LogError("Setting a minion's faction failed: " + e);
            }
        }

        // Swap what the minion fires.
        //
        // The minion's own grid is a SimpleModuleGrid seeded with its stock
        // weapon as the main module; replacing that and letting the holder
        // rebuild is the supported route, rather than assigning a WeaponBase
        // (which the next cluster refresh would throw away - the same rebuild
        // trap that shaped ForgeKillBuff).
        public static void Equip(Unit minion, Config cfg)
        {
            if (minion == null || cfg == null)
                return;

            if (!Resolve(cfg))
                return;

            try
            {
                var owner = minion.GetComponentInChildren<ModuleGridOwner>();

                if (owner == null)
                    return;

                var grid = owner.ModuleGrid as SimpleModuleGrid;

                if (grid == null)
                {
                    Log.LogWarning(
                        "A minion has no SimpleModuleGrid, so its weapon " +
                        "cannot be replaced. Its stock weapon stays.");
                    return;
                }

                IModuleCluster cluster =
                    grid.GetCluster(ClusterType.PrimaryWeapon);

                var simple = cluster as SimpleModuleCluster;

                if (simple == null)
                    return;

                // DeepCopy, not the registered instance: a Module carries live
                // per-instance state (its Level, its rolled PowerLevel), and
                // handing every minion the same object would make them share it.
                var module = cfg.weaponModule.DeepCopy() as WeaponModule;

                if (module == null)
                    return;

                // Setting MainModule is what RefreshWeapon actually reads, and
                // the setter also fires the cluster's refresh - so the holder
                // rebuilds the weapon on its own.
                simple.MainModule = module;

                WarnIfItCannotPayForItsGun(minion, cfg);
            }
            catch (Exception e)
            {
                Log.LogError("Equipping a minion failed: " + e);
            }
        }

        // "IT STOPS FIRING AFTER ATTACKING A BIT" - and he had to work that out
        // himself.
        //
        // R13 test 150, on the picket drone: *"I think i forgot to add white
        // regen to the drone so it stops firing after attacking a bit."* Right
        // diagnosis, entirely unaided - a drone carrying one of his weapons
        // spends that weapon's `cost` per shot out of its own tank, and a
        // minion whose stats give it no regen for that resource fires until the
        // tank empties and then goes quiet forever.
        //
        // Nothing said so anywhere. `Shooter.Update` gates every shot on
        // `OwnerHasResource` and simply does not fire - no log, no sound, no
        // tell. A drone that shoots for four seconds and then sits there is
        // indistinguishable from a broken feature.
        //
        // This is checked at EQUIP rather than at build time on purpose: a
        // carried weapon is resolved on first spawn (it may not exist yet when
        // the file is read), and the minion's tanks are rebuilt from module
        // effects, so the unit is the only place both facts are true at once.
        // Once per config, because a swarm equips one of these per spawn.
        private static void WarnIfItCannotPayForItsGun(Unit minion, Config cfg)
        {
            if (cfg == null || cfg.warnedNoAmmo || cfg.weaponModule == null)
                return;

            try
            {
                WeaponData wd = cfg.weaponModule.weapon;

                if (wd == null || wd.cost <= 0f || wd.resourceUsed == null)
                    return;

                cfg.warnedNoAmmo = true;

                bool hasTank = minion.HasTank(wd.resourceUsed);

                // The minion's own regen for that resource, as this mod set it.
                // `regen` is what `ForgeMinionStats` writes, so an author who
                // scaled it has already thought about this.
                bool hasRegen =
                    cfg.regen != null && cfg.regen.ContainsKey(wd.resourceUsed);

                // A capacity the author set counts as having thought about it -
                // a big enough tank is a legitimate answer to "how does it pay
                // for its gun", it just runs out eventually.
                bool hasPool =
                    cfg.resources != null &&
                    cfg.resources.ContainsKey(wd.resourceUsed);

                if (!hasTank)
                {
                    Log.LogWarning(
                        "minion \"weapon\": '" + cfg.weapon + "' costs " +
                        wd.cost.ToString("0.##") + " " +
                        wd.resourceUsed.name + " a shot and this minion has " +
                        "NO TANK of that resource at all - so it will never " +
                        "fire, silently. The game checks the tank before every " +
                        "shot and just does not shoot. Give the minion that " +
                        "resource in its \"stats\", or set the weapon's " +
                        "\"cost\": 0.");
                    return;
                }

                if (!hasRegen && !hasPool)
                {
                    Log.LogWarning(
                        "minion \"weapon\": '" + cfg.weapon + "' costs " +
                        wd.cost.ToString("0.##") + " " +
                        wd.resourceUsed.name + " a shot and this minion has " +
                        "NO REGEN for it - so it fires until its tank runs dry " +
                        "and then goes QUIET, with nothing in the log to say " +
                        "so. That reads exactly like the weapon breaking. Add " +
                        "a regen for " + wd.resourceUsed.name + " in the " +
                        "minion's \"stats\", raise its capacity, or set the " +
                        "weapon's \"cost\": 0 (a summoned drone paying your " +
                        "resource prices is usually not the intent).");
                }
            }
            catch (Exception)
            {
                // Advisory only - see the standing rule about validation being
                // the most defensive code in a file.
            }
        }

        // Install the minion's OWN weapon, tuned.
        //
        // This needs its own entry point rather than a branch inside `Equip`,
        // because `Equip` calls `Resolve`, which bails on an empty `weapon`
        // name - and an empty name is exactly the case this serves.
        //
        // The clone was made once at load time (ForgeMinionArms). All that
        // happens here is installing a per-minion DeepCopy of it, which is the
        // same route `Equip` uses and for the same reason: `MainModule` is what
        // `ModuleSlotWeaponHolder.RefreshWeapon` reads, and its setter fires the
        // cluster refresh, so the holder rebuilds the weapon itself.
        //
        // Deliberately NOT done by writing the live WeaponBase: that object is
        // either null at this moment (the prefabs whose guns are built in
        // Start) or about to be disposed by the next cluster refresh - and the
        // gadget path triggers one right after us.
        public static void EquipDefault(Unit minion, Config cfg)
        {
            if (minion == null || cfg == null || cfg.defaultWeaponModule == null)
                return;

            // A file that named a weapon gets that weapon; the tuned copy of
            // the prefab's own gun would be overwriting a deliberate choice.
            // (The author's weapon stats and effects were applied to whichever
            // weapon they chose, so nothing is lost.)
            if (!string.IsNullOrEmpty(cfg.weapon))
                return;

            try
            {
                var owner = minion.GetComponentInChildren<ModuleGridOwner>();

                if (owner == null)
                    return;

                var grid = owner.ModuleGrid as SimpleModuleGrid;

                if (grid == null)
                    return;

                var simple =
                    grid.GetCluster(ClusterType.PrimaryWeapon)
                        as SimpleModuleCluster;

                if (simple == null)
                    return;

                var module = cfg.defaultWeaponModule.DeepCopy() as WeaponModule;

                if (module == null)
                    return;

                simple.MainModule = module;
            }
            catch (Exception e)
            {
                Log.LogError(
                    "Installing a minion's tuned default weapon failed: " + e);
            }
        }
    }
}
