using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using BepInEx.Logging;
using UnityEngine;
using Mode = WeaponForge.ForgeDroneModeRules.Mode;
using Kind = WeaponForge.ForgeDroneModeRules.Kind;
using Shape = WeaponForge.ForgeDroneModeRules.Shape;
using P = WeaponForge.ForgeDroneModeRules.P;

namespace WeaponForge
{
    // DRONE MODES - a key that changes what your companions are doing, like
    // in a shoot-em-up. The design, and every decision behind it, is
    // `DRONE MODES - CONCEPT.md` (Sugarheady, 2026-09-20 .. 2026-10-01).
    //
    // This class holds the RUN'S STATE and answers the questions the moving
    // parts ask:
    //
    //   * which mode each GADGET SLOT of each ship is in (a mode belongs to a
    //     slot, because Y + 1 / 2 / 3 can split them - concept 2);
    //   * what that slot holds (ForgeDroneModeRules.Kind), and so which modes
    //     it can do;
    //   * where a companion goes in a formation - every companion of a ship in
    //     the same mode is ONE formation, wingmen and AI drones together, so
    //     two slots on ORBIT make one ring rather than two rings drawn on top
    //     of each other (the R19/R20 test 267 trap, 12.1 gaps);
    //   * the alert, the sentry recall, the save line and the New Game Plus
    //     carry.
    //
    // It never moves anything itself. Wingmen are placed by their own
    // controller (an overlay on what it already does, 5.1) and AI drones by
    // ForgeDroneBrain (5.2). The arithmetic lives in ForgeDroneModeRules,
    // with no Unity in it, so the harness runs it.
    //
    // ★ THE ONE RULE (5.1): a mode OVERRIDES what a controller reads; it
    // never writes a wingman's Config. A Config is shared by every ship
    // carrying that file - writing it is how `contactDamage`'s world modes
    // once armed the whole game for a session.
    public static class ForgeDroneModes
    {
        private static readonly ManualLogSource Log =
            ForgeLog.Source("WeaponForge.DroneModes");

        // ------------------------------------------------------------------
        // Settings - `[Drone modes]` in this mod's .cfg, read once at launch
        // ------------------------------------------------------------------

        public static ForgeKeyBinding ModeKey;
        public static ForgeKeyBinding NormalKey;
        public static readonly ForgeKeyBinding[] SlotKeys = new ForgeKeyBinding[3];

        public static List<Mode> Cycle = new List<Mode>(ForgeDroneModeRules.DefaultCycle);
        public static bool HoldForNormal = true;
        public static bool Locked;
        public static float SeekRange = 40f;
        public static float SentryRadius = 8f;
        public static float SentryRecall = 60f;
        public static float AlertSeconds = 2.5f;
        public static float OrbitSpin = 60f;
        public static bool InterceptorNoDamage;

        public static Shape Defaults = new Shape();

        // The feature is on while the mode key has something bound. "Key =
        // none" with "PadButton = none" switches it off, and the gadget
        // buttons then work exactly as they always did (12.2 fills).
        public static bool KeyOn
        {
            get { return ModeKey != null && ModeKey.IsSet; }
        }

        public static void Configure(
            string key, string pad, string slot1, string slot2, string slot3,
            string normalKey, bool holdForNormal, string cycle, string formation,
            float seekRange, float sentryRadius, float sentryRecall, float alertSeconds,
            bool interceptorNoDamage, Shape shapes, float orbitSpin)
        {
            ModeKey = ForgeKeyBinding.Parse("Drone modes", "PadButton", key, pad);
            NormalKey = ForgeKeyBinding.Parse("Drone modes", "NormalKey", normalKey, null);
            SlotKeys[0] = ForgeKeyBinding.Parse("Drone modes", "Slot1", slot1, null);
            SlotKeys[1] = ForgeKeyBinding.Parse("Drone modes", "Slot2", slot2, null);
            SlotKeys[2] = ForgeKeyBinding.Parse("Drone modes", "Slot3", slot3, null);

            HoldForNormal = holdForNormal;

            var problems = new List<string>();
            Cycle = ForgeDroneModeRules.ParseCycle(cycle, problems);

            foreach (string p in problems)
                Log.LogWarning("[Drone modes] " + p);

            string f = (formation ?? "").Trim().ToLowerInvariant();
            Locked = f == "locked" || f == "lock";

            if (f.Length > 0 && f != "loose" && !Locked)
            {
                Log.LogWarning(
                    "[Drone modes] Formation '" + formation + "' is not Loose or Locked - " +
                    "using Loose.");
            }

            SeekRange = Mathf.Clamp(seekRange, 10f, 80f);
            SentryRadius = Mathf.Clamp(sentryRadius, 2f, 30f);

            // ★ SAFELY INSIDE THE UNLOAD EDGE (5.4, answer D): every drone
            // prefab ships `destroyWhenUnloaded`, and the loaded area is about
            // 87 units round the camera - a sentry left past it is deleted with
            // nothing in the log.
            SentryRecall = Mathf.Clamp(sentryRecall, SentryRadius + 5f, 75f);

            AlertSeconds = Mathf.Clamp(alertSeconds, 0.5f, 10f);
            OrbitSpin = Mathf.Clamp(orbitSpin, -360f, 360f);
            InterceptorNoDamage = interceptorNoDamage;

            if (shapes != null)
                Defaults = shapes;

            // INTERCEPTOR needs the enemy shots, which no physics query can
            // see (their colliders ship disabled) - the tracker registers them
            // at fire time, and only while something asks.
            if (Cycle.Contains(Mode.Interceptor))
                ForgeProjectileTracker.Enabled = true;

            var words = new List<string>();

            foreach (Mode m in Cycle)
                words.Add(ForgeDroneModeRules.Word(m));

            Log.LogInfo(
                "drone modes: " + (KeyOn
                    ? ModeKey.Describe() + " steps every gadget's companions through NORMAL > " +
                      string.Join(" > ", words.ToArray()) + " > NORMAL; " +
                      ModeKey.KeyWord + " + a gadget's own button steps that gadget alone" +
                      (NormalKey.IsSet ? "; " + NormalKey.Describe() + " puts them all back on normal" : "") +
                      (HoldForNormal ? "; holding " + ModeKey.KeyWord + " about a second does too" : "") +
                      ". Formation: " + (Locked ? "Locked" : "Loose") + ", seek range " +
                      SeekRange.ToString("0.#", CultureInfo.InvariantCulture) + ", sentry radius " +
                      SentryRadius.ToString("0.#", CultureInfo.InvariantCulture) + ", recall at " +
                      SentryRecall.ToString("0.#", CultureInfo.InvariantCulture) + "." +
                      (InterceptorNoDamage ? " INTERCEPTOR TEST OPTION ON: interceptors take no damage." : "")
                    : "off - [Drone modes] Key and PadButton are both none, so the gadget buttons " +
                      "work as they always did."));
        }

