using System;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;

namespace WeaponForge
{
    // "leech": your hits heal you.
    //
    // WHY IT MEASURES THE VICTIM'S TANK INSTEAD OF READING THE DAMAGE NUMBER.
    // `DamagableResource.Damage(float)` can refuse the damage outright, three
    // different ways, all silent:
    //
    //     if (IsDamageBlocked()) return;                     // damageBlockers
    //     if (amount > 0 && Unit.HasTank(resource) &&         // wrong element
    //         Time.time - lastDamageTime >= iFrameDuration)   // i-frames
    //
    // So a prefix reading `amount` would heal you for hits that dealt NOTHING -
    // and that is not a corner case: plenty of enemies have no tank of a given
    // resource at all, which is exactly how the damage-type table makes a
    // Crawler resist nearly everything. Snapshotting the tank before and after
    // gives the damage that ACTUALLY landed instead, and it composes with crit
    // for free (crit multiplies `amount` in a Priority.First prefix, so the tank
    // delta already includes it).
    //
    // WHICH WEAPON is the same free lookup crit uses: `ForgeKillsPatch.Source`
    // is live for all three damage routes and null for a burn tick, a gas cloud
    // or a cell collision - so damage-over-time does not feed you unless the
    // file opts in.
    //
    // THE ORB IS NOT A WEAPON, and that was worth checking because it looks like
    // one. It is a plain GameObject with a SpriteRenderer that flies from the
    // victim to your ship. A real weapon would need a Shooter, an owner, a
    // barrel, a cost and a fire rate, would do damage, and would fire OUTWARD
    // from the ship - the opposite of the effect. As a cosmetic mover it works
    // identically on all six weapon types, which is the actual requirement.
    public static class ForgeLeech
    {
        private static readonly ManualLogSource Log =
            ForgeLog.Source("WeaponForge.Leech");

        // When the health actually arrives.
        public enum HealOn
        {
            // With the orb, on the orb reaching your ship. His default: it reads
            // as absorption rather than as a number changing.
            Arrival,

            // The instant the hit lands. Predictable, never lost, and the right
            // answer for a weapon that wants the drain without the flourish.
            Hit
        }

        public class Config
        {
            // ---- how much ------------------------------------------------

            // Share of the damage actually dealt. 0.1 = 10%.
            public float fraction;

            // Flat healing per hit, whatever the damage was. Stacks with
            // `fraction`; either alone is a complete config.
            public float flat;

            // Empty means the player's OWN health pool, found from their
            // DamagableResource rather than guessed by name.
            public string resource = "";

            // ---- limits --------------------------------------------------

            // Ceiling on healing per second. 0 = uncapped.
            //
            // THIS IS THE LOAD-BEARING NUMBER. A hitscan beam threading six
            // enemies at the Caps Laser's damageRepeatDelay of 0.08 is ~75 heal
            // events a second, so an uncapped leech beam is immortality. Unlike
            // the on-kill cap this one is PARTIAL: a hit that wants 3 with 2 left
            // in the budget heals 2, rather than being refused outright. An
            // all-or-nothing cap makes a weapon feel randomly dead.
            public float maxPerSecond;

            // Minimum seconds between heals from the SAME enemy. 0 = off.
            // Rewards spreading damage around instead of focusing one target.
            public float perEnemyDelay;

            public HealOn healOn = HealOn.Arrival;

            // The mod's own damage sources - gas, swipe, contact (a ram or a
            // lunge), slam. Off by default, same as crit and for the same
            // reason: a gas cloud ticks several times a second.
            public bool onExtras;

            // ---- the absorb orb -------------------------------------------

            public bool orb = true;

            // Stock art by the game's own name. A numbered run animates - see
            // ForgeSpriteLibrary.TryGetStockFrames.
            public string orbSprite = "area_zero_particle_tuff";
            public float orbFps = 14f;
            public float orbScale = 1f;

            // Units per second toward the ship. It TRACKS the ship rather than
            // flying at where it was, or a moving player would never catch it.
            public float orbSpeed = 22f;

