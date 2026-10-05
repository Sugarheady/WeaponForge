using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using MyBox;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace WeaponForge
{
    // Tune the gun a minion prefab ALREADY carries, and bolt explosion,
    // lightning and burn onto it.
    //
    // THE TRAP THIS FILE EXISTS TO AVOID. A minion's weapon is named by
    // `Enemy.primaryWeapon`, a public WeaponModuleData, whose `weapon` field
    // points at a WeaponData ScriptableObject **that is shared with live
    // enemies**. `Weapon_Cross.asset` is the gun of eight prefabs - the
    // Queen's Child minion and the ordinary Enemy_Cross among them - so adding
    // an explosion to "your" minion's weapon by writing that asset would hand
    // every Cross in the run an explosive gun, permanently, until the process
    // restarted. `WeaponModule`'s constructor is `this.WeaponData =
    // moduleData.weapon;`, a bare reference, so DeepCopy does NOT save you: it
    // clones the module and keeps pointing at the same weapon.
    //
    // So: clone both, once, at load time, and edit the clone.
    //
    // WHY NOT THE GAME'S OWN AddExplosionEffect / AddDischargeEffect /
    // AddBurnEffect, which look purpose-built for this. Because they are
    // `IHasPerProjectileCost`, and `ProjectileWeapon.FireSingle` spends that
    // cost with `Owner.GetTank(effect.CostResource).Value -= ...` - unguarded.
    // A cost of zero passes the affordability check, `GetTank` returns null for
    // a resource the unit has no tank for, and it throws *after* the projectile
    // is instantiated but *before* Shoot() - leaving an inert bullet sitting at
    // the muzzle on every trigger pull. Writing the weapon's own `explosion`,
    // `discharge` and `burn` fields reaches the same code with no resource
    // check anywhere in the path.
    public static class ForgeMinionArms
    {
        private static readonly ManualLogSource Log =
            ForgeLog.Source("WeaponForge.Minion");

        // Every clone we hand out has to be findable by id, because a minion is
        // a savable entity and its grid is saved as a list of module ids.
        // `Module.Memento.Restore()` is `registry.Get(moduleDataId).DeepCopy()`
        // with NO null check, so an unregistered id is a NullReferenceException
        // inside the game's load, not a warning.
        private static readonly List<ModuleData> _made =
            new List<ModuleData>();

        public static IEnumerable<ModuleData> Made { get { return _made; } }

        public static bool Any { get { return _made.Count > 0; } }

        // ------------------------------------------------------------------
        // Called once per weapon/gadget file that asks for any of this.
        public static void Build(
            Unit minionPrefab,
            JObject minionJson,
            ForgeMinion.Config cfg,
            string fileName)
        {
            if (cfg == null || minionJson == null)
                return;

            var stats = minionJson["weaponStats"] as JObject;
            var boom = minionJson["explosion"] as JObject;
            var zap = minionJson["lightning"] as JObject;
            JToken burnTok = minionJson["burn"];

            // A FIFTH reason to clone, and the file need not ask for it: a
            // minion on your side firing its own stock gun fires ENEMY
            // projectiles, which the layer matrix lets hit you and not the
            // things it is aiming at. Retargeting that ammo means owning a copy
            // of the prefab, so it belongs here with the rest of the cloning.
            // See ForgeMinionAllegiance.
            bool retargetAmmo =
                cfg.friendlyShots && ForgeMinion.SidesWithOwner(cfg);

            // Did the FILE ask for any of this, or are we only here for the
            // ammo? Every refusal below is worth saying out loud when the
            // author asked for something and did not get it, and is pure noise
            // when they did not - and after the ammo retarget this method runs
            // for nearly every minion weapon, so the difference matters.
            bool asked =
                stats != null || boom != null || zap != null || burnTok != null;

            if (!asked && !retargetAmmo)
                return;

            // A named weapon is the author's deliberate choice, and their
            // tweaks belong on it - but that is the ordinary weapon-building
            // path, not this one. Say so rather than doing nothing. (Nor does
            // it need the ammo retarget: a Forge weapon is already a player
            // weapon, firing on the player's projectile layer.)
            if (!string.IsNullOrEmpty(cfg.weapon))
            {
                if (asked)
                {
                    Log.LogWarning(
                        fileName + ": minion - \"weaponStats\" / \"explosion\" " +
                        "/ \"lightning\" / \"burn\" tune the gun THE PREFAB " +
                        "comes with, and this minion was given \"" + cfg.weapon +
                        "\" instead. Put those settings in that weapon's own " +
                        "file. Ignored here.");
                }

                return;
            }

            if (minionPrefab == null)
            {
                if (asked)
                {
                    Log.LogWarning(
                        fileName + ": minion - cannot tune the prefab's weapon " +
                        "because the minion prefab itself could not be " +
                        "resolved.");
                    cfg.defaultWeaponFailed = true;
                }

                return;
            }

            var enemy = minionPrefab.GetComponentInChildren<Enemy>(true);

            if (enemy == null || enemy.primaryWeapon == null)
            {
                if (asked)
                {
                    Log.LogWarning(
                        fileName + ": minion - '" + minionPrefab.name +
                        "' names no primary weapon, so there is nothing to " +
                        "tune. Some spawnables build their guns a different " +
                        "way (the Queen and the mines carry theirs as fixed " +
                        "turrets) and a few carry none at all. Every other " +
                        "minion setting still applies.");
                    cfg.defaultWeaponFailed = true;
                }

                return;
            }

            if (minionPrefab.GetComponentInChildren<ModuleSlotWeaponHolder>(true)
                == null)
            {
                if (asked)
                {
                    Log.LogWarning(
                        fileName + ": minion - '" + minionPrefab.name +
                        "' has no module-driven weapon holder, so a " +
                        "replacement weapon would never be picked up. Refusing " +
                        "to tune it rather than pretending. Every other minion " +
                        "setting still applies.");
                    cfg.defaultWeaponFailed = true;
                }

                return;
            }

            if (enemy.primaryWeapon.weapon == null)
            {
                if (asked)
                {
                    Log.LogWarning(
                        fileName + ": minion - '" + minionPrefab.name +
                        "' has a weapon module with no weapon in it. Nothing " +
                        "to tune.");
                    cfg.defaultWeaponFailed = true;
                }

                return;
            }

            try
            {
                Clone(enemy, minionPrefab, cfg, fileName, asked);

                if (cfg.defaultWeaponModule == null)
                    return;

                WeaponData wd = cfg.defaultWeaponModule.weapon;

                if (stats != null)
                    Stats(wd, stats, cfg, fileName);

                if (boom != null)
                    Explosion(wd, boom, cfg, fileName);

                if (zap != null)
                    Lightning(wd, zap, cfg, fileName);

                if (burnTok != null)
                    Burn(wd, burnTok, cfg, fileName);

                if (retargetAmmo &&
                    ForgeMinionAllegiance.Ammo(wd, cfg, fileName))
                {
                    Log.LogInfo(
                        fileName + ": minion shots retargeted to the player's " +
                        "projectile layer, so they hurt what the minion is " +
                        "aiming at. A shot's faction IS the layer of the prefab " +
                        "it came from, and this gun's ammo was on the enemy " +
                        "layer - which the collision matrix lets hit YOU and " +
                        "not the Entities layer everything it fights is on. " +
                        "Set \"friendlyShots\": false to leave it as the enemy " +
                        "built it.");
                }

                Safety(wd, fileName);
            }
            catch (Exception e)
            {
                cfg.defaultWeaponFailed = true;
                cfg.defaultWeaponModule = null;

                Log.LogError(
                    fileName + ": tuning a minion's own weapon failed, so it " +
                    "keeps its stock gun: " + e);
            }
        }

        // ------------------------------------------------------------------
        private static void Clone(
            Enemy enemy,
            Unit minionPrefab,
            ForgeMinion.Config cfg,
            string fileName,
            bool asked)
        {
            WeaponModuleData module =
                ScriptableObject.Instantiate(enemy.primaryWeapon);
            WeaponData weapon =
                ScriptableObject.Instantiate(enemy.primaryWeapon.weapon);

            // The whole point: the module gets its OWN weapon rather than
            // sharing the prefab's.
            module.weapon = weapon;

            // Named off the PREFAB, not off the author's display name, because
            // the id goes into save files. A display name can be renamed
            // between sessions; a save holding the old id would then fail to
            // load, and the game's restore path has no null check.
            string id = "Forge Minion Weapon " + minionPrefab.name;

            module.name = id;
            weapon.name = id;

            var idField =
                typeof(ModuleData).GetField(
                    "id",
                    BindingFlags.NonPublic | BindingFlags.Instance);

            if (idField != null)
            {
                idField.SetValue(
                    module,
                    "FORGE-MINIONGUN-" +
                    minionPrefab.name.ToUpperInvariant().Replace(" ", "-"));
            }
            else
            {
                Log.LogWarning(
                    fileName + ": could not set the id on the minion's weapon " +
                    "module. It will still work this session, but a save made " +
                    "with this minion alive may not load.");
            }

            cfg.defaultWeaponModule = module;
            _made.Add(module);

            Log.LogInfo(
                fileName + ": minion - cloned '" + minionPrefab.name +
                "'s own weapon as \"" + id + "\"." +
                (asked
                    ? " The stock asset is untouched, which matters: that gun " +
                      "is shared with the enemies you fight."
                    : ""));
        }

        // ---- the twelve weapon numbers -----------------------------------
        private static void Stats(
            WeaponData wd, JObject o, ForgeMinion.Config cfg, string fileName)
        {
            bool mult = cfg.statMode == ForgeMinionStats.Multiply;
            int done = 0;

            done += F(o, "fireRate", ref wd.fireRate, mult);
            done += I(o, "burstSize", ref wd.burstSize, mult);
            done += F(o, "burstDelay", ref wd.burstDelay, mult);
            done += F(o, "projectileCount", ref wd.projectileCount, mult);
            done += F(o, "spread", ref wd.spread, mult);
            done += F(o, "angleVariance", ref wd.angleVariance, mult);
            done += F(o, "angleOffset", ref wd.angleOffset, mult);
            done += F(o, "knockbackForce", ref wd.knockbackForce, mult);
            done += F(o, "pushForce", ref wd.pushForce, mult);
            done += F(o, "warmupTime", ref wd.warmupTime, mult);

            JToken dmg = o["damage"];

            if (dmg != null && dmg.Type != JTokenType.Null)
            {
                float amount = (float)dmg;

                wd.damage = new Damage(
                    mult ? wd.damage.amount * amount : amount,
                    wd.damage.damageType);

                done++;
            }

            string typeName = (string)o["damageType"];

            if (!string.IsNullOrEmpty(typeName))
            {
                Resource res = Res(typeName, "weaponStats.damageType", fileName);

                if (res != null)
                {
                    wd.damage = new Damage(wd.damage.amount, res);
                    done++;
                }
            }

            var pw = wd as ProjectileWeaponData;

            if (pw != null)
            {
                done += F(o, "projectileSpeed", ref pw.projectileSpeed, mult);
                done += F(o, "projectileRadius", ref pw.projectileRadius, mult);

                JToken range = o["range"];

                if (range != null && range.Type != JTokenType.Null)
                {
                    ProjectileRangeData rd = pw.rangeData;
                    rd.enabled = true;
                    rd.range = mult
                        ? rd.range * (float)range
                        : (float)range;
                    pw.rangeData = rd;
                    done++;
                }
            }
            else if (o["projectileSpeed"] != null || o["range"] != null)
            {
                Log.LogWarning(
                    fileName + ": minion \"weaponStats\" - this prefab's gun " +
                    "is not a projectile weapon, so \"projectileSpeed\" and " +
                    "\"range\" do not apply to it. The other stats did.");
            }

            if (done > 0)
            {
                Log.LogInfo(
                    fileName + ": minion weaponStats - " + done +
                    " value(s) changed on its own gun (" +
                    ForgeMinionStats.ModeWord(cfg.statMode) + ").");
            }
        }

        // ---- explosion ----------------------------------------------------
        private static void Explosion(
            WeaponData wd, JObject o, ForgeMinion.Config cfg, string fileName)
        {
            if (!Enabled(o, true))
                return;

            Explosion ex = wd.explosion;

            // Explosion is a STRUCT whose `damages` is a List reference. On a
            // fresh Instantiate the list is the clone's own, but a brand-new
            // explosion on a weapon that never had one has a null list.
            if (ex.damages == null)
                ex.damages = new List<Damage>();

            JToken radius = o["radius"];

            if (radius != null && radius.Type != JTokenType.Null)
            {
                ex.radius = cfg.statMode == ForgeMinionStats.Multiply
                    ? ex.radius * (float)radius
                    : (float)radius;
            }
            else if (ex.radius <= 0f)
            {
                ex.radius = 1.5f;

                Log.LogInfo(
                    fileName + ": minion explosion - no \"radius\" given and " +
                    "the gun had none, so 1.5 was used.");
            }

            JToken damage = o["damage"];

            if (damage != null && damage.Type != JTokenType.Null)
            {
                Resource type =
                    Res((string)o["damageType"], "explosion.damageType",
                        fileName) ?? wd.damage.damageType;

                ex.AddDamage(new Damage((float)damage, type));
            }

            JToken burn = o["burn"];

            if (burn != null && burn.Type != JTokenType.Null)
                ex.Burn = ex.Burn.Add((float)burn);

            wd.explosion = ex;

            bool onImpact = (bool?)o["onImpact"] ?? true;
            bool onTimeout = (bool?)o["onTimeout"] ?? false;

            var pw = wd as ProjectileWeaponData;

            if (pw == null)
            {
                Log.LogWarning(
                    fileName + ": minion \"explosion\" - this prefab's gun is " +
                    "hitscan (an instant beam, no bullet), and the hitscan " +
                    "code never reads explosion data. Only \"burn\" works on " +
                    "this one. Explosion ignored.");
                return;
            }

            if (onImpact)
            {
                ProjectileImpactBehaviour ib = pw.impactBehaviour;
                ib.enabled = true;
                ib.spawnExplosion = true;
                pw.impactBehaviour = ib;
            }

            if (onTimeout)
            {
                ProjectileLifetimeData ld = pw.lifetimeData;
                ld.enabled = true;
                ld.spawnExplosion = true;
                pw.lifetimeData = ld;
            }

            if (!onImpact && !onTimeout)
            {
                Log.LogWarning(
                    fileName + ": minion \"explosion\" - both \"onImpact\" and " +
                    "\"onTimeout\" are off, so nothing will ever trigger it.");
                return;
            }

            Log.LogInfo(
                fileName + ": minion explosion - radius " +
                wd.explosion.radius.ToString("0.##") + ", " +
                wd.explosion.damages.Count + " damage type(s), on " +
                (onImpact ? "impact" : "") +
                (onImpact && onTimeout ? " and " : "") +
                (onTimeout ? "timeout" : "") + ".");

            if (cfg.hurtsOwner)
            {
                Log.LogWarning(
                    fileName + ": minion \"explosion\" - THIS WILL HURT YOU. " +
                    "Explosions in this game have no owner filter, and the AI " +
                    "parks a minion within a few units of what it is shooting. " +
                    "Set \"hurtsOwner\": false to be immune to your own " +
                    "minions' blasts (and \"hurtsAllies\": false so they stop " +
                    "killing each other).");
            }
        }

        // ---- lightning ----------------------------------------------------
        private static void Lightning(
            WeaponData wd, JObject o, ForgeMinion.Config cfg, string fileName)
        {
            if (!Enabled(o, true))
                return;

            DischargeData dd = wd.discharge;

            JToken chain = o["chainLength"];

            if (chain != null && chain.Type != JTokenType.Null)
            {
                dd.chainLength = cfg.statMode == ForgeMinionStats.Multiply
                    ? Mathf.RoundToInt(dd.chainLength * (float)chain)
                    : (int)chain;
            }
            else if (dd.chainLength <= 0)
            {
                dd.chainLength = 2;
            }

            JToken damage = o["damage"];

            if (damage != null && damage.Type != JTokenType.Null)
            {
                Resource type =
                    Res((string)o["damageType"], "lightning.damageType",
                        fileName) ?? dd.damage.damageType ?? wd.damage.damageType;

                dd.damage = new Damage(
                    cfg.statMode == ForgeMinionStats.Multiply
                        ? dd.damage.amount * (float)damage
                        : (float)damage,
                    type);
            }
            else if (dd.damage.damageType == null)
            {
                dd.damage = new Damage(dd.damage.amount, wd.damage.damageType);
            }

            // Two settings that are the difference between real lightning and
            // a light show, and both are wrong by default on the stock drone
            // gun.
            //
            // subSystem: `ElectricityManager.Register` early-returns for
            // anything that is not the Player subsystem, so an unset or Enemy
            // value means no beams spawn at all.
            if (dd.subSystem != ElectricityManager.SubSystemType.Player)
            {
                dd.subSystem = ElectricityManager.SubSystemType.Player;

                Log.LogInfo(
                    fileName + ": minion lightning - subsystem set to Player, " +
                    "without which the game spawns no beams at all.");
            }

            // layerMask: the beam builds its contact filter from this, so zero
            // means the arcs draw and damage nothing. The stock Weapon Drone
            // ships exactly that.
            if (dd.layerMask.value == 0)
            {
                dd.layerMask = 1 << LayerMask.NameToLayer("Entities");

                if (dd.layerMask.value == 0)
                    dd.layerMask = 1 << 7;

                Log.LogInfo(
                    fileName + ": minion lightning - layer mask was 0, which " +
                    "draws arcs that damage NOTHING. Set to Entities.");
            }

            wd.discharge = dd;

            bool onImpact = (bool?)o["onImpact"] ?? true;
            bool onTimeout = (bool?)o["onTimeout"] ?? false;
            var pw = wd as ProjectileWeaponData;

            if (pw != null)
            {
                if (onImpact)
                {
                    ProjectileImpactBehaviour ib = pw.impactBehaviour;
                    ib.enabled = true;
                    ib.discharge = true;
                    pw.impactBehaviour = ib;
                }

                if (onTimeout)
                {
                    ProjectileLifetimeData ld = pw.lifetimeData;
                    ld.enabled = true;
                    ld.discharge = true;
                    pw.lifetimeData = ld;
                }
            }

            // Chain length only means something if there is a conductor to
            // chain THROUGH. That patch is global and off unless some weapon
            // asks for it, so asking is part of the feature.
            if ((bool?)o["chainThroughEnemies"] ?? true)
            {
                var electric = new ForgeElectric.Config();
                electric.chainThroughEnemies = true;
                ForgeElectric.Register(wd, electric);
            }

            Log.LogInfo(
                fileName + ": minion lightning - chain " + dd.chainLength +
                ", damage " + dd.damage.amount.ToString("0.#") + ", on " +
                (onImpact ? "impact" : "") +
                (onImpact && onTimeout ? " and " : "") +
                (onTimeout ? "timeout" : "") + ".");
        }

        // ---- burn ---------------------------------------------------------
        private static void Burn(
            WeaponData wd, JToken tok, ForgeMinion.Config cfg, string fileName)
        {
            float amount;

            var o = tok as JObject;

            if (o != null)
            {
                if (!Enabled(o, true))
                    return;

                JToken a = o["amount"];

                if (a == null || a.Type == JTokenType.Null)
                    return;

                amount = (float)a;
            }
            else
            {
                amount = (float)tok;
            }

            wd.burn = cfg.statMode == ForgeMinionStats.Multiply
                ? new MinMaxFloat(wd.burn.Min * amount, wd.burn.Max * amount)
                : new MinMaxFloat(amount, amount);

            Log.LogInfo(
                fileName + ": minion burn " + amount.ToString("0.##") +
                " - this raises how LONG a target burns, not how hard. The " +
                "damage per tick belongs to whatever is on fire, not to the " +
                "weapon.");
        }

        // ---- the two guards every clone needs ----------------------------
        private static void Safety(WeaponData wd, string fileName)
        {
            // A null damageType in the explosion list is a crash, and a
            // spectacular one: the game sorts damages by amount descending and
            // then reads `damages[0].damageType.explosionBasePrefab`. It throws
            // BEFORE the projectile is destroyed, so the shot also never dies
            // and keeps re-damaging whatever it is touching, every frame.
            WeaponBuilder.FixMinionExplosionTypes(wd, fileName);

            // NO MINION HAS A RESOURCE TANK FOR THE THING YOUR WEAPONS SPEND.
            // `Shooter.OwnerHasResource` is `Cost == 0 || GetResource(...) >=
            // Cost`, and GetResource returns 0 for a tank that does not exist -
            // so a costed weapon on a minion simply never fires, chirping the
            // no-resource sound once per shot. Every stock minion weapon ships
            // cost 0 for exactly this reason.
            if (wd.cost != 0f)
            {
                Log.LogWarning(
                    fileName + ": minion - its weapon cost " +
                    wd.cost.ToString("0.##") + " was forced to 0. A minion has " +
                    "no resource tanks of its own, so any cost above zero means " +
                    "it never fires at all - it just chirps. Give the minion a " +
                    "pool with \"resources\" if you actually want it to pay.");

                wd.cost = 0f;
            }
        }

        // ---- little readers ----------------------------------------------
        private static bool Enabled(JObject o, bool fallback)
        {
            bool on = (bool?)o["enabled"] ?? fallback;

            return on;
        }

        private static int F(JObject o, string key, ref float into, bool mult)
        {
            JToken t = o[key];

            if (t == null || t.Type == JTokenType.Null)
                return 0;

            into = mult ? into * (float)t : (float)t;
            return 1;
        }

        private static int I(JObject o, string key, ref int into, bool mult)
        {
            JToken t = o[key];

            if (t == null || t.Type == JTokenType.Null)
                return 0;

            into = mult
                ? Mathf.Max(1, Mathf.RoundToInt(into * (float)t))
                : (int)t;

            return 1;
        }

        private static Resource Res(string name, string what, string fileName)
        {
            if (string.IsNullOrEmpty(name))
                return null;

            var res =
                JsonFieldMapper.FindAsset(typeof(Resource), name) as Resource;

            if (res == null)
            {
                Log.LogWarning(
                    fileName + ": minion \"" + what + "\" - '" + name +
                    "' is not a resource this game has. Ignored.");
            }

            return res;
        }
    }
}
