using System;
using System.Collections.Generic;
using System.Text;
using BepInEx.Logging;
using UnityEngine;

namespace WeaponForge
{
    // Runs one wingman gadget: the bodies, where they sit, where they point,
    // and firing a copy of your shot when you fire.
    //
    // One controller per equipped wingman gadget, living on the ship.
    //
    // THE PIECE THAT DECIDES THE ARCHITECTURE: each wingman gets its **own**
    // `WeaponBase`, built from one shared cloned `WeaponData`. That is not
    // tidiness - `HitscanWeapon.visualsInstances` is an INSTANCE field, so N
    // wingmen sharing one WeaponBase would fight over one set of beam quads and
    // you would see a single beam snapping between them. One WeaponBase each
    // gives each its own, and it makes per-wingman state (spin-up, overheat)
    // independent for free.
    //
    // AND BEAMS NEED A HAND EVERY FRAME. `HitscanWeaponVisual` is driven
    // entirely from `HitscanWeapon.OnBarrelMoved`, which only `Shooter.Update`
    // calls - and a gadget never goes through `Shooter`. So `LateUpdate` calls
    // it per wingman, at that wingman's position and heading. Same class of
    // by-hand work as `ForgeShooterQuiet`, and for the same reason.
    public class ForgeWingmanController : MonoBehaviour
    {
        private static readonly ManualLogSource Log =
            ForgeLog.Source("WeaponForge.Wingman");

        private class Wing
        {
            public GameObject go;

            // Null on a DRONE body, and deliberately so. `ForgeWingmanArt
            // .Dress` skips both of its colour paths when there is no renderer
            // to write to, which is exactly the behaviour a unit needs: a
            // per-frame `sr.color` write is the read-modify-write trap that
            // makes the FIRST tint permanent once a second feature touches the
            // same unit. A drone body's colour goes through `ForgeMinionLook`
            // once and `ForgeUnitTint` for the pulse - see ForgeWingmanBody.
            public SpriteRenderer sr;

            public Vector3 baseScale = Vector3.one;

            // ---- drone bodies only -----------------------------------
            //
            // The live unit, and the component DATA behind it. The data is what
            // the owner's minion set holds, so it is the identity a stray body
            // is recognised by after a load.
            public Unit body;
            public Unit.Data data;

            // Where the ring wants this companion to be. A kinematic body is
            // written straight there; a physical one is pulled toward it by a
            // spring in FixedUpdate, so a blast can knock it off and it comes
            // back.
            public Vector2 target;

            // Last frame's position, so a KINEMATIC body can be handed a
            // velocity. It has none of its own - `Rigidbody2D.linearVelocity`
            // is zero on a body moved by its transform - and the contact sweep
            // reads exactly that field.
            public Vector2 lastPos;
            public bool hasLast;

            public ForgeContactController contact;

            // ONE WeaponBase PER SOURCE WEAPON, not one per wing. Under
            // `mirror: "both"` a single wingman has to be able to fire a copy
            // of your primary AND a copy of your secondary, and rebuilding the
            // instance every time you switched trigger would re-run
            // InitializeVisuals - which for a beam allocates its quads.
            public readonly Dictionary<WeaponData, WeaponBase> weapons =
                new Dictionary<WeaponData, WeaponBase>();

            // What it last fired, so LateUpdate knows which beam to drive.
            public WeaponBase lastFired;

            public float angle;        // current ring position, degrees
            public float bornAt;
            public Vector2 facing = Vector2.right;
            public int slot;           // index, for fan offsets

            // True for the frame a wing is spawned in, so it appears AT its
            // place on the ring rather than easing in from wherever index 0
            // happens to be.
            public bool fresh = true;

            // ---- R19 test 259 instrument -----------------------------
            // The body's health as last seen, and whether it came from a
            // load. Read at the moment a body vanishes, so the log can say
            // what it looked like just before - the one datum that tells a
            // killed companion from one the mod or the game removed.
            public string lastHealth = "?";
            public bool adopted;

            // A drone body's gun: its `BarrelTransform` (the `Head`),
            // looked up once per body. See TickDress.
            public BarrelTransform head;
            public bool headLooked;
        }

        private readonly List<Wing> _wings = new List<Wing>();

        private ForgeWingman.Config _cfg;
        private Unit _unit;
        private IModuleCluster _cluster;

        // ★ WHICH GADGET SLOT THIS IS, and it is half the controller's identity.
        // Until R19 a controller was found by CONFIG alone, so two copies of one
        // wingman file in two gadget slots were ONE controller: one cap, one
        // ring, and `_cluster` flipping to whichever slot last asked. Test 267:
        // "Another copy of the same wingman drone module share the drone limit
        // ... Each weapon in their own gadget slot should have their own drone
        // limit." Two different files never showed it - two configs, two
        // controllers - which is exactly why it survived. His call: fully per
        // slot, the save record included.
        private ClusterType _slot;
        private bool _slotKnown;

        // Set when the controller is torn down, because `Destroy` is deferred
        // to the end of the frame and a question asked before then ("is any
        // copy of this gadget still installed?") must not count the corpse.
        private bool _gone;
        private ShipMovement _movement;
        private Rigidbody2D _body;
        private Aimer _aimer;

        private bool _stowed;
        private float _shotCredit;      // fractional fireRatio accumulator
        private float _lastBearing;     // remembered while at rest
        private bool _hasBearing;
        private float _restingSince = -1f;
        private float _ringPhase;       // orbit: where the WHOLE ring is

        // Which grid generation the weapon instances were built against. See
        // ForgeWingmanPatch.Generation.
        private int _builtAt = -1;

        // The weapon whose shot is being mirrored right now. Only meaningful
        // under `mirror: "both"`, where "the weapon to copy" is a property of
        // the trigger you pulled rather than of the config.
        private WeaponBase _firing;

        // Set when a deploy press was refused at the cap, consumed by the HUD
        // count widget to play the game's own "maximum reached" flash.
        private bool _refused;

        // Trail mode: where the ship has been.
        private readonly List<Vector2> _path = new List<Vector2>();
        private const float PathStep = 0.15f;   // world units between samples

        private static bool _saidNoWeapon;
        private static bool _saidBodyDied;

        // Every live controller, so the uninstall hook can find the one it has
        // to tear down and the HUD can find the one it has to count.
        //
        // `OnUninstalled` is handed a `Unit.Data`, not a `Unit`, and there is
        // no route from one to the live GameObject - which is why R16 found
        // that pulling the gadget out of the grid left the companions flying
        // and shooting: nothing could reach them to stop them.
        private static readonly List<ForgeWingmanController> _live =
            new List<ForgeWingmanController>();

        // ------------------------------------------------------------------
        // Wiring
        // ------------------------------------------------------------------

        public ForgeWingman.Config Config { get { return _cfg; } }
        public int Deployed { get { return _wings.Count; } }
        public int Capacity { get { return Cap; } }

        // Stowed by the toggle or recall key. Saved alongside the count,
        // because without it a `recall`-keyed gadget you deliberately put away
        // would be waiting for you on the next load: the whole complement is
        // deployed for that mode, and a fresh controller is not stowed.
        public bool Stowed { get { return _stowed; } }

        // The owner entity's save id, so a coop save cannot hand one player's
        // companions to the other. See ForgeWingmanSave.
        public int OwnerId { get { return ForgeWingmanSave.IdOf(_unit); } }

        // The gadget slot, as the save record spells it ("Active2"), or "" when
        // it has not been worked out yet. Stable across a save and a load: the
        // game restores the grid at the same positions.
        public string SlotKey { get { return _slotKnown ? _slot.ToString() : ""; } }

        // Is this the controller for the gadget in `type`'s slot?
        public bool InSlot(Unit.OwnerConnectionType type)
        {
            return Connection() == type;
        }

        public static ForgeWingmanController For(
            Unit unit, ForgeWingman.Config cfg, IModuleCluster cluster)
        {
            if (unit == null || cfg == null)
                return null;

            bool known = cluster != null;
            ClusterType slot = known ? cluster.ClusterType : default(ClusterType);

            foreach (var c in unit.GetComponentsInChildren<ForgeWingmanController>(true))
            {
                if (c._cfg != cfg || c._gone)
                    continue;

                // ONE CONTROLLER PER GADGET SLOT. A controller whose slot is not
                // known yet is claimed by the first slot that asks for it - that
                // is the key press arriving before the grid was walked.
                if (known && c._slotKnown && c._slot != slot)
                    continue;

                if (known)
                {
                    c._cluster = cluster;
                    c._slot = slot;
                    c._slotKnown = true;
                }

                return c;
            }

            var made = unit.gameObject.AddComponent<ForgeWingmanController>();
            made._cfg = cfg;
            made._unit = unit;
            made._cluster = cluster;
            made._slot = slot;
            made._slotKnown = known;
            made._movement = unit.GetComponentInChildren<ShipMovement>(true);
            made._body = unit.GetComponentInChildren<Rigidbody2D>(true);
            made._aimer = unit.GetComponentInChildren<Aimer>(true);

            _live.Add(made);

            return made;
        }

        // Find every controller running this config, wherever it lives - every
        // SLOT of it. Used where the answer really is "all copies": the check
        // before a shared weapon clone is released.
        public static void Each(
            ForgeWingman.Config cfg, System.Action<ForgeWingmanController> act)
        {
            for (int i = _live.Count - 1; i >= 0; i--)
            {
                ForgeWingmanController c = _live[i];

                if (c == null)
                {
                    _live.RemoveAt(i);
                    continue;
                }

                if (c._cfg == cfg && !c._gone)
                    act(c);
            }
        }

        // The controller(s) for ONE gadget slot. The HUD readout asks this: each
        // slot's "2/3" is that gadget's, not the sum of every copy of the file.
        public static void EachInSlot(
            ForgeWingman.Config cfg, Unit.OwnerConnectionType type,
            System.Action<ForgeWingmanController> act)
        {
            Each(cfg, c =>
            {
                if (c.InSlot(type))
                    act(c);
            });
        }

        // Is any copy of this gadget still installed anywhere? The weapon
        // clones are cached per CONFIG and shared by every slot running it, so
        // they may only be released when the last copy goes.
        public static bool AnyFor(ForgeWingman.Config cfg)
        {
            bool any = false;

            Each(cfg, c => any = true);

            return any;
        }

        // Is `hit` a DRONE BODY belonging to `owner`?
        //
        // Asked from the physics-shot friendly skip, which runs on a collision
        // callback - so it has to be cheap and it has to be exact. Both come
        // from the same fact: a controller holds its own wings, and there are a
        // handful of controllers and a handful of wings. No search, no cache,
        // nothing to go stale.
        //
        // Scoped to the OWNER deliberately. In coop this must not let one
        // player's rockets fly through the other player's companions.
        public static bool OwnsBody(Unit owner, Unit hit)
        {
            if (owner == null || hit == null)
                return false;

            for (int i = _live.Count - 1; i >= 0; i--)
            {
                ForgeWingmanController c = _live[i];

                if (c == null)
                {
                    _live.RemoveAt(i);
                    continue;
                }

                if (!ReferenceEquals(c._unit, owner) || c._cfg == null ||
                    !c._cfg.IsDrone)
                {
                    continue;
                }

                for (int w = 0; w < c._wings.Count; w++)
                    if (ReferenceEquals(c._wings[w].body, hit))
                        return true;
            }

            return false;
        }

        // ---- drone modes ----------------------------------------------------
        //
        // The controllers of one ship's gadget slot, for a formation's roster
        // (ForgeDroneModes): every companion of a ship in the same mode is one
        // formation, wingmen and AI drones together.
        public static void ForSlot(
            Unit unit, int slot, System.Action<ForgeWingmanController> act)
        {
            if (unit == null)
                return;

            for (int i = 0; i < _live.Count; i++)
            {
                ForgeWingmanController c = _live[i];

                if (c == null || c._gone || !ReferenceEquals(c._unit, unit) || c._cfg == null)
                    continue;

                if (ForgeDroneModes.SlotOf(c.Connection()) == slot)
                    act(c);
            }
        }

        public int WingCount { get { return _wings.Count; } }

        public bool WingHasBody(int i)
        {
            return i >= 0 && i < _wings.Count && _wings[i].body != null;
        }

        public Vector2 WingPosition(int i)
        {
            if (i < 0 || i >= _wings.Count || _wings[i].go == null)
                return transform.position;

            return _wings[i].go.transform.position;
        }

