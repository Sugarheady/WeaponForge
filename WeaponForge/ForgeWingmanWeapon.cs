using System;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;

namespace WeaponForge
{
    // THE RUNTIME CLONE: turn "the gun you are carrying" into "the gun the
    // wingmen fire".
    //
    // This is the load-bearing half of `wingman` and the only place in this mod
    // where a weapon is built after startup. Four steps:
    //
    //   1. Clone the carried `WeaponData` (`ScriptableObject.Instantiate`), so
    //      nothing we do can reach your actual gun.
    //   2. Clone its projectile prefab, so prefab-attached features land on our
    //      copy rather than on the one your gun fires.
    //   3. Inherit the source weapon's own Forge registrations onto the clone
    //      (`ForgeWeaponAlias`), because those are keyed by the asset and a
    //      clone is a different asset.
    //   4. Re-run the gadget file's feature chain over the clone
    //      (`WeaponBuilder.ApplyFeaturesTo`), so the gadget's `wave` / `burn` /
    //      whatever lands on the copies and on nothing else.
    //
    // Order matters between 3 and 4: **inherit first, apply second**, so the
    // gadget's own blocks overwrite what the weapon brought. That is the
    // "gadget wins on conflicts" rule, and it is one line of ordering rather
    // than a resolution pass.
    //
    // WHAT THIS COSTS, SAID OUT LOUD. A clone is a real asset and a real prefab
    // and it registers into ~20 static tables that are deliberately never
    // cleared at run entry. Swap weapons ten times in a run and there are ten
    // of everything unless they are torn down, so `Release` is not optional
    // housekeeping - it is the thing that stops this feature leaking the
    // session. `ForgeBurnResetPatch.ResetAll` calls `Reset` as the backstop.
    public static class ForgeWingmanWeapon
    {
        private static readonly ManualLogSource Log =
            ForgeLog.Source("WeaponForge.Wingman");

        private class Built
        {
            public WeaponData clone;
            public GameObject prefabClone;
            public WeaponData source;
            public ForgeWingman.Config cfg;
        }

        // Keyed by the pair, because two wingman gadgets can be equipped at
        // once and each wants its own clone of the same carried weapon.
        private static readonly Dictionary<ForgeWingman.Config,
            Dictionary<WeaponData, Built>> _built =
            new Dictionary<ForgeWingman.Config, Dictionary<WeaponData, Built>>();

        private static bool _saidBuilt;

        // ★ WHICH WeaponData ARE COPIES. Needed because a wingman's shot is
        // indistinguishable from your own at the point damage lands: the clone
        // inherits the source's registrations, and `Equip(_unit)` means both
        // report the PLAYER as `WeaponBase.Owner`. That is correct - it is what
        // credits wingman kills to you - but it leaves no way to answer "was
        // that me or a copy?", which `rank.fromWingmen: false` has to ask.
        //
        // A set rather than a name check: the clone is named
        // "Forge Wingman <source>", and matching on that would make a rename
        // silently stop working. Maintained beside `_built`, so it cannot
        // outlive the clones it names.
        private static readonly HashSet<WeaponData> _copies =
            new HashSet<WeaponData>();

        // Is this weapon asset a wingman's copy rather than a weapon the player
        // is actually holding? False for anything this mod did not clone, which
        // is the safe answer - an unknown weapon is the player's.
        public static bool IsCopy(WeaponData data)
        {
            return data != null && _copies.Contains(data);
        }

        // The weapon a wingman copy mirrors, or null for anything that is not
        // one. Rank asks this so a wingman's damage ranks the COPY of the gun
        // you are carrying - since R19 each ranked gun is its own copy, and
        // "the file" is no longer an answer.
        public static WeaponData SourceOf(WeaponData copy)
        {
            if (copy == null || !_copies.Contains(copy))
                return null;

            foreach (var perSource in _built.Values)
                foreach (var kv in perSource)
                    if (kv.Value != null && ReferenceEquals(kv.Value.clone, copy))
                        return kv.Value.source;

            return null;
        }

