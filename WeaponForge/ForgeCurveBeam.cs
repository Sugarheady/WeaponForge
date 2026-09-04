using System;
using System.Collections.Generic;
using UnityEngine;

namespace WeaponForge
{
    // THE CURVE BEAM - a hitscan beam that locks onto one unit, bends its way
    // there around terrain, and damages everything it threads through on the
    // way.
    //
    // WHY THE STOCK BEAM CANNOT DO ANY OF THIS, in three facts:
    //
    //   1. THE BEAM IS ONE STRETCHED QUAD. HitscanWeaponVisual.UpdateVisual
    //      puts the root at `start`, rotates it to face `end`, and writes ONE
    //      SpriteRenderer's size.x to the whole distance. There is nowhere for
    //      a curve to live, so a bent beam has to be a CHAIN of short quads.
    //
    //   2. A STOCK BEAM HITS EXACTLY ONE THING. HitscanWeapon.FireSingle does a
    //      single CircleCast and damages `hit.collider` - the first thing in
    //      the way - and nothing else. So "pierces the enemies it passes
    //      through" is not a flag anywhere; it is a cast per segment.
    //
    //   3. DAMAGE AND VISUALS WERE TWO INDEPENDENT RAYCASTS. FireSingle casts
    //      to decide what to hurt; OnBarrelMoved casts AGAIN to decide where to
    //      draw. Bending only one of them leaves a beam that looks perfect and
    //      damages a straight line.
    //
    // (3) IS SOLVED BY CONSTRUCTION HERE, and it is worth knowing why. Look at
    // Shooter.Update: it calls `weapon.OnBarrelMoved(...)` EVERY FRAME and
    // unconditionally, and only afterwards sets IsTriggerPulled and gets as far
    // as Shoot() -> DoShoot -> FireSingle. So within one frame the drawing pass
    // always runs BEFORE the firing pass. The path is therefore computed once
    // in OnBarrelMoved and both consumers read that same cached path. There is
    // only ever one path to bend, so the two can no longer disagree.
    //
    // (A gadget-fired beam never goes through Shooter at all - a gadget calls
    // weapon.Fire(new FakeBarrel(...)) directly - so the firing pass rebuilds a
    // stale path itself. ForgeCurveBeamState.PathFor handles both.)
    //
    // THE CONFIG NEVER RIDES A PREFAB, which is deliberate: a Config reference
    // on a prefab-attached component arrives NULL on every clone, and that one
    // misconception silently killed the whole gas and catalyst blocks once. The
    // registry below is keyed by WeaponData and lives in the mod; the chain
    // renderer is handed its config by the patch every frame, and it is a live
    // scene object rather than a prefab, so there is nothing to serialize.
    public static class ForgeCurveBeam
    {
        // ---- how hard it works to get around terrain --------------------
        //
        // All three share ONE integrator. The router only ever changes what the
        // steering is AIMING at from step to step: at the target directly, or
        // at the next waypoint of a routed corridor. That is why offering all
        // three costs so little - the chain renderer and the damage pass have
        // no idea which one produced the points they are given.
        public const int RouteSteer = 0;          // default
        public const int RouteSteerThenPath = 1;
        public const int RoutePath = 2;

        public static int ParseRoute(string s)
        {
            switch ((s ?? "").Trim().ToLowerInvariant())
            {
                case "steerthenpath":
                case "steer-then-path":
                case "hybrid":
                case "fallback":
                    return RouteSteerThenPath;
                case "path":
                case "pathfind":
                case "route":
                case "astar":
                    return RoutePath;
                default:
                    return RouteSteer;
            }
        }

        public static string RouteWord(int r)
        {
            switch (r)
            {
                case RouteSteerThenPath:
                    return "steer, and pathfind when stuck";
                case RoutePath:
                    return "always pathfind";
                default:
                    return "steer around it";
            }
        }

        // ---- what it locks onto -----------------------------------------
        public const int LockNearest = 0;   // default - closest legitimate unit
        public const int LockForward = 1;   // smallest angle off the aim
        public const int LockNone = 2;      // no lock; it snakes where you point

