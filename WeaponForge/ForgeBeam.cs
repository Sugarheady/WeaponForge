using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace WeaponForge
{
    // Custom art for a hitscan beam (the "beam" block in a weapon file).
    //
    // This is the FOUNDATION layer: it re-skins the beam the game already
    // draws. It does not bend it - the stock beam is physically incapable of
    // bending, for a reason worth writing down:
    //
    //   HitscanWeaponVisual.UpdateVisual(start, end, normal) puts the root at
    //   `start`, rotates it to face `end`, and sets ONE SpriteRenderer's
    //   size.x to the whole distance. The beam is a single stretched quad, so
    //   there is no place for a curve to live. A bending beam needs a CHAIN of
    //   short quads - which is now ForgeCurveBeam, built on top of this one and
    //   using exactly the segment art this layer teaches the mod to load.
    //
    // The two blocks divide cleanly: `curveBeam` owns the FIRE beam and how it
    // is routed, `beam` still owns the warm-up beam, the material, the impact
    // sprite and the impact sound. A weapon can use both.
    //
    // What the game's own beams do, verified across all four LaserVisual
    // prefabs:
    //   * The FIRE renderer is drawMode Sliced; the WARMUP renderer is Tiled.
    //   * Every beam sprite has pivot (0, 0.5) - LEFT-centre - because the quad
    //     grows rightward from the barrel. This is the single biggest trap for
    //     custom art: the slicer writes a CENTRED pivot, which would hang half
    //     the beam out behind the ship. So sprites are rebuilt with a left
    //     pivot here rather than making anyone hand-edit a manifest.
    //   * Horizontal borders are always 0 (nothing protects the caps), while
    //     two of them use vertical borders so thickening does not squash the
    //     top and bottom edges.
    //   * size.y is the thickness and UpdateVisual preserves it; size.x is
    //     overwritten every frame, so it is not ours to set.
    //   * Material is EmissiveUnlitSprite (or ...Bright), which blows detailed
    //     art toward white - same problem projectileGlow solves for bullets.
    public static class ForgeBeam
    {
        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("WeaponForge.Beam");

        // HitscanWeaponVisual keeps all of these private [SerializeField].
        // Plain FieldInfo rather than Harmony's FieldRefAccess<T,F>, which
        // throws on any value-type mismatch (see the float 'offset').
        private static readonly FieldInfo _fFire =
            AccessTools.Field(
                typeof(HitscanWeaponVisual), "fireSpriteRenderer");

        private static readonly FieldInfo _fWarmup =
            AccessTools.Field(
                typeof(HitscanWeaponVisual), "warmUpSpriteRenderer");

        private static readonly FieldInfo _fImpact =
            AccessTools.Field(
                typeof(HitscanWeaponVisual), "impactParticle");

        private static readonly FieldInfo _fOffset =
            AccessTools.Field(typeof(HitscanWeaponVisual), "offset");

        // Left-pivot rebuilds, cached so one sprite used by five weapons
        // becomes one extra Sprite rather than five.
        private static readonly Dictionary<Sprite, Sprite> _leftPivot =
            new Dictionary<Sprite, Sprite>();

        // The three private [SerializeField]s the curve beam also needs. Kept
        // here rather than re-resolved there so there is one place that knows
        // HitscanWeaponVisual's private field names - if a game update renames
        // one, exactly one file breaks.
        public static SpriteRenderer FireOf(HitscanWeaponVisual visual)
        {
            if (visual == null || _fFire == null)
                return null;

            return _fFire.GetValue(visual) as SpriteRenderer;
        }

        public static SpriteRenderer WarmupOf(HitscanWeaponVisual visual)
        {
            if (visual == null || _fWarmup == null)
                return null;

            return _fWarmup.GetValue(visual) as SpriteRenderer;
        }

        public static ParticleSystem ImpactOf(HitscanWeaponVisual visual)
        {
            if (visual == null || _fImpact == null)
                return null;

            return _fImpact.GetValue(visual) as ParticleSystem;
        }

        public class Spec
        {
            public string fileName;

            public string spriteName;
            public string warmupSprite;
            public string impactSprite;
            public string tiling;
            public float? thickness;
            public float? warmupThickness;
            public bool? glow;
            public string material;
            public float? offset;
            public float? impactFps;

            // Set by ApplyImpact, not by the beam block. A beam has no
            // projectile to carry an impact sound, so it rides on the beam
            // VISUAL - and the visual is not cloned until the visual pass, so
            // the guid has to wait here until there is a private copy to
            // attach it to. impactSfxName is kept only for the log line.
            public string impactSfx;
            public string impactSfxName;
            public int impactSfxWhere;   // (int)ForgeImpact.Where

            // Renderer tint. Left unset, custom art resets it to white so the
            // sprite shows as drawn instead of inheriting the template's
            // element colour. See ApplyTint.
            public string color;
        }

        // "beam": "mySprite" is shorthand for just re-skinning the fire beam.
        public static Spec Parse(JToken token, string fileName)
        {
            if (token == null || token.Type == JTokenType.Null)
                return null;

            Spec spec;

            if (token.Type == JTokenType.String)
            {
                string s = ((string)token ?? string.Empty).Trim();

                if (s.Length == 0)
                    return null;

                spec = new Spec { spriteName = s };
            }
            else
            {
                var o = token as JObject;

                if (o == null)
                {
                    Log.LogWarning(
                        fileName + ": \"beam\" should be an object or a " +
                        "sprite name - ignored.");
                    return null;
                }

                spec = new Spec
                {
                    spriteName = (string)o["sprite"],
                    warmupSprite = (string)o["warmupSprite"],
                    impactSprite = (string)o["impactSprite"],
                    tiling = (string)o["tiling"],
                    thickness = (float?)o["thickness"],
                    warmupThickness = (float?)o["warmupThickness"],
                    glow = (bool?)o["glow"],
                    material = (string)o["material"],
                    offset = (float?)o["offset"],
                    impactFps = (float?)o["impactFps"],
                    color = (string)o["color"]
                };
            }

            spec.fileName = fileName;
            return spec;
        }

        // Why this exists: a stock beam prefab carries its ELEMENT tint on the
        // SpriteRenderer itself - the Caps laser's is orange-red. A renderer
        // tint MULTIPLIES whatever sprite sits in the slot, so custom art
        // loaded perfectly and then came out the wrong colour: magenta art on
        // an orange renderer reads as orange. Nothing in the game's own beam
        // code sets that colour, so it is purely inherited from the template.
        //
        // So: art you supplied resets its renderer to white and shows as drawn,
        // and "color" re-tints deliberately. A renderer whose sprite was NOT
        // replaced is left alone, or a custom fire beam would silently bleach
        // the stock warm-up beam next to it.
        private static void ApplyTint(
            SpriteRenderer fire,
            SpriteRenderer warmup,
            Spec spec,
            string fileName)
        {
            Color? tint = null;

            if (!string.IsNullOrEmpty(spec.color))
            {
                if (VisualCustomizer.IsRainbow(spec.color))
                {
                    Log.LogWarning(
                        fileName + ": beam \"color\": rainbow is not " +
                        "supported on a beam yet - the beam is one stretched " +
                        "quad with no per-frame driver on it. Use a solid " +
                        "colour. Ignored.");
                }
                else
                {
                    Color parsed;

                    if (VisualCustomizer.TryParseColor(spec.color, out parsed))
                        tint = parsed;
                    else
                        Log.LogWarning(
                            fileName + ": beam \"color\": '" + spec.color +
                            "' is not a colour I recognise - ignored.");
                }
            }

            if (tint.HasValue)
            {
                if (fire != null) fire.color = tint.Value;
                if (warmup != null) warmup.color = tint.Value;

                Log.LogInfo(fileName + ": beam tinted " + spec.color + ".");
                return;
            }

            bool reset = false;

            if (fire != null && !string.IsNullOrEmpty(spec.spriteName) &&
                fire.color != Color.white)
            {
                fire.color = Color.white;
                reset = true;
            }

            if (warmup != null && !string.IsNullOrEmpty(spec.warmupSprite) &&
                warmup.color != Color.white)
            {
                warmup.color = Color.white;
                reset = true;
            }

            if (reset)
            {
                Log.LogInfo(
                    fileName + ": beam tint reset to white so your art shows " +
                    "its own colours - this template's beam carried an " +
                    "element tint that would have multiplied over it. Set " +
                    "\"color\" in the beam block to tint it on purpose.");
            }
        }

        public static void Apply(HitscanWeaponVisual visual, Spec spec)
        {
            if (visual == null || spec == null)
                return;

            string fileName = spec.fileName ?? "beam";

            var fire = _fFire.GetValue(visual) as SpriteRenderer;
            var warmup = _fWarmup.GetValue(visual) as SpriteRenderer;
            var impact = _fImpact.GetValue(visual) as ParticleSystem;

            // The impact SOUND attaches to this visual (a beam has no
            // projectile to hang it on). Done first and outside the fire-
            // renderer guard below, because a weapon can legitimately want
            // the sound without touching the beam's art.
            if (!string.IsNullOrEmpty(spec.impactSfx))
            {
                var imp = visual.GetComponent<ForgeImpact>();

                if (imp == null)
                    imp = visual.gameObject.AddComponent<ForgeImpact>();

                imp.sfx = spec.impactSfx;
                imp.where = (ForgeImpact.Where)spec.impactSfxWhere;
                imp.groundLayer = LayerMask.NameToLayer("Ground");

                // A held beam re-fires every frame it is on, so this gap is
                // what stops the impact sound becoming a buzz. The weapon's
                // own damageRepeatDelay is the right idea but is not reachable
                // from here, so use a value in the same ballpark.
                imp.minInterval = 0.12f;

                Log.LogInfo(
                    fileName + ": beam impact sound '" +
                    (spec.impactSfxName ?? spec.impactSfx) +
                    "' attached (at most one every 0.12s).");
            }

            if (fire == null)
            {
                Log.LogWarning(
                    fileName + ": this beam visual has no fire sprite " +
                    "renderer - \"beam\" skipped.");
                return;
            }

            SpriteDrawMode? mode = ParseTiling(spec.tiling, fileName);

            ApplySprite(fire, spec.spriteName, "sprite", fileName);
            ApplySprite(warmup, spec.warmupSprite, "warmupSprite", fileName);

            ApplyTint(fire, warmup, spec, fileName);

            bool tiled = false;

            if (mode.HasValue)
            {
                fire.drawMode = mode.Value;

                // Continuous, not Adaptive: Adaptive scales the tiles to fit a
                // whole number into the length, so every change in beam length
                // would resize the art. Continuous keeps each tile the same
                // size and clips the last one, which is what reads as a stream
                // of segments travelling down the beam.
                if (mode.Value == SpriteDrawMode.Tiled)
                {
                    fire.tileMode = SpriteTileMode.Continuous;
                    tiled = true;

                    if (string.IsNullOrEmpty(spec.spriteName))
                    {
                        Log.LogWarning(
                            fileName + ": \"tiling\": \"repeat\" with the " +
                            "TEMPLATE's own beam sprite will look wrong. The " +
                            "stock beam sprites are 8x8 gradients drawn to be " +
                            "STRETCHED, so repeating one just makes a stripey " +
                            "mess. Use \"stretch\", or supply a seamless " +
                            "\"sprite\" of your own.");
                    }
                }
            }

            // size.x is rewritten every frame by UpdateVisual, so only y is
            // ours. Read-modify-write because size is a struct property.
            //
            // The trap that "repeat" hides: Tiled draw mode fills the size by
            // repeating in BOTH axes, not just along the beam. So a thickness
            // taller than the sprite repeats it VERTICALLY and you get two (or
            // three...) stacked beams instead of one thicker one. In repeat
            // mode the sprite's own pixel height IS the thickness.
            float natural = NaturalHeight(fire.sprite);

            if (spec.thickness.HasValue)
            {
                if (tiled && natural > 0f &&
                    Mathf.Abs(spec.thickness.Value - natural) > 0.01f)
                {
                    Log.LogWarning(
                        fileName + ": \"thickness\": " + spec.thickness.Value +
                        " with \"tiling\": \"repeat\" will STACK the sprite " +
                        Mathf.Max(1f, spec.thickness.Value / natural)
                            .ToString("0.#") +
                        " times vertically - that is what looks like two " +
                        "beams. In repeat mode the thickness comes from the " +
                        "sprite: this one is " +
                        (natural * ForgeSpriteLibrary.DefaultPixelsPerUnit)
                            .ToString("0") +
                        " px tall, so it wants \"thickness\": " +
                        natural.ToString("0.##") +
                        ". Draw the art taller for a thicker beam, or use " +
                        "\"tiling\": \"stretch\" where thickness is free.");
                }

                fire.size = new Vector2(fire.size.x, spec.thickness.Value);
            }
            else if (tiled && natural > 0f)
            {
                // No thickness asked for: match the art exactly so repeat mode
                // gives one clean row rather than however many the template's
                // leftover size happened to fit.
                fire.size = new Vector2(fire.size.x, natural);
            }

            if (warmup != null && spec.warmupThickness.HasValue)
            {
                warmup.size =
                    new Vector2(warmup.size.x, spec.warmupThickness.Value);
            }

            ApplyMaterial(visual, fire, warmup, spec, fileName);

            if (impact != null && !string.IsNullOrEmpty(spec.impactSprite))
            {
                ForgeSpriteLibrary.Art art;
                Sprite[] frames =
                    ForgeTrail.ResolveFrames(
                        spec.impactSprite, fileName, "beam impactSprite",
                        out art);

                if (frames != null && frames.Length > 0)
                {
                    // Fallback NULL: the beam's spark is a particle system whose
                    // lifetime the beam owns, so fitting the frames to it is the
                    // right default here, and it is what this key already did.
                    VisualCustomizer.ApplyParticleSprite(
                        impact, frames,
                        ForgeSpriteLibrary.ParticleFps(
                            art, spec.impactFps, null,
                            "beam impactSprite", fileName));
                }
            }

            if (spec.offset.HasValue && _fOffset != null)
                _fOffset.SetValue(visual, spec.offset.Value);

            Log.LogInfo(fileName + ": beam art applied.");
        }

        private static void ApplySprite(
            SpriteRenderer renderer,
            string name,
            string key,
            string fileName)
        {
            if (renderer == null || string.IsNullOrEmpty(name))
                return;

            Sprite[] frames =
                ForgeTrail.ResolveFrames(name, fileName, "beam " + key);

            if (frames == null || frames.Length == 0)
                return;

            if (frames.Length > 1)
            {
                Log.LogWarning(
                    fileName + ": beam " + key + " '" + name + "' is an " +
                    "ANIMATION, and a beam is one stretched quad with no " +
                    "flipbook of its own - only the first frame is used. To " +
                    "make a beam look like it is moving, use " +
                    "\"tiling\": \"repeat\" with a seamless sprite instead - " +
                    "or add a \"curveBeam\" block, which draws the beam as a " +
                    "chain and puts one frame of your animation on each link " +
                    "so the pattern travels along it.");
            }

            renderer.sprite = LeftPivot(frames[0]);
        }

        // Rebuild a sprite with its pivot on the LEFT edge, vertically centred.
        //
        // Every stock beam sprite is authored this way because the quad grows
        // rightward from the barrel: UpdateVisual puts the transform AT the
        // barrel and only ever grows size.x. A centred pivot - what the slicer
        // writes, and what a projectile wants - would centre the beam on the
        // barrel, so half of it would trail out behind the ship and it would
        // only reach half as far as its own length.
        //
        // Rebuilding rather than demanding a hand-edited pivot in the manifest
        // means ONE sprite can serve as both a bullet and a beam.
        public static Sprite LeftPivot(Sprite s)
        {
            if (s == null)
                return null;

            // Already left-centred (a stock beam sprite, or a manifest that
            // set pivotX 0) - nothing to do.
            if (s.rect.width > 0f && s.rect.height > 0f)
            {
                float px = s.pivot.x / s.rect.width;
                float py = s.pivot.y / s.rect.height;

                if (Mathf.Abs(px) < 0.001f && Mathf.Abs(py - 0.5f) < 0.001f)
                    return s;
            }

            Sprite cached;

            if (_leftPivot.TryGetValue(s, out cached) && cached != null)
                return cached;

            // Which rect to sample. The game's art lives in a packed Sprite
            // Atlas, where `rect` is the sprite's position on its ORIGINAL
            // texture while `texture` hands back the atlas page - the two do
            // not line up (Sprite_Lazer_6_0 is rect 364,1140 but sits at
            // 359,1139 in the atlas), so rebuilding off `rect` would sample
            // the wrong pixels. textureRect is the atlas-correct one, but it
            // is only readable when packing is rectangular.
            Rect source = s.rect;

            if (s.packed)
            {
                if (s.packingMode == SpritePackingMode.Tight)
                {
                    Log.LogWarning(
                        "'" + s.name + "' is TIGHTLY packed in the game's " +
                        "sprite atlas, so its pivot cannot be moved to the " +
                        "left edge and it would draw half-behind the ship. " +
                        "Use one of the Sprite_Lazer_* sprites, or your own " +
                        "art from the 'sprites' folder, which has no such " +
                        "limit.");
                    return s;
                }

                source = s.textureRect;
            }

            Sprite rebuilt;

            try
            {
                rebuilt = Sprite.Create(
                    s.texture,
                    source,
                    new Vector2(0f, 0.5f),
                    s.pixelsPerUnit,
                    0,
                    // FullRect, not Tight: Sliced and Tiled draw modes refuse
                    // to render a Tight-meshed sprite at all.
                    SpriteMeshType.FullRect,
                    s.border);
            }
            catch (Exception e)
            {
                Log.LogWarning(
                    "Could not re-pivot beam sprite '" + s.name + "' (" +
                    e.Message + ") - using it as-is, which will look " +
                    "off-centre.");
                return s;
            }

            if (rebuilt == null)
                return s;

            rebuilt.name = s.name + " (beam)";
            rebuilt.hideFlags = HideFlags.HideAndDontSave;

            _leftPivot[s] = rebuilt;
            return rebuilt;
        }

        // A sprite's height in WORLD units - what a Tiled renderer treats as
        // one row.
        private static float NaturalHeight(Sprite s)
        {
            if (s == null || s.pixelsPerUnit <= 0f)
                return 0f;

            return s.rect.height / s.pixelsPerUnit;
        }

        private static SpriteDrawMode? ParseTiling(
            string tiling, string fileName)
        {
            if (string.IsNullOrEmpty(tiling))
                return null;

            string t = tiling.Trim().ToLowerInvariant();

            if (t == "stretch" || t == "sliced" || t == "slice")
                return SpriteDrawMode.Sliced;

            if (t == "repeat" || t == "tiled" || t == "tile")
                return SpriteDrawMode.Tiled;

            if (t == "simple")
            {
                // Not offered on purpose: SpriteRenderer.size is IGNORED in
                // Simple mode, and size.x is the only thing that makes the
                // beam reach its target. A "simple" beam would be a fixed
                // stub at the barrel no matter how far away the enemy is.
                Log.LogWarning(
                    fileName + ": \"tiling\": \"simple\" is not usable for a " +
                    "beam - Simple draw mode ignores the renderer's size, " +
                    "and size is what makes the beam reach its target, so " +
                    "the beam would be a fixed stub. Use \"stretch\" (the " +
                    "stock look) or \"repeat\" (a seamless sprite tiled " +
                    "along the beam).");
                return null;
            }

            Log.LogWarning(
                fileName + ": \"tiling\": '" + tiling + "' is not " +
                "recognised - use \"stretch\" or \"repeat\".");
            return null;
        }

        private static void ApplyMaterial(
            HitscanWeaponVisual visual,
            SpriteRenderer fire,
            SpriteRenderer warmup,
            Spec spec,
            string fileName)
        {
            string name = spec.material;

            if (string.IsNullOrEmpty(name) && spec.glow.HasValue)
            {
                name = spec.glow.Value
                    ? "EmissiveUnlitSprite"
                    : "SpriteUnlitAA";
            }

            if (string.IsNullOrEmpty(name))
                return;

            var mat =
                JsonFieldMapper.FindAsset(typeof(Material), name) as Material;

            if (mat == null)
            {
                Log.LogWarning(
                    fileName + ": beam material '" + name + "' was not " +
                    "found - the beam keeps the template's. Every stock beam " +
                    "uses 'EmissiveUnlitSprite'; 'SpriteUnlitAA' draws art as " +
                    "painted.");
                return;
            }

            // sharedMaterial: point at the existing asset instead of
            // instantiating a per-renderer copy.
            if (fire != null)
                fire.sharedMaterial = mat;

            if (warmup != null)
                warmup.sharedMaterial = mat;
        }
    }
}
