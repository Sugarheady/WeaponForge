using UnityEngine;

namespace WeaponForge
{
    // The links.
    //
    // Generated rather than shipped, for the reason ForgeSwipeArt and
    // ForgeShieldArt both record: art built from the same numbers the thing
    // actually uses cannot drift out of sync with it. Change `thickness` and
    // the drawn link changes with it, with nothing to keep in step by hand.
    //
    // WHAT THE FRAMES ARE FOR, and it is not animation in the usual sense.
    // These are not played in time on one quad - they are laid out ALONG the
    // chain, one frame per link, with the starting index scrolled by `flow`.
    // So frame 0 sits on link 0, frame 1 on link 1, and next frame the whole
    // assignment shifts by one: the pattern travels down the beam toward the
    // target while every individual quad stays put.
    //
    // That is the one thing the plain `beam` block cannot do. It warns you that
    // only frame 0 of an animation is usable, because one stretched quad has no
    // flipbook. A chain has a frame per link, so an animation finally means
    // something on a beam.
    //
    // PIVOT IS LEFT-CENTRE (0, 0.5), matching every stock beam sprite and for
    // the same reason: a link is placed AT its start point and grown toward its
    // end, so a centred pivot would hang half of each link out behind itself.
    public static class ForgeCurveBeamArt
    {
        private const int Wide = 64;
        private const int Tall = 48;

        // The beam material is EmissiveUnlitSprite, which blows detail toward
        // white - the same problem projectileGlow solves for bullets. So the
        // core is drawn deliberately hot and the body is left saturated, or the
        // whole beam comes out as a white noodle.
        public static Sprite[] Build(int frames, Color tint)
        {
            int n = Mathf.Clamp(frames, 1, 16);

            var built = new Sprite[n];

            for (int i = 0; i < n; i++)
                built[i] = One(i, n, tint);

            return built;
        }

        private static Sprite One(int index, int count, Color tint)
        {
            var tex = new Texture2D(Wide, Tall, TextureFormat.RGBA32, false);
            tex.filterMode = FilterMode.Bilinear;
            tex.wrapMode = TextureWrapMode.Clamp;

            var px = new Color[Wide * Tall];

            // The pulse. Peaky rather than sinusoidal - squaring the cosine
            // keeps most links slim and makes the swollen one read as a
            // distinct node travelling along, which is what "a link chain"
            // looks like. With count 1 there is no pulse at all and the beam
            // is an even line.
            float phase = (count > 1) ? (index / (float)count) : 0f;

            float pulse = (count > 1)
                ? Mathf.Pow(0.5f + 0.5f * Mathf.Cos(phase * Mathf.PI * 2f), 2f)
                : 0f;

            float nodeRadius = 0.16f + 0.26f * pulse;

            for (int y = 0; y < Tall; y++)
            {
                // -1 at the bottom edge, +1 at the top.
                float v = ((y + 0.5f) / Tall) * 2f - 1f;

                for (int x = 0; x < Wide; x++)
                {
                    float u = (x + 0.5f) / Wide;          // 0..1 along the link

                    // ---- the connecting core ------------------------------
                    //
                    // Runs the FULL width, edge to edge. That is what keeps the
                    // beam continuous: a link that faded out at its own ends
                    // would leave a bead-string with gaps, however much the
                    // links overlap.
                    float core = Falloff(Mathf.Abs(v), 0.26f, 0.34f);

                    // ---- the node ----------------------------------------
                    //
                    // An ellipse at the middle of the link. Scaled on x by the
                    // aspect so it stays round-ish once the quad is stretched
                    // to a link's real proportions.
                    float dx = (u - 0.5f) * 1.6f;
                    float dy = v * 0.75f;

                    float node = Falloff(
                        Mathf.Sqrt(dx * dx + dy * dy), nodeRadius, 0.30f);

                    float a = Mathf.Max(core, node);

                    if (a <= 0f)
                    {
                        px[y * Wide + x] = new Color(0f, 0f, 0f, 0f);
                        continue;
                    }

                    // Hot centre, saturated edges. The lift is strongest right
                    // down the middle of the core and inside the node, so the
                    // beam reads as having an interior rather than being a flat
                    // stripe.
                    float heat = Mathf.Max(
                        Falloff(Mathf.Abs(v), 0.05f, 0.22f),
                        node * 0.85f);

                    px[y * Wide + x] = new Color(
                        Mathf.Lerp(tint.r, 1f, heat * 0.8f),
                        Mathf.Lerp(tint.g, 1f, heat * 0.8f),
                        Mathf.Lerp(tint.b, 1f, heat * 0.8f),
                        tint.a * a);
                }
            }

            tex.SetPixels(px);
            tex.Apply();

            Sprite s = Sprite.Create(
                tex,
                new Rect(0f, 0f, Wide, Tall),
                new Vector2(0f, 0.5f),          // LEFT-centre, like every beam
                Wide,                            // arbitrary: Sliced draw mode
                0,                               // sizes by `size`, not by PPU
                // FullRect, not Tight: Sliced and Tiled draw modes refuse to
                // render a Tight-meshed sprite at all.
                SpriteMeshType.FullRect);

            s.name = "Forge Curve Beam Link " + index;
            s.hideFlags = HideFlags.HideAndDontSave;

            return s;
        }

        // 1 inside `edge`, falling to 0 across `feather`.
        private static float Falloff(float d, float edge, float feather)
        {
            if (d <= edge)
                return 1f;

            return Mathf.Clamp01(1f - (d - edge) / Mathf.Max(0.0001f, feather));
        }
    }
}
