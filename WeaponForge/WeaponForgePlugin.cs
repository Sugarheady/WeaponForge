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
            // ---- THE LOG FILE, before the first line ----------------------
            //
            // Everything this mod logs goes to BepInEx\WeaponForge.log, and
            // LogOutput.log keeps only this plugin's own lines plus a copy of
            // every error - see ForgeLog. First, so "loaded" and BUILD are in
            // the file too: it has to stand on its own when it is the only
            // file somebody sends.
            ConfigEntry<bool> ownLogFile = Config.Bind(
                "Logging",
                "OwnLogFile",
                true,
                "true: Weapon Forge writes its log to BepInEx\\WeaponForge.log " +
                "(the launch before is kept as WeaponForge.prev.log), so " +
                "LogOutput.log and the console stay readable for BepInEx and " +
                "other mods. Only errors are copied to LogOutput.log.\n" +
                "false: everything goes to LogOutput.log and the console, the " +
                "way it did before 2026-09-25.");

            ForgeLog.Start(Logger, ownLogFile.Value);

            Logger.LogInfo("Weapon Forge loaded" + ForgeLog.Where);

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

            // ---- THE OFF SWITCH -------------------------------------------
            //
            // Added 2026-09-21 so Game Mode Forge's settings tab has something
            // real to switch, but this mod owns it and honours it with that
            // mod ABSENT - which is the whole point. Game Mode Forge only
            // edits this file; it never reaches in here, and nothing about
            // this setting depends on it existing.
            //
            // ★ IT HAS TO BE HERE, ABOVE EVERYTHING. A .NET assembly cannot be
            // unloaded in Unity's Mono (one AppDomain, no collectible load
            // contexts on .NET Framework 4.7.2), so "turn the mod off" can
            // only mean "this launch, do nothing at all". Returning here means
            // no patches, no registrations, and - because the content is built
            // from a Harmony startup patch that now never applies - no Forge
            // weapons in any registry either.
            //
            // ⚠ THAT ORDER IS LOAD-BEARING AND A PARTIAL VERSION WOULD BE
            // WORSE THAN NONE. Leave the content built and remove the patches
            // and you get Forge weapons that exist and do not work, which is
            // the silent-fallback family this project keeps paying for. Rip
            // the content back out of the registries mid-session instead and
            // you break saves: `Vault.RestoreFromMemento` does
            // `registry.Get(id).DeepCopy()` with no null check, and
            // `ConfigRegistry.Get` returns default(T) on a miss - so loading a
            // save that used a Forge module would throw inside the game's own
            // code. Off means off from the first line, or not at all.
            ConfigEntry<bool> enabled = Config.Bind(
                "General",
                "Enabled",
                true,
                "Master switch for this whole mod. Off means Weapon Forge " +
                "patches nothing and builds nothing for that launch - the " +
                "game runs as if the DLL were not installed. Takes effect on " +
                "the NEXT launch, because a loaded assembly cannot be " +
                "unloaded. Game Mode Forge's settings tab writes this for " +
                "you; editing it here by hand works just as well.\n" +
                "⚠ A saved run that used Forge weapons or modules will NOT " +
                "load while this is off. Turn it back on to recover the save.");

            if (!enabled.Value)
            {
                Logger.LogWarning(
                    "WEAPON FORGE IS SWITCHED OFF ([General] Enabled = false " +
                    "in this mod's BepInEx config). No patches, no weapons, " +
                    "nothing built - the game runs stock. A save that used " +
                    "Forge content will not load until this is switched back " +
                    "on.");

                return;
            }

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

            ConfigEntry<string> gaugeMode = Config.Bind(
                "Readouts",
                "Gauges",
                "files",
                "The `gauge` bar and `bubble` icon that hang off your ship.\n" +
                "  files - obey each weapon's own block. The default.\n" +
                "  on    - also give a bar to every weapon that can overheat " +
                "or spin up, even if its file never asked for one.\n" +
                "  off   - no readouts at all, whatever the files say.\n" +
                "This never touches the game's own self-destruct bar or fuel " +
                "warning; the readouts are copies.");

            // ---- rank choices ---------------------------------------------
            //
            // R20 252: "find a keyboard/gamepad key that can be rebound in the
            // .cfg". The weapon card of a gun whose rank lets you pick names
            // whatever is set here.
            ConfigEntry<string> rankKey = Config.Bind(
                "Rank choices",
                "Key",
                "H",
                "The key that puts a ranked gun's floating choices away, and " +
                "brings them back around your ship on the next press - for a " +
                "choice that landed in a wall or a station, or to get them out " +
                "of the way. A key name (H, G, F5, Backquote) or an Input " +
                "System path (<Keyboard>/h). \"none\" for no key. Nothing in " +
                "the game uses H while flying.");

            ConfigEntry<string> rankPad = Config.Bind(
                "Rank choices",
                "PadButton",
                "DpadUp",
                "The same on a gamepad: DpadUp (the default - unused while " +
                "flying), DpadDown, DpadLeft, DpadRight, LeftStickPress, " +
                "RightStickPress, Select, North / South / East / West, LB, RB, " +
                "or an Input System path (<Gamepad>/dpad/up). \"none\" for no " +
                "button. Note D-pad down opens the item wheel and D-pad left " +
                "the ship menu.");

            ForgeRankKeys.Configure(rankKey.Value, rankPad.Value);

            // ---- drone modes ----------------------------------------------
            //
            // `DRONE MODES - CONCEPT.md`. The key is a player preference, so it
            // lives here (decided 2026-09-21); key rows in the GAME MODE FORGE
            // tab are a later step (12.2 I) and would write these same lines.
            ConfigEntry<string> droneKey = Config.Bind(
                "Drone modes",
                "Key",
                "Y",
                "Steps every gadget's companions (drones and wingmen) to the next mode in Cycle " +
                "(by default NORMAL > ORBIT > DRAG > PROTECTOR > INTERCEPTOR > REAR GUARD > SEEKER > " +
                "SENTRY > NORMAL). " +
                "It acts when you LET GO. Hold it and press a gadget's own button (1, 2 or 3, " +
                "or whatever fires that gadget) to step only that gadget. A key name (Y, G, F5, " +
                "Backquote) or an Input System path (<Keyboard>/y). \"none\" here and in " +
                "PadButton switches drone modes off. Nothing in the game uses Y while flying - " +
                "GAME KEYS.txt lists every free key.");

            ConfigEntry<string> dronePad = Config.Bind(
                "Drone modes",
                "PadButton",
                "DpadRight",
                "The same on a gamepad: DpadRight (the default - unused while flying), " +
                "DpadUp, DpadDown, DpadLeft, LeftStickPress / L3, RightStickPress / R3, " +
                "Select, North / South / East / West, LB, RB, or an Input System path. Hold " +
                "it and press a gadget's face button to step that gadget alone. \"none\" for " +
                "no button. Note D-pad up is the rank key, D-pad down the item wheel and " +
                "D-pad left the ship menu.");

            ConfigEntry<string> droneSlot1 = Config.Bind(
                "Drone modes", "Slot1", "",
                "Optional: another key that, with Key held, steps gadget 1 alone. Blank = " +
                "just gadget 1's own button (1 / Ctrl, pad West). Both work when set.");

            ConfigEntry<string> droneSlot2 = Config.Bind(
                "Drone modes", "Slot2", "",
                "Optional: another key that, with Key held, steps gadget 2 alone. Blank = " +
                "just gadget 2's own button (2 / middle mouse, pad North).");

            ConfigEntry<string> droneSlot3 = Config.Bind(
                "Drone modes", "Slot3", "",
                "Optional: another key that, with Key held, steps gadget 3 alone. Blank = " +
                "just gadget 3's own button (3 / T, pad East).");

            ConfigEntry<string> droneNormal = Config.Bind(
                "Drone modes", "NormalKey", "U",
                "Puts every gadget's companions back on NORMAL at once. \"none\" for no key. " +
                "Nothing in the game uses U.");

            ConfigEntry<bool> droneHold = Config.Bind(
                "Drone modes", "HoldForNormal", true,
                "true: holding Key (or PadButton) about a second, with no gadget button, also " +
                "puts everything back on NORMAL - the way back on a gamepad.");

            ConfigEntry<string> droneCycle = Config.Bind(
                "Drone modes", "Cycle", ForgeDroneModeRules.DefaultCycleText,
                "The modes Key steps through, in order - reorder them, or leave any out and " +
                "it is never offered. NORMAL is always the start and the end. The modes: " +
                "Orbit, Drag, Protector, RearGuard, Seeker, Sentry, Interceptor, and four not " +
                "in the default list until they pass their tests - InterceptFront, InterceptRear, " +
                "Striker and Vengeance (add them to try them). A gadget whose file joins a mode " +
                "this list leaves out still gets it with Key + that gadget's own button.");

            ConfigEntry<string> droneFormation = Config.Bind(
                "Drone modes", "Formation", "Loose",
                "How drones hold ORBIT / DRAG / PROTECTOR / REAR GUARD. Loose: they fly to " +
                "their place at their own speed and lag a moment when you boost. Locked: " +
                "they hold their place the way wingmen do. Wingmen are always locked, and so " +
                "are the three INTERCEPTOR modes, STRIKER and VENGEANCE.");

            ConfigEntry<float> droneSeek = Config.Bind(
                "Drone modes", "SeekRange", 40f,
                "SEEKER: how far from you a drone goes hunting, in units (the game's own " +
                "fighting leash is 20). It flies at the nearest enemy within this range of you, " +
                "seen or not; with nothing in range it sweeps a loop round you at about half " +
                "this. 10 to 80 - the game only keeps the world loaded about 75 units round you " +
                "(25-unit squares, three out from the camera), so past that there is nothing to " +
                "find, and a drone that crosses into an unloaded square is deleted.");

            ConfigEntry<float> droneSentryRadius = Config.Bind(
                "Drone modes", "SentryRadius", 8f,
                "SENTRY: how far a drone roams from its post before it is walked back. " +
                "The post is where your ship was when you pressed.");

            ConfigEntry<float> droneRecall = Config.Bind(
                "Drone modes", "SentryRecall", 60f,
                "SENTRY: fly this far from the post and the sentries leave it and come back " +
                "to you (\"SENTRY RECALLED\"), and the gadget is on NORMAL again. Kept under " +
                "75, because the game deletes a drone left about 87 units behind.");

            ConfigEntry<float> droneAlert = Config.Bind(
                "Drone modes", "AlertSeconds", 2.5f,
                "How long the \"DRONES: ORBIT\" / \"SENTRY RECALLED\" alert stays before it " +
                "fades away.");

            ConfigEntry<bool> droneNoDamage = Config.Bind(
                "Drone modes", "InterceptorNoDamage", false,
                "TEST OPTION for INTERCEPTOR. true: interceptor drones take the hit and lose " +
                "no health, and wingman orbs (which have no hitbox) join in by deleting the " +
                "enemy shots they meet. Off by default.");

            ConfigEntry<float> droneOrbitRadius = Config.Bind(
                "Drone modes", "OrbitRadius", 3.5f,
                "ORBIT: the ring's radius. A crowded ring widens to fit. A wingman or drone " +
                "file's own \"modes\" block can set its own.");

            ConfigEntry<float> droneOrbitSpin = Config.Bind(
                "Drone modes", "OrbitSpin", 60f,
                "ORBIT: how fast the ring turns, degrees per second (negative turns the " +
                "other way).");

            ConfigEntry<float> droneDragSpacing = Config.Bind(
                "Drone modes", "DragSpacing", 1.6f,
                "DRAG: the gap between companions in the line behind you.");

            ConfigEntry<float> droneProtector = Config.Bind(
                "Drone modes", "ProtectorDistance", 2.6f,
                "PROTECTOR: how far in front of you (toward where you aim) the row sits.");

            ConfigEntry<float> droneRear = Config.Bind(
                "Drone modes", "RearGuardDistance", 4.5f,
                "REAR GUARD: how far behind you (away from where you aim) the row sits. It " +
                "is further out and wider than PROTECTOR's, so the two read differently.");

            ConfigEntry<float> droneSpacing = Config.Bind(
                "Drone modes", "Spacing", 1.3f,
                "PROTECTOR and REAR GUARD: the gap between companions across the row.");

            // R21 part 3, his 315 ask: "Can we somehow bump up the rate of how
            // quickly they snap to nearby projectiles? ... Can you make a
            // slider for that?"
            ConfigEntry<float> droneInterceptSpeed = Config.Bind(
                "Drone modes", "InterceptSpeed", 100f,
                "INTERCEPTOR, INTERCEPT FRONT and INTERCEPT REAR: how eagerly they snap to enemy " +
                "shots, in percent. 100 is how they have always snapped; higher notices shots " +
                "sooner, takes near misses too, and gets there faster - 400 is the extreme. " +
                "50 to 400. A wingman or drone file's own \"modes\" block can set its own " +
                "(interceptSpeed).");

            ConfigEntry<float> droneStrikeRange = Config.Bind(
                "Drone modes", "StrikeRange", 20f,
                "STRIKER and VENGEANCE: how far from YOU an enemy may be for your companions to " +
                "dive at it, in units. 6 to 40.");

            ConfigEntry<float> droneStrikeDistance = Config.Bind(
                "Drone modes", "StrikeDistance", 2.5f,
                "STRIKER and VENGEANCE: how close a companion sits to the enemy it dives at " +
                "(point blank), in units. 1 to 8.");

            var droneShapes = new ForgeDroneModeRules.Shape
            {
                orbitRadius = Mathf.Clamp(droneOrbitRadius.Value, 1f, 12f),
                dragSpacing = Mathf.Clamp(droneDragSpacing.Value, 0.5f, 6f),
                protectorDistance = Mathf.Clamp(droneProtector.Value, 1f, 10f),
                rearGuardDistance = Mathf.Clamp(droneRear.Value, 1f, 12f),
                spacing = Mathf.Clamp(droneSpacing.Value, 0.5f, 5f),
                interceptSpeed = Mathf.Clamp(droneInterceptSpeed.Value,
                    ForgeDroneModeRules.InterceptSpeedMin, ForgeDroneModeRules.InterceptSpeedMax),
                strikeRange = Mathf.Clamp(droneStrikeRange.Value,
                    ForgeDroneModeRules.StrikeRangeMin, ForgeDroneModeRules.StrikeRangeMax),
                strikeDistance = Mathf.Clamp(droneStrikeDistance.Value,
                    ForgeDroneModeRules.StrikeDistanceMin, ForgeDroneModeRules.StrikeDistanceMax)
            };

            // His R21 part 1 note, answered "Yes, on by default": "would it be
            // possible to reset their revenge or aggro between mode changes? I
            // think they fixate on a target and dont loose it between modes."
            ConfigEntry<bool> droneForget = Config.Bind(
                "Drone modes", "ForgetTargetOnChange", true,
                "true: whenever a drone's mode changes, it drops the enemy it was fighting and " +
                "any grudge it was holding, and picks again from what it can see from its new " +
                "place. false: it keeps fighting what it was fighting. Only the game's drones " +
                "and Forge drone files have a target to drop; wingmen aim on their own.");

            ForgeDroneModes.Configure(
                droneKey.Value, dronePad.Value,
                droneSlot1.Value, droneSlot2.Value, droneSlot3.Value,
                droneNormal.Value, droneHold.Value, droneCycle.Value, droneFormation.Value,
                droneSeek.Value, droneSentryRadius.Value, droneRecall.Value, droneAlert.Value,
                droneNoDamage.Value, droneShapes, droneOrbitSpin.Value);

            ForgeDroneModes.ForgetTargetOnChange = droneForget.Value;

            // The settings tab's slider writes this same line (R21 part 3).
            ForgeSettingsTab.InterceptSpeed = droneInterceptSpeed;
            ForgeSettingsTab.File = Config;

            string gaugeWord = (gaugeMode.Value ?? "files").Trim().ToLowerInvariant();

            ForgeGauge.global =
                gaugeWord == "on" ? ForgeGauge.Global.On
                    : gaugeWord == "off" ? ForgeGauge.Global.Off
                        : ForgeGauge.Global.Files;

            ForgeBuffHud.maxLines = Mathf.Clamp(buffMaxLines.Value, 1, 12);
            ForgeBuffHud.iconSize = Mathf.Clamp(buffIconSize.Value, 8f, 128f);

            ForgeBuffHud.style = ForgeBuffHud.ParseStyle(buffStyle.Value);
            ForgeBuffHud.labels = ForgeBuffHud.ParseLabels(buffLabel.Value);
            ForgeBuffHud.showTimedStates = buffStates.Value;
            ForgeBuffHud.showDebuffs = buffDebuffs.Value;

            // Spins up the state poller, but only if either switch is on.
            ForgeBuffStates.EnsureTicker();

            // Its own file, not LogOutput.log: that keeps load lines only.
            if (buffStates.Value || buffDebuffs.Value)
            {
                ForgeLog.Source("WeaponForge").LogInfo(
                    "Buff indicator reporting " +
                    (buffStates.Value
                        ? "states (shield up, orbit ring, tether latched)" : "") +
                    (buffStates.Value && buffDebuffs.Value ? " and " : "") +
                    (buffDebuffs.Value ? "debuffs (burning)" : "") +
                    ". Charge held and gas slow are not reported yet.");
            }

            // THE SHARED DAMAGE MEASUREMENT'S READERS, registered BEFORE the
            // patches go on. `ForgeDamageDealt` owns the one prefix/postfix
            // pair that measures what a hit actually removed from a tank;
            // leech, surge and rank read it. Registration is independent of
            // Harmony - a reader that is not subscribed simply never hears
            // anything - but doing it first means no window exists in which the
            // patch is live and a reader is not.
            ForgeLeechPatch.Register();
            ForgeSurgePatch.Register();
            ForgeRankPatch.Register();

            var harmony =
                new Harmony("com.sugarheady.weaponforge");

            harmony.PatchAll();

            // Patched by hand, because `ChargerHead` is not visible to this
            // assembly at compile time - there is no typeof() for PatchAll to
            // find. Stops a friendly rammer headbutting its own swarm to death.
            ForgeMinionRamGuard.Apply(harmony);

            Logger.LogInfo("Weapon Forge patches applied");
        }

        // Let go of WeaponForge.log as this game closes. The restart row
        // starts the next game first, and that one opens the same file.
        private void OnApplicationQuit()
        {
            ForgeLog.Close();
        }
    }
}
