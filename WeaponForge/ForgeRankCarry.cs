using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;

namespace WeaponForge
{
    // RANK ACROSS A NEW GAME PLUS JUMP (Game Mode Forge's New Game Plus,
    // CONCEPT.md 2.7).
    //
    // ★ HIS WORDS, 2026-09-29: "I definitely would love to keep rank weapons
    // level intact if possible. If the player has a rank weapon that scales
    // forever it would be sad to have to rebuild that." So a ranked gun crosses
    // a jump with EVERYTHING: its rank, its XP toward the next one, every
    // choice already taken, and any choice sets still owed (they come out in
    // the new world through ForgeRankQueue, one set at a time). The one thing
    // that does not cross is the list of stations that already paid it XP -
    // those stations are in the world being left.
    //
    // ★ THE CATCH IT SOLVES. A jump IS a new run: ForgeBurnResetPatch.ResetAll
    // (a prefix on RunData.Initialize) drops every copy's progress, exactly as
    // it must for a real new run. So the ranks are taken out BEFORE the old
    // world goes (NgCarry.Leaving) and handed back AFTER the new world's ships
    // have their carried grids but before any gun is built (NgCarry.Arrived) -
    // the same moment a Continue hands them back from the save.
    //
    // ★ AND IT IS THE CONTINUE PATH. The records are the save's own v2 lines
    // (ForgeRankSave.RecordLines), keyed by WHERE each copy is - `slot:<cluster>`
    // on a ship, `vault:<index>` - and they are re-attached by
    // ForgeRankSave.Attach, the method a Continue uses. New Game Plus restores
    // each grid slot for slot and the vault in its order, so every place still
    // names the same gun. Only the ship ids change (an id is a random number
    // from the world's seed), and NgCarry.NewShipId maps each old one. A copy
    // lying on the ground (`pickup:<id>`) stays in the old world.
    //
    // ★ FOUND BY TYPE NAME, NEVER A REFERENCE. With New Game Plus absent this
    // finds nothing and changes nothing. The lookup latches on success only
    // and is retried at each run entry, because load order is not guaranteed.
    public static class ForgeRankCarry
    {
        private static readonly ManualLogSource Log =
            ForgeLog.Source("WeaponForge.Rank");

        // The contract, pinned in both repos.
        private const string CarryType = "GameModeForge.NewGamePlus.NgCarry";

        private static bool _subscribed;
        private static bool _saidMismatch;
        private static PropertyInfo _carrying;
        private static MethodInfo _newShipId;

        // Taken when the old world is leaving, consumed when the new one has
        // arrived. Held OUTSIDE everything ResetAll clears, on purpose.
        private static List<string> _lines;

        // ForgeBurnResetPatch.ResetAll - every run entry.
        public static void OnRunEntry()
        {
            try
            {
                Subscribe();

                // A run that is NOT the new world of a jump: anything still
                // held belongs to nothing (a jump that never arrived).
                if (_lines != null && !Carrying())
                {
                    Log.LogWarning(
                        "rank: " + _lines.Count + " ranked gun(s) were held for a New Game Plus " +
                        "jump that never arrived - dropped.");
                    _lines = null;
                }
            }
            catch (Exception e)
            {
                Log.LogWarning("rank: the New Game Plus carry check failed: " + e.Message);
            }
        }

        private static void Subscribe()
        {
            if (_subscribed)
                return;

            Type t = FindQuietly(CarryType);

            if (t == null)
                return;   // not installed, or not loaded yet - asked again next run

            const BindingFlags S = BindingFlags.Public | BindingFlags.Static;

            EventInfo leaving = t.GetEvent("Leaving", S);
            EventInfo arrived = t.GetEvent("Arrived", S);
            _carrying = t.GetProperty("Carrying", S);
            _newShipId = t.GetMethod("NewShipId", S, null, new[] { typeof(int) }, null);

            if (leaving == null || arrived == null || _carrying == null || _newShipId == null ||
                leaving.EventHandlerType != typeof(Action) || arrived.EventHandlerType != typeof(Action))
            {
                // A contract that does not match will not start matching:
                // stop asking, say it once, and do nothing.
                _subscribed = true;

                if (!_saidMismatch)
                {
                    _saidMismatch = true;
                    Log.LogWarning(
                        "rank: New Game Plus is installed but its carry contract is not the one " +
                        "this build reads - ranked guns will NOT carry across a jump.");
                }

                return;
            }

            leaving.AddEventHandler(null, new Action(OnLeaving));
            arrived.AddEventHandler(null, new Action(OnArrived));
            _subscribed = true;

            Log.LogInfo(
                "rank: New Game Plus found - a ranked gun keeps its rank, XP, choices and owed " +
                "sets across a jump.");
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

        // The old world, still loaded: every copy where the save would say.
        private static void OnLeaving()
        {
            int unplaced;
            _lines = ForgeRankSave.RecordLines(out unplaced);

            Log.LogInfo(
                "rank: taking " + _lines.Count + " ranked gun(s) across the New Game Plus jump" +
                (unplaced > 0 ? " (" + unplaced + " are nowhere a save keeps a module and stay behind)" : "") +
                ".");
        }

        // The new world: carried grids installed, vault restored, no gun built.
        private static void OnArrived()
        {
            List<string> lines = _lines;
            _lines = null;

            if (lines == null || lines.Count == 0)
                return;

            var records = new List<ForgeRankSave.Record>();
            int left = 0;

            foreach (string line in lines)
            {
                ForgeRankSave.Record r = ForgeRankSave.ParseV2(line);

                if (r == null || r.p == null || r.where == null)
                    continue;

                // A gun lying on the old world's ground stays there.
                if (r.where.StartsWith("pickup:", StringComparison.Ordinal))
                {
                    left++;
                    continue;
                }

                // Those stations are in the world you left; the new world's
                // stations can each pay it again.
                r.p.paidStations.Clear();

                if (r.shipId != 0)
                {
                    int now = NewShipId(r.shipId);

                    if (now == 0)
                    {
                        left++;
                        continue;
                    }

                    r.shipId = now;
                }

                records.Add(r);
            }

            if (records.Count > 0)
                ForgeRankSave.Attach(records);

            Log.LogInfo(
                "rank: " + records.Count + " ranked gun(s) handed back in the new world" +
                (left > 0 ? " (" + left + " stayed behind - on the ground, or on a ship that did not carry)" : "") +
                ".");
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

        // AccessTools.TypeByName warns for an absent type, and "New Game Plus
        // is not installed" is a normal state. Scan the loaded assemblies.
        private static Type FindQuietly(string fullName)
        {
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t;

                try
                {
                    t = a.GetType(fullName, false);
                }
                catch (Exception)
                {
                    continue;
                }

                if (t != null)
                    return t;
            }

            return null;
        }
    }
}
