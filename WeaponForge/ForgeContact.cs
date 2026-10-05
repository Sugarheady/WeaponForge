using System;
using System.Collections.Generic;
using UnityEngine;

namespace WeaponForge
{
    // CONTACT DAMAGE - making a unit dangerous to touch.
    //
    // I HAD THIS WRONG, and he was right. I have written "nothing in this game
    // does contact damage" into three files. The narrower truth is that the
    // PLAYER'S SHIP does not - `ImpactDamage` on the ship is self-damage and is
    // only enabled while sprinting. The game has TWO contact-damage systems:
    //
    //   Hazard        a plain MonoBehaviour with OnCollisionEnter2D and
    //                 OnTriggerEnter2D that damages anything implementing
    //                 IHazardSensor - which HealthBase does - plus a
    //                 `contactPushbackForce`. TWELVE of them: Bouncer Red
    //                 (2 dmg / 30 push), Bouncer Worm (5/30), Fly Alfa and Fly
    //                 Zapper (0 damage, 30 push - pure bumpers), Turret
    //                 Bouncer (5/30), Fruit White (5/30), Fish (3/20), the
    //                 four beak enemies, and Ammo Dandelion.
    //
    //   ChargerHead   Larva, Child, Maggot, Swimmer Maggot. Ignores collision
    //                 callbacks entirely: a swept CircleCast along the
    //                 rigidbody's velocity every FixedUpdate, rate-limited by
    //                 `minDamageDelay`, pushing the victim by `pushForce` and
    //                 recoiling itself by `pushBackForce`. The Hazard on the
    //                 beak is used purely as a damage container, which is why
    //                 that GameObject has no collider at all.
    //
    // WHY THE SWEPT CAST IS THE DEFAULT HERE, and it is the whole reason this
    // feature is worth building rather than reusing `slam`:
    //
    //   **All 65 unit rigidbodies use DISCRETE collision detection** - only
    //   ammo prefabs use Continuous - and the fixed timestep is 0.02s. So a
    //   body moving faster than about 50 units/s skips a 1-unit object between
    //   physics steps. Riot Wave's pushForce 220 on a common Cross (mass 5) is
    //   44 u/s, which is marginal; add `slow`'s knockback, which HALVES the
    //   mass, and it is 88 u/s - 1.76 units per step, further than an enemy is
    //   wide. Those are exactly the "knocks them super far out" hits he wanted
    //   to matter, and a collision callback is the one thing guaranteed to
    //   miss them.
    //
    //   ChargerHead is the game's own answer to this, and copying it costs
    //   nothing.
    //
    // NOTE `slam` is deliberately LEFT ALONE - his call. It still detects
    // arrival with OnCollisionEnter2D by default. `slam.swept` opts into the
    // same cast so the two can be compared without changing what already
    // shipped.
    public static class ForgeContact
    {
        // ---- who carries it ----------------------------------------------
        public const int WhoMarked = 0;    // default: units this weapon touched
        public const int WhoEnemies = 1;   // every enemy, always
        public const int WhoShip = 2;      // your own ship - ram anything
        public const int WhoAll = 3;       // enemies and the ship

        public static int ParseWho(string s)
        {
            switch ((s ?? "").Trim().ToLowerInvariant())
            {
                case "enemies":
                case "everyone":
                case "units":
                    return WhoEnemies;
                case "ship":
                case "me":
                case "player":
                case "self":
                    return WhoShip;
                case "all":
                case "both":
                    return WhoAll;
                default:
                    return WhoMarked;
            }
        }

        public static string WhoWord(int w)
        {
            switch (w)
            {
                case WhoEnemies: return "every enemy, always";
                case WhoShip: return "your ship";
                case WhoAll: return "every enemy AND your ship";
                default: return "units this weapon has touched";
            }
        }

        // ---- how contact is detected --------------------------------------
        public const int DetectSwept = 0;      // default - ChargerHead's way
        public const int DetectCollision = 1;  // the stock Hazard's way

        public static int ParseDetect(string s)
        {
            switch ((s ?? "").Trim().ToLowerInvariant())
            {
                case "collision":
                case "collide":
                case "callback":
                case "hazard":
                    return DetectCollision;
                default:
                    return DetectSwept;
            }
        }

        public static string DetectWord(int d)
        {
            return d == DetectCollision
                ? "collision callbacks (the stock Hazard's way)"
                : "a swept cast (ChargerHead's way - survives high speed)";
        }

        public class Config
        {
            public bool enabled = true;

            public int who = WhoMarked;

            // How long a marked unit stays dangerous. Ignored by the other
            // modes, which are permanent.
            public float window = 1.5f;

            // ---- damage ---------------------------------------------------
            //
            // FLAT by default, matching every stock Hazard - the Maggot's beak
            // is a flat 3. Predictable, and it does not overlap with what
            // `slam` already does.
            public float damage = 3f;
            public Resource damageType;

            public bool scaleBySpeed;
            public float damagePerSpeed = 1f;
            public float minSpeed = 6f;
            public float maxDamage = 999f;

            // ---- shoving --------------------------------------------------
            public float push;      // the victim, along the direction of travel
            public float recoil;    // and the carrier, backwards

            // Per target, so one unit ploughing through a crowd hits each of
            // them once rather than the first one repeatedly. The stock
            // ChargerHeads use 0.3 and 0.5.
            public float repeatDelay = 0.3f;

            public int detect = DetectSwept;

