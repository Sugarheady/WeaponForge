using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;

namespace WeaponForge
{
    // One bloom: the live bubble. Rides the projectile prefab, so every shot
    // fired from a bloom weapon gets its own.
    //
    // THE CONFIG ARRIVES AS AN INT, not as a reference. Unity's Instantiate
    // copies SERIALIZED state only, and a plain C# Config class is not
    // serializable - so `public ForgeBloom.Config cfg` on a prefab-attached
    // component is null on every shot, with a perfectly healthy build log
    // saying otherwise. That is the single most expensive bug this project has
    // had (it silently disabled the whole of `gas` and `catalyst`). An int
    // Unity serializes happily, and ForgeConfigTable resolves it back to the
    // ONE shared instance in Awake - before Shoot, before any patch, so it
    // never depends on Harmony ordering.
    public class ForgeBloomShot : MonoBehaviour
    {
        private static readonly ManualLogSource Log =
            ForgeLog.Source("WeaponForge.Bloom");

        private static bool _saidNoLevel;
        private static bool _saidDigCap;

        // Resolved once. The layer index is what tells terrain apart from a
        // target cheaply enough to ask per collider per frame.
        private static int _groundLayer = -1;

        private static int GroundLayer
        {
            get
            {
                if (_groundLayer < 0)
                    _groundLayer = LayerMask.NameToLayer("Ground");

                return _groundLayer;
            }
        }

        // ---- serialized (survives Instantiate) --------------------------
        public int cfgId = -1;

        // Set by ApplyRally when the weapon also carries a `rally` block, so a
        // deflected shot gains the stacking bonuses instead of merely turning
        // around. Same int-not-reference rule as cfgId.
        public int rallyId = -1;

        // The template's numbers, so a bloom can still be sized when the live
        // weapon could not be consulted. The Shoot prefix prefers what the shot
        // was actually handed - those carry module augmentations, these do not.
        public float fallbackRadius = 1.5f;
        public float fallbackRate = 6f;
        public float fallbackRange = 40f;

        // ---- runtime -----------------------------------------------------
        [System.NonSerialized] public ForgeBloom.Config cfg;

        [System.NonSerialized] public bool live;
        [System.NonSerialized] public float bornAt;
        [System.NonSerialized] public float fuse;
        [System.NonSerialized] public float finalRadius;
        [System.NonSerialized] public Vector2 centre;      // of the pattern
        [System.NonSerialized] public Vector2 heading;     // drift, may be zero
        [System.NonSerialized] public float driftSpeed;

        // spiral: the path, and the warm start for solving it each step
        private ForgeBloomPath.Spiral _spiral;
        private double _phi;
        private bool _spinClockwise;

        // THE SHOT'S OWN END-OF-LIFE SETTINGS, TAKEN OVER RATHER THAN SHARED.
        //
        // Both of these are copied off the projectile in the Shoot prefix and
        // then switched OFF on the instance, because a bloom has to own the
        // moment it ends. Leaving lifetimeData live is a RACE we would lose
        // half the time: the fuse is clamped to lifetimeData.time, so the two
        // fire on the same frame - and HandleLifetime runs in Update while the
        // pop runs in FixedUpdate. Whichever goes first wins, and if the
        // engine's does, the object is destroyed and THE DAMAGE NEVER HAPPENS.
        //
        // Taking the copies also means the hooks below can still be fired at
        // the pop, which is the only moment they make sense on a bloom.
        [System.NonSerialized] public ProjectileLifetimeData life;
        [System.NonSerialized] public ProjectileImpactBehaviour impact;

        private Projectile _p;
        private Vector3 _baseScale = Vector3.one;
        private ForgeRally.Config _rally;
        private bool _rallyResolved;

        // Swell mode bookkeeping. `once` is the default, so the common case is
        // a set membership test and no allocation per frame.
        private readonly HashSet<IProjectileListener> _hitOnce =
            new HashSet<IProjectileListener>();

        private readonly Dictionary<IProjectileListener, float> _hitAt =
            new Dictionary<IProjectileListener, float>();