            // How hard the orb swoops instead of beelining, as a share of its
            // speed thrown sideways at launch: 0 is a straight line, 1 leaves at
            // 45 degrees off. Pure garnish, but a dead-straight line reads as a
            // bullet being fired AT you, which is the one thing this must not
            // look like. The side is random per orb so a stream fans out.
            public float orbArc = 0.7f;

            // Degrees per second of spin on the sprite.
            public float orbSpin;

            // Backstop. An orb that somehow cannot reach you pays out anyway at
            // the end of this, because a lost orb is lost HEALING.
            public float orbLifetime = 2.5f;

            public bool orbHasColor;
            public Color orbColor = Color.white;

            // Most orbs alive at once. A shotgun with eight pellets or a beam
            // threading a crowd would otherwise be a firehose. Over the cap the
            // heal is granted IMMEDIATELY instead of being dropped.
            public int maxOrbs = 14;

            // Optional trail on the orb, parsed with the ordinary trail block.
            public ForgeTrail.Spec orbTrail;

            public string sfx = "";

            // ---- resolved at build time ----------------------------------
            [NonSerialized] public Resource resourceAsset;
            [NonSerialized] public string sfxGuid;
            [NonSerialized] public Sprite[] orbFrames;

            // ---- runtime -------------------------------------------------
            [NonSerialized] public float windowStartedAt;
            [NonSerialized] public float healedThisSecond;
            [NonSerialized] public Dictionary<Unit, float> lastFromEnemy;

            public bool DoesAnything
            {
                get { return fraction > 0f || flat > 0f; }
            }

            // `healOn: "arrival"` with no orb to arrive would heal NEVER. The
            // builder warns; this is what stops it being a silent no-op.
            public HealOn EffectiveHealOn
            {
                get
                {
                    return (orb && orbFrames != null && orbFrames.Length > 0)
                        ? healOn
                        : HealOn.Hit;
                }
            }

            // How much of `wanted` the per-second budget allows. Partial on
            // purpose - see maxPerSecond.
            //
            // `cap` is passed in rather than read off `maxPerSecond`, because a
            // module can tighten it and that resolution belongs to Resolve.
            public float Take(float wanted, float cap)
            {
                if (wanted <= 0f)
                    return 0f;

                if (cap <= 0f)
                    return wanted;

                if (Time.time - windowStartedAt >= 1f)
                {
                    windowStartedAt = Time.time;
                    healedThisSecond = 0f;
                }

                float left = cap - healedThisSecond;

                if (left <= 0f)
                    return 0f;

                float given = Mathf.Min(wanted, left);
                healedThisSecond += given;
                return given;
            }

            // Keyed on the Unit, not the DamagableResource: a unit can carry
            // several damageable pools and "this enemy already fed me" is about
            // the enemy.
            // `delay` is passed in rather than read off `perEnemyDelay`, because
            // a module can lengthen it and that resolution belongs to Resolve.
            public bool EnemyReady(Unit victim, float delay)
            {
                if (delay <= 0f || victim == null)
                    return true;

                if (lastFromEnemy == null)
                    lastFromEnemy = new Dictionary<Unit, float>();

                float last;

                if (lastFromEnemy.TryGetValue(victim, out last) &&
                    Time.time - last < delay)
                {
                    return false;
                }

                // A dead enemy's entry is dead weight, and a crowded fight fills
                // this fast. Pruned rather than kept forever, the same shape as
                // the shot->weapon map.
                if (lastFromEnemy.Count > 128)
                    Prune(lastFromEnemy);

                lastFromEnemy[victim] = Time.time;
                return true;
            }

            private static void Prune(Dictionary<Unit, float> map)
            {
                var dead = new List<Unit>();

                foreach (var pair in map)
                {
                    if (pair.Key == null ||
                        Time.time - pair.Value > 10f)
                    {
                        dead.Add(pair.Key);
                    }
                }

                for (int i = 0; i < dead.Count; i++)
                    map.Remove(dead[i]);

                if (map.Count > 128)
                    map.Clear();
            }
        }

        // ---- per-weapon registry -------------------------------------------

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

        // One bool test guards a prefix that runs on every point of damage
        // anything in the game takes.
        public static bool Any
        {
            get
            {
                return _weapons.Count > 0 ||
                       _bonus.Count > 0 ||
                       _global.Count > 0;
            }
        }