        // A Forge `drone` file's own `modes` block, keyed by its module.
        // (A wingman file's lives on its own Config: `ForgeWingman.Config.modes`.)
        // Build-time configuration, like the weapon registries: NOT cleared at
        // run entry.
        private static readonly Dictionary<ModuleData, Shape> _droneShapes =
            new Dictionary<ModuleData, Shape>();

        public static void RegisterDroneShape(ModuleData module, Shape shape)
        {
            if (module != null && shape != null)
                _droneShapes[module] = shape;
        }

        private static readonly string[] ShapeKeys =
        {
            "join", "orbitRadius", "dragSpacing", "protectorDistance", "rearGuardDistance",
            "spacing", "interceptRadius", "seekRange", "sentryRadius"
        };

        // A wingman or drone file's `modes` block (concept 8): which modes this
        // gadget joins, and its own shape numbers on top of the [Drone modes]
        // defaults. Null when there is no block.
        //
        //   "modes": { "join": ["orbit", "protector"], "orbitRadius": 5 }
        //
        // `join` takes a list or one comma-separated string; "all" (the
        // default) or "none". A bad value is warned about and left out - a bad
        // file never costs the gadget.
        public static Shape ReadShape(Newtonsoft.Json.Linq.JToken token, string fileName, bool drone)
        {
            if (token == null || token.Type == Newtonsoft.Json.Linq.JTokenType.Null)
                return null;

            var o = token as Newtonsoft.Json.Linq.JObject;

            if (o == null)
            {
                Log.LogWarning(
                    fileName + ": \"modes\" should be a block, e.g. { \"join\": [\"orbit\"] } - " +
                    "ignored, so this gadget takes the [Drone modes] defaults.");
                return null;
            }

            Shape s = Defaults.Copy();
            var problems = new List<string>();

            Newtonsoft.Json.Linq.JToken join = o["join"];

            if (join != null)
            {
                var words = new List<string>();

                if (join.Type == Newtonsoft.Json.Linq.JTokenType.Array)
                {
                    foreach (Newtonsoft.Json.Linq.JToken w in join)
                        words.Add(w.Type == Newtonsoft.Json.Linq.JTokenType.String ? (string)w : w.ToString());
                }
                else
                {
                    words.AddRange((join.Type == Newtonsoft.Json.Linq.JTokenType.String
                        ? (string)join : join.ToString()).Split(','));
                }

                s.join = ForgeDroneModeRules.ParseJoin(words, problems);
            }

            s.orbitRadius = Mathf.Clamp((float?)o["orbitRadius"] ?? s.orbitRadius, 1f, 12f);
            s.dragSpacing = Mathf.Clamp((float?)o["dragSpacing"] ?? s.dragSpacing, 0.5f, 6f);
            s.protectorDistance = Mathf.Clamp((float?)o["protectorDistance"] ?? s.protectorDistance, 1f, 10f);
            s.rearGuardDistance = Mathf.Clamp((float?)o["rearGuardDistance"] ?? s.rearGuardDistance, 1f, 12f);
            s.spacing = Mathf.Clamp((float?)o["spacing"] ?? s.spacing, 0.5f, 5f);
            s.interceptRadius = Mathf.Clamp((float?)o["interceptRadius"] ?? s.interceptRadius, 1.5f, 8f);

            float? seek = (float?)o["seekRange"];
            float? sentry = (float?)o["sentryRadius"];

            if (drone)
            {
                if (seek.HasValue)
                    s.seekRange = Mathf.Clamp(seek.Value, 10f, 80f);

                if (sentry.HasValue)
                    s.sentryRadius = Mathf.Clamp(sentry.Value, 2f, 30f);
            }
            else if (seek.HasValue || sentry.HasValue)
            {
                problems.Add("seekRange and sentryRadius are for drone files - a wingman ring keeps " +
                             "its own motion under SEEKER and SENTRY, so they are ignored here.");
            }

            foreach (var pair in o)
            {
                if (Array.IndexOf(ShapeKeys, pair.Key) < 0)
                    problems.Add("\"" + pair.Key + "\" is not a modes key - the keys are " +
                                 string.Join(", ", ShapeKeys) + ".");
            }

            foreach (string p in problems)
                Log.LogWarning(fileName + ": modes - " + p);

            var joined = new List<string>();

            if (s.join != null)
            {
                foreach (Mode m in s.join)
                    joined.Add(ForgeDroneModeRules.Word(m));
            }

            Log.LogInfo(
                fileName + ": modes - joins " +
                (s.join == null ? "every mode it can do" :
                 s.join.Count == 0 ? "NO mode (it keeps its own behaviour whatever the key says)" :
                 string.Join(", ", joined.ToArray())) +
                "; orbit " + s.orbitRadius.ToString("0.##", CultureInfo.InvariantCulture) +
                ", drag gap " + s.dragSpacing.ToString("0.##", CultureInfo.InvariantCulture) +
                ", protector " + s.protectorDistance.ToString("0.##", CultureInfo.InvariantCulture) +
                ", rear guard " + s.rearGuardDistance.ToString("0.##", CultureInfo.InvariantCulture) +
                ", row gap " + s.spacing.ToString("0.##", CultureInfo.InvariantCulture) +
                (drone && s.seekRange > 0f
                    ? ", seek " + s.seekRange.ToString("0.#", CultureInfo.InvariantCulture) : "") +
                (drone && s.sentryRadius > 0f
                    ? ", sentry radius " + s.sentryRadius.ToString("0.#", CultureInfo.InvariantCulture) : "") +
                ".");

            return s;
        }