        // Terrain is dedupled by CELL, not by listener - a whole level segment
        // is ONE collider with one listener, so the listener-level set above
        // would let a bloom break exactly one cell and then refuse the rest.
        private readonly HashSet<long> _hitCells = new HashSet<long>();

        // Reused across every overlap this bloom performs, and across every
        // bloom, because they all run on the main thread one at a time. An
        // OverlapCircle per FixedUpdate per bubble is the steady state of
        // `swell` mode with stacking uncapped, so it must not allocate.
        private static readonly List<Collider2D> _found = new List<Collider2D>();
        private static readonly List<Component> _caught = new List<Component>();

        public ForgeRally.Config Rally
        {
            get
            {
                if (!_rallyResolved)
                {
                    _rallyResolved = true;

                    if (rallyId >= 0)
                        _rally = ForgeConfigTable.Get<ForgeRally.Config>(rallyId);
                }

                return _rally;
            }
        }

        private void Awake()
        {
            if (cfgId >= 0)
                cfg = ForgeConfigTable.Get<ForgeBloom.Config>(cfgId);

            _p = GetComponent<Projectile>();
        }

        // Called from the Shoot POSTFIX - not the prefix, and the distinction
        // is the load-bearing ordering in this feature. The prefix places the
        // shot and zeroes its velocity so that Shoot derives a start speed of
        // zero; applying the drift there instead would let Shoot capture the
        // DRIFT speed, which brings the engine's range handler back to life and
        // deletes a drifting bloom mid-swell. See ForgeBloomPatch's header.
        //
        // `index` / `count` are this bubble's slot on the ring, which only the
        // staggered close needs: the arrivals go round the ring in order.
        public void Begin(
            Vector2 patternCentre,
            float radius,
            float inflateRate,
            Vector2 aimAxis,
            int index,
            int count)
        {
            if (cfg == null || _p == null)
                return;

            centre = patternCentre;
            finalRadius = Mathf.Max(0.05f, radius);
            bornAt = Time.time;

            // ★ THE DRAWN BUBBLE IS SIZED TO THE HITBOX, not to the prefab.
            //
            // This is the one place bloom cannot inherit the game's own
            // relationship between art and radius, because there isn't one:
            // `Radius` is a cast radius in world units and the art is whatever
            // the prefab happens to be. `grow` gets away with ramping both by
            // the same MULTIPLIER, so whatever relationship they started in is
            // preserved - but bloom ramps from zero to an AUTHORED radius, and
            // the art would arrive at the prefab's ordinary size no matter what
            // that number said.
            //
            // Measured: the stock circle art is 20px at 20 pixels-per-unit, so
            // one world unit across - a half-width of 0.5. A bloom authored at
            // projectileRadius 3.5 would therefore draw a dot and kill things
            // three and a half units away from it. Seven times out, and it
            // would read as the feature being broken rather than as art.
            //
            // So the art is rescaled to make the drawn edge the damage edge,
            // which is what every line of the docs promises. Note this OVERRIDES
            // projectileScale on a bloom: the size is the hitbox, by definition.
            _baseScale = transform.localScale;

            float half = ArtHalfWidth();

            if (half > 0.0001f)
                _baseScale = _baseScale * (finalRadius / half);

            // THE FUSE IS DERIVED: size / inflate rate. Two of the three
            // numbers fix the third and these two are the ones a stock module
            // can reach (IHasSpeedProperty), so they are the ones the author
            // sets. An explicit `fuse` takes over, and it is the only answer
            // when the rate is zero - where the derivation is a division by it.
            if (cfg.fuse > 0f)
                fuse = cfg.fuse;
            else if (inflateRate > 0f)
                fuse = finalRadius / inflateRate;
            else
                fuse = 0.5f;

            // lifetimeData is a CEILING, never the driver - read off the COPY
            // taken before the prefix switched it off on the instance, so the
            // engine's own handler cannot race the pop for it.
            if (life.enabled && life.time > 0f)
                fuse = Mathf.Min(fuse, life.time);

            fuse = Mathf.Max(0.02f, fuse);

            // STAGGERED (and a spiral that names `stagger`): each bubble gets
            // its own fuse, the later ones longer,
            // so they land one after another round the ring - and because the
            // swell is radius / fuse and the drift is span / fuse, each one
            // still reaches the middle exactly as it pops. That is what R19's
            // bug did by accident (the ship's motion gave every pellet its own
            // rate); here the order is chosen. Applied AFTER the lifetime
            // ceiling on purpose: the ceiling is a race guard for the engine's
            // own handler, which a bloom switches off, so it caps the FIRST
            // bubble and the rest follow it.
            //
            // On a spiral the same scaling is the whole of "staggered spiral":
            // the fuse is longer, the derived path speed (length / fuse) is
            // lower, so the later bubbles wind the same arm more slowly and
            // land one after another round the ring.
            float staggerScale = 1f;
            float stagger = cfg.StaggerFor(cfg.drift);

            if (stagger > 0f)
            {
                staggerScale = (float)ForgeBloomPath.StaggerScale(
                    ForgeBloomPath.StaggerShare(
                        index, count, cfg.Clockwise(cfg.drift)),
                    stagger);

                fuse *= staggerScale;
            }

            // Drift. The speed is derived as well: far enough to reach the
            // middle of the pattern exactly as it goes off, so four blooms on a
            // rim heading inward cross at the centre with nothing tuned.
            heading = Vector2.zero;

            if (cfg.drift != ForgeBloom.Drift.None)
            {
                Vector2 here = transform.position;
                Vector2 out_ = here - centre;
                float span = out_.magnitude;

                // The path's own length, for the derived speed. A straight
                // drift's is the span; a spiral's is several times it.
                float pathLength = span;

                switch (cfg.drift)
                {
                    case ForgeBloom.Drift.Inward:
                    case ForgeBloom.Drift.Staggered:
                        heading = -out_;
                        break;

                    case ForgeBloom.Drift.Outward:
                        heading = out_;
                        break;

                    case ForgeBloom.Drift.Along:
                        heading = aimAxis;
                        break;

                    case ForgeBloom.Drift.Spin:
                        // Counter-clockwise unless the file says otherwise -
                        // the way spin has always turned.
                        _spinClockwise = cfg.Clockwise(cfg.drift);
                        heading = Tangent(out_, _spinClockwise);
                        break;

                    case ForgeBloom.Drift.Spiral:
                        _spiral = ForgeBloom.MakeSpiral(
                            here, centre, cfg.turns,
                            cfg.Clockwise(cfg.drift));
                        _phi = _spiral.phiMax;
                        pathLength = (float)_spiral.length;
                        heading = ForgeBloom.SpiralStartHeading(ref _spiral);
                        break;
                }

                // An explicit driftSpeed is the FIRST staggered bubble's; the
                // later ones are slower by the same factor as their fuse, so
                // the order still holds.
                driftSpeed = (cfg.driftSpeed > 0f)
                    ? cfg.driftSpeed / staggerScale
                    : ((span > 0.001f) ? pathLength / fuse : 0f);

                if (heading.sqrMagnitude > 0.000001f && driftSpeed > 0f)
                {
                    // Through ForgeSteer, never by writing Velocity alone.
                    // idealDirection is the base heading every curve feature
                    // rebuilds from, so publishing it is what lets `wave`,
                    // `homing` and `guided` ride a drifting bloom instead of
                    // silently overwriting it.
                    ForgeSteer.SetHeading(_p, heading.normalized, driftSpeed);
                }
                else
                {
                    heading = Vector2.zero;
                }
            }

            // Start as a point and swell. The art follows the same number, so
            // there is nothing to keep in sync.
            _p.Radius = 0f;

            live = true;

            Paint(0f);
        }