        // Every live controller, whatever it is running. Used at save time,
        // which has to record all of them and cannot ask per config.
        public static void All(System.Action<ForgeWingmanController> act)
        {
            for (int i = _live.Count - 1; i >= 0; i--)
            {
                ForgeWingmanController c = _live[i];

                if (c == null)
                {
                    _live.RemoveAt(i);
                    continue;
                }

                act(c);
            }
        }

        // Put back what a save said was out. The bodies are NOT restored - they
        // are built fresh from the config, which is the whole reason only a
        // count is stored. See ForgeWingmanSave.
        //
        // Clamped to the CAP as it is right now rather than as it was: load a
        // save made with a BoosterCore beside the gadget, then pull the core
        // out before loading, and the saved five must come back as the three
        // you can now field.
        public void Restore(int count, bool stowed)
        {
            _stowed = stowed;

            if (stowed)
            {
                RecallAll();
                return;
            }

            // ★ A DRONE-BODIED GADGET RESTORES ITSELF, AND THE COUNT WOULD
            // DOUBLE IT. A real drone is a `SavableEntity`, and
            // `Unit.Data.Memento` stores `ownerId` + `connectionToOwner` and
            // replays them through `SetOwner` on load - so the GAME brings the
            // bodies back, and `TickAdopt` picks them up. Spawning from a saved
            // count as well would give you eight where you saved four.
            //
            // The sidecar still owns the STOWED flag for these, because that is
            // a fact about the gadget rather than about the bodies, and nothing
            // in the save records it.
            if (_cfg.IsDrone)
                return;

            int want = Mathf.Min(Mathf.Max(0, count), Cap);
            int guard = 0;

            while (_wings.Count < want && guard++ < 64)
                if (!Spawn())
                    break;
        }

        // Take the companions away and stop running. The gadget is gone - ONE
        // gadget, the one in the slot that `module` just left.
        //
        // It used to tear down every controller running the config, which was
        // right while there could only be one. With two copies in two slots,
        // pulling one would have recalled the other's companions too.
        //
        // Matched two ways, because the uninstall hook may not know the slot:
        // by the cluster the game said the module was in, then by the module
        // still being the main module of the controller's cluster. If NEITHER
        // matches anything, every copy goes - the old behaviour, and the safe
        // side of the choice, since a companion left flying with no gadget is
        // the R16 bug this hook exists to prevent.
        public static void Remove(
            ForgeWingman.Config cfg, object module, IModuleCluster cluster)
        {
            var hit = new List<ForgeWingmanController>();

            Each(cfg, c =>
            {
                bool bySlot = cluster != null && c._slotKnown &&
                              c._slot == cluster.ClusterType;

                bool byModule = module != null && c._cluster != null &&
                                c._cluster.HasMainModule &&
                                ReferenceEquals(c._cluster.MainModule, module);

                if (bySlot || byModule)
                    hit.Add(c);
            });

            if (hit.Count == 0)
                Each(cfg, c => hit.Add(c));

            for (int i = 0; i < hit.Count; i++)
            {
                hit[i]._gone = true;
                hit[i].RecallAll();
                Destroy(hit[i]);
            }
        }

        // True once, then false: "a deploy press was refused because you are at
        // the cap". The HUD turns it into the flash the game plays when a drone
        // gadget is full.
        public bool ConsumeRefusal()
        {
            bool was = _refused;
            _refused = false;

            return was;
        }

        // The cap, read LIVE so a BoosterCore added mid-run raises it without a
        // restart - the same reason the orbit ring reads its orb count live.
        //
        // R16 test 207 failed here, and the cause was one layer down: the level
        // never MOVED. `ModuleGrid` applies a BoosterCore's bonus only to a
        // module whose `ModuleData.canBeBoosted` is set, and every stock weapon
        // module in the game ships it OFF - so a wingman file built on a weapon
        // template sat at level 1 no matter how many cores surrounded it, while
        // this property faithfully read that 1. Fixed in ApplyBoostability.
        private int Level
        {
            get
            {
                if (_cluster != null && _cluster.HasMainModule &&
                    _cluster.MainModule != null)
                {
                    return Mathf.Max(1, _cluster.MainModule.Level);
                }

                return 1;
            }
        }

        private int Cap
        {
            get
            {
                // `count` is the author's cap at level 1; each level above that
                // adds `capPerLevel`.
                return Mathf.Max(
                    0, _cfg.count + (Level - 1) * Mathf.Max(0, _cfg.capPerLevel));
            }
        }

        // WHICH SLOT THIS GADGET IS IN, as the minion system spells it.
        //
        // Drone bodies are registered under it, which is what lets a body be
        // told apart from a stock drone gadget's minion in another Active slot
        // - one module per slot, so the answer is unambiguous. `Undefined` is
        // what a spawner WEAPON's minions fall back to, so it is never adopted:
        // claiming those would be exactly the indiscriminate grab that
        // ForgeMinionTrimPatch exists to remove from the game's own code.
        private Unit.OwnerConnectionType Connection()
        {
            if (_cluster != null)
                return ForgeDronePatch.Connection(_cluster.ClusterType);

            if (_slotKnown)
                return ForgeDronePatch.Connection(_slot);

            // No cluster yet. Walk the grid for the cluster whose main module
            // is running THIS config, rather than guessing - a guess here would
            // hand us somebody else's minions. And skip a slot another copy of
            // the same file has already claimed: with two copies installed,
            // "the first cluster running this config" is the OTHER gadget half
            // the time, and adopting its bodies is the 267 bug again.
            try
            {
                var owner = _unit.GetComponentInChildren<ModuleGridOwner>(true);

                if (owner == null || owner.ModuleGrid == null)
                    return Unit.OwnerConnectionType.Undefined;

                foreach (IModuleCluster c in owner.ModuleGrid.GetAllClusters())
                {
                    if (c == null || !c.HasMainModule)
                        continue;

                    var gadget = c.MainModule as WeaponBasedActiveModule;

                    if (gadget == null)
                        continue;

                    ForgeWingman.Config found;

                    if (!ForgeWingman.TryGet(gadget.WeaponData, out found) ||
                        found != _cfg || ClaimedByAnother(c.ClusterType))
                    {
                        continue;
                    }

                    return ForgeDronePatch.Connection(c.ClusterType);
                }
            }
            catch (Exception)
            {
            }

            return Unit.OwnerConnectionType.Undefined;
        }

        private bool ClaimedByAnother(ClusterType slot)
        {
            for (int i = 0; i < _live.Count; i++)
            {
                ForgeWingmanController c = _live[i];

                if (c == null || c == this || c._gone || c._cfg != _cfg ||
                    !ReferenceEquals(c._unit, _unit))
                {
                    continue;
                }

                if (c._slotKnown && c._slot == slot)
                    return true;
            }

            return false;
        }

        // The gadget module itself, for the respawn charge. Same object the
        // key press is charged against, so a respawn cannot be priced
        // differently from a deploy by accident.
        private ActiveModule Gadget
        {
            get
            {
                return (_cluster != null && _cluster.HasMainModule)
                    ? _cluster.MainModule as ActiveModule
                    : null;
            }
        }

        // ------------------------------------------------------------------
        // The gadget key
        // ------------------------------------------------------------------

        // Returns true if the press was spent (so the caller should charge for
        // it). A refused press costs nothing.
        //
        // R16: `"key": "burst"` deployed nothing, ever. The reason is visible
        // in the old shape of this switch - Burst called `FireAll` over an
        // empty list and returned true, Recall returned false on an empty list,
        // and Toggle's first press set `_toggledOff = true` and recalled a
        // complement that had never been spawned. Only `deploy` and `none` put
        // a body on screen. THREE OF FIVE KEY MODES WERE UNREACHABLE, and each
        // read in isolation as a sensible handler for a state that could not be
        // arrived at.
        public bool OnKey()
        {
            // ★ ADOPT BEFORE YOU COUNT. Every branch below asks
            // `_wings.Count`, and after a load that number is a lie until
            // TickAdopt has run: the game has already handed the bodies back
            // and this controller has not noticed yet. See Settle.
            Settle();

            switch (_cfg.key)
            {
                case ForgeWingman.Key.None:
                    return false;

                case ForgeWingman.Key.Toggle:
                    if (_wings.Count > 0)
                    {
                        _stowed = true;
                        RecallAll();
                    }
                    else
                    {
                        _stowed = false;
                        DeployAll();
                    }

                    return true;

                case ForgeWingman.Key.Burst:
                    // They are deployed for you (see AutoDeploys), but a press
                    // that arrives before Ensure has run - or after a decay
                    // timer emptied the ring - should still put them out rather
                    // than fire nothing.
                    if (_wings.Count == 0)
                    {
                        _stowed = false;
                        DeployAll();
                        return true;
                    }

                    FireAll();
                    return true;

                case ForgeWingman.Key.Recall:
                    // Stow them, and send them back on the next press - or the
                    // key would be a one-way switch that permanently disables
                    // the gadget.
                    if (_wings.Count == 0)
                    {
                        _stowed = false;
                        DeployAll();
                    }
                    else
                    {
                        _stowed = true;
                        RecallAll();
                    }

                    return true;

                default:
                    return Deploy();
            }
        }

        // Nobody is going to press anything for these modes, so put the whole
        // complement out. Separate from Deploy so it cannot be mistaken for a
        // key press and charged for.
        public void OnKeyless()
        {
            if (_stowed)
                return;

            // ★★ THE AUTO-DEPLOY USED TO DESTROY THE SAVE IT WAS RESTORING,
            // and it did it silently, which is the expensive half.
            //
            // `ForgeWingmanPatch.Ensure` creates the controller and then, for
            // the three key modes nobody presses (`none` / `burst` /
            // `recall`), calls this on the very same line - so `_wings` is
            // empty, `DeployAll` spawns a brand-new complement, and only
            // afterwards does `TickAdopt` get a frame. By then the ring is at
            // the cap, so every body the game had restored fails the
            // `_wings.Count >= Cap` test and is TAKEN.
            //
            // The count came out right, the ring came out right, and your
            // saved companions - the ones that came back on the health you
            // left them with, which is the entire reason a drone body
            // persists through the game's save system rather than through an
            // orb count - had been deleted and replaced with fresh ones.
            //
            // > **A number is only a fact once whoever owns it has been
            // > asked.** Same family as the refill that would have hidden
            // > this feature's point, one layer further out: there the
            // > restored health was overwritten, here the body carrying it is
            // > thrown away instead.
            //
            // Both shipped R19 files use `key: "deploy"`, which does not
            // auto-deploy - so the round as written could not have caught it.
            // Test 268 exists for exactly this path.
            Settle();

            // ★ AND ADOPTING FIRST WAS NOT ENOUGH - R19 268 FAILED ON THE BUILD
            // THAT DID IT: "there are duplicated drones orbiting the hurt drones.
            // But they have their base AI where they auto shoot enemies." The
            // Settle above can only adopt a body that is already IN THE SCENE,
            // and straight after a load the game has put the companions back in
            // your ship's minion list before their GameObjects exist. So this
            // WAITS while any of this slot's restored companions is still not on
            // screen, for a few seconds at most, rather than spawning a fresh
            // ring over them.
            if (WaitingForRestored())
                return;

            DeployAll();
        }

        // ---- the load window (R19 268 / 279) -----------------------------

        private bool _saidWaiting;
        private bool _saidCensus;

        // Companions the save handed back to THIS slot that are not on screen
        // yet: in the ship's minion list, not held by a wing, not resolvable
        // to a live body.
        private int Unresolved(StringBuilder ids)
        {
            if (!_cfg.IsDrone || _unit == null || _unit.ComponentData == null)
                return 0;

            var minions = _unit.ComponentData.Minions;

            if (minions == null || minions.Count == 0)
                return 0;

            Unit.OwnerConnectionType want = Connection();

            if (want == Unit.OwnerConnectionType.Undefined)
                return 0;

            int n = 0;

            foreach (Unit.Data d in minions)
            {
                if (d == null || d.ConnectionToOwner != want || Held(d))
                    continue;

                if (ForgeWingmanBody.Resolve(d) != null)
                    continue;

                n++;

                if (ids != null && d.entity != null)
                {
                    if (ids.Length > 0)
                        ids.Append(", ");

                    ids.Append(d.entity.instanceId);
                }
            }

            return n;
        }

