using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace WeaponForge
{
    // DRONE MODES ACROSS A SAVE (concept 7, answer 8: "yes please").
    //
    // A plain text sidecar in the save folder, the same shape as the rank and
    // wingman ones and for the same reasons: it lives INSIDE the save folder,
    // so it is slot-correct for free and dies with the run (death -> new run
    // -> new save -> no sidecar), and a new run therefore starts on normal
    // with no code. One line per gadget slot that is off normal, plus a
    // SENTRY's post, plus the ship's place in the shared cycle.
    //
    // The three traps carried across from ForgeWingmanSave / ForgeRankSave:
    //   1. the read is a POSTFIX, because ForgeBurnResetPatch is a prefix on
    //      the same `Load` and clears the modes first;
    //   2. a save with every slot on normal DELETES the file - skipping the
    //      write would hand yesterday's modes back tonight;
    //   3. a record matches its ship id exactly, or it is dropped.
    public static class ForgeDroneModeSave
    {
        private static readonly ManualLogSource Log =
            ForgeLog.Source("WeaponForge.DroneModes");

        private const string FileName = "forge-dronemodes.txt";
        private const string Version = "forge-dronemodes 1";

        [HarmonyPatch(typeof(Punk.SaveLoad.GameSaver), "Save", new Type[] { typeof(string) })]
        public class OnSave
        {
            static void Postfix(Punk.SaveLoad.SaveFolder __result)
            {
                try
                {
                    Write(__result.RootDirectory);
                }
                catch (Exception e)
                {
                    Log.LogWarning("drone modes not saved: " + e.Message);
                }
            }
        }

        private static void Write(string folder)
        {
            if (string.IsNullOrEmpty(folder))
                return;

            string path = Path.Combine(folder, FileName);
            List<string> lines = ForgeDroneModes.RecordLines();

            if (lines.Count == 0)
            {
                if (File.Exists(path))
                    File.Delete(path);

                return;
            }

            var sb = new StringBuilder();
            sb.AppendLine(Version);
            sb.AppendLine("# ship id <TAB> gadget slot <TAB> mode <TAB> gadget <TAB> sentry post x <TAB> y");
            sb.AppendLine("# shared <TAB> ship id <TAB> the mode the key alone last chose");

            foreach (string line in lines)
                sb.AppendLine(line);

            // The game makes a run's save folder on a background thread that
            // races the rest of the save (R21 309), so on a run's first save it
            // may not exist yet. Creating it is idempotent - the wingman record
            // does the same.
            Directory.CreateDirectory(folder);

            File.WriteAllText(path, sb.ToString());

            Log.LogInfo("drone modes: saved " + lines.Count + " line(s).");
        }

        [HarmonyPatch(typeof(Punk.SaveLoad.GameSaver), "Load")]
        public class OnLoad
        {
            static void Postfix(Punk.SaveLoad.GameSaver __instance, string folderName, bool __result)
            {
                if (!__result)
                    return;

                try
                {
                    Read(__instance, folderName);
                }
                catch (Exception e)
                {
                    Log.LogWarning("drone modes not restored: " + e.Message);
                }
            }
        }

        private static void Read(Punk.SaveLoad.GameSaver saver, string folderName)
        {
            string root = null;

            if (saver != null)
                root = Traverse.Create(saver).Field("savesRootDirectory").GetValue<string>();

            if (string.IsNullOrEmpty(root))
                root = Path.Combine(Application.persistentDataPath, "saves");

            string path = Path.Combine(Path.Combine(root, folderName ?? string.Empty), FileName);

            if (!File.Exists(path))
                return;

            string[] raw = File.ReadAllLines(path);

            if (raw.Length == 0 || raw[0].Trim() != Version)
            {
                Log.LogWarning(
                    "drone modes: the save's record is '" + (raw.Length > 0 ? raw[0].Trim() : "") +
                    "' and this build reads " + Version + " - every gadget starts on normal.");
                return;
            }

            var lines = new List<string>();

            for (int i = 1; i < raw.Length; i++)
                lines.Add(raw[i]);

            int n = ForgeDroneModes.Restore(lines, id => id, false);

            if (n > 0)
                Log.LogInfo("drone modes: " + n + " gadget slot(s) put back in their saved mode.");
        }
    }

    // DRONE MODES ACROSS A NEW GAME PLUS JUMP (concept 7: "Across a New Game
    // Plus jump the mode carries", the same "only across a jump" rule as rank).
    //
    // The same contract ForgeRankCarry reads, found by TYPE NAME and never
    // referenced: `GameModeForge.NewGamePlus.NgCarry`'s `Leaving` / `Arrived`
    // events and `NewShipId`. The grid comes back slot for slot, so a
    // per-slot mode needs no remapping - only the ship ids change. A SENTRY
    // slot arrives on normal: its post is in the old world (12.1 G). Drones do
    // not cross a jump (they start at 0), so resummoned ones join their slot's
    // mode. With New Game Plus absent this finds nothing and does nothing.
    public static class ForgeDroneModeCarry
    {
        private static readonly ManualLogSource Log =
            ForgeLog.Source("WeaponForge.DroneModes");

        private const string CarryType = "GameModeForge.NewGamePlus.NgCarry";

        private static bool _subscribed;
        private static PropertyInfo _carrying;
        private static MethodInfo _newShipId;

        // Held OUTSIDE everything the run-entry reset clears.
        private static List<string> _lines;

        public static void OnRunEntry()
        {
            try
            {
                Subscribe();

                if (_lines != null && !Carrying())
                    _lines = null;
            }
            catch (Exception e)
            {
                Log.LogWarning("drone modes: the New Game Plus carry check failed: " + e.Message);
            }
        }

        private static void Subscribe()
        {
            if (_subscribed)
                return;

            Type t = null;

            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    t = a.GetType(CarryType, false);
                }
                catch (Exception)
                {
                }

                if (t != null)
                    break;
            }

            if (t == null)
                return;   // not installed, or not loaded yet - asked again next run

            const BindingFlags S = BindingFlags.Public | BindingFlags.Static;

            EventInfo leaving = t.GetEvent("Leaving", S);
            EventInfo arrived = t.GetEvent("Arrived", S);
            _carrying = t.GetProperty("Carrying", S);
            _newShipId = t.GetMethod("NewShipId", S, null, new[] { typeof(int) }, null);

            _subscribed = true;

            if (leaving == null || arrived == null || _carrying == null || _newShipId == null ||
                leaving.EventHandlerType != typeof(Action) || arrived.EventHandlerType != typeof(Action))
            {
                Log.LogWarning(
                    "drone modes: New Game Plus is installed but its carry contract is not the one " +
                    "this build reads - drone modes start on normal after a jump.");
                return;
            }

            leaving.AddEventHandler(null, new Action(OnLeaving));
            arrived.AddEventHandler(null, new Action(OnArrived));
        }

        private static bool Carrying()
        {
            try
            {
                return _carrying != null && (bool)_carrying.GetValue(null, null);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static void OnLeaving()
        {
            _lines = ForgeDroneModes.RecordLines();
        }

        private static void OnArrived()
        {
            List<string> lines = _lines;
            _lines = null;

            if (lines == null || lines.Count == 0)
                return;

            int n = ForgeDroneModes.Restore(lines, NewShipId, true);

            Log.LogInfo(
                "drone modes: " + n + " gadget slot(s) keep their mode in the new world (a SENTRY " +
                "slot arrives on normal - its post was in the world you left).");
        }

        private static int NewShipId(int old)
        {
            try
            {
                return (int)_newShipId.Invoke(null, new object[] { old });
            }
            catch (Exception)
            {
                return 0;
            }
        }
    }
}
