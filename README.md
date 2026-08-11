# Weapon Forge

**Build custom weapons for PUNK by writing small JSON files — no C#, no compiling, no Harmony patches.**

Weapon Forge clones a real in-game weapon as a starting point, then applies whatever stats you listed in your JSON on top. Everything you *don't* mention keeps the template's value, so a five-line file is a perfectly valid weapon.

Built against **PUNK Playtest v0.12.9**.

---

## Requirements

- **BepInEx** — see [PunkMods](https://github.com/Osanchez/PunkMods) for a good walkthrough of setting up mods for PUNK.

> **Contributing?** [BUILDING.md](BUILDING.md) gets you compiling — you point one property at your own PUNK install and nothing else needs touching. [CLAUDE.md](CLAUDE.md) is the orientation doc: the engine findings this mod is built on, the code conventions, and the traps that cost real time. Worth reading before your first change, whether you're working by hand or with an AI assistant.

## Install

1. Copy `WeaponForge.dll` into `...\PUNK Playtest\BepInEx\plugins\` (its own folder is fine).
2. Run the game once, then close it.
3. A **`weapons`** folder now sits next to the DLL, containing `README.txt` and several example weapons. A **`sprites`** folder appears too — that's where your own art goes.
4. Drop your own `.json` files in there and restart.

Your weapons appear as extra choices at the end of the starting-loadout list (past all the question marks if you haven't unlocked much).

> Files are read at **startup**. There's no hot reload — restart the game to see changes.
>
> Errors never crash the game. A bad file is skipped and the reason is written to `BepInEx\LogOutput.log` — search for `WeaponForge`.

---

## The Builder (recommended)

Open **`Weapon Builder.html`** in any browser. It's a single self-contained page — no install, no internet.

- Every field is documented inline, with the game's own stock values as reference points
- Only shows the fields that actually apply to the weapon type you picked
- Ready-made presets: White Tesla, Orbit Ring, Triple Ring, Sentry Disc, Wave Beam, Spiral Vortex
- **LOAD .JSON** to edit an existing weapon, **SAVE .JSON FILE** when you're done

There's also **`HOW TO MAKE WEAPONS.txt`** — the full written reference, including a per-type breakdown of what applies and what silently does nothing.

---

## What you can make

### Weapon types
All four of the game's weapon types are supported, and the builder knows which fields each one really honours:

| Type | Examples | Notes |
|---|---|---|
| **Projectile** | White Popper, Bolt, Shotgun, Worm | the full option set — and the one to start from |
| **Lobbed / physics** | White Derbis, Purple Flack, Caps Loon | arcs, tumbles, bounces (`usePhysics`) |
| **Missile** | White Dart, Purple/Caps/Electron Rocket | homing — **requires `usePhysics`** |
| **Hitscan / laser** | Caps Laser, Crawler Laser | instant beam; can also **terraform** |
| **Minion spawner** | the 5 raw minion weapons | spawns a real pet unit |

### Slots & availability
- Equip to **primary**, **secondary**, or **gadget1/2/3** (the 1/2/3 keys)
- `source`: `starter`, `loot`, `starterAndLoot`, or **`none`** (built and referenceable but never offered — for helper weapons)
- Optional in-run **shop** entry with price and unlock tier
- `target`: point a weapon at enemies or at the player — this also *fixes* enemy weapon templates so you can use them

### Visuals & feel
Recolor projectiles and beams (hex, HTML names, game colors, or animated **rainbow**), rescale them, or swap in any other weapon's ammo with `projectilePrefab`.

**Your own art.** Drop a PNG in the `sprites` folder and point a weapon at it with `projectileSprite`. **`Sprite Sheet Builder.html`** (beside the weapon builder) is a visual slicer for this: open a sheet, cut sprites out by dragging, auto-detecting islands, or grid-slicing, and it writes the manifest the mod reads. It previews every sprite at true in-game size next to the stock Popper bullet, because the game runs at 20 pixels per world unit and stock art is only ~7×7 px.

**Animated projectiles**, which the base game has no equivalent of — every stock ammo prefab is a single static sprite. Define a frame order, fps, and loop / once / ping-pong, and point `projectileSprite` at the animation's name instead of a sprite's. There's a *random start frame* option so a ten-pellet shotgun looks like ten spinning bullets rather than one strobe. Drop a **GIF** into the builder and it converts it to a PNG sheet (the engine can't read GIF), slices the frames, and builds the animation for you.

**Your own beam art**, via the `beam` block on a hitscan weapon: the firing beam, the charge-up beam, the hit spark, thickness, and whether the beam **stretches** one sprite to length (the stock look) or **repeats** a seamless one along it — which is how you get a beam built from visible segments rather than a smooth gradient. One trap is handled silently: every stock beam sprite is authored with a **left-edge pivot**, because the quad grows rightward from the muzzle, whereas the slicer writes a centred pivot — so sprites are rebuilt with the right pivot at load and a single sprite can serve as both a bullet and a beam. Note a beam is one stretched quad, so it can't bend and can't flipbook-animate; the hit spark is a particle system and *can*.

**Trails**, made of your own sprites. There are no ribbon trails anywhere in PUNK — not one `TrailRenderer`, and no prefab enables Unity's Trails module. Every trail in the game is a particle system riding along with the shot, dropping sprite puffs *per unit travelled*, which is precisely why your imported art can be the puffs. Only four ammo prefabs ship a trail, so `trail` copies the Popper's onto any projectile and then applies your settings: puff sprite, density, length, taper, fade, drift, draw order. Point it at an animation and every individual puff plays the whole flipbook over its own lifetime. `"trail": true` gets you the stock look, `false` strips a trail off, and a trail with its own `color` is left alone by `projectileColor` so a white bullet can trail orange.

**Your own sound effects.** Drop a `.wav` (or `.ogg` / `.mp3` / `.aiff`) into a `sounds` folder and put its file name in any of the weapon's seven sound slots — `shootSfx`, `reloadSfx`, `startSfx`, `continousShootSfx`, `releaseSfx`, `warmupSfx`, or `explosion.sfx`. The game itself can't load audio files (it ships pre-baked clips), so the mod decodes yours — WAV synchronously via its own RIFF reader, the compressed formats through the engine — and then **registers the clip as a genuine `Sfx` in the game's own audio database**. That last part is what matters: 3D positioning, the in-game volume slider, the mixer, voice pooling and repeat-throttling all work on it, because it isn't a bolt-on. Settings you don't specify are inherited from the sound you replaced, and the two *held* slots are forced to loop since the game stops those by handle. An optional same-named `.json` sets `volume` (usually needed — exported audio runs hot), `repeatMinDelay`, and `variants` for random per-shot selection like the game's own gunshots use.

You can also borrow any stock weapon's **sound and feel**: firing/reload/loop sounds, screen-shake and gamepad-rumble presets, muzzle and reload particles, and the impact / out-of-range / end-of-life effect prefabs. The builder ships pickers for all of them, so you're choosing from what the game actually has rather than guessing names.

The **muzzle flash** can be recoloured too (`muzzleColor`, including animated rainbow). It's tinted by multiplying the flash's own gradient, so the flicker and fade survive instead of flattening into a blob — and it's done on a private copy, so recolouring your weapon never recolours the 53 stock weapons that share the same flash prefab.

**Loot targeting.** A loot-enabled weapon can be restricted to specific crates with `lootFrom` — `white`, `caps`, `purple`, `tech`, or the Queen's own pool — because the game already splits its module pools by element. Worth knowing: the **Tech** pool holds only 5 entries against White's 18, so that's where a custom weapon has by far the best odds. Four enemies share those same pools (Crawler → tech, CrossJock and CrossRed Bomber → white, the Queen drops three at once), so targeting a pool targets those enemies too. `lootRepeat` overrides the game's own no-duplicates rule — every module zeroes its own drop weight once you own one — either fully or by a factor so duplicates just get rarer.

### Custom behaviours
These are Weapon Forge additions, not stock game features:

- **Orbit weapons** — projectiles that circle your ship. Passive / hold / toggle / fire modes, contact damage, enemy-bullet blocking, terrain digging, push, pulse, spin-up, destructible orbs with regeneration, and **multiple concentric rings** (spacing, stagger, counter-rotation, per-ring speed)
- **Spiral orbit** — orbs spiral outward and either sling off toward enemies or sweep like a sprinkler
- **Wave motion** — a clean repeating sine "S" (Super Metroid wave beam), with synced and double-helix variants
- **Bullet deflector** — the mod's first *defensive* weapon: a shot that clears enemy fire out of the air as it travels, or **turns it around and sends it back as yours** (it damages enemies, can't hit you, and the kills are credited to you). Aim the return straight back or at the nearest enemy. Worth knowing why this needs mod support at all: PUNK projectiles resolve their own collisions by raycast, so their colliders are *disabled* — enemy bullets are invisible to any physics query, and the mod keeps its own register of them instead
- **Growing / shrinking shots** — a pinprick that arrives as a wrecking ball, or a slug that tapers away. The hitbox grows with the art for free: the game rebuilds a projectile's collision circle from its `Radius` every frame and that value is writable, so what you see and what hits are the same number. Growth by distance or time, an easing curve, an optional damage ramp, and a visual-only mode
- **Boomerang** — a shot that flies out, turns around and comes back, hitting things on both legs. Leans on something the game already does: a weapon with `rangeData.slowDown` *decelerates* to a dead stop at its range (the DiscGun ships exactly that), so the pivot is free and looks natural rather than a snap turn. Return either **curves home** to wherever you are now, or **retraces the outbound path** via breadcrumbs — so a wave or homing throw curves on the way back too. Optional turn-on-walls, return-speed and damage multipliers, cost refund on catch, or a looping yo-yo
- **Ricochet** — the game can already bounce shots off a layer mask (and applies damage *before* the bounce, so a ricochet off an enemy still hurts it), but it has no concept of a bounce **count**, so a stock bouncer bounces forever. This adds the limit (a number or infinite), terrain / enemies / both / none targeting, per-bounce speed and damage falloff, angle scatter — which is also the practical escape for a shot trapped between two parallel walls — and an optional **seek** that redirects each bounce at the nearest enemy instead of mirroring off the surface, so a ricochet reliably hits something else. Runs the weapon's own impact behaviour on the final bounce, so it explodes if you set it to
- **Homing for plain bullets** — the game only wires its own `homingData` to *lobbed* shots, so a fast straight bullet can never home with stock data. This steers a plain projectile by rotating its velocity, which curves **the hitbox with it** because the collision sweep is rebuilt from that velocity every frame. Turn rate, acquisition range and cone, a straight-flight delay, a total-turn budget, target prediction, and retargeting when something dies. Pair it with a high fire rate and a long segment sprite for a curving beam made of shots
- **Deployable turret / mine** — a shot that glides to a stop and then keeps firing another weapon while it lives, sweeping in a circle or auto-targeting the nearest enemy, damaging what touches it
- **Phasing** — shots pass through terrain but still hit enemies
- **Pierce cap** — pierce *N* enemies then vanish, with optional damage falloff
- **Chain lightning** — turn any weapon into a Tesla-style arc, with configurable lightning colour (including RGB) and reach
- **Burn control** — burn tick rate and flame colour, optionally recolouring burning terrain too
- **Sub-emitters** — fire a *whole other weapon* when a shot expires or hits, chainable as deep as you like

### Module card
Customise the grid module your weapon lives on: icon, colour, description, power-core capacity, and which resource equipping it grants.

---

## Notes & known limitations

- **Save/continue is safe.** Weapons are registered at startup, so you can save mid-run and use Continue. If you *rename or delete* a weapon file, an old save that still references it will lose that weapon — keep the file around while any save needs it.
- **Not every option applies to every type.** Wave, phasing, pierce cap and turret need a straight-flying projectile; homing needs `usePhysics`; a hitscan beam ignores everything projectile-shaped. The builder and the txt reference spell this out per type.
- **The drone gadgets** (`Module Active Drone …`) are a different module type that holds a *unit* instead of a weapon, so they can't be used as a template. Use a minion-spawner weapon and set `minionPrefab` instead.
- **A pet's own stats** (its damage, health, weapon) live on its unit prefab and aren't editable from JSON.
- `Resource Money` can't power a weapon — the mod reverts it with a warning rather than letting the game hang.

Bugs are likely; this moves fast. Log output is verbose on purpose, so start there.

---

## Companion mod

**Module Forge** (separate repo — https://github.com/Sugarheady/ModuleForge) does the same thing for grid *modules*. The two cooperate automatically when both are installed — they share one burn engine, add their pierce caps together into a single number, and merge their stat-card lines instead of double-reporting.

---

