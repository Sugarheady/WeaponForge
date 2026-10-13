using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using Mode = WeaponForge.ForgeDroneModeRules.Mode;

namespace WeaponForge
{
    // DRONE MODES on an AI drone: the game's five drone gadgets and Forge
    // `drone` files (concept 5.2 - 5.4). Wingmen are placed by their own
    // controller and never come through here: this is attached only to a
    // minion whose gadget slot holds a `SpawnMinionModule`, and a wingman
    // gadget is a weapon-based module, so a wingman drone BODY - which is a
    // minion too, for saving and fast travel - is never mistaken for one
    // (concept 3, 11: "never steer a wingman drone body with the AI-drone
    // code").
    //
    // On NORMAL it does nothing at all: one lookup a physics step.
    //
    // ---- THE FORMATIONS: TAKE THE STEERING, KEEP THE GUN (5.2) -----------
    //
    // `MovementAction.Update` is the only thing steering a unit, and the
    // drone's gun (`ShootAction`, `AimAtTargetAction`) is a separate
    // component. So a formation switches off whatever movement action is
    // driving - every frame, because a state change enables a different one -
    // and the AI still sees, aims and shoots from wherever the formation puts
    // it. Near you, the game's own fire gate (it fights only when you and it
    // are both near the enemy) is naturally met. The Regroup / idle-orbit
    // action (`MoveAroundOwnerAction`, in both the Idle and Regroup states) is
    // off for as long as a formation is on, not just while off-spot - the
    // shudder `hold` found.
    //
    // ★ ONE DEPARTURE FROM THE CONCEPT, measured while building: the concept
    // proposed walking a loose drone with its own mover (`MoveTo`). The drone
    // carries one mover PER AI STATE and they differ ten-fold - the idle mover
    // pushes 30-60, the dodge 100-150, the regroup 250-350 - so a drone in
    // its idle state would crawl at about 4 units/second and never keep a
    // formation. LOOSE pushes the body itself instead, at the drone's OWN
    // regroup speed (read off its regroup mover), easing in and lagging a
    // boost. LOCKED sets its velocity every physics step and stays a normal
    // body that bumps walls and enemies (answer 4 + 5.2).
    //
    // ★ NOTHING MAY BE LEFT PARALYSED. Normal hands every action back; so do
    // disable, destroy and the error path.
    public class ForgeDroneBrain : MonoBehaviour
    {
        private static readonly ManualLogSource Log =
            ForgeLog.Source("WeaponForge.DroneModes");

        // Every unit uses discrete collision, so past ~50 units/second a body
        // can skip through 1-unit terrain in one step (5.2).
        public const float LockedCap = 45f;

        // A drone left ~87 units behind the camera is unloaded and DELETED -
        // so one this far from you in a formation is brought back beside you.
        public const float FarReset = 48f;

        private static readonly List<ForgeDroneBrain> _all = new List<ForgeDroneBrain>();

        private Unit _unit;
        private Rigidbody2D _body;
        private int _slot;
        private int _ownerId;
        private Unit _ship;
        private Rigidbody2D _shipBody;
        private float _shipLookAt;

        private MovementAction[] _actions;
        private UnitMovement[] _movers;
        private AIAgent _agent;

        private readonly List<MovementAction> _suspended = new List<MovementAction>();
        private readonly List<MovementAction> _followOff = new List<MovementAction>();

        private readonly List<Component> _leash = new List<Component>();
        private readonly List<float> _leashWas = new List<float>();
        private Vision _vision;
        private float _visionWas;
        private bool _visionRaised;

        private readonly List<Behaviour> _stances = new List<Behaviour>();

        private Mode _mode = Mode.Base;
        private bool _out;
        private float _nextToggle;

        private float _cruise = 20f;

        private GameObject _blocker;
        private DamagableResource _health;

        // ★ THE 2-SECOND REPORT (R21 310). "Base game drones still behave the
        // same regardless of which mode i set it too" - and the press lines
        // only ever said what the SLOT was set to, never what its drones did.
        // Two seconds into a mode, one line per slot says how far the drone is
        // from its spot and what it took from the AI, which separates "never
        // steered" from "steered and lost to the AI" from "working".
        private float _reportAt = -1f;
        private int _steerSteps;
        private float _spotDist = -1f;

        // ★ R21 PART 2: STILL NO "2 s into" LINE, IN THREE SESSIONS. The brain
        // attached (its line is there) and the slots changed mode (theirs are
        // there), and the report that only a mode change can arm never came -
        // so this brain never changed mode, or never ran. Neither can be told
        // from inside the brain, so the CENSUS (ForgeDroneModes, 2 s after
        // each press, from the ticker that is known to run) asks every drone
        // what it is doing, and these are what it reads.
        private int _thinks;
        private string _shipFrom;
        private float _noShipSince = -1f;

        public Vector2 Position
        {
            get { return _body != null ? _body.position : (Vector2)transform.position; }
        }

        // ------------------------------------------------------------------
        // Attaching
        // ------------------------------------------------------------------

