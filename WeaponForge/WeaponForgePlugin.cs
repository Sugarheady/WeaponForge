using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.Mono;
using HarmonyLib;
using UnityEngine;

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

            // ---- the on-screen buff indicator -----------------------------
            //
            // Global defaults; any single weapon can override with
            // "buffIndicator" in its JSON. On by default on purpose - a timed
            // buff nobody can see is a buff nobody can tune, which is the whole
            // reason this exists.
            ConfigEntry<string> buffStyle = Config.Bind(
                "Buff indicator",
                "Style",
                "off",
                "How an active buff is shown: \"off\" (the default), \"feed\" " +
                "(the game's own status line, where \"Fuel low\" appears), " +
                "\"icons\" (a row of icons beside the minion counter), or " +
                "\"both\". OFF by default deliberately - the status area is " +
                "next to the ammo / fuel / health readouts and this is an " +
                "opt-in overlay on top of them. A weapon can still ask for an " +
                "indicator per-file with \"buffIndicator\".");

            ConfigEntry<string> buffLabel = Config.Bind(
                "Buff indicator",
                "Label",
                "effect",
                "What the indicator reads: \"effect\" (FIRE RATE +30%), " +
                "\"seconds\" (a countdown), \"stacks\" (x3), or \"icon\" " +
                "(no text at all).");

            ConfigEntry<bool> buffStates = Config.Bind(
                "Buff indicator",
                "ShowTimedStates",
                false,
                "Also indicate the mod's ongoing states: a raised shield, a " +
                "live orbit ring, a latched tether. Off by default because a " +
                "busy fight fills the status area. (A held charge is not " +
                "reported yet.)");

            ConfigEntry<bool> buffDebuffs = Config.Bind(
                "Buff indicator",
                "ShowDebuffs",
                false,
                "Also indicate things working AGAINST you - today that means " +
                "BURNING, read from the game's own on-fire flag so it reports " +
                "whatever set you alight. Shown flashing, the way the game " +
                "shows \"Fuel low\". (Gas slowing you is not reported yet.)");

            ConfigEntry<int> buffMaxLines = Config.Bind(
                "Buff indicator",
                "MaxFeedLines",
                3,
                "The most status lines this mod may occupy at once. A backstop " +
                "so the feed can never grow over the ammo / fuel / health " +
                "readouts, whatever happens upstream.");

            ConfigEntry<float> buffIconSize = Config.Bind(
                "Buff indicator",
                "IconSize",
                28f,
                "Side of one icon in the icon row, in UI pixels. The widget is " +
                "borrowed from the minion counter and brings its own authored " +
                "size, so this is what actually decides how big it looks.");

            ForgeBuffHud.maxLines = Mathf.Clamp(buffMaxLines.Value, 1, 12);
            ForgeBuffHud.iconSize = Mathf.Clamp(buffIconSize.Value, 8f, 128f);

            ForgeBuffHud.style = ForgeBuffHud.ParseStyle(buffStyle.Value);
            ForgeBuffHud.labels = ForgeBuffHud.ParseLabels(buffLabel.Value);
            ForgeBuffHud.showTimedStates = buffStates.Value;
            ForgeBuffHud.showDebuffs = buffDebuffs.Value;

            // Spins up the state poller, but only if either switch is on.
            ForgeBuffStates.EnsureTicker();

            if (buffStates.Value || buffDebuffs.Value)
            {
                Logger.LogInfo(
                    "Buff indicator reporting " +
                    (buffStates.Value
                        ? "states (shield up, orbit ring, tether latched)" : "") +
                    (buffStates.Value && buffDebuffs.Value ? " and " : "") +
                    (buffDebuffs.Value ? "debuffs (burning)" : "") +
                    ". Charge held and gas slow are not reported yet.");
            }

            var harmony =
                new Harmony("com.sugarheady.weaponforge");

            harmony.PatchAll();

            Logger.LogInfo("Weapon Forge patches applied");
        }
    }
}