        private bool WaitingForRestored()
        {
            if (!_cfg.IsDrone || !ForgeWingmanPatch.LoadSettling)
                return false;

            var ids = new StringBuilder();
            int n = Unresolved(ids);

            if (n == 0)
                return false;

            if (!_saidWaiting)
            {
                _saidWaiting = true;

                Trace("the save handed back " + n + " companion(s) for this " +
                      "slot that are not in the scene yet (entity " + ids +
                      ") - the auto-deploy is WAITING for them rather than " +
                      "spawning a fresh ring over them");
            }

            return true;
        }

        // Once per controller, the first frame after the load window closes:
        // what this slot ended up holding. The question 268 and 279 could not
        // answer is where a companion went, and this is the snapshot that
        // tells "never came back" from "came back and was lost afterwards".
        private void CensusAfterLoad()
        {
            if (_saidCensus || !_cfg.IsDrone ||
                !ForgeWingmanPatch.LoadedThisRun || ForgeWingmanPatch.LoadSettling)
            {
                return;
            }

            _saidCensus = true;

            var ids = new StringBuilder();
            int unresolved = Unresolved(ids);
            int adopted = 0;

            for (int i = 0; i < _wings.Count; i++)
                if (_wings[i].adopted)
                    adopted++;

            Trace("after the load this slot has " + _wings.Count + " companion(s) " +
                  "out (" + adopted + " handed back by the save, " +
                  (_wings.Count - adopted) + " new)" +
                  (unresolved > 0
                      ? " - and " + unresolved + " more in your ship's minion list " +
                        "that NEVER appeared in the scene (entity " + ids + "). " +
                        "If you can see extra drones flying on their own, those " +
                        "are them"
                      : ""));
        }

        // ---- WHO destroyed a companion's saved data ----------------------
        //
        // Every "lost" line so far could say only THAT a body's data was gone.
        // `EntityManager.EntityDestroyed` is a public event raised for every
        // entity whose data is destroyed, so subscribing to it and taking the
        // call stack at that moment names the code that did it - the game's
        // streaming, a trim, a death, or us. One run of 279 then answers the
        // question three rounds of guessing did not.
        private static EntityManager _watched;

        private static void WatchEntities()
        {
            try
            {
                EntityManager em;

                if (!ServiceLocator.TryGet<EntityManager>(out em) || em == null ||
                    ReferenceEquals(em, _watched))
                {
                    return;
                }

                if (_watched != null)
                    _watched.EntityDestroyed -= OnEntityDestroyed;

                _watched = em;
                em.EntityDestroyed += OnEntityDestroyed;
            }
            catch (Exception)
            {
                // An instrument must never cost the thing it describes.
            }
        }

        private static void OnEntityDestroyed(EntityData e)
        {
            try
            {
                Unit.Data d;

                if (e == null || !e.TryGetComponent<Unit.Data>(out d) || d == null)
                    return;

                for (int i = 0; i < _live.Count; i++)
                {
                    ForgeWingmanController c = _live[i];

                    if (c == null || c._gone || c._cfg == null || !c._cfg.IsDrone)
                        continue;

                    bool ours = c.Held(d) ||
                        (c._unit != null && c._unit.ComponentData != null &&
                         c._unit.ComponentData.entity != null &&
                         d.Owner == c._unit.ComponentData.entity &&
                         d.ConnectionToOwner == c.Connection());

                    if (!ours)
                        continue;

                    c.Trace("the SAVED DATA of a companion (entity " +
                            e.instanceId + ") was DESTROYED - by " + Culprit());
                    return;
                }
            }
            catch (Exception)
            {
            }
        }

        // The call stack at the moment of destruction, minus the frames that
        // are only plumbing (this instrument, the event, reflection, Unity's
        // own internals), newest first.
        private static string Culprit()
        {
            try
            {
                var frames = new System.Diagnostics.StackTrace(2, false).GetFrames();
                var parts = new List<string>();

                if (frames != null)
                {
                    for (int i = 0; i < frames.Length && parts.Count < 7; i++)
                    {
                        var m = frames[i].GetMethod();

                        if (m == null || m.DeclaringType == null)
                            continue;

                        string type = m.DeclaringType.FullName ?? m.DeclaringType.Name;

                        if (type.StartsWith("System.") || type.StartsWith("UnityEngine.") ||
                            type.StartsWith("HarmonyLib") ||
                            type.Contains("ForgeWingmanController"))
                        {
                            continue;
                        }

                        parts.Add(m.DeclaringType.Name + "." + m.Name);
                    }
                }

                return parts.Count > 0
                    ? string.Join(" <- ", parts.ToArray())
                    : "(no stack available)";
            }
            catch (Exception)
            {
                return "(no stack available)";
            }
        }

        // Pick up anything the game has handed back before answering a
        // question about how many companions are out.
        //
        // Cheap by construction: `TickAdopt` returns on its first line for an
        // orb gadget, and on a count comparison for a drone one whose ring is
        // already accounted for. So this is a branch on every key press
        // outside the one frame after a load where it is the whole ballgame.
        private void Settle()
        {
            if (!_cfg.IsDrone)
                return;

            try { TickAdopt(); }
            catch (Exception e)
            {
                Log.LogError(
                    "wingman: adopting restored companions before a key " +
                    "press failed: " + e);
            }
        }

        private void DeployAll()
        {
            int cap = Cap;
            int guard = 0;

            while (_wings.Count < cap && guard++ < 64)
                if (!Spawn())
                    break;
        }

        private bool Deploy()
        {
            _stowed = false;

            if (_wings.Count < Cap)
            {
                // A refused spawn costs nothing: the press is not consumed,
                // so the resource is not taken either. That matters for a
                // drone body, whose spawn can genuinely fail for a frame or two
                // while the entity manager is coming up.
                return Spawn();
            }

            switch (_cfg.atCap)
            {
                case ForgeWingman.AtCap.RecallAll:
                    RecallAll();
                    return true;

                case ForgeWingman.AtCap.Refuse:
                    _refused = true;
                    return false;

                default:
                    RecallOne();
                    return true;
            }
        }

        private void RecallOne()
        {
            if (_wings.Count == 0)
                return;

            Kill(_wings[_wings.Count - 1]);
            _wings.RemoveAt(_wings.Count - 1);
            Reindex();
        }

        public void RecallAll()
        {
            for (int i = 0; i < _wings.Count; i++)
                Kill(_wings[i]);

            _wings.Clear();
        }

        private void Reindex()
        {
            for (int i = 0; i < _wings.Count; i++)
                _wings[i].slot = i;
        }

        // ------------------------------------------------------------------
        // Bodies
        // ------------------------------------------------------------------

        // Returns false when nothing could be put on screen, so a key press
        // that produced no companion costs nothing. An orb always succeeds; a
        // drone body can be refused by the entity manager not being ready yet,
        // which is a real state during a load.
        private bool Spawn()
        {
            var w = new Wing
            {
                bornAt = Time.time,
                slot = _wings.Count,
            };

            if (_cfg.IsDrone)
            {
                Unit.OwnerConnectionType slot = Connection();

                // ★ NO SLOT, NO BODY - and refusing is the kinder failure.
                //
                // The slot is what a body is recognised BY after a load: the
                // game restores it as a minion of this ship, and the only thing
                // that says whose it is is the connection type. Spawning under
                // `Undefined` would work perfectly this session and leave an
                // ORPHAN in the save - a real unit that comes back with its AI
                // switched on, owned by nothing, that no controller will ever
                // claim or clean up. A refused key press costs a press.
                if (slot == Unit.OwnerConnectionType.Undefined)
                {
                    if (!_saidNoSlot)
                    {
                        _saidNoSlot = true;

                        Log.LogWarning(
                            (_cfg.fileName ?? "") + ": wingman - could not work " +
                            "out which gadget slot this is in, so a drone body " +
                            "was NOT spawned. A body is recognised by its slot " +
                            "after a load; one spawned without it would come " +
                            "back owned by nothing and never be cleaned up. " +
                            "The press cost you nothing. Please report this - " +
                            "it should not be reachable.");
                    }

                    return false;
                }

                Unit made = ForgeWingmanBody.Spawn(
                    _cfg, _unit, transform.position, slot, Level, _cluster);

                if (made == null)
                    return false;

                Take(w, made);
            }
            else
            {
                w.go = ForgeWingmanArt.Build(_cfg, transform.position);

                if (w.go != null)
                {
                    w.sr = w.go.GetComponentInChildren<SpriteRenderer>(true);
                    w.baseScale = w.go.transform.localScale;
                }
            }

            _wings.Add(w);

            // The ring is spaced by how many are OUT, not by the cap, so every
            // existing companion's place changes the moment this one arrives.
            // Placing it now (rather than next frame) is what stops a new orb
            // appearing on top of an old one - R16, twice: *"if i spam it, the
            // orbs dont manage to spread out evenly"* and *"the 3rd orb ...
            // would sit in the same position as the second one."*
            if (_wings.Count > 0)
                TickMotion();

            return true;
        }

        // Adopt a live drone body into a wing - the one place a Wing learns
        // what its body IS, so a fresh spawn and a body handed back by a loaded
        // save go through identical code.
        private void Take(Wing w, Unit made)
        {
            w.body = made;
            w.data = made.ComponentData;
            w.go = made.gameObject;
            w.sr = null;            // see the field's own note
            w.baseScale = made.transform.localScale;
            w.lastPos = made.transform.position;
            w.hasLast = true;
            w.contact = made.GetComponent<ForgeContactController>();

            if (!_cfg.drone.solidToEachOther)
            {
                for (int i = 0; i < _wings.Count; i++)
                {
                    if (_wings[i].body != null && _wings[i].body != made)
                        ForgeWingmanBody.Separate(made, _wings[i].body, true);
                }
            }
        }

        private void Kill(Wing w)
        {
            if (w == null)
                return;

            foreach (var kv in w.weapons)
            {
                if (kv.Value == null)
                    continue;

                try { kv.Value.Dispose(); } catch { }
            }

            w.weapons.Clear();
            w.lastFired = null;

            // A DRONE BODY IS TAKEN, NOT KILLED. `ForgeWingmanBody.Take` uses
            // Destroy rather than `HealthBase.Die()` on purpose, and that is
            // the opposite of what `ForgeMinionLife` and the game's own cap
            // trim do: a recall is not a death, and `Die()` would play the
            // death particle, fire `onDeath`, credit whatever weapon was last
            // stamped, and - with `deathBlast` on - detonate a companion you
            // simply put away, on your own position.
            if (w.body != null)
            {
                ForgeWingmanBody.Take(w.body);
                w.body = null;
                w.data = null;
                w.go = null;
                return;
            }

            if (w.go != null)
                Destroy(w.go);
        }

        // Drop every built WeaponBase without touching the bodies, so the next
        // shot rebuilds them against the grid as it is NOW.
        private void Rearm()
        {
            for (int i = 0; i < _wings.Count; i++)
            {
                Wing w = _wings[i];

                foreach (var kv in w.weapons)
                {
                    if (kv.Value == null)
                        continue;

                    try { kv.Value.Dispose(); } catch { }
                }

                w.weapons.Clear();
                w.lastFired = null;
            }
        }

        private void OnDestroy()
        {
            UnblockAll();
            RecallAll();
            _live.Remove(this);
        }

        // ------------------------------------------------------------------
        // The weapon each wingman fires
        // ------------------------------------------------------------------

        // The weapon whose shot the copies are copies OF, right now.
        //
        // `mirror: "both"` is the case that matters and the one R16 caught:
        // *"when firing the right click (offhand) the wingman fires the
        // leftclick (Mainhand) weapon for some reason."* It did, because this
        // returned `primary ?? secondary` - a choice made from the CONFIG, with
        // nothing to say which trigger had actually been pulled. Under `both`
        // the answer is a property of the shot, so the shot has to carry it:
        // `_firing` is set by OnMirroredShot from the WeaponBase that DoShoot
        // was called on.
        private WeaponBase SourceWeapon()
        {
            var ship = _unit != null
                ? _unit.GetComponentInChildren<Ship>(true) : null;

            if (ship == null)
                return null;

            switch (_cfg.mirror)
            {
                case ForgeWingman.Mirror.Secondary:
                    return ship.SecondaryWeapon;

                case ForgeWingman.Mirror.Both:
                    // The trigger that is firing wins; the fallback is only for
                    // the paths with no shot behind them (the burst key, and
                    // the beam pass in LateUpdate).
                    return _firing ?? ship.PrimaryWeapon ?? ship.SecondaryWeapon;

                default:
                    return ship.PrimaryWeapon;
            }
        }

