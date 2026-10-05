using System;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;

namespace WeaponForge
{
    // A key from this mod's .cfg, read off ONE PLAYER'S devices.
    //
    // Shared by the rank put-away key (H) and the drone-mode keys (Y, U and
    // the optional slot keys), because both need the same three things and a
    // second copy would drift:
    //
    //  * **The words a player types.** "Y", "F5", "Backquote" for a key,
    //    "DpadRight", "LB", "R3" for a pad button, or an Input System path
    //    ("<Keyboard>/y", "<Gamepad>/dpad/right"). "none" for nothing.
    //  * **★ WHICH PLAYER PRESSED IT (concept 2, co-op; 12.2 N).**
    //    `ShipInput.AssignDevice` pairs each co-op player's `PlayerInput` with
    //    its own devices - a gamepad player gets that gamepad, the keyboard
    //    player keyboard + mouse - and `PlayerInput.devices` is public. So a
    //    key is read on THAT ship's devices only, and each player's key acts
    //    on their own ship. With one ship there is no pairing to trust (the
    //    game auto-switches between keyboard and pad), so every device counts.
    //  * **Only while flying.** The ship's own flying controls
    //    (`ShipControlActionMap.Enabled`) are off in the map, a shop, the ship
    //    menu, the start sequence and every menu - so a key that only means
    //    something in flight is never read there. The map uses the D-pad to
    //    scroll, which is why this matters for a pad button.
    //
    // ★ IT NEVER TOUCHES THE GAME'S CONTROLS. It reads the devices' own
    // buttons (`ButtonControl.isPressed` / `wasPressedThisFrame`); nothing is
    // bound into the game's `PlayerInput` and no action of the game's is
    // changed. A read that throws latches the binding off and says so once.
    public sealed class ForgeKeyBinding
    {
        private static readonly ManualLogSource Log =
            ForgeLog.Source("WeaponForge.Keys");

        private struct Path
        {
            public string layout;   // Keyboard / Mouse / Gamepad / any layout name
            public string child;    // "y", "dpad/right", "leftButton"
        }

        private readonly List<Path> _paths = new List<Path>();
        private readonly string _section;
        private readonly string _keyText;
        private readonly string _padText;
        private bool _broken;

        // deviceId -> child path -> the button on that device (null = it has none).
        private readonly Dictionary<int, Dictionary<string, ButtonControl>> _controls =
            new Dictionary<int, Dictionary<string, ButtonControl>>();

        private ForgeKeyBinding(string section, string key, string pad)
        {
            _section = section;
            _keyText = (key ?? "").Trim();
            _padText = (pad ?? "").Trim();
        }

        public bool IsSet { get { return _paths.Count > 0 && !_broken; } }

        // `key` is read as a keyboard key, `pad` as a pad button. Either may be
        // blank or "none". Anything unreadable is warned about once, naming the
        // section, and left out.
        public static ForgeKeyBinding Parse(string section, string what, string key, string pad)
        {
            var b = new ForgeKeyBinding(section, key, pad);

            string k = KeyPath(b._keyText);
            string p = PadPath(b._padText, section, what);

            b.Add(k);
            b.Add(p);

            return b;
        }

        private void Add(string path)
        {
            if (string.IsNullOrEmpty(path))
                return;

            // "<Layout>/child/path"
            int close = path.IndexOf('>');

            if (!path.StartsWith("<") || close < 2 || close + 2 > path.Length)
            {
                Log.LogWarning(
                    "[" + _section + "] '" + path + "' is not a key this mod can read - " +
                    "write a key name (Y, F5, Backquote) or a path such as <Keyboard>/y.");
                return;
            }

            string layout = path.Substring(1, close - 1);
            string child = path.Substring(close + 1).TrimStart('/');

            if (child.Length == 0)
                return;

            _paths.Add(new Path { layout = layout, child = child });
        }

        // "Y" / "F5" / "<Keyboard>/y" -> a keyboard control path.
        public static string KeyPath(string s)
        {
            if (string.IsNullOrEmpty(s) || s.Equals("none", StringComparison.OrdinalIgnoreCase))
                return null;

            if (s.StartsWith("<"))
                return s;

            // The Input System's control names are case-insensitive.
            return "<Keyboard>/" + s.Replace(" ", "").ToLowerInvariant();
        }

