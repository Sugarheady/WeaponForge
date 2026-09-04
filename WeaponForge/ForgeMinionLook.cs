using System;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;

namespace WeaponForge
{
    // Recolour and resize a spawned minion.
    //
    // Both halves rest on facts that had to be checked, because the obvious
    // implementation of each is wrong.
    //
    // COLOUR: **do not tint every SpriteRenderer.** A unit's AI state
    // indicators are SpriteRenderers too, and they are deliberately
    // colour-coded - green Idle, red Aggro, yellow Seek, blue Regroup, and on
    // other prefabs FLEE / Search / Charge / GotToLastKnownPosition. Tinting
    // everything recolours the AI's own tells. Every one of the 66 combatant
    // prefabs keeps its art under a child named `Visual*` (Visual_Drone,
    // Visual_Cross, Visual_Maggot, Visual), and the state sprites live outside
    // it - so that subtree is the boundary. The 15 spawnables without one are
    // exactly the crates, boxes, Dummy and FieldGenerator, which have no state
    // sprites to protect; those fall back to a name filter.
    //
    // And tinting is SAFE from the damage flash, which was the other worry:
    // `DamageHighlight` swaps `sharedMaterial` and never touches
    // `SpriteRenderer.color`, so a tint survives being shot.
    //
    // SCALE: the root transform is the right place. The Animator is never on the
    // root - it sits on `Visual*` - and **not one of the game's 404 animation
    // clips animates scale at all**, so nothing overwrites it. The
    // CircleCollider2D scales with the transform, so the hitbox genuinely
    // follows the art rather than only looking bigger. Mass does NOT follow on
    // its own, which is why scaling it is an option: otherwise a giant minion
    // stays as light as a fly and gets flung by every knockback.
    public static class ForgeMinionLook
    {
        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("WeaponForge.Minion");

        public const int ColorReplace = 0;  // flat, unmistakable (default)
        public const int ColorTint = 1;     // multiply, keeps shading

        public const int ScaleFull = 0;     // art + hitbox + mass (default)
        public const int ScaleHitbox = 1;   // art + hitbox, mass untouched
        public const int ScaleArt = 2;      // art only, hitbox unchanged

        public static int ParseColorMode(string s)
        {
            switch ((s ?? "").Trim().ToLowerInvariant())
            {
                case "tint":
                case "multiply":
                case "shade":
                    return ColorTint;
                default:
                    return ColorReplace;
            }
        }

        public static string ColorWord(int mode)
        {
            return mode == ColorTint ? "tint" : "replace";
        }

        public static int ParseScaleMode(string s)
        {
            switch ((s ?? "").Trim().ToLowerInvariant())
            {
                case "hitbox":
                case "nomass":
                    return ScaleHitbox;
                case "art":
                case "visual":
                case "cosmetic":
                    return ScaleArt;
                default:
                    return ScaleFull;
            }
        }

        public static string ScaleWord(int mode)
        {
            switch (mode)
            {
                case ScaleHitbox: return "hitbox (mass untouched)";
                case ScaleArt: return "art only";
                default: return "art + hitbox + mass";
            }
        }

        // AI state sprite names, for the fallback path only. Taken from the
        // prefabs rather than guessed: these are the state-machine node names
        // that carry an indicator sprite.
        private static readonly HashSet<string> StateNames =
            new HashSet<string>(System.StringComparer.OrdinalIgnoreCase)
            {
                "idle", "move", "wait", "aggro", "agro", "seek", "regroup",
                "flee", "search", "charge", "entry", "aim move", "move away",
                "move_dodge", "combat", "investigate",
                "gottolastknownposition"
            };

        public static void Apply(Unit minion, ForgeMinion.Config cfg)
        {
            if (minion == null || cfg == null)
                return;

            try
            {
                if (cfg.hasColor)
                    Recolour(minion, cfg);

                if (cfg.hasTrailColor || cfg.trailMatchesBody)
                    PaintTrail(minion, cfg);

                if (cfg.scale != 1f && cfg.scale > 0f)
                    Resize(minion, cfg);
            }
            catch (System.Exception e)
            {
                Log.LogError("Restyling a minion failed: " + e);
            }
        }

