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
  Nine files patch `Shooter` (two more mention `typeof(Shooter)` for reflection only, which is what
  makes a raw grep overstate this). Six already accounted for gadgets: `overheat`, `spinUp` and
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
>   files (`_said*` / `_warned*`), and **three** had a working reset. So the second run of a session
>   reported nothing — and the run where you go looking for a warning is exactly the run that cannot
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
  `JsonFieldMapper.FindAsset`, so `"HUD_GridTiles_04:2"` matched no asset and fell through to
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
- **The shield prefab sits on layer `Default` (0)**, and the layer matrix explains everything:
  `PlayerProjectiles` collides with `Default`, **`EnemyProjectiles` does not**. So an enemy shield
  stops your bullets, and no shield anywhere stops an enemy bullet geometrically — enemy fire is only
  ever absorbed through the damage pipeline after it reaches the hull.

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
  `Weapons/Popper/Shoot`, `UI/Click`), and `ForgeSfxRegistry.Find` matched **`s.guid` only** — so a
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

Module Forge does the same grafting, so the two mods coordinate through **one shared string**: the
created Money pool is named `Forge Modules Crate Money` in *both*, and both check `TableAlreadyRolls`
before appending. Rename it in one mod only and a player with both gets two module rolls per Money
crate.

### Modules & levels

`FloatSeries.GetElement(i)` is the formula `baseValue + change * i`, not an array lookup — a series
with `change: 0` can never respond to levelling. Level boosting works by pixel-mask
`LevelModificationField` grids adding +1 per overlap, gated on `canBeBoosted`. Power-core shapes are
parsed from **sprite pixels**, and width and height must both be **odd**.

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
  drone belonging to the other.
- Killing a minion deliberately should use **`HealthBase.Die()`**, which is what the game's own trim
  does; a raw `Destroy` leaves the owner's minion list holding a dead reference.

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

### Opening a new testing round — the carried list is derived from the OLD CHECKLIST, not the results

Each round lives in its own `TESTING CHECKLIST R<N>.html` with its own localStorage key, and the
previous one is moved into `Testing Checklists/`. The failure mode to guard against is the
**"captured, not built" section quietly shrinking**, and it has happened twice:

> R12 shipped with **5** captured entries where R11 had **11**. Eight were lost, because the section
> was rebuilt from the round's **results file** — what was fresh from reading his report — instead of
> from the previous round's own captured section. The results file holds this round's failures; it does
> not hold the standing backlog, so everything he had not just mentioned vanished. He caught it.

**The procedure, which costs about a minute:**

```bash
# 1. what the last round carried
grep -o '<details class="cap"><summary>[^<]*' "Testing Checklists/TESTING CHECKLIST R<N-1>.html"
# 2. what the new one carries
grep -o '<details class="cap"><summary>[^<]*' "TESTING CHECKLIST R<N>.html"
```

Everything in (1) must appear in (2) **or be explicitly retired with a reason written in the new
round**. Then re-verify each carried line — and this is the half that catches the other bug class:

- For a line describing a **decision or a question**, check it against its memory file.
- For a line describing **owed WORK**, check it against the **code**. A "still owed" note survives the
  work that closes it, because the work gets done under a different feature's name and nobody returns
  to the note. `bounce-feature-research-todo` was wrong about its own remaining item **three times**;
  each time one grep for the named symbol would have settled it. Reading the note again never does.

A tolerant diff script lives in the session scratchpad (`carryaudit.py`) — match titles by word
overlap, not equality, or legitimate rewording ("THE FIVE BEHAVIOURS LEFT" → "THE FIVE **WEAPON**
BEHAVIOURS LEFT") reports as a drop. Mine did, on its first run.
