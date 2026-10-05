using System;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;

namespace WeaponForge
{
    // FRAME SELECTION AND SPEED, CARRIED IN THE SPRITE NAME ITSELF.
    //
    //     "area_zero_particle_tuff"        all nine frames, default speed
    //     "area_zero_particle_tuff:2-6"    frames 2 to 6 only
    //     "area_zero_particle_tuff:8-0"    all nine, REVERSED
    //     "area_zero_particle_tuff:0,3,5"  those three, in that order
    //     "area_zero_particle_tuff:4"      one frozen frame
    //     "area_zero_particle_tuff@24"     all nine at 24 fps
    //     "area_zero_particle_tuff:8-0@18" both
    //
    // WHY IN THE NAME RATHER THAN AS THREE NEW KEYS. Asked for as *"an option to
    // set the frame rate or reverse the frame order or make a .json in the sprite
    // folder that set the exact frames to keep"*. FOURTEEN keys take a sprite
    // name, plus the two icon paths; three keys on each would be forty-odd new
    // keys, most of them duplicated documentation. One suffix works everywhere
    // the moment it is parsed in the two resolvers all of them already go
    // through.
    //
    // WHAT EACH KEY DOES WITH IT depends on whether its consumer draws a frame
    // LIST or a single sprite, and that split is worth knowing before promising
    // anyone a reversed animation:
    //
    //   FULL - reverse, trim and speed all apply, because the consumer takes
    //   `Art.animation.frames`:
    //     projectileSprite, trail.sprite, muzzleSprite, impactSprite,
    //     beam.impactSprite, curveBeam, leech.orbSprite
    //
    //   OPT-IN - the consumer draws ONE renderer, so a frame list only animates
    //   when the name asks for it. See ForgeSpriteLibrary.OptInFrames:
    //     gas.sprite, shield.sprite, swipe.sprite, catalyst.nodeSprite,
    //     charge.meterSprite
    //
    //   FRAME PICKER ONLY - `:4` chooses a frame, `@fps` is meaningless, and
    //   that is an ENGINE limit rather than an omission: a stock beam is one
    //   stretched quad with no flipbook, and an icon is assigned once and never
    //   ticked. ForgeBeam says so and points at `curveBeam`:
    //     beam.fireSprite, beam.warmupSprite, module icon, catalyst indicator
    //
    // IT CANNOT CHANGE WHAT AN EXISTING FILE MEANS. `:` and `@` are not legal in
    // Unity asset names and appear in none of the game's ~450 sprites nor in any
    // of the 156 custom ones currently loaded - names in play use letters,
    // digits, spaces, hyphens and underscores. So a name with no suffix parses to
    // exactly itself.
    //
    // FRAME NUMBERS ARE 0-BASED, and for a stock numbered run that is a real
    // convenience rather than a convention to remember: frame 3 IS
    // `area_zero_particle_tuff_3`. For a custom animation they are the order its
    // sprite-folder JSON lists.
    public class ForgeSpriteSpec
    {
        private static readonly ManualLogSource Log =
            ForgeLog.Source("WeaponForge.Sprites");

        // Frame indices in the order they should play. Null = every frame, in
        // order.
        public int[] order;

        // 0 = not specified; leave whatever the caller already had.
        public float fps;

        // "@life" - let each PARTICLE's own lifetime drive the frames, so the
        // run plays through exactly once however long that particle happens to
        // live. This is the game's own idiom for the tuff bubbles
        // (`ImpactParticle Cell Tuff` ships timeMode Lifetime), and it is the
        // only way to get a puff whose animation matches its life when the
        // lifetime is a RANDOM RANGE - 1 to 3 seconds there, so no single fps
        // can be right for every particle in the burst.
        //
        // It also removes an arithmetic step this mod used to ask for in
        // writing: "a flash lives about 0.4 seconds, a 20-frame animation needs
        // roughly 50 fps to play through once in that time". That is exactly
        // what Lifetime mode computes.
        public bool fitLifetime;

        public bool HasOrder { get { return order != null && order.Length > 0; } }
        public bool HasFps { get { return fps > 0f; } }

        public bool IsPlain
        {
            get { return !HasOrder && !HasFps && !fitLifetime; }
        }

