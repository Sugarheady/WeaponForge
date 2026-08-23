using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx.Logging;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace WeaponForge
{
    // Turns one weapon definition JSON file into a configured, ready-to-
    // register module (clone of a template module + weapon with the JSON
    // overrides applied). Does NOT touch the loadout pool or the registry
    // — ForgeRegistry owns those steps.
    public static class WeaponBuilder
    {
        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("WeaponForge");

        // A subEmitter names another weapon, which may well be one of YOUR
        // weapons sitting in a file that sorts later in the folder. Resolving
        // it during the build made success depend on filename order -
        // "cocktail.json" sorts before "cocktailFlame.json", so the cocktail
        // looked for a flame that did not exist yet and silently got none.
        // So the name is parked here and resolved once every file is built,
        // which also lets chains of any depth work regardless of order.
        private class PendingSub
        {
            public WeaponData weapon;
            public string subName;
            public string fileName;
        }

        private static readonly List<PendingSub> _pendingSubs =
            new List<PendingSub>();

        public static void ResolvePendingSubEmitters()
        {
            foreach (PendingSub p in _pendingSubs)
            {
                if (p.weapon == null || string.IsNullOrEmpty(p.subName))
                    continue;

                // Accept the bare name too: a Forge weapon called "Flame"
                // becomes the asset "Forge Weapon Flame", and expecting
                // people to know that prefix is a needless trip hazard.
                var sub = JsonFieldMapper.FindAsset(
                    typeof(WeaponData), p.subName) as WeaponData;

                if (sub == null && !p.subName.StartsWith("Forge Weapon "))
                {
                    sub = JsonFieldMapper.FindAsset(
                        typeof(WeaponData),
                        "Forge Weapon " + p.subName) as WeaponData;
                }

                if (sub == null)
                {
                    Log.LogWarning(
                        p.fileName + ": subEmitter '" + p.subName +
                        "' was not found, so this weapon fires no sub. " +
                        "For one of your own weapons use its \"name\" (or " +
                        "\"Forge Weapon <name>\"); for a stock one use the " +
                        "asset name like \"Weapon Caps Flame\".");
                    continue;
                }

                if (sub == p.weapon)
                {
                    Log.LogWarning(
                        p.fileName + ": subEmitter points at this same " +
                        "weapon - that would recurse forever, so it is " +
                        "ignored.");
                    continue;
                }

                p.weapon.subEmitter = sub;

                Log.LogInfo(
                    p.fileName + ": subEmitter -> '" + sub.name + "'.");
            }

            _pendingSubs.Clear();
        }

        // Returns null if the weapon can't/shouldn't be built (already
        // built, missing required keys, or missing assets).
        public static ForgeEntry BuildModule(
            string filePath,
            HashSet<string> alreadyBuilt)
        {
            string fileName = Path.GetFileName(filePath);

            JObject root =
                JObject.Parse(File.ReadAllText(filePath));

            string name = (string)root["name"];

            if (string.IsNullOrEmpty(name))
            {
                Log.LogError(
                    fileName + ": missing required \"name\"");
                return null;
            }

            // SPACES INSIDE A NAME ARE FINE. Nothing splits or tokenises it -
            // it becomes an asset name, a loadout name and a module id, all of
            // which are compared whole. "Gas Grenade" works.
            //
            // Whitespace at the EDGES is not fine, and it is invisible in a text
            // editor. The weapon's asset ends up named "Forge Weapon MyGun "
            // with the trailing space baked in, while every lookup trims what
            // you type - so `"weapon": "MyGun"` from a split or a minion block
            // would never find it, and the log would tell you the name did not
            // exist while showing you that exact name in the available list.
            if (name != name.Trim())
            {
                Log.LogWarning(
                    fileName + ": \"name\" had whitespace at the start or end " +
                    "('" + name + "'), which is invisible in an editor and " +
                    "would stop other files finding this weapon by name. " +
                    "Trimmed to '" + name.Trim() + "'. Spaces INSIDE a name " +
                    "are fine and are left alone.");

                name = name.Trim();
            }

            if (name.Length == 0)
            {
                Log.LogError(
                    fileName + ": \"name\" is nothing but whitespace.");
                return null;
            }

            // A name that is ALSO the name of a stock asset is a silent
            // shadowing bug rather than an error: every "name another weapon"
            // feature resolves an exact asset name first, so `split` or
            // `minion.weapon` pointing at this name would quietly get the
            // GAME's weapon instead of yours, and behave almost right.
            if (JsonFieldMapper.FindAsset(typeof(WeaponData), name) != null)
            {
                Log.LogWarning(
                    fileName + ": \"name\" is '" + name + "', which is already " +
                    "the name of a weapon in the game. Your weapon still " +
                    "builds, but anything that names it - a split's " +
                    "\"weapon\", a minion's \"weapon\", a turret - would " +
                    "resolve to the GAME's weapon instead of yours, and look " +
                    "almost right. Pick a name of your own.");
            }

            string loadoutName = "Forge_" + name;

            if (alreadyBuilt != null &&
                alreadyBuilt.Contains(loadoutName))
            {
                return null;
            }

            string templateName = (string)root["template"];

            if (string.IsNullOrEmpty(templateName))
            {
                Log.LogError(
                    fileName + ": missing required \"template\" " +
                    "(a weapon module like \"Module Weapon White " +
                    "Popper\", or a raw weapon like \"Weapon Grunt\")");
                return null;
            }

            // slot decides where the weapon goes and therefore what
            // kind of module shell wraps it. Weapon-type modules only
            // fit weapon slots; gadget (active) modules only fit the
            // 1/2/3 slots - that restriction is the game's ModuleType
            // compatibility, which we get right by cloning a shell of
            // the correct type.
            string slot =
                ((string)root["slot"] ?? "primary")
                    .Trim().ToLowerInvariant();

            bool isGadget =
                slot == "gadget1" || slot == "gadget2" ||
                slot == "gadget3" || slot == "gadget";

            if (slot == "gadget")
                slot = "gadget1";

            // A DRONE GADGET is not a weapon and cannot go down the weapon
            // path at all - a SpawnMinionModuleData has no WeaponData on it, so
            // everything below would fail at "template has no weapon", which is
            // exactly the refusal that used to be documented on the page.
            // Branch out before that.
            var templateDrone =
                JsonFieldMapper.FindAsset(
                    typeof(SpawnMinionModuleData),
                    templateName) as SpawnMinionModuleData;

            if (templateDrone != null)
            {
                return BuildDrone(
                    root, templateDrone, name, loadoutName, displayNameOf(root, name),
                    slot, isGadget, fileName);
            }

            // Resolve the template. It can be a weapon module, a gadget
            // module (WeaponBasedActiveModuleData - e.g. Air Mine), or a
            // raw weapon asset (enemy weapons). The weapon BEHAVIOR comes
            // from whichever it is; the SLOT decides the module type we
            // wrap it in.
            var templateModule =
                JsonFieldMapper.FindAsset(
                    typeof(WeaponModuleData),
                    templateName) as WeaponModuleData;

            var templateGadget =
                templateModule != null
                    ? null
                    : JsonFieldMapper.FindAsset(
                        typeof(WeaponBasedActiveModuleData),
                        templateName) as WeaponBasedActiveModuleData;

            WeaponData weaponSource;

            if (templateModule != null)
                weaponSource = templateModule.weapon;
            else if (templateGadget != null)
                weaponSource = templateGadget.weaponData;
            else
                weaponSource =
                    JsonFieldMapper.FindAsset(
                        typeof(WeaponData), templateName) as WeaponData;

            if (weaponSource == null)
            {
                Log.LogError(
                    fileName + ": template '" + templateName +
                    "' not found or has no weapon (expected a weapon " +
                    "module, a gadget module, or a weapon asset)");
                return null;
            }

            string displayName =
                (string)root["displayName"] ??
                name.ToUpperInvariant();

            string description =
                (string)root["description"] ??
                "Custom weapon built by Weapon Forge.";

            var weapon =
                ScriptableObject.Instantiate(weaponSource);

            weapon.name = "Forge Weapon " + name;

            // Build the module shell of the right type for the slot,
            // preferring to clone the template itself when its native
            // type already matches the slot (keeps its icon/behavior).
            ModuleData module =
                BuildShell(
                    isGadget,
                    templateModule,
                    templateGadget,
                    fileName);

            if (module == null)
                return null;

            module.name = "Forge Module " + name;

            var idField =
                typeof(ModuleData).GetField(
                    "id",
                    BindingFlags.NonPublic |
                    BindingFlags.Instance);

            if (idField != null)
            {
                idField.SetValue(
                    module,
                    "FORGE-" + name.ToUpperInvariant());
            }

            AssignWeapon(module, weapon);

            module.displayName = displayName;
            module.description = description;

            string projectileColor = null;
            float? projectileScale = null;
            float rainbowSpeed = 0.5f;
            string muzzleColor = null;
            float muzzleRgbSpeed = 0.5f;
            string muzzleSprite = null;
            float? muzzleSpriteFps = null;
            string impactSfx = null;
            string impactSfxOn = null;
            string impactSprite = null;
            float? impactSpriteFps = null;
            string projectileSprite = null;
            string subEmitterName = null;
            bool spriteOnly = false;
            string projectileMaterial = null;
            bool? projectileGlow = null;
            ForgeTrail.Spec trailSpec = null;
            ForgeBeam.Spec beamSpec = null;

            // Burn options (enemy burn tick-rate + burn color) - not real
            // weapon fields, so pulled out and turned into module effects.
            // Particle SHAPE for the muzzle flash and the impact effect -
            // how many puffs, how big, and whether they turn with the
            // emitter. Not real weapon fields, so pulled out by hand.
            int? muzzleCount = null;
            float? muzzleSize = null;
            bool? muzzleRotate = null;
            float? muzzleSpread = null;
            bool? muzzleFollow = null;
            int? impactCount = null;
            float? impactSize = null;
            bool? impactRotate = null;
            float? impactSpread = null;
            string impactColor = null;
            float impactRgbSpeed = 0.5f;
            string impactColorMode = null;

            JToken burnTickRateTok = null;
            string burnColor = null;
            float burnRgbSpeed = 0.5f;
            bool burnColorTerrain = false;

            var weaponJson = root["weapon"] as JObject;

            // Remember the template's resources so we can restore them
            // if the JSON tries to switch to an unusable (shared) one.
            Resource originalResourceUsed = weapon.resourceUsed;
            Resource originalDamageType = weapon.damage.damageType;

            // The template's own sound ids, captured BEFORE the mapper can
            // overwrite them. A custom sound inherits its volume, mixer group
            // and looping from whatever it is replacing, so we need to know
            // what was in each slot to begin with.
            SfxSlots originalSfx = CaptureSfx(weapon);

            if (weaponJson != null)
            {
                // Pull custom visual aliases out before the generic
                // mapper sees them (they aren't real weapon fields).
                projectileColor =
                    (string)weaponJson["projectileColor"];

                projectileScale =
                    (float?)weaponJson["projectileScale"];

                rainbowSpeed =
                    (float?)weaponJson["rainbowSpeed"] ?? 0.5f;

                muzzleColor = (string)weaponJson["muzzleColor"];
                muzzleRgbSpeed =
                    (float?)weaponJson["muzzleRgbSpeed"] ?? 0.5f;

                muzzleSprite = (string)weaponJson["muzzleSprite"];
                muzzleSpriteFps =
                    (float?)weaponJson["muzzleSpriteFps"];
                muzzleCount = (int?)weaponJson["muzzleCount"];
                muzzleSize = (float?)weaponJson["muzzleSize"];
                muzzleRotate = (bool?)weaponJson["muzzleRotate"];
                muzzleSpread = (float?)weaponJson["muzzleSpread"];
                muzzleFollow = (bool?)weaponJson["muzzleFollow"];

                impactSfx = (string)weaponJson["impactSfx"];
                impactSfxOn = (string)weaponJson["impactSfxOn"];
                impactSprite = (string)weaponJson["impactSprite"];
                impactSpriteFps =
                    (float?)weaponJson["impactSpriteFps"];
                impactCount = (int?)weaponJson["impactCount"];
                impactSize = (float?)weaponJson["impactSize"];
                impactRotate = (bool?)weaponJson["impactRotate"];
                impactSpread = (float?)weaponJson["impactSpread"];
                impactColor = (string)weaponJson["impactColor"];
                impactRgbSpeed =
                    (float?)weaponJson["impactRgbSpeed"] ?? 0.5f;
                impactColorMode = (string)weaponJson["impactColorMode"];

                projectileSprite =
                    (string)weaponJson["projectileSprite"];

                projectileMaterial =
                    (string)weaponJson["projectileMaterial"];
                projectileGlow = (bool?)weaponJson["projectileGlow"];

                // Held back from the mapper and resolved after every file
                // is built - see ResolvePendingSubEmitters.
                subEmitterName = (string)weaponJson["subEmitter"];

                spriteOnly =
                    (bool?)weaponJson["projectileSpriteOnly"] ?? false;

                trailSpec =
                    ForgeTrail.Parse(weaponJson["trail"], fileName);

                beamSpec =
                    ForgeBeam.Parse(weaponJson["beam"], fileName);

                burnTickRateTok = weaponJson["burnTickRate"];
                burnColor = (string)weaponJson["burnColor"];
                burnRgbSpeed =
                    (float?)weaponJson["burnRgbSpeed"] ?? 0.5f;
                burnColorTerrain =
                    (bool?)weaponJson["burnColorTerrain"] ?? false;

                weaponJson.Remove("projectileColor");
                weaponJson.Remove("projectileScale");
                weaponJson.Remove("rainbowSpeed");
                weaponJson.Remove("muzzleColor");
                weaponJson.Remove("muzzleRgbSpeed");
                weaponJson.Remove("muzzleSprite");
                weaponJson.Remove("muzzleSpriteFps");
                weaponJson.Remove("muzzleCount");
                weaponJson.Remove("muzzleSize");
                weaponJson.Remove("muzzleRotate");
                weaponJson.Remove("muzzleSpread");
                weaponJson.Remove("muzzleFollow");
                weaponJson.Remove("impactSfx");
                weaponJson.Remove("impactSfxOn");
                weaponJson.Remove("impactSprite");
                weaponJson.Remove("impactSpriteFps");
                weaponJson.Remove("impactCount");
                weaponJson.Remove("impactSize");
                weaponJson.Remove("impactRotate");
                weaponJson.Remove("impactSpread");
                weaponJson.Remove("impactColor");
                weaponJson.Remove("impactRgbSpeed");
                weaponJson.Remove("impactColorMode");
                weaponJson.Remove("projectileSprite");
                weaponJson.Remove("projectileMaterial");
                weaponJson.Remove("projectileGlow");
                weaponJson.Remove("subEmitter");
                weaponJson.Remove("projectileSpriteOnly");
                weaponJson.Remove("trail");
                weaponJson.Remove("beam");
                weaponJson.Remove("burnTickRate");
                weaponJson.Remove("burnColor");
                weaponJson.Remove("burnRgbSpeed");
                weaponJson.Remove("burnColorTerrain");

                JsonFieldMapper.Apply(
                    weapon,
                    weaponJson,
                    name + ".weapon");

                // Any sound field that now names a file from the "sounds"
                // folder gets swapped for a real registered Sfx id. Done
                // AFTER the mapper because the sfx fields are ordinary
                // strings - the mapper writes them like any other field, and
                // we only reinterpret what it wrote.
                ApplyCustomSounds(weapon, originalSfx, fileName);

                FixExplosionDamageTypes(weapon, fileName);

                if (!string.IsNullOrEmpty(subEmitterName))
                {
                    _pendingSubs.Add(new PendingSub
                    {
                        weapon = weapon,
                        subName = subEmitterName.Trim(),
                        fileName = fileName
                    });
                }
            }

            // A weapon that fires from a SHARED resource (e.g. Money)
            // makes the game install a per-unit ammo tank that collides
            // with the run-wide shared tank -> duplicate-key crash that
            // hangs loading. Fall back to the template's resource.
            if (weapon.resourceUsed != null &&
                weapon.resourceUsed.isShared)
            {
                Log.LogWarning(
                    fileName + ": resourceUsed '" +
                    weapon.resourceUsed.name + "' is a shared/currency " +
                    "resource and can't power a weapon (it would hang " +
                    "the game) - keeping '" +
                    (originalResourceUsed != null
                        ? originalResourceUsed.name
                        : "template default") + "' instead.");

                weapon.resourceUsed = originalResourceUsed;
            }

            // Same story for the damage element: a shared resource
            // (Money) as damageType is busted, so revert it.
            if (weapon.damage.damageType != null &&
                weapon.damage.damageType.isShared)
            {
                Log.LogWarning(
                    fileName + ": damage type '" +
                    weapon.damage.damageType.name + "' is a shared/" +
                    "currency resource and isn't usable - keeping '" +
                    (originalDamageType != null
                        ? originalDamageType.name
                        : "template default") + "' instead.");

                var dmg = weapon.damage;
                dmg.damageType = originalDamageType;
                weapon.damage = dmg;
            }

            // target: who the weapon hurts. "enemies" (default) makes it
            // hit enemies and not the player - this also fixes enemy
            // weapon templates, which otherwise only hurt the player.
            // "player" keeps the original enemy-style targeting.
            string target =
                ((string)root["target"] ?? "enemies")
                    .Trim().ToLowerInvariant();

            // BEFORE ApplyVisuals, deliberately. A beam's impact sound has to
            // ride on the beam visual, and ApplyVisuals is what clones that
            // visual and hands it to ForgeBeam.Apply - so the guid must
            // already be on the spec by then. (For projectiles the order does
            // not matter: ApplyVisuals clones again and Instantiate carries
            // the ForgeImpact component across.)
            ApplyImpact(
                weapon,
                impactSfx,
                impactSfxOn,
                impactSprite,
                impactSpriteFps,
                impactCount,
                impactSize,
                impactRotate,
                impactSpread,
                impactColor,
                impactRgbSpeed,
                impactColorMode,
                beamSpec,
                fileName);

            ApplyVisuals(
                weapon,
                projectileColor,
                projectileScale,
                rainbowSpeed,
                target,
                fileName,
                projectileSprite,
                projectileMaterial,
                projectileGlow,
                spriteOnly,
                trailSpec,
                beamSpec);

            ApplyMuzzle(
                weapon,
                muzzleColor,
                muzzleRgbSpeed,
                muzzleSprite,
                muzzleSpriteFps,
                muzzleCount,
                muzzleSize,
                muzzleRotate,
                muzzleSpread,
                muzzleFollow,
                fileName);

            ApplyBurn(
                module,
                burnTickRateTok,
                burnColor,
                burnRgbSpeed,
                burnColorTerrain,
                fileName);

            ApplyElectric(weapon, root, fileName);

            ApplyOrbit(weapon, root, fileName);

            ApplyPhasing(weapon, root, fileName);

            ApplyPierce(weapon, root, fileName);

            ApplyWave(weapon, root, fileName);

            // After wave: both clone the projectile prefab and both attach a
            // FixedUpdate-driven steerer, so running homing second means it
            // lands on the clone wave already made rather than a stale one.
            ApplyHoming(weapon, root, fileName);

            // Last of the prefab-cloning motion features, so each lands on the
            // clone the previous ones already made.
            ApplyRicochet(weapon, root, fileName);

            ApplyBoomerang(weapon, root, fileName);

            ApplyGrowth(weapon, root, fileName);

            ApplyDamageRamp(weapon, root, fileName);

            ApplyChain(weapon, root, fileName);

            ApplyConverge(weapon, root, fileName);

            ApplyDeflect(weapon, root, fileName);

            ApplyTurret(weapon, root, fileName);

            ApplyBounce(weapon, root, fileName);

            ApplySticky(weapon, root, fileName);

            ApplyTether(weapon, root, fileName);

            ApplyGravity(weapon, root, fileName);
            ApplyTractor(weapon, root, fileName);
            ApplyMinion(weapon, root, fileName);
            ApplySlow(weapon, root, fileName);
            ApplyOverheat(weapon, root, slot, fileName);

            ApplyCatalyst(weapon, root, fileName);

            ApplyOnKill(weapon, root, fileName);

            ApplyGas(weapon, root, fileName);

            // After every other prefab-cloning feature, so the prefab a
            // fragment is built from is the FINAL one - carrying the weapon's
            // art, trail, homing, growth and everything else. Clone it earlier
            // and fragments would wear a half-built version of the shot.
            ApplySplit(weapon, root, fileName);

            ApplyShield(weapon, root, isGadget, fileName);

            // AFTER the reflectors, because it attaches itself to whichever of
            // them the file configured.
            ApplyRally(weapon, root, fileName);

            // Last of all: the charge meter borrows the weapon's own bullet
            // art, and every motion feature above re-clones the projectile
            // prefab. Running here means it reads the final one rather than a
            // clone something else has already replaced.
            ApplyCharge(weapon, root, isGadget, fileName);

            var moduleJson = root["module"] as JObject;

            if (moduleJson != null)
            {
                // Friendly aliases handled here, not real ModuleData
                // fields — pull them out before the generic mapper.
                var resourceGain =
                    moduleJson["resourceGain"] as JObject;

                JToken powerNodes = moduleJson["powerNodes"];

                // icon can name a sprite from the sprites folder as well as
                // a stock one, so it needs the Forge lookup rather than the
                // generic mapper's game-asset-only scan.
                JToken icon = moduleJson["icon"];

                if (resourceGain != null)
                    moduleJson.Remove("resourceGain");

                if (powerNodes != null)
                    moduleJson.Remove("powerNodes");

                if (icon != null)
                    moduleJson.Remove("icon");

                JsonFieldMapper.Apply(
                    module,
                    moduleJson,
                    name + ".module");

                if (icon != null)
                {
                    ApplyModuleIcon(
                        module, icon.ToString(), name + ".module.icon");
                }

                if (resourceGain != null)
                {
                    ApplyResourceGain(
                        module,
                        resourceGain,
                        fileName);
                }

                if (powerNodes != null)
                {
                    ApplyPowerNodes(module, powerNodes, fileName);
                }
            }

            ApplyBoostability(
                module, weapon, moduleJson, burnTickRateTok, fileName);

            Log.LogInfo(
                "Built weapon '" + displayName +
                "' from " + fileName);

            // source: where the weapon can appear. "starter" (default),
            // "loot", or "starterAndLoot"/"both".
            string source =
                ((string)root["source"] ?? "starter")
                    .Trim().ToLowerInvariant();

            // "none" = built and registered, but offered NOWHERE. Use it for
            // helper weapons that only exist to be referenced by something
            // else (a subEmitter stage, turret ammo) - they still resolve by
            // name, they just never show up as a pick, a drop or in a crate.
            bool hidden =
                source == "none" || source == "nowhere" ||
                source == "hidden" || source == "never";

            bool inStarter =
                source == "starter" ||
                source == "starterandloot" || source == "both";

            bool inLoot =
                source == "loot" ||
                source == "starterandloot" || source == "both";

            // Unknown value -> default to starter so it isn't lost. ("none"
            // is a deliberate choice, so it skips that safety net.)
            if (!inStarter && !inLoot && !hidden)
                inStarter = true;

            ApplyLootRepeat(module, root, fileName);

            return new ForgeEntry
            {
                loadoutName = loadoutName,
                displayName = displayName,
                description = description,
                baseLoadoutName =
                    (string)root["baseLoadout"] ?? "Starter_Popper",
                module = module,
                slot = slot,
                inStarter = inStarter,
                inLoot = inLoot,
                lootGroups = ParseLootFrom(root, inLoot, fileName),
                lootWeight =
                    (float?)root["lootWeight"] ?? 10f,
                inShop =
                    (bool?)root["shop"] ?? false,
                shopPrice =
                    (float?)root["shopPrice"] ?? 100f,
                shopUnlockLevel =
                    (int?)root["shopUnlockLevel"] ?? 1
            };
        }

        private static string displayNameOf(JObject root, string name)
        {
            return (string)root["displayName"] ?? name.ToUpperInvariant();
        }

        // ------------------------------------------------------------------
        // A DRONE GADGET, authored from JSON
        // ------------------------------------------------------------------
        //
        // Built by CLONING one of the five stock drone gadgets rather than
        // constructing a SpawnMinionModuleData from nothing, and that is not
        // laziness - the game dereferences three things on this type without a
        // null check (particlePrefab on install AND uninstall, the minion's
        // SavableEntity, and the owner's tank for resourceUsed), so a
        // from-scratch module throws the moment it is equipped. A clone
        // inherits all three valid.
        //
        // It also inherits the template's `effects` list, which on these assets
        // lives in an Odin-serialized blob rather than plain YAML - another
        // reason cloning is the only sane route.
        private static ForgeEntry BuildDrone(
            JObject root,
            SpawnMinionModuleData template,
            string name,
            string loadoutName,
            string displayName,
            string slot,
            bool isGadget,
            string fileName)
        {
            if (!isGadget)
            {
                Log.LogWarning(
                    fileName + ": a drone gadget can only go in a gadget slot, " +
                    "so \"slot\" has been changed from '" + slot +
                    "' to \"gadget1\". This is not us being strict: the ship " +
                    "builds its gun from the slot's main module as a WEAPON " +
                    "module, and a spawn-minion module is not one - in a weapon " +
                    "slot it would install, cap correctly, and then do nothing " +
                    "at all when you clicked.");
                slot = "gadget1";
            }

            var module = ScriptableObject.Instantiate(template);
            module.name = "Forge Module " + name;

            var droneIdField =
                typeof(ModuleData).GetField(
                    "id",
                    BindingFlags.NonPublic | BindingFlags.Instance);

            if (droneIdField != null)
            {
                droneIdField.SetValue(
                    module, "FORGE-" + name.ToUpperInvariant());
            }

            module.displayName = displayName;
            module.description =
                (string)root["description"] ??
                "Drone gadget built by Weapon Forge.";

            JObject o = root["drone"] as JObject;

            // ---- the cap, which is the whole point ----
            //
            // `level` IS the minion cap, one for one: Activate refuses at
            // `count >= Level` and OnUpdate destroys the excess. Exposed as
            // "cap" because "level" gives no hint of that, with "level" kept as
            // an alias for anyone reading the raw asset.
            int cap = template.level;

            if (o != null)
                cap = (int?)o["cap"] ?? (int?)o["level"] ?? cap;

            module.level = Mathf.Clamp(cap, 1, 32);

            // ---- the minion ----
            Unit minion = template.minion;

            if (o != null)
            {
                string minionName = (string)o["minion"];

                if (!string.IsNullOrEmpty(minionName))
                {
                    Unit found = ForgeDrone.FindMinion(minionName, fileName);

                    if (found != null)
                    {
                        minion = found;
                    }
                    else
                    {
                        Log.LogWarning(
                            fileName + ": keeping the template's minion ('" +
                            (template.minion != null
                                ? template.minion.name : "none") +
                            "') so the gadget still works.");
                    }
                }
            }

            module.minion = minion;

            if (module.minion == null)
            {
                Log.LogError(
                    fileName + ": drone has no minion prefab at all, and the " +
                    "game dereferences it without checking - refusing to build " +
                    "this rather than let it throw when you press the key.");
                return null;
            }

            // ---- the rest of the spawn behaviour ----
            if (o != null)
            {
                module.cooldown =
                    Mathf.Max(0.05f, (float?)o["cooldown"] ?? template.cooldown);

                module.activationCost =
                    Mathf.Max(0f,
                        (float?)o["cost"] ??
                        (float?)o["activationCost"] ?? template.activationCost);

                string res = (string)o["resource"];

                if (!string.IsNullOrEmpty(res))
                {
                    var r = JsonFieldMapper.FindAsset(
                        typeof(Resource), res) as Resource;

                    if (r == null)
                    {
                        r = JsonFieldMapper.FindAsset(
                            typeof(Resource), "Resource " + res.Trim())
                            as Resource;
                    }

                    if (r != null)
                    {
                        module.resourceUsed = r;
                    }
                    else
                    {
                        Log.LogWarning(
                            fileName + ": drone \"resource\": '" + res +
                            "' not found - keeping the template's (" +
                            (template.resourceUsed != null
                                ? template.resourceUsed.name : "none") +
                            "). A null resource would throw inside Activate.");
                    }
                }

                module.startSpeed = (float?)o["startSpeed"] ?? template.startSpeed;
                module.dropAngle = (float?)o["dropAngle"] ?? template.dropAngle;
                module.dropAngleVariance =
                    (float?)o["dropAngleVariance"] ?? template.dropAngleVariance;

                float ox = (float?)o["spawnOffsetX"] ??
                           template.spawnPositionOffset.x;
                float oy = (float?)o["spawnOffsetY"] ??
                           template.spawnPositionOffset.y;

                module.spawnPositionOffset = new Vector2(ox, oy);
            }

            if (module.resourceUsed == null)
            {
                Log.LogError(
                    fileName + ": drone has no resource, and Activate does " +
                    "GetTank(resourceUsed) with no null check - refusing to " +
                    "build it.");
                return null;
            }

            if (module.particlePrefab == null)
            {
                Log.LogError(
                    fileName + ": drone has no particlePrefab. OnInstalled " +
                    "instantiates it without checking and OnUninstalled " +
                    "dereferences the result, so this would throw the moment " +
                    "the gadget was equipped. Refusing to build it. (Clone one " +
                    "of the five stock drone gadgets and it comes free.)");
                return null;
            }

            // ---- the per-minion options, applied when one actually spawns ----
            var minionJson = root["minion"] as JObject;

            if (minionJson != null)
            {
                var cfg = new ForgeMinion.Config();

                cfg.weapon = ((string)minionJson["weapon"] ?? "").Trim();
                cfg.faction =
                    ((string)minionJson["faction"] ?? "owner").Trim();
                cfg.lifetime =
                    Mathf.Max(0f, (float?)minionJson["lifetime"] ?? 0f);

                ReadMinionLook(
                    cfg,
                    (string)minionJson["color"],
                    (string)minionJson["colorMode"],
                    Mathf.Max(0f, (float?)minionJson["scale"] ?? 1f),
                    (string)minionJson["scaleMode"],
                    fileName);

                ReadMinionStats(cfg, minionJson, fileName);

                ForgeMinionArms.Build(
                    module.minion, minionJson, cfg, fileName);

                // The gadget's cap is its module level, so one at level 1.
                WarnIfHeavy(
                    module.minion,
                    Mathf.Max(1, module.level),
                    fileName);

                if (!string.IsNullOrEmpty(cfg.weapon) ||
                    cfg.lifetime > 0f ||
                    cfg.hasColor || cfg.scale != 1f ||
                    cfg.HasStats ||
                    cfg.defaultWeaponModule != null ||
                    !cfg.hurtsOwner || !cfg.hurtsAllies ||
                    !string.Equals(cfg.faction, "keep",
                        System.StringComparison.OrdinalIgnoreCase))
                {
                    ForgeDrone.Register(module, cfg);

                    // Say it on the CARD, not just in the log. A gadget's card
                    // is built from its description plus its effects, and this
                    // module carries no effects of ours - so without this a
                    // drone whose minions you carefully tuned advertises
                    // nothing about them. A stat you can set is a stat you
                    // should be able to see.
                    string blurb = MinionBlurb(cfg);

                    if (blurb.Length > 0)
                    {
                        module.description =
                            (module.description ?? "").TrimEnd() +
                            "\n" + blurb;
                    }

                    Log.LogInfo(
                        fileName + ": drone minions - " +
                        (string.IsNullOrEmpty(cfg.weapon)
                            ? "stock weapon"
                            : "carry '" + cfg.weapon +
                              "' (resolved on first spawn)") +
                        ", side: " + cfg.faction +
                        (cfg.lifetime > 0f
                            ? ", expiring after " +
                              cfg.lifetime.ToString("0.##") + "s"
                            : ""));
                }
            }

            // Boosting is what raises the cap, so a drone that scales with a
            // BoosterCore needs this on. The stock five all ship it on.
            if (!module.canBeBoosted)
            {
                module.canBeBoosted = true;

                Log.LogInfo(
                    fileName + ": drone - canBeBoosted switched on, so a " +
                    "BoosterCore raises the minion CAP (level is the cap).");
            }

            // The "module" block, exactly as the weapon path treats it, so a
            // drone's icon / colour / power nodes work the same way.
            var droneModuleJson = root["module"] as JObject;

            if (droneModuleJson != null)
            {
                JToken dIcon = droneModuleJson["icon"];
                JToken dNodes = droneModuleJson["powerNodes"];
                var dGain = droneModuleJson["resourceGain"] as JObject;

                if (dIcon != null)
                    droneModuleJson.Remove("icon");

                if (dNodes != null)
                    droneModuleJson.Remove("powerNodes");

                if (dGain != null)
                    droneModuleJson.Remove("resourceGain");

                JsonFieldMapper.Apply(
                    module, droneModuleJson, name + ".module");

                if (dIcon != null)
                {
                    ApplyModuleIcon(
                        module, dIcon.ToString(), name + ".module.icon");
                }

                if (dGain != null)
                    ApplyResourceGain(module, dGain, fileName);

                if (dNodes != null)
                    ApplyPowerNodes(module, dNodes, fileName);
            }

            ApplyLootRepeat(module, root, fileName);

            // Same source parsing as a weapon: starter / loot /
            // starterAndLoot / none.
            string droneSource =
                ((string)root["source"] ?? "starter")
                    .Trim().ToLowerInvariant();

            bool droneHidden =
                droneSource == "none" || droneSource == "nowhere" ||
                droneSource == "hidden" || droneSource == "never";

            bool inStarter =
                droneSource == "starter" ||
                droneSource == "starterandloot" || droneSource == "both";

            bool inLoot =
                droneSource == "loot" ||
                droneSource == "starterandloot" || droneSource == "both";

            if (!inStarter && !inLoot && !droneHidden)
                inStarter = true;

            bool inShopFlag = (bool?)root["shop"] ?? false;

            Log.LogInfo(
                fileName + ": drone gadget '" + displayName + "' - cap " +
                module.level + ", minion '" + module.minion.name + "', " +
                module.activationCost.ToString("0.##") + " " +
                module.resourceUsed.name + " per drone, cooldown " +
                module.cooldown.ToString("0.##") + "s, slot " + slot + ".");

            return new ForgeEntry
            {
                loadoutName = loadoutName,
                displayName = displayName,
                description = module.description,
                baseLoadoutName =
                    (string)root["baseLoadout"] ?? "Starter_Popper",
                module = module,
                slot = slot,
                inStarter = inStarter,
                inLoot = inLoot,
                lootGroups = ParseLootFrom(root, inLoot, fileName),
                lootWeight = (float?)root["lootWeight"] ?? 10f,
                inShop = inShopFlag,
                shopPrice = (float?)root["shopPrice"] ?? 100f,
                shopUnlockLevel = (int?)root["shopUnlockLevel"] ?? 1
            };
        }

        // "lootFrom": which crate pools this weapon may drop from.
        //
        // Absent (or "all") keeps the original behaviour - every module pool -
        // because that is what "source": "loot" has always meant. A list picks
        // specific ones. Returns null for "all".
        private static string[] ParseLootFrom(
            JObject root,
            bool inLoot,
            string fileName)
        {
            JToken token = root["lootFrom"];

            if (token == null || token.Type == JTokenType.Null)
                return null;

            if (!inLoot)
            {
                Log.LogWarning(
                    fileName + ": \"lootFrom\" was set but this weapon is not " +
                    "loot-enabled, so it can never drop. Add " +
                    "\"source\": \"loot\" (or \"both\") too.");
            }

            var names = new List<string>();

            if (token.Type == JTokenType.String)
            {
                names.Add((string)token);
            }
            else if (token is JArray)
            {
                foreach (JToken t in (JArray)token)
                {
                    string s = (string)t;

                    if (!string.IsNullOrEmpty(s))
                        names.Add(s);
                }
            }
            else
            {
                Log.LogWarning(
                    fileName + ": \"lootFrom\" should be a name or a list of " +
                    "names (" + ForgeLootPools.FriendlyList() + ") - ignored.");
                return null;
            }

            var resolved = new List<string>();

            foreach (string name in names)
            {
                string trimmed = (name ?? string.Empty).Trim();

                if (trimmed.Equals("all", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.Equals("any", StringComparison.OrdinalIgnoreCase))
                {
                    return null;   // every pool
                }

                string canonical;

                if (!ForgeLootPools.TryResolve(trimmed, out canonical))
                {
                    Log.LogWarning(
                        fileName + ": lootFrom '" + trimmed + "' is not a " +
                        "known crate pool - use " +
                        ForgeLootPools.FriendlyList() + ". Skipped.");
                    continue;
                }

                if (!ForgeLootPools.IsSupported(canonical))
                {
                    Log.LogWarning(
                        fileName + ": lootFrom '" + trimmed + "' resolves to '" +
                        canonical + "', which the GAME never rolls and which " +
                        "cannot be grafted on. A weapon in that pool could " +
                        "never drop. Skipped.");
                    continue;
                }

                // Money and Level 2 have no module roll of their own, so
                // naming one makes the mod ADD one to that crate. Say so - it
                // changes what a stock crate gives, which is worth knowing.
                string table;

                if (ForgeLootPools.NeedsGraft(canonical, out table))
                {
                    Log.LogInfo(
                        fileName + ": '" + trimmed + "' has no module drop in " +
                        "the base game, so a module roll will be ADDED to '" +
                        table + "'. That crate keeps everything it normally " +
                        "drops and gains a module on top." +
                        (canonical == ForgeLootPools.Level2
                            ? " This also revives the 5 stock regen/generator " +
                              "modules in that pool, which the game otherwise " +
                              "never rolls - your weapon competes with them."
                            : " Only Forge weapons targeting \"money\" are in " +
                              "that pool, so one of them always drops."));
                }

                if (!resolved.Contains(canonical))
                    resolved.Add(canonical);
            }

            if (resolved.Count == 0)
            {
                Log.LogWarning(
                    fileName + ": \"lootFrom\" left no usable pools, so this " +
                    "weapon falls back to dropping from ALL of them.");
                return null;
            }

            Log.LogInfo(
                fileName + ": drops only from " +
                string.Join(", ", resolved.ToArray()));

            return resolved.ToArray();
        }

        // "lootRepeat": may the same weapon drop more than once in a run?
        //
        // The game's own anti-duplicate rule lives on the module:
        // DroppabbleItemDistribution.GetWeight multiplies an entry's weight by
        // repeatedDropChanceMultiplyer ONCE FOR EACH copy already dropped this
        // run. 120 of ~145 stock modules set it to 0, so a module you already
        // own drops to weight 0 and cannot appear again.
        //
        // Our module is a private clone, so changing this affects only this
        // weapon - it can never make a stock module start repeating.
        private static void ApplyLootRepeat(
            ModuleData module,
            JObject root,
            string fileName)
        {
            JToken token = root["lootRepeat"];

            if (module == null || token == null ||
                token.Type == JTokenType.Null)
            {
                return;   // keep whatever the template shell had
            }

            float value;

            if (token.Type == JTokenType.Boolean)
            {
                // true = full chance every time, false = the stock "once only".
                value = (bool)token ? 1f : 0f;
            }
            else
            {
                // Strings are accepted deliberately: the builder page writes
                // this field as text, and anyone hand-editing is just as likely
                // to type "true" as true. Rejecting those would turn a
                // reasonable file into a silent no-op.
                if (!TryReadRepeat(token, out value))
                {
                    Log.LogWarning(
                        fileName + ": \"lootRepeat\" should be true, false, or " +
                        "a number from 0 to 1 - got '" + token +
                        "', ignored.");
                    return;
                }

                value = Mathf.Max(0f, value);

                if (value > 1f)
                {
                    Log.LogWarning(
                        fileName + ": \"lootRepeat\": " + value +
                        " is above 1, which makes the weapon MORE likely to " +
                        "drop again the more you already have. Legal, but " +
                        "probably not what you meant - 1 keeps the chance " +
                        "unchanged.");
                }
            }

            module.repeatedDropChanceMultiplyer = value;

            Log.LogInfo(
                fileName + ": lootRepeat " + value +
                (value <= 0f
                    ? " (drops once per run, the stock behaviour)"
                    : (value >= 1f
                        ? " (can drop again at full chance)"
                        : " (each copy you own makes the next x" + value +
                          " as likely)")));
        }

        // Accepts a real number, or the words a person would actually type.
        private static bool TryReadRepeat(JToken token, out float value)
        {
            value = 0f;

            if (token.Type == JTokenType.Integer ||
                token.Type == JTokenType.Float)
            {
                float? n = (float?)token;

                if (!n.HasValue)
                    return false;

                value = n.Value;
                return true;
            }

            string s = ((string)token ?? string.Empty).Trim();

            if (s.Length == 0)
                return false;

            if (s.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                s.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
                s.Equals("on", StringComparison.OrdinalIgnoreCase))
            {
                value = 1f;
                return true;
            }

            if (s.Equals("false", StringComparison.OrdinalIgnoreCase) ||
                s.Equals("no", StringComparison.OrdinalIgnoreCase) ||
                s.Equals("off", StringComparison.OrdinalIgnoreCase))
            {
                value = 0f;
                return true;
            }

            return float.TryParse(
                s,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out value);
        }

        // Default shells to clone for the module type each slot needs.
        // The shell supplies the ModuleType (weapon vs active) that the
        // slot's compatibility check requires, plus icon/plumbing.
        private const string DefaultWeaponShell =
            "Module Weapon White Popper";
        private const string DefaultGadgetShell =
            "Module Active Purple AirMines";

        private static ModuleData BuildShell(
            bool isGadget,
            WeaponModuleData templateModule,
            WeaponBasedActiveModuleData templateGadget,
            string fileName)
        {
            if (!isGadget)
            {
                // Weapon slot: clone the template weapon module directly
                // when it is one (keeps its icon/color). Otherwise (raw
                // weapon, or a gadget template dropped into a weapon
                // slot) clone the default weapon-module shell for its
                // weapon ModuleType.
                var shell = templateModule;

                if (shell == null)
                {
                    shell =
                        JsonFieldMapper.FindAsset(
                            typeof(WeaponModuleData),
                            DefaultWeaponShell) as WeaponModuleData;
                }

                if (shell == null)
                {
                    Log.LogError(
                        fileName + ": weapon module shell '" +
                        DefaultWeaponShell + "' not found");
                    return null;
                }

                return ScriptableObject.Instantiate(shell);
            }

            // Gadget slot: clone the template gadget module directly when
            // the template IS a gadget (keeps its icon + native gadget
            // behavior). Otherwise (a weapon template turned into a
            // gadget) clone the default gadget shell for its "active"
            // ModuleType.
            var gadgetShell = templateGadget;

            if (gadgetShell == null)
            {
                gadgetShell =
                    JsonFieldMapper.FindAsset(
                        typeof(WeaponBasedActiveModuleData),
                        DefaultGadgetShell)
                        as WeaponBasedActiveModuleData;
            }

            if (gadgetShell == null)
            {
                Log.LogError(
                    fileName + ": gadget shell '" +
                    DefaultGadgetShell + "' not found");
                return null;
            }

            return ScriptableObject.Instantiate(gadgetShell);
        }

        // Weapon-module shells store the weapon in `weapon`, gadget
        // shells in `weaponData`.
        private static void AssignWeapon(
            ModuleData module,
            WeaponData weapon)
        {
            var weaponModule = module as WeaponModuleData;

            if (weaponModule != null)
            {
                weaponModule.weapon = weapon;
                return;
            }

            var gadgetModule = module as WeaponBasedActiveModuleData;

            if (gadgetModule != null)
            {
                gadgetModule.weaponData = weapon;
            }
        }

        // Recolor / resize the weapon's projectile or beam AND set who
        // it hurts (target). Prefabs are cloned only when something
        // actually changes. Behavior per weapon type is documented in
        // the README / builder page.
        private static void ApplyVisuals(
            WeaponData weapon,
            string colorText,
            float? scale,
            float rainbowSpeed,
            string target,
            string fileName,
            string projectileSpriteName,
            string projectileMaterialName,
            bool? projectileGlow,
            bool spriteOnly,
            ForgeTrail.Spec trailSpec,
            ForgeBeam.Spec beamSpec)
        {
            Color? color = null;
            bool rainbow = false;

            // Custom art from the "sprites" folder. Its own namespace, so
            // the name can never clash with the game's ~450 atlas sprites.
            ForgeSpriteLibrary.Art customArt = null;
            if (!string.IsNullOrEmpty(projectileSpriteName))
            {
                if (!ForgeSpriteLibrary.TryGetArt(
                        projectileSpriteName, out customArt))
                {
                    string known = ForgeSpriteLibrary.Count > 0
                        ? " Loaded sprites: " +
                          string.Join(", ",
                              System.Linq.Enumerable.ToArray(
                                  ForgeSpriteLibrary.Names))
                        : " No custom sprites loaded at all - is there a " +
                          "PNG in the 'sprites' folder next to the DLL?";

                    Log.LogWarning(
                        fileName + ": projectileSprite '" +
                        projectileSpriteName + "' was not found." + known);
                }
            }

            if (!string.IsNullOrEmpty(colorText))
            {
                string c = colorText.Trim();

                if (c.Equals("rainbow", StringComparison.OrdinalIgnoreCase) ||
                    c.Equals("rgb", StringComparison.OrdinalIgnoreCase))
                {
                    rainbow = true;
                }
                else
                {
                    Color parsed;

                    if (VisualCustomizer.TryParseColor(c, out parsed))
                    {
                        color = parsed;
                    }
                    else
                    {
                        Log.LogWarning(
                            fileName + ": projectileColor '" +
                            colorText + "' is not a valid color");
                    }
                }
            }

            // Resolve the render material. "glow" is the plain-English
            // switch between the game's own two sprite materials; an
            // explicit name is the escape hatch for any other one.
            string materialName = projectileMaterialName;
            if (string.IsNullOrEmpty(materialName) && projectileGlow.HasValue)
            {
                materialName = projectileGlow.Value
                    ? "EmissiveUnlitSprite"
                    : "SpriteUnlitAA";
            }

            Material customMaterial = null;
            if (!string.IsNullOrEmpty(materialName))
            {
                customMaterial = JsonFieldMapper.FindAsset(
                    typeof(Material), materialName) as Material;

                if (customMaterial == null)
                {
                    Log.LogWarning(
                        fileName + ": material '" + materialName +
                        "' was not found - the shot keeps the template's. " +
                        "The two that matter are 'SpriteUnlitAA' (draws art " +
                        "as painted) and 'EmissiveUnlitSprite' (glows).");
                }
            }

            bool hasVisual =
                color.HasValue || rainbow || scale.HasValue ||
                customArt != null || customMaterial != null ||
                spriteOnly || trailSpec != null ||
                beamSpec != null;

            int fromLayer = VisualCustomizer.FactionFromLayer(target);
            int toLayer = VisualCustomizer.FactionToLayer(target);

            var projectileData = weapon as ProjectileWeaponData;

            if (projectileData != null)
            {
                var newProjectile =
                    ReskinProjectile(
                        projectileData.projectilePrefab != null
                            ? projectileData.projectilePrefab.gameObject
                            : null,
                        color, rainbow, rainbowSpeed, scale,
                        fromLayer, toLayer, hasVisual, customArt,
                        customMaterial, spriteOnly,
                        trailSpec);

                if (newProjectile != null)
                {
                    var comp =
                        newProjectile
                            .GetComponentInChildren<Projectile>(true);

                    if (comp != null)
                        projectileData.projectilePrefab = comp;

                    if (scale.HasValue)
                        projectileData.projectileRadius *= scale.Value;
                }

                if (projectileData.usePhysics &&
                    projectileData.physicsProjectilePrefab != null)
                {
                    var pp =
                        ReskinProjectile(
                            projectileData
                                .physicsProjectilePrefab.gameObject,
                            color, rainbow, rainbowSpeed, scale,
                            fromLayer, toLayer, hasVisual, customArt,
                            customMaterial, spriteOnly,
                            trailSpec);

                    if (pp != null)
                    {
                        var comp =
                            pp.GetComponentInChildren<PhysicsProjectile>(
                                true);

                        if (comp != null)
                            projectileData.physicsProjectilePrefab = comp;
                    }
                }

                return;
            }

            var hitscanData = weapon as HitscanWeaponData;

            if (hitscanData != null)
            {
                // Hitscan targeting is purely the layerMask (a data
                // field - no prefab clone needed).
                hitscanData.layerMask =
                    VisualCustomizer.HitscanMask(target);

                if (hasVisual && hitscanData.visual != null)
                {
                    GameObject clone =
                        VisualCustomizer.ClonePrefab(
                            hitscanData.visual.gameObject);

                    var visual =
                        clone.GetComponent<HitscanWeaponVisual>();

                    // Art before the tint, same order as the projectile path:
                    // the tint writes the renderer's colour, so it has to land
                    // on whichever sprite ends up sitting there.
                    if (beamSpec != null)
                        ForgeBeam.Apply(visual, beamSpec);

                    Paint(clone, color, rainbow, rainbowSpeed);

                    if (scale.HasValue)
                        VisualCustomizer.ScaleBeamThickness(
                            visual, scale.Value);

                    hitscanData.visual = visual;
                }

                return;
            }

            var physicsData = weapon as PhysicsWeaponData;

            if (physicsData != null)
            {
                var pp =
                    ReskinProjectile(
                        physicsData.projectilePrefab != null
                            ? physicsData.projectilePrefab.gameObject
                            : null,
                        color, rainbow, rainbowSpeed, scale,
                        fromLayer, toLayer, hasVisual,
                        trailSpec: trailSpec);

                if (pp != null)
                {
                    var comp =
                        pp.GetComponentInChildren<Rigidbody2D>(true);

                    if (comp != null)
                        physicsData.projectilePrefab = comp;
                }

                return;
            }

            var minionData = weapon as MinionSpawnerWeaponData;

            if (minionData != null)
            {
                // Minions have their own Unit faction (not a projectile
                // layer), so "target" doesn't apply here - only recolor.
                // Scaling a Unit can break its AI/colliders, so skip it.
                if (minionData.minionPrefab != null &&
                    (color.HasValue || rainbow))
                {
                    GameObject clone =
                        VisualCustomizer.ClonePrefab(
                            minionData.minionPrefab.gameObject);

                    Paint(clone, color, rainbow, rainbowSpeed);

                    minionData.minionPrefab =
                        clone.GetComponent<Unit>();
                }
            }
        }

        // Clone + recolor/scale + re-faction a projectile prefab, but
        // only if something actually changes. Re-factioning happens ONLY
        // when the prefab's root is on the "from" (wrong-faction) layer,
        // i.e. it's genuinely an enemy weapon needing to be flipped - a
        // weapon already on the right faction (e.g. a player gadget like
        // air mines) is left alone so its mixed collision layers survive.
        // Returns the clone, or null if no change was needed.
        private static GameObject ReskinProjectile(
            GameObject original,
            Color? color,
            bool rainbow,
            float rainbowSpeed,
            float? scale,
            int fromLayer,
            int toLayer,
            bool hasVisual,
            ForgeSpriteLibrary.Art customArt = null,
            Material customMaterial = null,
            bool spriteOnly = false,
            ForgeTrail.Spec trailSpec = null)
        {
            if (original == null)
                return null;

            bool needFaction =
                fromLayer >= 0 && toLayer >= 0 &&
                original.layer == fromLayer;

            if (!hasVisual && !needFaction)
                return null;

            GameObject clone =
                VisualCustomizer.ClonePrefab(original);

            // Art first, then colour: the tint lands on the renderer, so it
            // applies to whatever sprite is sitting there.
            if (customArt != null)
                VisualCustomizer.ApplyArt(clone, customArt);

            // After the swap, so the renderer we just wrote to is the one
            // kept and the template's glow/trail extras go quiet.
            if (spriteOnly)
                VisualCustomizer.IsolateSprite(clone);

            // Before the tint: the tint writes sr.color, the material
            // decides how that colour is then shaded.
            if (customMaterial != null)
                VisualCustomizer.SwapMaterial(clone, customMaterial);

            // After IsolateSprite, which switches off every child particle
            // system including any trail. ForgeTrail writes emission and the
            // renderer back on explicitly, so "one sprite, plus a trail" is a
            // legal combination and the two features cannot fight.
            if (trailSpec != null)
                ForgeTrail.Apply(clone, trailSpec);


            Paint(clone, color, rainbow, rainbowSpeed);

            if (scale.HasValue)
                VisualCustomizer.Scale(clone, scale.Value);

            if (needFaction)
                VisualCustomizer.RemapLayer(clone, fromLayer, toLayer);

            return clone;
        }

        // Static color or animated rainbow, whichever was requested.
        private static void Paint(
            GameObject clone,
            Color? color,
            bool rainbow,
            float rainbowSpeed)
        {
            if (rainbow)
            {
                VisualCustomizer.ApplyRainbow(clone, rainbowSpeed);
            }
            else if (color.HasValue)
            {
                VisualCustomizer.Recolor(clone, color.Value);
            }
        }

        // An explosion damage with no damageType is a CRASH, not a default.
        //
        // ExplosionManager.SpawnExplosion does, with no null check:
        //     Resource damageType = explosion.damages[0].damageType;
        //     this.Spawn(damageType.explosionBasePrefab, explosion);
        // so a missing type throws a NullReferenceException. And because that
        // happens BEFORE DoExplosionLogic and before the projectile's own
        // Destroy call, the symptom is bizarre rather than obvious: no blast at
        // all, and a shot that is never destroyed so it sits there hitting the
        // same target over and over doing "a lot of damage".
        //
        // The element also picks the explosion's whole VISUAL - each Resource
        // carries its own explosionBasePrefab - which is why an explosion needs
        // one at all. Filling it from the weapon's own damage type is both the
        // obvious intent and the only value guaranteed to exist.
        // Same guard, reachable from ForgeMinionArms. A minion's cloned weapon
        // needs it for exactly the same reason a built weapon does: a null
        // damageType in the explosion list crashes the game's own explosion
        // spawner and leaves the projectile alive.
        internal static void FixMinionExplosionTypes(
            WeaponData weapon,
            string fileName)
        {
            if (weapon != null)
                FixExplosionDamageTypes(weapon, fileName);
        }

        private static void FixExplosionDamageTypes(
            WeaponData weapon,
            string fileName)
        {
            Explosion ex = weapon.explosion;

            if (ex.damages == null || ex.damages.Count == 0)
                return;

            Resource fallback = weapon.damage.damageType;
            int fixedUp = 0;

            for (int i = 0; i < ex.damages.Count; i++)
            {
                if (ex.damages[i].damageType != null)
                    continue;

                if (fallback == null)
                {
                    Log.LogError(
                        fileName + ": explosion damage #" + (i + 1) +
                        " has no damageType, and the weapon has no damage " +
                        "type either to borrow. The game CRASHES on this " +
                        "(NullReferenceException in ExplosionManager) and the " +
                        "shot will never explode or die. Add " +
                        "\"damageType\": \"Resource White\" to it.");
                    continue;
                }

                Damage d = ex.damages[i];
                d.damageType = fallback;
                ex.damages[i] = d;
                fixedUp++;
            }

            if (fixedUp > 0)
            {
                weapon.explosion = ex;

                Log.LogWarning(
                    fileName + ": " + fixedUp + " explosion damage entr" +
                    (fixedUp == 1 ? "y had" : "ies had") +
                    " no damageType, which would have crashed the game's " +
                    "explosion spawner (no blast, and the shot never dies so " +
                    "it keeps hitting). Filled in from the weapon's own type " +
                    "'" + fallback.name + "' - set it explicitly if you " +
                    "wanted a different element.");
            }
        }

        // The seven sound slots a weapon has, as they stood on the template.
        private struct SfxSlots
        {
            public string shoot;
            public string reload;
            public string continuous;
            public string start;
            public string release;
            public string warmup;
            public string explosion;
        }

        private static SfxSlots CaptureSfx(WeaponData w)
        {
            return new SfxSlots
            {
                shoot = w.shootSfx,
                reload = w.reloadSfx,
                continuous = w.continousShootSfx,
                start = w.startSfx,
                release = w.releaseSfx,
                warmup = w.warmupSfx,
                explosion = w.explosion.sfx
            };
        }

        // Swap any sound field that names a file in the "sounds" folder for
        // the Sfx id we registered for it. A field the JSON never touched, or
        // one holding a real game sound id, is left exactly as it was.
        private static void ApplyCustomSounds(
            WeaponData w,
            SfxSlots original,
            string fileName)
        {
            w.shootSfx = SwapSound(
                w.shootSfx, original.shoot, false, "shootSfx", fileName);

            w.reloadSfx = SwapSound(
                w.reloadSfx, original.reload, false, "reloadSfx", fileName);

            w.startSfx = SwapSound(
                w.startSfx, original.start, false, "startSfx", fileName);

            w.releaseSfx = SwapSound(
                w.releaseSfx, original.release, false, "releaseSfx",
                fileName);

            // These two are HELD sounds: Shooter starts them, keeps the
            // handle, and calls AudioManager.Stop later. That only works on a
            // looping Sfx, so the slot forces looping on regardless of what
            // the replaced sound did.
            w.continousShootSfx = SwapSound(
                w.continousShootSfx, original.continuous, true,
                "continousShootSfx", fileName);

            w.warmupSfx = SwapSound(
                w.warmupSfx, original.warmup, true, "warmupSfx", fileName);

            // Explosion is a STRUCT field, so it has to be read out, changed
            // and written back - editing w.explosion.sfx in place would only
            // change a temporary copy.
            string newExplosion = SwapSound(
                w.explosion.sfx, original.explosion, false, "explosion.sfx",
                fileName);

            if (newExplosion != w.explosion.sfx)
            {
                Explosion ex = w.explosion;
                ex.sfx = newExplosion;
                w.explosion = ex;
            }
        }

        // "none" in any sound slot means SILENCE, and it has to be a word
        // rather than an empty string: a template's sound is inherited unless
        // the JSON says otherwise, and leaving a key out is how you say "keep
        // it". There was previously no way at all to say "keep the weapon but
        // lose the noise".
        //
        // AudioManager.PlaySfx returns -1 on a null id without touching the
        // pool, so a nulled slot is genuinely free rather than a silent clip
        // being played.
        public static bool IsSilence(string text)
        {
            if (string.IsNullOrEmpty(text))
                return false;

            switch (text.Trim().ToLowerInvariant())
            {
                case "none":
                case "silent":
                case "silence":
                case "off":
                case "mute":
                case "-":
                    return true;
            }

            return false;
        }

        private static string SwapSound(
            string current,
            string original,
            bool forceLoop,
            string slot,
            string fileName)
        {
            if (string.IsNullOrEmpty(current))
                return current;

            if (IsSilence(current))
            {
                // The held slots are worth a word, because silencing one has a
                // knock-on: releaseSfx is only ever played when the continuous
                // loop is STOPPED, so a weapon with no loop has nothing to
                // release and that slot goes quiet with it.
                Log.LogInfo(
                    fileName + ": " + slot + " silenced." +
                    (slot == "continousShootSfx"
                        ? " Note that this also silences releaseSfx - the " +
                          "game only plays that when it stops the loop, and " +
                          "there is now no loop to stop."
                        : string.Empty));

                return null;
            }

            // Untouched by the JSON - still the template's own sound.
            if (current == original)
                return current;

            string guid = ForgeSfxRegistry.Resolve(
                current, original, forceLoop, fileName);

            if (guid != null)
                return guid;

            // Neither a custom sound nor a real game sound id. This is the
            // silent-weapon trap, so say so loudly: the game's own reaction to
            // an unknown id is to return -1 and play nothing at all.
            if (!ForgeSfxRegistry.KnownGuid(current))
            {
                string known = ForgeSoundLibrary.Count > 0
                    ? " Loaded custom sounds: " +
                      string.Join(", ",
                          System.Linq.Enumerable.ToArray(
                              ForgeSoundLibrary.Names)) + "."
                    : " No custom sounds are loaded - is there an audio " +
                      "file in the 'sounds' folder next to the DLL?";

                Log.LogWarning(
                    fileName + ": " + slot + " is set to '" + current +
                    "', which is neither a custom sound nor a sound id this " +
                    "game has, so that slot will be SILENT." + known +
                    " A custom sound is named after its file, without the " +
                    "extension.");
            }

            return current;
        }

        // Recolor the muzzle flash. The prefab is SHARED - 53 stock
        // weapons point at "MuzzleParticle Popper" - so we tint a private
        // clone and hand the weapon that, otherwise recoloring one weapon
        // would recolor most of the game. Multiply-tinted rather than
        // overwritten so the flash keeps its gradient and alpha fade
        // (see VisualCustomizer.Tint).
        // "impactSfx" / "impactSprite": what happens where a shot lands.
        //
        // These are two features that share one method because they share one
        // awkward truth: an impact is handled in three unrelated places.
        //
        //   plain shot   ImpactBehaviour.destroyEffect, spawned by Projectile
        //   lobbed shot  the SAME field, spawned by PhysicsProjectile
        //   beam         HitscanWeaponVisual.impactParticle, a live particle
        //                system on the beam visual rather than a prefab
        //
        // The SOUND is stranger still: this game plays nothing at all when a
        // shot hits something. Every PlaySfx call site was checked - firing,
        // explosions, burning cells, terrain, ship damage, pickups, UI - and
        // none of them is an impact. So there is no field to fill in; the
        // sound is a patch (ForgeImpactPatch) reading a ForgeImpact component
        // off the prefab, which is why it is attached here.
        private static void ApplyImpact(
            WeaponData weapon,
            string impactSfx,
            string impactSfxOn,
            string impactSprite,
            float? impactSpriteFps,
            int? impactCount,
            float? impactSize,
            bool? impactRotate,
            float? impactSpread,
            string impactColor,
            float impactRgbSpeed,
            string impactColorMode,
            ForgeBeam.Spec beamSpec,
            string fileName)
        {
            // "none" here just means the same as not asking for one - there is
            // no stock impact sound to suppress, since the game plays nothing
            // on impact at all.
            bool wantSfx =
                !string.IsNullOrEmpty(impactSfx) && !IsSilence(impactSfx);

            bool wantArt = !string.IsNullOrEmpty(impactSprite);

            // Reshaping the STOCK puff is a legitimate thing to want on its
            // own - "keep the art, just stop it spraying everywhere" - so the
            // effect prefabs get cloned for these keys too, not only when
            // custom art is named.
            bool wantShape =
                impactCount.HasValue || impactSize.HasValue ||
                impactRotate.HasValue || impactSpread.HasValue;

            // Recolouring the stock puff is the commonest of these by far, and
            // for a long time it was the one thing you could not do. A weapon
            // built on the Caps Flame template fired recoloured projectiles
            // that burst into the template's ORANGE on impact, and there was no
            // key to fix it - only "impactSprite", which means replacing the
            // art entirely. That is what this closes.
            bool wantColor = !string.IsNullOrEmpty(impactColor);

            if (weapon == null ||
                (!wantSfx && !wantArt && !wantShape && !wantColor))
            {
                return;
            }

            // --- where should it play ---------------------------------
            //
            // Worth choosing rather than defaulting blindly: every enemy
            // ALREADY plays its own sound when damaged (onDamage -> SfxPlayer,
            // on all 37 unit prefabs), so "any" layers on top of that. What
            // the game has no sound for is hitting terrain.
            ForgeImpact.Where where = ForgeImpact.Where.Any;

            if (!string.IsNullOrEmpty(impactSfxOn))
            {
                switch (impactSfxOn.Trim().ToLowerInvariant())
                {
                    case "enemy":
                    case "enemies":
                    case "target":
                    case "targets":
                        where = ForgeImpact.Where.Enemy;
                        break;

                    case "terrain":
                    case "ground":
                    case "walls":
                    case "wall":
                        where = ForgeImpact.Where.Terrain;
                        break;

                    case "any":
                    case "all":
                    case "both":
                        where = ForgeImpact.Where.Any;
                        break;

                    default:
                        Log.LogWarning(
                            fileName + ": impactSfxOn '" + impactSfxOn +
                            "' is not one of any / enemy / terrain - " +
                            "using any.");
                        break;
                }
            }

            // --- resolve the sound once -------------------------------
            string guid = null;

            if (wantSfx)
            {
                ForgeSoundLibrary.LoadAll();

                // No slot to inherit settings from - the game has no impact
                // sound to copy, so this is always a fresh registration.
                guid = ForgeSfxRegistry.Resolve(
                    impactSfx, null, false, fileName);

                if (guid == null)
                {
                    if (ForgeSfxRegistry.KnownGuid(impactSfx))
                    {
                        guid = impactSfx;   // a stock game sound id
                    }
                    else
                    {
                        string known = ForgeSoundLibrary.Count > 0
                            ? " Loaded custom sounds: " +
                              string.Join(", ",
                                  System.Linq.Enumerable.ToArray(
                                      ForgeSoundLibrary.Names)) + "."
                            : " No custom sounds are loaded - is there an " +
                              "audio file in the 'sounds' folder next to " +
                              "the DLL?";

                        Log.LogWarning(
                            fileName + ": impactSfx '" + impactSfx +
                            "' is neither a custom sound nor a sound id this " +
                            "game has, so impacts will be SILENT." + known);

                        wantSfx = false;
                    }
                }
            }

            // --- resolve the art once ---------------------------------
            ForgeSpriteLibrary.Art art = null;

            if (wantArt)
            {
                ForgeSpriteLibrary.LoadAll();

                if (!ForgeSpriteLibrary.TryGetArt(impactSprite, out art))
                {
                    string known = ForgeSpriteLibrary.Count > 0
                        ? " Loaded custom art: " +
                          string.Join(", ", ForgeSpriteLibrary.Names) + "."
                        : " There is no art in the sprites folder yet.";

                    Log.LogWarning(
                        fileName + ": impactSprite '" + impactSprite +
                        "' is not one of your sprites." + known +
                        " Impact effects are left alone.");

                    wantArt = false;
                }
            }

            if (!wantSfx && !wantArt && !wantShape && !wantColor)
                return;

            Color tint = Color.white;
            bool rainbow = false;

            // REPLACE, not multiply, and that is the whole point of the
            // feature rather than a preference.
            //
            // The stock Flame impact keeps its orange in the particles'
            // startColor - (1, 0.22, 0) across three systems - NOT in a colour
            // ramp the way the muzzle prefabs do. Multiplying a blue tint over
            // that orange gives dark brown, so a tint could never fix "my
            // flamethrower bursts orange". Replacing it can.
            bool replace = true;

            if (!string.IsNullOrEmpty(impactColorMode))
            {
                string m = impactColorMode.Trim().ToLowerInvariant();

                if (m == "tint" || m == "multiply" || m == "shade")
                {
                    replace = false;
                }
                else if (m != "replace" && m != "set" && m != "flat")
                {
                    Log.LogWarning(
                        fileName + ": impactColorMode '" + impactColorMode +
                        "' is not \"replace\" or \"tint\" - using replace.");
                }
            }

            if (wantColor)
            {
                rainbow = VisualCustomizer.IsRainbow(impactColor);

                if (!rainbow &&
                    !VisualCustomizer.TryParseColor(impactColor, out tint))
                {
                    Log.LogWarning(
                        fileName + ": impactColor '" + impactColor +
                        "' is not a colour I understand - use #rrggbb, a name " +
                        "like \"orange\", or \"rainbow\". Ignored.");

                    wantColor = false;
                }
            }

            Sprite[] frames = null;
            float? fps = null;

            if (wantArt)
            {
                frames =
                    (art.animation != null && art.animation.frames != null &&
                     art.animation.frames.Length > 0)
                        ? art.animation.frames
                        : new[] { art.sprite };

                fps = impactSpriteFps.HasValue
                    ? impactSpriteFps
                    : (art.animation != null
                        ? (float?)art.animation.fps : null);
            }

            var hitscan = weapon as HitscanWeaponData;
            var pw = weapon as ProjectileWeaponData;

            // --- beams --------------------------------------------------
            if (hitscan != null)
            {
                if (wantArt)
                {
                    // The beam block already owns the spark's art. Feed it
                    // rather than fighting it, and let an explicit
                    // beam.impactSprite win - it is the more specific key.
                    if (beamSpec == null)
                    {
                        Log.LogWarning(
                            fileName + ": impactSprite on a BEAM needs a " +
                            "\"beam\" block to hang off - add \"beam\": {} " +
                            "(even empty) and it will apply to the beam's " +
                            "hit spark.");
                    }
                    else if (!string.IsNullOrEmpty(beamSpec.impactSprite))
                    {
                        Log.LogInfo(
                            fileName + ": both impactSprite and " +
                            "beam.impactSprite are set - the beam's own key " +
                            "wins for the hit spark.");
                    }
                    else
                    {
                        beamSpec.impactSprite = impactSprite;

                        if (impactSpriteFps.HasValue)
                            beamSpec.impactFps = impactSpriteFps;

                        Log.LogInfo(
                            fileName + ": impact art '" + impactSprite +
                            "' will be applied to the beam's hit spark.");
                    }
                }

                if (wantSfx)
                {
                    // The sound rides on the beam VISUAL, because a beam has
                    // no projectile to carry it. Cloned already by the visual
                    // path when there is a beam block; without one there is
                    // nothing private to attach to, so say so rather than
                    // editing a prefab 8 other weapons share.
                    if (hitscan.visual == null)
                    {
                        Log.LogWarning(
                            fileName + ": impactSfx on a beam needs the " +
                            "weapon to have a \"visual\" - this template has " +
                            "none, so there is nothing to attach the sound " +
                            "to. Ignored.");
                    }
                    else if (beamSpec == null)
                    {
                        Log.LogWarning(
                            fileName + ": impactSfx on a BEAM needs a " +
                            "\"beam\" block too - without one the beam visual " +
                            "is shared with other weapons and attaching the " +
                            "sound would give it to them as well. Add " +
                            "\"beam\": {} (even empty). Ignored.");
                    }
                    else
                    {
                        // Deferred: the visual is cloned later, in the visual
                        // pass. Stash it on the spec and let that pass attach.
                        beamSpec.impactSfx = guid;
                        beamSpec.impactSfxName = impactSfx;
                        beamSpec.impactSfxWhere = (int)where;

                        Log.LogInfo(
                            fileName + ": impact sound '" + impactSfx +
                            "' on beam hits" + WhereText(where) + ".");
                    }
                }

                return;
            }

            if (pw == null)
            {
                Log.LogWarning(
                    fileName + ": impactSfx/impactSprite need a projectile or " +
                    "beam weapon - a minion spawner has no impact of its own " +
                    "(its minions do their own hitting). Ignored.");
                return;
            }

            // --- projectiles (plain AND lobbed) -------------------------
            bool physics = pw.usePhysics;

            // The SOUND rides on the shot, so it needs a private copy of the
            // projectile prefab. The ART does not: the three effect slots
            // live on the weapon data, so cloning the projectile for art
            // alone would be a pointless copy of the wrong object.
            if (wantSfx)
            {
                Projectile plain = physics ? null : pw.projectilePrefab;
                PhysicsProjectile lobbed =
                    physics ? pw.physicsProjectilePrefab : null;

                if ((physics && lobbed == null) || (!physics && plain == null))
                {
                    Log.LogWarning(
                        fileName + ": impactSfx - this weapon has no " +
                        (physics
                            ? "physicsProjectilePrefab"
                            : "projectilePrefab") +
                        " to attach the sound to. Ignored.");
                }
                else
                {
                    GameObject clone = VisualCustomizer.ClonePrefab(
                        physics ? lobbed.gameObject : plain.gameObject);

                    Component comp = physics
                        ? (Component)clone
                            .GetComponentInChildren<PhysicsProjectile>(true)
                        : clone.GetComponentInChildren<Projectile>(true);

                    if (comp == null)
                    {
                        Log.LogWarning(
                            fileName + ": impactSfx - no " +
                            (physics ? "PhysicsProjectile" : "Projectile") +
                            " on the prefab. Ignored.");
                        UnityEngine.Object.Destroy(clone);
                    }
                    else
                    {
                        var imp = comp.GetComponent<ForgeImpact>();

                        if (imp == null)
                            imp = comp.gameObject.AddComponent<ForgeImpact>();

                        imp.sfx = guid;
                        imp.where = where;
                        imp.groundLayer = LayerMask.NameToLayer("Ground");

                        // A piercing shot re-enters the hit path every frame
                        // it overlaps something, so the gap between sounds
                        // defaults to the weapon's own re-hit delay when it
                        // has one.
                        float repeat = pw.piercingData.damageRepeatDelay;
                        imp.minInterval = (repeat > 0.01f) ? repeat : 0.08f;

                        if (physics)
                            pw.physicsProjectilePrefab =
                                (PhysicsProjectile)comp;
                        else
                            pw.projectilePrefab = (Projectile)comp;

                        Log.LogInfo(
                            fileName + ": impact sound '" + impactSfx +
                            "' on " + (physics ? "lobbed" : "projectile") +
                            " hits" + WhereText(where) + " (at most one " +
                            "every " + imp.minInterval.ToString("0.##") +
                            "s).");
                    }
                }
            }

            if (wantArt || wantShape || wantColor)
            {
                // All three effect slots, because they are the same kind of
                // thing and a file that says "my impact looks like X" means
                // the shot's death, however it died.
                // Same call as the muzzle: custom impact art turns rotation
                // ON by default, because ImpactVisualizer already spawns the
                // effect rotated to the SURFACE NORMAL - so directional art
                // points away from whatever it hit, for free. A stock puff is
                // symmetric and gains nothing from it, so shaping alone does
                // not flip it.
                bool? rotate = impactRotate.HasValue
                    ? impactRotate
                    : (wantArt ? (bool?)true : null);

                int changed = 0;

                changed += ApplyImpactArt(
                    ref pw.impactBehaviour, frames, fps, "impact", fileName,
                    impactCount, impactSize, rotate, impactSpread,
                    wantColor, tint, rainbow, impactRgbSpeed, replace);

                changed += ApplyRangeArt(
                    ref pw.rangeData, frames, fps, fileName,
                    impactCount, impactSize, rotate, impactSpread,
                    wantColor, tint, rainbow, impactRgbSpeed, replace);

                changed += ApplyLifetimeArt(
                    ref pw.lifetimeData, frames, fps, fileName,
                    impactCount, impactSize, rotate, impactSpread,
                    wantColor, tint, rainbow, impactRgbSpeed, replace);

                if (changed == 0)
                {
                    Log.LogWarning(
                        fileName + ": impactSprite '" + impactSprite +
                        "' had nothing to apply to - this template spawns no " +
                        "effect when its shot hits, runs out of range or " +
                        "times out. Pick a template that does (Popper, Flame, " +
                        "Toroid...), or set impactBehaviour.destroyEffect to " +
                        "one of the \"ImpactParticle Ammo ...\" prefabs.");
                }
            }
        }


        // Wording for the log, so "impactSfxOn" is visible in the file's own
        // build line rather than something you have to remember setting.
        private static string WhereText(ForgeImpact.Where where)
        {
            if (where == ForgeImpact.Where.Enemy)
                return " (enemies only)";

            if (where == ForgeImpact.Where.Terrain)
                return " (terrain only - enemies already make their own " +
                       "sound when damaged)";

            return " (enemies AND terrain - note enemies also play their own " +
                   "damage sound, so yours layers over it)";
        }

        // Each of the three effect slots is a STRUCT on the weapon, so it has
        // to be read out, changed and written back - editing in place would
        // only touch a temporary copy. (Same trap as explosion.sfx.)
        private static int ApplyImpactArt(
            ref ProjectileImpactBehaviour data,
            Sprite[] frames, float? fps, string slot, string fileName,
            int? count, float? size, bool? rotate, float? spread,
            bool color, Color tint, bool rainbow, float rgbSpeed,
            bool replace)
        {
            if (data.destroyEffect == null)
                return 0;

            GameObject art = CloneEffectAndPaint(
                data.destroyEffect, frames, fps, slot, fileName,
                count, size, rotate, spread, color, tint, rainbow, rgbSpeed,
                replace);

            if (art == null)
                return 0;

            data.destroyEffect = art;
            return 1;
        }

        private static int ApplyRangeArt(
            ref ProjectileRangeData data,
            Sprite[] frames, float? fps, string fileName,
            int? count, float? size, bool? rotate, float? spread,
            bool color, Color tint, bool rainbow, float rgbSpeed,
            bool replace)
        {
            if (data.destroyEffect == null)
                return 0;

            GameObject art = CloneEffectAndPaint(
                data.destroyEffect, frames, fps, "out-of-range", fileName,
                count, size, rotate, spread, color, tint, rainbow, rgbSpeed,
                replace);

            if (art == null)
                return 0;

            data.destroyEffect = art;
            return 1;
        }

        private static int ApplyLifetimeArt(
            ref ProjectileLifetimeData data,
            Sprite[] frames, float? fps, string fileName,
            int? count, float? size, bool? rotate, float? spread,
            bool color, Color tint, bool rainbow, float rgbSpeed,
            bool replace)
        {
            if (data.spawnOnTimeDown == null)
                return 0;

            GameObject art = CloneEffectAndPaint(
                data.spawnOnTimeDown, frames, fps, "end-of-life", fileName,
                count, size, rotate, spread, color, tint, rainbow, rgbSpeed,
                replace);

            if (art == null)
                return 0;

            data.spawnOnTimeDown = art;
            return 1;
        }

        // Private copy, then your art onto every particle system in it.
        //
        // The clone is not optional: "ImpactParticle Ammo Popper" is used by
        // SIX stock weapons and "ImpactParticle Ammo Flame" by five, so
        // painting the original would reskin other people's guns - the same
        // trap as the muzzle prefab shared by 53 weapons.
        private static GameObject CloneEffectAndPaint(
            GameObject original,
            Sprite[] frames,
            float? fps,
            string slot,
            string fileName,
            int? count,
            float? size,
            bool? rotate,
            float? spread,
            bool color,
            Color tint,
            bool rainbow,
            float rgbSpeed,
            bool replace)
        {
            GameObject clone = VisualCustomizer.ClonePrefab(original);

            if (clone == null)
                return null;

            // The game Instantiates this itself, and Instantiate copies
            // hideFlags - HideAndDontSave instances would never be cleaned up
            // on scene unload and would pile up run after run. Same reasoning
            // as the muzzle clone.
            clone.hideFlags = HideFlags.None;

            var systems = clone.GetComponentsInChildren<ParticleSystem>(true);

            if (systems.Length == 0)
            {
                Log.LogWarning(
                    fileName + ": the " + slot + " effect '" + original.name +
                    "' has no particle system to reskin - left alone.");
                UnityEngine.Object.Destroy(clone);
                return null;
            }

            if (frames != null)
            {
                for (int i = 0; i < systems.Length; i++)
                    VisualCustomizer.ApplyParticleSprite(systems[i], frames, fps);
            }

            // ONLY when something is actually being painted.
            //
            // This used to run unconditionally, which meant asking purely to
            // RESHAPE a puff ("same look, just stop it spraying everywhere")
            // silently drained the stock effect's colour as a side effect. The
            // ramp only needs neutralising when custom art or a tint is going
            // over it, because that is the case where it would multiply.
            if (frames != null || color)
            {
                // Stock impact puffs keep their colour in colorOverLifetime,
                // not in startColor - the Flame puff's orange lives there. That
                // ramp multiplies over whatever you put on top, so a blue tint
                // on an orange ramp comes out muddy. Drain the hue, keep the
                // brightness curve so it still fades.
                VisualCustomizer.NeutralizeColorRamp(clone);
            }

            if (color)
            {
                if (rainbow)
                    VisualCustomizer.ApplyRainbow(clone, rgbSpeed, true);
                else if (replace)
                    VisualCustomizer.Recolor(clone, tint);   // sets outright
                else
                    VisualCustomizer.Tint(clone, tint);      // multiplies
            }

            string shaped = VisualCustomizer.ShapeParticles(
                clone, count, size, rotate, spread);

            Log.LogInfo(
                fileName + ": " + slot + " effect '" + original.name +
                "' " + (frames != null ? "reskinned" : "cloned") +
                (color
                    ? (rainbow
                        ? " and set to rainbow"
                        : (replace ? " and recoloured " : " and tinted ") +
                          "#" + ColorUtility.ToHtmlStringRGB(tint))
                    : "") +
                " (" + systems.Length + " particle system" +
                (systems.Length == 1 ? "" : "s") + ")" +
                (shaped != null ? " - " + shaped : "") + ".");

            return clone;
        }

        // Colour AND art share ONE clone. Cloning twice would leave the
        // second pass working on a copy of the first and the weapon pointing
        // at whichever finished last, so a file setting both would silently
        // lose one of them.
        //
        // Works for every weapon type: muzzleParticlePrefab is declared on
        // WeaponData (the base), and WeaponBase.DoShoot - which spawns and
        // plays it - is neither virtual nor overridden. So a lobbed grenade,
        // a hitscan laser and a minion spawner all flash the same way. 73 of
        // the 77 stock weapons ship a muzzle prefab; the 4 without are
        // sub-weapons that are never fired directly.
        private static void ApplyMuzzle(
            WeaponData weapon,
            string muzzleColor,
            float muzzleRgbSpeed,
            string muzzleSprite,
            float? muzzleSpriteFps,
            int? muzzleCount,
            float? muzzleSize,
            bool? muzzleRotate,
            float? muzzleSpread,
            bool? muzzleFollow,
            string fileName)
        {
            bool wantColor = !string.IsNullOrEmpty(muzzleColor);
            bool wantSprite = !string.IsNullOrEmpty(muzzleSprite);

            bool wantShape = muzzleCount.HasValue || muzzleSize.HasValue ||
                             muzzleRotate.HasValue || muzzleFollow.HasValue ||
                             muzzleSpread.HasValue;

            if (weapon == null || (!wantColor && !wantSprite && !wantShape))
                return;

            if (weapon.muzzleParticlePrefab == null)
            {
                Log.LogWarning(
                    fileName + ": " +
                    (wantSprite ? "muzzleSprite" : "muzzle settings") +
                    " was set but this template has no muzzleParticlePrefab " +
                    "- there is no flash to change. Set " +
                    "muzzleParticlePrefab too (e.g. \"MuzzleParticle " +
                    "Popper\").");
                return;
            }

            // Resolve the art BEFORE cloning: a typo should not leave the
            // weapon pointing at a pointless private clone.
            ForgeSpriteLibrary.Art art = null;

            if (wantSprite)
            {
                ForgeSpriteLibrary.LoadAll();

                if (!ForgeSpriteLibrary.TryGetArt(muzzleSprite, out art))
                {
                    string known = ForgeSpriteLibrary.Count > 0
                        ? " Loaded custom art: " +
                          string.Join(", ", ForgeSpriteLibrary.Names) + "."
                        : " There is no art in the sprites folder yet.";

                    Log.LogWarning(
                        fileName + ": muzzleSprite '" + muzzleSprite +
                        "' is not one of your sprites." + known +
                        " The flash is left alone.");

                    wantSprite = false;

                    if (!wantColor && !wantShape)
                        return;
                }
            }

            bool rainbow = wantColor &&
                VisualCustomizer.IsRainbow(muzzleColor);

            Color color = Color.white;

            if (wantColor && !rainbow &&
                !VisualCustomizer.TryParseColor(muzzleColor, out color))
            {
                Log.LogWarning(
                    fileName + ": muzzleColor '" + muzzleColor +
                    "' is not a hex value, color name, ColorAsset or " +
                    "\"rainbow\" - ignored. Your muzzleSprite and any shape " +
                    "settings still apply.");

                // Drop the colour and carry on, rather than `return`ing.
                // Abandoning the whole method here meant one mistyped colour
                // silently threw away a perfectly good muzzleSprite and every
                // shape setting with it - the same shape as the validation bug
                // that cost a whole testing round in R8.
                wantColor = false;

                if (!wantSprite && !wantShape)
                    return;
            }

            // Name the prefab we are actually about to modify. A file that
            // sets "muzzleParticlePrefab" and then sees a DIFFERENT name on
            // this line knows straight away that the swap did not take,
            // instead of wondering why its art landed on the wrong flash.
            Log.LogInfo(
                fileName + ": muzzle prefab in use is '" +
                weapon.muzzleParticlePrefab.name + "'.");

            GameObject clone = VisualCustomizer.ClonePrefab(
                weapon.muzzleParticlePrefab.gameObject);

            var ps = clone.GetComponent<ParticleSystem>();
            if (ps == null)
                ps = clone.GetComponentInChildren<ParticleSystem>(true);

            if (ps == null)
            {
                Log.LogWarning(
                    fileName + ": the muzzle prefab '" +
                    weapon.muzzleParticlePrefab.name + "' has no " +
                    "ParticleSystem, so there is nothing to change - " +
                    "muzzle settings ignored.");
                UnityEngine.Object.Destroy(clone);
                return;
            }

            // ClonePrefab stamps HideAndDontSave, but the game spawns THIS
            // prefab itself (one instance per projectile) and Instantiate
            // copies hideFlags - so those instances would stop being
            // cleaned up on scene unload and pile up run after run, which
            // matters most for a gadget weapon (nothing ever disposes it).
            // The clone survives regardless: it lives under the
            // DontDestroyOnLoad holder, and it is the holder being
            // INACTIVE - not the flag - that keeps Awake from firing.
            clone.hideFlags = HideFlags.None;

            // Your own art, onto EVERY particle system in the flash.
            //
            // Most muzzle prefabs are two systems - the root plus a second
            // layer (Particle_muzzleFlash2, or "Fire" on CrawlerLaser) - and
            // both are 20-frame flipbooks of the same stock puff. Doing only
            // the root would leave that stock puff burning through underneath
            // your art, which reads as "the sprite didn't apply". Tint
            // already sweeps every system, so this matches it.
            //
            // Nothing special is needed to make animation work: these systems
            // ALREADY run TextureSheetAnimation in Sprites mode, so a
            // multi-frame name just replaces one flipbook with another.
            if (wantSprite && art != null)
            {
                var systems =
                    clone.GetComponentsInChildren<ParticleSystem>(true);

                Sprite[] frames =
                    (art.animation != null && art.animation.frames != null &&
                     art.animation.frames.Length > 0)
                        ? art.animation.frames
                        : new[] { art.sprite };

                float? fps = muzzleSpriteFps.HasValue
                    ? muzzleSpriteFps
                    : (art.animation != null
                        ? (float?)art.animation.fps : null);

                for (int i = 0; i < systems.Length; i++)
                {
                    VisualCustomizer.ApplyParticleSprite(
                        systems[i], frames, fps);
                }

                Log.LogInfo(
                    fileName + ": muzzle flash art '" + muzzleSprite +
                    "' (" + frames.Length + " frame" +
                    (frames.Length == 1 ? "" : "s") +
                    (frames.Length > 1
                        ? " at " + (fps.HasValue
                            ? fps.Value + " fps"
                            : "the flash's own lifetime")
                        : "") +
                    ") applied to " + systems.Length + " particle system" +
                    (systems.Length == 1 ? "" : "s") + " of '" +
                    weapon.muzzleParticlePrefab.name + "'.");
            }

            if (wantColor)
            {
                // Four stock muzzle prefabs (PopperRed, Laser, LaserRed,
                // CrawlerLaser) bake a colored fade into colorOverLifetime,
                // which Unity multiplies into startColor - so a cyan tint on
                // PopperRed's red ramp would come out black. Drain the ramp of
                // hue first, keeping its brightness curve.
                VisualCustomizer.NeutralizeColorRamp(clone);

                // Same reason as the art path: Tint MULTIPLIES, so a cyan
                // request over CrawlerLaser's blue-purple startColor would
                // come out wrong. Drain the hue first and the tint is exact.
                VisualCustomizer.NeutralizeStartColor(clone);

                if (rainbow)
                    VisualCustomizer.ApplyRainbow(clone, muzzleRgbSpeed, true);
                else
                    VisualCustomizer.Tint(clone, color);

                Log.LogInfo(
                    fileName + ": muzzle flash tinted " +
                    (rainbow ? "rainbow" : muzzleColor) + ".");
            }
            else if (wantSprite)
            {
                // A stock flash keeps its colour in colorOverLifetime, not in
                // startColor (every stock muzzle's startColor is white). That
                // ramp multiplies over custom art exactly the way a beam
                // template's element tint does - PopperRed would paint your
                // sprite red. Drain the hue and keep the brightness curve, so
                // the art shows its own colours and the flash still fades.
                int drained = VisualCustomizer.NeutralizeColorRamp(clone);

                // AND the start colour, which the ramp pass does not touch.
                //
                // The comment above used to say every stock muzzle starts
                // white. A census of the eleven says otherwise: CrawlerLaser
                // carries blue/purple and CrossAlpha cyan in startColor, and
                // Unity multiplies that into the particle just as it does the
                // ramp. Worse, both are RandomColor mode - the hue is picked
                // per particle - so custom art on those two did not come out
                // reliably tinted, it came out tinted SOMETIMES.
                int started = VisualCustomizer.NeutralizeStartColor(clone);

                if (drained > 0 || started > 0)
                {
                    Log.LogInfo(
                        fileName + ": muzzle tint reset to white so your art " +
                        "shows its own colours - '" +
                        weapon.muzzleParticlePrefab.name + "' carried a " +
                        (drained > 0 && started > 0
                            ? "coloured fade AND a coloured start colour"
                            : (drained > 0
                                ? "coloured fade"
                                : "coloured START colour (picked at random " +
                                  "per particle, so it tinted your art only " +
                                  "some of the time)")) +
                        " that would have multiplied over it. Set " +
                        "\"muzzleColor\" to tint it on purpose.");
                }
            }

            // Shape LAST, so it lands on whatever art or tint just went on.
            //
            // Custom art turns rotation ON unless the file says otherwise:
            // nobody imports a directional flash sprite wanting it locked to
            // the camera, and a symmetric puff cannot tell the difference.
            bool? rotate = muzzleRotate.HasValue
                ? muzzleRotate
                : ((wantSprite && art != null) ? (bool?)true : null);

            string shaped = VisualCustomizer.ShapeParticles(
                clone, muzzleCount, muzzleSize, rotate, muzzleSpread);

            if (shaped != null)
                Log.LogInfo(fileName + ": muzzle flash - " + shaped + ".");

            // Ride the barrel instead of being stamped where the last shot
            // went off. A marker on the prefab, because the game instantiates
            // this itself and the patch has to recognise the instances.
            if (muzzleFollow.HasValue && muzzleFollow.Value)
            {
                if (ps.GetComponent<ForgeMuzzleFollow>() == null)
                    ps.gameObject.AddComponent<ForgeMuzzleFollow>();

                ForgeMuzzleFollowPatch.Enabled = true;

                Log.LogInfo(
                    fileName + ": muzzle flash follows the barrel. It is " +
                    "still placed at barrelLength (" + weapon.barrelLength +
                    ") on each shot - the parent only decides where it sits " +
                    "BETWEEN shots.");
            }

            weapon.muzzleParticlePrefab = ps;
        }

        // Phasing: the projectile/beam passes through terrain but still
        // hits enemies. Projectiles get a ForgePhasing tag (a Shoot patch
        // strips the Ground collision bit); hitscan just drops Ground from
        // its layerMask, and can be made effectively unlimited-range.
        private static void ApplyPhasing(
            WeaponData weapon,
            JObject root,
            string fileName)
        {
            bool phasing = (bool?)root["phasing"] ?? false;
            if (!phasing)
                return;

            int ground = LayerMask.NameToLayer("Ground");

            var pw = weapon as ProjectileWeaponData;
            if (pw != null && pw.projectilePrefab != null)
            {
                GameObject clone = VisualCustomizer.ClonePrefab(
                    pw.projectilePrefab.gameObject);
                var comp = clone.GetComponentInChildren<Projectile>(true);
                if (comp != null)
                {
                    // Attach to the SAME GameObject as the Projectile so the
                    // Shoot patch's GetComponent<ForgePhasing>() finds it, and
                    // so it isn't dropped when the prefab is instantiated from
                    // a Projectile that sits on a child of the prefab root.
                    if (comp.GetComponent<ForgePhasing>() == null)
                        comp.gameObject.AddComponent<ForgePhasing>();
                    pw.projectilePrefab = comp;
                }
                ForgeWeaponInfo.SetPhasing(weapon);
                Log.LogInfo(fileName + ": phasing projectile");
            }

            var hs = weapon as HitscanWeaponData;
            if (hs != null && ground >= 0)
            {
                int m = hs.layerMask.value & ~(1 << ground);
                hs.layerMask = m;

                if ((bool?)root["phaseInfiniteRange"] ?? false)
                    hs.range = 1000f;

                ForgeWeaponInfo.SetPhasing(weapon);
                Log.LogInfo(fileName + ": phasing beam (terrain ignored)");
            }
        }

        // Pierce cap: caps the game's otherwise-infinite piercing. Enables
        // piercing and tags the projectile with a ForgePierceCap (a TryHit
        // patch counts distinct enemies and destroys past the limit).
        // Optional per-pierce damage falloff + explode-on-final-hit.
        private static void ApplyPierce(
            WeaponData weapon,
            JObject root,
            string fileName)
        {
            int? limit = (int?)root["pierceLimit"];
            if (!limit.HasValue)
                return;

            var pw = weapon as ProjectileWeaponData;
            if (!ProjectileTargetOk(
                    pw, "pierceLimit", fileName, false, null))
            {
                return;
            }

            // Piercing must be ON for the projectile to pass through
            // enemies in the first place.
            var pd = pw.piercingData;
            pd.enabled = true;
            if (pd.damageRepeatDelay <= 0f)
                pd.damageRepeatDelay = 0.15f;
            pw.piercingData = pd;

            GameObject clone = VisualCustomizer.ClonePrefab(
                pw.projectilePrefab.gameObject);

            var comp = clone.GetComponentInChildren<Projectile>(true);
            if (comp == null)
            {
                Log.LogWarning(fileName + ": pierce - no Projectile on prefab.");
                return;
            }

            // Attach to the Projectile's OWN GameObject (see phasing note) so
            // the TryHit patch finds it and it survives instantiation.
            var cap = comp.GetComponent<ForgePierceCap>();
            if (cap == null)
                cap = comp.gameObject.AddComponent<ForgePierceCap>();

            cap.limit = Mathf.Max(0, limit.Value);
            cap.falloff = (float?)root["pierceDamageFalloff"] ?? 0f;
            cap.explodeOnLimit = (bool?)root["pierceExplodeOnLimit"] ?? false;

            ForgeWeaponInfo.SetPierce(weapon, cap.limit);
            pw.projectilePrefab = comp;

            Log.LogInfo(
                fileName + ": pierce limit " + cap.limit +
                (cap.falloff > 0f ? " (falloff " + cap.falloff + ")" : "") +
                (cap.explodeOnLimit ? " (explode on last)" : ""));
        }

        // Wave / wobble motion for projectiles.
        //   "wobble": true  -> the game's built-in organic waver
        //     (movementNoiseData) - a Perlin wander. Free; also settable
        //     directly under "weapon".movementNoiseData.
        //   "wave": true     -> a clean, repeating sine "S" (Super Metroid
        //     wave beam). Custom: tags the projectile with ForgeWaveMotion,
        //     which ForgeWavePatch swings each FixedUpdate. waveMode picks
        //     single / synced / helix (see ForgeWaveMotion).
        // Both only apply to projectile weapons.
        private static void ApplyWave(
            WeaponData weapon,
            JObject root,
            string fileName)
        {
            var pw = weapon as ProjectileWeaponData;

            // Organic wobble alias -> the game's own movementNoiseData.
            if ((bool?)root["wobble"] ?? false)
            {
                if (pw != null)
                {
                    var nd = pw.movementNoiseData;
                    nd.enabled = true;
                    nd.angle = (float?)root["wobbleAngle"] ?? 30f;
                    nd.frequency = (float?)root["wobbleFrequency"] ?? 3f;
                    pw.movementNoiseData = nd;
                    Log.LogInfo(
                        fileName + ": organic wobble (angle " + nd.angle +
                        ", freq " + nd.frequency + ")");
                }
                else
                {
                    Log.LogWarning(
                        fileName + ": wobble only works on projectile " +
                        "weapons - ignored.");
                }
            }

            // Clean sine wave beam (custom).
            if (!((bool?)root["wave"] ?? false))
                return;

            if (!ProjectileTargetOk(
                    pw, "wave", fileName, false, null))
            {
                return;
            }

            // usePhysics weapons fire a PhysicsProjectile (a different class
            // the FixedUpdate patch never runs on), so the wave would be a
            // silent no-op. Fail loudly instead.
            if (pw.usePhysics)
            {
                Log.LogWarning(
                    fileName + ": wave is not supported on usePhysics " +
                    "projectiles - ignored.");
                return;
            }

            // The wave prefix sets the projectile heading each FixedUpdate,
            // but the game's own movementNoiseData branch runs AFTER it and
            // would recompute (and clobber) the heading from idealDirection.
            // Wave wins: turn the noise off so the clean sine is visible.
            var noise = pw.movementNoiseData;
            if (noise.enabled)
            {
                noise.enabled = false;
                pw.movementNoiseData = noise;
                Log.LogWarning(
                    fileName + ": wave overrides wobble/movementNoiseData - " +
                    "disabling the noise so the sine wave shows.");
            }

            GameObject clone = VisualCustomizer.ClonePrefab(
                pw.projectilePrefab.gameObject);

            var comp = clone.GetComponentInChildren<Projectile>(true);
            if (comp == null)
            {
                Log.LogWarning(fileName + ": wave - no Projectile on prefab.");
                return;
            }

            // Attach to the SAME GameObject as the Projectile so the patch's
            // GetComponent<ForgeWaveMotion>() finds it.
            var wave = comp.GetComponent<ForgeWaveMotion>();
            if (wave == null)
                wave = comp.gameObject.AddComponent<ForgeWaveMotion>();

            wave.angleDeg = (float?)root["waveAngle"] ?? 30f;
            wave.frequency = (float?)root["waveFrequency"] ?? 2f;
            wave.sharpness =
                Mathf.Clamp01((float?)root["waveSharpness"] ?? 1f);
            wave.faceTravel = (bool?)root["waveFaceTravel"] ?? false;

            string wm =
                ((string)root["waveMode"] ?? "single")
                    .Trim().ToLowerInvariant();
            wave.mode = (wm == "helix") ? 2 : (wm == "synced" ? 1 : 0);

            string ws =
                ((string)root["waveShape"] ?? "sine")
                    .Trim().ToLowerInvariant();

            // "v" is the same shape as "z" and that is not a shortcut - a run
            // of sharp corners IS a row of Vs, and which letter you see
            // depends only on where you start looking. Both words accepted so
            // nobody has to work that out.
            if (ws == "zigzag" || ws == "zig-zag" || ws == "z" ||
                ws == "v" || ws == "vee" || ws == "v-pattern" ||
                ws == "square" || ws == "sharp")
            {
                wave.shape = ForgeWaveMotion.Shape.Zigzag;
            }
            else if (ws == "saw" || ws == "sawtooth" || ws == "hook")
            {
                wave.shape = ForgeWaveMotion.Shape.Saw;
            }
            else
            {
                wave.shape = ForgeWaveMotion.Shape.Sine;

                if (ws != "sine" && ws != "s" && ws != "smooth")
                {
                    Log.LogWarning(
                        fileName + ": wave \"waveShape\": '" + ws +
                        "' is not recognised - use \"sine\" (the smooth S), " +
                        "\"zigzag\" (also spelled \"z\" or \"v\" - sharp " +
                        "corners) or \"saw\" (curve then snap). Using sine.");
                }
            }

            pw.projectilePrefab = comp;

            Log.LogInfo(
                fileName + ": wave beam (" + ws + ", angle " + wave.angleDeg +
                ", freq " + wave.frequency + ", " + wm +
                (wave.shape == ForgeWaveMotion.Shape.Zigzag
                    ? ", sharpness " + wave.sharpness
                    : "") +
                (wave.faceTravel ? ", art turns with it" : "") + ")");

            // A zigzag's whole point is the corner, and a corner is where
            // un-rotated art looks worst.
            if (wave.shape != ForgeWaveMotion.Shape.Sine && !wave.faceTravel)
            {
                Log.LogInfo(
                    fileName + ": this weave has CORNERS but the shot's art " +
                    "does not turn (the game sets a projectile's rotation " +
                    "once, when it is fired). On a round bullet nobody " +
                    "notices; on drawn directional art it will visibly fly " +
                    "sideways out of every corner. Set " +
                    "\"waveFaceTravel\": true if so.");
            }
        }

        // Homing for a PLAIN projectile - "homing": { ... } on the weapon.
        //
        // The game's own homingData is unreachable here: ProjectileWeapon
        // assigns it only inside the UsePhysics branch, so a fast straight shot
        // like the Popper's can never home with stock data no matter what the
        // JSON says. This attaches a ForgeHoming marker instead, which
        // ForgeHomingPatch steers by rotating Velocity - and because the
        // projectile's own collision sweep is built from Velocity, the hitbox
        // curves with it.
        private static void ApplyHoming(
            WeaponData weapon,
            JObject root,
            string fileName)
        {
            JToken token = root["homing"];

            if (token == null || token.Type == JTokenType.Null)
                return;

            // Two accepted forms: "homing": true is the whole feature at
            // defaults, and an object carries settings. The object's own
            // "enabled" is what the builder page writes, and it must be able
            // to say false - a block cannot offer both a bare boolean AND
            // nested keys under one name, because the bare value would be
            // overwritten the moment a nested one is set.
            var o = token as JObject;

            if (o == null)
            {
                if (token.Type != JTokenType.Boolean || !(bool)token)
                {
                    Log.LogWarning(
                        fileName + ": \"homing\" should be true or an " +
                        "object - ignored.");
                    return;
                }
            }
            else if (!((bool?)o["enabled"] ?? true))
            {
                return;
            }

            var pw = weapon as ProjectileWeaponData;

            if (!ProjectileTargetOk(
                    pw, "homing", fileName, false,
                    "A lobbed shot already homes natively, though: set " +
                    "\"homingData\": { \"enabled\": true, ... } in the weapon block " +
                    "and the GAME steers it - ForgeHoming exists only because " +
                    "plain bullets ignore homingData."))
            {
                return;
            }

            // A usePhysics weapon fires a PhysicsProjectile, which our
            // FixedUpdate patch never runs on - but that class DOES support the
            // game's own homing, so point them at it rather than just refusing.
            if (pw.usePhysics)
            {
                Log.LogWarning(
                    fileName + ": \"homing\" is for plain projectiles, and " +
                    "this weapon has usePhysics true. Lobbed/physics shots " +
                    "have the game's OWN homing instead - use " +
                    "\"homingData\": { \"enabled\": true, \"torque\": 40, " +
                    "\"maxSpeed\": 20, \"acceleration\": 30, " +
                    "\"maxAngularVelocity\": 200, \"targetMode\": " +
                    "\"AutoSeekWhenShot\" } for those. Ignored.");
                return;
            }

            // movementNoiseData recomputes the heading from idealDirection
            // AFTER our prefix runs, which would wipe out every turn we make.
            // Same clash wave has, same resolution.
            var noise = pw.movementNoiseData;
            if (noise.enabled)
            {
                noise.enabled = false;
                pw.movementNoiseData = noise;
                Log.LogWarning(
                    fileName + ": homing overrides wobble/movementNoiseData " +
                    "- disabling the noise, which would otherwise recompute " +
                    "the heading and undo the homing every frame.");
            }

            GameObject clone = VisualCustomizer.ClonePrefab(
                pw.projectilePrefab.gameObject);

            var comp = clone.GetComponentInChildren<Projectile>(true);
            if (comp == null)
            {
                Log.LogWarning(
                    fileName + ": homing - no Projectile on prefab.");
                return;
            }

            // Same GameObject as the Projectile, so the patch's
            // GetComponent<ForgeHoming>() finds it.
            var homing = comp.GetComponent<ForgeHoming>();
            if (homing == null)
                homing = comp.gameObject.AddComponent<ForgeHoming>();

            if (o != null)
            {
                homing.turnRate = (float?)o["turnRate"] ?? 180f;
                homing.range = (float?)o["range"] ?? 20f;
                homing.cone = (float?)o["cone"] ?? 90f;
                homing.retarget = (bool?)o["retarget"] ?? true;
                homing.delay = (float?)o["delay"] ?? 0f;
                homing.predict = (bool?)o["predict"] ?? false;
                homing.maxTurn = (float?)o["maxTurn"] ?? 0f;
                homing.faceTravel = (bool?)o["faceTravel"] ?? true;
            }

            pw.projectilePrefab = comp;

            // The turn RADIUS is what people actually see, and it grows with
            // the square of speed - so the same turnRate that whips a slow
            // shot around barely bends a fast one. Do that sum for them,
            // because "my homing does nothing" is otherwise a mystery.
            float speed = pw.projectileSpeed;

            if (speed > 0f && homing.turnRate > 0f)
            {
                float radius =
                    speed / (homing.turnRate * Mathf.Deg2Rad);

                Log.LogInfo(
                    fileName + ": homing (turnRate " + homing.turnRate +
                    " deg/s, range " + homing.range + ", cone " +
                    homing.cone + ") - at projectileSpeed " + speed +
                    " the tightest turn circle is about " +
                    radius.ToString("0.0") + " units across the radius." +
                    (radius > 12f
                        ? " That is WIDE - the curve will barely show. Raise " +
                          "turnRate or lower projectileSpeed if you want a " +
                          "visible bend."
                        : string.Empty));
            }
            else
            {
                Log.LogInfo(
                    fileName + ": homing (turnRate " + homing.turnRate +
                    " deg/s, range " + homing.range + ")");
            }
        }

        // Bullet deflector - "deflect": { ... } on the weapon.
        //
        // The first DEFENSIVE weapon in the mod: the shot clears enemy fire out
        // of the air as it travels, or turns it around and sends it back.
        private static void ApplyDeflect(
            WeaponData weapon,
            JObject root,
            string fileName)
        {
            JToken token = root["deflect"];

            if (token == null || token.Type == JTokenType.Null)
                return;

            var o = token as JObject;

            if (o == null)
            {
                if (token.Type != JTokenType.Boolean || !(bool)token)
                {
                    Log.LogWarning(
                        fileName + ": \"deflect\" should be true or an " +
                        "object - ignored.");
                    return;
                }
            }
            else if (!((bool?)o["enabled"] ?? true))
            {
                return;
            }

            var pw = weapon as ProjectileWeaponData;

            if (!ProjectileTargetOk(
                    pw, "deflect", fileName, false, null))
            {
                return;
            }

            if (pw.usePhysics)
            {
                Log.LogWarning(
                    fileName + ": deflect does not work on usePhysics " +
                    "(lobbed) shots - it runs on the plain projectile only. " +
                    "Ignored.");
                return;
            }

            GameObject clone = VisualCustomizer.ClonePrefab(
                pw.projectilePrefab.gameObject);

            var comp = clone.GetComponentInChildren<Projectile>(true);

            if (comp == null)
            {
                Log.LogWarning(
                    fileName + ": deflect - no Projectile on prefab.");
                return;
            }

            var d = comp.GetComponent<ForgeDeflect>();

            if (d == null)
                d = comp.gameObject.AddComponent<ForgeDeflect>();

            if (o != null)
            {
                d.radius = (float?)o["radius"] ?? 2f;
                d.maxTotal = (int?)o["maxTotal"] ?? 0;
                d.interval = (float?)o["interval"] ?? 0.05f;
                d.damageMultiplier = (float?)o["damage"] ?? 1f;
                d.speedMultiplier = (float?)o["speed"] ?? 1f;
                d.aimRange = (float?)o["aimRange"] ?? 25f;

                string mode =
                    ((string)o["mode"] ?? "destroy")
                        .Trim().ToLowerInvariant();

                if (mode == "reflect" || mode == "return" || mode == "bounce")
                {
                    d.mode = 1;
                }
                else
                {
                    d.mode = 0;

                    if (mode != "destroy" && mode != "block" &&
                        mode != "clear")
                    {
                        Log.LogWarning(
                            fileName + ": deflect mode '" + mode + "' is not " +
                            "recognised - use \"destroy\" or \"reflect\". " +
                            "Using destroy.");
                    }
                }

                string aim =
                    ((string)o["aim"] ?? "back")
                        .Trim().ToLowerInvariant();

                // Through the shared parser, so a deflector understands the
                // same words a shield does. `aim` is an int on a prefab
                // component, which is exactly why deflect can have the cursor
                // and mirror modes but cannot have a rally - an int serializes
                // onto a prefab, a config-class reference does not.
                d.aim = ForgeBulletTurn.ParseAim(aim);

                if (d.aim == ForgeBulletTurn.AimBack &&
                    aim != "back" && aim != "reverse" && aim != "sender")
                {
                    Log.LogWarning(
                        fileName + ": deflect aim '" + aim + "' is not " +
                        "recognised - use \"back\", \"nearest\", " +
                        "\"cursor\" or \"mirror\". Using back.");
                }
                else
                {
                    Log.LogInfo(
                        fileName + ": deflect sends caught shots " +
                        ForgeBulletTurn.AimWord(d.aim) + ".");
                }
            }

            // Without this, enemy shots are never registered and the sweep
            // finds nothing at all - the shared tracker is off by default so
            // it costs nothing for weapons that don't need it.
            ForgeProjectileTracker.Enabled = true;

            pw.projectilePrefab = comp;

            Log.LogInfo(
                fileName + ": deflector (" +
                (d.mode == 1
                    ? "reflects enemy fire " +
                      (d.aim == 1 ? "at the nearest enemy" : "back the way it came")
                    : "destroys enemy fire") +
                " within " + d.radius + " units" +
                (d.maxTotal > 0
                    ? ", up to " + d.maxTotal + " per shot"
                    : ", no limit per shot") +
                (d.mode == 1 && d.damageMultiplier != 1f
                    ? ", x" + d.damageMultiplier + " damage" : "") + ").");
        }

        // Growing / shrinking shot - "grow": { ... } on the weapon.
        //
        // Cheap because Projectile.Radius is public and the collision sweep is
        // rebuilt from it every frame, so the hitbox follows the art for free.
        private static void ApplyGrowth(
            WeaponData weapon,
            JObject root,
            string fileName)
        {
            JToken token = root["grow"];

            if (token == null || token.Type == JTokenType.Null)
                return;

            var o = token as JObject;

            if (o == null)
            {
                if (token.Type != JTokenType.Boolean || !(bool)token)
                {
                    Log.LogWarning(
                        fileName + ": \"grow\" should be true or an object - " +
                        "ignored.");
                    return;
                }
            }
            else if (!((bool?)o["enabled"] ?? true))
            {
                return;
            }

            // An ORBIT weapon's orbs are pure art, not projectiles: the
            // controller instantiates the visual, DISABLES its Projectile
            // component, kills its collider, and does damage itself with an
            // OverlapCircle of orbitHitRadius. So nothing the growth patch
            // hooks ever runs on an orb, and it accepted this silently.
            if ((bool?)root["orbit"] ?? false)
            {
                Log.LogWarning(
                    fileName + ": \"grow\" does nothing on an ORBIT weapon - " +
                    "ignored. An orbiting orb is only artwork: its Projectile " +
                    "component is switched off and the ring does its own " +
                    "damage, so none of grow's machinery reaches it. " +
                    "USE \"orbitGrow\" INSTEAD - same idea, built for the ring: " +
                    "{ \"from\": 0.5, \"to\": 3, \"over\": \"charge\", " +
                    "\"span\": 4 }. Its \"over\" can be \"charge\" (grows while " +
                    "the ring is up), \"orbLife\" (each orb grows from when it " +
                    "spawns) or \"breathe\" (in and out forever).");
                return;
            }

            var pw = weapon as ProjectileWeaponData;

            if (!ProjectileTargetOk(
                    pw, "grow", fileName, true, null))
            {
                return;
            }

            // A usePhysics weapon fires a PhysicsProjectile - a different
            // class on a different prefab - so it needs its own clone and its
            // own component. The growth patch hooks both. (The prefab is known
            // non-null for either mode: ProjectileTargetOk checked the one that
            // matters.)
            bool physics = pw.usePhysics;

            GameObject clone = VisualCustomizer.ClonePrefab(
                physics
                    ? pw.physicsProjectilePrefab.gameObject
                    : pw.projectilePrefab.gameObject);

            Component comp = physics
                ? (Component)clone.GetComponentInChildren<PhysicsProjectile>(true)
                : clone.GetComponentInChildren<Projectile>(true);

            if (comp == null)
            {
                Log.LogWarning(
                    fileName + ": grow - no " +
                    (physics ? "PhysicsProjectile" : "Projectile") +
                    " on prefab.");
                return;
            }

            var g = comp.GetComponent<ForgeGrowth>();

            if (g == null)
                g = comp.gameObject.AddComponent<ForgeGrowth>();

            if (o != null)
            {
                g.from = (float?)o["from"] ?? 0.4f;
                g.to = (float?)o["to"] ?? 3f;
                g.span = (float?)o["span"] ?? 0f;
                g.hitbox = (bool?)o["hitbox"] ?? true;
                g.damageAtFull = (float?)o["damageAtFull"] ?? 1f;
                g.curve = (float?)o["curve"] ?? 1f;
                g.clamp = (bool?)o["clamp"] ?? true;

                string over =
                    ((string)o["over"] ?? "distance")
                        .Trim().ToLowerInvariant();

                g.overTime = (over == "time" || over == "seconds" ||
                              over == "lifetime");

                if (!g.overTime && over != "distance" && over != "range" &&
                    over != "units")
                {
                    Log.LogWarning(
                        fileName + ": grow \"over\": '" + over + "' is not " +
                        "recognised - use \"distance\" or \"time\". Using " +
                        "distance.");
                }
            }

            if (g.from < 0f) g.from = 0f;
            if (g.to < 0f) g.to = 0f;

            // Nothing would visibly happen, which is worth saying rather than
            // leaving them to wonder.
            if (Mathf.Approximately(g.from, g.to))
            {
                Log.LogWarning(
                    fileName + ": grow \"from\" and \"to\" are both " + g.to +
                    ", so the shot never changes size. Set them to different " +
                    "values (e.g. from 0.4 to 3).");
            }

            // A lobbed shot's collider is REAL and scales with the transform,
            // so "visual only" is not achievable there - the hitbox comes along
            // whether or not it was asked for. Say so rather than silently
            // doing the opposite of what the file requested.
            if (physics && !g.hitbox)
            {
                Log.LogWarning(
                    fileName + ": grow \"hitbox\": false is ignored on a " +
                    "usePhysics (lobbed) shot. Those collide with a real " +
                    "Collider2D, which scales with the art, so the hitbox " +
                    "always follows. Only straight projectiles can grow the " +
                    "art alone.");
            }

            if (physics)
                pw.physicsProjectilePrefab = (PhysicsProjectile)comp;
            else
                pw.projectilePrefab = (Projectile)comp;

            // The span defaults to the weapon's own range so the shot peaks
            // exactly as it expires - say which one it landed on, since a
            // weapon with no rangeData falls back to a flat number.
            string spanText;

            if (g.span > 0f)
            {
                spanText = g.span + (g.overTime ? "s" : " units");
            }
            else if (!g.overTime && pw.rangeData.enabled &&
                     pw.rangeData.range > 0f)
            {
                spanText = "the weapon's range (" + pw.rangeData.range + ")";
            }
            else if (g.overTime && pw.lifetimeData.enabled &&
                     pw.lifetimeData.time > 0f)
            {
                spanText =
                    "the weapon's lifetime (" + pw.lifetimeData.time + "s)";
            }
            else
            {
                spanText =
                    (g.overTime ? "2s" : "10 units") +
                    " (no rangeData/lifetimeData to borrow)";
            }

            Log.LogInfo(
                fileName + ": " +
                (g.to > g.from ? "growing" : "shrinking") +
                (physics ? " lobbed" : "") + " shot x" +
                g.from + " -> x" + g.to + " over " + spanText +
                ((g.hitbox || physics) ? ", hitbox follows" : ", visual only") +
                (g.damageAtFull != 1f
                    ? ", damage up to x" + g.damageAtFull : "") + ".");
        }

        // Boomerang - "boomerang": { ... } on the weapon.
        //
        // Leans on how much of this the game already does: a weapon with
        // rangeData.slowDown decelerates to a dead stop at its range (the
        // DiscGun does exactly that at range 8), which is a free, natural
        // pivot. So the outbound half is stock; this supplies the trip home.
        private static void ApplyBoomerang(
            WeaponData weapon,
            JObject root,
            string fileName)
        {
            JToken token = root["boomerang"];

            if (token == null || token.Type == JTokenType.Null)
                return;

            var o = token as JObject;

            if (o == null)
            {
                if (token.Type != JTokenType.Boolean || !(bool)token)
                {
                    Log.LogWarning(
                        fileName + ": \"boomerang\" should be true or an " +
                        "object - ignored.");
                    return;
                }
            }
            else if (!((bool?)o["enabled"] ?? true))
            {
                return;
            }

            var pw = weapon as ProjectileWeaponData;

            if (!ProjectileTargetOk(
                    pw, "boomerang", fileName, false, null))
            {
                return;
            }

            if (pw.usePhysics)
            {
                Log.LogWarning(
                    fileName + ": boomerang does not work on usePhysics " +
                    "(lobbed) shots - the steering runs on the plain " +
                    "projectile only. Ignored.");
                return;
            }

            // The pivot IS rangeData: slowDown brings the shot to a halt at
            // its range, and that halt is what we turn on. Without it there is
            // nothing to turn at, so supply a sane one rather than silently
            // doing nothing.
            var rd = pw.rangeData;

            if (!rd.enabled || rd.range <= 0f)
            {
                rd.enabled = true;

                if (rd.range <= 0f)
                    rd.range = 8f;

                Log.LogWarning(
                    fileName + ": boomerang needs rangeData to know where to " +
                    "turn around - enabling it at range " + rd.range +
                    ". Set \"rangeData\": { \"enabled\": true, \"range\": N } " +
                    "to choose the throw distance yourself.");
            }

            // slowDown is what makes the turn look like a turn instead of a
            // snap: the shot eases to a stop, pivots, and accelerates back.
            rd.slowDown = true;

            // Would otherwise destroy the shot at exactly the moment it should
            // be turning round.
            rd.destroyWhenReached = false;

            pw.rangeData = rd;

            bool pierce = (bool?)(o != null ? o["pierce"] : null) ?? true;

            if (pierce)
            {
                var pd = pw.piercingData;

                if (!pd.enabled)
                {
                    pd.enabled = true;
                    pw.piercingData = pd;
                }
            }

            GameObject clone = VisualCustomizer.ClonePrefab(
                pw.projectilePrefab.gameObject);

            var comp = clone.GetComponentInChildren<Projectile>(true);

            if (comp == null)
            {
                Log.LogWarning(
                    fileName + ": boomerang - no Projectile on prefab.");
                return;
            }

            var boom = comp.GetComponent<ForgeBoomerang>();

            if (boom == null)
                boom = comp.gameObject.AddComponent<ForgeBoomerang>();

            if (o != null)
            {
                boom.returnOnHit = ParseReturnOn(o["returnOn"], fileName);

                string path =
                    ((string)o["returnPath"] ?? "home")
                        .Trim().ToLowerInvariant();

                boom.retrace = (path == "retrace" || path == "path" ||
                                path == "trace");

                if (path != "retrace" && path != "path" && path != "trace" &&
                    path != "home" && path != "ship" && path != "player")
                {
                    Log.LogWarning(
                        fileName + ": returnPath '" + path + "' is not " +
                        "recognised - use \"home\" or \"retrace\". Using " +
                        "home.");
                }

                boom.returnSpeed = (float?)o["returnSpeed"] ?? 1f;
                boom.turnRate = (float?)o["turnRate"] ?? 540f;
                boom.catchRadius = (float?)o["catchRadius"] ?? 1.2f;
                boom.rehit = (bool?)o["rehit"] ?? true;
                boom.returnDamage = (float?)o["returnDamage"] ?? 1f;
                boom.passes = (int?)o["passes"] ?? 2;
                boom.refundFraction = (float?)o["refund"] ?? 0.5f;
                boom.maxLife = (float?)o["maxLife"] ?? 12f;

                string onCatch =
                    ((string)o["onCatch"] ?? "vanish")
                        .Trim().ToLowerInvariant();

                if (onCatch == "refund")
                    boom.onCatch = 1;
                else if (onCatch == "loop" || onCatch == "yoyo")
                    boom.onCatch = 2;
                else
                {
                    boom.onCatch = 0;

                    if (onCatch != "vanish" && onCatch != "none")
                    {
                        Log.LogWarning(
                            fileName + ": onCatch '" + onCatch + "' is not " +
                            "recognised - use \"vanish\", \"refund\" or " +
                            "\"loop\". Using vanish.");
                    }
                }
            }

            // Refunding needs to know what the shot cost and which tank to put
            // it back into - neither is reachable from the projectile.
            boom.refundResource = weapon.resourceUsed;
            boom.refundAmount = weapon.cost;

            if (boom.onCatch == 1 &&
                (boom.refundResource == null || boom.refundAmount <= 0f))
            {
                Log.LogWarning(
                    fileName + ": \"onCatch\": \"refund\" but this weapon " +
                    "has no resource cost to refund - catching it will just " +
                    "make it vanish.");
            }

            pw.projectilePrefab = comp;

            Log.LogInfo(
                fileName + ": boomerang (range " + rd.range + ", " +
                (boom.retrace ? "retraces its path" : "homes back to you") +
                ", returns at x" + boom.returnSpeed + " speed" +
                (boom.returnOnHit ? ", also turns on walls" : "") +
                (boom.returnDamage != 1f
                    ? ", x" + boom.returnDamage + " damage coming back" : "") +
                (boom.onCatch == 1
                    ? ", refunds " + (boom.refundFraction * 100f) + "%"
                    : (boom.onCatch == 2
                        ? ", loops " + boom.passes + " times" : "")) + ")");
        }

        // "range" (default) or "hit"/"any" - the latter also turns on terrain.
        private static bool ParseReturnOn(JToken token, string fileName)
        {
            if (token == null || token.Type == JTokenType.Null)
                return false;

            string s = ((string)token ?? string.Empty)
                .Trim().ToLowerInvariant();

            if (s == "range" || s.Length == 0)
                return false;

            if (s == "hit" || s == "any" || s == "wall" || s == "terrain" ||
                s == "first")
            {
                return true;
            }

            Log.LogWarning(
                fileName + ": returnOn '" + s + "' is not recognised - use " +
                "\"range\" or \"hit\". Using range.");
            return false;
        }

        // Bullet ricochet - "ricochet": { ... } on the weapon.
        //
        // The bouncing itself is the GAME's (ProjectileBounceData), including
        // which layers bounce, and damage already lands before the bounce does.
        // What the game has no notion of is a bounce COUNT - nothing tracks
        // reflections, so a stock bouncer bounces until range or lifetime kills
        // it. ForgeRicochet supplies the count plus the per-bounce shaping.
        private static void ApplyRicochet(
            WeaponData weapon,
            JObject root,
            string fileName)
        {
            JToken token = root["ricochet"];

            if (token == null || token.Type == JTokenType.Null)
                return;

            var o = token as JObject;

            if (o == null)
            {
                if (token.Type != JTokenType.Boolean || !(bool)token)
                {
                    Log.LogWarning(
                        fileName + ": \"ricochet\" should be true or an " +
                        "object - ignored.");
                    return;
                }
            }
            else if (!((bool?)o["enabled"] ?? true))
            {
                return;
            }

            var pw = weapon as ProjectileWeaponData;

            if (!ProjectileTargetOk(
                    pw, "ricochet", fileName, false, null))
            {
                return;
            }

            // The game's SCRIPTED bounce (ProjectileBounceData + Reflect)
            // exists only on Projectile.OnObjectHit, so none of ricochet's
            // machinery reaches a lobbed shot.
            //
            // That is not the same as "a lobbed shot cannot bounce", which is
            // what this used to say. A PhysicsProjectile is a real rigidbody
            // and bounces perfectly well - eight stock weapons do - it just
            // does it through the physics solver instead of through code.
            // Shaping THAT is the separate "bounce" block.
            if (pw.usePhysics)
            {
                Log.LogWarning(
                    fileName + ": \"ricochet\" does not work on usePhysics " +
                    "(lobbed) shots - it shapes the game's scripted bounce, " +
                    "which only exists on the plain projectile. Ignored. Use " +
                    "the \"bounce\" block instead: a lobbed shot bounces as a " +
                    "real physics body, and that block shapes the rebound, " +
                    "counts it, and can drive bounciness off how far the shot " +
                    "fell.");
                return;
            }

            // What bounces. Terrain is the "Ground" layer, enemies "Entities";
            // resolved by NAME so a layer reshuffle can't silently retarget it.
            string targets =
                ((string)(o != null ? o["targets"] : null) ?? "terrain")
                    .Trim().ToLowerInvariant();

            int ground = LayerMask.NameToLayer("Ground");
            int entities = LayerMask.NameToLayer("Entities");

            int bits = 0;
            bool hitsEnemies = false;

            switch (targets)
            {
                case "none":
                    break;

                case "enemies":
                case "enemy":
                    if (entities >= 0) bits |= 1 << entities;
                    hitsEnemies = true;
                    break;

                case "both":
                case "all":
                    if (ground >= 0) bits |= 1 << ground;
                    if (entities >= 0) bits |= 1 << entities;
                    hitsEnemies = true;
                    break;

                case "terrain":
                case "ground":
                case "walls":
                    if (ground >= 0) bits |= 1 << ground;
                    break;

                default:
                    Log.LogWarning(
                        fileName + ": ricochet targets '" + targets +
                        "' is not recognised - use \"terrain\", " +
                        "\"enemies\", \"both\" or \"none\". Defaulting to " +
                        "terrain.");
                    if (ground >= 0) bits |= 1 << ground;
                    break;
            }

            // A FRESH object, never the one already on the weapon.
            // ProjectileBounceData is a CLASS, so if Unity's ScriptableObject
            // clone shares that reference with the stock asset, mutating it in
            // place would make every White Popper in the game ricochet. Same
            // trap the shared muzzle prefab had; the cheap fix is to never
            // touch the existing instance.
            var bounce = new ProjectileBounceData();
            bounce.enabled = bits != 0;
            bounce.layerMask = bits;
            pw.projectileBounceData = bounce;

            if (bits == 0)
            {
                Log.LogInfo(
                    fileName + ": ricochet targets \"none\" - bouncing " +
                    "turned off.");
                return;
            }

            // Pierce beats bounce ON ENEMIES, in the game's own ordering:
            // OnObjectHit returns early on a pierce before it ever reaches the
            // bounce branch (terrain is exempt from that early-out, so walls
            // still bounce). Both stock bouncers ship with piercing off. Which
            // one wins is the weapon's call.
            bool pierceWins =
                (bool?)(o != null ? o["pierceWins"] : null) ?? false;

            var pierce = pw.piercingData;

            if (hitsEnemies && pierce.enabled)
            {
                if (pierceWins)
                {
                    Log.LogWarning(
                        fileName + ": piercingData is on and pierceWins is " +
                        "true, so shots will PASS THROUGH enemies rather " +
                        "than bounce off them - only terrain will ricochet. " +
                        "Set \"pierceWins\": false to get enemy bounces.");
                }
                else
                {
                    pierce.enabled = false;
                    pw.piercingData = pierce;

                    Log.LogWarning(
                        fileName + ": ricochet targets enemies, so piercing " +
                        "has been turned OFF - the game checks piercing " +
                        "first and would pass through enemies instead of " +
                        "bouncing off them. Set \"pierceWins\": true to keep " +
                        "piercing and ricochet off terrain only.");
                }
            }

            GameObject clone = VisualCustomizer.ClonePrefab(
                pw.projectilePrefab.gameObject);

            var comp = clone.GetComponentInChildren<Projectile>(true);
            if (comp == null)
            {
                Log.LogWarning(
                    fileName + ": ricochet - no Projectile on prefab.");
                return;
            }

            var ric = comp.GetComponent<ForgeRicochet>();
            if (ric == null)
                ric = comp.gameObject.AddComponent<ForgeRicochet>();

            ric.bounces = ParseBounces(o, fileName);

            if (o != null)
            {
                ric.speedMultiplier = (float?)o["speedMultiplier"] ?? 1f;
                ric.damageMultiplier = (float?)o["damageMultiplier"] ?? 1f;
                ric.scatter = Mathf.Max(0f, (float?)o["scatter"] ?? 0f);
                ric.seek = (bool?)o["seek"] ?? false;
                ric.seekRange = (float?)o["seekRange"] ?? 20f;
                ric.seekCone = (float?)o["seekCone"] ?? 180f;

                // Effects on every bounce, with the shot living on. Same key
                // names as the lobbed "bounce" block, because they mean the
                // same thing on the other half of the mod.
                ric.explodeOnBounce = (bool?)o["explodeOnBounce"] ?? false;
                ric.dischargeOnBounce = (bool?)o["dischargeOnBounce"] ?? false;
                ric.subOnBounce = (bool?)o["subOnBounce"] ?? false;
                ric.effectOnBounce = (bool?)o["effectOnBounce"] ?? false;

                string effectsOn =
                    ((string)o["bounceEffectsOn"] ?? "any")
                        .Trim().ToLowerInvariant();

                if (effectsOn == "terrain" || effectsOn == "ground" ||
                    effectsOn == "walls")
                {
                    ric.effectsOn = ForgeRicochet.Where.Terrain;
                }
                else if (effectsOn == "enemies" || effectsOn == "enemy")
                {
                    ric.effectsOn = ForgeRicochet.Where.Enemy;
                }
                else
                {
                    ric.effectsOn = ForgeRicochet.Where.Any;

                    if (effectsOn != "any" && effectsOn != "both")
                    {
                        Log.LogWarning(
                            fileName + ": ricochet \"bounceEffectsOn\": '" +
                            effectsOn + "' is not recognised - use \"any\", " +
                            "\"terrain\" or \"enemies\". Using any.");
                    }
                }
            }

            ric.groundLayer = ground;

            if (ric.WantsEffects)
            {
                // An explosion needs somewhere to come from. SpawnExplosion
                // dereferences damages[0].damageType for the blast prefab and
                // throws on an empty list - which shows up as a shot that
                // never dies and keeps hitting, not as an error.
                if (ric.explodeOnBounce &&
                    (pw.explosion.damages == null ||
                     pw.explosion.damages.Count == 0 ||
                     pw.explosion.radius <= 0f))
                {
                    ric.explodeOnBounce = false;

                    Log.LogWarning(
                        fileName + ": ricochet \"explodeOnBounce\" needs an " +
                        "\"explosion\" block with a radius and at least one " +
                        "damage entry - this weapon has none, so it has been " +
                        "turned off rather than firing a blast with nothing " +
                        "in it.");
                }

                if (ric.dischargeOnBounce && pw.discharge.chainLength <= 0)
                {
                    ric.dischargeOnBounce = false;

                    Log.LogWarning(
                        fileName + ": ricochet \"dischargeOnBounce\" needs a " +
                        "\"discharge\" block with a chainLength above 0 - " +
                        "turned off.");
                }

                if (ric.WantsEffects)
                {
                    Log.LogInfo(
                        fileName + ": ricochet fires " +
                        (ric.explodeOnBounce ? "an explosion " : "") +
                        (ric.dischargeOnBounce ? "a discharge " : "") +
                        (ric.subOnBounce ? "its sub-emitter " : "") +
                        (ric.effectOnBounce ? "its impact art " : "") +
                        "on EVERY bounce off " +
                        (ric.effectsOn == ForgeRicochet.Where.Terrain
                            ? "terrain"
                            : ric.effectsOn == ForgeRicochet.Where.Enemy
                                ? "enemies"
                                : "anything") +
                        ", and the shot carries on. The game makes bouncing " +
                        "and exploding mutually exclusive, so this is all " +
                        "new. Point-blank bounces from " +
                        "impactBehaviour.safetyDistance are deliberately " +
                        "excluded - that guard exists so a weapon does not " +
                        "blow up in your face.");
                }
            }

            pw.projectilePrefab = comp;

            // Without a way to expire, an unlimited bouncer is immortal - and
            // the two things that would stop it are exactly the two a beginner
            // has not set.
            if (ric.Unlimited &&
                !pw.rangeData.enabled && !pw.lifetimeData.enabled)
            {
                Log.LogWarning(
                    fileName + ": ricochet bounces is unlimited and this " +
                    "weapon has neither rangeData nor lifetimeData enabled, " +
                    "so these shots will bounce around FOREVER and pile up. " +
                    "Give it \"lifetimeData\": { \"enabled\": true, " +
                    "\"time\": 3 } or a bounce limit.");
            }

            Log.LogInfo(
                fileName + ": ricochet off " + targets + ", " +
                (ric.Unlimited
                    ? "unlimited bounces"
                    : ric.bounces + " bounce(s)") +
                (ric.seek ? ", seeking" : string.Empty) +
                (ric.scatter > 0f
                    ? ", scatter " + ric.scatter + " deg"
                    : string.Empty) +
                (ric.speedMultiplier != 1f
                    ? ", speed x" + ric.speedMultiplier + "/bounce"
                    : string.Empty) +
                (ric.damageMultiplier != 1f
                    ? ", damage x" + ric.damageMultiplier + "/bounce"
                    : string.Empty) + ".");
        }

        // "bounces" accepts a number, or "infinite"/"unlimited"/-1. Anything
        // unlimited is stored as -1.
        private static int ParseBounces(JObject o, string fileName)
        {
            JToken token = o != null ? o["bounces"] : null;

            if (token == null || token.Type == JTokenType.Null)
                return 3;

            if (token.Type == JTokenType.String)
            {
                string s = ((string)token ?? string.Empty)
                    .Trim().ToLowerInvariant();

                if (s == "infinite" || s == "unlimited" ||
                    s == "forever" || s == "-1")
                {
                    return -1;
                }

                int parsed;
                if (int.TryParse(s, out parsed))
                    return parsed < 0 ? -1 : parsed;

                Log.LogWarning(
                    fileName + ": ricochet bounces '" + s + "' is not a " +
                    "number or \"infinite\" - using 3.");
                return 3;
            }

            int n = (int?)token ?? 3;
            return n < 0 ? -1 : n;
        }

        // Deployable turret / mine: the projectile repeatedly FIRES another
        // weapon while it is alive, and can damage things touching it. The
        // game has no while-alive hook (subEmitter is death-only), so this
        // attaches a ForgeTurret component that ticks itself. Pair it with
        // rangeData.slowDown + lifetimeData to get a shot that glides to a
        // stop, works for a few seconds, then vanishes.
        private static void ApplyTurret(
            WeaponData weapon,
            JObject root,
            string fileName)
        {
            if (!((bool?)root["turret"] ?? false))
                return;

            string turretWeapon = (string)root["turretWeapon"];
            if (string.IsNullOrEmpty(turretWeapon))
            {
                Log.LogWarning(
                    fileName + ": \"turret\" is on but no \"turretWeapon\" " +
                    "was given - ignored.");
                return;
            }

            // A turret that fires ITSELF multiplies without bound.
            if (string.Equals(turretWeapon, weapon.name,
                    System.StringComparison.OrdinalIgnoreCase))
            {
                Log.LogWarning(
                    fileName + ": turretWeapon '" + turretWeapon + "' is " +
                    "this weapon itself - ignored (it would spawn turrets " +
                    "endlessly).");
                return;
            }

            var pw = weapon as ProjectileWeaponData;
            if (!ProjectileTargetOk(
                    pw, "turret", fileName, false, null))
            {
                return;
            }

            // usePhysics fires a PhysicsProjectile, a different class that
            // this component's Projectile lookup wouldn't find.
            if (pw.usePhysics)
            {
                Log.LogWarning(
                    fileName + ": turret is not supported on usePhysics " +
                    "projectiles - ignored.");
                return;
            }

            // An orbit weapon snapshots its orb visual BEFORE this runs, so
            // the two can't be combined - say so instead of silently
            // dropping the turret.
            ForgeOrbit.Config orbitCfg;
            if (ForgeOrbit.TryGet(weapon, out orbitCfg))
            {
                Log.LogWarning(
                    fileName + ": turret can't be combined with orbit - " +
                    "the turret is ignored on an orbit weapon.");
                return;
            }

            // The carrier MUST be able to die, or every shot leaves a
            // permanent turret behind (clones are HideAndDontSave, so one
            // would even survive into the next run).
            var lt = pw.lifetimeData;
            bool diesByRange = pw.rangeData.enabled && pw.rangeData.destroyWhenReached;
            if (!lt.enabled && !diesByRange && !pw.impactBehaviour.enabled)
            {
                lt.enabled = true;
                if (lt.time <= 0f)
                    lt.time = 8f;
                pw.lifetimeData = lt;
                Log.LogWarning(
                    fileName + ": turret has no way to expire - forcing " +
                    "lifetimeData " + lt.time + "s so it can't live forever.");
            }

            GameObject clone = VisualCustomizer.ClonePrefab(
                pw.projectilePrefab.gameObject);

            var comp = clone.GetComponentInChildren<Projectile>(true);
            if (comp == null)
            {
                Log.LogWarning(fileName + ": turret - no Projectile on prefab.");
                return;
            }

            // Must live on the Projectile's own GameObject - the component
            // reads its Owner/Damage via GetComponent<Projectile>().
            var turret = comp.GetComponent<ForgeTurret>();
            if (turret == null)
                turret = comp.gameObject.AddComponent<ForgeTurret>();

            turret.weaponName = turretWeapon;
            turret.interval = (float?)root["turretInterval"] ?? 0.5f;
            turret.rotationSpeed = (float?)root["turretRotation"] ?? 90f;
            turret.startAngle = (float?)root["turretStartAngle"] ?? 0f;
            turret.searchRange = (float?)root["turretRange"] ?? 12f;
            turret.startDelay = (float?)root["turretDelay"] ?? 0f;
            turret.contactDamage = (bool?)root["turretContactDamage"] ?? true;
            turret.contactRadius = (float?)root["turretContactRadius"] ?? 0.5f;
            turret.contactRepeatDelay =
                (float?)root["turretContactDelay"] ?? 0.4f;

            string aim =
                ((string)root["turretAim"] ?? "rotate")
                    .Trim().ToLowerInvariant();
            turret.aimMode = (aim == "nearest" || aim == "enemy" ||
                              aim == "target")
                ? ForgeTurret.AimNearest
                : ForgeTurret.AimRotate;

            string dirn =
                ((string)root["turretDirection"] ?? "cw")
                    .Trim().ToLowerInvariant();
            turret.clockwise = !(dirn == "ccw" ||
                dirn == "counterclockwise" || dirn == "counter");

            // Keep the turret PARKED. Without piercing, touching an enemy
            // runs the game's impact path, which snaps the projectile onto
            // the hit point - the "hovering" disc gets dragged around by
            // whatever bumps it. Piercing makes enemy contact a pass-
            // through instead (ground still collides, so it can still
            // bounce off walls). The repeat delays keep the engine's own
            // knockback from firing every physics step.
            var pd = pw.piercingData;
            if (!pd.enabled)
            {
                pd.enabled = true;
                pd.damageRepeatDelay =
                    Mathf.Max(pd.damageRepeatDelay, turret.contactRepeatDelay);
                pd.knockBackRepeatDelay =
                    Mathf.Max(pd.knockBackRepeatDelay, turret.contactRepeatDelay);
                pw.piercingData = pd;
            }

            pw.projectilePrefab = comp;

            Log.LogInfo(
                fileName + ": turret (" + aim + ", every " +
                turret.interval + "s, fires '" + turretWeapon + "'" +
                (turret.aimMode == ForgeTurret.AimRotate
                    ? ", " + turret.rotationSpeed + " deg/s " +
                      (turret.clockwise ? "cw" : "ccw")
                    : "") + ")");
        }

        // Wires up an "orbit" weapon: projectiles that circle the player.
        // A fully custom behaviour (ForgeOrbit + ForgeOrbitController +
        // ForgeOrbitPatch) - the game has no orbital mechanic. All knobs
        // are read here from root-level JSON. Orb COUNT and per-hit DAMAGE
        // are read live from the weapon at runtime, so +projectile and
        // +damage modules scale the ring.
        private static void ApplyOrbit(
            WeaponData weapon,
            JObject root,
            string fileName)
        {
            bool orbit = (bool?)root["orbit"] ?? false;
            if (!orbit)
                return;

            var cfg = new ForgeOrbit.Config();

            string mode =
                ((string)root["orbitMode"] ?? "passive")
                    .Trim().ToLowerInvariant();
            if (mode == "hold") cfg.mode = ForgeOrbit.Mode.Hold;
            else if (mode == "toggle") cfg.mode = ForgeOrbit.Mode.Toggle;
            else if (mode == "fire") cfg.mode = ForgeOrbit.Mode.Fire;
            else cfg.mode = ForgeOrbit.Mode.Passive;

            string dirn =
                ((string)root["orbitDirection"] ?? "cw")
                    .Trim().ToLowerInvariant();
            cfg.clockwise = !(dirn == "ccw" ||
                dirn == "counterclockwise" || dirn == "counter");

            cfg.radius = (float?)root["orbitRadius"] ?? 3f;

            // Concentric rings.
            cfg.rings = (int?)root["orbitRings"] ?? 1;
            if (cfg.rings < 1) cfg.rings = 1;
            cfg.ringSpacing = (float?)root["orbitRingSpacing"] ?? 1.5f;
            cfg.ringAlternate = (bool?)root["orbitRingAlternate"] ?? false;
            cfg.ringSpeedStep = (float?)root["orbitRingSpeedStep"] ?? 1f;

            string ringMode =
                ((string)root["orbitRingMode"] ?? "split")
                    .Trim().ToLowerInvariant();
            cfg.ringsFullCount = (ringMode == "full" || ringMode == "each" ||
                                  ringMode == "perring");

            // "auto" (default) staggers each ring by half a slot so the orbs
            // interleave; a number sets the offset per ring in degrees.
            JToken staggerTok = root["orbitRingStagger"];
            if (staggerTok == null)
            {
                cfg.ringStagger = -1f;
            }
            else
            {
                string st = staggerTok.ToString().Trim().ToLowerInvariant();
                if (st == "auto" || st == "")
                    cfg.ringStagger = -1f;
                else if (st == "aligned" || st == "none")
                    cfg.ringStagger = 0f;
                else
                    cfg.ringStagger = (float?)staggerTok ?? -1f;
            }
            cfg.speed = (float?)root["orbitSpeed"] ?? 120f;
            cfg.hitRadius = (float?)root["orbitHitRadius"] ?? 0.6f;
            cfg.contactDamage = (bool?)root["orbitContactDamage"] ?? true;
            cfg.weaponEffects = (bool?)root["orbitWeaponEffects"] ?? true;
            cfg.damageRepeatDelay = (float?)root["orbitDamageRepeatDelay"] ?? 0.3f;
            cfg.blockProjectiles = (bool?)root["orbitBlockProjectiles"] ?? false;
            // Only pay for enemy-projectile tracking if something wants it.
            if (cfg.blockProjectiles)
                ForgeProjectileTracker.Enabled = true;
            cfg.pushForce = (float?)root["orbitPushForce"] ?? 0f;
            cfg.pulseAmount = (float?)root["orbitPulse"] ?? 0f;
            cfg.pulseSpeed = (float?)root["orbitPulseSpeed"] ?? 1f;
            cfg.spinUpSeconds = (float?)root["orbitSpinUp"] ?? 0f;
            cfg.fling = (bool?)root["orbitFling"] ?? false;
            cfg.flingReach = (float?)root["orbitFlingReach"] ?? 4f;
            cfg.flingDuration = (float?)root["orbitFlingDuration"] ?? 0.35f;
            cfg.suppressFire = (bool?)root["orbitSuppressFire"] ?? true;
            cfg.holdDrainPerSecond =
                0f;

            // Accepts a number OR the word "cost", which derives the rate from
            // the weapon itself: cost x fireRate is what a normal weapon spends
            // per second with its trigger held, so the ring ends up costing
            // what the weapon says it costs. Same token-or-number shape as
            // "orbitRingStagger": "auto".
            JToken drainTok = root["orbitHoldDrainPerSecond"];

            if (drainTok != null && drainTok.Type != JTokenType.Null)
            {
                string drainText =
                    drainTok.ToString().Trim().ToLowerInvariant();

                if (drainText == "cost" || drainText == "weapon" ||
                    drainText == "auto")
                {
                    cfg.holdDrainFromCost = true;
                }
                else
                {
                    // Parsed rather than cast: the field is free text in the
                    // builder page now that it also takes a word, so a number
                    // typed into it arrives as the STRING "2.5" and a straight
                    // (float?) cast is not guaranteed to convert that.
                    float parsed;

                    if (float.TryParse(
                            drainText,
                            System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out parsed))
                    {
                        cfg.holdDrainPerSecond = Mathf.Max(0f, parsed);
                    }
                    else if (drainText.Length > 0)
                    {
                        Log.LogWarning(
                            fileName + ": orbit " +
                            "\"orbitHoldDrainPerSecond\": '" + drainText +
                            "' is neither a number nor \"cost\" - the ring " +
                            "will be free to hold. Use \"cost\" to price it " +
                            "off the weapon, or a number for a flat rate.");
                    }
                }
            }
            cfg.fireDuration = (float?)root["orbitFireDuration"] ?? 3f;

            // Letting go of the ring. A short fade is ON by default because
            // the orbs blinking out mid-orbit reads as a glitch; set 0 for
            // the old instant pop.
            cfg.releaseFade = (float?)root["orbitReleaseFade"] ?? 0.3f;
            cfg.releaseShrink = (bool?)root["orbitReleaseShrink"] ?? true;
            cfg.releaseDamage = (bool?)root["orbitReleaseDamage"] ?? false;

            if (cfg.releaseFade < 0f)
                cfg.releaseFade = 0f;

            // Bringing it up. OFF by default, unlike the release fade: nobody
            // complained about the ring appearing, and switching this on for
            // everyone would change the feel of every orbit weapon already
            // tuned around an instant ring.
            cfg.appearFade = (float?)root["orbitAppearFade"] ?? 0f;
            cfg.appearGrow = (bool?)root["orbitAppearGrow"] ?? true;
            cfg.appearDamage = (bool?)root["orbitAppearDamage"] ?? false;

            if (cfg.appearFade < 0f)
                cfg.appearFade = 0f;

            // Spiral-outward mode: orbs travel out from the ship instead of
            // holding a fixed ring. "launch" = spiral out then fly off toward
            // enemies (don't return); "sweep" = spiral out and recycle.
            string spiral =
                ((string)root["orbitSpiral"] ?? "off")
                    .Trim().ToLowerInvariant();
            if (spiral == "launch" || spiral == "launchandleave")
                cfg.spiral = ForgeOrbit.SpiralMode.Launch;
            else if (spiral == "sweep" || spiral == "recycle")
                cfg.spiral = ForgeOrbit.SpiralMode.Sweep;
            else
                cfg.spiral = ForgeOrbit.SpiralMode.Off;

            cfg.spiralInner = (float?)root["orbitSpiralInner"] ?? 0.4f;
            cfg.spiralOutTime = (float?)root["orbitSpiralTime"] ?? 0.8f;
            cfg.spiralLaunchSpeed = (float?)root["orbitSpiralLaunchSpeed"] ?? 12f;
            cfg.spiralKillDistance = (float?)root["orbitSpiralRange"] ?? 14f;

            // Terrain digging. Orbs have no collision layer of their own, so
            // this is what makes them chew walls the way a normal shot does.
            cfg.damageTerrain = (bool?)root["orbitDamageTerrain"] ?? false;
            cfg.terrainRepeatDelay =
                (float?)root["orbitTerrainRepeatDelay"] ?? 0.15f;

            // Destructible orbs + regen.
            cfg.destroyOnEnemy = (bool?)root["orbitDestroyOnEnemy"] ?? false;
            cfg.destroyOnTerrain = (bool?)root["orbitDestroyOnTerrain"] ?? false;
            cfg.regenTime = (float?)root["orbitRegenTime"] ?? 3f;
            cfg.popExplosion = (bool?)root["orbitPopExplosion"] ?? false;
            cfg.popRadius = (float?)root["orbitPopRadius"] ?? 1.5f;

            string regen =
                ((string)root["orbitRegenMode"] ?? "both")
                    .Trim().ToLowerInvariant();
            if (regen == "timer") cfg.regenMode = ForgeOrbit.RegenMode.Timer;
            else if (regen == "fire") cfg.regenMode = ForgeOrbit.RegenMode.Fire;
            else cfg.regenMode = ForgeOrbit.RegenMode.Both;

            // AFTER the destructible flags: the orbLife warning needs to
            // know whether orbs can actually die and regenerate.
            ApplyOrbitGrow(cfg, root, fileName);

            // Orb visual: an explicit prefab, else the weapon's own.
            string vis = (string)root["orbitVisual"];
            if (!string.IsNullOrEmpty(vis))
            {
                var p = JsonFieldMapper.FindAsset(
                    typeof(Projectile), vis) as Projectile;
                if (p != null)
                    cfg.visualPrefab = p.gameObject;
            }

            if (cfg.visualPrefab == null)
            {
                var pw = weapon as ProjectileWeaponData;
                if (pw != null && pw.projectilePrefab != null)
                    cfg.visualPrefab = pw.projectilePrefab.gameObject;
            }

            ForgeOrbit.Register(weapon, cfg);

            Log.LogInfo(
                fileName + ": orbit weapon (" + mode +
                (cfg.clockwise ? ", cw" : ", ccw") +
                ", radius " + cfg.radius +
                (cfg.spiral != ForgeOrbit.SpiralMode.Off
                    ? ", spiral " + spiral
                    : "") +
                (cfg.appearFade > 0f
                    ? ", fades IN over " + cfg.appearFade + "s" +
                      (cfg.appearGrow ? " while swelling" : "") +
                      (cfg.appearDamage
                          ? " and damages while it arrives"
                          : " and is harmless until it arrives")
                    : "") +
                (cfg.releaseFade > 0f
                    ? ", fades out over " + cfg.releaseFade + "s on release" +
                      (cfg.releaseShrink ? " while shrinking" : "") +
                      (cfg.releaseDamage ? " and still damages" : "")
                    : ", vanishes instantly on release") + ")");

            bool upkeepModes =
                cfg.mode == ForgeOrbit.Mode.Hold ||
                cfg.mode == ForgeOrbit.Mode.Toggle;

            if (upkeepModes && cfg.holdDrainFromCost)
            {
                Log.LogInfo(
                    fileName + ": the ring costs " + weapon.cost + " x " +
                    weapon.fireRate + " = " +
                    (weapon.cost * weapon.fireRate).ToString("0.##") +
                    "/sec to hold, taken from the weapon's own cost and fire " +
                    "rate. It switches OFF when you cannot pay.");
            }
            else if (upkeepModes && cfg.holdDrainPerSecond > 0f)
            {
                Log.LogInfo(
                    fileName + ": the ring costs " + cfg.holdDrainPerSecond +
                    "/sec to hold, and switches OFF when you cannot pay.");
            }
            else if (upkeepModes && cfg.suppressFire)
            {
                // The gap he actually hit: a held ring with a cost set on the
                // weapon looks priced and is not.
                Log.LogWarning(
                    fileName + ": this " +
                    (cfg.mode == ForgeOrbit.Mode.Hold ? "held" : "toggled") +
                    " ring is FREE to keep up. The weapon's \"cost\": " +
                    weapon.cost + " does nothing here - the ship charges you " +
                    "when it FIRES, and this mode suppresses firing, so there " +
                    "is no shot to attach a price to. Set " +
                    "\"orbitHoldDrainPerSecond\": \"cost\" to spend the " +
                    "weapon's own cost per second, or a number for a flat " +
                    "rate.");
            }

            // The reason he wanted this, spelled out where it will be read:
            // a fade-in that still hurts does not make holding better than
            // tapping, and "stop the ring being spammable" was the actual ask.
            if (cfg.appearFade > 0f && cfg.appearDamage &&
                (cfg.mode == ForgeOrbit.Mode.Hold ||
                 cfg.mode == ForgeOrbit.Mode.Toggle))
            {
                Log.LogInfo(
                    fileName + ": orbit \"orbitAppearDamage\": true means the " +
                    "ring hurts things while it is still fading in, so " +
                    "tapping the trigger over and over is as good as holding " +
                    "it. Leave it off (the default) if the wind-up is meant " +
                    "to be a cost.");
            }
        }

        // Wires up "electric" (chain-lightning) behavior from root-level
        // flags, generalizing the standalone White Tesla. A hitscan weapon
        // with "dischargeOnFire" fires a discharge from the gun; enemies
        // are made to conduct so it chains; optional buildup/hideBeam
        // polish; and an optional global lightningColor (base or rainbow).
        private static void ApplyElectric(
            WeaponData weapon,
            JObject root,
            string fileName)
        {
            bool dischargeOnFire =
                (bool?)root["dischargeOnFire"] ?? false;

            // Reach override for player chain-lightning (per hop). Global
            // to player electricity; independent of dischargeOnFire.
            var lightningRange = (float?)root["lightningRange"];
            if (lightningRange.HasValue && lightningRange.Value > 0f)
            {
                ForgeElectric.SetLightningRange(lightningRange.Value);
                Log.LogInfo(
                    fileName + ": lightning range " + lightningRange.Value);
            }

            string lightningColor = (string)root["lightningColor"];

            // Lightning color is independent of dischargeOnFire so it can
            // also recolor projectile-discharge weapons if desired.
            if (!string.IsNullOrEmpty(lightningColor))
            {
                string lc = lightningColor.Trim();

                if (lc.Equals("rainbow", StringComparison.OrdinalIgnoreCase) ||
                    lc.Equals("rgb", StringComparison.OrdinalIgnoreCase))
                {
                    ForgeElectric.SetLightningRgb(
                        (float?)root["lightningRgbSpeed"] ?? 0.5f);
                    Log.LogInfo(fileName + ": lightning color RGB");
                }
                else
                {
                    Color parsed;

                    if (VisualCustomizer.TryParseColor(lc, out parsed))
                    {
                        ForgeElectric.SetLightningStatic(parsed);
                        Log.LogInfo(
                            fileName + ": lightning color " + lightningColor);
                    }
                    else
                    {
                        Log.LogWarning(
                            fileName + ": lightningColor '" + lightningColor +
                            "' is not a valid color - ignored");
                    }
                }
            }

            if (!dischargeOnFire)
                return;

            // A chain weapon needs enemies to conduct, so default that on.
            bool chainThroughEnemies =
                (bool?)root["chainThroughEnemies"] ?? true;

            var config = new ForgeElectric.Config
            {
                dischargeOnFire = true,
                chainThroughEnemies = chainThroughEnemies,
                buildupSeconds = (float?)root["buildupSeconds"] ?? 2f,
                hideBeam = (bool?)root["hideBeam"] ?? false
            };

            ForgeElectric.Register(weapon, config);

            // Match the discharge's targeting to the weapon's (set by
            // "target"), mirroring the standalone Tesla. Only hitscan
            // weapons carry a layerMask to copy from.
            var hitscan = weapon as HitscanWeaponData;

            if (hitscan != null)
            {
                var d = weapon.discharge;
                d.layerMask = hitscan.layerMask;
                weapon.discharge = d;
            }

            Log.LogInfo(
                fileName + ": electric weapon (dischargeOnFire" +
                (chainThroughEnemies ? ", chains through enemies" : "") +
                (config.hideBeam ? ", beam hidden" : "") + ")");
        }

        // Attaches burn effects to the module: a tick-rate booster (speeds
        // up how fast burn ticks on enemies) and/or a burn recolor (solid
        // or "rainbow"/"rgb", optionally including terrain). These are
        // ModuleEffects that route through ForgeBurnCompat, so they feed
        // ModuleForge's engine when it's installed or WeaponForge's own
        // otherwise. The player's burn is never affected.
        private static void ApplyBurn(
            ModuleData module,
            JToken tickRate,
            string colorText,
            float rgbSpeed,
            bool includeTerrain,
            string fileName)
        {
            if (module.effects == null)
                module.effects = new List<ModuleEffect>();

            if (tickRate != null)
            {
                // A plain number is a flat boost, exactly as before. An
                // object adds level scaling, so a green-crate BoosterCore
                // next to the weapon makes its burn tick faster:
                //   "burnTickRate": 0.5
                //   "burnTickRate": { "base": 0.5, "perLevel": 0.25 }
                float tr;
                float perLevel = 0f;

                var obj = tickRate as JObject;

                if (obj != null)
                {
                    tr = (float?)obj["base"] ??
                         (float?)obj["baseValue"] ??
                         (float?)obj["amount"] ?? 0f;

                    perLevel = (float?)obj["perLevel"] ??
                               (float?)obj["change"] ?? 0f;
                }
                else
                {
                    tr = (float)tickRate;
                }

                module.effects.Add(
                    new ForgeBurnRateEffect
                    {
                        ticksPerSecond = tr,
                        ticksPerSecondPerLevel = perLevel
                    });

                Log.LogInfo(
                    fileName + ": burn tick rate +" + tr + "/s" +
                    (perLevel != 0f
                        ? " (+" + perLevel + "/s per level above 1, so a " +
                          "BoosterCore raises it)"
                        : string.Empty));
            }

            if (!string.IsNullOrEmpty(colorText))
            {
                var effect = new ForgeBurnColorEffect
                {
                    rgbSpeed = rgbSpeed,
                    includeTerrain = includeTerrain
                };

                string c = colorText.Trim();

                if (c.Equals("rainbow", StringComparison.OrdinalIgnoreCase) ||
                    c.Equals("rgb", StringComparison.OrdinalIgnoreCase))
                {
                    effect.rgb = true;
                    effect.colorLabel = "RGB";
                }
                else
                {
                    Color parsed;

                    if (VisualCustomizer.TryParseColor(c, out parsed))
                    {
                        effect.color = parsed;
                        effect.colorLabel = colorText;
                    }
                    else
                    {
                        Log.LogWarning(
                            fileName + ": burnColor '" + colorText +
                            "' is not a valid color - skipped");
                        return;
                    }
                }

                module.effects.Add(effect);

                Log.LogInfo(
                    fileName + ": burn color " +
                    (effect.rgb ? "RGB" : colorText) +
                    (includeTerrain ? " +terrain" : ""));
            }
        }

        // "chain": a shot that hops from enemy to enemy.
        //
        //   "chain": true
        //   "chain": { "jumps": 4, "range": 8, "damageMultiplier": 0.8,
        //              "speedMultiplier": 1, "revisit": false,
        //              "onEmpty": "continue" }
        private static void ApplyChain(
            WeaponData weapon,
            JObject root,
            string fileName)
        {
            JToken token = root["chain"];

            if (token == null || token.Type == JTokenType.Null)
                return;

            var o = token as JObject;

            if (o == null)
            {
                if (token.Type != JTokenType.Boolean || !(bool)token)
                {
                    Log.LogWarning(
                        fileName + ": \"chain\" should be true or an object " +
                        "- ignored.");
                    return;
                }
            }
            else if (!((bool?)o["enabled"] ?? true))
            {
                return;
            }

            var pw = weapon as ProjectileWeaponData;

            if (!ProjectileTargetOk(
                    pw, "chain", fileName, false,
                    " Chaining is driven by Projectile.TryHit, which a lobbed " +
                    "shot never reaches. For a lobbed weapon, the game's own " +
                    "discharge (\"electricityData\") gives a similar " +
                    "enemy-to-enemy feel."))
            {
                return;
            }

            GameObject clone = VisualCustomizer.ClonePrefab(
                pw.projectilePrefab.gameObject);

            Component comp = clone.GetComponentInChildren<Projectile>(true);

            if (comp == null)
            {
                Log.LogWarning(
                    fileName + ": chain - no Projectile on the prefab. " +
                    "Ignored.");
                UnityEngine.Object.Destroy(clone);
                return;
            }

            var c = comp.GetComponent<ForgeChain>();

            if (c == null)
                c = comp.gameObject.AddComponent<ForgeChain>();

            if (o != null)
            {
                c.jumps = (int?)o["jumps"] ?? 3;
                c.range = (float?)o["range"] ?? 8f;
                c.damageMultiplier = (float?)o["damageMultiplier"] ?? 1f;
                c.speedMultiplier = (float?)o["speedMultiplier"] ?? 1f;
                c.revisit = (bool?)o["revisit"] ?? false;
                c.jumpDelay = (float?)o["jumpDelay"] ?? 0f;
                c.hoverDuringDelay = (bool?)o["hover"] ?? false;

                if (c.jumpDelay < 0f)
                    c.jumpDelay = 0f;

                string onEmpty =
                    ((string)o["onEmpty"] ?? "continue")
                        .Trim().ToLowerInvariant();

                c.vanishWhenNoTarget =
                    (onEmpty == "vanish" || onEmpty == "die" ||
                     onEmpty == "explode");
            }

            pw.projectilePrefab = (Projectile)comp;

            // THE thing that makes a chain work at all. The game only keeps a
            // shot alive through a hit when PiercingData.enabled is on
            // (Projectile.OnObjectHit destroys it otherwise), so without this
            // a chain weapon dies on its first target and reads as "chain
            // does nothing". Turned on rather than warned about, because
            // there is no sane reading of "chain" that wants the shot to stop
            // on contact.
            PiercingData pierce = pw.piercingData;

            bool forced = false;

            if (!pierce.enabled)
            {
                pierce.enabled = true;
                forced = true;
            }

            // Never 0: that re-damages the same enemy every frame it overlaps
            // - the machine-gun bug the deflector shipped with once.
            if (pierce.damageRepeatDelay <= 0.01f)
                pierce.damageRepeatDelay = 0.3f;

            pw.piercingData = pierce;

            if (forced)
            {
                Log.LogInfo(
                    fileName + ": chain turned piercing ON - a shot has to " +
                    "survive the hit to be able to jump anywhere.");
            }

            if (c.hoverDuringDelay && c.jumpDelay <= 0f)
            {
                Log.LogWarning(
                    fileName + ": chain \"hover\" needs a \"jumpDelay\" to " +
                    "hover FOR - with no delay the shot turns the instant it " +
                    "hits and never stops. Try \"jumpDelay\": 0.15.");
            }

            if (c.jumps <= 0)
            {
                Log.LogWarning(
                    fileName + ": chain \"jumps\": " + c.jumps +
                    " means it never redirects, so this is just a piercing " +
                    "shot. Use 2 or more.");
            }

            Log.LogInfo(
                fileName + ": chain - up to " + c.jumps + " jump(s) within " +
                c.range + " units" +
                (c.revisit
                    ? ", MAY revisit the same enemy"
                    : ", never revisits a target") +
                (c.damageMultiplier != 1f
                    ? ", x" + c.damageMultiplier + " damage per jump"
                    : "") +
                (c.speedMultiplier != 1f
                    ? ", x" + c.speedMultiplier + " speed per jump"
                    : "") +
                (c.jumpDelay > 0f
                    ? ", waiting " + c.jumpDelay + "s before each jump" +
                      (c.hoverDuringDelay ? " (hovering)" : "")
                    : "") +
                (c.vanishWhenNoTarget
                    ? ", vanishes when nothing is in range"
                    : ", flies on when nothing is in range") + ".");
        }

        // "converge": a shotgun whose pellets meet at a point.
        //
        //   "converge": true
        //   "converge": { "focus": "distance", "distance": 10, "width": 0,
        //                 "after": "spread", "maxTurnRate": 0 }
        //   "converge": { "focus": "target", "range": 14 }
        //
        // Needs "projectileCount" above 1 and a "spread" to be worth anything -
        // the feature shapes a fan, it does not create one.
        private static void ApplyConverge(
            WeaponData weapon,
            JObject root,
            string fileName)
        {
            JToken token = root["converge"];

            if (token == null || token.Type == JTokenType.Null)
                return;

            var o = token as JObject;

            if (o == null)
            {
                if (token.Type != JTokenType.Boolean || !(bool)token)
                {
                    Log.LogWarning(
                        fileName + ": \"converge\" should be true or an " +
                        "object - ignored.");
                    return;
                }
            }
            else if (!((bool?)o["enabled"] ?? true))
            {
                return;
            }

            var pw = weapon as ProjectileWeaponData;

            if (!ProjectileTargetOk(pw, "converge", fileName, true, null))
                return;

            // A lobbed weapon fires a PhysicsProjectile - a different class on
            // a different prefab - and converges by a completely different
            // route (a solved launch rather than mid-flight steering), so it
            // needs its own clone and its own component.
            bool physics = pw.usePhysics;

            GameObject clone = VisualCustomizer.ClonePrefab(
                physics
                    ? pw.physicsProjectilePrefab.gameObject
                    : pw.projectilePrefab.gameObject);

            Component comp = physics
                ? (Component)clone.GetComponentInChildren<PhysicsProjectile>(true)
                : clone.GetComponentInChildren<Projectile>(true);

            if (comp == null)
            {
                Log.LogWarning(
                    fileName + ": converge - no " +
                    (physics ? "PhysicsProjectile" : "Projectile") +
                    " on the prefab. Ignored.");
                UnityEngine.Object.Destroy(clone);
                return;
            }

            var c = comp.GetComponent<ForgeConverge>();

            if (c == null)
                c = comp.gameObject.AddComponent<ForgeConverge>();

            if (o != null)
            {
                c.distance = (float?)o["distance"] ?? 10f;
                c.range = (float?)o["range"] ?? 14f;
                c.width = (float?)o["width"] ?? 0f;
                c.maxTurnRate = (float?)o["maxTurnRate"] ?? 0f;
                c.faceTravel = (bool?)o["faceTravel"] ?? true;

                string focus =
                    ((string)o["focus"] ?? "cursor")
                        .Trim().ToLowerInvariant();

                if (focus == "none" || focus == "spazer" ||
                    focus == "rank" || focus == "abreast")
                {
                    // Deliberately NOT aliased to "parallel" - that already
                    // means something different on the "after" key (straighten
                    // once past the waist), and having one word mean two
                    // things in the same block would be a trap.
                    c.focus = ForgeConverge.Focus.None;
                }
                else if (focus == "target" || focus == "enemy" ||
                    focus == "nearest")
                {
                    c.focus = ForgeConverge.Focus.Target;
                }
                else if (focus == "distance" || focus == "point" ||
                         focus == "fixed")
                {
                    c.focus = ForgeConverge.Focus.Distance;
                }
                else
                {
                    c.focus = ForgeConverge.Focus.Cursor;

                    if (focus != "cursor" && focus != "aim" &&
                        focus != "crosshair" && focus != "mouse")
                    {
                        Log.LogWarning(
                            fileName + ": converge \"focus\": '" + focus +
                            "' is not recognised - use \"cursor\", " +
                            "\"distance\", \"target\" or \"none\" (the " +
                            "spazer). Using cursor.");
                    }
                }

                string after =
                    ((string)o["after"] ?? "spread")
                        .Trim().ToLowerInvariant();

                c.after = (after == "parallel" || after == "straight" ||
                           after == "lane")
                    ? ForgeConverge.After.Parallel
                    : ForgeConverge.After.Spread;
            }

            if (c.distance <= 0f)
            {
                Log.LogWarning(
                    fileName + ": converge \"distance\": " + c.distance +
                    " puts the meeting point on top of the barrel, so the " +
                    "pellets have arrived before they have flown. Use " +
                    "something like 8-14.");
                c.distance = 10f;
            }

            if (c.width < 0f)
                c.width = 0f;

            // A spazer IS its rank. With no width every shot leaves from the
            // same point on the same heading, so they overlap perfectly and
            // the weapon looks like it fires one bullet - which is not a
            // broken-looking mistake, it is an invisible one. Fill one in
            // rather than ship that.
            if (c.focus == ForgeConverge.Focus.None && c.width <= 0f)
            {
                c.width = 1.2f;

                Log.LogInfo(
                    fileName + ": converge \"focus\": \"none\" (spazer) " +
                    "with no \"width\" - defaulted to 1.2. The width is what " +
                    "separates the shots; without it they all fly the same " +
                    "line and you see one bullet.");
            }

            // A lobbed volley cannot use the weapon's angular spread the way
            // a straight one does - a grenade thrown at or below horizontal
            // never reaches a level target at any speed - so the spread is
            // reinterpreted as a spread of ARC HEIGHTS. Taking the weapon's
            // own "spread" as the default keeps the same JSON meaning the
            // same thing: "how varied is this volley".
            if (physics)
            {
                float loft = (o != null) ? ((float?)o["loft"] ?? -1f) : -1f;

                if (loft < 0f)
                    loft = (pw.spread > 0f) ? pw.spread : 30f;

                c.loftSpread = Mathf.Clamp(loft, 0f, 70f);
            }

            if (physics)
                pw.physicsProjectilePrefab = (PhysicsProjectile)comp;
            else
                pw.projectilePrefab = (Projectile)comp;

            // A lobbed shot is never steered - its whole arc is decided at the
            // moment of launch - so the two mid-flight dials do nothing. Say
            // so rather than letting them be set and quietly ignored.
            if (physics &&
                (c.after == ForgeConverge.After.Parallel || c.maxTurnRate > 0f))
            {
                Log.LogWarning(
                    fileName + ": converge \"after\" and \"maxTurnRate\" are " +
                    "ignored on a LOBBED (usePhysics) weapon. A lobbed shot " +
                    "converges by having its launch SPEED solved so its arc " +
                    "lands on the point - nothing steers it afterwards, so " +
                    "there is no turn to cap and no heading to straighten.");
            }

            // The feature shapes an existing fan; it cannot invent one. A
            // single pellet is already aimed at the focal point, so it curves
            // by exactly nothing and this reads as "converge does not work".
            if (pw.projectileCount <= 1f)
            {
                Log.LogWarning(
                    fileName + ": converge does nothing with " +
                    "\"projectileCount\": " + pw.projectileCount +
                    ". One pellet leaves the barrel already pointed at the " +
                    "focal point, so there is no angle to curve out of. Use " +
                    "5 or more pellets and a \"spread\" (25-45) to see it.");
            }
            else if (c.focus == ForgeConverge.Focus.None && pw.spread > 0f)
            {
                Log.LogInfo(
                    fileName + ": converge \"focus\": \"none\" (spazer) " +
                    "ignores \"spread\": " + pw.spread + " - every shot is " +
                    "snapped onto your aim so the rank stays parallel. Use " +
                    "\"width\" to set how far apart they are.");
            }
            else if (!physics && pw.spread <= 0f && c.width <= 0f)
            {
                Log.LogWarning(
                    fileName + ": converge with \"spread\": 0 and no " +
                    "\"width\" has every pellet on the same line already, so " +
                    "nothing visibly converges. Give the weapon a spread " +
                    "(25-45), or set converge \"width\" to launch the pellets " +
                    "abreast instead.");
            }
            else if (physics && c.loftSpread <= 0f)
            {
                Log.LogWarning(
                    fileName + ": converge \"loft\": 0 on a LOBBED weapon " +
                    "gives every grenade the identical arc, so they land on " +
                    "top of each other. Use 20-40 (or leave it off and it " +
                    "borrows the weapon's \"spread\").");
            }

            // Two steerers on one shot both rotate Velocity in a FixedUpdate
            // prefix, and Harmony does not order prefixes from separate
            // patch classes - so the result is a blend that varies by load
            // order. Worth saying out loud rather than leaving them to wonder
            // why the curve looks half-hearted. Lobbed weapons are exempt:
            // nothing steers them, and ApplyHoming refuses them anyway.
            if (!physics &&
                root["homing"] != null &&
                root["homing"].Type != JTokenType.Null)
            {
                Log.LogWarning(
                    fileName + ": converge AND homing are both set. Both " +
                    "steer the same shot every frame and will fight each " +
                    "other. Pick one - \"converge\": { \"focus\": " +
                    "\"target\" } already collapses the volley onto an enemy, " +
                    "which is most of what homing would have added.");
            }

            if (c.width > 0f && pw.spread > 0f)
            {
                Log.LogInfo(
                    fileName + ": converge \"width\" reads best with " +
                    "\"spread\": 0 - the pellets then leave abreast and " +
                    "square to the aim, so the rank collapses cleanly. With " +
                    "spread " + pw.spread + " the outer pellets start wide " +
                    "AND angled outward, so they turn harder.");
            }

            string where;

            if (c.focus == ForgeConverge.Focus.None)
            {
                where = "NOTHING - the shots stay parallel (spazer)";
            }
            else if (c.focus == ForgeConverge.Focus.Cursor)
            {
                where = "your CROSSHAIR (falling back to " + c.distance +
                        " units ahead when there is none)";
            }
            else if (c.focus == ForgeConverge.Focus.Target)
            {
                where = "the nearest target within " + c.range +
                        " units (falling back to " + c.distance +
                        " units ahead when there is none)";
            }
            else
            {
                where = c.distance + " units ahead of the muzzle";
            }

            Log.LogInfo(
                fileName + ": converge - " +
                (c.focus == ForgeConverge.Focus.None
                    ? "converging on "
                    : (physics
                        ? "LOBBED arcs solved to land on "
                        : "pellets meet ")) + where +
                (c.width > 0f
                    ? ", launched abreast across " + c.width + " units"
                    : "") +
                (physics
                    ? ", lofted across " + c.loftSpread +
                      " deg so each takes its own arc"
                    : (c.after == ForgeConverge.After.Parallel
                        ? ", then running parallel"
                        : ", then crossing and fanning out")) +
                (!physics && c.maxTurnRate > 0f
                    ? ", turn capped at " + c.maxTurnRate + " deg/s"
                    : "") + ".");
        }

        // "damageRamp": a shot whose damage changes while it flies.
        //
        //   "damageRamp": true
        //   "damageRamp": { "from": 1, "to": 3, "over": "distance",
        //                   "span": 0, "curve": 2, "clamp": true }
        //
        // Values are multipliers of the shot's normal damage, so this
        // composes with +damage modules instead of replacing them, and
        // from > to is a falloff rather than a mistake.
        private static void ApplyDamageRamp(
            WeaponData weapon,
            JObject root,
            string fileName)
        {
            JToken token = root["damageRamp"];

            if (token == null || token.Type == JTokenType.Null)
                return;

            var o = token as JObject;

            if (o == null)
            {
                if (token.Type != JTokenType.Boolean || !(bool)token)
                {
                    Log.LogWarning(
                        fileName + ": \"damageRamp\" should be true or an " +
                        "object - ignored.");
                    return;
                }
            }
            else if (!((bool?)o["enabled"] ?? true))
            {
                return;
            }

            var pw = weapon as ProjectileWeaponData;

            if (!ProjectileTargetOk(
                    pw, "damageRamp", fileName, true,
                    " A beam applies its damage instantly at any range, so " +
                    "there is no flight over which to ramp."))
            {
                return;
            }

            bool physics = pw.usePhysics;

            string over = (o != null)
                ? ((string)o["over"] ?? "distance").Trim().ToLowerInvariant()
                : "distance";

            ForgeDamageRamp.Over mode;

            switch (over)
            {
                case "time": mode = ForgeDamageRamp.Over.Time; break;
                case "pierce":
                case "pierces":
                case "hits": mode = ForgeDamageRamp.Over.Pierce; break;
                case "bounce":
                case "bounces":
                case "ricochet": mode = ForgeDamageRamp.Over.Bounce; break;
                case "distance":
                case "range": mode = ForgeDamageRamp.Over.Distance; break;
                default:
                    Log.LogWarning(
                        fileName + ": damageRamp \"over\": '" + over +
                        "' is not one of distance / time / pierce / bounce - " +
                        "using distance.");
                    mode = ForgeDamageRamp.Over.Distance;
                    break;
            }

            // Pierce and bounce are counted by patches on Projectile.TryHit
            // and Projectile.Reflect. A PhysicsProjectile is a different class
            // that goes through neither, so those two modes would sit at their
            // starting multiplier forever - say so rather than look broken.
            if (physics &&
                (mode == ForgeDamageRamp.Over.Pierce ||
                 mode == ForgeDamageRamp.Over.Bounce))
            {
                Log.LogWarning(
                    fileName + ": damageRamp \"over\": \"" + over + "\" does " +
                    "not work on a LOBBED (usePhysics) weapon - a lobbed shot " +
                    "neither pierces nor reflects through the code this " +
                    "counts. Using \"distance\" instead, which does work.");
                mode = ForgeDamageRamp.Over.Distance;
            }

            GameObject clone = VisualCustomizer.ClonePrefab(
                physics
                    ? pw.physicsProjectilePrefab.gameObject
                    : pw.projectilePrefab.gameObject);

            Component comp = physics
                ? (Component)clone.GetComponentInChildren<PhysicsProjectile>(true)
                : clone.GetComponentInChildren<Projectile>(true);

            if (comp == null)
            {
                Log.LogWarning(
                    fileName + ": damageRamp - no " +
                    (physics ? "PhysicsProjectile" : "Projectile") +
                    " on the prefab. Ignored.");
                UnityEngine.Object.Destroy(clone);
                return;
            }

            var r = comp.GetComponent<ForgeDamageRamp>();

            if (r == null)
                r = comp.gameObject.AddComponent<ForgeDamageRamp>();

            r.over = mode;

            if (o != null)
            {
                r.from = (float?)o["from"] ?? 1f;
                r.to = (float?)o["to"] ?? 3f;
                r.span = (float?)o["span"] ?? 0f;
                r.curve = (float?)o["curve"] ?? 1f;
                r.clamp = (bool?)o["clamp"] ?? true;
            }

            if (physics)
                pw.physicsProjectilePrefab = (PhysicsProjectile)comp;
            else
                pw.projectilePrefab = (Projectile)comp;

            // A ramp that never moves is the one way to get nothing at all,
            // and it is easy to reach by setting only "to".
            if (r.from == r.to)
            {
                Log.LogWarning(
                    fileName + ": damageRamp has from == to (" + r.from +
                    "), so damage never changes. Set them differently - " +
                    "from 1 to 3 means the shot ends up hitting three times " +
                    "as hard.");
            }

            string span = (r.span > 0f)
                ? r.span.ToString("0.##")
                : (mode == ForgeDamageRamp.Over.Distance
                    ? "the weapon's range"
                    : "1");

            Log.LogInfo(
                fileName + ": damage ramp x" + r.from + " -> x" + r.to +
                " over " + span + " " +
                (mode == ForgeDamageRamp.Over.Distance ? "units of travel"
                 : mode == ForgeDamageRamp.Over.Time ? "seconds"
                 : mode == ForgeDamageRamp.Over.Pierce ? "pierced enemies"
                 : "bounces") +
                (r.clamp ? "" : ", UNCLAMPED (keeps climbing past 'to')") +
                (r.curve != 1f ? ", curve " + r.curve : "") + ".");
        }

        // "orbitGrow": orbs that change SIZE while the ring runs.
        //
        // Deliberately its own block rather than reusing the projectile
        // "grow": an orb is not a projectile (its Projectile component is
        // switched off and the ring does its own damage), so none of grow's
        // machinery applies. The clocks are different too - an orb has no
        // range to travel, so growth runs off the ring's active time, the
        // orb's own life, or a breath cycle.
        //
        //   "orbitGrow": true
        //   "orbitGrow": { "from": 0.5, "to": 3, "over": "charge",
        //                  "span": 4, "curve": 1, "damageAtFull": 2,
        //                  "hitbox": true, "breatheSpeed": 1 }
        private static void ApplyOrbitGrow(
            ForgeOrbit.Config cfg,
            JObject root,
            string fileName)
        {
            JToken token = root["orbitGrow"];

            if (token == null || token.Type == JTokenType.Null)
                return;

            var o = token as JObject;

            if (o == null)
            {
                if (token.Type != JTokenType.Boolean || !(bool)token)
                {
                    Log.LogWarning(
                        fileName + ": \"orbitGrow\" should be true or an " +
                        "object - ignored.");
                    return;
                }
            }
            else if (!((bool?)o["enabled"] ?? true))
            {
                return;
            }

            // Restart growth every time the ring switches on, so each press
            // rebuilds the charge. Charge mode already behaved this way;
            // orbLife did not, because the orbs are only hidden when the ring
            // goes off rather than destroyed - so they came back full size.
            cfg.growReset = (o != null)
                ? ((bool?)o["reset"] ?? true)
                : true;

            string over = (o != null)
                ? ((string)o["over"] ?? "charge").Trim().ToLowerInvariant()
                : "charge";

            switch (over)
            {
                case "orblife":
                case "life":
                case "orb":
                    cfg.grow = ForgeOrbit.GrowMode.OrbLife;
                    break;

                case "breathe":
                case "breath":
                case "pulse":
                    cfg.grow = ForgeOrbit.GrowMode.Breathe;
                    break;

                case "charge":
                case "time":
                case "active":
                    cfg.grow = ForgeOrbit.GrowMode.Charge;
                    break;

                default:
                    Log.LogWarning(
                        fileName + ": orbitGrow \"over\": '" + over +
                        "' is not one of charge / orbLife / breathe - " +
                        "using charge.");
                    cfg.grow = ForgeOrbit.GrowMode.Charge;
                    break;
            }

            if (o != null)
            {
                cfg.growFrom = (float?)o["from"] ?? 0.5f;
                cfg.growTo = (float?)o["to"] ?? 3f;
                cfg.growSpan = (float?)o["span"] ?? 4f;
                cfg.growBreatheSpeed = (float?)o["breatheSpeed"] ?? 1f;
                cfg.growCurve = (float?)o["curve"] ?? 1f;
                cfg.growHitbox = (bool?)o["hitbox"] ?? true;
                cfg.growDamageAtFull = (float?)o["damageAtFull"] ?? 0f;
            }

            if (cfg.growSpan <= 0f)
                cfg.growSpan = 4f;

            if (cfg.growBreatheSpeed <= 0f)
                cfg.growBreatheSpeed = 1f;

            // A ring whose orbs never stop growing is the one way to make this
            // feature useless, and it is an easy slip: Charge holds at full,
            // but only because growSpan bounds it.
            if (cfg.grow == ForgeOrbit.GrowMode.OrbLife &&
                !cfg.destroyOnEnemy && !cfg.destroyOnTerrain)
            {
                Log.LogWarning(
                    fileName + ": orbitGrow \"over\": \"orbLife\" measures from " +
                    "when each orb spawned, but these orbs are not " +
                    "destructible - so they all spawn once, grow once, and " +
                    "then stay at full size, which is the same as \"charge\". " +
                    "Add \"orbitDestroyOnEnemy\": true (or DestroyOnTerrain) " +
                    "so orbs die and regenerate small.");
            }

            if (cfg.growDamageAtFull > 0f && !cfg.contactDamage)
            {
                Log.LogWarning(
                    fileName + ": orbitGrow \"damageAtFull\" was set but " +
                    "\"orbitContactDamage\" is false, so the orbs deal no " +
                    "contact damage to scale. The size still changes.");
            }

            Log.LogInfo(
                fileName + ": orbit orbs grow x" + cfg.growFrom + " -> x" +
                cfg.growTo +
                (cfg.grow == ForgeOrbit.GrowMode.Breathe
                    ? " breathing at " + cfg.growBreatheSpeed + " cycles/s"
                    : " over " + cfg.growSpan + "s (" +
                      (cfg.grow == ForgeOrbit.GrowMode.OrbLife
                          ? "each orb's own life"
                          : "while the ring is active") + ")") +
                (cfg.growHitbox
                    ? ", contact radius follows"
                    : ", ART ONLY - contact radius fixed") +
                (cfg.growDamageAtFull > 0f
                    ? ", x" + cfg.growDamageAtFull + " damage at full size"
                    : string.Empty) + ".");
        }

        // Can a projectile-shaping feature act on this weapon at all, and if
        // not, WHY not - in words that point somewhere useful.
        //
        // This exists because the old guard was
        //     if (pw == null || pw.projectilePrefab == null)
        // which conflated three different situations and reported all of them
        // as "only works on projectile weapons". The one that cost real testing
        // time: a LOBBED weapon (usePhysics) is a perfectly good
        // ProjectileWeaponData, but its projectilePrefab is NULL - the shot
        // lives on physicsProjectilePrefab instead. Weapon White Derbis is
        // exactly that, so every feature here refused it while claiming it
        // wasn't a projectile weapon. grow, which does support lobbed shots,
        // was rejected before it could ever run.
        //
        // supportsPhysics: does this feature's Harmony patch hook
        // PhysicsProjectile as well as Projectile? Only grow does today.
        // Can a green-crate BoosterCore raise this weapon's module level?
        //
        // EVERY stock weapon module ships "canBeBoosted: 0" - Popper, Bolt,
        // Worm, Blades, all of them - and a Forge weapon is a clone of one of
        // those, so it inherits the false. ModuleGrid.Install reads
        //
        //     if (module.Data.canBeBoosted) module.Level += GetLevelDelta(pos)
        //
        // and skips the boost entirely. That is why stacking eight
        // BoosterCores around a weapon did precisely nothing while setting
        // "module": { "level": n } by hand worked fine.
        //
        // The field is a real ModuleData member, so the generic mapper already
        // accepts "canBeBoosted": true - this only supplies the default,
        // because a weapon that declares PER-LEVEL scaling and can never gain
        // a level is asking for two contradictory things. Same call as chain
        // forcing piercing on: turn it on and say so, rather than warn and
        // leave a feature that silently does nothing.
        // "gravity": a well that drags everything nearby into one point.
        //
        // The game has no field forces at all. What it does have is the same
        // call pointed the other way - explosions and orbit rings both shove
        // rigidbodies with AddForce - so pulling is that with the sign
        // flipped, and that is why this was cheap.
        // "minion": { "weapon": "<name>" } - what the spawned drone fires.
        //
        // Resolved LAZILY at first use rather than here, because the weapon it
        // names is very often another Forge weapon, and file order decides
        // whether that one has been built yet. Resolving now would work or fail
        // depending on filenames, which is the worst kind of bug to explain.
        private static void ApplyMinion(
            WeaponData weapon,
            JObject root,
            string fileName)
        {
            JToken token = root["minion"];

            if (token == null || token.Type == JTokenType.Null)
                return;

            var o = token as JObject;

            if (o == null)
                return;

            string named = (string)o["weapon"];
            string side = (string)o["faction"];
            float life = Mathf.Max(0f, (float?)o["lifetime"] ?? 0f);
            int cap = Mathf.Max(0, (int?)o["cap"] ?? 0);
            int perLevel = Mathf.Max(0, (int?)o["capPerLevel"] ?? 0);
            bool inheritGrid = (bool?)o["inheritGrid"] ?? false;
            string capModeText = (string)o["capMode"];
            float lookScale = Mathf.Max(0f, (float?)o["scale"] ?? 1f);
            string colorText = (string)o["color"];
            string colorModeText = (string)o["colorMode"];
            string scaleModeText = (string)o["scaleMode"];

            // Any one of these is worth registering on its own - a file may only
            // want a cap, or only a lifetime, without touching the gun.
            if (string.IsNullOrEmpty(named) && string.IsNullOrEmpty(side) &&
                life <= 0f && cap <= 0 && !inheritGrid &&
                string.IsNullOrEmpty(capModeText) &&
                string.IsNullOrEmpty(colorText) && lookScale == 1f &&
                !MentionsStats(o))
            {
                return;
            }

            var pw = weapon as MinionSpawnerWeaponData;

            if (pw == null)
            {
                Log.LogWarning(
                    fileName + ": \"minion\" only applies to a minion-spawner " +
                    "weapon, and this template is not one. Use one of the game's " +
                    "five minion spawners (see the builder page) and set " +
                    "\"minionPrefab\" to pick the drone.");
                return;
            }

            var cfg = new ForgeMinion.Config();
            cfg.weapon = (named ?? "").Trim();

            if (!string.IsNullOrEmpty(side))
                cfg.faction = side.Trim();

            cfg.lifetime = life;
            cfg.cap = cap;
            cfg.capPerLevel = perLevel;
            cfg.inheritGrid = inheritGrid;
            cfg.capMode = ForgeSwarm.ParseCapMode(capModeText);

            ReadMinionLook(
                cfg, colorText, colorModeText, lookScale, scaleModeText,
                fileName);

            ReadMinionStats(cfg, o, fileName);

            ForgeMinionArms.Build(pw.minionPrefab, o, cfg, fileName);

            // HOW MANY PER TRIGGER PULL, said out loud.
            //
            // This is invisible otherwise and it is inherited: `burstSize` and
            // `projectileCount` come from the TEMPLATE unless the file says
            // otherwise, and every stock spawner except the Bomber ships
            // burstSize 4. So a file that mentions neither quietly fires FOUR
            // minions per click, which is easy to mistake for a broken cap.
            int perShot =
                Mathf.Max(1, Mathf.RoundToInt(pw.projectileCount)) *
                Mathf.Max(1, pw.burstSize);

            // ALWAYS logged, not only when the count is above one.
            //
            // It used to be gated on `perShot > 1`, which meant a file that
            // looked correct printed NOTHING - so when the behaviour disagreed
            // with the file there was no line to compare against and no way to
            // tell "my value never landed" from "my value landed and the game
            // ignores it". A stat this easy to get wrong should state itself
            // every time.
            float interval = pw.fireRate > 0f ? 1f / pw.fireRate : 0f;

            Log.LogInfo(
                fileName + ": minion FIRING - " + perShot +
                " minion(s) per trigger pull (burstSize " + pw.burstSize +
                " x projectileCount " +
                Mathf.RoundToInt(pw.projectileCount) + "), one pull every " +
                interval.ToString("0.###") + "s (fireRate " +
                pw.fireRate.ToString("0.###") + " = shots per SECOND, so " +
                "lower is slower)" +
                (pw.burstSize > 1
                    ? ", burst staggered " + pw.burstDelay.ToString("0.##") +
                      "s apart"
                    : "") +
                (pw.angleVariance != 0f
                    ? ", each scattered +/-" +
                      pw.angleVariance.ToString("0.#") + " degrees"
                    : "") +
                ". If what you see in game disagrees with this line, the " +
                "value did not reach the game - say so and quote this line.");

            if (pw.fireRate <= 0f)
            {
                Log.LogWarning(
                    fileName + ": minion - \"fireRate\" is " +
                    pw.fireRate.ToString("0.###") + ", and the game computes " +
                    "the gap between shots as 1/fireRate. At zero or below " +
                    "that is a divide by zero, which fires EVERY FRAME. Use a " +
                    "positive number: 0.5 is one every two seconds.");
            }

            // HOW HEAVY IS THE THING BEING SPAWNED?
            //
            // The spawnable prefabs are nothing like the same weight, and their
            // names actively mislead: "Unit Cross Small Caps" is 173
            // GameObjects carrying TWENTY fixed turrets, and Enemy_CrossQueen
            // is 705 with sixty-four, while a drone is 25 with one gun.
            //
            // This check exists because the shipped "Swarm Gun" preset made
            // exactly that mistake - six of the 173-object unit at four a
            // second, which froze the game for a couple of seconds and read as
            // a room full of bosses. Nothing in the log said a word about it.
            WarnIfHeavy(
                pw.minionPrefab, cap > 0 ? cap : perShot * 4, fileName);

            if (pw.burstSize <= 1 && pw.burstDelay > 0f)
            {
                Log.LogWarning(
                    fileName + ": minion - \"burstDelay\" " +
                    pw.burstDelay.ToString("0.##") + " does NOTHING here: it " +
                    "is the gap between shots WITHIN a burst, and burstSize is " +
                    pw.burstSize + " so there is no second shot to delay. To " +
                    "slow the weapon down use \"fireRate\" (shots per second - " +
                    "0.5 is one every two seconds).");
            }

            if (cap > 0 && perShot >= cap)
            {
                Log.LogWarning(
                    fileName + ": minion - one trigger pull spawns " + perShot +
                    " but the cap is " + cap +
                    ", so a SINGLE click fills it. That is probably not what " +
                    "you meant: lower \"burstSize\" / \"projectileCount\", or " +
                    "raise \"cap\".");
            }

            // THE CAP IS ENTIRELY OURS. MinionSpawnerWeapon.FireSingle has no
            // count check of any kind - that logic lives only in the drone
            // GADGET - so without a cap here a spawner weapon is a firehose.
            // Which is exactly what "spawns like 12 drones" was.
            if (cap <= 0 && cfg.capMode != ForgeSwarm.CapNone)
            {
                Log.LogWarning(
                    fileName + ": minion - no \"cap\" is set, so this weapon " +
                    "can spawn WITHOUT LIMIT. The game gives spawner weapons no " +
                    "cap of its own (only the drone gadget has one), so a few " +
                    "seconds of held fire will fill the room. Set \"cap\", or " +
                    "\"capMode\": \"none\" to say you meant it.");
            }

            if (perLevel > 0)
            {
                // Levelling a spawner does nothing on its own - a weapon's level
                // drives FloatSeries stats and a spawner has none - so the module
                // has to be boostable for capPerLevel to ever fire.
                var minionModuleJson = root["module"] as JObject;
                bool alreadyAsked =
                    minionModuleJson != null &&
                    minionModuleJson["canBeBoosted"] != null;

                if (alreadyAsked)
                {
                    Log.LogInfo(
                        fileName + ": minion \"capPerLevel\" is set and the " +
                        "module block names \"canBeBoosted\" explicitly, so " +
                        "your value stands. If it is false, capPerLevel can " +
                        "never fire - the level will not move.");
                }
                else
                {
                    Log.LogInfo(
                        fileName + ": minion \"capPerLevel\" is set, so a " +
                        "BoosterCore will raise the swarm cap by " + perLevel +
                        " per level (boostability is switched on for you).");
                }
            }

            if (inheritGrid)
            {
                Log.LogInfo(
                    fileName + ": minion \"inheritGrid\" is on - each spawned " +
                    "minion gets a COPY of this weapon's other grid modules, " +
                    "the way a drone gadget's do. Nothing in the game does this " +
                    "for a spawner weapon, so it is all new. Note it multiplies: " +
                    "a cap of 10 means ten minions each carrying your grid.");
            }

            ForgeMinion.Register(weapon, cfg);

            Log.LogInfo(
                fileName + ": minion - " +
                (string.IsNullOrEmpty(cfg.weapon)
                    ? "keeping the drone's own weapon"
                    : ("spawned drones will carry '" + cfg.weapon +
                       "' instead of their stock weapon (resolved when the " +
                       "first one spawns)")) +
                ", side: " +
                (cfg.faction.Equals("owner", StringComparison.OrdinalIgnoreCase)
                    ? "YOURS (the summoner's faction)"
                    : (cfg.faction.Equals("keep",
                                          StringComparison.OrdinalIgnoreCase)
                        ? "the prefab's own - NOTE some spawnable prefabs are " +
                          "hostile and will fight you"
                        : cfg.faction)) +
                (cfg.cap > 0
                    ? ", cap " + cfg.cap +
                      (cfg.capPerLevel > 0
                          ? " (+" + cfg.capPerLevel + "/level)" : "") +
                      " on " + ForgeSwarm.CapWord(cfg.capMode)
                    : ", NO CAP") +
                (cfg.lifetime > 0f
                    ? ", expiring after " + cfg.lifetime.ToString("0.##") + "s"
                    : "") +
                (cfg.inheritGrid ? ", inheriting the grid" : "") +
                ".");
        }

        // Colour and size for a spawned minion. Shared by both paths - the
        // spawner weapon and the drone gadget - because the Config is shared.
        private static void ReadMinionLook(
            ForgeMinion.Config cfg,
            string colorText,
            string colorModeText,
            float scale,
            string scaleModeText,
            string fileName)
        {
            cfg.colorMode = ForgeMinionLook.ParseColorMode(colorModeText);
            cfg.scaleMode = ForgeMinionLook.ParseScaleMode(scaleModeText);
            cfg.scale = Mathf.Clamp(scale <= 0f ? 1f : scale, 0.1f, 8f);

            if (!string.IsNullOrEmpty(colorText))
            {
                Color c = cfg.color;

                if (ReadColor(colorText, "minion \"color\"", fileName, ref c))
                {
                    cfg.color = c;
                    cfg.hasColor = true;
                }
            }

            if (cfg.hasColor)
            {
                Log.LogInfo(
                    fileName + ": minion colour " +
                    ForgeMinionLook.ColorWord(cfg.colorMode) +
                    " - applied to the prefab's \"Visual\" subtree only, so " +
                    "the AI's own state-indicator sprites (Idle / Aggro / Seek " +
                    "/ Regroup) keep their colours.");
            }

            if (cfg.scale != 1f)
            {
                Log.LogInfo(
                    fileName + ": minion scale x" +
                    cfg.scale.ToString("0.##") + " - " +
                    ForgeMinionLook.ScaleWord(cfg.scaleMode) +
                    (cfg.scaleMode == ForgeMinionLook.ScaleFull
                        ? " (mass by scale SQUARED, since a minion twice as " +
                          "wide covers four times the area)"
                        : "") + ".");
            }
        }

        // Everything about what a minion IS, rather than what it carries.
        //
        // Takes the whole JObject rather than loose parameters, deliberately.
        // The look reader takes five separate arguments and that is exactly why
        // the cap, capMode, capPerLevel and inheritGrid keys were never wired to
        // the drone path - every new field had to be threaded through two call
        // sites by hand, and one of them got missed. One object, two call sites,
        // no drift.
        private static void ReadMinionStats(
            ForgeMinion.Config cfg,
            JObject o,
            string fileName)
        {
            if (cfg == null || o == null)
                return;

            cfg.statMode = ForgeMinionStats.ParseMode((string)o["statMode"]);

            cfg.health = Tweak(o, "health", fileName);
            cfg.speed = Tweak(o, "speed", fileName);
            cfg.turnSpeed = Tweak(o, "turnSpeed", fileName);
            cfg.sight = Tweak(o, "sight", fileName);
            cfg.attackRange = Tweak(o, "attackRange", fileName);
            cfg.leash = Tweak(o, "leash", fileName);
            cfg.followDistance = Tweak(o, "followDistance", fileName);
            cfg.iFrames = Tweak(o, "iFrames", fileName);
            cfg.fireproof = Tweak(o, "fireproof", fileName);

            JToken invisible = o["untargetable"];

            if (invisible != null && invisible.Type != JTokenType.Null)
            {
                cfg.untargetable = (bool)invisible;
                cfg.hasUntargetable = true;
            }

            cfg.resources = Pools(o["resources"], "resources", fileName);
            cfg.regen = Pools(o["regen"], "regen", fileName);

            var shield = o["shield"] as JObject;

            if (shield != null)
            {
                string resName = (string)shield["resource"];

                var res =
                    string.IsNullOrEmpty(resName)
                        ? null
                        : JsonFieldMapper.FindAsset(typeof(Resource), resName)
                            as Resource;

                if (res == null)
                {
                    Log.LogWarning(
                        fileName + ": minion \"shield\" needs a \"resource\" " +
                        "naming a resource this game has" +
                        (string.IsNullOrEmpty(resName)
                            ? "."
                            : " - '" + resName + "' is not one."));
                }
                else
                {
                    cfg.shieldResource = res;
                    cfg.shieldEffectiveness =
                        (float?)shield["effectiveness"] ?? 1f;
                    cfg.hasShield = true;

                    bool paired =
                        cfg.resources != null &&
                        cfg.resources.ContainsKey(res);

                    if (!paired)
                    {
                        Log.LogWarning(
                            fileName + ": minion \"shield\" uses " + res.name +
                            " but no " + res.name + " pool was given, so the " +
                            "shield has nothing to spend and will do nothing. " +
                            "Add it under \"resources\".");
                    }
                }
            }

            // Three of the spawner-weapon prefabs burn a resource and die when
            // it runs out, in as little as five seconds - so a lifetime longer
            // than that would silently never happen. Scaling their fuel is
            // opt-out rather than opt-in because a key that quietly does
            // nothing is worse than one that does slightly more than you
            // expected.
            if (cfg.lifetime > 0f)
                cfg.fuelForLifetime = (bool?)o["extendFuel"] ?? true;

            cfg.hurtsOwner = (bool?)o["hurtsOwner"] ?? true;
            cfg.hurtsAllies = (bool?)o["hurtsAllies"] ?? true;

            if (!cfg.HasStats)
                return;

            Log.LogInfo(
                fileName + ": minion stats (" +
                ForgeMinionStats.ModeWord(cfg.statMode) + ") - " +
                Listed(cfg) + ". Health and resource pools are applied as " +
                "MODULE EFFECTS, not as direct writes: a unit's health in this " +
                "game IS a resource tank, and its capacity is rebuilt from " +
                "module effects every time stats recalculate, so a direct " +
                "write would be erased within a frame.");

            if (!cfg.hurtsOwner || !cfg.hurtsAllies)
            {
                Log.LogInfo(
                    fileName + ": minion blast safety - " +
                    (!cfg.hurtsOwner ? "you are" : "") +
                    (!cfg.hurtsOwner && !cfg.hurtsAllies ? " and " : "") +
                    (!cfg.hurtsAllies ? "its allies are" : "") +
                    " immune to its explosions. The game itself has no owner " +
                    "filter on explosions at all, so this is a filter of ours.");
            }
        }

        // Is the thing being spawned enormous?
        //
        // The spawnable prefabs are nothing like the same weight, and their
        // names actively mislead: "Unit Cross Small Caps" is 173 GameObjects
        // carrying TWENTY fixed turrets, and Enemy_CrossQueen is 705 with
        // sixty-four, while a drone is 25 with one gun.
        //
        // This exists because the shipped "Swarm Gun" preset made exactly that
        // mistake - six of the 173-object unit at four a second, which froze
        // the game for a moment and read as a room full of bosses. Nothing in
        // the log said a word about it.
        private static void WarnIfHeavy(
            Unit prefab, int atOnce, string fileName)
        {
            if (prefab == null)
                return;

            try
            {
                int objs =
                    prefab.GetComponentsInChildren<Transform>(true).Length;

                int turrets =
                    prefab.GetComponentsInChildren<StaticWeaponHolder>(true)
                        .Length;

                if (objs < 100 && turrets < 5)
                    return;

                Log.LogWarning(
                    fileName + ": minion - '" + prefab.name +
                    "' is a HEAVY prefab (" + objs + " objects" +
                    (turrets > 0 ? ", " + turrets + " fixed turrets" : "") +
                    "). A drone is about 25. Spawning " +
                    Mathf.Max(1, atOnce) + " of these will stall the game for " +
                    "a moment and look like a room full of bosses rather than " +
                    "a swarm - the prefab names mislead here, several " +
                    "\"Small\" units are among the biggest in the game. " +
                    "Either keep it to 1-2 as an escort, or pick a light " +
                    "prefab (Enemy_Cross is 16 objects, the drones are 25).");
            }
            catch (Exception e)
            {
                // A warning must never be the thing that breaks a build.
                Log.LogInfo(
                    fileName + ": could not weigh the minion prefab (" +
                    e.Message + "). Harmless.");
            }
        }

        // "slow" (a debuff on what you hit) and "timeSlow" (bending time for
        // everyone). Separate keys, one config, because a weapon commonly wants
        // both and they share their triggers.
        private static void ApplySlow(
            WeaponData weapon, JObject root, string fileName)
        {
            var slowJson = root["slow"] as JObject;
            var timeJson = root["timeSlow"] as JObject;

            if (slowJson == null && timeJson == null)
                return;

            var cfg = new ForgeSlow.Config();

            if (slowJson != null)
            {
                cfg.hasSlow = (bool?)slowJson["enabled"] ?? true;
                cfg.amount = Mathf.Clamp(
                    (float?)slowJson["amount"] ?? 0.5f, 0.01f, 1f);
                cfg.duration = Mathf.Max(
                    0.01f, (float?)slowJson["duration"] ?? 2f);
                cfg.mode = ForgeSlow.ParseMode((string)slowJson["mode"]);
                cfg.cooldown = Mathf.Max(
                    0f, (float?)slowJson["cooldown"] ?? 0f);
                cfg.maxStacks = Mathf.Max(
                    1, (int?)slowJson["maxStacks"] ?? 3);
                cfg.floor = Mathf.Clamp(
                    (float?)slowJson["floor"] ?? 0.2f, 0.01f, 1f);

                cfg.slowMove = (bool?)slowJson["move"] ?? true;
                cfg.slowTurn = (bool?)slowJson["turnSpeed"] ?? true;
                cfg.slowFire = (bool?)slowJson["fireRate"] ?? true;
                cfg.slowKnockback = (bool?)slowJson["knockback"] ?? true;

                string tint = (string)slowJson["tint"];

                if (!string.IsNullOrEmpty(tint))
                {
                    Color c = Color.white;

                    if (ReadColor(tint, "slow \"tint\"", fileName, ref c))
                    {
                        cfg.tint = c;
                        cfg.hasTint = true;
                    }
                }

                if (cfg.amount >= 1f)
                {
                    Log.LogWarning(
                        fileName + ": \"slow\" amount is " +
                        cfg.amount.ToString("0.##") + ", which is no slow at " +
                        "all - 1 means full speed. Use 0.5 for half speed.");
                }

                if (!cfg.slowMove && !cfg.slowTurn && !cfg.slowFire &&
                    !cfg.slowKnockback)
                {
                    Log.LogWarning(
                        fileName + ": \"slow\" has every effect switched off, " +
                        "so it does nothing. Turn one of \"move\", " +
                        "\"turnSpeed\", \"fireRate\" or \"knockback\" back on.");
                    cfg.hasSlow = false;
                }
            }

            if (timeJson != null)
            {
                cfg.hasTimeSlow = (bool?)timeJson["enabled"] ?? true;
                cfg.timeScale = Mathf.Clamp(
                    (float?)timeJson["scale"] ?? 0.35f, 0.01f, 1f);
                cfg.timeDuration = (float?)timeJson["duration"] ?? 0.25f;
                cfg.timeCooldown = Mathf.Max(
                    0f, (float?)timeJson["cooldown"] ?? 0f);
                cfg.timeMode = ForgeSlow.ParseMode((string)timeJson["mode"]);
                cfg.timeEase = (bool?)timeJson["ease"] ?? true;
                cfg.timeUnscaled = (bool?)timeJson["realSeconds"] ?? true;

                // DURATION 0 IS NOT "INSTANT", IT IS FOREVER. The game treats a
                // non-positive duration as an indefinite modifier and never
                // evaluates it for expiry, so the whole game would sit in slow
                // motion for the rest of the run with nothing to clear it.
                if (cfg.timeDuration <= 0f)
                {
                    Log.LogWarning(
                        fileName + ": \"timeSlow\" duration was " +
                        cfg.timeDuration.ToString("0.##") +
                        ". The game reads a duration of zero or less as " +
                        "PERMANENT - the whole game would stay in slow motion " +
                        "for the rest of the run. Set to 0.25s.");

                    cfg.timeDuration = 0.25f;
                }

                if (cfg.timeScale >= 1f)
                {
                    Log.LogWarning(
                        fileName + ": \"timeSlow\" scale is " +
                        cfg.timeScale.ToString("0.##") + ", which is normal " +
                        "speed. Lower is slower - 0.35 is a good punch.");
                }

                // With no cooldown, a weapon that hits more often than the slow
                // lasts holds the game in slow motion for as long as you keep
                // firing. That IS a legitimate effect - it ends when you stop -
                // but it should be a choice rather than a surprise.
                float gap = weapon.fireRate > 0f ? 1f / weapon.fireRate : 0f;

                if (cfg.timeCooldown <= 0f && gap > 0f &&
                    cfg.timeDuration >= gap)
                {
                    Log.LogWarning(
                        fileName + ": \"timeSlow\" lasts " +
                        cfg.timeDuration.ToString("0.##") +
                        "s but this weapon fires every " +
                        gap.ToString("0.##") + "s, so holding the trigger " +
                        "keeps the game in slow motion continuously (it ends " +
                        "when you stop firing). Set \"cooldown\" if you wanted " +
                        "an occasional punch instead.");
                }
            }

            cfg.onHit = Trigger(slowJson, timeJson, "onHit", true);
            cfg.onKill = Trigger(slowJson, timeJson, "onKill", false);

            if (!cfg.onHit && !cfg.onKill)
            {
                Log.LogWarning(
                    fileName + ": \"slow\" / \"timeSlow\" have both triggers " +
                    "off, so neither can ever fire.");
                return;
            }

            if (!cfg.hasSlow && !cfg.hasTimeSlow)
                return;

            ForgeSlow.Register(weapon, cfg);

            Log.LogInfo(
                fileName + ": " +
                (cfg.hasSlow
                    ? "slow x" + cfg.amount.ToString("0.##") + " for " +
                      cfg.duration.ToString("0.##") + "s (" +
                      ForgeSlow.ModeWord(cfg.mode) + ")" + Effects(cfg)
                    : "") +
                (cfg.hasSlow && cfg.hasTimeSlow ? ", and " : "") +
                (cfg.hasTimeSlow
                    ? "time to x" + cfg.timeScale.ToString("0.##") + " for " +
                      cfg.timeDuration.ToString("0.##") + "s" +
                      (cfg.timeEase ? " easing back" : " then snapping back")
                    : "") +
                " on " + (cfg.onHit ? "hit" : "") +
                (cfg.onHit && cfg.onKill ? " and " : "") +
                (cfg.onKill ? "kill" : "") + ".");
        }

        // Either block may carry the trigger, so a file that only uses one of
        // them does not have to know which one owns it.
        private static bool Trigger(
            JObject a, JObject b, string key, bool fallback)
        {
            if (a != null && a[key] != null)
                return (bool)a[key];

            if (b != null && b[key] != null)
                return (bool)b[key];

            return fallback;
        }

        private static string Effects(ForgeSlow.Config cfg)
        {
            var on = new List<string>();

            if (cfg.slowMove) on.Add("movement");
            if (cfg.slowTurn) on.Add("turning");
            if (cfg.slowFire) on.Add("fire rate");
            if (cfg.slowKnockback) on.Add("knockback resistance");

            return " - " + string.Join(", ", on.ToArray());
        }

        // "overheat" - sustained fire builds heat, too much heat stops the gun.
        private static void ApplyOverheat(
            WeaponData weapon, JObject root, string slot, string fileName)
        {
            var o = root["overheat"] as JObject;

            if (o == null)
                return;

            var cfg = new ForgeOverheat.Config();

            cfg.enabled = (bool?)o["enabled"] ?? true;

            if (!cfg.enabled)
                return;

            // ONLY THE SHOOTER PATH CAN BE JAMMED. A gadget calls
            // weapon.Fire(...) directly and never touches a Shooter, so heat
            // would build with nothing able to act on it - a feature that looks
            // configured and does nothing at all.
            bool isGadget =
                !string.IsNullOrEmpty(slot) &&
                slot.Trim().ToLowerInvariant().StartsWith("gadget");

            if (isGadget)
            {
                Log.LogWarning(
                    fileName + ": \"overheat\" only works on a PRIMARY or " +
                    "SECONDARY weapon. Gadgets fire without going through the " +
                    "Shooter, which is the thing overheat blocks - so heat " +
                    "would build and never be able to stop anything. Ignored.");
                return;
            }

            cfg.max = Mathf.Max(1f, (float?)o["max"] ?? 100f);
            cfg.perShot = Mathf.Max(0f, (float?)o["perShot"] ?? 8f);
            cfg.perSecond = Mathf.Max(0f, (float?)o["perSecond"] ?? 0f);
            cfg.coolPerSecond =
                Mathf.Max(0f, (float?)o["coolPerSecond"] ?? 35f);
            cfg.coolDelay = Mathf.Max(0f, (float?)o["coolDelay"] ?? 0.4f);

            cfg.response = ForgeOverheat.ParseResponse((string)o["response"]);
            cfg.jamSeconds = Mathf.Max(0.05f, (float?)o["jamSeconds"] ?? 1.5f);
            cfg.ventTo = Mathf.Max(0f, (float?)o["ventTo"] ?? 0f);
            cfg.slowestFireRate =
                Mathf.Clamp((float?)o["slowestFireRate"] ?? 0.3f, 0.05f, 1f);

            cfg.selfDamage = Mathf.Max(0f, (float?)o["selfDamage"] ?? 0f);
            cfg.ventShot = (bool?)o["ventShot"] ?? false;
            cfg.ventShotLevel =
                Mathf.Clamp01((float?)o["ventShotLevel"] ?? 1f);
            cfg.replacesAmmo = (bool?)o["replacesAmmo"] ?? false;

            cfg.tintMuzzle = (bool?)o["tintMuzzle"] ?? true;
            cfg.overheatSfx = (string)o["overheatSfx"] ?? "";
            cfg.recoverSfx = (string)o["recoverSfx"] ?? "";
            cfg.showHeat = (bool?)o["showHeat"] ?? false;
            cfg.shake = (bool?)o["shake"] ?? false;

            string hot = (string)o["hotColor"];

            if (!string.IsNullOrEmpty(hot))
            {
                Color c = cfg.hotColor;

                if (ReadColor(hot, "overheat \"hotColor\"", fileName, ref c))
                    cfg.hotColor = c;
            }

            if (cfg.replacesAmmo && weapon.cost != 0f)
            {
                Log.LogInfo(
                    fileName + ": overheat \"replacesAmmo\" - the weapon's " +
                    "cost of " + weapon.cost.ToString("0.##") +
                    " was set to 0, so heat is now its only limit.");

                weapon.cost = 0f;
            }

            // A vent shot scales by the CHARGE block's multipliers, because
            // that is the set of multipliers this mod already has. Without one
            // there is nothing to scale by, so say it at load rather than
            // letting the player wonder why the payoff never comes.
            if (cfg.ventShot && root["charge"] == null)
            {
                Log.LogWarning(
                    fileName + ": overheat \"ventShot\" needs a \"charge\" " +
                    "block on the same weapon - that block IS the set of " +
                    "multipliers the vent shot scales by (damage, size, speed, " +
                    "explosion and the rest). Add one, or the blowout will " +
                    "fire an ordinary shot.");
            }

            ForgeOverheat.Register(weapon, cfg);

            // THE ARITHMETIC, SAID OUT LOUD - the same lesson as the minion
            // firing line. A heat model is three numbers that only mean
            // something together, and "it never overheats" / "it never cools"
            // are both silent failures otherwise.
            float perSecondFiring =
                cfg.perShot * weapon.fireRate * Mathf.Max(1, weapon.burstSize) +
                cfg.perSecond;

            string timing;

            if (perSecondFiring <= 0f)
            {
                timing =
                    "it gains NO heat at all (perShot and perSecond are both " +
                    "0), so it can never overheat";
            }
            else if (perSecondFiring <= cfg.coolPerSecond && cfg.coolDelay <= 0f)
            {
                timing =
                    "it cools at least as fast as it heats (" +
                    cfg.coolPerSecond.ToString("0.#") + "/s against " +
                    perSecondFiring.ToString("0.#") + "/s) with no cool delay, " +
                    "so it can NEVER overheat";
            }
            else
            {
                timing =
                    "sustained fire overheats it after about " +
                    (cfg.max / perSecondFiring).ToString("0.##") + "s (" +
                    perSecondFiring.ToString("0.#") + " heat/s against a max " +
                    "of " + cfg.max.ToString("0.#") + "), and a full cooldown " +
                    "takes about " +
                    (cfg.coolPerSecond > 0f
                        ? (cfg.max / cfg.coolPerSecond).ToString("0.##") + "s"
                        : "FOREVER - coolPerSecond is 0");
            }

            Log.LogInfo(
                fileName + ": overheat - " +
                ForgeOverheat.ResponseWord(cfg.response) + ". " + timing + "." +
                (cfg.ventShot ? " Blowout fires a charged shot." : "") +
                (cfg.selfDamage > 0f
                    ? " Blowout costs you " +
                      cfg.selfDamage.ToString("0.#") + " health."
                    : ""));

            if (cfg.coolPerSecond <= 0f &&
                cfg.response != ForgeOverheat.Jam)
            {
                Log.LogWarning(
                    fileName + ": overheat \"coolPerSecond\" is 0 and the " +
                    "response is not \"jam\", so once this weapon overheats it " +
                    "is locked FOREVER - nothing brings the heat back down. " +
                    "Either give it a cooling rate or use \"response\": " +
                    "\"jam\", which unlocks on a timer instead.");
            }
        }

        // "rally" - reflect the same shot over and over, buffing it each time.
        //
        // Attaches to whichever reflector the file already has, because the
        // rally is a property of the CATCH rather than of any one feature, and
        // all three reflectors share ForgeBulletTurn.
        private static void ApplyRally(
            WeaponData weapon, JObject root, string fileName)
        {
            var o = root["rally"] as JObject;

            if (o == null)
                return;

            var cfg = new ForgeRally.Config();

            cfg.enabled = (bool?)o["enabled"] ?? true;

            if (!cfg.enabled)
                return;

            cfg.maxStacks = Mathf.Max(0, (int?)o["maxStacks"] ?? 0);
            cfg.atCap = ForgeRally.ParseCap((string)o["atCap"]);

            cfg.damagePerReflect =
                Mathf.Max(0f, (float?)o["damagePerReflect"] ?? 1.6f);
            cfg.speedPerReflect =
                Mathf.Max(0f, (float?)o["speedPerReflect"] ?? 1f);
            cfg.sizePerReflect =
                Mathf.Max(0f, (float?)o["sizePerReflect"] ?? 1f);
            cfg.piercePerReflect =
                Mathf.Max(0, (int?)o["piercePerReflect"] ?? 0);
            cfg.lifePerReflect =
                Mathf.Max(1f, (float?)o["lifePerReflect"] ?? 1.5f);

            cfg.bounce = (bool?)o["bounce"] ?? false;
            cfg.hostile = ForgeRally.ParseHostile((string)o["hostile"]);
            cfg.hostileAfter = Mathf.Max(1, (int?)o["hostileAfter"] ?? 3);

            cfg.sfx = (string)o["sfx"] ?? "";
            cfg.tint = (bool?)o["tint"] ?? false;

            string hot = (string)o["hotColor"];

            if (!string.IsNullOrEmpty(hot))
            {
                Color c = cfg.hotColor;

                if (ReadColor(hot, "rally \"hotColor\"", fileName, ref c))
                    cfg.hotColor = c;
            }

            // Which reflector is this rally for?
            int attached = 0;

            ForgeShield.Config shield;

            if (ForgeShield.TryGet(weapon, out shield) && shield != null)
            {
                shield.rally = cfg;
                attached++;
            }

            ForgeCatalyst.Config catalyst;

            if (ForgeCatalyst.TryGet(weapon, out catalyst) && catalyst != null)
            {
                catalyst.rally = cfg;
                attached++;
            }

            if (attached == 0)
            {
                Log.LogWarning(
                    fileName + ": \"rally\" needs something that CATCHES " +
                    "bullets to hang off - add a \"shield\" block (with " +
                    "\"onBlock\": \"reflect\") or a \"catalyst\" block. On its " +
                    "own it does nothing, because there is nothing to reflect " +
                    "with.");
                return;
            }

            // Deflect is deliberately not supported, and it is worth being
            // explicit rather than silently ignoring it: ForgeDeflect is a
            // component on the PROJECTILE PREFAB, and a plain config-class
            // reference on a prefab-attached component arrives NULL on every
            // clone. That exact trap silently disabled the whole of gas and
            // catalyst once. Supporting it properly means the ForgeConfigTable
            // int-id route, which is a separate change.
            if (root["deflect"] != null)
            {
                Log.LogWarning(
                    fileName + ": \"rally\" does not apply to \"deflect\" yet " +
                    "- only to \"shield\" and \"catalyst\". A deflector's " +
                    "settings live on the projectile prefab, which cannot " +
                    "carry a config reference safely. Your deflect still " +
                    "reflects normally, it just will not stack.");
            }

            // A reflect that cannot be caught again is not a rally, so say so
            // before it is tested rather than after.
            if (shield != null &&
                shield.onBlock != ForgeShield.OnBlock.Reflect &&
                shield.onBlock != ForgeShield.OnBlock.ReflectAtNearest &&
                shield.onBlock != ForgeShield.OnBlock.ReflectAtCursor &&
                shield.onBlock != ForgeShield.OnBlock.ReflectMirror)
            {
                Log.LogWarning(
                    fileName + ": \"rally\" needs the shield's \"onBlock\" to " +
                    "be \"reflect\" or \"reflectAtNearest\" - it is \"" +
                    shield.onBlock + "\", which destroys or absorbs the shot, " +
                    "so there is never anything to rally.");
            }

            // Catching your own shots back needs the player-side tracker,
            // which is off by default because almost nothing needs it.
            ForgeProjectileTracker.TrackPlayer = true;

            string growth;

            if (cfg.maxStacks > 0)
            {
                growth =
                    "capped at " + cfg.maxStacks + " reflects (x" +
                    Mathf.Pow(cfg.damagePerReflect, cfg.maxStacks)
                        .ToString("0.#") + " damage at full stacks)" +
                    (cfg.atCap == ForgeRally.CapDetonate
                        ? ", then it detonates"
                        : ", then it stops growing");
            }
            else
            {
                growth =
                    "NO CAP - x" + cfg.damagePerReflect.ToString("0.##") +
                    " damage per reflect, compounding forever (5 reflects is " +
                    "already x" +
                    Mathf.Pow(cfg.damagePerReflect, 5).ToString("0.#") +
                    ", 10 is x" +
                    Mathf.Pow(cfg.damagePerReflect, 10).ToString("0.#") +
                    "). Set \"maxStacks\" if that is not what you meant";
            }

            Log.LogInfo(
                fileName + ": rally on " + attached + " reflector(s) - " +
                growth + ". Reflected shots stay catchable by your own " +
                "shield, so get in front of one to hit it again" +
                (cfg.bounce ? ", and they bounce off terrain to come back" : "") +
                (cfg.hostile != ForgeRally.HostileNever
                    ? ". WARNING: rallied shots can hurt YOU"
                    : "") + ".");

            if (cfg.hostile == ForgeRally.HostileAlways)
            {
                Log.LogWarning(
                    fileName + ": rally \"hostile\": \"always\" means the shot " +
                    "is on the ENEMY side for its whole life - which means it " +
                    "cannot damage enemies at all. It is a hot potato, not a " +
                    "weapon. \"after\" is usually what people want.");
            }
        }

        // Does the minion block ask for anything in the stats family? Used only
        // by the "is this worth registering" gates, and spelled out as a list
        // rather than inferred, so adding a key is one obvious edit and a
        // forgotten one is a grep away rather than a silent discard.
        private static bool MentionsStats(JObject o)
        {
            if (o == null)
                return false;

            string[] keys =
            {
                "statMode", "health", "healthPerLevel", "speed", "turnSpeed",
                "sight", "attackRange", "leash", "followDistance", "iFrames",
                "fireproof", "untargetable", "resources", "regen", "shield",
                "extendFuel", "hurtsOwner", "hurtsAllies",
                "weaponStats", "explosion", "lightning", "burn"
            };

            foreach (string key in keys)
            {
                if (o[key] != null)
                    return true;
            }

            return false;
        }

        // "health": 60 with an optional "healthPerLevel": 10. The `set` flag is
        // what distinguishes absent from zero, and zero is meaningful for
        // several of these.
        private static ForgeMinionStats.Tweak Tweak(
            JObject o, string key, string fileName)
        {
            var tweak = new ForgeMinionStats.Tweak();
            JToken t = o[key];

            if (t == null || t.Type == JTokenType.Null)
                return tweak;

            tweak.value = (float)t;
            tweak.set = true;
            tweak.perLevel = (float?)o[key + "PerLevel"] ?? 0f;

            if (tweak.perLevel != 0f && key != "health")
            {
                Log.LogWarning(
                    fileName + ": minion \"" + key + "PerLevel\" - only " +
                    "\"healthPerLevel\" is wired to the module level so far, " +
                    "because health is the only one of these the game itself " +
                    "stores as a level series. Ignored.");

                tweak.perLevel = 0f;
            }

            return tweak;
        }

        // { "Resource White": 20, "Resource Caps": 5 }
        private static Dictionary<Resource, float> Pools(
            JToken token, string what, string fileName)
        {
            var o = token as JObject;

            if (o == null)
                return null;

            Dictionary<Resource, float> found = null;

            foreach (KeyValuePair<string, JToken> pair in o)
            {
                var res =
                    JsonFieldMapper.FindAsset(typeof(Resource), pair.Key)
                        as Resource;

                if (res == null)
                {
                    Log.LogWarning(
                        fileName + ": minion \"" + what + "\" - '" + pair.Key +
                        "' is not a resource this game has. Skipped. Try " +
                        "\"Resource White\", \"Resource Caps\", " +
                        "\"Resource Health\", \"Resource Electron\", " +
                        "\"Resource Purple\" or \"Resource Tech\".");
                    continue;
                }

                if (pair.Value == null || pair.Value.Type == JTokenType.Null)
                    continue;

                if (found == null)
                    found = new Dictionary<Resource, float>();

                found[res] = (float)pair.Value;
            }

            return found;
        }

        // The player-facing one-liner for a drone gadget's card. Deliberately
        // short and only mentions what was actually set - a card is not a log.
        private static string MinionBlurb(ForgeMinion.Config cfg)
        {
            var parts = new List<string>();
            string x = cfg.statMode == ForgeMinionStats.Multiply ? "x" : "";

            if (cfg.health.set)
                parts.Add("Health " + x + cfg.health.value.ToString("0.#"));

            if (cfg.speed.set)
                parts.Add("Speed " + x + cfg.speed.value.ToString("0.#"));

            if (cfg.sight.set)
                parts.Add("Sight " + x + cfg.sight.value.ToString("0.#"));

            if (cfg.lifetime > 0f)
                parts.Add("Lasts " + cfg.lifetime.ToString("0.#") + "s");

            if (cfg.hasShield)
                parts.Add("Shielded");

            if (cfg.untargetable)
                parts.Add("Untargetable");

            if (cfg.defaultWeaponModule != null &&
                cfg.defaultWeaponModule.weapon != null)
            {
                WeaponData gun = cfg.defaultWeaponModule.weapon;

                if (gun.explosion.radius > 0f && gun.explosion.damages != null &&
                    gun.explosion.damages.Count > 0)
                {
                    parts.Add("Explosive shots");
                }

                if (gun.discharge.chainLength > 0)
                    parts.Add("Chain lightning");

                if (gun.burn.Max > 0f)
                    parts.Add("Incendiary");
            }

            if (parts.Count == 0)
                return "";

            return "Minions: " + string.Join(", ", parts) + ".";
        }

        private static string Listed(ForgeMinion.Config cfg)
        {
            var parts = new List<string>();

            if (cfg.health.set) parts.Add("health " + cfg.health.value);
            if (cfg.speed.set) parts.Add("speed " + cfg.speed.value);
            if (cfg.turnSpeed.set) parts.Add("turnSpeed " + cfg.turnSpeed.value);
            if (cfg.sight.set) parts.Add("sight " + cfg.sight.value);

            if (cfg.attackRange.set)
                parts.Add("attackRange " + cfg.attackRange.value);

            if (cfg.leash.set) parts.Add("leash " + cfg.leash.value);

            if (cfg.followDistance.set)
                parts.Add("followDistance " + cfg.followDistance.value);

            if (cfg.iFrames.set) parts.Add("iFrames " + cfg.iFrames.value);

            if (cfg.fireproof.set)
                parts.Add("fireproof " + cfg.fireproof.value);

            if (cfg.hasUntargetable)
                parts.Add("untargetable " + cfg.untargetable);

            if (cfg.resources != null)
                parts.Add(cfg.resources.Count + " resource pool(s)");

            if (cfg.regen != null)
                parts.Add(cfg.regen.Count + " regen rate(s)");

            if (cfg.hasShield) parts.Add("a shield");
            if (cfg.fuelForLifetime) parts.Add("fuel scaled to the lifetime");

            return parts.Count == 0 ? "nothing" : string.Join(", ", parts);
        }

        private static void ApplyTractor(
            WeaponData weapon,
            JObject root,
            string fileName)
        {
            JToken token = root["tractor"];

            if (token == null || token.Type == JTokenType.Null)
                return;

            var cfg = new ForgeTractor.Config();
            var o = token as JObject;

            if (o != null)
            {
                if ((bool?)o["enabled"] == false)
                    return;

                string mode = ((string)o["mode"] ?? "pull")
                    .Trim().ToLowerInvariant();

                switch (mode)
                {
                    case "push":
                        cfg.mode = ForgeTractor.Mode.Push;
                        break;
                    case "hold":
                        cfg.mode = ForgeTractor.Mode.Hold;
                        break;
                    case "grapple":
                        cfg.mode = ForgeTractor.Mode.Grapple;
                        break;
                    case "pull":
                        cfg.mode = ForgeTractor.Mode.Pull;
                        break;
                    default:
                        Log.LogWarning(
                            fileName + ": tractor \"mode\": '" + mode +
                            "' is not recognised - use \"pull\", \"push\", " +
                            "\"hold\" or \"grapple\". Defaulting to pull.");
                        break;
                }

                cfg.range = Mathf.Max(1f, (float?)o["range"] ?? 14f);
                cfg.cone = Mathf.Clamp((float?)o["cone"] ?? 0f, 0f, 180f);
                cfg.beamWidth = Mathf.Max(0.05f, (float?)o["beamWidth"] ?? 0.6f);
                cfg.force = Mathf.Max(0f, (float?)o["force"] ?? 300f);
                cfg.holdDistance =
                    Mathf.Max(0.5f, (float?)o["holdDistance"] ?? 6f);
                cfg.maxSpeed = Mathf.Max(0.5f, (float?)o["maxSpeed"] ?? 26f);
                cfg.maxTargets = Mathf.Max(0, (int?)o["maxTargets"] ?? 3);
                cfg.drainPerSecond =
                    Mathf.Max(0f, (float?)o["drainPerSecond"] ?? 2f);
                cfg.affectsPlayer = (bool?)o["affectsPlayer"] ?? false;

                cfg.grappleToUnits = (bool?)o["grappleToUnits"] ?? false;
                cfg.grappleSlamsYou = (bool?)o["grappleSlamsYou"] ?? false;
                cfg.grappleStopDistance =
                    Mathf.Max(0f, (float?)o["grappleStopDistance"] ?? 2f);

                cfg.slam = (bool?)o["slam"] ?? true;
                cfg.slamMinSpeed =
                    Mathf.Max(0f, (float?)o["slamMinSpeed"] ?? 10f);
                cfg.slamDamagePerSpeed =
                    Mathf.Max(0f, (float?)o["slamDamagePerSpeed"] ?? 1.5f);
                cfg.slamMaxDamage =
                    Mathf.Max(0f, (float?)o["slamMaxDamage"] ?? 999f);
                cfg.slamWindow =
                    Mathf.Max(0.1f, (float?)o["slamWindow"] ?? 1.5f);
                cfg.slamHitsUnits = (bool?)o["slamHitsUnits"] ?? true;
                cfg.slamHitsPlayer = (bool?)o["slamHitsPlayer"] ?? false;

                cfg.drawBeam = (bool?)o["drawBeam"] ?? true;

                Color c = cfg.color;

                if (ReadColor((string)o["color"], "tractor \"color\"",
                              fileName, ref c))
                {
                    cfg.color = c;
                }

                cfg.sfxGuid = ResolveChargeSfx(
                    (string)o["sfx"], weapon.shootSfx, false,
                    "tractor sfx", fileName);
            }

            // NOTE, and it cannot be checked from here: a tractor beam must go
            // in a NORMAL weapon slot. Gadgets bypass Shooter entirely, and
            // Shooter's held trigger is the only thing that drives the beam - so
            // one placed in a gadget slot simply never runs. The compiler proves
            // the check is impossible at this level (a gadget *holds* a
            // WeaponData rather than being one), so it is documented in the
            // builder page and the txt instead of warned about here.
            ForgeTractor.Register(weapon, cfg);

            Log.LogInfo(
                fileName + ": tractor - " + cfg.mode + " beam, range " +
                cfg.range.ToString("0.#") + ", " +
                (cfg.IsLine
                    ? ("a LINE " + cfg.beamWidth.ToString("0.##") + " wide")
                    : (cfg.cone.ToString("0.#") + " deg cone")) +
                ", force " + cfg.force.ToString("0.#") +
                (cfg.mode == ForgeTractor.Mode.Hold
                    ? (" holding at " + cfg.holdDistance.ToString("0.#"))
                    : "") +
                (cfg.mode == ForgeTractor.Mode.Grapple
                    ? (" reeling YOU in, anchoring on " +
                       (cfg.grappleToUnits ? "terrain and units" : "terrain") +
                       ", stopping at " +
                       cfg.grappleStopDistance.ToString("0.#") +
                       (cfg.grappleSlamsYou
                           ? ", AND IT CAN HURT YOU on arrival" : ""))
                    : "") +
                ", " + (cfg.maxTargets > 0
                    ? (cfg.maxTargets + " target(s)") : "unlimited targets") +
                ", " + cfg.drainPerSecond.ToString("0.##") + "/sec" +
                (cfg.slam
                    ? (", SLAM above " + cfg.slamMinSpeed.ToString("0.#") +
                       " for x" + cfg.slamDamagePerSpeed.ToString("0.##") +
                       (cfg.slamHitsUnits ? ", unit-to-unit" : "") +
                       (cfg.slamHitsPlayer ? ", hurts you too" : ""))
                    : ", no slam") +
                ".");
        }

        private static void ApplyGravity(
            WeaponData weapon,
            JObject root,
            string fileName)
        {
            JToken token = root["gravity"];

            if (token == null || token.Type == JTokenType.Null)
                return;

            var o = token as JObject;

            if (o == null)
            {
                if (token.Type != JTokenType.Boolean || !(bool)token)
                {
                    Log.LogWarning(
                        fileName + ": \"gravity\" should be true or an " +
                        "object - ignored.");
                    return;
                }
            }
            else if (!((bool?)o["enabled"] ?? true))
            {
                return;
            }

            var pw = weapon as ProjectileWeaponData;

            if (!ProjectileTargetOk(pw, "gravity", fileName, true, null))
                return;

            bool physics = pw.usePhysics;

            GameObject clone = VisualCustomizer.ClonePrefab(
                physics
                    ? pw.physicsProjectilePrefab.gameObject
                    : pw.projectilePrefab.gameObject);

            Component comp = physics
                ? (Component)clone.GetComponentInChildren<PhysicsProjectile>(true)
                : clone.GetComponentInChildren<Projectile>(true);

            if (comp == null)
            {
                Log.LogWarning(
                    fileName + ": gravity - no " +
                    (physics ? "PhysicsProjectile" : "Projectile") +
                    " on the prefab. Ignored.");
                UnityEngine.Object.Destroy(clone);
                return;
            }

            var g = comp.GetComponent<ForgeGravity>();

            if (g == null)
                g = comp.gameObject.AddComponent<ForgeGravity>();

            if (o != null)
            {
                g.radius = Mathf.Max(0.5f, (float?)o["radius"] ?? 6f);
                // 300, not 30. A force of 30 settles a common enemy at
                // about 1 unit/second - which reads as "the well does
                // nothing", and cost a whole test round. With
                // massCompensated on, terminal speed is force / 30, so 300
                // is a brisk 10 units/second and maxSpeed caps it.
                g.force = (float?)o["force"] ?? 300f;
                g.massCompensated = (bool?)o["massCompensated"] ?? true;
                g.duration = Mathf.Max(0.1f, (float?)o["duration"] ?? 2.5f);
                g.holdRadius = Mathf.Max(0f, (float?)o["holdRadius"] ?? 0.7f);
                g.maxSpeed = Mathf.Max(0f, (float?)o["maxSpeed"] ?? 14f);
                g.drag = Mathf.Max(0f, (float?)o["drag"] ?? 0f);
                g.damagePerSecond =
                    Mathf.Max(0f, (float?)o["damagePerSecond"] ?? 0f);
                g.pullPlayer = (bool?)o["pullPlayer"] ?? false;
                g.pullShots = (bool?)o["pullShots"] ?? false;
                g.burst = (bool?)o["burst"] ?? true;

                string w =
                    ((string)o["where"] ?? "impact")
                        .Trim().ToLowerInvariant();

                if (w == "flight" || w == "travel" || w == "moving" ||
                    w == "shot")
                {
                    g.where = ForgeGravity.Where.Flight;
                }
                else
                {
                    g.where = ForgeGravity.Where.Impact;

                    if (w != "impact" && w != "land" && w != "landing")
                    {
                        Log.LogWarning(
                            fileName + ": gravity \"where\": '" + w +
                            "' is not recognised - use \"impact\" (opens " +
                            "where it lands) or \"flight\" (travels with the " +
                            "shot). Using impact.");
                    }
                }

                string f =
                    ((string)o["falloff"] ?? "linear")
                        .Trim().ToLowerInvariant();

                if (f == "constant" || f == "flat" || f == "even")
                    g.falloff = ForgeGravity.Falloff.Constant;
                else if (f == "inverse" || f == "square" || f == "sharp")
                    g.falloff = ForgeGravity.Falloff.Inverse;
                else
                    g.falloff = ForgeGravity.Falloff.Linear;
            }

            g.groundLayer = LayerMask.NameToLayer("Ground");

            // Eating enemy fire is only possible through the tracker: enemy
            // shots resolve collisions by raycast and ship with their
            // Collider2D DISABLED, so they are invisible to OverlapCircle at
            // any radius. The tracker registers each one as it is fired, and
            // it only does that when something has switched it on.
            if (g.pullShots)
            {
                ForgeProjectileTracker.Enabled = true;

                Log.LogInfo(
                    fileName + ": gravity \"pullShots\" is on, so enemy shot " +
                    "tracking has been switched on with it. Enemy bullets " +
                    "cannot be found any other way - they resolve their own " +
                    "collisions by raycast and their collider is disabled, " +
                    "so no overlap query will ever see one.");
            }

            bool canBurst =
                pw.explosion.damages != null &&
                pw.explosion.damages.Count > 0 &&
                pw.explosion.radius > 0f;

            if (g.burst && !canBurst)
            {
                g.burst = false;

                Log.LogInfo(
                    fileName + ": gravity \"burst\" needs an \"explosion\" " +
                    "block with a radius and a damage entry - there is none, " +
                    "so the well just closes quietly. (An empty explosion " +
                    "throws inside the game rather than doing nothing.)");
            }

            // The three timers would each delete the well mid-pull.
            ProjectileImpactBehaviour ib = pw.impactBehaviour;

            if (ib.enabled && g.where == ForgeGravity.Where.Impact)
            {
                ib.enabled = false;
                pw.impactBehaviour = ib;
            }

            if (pw.rangeData.enabled && pw.rangeData.destroyWhenReached)
            {
                ProjectileRangeData rd = pw.rangeData;
                rd.destroyWhenReached = false;
                pw.rangeData = rd;
            }

            if (pw.lifetimeData.enabled)
            {
                ProjectileLifetimeData lt = pw.lifetimeData;
                lt.enabled = false;
                pw.lifetimeData = lt;

                Log.LogInfo(
                    fileName + ": gravity - \"lifetimeData\" turned off. The " +
                    "well's own \"duration\" replaces it, and counts from " +
                    "when the well OPENED rather than from when the shot was " +
                    "fired.");
            }

            if (physics)
                pw.physicsProjectilePrefab = (PhysicsProjectile)comp;
            else
                pw.projectilePrefab = (Projectile)comp;

            Log.LogInfo(
                fileName + ": gravity well is ON - opens " +
                (g.where == ForgeGravity.Where.Flight
                    ? "the moment it is FIRED and travels with the shot"
                    : "where it LANDS") +
                ", radius " + g.radius + ", force " + g.force + " (" +
                (g.falloff == ForgeGravity.Falloff.Constant
                    ? "even at any range"
                    : g.falloff == ForgeGravity.Falloff.Inverse
                        ? "vicious close in"
                        : "strongest at the middle") +
                "), lasts " + g.duration + "s" +
                (g.pullShots ? ", eats enemy fire" : "") +
                (g.pullPlayer ? ", PULLS YOU TOO" : "") +
                (g.burst ? ", bursts when it closes" : "") + ".");

            if (g.massCompensated && g.force > 2000f)
            {
                Log.LogWarning(
                    fileName + ": gravity \"force\": " + g.force +
                    " with massCompensated on. That looks like a number tuned " +
                    "BEFORE compensation existed, when huge values were needed " +
                    "to move anything. Terminal speed is now force / 30, so " +
                    "this is asking for " + (g.force / 30f).ToString("0") +
                    " units/second and only \"maxSpeed\" is holding it back. " +
                    "Try 200-600, or set \"massCompensated\": false to keep " +
                    "the old raw behaviour.");
            }

            if (g.force <= 0f)
            {
                Log.LogWarning(
                    fileName + ": gravity \"force\": " + g.force +
                    " - the well will not pull anything. Negative values do " +
                    "not push, either: use the weapon's own \"pushForce\" " +
                    "or an explosion for that.");
            }

            if (g.holdRadius <= 0f)
            {
                Log.LogInfo(
                    fileName + ": gravity \"holdRadius\": 0 - everything " +
                    "caught will pile onto one point and JITTER, because each " +
                    "frame overshoots the centre and gets yanked back. A " +
                    "small value (0.5-1) is what makes a captured crowd sit " +
                    "still.");
            }
        }

        // "tether": a harpoon that latches on and leaves a rope behind.
        //
        // Same build-time bargain as sticky and bounce: impactBehaviour has to
        // go, because both projectile classes destroy themselves on contact
        // while it is on, and a harpoon that deletes itself on impact is a
        // bullet.
        private static void ApplyTether(
            WeaponData weapon,
            JObject root,
            string fileName)
        {
            JToken token = root["tether"];

            if (token == null || token.Type == JTokenType.Null)
                return;

            var o = token as JObject;

            if (o == null)
            {
                if (token.Type != JTokenType.Boolean || !(bool)token)
                {
                    Log.LogWarning(
                        fileName + ": \"tether\" should be true or an " +
                        "object - ignored.");
                    return;
                }
            }
            else if (!((bool?)o["enabled"] ?? true))
            {
                return;
            }

            var pw = weapon as ProjectileWeaponData;

            if (!ProjectileTargetOk(pw, "tether", fileName, true, null))
                return;

            bool physics = pw.usePhysics;

            GameObject clone = VisualCustomizer.ClonePrefab(
                physics
                    ? pw.physicsProjectilePrefab.gameObject
                    : pw.projectilePrefab.gameObject);

            Component comp = physics
                ? (Component)clone.GetComponentInChildren<PhysicsProjectile>(true)
                : clone.GetComponentInChildren<Projectile>(true);

            if (comp == null)
            {
                Log.LogWarning(
                    fileName + ": tether - no " +
                    (physics ? "PhysicsProjectile" : "Projectile") +
                    " on the prefab. Ignored.");
                UnityEngine.Object.Destroy(clone);
                return;
            }

            var t = comp.GetComponent<ForgeTether>();

            if (t == null)
                t = comp.gameObject.AddComponent<ForgeTether>();

            if (o != null)
            {
                string p =
                    ((string)o["pull"] ?? "target").Trim().ToLowerInvariant();

                if (p == "self" || p == "me" || p == "ship" || p == "grapple")
                    t.pull = ForgeTether.Pull.Self;
                else if (p == "both" || p == "leash" || p == "elastic")
                    t.pull = ForgeTether.Pull.Both;
                else if (p == "anchor" || p == "pin" || p == "peg")
                    t.pull = ForgeTether.Pull.Anchor;
                else
                {
                    t.pull = ForgeTether.Pull.Target;

                    if (p != "target" && p != "them" && p != "enemy" &&
                        p != "reel")
                    {
                        Log.LogWarning(
                            fileName + ": tether \"pull\": '" + p +
                            "' is not recognised - use \"target\" (drag them " +
                            "in), \"self\" (drag yourself), \"both\" (an " +
                            "elastic leash) or \"anchor\" (pin them where " +
                            "they stand). Using target.");
                    }
                }

                t.length = Mathf.Max(0.25f, (float?)o["length"] ?? 4f);
                t.stiffness = Mathf.Max(0f, (float?)o["stiffness"] ?? 40f);
                t.selfPull = Mathf.Max(0f, (float?)o["selfPull"] ?? 0.4f);
                t.maxClosingSpeed =
                    Mathf.Max(0f, (float?)o["maxClosingSpeed"] ?? 6f);
                t.reelSpeed = Mathf.Max(0f, (float?)o["reelSpeed"] ?? 0f);
                t.duration = Mathf.Max(0f, (float?)o["duration"] ?? 4f);
                t.breakDistance =
                    Mathf.Max(0f, (float?)o["breakDistance"] ?? 0f);
                t.damagePerSecond =
                    Mathf.Max(0f, (float?)o["damagePerSecond"] ?? 0f);
                t.max = Mathf.Max(1, (int?)o["max"] ?? 1);
                t.missLifetime =
                    Mathf.Max(0f, (float?)o["missLifetime"] ?? 3f);
                t.ropeWidth = Mathf.Max(0f, (float?)o["ropeWidth"] ?? 0.13f);

                t.hasRopeColor = ReadColor(
                    (string)o["ropeColor"], "tether \"ropeColor\"",
                    fileName, ref t.ropeColor);

                string to =
                    ((string)o["to"] ??
                     (t.pull == ForgeTether.Pull.Self ? "any" : "enemies"))
                        .Trim().ToLowerInvariant();

                if (to == "terrain" || to == "ground" || to == "walls")
                    t.latchTo = ForgeTether.To.Terrain;
                else if (to == "any" || to == "both" || to == "all")
                    t.latchTo = ForgeTether.To.Any;
                else
                    t.latchTo = ForgeTether.To.Enemies;
            }
            else
            {
                t.latchTo = ForgeTether.To.Enemies;
            }

            t.groundLayer = LayerMask.NameToLayer("Ground");

            // Borrow a LineRenderer material from the game's own electricity
            // beam. A fresh LineRenderer gets Unity's default-line material,
            // which ignores the colour we set - the same trap a fresh
            // ParticleSystem has, and the same fix: clone a stock donor.
            t.ropeMaterial = FindRopeMaterial();

            if (t.ropeMaterial == null && t.ropeWidth > 0f)
            {
                Log.LogInfo(
                    fileName + ": tether - no stock LineRenderer material was " +
                    "found to borrow, so the rope uses Unity's default. It " +
                    "will draw, but \"ropeColor\" may not take.");
            }

            // The three timers would each delete a harpoon mid-rope.
            ProjectileImpactBehaviour ib = pw.impactBehaviour;

            if (ib.enabled)
            {
                ib.enabled = false;
                pw.impactBehaviour = ib;
            }

            if (pw.rangeData.enabled && pw.rangeData.destroyWhenReached)
            {
                ProjectileRangeData rd = pw.rangeData;
                rd.destroyWhenReached = false;
                pw.rangeData = rd;
            }

            if (pw.lifetimeData.enabled)
            {
                ProjectileLifetimeData lt = pw.lifetimeData;
                lt.enabled = false;
                pw.lifetimeData = lt;

                Log.LogInfo(
                    fileName + ": tether - \"lifetimeData\" turned off. Use " +
                    "the tether's own \"duration\" instead: a lifetime counts " +
                    "from when the shot was FIRED, and the rope's timer counts " +
                    "from when it LANDED.");
            }

            if (physics)
                pw.physicsProjectilePrefab = (PhysicsProjectile)comp;
            else
                pw.projectilePrefab = (Projectile)comp;

            Log.LogInfo(
                fileName + ": tether is ON - " +
                (t.pull == ForgeTether.Pull.Target
                    ? "drags the TARGET to you"
                    : t.pull == ForgeTether.Pull.Self
                        ? "drags YOU to the target"
                        : t.pull == ForgeTether.Pull.Both
                            ? "an elastic leash, both ends pull"
                            : "PINS the target where it was struck") +
                ", rest length " + t.length + ", stiffness " + t.stiffness +
                (t.reelSpeed > 0f
                    ? ", winching in at " + t.reelSpeed + "/sec"
                    : "") +
                (t.duration > 0f ? ", lasts " + t.duration + "s" : "") +
                ", latches to " +
                (t.latchTo == ForgeTether.To.Terrain
                    ? "terrain"
                    : t.latchTo == ForgeTether.To.Enemies
                        ? "enemies"
                        : "anything") +
                ". \"impactBehaviour\" has been switched off so the harpoon " +
                "survives landing.");

            // The one combination that silently does nothing.
            if (t.latchTo == ForgeTether.To.Terrain &&
                t.pull != ForgeTether.Pull.Self)
            {
                Log.LogWarning(
                    fileName + ": tether latches to TERRAIN but \"pull\" is " +
                    "not \"self\" - a wall has no rigidbody, so there is " +
                    "nothing for the rope to drag and this will hang a line " +
                    "on the scenery and do nothing. Terrain only makes sense " +
                    "with \"pull\": \"self\" (a grappling hook).");
            }

            if (t.stiffness <= 0f && t.pull != ForgeTether.Pull.Anchor)
            {
                Log.LogWarning(
                    fileName + ": tether \"stiffness\": 0 means the rope " +
                    "never pulls on anything. It will draw and expire and do " +
                    "nothing else.");
            }
        }

        // The game's electricity beam is the only stock LineRenderer to hand.
        private static Material FindRopeMaterial()
        {
            try
            {
                var beam = JsonFieldMapper.FindAsset(
                    typeof(GameObject), "ElectricityBeam") as GameObject;

                if (beam == null)
                    return null;

                var lr = beam.GetComponentInChildren<LineRenderer>(true);

                return (lr != null) ? lr.sharedMaterial : null;
            }
            catch
            {
                return null;
            }
        }

        // "sticky": shots that stop where they land and go off together.
        //
        // The load-bearing build-time step, exactly as with "bounce", is
        // switching impactBehaviour OFF - both projectile classes destroy
        // themselves on contact while it is on, and no amount of runtime work
        // can stick a shot that has already been deleted. The explosion the
        // weapon would have used on impact becomes the bomb's payload instead.
        private static void ApplySticky(
            WeaponData weapon,
            JObject root,
            string fileName)
        {
            JToken token = root["sticky"];

            if (token == null || token.Type == JTokenType.Null)
                return;

            var o = token as JObject;

            if (o == null)
            {
                if (token.Type != JTokenType.Boolean || !(bool)token)
                {
                    Log.LogWarning(
                        fileName + ": \"sticky\" should be true or an " +
                        "object - ignored.");
                    return;
                }
            }
            else if (!((bool?)o["enabled"] ?? true))
            {
                return;
            }

            var pw = weapon as ProjectileWeaponData;

            if (!ProjectileTargetOk(pw, "sticky", fileName, true, null))
                return;

            bool physics = pw.usePhysics;

            GameObject clone = VisualCustomizer.ClonePrefab(
                physics
                    ? pw.physicsProjectilePrefab.gameObject
                    : pw.projectilePrefab.gameObject);

            Component comp = physics
                ? (Component)clone.GetComponentInChildren<PhysicsProjectile>(true)
                : clone.GetComponentInChildren<Projectile>(true);

            if (comp == null)
            {
                Log.LogWarning(
                    fileName + ": sticky - no " +
                    (physics ? "PhysicsProjectile" : "Projectile") +
                    " on the prefab. Ignored.");
                UnityEngine.Object.Destroy(clone);
                return;
            }

            var s = comp.GetComponent<ForgeSticky>();

            if (s == null)
                s = comp.gameObject.AddComponent<ForgeSticky>();

            var spec = new ForgeStickySpec.Spec();

            if (o != null)
            {
                s.fuse = Mathf.Max(0f, (float?)o["fuse"] ?? 0f);
                s.max = (int?)o["max"] ?? 8;
                s.armTime = Mathf.Max(0f, (float?)o["armTime"] ?? 0.25f);
                s.ripple = Mathf.Max(0f, (float?)o["ripple"] ?? 0f);
                s.proximityRange =
                    Mathf.Max(0.1f, (float?)o["proximityRange"] ?? 2.5f);
                s.follow = (bool?)o["follow"] ?? true;
                s.damageOnStick = (bool?)o["damageOnStick"] ?? true;

                string to =
                    ((string)o["to"] ?? "any").Trim().ToLowerInvariant();

                if (to == "terrain" || to == "ground" || to == "walls")
                    s.stickTo = ForgeSticky.To.Terrain;
                else if (to == "enemies" || to == "enemy")
                    s.stickTo = ForgeSticky.To.Enemies;
                else
                    s.stickTo = ForgeSticky.To.Any;

                string trig =
                    ((string)o["detonate"] ?? "refire")
                        .Trim().ToLowerInvariant();

                if (trig == "fuse" || trig == "timer")
                    s.trigger = ForgeSticky.Trigger.Fuse;
                else if (trig == "proximity" || trig == "mine" ||
                         trig == "near")
                    s.trigger = ForgeSticky.Trigger.Proximity;
                else if (trig == "manual" || trig == "none")
                    s.trigger = ForgeSticky.Trigger.Manual;
                else
                {
                    s.trigger = ForgeSticky.Trigger.Refire;

                    if (trig != "refire" && trig != "trigger" &&
                        trig != "command")
                    {
                        Log.LogWarning(
                            fileName + ": sticky \"detonate\": '" + trig +
                            "' is not recognised - use \"refire\", " +
                            "\"fuse\", \"proximity\" or \"manual\". Using " +
                            "refire.");
                    }
                }
            }

            if (s.max < 1)
                s.max = 1;

            s.groundLayer = LayerMask.NameToLayer("Ground");

            spec.trigger = s.trigger;
            spec.max = s.max;

            // A bomb with nothing to detonate is a dart. Refuse rather than
            // ship a weapon whose whole point silently does nothing - and note
            // that SpawnExplosion THROWS on an empty damages list, so this
            // also prevents the "shot never dies and keeps hitting" symptom.
            if (pw.explosion.damages == null ||
                pw.explosion.damages.Count == 0 ||
                pw.explosion.radius <= 0f)
            {
                Log.LogWarning(
                    fileName + ": \"sticky\" needs an \"explosion\" block " +
                    "with a radius and at least one damage entry - that " +
                    "blast IS the bomb. Ignored.");
                UnityEngine.Object.Destroy(clone);
                return;
            }

            // The shot has to survive contact, and impactBehaviour is what
            // kills it. Its explosion is not lost - it becomes what the bomb
            // does when it finally goes off.
            ProjectileImpactBehaviour ib = pw.impactBehaviour;

            if (ib.enabled)
            {
                ib.enabled = false;
                pw.impactBehaviour = ib;
            }

            // Same for the two timers, which would otherwise delete a bomb
            // out from under the player mid-wait.
            if (pw.rangeData.enabled && pw.rangeData.destroyWhenReached)
            {
                ProjectileRangeData rd = pw.rangeData;
                rd.destroyWhenReached = false;
                pw.rangeData = rd;

                Log.LogInfo(
                    fileName + ": sticky - \"rangeData.destroyWhenReached\" " +
                    "turned off, or a bomb would vanish at its range instead " +
                    "of waiting to be set off. (On a lobbed weapon rangeData " +
                    "does nothing at all anyway.)");
            }

            if (pw.lifetimeData.enabled)
            {
                ProjectileLifetimeData lt = pw.lifetimeData;
                lt.enabled = false;
                pw.lifetimeData = lt;

                Log.LogInfo(
                    fileName + ": sticky - \"lifetimeData\" turned off. Use " +
                    "sticky \"fuse\" instead: a lifetime counts from when the " +
                    "shot was FIRED, and a fuse counts from when it LANDED, " +
                    "which is the one you mean for a bomb.");
            }

            // Whether the LANDING hurts, which is not symmetrical between the
            // two shot types and cannot be made so.
            //
            // A plain shot always damages what it lands on: the game applies
            // the hit at the top of its collision routine, before any branch,
            // so it has already happened by the time anything of ours runs.
            // Zeroing the weapon's damage at build time is therefore the only
            // way to say "no".
            //
            // A lobbed shot is the opposite: its damage call lives INSIDE the
            // impactBehaviour block we just switched off, so it never damages
            // on landing whatever this says.
            if (!s.damageOnStick && !physics)
            {
                Damage none = pw.damage;
                none.amount = 0f;
                pw.damage = none;

                Log.LogInfo(
                    fileName + ": sticky \"damageOnStick\": false - the " +
                    "weapon's contact damage has been zeroed, because a " +
                    "plain shot's hit lands before anything of ours can " +
                    "stop it. Only the explosion hurts now.");
            }
            else if (s.damageOnStick && physics)
            {
                Log.LogInfo(
                    fileName + ": sticky \"damageOnStick\" cannot work on a " +
                    "LOBBED shot - the game's damage call for one lives " +
                    "inside the impactBehaviour block that had to be " +
                    "switched off for the bomb to survive landing. The bomb " +
                    "sticks silently and only the explosion hurts. Raise the " +
                    "explosion damage if the landing should count.");
            }

            if (physics)
                pw.physicsProjectilePrefab = (PhysicsProjectile)comp;
            else
                pw.projectilePrefab = (Projectile)comp;

            ForgeStickySpec.Register(weapon, spec);

            if (s.trigger == ForgeSticky.Trigger.Fuse && s.fuse <= 0f)
            {
                s.fuse = 2f;

                Log.LogInfo(
                    fileName + ": sticky \"detonate\": \"fuse\" with no " +
                    "\"fuse\" time - defaulted to 2 seconds, because a fuse " +
                    "of 0 is a bomb that never goes off.");
            }

            Log.LogInfo(
                fileName + ": sticky is ON - sticks to " +
                (s.stickTo == ForgeSticky.To.Terrain
                    ? "terrain"
                    : s.stickTo == ForgeSticky.To.Enemies
                        ? "enemies"
                        : "anything") +
                ", up to " + s.max + " at once, set off by " +
                (s.trigger == ForgeSticky.Trigger.Refire
                    ? "FIRING AGAIN once all " + s.max + " are laid"
                    : s.trigger == ForgeSticky.Trigger.Fuse
                        ? "a " + s.fuse + "s fuse from landing"
                        : s.trigger == ForgeSticky.Trigger.Proximity
                            ? "anything hostile within " + s.proximityRange
                            : "nothing but the fuse and the " + s.max +
                              "-bomb limit") +
                (s.ripple > 0f
                    ? ", rippling " + s.ripple + "s apart"
                    : "") +
                ". \"impactBehaviour\" has been switched off so the shot " +
                "survives landing; its explosion is now the bomb's payload.");

            if (s.trigger == ForgeSticky.Trigger.Refire && s.fuse <= 0f)
            {
                Log.LogInfo(
                    fileName + ": these bombs wait FOREVER for you to fire " +
                    "again (there is a 60s backstop). Add a \"fuse\" if you " +
                    "would rather they went off on their own eventually.");
            }
        }

        // "bounce": a lobbed shot that bounces off the world instead of dying
        // on it. See ForgeBounce for why this is a different key from
        // "ricochet" - they are two unrelated systems that happen to share a
        // word.
        //
        // The load-bearing build-time step is switching impactBehaviour OFF.
        // A PhysicsProjectile destroys itself in OnCollisionEnter2D whenever
        // that flag is on, so no amount of runtime work can make a bouncing
        // grenade out of one that has it. The eight stock bouncing bombs are
        // exactly this flag being off and nothing else. We keep the original
        // settings on the component and replay them on the LAST bounce.
        private static void ApplyBounce(
            WeaponData weapon,
            JObject root,
            string fileName)
        {
            JToken token = root["bounce"];

            if (token == null || token.Type == JTokenType.Null)
                return;

            var o = token as JObject;

            if (o == null)
            {
                if (token.Type != JTokenType.Boolean || !(bool)token)
                {
                    Log.LogWarning(
                        fileName + ": \"bounce\" should be true or an " +
                        "object - ignored.");
                    return;
                }
            }
            else if (!((bool?)o["enabled"] ?? true))
            {
                return;
            }

            var pw = weapon as ProjectileWeaponData;

            if (!ProjectileTargetOk(pw, "bounce", fileName, true, null))
                return;

            if (!pw.usePhysics)
            {
                Log.LogWarning(
                    fileName + ": \"bounce\" is for LOBBED weapons only " +
                    "(\"usePhysics\": true, the Derbis / grenade style) - " +
                    "ignored. A plain bullet has no gravity and no physics " +
                    "body, so there is no fall to be bouncy about; the game " +
                    "bounces those by mirroring their heading instead. Use " +
                    "\"ricochet\" for that.");
                return;
            }

            GameObject clone = VisualCustomizer.ClonePrefab(
                pw.physicsProjectilePrefab.gameObject);

            var shot = clone.GetComponentInChildren<PhysicsProjectile>(true);

            if (shot == null)
            {
                Log.LogWarning(
                    fileName + ": bounce - no PhysicsProjectile on the " +
                    "prefab. Ignored.");
                UnityEngine.Object.Destroy(clone);
                return;
            }

            var b = shot.GetComponent<ForgeBounce>();

            if (b == null)
                b = shot.gameObject.AddComponent<ForgeBounce>();

            if (o != null)
            {
                // "from" is accepted as an alias so this block reads the same
                // way as grow and damageRamp when it is used as a ramp.
                b.restitution =
                    (float?)o["restitution"] ?? (float?)o["from"] ?? 0.6f;

                b.to = (float?)o["to"] ?? -1f;
                b.overHeight = (float?)o["overHeight"] ?? 20f;
                b.curve = (float?)o["curve"] ?? 1f;
                b.friction = (float?)o["friction"] ?? -1f;
                b.max = (int?)o["max"] ?? 0;
                b.minSpeed = (float?)o["minSpeed"] ?? 1f;
                b.scatter = (float?)o["scatter"] ?? 0f;
                b.damageOnBounce = (bool?)o["damageOnBounce"] ?? false;
                b.damageFalloff = (float?)o["damageFalloff"] ?? 0f;
                b.explodeOnBounce = (bool?)o["explodeOnBounce"] ?? false;
                b.dischargeOnBounce = (bool?)o["dischargeOnBounce"] ?? false;
            }

            if (b.restitution < 0f)
                b.restitution = 0f;

            if (b.curve <= 0f)
                b.curve = 1f;

            if (b.overHeight <= 0f)
                b.overHeight = 20f;

            b.damageFalloff = Mathf.Clamp01(b.damageFalloff);
            b.minSpeed = Mathf.Max(0f, b.minSpeed);
            b.scatter = Mathf.Max(0f, b.scatter);

            // Take impactBehaviour away from the game and give it to the last
            // bounce. Without this the shot dies on its first contact and
            // nothing else in this method matters.
            b.ending = pw.impactBehaviour;
            b.hasEnding = true;

            ProjectileImpactBehaviour off = pw.impactBehaviour;
            off.enabled = false;
            pw.impactBehaviour = off;

            // Box2D must not add a rebound of its own on top of ours. We
            // rebuild the whole normal component from the impact speed, so any
            // restitution the solver already applied is discarded - but a
            // bouncy TERRAIN material could still throw the shot around on a
            // contact our minSpeed rejected. Minimum combine plus zero pins
            // that shut whatever the world is made of.
            //
            // A FRESH material, never an edit: the stock PhysMatGrenade is
            // shared by a dozen ammo prefabs, so tuning it here would retune
            // every grenade in the game.
            ApplyBounceMaterial(clone, b, fileName);

            // Calibrate the height ramp against this shot's OWN physics.
            if (b.to >= 0f)
            {
                var body = clone.GetComponentInChildren<Rigidbody2D>(true);

                float gravity = (body != null) ? body.gravityScale : 1.8f;
                float damping = (body != null) ? body.linearDamping : 0.4f;

                b.fullSpeed = ForgeBounce.FallSpeed(
                    b.overHeight, gravity, damping, Time.fixedDeltaTime);

                if (b.fullSpeed <= 0.01f)
                {
                    b.fullSpeed = 1f;

                    Log.LogWarning(
                        fileName + ": bounce - could not work out how fast " +
                        "this shot lands after a " + b.overHeight +
                        "-unit fall, so the height ramp is disabled and " +
                        "\"restitution\" applies flat.");

                    b.to = -1f;
                }
                else
                {
                    Log.LogInfo(
                        fileName + ": bounce height ramp reaches full after a " +
                        b.overHeight + "-unit fall, which lands at " +
                        b.fullSpeed.ToString("0.0") + " units/sec (gravity " +
                        "scale " + gravity + ", damping " + damping + "). " +
                        "Solved against the engine's real step - the textbook " +
                        "sqrt(2gh) would have overstated it by a quarter and " +
                        "made the ramp need a much bigger drop than you asked " +
                        "for.");
                }
            }

            pw.physicsProjectilePrefab = (PhysicsProjectile)shot;

            // A bouncing shot has nothing to stop it. rangeData does NOT
            // count: on a lobbed weapon the game computes a range and then
            // never reads it again, so lifetimeData is the only fuse there is.
            if (b.Unlimited && !pw.lifetimeData.enabled)
            {
                Log.LogWarning(
                    fileName + ": this weapon bounces forever. \"max\" is 0 " +
                    "(unlimited) and \"lifetimeData\" is off, so nothing ever " +
                    "ends the shot - and \"rangeData\" will not save you, " +
                    "because the game computes a range for a lobbed shot and " +
                    "then never reads it. Set \"lifetimeData\": " +
                    "{ \"enabled\": true, \"time\": 3 } or a bounce \"max\".");
            }

            if (b.restitution > 1f || b.to > 1f)
            {
                // It does NOT run away, which is worth saying because it is
                // the obvious fear. Linear damping eats more energy per bounce
                // than a modest overshoot puts in, so a super-elastic shot
                // converges on a STEADY bouncing height rather than climbing:
                // simulated at Derbis settings, 1.15 settles around 2 units,
                // 1.3 around 7, 1.5 around 19. Past about 1.5 it is high
                // enough to leave the room, which is what "max" is for.
                Log.LogInfo(
                    fileName + ": bounce is SUPER-ELASTIC (restitution above " +
                    "1) - the shot leaves each surface faster than it " +
                    "arrived. It will not climb away: damping balances it out " +
                    "at a steady bouncing height (about 2 units at 1.15, 7 at " +
                    "1.3, 19 at 1.5). Use \"max\" if you want it to end " +
                    "anyway.");
            }

            Log.LogInfo(
                fileName + ": bounce is ON - restitution " + b.restitution +
                (b.to >= 0f
                    ? " rising to " + b.to + " after a " + b.overHeight +
                      "-unit fall"
                    : " (flat)") +
                ", " + (b.Unlimited ? "unlimited" : b.max + " max") +
                " bounces" +
                (b.damageOnBounce ? ", damaging on contact" : "") +
                ". \"impactBehaviour\" has been switched off so the shot " +
                "survives contact; its settings now fire on the LAST bounce.");
        }

        // A private PhysicsMaterial2D for this weapon's shot, with bounce
        // pinned off. See ApplyBounce for why it must be a new instance.
        private static void ApplyBounceMaterial(
            GameObject clone, ForgeBounce b, string fileName)
        {
            Collider2D[] cols = clone.GetComponentsInChildren<Collider2D>(true);

            if (cols == null || cols.Length == 0)
            {
                Log.LogWarning(
                    fileName + ": bounce - this shot has no Collider2D, so " +
                    "it never actually touches anything and will not bounce. " +
                    "Is the template really a lobbed weapon?");
                return;
            }

            // Keep whatever friction the shot already had unless asked
            // otherwise - it is the difference between rolling and skidding,
            // and the stock grenade's 0.4 is a sensible roll.
            float friction = b.friction;

            if (friction < 0f)
            {
                friction = 0.4f;

                for (int i = 0; i < cols.Length; i++)
                {
                    PhysicsMaterial2D existing = cols[i].sharedMaterial;

                    if (existing != null)
                    {
                        friction = existing.friction;
                        break;
                    }
                }
            }

            var mat = new PhysicsMaterial2D("Forge Bounce");
            mat.friction = friction;
            mat.bounciness = 0f;

            // Minimum, not the stock Maximum: with Maximum a bouncy terrain
            // material would win over our zero and the solver would add its
            // own rebound on contacts we deliberately ignored.
            mat.bounceCombine = PhysicsMaterialCombine2D.Minimum;

            for (int i = 0; i < cols.Length; i++)
                cols[i].sharedMaterial = mat;
        }

        // "charge": hold the trigger to build a shot up, let go to fire it.
        //
        // The game has nothing like this. Its one held-trigger mechanic,
        // warmupTime, is a minigun SPIN-UP - it gates firing until a meter
        // fills, then fires normally forever, and empties the instant you let
        // go. Nothing anywhere reacts to the trigger coming UP, and no weapon
        // stat is ever scaled by how long it was held. So the release, the
        // scaling, the meter and the sounds are all ours: see ForgeCharge and
        // ForgeChargePatch.
        private static void ApplyCharge(
            WeaponData weapon,
            JObject root,
            bool isGadget,
            string fileName)
        {
            JToken token = root["charge"];

            if (token == null || token.Type == JTokenType.Null)
                return;

            var o = token as JObject;

            if (o == null)
            {
                if (token.Type != JTokenType.Boolean || !(bool)token)
                {
                    Log.LogWarning(
                        fileName + ": \"charge\" should be true or an " +
                        "object - ignored.");
                    return;
                }
            }
            else if (!((bool?)o["enabled"] ?? true))
            {
                return;
            }

            var cfg = new ForgeCharge.Config();

            string chargeSfx = null;

            if (o != null)
            {
                string mode =
                    ((string)o["mode"] ?? "release")
                        .Trim().ToLowerInvariant();

                if (mode == "auto" || mode == "sustained" ||
                    mode == "cycle" || mode == "every")
                {
                    cfg.mode = ForgeCharge.Mode.Auto;
                }
                else
                {
                    cfg.mode = ForgeCharge.Mode.Release;

                    if (mode != "release" && mode != "buster" &&
                        mode != "hold")
                    {
                        Log.LogWarning(
                            fileName + ": charge \"mode\": '" + mode +
                            "' is not recognised - use \"release\" (hold, " +
                            "let go, one big shot) or \"auto\" (keeps firing " +
                            "normally, one charged shot each time the meter " +
                            "fills). Using release.");
                    }
                }

                cfg.time = (float?)o["time"] ?? 1f;
                cfg.min = (float?)o["min"] ?? 0.3f;
                cfg.tapFire = (bool?)o["tapFire"] ?? true;
                cfg.holdFire = (bool?)o["holdFire"] ?? true;
                cfg.curve = (float?)o["curve"] ?? 1f;
                cfg.scale = (bool?)o["scale"] ?? true;

                cfg.damage = (float?)o["damage"] ?? 3f;
                cfg.size = (float?)o["size"] ?? 1.8f;
                cfg.speed = (float?)o["speed"] ?? 1f;
                cfg.range = (float?)o["range"] ?? 1f;
                cfg.knockback = (float?)o["knockback"] ?? 1f;
                cfg.push = (float?)o["push"] ?? 1f;
                cfg.explosion = (float?)o["explosion"] ?? 1f;
                cfg.burn = (float?)o["burn"] ?? 1f;
                cfg.spread = (float?)o["spread"] ?? 1f;
                cfg.cost = (float?)o["cost"] ?? 1f;
                cfg.count = (int?)o["count"] ?? 0;
                cfg.pierce = (int?)o["pierce"] ?? 0;

                cfg.meter = (bool?)o["meter"] ?? true;
                cfg.meterSprite = (string)o["meterSprite"];
                cfg.meterFrom = (float?)o["meterFrom"] ?? 0.2f;
                cfg.meterTo = (float?)o["meterTo"] ?? 1.1f;

                cfg.hasColor = ReadColor(
                    (string)o["color"], "charge \"color\"", fileName,
                    ref cfg.color);

                cfg.hasMeterColor = ReadColor(
                    (string)o["meterColor"], "charge \"meterColor\"",
                    fileName, ref cfg.meterColor);

                cfg.readySfx = (string)o["readySfx"];
                chargeSfx = (string)o["chargeSfx"];
            }

            // --- things that make the whole feature impossible -------------

            if (isGadget)
            {
                Log.LogWarning(
                    fileName + ": \"charge\" needs a trigger you can HOLD, " +
                    "and a gadget has none - the 1/2/3 slots fire their " +
                    "weapon directly the moment the module activates, " +
                    "without going through the ship's Shooter at all. " +
                    "Ignored. Put a charge weapon in the primary or " +
                    "secondary slot.");
                return;
            }

            var hitscan = weapon as HitscanWeaponData;

            if (hitscan != null && cfg.mode == ForgeCharge.Mode.Release)
            {
                Log.LogWarning(
                    fileName + ": \"charge\" in \"release\" mode does not " +
                    "work on a BEAM - ignored. A beam is drawn only while " +
                    "the trigger is DOWN, so a shot fired the moment you let " +
                    "go would do its damage completely invisibly. Use " +
                    "\"mode\": \"auto\" instead: the beam fires normally and " +
                    "one tick in every charge is the big one.");
                return;
            }

            // --- numbers that would quietly do nothing ---------------------

            if (cfg.time <= 0f)
            {
                Log.LogWarning(
                    fileName + ": charge \"time\": " + cfg.time +
                    " means the shot is fully charged the instant you touch " +
                    "the trigger, so there is nothing to build. Using 1.");
                cfg.time = 1f;
            }

            cfg.min = Mathf.Clamp01(cfg.min);
            cfg.meterFrom = Mathf.Max(0f, cfg.meterFrom);
            cfg.meterTo = Mathf.Max(0f, cfg.meterTo);

            if (cfg.curve <= 0f)
                cfg.curve = 1f;

            ForgeOrbit.Config orbitCfg;

            if (ForgeOrbit.TryGet(weapon, out orbitCfg) &&
                orbitCfg.suppressFire &&
                cfg.mode == ForgeCharge.Mode.Release &&
                cfg.holdFire)
            {
                Log.LogWarning(
                    fileName + ": this weapon is BOTH an orbit weapon and a " +
                    "charge weapon, and both of them work by taking over the " +
                    "ship's Shoot. They will fight: whichever gets there " +
                    "first wins, and the loser silently does nothing. Pick " +
                    "one - or set \"orbitSuppressFire\": false so the ring is " +
                    "passive and the charge owns the trigger.");
            }

            if (weapon.warmupTime > 0f &&
                cfg.mode == ForgeCharge.Mode.Release)
            {
                Log.LogWarning(
                    fileName + ": this weapon has BOTH \"charge\" and " +
                    "\"warmupTime\": " + weapon.warmupTime + ". They are two " +
                    "different mechanics and stacking them is almost " +
                    "certainly not what you want - warmupTime is the game's " +
                    "own minigun spin-up, which gates firing and then resets " +
                    "when you let go, and it drives the same warmupSfx slot " +
                    "the charge loop uses. Set \"warmupTime\": 0 on a charge " +
                    "weapon.");
            }

            bool doesSomething =
                cfg.damage != 1f || cfg.size != 1f || cfg.speed != 1f ||
                cfg.range != 1f || cfg.knockback != 1f || cfg.push != 1f ||
                cfg.explosion != 1f || cfg.burn != 1f || cfg.spread != 1f ||
                cfg.count != 0 || cfg.pierce != 0;

            if (!doesSomething)
            {
                Log.LogWarning(
                    fileName + ": \"charge\" is on but every bonus is 1 (or " +
                    "0), so a fully charged shot is identical to a normal " +
                    "one - all the hold does is delay you. Set at least one " +
                    "of damage / size / speed / range / explosion / count.");
            }

            // --- sounds -----------------------------------------------------

            if (!string.IsNullOrEmpty(chargeSfx))
            {
                // The looping charge sound rides the weapon's own warmupSfx
                // slot: Shooter already starts it, keeps the handle and stops
                // it, and a held sound only works if the Sfx loops - which
                // this registration forces. Writing the slot here rather than
                // inventing a second held-sound path is what gets 3D
                // position, the volume slider and the mixer for free.
                string guid = ResolveChargeSfx(
                    chargeSfx, weapon.warmupSfx, true, "chargeSfx", fileName);

                if (guid != null)
                {
                    if (!string.IsNullOrEmpty(weapon.warmupSfx) &&
                        weapon.warmupSfx != guid)
                    {
                        Log.LogInfo(
                            fileName + ": charge \"chargeSfx\" has replaced " +
                            "this weapon's warmupSfx - they are the same " +
                            "slot.");
                    }

                    weapon.warmupSfx = guid;
                }
            }

            if (!string.IsNullOrEmpty(cfg.readySfx))
            {
                cfg.readySfx = ResolveChargeSfx(
                    cfg.readySfx, null, false, "readySfx", fileName);
            }

            if (cfg.mode == ForgeCharge.Mode.Release &&
                string.IsNullOrEmpty(weapon.warmupSfx))
            {
                Log.LogInfo(
                    fileName + ": charge has no charging sound. The weapon's " +
                    "\"warmupSfx\" slot IS the charge loop - set it (or " +
                    "charge \"chargeSfx\") and the game will play it for as " +
                    "long as you hold. No stock projectile weapon fills that " +
                    "slot, so it is empty unless you say otherwise.");
            }

            // --- the meter --------------------------------------------------

            if (cfg.meter)
                ResolveChargeMeter(weapon, cfg, fileName);

            ForgeCharge.Register(weapon, cfg);

            Log.LogInfo(
                fileName + ": charge is ON - " +
                (cfg.mode == ForgeCharge.Mode.Auto
                    ? "auto mode, one charged shot every " + cfg.time +
                      "s of sustained fire"
                    : "release mode, " + cfg.time + "s to full") +
                ", x" + cfg.damage + " damage and x" + cfg.size +
                " size at full charge.");
        }

        // Shared by chargeSfx and readySfx: a custom sound name, or a stock
        // sound id, or a loud complaint. Returns null when it is neither, so
        // the caller can leave the slot alone rather than silence it.
        // ------------------------------------------------------------------
        // catalyst - a node you shoot INTO, which passes the shot on harder
        // ------------------------------------------------------------------
        private static void ApplyCatalyst(
            WeaponData weapon,
            JObject root,
            string fileName)
        {
            JToken token = root["catalyst"];

            if (token == null || token.Type == JTokenType.Null)
                return;

            var o = token as JObject;

            if (o == null)
            {
                if (token.Type != JTokenType.Boolean || !(bool)token)
                {
                    Log.LogWarning(
                        fileName + ": \"catalyst\" should be true or an " +
                        "object - ignored.");
                    return;
                }
            }
            else if (!((bool?)o["enabled"] ?? true))
            {
                return;
            }

            var pw = weapon as ProjectileWeaponData;

            if (!ProjectileTargetOk(pw, "catalyst", fileName, true, null))
                return;

            var cfg = new ForgeCatalyst.Config();

            if (o != null)
            {
                string n =
                    ((string)o["node"] ?? "stop").Trim().ToLowerInvariant();

                if (n == "travel" || n == "fly" || n == "moving")
                    cfg.node = ForgeCatalyst.Node.Travel;
                else if (n == "drift" || n == "wander")
                    cfg.node = ForgeCatalyst.Node.Drift;
                else if (n == "toss" || n == "coin" || n == "arc" ||
                         n == "throw")
                    cfg.node = ForgeCatalyst.Node.Toss;
                else
                    cfg.node = ForgeCatalyst.Node.Stop;

                string tr =
                    ((string)o["trigger"] ?? "different")
                        .Trim().ToLowerInvariant();

                cfg.trigger = (tr == "any" || tr == "all" || tr == "self")
                    ? ForgeCatalyst.Trigger.Any
                    : ForgeCatalyst.Trigger.Different;

                string rd =
                    ((string)o["redirect"] ?? "chain")
                        .Trim().ToLowerInvariant();

                cfg.redirect = (rd == "split" || rd == "burst" || rd == "fan")
                    ? ForgeCatalyst.Redirect.Split
                    : ForgeCatalyst.Redirect.Chain;

                cfg.armTime = Mathf.Max(0f, (float?)o["armTime"] ?? 0.35f);
                cfg.lifetime = Mathf.Max(0.2f, (float?)o["lifetime"] ?? 6f);
                cfg.radius = Mathf.Max(0.2f, (float?)o["radius"] ?? 1.2f);
                cfg.drift = Mathf.Max(0f, (float?)o["drift"] ?? 0.6f);
                cfg.max = Mathf.Max(0, (int?)o["max"] ?? 3);
                cfg.uses = Mathf.Max(1, (int?)o["uses"] ?? 1);

                cfg.targets = Mathf.Max(1, (int?)o["targets"] ?? 3);
                cfg.range = Mathf.Max(1f, (float?)o["range"] ?? 10f);
                cfg.targetObjects = (bool?)o["targetObjects"] ?? true;
                cfg.lockOn = (bool?)o["lockOn"] ?? true;
                cfg.lockOnTurnRate =
                    Mathf.Max(1f, (float?)o["lockOnTurnRate"] ?? 360f);
                cfg.damageMultiplier =
                    Mathf.Max(0f, (float?)o["damageMultiplier"] ?? 2f);
                cfg.speedMultiplier =
                    Mathf.Max(0.05f, (float?)o["speedMultiplier"] ?? 1.5f);
                cfg.chainFalloff =
                    Mathf.Max(0f, (float?)o["chainFalloff"] ?? 1f);
                cfg.chainRevisit = (bool?)o["chainRevisit"] ?? false;

                cfg.catchEnemyShots = (bool?)o["catchEnemyShots"] ?? true;
                cfg.reflectDamage =
                    Mathf.Max(0f, (float?)o["reflectDamage"] ?? 2f);

                // Where a reflected shot goes. The older "aimReflected" bool
                // still works and still means "at the nearest enemy"; this is
                // the fuller answer and adds the cursor.
                cfg.aimMode =
                    ForgeBulletTurn.ParseAim((string)o["reflectAim"]);
                cfg.reflectSpeed =
                    Mathf.Max(0.05f, (float?)o["reflectSpeed"] ?? 1.5f);
                cfg.aimReflected = (bool?)o["aimReflected"] ?? true;
                cfg.enemyShotsConsume =
                    (bool?)o["enemyShotsConsume"] ?? true;

                cfg.pulse = (bool?)o["pulse"] ?? true;
                cfg.spin = (bool?)o["spin"] ?? false;
                cfg.spinSpeed = Mathf.Max(0.1f, (float?)o["spinSpeed"] ?? 6f);
                cfg.spinHorizontal = (bool?)o["spinHorizontal"] ?? false;
                cfg.spinThinness =
                    Mathf.Clamp((float?)o["spinThinness"] ?? 0.12f, 0.02f, 1f);
                cfg.nodeSprite = (string)o["nodeSprite"] ?? "";
                cfg.gravityScale = (float?)o["gravityScale"] ?? -999f;
                cfg.preferNodes = (bool?)o["preferNodes"] ?? false;

                Color c = cfg.color;

                if (ReadColor((string)o["color"], "catalyst \"color\"",
                              fileName, ref c))
                {
                    cfg.color = c;
                }

                cfg.triggerSfxGuid = ResolveChargeSfx(
                    (string)o["triggerSfx"], weapon.shootSfx, false,
                    "catalyst triggerSfx", fileName);

                cfg.armSfxGuid = ResolveChargeSfx(
                    (string)o["armSfx"], weapon.shootSfx, false,
                    "catalyst armSfx", fileName);
            }

            bool physics = pw.usePhysics;

            GameObject clone = VisualCustomizer.ClonePrefab(
                physics
                    ? pw.physicsProjectilePrefab.gameObject
                    : pw.projectilePrefab.gameObject);

            Component comp = physics
                ? (Component)clone.GetComponentInChildren<PhysicsProjectile>(true)
                : clone.GetComponentInChildren<Projectile>(true);

            if (comp == null)
            {
                Log.LogWarning(
                    fileName + ": catalyst - no " +
                    (physics ? "PhysicsProjectile" : "Projectile") +
                    " on the prefab. Ignored.");
                UnityEngine.Object.Destroy(clone);
                return;
            }

            var node = comp.GetComponent<ForgeCatalystNode>();

            if (node == null)
                node = comp.gameObject.AddComponent<ForgeCatalystNode>();

            // Both, and the ID is the one that matters: Unity serializes the int
            // into every clone the game fires, and drops the reference. See
            // ForgeConfigTable.
            node.cfg = cfg;
            node.cfgId = ForgeConfigTable.Id(cfg);

            // A node must not die on contact, or it would explode against the
            // first wall - or, for a tossed coin, the floor - instead of
            // staying there to be shot. Same bargain the harpoon and the sticky
            // bombs make. Only `travel` keeps its impact behaviour, since a
            // node you are meant to catch in mid-air never lands anyway.
            if (cfg.SurvivesContact)
            {
                ProjectileImpactBehaviour ib = pw.impactBehaviour;

                if (ib.enabled)
                {
                    ib.enabled = false;
                    pw.impactBehaviour = ib;

                    Log.LogInfo(
                        fileName + ": catalyst - \"impactBehaviour\" switched " +
                        "off so a node parks where it lands instead of dying " +
                        "there. Its own \"lifetime\" ends it.");
                }

                ProjectileRangeData rd = pw.rangeData;

                if (rd.enabled && rd.destroyWhenReached)
                {
                    rd.destroyWhenReached = false;
                    pw.rangeData = rd;
                }
            }

            // The toss arc. Gravity is what makes a thrown coin a thrown coin,
            // so this is the one node mode that deliberately keeps it - and the
            // one that lets you tune it.
            if (cfg.gravityScale > -998f)
            {
                foreach (Rigidbody2D body in
                         clone.GetComponentsInChildren<Rigidbody2D>(true))
                {
                    body.gravityScale = cfg.gravityScale;
                }

                Log.LogInfo(
                    fileName + ": catalyst - node gravityScale " +
                    cfg.gravityScale.ToString("0.##") + ".");
            }

            if (cfg.node == ForgeCatalyst.Node.Toss && !physics)
            {
                Log.LogWarning(
                    fileName + ": catalyst \"node\": \"toss\" on a weapon " +
                    "that is not lobbed. A plain projectile has no rigidbody " +
                    "and no gravity, so the node will fly straight rather than " +
                    "arc. Use a lobbed template (Derbis, Loon, Flame) for a " +
                    "thrown coin, or \"stop\" for a hovering node.");
            }

            ResolveCatalystArt(cfg, fileName);

            if (physics)
                pw.physicsProjectilePrefab = (PhysicsProjectile)comp;
            else
                pw.projectilePrefab = (Projectile)comp;

            // Both halves need the tracker, and for DIFFERENT lists.
            //
            // Your own shots are what trigger a node, and there is no physics
            // route to them at all: PlayerProjectiles is absent from its own row
            // in the layer matrix, and a plain Projectile ships with its
            // collider disabled because it resolves hits by CircleCast. Enemy
            // fire is invisible for the same reason.
            ForgeProjectileTracker.TrackPlayer = true;

            if (cfg.catchEnemyShots)
                ForgeProjectileTracker.Enabled = true;

            if (cfg.redirect == ForgeCatalyst.Redirect.Chain)
            {
                // Deliberately NOT conditioned on this weapon's usePhysics.
                // The chain is granted to the shot that goes THROUGH the node,
                // which comes from a different weapon entirely - so the node
                // being lobbed says nothing about it. An earlier version warned
                // on the node's own physics and would have told anyone building
                // a thrown coin that chaining was broken, which is false.
                Log.LogInfo(
                    fileName + ": catalyst - chain redirect. Chaining is " +
                    "driven by hooks that exist only for PLAIN projectiles, so " +
                    "if the weapon you shoot this node WITH is lobbed, its shot " +
                    "is aimed at one enemy and boosted rather than chained " +
                    "through several. \"split\" works for both kinds.");
            }

            ForgeCatalyst.Register(weapon, cfg);

            Log.LogInfo(
                fileName + ": catalyst - " + cfg.node + " node, " +
                cfg.lifetime.ToString("0.#") + "s, catch radius " +
                cfg.radius.ToString("0.##") + ", set off by " +
                (cfg.trigger == ForgeCatalyst.Trigger.Different
                    ? "a DIFFERENT weapon" : "any of your shots") +
                ", redirect " + cfg.redirect + " at " + cfg.targets +
                " target(s)" +
                (cfg.targetObjects ? " (enemies and objects)" : " (enemies only)") +
                (cfg.lockOn ? ", locked on" : ", aimed once") +
                " for x" + cfg.damageMultiplier.ToString("0.##") +
                " damage" +
                (cfg.catchEnemyShots
                    ? (", reflects enemy fire at x" +
                       cfg.reflectDamage.ToString("0.##"))
                    : "") +
                ", " + cfg.uses + " use(s), max " + cfg.max + ".");
        }

        private static void ResolveCatalystArt(
            ForgeCatalyst.Config cfg, string fileName)
        {
            if (string.IsNullOrEmpty(cfg.nodeSprite))
                return;

            // "coin" is drawn rather than loaded, so a preset using it works
            // with nothing installed.
            if (cfg.nodeSprite.Trim().Equals(
                    "coin", StringComparison.OrdinalIgnoreCase))
            {
                cfg.nodeArt = ForgeCatalystArt.Coin();
                return;
            }

            ForgeSpriteLibrary.LoadAll();

            ForgeSpriteLibrary.Art custom;

            if (ForgeSpriteLibrary.TryGetArt(cfg.nodeSprite, out custom) &&
                custom.sprite != null)
            {
                cfg.nodeArt = custom.sprite;
                return;
            }

            string known = ForgeSpriteLibrary.Count > 0
                ? " Loaded custom art: " +
                  string.Join(", ", ForgeSpriteLibrary.Names) + "."
                : " There is no art in the sprites folder yet.";

            Log.LogWarning(
                fileName + ": catalyst \"nodeSprite\": '" + cfg.nodeSprite +
                "' is neither \"coin\" nor one of your sprites - the node " +
                "keeps the weapon's own bullet art." + known);
        }

        // ------------------------------------------------------------------
        // onKill - the weapon pays you for its own kills
        // ------------------------------------------------------------------
        //
        // Registered against the WeaponData rather than hung on the projectile,
        // because a kill can arrive by three routes (bullet, beam, explosion)
        // and only one of them has a projectile to carry a component.
        // The icon and colour the buff indicator wears.
        //
        // Read from the weapon's own "module" block rather than a new field: a
        // Forge weapon IS registered as a module, so the art the player already
        // picked for its card is exactly the art they will recognise on screen.
        // Same resolution path as the card icon, so a custom sprite works here
        // for free.
        private static void ResolveBuffIndicatorArt(
            ForgeOnKill.Config cfg, JObject root, string fileName)
        {
            if (cfg == null)
                return;

            var moduleJson = root["module"] as JObject;

            if (moduleJson == null)
                return;

            string iconName = (string)moduleJson["icon"];

            if (!string.IsNullOrEmpty(iconName))
            {
                iconName = iconName.Trim();

                cfg.indicatorIcon =
                    (JsonFieldMapper.FindAsset(typeof(Sprite), iconName)
                        as Sprite)
                    ?? ForgeSpriteLibrary.ResolveIconSprite(iconName);
            }

            Color c = Color.white;

            if (ReadColor((string)moduleJson["color"],
                          "module \"color\" (buff indicator)", fileName, ref c))
            {
                cfg.indicatorColor = c;
            }
        }

        private static void ApplyOnKill(
            WeaponData weapon,
            JObject root,
            string fileName)
        {
            JToken token = root["onKill"];

            if (token == null || token.Type == JTokenType.Null)
                return;

            var o = token as JObject;

            if (o == null)
            {
                if (token.Type != JTokenType.Boolean || !(bool)token)
                {
                    Log.LogWarning(
                        fileName + ": \"onKill\" should be true or an object " +
                        "- ignored.");
                    return;
                }
            }
            else if (!((bool?)o["enabled"] ?? true))
            {
                return;
            }

            var cfg = new ForgeOnKill.Config();

            if (o != null)
            {
                cfg.resource = (string)o["resource"] ?? "";
                cfg.amount = Mathf.Max(0f, (float?)o["amount"] ?? 0f);
                cfg.asPickup = (bool?)o["asPickup"] ??
                               (bool?)o["drop"] ?? false;

                cfg.chance = Mathf.Clamp01((float?)o["chance"] ?? 1f);
                cfg.maxPerSecond =
                    Mathf.Max(0f, (float?)o["maxPerSecond"] ?? 0f);

                cfg.clearBurn = Mathf.Max(0f, (float?)o["clearBurn"] ?? 0f);

                cfg.buffDuration =
                    Mathf.Max(0f, (float?)o["buffDuration"] ?? 0f);
                cfg.buffFireRate =
                    Mathf.Max(0f, (float?)o["buffFireRate"] ?? 0f);
                cfg.buffDamage =
                    Mathf.Max(0f, (float?)o["buffDamage"] ?? 0f);
                cfg.buffMaxStacks =
                    Mathf.Max(1, (int?)o["buffMaxStacks"] ?? 1);

                cfg.sfxGuid = ResolveChargeSfx(
                    (string)o["sfx"], weapon.shootSfx, false,
                    "onKill sfx", fileName);

                cfg.buffIndicator = (string)o["buffIndicator"] ?? "";
            }

            // The indicator wears the weapon's own module art, which is what he
            // asked for ("maybe use the icon of the weapon or module buffing
            // you") and needs no new field: a Forge weapon is registered as a
            // module, so it already has an icon and a colour.
            ResolveBuffIndicatorArt(cfg, root, fileName);

            // Resource: default to the weapon's own if none was named, since
            // "pay me back what I spent" is the commonest use.
            if (!string.IsNullOrEmpty(cfg.resource))
            {
                cfg.resourceAsset =
                    JsonFieldMapper.FindAsset(typeof(Resource), cfg.resource)
                        as Resource;

                if (cfg.resourceAsset == null)
                {
                    Log.LogWarning(
                        fileName + ": onKill \"resource\": '" + cfg.resource +
                        "' is not a resource I know - falling back to the " +
                        "weapon's own resourceUsed.");
                }
            }

            if (cfg.resourceAsset == null)
                cfg.resourceAsset = weapon.resourceUsed;

            if (cfg.amount > 0f && cfg.resourceAsset == null)
            {
                Log.LogWarning(
                    fileName + ": onKill has an \"amount\" but there is no " +
                    "resource to grant - this weapon has no \"resourceUsed\" " +
                    "either. Name a \"resource\" explicitly.");
            }

            if (cfg.buffDuration <= 0f &&
                (cfg.buffFireRate > 0f || cfg.buffDamage > 0f))
            {
                Log.LogWarning(
                    fileName + ": onKill sets \"buffFireRate\" or " +
                    "\"buffDamage\" but no \"buffDuration\", so the buff would " +
                    "last no time at all. Give it a duration in seconds.");
            }

            if (!cfg.DoesAnything)
            {
                Log.LogWarning(
                    fileName + ": onKill pays out nothing. Set at least one of " +
                    "\"amount\" (with a resource), \"clearBurn\", or a buff " +
                    "with a \"buffDuration\".");
                return;
            }

            ForgeOnKill.Register(weapon, cfg);

            Log.LogInfo(
                fileName + ": onKill - " +
                ((cfg.amount > 0f && cfg.resourceAsset != null)
                    ? (cfg.asPickup ? "drops " : "grants ") +
                      cfg.amount.ToString("0.##") + " " +
                      cfg.resourceAsset.name
                    : "no resource") +
                (cfg.clearBurn > 0f
                    ? (", cools " + cfg.clearBurn.ToString("0.#")) : "") +
                (cfg.buffDuration > 0f
                    ? (", buff " + cfg.buffDuration.ToString("0.#") + "s" +
                       (cfg.buffFireRate > 0f
                           ? (" fireRate +" +
                              (cfg.buffFireRate * 100f).ToString("0") + "%")
                           : "") +
                       (cfg.buffDamage > 0f
                           ? (" damage +" +
                              (cfg.buffDamage * 100f).ToString("0") + "%")
                           : "") +
                       (cfg.buffMaxStacks > 1
                           ? (" x" + cfg.buffMaxStacks) : ""))
                    : "") +
                (cfg.chance < 1f
                    ? (", " + (cfg.chance * 100f).ToString("0") + "% chance")
                    : "") +
                (cfg.maxPerSecond > 0f
                    ? (", max " + cfg.maxPerSecond.ToString("0.#") + "/s")
                    : "") + ".");
        }

        // ------------------------------------------------------------------
        // gas - a shot that leaves a lingering cloud
        // ------------------------------------------------------------------
        private static void ApplyGas(
            WeaponData weapon,
            JObject root,
            string fileName)
        {
            JToken token = root["gas"];

            if (token == null || token.Type == JTokenType.Null)
                return;

            var o = token as JObject;

            if (o == null)
            {
                if (token.Type != JTokenType.Boolean || !(bool)token)
                {
                    Log.LogWarning(
                        fileName + ": \"gas\" should be true or an object - " +
                        "ignored.");
                    return;
                }
            }
            else if (!((bool?)o["enabled"] ?? true))
            {
                return;
            }

            var pw = weapon as ProjectileWeaponData;

            if (!ProjectileTargetOk(pw, "gas", fileName, true, null))
                return;

            var cfg = new ForgeGas.Config();

            if (o != null)
            {
                string c =
                    ((string)o["cloud"] ?? "grow").Trim().ToLowerInvariant();

                if (c == "hang" || c == "still" || c == "static")
                    cfg.cloud = ForgeGas.Cloud.Hang;
                else if (c == "drift" || c == "roll" || c == "move")
                    cfg.cloud = ForgeGas.Cloud.Drift;
                else if (c == "single" || c == "burst" || c == "grenade" ||
                         c == "impact")
                    cfg.cloud = ForgeGas.Cloud.Single;
                else
                    cfg.cloud = ForgeGas.Cloud.Grow;

                cfg.radius = Mathf.Max(0.1f, (float?)o["radius"] ?? 1.6f);
                cfg.growTo = Mathf.Max(0.1f, (float?)o["growTo"] ?? 3.2f);
                cfg.growTime = Mathf.Max(0.01f, (float?)o["growTime"] ?? 1.2f);
                cfg.duration = Mathf.Max(0.1f, (float?)o["duration"] ?? 4f);
                cfg.fade = Mathf.Max(0f, (float?)o["fade"] ?? 0.6f);
                cfg.drift = Mathf.Max(0f, (float?)o["drift"] ?? 1.2f);
                cfg.driftDamping =
                    Mathf.Max(0f, (float?)o["driftDamping"] ?? 0.4f);
                cfg.max = Mathf.Max(0, (int?)o["max"] ?? 24);

                cfg.burnPerSecond =
                    Mathf.Max(0f, (float?)o["burnPerSecond"] ?? 0f);
                cfg.damagePerSecond =
                    Mathf.Max(0f, (float?)o["damagePerSecond"] ?? 0f);
                cfg.slow = Mathf.Clamp01((float?)o["slow"] ?? 0f);
                cfg.push = (float?)o["push"] ?? 0f;

                cfg.extinguish = Mathf.Max(0f, (float?)o["extinguish"] ?? 0f);
                cfg.extinguishTerrain =
                    Mathf.Max(0f, (float?)o["extinguishTerrain"] ?? 0f);
                cfg.igniteTerrain =
                    Mathf.Max(0f, (float?)o["igniteTerrain"] ?? 0f);

                cfg.puffGravity = (float?)o["puffGravity"] ?? -999f;
                cfg.puffDrag = (float?)o["puffDrag"] ?? -999f;
                cfg.puffPierceTerrain =
                    (bool?)o["puffPierceTerrain"] ?? false;

                cfg.hitEnemies = (bool?)o["hitEnemies"] ?? true;
                cfg.hitPlayer = (bool?)o["hitPlayer"] ?? false;
                cfg.tickRate = Mathf.Max(0.02f, (float?)o["tickRate"] ?? 0.25f);

                cfg.puffs = Mathf.Clamp((int?)o["puffs"] ?? 5, 1, 24);
                cfg.swirl = (float?)o["swirl"] ?? 25f;
                cfg.sprite = (string)o["sprite"] ?? "";

                Color col = cfg.color;

                if (ReadColor((string)o["color"], "gas \"color\"",
                              fileName, ref col))
                {
                    cfg.color = col;
                }

                cfg.sfxGuid = ResolveChargeSfx(
                    (string)o["sfx"], weapon.shootSfx, false, "sfx", fileName);

                // ---- the trail ----
                cfg.trail = (bool?)o["trail"] ?? false;

                string tm = ((string)o["trailMode"] ?? "distance")
                    .Trim().ToLowerInvariant();

                if (tm == "time" || tm == "seconds" || tm == "interval")
                    cfg.trailMode = ForgeGas.TrailTime;
                else if (tm == "both" || tm == "either")
                    cfg.trailMode = ForgeGas.TrailBoth;
                else
                    cfg.trailMode = ForgeGas.TrailDistance;

                // Floored, not clamped silently: below about a third of a unit
                // the clouds overlap so hard that the spacing stops meaning
                // anything and the cost climbs for no visible gain.
                cfg.trailEvery = Mathf.Max(0.3f, (float?)o["trailEvery"] ?? 1.2f);
                cfg.trailInterval =
                    Mathf.Max(0.02f, (float?)o["trailInterval"] ?? 0.15f);
                cfg.trailRadius =
                    Mathf.Max(0.1f, (float?)o["trailRadius"] ?? 0.9f);
                cfg.trailDuration =
                    Mathf.Max(0.1f, (float?)o["trailDuration"] ?? 2f);
                cfg.trailPuffs = Mathf.Clamp((int?)o["trailPuffs"] ?? 2, 1, 24);
                cfg.trailMax = Mathf.Clamp((int?)o["trailMax"] ?? 40, 1, 200);
                cfg.bloomOnDeath = (bool?)o["bloomOnDeath"] ?? true;
            }

            if (!cfg.DoesAnything)
            {
                Log.LogWarning(
                    fileName + ": gas - the cloud has no payload, so it is " +
                    "just a coloured puff. Set at least one of " +
                    "\"burnPerSecond\", \"damagePerSecond\", \"slow\", " +
                    "\"push\", \"extinguish\" or \"extinguishTerrain\". " +
                    "Leaving it on anyway in case that is what you wanted.");
            }

            // Burn alone can be a trap, so say it once rather than letting it
            // read as a broken weapon: burn damage in this game is FLAT (one
            // per second while alight, whatever the level), enemies shed it at
            // only 1/second, and one enemy is outright fireproof.
            if (cfg.burnPerSecond > 0f && cfg.damagePerSecond <= 0f)
            {
                Log.LogInfo(
                    fileName + ": gas - burn-only cloud. Note burn damage is " +
                    "flat (1/sec while alight) no matter how high the level " +
                    "goes, so this buys DURATION rather than intensity - and " +
                    "Enemy_Cross Variant Tablet is fireproof, so a little " +
                    "\"damagePerSecond\" is worth adding.");
            }

            if (cfg.extinguishTerrain > 0f && cfg.extinguishTerrain < 45f)
            {
                Log.LogWarning(
                    fileName + ": gas \"extinguishTerrain\": " +
                    cfg.extinguishTerrain.ToString("0.#") +
                    " is probably too small to put out the main terrain. Cell " +
                    "fire thresholds are not normalised - CellType_Solid " +
                    "ignites at 45 and is topped up by every burning " +
                    "neighbour, while Slime is 1 and Moss 2.5. Anything under " +
                    "45 will douse the soft stuff and lose the race against " +
                    "Solid. 120+ clears it.");
            }

            if (cfg.extinguishTerrain > 0f && cfg.igniteTerrain > 0f)
            {
                Log.LogWarning(
                    fileName + ": gas sets terrain alight AND puts it out at " +
                    "the same time. They cancel; whichever is larger wins by " +
                    "the difference.");
            }

            if (cfg.burnPerSecond > 0f && cfg.extinguish > 0f)
            {
                Log.LogWarning(
                    fileName + ": gas - this cloud both sets things alight " +
                    "and puts them out. They fight, and whichever is bigger " +
                    "wins by the difference. Probably not what you meant.");
            }

            if (cfg.trail)
            {
                // OVERLAP STACKS. Each cloud runs its own damage query on its
                // own tick, so two clouds covering the same enemy hit it twice.
                // At spacing below the trail radius the line is continuously
                // double-dipping, which is the balance trap here and is not
                // visible in game - it just feels wrong.
                // Only worth saying when there is damage to stack. A smoke
                // wall WANTS its clouds to overlap - that is what makes it a
                // wall rather than a dotted line - and warning about it there
                // would be noise.
                bool stacks = cfg.damagePerSecond > 0f || cfg.burnPerSecond > 0f;

                if (stacks && cfg.trailEvery < cfg.trailRadius)
                {
                    Log.LogWarning(
                        fileName + ": gas trail - \"trailEvery\" (" +
                        cfg.trailEvery.ToString("0.##") +
                        ") is smaller than \"trailRadius\" (" +
                        cfg.trailRadius.ToString("0.##") +
                        "), so the clouds overlap and their damage STACKS - " +
                        "anything standing in the line takes roughly " +
                        (cfg.trailRadius * 2f / cfg.trailEvery)
                            .ToString("0.#") +
                        "x what one cloud does. Fine if that is the plan; set " +
                        "trailEvery to about 2x trailRadius for an even line.");
                }

                // Cost, stated in the units that actually bite: sprites.
                float perSecond = weapon.fireRate * weapon.projectileCount;

                if (perSecond > 0f)
                {
                    Log.LogInfo(
                        fileName + ": gas trail - at fireRate " +
                        weapon.fireRate.ToString("0.##") + " x " +
                        weapon.projectileCount.ToString("0.##") +
                        " projectile(s) this lays a cloud roughly every " +
                        cfg.trailEvery.ToString("0.##") +
                        " units per shot, capped at " + cfg.trailMax +
                        " live clouds x " + cfg.trailPuffs + " puffs = " +
                        (cfg.trailMax * cfg.trailPuffs) +
                        " sprites. Lower \"trailMax\" if it costs frames.");
                }

                if (!cfg.bloomOnDeath)
                {
                    Log.LogInfo(
                        fileName + ": gas - \"bloomOnDeath\" is off, so this " +
                        "weapon leaves ONLY the trail: no cloud where the shot " +
                        "finally dies.");
                }

                if (cfg.cloud == ForgeGas.Cloud.Single)
                {
                    Log.LogInfo(
                        fileName + ": gas - \"single\" describes one cloud " +
                        "where the shot lands, which a trail cannot be, so the " +
                        "TRAIL clouds use \"grow\" instead. The end cloud is " +
                        "still a single bloom.");
                }
            }

            bool physics = pw.usePhysics;

            GameObject clone = VisualCustomizer.ClonePrefab(
                physics
                    ? pw.physicsProjectilePrefab.gameObject
                    : pw.projectilePrefab.gameObject);

            Component comp = physics
                ? (Component)clone.GetComponentInChildren<PhysicsProjectile>(true)
                : clone.GetComponentInChildren<Projectile>(true);

            if (comp == null)
            {
                Log.LogWarning(
                    fileName + ": gas - no " +
                    (physics ? "PhysicsProjectile" : "Projectile") +
                    " on the prefab. Ignored.");
                UnityEngine.Object.Destroy(clone);
                return;
            }

            var src = comp.GetComponent<ForgeGasSource>();

            if (src == null)
                src = comp.gameObject.AddComponent<ForgeGasSource>();

            // Both, and the ID is the one that matters: Unity serializes the int
            // into every clone the game fires, and drops the reference. See
            // ForgeConfigTable.
            src.cfg = cfg;
            src.cfgId = ForgeConfigTable.Id(cfg);

            // The trail's clouds are a DERIVED config with its own budget, so
            // a long line can never starve the impact cloud - and its own id,
            // because a reference on a prefab-attached component arrives null.
            if (cfg.trail)
            {
                cfg.trailCfg = cfg.DeriveTrail();
                src.trailCfg = cfg.trailCfg;
                src.trailCfgId = ForgeConfigTable.Id(cfg.trailCfg);
            }
            else
            {
                src.trailCfg = null;
                src.trailCfgId = -1;
            }

            // "The gravity pulls all the gas down and it gets destroyed by
            // terrain, so we cannot build up a big cloud" - both of those are
            // properties of the PUFF, so they are fixed on the prefab here
            // rather than on the cloud.
            if (cfg.puffGravity > -998f || cfg.puffDrag > -998f)
            {
                foreach (Rigidbody2D body in
                         clone.GetComponentsInChildren<Rigidbody2D>(true))
                {
                    if (cfg.puffGravity > -998f)
                        body.gravityScale = cfg.puffGravity;

                    if (cfg.puffDrag > -998f)
                        body.linearDamping = Mathf.Max(0f, cfg.puffDrag);
                }

                Log.LogInfo(
                    fileName + ": gas - puff physics overridden" +
                    (cfg.puffGravity > -998f
                        ? (", gravityScale " + cfg.puffGravity.ToString("0.##"))
                        : "") +
                    (cfg.puffDrag > -998f
                        ? (", damping " + cfg.puffDrag.ToString("0.##"))
                        : "") + ".");
            }

            if (cfg.puffPierceTerrain)
            {
                // ProjectileImpactBehaviour is a value type here, so this is
                // the copy-modify-assign the rest of the builder uses - never
                // mutate in place, or every weapon sharing the template
                // changes with it.
                ProjectileImpactBehaviour ib = pw.impactBehaviour;

                if (ib.enabled)
                {
                    ib.enabled = false;
                    pw.impactBehaviour = ib;
                }

                // With impact off, a puff would otherwise live forever - the
                // same trap the harpoon has. Make sure something still ends it.
                ProjectileLifetimeData lt = pw.lifetimeData;

                if (!lt.enabled)
                {
                    lt.enabled = true;
                    lt.time = (lt.time > 0f) ? lt.time : 2f;
                    pw.lifetimeData = lt;

                    Log.LogWarning(
                        fileName + ": gas \"puffPierceTerrain\" switches the " +
                        "puff's impact behaviour off so terrain cannot eat it - " +
                        "but this weapon had no lifetime either, so the puffs " +
                        "would never die. lifetimeData has been switched on at " +
                        lt.time.ToString("0.#") + "s.");
                }

                Log.LogInfo(
                    fileName + ": gas - puffs pass through terrain" +
                    (cfg.igniteTerrain > 0f
                        ? " and the cloud sets it alight instead."
                        : " (they no longer set it alight on contact - use " +
                          "\"igniteTerrain\" if you want that)."));
            }

            if (physics)
                pw.physicsProjectilePrefab = (PhysicsProjectile)comp;
            else
                pw.projectilePrefab = (Projectile)comp;

            ResolveGasArt(weapon, cfg, fileName);

            Log.LogInfo(
                fileName + ": gas - " + cfg.cloud + " cloud, radius " +
                cfg.radius.ToString("0.##") +
                (cfg.cloud == ForgeGas.Cloud.Grow
                    ? (" -> " + cfg.growTo.ToString("0.##"))
                    : "") +
                ", " + cfg.duration.ToString("0.#") + "s" +
                (cfg.burnPerSecond > 0f
                    ? (", burn " + cfg.burnPerSecond.ToString("0.#") + "/s")
                    : "") +
                (cfg.damagePerSecond > 0f
                    ? (", damage " + cfg.damagePerSecond.ToString("0.#") + "/s")
                    : "") +
                (cfg.slow > 0f ? ", slows" : "") +
                (cfg.push != 0f ? ", pushes" : "") +
                (cfg.extinguish > 0f ? ", puts units out" : "") +
                (cfg.extinguishTerrain > 0f ? ", puts terrain out" : "") +
                (cfg.igniteTerrain > 0f ? ", sets terrain alight" : "") +
                (cfg.hitPlayer ? ", HURTS YOU TOO" : "") + ".");
        }

        private static void ResolveGasArt(
            WeaponData weapon, ForgeGas.Config cfg, string fileName)
        {
            SpriteRenderer donor = null;

            var pw = weapon as ProjectileWeaponData;

            if (pw != null)
            {
                GameObject prefab = pw.usePhysics
                    ? (pw.physicsProjectilePrefab != null
                        ? pw.physicsProjectilePrefab.gameObject : null)
                    : (pw.projectilePrefab != null
                        ? pw.projectilePrefab.gameObject : null);

                if (prefab != null)
                    donor = prefab.GetComponentInChildren<SpriteRenderer>(true);
            }

            if (!string.IsNullOrEmpty(cfg.sprite))
            {
                ForgeSpriteLibrary.LoadAll();

                ForgeSpriteLibrary.Art custom;

                if (ForgeSpriteLibrary.TryGetArt(cfg.sprite, out custom) &&
                    custom.sprite != null)
                {
                    cfg.art = custom.sprite;
                }
                else
                {
                    string known = ForgeSpriteLibrary.Count > 0
                        ? " Loaded custom art: " +
                          string.Join(", ", ForgeSpriteLibrary.Names) + "."
                        : " There is no art in the sprites folder yet.";

                    Log.LogWarning(
                        fileName + ": gas \"sprite\": '" + cfg.sprite +
                        "' is not one of your sprites - the soft puff is " +
                        "drawn for you instead." + known);
                }
            }

            // Borrow the sorting from the weapon's own bullet so a cloud never
            // renders behind the terrain it is sitting on.
            if (donor != null)
            {
                cfg.material = donor.sharedMaterial;
                cfg.sortingLayerId = donor.sortingLayerID;
                cfg.sortingOrder = donor.sortingOrder - 1;
            }
        }

        // ------------------------------------------------------------------
        // split - a shot bursts into several, and the fragments INHERIT
        // ------------------------------------------------------------------
        //
        // Sits beside "subEmitter" rather than replacing it, deliberately. A
        // sub-emitter is a separate weapon built with a null module cluster
        // (WeaponFactory.Create passes null literally), never equipped to
        // anyone, fired straight up - so its damage can never be upgraded, its
        // kills credit nobody, and its spread fans around world-up. Weapons
        // already rely on that, so it is left alone. This key is the one that
        // inherits.
        private static void ApplySplit(
            WeaponData weapon,
            JObject root,
            string fileName)
        {
            JToken token = root["split"];

            if (token == null || token.Type == JTokenType.Null)
                return;

            var o = token as JObject;

            if (o == null)
            {
                if (token.Type != JTokenType.Boolean || !(bool)token)
                {
                    Log.LogWarning(
                        fileName + ": \"split\" should be true or an object - " +
                        "ignored.");
                    return;
                }
            }
            else if (!((bool?)o["enabled"] ?? true))
            {
                return;
            }

            var pw = weapon as ProjectileWeaponData;

            if (!ProjectileTargetOk(pw, "split", fileName, true, null))
                return;

            var cfg = new ForgeSplit.Config();

            if (o != null)
            {
                ReadSplitTriggers(o["on"], cfg, fileName);

                cfg.count = Mathf.Clamp((int?)o["count"] ?? 3, 1, 16);
                cfg.spread = Mathf.Clamp((float?)o["spread"] ?? 60f, 0f, 360f);
                cfg.speed = Mathf.Max(0.05f, (float?)o["speed"] ?? 1f);
                cfg.scale = Mathf.Max(0.05f, (float?)o["scale"] ?? 1f);
                cfg.delay = Mathf.Max(0.02f, (float?)o["delay"] ?? 0.5f);
                cfg.distance = Mathf.Max(0.1f, (float?)o["distance"] ?? 6f);
                cfg.range = Mathf.Max(0.5f, (float?)o["range"] ?? 12f);
                cfg.targetObjects = (bool?)o["targetObjects"] ?? true;
                cfg.lockOn = (bool?)o["lockOn"] ?? true;
                cfg.lockOnTurnRate =
                    Mathf.Max(1f, (float?)o["lockOnTurnRate"] ?? 220f);

                // A depth of 3 with a count of 16 is already 4368 fragments
                // from one trigger pull, so the cap is low on purpose and the
                // per-frame ceiling in ForgeSplit backs it up.
                cfg.generations = Mathf.Clamp((int?)o["generations"] ?? 1, 1, 3);
                cfg.maxPerShot = Mathf.Clamp((int?)o["maxPerShot"] ?? 8, 1, 64);
                cfg.consumeParent = !((bool?)o["keepParent"] ?? false);

                cfg.weaponName = ((string)o["weapon"] ?? "").Trim();

                cfg.inheritDamage = (bool?)o["inheritDamage"] ?? true;
                cfg.inheritBurn = (bool?)o["inheritBurn"] ?? true;
                cfg.inheritExplosion = (bool?)o["inheritExplosion"] ?? true;
                cfg.inheritDischarge = (bool?)o["inheritDischarge"] ?? true;

                ReadSplitDamage(o["damage"], cfg, fileName);
                ReadSplitAim(o["aim"], cfg, fileName);
            }

            if (cfg.on.Count == 0)
            {
                // Said nothing at all -> the familiar cluster-bomb reading.
                // Said something that came to nothing -> that is a mistake
                // worth naming, because the weapon would look merely broken.
                JToken onToken = (o != null) ? o["on"] : null;

                if (onToken != null && onToken.Type != JTokenType.Null)
                {
                    Log.LogWarning(
                        fileName + ": split \"on\" listed no usable trigger, " +
                        "so it could never fire. Falling back to \"death\".");
                }

                cfg.on.Add(ForgeSplit.OnDeath);
            }

            // A weapon that splits into ITSELF multiplies without bound - and
            // unlike the turret's version of this guard, the generation cap
            // would not save it, because a named weapon's fragments carry no
            // splitter to count generations with.
            if (!cfg.CopiesParent && string.Equals(
                    cfg.weaponName, weapon.name,
                    System.StringComparison.OrdinalIgnoreCase))
            {
                Log.LogWarning(
                    fileName + ": split \"weapon\" is this weapon itself - " +
                    "ignoring the name and splitting into copies of the shot " +
                    "instead, which is what it would have looked like anyway " +
                    "and cannot run away.");
                cfg.weaponName = "";
            }

            // "hit" means "shed fragments and KEEP GOING". Without piercing the
            // shot is destroyed by its first contact, so the trigger would
            // silently become a worse version of "death". Same reasoning as
            // chain, which forces it on for the same reason.
            if (cfg.on.Contains(ForgeSplit.OnHit) && !pw.usePhysics)
            {
                var pd = pw.piercingData;

                if (!pd.enabled)
                {
                    pd.enabled = true;

                    if (pd.damageRepeatDelay <= 0f)
                        pd.damageRepeatDelay = 0.3f;

                    pw.piercingData = pd;

                    Log.LogInfo(
                        fileName + ": split \"hit\" needs the shot to survive " +
                        "the hit, so piercing has been switched on for it " +
                        "(damageRepeatDelay " +
                        pd.damageRepeatDelay.ToString("0.##") + "s).");
                }
            }

            if (cfg.on.Contains(ForgeSplit.OnHit) &&
                cfg.on.Contains(ForgeSplit.OnDeath))
            {
                Log.LogInfo(
                    fileName + ": split has both \"hit\" and \"death\" on, so " +
                    "one shot bursts at each enemy it passes through AND " +
                    "again when it finally dies. That is allowed and is a " +
                    "real weapon - just be aware it is two bursts, and that " +
                    "\"maxPerShot\" (" + cfg.maxPerShot + ") is the shared " +
                    "budget for both.");
            }

            // slowDown is a real surprise on a death split and it is ON in the
            // stock White Popper, which is what most weapons clone. The shot
            // coasts to a dead STOP exactly at its range and is destroyed
            // there, so the burst happens at the stopping point rather than
            // "where the bullet was going", and the fragments inherit the same
            // deceleration. The heading is now taken from the last frame the
            // shot was actually moving, so the fan points the right way either
            // way - but say what is happening, because "my fragments crawl" is
            // this and nothing else.
            if (cfg.on.Contains(ForgeSplit.OnDeath) && !pw.usePhysics &&
                pw.rangeData.enabled && pw.rangeData.slowDown)
            {
                Log.LogInfo(
                    fileName + ": split - this weapon has " +
                    "\"rangeData.slowDown\", so the shot coasts to a full stop " +
                    "at its range before it dies. The fan still points the way " +
                    "the shot was travelling, but the fragments inherit the " +
                    "deceleration and will slow down over their own range too. " +
                    "Set \"rangeData\": { \"slowDown\": false } for fragments " +
                    "that keep their speed.");
            }

            if (cfg.on.Contains(ForgeSplit.OnBounce) && !pw.usePhysics &&
                !pw.projectileBounceData.enabled)
            {
                Log.LogWarning(
                    fileName + ": split \"bounce\" is on but this weapon never " +
                    "bounces - set \"projectileBounceData.enabled\" (or use " +
                    "the bounce block on a lobbed weapon), or the trigger can " +
                    "never fire.");
            }

            bool physics = pw.usePhysics;

            GameObject clone = VisualCustomizer.ClonePrefab(
                physics
                    ? pw.physicsProjectilePrefab.gameObject
                    : pw.projectilePrefab.gameObject);

            Component comp = physics
                ? (Component)clone.GetComponentInChildren<PhysicsProjectile>(true)
                : clone.GetComponentInChildren<Projectile>(true);

            if (comp == null)
            {
                Log.LogWarning(
                    fileName + ": split - no " +
                    (physics ? "PhysicsProjectile" : "Projectile") +
                    " on the prefab. Ignored.");
                UnityEngine.Object.Destroy(clone);
                return;
            }

            var splitter = comp.GetComponent<ForgeSplitter>();

            if (splitter == null)
                splitter = comp.gameObject.AddComponent<ForgeSplitter>();

            // Both, and the ID is the one that matters: Unity serializes the
            // int into every clone the game fires and drops the reference. See
            // ForgeConfigTable.
            splitter.cfg = cfg;
            splitter.cfgId = ForgeConfigTable.Id(cfg);
            splitter.generation = 0;

            // The prefab a fragment is built from is THIS one, so a fragment
            // arrives carrying the same art, the same trail and the same
            // splitter. It is held on the config rather than on the component
            // because Unity remaps in-hierarchy references when it copies, and
            // a self-reference on the component would come back on each clone
            // pointing at the clone.
            if (physics)
                cfg.sourcePhysicsPrefab = comp as PhysicsProjectile;
            else
                cfg.sourcePrefab = comp as Projectile;

            if (physics)
                pw.physicsProjectilePrefab = comp as PhysicsProjectile;
            else
                pw.projectilePrefab = comp as Projectile;

            ForgeSplit.Arm();

            Log.LogInfo(
                fileName + ": split (" + string.Join("+", Triggers(cfg)) +
                ", " + cfg.count + " x " + cfg.DamageWord + " damage, " +
                cfg.AimWord +
                (cfg.CopiesParent
                    ? ", copies of the shot"
                    : ", from '" + cfg.weaponName + "'") +
                (cfg.generations > 1
                    ? ", " + cfg.generations + " generations"
                    : "") + ")");
        }

        private static string[] Triggers(ForgeSplit.Config cfg)
        {
            var list = new List<string>(cfg.on);
            list.Sort(System.StringComparer.OrdinalIgnoreCase);
            return list.ToArray();
        }

        // "on" takes one name or a list of them. Unknown names are reported
        // rather than dropped, because a typo here is a weapon that simply
        // never splits and gives no other sign of it.
        private static void ReadSplitTriggers(
            JToken token, ForgeSplit.Config cfg, string fileName)
        {
            if (token == null || token.Type == JTokenType.Null)
                return;

            var names = new List<string>();

            if (token.Type == JTokenType.Array)
            {
                foreach (JToken t in token)
                {
                    string s = (string)t;

                    if (!string.IsNullOrEmpty(s))
                        names.Add(s);
                }
            }
            else
            {
                string s = (string)token;

                if (!string.IsNullOrEmpty(s))
                {
                    // "death, hit" is what people type, so accept it.
                    foreach (string part in s.Split(',', '+', '|', ' '))
                    {
                        if (!string.IsNullOrEmpty(part.Trim()))
                            names.Add(part.Trim());
                    }
                }
            }

            for (int i = 0; i < names.Count; i++)
            {
                string n = names[i].Trim().ToLowerInvariant();

                switch (n)
                {
                    case "death":
                    case "die":
                    case "end":
                    case "impact":
                        cfg.on.Add(ForgeSplit.OnDeath);
                        break;

                    case "time":
                    case "delay":
                    case "timer":
                    case "fuse":
                        cfg.on.Add(ForgeSplit.OnTime);
                        break;

                    case "distance":
                    case "range":
                    case "travel":
                        cfg.on.Add(ForgeSplit.OnDistance);
                        break;

                    case "hit":
                    case "enemy":
                    case "pierce":
                        cfg.on.Add(ForgeSplit.OnHit);
                        break;

                    case "bounce":
                    case "ricochet":
                    case "reflect":
                        cfg.on.Add(ForgeSplit.OnBounce);
                        break;

                    default:
                        Log.LogWarning(
                            fileName + ": split \"on\" - '" + names[i] +
                            "' is not a trigger this build knows. Valid ones " +
                            "are death, time, distance, hit, bounce. Ignored.");
                        break;
                }
            }
        }

        // "damage" is either a word or a plain multiplier.
        private static void ReadSplitDamage(
            JToken token, ForgeSplit.Config cfg, string fileName)
        {
            if (token == null || token.Type == JTokenType.Null)
                return;

            if (token.Type == JTokenType.Float ||
                token.Type == JTokenType.Integer)
            {
                cfg.damageMode = ForgeSplit.DamageCustom;
                cfg.damageMultiplier = Mathf.Max(0f, (float)token);
                return;
            }

            string s = ((string)token ?? "").Trim().ToLowerInvariant();

            switch (s)
            {
                case "full":
                case "same":
                case "inherit":
                    cfg.damageMode = ForgeSplit.DamageFull;
                    break;

                case "share":
                case "divide":
                case "split":
                    cfg.damageMode = ForgeSplit.DamageShare;
                    break;

                case "half":
                case "":
                    cfg.damageMode = ForgeSplit.DamageHalf;
                    break;

                default:
                    {
                        float n;

                        if (float.TryParse(s, out n))
                        {
                            cfg.damageMode = ForgeSplit.DamageCustom;
                            cfg.damageMultiplier = Mathf.Max(0f, n);
                        }
                        else
                        {
                            Log.LogWarning(
                                fileName + ": split \"damage\": '" + s +
                                "' is not one of full / half / share or a " +
                                "number - using half.");
                            cfg.damageMode = ForgeSplit.DamageHalf;
                        }
                    }
                    break;
            }
        }

        private static void ReadSplitAim(
            JToken token, ForgeSplit.Config cfg, string fileName)
        {
            string s = ((string)token ?? "fan").Trim().ToLowerInvariant();

            switch (s)
            {
                case "target":
                case "enemy":
                case "seek":
                    cfg.aim = ForgeSplit.AimTarget;
                    break;

                case "auto":
                case "targetorfan":
                case "either":
                    cfg.aim = ForgeSplit.AimAuto;
                    break;

                case "fan":
                case "cone":
                case "spread":
                case "":
                    cfg.aim = ForgeSplit.AimFan;
                    break;

                default:
                    Log.LogWarning(
                        fileName + ": split \"aim\": '" + s + "' is not fan / " +
                        "target / auto - using fan.");
                    cfg.aim = ForgeSplit.AimFan;
                    break;
            }
        }

        // ------------------------------------------------------------------
        // shield - a held arc near the ship, and the ram version of it
        // ------------------------------------------------------------------
        //
        // Unlike every other feature here this one hangs nothing on the
        // projectile prefab: a shield weapon may have no projectile at all. It
        // registers a config against the WeaponData and ForgeShieldPatch picks
        // it up from the Shooter, the same way orbit does.
        private static void ApplyShield(
            WeaponData weapon,
            JObject root,
            bool isGadget,
            string fileName)
        {
            JToken token = root["shield"];

            if (token == null || token.Type == JTokenType.Null)
                return;

            var o = token as JObject;

            if (o == null)
            {
                if (token.Type != JTokenType.Boolean || !(bool)token)
                {
                    Log.LogWarning(
                        fileName + ": \"shield\" should be true or an " +
                        "object - ignored.");
                    return;
                }
            }
            else if (!((bool?)o["enabled"] ?? true))
            {
                return;
            }

            var cfg = new ForgeShield.Config();

            if (o != null)
            {
                string mode =
                    ((string)o["mode"] ?? "arc").Trim().ToLowerInvariant();

                if (mode == "ram" || mode == "dash" || mode == "charge")
                    cfg.mode = ForgeShield.Mode.Ram;
                else if (mode == "both" || mode == "all")
                    cfg.mode = ForgeShield.Mode.Both;
                else
                    cfg.mode = ForgeShield.Mode.Arc;

                string act =
                    ((string)o["activation"] ?? "hold")
                        .Trim().ToLowerInvariant();

                if (act == "toggle")
                    cfg.activation = ForgeShield.Activation.Toggle;
                else if (act == "always" || act == "passive" || act == "on")
                    cfg.activation = ForgeShield.Activation.Always;
                else
                    cfg.activation = ForgeShield.Activation.Hold;

                string face =
                    ((string)o["facing"] ?? "auto").Trim().ToLowerInvariant();

                if (face == "aim" || face == "cursor" || face == "crosshair")
                    cfg.facing = ForgeShield.Facing.Aim;
                else if (face == "travel" || face == "movement" ||
                         face == "velocity")
                    cfg.facing = ForgeShield.Facing.Travel;
                else if (face == "frozen" || face == "fixed" || face == "lock")
                    cfg.facing = ForgeShield.Facing.Frozen;
                else
                    cfg.facing = ForgeShield.Facing.Auto;

                cfg.angle = Mathf.Clamp((float?)o["angle"] ?? 40f, 1f, 360f);
                cfg.radius = Mathf.Max(0.2f, (float?)o["radius"] ?? 2.2f);
                cfg.thickness =
                    Mathf.Max(0.05f, (float?)o["thickness"] ?? 0.6f);

                string blk =
                    ((string)o["onBlock"] ?? "reflect")
                        .Trim().ToLowerInvariant();

                if (blk == "destroy" || blk == "stop" || blk == "block")
                    cfg.onBlock = ForgeShield.OnBlock.Destroy;
                else if (blk == "reflectatnearest" || blk == "nearest" ||
                         blk == "aimed" || blk == "target")
                    cfg.onBlock = ForgeShield.OnBlock.ReflectAtNearest;
                else if (blk == "reflectatcursor" || blk == "cursor" ||
                         blk == "aim" || blk == "crosshair" || blk == "mouse")
                    cfg.onBlock = ForgeShield.OnBlock.ReflectAtCursor;
                else if (blk == "reflectmirror" || blk == "mirror" ||
                         blk == "bounce" || blk == "surface" || blk == "angle")
                    cfg.onBlock = ForgeShield.OnBlock.ReflectMirror;
                else if (blk == "absorb" || blk == "drain" || blk == "eat")
                    cfg.onBlock = ForgeShield.OnBlock.Absorb;
                else
                    cfg.onBlock = ForgeShield.OnBlock.Reflect;

                cfg.reflectDamage =
                    Mathf.Max(0f, (float?)o["reflectDamage"] ?? 1f);
                cfg.reflectSpeed =
                    Mathf.Max(0.05f, (float?)o["reflectSpeed"] ?? 1f);
                cfg.aimRange = Mathf.Max(1f, (float?)o["aimRange"] ?? 25f);
                cfg.blockBeams = (bool?)o["blockBeams"] ?? true;
                cfg.maxPerSecond = Mathf.Max(0, (int?)o["maxPerSecond"] ?? 0);

                // "cost" means Cost * FireRate - what this weapon would spend
                // per second with the trigger simply held down. Same idiom as
                // orbitHoldDrainPerSecond.
                JToken drain = o["drainPerSecond"];

                if (drain != null && drain.Type != JTokenType.Null)
                {
                    string raw = drain.ToString().Trim();

                    if (raw.Equals("cost", StringComparison.OrdinalIgnoreCase))
                    {
                        cfg.drainFromCost = true;
                    }
                    else
                    {
                        float parsed;

                        if (float.TryParse(
                                raw,
                                System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo
                                    .InvariantCulture,
                                out parsed))
                        {
                            cfg.drainPerSecond = Mathf.Max(0f, parsed);
                        }
                        else
                        {
                            Log.LogWarning(
                                fileName + ": shield \"drainPerSecond\" " +
                                "should be a number or \"cost\" - ignored.");
                        }
                    }
                }

                cfg.costPerBlock =
                    Mathf.Max(0f, (float?)o["costPerBlock"] ?? 0f);
                cfg.effectiveness =
                    Mathf.Max(0f, (float?)o["effectiveness"] ?? 0f);
                cfg.resource = (string)o["resource"] ?? "";

                string trig =
                    ((string)o["ramTrigger"] ?? "either")
                        .Trim().ToLowerInvariant();

                if (trig == "dash" || trig == "tap")
                    cfg.ramTrigger = ForgeShield.RamTrigger.Dash;
                else if (trig == "sprint" || trig == "boost" ||
                         trig == "hold")
                    cfg.ramTrigger = ForgeShield.RamTrigger.Sprint;
                else
                    cfg.ramTrigger = ForgeShield.RamTrigger.Either;

                cfg.ramDamage = Mathf.Max(0f, (float?)o["ramDamage"] ?? 8f);
                cfg.ramSprintMultiplier =
                    Mathf.Max(0f, (float?)o["ramSprintMultiplier"] ?? 0.4f);
                cfg.ramKnockback =
                    Mathf.Max(0f, (float?)o["ramKnockback"] ?? 30f);
                cfg.ramSpeedThreshold =
                    Mathf.Max(0f, (float?)o["ramSpeedThreshold"] ?? 6f);
                cfg.ramRepeatDelay =
                    Mathf.Max(0.02f, (float?)o["ramRepeatDelay"] ?? 0.4f);
                cfg.ramSelfProtect = (bool?)o["ramSelfProtect"] ?? true;
                cfg.ramIFrames = Mathf.Max(0f, (float?)o["ramIFrames"] ?? 0f);
                cfg.ramDashWindow =
                    Mathf.Max(0f, (float?)o["ramDashWindow"] ?? 0f);

                cfg.appearFade =
                    Mathf.Max(0f, (float?)o["appearFade"] ?? 0.12f);
                cfg.flashOnBlock = (bool?)o["flashOnBlock"] ?? true;
                cfg.sprite = (string)o["sprite"] ?? "";

                Color c = cfg.color;

                if (ReadColor((string)o["color"], "shield \"color\"",
                              fileName, ref c))
                {
                    cfg.color = c;
                }

                cfg.blockSfx = ResolveChargeSfx(
                    (string)o["blockSfx"], weapon.shootSfx, false,
                    "blockSfx", fileName);

                cfg.ramSfx = ResolveChargeSfx(
                    (string)o["ramSfx"], weapon.shootSfx, false,
                    "ramSfx", fileName);

                cfg.raiseSfx = ResolveChargeSfx(
                    (string)o["raiseSfx"], weapon.shootSfx, false,
                    "raiseSfx", fileName);
            }

            // A gadget never reaches Shooter at all - WeaponBasedActiveModule
            // calls weapon.Fire(new FakeBarrel(...)) directly - so a held arc
            // has nothing to hold. Say so rather than shipping a module that
            // quietly does nothing.
            if (isGadget && cfg.WantsArc)
            {
                Log.LogWarning(
                    fileName + ": shield - a held arc cannot work on a " +
                    "GADGET module. Gadgets bypass Shooter entirely (they " +
                    "call Fire() directly), so there is no held trigger to " +
                    "raise it with. Make this a normal weapon, or use " +
                    "\"mode\": \"ram\", which is driven by your dash and " +
                    "works either way.");

                if (!cfg.WantsRam)
                    return;

                cfg.mode = ForgeShield.Mode.Ram;
            }

            ResolveShieldArt(cfg, fileName);
            ResolveShieldResource(cfg, fileName);

            // The arc catches enemy fire, and enemy shots are invisible to
            // Physics2D - their Collider2D is disabled on the prefab. Tracking
            // has to be switched on or the sweep finds nothing at all.
            if (cfg.WantsArc)
                ForgeProjectileTracker.Enabled = true;

            ForgeShield.Register(weapon, cfg);

            Log.LogInfo(
                fileName + ": shield - " + cfg.mode + ", " +
                cfg.angle.ToString("0.#") + " degrees at radius " +
                cfg.radius.ToString("0.##") +
                (cfg.WantsArc
                    ? (", " + cfg.activation + ", on block: " + cfg.onBlock +
                       (cfg.blockBeams ? ", blocks beams" : ""))
                    : "") +
                (cfg.WantsRam
                    ? (", ram on " + cfg.ramTrigger + " for " +
                       cfg.ramDamage.ToString("0.#"))
                    : "") + ".");
        }

        private static void ResolveShieldArt(
            ForgeShield.Config cfg, string fileName)
        {
            if (string.IsNullOrEmpty(cfg.sprite))
                return;

            ForgeSpriteLibrary.LoadAll();

            ForgeSpriteLibrary.Art custom;

            if (ForgeSpriteLibrary.TryGetArt(cfg.sprite, out custom) &&
                custom.sprite != null)
            {
                cfg.art = custom.sprite;
                return;
            }

            string known = ForgeSpriteLibrary.Count > 0
                ? " Loaded custom art: " +
                  string.Join(", ", ForgeSpriteLibrary.Names) + "."
                : " There is no art in the sprites folder yet.";

            Log.LogWarning(
                fileName + ": shield \"sprite\": '" + cfg.sprite +
                "' is not one of your sprites - the arc will be drawn from " +
                "\"angle\", \"radius\" and \"thickness\" instead." + known);
        }

        private static void ResolveShieldResource(
            ForgeShield.Config cfg, string fileName)
        {
            if (string.IsNullOrEmpty(cfg.resource))
                return;

            var res =
                JsonFieldMapper.FindAsset(typeof(Resource), cfg.resource)
                    as Resource;

            if (res != null)
            {
                cfg.resourceAsset = res;
                return;
            }

            Log.LogWarning(
                fileName + ": shield \"resource\": '" + cfg.resource +
                "' is not a resource I know - the weapon's own resourceUsed " +
                "will pay instead.");
        }

        private static string ResolveChargeSfx(
            string name,
            string inheritFrom,
            bool forceLoop,
            string key,
            string fileName)
        {
            if (IsSilence(name))
                return null;

            ForgeSoundLibrary.LoadAll();

            string guid = ForgeSfxRegistry.Resolve(
                name, inheritFrom, forceLoop, fileName);

            if (guid != null)
                return guid;

            if (ForgeSfxRegistry.KnownGuid(name))
            {
                if (forceLoop)
                {
                    Log.LogInfo(
                        fileName + ": charge \"" + key + "\" is the stock " +
                        "sound '" + name + "', which is used as-is. Held " +
                        "sounds are started and stopped by handle, so this " +
                        "only fills the whole hold if that clip already " +
                        "loops - the Laser's warmup " +
                        "(ea8fb19f-4770-4b84-b478-95d30b805d6e) does. A " +
                        "CUSTOM sound is forced to loop, so it always will.");
                }

                return name;
            }

            string known = ForgeSoundLibrary.Count > 0
                ? " Loaded custom sounds: " +
                  string.Join(", ",
                      System.Linq.Enumerable.ToArray(
                          ForgeSoundLibrary.Names)) + "."
                : " No custom sounds are loaded - is there an audio file in " +
                  "the 'sounds' folder next to the DLL?";

            Log.LogWarning(
                fileName + ": charge \"" + key + "\" is set to '" + name +
                "', which is neither a custom sound nor a sound id this game " +
                "has, so it will be SILENT." + known);

            return null;
        }

        // The growing orb at the barrel. Its art defaults to the weapon's own
        // bullet, so it reads as the shot you are building - and its sorting
        // slot is copied from that bullet's renderer, because a sprite drawn
        // into the wrong layer is invisible in a way that looks exactly like a
        // feature that did not run.
        private static void ResolveChargeMeter(
            WeaponData weapon,
            ForgeCharge.Config cfg,
            string fileName)
        {
            SpriteRenderer donor = null;

            var pw = weapon as ProjectileWeaponData;

            if (pw != null)
            {
                GameObject prefab = null;

                if (pw.usePhysics)
                {
                    if (pw.physicsProjectilePrefab != null)
                        prefab = pw.physicsProjectilePrefab.gameObject;
                }
                else if (pw.projectilePrefab != null)
                {
                    prefab = pw.projectilePrefab.gameObject;
                }

                if (prefab != null)
                    donor = prefab.GetComponentInChildren<SpriteRenderer>(true);
            }

            Sprite art = null;

            if (!string.IsNullOrEmpty(cfg.meterSprite))
            {
                ForgeSpriteLibrary.LoadAll();

                ForgeSpriteLibrary.Art custom;

                if (ForgeSpriteLibrary.TryGetArt(cfg.meterSprite, out custom) &&
                    custom.sprite != null)
                {
                    art = custom.sprite;
                }
                else
                {
                    string known = ForgeSpriteLibrary.Count > 0
                        ? " Loaded custom art: " +
                          string.Join(", ", ForgeSpriteLibrary.Names) + "."
                        : " There is no art in the sprites folder yet.";

                    Log.LogWarning(
                        fileName + ": charge \"meterSprite\": '" +
                        cfg.meterSprite + "' is not one of your sprites." +
                        known + " Falling back to the weapon's own bullet.");
                }
            }

            if (art == null && donor != null)
                art = donor.sprite;

            if (art == null)
            {
                cfg.meter = false;

                Log.LogInfo(
                    fileName + ": charge has no meter art to draw with - " +
                    "this weapon has no bullet sprite to borrow (a beam or a " +
                    "minion spawner), so the meter is off. Name your own " +
                    "with charge \"meterSprite\" if you want one.");
                return;
            }

            cfg.meterArt = art;

            if (donor != null)
            {
                cfg.meterMaterial = donor.sharedMaterial;
                cfg.meterSortingLayerId = donor.sortingLayerID;

                // One in front of the bullet, so the orb is never hidden
                // behind a shot that is leaving the barrel at the same time.
                cfg.meterSortingOrder = donor.sortingOrder + 1;
            }
        }

        // A colour field that may be absent. Returns whether one was set, so
        // "not specified" and "specified as white" stay different things.
        private static bool ReadColor(
            string text, string what, string fileName, ref Color into)
        {
            if (string.IsNullOrEmpty(text))
                return false;

            Color parsed;

            if (VisualCustomizer.TryParseColor(text, out parsed))
            {
                into = parsed;
                return true;
            }

            Log.LogWarning(
                fileName + ": " + what + " '" + text + "' is not a colour I " +
                "understand - use #rrggbb or a name like \"orange\". Ignored.");

            return false;
        }

        private static void ApplyBoostability(
            ModuleData module,
            WeaponData weapon,
            JObject moduleJson,
            JToken burnTickRateTok,
            string fileName)
        {
            if (module == null)
                return;

            // Explicit wins, always. The generic mapper has already applied it
            // by this point; this just stops us overriding a deliberate false.
            if (moduleJson != null && moduleJson["canBeBoosted"] != null)
                return;

            if (module.canBeBoosted)
                return;

            // Does anything on this weapon actually scale with level?
            var tickObj = burnTickRateTok as JObject;

            bool scalesWithLevel =
                tickObj != null &&
                (((float?)tickObj["perLevel"] ?? 0f) != 0f ||
                 ((float?)tickObj["change"] ?? 0f) != 0f);

            // A swarm cap that grows per level is exactly "scales with level",
            // and it is the only reason to boost a spawner at all - a spawner
            // has no FloatSeries stats, so without this a BoosterCore beside one
            // is wasted.
            if (!scalesWithLevel && weapon != null)
            {
                ForgeMinion.Config swarm;

                if (ForgeMinion.TryGet(weapon, out swarm) && swarm != null &&
                    (swarm.capPerLevel > 0 || swarm.health.perLevel != 0f))
                {
                    // healthPerLevel counts for the same reason capPerLevel
                    // does: it is read off the SPAWNER's level, and without
                    // boosting that level cannot move, so the setting would be
                    // dead on arrival.
                    scalesWithLevel = true;
                }
            }

            if (!scalesWithLevel)
                return;

            module.canBeBoosted = true;

            Log.LogInfo(
                fileName + ": this weapon scales with module LEVEL, so " +
                "\"canBeBoosted\" has been turned ON - every stock weapon " +
                "module ships with it OFF, which is why a BoosterCore next " +
                "to a weapon never did anything. Set \"canBeBoosted\": false " +
                "in the module block to keep the stock behaviour.");
        }

        private static bool ProjectileTargetOk(
            ProjectileWeaponData pw,
            string feature,
            string fileName,
            bool supportsPhysics,
            string lobbedHint)
        {
            if (pw == null)
            {
                Log.LogWarning(
                    fileName + ": " + feature + " only works on projectile " +
                    "weapons - ignored. (A hitscan beam hits instantly, so " +
                    "there is no flight to shape; a minion spawner has no " +
                    "projectile at all.)");
                return false;
            }

            if (pw.usePhysics)
            {
                if (!supportsPhysics)
                {
                    Log.LogWarning(
                        fileName + ": " + feature + " does not support " +
                        "LOBBED weapons yet - ignored. This weapon has " +
                        "\"usePhysics\": true (the Derbis / grenade style), so " +
                        "its shot is a PhysicsProjectile, which is a different " +
                        "class from a normal bullet and is not hooked by this " +
                        "feature." + (lobbedHint ?? string.Empty));
                    return false;
                }

                if (pw.physicsProjectilePrefab == null)
                {
                    Log.LogWarning(
                        fileName + ": " + feature + " - this weapon is lobbed " +
                        "(usePhysics) but has no physicsProjectilePrefab to " +
                        "modify. Ignored.");
                    return false;
                }

                return true;
            }

            if (pw.projectilePrefab == null)
            {
                Log.LogWarning(
                    fileName + ": " + feature + " - this weapon has no " +
                    "projectilePrefab to modify. Ignored.");
                return false;
            }

            return true;
        }

        // The picture on the module card. Accepts a stock icon name
        // ("HUD_GridTiles_12") or the name of your own art from the sprites
        // folder - the same names projectileSprite takes.
        //
        // Stock is tried FIRST on purpose. Every weapon written before
        // custom icons existed names a stock sprite, and letting a
        // same-named PNG quietly replace one would be a nasty surprise to
        // debug. A collision is reported instead, so it can be renamed.
        private static void ApplyModuleIcon(
            ModuleData module,
            string iconName,
            string path)
        {
            if (module == null || string.IsNullOrEmpty(iconName))
                return;

            iconName = iconName.Trim();

            var stock =
                JsonFieldMapper.FindAsset(typeof(Sprite), iconName) as Sprite;

            Sprite custom = ForgeSpriteLibrary.ResolveIconSprite(iconName);

            if (stock != null)
            {
                module.icon = stock;

                if (custom != null)
                {
                    Log.LogWarning(
                        path + ": '" + iconName + "' is both a game sprite " +
                        "and one of yours in the sprites folder. The GAME's " +
                        "is used. Rename yours to use it.");
                }

                return;
            }

            if (custom != null)
            {
                module.icon = custom;
                return;
            }

            string known = ForgeSpriteLibrary.Count > 0
                ? " Your own sprites are: " +
                  string.Join(", ", ForgeSpriteLibrary.Names) + "."
                : " There is no art in the sprites folder yet.";

            Log.LogWarning(
                path + ": no sprite named '" + iconName + "'. Module icons " +
                "in the base game are called HUD_GridTiles_NN." + known +
                " The module keeps the template's icon.");
        }

        // Sets how many power cores can attach to the weapon module -
        // the "0 / N" cap in the grid. The game rolls
        // Random.Range(powerLevel.Min, powerLevel.Max) (Max exclusive),
        // so we treat the JSON max as inclusive and add 1.
        //   "powerNodes": 6                -> always 6
        //   "powerNodes": { "min":4,"max":8 } -> random 4..8
        private static void ApplyPowerNodes(
            ModuleData module,
            JToken powerNodes,
            string fileName)
        {
            int min;
            int max;

            var range = powerNodes as JObject;

            if (range != null)
            {
                min = (int?)range["min"] ?? 1;
                max = (int?)range["max"] ?? min;
            }
            else
            {
                min = (int)powerNodes;
                max = min;
            }

            if (min < 0) min = 0;
            if (max < min) max = min;

            try
            {
                // powerLevel is a MyBox MinMaxInt struct field; set its
                // Min/Max through the boxed value (Max exclusive).
                FieldInfo plField =
                    typeof(ModuleData).GetField(
                        "powerLevel",
                        BindingFlags.Public | BindingFlags.Instance);

                if (plField == null)
                {
                    Log.LogWarning(
                        fileName + ": powerLevel field not found");
                    return;
                }

                object boxed = plField.GetValue(module);
                Type t = boxed.GetType();

                t.GetField("Min").SetValue(boxed, min);
                t.GetField("Max").SetValue(boxed, max + 1);

                plField.SetValue(module, boxed);
            }
            catch (Exception e)
            {
                Log.LogWarning(
                    fileName + ": failed to set powerNodes: " +
                    e.Message);
            }
        }

        // Changes which resource the module grants when equipped (and
        // how much) by retargeting the module's ModifyResourceCapacity
        // effects. JSON: "resourceGain": { "resource": "...", "amount": n }
        private static void ApplyResourceGain(
            ModuleData module,
            JObject resourceGain,
            string fileName)
        {
            string resourceName =
                (string)resourceGain["resource"];

            float? amount =
                (float?)resourceGain["amount"];

            Resource resource = null;

            if (!string.IsNullOrEmpty(resourceName))
            {
                resource =
                    JsonFieldMapper.FindAsset(
                        typeof(Resource),
                        resourceName) as Resource;

                if (resource == null)
                {
                    Log.LogWarning(
                        fileName +
                        ": resourceGain resource '" +
                        resourceName + "' not found");
                }
                else if (resource.isShared)
                {
                    // Shared resources (e.g. Money) are managed by the
                    // run-wide shared-tank system, not per-unit. Giving
                    // one as capacity installs a duplicate tank and
                    // throws during unit setup, which hangs loading.
                    Log.LogWarning(
                        fileName + ": resourceGain resource '" +
                        resourceName + "' is a shared resource and " +
                        "can't be gained per-weapon - ignoring it " +
                        "(this would otherwise hang the game).");
                    return;
                }
            }

            bool found = false;

            foreach (var effect in module.effects)
            {
                var capacity =
                    effect as ModifyResourceCapacity;

                if (capacity == null)
                    continue;

                found = true;

                if (resource != null)
                    capacity.resource = resource;

                if (amount.HasValue)
                    capacity.delta.baseValue = amount.Value;
            }

            if (!found && (resource != null || amount.HasValue))
            {
                var capacity = new ModifyResourceCapacity();

                capacity.resource = resource;
                capacity.delta.baseValue = amount ?? 10f;
                capacity.delta.increaseMethod =
                    FloatSeries.IncreaseMethod.Add;
                capacity.delta.change = 0f;

                module.effects.Add(capacity);
            }
        }
    }
}
