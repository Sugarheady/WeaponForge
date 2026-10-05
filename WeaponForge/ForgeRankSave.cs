using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace WeaponForge
{
    // Rank persistence - a plain text sidecar in the save folder.
    //
    // ★ "THE RESET NEEDS NO CODE." He asked for rank to persist between saves
    // and die with the run, and because the file lives INSIDE the save folder
    // that falls out of where it is rather than being implemented: the folder
    // is slot-correct for free (a coop save and a normal save cannot read each
    // other's), `DeleteSave` removes the directory recursively and takes this
    // with it, and death -> new run -> new save -> no sidecar.
    //
    // ★ ONE LINE PER COPY OF A GUN (version 2, R19 252/256). A copy is a
    // Module instance, and a Module has no id of its own - its memento holds
    // the data id, the connections and a power roll, none of them unique. So a
    // copy is saved under WHERE IT IS, which the save itself restores exactly:
    //
    //   slot:PrimaryWeapon   installed in that slot of that ship's grid
    //   vault:4              the 5th module in the vault (its list order is
    //                        what the vault memento restores)
    //   pickup:<id>          lying in the level, inside that pickup entity
    //
    // ★ VERSION 1 IS STILL READ. Its lines are one per (ship, weapon file), and
    // each goes to the FIRST copy of that file the load finds - slots in order,
    // then the vault - which is exactly what "the file's rank" meant when it
    // was written. So a save from before this build keeps its ranks.
    //
    // THREE TRAPS CARRIED ACROSS FROM `ForgeWingmanSave`, each of which cost
    // something the first time:
    //
    //   1. **The read must be a POSTFIX.** `ForgeBurnResetPatch` is a PREFIX on
    //      this same method and drops every scrap of per-run state, rank
    //      progress included. Prefix -> original -> postfix is the only
    //      ordering that survives its own reset.
    //   2. **A record at rank 0 with nothing drawn must be DELETED, not
    //      skipped.** "Write a line for each ranked gun" leaves yesterday's
    //      file intact to hand the rank back tonight.
    //   3. **Absence of a record is not absence of a claimant.** There is no
    //      guessing fallback: a v2 location matches exactly or the line is
    //      dropped.
    //
    // ★ AND THE ONE THAT IS RANK'S OWN: **draw COUNTS, not a list.**
    // `chain:3,split:1` is compact, is exactly what `onRepeat` needs to know,
    // and is the only form that survives `maxRank: 9999`.
    public static class ForgeRankSave
    {
        private static readonly ManualLogSource Log =
            ForgeLog.Source("WeaponForge.Rank");

        private const string FileName = "forge-rank.txt";
        private const string Version = "forge-rank 2";
        private const string VersionV1 = "forge-rank 1";
        private const string StartTag = "start";

        // ------------------------------------------------------------------
        // Where every module is
        // ------------------------------------------------------------------

        private struct Place
        {
            public int shipId;     // 0 for the vault and the ground
            public string where;
        }

        // Every module the save will store, and where it will store it. Read
        // from the ENTITY data, not from live GameObjects, because that is what
        // the save writes and what the load gives back - the same walk works
        // either side of it.
        private static Dictionary<Module, Place> Locate(out List<KeyValuePair<Module, Place>> ordered)
        {
            var map = new Dictionary<Module, Place>();
            ordered = new List<KeyValuePair<Module, Place>>();

            EntityManager em;

            if (ServiceLocator.TryGet<EntityManager>(out em) && em != null)
            {
                foreach (EntityData ship in em.GetShips())
                {
                    ModuleGridOwner.Data grid;

                    if (ship == null || !ship.TryGetComponent<ModuleGridOwner.Data>(out grid) ||
                        grid == null || grid.ModuleGrid == null)
                    {
                        continue;
                    }

                    // In the game's own slot order, which is also the order a
                    // v1 record claims its copy in.
                    foreach (ClusterType slot in new[]
                             { ClusterType.PrimaryWeapon, ClusterType.SecondaryWeapon,
                               ClusterType.Active1, ClusterType.Active2,
                               ClusterType.Active3 })
                    {
                        IModuleCluster c;

                        try
                        {
                            c = grid.ModuleGrid.GetCluster(slot);
                        }
                        catch (Exception)
                        {
                            continue;
                        }

                        if (c == null || !c.HasMainModule || c.MainModule == null)
                            continue;

                        var place = new Place { shipId = ship.instanceId, where = "slot:" + slot };

                        if (!map.ContainsKey(c.MainModule))
                        {
                            map[c.MainModule] = place;
                            ordered.Add(new KeyValuePair<Module, Place>(c.MainModule, place));
                        }
                    }
                }

                try
                {
                    foreach (ModulePickup.Data d in em.GetEntitiesWithComponent<ModulePickup.Data>())
                    {
                        if (d == null || d.module == null || d.entity == null)
                            continue;

                        var place = new Place { where = "pickup:" +
                            d.entity.instanceId.ToString(CultureInfo.InvariantCulture) };

                        if (!map.ContainsKey(d.module))
                        {
                            map[d.module] = place;
                            ordered.Add(new KeyValuePair<Module, Place>(d.module, place));
                        }
                    }
                }
                catch (Exception)
                {
                    // Pickups are the least important place a copy can be.
                }
            }

            Vault vault;

            if (ServiceLocator.TryGet<Vault>(out vault) && vault != null)
            {
                int i = 0;

                foreach (Module m in vault.Modules)
                {
                    if (m != null && !map.ContainsKey(m))
                    {
                        var place = new Place { where = "vault:" +
                            i.ToString(CultureInfo.InvariantCulture) };

                        map[m] = place;
                        ordered.Add(new KeyValuePair<Module, Place>(m, place));
                    }

                    i++;
                }
            }

            return map;
        }

        // ------------------------------------------------------------------
        // Writing
        // ------------------------------------------------------------------
        //
        // `Save(bool coop)` calls `Save(string)`, so one patch covers both the
        // normal path and the debug menu's named slots. The overload has to be
        // named explicitly or Harmony cannot tell the two apart.
        [HarmonyPatch(typeof(Punk.SaveLoad.GameSaver), "Save",
                      new Type[] { typeof(string) })]
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
                    Log.LogWarning("rank not saved: " + e.Message);
                }
            }
        }

        private static void Write(string folder)
        {
            if (string.IsNullOrEmpty(folder))
                return;

            int unplaced;
            List<string> lines = RecordLines(out unplaced);

            string path = Path.Combine(folder, FileName);
            int start = ForgeRankStation.StartStation;

            // ★ THE DELETE IS LOAD-BEARING, not tidiness. Without it, a run
            // where every ranked weapon was dropped writes nothing and last
            // night's file survives untouched - so loading hands the ranks back
            // to guns you no longer carry. (A file holding only the start
            // station hands nothing back, so that one is kept.)
            if (lines.Count == 0 && start == 0)
            {
                if (File.Exists(path))
                    File.Delete(path);

                return;
            }

            var sb = new StringBuilder();

            sb.AppendLine(Version);
            sb.AppendLine(
                "# one line per COPY of a ranked gun: ship id <TAB> weapon " +
                "file <TAB> where it is <TAB> rank <TAB> xp <TAB> draws <TAB> " +
                "stations paid <TAB> choices owed");

            // The station the run began beside, which pays no XP. Too short to
            // be a record line, so a build before this one skips it.
            if (start != 0)
            {
                sb.AppendLine(StartTag + "\t" +
                              start.ToString(CultureInfo.InvariantCulture));
            }

            for (int i = 0; i < lines.Count; i++)
                sb.AppendLine(lines[i]);

            File.WriteAllText(path, sb.ToString());

            Log.LogInfo(
                "rank: saved " + lines.Count + " ranked gun(s)" +
                (unplaced > 0
                    ? " (" + unplaced + " more are no longer anywhere the " +
                      "game saves a module, so their rank ends here)"
                    : "") + ".");
        }

        // One v2 line per ranked copy, keyed by WHERE it is. The save writes
        // these to the sidecar; a New Game Plus jump (ForgeRankCarry) holds
        // them in memory across the new world's run-entry reset. One builder,
        // so the two can never write different records.
        internal static List<string> RecordLines(out int unplaced)
        {
            var lines = new List<string>();
            unplaced = 0;

            List<KeyValuePair<Module, Place>> ordered;
            Dictionary<Module, Place> places = Locate(out ordered);

            foreach (ForgeRankCopies.Copy copy in ForgeRankCopies.All())
            {
                ForgeRank.Progress p = copy.p;

                if (p == null || copy.cfg == null)
                    continue;

                // A gun at rank 0 with nothing drawn, no XP banked, no station
                // paid and nothing owed is in exactly the state a fresh copy
                // starts in, so it needs no line - and see trap 2 above for
                // why the FILE still has to be rewritten when every line goes.
                if (p.rank <= 0 && p.xp <= 0f && p.draws.Count == 0 &&
                    p.paidStations.Count == 0 && p.owed <= 0)
                {
                    continue;
                }

                Place place;

                if (!places.TryGetValue(copy.module, out place))
                {
                    // Nowhere the save stores a module - sold, or scrapped by
                    // something. Its rank goes with it.
                    unplaced++;
                    continue;
                }

                lines.Add(string.Join("\t", new[]
                {
                    place.shipId.ToString(CultureInfo.InvariantCulture),
                    copy.cfg.fileName.Replace('\t', ' '),
                    place.where,
                    p.rank.ToString(CultureInfo.InvariantCulture),
                    p.xp.ToString("R", CultureInfo.InvariantCulture),
                    Draws(p),
                    Stations(p),
                    p.owed.ToString(CultureInfo.InvariantCulture)
                }));
            }

            return lines;
        }

        private static string Draws(ForgeRank.Progress p)
        {
            if (p.draws.Count == 0)
                return "-";

            var sb = new StringBuilder();

            foreach (var d in p.draws)
            {
                if (sb.Length > 0)
                    sb.Append(',');

                sb.Append(d.Key.Replace(',', ' ').Replace(':', ' '))
                  .Append(':')
                  .Append(d.Value.ToString(CultureInfo.InvariantCulture));
            }

            return sb.ToString();
        }

        private static string Stations(ForgeRank.Progress p)
        {
            if (p.paidStations.Count == 0)
                return "-";

            var sb = new StringBuilder();

            foreach (int id in p.paidStations)
            {
                if (sb.Length > 0)
                    sb.Append(',');

                sb.Append(id.ToString(CultureInfo.InvariantCulture));
            }

            return sb.ToString();
        }

        // ------------------------------------------------------------------
        // Reading
        // ------------------------------------------------------------------
        [HarmonyPatch(typeof(Punk.SaveLoad.GameSaver), "Load")]
        public class OnLoad
        {
            static void Postfix(
                Punk.SaveLoad.GameSaver __instance, string folderName,
                bool __result)
            {
                // `Load` returns false when there is no save there at all, and
                // in that case nothing was restored to match against.
                if (!__result)
                    return;

                try
                {
                    Read(__instance, folderName);
                }
                catch (Exception e)
                {
                    Log.LogWarning("rank not restored: " + e.Message);
                }
            }
        }

        internal class Record
        {
            public int shipId;
            public string file;
            public string where;       // null for a v1 line
            public ForgeRank.Progress p;
        }

        private static void Read(
            Punk.SaveLoad.GameSaver saver, string folderName)
        {
            string root = null;

            if (saver != null)
            {
                root = Traverse.Create(saver)
                    .Field("savesRootDirectory").GetValue<string>();
            }

            // The field is private, so if it is ever renamed this rebuilds it
            // the same way the constructor does rather than failing.
            if (string.IsNullOrEmpty(root))
                root = Path.Combine(Application.persistentDataPath, "saves");

            string path = Path.Combine(
                Path.Combine(root, folderName ?? string.Empty), FileName);

            if (!File.Exists(path))
                return;

            string[] raw = File.ReadAllLines(path);
            bool sawVersion = false;
            bool v1 = false;
            var records = new List<Record>();

            for (int i = 0; i < raw.Length; i++)
            {
                string line = (raw[i] ?? string.Empty).Trim();

                if (line.Length == 0 || line[0] == '#')
                    continue;

                if (!sawVersion)
                {
                    sawVersion = true;

                    if (line == VersionV1)
                    {
                        v1 = true;
                        continue;
                    }

                    if (line != Version)
                    {
                        Log.LogWarning(
                            "rank: the save's progress record is version '" +
                            line + "' and this build reads " + Version +
                            " (and " + VersionV1 + "), so it is ignored and " +
                            "every ranked weapon starts this session at rank 0.");

                        return;
                    }

                    continue;
                }

                // The run's start station (see ForgeRankStation).
                if (line.StartsWith(StartTag + "\t", StringComparison.Ordinal))
                {
                    int start;

                    if (int.TryParse(line.Substring(StartTag.Length + 1).Trim(),
                                     NumberStyles.Integer,
                                     CultureInfo.InvariantCulture, out start))
                    {
                        ForgeRankStation.StartStation = start;
                    }

                    continue;
                }

                Record r = v1 ? ParseV1(line) : ParseV2(line);

                if (r != null)
                    records.Add(r);
            }

            if (records.Count == 0)
                return;

            Attach(records);
        }

        // v1: ship, file, rank, xp, draws, stations
        private static Record ParseV1(string line)
        {
            string[] bits = line.Split('\t');

            if (bits.Length < 4)
                return null;

            int shipId, rank;
            float xp;

            if (!int.TryParse(bits[0], NumberStyles.Integer,
                              CultureInfo.InvariantCulture, out shipId) ||
                !int.TryParse(bits[2], NumberStyles.Integer,
                              CultureInfo.InvariantCulture, out rank) ||
                !float.TryParse(bits[3], NumberStyles.Float,
                                CultureInfo.InvariantCulture, out xp))
            {
                return null;
            }

            var p = new ForgeRank.Progress();
            p.rank = Mathf.Max(0, rank);
            p.xp = Mathf.Max(0f, xp);

            if (bits.Length > 4)
                ReadDraws(p, bits[4]);

            if (bits.Length > 5)
                ReadStations(p, bits[5]);

            return new Record { shipId = shipId, file = bits[1], where = null, p = p };
        }

        // v2: ship, file, where, rank, xp, draws, stations, owed
        internal static Record ParseV2(string line)
        {
            string[] bits = line.Split('\t');

            if (bits.Length < 5)
                return null;

            int shipId, rank;
            float xp;

            if (!int.TryParse(bits[0], NumberStyles.Integer,
                              CultureInfo.InvariantCulture, out shipId) ||
                !int.TryParse(bits[3], NumberStyles.Integer,
                              CultureInfo.InvariantCulture, out rank) ||
                !float.TryParse(bits[4], NumberStyles.Float,
                                CultureInfo.InvariantCulture, out xp))
            {
                return null;
            }

            var p = new ForgeRank.Progress();
            p.rank = Mathf.Max(0, rank);
            p.xp = Mathf.Max(0f, xp);

            if (bits.Length > 5)
                ReadDraws(p, bits[5]);

            if (bits.Length > 6)
                ReadStations(p, bits[6]);

            int owed;

            if (bits.Length > 7 &&
                int.TryParse(bits[7], NumberStyles.Integer,
                             CultureInfo.InvariantCulture, out owed))
            {
                p.owed = Mathf.Max(0, owed);
            }

            return new Record
            {
                shipId = shipId,
                file = bits[1],
                where = bits[2].Trim(),
                p = p
            };
        }

        // Hand each record to the module now sitting where it was saved.
        internal static void Attach(List<Record> records)
        {
            List<KeyValuePair<Module, Place>> ordered;
            Locate(out ordered);

            var taken = new HashSet<Module>();
            int restored = 0, lost = 0, owed = 0;

            foreach (Record r in records)
            {
                Module hit = null;

                foreach (var kv in ordered)
                {
                    if (taken.Contains(kv.Key) || !IsFile(kv.Key, r.file))
                        continue;

                    if (r.where != null)
                    {
                        // Exact: the same place, on the same ship for a slot.
                        if (kv.Value.where != r.where)
                            continue;

                        if (r.where.StartsWith("slot:") && kv.Value.shipId != r.shipId)
                            continue;
                    }
                    else if (kv.Value.shipId != 0 && kv.Value.shipId != r.shipId)
                    {
                        // v1: the first copy of the file, on that ship or
                        // anywhere shared (vault, ground).
                        continue;
                    }

                    hit = kv.Key;
                    break;
                }

                if (hit == null)
                {
                    lost++;
                    continue;
                }

                taken.Add(hit);

                ForgeRankCopies.Copy adopted = ForgeRankCopies.Adopt(hit, r.p);

                if (adopted != null)
                {
                    restored++;

                    // The ship it was saved on, so the queue can find it again
                    // before the gun is next built.
                    if (r.shipId != 0)
                        adopted.shipId = r.shipId;

                    owed += Mathf.Max(0, r.p.owed);
                }
                else
                {
                    lost++;
                }
            }

            Log.LogInfo(
                "rank: " + restored + " ranked gun(s) restored from your save" +
                (lost > 0
                    ? " (" + lost + " record(s) found no gun where they were " +
                      "saved - the gun was moved or is gone - and were dropped)"
                    : "") + ".");

            // ★ R20 256: "saving and quiting with a rank choice out and coming
            // back removes that level choice". The count always came back; the
            // SET did not. Said here so the log shows the one and the next
            // lines show the other.
            if (owed > 0)
            {
                Log.LogInfo(
                    "rank: " + owed + " choice set(s) were still owed when you " +
                    "saved. They come back out around your ship once you are " +
                    "flying - one set at a time.");

                ForgeRankQueue.Kick();
            }
        }

        private static bool IsFile(Module m, string file)
        {
            WeaponData wd = ForgeRankCopies.WeaponOf(m);
            ForgeRank.Config cfg;

            return wd != null && ForgeRank.TryGet(wd, out cfg) && cfg != null &&
                   cfg.fileName == file;
        }

        private static void ReadDraws(ForgeRank.Progress p, string s)
        {
            if (string.IsNullOrEmpty(s) || s == "-")
                return;

            string[] parts = s.Split(',');

            for (int i = 0; i < parts.Length; i++)
            {
                int at = parts[i].LastIndexOf(':');

                if (at <= 0)
                    continue;

                string feature = parts[i].Substring(0, at).Trim();
                int times;

                if (!int.TryParse(parts[i].Substring(at + 1),
                                  NumberStyles.Integer,
                                  CultureInfo.InvariantCulture, out times))
                {
                    continue;
                }

                if (feature.Length > 0 && times > 0)
                    p.draws[feature] = times;
            }
        }

        private static void ReadStations(ForgeRank.Progress p, string s)
        {
            if (string.IsNullOrEmpty(s) || s == "-")
                return;

            string[] parts = s.Split(',');

            for (int i = 0; i < parts.Length; i++)
            {
                int id;

                if (int.TryParse(parts[i], NumberStyles.Integer,
                                 CultureInfo.InvariantCulture, out id))
                {
                    p.paidStations.Add(id);
                }
            }
        }
    }
}
