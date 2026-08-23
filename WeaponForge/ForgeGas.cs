using UnityEngine;

namespace WeaponForge
{
    // Lingering GAS. A shot that leaves a cloud behind, and the cloud is what
    // does the work: burning, poisoning, bogging things down, shoving them
    // around, or putting fires out.
    //
    // Why none of this could be taken from the game, having looked:
    //
    // 1. PUNK has TWO unrelated burn systems. A weapon's `burn` raises a
    //    UNIT's burn level (that one hurts things), and separately any shot
    //    that hits terrain raises that CELL's burn level. But nothing reads a
    //    burning cell except the flame particles and terrain destruction -
    //    **standing in burning ground does nothing to anyone.**
    // 2. The engine that WOULD do it exists and has never been switched on.
    //    `CellType.cellBehaviours` accepts BurnCellBehaviour /
    //    DamageCellBehaviour / DragCellBehaviour, read off a poller that knows
    //    which cell you are standing in - and **not one CellType in the game
    //    has a single behaviour on it.** Worse, the burn and damage readers
    //    exist only on the four SHIP prefabs, so a cell-based gas would poison
    //    the player and nothing else.
    // 3. `cellConvertData` cannot help either: Level.ConvertCells only rewrites
    //    cells already in its convertable list, so it can scorch ground but can
    //    never fill empty air. No floating cloud.
    //
    // So the cloud is the mod's own object. What IS worth copying is
    // `Weapon Caps Flame`, the game's flamethrower: lobbed puffs at mass 0.1
    // with linearDamping 1.7 - light and heavily damped, which is what makes a
    // puff stall in the air instead of flying away - carrying `burn: 3` and
    // only 0.5 damage. The payload is the burn, not the hit.
    public static class ForgeGas
    {
        // What the cloud does once it is there.
        public enum Cloud
        {
            Grow,   // stall, swell, thin out - reads most like real gas
            Hang,   // stall at one size and sit there
            Drift,  // keeps rolling slowly across the room
            Single  // one big cloud, on IMPACT only - a gas grenade
        }

        // How a TRAIL decides where to drop its next cloud.
        public const int TrailDistance = 0;  // every N world units (default)
        public const int TrailTime = 1;      // every N seconds
        public const int TrailBoth = 2;      // whichever comes first

        public class Config
        {
            public Cloud cloud = Cloud.Grow;

            // ---- shape and life -----------------------------------------
            public float radius = 1.6f;      // starting radius
            public float growTo = 3.2f;      // Grow only: radius at the end
            public float growTime = 1.2f;    // seconds radius -> growTo
            public float duration = 4f;      // seconds the cloud lives
            public float fade = 0.6f;        // seconds of fade-out at the end
            public float drift = 1.2f;       // Drift only: units/second
            public float driftDamping = 0.4f;// Drift only: how fast it settles

            // Total clouds this weapon may have alive at once. A spray weapon
            // firing 28 puffs a second will otherwise carpet the level.
            public int max = 24;

            // ---- payload (any combination) ------------------------------
            // The game's own fire. Cheap, stacks with everything, and shows
            // the burning visual for free - but its damage is FLAT (1/sec
            // while alight, no matter how high the level goes), so this buys
            // duration rather than intensity.
            public float burnPerSecond;

            // The cloud's own damage. Works on anything, including the
            // fireproof Tablet enemy, and is the only payload that scales.
            public float damagePerSecond;

            // Fraction of velocity bled off per second, 0-1. The game has a
            // DragCellBehaviour for exactly this and never used it.
            public float slow;

            // Outward shove per second. Negative pulls inward.
            public float push;

            // ---- putting fires out --------------------------------------
            // Burn level removed per second from units inside. Enough of this
            // and the cloud is a fire extinguisher rather than a weapon -
            // it can save you as easily as it hurts them.
            public float extinguish;

            // Burn level removed per second from burning TERRAIN inside. This
            // is the separate cell-fire system, and it is the only thing in
            // the mod that touches it.
            //
            // CALIBRATION MATTERS HERE and 6 was far too small. Cell fire
            // thresholds are NOT normalised: CellType_Solid - the main terrain
            // - ignites at **45**, while Slime is 1, Tuff 1.3, Moss 2.5,
            // Night 5, Bark and Gold 10. A burning Solid cell therefore sits
            // above 45 and is being topped up by every burning neighbour, so
            // 6/second could never win. 120 clears it in well under a second.
            public float extinguishTerrain;

            // The inverse: burn level ADDED per second to terrain inside the
            // cloud. This is how a gas cloud sets the ground alight without
            // having to physically touch it - the game only ignites terrain
            // when a projectile collides with it, which is no use to a cloud
            // that is meant to hang in the air.
            public float igniteTerrain;

            // ---- the puffs that make the cloud -------------------------
            // These reach past the cloud and change the PROJECTILE, because
            // "the gas all falls down and dies on the floor" is a property of
            // the puff, not of the cloud.
            //
            // -999 means "leave the template alone".

            // Rigidbody gravityScale on each puff. 0 makes gas that hangs and
            // spreads instead of pooling on the ground.
            public float puffGravity = -999f;

            // Rigidbody linearDamping on each puff. The game's flamethrower
            // uses 1.7, which is what makes a puff stall rather than fly; lower
            // it to throw the gas further before it settles.
            public float puffDrag = -999f;

            // Stop terrain eating the puffs. Their impact behaviour is switched
            // off, so they drift past walls and floors instead of being
            // destroyed on contact - which is what lets a big cloud build up.
            // Pair with igniteTerrain if you still want the ground to burn.
            public bool puffPierceTerrain;

