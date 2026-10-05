using System.Collections.Generic;
using UnityEngine;

namespace WeaponForge
{
    // Config + registry for `wingman`: a GADGET that puts companions around
    // your ship which fire a COPY of your own weapon's shot whenever you fire.
    //
    // The full design is in `Docs/WINGMEN - CONCEPT.md`; this file
    // is only the data. Three things about it are worth knowing before reading
    // anything else:
    //
    // 1. **The gadget has no template of its own.** Its weapon is whatever you
    //    are carrying, cloned at runtime - see `ForgeWingmanWeapon`. That makes
    //    it the first Weapon Forge file whose weapon is not known until play.
    //
    // 2. **"Modules on the gadget upgrade the copies" costs nothing.**
    //    `WeaponBasedActiveModule.OnContainingClusterRefreshed` already builds
    //    its weapon with `WeaponFactory.Create(WeaponData,
    //    cluster.ConnectedAndPoweredModules)`. Give it our clone and the game
    //    does the rest.
    //
    // 3. **`count` is a CAP, not a starting number.** You deploy wingmen one at
    //    a time with the gadget key, the way Gradius options are collected, and
    //    a BoosterCore raises the ceiling.
    public static class ForgeWingman
    {
        // Which trigger the companions mirror.
        public enum Mirror { Primary, Secondary, Both }

        // What the companion IS. `Orb` is pure art - no health, no collider,
        // untargetable, enemy shots pass through. `Drone` is a REAL UNIT:
        // hittable, targetable, killable.
        //
        // ★ THAT DISTINCTION IS NOT ABOUT LOOKS, and the docs have to say so.
        // `ForgeWingmanArt.FindPrefab` searches loaded GameObjects by name, so
        // `"visual": "Enemy_Fly Variant Drone"` has always given you a drone's
        // art with all 103 of its MonoBehaviours stripped, floating as a
        // phantom. `body: "drone"` buys exactly one thing on top of that:
        // being a unit in the world.
        //
        // AND THERE IS NO CHEAPER VERSION. Decoded from
        // `Physics2DSettings.m_LayerCollisionMatrix`, every layer
        // `EnemyProjectiles` reaches `PlayerProjectiles` reaches too - the set
        // difference in that direction is EMPTY - so there is no layer in this
        // game that enemies can shoot and your own fire passes through. The
        // only thing separating the two is the friendly-fire skip in
        // `Projectile.FixedUpdate`, and it is keyed on the hit's rigidbody
        // carrying a `Unit` friendly to `Owner`. A mod-made GameObject has no
        // `Unit` and cannot be given one - it is a `SavableComponent<Unit.Data>`
        // bound by the entity system. So a damageable body HAS to be a spawned
        // entity, and that is why the orb came first.
        public enum Body { Orb, Drone }

        // How they sit around the ship.
        //   Orbit - classic ring, rotating at `spinSpeed`.
        //   Trail - strung out behind you along the path you actually flew.
        //   Drag  - on the ring, sliding to the side OPPOSITE a bearing.
        //   Lead  - the same, on the side ALONG it.
        public enum Motion { Orbit, Trail, Drag, Lead }

        // What `drag` / `lead` measure their side from.
        public enum Bearing { Movement, Aim, Both }

        // What "movement" means. Input is crisp and goes to zero the instant
        // you stop pressing; velocity is what is actually happening to you and
        // is the only one that sees knockback, gravity wells and dashes.
        public enum Signal { Both, Velocity, Input }

        // How the beads avoid stacking on one another.
        public enum Bunching { Fan, Repel }

        // Where they go when there is no movement bearing to be opposite of.
        // Never consulted under `Bearing.Aim` - a cursor always has a position.
        public enum AtRest { Relax, Hold, HoldThenRelax }

        // Where the copies point.
        //   Parallel - along the ship's own aim. Shots stay side by side.
        //   Cursor   - each companion aims AT the crosshair, so shots cross.
        //   Mirrored - angles mirrored about your aim, opening outward.
        //   Nearest  - each picks its own closest enemy.
        //   Outward  - along the line from the ship THROUGH that companion.
        // None - the ring never fires (R19, his idea): a pure tackle ring
        // with drone.contactDamage, or decoration. On an ORB it does nothing
        // at all - an orb has no hitbox - and the build log says so.
        public enum Aim { Parallel, Cursor, Mirrored, Nearest, Outward, None }