        // ------------------------------------------------------------------
        // The run's state
        // ------------------------------------------------------------------

        public sealed class Slot
        {
            public Mode mode;
            public string gadget;      // the gadget's data id when last seen
            public Vector2 post;       // SENTRY: where the ship was at the press
            public bool hasPost;
            public bool comboLatch;    // the gadget button is ours until let go
            public bool activatorWas;  // last frame's IsActivated, for the edge

            // Read once a frame.
            public Kind kind;
            public Shape shape;
            public string label;
        }

        public sealed class Ship
        {
            public int id;
            public Unit unit;
            public int shared = -1;
            public readonly Slot[] slots = { new Slot(), new Slot(), new Slot() };
            public readonly ForgeDroneModeRules.KeyMachine keys =
                new ForgeDroneModeRules.KeyMachine();

            public int pendingCombo;
            public int heldFrame = -1;
            public bool heldValue;

            public int readFrame = -1;
            public int rosterFrame = -1;

            public readonly List<P> path = new List<P>();
            public Vector2 last;
            public bool hasLast;
            public float phase;

            public Aimer aimer;
            public Vector2 aim = Vector2.right;
            public Rigidbody2D body;
            public ModuleGridOwner gridOwner;

            public string alertKey;
        }

        private static readonly Dictionary<int, Ship> _ships = new Dictionary<int, Ship>();

        // Run entry (ForgeBurnResetPatch, both entry points): a new run starts
        // on normal (answer 8). A Continue reads the save AFTER this, in the
        // Load postfix.
        public static void Reset()
        {
            _ships.Clear();
            _roster.Clear();
            _saidFar = false;
            ForgeDroneModeCarry.OnRunEntry();
        }

        public static Ship Get(Unit unit, bool create)
        {
            if (unit == null)
                return null;

            int id = ForgeWingmanSave.IdOf(unit);

            if (id == 0)
                return null;

            Ship s;

            if (!_ships.TryGetValue(id, out s))
            {
                if (!create)
                    return null;

                s = new Ship { id = id, alertKey = "WeaponForge.DroneModes." + id };
                _ships[id] = s;
            }

            if (!ReferenceEquals(s.unit, unit))
            {
                s.unit = unit;
                s.aimer = unit.GetComponentInChildren<Aimer>(true);
                s.body = unit.GetComponentInChildren<Rigidbody2D>(true);
                s.gridOwner = unit.GetComponentInChildren<ModuleGridOwner>(true);
                s.hasLast = false;
                s.path.Clear();
            }

            return s;
        }

        private static readonly ClusterType[] Clusters =
            { ClusterType.Active1, ClusterType.Active2, ClusterType.Active3 };

        public static int SlotOf(Unit.OwnerConnectionType c)
        {
            switch (c)
            {
                case Unit.OwnerConnectionType.Active1: return 1;
                case Unit.OwnerConnectionType.Active2: return 2;
                case Unit.OwnerConnectionType.Active3: return 3;
                default: return 0;
            }
        }

        // What each gadget slot holds, once a frame. Also the place a slot
        // whose gadget CHANGED goes back to normal (12.1 H: "Normal also works
        // as well") - a different gadget installed, or this one moved to
        // another slot.
        private static void Read(Ship s)
        {
            if (s.readFrame == Time.frameCount)
                return;

            s.readFrame = Time.frameCount;

            IModuleGrid grid = null;

            try
            {
                if (s.gridOwner == null && s.unit != null)
                    s.gridOwner = s.unit.GetComponentInChildren<ModuleGridOwner>(true);

                grid = s.gridOwner != null ? s.gridOwner.ModuleGrid : null;
            }
            catch (Exception)
            {
            }

            // ★ NO GRID YET IS NOT "THE GADGET CHANGED". A ship that is still
            // being bound after a load has no grid for a frame or two; treating
            // that as three empty slots would reset every restored mode.
            if (grid == null)
            {
                for (int n = 0; n < 3; n++)
                {
                    s.slots[n].kind = Kind.None;
                    s.slots[n].shape = Defaults;
                    s.slots[n].label = "";
                }

                return;
            }

            for (int n = 0; n < 3; n++)
            {
                Slot slot = s.slots[n];
                slot.kind = Kind.None;
                slot.shape = Defaults;
                slot.label = "";

                string gadget = "";

                try
                {
                    IModuleCluster c = grid.GetCluster(Clusters[n]);
                    Module main = (c != null && c.HasMainModule) ? c.MainModule : null;

                    if (main != null && main.Data != null)
                        gadget = main.Data.Id ?? "";

                    var spawner = main as SpawnMinionModule;
                    var wb = main as WeaponBasedActiveModule;
                    ForgeWingman.Config wcfg;

                    if (spawner != null)
                    {
                        slot.kind = Kind.AiDrone;

                        Shape own;

                        if (_droneShapes.TryGetValue(spawner.Data, out own) && own != null)
                            slot.shape = own;

                        slot.label = spawner.DisplayName;
                    }
                    else if (wb != null && ForgeWingman.TryGet(wb.WeaponData, out wcfg) && wcfg != null)
                    {
                        slot.kind = wcfg.IsDrone ? Kind.WingBodies : Kind.WingOrbs;

                        if (wcfg.modes != null)
                            slot.shape = wcfg.modes;

                        slot.label = wb.DisplayName;
                    }
                }
                catch (Exception)
                {
                }

                if (slot.gadget == null)
                {
                    slot.gadget = gadget;
                }
                else if (slot.gadget != gadget)
                {
                    if (slot.mode != Mode.Base)
                    {
                        Log.LogInfo(
                            "drone modes: gadget " + (n + 1) + " changed (" + Name(slot.gadget) +
                            " -> " + Name(gadget) + "), so it goes back to normal.");
                    }

                    slot.mode = Mode.Base;
                    slot.hasPost = false;
                    slot.gadget = gadget;
                }
            }
        }

