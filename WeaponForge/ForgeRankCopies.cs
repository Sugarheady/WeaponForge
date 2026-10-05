using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace WeaponForge
{
    // ONE RANKED GUN = ONE COPY, with its own XP, its own unlocks and its own
    // weapon asset.
    //
    // ★ R19 TESTS 252 AND 256. Rank was keyed `(ship, weapon FILE)`, so every
    // copy of one file shared one climb: "Spawning a new prospector doesn't give
    // me a new weapon with zero experience, it gives me the old weapon with the
    // old total experience. Its being shared and it shouldnt," and "Both weapons
    // level exactly at the same time even though i started one at level 2 and a
    // new weapon at 0." The old header called that "a constraint rather than a
    // choice". It was a choice, and it was the wrong one.
    //
    // ★ THE SEAM, measured rather than hoped for. `WeaponModule.WeaponData` and
    // `WeaponBasedActiveModule.WeaponData` are PER-INSTANCE settable properties,
    // and the game builds the gun from them - `ModuleSlotWeaponHolder
    // .RecreateWeapon(weaponModule.WeaponData)` for a weapon slot, and
    // `WeaponFactory.Create(this.WeaponData, ...)` inside
    // `WeaponBasedActiveModule.OnContainingClusterRefreshed` for a gadget. So a
    // copy gets its OWN WeaponData the moment the game is about to build its
    // gun, and every Forge registry follows it there (`ForgeWeaponAlias`).
    // After that, "which copy fired this?" is just `WeaponBase.TemplateData`.
    //
    // Nothing downstream can tell the clone from the file's weapon except by
    // asking here, which is the point: stats, unlocks, the card and the save
    // all key on the copy now.
    //
    // ★ A LIVE MODULE IS NEVER DUPLICATED by the game - measured: every
    // `DeepCopy` in the decompile starts from a ModuleData asset (shop, loot,
    // the debug menu, a restore). So the Module instance is a safe identity for
    // the whole time it exists, including in the vault and on the ground.
    public static class ForgeRankCopies
    {
        private static readonly ManualLogSource Log =
            ForgeLog.Source("WeaponForge.Rank");

        public class Copy
        {
            public int id;                 // per run - log lines and HUD keys
            public Module module;          // the copy itself
            public WeaponData source;      // the file's weapon asset
            public WeaponData clone;       // this copy's own weapon
            public GameObject prefabClone;
            public ForgeRank.Config cfg;
            public ForgeRank.Progress p;
            public int shipId;             // last ship seen carrying it

            // When this copy's owed choice set joined the line - the queue
            // puts out the gun that has waited longest. Per run, not saved:
            // a copy handed back by a save has 0 and goes first.
            public float owedSince;

            public string Key
            {
                get { return "rank#" + id; }
            }
        }

        private static readonly Dictionary<Module, Copy> _byModule =
            new Dictionary<Module, Copy>();

        private static readonly Dictionary<WeaponData, Copy> _byClone =
            new Dictionary<WeaponData, Copy>();

        private static int _nextId = 1;
        private static bool _saidFirst;

        public static IEnumerable<Copy> All()
        {
            return _byModule.Values;
        }

        // ------------------------------------------------------------------
        // Lookup
        // ------------------------------------------------------------------

        public static bool TryGet(Module module, out Copy c)
        {
            c = null;

            return module != null && _byModule.TryGetValue(module, out c);
        }

        // Which copy a weapon belongs to. A WINGMAN's weapon is a clone of the
        // gun it mirrors, so its copy is that gun's - which is what makes a
        // wingman's damage rank the gun you are carrying (`fromWingmen`).
        public static bool TryGet(WeaponData td, out Copy c)
        {
            c = null;

            if (td == null)
                return false;

            if (_byClone.TryGetValue(td, out c))
                return true;

            WeaponData mirrored = ForgeWingmanWeapon.SourceOf(td);

            return mirrored != null && _byClone.TryGetValue(mirrored, out c);
        }

        public static bool TryGet(WeaponBase weapon, out Copy c)
        {
            c = null;

            return weapon != null && TryGet(weapon.TemplateData, out c);
        }

        // ------------------------------------------------------------------
        // Making a copy
        // ------------------------------------------------------------------

        // Called just before the game builds a gun from `module`. A module
        // whose weapon is ranked gets its own WeaponData here, once; after
        // that this only makes sure the module still points at it.
        public static void Ensure(Module module)
        {
            if (!ForgeRank.Any || module == null)
                return;

            WeaponData wd = WeaponOf(module);

            if (wd == null)
                return;

            Copy c;

            if (_byModule.TryGetValue(module, out c))
            {
                // Adopted from a save before its gun existed: the progress is
                // here, the weapon is not yet.
                if (c.clone == null)
                    Build(c, c.source ?? wd);

                if (c.clone != null && !ReferenceEquals(wd, c.clone))
                    SetWeapon(module, c.clone);

                return;
            }

            // A clone that belongs to no module we know is left alone - it
            // cannot happen by the game's own paths (see the header).
            if (_byClone.ContainsKey(wd))
                return;

            ForgeRank.Config cfg;

            if (!ForgeRank.TryGet(wd, out cfg) || cfg == null || !cfg.enabled)
                return;

            c = new Copy
            {
                id = _nextId++,
                module = module,
                source = wd,
                cfg = cfg,
                p = new ForgeRank.Progress()
            };

            _byModule[module] = c;

            Build(c, wd);
        }

        // The load path: this module had a rank in the save. Its gun is built
        // later (when the ship binds to its grid), and Ensure finishes the job.
        public static Copy Adopt(Module module, ForgeRank.Progress p)
        {
            if (module == null || p == null)
                return null;

            WeaponData wd = WeaponOf(module);
            ForgeRank.Config cfg;

            if (wd == null || !ForgeRank.TryGet(wd, out cfg) || cfg == null)
                return null;

            Copy c;

            if (_byModule.TryGetValue(module, out c))
            {
                c.p = p;
                return c;
            }

            c = new Copy
            {
                id = _nextId++,
                module = module,
                source = wd,
                cfg = cfg,
                p = p
            };

            _byModule[module] = c;

            return c;
        }

        private static void Build(Copy c, WeaponData source)
        {
            try
            {
                WeaponData clone = UnityEngine.Object.Instantiate(source);
                clone.name = "Forge Rank " + source.name + " #" + c.id;
                clone.hideFlags = HideFlags.None;

                // Its own projectile prefab from the start, for the reason the
                // wingman copy has one: an unlock edits prefabs, and a copy's
                // unlock must never reach another copy's shots.
                GameObject prefab = ForgeWingmanWeapon.ClonePrefabOf(clone);

                // Every Forge registration the file's weapon has lands on the
                // clone - rank itself included, so `ForgeRank.TryGet(clone)` is
                // the same Config and nothing that asks has to know.
                ForgeWeaponAlias.Inherit(source, clone);

                c.source = source;
                c.clone = clone;
                c.prefabClone = prefab;

                _byClone[clone] = c;

                SetWeapon(c.module, clone);

                // A copy handed back by a save carries its draws; put them on
                // its new weapon now, before the game builds the gun from it.
                if (c.p != null && c.p.draws.Count > 0)
                    Rebuild(c, false);

                if (!_saidFirst)
                {
                    _saidFirst = true;

                    Log.LogInfo(
                        c.cfg.fileName + ": rank - this gun now has its OWN " +
                        "rank, XP and unlocks. Every copy of a ranked weapon " +
                        "climbs on its own; a second one you find or buy starts " +
                        "at rank 0. Said once per run.");
                }
            }
            catch (Exception e)
            {
                // A copy that cannot be made costs its own rank and nothing
                // else: the module keeps the file's weapon and fires normally.
                Log.LogError(
                    "rank: could not give '" + source.name + "' its own copy, " +
                    "so this gun will not rank this run. " + e);
            }
        }

        // ------------------------------------------------------------------
        // Unlocks
        // ------------------------------------------------------------------

        // Switch on everything this copy has drawn, deepen repeats, and re-run
        // the feature chain against ITS weapon. `refreshGun` rebuilds the live
        // gun afterwards, because a WeaponBase copies its projectile prefab
        // when it is built (`ProjectileWeapon` sets `ProjectilePrefab` in its
        // constructor) and an unlock that forks the prefab would otherwise not
        // reach the gun you are holding until the next grid change.
        public static bool Rebuild(Copy c, bool refreshGun)
        {
            if (c == null || c.clone == null || c.cfg == null || c.cfg.source == null)
                return false;

            if (c.p.json == null)
                c.p.json = (JObject)c.cfg.source.DeepClone();

            if (!ForgeRankPatch.Features(c.cfg, c.p))
                return false;

            // ★★ THE GUARD THAT MAKES AN UNLOCK AN UNLOCK. `ApplyRank` runs
            // FIRST in the chain and its job is to switch every pooled block
            // OFF and register a fresh Config - correct at build time, and
            // exactly wrong here: it turned the feature just drawn back off
            // before any of the fifty calls below could read it, and replaced
            // the Config the progress was tied to. R19 256: "The orbs vanish
            // but dont upgrade the weapons." They could not have.
            ForgeRank.Rebuilding = true;

            try
            {
                WeaponBuilder.ApplyFeaturesTo(
                    c.clone, c.p.json, c.cfg.isGadget, c.cfg.slot, c.cfg.fileName);
            }
            finally
            {
                ForgeRank.Rebuilding = false;
            }

            // Wingmen mirroring this gun built THEIR copies from the old
            // version of it; drop those so the next mirrored shot rebuilds.
            ForgeWingmanWeapon.ForgetSource(c.clone);

            if (refreshGun)
                RefreshGun(c);

            return true;
        }

        // ---- deferred to the next frame ---------------------------------
        private static readonly List<Copy> _later = new List<Copy>();

        public static void RebuildNextFrame(Copy c)
        {
            if (c == null || _later.Contains(c))
                return;

            _later.Add(c);
            ForgeRankClock.Ensure();
        }

        // Returns whether anything is left to do.
        internal static bool RunLater()
        {
            if (_later.Count == 0)
                return false;

            var now = new List<Copy>(_later);
            _later.Clear();

            foreach (Copy c in now)
            {
                try
                {
                    Rebuild(c, true);
                }
                catch (Exception e)
                {
                    Log.LogWarning("rank unlock skipped: " + e.Message);
                }
            }

            return _later.Count > 0;
        }

        // Rebuild the live gun built from this copy, the way the game does on
        // a grid change - so the new gun is equipped through the game's own
        // path and `ForgeRankPatch.OnEquip` puts the stats back on it.
        private static FieldInfo _holderCluster;
        private static MethodInfo _holderRefresh;

        private static void RefreshGun(Copy c)
        {
            try
            {
                if (c.module is WeaponBasedActiveModule)
                {
                    IModuleCluster cluster = ClusterOf(c.module);

                    if (cluster != null)
                        c.module.OnContainingClusterRefreshed(cluster);

                    return;
                }

                if (_holderCluster == null)
                    _holderCluster = AccessTools.Field(
                        typeof(ModuleSlotWeaponHolder), "moduleCluster");

                if (_holderRefresh == null)
                    _holderRefresh = AccessTools.Method(
                        typeof(ModuleSlotWeaponHolder), "RefreshWeapon");

                if (_holderCluster == null || _holderRefresh == null)
                    return;

                foreach (ModuleSlotWeaponHolder h in
                         UnityEngine.Object.FindObjectsByType<ModuleSlotWeaponHolder>(
                             FindObjectsSortMode.None))
                {
                    var cluster = _holderCluster.GetValue(h) as IModuleCluster;

                    if (cluster != null && cluster.HasMainModule &&
                        ReferenceEquals(cluster.MainModule, c.module))
                    {
                        _holderRefresh.Invoke(h, null);
                    }
                }
            }
            catch (Exception e)
            {
                Log.LogWarning(
                    c.cfg.fileName + ": rank - the unlock is recorded, but the " +
                    "gun could not be rebuilt to show it (" + e.GetType().Name +
                    "). It will on your next grid change.");
            }
        }

        private static IModuleCluster ClusterOf(Module module)
        {
            foreach (ModuleGridOwner o in
                     UnityEngine.Object.FindObjectsByType<ModuleGridOwner>(
                         FindObjectsSortMode.None))
            {
                if (o == null || o.ModuleGrid == null)
                    continue;

                foreach (IModuleCluster cl in o.ModuleGrid.GetAllClusters())
                {
                    if (cl != null && cl.HasMainModule &&
                        ReferenceEquals(cl.MainModule, module))
                    {
                        return cl;
                    }
                }
            }

            return null;
        }

        // Every live gun built from this copy: the Shooter holding it, and a
        // gadget's own private weapon. Stats are published per WeaponBase, so
        // this is how a rank up reaches the gun you are holding even when the
        // XP arrived through a wingman's copy of it.
        private static FieldInfo _gadgetWeapon;

        public static List<WeaponBase> LiveGuns(Copy c, Unit ship)
        {
            var guns = new List<WeaponBase>();

            if (c == null || c.clone == null)
                return guns;

            try
            {
                if (ship != null)
                {
                    foreach (Shooter s in ship.GetComponentsInChildren<Shooter>(true))
                    {
                        WeaponBase w = (s != null) ? s.Weapon : null;

                        if (w != null && ReferenceEquals(w.TemplateData, c.clone) &&
                            !guns.Contains(w))
                        {
                            guns.Add(w);
                        }
                    }
                }

                if (c.module is WeaponBasedActiveModule)
                {
                    if (_gadgetWeapon == null)
                        _gadgetWeapon = AccessTools.Field(
                            typeof(WeaponBasedActiveModule), "weapon");

                    var w = (_gadgetWeapon != null)
                        ? _gadgetWeapon.GetValue(c.module) as WeaponBase
                        : null;

                    if (w != null && !guns.Contains(w))
                        guns.Add(w);
                }
            }
            catch (Exception)
            {
            }

            return guns;
        }

        // ------------------------------------------------------------------
        // The module's weapon, whichever kind of module it is
        // ------------------------------------------------------------------

        public static WeaponData WeaponOf(Module module)
        {
            var wm = module as WeaponModule;

            if (wm != null)
                return wm.WeaponData;

            var wb = module as WeaponBasedActiveModule;

            return (wb != null) ? wb.WeaponData : null;
        }

        private static void SetWeapon(Module module, WeaponData data)
        {
            var wm = module as WeaponModule;

            if (wm != null)
            {
                wm.WeaponData = data;
                return;
            }

            var wb = module as WeaponBasedActiveModule;

            if (wb != null)
                wb.WeaponData = data;
        }

        // ------------------------------------------------------------------
        // Run entry
        // ------------------------------------------------------------------

        // Per-run state: the modules of the last run are gone, and so are the
        // weapons made for them. Called from ForgeRank.Reset, which
        // ForgeBurnResetPatch reaches at both run entries - on a continue that
        // is the Load PREFIX, so the save's copies are adopted after this.
        public static void Reset()
        {
            foreach (Copy c in _byModule.Values)
            {
                try
                {
                    if (c.clone != null)
                    {
                        ForgeWingmanWeapon.ForgetSource(c.clone);
                        ForgeWeaponAlias.Forget(c.clone);
                    }

                    if (c.prefabClone != null)
                        UnityEngine.Object.Destroy(c.prefabClone);

                    if (c.clone != null)
                        UnityEngine.Object.Destroy(c.clone);
                }
                catch (Exception)
                {
                }
            }

            _byModule.Clear();
            _byClone.Clear();
            _later.Clear();
            _nextId = 1;
            _saidFirst = false;
        }

        // ------------------------------------------------------------------
        // The two moments the game builds a gun
        // ------------------------------------------------------------------

        // `RefreshWeapon` is private and reads `moduleCluster.MainModule` - so
        // the prefix reaches the same module the game is about to build from.
        [HarmonyPatch(typeof(ModuleSlotWeaponHolder), "RefreshWeapon")]
        public class OnHolderRefresh
        {
            static void Prefix(ModuleSlotWeaponHolder __instance)
            {
                if (!ForgeRank.Any)
                    return;

                try
                {
                    if (_holderCluster == null)
                        _holderCluster = AccessTools.Field(
                            typeof(ModuleSlotWeaponHolder), "moduleCluster");

                    var cluster = (_holderCluster != null)
                        ? _holderCluster.GetValue(__instance) as IModuleCluster
                        : null;

                    if (cluster != null && cluster.HasMainModule)
                        Ensure(cluster.MainModule);
                }
                catch (Exception e)
                {
                    Log.LogWarning("rank: copy check skipped: " + e.Message);
                }
            }
        }

        [HarmonyPatch(typeof(WeaponBasedActiveModule), "OnContainingClusterRefreshed")]
        public class OnGadgetRefresh
        {
            static void Prefix(WeaponBasedActiveModule __instance)
            {
                if (!ForgeRank.Any)
                    return;

                try
                {
                    Ensure(__instance);
                }
                catch (Exception e)
                {
                    Log.LogWarning("rank: copy check skipped: " + e.Message);
                }
            }
        }
    }
    // The one per-frame tick rank needs: an unlock waiting for a frame where
    // no gun is mid-shot. Created on demand and gone as soon as it is idle.
    internal class ForgeRankClock : MonoBehaviour
    {
        private static ForgeRankClock _one;

        public static void Ensure()
        {
            if (_one != null)
                return;

            var go = new GameObject("ForgeRankClock");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            _one = go.AddComponent<ForgeRankClock>();
        }

        private void Update()
        {
            bool more;

            try
            {
                more = ForgeRankCopies.RunLater();
            }
            catch (Exception)
            {
                more = false;
            }

            if (!more)
            {
                _one = null;
                Destroy(gameObject);
            }
        }
    }
}