            // 0 = taken from the unit's own collider, which is almost always
            // what is wanted and means one setting fits a Larva and a Queen.
            public float radius;

            public bool hitsEnemies = true;
            public bool hitsPlayer;

            // ---- the tell -------------------------------------------------
            //
            // Same owner as the slam tint, so the two compose instead of
            // fighting. See ForgeUnitTint.
            public Color tint = Color.white;
            public bool hasTint;
            public float tintPulse;

            // HOW HARD THE TINT COMES THROUGH, 0..1. 1 is the full colour and
            // is what this has always done; 0.35 is a hint of it. Read from
            // either "tintStrength" or "colorStrength" so the two naming
            // styles in this mod's blocks both work. See ForgeUnitTint.
            public float tintStrength = 1f;

            // multiply (the default, and what every existing file was tuned
            // against) or replace, which moves the sprite TOWARD the colour so
            // it can brighten a dark enemy. See ForgeUnitTint.
            public int tintMode = ForgeUnitTint.Multiply;

            public bool DoesAnything
            {
                get
                {
                    return damage > 0f || push != 0f || recoil != 0f ||
                           scaleBySpeed;
                }
            }
        }

        private static readonly Dictionary<WeaponData, Config> _weapons =
            new Dictionary<WeaponData, Config>();

        // How many weapon FILES declare an always-on mode. Only decides whether
        // the sweeper needs to exist at all - never whether it should act.
        private static int _worldOwners;

        // ------------------------------------------------------------------
        // WHAT IS ACTUALLY EQUIPPED.
        //
        // THE BUG THIS EXISTS TO FIX, reported in round 11: *"I restarted the
        // game and loaded Kamikaze swarm. When i Dash into enemies they take
        // damage now and die. That shouldn't be the case."* Correct, and
        // Kamikaze Swarm has no `contactDamage` block at all.
        //
        // `Register` runs at BUILD time, once per weapon file at startup, and
        // it used to latch a single static `_world` and start the sweeper right
        // there. So the moment ANY file in the folder carried
        // `"contactDamage": { "who": "ship" }`, every unit in every run was
        // armed for the rest of the session - whatever the player had equipped,
        // and even for weapons that were never taken out of the folder. A
        // gameplay rule was being decided by the presence of a FILE.
        //
        // The `who: "marked"` route never had this problem, because it asks
        // `TryGet(source.TemplateData)` about the weapon that actually dealt the
        // damage. Only the world modes were unscoped, which is why this fix is
        // narrow: it gives them the same "which weapon is this?" discipline the
        // marked route always had.
        //
        // Tracked through `WeaponBase.Equip` / `Dispose` rather than by walking
        // the module grid every tick, because those two are the ONLY places a
        // weapon is equipped or torn down (Shooter.SetWeapon for the primary and
        // secondary, WeaponBasedActiveModule.Activate for a gadget) - so one
        // pair of patches covers both paths, including gadgets, at no per-frame
        // cost. `Live` still re-validates against `Owner`, so a weapon that goes
        // away without a Dispose cannot leave the rule stuck on.
        // ------------------------------------------------------------------
        private static readonly List<WeaponBase> _equipped =
            new List<WeaponBase>();

        public static void Equipped(WeaponBase weapon)
        {
            if (weapon == null || _worldOwners == 0)
                return;

            if (!_equipped.Contains(weapon))
                _equipped.Add(weapon);
        }

        public static void Unequipped(WeaponBase weapon)
        {
            if (weapon == null)
                return;

            _equipped.Remove(weapon);
        }

        // Cleared at every run entry alongside the other per-run state.
        public static void Reset()
        {
            _equipped.Clear();
        }

        // The world rule IF something carrying one is really in the loadout,
        // else null. Null is the signal for "put the game back how it was".
        public static Config Live
        {
            get
            {
                if (_worldOwners == 0)
                    return null;

                for (int i = _equipped.Count - 1; i >= 0; i--)
                {
                    WeaponBase w = _equipped[i];

                    // A torn-down weapon, or one whose owner has been
                    // destroyed. `Owner` is a Unit, and a destroyed Unity
                    // object compares equal to null.
                    if (w == null || w.Owner == null)
                    {
                        _equipped.RemoveAt(i);
                        continue;
                    }

                    Config c;

                    if (w.TemplateData != null &&
                        _weapons.TryGetValue(w.TemplateData, out c) &&
                        c != null && c.enabled && c.who != WhoMarked)
                    {
                        return c;
                    }
                }

                return null;
            }
        }

        public static void Register(WeaponData weapon, Config config)
        {
            if (weapon == null || config == null)
                return;

            _weapons[weapon] = config;

            if (config.who == WhoMarked)
                return;

            _worldOwners++;

            // The sweeper may exist from startup; it simply does nothing until
            // `Live` says a world-rule weapon is equipped.
            ForgeContactPatch.ForgeContactSweeper.Ensure();
        }

        public static bool TryGet(WeaponData weapon, out Config config)
        {
            config = null;

            if (weapon == null || !_weapons.TryGetValue(weapon, out config))
                return false;

            return config != null && config.enabled;
        }

        public static bool Any { get { return _weapons.Count > 0; } }

        // "Does the sweeper need to exist", NOT "should it act". The second
        // question is `Live`, and conflating the two is what shipped the bug.
        public static bool WantsWorld { get { return _worldOwners > 0; } }
    }
}