        // Drop every copy built FROM `source`. Called when a ranked gun's own
        // weapon is rebuilt with a new unlock: a wingman mirroring it built
        // its copy from the old version, and the per-source cache would
        // otherwise keep handing that back. The controllers rebuild their
        // weapon instances on the next generation, which this also bumps.
        public static void ForgetSource(WeaponData source)
        {
            if (source == null)
                return;

            bool any = false;

            foreach (var perSource in _built.Values)
            {
                Built b;

                if (perSource.TryGetValue(source, out b))
                {
                    Destroy(b);
                    perSource.Remove(source);
                    any = true;
                }
            }

            if (any)
                ForgeWingmanPatch.Invalidate();
        }

        // ------------------------------------------------------------------

        // The weapon this gadget's wingmen should fire, given what the player
        // is carrying right now. Null when there is nothing to copy - which is
        // a legitimate state (an empty secondary slot), not a failure.
        public static WeaponData For(
            ForgeWingman.Config cfg, WeaponData source)
        {
            if (cfg == null || source == null)
                return null;

            Dictionary<WeaponData, Built> perSource;

            if (!_built.TryGetValue(cfg, out perSource))
            {
                perSource = new Dictionary<WeaponData, Built>();
                _built[cfg] = perSource;
            }

            Built built;

            if (perSource.TryGetValue(source, out built) &&
                built.clone != null)
            {
                return built.clone;
            }

            built = Build(cfg, source);

            if (built == null)
                return null;

            perSource[source] = built;

            if (built.clone != null)
                _copies.Add(built.clone);

            return built.clone;
        }

        private static Built Build(
            ForgeWingman.Config cfg, WeaponData source)
        {
            try
            {
                WeaponData clone = UnityEngine.Object.Instantiate(source);
                clone.name = "Forge Wingman " + source.name;
                clone.hideFlags = HideFlags.None;

                GameObject prefabClone = ClonePrefabOf(clone);

                // INHERIT FIRST. Everything the source weapon was registered
                // for lands on the clone, and then the gadget's own blocks run
                // over the top - so a key set in both places resolves to the
                // gadget's, which is the rule the design settled on.
                List<string> carried = cfg.inherit
                    ? ForgeWeaponAlias.Inherit(source, clone)
                    : new List<string>();

                // A wingman gadget IS a gadget, so the five features that refuse
                // or warn differently in a gadget slot see the truth.
                WeaponBuilder.ApplyFeaturesTo(
                    clone, cfg.root, true, "gadget1", cfg.fileName);

                // The one number that is ours rather than the weapon's.
                if (cfg.damageScale != 1f)
                {
                    Damage d = clone.damage;
                    d.amount = Mathf.Max(0f, d.amount * cfg.damageScale);
                    clone.damage = d;
                }

                Say(cfg, source, clone, carried, prefabClone != null);

                return new Built
                {
                    clone = clone,
                    prefabClone = prefabClone,
                    source = source,
                    cfg = cfg
                };
            }
            catch (Exception e)
            {
                // A wingman that cannot be built must cost you the wingmen and
                // nothing else. Your own gun is a different asset and has not
                // been touched at this point - step 1 is the clone.
                Log.LogError(
                    "wingman: could not build a copy of '" + source.name +
                    "', so this gadget will do nothing until you change " +
                    "weapon. Your own weapon is unaffected. " + e);

                return null;
            }
        }

        // Give the clone its own projectile prefab up front.
        //
        // Each Apply* clones the prefab lazily when it needs to modify one, so
        // strictly this is only needed when the gadget's JSON happens to touch
        // no prefab feature. Doing it unconditionally means the wingman shots
        // are never the SAME OBJECT as your own shots, which is much easier to
        // reason about than "they are, unless one of fifty features happened to
        // fork it".
        // Shared with ForgeRankCopies, which needs exactly this for the same
        // reason: a copy's unlocks must never reach another copy's shots.
        internal static GameObject ClonePrefabOf(WeaponData weapon)
        {
            var pw = weapon as ProjectileWeaponData;

            if (pw != null && pw.projectilePrefab != null)
            {
                GameObject go = VisualCustomizer.ClonePrefab(
                    pw.projectilePrefab.gameObject);

                var comp = (go != null) ? go.GetComponent<Projectile>() : null;

                if (comp != null)
                {
                    pw.projectilePrefab = comp;
                    return go;
                }

                return go;
            }

            // A lobbed weapon's projectilePrefab is NULL and only
            // physicsProjectilePrefab is set - the pair is mutually exclusive,
            // and a guard that checks the wrong one refuses every lobbed weapon
            // while reporting "not a projectile weapon".
            var phys = weapon as PhysicsWeaponData;

            if (phys != null && phys.projectilePrefab != null)
            {
                GameObject go = VisualCustomizer.ClonePrefab(
                    phys.projectilePrefab.gameObject);

                // `PhysicsWeaponData.projectilePrefab` is typed **Rigidbody2D**,
                // not PhysicsProjectile - the two classes are unrelated here and
                // the compiler is the only thing that says so.
                var body = (go != null) ? go.GetComponent<Rigidbody2D>() : null;

                if (body != null)
                    phys.projectilePrefab = body;

                return go;
            }

            // Hitscan has no prefab to clone; its visuals are per-WeaponBase
            // instances, which is what makes per-wingman beams possible at all.
            return null;
        }