        // From `Unit.SetOwner` (a new drone) and `SavableEntity.Bind` (one
        // coming back from a save, or streaming back in).
        public static void Attach(Unit minion)
        {
            if (minion == null)
                return;

            try
            {
                if (minion.GetComponent<ForgeDroneBrain>() != null)
                    return;

                int slot, ownerId;
                string why;

                if (!Eligible(minion, out slot, out ownerId, out why))
                {
                    // R21 310: the stock drones kept their own behaviour in
                    // every mode and nothing in the log could say whether this
                    // ever took them on. Now a refusal says why, once per kind.
                    if (why != null && _saidRefused.Add(minion.name + "|" + why))
                        Log.LogInfo("drone modes: '" + minion.name + "' will keep its own AI in every mode - " + why + ".");

                    return;
                }

                var b = minion.gameObject.AddComponent<ForgeDroneBrain>();
                b._unit = minion;
                b._slot = slot;
                b._ownerId = ownerId;
                b._body = minion.RigidBody != null
                    ? minion.RigidBody
                    : minion.GetComponentInChildren<Rigidbody2D>(true);

                // includeInactive is MANDATORY: StateMachine.Awake has already
                // switched every state child off by the time a spawn hook runs.
                b._actions = minion.GetComponentsInChildren<MovementAction>(true);
                b._movers = minion.GetComponentsInChildren<UnitMovement>(true);
                b._agent = minion.GetComponentInChildren<AIAgent>(true);
                b._cruise = Cruise(b);

                if (_saidAttach.Add(minion.name + "|" + slot))
                {
                    Log.LogInfo(
                        "drone modes: '" + minion.name + "' (gadget " + slot + ") takes the modes - " +
                        b._actions.Length + " movement action(s) it can be held off, " +
                        b._movers.Length + " mover(s), " +
                        (b._body != null ? "a body to steer" : "NO BODY TO STEER") + ", its own regroup " +
                        "speed " + b._cruise.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) +
                        " u/s. Said once per drone kind and slot.");
                }

                ForgeDroneModes.Kick();
            }
            catch (Exception e)
            {
                Log.LogWarning("drone modes: could not attach to '" + minion.name + "': " + e.Message);
            }
        }

        // A drone family unit (it has the come-back-to-you action), from a
        // gadget slot whose gadget is a drone spawner. `why` names a refusal
        // worth logging (null for the ordinary "not a gadget minion at all").
        private static bool Eligible(Unit minion, out int slot, out int ownerId, out string why)
        {
            slot = 0;
            ownerId = 0;
            why = null;

            Unit.Data data = minion.ComponentData;

            if (data == null || data.Owner == null)
                return false;

            slot = ForgeDroneModes.SlotOf(data.ConnectionToOwner);

            if (slot == 0)
                return false;

            ownerId = data.Owner.instanceId;

            ModuleGridOwner.Data grid;

            if (!data.Owner.TryGetComponent<ModuleGridOwner.Data>(out grid) || grid == null ||
                grid.ModuleGrid == null)
            {
                why = "its owner has no module grid to read the gadget from";
                return false;
            }

            IModuleCluster c = grid.ModuleGrid.GetCluster(
                slot == 1 ? ClusterType.Active1 : slot == 2 ? ClusterType.Active2 : ClusterType.Active3);

            if (c == null || !c.HasMainModule || !(c.MainModule is SpawnMinionModule))
                return false;   // a wingman body: its own controller places it

            if (!ForgeMinionMove.FollowsOwnerItself(minion))
            {
                why = "it has no come-back-to-you movement (MoveAroundOwnerAction), so it is not a drone";
                return false;
            }

            return true;
        }

        // Once-only log gates, swept at run entry by ForgeDiagnosticGates
        // (the `_said` naming convention).
        private static readonly HashSet<string> _saidRefused = new HashSet<string>();
        private static readonly HashSet<string> _saidAttach = new HashSet<string>();
        private static readonly Dictionary<int, float> _saidSteer = new Dictionary<int, float>();
        private static readonly HashSet<int> _saidNoShip = new HashSet<int>();

        // The drone's own regroup speed, which is what LOOSE flies at. A push
        // is `AddForce(dir * pushForce * mass)` for one physics step, every
        // `pushDelay` - so it averages `pushForce * dt / delay` of
        // acceleration, against the body's damping.
        private static float Cruise(ForgeDroneBrain b)
        {
            float best = 0f;

            try
            {
                Type follow = ForgeMinionMove.OwnerFollowType;
                float damping = b._body != null ? Mathf.Max(0.5f, b._body.linearDamping) : 3f;

                foreach (MovementAction a in b._actions)
                {
                    if (a == null || follow == null || !follow.IsInstanceOfType(a))
                        continue;

                    var push = a.movement as PushMovement;

                    if (push == null)
                        continue;

                    var v = (PushMovement.ValueOverride)push.GetCurrentValues();
                    float force = (v.pushForce.Min + v.pushForce.Max) * 0.5f;
                    float delay = Mathf.Max(0.02f, (v.pushDelay.Min + v.pushDelay.Max) * 0.5f);
                    float speed = force * Time.fixedDeltaTime / delay / damping;

                    best = Mathf.Max(best, speed);
                }
            }
            catch (Exception)
            {
            }

            return best > 0f ? Mathf.Clamp(best, 8f, 40f) : 20f;
        }

