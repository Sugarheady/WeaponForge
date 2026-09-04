using System;
using System.Reflection;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace WeaponForge
{
    // Recolors and resizes weapon visuals. The game bakes projectile /
    // beam appearance into the prefab's child renderers and shares those
    // prefabs with the stock weapon, so we never mutate an original:
    // we clone the visual prefab, tint/scale the clone, and hand the
    // clone back to the weapon. Clones are parented under a permanently
    // inactive holder so instantiating them never runs Awake/OnEnable
    // (no ghost projectiles/minions at world origin); each still has
    // activeSelf == true, so the game's own Instantiate spawns them
    // active as normal.
    public static class VisualCustomizer
    {
        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("WeaponForge");

        private static Transform _holder;

        private static Transform Holder()
        {
            if (_holder == null)
            {
                var go = new GameObject("WeaponForge Prefab Cache");
                go.SetActive(false);
                go.hideFlags = HideFlags.HideAndDontSave;
                UnityEngine.Object.DontDestroyOnLoad(go);
                _holder = go.transform;
            }

            return _holder;
        }

        public static GameObject ClonePrefab(GameObject original)
        {
            // Parented to an inactive holder => Awake/OnEnable do not
            // fire on the clone.
            GameObject clone =
                UnityEngine.Object.Instantiate(original, Holder());

            clone.hideFlags = HideFlags.HideAndDontSave;
            return clone;
        }

        // The animated-RGB sentinel, spelled either way. Every color key
        // in the mod accepts both spellings, so they share one test.
        public static bool IsRainbow(string text)
        {
            if (string.IsNullOrEmpty(text))
                return false;

            string t = text.Trim();
            return t.Equals("rainbow", System.StringComparison.OrdinalIgnoreCase) ||
                   t.Equals("rgb", System.StringComparison.OrdinalIgnoreCase);
        }

        // Accepts "#rrggbb" / "#rrggbbaa", HTML color names ("red",
        // "cyan"...), or a game ColorAsset name ("ColorPurple").
        public static bool TryParseColor(string text, out Color color)
        {
            color = Color.white;

            if (string.IsNullOrEmpty(text))
                return false;

            text = text.Trim();

            if (text.StartsWith("#"))
                return ColorUtility.TryParseHtmlString(text, out color);

            var colorAsset =
                JsonFieldMapper.FindAsset(
                    typeof(ColorAsset), text) as ColorAsset;

            if (colorAsset != null)
            {
                color = colorAsset.color;
                return true;
            }

            // Fall back to Unity's HTML color names.
            return ColorUtility.TryParseHtmlString(text, out color);
        }

        // Has some child claimed its own colour (see ForgeColorLock)?
        //
        // "self" is the GameObject doing the painting, and it is EXEMPT: a
        // lock exists to keep outside sweeps off a child, not to stop that
        // child from colouring itself. So a trail with its own RgbAnimator
        // still animates, while the projectile root's sweep steps around it.
        // Pass null from a plain one-shot recolor, which is never the owner.
        //
        // includeInactive matters: cloned prefabs hang off an INACTIVE holder
        // so Awake cannot fire on them, and the default overload would walk
        // straight past every parent in that hierarchy.
        public static bool ColorLocked(Component c, GameObject self)
        {
            if (c == null)
                return false;

            var owner = c.GetComponentInParent<ForgeColorLock>(true);

            if (owner == null)
                return false;

            return self == null || owner.gameObject != self;
        }

        // Tint every renderer in the hierarchy. Covers projectile
        // sprites/trails and the hitscan beam's sprites/light/particle.
        public static void Recolor(GameObject root, Color c)
        {
            foreach (var sr in
                root.GetComponentsInChildren<SpriteRenderer>(true))
            {
                if (ColorLocked(sr, null))
                    continue;

                sr.color = c;
            }

            foreach (var lr in
                root.GetComponentsInChildren<LineRenderer>(true))
            {
                if (ColorLocked(lr, null))
                    continue;

                lr.startColor = c;
                lr.endColor = c;
            }

            foreach (var tr in
                root.GetComponentsInChildren<TrailRenderer>(true))
            {
                if (ColorLocked(tr, null))
                    continue;

                tr.startColor = c;
                tr.endColor =
                    new Color(c.r, c.g, c.b, 0f);
            }

            foreach (var ps in
                root.GetComponentsInChildren<ParticleSystem>(true))
            {
                if (ColorLocked(ps, null))
                    continue;

                var main = ps.main;
                main.startColor = c;
            }

            foreach (var light in
                root.GetComponentsInChildren<Light2D>(true))
            {
                if (ColorLocked(light, null))
                    continue;

                light.color = c;
            }
        }

        // --- Faction / targeting ---------------------------------
        // A projectile only hits what its GameObject layer collides
        // with. Enemy projectiles bake to "EnemyProjectiles" (hits the
        // player); "PlayerProjectiles" hits enemies and, via the physics
        // matrix, spares the player. We re-faction by SWAPPING only the
        // projectile-faction layer, leaving every other layer (ground,
        // triggers, deployables) untouched - blanket re-layering breaks
        // prefabs like air mines that mix layers on purpose. Resolved by
        // name so it survives layer-index changes; -1 if the layer is
        // missing.
        public static int EnemyProjectileLayer()
        {
            return LayerMask.NameToLayer("EnemyProjectiles");
        }

        public static int PlayerProjectileLayer()
        {
            return LayerMask.NameToLayer("PlayerProjectiles");
        }

        // The layer a re-factioned prefab's root is EXPECTED to be on if
        // it still needs swapping ("from"), and what to swap it to.
        public static int FactionFromLayer(string target)
        {
            return target == "player"
                ? PlayerProjectileLayer()
                : EnemyProjectileLayer();
        }

        public static int FactionToLayer(string target)
        {
            return target == "player"
                ? EnemyProjectileLayer()
                : PlayerProjectileLayer();
        }

        // Recursively remap ONLY the objects currently on `from` to `to`.
        public static void RemapLayer(GameObject go, int from, int to)
        {
            if (go == null || from < 0 || to < 0)
                return;

            if (go.layer == from)
                go.layer = to;

            foreach (Transform child in go.transform)
                RemapLayer(child.gameObject, from, to);
        }

        // Hitscan has no faction check - only its layerMask matters.
        // "enemies" => Entities+Ground+Fruits (like the player laser);
        // "player" => Player+Ground+Fruits (like the enemy laser).
        public static LayerMask HitscanMask(string target)
        {
            if (target == "player")
                return LayerMask.GetMask("Player", "Ground", "Fruits");

            return LayerMask.GetMask("Entities", "Ground", "Fruits");
        }

        // Attach the RGB/rainbow cycler to a cloned prefab so every
        // instance spawned from it animates its own color.
        public static void ApplyRainbow(GameObject root, float speed)
        {
            ApplyRainbow(root, speed, false);
        }

        // multiply = tint the artist's own colors rather than overwriting
        // them, so gradients and alpha fades survive (see Tint).
        public static void ApplyRainbow(
            GameObject root, float speed, bool multiply)
        {
            var animator = root.AddComponent<RgbAnimator>();
            animator.speed = speed;
            animator.multiply = multiply;
        }

        // --- Multiply-tint -----------------------------------------
        // Recolor() REPLACES every color with a flat one, which is right
        // for a projectile sprite but wrong for a particle burst: a
        // muzzle flash stores its variety in a startColor GRADIENT (the
        // stock ones are RandomColor over white -> transparent), so
        // flattening it turns a flickering flash into a solid blob.
        // Tint() multiplies instead: the artist's gradient, alpha fade
        // and random spread all survive, just pushed toward your hue.
        // Because it multiplies, a DARK tint dims the effect rather than
        // darkening it - use bright hues.
        public static void Tint(GameObject root, Color c)
        {
            foreach (var ps in
                root.GetComponentsInChildren<ParticleSystem>(true))
            {
                var main = ps.main;
                main.startColor = TintGradient(main.startColor, c);
            }

            foreach (var sr in
                root.GetComponentsInChildren<SpriteRenderer>(true))
            {
                sr.color = Multiply(sr.color, c);
            }

            foreach (var tr in
                root.GetComponentsInChildren<TrailRenderer>(true))
            {
                tr.startColor = Multiply(tr.startColor, c);
                tr.endColor = Multiply(tr.endColor, c);
            }

            foreach (var light in
                root.GetComponentsInChildren<Light2D>(true))
            {
                light.color = Multiply(light.color, c);
            }
        }

        public static Color Multiply(Color a, Color b)
        {
            return new Color(a.r * b.r, a.g * b.g, a.b * b.b, a.a * b.a);
        }

        // Unity multiplies a particle's startColor by its
        // colorOverLifetime ramp, so tinting startColor alone is not
        // enough: 4 of the 11 stock muzzle prefabs (PopperRed, Laser,
        // LaserRed, CrawlerLaser) ship a HARD-CODED colored ramp - white
        // fading to red or blue - which would annihilate the requested
        // hue. Tinting the ramp as well would square the tint, so instead
        // we drain the ramp of hue and keep only its brightness: every
        // color key becomes its own luminance grey. The artist's fade
        // curve survives untouched, the color now comes purely from the
        // tint, and a prefab whose ramp is already white/disabled is
        // unaffected. Build-time, on the private clone only.
        // Returns how many systems actually had a colour ramp to drain, so a
        // caller can say "your art was going to be painted over" only when it
        // was true.
        public static int NeutralizeColorRamp(GameObject root)
        {
            int drained = 0;

            foreach (var ps in
                root.GetComponentsInChildren<ParticleSystem>(true))
            {
                var col = ps.colorOverLifetime;
                if (!col.enabled)
                    continue;

                col.color = GreyGradient(col.color);
                drained++;
            }

            return drained;
        }

        // Drain the hue from each system's START COLOUR, keeping its
        // brightness and its random range.
        //
        // The companion to NeutralizeColorRamp, and needed for the same reason
        // from the other direction. That method only touches
        // colorOverLifetime, on the stated assumption that "every stock muzzle
        // keeps its colour in the fade ramp and starts white". A census of the
        // eleven muzzle prefabs says otherwise: **CrawlerLaser and CrossAlpha
        // carry their colour in startColor** - blue/purple (0.57, 0, 1) and
        // cyan (0.09, 0.96, 1). Unity multiplies startColor into the particle,
        // so custom art on those two came out tinted.
        //
        // And both are minMaxState RandomColor, which picks a colour per
        // particle from the gradient - so the art did not come out reliably
        // blue, it came out blue SOMETIMES. That is the symptom that got
        // reported, and it is why this was hard to pin down.
        public static int NeutralizeStartColor(GameObject root)
        {
            int drained = 0;

            foreach (var ps in
                root.GetComponentsInChildren<ParticleSystem>(true))
            {
                var main = ps.main;

                if (!HasHue(main.startColor))
                    continue;

                main.startColor = GreyGradient(main.startColor);
                drained++;
            }

            return drained;
        }

        // Is there any actual colour in here, as opposed to greyscale
        // brightness? Used so a prefab that was already neutral does not get
        // reported as having been changed.
        private static bool HasHue(ParticleSystem.MinMaxGradient g)
        {
            switch (g.mode)
            {
                case ParticleSystemGradientMode.Color:
                    return Coloured(g.color);

                case ParticleSystemGradientMode.TwoColors:
                    return Coloured(g.colorMin) || Coloured(g.colorMax);

                case ParticleSystemGradientMode.Gradient:
                    return ColouredKeys(g.gradient);

                case ParticleSystemGradientMode.RandomColor:
                    return ColouredKeys(g.gradientMax);

                case ParticleSystemGradientMode.TwoGradients:
                    return ColouredKeys(g.gradientMin) ||
                           ColouredKeys(g.gradientMax);

                default:
                    return false;
            }
        }

        private static bool ColouredKeys(Gradient g)
        {
            if (g == null)
                return false;

            GradientColorKey[] keys = g.colorKeys;

            for (int i = 0; i < keys.Length; i++)
            {
                if (Coloured(keys[i].color))
                    return true;
            }

            return false;
        }

        private static bool Coloured(Color c)
        {
            float hi = Mathf.Max(c.r, Mathf.Max(c.g, c.b));
            float lo = Mathf.Min(c.r, Mathf.Min(c.g, c.b));

            // Saturation, near enough. Grey is r == g == b at any brightness,
            // so anything with a spread between its channels has a hue.
            return hi - lo > 0.04f;
        }

        private static ParticleSystem.MinMaxGradient GreyGradient(
            ParticleSystem.MinMaxGradient g)
        {
            switch (g.mode)
            {
                case ParticleSystemGradientMode.Color:
                    return new ParticleSystem.MinMaxGradient(Grey(g.color));

                case ParticleSystemGradientMode.TwoColors:
                    return new ParticleSystem.MinMaxGradient(
                        Grey(g.colorMin), Grey(g.colorMax));

                case ParticleSystemGradientMode.Gradient:
                    return new ParticleSystem.MinMaxGradient(
                        GreyKeys(g.gradient));

                case ParticleSystemGradientMode.TwoGradients:
                    return new ParticleSystem.MinMaxGradient(
                        GreyKeys(g.gradientMin), GreyKeys(g.gradientMax));

                case ParticleSystemGradientMode.RandomColor:
                    var random = new ParticleSystem.MinMaxGradient(
                        GreyKeys(g.gradientMax));
                    random.mode = ParticleSystemGradientMode.RandomColor;
                    return random;

                default:
                    return g;
            }
        }

        private static Gradient GreyKeys(Gradient g)
        {
            if (g == null)
                return null;

            GradientColorKey[] keys = g.colorKeys;
            for (int i = 0; i < keys.Length; i++)
                keys[i].color = Grey(keys[i].color);

            var result = new Gradient();
            result.mode = g.mode;
            result.colorKeys = keys;
            result.alphaKeys = g.alphaKeys;
            return result;
        }

        // Rec. 709 luminance, so a dark ramp stays dark.
        private static Color Grey(Color c)
        {
            float y = c.r * 0.2126f + c.g * 0.7152f + c.b * 0.0722f;
            return new Color(y, y, y, c.a);
        }

        // Tint a particle MinMaxGradient in whichever mode it is using,
        // keeping that mode. Every branch is needed - the stock muzzle
        // flashes use RandomColor, projectiles use flat Color, and some
        // effects use TwoColors.
        public static ParticleSystem.MinMaxGradient TintGradient(
            ParticleSystem.MinMaxGradient g, Color c)
        {
            switch (g.mode)
            {
                case ParticleSystemGradientMode.Color:
                    return new ParticleSystem.MinMaxGradient(
                        Multiply(g.color, c));

                case ParticleSystemGradientMode.TwoColors:
                    return new ParticleSystem.MinMaxGradient(
                        Multiply(g.colorMin, c),
                        Multiply(g.colorMax, c));

                case ParticleSystemGradientMode.Gradient:
                    return new ParticleSystem.MinMaxGradient(
                        MultiplyGradient(g.gradient, c));

                case ParticleSystemGradientMode.TwoGradients:
                    return new ParticleSystem.MinMaxGradient(
                        MultiplyGradient(g.gradientMin, c),
                        MultiplyGradient(g.gradientMax, c));

                case ParticleSystemGradientMode.RandomColor:
                    // Reads a random stop along gradientMax, which is the
                    // same slot the single-gradient ctor fills - so build
                    // it that way, then restore the mode.
                    var random = new ParticleSystem.MinMaxGradient(
                        MultiplyGradient(g.gradientMax, c));
                    random.mode = ParticleSystemGradientMode.RandomColor;
                    return random;

                default:
                    return g;
            }
        }

        // A Gradient keeps color and alpha on SEPARATE key tracks, and
        // Evaluate takes rgb from one and a from the other - so the tint
        // has to be applied to both tracks or its alpha is silently lost.
        // (The stock muzzle flashes fade via their COLOR keys, white ->
        // black, with alpha pinned at 1, so tinting only the color track
        // would make "#ffffff80" a no-op.) Writes into 'reuse' when
        // given, to keep a per-frame animator from allocating a Gradient
        // every tick.
        public static Gradient MultiplyGradient(
            Gradient g, Color c, Gradient reuse = null)
        {
            if (g == null)
                return null;

            GradientColorKey[] keys = g.colorKeys;
            for (int i = 0; i < keys.Length; i++)
                keys[i].color = Multiply(keys[i].color, c);

            GradientAlphaKey[] alphas = g.alphaKeys;
            for (int i = 0; i < alphas.Length; i++)
                alphas[i].alpha = alphas[i].alpha * c.a;

            // Assign the two key arrays directly rather than via
            // SetKeys: this Unity's SetKeys resolves to a ReadOnlySpan
            // overload, which .NET Framework 4.7.2 has no type for.
            Gradient result = reuse ?? new Gradient();
            result.mode = g.mode;
            result.colorKeys = keys;
            result.alphaKeys = alphas;
            return result;
        }

        // Swap the art on a projectile-style prefab for a custom sprite.
        // The stock ammo prefabs carry their SpriteRenderer on the ROOT
        // (verified on ammo_Popper and ammo_Bolt) with the child objects
        // being particle systems, so the root is tried first and a child
        // only as a fallback for prefabs shaped differently. Child
        // particles/trails are deliberately left alone - they are separate
        // art with their own flipbooks.
        public static bool SwapSprite(GameObject root, Sprite sprite)
        {
            if (root == null || sprite == null)
                return false;

            var sr = root.GetComponent<SpriteRenderer>();

            if (sr == null)
                sr = root.GetComponentInChildren<SpriteRenderer>(true);

            if (sr == null)
                return false;

            sr.sprite = sprite;
            return true;
        }

        // A projectile prefab is usually MORE than one sprite: ammo_Popper
        // carries child particle systems called particle, particle_glow and
        // trail, ammo_Worm has two, ammo_Bolt has a trail. SwapSprite only
        // replaces the SpriteRenderer, so all that extra art keeps drawing and
        // custom art looks like it was pasted ON TOP of the original rather
        // than replacing it. This switches the extras off, leaving just the one
        // sprite we swapped.
        //
        // Components are DISABLED, never destroyed - a child could carry
        // something functional, and disabling is reversible and cheap.
        public static int IsolateSprite(GameObject root)
        {
            if (root == null)
                return 0;

            // The one SwapSprite would have used, so it survives.
            var keep = root.GetComponent<SpriteRenderer>();
            if (keep == null)
                keep = root.GetComponentInChildren<SpriteRenderer>(true);

            int hidden = 0;

            foreach (var sr in
                root.GetComponentsInChildren<SpriteRenderer>(true))
            {
                if (sr == keep)
                    continue;
                sr.enabled = false;
                hidden++;
            }

            foreach (var ps in
                root.GetComponentsInChildren<ParticleSystem>(true))
            {
                var emission = ps.emission;
                emission.enabled = false;
                ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

                var psr = ps.GetComponent<ParticleSystemRenderer>();
                if (psr != null)
                    psr.enabled = false;

                hidden++;
            }

            foreach (var tr in
                root.GetComponentsInChildren<TrailRenderer>(true))
            {
                tr.enabled = false;
                hidden++;
            }

            foreach (var lr in
                root.GetComponentsInChildren<LineRenderer>(true))
            {
                lr.enabled = false;
                hidden++;
            }

            return hidden;
        }

        // Put a sprite (or a flipbook) on a ParticleSystem.
        //
        // Particles have no SpriteRenderer - art reaches them through the
        // Texture Sheet Animation module in Sprites mode, which is exactly how
        // the stock trail carries part_cyrcle_12 and how the muzzle flash
        // carries its 20-frame flicker. Shared by the projectile trail and the
        // beam's impact spark, which need identical handling.
        //
        // fps null = spread the sequence over each particle's lifetime, so it
        // always finishes exactly as the particle dies and stays in step if the
        // lifetime changes later. A value pins it to a fixed frame rate.
        // How many puffs, how big, and do they turn with the emitter.
        //
        // These three answer the whole "my flash looks like a cloud of tiny
        // clouds" complaint, and each one is a different part of the system:
        //
        //  count  - EVERY stock flash and impact puff emits by BURSTS, not by
        //           rate (rateOverTime is 0 on all of them). MuzzleParticle
        //           Popper fires bursts of 30 AND 20 across its two systems -
        //           fifty puffs. That is fine for a smoke puff and terrible
        //           for one piece of directional art, which wants 1.
        //  size   - startSize, in WORLD UNITS. Particles ignore pixels-per-unit
        //           entirely (the quad is sized by startSize, not by the
        //           sprite), which is why redrawing the art bigger or changing
        //           the sheet's PPU does nothing at all here. Every stock
        //           flash ships startSize 1.
        //  rotate - the renderer's ALIGNMENT. Every stock prefab ships
        //           m_RenderAlignment: 0 = View, so particles face the camera
        //           and ignore the emitter's rotation completely. That is why
        //           art drawn to point somewhere never points anywhere.
        //           Local aligns them to the emitter's transform instead - and
        //           the game already rotates that transform to the aim
        //           direction for a muzzle (WeaponBase.DoShoot) and to the
        //           surface normal for an impact
        //           (ImpactVisualizer.VisualizeImpact).
        //
        // Returns a short human summary, or null when nothing was asked for.
        //  spread - how far the puff THROWS ITSELF from the point it was
        //           spawned at, as a multiple of the prefab's own scatter.
        //           This is the one that makes drawn art usable, and it is
        //           two settings rather than one: a stock flash gives every
        //           particle a random launch speed (Popper: 0-40 on one
        //           system, 0-70 on the other) AND spawns it anywhere inside
        //           a circle (radius 0.68 / 0.49). So even at count 1 the
        //           single puff lands somewhere different every shot, which
        //           is exactly the "why won't it stay put" complaint.
        //           0 pins both: one puff, dead on the barrel or dead on the
        //           hit point. 1 is stock. Impacts scatter by speed alone -
        //           their spawn radius is already 0.0001.
        public static string ShapeParticles(
            GameObject root,
            int? count,
            float? size,
            bool? rotate,
            float? spread,
            float? lifetime = null,
            float? gravity = null)
        {
            if (root == null ||
                (!count.HasValue && !size.HasValue && !rotate.HasValue &&
                 !spread.HasValue && !lifetime.HasValue && !gravity.HasValue))
            {
                return null;
            }

            ParticleSystem[] systems =
                root.GetComponentsInChildren<ParticleSystem>(true);

            if (systems.Length == 0)
                return null;

            int burstsSet = 0;
            int noBursts = 0;

            for (int i = 0; i < systems.Length; i++)
            {
                ParticleSystem ps = systems[i];

                if (count.HasValue)
                {
                    ParticleSystem.EmissionModule em = ps.emission;
                    int n = em.burstCount;

                    if (n == 0)
                    {
                        noBursts++;
                    }
                    else
                    {
                        for (int b = 0; b < n; b++)
                        {
                            ParticleSystem.Burst burst = em.GetBurst(b);
                            burst.count = (float)count.Value;
                            em.SetBurst(b, burst);
                            burstsSet++;
                        }
                    }
                }

                if (size.HasValue)
                {
                    ParticleSystem.MainModule main = ps.main;
                    main.startSize = size.Value;
                }

                if (lifetime.HasValue)
                {
                    // A flat constant, so it replaces whatever range the stock
                    // system had. That is the point: a puff you asked to live
                    // 2 seconds should live 2 seconds, not 0.6-2.
                    ParticleSystem.MainModule mainLife = ps.main;
                    mainLife.startLifetime =
                        new ParticleSystem.MinMaxCurve(
                            Mathf.Max(0.01f, lifetime.Value));
                }

                if (gravity.HasValue)
                {
                    // Unity's sign convention, kept deliberately rather than
                    // flipped into a friendlier "rise" key: values here get
                    // copied verbatim off stock prefabs while tuning, and
                    // `ImpactParticle Cell Tuff` reads -0.05. Inventing an
                    // opposite sign would make every such number wrong.
                    ParticleSystem.MainModule mainGrav = ps.main;
                    mainGrav.gravityModifier = gravity.Value;
                }

                if (rotate.HasValue)
                {
                    var r = ps.GetComponent<ParticleSystemRenderer>();

                    if (r != null)
                    {
                        r.alignment = rotate.Value
                            ? ParticleSystemRenderSpace.Local
                            : ParticleSystemRenderSpace.View;
                    }
                }

                if (spread.HasValue)
                {
                    float k = Mathf.Max(0f, spread.Value);

                    // startSpeedMultiplier rather than startSpeed: the stock
                    // value is a RANDOM RANGE (two constants), and assigning a
                    // plain number would flatten that to a single speed. The
                    // multiplier scales whatever curve is already there, so
                    // spread 0.5 keeps the shape of the randomness and halves
                    // it, and spread 0 zeroes the whole range.
                    ParticleSystem.MainModule mainSpeed = ps.main;
                    mainSpeed.startSpeedMultiplier *= k;

                    // And the spawn area, which is the other half of it on a
                    // muzzle flash.
                    ParticleSystem.ShapeModule shape = ps.shape;

                    if (shape.enabled)
                        shape.radius *= k;
                }
            }

            var parts = new System.Collections.Generic.List<string>();

            if (count.HasValue)
            {
                parts.Add(count.Value + " per burst" +
                          (noBursts > 0
                              ? " (" + noBursts + " system(s) emit by rate, " +
                                "not bursts - unchanged)"
                              : ""));
            }

            if (size.HasValue)
                parts.Add("size " + size.Value);

            if (rotate.HasValue)
            {
                parts.Add(rotate.Value
                    ? "turning with the emitter"
                    : "camera-facing");
            }

            if (spread.HasValue)
            {
                parts.Add(spread.Value <= 0f
                    ? "pinned to one spot (no scatter)"
                    : "scatter x" + spread.Value);
            }

            if (lifetime.HasValue)
                parts.Add("lifetime " + lifetime.Value.ToString("0.###") + "s");

            if (gravity.HasValue)
            {
                parts.Add("gravity " + gravity.Value.ToString("0.###") +
                          (gravity.Value < 0f
                              ? " (drifts UP)"
                              : (gravity.Value > 0f ? " (falls)" : " (none)")));
            }

            return string.Join(", ", parts.ToArray()) +
                   " across " + systems.Length + " system" +
                   (systems.Length == 1 ? "" : "s");
        }

        public static void ApplyParticleSprite(
            ParticleSystem ps,
            Sprite[] frames,
            float? fps)
        {
            if (ps == null || frames == null || frames.Length == 0)
                return;

            var uv = ps.textureSheetAnimation;
            uv.enabled = true;
            uv.mode = ParticleSystemAnimationMode.Sprites;
            uv.numTilesX = 1;
            uv.numTilesY = 1;

            // Overwrite in place, then trim: a donor already has sprites in
            // the list, and SetSprite on an index that does not exist throws.
            for (int i = 0; i < frames.Length; i++)
            {
                if (i < uv.spriteCount)
                    uv.SetSprite(i, frames[i]);
                else
                    uv.AddSprite(frames[i]);
            }

            while (uv.spriteCount > frames.Length)
                uv.RemoveSprite(uv.spriteCount - 1);

            if (frames.Length == 1)
            {
                // One frame: hold it. Stock systems ship a frameOverTime curve
                // with cycleCount 10, harmless on a single sprite but read as
                // "loop it ten times" once our list is longer.
                uv.cycleCount = 1;
                uv.frameOverTime = new ParticleSystem.MinMaxCurve(0f);
                return;
            }

            uv.cycleCount = 1;
            uv.frameOverTime =
                new ParticleSystem.MinMaxCurve(
                    1f, AnimationCurve.Linear(0f, 0f, 1f, 1f));

            if (fps.HasValue && fps.Value > 0f)
            {
                uv.timeMode = ParticleSystemAnimationTimeMode.FPS;
                uv.fps = fps.Value;
            }
            else
            {
                uv.timeMode = ParticleSystemAnimationTimeMode.Lifetime;
            }
        }

        // Some ammo prefabs render through the game's EMISSIVE sprite shader
        // (Ammo Derbis does), which is right for a glowing debris chunk but
        // blows detailed custom art out toward white - it reads as "the game
        // recoloured my sprite". Swapping the renderer's material for the
        // plain unlit one the Popper uses makes the art draw as painted.
        // sharedMaterial, not material: we point at the existing asset
        // rather than instantiating a per-renderer copy.
        public static bool SwapMaterial(GameObject root, Material mat)
        {
            if (root == null || mat == null)
                return false;

            var found = root.GetComponentsInChildren<SpriteRenderer>(true);
            if (found.Length == 0)
                return false;

            for (int i = 0; i < found.Length; i++)
                found[i].sharedMaterial = mat;

            return true;
        }

        // Static sprite or animated flipbook, whichever the name resolved
        // to. The first frame is written straight away so the prefab looks
        // right even before an instance's Awake runs.
        public static bool ApplyArt(
            GameObject root, ForgeSpriteLibrary.Art art)
        {
            if (root == null || art == null)
                return false;

            if (!SwapSprite(root, art.sprite))
                return false;

            if (art.animation == null)
                return true;

            // Same reason as the note in ForgeSpriteLibrary.OptInFrames: a
            // projectile is drawn by a SpriteRenderer, so there is no particle
            // lifetime for `@life` to fit to. Do not default in silence.
            if (art.spec != null && art.spec.fitLifetime)
            {
                Log.LogWarning(
                    "'@life' fits an animation to a PARTICLE's lifetime, and " +
                    "projectileSprite is drawn by a sprite renderer. It works " +
                    "on impactSprite, muzzleSprite, the trail and the beam's " +
                    "impactSprite. Using " +
                    art.animation.fps.ToString("0.##") + " fps here instead.");
            }

            var anim = root.AddComponent<ForgeSpriteAnimation>();
            anim.frames = art.animation.frames;
            anim.fps = art.animation.fps;
            anim.loop = art.animation.loop;
            anim.randomStart = art.animation.randomStart;
            return true;
        }

        // Flip ONE named renderer through a frame list.
        //
        // `ApplyArt` above takes a GameObject and lets the animator find a
        // renderer for itself, which is right for a projectile prefab whose art
        // is on the root. The opt-in animation on the gas puff, shield arc,
        // swipe blade, catalyst node and charge meter is the other shape: the
        // caller has already built the exact renderer it wants driven, and some
        // of those objects hold SEVERAL (a gas cloud is up to 24 puffs, a
        // catalyst node has a face per child). Attaching to `sr.gameObject`
        // makes the animator's own `GetComponent<SpriteRenderer>()` resolve to
        // that one renderer and no other.
        //
        // `randomStart` matters wherever a caller drives more than one: without
        // it, 24 puffs flip in lock-step and read as one strobing blob rather
        // than a churning cloud - the same reason a shotgun's pellets need it.
        // `restart` is for a renderer reused across separate appearances - pass
        // true from the swipe blade, which is one object switched on per swing.
        public static void AnimateRenderer(
            SpriteRenderer sr,
            Sprite[] frames,
            float fps,
            bool randomStart,
            bool restart = false)
        {
            if (sr == null || frames == null || frames.Length < 2)
                return;

            // Idempotent: a caller that re-dresses a live object (the catalyst
            // node re-runs Dress, the swipe blade re-shows every swing) must not
            // stack animators fighting over one renderer.
            var anim = sr.GetComponent<ForgeSpriteAnimation>();

            if (anim == null)
                anim = sr.gameObject.AddComponent<ForgeSpriteAnimation>();

            anim.frames = frames;
            anim.fps = (fps > 0f) ? fps : 12f;
            anim.loop = ForgeSpriteAnimation.LoopMode.Loop;
            anim.randomStart = randomStart;

            if (restart)
                anim.Restart();
        }

        // Multiply the visual (and, for a caller-passed radius, physics)
        // size of a projectile-style prefab.
        public static void Scale(GameObject root, float multiplier)
        {
            root.transform.localScale =
                root.transform.localScale * multiplier;
        }

        // Beam thickness lives in the sprite renderers' size.y (the
        // beam is a sliced sprite; size.x = length is rewritten every
        // frame, size.y = thickness persists). Both fields are private.
        public static void ScaleBeamThickness(
            HitscanWeaponVisual visual,
            float multiplier)
        {
            ScaleSpriteHeight(visual, "fireSpriteRenderer", multiplier);
            ScaleSpriteHeight(visual, "warmUpSpriteRenderer", multiplier);
        }

        private static void ScaleSpriteHeight(
            HitscanWeaponVisual visual,
            string fieldName,
            float multiplier)
        {
            FieldInfo field =
                typeof(HitscanWeaponVisual).GetField(
                    fieldName,
                    BindingFlags.NonPublic | BindingFlags.Instance);

            if (field == null)
                return;

            var sr = field.GetValue(visual) as SpriteRenderer;

            if (sr == null)
                return;

            sr.size = new Vector2(
                sr.size.x,
                sr.size.y * multiplier);
        }
    }
}