        // ------------------------------------------------------------------
        // Teardown
        // ------------------------------------------------------------------

        // Drop every clone this gadget made. Called when the gadget is
        // uninstalled and from the run reset.
        public static void Release(ForgeWingman.Config cfg)
        {
            Dictionary<WeaponData, Built> perSource;

            if (cfg == null || !_built.TryGetValue(cfg, out perSource))
                return;

            foreach (var kv in perSource)
                Destroy(kv.Value);

            perSource.Clear();
            _built.Remove(cfg);
        }

        private static void Destroy(Built b)
        {
            if (b == null)
                return;

            try
            {
                // Before destroying the asset, not after: Forget looks the
                // clone up by reference, and a destroyed ScriptableObject is
                // still a live dictionary key but a dead object. Same reason
                // the copies set is cleaned here rather than later.
                _copies.Remove(b.clone);

                ForgeWeaponAlias.Forget(b.clone);

                if (b.prefabClone != null)
                    UnityEngine.Object.Destroy(b.prefabClone);

                if (b.clone != null)
                    UnityEngine.Object.Destroy(b.clone);
            }
            catch (Exception e)
            {
                Log.LogWarning("wingman: teardown failed: " + e.Message);
            }
        }

        public static void Reset()
        {
            foreach (var perSource in _built.Values)
                foreach (var kv in perSource)
                    Destroy(kv.Value);

            _built.Clear();
            _copies.Clear();
        }

        public static int LiveCloneCount
        {
            get
            {
                int n = 0;

                foreach (var perSource in _built.Values)
                    n += perSource.Count;

                return n;
            }
        }

        // ------------------------------------------------------------------
        // THE CLONE-TIME DIAGNOSTIC
        // ------------------------------------------------------------------
        //
        // This exists because of how this feature fails. A broken clone does
        // not read as "the clone is broken" - it reads as ten separate reports
        // that crit, burn, the sprite, the damage and seven other things are
        // each wrong on the wingman shots. This project has paid for that shape
        // twice (a `(float?)` cast that cost five tests, a prefab Config
        // reference that cost ten).
        //
        // So the log states, at the moment a copy is made: what was cloned,
        // what the source weapon brought with it BY NAME, and whether the
        // projectile prefab was forked. A failure should then name its own
        // layer instead of being inferred from symptoms.
        private static void Say(
            ForgeWingman.Config cfg,
            WeaponData source,
            WeaponData clone,
            List<string> carried,
            bool forkedPrefab)
        {
            string inherited = cfg.inherit
                ? (carried.Count > 0
                    ? string.Join(", ", carried.ToArray())
                    : "nothing (this weapon carries no Forge features)")
                : "nothing (\"inherit\" is off)";

            Log.LogInfo(
                cfg.fileName + ": wingman copy built from '" + source.name +
                "' - inherited " + inherited +
                "; projectile prefab " +
                (forkedPrefab ? "forked" : "not forked (hitscan or none)") +
                "; then this gadget's own blocks were applied over the top" +
                (cfg.damageScale != 1f
                    ? "; damage x" + cfg.damageScale.ToString("0.##")
                    : "") + ".");

            if (_saidBuilt)
                return;

            _saidBuilt = true;

            Log.LogInfo(
                "wingman: that line is printed every time you change weapon, " +
                "and it is the one to read first if the wingman shots look " +
                "wrong - it says which layer supplied what. Your own weapon is " +
                "a different asset and is never modified.");
        }
    }
}