        private static string Name(string id)
        {
            return string.IsNullOrEmpty(id) ? "empty" : id;
        }

        private static bool Can(Slot slot, Mode m)
        {
            return ForgeDroneModeRules.Can(
                slot.kind, slot.shape != null ? slot.shape.join : null, m, InterceptorNoDamage);
        }

        // ★ THE MODE A SLOT'S COMPANIONS ACT ON RIGHT NOW. A slot holds the
        // mode it was put in; it ACTS on it only if its companions can do it
        // (a wingman slot on SEEKER keeps its normal behaviour - 12.1 gaps)
        // and the mode is still in the cycle (a .cfg changed under a save).
        public static Mode Effective(Unit unit, int slot)
        {
            // Key = none and PadButton = none switch the whole feature off -
            // including a mode a save handed back.
            if (slot < 1 || slot > 3 || !KeyOn)
                return Mode.Base;

            Ship s = Get(unit, false);

            if (s == null)
                return Mode.Base;

            Read(s);

            Slot sl = s.slots[slot - 1];

            if (sl.mode == Mode.Base || !Cycle.Contains(sl.mode) || !Can(sl, sl.mode))
                return Mode.Base;

            return sl.mode;
        }

        public static bool PostOf(Unit unit, int slot, out Vector2 post)
        {
            post = Vector2.zero;

            Ship s = Get(unit, false);

            if (s == null || slot < 1 || slot > 3 || !s.slots[slot - 1].hasPost)
                return false;

            post = s.slots[slot - 1].post;
            return true;
        }

        public static Shape ShapeOf(Unit unit, int slot)
        {
            Ship s = Get(unit, false);

            if (s == null || slot < 1 || slot > 3)
                return Defaults;

            Read(s);

            return s.slots[slot - 1].shape ?? Defaults;
        }

        // ------------------------------------------------------------------
        // Pressing
        // ------------------------------------------------------------------

        private static bool AnyCompanions(Ship s)
        {
            for (int n = 0; n < 3; n++)
                if (s.slots[n].kind != Kind.None)
                    return true;

            return false;
        }

        // The mode key alone.
        public static void StepAll(Ship s)
        {
            Read(s);

            if (!AnyCompanions(s))
            {
                Alert(s, "NO DRONES");
                return;
            }

            s.shared = ForgeDroneModeRules.NextShared(Cycle, s.shared, m =>
            {
                for (int n = 0; n < 3; n++)
                    if (Can(s.slots[n], m))
                        return true;

                return false;
            });

            Mode mode = s.shared < 0 ? Mode.Base : Cycle[s.shared];

            for (int n = 0; n < 3; n++)
                Enter(s, n, s.slots[n].kind == Kind.None ? Mode.Base : mode);

            Alert(s, "DRONES: " + ForgeDroneModeRules.Word(mode));
            Said(s, "DRONES: " + ForgeDroneModeRules.Word(mode));
        }

        // The mode key + gadget n's own button (or the optional SlotN key).
        public static void StepSlot(Ship s, int n)
        {
            Read(s);

            if (n < 1 || n > 3)
                return;

            Slot slot = s.slots[n - 1];

            if (slot.kind == Kind.None)
            {
                Alert(s, "GADGET " + n + ": NO DRONES");
                return;
            }

            Mode next = ForgeDroneModeRules.NextForSlot(Cycle, slot.mode, m => Can(slot, m));

            Enter(s, n - 1, next);

            Alert(s, "GADGET " + n + ": " + ForgeDroneModeRules.Word(next));
            Said(s, "GADGET " + n + ": " + ForgeDroneModeRules.Word(next));
        }

        // The back-to-normal key (U), or Y held (12.2 M).
        public static void Normal(Ship s)
        {
            Read(s);

            if (!AnyCompanions(s))
            {
                Alert(s, "NO DRONES");
                return;
            }

            s.shared = -1;

            for (int n = 0; n < 3; n++)
                Enter(s, n, Mode.Base);

            Alert(s, "DRONES: NORMAL");
            Said(s, "DRONES: NORMAL");
        }

        private static void Enter(Ship s, int n, Mode mode)
        {
            Slot slot = s.slots[n];

            slot.mode = mode;

            // SENTRY guards the spot where your ship was when you pressed
            // (5.4). Re-pressing into SENTRY stamps a new post. Only for a
            // slot that can stand sentry - a wingman slot swept into SENTRY by
            // the shared key keeps its own motion and must not be "recalled".
            if (mode == Mode.Sentry && s.unit != null && Can(slot, Mode.Sentry))
            {
                slot.post = s.unit.transform.position;
                slot.hasPost = true;
            }
            else
            {
                slot.hasPost = false;
            }
        }

        private static void Alert(Ship s, string text)
        {
            try
            {
                ForgeBuffHud.Alert(s.alertKey, new Color(0.55f, 0.9f, 1f), text, AlertSeconds);
            }
            catch (Exception)
            {
                // An alert must never cost the press.
            }
        }

        // The log line every press writes, naming what each slot did - it is
        // what a test reads, and the one place "it did nothing" can be told
        // from "that gadget cannot do that mode".
        private static void Said(Ship s, string what)
        {
            var sb = new StringBuilder();
            sb.Append("drone modes: ").Append(what).Append(" -");

            for (int n = 0; n < 3; n++)
            {
                Slot sl = s.slots[n];

                if (sl.kind == Kind.None)
                    continue;

                Mode acts = (sl.mode == Mode.Base || Can(sl, sl.mode)) ? sl.mode : Mode.Base;

                sb.Append(" gadget ").Append(n + 1).Append(" (").Append(sl.label).Append(", ")
                  .Append(KindWord(sl.kind)).Append(") ").Append(ForgeDroneModeRules.Word(acts));

                if (acts != sl.mode)
                    sb.Append(" [it cannot do ").Append(ForgeDroneModeRules.Word(sl.mode)).Append(']');

                if (sl.mode == Mode.Sentry && sl.hasPost)
                {
                    sb.Append(" at ").Append(sl.post.x.ToString("0", CultureInfo.InvariantCulture))
                      .Append(',').Append(sl.post.y.ToString("0", CultureInfo.InvariantCulture));
                }

                sb.Append(';');
            }

            Log.LogInfo(sb.ToString());
        }

