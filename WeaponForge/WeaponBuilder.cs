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
                // A MODULE FORGE FILE IN THE WEAPONS FOLDER IS THE COMMON CAUSE,
                // and "missing required template" sends you to look for a typo
                // instead. Round 11's log carried this error four times over for
                // `overdrive.json`, a file Module Forge was building perfectly
                // well from its own folder - so the reader had a red ERROR line
                // about a file that was not broken and not theirs to fix here.
                //
                // A module has no "template" and always has "effects"; that pair
                // is enough to name it without guessing.
                if (root["effects"] != null)
                {
                    Log.LogWarning(
                        fileName + " looks like a MODULE FORGE module, not a " +
                        "weapon - it has \"effects\" and no \"template\". It " +
                        "belongs in the \"modules\" folder next to " +
                        "ModuleForge.dll; this is the \"weapons\" folder. " +
                        "Skipped, and Module Forge will have built it from its " +
                        "own copy already. Delete this one to quiet the log.");
                    return null;
                }

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
            float? impactLifetime = null;
            float? impactGravity = null;
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
            float? muzzleLifetime = null;
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
                muzzleLifetime = (float?)weaponJson["muzzleLifetime"];
                muzzleSize = (float?)weaponJson["muzzleSize"];
                muzzleRotate = (bool?)weaponJson["muzzleRotate"];
                muzzleSpread = (float?)weaponJson["muzzleSpread"];
                muzzleFollow = (bool?)weaponJson["muzzleFollow"];

                impactSfx = (string)weaponJson["impactSfx"];
                impactSfxOn = (string)weaponJson["impactSfxOn"];
                impactSprite = (string)weaponJson["impactSprite"];
                impactSpriteFps =
                    (float?)weaponJson["impactSpriteFps"];
                impactLifetime = (float?)weaponJson["impactLifetime"];
                impactGravity = (float?)weaponJson["impactGravity"];
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
                // Was MISSING while every one of its neighbours was here, so
                // the flash lifetime applied correctly and the log ALSO said
                // "muzzleLifetime does not match any field on
                // ProjectileWeaponData - skipped" in the same run. A warning
                // about a key that worked is worse than no warning: it teaches
                // you to distrust the log. Found in Sugarheady's R10 part 4
                // log, where it fired for CurveStream and RiotWave.
                weaponJson.Remove("muzzleLifetime");
                weaponJson.Remove("muzzleSize");
                weaponJson.Remove("muzzleRotate");
                weaponJson.Remove("muzzleSpread");
                weaponJson.Remove("muzzleFollow");
                weaponJson.Remove("impactSfx");
                weaponJson.Remove("impactSfxOn");
                weaponJson.Remove("impactSprite");
                weaponJson.Remove("impactSpriteFps");
                weaponJson.Remove("impactLifetime");
                weaponJson.Remove("impactGravity");
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

                // "convertableCells": "all" and friends. Expanded HERE, before
                // the mapper sees it, because that field is a List<CellType>
                // filled by reflection - there is no per-field hook to put a
                // keyword in, and the mapper would look for one CellType asset
                // literally called "all".
                ExpandCellKeywords(weaponJson, name);

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
                impactLifetime,
                impactGravity,
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
                muzzleLifetime,
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
            ApplySpeedRamp(weapon, root, fileName);
            ApplyCrit(weapon, root, fileName);
            ApplyExecute(weapon, root, fileName);
            ApplySpiral(weapon, root, isGadget, fileName);
            ApplyGuided(weapon, root, fileName);
            ApplyStun(weapon, root, fileName);
            ApplyWallRide(weapon, root, fileName);
            ApplyCharm(weapon, root, fileName);
            ApplyAdaptive(weapon, root, fileName);
            ApplyLeech(weapon, root, fileName);

            // Reads the JSON only, so it does not care what order the blocks
            // above parsed their colours in. See the method.
            WarnUnusedTintStrengths(root, fileName);

            ApplyChain(weapon, root, fileName);

            ApplyConverge(weapon, root, fileName);

            ApplyDeflect(weapon, root, fileName);

            ApplyTurret(weapon, root, fileName);

            ApplyBounce(weapon, root, fileName);

            ApplySticky(weapon, root, isGadget, fileName);

            ApplyTether(weapon, root, fileName);

            ApplyGravity(weapon, root, fileName);
            ApplyTractor(weapon, root, isGadget, fileName);
            // BEFORE ApplyMinion, so the "minion FIRING" line it prints reports
            // the tamed numbers rather than the enemy's.
            TameSpawner(weapon, root, fileName);
            // AFTER TameSpawner - the opt-in block finishes what taming starts,
            // and its own "did the file mention this" tests must see the file's
            // values, not the tamed ones.
            ApplySpawner(weapon, root, fileName);
            ApplyMinion(weapon, root, fileName);
            ApplyFade(weapon, root, fileName);
            ApplySlam(weapon, root, fileName);
            ApplySwipe(weapon, root, fileName);
            ApplyCurveBeam(weapon, root, fileName);
            ApplyContactDamage(weapon, root, fileName);
            ApplyHitSound(weapon, root, fileName);
            ApplySlow(weapon, root, fileName);
            ApplyOverheat(weapon, root, slot, fileName);
            // AFTER ApplyOverheat, so its "these compose" line reads as a
            // follow-up to the heat model rather than preceding it. Neither
            // parses the other's config - both read the raw JSON - so the order
            // is presentational only.
            ApplySpinUp(weapon, root, slot, fileName);

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
            ApplyTargeting(weapon, root, fileName);

            // Last of all: the charge meter borrows the weapon's own bullet
            // art, and every motion feature above re-clones the projectile
            // prefab. Running here means it reads the final one rather than a
            // clone something else has already replaced.
            ApplyCharge(weapon, root, isGadget, fileName);

            // AFTER ApplyCharge, and that ordering is load-bearing: the lunge
            // rides the charge block's hold/release, so it checks that one is
            // registered and refuses loudly when it is not. Run before
            // ApplyCharge, that check would fail on every weapon.
            ApplyLunge(weapon, root, fileName);

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
                    (string)minionJson["trailColor"],
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
                    cfg.hasTrailColor || cfg.trailMatchesBody ||
                    cfg.HasStats ||
                    cfg.defaultWeaponModule != null ||
                    !cfg.hurtsOwner || !cfg.hurtsAllies || !cfg.solidToOwner ||
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
                    // Fall back to the GAME's own sprites, by the name the game
                    // gives them - so "part_cyrcle_12" gets you the little orb
                    // that pops out of a white block. Custom art is tried first
                    // and always wins a name clash, so no existing file can
                    // change meaning by this being added.
                    if (ForgeSpriteLibrary.TryGetStockArt(
                            projectileSpriteName, out customArt))
                    {
                        Log.LogInfo(
                            fileName + ": projectileSprite '" +
                            projectileSpriteName + "' is one of the GAME's own " +
                            "sprites (there is no PNG of that name in your " +
                            "sprites folder). Using it.");
                    }
                    else
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
                            projectileSpriteName + "' was not found." + known +
                            " It is not one of the game's own sprite names " +
                            "either - those are things like 'part_cyrcle_12' " +
                            "(the little orb from a white block).");
                    }
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
                    "', which is not a custom sound, not one of the GAME's " +
                    "own sound names, and not a sound id - so that slot will " +
                    "be SILENT." + known +
                    " A custom sound is named after its file, without the " +
                    "extension. A game sound is named the way the audio " +
                    "database names it, with slashes - \"Weapons/Popper/" +
                    "Shoot\", \"Cells/Fuel\", \"UI/Click\". All 259 of them " +
                    "are listed in SOUNDS.txt next to this mod.");
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
            float? impactLifetime,
            float? impactGravity,
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
                impactRotate.HasValue || impactSpread.HasValue ||
                impactLifetime.HasValue || impactGravity.HasValue;

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

                if (!ForgeSpriteLibrary.TryGetAnyArt(impactSprite, out art))
                {
                    Log.LogWarning(
                        fileName + ": impactSprite '" + impactSprite +
                        "' is not a sprite I can find." +
                        ForgeSpriteLibrary.NotFoundHint() +
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

                fps = ForgeSpriteLibrary.ParticleFps(
                    art,
                    impactSpriteFps,
                    art.animation != null
                        ? (float?)art.animation.fps : null,
                    "impactSprite",
                    fileName);
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
                    wantColor, tint, rainbow, impactRgbSpeed, replace,
                    impactLifetime, impactGravity);

                changed += ApplyRangeArt(
                    ref pw.rangeData, frames, fps, fileName,
                    impactCount, impactSize, rotate, impactSpread,
                    wantColor, tint, rainbow, impactRgbSpeed, replace,
                    impactLifetime, impactGravity);

                changed += ApplyLifetimeArt(
                    ref pw.lifetimeData, frames, fps, fileName,
                    impactCount, impactSize, rotate, impactSpread,
                    wantColor, tint, rainbow, impactRgbSpeed, replace,
                    impactLifetime, impactGravity);

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
            bool replace,
            float? lifetime = null,
            float? gravity = null)
        {
            if (data.destroyEffect == null)
                return 0;

            GameObject art = CloneEffectAndPaint(
                data.destroyEffect, frames, fps, slot, fileName,
                count, size, rotate, spread, color, tint, rainbow, rgbSpeed,
                replace, lifetime, gravity);

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
            bool replace,
            float? lifetime = null,
            float? gravity = null)
        {
            if (data.destroyEffect == null)
                return 0;

            GameObject art = CloneEffectAndPaint(
                data.destroyEffect, frames, fps, "out-of-range", fileName,
                count, size, rotate, spread, color, tint, rainbow, rgbSpeed,
                replace, lifetime, gravity);

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
            bool replace,
            float? lifetime = null,
            float? gravity = null)
        {
            if (data.spawnOnTimeDown == null)
                return 0;

            GameObject art = CloneEffectAndPaint(
                data.spawnOnTimeDown, frames, fps, "end-of-life", fileName,
                count, size, rotate, spread, color, tint, rainbow, rgbSpeed,
                replace, lifetime, gravity);

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
            bool replace,
            float? lifetime = null,
            float? gravity = null)
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
                clone, count, size, rotate, spread, lifetime, gravity);

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
            float? muzzleLifetime,
            string fileName)
        {
            bool wantColor = !string.IsNullOrEmpty(muzzleColor);
            bool wantSprite = !string.IsNullOrEmpty(muzzleSprite);

            bool wantShape = muzzleCount.HasValue || muzzleSize.HasValue ||
                             muzzleRotate.HasValue || muzzleFollow.HasValue ||
                             muzzleSpread.HasValue || muzzleLifetime.HasValue;

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

                if (!ForgeSpriteLibrary.TryGetAnyArt(muzzleSprite, out art))
                {
                    Log.LogWarning(
                        fileName + ": muzzleSprite '" + muzzleSprite +
                        "' is not a sprite I can find." +
                        ForgeSpriteLibrary.NotFoundHint() +
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

                float? fps = ForgeSpriteLibrary.ParticleFps(
                    art,
                    muzzleSpriteFps,
                    art.animation != null
                        ? (float?)art.animation.fps : null,
                    "muzzleSprite",
                    fileName);

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

            // HOW LONG EACH PUFF LIVES, and why it is worth a knob.
            //
            // `WeaponBase.DoShoot` calls `Play()` on the SAME particle system
            // instance every shot - one instance per projectileCount, reused -
            // and Play() on a system that is already running ADDS a burst
            // rather than restarting. So if a puff outlives the gap between
            // shots, a fast weapon stacks flashes on top of each other: with
            // alpha blending that darkens toward black, and with a drawn sprite
            // it reads as smeared art rather than as one flash. That is
            // "when you fire real quickly the muzzle sprite stacks ontop of
            // itself and turns black".
            //
            // The lifetime is not otherwise reachable - it is a particle
            // property, not a weapon field - so there was no way to fix it from
            // a file at all.
            {
                var systems =
                    clone.GetComponentsInChildren<ParticleSystem>(true);

                float longest = 0f;

                for (int i = 0; i < systems.Length; i++)
                {
                    if (systems[i] == null)
                        continue;

                    ParticleSystem.MainModule main = systems[i].main;

                    if (muzzleLifetime.HasValue)
                    {
                        main.startLifetime =
                            new ParticleSystem.MinMaxCurve(
                                Mathf.Max(0.01f, muzzleLifetime.Value));
                    }

                    longest = Mathf.Max(
                        longest, main.startLifetime.constantMax);
                }

                float gap = weapon.fireRate > 0f ? 1f / weapon.fireRate : 0f;

                if (muzzleLifetime.HasValue)
                {
                    Log.LogInfo(
                        fileName + ": muzzle flash lifetime " +
                        muzzleLifetime.Value.ToString("0.###") + "s across " +
                        systems.Length + " system(s).");
                }

                // Only worth saying when the weapon is fast enough for it to
                // matter, and only for a weapon whose flash was customised at
                // all - the stock puffs are tuned for their own guns.
                if (gap > 0f && longest > gap * 1.5f &&
                    (wantSprite || wantColor))
                {
                    Log.LogWarning(
                        fileName + ": muzzle flash - each puff lives " +
                        longest.ToString("0.###") + "s but this weapon fires " +
                        "every " + gap.ToString("0.###") + "s, so up to " +
                        Mathf.CeilToInt(longest / gap) + " flashes are on " +
                        "screen at once, drawn over each other. The game " +
                        "re-Plays the same particle system rather than " +
                        "restarting it, so they stack rather than replace - " +
                        "which darkens an alpha-blended sprite and smears a " +
                        "drawn one. Set \"muzzleLifetime\": " +
                        (gap * 0.8f).ToString("0.###") + " (or less) for one " +
                        "flash at a time.");
                }
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
                    d.mode = ForgeDeflect.ModeReflect;
                }
                else
                {
                    d.mode = ForgeDeflect.ModeDestroy;

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
                (d.mode == ForgeDeflect.ModeReflect
                    ? "reflects enemy fire " +
                      (d.aim == 1 ? "at the nearest enemy" : "back the way it came")
                    : "destroys enemy fire") +
                " within " + d.radius + " units" +
                (d.maxTotal > 0
                    ? ", up to " + d.maxTotal + " per shot"
                    : ", no limit per shot") +
                (d.mode == ForgeDeflect.ModeReflect && d.damageMultiplier != 1f
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

        // "convertableCells": "all" | "solid" | "resources" | "soft"
        //
        // Naming sixteen cell types by hand to say "everything" is exactly the
        // kind of thing nobody should have to do, and the list is not
        // discoverable from inside the game. These keywords expand to real
        // name lists before the field is mapped.
        //
        // WHY IT IS DONE ON THE JSON RATHER THAN ON THE FIELD: the field is a
        // `List<CellType>` filled by `JsonFieldMapper` through reflection.
        // That mapper has no per-field hooks - it looks up each entry as an
        // asset name - so a keyword left in place would send it hunting for a
        // CellType asset called "all". Rewriting the token first means the
        // mapper sees an ordinary list and needs to know nothing about this.
        //
        // The groups are chosen from the COLLIDER and the DROP TABLE, which
        // are the two things that actually decide what a terraforming weapon
        // does:
        //
        //   all        every cell type in the game (16)
        //   solid      only the ones you cannot fly through (12) - the right
        //              answer for a digging weapon, since converting a Fog
        //              into a Fog spends radius achieving nothing
        //   resources  Tuff (White/stamina), Blob (Fuel), Gold (Money) - the
        //              three that PAY when destroyed, i.e. a mining weapon
        //   soft       Slime and the three Fogs - the already-passable ones
        private static readonly string[] CellsSolid =
        {
            "CellType Bark", "CellType Gold", "CellType_Blob",
            "CellType_Hazard", "CellType_Moss", "CellType_Night",
            "CellType_Shard", "CellType_Solid", "CellType_Tuff",
            "CellType_Panel", "CellType_Obsidian", "CellType_Mud"
        };

        private static readonly string[] CellsSoft =
        {
            "CellType_Slime", "CellType Fog Mist",
            "CellType Fog Musk", "CellType Fog Steam"
        };

        // Ordered most-valuable first, purely so the log reads sensibly.
        private static readonly string[] CellsResources =
        {
            "CellType_Tuff", "CellType_Blob", "CellType Gold"
        };

        private static void ExpandCellKeywords(JObject weaponJson, string name)
        {
            if (weaponJson == null)
                return;

            var ccd = weaponJson["cellConvertData"] as JObject;

            if (ccd == null)
                return;

            JToken token = ccd["convertableCells"];

            if (token == null || token.Type != JTokenType.String)
                return;

            string key = ((string)token ?? "").Trim().ToLowerInvariant();
            string[] names;

            switch (key)
            {
                case "all":
                case "everything":
                    names = new string[CellsSolid.Length + CellsSoft.Length];
                    CellsSolid.CopyTo(names, 0);
                    CellsSoft.CopyTo(names, CellsSolid.Length);
                    break;

                case "solid":
                case "walls":
                case "rock":
                    names = CellsSolid;
                    break;

                case "resources":
                case "resource":
                case "ore":
                case "mine":
                    names = CellsResources;
                    break;

                case "soft":
                case "passable":
                    names = CellsSoft;
                    break;

                default:
                    return;   // an ordinary asset name - leave it alone
            }

            var array = new JArray();

            for (int i = 0; i < names.Length; i++)
                array.Add(names[i]);

            ccd["convertableCells"] = array;

            Log.LogInfo(
                name + ": cellConvertData \"convertableCells\": \"" + key +
                "\" expanded to " + names.Length + " cell type(s) - " +
                string.Join(", ", names) + ".");

            // The one combination that spends its whole radius doing nothing.
            var resultToken = ccd["resultCellType"];

            if (resultToken != null && resultToken.Type == JTokenType.String)
            {
                string result = ((string)resultToken ?? "").Trim();

                for (int i = 0; i < names.Length; i++)
                {
                    if (!string.Equals(names[i], result,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    Log.LogInfo(
                        name + ": '" + result + "' is in that list AND is what " +
                        "the terrain becomes, so those cells are rewritten to " +
                        "what they already were. Harmless, just wasted.");
                    break;
                }
            }
        }

        // "speedRamp": a shot whose SPEED changes while it flies.
        //
        //   "speedRamp": true
        //   "speedRamp": { "from": 0.3, "to": 2.5, "over": "distance",
        //                  "span": 0, "curve": 2, "clamp": true,
        //                  "minSpeed": 0, "maxSpeed": 50 }
        //
        // Deliberately the same keys as "damageRamp", because they are the same
        // idea on a different quantity and a file that reads one should be able
        // to guess the other. Values are multipliers of the shot's LAUNCH speed,
        // so a module that raised projectileSpeed still counts.
        // "crit": { "chance": 0.15, "multiplier": 2, "roll": "hit",
        //           "overflow": true, "impact": true, "impactScale": 2,
        //           "sfx": "", "color": "#ffd27f", "colorSeconds": 0.25,
        //           "shake": false, "onExtras": false }
        //
        // Applies to ordinary weapon damage - shots, beams and explosions -
        // because those are the three routes that report which weapon fired.
        // Burn and gas ticks report none and so never crit, which is the
        // intended behaviour rather than a limitation.
        private static void ApplyCrit(
            WeaponData weapon,
            JObject root,
            string fileName)
        {
            JToken token = root["crit"];

            if (token == null || token.Type == JTokenType.Null)
                return;

            var o = token as JObject;

            if (o == null)
            {
                Log.LogWarning(
                    fileName + ": \"crit\" should be an object like " +
                    "{ \"chance\": 0.15, \"multiplier\": 2 } - ignored.");
                return;
            }

            if (!((bool?)o["enabled"] ?? true))
                return;

            var cfg = new ForgeCrit.Config();

            float chance = (float?)o["chance"] ?? 0f;

            // A percent-looking number is almost certainly meant as a percent.
            // 15 would otherwise be "always crit, fifteen stacks deep", which
            // is not a thing anybody typed on purpose.
            if (chance > 1f)
            {
                Log.LogInfo(
                    fileName + ": crit \"chance\": " +
                    chance.ToString("0.###") + " reads as a PERCENT, so it is " +
                    "being used as " + (chance / 100f).ToString("0.###") +
                    " (" + chance.ToString("0.#") + "%). Write it as a " +
                    "fraction (0." + Mathf.RoundToInt(chance).ToString() +
                    ") to silence this. Values above 1 as a FRACTION are also " +
                    "meaningful - see \"overflow\" - so this guess only " +
                    "applies above 1.");

                chance = chance / 100f;
            }

            cfg.chance = Mathf.Max(0f, chance);
            cfg.multiplier = Mathf.Max(1f, (float?)o["multiplier"] ?? 2f);
            cfg.overflow = (bool?)o["overflow"] ?? true;

            string rollText = ((string)o["roll"] ?? "hit")
                .Trim().ToLowerInvariant();

            if (rollText == "shot")
                cfg.roll = ForgeCrit.Roll.Shot;
            else if (rollText == "pull" || rollText == "volley")
                cfg.roll = ForgeCrit.Roll.Pull;
            else
                cfg.roll = ForgeCrit.Roll.Hit;

            cfg.impact = (bool?)o["impact"] ?? true;
            cfg.impactScale = Mathf.Max(1.05f, (float?)o["impactScale"] ?? 2f);
            cfg.shake = (bool?)o["shake"] ?? false;
            cfg.onExtras = (bool?)o["onExtras"] ?? false;

            string sfx = (string)o["sfx"];

            if (!string.IsNullOrEmpty(sfx))
            {
                cfg.sfxGuid = ForgeSfxRegistry.Resolve(
                    sfx, null, false, fileName);

                if (string.IsNullOrEmpty(cfg.sfxGuid))
                {
                    Log.LogWarning(
                        fileName + ": crit \"sfx\": '" + sfx + "' is not a " +
                        "sound I know, so the crit will be silent. Custom " +
                        "sounds go in the mod's sounds folder; the startup log " +
                        "lists what loaded.");
                }
            }

            string colorText = (string)o["color"];

            if (!string.IsNullOrEmpty(colorText))
            {
                Color c;

                if (VisualCustomizer.TryParseColor(colorText, out c))
                {
                    cfg.hasColor = true;
                    cfg.color = c;
                }
                else
                {
                    Log.LogWarning(
                        fileName + ": crit \"color\": '" + colorText +
                        "' is not a colour I can read, so no crit tint.");
                }
            }

            cfg.colorSeconds = Mathf.Max(0.05f, (float?)o["colorSeconds"] ?? 0.25f);
            cfg.colorPulse = Mathf.Max(0f, (float?)o["colorPulse"] ?? 0f);
            cfg.tintStrength = TintStrength(o, fileName, "crit", 1f);

            if (cfg.chance <= 0f)
            {
                Log.LogWarning(
                    fileName + ": \"crit\" has no \"chance\", so nothing will " +
                    "ever crit. Set \"chance\": 0.15 for 15%.");
                return;
            }

            ForgeCrit.Register(weapon, cfg);

            // Say what it means in play, not just what was parsed - a chance
            // and a multiplier are hard to feel without the average.
            float avg = 1f +
                Mathf.Min(cfg.chance, 8f) * (cfg.multiplier - 1f);

            string rollWord =
                cfg.roll == ForgeCrit.Roll.Shot ? "once per SHOT (a shotgun " +
                    "rolls per pellet)" :
                cfg.roll == ForgeCrit.Roll.Pull ? "once per TRIGGER PULL (a " +
                    "whole blast crits together)" :
                    "once per THING HIT (a piercing shot rolls again for each " +
                    "enemy)";

            Log.LogInfo(
                fileName + ": crit - " +
                (cfg.chance * 100f).ToString("0.#") + "% for x" +
                cfg.multiplier.ToString("0.##") + ", rolled " + rollWord +
                ". Average damage is about x" + avg.ToString("0.##") +
                " over time." +
                (cfg.overflow && cfg.chance > 1f
                    ? " Chance is above 100%, so it ALWAYS crits and " +
                      ((cfg.chance - Mathf.Floor(cfg.chance)) * 100f)
                          .ToString("0.#") +
                      "% of the time crits twice (stacks add the bonus, so " +
                      "two stacks of x" + cfg.multiplier.ToString("0.##") +
                      " is x" + ForgeCrit.Factor(2, cfg.multiplier)
                          .ToString("0.##") + ")."
                    : ""));

            // The game has no damage numbers anywhere, so a crit with every
            // feedback switched off is genuinely invisible. Worth saying.
            if (!cfg.impact && !cfg.hasColor && !cfg.shake &&
                string.IsNullOrEmpty(cfg.sfxGuid))
            {
                Log.LogWarning(
                    fileName + ": crit has EVERY feedback switched off, and " +
                    "this game has no floating damage numbers - so a crit will " +
                    "be completely invisible. Turn \"impact\" back on, or set " +
                    "\"color\" / \"sfx\" / \"shake\".");
            }

            if (cfg.onExtras)
            {
                // Names exactly what is wired, because "this mod's own damage"
                // would be a promise five other features do not keep.
                Log.LogInfo(
                    fileName + ": crit \"onExtras\" is on. FOUR of this mod's " +
                    "own damage sources can crit: the GAS cloud, the SWIPE " +
                    "blade, CONTACT damage (which is also how a ram and a " +
                    "lunge hurt what they hit), and SLAM. Not wired: orbit " +
                    "rings, tethers, turrets, gravity wells and the shield - " +
                    "say the word if you want any of those too. " +
                    "The lunge's own crash damage to YOU is deliberately " +
                    "excluded; a critical hit on your own self-damage is a bug, " +
                    "not a feature. Note the gas cloud ticks several times a " +
                    "second, so a crit there fires often and reads as noise " +
                    "rather than as a payoff - which is why this is off by " +
                    "default.");
            }
        }

        // "leech" - your hits heal you, and an absorb orb flies back from the
        // enemy to your ship.
        private static void ApplyLeech(
            WeaponData weapon,
            JObject root,
            string fileName)
        {
            JToken token = root["leech"];

            if (token == null || token.Type == JTokenType.Null)
                return;

            JObject o = token as JObject;

            if (o == null)
            {
                // "leech": true is a complete config - the defaults are a
                // playable weapon.
                bool on;

                if (token.Type == JTokenType.Boolean)
                {
                    on = (bool)token;

                    if (!on)
                        return;
                }
                else
                {
                    Log.LogWarning(
                        fileName + ": \"leech\" should be true or an object " +
                        "like { \"fraction\": 0.15 } - ignored.");
                    return;
                }
            }
            else if (!((bool?)o["enabled"] ?? true))
            {
                return;
            }

            var cfg = new ForgeLeech.Config();

            // "leech": true means "heal me for a tenth of what I deal", which is
            // a sane weapon rather than a placeholder.
            cfg.fraction = 0.1f;

            if (o != null)
            {
                cfg.fraction = Mathf.Max(
                    0f, (float?)o["fraction"] ?? (float?)o["percent"] ?? 0.1f);

                cfg.flat = Mathf.Max(0f, (float?)o["flat"] ?? 0f);
                cfg.resource = (string)o["resource"] ?? "";

                cfg.maxPerSecond =
                    Mathf.Max(0f, (float?)o["maxPerSecond"] ?? 0f);
                cfg.perEnemyDelay =
                    Mathf.Max(0f, (float?)o["perEnemyDelay"] ?? 0f);

                cfg.onExtras = (bool?)o["onExtras"] ?? false;

                cfg.orb = (bool?)o["orb"] ?? true;
                cfg.orbSprite = (string)o["orbSprite"] ?? cfg.orbSprite;
                cfg.orbFps = Mathf.Max(1f, (float?)o["orbFps"] ?? 14f);
                cfg.orbScale = Mathf.Max(0.01f, (float?)o["orbScale"] ?? 1f);
                cfg.orbSpeed = Mathf.Max(1f, (float?)o["orbSpeed"] ?? 22f);
                cfg.orbArc = Mathf.Max(0f, (float?)o["orbArc"] ?? 0.7f);
                cfg.orbSpin = (float?)o["orbSpin"] ?? 0f;
                cfg.orbLifetime =
                    Mathf.Max(0.2f, (float?)o["orbLifetime"] ?? 2.5f);
                cfg.maxOrbs = Mathf.Max(1, (int?)o["maxOrbs"] ?? 14);

                string healOnText = ((string)o["healOn"] ?? "arrival")
                    .Trim().ToLowerInvariant();

                if (healOnText == "hit" || healOnText == "instant" ||
                    healOnText == "immediate" || healOnText == "now")
                {
                    cfg.healOn = ForgeLeech.HealOn.Hit;
                }
                else
                {
                    cfg.healOn = ForgeLeech.HealOn.Arrival;
                }

                cfg.sfxGuid = ResolveChargeSfx(
                    (string)o["sfx"], null, false, "leech sfx", fileName);

                string colorText = (string)o["orbColor"] ?? (string)o["color"];

                if (!string.IsNullOrEmpty(colorText))
                {
                    Color c;

                    if (VisualCustomizer.TryParseColor(colorText, out c))
                    {
                        cfg.orbHasColor = true;
                        cfg.orbColor = c;
                    }
                    else
                    {
                        Log.LogWarning(
                            fileName + ": leech \"orbColor\": '" + colorText +
                            "' is not a colour I can read, so the orb keeps " +
                            "the sprite's own colours.");
                    }
                }

                JToken trailToken = o["orbTrail"] ?? o["trail"];

                if (trailToken != null && trailToken.Type != JTokenType.Null)
                    cfg.orbTrail = ForgeTrail.Parse(trailToken, fileName);
            }

            // A FRACTION ABOVE 1 IS NOT CONVERTED, and that is a deliberate
            // difference from crit's "chance". There, 15 could only ever have
            // meant 15%, so it is divided by 100. Here "heal me for more than I
            // dealt" is a real (if silly) build, and silently dividing would
            // make it unreachable. So the number is used as written and the log
            // says what it will do, loudly enough to catch a typo on the first
            // shot.
            if (cfg.fraction > 1f)
            {
                Log.LogWarning(
                    fileName + ": leech \"fraction\": " +
                    cfg.fraction.ToString("0.##") + " is a FRACTION, not a " +
                    "percent - so this heals you " +
                    (cfg.fraction * 100f).ToString("0.#") + "% of the damage " +
                    "you deal, which is more than you dealt. If you meant " +
                    (cfg.fraction).ToString("0.#") + "%, write \"fraction\": " +
                    (cfg.fraction / 100f).ToString("0.###") + ". Unlike crit's " +
                    "\"chance\" this is NOT auto-corrected, because a leech " +
                    "above 100% is a legitimate thing to build.");
            }

            if (!string.IsNullOrEmpty(cfg.resource))
            {
                cfg.resourceAsset =
                    JsonFieldMapper.FindAsset(typeof(Resource), cfg.resource)
                        as Resource;

                if (cfg.resourceAsset == null)
                {
                    Log.LogWarning(
                        fileName + ": leech \"resource\": '" + cfg.resource +
                        "' is not a resource I know - falling back to your " +
                        "ship's own health pool.");
                }
            }

            if (!cfg.DoesAnything)
            {
                Log.LogWarning(
                    fileName + ": leech heals nothing - set \"fraction\" (a " +
                    "share of the damage you deal) or \"flat\" (a fixed amount " +
                    "per hit). Skipped.");
                return;
            }

            if (cfg.orb)
                ResolveLeechOrbArt(cfg, fileName);

            // `healOn: "arrival"` with no orb to arrive would heal NEVER. The
            // config degrades to healing on the hit by itself; this is the line
            // that stops that being invisible.
            if (cfg.healOn == ForgeLeech.HealOn.Arrival &&
                cfg.EffectiveHealOn == ForgeLeech.HealOn.Hit)
            {
                Log.LogInfo(
                    fileName + ": leech asks to heal when the orb ARRIVES, but " +
                    "there is no orb (\"orb\": false, or its art could not be " +
                    "found). Healing on the HIT instead - the alternative would " +
                    "be healing never.");
            }

            ForgeLeech.Register(weapon, cfg);

            LogLeech(cfg, weapon, fileName);
        }

        // The orb's art. Custom PNG first (its own namespace, as everywhere
        // else), then the game's own sprites - where a NUMBERED RUN animates, so
        // "area_zero_particle_tuff" is the nine-frame tuff bubble rather than one
        // frozen frame of it.
        private static void ResolveLeechOrbArt(
            ForgeLeech.Config cfg, string fileName)
        {
            string name = (cfg.orbSprite ?? "").Trim();

            if (name.Length > 0)
            {
                ForgeSpriteLibrary.Art art;

                if (ForgeSpriteLibrary.TryGetArt(name, out art) ||
                    ForgeSpriteLibrary.TryGetStockArt(name, out art))
                {
                    cfg.orbFrames =
                        (art.animation != null &&
                         art.animation.frames != null &&
                         art.animation.frames.Length > 0)
                            ? art.animation.frames
                            : new[] { art.sprite };
                }
            }

            if (cfg.orbFrames != null && cfg.orbFrames.Length > 0)
                return;

            // A NAMED FALLBACK, not an arbitrary one: part_cyrcle_12 is the orb
            // that pops out of a white block, so a leech weapon whose art went
            // missing still looks like something being collected. Falling through
            // to "no orb" would silently turn "heal on arrival" into "heal on
            // hit" and change the feel of the weapon over a typo.
            var backup = JsonFieldMapper.FindAsset(
                typeof(Sprite), "part_cyrcle_12") as Sprite;

            if (backup != null)
            {
                cfg.orbFrames = new[] { backup };

                Log.LogWarning(
                    fileName + ": leech \"orbSprite\": '" + name + "' was not " +
                    "found, so the orb is using the game's own white-block orb " +
                    "(part_cyrcle_12) instead. For the tuff bubbles write " +
                    "\"area_zero_particle_tuff\" - naming the BASE animates all " +
                    "nine frames, while \"area_zero_particle_tuff_0\" is just " +
                    "frame one.");

                return;
            }

            Log.LogWarning(
                fileName + ": leech could not resolve any orb art, so there " +
                "will be no orb. The healing still works.");
        }

        private static void LogLeech(
            ForgeLeech.Config cfg, WeaponData weapon, string fileName)
        {
            string pool = (cfg.resourceAsset != null)
                ? cfg.resourceAsset.name
                : "your ship's own health pool";

            string per = (cfg.fraction > 0f)
                ? (cfg.fraction * 100f).ToString("0.#") + "% of the damage dealt"
                : "";

            if (cfg.flat > 0f)
            {
                per += (per.Length > 0 ? " plus " : "") +
                       cfg.flat.ToString("0.##") + " a hit";
            }

            Log.LogInfo(
                fileName + ": leech - heals " + per + " into " + pool + ", " +
                (cfg.EffectiveHealOn == ForgeLeech.HealOn.Arrival
                    ? "arriving when the absorb orb reaches your ship"
                    : "the instant the hit lands") +
                ". Works on EVERY weapon type (bullet, lobbed, beam, explosion, " +
                "missile) because it measures the victim's own tank rather than " +
                "hooking a projectile." +
                (cfg.maxPerSecond > 0f
                    ? " Capped at " + cfg.maxPerSecond.ToString("0.##") +
                      " healing a second."
                    : "") +
                (cfg.perEnemyDelay > 0f
                    ? " At most once every " +
                      cfg.perEnemyDelay.ToString("0.##") +
                      "s from the same enemy."
                    : ""));

            // THE ONE THAT WILL BITE, and it is arithmetic rather than a bug.
            if (cfg.maxPerSecond <= 0f)
            {
                bool beam = weapon is HitscanWeaponData;

                Log.LogWarning(
                    fileName + ": leech has NO \"maxPerSecond\" cap." +
                    (beam
                        ? " This is a BEAM, which is the worst case: a beam " +
                          "damages its target every damageRepeatDelay (0.08s on " +
                          "the stock Caps Laser), and a piercing one does that " +
                          "to every enemy it threads - roughly 75 heals a " +
                          "second through six enemies. Expect to be effectively " +
                          "immortal until you set a cap."
                        : " A fast or piercing weapon can heal many times a " +
                          "second, so this may be much stronger than it looks " +
                          "on paper.") +
                    " Set \"maxPerSecond\" to the most healing a second you " +
                    "want this weapon to be worth.");
            }

            if (cfg.onExtras)
            {
                Log.LogInfo(
                    fileName + ": leech \"onExtras\" is on, so FOUR of this " +
                    "mod's own damage sources also feed you: the GAS cloud, the " +
                    "SWIPE blade, CONTACT damage (which is how a ram and a " +
                    "lunge hurt what they hit), and SLAM. Not wired: orbit " +
                    "rings, tethers, turrets, gravity wells, the shield. NOTE " +
                    "that a gas cloud ticks several times a second, so leech on " +
                    "gas is a very large sustain - the cap matters more here, " +
                    "not less.");
            }

            // Burn and gas ticks report no weapon at all, so they are excluded
            // for free rather than by a rule. Worth stating, because "my burn
            // does not heal me" reads as a bug otherwise.
            Log.LogInfo(
                fileName + ": leech does NOT trigger on burn ticks or terrain " +
                "damage - those report no weapon, so there is nothing to credit. " +
                "Only damage a weapon actually dealt counts, and only what " +
                "LANDED: a hit blocked by invincibility, refused by i-frames, or " +
                "aimed at an enemy with no tank of that damage type heals you " +
                "nothing. Overkill does not pay either - a 200-damage rocket " +
                "into a 5-health fly leeches off 5.");
        }

        private static void ApplySpeedRamp(
            WeaponData weapon,
            JObject root,
            string fileName)
        {
            JToken token = root["speedRamp"];

            if (token == null || token.Type == JTokenType.Null)
                return;

            var o = token as JObject;

            if (o == null)
            {
                if (token.Type != JTokenType.Boolean || !(bool)token)
                {
                    Log.LogWarning(
                        fileName + ": \"speedRamp\" should be true or an " +
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
                    pw, "speedRamp", fileName, true,
                    " A beam arrives instantly at any range, so there is no " +
                    "flight to speed up."))
            {
                return;
            }

            bool physics = pw.usePhysics;

            string over = (o != null)
                ? ((string)o["over"] ?? "distance").Trim().ToLowerInvariant()
                : "distance";

            ForgeSpeedRamp.Over mode;

            switch (over)
            {
                case "time": mode = ForgeSpeedRamp.Over.Time; break;
                case "pierce":
                case "pierces":
                case "hits": mode = ForgeSpeedRamp.Over.Pierce; break;
                case "bounce":
                case "bounces":
                case "ricochet": mode = ForgeSpeedRamp.Over.Bounce; break;
                case "distance":
                case "range": mode = ForgeSpeedRamp.Over.Distance; break;
                default:
                    Log.LogWarning(
                        fileName + ": speedRamp \"over\": '" + over +
                        "' is not one of distance / time / pierce / bounce - " +
                        "using distance.");
                    mode = ForgeSpeedRamp.Over.Distance;
                    break;
            }

            // Pierce and bounce are counted by patches on Projectile.TryHit and
            // Projectile.Reflect. A PhysicsProjectile is a different class that
            // goes through neither, so those two modes would sit at their
            // starting multiplier forever - say so rather than look broken.
            // Same rule, same wording, as damageRamp.
            if (physics &&
                (mode == ForgeSpeedRamp.Over.Pierce ||
                 mode == ForgeSpeedRamp.Over.Bounce))
            {
                Log.LogWarning(
                    fileName + ": speedRamp \"over\": \"" + over + "\" does " +
                    "not work on a LOBBED (usePhysics) weapon - a lobbed shot " +
                    "neither pierces nor reflects through the code this " +
                    "counts. Using \"distance\" instead, which does work.");
                mode = ForgeSpeedRamp.Over.Distance;
            }

            // RANGEDATA AND A SPEED RAMP FIGHT EACH OTHER, and nothing said so.
            //
            // Round 11 test 110, which he could not classify: *"First I didn't
            // have rangedata at all and it fired normally. No ramp or anything.
            // Added rangedata: enabled true with slowdown false, no projectile at
            // all... then turning enabled to false its behaving like it should.
            // This feels really janky."* Every one of those three observations is
            // the same mechanism.
            //
            // While `RangeData.enabled` is true, `Projectile.FixedUpdate`
            // REWRITES Velocity from `startSpeed` and the range clock every
            // frame - the same fact that makes the boomerang pivot free and that
            // forces a mid-flight turn to switch RangeData off. So a ramp
            // writing Velocity is overwritten before it is ever drawn, and a ramp
            // starting at `from: 0.01` hands the range clock a shot travelling at
            // 1% speed, which reaches its range almost immediately - hence "no
            // projectile at all", and hence damage only to whatever was already
            // touching the barrel.
            //
            // Named at build time because the runtime behaviour is not
            // interpretable: three different settings produced three different
            // wrong-looking results and none of them pointed at range.
            if (!physics)
            {
                ProjectileRangeData rd = pw.rangeData;

                if (rd.enabled)
                {
                    Log.LogWarning(
                        fileName + ": \"speedRamp\" and \"rangeData\": " +
                        "{ \"enabled\": true } cannot both work on the same " +
                        "weapon. While range is on, the game rewrites the " +
                        "shot's velocity from its ORIGINAL speed every frame, " +
                        "so the ramp is overwritten before you see it - and a " +
                        "low \"from\" makes the shot reach its range almost at " +
                        "once, which looks like no projectile at all. Set " +
                        "\"rangeData\": { \"enabled\": false } and bound the " +
                        "shot with \"lifetimeData\" instead; the ramp then " +
                        "works as written.");
                }
            }

            // A lobbed weapon's `projectilePrefab` is NULL - the shot lives on
            // `physicsProjectilePrefab` and reading the wrong one is how eight
            // features silently refused to work once before.
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
                    fileName + ": speedRamp - no " +
                    (physics ? "PhysicsProjectile" : "Projectile") +
                    " on the prefab. Ignored.");
                UnityEngine.Object.Destroy(clone);
                return;
            }

            var r = comp.GetComponent<ForgeSpeedRamp>();

            if (r == null)
                r = comp.gameObject.AddComponent<ForgeSpeedRamp>();

            r.over = mode;

            if (o != null)
            {
                r.from = (float?)o["from"] ?? 1f;
                r.to = (float?)o["to"] ?? 3f;
                r.span = (float?)o["span"] ?? 0f;
                r.curve = (float?)o["curve"] ?? 1f;
                r.clamp = (bool?)o["clamp"] ?? true;
                r.minSpeed = Mathf.Max(0f, (float?)o["minSpeed"] ?? 0f);
                r.maxSpeed = Mathf.Max(0f, (float?)o["maxSpeed"] ?? 0f);
            }

            if (physics)
                pw.physicsProjectilePrefab = (PhysicsProjectile)comp;
            else
                pw.projectilePrefab = (Projectile)comp;

            if (r.from == r.to)
            {
                Log.LogWarning(
                    fileName + ": speedRamp has from == to (" + r.from +
                    "), so speed never changes. Set them differently - from " +
                    "0.3 to 2 means the shot leaves slowly and arrives fast.");
            }

            // A LOBBED SHOT STOPS BEING A PARABOLA, and that is worth saying
            // out loud rather than letting someone wonder why their grenade
            // flies further than it used to. Gravity still bends it - this runs
            // after the physics step and only changes the LENGTH of the
            // velocity - but the shot no longer trades speed for height, so a
            // ramp up flies flatter and a ramp down drops hard.
            if (physics)
            {
                Log.LogInfo(
                    fileName + ": speedRamp on a LOBBED shot - it still arcs " +
                    "(gravity is applied first and this only changes the " +
                    "SPEED, not the direction), but it no longer trades speed " +
                    "for height, so expect a flatter, longer throw on a ramp " +
                    "up and a hard drop on a ramp down. The game's own " +
                    "\"homingData\": { \"acceleration\", \"maxSpeed\" } is " +
                    "constant thrust along the shot's facing and works on " +
                    "these with no target and no homing enabled, if that suits " +
                    "the weapon better - this block runs afterwards, so it " +
                    "wins if you set both.");
            }

            // THE ONE REAL CLASH, and it is on the most-copied template in the
            // mod. `rangeData.slowDown` is the game's own "coast to a dead stop
            // at your range", and the White Popper ships it ON. The ramp runs
            // as a postfix so it wins the argument every frame - but the file
            // is then asking for two opposite things and the loser is silent.
            if (!physics && pw.rangeData.enabled && pw.rangeData.slowDown)
            {
                Log.LogWarning(
                    fileName + ": speedRamp is fighting \"rangeData\": " +
                    "{ \"slowDown\": true }, which decelerates the shot to " +
                    "ZERO at its range. The ramp runs afterwards so the ramp " +
                    "wins - but set \"rangeData\": { \"slowDown\": false } so " +
                    "the file says what it means. (The stock White Popper " +
                    "template ships slowDown ON, which is where this usually " +
                    "comes from.)");
            }

            // THE REAL TRAP IS THE SLOW END, NOT THE FAST ONE.
            //
            // Going fast is safe here: `Projectile.FixedUpdate` finds its hits
            // with a SWEPT `Physics2D.CircleCast` along
            // `Velocity * Time.deltaTime`, so the whole step is tested and a
            // fast shot cannot step over a wall. (A fast MINION can, because a
            // minion moves by rigidbody physics with Discrete detection - a
            // different mechanism that does not apply to this.)
            //
            // Going slow is the one that bites, and it bites twice: a shot that
            // ramps down to nearly nothing hangs in the air until its range or
            // lifetime ends - and on a DISTANCE ramp it also stops making
            // progress through its own ramp, so it freezes at whatever
            // multiplier stalled it. `minSpeed` is the fix and it is worth
            // naming, because that self-reference is not obvious.
            float floor = pw.projectileSpeed * Mathf.Min(r.from, r.to);

            if (r.minSpeed <= 0f && floor < 2f)
            {
                Log.LogWarning(
                    fileName + ": speedRamp bottoms out around " +
                    floor.ToString("0.##") + " units/second (projectileSpeed " +
                    pw.projectileSpeed.ToString("0.##") + " x " +
                    Mathf.Min(r.from, r.to).ToString("0.##") +
                    "), which is close to a standstill - the shot will hang in " +
                    "the air until its range or lifetime ends." +
                    (mode == ForgeSpeedRamp.Over.Distance
                        ? " And because this is a DISTANCE ramp, a stalled shot " +
                          "stops advancing through its own ramp, so it stays " +
                          "stalled."
                        : "") +
                    " Set \"minSpeed\" to a few units/second to give it a floor.");
            }

            string span = (r.span > 0f)
                ? r.span.ToString("0.##")
                : (mode == ForgeSpeedRamp.Over.Distance
                    ? "the weapon's range"
                    : "1");

            Log.LogInfo(
                fileName + ": speed ramp x" + r.from + " -> x" + r.to +
                " over " + span + " " +
                (mode == ForgeSpeedRamp.Over.Distance ? "units of travel"
                 : mode == ForgeSpeedRamp.Over.Time ? "seconds"
                 : mode == ForgeSpeedRamp.Over.Pierce ? "pierced enemies"
                 : "bounces") +
                " (launch speed " + pw.projectileSpeed.ToString("0.##") +
                ", so " + (pw.projectileSpeed * r.from).ToString("0.#") +
                " -> " + (pw.projectileSpeed * r.to).ToString("0.#") +
                " units/second)" +
                (r.clamp ? "" : ", UNCLAMPED (keeps climbing past 'to')") +
                (r.curve != 1f ? ", curve " + r.curve : "") +
                (r.maxSpeed > 0f
                    ? ", capped at " + r.maxSpeed.ToString("0.##") : "") +
                (r.minSpeed > 0f
                    ? ", floored at " + r.minSpeed.ToString("0.##") : "") +
                ".");
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
        // "swipe": a melee swing for the ship.
        //
        // NOTHING IN THIS GAME DOES CONTACT DAMAGE. HealthBase's only entry
        // points are projectile / explosion / hitscan / hazard / electricity /
        // cell collision, there is no melee component anywhere, and there is no
        // OnCollisionStay2D or OnTriggerStay2D in the whole assembly - flying
        // into an enemy is free. So a blade runs its own overlap query and
        // deals its own damage, which is also why it has to credit its own
        // kills.
        //
        // Works in a weapon slot AND on a gadget key with no extra code: the
        // trigger hook is `WeaponBase.DoShoot`, which is on the path of every
        // shot from either direction. See ForgeSwipePatch.
        // THE CHARGE LUNGE - wind up, then launch yourself.
        //
        // Modelled on the Maggot, which he pointed me at. Its wind-up is not a
        // timer, it is `maxSpeed: 1` - it telegraphs by almost STOPPING - and
        // its commitment is `maxAngularVelocity: 0.3`, so once launched it can
        // barely steer. Both are copied; see ForgeLunge.
        private static void ApplyLunge(
            WeaponData weapon, JObject root, string fileName)
        {
            var o = root["lunge"] as JObject;

            if (o == null)
                return;

            if (!((bool?)o["enabled"] ?? true))
                return;

            var cfg = new ForgeLunge.Config();

            cfg.windSpeed = Mathf.Clamp((float?)o["windSpeed"] ?? 0.3f, 0.02f, 1f);
            cfg.windAccel = Mathf.Clamp((float?)o["windAccel"] ?? 0.4f, 0.02f, 1f);
            cfg.windCurve = Mathf.Clamp((float?)o["windCurve"] ?? 1f, 0.05f, 6f);

            cfg.force = Mathf.Max(0f, (float?)o["force"] ?? 60f);
            cfg.minForce = Mathf.Max(0f, (float?)o["minForce"] ?? 0f);
            cfg.aim = ForgeLunge.ParseAim((string)o["aim"]);

            cfg.commit = Mathf.Max(0.02f, (float?)o["commit"] ?? 0.3f);
            cfg.commitSteer = Mathf.Clamp01((float?)o["commitSteer"] ?? 0f);

            cfg.fires = (bool?)o["fires"] ?? true;

            cfg.damage = Mathf.Max(0f, (float?)o["damage"] ?? 0f);
            cfg.push = (float?)o["push"] ?? 40f;
            cfg.recoil = (float?)o["recoil"] ?? 0f;
            cfg.repeatDelay = Mathf.Max(0.02f, (float?)o["repeatDelay"] ?? 0.3f);
            cfg.radius = Mathf.Max(0f, (float?)o["radius"] ?? 0f);
            cfg.hitsPlayer = (bool?)o["hitsPlayer"] ?? false;

            cfg.selfDamage = Mathf.Max(0f, (float?)o["selfDamage"] ?? 0f);

            cfg.sfx = ResolveChargeSfx(
                (string)o["sfx"], null, false, "lunge sfx", fileName) ?? "";

            string type = (string)o["damageType"];

            if (!string.IsNullOrEmpty(type))
            {
                var res = JsonFieldMapper.FindAsset(
                    typeof(Resource), type) as Resource;

                if (res != null)
                    cfg.damageType = res;
                else
                    Log.LogWarning(
                        fileName + ": lunge \"damageType\": '" + type +
                        "' is not a resource this game has. Using the " +
                        "weapon's own.");
            }

            if (!cfg.DoesAnything)
            {
                Log.LogWarning(
                    fileName + ": \"lunge\" has \"force\": 0, so nothing " +
                    "launches. The ship's mass is 1, so force IS the speed in " +
                    "units/second - and the ship's own top speed is 12.5, so " +
                    "60 is a real launch.");
                return;
            }

            // THE ONE HARD REQUIREMENT, and it is worth failing loudly on.
            // The lunge rides the charge block's hold/release rather than
            // growing its own, which is what gets it the meter, the minimum
            // charge, the curve and the ready sound for nothing.
            ForgeCharge.Config charge;

            if (!ForgeCharge.TryGet(weapon, out charge))
            {
                Log.LogWarning(
                    fileName + ": \"lunge\" needs a \"charge\" block and this " +
                    "weapon has none, so nothing will ever launch. The lunge " +
                    "fires when a charge is RELEASED - that is where the " +
                    "wind-up, the meter and the release moment all come from. " +
                    "Add a \"charge\" block with \"mode\": \"release\".");
                return;
            }

            if (charge.mode == ForgeCharge.Mode.Auto)
            {
                Log.LogWarning(
                    fileName + ": \"lunge\" with \"charge\": \"mode\": " +
                    "\"auto\" will never launch - an auto weapon fires " +
                    "continuously and has no release. Use \"release\" mode.");
                return;
            }

            ForgeLunge.Register(weapon, cfg);

            Log.LogInfo(
                fileName + ": lunge - winds up to " +
                (cfg.windSpeed * 100f).ToString("0") + "% speed and " +
                (cfg.windAccel * 100f).ToString("0") + "% acceleration, then " +
                "launches at " + cfg.force.ToString("0.#") +
                " units/second " + ForgeLunge.AimWord(cfg.aim) +
                ", committed for " + cfg.commit.ToString("0.##") + "s at " +
                (cfg.commitSteer * 100f).ToString("0") + "% steering" +
                (cfg.damage > 0f
                    ? ", dealing " + cfg.damage.ToString("0.#") +
                      " on contact"
                    : ", with the hurting left to a \"contactDamage\" block") +
                ".");

            if (cfg.damage <= 0f)
            {
                ForgeContact.Config ignored;

                if (!ForgeContact.TryGet(weapon, out ignored))
                {
                    Log.LogWarning(
                        fileName + ": \"lunge\" has no \"damage\" of its own " +
                        "AND this weapon has no \"contactDamage\" block, so " +
                        "the launch will move you but hurt nothing. Set " +
                        "\"lunge\": \"damage\", or add a \"contactDamage\" " +
                        "block with \"who\": \"ship\".");
                }
            }

            // ---- pure melee, the same route SWIPE takes --------------------
            if (!cfg.fires)
            {
                var pw = weapon as ProjectileWeaponData;

                if (pw != null && pw.projectileCount > 0f)
                {
                    pw.projectileCount = 0f;

                    Log.LogInfo(
                        fileName + ": lunge \"fires\": false - projectileCount " +
                        "set to 0, so the charge releases into a pure lunge. " +
                        "Cost, fire rate and the shoot sound are all still " +
                        "taken, because they happen inside the shot rather " +
                        "than in the projectile. The price is no muzzle flash " +
                        "either, since one is built per pellet.");
                }
            }
        }

        // CONTACT DAMAGE - making a unit dangerous to touch.
        //
        // From his R9 test 50: "it really messes with the enemies mass and when
        // i hit them knocks them super far out! Would be fun to enable damage
        // on collison for the enemies in this case."
        //
        // He was also right that the game already has this, which I had denied
        // in three files - see ForgeContact for the correction and for the two
        // systems it actually has.
        private static void ApplyContactDamage(
            WeaponData weapon, JObject root, string fileName)
        {
            var o = root["contactDamage"] as JObject;

            if (o == null)
                return;

            if (!((bool?)o["enabled"] ?? true))
                return;

            var cfg = new ForgeContact.Config();

            cfg.who = ForgeContact.ParseWho((string)o["who"]);
            cfg.window = Mathf.Max(0.1f, (float?)o["window"] ?? 1.5f);

            cfg.damage = Mathf.Max(0f, (float?)o["damage"] ?? 3f);

            cfg.scaleBySpeed = (bool?)o["scaleBySpeed"] ?? false;
            cfg.damagePerSpeed =
                Mathf.Max(0f, (float?)o["damagePerSpeed"] ?? 1f);
            cfg.minSpeed = Mathf.Max(0f, (float?)o["minSpeed"] ?? 6f);
            cfg.maxDamage = Mathf.Max(0f, (float?)o["maxDamage"] ?? 999f);

            cfg.push = (float?)o["push"] ?? 0f;
            cfg.recoil = (float?)o["recoil"] ?? 0f;

            cfg.repeatDelay = Mathf.Max(0.02f, (float?)o["repeatDelay"] ?? 0.3f);

            cfg.detect = ForgeContact.ParseDetect((string)o["detect"]);
            cfg.radius = Mathf.Max(0f, (float?)o["radius"] ?? 0f);

            cfg.hitsEnemies = (bool?)o["hitsEnemies"] ?? true;
            cfg.hitsPlayer = (bool?)o["hitsPlayer"] ?? false;

            string type = (string)o["damageType"];

            if (!string.IsNullOrEmpty(type))
            {
                var res = JsonFieldMapper.FindAsset(
                    typeof(Resource), type) as Resource;

                if (res != null)
                    cfg.damageType = res;
                else
                    Log.LogWarning(
                        fileName + ": contactDamage \"damageType\": '" + type +
                        "' is not a resource this game has. Using the weapon's " +
                        "own.");
            }

            // RESOLVED HERE, NOT AT DAMAGE TIME, and this is a bug fix rather
            // than a tidy-up. `ForgeContactController.Hurt` needs a damage type
            // and used to fall back to `weapon.Damage.damageType` at runtime -
            // but the ALWAYS-ON modes ("enemies", "ship", "all") are given to a
            // unit by the sweeper with **no weapon at all**, so that fallback
            // resolved to null and `Hurt` returned without dealing any damage.
            // Silently: no log, nothing on screen.
            //
            // That is what made R10 tests 73 and 74 fail while 70 passed - the
            // marked mode is the only one that carries a weapon. Same family as
            // the prefab-config trap: a null reference that disables a whole
            // feature while everything upstream reports success.
            if (cfg.damageType == null)
            {
                cfg.damageType = weapon.damage.damageType;

                if (cfg.damageType == null)
                {
                    Log.LogWarning(
                        fileName + ": \"contactDamage\" has no " +
                        "\"damageType\" and this weapon's own damage has no " +
                        "type either, so there is nothing to deal the damage " +
                        "AS and it would silently do nothing. Name a " +
                        "\"damageType\" (\"Resource White\", \"Resource " +
                        "Caps\", ...). Ignored.");
                    return;
                }
            }

            cfg.tintPulse = Mathf.Max(0f, (float?)o["tintPulse"] ?? 0f);
            cfg.tintStrength =
                TintStrength(o, fileName, "contactDamage", 1f);

            string tint = (string)o["tint"];

            if (!string.IsNullOrEmpty(tint))
            {
                Color c = Color.white;

                if (ReadColor(tint, "contactDamage \"tint\"", fileName, ref c))
                {
                    cfg.tint = c;
                    cfg.hasTint = true;
                }
            }

            if (!cfg.DoesAnything)
            {
                Log.LogWarning(
                    fileName + ": \"contactDamage\" does nothing - no damage, " +
                    "no push and no recoil. Note damage 0 with a push is a " +
                    "legitimate weapon (the game's own Fly Alfa and Fly Zapper " +
                    "are exactly that: 0 damage, 30 push), so set one of them " +
                    "on purpose.");
                return;
            }

            if (!cfg.hitsEnemies && !cfg.hitsPlayer)
            {
                Log.LogWarning(
                    fileName + ": \"contactDamage\" has both \"hitsEnemies\" " +
                    "and \"hitsPlayer\" off, so it can never touch anything. " +
                    "Ignored.");
                return;
            }

            ForgeContact.Register(weapon, cfg);

            Log.LogInfo(
                fileName + ": contactDamage - " +
                ForgeContact.WhoWord(cfg.who) +
                (cfg.who == ForgeContact.WhoMarked
                    ? " for " + cfg.window.ToString("0.##") + "s"
                    : " (permanent)") +
                ", " +
                (cfg.scaleBySpeed
                    ? "damage scaled by impact speed above " +
                      cfg.minSpeed.ToString("0.#")
                    : "a flat " + cfg.damage.ToString("0.#") + " damage") +
                ", detected by " + ForgeContact.DetectWord(cfg.detect) +
                ", at most one hit per target every " +
                cfg.repeatDelay.ToString("0.##") + "s.");

            if (cfg.who == ForgeContact.WhoEnemies ||
                cfg.who == ForgeContact.WhoAll)
            {
                Log.LogWarning(
                    fileName + ": contactDamage \"who\" is a WORLD RULE - " +
                    "every enemy in the run becomes dangerous to every other " +
                    "one, whether this weapon was involved or not. That is a " +
                    "big change to how the game plays, and only one weapon can " +
                    "own it. Use \"marked\" if you wanted a weapon property.");
            }

            if (cfg.hitsPlayer)
            {
                Log.LogWarning(
                    fileName + ": contactDamage \"hitsPlayer\" is ON, so " +
                    "anything carrying this can hurt YOU by touching you. " +
                    "Fine on purpose - just say so out loud.");
            }
        }

        // THE HIT "TICK" - the little sound something makes when you damage it.
        //
        // EVERY DEFAULT HERE IS THE GAME'S OWN BEHAVIOUR, at his request. A
        // block that only sets "scope" changes nothing. That includes leaving
        // the tick UNTHROTTLED, which is the one thing genuinely wrong with it
        // - so the log says so rather than quietly fixing it.
        private static void ApplyHitSound(
            WeaponData weapon, JObject root, string fileName)
        {
            var o = root["hitSound"] as JObject;

            if (o == null)
                return;

            if (!((bool?)o["enabled"] ?? true))
                return;

            var cfg = new ForgeHitSound.Config();

            cfg.scope = ForgeHitSound.ParseScope((string)o["scope"]);

            // ---- the sound -------------------------------------------------
            string want = ((string)o["sfx"] ?? "").Trim();
            string lower = want.ToLowerInvariant();

            if (lower == "none" || lower == "off" || lower == "silent" ||
                lower == "silence" || lower == "mute")
            {
                cfg.silence = true;
            }
            else if (want.Length > 0)
            {
                string guid = ResolveChargeSfx(
                    want, null, false, "hitSound sfx", fileName);

                if (!string.IsNullOrEmpty(guid))
                    cfg.sfx = guid;
            }

            cfg.volume = Mathf.Clamp((float?)o["volume"] ?? 1f, 0f, 4f);

            // ---- the throttle ----------------------------------------------
            cfg.minInterval = Mathf.Max(0f, (float?)o["minInterval"] ?? 0f);
            cfg.perTarget = (bool?)o["perTarget"] ?? false;

            // ---- pitch -----------------------------------------------------
            cfg.pitchMode = ForgeHitSound.ParsePitch((string)o["pitch"]);
            cfg.pitchLow = Mathf.Clamp((float?)o["pitchLow"] ?? 0.85f, 0.25f, 3f);
            cfg.pitchHigh = Mathf.Clamp((float?)o["pitchHigh"] ?? 1.25f, 0.25f, 3f);
            cfg.damageAt = Mathf.Max(0.01f, (float?)o["damageAt"] ?? 10f);
            cfg.vary = Mathf.Clamp((float?)o["vary"] ?? 0f, 0f, 1f);

            if (!cfg.DoesAnything)
            {
                Log.LogWarning(
                    fileName + ": \"hitSound\" does nothing - every key is at " +
                    "the game's own default. Set \"sfx\" to replace the sound " +
                    "(or \"none\" to silence it), \"minInterval\" to stop it " +
                    "machine-gunning, \"pitch\" to make it carry information, " +
                    "or \"vary\" to stop identical repeats grating.");
                return;
            }

            ForgeHitSound.Register(weapon, cfg, fileName);
            ForgeHitSound.Invalidate();

            Log.LogInfo(
                fileName + ": hitSound - " +
                (cfg.silence
                    ? "SILENCED"
                    : (!string.IsNullOrEmpty(cfg.sfx)
                        ? "replaced with your own sound"
                        : "the game's own sound")) +
                ", applying to " + ForgeHitSound.ScopeWord(cfg.scope) +
                ", pitch " + ForgeHitSound.PitchWord(cfg.pitchMode) +
                (cfg.vary > 0f
                    ? " (+/- " + cfg.vary.ToString("0.##") + " jitter)"
                    : "") +
                ", throttle " +
                (cfg.minInterval > 0f
                    ? cfg.minInterval.ToString("0.###") + "s " +
                      (cfg.perTarget ? "per target" : "overall")
                    : "NONE (the game's own behaviour)") +
                ".");

            // THE ONE THING WORTH SAYING OUT LOUD. iFrameDuration is 0 on all
            // 84 damageable prefabs, so the tick has no per-victim gate at
            // all - the only limiter is a GLOBAL 100-per-second cap in the
            // audio database. A shotgun plays one per pellet that connects.
            if (cfg.minInterval <= 0f && !cfg.silence)
            {
                var pw = weapon as ProjectileWeaponData;

                bool multi = weapon.projectileCount > 1f ||
                             (pw != null && pw.piercingData.enabled);

                if (multi)
                {
                    Log.LogInfo(
                        fileName + ": this weapon fires " +
                        weapon.projectileCount.ToString("0") + " projectiles" +
                        ((pw != null && pw.piercingData.enabled)
                            ? " and pierces" : "") +
                        ", and the hit tick has NO throttle of its own - the " +
                        "game's iFrameDuration is 0 on every enemy, so each " +
                        "pellet that connects plays its own. If it sounds " +
                        "like a buzz, that is why: try " +
                        "\"minInterval\": 0.06.");
                }
            }
        }

        // THE CURVE BEAM. A hitscan beam that locks onto one unit, bends its
        // way there around terrain, and damages everything it threads through.
        //
        // Three things the stock beam physically cannot do, so all three are
        // ours: the visual is ONE stretched quad (no room for a curve), the
        // damage cast hits exactly the FIRST thing in the way (no piercing),
        // and damage and visuals were two independent raycasts. See
        // ForgeCurveBeam for how the third stops being a problem.
        private static void ApplyCurveBeam(
            WeaponData weapon, JObject root, string fileName)
        {
            var o = root["curveBeam"] as JObject;

            if (o == null)
                return;

            if (!((bool?)o["enabled"] ?? true))
                return;

            var hs = weapon as HitscanWeaponData;

            if (hs == null)
            {
                Log.LogWarning(
                    fileName + ": \"curveBeam\" only works on a BEAM weapon, " +
                    "and this template is not one - so it is ignored. Build " +
                    "on a hitscan template such as " +
                    "\"Module Weapon Caps Laser\". " +
                    "(For a bullet that curves toward a target, the block you " +
                    "want is \"homing\".)");
                return;
            }

            var cfg = new ForgeCurveBeam.Config();

            // ---- the lock ---------------------------------------------------
            cfg.lockMode = ForgeCurveBeam.ParseLock((string)o["lock"]);
            cfg.lockRange = Mathf.Max(0f, (float?)o["lockRange"] ?? 0f);
            cfg.lockCone = Mathf.Clamp((float?)o["lockCone"] ?? 60f, 1f, 360f);
            cfg.objectsToo = (bool?)o["objectsToo"] ?? false;
            cfg.holdLock = (bool?)o["holdLock"] ?? true;
            cfg.reacquire = (bool?)o["reacquire"] ?? true;

            // ---- the curve --------------------------------------------------
            cfg.turnRate = Mathf.Clamp((float?)o["turnRate"] ?? 40f, 0f, 720f);
            cfg.segment = Mathf.Clamp((float?)o["segment"] ?? 0.4f, 0.1f, 4f);
            cfg.maxLength = Mathf.Max(0f, (float?)o["maxLength"] ?? 0f);
            cfg.smoothing = Mathf.Clamp01((float?)o["smoothing"] ?? 0.35f);

            // ---- weaving ----------------------------------------------------
            cfg.weave = (bool?)o["weave"] ?? false;
            cfg.weaveRange = Mathf.Clamp((float?)o["weaveRange"] ?? 4f, 0.5f, 30f);
            cfg.weaveMax = Mathf.Clamp((int?)o["weaveMax"] ?? 3, 0, 12);

            // ---- terrain ----------------------------------------------------
            cfg.route = ForgeCurveBeam.ParseRoute((string)o["route"]);
            cfg.lookAhead = Mathf.Max(0.2f, (float?)o["lookAhead"] ?? 1.4f);
            cfg.clearance = Mathf.Clamp((float?)o["clearance"] ?? 0.45f, 0.05f, 4f);

            // ---- damage -----------------------------------------------------
            cfg.pierce = (bool?)o["pierce"] ?? true;
            cfg.bystander = Mathf.Max(0f, (float?)o["bystander"] ?? 1f);
            cfg.maxTargets = Mathf.Max(0, (int?)o["maxTargets"] ?? 0);
            cfg.push = Mathf.Max(0f, (float?)o["push"] ?? 0f);

            // ---- art --------------------------------------------------------
            cfg.sprite = ((string)o["sprite"] ?? "").Trim();
            cfg.frames = Mathf.Clamp((int?)o["frames"] ?? 4, 1, 16);
            cfg.flow = (float?)o["flow"] ?? 6f;
            cfg.thickness = Mathf.Clamp((float?)o["thickness"] ?? 0.35f, 0.02f, 8f);
            cfg.overlap = Mathf.Clamp((float?)o["overlap"] ?? 0.35f, 0f, 2f);
            cfg.taper = Mathf.Clamp01((float?)o["taper"] ?? 0f);
            cfg.endPuff = (bool?)o["endPuff"] ?? true;

            cfg.explodeOnTarget = (bool?)o["explodeOnTarget"] ?? false;
            cfg.explodeInterval =
                Mathf.Max(0.05f, (float?)o["explodeInterval"] ?? 0.25f);

            string colour = (string)o["color"];

            if (!string.IsNullOrEmpty(colour))
            {
                Color c = Color.white;

                if (ReadColor(colour, "curveBeam \"color\"", fileName, ref c))
                {
                    cfg.color = c;
                    cfg.hasColor = true;
                }
            }

            // ---- the things worth saying out loud ---------------------------
            //
            // A LOOPING BEAM IS LONGER THAN ITS REACH. maxLength is the length
            // of the beam, not the distance to the target - the whole point of
            // a curve beam is that it does not travel in a straight line - so
            // leaving it at the weapon's range means a beam that curls twice
            // runs out well before an enemy it could see.
            float reach = (cfg.maxLength > 0f) ? cfg.maxLength : hs.range;

            if (cfg.maxLength <= 0f)
            {
                Log.LogInfo(
                    fileName + ": curveBeam has no \"maxLength\", so it uses " +
                    "the weapon's range (" + hs.range.ToString("0.#") + "). " +
                    "Note that is the length of the BEAM, not how far away it " +
                    "can reach - a beam that loops spends length on the loop. " +
                    "If it keeps stopping short, raise maxLength rather than " +
                    "range.");
            }

            // The path is capped at 160 links whatever the numbers say, so a
            // small segment silently shortens the beam. Better to say so than
            // to let someone tune a number that has stopped doing anything.
            float reachable = ForgeCurveBeamPath.MaxPoints * cfg.Segment;

            if (reachable < reach - 0.01f)
            {
                Log.LogWarning(
                    fileName + ": curveBeam \"segment\": " +
                    cfg.Segment.ToString("0.##") + " gives at most " +
                    reachable.ToString("0.#") + " units of beam (there is a " +
                    "hard cap of " + ForgeCurveBeamPath.MaxPoints + " links), " +
                    "but this weapon wants " + reach.ToString("0.#") + ". The " +
                    "beam will stop short. Use a longer \"segment\" for a " +
                    "longer beam - the curve stays smooth because the turn " +
                    "limit is per unit of length, not per link.");
            }

            if (cfg.weave && cfg.lockMode == ForgeCurveBeam.LockNone)
            {
                Log.LogWarning(
                    fileName + ": curveBeam \"weave\" needs something to weave " +
                    "TOWARD, and \"lock\": \"none\" gives it no destination - " +
                    "so there is no path for a detour to be on. Weaving is " +
                    "ignored. Use \"nearest\" or \"forward\".");
            }

            if (cfg.weave && cfg.route == ForgeCurveBeam.RoutePath)
            {
                Log.LogInfo(
                    fileName + ": curveBeam has both \"weave\" and " +
                    "\"route\": \"path\" - weaving wins, because routing a " +
                    "corridor to each detour in turn would be several " +
                    "searches per frame. Terrain is still avoided by the " +
                    "steering between detours, and \"steerThenPath\" still " +
                    "rescues a weave that dead-ends.");
            }

            if (cfg.weave && !cfg.pierce)
            {
                Log.LogWarning(
                    fileName + ": curveBeam \"weave\" with \"pierce\" off " +
                    "will route through other enemies and then only damage " +
                    "the FIRST one it reaches. That is a legitimate weapon - " +
                    "a beam that visibly threads a crowd to reach one target " +
                    "- but if you wanted it to hurt them all, turn pierce on.");
            }

            if (cfg.route == ForgeCurveBeam.RoutePath &&
                cfg.lockMode == ForgeCurveBeam.LockNone)
            {
                Log.LogWarning(
                    fileName + ": curveBeam \"route\": \"path\" with " +
                    "\"lock\": \"none\" has nothing to route TO - pathfinding " +
                    "needs a destination. It will steer instead, which still " +
                    "slides around terrain.");
            }

            if (!cfg.pierce && cfg.bystander != 1f)
            {
                Log.LogInfo(
                    fileName + ": curveBeam \"bystander\" is set but " +
                    "\"pierce\" is off, so the beam only ever damages one " +
                    "thing and the multiplier never applies.");
            }

            ForgeCurveBeam.Register(weapon, cfg);

            Log.LogInfo(
                fileName + ": curveBeam - locks onto " +
                ForgeCurveBeam.LockWord(cfg.lockMode) + ", " +
                (cfg.weave
                    ? "WEAVING through up to " + cfg.weaveMax +
                      " other target(s) within " +
                      cfg.weaveRange.ToString("0.#") +
                      " units of its path, "
                    : "") +
                ForgeCurveBeam.RouteWord(cfg.route) + ", turning up to " +
                cfg.Turn.ToString("0.#") + " degrees per unit (tightest loop " +
                "about " + (cfg.Turn > 0.01f
                    ? (360f / cfg.Turn).ToString("0.#")
                    : "n/a") +
                " units around), " +
                (cfg.pierce ? "piercing" : "hitting one target") +
                (cfg.pierce && cfg.maxTargets > 0
                    ? " up to " + cfg.maxTargets + " at a time"
                    : "") +
                (cfg.pierce && Mathf.Abs(cfg.bystander - 1f) > 0.001f
                    ? " (bystanders at " + cfg.bystander.ToString("0.##") + "x)"
                    : "") +
                ".");
        }

        private static void ApplySwipe(
            WeaponData weapon, JObject root, string fileName)
        {
            var o = root["swipe"] as JObject;

            if (o == null)
                return;

            if (!((bool?)o["enabled"] ?? true))
                return;

            var cfg = new ForgeSwipe.Config();

            cfg.arc = Mathf.Clamp((float?)o["arc"] ?? 120f, 1f, 360f);
            cfg.reach = Mathf.Max(0.2f, (float?)o["reach"] ?? 3.5f);
            cfg.thickness = Mathf.Max(0.05f, (float?)o["thickness"] ?? 0.7f);
            cfg.duration = Mathf.Max(0.02f, (float?)o["duration"] ?? 0.18f);
            cfg.ease = Mathf.Max(0.1f, (float?)o["ease"] ?? 2f);
            cfg.direction = ForgeSwipe.ParseDirection((string)o["direction"]);

            JToken dmg = o["damage"];

            if (dmg != null && dmg.Type != JTokenType.Null)
            {
                cfg.damage = Mathf.Max(0f, (float)dmg);
                cfg.hasDamage = true;
            }
            else
            {
                // No number given: use the weapon's own damage. A melee weapon
                // that mentions nothing should still cut.
                cfg.hasDamage = true;
            }

            string dtype = (string)o["damageType"];

            if (!string.IsNullOrEmpty(dtype))
            {
                var res = JsonFieldMapper.FindAsset(
                    typeof(Resource), dtype) as Resource;

                if (res != null)
                    cfg.damageType = res;
                else
                    Log.LogWarning(
                        fileName + ": swipe \"damageType\": '" + dtype +
                        "' is not a resource this game has. Using the weapon's " +
                        "own.");
            }

            cfg.knockback = (float?)o["knockback"] ?? 0f;
            cfg.marksSlammable = (bool?)o["marksSlammable"] ?? false;

            cfg.cutTerrain = Mathf.Max(0f, (float?)o["cutTerrain"] ?? 0f);
            cfg.igniteTerrain = Mathf.Max(0f, (float?)o["igniteTerrain"] ?? 0f);

            cfg.deflects = (bool?)o["deflects"] ?? true;
            cfg.aimMode = ForgeBulletTurn.ParseAim(
                (string)o["aim"] ?? "mirror");
            cfg.deflectSpeed = Mathf.Max(0.05f, (float?)o["deflectSpeed"] ?? 1f);
            cfg.deflectDamage =
                Mathf.Max(0f, (float?)o["deflectDamage"] ?? 1f);
            cfg.maxDeflect = Mathf.Max(1, (int?)o["maxDeflect"] ?? 8);

            cfg.sprite = ((string)o["sprite"] ?? "").Trim();
            cfg.trail = Mathf.Clamp((float?)o["trail"] ?? 0.55f, 0.05f, 1f);
            cfg.fadeOut = Mathf.Max(0f, (float?)o["fadeOut"] ?? 0.12f);
            cfg.sfx = ResolveChargeSfx(
                (string)o["sfx"], null, false, "swipe sfx", fileName) ?? "";

            string colour = (string)o["color"];

            if (!string.IsNullOrEmpty(colour))
            {
                Color c = Color.white;

                if (ReadColor(colour, "swipe \"color\"", fileName, ref c))
                {
                    cfg.color = c;
                    cfg.hasColor = true;
                }
            }

            if (!cfg.DoesAnything)
            {
                Log.LogWarning(
                    fileName + ": \"swipe\" does nothing - no damage, no " +
                    "knockback, no marking, no terrain cutting and deflecting " +
                    "switched off. Give it at least one of those.");
                return;
            }

            // Catching shots needs the tracker running: enemy projectiles have
            // their Collider2D DISABLED (they resolve hits by raycast), so they
            // are invisible to Physics2D.OverlapCircle at any radius and the
            // only way to find one is to have registered it when it was fired.
            if (cfg.deflects)
                ForgeProjectileTracker.Enabled = true;

            ForgeSwipe.Register(weapon, cfg);

            // ---- PURE MELEE: stop the weapon firing bullets ---------------
            //
            // Done by setting projectileCount to 0 rather than by suppressing
            // Shooter.Shoot, and the difference matters. Cost is taken INSIDE
            // Shoot, and so is knockback and the shoot sound - so a prefix that
            // blocked Shoot would give you a free swing and a silent one.
            // GetDirections loops `while (i < ProjectileCount)`, so zero yields
            // no pellets while DoShoot still runs, still plays the sound, and
            // still fires the swing hook.
            //
            // The cost of that: no muzzle flash either, since InitializeVisuals
            // builds one instance per projectile. The blade is the flash.
            bool melee = (bool?)o["pureMelee"] ?? false;

            if (melee)
            {
                var pw = weapon as ProjectileWeaponData;

                if (pw != null && pw.projectileCount > 0f)
                {
                    pw.projectileCount = 0f;

                    Log.LogInfo(
                        fileName + ": swipe \"pureMelee\" - projectileCount " +
                        "set to 0, so the trigger swings and fires nothing. " +
                        "The weapon still costs what it costs and still " +
                        "respects fireRate, because both of those are taken " +
                        "inside the shot rather than by the projectile. Note " +
                        "there is no muzzle flash at zero pellets - the blade " +
                        "is the flash.");
                }
                else if (pw == null)
                {
                    Log.LogWarning(
                        fileName + ": swipe \"pureMelee\" only works on a " +
                        "projectile weapon - a hitscan beam has no pellet " +
                        "count to zero, and a minion spawner has no projectile " +
                        "at all. The swing still happens; so does whatever the " +
                        "weapon normally fires.");
                }
            }

            // ---- what it is, said out loud --------------------------------
            var did = new List<string>();

            if (cfg.hasDamage)
            {
                did.Add(cfg.damage > 0f
                    ? "damage " + cfg.damage.ToString("0.##")
                    : "the weapon's own damage");
            }

            if (cfg.knockback != 0f)
                did.Add("knockback " + cfg.knockback.ToString("0.#") +
                        " ALONG the swing");

            if (cfg.marksSlammable)
                did.Add("marks slammable");

            if (cfg.cutTerrain > 0f)
                did.Add("cuts terrain at " + cfg.cutTerrain.ToString("0.#"));

            if (cfg.igniteTerrain > 0f)
                did.Add("sets terrain alight");

            if (cfg.deflects)
                did.Add("deflects up to " + cfg.maxDeflect + " shot(s) " +
                        ForgeBulletTurn.AimWord(cfg.aimMode));

            Log.LogInfo(
                fileName + ": swipe - a " + cfg.arc.ToString("0.#") +
                " degree swing, reach " + cfg.reach.ToString("0.##") +
                ", over " + cfg.duration.ToString("0.###") + "s, " +
                ForgeSwipe.DirectionWord(cfg.direction) + ". " +
                string.Join("; ", did.ToArray()) + ". Hits each target ONCE " +
                "per swing." +
                (string.IsNullOrEmpty(cfg.sprite)
                    ? " Art is the generated crescent, drawn from arc, reach " +
                      "and thickness so it always matches the swing."
                    : " Art is your sprite '" + cfg.sprite + "', placed at the " +
                      "blade and turned to the tangent."));

            // Two combinations worth pointing at, because both are better than
            // the sum of their parts and neither is guessable.
            if (cfg.knockback != 0f && root["slam"] == null)
            {
                Log.LogInfo(
                    fileName + ": swipe - this swing SHOVES but the weapon has " +
                    "no \"slam\" block, so arriving at a wall costs them " +
                    "nothing. Adding one is what turns a bat into a kill: " +
                    "\"slam\": { \"damagePerSpeed\": 1.6 }.");
            }

            if (cfg.arc >= 340f)
            {
                Log.LogInfo(
                    fileName + ": swipe - an arc of " +
                    cfg.arc.ToString("0.#") + " is a full SPIN rather than a " +
                    "swing, so everything around you is hit once per pull. " +
                    "Worth a longer \"duration\" so it reads as a spin.");
            }
        }

        // "slam": arriving somewhere hard hurts.
        //
        // A STANDALONE PROPERTY, in the same sense as `discharge` or
        // `aimAssistData`: bolt it onto any weapon and whatever that weapon
        // touches becomes brittle for a moment. It is deliberately NOT tied to
        // the thing that throws them - see `requiresPush` - because "this one
        // is fragile right now" composes with far more than a shove does:
        //
        //   slam + pushForce   one weapon does both (the Riot Wave preset)
        //   slam + gravity     the well flings them, the mark prices it
        //   slam + explosion   the blast throws them, the mark prices it
        //   slam + slow        slow LOWERS MASS, so everything throws further
        //   slam alone         a marker gun. Shove them with your other weapon,
        //                      or let them charge you and clip a wall
        //
        // Five routes mark a target: both projectile contact paths, and the
        // three damage paths (which is what covers a hitscan beam, an
        // explosion and a discharge arc). The gravity well marks what it drags
        // separately, because a well never "hits" anything.
        //
        // The game already does the shoving where a shove is wanted: both
        // projectile classes apply
        // `AddForceAtPosition(Velocity.normalized * PushForce, hit.point,
        // ForceMode2D.Impulse)` to whatever they hit, so a weapon with a big
        // `pushForce` is already a shove weapon - it just had nothing to show
        // for it, because arriving at a wall hurt nobody. The machinery that
        // prices the arrival was built for the tractor beam.
        //
        //   "weapon": { "pushForce": 220, "projectileCount": 9, "spread": 60 },
        //   "slam": { "damagePerSpeed": 1.6, "minSpeed": 6 }
        //
        // THE ARITHMETIC, because one number never fits in this game and a raw
        // force is meaningless without it. An IMPULSE gives an instant velocity
        // change of `pushForce / mass`, and linear damping then bleeds it off,
        // so the distance a body actually travels is
        //
        //     pushForce / (mass * linearDamping)
        //
        // A common `Enemy_Cross` is mass 5, damping 6 - so 30, and a pushForce
        // of 220 carries it about 7 units. The stock White Shotgun's
        // `pushForce: 10` carries it a third of a unit, which is why the stock
        // shove reads as a nudge. A Jock or a Red is mass 50 and a Queen is 200,
        // so the same shot moves them 10x and 40x less; pairing this with
        // `slow` helps, because slow's `knockback` switch works by LOWERING
        // mass.
        private static void ApplySlam(
            WeaponData weapon, JObject root, string fileName)
        {
            JToken tok = root["slam"];

            if (tok == null || tok.Type == JTokenType.Null)
                return;

            var o = tok as JObject;
            var spec = new ForgeSlam.Spec();

            if (o != null)
            {
                if (!((bool?)o["enabled"] ?? true))
                    return;

                spec.minSpeed = Mathf.Max(0f, (float?)o["minSpeed"] ?? 6f);
                spec.damagePerSpeed =
                    Mathf.Max(0f, (float?)o["damagePerSpeed"] ?? 1.5f);
                spec.maxDamage = Mathf.Max(0f, (float?)o["maxDamage"] ?? 999f);
                spec.window = Mathf.Max(0.1f, (float?)o["window"] ?? 1.5f);
                spec.hitsUnits = (bool?)o["hitsUnits"] ?? true;
                spec.hitsPlayer = (bool?)o["hitsPlayer"] ?? false;
                spec.requiresPush = (bool?)o["requiresPush"] ?? false;

                // OFF by default on purpose - this is what shipped, and it is
                // not being changed underneath anything already tested. See
                // ForgeSlam.Spec.swept for why it exists.
                spec.swept = (bool?)o["swept"] ?? false;

                spec.tintPulse = Mathf.Max(0f, (float?)o["tintPulse"] ?? 3f);
                spec.tintStrength = TintStrength(o, fileName, "slam", 1f);

                string tintName = (string)o["tint"];

                if (!string.IsNullOrEmpty(tintName))
                {
                    Color c = Color.white;

                    if (ReadColor(tintName, "slam \"tint\"", fileName, ref c))
                    {
                        spec.tint = c;
                        spec.hasTint = true;
                    }
                }

                string type = (string)o["damageType"];

                if (!string.IsNullOrEmpty(type))
                {
                    var res = JsonFieldMapper.FindAsset(
                        typeof(Resource), type) as Resource;

                    if (res != null)
                        spec.damageType = res;
                    else
                        Log.LogWarning(
                            fileName + ": slam \"damageType\": '" + type +
                            "' is not a resource this game has. Using the " +
                            "weapon's own damage type.");
                }
            }
            else
            {
                // A bare number is the friendly form: damage per unit of
                // closing speed, everything else default.
                spec.damagePerSpeed = Mathf.Max(0f, (float)tok);
            }

            if (spec.damagePerSpeed <= 0f)
            {
                Log.LogWarning(
                    fileName + ": \"slam\" damagePerSpeed is 0, so arriving at " +
                    "a wall would cost nothing. Use \"slam\": 1.5, or " +
                    "{ \"damagePerSpeed\": 1.5 }.");
                return;
            }

            ForgeSlam.Register(weapon, spec);

            if (spec.hasTint)
            {
                Log.LogInfo(
                    fileName + ": slam \"tint\" - anything marked wears this " +
                    "colour for the " + spec.window.ToString("0.##") +
                    "s window" +
                    (spec.tintPulse > 0f
                        ? ", throbbing " + spec.tintPulse.ToString("0.#") +
                          " times a second so you can see the timer running out"
                        : ", steady") +
                    ". It MULTIPLIES over the unit's own colours and composes " +
                    "with the \"slow\" tint rather than fighting it - two " +
                    "strong tints do compound, so a unit that is both slowed " +
                    "and slammable reads as doubly odd, which is the point.");
            }

            // --- is this weapon actually able to shove anything? -----------
            var pw = weapon as ProjectileWeaponData;

            float push = weapon.pushForce;

            if (push <= 0f && spec.requiresPush)
            {
                Log.LogWarning(
                    fileName + ": \"slam\" has \"requiresPush\" on but this " +
                    "weapon's \"pushForce\" is 0, so nothing is ever shoved " +
                    "and the slam can never fire at all. Either give it a " +
                    "pushForce or turn requiresPush off - without it, marking " +
                    "on a plain hit is the point and something ELSE does the " +
                    "throwing. A common enemy is mass 5 with damping 6, and a " +
                    "shove carries pushForce / (mass x damping) units, so 220 " +
                    "moves one about 7 units. (The stock White Shotgun ships " +
                    "pushForce 10, which is a third of a unit.)");
            }
            else if (push <= 0f)
            {
                // NOT a warning. A weapon that marks without shoving is a
                // legitimate and interesting thing - it just needs saying out
                // loud, because "I set slam and nothing happened" would
                // otherwise be a mystery when the answer is "yes, and now
                // throw them at something".
                Log.LogInfo(
                    fileName + ": slam - this weapon MARKS what it hits as " +
                    "slammable (" + spec.damagePerSpeed.ToString("0.##") +
                    " per unit of closing speed above " +
                    spec.minSpeed.ToString("0.#") + ", for " +
                    spec.window.ToString("0.#") + "s) but does not shove it " +
                    "itself - \"pushForce\" is 0. That is a marker weapon, " +
                    "and it is a real design: something else has to do the " +
                    "throwing. A \"gravity\" well, an explosion, a tractor " +
                    "beam, your other gun, or the enemy charging you and " +
                    "clipping a wall all count. Add \"pushForce\" if you " +
                    "wanted the one weapon to do both.");
            }
            else
            {
                // Said out loud in the units he will judge it in, the same way
                // the gravity well and the heat model report themselves.
                float perCross = push / 30f;

                Log.LogInfo(
                    fileName + ": slam - " +
                    spec.damagePerSpeed.ToString("0.##") +
                    " damage per unit of closing speed above " +
                    spec.minSpeed.ToString("0.#") +
                    (spec.maxDamage < 999f
                        ? ", capped at " + spec.maxDamage.ToString("0.#")
                        : "") +
                    ", for " + spec.window.ToString("0.#") +
                    "s after being shoved" +
                    (spec.hitsUnits
                        ? ", and crashing into another unit counts" +
                          (spec.hitsPlayer ? " INCLUDING YOU" : "")
                        : ", terrain only") +
                    ". pushForce " + push.ToString("0.#") +
                    " carries a common enemy about " +
                    perCross.ToString("0.#") + " units (mass 5 x damping 6), " +
                    "so aim it at something close." +
                    (spec.requiresPush
                        ? " \"requiresPush\" is on, so ONLY a hit that shoves " +
                          "marks them."
                        : " Any hit from this weapon marks them, whether or " +
                          "not the shove landed - so a gravity well, a blast " +
                          "or your other gun can be what throws them."));
            }

            if (pw != null && pw.piercingData.enabled)
            {
                Log.LogWarning(
                    fileName + ": \"slam\" with PIERCING on - and a piercing " +
                    "shot NEVER PUSHES ANYTHING. The projectile code returns " +
                    "out of the hit early when piercing is enabled, and the " +
                    "knockback line sits below that return, so the two are " +
                    "mutually exclusive. Turn \"piercingData\".enabled off (or " +
                    "drop \"pierceLimit\") for a shove weapon.");
            }

            if (pw != null && pw.usePhysics)
            {
                Log.LogInfo(
                    fileName + ": slam on a LOBBED weapon - worth knowing that " +
                    "a physics shot pushes on EVERY collision it has, with no " +
                    "repeat delay at all, so a bomb that rolls along a line of " +
                    "enemies shoves each one.");
            }

            if (spec.hitsPlayer)
            {
                Log.LogWarning(
                    fileName + ": slam \"hitsPlayer\" is on, so an enemy flung " +
                    "into YOUR hull hurts you as well as it. That is a real " +
                    "way to play it, but it is off by default for a reason.");
            }
        }

        // "fade": a shot that eases in at the muzzle and out as it dies.
        //
        // Nothing in the game does this - every ammo prefab is a SpriteRenderer
        // at full alpha for its whole life which then simply disappears. Asked
        // for by name ("if there isn't a way to add a physic and non physic
        // projectile fade, id like to add that feature"), so it deliberately
        // covers BOTH projectile classes.
        //
        //   "fade": 0.15                     - both ends, 0.15s each
        //   "fade": { "in": 0.1, "out": 0.4 } - separately
        //
        // The fade-out needs to know when the shot ENDS, and the two classes
        // answer that differently - a plain shot from its range or lifetime, a
        // lobbed one from its lifetime alone, because `rangeData` is inert on a
        // lobbed shot. A shot with neither gets `assumeLife` or no fade-out at
        // all, said out loud rather than guessed at.
        private static void ApplyFade(
            WeaponData weapon, JObject root, string fileName)
        {
            JToken tok = root["fade"];

            if (tok == null || tok.Type == JTokenType.Null)
                return;

            float fadeIn, fadeOut, assume;
            bool everything;

            var o = tok as JObject;

            if (o != null)
            {
                if (!((bool?)o["enabled"] ?? true))
                    return;

                fadeIn = Mathf.Max(0f, (float?)o["in"] ?? 0f);
                fadeOut = Mathf.Max(0f, (float?)o["out"] ?? 0f);
                assume = Mathf.Max(0f, (float?)o["assumeLife"] ?? 0f);
                everything = (bool?)o["trail"] ?? true;

                // SAY IT IN UNITS INSTEAD, because units are what you can see.
                //
                // The field is in SECONDS and stays that way - it is the honest
                // unit for a thing driven by a clock, and a lobbed shot has no
                // meaningful speed to convert through. But R13 test 144 was the
                // second time in three rounds that a fade was reported as
                // absent when it was working, and both times the cause was the
                // author picking a number of seconds whose DISTANCE was wrong.
                // The build log already prints the conversion; doing the
                // conversion for him removes the step entirely.
                float inUnits = Mathf.Max(0f, (float?)o["inUnits"] ?? 0f);
                float outUnits = Mathf.Max(0f, (float?)o["outUnits"] ?? 0f);

                if (inUnits > 0f || outUnits > 0f)
                {
                    var pwu = weapon as ProjectileWeaponData;
                    float sp = (pwu != null) ? pwu.projectileSpeed : 0f;

                    if (sp <= 0.01f)
                    {
                        Log.LogWarning(
                            fileName + ": fade \"inUnits\"/\"outUnits\" need a " +
                            "projectileSpeed to convert into seconds, and this " +
                            "weapon's is " + sp.ToString("0.##") + ". Use " +
                            "\"in\" and \"out\" (seconds) instead. Ignored.");
                    }
                    else
                    {
                        if (inUnits > 0f)
                            fadeIn = inUnits / sp;

                        if (outUnits > 0f)
                            fadeOut = outUnits / sp;

                        Log.LogInfo(
                            fileName + ": fade given in UNITS - " +
                            (inUnits > 0f
                                ? "in over " + inUnits.ToString("0.#") +
                                  " units = " + fadeIn.ToString("0.###") + "s"
                                : "") +
                            (inUnits > 0f && outUnits > 0f ? ", " : "") +
                            (outUnits > 0f
                                ? "out over " + outUnits.ToString("0.#") +
                                  " units = " + fadeOut.ToString("0.###") + "s"
                                : "") +
                            " at projectileSpeed " + sp.ToString("0.##") +
                            ". Note this is fixed at BUILD time: a module that " +
                            "changes the speed later will not re-scale it.");
                    }
                }
            }
            else
            {
                // A bare number is the friendly form: the same fade at both
                // ends, which is what "fade" means to most people.
                float both = Mathf.Max(0f, (float)tok);
                fadeIn = both;
                fadeOut = both;
                assume = 0f;
                everything = true;
            }

            if (fadeIn <= 0f && fadeOut <= 0f)
            {
                Log.LogWarning(
                    fileName + ": \"fade\" is 0 at both ends, so it does " +
                    "nothing. Use \"fade\": 0.2, or " +
                    "{ \"in\": 0.1, \"out\": 0.4 }.");
                return;
            }

            var pw = weapon as ProjectileWeaponData;

            if (!ProjectileTargetOk(pw, "\"fade\"", fileName, true, null))
                return;

            GameObject source = pw.usePhysics
                ? (pw.physicsProjectilePrefab != null
                    ? pw.physicsProjectilePrefab.gameObject : null)
                : (pw.projectilePrefab != null
                    ? pw.projectilePrefab.gameObject : null);

            if (source == null)
                return;

            GameObject clone = VisualCustomizer.ClonePrefab(source);

            if (clone == null)
                return;

            // On the projectile's OWN GameObject, not the clone root. The
            // Projectile component sits on the root of all 51 stock ammo
            // prefabs, but ClonePrefab parents the clone under a holder, so
            // going through the component is what keeps this correct if a
            // prefab ever differs.
            Component comp = pw.usePhysics
                ? (Component)clone.GetComponentInChildren<PhysicsProjectile>(true)
                : clone.GetComponentInChildren<Projectile>(true);

            if (comp == null)
            {
                Log.LogWarning(
                    fileName + ": \"fade\" - no projectile component on the " +
                    "prefab. Ignored.");
                UnityEngine.Object.Destroy(clone);
                return;
            }

            var fade = comp.gameObject.GetComponent<ForgeFade>();

            if (fade == null)
                fade = comp.gameObject.AddComponent<ForgeFade>();

            fade.inSeconds = fadeIn;
            fade.outSeconds = fadeOut;
            fade.everything = everything;
            fade.assumeLife = assume;

            if (pw.usePhysics)
                pw.physicsProjectilePrefab = (PhysicsProjectile)comp;
            else
                pw.projectilePrefab = (Projectile)comp;

            // Can this weapon's shots even reach their fade-out?
            bool bounded =
                pw.lifetimeData.enabled ||
                (!pw.usePhysics && pw.rangeData.enabled);

            Log.LogInfo(
                fileName + ": fade - " +
                (fadeIn > 0f ? "in over " + fadeIn.ToString("0.##") + "s" : "") +
                (fadeIn > 0f && fadeOut > 0f ? ", " : "") +
                (fadeOut > 0f ? "out over " + fadeOut.ToString("0.##") + "s" : "") +
                (everything
                    ? ", trail and glow included"
                    : ", the bullet sprite only") + ".");

            if (fadeOut > 0f && !bounded && assume <= 0f)
            {
                Log.LogWarning(
                    fileName + ": fade \"out\" needs to know when the shot " +
                    "ENDS, and this weapon has no lifetimeData" +
                    (pw.usePhysics
                        ? " (and \"rangeData\" is completely inert on a " +
                          "lobbed shot - the game computes a range time for one " +
                          "and then never reads it again)"
                        : " and no rangeData") +
                    ", so nothing bounds it. The fade-IN still works. Add a " +
                    "\"lifetimeData\" or a fade \"assumeLife\" to get the " +
                    "fade-out.");
            }

            // SAY IT IN UNITS OF TRAVEL, NOT JUST SECONDS - because distance is
            // what decides whether the fade is on screen to be seen.
            //
            // R12 test 27 was reported as "fade is still not working correctly"
            // on a weapon where it was working exactly as written: speed 10,
            // lifetime 8s, in 3s / out 3s. The per-frame instrument confirmed
            // the alpha was being applied and read back correctly. But 3
            // seconds of fade-in at speed 10 is THIRTY UNITS of travel before
            // the shot is at full brightness, and the camera shows nothing like
            // thirty units - so the bullet is faint for its whole time on
            // screen and reads as broken.
            //
            // Seconds are the honest unit for the field and I am not changing
            // it. What was missing is that nothing told the author the number
            // in the unit that matters. Two log lines cost nothing and would
            // have saved the round.
            float speed = pw.usePhysics ? 0f : pw.projectileSpeed;

            if (speed > 0.01f)
            {
                string msg =
                    fileName + ": fade in units of TRAVEL - at " +
                    "projectileSpeed " + speed.ToString("0.##") + ", ";

                if (fadeIn > 0f)
                {
                    msg += "the shot is not at full brightness until it is " +
                           (fadeIn * speed).ToString("0.#") + " units out";
                }

                if (fadeIn > 0f && fadeOut > 0f)
                    msg += ", and ";

                if (fadeOut > 0f)
                {
                    msg += "the fade-out covers the last " +
                           (fadeOut * speed).ToString("0.#") + " units of " +
                           "its flight";
                }

                msg += ". Compare that with how much of the room you can see: " +
                       "a fade measured in tens of units happens off screen.";

                Log.LogInfo(msg);

                // THE FRACTION OF ITS OWN FLIGHT, which is the test the
                // absolute one below misses.
                //
                // R13 test 144 slipped through: at projectileSpeed 4 a
                // 3-second fade-in is only 12 units, well under the 20 that
                // triggers the warning below - but the shot's whole life was 8
                // seconds, so the fade-in ate 37% of it and the bullet was
                // never brighter than dim for most of what he could see. A
                // SLOW shot makes an absolute-distance check quieter and the
                // actual problem worse, which is exactly backwards.
                //
                // So there are two independent failure modes and they need two
                // tests: the fade being longer than the ROOM (absolute), and
                // the fade being most of the FLIGHT (proportional).
                float life = 0f;

                if (pw.lifetimeData.enabled)
                    life = pw.lifetimeData.time;

                if (pw.rangeData.enabled && speed > 0.01f)
                {
                    float byRange = pw.rangeData.range / speed;

                    if (pw.rangeData.slowDown)
                        byRange *= 2f;

                    life = (life > 0f) ? Mathf.Min(life, byRange) : byRange;
                }

                if (life <= 0f)
                    life = assume;

                if (life > 0f && fadeIn > 0f && fadeIn / life > 0.3f)
                {
                    Log.LogWarning(
                        fileName + ": fade \"in\" of " +
                        fadeIn.ToString("0.##") + "s is " +
                        (100f * fadeIn / life).ToString("0") + "% OF THE " +
                        "SHOT'S WHOLE LIFE (" + life.ToString("0.##") +
                        "s), so it spends most of its flight dimmer than half " +
                        "brightness. That reads as a faint bullet rather than " +
                        "as one fading in - and it gets worse, not better, on " +
                        "a slow shot. Try " +
                        (life * 0.15f).ToString("0.##") + "s. Also note the " +
                        "shot DIES ON CONTACT unless " +
                        "\"impactBehaviour\": { \"enabled\": false }, so its " +
                        "real life in a fight is shorter than " +
                        life.ToString("0.##") + "s - watch the log for the " +
                        "\"fade AT DEATH\" line, which measures what actually " +
                        "happened.");
                }

                // The fade-in alone eating most of a visible flight is the
                // specific shape that got reported as a bug.
                if (fadeIn * speed > 20f)
                {
                    Log.LogWarning(
                        fileName + ": fade \"in\": " +
                        fadeIn.ToString("0.##") + "s at projectileSpeed " +
                        speed.ToString("0.##") + " means the shot spends its " +
                        "first " + (fadeIn * speed).ToString("0.#") +
                        " units fading UP from invisible - almost certainly " +
                        "further than you can see. It will look like a dim " +
                        "bullet that never appears rather than one that fades " +
                        "in. Either shorten \"in\" or slow the shot down.");
                }
            }
        }

        // Make a minion spawner behave like a PLAYER weapon.
        //
        // THE NUMBERS, because they are the whole reason this exists. Every one
        // of the game's minion-spawner assets is a BOSS ATTACK, and they all
        // ship the same shape:
        //
        //   Weapon Minion CrossSmallCaps   fireRate 0.2  warmup 2  burst 4
        //   Weapon Minion Cross Red        fireRate 0.2  warmup 2  burst 4
        //   Weapon_Minion_CrossQueen       fireRate 0.2  warmup 2  burst 4
        //   Weapon Minion CrossRedBomber   fireRate 0.2  warmup 2  burst 1
        //
        // plus `angleVariance` 10-30 and `projectileSpeed` 60 with a variance
        // of 40. Read that as a player weapon: hold the trigger, wait TWO
        // SECONDS while nothing happens, get four minions at once flung out at
        // 20-100 units/s in a 60-degree cone, then wait five seconds for the
        // next lot. Which is exactly what was reported - "it takes like 4
        // seconds to shoot them out, a TON of them", "an insane charge time",
        // "I dont think im able to change those firing settings".
        //
        // AND THE SETTINGS WERE REACHING THE GAME ALL ALONG. `warmupTime` was
        // the missing one: it is the game's minigun spin-up, `Shooter.Update`
        // gates every shot on `IsWarmedUp`, and no file ever set it - so a
        // template value of 2 stood. The other three only looked ignored
        // because a file that mentions `fireRate` still inherits `burstSize 4`
        // and `warmupTime 2` from the template, and the delay dominates.
        //
        // So: anything the file does NOT mention gets a player-weapon value,
        // once, with the change stated in the log. Anything it DOES mention is
        // left exactly as asked - including a deliberate 2-second spin-up.
        private static void TameSpawner(
            WeaponData weapon, JObject root, string fileName)
        {
            var pw = weapon as MinionSpawnerWeaponData;

            if (pw == null)
                return;

            var w = root["weapon"] as JObject;
            var changes = new List<string>();

            // warmupTime: the one nobody knew about, and the one that mattered
            // most. Zero is right for a player weapon; a spin-up is a choice.
            if (!Mentions(w, "warmupTime") && pw.warmupTime > 0f)
            {
                changes.Add(
                    "warmupTime " + pw.warmupTime.ToString("0.##") +
                    " -> 0 (that is a spin-up: the game refuses to fire at all " +
                    "until you have held the trigger that long)");

                pw.warmupTime = 0f;
            }

            // burstSize: minions per pull is burstSize x projectileCount, so 4
            // here is four minions a click before any cap is consulted.
            if (!Mentions(w, "burstSize") && pw.burstSize > 1)
            {
                changes.Add(
                    "burstSize " + pw.burstSize + " -> 1 (that many minions " +
                    "came out per click)");

                pw.burstSize = 1;
            }

            // fireRate is shots per SECOND, so 0.2 is one pull every 5s.
            if (!Mentions(w, "fireRate") && pw.fireRate > 0f && pw.fireRate < 0.5f)
            {
                changes.Add(
                    "fireRate " + pw.fireRate.ToString("0.###") + " -> 1 (it is " +
                    "shots per SECOND, so " + pw.fireRate.ToString("0.###") +
                    " meant one pull every " +
                    (1f / pw.fireRate).ToString("0.#") + "s)");

                pw.fireRate = 1f;
            }

            // A 30-degree scatter is how a boss sprays a room. Aiming a swarm
            // gun should send minions where you pointed.
            if (!Mentions(w, "angleVariance") && pw.angleVariance > 5f)
            {
                changes.Add(
                    "angleVariance " + pw.angleVariance.ToString("0.#") +
                    " -> 0 (they were thrown in a " +
                    (pw.angleVariance * 2f).ToString("0.#") + "-degree cone)");

                pw.angleVariance = 0f;
            }

            // Speed variance above the speed itself means some minions come out
            // backwards. Clamp it to the speed rather than zeroing it - a
            // little spread is what makes a swarm look alive.
            if (!Mentions(w, "projectileSpeedVariance") &&
                pw.projectileSpeedVariance > pw.projectileSpeed * 0.5f &&
                pw.projectileSpeed > 0f)
            {
                float was = pw.projectileSpeedVariance;
                pw.projectileSpeedVariance = pw.projectileSpeed * 0.25f;

                changes.Add(
                    "projectileSpeedVariance " + was.ToString("0.#") + " -> " +
                    pw.projectileSpeedVariance.ToString("0.#") +
                    " (it was more than half the speed, so some came out barely " +
                    "moving)");
            }

            if (changes.Count == 0)
                return;

            Log.LogInfo(
                fileName + ": minion spawner tamed - " +
                string.Join("; ", changes.ToArray()) + ". Every one of the " +
                "game's minion spawners is a BOSS attack and these are its " +
                "values; a file that does not mention them inherits them. Set " +
                "any of them yourself and yours is used instead, untouched.");
        }

        // "spawner": OUR OWN SPAWNER WEAPON - the opt-in that finishes the job
        // TameSpawner starts.
        //
        //   "spawner": true
        //   "spawner": { "enabled": true, "perShot": 3 }
        //
        // WHY THIS IS OPT-IN AND TameSpawner IS NOT. TameSpawner fixes the five
        // values that make a spawner look BROKEN - a two-second wind-up, four
        // minions a click, a pull every five seconds, a 30-degree scatter, and a
        // speed variance so wide some minions come out backwards. Those are not
        // choices anyone made, so they are corrected for everybody.
        //
        // The three below are different: they are legitimate boss settings that
        // merely make a poor PLAYER weapon. A hard 60 unit/second throw is right
        // for a Queen flinging larvae across an arena; free spawns are right for
        // something that is not paying your resource bar. Changing those under a
        // file that never asked would be rewriting a weapon someone tuned - so
        // they move only when a file says "this is a player weapon".
        //
        // NOTE ON WHAT IS DELIBERATELY NOT HERE. There is no "speed", "cost" or
        // "spread" key in this block, and that is the point: they already exist
        // in "weapon" and a second spelling for the same field is how a builder
        // page and a parser drift apart. This block only says WHICH DEFAULTS
        // APPLY. Everything is still tuned in "weapon".
        private static void ApplySpawner(
            WeaponData weapon, JObject root, string fileName)
        {
            JToken token = root["spawner"];

            if (token == null || token.Type == JTokenType.Null)
                return;

            var o = token as JObject;

            if (o == null)
            {
                if (token.Type != JTokenType.Boolean || !(bool)token)
                {
                    Log.LogWarning(
                        fileName + ": \"spawner\" should be true or an object " +
                        "- ignored.");
                    return;
                }
            }
            else if (!((bool?)o["enabled"] ?? true))
            {
                return;
            }

            var pw = weapon as MinionSpawnerWeaponData;

            if (pw == null)
            {
                Log.LogWarning(
                    fileName + ": \"spawner\" only applies to a minion-spawner " +
                    "weapon, and this template is not one. Pick a spawner " +
                    "template (the builder page lists them) and set " +
                    "\"weapon\".minionPrefab. Ignored.");
                return;
            }

            var w = root["weapon"] as JObject;
            var changes = new List<string>();

            // 60 units/second is a boss hurling larvae across an arena. A
            // player spawner wants them to arrive where you pointed and still
            // be there - 14 is a brisk lob that clears the ship without
            // launching them into the next room.
            if (!Mentions(w, "projectileSpeed") && pw.projectileSpeed > 25f)
            {
                changes.Add(
                    "projectileSpeed " + pw.projectileSpeed.ToString("0.#") +
                    " -> 14 (they were being thrown, not placed)");

                pw.projectileSpeed = 14f;

                // The variance is a fraction of the old speed and would now be
                // most of the new one. TameSpawner already clamped the worst
                // case; this keeps the ratio sane after the speed moves.
                if (!Mentions(w, "projectileSpeedVariance") &&
                    pw.projectileSpeedVariance > 3.5f)
                {
                    pw.projectileSpeedVariance = 3.5f;
                }
            }

            // Every stock spawner ships cost 0, because a boss does not pay a
            // resource bar. A free minion printer is not a weapon, it is a
            // button you hold.
            if (!Mentions(w, "cost") && pw.cost <= 0f)
            {
                changes.Add(
                    "cost 0 -> 1 (every stock spawner is free because a boss " +
                    "has no resource bar)");

                pw.cost = 1f;
            }

            // The headline request: "the spawns spawn from the direction the
            // players gun is facing". The LAUNCH direction was always the
            // barrel's - MinionSpawnerWeapon.FireSingle spawns at
            // `position + direction * BarrelLength` and sets the velocity along
            // `direction` - but the minion itself was left pointing wherever
            // its prefab happened to face unless this was on.
            if (!Mentions(w, "minionFacesShootDirection") &&
                !pw.minionFacesShootDirection)
            {
                changes.Add(
                    "minionFacesShootDirection -> true (they now come out " +
                    "facing where you aimed)");

                pw.minionFacesShootDirection = true;
            }

            // projectileCount FANS the spawn across "spread" - on a spawner it
            // is minions per pull, not pellets. Left alone unless it is one of
            // the odd template values.
            if (!Mentions(w, "projectileCount") && pw.projectileCount > 1f)
            {
                changes.Add(
                    "projectileCount " + pw.projectileCount.ToString("0.#") +
                    " -> 1 (on a spawner that is minions per pull, fanned " +
                    "across \"spread\")");

                pw.projectileCount = 1f;
            }

            // The one convenience worth having, because "minions per pull =
            // burstSize x projectileCount" is genuinely hard to hold in your
            // head - and getting it wrong fills the whole cap in one click,
            // which looks exactly like a broken cap.
            //
            // It sets projectileCount rather than burstSize so they arrive
            // TOGETHER from one press rather than staggered burstDelay apart.
            int perShot = Mathf.Clamp((int?)((o != null) ? o["perShot"] : null) ?? 0, 0, 32);

            if (perShot > 0)
            {
                pw.projectileCount = perShot;
                pw.burstSize = 1;

                changes.Add(
                    "perShot " + perShot + " (projectileCount " + perShot +
                    " x burstSize 1)" +
                    (perShot > 1 && pw.spread <= 0f
                        ? " - they will come out on top of each other, so set " +
                          "\"weapon\".spread to fan them"
                        : ""));
            }

            Log.LogInfo(
                fileName + ": \"spawner\" - player-weapon defaults" +
                (changes.Count > 0
                    ? ": " + string.Join("; ", changes.ToArray())
                    : " (nothing to change - the file already set them)") +
                ". Anything you set in \"weapon\" yourself is left alone.");

            // The aim half of his request, and the half that is FREE - said out
            // loud because it is the thing people assume is missing.
            Log.LogInfo(
                fileName + ": spawner aim - minions launch along the BARREL, so " +
                "they already come out where you are pointing. On a GADGET key " +
                "the game aims a fabricated barrel due east instead; Weapon " +
                "Forge redirects that to your cursor.");
        }

        // Did the file's "weapon" block mention this field at all? "Absent" has
        // to differ from "set to the template's value", or taming would
        // overwrite a deliberate choice.
        private static bool Mentions(JObject weaponBlock, string key)
        {
            if (weaponBlock == null)
                return false;

            JToken t = weaponBlock[key];

            return t != null && t.Type != JTokenType.Null;
        }

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
            // DEFAULTS TO 1 WHEN THERE IS A CAP, as of Round 10.
            //
            // It used to default to 0, which made the boost display lie: a
            // BoosterCore raises the module's level and the grid draws one
            // green up-arrow per level, but with no capPerLevel the cap never
            // moved - so the card said "boosted, boosted, boosted" and the
            // weapon did exactly the same thing. His words: "when we set the
            // minion cap for the weapon it should just be set at that cap, so
            // when we do boost the cap with a booster the display is correct."
            //
            // One arrow, one more minion. `"capPerLevel": 0` still opts out.
            int perLevel = Mathf.Max(
                0, (int?)o["capPerLevel"] ?? (cap > 0 ? 1 : 0));
            // ON by default as of Round 10, at his request. The old default was
            // off out of caution about cost - a cap of twenty means twenty
            // minions each carrying a copy of your grid - but a minion that
            // ignores the modules you built around it is the more surprising
            // behaviour of the two. The cost is still warned about below.
            bool inheritGrid = (bool?)o["inheritGrid"] ?? true;
            string capModeText = (string)o["capMode"];
            float lookScale = Mathf.Max(0f, (float?)o["scale"] ?? 1f);
            string colorText = (string)o["color"];
            string colorModeText = (string)o["colorMode"];
            string scaleModeText = (string)o["scaleMode"];
            string trailColorText = (string)o["trailColor"];

            // Any one of these is worth registering on its own - a file may only
            // want a cap, or only a lifetime, without touching the gun.
            if (string.IsNullOrEmpty(named) && string.IsNullOrEmpty(side) &&
                life <= 0f && cap <= 0 && !inheritGrid &&
                string.IsNullOrEmpty(capModeText) &&
                string.IsNullOrEmpty(colorText) &&
                string.IsNullOrEmpty(trailColorText) && lookScale == 1f &&
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
                trailColorText, fileName);

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
                        fileName + ": minion cap " + cap + ", +" + perLevel +
                        " per module level - so a BoosterCore raises it, and " +
                        "the green up-arrows on the card now mean something " +
                        "(boostability is switched on for you). One arrow, " +
                        (perLevel == 1 ? "one more minion" :
                         perLevel + " more minions") +
                        ". Set \"capPerLevel\": 0 to keep the cap fixed while " +
                        "still allowing boosts.");
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
            string trailColorText,
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

            // The movement smear. A separate key from "color" because the body
            // and the streak are genuinely separate choices - a red drone with
            // a cyan trail is a reasonable thing to want.
            if (!string.IsNullOrEmpty(trailColorText))
            {
                string t = trailColorText.Trim();

                if (t.Equals("match", System.StringComparison.OrdinalIgnoreCase) ||
                    t.Equals("body", System.StringComparison.OrdinalIgnoreCase) ||
                    t.Equals("same", System.StringComparison.OrdinalIgnoreCase))
                {
                    if (cfg.hasColor)
                    {
                        cfg.trailMatchesBody = true;

                        Log.LogInfo(
                            fileName + ": minion trail follows the body " +
                            "colour.");
                    }
                    else
                    {
                        // "match" with nothing to match is the one way this
                        // key can silently do nothing, so it gets named.
                        Log.LogWarning(
                            fileName + ": minion \"trailColor\": \"match\" " +
                            "needs a \"color\" to follow, and none was set - " +
                            "the trail keeps the prefab's own colour. Set " +
                            "\"color\", or give \"trailColor\" a colour of " +
                            "its own.");
                    }
                }
                else
                {
                    Color tc = cfg.trailColor;

                    if (ReadColor(t, "minion \"trailColor\"", fileName, ref tc))
                    {
                        cfg.trailColor = tc;
                        cfg.hasTrailColor = true;

                        Log.LogInfo(
                            fileName + ": minion trail painted - the little " +
                            "streak it leaves as it moves. Stock colours are " +
                            "themed per drone (Caps orange, Electron cyan, " +
                            "Purple and Phantom violet, Suicide red).");
                    }
                }
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

                    // ---- the art ------------------------------------------
                    cfg.shieldShow = (bool?)shield["show"];
                    cfg.shieldSize =
                        Mathf.Clamp((float?)shield["size"] ?? 4f, 0.1f, 40f);

                    string shieldTint = (string)shield["color"];

                    if (!string.IsNullOrEmpty(shieldTint))
                    {
                        Color sc = Color.white;

                        if (ReadColor(shieldTint, "minion shield \"color\"",
                                      fileName, ref sc))
                        {
                            cfg.shieldColor = sc;
                            cfg.hasShieldColor = true;
                        }
                    }

                    // FOUR OF THE EIGHT RESOURCES HAVE AN INVISIBLE SHIELD,
                    // and it is deliberate rather than an oversight: the game
                    // ships shield modules for Caps, Electron, Purple and Tech
                    // only, so White, Fuel, Health and Money were never given
                    // a shieldColor and sit at rgba(0,0,0,0).
                    //
                    // Worth naming, because it fails in the most confusing way
                    // available - the shield absorbs damage perfectly and
                    // draws nothing, which reads as "the art is broken".
                    if (!cfg.hasShieldColor &&
                        res.shieldColor.a <= 0.001f)
                    {
                        Log.LogWarning(
                            fileName + ": minion \"shield\" uses " + res.name +
                            ", and that resource has no shield colour of its " +
                            "own (the game only draws shields for Caps, " +
                            "Electron, Purple and Tech). It will absorb " +
                            "damage correctly and be COMPLETELY INVISIBLE. " +
                            "Set \"color\" on the shield to paint it, or use " +
                            "one of those four resources.");
                    }

                    if (cfg.shieldShow == false && cfg.hasShieldColor)
                    {
                        Log.LogWarning(
                            fileName + ": minion \"shield\" sets a \"color\" " +
                            "but also \"show\": false, so the bubble is " +
                            "hidden and the colour can never be seen.");
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

            // FALSE by default now - see ForgeMinion.Config. The old default
            // of true was not a choice, it was the only thing that worked.
            cfg.hurtsOwner = (bool?)o["hurtsOwner"] ?? false;
            cfg.hurtsAllies = (bool?)o["hurtsAllies"] ?? false;

            // Physics, not damage - see ForgeMinionSolid. Defaults to solid,
            // which is what the game does.
            cfg.solidToOwner = (bool?)o["solidToOwner"] ?? true;

            // ---- being able to fight for the side it is on ---------------
            //
            // All three default ON because all three are fixing something
            // that was broken, not adding a behaviour. See
            // ForgeMinionAllegiance and ForgeMinionEscort.
            cfg.seeEverything = (bool?)o["seek"] ??
                                (bool?)o["seeEnemies"] ?? true;

            cfg.friendlyShots = (bool?)o["friendlyShots"] ?? true;

            bool? followAsked = (bool?)o["follow"];
            cfg.follow = followAsked ?? true;
            cfg.followSet = followAsked.HasValue;
            cfg.followSpeed = Mathf.Max(0f, (float?)o["followSpeed"] ?? 0f);
            cfg.followDelay = Mathf.Max(0f, (float?)o["followDelay"] ?? 0.6f);

            // ---- the other stance: hold ground ---------------------------
            //
            // Opt-in, and mutually exclusive with the escort. See
            // ForgeMinionHold. The `set` flag is kept because it is the only way
            // to tell "hold, and I know what follow does" from a file that
            // simply never mentioned either.
            bool? holdAsked = (bool?)o["hold"];
            cfg.hold = holdAsked ?? false;
            cfg.holdSet = holdAsked.HasValue;

            // A post narrower than the creature standing in it is a unit
            // vibrating on a point, so there is a floor. Warned rather than
            // silently corrected, because a 0 here reads as "no radius" and
            // would otherwise look like the feature ignoring the key.
            float radiusAsked = (float?)o["holdRadius"] ?? 8f;
            cfg.holdRadius = Mathf.Max(1f, radiusAsked);
            cfg.holdDelay = Mathf.Max(0f, (float?)o["holdDelay"] ?? 0.6f);
            cfg.holdSpeed = Mathf.Max(0f, (float?)o["holdSpeed"] ?? 0f);

            if (cfg.hold && radiusAsked < 1f)
            {
                Log.LogWarning(
                    fileName + ": minion \"holdRadius\": " +
                    radiusAsked.ToString("0.##") + " is smaller than the " +
                    "things standing in it, which would leave them shuffling " +
                    "on a single point. Raised to 1. A post is a patch of " +
                    "ground to mill around in, not a peg.");
            }

            HoldBlurb(cfg, fileName);

            AllegianceBlurb(cfg, fileName);

            // BEFORE the HasStats gate below, deliberately: a comfort setting
            // like this is most likely on a minion with no stat overrides at
            // all, and the blast-safety line further down is inside that gate
            // and therefore silent for exactly those files.
            if (!cfg.solidToOwner)
            {
                Log.LogInfo(
                    fileName + ": minion \"solidToOwner\": false - your own " +
                    "minions will pass through the ship instead of shoving it. " +
                    "This is a COLLISION switch, not a damage one: they still " +
                    "block enemies, still hit terrain, and you can still shoot " +
                    "them. Look for the collider-pair count once the first one " +
                    "spawns.");
            }

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
                    " immune to its explosions. This is now the DEFAULT. The " +
                    "game has no owner filter on explosions at all, so this " +
                    "is a filter of ours - and until 2026-09-02 it could not " +
                    "run on a DEATH explosion, because ExplosionComponent " +
                    "never gives its blast an owner and the filter needs one. " +
                    "That is fixed, which is what makes this switch real.");
            }

            // SAY IT LOUDLY WHEN SOMEBODY OPTS BACK IN, because this used to
            // be the only behaviour and is now the unusual one.
            if (cfg.hurtsOwner || cfg.hurtsAllies)
            {
                Log.LogWarning(
                    fileName + ": minion " +
                    (cfg.hurtsOwner ? "\"hurtsOwner\": true" : "") +
                    (cfg.hurtsOwner && cfg.hurtsAllies ? " and " : "") +
                    (cfg.hurtsAllies ? "\"hurtsAllies\": true" : "") +
                    " - its explosions CAN hurt " +
                    (cfg.hurtsOwner ? "you" : "") +
                    (cfg.hurtsOwner && cfg.hurtsAllies ? " and " : "") +
                    (cfg.hurtsAllies ? "its own swarm" : "") +
                    ". That is a real design - a swarm you have to keep clear " +
                    "of - but it is no longer the default, and it now " +
                    "genuinely happens where before it happened whatever you " +
                    "set. Remove the key if you did not mean it.");
            }
        }

        // Which STANCE won, and - the part worth a log line - what the other one
        // would have done. `follow` and `hold` are opposite answers to one
        // question, and a file that sets both is asking for something that
        // cannot happen, so the resolution has to be stated rather than left to
        // be discovered by watching a minion sit between two points.
        private static void HoldBlurb(ForgeMinion.Config cfg, string fileName)
        {
            if (!cfg.hold)
            {
                // Nothing to say for the default stance - the allegiance blurb
                // below already covers a deliberate "follow": false.
                return;
            }

            Log.LogInfo(
                fileName + ": minion \"hold\": true - these keep to a POST " +
                "rather than to you, " + cfg.holdRadius.ToString("0.##") +
                " units across, stamped " + cfg.holdDelay.ToString("0.##") +
                "s after each one spawns. That delay is deliberate: a spawner " +
                "THROWS its minions, so the post you meant is where the throw " +
                "lands, not the barrel it left. Inside the post they behave " +
                "completely normally - they wander, charge, ram and shoot; " +
                "outside it their own steering is suspended and they walk back " +
                "on their own legs, which is why a held Larva is still " +
                "dangerous instead of being a statue. They will NOT chase " +
                "something out of the post, and they will not come to you.");

            if (cfg.followSet && cfg.follow)
            {
                Log.LogWarning(
                    fileName + ": minion has BOTH \"follow\": true and " +
                    "\"hold\": true, and they are opposite stances - keep to " +
                    "me, or keep to a spot. HOLD WINS and the escort stands " +
                    "down. Running both would lean one body toward two " +
                    "different points and leave the minion sitting on the line " +
                    "between them. Remove whichever you did not mean.");
            }

            if (cfg.followSpeed > 0f || cfg.followDistance.set || cfg.leash.set)
            {
                Log.LogWarning(
                    fileName + ": minion \"hold\" is on, so \"followSpeed\", " +
                    "\"followDistance\" and \"leash\" have nothing to tune - " +
                    "all three are about keeping station on YOU. Use " +
                    "\"holdRadius\" for how far it may roam from its post and " +
                    "\"holdSpeed\" for the walk home. (holdSpeed only matters " +
                    "on a minion with no mover of its own; everything else " +
                    "walks back at its own speed, which is the point.)");
            }
        }

        // Say what was done about the three things that stopped a minion
        // fighting for the side it is on - and, more usefully, say it when one
        // has been switched OFF, because then the old behaviour is back and
        // "my minion just drifts" needs a line in the log pointing at the
        // switch that caused it.
        private static void AllegianceBlurb(
            ForgeMinion.Config cfg, string fileName)
        {
            var off = new List<string>();

            if (!cfg.seeEverything)
            {
                off.Add(
                    "\"seek\": false - it keeps the ONE layer its prefab was " +
                    "built to watch, so on any side but its original it will " +
                    "never find a target");
            }

            if (!cfg.friendlyShots)
            {
                off.Add(
                    "\"friendlyShots\": false - its own gun keeps firing on the " +
                    "enemy projectile layer, which can hit YOU and not the " +
                    "things it is shooting at");
            }

            // Skipped when `hold` is on: not following you is the whole point
            // of that stance, and HoldBlurb has already said so at length.
            // Reporting it here as well would read as a warning about the very
            // thing the file asked for.
            if (!cfg.follow && !cfg.hold)
            {
                off.Add(
                    "\"follow\": false - only the five drone prefabs have a " +
                    "follow AI state, so anything else will not come back to you");
            }

            if (off.Count == 0)
                return;

            Log.LogWarning(
                fileName + ": minion - " + string.Join("; ", off.ToArray()) +
                ". That is what you asked for; it is only worth saying because " +
                "each of these was a bug being fixed rather than a feature " +
                "being added.");
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
                cfg.slowShots = (bool?)slowJson["shots"] ?? true;

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

                cfg.tintStrength =
                    TintStrength(slowJson as JObject, fileName, "slow", 1f);

                if (cfg.amount >= 1f)
                {
                    Log.LogWarning(
                        fileName + ": \"slow\" amount is " +
                        cfg.amount.ToString("0.##") + ", which is no slow at " +
                        "all - 1 means full speed. Use 0.5 for half speed.");
                }

                if (!cfg.slowMove && !cfg.slowTurn && !cfg.slowFire &&
                    !cfg.slowKnockback && !cfg.slowShots)
                {
                    Log.LogWarning(
                        fileName + ": \"slow\" has every effect switched off, " +
                        "so it does nothing. Turn one of \"move\", " +
                        "\"turnSpeed\", \"fireRate\", \"knockback\" or " +
                        "\"shots\" back on.");
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
            if (cfg.slowShots) on.Add("the speed of the shots it fires");

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
            cfg.tintStrength = TintStrength(o, fileName, "overheat", 1f);
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

            // Overheat is a SUSTAINED-FIRE mechanic, and a release charge is
            // the one thing that removes sustained fire: `holdFire` (on by
            // default) means holding the trigger builds a shot instead of
            // firing one, so the only heat this weapon can ever gain is per
            // RELEASE. Read from the JSON rather than from the charge config, so
            // this does not depend on which Apply ran first.
            var charge = root["charge"] as JObject;

            bool chargeOn =
                charge != null && ((bool?)charge["enabled"] ?? true);

            if (chargeOn && cfg.perSecond <= 0f)
            {
                string mode = ((string)charge["mode"] ?? "release").Trim();
                bool release = !mode.Equals(
                    "auto", StringComparison.OrdinalIgnoreCase);
                bool holdFire = (bool?)charge["holdFire"] ?? true;

                if (release && holdFire)
                {
                    Log.LogWarning(
                        fileName + ": overheat + charge - this weapon cannot " +
                        "build heat by holding the trigger. \"charge\" in " +
                        "release mode with \"holdFire\" on (the default) means " +
                        "holding BUILDS a shot rather than firing one, so the " +
                        "only heat it gains is one \"perShot\" per release. " +
                        "For a gun that overheats while you lean on it, either " +
                        "set charge \"holdFire\": false (it fires normally AND " +
                        "gives you a charged shot on release - which is also " +
                        "what makes \"ventShot\" feel like a reward) or give " +
                        "overheat a \"perSecond\" so holding heats it up on its " +
                        "own.");
                }
            }
        }

        // "spinUp" - sustained fire REWARDS you. The mirror of overheat.
        private static void ApplySpinUp(
            WeaponData weapon, JObject root, string slot, string fileName)
        {
            var o = root["spinUp"] as JObject;

            if (o == null)
                return;

            var cfg = new ForgeSpinUp.Config();

            cfg.enabled = (bool?)o["enabled"] ?? true;

            if (!cfg.enabled)
                return;

            // GADGETS FIRE WITHOUT A SHOOTER, and this feature needs one twice
            // over: the meter watches `Shooter.IsShooting` to know you are
            // holding the trigger, and the headline reward is `FireRate`, which
            // only `Shooter.FireRatePassed` reads. A gadget's cadence belongs to
            // its module, so even a perfectly wound-up gadget would fire at
            // exactly its normal speed. Refused rather than half-working, the
            // same call overheat makes for the same reason.
            bool isGadget =
                !string.IsNullOrEmpty(slot) &&
                slot.Trim().ToLowerInvariant().StartsWith("gadget");

            if (isGadget)
            {
                Log.LogWarning(
                    fileName + ": \"spinUp\" only works on a PRIMARY or " +
                    "SECONDARY weapon. A gadget fires without going through " +
                    "the Shooter, so there is no held trigger to measure - and " +
                    "its firing cadence is owned by the module rather than by " +
                    "the weapon's fireRate, so the main reward could not " +
                    "arrive either. Ignored.");
                return;
            }

            cfg.spinUpTime = Mathf.Max(0f, (float?)o["spinUpTime"] ?? 1.5f);
            cfg.spinDownTime = Mathf.Max(0f, (float?)o["spinDownTime"] ?? 1f);
            cfg.grace = Mathf.Max(0f, (float?)o["grace"] ?? 0.25f);

            cfg.ramp = ForgeSpinUp.ParseRamp((string)o["ramp"]);
            cfg.steps = Mathf.Clamp((int?)o["steps"] ?? 3, 1, 20);

            // The five rewards. Fire rate and accuracy default ON - they are the
            // two the feature was asked for - and the other three are off until
            // named, so a file that only wants a faster gun says nothing about
            // damage, pellets or cost.
            cfg.rate = (bool?)o["rate"] ?? true;
            cfg.fireRateAtFull = Mathf.Max(0.05f, (float?)o["fireRate"] ?? 2f);

            cfg.accuracy = (bool?)o["accuracy"] ?? true;
            cfg.accuracyAtFull =
                Mathf.Clamp01((float?)o["accuracyAtFull"] ?? 1f);
            cfg.spreadAtFull =
                Mathf.Clamp((float?)o["spread"] ?? 1f, 0f, 4f);

            cfg.damage = (bool?)o["damage"] ?? false;
            cfg.damageAtFull = Mathf.Max(0f, (float?)o["damageAtFull"] ?? 1.5f);

            cfg.pellets = (bool?)o["pellets"] ?? false;
            cfg.pelletsAtFull =
                Mathf.Clamp((float?)o["pelletsAtFull"] ?? 2f, 0.1f, 20f);

            cfg.cheaper = (bool?)o["cheaper"] ?? false;
            cfg.costAtFull = Mathf.Clamp((float?)o["costAtFull"] ?? 0.5f, 0f, 4f);

            cfg.tintMuzzle = (bool?)o["tintMuzzle"] ?? true;
            cfg.tintStrength = TintStrength(o, fileName, "spinUp", 1f);
            cfg.showSpin = (bool?)o["showSpin"] ?? false;

            string tint = (string)o["color"];

            if (!string.IsNullOrEmpty(tint))
            {
                Color c = cfg.color;

                if (ReadColor(tint, "spinUp \"color\"", fileName, ref c))
                    cfg.color = c;
            }

            cfg.readySfx = ResolveChargeSfx(
                (string)o["readySfx"], null, false, "spinUp \"readySfx\"",
                fileName) ?? "";
            cfg.stepSfx = ResolveChargeSfx(
                (string)o["stepSfx"], null, false, "spinUp \"stepSfx\"",
                fileName) ?? "";
            cfg.lostSfx = ResolveChargeSfx(
                (string)o["lostSfx"], null, false, "spinUp \"lostSfx\"",
                fileName) ?? "";

            ForgeSpinUp.Register(weapon, cfg);

            SaySpinUp(weapon, root, cfg, fileName);
        }

        // THE ARITHMETIC, SAID OUT LOUD. Same lesson as the heat model and the
        // minion firing line: a ramp is a handful of numbers that only mean
        // anything together, and every way this feature can be configured into
        // doing nothing produces a perfectly valid weapon with a clean log.
        private static void SaySpinUp(
            WeaponData weapon, JObject root, ForgeSpinUp.Config cfg,
            string fileName)
        {
            var gains = new List<string>();

            if (cfg.rate)
            {
                gains.Add(
                    "fire rate " + weapon.fireRate.ToString("0.##") + "/s -> " +
                    (weapon.fireRate * cfg.fireRateAtFull).ToString("0.##") +
                    "/s");
            }

            if (cfg.accuracy)
            {
                if (weapon.angleVariance > 0.0001f)
                {
                    gains.Add(
                        "scatter " + weapon.angleVariance.ToString("0.##") +
                        "deg -> " +
                        (weapon.angleVariance *
                         (1f - cfg.accuracyAtFull)).ToString("0.##") + "deg");
                }

                if (Mathf.Abs(cfg.spreadAtFull - 1f) > 0.0001f)
                {
                    gains.Add(
                        "spread " + weapon.spread.ToString("0.##") + "deg -> " +
                        (weapon.spread * cfg.spreadAtFull).ToString("0.##") +
                        "deg");
                }
            }

            if (cfg.damage)
            {
                gains.Add(
                    "damage " + weapon.damage.amount.ToString("0.##") + " -> " +
                    (weapon.damage.amount * cfg.damageAtFull).ToString("0.##"));
            }

            if (cfg.pellets)
            {
                gains.Add(
                    "projectiles " +
                    weapon.projectileCount.ToString("0.##") + " -> " +
                    Mathf.Round(weapon.projectileCount *
                                cfg.pelletsAtFull).ToString("0.##"));
            }

            if (cfg.cheaper)
            {
                gains.Add(
                    "cost " + weapon.cost.ToString("0.##") + " -> " +
                    (weapon.cost * cfg.costAtFull).ToString("0.##"));
            }

            string reach = (cfg.spinUpTime <= 0.0001f)
                ? "instantly"
                : "after " + cfg.spinUpTime.ToString("0.##") +
                  "s of sustained fire";

            string fall = (cfg.spinDownTime <= 0.0001f)
                ? "and drops to nothing the moment you stop"
                : "and unwinds over " + cfg.spinDownTime.ToString("0.##") +
                  "s" +
                  (cfg.grace > 0.0001f
                      ? " after a " + cfg.grace.ToString("0.##") +
                        "s grace period"
                      : " with no grace period, so tapping loses it");

            Log.LogInfo(
                fileName + ": spinUp - winds up " +
                ForgeSpinUp.RampWord(cfg.ramp, cfg.steps) + " " + reach +
                ", " + fall + ". At full: " +
                (gains.Count > 0 ? string.Join(", ", gains.ToArray()) :
                 "NOTHING - every reward is switched off") + ".");

            // ---- the ways this can be configured into silence ----

            if (gains.Count == 0)
            {
                Log.LogWarning(
                    fileName + ": spinUp has NO REWARD switched on, so the " +
                    "meter fills and nothing happens. \"rate\" and " +
                    "\"accuracy\" are on by default - one of them has been set " +
                    "to false without another being turned on.");
            }

            // THE SILENT ONE. Two of the three weapons an author would reach
            // for when they want "inaccurate" ship angleVariance 0 - White
            // Popper and White Shotgun - so the accuracy reward has nothing to
            // remove on either, and a shotgun's inaccuracy is entirely its
            // deliberate `spread`.
            if (cfg.accuracy &&
                weapon.angleVariance <= 0.0001f &&
                Mathf.Abs(cfg.spreadAtFull - 1f) <= 0.0001f)
            {
                Log.LogWarning(
                    fileName + ": spinUp \"accuracy\" is on but this weapon " +
                    "has an \"angleVariance\" of 0 - there is no scatter to " +
                    "remove, so the accuracy half does NOTHING. Most stock " +
                    "weapons are like this (White Popper and White Shotgun " +
                    "both are). Either give the weapon an \"angleVariance\" " +
                    "for the spin-up to tighten, or set spinUp \"spread\" " +
                    "below 1 to close its fan instead - which is the only " +
                    "\"accuracy\" a shotgun has.");
            }

            if (cfg.rate && cfg.fireRateAtFull <= 1.0001f)
            {
                Log.LogWarning(
                    fileName + ": spinUp \"fireRate\" is " +
                    cfg.fireRateAtFull.ToString("0.##") + ", which is a " +
                    "MULTIPLIER - anything at or below 1 means sustained fire " +
                    "makes the gun slower, not quicker. Use 2 for double " +
                    "speed. (If a penalty is what you want, that is what " +
                    "\"overheat\" with \"response\": \"slow\" is for.)");
            }

            if (cfg.cheaper && weapon.cost <= 0.0001f)
            {
                Log.LogWarning(
                    fileName + ": spinUp \"cheaper\" is on but this weapon " +
                    "costs 0 to fire, so there is nothing to discount. Check " +
                    "the weapon's \"cost\", and note that overheat's " +
                    "\"replacesAmmo\" sets it to 0 deliberately.");
            }

            if (cfg.pellets && weapon.projectileCount <= 0f)
            {
                Log.LogWarning(
                    fileName + ": spinUp \"pellets\" is on but this weapon's " +
                    "\"projectileCount\" is 0, and the reward is a multiplier " +
                    "- zero times anything is still zero. A pure-melee weapon " +
                    "(projectileCount 0) has nothing to multiply.");
            }

            // ---- and the ways it interacts with other blocks ----

            var overheat = root["overheat"] as JObject;

            if (overheat != null && ((bool?)overheat["enabled"] ?? true))
            {
                Log.LogInfo(
                    fileName + ": spinUp + overheat on one weapon - these " +
                    "COMPOSE rather than fight. Both publish to the same " +
                    "fire-rate owner and multiply, so the gun winds up to its " +
                    "best right as the heat is about to take it away. Note the " +
                    "wind-up is LOST while it is jammed: a blocked Shooter is " +
                    "not firing, so the meter unwinds through the lockout.");
            }

            var charge = root["charge"] as JObject;

            if (charge != null && ((bool?)charge["enabled"] ?? true))
            {
                string mode = ((string)charge["mode"] ?? "release").Trim();
                bool release = !mode.Equals(
                    "auto", StringComparison.OrdinalIgnoreCase);
                bool holdFire = (bool?)charge["holdFire"] ?? true;

                if (release && holdFire)
                {
                    Log.LogInfo(
                        fileName + ": spinUp + charge - holding this weapon " +
                        "builds a shot rather than firing one (\"holdFire\" is " +
                        "on), so the spin-up winds up during the CHARGE and " +
                        "the released shot arrives carrying it. That works, and " +
                        "it is worth knowing it is what you have built: the " +
                        "reward lands on one shot rather than on a stream.");
                }
            }

            if (weapon.warmupTime > 0.0001f)
            {
                Log.LogInfo(
                    fileName + ": spinUp + a \"warmupTime\" of " +
                    weapon.warmupTime.ToString("0.##") + "s - that is the " +
                    "game's own minigun gate (it refuses to fire until wound " +
                    "up) and this is a separate, softer meter. Together they " +
                    "are a proper minigun. They differ in one way worth " +
                    "knowing: the game's gate is ZEROED the instant you " +
                    "release, while spinUp keeps its progress through " +
                    "\"grace\" and then unwinds.");
            }
        }

        // "rally" - reflect the same shot over and over, buffing it each time.
        //
        // Attaches to whichever reflector the file already has, because the
        // rally is a property of the CATCH rather than of any one feature, and
        // all three reflectors share ForgeBulletTurn.
        // ADAPTIVE DAMAGE TYPE - each hit picks the type this victim takes
        // worst. See ForgeAdaptive for the decoded table and the reach caveat.
        private static void ApplyAdaptive(
            WeaponData weapon, JObject root, string fileName)
        {
            JToken token = root["adaptive"];

            if (token == null || token.Type == JTokenType.Null)
                return;

            var cfg = new ForgeAdaptive.Config();
            var o = token as JObject;

            if (o != null)
            {
                if (!((bool?)o["enabled"] ?? true))
                    return;

                cfg.neverWorse = (bool?)o["neverWorse"] ?? true;
                cfg.tint = (bool?)o["tint"] ?? true;
                cfg.tintSeconds = Mathf.Max(0.02f, (float?)o["tintSeconds"] ?? 0.18f);
                cfg.tintStrength =
                    TintStrength(o, fileName, "adaptive", 1f);

                // EITHER FORM. A JSON array is the documented shape, but the
                // builder page's text box emits one comma-separated string -
                // and so does anyone typing it by hand. Accepting both is one
                // branch and removes a whole class of "why is my list ignored".
                JToken types = o["types"];
                var list = types as JArray;

                if (list != null)
                {
                    foreach (JToken t in list)
                    {
                        string n = ((string)t ?? "").Trim();

                        if (n.Length > 0)
                            cfg.typeNames.Add(n);
                    }
                }
                else if (types != null && types.Type == JTokenType.String)
                {
                    foreach (string n in ((string)types).Split(','))
                    {
                        string trimmed = n.Trim();

                        if (trimmed.Length > 0)
                            cfg.typeNames.Add(trimmed);
                    }
                }
            }
            else if (token.Type == JTokenType.Boolean && !(bool)token)
            {
                return;
            }
            else if (token.Type != JTokenType.Boolean)
            {
                Log.LogWarning(
                    fileName + ": \"adaptive\" should be true or an object " +
                    "with a \"types\" list. Ignored.");
                return;
            }

            if (cfg.typeNames.Count == 0)
                cfg.typeNames.AddRange(ForgeAdaptive.DefaultTypes);

            // RESOLVE ONCE, AT BUILD TIME. Looking a Resource up per hit would
            // put an asset scan on the damage path, and a name that is wrong is
            // wrong forever - better to say so now than to go quiet in play.
            var found = new System.Collections.Generic.List<Resource>();

            foreach (string n in cfg.typeNames)
            {
                var r = JsonFieldMapper.FindAsset(typeof(Resource), n) as Resource;

                if (r != null)
                {
                    if (!found.Contains(r))
                        found.Add(r);

                    continue;
                }

                Log.LogWarning(
                    fileName + ": adaptive \"types\" - '" + n + "' is not a " +
                    "resource I know, so it is dropped from the list. Names " +
                    "look like \"Resource Electron\".");
            }

            if (found.Count == 0)
            {
                Log.LogWarning(
                    fileName + ": \"adaptive\" has no usable damage types " +
                    "left, so it can never change anything. Ignored.");
                return;
            }

            if (found.Count == 1)
            {
                Log.LogWarning(
                    fileName + ": \"adaptive\" has only ONE type to choose " +
                    "from (" + found[0].name + "), so there is nothing to " +
                    "adapt between - that is just \"damage\": " +
                    "{ \"damageType\": \"" + found[0].name + "\" }.");
            }

            cfg.types = found.ToArray();

            ForgeAdaptive.Register(weapon, cfg);
            ForgeAdaptive.Describe(cfg, null, fileName);
        }

        // ADAPTIVE DAMAGE TYPE - each hit picks the type this victim takes
        // worst. See ForgeAdaptive for the decoded table and the reach caveat.
        // CHARM ON HIT - an enemy fights for you for a while, then goes back.
        //
        // See ForgeCharm for why this reuses the minion allegiance machinery
        // rather than rediscovering it: a flipped unit needs its EYES widened
        // or it cannot see anything, and its headbutt guarded or it eats your
        // own minions. Both are bugs this project has already shipped once.
        private static void ApplyCharm(
            WeaponData weapon, JObject root, string fileName)
        {
            JToken token = root["charm"];

            if (token == null || token.Type == JTokenType.Null)
                return;

            var cfg = new ForgeCharm.Config();

            if (token.Type == JTokenType.Float ||
                token.Type == JTokenType.Integer)
            {
                cfg.chance = (float)token;
            }
            else
            {
                var o = token as JObject;

                if (o == null)
                {
                    if (token.Type == JTokenType.Boolean && !(bool)token)
                        return;

                    if (token.Type != JTokenType.Boolean)
                    {
                        Log.LogWarning(
                            fileName + ": \"charm\" should be a chance like " +
                            "0.25, true, or an object. Ignored.");
                        return;
                    }
                }
                else
                {
                    if (!((bool?)o["enabled"] ?? true))
                        return;

                    cfg.chance = (float?)o["chance"] ?? cfg.chance;
                    cfg.seconds = Mathf.Max(0.1f, (float?)o["seconds"] ?? cfg.seconds);

                    // Max(0, ...), NOT Max(1, ...). ForgeCharm.Apply has always
                    // treated 0 as no limit (`if (cfg.max > 0)`), and this line
                    // clamped a 0 straight back up to 1 - so the unlimited mode
                    // existed in the engine and could not be reached from a
                    // file. R12 asked for the option that was already there.
                    cfg.max = Mathf.Max(0, (int?)o["max"] ?? cfg.max);

                    cfg.maxTargetHealth =
                        Mathf.Max(0f, (float?)o["maxTargetHealth"] ?? 0f);
                    cfg.harmless = (bool?)o["harmless"] ?? true;
                    cfg.playerCanHit = (bool?)o["playerCanHit"] ?? false;
                    cfg.infighting = (bool?)o["infighting"] ?? false;
                    cfg.colorPulse = (float?)o["colorPulse"] ?? cfg.colorPulse;
                    cfg.tintStrength =
                        TintStrength(o, fileName, "charm", cfg.tintStrength);

                    string col = (string)o["color"];

                    if (!string.IsNullOrEmpty(col))
                    {
                        cfg.hasColor = ReadColor(
                            col, "charm \"color\"", fileName, ref cfg.color);
                    }
                    else
                    {
                        cfg.hasColor = (bool?)o["tint"] ?? true;
                    }

                    string sfx = (string)o["sfx"];

                    if (!string.IsNullOrEmpty(sfx))
                    {
                        cfg.sfx = sfx;
                        cfg.sfxGuid = ForgeSfxRegistry.Resolve(
                            sfx, null, false, fileName);

                        if (string.IsNullOrEmpty(cfg.sfxGuid))
                        {
                            Log.LogWarning(
                                fileName + ": charm \"sfx\": '" + sfx +
                                "' is not a sound I know, so charming will be " +
                                "silent.");
                        }
                    }
                }
            }

            // A CHANCE ABOVE 1 IS A PERCENT SIGN, same trap as execute's.
            if (cfg.chance > 1f)
            {
                Log.LogWarning(
                    fileName + ": charm \"chance\": " +
                    cfg.chance.ToString("0.##") + " is a FRACTION, not a " +
                    "percentage - 0.25 means 25%. Reading it as " +
                    (cfg.chance / 100f).ToString("0.###") + ".");

                cfg.chance /= 100f;
            }

            if (cfg.chance <= 0f)
            {
                Log.LogWarning(
                    fileName + ": charm \"chance\" is 0, so it can never " +
                    "fire. Set one, or drop the block.");
                return;
            }

            // A CHARMED ENEMY THAT CAN STILL SHOOT YOU IS WORSE THAN NO CHARM,
            // and turning the guarantee off does exactly that - so it is said
            // plainly rather than left in the docs.
            if (!cfg.harmless)
            {
                Log.LogWarning(
                    fileName + ": charm \"harmless\": false. The safety net " +
                    "is OFF, so a charmed enemy can hurt you and your side " +
                    "again - a contact hit, an explosion, or a shot fired in " +
                    "the instant before the charm landed. Leave this on " +
                    "unless you are testing that specifically.");
            }

            if (cfg.playerCanHit)
            {
                // Not a warning: it is exactly what was asked for. But the
                // reach is wider than the sentence suggests, so it is said out
                // loud rather than left in a comment.
                Log.LogInfo(
                    fileName + ": charm \"playerCanHit\" - your own fire " +
                    "stops passing through a charmed unit. It works by making " +
                    "a charmed unit of yours answer \"not a friend\" to the " +
                    "one question the game asks before skipping a friendly " +
                    "hit, so it ALSO makes them valid targets for this mod's " +
                    "own homing, chain, curve beam, tractor, turret and " +
                    "execute - all of which ask that same question. That is " +
                    "one coherent rule rather than two contradictory ones. " +
                    "Note a LOBBED weapon could always hit them: a physics " +
                    "shot has no friendly-fire test at all.");

                ForgeCharm.AnyPlayerCanHit = true;
            }

            if (cfg.infighting && cfg.max == 1)
            {
                Log.LogWarning(
                    fileName + ": charm \"infighting\" needs more than one " +
                    "charmed unit to mean anything, and \"max\" is 1. Raise " +
                    "it, or set \"max\": 0 for no limit.");
            }

            if (cfg.infighting)
            {
                ForgeCharm.AnyInfighting = true;

                // Said out loud because "they aim at each other" and "they can
                // hurt each other" were two different features until R13, and
                // the first one shipped without the second.
                Log.LogInfo(
                    fileName + ": charm \"infighting\" - charmed units are " +
                    "written into each other's grudge lists so they PICK each " +
                    "other as targets, and they are exempted from both of the " +
                    "rules that would otherwise stop the shot landing: the " +
                    "game's friendly-fire skip (they share your faction, so a " +
                    "bullet would pass straight through) and charm's own " +
                    "\"harmless\" guard (which cancels damage to your side, " +
                    "and they ARE your side). The exemption is narrow - it " +
                    "needs both units charmed by you with infighting on - so " +
                    "they still cannot touch your ship or a minion you " +
                    "summoned yourself.");
            }

            ForgeCharm.Register(weapon, cfg);
            ForgeCharm.Describe(cfg, fileName);
        }

        // CHARM ON HIT - an enemy fights for you for a while, then goes back.
        //
        // See ForgeCharm for why this reuses the minion allegiance machinery
        // rather than rediscovering it: a flipped unit needs its EYES widened
        // or it cannot see anything, and its headbutt guarded or it eats your
        // own minions. Both are bugs this project has already shipped once.
        // WALL RIDING - a shot that follows terrain instead of ignoring it.
        //
        // See ForgeWallRide for the mechanism. PLAIN PROJECTILES ONLY: it steers
        // by publishing `idealDirection`, which is a `Projectile` field. A
        // lobbed shot is a PhysicsProjectile - a different class with no such
        // field - and rewriting its rigidbody velocity every frame would cancel
        // the fall and turn a grenade into a missile, which is the same reason
        // converge solves a LAUNCH for lobbed shots rather than steering them.
        private static void ApplyWallRide(
            WeaponData weapon, JObject root, string fileName)
        {
            JToken tok = root["wallRide"];

            if (tok == null || tok.Type == JTokenType.Null)
                return;

            var cfg = new ForgeWallRide();   // defaults, never attached
            var o = tok as JObject;

            float grab = cfg.grabRange, stand = cfg.standoff;
            float turn = cfg.turnRate, hug = cfg.hug;
            float speed = cfg.speedMultiplier, seconds = cfg.rideSeconds;
            bool leave = cfg.leaveOnLostWall;
            int sideValue = 0;

            if (o != null)
            {
                if (!((bool?)o["enabled"] ?? true))
                    return;

                grab = Mathf.Max(0.6f, (float?)o["grabRange"] ?? grab);
                stand = Mathf.Max(0.05f, (float?)o["standoff"] ?? stand);
                turn = Mathf.Max(1f, (float?)o["turnRate"] ?? turn);
                hug = Mathf.Max(0f, (float?)o["hug"] ?? hug);
                speed = Mathf.Max(0.05f, (float?)o["speed"] ?? speed);
                seconds = Mathf.Max(0f, (float?)o["seconds"] ?? seconds);
                leave = (bool?)o["leaveOnLostWall"] ?? leave;

                string side = ((string)o["side"] ?? "").Trim().ToLowerInvariant();

                switch (side)
                {
                    case "":
                    case "auto":  sideValue = 0; break;
                    case "left":  sideValue = -1; break;
                    case "right": sideValue = 1; break;
                    default:
                        Log.LogWarning(
                            fileName + ": wallRide \"side\": '" + side +
                            "' is not auto, left or right - using auto, which " +
                            "keeps whichever way the shot was already going.");
                        break;
                }
            }
            else if (tok.Type != JTokenType.Boolean || !(bool)tok)
            {
                // "wallRide": false, or something that is not an object.
                if (tok.Type == JTokenType.Boolean)
                    return;

                Log.LogWarning(
                    fileName + ": \"wallRide\" should be true or an object " +
                    "like { \"standoff\": 0.7 }. Ignored.");
                return;
            }

            // A STANDOFF SMALLER THAN THE SHOT IS THE ONE SETTING THAT BREAKS
            // IT, and it breaks it in a way that looks like the feature simply
            // not working: the shot rides so close that its own swept cast
            // reaches the wall and the game destroys it on contact. Named here
            // rather than left to be discovered.
            if (stand < 0.25f)
            {
                Log.LogWarning(
                    fileName + ": wallRide \"standoff\": " +
                    stand.ToString("0.##") + " is very small. The shot holds " +
                    "that gap from the wall, and it dies on contact like any " +
                    "other shot - below about 0.25 it will clip the surface " +
                    "and vanish. 0.5 to 0.9 rides visibly close without " +
                    "touching.");
            }

            if (grab < stand)
            {
                Log.LogWarning(
                    fileName + ": wallRide \"grabRange\" (" +
                    grab.ToString("0.##") + ") is smaller than \"standoff\" (" +
                    stand.ToString("0.##") + "), so the shot can never notice a " +
                    "wall it is already too far from to hold. Raising " +
                    "grabRange to " + (stand + 0.9f).ToString("0.##") + ".");

                grab = stand + 0.9f;
            }

            var pw = weapon as ProjectileWeaponData;

            if (!ProjectileTargetOk(pw, "\"wallRide\"", fileName, false, null))
                return;

            if (pw.usePhysics)
            {
                Log.LogWarning(
                    fileName + ": \"wallRide\" does not work on a LOBBED " +
                    "(usePhysics) weapon. It steers by publishing the base " +
                    "heading a plain Projectile flies along, and a lobbed shot " +
                    "is a different class with no such field - holding one to " +
                    "a wall would mean rewriting its velocity every frame, " +
                    "which cancels the fall and stops it being a lobbed shot " +
                    "at all. Ignored.");
                return;
            }

            GameObject source = (pw.projectilePrefab != null)
                ? pw.projectilePrefab.gameObject : null;

            if (source == null)
                return;

            GameObject clone = VisualCustomizer.ClonePrefab(source);

            if (clone == null)
                return;

            Projectile comp = clone.GetComponentInChildren<Projectile>(true);

            if (comp == null)
            {
                Log.LogWarning(
                    fileName + ": \"wallRide\" - no Projectile component on " +
                    "the prefab. Ignored.");
                UnityEngine.Object.Destroy(clone);
                return;
            }

            var ride = comp.gameObject.GetComponent<ForgeWallRide>();

            if (ride == null)
                ride = comp.gameObject.AddComponent<ForgeWallRide>();

            ride.grabRange = grab;
            ride.standoff = stand;
            ride.turnRate = turn;
            ride.hug = hug;
            ride.speedMultiplier = speed;
            ride.rideSeconds = seconds;
            ride.side = sideValue;
            ride.leaveOnLostWall = leave;

            pw.projectilePrefab = comp;

            Log.LogInfo(
                fileName + ": wallRide - shots follow terrain, holding " +
                stand.ToString("0.##") + " units off the surface and catching " +
                "a wall within " + grab.ToString("0.##") + ". Turning " +
                turn.ToString("0.#") + " deg/s, " +
                (sideValue == 0
                    ? "going whichever way they were already headed"
                    : (sideValue < 0 ? "always hugging left" : "always hugging right")) +
                (seconds > 0f
                    ? ", for " + seconds.ToString("0.##") + "s"
                    : ", for as long as there is wall") +
                ". They never touch the surface, so terrain damage and digging " +
                "are unaffected.");
        }

        // EXECUTE - finish anything already below a health threshold.
        //
        // See ForgeExecute for the design. The short version: it hooks the one
        // method every damage route funnels into, and calls the game's own
        // Die() so the game's own kill-credit line pays out normally.
        private static void ApplyExecute(
            WeaponData weapon, JObject root, string fileName)
        {
            JToken token = root["execute"];

            if (token == null)
                return;

            var cfg = new ForgeExecute.Config();

            // "execute": 0.2 is the short spelling for "under 20%".
            if (token.Type == JTokenType.Float ||
                token.Type == JTokenType.Integer)
            {
                cfg.percent = (float)token;
                cfg.flat = 0f;
            }
            else
            {
                var o = token as JObject;

                if (o == null)
                {
                    Log.LogWarning(
                        fileName + ": \"execute\" should be a fraction like " +
                        "0.2, or an object with \"percent\" and/or \"flat\". " +
                        "Ignored.");
                    return;
                }

                cfg.percent = (float?)o["percent"] ?? 0.15f;
                cfg.flat = (float?)o["flat"] ?? 0f;
                cfg.maxTargetHealth = (float?)o["maxTargetHealth"] ?? 0f;

                cfg.colorSeconds = (float?)o["colorSeconds"] ?? 0.25f;
                cfg.colorPulse = (float?)o["colorPulse"] ?? 0f;
                cfg.tintStrength = TintStrength(o, fileName, "execute", 1f);

                cfg.hasColor = ReadColor(
                    (string)o["color"], "execute \"color\"", fileName,
                    ref cfg.color);

                string sfx = (string)o["sfx"];

                if (!string.IsNullOrEmpty(sfx))
                {
                    cfg.sfx = sfx;
                    cfg.sfxGuid = ForgeSfxRegistry.Resolve(
                        sfx, null, false, fileName);

                    if (string.IsNullOrEmpty(cfg.sfxGuid))
                    {
                        Log.LogWarning(
                            fileName + ": execute \"sfx\": '" + sfx + "' is not " +
                            "a sound I know, so the execute will be silent. " +
                            "Custom sounds go in the mod's sounds folder; the " +
                            "startup log lists what loaded.");
                    }
                }
            }

            // A PERCENTAGE ABOVE 1 IS ALMOST CERTAINLY A TYPO FOR A PERCENT
            // SIGN, and left alone it executes everything the weapon touches -
            // "percent": 20 means "under 2000% health", which every living
            // thing satisfies. Caught here rather than in play.
            if (cfg.percent > 1f)
            {
                Log.LogWarning(
                    fileName + ": execute \"percent\": " +
                    cfg.percent.ToString("0.##") + " is a FRACTION, not a " +
                    "percentage - 0.2 means 20%. A value above 1 would finish " +
                    "everything you hit, so it has been read as " +
                    (cfg.percent / 100f).ToString("0.###") + " instead. Write " +
                    "it as a fraction to silence this.");

                cfg.percent /= 100f;
            }

            if (cfg.percent <= 0f && cfg.flat <= 0f)
            {
                Log.LogWarning(
                    fileName + ": \"execute\" has neither a \"percent\" nor a " +
                    "\"flat\" threshold above zero, so it can never fire. Set " +
                    "one, or drop the block.");
                return;
            }

            ForgeExecute.Register(weapon, cfg);
            ForgeExecute.Describe(cfg, fileName);
        }

        // STUN - it cannot move and it cannot shoot.
        //
        // See ForgeStun, including why this is not `slow` with amount 0 (slow
        // cannot reach zero by construction, and its three levers are all the
        // wrong shape for a freeze).
        private static void ApplyStun(
            WeaponData weapon, JObject root, string fileName)
        {
            JToken token = root["stun"];

            if (token == null)
                return;

            var cfg = new ForgeStun.Config();

            // "stun": 1.5 is the short spelling for a 1.5 second freeze.
            if (token.Type == JTokenType.Float ||
                token.Type == JTokenType.Integer)
            {
                cfg.duration = (float)token;
            }
            else if (token.Type == JTokenType.Boolean)
            {
                if (!(bool)token)
                    return;
            }
            else
            {
                var o = token as JObject;

                if (o == null)
                {
                    Log.LogWarning(
                        fileName + ": \"stun\" should be a duration in seconds " +
                        "like 1.5, or an object with \"duration\", \"chance\" " +
                        "and \"cooldown\". Ignored.");
                    return;
                }

                if ((bool?)o["enabled"] == false)
                {
                    Log.LogInfo(
                        fileName + ": \"stun\" is present but " +
                        "\"enabled\": false, so nothing is frozen. The " +
                        "settings are kept for whenever you turn it back on.");
                    return;
                }

                cfg.duration = (float?)o["duration"] ?? 1.2f;
                cfg.chance = (float?)o["chance"] ?? 1f;
                cfg.cooldown = Mathf.Max(0f, (float?)o["cooldown"] ?? 0f);
                cfg.refresh = (bool?)o["refresh"] ?? true;
                cfg.stopGun = (bool?)o["stopGun"] ?? true;
                cfg.stopMovement = (bool?)o["stopMovement"] ?? true;
                cfg.stopMotion = (bool?)o["stopMotion"] ?? true;
                cfg.maxTargetHealth =
                    Mathf.Max(0f, (float?)o["maxTargetHealth"] ?? 0f);
                cfg.tintPulse = (float?)o["tintPulse"] ?? 0f;
                cfg.tintStrength = TintStrength(o, fileName, "stun", 1f);

                // A tint is ON by default here, unlike slow's. A stun is a
                // total shutdown with no other tell - no slowed animation, no
                // reduced rate of fire to notice - so without a colour the
                // player cannot see that it worked, and "did that do anything?"
                // is the report I would otherwise get.
                JToken tintToken = o["tint"];

                if (tintToken != null && tintToken.Type == JTokenType.Boolean)
                {
                    cfg.hasTint = (bool)tintToken;
                }
                else if (tintToken != null)
                {
                    Color c = cfg.tint;

                    if (ReadColor((string)tintToken, "stun \"tint\"",
                                  fileName, ref c))
                    {
                        cfg.tint = c;
                        cfg.hasTint = true;
                    }
                }

                string sfx = (string)o["sfx"];

                if (!string.IsNullOrEmpty(sfx))
                {
                    cfg.sfx = sfx;
                    cfg.sfxGuid = ForgeSfxRegistry.Resolve(
                        sfx, null, false, fileName);

                    if (string.IsNullOrEmpty(cfg.sfxGuid))
                    {
                        Log.LogWarning(
                            fileName + ": stun \"sfx\": '" + sfx + "' is not a " +
                            "sound I know, so the stun will be silent. Game " +
                            "sounds can be named directly - see SOUNDS.txt - " +
                            "and custom ones go in the mod's sounds folder.");
                    }
                }
            }

            if (cfg.duration <= 0f)
            {
                Log.LogWarning(
                    fileName + ": stun \"duration\" is " +
                    cfg.duration.ToString("0.##") + ", so nothing would be " +
                    "frozen for any length of time and the block is ignored. " +
                    "Set it to the seconds a hit should freeze something.");
                return;
            }

            float chanceAsked = cfg.chance;
            cfg.chance = Mathf.Clamp01(cfg.chance);

            // A CHANCE ABOVE 1 IS ALMOST CERTAINLY A PERCENT SIGN, the same
            // typo `execute`'s percent guard exists for - except here it fails
            // SAFE (always stuns) rather than loud, so it needs saying.
            if (chanceAsked > 1f)
            {
                Log.LogWarning(
                    fileName + ": stun \"chance\": " +
                    chanceAsked.ToString("0.##") + " is a FRACTION, not a " +
                    "percentage - 0.25 means a quarter of hits. Anything above " +
                    "1 simply means every hit, which is what this will now do.");
            }

            if (!cfg.stopGun && !cfg.stopMovement && !cfg.stopMotion)
            {
                Log.LogWarning(
                    fileName + ": stun has \"stopGun\", \"stopMovement\" and " +
                    "\"stopMotion\" all false, so it would freeze nothing at " +
                    "all. The block is ignored - turn one of them back on, or " +
                    "drop it.");
                return;
            }

            ForgeStun.Register(weapon, cfg);
            ForgeStun.Describe(cfg, fileName, weapon.fireRate);

            if (!cfg.hasTint)
            {
                Log.LogInfo(
                    fileName + ": stun \"tint\": false - no colour cue. Worth " +
                    "knowing that a stun has no other tell: nothing about a " +
                    "frozen unit looks different from one that happens to be " +
                    "standing still, so the colour is usually the only way to " +
                    "see that it landed.");
            }
        }

        // GUIDED PROJECTILES - the shot follows your crosshair while it flies.
        //
        // See ForgeGuided. The short version: it reads the crosshair EVERY
        // frame, where homing picks its own target and converge reads the
        // cursor once at launch. It is the only one of the three that is an
        // input rather than an automation.
        private static void ApplyGuided(
            WeaponData weapon, JObject root, string fileName)
        {
            JToken token = root["guided"];

            if (token == null)
                return;

            var pw = weapon as ProjectileWeaponData;

            // supportsPhysics: TRUE as of R13.
            //
            // This used to pass `false` with the hint "a lobbed shot cannot be
            // steered at all: rewriting its velocity cancels the fall". Every
            // clause of that is true about the technique it assumed and the
            // game does not use that technique - see ForgeGuidedPhysics. The
            // rocket has `gravityScale 0` and the engine already torque-steers
            // it toward a point, so `usePhysics` was the wrong thing to key on.
            if (!ProjectileTargetOk(pw, "guided", fileName, true, null))
                return;

            float turnRate = 240f;
            float delay = 0f;
            float duration = 0f;
            float arrive = 1.5f;
            bool whileHeld = false;
            bool faceTravel = true;

            // "guided": true is the short spelling, and "guided": 300 sets the
            // turn rate directly - both match how the other blocks read.
            if (token.Type == JTokenType.Boolean)
            {
                if (!(bool)token)
                    return;
            }
            else if (token.Type == JTokenType.Float ||
                     token.Type == JTokenType.Integer)
            {
                turnRate = (float)token;
            }
            else
            {
                var o = token as JObject;

                if (o == null)
                {
                    Log.LogWarning(
                        fileName + ": \"guided\" should be true, a turn rate " +
                        "like 240, or an object with \"turnRate\", \"delay\", " +
                        "\"duration\" and \"while\". Ignored.");
                    return;
                }

                if ((bool?)o["enabled"] == false)
                {
                    Log.LogInfo(
                        fileName + ": \"guided\" is present but " +
                        "\"enabled\": false, so shots fly straight. The " +
                        "settings are kept for whenever you turn it back on.");
                    return;
                }

                turnRate = (float?)o["turnRate"] ?? 240f;
                delay = Mathf.Max(0f, (float?)o["delay"] ?? 0f);
                duration = Mathf.Max(0f, (float?)o["duration"] ?? 0f);
                arrive = Mathf.Max(0f, (float?)o["arrive"] ?? 1.5f);
                faceTravel = (bool?)o["faceTravel"] ?? true;

                string when = (string)o["while"];

                if (!string.IsNullOrEmpty(when))
                {
                    string w = when.Trim().ToLowerInvariant();

                    if (w == "held" || w == "hold" || w == "trigger")
                    {
                        whileHeld = true;
                    }
                    else if (w != "always" && w != "life" && w != "ever")
                    {
                        Log.LogWarning(
                            fileName + ": guided \"while\": '" + when +
                            "' is not recognised - use \"always\" (the " +
                            "default: it steers for its whole flight) or " +
                            "\"held\" (it steers only while you hold the " +
                            "trigger, and commits the moment you let go). " +
                            "Read as \"always\".");
                    }
                }
            }

            if (turnRate <= 0f)
            {
                Log.LogWarning(
                    fileName + ": guided \"turnRate\" is " +
                    turnRate.ToString("0.##") + ", so the shot could never " +
                    "turn and the block is ignored. Set it to the degrees per " +
                    "second it may steer - 240 keeps up with the mouse, 90 is " +
                    "a heavy missile.");
                return;
            }

            // ---- the ROCKET path ------------------------------------------
            if (pw.usePhysics)
            {
                ApplyGuidedPhysics(
                    pw, token as JObject, fileName,
                    turnRate, delay, duration, arrive, whileHeld);
                return;
            }

            // THE FOUR ROCKET-ONLY KEYS MUST NOT GO QUIET ON A BULLET.
            //
            // `cruise`, `thrust`, `speedCap` and `wobble` are read only inside
            // ApplyGuidedPhysics, because they configure the engine's own
            // `homingData` steering loop and a plain `Projectile` has no such
            // thing. Left unmentioned that is a silent default on a tuning
            // path - the failure shape this project pays for most often - and
            // it is a very easy mistake to make, because the same block accepts
            // them one template away.
            JObject keys = token as JObject;

            if (keys != null)
            {
                var stray = new List<string>();

                foreach (string k in new[]
                         { "cruise", "thrust", "speedCap", "wobble" })
                {
                    if (keys[k] != null)
                        stray.Add(k);
                }

                if (stray.Count > 0)
                {
                    Log.LogWarning(
                        fileName + ": guided \"" +
                        string.Join("\", \"", stray.ToArray()) +
                        "\" " + ((stray.Count == 1) ? "is" : "are") +
                        " IGNORED on this weapon, because it fires an " +
                        "ordinary bullet rather than a rigidbody. Those keys " +
                        "configure the engine's own thrust-and-torque loop, " +
                        "which only a \"usePhysics\" shot (a rocket or a " +
                        "grenade) has. On a bullet the speed is " +
                        "\"projectileSpeed\" and the turn is \"turnRate\" - " +
                        "both of which work here.");
                }
            }

            GameObject clone = VisualCustomizer.ClonePrefab(
                pw.projectilePrefab.gameObject);

            var comp = clone.GetComponentInChildren<Projectile>(true);

            if (comp == null)
            {
                Log.LogWarning(
                    fileName + ": guided - no Projectile on the prefab, so " +
                    "there is nothing to steer.");
                return;
            }

            // The SAME GameObject as the Projectile, because the patch finds
            // this with GetComponent, not GetComponentInChildren - and on
            // several stock prefabs the Projectile sits on a CHILD rather than
            // the root, so adding to the clone root would be invisible to it.
            var g = comp.GetComponent<ForgeGuided>();

            if (g == null)
                g = comp.gameObject.AddComponent<ForgeGuided>();

            g.turnRate = turnRate;
            g.delay = delay;
            g.duration = duration;
            g.arrive = arrive;
            g.whileHeld = whileHeld;
            g.faceTravel = faceTravel;

            pw.projectilePrefab = comp;
            ForgeGuided.Any = true;

            // THE TURN RADIUS IS WHAT PEOPLE ACTUALLY SEE, and it grows with
            // speed - so the same turn rate that whips a slow shot round barely
            // bends a fast one. Do that sum for them, the same way `homing`
            // does, because "my guided shot ignores the mouse" is otherwise a
            // mystery with a perfectly correct log line above it.
            float speed = pw.projectileSpeed;
            string radiusNote = string.Empty;

            if (speed > 0f)
            {
                float radius = speed / (turnRate * Mathf.Deg2Rad);

                radiusNote =
                    " At projectileSpeed " + speed.ToString("0.#") +
                    " its tightest turn circle is about " +
                    radius.ToString("0.0") + " units in radius." +
                    (radius > 10f
                        ? " THAT IS WIDE - it will feel sluggish and miss what " +
                          "you point at. Raise turnRate or lower " +
                          "projectileSpeed."
                        : string.Empty);
            }

            Log.LogInfo(
                fileName + ": guided - shots follow your CROSSHAIR at " +
                turnRate.ToString("0.#") + " deg/s" +
                (delay > 0f
                    ? ", starting " + delay.ToString("0.##") + "s after launch"
                    : "") +
                (duration > 0f
                    ? ", for " + duration.ToString("0.##") + "s"
                    : ", for its whole flight") +
                "." + radiusNote);

            // SLOWDOWN QUIETLY TAKES THE GUIDANCE AWAY, and the most-cloned
            // stock template ships it ON. Steering re-normalises to the shot's
            // CURRENT speed, so it composes with a decelerating shot right up
            // until the speed reaches zero - at which point there is no heading
            // to rotate and guidance stops for good. The shot then hangs where
            // it stopped, ignoring the mouse. Found by simulating rather than
            // by playing, and it is exactly the shape of trap `slowDown` has
            // sprung on this project before.
            if (pw.rangeData.enabled && pw.rangeData.slowDown)
            {
                Log.LogWarning(
                    fileName + ": guided + \"rangeData\": " +
                    "{ \"slowDown\": true } - slowDown decelerates the shot to " +
                    "a DEAD STOP at its range, and a shot with no speed has no " +
                    "heading to steer, so guidance fades out as it slows and " +
                    "stops entirely at the end. The stock White Popper ships " +
                    "slowDown ON, so this is easy to inherit without asking " +
                    "for it. Set \"rangeData\": { \"slowDown\": false } for a " +
                    "guided shot that answers the mouse for its whole flight.");
            }

            Log.LogInfo(
                fileName + ": guided - " +
                (whileHeld
                    ? "\"while\": \"held\" - it steers only while you hold the " +
                      "trigger and COMMITS the moment you let go. Note a " +
                      "GADGET has no trigger, so a gadget-fired shot flies " +
                      "straight in this mode."
                    : "it steers for its whole flight; set " +
                      "\"while\": \"held\" if you would rather let go and " +
                      "have it commit.") +
                " It stops steering within " + arrive.ToString("0.##") +
                " units of the cursor, so it cannot end up orbiting it. This " +
                "reads the UNASSISTED crosshair, not the aim-assisted firing " +
                "direction, so it goes where you point.");
        }

        // GUIDED ON A PHYSICS SHOT - a rocket you fly, or a steerable grenade.
        //
        // Split out rather than folded into ApplyGuided because almost nothing
        // is shared: this writes the weapon's own `homingData` and lets the
        // ENGINE steer, where the plain path installs a steering loop of ours.
        // See ForgeGuidedPhysics for why that is possible at all.
        private static void ApplyGuidedPhysics(
            ProjectileWeaponData pw, JObject o, string fileName,
            float turnRate, float delay, float duration, float arrive,
            bool whileHeld)
        {
            // ---- extra knobs, all of which exist because the ROCKET's own
            // homingData overrides things the author already set elsewhere ----
            //
            // `wobble` defaults to 0 for a guided shot and that is a real
            // decision, not tidiness: `Weapon Rocket Fire` ships
            // `turbulenceTorque 2.5` against `torque 2`, so the WOBBLE IS
            // STRONGER THAN THE STEERING and a missile you are trying to fly by
            // hand would fight you. There is no existing behaviour to preserve
            // here - guidance on a physics shot has never worked - so the
            // default can be the one that makes the feature work.
            float wobble = 0f;
            float speedCap = -1f;
            float thrust = -1f;
            float cruise = -1f;

            if (o != null)
            {
                wobble = Mathf.Max(0f, (float?)o["wobble"] ?? 0f);
                speedCap = (float?)o["speedCap"] ?? -1f;
                thrust = (float?)o["thrust"] ?? -1f;
                cruise = (float?)o["cruise"] ?? -1f;
            }

            PhysicsProjectile prefab = pw.physicsProjectilePrefab;

            if (prefab == null)
            {
                Log.LogWarning(
                    fileName + ": guided - this weapon is usePhysics but has " +
                    "no physicsProjectilePrefab, so there is nothing to steer.");
                return;
            }

            GameObject clone = VisualCustomizer.ClonePrefab(prefab.gameObject);

            var comp = clone.GetComponentInChildren<PhysicsProjectile>(true);

            if (comp == null)
            {
                Log.LogWarning(
                    fileName + ": guided - no PhysicsProjectile on the prefab, " +
                    "so there is nothing to steer.");
                return;
            }

            // Same GameObject as the PhysicsProjectile: the patch finds this
            // with GetComponent, and on some prefabs the shot component is on a
            // child rather than the clone root.
            var g = comp.GetComponent<ForgeGuidedPhysics>();

            if (g == null)
                g = comp.gameObject.AddComponent<ForgeGuidedPhysics>();

            g.turnRate = turnRate;
            g.delay = delay;
            g.duration = duration;
            g.arrive = arrive;
            g.whileHeld = whileHeld;

            pw.physicsProjectilePrefab = comp;
            ForgeGuidedPhysics.Any = true;

            // ---- the weapon's own homingData, which is what actually steers --
            //
            // `maxAngularVelocity` IS a degrees-per-second cap, because Unity 2D
            // measures angularVelocity in degrees - so turnRate maps onto it
            // exactly with no calibration. The stock rocket ships 1000, which
            // is "no limit".
            ProjectileHomingData h = pw.homingData;

            float wasCap = h.maxAngularVelocity;
            float wasWobble = h.turbulenceTorque;

            h.maxAngularVelocity = turnRate;
            h.turbulenceTorque = wasWobble * wobble;

            if (speedCap >= 0f)
                h.maxSpeed = speedCap;

            // ---- "cruise" IS THE FIELD ANYONE ACTUALLY WANTS -------------
            //
            // A SIMULATION CAUGHT THIS AND IT WOULD HAVE SHIPPED WRONG.
            //
            // `acceleration` is a FORCE, and a constant force does not produce
            // a speed - it settles at `force / (mass * linearDamping)`. The
            // rocket is mass 4 with damping 3, so its stock `acceleration: 70`
            // cruises at 70 / 12 = **5.8 units/second**, and its
            // `maxSpeed: 100` is a ceiling nothing can ever reach.
            //
            // I had already written a build warning saying the shot "thrusts up
            // to 100 units/second", which is what `maxSpeed` looks like it
            // means and is off by a factor of seventeen. And the WireGuided
            // preset was about to ship `thrust: 18`, which is a cruise of
            // **1.5 u/s** - a rocket that visibly stops in mid-air.
            //
            // This is the exact rule already written down in this repo for the
            // gravity well ("a constant force settles at force/(mass*damping),
            // so compensate rather than shipping a raw number") applied to a
            // different subsystem, and I made the same mistake the well made.
            // So `cruise` takes the speed and does the compensation, and
            // `thrust` stays for anyone who wants the raw force.
            var body = comp.GetComponent<Rigidbody2D>();
            float mass = (body != null) ? Mathf.Max(0.0001f, body.mass) : 1f;
            float damping = (body != null) ? body.linearDamping : 0f;

            if (cruise >= 0f)
            {
                if (damping <= 0.0001f)
                {
                    Log.LogWarning(
                        fileName + ": guided \"cruise\" needs the shot to have " +
                        "linear damping to settle at a speed, and this one's " +
                        "is " + damping.ToString("0.##") + " - with no damping " +
                        "a constant thrust accelerates without limit and only " +
                        "\"speedCap\" bounds it. Use \"speedCap\" and " +
                        "\"thrust\" instead. Ignored.");
                }
                else
                {
                    h.acceleration = cruise * mass * damping;

                    // The cap has to be above the cruise or it becomes the
                    // real limit and `cruise` silently means nothing.
                    if (h.maxSpeed > 0f && h.maxSpeed < cruise)
                        h.maxSpeed = cruise * 1.1f;

                    Log.LogInfo(
                        fileName + ": guided \"cruise\": " +
                        cruise.ToString("0.##") + " units/second -> " +
                        "acceleration " + h.acceleration.ToString("0.#") +
                        " (mass " + mass.ToString("0.##") + " x damping " +
                        damping.ToString("0.##") + "). A thrust is a FORCE, " +
                        "not a speed: it settles at force/(mass x damping), so " +
                        "the raw number that gives 13 units/second on this " +
                        "shot gives a different speed on any other. This is " +
                        "the field to use.");
                }
            }
            else if (thrust >= 0f)
            {
                h.acceleration = thrust;
            }

            pw.homingData = h;

            Log.LogInfo(
                fileName + ": guided on a PHYSICS shot (a rocket or a lobbed " +
                "one) - you fly it with the CROSSHAIR at up to " +
                turnRate.ToString("0.#") + " deg/s" +
                (delay > 0f
                    ? ", starting " + delay.ToString("0.##") + "s after launch"
                    : "") +
                (duration > 0f
                    ? ", for " + duration.ToString("0.##") + "s"
                    : ", for its whole flight") +
                ". This steers a DIFFERENT way from a bullet and it is worth " +
                "knowing which: nothing rewrites its velocity. The engine's " +
                "own PhysicsProjectile.Homing() torques the nose toward a " +
                "target point and thrusts along it, and all this feature does " +
                "is put your cursor in that point every frame. So gravity, " +
                "damping and its own thrust all keep working.");

            if (whileHeld)
            {
                Log.LogInfo(
                    fileName + ": guided - \"while\": \"held\", so letting go " +
                    "aims it 10,000 units along its own nose and stops - it " +
                    "commits to wherever it was pointing rather than freezing " +
                    "or curling back. (A GADGET has no trigger to hold, so a " +
                    "gadget-fired shot commits immediately in this mode.)");
            }

            // WHAT SPEED THE SHOT ACTUALLY ENDS UP AT, which is neither
            // `projectileSpeed` nor `maxSpeed`.
            //
            // `projectileSpeed` is only the launch. `Homing()` then thrusts
            // along the nose forever, and damping fights it, so the shot
            // converges on `acceleration / (mass * damping)` from EITHER
            // direction - a fast launch decelerates to it and a slow one
            // accelerates to it. `maxSpeed` is a ceiling that on the stock
            // rocket (5.8 against 100) is never reached at all.
            //
            // So a guided rocket's real speed is a number that appears nowhere
            // in the weapon file, and it is the one that decides whether the
            // thing is flyable. Print it.
            float terminal = (damping > 0.0001f)
                ? h.acceleration / (mass * damping)
                : 0f;

            if (terminal > 0f)
            {
                float capped = (h.maxSpeed > 0f)
                    ? Mathf.Min(terminal, h.maxSpeed) : terminal;

                Log.LogInfo(
                    fileName + ": guided - this shot SETTLES AT " +
                    capped.ToString("0.#") + " units/second in flight" +
                    ((h.maxSpeed > 0f && h.maxSpeed < terminal)
                        ? " (its own maxSpeed cap of " +
                          h.maxSpeed.ToString("0.#") + " binds first)"
                        : " (thrust " + h.acceleration.ToString("0.#") +
                          " / mass " + mass.ToString("0.##") +
                          " / damping " + damping.ToString("0.##") + ")") +
                    ", whatever \"projectileSpeed\" (" +
                    pw.projectileSpeed.ToString("0.##") + ") says - that " +
                    "field is only the launch. A thrusting shot converges on " +
                    "this speed from either direction, so a fast launch slows " +
                    "down to it. Use guided \"cruise\" to choose it directly.");

                if (capped > 25f)
                {
                    Log.LogWarning(
                        fileName + ": guided - " + capped.ToString("0.#") +
                        " units/second is FAST for something you fly by hand. " +
                        "Set guided \"cruise\": 12 (or so) for a missile you " +
                        "can actually steer; the crosshair cannot lead a shot " +
                        "that crosses the room in under a second.");
                }
                else if (capped < 3f)
                {
                    Log.LogWarning(
                        fileName + ": guided - " + capped.ToString("0.#") +
                        " units/second means the shot visibly STOPS in mid-air " +
                        "after its launch speed bleeds off, because a thrust " +
                        "of " + h.acceleration.ToString("0.#") + " against " +
                        "mass " + mass.ToString("0.##") + " and damping " +
                        damping.ToString("0.##") + " settles there. Raise " +
                        "guided \"cruise\" (which does this sum for you) or " +
                        "\"thrust\".");
                }
            }

            if (wobble <= 0f && wasWobble > 0f)
            {
                Log.LogInfo(
                    fileName + ": guided - the shot's turbulence wobble " +
                    "(turbulenceTorque " + wasWobble.ToString("0.##") + ") is " +
                    "switched OFF, because it is a random torque and it was " +
                    "stronger than the steering torque - it would fight your " +
                    "cursor. Set guided \"wobble\": 1 to keep the stock " +
                    "wobble, or 0.3 for a little of it.");
            }

            if (wasCap > 0f && turnRate > wasCap)
            {
                Log.LogInfo(
                    fileName + ": guided - \"turnRate\" " +
                    turnRate.ToString("0.#") + " is above this shot's own " +
                    "maxAngularVelocity of " + wasCap.ToString("0.#") +
                    ", which has been raised to match. Note the cap is only " +
                    "the ceiling: how fast it gets there is homingData " +
                    "\"torque\" (" + pw.homingData.torque.ToString("0.##") +
                    " here), and that is raised for you at launch if it is too " +
                    "weak to reach the rate you asked for.");
            }

            // A LOBBED shot with gravity can be guided too, and the honest
            // description is different from a rocket's - so say which one this
            // is rather than leaving the author to work it out from a number.
            float gravity = (body != null) ? body.gravityScale : 0f;

            if (gravity > 0.01f)
            {
                Log.LogInfo(
                    fileName + ": guided - this shot HAS GRAVITY " +
                    "(gravityScale " + gravity.ToString("0.##") + "), so it is " +
                    "a grenade rather than a rocket. Guidance aims its nose " +
                    "and its thrust; gravity still pulls it down the whole " +
                    "time. Expect to fly it in an arc and to have to lead " +
                    "upward - which is a real weapon, just not the same one as " +
                    "a rocket. \"converge\" is still the answer if you want a " +
                    "lobbed shot to LAND on a point without steering it.");
            }
            else
            {
                Log.LogInfo(
                    fileName + ": guided - gravityScale is 0 on this shot, so " +
                    "it is a rocket: it flies where it is pointed and there is " +
                    "no arc to fight. (This is the case the old build warning " +
                    "refused outright, on the grounds that steering would " +
                    "cancel a fall. There is no fall.)");
            }

            // ---- WHAT "arrive" DOES AND DOES NOT DO ON A ROCKET ------------
            //
            // The plain-projectile half of this feature describes `arrive` as
            // the thing that stops a shot "orbiting the cursor", and that is
            // true there: a bullet's velocity is rewritten directly, so it can
            // be turned as hard as the code likes and will sit on a point
            // spinning. A ROCKET cannot. It has momentum and a minimum turn
            // radius, so it flies THROUGH the cursor and comes back round.
            //
            // I nearly shipped two different false claims about this. The first
            // was to copy the bullet's sentence. The second - after a
            // simulation showed the shot circling three times in eight seconds
            // - was to conclude the threshold must be the turn radius, and to
            // raise `arrive` to it automatically. Measured across its whole
            // range, that buys almost nothing:
            //
            //     arrive  0.0  0.5  1.5  3.0  4.1  8.0   (turn radius 3.7)
            //     laps    3.7  3.8  3.1  3.2  3.0  1.9
            //
            // So the honest answer is that **nothing you can set here stops a
            // rocket circling a stationary point** - only its range, its
            // lifetime, or you moving the cursor does. The field is kept
            // because it still shaves the loop and it stops the thrash right on
            // top of the point, and the log now says what it is worth.
            //
            // > A measurement that refutes your fix is worth as much as one
            // > that confirms it. Both of my explanations were plausible and
            // > the second was going to rewrite the author's number.
            float radius = (terminal > 0f && turnRate > 0f)
                ? Mathf.Min(terminal, (h.maxSpeed > 0f) ? h.maxSpeed : terminal)
                  / (turnRate * Mathf.Deg2Rad)
                : 0f;

            Log.LogInfo(
                fileName + ": guided - its tightest turn circle is " +
                radius.ToString("0.##") + " units in radius" +
                ((radius > 8f)
                    ? " - THAT IS WIDE, and it will feel sluggish. Raise " +
                      "\"turnRate\" or lower \"cruise\"."
                    : ".") +
                " It stops steering within " + arrive.ToString("0.##") +
                " units of the cursor. Note that unlike a bullet, a rocket " +
                "cannot settle ON your cursor: it has momentum and that turn " +
                "radius, so it flies through and comes back round for another " +
                "pass. Nothing in this block changes that - its range, its " +
                "lifetime, or you moving the cursor are what end the flight.");
        }

        // ROTATING SPIRAL - successive shots step a fixed angle.
        //
        // See ForgeSpiral for the design. The short version: `AngleOffset` is a
        // public per-instance property that `GetDirections` reads to place the
        // whole volley, so advancing it rotates the fan as a rigid body and
        // composes with `spread` and `angleVariance` for free.
        private static void ApplySpiral(
            WeaponData weapon, JObject root, bool isGadget, string fileName)
        {
            JToken token = root["spiral"];

            if (token == null)
                return;

            var cfg = new ForgeSpiral.Config();

            // "spiral": 20 is the short spelling for "20 degrees a shot",
            // matching how `execute` and `fade` take a bare number.
            if (token.Type == JTokenType.Float ||
                token.Type == JTokenType.Integer)
            {
                cfg.step = (float)token;
            }
            else
            {
                var o = token as JObject;

                if (o == null)
                {
                    Log.LogWarning(
                        fileName + ": \"spiral\" should be a number of degrees " +
                        "like 20, or an object with \"step\", \"sweep\" and " +
                        "\"reset\". Ignored.");
                    return;
                }

                // An explicit "enabled": false is an off switch that keeps the
                // rest of the block in the file for later.
                if ((bool?)o["enabled"] == false)
                {
                    Log.LogInfo(
                        fileName + ": \"spiral\" is present but " +
                        "\"enabled\": false, so shots leave on your aim as " +
                        "normal. The settings are kept for whenever you turn " +
                        "it back on.");
                    return;
                }

                cfg.step = (float?)o["step"] ?? 20f;
                cfg.sweep = Mathf.Max(0f, (float?)o["sweep"] ?? 0f);

                string reset = (string)o["reset"];

                if (!string.IsNullOrEmpty(reset))
                {
                    string r = reset.Trim().ToLowerInvariant();

                    if (r == "pull" || r == "trigger" || r == "release")
                    {
                        cfg.resetOnPull = true;
                    }
                    else if (r != "never" && r != "none")
                    {
                        Log.LogWarning(
                            fileName + ": spiral \"reset\": '" + reset +
                            "' is not recognised - use \"pull\" (each trigger " +
                            "pull starts on your aim again) or \"never\" (the " +
                            "default: the rotation carries on across pulls). " +
                            "Read as \"never\".");
                    }
                }
            }

            // A STEP OF ZERO WOULD ROTATE NOTHING, and a block that parses,
            // logs a summary and then does nothing is the failure shape this
            // project keeps paying for. Refused out loud instead.
            if (Mathf.Approximately(cfg.step, 0f))
            {
                Log.LogWarning(
                    fileName + ": spiral \"step\" is 0, so nothing would " +
                    "rotate and the block is ignored. Set it to the degrees " +
                    "each shot should advance - 20 gives a full turn every 18 " +
                    "shots. A negative value spins the other way.");
                return;
            }

            // A step that outruns its own arc turns a sweep into a two-position
            // flip-flop. Legal, occasionally wanted, never intended silently.
            if (cfg.sweep > 0f && Mathf.Abs(cfg.step) >= cfg.sweep)
            {
                Log.LogWarning(
                    fileName + ": spiral \"step\" (" +
                    Mathf.Abs(cfg.step).ToString("0.#") + " degrees) is as big " +
                    "as its \"sweep\" (" + cfg.sweep.ToString("0.#") +
                    "), so instead of sweeping it will flip between the two " +
                    "ends of the arc. Use a smaller step for a smooth pass - " +
                    "sweep divided by step is roughly the shots per pass.");
            }

            // SPIRAL ITSELF WORKS ON A GADGET - the stepping rides
            // `WeaponBase.DoShoot`, which a gadget goes through - but
            // `reset: "pull"` does not. That reset watches the trigger's
            // falling edge through a `Shooter.Update` postfix, and a gadget has
            // no held trigger to fall. Worth one line because the rest of the
            // block behaving correctly is what makes the missing half hard to
            // spot.
            if (isGadget && cfg.resetOnPull)
            {
                Log.LogWarning(
                    fileName + ": spiral \"reset\": \"pull\" does nothing " +
                    "on a GADGET - it watches the ship trigger being released, " +
                    "and a gadget has no trigger to hold. The spiral itself " +
                    "still steps normally; the angle simply carries on from " +
                    "where it was instead of restarting. Use \"reset\": " +
                    "\"never\" to say that on purpose, or move this to the " +
                    "primary or secondary slot.");
            }

            ForgeSpiral.Register(weapon, cfg);

            ForgeSpiral.Describe(
                cfg, fileName, weapon.name, weapon.fireRate,
                weapon is HitscanWeaponData);
        }

        // EXECUTE - finish anything already below a health threshold.
        //
        // See ForgeExecute for the design. The short version: it hooks the one
        // method every damage route funnels into, and calls the game's own
        // Die() so the game's own kill-credit line pays out normally.
        // WHICH enemy this weapon's targeting picks. Opt-in, per weapon.
        //
        // Runs LAST of the feature appliers, because it stamps itself onto two
        // of them and they have to exist first - the same ordering ApplyRally
        // depends on. See ForgeTargeting for why the scope is airtight: the
        // registry is keyed on WeaponData, so a weapon without this block
        // resolves to null and every finder takes its original code path.
        // WHICH enemy this weapon's targeting picks. Opt-in, per weapon.
        //
        // Runs LAST of the feature appliers, because it stamps itself onto two
        // of them and they have to exist first - the same ordering ApplyRally
        // depends on. See ForgeTargeting for why the scope is airtight: the
        // registry is keyed on WeaponData, so a weapon without this block
        // resolves to null and every finder takes its original code path.
        private static void ApplyTargeting(
            WeaponData weapon, JObject root, string fileName)
        {
            JToken token = root["targeting"];

            if (token == null)
                return;

            var cfg = new ForgeTargeting.Config();
            string word;

            // "targeting": "lowest" is the short spelling; the object form
            // exists for `unitsOnly`.
            if (token.Type == JTokenType.String)
            {
                word = (string)token;
            }
            else
            {
                var o = token as JObject;

                if (o == null)
                {
                    Log.LogWarning(
                        fileName + ": \"targeting\" should be a priority name " +
                        "like \"lowest\", or an object with a \"priority\" key. " +
                        "Ignored, so this weapon targets the way it always did.");
                    return;
                }

                word = (string)o["priority"] ?? (string)o["mode"];
                cfg.unitsOnly = (bool?)o["unitsOnly"] ?? false;
            }

            bool known;
            cfg.mode = ForgeTargeting.Parse(word, out known);

            if (!known)
            {
                Log.LogWarning(
                    fileName + ": targeting \"priority\": '" + word +
                    "' is not one I know. Use nearest, farthest, lowest, " +
                    "highest, weakest, toughest, unshielded, shielded or " +
                    "forward. Ignored, so this weapon targets the way it " +
                    "always did.");
                return;
            }

            if (cfg.mode == ForgeTargeting.Default)
            {
                Log.LogWarning(
                    fileName + ": \"targeting\" has no \"priority\" in it, so " +
                    "there is nothing to change. Add one, or drop the block.");
                return;
            }

            ForgeTargeting.Register(weapon, cfg);

            // STAMPED ONTO THE TWO CONFIGS THAT CANNOT RESOLVE A WEAPON
            // THEMSELVES, and this is the part to get right - it is exactly the
            // shape of the rally bug, where a reflector was left unwired and the
            // feature silently did nothing on it.
            //
            // Everything else resolves the priority from the SHOT (homing,
            // chain, ricochet, split - `ForgeKillsPatch` stamps every projectile
            // with its weapon) or from the weapon directly (the curve beam). The
            // catalyst picks targets from a NODE, and converge picks its focus
            // at DoShoot before any shot exists, so those two need the config
            // handed to them.
            int stamped = 0;

            ForgeCatalyst.Config catalyst;

            if (ForgeCatalyst.TryGet(weapon, out catalyst) && catalyst != null)
            {
                catalyst.targeting = cfg;
                stamped++;
            }

            ForgeTargeting.Describe(weapon, cfg, fileName);

            if (stamped > 0)
            {
                Log.LogInfo(
                    fileName + ": targeting priority also handed to " + stamped +
                    " block(s) that pick targets without a shot to read it " +
                    "from (the catalyst node).");
            }
        }

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

            // THE ONE SOUND KEY THAT NEVER RESOLVED ANYTHING. This stored the
            // raw string and ForgeRally handed it straight to
            // AudioManager.PlaySfx, which looks up by guid - so a rally sfx had
            // to be a raw id, a custom sound name silently did nothing, and no
            // warning was printed either way. Every other sound key in the mod
            // goes through ResolveChargeSfx; this one was missed. Found while
            // auditing the sound keys for SOUNDS.txt.
            cfg.sfx = ResolveChargeSfx(
                (string)o["sfx"], weapon.shootSfx, false, "rally sfx",
                fileName) ?? "";

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

            // SWIPE - the third reflector, and the one this function forgot.
            //
            // Round 11 test 101: *"it doesn't rally the projectiles, only
            // reflects once."* Exactly right, and the cause was one missing
            // line here. `ForgeSwipe.Config` already had a `rally` field and
            // `ForgeSwipeController` already passed it into the reflect call -
            // BOTH ENDS WERE BUILT AND THE WIRE BETWEEN THEM WAS NEVER RUN, so
            // every swipe reflected with a null rally and no stacks accrued.
            // The build log even said so ("rally needs something that CATCHES
            // bullets"), which read as advice about his file rather than as the
            // mod reporting its own gap.
            //
            // A blade only counts if it is actually set to bat shots back;
            // `deflects: false` is a pure damage swing with nothing to rally.
            ForgeSwipe.Config swipe;

            if (ForgeSwipe.TryGet(weapon, out swipe) && swipe != null)
            {
                if (swipe.deflects)
                {
                    swipe.rally = cfg;
                    attached++;
                }
                else
                {
                    Log.LogWarning(
                        fileName + ": \"rally\" found a \"swipe\" block with " +
                        "\"deflects\": false - a blade that does not bat shots " +
                        "back has nothing to rally. Set \"deflects\": true.");
                }
            }

            // DEFLECT, which used to be refused here.
            //
            // ForgeDeflect rides the PROJECTILE PREFAB, and a plain
            // config-class reference on a prefab-attached component arrives
            // NULL on every clone - the trap that silently disabled the whole
            // of gas and catalyst once. So it takes the same route those two
            // now use: an int id, which Unity serializes happily, resolved
            // through ForgeConfigTable in Awake.
            //
            // ApplyDeflect runs before ApplyRally, so the component already
            // exists on the cloned prefab by the time we get here.
            var deflectPw = weapon as ProjectileWeaponData;

            if (root["deflect"] != null && deflectPw != null &&
                !deflectPw.usePhysics && deflectPw.projectilePrefab != null)
            {
                var dComp = deflectPw.projectilePrefab
                    .GetComponentInChildren<ForgeDeflect>(true);

                if (dComp != null)
                {
                    if (dComp.mode == ForgeDeflect.ModeReflect)
                    {
                        dComp.rallyId = ForgeConfigTable.Id(cfg);
                        attached++;
                    }
                    else
                    {
                        // A deflector set to DESTROY never sends anything back,
                        // so there is nothing for a rally to buff. Naming it
                        // beats letting the module look broken.
                        Log.LogWarning(
                            fileName + ": \"rally\" needs deflect's " +
                            "\"mode\" to be \"reflect\" - on \"destroy\" the " +
                            "shot is deleted rather than returned, so there " +
                            "is nothing to catch again.");
                    }
                }
            }

            // THE "NOTHING CATCHES BULLETS" CHECK BELONGS HERE, NOT ABOVE THE
            // DEFLECT BLOCK. It used to sit before it and `return` early, which
            // meant a weapon with `rally` + `deflect` and no shield or catalyst
            // was refused on its way to the one thing that WOULD have worked -
            // the deflect wiring is twenty lines further down. Same shape as the
            // swipe gap above: the code to make it work existed and was
            // unreachable. Counting all four reflectors first, then reporting,
            // is what makes the message true.
            if (attached == 0)
            {
                Log.LogWarning(
                    fileName + ": \"rally\" needs something that CATCHES " +
                    "bullets to hang off, and this weapon has none of the four " +
                    "- a \"shield\" block (with \"onBlock\": \"reflect\"), a " +
                    "\"catalyst\" block, a \"swipe\" block with " +
                    "\"deflects\": true, or a \"deflect\" block with " +
                    "\"mode\": \"reflect\". On its own rally does nothing, " +
                    "because there is nothing to reflect with.");
                return;
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
                "extendFuel", "hurtsOwner", "hurtsAllies", "solidToOwner",
                "seek", "seeEnemies", "friendlyShots",
                "follow", "followSpeed", "followDelay",
                "hold", "holdRadius", "holdDelay", "holdSpeed",
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
            bool isGadget,
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

                // Both of these were documented in the builder page and used by
                // ForgeTractorPatch, and neither was ever read from the file -
                // so the field existed at both ends and only the wire between
                // them was missing. Defaults match ForgeTractor.Config, so a
                // file that says nothing behaves exactly as it did before.
                cfg.holdStiffness =
                    Mathf.Clamp01((float?)o["holdStiffness"] ?? 0.65f);
                cfg.grappleDebug = (bool?)o["grappleDebug"] ?? true;

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

                cfg.slamTintPulse =
                    Mathf.Max(0f, (float?)o["slamTintPulse"] ?? 3f);
                cfg.slamTintStrength =
                    TintStrength(o, fileName, "tractor slam", 1f);

                string slamTint = (string)o["slamTint"];

                if (!string.IsNullOrEmpty(slamTint))
                {
                    Color st = Color.white;

                    if (ReadColor(
                            slamTint, "tractor \"slamTint\"", fileName, ref st))
                    {
                        cfg.slamTint = st;
                        cfg.slamHasTint = true;
                    }
                }

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

            // A TRACTOR BEAM MUST GO IN A NORMAL WEAPON SLOT, and this now
            // says so at build time.
            //
            // THE COMMENT THAT USED TO SIT HERE TALKED ITSELF OUT OF THE
            // WARNING ON A FALSE PREMISE: *"the compiler proves the check is
            // impossible at this level (a gadget holds a WeaponData rather than
            // being one), so it is documented in the builder page and the txt
            // instead."* The first half is wrong - the slot the FILE declares is
            // known right here, and three other blocks already take it
            // (`overheat`, `spinUp`, `shield`). The second half is true and is
            // not a substitute: page documentation is read by someone browsing
            // the page, and the person who needs this is the one who hand-edited
            // a JSON file and is now staring at a beam that does nothing.
            //
            // `Shooter.Update`'s postfix is the ONLY thing that drives this
            // beam, and a gadget calls `weapon.Fire(new FakeBarrel(...))`
            // directly, so the whole feature is inert there - not degraded,
            // inert.
            if (isGadget)
            {
                Log.LogWarning(
                    fileName + ": \"tractor\" cannot work on a GADGET. The " +
                    "beam is driven entirely by the ship's held trigger " +
                    "(Shooter), and a gadget bypasses that completely - it " +
                    "fires its weapon directly the moment the module " +
                    "activates. Nothing about the beam will run. Put it in the " +
                    "primary or secondary slot.");
                return;
            }

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
            bool isGadget,
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

            // A GADGET CAN LAY STICKIES AND THEN NEVER DETONATE THEM, and
            // `refire` - the trigger this happens to - IS THE DEFAULT.
            //
            // The two halves of the feature reach a gadget differently, which
            // is exactly what makes it invisible. Laying one rides
            // `Projectile.OnObjectHit`, which every shot goes through whatever
            // fired it. Detonating one on `refire` is a `Shooter.Shoot` PREFIX
            // - the press that would be the detonator - and a gadget never
            // touches a Shooter. So the mines appear, stick, and sit there
            // forever, which reads as "the explosion is broken" rather than as
            // "this trigger needs a trigger".
            //
            // Half-working is worse than not working: there is something on
            // screen, so nobody suspects the slot.
            if (isGadget && s.trigger == ForgeSticky.Trigger.Refire)
            {
                Log.LogWarning(
                    fileName + ": sticky \"trigger\": \"refire\" (the " +
                    "DEFAULT) cannot work on a GADGET. The shots will stick " +
                    "fine, but the thing that sets them off is the next pull " +
                    "of the ship's trigger - and a gadget fires directly " +
                    "without going through it, so they would stick and never " +
                    "blow. Use \"trigger\": \"fuse\" (a timer) or " +
                    "\"proximity\" (something comes close), both of which " +
                    "work on a gadget, or move this to the primary or " +
                    "secondary slot.");
            }

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

                // Said at BUILD time, because the runtime guard alone leaves a
                // file that reads as if it works. `projectileCount: 0` means
                // "this gun fires no bullets" - a pure melee swipe, a lunge-only
                // ram - so charge "count" has nothing to add pellets to, and it
                // used to produce a shotgun blast on release (round 11 test 80).
                if (cfg.count > 0 && weapon.projectileCount <= 0f)
                {
                    Log.LogWarning(
                        fileName + ": charge \"count\": " + cfg.count +
                        " is ignored, because this weapon is " +
                        "\"projectileCount\": 0 - it fires no bullets at all by " +
                        "design, so there is nothing to add pellets to. Drop " +
                        "charge \"count\", or give the weapon a real " +
                        "projectileCount if you did want it to shoot.");
                }

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

            // "holdFire": false MAKES THE CHARGED SHOT COMPETE WITH THE
            // WEAPON'S OWN AUTO-FIRE FOR ONE FIRE-RATE GATE, and on a fast
            // weapon that is why a release "does nothing" at random.
            //
            // `Release` ends with `if (!shooter.FireRatePassed) return;` - the
            // weapon's own cadence still applies. With holdFire ON that is
            // harmless, because nothing else has fired while you held. With it
            // OFF the gun has been firing all along, so at the instant you let
            // go there is nearly always a normal shot inside the last
            // 1/fireRate seconds and the charged one is refused in silence.
            //
            // Reported for R12 (carried from R11 test 14) as "the charge shot
            // doesn't fire". Warned rather than changed, because exempting the
            // release from the gate would alter every charge weapon already
            // written - see the R13 question.
            if (cfg.mode == ForgeCharge.Mode.Release && !cfg.holdFire &&
                weapon.fireRate > 0f)
            {
                float gap = 1f / weapon.fireRate;

                Log.LogWarning(
                    fileName + ": charge \"holdFire\": false on a " +
                    "\"release\" charge means the gun keeps firing normally " +
                    "while you hold - and the charged shot then has to pass " +
                    "the SAME fire-rate gate as those shots. At fireRate " +
                    weapon.fireRate.ToString("0.##") + " that is one shot " +
                    "every " + gap.ToString("0.###") + "s, so letting go " +
                    "inside that window drops the charged shot silently. It " +
                    "looks exactly like the charge not working. Either set " +
                    "\"holdFire\": true (hold builds, release fires, nothing " +
                    "in between) or lower \"fireRate\" so the window is " +
                    "wide enough to release in.");
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

                // AN UNRECOGNISED VALUE USED TO BECOME THE DEFAULT IN SILENCE,
                // and that is worse than an error. R10 test 84 was reported as
                // *"catalyst.trigger is not inside weapon builder.html"* - the
                // control is there, but it offers `different` and `any` while
                // the file said `"shot"`. That fell through to `different`,
                // which happens to look right (you shoot the node with your
                // other gun), so nothing seemed wrong except that the value
                // could not be found on the page. Say so instead.
                if (tr != "different" && tr != "any" && tr != "all" &&
                    tr != "self")
                {
                    Log.LogWarning(
                        fileName + ": catalyst \"trigger\": '" + tr + "' is " +
                        "not a value this key takes, so it fell back to " +
                        "\"different\". The two choices are \"different\" (only " +
                        "a shot from ANOTHER weapon sets the node off - the combo " +
                        "reading, and the default) and \"any\" (this weapon's own " +
                        "shots count too). The builder page's dropdown lists " +
                        "exactly those two.");
                }

                string rd =
                    ((string)o["redirect"] ?? "chain")
                        .Trim().ToLowerInvariant();

                if (rd != "chain" && rd != "split" && rd != "burst" &&
                    rd != "fan")
                {
                    Log.LogWarning(
                        fileName + ": catalyst \"redirect\": '" + rd + "' is " +
                        "not a value this key takes, so it fell back to " +
                        "\"chain\" (one shot leaping target to target). The " +
                        "other is \"split\" (one shot per target, all at once).");
                }

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
                //
                // ForgeCatalystNode reads `aimReflected ? AimNearest : aimMode`,
                // so while that bool is true it DISCARDS aimMode entirely - and
                // it defaults to true. Setting only "reflectAim" therefore used
                // to do precisely nothing, silently. Naming the fuller key is
                // now taken as choosing it: the old bool keeps its default only
                // while the new one is absent.
                JToken reflectAimTok = o["reflectAim"];

                cfg.aimMode =
                    ForgeBulletTurn.ParseAim((string)reflectAimTok);
                cfg.reflectSpeed =
                    Mathf.Max(0.05f, (float?)o["reflectSpeed"] ?? 1.5f);

                bool? aimReflected = (bool?)o["aimReflected"];

                cfg.aimReflected =
                    aimReflected ?? (reflectAimTok == null);

                // Both given, and the old one wins by the rule above. That is
                // legal but almost never meant, and it fails silently, so name
                // it rather than letting the weapon look merely wrong.
                if (aimReflected == true && reflectAimTok != null &&
                    cfg.aimMode != ForgeBulletTurn.AimNearest)
                {
                    Log.LogWarning(
                        fileName + ": catalyst sets \"reflectAim\": \"" +
                        ForgeBulletTurn.AimWord(cfg.aimMode) + "\" AND " +
                        "\"aimReflected\": true. The bool wins and reflected " +
                        "shots will go at the nearest enemy. Drop " +
                        "\"aimReflected\" to use \"reflectAim\".");
                }
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

            if (ForgeSpriteLibrary.TryGetAnyArt(cfg.nodeSprite, out custom) &&
                custom.sprite != null)
            {
                cfg.nodeArt = custom.sprite;

                cfg.nodeFrames =
                    ForgeSpriteLibrary.OptInFrames(custom, out cfg.nodeFps);

                return;
            }

            Log.LogWarning(
                fileName + ": catalyst \"nodeSprite\": '" + cfg.nodeSprite +
                "' is neither \"coin\" nor a sprite I can find - the " +
                "node keeps the weapon's own bullet art." +
                ForgeSpriteLibrary.NotFoundHint());
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
                    ForgeSpriteLibrary.ResolveStockIcon(iconName)
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
                ReadCloudMode(cfg, o, fileName);

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
                cfg.bloomOnImpact = (bool?)o["bloomOnImpact"] ?? false;
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

                if (cfg.impactOnly)
                {
                    Log.LogInfo(
                        fileName + ": gas - \"single\" describes one cloud " +
                        "where the shot lands, which a trail cannot be, so the " +
                        "TRAIL clouds drop that half" +
                        (cfg.drifts ? " and keep the drift" : "") +
                        ". The end cloud is still a single bloom.");
                }

                if (cfg.bloomOnImpact && !cfg.impactOnly)
                {
                    Log.LogInfo(
                        fileName + ": gas - \"bloomOnImpact\" is on, so the " +
                        "cloud lands at the FIRST thing this shot hits rather " +
                        "than wherever it finally dies. One cloud per shot " +
                        "either way.");
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

                // AND WITH IMPACT OFF IT RE-DAMAGES EVERY FRAME IT OVERLAPS
                // SOMETHING. Re-hitting one target is rate-limited by
                // `piercingData.damageRepeatDelay` and by nothing else, and a
                // delay of ZERO means "no limit" rather than "no repeat" - so a
                // puff that flies THROUGH an enemy instead of dying on it hits
                // it once per physics step, fifty times a second. That is what
                // "the preset has no impact so it hits the same target a ton"
                // was: not too much damage per hit, far too many hits, each one
                // firing the victim's own damage sound.
                //
                // 0.25s is the game's own instinct - it is the default
                // `HitscanWeaponData.damageRepeatDelay`, i.e. what the devs
                // chose for the other continuous-contact weapon in the game.
                PiercingData pd = pw.piercingData;

                if (pd.damageRepeatDelay <= 0f)
                {
                    pd.damageRepeatDelay = 0.25f;
                    pw.piercingData = pd;

                    Log.LogInfo(
                        fileName + ": gas - the puff's re-hit delay was 0, " +
                        "which means it damages one target EVERY PHYSICS STEP " +
                        "while overlapping it (about 50 times a second, each " +
                        "with its own hit sound). Set to 0.25s - the same " +
                        "delay the game's own beams use. Set " +
                        "\"weapon\".piercingData.damageRepeatDelay yourself to " +
                        "choose a different one.");
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
                fileName + ": gas - " + cfg.ModeWords + " cloud, radius " +
                cfg.radius.ToString("0.##") +
                (cfg.grows
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

                if (ForgeSpriteLibrary.TryGetAnyArt(cfg.sprite, out custom) &&
                    custom.sprite != null)
                {
                    cfg.art = custom.sprite;
                    cfg.artFrames =
                        ForgeSpriteLibrary.OptInFrames(
                            custom, out cfg.artFps);
                }
                else
                {
                    Log.LogWarning(
                        fileName + ": gas \"sprite\": '" + cfg.sprite +
                        "' is not a sprite I can find - the soft puff is " +
                        "drawn for you instead." +
                        ForgeSpriteLibrary.NotFoundHint());
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
                cfg.resetSlowdown = (bool?)o["resetSlowdown"] ?? true;

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
        // "cloud" is now a COMBINATION rather than one of four words.
        //
        // The four names still work exactly as before - they are just presets
        // over three independent switches now (see ForgeGas.Cloud). What is new
        // is that they compose: "grow drift" is a swelling cloud that also
        // rolls, and "single drift" is one big impact cloud that rolls away
        // from where it landed. Those were the two he asked for, and neither
        // had a name before.
        //
        // Separators are space, "+" and "," so every way a person might write
        // it lands in the same place. Explicit booleans win over the words, so
        // "cloud": "grow", "drift": true is unambiguous.
        private static void ReadCloudMode(
            ForgeGas.Config cfg, JObject o, string fileName)
        {
            string raw = ((string)o["cloud"] ?? "grow").Trim().ToLowerInvariant();

            string[] words = raw.Split(
                new char[] { ' ', '+', ',', '/', '&' },
                StringSplitOptions.RemoveEmptyEntries);

            bool grows = false, drifts = false, impact = false, hang = false;
            var unknown = new List<string>();

            foreach (string w in words)
            {
                switch (w)
                {
                    case "grow":
                    case "growing":
                    case "swell":
                    case "expand":
                        grows = true;
                        break;

                    case "hang":
                    case "still":
                    case "static":
                    case "stay":
                        hang = true;
                        break;

                    case "drift":
                    case "drive":
                    case "roll":
                    case "move":
                    case "moving":
                    case "drifting":
                        drifts = true;
                        break;

                    case "single":
                    case "burst":
                    case "grenade":
                    case "impact":
                        impact = true;
                        break;

                    case "and":
                    case "cloud":
                        break;

                    default:
                        unknown.Add(w);
                        break;
                }
            }

            if (unknown.Count > 0)
            {
                Log.LogWarning(
                    fileName + ": gas \"cloud\" - did not recognise " +
                    string.Join(", ", unknown.ToArray()) +
                    ". The words are \"grow\", \"hang\", \"drift\" and " +
                    "\"single\", and they COMBINE - \"grow drift\" is a " +
                    "swelling cloud that also rolls, \"single drift\" is one " +
                    "impact cloud that rolls away. Ignoring what I could not " +
                    "read.");
            }

            // Growth is opt-in by word, with one exception: a "cloud" value
            // nobody could read at all falls back to the documented default.
            // That reproduces the old four modes exactly - only "grow" grew.
            if (!grows && !hang && !drifts && !impact)
                grows = true;

            // Explicit booleans win over the words. Note the KEY NAMES: not
            // "drift", which is already the drift SPEED in units/second, and
            // casting a number to bool? throws - the exact shape of bug that
            // has cost this project two rounds. "grows" / "drifts" /
            // "impactOnly" collide with nothing.
            cfg.grows = (bool?)o["grows"] ?? grows;
            cfg.drifts = (bool?)o["drifts"] ?? drifts;
            cfg.impactOnly = (bool?)o["impactOnly"] ?? impact;

            // The enum is now only a label, so give it the closest one for the
            // benefit of anything that still prints it.
            cfg.cloud =
                cfg.impactOnly ? ForgeGas.Cloud.Single :
                cfg.drifts ? ForgeGas.Cloud.Drift :
                cfg.grows ? ForgeGas.Cloud.Grow :
                ForgeGas.Cloud.Hang;
        }

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

            if (ForgeSpriteLibrary.TryGetAnyArt(cfg.sprite, out custom) &&
                custom.sprite != null)
            {
                cfg.art = custom.sprite;

                cfg.artFrames =
                    ForgeSpriteLibrary.OptInFrames(custom, out cfg.artFps);

                return;
            }

            Log.LogWarning(
                fileName + ": shield \"sprite\": '" + cfg.sprite +
                "' is not a sprite I can find - the arc will be drawn from " +
                "\"angle\", \"radius\" and \"thickness\" instead." +
                ForgeSpriteLibrary.NotFoundHint());
        }

        // HOW HARD A TINT COMES THROUGH - read once, for every block that
        // tints a unit.
        //
        // His R13 ask, and it is a single number in ForgeUnitTint, so the only
        // real work is that NINE blocks apply a tint and they must all take it
        // or the answer to "can I do that for all the weapons that tint?" is
        // "for some of them", which is worse than no.
        //
        // BOTH SPELLINGS ARE ACCEPTED, deliberately. This mod's blocks are
        // split between a `tint`/`tintPulse` naming and a `color`/`colorPulse`
        // one - `slow` and `stun` use the first, `crit` and `charm` the second
        // - and that split is now baked into files people have written. Making
        // the author remember which block uses which prefix for the NEW key
        // would generate exactly one bug report per block. Reading either is
        // two lines here and no lines anywhere else.
        //
        // Out-of-range values are clamped rather than refused, but a value
        // above 1 is reported, because "opacity 100" is a reasonable thing to
        // type and silently means 1.
        private static float TintStrength(
            JObject o, string fileName, string block, float fallback)
        {
            if (o == null)
                return fallback;

            JToken t = o["tintStrength"] ?? o["colorStrength"];

            if (t == null)
                return fallback;

            float v;

            if (t.Type == JTokenType.Float || t.Type == JTokenType.Integer)
            {
                v = (float)t;
            }
            else
            {
                Log.LogWarning(
                    fileName + ": " + block + " tint strength should be a " +
                    "number from 0 to 1 (1 is the full colour, 0.35 is a hint " +
                    "of it). Ignored, so the tint stays at full strength.");
                return fallback;
            }

            if (v > 1.0001f)
            {
                Log.LogWarning(
                    fileName + ": " + block + " tint strength is " +
                    v.ToString("0.##") + " - the scale is 0 to 1, not a " +
                    "percentage, so this reads as 1 (the full colour). Use " +
                    "0.5 for half.");
            }
            else if (v < 0f)
            {
                Log.LogWarning(
                    fileName + ": " + block + " tint strength is negative, " +
                    "which reads as 0 - the unit will not be tinted at all. " +
                    "Set the block's own tint switch off if that is what you " +
                    "meant.");
            }

            return Mathf.Clamp01(v);
        }

        // A TINT STRENGTH WITH NO TINT DOES NOTHING - say so.
        //
        // Six of the nine tinting blocks have their colour OFF until a colour
        // is named, so `"slam": { "tintStrength": 0.4 }` with no `"tint"` is a
        // number that cannot possibly have an effect. That is a silent default
        // on a tuning path, which is the failure shape this project pays for
        // most often, and an easy mistake because the strength reads like an
        // opacity that would work on its own.
        //
        // WHY THIS IS ONE METHOD READING THE JSON rather than a check inside
        // each block: most of those blocks parse the strength BEFORE they
        // resolve their colour (the colour goes through `ReadColor`, which can
        // fail), so an in-place check would be asking a flag that is not set
        // yet. Reordering six parse blocks to suit a warning is the riskier
        // change. Asking the JSON is order-independent by construction.
        private static void WarnUnusedTintStrengths(
            JObject root, string fileName)
        {
            if (root == null)
                return;

            // block, the key that turns its colour on, whether that colour is on
            // by DEFAULT, and (optionally) the fix to suggest when the generic
            // "name a colour" advice is wrong for that block. The defaults-on
            // blocks can only fail the other way - by being switched off
            // explicitly.
            //
            // THE LAST TWO TINT A MUZZLE FLASH RATHER THAN A UNIT, so their gate
            // is a BOOL (`tintMuzzle`) and not a colour: the colour has a
            // default and cannot be missing, so the only way to set a strength
            // with nothing to apply it to is to have switched the tint off.
            // Which is exactly the case a generic "add a colour" message would
            // have sent the reader hunting for.
            var blocks = new[]
            {
                new[] { "slow", "tint", "off", "" },
                new[] { "slam", "tint", "off", "" },
                new[] { "contactDamage", "tint", "off", "" },
                new[] { "tractor", "slamTint", "off", "" },
                new[] { "crit", "color", "off", "" },
                new[] { "execute", "color", "off", "" },
                new[] { "charm", "color", "on", "" },
                new[] { "stun", "tint", "on", "" },
                new[] { "adaptive", "tint", "on", "" },
                new[] { "overheat", "tintMuzzle", "on",
                        "Set \"tintMuzzle\": true to get the heat colour back, " +
                        "or drop the strength." },
                new[] { "spinUp", "tintMuzzle", "on",
                        "Set \"tintMuzzle\": true to get the wind-up colour " +
                        "back, or drop the strength." },
            };

            foreach (string[] b in blocks)
            {
                try
                {
                    var o = root[b[0]] as JObject;

                    if (o == null)
                        continue;

                    // Either spelling, matching what TintStrength accepts.
                    JToken s = o["tintStrength"] ?? o["colorStrength"];

                    if (b[0] == "tractor")
                        s = o["slamTintStrength"] ?? s;

                    if (s == null)
                        continue;

                    JToken tint = o[b[1]];
                    bool defaultOn = (b[2] == "on");

                    bool off = defaultOn
                        // On by default, so only an explicit false turns it off.
                        ? (tint != null &&
                           tint.Type == JTokenType.Boolean && !(bool)tint)
                        // Off by default, so a missing key means no colour.
                        : (tint == null ||
                           (tint.Type == JTokenType.Boolean && !(bool)tint));

                    if (!off)
                        continue;

                    string fix = (b.Length > 3 && !string.IsNullOrEmpty(b[3]))
                        ? b[3]
                        : "Add \"" + b[1] + "\": \"#8CCCFF\" (or any colour " +
                          "name) to make it visible, or drop the strength.";

                    Log.LogWarning(
                        fileName + ": " + b[0] + " tint strength is set but " +
                        "that block has NO COLOUR" +
                        (defaultOn
                            ? " (\"" + b[1] + "\" is switched off)"
                            : " (\"" + b[1] + "\" is not set, and it is off " +
                              "until you name one)") +
                        ", so the strength does nothing at all. " + fix);
                }
                catch (Exception)
                {
                    // A WARNING-ONLY READ MUST NEVER BE THE THING THAT THROWS.
                    // Standing rule here since a validation line took five
                    // tests down with it: this whole method is advisory, so
                    // every block is guarded on its own and a surprising shape
                    // costs one missing warning rather than a broken weapon.
                }
            }
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

            // An EMPTY string means "no sound", not "a sound whose name is
            // nothing". It used to fall through to the not-found warning, so
            // every file that left a sound slot blank - which is most of them -
            // produced a line reading `charge "sfx" is set to '', which is
            // neither a custom sound nor a sound id this game has`. Four of
            // those in one log, all of them noise, one of them for a slot the
            // author never touched.
            if (string.IsNullOrEmpty(name) || name.Trim().Length == 0)
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
                        fileName + ": sound \"" + key + "\" is the stock " +
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
                fileName + ": sound \"" + key + "\" is set to '" + name +
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

                if (ForgeSpriteLibrary.TryGetAnyArt(cfg.meterSprite, out custom) &&
                    custom.sprite != null)
                {
                    art = custom.sprite;

                    cfg.meterFrames =
                        ForgeSpriteLibrary.OptInFrames(
                            custom, out cfg.meterFps);
                }
                else
                {
                    Log.LogWarning(
                        fileName + ": charge \"meterSprite\": '" +
                        cfg.meterSprite + "' is not a sprite I can find." +
                        ForgeSpriteLibrary.NotFoundHint() +
                        " Falling back to the weapon's own bullet.");
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

            var stock = ForgeSpriteLibrary.ResolveStockIcon(iconName);

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
