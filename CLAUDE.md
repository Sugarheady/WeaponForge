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
- **A lobbed shot cannot be steered, but it can be *aimed*.** Rewriting a rigidbody's velocity every
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
- **`Shooter.Shoot()` skips the resource check** — `OwnerHasResource` is tested in `Update`, not in
  `Shoot`. Calling `Shoot()` from a patch therefore hands out a free shot unless you test
  `OwnerHasResource` (public) yourself. It ignores `FireRatePassed` for the same reason.
- `Cost` and `KnockbackForce` are read inside `Shoot()` itself; everything else the weapon reads
  happens inside `DoShoot`. That split is why `ForgeChargePatch` scopes them in two different places.
- **`AimAction.RotationSpeed` clamps turning to `MaxRotationSpeedWhileShooting` while shooting** —
  but that's the *enemy* aimer. The player aims through `Aimer`, which has no such clamp, so a
  held-trigger player weapon doesn't slow the crosshair.

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
> order-independent. `ForgeBoomerangPatch.SetHeading` is the reference implementation. For the same
> reason, steer from `idealDirection` rather than from `Velocity` — rotating from `Velocity` folds
> another feature's offset back in each frame and widens into a spiral.

> **If your feature changes damage in flight, publish a multiplier to `ForgeDamageStack` — don't
> write `Damage` directly.** Exactly the same problem as `idealDirection`, one field along: two
> features assigning `Damage.amount` from their own captured bases means whichever FixedUpdate prefix
> runs last wins, and Harmony orders them arbitrarily. `grow`'s `damageAtFull` and `damageRamp` would
> have cancelled each other out. The stack keeps one captured base and multiplies every contributor,
> so it is order-independent. **Capture at `Shoot`**, not lazily — one feature's Shoot postfix can
> modify `Damage` before another's reads it, and the second would record the modified value as normal.

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
- Particles receive sprites via **TextureSheetAnimation in Sprites mode**, *not* a SpriteRenderer — so
  **pixels-per-unit does nothing for particle art**. The quad is sized by the system's own `startSize`,
  and every stock muzzle is Billboard with no `startSize3D`, meaning a *square* quad that stretches
  non-square art. That's a third scale rule alongside sprite renderers (PPU) and UI Images (stretch).
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
  So a beam cannot bend and cannot flipbook-animate. The hit spark is a particle system and can.
- **The damage raycast is separate from the visual raycast.** A bending beam would have to patch both.
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

**Nothing in this game does contact damage to anything else.** `HealthBase`'s only entry points are
`ProjectileCollided`, `OnExplosion`, `OnHitByHitscanWeapon`, `OnHazardTouched`, `OnHitByElectricity`
and `OnCellCollision`. There is no melee component, and **no `OnCollisionStay2D` / `OnTriggerStay2D`
anywhere in the assembly**. Flying into an enemy is free.

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
- **Inherit `mixerGroup` from the sound you replaced**, or the clip bypasses the in-game volume slider.
- **Enemies already play a hit sound, but you cannot find it by grepping code.** All 37 enemy Unit
  prefabs wire `HealthBase.onDamage` to an `SfxPlayer` as a **UnityEvent in the prefab**, so it appears
  at no `PlaySfx` call site. When asking "does the game do X", **search the prefab YAML as well as the
  decompile** — this exact trap produced a confidently wrong "the game has no impact sound" once.
  What is genuinely missing is a *per-weapon* impact sound (the existing one belongs to the victim) and
  any sound at all for hitting terrain. Hence `impactSfx` is a patch (`ForgeImpactPatch`) with an
  `impactSfxOn` filter, not a data swap.
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

`auditFieldPaths()` runs once at load and checks both failure modes — a same-visibility duplicate, and
a path that is a dotted prefix of another — reporting to the console *and* into the warnings box.
`refresh()` wraps `build()` in try/catch for the prefix case, which really does throw: without the
catch the page freezes on its last good output with no explanation, which is how that class of bug
stayed invisible before.

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
