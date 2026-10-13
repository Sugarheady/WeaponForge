using System.Collections.Generic;
using UnityEngine;

namespace WeaponForge
{
    // BLOOM - a shot that APPEARS at the crosshair, swells, and pops.
    //
    // Nothing travels. That is the whole feature and it is why it can "shoot
    // around objects": there is no flight for geometry to interrupt, so a wall
    // between you and the point is not in the way of anything.
    //
    // WHAT IT IS DELIBERATELY NOT BUILT FROM, because both near misses are
    // tempting and both are wrong:
    //
    //   A HITSCAN BEAM is the feel - damage where you point, more or less at
    //   once - but a beam is a ray FROM THE BARREL and is stopped by the very
    //   object you wanted to shoot past.
    //
    //   AN EXPLOSION is the shape, and the one spawned from a prefab
    //   (ExplosionComponent) never sets Owner, so it credits no kills and obeys
    //   no friendly-fire rule. (Projectile.SpawnExplosion DOES set it - the two
    //   routes differ, and only the prefab one is ownerless.)
    //
    // So it is a real Projectile with its velocity zeroed. It keeps its Owner,
    // its Damage, its damage type, its burn, its explosion and its kill
    // attribution, and the pop is delivered through the game's own
    // IProjectileListener.ProjectileCollided - which is the same call a normal
    // bullet makes. That one decision is what buys, for free: kill credit,
    // friendly fire, the damage-type table, burn on contact, the cell shake,
    // and TERRAIN DIGGING WITH THE CORRECT ARMOUR CHECK
    // (cellType.GetDamageAmount(damage) > 0), which is exactly the stock
    // behaviour rather than an imitation of it.
    //
    // ---- THE STAT MAPPING ------------------------------------------------
    //
    // Every number is a stat the weapon already has, so the module a player
    // already owns tunes the right thing:
    //
    //   projectileRadius   the bloom's FINAL SIZE
    //   projectileSpeed    how fast it INFLATES (units of radius per second)
    //   rangeData.range    how far from the ship it can be PLACED
    //   spread             how far from the cursor the blooms sit (see below)
    //   projectileCount    how many blooms per trigger pull
    //   angleVariance      scatter - jitter off each ring slot
    //   angleOffset        rotates the ring (so `spiral` spins the pattern)
    //   fireRate           how fast you make another
    //   pushForce          the pop's shove, when `push` is on
    //   burstSize/Delay    a walking line of blooms, one per burst step
    //
    // `Range` and `Speed` are both reachable by the game's own
    // ModifyWeaponProperty (via IHasRangeProperty / IHasSpeedProperty), so a
    // stock Range or Speed module really does extend the reach and quicken the
    // inflate with no code here.
    //
    // THE FUSE IS DERIVED, not authored: size / inflate rate. Two of the three
    // numbers fix the third, and these two were chosen because both are
    // module-reachable and the third is not. `fuse` exists as an override, and
    // is the only answer when the inflate rate is zero.
    //
    // ---- WHY `spread` MEANS A RADIUS HERE ---------------------------------
    //
    // This is a top-down 2D game, so a volley scatters in ONE axis:
    // WeaponBase.GetDirections fans pellets by angle and by nothing else. Project
    // that real cone onto the point you are aiming at and you get a LINE of
    // blooms perpendicular to your aim, not the ring people picture.
    //
    // So `spread` keeps meaning "how wide this gun throws" and is read as the
    // cone half-angle, giving
    //
    //     ringRadius = placementDistance * tan(spread / 2)
    //
    // which is the pattern width a real shotgun would have produced at that
    // distance. The blooms are then spaced evenly AROUND that circle rather
    // than along an arc. Consequences worth stating because they are all
    // consistent with the rest of the game:
    //
    //   * the ring widens the further out you place it, like a real pattern
    //   * spread 0 stacks every bloom dead on the crosshair
    //   * more pellets makes the ring DENSER, not wider - which is precisely
    //     what GetDirections does for every other weapon in the game
    //     (step = spread / (count - 1)), so bloom does not need a special rule
    //   * ONE pellet always lands on the centre, matching GetDirections' own
    //     `ProjectileCount == 1` special case
    //
    // ---- THE DRIFT MODE ---------------------------------------------------
    //
    // A static bloom is dodgeable by walking: the circle is a fixed area and
    // the fuse gives you time to leave it. `drift` gives each bloom a heading
    // so the area CLOSES instead, and the default `inward` is what produces the
    // four-blooms-crossing-through-the-centre pattern at count 4 - without
    // needing a rule about "opposite sides", which cannot be defined at count 1
    // or count 3.
    //
    // The drift speed is derived too: fast enough to reach the centre exactly
    // as it pops. So the pattern needs no tuning at all.
    //
    // AND IT IS THE DOOR TO EVERY STEERING FEATURE. wave, homing, guided and
    // the game's own noise wobble all rebuild Velocity as
    // `rotate(idealDirection) * Velocity.magnitude` - so at zero speed they
    // propagate zero and do nothing whatever. Any non-zero drift wakes all of
    // them at once, and they compose through ForgeSteer exactly as usual.
    //
    // ---- SPIRAL, STAGGERED, AND BULLET MODE (2026-10-02) ------------------
    //
    // His design, Docs/BLOOM SPIRAL - CONCEPT.md. Two more drifts and one more
    // `when`, all inside this block:
    //
    //   spiral     each shot winds into the centre on an even spiral, `turns`
    //              laps, clockwise or counter. Re-aimed every step, so it is
    //              the one drift that owns its shot's direction outright -
    //              wave, homing and guided are overruled on it, said at build.
    //   staggered  straight in, but one after another round the ring (the R19
    //              look, which was a bug then: the ship's motion gave every
    //              pellet its own speed). The last lands `stagger` x the fuse
    //              after the first.
    //   bullet     no swell and no pop. The shot is PLACED like a bloom and
    //              then flies the drift's path as the gun's own projectile -
    //              its art, its hits, its piercing, its impact - including a
    //              rocket (ForgeBloomBullet).
    //
    // The geometry is ForgeBloomPath, which has no Unity in it so a probe can
    // run it.
    public static class ForgeBloom
    {
        // WHEN it hurts.
        //   Pop    - harmless while it swells, one application at the burst.
        //            Telegraphed and dodgeable; the size is a warning.
        //   Swell  - hurts from the moment it appears, over a growing area.
        //   Bullet - neither. It flies the drift's path as an ordinary shot
        //            and hurts the way that shot always does.
        public enum When { Pop, Swell, Bullet }