        // ------------------------------------------------------------------
        // The movement smear - the little coloured streak a unit leaves as it
        // moves, themed per drone module (Caps orange, Electron cyan, Purple
        // and Phantom violet, Suicide red).
        //
        // WHY THE BODY RECOLOUR NEVER TOUCHED IT: Recolour walks
        // SpriteRenderers, and a trail is a ParticleSystem. It already sits
        // inside the Visual subtree we scope to, so it was excluded by TYPE,
        // not by location - which is why the body recoloured and the trail
        // stayed factory.
        //
        // IDENTIFIED BY SHAPE, NOT BY NAME. Every trail in this game is a
        // child ParticleSystem emitting by rateOverDistance (there is no
        // TrailRenderer anywhere in PUNK), and every unit prefab has exactly
        // one - `ParticlesSmear`. Matching on "emits by distance" rather than
        // on a name means this works on any unit without a lookup table, and
        // leaves the engine fire and hover puffs (which emit over time) alone.
        private static void PaintTrail(Unit minion, ForgeMinion.Config cfg)
        {
            Color c = cfg.trailMatchesBody ? cfg.color : cfg.trailColor;

            Transform visual = FindVisual(minion.transform);

            Component root = (visual != null) ? (Component)visual : minion;

            int touched = 0;

            foreach (ParticleSystem ps in
                     root.GetComponentsInChildren<ParticleSystem>(true))
            {
                if (ps == null || !EmitsByDistance(ps))
                    continue;

                // startColor is the whole job here, and only because the
                // lifetime gradient happens to be colourless: all 106
                // ParticlesSmear instances in the game fade pure white from
                // alpha 0.74 to 0. colorOverLifetime MULTIPLIES startColor, so
                // a COLOURED gradient would tint whatever we asked for - if a
                // future prefab ever has one, this is where to neutralise it.
                var main = ps.main;
                main.startColor = c;

                touched++;
            }

            if (touched == 0)
            {
                Log.LogWarning(
                    "minion \"trailColor\": '" + minion.name + "' has no " +
                    "movement trail to paint (nothing on it emits particles " +
                    "by distance). Every stock unit has one, so this is " +
                    "likely a prefab that was never meant to leave a streak. " +
                    "Everything else still applied.");
            }
        }

        // A trail emits as the object MOVES; everything else emits over time.
        private static bool EmitsByDistance(ParticleSystem ps)
        {
            var emission = ps.emission;

            if (!emission.enabled)
                return false;

            // `.constant` is the right read: the stock trails all use a plain
            // scalar (5 on a drone, 3 on a Cross) rather than a curve.
            return emission.rateOverDistance.constant > 0f ||
                   emission.rateOverDistanceMultiplier > 0f &&
                   emission.rateOverDistance.mode !=
                       ParticleSystemCurveMode.Constant;
        }

        // ------------------------------------------------------------------
        private static void Recolour(Unit minion, ForgeMinion.Config cfg)
        {
            Transform visual = FindVisual(minion.transform);
            int touched = 0;

            if (visual != null)
            {
                foreach (SpriteRenderer sr in
                         visual.GetComponentsInChildren<SpriteRenderer>(true))
                {
                    if (Paint(sr, cfg))
                        touched++;
                }
            }
            else
            {
                // No Visual subtree - a crate, a box, the Dummy. Tint
                // everything except anything that looks like an AI state
                // indicator, so the fallback cannot do what the whole
                // Visual-scoping exists to prevent.
                foreach (SpriteRenderer sr in
                         minion.GetComponentsInChildren<SpriteRenderer>(true))
                {
                    if (sr == null || IsStateSprite(sr.transform))
                        continue;

                    if (Paint(sr, cfg))
                        touched++;
                }
            }

            if (touched == 0)
            {
                Log.LogWarning(
                    "minion \"color\": found no sprite to recolour on '" +
                    minion.name + "'.");
            }
        }