        // Driven from the FixedUpdate prefix, which skips the original - so
        // this owns movement and damage outright and the engine's own swept
        // cast never runs on a bloom. See ForgeBloomPatch for why.
        public void Tick(float dt)
        {
            if (!live || cfg == null || _p == null)
                return;

            float age = Time.time - bornAt;
            float t = Mathf.Clamp01(age / fuse);

            _p.Radius = finalRadius * t;

            Paint(t);

            // SPIN AND SPIRAL ARE THE DRIFTS THAT HAVE TO BE RE-AIMED. The
            // others are a fixed bearing, so publishing once at Shoot is the
            // whole job - but a tangent that is never recomputed is a STRAIGHT
            // LINE leaving the pattern, not a swirl, and "swirls around the
            // centre" is what the word promises. Re-aimed here so it means
            // what it says.
            if (heading != Vector2.zero &&
                cfg.drift == ForgeBloom.Drift.Spin)
            {
                Vector2 out_ = (Vector2)transform.position - centre;

                if (out_.sqrMagnitude > 0.000001f)
                {
                    heading = Tangent(out_, _spinClockwise).normalized;

                    ForgeSteer.SetHeading(_p, heading, driftSpeed);
                }
            }

            // THE SPIRAL IS A POSITION AT EVERY MOMENT, not a heading: aim
            // each step at where the path says the bubble is at the END of it,
            // so it cannot drift off the curve however tight the laps get (a
            // tangent re-aimed per step, like spin's, spirals OUTWARD - at a
            // spiral's inner laps that error is larger than the gap between
            // the arms). Through ForgeSteer.Drive, which does not cede the
            // direction to a wave: a path owns its shot outright.
            if (heading != Vector2.zero &&
                cfg.drift == ForgeBloom.Drift.Spiral)
            {
                float t1 = Mathf.Min(fuse, age + dt);

                Vector2 next = (cfg.pace == ForgeBloom.Pace.Even)
                    ? ForgeBloom.SpiralAtShare(ref _spiral, t1 / fuse)
                    : ForgeBloom.SpiralAt(ref _spiral, driftSpeed * t1, ref _phi);

                Vector2 step = next - (Vector2)transform.position;

                // Arrived and waiting for the pop: stand still on the centre.
                ForgeSteer.Drive(
                    _p, (step.sqrMagnitude > 0.0000001f && dt > 0f)
                        ? step / dt
                        : Vector2.zero);
            }

            // Drift. Read Velocity rather than our own heading, so a wave or a
            // homing steerer that rewrote it this frame is what actually moves
            // the bubble.
            if (heading != Vector2.zero)
            {
                Vector2 v = _p.Velocity;

                if (v.sqrMagnitude > 0.000001f)
                    transform.position += new Vector3(v.x, v.y, 0f) * dt;
            }

            if (cfg.absorb)
                Absorb();

            if (cfg.when == ForgeBloom.When.Swell)
                Strike(_p.Radius, false);

            if (age >= fuse)
                Pop();
        }