        private static string KindWord(Kind k)
        {
            switch (k)
            {
                case Kind.AiDrone: return "drones";
                case Kind.WingOrbs: return "wingman orbs";
                case Kind.WingBodies: return "wingman drones";
                default: return "nothing";
            }
        }

        // ------------------------------------------------------------------
        // The gadget buttons while the mode key is held
        // ------------------------------------------------------------------

        // Is this ship's mode key down right now? Read live, once a frame, so
        // the gadget gate below sees it on the very frame it goes down.
        public static bool ModeHeld(Ship s)
        {
            if (s.heldFrame == Time.frameCount)
                return s.heldValue;

            s.heldFrame = Time.frameCount;
            s.heldValue = false;

            if (!KeyOn || s.unit == null || !ForgeKeyBinding.Flying(s.unit))
                return false;

            s.heldValue = ModeKey.Held(ForgeKeyBinding.DevicesOf(s.unit));
            return s.heldValue;
        }

        // ModuleActivator.Update prefix. Returns false to keep the gadget from
        // firing.
        //
        // ★ WHILE THE MODE KEY IS HELD THE GADGET MUST NOT FIRE (concept 2,
        // 11) - or Y + 1 would change the mode AND deploy a drone. And the slot
        // stays ours until its button is let go, so letting go of Y a moment
        // before 1 does not deploy on the way out.
        //
        // ★ A COMBO IS A GADGET PRESS THAT BEGINS WHILE THE MODE KEY IS DOWN.
        // A button already held when Y went down is held back too, but does not
        // count - holding 1 to deploy and tapping Y steps everything, not just
        // gadget 1.
        public static bool ActivatorGate(Unit unit, int index, bool activated)
        {
            if (!KeyOn || unit == null || index < 1 || index > 3)
                return true;

            Kick();

            Ship s = Get(unit, true);

            if (s == null)
                return true;

            Slot slot = s.slots[index - 1];
            bool was = slot.activatorWas;
            slot.activatorWas = activated;

            bool held = ModeHeld(s);

            if (held && activated && !was)
            {
                s.pendingCombo = index;
                slot.comboLatch = true;
            }

            if (!activated)
                slot.comboLatch = false;

            return !(held || slot.comboLatch);
        }

        // ------------------------------------------------------------------
        // Once a frame, per ship (ForgeDroneModeTicker)
        // ------------------------------------------------------------------

        public static void Tick(Ship s)
        {
            if (s == null || s.unit == null)
                return;

            Read(s);

            Vector2 here = s.unit.transform.position;

            // ★ FAST TRAVEL CARRIES THE SENTRIES, so their post moves too
            // (5.4): `FastTravelManager.Teleport` puts the ship's minions at
            // the arrival point, and a post left at the old spot would fly them
            // back across the map - or recall them on the first frame.
            if (s.hasLast && (here - s.last).sqrMagnitude > 30f * 30f)
            {
                for (int n = 0; n < 3; n++)
                {
                    if (s.slots[n].mode == Mode.Sentry && s.slots[n].hasPost)
                    {
                        s.slots[n].post = here;

                        Log.LogInfo(
                            "drone modes: your ship jumped (fast travel or a load) - gadget " +
                            (n + 1) + "'s sentries guard the spot you arrived at.");
                    }
                }

                s.path.Clear();
            }

            s.last = here;
            s.hasLast = true;

            // DRAG follows the path you FLEW, like a tail.
            P p = new P(here.x, here.y);

            if (s.path.Count == 0 || (s.path[s.path.Count - 1] - p).Length >= 0.25f)
            {
                s.path.Add(p);

                while (s.path.Count > 240)
                    s.path.RemoveAt(0);
            }

            s.phase = Mathf.Repeat(s.phase + OrbitSpin * Time.deltaTime, 360f);

            try
            {
                if (s.aimer != null)
                {
                    Vector2 d = (Vector2)s.aimer.TargetPosition - here;

                    if (d.sqrMagnitude > 0.0001f)
                        s.aim = d.normalized;
                }
            }
            catch (Exception)
            {
            }

            // ★ THE SENTRY RECALL (answer D, "What you suggested sounds
            // perfect"): fly past SentryRecall from the post and the sentries
            // leave it and come back, and the slot is on normal again (12.1 G).
            for (int n = 0; n < 3; n++)
            {
                Slot sl = s.slots[n];

                if (sl.mode != Mode.Sentry || !sl.hasPost || !Can(sl, Mode.Sentry))
                    continue;

                float limit = SentryRecall;

                if ((here - sl.post).sqrMagnitude > limit * limit)
                {
                    sl.mode = Mode.Base;
                    sl.hasPost = false;

                    Alert(s, "GADGET " + (n + 1) + ": SENTRY RECALLED");
                    Log.LogInfo(
                        "drone modes: GADGET " + (n + 1) + ": SENTRY RECALLED - you flew " +
                        limit.ToString("0", CultureInfo.InvariantCulture) + " units from the post, " +
                        "so the sentries left it and gadget " + (n + 1) + " is back on normal.");
                }
            }
        }

