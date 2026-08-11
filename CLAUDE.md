# Weapon Forge — working notes for Claude (and humans)

Guidance for anyone, human or AI, making changes here. Read it before writing code; most of it is
hard-won and not deducible from the source in this repo.

Target: **PUNK Playtest v0.12.9**, Unity **6000.3.4f1**, .NET Framework **4.7.2**, BepInEx + Harmony.

## What this mod is

A JSON-driven weapon framework. A weapon file names a **stock in-game weapon as a template**, the mod
clones it, then overlays whatever fields the JSON mentions. Anything unmentioned keeps the template's
value — so a five-line file is a valid weapon. On top of that sit behaviours the base game has no
concept of (orbit, wave, boomerang, ricochet, growth, deflect, homing for plain bullets…).

**Design rule that matters:** a bad JSON file must never crash the game. It is skipped, the reason is
logged, and the run continues. Preserve that when adding anything.

## Build & test loop

See **BUILDING.md** — you point one property at your PUNK install; you never edit a `.csproj`.

```bash
msbuild WeaponForge.sln -p:Configuration=Debug
```

**There is no hot reload.** Files are read at startup, so every test is a full game restart. Budget
for that: prefer one restart that tests five things over five restarts.

Logs are verbose on purpose. `BepInEx\LogOutput.log`, search `WeaponForge`.

## Do not commit game code

The findings below came from reading a local decompile of the game's assemblies. **That decompiled
source is not in this repo and must not be added to it** — it's the game's code. Referring to type and
member *names* (as this file does) is what makes collaboration possible; pasting game method bodies in
is not. Keep that line.

## How the code is organised

- Everything mod-owned is prefixed **`Forge`**.
- A behaviour is split in two: a **component/data class** (`ForgeRicochet`) holding the tuned values
  and per-shot state, plus a **Harmony patch class** (`ForgeRicochetPatch`) that hooks the game. Follow
  that split; it keeps patches thin and testable.
- `WeaponBuilder` has one **`Apply<Feature>(weapon, json, fileName)`** per feature, all called from a
  single place. Add features the same way.
- `JsonFieldMapper` does the generic JSON→field mapping; only hand-write a setter when the field needs
  interpretation (aliases, enums, colour names, sound names).
- Anything user-facing needs three updates in the *same* change: the C#, the `Weapon Builder.html`
  block, **and** `HOW TO MAKE WEAPONS.txt`.

## Engine facts that will cost you days if you don't know them

### Projectiles — the one load-bearing fact

`Projectile.Velocity` is a public get/set, and `FixedUpdate` **rebuilds the `Physics2D.CircleCast`
from it every frame**. So steering the velocity curves *the damage volume*, not just the sprite. Wave,
homing, ricochet, boomerang and deflect all rest on this single property. It is why those features
were cheap.

Also public and writable: `Radius` (so writing it grows the real hitbox — art is just
`transform.localScale`), `Damage`, `Owner`, `RangeData`, `PiercingData`, `ImpactBehaviour`, and
`Reflect(normal)`.

- **`collisionLayerMask` is private and typed `LayerMask`, not `int`.** Harmony's
  `FieldRefAccess<T,F>` throws on a value-type mismatch — use a plain `FieldInfo`. This silently broke
  phasing once.
- **A projectile's GameObject layer *is* its faction**, and its collision mask is baked from that
  layer at fire time. So turning an enemy bullet friendly takes **three** changes, not one: reverse
  velocity, remap the layer, *and* rewrite that private mask. Miss the mask and the bullet either
  passes through everything or comes back and kills you. Set `Owner` too, or friendly-fire checks fail
  and kills aren't credited.
- **Enemy projectiles have their `Collider2D` disabled** — they resolve collisions by raycast, so they
  are invisible to `Physics2D.OverlapCircle` at any radius. `ForgeProjectileTracker` exists solely
  because of this: it registers each enemy shot at fire time so we can find them. Set
  `ForgeProjectileTracker.Enabled = true` from the builder for any feature that needs them.
- **Stock `homingData` only works on physics/lobbed shots.** It is a no-op on a plain projectile —
  hence `ForgeHoming` steering velocity manually.
- `rangeData.slowDown` decelerates a shot to a dead stop exactly at its range (the DiscGun ships it),
  which is what makes the boomerang pivot free. While `RangeData.enabled` is true the game rewrites
  Velocity every frame, so a mid-flight turn has to switch the projectile's *own* RangeData off.
- Re-hitting the same target is governed by per-target `PiercingData.damageRepeatDelay`. Use a real
  delay (~0.3s) or a huge one to disable — **never 0**, which re-damages every frame while overlapping.
- **Gadget weapons bypass `Shooter` entirely.** Anything hooked onto Shooter (e.g. orbit) needs the
  gadget path handled separately.

> **If your feature steers a shot, write `idealDirection` — not just `Velocity`.**
> `Projectile.idealDirection` (private) is the game's *base heading*. Everything that curves a
> projectile — our wave motion, the game's own noise wobble — rebuilds `Velocity` every frame by
> rotating that field, and never by reading `Velocity` back. So a feature that writes only `Velocity`
> gets silently overwritten by any curve feature sharing the shot. That is exactly how a boomerang
> with `wave` enabled flew off along its *original* heading while still visibly waving. Two features
> both writing `Velocity` also can't be ordered reliably, since they're both `FixedUpdate` prefixes.
> Publishing the intended heading to `idealDirection` makes them compose instead of fight, and it is
> order-independent. `ForgeBoomerangPatch.SetHeading` is the reference implementation. For the same
> reason, steer from `idealDirection` rather than from `Velocity` — rotating from `Velocity` folds
> another feature's offset back in each frame and widens into a spiral.

