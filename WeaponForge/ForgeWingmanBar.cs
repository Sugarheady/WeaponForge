using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace WeaponForge
{
    // A drone companion's health bar, three ways the game's own fields cannot:
    //
    //   rows    "wrapped the width but not the height ... add total number of
    //           bar height as well or how many bars to have" - R19 test 278.
    //           The game wraps at a SEGMENT count (`maxUnitsPerRow`), so a
    //           row count is turned into one from the companion's own health.
    //   scale   the whole widget, both ways. Wrapping trades width for height;
    //           only scale makes the bar smaller.
    //   flash   "show for a moment (like 2 or 3 seconds) then fade away
    //           again". The game never hides a bar once it has appeared, so
    //           this fades it out a few seconds after the LAST hit and puts it
    //           back on the next one.
    //
    // ★ IT ACTS ON THE WIDGET, AND THE WIDGET IS NOT THERE YET. The game makes
    // it in `HealthbarManager.Add` (on the owner's Start) or on the first hit
    // with `showWhenFull` off - and REMAKES the rows inside it whenever the
    // stats recalculate. So this looks the widget up every frame through the
    // manager's own table rather than holding on to one, and re-applies what
    // it has to. A cosmetic that throws must cost nothing but itself: every
    // step is caught.
    public class ForgeWingmanBar : MonoBehaviour
    {
        private static readonly ManualLogSource Log =
            ForgeLog.Source("WeaponForge.Wingman");

        private HealthbarOwner _owner;
        private int _rows;
        private float _scale = 1f;
        private float _seconds;          // > 0 means "flash"

        private bool _rowsDone;
        private float _visibleUntil = -1f;
        private bool _fading;
        private float _fadeStart;
        private float _hideAt;
        private GameObject _hidden;      // the widget we switched off

        private static FieldInfo _widgetsField;
        private static FieldInfo _perRowField;
        private static bool _saidFailed;

        // How long the bar takes to fade away after its seconds are up.
        private const float FadeOut = 0.6f;

        // What every faded thing looked like before the fade, so it can be put
        // back exactly - read once, never from a faded state.
        private readonly Dictionary<SpriteRenderer, Color> _spriteBase =
            new Dictionary<SpriteRenderer, Color>();

        private readonly Dictionary<Material, Color[]> _materialBase =
            new Dictionary<Material, Color[]>();

        // The ResourceBar shader's colour slots, measured off
        // `ResourceBar.mat` (the export cannot decompile the shader itself).
        private static readonly string[] BarColours =
            { "_Color", "_ResourceColor", "_ResourceColorEmpty", "_BackgroundColor" };

        public static void Attach(Unit body, int rows, float scale, float seconds)
        {
            if (body == null)
                return;

            HealthbarOwner hb = body.GetComponentInChildren<HealthbarOwner>(true);

            if (hb == null)
                return;

            var bar = hb.gameObject.GetComponent<ForgeWingmanBar>();

            if (bar == null)
                bar = hb.gameObject.AddComponent<ForgeWingmanBar>();

            if (bar._owner != null)
                bar._owner.Damaged -= bar.OnDamaged;

            bar._owner = hb;
            bar._rows = Mathf.Max(0, rows);
            bar._scale = Mathf.Clamp(scale, 0.1f, 3f);
            bar._seconds = Mathf.Max(0f, seconds);
            bar._rowsDone = false;

            // `Damaged` is the game's own public event, raised exactly when the
            // manager would show the bar - so "a hit puts it back" costs no
            // patch at all.
            if (bar._seconds > 0f)
                hb.Damaged += bar.OnDamaged;
        }

        private void OnDestroy()
        {
            if (_owner != null)
                _owner.Damaged -= OnDamaged;
        }

        private void OnDamaged(HealthbarOwner owner)
        {
            _visibleUntil = Time.time + _seconds;
            _fading = false;

            try
            {
                GameObject w = _hidden != null ? _hidden : Widget();

                if (w == null)
                    return;

                Restore(w);

                if (!w.activeSelf)
                    w.SetActive(true);

                _hidden = null;
            }
            catch (Exception e)
            {
                Failed(e);
            }
        }

        private void LateUpdate()
        {
            if (_owner == null)
                return;

            try
            {
                if (_rows > 0 && !_rowsDone)
                    ApplyRows();

                GameObject w = Widget();

                if (w == null)
                    return;

                Vector3 want = Vector3.one * _scale;

                if (w.transform.localScale != want)
                    w.transform.localScale = want;

                if (_seconds <= 0f)
                    return;

                // A bar the game made without a hit (Start on a body that came
                // back hurt from a save) gets the same few seconds a hit does.
                if (_visibleUntil < 0f)
                    _visibleUntil = Time.time + _seconds;

                if (!_fading && Time.time >= _visibleUntil && w.activeSelf)
                {
                    _fading = true;
                    _fadeStart = Time.time;
                    _hideAt = Time.time + FadeOut;
                }

                if (_fading)
                {
                    if (Time.time >= _hideAt)
                    {
                        _fading = false;
                        _hidden = w;
                        w.SetActive(false);

                        // Put the colours back while it is out of sight, so the
                        // next hit shows it whole.
                        Restore(w);
                    }
                    else
                    {
                        Alpha(w, 1f - (Time.time - _fadeStart) / FadeOut);
                    }
                }
            }
            catch (Exception e)
            {
                Failed(e);
            }
        }

        // "How many rows" -> the segments-per-row the game actually reads. Done
        // once the bar knows what it displays (the owner's Start fills that),
        // then the bar is re-registered so the manager rebuilds it with the new
        // number - the same Remove + Add the body code already does.
        private void ApplyRows()
        {
            if (_owner.Resources == null || _owner.Resources.Count == 0)
                return;

            float capacity = 0f;

            foreach (Resource r in _owner.Resources)
            {
                ResourceTank tank = (_owner.Unit != null) ? _owner.Unit.GetTank(r) : null;

                if (tank != null)
                    capacity = Mathf.Max(capacity, tank.Capacity);
            }

            _rowsDone = true;

            if (capacity <= 0f)
                return;

            int perRow = Mathf.Max(1, Mathf.CeilToInt(capacity / _rows));

            if (_perRowField == null)
                _perRowField = AccessTools.Field(typeof(HealthbarOwner), "maxUnitsPerRow");

            if (_perRowField == null)
                return;

            _perRowField.SetValue(_owner, perRow);

            HealthbarManager mgr;

            if (ServiceLocator.TryGet<HealthbarManager>(out mgr) && mgr != null)
            {
                mgr.Remove(_owner);
                mgr.Add(_owner);
            }
        }

        // The widget the manager made for this bar, or null.
        private GameObject Widget()
        {
            HealthbarManager mgr;

            if (!ServiceLocator.TryGet<HealthbarManager>(out mgr) || mgr == null)
                return null;

            if (_widgetsField == null)
                _widgetsField = AccessTools.Field(typeof(HealthbarManager), "healthbarWidgets");

            var table = (_widgetsField != null) ? _widgetsField.GetValue(mgr) as IDictionary : null;

            if (table == null || !table.Contains(_owner))
                return _hidden;

            var widget = table[_owner] as Component;

            return (widget != null) ? widget.gameObject : null;
        }

        // ★ R20 TEST 278: "it goes away after about 3 seconds ... it would be
        // nice to have it fade instead of just disappearing." It was meant to
        // fade, with `Graphic.CrossFadeAlpha` - and that fades a UI Graphic's
        // VERTEX alpha, which this bar hardly uses. Measured off the game's
        // own classes: each row is a RawImage drawn by the `Punk/ResourceBar`
        // material, which colours its segments from `_ResourceColor` /
        // `_ResourceColorEmpty` (per-row material instances, made in
        // `ResourceBarRow.Awake`), and the segments that just changed are
        // `ResourceUnit`s - world SpriteRenderers, which no UI fade reaches. So
        // the fade ran on nothing visible and the bar popped out at the end.
        //
        // Now every layer that draws it is faded together, from a captured
        // base: the Graphics' own alpha, the bar material's colour slots, and
        // the unit sprites. Only materials whose shader is the ResourceBar one
        // are touched - a plain UI Graphic's material is the SHARED default,
        // and changing that would fade every menu in the game.
        private void Alpha(GameObject w, float a)
        {
            a = Mathf.Clamp01(a);

            foreach (Graphic g in w.GetComponentsInChildren<Graphic>(true))
            {
                if (g == null)
                    continue;

                // Duration 0 sets the vertex alpha at once. `CanvasRenderer`
                // itself lives in UnityEngine.UIModule, which this csproj does
                // not reference.
                g.CrossFadeAlpha(a, 0f, true);

                Material m = g.material;

                if (m == null || m.shader == null ||
                    m.shader.name.IndexOf("ResourceBar", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                Color[] baseColours;

                if (!_materialBase.TryGetValue(m, out baseColours))
                {
                    baseColours = new Color[BarColours.Length];

                    for (int i = 0; i < BarColours.Length; i++)
                    {
                        baseColours[i] = m.HasProperty(BarColours[i])
                            ? m.GetColor(BarColours[i])
                            : Color.clear;
                    }

                    _materialBase[m] = baseColours;
                }

                for (int i = 0; i < BarColours.Length; i++)
                {
                    if (!m.HasProperty(BarColours[i]))
                        continue;

                    Color c = baseColours[i];
                    c.a *= a;
                    m.SetColor(BarColours[i], c);
                }
            }

            foreach (SpriteRenderer sr in w.GetComponentsInChildren<SpriteRenderer>(true))
            {
                if (sr == null)
                    continue;

                Color baseColour;

                if (!_spriteBase.TryGetValue(sr, out baseColour))
                {
                    baseColour = sr.color;
                    _spriteBase[sr] = baseColour;
                }

                Color c = baseColour;
                c.a *= a;
                sr.color = c;
            }
        }

        // Everything back to how it looked before the fade began.
        private void Restore(GameObject w)
        {
            if (w != null)
            {
                foreach (Graphic g in w.GetComponentsInChildren<Graphic>(true))
                {
                    if (g != null)
                        g.CrossFadeAlpha(1f, 0f, true);
                }
            }

            foreach (var pair in _materialBase)
            {
                if (pair.Key == null)
                    continue;

                for (int i = 0; i < BarColours.Length; i++)
                {
                    if (pair.Key.HasProperty(BarColours[i]))
                        pair.Key.SetColor(BarColours[i], pair.Value[i]);
                }
            }

            foreach (var pair in _spriteBase)
            {
                if (pair.Key != null)
                    pair.Key.color = pair.Value;
            }

            _materialBase.Clear();
            _spriteBase.Clear();
        }

        private static void Failed(Exception e)
        {
            if (_saidFailed)
                return;

            _saidFailed = true;

            Log.LogWarning(
                "wingman: a drone health bar could not be reshaped (" +
                e.GetType().Name + ": " + e.Message + ") - it is left as the " +
                "game draws it. Said once per run.");
        }
    }
}