        private WeaponData SourceData()
        {
            WeaponBase pick = SourceWeapon();

            return (pick != null) ? pick.TemplateData : null;
        }

        // The modules slotted around the gun being copied.
        //
        // THIS IS WHY AN UPGRADE ON YOUR OWN WEAPON REACHES THE COPIES. A
        // module's effect is an `IWeaponModifier`, and `WeaponFactory.Create`
        // runs it against the BUILT WeaponBase - it never writes anything back
        // to the WeaponData asset. So cloning the asset gets you the gun as it
        // shipped and none of what the grid did to it, which is exactly what
        // R16 measured. The fix is not to clone harder, it is to hand the same
        // modules to our own Create call.
        private IEnumerable<Module> SourceModules()
        {
            if (!_cfg.inheritUpgrades || _unit == null)
                return null;

            try
            {
                var owner = _unit.GetComponentInChildren<ModuleGridOwner>(true);

                if (owner == null || owner.ModuleGrid == null)
                    return null;

                var ship = _unit.GetComponentInChildren<Ship>(true);

                if (ship == null)
                    return null;

                WeaponBase pick = SourceWeapon();

                ClusterType type =
                    (pick != null && ReferenceEquals(pick, ship.SecondaryWeapon))
                        ? ClusterType.SecondaryWeapon
                        : ClusterType.PrimaryWeapon;

                IModuleCluster cluster = owner.ModuleGrid.GetCluster(type);

                return (cluster != null)
                    ? cluster.ConnectedAndPoweredModules : null;
            }
            catch
            {
                // A grid we cannot read costs the upgrades, not the wingmen.
                return null;
            }
        }

        // Everything that should shape a copy: the gadget's own cluster (the
        // game's behaviour, and the one that already worked) plus the carried
        // weapon's cluster (ours, and new).
        private List<Module> ModulesForCopies()
        {
            var all = new List<Module>();

            if (_cluster != null)
            {
                IEnumerable<Module> mine = _cluster.ConnectedAndPoweredModules;

                if (mine != null)
                    foreach (Module m in mine)
                        if (m != null)
                            all.Add(m);
            }

            IEnumerable<Module> theirs = SourceModules();

            if (theirs != null)
                foreach (Module m in theirs)
                    if (m != null && !all.Contains(m))
                        all.Add(m);

            return all;
        }

        // True when this shot is one we mirror.
        public bool Mirrors(WeaponBase fired)
        {
            if (fired == null || _unit == null)
                return false;

            var ship = _unit.GetComponentInChildren<Ship>(true);

            if (ship == null)
                return false;

            bool isPrimary = ReferenceEquals(fired, ship.PrimaryWeapon);
            bool isSecondary = ReferenceEquals(fired, ship.SecondaryWeapon);

            switch (_cfg.mirror)
            {
                case ForgeWingman.Mirror.Secondary: return isSecondary;
                case ForgeWingman.Mirror.Both: return isPrimary || isSecondary;
                default: return isPrimary;
            }
        }

        private WeaponBase WeaponFor(Wing w)
        {
            // ANY grid change rebuilds every instance. That covers a module
            // dropped beside the gadget AND one dropped beside your own gun,
            // because both go through the same cluster refresh.
            //
            // R16: *"I have to unequippe the wingman module and reequipe it to
            // get the stat upgrade."* Re-equipping worked because it was the
            // only thing that ever rebuilt these - the instance was cached
            // against the CLONE, and installing a module does not change which
            // clone you are copying, so the cache never missed.
            if (_builtAt != ForgeWingmanPatch.Generation)
            {
                _builtAt = ForgeWingmanPatch.Generation;
                Rearm();
            }

            WeaponData source = SourceData();

            if (source == null)
            {
                SayNoWeapon();
                return null;
            }

            WeaponData data = ForgeWingmanWeapon.For(_cfg, source);

            if (data == null)
                return null;

            WeaponBase have;

            if (w.weapons.TryGetValue(data, out have) && have != null)
                return have;

            try
            {
                WeaponBase made =
                    new WeaponFactory().Create(data, ModulesForCopies());

                // Equip sets Owner, without which kills are not credited and
                // the friendly-fire skip never runs.
                made.Equip(_unit);

                // Creates this wingman's OWN beam visuals. The whole reason
                // each gets its own WeaponBase.
                made.InitializeVisuals();

                w.weapons[data] = made;

                return made;
            }
            catch (Exception e)
            {
                Log.LogError(
                    "wingman: could not build a weapon instance: " + e.Message);
            }

            return null;
        }

        private void SayNoWeapon()
        {
            if (_saidNoWeapon)
                return;

            _saidNoWeapon = true;

            Log.LogInfo(
                (_cfg != null ? _cfg.fileName + ": " : "") +
                "wingmen are set to mirror your " +
                _cfg.mirror.ToString().ToLowerInvariant() +
                " weapon and you are not carrying one, so they are idle. " +
                "They start working the moment you equip one. Said once.");
        }

        // ------------------------------------------------------------------
        // Firing
        // ------------------------------------------------------------------

        // Called from the DoShoot mirror. One volley per wingman per qualifying
        // shot, with `fireRatio` accumulated fractionally so 0.5 really is
        // every other shot rather than a coin flip.
        //
        // `fired` is the WeaponBase whose trigger was pulled. It is carried all
        // the way down rather than re-derived, because under `mirror: "both"`
        // it is the ONLY thing that says which of your two guns to copy.
        public void OnMirroredShot(WeaponBase fired)
        {
            if (_stowed || _wings.Count == 0)
                return;

            _shotCredit += Mathf.Max(0f, _cfg.fireRatio);

            int volleys = 0;

            while (_shotCredit >= 1f && volleys < 8)
            {
                _shotCredit -= 1f;
                volleys++;
            }

            if (volleys == 0)
                return;

            _firing = fired;

            try
            {
                for (int i = 0; i < volleys; i++)
                    FireAll();
            }
            finally
            {
                // Left set, deliberately: the beam pass in LateUpdate runs
                // after this and has to drive the same weapon. Cleared only
                // when the carried gun is gone.
            }
        }

        private void FireAll()
        {
            for (int i = 0; i < _wings.Count; i++)
                FireOne(_wings[i]);
        }

        private void FireOne(Wing w)
        {
            if (w == null || w.go == null)
                return;

            // "aim": "none" - a ring that never fires. Returned before the
            // copy is built, so a tackle ring does not keep a weapon per wing
            // it will never use.
            if (_cfg.aim == ForgeWingman.Aim.None)
                return;

            WeaponBase weapon = WeaponFor(w);

            if (weapon == null)
                return;

            Vector2 pos = w.go.transform.position;
            Vector2 dir = AimFor(w, pos);

            if (dir.sqrMagnitude <= 0.000001f)
                return;

            w.facing = dir;
            w.lastFired = weapon;

            try
            {
                // Fire costs no resource on this path - the cost lives in
                // Shooter.Shoot, which a gadget never reaches. That is correct
                // here: you already paid when your own gun fired.
                weapon.Fire(new FakeBarrel(pos, dir));
            }
            catch (Exception e)
            {
                Log.LogError("wingman: a shot failed: " + e.Message);
            }
        }

        // ------------------------------------------------------------------
        // Aiming
        // ------------------------------------------------------------------

        private Vector2 ShipAim()
        {
            if (_aimer != null)
            {
                Vector2 d = (Vector2)_aimer.TargetPosition -
                            (Vector2)transform.position;

                if (d.sqrMagnitude > 0.000001f)
                    return d.normalized;
            }

            return Vector2.right;
        }

        private Vector2 AimFor(Wing w, Vector2 from)
        {
            // No aim at all: nothing fires, and a zero here also leaves the
            // body's facing and a drone's head to their own devices.
            if (_cfg.aim == ForgeWingman.Aim.None)
                return Vector2.zero;

            // ★ STRIKER / VENGEANCE (R21 part 3): "firing point blank". A
            // companion sent at an enemy shoots THAT enemy, whatever the
            // file's own aim says - parallel or outward fire from beside an
            // enemy would miss it.
            if (_inMode && ForgeDroneModeRules.IsStrike(_modeNow))
            {
                Unit target;
                int index = _wings.IndexOf(w);

                if (index >= 0 &&
                    ForgeDroneModes.StrikeTargetFor(_unit, _modeNow, this, index, out target) &&
                    target != null)
                {
                    Vector2 d = (Vector2)target.transform.position - from;

                    if (d.sqrMagnitude > 0.000001f)
                        return d.normalized;
                }
            }

            switch (_cfg.aim)
            {
                case ForgeWingman.Aim.Cursor:
                {
                    if (_aimer == null)
                        return ShipAim();

                    Vector2 d = (Vector2)_aimer.TargetPosition - from;
                    return (d.sqrMagnitude > 0.000001f) ? d.normalized : ShipAim();
                }

                case ForgeWingman.Aim.Mirrored:
                {
                    // Angles mirrored about the ship's aim, opening outward:
                    // alternate sides, widening a step at a time.
                    float step = _cfg.spread * 0.5f;
                    int pair = (w.slot / 2) + 1;
                    float sign = ((w.slot % 2) == 0) ? 1f : -1f;

                    return Rotate(ShipAim(), sign * step * pair);
                }

                case ForgeWingman.Aim.Nearest:
                {
                    // `objectsToo: false` is the discriminator that keeps
                    // wingmen shooting enemies rather than crates: all 60
                    // combatant prefabs carry an AIAgent and none of the 13
                    // containers do, and a crate's faction is the same asset
                    // the Cross enemies use - so IsFriendsWith does not filter
                    // them out on its own.
                    AimAssistTarget t = ForgeHomingPatch.FindNearestTarget(
                        _unit, from, 14f, ~0, null, false);

                    if (t != null)
                    {
                        Vector2 d = (Vector2)t.transform.position - from;

                        if (d.sqrMagnitude > 0.000001f)
                            return d.normalized;
                    }

                    return ShipAim();
                }

                case ForgeWingman.Aim.Outward:
                {
                    // The line from the SHIP through this wingman, extended.
                    // One rule, and it is what makes a stopped ring fire at the
                    // compass points.
                    Vector2 d = from - (Vector2)transform.position;
                    return (d.sqrMagnitude > 0.000001f) ? d.normalized : ShipAim();
                }

                default:
                    return ShipAim();
            }
        }

        // Run entry.
        //
        // ★ THIS METHOD'S OWN COMMENT USED TO BE FALSE FOR A DRONE BODY. It
        // said *"the controllers themselves die with their GameObjects, so
        // this is only the statics that outlive a run"* - true of an orb, which
        // is a bare GameObject parented to nothing. A drone body is a
        // `SavableEntity` parented to the entity manager's own `Entities` root,
        // with its `EntityData` living in the `EntityManager`. It does not die
        // with the ship and its data outlives the scene object entirely.
        //
        // So run entry takes them explicitly. Without it, a death, a new run or
        // a continue would leave orphaned entities in the world: still drawn,
        // still solid, still damageable, owned by a controller that no longer
        // exists. Same family as the wingman weapon clones needing `Release` /
        // `Reset` - **the first runtime-created thing of a new kind always
        // needs its teardown written by hand.**
        public static void Reset()
        {
            for (int i = _live.Count - 1; i >= 0; i--)
            {
                ForgeWingmanController c = _live[i];

                if (c == null)
                    continue;

                try { c.RecallAll(); }
                catch (Exception e)
                {
                    Log.LogError(
                        "wingman: clearing companions at run entry failed: " + e);
                }
            }

            _live.Clear();
            _saidNoWeapon = false;
            _saidBodyDied = false;
        }

        private static Vector2 Rotate(Vector2 v, float degrees)
        {
            float r = degrees * Mathf.Deg2Rad;
            float c = Mathf.Cos(r), s = Mathf.Sin(r);

            return new Vector2(v.x * c - v.y * s, v.x * s + v.y * c);
        }

        // ------------------------------------------------------------------
        // Motion
        // ------------------------------------------------------------------