        // A renderer's ALPHA is a visibility decision, never a colour one -
        // and Replace mode used to overwrite it.
        //
        // THE BUG THIS FIXES, because it was reported as something much
        // stranger than it is. Every Larva and Maggot prefab keeps a
        // `HighlightSprite` inside its `Visual_*` subtree: a full silhouette of
        // the creature, held at alpha 0 and flashed by the damage effect.
        // `sr.color = cfg.color` set that sprite to an OPAQUE colour, so a
        // recoloured minion wore its own silhouette over its real art - which
        // is exactly "changing the color puts their weird default sprite to the
        // forefront and not this diamond shape".
        //
        // So: multiply the requested alpha by the renderer's own in BOTH modes.
        // Something invisible stays invisible; something opaque still takes the
        // colour asked for. And skip anything named like a highlight outright,
        // since it is an effect rather than art and there is nothing to gain by
        // colouring it.
        private static bool Paint(SpriteRenderer sr, ForgeMinion.Config cfg)
        {
            if (sr == null)
                return false;

            if (IsHighlight(sr.transform))
                return false;

            Color had = sr.color;

            if (cfg.colorMode == ColorTint)
            {
                // Multiply over what the prefab already had. A fresh instance
                // still carries the prefab's authored colour at this point, so
                // this needs no "original" bookkeeping.
                sr.color = new Color(
                    had.r * cfg.color.r,
                    had.g * cfg.color.g,
                    had.b * cfg.color.b,
                    had.a * cfg.color.a);
            }
            else
            {
                sr.color = new Color(
                    cfg.color.r,
                    cfg.color.g,
                    cfg.color.b,
                    cfg.color.a * had.a);
            }

            return true;
        }

        // Internal rather than private: ForgeUnitTint has to skip exactly the
        // same renderers, for exactly the same reason. One owner of the rule.
        internal static bool IsHighlight(Transform t)
        {
            return t != null &&
                   t.name.IndexOf(
                       "highlight",
                       StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // Internal rather than private: ForgeSlow tints a slowed unit and
        // needs exactly the same scoping, for exactly the same reason - the
        // AI's state indicators are sprites too.
        internal static Transform FindVisual(Transform root)
        {
            // Direct children first - that is where it always is - then a full
            // walk, so an unusual prefab still resolves.
            for (int i = 0; i < root.childCount; i++)
            {
                Transform c = root.GetChild(i);

                if (c != null && c.name != null &&
                    c.name.StartsWith("Visual",
                        System.StringComparison.OrdinalIgnoreCase))
                {
                    return c;
                }
            }

            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
            {
                if (t != null && t.name != null &&
                    t.name.StartsWith("Visual",
                        System.StringComparison.OrdinalIgnoreCase))
                {
                    return t;
                }
            }

            return null;
        }

        // Internal for the same reason as IsHighlight above - the tint owner
        // needs the identical fallback rule when there is no Visual subtree.
        internal static bool IsStateSprite(Transform t)
        {
            for (Transform p = t; p != null; p = p.parent)
            {
                if (p.name == null)
                    continue;

                if (StateNames.Contains(p.name.Trim()))
                    return true;

                if (p.name.StartsWith("AI_", System.StringComparison.OrdinalIgnoreCase) ||
                    p.name.StartsWith("AiS ", System.StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        // ------------------------------------------------------------------
        private static void Resize(Unit minion, ForgeMinion.Config cfg)
        {
            float s = Mathf.Clamp(cfg.scale, 0.1f, 8f);

            if (cfg.scaleMode == ScaleArt)
            {
                Transform visual = FindVisual(minion.transform);

                // Art-only on a prefab with no Visual subtree would have to
                // scale the root, which is the opposite of art-only - so say so
                // rather than silently changing the hitbox.
                if (visual == null)
                {
                    Log.LogWarning(
                        "minion \"scaleMode\": \"art\" needs a Visual subtree " +
                        "and '" + minion.name + "' has none (the crates and " +
                        "boxes do not). Left unscaled rather than quietly " +
                        "resizing its hitbox too.");
                    return;
                }

                visual.localScale = visual.localScale * s;
                return;
            }

            // Root scale: the collider goes with it, so the hitbox is real.
            minion.transform.localScale = minion.transform.localScale * s;

            if (cfg.scaleMode != ScaleFull)
                return;

            // Mass by AREA, not by side. This is 2D: a minion twice as wide
            // covers four times the space, so scale squared is what makes a big
            // one feel heavy instead of being punted across the room by every
            // explosion.
            var rb = minion.GetComponent<Rigidbody2D>();

            if (rb == null)
                rb = minion.GetComponentInChildren<Rigidbody2D>();

            if (rb != null)
                rb.mass = rb.mass * s * s;
        }
    }
}
