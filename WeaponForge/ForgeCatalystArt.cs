using UnityEngine;

namespace WeaponForge
{
    // A coin, drawn rather than imported.
    //
    // Generated for the same reason the shield arc is: it needs no art file to
    // install, it cannot be lost, and a preset that references it works the
    // moment it is loaded. Stock art could not be borrowed anyway - every
    // sprite in the game lives packed in one 1024x2048 atlas, and neighbouring
    // pixels bleed in as soon as you scale one up.
    //
    // The SPIN is not part of the texture. A coin turning about its vertical
    // axis is just a disc whose apparent WIDTH oscillates, so one round sprite
    // plus an animated localScale.x reads exactly right and costs nothing -
    // where a flipbook would need a dozen frames and a texture sheet.
    public static class ForgeCatalystArt
    {
        private const int Size = 64;

        private static Sprite _coin;

        public static Sprite Coin()
        {
            if (_coin != null)
                return _coin;

            var tex = new Texture2D(Size, Size, TextureFormat.RGBA32, false);
            tex.filterMode = FilterMode.Bilinear;
            tex.wrapMode = TextureWrapMode.Clamp;

            var px = new Color[Size * Size];

            // Gold, lit from the upper left so it reads as a solid object
            // rather than a flat token.
            Color rim = new Color(0.55f, 0.38f, 0.05f, 1f);
            Color face = new Color(0.98f, 0.79f, 0.22f, 1f);
            Color shine = new Color(1f, 0.95f, 0.65f, 1f);
            Color mark = new Color(0.72f, 0.50f, 0.08f, 1f);

            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x < Size; x++)
                {
                    float dx = (x + 0.5f) / Size * 2f - 1f;
                    float dy = (y + 0.5f) / Size * 2f - 1f;
                    float d = Mathf.Sqrt(dx * dx + dy * dy);

                    if (d > 1f)
                    {
                        px[y * Size + x] = Color.clear;
                        continue;
                    }

                    Color c;

                    if (d > 0.82f)
                    {
                        // Milled edge.
                        c = rim;
                    }
                    else
                    {
                        // Face, with a soft highlight toward the upper left and
                        // a small centre mark so the spin is legible.
                        float lit = Mathf.Clamp01(
                            0.5f - (dx * 0.45f) + (dy * 0.45f));

                        c = Color.Lerp(face, shine, lit * 0.8f);

                        float ring = Mathf.Abs(d - 0.45f);

                        if (ring < 0.07f)
                            c = Color.Lerp(c, mark, 1f - ring / 0.07f);
                    }

                    // One pixel of feather at the outer edge, or the disc
                    // crawls with aliasing while it spins.
                    float a = Mathf.Clamp01((1f - d) / (1.5f / Size));

                    c.a = a;
                    px[y * Size + x] = c;
                }
            }

            tex.SetPixels(px);
            tex.Apply();

            // 1 world unit across at scale 1, so the weapon's own
            // projectileScale is what sizes it.
            _coin = Sprite.Create(
                tex, new Rect(0f, 0f, Size, Size), new Vector2(0.5f, 0.5f),
                Size, 0, SpriteMeshType.FullRect);

            _coin.name = "Forge Coin";
            return _coin;
        }
    }
}
