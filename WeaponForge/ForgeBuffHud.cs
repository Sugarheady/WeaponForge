using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.UI;

namespace WeaponForge
{
    // "Show me on screen when I'm buffed."
    //
    // The game has NO status-effect HUD. `StatusEffectForUnit` sounds like one
    // and is not - it only emits fire particles into the world, for `IsOnFire`,
    // and nothing else in the assembly draws a status anywhere.
    //
    // What it does have is `Ship.LogOutput`, which is entirely public:
    //
    //     Log(id, message, icon, duration, iconColor, textColor, flashing)
    //     Clear(id)
    //
    // and - this is the part that makes it the right answer rather than a hack -
    // **the game already uses it as a persistent indicator, not just a message
    // feed.** "Fuel low" is logged with `duration: 0` (the expiry check is
    // `duration > 0f && ...`, so zero never times out), coloured, and FLASHING,
    // then `Clear`ed when it stops applying. Pickups pass icons through the same
    // call. So an icon + label that lives exactly as long as a buff is native
    // behaviour in the place the player already looks for status.
    //
    // The icon ROW is the second style, cloned from `MinionCountWidget` (icon +
    // text + animator) - the same widget the minion counter spawns per slot.
    public static class ForgeBuffHud
    {
        private static readonly ManualLogSource Log =
            ForgeLog.Source("WeaponForge.BuffHud");

        public enum Style { Off, Feed, Icons, Both }

        // What sort of thing is being announced, and therefore which switch
        // gates it. A BUFF always shows (that is the headline feature); a STATE
        // and a DEBUFF are opt-in, because a busy fight would otherwise fill the
        // status area.
        public enum Kind { Buff, State, Debuff }

        // What the label reads. He asked for all four, defaulting to the first.
        public enum Labels { EffectAmount, Seconds, Stacks, IconOnly }

        // What is worth announcing. On-kill only by default; the other two are
        // opt-in because a busy fight would otherwise fill the feed.
        public static Style style = Style.Feed;
        public static Labels labels = Labels.EffectAmount;
        public static bool showTimedStates;
        public static bool showDebuffs;

        // Side of one icon in the row, in UI pixels.
        //
        // This needs a number because the widget is BORROWED from the minion
        // counter, and a borrowed prefab brings its own RectTransform size with
        // it. In its own container the game lays it out; dropped into our row it
        // kept its authored size and "took up most of the bottom row" - which is
        // not a scaling bug so much as the absence of any scaling at all.
        public static float iconSize = 28f;

        // Log ids. The game uses 0-5 (0 generic, 1 boss, 2 fuel, 3 respawn,
        // 4 self-destruct, 5 instrument), and Module Forge's copy of this owns a
        // different block, so neither mod can clear the other's entries - or the
        // game's.
        private const int IdBase = 9100;
        private const int IdLimit = 9160;

        // Shared by the BepInEx config and the per-weapon JSON override, so both
        // spell the same words.
        public static Style ParseStyle(string s)
        {
            switch ((s ?? "").Trim().ToLowerInvariant())
            {
                case "off":
                case "none":
                case "false": return Style.Off;
                case "icons":
                case "icon":
                case "row":   return Style.Icons;
                case "both":  return Style.Both;
                default:      return Style.Feed;
            }
        }

        public static Labels ParseLabels(string s)
        {
            switch ((s ?? "").Trim().ToLowerInvariant())
            {
                case "seconds":
                case "time":
                case "countdown": return Labels.Seconds;
                case "stacks":
                case "count":     return Labels.Stacks;
                case "icon":
                case "icononly":
                case "none":      return Labels.IconOnly;
                default:          return Labels.EffectAmount;
            }
        }

        private class Entry
        {
            public int id;
            public Sprite icon;
            public Color color;
            public string text;        // what is currently on screen
            public float endsAt;
            public bool debuff;
            public GameObject widget;  // icon-row instance, if any
            public object textComponent;
        }

        private static readonly Dictionary<object, Entry> _live =
            new Dictionary<object, Entry>();

        private static int _nextId = IdBase;

        // ------------------------------------------------------------------
        // The live HUD, captured by ForgeBuffHudPatch
        // ------------------------------------------------------------------

        private static ShipHud _hud;
        private static Ship _ship;
        private static Transform _row;
        private static MinionCountWidget _widgetPrefab;
        private static bool _prefabLooked;