        // Every drone of this ship's gadget slot `slot`, in a stable order.
        public static void ForSlot(Unit ship, int slot, Action<ForgeDroneBrain> act)
        {
            int id = ForgeWingmanSave.IdOf(ship);

            if (id == 0)
                return;

            for (int i = _all.Count - 1; i >= 0; i--)
                if (_all[i] == null)
                    _all.RemoveAt(i);

            for (int i = 0; i < _all.Count; i++)
            {
                ForgeDroneBrain b = _all[i];

                if (b._ownerId != id || b._slot != slot || !b.isActiveAndEnabled || b._body == null)
                    continue;

                act(b);
            }
        }

        private void OnEnable()
        {
            if (!_all.Contains(this))
            {
                // Stable formation order: by instance id.
                int at = 0;

                while (at < _all.Count && _all[at] != null &&
                       _all[at].GetInstanceID() < GetInstanceID())
                {
                    at++;
                }

                _all.Insert(at, this);
            }
        }

        // ------------------------------------------------------------------
        // Every physics step
        // ------------------------------------------------------------------

        private Unit Ship()
        {
            if (_ship != null)
                return _ship;

            if (Time.time < _shipLookAt)
                return null;

            _shipLookAt = Time.time + 0.5f;

            try
            {
                // ★ THE SAME SHIPS THE MODE KEY READS, FIRST. Every mode is
                // looked up by the ship's entity id, and the key finds its
                // ships through their ShipInput - so a brain that asks the
                // same list cannot disagree with the key about who its ship
                // is. The entity map was the only route before, and nothing
                // ever said whether it answered: `UnloadEntity` drops an entry
                // from it whenever its entity moves into a segment that is not
                // loaded yet, and only then asks whether the object may unload
                // (a ship may not, so it stays alive and unlisted).
                foreach (Unit u in ForgeKeyBinding.Ships())
                {
                    if (u != null && ForgeWingmanSave.IdOf(u) == _ownerId)
                    {
                        _ship = u;
                        _shipFrom = "the ships the mode key reads";
                        break;
                    }
                }

                EntityGameObjectManager m;
                SavableEntity se;

                if (_ship == null && ServiceLocator.TryGet<EntityGameObjectManager>(out m) && m != null &&
                    m.TryGetSavableEntity(_ownerId, out se) && se != null)
                {
                    _ship = se.GetComponent<Unit>() ?? se.GetComponentInChildren<Unit>(true);
                    _shipFrom = "the game's entity map";
                }

                _shipBody = _ship != null ? _ship.RigidBody : null;
            }
            catch (Exception)
            {
            }

            if (_ship != null)
            {
                _noShipSince = -1f;
            }
            else if (_noShipSince < 0f)
            {
                _noShipSince = Time.time;
            }
            else if (Time.time - _noShipSince > 3f && _saidNoShip.Add(_ownerId * 4 + _slot))
            {
                // Without its ship a drone can only ever be on NORMAL.
                Log.LogWarning(
                    "drone modes: gadget " + _slot + "'s '" + (_unit != null ? _unit.name : "?") +
                    "' cannot find the ship it belongs to (its owner is entity " + _ownerId +
                    ", and neither the ships the mode key reads nor the game's entity map has it) - " +
                    "it keeps its own AI in every mode until it does. Said once per gadget.");
            }

            return _ship;
        }

        // For the census: what this drone is doing, in a few words.
        internal string Describe(Mode slotSays)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;