        public static int ParseLock(string s)
        {
            switch ((s ?? "").Trim().ToLowerInvariant())
            {
                case "forward":
                case "aim":
                case "aimed":
                case "cone":
                    return LockForward;
                case "none":
                case "off":
                case "no":
                case "free":
                    return LockNone;
                default:
                    return LockNearest;
            }
        }

        public static string LockWord(int l)
        {
            switch (l)
            {
                case LockForward: return "the target nearest your aim";
                case LockNone: return "nothing - it snakes where you point";
                default: return "the nearest target";
            }
        }

        public class Config
        {
            public bool enabled = true;

            // ---- the lock -------------------------------------------------
            public int lockMode = LockNearest;

            // 0 = the weapon's own Range. How far away it will look for
            // something to lock onto.
            public float lockRange;

            // Degrees either side of your aim that a target may be in, for
            // "forward". Ignored by "nearest", which looks all around.
            public float lockCone = 60f;

            // Crates and boxes carry AimAssistTarget exactly like enemies do
            // and are on the same faction, so nothing excludes them by
            // default. Off here, because locking a seeking beam onto a supply
            // crate while something is shooting you is never what was wanted.
            public bool objectsToo;

            // Hold the lock while the trigger is down. Off re-picks every
            // frame, which whips the beam between enemies in a crowd - fun,
            // but not what "locks onto a single unit" means.
            public bool holdLock = true;

            // Pick a new one when the locked target dies mid-burst. Off lets
            // the beam fall straight until you release.
            public bool reacquire = true;

            // ---- the curve ------------------------------------------------
            //
            // Degrees the beam may turn PER WORLD UNIT of its own length -
            // not per second. That is the honest unit for a shape rather than
            // a motion: it makes the tightest circle the beam can draw
            // (360 / turnRate units of circumference) independent of frame
            // rate, of how fast you are moving, and of the weapon's fire rate.
            //
            // At 40 the tightest loop is about 9 units around; at 120 it is
            // about 3, which is what makes a beam curl back on itself in a
            // small room.
            public float turnRate = 40f;

            // Length of one link, in world units. Shorter is a smoother curve
            // and more quads; the segment count is capped, so making this very
            // small shortens the beam rather than costing frames.
            public float segment = 0.4f;

            // 0 = the weapon's own Range. This is the length of the BEAM, not
            // the distance to the target - a beam that loops travels much
            // further than the gap it crosses, so a curve beam usually wants
            // more length than a straight one of the same reach.
            public float maxLength;

            // How much of last frame's heading is kept. The path is rebuilt
            // from scratch every frame, so with no smoothing a target stepping
            // sideways snaps the whole curve at once. This is what makes it
            // read as a heavy tether being dragged rather than a rubber band.
            public float smoothing = 0.35f;

            // ---- weaving through other enemies ----------------------------
            //
            // His request: "once the beam locks onto the enemy, the segments
            // going to that enemy also looks for other enemies that it can hit
            // along or near it's path. Like seeking behavior."
            //
            // Off by default, because it is a real damage increase on top of
            // piercing - and because a beam that snaps straight at its target
            // is a different, more precise weapon.
            //
            // IMPLEMENTED AS A ROUTER, not as a steering force, which is what
            // makes it cheap. The integrator already follows a corridor of
            // waypoints for the pathfinder; weaving just supplies a different
            // corridor - the enemies worth collecting on the way - ending at
            // the locked target. So the turn limit, the terrain avoidance and
            // the damage pass all work exactly as they already did, and none
            // of them needed to learn about this.
            public bool weave;

            // How far off the straight line to the target an enemy may be and
            // still be worth a detour. This is the greed dial: small keeps it
            // near the direct line, large sends it wandering.
            public float weaveRange = 4f;

            // Most detours in one beam. Without a cap a crowded room turns the
            // beam into a scribble that never reaches what you locked.
            public int weaveMax = 3;

