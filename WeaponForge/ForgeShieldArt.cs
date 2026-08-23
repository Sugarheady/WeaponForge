using UnityEngine;

namespace WeaponForge
{
    // Draws the arc, because nothing in the game could be reused for it.
    //
    // The stock Shield is a full ring: its shader takes _Radius, _RingAlpha,
    // _InnerAlpha, _InnerGradientWidth, _BackgroundEnabled and
    // _BackgroundScrollSpeed, and there is no angle parameter anywhere in it.
    // A 40-degree wedge is simply not a configuration of that asset, so the
    // sprite is generated instead - which has the side benefit that `angle`,
    // `radius` and `thickness` are always exactly what the catching maths uses,
    // rather than art that has to be kept in sync with them by hand.
    //
    // Orientation: +X is the facing direction, and the pivot is the CENTRE of
    // the circle (the ship), because ForgeShieldController positions the art at
    // the ship and rotates it with FromToRotation(Vector3.right, facing).
    public static class ForgeShieldArt
    {
        private const int Size = 256;

        // `pointed` sweeps the ends of the wedge back toward the ship, which is
        // the difference between the two sketched shapes: a smooth curve for
        // the reflector, a swept chevron for the rammer.
        public static Sprite Build(
            float angleDegrees,
            float thickness,
            float radius,
            bool pointed)
        {
            float half = Mathf.Clamp(angleDegrees, 1f, 360f) * 0.5f;

            // "Pointed" sweeps the ENDS of the wedge back toward the ship
            // rather than pushing the middle forward. Both read as a chevron,
            // but only this one keeps the outermost point of the art at exactly
            // radius + thickness/2 - which is ForgeShield.Config.Reach, the
            // distance the catching and the ram damage actually use. Bulging
            // forward instead would draw a tip that reaches further than it
            // hits.
            float sweep = pointed
                ? Mathf.Min(radius * 0.55f, Mathf.Max(thickness, radius * 0.35f))
                : 0f;

            // Everything the sprite must contain, plus a little margin so the
            // outer edge is not clipped by the texture boundary.
            float reach = radius + thickness * 0.5f;
            float extent = reach * 1.04f;

            var tex = new Texture2D(Size, Size, TextureFormat.RGBA32, false);
            tex.filterMode = FilterMode.Bilinear;
            tex.wrapMode = TextureWrapMode.Clamp;

            var px = new Color[Size * Size];

            // World units per pixel, and the soft edge width in the same units.
            float perPixel = (extent * 2f) / Size;
            float feather = Mathf.Max(perPixel * 1.5f, thickness * 0.18f);

            for (int y = 0; y < Size; y++)
            {
                // +0.5 samples pixel centres; without it the wedge sits half a
                // pixel off and the arc looks lopsided at small sizes.
                float wy = ((y + 0.5f) / Size * 2f - 1f) * extent;

                for (int x = 0; x < Size; x++)
                {
                    float wx = ((x + 0.5f) / Size * 2f - 1f) * extent;

                    px[y * Size + x] = Sample(
                        wx, wy, half, radius, thickness, sweep, feather);
                }
            }

            tex.SetPixels(px);
            tex.Apply();

            // Pixels per unit chosen so the texture spans exactly 2 * extent
            // world units, which puts the arc at the radius that was asked for.
            float ppu = Size / (extent * 2f);

            Sprite s = Sprite.Create(
                tex,
                new Rect(0f, 0f, Size, Size),
                new Vector2(0.5f, 0.5f),
                ppu,
                0,
                SpriteMeshType.FullRect);

            s.name = "Forge Shield Arc";
            return s;
        }

        private static Color Sample(
            float wx,
            float wy,
            float halfAngle,
            float radius,
            float thickness,
            float sweep,
            float feather)
        {
            float r = Mathf.Sqrt(wx * wx + wy * wy);

            if (r < 0.0001f)
                return Color.clear;

            float ang = Mathf.Abs(Mathf.Atan2(wy, wx) * Mathf.Rad2Deg);

            if (ang > halfAngle)
                return Color.clear;

            // The band's centre line. A pointed shield keeps its tip at
            // `radius` and pulls the two ends back toward the ship - linear in
            // angle, which reads as the swept-back chevron rather than a
            // bubble, and keeps the furthest point of the art equal to Reach.
            float centre = radius;

            if (sweep > 0f)
                centre -= sweep * (ang / Mathf.Max(0.0001f, halfAngle));

            float dist = Mathf.Abs(r - centre);
            float edge = thickness * 0.5f;

            if (dist > edge)
                return Color.clear;

            // Soft across the band, and soft at the two ends of the wedge, so
            // the arc does not terminate in a hard staircase.
            float aRadial = Mathf.Clamp01((edge - dist) / Mathf.Max(0.0001f, feather));

            float angFeather = Mathf.Max(1.5f, halfAngle * 0.12f);
            float aAngular = Mathf.Clamp01(
                (halfAngle - ang) / angFeather);

            float a = Mathf.Min(aRadial, aAngular);

            // Brighter along the centre line, which gives the band some depth
            // instead of reading as a flat stripe.
            float core = 1f - Mathf.Clamp01(dist / Mathf.Max(0.0001f, edge));
            float lift = 0.55f + 0.45f * core;

            return new Color(lift, lift, lift, a);
        }
    }
}
