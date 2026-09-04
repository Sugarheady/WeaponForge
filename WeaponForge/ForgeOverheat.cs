using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace WeaponForge
{
    // Sustained fire builds heat; too much heat and the gun stops.
    //
    // THE GOOD NEWS, and it is unusually good: `Shooter.Block(object)` and
    // `Unblock(object)` are **public**, and `Shooter.Update` returns
    // immediately when anything is blocking - before the warmup tick, before
    // the continuous sound, before `Shoot`. So a jam is one call and it is
    // complete, rather than the usual "suppress the shot and then chase down
    // the four other things that still happen".
    //
    // THE ONE THING THAT EARLY RETURN COSTS US. `StopContinousSound` lives
    // *after* it, so a weapon already looping its firing sound keeps looping it
    // forever once blocked. Anything that blocks a Shooter has to stop that
    // sound itself. Same shape as the trap noted in CLAUDE.md for charge,
    // arriving from the opposite direction.
    //
    // ONLY THE SHOOTER PATH CAN JAM. Gadgets call `weapon.Fire(...)` directly
    // and never touch a Shooter, so overheat on a gadget would build heat it
    // could never act on. The builder refuses it rather than half-working.
    public static class ForgeOverheat
    {
        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("WeaponForge.Heat");

        // What happens at maximum heat.
        public const int Vent = 0;   // locked until cool - the Halo plasma
        public const int Jam = 1;    // locked for a fixed time
        public const int Slow = 2;   // never locks, just degrades
        public const int None = 3;   // heat is only a trigger for the extras

        public static int ParseResponse(string s)
        {
            switch ((s ?? "").Trim().ToLowerInvariant())
            {
                case "jam":
                case "timed":
                    return Jam;
                case "slow":
                case "degrade":
                case "penalty":
                    return Slow;
                case "none":
                case "off":
                    return None;
                default:
                    return Vent;
            }
        }

        public static string ResponseWord(int r)
        {
            switch (r)
            {
                case Jam: return "jam for a fixed time";
                case Slow: return "degrade the fire rate";
                case None: return "no lockout";
                default: return "vent - locked until fully cool";
            }
        }

        public class Config
        {
            public bool enabled = true;

            public float max = 100f;
            public float perShot = 8f;
            public float perSecond;          // for held/continuous weapons
            public float coolPerSecond = 35f;
            public float coolDelay = 0.4f;

            public int response = Vent;
            public float jamSeconds = 1.5f;
            public float ventTo;             // vent unlocks at or below this
            public float slowestFireRate = 0.3f;

            public float selfDamage;
            public bool ventShot;
            public float ventShotLevel = 1f;
            public bool replacesAmmo;

            // ---- feedback ----
            public bool tintMuzzle = true;   // the default, by request
            public Color hotColor = new Color(1f, 0.35f, 0.16f, 1f);
            public float tintStrength = 1f;  // 0..1, same scale as every tint
            public string overheatSfx = "";
            public string recoverSfx = "";
            public bool showHeat;            // the buff HUD is off by default
            public bool shake;
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

        // The live muzzle flash instances, so the gun can glow as it heats.
        // They are a private List<ParticleSystem> on WeaponBase, rebuilt by
        // InitializeVisuals whenever the projectile count changes - so this is
        // read fresh each time rather than cached.
        private static FieldInfo _muzzleList;
        private static bool _muzzleLooked;

        public static List<ParticleSystem> MuzzlesOf(WeaponBase weapon)
        {
            if (weapon == null)
                return null;

            if (!_muzzleLooked)
            {
                _muzzleLooked = true;
                _muzzleList = AccessTools.Field(
                    typeof(WeaponBase), "muzzleParticleSystemInstances");

                if (_muzzleList == null)
                {
                    Log.LogWarning(
                        "Could not reach the muzzle flash instances, so " +
                        "\"tintMuzzle\" will do nothing. Everything else about " +
                        "overheat still works.");
                }
            }

            if (_muzzleList == null)
                return null;

            try
            {
                return _muzzleList.GetValue(weapon) as List<ParticleSystem>;
            }
            catch (Exception)
            {
                return null;
            }
        }

        // Camera shake, by reflection.
        //
        // ProCamera2D is a third-party assembly the mod does not reference, and
        // referencing it would add a build dependency for one cosmetic call.
        // House rule applies: if the reflection fails, do nothing rather than
        // risk anything. Resolved once and latched either way.
        private static object _shaker;
        private static MethodInfo _shakeMethod;
        private static bool _shakeLooked;

        public static void Shake(object preset)
        {
            if (preset == null)
                return;

            try
            {
                if (!_shakeLooked)
                {
                    _shakeLooked = true;

                    Type t = AccessTools.TypeByName(
                        "Com.LuisPedroFonseca.ProCamera2D.ProCamera2DShake");

                    if (t != null)
                    {
                        PropertyInfo inst = AccessTools.Property(t, "Instance");
                        _shaker = inst != null ? inst.GetValue(null, null) : null;

                        // Resolve the overload by the preset's own type,
                        // since we cannot name it at compile time.
                        _shakeMethod = AccessTools.Method(
                            t, "Shake", new Type[] { preset.GetType() });
                    }

                    if (_shaker == null || _shakeMethod == null)
                    {
                        Log.LogInfo(
                            "Camera shake is not reachable, so overheat's " +
                            "\"shake\" does nothing. Everything else works.");
                    }
                }

                if (_shaker != null && _shakeMethod != null)
                    _shakeMethod.Invoke(_shaker, new object[] { preset });
            }
            catch (Exception)
            {
            }
        }
    }
}