        // A quarter turn off `out_`, the way round `clockwise` says. Spin's
        // original heading, (-y, x), is the counter-clockwise one.
        private static Vector2 Tangent(Vector2 out_, bool clockwise)
        {
            return clockwise
                ? new Vector2(out_.y, -out_.x)
                : new Vector2(-out_.y, out_.x);
        }

        // The burst. One damage application over the FULL radius, whatever the
        // swell was doing, then the shot dies.
        private void Pop()
        {
            live = false;

            if (cfg.when == ForgeBloom.When.Pop)
            {
                // A pop hits everything once regardless of what the swell
                // touched - the two modes never both run, but clearing is what
                // makes that a statement rather than an accident.
                _hitOnce.Clear();
                _hitAt.Clear();

                Strike(finalRadius, true);
            }

            if (cfg.digThrough)
                Dig(finalRadius);

            FireEndHooks();

            Destroy(gameObject);
        }

        // ---- damage -------------------------------------------------------
        //
        // Delivered through the game's own IProjectileListener.ProjectileCollided,
        // which is the same call a normal bullet makes. That is what buys kill
        // credit (it is one of only three places RegisterKill is ever reached),
        // the damage-type table, burn on contact, the cell shake and terrain
        // destruction with the stock armour check - none of which is reproduced
        // here.
        // Shared across every bloom, and therefore guarded. Strike is only ever
        // reached from a FixedUpdate-driven Tick or Pop and Unity does not nest
        // those, so a re-entry cannot happen today - but the call inside the
        // loop reaches DamagableResource.Damage, which DOES nest (it can kill,
        // and a death explosion comes back through the damage path before the
        // outer call returns). A shared buffer on a nesting damage path is the
        // shape this repo has been bitten by before; three lines is cheaper
        // than re-deriving that it is safe every time somebody reads this.
        private static bool _striking;