        // The Unit the HUD belongs to - what the state poller compares against,
        // so a shield or a fire on some other unit is not reported as yours.
        public static Unit BoundUnit
        {
            get { return (_ship != null) ? _ship.Unit : null; }
        }

        public static void Bind(ShipHud hud, Ship ship)
        {
            _hud = hud;
            _ship = ship;

            // A fresh HUD means the old row and every widget in it are gone.
            _row = null;

            foreach (var e in _live.Values)
            {
                e.widget = null;
                e.textComponent = null;
            }

            _live.Clear();
            _noticeUntil.Clear();

            // A new HUD means new rows; the old ShipLogEntry objects belong to
            // a display that no longer exists.
            _logged.Clear();
        }

        public static void Unbind()
        {
            if (_subscribed != null)
            {
                _subscribed.LogAdded -= OnLogAdded;
                _subscribed = null;
            }

            _hud = null;
            _ship = null;
            _row = null;
            _live.Clear();
            _noticeUntil.Clear();
            _logged.Clear();
        }

        // Whether it is safe to touch the log at all.
        //
        // THE TRAP: `ShipLogOutput.Clear` and `Update` invoke `LogRemoved`
        // WITHOUT a null check, unlike `Log`, which guards it carefully. So
        // clearing an entry while no ShipLogDisplay is subscribed throws an
        // NRE from inside the game's own code. A live ShipHud is what
        // guarantees a subscriber (`ShipLogDisplay.Assign` wires it up), so
        // everything here goes through this gate.
        private static bool FeedReady
        {
            get
            {
                return _ship != null && _hud != null && _ship.LogOutput != null;
            }
        }

        // ------------------------------------------------------------------
        // The API the buff features call
        // ------------------------------------------------------------------

        // `key` identifies one indicator - pass the weapon, the module, whatever
        // owns the state. Calling again with the same key updates in place.
        // An on/off state with no percentage and no clock - a raised shield, a
        // live ring, a latched harpoon, burning. Everything the feed needs is
        // the label and a colour.
        public static void ShowState(
            object key, Color color, string label, Kind kind)
        {
            Show(key, null, color, label, 0f, 1, 0f, kind);
        }

        public static void Show(
            object key, Sprite icon, Color color, string effect,
            float amount, int stacks, float endsAt, Kind kind)
        {
            if (style == Style.Off || key == null)
                return;

            if (kind == Kind.State && !showTimedStates)
                return;

            if (kind == Kind.Debuff && !showDebuffs)
                return;

            try
            {
                Entry e;

                if (!_live.TryGetValue(key, out e))
                {
                    e = new Entry();
                    e.id = NextId();
                    _live[key] = e;
                }

                e.icon = icon;
                e.color = color;
                e.endsAt = endsAt;

                // Flashing is reserved for things working against you, matching
                // the game's own use of it for "Fuel low".
                e.debuff = kind == Kind.Debuff;

                string text = Compose(effect, amount, stacks, endsAt);

                // Only repaint when the words actually change. ShipLogDisplay
                // plays its new-message sound on EVERY entry it shows, so
                // re-logging each frame would chirp continuously - and with
                // "seconds remaining" the text changes about once a second,
                // which is exactly the cadence that stays tolerable.
                if (text == e.text && Painted(e))
                    return;

                e.text = text;

                if (style == Style.Feed || style == Style.Both)
                    Feed(e);

                if (style == Style.Icons || style == Style.Both)
                    Icons(e);
            }
            catch (Exception ex)
            {
                Log.LogError("Buff indicator failed: " + ex);
            }
        }

        // Is this entry's current text already on screen, everywhere the style
        // draws it? The old test asked only for the ICON widget, which the
        // "feed" style never builds - so under that style it was never true and
        // every call re-fed a line whose words had not changed. The in-place
        // rewrite made that cheap rather than a flood, but it was still a
        // component walk per line per frame for nothing.
        private static bool Painted(Entry e)
        {
            bool feed = style == Style.Feed || style == Style.Both;
            bool icons = style == Style.Icons || style == Style.Both;

            return (!feed || _logged.ContainsKey(e.id)) &&
                   (!icons || e.widget != null);
        }