        // A few friendly pad names, or any path as-is.
        public static string PadPath(string s, string section, string what)
        {
            if (string.IsNullOrEmpty(s) || s.Equals("none", StringComparison.OrdinalIgnoreCase))
                return null;

            if (s.StartsWith("<"))
                return s;

            switch (s.Replace(" ", "").Replace("-", "").ToLowerInvariant())
            {
                case "dpadup": return "<Gamepad>/dpad/up";
                case "dpaddown": return "<Gamepad>/dpad/down";
                case "dpadleft": return "<Gamepad>/dpad/left";
                case "dpadright": return "<Gamepad>/dpad/right";
                case "leftstickpress":
                case "l3": return "<Gamepad>/leftStickPress";
                case "rightstickpress":
                case "r3": return "<Gamepad>/rightStickPress";
                case "select":
                case "back":
                case "view": return "<Gamepad>/select";
                case "start":
                case "menu": return "<Gamepad>/start";
                case "north":
                case "y":
                case "triangle": return "<Gamepad>/buttonNorth";
                case "south":
                case "a":
                case "cross": return "<Gamepad>/buttonSouth";
                case "east":
                case "b":
                case "circle": return "<Gamepad>/buttonEast";
                case "west":
                case "x":
                case "square": return "<Gamepad>/buttonWest";
                case "leftshoulder":
                case "lb":
                case "l1": return "<Gamepad>/leftShoulder";
                case "rightshoulder":
                case "rb":
                case "r1": return "<Gamepad>/rightShoulder";
            }

            Log.LogWarning(
                "[" + section + "] " + what + " '" + s + "' is not a pad button this " +
                "mod knows - use DpadUp, DpadDown, DpadLeft, DpadRight, " +
                "LeftStickPress, RightStickPress, Select, North/South/East/West, " +
                "LB or RB, or an Input System path such as <Gamepad>/dpad/up. " +
                "The pad has no button for it this launch.");

            return null;
        }

        // For cards and the log: "Y (pad: D-pad right)".
        public string Describe()
        {
            bool key = KeyPath(_keyText) != null;
            bool pad = !string.IsNullOrEmpty(_padText) &&
                       !_padText.Equals("none", StringComparison.OrdinalIgnoreCase);

            if (!key && !pad)
                return "no key";

            return (key ? _keyText.ToUpperInvariant() : "") +
                   (key && pad ? " (pad: " + PadWord(_padText) + ")" : "") +
                   (!key && pad ? "pad " + PadWord(_padText) : "");
        }

        public string KeyWord
        {
            get { return KeyPath(_keyText) != null ? _keyText.ToUpperInvariant() : ""; }
        }

        public static string PadWord(string s)
        {
            switch ((s ?? "").Replace(" ", "").Replace("-", "").ToLowerInvariant())
            {
                case "dpadup": return "D-pad up";
                case "dpaddown": return "D-pad down";
                case "dpadleft": return "D-pad left";
                case "dpadright": return "D-pad right";
                case "leftstickpress":
                case "l3": return "L3";
                case "rightstickpress":
                case "r3": return "R3";
                default: return s;
            }
        }

        // ------------------------------------------------------------------
        // Reading
        // ------------------------------------------------------------------

        public bool Held(IList<InputDevice> devices)
        {
            return Read(devices, false);
        }

        public bool Pressed(IList<InputDevice> devices)
        {
            return Read(devices, true);
        }

        private bool Read(IList<InputDevice> devices, bool edge)
        {
            if (_broken || _paths.Count == 0 || devices == null)
                return false;

            try
            {
                for (int d = 0; d < devices.Count; d++)
                {
                    InputDevice dev = devices[d];

                    if (dev == null || !dev.added)
                        continue;

                    for (int p = 0; p < _paths.Count; p++)
                    {
                        ButtonControl b = Control(dev, _paths[p]);

                        if (b == null)
                            continue;

                        if (edge ? b.wasPressedThisFrame : b.isPressed)
                            return true;
                    }
                }
            }
            catch (Exception e)
            {
                _broken = true;

                Log.LogWarning(
                    "[" + _section + "] the key could not be read (" + e.GetType().Name + ": " +
                    e.Message + ") - it is off this launch.");
            }

            return false;
        }

