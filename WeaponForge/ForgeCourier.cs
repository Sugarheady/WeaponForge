using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace WeaponForge
{
    // COURIER - a gun whose trigger launches a DRONE instead of its shot
    // (built 2026-10-09; his idea from R21 part 1, inspired by Undertale's
    // Gaster Blaster). The drone flies to your cursor, winds up, fires the
    // gun from there - or a weapon the file names - as many times as it has
    // shots, and fades. Design and every decision:
    // `Docs/CURSOR DRONE - CONCEPT.md`; what the build chose is its section 11.
    //
    // The pieces:
    //   ForgeCourierRules  the arithmetic and the words (no Unity; the probe)
    //   ForgeCourier       the settings, the registry, and the runtime help
    //                      below: the live gun, beam copies, named payloads
    //   ForgeCourierDrone  one drone, start to finish
    //   ForgeCourierPatch  the launch (WeaponBase.Fire), live-gun tracking,
    //                      the launcher's own beam held off, decoys out of a
    //                      save
    //
    // ★ THE SEAM IS `WeaponBase.Fire`, NOT `DoShoot`. Every pull enters a gun
    // through Fire (the ship's Shooter, a gadget, a wingman's copy). DoShoot
    // carries nine of this mod's hooks, and Harmony runs EVERY prefix and
    // postfix even when one skips the original - so skipping DoShoot at the
    // launch would still step a spiral, swing a swipe and fire the wingmen.
    // Skipping Fire means none of them sees the launch; the drone then calls
    // the gun's own public DoShoot, so every one of them sees ONE real volley,
    // fired from the drone. That also settles a burst: Fire runs the burst
    // tail later, outside any scope, where DoShoot is one volley and done.
    public static class ForgeCourier
    {
        private static readonly ManualLogSource Log =
            ForgeLog.Source("WeaponForge.Courier");

        public class Config
        {
            public string fileName = "";

            // ---- what it fires ----------------------------------------
            // Empty = the gun's own shot (his default). A name = that weapon
            // file, built with the gun's modules, its kills paid to the gun.
            public string weapon;
            public int shots = 1;
            public float interval;              // 0 = the gun's own rhythm
            public float beamTime = 0.75f;      // a beam: seconds per shot

            // A beam's damage ticks a second while a drone holds it. Its OWN
            // number because on a courier gun `fireRate` is how often the
            // trigger LAUNCHES - a laser courier wants about 1 - while the
            // stock laser ticks 13 times a second (Caps Laser, measured).
            public float beamRate = 12f;

            // ---- the trip -----------------------------------------------
            public float speed = 30f;
            public float reach;                 // 0 = the gun's range
            public bool follow;
            public bool overWalls = true;
            public ForgeCourierRules.Entry entry = ForgeCourierRules.Entry.Fly;

            // ---- aim ----------------------------------------------------
            public float windup = 0.25f;
            public ForgeCourierRules.Aim aim = ForgeCourierRules.Aim.Outward;
            public bool aimSet;
            public float snap;
            public ForgeCourierRules.Pattern pattern = ForgeCourierRules.Pattern.Single;
            public float turn;
            public ForgeCourierRules.Formation formation = ForgeCourierRules.Formation.One;

            // ---- boosted: more drones a pull (R21 part 4, his answers) ----
            // "+1 drone a level", every courier boostable by default (the
            // builder switches canBeBoosted on - ApplyBoostability); 0 keeps a
            // file at one drone however boosted. Several drones stand SIDE BY
            // SIDE ACROSS YOUR AIM, `spacing` apart; a ring formation adds
            // them to the ring instead.
            public int dronesPerLevel = 1;
            public float spacing = 1.5f;

            // A WINGMAN's copy of this gun lands its drone offset from your
            // cursor the way that wingman sits beside your ship ("mirror",
            // his answer), or on top of yours ("stack", the old way).
            public bool mirrorWingmen = true;

            // ---- leaving ------------------------------------------------
            public ForgeCourierRules.Exit exit = ForgeCourierRules.Exit.Fade;
            public float fadeTime = 0.3f;

            // ---- its body -----------------------------------------------
            public ForgeCourierRules.Shootable shootable = ForgeCourierRules.Shootable.Off;
            public float health = 10f;
            public bool contact;
            public bool faceAim = true;

            // The wingman's art resolver does the look (custom sprite, stock
            // sprite or a borrowed prefab), so its fields live on a wingman
            // Config built once at build time.
            public ForgeWingman.Config art;

            // ---- the reach ring (his ask: "if the courier drone has a range,
            // then we should probably implement it") --------------------------
            // A courier's reach is as invisible as bloom's placement range: a
            // cursor past it is pulled in, and nothing on screen says where
            // that happens. Bloom's ring, driven by the same code with this
            // reach as its radius; the same keys and defaults (off until asked
            // for, the same reasoning: an always-on circle is clutter).
            public ForgeBloom.RingShow ring = ForgeBloom.RingShow.Off;
            public string ringSprite = "";
            public Color ringColor = new Color(0.35f, 0.85f, 1f, 0.30f);
            public float ringFade;
            public float ringOpacity = 1f;
            public float ringWidth;

            // ---- sound --------------------------------------------------
            public string launchSfx;            // a guid, or null for none

            // A BEAM launcher's own held sounds, moved to the drone: the ship
            // must not hum while nothing fires from it (build time).
            public string beamHum;
            public string beamWarmup;
            public string beamRelease;

            // The aim the ring formation defaults to.
            public ForgeCourierRules.Aim AimFor(bool ring)
            {
                if (ring && !aimSet)
                    return ForgeCourierRules.Aim.Centre;

                return aim;
            }
        }

        // Build-time registration, keyed by the weapon asset. NOT cleared at
        // run entry (see ForgeCrit.Reset for that trap). Swept by
        // ForgeWeaponAlias, so a wingman's copy of a courier gun is a courier
        // gun too, and launches drones from the companion.
        private static readonly Dictionary<WeaponData, Config> _weapons =
            new Dictionary<WeaponData, Config>();

        public static bool Any { get { return _weapons.Count > 0; } }

        public static void Register(WeaponData weapon, Config cfg)
        {
            if (weapon != null && cfg != null)
                _weapons[weapon] = cfg;
        }

        public static bool TryGet(WeaponData weapon, out Config cfg)
        {
            cfg = null;
            return weapon != null && _weapons.TryGetValue(weapon, out cfg) && cfg != null;
        }

        // ------------------------------------------------------------------
        // Runtime state - per run, cleared by Reset
        // ------------------------------------------------------------------

        // > 0 while a drone fires: the wingman mirror stands down (it already
        // ran at the launch) and the launch intercept lets the shot through.
        public static int Firing;

        // Disposed weapon objects. Weak, so it never grows: a dead gun is
        // forgotten when nothing holds it any more.
        private static readonly ConditionalWeakTable<WeaponBase, object> _dead =
            new ConditionalWeakTable<WeaponBase, object>();

        private static readonly object Mark = new object();

        // Weapon objects WE built (beam copies, named payloads). They are never
        // launchers, and their beams are ours to draw.
        private static readonly HashSet<WeaponBase> _ours = new HashSet<WeaponBase>();

        // Every live drone, oldest first, for the per-ship limit.
        private static readonly List<ForgeCourierDrone> _drones =
            new List<ForgeCourierDrone>();

        // A named payload per gun object, rebuilt when the gun is.
        private static readonly Dictionary<WeaponBase, WeaponBase> _payloads =
            new Dictionary<WeaponBase, WeaponBase>();

        private static readonly Dictionary<string, WeaponData> _named =
            new Dictionary<string, WeaponData>();

        // ★ A LIMIT THAT SPEAKS. His answer is "no cap"; this is a performance
        // backstop only, and reaching it retires the OLDEST drone of that ship
        // (it fades) and says so once - never a shot that silently does not
        // happen, which is what ForgeTurret's own 64 does.
        public const int MaxPerShip = 64;
        public const int MaxDecoysPerShip = 16;

        private static bool _saidLimit;
        private static bool _saidNoOwner;
        private static bool _saidGunGone;
        private static bool _saidLaunchFail;
        private static readonly HashSet<string> _saidNamed = new HashSet<string>();

        public static void Reset()
        {
            Firing = 0;

            foreach (var kv in _payloads)
            {
                try { if (kv.Value != null) kv.Value.Dispose(); } catch (Exception) { }
            }

            _ours.Clear();
            _drones.Clear();
            _payloads.Clear();
            _named.Clear();
            ForgeKillsPatch.ClearStandIns();
        }

        // ★ ONE ANSWER TO "HOW FAR": the drone's flight AND the reach ring
        // read this, so the circle on screen is exactly where a drone can go.
        // The file's `reach`, else the gun's own (live) range, else 20 - a
        // grenade's range is 0.
        public static float ReachOf(WeaponBase gun, Config cfg)
        {
            if (cfg != null && cfg.reach > 0f)
                return cfg.reach;

            var r = gun as IHasRangeProperty;
            float range = (r != null) ? r.Range : 0f;

            return (range > 0.5f) ? range : 20f;
        }

        public static bool IsOurs(WeaponBase w)
        {
            return w != null && _ours.Contains(w);
        }

        // A courier gun the player (or anything) carries: registered, and not
        // one of the weapon objects we built for a drone.
        public static bool IsLauncher(WeaponBase w)
        {
            Config cfg;
            return w != null && !IsOurs(w) && TryGet(w.TemplateData, out cfg);
        }

        public static void MarkDead(WeaponBase w)
        {
            if (w == null)
                return;

            try { _dead.GetValue(w, k => Mark); } catch (Exception) { }

            WeaponBase payload;

            if (_payloads.TryGetValue(w, out payload))
            {
                _payloads.Remove(w);
                Release(payload);
            }
        }

        private static bool IsDead(WeaponBase w)
        {
            object o;
            return w == null || _dead.TryGetValue(w, out o);
        }

        // ------------------------------------------------------------------
        // The launch - from the WeaponBase.Fire prefix
        // ------------------------------------------------------------------

        // Returns true when it launched (the original Fire is skipped). False
        // fires the gun normally - only ever for a gun nobody owns.
        public static bool Launch(WeaponBase gun, IBarrel barrel, Config cfg)
        {
            Unit owner = gun.Owner;

            if (owner == null)
            {
                if (!_saidNoOwner)
                {
                    _saidNoOwner = true;
                    Log.LogInfo(
                        cfg.fileName + ": a courier gun fired with no owner (a " +
                        "sub-emitter or a turret firing it) fires normally - a " +
                        "drone needs someone's cursor to fly to. Said once.");
                }

                return false;
            }

            int burst = Mathf.Max(0, gun.BurstSize);
            bool ring = cfg.formation == ForgeCourierRules.Formation.Ring;

            // ★ BOOSTED: +dronesPerLevel a level (his "+1 drone a level"). A
            // ring grows by the same number; one drone becomes a firing line.
            // A wingman's copy is in no weapon slot, so it reads level 1 and
            // sends its own one drone (mirrored beside yours, below).
            int level = ForgeSwarm.LevelOf(gun);
            int baseCount = ring ? Mathf.Max(1, Mathf.RoundToInt(gun.ProjectileCount)) : 1;
            int count = ForgeCourierRules.DronesFor(
                baseCount, cfg.dronesPerLevel, level,
                ring ? Mathf.Max(baseCount, ForgeCourierRules.MaxLine) : ForgeCourierRules.MaxLine);

            NoteBoost(cfg, level, count);

            // A WINGMAN's drone lands where that wingman sits beside your ship
            // (his answer, "mirror"): the offset from the ship to the barrel it
            // fired from. Your own gun's barrel is on the ship, so no shift.
            Vector2 shift = Vector2.zero;

            if (cfg.mirrorWingmen && ForgeWingmanWeapon.IsCopy(gun.TemplateData) && barrel != null)
            {
                try { shift = barrel.Position - (Vector2)owner.transform.position; }
                catch (Exception) { shift = Vector2.zero; }
            }

            for (int b = 0; b < burst; b++)
            {
                float delay = b * Mathf.Max(0f, gun.BurstDelay);

                for (int i = 0; i < count; i++)
                {
                    Make(gun, barrel, owner, cfg, delay, ring, i, count,
                         /* first of this burst shot */ i == 0, shift);
                }
            }

            return true;
        }

        private static bool _saidBoost;

        // Said once per run, the first time a boosted courier sends more than
        // one drone - so "is the BoosterCore doing anything" has an answer.
        private static void NoteBoost(Config cfg, int level, int count)
        {
            if (_saidBoost || level <= 1 || count <= 1)
                return;

            _saidBoost = true;
            Log.LogInfo(
                cfg.fileName + ": boosted to level " + level + ", so each pull sends " +
                count + " drones (" + (cfg.formation == ForgeCourierRules.Formation.Ring
                    ? "added to the ring"
                    : "side by side across your aim, " + cfg.spacing.ToString("0.##") + " apart") +
                "). Said once per run.");
        }

        private static void Make(
            WeaponBase gun, IBarrel barrel, Unit owner, Config cfg,
            float delay, bool ring, int index, int count, bool first, Vector2 shift)
        {
            try
            {
                Limit(owner, cfg);

                var go = new GameObject("Forge Courier");
                var d = go.AddComponent<ForgeCourierDrone>();

                d.Begin(cfg, gun, barrel, owner, delay, ring, index, count, first, shift);

                _drones.Add(d);
            }
            catch (Exception e)
            {
                if (!_saidLaunchFail)
                {
                    _saidLaunchFail = true;
                    Log.LogError(cfg.fileName + ": a courier drone could not be " +
                                 "launched: " + e);
                }
            }
        }

        public static void Forget(ForgeCourierDrone d)
        {
            _drones.Remove(d);
        }

        // The oldest drone of this ship leaves when the backstop is reached.
        private static void Limit(Unit owner, Config cfg)
        {
            bool decoy = cfg.shootable == ForgeCourierRules.Shootable.Decoy;
            int cap = decoy ? MaxDecoysPerShip : MaxPerShip;
            int mine = 0;
            ForgeCourierDrone oldest = null;

            for (int i = 0; i < _drones.Count; i++)
            {
                ForgeCourierDrone d = _drones[i];

                if (d == null || !d.Counts || !ReferenceEquals(d.Owner, owner))
                    continue;

                if (decoy && !d.IsDecoy)
                    continue;

                mine++;

                if (oldest == null)
                    oldest = d;
            }

            if (mine < cap || oldest == null)
                return;

            oldest.Retire();

            if (!_saidLimit)
            {
                _saidLimit = true;
                Log.LogInfo(
                    cfg.fileName + ": " + cap + (decoy ? " decoy" : "") +
                    " courier drones were out at once for one ship, so the " +
                    "oldest left early to make room. This is a performance " +
                    "backstop, not a cap - lower fireRate or shots if it " +
                    "happens often. Said once per run.");
            }
        }

        // ------------------------------------------------------------------
        // The gun, at the moment a drone fires
        // ------------------------------------------------------------------

        // ★ A GUN'S WEAPON OBJECT IS REBUILT whenever a module is slotted or
        // removed, so the object a drone launched with may be dead by the time
        // it fires. Ask for the CURRENT one: the same weapon file on the same
        // ship. Null means the gun is gone (swapped, dropped, the ship died) -
        // his answer: the drone fades without firing.
        public static WeaponBase LiveGun(WeaponBase launched, WeaponData data, Unit owner, Config cfg)
        {
            if (owner == null)
                return null;

            if (!IsDead(launched) && launched.Owner != null)
                return launched;

            try
            {
                var ship = owner.GetComponentInChildren<Ship>(true);

                if (ship != null)
                {
                    if (Same(ship.PrimaryWeapon, data))
                        return ship.PrimaryWeapon;

                    if (Same(ship.SecondaryWeapon, data))
                        return ship.SecondaryWeapon;
                }

                foreach (WeaponBase g in Gadgets(owner))
                    if (Same(g, data))
                        return g;
            }
            catch (Exception)
            {
                // A gun we cannot find is a gun that is gone.
            }

            if (!_saidGunGone)
            {
                _saidGunGone = true;
                Log.LogInfo(
                    cfg.fileName + ": a courier drone's gun is no longer " +
                    "carried (swapped, dropped, or the ship is gone), so it " +
                    "faded without firing - it never fires a gun you do not " +
                    "have. Said once per run.");
            }

            return null;
        }

        private static bool Same(WeaponBase w, WeaponData data)
        {
            return w != null && !IsDead(w) && ReferenceEquals(w.TemplateData, data);
        }

        private static AccessTools.FieldRef<WeaponBasedActiveModule, WeaponBase> _gadgetWeapon;
        private static bool _lookedGadget;

        private static WeaponBase GadgetWeapon(WeaponBasedActiveModule m)
        {
            if (!_lookedGadget)
            {
                _lookedGadget = true;

                try
                {
                    _gadgetWeapon = AccessTools.FieldRefAccess<
                        WeaponBasedActiveModule, WeaponBase>("weapon");
                }
                catch (Exception e)
                {
                    _gadgetWeapon = null;
                    Log.LogWarning(
                        "courier: could not reach a gadget's weapon (" +
                        e.Message + ") - a courier gadget whose gun is rebuilt " +
                        "while its drones fly will fade them instead.");
                }
            }

            return (_gadgetWeapon != null && m != null) ? _gadgetWeapon(m) : null;
        }

        private static IEnumerable<WeaponBase> Gadgets(Unit owner)
        {
            var gridOwner = owner.GetComponentInChildren<ModuleGridOwner>(true);

            if (gridOwner == null || gridOwner.ModuleGrid == null)
                yield break;

            foreach (IModuleCluster c in gridOwner.ModuleGrid.GetAllClusters())
            {
                if (c == null || !c.HasMainModule)
                    continue;

                var m = c.MainModule as WeaponBasedActiveModule;

                if (m == null)
                    continue;

                WeaponBase w = GadgetWeapon(m);

                if (w != null)
                    yield return w;
            }
        }

        // The modules slotted round THIS gun - what makes a module on your gun
        // reach a drone's beam copy or a named payload. A module's effect is
        // applied to a built weapon object by WeaponFactory.Create and never
        // written back to the weapon file (the wingman lesson, R16).
        public static IEnumerable<Module> ModulesOf(WeaponBase gun)
        {
            if (gun == null || gun.Owner == null)
                return null;

            try
            {
                Unit u = gun.Owner;
                var gridOwner = u.GetComponentInChildren<ModuleGridOwner>(true);

                if (gridOwner == null || gridOwner.ModuleGrid == null)
                    return null;

                var ship = u.GetComponentInChildren<Ship>(true);

                if (ship != null && ReferenceEquals(gun, ship.PrimaryWeapon))
                    return Modules(gridOwner.ModuleGrid.GetCluster(ClusterType.PrimaryWeapon));

                if (ship != null && ReferenceEquals(gun, ship.SecondaryWeapon))
                    return Modules(gridOwner.ModuleGrid.GetCluster(ClusterType.SecondaryWeapon));

                foreach (IModuleCluster c in gridOwner.ModuleGrid.GetAllClusters())
                {
                    if (c == null || !c.HasMainModule)
                        continue;

                    var m = c.MainModule as WeaponBasedActiveModule;

                    if (m != null && ReferenceEquals(GadgetWeapon(m), gun))
                        return Modules(c);
                }
            }
            catch (Exception)
            {
                // A grid we cannot read costs the upgrades, not the drone.
            }

            return null;
        }

        private static IEnumerable<Module> Modules(IModuleCluster c)
        {
            return (c != null) ? c.ConnectedAndPoweredModules : null;
        }

        // ------------------------------------------------------------------
        // What the drone fires
        // ------------------------------------------------------------------

        private static System.Reflection.MethodInfo _ownerSetter;
        private static bool _lookedOwner;

        // Owner WITHOUT `Equip`. Equip carries three of this mod's hooks
        // (contact, rank, and ours), and rank's would treat a drone's copy as
        // the gun itself. The copy reads the live gun's numbers instead, which
        // already carry rank's and every module's.
        public static void SetOwner(WeaponBase w, Unit owner)
        {
            if (!_lookedOwner)
            {
                _lookedOwner = true;

                try { _ownerSetter = AccessTools.PropertySetter(typeof(WeaponBase), "Owner"); }
                catch (Exception) { _ownerSetter = null; }
            }

            if (_ownerSetter != null)
            {
                _ownerSetter.Invoke(w, new object[] { owner });
            }
            else
            {
                // Last resort: Equip's hooks all tolerate a stranger.
                w.Equip(owner);
            }
        }

        // The named payload's weapon file, looked up when first fired (so the
        // file order never matters - the turret's rule).
        public static WeaponData NamedData(Config cfg)
        {
            if (string.IsNullOrEmpty(cfg.weapon))
                return null;

            WeaponData data;

            if (_named.TryGetValue(cfg.weapon, out data))
                return data;

            data = ForgeWeaponLookup.Find(cfg.weapon);

            if (data == null && _saidNamed.Add(cfg.weapon))
                ForgeWeaponLookup.LogMiss("courier \"weapon\"", cfg.weapon);

            _named[cfg.weapon] = data;
            return data;
        }

        // A named BULLET payload: one per gun object, with the gun's modules,
        // its kills credited to the gun.
        public static WeaponBase Payload(WeaponBase gun, WeaponData data)
        {
            WeaponBase have;

            if (_payloads.TryGetValue(gun, out have) && have != null &&
                ReferenceEquals(have.TemplateData, data))
            {
                return have;
            }

            if (have != null)
                Release(have);

            WeaponBase made = new WeaponFactory().Create(data, ModulesOf(gun));

            SetOwner(made, gun.Owner);
            made.InitializeVisuals();

            _ours.Add(made);
            ForgeKillsPatch.CreditTo(made, gun);

            _payloads[gun] = made;
            return made;
        }

        // A BEAM, one per direction a drone fires: a beam's visuals belong to
        // one weapon object (HitscanWeapon.visualsInstances), so two drones on
        // one laser would fight over one beam. Built from the gun's own file
        // (or the named one) with the gun's modules; for the gun's own shot,
        // the LIVE gun's numbers are copied on top, which carry what the
        // modules did plus rank's and every runtime multiplier.
        public static HitscanWeapon Beam(
            WeaponData data, WeaponBase gun, bool own, bool onePellet, float windup)
        {
            var made = new WeaponFactory().Create(data, ModulesOf(gun)) as HitscanWeapon;

            if (made == null)
                return null;

            if (own)
            {
                made.Damage = gun.Damage;
                made.Burn = gun.Burn;
                made.FireRate = gun.FireRate;
                made.ProjectileCount = gun.ProjectileCount;
                made.Spread = gun.Spread;
                made.AngleVariance = gun.AngleVariance;
                made.AngleOffset = gun.AngleOffset;
                made.KnockbackForce = gun.KnockbackForce;
                made.PushForce = gun.PushForce;
                made.BarrelLength = gun.BarrelLength;
                made.DischargeData = gun.DischargeData;
                made.CellConvertData = gun.CellConvertData;

                var live = gun as HitscanWeapon;

                if (live != null)
                {
                    made.Range = live.Range;
                    made.RayWidth = live.RayWidth;
                    made.DamageRepeatDelay = live.DamageRepeatDelay;
                }
            }

            if (onePellet)
                made.ProjectileCount = 1f;

            // The windup IS the warm-up: the stock beams carry a warm-up art,
            // so a drone holding still shows the thin warning beam before it
            // fires - the Gaster charge, from the game's own sprites.
            made.WarmupTime = Mathf.Max(0f, windup);

            SetOwner(made, gun.Owner);
            made.InitializeVisuals();

            _ours.Add(made);
            ForgeKillsPatch.CreditTo(made, gun);

            return made;
        }

        public static void Release(WeaponBase w)
        {
            if (w == null)
                return;

            _ours.Remove(w);
            ForgeKillsPatch.CreditTo(w, null);

            try { w.Dispose(); } catch (Exception) { }
        }

        // ------------------------------------------------------------------
        // Decoys and the save
        // ------------------------------------------------------------------

        // ★ A DECOY IS A REAL GAME ENTITY, AND A SAVE WOULD KEEP IT. Taken
        // mid-flight, it would come back on load as a stray stock drone with
        // its AI on. So before every save each decoy's entity is destroyed
        // there and then (EntityData.Destroy is what SavableEntity.OnDestroy
        // would have called at the end of the frame - too late for a save in
        // this one) and the drone leaves.
        public static void BeforeSave()
        {
            for (int i = _drones.Count - 1; i >= 0; i--)
            {
                ForgeCourierDrone d = _drones[i];

                if (d != null && d.IsDecoy)
                    d.DropFromSave();
            }
        }

        public static int LiveDrones
        {
            get { return _drones.Count; }
        }
    }
}
