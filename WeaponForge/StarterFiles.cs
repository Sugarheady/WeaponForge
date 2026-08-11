using System.IO;

namespace WeaponForge
{
    // Writes the example weapon + README into a freshly created
    // "weapons" folder so first-time users have something that works
    // and something to read.
    public static class StarterFiles
    {
        public static void Write(string folder)
        {
            File.WriteAllText(
                Path.Combine(folder, "ExamplePopper.json"),
@"{
  ""name"": ""ForgeExample"",
  ""displayName"": ""FORGE EXAMPLE"",
  ""description"": ""A fast-firing cyan popper. Edit ExamplePopper.json to change me, or copy the file to make more weapons."",
  ""template"": ""Module Weapon White Popper"",
  ""baseLoadout"": ""Starter_Popper"",
  ""weapon"": {
    ""fireRate"": 8,
    ""cost"": 0.5,
    ""damage"": { ""amount"": 1.5, ""damageType"": ""Resource White"" },
    ""projectileCount"": 1,
    ""spread"": 0,
    ""angleVariance"": 3,
    ""projectileSpeed"": 30,
    ""projectileColor"": ""#33ddff"",
    ""projectileScale"": 1.25
  },
  ""module"": {
    ""color"": ""ColorBlue"",
    ""resourceGain"": { ""resource"": ""Resource White"", ""amount"": 12 }
  }
}
");

            File.WriteAllText(
                Path.Combine(folder, "ExampleWhiteTesla.json"),
@"{
  ""name"": ""WhiteTesla"",
  ""displayName"": ""WHITE TESLA"",
  ""description"": ""Experimental electrical discharge cannon."",
  ""template"": ""Module Weapon Caps Laser"",
  ""slot"": ""primary"",
  ""target"": ""enemies"",
  ""dischargeOnFire"": true,
  ""chainThroughEnemies"": true,
  ""buildupSeconds"": 2,
  ""hideBeam"": true,
  ""weapon"": {
    ""resourceUsed"": ""Resource White"",
    ""damage"": { ""amount"": 0, ""damageType"": ""Resource White"" },
    ""cost"": 1,
    ""fireRate"": 0.4,
    ""range"": 12,
    ""warmupTime"": 0.35,
    ""burn"": { ""Min"": 0, ""Max"": 0 },
    ""discharge"": {
      ""damage"": { ""amount"": 3, ""damageType"": ""Resource White"" },
      ""chainLength"": 6,
      ""subSystem"": ""Player""
    }
  },
  ""module"": {
    ""color"": ""ColorWhite"",
    ""resourceGain"": { ""resource"": ""Resource White"", ""amount"": 12 }
  }
}
");

            File.WriteAllText(
                Path.Combine(folder, "ExampleOrbitRing.json"),
@"{
  ""name"": ""OrbitRing"",
  ""displayName"": ""ORBIT RING"",
  ""description"": ""Projectiles orbit your ship, shredding what they touch."",
  ""template"": ""Module Weapon White Worm"",
  ""slot"": ""primary"",
  ""target"": ""enemies"",
  ""orbit"": true,
  ""orbitMode"": ""passive"",
  ""orbitDirection"": ""cw"",
  ""orbitRadius"": 3,
  ""orbitSpeed"": 140,
  ""orbitContactDamage"": true,
  ""orbitDamageRepeatDelay"": 0.3,
  ""orbitSpinUp"": 1,
  ""weapon"": {
    ""projectileCount"": 4,
    ""damage"": { ""amount"": 4, ""damageType"": ""Resource White"" }
  },
  ""module"": { ""color"": ""ColorBlue"" }
}
");

            File.WriteAllText(
                Path.Combine(folder, "ExampleWaveBeam.json"),
@"{
  ""name"": ""WaveBeam"",
  ""displayName"": ""WAVE BEAM"",
  ""description"": ""Shots ride a clean sine S, weaving toward the enemy."",
  ""template"": ""Module Weapon White Popper"",
  ""slot"": ""primary"",
  ""target"": ""enemies"",
  ""wave"": true,
  ""waveAngle"": 35,
  ""waveFrequency"": 2.5,
  ""waveMode"": ""helix"",
  ""weapon"": {
    ""fireRate"": 6,
    ""cost"": 0.5,
    ""damage"": { ""amount"": 1.5, ""damageType"": ""Resource White"" },
    ""projectileCount"": 2,
    ""spread"": 0,
    ""angleVariance"": 0,
    ""projectileSpeed"": 26,
    ""projectileColor"": ""#66ffcc""
  },
  ""module"": {
    ""color"": ""ColorBlue"",
    ""resourceGain"": { ""resource"": ""Resource White"", ""amount"": 12 }
  }
}
");

            File.WriteAllText(
                Path.Combine(folder, "ExampleSpiralVortex.json"),
@"{
  ""name"": ""SpiralVortex"",
  ""displayName"": ""SPIRAL VORTEX"",
  ""description"": ""Orbs spiral out from the ship and sling off toward enemies."",
  ""template"": ""Module Weapon White Worm"",
  ""slot"": ""primary"",
  ""target"": ""enemies"",
  ""orbit"": true,
  ""orbitMode"": ""passive"",
  ""orbitDirection"": ""cw"",
  ""orbitRadius"": 4,
  ""orbitSpeed"": 200,
  ""orbitContactDamage"": true,
  ""orbitDamageRepeatDelay"": 0.25,
  ""orbitSpiral"": ""launch"",
  ""orbitSpiralInner"": 0.4,
  ""orbitSpiralTime"": 0.6,
  ""orbitSpiralLaunchSpeed"": 14,
  ""orbitSpiralRange"": 14,
  ""weapon"": {
    ""projectileCount"": 5,
    ""damage"": { ""amount"": 3, ""damageType"": ""Resource White"" }
  },
  ""module"": { ""color"": ""ColorPower"" }
}
");

            File.WriteAllText(
                Path.Combine(folder, "ExampleTurretShot.json"),
@"{
  ""name"": ""TurretShot"",
  ""displayName"": ""SENTRY ROUND"",
  ""description"": ""Sentry disc ammunition. Not obtainable."",
  ""template"": ""Module Weapon White Popper"",
  ""source"": ""none"",
  ""weapon"": {
    ""damage"": { ""amount"": 1, ""damageType"": ""Resource White"" },
    ""cost"": 0,
    ""projectileCount"": 1,
    ""spread"": 0,
    ""angleVariance"": 0,
    ""projectileSpeed"": 18,
    ""rangeData"": {
      ""enabled"": true, ""range"": 7, ""destroyWhenReached"": true
    }
  },
  ""module"": { ""color"": ""ColorWhite"" }
}
");

            File.WriteAllText(
                Path.Combine(folder, "ExampleBoomerang.json"),
@"{
  ""name"": ""Boomerang"",
  ""displayName"": ""BOOMERANG"",
  ""description"": ""A spinning disc that flies out, turns around and comes back - hitting everything twice."",
  ""template"": ""Module Weapon White DiscGun"",
  ""baseLoadout"": ""Starter_Popper"",
  ""weapon"": {
    ""fireRate"": 1.5,
    ""cost"": 1,
    ""damage"": { ""amount"": 3, ""damageType"": ""Resource White"" },
    ""projectileSpeed"": 34,
    ""rangeData"": { ""enabled"": true, ""range"": 12, ""slowDown"": true }
  },
  ""boomerang"": {
    ""enabled"": true,
    ""returnPath"": ""home"",
    ""returnSpeed"": 1.2,
    ""returnDamage"": 1.5,
    ""pierce"": true,
    ""rehit"": true,
    ""onCatch"": ""vanish""
  },
  ""module"": {
    ""icon"": ""HUD_GridTiles_13"",
    ""color"": ""ColorBlue""
  }
}
");

            File.WriteAllText(
                Path.Combine(folder, "ExampleTurretDisc.json"),
@"{
  ""name"": ""TurretDisc"",
  ""displayName"": ""SENTRY DISC"",
  ""description"": ""A disc that glides to a stop, then sprays fire for four seconds."",
  ""template"": ""Module Weapon White DiscGun"",
  ""slot"": ""primary"",
  ""target"": ""enemies"",
  ""source"": ""starter"",
  ""weapon"": {
    ""damage"": { ""amount"": 2, ""damageType"": ""Resource White"" },
    ""resourceUsed"": ""Resource White"",
    ""cost"": 4,
    ""fireRate"": 0.8,
    ""projectileCount"": 1,
    ""projectileSpeed"": 14,
    ""rangeData"": {
      ""enabled"": true, ""range"": 5, ""slowDown"": true,
      ""destroyWhenReached"": false
    },
    ""lifetimeData"": { ""enabled"": true, ""time"": 4 },
    ""impactBehaviour"": { ""enabled"": false },
    ""projectileBounceData"": { ""enabled"": true, ""layerMask"": ""Ground"" }
  },
  ""module"": { ""color"": ""ColorWhite"" },
  ""turret"": true,
  ""turretWeapon"": ""Forge Weapon TurretShot"",
  ""turretInterval"": 0.25,
  ""turretAim"": ""rotate"",
  ""turretRotation"": 140,
  ""turretDirection"": ""cw"",
  ""turretDelay"": 0.8,
  ""turretContactDamage"": true,
  ""turretContactRadius"": 0.6,
  ""turretContactDelay"": 0.4
}
");

            File.WriteAllText(
                Path.Combine(folder, "README.txt"),
@"WEAPON FORGE - custom weapon definitions
=========================================

Every *.json file in this folder becomes a starting loadout in PUNK.
Copy ExamplePopper.json, rename it, and edit away. Errors are logged
to BepInEx/LogOutput.log with the file name.

Weapons are built and registered at game startup, so saving mid-run
and using Continue works. Edits are read at startup / when the ship-
select screen opens - restart the game to see changes.

TOP-LEVEL KEYS
--------------
name         (required) unique internal id, letters/numbers only
template     (required) what to clone. Any of:
             - a weapon module (""Module Weapon White Popper"")
             - a GADGET module (""Module Active Purple AirMines"",
               Surge, Teardrop, Caps Igniter, Caps Vial, Electron
               Circuit, Generator Fuel) - a real gadget with its own
               behavior
             - a raw weapon asset incl. enemy weapons (""Weapon_Grunt"")
             Decides behavior + visuals/projectile model.
slot         where it equips (default ""primary""):
               ""primary""   - left click
               ""secondary"" - right click
               ""gadget1"" / ""gadget2"" / ""gadget3"" - the 1/2/3 keys
             Weapons and gadgets are different module types, but you can
             mix freely: a gadget template in a gadget slot stays a
             gadget; a weapon template in a gadget slot becomes a gadget
             that fires that weapon; a gadget template in a weapon slot
             fires its weapon on click. The slot picks the final type.
target       who the weapon hurts (default ""enemies""):
               ""enemies"" - hits enemies, not you. Also FIXES enemy
                           weapon templates (FireBall, Crawler Laser,
                           etc.) which otherwise only hurt the player.
               ""player""  - original enemy-style targeting (hurts you).
             Normal player weapons are already ""enemies"", so this is a
             no-op for them.
source       where the weapon can show up (default ""starter""):
               ""starter""         - only as a new-game loadout pick
               ""loot""            - only as a drop from crates
               ""starterAndLoot""  - both
               ""none""            - NEVER offered. Still built and
                 findable by name, so use it for helper weapons that
                 only exist to be referenced (subEmitter stages,
                 turret ammo).
lootFrom     (loot only, optional) WHICH crates it drops from. Blank or
             ""all"" = every pool. The five that work:
               ""white""   the plain Crate (biggest pool, 18 entries).
                         Also CrossJock + CrossRed Bomber enemies.
               ""caps""    Crate Caps, 11 entries.
               ""purple""  Crate Purple, 11 entries.
               ""tech""    Crate Tech - only 5 entries, so BEST ODDS.
                         Also the Crawler enemy.
               ""queen""   the Queen's own pool (she drops 3 at once).
             One name or a list: ""lootFrom"": [""white"", ""caps""].
             Crate Green / Money / Level 2 and the Boxes drop no
             modules at all, so they are not options (Level 2 has a
             pool but the game never rolls it).
lootRepeat   (loot only, optional) by default a weapon you already own
             stops dropping - that is the game's rule, 120 of ~145
             stock modules zero their own drop weight once found. Set
             true to keep dropping at full chance, or a number like
             0.5 so each copy you own halves the odds of the next.
lootWeight   (loot only, optional) drop chance vs other crate modules,
             default 10. Higher = more common. Crates roll from a
             weighted pool; your weapon joins it at this weight.
shop         (optional, default false) if true, the weapon can be
             bought in the in-run shop. Independent of ""source"" - a
             weapon can be starter and/or loot and/or shop.
shopPrice    (shop only) cost in money/yellow (default 100).
shopUnlockLevel  (shop only) how many STATIONS the player must unlock
             before it can appear in the shop (default 1). PUNK gates
             the shop by station count: 0 = available from the very
             first shop, 1 = after the 1st station, etc. Your weapon is
             GUARANTEED to appear once that tier is reached (unlike the
             stock weighted pool).
displayName  loadout + module card title
description  loadout + module card description
baseLoadout  loadout to clone for the ship/slots (default Starter_Popper)
weapon       { field overrides applied to the cloned weapon }
module       { field overrides applied to the cloned module card }

GADGETS: a gadget fires its weapon when you press its key (1/2/3),
on a cooldown of 1/fireRate, spending ""cost"" of ""resourceUsed"" per
use - so those weapon fields control the gadget's feel. The loadout
carries ONLY the weapon you make (plus the ship) - it does NOT keep
the base loadout's popper on left-click.

ENEMY WEAPONS: raw ""Weapon ..."" assets (many used by enemies) can be
used as templates. Most resolve fine; if one isn't loaded in memory
when you reach the menu, the log will say it wasn't found - pick a
different one.

VISUALS (inside ""weapon"")
--------------------------
projectileColor  recolor the projectile / beam. ""#33ddff"" hex, an
                 HTML name (""cyan""), a game color (""ColorBlue""), or
                 ""rainbow"" for an animated RGB cycle.
                 Projectiles: tints sprites/trails/particles.
                 Hitscan: tints the beam sprite, light, impact.
rainbowSpeed     (rainbow only) hue cycles per second, default 0.5.
projectileScale  size multiplier. Projectiles: bigger shot + matching
                 hit radius. Hitscan: beam thickness. (Minions: not
                 scaled - only recolored - to avoid breaking them.)

CUSTOM ART (inside ""weapon"")
-----------------------------
projectileSprite  use art YOU drew. Put a PNG in the ""sprites"" folder
                 next to the DLL and name it here. Use the Sprite Sheet
                 Builder.html tool to cut a sheet up visually - it
                 writes the .json the mod reads. NOTE the name is of a
                 thing INSIDE the sheet, not the file: ""petrolbm"" is
                 one still frame, ""petrolbmAnim"" is the animation.
projectileSpriteOnly  true if your sprite appears drawn ON TOP of the
                 old one - it switches off the template's own glow and
                 trail particles so only your art shows.
projectileGlow   false if your art looks washed out / recolored. Some
                 ammo renders through an emissive shader that blows
                 detail toward white; false swaps in the plain one.
trail            a streak behind the shot, made of sprite puffs (the
                 game has no ribbon trails - every trail in PUNK is a
                 particle system dropping puffs per unit travelled).
                 Shorthand: true adds a stock one, false removes the
                 template's, ""myPuff"" makes it out of that sprite.
                 Full form:
                   ""trail"": { ""sprite"": ""myPuffAnim"",
                              ""color"": ""orange"", ""perUnit"": 20,
                              ""lifetime"": 0.3, ""size"": 0.8,
                              ""sizeEnd"": 0, ""fade"": true }
                 perUnit is density, lifetime is how long the streak
                 is (stock 0.025 is very short - try 0.2-0.5), sizeEnd
                 0 tapers it to a point. An ""...Anim"" name makes every
                 puff play the whole flipbook. See HOW TO MAKE
                 WEAPONS.txt for the rest.

CUSTOM SOUNDS
-------------
Every sound field takes EITHER a game sfx GUID OR the name of an
audio file you supply. Put a file in a ""sounds"" folder next to the
DLL and use its file name without the extension:

  sounds/mylaser.wav   ->   ""shootSfx"": ""mylaser""

Slots: shootSfx (per volley), reloadSfx, startSfx,
continousShootSfx (loops while held - game's own typo, one 'u'),
releaseSfx, warmupSfx (loops), and explosion.sfx.

.wav is best - decoded directly, always works, ready instantly.
.ogg and .aiff work. .mp3 usually works but depends on the
platform's decoder; convert to .wav if it stays silent.

The clip is registered as a REAL game sound, so 3D positioning, the
SFX volume slider and the mixer all apply to it. Settings you don't
specify are copied from the sound you replaced. continousShootSfx
and warmupSfx are forced to loop, because the game stops those by
handle.

Too loud? Very likely - exported audio is hotter than the game's.
Add a .json with the SAME NAME as the audio file:
  { ""volume"": 0.6 }
It also takes looping, is3d, priority, repeatMinDelay,
cancelPrevious, and variants for random per-shot selection:
  { ""variants"": [""shot1.wav"", ""shot2.wav""] }
There is no pitch control - bake it into the files.
Full details in sounds/README.txt, written on first run.

MODULE EXTRAS (inside ""module"")
--------------------------------
icon          module card icon. A game sprite name, e.g.
              ""HUD_GridTiles_8"" (the weapon icons) - the builder page
              has a dropdown to pick one by weapon name. A few:
              Popper=13, Bolt=1, Dart/Rocket=29, Shotgun=7, Laser=8,
              AirMine=25, Drone=19.
              OR art YOU drew: the same names projectileSprite takes,
              so a PNG in the ""sprites"" folder called myicon.png is
              ""icon"": ""myicon"". Draw it SQUARE (stock icons are
              24x24) - the card stretches art to a square, though the
              pickup on the ground keeps its shape. Any size works;
              it is scaled to match the stock icons. The icon is
              TINTED by ""color"" below, so draw white/grey and let
              color do the work, or set color to ""#ffffff"" to see
              your own colours. A game sprite wins a name clash, and
              the log says so.
color         game ColorAsset name (ColorWhite, ColorOrange,
              ColorPurple, ColorBlue, ColorRed, ColorYellow,
              Color Tech, ColorPower) OR a hex value ""#7fd4ff""
resourceGain  { ""resource"": ""Resource White"", ""amount"": 12 }
              change which resource (and how much) equipping this
              weapon adds to your ship's tanks. NOTE: can't be a
              SHARED resource like ""Resource Money"" (currency) - the
              game manages those run-wide, so giving one per-weapon is
              ignored (it would otherwise hang loading).
powerNodes    how many power cores can attach to the module (the
              ""0 / N"" cap in the grid). A number = fixed (""powerNodes"":
              6 always gives 6), or a range { ""min"":4, ""max"":8 } for
              a random roll. The Popper is normally 2-3.

TEMPLATES (behavior comes from the type)
----------------------------------------
Projectile guns (machine guns, rifles, shotguns, missiles):
  Module Weapon White Popper / White Bolt / White Dart / White Shotgun
  / White Blades / White Derbis / White DiscGun / White Worm
  / Purple Marbles / Purple Flack / Purple Shard / Purple Rocket
  / Caps Rocket / Electron Rocket / Electron Zapper / Caps Flame ...
Hitscan beams:  Module Weapon Caps Laser
Lobbed physics: Module Weapon Caps Loon / Purple Can / Purple ClusterCube
                / Tech Dandelion
Minion pets:    Module Weapon Fly

WEAPON FIELDS (all weapon types)
--------------------------------
damage: { amount, damageType }   fireRate     warmupTime
burstSize    burstDelay          projectileCount   spread
angleVariance    angleOffset     knockbackForce    pushForce
resourceUsed (e.g. ""Resource White"")   cost
barrelLength     shootSfx/startSfx/... (a game sfx GUID, or the name
                 of your own audio file - see CUSTOM SOUNDS below)
explosion: { radius, damages: [ { amount, damageType } ] }
discharge: { damage: {...}, chainLength, subSystem: ""Player"" }
aimAssistData: { enabled, maxAngle, isPredictive }

PROJECTILE WEAPON FIELDS
------------------------
projectileSpeed    projectileSpeedVariance    projectileRadius
usePhysics         collidesWithSlime
projectilePrefab (asset name - steal another weapon's projectile!)
rangeData:    { enabled, range, variance, slowDown, destroyWhenReached,
                spawnExplosion, fireSub }
lifetimeData: { enabled, time, timeVariance, spawnExplosion, fireSub,
                discharge }
homingData:   { enabled, targetMode: ""AutoSeekWhenShot"", acceleration,
                torque, maxSpeed, maxAngularVelocity }
piercingData: { enabled, damageRepeatDelay, knockBackRepeatDelay }
projectileBounceData: { enabled }
movementNoiseData:    { enabled, angle, frequency }  (organic Perlin wobble)

WAVE / CURVED MOTION (top-level keys, projectile weapons)
---------------------------------------------------------
wave:true + waveAngle + waveFrequency + waveMode (single/synced/helix)
   a clean, repeating sine ""S"" (Super Metroid wave beam). Collisions
   follow the curve. See ExampleWaveBeam.json.
wobble:true + wobbleAngle + wobbleFrequency
   easy alias for the game's organic movementNoiseData wander (above).

RICOCHET (top-level keys, plain projectile weapons)
---------------------------------------------------
ricochet: true, or { targets, bounces, seek, seekRange, seekCone,
   scatter, speedMultiplier, damageMultiplier, pierceWins }
   Bullets that BOUNCE. The bouncing is the game's own, and damage is
   applied BEFORE the bounce, so a shot ricocheting off an enemy has
   already hurt it. What the game has no concept of is a bounce COUNT -
   a stock bouncer bounces forever - so that is what this adds.
   targets: ""terrain"" (default) / ""enemies"" / ""both"" / ""none"".
   bounces: a number, or ""infinite"". Default 3. With infinite and no
     rangeData/lifetimeData the shots never die - the mod warns.
   seek: true redirects each bounce at the nearest enemy instead of
     mirroring off the surface, so it reliably hits something else.
   scatter: degrees of random angle per bounce - also the escape for a
     shot trapped between two parallel walls.
   speedMultiplier / damageMultiplier: fraction kept per bounce
     (1 = arcade, the default).
   pierceWins: only matters if piercingData is also on. The game checks
     piercing first and returns early, so a pierced enemy is never
     bounced. false (default) turns piercing off; true keeps it and
     ricochets off terrain only.
   When the bounces run out the shot runs its own impactBehaviour and is
   destroyed - so it explodes if you set spawnExplosion, else vanishes.
   Not for usePhysics shots (no bounce code exists on those).

HOMING (top-level keys, plain projectile weapons)
-------------------------------------------------
homing: true, or { turnRate, range, cone, delay, maxTurn, retarget,
   predict, faceTravel }
   Bullets that CURVE onto enemies. The game's own ""homingData"" only
   works on lobbed/usePhysics shots - ProjectileWeapon assigns it inside
   that branch only - so a fast straight bullet like the Popper's needs
   this instead. It steers by rotating the shot's velocity, and because
   the projectile's collision sweep is rebuilt from that velocity every
   frame, THE HITBOX CURVES TOO.
   turnRate (deg/sec) is the main dial, but what you see is the turn
   RADIUS = speed / turnRate: at projectileSpeed 30, turnRate 180 gives
   a ~9.5-unit radius, 360 a ~4.8 hard chase, 90 an almost-straight ~19.
   The mod logs the radius for your numbers and warns when it is too wide
   to notice.
   For a BEAM-LIKE STREAM: high fireRate + a long segment sprite. Spacing
   is projectileSpeed / fireRate, and at 20 px per world unit a speed 30
   / fireRate 20 gun spaces shots 1.5 units (30 px) apart. Add
   piercingData so it doesn't stop on the first enemy.
   Not for usePhysics weapons (the log points you at homingData).

SPIRAL ORBIT (top-level keys, on an orbit weapon)
-------------------------------------------------
orbitSpiral: ""launch"" (spiral out then fly off, don't return) or
   ""sweep"" (spiral out and recycle - sprinkler). Plus orbitSpiralInner,
   orbitSpiralTime, orbitSpiralLaunchSpeed, orbitSpiralRange. See
   ExampleSpiralVortex.json.
impactBehaviour:      { enabled, spawnExplosion, fireSub, discharge }
electricityData:      { enabled, isSource, chainLength, conductivity,
                        damage: {...}, ... }

HITSCAN WEAPON FIELDS
---------------------
range    rayWidth    damageRepeatDelay

PHYSICS WEAPON FIELDS
---------------------
projectileSpeed    projectileSpeedVariance    projectilePrefab

MINION SPAWNER FIELDS
---------------------
minionPrefab    projectileSpeed    minionFacesShootDirection

NOTES
-----
- Resource names: ""Resource White"" (stamina), ""Resource Caps""
  (orange), ""Resource Purple"", ""Resource Electron"" (blue),
  ""Resource Health"" (red), ""Resource Money"" (yellow), ""Resource
  Fuel"", ""Resource Tech"". Shorthand ""White"" etc. also works.
- ""Resource Money"" is SHARED/currency and is NOT usable as a weapon
  resource at all - not as ""resourceUsed"", ""resourceGain"", or
  ""damageType"" (any of them hangs/breaks the game). Weapon Forge
  ignores/reverts it with a log warning. Use White/Caps/Purple/
  Electron/Health/Fuel/Tech instead.
- Asset references are looked up by name; use the AssetRipper export
  to find prefab/sprite/module names.
- Some game fields have quirky spelling: ""multiplyer"",
  ""repeatedDropChanceMultiplyer"", ""lowTreshold"". Match them exactly.
- Any field you leave out keeps the template's original value.
- A field name that doesn't exist on the target logs a warning to
  the BepInEx log and is skipped.
");
        }
    }
}