        private void Update()
        {
            if (_cfg == null || _unit == null)
                return;

            try
            {
                if (_cfg.IsDrone)
                    WatchEntities();

                TickBodies();
                TickAdopt();
                TickCap();

                if (_cfg.IsDrone)
                {
                    SampleHealth();
                    CensusAfterLoad();
                }
                TickGrid();
                TickDecay();
                TickRespawn();
                TickPath();
                TickMotion();
                TickDress();
                TickContact();
            }
            catch (Exception e)
            {
                Log.LogError("wingman: update failed: " + e);
            }
        }

        // ------------------------------------------------------------------
        // Drone bodies: picking them back up, and replacing them
        // ------------------------------------------------------------------

        // ★ AFTER A LOAD THE BODIES ARE ALREADY THERE, AND NOTHING TOLD US.
        //
        // The game restores a drone body by itself - `Unit.Data.Memento`
        // replays `ownerId` and `connectionToOwner` through `SetOwner`, so the
        // entity comes back, in the owner's minion set, under this gadget's
        // slot. The controller, meanwhile, is a fresh MonoBehaviour on a fresh
        // ship with an empty `_wings`. Without this sweep the companions would
        // be on screen, owned, damageable, and steered by nobody: they would
        // sit where they died-and-were-saved while the ship flew away, and the
        // gadget key would deploy MORE of them on top.
        //
        // A stray is recognised by slot rather than by a marker, because a
        // marker is a component and components do not survive a load. One
        // module per slot makes that unambiguous.
        private void TickAdopt()
        {
            if (!_cfg.IsDrone || _unit == null || _unit.ComponentData == null)
                return;

            IReadOnlyCollection<Unit.Data> minions = _unit.ComponentData.Minions;

            if (minions == null || minions.Count == 0)
                return;

            Unit.OwnerConnectionType want = Connection();

            if (want == Unit.OwnerConnectionType.Undefined)
                return;

            // THE CHEAP GATE, and it runs every frame on every ship in the
            // world that carries this gadget. Count first; walk only when the
            // numbers disagree.
            int mine = 0;

            foreach (Unit.Data d in minions)
                if (d != null && d.ConnectionToOwner == want)
                    mine++;

            if (mine <= _wings.Count)
                return;

            foreach (Unit.Data d in minions)
            {
                if (d == null || d.ConnectionToOwner != want || Held(d))
                    continue;

                Unit found = ForgeWingmanBody.Resolve(d);

                // Not loaded yet, or already destroyed. Either way there is
                // nothing to adopt this frame; we will be back next frame.
                if (found == null)
                    continue;

                // `persist: false` HAS TO CONSUME WHAT IT REFUSES. With orbs,
                // "do not persist" is achieved by not writing a record. A drone
                // body persists because the GAME saves entities, so the only
                // place the setting can be honoured is here - and leaving the
                // body alone would make it a deferral rather than a refusal,
                // which is the exact shape of the R17 `persist` bug.
                if (!_cfg.persist)
                {
                    Trace("a body the game handed back was REMOVED because " +
                          "\"persist\" is false (" +
                          ForgeWingmanBody.HealthText(found) + ")");
                    ForgeWingmanBody.Take(found);

                    if (!_saidUnpersisted)
                    {
                        _saidUnpersisted = true;

                        Log.LogInfo(
                            (_cfg.fileName ?? "") + ": wingman \"persist\": " +
                            "false with a drone body - the game had already " +
                            "brought your companions back (a drone is a saved " +
                            "entity, so it persists whether we ask or not), " +
                            "and they have been dismissed. Press the gadget " +
                            "key to deploy fresh ones. Said once per run.");
                    }

                    continue;
                }

                // STOWED MEANS NONE OUT, and it has to mean that for a body the
                // game handed back as much as for one we spawned. Every other
                // body-creating path here consults `_stowed`; this was the one
                // place the invariant was implied rather than stated, so a
                // stray arriving while the ring was put away would have been
                // adopted into a complement that is supposed to be empty.
                //
                // Taken rather than skipped, for the reason the `persist`
                // branch above spells out: leaving it alone is a deferral, not
                // a refusal, and it would fly around unsteered forever.
                if (_stowed)
                {
                    Trace("a body the game handed back was REMOVED because " +
                          "the ring is STOWED (" +
                          ForgeWingmanBody.HealthText(found) + ")");
                    ForgeWingmanBody.Take(found);
                    continue;
                }

                if (_wings.Count >= Cap)
                {
                    // Over the cap because the grid changed between saving and
                    // loading. TickCap would trim it a moment later anyway;
                    // taking it now saves a frame of a body nobody steers.
                    Trace("a body the game handed back was REMOVED because " +
                          "the ring was already full (" + _wings.Count +
                          " of a cap of " + Cap + ", level " + Level + ") - " +
                          ForgeWingmanBody.HealthText(found));
                    ForgeWingmanBody.Take(found);
                    continue;
                }

                var w = new Wing
                {
                    bornAt = Time.time,
                    slot = _wings.Count,
                    fresh = true,
                };

                Take(w, found);
                w.adopted = true;
                w.lastHealth = ForgeWingmanBody.HealthText(found);
                _wings.Add(w);

                Trace("re-adopted a body the game handed back: " +
                      w.lastHealth + " (now " + _wings.Count +
                      " of a cap of " + Cap + ")");

                // Everything that dies with the GameObject has to be put back:
                // the AI comes back ON after a load, the colour comes back
                // factory, and the collision ignores are gone.
                //
                // `fresh: false` is the load-shaped half of that. The module
                // GRID is restored by the game (it has a memento), so
                // installing the gadget's augmentations again would stack a
                // second copy; the stat EDITS are not (a restored module
                // carries the asset's effects), so those are re-applied - and
                // the tanks are deliberately not refilled, because the saved
                // health is the whole point of persisting a body rather than a
                // count.
                ForgeWingmanBody.Dress(
                    _cfg, found, _unit, Level, _cluster, false);

                if (!_saidAdopted)
                {
                    _saidAdopted = true;

                    Log.LogInfo(
                        (_cfg.fileName ?? "") + ": wingman - a drone companion " +
                        "was handed back by the game and re-adopted. Drone " +
                        "bodies are real saved entities, so they come back at " +
                        "the HEALTH they had rather than as fresh ones - which " +
                        "is why they persist through the game's own save " +
                        "system rather than through the orb count. Said once " +
                        "per run.");
                }
            }

            Reindex();
        }

        // Which grid generation the BODIES were last synced against. Separate
        // from `_builtAt`, which is the same question for the weapon copies -
        // two different things that go stale, on the same signal.
        private int _gridAt = -1;

        private bool _saidRegrid;

        // ★★ A COMPANION THAT OUTLIVES THE GRID IT WAS BORN FROM.
        //
        // `inheritModules` installs the gadget cluster's augmentations into the
        // body when it spawns, which is the game's own behaviour for a drone
        // gadget. On an ORB that is the whole story, because an orb is deployed
        // and recalled constantly and has no stats to speak of. A drone body
        // persists - across a BoosterCore, across armour slotted beside the
        // gadget, across a save - and nothing re-read the grid, so it kept
        // whatever it was born with.
        //
        // The tell is that the OTHER half of the same gadget already did this
        // right: `WeaponFor` rebuilds the weapon copies whenever
        // `ForgeWingmanPatch.Generation` moves, which is R16's *"I have to
        // unequip and re-equip to get the stat upgrade"* fix. So slotting a
        // module made the copies hit harder on the next shot and left the
        // bodies exactly as tough as before - two halves of one gadget
        // disagreeing about the same grid.
        //
        // > **When a feature caches something derived from live state, ask what
        // > ELSE it derived from that state.** The generation counter existed,
        // > was correct, and had one reader.
        //
        // Cheap: `Generation` only moves when a cluster actually recalculates,
        // which is a grid edit rather than a frame, so the steady state is one
        // int comparison.
        private void TickGrid()
        {
            if (!_cfg.IsDrone || _wings.Count == 0)
                return;

            int now = ForgeWingmanPatch.Generation;

            if (_gridAt == now)
                return;

            bool first = _gridAt < 0;
            _gridAt = now;

            // Nothing to re-sync on the first tick that has a companion in it:
            // whatever is out was dressed against the grid as it is.
            if (first)
                return;

            int done = 0;

            for (int i = 0; i < _wings.Count; i++)
            {
                Wing w = _wings[i];

                if (w.body == null)
                    continue;

                if (ForgeWingmanBody.Resync(_cfg, w.body, Level, _cluster))
                    done++;
            }

            if (done == 0 || _saidRegrid)
                return;

            _saidRegrid = true;

            Log.LogInfo(
                (_cfg.fileName ?? "") + ": wingman - your module grid changed, " +
                "so the " + done + " companion(s) already out were re-synced " +
                "to it. Their health is NOT topped up by this: a drone body " +
                "keeps the damage it has taken, and refilling on a grid edit " +
                "would make slotting and unslotting a card a free heal. Said " +
                "once per run.");
        }

        private bool Held(Unit.Data data)
        {
            for (int i = 0; i < _wings.Count; i++)
                if (ReferenceEquals(_wings[i].data, data))
                    return true;

            return false;
        }

        // When each lost companion died, oldest first. Only used when
        // `drone.respawn` is set; a 0 there is the default and means "gone
        // until you press the gadget key again", which is the deploy economy
        // the rest of the gadget already runs on.
        private readonly List<float> _lost = new List<float>();

        // Instance fields rather than statics, and that is what makes them
        // per-RUN with no reset wiring: a controller dies with the ship.
        // `ForgeDiagnosticGates` sweeps the static `_said*` gates by naming
        // convention precisely because those do NOT die on their own.
        private bool _saidAdopted;
        private bool _saidUnpersisted;
        private bool _saidRespawnWaiting;
        private bool _saidLost;
        private bool _saidNoSlot;

        private void TickRespawn()
        {
            if (!_cfg.IsDrone || _cfg.drone.respawn <= 0f || _lost.Count == 0)
                return;

            while (_lost.Count > 0 &&
                   Time.time - _lost[0] >= _cfg.drone.respawn)
            {
                if (_stowed || _wings.Count >= Cap)
                {
                    // Nothing to come back to. Drop the claim rather than
                    // holding it forever, or stowing a ring would queue a
                    // replacement for every companion in it.
                    _lost.RemoveAt(0);
                    continue;
                }

                // A RESPAWN THAT CANNOT BE PAID FOR WAITS - it is never
                // skipped. A skipped respawn is a companion that silently never
                // returns, which is the same failure shape as a refusal that
                // does not consume what it refuses.
                if (_cfg.drone.respawnCost && !CanAfford())
                {
                    if (!_saidRespawnWaiting)
                    {
                        _saidRespawnWaiting = true;

                        Log.LogInfo(
                            (_cfg.fileName ?? "") + ": wingman " +
                            "\"drone.respawnCost\": true - a companion is due " +
                            "back and you cannot afford the gadget's " +
                            "activation cost, so the respawn is WAITING rather " +
                            "than being skipped. It arrives the moment you can " +
                            "pay. Said once per run.");
                    }

                    return;
                }

                if (!Spawn())
                    return;

                if (_cfg.drone.respawnCost)
                    Charge();

                _lost.RemoveAt(0);
            }
        }

        private bool CanAfford()
        {
            ActiveModule m = Gadget;

            if (m == null || m.ResourceUsed == null || m.ActivationCost <= 0f)
                return true;

            return _unit.GetResource(m.ResourceUsed) >= m.ActivationCost;
        }

        private void Charge()
        {
            ActiveModule m = Gadget;

            if (m == null || m.ResourceUsed == null || m.ActivationCost <= 0f)
                return;

            ResourceTank tank = _unit.GetTank(m.ResourceUsed);

            if (tank != null)
                tank.Value -= m.ActivationCost;
        }

        // ------------------------------------------------------------------
        // Contact damage
        // ------------------------------------------------------------------

