using System;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;

namespace WeaponForge
{
    // "crit": a chance for a hit to do multiplied damage.
    //
    // WHY THIS SITS IN THE DAMAGE PATH AND NOT ON THE SHOT. Every damage route
    // in this game funnels into ONE method - `DamagableResource.Damage(float)`.
    // `ProjectileCollided` -> `TakeDamage(Damage)` -> `Damage(amount)`;
    // `OnExplosion` -> `TakeDamage(IReadOnlyList<Damage>)` -> `Damage(amount)`;
    // `OnHitByHitscanWeapon` likewise. So a single prefix there covers
    // projectiles, beams AND explosions with no per-feature work, and every
    // future damage source arrives already covered.
    //
    // AND IT CANNOT FIGHT ForgeDamageStack. That contract exists because two
    // features writing `projectile.Damage` fought over one field. Crit never
    // touches the projectile: it multiplies the float already in flight to the
    // victim, after the stack has produced it. So grow, damageRamp, ricochet
    // and a crit all compose, and the ordering problem never arises.
    //
    // WHICH WEAPON gets the credit is already solved: `ForgeKillsPatch.Source`
    // is live for the duration of all three damage routes. It is null for a
    // burn tick, a gas cloud or a cell collision - which is exactly the gate
    // this feature wants, so ordinary weapon damage crits and damage-over-time
    // does not, for free.
    public static class ForgeCrit
    {
        private static readonly ManualLogSource Log =
            ForgeLog.Source("WeaponForge.Crit");

        // When does the dice get rolled?
        public enum Roll
        {
            // Once per thing damaged. A piercing shot can crit enemy 1 and not
            // enemy 2. The default, and the only mode that means anything on a
            // beam or an explosion.
            Hit,

            // Once per projectile, at Shoot. If it crits, everything that shot
            // touches crits. A shotgun rolls per PELLET here, because each
            // pellet is its own projectile.
            Shot,

            // Once per trigger pull, at DoShoot - so a whole shotgun blast is
            // all-crit or no-crit. Exists because "half my pellets crit" reads
            // as noise rather than as a payoff.
            Pull
        }

        public class Config
        {
            // 0.15 = 15%. Values above 1 are allowed and meaningful - see
            // `overflow`. A percent-looking number (15) is converted at build
            // time, so both spellings work.
            public float chance;

            // What a crit multiplies damage BY. 2 = double.
            public float multiplier = 2f;

            public Roll roll = Roll.Hit;

            // Above 100%, the excess becomes a chance at a SECOND crit stack
            // rather than being thrown away: 1.5 always crits and half the time
            // crits twice. Stacks add the BONUS, so two stacks of x2 is x3, not
            // x4 - multiplying would make three stacks x8 and stop being
            // tunable.
            public bool overflow = true;

            // ---- feedback. The game has NO floating damage numbers anywhere,
            // so a crit is invisible unless one of these is on. -------------

            // A second, larger copy of the weapon's own impact burst. ON by
            // default: it is the only one of the four that needs no extra
            // authoring from the file. Projectile hits only - a beam and an
            // explosion have no per-hit impact prefab to borrow.
            public bool impact = true;
            public float impactScale = 2f;

            // A distinct sound. Resolved to a guid at build time.
            public string sfxGuid = "";

            // A brief tint on whatever got crit. Goes through ForgeUnitTint, so
            // it composes with the slow and slam tints instead of fighting them
            // (see the unit-tint contract - never save/restore a colour).
            public bool hasColor;
            public Color color = Color.white;
            public float colorSeconds = 0.25f;
            public float colorPulse;

            // HOW HARD THE TINT COMES THROUGH, 0..1. 1 is the full colour and
            // is what this has always done; 0.35 is a hint of it. Read from
            // either "tintStrength" or "colorStrength" so the two naming
            // styles in this mod's blocks both work. See ForgeUnitTint.
            public float tintStrength = 1f;

            // multiply (the default, and what every existing file was tuned
            // against) or replace, which moves the sprite TOWARD the colour so
            // it can brighten a dark enemy. See ForgeUnitTint.
            public int tintMode = ForgeUnitTint.Multiply;

            // Camera kick, using the weapon's own ShakePreset.
            public bool shake;

            // ---- scope ----------------------------------------------------

            // The mod's OWN damage sources - gas clouds, the swipe blade, the
            // ram, contact damage, orbit rings, tethers, turrets. Default OFF
            // at his request: a gas cloud ticking several times a second would
            // roll crit on every tick and read as noise rather than a payoff.
            public bool onExtras;