        // ---- contributions from OTHER sources (Module Forge) ---------------
        //
        // Same channel crit has, and for the same reason: this mod owns the
        // leech damage patch, so a Module Forge leech module stands its own
        // engine down and pushes its numbers in here instead.
        private class Contribution
        {
            public float fraction;
            public float flat;
            public float maxPerSecond;
            public float perEnemyDelay;
        }

        // Weapon -> contributor -> its numbers. KEYED, so pushing again is
        // idempotent: a module re-pushes on every stat recalculation (that is
        // how it answers to a BoosterCore) and an ADD-style API would drift a
        // weapon to healing everything it touches.
        private static readonly
            Dictionary<WeaponBase, Dictionary<object, Contribution>> _bonus =
                new Dictionary<WeaponBase, Dictionary<object, Contribution>>();

        // Contributors with no weapon to key on: a module in the SHIP grid,
        // which never gets `IWeaponModifier.Modify` and so never learns a
        // weapon. "All your guns drain" is what it should mean.
        private static readonly Dictionary<object, Contribution> _global =
            new Dictionary<object, Contribution>();

        // `weapon` may be NULL, meaning every weapon. The contributor is
        // withdrawn from wherever it was first: a module can move grids, and a
        // weapon is a fresh `WeaponBase` instance whenever its cluster is
        // rebuilt, so "set here and nowhere else" is the only semantics that
        // cannot leave a stale contribution crediting a dead weapon.
        public static void SetBonus(
            WeaponBase weapon, object key,
            float fraction, float flat, float maxPerSecond,
            float perEnemyDelay)
        {
            if (key == null)
                return;

            ClearBonus(key);

            Dictionary<object, Contribution> byKey;

            if (weapon == null)
            {
                byKey = _global;
            }
            else if (!_bonus.TryGetValue(weapon, out byKey))
            {
                byKey = new Dictionary<object, Contribution>();
                _bonus[weapon] = byKey;
            }

            byKey[key] = new Contribution
            {
                fraction = fraction,
                flat = flat,
                maxPerSecond = maxPerSecond,
                perEnemyDelay = perEnemyDelay
            };
        }

        public static void ClearBonus(object key)
        {
            if (key == null)
                return;

            _global.Remove(key);

            List<WeaponBase> emptied = null;

            foreach (var pair in _bonus)
            {
                if (!pair.Value.Remove(key) || pair.Value.Count > 0)
                    continue;

                if (emptied == null)
                    emptied = new List<WeaponBase>();

                emptied.Add(pair.Key);
            }

            if (emptied == null)
                return;

            for (int i = 0; i < emptied.Count; i++)
                _bonus.Remove(emptied[i]);
        }

        // ---- resolving one live weapon -------------------------------------

        // Settings for a weapon that never asked for leech itself but had it
        // granted by a module. Cloned per weapon rather than shared, because a
        // Config carries per-weapon RUNTIME state (the per-second budget and
        // the per-enemy timers) and two weapons sharing one budget would be a
        // silent surprise.
        private static readonly Dictionary<WeaponBase, Config> _granted =
            new Dictionary<WeaponBase, Config>();

        // A MODULE-GRANTED LEECH IS CAPPED BY DEFAULT, and that is the one
        // opinionated number in this file. A weapon author who writes
        // "maxPerSecond": 0 has been warned by their own build log; a player who
        // slots a leech module onto an arbitrary gun has seen no warning at all,
        // so the default must not be "immortality on a beam".
        private const float GrantedCapPerSecond = 5f;

        private static Config GrantedFor(WeaponBase weapon)
        {
            Config cfg;

            if (_granted.TryGetValue(weapon, out cfg))
                return cfg;

            cfg = new Config { maxPerSecond = GrantedCapPerSecond };
            ResolveDefaultOrbArt(cfg);

            // Bounded rather than unbounded: a long run rebuilds weapons.
            if (_granted.Count > 64)
                _granted.Clear();

            _granted[weapon] = cfg;
            return cfg;
        }

        // The orb art for a granted config. Resolved once, lazily - it needs
        // the game's assets loaded, which they are by the time anything is
        // taking damage.
        private static Sprite[] _defaultOrbFrames;
        private static bool _defaultOrbLooked;