        private void Strike(float radius, bool isPop)
        {
            if (radius <= 0.001f || _striking)
                return;

            _striking = true;

            try
            {
                StrikeInner(radius, isPop);
            }
            finally
            {
                _striking = false;
            }
        }

        private void StrikeInner(float radius, bool isPop)
        {
            Vector2 here = transform.position;

            var filter = new ContactFilter2D();
            filter.useLayerMask = true;
            filter.layerMask = Physics2D.GetLayerCollisionMask(gameObject.layer);
            filter.useTriggers = true;

            _found.Clear();

            if (Physics2D.OverlapCircle(here, radius, filter, _found) <= 0)
                return;

            Collider2D bestCol = null;
            float bestSqr = float.MaxValue;

            for (int i = 0; i < _found.Count; i++)
            {
                Collider2D col = _found[i];

                if (col == null || !Allowed(col))
                    continue;

                // TERRAIN IS NEVER IN THE CONTEST, and leaving it in was very
                // nearly a feature that did nothing. A level segment is one
                // enormous collider, so `bounds.ClosestPoint` returns the
                // bubble's own centre for it - distance ZERO - which beats
                // every real target every time. `pick: "nearest"` would have
                // resolved to "the rock you are standing in" and damaged no
                // enemy at all, in any level, for any radius.
                //
                // Terrain is also not a TARGET: "nearest" is about choosing
                // between things to hit, and digging is something the pop does
                // to its whole area either way. So it is delivered outside the
                // contest, in both modes.
                if (col.gameObject.layer == GroundLayer)
                {
                    Deliver(col, here, isPop, radius);
                    continue;
                }

                if (cfg.pick == ForgeBloom.Pick.Nearest)
                {
                    // CLOSEST TO THE CENTRE, which is a question the engine
                    // cannot answer for us: a zero-length CircleCast returns
                    // ONE start-overlap with no defined ordering. That is the
                    // whole reason this does its own overlap rather than
                    // leaning on the sweep.
                    float sqr = ((Vector2)col.bounds.ClosestPoint(here) - here)
                        .sqrMagnitude;

                    if (sqr < bestSqr && Listener(col) != null)
                    {
                        bestSqr = sqr;
                        bestCol = col;
                    }

                    continue;
                }

                Deliver(col, here, isPop, radius);
            }

            if (cfg.pick == ForgeBloom.Pick.Nearest && bestCol != null)
                Deliver(bestCol, here, isPop, radius);
        }

        // The engine's own friendly-fire test, copied exactly:
        // Projectile.FixedUpdate skips a hit whose rigidbody carries a Unit the
        // Owner is friends with. Doing our own overlap means doing our own
        // version of this - it is not inherited.
        private bool Allowed(Collider2D col)
        {
            Rigidbody2D rb = col.attachedRigidbody;

            if (rb == null || _p.Owner == null)
                return true;

            Unit u;

            if (!rb.TryGetComponent<Unit>(out u) || u == null)
                return true;

            if (ReferenceEquals(u, _p.Owner))
                return false;

            return !_p.Owner.IsFriendsWith(u);
        }

        private static IProjectileListener Listener(Collider2D col)
        {
            var own = col.GetComponents<IProjectileListener>();

            if (own.Length != 0)
                return own[0];

            return col.GetComponentInParent<IProjectileListener>();
        }

