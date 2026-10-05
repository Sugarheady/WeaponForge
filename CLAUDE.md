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

## Harness — the checks that run without the game

`Harness/` holds every non-game check for this repo. `python Harness/run.py` runs them all and prints
one total; `npm install` once, inside that folder, because several load `Weapon Builder.html` in jsdom
(the page is far too big for a browser preview tool).

**They live here as of 2026-09-19, and the move is the lesson.** They used to live in a temporary
session scratchpad. Rebuilding `gadgettest.py` found that the file was **gone** and that the set it
existed to pin had drifted by two in the meantime. Harvesting the rest found the same thing at scale:
`steertest.py`, `teardowntest.py`, `navtest.js` and `carryaudit.py` were all gone, while this file
went on describing each of them in the present tense as pinning something. `applyordertest.py`
survived and had drifted by **four** entries.

> **A pin that does not run is a comment.**

**All four were rebuilt the same day** — from what this file says they guarded, then checked against
the code rather than against the prose. What that turned up is worth keeping, because in three cases
out of four the HARNESS was wrong and the code was right:

- **`steertest.py`** — the `Velocity` census is 26 writes in six kinds (FREEZE / SPEED / BIRTH /
  STEER / OWNER / OTHER; 26th: `ForgeSteer.Drive`, 2026-10-02), because **only DIRECTION is
  contested**. A first version reported a latch
  bug in `ForgeSteer.Ensure` by searching the whole file for `AccessTools` and finding the header
  comment. **An assertion that reads prose is not reading the code** — it is scoped to the method now.
- **`teardowntest.py`** — the "every teardown is actually CALLED" sweep initially reported *fifteen*
  false failures, because it attributed each method to the nearest class declared above it, which is
  the nested `Config` rather than the outer static class. It walks brace depth now. The two genuinely
  uncalled ones are both excluded with a reason: `ForgeSlow.ClearTime` is deliberately unwired, and
  **`ForgeMinionSolid.Reset` is redundant rather than missing** — its gate is a `HashSet` named
  `_said`, which `ForgeDiagnosticGates` already sweeps by convention.
- **`navtest.js`** — the load-bearing assertion is that the emitted JSON is byte-identical before,
  during and after filtering. **The search is debounced by 120 ms and the test has to respect that**:
  typing and asserting in the same tick made a correct jump look broken.
- **`carryaudit.py`** — matches carried titles by WORD OVERLAP, because a rewording is not a drop.
  The `RETIRED THIS ROUND` entry is excluded by name: its text is *supposed* to differ every round,
  so comparing it reports a drop on a round that did nothing wrong.

> **A harness written from a description is a hypothesis.** Every one of these had to be run against
> the real source before it was worth anything, and three of the four failed first against code that
> was correct.

### The three that had gone stale, and what a stale one actually costs

Three harnesses were carrying expectations from before features that had since shipped. Fixed
2026-09-19, and none of the three was the simple bump it looked like:

- **`barricadetest.js` → RETIRED to `closed/`.** Its subject was not broken, it was *replaced*:
  Barricade was retuned into **Excavator** on his R17 test 228 report, and every axis inverted
  (lobbed→straight, secondary→primary, `"soft"`→`"all"`, Obsidian→Tuff). Six of eleven assertions
  failed correctly. But two still PASSED and were worth keeping — the `"soft"` keyword case and
  `CellsSoft` holding the three Fogs — and they were only ever reachable because one shipped preset
  happened to use that word. **A harness failing for six unrelated reasons reads as "that old test is
  stale", so nobody reads the assertions that still pass.** They now live in `cellwordstest.js`,
  which asserts the page↔C#↔how-to *vocabulary* directly, so no preset has to use a word for that
  word to stay guarded.
- **`seealsotest.js`** — asserted `callouts.length === 3` and "each feature has exactly ONE". Both
  were facts about the page on the day it was written, not promises: `surge` and `rank` shipped with
  callouts, and `spinUp` legitimately carries **two** doing different jobs. It asserts by NAME now,
  the total may only grow, and the durable form of his original ask is read from the C#: **every
  `ForgeGauge.Source` must point at the readout section**, so a fifth source fails until it is
  signposted. It also clicked `bubble` links by index `[2]` under the comment *"the one in `charge`"* —
  two callouts later that index is spinUp's, so **the assertion passed while testing something other
  than what its name said**, and would have kept passing with charge's link deleted.
- **`gaugetest.py`** — three of its four failures were an exact-match regex reporting an addition as a
  violation. The fourth was not a failure at all, which is the point:

> **★ A green assertion was protecting a bug.** `gaugetest` asserted the builder page contained
> `"heat,charge,spinUp"` and the words `"recommended order"`. Both stayed true while the sentence
> around them rotted. `Source.Surge` was added at the **front** of `DefaultSources`, the how-to was
> rewritten to match, and the page went on recommending *"heat → charge → spin-up"* — omitting the
> source that now leads the list — while the dropdown **directly above that sentence** had already
> been updated to offer surge. The control and its own explanation disagreed on one screen, and the
> harness held the stale half true. Same family as `r19live.js` asserting `source == "shop"`, a value
> that was never valid. **Pin the thing that is TRUE, not the substring that happens to survive.**

Three rules the layout enforces rather than states:

- **Everything directly in `Harness/` only asserts. Mutators go in `Harness/edits/`**, which `run.py`
  never walks — because a filename cannot tell you whether a script asserts or mutates, and running
  every `*.py` in a folder once silently duplicated four methods in `WeaponBuilder.cs`, four
  builder-page sections and five how-to blocks.
- **`STALE` is a distinct result from `FAIL`.** A harness whose subject was archived (an old round's
  checklist) is not a code failure, and collapsing the two makes a red total meaningless.
- **`closed/` takes two kinds of retirement** — a round that is over, *and* a subject that was
  deliberately replaced. Before parking one, check what else it was holding: see its README.

Nothing here contains an absolute path — each harness resolves the repo from its own location, since
this repo is public and an absolute path in one names its author.

## Build & test loop

See **BUILDING.md** — you point one property at your PUNK install; you never edit a `.csproj`.

```bash
msbuild WeaponForge.sln -p:Configuration=Debug
```

**There is no hot reload.** Files are read at startup, so every test is a full game restart. Budget
for that: prefer one restart that tests five things over five restarts.

Logs are verbose on purpose. **`BepInEx\WeaponForge.log`** (the launch before: `WeaponForge.prev.log`).
Since 2026-09-25 `LogOutput.log` and the console keep only the plugin's own load lines plus a copy of
every error. See "The log file" below.

### The log file — `ForgeLog`

- **Every log source is `ForgeLog.Source("WeaponForge.X")`, never `Logger.CreateLogSource`.** In
  BepInEx 6.0.0-be.785 `CreateLogSource` is `new ManualLogSource` + `Logger.Sources.Add`, and that
  Add is the only wire to the shared listeners. An unregistered source reaches only `ForgeLog`, which
  writes the file and forwards Error/Fatal. `logfiletest.py` fails on a stray `CreateLogSource`.
- **The plugin's own `Logger` is the one registered source** — its lines (loaded + where the file
  is, BUILD, switched off, patches applied) are what LogOutput.log keeps. Anything else in `Awake`
  goes through `ForgeLog.Source`. `ForgeLog.Start` is the FIRST line of `Awake` so those lines are
  copied into the file as well.
- **It catches the game's own errors that name us.** `WriteUnityLog` defaults false, so BepInEx
  blacklists "Unity Log" from LogOutput.log — an exception thrown out of one of our patches used to
  be in no file at all. `GameErrorCatcher` copies any whose stack trace has a `WeaponForge.` frame
  (3 per error, 50 per launch). The match is a frame START, not a Contains, because the three mods'
  names overlap as text.
- **Every failure falls back to LogOutput.log and says so.** No folder, no file, a full disk, after
  `Close`: the line is forwarded, never dropped.
- **A relaunch overlaps**: the restart row starts the new game before this one quits, so `TryOpen`
  waits up to 2s for the file before deciding the game is running twice (then `WeaponForge.2.log`).
- `logfileprobe.py` compiles the shipped `ForgeLog.cs` against the real `BepInEx.Core.dll` and runs
  seven launch scenarios, one process each. Mutation-tested: eight defects, each caught by its own
  assertion.
- `[Logging] OwnLogFile = false` restores the old behaviour. Module Forge and Game Mode Forge carry
  their own copies (`ModuleForgeLog`, `GameModeForge.ForgeLog`) — duplicated on purpose.

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
- **A lobbed (`usePhysics`) weapon's `projectilePrefab` is NULL** — only `physicsProjectilePrefab` is
  set; the two are mutually exclusive. A guard written `if (pw == null || pw.projectilePrefab == null)`
  therefore refuses every lobbed weapon *while reporting "not a projectile weapon"*. That single line,
  copied into eight features, is what made `grow` look broken on Derbis when its physics support was
  already there. Go through **`ProjectileTargetOk`**, which branches on `usePhysics` and checks the
  prefab that mode actually uses. Only pass `supportsPhysics: true` if your patch really hooks
  `PhysicsProjectile` — today `ForgeGrowthPatch`, `ForgeDamageRampPatch` and `ForgeConvergePatch` do.
- **`ProjectileWeapon.FireSingle` calls `Shoot()` TWICE on a lobbed shot**, and the first call is
  against a *blank* projectile — the early one is tucked inside the `if (UsePhysics)` branch, before
  `Damage`, `RangeData`, `LifetimeData`, `Velocity` and `Owner` are assigned further down. A plain
  `Projectile` never does this. Anything that **captures** state in a `PhysicsProjectile.Shoot` hook
  latches the blanks: `ForgeDamageStack.Capture` is deliberately once-only, so it recorded a base
  damage of **0** and every later multiplier resolved to 0 — `damageRamp` and `grow`'s `damageAtFull`
  made a lobbed weapon do **no damage at all**. "Borrow the weapon's range" silently fell back to a
  default for the same reason. Go through **`ForgePhysicsShot.NotReady`**, which tests `Owner == null`
  *and* zero velocity together (either alone would reject a legitimate speed-0 dropped mine).
- **`usePhysics` DOES NOT MEAN "LOBBED" — it means "this shot is a rigidbody", and the two things
  it covers behave nothing alike.** `Ammo Derbis` (a grenade) ships `gravityScale 1.8`;
  **`Ammo Rocket Fire` ships `gravityScale 0`**, mass 4, `linearDamping 3`. Keying a refusal on the
  flag rather than on the gravity is how `guided` came to refuse rockets for a reason — "steering
  would cancel the fall" — that **does not apply to a shot with no fall.** Check `gravityScale`.
- **`PhysicsProjectile.Homing()` IS A FREE, ALWAYS-RUNNING STEERING LOOP.** Every FixedUpdate it
  `AddTorque`s the nose toward the private `targetPosition` (gated by `HomingData.maxAngularVelocity`)
  and `AddForce`s along `transform.right` up to `maxSpeed` — and **it does not check
  `HomingData.enabled`**, which gates only the re-target block in `Update`. `Weapon Rocket Fire`
  ships `torque 2, acceleration 70, maxSpeed 100, maxAngularVelocity 1000, targetMode FromLockOnly`,
  so a stock rocket is already a guided missile whose target never moves (`Shoot` puts it 10,000
  units dead ahead and nothing updates it). **So "steer a physics shot" is one write to
  `targetPosition`** — no velocity rewriting, so gravity, damping and thrust all keep working, and
  `FromLockOnly` + a null `Target` is the engine's own "commit straight ahead".
- **`maxAngularVelocity` is a DEGREES-PER-SECOND cap**, because Unity 2D measures `angularVelocity`
  in degrees — so a `turnRate` maps onto it with no calibration.
- **BUT THAT CAP HAS A DEAD ZONE, and it makes an over-strong torque WORSE than a weak one.** The
  gate is `if (abs(spin) < cap)`, checked **before** the add, so one step can push the spin past the
  cap — and while it is over, **no torque is applied at all, including no reversing torque.** The
  controller goes deaf exactly when it needs to correct. The rocket ships **29x** the torque needed
  to hold 200 deg/s and its heading swings **43 degrees** either side of the bearing in a slow limit
  cycle. Simulated, the cliff is between 16x and 18x needed. So bound the torque from **both** ends,
  and express the upper bound on the thing that causes it — the per-step increment as a fraction of
  the cap, which is damping-independent:
  ```
  needed  = turnRate * inertia * angularDamping / Rad2Deg
  ceiling = 0.25 * turnRate * inertia / (Rad2Deg * fixedDeltaTime)
  torque  = Clamp(prefabTorque, needed, Max(needed, ceiling))
  ```
- **`homingData.acceleration` IS A FORCE, SO IT IS NOT A SPEED** — the same trap as the gravity well,
  one subsystem along. A constant force settles at `force / (mass * damping)`, so the rocket's
  `acceleration: 70` cruises at **5.8 units/second** and its `maxSpeed: 100` is a ceiling nothing
  ever reaches. **`projectileSpeed` is only the LAUNCH**: a thrusting shot converges on its cruise
  from *either* direction, so a fast launch decelerates. A build warning claiming "thrusts up to 100
  units/second" was out by 17x, and a preset shipping `thrust: 18` was a cruise of 1.5 u/s — a rocket
  that visibly stops in mid-air. Take the SPEED from the author and compensate.
- **ON A SHOT WITH GRAVITY, `cruise` HAS TO BEAT THE WEIGHT - and the cruise line says nothing
  about that.** The thrust-is-a-force note above solves `force / (mass * damping)`, which is the
  **level-flight** speed: derived, correct, and measured in a world with no gravity. R14 test 163
  was a Derbis grenade with `cruise: 10` that *"mostly shoots up briefly and then falls"*, while the
  build log stated with total confidence that it "SETTLES AT 10 units/second". The hover point is
  where thrust acceleration equals gravity acceleration, and it reduces to one clean number:
  ```
  hoverCruise = gravityScale * 9.81 / damping        // the MASS CANCELS
  ```
  Ammo Derbis (1.8 / 0.4) needs **44**, so his file had 23% of the thrust required to hold itself
  up - it could only ever fall more slowly. It is also numerically the shot's terminal fall speed,
  which is the same statement read the other way round. Warned now, with the number printed and both
  honest answers offered (raise it, or keep it as a mortar you can nudge).
  > **A derived number is only true in the conditions it was derived for**, and a log line stating
  > it without the conditions reads as a measurement. Print the comparison, not the number. Same
  > family as `acceleration` being a force one field along, which is the trap this one hid behind.
- **`homingData.maxSpeed` OF ZERO MEANS "NEVER THRUST", NOT "NO LIMIT" - and zero is the stock
  value on every weapon that is not already a homing weapon.** The engine's whole thrust is
  `if (rigidbody.linearVelocity.magnitude < HomingData.maxSpeed) AddForce(transform.right *
  HomingData.acceleration)`. A magnitude is never negative, so at 0 that test can never pass and the
  `AddForce` never runs, **whatever `acceleration` says**. `Weapon White Derbis` ships `homingData`
  all-zeroes; `Ammo Rocket Fire` ships `maxSpeed 100`. That is the entire reason `guided` worked on a
  rocket and did nothing at all on a grenade - R15 test 187, *"even bumping up to cruise:60 I
  couldn't get it to fly"*: he got a nose that turned to the cursor and no engine behind it.
  The check that should have caught it was already there and its own comment stated the failure
  ("the cap has to be above the cruise or it becomes the real limit and cruise silently means
  nothing"), then guarded on `h.maxSpeed > 0f` - excluding zero, which is the strongest case of a cap
  being too low and the only one where the feature does nothing whatsoever.
  > **A guard written for "this value is too small" must say what it does at zero.** Treating zero as
  > "unset, leave it alone" is right for a field that means a limit and wrong for one the engine
  > reads as a gate, and which of those it is cannot be guessed from the name.
- **`arrive`-style "stop steering when close" cannot stop a rigidbody circling a fixed point.** It
  works on a plain `Projectile`, whose velocity is rewritten directly and which will therefore sit
  on a point spinning. A physics shot has momentum and a minimum turn radius, so it flies through and
  comes back. Measured laps around a stationary cursor across the whole range of the field:
  `0.0 -> 3.7, 1.5 -> 3.1, 4.1 (the turn radius) -> 3.0, 8.0 -> 1.9`. **Two successive explanations
  of mine were refuted by that table**, the second of which was about to auto-rewrite the author's
  number. Only range, lifetime or the cursor moving ends such a flight.
- **A lobbed shot cannot be steered *by rewriting velocity*, but it can be *aimed*.** Rewriting a rigidbody's velocity every
  frame cancels the fall and turns a grenade into a missile. The honest route is to solve the launch:
  `ForgeBallistics` bisects the launch speed against a replica of Box2D's actual step. Do **not** use
  the game's own `ProjectileMath.LaunchSpeed` — it assumes no air resistance, and every physics
  projectile has linear damping (Ammo Derbis: `linearDamping 0.4`, `gravityScale 1.8`), so it lands a
  shot **0.6–18 units short** depending on launch angle.
- **`rangeData` is completely inert on a lobbed (`usePhysics`) shot.** `PhysicsProjectile.Shoot`
  computes `timeToReachRange` from it and **nothing ever reads that field again** — there is no
  `HandleRange` on that class, only `HandleLifetime`. So a lobbed weapon is bounded by
  `lifetimeData` and by nothing else, and a `rangeData.range` set on one is a number the game never
  enforces. (Stock lobbed weapons all ship `rangeData.enabled: 0`, which is consistent with this.)
- **`ProjectileImpactBehaviour.destroyVelocityThreshold` is a dead field.** It is serialized on
  every weapon asset and set to a real value on three of them (Dandelion Sub 30, TurretSeeker Sub
  30, Tech Dandelion 3), and **no code anywhere reads it**. The intent is legible — "only die if you
  hit hard enough", i.e. bounce off soft landings — but it was never wired up.
- **Bouncing is two unrelated systems, and only the plain one is scripted.** A `Projectile` bounces
  through `ProjectileBounceData` + `Reflect(normal)`, a perfect mirror with no energy loss, and that
  branch **`return`s before the `ImpactBehaviour` block** — so a scripted bounce can never fire an
  explosion, sub-emitter or discharge. A `PhysicsProjectile` has no bounce code at all; it bounces
  because it is a real rigidbody, and **only when `impactBehaviour.enabled` is false** (otherwise
  `OnCollisionEnter2D` → `Impact` destroys it past `safetyDistance`). Eight stock weapons ship it
  off and bounce that way — Item Bomb, Item Matchbox, Caps Vial, Purple ClusterCube, Queen Bomb,
  both Bomber bombs, Fly Alfa. Because `TriggerCollisionOnObject` is *inside* that same
  `if (ImpactBehaviour.enabled)`, **those eight do no contact damage at all** — they only hurt via
  the explosion on their fuse.
- Lobbed restitution is `PhysMatGrenade` (friction 0.4, **bounciness 0.3**), set on the
  **Rigidbody2D**, not the collider — and **shared by a dozen ammo prefabs**, so it must be replaced
  with a fresh `PhysicsMaterial2D` rather than mutated. Its `m_BounceCombine: 4` is
  `PhysicsMaterialCombine2D.Maximum`, so the weapon's own bounciness wins over whatever the terrain
  uses. Two global settings shape this: `Physics2D` `m_BounceThreshold: 1` makes any contact slower
  than 1 unit/s perfectly inelastic, and `m_TimeToSleep: 0.5` puts a settled shot to sleep.
- **Linear damping dominates bounce arithmetic, so simulate it — twice now it has broken the
  intuition.** With the grenade's `gravityScale 1.8` / `linearDamping 0.4`, a 20-unit fall lands at
  **21.5 units/s, not the 26.6** that `sqrt(2gh)` predicts, and the error grows with height (−26% at
  40 units). Anything that converts a height the user typed into a speed must integrate the real
  step — `ForgeBounce.FallSpeed` does, for the same reason `ForgeBallistics` does. And **restitution
  above 1 does not run away**: damping removes more energy per bounce than a modest overshoot adds,
  so a super-elastic shot converges on a *steady* bouncing height (≈2 units at 1.15, 7 at 1.3, 19 at
  1.5) instead of climbing out of the level.
- **Impact hardness has an idiom in this codebase**: `Vector2.Dot(collision.relativeVelocity,
  contact.normal)` — the closing speed along the normal — used by `ImpactDamage`,
  `DestroyOnImpact` and `ImpactParticle`. Gravity is `(0, -9.81)` in world space with no fake
  z/height axis, so for a floor impact that number *is* the fall speed, and drop height is
  `v² / (2 · 9.81 · gravityScale)`.
- **Neither projectile class serializes ANY of its own state, so `Instantiate`-ing a live shot gives
  you a blank one.** Every one of `Projectile`'s fifteen properties — `Damage`, `Owner`, `Radius`,
  `RangeData`, `LifetimeData`, `PiercingData`, `ImpactBehaviour`, `Explosion`, `SubEmitters`, the lot
  — is a plain auto-property, and `shotTime` / `startSpeed` / `idealDirection` / `timeToReachRange` /
  `collisionLayerMask` are private fields with no `[SerializeField]`. `PhysicsProjectile` is the same
  (its only serialized field is `rigidbody`). Unity copies serialized state only, so a duplicated
  bullet arrives with **damage 0, radius 0, no lifetime and a collision mask of ZERO** — it flies
  through the world hitting nothing and, with no lifetime enabled, never despawns either. That is
  exactly how the catalyst's `split` shipped: three pretty ghosts per shot, every one inert and
  immortal. **Anything that duplicates a shot must copy the state across by hand and then call
  `Shoot()`** — `Shoot` is what stamps `shotTime`, `startSpeed`, `idealDirection` and the collision
  mask, and it is also what registers the copy with `ForgeProjectileTracker` and `ForgeKillsPatch`.
  `ForgeCatalystSplit.Clone` is the reference. (Attribution does **not** come free: `Stamp` only fires
  while `DoShoot` is on the stack, so a shot born mid-flight needs `ForgeKillsPatch.Attribute`.)
- **A config-object REFERENCE on a prefab-attached component arrives NULL on every shot.** Same root
  misconception as the blank clone above, one step earlier, and it is the single most expensive bug this
  project has had: it silently disabled the **entire `gas` and `catalyst` blocks** and cost ten failed
  tests in one round. `Instantiate` copies serialized state only, and a plain C# `Forge*.Config` class
  is not serializable, so `public ForgeGas.Config cfg;` on a component riding the projectile prefab is
  dropped — every guard reading `cfg` early-returns and the feature does nothing at all. **The build log
  is no help whatsoever**: the JSON parsed, the component attached, and the summary line printed the
  config back in full detail, because parsing was never the problem. In play a gas weapon fired a bare
  recoloured projectile and left no cloud; a catalyst node never armed, never wore its coin sprite,
  never halted and let bullets pass straight through.
  Go through **`ForgeConfigTable`**: the prefab carries an `int` id (Unity serializes ints happily) and
  the clone resolves the real config in **`Awake`** — before Shoot, before any patch, so it never
  depends on Harmony ordering. Do **not** just mark Config `[Serializable]`: Unity would then copy it
  per clone, losing `[NonSerialized]` runtime state (resolved sprites) and giving every shot a
  *different* instance — `ForgeCatalystNode.Cull` groups nodes by `cfg` identity to enforce `max`, so
  per-clone copies would make every node think it was the only one.
  The diagnostic, if this ever recurs: grep mod MonoBehaviours for a `*.Config` field, then check
  whether each is attached to a **prefab** or to a **live runtime object**. `ForgeShieldController` and
  `ForgeChargePatch.Scope` are the safe shapes (live object; plain non-MonoBehaviour class); the two
  prefab-attached ones were the two that failed.
- **The `Projectile` component is on the prefab ROOT** — checked across all 51 ammo prefabs that carry
  one, with zero exceptions — so `Instantiate(projectile.gameObject)` does bring the trail and glow
  children along.
- `rangeData.slowDown` decelerates a shot to a dead stop exactly at its range (the DiscGun ships it),
  which is what makes the boomerang pivot free. While `RangeData.enabled` is true the game rewrites
  Velocity every frame, so a mid-flight turn has to switch the projectile's *own* RangeData off.
- Re-hitting the same target is governed by per-target `PiercingData.damageRepeatDelay`. Use a real
  delay (~0.3s) or a huge one to disable — **never 0**, which re-damages every frame while overlapping.
- **A shot only survives a hit when `PiercingData.enabled` is on.** `Projectile.OnObjectHit` destroys
  it otherwise. Any feature whose whole point is "keep going after hitting something" (chain jump, and
  anything like it) must force piercing on rather than warn, or it silently does nothing.
- **`Projectile.TryHit` is the right hook for "hit an enemy"** — it only runs when the game's own
  re-hit delay allows the damage, so it counts enemies rather than frames spent overlapping one.
  `OnObjectHit` fires every frame of an overlap and will burn a per-hit budget instantly.
- **`ResourceTank.Value` has no floor.** The setter assigns whatever it is given (its only guard is
  for infinite tanks), so anything draining a tank per-frame must clamp at 0 itself and decide what
  happens at empty — otherwise the tank goes negative and the thing being paid for keeps running for
  free. That is exactly what the orbit hold-drain did before `ForgeOrbitPatch.Pay`.
- **A held or toggled orbit ring can never be charged the weapon's `cost`.** Cost is taken inside
  `Shooter.Shoot`, and those modes suppress firing, so there is no shot to price — hence
  `orbitHoldDrainPerSecond`, and `"cost"` meaning `Cost * FireRate` (what a normal weapon spends per
  second with the trigger held). `warmupTime` is inert on those rings for the same reason.
- **Gadget weapons bypass `Shooter` entirely.** Anything hooked onto Shooter (e.g. orbit) needs the
  gadget path handled separately.
  **AUDITED 2026-09-03, and the result is worth keeping because two thirds of it was already right.**
  Nine files patched `Shooter` at the time — **eleven as of 2026-09-19**, see the bump note below —
  and two more mention `typeof(Shooter)` for reflection only, which is what
  makes a raw grep overstate this. Six already accounted for gadgets: `overheat`, `spinUp` and
  `charge` refuse outright; `shield` warns for its held arc and points at `ram`, which is dash-driven
  and works either way; `orbit` has a dedicated `ForgeOrbitGadgetPatch` hooking the module's own
  `Activate`; and the minion cap is enforced a **second** time in the `MinionSpawnerWeapon.FireSingle`
  patch, which gadgets do reach. Three did not, and the two interesting ones are interesting for
  different reasons:
  - **`sticky` with the DEFAULT `refire` trigger HALF worked**, which is worse than not working.
    Laying a mine rides `Projectile.OnObjectHit` (every shot, whatever fired it); detonating on
    `refire` is a `Shooter.Shoot` prefix. So mines stuck and sat there forever, reading as "the
    explosion is broken" rather than "this slot has no trigger" — and nobody had to *choose* the
    broken option to hit it. **When a feature's halves reach a path differently, the working half is
    camouflage.**
  - **`tractor` was inert and a comment had already talked itself out of warning about it**, on a
    false premise: *"the compiler proves the check is impossible at this level … so it is documented
    in the builder page and the txt instead."* The slot the file declares is known right there, and
    three other blocks already take it. > **A comment explaining why something was not done is not
    evidence that it could not be done — re-derive the claim, especially when it is load-bearing for
    a missing guard.** Documentation is also not a substitute for a build warning: the page is read
    by someone browsing it, while the person who needs the warning hand-edited a file and is staring
    at a weapon doing nothing.
  - `spiral`'s `reset: "pull"` is inert (the stepping itself works, via `DoShoot`) — mild, opt-in,
    and now warned with "the spiral itself still steps" said out loud.
  **`gadgettest.py` pins the SET of Shooter-patching files**, so a new feature that patches Shooter
  and neither warns nor handles gadgets fails a check rather than shipping quietly.
  **BUMPED TO ELEVEN 2026-09-19, and the drift is the finding.** The set had grown by *two* since
  the audit and the pin had caught neither, because the harness itself had been lost with its
  session scratchpad — **a pin that does not run is a comment.** `surge` (2026-09-15) and `bloom`'s
  reach ring (2026-09-19) are both handled, and each by a route a warning-grep would have called a
  failure: `ApplySurge` takes no slot at all and asks `ForgeOverheat.TryGet` — the **registration**,
  not the JSON — so overheat's own gadget refusal already covers all three ways there might be no
  meter; and `bloom` warns only for the *ring*, because the bloom itself rides `Projectile.Shoot`
  and works in a gadget perfectly. So the check is that each file is **classified**, not that it
  warns: three of the eleven are correct with no warning of their own, and a harness demanding one
  from each would report three false failures and be deleted. One thing it pins that the original
  did not: **no patch may be declared by type NAME** (`[HarmonyPatch("Shooter", …)]`), which would
  slip past a `typeof` census silently.
- **A sub-emitter is unupgradeable, ownerless and fires straight up — all three by design, none
  fixable in place.** `WeaponFactory.Create` applies every module's `IWeaponModifier` to the weapon
  and *then* builds the sub with `Create(weaponData.subEmitter, **null**)` — the empty module cluster
  is passed literally, so a sub-emitter's damage is permanently whatever its asset says. Worse,
  **nothing ever equips one**: `WeaponBase.Equip` is called from exactly two places
  (`Shooter.SetWeapon`, `WeaponBasedActiveModule.Activate`), so `Owner` stays null and `FireSingle`
  assigns `projectile.Owner = null` to every fragment. Since `DamagableResource.ProjectileCollided`
  credits a kill only `if (projectile.Owner != null)`, **a sub-emitter kill credits nobody** — `onKill`
  rewards never pay for one — and `Projectile.FixedUpdate`'s friendly-fire skip is gated on the same
  null check, so a sub shot does no friend/foe test at all (only layer separation keeps it off you).
  Eleven stock weapons use sub-emitters, six of them player weapons (Tech Dandelion, Rocket
  TurretSeeker, Purple ClusterCube, Caps Vial, Item Matchbox, Rocket Fire). Both `FireSub`
  implementations fire `new FakeBarrel(transform.position, **Vector2.up**)`, so a sub's spread fans
  around world-up whatever the parent was doing. **This is why `split` exists as a separate key rather
  than a fix to `subEmitter`** — weapons already depend on that behaviour, and the new key inherits
  by *copying the live shot* (which `FireSingle` has already loaded with the weapon's upgraded
  properties) rather than by re-plumbing any of it.
- **`Instantiate` the PREFAB, not the live shot, when duplicating a projectile — and never hold the
  prefab reference on the component.** Copying the live object is what `ForgeCatalystSplit.Clone`
  does and it works there, but it cannot be done from `OnDestroy` (the source is mid-destruction), so
  a death-triggered burst has to build from the prefab. The trap: **Unity remaps Object references
  that point inside the hierarchy it is copying**, so a `public Projectile sourcePrefab` on a
  component pointing at its own prefab comes back on the clone *pointing at the clone* — and a
  second-generation fragment would copy itself. Keep it on the (non-serialized) `Config` instead, the
  same reason `ForgeConfigTable` exists for the config itself.
- **`rangeData.slowDown` means a shot's velocity is EXACTLY ZERO at the moment it dies**, and the
  stock White Popper — the most-cloned template in the mod — ships it ON. `FixedUpdate` lerps speed to
  0 across `timeToReachRange` and `HandleRange` destroys the shot at the same instant. So anything
  that reads `Velocity` in a death hook gets zero, not a heading. `split` fell through to its
  `transform.right` fallback and fanned every fragment due **world east** regardless of aim (a plain
  `Projectile` is never rotated unless `rotationData` says so), and the same zero made fragments
  launch at a fallback speed and then inherit slowDown, so they crawled and clumped. **Track the last
  heading and speed the shot had while it was actually moving** — `ForgeSplitter` does, and the
  mid-flight triggers never showed the bug precisely because the shot is still travelling there.
- **Every pellet of a volley spawns at the *same* point and differs only by angle.**
  `WeaponBase.DoShoot` walks `GetDirections()` and calls `FireSingle(barrel.Position, dir)` once per
  pellet, so an individual shot has no idea where the volley as a whole was aimed. Any feature that
  needs the volley rather than the pellet — a converging pattern, a per-pellet launch offset — must
  capture it at `DoShoot`. That method is provably on the path of every shot: the shoot SFX and the
  muzzle-flash instances both fire from it. The loop is synchronous (`FireSingle` instantiates *and*
  calls `Shoot()` before returning), so a plain static holds good for the whole volley —
  `ForgeVolley`, stamped with `Time.frameCount` so a later sub-emitter can't read a stale axis.
- **★ BY THE TIME `Shoot` RUNS, A SHOT'S `Velocity` ALREADY CARRIES THE SHIP'S VELOCITY.**
  `ProjectileVelocityInfluencer` hooks `OnPrepareProjectile` — the last thing `FireSingle` does
  before `Shoot()` — and adds the firer's motion (per `velocityInfluenceData`). So
  `Velocity.magnitude` in a `Shoot` hook is `projectileSpeed` **plus how fast you were flying along
  the aim line**, and its direction is skewed by any strafe. `bloom` read its swell rate from that
  and shipped with pop timing that followed the ship: faster flying toward the crosshair, slower
  flying away, and a launch speed exactly cancelled gave a rate of ~0 — a bubble that never finished
  swelling and never popped (R19 247-249). Anything that treats the launch speed as the GUN's number
  must read `IHasSpeedProperty.Speed` off the weapon instead; that is the live `projectileSpeed`,
  modules included, motion not.
- **`WeaponBase.Fire` is `for (burst = 0; burst < BurstSize; burst++) { DoShoot(barrel); await
  UniTask.Delay(BurstDelay * 1000); }`** — verified against the IL of the `<Fire>d__147` state
  machine, which dnSpy doesn't emit. Three consequences: `DoShoot` runs **exactly `BurstSize`**
  times per trigger pull (so `BurstSize: 0` is a weapon that fires nothing); the **first** shot is
  synchronous inside `Shooter.Shoot`, and only the burst *tail* is deferred; and anything that wants
  to scope a change to "one trigger pull" must survive `(BurstSize-1) * BurstDelay` seconds of
  real time rather than one stack frame.

### Holding the trigger

`Shooter` is the only path with a held trigger — **gadgets call `weapon.Fire(new FakeBarrel(...))`
directly**, so nothing Shooter-shaped reaches them. Things worth knowing before hooking it:

- **Nothing in the game reacts to the trigger coming UP.** `WarmupTime` is a minigun spin-up:
  `Update` ticks `Warmup(deltaTime)` while held, gates `Shoot()` on `IsWarmedUp`, and calls
  `CoolDown()` (which zeroes the meter) the moment it isn't. So "release fires something" is
  entirely mod-side. `warmupProgress` is private but `IsWarmingUp` / `IsWarmedUp` / `Warmup` /
  `CoolDown` are public.
- **Suppressing the shot is not enough to make a weapon quiet.** `Update` calls
  `PlayContinousSound()` *before* it gets as far as `Shoot()`, so a prefix that only blocks `Shoot`
  leaves the gun roaring over a silent charge. Both are public and both need the prefix.
- **A Harmony POSTFIX still runs when a prefix skipped the original**, and on `Shooter.Shoot` that
  is a live trap: three of this mod's own features prefix it and return false (the charge block's
  hold-fire, the swarm cap, and anything added later). Overheat's postfix charged heat for shots that
  never happened — at `fireRate 8` with `perShot 8`, **64 heat/second while firing nothing** — which
  made overheat + a release charge into a weapon that fired nothing at all. The discriminator is
  public and needs no cooperation: `Shooter.Update` only calls `Shoot()` when `FireRatePassed` is
  true, and the real `Shoot` sets `lastShootTime = Time.time` on its way out, making it false. So
  `FireRatePassed` in a postfix answers exactly "did the shot happen", for every suppressor at once.