            // ---- terrain --------------------------------------------------
            public int route = RouteSteer;

            // How far ahead it looks for something to slide around.
            public float lookAhead = 1.4f;

            // How wide a berth it gives terrain. Also the radius the router
            // uses to decide a gap is passable at all.
            public float clearance = 0.45f;

            // ---- damage ---------------------------------------------------
            //
            // A stock beam damages the FIRST thing it touches. Piercing is
            // the whole point here, so it defaults on - and it is a real
            // increase, not a small one: at the Caps Laser's fireRate 13 and
            // damageRepeatDelay 0.08 a beam threading six enemies does about
            // six times the damage of the stock one.
            public bool pierce = true;

            // Multiplier on anything that is NOT the locked target. 1 = the
            // beam hits everything equally.
            public float bystander = 1f;

            // 0 = no cap. Most units hurt per firing tick.
            public int maxTargets;

            // Pushes along the beam at the point it touches, like the stock
            // one does. 0 = the weapon's own pushForce.
            public float push;

            // ---- art ------------------------------------------------------
            //
            // Empty = generated links, drawn from `thickness` and `segment` so
            // the picture cannot drift away from the beam it is describing.
            // A NAMED ANIMATION is used frame by frame ALONG the chain, which
            // is the one thing the plain `beam` block cannot do: it warns that
            // only frame 0 of an animation is usable, because one stretched
            // quad has no flipbook. A chain has a frame per link.
            public string sprite = "";

            // How many links the generated pattern cycles through.
            public int frames = 4;

            // Patterns per second travelling down the chain. Negative runs the
            // pattern back toward the ship.
            public float flow = 6f;

            public float thickness = 0.35f;

            // Fraction of a link's length that overlaps the next one. Quads
            // meeting at an angle leave a wedge-shaped gap on the outside of
            // every bend; a little overlap is what makes the beam read as
            // connected the whole way through.
            public float overlap = 0.35f;

            // Thins the beam toward the far end. 0 = an even beam.
            public float taper;

            public Color color = Color.white;
            public bool hasColor;

            // The puff at the end. HitscanWeaponVisual already carries an
            // impactParticle that UpdateVisual parks at the beam's endpoint -
            // this just re-parks it at the end of the CURVE instead.
            public bool endPuff = true;

            // A real explosion where the beam lands, using the weapon's own
            // Explosion. Rate-limited, because a held beam fires many times a
            // second and the stock explosion is not priced for that.
            public bool explodeOnTarget;
            public float explodeInterval = 0.25f;

            [NonSerialized] public Sprite[] art;
            [NonSerialized] public bool artTried;

            // Cheap accessors, clamped once so the rest of the code can stop
            // worrying about a file that says segment 0.
            public float Segment { get { return Mathf.Clamp(segment, 0.1f, 4f); } }
            public float Turn { get { return Mathf.Clamp(turnRate, 0f, 720f); } }
            public float Thickness { get { return Mathf.Clamp(thickness, 0.02f, 8f); } }
            public int Frames { get { return Mathf.Clamp(frames, 1, 16); } }
        }

        private static readonly Dictionary<WeaponData, Config> _weapons =
            new Dictionary<WeaponData, Config>();

        public static void Register(WeaponData weapon, Config config)
        {
            if (weapon != null && config != null)
                _weapons[weapon] = config;
        }

        public static bool TryGet(WeaponData weapon, out Config config)
        {
            config = null;

            if (weapon == null || !_weapons.TryGetValue(weapon, out config))
                return false;

            return config != null && config.enabled;
        }

        // Used by ForgeImpactPatch to stand down: the impact sound normally
        // re-casts the straight ray to find out where to play, which for a
        // curve beam would put the sound wherever the beam WOULD have gone.
        // The curve beam plays it at the end of the actual curve instead.
        public static bool Owns(WeaponData weapon)
        {
            Config ignored;
            return TryGet(weapon, out ignored);
        }

        public static bool Any { get { return _weapons.Count > 0; } }
    }
}
