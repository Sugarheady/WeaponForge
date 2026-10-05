using System;
using System.Collections.Generic;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace WeaponForge
{
    // A DAMAGEABLE wingman: spawning the unit, switching its brain off,
    // dressing it, and taking it away again.
    //
    // ⚠ THIS IS THE FIRST TIME EITHER MOD HAS SPAWNED A UNIT. Both existing
    // minion paths hook the game's OWN spawn - the gadget through
    // `Unit.SetOwner`, the spawner weapon through
    // `MinionSpawnerWeapon.OnPrepareMinion` - and nothing in either repo calls
    // `CreateEntity`. So everything here is new ground, and two traps in it are
    // worth naming before the code:
    //
    //   * **`CreateEntity(prefab, position)` uses the prefab only for
    //     `CreateData()`.** It then calls `SpawnObjectForEntity`, which
    //     instantiates from `entityPrefabDictionary[entity.entityId]` - the
    //     prefab registered in the `SavablesCollection` under that id. You get
    //     the STOCK prefab back, not anything you customised. That is fine
    //     here, and deliberately so: every piece of dressing is applied to the
    //     live instance afterwards, which is what `ForgeMinionLook` already
    //     does anyway. Nothing on the prefab is ever edited.
    //   * **`SavableEntity.Update` writes `EntityData.MoveTo(transform
    //     .position)` every frame the transform has changed**, and the entity
    //     manager raises `EntityMovedToNewSegment` off that. Kinematic
    //     placement writes the transform every frame, so this path runs
    //     constantly. Stock units move too, so it is a cost to be aware of
    //     rather than a blocker - but it is a per-frame cost per body that an
    //     orb does not pay.
    //
    // ★ THE BODY IS A BODY. Nothing about the weapon, the mirror trigger, the
    // beam pass or the inheritance sweep changes - a drone wingman fires
    // exactly the way an orb does, from the controller's own per-wing
    // `WeaponBase`, equipped to the SHIP. The tempting alternative was to hand
    // it the mirrored weapon through its own grid
    // (`SimpleModuleGrid.InstallMainModule` is public and
    // `ModuleSlotWeaponHolder` would build and fire it) and **it would break
    // saves**: `Module.Memento.Restore()` resolves `moduleDataId` out of the
    // registry and calls `DeepCopy()` on the result WITH NO NULL CHECK, and a
    // wingman weapon clone is created at runtime and released on the next
    // weapon swap - so its id would be written into a save and be missing from
    // the registry on the next load. A NullReferenceException inside the game's
    // own load path, with nothing pointing at us.
    //
    // Keeping `Owner` as the player is also what makes kill credit, friendly
    // fire, `leech`, `surge` and `rank` need no special case anywhere.
    public static class ForgeWingmanBody
    {
        private static readonly ManualLogSource Log =
            ForgeLog.Source("WeaponForge.Wingman");

        // Handed to Shooter.Block as a second lock on the body's own gun. A
        // plain object, because that is what the game's HashSet<object> wants.
        private static readonly object GunLock = new object();

        private static readonly HashSet<string> _said =
            new HashSet<string>();

        // ------------------------------------------------------------------
        // Spawning
        // ------------------------------------------------------------------

        // Returns the live body, or null if anything refused. Every failure is
        // logged and costs the companion, never the run.
        public static Unit Spawn(
            ForgeWingman.Config cfg,
            Unit owner,
            Vector2 at,
            Unit.OwnerConnectionType slot,
            int level,
            IModuleCluster cluster)
        {
            if (cfg == null || !cfg.IsDrone || owner == null)
                return null;

            ForgeWingman.Drone d = cfg.drone;

            if (d.prefab == null)
                return null;

            try
            {
                var savable = d.prefab.GetComponent<SavableEntity>();

                if (savable == null)
                {
                    Warn(cfg, "the body prefab '" + d.minion + "' is not a " +
                         "savable entity, so it cannot be spawned.");
                    return null;
                }

                EntityGameObjectManager manager;

                if (!ServiceLocator.TryGet<EntityGameObjectManager>(out manager) ||
                    manager == null)
                {
                    Warn(cfg, "the entity manager is not available yet, so a " +
                         "companion could not be spawned. Try again in a " +
                         "moment.");
                    return null;
                }

                SavableEntity made = manager.CreateEntity(savable, at);

                if (made == null)
                    return null;

                Unit body = made.GetComponent<Unit>();

                if (body == null)
                    body = made.GetComponentInChildren<Unit>(true);

                if (body == null)
                {
                    UnityEngine.Object.Destroy(made.gameObject);
                    return null;
                }

                body.name = "Forge Wingman " + d.minion;

                // ★ `SetOwner` IS REQUIRED, and it buys three things rather
                // than the one it looks like.
                //
                // The obvious one is the owner link. The other two are why an
                // earlier draft of this design recommended AGAINST calling it
                // and was wrong three times over:
                //
                //   1. PERSISTENCE. `Unit.Data.Memento` stores `ownerId` and
                //      `connectionToOwner` and replays them through SetOwner on
                //      load - AND it stores `resourceValues` for every
                //      non-shared tank. A drone's health is one. So the game's
                //      route brings a damaged companion back DAMAGED, which a
                //      count rebuild cannot do.
                //   2. FAST TRAVEL. `FastTravelManager` walks
                //      `ship.Unit.ComponentData.Minions`, shields each one from
                //      streaming, moves its EntityData to the destination, then
                //      moves the ship, then restores the flag on a timer. A
                //      body registered here is carried for free, coop included.
                //
                // The cost - `SpawnMinionModule.OnUpdate` trimming
                // `unit.Minions.First()`, which is neither ordered nor of its
                // own connection type - is fixed outright in
                // ForgeMinionTrimPatch rather than lived with.
                //
                // > **Ask what a registration BUYS, not only what it exposes
                // > you to.** The cost showed up in the one method I had read;
                // > both benefits were one grep of `.Minions` away.
                SavableEntity ownerEntity = owner.GetComponent<SavableEntity>();

                if (ownerEntity == null)
                    ownerEntity = owner.GetComponentInChildren<SavableEntity>(true);

                if (ownerEntity == null)
                {
                    Warn(cfg, "your ship has no savable entity, so a drone " +
                         "body cannot be owned by it - and without an owner it " +
                         "would not persist or follow you through a fast " +
                         "travel. Refusing the spawn.");

                    UnityEngine.Object.Destroy(made.gameObject);
                    return null;
                }

                body.SetOwner(ownerEntity, slot);

                // Side BEFORE anything reads it. The five drone prefabs are
                // already Player-faction despite their "Enemy_" name, but the
                // other 79 spawnables the key accepts are not, and a hostile
                // body would be shot by your own bullets and ignored by every
                // enemy in the room.
                ForgeMinion.SetSide(body, owner, d.body);

                Dress(cfg, body, owner, level, cluster, true);

                return body;
            }
            catch (Exception e)
            {
                Log.LogError("wingman: spawning a drone body failed: " + e);
                return null;
            }
        }

        // ------------------------------------------------------------------
        // Everything that has to be true of a body, applied or re-applied
        // ------------------------------------------------------------------

        // EVERY LINE OF THIS HAS TO BE RE-RUNNABLE, because a body's GameObject
        // and its DATA have different lifetimes: a load rebuilds the object
        // from the prefab and replays only what a memento carries, so the AI
        // comes back ON, the colour comes back factory and the collision
        // ignores are gone. The stat edits are the exception and they guard
        // themselves - `ForgeMinionStats` keys a ConditionalWeakTable on the
        // Module instance, which survives a rebind and does not survive a load.
        // That is exactly the right split, and it is not ours: it is the same
        // reason `ForgeMinionRestore` exists for the other two minion paths.
        public static void Dress(
            ForgeWingman.Config cfg,
            Unit body,
            Unit owner,
            int level,
            IModuleCluster cluster,
            bool fresh)
        {
            if (cfg == null || !cfg.IsDrone || body == null)
                return;

            ForgeWingman.Drone d = cfg.drone;

            try
            {
                Silence(cfg, body);
                Physics(cfg, body);
                Death(cfg, body);
                HealthBar(cfg, body);

                bool touched = false;

                // ⚠ SYNC, NEVER INSTALL AGAIN. `ModuleGridOwner.Data` has a
                // memento, so the augmentations we install ARE saved and ARE
                // restored - a plain `InheritGrid` on a body the game handed
                // back would stack a second copy of every module in the
                // gadget's cluster, and a third on the next load. That is why
                // this used to run on a FRESH body only.
                //
                // Running on a fresh body only had its own cost, and it is the
                // one he asked to fix: a module slotted beside the gadget
                // AFTER a companion was deployed never reached it, so the ring
                // kept whatever it was born with while the weapon copies
                // rebuilt on the next shot. `ForgeSwarm.SyncGrid` strips what
                // is there and installs the current set, which is idempotent -
                // so one call covers birth, re-adoption after a load, and a
                // grid the player edited a moment ago, and none of them can
                // stack.
                //
                // The STATS are a different case and that asymmetry is the
                // interesting part. `Module.Memento.Restore()` resolves the
                // module data out of the registry and `DeepCopy()`s it, so a
                // restored module carries the ASSET's effects rather than the
                // ones we edited - our health, resources, regen and shield are
                // gone. They have to be re-applied, which is exactly why
                // `ForgeMinionRestore` exists for the other two minion paths.
                //
                // So: the grid is MIRRORED, the stat edits are RE-APPLIED, and
                // which of those a thing needs is a property of what carries a
                // memento rather than of anything we chose.
                if (d.inheritModules && cluster != null)
                {
                    ForgeSwarm.SyncGrid(cluster, body, false);
                    touched = true;
                }

                if (d.body.HasStats)
                {
                    // Guards itself against a double application within one
                    // GameObject lifetime - ForgeMinionStats keys a
                    // ConditionalWeakTable on the Module instance, which
                    // survives a rebind and does not survive a load. Both
                    // halves of that are what we want here.
                    ForgeMinionStats.Apply(
                        body, owner, d.body, Mathf.Max(1, level), false);

                    touched = true;
                }

                // ★ AND A LOADED BODY IS NOT REFILLED. `Unit.Data.Memento`
                // stores every non-shared tank value, so a companion you saved
                // on 12 health comes back on 12 - which is the entire reason
                // drone bodies persist through the GAME's save system rather
                // than through the orb count. Refilling here would hand back a
                // fresh one and make the difference invisible.
                if (touched)
                    ForgeMinionStats.Recalculate(body, fresh);

                // Colour and scale. `wingman` owns this vocabulary, so the
                // drone block never grows its own `color` / `scale` - two
                // vocabularies for one idea is the trap this project keeps
                // writing down. `d.body` carries copies of the wingman keys and
                // ForgeMinionLook does the work, including the two scoping
                // rules a unit needs and an orb does not: stay inside the
                // `Visual_*` subtree so the AI state indicators keep their own
                // colours, and never paint a renderer held at alpha 0.
                ForgeMinionLook.Apply(body, d.body);

                // colorPulse is a PER-FRAME colour, and a per-frame colour on a
                // unit must go through ForgeUnitTint - never `sr.color`. The
                // capture-and-restore shape is what makes the FIRST tint
                // permanent once a second feature (slow, slam, contact) touches
                // the same unit. Published once with its own pulse rate; the
                // table ticks it.
                if (cfg.hasColor && cfg.colorPulse > 0f)
                {
                    ForgeUnitTint.Set(
                        body, TintKey, cfg.color, 0f, cfg.colorPulse, 1f,
                        ForgeUnitTint.Replace);
                }

                ForgeMinionSolid.Apply(body, owner, d.body, Label(cfg));

                Contact(cfg, body, owner);
            }
            catch (Exception e)
            {
                Log.LogError("wingman: dressing a drone body failed: " + e);
            }
        }

        public const string TintKey = "wingman";

        // ------------------------------------------------------------------
        // Keeping a LIVE body in step with a grid the player just edited
        // ------------------------------------------------------------------

        // Deliberately NOT `Dress`. A grid edit should move the body's modules
        // and nothing else: re-dressing would re-publish the tint layer, re-run
        // `ForgeMinionSolid`'s collision-ignore loops and re-`Give` the contact
        // rule, which re-runs its Setup - work with visible side effects for a
        // question that is only about the grid.
        //
        // ★ AND IT MUST NOT REFILL. `SyncGrid`'s own recalculate path ends in
        // `RefillResources`, which here would be a free heal every time you
        // touched your module grid - slot a card, unslot it, full companions.
        // So the recalculation is done by hand with `refill: false`, exactly as
        // the load path does it, and for the same reason: a drone body's health
        // is state you are supposed to keep.
        //
        // The stat edits are not re-applied and do not need to be. They live as
        // effects on the body's OWN main module, which `Strip` never touches -
        // so they are still there, and `RecalculateStats` reads them back into
        // capacities along with whatever the new grid contributes. Calling
        // `ForgeMinionStats.Apply` again would be a guarded no-op on the happy
        // path and a doubled health pool on any day its guard missed.
        public static bool Resync(
            ForgeWingman.Config cfg, Unit body, int level, IModuleCluster cluster)
        {
            if (cfg == null || !cfg.IsDrone || body == null)
                return false;

            ForgeWingman.Drone d = cfg.drone;

            if (!d.inheritModules || cluster == null)
                return false;

            try
            {
                ForgeSwarm.SyncGrid(cluster, body, false);
                ForgeMinionStats.Recalculate(body, false);

                return true;
            }
            catch (Exception e)
            {
                Log.LogError(
                    "wingman: re-syncing a drone body's modules failed: " + e);

                return false;
            }
        }

        // ------------------------------------------------------------------
        // Switching the brain off
        // ------------------------------------------------------------------

        // ONE `SetActive(false)`, AND THE GAME WROTE IT.
        //
        // The AI lives under a single child (`AI_Fly` on the flying drones,
        // `AI_Cross` / `AI_Maggot` elsewhere) which holds the top-level
        // `StateMachine`; the twelve `State`s, sixteen `Transition`s, the
        // movement actions and the lone `ShootAction` all hang below it.
        // Disabling that child stops every one of them, and
        // `MovementAction.OnDisable` calls `movement.Stop()` on the way out, so
        // the body comes to rest rather than drifting.
        //
        // It is the game's own idiom: `DamagableResource.EnterCorpseState` does
        // exactly `corpseSettings.ai.SetActive(false)`. We cannot borrow that
        // FIELD - the drone prefabs ship `hasCorpseState: 0` and
        // `corpseSettings.ai: {fileID: 0}` - so the child is found by looking
        // for the shallowest StateMachine, which is name-independent.
        //
        // Timing is a gift here. `Instantiate` runs `StateMachine.Awake`
        // immediately (which disables every state child), and `Start` - the
        // thing that would enter the first state - does not run on an inactive
        // GameObject. So a body spawned by us never starts thinking at all.
        // After a LOAD it may have had a frame, which is why `MovementAction`'s
        // stop-on-exit matters.
        private static void Silence(ForgeWingman.Config cfg, Unit body)
        {
            Type sm = TypeOf("StateMachine");
            GameObject root = null;
            int best = int.MaxValue;

            if (sm != null)
            {
                foreach (Component c in body.GetComponentsInChildren(sm, true))
                {
                    if (c == null)
                        continue;

                    int depth = Depth(body.transform, c.transform);

                    if (depth >= 0 && depth < best)
                    {
                        best = depth;
                        root = c.gameObject;
                    }
                }
            }

            if (root != null && root != body.gameObject)
            {
                if (root.activeSelf)
                    root.SetActive(false);
            }
            else
            {
                // No state machine, or one sitting on the root (which we must
                // not disable - that would switch off the body itself). Fall
                // back to disabling the AI components individually. Said out
                // loud, because a body that still thinks is a companion that
                // wanders off and it must not look like a physics bug.
                int off = 0;

                foreach (MovementAction a in
                         body.GetComponentsInChildren<MovementAction>(true))
                {
                    if (a != null && a.enabled)
                    {
                        a.enabled = false;
                        off++;
                    }
                }

                var agent = body.GetComponentInChildren<AIAgent>(true);

                if (agent != null && agent.enabled)
                {
                    agent.enabled = false;
                    off++;
                }

                Once(cfg, "ai-fallback",
                     "wingman drone body '" + body.name + "' has no state " +
                     "machine under its root, so its brain was switched off " +
                     "component by component instead (" + off + " disabled). " +
                     "If it drifts or shoots on its own, that is why - use one " +
                     "of the five drone prefabs, which all have one.");
            }

            // A SECOND LOCK ON ITS OWN GUN, and it is cheap insurance rather
            // than duplication. `Shooter` sits on the ROOT on every drone
            // prefab - only the `ShootAction` that drives it is under the AI -
            // so anything that ever re-enables a state child would find a
            // working gun waiting. `Shooter.Block` is public and its blocker
            // set is keyed by object, so ours cannot collide with overheat's or
            // stun's.
            var shooter = body.GetComponentInChildren<Shooter>(true);

            if (shooter != null)
            {
                try { shooter.Block(GunLock); } catch { }
            }
        }

        private static int Depth(Transform root, Transform of)
        {
            int n = 0;

            for (Transform t = of; t != null; t = t.parent)
            {
                if (t == root)
                    return n;

                n++;
            }

            return -1;
        }

        // ------------------------------------------------------------------
        // Kinematic or physical
        // ------------------------------------------------------------------

        private static void Physics(ForgeWingman.Config cfg, Unit body)
        {
            Rigidbody2D rb = body.GetComponent<Rigidbody2D>();

            if (rb == null)
                rb = body.GetComponentInChildren<Rigidbody2D>(true);

            if (rb == null)
                return;

            if (!cfg.drone.physics)
            {
                // KINEMATIC: the body sits exactly where the controller puts
                // it, so all four motions, five aim modes and both bunchings
                // work unchanged - a drone wingman formats identically to an
                // orb one. Nothing pushes it: not an explosion, not the ship,
                // not terrain.
                rb.linearVelocity = Vector2.zero;
                rb.angularVelocity = 0f;
                rb.bodyType = RigidbodyType2D.Kinematic;
                return;
            }

            rb.bodyType = RigidbodyType2D.Dynamic;

            // `rotate` writes `transform.rotation` directly, which on a
            // rigidbody fights the two `LimitAngularVelocity` components every
            // drone prefab carries. Freezing rotation and keeping the transform
            // write is the simpler of the two honest answers (the other being
            // MoveRotation), and it costs nothing here because the spin those
            // components exist to damp belongs to an AI that is switched off.
            rb.constraints |= RigidbodyConstraints2D.FreezeRotation;
        }

        // ------------------------------------------------------------------
        // Death
        // ------------------------------------------------------------------

        // ★ A FEATURE THAT ADDS A NEW STATE ADDS EVERYTHING THAT STATE
        // TRIGGERS. An orb could not die, so nothing downstream of dying had
        // ever been looked at - and the stock drone's `spawnOnDeath` is a real
        // explosion that throws burn in a 2-unit radius. The ring sits at 2.2.
        // So with this on by default, losing a companion would set the ship on
        // fire, at a distance that is geometry rather than luck.
        //
        // AND THERE ARE TWO FIELDS, NOT ONE. `DamagableResource.spawnOnDeath`
        // is the obvious one; `DestroyWhenResourceDrained` carries its own
        // (private, hence the reflection), and while that component is inert on
        // a stock drone - it watches `Resource Fuel` and the embedded module
        // grants no Fuel tank - a file that adds a Fuel pool under
        // `drone.resources` wakes it up. Stripping one and not the other is
        // exactly the kind of half-fix that reads as intermittent.
        // ★ THE HEALTH BAR, through the game's own two fields. One segment
        // per point of capacity and one endless row by default is how a
        // 120-health companion came to wear a bar across the whole ship
        // (R19 tests 258/261).
        //
        // ⚠ RE-REGISTER ONLY A BAR THE GAME HAS ALREADY REGISTERED. The
        // manager builds its widget in Add, reading maxUnitsPerRow once, so a
        // body the game handed back after a load needs Remove + Add to pick
        // new settings up. But HealthbarOwner.Start calls Add itself, and a
        // second Add before Start would have the manager create a SECOND
        // widget under the same dictionary key - an ArgumentException inside
        // the game's Start. `Resources` is filled in Start, so a non-empty
        // one is the tell that Start has run. A fresh spawn has not, and
        // simply gets its fields set before the game reads them.
        private static void HealthBar(ForgeWingman.Config cfg, Unit body)
        {
            ForgeWingman.Drone d = cfg.drone;

            bool hide = d.healthBar == "hide";
            bool flash = d.healthBar == "flash";

            // "flash" starts exactly as "hurt" does - no bar until the first
            // hit - and ForgeWingmanBar then takes it away again.
            bool hurt = d.healthBar == "hurt" || flash;

            bool shaped = flash || d.healthBarRows > 0 ||
                          Mathf.Abs(d.healthBarScale - 1f) > 0.001f;

            if (!hide && !hurt && d.healthBarRow <= 0 && !shaped)
                return;

            // Rows, scale and the fade live on a component, because all three
            // act on the WIDGET - which the game makes later (on Start, or on
            // the first hit) and remakes whenever the stats recalculate.
            if (!hide && shaped)
                ForgeWingmanBar.Attach(body, d.healthBarRows, d.healthBarScale,
                                       flash ? d.healthBarSeconds : 0f);

            foreach (HealthbarOwner hb in
                     body.GetComponentsInChildren<HealthbarOwner>(true))
            {
                if (hb == null)
                    continue;

                try
                {
                    if (hide)
                    {
                        // Its own OnDestroy unregisters it and drops the
                        // widget, which is the whole of what hiding needs.
                        UnityEngine.Object.Destroy(hb);
                        continue;
                    }

                    if (hurt)
                        Field(typeof(HealthbarOwner), "showWhenFull")
                            ?.SetValue(hb, false);

                    if (d.healthBarRow > 0)
                        Field(typeof(HealthbarOwner), "maxUnitsPerRow")
                            ?.SetValue(hb, d.healthBarRow);

                    if (hb.Resources != null && hb.Resources.Count > 0)
                    {
                        HealthbarManager mgr;

                        if (ServiceLocator.TryGet<HealthbarManager>(out mgr) &&
                            mgr != null)
                        {
                            mgr.Remove(hb);
                            mgr.Add(hb);
                        }
                    }
                }
                catch (Exception e)
                {
                    // Cosmetic. A bar that cannot be changed is left as
                    // the game drew it; the companion is unaffected.
                    Warn(cfg, "the health bar could not be changed (" +
                         e.GetType().Name + "), so it is left as the game " +
                         "draws it.");
                }
            }
        }

        private static void Death(ForgeWingman.Config cfg, Unit body)
        {
            if (cfg.drone.deathBlast)
                return;

            foreach (DamagableResource dr in
                     body.GetComponentsInChildren<DamagableResource>(true))
            {
                if (dr != null)
                    dr.spawnOnDeath = null;
            }

            System.Reflection.FieldInfo f =
                Field(typeof(DestroyWhenResourceDrained), "spawnOnDeath");

            if (f == null)
                return;

            foreach (DestroyWhenResourceDrained dw in
                     body.GetComponentsInChildren<DestroyWhenResourceDrained>(true))
            {
                if (dw == null)
                    continue;

                try { f.SetValue(dw, null); } catch { }
            }
        }

        // ------------------------------------------------------------------
        // Contact damage
        // ------------------------------------------------------------------

        private static void Contact(
            ForgeWingman.Config cfg, Unit body, Unit owner)
        {
            if (cfg.drone.contact == null)
                return;

            ForgeContactController.Give(
                body, cfg.drone.contact, owner, null, 0f, false);
        }

        // ------------------------------------------------------------------
        // Wingman-to-wingman collision
        // ------------------------------------------------------------------

        // `Physics2D.IgnoreCollision` rather than a layer swap, for the reason
        // `ForgeMinionSolid`'s header sets out at length: a unit's layer is
        // load-bearing (Vision scans by layer, a projectile's faction IS its
        // layer), and an ignored PAIR is still returned by queries - so vision,
        // casts and our own placement maths are untouched. Only the shoving
        // stops.
        public static void Separate(Unit a, Unit b, bool ignore)
        {
            if (a == null || b == null)
                return;

            try
            {
                Collider2D[] mine = a.GetComponentsInChildren<Collider2D>(true);
                Collider2D[] theirs = b.GetComponentsInChildren<Collider2D>(true);

                for (int i = 0; i < mine.Length; i++)
                {
                    if (mine[i] == null)
                        continue;

                    for (int j = 0; j < theirs.Length; j++)
                    {
                        if (theirs[j] == null)
                            continue;

                        Physics2D.IgnoreCollision(mine[i], theirs[j], ignore);
                    }
                }
            }
            catch (Exception)
            {
                // A comfort setting must never cost a spawn.
            }
        }

        // ------------------------------------------------------------------
        // Taking one away
        // ------------------------------------------------------------------

        // ★ `Destroy`, NOT `Die()` - and this is the opposite of the rule the
        // rest of the minion code follows.
        //
        // `ForgeMinionLife` deliberately calls `HealthBase.Die()` because that
        // is what the game's own cap trim does, so death effects, drops and
        // listeners all behave as though it died normally. A RECALL is not a
        // death: `Die()` would play the death particle, fire `onDeath`, credit
        // whatever `ForgeKillsPatch` had stamped, and - with `deathBlast` on -
        // detonate a companion you simply put away. Stowing the ring would
        // carpet-bomb your own position.
        //
        // `SavableEntity.OnDestroy` calls `EntityData.Destroy()`, which runs
        // `OnDestroy` on every ComponentData, and `Unit.Data.OnDestroy` removes
        // the body from the owner's minion set. So a plain Destroy leaves no
        // ghost entry filling anybody's cap. Checked rather than assumed.
        public static void Take(Unit body)
        {
            if (body == null)
                return;

            try
            {
                ForgeUnitTint.Clear(body, TintKey);
            }
            catch (Exception)
            {
            }

            try
            {
                UnityEngine.Object.Destroy(body.gameObject);
            }
            catch (Exception e)
            {
                Log.LogError("wingman: recalling a drone body failed: " + e);
            }
        }

        // ---- R19 test 259 instrument ----------------------------------
        //
        // "7/10 Resource Caps" - the tank the body DIES on, which is
        // `DamagableResource.resource` (a drone's health is Resource Caps, not
        // Health) - the same resource the game's own health bar reads in
        // HealthbarOwner.CollectResourcesToDisplay. NOT the resource
        // `DestroyWhenResourceDrained` watches: on a stock drone that one is
        // Fuel, which the body has no tank for (see Death above), so reading
        // it would have printed "no tank" for every companion - an instrument
        // that lies in exactly the case it exists to describe. Never throws:
        // a diagnostic must not cost the thing it describes.
        public static string HealthText(Unit body)
        {
            try
            {
                if (body == null)
                    return "no body";

                DamagableResource dr = body.GetComponent<DamagableResource>();

                if (dr == null)
                    dr = body.GetComponentInChildren<DamagableResource>(true);

                Resource res = (dr != null) ? dr.resource : null;

                if (res == null)
                    return "health unknown (no damageable resource)";

                ResourceTank tank = body.GetTank(res);

                if (tank == null)
                    return "NO " + res.name + " tank at all";

                return tank.Value.ToString("0.#") + "/" +
                       tank.Capacity.ToString("0.#") + " " + res.name;
            }
            catch (Exception e)
            {
                return "health unreadable (" + e.GetType().Name + ")";
            }
        }

        // ------------------------------------------------------------------
        // Finding a body again
        // ------------------------------------------------------------------

        // A minion's DATA outlives its GameObject, and after a load the
        // GameObject is a fresh instance bound to restored data. This is the
        // route back: the manager's own id lookup, which is what
        // `SpawnMinionModule.DestroyUnit` and `ForgeMinionRestore.OwnerUnit`
        // both use.
        public static Unit Resolve(Unit.Data data)
        {
            if (data == null || data.entity == null)
                return null;

            try
            {
                EntityGameObjectManager manager;

                if (!ServiceLocator.TryGet<EntityGameObjectManager>(out manager) ||
                    manager == null)
                {
                    return null;
                }

                SavableEntity found;

                if (!manager.TryGetSavableEntity(
                        data.entity.instanceId, out found) || found == null)
                {
                    return null;
                }

                Unit unit = found.GetComponent<Unit>();

                return (unit != null) ? unit : found.GetComponentInChildren<Unit>(true);
            }
            catch (Exception)
            {
                return null;
            }
        }

        // ------------------------------------------------------------------
        // Build-time resolve
        // ------------------------------------------------------------------

        // Refuses a bad body BEFORE it can throw inside the game's own code,
        // and falls back to the DEFAULT DRONE rather than to an orb. Falling
        // back to an orb would make a damageable feature silently
        // undamageable, which is the whole point of the setting.
        public static bool ResolveBody(
            ForgeWingman.Config cfg, string fileName)
        {
            if (cfg == null || cfg.drone == null)
                return false;

            ForgeWingman.Drone d = cfg.drone;

            string want = (d.minion ?? "").Trim();

            if (want.Length == 0)
                want = ForgeWingman.DefaultBody;

            if (string.Equals(want, ForgeWingman.RefusedBody,
                              StringComparison.OrdinalIgnoreCase))
            {
                Log.LogWarning(
                    fileName + ": wingman \"drone.minion\": '" +
                    ForgeWingman.RefusedBody + "' is refused as a body, and " +
                    "for a measurable reason rather than a taste one - it is " +
                    "the only drone on layer 8 (PlayerProjectiles), which " +
                    "EnemyProjectiles does NOT collide with. Enemy fire could " +
                    "not touch it, so \"damageable\" would be a lie. It is a " +
                    "flying bomb, not a companion. Using '" +
                    ForgeWingman.DefaultBody + "' instead.");

                want = ForgeWingman.DefaultBody;
                d.fellBack = true;
            }

            d.prefab = ForgeDrone.FindMinion(want, fileName);

            if (d.prefab == null && !string.Equals(
                    want, ForgeWingman.DefaultBody,
                    StringComparison.OrdinalIgnoreCase))
            {
                Log.LogWarning(
                    fileName + ": wingman \"drone.minion\": falling back to '" +
                    ForgeWingman.DefaultBody + "'. A drone body that cannot be " +
                    "resolved must not quietly become an orb - an orb cannot " +
                    "be hit at all, and being hittable is the entire point of " +
                    "\"body\": \"drone\".");

                d.prefab = ForgeDrone.FindMinion(
                    ForgeWingman.DefaultBody, fileName);

                d.fellBack = true;
            }

            d.minion = want;

            return d.prefab != null;
        }

        // ------------------------------------------------------------------
        // Small shared helpers
        // ------------------------------------------------------------------

        private static string Label(ForgeWingman.Config cfg)
        {
            return (cfg != null && !string.IsNullOrEmpty(cfg.fileName))
                ? cfg.fileName
                : "(wingman)";
        }

        private static void Warn(ForgeWingman.Config cfg, string message)
        {
            Log.LogWarning(Label(cfg) + ": wingman - " + message);
        }

        private static void Once(
            ForgeWingman.Config cfg, string key, string message)
        {
            if (!_said.Add(key))
                return;

            Log.LogWarning(Label(cfg) + ": " + message + " Said once per run.");
        }

        // Cached for the same reason ForgeMinionStats caches its own:
        // `AccessTools.TypeByName` SCANS EVERY LOADED ASSEMBLY, and BepInEx
        // reports around 200 of them. A null result is cached too - a type the
        // game does not have must not re-scan on every spawn just to fail
        // again.
        private static readonly Dictionary<string, Type> _types =
            new Dictionary<string, Type>();

        private static Type TypeOf(string name)
        {
            Type found;

            if (_types.TryGetValue(name, out found))
                return found;

            try { found = AccessTools.TypeByName(name); }
            catch { found = null; }

            _types[name] = found;

            return found;
        }

        private static readonly Dictionary<string, System.Reflection.FieldInfo>
            _fields = new Dictionary<string, System.Reflection.FieldInfo>();

        // Plain FieldInfo, deliberately NOT Harmony's FieldRefAccess<T,F> -
        // a value-type mismatch there throws rather than returning null, and
        // that has bitten this codebase.
        private static System.Reflection.FieldInfo Field(Type owner, string name)
        {
            string key = owner.FullName + "." + name;
            System.Reflection.FieldInfo found;

            if (_fields.TryGetValue(key, out found))
                return found;

            found = AccessTools.Field(owner, name);
            _fields[key] = found;

            return found;
        }

        public static void Reset()
        {
            _said.Clear();
        }
    }
}