        // What the gadget key does.
        //   Deploy - put out one more, up to the cap. The default, and the
        //            reason `count` is a cap.
        //   Toggle - first press sends the WHOLE complement out, next stows it.
        //   Burst  - they are already out; the key makes them all fire at once.
        //   Recall - they are already out; the key stows them, and a second
        //            press sends them back.
        //   None   - they are already out and the key does nothing.
        //
        // THE LAST THREE ARE DEPLOYED FOR YOU, and until R16 they were not:
        // `burst` fired a volley from a complement nobody had spawned, `recall`
        // stowed companions that were never out, and `toggle`'s first press
        // toggled OFF from an empty ring. Three of the five key modes could not
        // put a single wingman on screen. See `AutoDeploys`.
        public enum Key { Deploy, Toggle, Burst, Recall, None }

        // Pressing deploy while already at the cap.
        public enum AtCap { RecallOne, RecallAll, Refuse }

        // Which way a body's art points.
        //   Fire   - where its next shot will actually go. Never lies.
        //   Cursor - the crosshair, even when firing elsewhere.
        //   Travel - the way it is sliding around the ring.
        public enum Rotate { Fire, Cursor, Travel, Off }

        // The stock body, and the one that is refused.
        //
        // THE FIVE DRONE GADGET ASSETS FOLLOW TWO DIFFERENT NAMING RULES, which
        // is why nobody can remember them: three are named after the resource
        // they cost (Caps, Electron, Purple) and two after what they DO
        // (Phantom, Suicide). So **the Tech drone is `Phantom`** and the Health
        // drone is `Suicide`, and neither says so in its name or its display
        // name. `Module Active Drone *` is the GADGET; the key below names the
        // BODY prefab it spawns.
        public const string DefaultBody = "Enemy_Fly Variant Drone";

        // Refused for a measurable reason rather than a taste one: the suicide
        // drone sits on layer 8 (`PlayerProjectiles`), which `EnemyProjectiles`
        // does not collide with - so enemy fire cannot touch it and
        // "damageable" would be a lie. It is a flying bomb, not a companion.
        public const string RefusedBody = "Enemy_Fly Variant Drone Suicide";

        // `wingman.drone` - everything about a damageable body.
        //
        // THE STATS LIVE INSIDE THIS BLOCK rather than reusing the top-level
        // `minion` one. Two reasons, and the second is a correctness rule
        // rather than a layout preference: the page can offer only the keys
        // that do something (nine of `minion`'s fifty-one survive the fact that
        // a wingman body's AI and gun are switched off), and all 51 `minion.*`
        // controls live inside `<section class="type-section" data-type=
        // "minion">`, which `isIncluded()` SKIPS whenever the chosen template
        // is not a minion spawner. A wingman file's template is a weapon or a
        // gadget, so every one of those fields would have emitted nothing -
        // the cross-type trap that has shipped four times in that page.
        //
        // They are CARRIED in a `ForgeMinion.Config`, because
        // `ForgeMinionStats`, `ForgeMinionSolid` and `ForgeMinionLook` already
        // know how to apply exactly these - including the one thing that would
        // otherwise be a silent no-op: **"health" is whichever resource this
        // unit's `DamagableResource` points at, and on every stock drone that
        // is Caps, not Health.**
        public class Drone
        {
            public string minion = DefaultBody;

            // Resolved at build time by ForgeDrone.FindMinion, which already
            // refuses a unit with no SavableEntity - the thing that would
            // otherwise throw inside the game's own CreateEntity.
            [System.NonSerialized] public Unit prefab;

            // false = the body sits exactly where it is put (kinematic, the
            // default, and the only mode in which all four motions and both
            // bunchings are exact). true = a real rigidbody that gets knocked
            // about by blasts, held on the ring by a spring.
            public bool physics;

            // Seconds before a dead companion comes back. 0 = off, which is
            // the default: gone until you press the gadget key again. Same
            // shape and same default as `decay`, which is its mirror.
            public float respawn;

            // Whether a respawn charges the gadget's ActivationCost. When it
            // does and you cannot afford it, the respawn WAITS rather than
            // being skipped - a skipped respawn is a companion that silently
            // never returns.
            public bool respawnCost;

            // Install the gadget cluster's non-weapon modules into the body,
            // literally, the way SpawnMinionModule.Activate does. The weapon
            // half is dead weight here (the body's own gun is off) and already
            // reaches the COPIES through ModulesForCopies(), so skipping it is
            // correct and free rather than a compromise.
            public bool inheritModules = true;

            // The body's `spawnOnDeath` is STRIPPED unless this asks for it.
            // Off by default because the stock drone's death explosion throws
            // burn in a 2-unit radius and the ring sits at 2.2 - on by default
            // would set the ship on fire every time one died.
            public bool deathBlast;

