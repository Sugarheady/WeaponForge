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
muzzleSprite     your own art for the MUZZLE FLASH, from the same
                 ""sprites"" folder and by the same names. A still name
                 gives one frame, an ""...Anim"" name plays the flipbook.
                 Works on EVERY weapon type - bullets, lobbed grenades,
                 hitscan lasers, minion spawners - because the flash
                 belongs to the weapon, not the shot, and the game spawns
                 it from one shared path. 73 of 77 stock weapons have a
                 flash; if yours is one of the 4 without, set
                 ""muzzleParticlePrefab"" too and the log will say so.
                 Applied to EVERY particle system in the flash: most
                 muzzle prefabs are two layered puffs, and changing only
                 the first leaves the stock puff showing underneath.
                 DRAW IT SMALL - 20 px per world unit, so flash art is
                 bullet-sized. Without ""muzzleColor"" the template's own
                 coloured fade is drained to grey so your art keeps its
                 own colours (PopperRed would otherwise paint it red);
                 the brightness fade is kept. muzzleColor still works and
                 the two compose.
impactSfx        a sound where the shot LANDS. A custom sound from the
                 ""sounds"" folder (file name, no extension) or a stock
                 sfx id.
                 WHAT THE GAME ALREADY DOES: every one of the 37 enemy
                 prefabs wires an onDamage event to a sound player, so an
                 enemy already makes a noise when damaged. But that sound
                 belongs to the ENEMY - every weapon hitting a Fly sounds
                 identical - and hitting TERRAIN makes no sound at all.
                 So this is a per-weapon layer, and impactSfxOn decides
                 where it plays.
                 Rate-limited on purpose: a piercing shot sits inside an
                 enemy for many frames and an ungated sound there is a
                 buzz. The gap is the weapon's own
                 piercingData.damageRepeatDelay when it has one, else
                 0.08s; beams use 0.12s.
                 Works on bullets, lobbed shots and beams. A BEAM needs a
                 ""beam"" block too (even empty) - the sound rides on the
                 beam's visual, and without a beam block that visual is
                 shared with other weapons.
impactSfxOn      where impactSfx plays: ""any"" (default), ""enemy"" or
                 ""terrain"". Use ""terrain"" to add a sound the game
                 genuinely lacks without doubling up on the enemy's own
                 damage sound.
impactSprite     your own art for the impact puff, from the ""sprites""
                 folder, same names as projectileSprite. Applied to ALL
                 THREE effect slots a shot can use: impact,
                 out-of-range and end-of-life - "" my impact looks like
                 this"" means the shot's death however it died.
                 Those prefabs are SHARED (6 stock weapons use
                 ""ImpactParticle Ammo Popper""), so a private copy is
                 made and your weapon never reskins anyone else's.
                 Not every template has one. If the log says there was
                 nothing to apply to, use a template that spawns an
                 impact effect (Popper, Flame, Toroid) or set
                 impactBehaviour.destroyEffect to an
                 ""ImpactParticle Ammo ..."" prefab yourself.
                 On a BEAM this feeds beam.impactSprite (the hit spark),
                 so it needs a ""beam"" block; an explicit
                 beam.impactSprite wins if both are set.
impactSpriteFps  frames per second for animated impact art. Blank = the
                 animation's own fps.
muzzleSpriteFps  frames per second for an animated flash. Blank = the
                 animation's own fps. A flash only lives ~0.4s, so a
                 20-frame animation needs about 50 fps to play through
                 once - too slow and you only see the first few frames.

SHAPING A FLASH OR AN IMPACT - read this if yours looks like a cloud of
tiny puffs, or is too small no matter what you draw.

A stock flash is not one picture, it is a BURST OF PARTICLES.
""MuzzleParticle Popper"" emits bursts of 30 AND 20 across its two
systems - fifty puffs. Right for smoke, completely wrong for one piece
of drawn art. ""ImpactParticle Ammo Popper"" is the same, 30 and 20.

muzzleCount      how many particles each burst emits. SET 1 for a
impactCount      single drawn flash / impact instead of a cloud.
                 Applies to every burst on every system in the prefab;
                 the log says how many that was.
muzzleSize       particle size in WORLD UNITS. Stock is 1, so 2 is
impactSize       double.
                 THIS IS THE ONLY SIZE THAT MATTERS. Particles ignore
                 pixelsPerUnit completely - the quad is sized by this
                 number, not by your sprite - which is why redrawing
                 the art bigger, or changing the sheet's PPU, does
                 nothing at all here.
muzzleFollow     the flash RIDES THE BARREL instead of being stamped
                 where you last fired. The game positions the emitter
                 at the barrel on each shot and then leaves it loose in
                 the world, so a slow weapon leaves its flash hanging
                 in the air the moment you move. A fast weapon already
                 looked attached only because it re-stamps 30 times a
                 second. true parents it, so it follows position AND
                 aim between shots. Does not change WHERE it sits -
                 that is still barrelLength.
muzzleSpread     how far the puff THROWS ITSELF from where it spawned, as
impactSpread     a multiple of the prefab's own scatter. 0 = ONE PUFF,
                 DEAD ON THE SPOT - the fix for ""why will my flash not
                 stay in one place"". A stock flash scatters two ways at
                 once: a random launch speed per particle (Popper 0-40
                 and 0-70) AND a spawn circle (radius 0.68 / 0.49), so
                 even at muzzleCount 1 the puff lands somewhere new every
                 shot. An impact already spawns ON the contact point, so
                 there it is launch speed alone. 1 = stock, 0.3 = tight,
                 0 = pinned. Neither moves WHERE the effect is placed.
                 impactSpread works on a stock puff with no custom sprite.
muzzleRotate     whether the particles turn with the emitter.
impactRotate     Every stock prefab is set to FACE THE CAMERA and
                 ignore the emitter's rotation, which is why art drawn
                 to point somewhere never points anywhere. true aligns
                 them to the emitter instead - and the game already
                 rotates that to YOUR AIM for a muzzle, and to the
                 SURFACE NORMAL for an impact, so directional art
                 follows the gun or points away from the wall for free.
                 Both default to ON once you set custom art (nobody
                 imports a directional sprite wanting it camera-locked,
                 and a symmetric puff cannot tell the difference). Set
                 false for the stock behaviour.
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

BURN (inside ""weapon"")
-----------------------
Burn damage-over-time lives on the VICTIM, not on the shot, so these
change how burn behaves on ENEMIES while the weapon is equipped. Your
own burn is never affected. They need a burn source to be visible - a
weapon that applies no burn has nothing to speed up or recolour.

burnTickRate     ticks/sec ADDED to how fast enemy burn ticks. Stacks
                 across equipped copies, and is capped by
                 MaxTicksPerSecond in the BepInEx config (default 100).
                 A plain number is a flat boost:
                     ""burnTickRate"": 0.5
                 An object also scales with the module's LEVEL, so a
                 green-crate BoosterCore placed next to this weapon on
                 the grid speeds its burn up:
                     ""burnTickRate"": { ""base"": 0.5, ""perLevel"": 0.25 }
                 base is the level-1 value; perLevel is added for each
                 level above 1. Leave perLevel out (or 0) and it behaves
                 exactly as a plain number always has.