        public static void Keys(Ship s)
        {
            if (!KeyOn || s.unit == null)
                return;

            bool flying = ForgeKeyBinding.Flying(s.unit);
            IList<UnityEngine.InputSystem.InputDevice> devices =
                flying ? ForgeKeyBinding.DevicesOf(s.unit) : null;

            bool held = flying && ModeHeld(s);
            int combo = s.pendingCombo;
            s.pendingCombo = 0;

            if (held && combo == 0)
            {
                for (int n = 0; n < 3; n++)
                {
                    if (SlotKeys[n] != null && SlotKeys[n].IsSet && SlotKeys[n].Pressed(devices))
                    {
                        combo = n + 1;
                        break;
                    }
                }
            }

            bool normal = flying && NormalKey != null && NormalKey.IsSet && NormalKey.Pressed(devices);

            s.keys.holdForNormal = HoldForNormal;

            int slot;
            ForgeDroneModeRules.Act act =
                s.keys.Tick(flying, held, combo, normal, Time.unscaledTime, out slot);

            switch (act)
            {
                case ForgeDroneModeRules.Act.StepAll:
                    StepAll(s);
                    break;
                case ForgeDroneModeRules.Act.StepSlot:
                    StepSlot(s, slot);
                    break;
                case ForgeDroneModeRules.Act.Normal:
                    Normal(s);
                    break;
            }
        }

        // The clock, made the first time anything needs it.
        private static ForgeDroneModeTicker _ticker;

        public static void Kick()
        {
            if (_ticker != null)
                return;

            var go = new GameObject("ForgeDroneModes");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            _ticker = go.AddComponent<ForgeDroneModeTicker>();
        }

        public static void TickAll()
        {
            foreach (Unit ship in ForgeKeyBinding.Ships())
            {
                if (ship == null)
                    continue;

                Ship s = Get(ship, true);

                if (s == null)
                    continue;

                Tick(s);
                Keys(s);
            }
        }

        // ------------------------------------------------------------------
        // Formations: one per ship per mode, every companion in it
        // ------------------------------------------------------------------

        private sealed class Member
        {
            public object who;
            public int sub;
            public Shape shape;
            public Vector2 pos;
        }

        private sealed class Formation
        {
            public readonly List<Member> members = new List<Member>();
            public int[] taken;                           // INTERCEPTOR
            public readonly List<ForgeDroneModeRules.Incoming> shots =
                new List<ForgeDroneModeRules.Incoming>();
        }

        // ship id -> mode -> its formation, rebuilt once a frame on demand.
        private static readonly Dictionary<int, Dictionary<Mode, Formation>> _roster =
            new Dictionary<int, Dictionary<Mode, Formation>>();

        private static Dictionary<Mode, Formation> Roster(Ship s)
        {
            Dictionary<Mode, Formation> r;

            if (!_roster.TryGetValue(s.id, out r))
            {
                r = new Dictionary<Mode, Formation>();
                _roster[s.id] = r;
            }

            if (s.rosterFrame == Time.frameCount)
                return r;

            s.rosterFrame = Time.frameCount;

            foreach (var f in r.Values)
            {
                f.members.Clear();
                f.shots.Clear();
                f.taken = null;
            }

            // Slot order, wingmen before AI drones within a slot, AI drones by
            // instance - so a formation is laid out the same way every frame
            // and nobody swaps places.
            for (int n = 1; n <= 3; n++)
            {
                Mode m = Effective(s.unit, n);

                if (!ForgeDroneModeRules.IsFormation(m) && m != Mode.Interceptor)
                    continue;

                Formation f;

                if (!r.TryGetValue(m, out f))
                {
                    f = new Formation();
                    r[m] = f;
                }

                Shape shape = s.slots[n - 1].shape ?? Defaults;
                int slot = n;

                ForgeWingmanController.ForSlot(s.unit, slot, c =>
                {
                    for (int w = 0; w < c.WingCount; w++)
                    {
                        // INTERCEPTOR without the test option: an orb has no
                        // hitbox and cannot block, so it keeps its own ring.
                        if (m == Mode.Interceptor && !c.WingHasBody(w) && !InterceptorNoDamage)
                            continue;

                        f.members.Add(new Member
                            { who = c, sub = w, shape = shape, pos = c.WingPosition(w) });
                    }
                });

                ForgeDroneBrain.ForSlot(s.unit, slot, b =>
                    f.members.Add(new Member { who = b, sub = 0, shape = shape, pos = b.Position }));
            }

            Formation guard;

            if (r.TryGetValue(Mode.Interceptor, out guard) && guard.members.Count > 0)
                Intercept(s, guard);

            return r;
        }

        // Where companion (who, sub) of this ship goes in mode `m`. False if it
        // is not in that formation this frame (it then keeps its own motion).
        public static bool SpotFor(Unit unit, Mode m, object who, int sub, out Vector2 spot)
        {
            spot = unit != null ? (Vector2)unit.transform.position : Vector2.zero;

            Ship s = Get(unit, false);

            if (s == null)
                return false;

            Formation f;

            if (!Roster(s).TryGetValue(m, out f))
                return false;

            int i = -1;

            for (int k = 0; k < f.members.Count; k++)
            {
                if (ReferenceEquals(f.members[k].who, who) && f.members[k].sub == sub)
                {
                    i = k;
                    break;
                }
            }

            if (i < 0)
                return false;

            int count = f.members.Count;
            Shape sh = f.members[i].shape ?? Defaults;

            Vector2 here = unit.transform.position;
            P ship = new P(here.x, here.y);
            P aim = new P(s.aim.x, s.aim.y);
            P at;

            switch (m)
            {
                case Mode.Orbit:
                    at = ForgeDroneModeRules.Orbit(ship, s.phase, i, count, sh.orbitRadius);
                    break;

                case Mode.Drag:
                    at = ForgeDroneModeRules.Drag(s.path, ship, i, sh.dragSpacing);
                    break;

                case Mode.Protector:
                    at = ForgeDroneModeRules.Screen(ship, aim, i, count, sh.protectorDistance, sh.spacing);
                    break;

                case Mode.RearGuard:
                    at = ForgeDroneModeRules.Screen(
                        ship, aim * -1f, i, count, sh.rearGuardDistance,
                        sh.spacing * ForgeDroneModeRules.RearGuardWiden);
                    break;

                case Mode.Interceptor:
                    if (f.taken != null && i < f.taken.Length && f.taken[i] >= 0 &&
                        f.taken[i] < f.shots.Count)
                    {
                        at = f.shots[f.taken[i]].at;
                    }
                    else
                    {
                        at = ForgeDroneModeRules.GuardIdle(ship, aim, i, count, sh.interceptRadius);
                    }
                    break;

                default:
                    return false;
            }

            spot = new Vector2(at.x, at.y);
            return true;
        }