            // THE HEALTH BAR. The game draws one segment per point of
            // capacity and a drone's HealthbarOwner ships maxUnitsPerRow 0 -
            // one endless row - so a 120-health companion wore a bar 120
            // segments wide across the ship (R19 tests 258/261). "full" is
            // the game's bar and the default; "hurt" hides it until the
            // companion is first damaged; "hide" removes it. healthBarRow
            // wraps it at that many segments per row. Both are the game's
            // OWN fields, set by reflection.
            public string healthBar = "full";
            public int healthBarRow;

            // R19 test 278, his three asks on top of the three above:
            //
            //   "flash"  - "I figured with "healthBar": "hurt" it would show for
            //              a moment (like 2 or 3 seconds) then fade away again."
            //              "hurt" cannot - the game never hides a bar once it
            //              has appeared - so this is a fourth mode that does:
            //              shown on each hit, faded out `healthBarSeconds`
            //              after the LAST hit.
            //   healthBarRows - "wrapped the width but not the height ... add
            //              total number of bar height as well or how many bars to
            //              have." The number of ROWS; segments per row is then
            //              worked out from the companion's own capacity. Wins
            //              over healthBarRow when both are set.
            //   healthBarScale - the whole bar, both ways. The honest answer to
            //              "shrink the height as well": wrapping trades width for
            //              height, only scale makes it smaller.
            public int healthBarRows;
            public float healthBarScale = 1f;
            public float healthBarSeconds = 3f;

            // Wingman-to-wingman collision. Moot under the default kinematic
            // body (two kinematic rigidbodies do not push each other), so this
            // is a `physics: true` setting in practice.
            public bool solidToEachOther = true;

            // Absent = no contact damage, which IS the off switch.
            public ForgeContact.Config contact;

            // The nine keys that transfer, plus the colour and scale the
            // wingman block already owns - copied in, so one vocabulary drives
            // both kinds of body.
            public ForgeMinion.Config body = new ForgeMinion.Config();

            // For the log: whether the file asked for a body we refused.
            [System.NonSerialized] public bool fellBack;
        }

        public class Config
        {
            // ---- how many, and how you get them ----------------------
            public int count = 2;                 // the CAP
            public Key key = Key.Deploy;
            public AtCap atCap = AtCap.RecallOne;
            public float decay;                   // seconds; 0 = forever

            // Extra cap per module LEVEL above 1, so a BoosterCore beside the
            // gadget is worth something. Unlike `minion`'s `capPerLevel` this
            // defaults to 1 rather than 0, because the build log has promised
            // "a BoosterCore beside it raises the cap" since the first build
            // and R16 test 207 failed on exactly that promise. Set 0 for a
            // fixed cap.
            //
            // It needs `ModuleData.canBeBoosted`, which every stock WEAPON
            // module ships OFF - and a wingman file's template is usually a
            // weapon. The builder switches it on; see ApplyBoostability.
            public int capPerLevel = 1;

            // Deployed companions come back when you load the save you were
            // carrying them in. Only the COUNT is stored - the bodies are
            // rebuilt from this file, so editing it between saving and loading
            // takes effect. See ForgeWingmanSave.
            //
            // Off makes the gadget a per-session summon: you load with none and
            // press the key again. The natural pairing is with `decay`, where a
            // companion is meant to be temporary anyway.
            public bool persist = true;

            // ---- what they copy --------------------------------------
            public Mirror mirror = Mirror.Primary;
            public float fireRatio = 1f;          // copies per shot you fire
            public float damageScale = 1f;
            public bool inherit = true;           // the source weapon's own
                                                  // Forge features - see
                                                  // ForgeWeaponAlias

            // Hand the copies the modules slotted around YOUR gun as well as
            // the ones slotted around the gadget.
            //
            // Without this a wingman fires the weapon's UNUPGRADED shot: a
            // module's effect is an `IWeaponModifier` that writes to the built
            // `WeaponBase`, not to the `WeaponData` asset, and the asset is all
            // a clone can see. R16: *"if i place a +1 projectile on the left
            // main weapon and one on Wingman they shoot out 2 projectiles
            // instead of 3."* Two, because only the gadget's own module was
            // ever counted.
            public bool inheritUpgrades = true;

            // ---- the body --------------------------------------------
            public Body body = Body.Orb;

            // Only read when `body` is `Drone`, and never null then - the
            // builder creates it. Null everywhere else, which is also the
            // cheapest "is this an orb?" test on the per-frame paths.
            public Drone drone;

            public bool IsDrone
            {
                get { return body == Body.Drone && drone != null; }
            }

            public string visual;                 // ammo / pickup / drone
                                                  // prefab, or a custom sprite
            public bool hasColor;
            public Color color = Color.white;
            public float scale = 1f;