- **A PREFIX THAT RETURNS FALSE NEVER STAMPS THE FIRE-RATE CLOCK, so the weapon is asked again on
  the NEXT FRAME - and every frame after.** `FireRatePassed` is `Time.time > lastShootTime +
  1/FireRate`, and only the real `Shoot` assigns `lastShootTime`. So any suppressor built as a
  skipping prefix turns the weapon into a per-frame retry: the instant the condition clears, a shot
  fires immediately rather than at the next legitimate interval. That is how the swarm cap made a
  `fireRate` of 0.8 (a pull every 1.25s) spawn two minions **0.017 seconds apart** - one frame - which
  R12's rate instrument caught and then blamed on `fireRate`, a field that was innocent.
  **A refusal should consume the pull**: `ForgeSwarm.ConsumePull` stamps `lastShootTime` by reflection
  so the cap becomes a clock rather than a gate. Worth checking for any other skipping prefix on
  `Shoot`.
- **A PULL THAT REACHED THE WEAPON HAS SPENT ITS INTERVAL, WHATEVER BECAME OF THE SHOT.** The
  note above says a *skipping prefix* never stamps the clock, and `ForgeSwarm.ConsumePull` fixed
  that for a refused pull. R14 test 170 came back with the PULLS instrument reporting pulls **0.017s
  apart** - one frame - against a 1.25s interval, on pulls that were *allowed* and did spawn. That
  cannot be sourced by reading the code: `Shoot` stamps `lastShootTime` as its last statement,
  `Update` calls it once a frame, and every path is once per interval. But the shape of the evidence
  narrows it to one half - **every allowed pull produced a minion, and firing is the first statement
  while the stamp is the last** - so the shot happened and the clock did not move.
  So the rule is applied to *every* pull rather than only the refused one, via a `Shooter.Shoot`
  **finalizer** that compares the clock across the call, repairs it, and logs the cause with
  Harmony's `__exception` attached. A finalizer returning `void` leaves the exception exactly as it
  was, so it observes without changing what the game does with it.
  > **A guard written for the exceptional path is not a guard for the ordinary one.** Twin of the
  > gadget-audit lesson that a guard against one instance of a mechanism is not a guard against the
  > mechanism.
  > And the clock write had a bare `catch (Exception) {}` four lines below a comment of mine saying
  > that a silent fallback on a timing path is exactly what costs a test round. **Grep your own
  > standing rules against the code they were written about.**
