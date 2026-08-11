using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.Mono;
using HarmonyLib;

namespace WeaponForge
{
    [BepInPlugin(
        "com.sugarheady.weaponforge",
        "Weapon Forge",
        "1.0.0")]
    public class WeaponForgePlugin : BaseUnityPlugin
    {
        private void Awake()
        {
            Logger.LogInfo("Weapon Forge loaded");

            // Build stamp. We wasted a test round arguing about whether an
            // old DLL was installed; this makes that unanswerable. The
            // timestamp is the DLL's own last-write time, so it changes on
            // every rebuild whether or not anyone remembers to bump a number.
            try
            {
                string dll = System.Reflection.Assembly
                    .GetExecutingAssembly().Location;

                Logger.LogInfo(
                    "BUILD " +
                    System.IO.File.GetLastWriteTime(dll)
                        .ToString("yyyy-MM-dd HH:mm:ss") +
                    "  (if this is older than the change you are testing, " +
                    "the DLL did not get copied)");
            }
            catch { }

            // Cap for WeaponForge's OWN burn tick-rate engine (used only
            // when ModuleForge isn't installed; otherwise ModuleForge's cap
            // applies). Editable in the BepInEx config file.
            ConfigEntry<float> maxBurnTicks = Config.Bind(
                "Burn",
                "MaxTicksPerSecond",
                100f,
                "Cap on how fast burn can tick from burnTickRate weapons " +
                "(ticks/sec) when WeaponForge runs its own burn engine " +
                "(ModuleForge not installed). The game ticks burn at most " +
                "once per frame, so values above your frame rate just mean " +
                "'every frame'. Must be > 0.");

            if (maxBurnTicks.Value > 0f)
                ForgeBurn.MaxTicksPerSecond = maxBurnTicks.Value;

            var harmony =
                new Harmony("com.sugarheady.weaponforge");

            harmony.PatchAll();

            Logger.LogInfo("Weapon Forge patches applied");
        }
    }
}