        private static void ResolveDefaultOrbArt(Config cfg)
        {
            if (!_defaultOrbLooked)
            {
                _defaultOrbLooked = true;

                try
                {
                    ForgeSpriteLibrary.Art art;

                    if (ForgeSpriteLibrary.TryGetStockArt(
                            cfg.orbSprite, out art) && art != null)
                    {
                        _defaultOrbFrames =
                            (art.animation != null &&
                             art.animation.frames != null &&
                             art.animation.frames.Length > 0)
                                ? art.animation.frames
                                : new[] { art.sprite };
                    }
                }
                catch (Exception e)
                {
                    Log.LogWarning(
                        "Resolving the default orb art failed: " + e.Message);
                }
            }

            cfg.orbFrames = _defaultOrbFrames;
        }

        // What is actually in force on one live weapon: its own file plus every
        // module contribution. False when there is no leech at all.
        //
        // The cap comes back as an OUT PARAM rather than being written onto the
        // Config, because a Config belongs to a WeaponData (a template) while
        // contributions are keyed to a live WeaponBase - two weapons sharing one
        // template with different modules attached would otherwise overwrite
        // each other's cap on every hit.
        public static bool Resolve(
            WeaponBase weapon, out Config cfg,
            out float fraction, out float flat, out float cap,
            out float perEnemyDelay)
        {
            cfg = null;
            fraction = 0f;
            flat = 0f;
            cap = 0f;
            perEnemyDelay = 0f;

            if (weapon == null)
                return false;

            Config own;
            bool hasOwn = TryGet(weapon.TemplateData, out own);

            float bonusFraction = 0f, bonusFlat = 0f, tightestCap = 0f;
            float longestDelay = 0f;
            bool hasBonus = false;

            // ★ ONLY THE WEAPON'S OWN SHIP'S MODULES - before R19 a Module
            // Forge leech module on the ship grid let ENEMY guns heal off you.
            // See ForgeModuleScope.
            foreach (var pair in _global)
            {
                if (!ForgeModuleScope.Applies(pair.Key, weapon))
                    continue;

                Contribution c = pair.Value;
                bonusFraction += c.fraction;
                bonusFlat += c.flat;
                tightestCap = Tighter(tightestCap, c.maxPerSecond);
                longestDelay = Mathf.Max(longestDelay, c.perEnemyDelay);
                hasBonus = true;
            }

            Dictionary<object, Contribution> byKey;

            if (_bonus.TryGetValue(weapon, out byKey))
            {
                foreach (Contribution c in byKey.Values)
                {
                    bonusFraction += c.fraction;
                    bonusFlat += c.flat;
                    tightestCap = Tighter(tightestCap, c.maxPerSecond);
                    longestDelay = Mathf.Max(longestDelay, c.perEnemyDelay);
                    hasBonus = true;
                }
            }

            if (!hasOwn && !hasBonus)
                return false;

            cfg = hasOwn ? own : GrantedFor(weapon);

            fraction = (hasOwn ? own.fraction : 0f) + bonusFraction;
            flat = (hasOwn ? own.flat : 0f) + bonusFlat;

            // THE TIGHTEST CAP WINS, treating 0 as "no cap of my own". A cap is
            // a safety limit, so the strict direction is the right one: a module
            // that ships a cap always protects the player, even on a weapon
            // whose author left it uncapped. The converse - letting a generous
            // module loosen a weapon's tight cap - would let a module quietly
            // create the immortality the weapon author had ruled out.
            cap = Tighter(cfg.maxPerSecond, tightestCap);

            // Same direction, same reason: the LONGEST wait wins, because a
            // per-enemy delay is also a limit and a module that ships one should
            // not be loosened by the weapon it is slotted into.
            perEnemyDelay = Mathf.Max(cfg.perEnemyDelay, longestDelay);

            return fraction > 0f || flat > 0f;
        }

        private static float Tighter(float a, float b)
        {
            if (a <= 0f) return b;
            if (b <= 0f) return a;
            return Mathf.Min(a, b);
        }

