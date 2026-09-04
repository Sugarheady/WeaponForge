using System;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;

namespace WeaponForge
{
    // The chain of quads that replaces the one stretched quad.
    //
    // Rides on the HitscanWeaponVisual instance, so it is destroyed with it -
    // HitscanWeapon.Dispose destroys every visual when the weapon changes, and
    // a pool of orphaned segment objects left behind would be a slow leak
    // across a run.
    //
    // WHY THE SEGMENTS ARE POSITIONED IN WORLD SPACE. UpdateVisual rotates the
    // visual's root transform to face the beam's straight-line endpoint every
    // single frame, and it does that whether we want it or not. Anything
    // parented to it with local coordinates would be dragged around by that
    // rotation. Writing world position and rotation per link sidesteps the
    // parent's transform entirely, so the container below exists purely to own
    // the objects' lifetime.
    //
    // WHY THE LINKS OVERLAP. Two quads meeting at an angle leave a
    // wedge-shaped gap on the OUTSIDE of the bend - small at 4 degrees a link,
    // very visible at 20. Extending each link past its endpoint by a fraction
    // of its own length fills that wedge. It is the difference between a beam
    // that is connected the whole way through and a dotted line that follows a
    // curve.
    public class ForgeCurveBeamChain : MonoBehaviour
    {
        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("WeaponForge.CurveBeam");

        private Transform container;
        private readonly List<SpriteRenderer> pool = new List<SpriteRenderer>();

        private Sprite[] frames;
        private bool customArt;

        public void Hide()
        {
            for (int i = 0; i < pool.Count; i++)
            {
                if (pool[i] != null)
                    pool[i].enabled = false;
            }
        }

        // ------------------------------------------------------------------
        public void Draw(
            List<Vector2> points,
            ForgeCurveBeam.Config cfg,
            SpriteRenderer donor)
        {
            if (cfg == null || points == null || points.Count < 2)
            {
                Hide();
                return;
            }

            try
            {
                EnsureArt(cfg);

                if (frames == null || frames.Length == 0)
                {
                    Hide();
                    return;
                }

                EnsureContainer();

                int links = points.Count - 1;

                // How far the pattern has travelled. Floor rather than round so
                // each link holds its frame for a whole step instead of
                // shimmering between two at the boundary.
                int n = frames.Length;

                int scroll = -Mathf.FloorToInt(Time.time * cfg.flow * n);

                float thickness = cfg.Thickness;
                float taper = Mathf.Clamp01(cfg.taper);
                float overlap = Mathf.Clamp(cfg.overlap, 0f, 2f);

                for (int i = 0; i < links; i++)
                {
                    Vector2 a = points[i];
                    Vector2 b = points[i + 1];

                    Vector2 span = b - a;
                    float len = span.magnitude;

                    if (len < 0.0001f)
                        continue;

                    SpriteRenderer r = Take(i, donor);

                    if (r == null)
                        break;

                    // Proper modulo: C# gives a negative result for a negative
                    // dividend, and a negative sprite index throws.
                    int f = ((i + scroll) % n + n) % n;

                    r.sprite = frames[f];

                    // A generated link has its colour baked into the pixels, so
                    // tinting again would square it. A custom sprite is
                    // somebody else's art, so `color` tints it the way every
                    // other art key in the mod does.
                    r.color = (customArt && cfg.hasColor)
                        ? cfg.color
                        : Color.white;

                    float t = (links > 1) ? (i / (float)(links - 1)) : 0f;
                    float thick = thickness * Mathf.Lerp(1f, 1f - taper, t);

                    r.size = new Vector2(
                        len * (1f + overlap), Mathf.Max(0.01f, thick));

                    r.transform.SetPositionAndRotation(
                        a,
                        Quaternion.FromToRotation(Vector3.right, span));

                    r.enabled = true;
                }

                // Anything the path no longer needs.
                for (int i = links; i < pool.Count; i++)
                {
                    if (pool[i] != null)
                        pool[i].enabled = false;
                }
            }
            catch (Exception e)
            {
                Log.LogError("Drawing a curve beam failed: " + e);
                Hide();
            }
        }

        // ------------------------------------------------------------------
        private void EnsureContainer()
        {
            if (container != null)
                return;

            var go = new GameObject("Forge Curve Beam");
            go.transform.SetParent(transform, false);

            container = go.transform;
        }

        private SpriteRenderer Take(int index, SpriteRenderer donor)
        {
            while (pool.Count <= index)
            {
                if (pool.Count >= ForgeCurveBeamPath.MaxPoints)
                    return null;

                var go = new GameObject("Link " + pool.Count);
                go.transform.SetParent(container, false);

                var r = go.AddComponent<SpriteRenderer>();

                // Borrow everything that decides WHERE a beam is drawn from the
                // beam this weapon already had. Getting the sorting layer wrong
                // hides a sprite completely, in a way that looks exactly like a
                // feature that never ran - the lesson the charge meter taught.
                if (donor != null)
                {
                    r.sharedMaterial = donor.sharedMaterial;
                    r.sortingLayerID = donor.sortingLayerID;
                    r.sortingOrder = donor.sortingOrder;
                }

                r.drawMode = SpriteDrawMode.Sliced;
                r.enabled = false;

                pool.Add(r);
            }

            return pool[index];
        }

        private void EnsureArt(ForgeCurveBeam.Config cfg)
        {
            if (cfg.artTried)
            {
                frames = cfg.art;
                customArt = !string.IsNullOrEmpty(cfg.sprite);
                return;
            }

            cfg.artTried = true;

            if (!string.IsNullOrEmpty(cfg.sprite))
            {
                Sprite[] found = ForgeTrail.ResolveFrames(
                    cfg.sprite, "curveBeam", "curveBeam sprite");

                if (found != null && found.Length > 0)
                {
                    // Every stock beam sprite is authored with a left-edge
                    // pivot because the quad grows rightward from its start,
                    // and a link is exactly the same shape of problem. The
                    // rebuild is cached, so one sprite used by five weapons
                    // becomes one extra Sprite rather than five.
                    var repivoted = new Sprite[found.Length];

                    for (int i = 0; i < found.Length; i++)
                        repivoted[i] = ForgeBeam.LeftPivot(found[i]);

                    cfg.art = repivoted;
                    frames = repivoted;
                    customArt = true;

                    if (found.Length == 1)
                    {
                        Log.LogInfo(
                            "curveBeam \"sprite\": '" + cfg.sprite + "' is a " +
                            "single sprite, so every link looks the same and " +
                            "\"flow\" has nothing to travel. Use an ANIMATION " +
                            "of 2 or more frames for a pattern that moves " +
                            "along the beam - unlike the plain \"beam\" block, " +
                            "a chain can use every frame.");
                    }

                    return;
                }

                string known = ForgeSpriteLibrary.Count > 0
                    ? " Loaded custom art: " +
                      string.Join(", ", ForgeSpriteLibrary.Names) + "."
                    : " There is no art in the sprites folder yet.";

                Log.LogWarning(
                    "curveBeam \"sprite\": '" + cfg.sprite + "' is not one of " +
                    "your sprites, so the generated links are used instead." +
                    known);
            }

            cfg.art = ForgeCurveBeamArt.Build(
                cfg.Frames,
                cfg.hasColor ? cfg.color : new Color(0.6f, 0.85f, 1f, 1f));

            frames = cfg.art;
            customArt = false;
        }
    }
}