            // Set by the builder when a file asked for something impossible, so
            // the log can explain once rather than the feature failing quietly.
            [NonSerialized] public bool impactUnavailable;
        }

        // ---- per-weapon registry ------------------------------------------

        private static readonly Dictionary<WeaponData, Config> _weapons =
            new Dictionary<WeaponData, Config>();

        public static void Register(WeaponData weapon, Config config)
        {
            if (weapon == null || config == null)
                return;

            _weapons[weapon] = config;
            Any = true;
        }

        // True once any weapon wants crit, so the damage prefix costs one bool
        // test in the overwhelmingly common case. That prefix runs on EVERY
        // point of damage dealt in the game, so it must be nearly free when
        // nothing is using it.
        public static bool Any { get; private set; }

        public static bool TryGet(WeaponData weapon, out Config config)
        {
            config = null;

            if (weapon == null)
                return false;

            return _weapons.TryGetValue(weapon, out config);
        }

        // ---- contributions from OTHER sources (Module Forge) --------------
        //
        // A crit module adds to whatever the weapon already has, the way pierce
        // caps are added into one number rather than one mod winning. Keyed by
        // the LIVE WeaponBase, because that is what an IWeaponModifier is
        // handed. Withdrawn explicitly on uninstall - a module that is removed
        // must take its chance with it.
        private class Contribution
        {
            public float chance;
            public float multiplier;
        }

        // Weapon -> contributor -> its numbers. KEYED, and that is the whole
        // point: `IWeaponModifier.Modify` is called again on every stat
        // recalculation, so an ADD-style API would pile the same module's crit
        // chance on over and over and a weapon would drift to a permanent 100%.
        // Setting under a key is idempotent, and several modules still stack
        // because each has its own key.
        //
        // Same family of mistake as the ship stats, which have to be recomputed
        // from a captured base rather than added to.
        private static readonly
            Dictionary<WeaponBase, Dictionary<object, Contribution>> _bonus =
                new Dictionary<WeaponBase, Dictionary<object, Contribution>>();

        // Contributors with no weapon to key on. A Module Forge module in the
        // SHIP grid is never in a weapon's cluster, so `IWeaponModifier.Modify`
        // is never called for it and there is no `WeaponBase` to key on - but
        // "all your guns crit" is exactly what a ship-grid module should mean.
        // Without this channel such a module would have been silently inert.
        private static readonly Dictionary<object, Contribution> _global =
            new Dictionary<object, Contribution>();

        // `weapon` may be NULL, meaning every weapon (see _global).
        //
        // The contributor is removed from wherever it was BEFORE being set, and
        // that is not tidiness: a module can move between grids, and a weapon is
        // rebuilt (a fresh WeaponBase instance) whenever its cluster changes. So
        // "set here, and nowhere else" is the only semantics that cannot leave a
        // stale contribution crediting a weapon that no longer exists.
        public static void SetBonus(
            WeaponBase weapon, object key, float chance, float multiplier)
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
                chance = chance,
                multiplier = multiplier
            };