        // WHO it hurts inside the circle.
        //   All     - everything overlapping it.
        //   Nearest - only whatever is closest to the centre of the pop.
        public enum Pick { All, Nearest }

        // Where a bloom heads while it lives. None is a parked detonation.
        // NEW VALUES GO AT THE END: nothing serialises this enum today, but a
        // reordered enum is the kind of change that stays invisible until
        // something does.
        public enum Drift { None, Inward, Outward, Along, Spin, Spiral, Staggered }

        // Which way round a spiral, a staggered close or a spin goes. Default
        // means "whatever that drift does when the file does not say" - which
        // is CLOCKWISE for spiral and staggered (his reference picture) and
        // COUNTER-clockwise for spin, because spin has always turned that way
        // and flipping it would change every spin file already written.
        public enum Turn { Default, Clockwise, Counter }

        // How a spiralling BUBBLE moves along its path (his answer: both).
        //   Steady - the same speed all the way, so it turns faster and faster
        //            as the laps tighten, like water down a drain.
        //   Even   - the same turning rate all the way: fastest on the outer
        //            lap, easing into the middle.
        // A bullet always flies at a steady projectileSpeed.
        public enum Pace { Steady, Even }

        // What a BULLET does when it reaches the centre.
        //   End     - its flight ends there (the default). What it fires is
        //             what it would at the end of its range or its lifetime,
        //             so a missile explodes and a Popper simply goes.
        //   Through - it carries on straight, out the other side, for as far
        //             as the gun reaches (or until its own lifetime ends).
        //   Burst   - it goes off as if it had hit something: its impact
        //             effect, explosion, sub-emitter and discharge.
        public enum Centre { End, Through, Burst }

