using System;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;

namespace WeaponForge
{
    // ONE OWNER FOR A UNIT'S SPRITE COLOUR.
    //
    // WHY THIS EXISTS. Before it, `slow` tinted a unit by walking its sprite
    // renderers, remembering each `sr.color`, and writing the remembered value
    // back when the debuff ended. That is correct for exactly one tint at a
    // time and silently wrong for two:
    //
    //     slow starts   remembers ORIGINAL,        writes ORIGINAL * blue
    //     slam starts   remembers ORIGINAL*blue,   writes ORIGINAL * blue * red
    //     slow ends     writes back ORIGINAL        <- the red is gone
    //     slam ends     writes back ORIGINAL*blue   <- the BLUE IS NOW PERMANENT
    //
    // The unit stays blue for the rest of the run. That is not a tint fighting
    // a tint, it is a save/restore pair capturing an already-modified value -
    // and it gets worse the more features join in. Which is exactly why the
    // slam tint was offered and deliberately NOT built until this existed.
    //
    // THE FIX IS THE SAME SHAPE AS ForgeDamageStack. Keep ONE captured base per
    // renderer, captured once and never from a tinted state, and let every
    // feature publish a named LAYER. The result is the base times the product
    // of every live layer. Multiplication is commutative, so two features can
    // never disagree about who ran last - there is no ordering to get wrong,
    // and no restore path that can bake somebody else's colour in.
    //
    // ALPHA IS NEVER TOUCHED. A renderer's alpha is a visibility decision, not
    // a colour: every Larva and Maggot carries a full-body `HighlightSprite`
    // held at alpha 0 that the damage flash drives, and painting that opaque is
    // what once made a recoloured minion wear its own outline. So layers carry
    // RGB only, and each write preserves whatever alpha the renderer has AT
    // THAT MOMENT - which also means `fade` can drive alpha independently and
    // the two compose instead of fighting.
    public class ForgeUnitTint : MonoBehaviour
    {
        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("WeaponForge.Tint");

        // Keys used by the mod's own features. Strings rather than an enum so a
        // new feature needs no change here.
        public const string Slow = "slow";
        public const string Slam = "slam";
        public const string Contact = "contact";

        private class Layer
        {
            public string key;
            public Color color;
            public float endsAt;      // 0 = until explicitly cleared
            public float pulseHz;     // 0 = steady

            // HOW HARD THE COLOUR COMES THROUGH, 0..1.
            //
            // His R13 ask: *"For the tint would it be possible to change the
            // intensity of the colors changes ... I would like the ability to
            // change how intense the color comes through or how strong the
            // opacity is. I would like to do that for all the weapons that
            // apply a tint."*
            //
            // It costs almost nothing here because `Product` was ALREADY
            // lerping from "no tint" toward the colour - that is how the pulse
            // works. The pulse was simply the only thing allowed to drive that
            // lerp. Giving the layer its own strength and multiplying the two
            // together makes one knob out of what was an internal detail, and
            // it applies to every tinting feature at once rather than being
            // reimplemented eight times.
            //
            // 1 is exactly today's look, which is why it is the default
            // everywhere - nothing already written changes.
            public float strength = 1f;
        }

        private readonly List<Layer> _layers = new List<Layer>();

        private readonly List<SpriteRenderer> _renderers =
            new List<SpriteRenderer>();

        private readonly List<Color> _base = new List<Color>();

        private bool _captured;

        // ------------------------------------------------------------------
        // Publishing a layer
        // ------------------------------------------------------------------

        // `seconds` of 0 or less means "until Clear", which is what a held
        // effect wants. Calling this again with the same key REPLACES that
        // layer rather than adding another - being hit twice by the same
        // weapon should not double the colour.
        public static void Set(
            Unit unit, string key, Color color, float seconds, float pulseHz)
        {
            Set(unit, key, color, seconds, pulseHz, 1f);
        }

        // `strength` is 0..1: 0 leaves the unit exactly as it was, 1 is the
        // full colour. Every feature passes its own, so an author can have a
        // faint slow tint and a hard stun tint on the same unit.
        public static void Set(
            Unit unit, string key, Color color, float seconds, float pulseHz,
            float strength)
        {
            if (unit == null || string.IsNullOrEmpty(key))
                return;

            try
            {
                ForgeUnitTint t = unit.gameObject.GetComponent<ForgeUnitTint>();

                if (t == null)
                    t = unit.gameObject.AddComponent<ForgeUnitTint>();

                t.Capture();

                Layer layer = t.Find(key);

                if (layer == null)
                {
                    layer = new Layer { key = key };
                    t._layers.Add(layer);
                }

                layer.color = color;
                layer.pulseHz = Mathf.Max(0f, pulseHz);
                layer.endsAt = (seconds > 0f) ? Time.time + seconds : 0f;
                layer.strength = Mathf.Clamp01(strength);

                t.Write();
            }
            catch (Exception e)
            {
                Log.LogError("Tinting a unit failed: " + e);
            }
        }