        public static void Clear(object key)
        {
            if (key == null)
                return;

            // A notice cleared early (its owner went away) must not be swept
            // again later - by then the key could be showing something else.
            _noticeUntil.Remove(key);

            Entry e;

            if (!_live.TryGetValue(key, out e))
                return;

            _live.Remove(key);

            try
            {
                if (FeedReady)
                    _ship.LogOutput.Clear(e.id);
            }
            catch (Exception ex)
            {
                Log.LogError("Clearing a buff indicator failed: " + ex);
            }

            // The row is gone, so the cached entry must go with it or the next
            // Show would try to rewrite text on a destroyed object.
            _logged.Remove(e.id);

            if (e.widget != null)
                UnityEngine.Object.Destroy(e.widget);
        }

        // ------------------------------------------------------------------
        // Lines that take themselves down
        // ------------------------------------------------------------------
        //
        // ★ `endsAt` NEVER CLEARED ANYTHING. It only feeds the seconds-left
        // label; every feature that shows a line is also the one that clears
        // it (the kill buff on its own timer, a shield when it drops). Rank
        // showed a line on every rank up and nothing ever took it down - R19
        // 252: "the alert for the rank number is still permenant. It needs to
        // go away. Have it stay for like 3 to 4 seconds". A NOTICE is a
        // different kind of line from a buff: there is no state behind it to
        // end, so it has to end itself.
        public static void Notice(
            object key, Color color, string text, int stacks, float seconds)
        {
            if (style == Style.Off || key == null)
                return;

            float until = Time.time + Mathf.Max(0.5f, seconds);

            Show(key, null, color, text, 0f, stacks, until, Kind.Buff);

            _noticeUntil[key] = until;
            ForgeBuffHudClock.Ensure();
        }

        // ★ AN ALERT IS A NOTICE THAT IGNORES `[Buff indicator] Style`.
        //
        // Drone modes (2026-10-01): the alert is the ONLY thing that says which
        // mode a press chose (his answer, 12.2 L: "alert only"), so it cannot
        // depend on an opt-in overlay that ships OFF - a `Notice` under the
        // default `Style = off` returns before drawing anything. An alert goes
        // to the game's own status feed whatever the style is, with its text
        // exactly as given (no "+0%", no countdown), under ONE key so pressing
        // three times quickly rewrites one row rather than stacking three.
        // It takes itself down through the same sweep as a notice.
        public static void Alert(object key, Color color, string text, float seconds)
        {
            if (key == null || string.IsNullOrEmpty(text))
                return;

            try
            {
                Entry e;

                if (!_live.TryGetValue(key, out e))
                {
                    e = new Entry();
                    e.id = NextId();
                    _live[key] = e;
                }

                e.icon = null;
                e.color = color;
                e.debuff = false;

                float until = Time.time + Mathf.Max(0.5f, seconds);
                e.endsAt = until;

                if (text != e.text || !_logged.ContainsKey(e.id))
                {
                    e.text = text;
                    Feed(e);
                }

                _noticeUntil[key] = until;
                ForgeBuffHudClock.Ensure();
            }
            catch (Exception ex)
            {
                Log.LogError("Alert failed: " + ex);
            }
        }

        private static readonly Dictionary<object, float> _noticeUntil =
            new Dictionary<object, float>();

        // Called once a frame by ForgeBuffHudClock while a notice is up.
        // Returns whether any are left, so the clock can stop.
        internal static bool SweepNotices()
        {
            if (_noticeUntil.Count == 0)
                return false;

            List<object> done = null;

            foreach (var kv in _noticeUntil)
            {
                if (Time.time >= kv.Value)
                {
                    if (done == null)
                        done = new List<object>();

                    done.Add(kv.Key);
                }
            }

            if (done != null)
            {
                for (int i = 0; i < done.Count; i++)
                {
                    _noticeUntil.Remove(done[i]);
                    Clear(done[i]);
                }
            }

            return _noticeUntil.Count > 0;
        }

        // Seconds-remaining labels need a nudge every second even when nothing
        // about the buff changed. Called by the buff ticker.
        public static void Refresh(object key, float endsAt)
        {
            if (labels != Labels.Seconds || key == null)
                return;

            Entry e;

            if (_live.TryGetValue(key, out e))
                e.endsAt = endsAt;
        }

        private static int NextId()
        {
            int id = _nextId++;

            if (_nextId >= IdLimit)
                _nextId = IdBase;

            return id;
        }