### Sprites

- The game runs at **20 pixels per world unit**, and stock projectile art is only about **7×7 px**.
  Imported art looks enormous until you internalise that.
- **Module icons are the exception: 24×24 at PPU 40.** And an icon is drawn by *two* renderers that
  disagree — the card on the grid is a UI `Image` (72×72, Simple, `preserveAspect` off) which stretches
  art and ignores PPU, while the ground pickup is a world `SpriteRenderer` where PPU is the only thing
  setting the size. So art imported at the 20 default is double size on the ground and perfect on the
  card. `ForgeSpriteLibrary.ResolveIconSprite` rebuilds to the stock footprint rather than making
  people know this. Icons are also **tinted by `module.color`** in both places.
- Stock art lives in one **1024×2048 atlas**. Sprites packed tightly in an atlas can't be reused as
  beams (neighbouring pixels bleed in) — `ForgeBeam` refuses them rather than rendering garbage.
- Particles receive sprites via **TextureSheetAnimation in Sprites mode**, *not* a SpriteRenderer.

### Beams (hitscan)

- A beam is **ONE stretched quad**. `UpdateVisual(start, end, normal)` sets `size.x` to the distance.
  So a beam cannot bend and cannot flipbook-animate. The hit spark is a particle system and can.
- **The damage raycast is separate from the visual raycast.** A bending beam would have to patch both.
- Every stock beam sprite is authored with a **left-edge pivot (0, 0.5)** because the quad grows
  rightward from the muzzle. Slicers write a centred pivot, so sprites are rebuilt at load.
- The fire renderer is **Sliced**, the warmup is **Tiled** — and **Tiled fills size in *both* axes**,
  which is what produces the "two beams stacked" look if you pick repeat mode for the wrong slot.

### Trails

- **There is no `TrailRenderer` anywhere in PUNK**, and no prefab enables Unity's Trails module. Every
  trail is a child **ParticleSystem** in **World** simulation space emitting by `rateOverDistance`.
- Only four ammo prefabs ship one, so `trail` clones the Popper's onto whatever you asked for.
- **A fresh `AddComponent<ParticleSystem>()` gets the wrong default material** — always clone a stock
  donor instead.
- Trails use `scalingMode: Local`, so they do **not** scale with a growing shot.

### Audio

- **Not FMOD.** Plain Unity `AudioClip` / `AudioSource`. `AudioDatabase.sfxs` is a public
  `List<Sfx>` and `AudioManager.PlaySfx(guid)` linear-scans it, so a custom sound is registered as a
  genuine `Sfx` rather than played out-of-band. That's what gets 3D position, the volume slider, the
  mixer and voice pooling for free.
- **Inherit `mixerGroup` from the sound you replaced**, or the clip bypasses the in-game volume slider.
- The two *held* slots must be forced to loop, because the game stops them by handle.

### Loot

`LootDropper` → `DropTable` → `DropTableWeightedGroup`. Only **8 of 64** tables can drop a module, and
there are **5 live pools**. `useGroup` stays serialized even when false, so **many tables look live but
are inert** — check it before believing a table matters. The **Tech** pool holds ~5 entries against
White's ~18, so that's where a custom weapon has the best odds. Green crates always drop one
`Module BoosterCore` as a *fixed* item, not a roll.

### Modules & levels

`FloatSeries.GetElement(i)` is the formula `baseValue + change * i`, not an array lookup — a series
with `change: 0` can never respond to levelling. Level boosting works by pixel-mask
`LevelModificationField` grids adding +1 per overlap, gated on `canBeBoosted`. Power-core shapes are
parsed from **sprite pixels**, and width and height must both be **odd**.

## Platform gotchas (.NET 4.7.2 against a Unity 6 game)

`Gradient.SetKeys`, `Texture2D.LoadImage` and `AudioClip.SetData` all bind to the `ReadOnlySpan<T>`
overloads, which this target framework can't satisfy. Use the array-taking setters, or resolve the
method by reflection. Expect the same class of problem from any new Unity API you reach for.

## `Weapon Builder.html` conventions

One self-contained page, no build step, no network. Fields carry a **`data-path`** and are folded into
nested JSON by `deepSet`.

> **A `data-path` that is a prefix of another one throws**, and because the exception escapes
> `build()`, the page silently emits *nothing at all* and drops every field after it. A `homing` bool
> alongside `homing.turnRate` caused exactly this. Use `homing.enabled` instead — and sweep all paths
> for prefix collisions after any change.

Round-trip matters: anything folded in `build()` must be unfolded in `loadObject()`, or LOAD .JSON
quietly loses it.

**"Preset" means a one-click entry in this page** that fills the whole form — not an example `.json`.

## Cross-mod rules (Module Forge)

The two mods cooperate but must stay independently buildable and runnable:

- **Find each other by type name only** — `AccessTools.TypeByName("ModuleForge.ModuleForgeBurn")`.
  Never an assembly reference, never `BepInDependency`, never the plugin GUID.
- Exactly one owner per shared mechanic. If Module Forge is present it owns the burn engine and
  Weapon Forge's burn patches early-return; pierce caps are added into one number and stat-card lines
  are merged rather than double-reported.
- If the reflection fails, degrade to doing nothing — never risk two mods fighting over the same patch.

## House rules

- Verify against the decompile before building. Nearly every bug in this project's history was a wrong
  assumption about the engine, not a wrong line of C#.
- Check whether a game field already does the job. `slowDown` gave us the boomerang pivot; `Radius`
  gave us the growing hitbox. Both would have been ugly hand-rolled code.
- Assume any class you hand a struct/class instance to keeps the reference. `ProjectileBounceData` is
  a **class** — assign a fresh instance or you mutate every stock weapon that shares it.