        // ⚠ THE SWEPT CAST READS `rigidbody.linearVelocity`, WHICH IS ZERO ON A
        // KINEMATIC BODY - so `drone.contactDamage` on the DEFAULT body would
        // have done nothing at all, silently, with every number in the build
        // log correct. `ForgeContactController.DetectSwept` returns early below
        // 0.01 units/second, and a body moved by `transform.position` reports
        // exactly 0.
        //
        // The controller already knows where it put each body last frame, so
        // the honest velocity is a position delta - and handing that over makes
        // `scaleBySpeed` meaningful as well, which is the whole appeal of a
        // tackle: a fast-spinning ring hits harder than a parked one.
        private void TickContact()
        {
            if (!_cfg.IsDrone || _cfg.drone.contact == null)
                return;

            float dt = Mathf.Max(0.0001f, Time.deltaTime);

            for (int i = 0; i < _wings.Count; i++)
            {
                Wing w = _wings[i];

                if (w.go == null)
                    continue;

                Vector2 now = w.go.transform.position;

                if (w.contact == null)
                    w.contact = w.go.GetComponent<ForgeContactController>();

                if (w.contact != null)
                {
                    if (!_cfg.drone.physics)
                    {
                        w.contact.drivenVelocity =
                            w.hasLast ? (now - w.lastPos) / dt : Vector2.zero;

                        w.contact.hasDrivenVelocity = true;
                    }

                    // A tackle kill is credited to the copy this companion
                    // fires, which does not exist until it has fired once - so
                    // it is handed over here rather than at spawn. Before the
                    // first shot a contact kill credits nobody and pays no
                    // `onKill` reward, which is the same state a stock Larva's
                    // beak kill is in permanently.
                    if (w.lastFired != null)
                        w.contact.Attribute(_unit, w.lastFired);
                }

                w.lastPos = now;
                w.hasLast = true;
            }
        }

        // ------------------------------------------------------------------
        // The spring that holds a PHYSICAL body on the ring
        // ------------------------------------------------------------------

        // Only under `drone.physics`. A kinematic body is written straight to
        // its place in `Put`; a dynamic one is pulled toward it, so an
        // explosion really can knock a companion out of formation and it really
        // does come back. Critically damped off `looseness`, so the one knob
        // that already means "how eagerly do they take up station" means the
        // same thing here.
        //
        // Mass-compensated, for the reason the gravity well is: PUNK's
        // rigidbodies differ by a factor of 200 in mass, so a raw force number
        // would move one body sixty times faster than another.
        private void FixedUpdate()
        {
            if (_cfg == null || !_cfg.IsDrone || !_cfg.drone.physics)
                return;

            float ease = Mathf.Max(0.05f, _cfg.looseness);
            float stiffness = 1f / (ease * ease);
            float damping = 2f / ease;

            for (int i = 0; i < _wings.Count; i++)
            {
                Wing w = _wings[i];

                if (w.body == null || w.go == null)
                    continue;

                Rigidbody2D rb = w.body.GetComponent<Rigidbody2D>();

                if (rb == null || rb.bodyType != RigidbodyType2D.Dynamic)
                    continue;

                Vector2 to = w.target - (Vector2)w.go.transform.position;

                rb.AddForce(
                    (to * stiffness - rb.linearVelocity * damping) * rb.mass);
            }
        }

        // THE CAP HAS TO BE ENFORCED DOWNWARD, NOT ONLY CHECKED ON THE WAY UP.
        //
        // `Cap` is computed live off the module's `Level`, so pulling a
        // BoosterCore lowers it immediately - but nothing was reading it except
        // `Deploy` and `DeployAll`, both of which only ever ADD. So the extra
        // companions a core had paid for stayed out after it was removed, and
        // the count only corrected itself the next time the gadget key was
        // pressed and the `_wings.Count < Cap` test finally failed.
        //
        // R17 test 215, and he named the right precedent himself: *"This
        // probably should get the same treatment as drones where removing the
        // booster caps removes those orbs that are over the limit."* It is not
        // just a precedent - it is the game's own behaviour one class along.
        // `SpawnMinionModule.Activate` refuses at `count >= Level` and
        // `OnUpdate` enforces the same number downward by destroying the
        // excess, every frame. We had written the first half and not the
        // second.
        //
        // > A LIMIT READ ONLY WHERE THINGS ARE CREATED IS A GATE, NOT A CAP.
        // > It is correct for every input that goes through that gate and
        // > silently wrong for every other way the number can change - and a
        // > cap derived from live state (a level, a stat, a module) has plenty
        // > of those.
        //
        // Newest first, matching `RecallOne` and `atCap`, so what you lose is
        // what the core most recently bought you rather than your oldest wing.
        private void TickCap()
        {
            int cap = Cap;

            if (_wings.Count <= cap)
                return;

            int over = _wings.Count - cap;

            while (_wings.Count > cap && _wings.Count > 0)
            {
                Kill(_wings[_wings.Count - 1]);
                _wings.RemoveAt(_wings.Count - 1);
            }

            // `Reindex` is the whole re-spacing mechanism: the ring is spaced
            // from `_wings.Count` and each wing's `slot`, live, which is the
            // R16 fix for a ring spaced by the CAP instead of by how many were
            // actually out. So trimming three of six re-spreads the other
            // three for free, and this is exactly the state that bug lived in.
            Reindex();

            if (_saidTrimmed)
                return;

            _saidTrimmed = true;

            Log.LogInfo(
                (_cfg != null ? _cfg.fileName + ": " : "") +
                "wingman - the cap dropped to " + cap + " (a BoosterCore " +
                "beside the gadget was removed, or the module lost a level), " +
                "so " + over + " companion(s) over the new limit were " +
                "recalled. The game's own drone gadgets do the same. Said " +
                "once per run.");
        }

        private bool _saidTrimmed;

        // ---- R19 test 259 instrument ----------------------------------
        //
        // His report: hurt drones came back from an in-session continue and
        // were MISSING after quitting to the desktop and continuing. The
        // night's log shows the relaunch path handing three bodies back and
        // losing one almost at once - and nothing saying why, because every
        // line on this path was once-per-run. So instead of a fourth guess,
        // this says what happened to each body. Capped per controller, so a
        // long fight with a respawning ring cannot flood the log.
        private int _traceLines;
        private const int TraceCap = 24;

        private void Trace(string what)
        {
            if (_traceLines >= TraceCap)
                return;

            _traceLines++;

            Log.LogInfo(
                (_cfg != null ? _cfg.fileName + ": " : "") +
                "wingman BODY - " + what +
                (_traceLines == TraceCap
                    ? " (last body line this run - the rest are not logged)"
                    : ""));
        }

        private bool StillOwned(Unit.Data data)
        {
            if (data == null || _unit == null || _unit.ComponentData == null)
                return false;

            var minions = _unit.ComponentData.Minions;

            if (minions == null)
                return false;

            foreach (Unit.Data d in minions)
                if (ReferenceEquals(d, data))
                    return true;

            return false;
        }

        // One tank read per companion, four times a second - cheap enough,
        // and it is what lets a vanished body be described as it was.
        private float _nextHealthSample;

        private void SampleHealth()
        {
            if (Time.time < _nextHealthSample)
                return;

            _nextHealthSample = Time.time + 0.25f;

            for (int i = 0; i < _wings.Count; i++)
                if (_wings[i].body != null)
                    _wings[i].lastHealth =
                        ForgeWingmanBody.HealthText(_wings[i].body);
        }

        // A body that is gone when we did not remove it ourselves. This should
        // not happen any more - `ForgeWingmanArt.Disarm` now strips every
        // script from a borrowed prefab, and it was `ParticleLifetime` on
        // `FuelPickup` calling Destroy at 5-7 seconds that made it happen in
        // R16 - but a wing holding a destroyed GameObject is invisible AND
        // silent: it stops drawing, stops firing, and still counts toward the
        // cap. Better to notice and say so than to leave a phantom in the list.
        private void TickBodies()
        {
            for (int i = _wings.Count - 1; i >= 0; i--)
            {
                if (_wings[i].go != null)
                    continue;

                bool wasDrone = _wings[i].data != null;

                if (wasDrone)
                {
                    Unit.Data lostData = _wings[i].data;
                    bool stillOwned = StillOwned(lostData);

                    Trace("a companion's body vanished " +
                          (Time.time - _wings[i].bornAt).ToString("0.0") +
                          "s after it " +
                          (_wings[i].adopted
                              ? "was handed back by a load"
                              : "was spawned") +
                          ". Last seen at " + _wings[i].lastHealth +
                          ". Its saved data is " +
                          (stillOwned
                              ? "STILL in your ship's minion list, so it was " +
                                "UNLOADED rather than killed and should come back"
                              : "GONE from your ship's minion list, so it was " +
                                "destroyed"));
                }

                Kill(_wings[i]);
                _wings.RemoveAt(i);

                // A DRONE BODY VANISHING IS ORDINARY, not a borrowed-prefab
                // bug: something shot it. So it takes the other branch, and it
                // queues a replacement when the file asked for one.
                if (wasDrone)
                {
                    if (_cfg.drone != null && _cfg.drone.respawn > 0f)
                        _lost.Add(Time.time);

                    if (!_saidLost)
                    {
                        _saidLost = true;

                        Log.LogInfo(
                            (_cfg != null ? _cfg.fileName + ": " : "") +
                            "wingman - a drone companion was destroyed. " +
                            "Drone bodies are real units: enemies target them, " +
                            "enemy fire hits them, and your OWN explosions and " +
                            "gas clouds hit them too (the game puts no faction " +
                            "filter on a blast at all). That is the cost of a " +
                            "damageable companion, and it is the honest one - " +
                            "what it cannot do is PAY you, which is guarded " +
                            "separately. " +
                            (_cfg != null && _cfg.drone != null &&
                             _cfg.drone.respawn > 0f
                                ? "A replacement is due in " +
                                  _cfg.drone.respawn.ToString("0.#") + "s."
                                : "Press the gadget key to put another out, or " +
                                  "set \"drone\": { \"respawn\": 8 } for a " +
                                  "timer.") +
                            " Said once per run.");
                    }

                    continue;
                }

                if (_saidBodyDied)
                    continue;

                _saidBodyDied = true;

                Log.LogWarning(
                    (_cfg != null ? _cfg.fileName + ": " : "") +
                    "wingman - something outside this mod destroyed a " +
                    "companion's body, so it has been dropped from the count " +
                    "rather than left as an invisible one that still fills a " +
                    "slot. If you borrowed a prefab for \"visual\", it is that " +
                    "prefab doing it. Said once per run.");
            }

            if (_wings.Count > 0)
                Reindex();
        }

        private void TickDecay()
        {
            if (_cfg.decay <= 0f)
                return;

            for (int i = _wings.Count - 1; i >= 0; i--)
            {
                if (Time.time - _wings[i].bornAt < _cfg.decay)
                    continue;

                Kill(_wings[i]);
                _wings.RemoveAt(i);
            }

            Reindex();
        }

        private void TickPath()
        {
            if (_cfg.motion != ForgeWingman.Motion.Trail)
                return;

            Vector2 here = transform.position;

            if (_path.Count == 0 ||
                Vector2.Distance(_path[_path.Count - 1], here) >= PathStep)
            {
                _path.Add(here);

                int need = Mathf.CeilToInt(
                    (Mathf.Max(1, Cap) * Mathf.Max(0.1f, _cfg.spacing)) /
                    PathStep) + 8;

                while (_path.Count > need)
                    _path.RemoveAt(0);
            }
        }

        // The direction the beads measure their side from.
        private bool TryBearing(out float degrees)
        {
            degrees = 0f;

            Vector2 v = Vector2.zero;

            if (_cfg.bearing == ForgeWingman.Bearing.Aim)
            {
                v = ShipAim();
            }
            else
            {
                Vector2 move = MovementVector();

                if (_cfg.bearing == ForgeWingman.Bearing.Both)
                {
                    Vector2 aim = ShipAim();

                    v = (move.sqrMagnitude > 0.0001f)
                        ? (move.normalized + aim).normalized
                        : Vector2.zero;
                }
                else
                {
                    v = move;
                }
            }

            if (v.sqrMagnitude <= 0.0001f)
                return false;

            degrees = Mathf.Atan2(v.y, v.x) * Mathf.Rad2Deg;
            return true;
        }

        private Vector2 MovementVector()
        {
            Vector2 vel = (_body != null) ? _body.linearVelocity : Vector2.zero;

            Vector2 input = (_movement != null)
                ? _movement.FlyDirection : Vector2.zero;

            switch (_cfg.signal)
            {
                case ForgeWingman.Signal.Velocity:
                    return vel;

                case ForgeWingman.Signal.Input:
                    return input;

                default:
                    // Summed, so you get crisp response to what you press AND
                    // still get swung when something throws you across the room.
                    return (vel.sqrMagnitude > 0.0001f ? vel.normalized : Vector2.zero) +
                           (input.sqrMagnitude > 0.0001f ? input.normalized : Vector2.zero);
            }
        }