            return (isActiveAndEnabled ? "" : "BRAIN SWITCHED OFF, ") +
                   "acting " + ForgeDroneModeRules.Word(_mode) +
                   (slotSays != _mode ? " while its gadget says " + ForgeDroneModeRules.Word(slotSays) : "") +
                   ", " + _thinks.ToString(inv) + " physics step(s) run, ship " +
                   (_ship != null ? "found (" + _shipFrom + ")" : "NOT FOUND (owner entity " + _ownerId + ")") +
                   (Steering ? ", steered " + _steerSteps.ToString(inv) + " step(s)" +
                               (_spotDist >= 0f ? ", " + _spotDist.ToString("0.0", inv) + " from its spot" : "")
                             : "");
        }

        // For the census: why a drone of yours has no brain.
        internal static string WhyNot(Unit minion)
        {
            try
            {
                if (minion == null)
                    return "no unit";

                Unit.Data data = minion.ComponentData;

                if (data == null || data.Owner == null)
                    return "it has no owner";

                if (ForgeDroneModes.SlotOf(data.ConnectionToOwner) == 0)
                    return "it is not tied to a gadget slot (its link to you is " + data.ConnectionToOwner + ")";

                int slot, ownerId;
                string why;

                if (Eligible(minion, out slot, out ownerId, out why))
                    return "it could take the modes, but neither attach hook (a new drone, a drone coming back) saw it";

                return why ?? "its gadget is not a drone spawner (a wingman body - its gadget places it)";
            }
            catch (Exception e)
            {
                return "could not tell (" + e.GetType().Name + ")";
            }
        }

        private void FixedUpdate()
        {
            _thinks++;

            if (_body == null || _unit == null)
                return;

            try
            {
                Think();
            }
            catch (Exception e)
            {
                Log.LogError("drone modes: a drone's mode failed and it was handed back to its AI: " + e);

                HandBack();
                enabled = false;
            }
        }

        // A state change enables a different movement action, and its Update
        // could steer once before the next physics step - so the suspension is
        // repeated here too while this drone is being steered.
        private void Update()
        {
            if (_body == null || !Steering)
                return;

            try
            {
                ForgeSuspend.Suspend(_actions, _suspended);
            }
            catch (Exception)
            {
            }
        }

        private bool Steering
        {
            get
            {
                if (_mode == Mode.Sentry)
                    return _out;

                if (_mode == Mode.Seeker)
                    return _seekSteer;

                return ForgeDroneModeRules.IsPlaced(_mode);
            }
        }

        private void Think()
        {
            Unit ship = Ship();

            Mode want = ship != null ? ForgeDroneModes.Effective(ship, _slot) : Mode.Base;

            if (want != _mode)
                Switch(want);

            if (_reportAt > 0f && Time.time >= _reportAt)
                Report();

            if (_mode == Mode.Base || ship == null)
                return;

            if (ForgeDroneModeRules.IsPlaced(_mode))
            {
                Formation(ship);
                return;
            }

            if (_mode == Mode.Sentry)
                Sentry(ship);

            if (_mode == Mode.Seeker)
                Seek(ship);
        }

        private void Report()
        {
            _reportAt = -1f;

            try
            {
                if (_mode == Mode.Base || _unit == null)
                    return;

                // One line per slot per press, from whichever of its drones
                // gets there first.
                int key = _ownerId * 4 + _slot;
                float last;

                if (_saidSteer.TryGetValue(key, out last) && Time.time - last < 1.5f)
                    return;

                _saidSteer[key] = Time.time;

                var inv = System.Globalization.CultureInfo.InvariantCulture;
                string what;

                if (_mode == Mode.Seeker)
                {
                    what = (_hunt != null
                               ? "HUNTING '" + _hunt.name + "' " +
                                 ((Vector2)_hunt.transform.position - Position).magnitude.ToString("0", inv) +
                                 " units away"
                               : AiBusy() ? "FIGHTING what it can see"
                               : "WANDERING round you (nothing within the seek range)") +
                           ", its leash raised on " + _leash.Count + " range check(s)" +
                           (_visionRaised && _vision != null ? ", sight " + _vision.Range.ToString("0", inv) : "");
                }
                else if (_mode == Mode.Sentry)
                {
                    what = (_out ? "being walked back to its post" : "inside its post's radius, on its own AI") +
                           ", leash raised on " + _leash.Count + " range check(s)";
                }
                else
                {
                    bool locked = FliesLocked(_mode);

                    what = (_spotDist >= 0f
                               ? _spotDist.ToString("0.0", inv) + " units from its spot"
                               : "NOT IN THE FORMATION (no spot was found for it)") +
                           ", steered for " + _steerSteps + " physics steps, " +
                           new HashSet<MovementAction>(_suspended).Count + " AI movement action(s) held off and " +
                           _followOff.Count + " come-back action(s) off, flying " +
                           (locked ? "LOCKED" : "LOOSE at " + _cruise.ToString("0.#", inv) + " u/s");
                }

                Log.LogInfo(
                    "drone modes: 2 s into " + ForgeDroneModeRules.Word(_mode) + ", gadget " + _slot +
                    "'s '" + _unit.name + "' is " + what +
                    (_forgot ? " (it dropped its target and grudges on the change)" : "") + ".");
            }
            catch (Exception)
            {
                // A report must never cost the mode.
            }
        }

        // ------------------------------------------------------------------
        // Entering and leaving a mode
        // ------------------------------------------------------------------

        private static bool Steers(Mode m)
        {
            return ForgeDroneModeRules.IsPlaced(m) || m == Mode.Sentry;
        }

        // INTERCEPTOR (all three) and the two dive modes always fly LOCKED - a
        // loose drone at its own speed misses fast shots and never reaches
        // the enemy it was sent at. The rest follow [Drone modes] Formation.
        private static bool FliesLocked(Mode m)
        {
            return ForgeDroneModeRules.IsGuard(m) || ForgeDroneModeRules.IsStrike(m) ||
                   ForgeDroneModes.Locked;
        }

        private void Switch(Mode to)
        {
            Mode from = _mode;

            if (Steers(from))
            {
                ForgeSuspend.Release(_suspended);
                ResumeFollow();
                _out = false;
                _path = null;
            }

            // SEEKER steers only while it is hunting or wandering, so it is not
            // in Steers - but whatever it held off goes back the same way.
            if (from == Mode.Seeker)
            {
                ForgeSuspend.Release(_suspended);
                _seekSteer = false;
                _hunt = null;
                _path = null;
            }

            if (from == Mode.Seeker || from == Mode.Sentry)
                RestoreLeash();

            if (ForgeDroneModeRules.IsGuard(from))
                Unblock();

            if (to == Mode.Base)
                ResumeStances();
            else
                PauseStances();

            if (Steers(to))
                SuppressFollow();

            if (to == Mode.Seeker)
            {
                ForgeDroneModeRules.Shape sh = ForgeDroneModes.ShapeOf(Ship(), _slot);
                RaiseLeash(sh.seekRange > 0f ? sh.seekRange : ForgeDroneModes.SeekRange, true);
            }

            if (to == Mode.Sentry)
            {
                // ★ THE LEASH GOES UP OR A SENTRY STOPS FIGHTING THE MOMENT
                // YOU LEAVE (5.4). The leash measures the drone's distance to
                // YOU; past it the AI switches to its come-back state, which is
                // the "only fights when you are both near" gate - and the
                // come-back movement is off, so it would just sit. He agreed to
                // loosening the gate for SENTRY and SEEKER only.
                RaiseLeash(ForgeDroneModes.SentryRecall + SentryRadiusNow() + 10f, false);
            }

            if (ForgeDroneModeRules.IsGuard(to) && ForgeDroneModes.InterceptorNoDamage)
                Block();

            _forgot = ForgetTarget();

            _mode = to;

            _steerSteps = 0;
            _spotDist = -1f;
            _reportAt = to == Mode.Base ? -1f : Time.time + 2f;
        }

        // ★ HIS R21 PART 1 NOTE, answered "on by default": "would it be
        // possible to reset their revenge or aggro between mode changes? I
        // think they fixate on a target and dont loose it between modes." Two
        // things hold a drone on one enemy, and both go: its target
        // (`AIAgent.Update` keeps a target for as long as it is visible) and
        // its grudge list (`enemyBlackList` - being hit blacklists the
        // attacker, and the AI reads that before faction, see the grudge
        // notes in CLAUDE.md). The AI then picks again from what it sees from
        // its new place. The list is save-persisted; clearing it is safe
        // because nothing of ours writes to it on a player's drone.
        // [Drone modes] ForgetTargetOnChange = false keeps both.
        private bool _forgot;

        private bool ForgetTarget()
        {
            if (!ForgeDroneModes.ForgetTargetOnChange || _agent == null)
                return false;

            try
            {
                _agent.ForgetTarget();

                AIAgent.Data data = _agent.ComponentData;

                if (data != null && data.enemyBlackList != null)
                    data.enemyBlackList.Clear();

                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // Back to its own AI entirely. Normal, disable, destroy, error.
        private void HandBack()
        {
            try
            {
                ForgeSuspend.Release(_suspended);
                ResumeFollow();
                RestoreLeash();
                Unblock();
                ResumeStances();
            }
            catch (Exception)
            {
            }

            _mode = Mode.Base;
            _out = false;
            _path = null;
            _seekSteer = false;
            _hunt = null;
        }

        private void OnDisable()
        {
            HandBack();
        }

        private void OnDestroy()
        {
            HandBack();
            _all.Remove(this);
        }

        // The Regroup and idle-orbit movement (MoveAroundOwnerAction) - the
        // exact opposite of keeping a formation or a post.
        private void SuppressFollow()
        {
            Type follow = ForgeMinionMove.OwnerFollowType;

            if (follow == null || _actions == null)
                return;

            foreach (MovementAction a in _actions)
            {
                if (a == null || !a.enabled || !follow.IsInstanceOfType(a))
                    continue;

                a.enabled = false;
                _followOff.Add(a);
            }
        }

        private void ResumeFollow()
        {
            for (int i = 0; i < _followOff.Count; i++)
                if (_followOff[i] != null)
                    _followOff[i].enabled = true;

            _followOff.Clear();
        }

        // ★ A FORGE `drone` FILE'S OWN `follow` AND `hold` ARE ITS NORMAL
        // (12.2 fills): a mode pauses them and normal gives them back, so a
        // `hold` drone goes back to its own post.
        private void PauseStances()
        {
            Pause(GetComponent<ForgeMinionHold>());
            Pause(GetComponent<ForgeMinionEscort>());
        }

        private void Pause(Behaviour b)
        {
            if (b == null || !b.enabled || _stances.Contains(b))
                return;

            b.enabled = false;
            _stances.Add(b);
        }

        private void ResumeStances()
        {
            for (int i = 0; i < _stances.Count; i++)
                if (_stances[i] != null)
                    _stances[i].enabled = true;

            _stances.Clear();
        }

        // ★ SEEKER IS A LONGER LEASH (5.3). The AI already hunts; what keeps
        // it near you is `OwnerIsWithinRangeCondition`, whose `maxDistance`
        // (15 idle, 20 in a fight) is read fresh on every check. So the leash
        // goes up while the mode is on - the fighting leash to the seek range,
        // the others in proportion - and back down after. A condition's range
        // is not a mover value, so the snapshot rule does not apply to it. The
        // sight goes up with it, or a drone allowed to go 40 units out would
        // still only see what is next to it.
        private static FieldInfo _maxDistance;
        private static bool _lookedLeash;

        private void RaiseLeash(float want, bool sight)
        {
            RestoreLeash();

            if (!_lookedLeash)
            {
                _lookedLeash = true;

                Type t = AccessTools.TypeByName("OwnerIsWithinRangeCondition");
                _maxDistance = t != null ? AccessTools.Field(t, "maxDistance") : null;
            }

            if (_maxDistance != null)
            {
                float longest = 0f;

                foreach (Component c in GetComponentsInChildren(_maxDistance.DeclaringType, true))
                {
                    if (c == null)
                        continue;

                    float was = (float)_maxDistance.GetValue(c);

                    _leash.Add(c);
                    _leashWas.Add(was);
                    longest = Mathf.Max(longest, was);
                }

                float k = longest > 0f ? Mathf.Max(1f, want / longest) : 1f;

                for (int i = 0; i < _leash.Count; i++)
                    _maxDistance.SetValue(_leash[i], _leashWas[i] * k);
            }

            if (sight)
            {
                _vision = GetComponentInChildren<Vision>(true);

                if (_vision != null)
                {
                    _visionWas = _vision.Range;
                    _vision.Range = Mathf.Max(_visionWas, want);
                    _visionRaised = true;
                }
            }
        }

        private void RestoreLeash()
        {
            if (_maxDistance != null)
            {
                for (int i = 0; i < _leash.Count; i++)
                {
                    if (_leash[i] != null)
                        _maxDistance.SetValue(_leash[i], _leashWas[i]);
                }
            }

            _leash.Clear();
            _leashWas.Clear();

            if (_visionRaised && _vision != null)
                _vision.Range = _visionWas;

            _visionRaised = false;
        }

        // INTERCEPTOR's test option (answer C, 4.1): the shot still hits the
        // drone and still dies there, and the drone loses nothing - the game's
        // own invincibility hook, `DamagableResource.damageBlockers`.
        private void Block()
        {
            if (_blocker != null)
                return;

            _health = GetComponentInChildren<DamagableResource>(true);

            if (_health == null)
                return;

            _blocker = new GameObject("ForgeInterceptorTest");
            _blocker.transform.SetParent(transform, false);

            if (_health.damageBlockers == null)
                _health.damageBlockers = new List<GameObject>();

            _health.damageBlockers.Add(_blocker);
        }

        private void Unblock()
        {
            if (_blocker == null)
                return;

            if (_health != null && _health.damageBlockers != null)
                _health.damageBlockers.Remove(_blocker);

            Destroy(_blocker);
            _blocker = null;
        }

        // ------------------------------------------------------------------
        // Steering
        // ------------------------------------------------------------------

        private void Suspend()
        {
            ForgeSuspend.Suspend(_actions, _suspended);

            if (_movers == null)
                return;

            for (int i = 0; i < _movers.Length; i++)
            {
                if (_movers[i] != null && _movers[i].isActiveAndEnabled)
                    _movers[i].Stop();
            }
        }

        private Vector2 ShipVelocity()
        {
            return _shipBody != null ? _shipBody.linearVelocity : Vector2.zero;
        }

        private void Formation(Unit ship)
        {
            Vector2 shipPos = ship.transform.position;
            Vector2 spot;

            Suspend();

            // Not in this frame's formation yet (it joined after the roster
            // was taken): hold still for a step rather than fly at the ship.
            if (!ForgeDroneModes.SpotFor(ship, _mode, this, 0, out spot))
                return;

            spot = ForgeDroneModes.ClearOfRock(spot, shipPos);

            Vector2 pos = _body.position;

            _spotDist = (pos - spot).magnitude;
            _steerSteps++;

            if ((pos - shipPos).sqrMagnitude > FarReset * FarReset)
            {
                _body.position = spot;
                _body.linearVelocity = ShipVelocity();
                transform.position = spot;
                _path = null;

                ForgeDroneModes.SayFar(_unit.name);
                return;
            }

            // ★ INTERCEPTOR ALWAYS FLIES LOCKED (answer C): a loose drone at
            // its own speed misses fast shots. So do the dive modes.
            bool locked = FliesLocked(_mode);

            // The interceptSpeed slider pulls a shot-guarding drone onto its
            // spot harder (still under LockedCap, which is a terrain limit).
            float gain = ForgeDroneModeRules.IsGuard(_mode)
                ? ForgeDroneModes.TuningOf(ship, _slot).reachScale
                : 1f;

            // STRIKER / VENGEANCE: point its gun at the enemy it was sent to.
            // The AI would pick whatever it sees first; this is the one it is
            // sitting next to.
            if (ForgeDroneModeRules.IsStrike(_mode) && _agent != null)
            {
                Unit target;

                if (ForgeDroneModes.StrikeTargetFor(ship, _mode, this, 0, out target) &&
                    target != null && _agent.Target != target)
                {
                    _agent.SetTarget(target);
                }
            }

            Steer(Route(pos, spot), locked, ShipVelocity(), gain);
        }

        // ------------------------------------------------------------------
        // SEEKER - hunt + wander (his answer, R21 part 3, 312)
        // ------------------------------------------------------------------
        //
        // ★ R21 312: "they sort of sit in a spot unless the player sees it
        // first", and 310: "if i fly ahead it doesnt really notice things until
        // they're within 10/6 units away from me. Maybe it needs to wander a
        // bit?" SEEKER used to be only a longer leash and longer sight - the
        // AI still waited for an enemy to come into its own view. Now:
        //
        //   * FIGHTING: while the AI has a target it can see, this does nothing
        //     at all - the AI chases and shoots as it always did;
        //   * HUNTING: otherwise it flies at the nearest enemy within the seek
        //     range of YOU, seen or not (the game's own pathfinder round rock),
        //     until the AI sees it and takes over;
        //   * WANDERING: with nothing in range, it sweeps a slow loop round you
        //     at about half the seek range.
        //
        // It only ever hunts what is within the seek range of your ship, and
        // the seek range is clamped under the edge of the loaded world, so a
        // seeker never chases something into the part of the map the game has
        // unloaded (where it would be deleted).
        private bool _seekSteer;
        private Unit _hunt;
        private float _huntAt;

        private static readonly List<Unit> _found = new List<Unit>();

        private bool AiBusy()
        {
            try
            {
                return _agent != null && _agent.HasTarget && _agent.IsTargetVisible;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private void Seek(Unit ship)
        {
            if (AiBusy())
            {
                if (_seekSteer)
                {
                    ForgeSuspend.Release(_suspended);
                    _seekSteer = false;
                    _path = null;
                }

                _hunt = null;
                return;
            }

            ForgeDroneModeRules.Shape sh = ForgeDroneModes.ShapeOf(ship, _slot);
            float range = sh.seekRange > 0f ? sh.seekRange : ForgeDroneModes.SeekRange;
            Vector2 shipPos = ship.transform.position;

            if (Time.time >= _huntAt || (_hunt != null && !ForgeDroneModes.Alive(_hunt)))
            {
                _huntAt = Time.time + 0.4f;
                _hunt = null;

                // Every enemy within the seek range of YOU; this drone takes
                // the one nearest to ITSELF.
                ForgeDroneModes.Enemies(ship, shipPos, range, false, _found);

                float best = float.MaxValue;

                foreach (Unit u in _found)
                {
                    float d = ((Vector2)u.transform.position - Position).sqrMagnitude;

                    if (d < best)
                    {
                        best = d;
                        _hunt = u;
                    }
                }

                _found.Clear();
            }

            Vector2 goal;

            if (_hunt != null)
            {
                goal = _hunt.transform.position;
            }
            else
            {
                // Each drone of the slot on its own point of the loop.
                int i = 0, n = 0;

                ForSlot(ship, _slot, b =>
                {
                    if (ReferenceEquals(b, this))
                        i = n;

                    n++;
                });

                var w = ForgeDroneModeRules.WanderPoint(
                    new ForgeDroneModeRules.P(shipPos.x, shipPos.y), range, Time.time, i, Mathf.Max(1, n));

                goal = ForgeDroneModes.ClearOfRock(new Vector2(w.x, w.y), shipPos);
            }

            Suspend();
            _seekSteer = true;
            _steerSteps++;
            _spotDist = (Position - goal).magnitude;

            Steer(Route(Position, goal), false, Vector2.zero);
        }

        private float SentryRadiusNow()
        {
            ForgeDroneModeRules.Shape sh = ForgeDroneModes.ShapeOf(Ship(), _slot);
            return sh.sentryRadius > 0f ? sh.sentryRadius : ForgeDroneModes.SentryRadius;
        }

        // SENTRY is `hold` pointed at the slot's post (5.4): inside the radius
        // the drone does whatever its AI wants - wanders, chases, shoots -
        // and outside it is walked back. Released at 75% of the radius, not
        // at the rim, and not more often than every 0.35s, because each
        // re-enable of a movement action costs a pathfinder request.
        private void Sentry(Unit ship)
        {
            Vector2 post;

            if (!ForgeDroneModes.PostOf(ship, _slot, out post))
                return;

            float radius = SentryRadiusNow();
            float release = Mathf.Max(radius * 0.75f, radius - 2f);

            Vector2 pos = _body.position;
            float d = (pos - post).magnitude;

            if (_out)
            {
                if (d <= release)
                {
                    _out = false;
                    _nextToggle = Time.time + 0.35f;
                    _path = null;
                    ForgeSuspend.Release(_suspended);
                    return;
                }

                Suspend();
                Steer(Route(pos, ForgeDroneModes.ClearOfRock(post, post)), false, Vector2.zero);
                return;
            }

            if (d > radius && Time.time >= _nextToggle)
            {
                _out = true;
                _nextToggle = Time.time + 0.35f;
                Suspend();
                Steer(Route(pos, ForgeDroneModes.ClearOfRock(post, post)), false, Vector2.zero);
            }
        }

        // LOOSE: a push toward the spot at the drone's own regroup speed,
        // carrying most of the ship's own motion so a formation keeps its shape
        // while you cruise - and capped, so a boost leaves it behind for a
        // moment and it catches up. LOCKED: the velocity is set outright, the
        // way a wingman holds its place, capped below the speed that tunnels.
        private void Steer(Vector2 target, bool locked, Vector2 shipVel, float gain = 1f)
        {
            Vector2 pos = _body.position;
            Vector2 to = target - pos;
            float d = to.magnitude;

            if (locked)
            {
                Vector2 v = shipVel + to * (8f * Mathf.Max(0.5f, gain));

                if (v.magnitude > LockedCap)
                    v = v.normalized * LockedCap;

                _body.linearVelocity = v;
                return;
            }

            Vector2 want = shipVel * 0.85f;

            if (d > 0.15f)
                want += to / d * Mathf.Min(_cruise, d * 3f);

            if (want.magnitude > _cruise)
                want = want.normalized * _cruise;

            const float response = 5f;

            Vector2 force = ((want - _body.linearVelocity) * response +
                             want * _body.linearDamping) * _body.mass;

            _body.AddForce(force);
        }

        // ------------------------------------------------------------------
        // Rock in the way: the game's own pathfinder (12.2 K)
        // ------------------------------------------------------------------
        //
        // ★ A STRAIGHT LINE INTO A WALL IS THE ONE THING `hold` NEVER HAD TO
        // HANDLE. Its walk is a straight `MoveTo`; the game's movers path round
        // terrain (`MovementAction.OnEnable` -> `FindPath`). So while there is
        // rock between the drone and its spot, it asks the drone's own A*
        // `Seeker` for a path and follows the waypoints, and goes straight
        // again once the line is clear. Re-checked every 0.25s and re-pathed
        // every 0.6s at most - the hysteresis 5.2 asks for.
        //
        // Reached by REFLECTION: the Seeker type lives in the game's
        // pathfinding assembly, which this mod does not reference. Any failure
        // latches the pathing off for the session, says so once, and the drone
        // steers straight - which is what it would do without this.
        private List<Vector3> _path;
        private int _waypoint;
        private float _pathAt;
        private float _wallAt;
        private bool _walled;

        private static PropertyInfo _seekerProp;
        private static MethodInfo _startPath;
        private static Type _onPathType;
        private static FieldInfo _vectorPath;
        private static PropertyInfo _errorProp;
        private static FieldInfo _errorField;
        private static bool _pathLooked;
        private static bool _pathBroken;

        private Vector2 Route(Vector2 pos, Vector2 spot)
        {
            if (Time.time >= _wallAt)
            {
                _wallAt = Time.time + 0.25f;
                _walled = ForgeDroneModes.Walled(pos, spot);

                if (!_walled)
                    _path = null;
            }

            if (!_walled || _pathBroken)
                return spot;

            if (Time.time >= _pathAt)
            {
                _pathAt = Time.time + 0.6f;
                AskPath(pos, spot);
            }

            if (_path == null || _path.Count == 0)
                return spot;

            while (_waypoint < _path.Count - 1 &&
                   ((Vector2)_path[_waypoint] - pos).sqrMagnitude < 1.2f * 1.2f)
            {
                _waypoint++;
            }

            return _waypoint < _path.Count ? (Vector2)_path[_waypoint] : spot;
        }

        private void AskPath(Vector2 from, Vector2 to)
        {
            if (_agent == null || _pathBroken)
                return;

            try
            {
                if (!_pathLooked)
                {
                    _pathLooked = true;

                    _seekerProp = AccessTools.Property(typeof(AIAgent), "Seeker");
                    Type seeker = _seekerProp != null ? _seekerProp.PropertyType : null;

                    if (seeker != null)
                    {
                        foreach (MethodInfo m in seeker.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                        {
                            ParameterInfo[] ps = m.GetParameters();

                            if (m.Name == "StartPath" && ps.Length == 3 &&
                                ps[0].ParameterType == typeof(Vector3) &&
                                ps[1].ParameterType == typeof(Vector3) &&
                                typeof(Delegate).IsAssignableFrom(ps[2].ParameterType))
                            {
                                _startPath = m;
                                _onPathType = ps[2].ParameterType;
                                break;
                            }
                        }
                    }

                    if (_startPath == null)
                    {
                        _pathBroken = true;
                        Log.LogWarning(
                            "drone modes: the game's pathfinder could not be found, so a drone " +
                            "walled off from its spot steers straight at it this session.");
                        return;
                    }
                }

                object seekerObj = _seekerProp.GetValue(_agent, null);

                if (seekerObj == null)
                    return;

                Delegate cb = Delegate.CreateDelegate(
                    _onPathType, this,
                    typeof(ForgeDroneBrain).GetMethod(
                        "OnPath", BindingFlags.NonPublic | BindingFlags.Instance));

                _startPath.Invoke(seekerObj, new object[] { (Vector3)from, (Vector3)to, cb });
            }
            catch (Exception e)
            {
                _pathBroken = true;

                Log.LogWarning(
                    "drone modes: asking the game's pathfinder failed (" + e.GetType().Name + ": " +
                    e.Message + ") - walled-off drones steer straight this session.");
            }
        }

        // The pathfinder's answer. Bound to its own delegate type by
        // parameter contravariance, so it takes the path as an object.
        private void OnPath(object p)
        {
            if (this == null || p == null)
                return;

            try
            {
                Type t = p.GetType();

                if (_vectorPath == null)
                {
                    _vectorPath = AccessTools.Field(t, "vectorPath");
                    _errorProp = AccessTools.Property(t, "error");

                    if (_errorProp == null)
                        _errorField = AccessTools.Field(t, "error");
                }

                bool error = _errorProp != null
                    ? (bool)_errorProp.GetValue(p, null)
                    : _errorField != null && (bool)_errorField.GetValue(p);

                if (error || _vectorPath == null)
                {
                    _path = null;
                    return;
                }

                var list = _vectorPath.GetValue(p) as List<Vector3>;

                _path = list != null ? new List<Vector3>(list) : null;
                _waypoint = _path != null && _path.Count > 1 ? 1 : 0;
            }
            catch (Exception)
            {
                _path = null;
            }
        }
    }
}
