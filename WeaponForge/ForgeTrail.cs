using System;
using BepInEx.Logging;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace WeaponForge
{
    // Adds / retunes / removes the trail on a projectile.
    //
    // Shaped from the game's own prefabs rather than invented. Findings that
    // drove every decision below:
    //
    //  * PUNK contains NO TrailRenderer at all, and not one prefab enables
    //    the ParticleSystem "Trails" module. Every trail in the game is the
    //    same idiom: a child ParticleSystem simulating in WORLD space that
    //    emits sprite puffs per unit TRAVELLED (rateOverDistance), with
    //    rateOverTime at zero - so the trail thickens with speed instead of
    //    bunching up when the shot is slow.
    //  * ammo_Popper and ammo_Bolt use identical numbers - startLifetime
    //    0.025, startSize 0.8, rateOverDistance 20, sprite part_cyrcle_12,
    //    renderer one sorting order BEHIND the bullet - so those are the
    //    defaults here.
    //  * emitterVelocityMode is Transform, not Rigidbody: projectiles move
    //    by transform, so Rigidbody would read zero velocity.
    //  * only 4 ammo prefabs ship a trail child, so grafting one on is the
    //    common case, not the exception.
    //
    // We CLONE a stock trail rather than building a ParticleSystem from
    // scratch. AddComponent<ParticleSystem> hands back Unity's built-in
    // default particle material, which is not one of this project's 2D
    // sprite materials; cloning inherits the correct material, shader,
    // render mode and sorting layer for free. Same reasoning as
    // ApplyMuzzleColor cloning the muzzle prefab.
    public static class ForgeTrail
    {
        private static readonly ManualLogSource Log =
            ForgeLog.Source("WeaponForge.Trail");

        // The prefab we lift a trail off when the template has none.
        public const string DefaultTemplate = "ammo_Popper";

        // ammo_Popper / ammo_Bolt values, so an unqualified "trail": true
        // looks like it belongs in the game.
        public const float DefaultLifetime = 0.025f;
        public const float DefaultSize = 0.8f;
        public const float DefaultPerUnit = 20f;

        private static ParticleSystem _donorCache;
        private static string _donorCacheKey;

        // What a weapon file's "trail" block asked for. Everything is
        // nullable so an unset key keeps whatever the cloned donor had -
        // "retune one number" has to be possible without restating the rest.
        public class Spec
        {
            // Carried on the spec rather than passed alongside it, so
            // ReskinProjectile (which has no file context of its own) can
            // still produce warnings that name the file at fault.
            public string fileName;

            public bool enabled = true;
            public string spriteName;
            public string colorText;
            public float rgbSpeed = 0.5f;
            public float? lifetime;
            public float? size;
            public float? sizeEnd;
            public float? perUnit;
            public float? perSecond;
            public float? speed;
            public float? gravity;
            public bool? fade;
            public float? fps;
            public int? sortingOffset;
            public string template;
        }

        // Accepts the full object form, plus two shorthands that cover most
        // real use: "trail": false to strip one off, "trail": "myPuff" to
        // add one made of that sprite with stock numbers.
        public static Spec Parse(JToken token, string fileName)
        {
            Spec spec = ParseCore(token, fileName);

            if (spec != null)
                spec.fileName = fileName;

            return spec;
        }

        private static Spec ParseCore(JToken token, string fileName)
        {
            if (token == null || token.Type == JTokenType.Null)
                return null;

            if (token.Type == JTokenType.Boolean)
                return new Spec { enabled = (bool)token };

            if (token.Type == JTokenType.String)
            {
                string s = ((string)token ?? string.Empty).Trim();

                if (s.Length == 0)
                    return null;

                // "none"/"off" read as "remove it" to anyone skimming.
                if (s.Equals("none", StringComparison.OrdinalIgnoreCase) ||
                    s.Equals("off", StringComparison.OrdinalIgnoreCase) ||
                    s.Equals("false", StringComparison.OrdinalIgnoreCase))
                {
                    return new Spec { enabled = false };
                }

                return new Spec { spriteName = s };
            }

            var o = token as JObject;

            if (o == null)
            {
                Log.LogWarning(
                    fileName + ": \"trail\" should be an object, a sprite " +
                    "name, or false - ignored.");
                return null;
            }

            var spec = new Spec
            {
                enabled = (bool?)o["enabled"] ?? true,
                spriteName = (string)o["sprite"],
                colorText = (string)o["color"],
                rgbSpeed = (float?)o["rgbSpeed"] ?? 0.5f,
                lifetime = (float?)o["lifetime"],
                size = (float?)o["size"],
                sizeEnd = (float?)o["sizeEnd"],
                perUnit = (float?)o["perUnit"],
                perSecond = (float?)o["perSecond"],
                speed = (float?)o["speed"],
                gravity = (float?)o["gravity"],
                fade = (bool?)o["fade"],
                fps = (float?)o["fps"],
                sortingOffset = (int?)o["sortingOffset"],
                template = (string)o["template"]
            };

            return spec;
        }

        // Apply the spec to an already-cloned projectile prefab. Runs AFTER
        // projectileSpriteOnly's IsolateSprite (which switches every child
        // particle system off), so every field it cares about is written
        // explicitly rather than left at the donor's value - that way the
        // two features can be combined in either order and still work.
        public static void Apply(GameObject clone, Spec spec)
        {
            if (clone == null || spec == null)
                return;

            string fileName = spec.fileName ?? "trail";

            ParticleSystem existing = FindTrail(clone);

            if (!spec.enabled)
            {
                if (existing == null)
                {
                    Log.LogWarning(
                        fileName + ": \"trail\": false but this template " +
                        "has no trail to remove - nothing to do.");
                    return;
                }

                Silence(existing);
                Log.LogInfo(
                    fileName + ": trail removed ('" +
                    existing.name + "').");
                return;
            }

            ParticleSystem ps = existing;
            bool grafted = false;

            if (ps == null)
            {
                ps = Graft(clone, spec.template, fileName);

                if (ps == null)
                    return;

                grafted = true;
            }

            Configure(clone, ps, spec, grafted, fileName);

            Log.LogInfo(
                fileName + ": trail " +
                (grafted ? "added" : "retuned") + " on '" +
                clone.name + "'.");
        }

        // A trail is either named like one, or - more reliably - emits by
        // distance travelled, which nothing but a trail does.
        private static ParticleSystem FindTrail(GameObject clone)
        {
            ParticleSystem[] all =
                clone.GetComponentsInChildren<ParticleSystem>(true);

            foreach (var ps in all)
            {
                if (ps.name.IndexOf(
                        "trail",
                        StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return ps;
                }
            }

            foreach (var ps in all)
            {
                if (ps.emission.rateOverDistance.constantMax > 0f)
                    return ps;
            }

            return null;
        }

        // Off, not destroyed: a child could carry something functional, and
        // this stays reversible. Mirrors IsolateSprite.
        private static void Silence(ParticleSystem ps)
        {
            var emission = ps.emission;
            emission.enabled = false;

            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

            var psr = ps.GetComponent<ParticleSystemRenderer>();
            if (psr != null)
                psr.enabled = false;
        }

        private static ParticleSystem Graft(
            GameObject clone,
            string template,
            string fileName)
        {
            string wanted = string.IsNullOrEmpty(template)
                ? DefaultTemplate
                : template.Trim();

            ParticleSystem donor = Donor(wanted);

            if (donor == null)
            {
                Log.LogWarning(
                    fileName + ": no stock trail could be found to copy" +
                    (string.IsNullOrEmpty(template)
                        ? " (looked for the '" + DefaultTemplate +
                          "' one)"
                        : " on template '" + wanted + "'") +
                    " - trail skipped.");
                return null;
            }

            GameObject go =
                UnityEngine.Object.Instantiate(
                    donor.gameObject, clone.transform);

            go.name = "Forge Trail";
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = Vector3.one;

            // Match the shot it belongs to on both counts: the layer so a
            // later RemapLayer (enemy template -> player weapon) has nothing
            // odd to trip over, and the hideFlags so one hierarchy is not
            // half HideAndDontSave.
            go.layer = clone.layer;
            go.hideFlags = clone.hideFlags;

            return go.GetComponent<ParticleSystem>();
        }

        // Prefer the named prefab's own trail, fall back to any stock trail,
        // and only then to anything that emits by distance. Cached: this is
        // a scan of every loaded ParticleSystem.
        private static ParticleSystem Donor(string template)
        {
            if (_donorCache != null && _donorCacheKey == template)
                return _donorCache;

            UnityEngine.Object[] all =
                Resources.FindObjectsOfTypeAll(typeof(ParticleSystem));

            ParticleSystem named = null;
            ParticleSystem byDistance = null;

            foreach (UnityEngine.Object obj in all)
            {
                var ps = obj as ParticleSystem;

                if (ps == null)
                    continue;

                bool isTrail =
                    ps.name.Equals(
                        "trail", StringComparison.OrdinalIgnoreCase);

                if (isTrail)
                {
                    Transform parent = ps.transform.parent;

                    if (parent != null &&
                        parent.name.Equals(
                            template, StringComparison.OrdinalIgnoreCase))
                    {
                        _donorCache = ps;
                        _donorCacheKey = template;
                        return ps;
                    }

                    // Keep the best also-ran: an ammo prefab's trail is a
                    // safer copy than some scene decoration's. Once we hold
                    // an ammo one, stop - otherwise every later match would
                    // replace it and the LAST prefab scanned would win.
                    if (named == null)
                        named = ps;
                    else if (!IsAmmoChild(named) && IsAmmoChild(ps))
                        named = ps;
                }
                else if (byDistance == null &&
                         ps.emission.rateOverDistance.constantMax > 0f)
                {
                    byDistance = ps;
                }
            }

            _donorCache = named ?? byDistance;
            _donorCacheKey = template;
            return _donorCache;
        }

        // Every stock projectile prefab is named "ammo_..." or "Ammo ...".
        private static bool IsAmmoChild(ParticleSystem ps)
        {
            Transform parent = ps.transform.parent;

            return parent != null &&
                   parent.name.StartsWith(
                       "ammo", StringComparison.OrdinalIgnoreCase);
        }

        private static void Configure(
            GameObject clone,
            ParticleSystem ps,
            Spec spec,
            bool grafted,
            string fileName)
        {
            var main = ps.main;

            // The two that make it a trail rather than a puff cloud: world
            // space so the particles stay where they were dropped, and
            // Transform velocity because projectiles move by transform.
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.emitterVelocityMode =
                ParticleSystemEmitterVelocityMode.Transform;
            main.loop = true;
            main.playOnAwake = true;

            if (grafted)
            {
                main.startLifetime =
                    spec.lifetime.HasValue
                        ? spec.lifetime.Value
                        : DefaultLifetime;

                main.startSize =
                    spec.size.HasValue ? spec.size.Value : DefaultSize;
            }
            else
            {
                if (spec.lifetime.HasValue)
                    main.startLifetime = spec.lifetime.Value;

                if (spec.size.HasValue)
                    main.startSize = spec.size.Value;
            }

            if (spec.speed.HasValue)
                main.startSpeed = spec.speed.Value;

            if (spec.gravity.HasValue)
                main.gravityModifier = spec.gravity.Value;

            // A fast shot with a generous lifetime can want more than the
            // donor's ceiling; raise it but never lower what was there.
            if (main.maxParticles < 500)
                main.maxParticles = 500;

            // A drift speed with no shape emits straight along one axis, so
            // the puffs would march off in a line instead of spreading.
            // Circle is the 2D-correct choice here - Cone points into the
            // screen on Z, which is invisible in a side-on game.
            if (spec.speed.HasValue && spec.speed.Value != 0f)
            {
                var shape = ps.shape;
                shape.enabled = true;
                shape.shapeType = ParticleSystemShapeType.Circle;
                shape.radius = 0.01f;
                shape.arc = 360f;
                shape.arcMode = ParticleSystemShapeMultiModeValue.Random;
            }

            var emission = ps.emission;
            emission.enabled = true;

            if (grafted)
            {
                emission.rateOverDistance =
                    spec.perUnit.HasValue
                        ? spec.perUnit.Value
                        : DefaultPerUnit;

                emission.rateOverTime =
                    spec.perSecond.HasValue ? spec.perSecond.Value : 0f;
            }
            else
            {
                if (spec.perUnit.HasValue)
                    emission.rateOverDistance = spec.perUnit.Value;

                if (spec.perSecond.HasValue)
                    emission.rateOverTime = spec.perSecond.Value;
            }

            if (emission.rateOverDistance.constantMax <= 0f &&
                emission.rateOverTime.constantMax <= 0f)
            {
                Log.LogWarning(
                    fileName + ": the trail emits nothing - perUnit and " +
                    "perSecond are both 0. perUnit is the one you want " +
                    "(puffs per unit travelled, stock value " +
                    DefaultPerUnit + ").");
            }

            if (main.startLifetime.constantMax <= 0f)
            {
                Log.LogWarning(
                    fileName + ": the trail's lifetime is 0, so every puff " +
                    "dies the instant it spawns - nothing will be visible.");
            }

            var psr = ps.GetComponent<ParticleSystemRenderer>();

            if (psr != null)
            {
                psr.enabled = true;

                // A grafted trail comes off another prefab, so its sorting
                // is only right by luck. Pin it to THIS shot's sprite and
                // put it one order behind, which is what the stock trails do.
                if (grafted || spec.sortingOffset.HasValue)
                {
                    var sr = clone.GetComponent<SpriteRenderer>();

                    if (sr == null)
                        sr = clone.GetComponentInChildren<SpriteRenderer>(true);

                    if (sr != null)
                    {
                        psr.sortingLayerID = sr.sortingLayerID;
                        psr.sortingOrder =
                            sr.sortingOrder +
                            (spec.sortingOffset.HasValue
                                ? spec.sortingOffset.Value
                                : -1);
                    }
                }
            }

            ApplySprite(ps, spec, fileName);
            ApplySize(ps, spec, main);
            ApplyColor(ps, spec, fileName);
        }

        // The puff art. Particles do not have a SpriteRenderer - a sprite
        // reaches them through the Texture Sheet Animation module in Sprites
        // mode, which is exactly how the stock trail carries part_cyrcle_12.
        // An imported ANIMATION drops straight in as a multi-frame flipbook,
        // so every puff plays the whole sequence over its own lifetime.
        private static void ApplySprite(
            ParticleSystem ps,
            Spec spec,
            string fileName)
        {
            if (string.IsNullOrEmpty(spec.spriteName))
                return;

            ForgeSpriteLibrary.Art art;
            Sprite[] frames =
                ResolveFrames(spec.spriteName, fileName, "trail sprite",
                              out art);

            if (frames == null || frames.Length == 0)
                return;

            // Fallback NULL, which is this key's long-standing behaviour rather
            // than a new choice: a trail with neither an `fps` key nor an `@`
            // has always animated by particle lifetime, because the old code
            // passed the name's fps straight through and that is 0 when absent.
            // Deliberately NOT the animation's own authored fps - see
            // ForgeSpriteLibrary.ParticleFps for why the four callers differ.
            float? use = ForgeSpriteLibrary.ParticleFps(
                art, spec.fps, null, "trail sprite", fileName);

            VisualCustomizer.ApplyParticleSprite(ps, frames, use);
        }

        // Custom art first, then the game's own sprite atlas - same order
        // and same namespace rules as projectileSprite.
        // "what" names the thing being skinned so the not-found warning reads
        // correctly for a beam as well as a trail - this is shared now.
        public static Sprite[] ResolveFrames(
            string name, string fileName, string what = "trail sprite")
        {
            ForgeSpriteLibrary.Art ignored;
            return ResolveFrames(name, fileName, what, out ignored);
        }

        // Kept separate so the callers that have no fps key of their own (the
        // two beam sprite slots and the curve beam's chain, all of which can
        // only ever show one frame) need no change.
        // Hands the resolved `Art` back so the caller can put it through
        // `ForgeSpriteLibrary.ParticleFps` - which owns the "block key beats the
        // name, and `@life` means fit each particle's lifetime" rule for all
        // four particle keys. This overload replaced an `out float nameFps` one
        // that re-Split the name to recover only the fps: the Art already
        // carries the whole parsed spec, so asking twice was a second answer to
        // a question that already had an owner.
        public static Sprite[] ResolveFrames(
            string name, string fileName, string what,
            out ForgeSpriteLibrary.Art art)
        {
            // BOTH stock and custom resolution now happen in ForgeSpriteLibrary,
            // including the numbered runs and the `:frames@fps` suffix. This used
            // to keep a third copy of the stock lookup and a second call to
            // TryGetStockFrames; one owner per question is what makes the suffix
            // work everywhere at once.
            bool found = ForgeSpriteLibrary.TryGetArt(name, out art) ||
                         ForgeSpriteLibrary.TryGetStockArt(name, out art);

            if (found && art != null)
            {
                if (art.animation != null &&
                    art.animation.frames != null &&
                    art.animation.frames.Length > 0)
                {
                    return art.animation.frames;
                }

                if (art.sprite != null)
                    return new[] { art.sprite };
            }

            string known = ForgeSpriteLibrary.Count > 0
                ? " Loaded custom sprites: " +
                  string.Join(", ",
                      System.Linq.Enumerable.ToArray(
                          ForgeSpriteLibrary.Names))
                : " No custom sprites are loaded - is there a PNG in the " +
                  "'sprites' folder next to the DLL?";

            Log.LogWarning(
                fileName + ": " + what + " '" + name +
                "' was not found, so the template's own art is kept." +
                known + " Stock art worth trying: part_cyrcle_12, " +
                "part_cyrcle_16, part_engineFIre_0 for particles; " +
                "Sprite_Lazer_6_0 for a beam.");

            return null;
        }

        private static void ApplySize(
            ParticleSystem ps,
            Spec spec,
            ParticleSystem.MainModule main)
        {
            if (!spec.sizeEnd.HasValue)
                return;

            float start = main.startSize.constantMax;

            if (start <= 0f)
                return;

            // sizeOverLifetime is a MULTIPLIER on startSize, so the end size
            // has to be expressed as a fraction of the start. NOT clamped to
            // 1: a fraction above 1 makes each puff SWELL as it ages, which
            // is what expanding smoke or a widening contrail wants. Only
            // negative sizes are nonsense.
            float endFraction =
                Mathf.Max(0f, spec.sizeEnd.Value / start);

            var sol = ps.sizeOverLifetime;
            sol.enabled = true;
            sol.size =
                new ParticleSystem.MinMaxCurve(
                    1f, AnimationCurve.Linear(0f, 1f, 1f, endFraction));
        }

        // Colour and fade.
        //
        // colorOverLifetime MULTIPLIES startColor, which is the trap that bit
        // the muzzle flash: a cyan tint over a donor's red ramp multiplies to
        // black. So any ramp we inherit is either replaced with a pure
        // white-to-clear fade (safe by construction) or drained to greyscale
        // before the tint lands.
        private static void ApplyColor(
            ParticleSystem ps,
            Spec spec,
            string fileName)
        {
            // Only true when WE wrote the ramp, so it is known safe to
            // multiply a tint through. An inherited ramp never counts.
            bool safeRamp = false;

            if (spec.fade.HasValue)
            {
                var col = ps.colorOverLifetime;

                if (spec.fade.Value)
                {
                    col.enabled = true;

                    var g = new Gradient();

                    // Array setters, not SetKeys: SetKeys binds to a
                    // ReadOnlySpan overload that .NET Framework 4.7.2 has no
                    // type for, so it will not compile against Unity 6.
                    g.colorKeys = new[]
                    {
                        new GradientColorKey(Color.white, 0f),
                        new GradientColorKey(Color.white, 1f)
                    };

                    g.alphaKeys = new[]
                    {
                        new GradientAlphaKey(1f, 0f),
                        new GradientAlphaKey(0f, 1f)
                    };

                    col.color = new ParticleSystem.MinMaxGradient(g);
                    safeRamp = true;
                }
                else
                {
                    col.enabled = false;
                }
            }

            if (string.IsNullOrEmpty(spec.colorText))
                return;

            bool rainbow = VisualCustomizer.IsRainbow(spec.colorText);
            Color color = Color.white;

            if (!rainbow &&
                !VisualCustomizer.TryParseColor(spec.colorText, out color))
            {
                Log.LogWarning(
                    fileName + ": trail color '" + spec.colorText +
                    "' is not a hex value, colour name, ColorAsset or " +
                    "\"rainbow\" - ignored.");
                return;
            }

            // Claim the colour BEFORE anything writes it, so projectileColor
            // and a rainbow root both step around this child.
            if (ps.GetComponent<ForgeColorLock>() == null)
                ps.gameObject.AddComponent<ForgeColorLock>();

            if (!safeRamp)
                VisualCustomizer.NeutralizeColorRamp(ps.gameObject);

            if (rainbow)
            {
                // Scoped to the trail's own GameObject, so it cycles the
                // trail without touching the bullet.
                var rgb = ps.gameObject.AddComponent<RgbAnimator>();
                rgb.speed = spec.rgbSpeed;
                return;
            }

            var main = ps.main;
            main.startColor = color;
        }
    }
}