burnColor        recolours enemy burn flames. A game colour name, a
                 #hex, or ""rainbow"" to cycle.
burnRgbSpeed     rainbow only: hue cycles per second.
burnColorTerrain true also recolours burning terrain, not just enemies.

(With Module Forge installed these feed ITS burn engine so the two mods
never fight; with Weapon Forge alone the bundled engine does the work.
Either way the behaviour is the same.)

CUSTOM SOUNDS
-------------
SILENCE first: put ""none"" in ANY sound field and that slot goes quiet.

  ""shootSfx"": ""none""

(""silent"", ""off"", ""mute"" and ""-"" work too.) It has to be a word, not
an empty value - leaving a key out is how you say ""keep the template's
sound"". Silencing ""continousShootSfx"" also silences ""releaseSfx"",
because the game only plays that when it stops the loop.

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
canBeBoosted  whether a green-crate BOOSTERCORE next to this weapon in
              the grid raises its module LEVEL.

              READ THIS IF A BOOSTERCORE SEEMS TO DO NOTHING. Every
              stock weapon module in the game ships with this OFF -
              Popper, Bolt, Worm, Blades, all of them - and a Forge
              weapon is a clone of one of those, so it inherits the
              same OFF. The game checks the flag before applying the
              level bonus at all, which is why stacking BoosterCores
              around a weapon changes nothing while setting
              ""level"" by hand works fine.

              Set ""canBeBoosted"": true to allow it. It is turned on
              FOR you (with a log line) if the weapon declares anything
              that scales per level, such as
              ""burnTickRate"": { ""base"": 1, ""perLevel"": 10 } -
              asking for per-level scaling on something that can never
              gain a level is two contradictory requests. Set it to
              false explicitly to keep the stock behaviour.

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
   a repeating weave (Super Metroid wave beam). Collisions follow the
   curve. See ExampleWaveBeam.json.
waveShape: ""sine"" (default) / ""zigzag"" / ""saw""
   The feature only rotates the shot's HEADING each frame, so the shape
   of the path is entirely the shape of that function. sine eases from
   side to side (the rolling S). zigzag SNAPS between the extremes and
   holds, so the shot flies a straight leg, turns hard, flies another -
   ""z"" and ""v"" both work and mean this, because a run of sharp corners
   IS a row of Vs. saw sweeps one way then snaps back: a hook.
   A zigzag swings ~1.5x wider than a sine at the same waveAngle.
waveSharpness: 1
   ZIGZAG ONLY. How much of each cycle is a STRAIGHT LEG rather than a
   turn, 0-1, and it means exactly that: 0.5 is half and half. 1 is a
   true snap; 0 rounds it off into a triangle wave.
waveFaceTravel: false
   Turn the ART to face where the shot is going. The game sets a
   projectile's rotation once, at Shoot, so a weaving shot's art points
   where it was AIMED - very visible on a zigzag with drawn directional
   art. Off by default.
wobble:true + wobbleAngle + wobbleFrequency
   easy alias for the game's organic movementNoiseData wander (above).

RICOCHET (top-level keys, plain projectile weapons)
---------------------------------------------------
ricochet: true, or { targets, bounces, seek, seekRange, seekCone,
   scatter, speedMultiplier, damageMultiplier, pierceWins,
   explodeOnBounce, dischargeOnBounce, subOnBounce, effectOnBounce,
   bounceEffectsOn }
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

   EFFECTS ON EVERY BOUNCE, with the shot carrying on:
   explodeOnBounce / dischargeOnBounce / subOnBounce / effectOnBounce,
   filtered by bounceEffectsOn (""any"" / ""terrain"" / ""enemies"").
   THE GAME MAKES THESE IMPOSSIBLE - its bounce branch RETURNS before
   the impactBehaviour block, so you get a bounce OR an explosion, never
   both. Note that DAMAGE, BURN and DIGGING already work on a bounce and
   need none of these. explodeOnBounce needs a real ""explosion"" block
   and dischargeOnBounce a chainLength above 0, or the builder switches
   them back off. Point-blank bounces (impactBehaviour.safetyDistance)
   are excluded, so a weapon still cannot blow up in your face.

   Not for usePhysics shots - those bounce as real rigidbodies instead,
   which is the separate ""bounce"" block.

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

CHAIN JUMP (top-level key)
--------------------------
""chain"": { ""jumps"": 4, ""range"": 8, ""damageMultiplier"": 0.75,
           ""speedMultiplier"": 1, ""revisit"": false,
           ""onEmpty"": ""continue"" }

A shot that HOPS from enemy to enemy: hit one, turn, fly at the next.

NOT THE SAME AS DISCHARGE. This mod has two things that jump between
enemies and they behave nothing alike:
  discharge  (""electricityData"" / ""dischargeOnFire"") spawns a
             LIGHTNING ARC from the hit point - instant, its own
             damage, jumps by conductivity. Nothing travels; the
             projectile is unaffected.
  chain      redirects the ACTUAL PROJECTILE. It visibly flies from one
             enemy to the next carrying its own damage, and can be
             dodged, blocked or run out of things to hit.
