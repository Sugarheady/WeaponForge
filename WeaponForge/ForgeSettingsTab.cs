using System;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;

namespace WeaponForge
{
    // THE ROWS WEAPON FORGE PUTS IN GAME MODE FORGE'S SETTINGS TAB, under its
    // own on/off switch. The first is his R21 part 3 ask (315): "Can we somehow
    // bump up the rate of how quickly they snap to nearby projectiles? ... Can
    // you make a slider for that?" - INTERCEPTOR's snap speed.
    //
    // ★ GAME MODE FORGE DRAWS THEM, WEAPON FORGE OWNS THEM. Found by TYPE NAME
    // ("WeaponForge.ForgeSettingsTab") - no reference either way, strings and
    // ints only, so each mod builds and runs alone. With Game Mode Forge
    // absent the same number is `[Drone modes] InterceptSpeed` in the .cfg.
    //
    // THE CONTRACT - the one New Game Plus's NgTab and Salvage Shop's
    // SalvageTab speak (GameModeForge.ForgeTabRows):
    //
    //   string[] Rows()                    "id|slider|caption|steps|maxShown"
    //   int      Get(string id)            the slider's step
    //   bool     Set(string id, int value) false = not saved (the row goes back)
    //   string   Label(string id, int v)   the row's caption for that value
    public static class ForgeSettingsTab
    {
        public const int ContractVersion = 1;

        private static readonly ManualLogSource Log = ForgeLog.Source("WeaponForge.Settings");

        // 50% to 400% in steps of 25: step 0 = 50, step 14 = 400.
        private const int SpeedStep = 25;
        private static readonly int SpeedSteps =
            (int)((ForgeDroneModeRules.InterceptSpeedMax - ForgeDroneModeRules.InterceptSpeedMin) / SpeedStep);

        // Set by the plugin's Awake: the entry, and the file it lives in.
        internal static ConfigEntry<float> InterceptSpeed;
        // Fully qualified: the game declares its own ConfigFile in the global
        // namespace, and a global type beats one imported by a using.
        internal static BepInEx.Configuration.ConfigFile File;

        public static string[] Rows()
        {
            if (InterceptSpeed == null)
                return new string[0];

            return new[]
            {
                "drones.interceptspeed|slider|Interceptor snap speed|" + SpeedSteps + "|" +
                ((int)ForgeDroneModeRules.InterceptSpeedMax)
            };
        }

        public static int Get(string id)
        {
            try
            {
                if (id == "drones.interceptspeed")
                {
                    float v = ForgeDroneModes.Defaults.interceptSpeed;
                    int s = Mathf.RoundToInt((v - ForgeDroneModeRules.InterceptSpeedMin) / SpeedStep);
                    return Mathf.Clamp(s, 0, SpeedSteps);
                }
            }
            catch (Exception)
            {
            }

            return 0;
        }

        // ★ RELOAD BEFORE WRITING. Game Mode Forge writes this mod's
        // `[General] Enabled` into the same .cfg mid-run (its own fresh copy of
        // the file), and BepInEx saves the WHOLE file from memory - so a save
        // straight from this launch's copy would quietly put back the Enabled
        // value this launch started with, undoing a switch flipped a moment
        // ago. Reading the file first keeps it.
        public static bool Set(string id, int value)
        {
            try
            {
                if (id != "drones.interceptspeed" || InterceptSpeed == null)
                    return false;

                float speed = ForgeDroneModeRules.InterceptSpeedMin + Mathf.Clamp(value, 0, SpeedSteps) * SpeedStep;

                if (File != null)
                    File.Reload();

                InterceptSpeed.Value = speed;

                if (File != null && !File.SaveOnConfigSet)
                    File.Save();

                // Live: every gadget whose file does not set its own
                // interceptSpeed reads the default each frame.
                ForgeDroneModes.Defaults.interceptSpeed = speed;

                Log.LogInfo(
                    "drone modes: interceptor snap speed set to " + speed.ToString("0") + "% from the settings " +
                    "tab - every gadget without its own \"interceptSpeed\" takes it at once.");

                return true;
            }
            catch (Exception ex)
            {
                Log.LogError("the settings tab could not save " + id + ": " + ex);
                return false;
            }
        }

        public static string Label(string id, int value)
        {
            if (id == "drones.interceptspeed")
            {
                int speed = (int)ForgeDroneModeRules.InterceptSpeedMin + Mathf.Clamp(value, 0, SpeedSteps) * SpeedStep;
                return "Interceptor snap speed: " + speed + "%";
            }

            return id;
        }
    }
}