        // How a MISSILE flies the path.
        //   Fly   - the mod sets its velocity every step and switches its own
        //           engine off for that shot: the exact path, no wobble, at a
        //           steady projectileSpeed (the default).
        //   Chase - its own engine chases a point moving along the path: it
        //           feels like a rocket, and cuts the laps it cannot turn
        //           tightly enough for.
        public enum Missile { Fly, Chase }

        // Where a bloom lands when NOTHING is pointing at a spot - a minion, a
        // turret, an enemy, or a wingman in one of its direction-aiming modes.
        //   MaxRange - straight out along the firing direction, at full reach.
        //   FirstHit - at the first thing that direction runs into, so it
        //              behaves like the hitscan weapon it resembles.
        public enum Blind { MaxRange, FirstHit }

        // When the reach indicator is drawn.
        public enum RingShow { Off, Held, Always, OutOfRange }

        public class Config
        {
            // ---- when and who ------------------------------------------
            public When when = When.Pop;
            public Pick pick = Pick.All;

            // Swell mode only: re-hit something already hit, every this many
            // seconds. 0 = each thing is hit exactly once as the circle passes
            // over it (the default). NEVER leave a re-tick at 0 by accident -
            // that is a hit every frame of an overlap.
            public float retick;

            // ---- placement ---------------------------------------------
            // Reach comes from rangeData.range; this only says what to do past
            // it. Clamping slides the bloom down the line to the cursor, which
            // is what the author chose: refusing would cost you the shot.
            public Blind blind = Blind.MaxRange;

            // ---- fuse --------------------------------------------------
            // Normally derived as finalRadius / inflateRate. Set this to take
            // it over, and it is the ONLY answer when the inflate rate is 0.
            public float fuse;

            // ---- drift -------------------------------------------------
            public Drift drift = Drift.None;

            // Units per second. 0 = derived, so a bloom reaches the middle of
            // the pattern exactly as it goes off.
            public float driftSpeed;

            // ---- spiral / staggered (2026-10-02) ------------------------
            // Laps on the way in. 1 by default; his reference picture is about
            // 3. 0 is a straight close, the same as inward.
            public float turns = 1f;

            public Turn direction = Turn.Default;

            // Staggered: the LAST shot lands this many fuses after the first
            // (a share of the fuse for a bubble, of the flight time for a
            // bullet). 0.5 by default.
            public float stagger = 0.5f;

            // Whether the file NAMED `stagger`. A spiral is staggered only when
            // it does (his answer, 2026-10-02: "spiral + stagger", the later
            // shots SLOWER, the way staggered does it) - so every spiral already
            // written, none of which has the key, is unchanged.
            public bool staggerSet;

            // Spiral, bubbles only.
            public Pace pace = Pace.Steady;

            // ---- bullet mode --------------------------------------------
            public Centre atCentre = Centre.End;
            public Missile missile = Missile.Fly;

            // Whether the file NAMED `atCentre`. A GRENADE that does not goes
            // off at the centre (his answer, 2026-10-09: "default to go off
            // there") while every other bullet keeps `end`, so this is the
            // only way to tell "chose end" from "said nothing".
            public bool atCentreSet;