            // ---- who it touches -----------------------------------------
            public bool hitEnemies = true;

            // Off by default, same call as the gravity well's pullPlayer: a
            // weapon that can kill its own user should be a decision.
            public bool hitPlayer;

            // Seconds between applications. Damage calls drive hit flashes,
            // sounds and the aggro event, so this is not free per frame.
            public float tickRate = 0.25f;

            // ---- the TRAIL ----------------------------------------------
            //
            // Gas laid along the flight path instead of only where the shot
            // ends: a napalm line, an acid stripe, a smoke screen to retreat
            // through.
            //
            // This is deliberately NOT a fifth `cloud` mode. `cloud` says how a
            // cloud BEHAVES once it exists (stall, swell, roll); the trail says
            // WHERE clouds get placed. They are orthogonal - a trail of drifting
            // clouds and a trail of hanging ones are both sensible - so a trail
            // keeps whatever `cloud` mode the weapon already asked for.
            //
            // And it is not built on `trail` (the visual key) because it cannot
            // be: our trails are a child ParticleSystem emitting by
            // rateOverDistance, and particles are not colliders, so there is
            // nothing there to damage with. The cloud already does the hard half.
            public bool trail;

            public int trailMode = TrailDistance;

            // Distance mode: world units between clouds. Constant spacing at
            // any speed, which is what makes a fast shot and a slow one leave
            // the same looking line.
            public float trailEvery = 1.2f;

            // Time mode: seconds between clouds. In Both mode this is a FLOOR,
            // so a stalled or slowed shot still leaks instead of stopping.
            public float trailInterval = 0.15f;

            // A trail cloud is its own thing, with its own numbers, because
            // twenty-five full-size clouds would fill the room. It also gets its
            // own budget, so a long trail can never starve the impact cloud.
            public float trailRadius = 0.9f;
            public float trailDuration = 2f;
            public int trailPuffs = 2;
            public int trailMax = 40;

            // Whether the shot still leaves a cloud where it DIES. On by
            // default (the napalm-shell reading: a line, then a bloom); false
            // makes it a pure trail. Worth having on its own - until now there
            // was no way to suppress the end cloud at all.
            public bool bloomOnDeath = true;

            // Built at load time: the derived Config that trail clouds actually
            // use. A separate instance rather than overrides threaded through
            // ForgeGasCloud, which keeps proven code untouched AND gives the
            // trail its own entry in the cloud budget (ForgeGasCloud.CountFor
            // groups by config identity).
            [System.NonSerialized] public Config trailCfg;

            // ---- look ----------------------------------------------------
            public Color color = new Color(0.55f, 0.95f, 0.4f, 0.5f);
            public string sprite = "";
            public string sfx = "";

            // Puffs drawn per cloud. More reads as thicker gas; each is a
            // sprite, so this is the main cost knob.
            public int puffs = 5;

            // How much the puffs churn, in degrees/second.
            public float swirl = 25f;

            // ---- resolved at build time ---------------------------------
            [System.NonSerialized] public Sprite art;
            [System.NonSerialized] public Material material;
            [System.NonSerialized] public int sortingLayerId;
            [System.NonSerialized] public int sortingOrder;
            [System.NonSerialized] public string sfxGuid;

            public bool DoesAnything
            {
                get
                {
                    return burnPerSecond > 0f || damagePerSecond > 0f ||
                           slow > 0f || push != 0f ||
                           extinguish > 0f || extinguishTerrain > 0f ||
                           igniteTerrain > 0f;
                }
            }

            // Single is a gas grenade: it only blooms where the shot lands, so
            // a puff that simply times out in mid-air leaves nothing.
            public bool ImpactOnly
            {
                get { return cloud == Cloud.Single; }
            }

            public float MaxRadius
            {
                get
                {
                    return cloud == Cloud.Grow
                        ? Mathf.Max(radius, growTo)
                        : radius;
                }
            }

            // The config a trail's clouds use: this one, shrunk, with its own
            // budget and NO sound.
            //
            // Silencing it is not a detail. A trail drops a cloud every metre,
            // so inheriting `sfx` would fire the gas sound twenty times a second
            // - and ShipLog aside, AudioManager has no rate limit of its own.
            public Config DeriveTrail()
            {
                var t = (Config)MemberwiseClone();

                // Single means "one big cloud where it lands", which cannot
                // describe a trail. Everything else carries over: a trail of
                // Drift clouds rolls outward from the line, a trail of Grow
                // clouds thickens behind you.
                if (t.cloud == Cloud.Single)
                    t.cloud = Cloud.Grow;

                // Keep the weapon's own grow PROPORTION rather than its
                // absolute growTo, so a trail cloud swells by the same ratio
                // the main cloud does.
                float ratio = (radius > 0.01f) ? (growTo / radius) : 2f;

                t.radius = Mathf.Max(0.1f, trailRadius);
                t.growTo = Mathf.Max(t.radius, t.radius * ratio);
                t.duration = Mathf.Max(0.1f, trailDuration);
                t.fade = Mathf.Min(fade, t.duration * 0.5f);
                t.puffs = Mathf.Clamp(trailPuffs, 1, 24);
                t.max = Mathf.Max(1, trailMax);

                // A trail never spawns its own trail.
                t.trail = false;
                t.trailCfg = null;

                t.sfx = "";
                t.sfxGuid = null;

                return t;
            }
        }
    }
}