        private static string Compose(
            string effect, float amount, int stacks, float endsAt)
        {
            switch (labels)
            {
                case Labels.IconOnly:
                    return "";

                case Labels.Stacks:
                    return stacks > 1
                        ? (effect + " x" + stacks)
                        : effect;

                case Labels.Seconds:
                    // A state has no clock to count down - a shield stays up
                    // until you drop it - so it just names itself.
                    if (endsAt <= 0f)
                        return effect;

                    float left = Mathf.Max(0f, endsAt - Time.time);
                    return effect + " " + left.ToString("0.#") + "s";

                default:
                    // Likewise no "+0%" on something that has no percentage.
                    string s = (amount > 0f)
                        ? (effect + " +" + Mathf.RoundToInt(amount * 100f) + "%")
                        : effect;

                    return stacks > 1 ? (s + " x" + stacks) : s;
            }
        }

        // ------------------------------------------------------------------
        // Style 1: the game's own status feed
        // ------------------------------------------------------------------

        // How many feed lines this mod may occupy at once. A backstop, not a
        // tuning knob: whatever goes wrong upstream, the status feed must never
        // grow over the ammo / fuel / health readouts.
        public static int maxLines = 3;

        // Our own ShipLogEntry per id, captured from LogAdded. Needed because
        // ShipLogOutput.Log does not hand the entry back and the display keys
        // its rows on the instance.
        private static readonly Dictionary<int, ShipLogEntry> _logged =
            new Dictionary<int, ShipLogEntry>();

        private static ShipLogOutput _subscribed;

        private static void Feed(Entry e)
        {
            if (!FeedReady)
                return;

            Watch();

            // ---- update the row that already exists, in place ----
            //
            // THIS IS THE WHOLE FIX for "a TON of them popped up at the same
            // time. LIKE ALOT." Re-logging an id does NOT replace its row:
            //
            //   * ShipLogOutput.Log ALWAYS appends a new ShipLogEntry - there is
            //     no replace-by-id anywhere in it;
            //   * ShipLogDisplay.Show then INSTANTIATES a fresh LogEntry row and
            //     plays newMessageSfx;
            //   * and the Clear() that was supposed to remove the old one calls
            //     LogEntry.Hide(), which is `async void` - it runs a close
            //     animation and destroys the row LATER.
            //
            // So every text change (a new stack, or the seconds ticking down)
            // stacked a new row on top of one still fading out, and chirped
            // while doing it. Updating the live row's text instead is silent,
            // allocates nothing, and can never grow the feed.
            if (TryUpdateRow(e))
                return;

            // ---- otherwise log it once ----
            if (_logged.Count >= Mathf.Max(1, maxLines) &&
                !_logged.ContainsKey(e.id))
            {
                return;
            }

            ColorAsset tint = Tint(e.color);

            // duration 0 = never auto-expires, so the entry lives exactly as
            // long as the buff and we own its removal. Flashing is reserved for
            // debuffs, matching the game's use of it for "Fuel low".
            _ship.LogOutput.Clear(e.id);
            _logged.Remove(e.id);

            _ship.LogOutput.Log(
                e.id,
                string.IsNullOrEmpty(e.text) ? " " : e.text,
                e.icon, 0f, tint, tint, e.debuff);
        }

        // Subscribe once per ship so we know which ShipLogEntry is ours.
        private static void Watch()
        {
            if (ReferenceEquals(_subscribed, _ship.LogOutput))
                return;

            if (_subscribed != null)
                _subscribed.LogAdded -= OnLogAdded;

            _subscribed = _ship.LogOutput;
            _subscribed.LogAdded += OnLogAdded;
        }

        private static void OnLogAdded(ShipLogEntry entry)
        {
            if (entry == null || entry.id < IdBase || entry.id >= IdLimit)
                return;

            _logged[entry.id] = entry;
        }