        private void Deliver(
            Collider2D col, Vector2 here, bool isPop, float radius)
        {
            if (col == null)
                return;

            Vector2 point = col.bounds.ClosestPoint(here);
            Vector2 away = point - here;

            // THE NORMAL POINTS BACK AT THE BUBBLE, not away from it, and that
            // is not cosmetic. LevelSegmentComponent.CalculateCellPosition is
            //     FloorToInt(point - normal * 0.5 + (0.5, 0.5))
            // so it steps HALF A CELL BACKWARDS along the normal to land inside
            // the cell that was hit - which only works if the normal points out
            // of the surface toward whatever hit it, exactly as a real
            // CircleCast's hit.normal does. Handing it the outward-from-the-
            // bubble direction resolves a cell one step too deep: usually the
            // wrong cell, sometimes one that is not there at all.
            Vector2 normal = (away.sqrMagnitude > 0.000001f)
                ? (-away).normalized
                : Vector2.up;

            IProjectileListener single = Listener(col);

            // TERRAIN IS ONE COLLIDER FOR A WHOLE SEGMENT, so a single hit on
            // it breaks a single cell and the once-per-listener rule then
            // refuses every other cell in the circle. A bloom covering three
            // units of rock would dig one square. Terrain gets its own pass.
            if (single is LevelSegmentProjectileListener)
            {
                StrikeTerrain(single, here, radius);
                return;
            }

            var own = col.GetComponents<IProjectileListener>();

            if (own.Length != 0)
            {
                for (int i = 0; i < own.Length; i++)
                    Hit(own[i], point, normal);
            }
            else if (single != null)
            {
                Hit(single, point, normal);
            }

            if (cfg.push && _p.PushForce > 0f)
            {
                Rigidbody2D rb = col.attachedRigidbody;

                if (rb != null)
                {
                    // The SHOVE goes the other way - outward from the burst -
                    // which is why the two directions are named separately
                    // rather than one being reused for both.
                    rb.AddForceAtPosition(
                        -normal * _p.PushForce, point, ForceMode2D.Impulse);
                }
            }
        }

        private void Hit(
            IProjectileListener listener, Vector2 point, Vector2 normal)
        {
            if (listener == null)
                return;

            // ONCE is the default, and the re-tick is opt-in for the reason
            // this repo has written down twice: a per-frame collision point
            // with no rate limit re-damages every frame of an overlap, which at
            // 50 steps a second is not a weapon, it is a deletion.
            if (cfg.retick > 0f)
            {
                float last;

                if (_hitAt.TryGetValue(listener, out last) &&
                    Time.time - last < cfg.retick)
                {
                    return;
                }

                _hitAt[listener] = Time.time;
            }
            else if (!_hitOnce.Add(listener))
            {
                return;
            }

            listener.ProjectileCollided(_p, point, normal);
        }

        // ---- terrain, the ORDINARY way ------------------------------------
        //
        // One hit per CELL inside the circle, each handed to the game's own
        // listener so the game's own armour check decides whether that cell
        // breaks. Nothing about the threshold is reimplemented here.
        //
        // The point and the normal are built to survive the round trip through
        // CalculateCellPosition, which is
        //     FloorToInt(point - normal * 0.5 + (0.5, 0.5))
        // so a normal pointing from the cell back at the bubble, with the point
        // half a cell out along it, resolves to exactly the cell we meant.
        private void StrikeTerrain(
            IProjectileListener listener, Vector2 here, float radius)
        {
            if (listener == null || radius <= 0.001f)
                return;

            Level level;

            if (!ServiceLocator.TryGet<Level>(out level) || level == null)
            {
                if (!_saidNoLevel)
                {
                    _saidNoLevel = true;

                    Log.LogWarning(
                        "bloom: no Level service, so terrain is hit at a " +
                        "single point rather than across the circle.");
                }

                // Better than nothing: one hit where the bubble is.
                Hit(listener, here + Vector2.up * 0.5f, Vector2.up);
                return;
            }

            int minX = Mathf.FloorToInt(here.x - radius);
            int maxX = Mathf.CeilToInt(here.x + radius);
            int minY = Mathf.FloorToInt(here.y - radius);
            int maxY = Mathf.CeilToInt(here.y + radius);

            float sqr = radius * radius;
            int done = 0;

            for (int x = minX; x <= maxX; x++)
            {
                for (int y = minY; y <= maxY; y++)
                {
                    if (done >= DigCap)
                        return;

                    if (!level.ContainsCell(x, y) || level.IsEmpty(x, y))
                        continue;

                    float dx = x - here.x;
                    float dy = y - here.y;

                    if (dx * dx + dy * dy > sqr)
                        continue;

                    long key = ((long)x << 32) ^ (uint)y;

                    // Once per cell per bloom. A cell that survived the armour
                    // check is not asked again, which is what stops `swell`
                    // from re-testing every rock fifty times a second.
                    if (!_hitCells.Add(key))
                        continue;

                    Vector2 cell = new Vector2(x, y);
                    Vector2 n = here - cell;

                    n = (n.sqrMagnitude > 0.000001f) ? n.normalized : Vector2.up;

                    listener.ProjectileCollided(_p, cell + n * 0.5f, n);

                    done++;
                }
            }
        }