Use discharge for lightning, chain for a ricocheting knife.

  jumps     how many redirects. 3 jumps = up to 4 enemies hit.
  range     how far it looks for the next link, from where it just hit.
            This is the dial that decides whether a chain feels
            reliable or lucky - 8 clears a normal cluster, 4 hunts.
  damageMultiplier  per jump, MULTIPLYING. 0.75 over 4 jumps ends at
            about a third. Goes through the same shared damage value as
            grow and damageRamp, so all three compose.
  speedMultiplier   per jump. Under 1 makes it run out of steam.
  revisit   off = never hits the same enemy twice, which is what
            normally stops a chain ping-ponging until its jumps run
            out. ON gives you the PING-PONG deliberately: A->B->A->B.
            The enemy just hit is still excluded from the NEXT search
            only, so the shot cannot pick the body it is standing in
            and jump nowhere - which is why turning this on used to
            look like it did nothing. Pair it with jumpDelay or the
            bouncing is too fast to read.
  jumpDelay seconds to keep going after a hit before turning toward
            the next target. 0 (default) turns immediately. 0.1-0.25
            reads as the blade carrying through and then whipping
            round. Longer means it travels further between hops, so it
            can overshoot out of range.
  hover     needs a jumpDelay. The shot STOPS DEAD in mid-air for the
            pause and then snaps to the next throat. Not for a weapon
            whose rangeData has slowDown - that rewrites the speed
            every frame and fights it.
  onEmpty   nothing left in range: ""continue"" (fly on straight, like a
            normal piercing round) or ""vanish"" (stop there and run the
            weapon's own impact explosion).

PIERCING IS TURNED ON FOR YOU. The game only keeps a shot alive through
a hit when piercing is enabled, so without it a chain weapon would die
on its first target and look like the feature did nothing.

PLAIN PROJECTILES ONLY - chaining is driven by Projectile.TryHit, which
a lobbed (usePhysics) shot never reaches. For a lobbed weapon, the
game's own discharge gives a similar enemy-to-enemy feel.

There is a ""Chain Knife"" preset in the builder page.

SPLIT (top-level key)
---------------------
""split"": { ""on"": [""death""], ""count"": 3, ""spread"": 70,
           ""damage"": ""half"", ""aim"": ""auto"" }

The shot BREAKS INTO several, and the fragments INHERIT its live stats.

NOT THE SAME AS subEmitter, and the difference is the whole reason it
exists. A subEmitter fires a separate WEAPON, and the game builds that
weapon with no modules at all - so its damage is forever the number in
its file, however many +damage modules you fit. It is also never given
an owner, so ITS KILLS CREDIT NOBODY (""onKill"" never pays for one) and
it does no friend/foe check. And it fires straight up.

A split fragment is a COPY OF THE SHOT AS IT IS RIGHT NOW: upgraded
damage, burn, explosion, discharge, piercing, art and trail, plus
whatever ""grow"" or ""damageRamp"" had already done to it. It leaves
along the parent's REAL heading and its kills pay out normally.

Both keys still work and neither touches the other.

  on           one trigger or a list. ""death"" (impact / range /
               lifetime - the default), ""time"", ""distance"",
               ""hit"" (and keep going), ""bounce"". Combine freely.
  count        1-16 fragments per burst (default 3)
  spread       fan width in degrees (default 60; 360 = a ring)
  damage       ""half"" (default) / ""full"" / ""share"" / a number
  aim          ""fan"" (default) / ""target"" / ""auto""
  range        how far a targeted fragment looks (default 12)
  lockOn       targeted fragments keep following (default true)
  speed,scale  multipliers on the parent (default 1). scale shrinks the
               HITBOX as well as the art.
  delay        seconds for the ""time"" trigger (default 0.5)
  distance     units for the ""distance"" trigger (default 6)
  keepParent   ""time""/""distance"" normally use the shot up; true keeps
               it flying (default false)
  generations  how many times one lineage may split, 1-3 (default 1)
  maxPerShot   total fragments one shot may make (default 8)
  weapon       optional - fragments come from THIS weapon instead of
               being copies, still carrying the parent's live stats.
               Give it ""burstSize"": 1; only its first burst inherits.

""hit"" switches piercing on for you - without it the shot dies on its
first contact and the trigger could never repeat. ""bounce"" warns if
the weapon has no way to bounce.

Works on LOBBED weapons too - a grenade splits into grenades.

CONVERGING SHOTGUN (top-level key)
----------------------------------
""converge"": { ""focus"": ""distance"", ""distance"": 9, ""width"": 0,
              ""after"": ""spread"", ""maxTurnRate"": 0 }

The pellets of one volley CURVE INWARD and meet at a single point ahead
of the muzzle, then cross and fan out again. The shot reads as an
hourglass with a real SWEET SPOT at the waist: stand at the right range
and the whole volley lands on one enemy, stand too close or too far and
it scatters.

NOT ""aim every pellet at the same spot"" - that is just a weapon with
no spread. Each pellet keeps the launch angle ""spread"" gave it and
bends along a smooth arc onto the focal point, so the cone visibly
necks down on its way there.

IT SHAPES A FAN, IT DOES NOT CREATE ONE. You need ""projectileCount""
of 5+ and a ""spread"" (25-45). With one pellet there is no angle to
curve out of and nothing happens - the log says so.

  focus     ""cursor"" (DEFAULT) puts the meeting point under YOUR
            CROSSHAIR, so you choose the range every shot by how far
            out you aim. It is the EXACT mouse position, so aim close
            and they meet close, aim far and they meet far. Falls back
            to distance for anything with no crosshair (a sub-emitter,
            a turret). If it seems not to track, check the log: it says
            either ""meet your CROSSHAIR"" or ""meet N units ahead of the
            muzzle"", and the second means that file is still on
            ""focus"": ""distance"".
            ""distance"" pins it a fixed way ahead wherever you point
            instead, so landing a full volley is a POSITIONING problem
            - the weapon has one sweet spot and you move to it.
            ""target"" collapses the volley onto the nearest enemy in
            front of you - forgiving and much stronger. Falls back to
            distance if nothing is near.
            ""none"" is the SPAZER: they do not converge at all. The
            shots leave abreast across ""width"" and fly dead parallel
            forever, holding formation the whole way. It is also the
            cheapest mode here - no steering runs at all.
            THE SPAZER IGNORES THE WEAPON'S ""spread"": every shot is
            snapped onto your aim, because a rank that fans out is not
            a rank. ""width"" is what separates them, and one is filled
            in for you if you forget - a spazer with no width is a
            single bullet fired on top of itself, which looks like
            nothing is wrong at all.
  distance  how far ahead they meet. THIS IS THE SWEET SPOT, so it is
            the number that defines the weapon: 6 is a brawler, 14
            rewards holding the line.
  range     ""focus"": ""target"" only - how far to look, within 90
            degrees of your aim. Every pellet picks the target from the
            BARREL, so the whole volley agrees on one enemy.
  width     optional. Launches the pellets ABREAST on a line square to
            your aim (measured end to end) instead of all from the
            barrel point, so the silhouette becomes a wall collapsing
            to a point. Reads best with ""spread"": 0. Pellets that
            would spawn inside terrain are pulled back to just short of
            the wall, so a wide rank does not quietly lose its outer
            shots in a corridor. Keep it under about 4.
  after     what happens past the waist. ""spread"" (default) lets them
            cross and diverge, which is what makes the sweet spot
            matter. ""parallel"" turns them onto your aim so the volley
            runs on as a tight lane - a shotgun that becomes a lance.
  maxTurnRate  degrees/second ceiling. LEAVE AT 0, which means ""turn
            exactly as hard as the arc needs"". Capping it makes the
            outer pellets under-turn and miss the point on the wide
            side. Ignored on lobbed weapons.

DO NOT STACK IT WITH ""homing"". Both steer the same shot every frame
and neither reliably goes last, so they fight and the curve comes out
half-hearted. If you want the volley to track an enemy, that is what
""focus"": ""target"" is for. The log warns if both are set.

LOBBED (usePhysics) WEAPONS WORK TOO, BY A DIFFERENT ROUTE. A lobbed
shot is a rigidbody falling under gravity and cannot be steered -
rewriting its velocity every frame would cancel the fall and turn a
grenade into a missile. So each pellet KEEPS its launch angle and has
its SPEED solved instead, so that its particular arc comes down on the
focal point. The volley leaves as a fan of genuinely different
parabolas that all land on one spot, and because a steeper arc takes
longer they land STAGGERED - a mortar barrage onto a marked spot rather
than a shotgun blast. Give it a real ""spread"" (20-40): the spread is
what makes the arcs differ, and with spread 0 every grenade flies the
identical parabola. ""after"" and ""maxTurnRate"" do nothing there.

  The speed is solved by simulating the shot against the ACTUAL physics
  step, so drag and gravity scale are both accounted for. (The game has
  its own ballistic formula, ProjectileMath.LaunchSpeed, but it assumes
  no air resistance - against a Derbis it lands 0.6 to 18 units short,
  so it is not used.) The solved speed is kept between 0.1x and 4x the
  weapon's own projectileSpeed, so a distant focal point cannot turn a
  grenade into a railgun slug; if the point is out of reach at that
  angle the pellet is thrown as hard as allowed and FALLS SHORT, which
  is the honest result rather than a bug.

NOT HITSCAN - a beam arrives instantly, so there is no flight to shape.

There are ""Full Choke"" (cone to a point), ""Rail Rack"" (a wall of
shots folding into a lane) and ""Mortar Volley"" (lobbed arcs onto one
spot) presets in the builder page.

DAMAGE RAMP (top-level key)
---------------------------
""damageRamp"": { ""from"": 1, ""to"": 6, ""over"": ""distance"",
                ""span"": 0, ""curve"": 2, ""clamp"": true }

Damage that CHANGES while the shot flies - the railgun: weak up close,
devastating across the room. The game only models falloff; this ramps
either way, so ""from"" bigger than ""to"" gives a shotgun that fades
with range instead.

Values are MULTIPLIERS of the shot's normal damage, so 1 is unchanged
and it composes with +damage modules instead of replacing them.

  over    what drives it:
          ""distance"" (default) world units travelled. THE RAILGUN.
          ""time""     seconds in flight.
          ""pierce""   each enemy punched through. Needs piercing on.
                     The enemy that triggers a step is hit at the OLD
                     value, so it reads as building up.
          ""bounce""   each ricochet off a wall.
          pierce and bounce are PLAIN-PROJECTILE only - a lobbed shot
          goes through neither code path. The log says so and falls
          back to distance. distance and time work on both.
  span    how much of that reaches ""to"": units / seconds / a count.
          0 on a distance ramp = THE WEAPON'S OWN RANGE, so the shot
          peaks exactly as it runs out. A lobbed shot has no range to
          borrow and falls back to 12 units.
  curve   1 = steady. ABOVE 1 holds low then rushes, which is what
          makes a railgun feel like one: at curve 2 a shot is only
          x1.75 of the way to x4 at half range, then slams to full.
  clamp   false lets it keep climbing past ""to"" for as long as the
          shot lives.

IT COMPOSES WITH ""grow"". Both change damage in flight, and they now
publish into one shared value instead of overwriting each other - a
3-damage shot with growth x2 and ramp x4 lands for 24.

There is a ""Railgun"" preset in the builder page.

GRAVITY WELL (top-level key, plain OR lobbed)
---------------------------------------------
""gravity"": { ""radius"": 7, ""force"": 34, ""duration"": 2.5,
             ""drag"": 1.2, ""pullShots"": true }

A shot that opens a WELL - everything nearby is dragged into one point,
held, then it bursts. THE GAME HAS NO FIELD FORCES AT ALL; what it has
is the same AddForce that explosions and orbit rings use, pointed the
other way. That is the whole trick.

  where     ""impact"" (default) opens where it lands. ""flight"" opens on
            firing and TRAVELS with the shot - a vacuum cleaner rather
            than a trap.
  radius / force / duration    reach, strength, how long it lives
            (counted from when the well OPENED, which is why
            lifetimeData is switched off).
  falloff   ""linear"" (default) strongest at the middle - reads most
            like gravity. ""constant"" is a tractor beam. ""inverse"" makes
            things drift, then snap.
  holdRadius  inside this the pull STOPS. Not a nicety: at 0 everything
            piles onto one pixel and JITTERS, because each frame
            overshoots the centre and is yanked back. 0.5-1 fixes it.
  maxSpeed  ceiling on the fling, or a light enemy becomes a bullet.
  drag      per-second damping on anything caught. Makes the well feel
            THICK and stops things slingshotting through the middle.
  damagePerSecond   crush damage, applied once a second.
  burst     default true - explodes when it closes. Needs an
            ""explosion"" block or the builder turns it off.
  pullShots swallows ENEMY BULLETS too. Switches the mod's shot tracker
            on for you, and there is no alternative - enemy shots ship
            with their collider DISABLED and are invisible to any
            overlap query.
  pullPlayer  off by default; on, it is a movement tool as well.

There is a ""Singularity"" preset in the builder page.

CATALYST (top-level key, plain OR lobbed)
-----------------------------------------
""catalyst"": { ""node"": ""stop"", ""trigger"": ""different"",
              ""redirect"": ""chain"", ""targets"": 4,
              ""damageMultiplier"": 2.5, ""radius"": 1.4 }

A NODE YOU SHOOT INTO. Fire one, it hangs in the air; put another shot
through it and that shot is redirected at enemies with more damage.
Enemy fire reaching the node is turned around and sent back buffed.
Either use consumes the node.

YOUR OWN BULLETS CANNOT COLLIDE WITH EACH OTHER - PlayerProjectiles is
absent from its own row in the layer matrix, and a plain projectile has
its collider DISABLED because it resolves hits by raycast. So the node
watches a registry of your shots rather than waiting for a collision
that can never happen.

  node      ""stop"" (default) / ""travel"" / ""drift"" / ""toss"". Stop is far
            the easiest to aim a second shot into.
            ""toss"" is the THROWN COIN - the one mode that keeps its
            gravity, so it arcs, falls and SURVIVES LANDING. Wants a
            lobbed template (Derbis, Loon, Flame); a plain projectile has
            no rigidbody to fall with.
  gravityScale   toss only - how hard it falls. 1-1.5 is a readable arc.
  preferNodes    THE RICOSHOT: a redirected shot looks for another NODE
            before an enemy, so several coins cascade from one bullet.
            Cannot loop - a node is consumed the moment it fires.
  nodeSprite / spin / spinSpeed
            ""coin"" is a BUILT-IN gold coin drawn by the mod, so it needs
            no art file. The spin is an animated squash, not a flipbook -
            a coin turning about its axis is a disc whose WIDTH changes.
            The game's own money sound is
            11e8d93f-393e-4958-a468-863166ed6595 for ""triggerSfx"".
  trigger   ""different"" (default) = only ANOTHER weapon sets it off, so
            primary places and secondary detonates. ""any"" includes this
            weapon's own shots.
  redirect  ""chain"" grants the shot the mod's existing chain feature, so
            one bullet visits them in sequence; ""split"" BREAKS IT INTO
            ONE COPY PER TARGET, all flying at once. The copies are real
            shots - full damage, piercing and lifetime - so one can set
            off another coin and a kill by one pays the weapon.
            CHAIN IS PLAIN-PROJECTILE ONLY; split works on both, and the
            builder warns if you pair chain with a lobbed weapon.
  targets / range / damageMultiplier / speedMultiplier
            how many, how far it looks, and how much harder/faster.
            Split takes the NEAREST things first and walks outward, and
            EVERY COPY inherits the damage boost, not just the original.
  lockOn / lockOnTurnRate
            default true - the redirected shot FOLLOWS its target in
            rather than being pointed once and flying straight. Plain
            projectiles only, same limit as chain.
  targetObjects  default true - crates and boxes count as targets, so a
            split bursts them too. False picks only what fights back.
  chainFalloff / chainRevisit    chain only.
  catchEnemyShots / reflectDamage / reflectSpeed / aimReflected
            the reflect half. A reflected shot becomes properly yours.
  enemyShotsConsume   default true - the node dies on enemy bullets.
  armTime / lifetime / radius / max / uses / pulse

A stop/drift node has ""impactBehaviour"" switched off for you or it
would explode on the first wall instead of parking there.

THE COIN DOES NOT RICOCHET - YOUR BULLET DOES. The coin hangs there and
the shot you put through it is what comes out redirected.

There are ""Catalyst"", ""Marksman Coin"" (chain) and ""Sharpshooter Coin""
(split in three) presets in the builder page.

ON KILL (top-level key, any weapon)
-----------------------------------
""onKill"": { ""resource"": ""Resource Fuel"", ""amount"": 4,
            ""clearBurn"": 3, ""buffDuration"": 2.5,
            ""buffFireRate"": 0.3, ""buffMaxStacks"": 3,
            ""maxPerSecond"": 6 }

A weapon that GIVES SOMETHING BACK when it kills.

  buffIndicator  shows the buff ON SCREEN while it lasts, wearing this
            weapon's own module icon: ""feed"" (the game's own status line,
            where FUEL LOW appears), ""icons"", ""both"" or ""off"". Leave it
            out to follow the global default in the BepInEx config, which
            also picks what the label reads and whether other timed
            states and debuffs get one.

