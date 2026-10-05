using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace WeaponForge
{
    // The reach indicator - a circle on the ground at the distance a bloom can
    // still be placed.
    //
    // It exists because bloom is the one weapon in the mod whose range is
    // INVISIBLE. A normal bullet shows you its reach by falling short; a bloom
    // past its range does not miss, it silently lands somewhere nearer than you
    // pointed, and nothing on screen says which. So this is a readout, not a
    // decoration - which is also why it is OFF by default and has to be asked
    // for. An always-on circle around the ship is clutter for someone who has
    // learned the range.
    //
    // Purely cosmetic in the strict sense: every path here can fail and the
    // weapon keeps working. A missing sprite warns once and draws nothing.
    public class ForgeBloomRing : MonoBehaviour
    {
        private static readonly ManualLogSource Log =
            ForgeLog.Source("WeaponForge.Bloom");

        private static bool _saidNoSprite;

        private SpriteRenderer _sr;
        private string _spriteName;
        private int _thicknessPx = DefaultThicknessPx;
        private float _unitSize = 1f;
        private bool _failed;

        // ---- R19 276: fade, opacity, width -----------------------------
        //
        // The ring is DRIVEN each frame from Shooter.Update and PAINTED in
        // LateUpdate, so a fade can finish after the driver stops asking -
        // which is exactly the case that matters: put the gun away mid-fade
        // and nothing would call Drive again to finish taking the ring down.
        private float _alpha;             // 0..1, what is on screen
        private bool _want;               // does the driver want it shown?
        private int _drivenFrame = -1;
        private Color _color = Color.white;
        private float _fade;
        private float _opacity = 1f;

        // WHICH SHOOTER THE RING BELONGS TO.
        //
        // A ship has more than one Shooter (primary and secondary), both of
        // them ticking every frame, and the ring is ONE component on the unit.
        // Without an owner the two fight: the bloom weapon calls Drive(true)
        // and the other gun calls Drive(false) in the same frame, so whichever
        // runs last decides - the circle flickers, or never appears at all
        // depending on component order. Only the owner drives it, and only the
        // owner can put it away.
        [System.NonSerialized] public Object claim;

        public void Drive(
            bool visible, float radius, Color color, string spriteName)
        {
            Drive(visible, radius, color, spriteName, 0f, 1f, 0f);
        }

        public void Drive(
            bool visible, float radius, Color color, string spriteName,
            float fade, float opacity, float width)
        {
            _drivenFrame = Time.frameCount;

            // A HIDE KEEPS THE LOOK IT IS FADING OUT OF. The swap path asks
            // with Color.clear and no fade, and taking those would make the
            // fade-out instant and invisible - the ring would simply vanish,
            // which is the "jarring" 276 is about.
            if (!visible || radius <= 0.01f)
            {
                _want = false;
                return;
            }

            _fade = Mathf.Max(0f, fade);
            _opacity = Mathf.Clamp01(opacity);
            _color = color;

            // The line's thickness in the drawn texture: a width in world
            // units is a FRACTION of the diameter, and the texture is 256
            // across. Quantised to whole pixels so a ring that is growing with
            // a Range module rebuilds a handful of times, not every frame.
            int px = DefaultThicknessPx;

            if (width > 0f && string.IsNullOrEmpty(spriteName))
            {
                px = Mathf.Clamp(
                    Mathf.RoundToInt(width / (radius * 2f) * DrawnSize), 1, 64);
            }

            if (_sr == null || _spriteName != spriteName || _thicknessPx != px)
            {
                _thicknessPx = px;

                // A FAILED LOOKUP LATCHES. TryGetStockArt re-scans every loaded
                // asset on each call, and without this a missing sprite would
                // do that EVERY FRAME for the rest of the run - the warning is
                // gated once, so the cost would have been completely silent.
                if (_failed && _spriteName == spriteName)
                    return;

                if (!Build(spriteName))
                {
                    _failed = true;
                    return;
                }

                _failed = false;
            }

            _want = true;

            // A sprite's own world size is its bounds; scaling from that is
            // what makes the drawn circle match the number rather than merely
            // correlate with it.
            float want = (radius * 2f) / Mathf.Max(0.0001f, _unitSize);

            _sr.transform.localScale = new Vector3(want, want, 1f);

            // The ring belongs to the WORLD, not to the ship's facing - it
            // would otherwise spin with the hull, which reads as a targeting
            // reticle rather than a distance.
            _sr.transform.rotation = Quaternion.identity;
        }

        private bool Build(string spriteName)
        {
            _spriteName = spriteName;

            Sprite sprite = null;
            ForgeSpriteLibrary.Art art;

            if (string.IsNullOrEmpty(spriteName))
            {
                sprite = DrawnRing(_thicknessPx);
            }
            else if (ForgeSpriteLibrary.TryGetAnyArt(spriteName, out art) &&
                     art != null)
            {
                sprite = art.sprite;
            }

            if (sprite == null)
            {
                if (!_saidNoSprite)
                {
                    _saidNoSprite = true;

                    Log.LogWarning(
                        "bloom ringSprite '" + spriteName + "' resolved to " +
                        "nothing, so the reach circle is not drawn. The " +
                        "weapon is unaffected. " +
                        ForgeSpriteLibrary.NotFoundHint());
                }

                return false;
            }

            if (_sr == null)
            {
                var go = new GameObject("ForgeBloomRing");
                go.transform.SetParent(transform, false);
                go.transform.localPosition = Vector3.zero;

                _sr = go.AddComponent<SpriteRenderer>();

                // Behind everything that matters. A reach circle that draws
                // over the enemies you are trying to shoot is worse than none.
                _sr.sortingOrder = -50;
            }

            _sr.sprite = sprite;

            Bounds b = sprite.bounds;

            _unitSize = Mathf.Max(b.size.x, b.size.y);

            if (_unitSize <= 0.0001f)
                _unitSize = 1f;

            return true;
        }

        // ★ THE DEFAULT RING IS DRAWN, NOT BORROWED. No stock sprite is a
        // clean thin circle - the one this used to borrow was the toroid's
        // star-burst, which read as a stray weapon icon behind the ship
        // (R19 tests 247/250). One 256px outline, built once and shared by
        // every ring: Texture2D.SetPixels32 takes an ARRAY, so this avoids
        // the ReadOnlySpan overloads this target framework cannot bind.
        private const int DrawnSize = 256;
        private const int DefaultThicknessPx = 5;

        // One texture per line thickness, shared by every ring that asks for it.
        private static readonly System.Collections.Generic.Dictionary<int, Sprite>
            _drawn = new System.Collections.Generic.Dictionary<int, Sprite>();

        public static Sprite DrawnRing()
        {
            return DrawnRing(DefaultThicknessPx);
        }

        public static Sprite DrawnRing(int thicknessPx)
        {
            Sprite cached;

            if (_drawn.TryGetValue(thicknessPx, out cached) && cached != null)
                return cached;

            const int size = DrawnSize;
            const float outer = size * 0.5f - 1f;
            float thickness = thicknessPx;

            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.filterMode = FilterMode.Bilinear;
            tex.wrapMode = TextureWrapMode.Clamp;

            var px = new Color32[size * size];
            float c = (size - 1) * 0.5f;

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float d = Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c));

                    // One pixel of soft edge each side, so the line does
                    // not shimmer as it scales.
                    float a = Mathf.Clamp01(1f - Mathf.Abs(d - (outer - thickness * 0.5f))
                                                 + thickness * 0.5f - 0.5f);

                    px[y * size + x] = new Color32(255, 255, 255, (byte)(a * 255f));
                }
            }

            tex.SetPixels32(px);
            tex.Apply(false, true);

            // PPU = size, so the sprite is exactly 1 unit across and Drive's
            // scale is simply the diameter.
            Sprite made = Sprite.Create(
                tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), size);
            made.name = "ForgeBloomRing (drawn " + thicknessPx + "px)";

            _drawn[thicknessPx] = made;

            return made;
        }

        private void LateUpdate()
        {
            if (_sr == null)
                return;

            // A frame with no driver is a frame the gun was put away (the
            // swap path drives false once, then nobody drives at all).
            bool want = _want && _drivenFrame >= Time.frameCount - 1;

            float step = (_fade > 0f) ? Time.deltaTime / _fade : 1f;

            _alpha = Mathf.MoveTowards(_alpha, want ? 1f : 0f, step);

            if (_alpha <= 0f)
            {
                if (_sr.enabled)
                    _sr.enabled = false;

                return;
            }

            _sr.enabled = true;

            Color c = _color;
            c.a *= _alpha * _opacity;
            _sr.color = c;

            // Parented to the unit, so it follows for free - but the ring must
            // not inherit the hull's spin, and a parent's rotation reaches a
            // child even when the child's local rotation is identity.
            _sr.transform.rotation = Quaternion.identity;
        }
    }

    // Driven from Shooter, because that is where all four things the ring needs
    // live at once: the equipped weapon, the owning unit, whether the trigger
    // is held, and a per-frame tick.
    //
    // GADGETS ARE NOT COVERED, and that is stated at build time rather than
    // left to be discovered. A gadget calls weapon.Fire(new FakeBarrel(...))
    // directly and never touches Shooter, so a bloom in a gadget slot works
    // perfectly and simply draws no circle.
    public static class ForgeBloomRingPatch
    {
        [HarmonyPatch(typeof(Shooter), "Update")]
        public class OnUpdate
        {
            static void Postfix(Shooter __instance)
            {
                if (!ForgeBloom.Any)
                    return;

                try
                {
                    Tick(__instance);
                }
                catch
                {
                    // A readout must never be able to stop a gun firing.
                }
            }
        }

        private static void Tick(Shooter s)
        {
            if (s == null)
                return;

            WeaponBase w = s.Weapon;
            Unit unit = s.Unit;

            if (unit == null)
                return;

            ForgeBloom.Config cfg = null;

            bool wants =
                w != null &&
                ForgeBloom.TryGet(w.TemplateData, out cfg) &&
                cfg != null &&
                cfg.ring != ForgeBloom.RingShow.Off;

            var ring = unit.GetComponent<ForgeBloomRing>();

            if (!wants)
            {
                // Put the circle away when the weapon is swapped out. Leaving
                // it up is the same class of bug as a world rule that has no
                // off switch: correct while you hold the gun, wrong the moment
                // you do not.
                //
                // ONLY IF IT IS OURS. A ship's other Shooter reaches this line
                // every frame with a weapon that has no bloom, and without the
                // ownership test it would put away a circle the bloom weapon
                // had just raised.
                if (ring != null && ReferenceEquals(ring.claim, s))
                {
                    ring.Drive(false, 0f, Color.clear, null);
                    ring.claim = null;
                }

                return;
            }

            var ranged = w as IHasRangeProperty;

            float range = (ranged != null) ? ranged.Range : cfg.templateRange;

            if (range <= 0f)
                range = cfg.templateRange;

            bool show;

            switch (cfg.ring)
            {
                case ForgeBloom.RingShow.Always:
                    show = true;
                    break;

                case ForgeBloom.RingShow.OutOfRange:
                    show = CursorBeyond(unit, range);
                    break;

                default:
                    show = s.IsShooting;
                    break;
            }

            if (ring == null)
                ring = unit.gameObject.AddComponent<ForgeBloomRing>();

            // Claim it if it is free or already ours. If the OTHER Shooter got
            // there first - two bloom weapons equipped at once - it keeps it,
            // and we simply do not draw a second circle over the top.
            if (ring.claim == null)
                ring.claim = s;

            if (!ReferenceEquals(ring.claim, s))
                return;

            ring.Drive(show, range, cfg.ringColor, cfg.ringSprite,
                       cfg.ringFade, cfg.ringOpacity, cfg.ringWidth);
        }

        // The aimer is CACHED, because this runs every frame for the whole time
        // a bloom weapon is equipped and ForgeVolley.TryCursor walks the unit's
        // hierarchy - twice, when the first walk misses. That is fine for a
        // per-shot question and wasteful for a per-frame readout. A destroyed
        // Unity object compares equal to null, so the cache re-finds itself.
        private static Unit _aimedUnit;
        private static Aimer _aimer;

        private static bool CursorBeyond(Unit unit, float range)
        {
            if (_aimer == null || !ReferenceEquals(_aimedUnit, unit))
            {
                _aimedUnit = unit;
                _aimer = unit.GetComponentInChildren<Aimer>(true);
            }

            if (_aimer == null)
                return false;

            return Vector2.Distance(
                unit.transform.position, _aimer.TargetPosition) > range;
        }
    }
}