        // ---- terrain, the OVERRIDE ----------------------------------------
        //
        // Only reached with `digThrough`, which skips the armour check the
        // pass above goes through and simply removes the cells.
        private void Dig(float radius)
        {
            Level level;

            if (!ServiceLocator.TryGet<Level>(out level) || level == null)
            {
                if (!_saidNoLevel)
                {
                    _saidNoLevel = true;

                    Log.LogWarning(
                        "bloom digThrough: no Level service, so the pop could " +
                        "not remove terrain outright. Its ordinary damage " +
                        "still digs whatever it is strong enough to break.");
                }

                return;
            }

            Vector2 here = transform.position;

            int minX = Mathf.FloorToInt(here.x - radius);
            int maxX = Mathf.CeilToInt(here.x + radius);
            int minY = Mathf.FloorToInt(here.y - radius);
            int maxY = Mathf.CeilToInt(here.y + radius);

            float sqr = radius * radius;
            int dug = 0;

            for (int x = minX; x <= maxX; x++)
            {
                for (int y = minY; y <= maxY; y++)
                {
                    if (dug >= DigCap)
                    {
                        if (!_saidDigCap)
                        {
                            _saidDigCap = true;

                            Log.LogWarning(
                                "bloom digThrough stopped at " + DigCap +
                                " cells in one pop. Lower projectileRadius, " +
                                "or turn digThrough off and let the damage " +
                                "decide - the armour check is what keeps a " +
                                "big bloom from deleting a room.");
                        }

                        return;
                    }

                    if (!level.ContainsCell(x, y) || level.IsEmpty(x, y))
                        continue;

                    float dx = x - here.x;
                    float dy = y - here.y;

                    if (dx * dx + dy * dy > sqr)
                        continue;

                    level.DestroyCell(x, y, 0);
                    dug++;
                }
            }
        }

        private const int DigCap = 400;

        // ---- absorbing enemy fire -----------------------------------------
        //
        // Enemy projectiles have their Collider2D DISABLED - they resolve hits
        // by raycast - so they are invisible to OverlapCircle at any radius.
        // ForgeProjectileTracker exists precisely for this and is the only way
        // to find them.
        private void Absorb()
        {
            float r = _p.Radius;

            if (r <= 0.05f)
                return;

            _caught.Clear();

            if (ForgeProjectileTracker.CollectNear(
                    transform.position, r, 0, _caught) <= 0)
            {
                return;
            }

            for (int i = 0; i < _caught.Count; i++)
            {
                Component bullet = _caught[i];

                if (bullet == null)
                    continue;

                if (cfg.deflect)
                {
                    // Turning a shot around is three changes, not one - the
                    // velocity, the GameObject's layer AND the private
                    // collision mask baked from it - plus the Owner, or the
                    // kill is credited to nobody. ForgeBulletTurn is the one
                    // owner of all four, and it destroys anything it cannot
                    // fully convert rather than leaving a half-turned bullet.
                    //
                    // MIRROR rather than straight back, because a bubble really
                    // is a convex surface: where on it a shot lands decides
                    // where the shot goes. Catching something dead centre sends
                    // it at whoever fired it; clipping the edge glances it away.
                    ForgeBulletTurn.Turn(
                        bullet, _p.Owner, ForgeBulletTurn.AimMirror,
                        0f, 1f, 1f, Rally, transform.position);
                }
                else
                {
                    Destroy(bullet.gameObject);
                }
            }
        }

