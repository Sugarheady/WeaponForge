using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx.Logging;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace WeaponForge
{
    // Loads the player's OWN art from a "sprites" folder next to the DLL
    // and hands it out by name, so a weapon JSON can say
    // "projectileSprite": "myBullet".
    //
    // Custom sprites live in their own namespace: this dictionary is
    // consulted ONLY by the Forge sprite keys, never by the generic
    // by-name asset lookup that finds the game's ~450 atlas sprites. So a
    // custom name can never shadow (or be shadowed by) a stock one.
    //
    // A folder can hold:
    //   * a bare PNG            -> one sprite, named after the file
    //   * a PNG + a .json sheet -> as many sprites as you slice out of it
    public static class ForgeSpriteLibrary
    {
        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("WeaponForge.Sprites");

        // The game's own scale: 20 texture pixels to one world unit.
        public const float DefaultPixelsPerUnit = 20f;

        // How much world space a module icon takes up. Stock icons are
        // HUD_GridTiles_NN: 24x24 px at pixels-per-unit 40, so 0.6 units -
        // NOT the 20 ppu everything else in this game uses. See
        // ResolveIconSprite for why that matters.
        public const float ModuleIconUnits = 0.6f;

        // One frame sequence, ready to hand to a ForgeSpriteAnimation.
        public class SpriteAnim
        {
            public Sprite[] frames;
            public float fps = 12f;
            public ForgeSpriteAnimation.LoopMode loop =
                ForgeSpriteAnimation.LoopMode.Loop;
            public bool randomStart;
        }

        // What a weapon's projectileSprite name resolved to. Static art and
        // an animation share ONE namespace, so a weapon can swap between
        // them without touching anything but the name.
        public class Art
        {
            public Sprite sprite;      // static art, or the animation's frame 0
            public SpriteAnim animation;   // null when it is just a sprite

            // What the NAME asked for, kept so a consumer can tell an
            // animation the author requested from one they merely inherited by
            // naming a sheet. Never null from either resolver. See OptInFrames.
            public ForgeSpriteSpec spec;

            // Resolved from the CUSTOM animation table - the author wrote a
            // sprites JSON that declares this an animation, with its own fps and
            // loop mode, and then named it. That is an explicit request to
            // animate, unlike a stock numbered run where the bare name meaning
            // "the whole run" is a convention this mod invented. See OptInFrames.
            public bool fromCustomAnim;
        }

        // CUSTOM ART FIRST, THEN THE GAME'S OWN - the resolution every sprite
        // key should have had, and seven of them did not.
        //
        // Found 2026-08-31 from his round 11 log, which named it outright:
        //   gas "sprite": 'area_zero_particle_tuff:0-8' is not one of your sprites
        //   impactSprite 'area_zero_particle_tuff' is not one of your sprites
        // Both are real GAME sprites. Only `projectileSprite` and the leech orb
        // ever tried `TryGetStockArt`; `impactSprite`, `muzzleSprite`,
        // `gas.sprite`, `shield.sprite`, `swipe.sprite`, `catalyst.nodeSprite`
        // and `charge.meterSprite` asked ONLY the custom library, so every one
        // of the game's ~450 sprites was unreachable through them.
        //
        // WHY IT SURVIVED: the failure is loud - each site warns and names the
        // string - so it never looked like a silent bug. What made it invisible
        // was the WORDING. "is not one of your sprites" is a true sentence that
        // reads as "you typo'd", when the real meaning was "this key cannot do
        // stock art at all". Two R11 tests were written telling him to put stock
        // art on two of these keys, and the docs claimed the frame suffix worked
        // on all fourteen - true of the parsing, false of the resolution.
        //
        // LESSON, and it generalises past sprites: a warning that names the
        // input but not the CAPABILITY sends the reader to check their spelling
        // instead of the feature. When a lookup can only search half its
        // namespace, the message has to say which half.
        public static bool TryGetAnyArt(string name, out Art art)
        {
            return TryGetArt(name, out art) || TryGetStockArt(name, out art);
        }

        // The tail of a "could not find that sprite" warning.
        //
        // Deliberately NOT the whole list. It used to be `string.Join(", ",
        // Names)` at every site, which with his 156 sprites made single log
        // lines about 2,000 characters long - two of them dominated the round 11
        // log and buried the useful ones. A count plus a handful is enough to
        // tell "the folder loaded" from "the folder is empty", which is the only
        // question the list was ever answering.
        public static string NotFoundHint()
        {
            if (Count == 0)
            {
                return " There is no art in the sprites folder yet, and it is " +
                       "not one of the game's own sprite names either.";
            }

            var some = new List<string>();

            foreach (string n in Names)
            {
                some.Add(n);

                if (some.Count == 6)
                    break;
            }

            return " Checked your " + Count + " sprite(s) (" +
                   string.Join(", ", some.ToArray()) +
                   (Count > some.Count ? ", ..." : "") +
                   ") and the game's own art. Custom names are whatever your " +
                   "sprites JSON calls them; game names look like " +
                   "\"part_cyrcle_12\" or \"area_zero_particle_tuff\".";
        }

        // WHAT SPEED A PARTICLE SYSTEM SHOULD ANIMATE AT - one owner for the
        // rule, four callers (impactSprite, muzzleSprite, trail.sprite and the
        // beam's impactSprite).
        //
        // A NULL RETURN MEANS "TIE THE FRAMES TO EACH PARTICLE'S OWN LIFETIME",
        // which `ApplyParticleSprite` turns into `timeMode = Lifetime`. That is
        // the game's own idiom for these effects and the only correct answer
        // when a system's lifetime is a random RANGE: `ImpactParticle Cell Tuff`
        // lives 1-3 seconds, so no single fps pops every bubble in the burst
        // over its own life.
        //
        // THE `fallback` PARAMETER EXISTS BECAUSE THE FOUR CALLERS DISAGREE, and
        // that disagreement predates this method rather than being introduced by
        // it. The trail has always passed its `@`/block fps straight through, so
        // a trail with neither set already animates by lifetime. Impact and
        // muzzle have always passed `art.animation.fps`, which for a STOCK run
        // is a fabricated 12 - so they could never reach Lifetime at all, which
        // is exactly what made the tuff bubbles finish popping in 9/12 = 0.75s
        // and then sit as a 4px dot for the rest of a 1-3 second life.
        // Unifying the defaults would change how weapons already written look,
        // so the fix is the explicit `@life` spelling and each caller keeps
        // stating its own default out loud.
        public static float? ParticleFps(
            Art art,
            float? blockKey,
            float? fallback,
            string what,
            string fileName)
        {
            bool wantsLife =
                art != null && art.spec != null && art.spec.fitLifetime;

            float? nameFps =
                (art != null && art.spec != null && art.spec.HasFps)
                    ? art.spec.fps
                    : (float?)null;

            // The block's own key is the more specific statement and wins - but
            // a name that asked for a speed and did not get it must not be
            // ignored in silence.
            if (blockKey.HasValue)
            {
                if (wantsLife)
                {
                    Log.LogInfo(
                        fileName + ": " + what + " asks for \"@life\" and the " +
                        "block's own fps key says " +
                        blockKey.Value.ToString("0.##") + ". The key wins - " +
                        "drop whichever one you did not mean.");
                }
                else if (nameFps.HasValue &&
                         Mathf.Abs(nameFps.Value - blockKey.Value) > 0.001f)
                {
                    Log.LogInfo(
                        fileName + ": " + what + " NAME asks for " +
                        nameFps.Value.ToString("0.##") + " fps and the block's " +
                        "own fps key says " + blockKey.Value.ToString("0.##") +
                        ". The key wins - drop whichever one you did not mean.");
                }

                return blockKey;
            }

            if (wantsLife)
                return null;

            if (nameFps.HasValue)
                return nameFps;

            return fallback;
        }

        // OPT-IN ANIMATION, for the sprite keys whose consumer draws exactly
        // ONE sprite: the gas puff, the shield arc, the swipe blade, the
        // catalyst node and the charge meter.
        //
        // WHY IT IS OPT-IN AND NOT SIMPLY ON. Those five resolve art through
        // `TryGetArt` like everything else, so a multi-frame sheet has ALWAYS
        // arrived with its frames attached - they just took `.sprite` and threw
        // the rest away. Animating them unconditionally would therefore change
        // weapons that are already written: a file saying
        // `"sprite": "area_zero_particle_tuff"` in its gas block renders one
        // still frame today and would silently start moving on the next launch,
        // with no edit to the file. So the NAME has to ask - `:0-8` or `@12` -
        // and a bare name keeps exactly the look it has now.
        //
        // Returns null when there is nothing to animate, which is the common
        // case; the caller then does what it always did.
        public static Sprite[] OptInFrames(Art art, out float fps)
        {
            fps = 12f;

            if (art == null || art.spec == null)
                return null;

            // NARROWED 2026-08-31, from round 11 test 48: *"looks like its stuck
            // on the first animation where the swipe is small."* His swipe named
            // `wf_swipeAnim` - 4 frames at 24 fps, declared in his own sprites
            // JSON - and got one frozen frame.
            //
            // The original rule ("a bare name never animates") was too broad. It
            // was written to protect files already on disk, and for a STOCK run
            // that protection is real: `area_zero_particle_tuff` is just a
            // sprite prefix, and "the bare name means the whole run" is a
            // convention this mod invented, so a file naming it could genuinely
            // have meant one frame.
            //
            // A CUSTOM animation is not ambiguous in that way. To have a name at
            // all somebody wrote a JSON entry declaring it an animation, gave it
            // an fps and a loop mode, and then typed that name rather than the
            // sheet's. There is no reading of that which wants a still frame -
            // and the library elsewhere already treats the two names as
            // different intents, warning "if you wanted the shot to animate, use
            // '<sheet>Anim' instead". So this honours it.
            //
            // The cost is honest and small: a weapon already naming a custom
            // animation on one of these five keys starts animating. That is a
            // FIX for those files, not a regression - nobody named `...Anim` and
            // wanted frame 0.
            if (art.spec.IsPlain && !art.fromCustomAnim)
                return null;

            bool haveRun =
                art.animation != null &&
                art.animation.frames != null &&
                art.animation.frames.Length > 1;

            if (!haveRun)
            {
                // Asking for a SPEED and getting a still is the one shape here
                // worth a word: `:4` is a deliberate single frame, but `@24` on
                // something with no run behind it is a file that expected
                // movement and will not get any.
                if (art.spec.HasFps || art.spec.fitLifetime)
                {
                    Log.LogWarning(
                        "a frame rate was asked for with '@" +
                        (art.spec.fitLifetime
                            ? "life"
                            : art.spec.fps.ToString("0.##")) +
                        "', but this name resolves to a single sprite, so " +
                        "there is nothing to animate. For stock art the run " +
                        "is the BARE name (\"area_zero_particle_tuff\", not " +
                        "\"area_zero_particle_tuff_0\"); for your own art it " +
                        "is the name its sprites JSON gives the animation.");
                }

                return null;
            }

            // `@life` has no meaning on a SpriteRenderer - there is no particle
            // whose lifetime could drive it. Say so rather than defaulting
            // quietly: the author asked for a timing they are not getting.
            if (art.spec.fitLifetime)
            {
                Log.LogWarning(
                    "'@life' fits an animation to a PARTICLE's lifetime, and " +
                    "this art is drawn by a sprite renderer instead. It works " +
                    "on impactSprite, muzzleSprite, the trail and the beam's " +
                    "impactSprite. Using " +
                    ((art.animation.fps > 0f) ? art.animation.fps : 12f)
                        .ToString("0.##") + " fps here - set a speed with " +
                    "\"@12\" if that is not what you want.");
            }

            fps = (art.animation.fps > 0f) ? art.animation.fps : 12f;

            return art.animation.frames;
        }

        private static readonly Dictionary<string, Sprite> _sprites =
            new Dictionary<string, Sprite>(StringComparer.OrdinalIgnoreCase);

        private static readonly Dictionary<string, SpriteAnim> _anims =
            new Dictionary<string, SpriteAnim>(StringComparer.OrdinalIgnoreCase);

        // Sheets are cached so several manifests can slice one PNG without
        // uploading the texture more than once.
        private static readonly Dictionary<string, Texture2D> _sheets =
            new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);

        // Icon-scaled rebuilds, cached so ten modules naming one sprite do
        // not create ten Sprite objects off the same rect.
        private static readonly Dictionary<string, Sprite> _icons =
            new Dictionary<string, Sprite>(StringComparer.OrdinalIgnoreCase);

        private static bool _loaded;

        // ------------------------------------------------------------------
        // STOCK ART, by the name the game gives it
        // ------------------------------------------------------------------
        //
        // Asked for as *"theres little bubbles that show up when you destroy
        // white bubble blocks... Would it be possible to turn those bubbles
        // into a projectile?"* - and the answer was no, for a silly reason:
        // `projectileSprite` read THIS dictionary and stopped. The mod already
        // resolves stock assets by name everywhere else (module icons do it),
        // so the game's ~450 sprites were one lookup away the whole time.
        //
        // The bubbles specifically are `part_cyrcle_15` (the White pickup) and
        // `part_cyrcle_12` - the orb that Fuel, Caps and Purple all share and
        // tint three different ways, which is why the same art shows up in
        // several colours around the level.
        //
        // ORDER MATTERS AND CUSTOM WINS. A file that already says
        // "projectileSprite": "myBullet" must keep meaning the PNG in the
        // sprites folder even if the game happens to ship a sprite of that
        // name - so this is only ever consulted after the custom lookup misses.
        // That keeps the original guarantee in this file's header intact: a
        // custom name can never be shadowed by a stock one.
        //
        // DELEGATED to `JsonFieldMapper.FindAsset` rather than given its own
        // scan and cache. `ForgeTrail.ResolveFrames` already resolves stock
        // sprites through exactly that call - its comment even claimed it was
        // doing "the same order and same namespace rules as projectileSprite",
        // which was describing behaviour projectileSprite did not have. One
        // owner per mechanic: a second cache here could only ever disagree with
        // the first. It runs at weapon-build time, not per frame.
        public static bool TryGetStockArt(string name, out Art art)
        {
            art = null;

            if (string.IsNullOrEmpty(name))
                return false;

            try
            {
                string raw = name.Trim();
                string key;
                ForgeSpriteSpec spec = ForgeSpriteSpec.Split(raw, out key);

                // A NUMBERED SEQUENCE, when the name asks for one. See
                // TryGetStockFrames for why this can never change what an
                // existing file means.
                Sprite[] frames;

                if (TryGetStockFrames(key, out frames) && frames.Length > 1)
                {
                    frames = spec.Apply(frames, raw);

                    art = new Art
                    {
                        sprite = frames[0],
                        animation = (frames.Length > 1)
                            ? new SpriteAnim
                            {
                                frames = frames,
                                fps = spec.HasFps ? spec.fps : 12f
                            }
                            : null,
                        spec = spec
                    };

                    return true;
                }

                var s = JsonFieldMapper.FindAsset(
                    typeof(Sprite), key) as Sprite;

                if (s == null)
                    return false;

                art = new Art { sprite = s, spec = spec };
                return true;
            }
            catch (Exception e)
            {
                Log.LogError("Looking for a stock sprite failed: " + e);
                return false;
            }
        }

        // ------------------------------------------------------------------
        // STOCK ANIMATIONS - a numbered run of sprite assets as one flipbook
        // ------------------------------------------------------------------
        //
        // Asked for as *"ideally i would love the bubble effect from
        // area_zero_particle_tuff_0 to be the projectile and trailing sprite
        // (at least the animation on the trailing sprite)"*. The gap: stock art
        // resolved as exactly ONE sprite everywhere - `TryGetStockArt` returned
        // a single Art and `ForgeTrail.ResolveFrames` literally returned
        // `new[] { stock }` - while CUSTOM art could be a multi-frame flipbook.
        // So a stock animation was unreachable, and the game ships a lot of them
        // (the tuff bubbles are nine frames, `_0` to `_8`).
        //
        // IT CANNOT CHANGE WHAT AN EXISTING FILE MEANS, and that constraint is
        // what decides the naming rule. A name ending in `_<digits>` keeps
        // meaning EXACTLY that one frame - otherwise `"part_cyrcle_12"`, which
        // real files already use, would suddenly animate through
        // `part_cyrcle_0..16` and every one of those weapons would change look
        // on upgrade. So a sequence has to be asked for by a name that resolves
        // to nothing today:
        //
        //     "area_zero_particle_tuff_0"    ONE frozen frame (unchanged)
        //     "area_zero_particle_tuff"      all nine frames, animated
        //     "area_zero_particle_tuffAnim"  the same, spelled the way a
        //                                    CUSTOM animation is spelled
        //
        // ONE SCAN, NOT N PROBES. `JsonFieldMapper.FindAsset` runs a full
        // `Resources.FindObjectsOfTypeAll` per call, so probing `_0`, `_1`, `_2`
        // one at a time would be one whole-project scan per frame. This is a
        // different question anyway - "gather everything with this prefix"
        // rather than "find this exact name" - so it does the prefix pass
        // itself. That is not a second cache for the first question; the exact
        // -name path above still delegates, as its own comment requires.
        private static readonly Dictionary<string, Sprite[]> _stockRuns =
            new Dictionary<string, Sprite[]>(StringComparer.OrdinalIgnoreCase);

        public static bool TryGetStockFrames(string name, out Sprite[] frames)
        {
            frames = null;

            if (string.IsNullOrEmpty(name))
                return false;

            // Stripped here too, defensively: this is public and a caller that
            // has not been through Split would otherwise probe for a run called
            // "tuff:2-6" and find nothing.
            string key;
            ForgeSpriteSpec.Split(name, out key);

            // Ends in _<digits>? That is a request for one specific frame, and
            // it must stay one specific frame.
            if (EndsInFrameNumber(key))
                return false;

            if (key.EndsWith("Anim", StringComparison.OrdinalIgnoreCase) &&
                key.Length > 4)
            {
                key = key.Substring(0, key.Length - 4);
            }

            if (_stockRuns.TryGetValue(key, out frames))
                return frames != null && frames.Length > 0;

            try
            {
                frames = GatherRun(key);
            }
            catch (Exception e)
            {
                Log.LogError("Looking for a stock animation failed: " + e);
                frames = null;
            }

            _stockRuns[key] = frames;
            return frames != null && frames.Length > 0;
        }

        private static bool EndsInFrameNumber(string name)
        {
            int i = name.LastIndexOf('_');

            if (i < 0 || i == name.Length - 1)
                return false;

            for (int c = i + 1; c < name.Length; c++)
            {
                if (!char.IsDigit(name[c]))
                    return false;
            }

            return true;
        }

        // Every `<baseName>_<n>` in one pass, ordered by n. Gaps end the run
        // rather than being skipped over: a run that jumps 0,1,2,7 is far more
        // likely to be two different effects sharing a prefix than one
        // nine-frame animation with a hole in it.
        private static Sprite[] GatherRun(string baseName)
        {
            var byIndex = new Dictionary<int, Sprite>();
            string prefix = baseName + "_";

            UnityEngine.Object[] all =
                Resources.FindObjectsOfTypeAll(typeof(Sprite));

            for (int i = 0; i < all.Length; i++)
            {
                var s = all[i] as Sprite;

                if (s == null || s.name == null ||
                    !s.name.StartsWith(
                        prefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string tail = s.name.Substring(prefix.Length);
                int n;

                if (!int.TryParse(tail, out n) || n < 0)
                    continue;

                // First one wins a duplicate index - two Sprite objects can
                // share a name across atlases and picking either is arbitrary,
                // but picking a DIFFERENT one on a later run would make the
                // animation change between sessions.
                if (!byIndex.ContainsKey(n))
                    byIndex[n] = s;
            }

            if (byIndex.Count == 0)
                return null;

            var run = new List<Sprite>();

            for (int n = 0; byIndex.ContainsKey(n); n++)
                run.Add(byIndex[n]);

            // No frame 0 means there is no run to speak of, whatever else
            // matched - the numbering the game uses always starts at 0.
            if (run.Count == 0)
                return null;

            return run.ToArray();
        }

        public static string SpritesFolder()
        {
            return Path.Combine(
                Path.GetDirectoryName(
                    Assembly.GetExecutingAssembly().Location),
                "sprites");
        }

        public static int Count
        {
            get { return _sprites.Count + _anims.Count; }
        }

        // Animations first: they are what a weapon most likely means, and a
        // duplicate name is refused at load time anyway.
        public static IEnumerable<string> Names
        {
            get { return _anims.Keys.Concat(_sprites.Keys); }
        }

        public static bool TryGet(string name, out Sprite sprite)
        {
            sprite = null;

            if (string.IsNullOrEmpty(name))
                return false;

            return _sprites.TryGetValue(name.Trim(), out sprite);
        }

        // Resolve a name to either an animation or a static sprite.
        //
        // THE FRAME SUFFIX IS HANDLED HERE, and that placement is the whole
        // design: all fourteen sprite keys come through this method or
        // TryGetStockArt below, so `:2-6`, `:8-0` and `@24` are parsed once
        // rather than at fourteen call sites.
        //
        // PARSING IT EVERYWHERE IS NOT THE SAME AS HONOURING IT EVERYWHERE.
        // Seven keys hand the frame list to something that flips through it;
        // five draw a single renderer and animate only on request
        // (`OptInFrames`); two beam slots and the icons can only ever show one
        // frame, by engine limit. `ForgeSpriteSpec`'s header has the full split
        // - check it before telling anyone a key will animate.
        public static bool TryGetArt(string name, out Art art)
        {
            art = null;

            if (string.IsNullOrEmpty(name))
                return false;

            string raw = name.Trim();
            string key;
            ForgeSpriteSpec spec = ForgeSpriteSpec.Split(raw, out key);

            SpriteAnim anim;
            if (_anims.TryGetValue(key, out anim))
            {
                Sprite[] frames = spec.Apply(anim.frames, raw);

                art = new Art
                {
                    animation = new SpriteAnim
                    {
                        frames = frames,
                        fps = spec.HasFps ? spec.fps : anim.fps,
                        loop = anim.loop,
                        randomStart = anim.randomStart
                    },
                    sprite = (frames != null && frames.Length > 0)
                        ? frames[0] : null,
                    spec = spec,
                    fromCustomAnim = true
                };

                // A single frame is a still, not a one-frame animation - and the
                // animator would otherwise sit on the shot doing nothing.
                if (frames != null && frames.Length == 1)
                    art.animation = null;

                return art.sprite != null;
            }

            Sprite sprite;
            if (_sprites.TryGetValue(key, out sprite))
            {
                art = new Art { sprite = sprite, spec = spec };

                // Naming a single frame when you meant the flipbook is an easy
                // slip - the tool auto-names an animation "<sheet>Anim" while
                // frame 1 keeps the bare sheet name. Point it out; a static
                // frame is a legitimate choice, so this is only a hint.
                string owner = AnimationUsing(key);
                if (owner != null)
                {
                    Log.LogWarning(
                        "'" + key + "' is a single still frame, and it is also " +
                        "frame 1 of the animation '" + owner + "'. If you " +
                        "wanted the shot to animate, use \"projectileSprite\": \"" +
                        owner + "\" instead.");
                }

                return true;
            }

            return false;
        }

        // The first animation whose frame list starts with this sprite.
        private static string AnimationUsing(string spriteName)
        {
            Sprite target;
            if (!_sprites.TryGetValue(spriteName, out target))
                return null;

            foreach (var pair in _anims)
            {
                SpriteAnim a = pair.Value;
                if (a.frames != null && a.frames.Length > 1 &&
                    a.frames[0] == target)
                {
                    return pair.Key;
                }
            }

            return null;
        }

        // Resolve a custom sprite for use as a MODULE ICON, rebuilt at the
        // scale the game's own icons use. Returns null when the name is not
        // one of ours - the caller should then treat it as a stock sprite
        // name. Module Forge reaches this by reflection (ForgeInterop), so
        // there is one sprites folder across both mods; keep the signature
        // stable.
        //
        // Why rebuild rather than hand back the sprite as loaded: an icon is
        // drawn TWICE, by two renderers that disagree about size.
        //   * the card on the grid/shop is a UI Image, 72x72, Simple, with
        //     preserveAspect OFF - it stretches art to fill and does not
        //     care about pixels-per-unit at all;
        //   * the pickup lying on the ground is a world SpriteRenderer,
        //     where pixels-per-unit is the ONLY thing deciding how big it
        //     draws.
        // Stock icons are 0.6 units. Art imported at this library's default
        // 20 ppu would be double that on the ground while looking perfect on
        // the card - which reads as a mod bug and is miserable to diagnose.
        // So the longest side is fitted to the stock footprint, whatever
        // size it was drawn at, and the pivot is centred to match stock (a
        // sprite authored for a beam has a left-edge pivot, and would
        // otherwise hang off to one side).
        public static Sprite ResolveIconSprite(string name)
        {
            if (string.IsNullOrEmpty(name))
                return null;

            // Load order between two BepInEx plugins is not guaranteed and
            // Module Forge may ask before anything here has run. Idempotent.
            LoadAll();

            string raw = name.Trim();

            Sprite cached;
            if (_icons.TryGetValue(raw, out cached))
                return cached;

            // The frame suffix works here too, and it is the ONLY way to
            // choose which frame of a run an icon shows. An icon still cannot
            // animate - the widget assigns a sprite and nothing ever ticks it -
            // so `:frames` is a picker rather than a flipbook and `@fps` is
            // meaningless. Cached under the RAW name so `sheet:2` and `sheet:5`
            // are two icons rather than one.
            string key;
            ForgeSpriteSpec spec = ForgeSpriteSpec.Split(raw, out key);

            // Deliberately NOT TryGetArt: it emits a "did you mean the
            // animation?" hint that only makes sense for a projectile.
            Sprite source;

            if (!_sprites.TryGetValue(key, out source))
            {
                SpriteAnim anim;

                if (!_anims.TryGetValue(key, out anim) ||
                    anim.frames == null || anim.frames.Length == 0)
                {
                    return null;
                }

                Sprite[] frames = spec.Apply(anim.frames, raw);

                if (frames == null || frames.Length == 0)
                    return null;

                source = frames[0];

                if (!spec.HasOrder)
                {
                    Log.LogInfo(
                        "Icon '" + key + "' is an animation; module icons " +
                        "cannot animate, so its first frame is used. Add " +
                        "\":3\" to the name to pick a different one.");
                }
            }

            if (source == null || source.texture == null)
                return null;

            Rect rect = source.rect;
            float longest = Mathf.Max(rect.width, rect.height);

            if (longest <= 0f)
                return null;

            Sprite icon = Sprite.Create(
                source.texture,
                rect,
                new Vector2(0.5f, 0.5f),
                longest / ModuleIconUnits,
                0,
                SpriteMeshType.FullRect,
                Vector4.zero);

            if (icon == null)
                return null;

            icon.name = "Forge Icon " + raw;
            icon.hideFlags = HideFlags.HideAndDontSave;

            // Keyed by the RAW name, matching the lookup above: two frames of
            // one sheet are two different icons.
            _icons[raw] = icon;

            if (Mathf.Abs(rect.width - rect.height) > 0.5f)
            {
                Log.LogWarning(
                    "Icon '" + key + "' is " + (int)rect.width + "x" +
                    (int)rect.height + ", not square. The module CARD " +
                    "stretches art to a square, so it will look squashed " +
                    "there, while the pickup on the ground keeps its shape. " +
                    "Square art (24x24 is the stock size) avoids that.");
            }

            return icon;
        }

        // A GAME sprite for use as an icon, frame-suffix aware.
        //
        // The two icon call sites used to reach `JsonFieldMapper.FindAsset`
        // with the whole string, so `"HUD_GridTiles_04:2"` matched no asset and
        // the icon fell through to "no sprite named ..." - loudly, but with the
        // suffix as the reason and no way to guess that. Going through
        // TryGetStockArt gives icons the same syntax as everything else: it
        // splits the name, resolves a numbered run, applies `:frames` and hands
        // back frame 0 of whatever was chosen.
        //
        // NOT rebuilt to the icon footprint, deliberately - unlike custom art,
        // a stock sprite is already authored at the size the game draws icons
        // at, which is why the old call site used the raw asset too.
        public static Sprite ResolveStockIcon(string name)
        {
            Art art;

            return TryGetStockArt(name, out art) ? art.sprite : null;
        }

        // Scan the folder once. Safe to call repeatedly.
        public static void LoadAll()
        {
            if (_loaded)
                return;

            _loaded = true;

            string folder = SpritesFolder();

            if (!Directory.Exists(folder))
            {
                Directory.CreateDirectory(folder);
                WriteReadme(folder);
                return;
            }

            // Refresh the readme when the mod's has moved on. Writing it
            // only on first run means anyone who already had this folder
            // never learns about anything added since - which is exactly
            // what happened with module icons.
            WriteReadme(folder);

            // Three passes, because an animation names sprites and those
            // sprites may live in a different sheet (or be a bare PNG):
            //   1. every manifest's sprites
            //   2. bare PNGs that no manifest claimed
            //   3. every manifest's animations, now that all frames exist
            var claimedSheets =
                new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var manifests = new List<KeyValuePair<string, JObject>>();

            foreach (string file in
                Directory.GetFiles(folder, "*.json").OrderBy(x => x))
            {
                try
                {
                    JObject root = JObject.Parse(File.ReadAllText(file));
                    manifests.Add(
                        new KeyValuePair<string, JObject>(file, root));
                    LoadManifestSprites(file, root, claimedSheets);
                }
                catch (Exception e)
                {
                    Log.LogError(
                        "Failed to read sheet " +
                        Path.GetFileName(file) + ": " + e.Message);
                }
            }

            foreach (string file in
                Directory.GetFiles(folder, "*.png").OrderBy(x => x))
            {
                if (claimedSheets.Contains(Path.GetFileName(file)))
                    continue;

                try
                {
                    LoadWholeImage(file);
                }
                catch (Exception e)
                {
                    Log.LogError(
                        "Failed to read image " +
                        Path.GetFileName(file) + ": " + e.Message);
                }
            }

            foreach (var m in manifests)
            {
                try
                {
                    LoadAnimations(Path.GetFileName(m.Key), m.Value);
                }
                catch (Exception e)
                {
                    Log.LogError(
                        "Failed to read animations in " +
                        Path.GetFileName(m.Key) + ": " + e.Message);
                }
            }

            if (Count > 0)
            {
                Log.LogInfo(
                    "Loaded " + _sprites.Count + " custom sprite(s) and " +
                    _anims.Count + " animation(s): " +
                    string.Join(", ", Names.ToArray()));
            }
        }

        private static void LoadAnimations(string fileName, JObject root)
        {
            var list = root["animations"] as JArray;
            if (list == null || list.Count == 0)
                return;

            foreach (JToken token in list)
            {
                var entry = token as JObject;
                if (entry == null)
                    continue;

                string name = (string)entry["name"];
                if (string.IsNullOrEmpty(name))
                {
                    Log.LogWarning(
                        fileName + ": an animation has no \"name\" - skipped.");
                    continue;
                }

                name = name.Trim();

                var frameNames = entry["frames"] as JArray;
                if (frameNames == null || frameNames.Count == 0)
                {
                    Log.LogWarning(
                        fileName + ": animation '" + name +
                        "' has no \"frames\" list - skipped.");
                    continue;
                }

                // Frame ORDER is the array's order, deliberately. Inferring it
                // from name suffixes would break at ten frames ("_10" sorts
                // before "_2") and would silently reorder on a rename.
                var frames = new List<Sprite>();
                foreach (JToken f in frameNames)
                {
                    string frameName = ((string)f ?? "").Trim();

                    Sprite sprite;
                    if (string.IsNullOrEmpty(frameName) ||
                        !_sprites.TryGetValue(frameName, out sprite))
                    {
                        Log.LogWarning(
                            fileName + ": animation '" + name +
                            "' wants frame '" + frameName +
                            "' but no sprite of that name was loaded - " +
                            "frame dropped.");
                        continue;
                    }

                    frames.Add(sprite);
                }

                if (frames.Count == 0)
                {
                    Log.LogWarning(
                        fileName + ": animation '" + name +
                        "' had no usable frames - skipped.");
                    continue;
                }

                if (_anims.ContainsKey(name) || _sprites.ContainsKey(name))
                {
                    Log.LogWarning(
                        fileName + ": the name '" + name +
                        "' is already used by another sprite or animation - " +
                        "this animation is skipped. Rename one of them.");
                    continue;
                }

                var anim = new SpriteAnim();
                anim.frames = frames.ToArray();
                anim.fps = (float?)entry["fps"] ?? 12f;
                anim.randomStart = (bool?)entry["randomStart"] ?? false;

                string mode =
                    ((string)entry["loop"] ?? "loop").Trim().ToLowerInvariant();
                if (mode == "once")
                    anim.loop = ForgeSpriteAnimation.LoopMode.Once;
                else if (mode == "pingpong" || mode == "ping-pong")
                    anim.loop = ForgeSpriteAnimation.LoopMode.PingPong;
                else
                    anim.loop = ForgeSpriteAnimation.LoopMode.Loop;

                if (anim.fps <= 0f)
                {
                    Log.LogWarning(
                        fileName + ": animation '" + name + "' has fps " +
                        anim.fps + " - it will just show its first frame.");
                }

                _anims[name] = anim;

                Log.LogInfo(
                    fileName + ": animation '" + name + "' - " +
                    anim.frames.Length + " frames at " + anim.fps + " fps (" +
                    anim.loop + (anim.randomStart ? ", random start" : "") +
                    ").");
            }
        }

        private static void LoadManifestSprites(
            string path, JObject root, HashSet<string> claimedSheets)
        {
            string fileName = Path.GetFileName(path);

            string sheetName = (string)root["sheet"];
            if (string.IsNullOrEmpty(sheetName))
            {
                Log.LogWarning(
                    fileName + ": no \"sheet\" given (the PNG this slices) " +
                    "- skipped.");
                return;
            }

            claimedSheets.Add(sheetName);

            Texture2D sheet =
                LoadSheet(Path.Combine(Path.GetDirectoryName(path), sheetName),
                          ((string)root["filter"] ?? "point"));

            if (sheet == null)
            {
                Log.LogWarning(
                    fileName + ": could not load sheet '" + sheetName +
                    "' - is the PNG next to this file?");
                return;
            }

            float sheetPpu =
                (float?)root["pixelsPerUnit"] ?? DefaultPixelsPerUnit;

            // The slicer records the size it cut against. If the PNG on disk
            // is a different size, every rect points somewhere else - and the
            // rects usually still fit, so nothing else would complain and you
            // would just get sprites full of background. By far the most
            // common cause is cleaning up or shrinking the sheet in the tool
            // and then shipping the untouched original.
            int authoredW = (int?)root["sheetWidth"] ?? 0;
            int authoredH = (int?)root["sheetHeight"] ?? 0;

            if (authoredW > 0 && authoredH > 0 &&
                (authoredW != sheet.width || authoredH != sheet.height))
            {
                Log.LogError(
                    fileName + ": SHEET SIZE MISMATCH. These sprites were cut " +
                    "against a " + authoredW + "x" + authoredH + " image, but '" +
                    sheetName + "' on disk is " + sheet.width + "x" +
                    sheet.height + ". Every sprite will be sliced from the " +
                    "wrong place. If you removed a background or shrank the " +
                    "image in Sprite Sheet Builder, you need to click " +
                    "\"Download PNG\" there and replace '" + sheetName +
                    "' with that file - the original is untouched. " +
                    "(Loading anyway in case the mismatch is deliberate.)");
            }

            var list = root["sprites"] as JArray;
            if (list == null || list.Count == 0)
            {
                Log.LogWarning(
                    fileName + ": no \"sprites\" array - nothing to slice.");
                return;
            }

            int made = 0;
            foreach (JToken token in list)
            {
                var entry = token as JObject;
                if (entry == null)
                    continue;

                if (AddSprite(entry, sheet, sheetPpu, fileName))
                    made++;
            }

            Log.LogInfo(
                fileName + ": sliced " + made + " sprite(s) from " +
                sheetName + " (" + sheet.width + "x" + sheet.height + ").");
        }

        private static bool AddSprite(
            JObject entry, Texture2D sheet, float sheetPpu, string fileName)
        {
            string name = (string)entry["name"];
            if (string.IsNullOrEmpty(name))
            {
                Log.LogWarning(
                    fileName + ": a sprite entry has no \"name\" - skipped.");
                return false;
            }

            name = name.Trim();

            // Rect defaults to the whole sheet, so a one-sprite sheet needs
            // only a name.
            int x = (int?)entry["x"] ?? 0;
            int yTop = (int?)entry["y"] ?? 0;
            int w = (int?)entry["w"] ?? sheet.width;
            int h = (int?)entry["h"] ?? sheet.height;

            if (w <= 0 || h <= 0)
            {
                Log.LogWarning(
                    fileName + ": sprite '" + name + "' has a zero/negative " +
                    "size (" + w + "x" + h + ") - skipped.");
                return false;
            }

            if (x < 0 || yTop < 0 ||
                x + w > sheet.width || yTop + h > sheet.height)
            {
                Log.LogWarning(
                    fileName + ": sprite '" + name + "' rect " +
                    x + "," + yTop + " " + w + "x" + h +
                    " falls outside the " + sheet.width + "x" +
                    sheet.height + " sheet - skipped.");
                return false;
            }

            // Manifests use IMAGE coordinates: origin top-left, y counting
            // down, matching every image editor and the html slicer. Unity
            // wants bottom-left, y up.
            int yUnity = sheet.height - (yTop + h);

            float ppu = (float?)entry["pixelsPerUnit"] ?? sheetPpu;
            if (ppu <= 0f)
                ppu = DefaultPixelsPerUnit;

            var pivot = new Vector2(
                (float?)entry["pivotX"] ?? 0.5f,
                (float?)entry["pivotY"] ?? 0.5f);

            // border = [left, bottom, right, top], Unity's own order. Only
            // meaningful for a sliced/tiled renderer (beams), harmless on a
            // plain projectile.
            Vector4 border = Vector4.zero;
            var borderArray = entry["border"] as JArray;
            if (borderArray != null && borderArray.Count == 4)
            {
                border = new Vector4(
                    (float)borderArray[0], (float)borderArray[1],
                    (float)borderArray[2], (float)borderArray[3]);
            }

            Sprite sprite = Sprite.Create(
                sheet,
                new Rect(x, yUnity, w, h),
                pivot,
                ppu,
                0,
                // FullRect, not Tight: sliced/tiled renderers REQUIRE
                // full-rect geometry, and a projectile does not care - so one
                // mesh type covers both and beams work later for free.
                SpriteMeshType.FullRect,
                border);

            if (sprite == null)
            {
                Log.LogWarning(
                    fileName + ": Unity refused to create sprite '" +
                    name + "'.");
                return false;
            }

            sprite.name = "Forge Sprite " + name;
            // Keep Resources.UnloadUnusedAssets from reaping it in the window
            // before a weapon references it.
            sprite.hideFlags = HideFlags.HideAndDontSave;

            if (_sprites.ContainsKey(name))
            {
                Log.LogWarning(
                    fileName + ": sprite name '" + name +
                    "' is already taken by another sheet - the earlier one " +
                    "is kept. Rename one of them.");
                return false;
            }

            _sprites[name] = sprite;
            return true;
        }

        // A PNG with no manifest becomes a single sprite named after the
        // file, centered, at the game's 20 pixels-per-unit.
        private static void LoadWholeImage(string path)
        {
            Texture2D tex = LoadSheet(path, "point");
            if (tex == null)
                return;

            string name = Path.GetFileNameWithoutExtension(path);

            if (_sprites.ContainsKey(name))
            {
                Log.LogWarning(
                    name + ": a sprite of that name already exists - " +
                    Path.GetFileName(path) + " ignored.");
                return;
            }

            Sprite sprite = Sprite.Create(
                tex,
                new Rect(0, 0, tex.width, tex.height),
                new Vector2(0.5f, 0.5f),
                DefaultPixelsPerUnit,
                0,
                SpriteMeshType.FullRect,
                Vector4.zero);

            if (sprite == null)
                return;

            sprite.name = "Forge Sprite " + name;
            sprite.hideFlags = HideFlags.HideAndDontSave;
            _sprites[name] = sprite;

            Log.LogInfo(
                "Loaded " + Path.GetFileName(path) + " as sprite '" + name +
                "' (" + tex.width + "x" + tex.height + ", whole image).");
        }

        // ImageConversion.LoadImage is reached by reflection on purpose.
        // Calling it directly binds to a ReadOnlySpan<byte> overload in this
        // Unity, and .NET Framework 4.7.2 - which this mod must target to
        // match BepInEx - has no ReadOnlySpan type at all, so it will not
        // compile. Resolving the byte[] overload at runtime avoids the
        // problem entirely and survives Unity reshuffling the signatures.
        private static MethodInfo _loadImage;
        private static bool _loadImageResolved;
        private static bool _loadImageWantsFlag;

        private static bool DecodeImage(Texture2D tex, byte[] bytes)
        {
            if (!_loadImageResolved)
            {
                _loadImageResolved = true;

                foreach (MethodInfo m in
                    typeof(ImageConversion).GetMethods(
                        BindingFlags.Public | BindingFlags.Static))
                {
                    if (m.Name != "LoadImage")
                        continue;

                    ParameterInfo[] p = m.GetParameters();

                    if (p.Length < 2 || p.Length > 3)
                        continue;
                    if (p[0].ParameterType != typeof(Texture2D))
                        continue;
                    if (p[1].ParameterType != typeof(byte[]))
                        continue;
                    if (p.Length == 3 &&
                        p[2].ParameterType != typeof(bool))
                        continue;

                    _loadImage = m;
                    _loadImageWantsFlag = p.Length == 3;
                    break;
                }

                if (_loadImage == null)
                {
                    Log.LogError(
                        "This Unity build has no byte[] overload of " +
                        "ImageConversion.LoadImage, so custom sprites " +
                        "cannot be decoded. Custom art is disabled; " +
                        "everything else still works.");
                }
            }

            if (_loadImage == null)
                return false;

            object[] args = _loadImageWantsFlag
                ? new object[] { tex, bytes, false }
                : new object[] { tex, bytes };

            try
            {
                return (bool)_loadImage.Invoke(null, args);
            }
            catch (Exception e)
            {
                Log.LogWarning("LoadImage threw: " + e.Message);
                return false;
            }
        }

        private static Texture2D LoadSheet(string path, string filter)
        {
            string key = Path.GetFullPath(path);

            Texture2D cached;
            if (_sheets.TryGetValue(key, out cached))
                return cached;

            if (!File.Exists(path))
                return null;

            byte[] bytes = File.ReadAllBytes(path);

            // mipChain false: a 7x7 pixel-art sprite has nothing to mip and
            // mips only make it mushy at distance.
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);

            if (!DecodeImage(tex, bytes))
            {
                UnityEngine.Object.Destroy(tex);
                Log.LogWarning(
                    Path.GetFileName(path) + " is not a readable PNG/JPG. " +
                    "GIF is NOT supported by the engine - convert it to a " +
                    "PNG sheet first (the html sprite tool does this).");
                return null;
            }

            // Point by default: the game's art is 7x7-ish pixel art and the
            // default bilinear filter turns that into mush.
            tex.filterMode =
                (filter ?? "point").Trim().ToLowerInvariant() == "bilinear"
                    ? FilterMode.Bilinear
                    : FilterMode.Point;
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.name = "Forge Sheet " + Path.GetFileNameWithoutExtension(path);
            tex.hideFlags = HideFlags.HideAndDontSave;
            tex.Apply(false, false);

            _sheets[key] = tex;
            return tex;
        }

        private static void WriteReadme(string folder)
        {
            string path = Path.Combine(folder, "README.txt");
            string text = ReadmeText();

            try
            {
                // Don't rewrite an identical file every launch.
                if (File.Exists(path) && File.ReadAllText(path) == text)
                    return;

                File.WriteAllText(path, text);
            }
            catch (Exception e)
            {
                Log.LogWarning(
                    "Could not write " + path + ": " + e.Message);
            }
        }

        private static string ReadmeText()
        {
            return
                @"CUSTOM SPRITES FOR WEAPON FORGE
==============================

Drop your own art in this folder and reference it from a weapon's JSON:

    ""weapon"": { ""projectileSprite"": ""myBullet"" }

The same art can also be a MUZZLE FLASH, an IMPACT PUFF and a MODULE CARD
ICON - see the end of this file.

TWO WAYS TO DO IT
-----------------

1. THE LAZY WAY - drop a PNG in here.
   The whole image becomes one sprite named after the file, centered, at
   the game's 20 pixels-per-unit. ""myBullet.png"" -> ""myBullet"".

2. A SHEET - drop a PNG plus a .json describing how to slice it.
   Use the ""Sprite Sheet Builder.html"" tool that ships with the mod: it
   opens your PNG (or GIF), lets you slice it visually, and writes this
   file for you. The format:

    {
      ""sheet"": ""myart.png"",
      ""pixelsPerUnit"": 20,
      ""filter"": ""point"",
      ""sprites"": [
        { ""name"": ""myBullet"", ""x"": 0,  ""y"": 0, ""w"": 8, ""h"": 8 },
        { ""name"": ""myShard"",  ""x"": 8,  ""y"": 0, ""w"": 8, ""h"": 8,
          ""pivotX"": 0.5, ""pivotY"": 0.5 }
      ]
    }

NAMING - THE ONE EVERYBODY TRIPS ON
-----------------------------------

""projectileSprite"" takes the name of a thing INSIDE the sheet. It is not
the .png filename and not the .json filename. A sheet holds two kinds of
named thing, and they have separate names:

  sprites     still frames. The slicer names them after the sheet:
              myart, myart_1, myart_2 ... Those numbers only keep the
              names unique - they are NOT frame numbers.
  animation   the flipbook. The slicer names it myartAnim.

So for a sheet called petrolbm.png:
  ""projectileSprite"": ""petrolbm""      -> ONE frozen frame
  ""projectileSprite"": ""petrolbmAnim""  -> the animation

Both are legal (a still frame is a fine choice), so nothing can reject
either - but the log points it out when you name a frame that is also
frame 1 of an animation. Rename anything you like; the slicer's export
panel always shows the exact line to paste.

ANIMATED PROJECTILES
--------------------

Add an ""animations"" block and point the weapon at the ANIMATION's name
instead of a sprite's - it is the same ""projectileSprite"" key either way:

    ""animations"": [
      { ""name"": ""spinBullet"",
        ""fps"": 12,
        ""loop"": ""loop"",
        ""randomStart"": true,
        ""frames"": [ ""frame0"", ""frame1"", ""frame2"", ""frame3"" ] }
    ]

  frames      the sprite names to play, IN THIS ORDER. Order comes from
              this array and nothing else - the ""_1"" / ""_2"" numbers the
              slicer puts on auto-named sprites are only there to keep
              names unique, they are not frame numbers.
  fps         frames per second. 0 or less just shows the first frame.
  loop        ""loop"" (default) / ""once"" (stops on the last frame) /
              ""pingpong"" (forward then back).
  randomStart true starts every shot on a random frame. Worth turning on
              for a shotgun - otherwise all ten pellets flip in lock-step
              and read as one strobe instead of ten spinning bullets.

A frame naming a sprite that does not exist is dropped with a warning; if
none of them resolve the whole animation is skipped. An animation may use
sprites from ANY sheet in this folder, including bare PNGs.

THINGS THAT WILL BITE YOU
-------------------------

* COORDINATES are image coordinates: x,y is the TOP-LEFT corner of the
  rect, y counts DOWNWARD. That matches every image editor. (Unity counts
  from the bottom internally; the mod flips it for you.)

* PIXELS-PER-UNIT is 20 in this game, and that is the default here. The
  stock projectile sprites are TINY - the Popper's is 7x7 pixels. If you
  draw a 64x64 bullet it will be about nine times too big; either draw
  small or raise pixelsPerUnit to shrink it.

* GIF DOES NOT WORK. The engine only reads PNG and JPG. The html tool
  will convert a GIF into a PNG sheet for you.

* NAMES are yours alone - they cannot collide with the game's own art, so
  ""trail"" or ""popper"" are fine. Sprites and animations share one pool of
  names, so an animation cannot reuse a sprite's name. Two of YOUR sheets
  using the same name will collide, and the log says which.

* Art is uploaded uncompressed, so a few small PNGs are free but a
  hundred large ones cost memory.

MUZZLE FLASHES
--------------

    ""weapon"": { ""muzzleSprite"": ""myFlashAnim"", ""muzzleSpriteFps"": 50 }

The flash belongs to the WEAPON, not the shot, so this works on every
weapon type - normal bullets, lobbed grenades, hitscan lasers, minion
spawners alike.

* AN ANIMATION IS USUALLY WHAT YOU WANT. The stock flash is a 20-frame
  flipbook, and these particle systems already run in Sprites mode, so
  naming an ""...Anim"" just swaps one flipbook for another.

* SET THE FPS. A flash lives about 0.4 seconds. A 20-frame animation
  needs roughly 50 fps to play through once in that time; leave it too
  slow and you only ever see the first few frames. Blank uses the
  animation's own fps from the sheet.

* DRAW IT SMALL, like a bullet - 20 pixels per world unit. This is the
  opposite of icons, which are drawn at 40.

* IT REPLACES BOTH LAYERS. Most muzzle prefabs are two stacked puffs;
  the art goes on all of them, because doing only the first leaves the
  stock puff burning through underneath.

* COLOUR. Without ""muzzleColor"" the template's own coloured fade is
  drained to grey so your art shows its true colours - otherwise a red
  template would paint your sprite red. The brightness fade is kept, so
  the flash still fades out. Add ""muzzleColor"" to tint on purpose.

IMPACT PUFFS
------------

    ""weapon"": { ""impactSprite"": ""mySplatAnim"" }

The puff where a shot lands. Same rules as the muzzle flash - it is a
particle, so draw it small and square and pixelsPerUnit does not apply.

* IT COVERS THREE SLOTS: the impact effect, the out-of-range effect and
  the end-of-life effect. One key, because ""my impact looks like this""
  means the shot's death however it died.

* NOT EVERY TEMPLATE HAS ONE. Popper, Flame and Toroid do; plenty do
  not. The log says when there was nothing to apply to.

* ON A BEAM it becomes the hit spark, and needs a ""beam"" block.

There is a matching SOUND, which is a different kind of thing entirely -
the game has no impact sound at all, so ""impactSfx"" is a noise this mod
plays itself rather than a slot it fills. See HOW TO MAKE WEAPONS.txt.

MODULE CARD ICONS
-----------------

The picture on the module card is the same kind of thing, so the same
names work there. In a WEAPON FORGE weapon file:

    ""module"": { ""icon"": ""myicon"" }

and in a MODULE FORGE module file, the top-level ""icon"". (Module Forge
has no sprites folder of its own - it borrows this one, so there is one
folder and one set of names for both mods. Custom icons therefore need
Weapon Forge installed; stock icon names work either way.)

Four things to know, because an icon is drawn by two different renderers:

* DRAW IT SQUARE. Stock icons are 24x24. The card on the grid stretches
  art to fill a square and does not preserve the aspect ratio, so a tall
  or wide picture looks squashed there - while the pickup lying on the
  ground keeps its shape. Any SIZE is fine (it is scaled to match the
  stock icons automatically, at the icons' own 40 pixels-per-unit rather
  than the 20 used for bullets), but the SHAPE is yours to get right.

* IT IS TINTED by the module's ""color"", in both places. Draw in white or
  grey and let color do the work, or set ""color"": ""#ffffff"" to see your
  art's own colours.

* AN ICON CANNOT ANIMATE. The game assigns the sprite once and nothing
  ticks it. Naming an animation gets you its first frame, and the log
  says so.

* THE GAME WINS A NAME CLASH. Naming your PNG after a stock sprite gets
  the stock one, with a warning telling you to rename yours. Stock module
  icons are called HUD_GridTiles_NN.

Anything the mod could not read is reported in BepInEx\LogOutput.log -
search for ""WeaponForge.Sprites"".
";
        }
    }
}