            // ---- a GRENADE in bullet mode (2026-10-09) ------------------
            // Thrown once from its ring slot, never flown - see ForgeBloomPath
            // "the throw". How the ring arrives (his three, "default to all at
            // once"), and how high: 1 is a level throw at the gun's speed (his
            // pick, "flat and fast"), 2 takes twice as long, 3 is a mortar.
            public ForgeBloomPath.Arrival arrive = ForgeBloomPath.Arrival.Together;
            public bool arriveSet;
            public float lob = 1f;

            // ---- the pop -----------------------------------------------
            // Shove what it catches, using the weapon's own pushForce. Off by
            // default: a pop that throws bodies is a large change to how a
            // weapon feels and should be asked for.
            public bool push;

            // Destroy terrain cells OUTRIGHT, skipping the armour check every
            // other weapon in the game goes through. Off by default, so a
            // bloom digs exactly as hard as its damage says it should.
            public bool digThrough;

            // ---- absorbing enemy fire ----------------------------------
            // A live bloom eats enemy shots that touch it. Off by default: the
            // bubble is cosmetic until it pops.
            public bool absorb;

            // Absorbed shots are turned around instead of deleted, through the
            // same machinery `rally` uses - so a reflected bullet becomes
            // YOURS and can kill what fired it. Needs `absorb`.
            public bool deflect;

            // ---- the reach indicator -----------------------------------
            public RingShow ring = RingShow.Off;
            // EMPTY = a clean ring drawn in code (ForgeBloomRing.DrawnRing).
            // Until R19 this defaulted to "Sprite Particle Toroid_0", which is
            // the electric toroid's star-burst particle rather than a circle -
            // stretched to the full reach it read as "a giant sprite behind my
            // character" (tests 247/250).
            public string ringSprite = "";
            public Color ringColor = new Color(0.35f, 0.85f, 1f, 0.30f);

            // R19 test 276, his asks on top of the circle itself: "there should
            // be a fade time scale somewhere for the other modes. It seems kind
            // of jarring to have it flash ... Maybe an option to change the
            // ring thickness and opacity."
            //
            //   ringFade     seconds to fade in AND out as the ring is shown or
            //                put away. 0 = instant, which is what shipped.
            //   ringOpacity  a multiplier on the colour's own alpha, so a
            //                preset's colour can be dimmed without retyping it.
            //   ringWidth    the LINE's thickness in world units, the same at
            //                any reach. 0 = the drawn ring's own proportion
            //                (thicker as the ring grows), which is what shipped.
            //                Only the drawn ring - a ringSprite is its own art.
            public float ringFade;
            public float ringOpacity = 1f;
            public float ringWidth;

            // ---- runtime, not authored ---------------------------------
            // Set by the builder so the shot can read its own weapon's numbers
            // without a lookup. These are the TEMPLATE values; the live weapon
            // may have been modified, which is why the shot prefers what it
            // was actually handed and falls back to these.
            [System.NonSerialized] public float templateRange = 40f;

            public bool Bullet
            {
                get { return when == When.Bullet; }
            }

            // How far apart this drift lands its shots: the staggered close
            // always (0.5 unless the file says), a spiral only when the file
            // names `stagger`, anything else never.
            public float StaggerFor(Drift d)
            {
                if (d == Drift.Staggered)
                    return stagger;

                if (d == Drift.Spiral && staggerSet)
                    return stagger;

                return 0f;
            }

            // Resolved per drift - see Turn. Spin keeps its old way round.
            public bool Clockwise(Drift d)
            {
                if (direction == Turn.Clockwise)
                    return true;

                if (direction == Turn.Counter)
                    return false;

                return d != Drift.Spin;
            }
        }

        // Build-time registration, keyed by the weapon asset. NOT cleared at
        // run entry: these are parsed once from the JSON at startup, and
        // clearing them would unbuild the weapon for the rest of the session.
        // (See ForgeCrit.Reset for the same trap written out in full.)
        private static readonly Dictionary<WeaponData, Config> _weapons =
            new Dictionary<WeaponData, Config>();

