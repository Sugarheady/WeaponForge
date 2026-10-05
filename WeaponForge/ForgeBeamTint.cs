using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace WeaponForge
{
    // THE BEAM'S OWN LAYER TABLE - `ForgeMuzzleTint` for a hitscan weapon.
    //
    // R15 test 188, on a beam with an `overheat` block: *"the muzzleflare
    // changes to red not the beam or the beam effects hitting the target."*
    // Exactly right, and it is a gap rather than a bug: `overheat` and `spinUp`
    // both publish their colour to the MUZZLE, which on a bullet weapon is the
    // whole visible flash and on a beam is a small puff at the barrel with a
    // twenty-unit lance coming out of it.
    //
    // A beam's parts are a `HitscanWeaponVisual` per pellet, each holding a
    // `fireSpriteRenderer` (the stretched quad), a `warmUpSpriteRenderer` and an
    // `impactParticle` at the far end - which is the "beam effects hitting the
    // target" half of the report. All three are tinted, because all three are
    // the beam as far as anyone looking at it is concerned.
    //
    // EVERY RULE HERE IS INHERITED FROM THE MUZZLE TABLE RATHER THAN
    // REDISCOVERED, and the three that matter:
    //
    //   - **One captured base per renderer, captured once, never from a tinted
    //     state.** Two features privately remembering "the colour before I
    //     started" is the permanent-bake-in bug, and `ForgeBeam.ApplyTint`
    //     already wrote a colour onto the visual PREFAB - so the base we
    //     capture is the author's beam colour, which is what cooling has to
    //     return to.
    //   - **A weighted mix, not a chain of lerps.** `Lerp(x, c, 1)` is `c`, so
    //     a chain lets the last key alphabetically erase every layer before it.
    //     That shipped in the muzzle table and made a fully wound gun show no
    //     heat at all. With one layer the mix is `Lerp(base, c, a)` term for
    //     term, so a beam with only `overheat` on it looks the same as if this
    //     had been written per-feature.
    //   - **A no-allocation steady path.** `Set` runs every frame while a ramp
    //     is live. `visualsInstances.Count` is the churn signal - that is the
    //     list `InitializeVisuals` resizes, which `spinUp`'s pellet reward makes
    //     it do - and comparing one int costs nothing.
    //
    // Alpha is preserved on the sprite renderers (a renderer's alpha is a
    // visibility decision, never a colour) and taken from the base gradient on
    // the particles.
    public static class ForgeBeamTint
    {
        private static readonly ManualLogSource Log =
            ForgeLog.Source("WeaponForge.Tint");

        // The same two keys the muzzle table uses, on purpose: a weapon with
        // both blocks paints both surfaces with one pair of names.
        public const string Heat = ForgeMuzzleTint.Heat;
        public const string Spin = ForgeMuzzleTint.Spin;

        private static readonly FieldInfo _fVisuals =
            AccessTools.Field(typeof(HitscanWeapon), "visualsInstances");

        private class Layer
        {
            public string key;
            public Color color;
            public float amount;
        }

        private class Entry
        {
            public readonly List<SpriteRenderer> sprites =
                new List<SpriteRenderer>();
            public readonly List<Color> spriteBases = new List<Color>();

            public readonly List<ParticleSystem> systems =
                new List<ParticleSystem>();
            public readonly List<ParticleSystem.MinMaxGradient> systemBases =
                new List<ParticleSystem.MinMaxGradient>();

            public readonly List<Layer> layers = new List<Layer>();

            public int sourceCount = -1;

            public Layer Find(string key)
            {
                for (int i = 0; i < layers.Count; i++)
                {
                    if (layers[i].key == key)
                        return layers[i];
                }

                return null;
            }
        }

        private static readonly Dictionary<WeaponBase, Entry> _live =
            new Dictionary<WeaponBase, Entry>();

        private static bool _warned;

        // Safe to call with any weapon: a bullet weapon is not a HitscanWeapon
        // and falls straight out, so a caller does not have to know which kind
        // it is holding.
        public static void Set(
            WeaponBase weapon, string key, Color color, float amount)
        {
            var beam = weapon as HitscanWeapon;

            if (beam == null || string.IsNullOrEmpty(key) || _fVisuals == null)
                return;

            try
            {
                Entry e;

                if (!_live.TryGetValue(weapon, out e))
                {
                    e = new Entry();
                    _live[weapon] = e;
                }

                if (!Capture(beam, e))
                    return;

                Layer layer = e.Find(key);

                if (layer == null)
                {
                    layer = new Layer { key = key };
                    e.layers.Add(layer);

                    e.layers.Sort(
                        delegate(Layer a, Layer b)
                        {
                            return string.Compare(
                                a.key, b.key, StringComparison.Ordinal);
                        });
                }

                layer.color = color;
                layer.amount = Mathf.Clamp01(amount);

                Write(e);
            }
            catch (Exception ex)
            {
                Warn(ex);
            }
        }

        public static void Clear(WeaponBase weapon, string key)
        {
            if (weapon == null || string.IsNullOrEmpty(key))
                return;

            try
            {
                Entry e;

                if (!_live.TryGetValue(weapon, out e))
                    return;

                Layer layer = e.Find(key);

                if (layer != null)
                    e.layers.Remove(layer);

                Write(e);

                if (e.layers.Count == 0)
                    _live.Remove(weapon);
            }
            catch (Exception ex)
            {
                Warn(ex);
            }
        }

        public static void Reset()
        {
            _live.Clear();
        }

        // ------------------------------------------------------------------
        private static bool Capture(HitscanWeapon beam, Entry e)
        {
            var visuals = _fVisuals.GetValue(beam) as IList;

            if (visuals == null || visuals.Count == 0)
                return false;

            // The cheap path, for the same reason the muzzle table has one:
            // this runs once a frame for as long as a gun is hot.
            if (e.sourceCount == visuals.Count && e.sprites.Count > 0 &&
                AllLive(e))
            {
                return true;
            }

            e.sprites.Clear();
            e.spriteBases.Clear();
            e.systems.Clear();
            e.systemBases.Clear();
            e.sourceCount = visuals.Count;

            for (int i = 0; i < visuals.Count; i++)
            {
                var one = visuals[i] as Component;

                if (one == null)
                    continue;

                // By component rather than by the three private field names:
                // this has to survive a game update renaming a serialized
                // field, and there is nothing under a beam visual that is not
                // part of the beam.
                SpriteRenderer[] srs =
                    one.GetComponentsInChildren<SpriteRenderer>(true);

                for (int j = 0; j < srs.Length; j++)
                {
                    if (srs[j] == null)
                        continue;

                    e.sprites.Add(srs[j]);
                    e.spriteBases.Add(srs[j].color);
                }

                ParticleSystem[] pss =
                    one.GetComponentsInChildren<ParticleSystem>(true);

                for (int j = 0; j < pss.Length; j++)
                {
                    if (pss[j] == null)
                        continue;

                    e.systems.Add(pss[j]);
                    e.systemBases.Add(pss[j].main.startColor);
                }
            }

            return e.sprites.Count > 0 || e.systems.Count > 0;
        }

        private static bool AllLive(Entry e)
        {
            for (int i = 0; i < e.sprites.Count; i++)
            {
                if (e.sprites[i] == null)
                    return false;
            }

            for (int i = 0; i < e.systems.Count; i++)
            {
                if (e.systems[i] == null)
                    return false;
            }

            return true;
        }

        // total <= 1 : base * (1 - total) + SUM(c * a)
        // total >  1 : normalised by the total, so it saturates rather than
        //              letting whichever layer sorts last take the whole beam.
        private static void Mix(
            Entry e, Color b, out float r, out float g, out float bl)
        {
            float total = 0f, ar = 0f, ag = 0f, ab = 0f;

            for (int i = 0; i < e.layers.Count; i++)
            {
                Layer l = e.layers[i];

                if (l.amount <= 0.001f)
                    continue;

                total += l.amount;
                ar += l.color.r * l.amount;
                ag += l.color.g * l.amount;
                ab += l.color.b * l.amount;
            }

            if (total <= 0.001f)
            {
                r = b.r; g = b.g; bl = b.b;
                return;
            }

            if (total > 1f)
            {
                r = ar / total; g = ag / total; bl = ab / total;
                return;
            }

            float keep = 1f - total;

            r = b.r * keep + ar;
            g = b.g * keep + ag;
            bl = b.b * keep + ab;
        }

        private static void Write(Entry e)
        {
            bool none = e.layers.Count == 0;

            for (int i = 0; i < e.sprites.Count; i++)
            {
                SpriteRenderer sr = e.sprites[i];

                if (sr == null)
                    continue;

                Color b = e.spriteBases[i];

                if (none)
                {
                    // The captured colour back UNTOUCHED, which is what makes
                    // cooling visibly reverse. Lerping toward it by 0 would
                    // return whatever is there now.
                    sr.color = new Color(b.r, b.g, b.b, sr.color.a);
                    continue;
                }

                float r, g, bl;
                Mix(e, b, out r, out g, out bl);

                // The renderer's CURRENT alpha: a beam that is fading in or out
                // keeps doing that while it changes colour.
                sr.color = new Color(r, g, bl, sr.color.a);
            }

            for (int i = 0; i < e.systems.Count; i++)
            {
                ParticleSystem ps = e.systems[i];

                if (ps == null)
                    continue;

                ParticleSystem.MainModule main = ps.main;

                if (none)
                {
                    main.startColor = e.systemBases[i];
                    continue;
                }

                Color b = e.systemBases[i].color;

                float r, g, bl;
                Mix(e, b, out r, out g, out bl);

                main.startColor = new Color(r, g, bl, b.a);
            }
        }

        private static void Warn(Exception ex)
        {
            if (_warned)
                return;

            _warned = true;

            Log.LogError(
                "Tinting a beam failed, so an overheating or winding-up BEAM " +
                "will keep its own colour while its muzzle flash changes: " + ex);
        }
    }
}
