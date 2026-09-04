using UnityEngine;

namespace WeaponForge
{
    // Draws the blade: a tapered crescent - a curved LINE rather than a wedge.
    //
    // Generated rather than shipped, for the reason ForgeShieldArt records: art
    // built from the same `arc`, `reach` and `thickness` the swing actually uses
    // can never drift out of sync with the hitbox. Change `reach` and the
    // picture changes with it, with nothing to keep in step by hand.
    //
    // THE SHAPE. An annulus segment at radius `reach`, whose radial thickness
    // tapers to nothing at both angular ends. That taper is the whole
    // difference between this and the shield's wedge: a wedge reads as a
    // barrier, a lens-shaped crescent reads as an edge that has just swept
    // through. Fat in the middle, pointed at the tips.
    //
    // ORIENTATION AND PIVOT, which the controller depends on: +X is the middle
    // of the crescent and the pivot is the CENTRE OF THE CIRCLE - i.e. the ship
    // - not the centre of the drawn shape. So the controller can place the art
    // at the ship and rotate it, and the crescent sweeps around the ship the
    // way an arm would. Same contract as ForgeShieldArt.
    public static class ForgeSwipeArt
    {
        private const int Size = 256;

        public static Sprite Build(
            float arcDegrees, float thickness, float reach, Color tint)
        {
            float half = Mathf.Clamp(arcDegrees, 1f, 360f) * 0.5f;

            // A hair of margin so the outer edge is not clipped by the texture
            // boundary and the tips do not look chopped.
            float extent = Mathf.Max(0.01f, reach + thickness * 0.5f) * 1.06f;

            var tex = new Texture2D(Size, Size, TextureFormat.RGBA32, false);
            tex.filterMode = FilterMode.Bilinear;
            tex.wrapMode = TextureWrapMode.Clamp;

            var px = new Color[Size * Size];

            float perPixel = (extent * 2f) / Size;

            // The soft edge. Wide enough to read as a glow at this resolution,
            // never so wide that a thin blade dissolves entirely.
            float feather = Mathf.Max(perPixel * 1.5f, thickness * 0.30f);

            for (int y = 0; y < Size; y++)
            {
                // +0.5 samples pixel centres - without it the crescent sits
                // half a pixel off and looks lopsided at small sizes.
                float wy = ((y + 0.5f) / Size * 2f - 1f) * extent;

                for (int x = 0; x < Size; x++)
                {
                    float wx = ((x + 0.5f) / Size * 2f - 1f) * extent;

                    float a = Sample(wx, wy, half, reach, thickness, feather);

                    px[y * Size + x] = new Color(
                        tint.r, tint.g, tint.b, tint.a * a);
                }
            }

            tex.SetPixels(px);
            tex.Apply();

            // Chosen so the texture spans exactly 2 * extent world units, which
            // is what puts the crescent at the reach that was asked for.
            float ppu = Size / (extent * 2f);

            Sprite s = Sprite.Create(
                tex,
                new Rect(0f, 0f, Size, Size),
                new Vector2(0.5f, 0.5f),
                ppu,
                0,
                SpriteMeshType.FullRect);

            s.name = "Forge Swipe Blade";
            return s;
        }

        // Coverage at one point, 0..1.
        private static float Sample(
            float wx, float wy,
            float half, float reach, float thickness, float feather)
        {
            float r = Mathf.Sqrt(wx * wx + wy * wy);

            if (r < 0.0001f)
                return 0f;

            // Angle off +X, signed, in degrees.
            float ang = Mathf.Atan2(wy, wx) * Mathf.Rad2Deg;
            float away = Mathf.Abs(Mathf.DeltaAngle(0f, ang));

            if (away > half)
                return 0f;

            // THE TAPER. u is 0 in the middle of the crescent and 1 at its
            // tips; the profile takes the thickness to zero there. Squared
            // rather than linear so the belly stays broad and only the last
            // stretch narrows - a linear taper reads as a triangle.
            float u = (half > 0.0001f) ? (away / half) : 0f;
            float profile = 1f - u * u;

            if (profile <= 0f)
                return 0f;

            float halfThick = thickness * 0.5f * profile;

            // Distance from the blade's centre line, and a soft edge across it.
            float d = Mathf.Abs(r - reach);

            float a = 1f - Mathf.Clamp01((d - halfThick) / Mathf.Max(0.0001f, feather));

            // Fade the very tips as well as the sides, so they end in a point
            // rather than a cut-off stub.
            a *= Mathf.Clamp01(profile * 3f);

            return Mathf.Clamp01(a);
        }
    }
}