        public static bool Any { get { return _weapons.Count > 0; } }

        public static void Register(WeaponData weapon, Config cfg)
        {
            if (weapon == null || cfg == null)
                return;

            _weapons[weapon] = cfg;
        }

        public static bool TryGet(WeaponData weapon, out Config cfg)
        {
            cfg = null;

            return weapon != null && _weapons.TryGetValue(weapon, out cfg);
        }

        // ---- the ring geometry ------------------------------------------
        //
        // The pattern width a cone of `spreadDegrees` would have had at
        // `distance`. Half-angle is clamped well short of 90 degrees, where the
        // tangent runs away to infinity - the game clamps spread to 0..360 and
        // a value above about 150 is already a ring wider than the screen.
        public static float RingRadius(float spreadDegrees, float distance)
        {
            if (spreadDegrees <= 0f || distance <= 0f)
                return 0f;

            float half = Mathf.Clamp(spreadDegrees * 0.5f, 0f, 75f);

            return distance * Mathf.Tan(half * Mathf.Deg2Rad);
        }

        // Where pellet `index` of `count` sits, relative to the centre of the
        // pattern.
        //
        // ONE PELLET IS ALWAYS CENTRED, which is not an arbitrary kindness -
        // GetDirections has the identical special case (`ProjectileCount == 1`
        // uses AngleOffset alone and ignores spread), so a single-pellet bloom
        // behaves like a single-pellet anything.
        public static Vector2 RingOffset(
            int index, int count, float radius,
            float angleOffsetDegrees, float varianceDegrees)
        {
            if (count <= 1 || radius <= 0f)
                return Vector2.zero;

            float step = 360f / count;

            float angle = angleOffsetDegrees + index * step;

            if (varianceDegrees > 0f)
                angle += Random.Range(-varianceDegrees, varianceDegrees);

            float r = angle * Mathf.Deg2Rad;

            return new Vector2(Mathf.Cos(r), Mathf.Sin(r)) * radius;
        }

        // ---- the Unity side of ForgeBloomPath ----------------------------
        //
        // That file stays free of Unity so the probe can compile it alone;
        // these are the five conversions the shots need, and nothing else.
        public static ForgeBloomPath.Spiral MakeSpiral(
            Vector2 from, Vector2 centre, float turns, bool clockwise)
        {
            return ForgeBloomPath.Make(
                from.x, from.y, centre.x, centre.y, turns, clockwise);
        }

        public static Vector2 SpiralAt(
            ref ForgeBloomPath.Spiral s, float along, ref double phi)
        {
            double x, y;
            ForgeBloomPath.AtDistance(ref s, along, ref phi, out x, out y);
            return new Vector2((float)x, (float)y);
        }

        public static Vector2 SpiralAtShare(
            ref ForgeBloomPath.Spiral s, float share)
        {
            double x, y;
            ForgeBloomPath.AtShare(ref s, share, out x, out y);
            return new Vector2((float)x, (float)y);
        }

        public static Vector2 SpiralStartHeading(ref ForgeBloomPath.Spiral s)
        {
            double x, y;
            ForgeBloomPath.StartHeading(ref s, out x, out y);
            return new Vector2((float)x, (float)y);
        }

        public static Vector2 SpiralEndHeading(ref ForgeBloomPath.Spiral s)
        {
            double x, y;
            ForgeBloomPath.EndHeading(ref s, out x, out y);
            return new Vector2((float)x, (float)y);
        }

        // ---- word parsing ------------------------------------------------

        public static bool ParseWhen(string s, out When w)
        {
            w = When.Pop;

            if (string.IsNullOrEmpty(s))
                return true;

            switch (s.Trim().ToLowerInvariant())
            {
                case "pop":
                case "burst":
                case "onpop":
                case "end":
                    w = When.Pop;
                    return true;

                case "swell":
                case "grow":
                case "always":
                case "while":
                    w = When.Swell;
                    return true;

                case "bullet":
                case "bullets":
                case "normal":
                case "projectile":
                case "shot":
                    w = When.Bullet;
                    return true;
            }

            return false;
        }