        // Rewrite the text on the row the game already built for this id.
        // Returns false if there is no such row, in which case the caller logs
        // one normally.
        private static bool TryUpdateRow(Entry e)
        {
            try
            {
                ShipLogEntry logged;

                if (!_logged.TryGetValue(e.id, out logged) || logged == null)
                    return false;

                if (_hud == null || _hud.logDisplay == null)
                    return false;

                // ShipHud.logDisplay is public; only the row dictionary inside
                // it is private.
                if (_rowsField == null)
                {
                    _rowsField = HarmonyLib.AccessTools.Field(
                        typeof(ShipLogDisplay), "logEntries");
                }

                if (_rowsField == null)
                    return false;

                var rows = _rowsField.GetValue(_hud.logDisplay)
                    as IDictionary;

                if (rows == null || !rows.Contains(logged))
                {
                    // The row is gone (a scene change, or the game cleared it),
                    // so the cached entry is stale.
                    _logged.Remove(e.id);
                    return false;
                }

                var row = rows[logged] as Component;

                if (row == null)
                    return false;

                SetTextOn(row.gameObject, e.text);
                return true;
            }
            catch (Exception ex)
            {
                Log.LogWarning(
                    "Could not update a buff line in place (" + ex.Message +
                    ") - falling back to re-logging it.");
                return false;
            }
        }

        private static FieldInfo _rowsField;

        // ColorAsset is a ScriptableObject wrapping one Color, with an implicit
        // conversion - so a runtime instance is a perfectly ordinary one.
        private static readonly Dictionary<int, ColorAsset> _tints =
            new Dictionary<int, ColorAsset>();

        private static ColorAsset Tint(Color c)
        {
            int key = ((Color32)c).r << 16 | ((Color32)c).g << 8 |
                      ((Color32)c).b;

            ColorAsset asset;

            if (_tints.TryGetValue(key, out asset) && asset != null)
                return asset;

            asset = ScriptableObject.CreateInstance<ColorAsset>();
            asset.color = c;
            UnityEngine.Object.DontDestroyOnLoad(asset);
            _tints[key] = asset;
            return asset;
        }

        // ------------------------------------------------------------------
        // Style 2: a row of icons
        // ------------------------------------------------------------------

        // Deliberately NOT parented into `resourceBarParent`: ShipHud's
        // RefreshResourcePanelSize walks every child of that transform and
        // rewrites its anchoredPosition into a vertical stack, so a guest there
        // gets shuffled about by the game. The minions widget's parent is a
        // neighbour nothing else lays out.
        //
        // The row name is shared with Module Forge on purpose - the same
        // one-shared-string trick the loot pools use - so a player with both
        // mods gets ONE row of icons rather than two rows.
        public const string RowName = "Forge Buff Indicators";

        private static Transform Row()
        {
            if (_row != null)
                return _row;

            if (_hud == null || _hud.minionsWidget == null)
                return null;

            Transform parent = _hud.minionsWidget.transform.parent;

            if (parent == null)
                return null;

            Transform existing = parent.Find(RowName);

            if (existing != null)
            {
                _row = existing;
                return _row;
            }

            var go = new GameObject(RowName, typeof(RectTransform));
            go.transform.SetParent(parent, false);

            var layout = go.AddComponent<HorizontalLayoutGroup>();
            layout.spacing = 4f;
            layout.childForceExpandWidth = false;
            layout.childForceExpandHeight = false;

            // childControl* must be ON for a child's LayoutElement to be
            // honoured at all. With it off (the default) the group leaves every
            // child at whatever size its prefab was authored with, which is
            // exactly why the borrowed widget arrived enormous.
            layout.childControlWidth = true;
            layout.childControlHeight = true;

            _row = go.transform;
            return _row;
        }

        private static MinionCountWidget WidgetPrefab()
        {
            if (_prefabLooked)
                return _widgetPrefab;

            _prefabLooked = true;

            // Borrowed rather than built: this prefab is already an icon + a
            // count text + an animator, styled to match the HUD. Building an
            // equivalent by hand would need a font, a sprite and a material,
            // and would still look foreign.
            try
            {
                if (_hud != null && _hud.minionsWidget != null)
                {
                    FieldInfo f = HarmonyLib.AccessTools.Field(
                        typeof(MinionsWidget), "minionCountWidgetPrefab");

                    if (f != null)
                    {
                        _widgetPrefab =
                            f.GetValue(_hud.minionsWidget) as MinionCountWidget;
                    }
                }
            }
            catch (Exception e)
            {
                Log.LogWarning(
                    "Could not borrow the minion count widget (" + e.Message +
                    ") - the icon row is unavailable, so buff indicators will " +
                    "use the status feed instead.");
            }

            return _widgetPrefab;
        }

        private static void Icons(Entry e)
        {
            Transform row = Row();
            MinionCountWidget prefab = WidgetPrefab();

            if (row == null || prefab == null)
            {
                // No row available: make sure the player still gets told.
                if (style == Style.Icons)
                    Feed(e);

                return;
            }

            if (e.widget == null)
            {
                MinionCountWidget w =
                    UnityEngine.Object.Instantiate(prefab, row);

                e.widget = w.gameObject;
                e.widget.SetActive(true);

                Size(e.widget);
            }

            Paint(e);
        }

