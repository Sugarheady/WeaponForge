# Weapon Forge

**Build custom weapons for PUNK by writing small JSON files — no C#, no compiling, no Harmony patches.**

Weapon Forge clones a real in-game weapon as a starting point, then applies whatever stats you listed in your JSON on top. Everything you *don't* mention keeps the template's value, so a five-line file is a perfectly valid weapon.

Built against **PUNK Playtest v0.12.9**.

---

## Requirements

- **BepInEx** — see [PunkMods](https://github.com/Osanchez/PunkMods) for a good walkthrough of setting up mods for PUNK.

> **Contributing?** [BUILDING.md](BUILDING.md) gets you compiling — you point one property at your own PUNK install and nothing else needs touching. [CLAUDE.md](CLAUDE.md) is the orientation doc: the engine findings this mod is built on, the code conventions, and the traps that cost real time. Worth reading before your first change, whether you're working by hand or with an AI assistant. The design write-ups for the bigger features (wingmen, drone modes, the bloom spiral) and the research notes are in [Docs/](Docs/).

## Install

1. Copy `WeaponForge.dll` into `...\PUNK Playtest\BepInEx\plugins\` (its own folder is fine).
2. Run the game once, then close it.
3. A **`weapons`** folder now sits next to the DLL, containing `README.txt` and several example weapons. A **`sprites`** folder appears too — that's where your own art goes.
4. Drop your own `.json` files in there and restart.

Your weapons appear as extra choices at the end of the starting-loadout list (past all the question marks if you haven't unlocked much).

> Files are read at **startup**. There's no hot reload — restart the game to see changes.
>
> Errors never crash the game. A bad file is skipped and the reason is written to the mod's own log, `BepInEx\WeaponForge.log` (the launch before is `WeaponForge.prev.log`). `LogOutput.log` stays readable for BepInEx and other mods: it gets only Weapon Forge's load lines and a copy of every error. Set `[Logging] OwnLogFile = false` to put everything back in `LogOutput.log`.

---

## The Builder (recommended)

Open **`Weapon Builder.html`** in any browser. It's a single self-contained page — no install, no internet.

- Every field is documented inline, with the game's own stock values as reference points
- Only shows the fields that actually apply to the weapon type you picked
- Ready-made **presets** that fill the whole form in one click, covering most of the blocks below
- A live **filter** and a generated **index** over ~19 sections, because the page reached the size where "it is all collapsed by default" stopped being enough — a block you cannot name is a block you cannot find
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
- **Loadouts** — a separate small file in a `loadouts` folder (beside `weapons`) that starts a run with up to **five** things equipped: a gun on each mouse button and a gadget on each key. Slots take your own weapons by name, stock modules, or the name on the in-game card, and a loot-only weapon can be named too. The game's own loadout card already had all five slots; no stock card ever filled more than one. **`Loadout Builder.html`** (beside the weapon builder) writes the file and checks each slot against the game's weapon/gadget rule

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
These are Weapon Forge additions, not stock game features. Each is a named block in your JSON, is
documented inline in the builder, and warns at startup if it cannot apply to the weapon type you put
it on.

**How the shot moves**
- **Orbit weapons** — projectiles that circle your ship. Passive / hold / toggle / fire modes, contact damage, enemy-bullet blocking, terrain digging, push, pulse, spin-up, destructible orbs with regeneration, and **multiple concentric rings** (spacing, stagger, counter-rotation, per-ring speed). **Spiral orbit** sends the orbs outward to sling off at enemies or sweep like a sprinkler
- **Wave motion** — a clean repeating sine "S" (Super Metroid wave beam), with synced and double-helix variants, plus a separate **wobble** for jitter
- **Homing for plain bullets** — the game wires its own `homingData` only to *lobbed* shots, so a fast straight bullet can never home with stock data. This steers a plain projectile by rotating its heading, which curves **the hitbox with it**, because the collision sweep is rebuilt from that heading every frame
- **Guided** — fly the shot yourself with the crosshair, for the whole flight or only while you hold. On a rocket it is the engine's own torque-and-thrust loop with your cursor as its target, so gravity, damping and thrust all keep working
- **Boomerang** — flies out, turns around and comes back, hitting things on both legs. The return either curves home to wherever you are now or **retraces the outbound path** via breadcrumbs, so a waving throw waves on the way back too
- **Ricochet** with a real bounce **count** (the game has no concept of one, so a stock bouncer bounces forever), plus per-bounce falloff, scatter, and an optional **seek** that redirects each bounce at the nearest enemy. **Bounce** is the physics-shot equivalent, including a super-elastic mode that converges on a steady bouncing height rather than climbing out of the level
- **Wall riding** — shots that catch a wall and follow the terrain around it
- **Converge / spazer** — aim a whole volley at one point, or launch it abreast and keep it parallel
- **Rotating spiral** — the volley steps around between shots, as a full rotation or a windscreen-wiper sweep
- **Speed ramp**, **damage ramp** and **growing / shrinking shots** — by distance or by time, with easing. The hitbox grows with the art for free, because the game rebuilds a projectile's collision circle from its writable `Radius` every frame
- **Phasing** through terrain, **sticky** mines, **tether**, **gravity wells** (which can also swallow enemy fire), and **fade** in and out

**What happens on impact**
- **Pierce cap** — pierce *N* enemies then vanish, with optional damage falloff. Worth knowing that piercing is not a flag anywhere in this game; it is a cast per target that the mod does itself
- **Split** — a shot bursts into fragments on death, on a timer, or on demand, inheriting the parent's upgraded properties
- **Chain lightning** with configurable colour (including RGB) and reach — and **chain jumps**, where the real projectile is redirected from enemy to enemy
- **Crit**, **leech** and **execute** — a chance to double, a share of the damage dealt returned as health, and a finisher below a health threshold. All three measure the victim's own tank rather than hooking a projectile, so they work on every weapon type including beams and explosions
- **Slam** — mark a unit as slammable and hurt it by how hard it is thrown into something: by your push, a gravity well, a blast, or another gun entirely
- **Slow**, **stun** and **time-slow**. Stun is a real freeze rather than a heavy slow, because a slow can never reach zero — it blocks the gun and suspends the AI's own steering
- **Adaptive damage type** — each hit switches to whichever element the victim takes worst
- **On kill** — grant resource, cool the gun, or start a stacking buff, and drop the reward as a real pickup if you want it earned rather than given
- **Charm** — turn an enemy to your side for a while, with optional infighting between converts. Its faction, its eyes, its bullets, its headbutt and its grudge against you are all restored when it wears off

**Sustained and defensive weapons**
- **Overheat / jam** — heat per shot *or per second*, then jam, vent, slow down, or fire a blowout shot
- **Spin-up** — hold the trigger and the gun gets better: fire rate, accuracy, damage, pellets or cost, with a grace period so a short dodge does not cost you the wind-up
- **Charge** — hold to size a shot up, in release or auto mode, with an on-screen meter
- **Bullet deflector and rally** — a shot that clears enemy fire out of the air, or **turns it around and sends it back as yours**, compounding damage per reflect. Why this needs mod support at all: PUNK projectiles resolve their own collisions by raycast, so their colliders are *disabled* — enemy bullets are invisible to every physics query, and the mod keeps its own register of them instead
- **Shield arc** — a reflecting wedge in front of you, or a dash-driven ram
- **Tractor beam** — pull, push or hold what it catches, and optionally slam it
- **Swipe** — a real melee blade with an arc, a reach and generated crescent art, which can also deflect shots
- **Lunge** — wind up, then launch at the cursor and deal contact damage on arrival
- **Contact damage** — for your ship, for what a weapon has touched, or as a world rule
- **Curve beam** — a beam that locks on and bends around terrain, threading several targets. A stock beam is one stretched quad, so this replaces it with a chain of short ones

**Things that fight for you**
- **Deployable turret / mine** — a shot that glides to a stop and then keeps firing another weapon for as long as it lives
- **Minion spawners and drone gadgets** — real pet units, with their side, cap (and cap-per-module-level), lifetime, colour, scale, vision, blast safety, their own weapon, and a **hold-ground** or **escort** stance. `inheritGrid` gives every spawned minion a copy of the weapon's other grid modules, which nothing in the game does for a spawner
- **Catalyst nodes** — a node that parks where it lands and transforms the next shot that reaches it: chain it, split it, boost it, or reflect it
- **Gas clouds** — lingering clouds that damage, burn, slow or set terrain alight, laid as a trail or on impact
- **Sub-emitters** — fire a *whole other weapon* when a shot expires or hits, chainable as deep as you like
- **Drone modes** — one key steps every companion (wingmen, Forge drones and the game's own five drone gadgets) through orbit, drag, protector, rear guard, seeker and sentry, with an opt-in interceptor that takes shots aimed at you. Hold the key and press a gadget's button to command that gadget alone. The keys and the cycle live in the mod's config; a `modes` block tunes one gadget

### Module card
Customise the grid module your weapon lives on: icon, colour, description, power-core capacity, and which resource equipping it grants.

---

## Notes & known limitations

- **Save/continue is safe.** Weapons are registered at startup, so you can save mid-run and use Continue. If you *rename or delete* a weapon file, an old save that still references it will lose that weapon — keep the file around while any save needs it.
- **Not every option applies to every type.** Wave, phasing, pierce cap and turret need a straight-flying projectile; homing needs `usePhysics`; a hitscan beam ignores everything projectile-shaped. The builder and the txt reference spell this out per type.
- **Drone gadgets are supported**, through the `drone` block. They are a different module type — one that holds a *unit* rather than a weapon — so they are cloned from a stock gadget rather than built from scratch; three unguarded dereferences in the game's own code make that mandatory. A minion-spawner weapon with `minionPrefab` is the other route, and puts pets on the trigger instead of a gadget key.
- **A pet's own stats are editable**, through the `minion` block — health, speed, sight, scale, its weapon, its colour, its blast safety. Health and resource pools have to be applied as module *effects* rather than direct writes, because a unit's health in this game **is** a resource tank whose capacity is rebuilt from module effects on every recalculation, so a direct write is erased within a frame. The mod does that for you.
- `Resource Money` can't power a weapon — the mod reverts it with a warning rather than letting the game hang.

Bugs are likely; this moves fast. Log output is verbose on purpose, so start there.

---

## Companion mod

**Module Forge** (separate repo — https://github.com/Sugarheady/ModuleForge) does the same thing for grid *modules*. The two cooperate automatically when both are installed — they share one burn engine, add their pierce caps together into a single number, and merge their stat-card lines instead of double-reporting.

---