        public static void Clear(Unit unit, string key)
        {
            if (unit == null || string.IsNullOrEmpty(key))
                return;

            try
            {
                var t = unit.gameObject.GetComponent<ForgeUnitTint>();

                if (t == null)
                    return;

                Layer layer = t.Find(key);

                if (layer == null)
                    return;

                t._layers.Remove(layer);
                t.Write();

                if (t._layers.Count == 0)
                    Destroy(t);
            }
            catch (Exception e)
            {
                Log.LogError("Clearing a unit tint failed: " + e);
            }
        }

        private Layer Find(string key)
        {
            for (int i = 0; i < _layers.Count; i++)
            {
                if (_layers[i].key == key)
                    return _layers[i];
            }

            return null;
        }

        // ------------------------------------------------------------------
        // The renderers
        // ------------------------------------------------------------------

        // Scoped to the prefab's `Visual` subtree, for the reason the minion
        // recolour and the slow tint are both scoped that way: a unit's AI
        // state indicators are SpriteRenderers too and are deliberately colour
        // coded, so tinting everything paints over the AI's own tells.
        private void Capture()
        {
            if (_captured)
                return;

            _captured = true;

            Transform visual = ForgeMinionLook.FindVisual(transform);

            SpriteRenderer[] found = (visual != null)
                ? visual.GetComponentsInChildren<SpriteRenderer>(true)
                : GetComponentsInChildren<SpriteRenderer>(true);

            for (int i = 0; i < found.Length; i++)
            {
                SpriteRenderer sr = found[i];

                if (sr == null)
                    continue;

                // Never the damage-flash silhouette, and - when there is no
                // Visual subtree to scope us - never an AI state sprite.
                if (ForgeMinionLook.IsHighlight(sr.transform))
                    continue;

                if (visual == null &&
                    ForgeMinionLook.IsStateSprite(sr.transform))
                {
                    continue;
                }

                _renderers.Add(sr);
                _base.Add(sr.color);
            }
        }

        // ------------------------------------------------------------------
        private void Update()
        {
            bool dropped = false;

            for (int i = _layers.Count - 1; i >= 0; i--)
            {
                Layer l = _layers[i];

                if (l.endsAt > 0f && Time.time >= l.endsAt)
                {
                    _layers.RemoveAt(i);
                    dropped = true;
                }
            }

            if (_layers.Count == 0)
            {
                if (dropped)
                    Write();          // puts the base colours back

                Destroy(this);
                return;
            }

            // Re-asserted every frame rather than written once. A unit's
            // renderers are few, and something else touching a colour (a
            // fade, an animator) would otherwise silently drop the tint until
            // the next Set - which is the kind of bug that looks like the
            // feature never ran.
            Write();
        }

        private void OnDestroy()
        {
            // Whatever happens - the unit dying, the component being removed,
            // an exception upstream - the base colours go back. Restoring in
            // Clear alone would leave a killed-mid-debuff unit tinted if
            // anything ever pooled and reused it.
            try
            {
                _layers.Clear();
                Write();
            }
            catch (Exception) { }
        }

        private void Write()
        {
            Color product = Product();

            for (int i = 0; i < _renderers.Count; i++)
            {
                SpriteRenderer sr = _renderers[i];

                if (sr == null)
                    continue;

                Color b = _base[i];

                // The renderer's CURRENT alpha, not the captured one - so a
                // fade running at the same time is preserved rather than
                // reverted every frame.
                sr.color = new Color(
                    b.r * product.r,
                    b.g * product.g,
                    b.b * product.b,
                    sr.color.a);
            }
        }

        // Every live layer multiplied together. Commutative, so no ordering
        // and no priorities - two features cannot fight over who wins.
        //
        // Note two strong tints DO compound (0.5 x 0.5 = 0.25), so a unit that
        // is both slowed and slammable reads as doubly odd. That is the honest
        // answer and it is what makes stacked debuffs legible.
        private Color Product()
        {
            float r = 1f, g = 1f, b = 1f;

            for (int i = 0; i < _layers.Count; i++)
            {
                Layer l = _layers[i];

                float strength = l.strength;

                if (l.pulseHz > 0f)
                {
                    // 0..1, so the pulse runs between "no tint at all" and the
                    // full colour rather than between two tints. A slammable
                    // enemy is a TEMPORARY state, and a throb reads as a timer
                    // running out in a way a steady colour cannot.
                    //
                    // The layer's own strength MULTIPLIES the throb rather than
                    // replacing it, so a half-strength pulsing tint still
                    // pulses - between "nothing" and "half" instead of between
                    // "nothing" and "full". Setting one should never silently
                    // switch the other off.
                    strength *=
                        0.5f + 0.5f * Mathf.Sin(Time.time * l.pulseHz * Mathf.PI * 2f);
                }

                r *= Mathf.Lerp(1f, l.color.r, strength);
                g *= Mathf.Lerp(1f, l.color.g, strength);
                b *= Mathf.Lerp(1f, l.color.b, strength);
            }

            return new Color(r, g, b, 1f);
        }
    }
}
