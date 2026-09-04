using System;
using System.Collections.Generic;
using UnityEngine;

namespace WeaponForge
{
    // SWIPE - a melee swing for the ship.
    //
    // WHY THIS IS A MOD FEATURE AND NOT A CONFIGURATION. **Nothing in this game
    // does contact damage to anything else.** `HealthBase`'s only entry points
    // are projectile / explosion / hitscan / hazard / electricity / cell
    // collision, there is no melee component anywhere, and there is no
    // `OnCollisionStay2D` or `OnTriggerStay2D` in the whole assembly. Flying
    // into an enemy is free. So a blade has to run its own overlap query and
    // deal the damage itself - which also means going through
    // `ForgeKillsPatch.Credit`, because the game credits a kill from exactly
    // three places and none of them is ours.
    //
    // WHAT WAS ALREADY BUILT, and it is most of it:
    //
    //   the sector query   ForgeProjectileTracker.CollectInSector, for catching
    //                      shots in a wedge (the shield's block)
    //   the damage sweep   ForgeShieldController.Ram - overlap, sector filter,
    //                      a per-target repeat delay, kill credit, knockback
    //   the deflect        ForgeBulletTurn.Turn, with four aim modes. MIRROR is
    //                      the right default for a blade: a shot batted by a
    //                      moving edge should leave at the angle it arrived.
    //   generated art      ForgeShieldArt draws its own arc, for the reason
    //                      that applies here too - art generated from the same
    //                      numbers the damage uses can never drift out of sync
    //                      with the hitbox.
    //   terrain cutting    the cell API the gas trail already uses
    //
    // WHAT IS GENUINELY NEW is the swing: an angle that SWEEPS over a duration
    // rather than a facing that is held. Everything else here is arranging
    // parts that existed.
    //
    // ONE HOOK COVERS BOTH SLOT KINDS. `WeaponBase.DoShoot` runs on the Shooter
    // path *and* the gadget path (a gadget calls `weapon.Fire(new FakeBarrel)`,
    // which reaches the same burst loop), and `Equip` is called from both
    // `Shooter.SetWeapon` and `WeaponBasedActiveModule.Activate` so `Owner` is
    // set either way. That is why a swipe works on a gadget key for free, when
    // orbit needed a separate gadget patch.
    public static class ForgeSwipe
    {
        // Which way the next swing goes.
        public const int Alternate = 0;   // left, right, left ... (default)
        public const int Rightward = 1;   // always the same way
        public const int Leftward = 2;
        public const int Random = 3;

        public static int ParseDirection(string s)
        {
            switch ((s ?? "").Trim().ToLowerInvariant())
            {
                case "right":
                case "rightward":
                case "cw":
                case "clockwise":
                    return Rightward;
                case "left":
                case "leftward":
                case "ccw":
                case "counterclockwise":
                    return Leftward;
                case "random":
                    return Random;
                default:
                    return Alternate;
            }
        }

        public static string DirectionWord(int d)
        {
            switch (d)
            {
                case Rightward: return "always rightward";
                case Leftward: return "always leftward";
                case Random: return "a random way each time";
                default: return "alternating";
            }
        }

        public class Config
        {
            // ---- the swing ------------------------------------------------
            //
            // `arc` is the TOTAL sweep in degrees, centred on where you are
            // aiming. 120 is a broad slash; 45 is a jab; 360 is a spin.
            public float arc = 120f;

            // How far the blade reaches, in world units. The ship is about 1
            // unit across, so 3.5 is a comfortable arm's length.
            public float reach = 3.5f;

            // Radial width of the blade. Only the ART uses this - the hit test
            // is "inside reach and inside the swept angle" - so it is honest
            // about the fact that a thicker-looking blade does not hit further.
            public float thickness = 0.7f;

            // Seconds the swing takes. Short is snappy; long is a wind-up you
            // can see coming, which matters because the sweep only hits what
            // it passes THROUGH.
            public float duration = 0.18f;

            // Above 1 the blade accelerates through the middle of the swing and
            // eases at both ends, which is what makes a swing read as a swing
            // rather than a rotating stick.
            public float ease = 2f;

            public int direction = Alternate;

            // ---- what it does on contact ---------------------------------
            public float damage;              // 0 = the weapon's own damage
            public bool hasDamage;

            public Resource damageType;       // null = the weapon's own

            // An impulse along the swing. Pairs with the `slam` block, which is
            // the interesting combination: bat them into a wall and the wall
            // does the real damage.
            public float knockback;

            // Mark what it touches slammable even without shoving it, so
            // something else can do the throwing. Free, because `slam` is a
            // standalone property now.
            public bool marksSlammable;

            // ---- terrain -------------------------------------------------
            //
            // Cuts cells the blade passes through, gated by the same hardness
            // test explosions use - so a swipe carves Slime and bounces off
            // Solid unless it is strong enough.
            public float cutTerrain;
            public float igniteTerrain;

            // ---- deflecting ----------------------------------------------
            public bool deflects = true;
            public int aimMode = ForgeBulletTurn.AimMirror;
            public float deflectSpeed = 1f;
            public float deflectDamage = 1f;
            public int maxDeflect = 8;

            // Rally rides along, so a batted shot can be buffed each time -
            // exactly the loop the shield's rally already does, and a blade is
            // a better fit for it than a held arc.
            [NonSerialized] public ForgeRally.Config rally;

            // ---- art -----------------------------------------------------
            //
            // Empty means the generated crescent, which is drawn from `arc`,
            // `reach` and `thickness` so it always matches the swing. A named
            // sprite is placed at the blade instead - see ForgeSwipeController.
            public string sprite = "";
            public Color color = new Color(0.85f, 0.95f, 1f, 0.9f);
            public bool hasColor;

            // How much of the arc the drawn crescent covers, as a fraction of
            // `arc`. Less than 1 reads as a trailing slash rather than a solid
            // fan.
            public float trail = 0.55f;

            public float fadeOut = 0.12f;

            [NonSerialized] public Sprite art;
            [NonSerialized] public bool artTried;

            // Opt-in animation - see ForgeSpriteLibrary.OptInFrames. Resolved
            // in the same one-shot Art() pass that sets `art`, so it is covered
            // by `artTried` and never re-resolved per swing.
            [NonSerialized] public Sprite[] artFrames;
            [NonSerialized] public float artFps;

            public string sfx = "";

            public bool DoesAnything
            {
                get
                {
                    return hasDamage || knockback != 0f || marksSlammable ||
                           cutTerrain > 0f || igniteTerrain > 0f || deflects;
                }
            }

            public float Half { get { return Mathf.Clamp(arc, 1f, 360f) * 0.5f; } }
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
            return weapon != null && _weapons.TryGetValue(weapon, out config);
        }

        public static bool Any { get { return _weapons.Count > 0; } }
    }
}