        // ------------------------------------------------------------------
        // INTERCEPTOR - his idea (4.1)
        // ------------------------------------------------------------------

        private static readonly List<Component> _near = new List<Component>();

        // How fast an interceptor can get somewhere: it always flies LOCKED
        // (answer C), at the locked chase cap.
        public const float InterceptReach = ForgeDroneBrain.LockedCap;

        private static void Intercept(Ship s, Formation f)
        {
            _near.Clear();

            Vector2 here = s.unit.transform.position;
            ForgeProjectileTracker.EnemyNear(here, 30f, _near);

            if (_near.Count == 0)
                return;

            P ship = new P(here.x, here.y);
            float guard = (f.members[0].shape ?? Defaults).interceptRadius;

            foreach (Component c in _near)
            {
                var ip = c as IProjectile;

                if (ip == null)
                    continue;

                Vector2 pos = c.transform.position;
                Vector2 v;

                try
                {
                    v = ip.Velocity;
                }
                catch (Exception)
                {
                    continue;
                }

                P from = new P(pos.x, pos.y);
                P vel = new P(v.x, v.y);
                float tc;

                // Only shots heading AT you (answer C: "only shots heading at
                // you, nearest first, one drone per shot").
                if (!ForgeDroneModeRules.Threat(ship, 2.2f, from, vel, 2.5f, out tc))
                    continue;

                // ★ AN EXPLOSIVE SHOT IS MET OUTSIDE ITS OWN BLAST (4.1, 11):
                // a rocket that meets a drone detonates on it, and no explosion
                // in this game has a friendly filter - so meet it close and it
                // blows up in your face.
                float meet = ForgeDroneModeRules.MeetRadius(guard, BlastReach(ip));

                P at;
                float t;

                if (!ForgeDroneModeRules.Crossing(ship, meet, from, vel, out at, out t))
                    continue;

                f.shots.Add(new ForgeDroneModeRules.Incoming { at = at, t = t });
            }

            if (f.shots.Count == 0)
                return;

            var mine = new List<P>();

            foreach (Member m in f.members)
                mine.Add(new P(m.pos.x, m.pos.y));

            f.taken = ForgeDroneModeRules.Assign(mine, f.shots, InterceptReach);
        }

        // How far a shot's explosion reaches, or 0 if it does not explode.
        private static float BlastReach(IProjectile ip)
        {
            try
            {
                // All three are structs, read by value.
                ProjectileImpactBehaviour impact = ip.ImpactBehaviour;
                ProjectileLifetimeData life = ip.LifetimeData;
                ProjectileRangeData range = ip.RangeData;

                bool boom =
                    (impact.enabled && impact.spawnExplosion) ||
                    (life.enabled && life.spawnExplosion) ||
                    (range.enabled && range.spawnExplosion);

                return boom ? ip.Explosion.BurnRadius : 0f;
            }
            catch (Exception)
            {
                return 0f;
            }
        }

        // ------------------------------------------------------------------
        // Rock
        // ------------------------------------------------------------------
        //
        // ★ HIS ANSWER K: "I'll take your recommendation." A formation spot
        // inside rock moves toward you until it is clear - the ring squeezes in
        // a tunnel and opens again outside it. Terrain is read off the CELL
        // GRID, the way wall riding and the rank choices read it. Wingmen are
        // unchanged (orbs have no collider, drone bodies are kinematic), so only
        // AI drones ask.

        public static bool Solid(Vector2 p, float clear)
        {
            try
            {
                Level level;

                if (!ServiceLocator.TryGet<Level>(out level) || level == null)
                    return false;

                int minX = Mathf.FloorToInt(p.x - clear);
                int maxX = Mathf.FloorToInt(p.x + clear);
                int minY = Mathf.FloorToInt(p.y - clear);
                int maxY = Mathf.FloorToInt(p.y + clear);

                for (int x = minX; x <= maxX; x++)
                {
                    for (int y = minY; y <= maxY; y++)
                    {
                        if (!level.ContainsCell(x, y) || level.IsEmpty(x, y))
                            continue;

                        CellType type = level.GetCellType(x, y);

                        if (type == null || type.colliderType != CellType.ColliderType.NonTrigger)
                            continue;

                        float dx = Mathf.Max(0f, Mathf.Max(x - p.x, p.x - (x + 1)));
                        float dy = Mathf.Max(0f, Mathf.Max(y - p.y, p.y - (y + 1)));

                        if (dx * dx + dy * dy < clear * clear)
                            return true;
                    }
                }
            }
            catch (Exception)
            {
                // Unknown is clear.
            }

            return false;
        }

        public static Vector2 ClearOfRock(Vector2 spot, Vector2 ship)
        {
            const float clear = 0.75f;

            if (!Solid(spot, clear))
                return spot;

            Vector2 to = ship - spot;
            float d = to.magnitude;

            if (d < 0.01f)
                return ship;

            Vector2 step = to / d * 0.4f;
            Vector2 p = spot;

            for (int i = 0; i < 40 && (p - spot).magnitude < d; i++)
            {
                p += step;

                if (!Solid(p, clear))
                    return p;
            }

            return ship;
        }

        // Is there rock on the straight line from a to b?
        public static bool Walled(Vector2 a, Vector2 b)
        {
            Vector2 to = b - a;
            float d = to.magnitude;

            if (d < 0.6f)
                return false;

            int steps = Mathf.Min(80, Mathf.CeilToInt(d / 0.5f));

            for (int i = 1; i < steps; i++)
            {
                if (Solid(a + to * (i / (float)steps), 0.35f))
                    return true;
            }

            return false;
        }