        public static bool ParsePick(string s, out Pick p)
        {
            p = Pick.All;

            if (string.IsNullOrEmpty(s))
                return true;

            switch (s.Trim().ToLowerInvariant())
            {
                case "all":
                case "everything":
                case "area":
                    p = Pick.All;
                    return true;

                case "nearest":
                case "closest":
                case "centre":
                case "center":
                case "one":
                    p = Pick.Nearest;
                    return true;
            }

            return false;
        }

        public static bool ParseDrift(string s, out Drift d)
        {
            d = Drift.None;

            if (string.IsNullOrEmpty(s))
                return true;

            switch (s.Trim().ToLowerInvariant())
            {
                case "none":
                case "off":
                case "static":
                case "park":
                    d = Drift.None;
                    return true;

                case "inward":
                case "in":
                case "converge":
                case "close":
                    d = Drift.Inward;
                    return true;

                case "outward":
                case "out":
                case "expand":
                case "shockwave":
                    d = Drift.Outward;
                    return true;

                case "along":
                case "forward":
                case "aim":
                    d = Drift.Along;
                    return true;

                case "spin":
                case "swirl":
                case "tangent":
                case "orbit":
                    d = Drift.Spin;
                    return true;

                // "spiral" is ALSO the name of a separate top-level block,
                // which turns where each pull's RING starts. The two are
                // different keys in different places, so the word can mean the
                // path here without any confusion in the parser - the how-to
                // says so for the reader.
                case "spiral":
                case "vortex":
                case "whirlpool":
                case "corkscrew":
                    d = Drift.Spiral;
                    return true;

                case "staggered":
                case "stagger":
                case "cascade":
                case "sequence":
                    d = Drift.Staggered;
                    return true;
            }

            return false;
        }

        public static bool ParseTurn(string s, out Turn t)
        {
            t = Turn.Default;

            if (string.IsNullOrEmpty(s))
                return true;

            switch (s.Trim().ToLowerInvariant().Replace("-", "").Replace(" ", ""))
            {
                case "clockwise":
                case "cw":
                    t = Turn.Clockwise;
                    return true;

                case "counter":
                case "counterclockwise":
                case "anticlockwise":
                case "ccw":
                    t = Turn.Counter;
                    return true;
            }

            return false;
        }

        public static bool ParsePace(string s, out Pace p)
        {
            p = Pace.Steady;

            if (string.IsNullOrEmpty(s))
                return true;

            switch (s.Trim().ToLowerInvariant())
            {
                case "steady":
                case "constant":
                case "drain":
                    p = Pace.Steady;
                    return true;

                case "even":
                case "eventurn":
                case "turning":
                    p = Pace.Even;
                    return true;
            }

            return false;
        }

        public static bool ParseCentre(string s, out Centre c)
        {
            c = Centre.End;

            if (string.IsNullOrEmpty(s))
                return true;

            switch (s.Trim().ToLowerInvariant())
            {
                case "end":
                case "stop":
                case "vanish":
                    c = Centre.End;
                    return true;

                case "through":
                case "pass":
                case "continue":
                case "carry":
                    c = Centre.Through;
                    return true;

                case "burst":
                case "impact":
                case "explode":
                case "hit":
                    c = Centre.Burst;
                    return true;
            }

            return false;
        }

        public static bool ParseMissile(string s, out Missile m)
        {
            m = Missile.Fly;

            if (string.IsNullOrEmpty(s))
                return true;

            switch (s.Trim().ToLowerInvariant())
            {
                case "fly":
                case "drive":
                case "exact":
                    m = Missile.Fly;
                    return true;

                case "chase":
                case "steer":
                case "follow":
                    m = Missile.Chase;
                    return true;
            }

            return false;
        }