- **BLOCKING A SHOOTER FREEZES A BEAM ON SCREEN, AND CLEARING `IsTriggerPulled` DOES NOT FIX IT.**
  R15 test 188: *"after overheating the beam stays in place firing from the original place that it
  died out at. I move away and the beam is still firing from that spot."* `HitscanWeaponVisual.Firing`
  is written from exactly one place - the `else` branch of `if (IsTriggerPulled)` inside
  `HitscanWeapon.OnBarrelMoved` - and `Shooter.Update` returns on `blockers.Count > 0` **before** it
  calls `OnBarrelMoved`. So the flag we set is read by nobody, and the visual keeps its sprite, its
  `Light2D` and its impact particle at the position and rotation of the last frame the gun could fire.
  > **Clearing a flag is not the same as applying it.** A flag is applied by whatever reads it, and
  > if that reader is above the guard you just added yourself to, nothing has happened at all.
  **`ForgeShooterQuiet.Silence(shooter)` is the one owner** of everything a block has to do by hand -
  both sounds, the trigger flag, `CoolDown()`, and one more `OnBarrelMoved` with the flag already
  false. Overheat and stun both call it; stun had predicted this case in a comment ("left true, a
  beam's renderer can keep drawing its last frame") and half-fixed it the same way.
  (The damage really does stop: the cast is in `FireSingle`, which needs `Shoot()`, which returns on
  the same blocker list. What looks like continuing damage is the impact-spark particle still
  playing on whatever the beam was last pointed at.)
- **`Shooter.FireRatePassed` RE-READS `FireRate` EVERY FRAME** — it is
  `Time.time > lastShootTime + 1f / weapon.FireRate`, computed fresh, never cached. So raising the
  rate mid-hold shortens the gap you are *already waiting through* rather than the next one, and
  there is nothing to re-arm; that is what makes `spinUp`'s headline reward a single field write.
  The same property is why a rate of **0 is fatal** rather than merely slow: `1f / 0` is Infinity
  and the weapon never fires again, so anything writing that field needs a floor above zero.
- **Most stock weapons ship `angleVariance: 0`**, including White Popper, White Shotgun, Bolt and
  Worm — a shotgun's inaccuracy is entirely its deliberate `spread`. So any feature that improves
  *accuracy* by tightening `angleVariance` does nothing at all on the weapons an author would
  reach for first, and has to say so at build time or offer the `spread` knob instead.
- **`Shooter.Shoot()` skips the resource check** — `OwnerHasResource` is tested in `Update`, not in
  `Shoot`. Calling `Shoot()` from a patch therefore hands out a free shot unless you test
  `OwnerHasResource` (public) yourself. It ignores `FireRatePassed` for the same reason.
- `Cost` and `KnockbackForce` are read inside `Shoot()` itself; everything else the weapon reads
  happens inside `DoShoot`. That split is why `ForgeChargePatch` scopes them in two different places.
- **`AimAction.RotationSpeed` clamps turning to `MaxRotationSpeedWhileShooting` while shooting** —
  but that's the *enemy* aimer. The player aims through `Aimer`, which has no such clamp, so a
  held-trigger player weapon doesn't slow the crosshair.

### A LOG LINE THAT BORROWS ANOTHER FEATURE'S WORD SENDS PEOPLE LOOKING FOR A FEATURE THAT IS NOT THERE

`onKill`'s `clearBurn` removes the ship's **burn level** - being on fire - and the build log printed
it as **"cools 3"**, in a mod where "cool" means the `overheat` meter everywhere else, on a weapon
that can carry both blocks. R15 comments: *"just noticed in onkill options for weaponforge that its
missing a reduce heat option here too. Could you add that (unless i missed all that)?"* He had read
his own log saying "cools 3", gone looking for the heat option, and found none - because there was
none. It says "puts out N of your own burning" now.

`onKill.coolHeat` is the real thing, and it needed no new mechanism: `ForgeHeat.Remove` takes heat
off, and a kill that drops the meter below `ventTo` **ends a vent lockout on the next frame** through
the path that already knows how to recover (sound, repaint, rate restore) - so a second unlock route
was deliberately not written. Scoped to the weapon that scored the kill (`_credit`), so a secondary's
kill never cools your primary. `ForgeHeat.CoolOn(unit, weapon, amount)` is also the entry point
Module Forge's `KillRewardEffect.coolHeat` reaches by reflection.

### ARMING A SHOT IS NOT FIRING ONE, and a lockout can make the difference invisible

`overheat`'s `ventShot` promised "Blowout fires a charged shot" in its own build log and could never
do it. Two independent reasons, and each alone was enough:

1. `ForgeChargePatch.Arm` only marks the **next** shot as charged - it fires nothing.
2. `ForgeHeat.Blow` then blocked the Shooter and cleared `IsTriggerPulled`. In `vent` mode the gun
   stays blocked until fully cool (~2.9s on his file) while `armedUntil` expires after **0.35s**, so
   the arming was always dead before anything could claim it.

Nothing warned, because the `charge` block it needs really was present. Now `FireExternally` arms
**and** fires, before the lock, and it needs three things a bare `shooter.Shoot()` would not:
`_firing` (a `holdFire` release charge prefixes `Shoot` and would swallow the very shot it sizes),
`Cost = 0` scoped around it (`Shoot` subtracts Cost with no check at all - the check lives in
`Update`), and a `_venting` re-entry guard, because this runs from the `Shoot` **postfix** and heat is
already at max, so `Add` would call `Blow` from inside `Blow` - an immediate stack overflow, not a
subtle bug.

**And the same weapon had a second, unrelated reason its charge "did nothing":** `charge` in `release`
mode with `holdFire: false` keeps auto-firing while you hold, and `Release` ends with
`if (!shooter.FireRatePassed) return;` - so the charged shot competes with the weapon's own fire for
one gate. At `fireRate 8` that is a 0.125s window, and letting go inside it drops the charged shot
silently. Warned rather than changed, because exempting the release would alter every charge weapon
already written.

### Aiming — the crosshair and the barrel are two different rays

**The game HAS aim assist, and every stock player weapon uses it.** This cost a wrong conclusion
once, so the shape of the mistake matters as much as the fact: `Aimer.AimAt` runs its point through
every `IAimEffect` before storing it, **nothing in the game implements `IAimEffect`**, and the
inference drawn from that — "therefore there is no aim assist" — was wrong. The assist is applied one
stage further down, to the *barrel*, by a different class entirely.

- `Aimer.TargetPosition` **is** the raw mouse (`MouseWordPosition.Current`, a plane raycast at z=0).
  That half was right.
- **`AssistedBarrel` wraps the `BarrelTransform` on all four ship prefabs.** Its
  `GetTargetDirection` asks `AimAssist.TryGetTarget` for the nearest on-screen `AimAssistTarget`
  within `WeaponData.aimAssistData.maxAngle` of the current direction and **returns a direction at
  that target instead** — led by `targetVelocity * distance / projectileSpeed` when `isPredictive`.
- **50 of 97 weapon assets ship `aimAssistData.enabled: 1`, including every stock White weapon** —
  15° on Popper / Shotgun / Blades / DiscGun / Bolt / Worm, **45° on every rocket**, and predictive on
  all of them. `WeaponActiveModule` (the gadget path) is on at 10°.
- The player can switch it off (`GameplayOptions.disableAimAssist`), which makes it a *variable* you
  cannot assume. Note the target is still acquired when it's off — only the direction swap is skipped.

So **the crosshair and the direction pellets leave along are not the same ray**, and any feature keyed
to the barrel (`ForgeVolley.Axis`, and therefore converge's `distance` focus, muzzle offsets, the
post-focus settle heading) inherits the assisted one. A feature that must honour the cursor has to
read `Aimer.TargetPosition` — which is what converge's `focus: "cursor"` already does.

**`AimAssistTarget` marks crates and boxes as well as enemies, and there are two ways to pick one.**
73 prefabs carry the component: 60 fighting units and **13 containers** (`Crate`, `Crate Money`,
`Crate Tech`, `Box Health`, `Box Bomb`…). They are not neutral either — a crate's `Unit.faction` is
the *same asset* the Cross enemies use, and the Player faction lists it under `enemies` — so
`IsFriendsWith` does not filter them and anything auto-targeting picks them up. Usually right (a split
that also bursts the crate beside you is the ULTRAKILL behaviour); when it isn't, the discriminator is
**`AIAgent`**: all 60 combatants have one and none of the 13 containers do. That is what
`FindNearestTarget`'s `objectsToo` switches on.

**And the two finders choose differently — the names do not say so loudly enough.**
`FindTarget` takes the smallest **angle** off the current heading (matching the game's own
`PhysicsProjectile.TryFindTarget`), which is right for a shot steering toward something ahead of it
and wrong for "hit whatever is near me": at range 10 it prefers an enemy 9.9 units dead ahead over one
a unit to the side. `FindNearestTarget` takes the smallest **distance**. Chain jumps and catalyst
splits want the second.

**Finding the Aimer at all is the hard part, and getting it wrong cost a test round.** On the ship the
two things sit on unrelated branches — `Aimer` on `Barrel <- Base <- ShipKBM`, `AssistedBarrel` on
`AssistedBarrel <- PrimaryWeapon <- ModuleSlots <- ShipKBM` — so `GetComponentInParent<Aimer>()` from
the barrel finds nothing. Worse, **`Aimer.barrel` is typed `BarrelTransform` while the weapon fires
through an `AssistedBarrel`**, so matching the two by reference is always false. `ForgeVolley.FindAimer`
now unwraps `AssistedBarrel`'s private `barrel` field and, failing that, accepts a lone Aimer on the
unit. Anything else that wants the crosshair should go through it rather than re-deriving it.

> **And a process note, because it was the expensive part.** Aim assist is a real mechanism that
> *could* explain "my shots don't go where I point", so it got written into three files as the
> diagnosis for a converge bug it had nothing to do with. One A/B — the game's own aim-assist toggle —
> falsified it in seconds. A plausible mechanism is not a diagnosis; find the cheap experiment first.

> **If your feature steers a shot, write `idealDirection` — not just `Velocity`.**
> `Projectile.idealDirection` (private) is the game's *base heading*. Everything that curves a
> projectile — our wave motion, the game's own noise wobble — rebuilds `Velocity` every frame by
> rotating that field, and never by reading `Velocity` back. So a feature that writes only `Velocity`
> gets silently overwritten by any curve feature sharing the shot. That is exactly how a boomerang
> with `wave` enabled flew off along its *original* heading while still visibly waving. Two features
> both writing `Velocity` also can't be ordered reliably, since they're both `FixedUpdate` prefixes.
> Publishing the intended heading to `idealDirection` makes them compose instead of fight, and it is
> order-independent. **`ForgeSteer` is the one owner** — `SetHeading` / `SetIdeal` / `TryGetIdeal`,
> and nothing else may reach the field. For the same reason, steer from `idealDirection` rather than
> from `Velocity` — rotating from `Velocity` folds another feature's offset back in each frame and
> widens into a spiral.
>
> **AUDITED 2026-09-03. The contract was half-kept, and the half that was missing was not the half
> the note above predicted.** `steertest.py` pins the result: one reflection copy, and a census of
> every direct `Velocity =` write with its reason, so a new unaccounted writer fails a check.
> - **Only DIRECTION is contested. Speed is not.** Both overwriters rebuild Velocity as
>   `rotate(idealDirection) * Velocity.magnitude` — they *carry* whatever magnitude they find. So a
>   speed-only write is immune, and `Velocity = Vector2.zero` is immune too (zero propagates through
>   both). That clears nine of the nineteen writers outright — five freezes and four speed scales —
>   and it means the contract is narrower than "don't write Velocity".
> - **`rangeData.slowDown` is the MIRROR overwriter and it has no contract.** It rewrites *speed*
>   every frame from the `startSpeed` captured at `Shoot`, preserving direction. So a mid-flight
>   speed change is undone on a weapon with it on — which the most-copied stock template ships.
>   `ForgeSlowShotPatch` gets this exactly right and says why: it hooks the **`Shoot` prefix**, where
>   `startSpeed`, `idealDirection` and `timeToReachRange` are all derived, so one write makes all
>   three agree. Put a speed feature there, not in flight.
> - **`homing` wrote Velocity alone and read Velocity back — both halves wrong.** So it did *nothing
>   whatever* on a shot that also waved. It had defended against the game's identical noise wobble by
>   switching that off at build time, with a comment showing the mechanism was fully understood; the
>   mod's own `wave` is the same overwriter and nothing guarded it. **A guard written against one
>   instance of a mechanism is not a guard against the mechanism.**
> - **`deflect`/`rally` aim wrote Velocity alone, and it is the one feature build time cannot save.**
>   Every other steerer disables the noise when it *builds* the weapon. A deflected shot is not ours
>   to build — it belongs to whoever fired it. And the four stock weapons shipping
>   `movementNoiseData` enabled are all **enemy** weapons (Soldier, Floater, Floater Officer, Cross
>   Small — purple), at 60° / 2 Hz. Enemy fire is the only thing there is to deflect, so the single
>   slot it could break in was exactly the slot it exists for. **A build-time guard protects what you
>   build and silently protects nothing on what you only touch** — the same asymmetry applies to
>   `gravity`, which also captures enemy shots.
> - **Five copies of the one reflection, four of them subtly different**, in a file whose own header
>   says one owner per question is the rule. `ForgeSteering` was a whole duplicate class serving a
>   single call site; ricochet and gravity had their own (gravity's swallowed the failure silently);
>   and **`ForgeWavePatch`'s copy latched `_ready` AFTER the throwing call**, so a lookup failure
>   would never latch — retrying the reflection and logging an error *every frame, per projectile*.
>   Centralising fixed a bug nobody was looking for. **Latch before the call that can throw.**
> - `wave` + a steerer is still order-dependent in principle (two prefixes, no reliable order), so
>   `SetHeading` cedes the **direction** to a wave that is already flying and writes only the
>   **speed**. Skipping the write entirely was the first attempt and was wrong: `speed` is not always
>   the speed the shot already had, and a boomerang ramps it through the turn — that version would
>   have frozen every speed change on a waving shot. R14 test 181 observes what is reasoned here.

> **If your feature changes a weapon's LIVE TUNING, publish a multiplier to `ForgeWeaponStats` —
> don't write the property.** `WeaponBase.FireRate`, `Damage`, `AngleVariance`, `Spread`,
> `ProjectileCount` and `Cost` are all plain public setters, and by the time `spinUp` was asked for
> **three features were each writing `FireRate` from their own private base**: overheat's
> `response: slow` (every frame), `ForgeKillBuff` (the onKill buff), and `ForgeSlow`. Module Forge's
> kill buff was a fourth, across the reflection boundary.
>
> **The failure is not "the last writer wins" — it is a RATCHET, and it is order-dependent**, which
> is why nobody had pinned it down. Simulated against the real code shape:
>
> - **Buff first, then heat:** overheat's drift check re-baselines onto the buffed rate and the pair
>   lands on the correct product **by accident**. Looks fine.
> - **Heat first, then a kill:** each feature folds the other's output into its own baseline, so a
>   kill on a hot gun makes it **slower**, and every kill after that makes it slower again. Four
>   kill cycles took a 6/s gun below 1/s. `slowestFireRate` cannot protect you, because the
>   compounding is in the *baseline*, not in the multiplier.
>
> A drift check — "the weapon moved since I last wrote, so treat what is there now as the base" —
> reads as careful and is the mechanism of the bug: against a per-frame writer, "it moved" is true
> on every frame. One captured base per (weapon, stat) plus named multipliers has no ordering to get
> wrong. **Withdrawing the last contributor drops the captured base**, so a legitimate change (a
> module augmentation between engagements) is picked up instead of being pinned by a stale number;
> publishing a multiplier of 1 is *not* the same as withdrawing.

> **If your feature needs to know HOW MUCH DAMAGE LANDED, read `ForgeDamageDealt` — don't measure
> it again.** `DamagableResource.Damage(float)` refuses damage three silent ways before it touches a
> tank (a damage blocker, no tank of that resource type, i-frames), so the honest measurement is to
> snapshot `Tank.Value` in a prefix and diff it in a postfix. `leech` worked that out; `surge`
> copied it **verbatim, comments included**; and by the time `rank` wanted the same number there
> were about to be **three byte-identical copies on the method every point of damage in the game
> passes through** — which already carried seven of this mod's patches.
>
> One owner since 2026-09-19. Readers subscribe with a cheap gate and a callback.
>
> - **The point is not the patch count.** Three copies of one measurement can drift apart and
>   nothing would report it: leech could start counting overkill while surge did not, and the two
>   would disagree about what the same hit was worth on the same weapon.
> - **The carrier must be `__state`, never a static.** `Damage` NESTS — it can call `Die()`, whose
>   spawn-on-death explosion reaches `OnExplosion` and back into `Damage` before the outer call
>   returns, so a static snapshot is overwritten by the inner hit.
> - **Overkill is removed once, centrally.** `ResourceTank.Value` has no floor, so a 200-damage
>   rocket into a 5-health fly leaves the tank at −195 and the raw delta reads 200.
> - **★ A REFACTOR THAT MAKES NEW INFORMATION AVAILABLE IS A BEHAVIOUR CHANGE unless each reader is
>   asked whether it wants it.** The shared version can attribute a **burn tick** (see below), which
>   neither old copy could. Handing those hits to `leech` would have silently started healing every
>   burn weapon already written, and to `surge` would have started filling meters from gas clouds.
>   Both explicitly decline them, and `ranktest.js` asserts that they still do.

> **A BURN TICK CARRIES NO WEAPON, and `ForgeIgnition` is how one is attributed.**
> `DamagableResource.Update` ticks `this.Damage(burnProperties.fireDmgPerTick)` with nothing on the
> stack saying where the fire came from — so "damage over time earns XP" would have been a setting
> that read as working and paid nothing. Two facts make the fix exact rather than a guess:
> **`ApplyBurn` is private and called from exactly three places, all inside the methods that set
> `ForgeKillsPatch.Source`** (so the weapon IS knowable at ignition), and **the burn tick is the only
> `Damage` call inside `Update`** (so "we are inside Update" identifies one precisely — inferring it
> from "the victim is on fire" would also have claimed lava and hazard cells). The stamp expires, and
> the whole thing is gated on a static nothing sets unless a feature asks for it.

> **If your feature changes damage in flight, publish a multiplier to `ForgeDamageStack` — don't
> write `Damage` directly.** Exactly the same problem as `idealDirection`, one field along: two
> features assigning `Damage.amount` from their own captured bases means whichever FixedUpdate prefix
> runs last wins, and Harmony orders them arbitrarily. `grow`'s `damageAtFull` and `damageRamp` would
> have cancelled each other out. The stack keeps one captured base and multiplies every contributor,
> so it is order-independent. **Capture at `Shoot`**, not lazily — one feature's Shoot postfix can
> modify `Damage` before another's reads it, and the second would record the modified value as normal.

> **If your feature keeps static state, decide which of three kinds it is.** The mod's statics
> outlive the scene, and `ForgeBurnResetPatch.ResetAll()` is the one place a run beginning is
> announced — hooked at **both** `RunData.Initialize` (new run) and `GameSaver.Load` (continue).
> - **Build-time config** keyed by a weapon asset: registered once at load, and clearing it would
>   *unbuild the weapon* for the rest of the session. Must NOT be reset. `ForgeCrit.Reset()`
>   documents this trap for `_weapons`.
> - **Live per-shot state:** put it on a MonoBehaviour and it dies with the scene for free. This is
>   why the reset list is as short as it is, and it is why `ForgeGravity`'s `open` / `owner` fields
>   are safe despite holding a `Unit`.
> - **Per-run accumulators** in a static: these need the seam. Bounded caches are fine unreset if
>   they cap and sweep dead keys (`ForgeKillsPatch` at 512, `ForgeHitSound` at 128), and a
>   per-second window is fine unreset because **no clock here uses `Time.timeSinceLevelLoad`** —
>   session-monotonic clocks make a stale stamp read as "long ago" and the window self-heals. That
>   last one is load-bearing: `timeSinceLevelLoad` restarts per scene, so a stale stamp would read
>   as "just now" and hold the cap shut for a second of every new run.
>
> **AUDITED 2026-09-03. `teardowntest.py` pins it. The frightening categories were already clean**
> — no unbounded growth, no stacked event handlers (the one real subscription is a *constructor*
> postfix, so it is one per object and GC takes it), no clone accumulation (`BuildAll` is idempotent
> via `_builtNames`). The finding was somewhere else entirely:
> - **A once-only log gate is per SESSION, but what it describes is per RUN.** 26 of them across 19
>   files (`_said*` / `_warned*`) - **20 one-shot bools and 6 keyed `HashSet<string>`s** - and
>   **three** had a working reset. So the second run of a session reported nothing — and the run where you go looking for a warning is exactly the run that cannot
>   produce it. Every doc line saying "check the log if X does nothing" was true only on run 1.
>   `ForgeDiagnosticGates` now sweeps them **by naming convention** rather than by a list: the
>   hand-written version is what drifted, and a list nobody rereads is not a mechanism. It
>   deliberately over-resets (a few gates describe a session-invariant reflection failure), because
>   one repeated line beats losing a diagnostic when it is wanted.
> - **Two teardown methods were written, documented and never called.**
>   `ForgeMinionSolid.Reset()`'s own comment stated the exact bug its absence caused; `ForgeSlow`
>   held a `ClearTime()` describing itself as being for "a future *cancel all of ours*". **A written
>   teardown method is not a wired one** — grep the call, not the declaration.
> - **The reset list existed twice, byte for byte**, once per entry point. Anything added to a
>   duplicated list is one paste from being half-wired, and a half-wired reset fails only on the
>   path you did not test. One `ResetAll()` now.
>
> **AND THE SWEEP ITSELF WAS 20 OF 26 CORRECT, reported as done, with the missing six named in the
> log I read.** Found 2026-09-08 from his round-14 log: *"Once-only log gates found: 20"*, against a
> grep of 26 written down in the same session. **`HashSet<T>` does not implement the non-generic
> `System.Collections.ICollection`**, which is what `IsGate` tested for - and `List<T>` and
> `Dictionary<K,V>` both do, so the test reads as safely conservative while excluding the single
> container actually in use. All six keyed gates are `HashSet<string>`, all six in the minion
> features he tests most. Two rules out of it:
> - **Test the capability you are about to use, not an interface that implies it.** `IsGate` asked
>   "is this an `ICollection`" while `Clear` went looking for a `Clear()` method - two different
>   questions about the same field, so acceptance and action could disagree. Both ask for `Clear()`
>   now, so they cannot.
> - **A COUNT IS ONLY EVIDENCE WHEN IT CAN BE CHECKED AGAINST A COUNT DERIVED ANOTHER WAY.** One
>   total told me nothing and I read it as confirmation. The line prints the split now - "20
>   one-shot, 6 keyed" - which a grep of the declarations can contradict. The same shape as the
>   RATE CHECK reading the template's fireRate: an instrument whose number looks like evidence is
>   worse than no instrument.

### Sprites

- The game runs at **20 pixels per world unit**, and stock projectile art is only about **7×7 px**.
  Imported art looks enormous until you internalise that.
- **Module icons are the exception: 24×24 at PPU 40.** And an icon is drawn by *two* renderers that
  disagree — the card on the grid is a UI `Image` (72×72, Simple, `preserveAspect` off) which stretches
  art and ignores PPU, while the ground pickup is a world `SpriteRenderer` where PPU is the only thing
  setting the size. So art imported at the 20 default is double size on the ground and perfect on the
  card. `ForgeSpriteLibrary.ResolveIconSprite` rebuilds to the stock footprint rather than making
  people know this. Icons are also **tinted by `module.color`** in both places.
- **A renderer's ALPHA is a visibility decision, not a colour — never overwrite it.** Every Larva and
  Maggot keeps a `HighlightSprite` inside its `Visual_*` subtree: a full-body silhouette held at
  **alpha 0** and flashed by the damage effect. `sr.color = yourColour` set that to an *opaque*
  colour, so a recoloured minion wore its own outline over its real art — reported as "changing the
  color puts their weird default sprite to the forefront". Multiply the requested alpha by the
  renderer's own in every colour path, and skip anything named like a highlight. `ForgeFade` follows
  the same rule for the same reason.
- **NEVER save-and-restore a unit's sprite colour — publish a layer to `ForgeUnitTint`.** Same class
  of bug as writing `Damage` directly instead of going through `ForgeDamageStack`, and it bites
  harder because it is *persistent*. "Remember `sr.color`, write it back when the effect ends" is
  correct for exactly one effect at a time; with two, the second to start remembers the
  already-tinted colour and restores to it, making the first effect's tint **permanent for the rest
  of the run**:
  ```
  slow starts   remembers ORIGINAL,          writes ORIGINAL × blue
  slam starts   remembers ORIGINAL×blue,     writes ORIGINAL × blue × red
  slow ends     writes back ORIGINAL           ← the red is gone
  slam ends     writes back ORIGINAL×blue      ← THE BLUE IS NOW PERMANENT
  ```
  > **A MULTIPLY CANNOT BRIGHTEN, so "make the tint stronger" is not a range problem.** He asked
  > twice in one round (R14 tests 166 and 167), having already turned `tintStrength` up: *"I was
  > hoping to make the color STRONGER and more prominent... some of the enemies look like they're
  > being tinted and others not so much."* Both halves of that are the operator: `base * colour`
  > moves a pixel toward black and never toward the colour, so a pale tint over a dark grey Cross
  > barely registers while the same tint over a pale Fly reads clearly - and **at strength 1 a
  > multiply is already as strong as a multiply gets.** So the answer was a second operator, not a
  > wider range on the first: `"tintMode": "replace"` lerps the sprite toward the colour.
  > `multiply` stays the default because every file already written was tuned against it.
  >
  > **AND "REPLACE BRIGHTENS AS READILY AS IT DARKENS" WAS WRONG, one layer further down.** R15 test
  > 185: *"their prefab models dont appear to change color very much except for their mouths."*
  > `SpriteRenderer.color` is itself a **vertex multiply in the shader** - the pixel is always
  > `texture x colour` - so the very most any tint can do is `texel x yours`, in either mode.
  > Measured off the atlas, **`Unit Fly Regular`'s body art averages 40/255 luminance (16% grey)
  > against 76 for its beak**, both on the same shared `SpriteLitAA` material, which is exactly why
  > the mouth changed and the body did not. It was being written correctly the whole time.
  > What `replace` really buys is the **layer arithmetic** - the tint factor is set rather than
  > compounded, so it cannot get weaker as layers pile on - not a higher ceiling. Three honest
  > answers now in the docs: pick a colour that survives a multiply (saturated, not pastel), use
  > `colorPulse` because motion is visible at any brightness, or accept a dark version of the colour.
  > A coloured `Light2D` on the unit is the only thing that would genuinely brighten it (the
  > material is Lit) - **offered and not built**, since it is a new mechanism rather than a setting.
  >
  > **Four hypotheses were killed by measurement before the right one**, and each was cheap to test
  > and would have been expensive to ship: an Animator overwriting `m_Color` (only the *Dead* clip
  > animates colour), a different material on the body (it is the same asset as the beak), the
  > `Visual`-subtree scoping missing it (`Visual/Body/BodySprite` is inside it), and `stun` not
  > passing `tintMode` through (it does). *Decoding the texture was the only step that answered it.*
  >
  > Three details worth keeping. **Multiply resolves first and replace paints over the result** -
  > the only ordering that means anything, since a replace layer is "paint this on top" and a
  > multiply applied afterwards would darken the paint. **Two replace layers are a weighted mix**,
  > not a chain of lerps, for the reason `ForgeMuzzleTint` learned the hard way. And **`tint` is
  > accepted as a spelling of multiply**, because `minion.colorMode` got there first and already
  > calls the same two things `replace` and `tint`: two vocabularies for one idea in one mod is a
  > bug report waiting to be filed. Read in one place, `WeaponBuilder.TintMode`, beside the
  > strength - so it reached eight features with no lines added to any of them.

  `ForgeUnitTint` keeps one captured base per renderer — captured once, never from a tinted state —
  and multiplies every live layer over it. Multiplication is commutative, so there is no ordering to
  get wrong and no restore path that can bake somebody else's colour in. It also owns the two scoping
  rules in one place (skip highlight renderers; scope to the `Visual` subtree so AI state indicators
  keep their own colours) and writes RGB only, preserving whatever alpha the renderer has at that
  moment so a `fade` composes instead of fighting.
- **THE MUZZLE FLASH NEEDS THE SAME LAYER TABLE AS A UNIT, and `ForgeMuzzleTint` is it.** Predicted
  rather than reported, which is the only reason it never shipped: `overheat` tints the flash toward
  `hotColor` as heat builds, `spinUp` tints it toward its own colour as the gun winds up, and the
  author asked for the two blocks to **compose** on one weapon. Two private captures of one field is
  the permanent-bake-in bug — whichever captures second records the first one's tint as its base and
  restores to *that*, so the flash keeps a colour it was never meant to keep for the rest of the run,
  with both features reporting a correct restore. **Layers here are LERPS applied in a stable order
  sorted by key, not multiplied like `ForgeUnitTint`'s**: a flash colour is a *replacement* (orange
  taking over a white puff), and switching to multiply would visibly change every overheat weapon
  already written. Sorting gets the property that matters — the result does not depend on which
  feature published first — and with a single layer it is byte-identical to what overheat computed
  before. The capture is also **re-taken when the instance list changes shape**, because
  `InitializeVisuals` destroys and rebuilds those systems and `spinUp`'s `pellets` reward triggers
  exactly that.
  > **ORDER-INDEPENDENCE IS NOT THE SAME THING AS COMPOSING, and I wrote the weaker property down
  > as though it were the goal.** The first version applied the layers as a **chain of lerps** in
  > key-sorted order, and the note above argued for exactly that: the result does not depend on
  > which feature published first. True, and insufficient - `Lerp(x, c, 1)` *is* `c`, so the last
  > key alphabetically discards every layer before it. "heat" sorts before "spin", so a fully wound
  > gun showed **no heat at all**, and it stopped showing heat exactly as the heat began to matter.
  > R14 test 176 reported a white flash with no orange, and the build log claimed in as many words
  > that the two blocks "COMPOSE rather than fight".
  > Fixed 2026-09-08 with a **weighted mix**: `total <= 1` gives `base*(1-total) + SUM(c_i * a_i)`,
  > `total > 1` normalises by the total. With ONE layer that is `Lerp(base, c, a)` term for term, so
  > every overheat weapon already written renders byte-identically including its alpha - which is
  > what made the change safe to make. **The same arithmetic is now in `ForgeUnitTint` for its
  > `replace` layers, for the same reason.**

  > **A SHARED TABLE REPLACING A CAPTURE-ONCE-AND-LATCH MUST KEEP A NO-ALLOCATION FAST PATH, or the
  > refactor is a regression in the feature it absorbed.** Caught auditing this one after it had
  > already built clean and passed 88 assertions: the first version re-walked
  > `GetComponentsInChildren` and allocated a `List` on **every `Set`**, and `Set` is called once a
  > frame by a live ramp — so routing overheat through it made a shipped, tested feature allocate
  > per frame while hot, where its own code had captured once and latched. Compare the cheapest thing
  > that genuinely changes (the weapon's own instance `Count`, plus a null sweep for destroyed
  > systems) and only walk deep when that fails. **The same shape applies to an idle publisher:**
  > `ForgeSpin` published-then-withdrew an Entry, a Layer and a muzzle re-scan every frame on a gun
  > nobody was firing, until an `_active` latch was added. **When you move state into a table, ask
  > what the steady state costs** — correctness tests will not tell you.
- **THE READ-MODIFY-WRITE COLOUR RULE IS NOT ABOUT SPRITES - it is about any per-frame write that
  reads its own previous output.** `ForgeUnitTint` exists because of it for `SpriteRenderer.color`,
  and overheat's muzzle tint shipped making the identical mistake one subsystem along: it read
  `main.startColor` back and lerped *that* toward `hotColor` every frame. Two consequences, and he
  reported the second - the colour converges on fully-hot within about a second whatever the heat
  fraction is, and **once the base is gone `Lerp(now, hot, 0)` returns `now`, so cooling can never
  restore anything.** Capture one base per system up front (`ForgeBurnColorTerrainPatch` and
  `RgbAnimator` both do; that is the house idiom) and restore it on disable, because the prefab clone
  is shared with the weapon.
- Stock art lives in one **1024×2048 atlas**. Sprites packed tightly in an atlas can't be reused as
  beams (neighbouring pixels bleed in) — `ForgeBeam` refuses them rather than rendering garbage.
- **The game ships plenty of ANIMATIONS as numbered runs of sprite assets, and until 2026-08-28 only
  one frame of such a run was reachable.** Every stock path returned a single sprite —
  `TryGetStockArt` handed back one `Art`, `ForgeTrail.ResolveFrames` literally returned
  `new[] { stock }` — while *custom* art could be a multi-frame flipbook. `ForgeSpriteLibrary`
  **`TryGetStockFrames`** closes that, and the naming rule is dictated by what it must not break:
  **a name ending `_<digits>` still means exactly that frame**, because real files already say
  `part_cyrcle_12` and animating it through `part_cyrcle_0..16` would change those weapons' look on
  upgrade. A sequence is asked for by the **bare base name** (`area_zero_particle_tuff`, nine frames)
  or an `Anim` suffix, both of which resolve to nothing today. Gaps end a run rather than being
  skipped — `0,1,2,7` is far more likely to be two effects sharing a prefix. It does **one**
  `FindObjectsOfTypeAll` prefix pass rather than N `FindAsset` probes, because that helper re-scans
  every loaded asset on every call.
- **SEVEN SPRITE KEYS COULD NOT SEE THE GAME'S OWN ART AT ALL, and the warning is why nobody noticed.**
  `impactSprite`, `muzzleSprite`, `gas.sprite`, `shield.sprite`, `swipe.sprite`,
  `catalyst.nodeSprite` and `charge.meterSprite` called `TryGetArt` and nothing else — custom folder
  only. Only `projectileSprite` and the leech orb followed with `TryGetStockArt`. So all ~450 game
  sprites were unreachable through those seven. Fixed 2026-08-31 with **`TryGetAnyArt`** (custom then
  stock) at every site.
  **The failure was LOUD — every site warned and named the string — which is exactly why it survived a
  long time.** *"impactSprite 'area_zero_particle_tuff' is not one of your sprites"* is true, and it
  sends the reader to check their spelling; what it meant was "this key cannot do stock art at all."
  > **A warning that names the INPUT but not the CAPABILITY sends the reader to check their typing
  > instead of the feature. When a lookup can only search half its namespace, the message has to say
  > which half.**
  Related trim: each of those sites printed all 156 custom names via `string.Join(", ", Names)`, making
  single log lines ~2,000 characters. `NotFoundHint()` prints a count plus six examples — enough to
  distinguish "folder loaded" from "folder empty", which is all the list ever answered.
- **A MODULE FORGE FILE IN THE `weapons` FOLDER now identifies itself.** The two mods read different
  folders (`weapons` / `modules`, each derived from its own assembly location), so a duplicate file is
  a user placement issue — but "missing required template" as an ERROR, four times a session, reads as
  a mod bug. A module has `effects` and no `template`; that pair names it, and it is a warning pointing
  at the right folder now.
- **"THE FRAME SUFFIX IS PARSED EVERYWHERE" IS NOT "EVERY KEY ANIMATES", and I asserted the second
  from the first.** `:2-6` / `:8-0` / `@24` are split in `TryGetArt` and `TryGetStockArt`, which all
  **fourteen** sprite keys go through — so the parsing really is universal, and three files plus a test
  went out claiming it worked on "all nine places a sprite name is read". Both halves were wrong:
  fourteen keys, not nine, and **only seven of them flip through a frame list.** Asked "does the
  reverse apply to the other sprites?", the answer had to be read out of the code rather than recalled.
  The split is a property of the **consumer**, not of the resolver:
  - **Seven read `Art.animation.frames`** and animate unconditionally — `projectileSprite`,
    `trail.sprite`, `muzzleSprite`, `impactSprite`, `beam.impactSprite`, `curveBeam`,
    `leech.orbSprite`.
  - **Five draw ONE `SpriteRenderer`** and animate only when the name asks — `gas.sprite`,
    `shield.sprite`, `swipe.sprite`, `catalyst.nodeSprite`, `charge.meterSprite`. See the opt-in note
    below.
  - **Two beam slots and the two icon paths can only ever show one frame**, by engine limit rather than
    omission: a beam is one stretched quad (`ForgeBeam.ApplySprite` warns and points at `curveBeam`)
    and an icon is assigned once and never ticked. `:4` still *picks* the frame on all four.
  The generalisable lesson: **a shared parser proves the syntax reaches a call site, not that the call
  site does anything with the result.** Confirm by grepping the consumers for `.animation`, which is
  what finally produced this list.
- **OPT-IN ANIMATION on those five is a compatibility rule, not a taste one — `ForgeSpriteLibrary.OptInFrames`
  owns it.** All five have always resolved art through `TryGetArt`, so a multi-frame sheet has always
  arrived with its frames attached; they simply took `.sprite` and dropped the rest. Animating them
  unconditionally therefore **changes weapons already written**: an existing `gas` or `shield` block
  naming a sheet renders one still frame today and would silently start moving on the next launch with
  no edit to the file. So `Art` carries the parsed `spec` and a bare name stays still, while `:0-8` or
  `@12` opts in. That is also why this needed the author's decision rather than being an obvious
  improvement — the choice was his (2026-08-30), and it is the reason the flag lives on `Art` rather
  than being inferred from `animation != null`.
  Two per-consumer details that are about the art, not the plumbing: **gas passes `randomStart: true`**
  because a cloud is up to 24 renderers and lock-step reads as one strobing blob, while **the catalyst
  node passes false on purpose** — its renderers are faces of one object and should agree.
  `VisualCustomizer.AnimateRenderer` attaches to `sr.gameObject` (not the root) precisely so a caller
  holding several renderers can drive each one, and it is idempotent because the swipe blade re-dresses
  a live object every swing.
- **The icon paths bypassed the suffix entirely, and the failure was loud, which is why it survived.**
  Both `ApplyModuleIcon` and the catalyst indicator handed the whole string to
  `JsonFieldMapper.FindAsset`, so `"HUD_GridTiles_4:2"` matched no asset and fell through to
  "no sprite named ..." — naming the exact string, so not a silent fallback, but with the suffix as an
  unstated reason. `ResolveStockIcon` (and Module Forge's own `StockIcon`) fix it, and
  `ResolveIconSprite` now splits too and caches under the **raw** name so `sheet:2` and `sheet:5` are
  two icons. An icon still cannot animate; picking *which* frame it shows is the whole feature.
- **THE TUFF "POP" IS NOT A SEPARATE ANIMATION — the nine frames ARE the pop, and I described the
  direction backwards without ever looking at them.** `area_zero_particle_tuff_0..8` sliced from a
  60×60 PNG as a 3×3 grid, and the sprite rects settle it: widths **12, 11, 10, 10, 9, 8, 7, 6, 4**,
  monotonically shrinking. Frame 0 is the biggest bubble, frame 8 the smallest, so **forward is the
  pop** and `:8-0` makes bubbles *grow*. R11 test 127 shipped saying the opposite ("shrinking instead
  of growing"), which would have failed a working feature. **The error came from writing a test off the
  syntax rather than the art** — one `Read` of the PNG, or one grep of the rects, was the whole cost.
  Only two prefabs reference those sprites, found by grepping the nine GUIDs (the right method):
  - **`ImpactParticle Cell Tuff`** — burst **2–5**, speed **0–15**, size **0.5**, lifetime **1–3s**,
    `gravityModifier` **−0.05** so they drift **UP**, ColorModule disabled, and the nine frames at
    `cycles: 1` with **`timeMode: Lifetime`**.
  - **`DeathParticle Cell Tuff`** — three systems: **10–20** bubbles, **30–40** of the `part_cyrcle`
    smoke flipbook, **1–5** lingering bubbles.
- **`AutoPopper` is a free, global, faction-agnostic terrain cascade.** It subscribes to
  `Level.CellChanged` and pops the four neighbours of any destroyed cell whose `CellType.cellBehaviours`
  holds an `AutoPopNeighbour`. Tuff: **probability 0.65, probabilityDecrease 0.45, delay 0.05–0.15s** —
  so gen 0 rolls 65%, gen 1 rolls 20%, gen 2 is negative and **the ripple dies after two generations**.
  It pops via `DestroyCell(x, y, 1324)` and `OnCellChanged` ignores changeSource 1324/15324, which is
  the recursion guard. **No faction test and no player gating** — any weapon that breaks Tuff already
  gets the cascade, so "make my shots pop terrain" needs no code for Tuff. Do not confuse this with the
  *damage/burn* cell behaviours, which are a different driver.
- **PARTICLE ANIMATION SPEED HAD TWO DIFFERENT ANSWERS, and `ForgeSpriteLibrary.ParticleFps` is now the
  one owner.** Four keys feed `ApplyParticleSprite` — `impactSprite`, `muzzleSprite`, `trail.sprite`,
  `beam.impactSprite` — and they disagreed *before* that method existed: the trail passed only the
  name's/block's fps, so a trail with neither **already animated by lifetime**, while impact and muzzle
  passed `art.animation.fps`, which for a stock run is a fabricated **12** and so could never reach
  Lifetime at all. That is precisely why stock bubbles finished popping in 9/12 = **0.75s** and then sat
  as a 4px dot for the rest of a 1–3 second life.
  **`@life` is the fix and it is opt-in by construction**, which is why it beat the tempting shortcut:
  "pass fps only when explicitly asked" cannot work, because custom art defaults its fps too
  (`anim.fps = (float?)entry["fps"] ?? 12f`), so `spec.HasFps` cannot tell an authored 12 from an
  invented one. Each caller still states its own `fallback` out loud rather than the defaults being
  quietly unified — unifying them would change how existing weapons look, so it stays his call.
  `@life` also removes an arithmetic step this repo used to ask for in writing ("a flash lives about
  0.4 seconds, a 20-frame animation needs roughly 50 fps"), and it is the *only* correct answer when a
  prefab's lifetime is a random range. On a SpriteRenderer key it warns rather than defaulting quietly.
- **`impactGravity` keeps Unity's sign (negative RISES)** rather than being flipped into a friendlier
  "rise" key, because values here get copied verbatim off stock prefabs while tuning and the tuff puff
  reads `-0.05`. **`impactLifetime` is the twin of `muzzleLifetime`**, which existed for ages while
  impact had nothing; both now live in `VisualCustomizer.ShapeParticles`, the existing reshape owner.
- Particles receive sprites via **TextureSheetAnimation in Sprites mode**, *not* a SpriteRenderer — so
  **pixels-per-unit does nothing for particle art**. The quad is sized by the system's own `startSize`,
  and every stock muzzle is Billboard with no `startSize3D`, meaning a *square* quad that stretches
  non-square art. That's a third scale rule alongside sprite renderers (PPU) and UI Images (stretch).
- **NOTHING IN THE GAME SHRINKS A PARTICLE BY SIZE — `SizeModule.enabled` is 0 on every impact
  prefab.** Both of the game's shrinking effects are **flipbooks whose art gets smaller**, and the two
  do it differently: `area_zero_particle_tuff_0..8` shrinks through its sprite **rects** (12px → 4px,
  which is why `:8-0` reverses the pop), while **`part_cyrcle_0..19`** shrinks *inside* a fixed 20×20
  frame — a hard-edged circle going 20px → 1px monotonically, verified by decoding the alpha channel
  of `Sp Particle Smoke.png`. So "an impact of four solid circles that shrink" is
  `"impactSprite": "part_cyrcle@life"` with `impactCount: 4` and needs no code; the bare base name is
  what asks for the run, since a trailing `_<digits>` always means one specific frame.
- **A flash's scatter is `startSpeed`, not the burst count — and on a muzzle it is two things at
  once.** Every particle gets a random launch speed (Popper muzzle: 0–40 on one system, 0–70 on the
  other; Popper impact: 0–30) *and*, on a muzzle only, spawns anywhere inside a `ShapeModule` circle
  (radius 0.68 / 0.49). An impact's spawn radius is already 0.0001, so it scatters by speed alone.
  This is why `muzzleCount: 1` still landed the single puff somewhere new every shot — the count was
  never the problem. `muzzleSpread`/`impactSpread` scale `startSpeedMultiplier` and `shape.radius`
  together; use the *multiplier*, since the stock value is a two-constant random range that a plain
  assignment would flatten to one speed.
- **A muzzle flash is usually TWO stacked particle systems** (root + `Particle_muzzleFlash2`). Apply
  changes to all of them, or the stock puff shows through underneath whatever you did. `muzzleColor`
  and `muzzleSprite` share one prefab clone — cloning twice loses whichever pass finished first.

### Beams (hitscan)

- A beam is **ONE stretched quad**. `UpdateVisual(start, end, normal)` sets `size.x` to the distance.
  So the *stock* beam cannot bend and cannot flipbook-animate. The hit spark is a particle system and
  can. `curveBeam` replaces that quad with a chain of short ones, which is the only way to get either.
- **A stock beam damages exactly ONE thing.** `FireSingle` does a single `CircleCast` and takes
  `hit.collider` — so **piercing is not a flag anywhere in this game**; it is a cast per segment that
  the mod does itself, and it is a large damage increase (threading six enemies at the Caps Laser's
  `damageRepeatDelay` of 0.08 is ~6× the stock beam).
- **A BEAM WALKS `GetDirections` TWICE, from two different methods**, and a patch on one is not a
  patch on both. `HitscanWeapon.OnBarrelMoved` walks it every frame to place each beam **visual**;
  `DoShoot` walks it again for the damage **cast**. `spiral` hooked only `DoShoot` — verified that
  `DoShoot` is overridden nowhere, which is true and was not the question — so it rotated the damage
  ray while the drawn beam stayed put. **Not "not working" but silently desynced**, which is worse,
  and the author reported the visual because that is the honest thing to look at.
  > **A patch on the method that CONSUMES a value is not a patch on every READER of that value.**
  `OnBarrelMoved` is the only `override void OnBarrelMoved` in the assembly, so the pair is closed.
  Anything scoping a `WeaponBase` property for a shot must scope it there too — applying the current
  value and **advancing nothing**, since that method runs per frame rather than per shot. The frame
  order makes them exact: `Shooter.Update` calls `OnBarrelMoved` before it reaches `Shoot()`.
- **A held beam re-fires at its fireRate**, so any per-shot angle step is a per-second rate: 24
  degrees a shot at fireRate 12 is **288 deg/s**, a strobe. Warn in the unit that matters.
- **`HitscanWeapon.OnBarrelMoved` indexes `visualsInstances[num]` with NO BOUNDS GUARD**, and the
  contrast with the bullet path is the trap. `WeaponBase.DoShoot` indexes
  `muzzleParticleSystemInstances` and *does* guard it (`Count > num`), so raising `ProjectileCount`
  at runtime on a bullet weapon is merely cosmetic — the extra pellets get no muzzle flash. Do the
  same on a **beam** and it throws `ArgumentOutOfRange` **every frame the barrel moves**. So
  anything that changes the pellet count at runtime must call the public
  `InitializeVisuals()` — which `HitscanWeapon` overrides to resize exactly that list — and must do
  it in **both directions**: `OnBarrelMoved` only walks as far as the current count, so orphaned
  visuals left *above* it keep their last state, which is a beam frozen in mid-air with `Firing`
  stuck true. Rebuilding is safe for custom art: it instantiates from the weapon's own visual
  prefab, which is the clone this mod has already customised (`ForgeBeam.Apply` edits the prefab,
  not the live instances). Gate it on the **rounded** count so a ramp costs a handful of rebuilds
  rather than one per frame.
- **The damage raycast is separate from the visual raycast** — the classic trap, because bending only
  the visual gives you a beam that *looks* perfect and damages a straight line. **But the frame
  ordering makes it free:** `Shooter.Update` calls `OnBarrelMoved` first, every frame,
  unconditionally, and only afterwards sets `IsTriggerPulled` and reaches `Shoot()` → `DoShoot` →
  `FireSingle`. So compute the path once in `OnBarrelMoved`, cache it, and let `FireSingle` read the
  same cache — one path, two consumers, and they cannot disagree. `ForgeCurveBeamPatch` is the
  reference. (`IsTriggerPulled` is therefore *stale* inside `OnBarrelMoved`; the stock code reads it
  there too, so the one-frame lag is house behaviour. And a gadget-fired beam never goes through
  `Shooter` at all, so the damage pass has to rebuild a stale path itself.)
- **A beam's steering rate should be per unit of LENGTH, not per second.** A beam is a shape, not a
  motion: degrees-per-unit makes the tightest circle it can draw (`360 / turnRate` units around) the
  same regardless of frame rate, ship speed and fire rate. Per-second would make the same weapon curve
  differently on a faster computer.
- **Quads meeting at an angle leave a wedge-shaped gap on the OUTSIDE of the bend.** Any chained-quad
  visual has to overrun each segment past its own endpoint by a fraction of its length, or a tight
  curve renders as a dotted line rather than a beam.
- Every stock beam sprite is authored with a **left-edge pivot (0, 0.5)** because the quad grows
  rightward from the muzzle. Slicers write a centred pivot, so sprites are rebuilt at load.
- The fire renderer is **Sliced**, the warmup is **Tiled** — and **Tiled fills size in *both* axes**,
  which is what produces the "two beams stacked" look if you pick repeat mode for the wrong slot.

### Orbit

An orbiting **orb is not a projectile** — `ForgeOrbitController.SpawnOrb` disables its `Projectile`
component, desimulates the rigidbody and disables the collider, then does damage itself with
`Physics2D.OverlapCircle`. So no projectile patch reaches it, and features like `grow` have to be
reimplemented on the controller (`orbitGrow`) rather than reused. If you add anything per-orb that
affects damage area, note that `cfg.hitRadius` is read by five methods — pass the value instead of
adding another config read, or the damage will stop matching the art.

### Your own bullets cannot hit each other

`PlayerProjectiles` does **not** appear in its own row of the layer collision matrix, so two of your
shots pass straight through one another — and a plain `Projectile` ships with its `Collider2D`
disabled anyway, because it resolves hits by `CircleCast`. So **"my shot reached that other shot of
mine" has no physics event at any radius**, exactly as with enemy fire.

Anything that needs it goes through `ForgeProjectileTracker.TrackPlayer` + `MineNear`, which is
**non-destructive** on purpose — unlike `CollectNear`/`DestroyNear`, which remove what they return
because the caller is taking ownership. A catalyst node inspects your bullets and hands them back, so
removing them would break every later node.

### Kills — who is credited, and who is not

`Unit.RegisterKill(Unit)` fires a **public** `KilledAnotherUnit` event with (killer, victim), and it
is called from **exactly three places**, all in `DamagableResource` and all gated on
`IsDead && owner != null`: `ProjectileCollided`, `OnHitByHitscanWeapon`, `OnExplosion`.

- **Nothing else in the game credits a kill.** Not `Die()` itself, not burn ticks, not electricity,
  not cell damage — and **not any of this mod's own damage.** The ram and the gas cloud both call
  `HealthBase.TakeDamage` directly, so their kills were invisible to everything, which would have made
  a "resource on kill" reward silently never pay for the very case it was asked for. Both now go
  through `ForgeKillsPatch.Credit`, which calls `RegisterKill` properly rather than paying out behind
  the game's back — so the music controller and anything else listening see them too. **Any future
  feature that damages a unit directly must do the same.**
- **The weapon is never recorded**, only the owning Unit. Each of the three routes needs its own
  answer, and `ForgeKillsPatch` has them: a projectile is stamped at `Shoot` with the weapon `DoShoot`
  is running for (that loop is synchronous, so a frame-scoped static is exact); a beam is handed the
  weapon already; an explosion takes it from the projectile that spawned it, scoped around
  `SpawnExplosion`. There is no back-reference from `Explosion` to a weapon — it is a struct.
- `ResourceTank.Charge(amount)` clamps to `[0, Capacity]`; writing `Value` directly does not.

### Damage DEALT is not the damage number — measure the victim's tank

`DamagableResource.Damage(float)` **refuses the damage outright, three silent ways**, before it ever
touches a tank:

```
if (IsDamageBlocked()) return;                       // damageBlockers, non-empty + active
if (amount > 0 && Unit.HasTank(resource) &&          // NO TANK OF THAT TYPE
    Time.time - lastDamageTime >= iFrameDuration)    // i-frames
```

The middle one is not a corner case — it is how the damage-type table lets a Crawler resist nearly
everything. So **anything that pays out proportionally to damage must snapshot `Tank.Value` in a
prefix and diff it in a postfix**, not read `amount`; otherwise it pays for hits that did nothing.
`ForgeLeechPatch` is the reference. Three things fall out of doing it that way:

- **It composes with crit for free.** Crit multiplies `amount` in a `Priority.First` prefix, so the
  tank delta already contains the crit and there is no ordering to arrange.
- **Overkill has to be clamped by hand.** `Value` has no floor, so a 200-damage rocket into a
  5-health fly leaves the tank at −195 and the raw delta reads 200.
- **Carry the snapshot in Harmony's `__state`, never a static.** `Damage` **nests**: it can call
  `Die()`, whose spawn-on-death explosion reaches `OnExplosion` and back into `Damage` before the
  outer call returns. A static would be overwritten by the inner hit and the outer one would measure
  the wrong delta.

Useful and public, checked rather than assumed: **`DamagableResource.Tank`** (a property resolving
`Unit.GetTank(resource)` — so guard it with `HasTank` exactly as `Damage` does), **`.resource`**,
**`.IsDead`**, **`.IsInvincible`**, and **`WeaponBase.Owner`**. `DamagableResource.Unit` is private;
`GetComponentInParent<Unit>()` is the house idiom.

**Dropping the reward as a pickup (`asPickup`) — three traps, all found the hard way:**

- **There are NINE `ResourcePickup` prefabs and two are named nothing like their resource.**
  Fuel→`FuelPickup` (blue orb), Caps (orange orb), Purple (pink orb), White, **Health→
  `ResourcePickup_Red`**, Tech (purple squares), and Money at 10/20/50. **`Resource Electron` is the
  only stock resource with no pickup.** Fuel, Caps and Purple share one orb sprite and differ only by
  `SpriteRenderer.color`, which is why re-tinting the orb is a good fallback — `Resource.color` is a
  `ColorAsset` (public `color` field, implicit cast to `Color`) with `resourceBarUnitColorFull` as a
  backup. **Enumerate by COMPONENT, never by name.** Two successive comments here got this list wrong
  — one claiming nine "one per resource plus Fuel", then one claiming eight with no Fuel at all —
  because both were built from the `ResourcePickup*` naming pattern instead of from the type. The
  code's own scan is by type and always found all nine; only the prose was wrong, which no test can
  catch.
- **`Resources.FindObjectsOfTypeAll` sees PREFABS; `FindObjectsByType` does not.** Learning the
  template only from pickups that have already *spawned* means the art is whatever crate you opened
  first — right payout, wrong colour, consistent within a run and random between runs. That is the
  fingerprint. Cache the prefabs; they are in memory because the drop tables reference them, and a
  prefab is never destroyed. A live pickup **destroys itself when collected** and a destroyed Unity
  object compares equal to null, so a cached one silently goes null.
  **Guard the scan with its own flag, set only by the scan and only on success** — Module Forge's twin
  guarded it on "is the cache non-empty", which a `Pickup.Start` postfix filling the same cache makes
  true immediately, so its scan never ran at all.
- **A cosmetic failure must not become a payout failure.** `Drop` returns a bool and the caller falls
  through to the tank. It used to return void inside an `if (asPickup) … else if (HasTank) …`, so a
  missing template took the branch, dropped nothing, granted nothing, and said nothing.

### A widened layer mask has no faction attached to it

`ChargerHead` (the ram on Larva / Maggot / Child) does its damage in `OnObjectHit` with **no faction
test of any kind** — it hurts whatever the `CircleCast` returned. Its `collisionLayerMask` ships as
`1088 = Player | Ground`, so `ForgeMinionAllegiance.Ram` adds the Entities bit to let a friendly
rammer reach enemies — **and every unit in the game is on Entities, including the other eleven
minions in the swarm.** That shipped as a swarm that ate itself, reported as "there's a health drain
on them somehow".

**The general rule: widening a mask to reach a new group grants access to everything in that group,
and the faction filter you were relying on may not exist.** `ForgeMinionRamGuard` supplies the
missing one via `Unit.IsEnemiesWith` (the game's own question, and the same one the AI asks to pick a
target). Terrain has no `Unit`, so it falls through and rammers still stop at walls.

`ChargerHead` is **not visible to this assembly at compile time**, so there is no `typeof()` for a
`[HarmonyPatch]` attribute and `PatchAll` cannot find it — patched by hand from the plugin, with
`object __instance` and positional `__0`.

**AND THE SAME MASK MAKES A CHARMED RAMMER HARMLESS TO EVERYTHING, which is a wider bug than the
one that got reported.** `ForgeMinionAllegiance.Ram` widened the mask for *summoned* minions in R10
and nothing carried it to `charm`, which set the faction, widened the eyes, dropped the grudge and
left the headbutt as the prefab shipped it - `Player | Ground`. So a charmed Larva could not touch
another convert (R14 test 158, the reported half) **and could not touch an enemy either**: its only
reachable target was the player, which the ram guard correctly refuses. Charm on a rammer produced a
pacifist. `WidenRam` / `RestoreRam` are the reusable pair now, and charm records what it changed
because unlike a summoned minion this is a stock enemy that goes back to being one - restored
*before* the `Widened` marker is dropped, or there is a frame in which a released enemy rams
everything on Entities.

> **A hit that arrives by a different route is not covered by a rule written at a chokepoint it does
> not pass through.** Charm's `harmless` guard hangs off `ForgeKillsPatch.Source` - the WEAPON that
> dealt the damage - and a headbutt has no weapon. So a ram was never subject to that guard, never
> appeared when the two `infighting` exemptions were being reasoned about, and needed a third
> exemption in `ForgeMinionRamGuard`. Contact damage, burn ticks and cell collisions have the same
> property: they report no weapon.

**AND THE WIDENED CAST STARTS INSIDE THE RAMMER'S OWN BODY, which is a second bug from the same
line and the one that actually killed things.** R15 test 184: *"with the larva and maggots they take
ticking damage and pretty much die from being charmed"*, plus the older never-explained *"the
swarmgun minions hurt themselves"*. One cause for both.

`ChargerHead.FixedUpdate` casts a circle of `radius` **from the beak** along the body's velocity.
Measured off all four prefabs that carry one, the beak circle and the body collider overlap each
other *before the sweep starts*:

| prefab | cast r | beak offset | body r | overlap |
|---|---|---|---|---|
| `Enemy_Larva` | 0.39 | 0.61 | 0.39 | 0.17 |
| `Enemy_Maggot` | 0.51 | 0.671 | 0.51 | 0.35 |
| `Enemy_Child` | 0.39 | 0.61 | 0.53 | 0.31 |
| `Unit Swimmer Maggot` | 0.51 | 0.671 | 0.51 | 0.35 |

Every one of those bodies is on **Entities (layer 7)** - the bit we add - and `Physics2DSettings`
ships **`m_QueriesStartInColliders: 1`**, so Unity returns a collider the shape is already inside at
**fraction 0**. The `CircleCollider2D` and the `HealthBase` are both on the unit root, and
`OnObjectHit` hands the damage to `hit.collider.GetComponents<IHazardSensor>()` with no self test.
So a widened rammer headbutts **itself** every `minDamageDelay` - 0.5s on a Larva, 0.3s on a Maggot.

**And `ForgeMinionRamGuard` was letting it through on purpose**: the self case was folded in with
terrain as `hurt == null || ReferenceEquals(hurt, rammer) -> return true`. Right for terrain, exactly
wrong for self.

**The second half, which the damage fix alone does not give back:** a fraction-0 hit is the nearest
hit there can be and `CircleCast` returns only the nearest, so the self overlap was also *eating the
cast* - the ram reached an enemy only on a frame where the enemy happened to be returned instead.
`SweepWithoutSelf` (a `ChargerHead.FixedUpdate` prefix, widened prefabs only) redoes the cast with
the rammer's own rigidbody filtered out and hands the nearest remaining hit to the original. It
re-checks `minDamageDelay` first, because the original's first statement is that same test - so a
reflected `Invoke` costs the two or three a second a ram can land rather than fifty.

> **A fix that widens what a query can SEE has to be checked against what the query STARTS INSIDE.**
> The R10 note above reasoned carefully about the other units the new bit exposed and never once
> about the unit doing the casting.

**Diagnosis note worth keeping:** the first suspect was `ImpactDamage` (self-damage on crashing),
which fit the symptom perfectly and **the Larva prefab does not have**. Checking the prefab for the
component's field shape (`minVelocity`) took one grep and saved building the wrong fix.

### Projectiles do not tunnel; minions do

`Projectile.FixedUpdate` finds hits with a **swept** `Physics2D.CircleCast` along
`Velocity * Time.deltaTime`, so the whole step is tested and a fast shot cannot skip a wall. The
"past ~56 u/s it tunnels 1-unit terrain" figure in the minion notes is about **rigidbody movement
with Discrete collision detection** and does not transfer. A `speedRamp` warning nearly shipped
asserting the opposite — *check which mechanism a fact belongs to before moving it between systems.*

The real trap on a speed ramp is the **slow** end: a shot that ramps down to near zero hangs in the
air, and on a Distance ramp a stalled shot stops advancing through its own ramp, so it stays stalled.

### Force, mass and damping — why one number never fits

PUNK's rigidbodies are wildly unlike each other, and `AddForce` fights **both** mass and damping:

| | mass | linear damping |
|---|---|---|
| ship | 1 | 0.5 |
| `Enemy_Cross` | 5 | **6** |
| `Enemy_Cross Jock` / `Red` | 50 | 4 |
| `Enemy_CrossQueen` | 200 | 4 |

A constant force settles at `force / (mass * damping)`, so the same number moves the ship **60×**
faster than a common enemy and over **1000×** faster than the Queen. That is why the gravity well's
`force: 34` default looked completely inert on enemies while a value that felt right needed 9000 — and
why anything new that pushes or pulls bodies should compensate by `mass * damping` rather than shipping
a raw number. `ForgeGravityController.Compensation` is the reference: it normalises to a common Cross
(5 × 6 = 30) so `force / 30` is the speed everything gets dragged at.

Related trap on the same feature: **the ship is on the `Player` layer, not `Entities`.** An overlap
filter of `Entities, Fruits` silently excludes the player, which is how `pullPlayer` came to be dead
code that no amount of force could revive.

### Shields, dashing, and how anything actually takes contact damage

Researched for the shield-arc / ram-shield weapons. Almost every intuition here was wrong first time.

**`Shield` is a damage filter, not an obstacle.** `DamagableResource` instantiates one `Shield` per
`Unit.ShieldData` (which `AddShieldEffect` appends, so shields come from *modules*), and
`TakeDamage(Damage)` walks them **back to front**, each one absorbing until `damage.amount <= 0`.
`Shield.TakeDamage` charges `damage / effectiveness` out of a `ResourceTank`, so **`effectiveness` is
damage absorbed per point of resource** — and `effectiveness: 0` only earns a `Debug.LogError`.
`IsActive` is purely `Charge > 0`.

- **The player's shields have no collider at all.** All four ship prefabs ship `shieldHasCollider: 0`
  (and `firstShieldSize: 4`); most enemies ship `1` with `firstShieldSize: 0`. So an enemy's shield is
  a real surface your lobbed shots pop against, and yours is an invisible damage filter. That
  asymmetry is deliberate — see the layer note below.
- **Radius is not authorable.** `Setup` is called with `firstShieldSize + index`, so stacked shields
  are always exactly 1 unit apart, and `ColliderRadius` is `radius * 0.25 + 0.1`.
- **The shader has no arc parameter** (`_Radius`, `_RingAlpha`, `_InnerAlpha`,
  `_InnerGradientWidth`, `_BackgroundEnabled`, `_BackgroundScrollSpeed`). A wedge cannot be had by
  configuring the stock shield; it needs its own art.
- **Only `PhysicsProjectile` knows what a `Shield` is** — `OnTriggerEnter2D` does
  `TryGetComponent<Shield>` and impacts on the surface at `ColliderRadius`. Plain `Projectile` has no
  such branch; it just hits the trigger collider like any other and resolves the unit through
  `GetComponentInParent<IProjectileListener>()`.
- **The shield prefab sits on layer `Default` (0)**, and `PlayerProjectiles` collides with
  `Default`, so an enemy shield stops your bullets. ⚠ **Corrected 2026-10-01:** this line used to
  say `EnemyProjectiles` does NOT collide with `Default`. The decoded matrix (below) says it does.
  The conclusion survives for other reasons: the player's shields have no collider
  (`shieldHasCollider: 0` on all four ships and all five drones), and an enemy's own shield is
  skipped by the friend check. Enemy fire still only ever reaches the player through the damage
  pipeline.
- **★ THE LAYER COLLISION MATRIX, DECODED 2026-10-01** (`ProjectSettings/Physics2DSettings.asset`,
  32 rows of 4 little-endian bytes; the exporter prints the top byte oddly as `/f`, but that byte
  is layers 24–31, all unused). It matches both beam masks bit for bit (player 9344, enemy 9280),
  and a plain bullet's mask IS its layer's row (`Physics2D.GetLayerCollisionMask` in
  `Projectile.Shoot`):

  | row | collides with |
  |---|---|
  | 6 Player | Default, 3, Entities, EnemyProjectiles, Ground |
  | 7 Entities | Default, 3, Player, Entities, PlayerProjectiles, Ground |
  | 8 PlayerProjectiles | Default, 3, Entities, Ground, Fruits — **not Player, not itself** |
  | 9 EnemyProjectiles | Default, 3, Player, Ground, Fruits |

  **So nothing of yours, bullet or rocket, can touch your ship or anything on the Player layer**:
  the drones, wingman drone bodies and drone-gadget minions are all on layer 6. Your rockets DO
  stop on swarm-gun minions, which are enemy prefabs on Entities. The suicide drone's root is on
  layer 8, so it cannot touch you and enemy fire cannot touch it. No game code changes the matrix
  (no `IgnoreLayerCollision` / `SetLayerCollisionMask`), and `m_Include/ExcludeLayers` are 0 on
  the rocket, drone and ship prefabs.

**The PLAYER'S SHIP does no contact damage — but plenty of enemies do.** (This entry used to say
"nothing in this game does contact damage to anything else", which was wrong and was repeated into
two other files. `OnHazardTouched` was sitting in the list of `HealthBase` entry points the whole
time; that *is* contact damage.) There are two systems:

- **`Hazard`** — a plain MonoBehaviour with `OnCollisionEnter2D` *and* `OnTriggerEnter2D` that hands
  its `damage` to any `IHazardSensor` (which `HealthBase` implements), plus a `contactPushbackForce`.
  **Twelve of them**: `Unit Bouncer Red` (2 dmg / 30 push), `Bouncer Worm` (5/30), **`Unit Fly Alfa`
  and `Fly Zapper` at 0 damage / 30 push** (pure bumpers), `Enemy_Turret_Bouncer` (5/30),
  `Fruit White` (5/30), `Enemy_Fish` (3/20), the four beak enemies, and `Ammo Dandelion`. Note it is
  **Enter, not Stay** — resting against a hazard does not keep hurting.
- **`ChargerHead`** — Larva, Child, Maggot, Swimmer Maggot. Uses no collision callback at all: a swept
  `CircleCast` along `rigidbody.linearVelocity` every `FixedUpdate`, rate-limited by
  `minDamageDelay`, pushing the victim by `pushForce` and recoiling itself by `pushBackForce`. The
  `Hazard` it references is a **damage container only**, which is why the beak GameObject has no
  collider on it.

Still true: there is **no `OnCollisionStay2D` / `OnTriggerStay2D` anywhere in the assembly**, and
flying into an enemy is free *for the player*.

**A BUILD-TIME REGISTRATION IS FOR DATA, NOT FOR LIVE STATE — `contactDamage`'s world modes broke that
and armed the whole game.** `ForgeContact.Register` runs once per weapon FILE at startup. It used to
latch a single `static _world` and start the sweeper there, so the moment any file in the folder carried
`"who": "enemies" / "ship" / "all"`, every unit in every run was armed for the rest of the session
regardless of loadout — reported as *"I loaded Kamikaze swarm [which has no contactDamage block]. When
I dash into enemies they take damage now and die."* A gameplay rule was decided by a file existing.

Fixed 2026-08-31 by separating the two questions that were conflated in one property: **`WantsWorld`**
= "does the sweeper need to exist" (build time, fine) and **`Live`** = "should it act" (the config of an
*equipped* weapon). Notes worth keeping:

- **`WeaponBase.Equip` + `Dispose` are the whole equip signal.** Equip is called from exactly
  `Shooter.SetWeapon` and `WeaponBasedActiveModule.Activate`, so one patch pair covers primary,
  secondary AND gadgets with no per-frame cost and no module-grid walking. `Live` re-validates against
  `Owner` so a weapon that vanishes without a Dispose cannot leave the rule stuck on.
- **Unequipping must REVOKE, not merely stop.** Those grants are permanent by design (`expiresAt` 0),
  so without `RevokeWorld()` the rule persisted on everything already alive and the weapon looked like
  it had no off switch. The `fromWorld` flag is what stops a revoke from also tearing off a marked hit
  or a lunge's own contact window, and the tint layer has to be released by hand or the colour sticks
  for the run.
- **`who: "marked"` was never affected**, and that is the diagnostic: it always asked
  `TryGet(source.TemplateData)` about the weapon that dealt the damage. The world modes were the only
  ones with no "which weapon is this?" question.

**EVERY UNIT RIGIDBODY USES DISCRETE COLLISION DETECTION** — all 65 of them; only ammo prefabs use
Continuous — and the fixed timestep is 0.02s. So a body moving faster than ~50 units/s **skips a
1-unit object between physics steps**. A `pushForce` of 220 on a common Cross (mass 5) is 44 u/s, and
`slow`'s knockback *halves* the mass, taking it to 88 u/s — 1.76 units per step, further than an enemy
is wide. **So anything that detects an impact with a collision callback silently misses the hardest
hits**, which is precisely when it matters most. `ChargerHead`'s swept cast is the game's own answer;
`ForgeContactController` copies it, and `slam.swept` offers it (off by default).

**`ImpactDamage` is self-damage, not ram damage.** It reads `collision.relativeVelocity ·
contact.normal >= minVelocity` and then calls `this.damagableResource.Damage(...)` — a
`[SerializeField]` pointing at *its own* unit. It exists on exactly five prefabs: the four ships
(`minVelocity: 10`, `amount: 1`) and `Enemy_Fly Variant Drone Suicide` (`minVelocity: **0**`,
`amount: 30` — which is the whole suicide mechanic: it kills itself on any contact and its
`spawnOnDeath` explosion is what hurts you).

**And on the ship it is switched off almost all the time.** `ShipMovement.SetBoosted` does
`impactDamage.enabled = boosted`, and `Awake` calls `SetBoosted(false)`. So crash damage applies
**only while sprinting**. Consequences: being flung into a wall (by a gravity well, a tether, or
anything else) costs a normal-flying player nothing, and any "ram" feature that wants the player to
survive its own impact must suppress that component rather than add invulnerability.

**Dash and sprint are the same button and both are already public.** `ShipInput` calls
`ShipMovement.Dash()` on the press and holds `dashHeld` while it is down; `Update` promotes a held
button to boosting after `boostStartDelay`. **`DashStarted`, `DashEnded` and `BoostStarted` are
public `Action` fields** — subscribe with `+=`, no Harmony needed. There is **no `BoostEnded`**; poll
the public `IsBoosted` for that. `Dash()` costs `dashCost` fuel, is gated on `dashCooldown`, and fires
along `flyDirection` — **the movement stick, not the aim**. Taking damage cancels a boost
(`OnDamaged` → `StopBoosting`).

**`ShipMovement.boostImpactDamage` is a dead field** — public, serialized, read by nothing. The same
shape as `destroyVelocityThreshold`: the intent ("boosting should hit harder") was designed and never
wired up.

**Official invincibility hook: `DamagableResource.damageBlockers`.** A `public List<GameObject>`;
`Damage(float)` early-returns when the list is non-empty **and every entry is non-null and
`activeInHierarchy`**. So one dummy GameObject toggled on and off is a clean i-frame switch — but it
blocks *all* damage, not just impacts. (`IsInvincible` is a different thing: it floors the tank at 1
rather than preventing damage.) Note also `iFrameDuration` already rate-limits every unit's damage.

**Hitscan cannot be blocked by geometry on `Default`.** `HitscanWeapon` casts against
`WeaponData.layerMask`, and every enemy laser ships `9280` = `Player | Ground | Fruits`. Nothing casts
against `Default`. Blocking a beam therefore means patching the cast — and remember the damage
raycast and the visual raycast are separate, so both need it.

**Damaging terrain exists but is rare:** `CellType.contactDamage` is read by
`HealthBase.OnCellCollision`, and exactly one cell type (`CellType_Hazard`, amount 1) has a non-zero
value.

**Cell fire thresholds are NOT normalised, and assuming they were made a feature look broken.**
`CellType_Solid` — the main terrain — ignites at **45**, while everything soft is in the low single
digits: Slime and Blob 1, Tuff 1.3, Moss 2.5, Night 5, Bark and Gold 10. The three `Fog` types are 100
(effectively fireproof) and `Hazard` / `Mud` / `Obsidian` / `Panel` / `Shard` are **0**, which
`BurningUpdateJob.IsOnFire` treats as *never burns* (it requires `threshold > 0`). A burning cell is
also topped up every frame by each burning neighbour, so anything that wants to *extinguish* terrain
has to beat both the level and the spread — `gas`'s `extinguishTerrain` at 6/second doused Slime and
lost outright against Solid. Note `Level.SetBurnLevel` **removes** the cell's entry at 0 rather than
storing a zero, which is what fully puts it out.

### The status feed is not an upsert

`ShipLogOutput.Log(id, ...)` looks like "replace the line with this id". **It is not, and the idiom
`Clear(id); Log(id, ...)` floods the screen.** Three parts:

- **`Log` always APPENDS** a new `ShipLogEntry`. There is no replace-by-id anywhere in the class.
- **`ShipLogDisplay.Show` then `Instantiate`s a fresh `LogEntry` row** *and* plays `newMessageSfx`.
  Every log call is a new row and a new chirp.
- **`Clear` does not remove the row promptly** — it fires `LogRemoved` → `LogEntry.Hide()`, which is
  **`async void`**: a close animation, destroyed later.

So any text that changes often (a stack count, a seconds countdown) stacks rows on top of ones still
fading out. That is exactly how the buff indicator came to cover the left of the screen and chirp
continuously in a group fight.

**And `endsAt` expires nothing.** It feeds the "seconds left" label; the feature that showed a line
is the only thing that clears it. For a line with no state behind it (a rank up) use
`ForgeBuffHud.Notice(key, colour, text, stacks, seconds)`, which is swept by a clock object that
exists only while a notice is up. R19 252's permanent "RANK n" line was a `Show` with an `endsAt`.

**The "unchanged text" short-circuit has to know the style.** `Show` skipped a repaint when the
text matched *and the icon widget existed* — which the `feed` style never builds, so under it every
call re-fed an unchanged line. `Painted(e)` asks the right question per style now (both mods).

**Rewrite the live row's text in place instead.** `ShipHud.logDisplay` is public; only
`ShipLogDisplay.logEntries` (`Dictionary<ShipLogEntry, LogEntry>`) is private. `ShipLogEntry` is a
**class**, so it keys by reference — and `Log` does not hand it back, so subscribe to
`LogOutput.LogAdded` and cache `id → ShipLogEntry` yourself. Wrap the text in
`<uppercase>…</uppercase>`, which is what `LogEntry.Display` does. Keep a hard line cap anyway: the
status area sits beside the ammo / fuel / health readouts.

**Related UI trap, same feature:** a borrowed prefab brings its own `RectTransform` size, and a
`HorizontalLayoutGroup` with `childControlWidth/Height` **false** (the default) ignores a child's
`LayoutElement` entirely — so there is no sizing at all, not merely bad sizing. Set `childControl*`
true *and* add a `LayoutElement`.

### Trails

- **There is no `TrailRenderer` anywhere in PUNK**, and no prefab enables Unity's Trails module. Every
  trail is a child **ParticleSystem** in **World** simulation space emitting by `rateOverDistance`.
- **WHICH IS WHY `fade`'s "trail and glow included" WAS A LIE FOR MONTHS.** `ForgeFade.Collect`
  walked `GetComponentsInChildren<SpriteRenderer>` when `everything` was on - and `ammo_Popper`, the
  most-cloned template in the mod, carries **one** SpriteRenderer (the bullet) and **three**
  ParticleSystems (`particle`, `particle_glow`, `trail`). So the flag walked a set of one while the
  build log said the trail and glow were included. That is his *"I want EVERYTHING regarding the
  projectile to fade, not some alpha."*
  > **"Include the children" is only a coverage claim if the children are the same KIND of thing.**
  The fade now scales each system's `main.startColor` alpha from a captured base. Two details:
  **both ends of a TwoColors range are scaled** (assigning one colour would flatten the range, the
  same trap as writing a particle's `startSpeed` instead of its multiplier - `particle_glow` is
  TwoColors, `trail` is Color), and **gradient-mode systems are skipped and SAID** rather than
  skipped quietly, because scaling a `Gradient` means rebuilding one per system per frame per shot.
  Particles already emitted keep the alpha they were born with, which is right - a streak behind the
  shot is in the world and should not retroactively change.
- Only four ammo prefabs ship one, so `trail` clones the Popper's onto whatever you asked for.
- **A fresh `AddComponent<ParticleSystem>()` gets the wrong default material** — always clone a stock
  donor instead.
- Trails use `scalingMode: Local`, so they do **not** scale with a growing shot.

### Audio

- **Not FMOD.** Plain Unity `AudioClip` / `AudioSource`. `AudioDatabase.sfxs` is a public
  `List<Sfx>` and `AudioManager.PlaySfx(guid)` linear-scans it, so a custom sound is registered as a
  genuine `Sfx` rather than played out-of-band. That's what gets 3D position, the volume slider, the
  mixer and voice pooling for free.
- **THE GAME HAS 259 SOUNDS AND UNTIL 2026-09-02 ALL OF THEM WERE UNREACHABLE BY NAME.**
  `Sfx` has both a `guid` and a readable hierarchical `name` (`Cells/Fuel`,
  `Weapons/Popper/Shoot`, `UI/OK`), and `ForgeSfxRegistry.Find` matched **`s.guid` only** — so a
  stock sound could be asked for solely by typing a 36-character guid, and nothing in either repo
  mapped a name to one. The builder page and the how-to both documented this as a limitation
  (*"the lookup compares GUIDs only, so typing Weapons/Bolt/Shoot is silence"*), which is why it
  survived: **a limitation that is written down stops being read as a bug.** It surfaced when he
  asked "what is the SFX for popping the bubble that provides fuel?" — a question with no answerable
  form. `ForgeSfxRegistry.StockGuid` closes it (custom first, then stock, same order every sprite key
  uses) and **SOUNDS.txt** in both repos is the generated list.
  Exactly the shape of the seven sprite keys that could not see stock art: **a lookup that can only
  search half its namespace.**
- **70 of the 259 entries are declared with NO CLIP.** They resolve perfectly and play nothing —
  `Items/Resources/Fuel`, `Weapons/Popper/Impact` and 68 others. Silent-fallback family; both
  resolvers warn now.
- **`rally.sfx` was the one sound key in the mod that resolved nothing at all** — it stored the raw
  string and `ForgeRally` handed it to `PlaySfx`, so only a raw guid ever worked and no warning was
  printed either way. Every other key goes through `ResolveChargeSfx`. Found by auditing the keys for
  SOUNDS.txt rather than by a test.
- **The "bubble that provides fuel" is `Cells/Fuel`, and it belongs to `CellType_Blob`** — whose drop
  table is a `FuelPickup`. The sound's name and the cell's name do not match, which is the whole
  reason it could not be found. 10 clips. `Resources/Fuel` is a different thing: the 30-clip *collect*
  ladder.
- **Inherit `mixerGroup` from the sound you replaced**, or the clip bypasses the in-game volume slider.
- **Enemies already play a hit sound, but you cannot find it by grepping code.** **83 damageable
  prefabs** (97 `SfxPlayer` components) wire `HealthBase.onDamage` to `SfxPlayer.Play` as a
  **UnityEvent in the prefab**, so it appears at no `PlaySfx` call site. When asking "does the game do
  X", **search the prefab YAML as well as the decompile** — this exact trap produced a confidently
  wrong "the game has no impact sound" once. What is genuinely missing is a *per-weapon* impact sound
  (the existing one belongs to the victim) and any sound at all for hitting terrain. Hence `impactSfx`
  is a patch (`ForgeImpactPatch`) with an `impactSfxOn` filter, not a data swap.
- **That hit sound is ONE shared `Sfx`, and the player is not one of its users.** Every one of the 83
  points at the same guid — `Enemies/Hit`, volume 0.22, `is3d`, **a single clip with no variation** —
  and the list is not just enemies: crates, boxes, fruit and the Dummy too. All four ship prefabs
  carry **no `SfxPlayer` at all** and route `onDamage` to `Ship.OnDamage`, so this sound is
  unambiguously a *hitmarker* and changing it can never affect the sound of the player being hurt.
- **The hit sound has NO throttle.** `onDamage` fires only when damage really lands, which looks like
  a gate until you check `iFrameDuration` — it is **0 on all 84 damageable prefabs**. The only
  limiter is the `Sfx`'s own `repeatMinDelay: 0.01`, and `AudioManager` keys that off the **Sfx
  object**, so it is a *global* 100/sec cap rather than a per-victim one. A shotgun therefore plays
  one tick per pellet that connects, and a piercing beam one per enemy per damage tick.
- **`AudioManager` never writes `audioSource.pitch`, and its sources come from a pool.** So the first
  feature to set a pitch silently detunes whatever borrows that source next. Anything that sets pitch
  must write an explicit pitch on **every** play — its own where wanted, `1f` everywhere else.
  `ForgeHitSoundPatch.OnPlaySfx` is the reference. (Volume needs no such guard: `PlaySfx` assigns
  `audioSource.volume = sfx.volume` on every play.)
- **`ForgeKillsPatch.Source` is live during a victim's `onDamage` event.** It is set in a *prefix* on
  each of the three damage routes and cleared in a *finalizer*, and `onDamage.Invoke()` happens inside
  the original method body — so anything reacting to a victim being damaged can ask "which weapon did
  this?" without adding a patch, and gets `null` for a burn tick, a gas cloud or a cell collision.
- Anything hooked at a per-frame collision point **must** be rate-limited: a piercing shot re-enters
  `Projectile.OnObjectHit` every FixedUpdate it overlaps something.
- The two *held* slots must be forced to loop, because the game stops them by handle.

### Loot

`LootDropper` → `DropTable` → `DropTableWeightedGroup`. Only **8 of 64** tables can drop a module, and
there are **5 live pools**. `useGroup` stays serialized even when false, so **many tables look live but
are inert** — check it before believing a table matters. The **Tech** pool holds ~5 entries against
White's ~18, so that's where a custom weapon has the best odds. Green crates always drop one
`Module BoosterCore` as a *fixed* item, not a roll.

**A CONFIRMATION PRINTED AFTER A REFUSAL READS AS THE REFUSAL BEING OVERRULED.** Found in his R15
log, in Module Forge and then in the identical code here:

```
[Warning] PiercingTest1.json: "lootFrom" was set but this module is not loot-enabled, so it can
          never drop.
[Info   ] PiercingTest1.json: drops only from DropGroup Modules Crate Caps
```

The second line is the more confident of the two and it is the one a reader believes - so he came
back asking whether crate targeting had ever been built, having tested it against a file the mod had
already refused. The summary line now says which of the two states the file is in. (Worth recording
too: **the captured entry asking to "re-verify loot and target a specific crate" was stale** -
`lootFrom` is fully built in *both* mods, across C#, page and txt, and he had tested it. He said so
and he was right.)

Module Forge does the same grafting, so the two mods coordinate through **one shared string**: the
created Money pool is named `Forge Modules Crate Money` in *both*, and both check `TableAlreadyRolls`
before appending. Rename it in one mod only and a player with both gets two module rolls per Money
crate.

### `wingman` — the only weapon this mod builds at RUNTIME

Everything else here is built once at startup from a named template. A wingman gadget's companions
fire a **clone of whatever weapon you are carrying**, so its weapon does not exist until you pick one
up, and is rebuilt when you pick up another. Four things about that are load-bearing.

- **The game does a third of it for free.** `WeaponBasedActiveModule.OnContainingClusterRefreshed`
  already does `WeaponFactory.Create(this.WeaponData, cluster.ConnectedAndPoweredModules)`. Hand it
  our clone and "weapon modules slotted around the gadget upgrade the copies and not your gun" needs
  **no code at all**.
- **`ApplyBehaviourFeatures` exists so the chain can be re-run.** The ~50 `Apply<Feature>` calls were
  inline in `BuildModule`; they are a method now with exactly **two** legal callers — the startup
  pass and `ApplyFeaturesTo`, the public door for this feature. `applyordertest.py` pins the call
  list, the order, and the fact that there are two call sites, because **nothing about that order is
  arbitrary and none of it is enforced by the compiler** (homing must follow wave, split must follow
  every prefab-cloner, lunge must follow charge…).
- **Inherit BEFORE applying.** `ForgeWeaponAlias.Inherit` copies the source weapon's registrations
  onto the clone, then the gadget's own blocks run over the top — so "the gadget wins on conflicts"
  is one line of ordering rather than a resolution pass.
- **A clone is a real asset, a real prefab, and ~26 registry entries**, and those registries are
  deliberately never cleared at run entry. Without `ForgeWingmanWeapon.Release` / `Reset` this
  feature leaks one of everything per weapon swap for the whole session.

**THE RECURSION ARRIVED THROUGH THE GENERIC DOOR.** `ForgeWeaponAlias` finds registries by their
SHAPE — a static `Dictionary<WeaponData, T>` — rather than from a hand-written list, for the same
reason `ForgeDiagnosticGates` sweeps by naming convention. But `ForgeWingman`'s own registry *is*
that shape, so the sweep found it, and inheriting it would have made every clone a wingman weapon
whose wingmen build wingmen. It is in a `NeverInherit` list now.

> **A feature that makes a weapon SPAWN A COPY of a weapon must not be inherited by the copy** — and
> a shape-based sweep will find such a feature without being told, which is the point and the danger
> in one. Two other registries are `CopyNotShare` rather than shared, both measured:
> `ForgeLeech`'s Config holds the per-second healing budget, and `ForgeCatalystNode.Cull` counts its
> node cap by **config reference identity**. `wingmanaliastest.py` asserts every registry in the
> assembly is classified as swept, copied or excluded, so number 27 cannot land in none of them.

**Beams turned out cheap, and the reason is one word:**
`HitscanWeapon.visualsInstances` is an **instance** field, not static. So one `WeaponBase` per
wingman — all built from one shared cloned `WeaponData` — gives each its own beam quads, and the
feared "N companions fighting over one beam" cannot happen. What remains is that
`HitscanWeaponVisual` is driven only from `OnBarrelMoved`, which only `Shooter.Update` calls, so the
controller calls it per wingman in `LateUpdate` (after motion, or the beam draws from last frame's
position). Same by-hand work as `ForgeShooterQuiet`.

**A wingman file still needs a `template`** — `WeaponBuilder` refuses outright without one — it is
simply never fired; it supplies the gadget shell. I wrote "the only weapon file with no template"
into the how-to and the builder page before checking, and caught it only when writing the test file.

Both of the things this section used to list as unbuilt have since shipped: **save/load persistence**
(2026-09-13, R17 tests 220–222, below) and **damageable drone bodies** (2026-09-20, R19 tests
258–268, below). `body: "drone"` is real and no longer falls back to an orb.

#### What R16 actually found — the core was fine and everything around it was not

Nine defects in one evening of play, and **not one of them was in the four load-bearing things
above.** The clone, the inheritance sweep, the re-runnable feature chain and the per-wingman beams
all worked on the first try. Test 204 was written as a ladder gate that would stop the round if the
clone was broken; it passed. The bugs were all in the ordinary code around it, and the pattern is
worth keeping.

- **Three of five `key` modes could not put a companion on screen.** `burst` fired a volley over an
  empty list, `recall` returned "nothing to do" on an empty list, `toggle`'s first press stowed a
  complement that had never been spawned. **Every handler was written for a state only one other
  handler could produce**, and each one reads as correct in isolation. A switch whose cases share an
  unstated precondition needs that precondition to be one function (`Config.AutoDeploys`), not five
  assumptions.
- **A deny-list on borrowed game prefabs is a promise you cannot keep.** `Disarm` named seven
  component types to strip — Projectile, Pickup, Shooter, AIAgent… — and covered everything anybody
  had thought of. `FuelPickup` also carries `ParticleLifetime`, which shrinks the sprite over three
  seconds and then **destroys the GameObject** at a 5–7s lifetime. It is now an ALLOW-list: every
  MonoBehaviour goes except two that provably only set a sprite. *Losing a flourish is visible;
  keeping a self-destruct is not.*
- **The cache never missed, so it never rebuilt.** The built copy was keyed on the *clone*, and
  installing a module does not change which clone you are copying — so "modules on the gadget
  upgrade the copies" only took effect on re-equip, and the stale result looked like an inconsistent
  one. Any `ModuleCluster.RefreshPoweredModules` now bumps a generation counter the controller
  compares. It has to be **any** cluster: a module beside your PRIMARY must rebuild what a GADGET's
  companions fire, and those two clusters have no relationship the game exposes.
- **A module's effect writes to the built `WeaponBase`, never back to the `WeaponData`.** So a clone
  of the asset is your gun *with none of your grid on it*. Passing the source weapon's cluster
  modules to our own `Create` is what makes "+1 projectile on your own gun reaches the copies" true.
- **`mirror: "both"` answered "which gun?" from the CONFIG when it is a property of the SHOT.** It
  returned `primary ?? secondary`, so right-clicking fired copies of the left-click weapon. The
  fired `WeaponBase` is carried down from the hook now, and each wing caches one built weapon per
  source.
- **Two arithmetic bugs, both invisible from the symptom.** The orbit ring was spaced by the CAP
  rather than by how many were deployed *and* never re-spread after a change; and `atRest: relax`
  had each bead round to its own nearest even slot, which is right for one bead and wrong for a set —
  `drag` bunches them inside `spread` degrees **by design**, so three beads inside 55° all round to
  the same slot and stack. **That bug could only appear in the state the feature exists to
  produce.** Both are re-implemented and proved in `wingmanfixtest.py` rather than eyeballed.
- **`OnUninstalled` is handed a `Unit.Data`, not a `Unit`**, so it could release the weapon clones
  and not reach the bodies — pulling the gadget left the companions flying and firing. The
  controller keeps a static list of itself and is found by config.

#### Save/load persistence — built 2026-09-13, R17 tests 220-222

A plain-text sidecar in the save folder, written from a postfix on `GameSaver.Save(string)` and read
from a postfix on `GameSaver.Load`, keyed `(EntityData.instanceId, weapon file name)`. Only the
deployed **count** and a stowed flag are stored; the bodies are rebuilt from the JSON, which sidesteps
the "spawn-time state silently reverts" trap. Four things worth carrying:

- **`ModuleGridOwner.Update` is the only per-frame seam that hands you a LIVE `Unit`.** Every module
  lifecycle callback — `OnInstalled`, `OnUninstalled`, `Module.OnUpdate` — takes a `Unit.Data`, which
  is save data with no transform and no components. Without this seam the restore would have ridden
  the `DoShoot` mirror, i.e. *"your companions come back when you shoot"*. (`EntityGameObjectManager
  .TryGetSavableEntity(instanceId, …)` is the route back the other way, and is how
  `SpawnMinionModule` kills a minion from `OnUninstalled`.)
- **Reading the record has to be a POSTFIX**, because `ForgeBurnResetPatch` is a *prefix* on the same
  method that clears per-run state. Prefix → original → postfix is the only ordering that survives
  its own reset.
- **A save with nothing deployed must DELETE the record, not skip the write.** "Write a line for each
  gadget that has companions out" leaves yesterday's file intact to hand them back tonight.
- **The bug the randomised test found and reading did not.** The fallback for a missed key was "a file
  name appearing exactly once". In coop a player carrying the gadget with *none* deployed writes no
  line at all — so the other player's entry is the only one with that name, and one ship is handed the
  other's companions. **Absence of a record is not absence of a claimant.** Fallback is now
  single-player only.

**R17 part 2 found three more, and two of them are rules rather than bugs.**

- **A LIMIT READ ONLY WHERE THINGS ARE CREATED IS A GATE, NOT A CAP.** `ForgeWingmanController.Cap`
  is derived live from `Module.Level`, and it was read in exactly two places — `Deploy` and
  `DeployAll` — both of which only ADD. So pulling a BoosterCore lowered the cap and nothing acted
  on it: the extra companions stayed out until a key press happened to re-test the condition, which
  reads as a delay rather than as a gap. **The game's own code has both halves**:
  `SpawnMinionModule.Activate` refuses at `count >= Level` *and* `OnUpdate` enforces the same number
  downward by destroying the excess, every frame. `TickCap()` is ours. The general shape: a cap
  derived from live state (a level, a stat, a module) can move without anything passing through the
  gate, so the gate cannot be the only reader.
- **A REFUSAL HAS TO CONSUME THE THING IT REFUSES, OR IT IS A DEFERRAL.** `ForgeWingmanSave.Apply`
  returned on `!cfg.persist` and left the loaded record sitting in `_pending` — which `Write`'s
  unclaimed-record pass-through then wrote back out, unfiltered, at every save. So a sidecar written
  while persist was ON became **immortal** the moment persist went off: the restore would not claim
  it, the write would not drop it, and deleting the file by hand bought you until the next save.
  Same family as a skipping prefix that never stamps the fire-rate clock — the decision is made and
  the state that provoked it is left where it was, so it is made again forever. `Forget` drops it by
  **exact key only**; the single-match fallback is right when a miss costs a key press and wrong
  when it costs somebody else's record.
- **`persist: false` IS UNOBSERVABLE UNDER `none` / `burst` / `recall`**, because those auto-deploy
  the complement on every run entry (`Config.AutoDeploys`) — the restore correctly declines to hand
  anything back, and the auto-deploy fills the ring anyway. R17 test 222 asked for the setting to be
  proved on a `recall` file, so it could not have failed or passed. Now a build warning pointing at
  `deploy` / `toggle`. **A test whose subject is invisible in the configuration it specifies is not
  a weak test, it is not a test** — and the tell was in his own words: a restore replays a *saved*
  number, so a count that tracks edits to the JSON is the auto-deploy, not the restore.

**The HUD count is the game's own widget.** `MinionsWidget` already instantiates a
`MinionCountWidget` for all five connection slots at `Start` and hides the ones whose grid position
does not hold a `SpawnMinionModule`. A postfix on `UpdateWidget` supplies the case it does not know
about; `MinionCountWidget.SetIcon` takes a base `Module`, so it needs no coaxing. **The widget keys
off the module's TYPE, not its template** — so his guess that switching to a drone template would
bring the count with it would not have worked.

#### Damageable drone bodies — built 2026-09-20, R19 tests 258–268

`body: "drone"` spawns a real `Unit` per companion (`ForgeWingmanBody`) instead of a phantom orb.
The motion, aim, mirror and beam code is untouched — a drone wingman is the same ring with a
different kind of body — and `Owner` stays the player, which is what makes kill credit, friendly
fire, `leech`, `surge` and `rank` need no special case.

**★ THE WHOLE FEATURE IS A QUESTION ABOUT LIFETIMES, and the answers differ per thing.** A body has
three: the GameObject (dies on load), the `Unit.Data` / `EntityData` (survives a rebind, dies on
unload — `destroyWhenUnloaded: 1`), and the module grid. Getting the split right is most of the code:

- **The module GRID has a memento, so the game restores it — the STAT EDITS do not.**
  `Module.Memento.Restore()` `DeepCopy()`s the module data out of the registry, so a restored module
  carries the *asset's* effects rather than ours. So on a re-dress the augmentations must NOT be
  installed again (they would stack, and again next load) while the health/resources/shield edits
  MUST be re-applied. Opposite answers, both falling out of what carries a memento.
- **`Unit.Data.Memento` stores `resourceValues` for every non-shared tank**, so a companion saved on
  12 health comes back on 12. That is the entire reason drone bodies persist through the game's save
  system rather than through the sidecar count — and it is why `ForgeMinionStats.Recalculate` grew a
  `refill: false` overload. Recalculating with a refill right after a load throws the restored value
  away and **the feature tests as working**.
- **The sidecar must not also store a count** for a drone gadget, or you load with double. It stores
  only the `stowed` flag, which is a fact about the gadget that nothing in the game's save records.

**AND THE TWO THINGS A POST-BUILD AUDIT FOUND, both silent, neither reachable by the round's own
test files.** Worth keeping because they are the same shape as each other:

- **The auto-deploy destroyed the save it was restoring.** `Ensure` builds the controller and then,
  for the three `AutoDeploys` key modes (`none` / `burst` / `recall`), calls `OnKeyless` on the same
  line — so `_wings` was empty, `DeployAll` spawned a fresh complement, and `TickAdopt` then found
  the ring at its cap and TOOK every body the game had restored. Right count, right ring, saved
  health silently gone. Both shipped R19 drone files use `key: "deploy"`, so the round as written
  could not fail on it; `R19_DroneSentry.json` and test 268 exist for that path.
  > **A number is only a fact once whoever owns it has been asked.** `OnKey`/`OnKeyless` now
  > `Settle()` (adopt) before reading `_wings.Count`, which also closes the toggle race.
- **The stock cap-trim fix was only half a fix** — see the `Die()`-is-deferred bullet under *Drone
  gadgets and minions*, which is the generalisable half.

Smaller things the build settled, each a silent failure if got wrong: a kinematic `Rigidbody2D`
reports `linearVelocity` of exactly **0**, so `drone.contactDamage` had to be fed a position delta
(`ForgeContactController.drivenVelocity`) or it would have done nothing while logging perfectly; the
stock drone's `spawnOnDeath` is a real burn explosion at radius 2 and the ring sits at 2.2, so it is
stripped unless `deathBlast` asks for it, and it lives on **two** components (`DamagableResource`
and a private field on `DestroyWhenResourceDrained`); a recall uses `Destroy`, never `Die()`, because
a recall is not a death; and `PhysicsProjectile` has no friendly-fire skip at all, so a lobbed or
rocket wingman was believed to detonate on its own squadmates (`ForgeWingmanShotPatch`, scoped to
your own bodies rather than applied globally so enemy rockets keep their current behaviour).
⚠ **Corrected 2026-10-01: it could not.** That premise ("`PlayerProjectiles` collides with
`Player`") was never measured. The decoded matrix (Shields section) says your shots never touch
layer 6, where the bodies are, and a drone's shield has no collider. So the patch never fires. Its
once-per-run line is in no saved log, and Sugarheady confirmed from play that your own rockets
never hit your ship. It is harmless and left in place. Delete it only as its own change, with its
pins.

**What R19 part 1 found about the bodies (2026-09-25):**

- **A drone's gun is its `Head`, a `BarrelTransform`** with `updateTransformRotation` on — its
  LateUpdate writes the head's WORLD rotation from `Direction`, which defaults to due east and is
  only ever set by the AI's aim actions. With the AI off it sat pointing right while the shots went
  where the ring aimed. The controller sets `Direction` to each companion's next shot every frame.
- **The health bar is one segment per point of capacity, in one endless row** (`ResourceBar`,
  `HealthbarOwner.maxUnitsPerRow` 0), so a 120-health companion wore a bar across the ship.
  `drone.healthBar` (`full` / `hurt` / `hide`) and `drone.healthBarRow` use the game's own two
  fields. ⚠ Re-registering a bar only after its `Start` has run (a non-empty `Resources`), because
  `HealthbarManager.Add` before `Start` would create a second widget under the same key.
- **`drone.trailColor` was a false promise.** It was a dead key whose warning said "use wingman
  color" — and nothing painted a drone body's trail from anything. It is a real key now (a colour or
  `"match"`); `wingman.color` was always the body colour.
- **`"aim": "none"`** — his idea: a ring that never fires, for a pure tackle ring. On an orb it does
  nothing (no hitbox) and says so.
- **Test 259 is a TRACE, not a fix.** Hurt bodies came back from an in-session continue and were
  lost after quitting to the desktop. Every line on that path was once-per-run, so the controller
  now logs per body (`wingman BODY -`): adopted at what health, removed by which rule, or vanished
  with its last health and whether its data is still owned. ⚠ `ForgeWingmanBody.Take` destroys the
  GameObject, and `SavableEntity.OnDestroy` destroys the entity data while the scene is loaded — so
  any rule that `Take`s a restored body deletes it from the save for good.

**What R19's close found (2026-09-27):**

- **★ A controller was found by CONFIG alone**, so two copies of ONE wingman file in two gadget
  slots were one gadget — one cap, one ring, `_cluster` flipping to whichever slot asked last, and
  a HUD row summing both (267). Two different files never showed it. **Per slot now, in five places
  that all have to agree**: `For` (cfg + ClusterType), `Remove` (by slot, then by module identity,
  else everything), `AnyFor` before releasing the SHARED weapon clones (released while another copy
  flies = companions firing a destroyed asset), `EachInSlot` for the HUD row, and the sidecar
  (**v2 adds the slot; v1 lines match any slot of the file**). `Connection()`'s grid-walk fallback
  also skips a slot another copy has claimed.
- **★ …and per-slot rings from one file were drawn ON TOP OF EACH OTHER.** Found writing R20's 267,
  not in play: same `radius`, same `spinSpeed`, both `_ringPhase` from 0 — drone i of the second
  ring sat exactly on drone i of the first, so six companions would read as three, **the very
  shared-cap bug the fix was for**. `PlaceOrbit` now asks `GatherSiblings()` (every live copy of
  this cfg on this unit with companions out, in SLOT order), follows the first copy's phase, and
  places each companion by `SharedAngle` — its rank on one shared circle, ordered by s/n then by
  copy, in integer arithmetic. A per-copy offset (`ordinal·step/copies`) was the first version and
  fails on 2-out + 1-out (both at 180°); `wingmanfixtest.py` keeps it as the counter-example and
  sweeps every mix of up to three copies. **Orbit only** — trail/drag copies of one file still
  stack; nothing has asked for two of those.
- **268 (always-out ring, duplicates flying on stock AI after a load) was NOT sourced** — no log of
  that session. Two things instead of a third guess: the auto-deploy **waits** (≤8 s after a load,
  `ForgeWingmanPatch.LoadSettling`) while any of its slot's restored companions is in the ship's
  minion list but not yet in the scene, and the trace grew two instruments — a census after the
  load window (adopted / new / never-appeared, with entity ids) and a subscription to the game's
  public `EntityManager.EntityDestroyed` that logs **the call stack that destroyed a companion's saved
  data**. Ruled out on the way: entity-id collisions (a continue re-seeds `RunArguments.seed`
  randomly, so `CreateInstanceId`'s sequence is new) and the owner restore (robust:
  `Unit.Data.RestoreFromMemento` → `SetOwner` → `AddMinion`).
- **`healthBar: "flash"`, `healthBarRows`, `healthBarScale`, `healthBarSeconds`** (`ForgeWingmanBar`).
  The widget is made late (Start, or first hit) and remade on every stats recalc, so the component
  looks it up through `HealthbarManager.healthbarWidgets` each frame. The fade is
  `Graphic.CrossFadeAlpha` — `CanvasGroup` lives in `UnityEngine.UIModule`, which this csproj does
  not reference, and CrossFadeAlpha's signature needs nothing from it.

### `rank` — the second feature that rebuilds a weapon at runtime

Built 2026-09-19. A gun banks the damage it deals as XP and gets stronger. Three things about it are
worth knowing before touching anything near it.

- **An unlock is a JSON edit plus a rebuild, not reflection into twelve config classes.**
  `ApplyBehaviourFeatures` is re-runnable with a public door (`WeaponBuilder.ApplyFeaturesTo`)
  because `wingman` needed runtime weapons. So a pooled feature is authored normally, switched OFF at
  build time, and drawn by setting `enabled: true` on a **deep clone of the file** and re-running the
  chain. `onRepeat` deepens the feature's own numbers in that same clone — which is why it costs
  nothing per feature and uses key names the author is already typing.
- **★ `ApplyRank` MUST RUN FIRST in the chain, and that is the whole mechanism.** It switches pooled
  blocks off, so anything above it reads them live and the reward is granted at rank 0 on every
  weapon that pools it. `applyordertest.py` pins the position.
- **Only re-run the chain when the JSON actually moved.** A rank that pays stats alone is a few
  dictionary writes into `ForgeWeaponStats`. That is what makes `maxRank: 9999` survivable, and it is
  also why the ONE feature family that cannot be pooled is refused at build time: `minion` / `turret`
  / `spawner` clone game assets when they build and keep a `_made` list, so each re-run leaks a set.
- **Repeats are computed from the AUTHORED value, never from the live one.** Percentages must SUM
  (three 20% draws are +60%, not 1.2 cubed), and a read-modify-write against its own previous output
  is the trap that made overheat's muzzle tint converge on fully-hot in a second. The authored values
  are captured at parse time, before anything can edit them.
- **The payout must be re-applied on `Equip`.** A `WeaponBase` is a fresh instance whenever its
  cluster is rebuilt, so a rank would otherwise evaporate the first time you slotted a module.

#### R19 closed, 2026-09-27 — rank is PER COPY now, and no unlock had ever worked

- **★ "A constraint rather than a choice" was wrong.** Progress used to be keyed
  `(ship instanceId, weapon FILE)` because the `WeaponBase` is rebuilt — true, but the **Module** is
  not, and `WeaponModule.WeaponData` / `WeaponBasedActiveModule.WeaponData` are **settable
  per-instance properties the game builds the gun from** (`ModuleSlotWeaponHolder.RecreateWeapon`,
  `WeaponBasedActiveModule.OnContainingClusterRefreshed`). So `ForgeRankCopies` gives each ranked
  Module its own WeaponData clone (+ forked prefab, + `ForgeWeaponAlias.Inherit`) just before the
  game builds the gun, and `TemplateData` identifies the copy from then on. R19 252/256: *"Spawning
  a new prospector ... gives me the old weapon with the old total experience,"* and two copies
  levelled in lock-step. **The game never duplicates a live Module** (every `DeepCopy` in the
  decompile starts from an asset), which is what makes the instance a safe identity.
- **★★ An unlock re-ran the chain and `ApplyRank` — FIRST in it — undid the unlock.** Its build-time
  job is to switch every pooled block OFF and register a fresh Config, so on an unlock's re-run it
  turned the drawn block straight back off before any Apply* read it and swapped out the Config.
  **No pickup or order unlock had ever reached a gun** (*"The orbs vanish but dont upgrade the
  weapons"*), past 122 shape assertions. `ForgeRank.Rebuilding` makes `ApplyRank` return;
  `rankcopytest.py` pins it by mutation.
  > **A step that must run first at build time must be asked whether it should run at all on a
  > re-run.** "Re-runnable chain" was true of the other fifty calls and false of the first.
- Unlocks edit **the copy's own JSON** (`Progress.json`), never `cfg.source` — editing the shared
  clone is how one copy's unlock would reach every copy. Rebuild the live gun afterwards
  (`ProjectileWeapon` copies its prefab in its constructor) — and **next frame** when it came from
  damage, because the rank up is inside the game's damage call and disposing a beam from inside
  its own `FireSingle` throws on the way out.
- **Stats go on the LIVE gun (`LiveGuns`), not on "the weapon that hit"** — the hit may have been a
  wingman's copy; `ForgeWingmanWeapon.SourceOf` maps a wingman's weapon back to the gun it mirrors.
- **Save v2** is one line per copy under *where it is*: `slot:<ClusterType>`, `vault:<index>`,
  `pickup:<entity id>` — read off the ENTITY data (ship grid, pickup components) and the Vault's list
  order, which is exactly what the save restores. v1 (per file) still loads onto the first copy
  of the file found.
- **The rank line was permanent** because `ForgeBuffHud`'s `endsAt` only ever fed the label; nothing
  swept it. `ForgeBuffHud.Notice(key, …, seconds)` is a line that ends itself, driven by a clock
  object that exists only while a notice is up.
- **Choices are module-style pickups (his pick):** the game's own `ModulePickup` prefab, cloned
  under an inactive holder and stripped by allow-list in `[RequireComponent]` order (Unity refuses to
  remove a required component and leaves it), keeping only the prompt UI and the `Interactable`, whose
  `OnInteracted` is re-pointed (persistent listeners switched off one at a time). Taken with the
  **use key**, never by touch; a key-press fallback takes the nearest in reach only when the
  `Interactor` has nothing else in range. Owed choices are saved on the progress.

#### R20 closed, 2026-09-29 — the choices, as a QUEUE

- **★ Two ranked guns put two sets on ONE ring**, drawn exactly on top of each other (same
  `Ring`, same slot angles), and he took the other gun's choice without knowing: *"i may glitched
  one instance of the weapon where it got the bonus of the other ranked prospector"*. That was not
  a leak between copies — it was two identical-looking sets. **One set per ship now**:
  `ForgeRankDraw.Owe` only counts and `ForgeRankQueue` (a clock object alive while anything is owed
  or out) decides what comes out — per ship, carried guns only, longest-waiting first, and a taken
  set sends its gun to the back of the line so two guns take turns. Each choice's last prompt line
  names the gun and slot (`SlotOf`: PRIMARY / SECONDARY / GADGET n / IN STORAGE).
- **★ The owed count is the only truth; a set on screen is a view of it.** R20 256: owed choices
  were gone after save and quit to the desktop, though the count was saved. Not sourced — the
  Equip-time `Resume` should have spawned them. Rather than guess a third cause, the queue brings a
  set out whenever one is owed and none is out, so ANY lost set comes back: after a load, after the
  ship was gone, after something nobody found. Gated on the run being 1.25 s old (stamped at run
  entry, never a per-scene clock), not paused, and the ship's flying controls being on.
- **Placement clear of walls and stations** (`Relayout` / `Blocked`): the ring turns in 30° steps
  and widens until every slot is off solid cells (the cell grid, the way wall riding reads it) and
  away from any `Station` collider; re-checked every 0.3 s as the set follows you; a jump past 30
  units (teleport, load) snaps instead of flying across the map.
- **The put-away key** (`ForgeRankKeys`): `[Rank choices] Key = H`, `PadButton = DpadUp` — both
  unused by `ShipControl` in the game's `InputActions` asset. Its OWN `InputAction` (reference to
  the game's `Unity.InputSystem`), never an edit to the game's PlayerInput. One press hides every
  set (still owed, still "out"), the next brings them back around the ship, re-laid. The card of a
  pick-your-choice gun names the key.
- **The start station pays no station XP** (his answer): `Station.PlayStartSequence` is called for
  it on a NEW run only (`GameController.StartGame`'s else branch), so a prefix names it; its id is
  a `start` line in the rank sidecar so a continue still knows it.

#### Across a New Game Plus jump, 2026-09-29 — `ForgeRankCarry`

Game Mode Forge's `NewGamePlus.dll` makes a new world and carries your grid and vault into it. A
jump IS a new run, so `ResetAll` drops every copy's progress — correctly. His ask: *"I definitely
would love to keep rank weapons level intact if possible."* So:

- **It is found, never referenced.** `ForgeRankCarry.OnRunEntry` (called from `ResetAll`) finds
  `GameModeForge.NewGamePlus.NgCarry` by type name and subscribes to its `Leaving` / `Arrived`
  events. With New Game Plus absent nothing happens.
- **It is the Continue path.** `Leaving` takes the save's own v2 lines
  (`ForgeRankSave.RecordLines`, now the one builder `Write` also uses); `Arrived` parses them with
  `ParseV2` and restores with `Attach`. New Game Plus restores each grid slot for slot and the vault
  in its order, so `slot:` / `vault:` still name the same gun; only ship ids change, and
  `NgCarry.NewShipId` maps them. `Arrived` fires after the new ships' grids are installed and before
  any gun is built — the same moment a Continue's Load postfix runs.
- **Two things do not cross**: a copy lying on the ground (`pickup:`) stays in the old world, and
  `paidStations` is cleared (those stations are gone; the new world's can pay again).
- The held lines live outside `ResetAll`'s list, and a normal run start drops any left over from
  a jump that never arrived. `rankcarrytest.py` pins all of it, and both sides of the contract.

**The game's own `Pickup` class could not be used for the choice orbs, and the design note said it
could.** Its shape is right — `OnPickedUp(Unit)`, a public `Rigidbody`, no gravity — but its
`FixedUpdate` **magnets itself to the nearest `LootCollector` and collects on contact.** Correct for
loot and fatal for a choice: all three rewards fly at you and the nearest takes itself, so there is
nothing left to choose. `ForgeRankChoice` is an ordinary trigger that trails the ship instead.

> **An abstract base matching your shape is not the same as matching its behaviour.**

**`Interactable.Activate(Interactor)` is the station hook** — public, firing a `UnityEvent` **wired in
the prefab**, so it appears at no call site in the decompile. Seven prefabs carry it. Same shape as
the 83 prefabs wiring `HealthBase.onDamage` to `SfxPlayer.Play`: search the prefab YAML too.

**⚠ A percent XP curve stops dead at a high `maxRank`.** `1.25^9999` is past float *and* double, so
the requirement becomes Infinity and ranking stops with nothing reported. `PercentCeiling` computes
the rank where that happens and the build log prints it.

**⚠ PICKUPS HAVE SAVED IDENTITIES TOO, so "has a `SavableEntity`" is not "is a place".** The
station hook sees every `Interactable`, and the first version's only filter was that — on the
assumption a pickup has no entity. All three do (`IngredientPickup`, `ConsumablePickup`,
`ModulePickup` persist in the level), so every pickup paid a whole station's XP (R19 251: *"I picked
up 2 fiber and got my weapon from 5 to 7"*), and because station XP pays every ranked gun that has
`stations` on, the guns jumped together and looked like one shared pool. It is an allow-list now —
the interactable must belong to a `Station` (both station prefabs; his ask was "either station").

**⚠ THE CHOICE ORBS WERE SPAWNING EVERY RANK, BEHIND THE TERRAIN.** A fresh `SpriteRenderer` lands
on sorting layer "Default", which in this game draws behind the level — the leech orb's own comment
says exactly that, and every other runtime sprite here borrows a donor's layer. The orb set only
`sortingOrder`, which orders within Default and changed nothing: R19 252 saw no orbs at all. They
borrow the ship's layer now, are collected by **distance** rather than a trigger (no dependence on
the physics layer matrix), spawn on their ring slot with a short arming delay (a distance check on a
body born inside the hull would choose for you), and log each set as it appears.

**⚠ `"source": "shop"` was never a valid value** — found while writing rank's presets. Four shipped
test files used it and every one quietly became a **starter** pick, under a comment describing the
unknown-value fallback as a safety net; `r19live.js` was asserting it too. The real values are
`starter` / `loot` / `starterAndLoot` / `none`, and an unknown one warns now.

### Drone modes — built 2026-10-01, untested in game

A key (Y / D-pad right) that steps every gadget's companions through NORMAL > ORBIT > DRAG >
PROTECTOR > REAR GUARD > SEEKER > SENTRY; Y + a gadget's own button steps that slot alone; U or a
one-second hold puts everything back. Design and every decision: `Docs/DRONE MODES - CONCEPT.md`; what
the build chose is its **section 14**. The facts that will bite:

- **The rules have no Unity in them, on purpose.** `ForgeDroneModeRules.cs` (cycle, steps, the
  act-on-release key machine, the shapes, INTERCEPTOR's shot-picking) is compiled and run by
  `Harness/dronemodeprobe.cs`. Keep `UnityEngine` out of it, or the probe cannot build. Change a
  rule there and nowhere else.
- **A mode belongs to a gadget SLOT of a ship**, and the slot ACTS on it only while the feature is
  on, the mode is in `Cycle`, and its companions can do it (`ForgeDroneModes.Effective`). Wingman
  orbs and bodies do the four formations (bodies also INTERCEPTOR); SEEKER and SENTRY are AI-drone
  only.
- **Every companion of a ship in one mode is ONE formation**, wingmen and AI drones together, so
  two slots on ORBIT make one ring (the R19/R20 test 267 trap). The roster is rebuilt once a frame.
- **The gadget button is held back by a prefix on `ModuleActivator.Update`**, not by binding
  anything: `IsActivated` is what every gadget binding sets. A combo is a press that BEGINS while
  the key is down; the slot stays latched until its button is let go.
- **AI drones (`ForgeDroneBrain`) are only those from a `SpawnMinionModule` slot.** A wingman body
  is a minion too (for saving and fast travel) and its slot holds a weapon-based module, so it is
  never steered twice. A formation suspends every active `MovementAction` each frame (Update AND
  FixedUpdate) and switches off every `MoveAroundOwnerAction` — **which sits in the Idle state as
  well as Regroup**. The gun is untouched. HandBack on normal, disable, destroy and the error path.
- **LOOSE is our push at the drone's own regroup speed, not its own mover**: the stock drone's
  movers differ ten-fold by AI state (idle 30–60, dodge 100–150, regroup 250–350). LOCKED sets
  velocity, capped at 45 u/s. Never write a mover's tuning mid-state (the snapshot trap); condition
  ranges (`OwnerIsWithinRangeCondition.maxDistance`) and `Vision.Range` are safe and are what
  SEEKER and SENTRY raise, restored on the way out.
- **Keys are read on ONE player's devices (`ForgeKeyBinding`)**: a ship's paired
  `PlayerInput.devices` in co-op, every device with one ship, only while that ship's
  `ShipControlActionMap` is enabled. Nothing is bound into the game's input. The rank key H uses the
  same reader since this build (per player, and gated on flying, which it claimed and did not do).
- **The alert is `ForgeBuffHud.Alert`**, which shows under any `[Buff indicator] Style`.
  `Notice` returns early under `Style = off`, **the default**, which is why rank's "RANK n" line is
  invisible to anyone who has not switched the indicator on.
- Saved in `forge-dronemodes.txt` beside the save (deleted when every slot is normal; read in the
  Load postfix), carried across New Game Plus through the `NgCarry` contract rank uses (SENTRY
  arrives on normal). "The gadget changed" is read off the module DATA id, which survives a load.

### Bloom spiral, staggered and bullet mode — built 2026-10-02, untested in game

`drift: "spiral"` / `"staggered"`, `pace`, and `"when": "bullet"` (the gun's own shot, a rocket
included, flies the bloom path). Design and every decision: `Docs/BLOOM SPIRAL - CONCEPT.md`;
what the build chose is its **section 7**. The facts that will bite:

- **The geometry has no Unity in it, on purpose.** `ForgeBloomPath.cs` is compiled and run by
  `Harness/bloompathprobe.cs` (via `bloompathtest.py`). Keep `UnityEngine` out of it. Change a
  shape there and nowhere else.
- **A path is a position, not a heading.** Re-aiming a tangent every step drifts OUTWARD by about
  pi x the step length per lap. That is how `spin` has always moved and files depend on it, but on
  a spiral's inner laps it is wider than the gap between the arms. Spiral (and a spin BULLET) aims
  at the next point on the path and goes through **`ForgeSteer.Drive`**, which does not cede the
  direction to a wave. So wave/homing/guided are overruled there, and the log says so.
- **Bloom's `Projectile.FixedUpdate` prefix is `Priority.Last`.** It is safe only because every
  other prefix on that method, and on `PhysicsProjectile.FixedUpdate`, returns void.
  `bloompathtest.py` asserts it. A bool prefix added there later changes what "last" skips.
- **Bullet mode switches the shot's range OFF and keeps it as placement.** It comes back as a
  flight budget only where there is no centre to end at. Lifetime is left to the engine.
- **`safetyDistance` is -1 on a bloom bullet.** Inside its safety distance a plain shot REFLECTS
  instead of dying (`OnObjectHit`), and the stock Popper re-hits with `damageRepeatDelay` 0. So a
  shot that appears inside an enemy would hit it every step. Anything that spawns a shot away from
  a muzzle needs the same thought.
- **A bullet leaves the path when its `idealDirection` moves** more than 2 degrees from the one
  bloom set. Homing, guided, chain, ricochet and the engine's `Reflect` all move it; wave does not.
- **`stagger` defaults DIFFERENTLY per drift**, through `Config.StaggerFor`: 0.5 on `staggered`,
  and on a `spiral` only when the file named the key (`staggerSet`) - a staggered spiral was added
  after spirals had shipped, and an unconditional default would have staggered every one of them.
- **★ A ROCKET'S OWN `FixedUpdate` IS ONLY `Homing()`, and "turn the engine off" has to skip ALL
  of it.** `maxSpeed 0` stops the thrust and `maxAngularVelocity 0` stops the steering torque, but
  `turbulenceTorque` is added outside the cap, and the stock rockets ship 15. `fly` skips the
  method.
- **★ WRITING A RIGIDBODY'S VELOCITY EVERY STEP: DAMP IT UP.** Box2D applies
  v x 1/(1 + dt x linearDamping) after the write. A rocket's damping is 3, so a bare write lands 6%
  short of its target every step. `ForgeBloomBullet.Fly` writes v x (1 + dt x damping). This is
  only legitimate with no gravity: velocity writes cancel a fall, which is why grenades are refused.
- **★ AN EXPLOSION FIRED BY HAND DOES NOT NAME THE WEAPON.** `ForgeKillsPatch` attributes an
  explosion only while a projectile's own private `SpawnExplosion` is running. Calling
  `ExplosionManager.SpawnExplosion` directly still credits the UNIT (the explosion's `Owner`), but
  per-weapon onKill rewards and rank XP never pay for those kills. `ForgeBloomHooks.Explode` reaches
  the private method by reflection. Bloom's own pop had this gap from 2026-09-19 to 2026-10-02.
  Grep for any other direct `SpawnExplosion(` call before trusting a kill reward on it.

### Modules & levels

`FloatSeries.GetElement(i)` is the formula `baseValue + change * i`, not an array lookup — a series
with `change: 0` can never respond to levelling. Level boosting works by pixel-mask
`LevelModificationField` grids adding +1 per overlap, gated on `canBeBoosted`. Power-core shapes are
parsed from **sprite pixels**, and width and height must both be **odd**.

> **THREE FEATURES HAVE NOW SHIPPED BROKEN INTO THE SAME TRAP, so write it down as a rule.** Weapon
> per-level scaling, then `minion.capPerLevel`, then `wingman.capPerLevel` (R16 test 207:
> *"failure: it doesn't increase the cap"*). Every time, the code that READ `Module.Level` was
> correct and the level never moved — because `ModuleGrid` applies a BoosterCore's bonus only where
> `ModuleData.canBeBoosted` is set, **every stock WEAPON module in the game ships that flag OFF**,
> and a Forge weapon is a clone of one.
>
> The shape to recognise: **a number read off `Level` is only as live as the flag that lets `Level`
> change, and that flag lives on a different object in a different file.** Nothing connects them, so
> the feature tests fine in isolation and does nothing in play. Any new per-level field must add
> itself to `ApplyBoostability`, which turns the flag on and says so in the log — and the giveaway
> that it has not is a build log cheerfully promising "a BoosterCore raises this" next to a number
> that never moves.

**THE POWERED-MODULE AUDIT: the twin had the same code SHAPE and not the same bug, and the reason is
worth keeping.** Module Forge shipped `ModuleForgeLive` on 2026-09-11 because `Module.OnInstalled`
fires for a card dropped anywhere in the grid while every seam the game *runs* a module through takes
`cluster.ConnectedAndPoweredModules` — so twelve of its effects kept working while unpowered. The
"audit one mod, audit its twin" rule sends you straight here: this mod has exactly two
`ModuleEffect` subclasses, **`ForgeBurnRateEffect` and `ForgeBurnColorEffect`**, and both register in
`OnInstalled` in the identical shape. **Neither was changed, and gating them would have been a
regression.** Two measurements:

- **They ride the WEAPON module itself** (`WeaponBuilder` does `module.effects.Add(...)` on the
  weapon's own `ModuleData`), which is a cluster's **MainModule** — not an augmentation.
- **The game does not gate the weapon on power.** `ModuleSlotWeaponHolder.RefreshWeapon` reads
  `cluster.MainModule` with only a `HasMainModule` test (`!grid.IsEmpty(rootPosition)`), no power
  check at all. So the gun fires regardless, and a burn setting that switched itself off would be
  **stricter than the gun it belongs to**.

And the edge it would have opened, which is the part that could not be reasoned out: whether a main
module is in its own powered slots is **order-dependent**. `ModuleCluster.RefreshPoweredSlots` walks
`ConnectedModules` — a `Dictionary`, so arbitrary order — and gates each module's core on
`connectedPowerCores < MainModule.PowerLevel`, incrementing only for non-main modules. A weapon's
`PowerLevel` is 5, so a fully-kitted weapon whose five power-core augmentations happen to be walked
first leaves the main module's own core ungated and the weapon module reading as unpowered.
(The centre pixel is not the issue: **all nine `Pattern_PowerCore_*` masks have an opaque centre**,
decoded from the 15×15 sheet, and mirror/rotation map an odd square's centre to itself — so a module
with a power core always covers its own position.)

> **"The same code shape" is a lead, not a verdict.** The duplicate-by-design rule is what sends you
> to look; what you find still has to be measured against the mod you are standing in.


### Buying a Forge item with INGREDIENTS, not just money

Built 2026-09-20, both mods, R19 tests 269-270. **The entire mechanism was already in the game,
including the UI** - this change only stops flattening a list that was always a list.

- **`ShopItemConfig.price` is a `List<Price>`**, and a `Price` is either an `Ingredient` (held in the
  run's `Vault`) or a `Resource` (a tank on the ship). `Shop` deducts EVERY entry on purchase and
  `CanAfford` requires all of them.
- **`priceIncrement` is a second list**, and `ShopItem.IncreasePrice` matches each increment to the
  price of the **same currency** - so escalation is per-currency for free.
- **`ShopItemWidget` already does `foreach (Price price in shopItem.price)`** and instantiates one
  `PriceWidget` each, which picks `ingredient.iconSmall` or `resource.icon` and reddens the number
  when you cannot afford it. **A multi-currency price renders correctly with no UI work**, which is
  normally the expensive half.
- **`unlockRequirements` is a `List<Ingredient>`** that `RunData` reads as an "ever owned" gate -
  the item is not in the shop at all until you have held those. Seven stock items use it.

Keys: `shopCost` / `shopCostIncrement` (objects keyed by currency name) and `shopUnlock` (a name or
a list), in both mods. `shopPrice` is unchanged and ingredients stack ON TOP of it, which is the
stock convention: **all 62 entries in the game's own shop table carry a money price**, and 25 of
them carry ingredients as well.

> **⚠ AN AMOUNT BELOW 1 IS A CRASH, NOT A NO-OP, AND THE TWO HALVES OF THE GAME DISAGREE ABOUT IT.**
> `Price.CanAfford` asks `unit.GetResource(r)`, which returns **0** for a resource the ship has no
> tank for. `Shop`'s purchase line is `this.ship.Unit.GetTank(price.resource).Value -= amount` with
> **no null check**, and `Unit.Data.GetTank` is `resourceTanks.GetValueOrDefault(resource)`. Those
> disagree in exactly one place: an amount of 0 makes `0 >= 0` true, the check passes, and the buy
> dereferences null **inside the game's own Shop**. `Price.AmountFloored` is `FloorToInt`, so 0.5
> arrives as 0 too. Both mods refuse a floored amount below 1.
> **A check and its action can disagree, and the gap is where the crash lives** - the same asymmetry
> as `DamagableResource.Damage` guarding with `HasTank` while the `.Tank` property does not.

> **★ THE ID AND THE NAME ON SCREEN DIFFER FOR TWO OF THE NINE INGREDIENTS**, and they are the two a
> person is most likely to type: **`Coral` displays as "Fiber"** and **`Shell` as "Generator"**. The
> lookup takes id, `displayName` and asset name. Same family as the 259 sounds reachable only by
> GUID: **a lookup that cannot see the name on screen fails for the only spelling anyone will try.**

**Four of the nine are not in circulation.** Measured by reference count across the exported assets:
the five the stock shop trades in (Chip, Coral, Gland, Powerstar, Shell) are each referenced 3-17
times, while **Bond, Ex and Face are referenced once - their own registry - and Strange Ball not at
all.** A price in one can never be paid, so it is **warned about and kept** rather than refused (a
game update could start dropping them, and this project reports a questionable file rather than
silently dropping it).

**⚠ AN INCREMENT NEEDS AN ITEM THAT COMES BACK.** `Shop` removes a bought item from the list unless
`ModuleData.repeatInShop` is set, and `IncreasePrice` runs on what is left - so a rising price on a
one-shot item is a number nothing ever reads, printed in the build log as though it applied. Both
mods now turn repeat on when any increment is asked for. **Module Forge had this since it shipped
and it asked about the MONEY increment only** - correct for the only escalation that existed when it
was written, and a silent no-op the moment an ingredient could escalate too.

**Weapon Forge gained `shopPriceIncrement` in the same change.** It had been writing a hard-coded
`amount = 0f`, so a Forge WEAPON could never get more expensive while a Forge MODULE could. Found by
reading the twin, per duplicate-by-default.

**Two stock-game oddities found in passing and not acted on:** `SimpleModuleGrid.RestoreFromMemento`
restores `Active3` from `memento.active2`, and `ShopItemsConfig` pulls its CSV from a published
Google Sheets URL at runtime. (`SimpleModuleGrid` is the ENEMY/minion grid —
`ModuleGridOwner.Data(useSimpleGrid)` — so the first one does not touch the player's ship.)

**R19's close found both shop halves reading one null (2026-09-27):**

- **★ `config.Get(id)` answers NULL for an item added this pass**, because `ShopItemsConfig`'s
  dictionary is only rebuilt by `config.Initialize()` after the injection loop. Three symptoms, one
  read: `Repeatable` returned early, so **TOLLGATE vanished after one purchase and its rising price
  never showed** (269's second half); every summary line said **"for free"**; and the "hidden until
  you have found" half never printed — so the log could not say whether 270's gate was applied.
  `EnsureConfig` hands the config back now, in both mods. **The harness had pinned the bug**:
  `shopcosttest.js` asserted `ShopItemConfig made = config.Get(` was present. It asserts the
  opposite now. > **Pin what the code DOES, not the line it happens to contain.**
- **★ A gated item must get the PRICE and not the POOL.** `RunData.RegisterShopUnlock` (run start +
  every station) draws from the per-tier groups and never reads `unlockRequirements`; only
  `Shop` → `RunData.AddShopItemsWhereRequirementsMet`, as a shop opens, honours the gate. So a gated
  item injected into a pool was on sale at the first station (270). Consequence worth saying in the
  log, and it is: a gated item is not tied to a station tier at all.


### Loadouts — a card with several slots filled

Built 2026-09-25 from his R19 part-1 idea, R19 test 282. `ForgeLoadoutFiles.cs`, called from
`ForgeLoadoutPatch` before the weapon cards; `Loadout Builder.html`; how-to section **4t**;
`loadouttest.py` + `loadoutpagetest.js`.

**The game already had the whole shape.** `LoadoutTemplate` carries `embedded` + `primary`,
`secondary`, `active1-3`; `Apply` installs whichever are set at fixed grid positions, and
`LoadoutCard.Display` lists all five. **No stock card fills more than one**, and all six share the
same embedded module (measured from the exported assets), so a loadout borrows the ship from
`Starter_Popper` and has nothing to choose there. Run start, save and Continue are the game's own
code — the modules become ordinary grid entries the memento restores.

- **Its own `loadouts` folder beside `weapons`.** The weapons folder is read without recursion and
  every file in it is a weapon, so `WeaponBuilder`'s no-template branch asks
  `ForgeLoadoutFiles.LooksLikeLoadout` and names a stray loadout instead of saying "missing
  template". A weapon names ONE slot as a value (`"slot": "primary"`); a loadout names slots as
  KEYS — the two never overlap.
- **★ Names resolve through `ModuleRegistry.AllItems`, never a scene-wide search.** The save restores
  a module with `registry.Get(id).DeepCopy()` and no null check, so an unregistered module would
  start a run fine and break Continue. Order: a Forge file's `name` → registry asset name (the
  `Module Weapon ` / `Module Active ` prefix optional) → the card's `displayName` if unique.
  **`ForgeMinionArms.Made` is excluded from the display-name step**: each tuned minion gun keeps the
  stock displayName and IS registered, so "GEL MISSILE" would turn ambiguous the moment a minion
  carried one.
- **★ `ModuleGrid.Install` checks nothing**, so the mod checks the game's own rule:
  `ModuleSlotType.Weapon/Active.compatibleModuleTypes.Contains(module.moduleType)`. Measured: Weapon
  slots take ModuleType Weapon, Active slots take Active, and all 35 stock `Module Weapon *` /
  `Module Active *` assets are registered and split cleanly by those two types. A misfit leaves the
  slot EMPTY with a warning that names the weapon file's own `slot`; nothing resolving at all means
  no card (a card with nothing on it starts a run with no gun).
  - **★ AND THAT RULE WAS NOT LOADED WHERE IT WAS ASKED.** `ModuleSlotType.Weapon` / `.Active` are
    statics filled by `EnumsCollection.Initialize`, a GAME-scene service; ship select is in the main
    menu. So R19's log said *"the game's slot rules were not loaded yet, so the loadout files' slot
    choices could NOT be checked"* and a primary weapon went onto key 2 — the check skipped itself
    in the only place it runs. `Fits` falls back to the slot-type assets
    (`FindObjectsOfTypeAll<ModuleSlotType>`, the `SetValues` name rule) and then to the module type's
    asset name (`ModuleType Weapon` / `ModuleType Active`, measured off `Weapon.asset` /
    `Active.asset`). **A guard that can be skipped by load order is a guard only on the paths that
    load late.**
- `unlockingModules` is empty, so `LoadoutSelector.IsLocked` never locks the card. Card asset names
  are `ForgeLoadout_<name>`, disjoint from weapon cards' `Forge_<name>`.

**Upgrade modules on the rest of the grid are DECLINED, by design** (his call, 2026-09-25): *"That
wouldn't really work for starting the game with... This template loadout is for starting the game
with a certain setup... there might be weapons that complement each other."* A loadout is a
starting WEAPON setup — five slots of weapons and gadgets that work together. Do not add grid
placement or re-offer it.

## Platform gotchas (.NET 4.7.2 against a Unity 6 game)

`Gradient.SetKeys`, `Texture2D.LoadImage` and `AudioClip.SetData` all bind to the `ReadOnlySpan<T>`
overloads, which this target framework can't satisfy. Use the array-taking setters, or resolve the
method by reflection. Expect the same class of problem from any new Unity API you reach for.

### Drone gadgets and minions

- **`SpawnMinionModuleData.level` IS the minion cap**, one for one. `Activate` refuses at
  `count >= Level` and `OnUpdate` enforces it downward by *destroying* the excess. `canBeBoosted: 1`
  on all five stock gadgets is therefore what makes a BoosterCore raise the cap.
- **Spawner modules are gadget-only because they are UNREACHABLE in a weapon slot, not forbidden.**
  `GetConnectionType` handles PrimaryWeapon/SecondaryWeapon fine and the cap works there — but the
  ship builds its gun from `MainModule as WeaponModule`, and a spawner is not one, so the trigger has
  nothing to call. A primary-fire swarm gun needs a fake weapon that spawns units.
- **Three unguarded dereferences make cloning a stock gadget mandatory**: `OnInstalled` does
  `Instantiate(ParticlePrefab)` with no null check (and `OnUninstalled` dereferences the result),
  `Activate` does `Minion.GetComponent<SavableEntity>()` straight into `CreateEntity`, and it ends with
  `GetTank(ResourceUsed).Value -= ...`. A from-scratch module throws on equip. The template's
  `effects` also live in an **Odin-serialized blob**, not plain YAML, so cloning is the only route that
  keeps them.
- **`Unit.SetOwner` is the hook for anything that must touch a gadget-spawned minion.**
  `SpawnMinionModule.Activate` raises no event and the unit is a *local*, so no postfix can see it —
  but SetOwner is public, runs on the new minion, and is handed the slot. The walk back to the module
  is entirely public (`ModuleGridOwner.ModuleGrid` → `GetAllClusters()` → `ClusterType` /
  `MainModule` → `Module.Data`); no reflection. **And the timing is free:** SetOwner runs *before* the
  game installs the cluster's augmentations and recalculates, so a `MainModule` set there gets the
  game's own augmentations layered on top of it.
- **Stock bug:** the cap trim destroys `unit.Minions.First()` — the first minion in the list, not the
  first of that connection type. Two drone gadgets equipped, and going over the cap on one can kill a
  drone belonging to the other. Fixed by `ForgeMinionTrimPatch` — see the next bullet for the half of
  that fix which is not obvious and which shipped wrong first.
- Killing a minion deliberately should use **`HealthBase.Die()`**, which is what the game's own trim
  does; a raw `Destroy` leaves the owner's minion list holding a dead reference.
- **⚠ BUT `Die()` DOES NOT LEAVE `unit.Minions` THIS FRAME, so a prefix that trims by killing is
  invisible to the original it is trying to correct.** `DamagableResource.Die()` ends at
  `Object.Destroy(gameObject)`, which Unity defers to the end of the frame; the entry is removed by
  `Unit.Data.OnDestroy`, later still. `ForgeMinionTrimPatch` shipped as a prefix that killed the
  right minions under a comment asserting *"by the time the original's own loop is reached the count
  is already within the cap, so its buggy line never executes."* It executes: `OnUpdate` recomputes
  `num` from a set nothing has changed, still finds it over `Level`, and runs its
  `DestroyUnit(unit.Minions.First())` on top of the correct kills. `.First()` is stable within a
  frame, so it usually re-`Die`s a body already destroyed — `TryGetComponent` on a destroyed object,
  inside the game's own `OnUpdate`. **`Unit.Data.RemoveMinion` is public**, is what the minion's own
  teardown eventually calls, and a `HashSet.Remove` of something already gone is a no-op — so the
  prefix unregisters each kill itself.
  > **"Destroyed" is a decision, not an event.** Unity's `Destroy` marks the object (so
  > `go == null` is true immediately) and runs nothing until the end of the frame — so anything a
  > *collection* is asked about this frame still sees the corpse. A prefix's entire job is to be
  > read by the original, so it has to change what the original reads, not merely what is doomed.
  > Same family as the fire-rate clock: a refusal that does not consume the state that provoked it
  > is not a refusal.

### Minions: eyes, bullets, and following

Three separate facts, each of which produced its own "the AI is broken" report, and all three are
invisible in a log because every value the mod writes is written correctly.

- **A unit's eyes are wired to ONE layer and it is not the layer its enemies are on once you change
  its faction.** `Vision` derives from `ComponentScanner<Unit>`, whose `Scan()` is an
  `OverlapCircleNonAlloc` filtered by a serialized private `targetLayers` LayerMask. The prefabs split
  cleanly in two: enemies (`Enemy_Larva`, `Enemy_Maggot`, …) ship **64 = Player**, and the five drone
  prefabs ship **128 = Entities**. `AIAgent.Update` then sorts whatever Vision handed it into friends
  and enemies *by faction* — so faction decides who a unit fights, the layer mask decides who it can
  see, and **nothing reconciles the two**. A hostile prefab flipped to your side sees only you,
  decides you are a friend, and drifts; a drone left hostile (`"faction": "keep"`) cannot see the
  player at all, so it never attacks. Same field, opposite directions.
  `ForgeMinionAllegiance.Eyes` sets the mask to `Player | Entities` and lets the faction decide, which
  is what `AIAgent` was already written to do.
- **A minion's own gun fires ENEMY bullets, and only you can be hit by them.** `Projectile.Shoot`
  builds `collisionLayerMask` from `gameObject.layer`, so a shot's faction is the layer of the prefab
  it came from — `EnemyProjectiles`, which the matrix collides with Player / Ground / Fruits and
  **not** with Entities. Retargeting the *cloned* ammo prefab to `PlayerProjectiles` is enough,
  because the mask is rebuilt from the layer at fire time. Clone first: one ammo asset is shared by up
  to eight enemy prefabs. Hitscan needs its `WeaponData.layerMask` swapped instead.
- **Following an owner is an AI STATE, not a number.** Compare the state children:
  `Enemy_Fly Variant Drone` has Idle / Aggro / Seek / **Regroup**; `Enemy_Maggot` has Idle / Aggro /
  Charge / Seek; `Enemy_Larva` has Idle / Aggro. Regroup is the come-back state and **only the five
  drone prefabs have one** — which is why `followDistance` and `leash` (which tune
  `MoveAroundOwnerAction` and its conditions) are honest no-ops everywhere else. `ForgeMinionEscort`
  does it from outside with a rigidbody force, for the same reason the enemy slow does: a
  `MovementAction` snapshots its component's values at state entry and restores them at exit.

### Driving a minion: `MoveTo` is public, and a force COMPOSES rather than being overwritten

The escort was built on a rigidbody force out of caution about the snapshot trap above. Reading the
movers settled what is actually available, and the answer is better than the caution assumed.

- **`PushMovement` moves by `AddForce`, not by assigning velocity** —
  `rigidbody.AddForce(dir * pushForce.RandomInRange() * rigidbody.mass)`, and it is already
  mass-compensated. So an outside force **sums** with the AI's own locomotion instead of being
  erased by it, which is why the escort's lean visibly drags a Larva at all. (`SwimmingMovement` is
  the only other `UnitMovement`.)
- **`UnitMovement.MoveTo(Vector2)` is public and abstract**, and both implementations are two lines:
  `targetPosition = position; isStopped = false;`. Each mover's own `FixedUpdate` then propels the
  body toward that point at the creature's own speed, cadence and angle jitter. **This is strictly
  better than pushing the body yourself whenever a mover is active** — nothing to calibrate, and the
  creature moves the way that creature moves.
- **`MoveTo` is outside the snapshot cycle.** `MovementAction.OnEnable` records
  `movement.GetCurrentValues()` and `OnDisable` writes it back wholesale — but `targetPosition` is in
  neither `ValueOverride` nor `GetCurrentValues()`. It is a *destination*, not tuning, so writing it
  is untouched by state changes. The snapshot rule applies to speeds and delays, not to where a unit
  is going.
- **`MovementAction.Update` is the ONLY thing steering a unit** —
  `movement.MoveTo(path.vectorPath[currentWaypoint])`, every frame while that state is active. So
  disabling the action leaves nothing steering, and `movement` is a **public field** on it, giving
  the exact mover that action is paired with rather than a guess (a unit carries one mover per state;
  the stock drone has five). `OnDisable` calls `movement.Stop()` when `stopOnExit` (default true), so
  **suspend first and `MoveTo` second** or the walk is cancelled on the frame it is requested.
- **`ChargerHead` does not propel.** It casts for damage and applies a recoil impulse to *itself*,
  nothing more. So suspending a Larva's or Maggot's movement action really does stop a charge — worth
  checking before building a leash on that assumption, since the beak looks like the thing driving it.
- Re-enabling a `MovementAction` costs a **pathfinder request** (`OnEnable` → `FindPath`), so anything
  that toggles one needs hysteresis in distance *and* time. `ForgeMinionHold` releases at 75% of the
  radius, not at the rim.

> **A LEASH CANNOT BE BUILT ON TARGETING.** `AIAgent.Update` calls
> `SelectTargetFromVisibleEnemies()` on **every frame it has no visible target**, and that takes
> `visibleEnemies.First()`. So `ForgetTarget()` on a target still in Vision is undone on the same
> frame — a per-frame thrash, not a leash. (Blinding the unit instead stops it fighting at all.)
> Movement is the only honest lever, which is what `hold` is built on.

### Three things decide who a unit fights, and NOTHING reconciles them

Faction, the `Vision` layer mask, and a **personal grudge list**. This mod had already been bitten by
the first two (see the minion notes above); the third cost R12 test 134.

**`AIAgent.Data.enemyBlackList` is a `HashSet<int>` of entity instance ids and it is checked BEFORE
faction:**

```
if (enemyBlackList.Contains(id))   -> ENEMY      <-- wins outright
else if (friendWhiteList...)       -> friend
else if (IsFriendsWith(unit))      -> friend
else if (IsEnemiesWith(unit))      -> enemy
```

And `AIAgent.HandleHitBy(other)` — which `AggroWhenHit` calls from the victim's own `onGotAttacked` —
adds the attacker and never removes it. So **the act of charming something blacklists you on it**: its
faction says friend, its grudge says enemy, and the grudge is read first. A charmed enemy kept hunting
the player for the whole charm with every value written correctly and nothing in the log.

**His report contained the proof and I would not have thought of it:** *"So it works while
invisible."* Under `stealthPERMA` the player never enters `Vision.VisibleUnits`, so the blacklist
entry is never matched and the agent falls through to `SelectTargetFromVisibleEnemies`. **The one
configuration where charm worked was the one where the grudge could not be consulted.** A bug report
that names the working case is often naming the mechanism.

Everything needed is public — `ComponentData` has a public getter, `enemyBlackList` is a public field,
`ForgetTarget()` / `SetTarget()` / `Target` / `HasTarget` / `SeesUnit()` are all public. Two
consequences:

- **It is save-persisted** through `AIAgent.Data.Memento`, so anything that withdraws a grudge must
  put it back, and must only put back what it actually took.
- **It is also the mechanism for the opposite feature.** Charm's `infighting` cannot be done with
  factions (charmed units all share yours, so they are all friends) and does not need to be: writing
  each charmed unit into the others' blacklists makes them fight, because the grudge outranks faction.
  The bug and the feature are the same field.

**And dropping the grudge is not enough on its own** — `AIAgent.Update` returns early while
`HasTarget && IsTargetVisible`, so a stale target survives until it leaves the unit's sight.
`ForgetTarget()` has to be called too, and re-checked, because any later hit re-adds the grudge.

### `Unit.IsFriendsWith` has TWO callers in the game and ELEVEN in this mod

Worth knowing before patching it, because the ratio is the whole decision. The game calls it from
`Projectile.FixedUpdate` (`Owner.IsFriendsWith(hit)` — skips the hit) and from `AIAgent`
(`thisUnit.IsFriendsWith(seen)`). This mod calls it from homing and chain target-finding, the curve
beam, the tractor, sticky, turrets, the minion blast filter and execute — all asking "is this on my
side, so leave it alone".

So a postfix on it is a **wide** change, and that is why charm's `playerCanHit` is off by default and
documented as one rule ("my weapons no longer treat it as a friend") rather than as "my bullets hit
it". Two narrower designs were considered and are both worse:

- **Scoping a flag to `Projectile.FixedUpdate`** looks exact and is not: homing runs *inside* that
  same FixedUpdate, so the flag is live for `FindTarget` anyway, and the damage call nests inside it
  too — so it would also reach charm's own protection guard and let a charmed unit hurt you.
- **Giving charmed units a cloned faction** is asymmetric the right way for your own fire, but no
  ENEMY faction lists the clone under its `enemies` either, so every enemy in the room would calmly
  ignore the charmed unit instead of fighting it.

The mod's own internal ally checks go through **`ForgeCharm.RawFriends`**, which suppresses the
postfix — `harmless` and `playerCanHit` are opposite directions through one method.

**The player ship has no `AIAgent`** (verified on the prefab: `ShipKBM` has no `seeker` field where
`Enemy_Larva` has one), so the AI caller is unreachable on the player.

### Minion spawner templates are BOSS attacks

All four minion-spawner assets ship `fireRate 0.2`, **`warmupTime 2`** and (three of four)
`burstSize 4`, plus `angleVariance` 10–30 and `projectileSpeed 60 ± 40`. `warmupTime` is the killer
and the one nobody had checked: it is the game's minigun spin-up, `Shooter.Update` gates every shot
on `IsWarmedUp`, and no file ever set it — so a player weapon built on one of these templates
**refused to fire for two seconds** and then dumped four minions in a 60° cone. That is the whole of
"insane charge time … shoots 5 at a time and rapidly regardless of what I did"; the settings were
reaching the game all along, and the delay drowned everything else. `WeaponBuilder.TameSpawner`
neutralises whatever the file does not mention, and names each change.

### Naming another weapon from JSON

A Forge weapon's `WeaponData` asset is named **`"Forge Weapon " + name`** (set in `WeaponBuilder`),
and that is not guessable from outside. `split`, `minion` and `turret` each did their own bare
exact-match lookup, so **the weapon's own `name` field — the one string the author has in front of
them — was the one string that could not resolve**, and `ForgeMinion` matched `loadoutName` /
`displayName` / `module.name`, none of which is it either. Five reasonable spellings failed in one
test round.

Everything that takes a weapon name now goes through **`ForgeWeaponLookup.Find`**, which accepts the
exact string, `"Forge Weapon " + name`, a stray-`Forge ` near miss, and the `displayName` (which lives
on the **ModuleData** — `WeaponData` has no `displayName` at all). On a miss, `LogMiss` prints every
Forge weapon by `name`: a name lookup that fails silently costs a test round, one that lists the
candidates costs a glance.

## `Weapon Builder.html` conventions

One self-contained page, no build step, no network. Fields carry a **`data-path`** and are folded into
nested JSON by `deepSet`.

> **A `data-path` that is a prefix of another one throws**, and because the exception escapes
> `build()`, the page silently emits *nothing at all* and drops every field after it. A `homing` bool
> alongside `homing.turnRate` caused exactly this. Use `homing.enabled` instead — and sweep all paths
> for prefix collisions after any change.

Round-trip matters: anything folded in `build()` must be unfolded in `loadObject()`, or LOAD .JSON
quietly loses it.

> **Duplicate `data-path`s across `.type-section`s are DELIBERATE — do not "fix" them.** The page has
> one section per weapon type (`sec-projectile`, `sec-hitscan`, `sec-minion`) and shows only the one
> matching the chosen template. `projectileSpeed`, `projectileSpeedVariance` and the four
> `velocityInfluenceData` fields genuinely belong to both a projectile weapon and a minion spawner, so
> each appears twice — and `isIncluded()` skips anything inside a `.hidden` container, so exactly one
> copy is ever collected. A naive "no two elements share a data-path" check reports six false
> positives here; **group by owning type-section before calling anything a duplicate.** (This was
> nearly mis-diagnosed as dead UI once: a console test filled the *minion* copy while a *projectile*
> template was selected, saw nothing emitted, and concluded the field was broken. It was working
> exactly as designed.)

> **A CROSS-TYPE BLOCK PLACED INSIDE A TYPE-SECTION SILENTLY EMITS NOTHING FOR EVERY OTHER TYPE.**
> Found 2026-08-28 while building `leech`, and it had already shipped twice. `isIncluded()` skips
> anything inside a `.hidden` container, and `sec-projectile` **is** hidden whenever a hitscan or
> spawner template is chosen — so `crit`, `onKill` and (nearly) `leech` were sitting in the projectile
> section while working on every weapon type in the C#. Picking the Caps Laser and filling the crit
> block produced JSON with **no crit block in it at all**, and nothing warned.
>
> It is invisible three ways over: the controls look present (you were on a projectile template while
> authoring), the C# is correct, and the emitted JSON is valid — just missing a key. The `Siphon`
> preset here is what exposed it: a beam-template preset carrying a `leech` block round-tripped
> without one.
>
> **AND IT HAD SHIPPED TWICE MORE THAN THAT — found 2026-09-03 while checking `spinUp`.**
> `overheat` and `slow & time-slow` were both sitting in `sec-projectile`, and both are
> type-agnostic in the C#: `ForgeSlowPatch` patches `ProjectileCollided`,
> **`OnHitByHitscanWeapon`** and `OnExplosion`, and **overheat's `perSecond` key exists
> specifically so a held weapon — a beam — can overheat at all.** So a key documented as being for
> beams lived in a block that could not be filled in on a beam. All three now sit in always-visible
> sections following the house naming (`<h2>X (optional) — all weapon types</h2>`).
>
> **The cheap test is worth building once per feature, not reasoning about**: boot the page in jsdom,
> pick a hitscan and a spawner template, and assert both that every control is reachable and that
> the key survives into the emitted JSON. `spinUp` failed that on its first run — the block had
> landed next to `slow`, inside the very section this rule is about.
>
> **So the placement rule is a correctness rule, not a layout preference.** A block goes in
> `sec-projectile` only if the FEATURE is projectile-only (`grow`, `damageRamp`, `speedRamp` genuinely
> are — they patch `Projectile`/`PhysicsProjectile`). Anything that hooks the damage path, the kill
> path, or `WeaponBase` belongs in its own always-visible section, the same shape as
> `Slam (optional)` and `Contact Damage (optional)`. **The test is a preset on a non-projectile
> template**: load it, read the JSON back, and check the key survived.

`auditFieldPaths()` runs once at load and checks both failure modes — a same-visibility duplicate, and
a path that is a dotted prefix of another — reporting to the console *and* into the warnings box.
`refresh()` wraps `build()` in try/catch for the prefix case, which really does throw: without the
catch the page freezes on its last good output with no explanation, which is how that class of bug
stayed invisible before.

**"Preset" means a one-click entry in this page** that fills the whole form — not an example `.json`.

### Navigation — and the one rule the filter must obey

The page reached **~930 fields in 60 collapsed blocks across 19 sections**, at which point "it is all
collapsed by default" stopped being enough: everything *was* closed, but a block you cannot name is
a block you cannot find. Two R10 tests were failed as "this feature is missing from the builder page"
when the fields were present and simply unfindable.

So there is a sticky bar at the top of `#form` with a **live filter**, a **generated index**, and
expand/collapse-all.

> **THE FILTER MUST NEVER CHANGE THE EMITTED JSON, and that is why it has its own class.**
> `isIncluded()` skips two things — anything inside **`.hidden`** and anything in a block whose gate
> is unchecked. So the filter hides with **`.nomatch`**, never `.hidden`, and never touches a gate.
> Searching for "fade" hides 48 of 60 blocks and the output is byte-identical; a filter that reused
> `.hidden` would silently drop every field you were not currently looking at.

**The index is built from the DOM at load**, walking `section > h2` and `details.block > summary`, so
it cannot drift from the content the way a hand-written list would. Adding a block adds an index
entry for free.

Two smaller rules the filter follows: a block inside a `.hidden` **type-section** is never a match
(it genuinely does not apply to the chosen template), and blocks the search auto-opened are closed
again when the search is cleared — so clearing returns the page to how you left it rather than
leaving 60 blocks hanging open.

> **`scrollIntoView` on a `display:none` element does nothing, silently — and that made the whole
> index look broken.** Reported 2026-08-28 as *"the search function doesn't really do anything… when I
> have a search term and then click a button in the index menu to go to a section, it doesnt do
> anything… the index doesn't appear to be working correctly."* Three separate faults, one symptom:
>
> - **A match that cannot be displayed was still counted.** `found++` fired for `.hidden`
>   type-sections whose prose matched, so searching `solidToOwner` on a projectile template reported
>   matches and showed an empty page. The count now only counts what is on screen, and unreachable
>   matches are reported separately **by section name** — `nothing matches here · 2 more in Minion
>   Spawner / Drone Gadget (switch template)`.
> - **Clicking an index entry whose target the SEARCH had hidden did nothing.** `jump()` now clears the
>   search first and then goes — you asked for that block, so you get it.
> - **Clicking an entry in a wrong-template section also did nothing.** Those entries are now dimmed
>   with a dashed border, and clicking one says which template to pick instead of failing quietly.
>
> The index is also filtered and highlighted by the search now (`.idx-off` on entries, and on a whole
> `.idx-group` once all its entries go off — a heading over nothing is its own bug). **The index uses
> `.idx-off`, never `.nomatch`**, for the same reason the filter does not use `.hidden`: one class per
> concern, so nothing that hides a *link* can ever reach the form fields `isIncluded()` walks.

**The four index classes, and why a fifth needs the same discipline.** `.idx-off` is the SEARCH,
`.idx-unreachable` is the TEMPLATE, `.idx-on` is the GATE (added 2026-09-14 — *"show a different
color to features that are enabled on the page. That way I don't need to search the list each time
for a specific feature im currently working with"*), and `.nomatch` belongs to the form and must
never appear in the panel. Two rules fall out of `.idx-on`:

- **"Enabled" means the GATE, not "has values typed in it."** `isIncluded()` reads the gate, so a
  block full of numbers with its gate off emits nothing — colouring that one would be a lie in the
  expensive direction. `indexontest.js` asserts both halves against the emitted JSON.
- **It is repainted from `refresh()`, not from a `change` listener.** LOAD .JSON, RESET FORM and
  every preset assign `.checked` directly, and **assigning a property fires no event at all** — so a
  listener would track hand-ticks and silently miss every bulk path. `refresh()` is the one function
  all of them already end in. It is reached across the IIFE boundary as `window.__syncIndexGates`,
  and it short-circuits on a signature of the 76 gates, because `refresh()` runs on every keystroke
  in ~930 fields and almost none of them touch a gate.

`#f-search` is `type="search"`, whose native clear X exists in Chrome and not in Firefox; it is
suppressed and replaced by a real `#f-clear` button. **Clearing must cancel the pending 120 ms
debounce** — without that, clicking it inside the window lets the stale `applyFilter` fire
afterwards, and the box reads empty while the form stays filtered.
>
> **`#form` must have NO `padding-top`, and the navbar must not try to cancel one with a negative
> margin.** A sticky offset is measured from the scroll container's **padding box**, so
> `padding-top: 18px` on `#form` parked `#navbar` (`position: sticky; top: 0`) 18px down and left a
> strip of `--bg` above it at *every* scroll position — reported 2026-08-28 as *"theres this gap
> between the search/index/expand all/collapse all bar that shows the background."* The
> `margin: -18px …` that was there to compensate never worked: **the sticky clamp beats a negative
> margin.** The fix is `padding: 0 24px 60px` on `#form` and `margin: 0 -24px 14px` on the bar — the
> bar supplies its own top spacing, and its `margin-bottom` spaces the first section.
> Measured, not reasoned: a 40-line standalone repro with the real CSS reported
> `gapAboveNavbar_px: 18` before and `0` after, at `scrollTop` 0 and 400. Build that repro again
> rather than arguing about it — the whole page is too big for the preview tool.

> **This page is too big for the browser preview tool (847 KB), so it is verified with jsdom instead.**
> `navtest.js` in the session scratchpad loads the real file with `runScripts: "dangerously"`, stubs
> `scrollIntoView` to record whether a jump landed, and asserts twelve behaviours including the
> load-bearing one: **the emitted JSON is byte-identical before, during and after filtering.** Re-run
> that rather than reasoning about it — the JSON invariant is the one that would do real damage.

## Cross-mod rules (Module Forge)

The two mods cooperate but must stay independently buildable and runnable:

- **Find each other by type name only** — `AccessTools.TypeByName("ModuleForge.ModuleForgeBurn")`.
  Never an assembly reference, never `BepInDependency`, never the plugin GUID.
- Exactly one owner per shared mechanic. If Module Forge is present it owns the burn engine and
  Weapon Forge's burn patches early-return; pierce caps are added into one number and stat-card lines
  are merged rather than double-reported.
- **CRIT owns the other way round — THIS mod owns it, always.** Module Forge ships its own crit engine
  so a crit module works with this mod absent, and stands its patches down when this mod is present
  (`ModuleForgeCritCompat.OwnsPatches`), feeding `ForgeCrit.SetBonus` instead. Two prefixes on
  `DamagableResource.Damage` would each roll their own dice on the same hit. Consequences here:
  - **`ForgeCrit.SetBonus` accepts a NULL weapon**, meaning every weapon. A Module Forge module in the
    SHIP grid never gets `IWeaponModifier.Modify` and so has no `WeaponBase` to key on; without that
    channel it would have been silently inert. It also **withdraws the contributor from every other
    bucket first** — a module can move grids, and a weapon is a fresh `WeaponBase` instance whenever
    its cluster is rebuilt, so "set here and nowhere else" is the only semantics that cannot leave a
    stale contribution crediting a weapon that no longer exists.
  - **`ForgeCrit.Reset()` deliberately does NOT clear `_weapons`.** Those configs are registered once
    at startup from the weapon files, and this is called from `ForgeBurnResetPatch` at every run
    entry — clearing them would silently delete crit from every weapon for the rest of the session.
    Only per-run state (module contributions, pull decisions) goes.
  - `ForgeCrit.TryGetTotal` exists for Module Forge's stat card and returns the **total**, module
    contributions included, because they already live here. Handing back only the weapon's baked half
    would make that card contradict the game.
- **LEECH is the same deal, plus two rules of its own.** `ForgeLeech.SetBonus` takes
  `(weapon|null, key, fraction, flat, maxPerSecond, perEnemyDelay)` and `ForgeLeech.Resolve` folds the
  weapon's own config together with every contribution.
  - **The CAP and the DELAY cross the bridge, and the resolution is asymmetric: TIGHTEST cap, LONGEST
    delay.** Both are safety limits, so a module that ships one must protect the player even on a
    weapon whose author left it uncapped — the converse would let a module quietly create the
    immortality that author had ruled out.
  - **A module-granted leech on a weapon with no `leech` block gets `GrantedCapPerSecond`**, cloned
    per weapon by `GrantedFor`. Not shared: a `Config` carries per-weapon runtime state (the
    per-second budget and the per-enemy timers), so one shared `Defaults` would make two weapons
    silently share one allowance. And the cap comes back from `Resolve` as an **out param** rather
    than being written onto the Config, because a Config belongs to a `WeaponData` while
    contributions are keyed to a live `WeaponBase`.
- **★ THE NULL-WEAPON CHANNEL IS "THE CARRIER'S GUNS", NOT "EVERY GUN" — fixed R19 part 1.**
  `ForgeCrit` / `ForgeLeech` `_global` summed every ship-grid bonus into EVERY weapon's numbers,
  enemy guns included — so a Module Forge crit module made enemy shots crit the player and a leech
  module let enemies heal off him. Module Forge found the same shape in six of its own registries
  (grow and windup reaching enemy bullets and guns were the visible half). `ForgeModuleScope`
  (a mirror of Module Forge's `ModuleForgeScope`, deliberately not shared) holds the owner of each
  contributor; Module Forge's bridges call `ForgeModuleScope.SetOwner(key, Unit.Data)` by type name
  before a null-weapon push, and both sums skip a global entry unless the weapon's
  `Owner.ComponentData` is that unit. An unknown owner applies to nobody and is said once.
- **Kill ATTRIBUTION has to be pushed, not shared.** Both mods keep their own shot→weapon map (each
  must work with the other absent) and both stamp it from their own firing hook, which is
  `DoShoot`-scoped. So a shot *this* mod creates mid-flight — a split fragment, a chain jump, a
  rallied reflect, a catalyst copy — was attributed here and nowhere else, and a Module Forge on-kill
  module on the weapon's grid never matched the weapon and never paid out. `ForgeKillsPatch.Attribute`
  now mirrors into `ModuleForge.ModuleForgeKills.Attribute` by reflection, which covers every
  mid-flight shot at once rather than one feature at a time.
- If the reflection fails, degrade to doing nothing — never risk two mods fighting over the same patch.

## House rules

- **BEFORE APPENDING A LINE TO A METHOD, READ EVERY `return` ABOVE IT.** Three features shipped
  broken this way in two weeks, in three classes, and in each case the added line was correct:
  `Shooter.Update` returns before its own `StopContinousSound` (a stunned gun kept roaring);
  `ForgeOverheatPatch.Tick` returned before its own `Paint` (the heat colour only repainted once you
  *stopped* firing, so tapping showed a ramp and holding did not); and the beam's second
  `GetDirections` reader above. The guard was written for a different concern and is invisible from
  the diff. **Feedback, logging and painting are pure reads of state and belong above every guard.**
  The tell: a feature that works in one input pattern and not another (tap vs hold, one target vs
  several) is a guard, not a broken feature.
- Verify against the decompile before building. Nearly every bug in this project's history was a wrong
  assumption about the engine, not a wrong line of C#.
- Check whether a game field already does the job. `slowDown` gave us the boomerang pivot; `Radius`
  gave us the growing hitbox. Both would have been ugly hand-rolled code.
- Assume any class you hand a struct/class instance to keeps the reference. `ProjectileBounceData` is
  a **class** — assign a fresh instance or you mutate every stock weapon that shares it.

### Testing rounds — the checklist is BUILT from card files (since 2026-09-28)

**Read `Checklist/README.md` before closing or opening a round. It is the whole procedure.**
`TESTING CHECKLIST R<N>.html` is written only by `Checklist/build.py`, from one file per test card
(`Checklist/cards/`), one per captured idea (`Checklist/captured/`) and one per round
(`Checklist/rounds/R<N>.txt`). Never edit the page by hand — the build refuses to overwrite a page
that changed since it last wrote it, and `Harness/checklisttest.py` fails while the page and the
cards disagree.

Why it changed: rounds 2-20 were each made by copying the last page and patching it (R20: ~13 KB of
find-and-replace edits), then writing a new 200+-assertion harness (`r16live.js` … `r20live.js`,
now in `Harness/closed/`) to prove the patching worked. R19's close ran six hours and overflowed the
context window. Every check those harnesses re-wrote now lives once in `build.py`; a round's own
specifics are `quote:` / `pin:` / `keep:` / `lacks:` lines on the cards. `close.py` files the pasted
results into each card's `== history`, so a card carries what was asked and what the tester said,
round by round. Conversion was proven by rebuilding R20 from the cards **byte-identical** to the hand-built
page, and `Harness/edits/checklist_mutants.py` breaks a copy 16 ways, all refused.

**The captured list still must never quietly shrink** — it has twice:

> R12 shipped with **5** captured entries where R11 had **11**. Eight were lost, because the section
> was rebuilt from the round's **results file** — what was fresh from reading his report — instead of
> from the previous round's own captured section. He caught it.

That is now structural: a captured idea is a file that is edited in place, never recreated, and the
build checks every file the last round's manifest listed is still there. Leaving the list means a
`retired: R<N> | why` line, which the round's retired note lists automatically. `carryaudit.py`
still compares the built pages as a second opinion.

**Re-verify carried lines when you touch them** — this half no build can do:

- For a line describing a **decision or a question**, check it against its memory file.
- For a line describing **owed WORK**, check it against the **code**. A "still owed" note survives the
  work that closes it, because the work gets done under a different feature's name and nobody returns
  to the note. `bounce-feature-research-todo` was wrong about its own remaining item **three times**;
  each time one grep for the named symbol would have settled it. Reading the note again never does.