        // Pin one borrowed widget to a sane size. Both halves are needed: the
        // LayoutElement is what the HorizontalLayoutGroup reads, and the
        // RectTransform is what shows if the group is ever missing.
        private static void Size(GameObject go)
        {
            float side = Mathf.Clamp(iconSize, 8f, 128f);

            var le = go.GetComponent<LayoutElement>();

            if (le == null)
                le = go.AddComponent<LayoutElement>();

            le.preferredWidth = side;
            le.preferredHeight = side;
            le.minWidth = side;
            le.minHeight = side;
            le.flexibleWidth = 0f;
            le.flexibleHeight = 0f;

            var rt = go.transform as RectTransform;

            if (rt != null)
                rt.sizeDelta = new Vector2(side, side);
        }

        // The widget's own fields are private and its SetIcon takes a Module,
        // which a weapon buff has no business constructing - so the two child
        // components are addressed directly.
        private static void Paint(Entry e)
        {
            if (e.widget == null)
                return;

            var image = FindIcon(e.widget);

            if (image != null)
            {
                if (e.icon != null)
                    image.sprite = e.icon;

                image.color = e.color;
            }

            SetText(e, e.text);
        }

        private static Image FindIcon(GameObject go)
        {
            Image[] all = go.GetComponentsInChildren<Image>(true);

            // The icon is the first Image that is not the widget's own
            // background; the prefab puts the sprite on a child.
            for (int i = 0; i < all.Length; i++)
                if (all[i] != null && all[i].gameObject != go)
                    return all[i];

            return all.Length > 0 ? all[0] : null;
        }

        // Set the text on any object that has a text component somewhere under
        // it. TMP_Text lives in an assembly this project does not reference, so
        // the component is found by type NAME and written through reflection.
        private static void SetTextOn(GameObject go, string text)
        {
            if (go == null)
                return;

            foreach (Component c in go.GetComponentsInChildren<Component>(true))
            {
                if (c == null)
                    continue;

                string n = c.GetType().Name;

                if (n != "TextMeshProUGUI" && n != "TMP_Text" && !(c is Text))
                    continue;

                PropertyInfo p = c.GetType().GetProperty("text");

                if (p == null)
                    continue;

                // The game wraps its own message in <uppercase> tags; matching
                // that keeps a re-written line looking like every other one.
                p.SetValue(
                    c,
                    "<uppercase>" + (text ?? "") + "</uppercase>",
                    null);

                return;
            }
        }

        // TMP_Text lives in a separate assembly this project does not reference,
        // so the text is set through reflection on whatever component is there.
        private static void SetText(Entry e, string text)
        {
            try
            {
                if (e.textComponent == null)
                {
                    foreach (Component c in
                             e.widget.GetComponentsInChildren<Component>(true))
                    {
                        if (c == null)
                            continue;

                        if (c.GetType().Name == "TextMeshProUGUI" ||
                            c.GetType().Name == "TMP_Text" ||
                            c is Text)
                        {
                            e.textComponent = c;
                            break;
                        }
                    }
                }

                if (e.textComponent == null)
                    return;

                PropertyInfo p =
                    e.textComponent.GetType().GetProperty("text");

                if (p != null)
                    p.SetValue(e.textComponent, text ?? "", null);
            }
            catch { }
        }
    }
    // The one per-frame tick the HUD needs - for notices, and nothing else.
    // Its own hidden object, created by the first notice and destroyed as soon
    // as none are waiting, so between rank ups it does not exist at all.
    internal class ForgeBuffHudClock : MonoBehaviour
    {
        private static ForgeBuffHudClock _one;

        public static void Ensure()
        {
            if (_one != null)
                return;

            var go = new GameObject("ForgeBuffHudClock");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            _one = go.AddComponent<ForgeBuffHudClock>();
        }

        private void Update()
        {
            bool more;

            try
            {
                more = ForgeBuffHud.SweepNotices();
            }
            catch (System.Exception)
            {
                // A readout must never be able to throw every frame.
                more = false;
            }

            if (!more)
            {
                _one = null;
                Destroy(gameObject);
            }
        }
    }
}