        // A grenade ring's arrival. "staggered" is the staggered drift's word.
        public static bool ParseArrive(string s, out ForgeBloomPath.Arrival a)
        {
            a = ForgeBloomPath.Arrival.Together;

            if (string.IsNullOrEmpty(s))
                return true;

            switch (s.Trim().ToLowerInvariant().Replace("-", "").Replace(" ", ""))
            {
                case "together":
                case "atonce":
                case "allatonce":
                case "all":
                case "same":
                    a = ForgeBloomPath.Arrival.Together;
                    return true;

                case "staggered":
                case "stagger":
                case "oneafteranother":
                case "sequence":
                case "cascade":
                    a = ForgeBloomPath.Arrival.Staggered;
                    return true;

                case "arc":
                case "ownarc":
                case "own":
                case "each":
                case "eachonitsownarc":
                    a = ForgeBloomPath.Arrival.Arc;
                    return true;
            }

            return false;
        }

        // `lob` as a word - his three looks, flat and fast by default - or a
        // number written as text. The caller clamps.
        public static bool ParseLob(string s, out float lob)
        {
            lob = 1f;

            if (string.IsNullOrEmpty(s))
                return true;

            float v;

            if (float.TryParse(
                    s.Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out v))
            {
                lob = v;
                return true;
            }

            switch (s.Trim().ToLowerInvariant())
            {
                case "flat":
                case "fast":
                case "low":
                    lob = 1f;
                    return true;

                case "medium":
                case "mid":
                case "arc":
                    lob = 2f;
                    return true;

                case "high":
                case "lob":
                case "mortar":
                    lob = 3f;
                    return true;
            }

            return false;
        }

        public static string ArriveWord(ForgeBloomPath.Arrival a)
        {
            switch (a)
            {
                case ForgeBloomPath.Arrival.Staggered:
                    return "one after another";
                case ForgeBloomPath.Arrival.Arc:
                    return "each on its own arc, thrown equally hard";
                default:
                    return "all at once";
            }
        }

        // Does this drift end AT the centre? The ones that do are the ones the
        // bullet-mode `atCentre` rule applies to.
        public static bool ReachesCentre(Drift d)
        {
            return d == Drift.Inward || d == Drift.Spiral ||
                   d == Drift.Staggered;
        }

        public static bool ParseBlind(string s, out Blind b)
        {
            b = Blind.MaxRange;

            if (string.IsNullOrEmpty(s))
                return true;

            switch (s.Trim().ToLowerInvariant())
            {
                case "maxrange":
                case "range":
                case "far":
                case "full":
                    b = Blind.MaxRange;
                    return true;

                case "firsthit":
                case "hitscan":
                case "first":
                case "beam":
                    b = Blind.FirstHit;
                    return true;
            }

            return false;
        }

        public static bool ParseRing(string s, out RingShow r)
        {
            r = RingShow.Off;

            if (string.IsNullOrEmpty(s))
                return true;

            switch (s.Trim().ToLowerInvariant())
            {
                case "off":
                case "never":
                case "none":
                    r = RingShow.Off;
                    return true;

                case "held":
                case "firing":
                case "trigger":
                    r = RingShow.Held;
                    return true;

                case "always":
                case "on":
                    r = RingShow.Always;
                    return true;

                case "outofrange":
                case "far":
                case "warn":
                    r = RingShow.OutOfRange;
                    return true;
            }

            return false;
        }

        public static string DriftWord(Drift d)
        {
            switch (d)
            {
                case Drift.Inward: return "closing on the centre";
                case Drift.Outward: return "spreading outward";
                case Drift.Along: return "sweeping along your aim";
                case Drift.Spin: return "swirling around the centre";
                case Drift.Spiral: return "spiralling into the centre";
                case Drift.Staggered: return "closing on the centre one after another";
                default: return "parked where it lands";
            }
        }
    }
}