        private ButtonControl Control(InputDevice dev, Path path)
        {
            if (!Matches(dev, path.layout))
                return null;

            Dictionary<string, ButtonControl> on;

            if (!_controls.TryGetValue(dev.deviceId, out on))
            {
                on = new Dictionary<string, ButtonControl>(StringComparer.OrdinalIgnoreCase);
                _controls[dev.deviceId] = on;
            }

            ButtonControl b;

            if (on.TryGetValue(path.child, out b))
                return b;

            b = dev.TryGetChildControl(path.child) as ButtonControl;
            on[path.child] = b;

            if (b == null)
            {
                Log.LogWarning(
                    "[" + _section + "] '" + path.child + "' is not a button on " +
                    dev.displayName + " (" + dev.layout + "). Check the spelling - the free " +
                    "keys and their names are in GAME KEYS.txt.");
            }

            return b;
        }

        private static bool Matches(InputDevice dev, string layout)
        {
            switch ((layout ?? "").ToLowerInvariant())
            {
                case "keyboard": return dev is Keyboard;
                case "mouse": return dev is Mouse;
                case "gamepad": return dev is Gamepad;
            }

            return string.Equals(dev.layout, layout, StringComparison.OrdinalIgnoreCase);
        }

        // ------------------------------------------------------------------
        // The player ships, and whose devices are whose
        // ------------------------------------------------------------------

        private static readonly List<Unit> _ships = new List<Unit>();
        private static readonly List<ShipInput> _inputs = new List<ShipInput>();
        private static float _shipsAt = -10f;

        // Every ship a player flies, refreshed twice a second - a ship is
        // replaced on death and at every run entry, and nothing announces it.
        public static List<Unit> Ships()
        {
            if (Time.unscaledTime - _shipsAt > 0.5f || HasDead())
                Refresh();

            return _ships;
        }

        private static bool HasDead()
        {
            for (int i = 0; i < _ships.Count; i++)
                if (_ships[i] == null)
                    return true;

            return false;
        }

        private static void Refresh()
        {
            _shipsAt = Time.unscaledTime;
            _ships.Clear();
            _inputs.Clear();

            try
            {
                foreach (ShipInput input in UnityEngine.Object.FindObjectsByType<ShipInput>(
                             FindObjectsSortMode.None))
                {
                    if (input == null)
                        continue;

                    Unit u = input.GetComponentInParent<Unit>();

                    if (u == null || _ships.Contains(u))
                        continue;

                    _ships.Add(u);
                    _inputs.Add(input);
                }
            }
            catch (Exception)
            {
                // No ships this frame; asked again next time.
            }
        }

        private static ShipInput InputOf(Unit ship)
        {
            for (int i = 0; i < _ships.Count; i++)
                if (ReferenceEquals(_ships[i], ship))
                    return _inputs[i];

            return ship != null ? ship.GetComponentInChildren<ShipInput>(true) : null;
        }

        private static readonly List<InputDevice> _all = new List<InputDevice>();
        private static readonly List<InputDevice> _one = new List<InputDevice>();

        // The devices whose keys count for this ship. Single player: all of
        // them. Co-op: the ones the game paired with this ship's player.
        public static IList<InputDevice> DevicesOf(Unit ship)
        {
            if (Ships().Count <= 1)
            {
                _all.Clear();

                foreach (InputDevice d in InputSystem.devices)
                    _all.Add(d);

                return _all;
            }

            _one.Clear();

            ShipInput input = InputOf(ship);

            if (input == null || input.PlayerInput == null)
                return _one;

            foreach (InputDevice d in input.PlayerInput.devices)
                _one.Add(d);

            return _one;
        }

        // The ship's own flying controls are on, and the game is not paused.
        public static bool Flying(Unit ship)
        {
            if (ship == null || Time.timeScale <= 0f)
                return false;

            try
            {
                ShipInput input = InputOf(ship);

                return input != null && input.isActiveAndEnabled &&
                       input.ShipControlActionMap != null &&
                       input.ShipControlActionMap.Enabled;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
