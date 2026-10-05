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
    // THERE ARE TWO OPERATORS NOW, `multiply` (the default) and `replace`, and
    // the reason is that no amount of strength could make a multiply brighter -
    // see the note on the constants. Each group composes with itself
    // commutatively; multiply resolves first and replace paints over the
    // result, which is the only ordering that means anything.
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
            ForgeLog.Source("WeaponForge.Tint");

        // Keys used by the mod's own features. Strings rather than an enum so a
        // new feature needs no change here.
        public const string Slow = "slow";
        public const string Slam = "slam";
        public const string Contact = "contact";

        // HOW A LAYER MEETS THE SPRITE UNDERNEATH IT.
        //
        // R14 tests 166 and 167, and he asked twice in the same round: *"I was
        // hoping to make the color STRONGER and more prominent... some of the
        // enemies look like they're being tinted and others not so much."*
        //
        // Both halves of that are one fact about multiplication: **it can only
        // ever darken.** `base * colour` with any channel at or below the
        // base's own value moves toward black, never toward the colour, so a
        // pale blue over a dark grey Cross is nearly invisible while the same
        // blue over a white Fly reads clearly. There is no strength value that
        // fixes it - at strength 1 a multiply is already as strong as a multiply
        // gets, and turning it up is what he had already tried.
        //
        // So Replace is a genuinely different operator rather than more of the
        // same one: it moves the sprite TOWARD the colour, which brightens as
        // readily as it darkens and hits a flat, unmissable colour at strength
        // 1. Multiply stays the default because every weapon already written
        // was tuned against it.
        public const int Multiply = 0;
        public const int Replace = 1;

        // Returns -1 for a word it does not know, so the caller can warn
        // rather than silently handing back the default - which is the whole
        // silent-fallback family this project keeps paying for.
        //
        // "tint" IS ACCEPTED FOR MULTIPLY because `minion.colorMode` got here
        // first and already spells the same distinction `replace` / `tint`
        // ("tint - multiply, keeps shading"). Two vocabularies for one idea in
        // one mod is a bug report waiting to be filed, and the cost of
        // accepting both is this line.
        public static int ParseMode(string s)
        {
            switch ((s ?? "").Trim().ToLowerInvariant())
            {
                case "replace":
                case "blend":
                case "over":
                case "solid":
                case "strong":
                    return Replace;

                case "multiply":
                case "tint":
                case "shade":
                    return Multiply;

                default:
                    return -1;
            }
        }

        public static string ModeWord(int mode)
        {
            return (mode == Replace) ? "replace" : "multiply";
        }

        private class Layer
        {
            public string key;
            public Color color;
            public float endsAt;      // 0 = until explicitly cleared
            public float pulseHz;     // 0 = steady
            public int mode = Multiply;

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
            Set(unit, key, color, seconds, pulseHz, strength, Multiply);
        }

        public static void Set(
            Unit unit, string key, Color color, float seconds, float pulseHz,
            float strength, int mode)
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
                layer.mode = mode;

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

            // The replace layers, gathered once for the whole unit rather than
            // per renderer - they do not depend on the base colour.
            float over = 0f;
            float or_ = 0f, og = 0f, ob = 0f;

            for (int i = 0; i < _layers.Count; i++)
            {
                Layer l = _layers[i];

                if (l.mode != Replace)
                    continue;

                float s = Live(l);

                if (s <= 0.001f)
                    continue;

                over += s;
                or_ += l.color.r * s;
                og += l.color.g * s;
                ob += l.color.b * s;
            }

            // TWO REPLACE LAYERS ARE A WEIGHTED MIX, not a chain of lerps.
            // Chaining would let the last one written win outright at strength
            // 1, and which one is "last" is list order - i.e. whoever
            // published first. Weights commute, so a slammed AND stunned unit
            // reads as the average of the two rather than as whichever feature
            // happened to fire second. Same arithmetic as ForgeMuzzleTint,
            // arrived at there for the same reason.
            if (over > 1f)
            {
                or_ /= over;
                og /= over;
                ob /= over;
            }

            for (int i = 0; i < _renderers.Count; i++)
            {
                SpriteRenderer sr = _renderers[i];

                if (sr == null)
                    continue;

                Color b = _base[i];

                // Multiply first, then replace over the result. That order is
                // the only one that means anything: a replace layer is "paint
                // this colour on top", so it has to see the finished
                // multiplied colour, and a multiply applied afterwards would
                // darken the paint the author asked to be unmissable.
                float r = b.r * product.r;
                float g = b.g * product.g;
                float bl = b.b * product.b;

                if (over > 0.001f)
                {
                    float keep = (over > 1f) ? 0f : 1f - over;

                    r = r * keep + or_;
                    g = g * keep + og;
                    bl = bl * keep + ob;
                }

                // The renderer's CURRENT alpha, not the captured one - so a
                // fade running at the same time is preserved rather than
                // reverted every frame.
                sr.color = new Color(r, g, bl, sr.color.a);
            }
        }

        // Every live MULTIPLY layer multiplied together. Commutative, so no
        // ordering and no priorities - two features cannot fight over who wins.
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

                if (l.mode != Multiply)
                    continue;

                float strength = Live(l);

                r *= Mathf.Lerp(1f, l.color.r, strength);
                g *= Mathf.Lerp(1f, l.color.g, strength);
                b *= Mathf.Lerp(1f, l.color.b, strength);
            }

            return new Color(r, g, b, 1f);
        }

        // This layer's strength right now, pulse included. Shared by both
        // operators so a throb behaves identically in either mode.
        private static float Live(Layer l)
        {
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

            return Mathf.Clamp01(strength);
        }
    }
}