THE GAME CREDITS KILLS FROM ONLY THREE PLACES - a projectile hit, a beam
hit and an explosion - and records only which UNIT killed, never which
weapon, so the weapon is tracked here. And nothing else credits a kill at
all, not even Die(). The ""ram"" and the ""gas"" cloud now register theirs
properly, which is what makes ""dash through enemies to win fuel back""
actually pay out.

  resource  leave it OUT and it pays back the weapon's own resourceUsed.
  amount    per kill, clamped to the tank's capacity.
  asPickup  drop a pickup on the corpse instead of granting it instantly.
  chance    0-1, rolled BEFORE the cap so a miss does not consume it.
  maxPerSecond   0 = uncapped. Set it: ONE EXPLOSION CAN KILL A DOZEN
            ENEMIES in the same frame.
  clearBurn burn removed from your OWN ship per kill - the only payout
            that can save you mid-fire (the ship sheds 5/second alone).
  buffDuration / buffFireRate / buffDamage / buffMaxStacks
            a timed buff; the bonuses are FRACTIONS and need a duration.
            maxStacks 1 refreshes rather than stacks.
  sfx       plays on each payout, or ""none"".

Module Forge has the same thing as a MODULE, on the ship grid (any
weapon's kills) or in a weapon's grid (only that weapon's).

There is a ""Fuel Reaver"" preset in the builder page.

GAS CLOUDS (top-level key, plain OR lobbed)
-------------------------------------------
""gas"": { ""cloud"": ""grow"", ""radius"": 1.1, ""growTo"": 2.4,
         ""duration"": 3.5, ""burnPerSecond"": 1.5,
         ""damagePerSecond"": 2.5, ""slow"": 0.35 }

A shot that leaves a lingering CLOUD, and the cloud does the work.

PUNK HAS TWO UNRELATED BURN SYSTEMS. A weapon's ""burn"" raises a UNIT's
burn level - that one hurts. Separately, any shot hitting terrain sets
that CELL alight and the fire spreads on its own. But nothing reads a
burning cell except the flame particles: STANDING IN BURNING GROUND
DOES NOTHING TO ANYONE. The engine that would do it (CellType
.cellBehaviours) exists, has never been switched on by any cell type in
the game, and is only read by the SHIP - so a cell-based gas would
poison you and nothing else. Hence the cloud is the mod's own object.

Built on the game's flamethrower, ""Weapon Caps Flame"": lobbed puffs at
mass 0.1 with damping 1.7 - light and heavily damped, which is what
makes a puff STALL IN THE AIR rather than fly away.

  cloud     ""grow"" (default) stall, swell, thin out. ""hang"" one fixed
            size. ""drift"" keeps rolling. ""single"" ONE burst ON IMPACT
            ONLY - a gas grenade; a puff timing out in air leaves
            nothing.
  radius / growTo / growTime / duration / fade    shape and life.
  max       clouds alive at once; when full the OLDEST is retired, so a
            spray weapon rolls its budget forward. 0 = unlimited.
  burnPerSecond   the game's own fire. BURN DAMAGE IS FLAT - 1/sec
            while alight however high the level - so this buys
            DURATION, never intensity. Enemies shed 1/sec (you shed 5),
            so much above 1.5 is wasted, and Enemy_Cross Variant Tablet
            is FIREPROOF.
  damagePerSecond   the cloud's own damage, in the weapon's damage
            type. Scales, and is the only payload that touches the
            fireproof enemy.
  slow      fraction of velocity bled per second, 0-1. The game has a
            DragCellBehaviour for this and never used it.
  push      outward shove/sec. NEGATIVE PULLS INWARD and herds a group.
  puffGravity / puffDrag / puffPierceTerrain
            these change the PROJECTILE, not the cloud. ""puffGravity"": 0
            MAKES THE GAS FLOAT - the flamethrower template is lobbed, so
            its puffs otherwise fall and pool on the floor.
            puffPierceTerrain stops walls eating them (and then only
            igniteTerrain can burn the ground).
  igniteTerrain   burn ADDED per second to terrain in the cloud. The game
            only ignites ground on a projectile COLLISION, so this is a
            cloud's only route to it.
  extinguish / extinguishTerrain
            burn removed per second from UNITS / from burning GROUND.
            CALIBRATE extinguishTerrain PROPERLY: cell thresholds are not
            normalised - CellType_Solid (the main terrain) ignites at 45,
            Bark/Gold 10, Night 5, Moss 2.5, Slime 1 - and a burning cell
            is topped up by its neighbours. Under 45 loses the race; 120+
            clears Solid. The builder warns.
            The terrain one is the only thing in the mod that touches
            the spreading cell fire. ""extinguish"" always reaches YOU,
            even on a cloud that is otherwise harmless to you.
  hitEnemies / hitPlayer    hitPlayer is OFF by default, same call as
            the gravity well's pullPlayer.
  tickRate  seconds between applications; damage calls drive flashes,
            sounds and aggro, so not free every frame.
  color / puffs / swirl / sprite / sfx    THE ALPHA IN ""color"" MATTERS -
            gas should be see-through.

There are ""BioBlaster"", ""Smog"", ""Suppressant"" and ""Gas Grenade""
presets in the builder page.

SHIELD ARC / RAM (top-level key, any weapon)
--------------------------------------------
""shield"": { ""mode"": ""arc"", ""angle"": 40, ""radius"": 2.2,
            ""onBlock"": ""reflect"", ""drainPerSecond"": 1.5,
            ""effectiveness"": 2 }

A held ARC in front of the ship that catches incoming fire, and a
pointed RAM version that hurts what you charge into. The first weapon
here that is not a thing you throw.

THE GAME'S OWN SHIELDS COULD NOT BE REUSED. A Shield in PUNK is a damage
FILTER, not a barrier: on the player it has NO COLLIDER AT ALL, its
shader draws a full ring with no angle parameter, and it sits on a layer
enemy bullets do not collide with. What was worth taking is its pricing
idea - see ""effectiveness"".

AND NOTHING IN PUNK DOES CONTACT DAMAGE BETWEEN UNITS - no melee
component, no collision-stay handler anywhere. Flying into an enemy is
free in both directions, which is why the ram invents its damage.

  mode      ""arc"" (default) the reflector. ""ram"" the pointed one, which
            does NOT eat the trigger - a ram weapon is still a normal
            gun. ""both"" does each in its moment.
  activation  ""hold"" (default) / ""toggle"" / ""always"". Arc only.
            NONE work on a GADGET module: gadgets bypass the shooter, so
            there is no held trigger to raise an arc with.
  facing    ""auto"" (default) crosshair while shielding, movement while
            ramming - because A DASH FIRES ALONG YOUR MOVEMENT STICK,
            NOT YOUR AIM. Also ""aim"" / ""travel"" / ""frozen"".
  angle / radius / thickness   the shape. thickness also sets the outer
            edge (radius + thickness/2), which is exactly where beams
            stop and the ram's damage ends.
  onBlock   ""reflect"" (default) back as YOUR bullet, kills credited to
            you. ""reflectAtNearest"" aims it. ""destroy"" stops it.
            ""absorb"" eats it and REFUNDS resource - a battery you charge
            by standing in the wrong place.
  blockBeams  default true. A beam is a raycast, so it is cut short at
            the source. The game casts TWICE for a beam (damage and
            visual) and both are clipped.
  maxPerSecond  cap on shots stopped. 0 = unlimited; a low number makes
            the shield leak under heavy fire.
  drainPerSecond  resource/sec while up. A number or ""cost"" (= cost x
            fireRate). The arc DROPS when it cannot pay.
  costPerBlock / effectiveness   flat charge per shot, or the game's own
            formula - stopping d damage costs d / effectiveness. A flat
            cost prices SHOTS; effectiveness prices DAMAGE, so a swarm of
            pinpricks is cheap and one big hit is expensive.
  ramTrigger  ""either"" (default) / ""dash"" (the tap) / ""sprint"" (while
            boosting). Same key: a tap dashes, holding it boosts.
  ramDamage / ramKnockback / ramSpeedThreshold / ramRepeatDelay
            what a ram does, how hard it shoves, the speed below which
            it does nothing, and the per-target re-hit delay.
  ramSelfProtect  default true, and this is what ""it protects you""
            means. Enemies cannot hurt you by touch, but YOUR OWN SHIP
            DAMAGES ITSELF FOR CRASHING while you sprint - so without
            this a sprint-ram costs you health every time it works.
  ramIFrames  seconds of FULL invulnerability from a dash. 0 = none (the
            game's own dash has none either). Blocks all damage, so keep
            it short.

There are ""Reflect Shield"", ""Ram Shield"", ""Bulwark"" and ""Absorber""
presets in the builder page.

HARPOON / TETHER (top-level key, plain OR lobbed)
-------------------------------------------------
""tether"": { ""pull"": ""target"", ""length"": 3, ""stiffness"": 55,
            ""reelSpeed"": 1.5, ""duration"": 3 }

A shot that LATCHES ON and leaves a rope. The game's own Hook is NOT a
harpoon - it fires nothing, latches onto the nearest ""Grabbable"" in a
cone, and no enemy carries one. What was worth taking is its FORCE
MODEL: pull only past the rope's rest length, and stop once the gap is
already closing fast enough. That is what makes a rope feel taut
instead of magnetic.

Switches ""impactBehaviour"", ""lifetimeData"" and ""destroyWhenReached""
off - a harpoon that deletes itself on impact is a bullet.

  pull      ""target"" (default) drag THEM to you. ""self"" drag YOU to
            them - a traversal tool, and the only mode that does
            anything against TERRAIN (a wall has no rigidbody to drag).
            ""both"" the game's elastic leash, neither end can leave.
            ""anchor"" no pulling - they just cannot leave ""length"" from
            where they were struck, and the head stays put.
  length    rest length. NO force at all inside it.
  stiffness force per unit of OVERSTRETCH - a spring, not a constant tug.
  reelSpeed units/sec the rest length shrinks - an active winch. This is
            what turns ""tethered"" into ""reeled in"".
  duration  seconds from LANDING (not from firing - hence lifetimeData
            being switched off). breakDistance snaps it if overstretched.
  to        ""enemies"" (default) / ""terrain"" / ""any"". Terrain only makes
            sense with pull:self and the builder warns otherwise.
  damagePerSecond   bleed while attached, applied once a second.
  max       ropes at once. Above 1 you can haul several things into a
            pile. selfPull tunes the ""both"" mode. ropeWidth 0 = no line.

There is a ""Harpoon"" preset in the builder page.

STICKY BOMBS (top-level key, plain OR lobbed)
---------------------------------------------
""sticky"": { ""detonate"": ""refire"", ""max"": 6, ""ripple"": 0.08 }

Shots that STOP WHERE THEY LAND, hang there, and go off TOGETHER on
command. Nothing in the game does any part of this - no stick, no
attach, no joint, no ""set off the ones you already fired"".

It switches ""impactBehaviour"", ""lifetimeData"" and
""rangeData.destroyWhenReached"" OFF for you - any of them would delete a
bomb while it waited. The ""explosion"" block IS the bomb, so a sticky
weapon must have one or the builder refuses it.

  detonate  ""refire"" (default) lay up to max, then the next shot blows
            the lot instead of laying another - load, load, load, BANG.
            ""fuse"" each bomb blows N seconds after IT landed and the gun
            keeps firing normally. ""proximity"" is a minefield (never
            trips on you or your friends). ""manual"" is fuse + cull only.
  max       most stuck at once. Laying past it detonates the OLDEST
            rather than refusing the shot. In refire mode this is also
            the magazine.
  fuse      seconds from LANDING (not from firing - that is why
            lifetimeData gets turned off). 0 = waits for you.
  to        ""any"" / ""terrain"" / ""enemies"".
  follow    default true - a bomb on an enemy travels with it.
  armTime   cannot be detonated for this long after landing, so a fast
            second shot cannot blow one up on your own muzzle.
  ripple    seconds between bombs in a salvo. 0.08 reads far bigger than
            one simultaneous bang.
  damageOnStick
            plain shots only - a plain hit lands before anything of ours
            runs, so ""false"" zeroes the weapon's damage at build time. A
            LOBBED bomb never damages on landing at all, because that
            call lives inside the impact block we switch off.

There is a 60-second backstop on every bomb.
There is a ""Stickies"" preset in the builder page.

BOUNCING GRENADES (top-level key, LOBBED weapons only)
------------------------------------------------------
""bounce"": { ""restitution"": 0.35, ""to"": 1.05, ""overHeight"": 12,
             ""max"": 8, ""damageOnBounce"": true }

A lobbed shot that bounces off the world instead of dying on it, and
whose bounciness can be driven by HOW FAR IT FELL.

DIFFERENT MECHANIC FROM ""ricochet"", hence the different key. Ricochet
shapes the game's SCRIPTED bounce - a plain bullet mirroring off a
wall, no gravity involved. A lobbed shot has no scripted bounce at all;
it is a real physics body and the engine bounces it.

WHAT THE GAME HAS is almost nothing: its ""bouncing bomb"" is just
""impactBehaviour"" switched OFF so the shot stops destroying itself.
Eight stock weapons do that and rely on a fuse - and because the damage
call sits inside that same switch, all eight do NO CONTACT DAMAGE AT
ALL.

THIS BLOCK SWITCHES ""impactBehaviour"" OFF FOR YOU (it has to, or the
shot dies on contact one). Your settings are replayed on the LAST
bounce instead, so it still means ""what this shot does when it lands"".

  restitution  fraction of impact speed that comes back out. 0.3 is the
               stock grenade, 1 is arcade, above 1 is super-elastic.
  to           turns it into a HEIGHT RAMP - the bounciness after
               falling ""overHeight"" units. One weapon, two ways to
               play: skipped down a corridor it dies fast, dropped down
               a shaft it comes back at you.
  overHeight   world units of FALL that reach ""to"". Calibrated against
               the engine's real step, not sqrt(2gh) - physics shots
               have damping and the formula overstates landing speed by
               up to a quarter.
  curve        1 ramps steadily; above 1 stays dull until the drop is
               genuinely big.
  max          bounces before the shot ends - and ending fires your
               ""impactBehaviour"". ""Bounces 3 times then explodes"" is
               this. 0 = unlimited.
  minSpeed     below this impact speed a contact is not a bounce, so a
               settling grenade rolls instead of jittering forever. 1
               matches the engine's own threshold.
  friction     sideways grip - rolls vs skids. Left out, keeps 0.4.
  scatter      degrees of random turn per rebound. The escape from a
               shot trapped between two parallel walls.
  damageOnBounce / damageFalloff
               off by default (that is what stock bouncing bombs do).
               On, it goes through the game's own collision listener, so
               digging, cell shake, burn and kill credit come along.
               Falloff is per bounce; the first is always full.
  explodeOnBounce / dischargeOnBounce
               and the shot SURVIVES them. The game makes bounce and
               explode-on-impact mutually exclusive, so a grenade that
               blasts its way down a corridor only exists here.

SUPER-ELASTIC DOES NOT RUN AWAY. Damping eats more per bounce than a
modest overshoot adds, so restitution above 1 settles at a STEADY
bouncing height - about 2 units at 1.15, 7 at 1.3, 19 at 1.5.

NOTHING ELSE STOPS A LOBBED SHOT. ""rangeData"" does not count: the game
computes a range at launch and never reads it again. Use ""max"", a
""lifetimeData"" fuse, or both.

THE BOUNCE SOUND is ""impactSfx"" - there is no separate key.

There is a ""Skipper"" preset in the builder page.

CHARGE SHOT (top-level key)
---------------------------
""charge"": { ""mode"": ""release"", ""time"": 1, ""min"": 0.3,
            ""damage"": 3, ""size"": 1.8, ""color"": ""#ffdd55"" }

Hold the trigger, let go, one big shot - the Mega Man buster. A quick
tap still fires the weapon normally, so the gun is never dead in your
hands; you decide per shot whether to spend the time.

THE GAME HAS NONE OF THIS. Its one held-trigger mechanic, ""warmupTime"",
is a minigun SPIN-UP: it gates firing until a meter fills, then fires
normally forever, and it empties the instant you let go. Nothing in
PUNK reacts to the trigger coming UP.

Every bonus is a MULTIPLIER of the weapon's own number, so 1 is
unchanged and a charge composes with +damage modules instead of
replacing them. ""count"" and ""pierce"" ADD instead.

  mode      ""release"" (default) - holding builds the shot, letting go
            fires it. ""auto"" - the weapon keeps firing normally and the
            charge builds alongside; every time the meter fills, ONE
            shot comes out charged. auto costs you no DPS to use.
  time      seconds to FULL charge. The weapon's whole feel: 0.4 is a
            snappy tap-charge, 2 is a commitment you line up.
  min       fraction of full (0-1) a release needs to count as charged.
            Below it you get a normal shot (""tapFire"", default true).
  scale     default true - bonuses ramp with the meter. False makes
            anything past ""min"" a FULL charged shot.
  curve     1 = steady. Above 1 is slow then a rush at the end (2-3).
  holdFire  release mode, default true. False = fires as usual AND
            gives you a bonus shot on release. Strictly better than not
            charging, so it is a power fantasy rather than a balance.
  damage / size / speed / range / explosion / burn / push / knockback /
  spread / cost   multipliers at full charge. ""size"" grows the art AND
            the real hitbox. ""explosion"" scales radius and its damage
            together. ""knockback"" is the shove the shot gives YOU.
  count     ADDED pellets at full charge.
  pierce    ADDED enemies it passes through.
  color     tints the charged SHOT and the meter fades into it. It is
            MULTIPLIED over the shot's own colour, so keep the base
            bullet near-white if the charge tint is what you want to
            see.
  meter     the growing orb at the muzzle, drawn with THIS WEAPON'S own
            bullet art. ""meterSprite"" names your own art instead;
            meterFrom / meterTo / meterColor tune it.
  chargeSfx the looping charging sound. It writes the weapon's own
            ""warmupSfx"" slot - the same thing. A CUSTOM sound is forced
            to loop; a stock one-shot there plays once and then goes
            quiet. No stock projectile weapon fills that slot, so a
            charge weapon is SILENT while charging unless you set it.
  readySfx  played once the instant the meter fills. Without it you are
            watching the muzzle instead of the fight.

TWO THINGS IT CANNOT DO, and the builder refuses them out loud:

  NOT ON A GADGET. The 1/2/3 slots fire their weapon the instant the
  module activates, without going through the ship's Shooter, so there
  is no trigger to hold. Primary and secondary only.

  NOT ""release"" ON A BEAM. A beam is drawn only while the trigger is
  DOWN, so a shot fired the moment you let go would do its damage
  invisibly. Use ""mode"": ""auto"" on a beam instead.

DO NOT SET ""warmupTime"" AS WELL - different mechanic, same sound slot.
Stacking them just delays your shots.

Works on lobbed shots, and a charged grenade really is bigger: a
physics shot collides with a real collider, so scaling it up scales the
hitbox with it.

There is a ""Buster"" preset in the builder page.

GROWING ORBS (top-level key, on an orbit weapon)
------------------------------------------------
""orbitGrow"": { ""from"": 0.5, ""to"": 3, ""over"": ""charge"",
               ""span"": 4, ""curve"": 1, ""damageAtFull"": 0,
               ""hitbox"": true, ""breatheSpeed"": 1 }

USE THIS, NOT ""grow"". The projectile ""grow"" block does nothing on an
orbit weapon and now says so: an orbiting orb is only artwork (its
projectile component is switched off and the ring does its own damage),
so nothing grow hooks ever runs on one.

Sizes are MULTIPLIERS of the orb art's normal size - 1 is unchanged, and
""from"" bigger than ""to"" shrinks instead. The contact radius grows with
the art, so a big orb really sweeps a wider path.

  over    which clock drives it:
          ""charge""  (default) grows over ""span"" seconds from the moment
                    the ring switches on, then holds at full. A hold /
                    fire / toggle ring RESETS when you let go, so it
                    plays as a wind-up. A passive ring just grows once.
          ""orbLife"" each orb grows from when IT spawned, so a
                    destructible ring regenerates small orbs that swell
                    back. Needs orbitDestroyOnEnemy or
                    orbitDestroyOnTerrain to mean anything - the log
                    warns when they are off.
          ""breathe"" swells and shrinks forever between the two sizes.
                    Like orbitPulse but on the orb instead of the ring
                    radius, and the two combine happily.
  span    seconds from -> to (charge / orbLife).
  breatheSpeed  full in-and-out cycles per second (breathe).
  curve   1 = steady. Above 1 stays small then rushes to full (2 feels
          like charging up); below 1 jumps big early then eases.
  damageAtFull  damage multiplier at full size, ramped from 1x at the
          smallest. 0 (default) = size changes REACH only, so existing
          orbit weapons are unaffected. 2 = a full-size orb hits twice
          as hard.
  hitbox  false makes growth purely cosmetic (fixed contact size).
          Rarely what you want.
  reset   default TRUE - growth restarts every time the ring switches
          on, so each press rebuilds the charge.
          THIS WAS A BUG UNTIL NOW. When a ring switches off its orbs
          are only HIDDEN, never destroyed, and ""orbLife"" measures
          growth from each orb's own birth - so they came straight back
          at FULL SIZE and a charge ring only ever wound up once, on
          the very first press. ""charge"" mode always reset (it counts
          from when the ring went active); now both do. Set false for
          the old behaviour.

BRINGING THE RING UP / LETTING IT GO (top-level, on an orbit weapon)
--------------------------------------------------------------------
Without these the orbs BLINK IN and BLINK OUT of existence the frame
the trigger moves, which reads as a glitch rather than as summoning or
dismissing something.

orbitHoldDrainPerSecond
                   WHAT A HELD OR TOGGLED RING COSTS TO KEEP UP, and the
                   only way one ever costs anything. ""cost"" prices it off
                   the weapon itself (cost x fireRate - what a normal
                   weapon spends per second with its trigger held); a
                   number gives a flat rate.
                   The weapon's own ""cost"" does NOTHING here: the ship
                   charges you when it FIRES, and these modes suppress
                   firing, so there is no shot to price. Running dry now
                   switches the ring OFF and plays the game's own
                   out-of-resource cue. ""fire"" mode is unaffected.

orbitAppearFade    seconds the orbs spend FADING IN as the ring comes
                   up. DEFAULT 0 (instant) - unlike the release fade,
                   because turning it on for everyone would change the
                   feel of rings already tuned. 0.3-0.6 reads well.
orbitAppearGrow    default true - swell them out of nothing as they
                   fade in, so the ring BLOOMS rather than brightening.
orbitAppearDamage  default FALSE, and the one that actually matters.
                   With this off and orbitReleaseDamage off, a tap
                   costs you the whole wind-up AND the whole wind-down,
                   so holding is strictly better than tapping. THIS IS
                   HOW YOU STOP A RING BEING SPAMMABLE - ""warmupTime""
                   cannot, because a held ring never goes through the
                   ship's fire path at all.

orbitReleaseFade   seconds the orbs keep orbiting while fading out
                   after the ring switches off. Default 0.3; 0 = the
                   old instant pop. They keep spinning, pulsing and
                   spiralling exactly as they were while they go, and
                   it covers a ""fire"" ring reaching the end of its
                   orbitFireDuration too.
orbitReleaseShrink default true - shrink them away as they fade, so a
                   big charged ring visibly COLLAPSES instead of just
                   dimming.
orbitReleaseDamage default FALSE. Off, a dissolving ring stops
                   damaging, pushing, blocking shots and digging - it
                   is being dismissed. On, releasing becomes an extra
                   sweep, which makes tap-release-tap strictly better
                   than holding: a legitimate design for a ""burst""
                   ring, an exploit if you did not mean it.

  A ring that blooms and dissolves gently:
      ""orbitAppearFade"": 0.5, ""orbitReleaseFade"": 0.5,
      ""orbitAppearGrow"": true, ""orbitReleaseShrink"": true

  A wind-up ring:      ""orbitGrow"": { ""from"": 0.3, ""to"": 4,
                                     ""span"": 3, ""curve"": 2,
                                     ""damageAtFull"": 2 }
  A throbbing ring:    ""orbitGrow"": { ""from"": 0.8, ""to"": 1.8,
                                     ""over"": ""breathe"",
                                     ""breatheSpeed"": 1.5 }

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

PHYSICS WEAPON FIELDS  (""usePhysics"": true - the lobbed / grenade style)
------------------------------------------------------------------------
projectileSpeed    projectileSpeedVariance    physicsProjectilePrefab

A lobbed shot is a PhysicsProjectile - a DIFFERENT class from a normal
bullet, with a real Rigidbody2D and a real collider. Note the prefab
field is ""physicsProjectilePrefab""; a lobbed weapon's plain
""projectilePrefab"" is null, which is worth knowing because it is what
made these behaviours look broken rather than unsupported.

WHAT WORKS ON A LOBBED SHOT
  grow            yes. Simpler here, in fact: the collider scales with
                  the art, so the hitbox follows for free (which makes
                  ""hitbox"": false meaningless - it is ignored).
  homingData      yes, and NATIVELY - the game steers physics shots
                  itself. Set ""homingData"": { ""enabled"": true,
                  ""torque"": ..., ""maxSpeed"": ... } in the weapon
                  block. The mod's own ""homing"" block is for plain
                  bullets, which ignore homingData entirely.
  explosion,      yes - those are impact behaviours, not flight paths.
  burn, discharge
  projectileSprite, yes.
  trail, scale

  split           yes. A grenade splits into grenades, still arcing
                  under gravity. Remember a lobbed shot ignores
                  rangeData entirely, so its ""death"" trigger is
                  lifetime or impact.

WHAT DOES NOT (yet)
  wave, boomerang, ricochet, deflect, turret, pierceLimit
                  These all steer or re-aim a shot in flight by hooking
                  Projectile, and a PhysicsProjectile is not that class.
                  The log now says exactly this instead of claiming the
                  weapon isn't a projectile weapon.

grow is also IGNORED ON AN ORBIT WEAPON, whatever the shot type: an
orbiting orb is only artwork (its projectile component is switched off
and the ring does damage with its own radius check). Use ""orbitPulse"" /
""orbitSpinUp"" and ""orbitHitRadius"" to change a ring's size instead.

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