        // ---- the end-of-life hooks ----------------------------------------
        //
        // lifetimeData's own spawn/explode/sub/discharge hooks belong to the
        // moment the shot ends, and for a bloom that moment is the POP - but
        // HandleLifetime will never reach them, because we destroy the object
        // first. So they are fired here, in the same order the game fires them.
        private void FireEndHooks()
        {
            try
            {
                // BOTH SETS, because on a bloom the pop is both moments at
                // once - the end of its life AND its impact - and each set is
                // reachable from the builder page on its own.
                //
                // impactBehaviour is the one that would otherwise have been
                // lost, and losing it is the expensive kind of silent: the
                // natural way to say "this shot explodes" is
                // impactBehaviour.spawnExplosion, and a bloom never reaches
                // OnObjectHit, so an author would have set it, seen a clean
                // build log, and got no explosion at all.
                bool wantEffect =
                    (life.enabled && life.spawnOnTimeDown != null) ||
                    (impact.enabled && impact.destroyEffect != null);

                bool wantExplosion =
                    (life.enabled && life.spawnExplosion) ||
                    (impact.enabled && impact.spawnExplosion);

                bool wantSub =
                    (life.enabled && life.fireSub) ||
                    (impact.enabled && impact.fireSub);

                bool wantDischarge =
                    (life.enabled && life.discharge) ||
                    (impact.enabled && impact.discharge);

                if (wantEffect)
                {
                    // Deduped: if both name an effect, the lifetime one wins
                    // rather than two puffs landing on the same spot.
                    GameObject fx =
                        (life.enabled && life.spawnOnTimeDown != null)
                            ? life.spawnOnTimeDown
                            : impact.destroyEffect;

                    if (fx != null)
                        Instantiate(fx, transform.position, transform.rotation);
                }

                // Through the shot's OWN SpawnExplosion, so the kills it makes
                // are credited to this weapon (onKill rewards, rank XP) and
                // not only to you. See ForgeBloomHooks.
                if (wantExplosion)
                    ForgeBloomHooks.Explode(_p, _p, transform.position);

                if (wantSub)
                    FireSub();

                if (wantDischarge)
                {
                    ElectricityManager elec;

                    if (ServiceLocator.TryGet<ElectricityManager>(out elec) &&
                        elec != null)
                    {
                        elec.SpawnDischarge(
                            _p.DischargeData, transform.position);
                    }
                }
            }
            catch (System.Exception e)
            {
                // A cosmetic hook must never cost the pop that already
                // happened - the damage is delivered above this.
                Log.LogWarning("bloom: end-of-life hook failed: " + e.Message);
            }
        }

        // Same shape as ForgeBouncePatch.FireSub, and Vector2.up for the same
        // reason: it is what the game's own FireSub does, because a sub-emitter
        // has no barrel and no heading of its own.
        private void FireSub()
        {
            if (_p.SubEmitters == null)
                return;

            try
            {
                foreach (WeaponBase sub in _p.SubEmitters)
                {
                    if (sub != null)
                    {
                        sub.Fire(new FakeBarrel(transform.position, Vector2.up))
                            .Forget();
                    }
                }
            }
            catch { }
        }

        // The art's world half-extent at its CURRENT scale, which at the moment
        // this is called is the prefab's own. Renderers are walked rather than
        // the root being assumed, because a projectile's sprite is often on a
        // child. Returns 0 when there is nothing drawn to measure, and the
        // caller then leaves the scale alone rather than guessing.
        private float ArtHalfWidth()
        {
            var rs = GetComponentsInChildren<SpriteRenderer>(true);

            float best = 0f;

            for (int i = 0; i < rs.Length; i++)
            {
                SpriteRenderer sr = rs[i];

                if (sr == null || sr.sprite == null)
                    continue;

                // FROM THE SPRITE AND THE TRANSFORM, not from Renderer.bounds.
                // The bounds of a renderer that has never drawn can read as
                // zero, and a zero here would silently leave the art at the
                // prefab's size - which is exactly the bug this measurement
                // exists to prevent, returning in a form nothing would catch.
                // sprite.bounds is pure data (already divided by
                // pixels-per-unit) and is correct the instant the shot exists.
                Vector3 e = sr.sprite.bounds.extents;
                Vector3 s = sr.transform.lossyScale;

                float h = Mathf.Max(
                    Mathf.Abs(e.x * s.x), Mathf.Abs(e.y * s.y));

                if (h > best)
                    best = h;
            }

            return best;
        }

        // Art follows the same number as the hitbox, so the thing you see and
        // the thing that hits cannot disagree.
        private void Paint(float t)
        {
            transform.localScale = _baseScale * Mathf.Max(0.0001f, t);
        }
    }
}