            // Dressing. `pulse` and `spin` both write localScale, so they are
            // mutually exclusive and the builder says which one won.
            public bool pulse = true;
            public float pulseAmount = 0.12f;     // +/- fraction of normal size
            public float pulseSpeed = 0.95f;      // throbs per second
            public bool spin;                     // the coin turn instead
            public float spinRate = 3f;           // radians/sec of the turn
            public float spinThinness = 0.15f;    // narrowest face; never 0
            public bool spinHorizontal;
            public float colorPulse;              // 0 = steady
            public Rotate rotate = Rotate.Fire;

            // ---- where they sit --------------------------------------
            public Motion motion = Motion.Orbit;
            public float radius = 2.2f;           // orbit / drag / lead
            public float spinSpeed = 60f;         // ORBIT ONLY: ring deg/sec
            public float spacing = 1.2f;          // trail: gap along the path
            public float smoothing = 0.15f;       // trail: follow tightness

            // drag / lead only.
            public Bearing bearing = Bearing.Movement;
            public Signal signal = Signal.Both;
            public float spread = 50f;            // fan width in degrees
            public float looseness = 0.25f;       // seconds-ish to reach target
            public Bunching bunching = Bunching.Fan;
            public AtRest atRest = AtRest.Relax;
            public float holdSeconds = 1f;        // HoldThenRelax only

            // ---- where they shoot ------------------------------------
            public Aim aim = Aim.Parallel;

            // ---- drone modes -----------------------------------------
            //
            // The file's `modes` block: which modes this gadget joins and its
            // own shape numbers. Null = the [Drone modes] defaults, joining
            // every mode a wingman can do. READ ONLY at runtime - a mode is an
            // override the controller consults, never a write to this Config
            // (concept 5.1), because every ship carrying the file shares it.
            [System.NonSerialized] public ForgeDroneModeRules.Shape modes;

            // Resolved at build time from `visual`; null means "draw nothing"
            // and the body is an empty GameObject, which is a legitimate
            // (if invisible) configuration rather than an error.
            [System.NonSerialized] public GameObject visualPrefab;
            [System.NonSerialized] public Sprite[] visualFrames;
            [System.NonSerialized] public float visualFps;
            [System.NonSerialized] public bool visualAnimates;

            // The file this came from, for runtime log lines that have to name
            // the weapon rather than the gadget.
            [System.NonSerialized] public string fileName;

            // The gadget's own JSON, kept because the feature chain is re-run
            // over a fresh clone every time you change weapons. This is the one
            // place in the mod that holds JSON past build time, and it is the
            // reason it can: the clone does not exist yet at build time.
            [System.NonSerialized] public Newtonsoft.Json.Linq.JObject root;

            public bool UsesRing
            {
                get
                {
                    return motion == Motion.Orbit ||
                           motion == Motion.Drag ||
                           motion == Motion.Lead;
                }
            }

            public bool UsesBearing
            {
                get
                {
                    return motion == Motion.Drag || motion == Motion.Lead;
                }
            }

            // Key modes where the player never spawns a companion by hand, so
            // the complement has to be put out for them or the gadget does
            // nothing at all. `Toggle` is NOT here: its first press is the
            // deploy, which is what makes it a toggle rather than a switch that
            // starts on.
            public bool AutoDeploys
            {
                get
                {
                    return key == Key.None ||
                           key == Key.Burst ||
                           key == Key.Recall;
                }
            }
        }

        private static readonly Dictionary<WeaponData, Config> _weapons =
            new Dictionary<WeaponData, Config>();

        public static void Register(WeaponData weapon, Config cfg)
        {
            if (weapon == null || cfg == null)
                return;

            _weapons[weapon] = cfg;

            if (cfg.IsDrone)
                AnyDrone = true;
        }

        public static bool TryGet(WeaponData weapon, out Config cfg)
        {
            cfg = null;

            return weapon != null && _weapons.TryGetValue(weapon, out cfg);
        }

        public static bool Any { get { return _weapons.Count > 0; } }

        // Does ANY file in the folder field damageable bodies?
        //
        // Latched at registration rather than computed, because it is read from
        // a per-frame gate on `ModuleGridOwner.Update` - which runs for every
        // unit in the world that has a grid, enemies included. A dictionary
        // walk there would be the whole cost of the feature.
        //
        // This is build-time configuration, so it is NOT reset at run entry -
        // same category as `_weapons` itself, and clearing it would stop a
        // drone gadget finding its companions again after a load.
        public static bool AnyDrone { get; private set; }

        // Build-time registrations keyed by a weapon ASSET, so this is the
        // category `ForgeCrit.Reset` warns about: clearing it would unbuild
        // every wingman gadget for the rest of the session. It is deliberately
        // NOT in the run-entry reset list.
    }
}