        // Splits "name:2-6@24" into "name" and a spec. Never throws and never
        // returns null: an unreadable suffix warns and is dropped, because the
        // art still being wrong is better than the weapon not building.
        public static ForgeSpriteSpec Split(string raw, out string baseName)
        {
            var spec = new ForgeSpriteSpec();
            baseName = (raw ?? "").Trim();

            if (baseName.Length == 0)
                return spec;

            try
            {
                // @fps comes off the END first, so ":2-6@24" and "@24" both work
                // and a frame list cannot swallow the speed.
                int at = baseName.LastIndexOf('@');

                if (at >= 0)
                {
                    string tail = baseName.Substring(at + 1).Trim();
                    baseName = baseName.Substring(0, at).Trim();

                    float f;
                    string word = tail.ToLowerInvariant();

                    if (word == "life" || word == "lifetime")
                    {
                        spec.fitLifetime = true;
                    }
                    else if (float.TryParse(
                            tail,
                            System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out f) && f > 0f)
                    {
                        spec.fps = f;
                    }
                    else
                    {
                        Log.LogWarning(
                            "sprite name '" + raw + "': '@" + tail + "' is not " +
                            "a frame rate. Write it as a number of frames per " +
                            "second, like \"@24\", or \"@life\" to fit the " +
                            "animation to each particle's own lifetime. " +
                            "Ignored.");
                    }
                }

                int colon = baseName.LastIndexOf(':');

                if (colon >= 0)
                {
                    string tail = baseName.Substring(colon + 1).Trim();
                    baseName = baseName.Substring(0, colon).Trim();

                    spec.order = ParseOrder(tail, raw);
                }
            }
            catch (Exception e)
            {
                Log.LogWarning(
                    "sprite name '" + raw + "' could not be read (" + e.Message +
                    "), so it is being used as-is.");
            }

            return spec;
        }

        // "2-6" | "6-2" | "0,3,5" | "4" | "0-8,7-1"
        private static int[] ParseOrder(string text, string raw)
        {
            if (text.Length == 0)
                return null;

            var made = new List<int>();

            string[] parts = text.Split(',');

            for (int i = 0; i < parts.Length; i++)
            {
                string part = parts[i].Trim();

                if (part.Length == 0)
                    continue;

                int dash = part.IndexOf('-', 1);   // from 1: allow a leading -

                if (dash > 0)
                {
                    int a, b;

                    if (!int.TryParse(part.Substring(0, dash).Trim(), out a) ||
                        !int.TryParse(part.Substring(dash + 1).Trim(), out b))
                    {
                        Bad(part, raw);
                        continue;
                    }

                    // A RANGE RUNS EITHER WAY, and that is the whole reverse
                    // feature: "8-0" is not an error, it is backwards.
                    int step = (b >= a) ? 1 : -1;

                    for (int n = a; ; n += step)
                    {
                        made.Add(n);

                        if (n == b)
                            break;

                        // Guard against a pathological range in a typo.
                        if (made.Count > 512)
                            break;
                    }
                }
                else
                {
                    int one;

                    if (int.TryParse(part, out one))
                        made.Add(one);
                    else
                        Bad(part, raw);
                }
            }

            return (made.Count > 0) ? made.ToArray() : null;
        }

        private static void Bad(string part, string raw)
        {
            Log.LogWarning(
                "sprite name '" + raw + "': '" + part + "' is not a frame " +
                "number or range. Use \"2-6\" for a range, \"6-2\" to reverse " +
                "it, \"0,3,5\" for a list, or \"4\" for one frame. That part " +
                "is ignored.");
        }

        // Reorder / trim a resolved frame list. Returns the SAME array when
        // there is nothing to do, so the common case allocates nothing.
        public Sprite[] Apply(Sprite[] frames, string raw)
        {
            if (!HasOrder || frames == null || frames.Length == 0)
                return frames;

            var made = new List<Sprite>(order.Length);
            bool clamped = false;

            for (int i = 0; i < order.Length; i++)
            {
                int n = order[i];

                if (n < 0 || n >= frames.Length)
                {
                    clamped = true;
                    continue;
                }

                if (frames[n] != null)
                    made.Add(frames[n]);
            }

            if (made.Count == 0)
            {
                // EVERY frame asked for was out of range. Falling through to the
                // full run is the useful failure: the art appears and the log
                // says why it is not what was asked for.
                Log.LogWarning(
                    "sprite name '" + raw + "': none of those frame numbers " +
                    "exist - this animation has " + frames.Length +
                    " frame(s), numbered 0 to " + (frames.Length - 1) +
                    ". Using all of them.");

                return frames;
            }

            if (clamped)
            {
                Log.LogWarning(
                    "sprite name '" + raw + "': some frame numbers are outside " +
                    "this animation, which has " + frames.Length +
                    " frame(s) numbered 0 to " + (frames.Length - 1) +
                    ". The ones that exist are being used.");
            }

            return made.ToArray();
        }

        // For the build log, so a file that asked for something unusual says so.
        public string Describe(int frameCount)
        {
            if (IsPlain)
                return "";

            string s = " (";

            if (HasOrder)
            {
                s += frameCount + " frame(s)";

                if (order.Length >= 2 && order[order.Length - 1] < order[0])
                    s += ", REVERSED";
            }

            if (HasFps)
                s += (HasOrder ? ", " : "") + "at " + fps.ToString("0.##") + " fps";

            if (fitLifetime)
                s += (HasOrder ? ", " : "") + "fitted to each particle's life";

            return s + ")";
        }
    }
}