            Any = true;
        }

        // Withdraws a contributor from every weapon and from the global
        // channel. A module that is removed must take its chance with it.
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

        private static bool SumBonus(
            WeaponBase weapon, out float chance, out float multiplier)
        {
            chance = 0f;
            multiplier = 0f;

            // ★ ONLY THE WEAPON'S OWN SHIP'S MODULES. This loop used to add
            // every ship-grid bonus to EVERY weapon, enemy guns included - a
            // Module Forge crit module made enemy shots crit you. R19; see
            // ForgeModuleScope.
            foreach (var pair in _global)
            {
                if (!ForgeModuleScope.Applies(pair.Key, weapon))
                    continue;

                chance += pair.Value.chance;
                multiplier += pair.Value.multiplier;
            }

            Dictionary<object, Contribution> byKey;

            if (weapon != null && _bonus.TryGetValue(weapon, out byKey))
            {
                foreach (Contribution c in byKey.Values)
                {
                    chance += c.chance;
                    multiplier += c.multiplier;
                }
            }

            return chance != 0f || multiplier != 0f;
        }

        // The numbers actually in force for one live weapon: its own file plus
        // every module contribution. Returns false when there is no crit at all.
        public static bool Resolve(
            WeaponBase weapon, out Config cfg, out float chance, out float mult)
        {
            cfg = null;
            chance = 0f;
            mult = 2f;

            if (weapon == null)
                return false;

            Config own;
            bool hasOwn = TryGet(weapon.TemplateData, out own);

            float bonusChance, bonusMult;
            bool hasBonus = SumBonus(weapon, out bonusChance, out bonusMult);

            if (!hasOwn && !hasBonus)
                return false;

            // A module can grant crit to a weapon whose file never mentioned it.
            // That weapon needs a Config for the feedback settings, so it gets
            // the defaults - which is why `impact` defaults to true rather than
            // false: a crit from a module is visible without the weapon author
            // having done anything.
            cfg = hasOwn ? own : Defaults;

            chance = (hasOwn ? own.chance : 0f) + bonusChance;
            mult = (hasOwn ? own.multiplier : 2f) + bonusMult;

            return chance > 0f && mult > 1f;
        }

        // Shared, never mutated - handed out when a module grants crit to a
        // weapon with no crit block of its own.
        private static readonly Config _defaults = new Config();

        public static Config Defaults { get { return _defaults; } }

        // ---- the roll ------------------------------------------------------

        // How many crit stacks this hit earned. 0 = ordinary hit.
        public static int Stacks(float chance, bool overflow)
        {
            if (chance <= 0f)
                return 0;

            if (!overflow)
                return UnityEngine.Random.value < Mathf.Min(chance, 1f) ? 1 : 0;

            int guaranteed = Mathf.FloorToInt(chance);
            float remainder = chance - guaranteed;

            if (UnityEngine.Random.value < remainder)
                guaranteed++;

            // A pathological chance (a module stack gone wrong) should not
            // produce a 40x hit silently.
            return Mathf.Min(guaranteed, 8);
        }

        // Stacks add the bonus: x2 twice is x3.
        public static float Factor(int stacks, float multiplier)
        {
            if (stacks <= 0)
                return 1f;

            return 1f + stacks * (multiplier - 1f);
        }

        // ---- per-pull and per-shot decisions -------------------------------
        //
        // The damage path knows the WEAPON but not the shot, so a decision made
        // earlier has to be findable from there. Same shape as ForgeVolley's
        // frame-stamped axis: a pull is synchronous inside Shooter.Shoot for its
        // first shot, and the burst tail is bounded, so a weapon-keyed entry
        // stamped with the pull is exact enough and allocates nothing per hit.
        private class PullDecision
        {
            public int stacks;
            public int frame;
        }

        private static readonly Dictionary<WeaponBase, PullDecision> _pull =
            new Dictionary<WeaponBase, PullDecision>();

        public static void RollPull(WeaponBase weapon, int stacks)
        {
            if (weapon == null)
                return;

            PullDecision d;

            if (!_pull.TryGetValue(weapon, out d))
            {
                d = new PullDecision();
                _pull[weapon] = d;
            }

            d.stacks = stacks;
            d.frame = Time.frameCount;
        }

        public static bool TryGetPull(WeaponBase weapon, out int stacks)
        {
            stacks = 0;

            PullDecision d;

            if (weapon == null || !_pull.TryGetValue(weapon, out d))
                return false;

            stacks = d.stacks;
            return true;
        }

        // Rides the projectile for Roll.Shot. An int, so it survives the
        // Instantiate that a split or a catalyst copy performs - unlike a
        // config reference, which would arrive null (the prefab-config trap).
        public class ShotMark : MonoBehaviour
        {
            [NonSerialized] public int stacks;
            [NonSerialized] public bool rolled;
        }

        // ---- scoping the mod's own damage ---------------------------------
        //
        // Twelve places in this mod call HealthBase.TakeDamage directly (gas,
        // swipe, ram, contact, orbit, tether, turret, slam, lunge, gravity,
        // shield, overheat). None of them goes through the three routes that
        // set ForgeKillsPatch.Source, so none of them can crit unless it says
        // so. Wrapping a call in Begin/End opts it in - and `onExtras` still
        // has to be on in the file.
        private static WeaponBase _extraWeapon;

        public static WeaponBase ExtraWeapon { get { return _extraWeapon; } }

        // Returns what was in force so the caller can put it back. Nesting is
        // not expected - a gas tick does not trigger a swipe - but a plain
        // "set to null on the way out" would silently break the outer scope if
        // it ever did, and save/restore costs one local.
        public static WeaponBase Begin(WeaponBase weapon)
        {
            WeaponBase previous = _extraWeapon;
            _extraWeapon = weapon;
            return previous;
        }

        public static void End(WeaponBase previous)
        {
            _extraWeapon = previous;
        }

        // ---- feedback ------------------------------------------------------

        public static void Feedback(
            Config cfg, WeaponBase weapon, Component victim, int stacks)
        {
            if (cfg == null || victim == null)
                return;

            try
            {
                if (!string.IsNullOrEmpty(cfg.sfxGuid))
                    AudioManager.PlaySfx(cfg.sfxGuid, victim.transform.position);

                if (cfg.hasColor)
                {
                    // DamagableResource keeps its own Unit private, so find it
                    // the way that class does - upward, because health sits on
                    // a child on some prefabs.
                    Unit hurt = victim.GetComponentInParent<Unit>();

                    if (hurt != null)
                    {
                        ForgeUnitTint.Set(
                            hurt, "crit", cfg.color,
                            Mathf.Max(0.05f, cfg.colorSeconds),
                            cfg.colorPulse, cfg.tintStrength, cfg.tintMode);
                    }
                }

                if (cfg.shake && weapon != null)
                    Shake(weapon);
            }
            catch (Exception e)
            {
                // Feedback is decoration. It must never cost the damage that
                // earned it - the same rule the diagnostics follow.
                Log.LogWarning("crit feedback failed: " + e.Message);
            }
        }

        // The weapon's own preset, so a crit shakes like a heavier version of
        // that gun rather than like a generic thump.
        //
        // BOTH the preset read and the shake call go through reflection, and
        // that is not incidental: `ShakePreset` and `ProCamera2DShake` live in
        // ProCamera2D, a third-party assembly this project deliberately does
        // not reference. Naming the type in a field access drags the reference
        // back in - which is exactly the compile error this replaced.
        // ForgeOverheat already solved this, so reuse it rather than keeping a
        // second copy of the same reflection.
        private static void Shake(WeaponBase weapon)
        {
            try
            {
                WeaponData data = weapon.TemplateData;

                if (data == null)
                    return;

                var field = HarmonyLib.AccessTools.Field(
                    typeof(WeaponData), "shakePreset");

                if (field == null)
                    return;

                ForgeOverheat.Shake(field.GetValue(data));
            }
            catch
            {
                // No camera in this scene, or no preset. Nothing to do.
            }
        }

        // The bigger impact burst. Projectile hits only: a beam's spark and an
        // explosion's flash are not per-hit prefabs we can borrow.
        public static void BigImpact(
            Config cfg, Projectile shot, Vector3 at)
        {
            if (cfg == null || !cfg.impact || shot == null)
                return;

            try
            {
                GameObject prefab = shot.ImpactBehaviour.destroyEffect;

                if (prefab == null)
                    return;

                GameObject extra = UnityEngine.Object.Instantiate(
                    prefab, at, Quaternion.identity);

                float s = Mathf.Max(1.05f, cfg.impactScale);
                extra.transform.localScale *= s;

                // Particles ignore transform scale for their own quads - the
                // size lives on startSize - so scale the systems too or the
                // burst is the same size in a bigger empty object.
                ParticleSystem[] systems =
                    extra.GetComponentsInChildren<ParticleSystem>(true);

                for (int i = 0; i < systems.Length; i++)
                {
                    var main = systems[i].main;
                    main.startSizeMultiplier = main.startSizeMultiplier * s;
                }
            }
            catch (Exception e)
            {
                Log.LogWarning("crit impact burst failed: " + e.Message);
            }
        }

        // PER-RUN state only. `_weapons` is deliberately NOT cleared: those
        // configs are registered once at startup from the weapon files, so
        // clearing them here would silently delete crit from every weapon for
        // the rest of the session - and this is called from a run-entry patch.
        // Anything per-run (module contributions, pull decisions) is rebuilt by
        // the modules re-registering, so it must go.
        public static void Reset()
        {
            _bonus.Clear();
            _global.Clear();
            _pull.Clear();
            _extraWeapon = null;
            Any = _weapons.Count > 0;
        }

        // The numbers in force on one live weapon - its own file PLUS every
        // module contribution - for Module Forge's stat-card line.
        //
        // A total rather than "the weapon's own half", because when both mods
        // are installed the module contributions have already been pushed in
        // HERE (this mod owns the crit patch). Handing back only the weapon's
        // baked value would make the card contradict the game, and summing two
        // halves on the other side would double-count them. One number, one
        // owner - the same reason the pierce lines are merged rather than
        // double-reported.
        public static bool TryGetTotal(
            WeaponBase weapon, out float chance, out float multiplier)
        {
            Config ignored;
            return Resolve(weapon, out ignored, out chance, out multiplier);
        }
    }
}