        private void TickMotion()
        {
            if (_wings.Count == 0)
                return;

            // DRONE MODES first: while this gadget's slot is in a formation the
            // ring is placed by the mode, and the file's own motion waits.
            if (TickMode())
                return;

            if (_cfg.motion == ForgeWingman.Motion.Trail)
            {
                PlaceTrail();
                return;
            }

            if (_cfg.motion == ForgeWingman.Motion.Orbit)
            {
                PlaceOrbit();
                return;
            }

            if (_cfg.UsesRow)
            {
                PlaceRow();
                return;
            }

            PlaceBeads();
        }

        // protector / rearguard - his R21 part 1 answer: "it should be its own
        // settings in the wingman". The drone modes' PROTECTOR and REAR GUARD
        // rows as this gadget's NORMAL motion: the same shape function
        // (ForgeDroneModeRules.Screen), with this file's own "distance",
        // "spacing", "looseness" and "bearing" (the aim unless it says
        // otherwise). A drone mode still takes over while its slot is in one
        // (TickMode, above) and hands back to this.
        private float _rowBearing;
        private bool _hasRowBearing;

        private void PlaceRow()
        {
            float deg;

            // A bearing that has gone quiet (movement, standing still) keeps
            // the last one, as drag / lead do, rather than snapping to east.
            if (TryBearing(out deg))
            {
                _rowBearing = deg;
                _hasRowBearing = true;
            }
            else if (!_hasRowBearing)
            {
                _rowBearing = 0f;
            }

            bool rear = _cfg.motion == ForgeWingman.Motion.RearGuard;
            float a = (_rowBearing + (rear ? 180f : 0f)) * Mathf.Deg2Rad;

            Vector2 here = transform.position;
            var ship = new ForgeDroneModeRules.P(here.x, here.y);
            var facing = new ForgeDroneModeRules.P(Mathf.Cos(a), Mathf.Sin(a));

            float gap = Mathf.Max(0.1f, _cfg.spacing) *
                        (rear ? ForgeDroneModeRules.RearGuardWiden : 1f);

            float k = 1f - Mathf.Exp(-Time.deltaTime / Mathf.Max(0.01f, _cfg.looseness));
            bool physical = _cfg.IsDrone && _cfg.drone.physics;
            int n = _wings.Count;

            for (int i = 0; i < n; i++)
            {
                Wing w = _wings[i];

                if (w.go == null)
                    continue;

                ForgeDroneModeRules.P at = ForgeDroneModeRules.Screen(
                    ship, facing, i, n, _cfg.RowDistance, gap);

                Vector2 spot = new Vector2(at.x, at.y);
                Vector2 to = w.fresh ? spot : Vector2.Lerp(w.go.transform.position, spot, k);

                // Same split as `Put`: a physical body is pulled to its place
                // rather than written to it, or a knockback would be erased on
                // the frame it landed.
                w.target = physical ? spot : to;

                if (!physical)
                    w.go.transform.position = to;

                w.fresh = false;
            }
        }

        // ------------------------------------------------------------------
        // Drone modes (DRONE MODES - CONCEPT.md, 5.1)
        // ------------------------------------------------------------------
        //
        // ★ AN OVERLAY, NEVER A WRITE. A mode does not touch `_cfg` - the
        // Config is shared by every ship carrying this file. It answers "where
        // do I put companion i" instead of the file's own motion, for the four
        // formations and INTERCEPTOR, and hands back the moment the slot is on
        // normal again. SEEKER and SENTRY are AI-drone modes; a wingman ring
        // keeps its own motion under them (answer 5, 5.4).
        //
        // The spot comes from ForgeDroneModes, because every companion of the
        // ship in one mode is ONE formation - two slots on ORBIT are one ring,
        // wingmen and AI drones together, never two drawn on top of each other.
        private bool _inMode;
        private ForgeDroneModeRules.Mode _modeNow = ForgeDroneModeRules.Mode.Base;
        private const float ModeEase = 0.1f;   // seconds-ish to settle
        private const float OrbCatch = 0.8f;   // INTERCEPTOR test option, orbs

        private readonly Dictionary<Unit, GameObject> _modeBlockers =
            new Dictionary<Unit, GameObject>();

        private bool TickMode()
        {
            ForgeDroneModeRules.Mode m = ForgeDroneModeRules.Mode.Base;
            int slot = ForgeDroneModes.SlotOf(Connection());

            if (slot > 0)
                m = ForgeDroneModes.Effective(_unit, slot);

            bool placed = ForgeDroneModeRules.IsPlaced(m);

            Vector2 first;

            // Not in a formation, or not in THIS one (orbs under INTERCEPTOR
            // without the test option have no hitbox to block with).
            if (!placed || !ForgeDroneModes.SpotFor(_unit, m, this, 0, out first))
            {
                if (_inMode)
                    LeaveMode();

                return false;
            }

            _inMode = true;
            _modeNow = m;

            bool guard = ForgeDroneModeRules.IsGuard(m);

            // The interceptSpeed slider (R21 part 3): a shot-guarding wingman
            // settles on its spot faster as it goes up - 0.1 s at 100%, a
            // quarter of that at 400%.
            float ease = guard ? ForgeDroneModes.TuningOf(_unit, slot).ease : ModeEase;
            float k = 1f - Mathf.Exp(-Time.deltaTime / Mathf.Max(0.01f, ease));
            bool physical = _cfg.IsDrone && _cfg.drone.physics;

            for (int i = 0; i < _wings.Count; i++)
            {
                Wing w = _wings[i];

                if (w.go == null)
                    continue;

                Vector2 spot;

                if (!ForgeDroneModes.SpotFor(_unit, m, this, i, out spot))
                    continue;

                Vector2 cur = w.go.transform.position;
                Vector2 to = w.fresh ? spot : Vector2.Lerp(cur, spot, k);

                // Same split as `Put`: a physical body is pulled by the spring
                // in FixedUpdate, a kinematic one or an orb is written.
                w.target = physical ? spot : to;

                if (!physical)
                    w.go.transform.position = to;

                w.fresh = false;

                if (guard && ForgeDroneModes.InterceptorNoDamage)
                {
                    // The test option (answer C): a body takes the hit and
                    // loses nothing; an orb, which has no hitbox, deletes the
                    // enemy shots it meets.
                    if (w.body != null)
                        BlockFor(w.body);
                    else
                        ForgeProjectileTracker.DestroyNear(to, OrbCatch);
                }
            }

            if (!(guard && ForgeDroneModes.InterceptorNoDamage))
                UnblockAll();

            return true;
        }

        // Back to the file's own motion. Each companion's ring angle is
        // re-read from where it actually is, so the ring eases round from
        // there instead of jumping to wherever it was before the mode.
        private void LeaveMode()
        {
            _inMode = false;
            _modeNow = ForgeDroneModeRules.Mode.Base;

            Vector2 here = transform.position;

            for (int i = 0; i < _wings.Count; i++)
            {
                Wing w = _wings[i];

                if (w.go == null)
                    continue;

                Vector2 d = (Vector2)w.go.transform.position - here;

                if (d.sqrMagnitude > 0.0001f)
                    w.angle = Mathf.Repeat(Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg, 360f);
            }

            UnblockAll();
        }

        private void BlockFor(Unit body)
        {
            GameObject b;

            if (_modeBlockers.TryGetValue(body, out b) && b != null)
                return;

            var health = body.GetComponentInChildren<DamagableResource>(true);

            if (health == null)
                return;

            b = new GameObject("ForgeInterceptorTest");
            b.transform.SetParent(body.transform, false);

            if (health.damageBlockers == null)
                health.damageBlockers = new List<GameObject>();

            health.damageBlockers.Add(b);
            _modeBlockers[body] = b;
        }

        private void UnblockAll()
        {
            if (_modeBlockers.Count == 0)
                return;

            foreach (var pair in _modeBlockers)
            {
                if (pair.Key != null)
                {
                    var health = pair.Key.GetComponentInChildren<DamagableResource>(true);

                    if (health != null && health.damageBlockers != null)
                        health.damageBlockers.Remove(pair.Value);
                }

                if (pair.Value != null)
                    Destroy(pair.Value);
            }

            _modeBlockers.Clear();
        }

        // ONE RING PHASE, and every companion is placed at an EVEN OFFSET from
        // it. That is the whole of the orbit fix.
        //
        // Before, each companion carried its own angle, advanced it by
        // `spinSpeed` every frame, and was given a starting angle of
        // `index * 360 / CAP` at spawn. Two things went wrong with that, both
        // reported from R16 play:
        //
        //   * SPACED BY THE CAP, NOT BY HOW MANY ARE OUT. Deploy three of a cap
        //     of six and they sit at 0, 60 and 120 - a third of the ring, in a
        //     clump, with half the circle empty. *"the orbs dont manage to
        //     spread out evenly even if i set to 90 degrees."*
        //   * NOTHING EVER RE-SPREAD THEM. Recall one, let one decay, or raise
        //     the cap with a BoosterCore, and the survivors kept whatever
        //     angles they happened to hold. There was no force pulling the ring
        //     back to even, so every disturbance was permanent.
        //
        // Deriving each position from a shared phase makes even spacing the
        // only state the ring can be in, rather than a state it is put into
        // once at spawn and then drifts out of.
        private void PlaceOrbit()
        {
            int n = _wings.Count;

            if (n == 0)
                return;

            _ringPhase = Mathf.Repeat(
                _ringPhase + _cfg.spinSpeed * Time.deltaTime, 360f);

            int me = GatherSiblings();
            int total = 0;

            for (int j = 0; j < _siblings.Count; j++)
                total += _siblings[j]._wings.Count;

            float k = 1f - Mathf.Exp(
                -Time.deltaTime / Mathf.Max(0.01f, _cfg.looseness));

            for (int i = 0; i < n; i++)
            {
                Wing w = _wings[i];

                float target = Mathf.Repeat(
                    _ringPhase + SharedAngle(w.slot, n, me, total), 360f);

                // A companion that has just been spawned goes straight to its
                // place; the rest ease, so an arrival slides everyone round
                // rather than teleporting the ring.
                w.angle = w.fresh
                    ? target
                    : Mathf.Repeat(
                        w.angle + Mathf.DeltaAngle(w.angle, target) * k, 360f);

                w.fresh = false;

                Put(w, w.angle);
            }
        }

        // ★ TWO COPIES OF ONE FILE WOULD FLY IN EACH OTHER'S PLACES. Since R19
        // test 267 each gadget slot runs its own ring, and two rings from one
        // file share a radius and a spin - both start at phase 0, both turn at
        // `spinSpeed` - so drone i of the second sat exactly on drone i of the
        // first, and six companions read as three: the shared cap he reported,
        // drawn by the fix for it. Found while writing R20's test, not in play.
        // So the copies fly as ONE even ring: every copy follows the first
        // copy's phase, and each companion takes a place on the shared circle
        // in the order of where it sits on its own - A's first, B's first, A's
        // second, B's second... An offset per copy was tried first and failed on
        // paper for two out and one out (both landed at 180). A ring flying
        // alone gets exactly its old spacing, `slot * 360 / n`.
        private static readonly List<ForgeWingmanController> _siblings =
            new List<ForgeWingmanController>();

        // Fills `_siblings` with every copy of this file on this ship that has
        // companions out, in slot order, and returns where this one is in it.
        private int GatherSiblings()
        {
            _siblings.Clear();

            for (int i = 0; i < _live.Count; i++)
            {
                ForgeWingmanController c = _live[i];

                if (c == null || c._gone || c._cfg != _cfg || c._unit != _unit ||
                    c._wings.Count == 0)
                {
                    continue;
                }

                int at = 0;

                while (at < _siblings.Count && Before(_siblings[at], c))
                    at++;

                _siblings.Insert(at, c);
            }

            int me = _siblings.IndexOf(this);

            if (me < 0)
            {
                _siblings.Clear();
                _siblings.Add(this);
                me = 0;
            }

            if (me > 0)
                _ringPhase = _siblings[0]._ringPhase;

            return me;
        }