        // ---- scoping the mod's own damage ----------------------------------
        //
        // None of this mod's own `TakeDamage` calls goes through a route that
        // sets `ForgeKillsPatch.Source`, so each has to opt in explicitly - the
        // gas cloud, the swipe blade, contact damage (which is how a ram and a
        // lunge hurt things) and slam already do, by wrapping their call in
        // `ForgeCrit.Begin`/`End`.
        //
        // DELIBERATELY DELEGATED rather than given a second scope of its own.
        // "which of the mod's own damage sources is running right now" is ONE
        // fact, and two statics tracking it would be two things to remember to
        // wrap at every future damage site - the second would silently be
        // forgotten. ForgeCrit happens to own it because it needed it first;
        // `onExtras` on THIS config still decides whether leech acts on it.
        public static WeaponBase ExtraWeapon
        {
            get { return ForgeCrit.ExtraWeapon; }
        }

        // ---- which pool gets healed ----------------------------------------
        //
        // Default is the player's OWN health pool, read off their
        // DamagableResource rather than resolved from the string "Health".
        // Correct by construction: whatever the ship counts as its life is what
        // a leech weapon refills, with no name to get wrong. Falling back to the
        // name only matters if a ship ever ships without one.
        private static readonly Dictionary<Unit, Resource> _ownHealth =
            new Dictionary<Unit, Resource>();

        public static Resource HealthOf(Unit unit)
        {
            if (unit == null)
                return null;

            Resource found;

            if (_ownHealth.TryGetValue(unit, out found) && found != null)
                return found;

            try
            {
                var dr = unit.GetComponent<DamagableResource>();

                if (dr == null)
                    dr = unit.GetComponentInChildren<DamagableResource>(true);

                found = (dr != null) ? dr.resource : null;

                if (found == null)
                {
                    found = JsonFieldMapper.FindAsset(
                        typeof(Resource), "Health") as Resource;
                }

                if (_ownHealth.Count > 64)
                    _ownHealth.Clear();

                _ownHealth[unit] = found;
                return found;
            }
            catch (Exception e)
            {
                Log.LogWarning("Finding the ship's health pool failed: " + e);
                return null;
            }
        }

        // ---- paying out -----------------------------------------------------

        // Returns what actually went in, so the caller can decide whether the
        // flourish was worth playing.
        public static float Heal(Unit player, Resource resource, float amount)
        {
            if (player == null || resource == null || amount <= 0f)
                return 0f;

            try
            {
                if (!player.HasTank(resource))
                    return 0f;

                ResourceTank tank = player.GetTank(resource);

                if (tank == null)
                    return 0f;

                float before = tank.Value;

                // Charge() clamps to [0, Capacity], which is right for health
                // and wrong for an infinite tank whose Capacity is meaningless -
                // there the Value setter refuses decreases on its own, so adding
                // directly is both safe and the only thing that works. Same rule
                // as the on-kill payout.
                if (tank.isInfinite)
                    tank.Value = tank.Value + amount;
                else
                    tank.Charge(amount);

                return tank.Value - before;
            }
            catch (Exception e)
            {
                Log.LogWarning("Healing failed: " + e);
                return 0f;
            }
        }

        // ---- orb bookkeeping ------------------------------------------------

        private static int _orbs;

        public static int OrbsInFlight { get { return _orbs; } }

        public static bool ClaimOrb(int max)
        {
            if (max > 0 && _orbs >= max)
                return false;

            _orbs++;
            return true;
        }

        public static void ReleaseOrb()
        {
            if (_orbs > 0)
                _orbs--;
        }

        public static void Reset()
        {
            _orbs = 0;
            _ownHealth.Clear();
            _bonus.Clear();
            _global.Clear();
            _granted.Clear();

            // Per-weapon runtime state only. The configs themselves are
            // registered once at startup from the weapon files, so clearing
            // `_weapons` here would delete leech from every weapon for the rest
            // of the session - the mistake ForgeCrit.Reset nearly shipped.
            foreach (Config c in _weapons.Values)
            {
                c.windowStartedAt = 0f;
                c.healedThisSecond = 0f;

                if (c.lastFromEnemy != null)
                    c.lastFromEnemy.Clear();
            }
        }
    }
}