        private static bool _saidFar;

        public static void SayFar(string who)
        {
            if (_saidFar)
                return;

            _saidFar = true;

            Log.LogInfo(
                "drone modes: " + who + " fell far behind in a formation, so it was brought " +
                "back beside you rather than asked to fly the whole way (a drone left ~87 " +
                "units behind the camera is unloaded and deleted). Said once per run.");
        }

        // ------------------------------------------------------------------
        // The card
        // ------------------------------------------------------------------

        // "Y: every gadget - Y + its button: this one - U: normal", on a drone
        // or wingman gadget's card (concept 6: "the gadget's card names the
        // key, the way a ranked gun's card names H").
        public static string CardLine()
        {
            if (!KeyOn)
                return null;

            string y = ModeKey.Describe();
            string k = ModeKey.KeyWord.Length > 0 ? ModeKey.KeyWord : y;

            return y + " steps every gadget's companions; " + k + " + this gadget's button " +
                   "steps only these" +
                   (NormalKey != null && NormalKey.IsSet ? "; " + NormalKey.Describe() + " = normal" : "");
        }

        // ------------------------------------------------------------------
        // Save lines (ForgeDroneModeSave) and the New Game Plus carry
        // ------------------------------------------------------------------

        // One line per slot that is off normal, plus one per ship for the
        // shared step: "<ship>\t<slot>\t<Mode>\t<gadget>\t<x>\t<y>" and
        // "shared\t<ship>\t<Mode>".
        public static List<string> RecordLines()
        {
            var lines = new List<string>();

            foreach (Ship s in _ships.Values)
            {
                if (s.shared >= 0 && s.shared < Cycle.Count)
                {
                    lines.Add("shared\t" + s.id.ToString(CultureInfo.InvariantCulture) + "\t" +
                              ForgeDroneModeRules.Token(Cycle[s.shared]));
                }

                for (int n = 0; n < 3; n++)
                {
                    Slot sl = s.slots[n];

                    if (sl.mode == Mode.Base)
                        continue;

                    lines.Add(string.Join("\t", new[]
                    {
                        s.id.ToString(CultureInfo.InvariantCulture),
                        (n + 1).ToString(CultureInfo.InvariantCulture),
                        ForgeDroneModeRules.Token(sl.mode),
                        (sl.gadget ?? "").Replace('\t', ' '),
                        sl.hasPost ? sl.post.x.ToString("R", CultureInfo.InvariantCulture) : "-",
                        sl.hasPost ? sl.post.y.ToString("R", CultureInfo.InvariantCulture) : "-"
                    }));
                }
            }

            return lines;
        }

        // Put saved lines back. `mapId` turns a saved ship id into this
        // world's (identity for a Continue, NgCarry.NewShipId for a jump;
        // 0 = drop the line). `dropSentry`: across a New Game Plus jump a
        // SENTRY slot arrives on normal - its post is in the old world (12.1 G).
        public static int Restore(IEnumerable<string> lines, Func<int, int> mapId, bool dropSentry)
        {
            int restored = 0;

            foreach (string raw in lines)
            {
                string line = (raw ?? "").Trim();

                if (line.Length == 0 || line[0] == '#')
                    continue;

                string[] f = line.Split('\t');

                try
                {
                    if (f[0] == "shared" && f.Length >= 3)
                    {
                        int id = mapId(int.Parse(f[1], CultureInfo.InvariantCulture));
                        Mode m;

                        if (id == 0 || !ForgeDroneModeRules.TryParse(f[2], out m))
                            continue;

                        if (dropSentry && m == Mode.Sentry)
                            continue;

                        Ship s = Placeholder(id);
                        s.shared = ForgeDroneModeRules.IndexOf(Cycle, m);
                        continue;
                    }

                    if (f.Length < 6)
                        continue;

                    int ship = mapId(int.Parse(f[0], CultureInfo.InvariantCulture));
                    int slot = int.Parse(f[1], CultureInfo.InvariantCulture);
                    Mode mode;

                    if (ship == 0 || slot < 1 || slot > 3 || !ForgeDroneModeRules.TryParse(f[2], out mode))
                        continue;

                    if (dropSentry && mode == Mode.Sentry)
                        continue;

                    Ship st = Placeholder(ship);
                    Slot sl = st.slots[slot - 1];

                    sl.mode = mode;
                    sl.gadget = f[3];

                    if (f[4] != "-" && f[5] != "-")
                    {
                        sl.post = new Vector2(
                            float.Parse(f[4], CultureInfo.InvariantCulture),
                            float.Parse(f[5], CultureInfo.InvariantCulture));
                        sl.hasPost = true;
                    }
                    else
                    {
                        sl.hasPost = false;
                    }

                    // A SENTRY with no post cannot guard anything.
                    if (sl.mode == Mode.Sentry && !sl.hasPost)
                        sl.mode = Mode.Base;

                    restored++;
                }
                catch (Exception)
                {
                    // One bad line costs that line.
                }
            }

            return restored;
        }

        // A ship known by id before its Unit is seen (a load hands the lines
        // back before the ship is flying). `Get` fills the Unit in later.
        private static Ship Placeholder(int id)
        {
            Ship s;

            if (!_ships.TryGetValue(id, out s))
            {
                s = new Ship { id = id, alertKey = "WeaponForge.DroneModes." + id };
                _ships[id] = s;
            }

            return s;
        }
    }

    // The clock: one frame tick for every player ship. Made by
    // ForgeDroneModes.Kick the first time anything needs it, and kept for the
    // session - it costs a dictionary lookup per ship per frame.
    public class ForgeDroneModeTicker : MonoBehaviour
    {
        private static readonly ManualLogSource Log =
            ForgeLog.Source("WeaponForge.DroneModes");

        private bool _said;

        private void Update()
        {
            try
            {
                ForgeDroneModes.TickAll();
            }
            catch (Exception e)
            {
                if (!_said)
                {
                    _said = true;
                    Log.LogWarning("drone modes skipped a beat: " + e);
                }
            }
        }
    }
}