        // Degrees from the ring phase for companion `s` of `n` in this copy.
        // Its place on the shared circle is how many companions of ALL copies
        // come before it, ordering by s/n and then by copy - counted in whole
        // numbers, so two places can never tie by rounding.
        private static float SharedAngle(int s, int n, int me, int total)
        {
            if (total <= 0 || n <= 0)
                return 0f;

            int index = s;

            for (int j = 0; j < _siblings.Count; j++)
            {
                if (j == me)
                    continue;

                int nj = _siblings[j]._wings.Count;
                int num = s * nj;

                // Companions t of copy j with t/nj < s/n.
                int before = (num + n - 1) / n;

                // One exactly level with this one: the earlier copy goes first.
                if (j < me && num % n == 0)
                    before++;

                index += before;
            }

            return index * 360f / total;
        }

        // Slot order, so which copy leads does not depend on which was built
        // first - gadget 1 before gadget 3, every time, after a load as well.
        private static bool Before(ForgeWingmanController a, ForgeWingmanController b)
        {
            int sa = a._slotKnown ? (int)a._slot : int.MaxValue;
            int sb = b._slotKnown ? (int)b._slot : int.MaxValue;

            if (sa != sb)
                return sa < sb;

            return a.GetInstanceID() < b.GetInstanceID();
        }

        private void PlaceTrail()
        {
            for (int i = 0; i < _wings.Count; i++)
            {
                Wing w = _wings[i];

                if (w.go == null)
                    continue;

                float back = (i + 1) * Mathf.Max(0.1f, _cfg.spacing);
                int steps = Mathf.RoundToInt(back / PathStep);
                int idx = _path.Count - 1 - steps;

                // Not enough path yet - hold where they are rather than
                // snapping onto the ship, which is the same answer `atRest`
                // gives for the ring.
                if (idx < 0 || _path.Count == 0)
                    continue;

                Vector2 target = _path[idx];

                Vector2 to = w.fresh
                    ? target
                    : Vector2.Lerp(
                        w.go.transform.position, target,
                        1f - Mathf.Exp(-Time.deltaTime /
                                       Mathf.Max(0.01f, _cfg.smoothing)));

                w.target = to;

                // Same split as `Put`: a physical body is pulled to its place
                // rather than written to it, or a knockback would be erased on
                // the frame it landed.
                if (!(_cfg.IsDrone && _cfg.drone.physics))
                    w.go.transform.position = to;

                w.fresh = false;
            }
        }

        // drag / lead: beads on a hoop.
        private void PlaceBeads()
        {
            float bearing;
            bool moving = TryBearing(out bearing);

            if (moving)
            {
                _lastBearing = bearing;
                _hasBearing = true;
                _restingSince = -1f;
            }
            else if (_restingSince < 0f)
            {
                _restingSince = Time.time;
            }

            bool relax = !moving && AtRestRelaxes();

            float centre = _cfg.motion == ForgeWingman.Motion.Drag
                ? (_hasBearing ? _lastBearing + 180f : 180f)
                : (_hasBearing ? _lastBearing : 0f);

            int n = _wings.Count;

            // Relax hands out DISTINCT slots, which is the fix for the last
            // thing R16 reported about this mode: *"Stopping keeps them in
            // their place for a moment then for some reason the 3 stack as one
            // shooting in the same direction."*
            //
            // They stacked because each bead independently rounded its own
            // angle to the nearest multiple of 360/n. Under `drag` every bead
            // is bunched within `spread` degrees of one point BY DESIGN, so
            // with three beads inside 55 degrees and slots 120 degrees apart,
            // all three round to the SAME slot and converge on it. The rule
            // "ease to your nearest even slot" is correct for one bead and
            // wrong for a set, and the bug only shows up in the state the
            // feature exists to produce.
            float[] relaxTo = relax ? RelaxSlots(n) : null;

            for (int i = 0; i < n; i++)
            {
                Wing w = _wings[i];

                float target;

                if (relax)
                {
                    target = relaxTo[i];
                }
                else if (_cfg.bunching == ForgeWingman.Bunching.Repel)
                {
                    target = centre;
                }
                else
                {
                    float step = (n > 1) ? _cfg.spread / (n - 1) : 0f;
                    target = centre - (_cfg.spread * 0.5f) + step * w.slot;
                }

                float k = 1f - Mathf.Exp(
                    -Time.deltaTime / Mathf.Max(0.01f, _cfg.looseness));

                w.angle = w.fresh
                    ? target
                    : Mathf.Repeat(
                        w.angle + Mathf.DeltaAngle(w.angle, target) * k, 360f);

                w.fresh = false;
            }

            if (!relax && _cfg.bunching == ForgeWingman.Bunching.Repel)
                Repel(n);

            for (int i = 0; i < n; i++)
                Put(_wings[i], _wings[i].angle);
        }

        private bool AtRestRelaxes()
        {
            // Never consulted under an aim bearing - a cursor always has a
            // position, so those beads never go slack.
            if (_cfg.bearing == ForgeWingman.Bearing.Aim)
                return false;

            switch (_cfg.atRest)
            {
                case ForgeWingman.AtRest.Hold:
                    return false;

                case ForgeWingman.AtRest.HoldThenRelax:
                    return _restingSince >= 0f &&
                           Time.time - _restingSince >= _cfg.holdSeconds;

                default:
                    return true;
            }
        }

        // One even slot per companion, assigned so that the SET travels as
        // little as possible - which is the same intent the old per-bead
        // "nearest slot" had, but expressed over the whole ring so two beads
        // cannot be handed the same answer.
        //
        // Sort by current angle, then try each of the n rotations of the slot
        // ring and keep the cheapest. Ring order is preserved by construction,
        // so nobody crosses anybody on the way out - the property the old code
        // was reaching for when it picked "nearest" over "by index".
        //
        // n is a handful, so O(n^2) here is free and a matching algorithm would
        // be ceremony.
        private float[] RelaxSlots(int n)
        {
            var slots = new float[n];

            if (n <= 0)
                return slots;

            float step = 360f / n;

            var order = new int[n];

            for (int i = 0; i < n; i++)
                order[i] = i;

            // Insertion sort by angle: n is tiny and this keeps it allocation
            // free.
            for (int i = 1; i < n; i++)
            {
                int key = order[i];
                int j = i - 1;

                while (j >= 0 && _wings[order[j]].angle > _wings[key].angle)
                {
                    order[j + 1] = order[j];
                    j--;
                }

                order[j + 1] = key;
            }

            int best = 0;
            float bestCost = float.MaxValue;

            for (int o = 0; o < n; o++)
            {
                float cost = 0f;

                for (int j = 0; j < n; j++)
                {
                    float slot = Mathf.Repeat((j + o) * step, 360f);

                    cost += Mathf.Abs(
                        Mathf.DeltaAngle(_wings[order[j]].angle, slot));
                }

                if (cost < bestCost)
                {
                    bestCost = cost;
                    best = o;
                }
            }

            for (int j = 0; j < n; j++)
                slots[order[j]] = Mathf.Repeat((j + best) * step, 360f);

            return slots;
        }

        // Beads on a hoop: push neighbours apart so they bunch without
        // stacking. Damped, or it buzzes.
        private void Repel(int n)
        {
            if (n < 2)
                return;

            float want = Mathf.Max(4f, _cfg.spread / Mathf.Max(1, n));

            for (int i = 0; i < n; i++)
            {
                for (int j = i + 1; j < n; j++)
                {
                    float d = Mathf.DeltaAngle(_wings[i].angle, _wings[j].angle);
                    float gap = Mathf.Abs(d);

                    if (gap >= want)
                        continue;

                    float push = (want - gap) * 0.25f * (d >= 0f ? 1f : -1f);

                    _wings[i].angle = Mathf.Repeat(_wings[i].angle - push, 360f);
                    _wings[j].angle = Mathf.Repeat(_wings[j].angle + push, 360f);
                }
            }
        }

        private void Put(Wing w, float degrees)
        {
            if (w.go == null)
                return;

            float r = degrees * Mathf.Deg2Rad;

            Vector2 at = (Vector2)transform.position +
                         new Vector2(Mathf.Cos(r), Mathf.Sin(r)) * _cfg.radius;

            w.target = at;

            // A PHYSICAL body is pulled here by the spring in FixedUpdate
            // rather than written here. Writing the transform of a dynamic
            // rigidbody every frame is what "gets knocked about by a blast"
            // cannot survive - the knock would be erased the same frame it
            // landed, and the mode would be indistinguishable from kinematic
            // while costing more.
            if (_cfg.IsDrone && _cfg.drone.physics)
                return;

            w.go.transform.position = at;
        }

        // ------------------------------------------------------------------
        // Dressing
        // ------------------------------------------------------------------

        private void TickDress()
        {
            for (int i = 0; i < _wings.Count; i++)
            {
                Wing w = _wings[i];

                if (w.go == null)
                    continue;

                // `w.sr` IS NULL ON A DRONE BODY and that is the whole of the
                // unit-tint compliance here: `Dress` guards both of its colour
                // writes on a non-null renderer, so a drone body gets the
                // scale half (pulse / spin) and none of the `sr.color` half.
                // Its colour is published once through ForgeUnitTint instead.
                //
                // The scale half really does breathe the hurtbox - a
                // CircleCollider2D scales with its transform - which is why
                // `pulse` DEFAULTS OFF on a drone body and the build log says
                // so. Turned on, what you see is what can be hit, which is the
                // honest behaviour and needs no extra machinery.
                ForgeWingmanArt.Dress(_cfg, w.go, w.sr, w.baseScale);

                Vector2 face = FaceFor(w);

                if (_cfg.rotate != ForgeWingman.Rotate.Off &&
                    face.sqrMagnitude > 0.000001f)
                {
                    // The game's own expression - so custom art must be drawn
                    // pointing RIGHT, exactly as every projectile sprite is.
                    w.go.transform.rotation =
                        Quaternion.FromToRotation(Vector2.right, face);
                }

                // ★ THE GUN AIMS WHERE THE SHOT GOES, whatever the body does.
                // A drone's gun is its `Head`, a `BarrelTransform` whose
                // LateUpdate writes its WORLD rotation from `Direction` -
                // which defaults to due east and is only ever set by the AI's
                // aim actions, the AI a wingman body switches off. So the head
                // sat pointing right while the shots went where you aimed
                // (R19 test 259). Set here in Update, before its LateUpdate.
                if (w.body != null)
                {
                    if (!w.headLooked)
                    {
                        w.headLooked = true;
                        w.head = w.body.GetComponentInChildren<BarrelTransform>(true);
                    }

                    if (w.head != null)
                    {
                        Vector2 aim = AimFor(w, w.go.transform.position);

                        if (aim.sqrMagnitude > 0.000001f)
                            w.head.Direction = aim;
                    }
                }
            }
        }

        private Vector2 FaceFor(Wing w)
        {
            switch (_cfg.rotate)
            {
                case ForgeWingman.Rotate.Cursor:
                {
                    if (_aimer == null || w.go == null)
                        return Vector2.zero;

                    Vector2 d = (Vector2)_aimer.TargetPosition -
                                (Vector2)w.go.transform.position;

                    return (d.sqrMagnitude > 0.000001f) ? d.normalized : Vector2.zero;
                }

                case ForgeWingman.Rotate.Travel:
                {
                    Vector2 d = (Vector2)w.go.transform.position -
                                (Vector2)transform.position;

                    // Tangent to the ring, which is what "the way it is
                    // sliding" means on a circle.
                    return new Vector2(-d.y, d.x).normalized;
                }

                case ForgeWingman.Rotate.Off:
                    return Vector2.zero;

                default:
                    // Where the next shot will actually go. Recomputed rather
                    // than remembered, so the nose is right before the first
                    // shot as well as after it.
                    return (w.go != null)
                        ? AimFor(w, w.go.transform.position)
                        : Vector2.zero;
            }
        }

        // ------------------------------------------------------------------
        // Beams
        // ------------------------------------------------------------------

        // AFTER motion, so the beam is drawn from where the wingman actually
        // ended up this frame rather than from where it was last frame.
        private void LateUpdate()
        {
            if (_cfg == null || _wings.Count == 0)
                return;

            for (int i = 0; i < _wings.Count; i++)
            {
                Wing w = _wings[i];

                if (w.go == null || w.lastFired == null)
                    continue;

                var beam = w.lastFired as HitscanWeapon;

                if (beam == null)
                    continue;

                try
                {
                    Vector2 pos = w.go.transform.position;
                    Vector2 dir = AimFor(w, pos);

                    if (dir.sqrMagnitude > 0.000001f)
                        beam.OnBarrelMoved(pos, dir);
                }
                catch
                {
                    // A beam that cannot draw must not stop the others moving.
                }
            }
        }
    }
}
