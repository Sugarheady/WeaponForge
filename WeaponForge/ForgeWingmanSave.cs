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
    // Wingmen survive saving and loading.
    //
    // WHAT IS ACTUALLY SAVED IS ONE INTEGER PER GADGET. A phantom orb has no
    // health, no inventory and no position of its own - it is art at a computed
    // point on a ring - so re-spawning `n` of them from a saved count is
    // indistinguishable from having persisted the bodies themselves, and it
    // avoids the trap this project has walked into before: spawn-time state
    // (colour, scale, the stripped scripts) silently reverting on reload,
    // because only mementos replay. The companions are rebuilt FROM YOUR JSON,
    // so a wingman file you edited between saving and loading takes effect.
    //
    // WHERE IT GOES. `GameSaver.Save(string)` and `GameSaver.Load(string)` are
    // both public and both name a save FOLDER, so the sidecar lives in that
    // folder. That makes it slot-correct for free: a coop save and a normal
    // save cannot read each other's wingmen, and deleting a save takes its
    // sidecar with it (`DeleteSave` removes the directory recursively).
    //
    // A MISSING OR UNREADABLE SIDECAR MEANS ZERO DEPLOYED, NEVER A GUESS. The
    // failure mode of guessing is a player loading a save and finding
    // companions they never paid the activation cost for, which is worse than
    // pressing the key again. Every failure path here lands on zero.
    //
    // The matching hazard, and it is the one that needed thought: a save that
    // still holds three wingmen written five seconds ago must not be re-applied
    // by a LATER save that has none. So a save with nothing deployed DELETES
    // the sidecar rather than skipping the write - see Write.
    public static class ForgeWingmanSave
    {
        private static readonly ManualLogSource Log =
            ForgeLog.Source("WeaponForge.Wingman");

        // Named so a human poking around in a save folder can see at a glance
        // that it is not one of the game's own files - those have no extension
        // (`entities`, `world`, `rundata`) and are Odin-serialised and
        // compressed. This one is plain text on purpose: it is four small
        // numbers, and a format you can read in Notepad is a format you can
        // diagnose without the mod.
        private const string FileName = "weaponforge_wingmen.txt";

        // v2 adds the GADGET SLOT (R19 test 267: two copies of one file are two
        // gadgets, each with its own record). v1 is still READ - its lines
        // carry no slot and match any slot of that file, which is exactly what
        // v1 meant - so a save made before this build keeps its companions.
        private const string Version = "v2";
        private const string VersionV1 = "v1";

        // How long after a load we keep looking for the ship. Entries for a
        // gadget the player is no longer carrying would otherwise keep the
        // restore hook walking every grid on every unit forever.
        private const float PatienceSeconds = 30f;

        private class Pending
        {
            public int id;          // the owner entity's instanceId
            public string file;     // the wingman weapon's file name
            public string slot;     // "Active2"; "" = any slot (a v1 record)
            public int count;
            public bool stowed;
        }

        private static readonly List<Pending> _pending = new List<Pending>();
        private static float _loadedAt = -1f;
        private static bool _saidUnclaimed;

        // True while there is something to put back and somebody might still
        // turn up to put it back onto.
        //
        // This has a side effect - it gives up, and says so, once the patience
        // window closes - because the alternative is a second timer checked
        // from somewhere else, and every caller is on the main thread inside a
        // Harmony postfix.
        public static bool HasPending
        {
            get
            {
                if (_pending.Count == 0)
                    return false;

                if (_loadedAt < 0f ||
                    Time.unscaledTime - _loadedAt < PatienceSeconds)
                {
                    return true;
                }

                GiveUp();

                return false;
            }
        }

        // ------------------------------------------------------------------
        // Writing
        // ------------------------------------------------------------------

        // `Save(bool coop)` calls `Save(string)`, so one patch covers both the
        // normal path and the debug menu's named slots. The overload has to be
        // named explicitly or Harmony cannot tell the two apart.
        [HarmonyPatch(typeof(Punk.SaveLoad.GameSaver), "Save",
                      new Type[] { typeof(string) })]
        public class OnSave
        {
            static void Postfix(Punk.SaveLoad.SaveFolder __result)
            {
                Write(__result.RootDirectory);
            }
        }

        private static void Write(string folder)
        {
            if (string.IsNullOrEmpty(folder))
                return;

            try
            {
                var lines = new List<string>();

                ForgeWingmanController.All(c =>
                {
                    if (c == null || c.Config == null || !c.Config.persist)
                        return;

                    // ★ A DRONE-BODIED GADGET STORES NO COUNT, AND WRITING ONE
                    // WOULD DOUBLE THE COMPLEMENT.
                    //
                    // A drone body is a real `SavableEntity`, so the GAME saves
                    // it: `Unit.Data.Memento` carries `ownerId`,
                    // `connectionToOwner` AND every non-shared tank value, and
                    // replays them on load. That is strictly better than a
                    // count - it brings a damaged companion back damaged, which
                    // is what "load the state we were in" actually means - but
                    // it means the sidecar must not ALSO hand back a number, or
                    // you get eight where you saved four.
                    //
                    // The STOWED flag stays ours either way. It is a fact about
                    // the gadget rather than about the bodies, and nothing in
                    // the game's save records it - without it, a `recall`-keyed
                    // gadget you deliberately put away would be waiting for you
                    // on the next load, because a fresh controller is not
                    // stowed and that key mode auto-deploys.
                    bool drone = c.Config.IsDrone;

                    if (drone)
                    {
                        if (!c.Stowed)
                            return;
                    }
                    else if (c.Deployed <= 0 && !c.Stowed)
                    {
                        // Nothing out and nothing stowed is the same state a
                        // fresh run starts in, so it does not need a line.
                        return;
                    }

                    lines.Add(
                        c.OwnerId.ToString(CultureInfo.InvariantCulture) + "\t" +
                        (drone ? 0 : c.Deployed)
                            .ToString(CultureInfo.InvariantCulture) + "\t" +
                        (c.Stowed ? "1" : "0") + "\t" +
                        c.SlotKey + "\t" +
                        (c.Config.fileName ?? string.Empty).Replace('\t', ' '));
                });

                // ANYTHING THE LAST LOAD HAS NOT HANDED BACK YET GOES STRAIGHT
                // THROUGH. The only save in the game is "Save and Exit", so
                // Continue -> immediately Save and Exit is a real sequence a
                // player can perform, and until the ship's first grid update
                // the companions exist only as a record waiting to be claimed.
                // Without this that save would find no live controllers, write
                // nothing, and DELETE the record they were sitting in.
                // Read through the property, so an expired record is dropped
                // here on exactly the terms it is dropped everywhere else.
                for (int i = 0; HasPending && i < _pending.Count; i++)
                {
                    Pending p = _pending[i];

                    string line =
                        p.id.ToString(CultureInfo.InvariantCulture) + "\t" +
                        p.count.ToString(CultureInfo.InvariantCulture) + "\t" +
                        (p.stowed ? "1" : "0") + "\t" + (p.slot ?? "") +
                        "\t" + p.file;

                    // A controller CAN exist alongside its own unclaimed entry
                    // for a frame or two: the gadget key builds one without
                    // going through the restore. Its live count is the truer
                    // number, so the live line wins and this one is dropped.
                    if (!Claimed(lines, p))
                        lines.Add(line);
                }

                string path = Path.Combine(folder, FileName);

                // THE DELETE IS LOAD-BEARING, not tidiness. Deploy three, save,
                // recall them all, save again: without this the second save
                // writes nothing, last night's file survives untouched, and
                // loading hands you three companions you had already sent away.
                // "Write only when there is something to write" is the shape of
                // that bug, and it reads as correct.
                if (lines.Count == 0)
                {
                    if (File.Exists(path))
                        File.Delete(path);

                    return;
                }

                var sb = new StringBuilder();

                sb.AppendLine(
                    "# WeaponForge - how many wingmen were deployed when this " +
                    "save was written.");
                sb.AppendLine(
                    "# Only the COUNT is stored; the companions are rebuilt " +
                    "from your weapon JSON on load,");
                sb.AppendLine(
                    "# so edits to the file take effect. Delete this and you " +
                    "simply load with none out.");
                sb.AppendLine(
                    "# owner entity id <TAB> deployed <TAB> stowed <TAB> " +
                    "gadget slot <TAB> weapon file");
                sb.AppendLine(Version);

                for (int i = 0; i < lines.Count; i++)
                    sb.AppendLine(lines[i]);

                // The game creates the save directory somewhere inside an async
                // method the decompiler does not show, so this postfix cannot
                // assume it is there yet. Creating it is idempotent and costs a
                // syscall once per save.
                Directory.CreateDirectory(folder);

                File.WriteAllText(path, sb.ToString());
            }
            catch (Exception e)
            {
                // A save that cannot write the sidecar is still a good save of
                // the game. The cost is that the wingmen come back as zero,
                // which is the documented safe answer.
                Log.LogWarning(
                    "wingman: could not record how many companions were out (" +
                    e.Message + "). The save itself is fine; you will load " +
                    "with none deployed and can press the gadget key again.");
            }
        }

        // Has a live controller already written a line for this owner, this
        // slot and this weapon file? The same (owner, slot, file) key the
        // restore matches on - where a pending v1 record (no slot) is claimed
        // by any slot's line for that file, as it would be on restore.
        private static bool Claimed(List<string> lines, Pending p)
        {
            string id = p.id.ToString(CultureInfo.InvariantCulture);

            for (int i = 0; i < lines.Count; i++)
            {
                string[] bits = lines[i].Split('\t');

                if (bits.Length >= 5 && bits[0] == id &&
                    string.Join("\t", bits, 4, bits.Length - 4) == p.file &&
                    (string.IsNullOrEmpty(p.slot) || bits[3] == p.slot))
                {
                    return true;
                }
            }

            return false;
        }

        // ------------------------------------------------------------------
        // Reading
        // ------------------------------------------------------------------

        // A POSTFIX, deliberately. `ForgeBurnResetPatch.OnContinue` is a PREFIX
        // on this same method and drops every scrap of per-run state, this
        // table included - so reading in a prefix would hand the reset
        // something to throw away. Prefixes run, then the original, then
        // postfixes; reading here is the only ordering that survives.
        [HarmonyPatch(typeof(Punk.SaveLoad.GameSaver), "Load")]
        public class OnLoad
        {
            static void Postfix(
                Punk.SaveLoad.GameSaver __instance, string folderName,
                bool __result)
            {
                // `Load` returns false when there is no save there at all, and
                // in that case nothing was restored to match against.
                if (__result)
                {
                    ForgeWingmanPatch.NoteLoad();
                    Read(__instance, folderName);
                }
            }
        }

        private static void Read(
            Punk.SaveLoad.GameSaver saver, string folderName)
        {
            _pending.Clear();
            _loadedAt = -1f;
            _saidUnclaimed = false;

            try
            {
                string root = null;

                if (saver != null)
                {
                    root = Traverse.Create(saver)
                        .Field("savesRootDirectory").GetValue<string>();
                }

                // The field is private, so if it is ever renamed this rebuilds
                // it the same way the constructor does rather than failing.
                if (string.IsNullOrEmpty(root))
                    root = Path.Combine(Application.persistentDataPath, "saves");

                string path = Path.Combine(
                    Path.Combine(root, folderName ?? string.Empty), FileName);

                if (!File.Exists(path))
                    return;

                string[] raw = File.ReadAllLines(path);
                bool sawVersion = false;
                bool v1 = false;

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
                                "wingman: the save's companion record is " +
                                "version '" + line + "' and this build reads " +
                                Version + ", so it is ignored and you load " +
                                "with none deployed.");

                            return;
                        }

                        continue;
                    }

                    string[] bits = line.Split('\t');

                    // v1: id, count, stowed, file.  v2: id, count, stowed,
                    // slot, file.
                    int fileAt = v1 ? 3 : 4;

                    if (bits.Length < fileAt + 1)
                        continue;

                    int id, count;

                    if (!int.TryParse(bits[0], NumberStyles.Integer,
                                      CultureInfo.InvariantCulture, out id))
                    {
                        continue;
                    }

                    if (!int.TryParse(bits[1], NumberStyles.Integer,
                                      CultureInfo.InvariantCulture, out count))
                    {
                        continue;
                    }

                    _pending.Add(new Pending
                    {
                        id = id,
                        count = Mathf.Max(0, count),
                        stowed = bits[2] == "1",
                        slot = v1 ? "" : bits[3].Trim(),

                        // A weapon file name cannot contain a tab, but joining
                        // the tail back together costs a line and means a file
                        // name that somehow does is restored rather than
                        // truncated.
                        file = string.Join("\t", bits, fileAt, bits.Length - fileAt),
                    });
                }

                if (_pending.Count == 0)
                    return;

                _loadedAt = Time.unscaledTime;

                Log.LogInfo(
                    "wingman: this save had " + Describe() + " deployed. They " +
                    "are rebuilt from your JSON as soon as your ship is in the " +
                    "world.");
            }
            catch (Exception e)
            {
                // EVERY failure lands on zero. See the class note.
                _pending.Clear();
                _loadedAt = -1f;

                Log.LogWarning(
                    "wingman: the save's companion record could not be read (" +
                    e.Message + "), so you load with none deployed.");
            }
        }

        private static string Describe()
        {
            var sb = new StringBuilder();

            for (int i = 0; i < _pending.Count; i++)
            {
                if (sb.Length > 0)
                    sb.Append(", ");

                sb.Append(_pending[i].stowed
                    ? _pending[i].file + " stowed"
                    : _pending[i].count + " x " + _pending[i].file);
            }

            return sb.ToString();
        }

        // ------------------------------------------------------------------
        // Putting them back
        // ------------------------------------------------------------------

        // Called from ForgeWingmanPatch.Ensure, for one gadget on one ship,
        // BEFORE the auto-deploy that `none` / `burst` / `recall` rely on.
        // Order matters both ways: a gadget stowed when you saved must not be
        // deployed for you the instant you load, and a partial complement must
        // not be topped up to full.
        public static void Apply(
            Unit unit, ForgeWingman.Config cfg, ForgeWingmanController ctl)
        {
            if (unit == null || cfg == null || ctl == null)
                return;

            if (!HasPending)
                return;

            // `persist: false` USED TO RETURN HERE AND LEAVE THE RECORD SITTING
            // IN `_pending` - and the pass-through loop in `Write` re-wrote it
            // on the next save, unfiltered. So a sidecar written while persist
            // was ON became IMMORTAL the moment you turned persist off: the
            // restore would not claim it, the write would not drop it, and
            // deleting the file by hand only bought you until the next save.
            // That is R17 test 222 - *"It appears that the save file is created
            // even if it's set to persist false."* He was right, and it was not
            // the write path creating it; it was the read path refusing to
            // consume what it had already loaded.
            //
            // A REFUSAL HAS TO CONSUME THE THING IT REFUSES, or it is a
            // deferral. Same shape as a skipping prefix that never stamps the
            // fire-rate clock: the decision is made and the state that provoked
            // it is left exactly where it was, so it is made again forever.
            //
            // Dropping it is also the right MEANING. `persist: false` says
            // "this gadget does not come back", and honouring that includes
            // forgetting what a previous save said about it - otherwise turning
            // persist back on months later silently hands you a complement
            // frozen at whatever the count was the day you switched it off.
            if (!cfg.persist)
            {
                Forget(unit, cfg);
                return;
            }

            int id = IdOf(unit);
            int hit = -1;

            for (int i = 0; i < _pending.Count; i++)
            {
                if (_pending[i].id == id && _pending[i].file == cfg.fileName &&
                    SlotMatches(_pending[i], ctl))
                {
                    hit = i;
                    break;
                }
            }

            // THE FALLBACK, AND THE CONDITION THAT MAKES IT SAFE.
            //
            // The exact key is owner entity plus file. It should always hit:
            // `EntityData.instanceId` is written into the save and read back
            // out of it, so it is the same number either side of a load. The
            // fallback is insurance against that assumption, and it accepts a
            // file name that appears exactly once in the record.
            //
            // ONLY IN SINGLE PLAYER, and the first version of this got that
            // wrong. "Exactly one entry with that name" is not the same as "no
            // ambiguity": in coop, a player carrying the gadget with NONE
            // deployed writes no line at all, so the other player's entry is
            // the only one bearing that file name - and the fallback would hand
            // one ship the other's companions. That is precisely the failure
            // this feature is built to avoid, arriving through the safety net
            // rather than the mechanism. Caught by the randomised half of
            // wingmansavetest.py, not by reading it.
            //
            // Single player has ONE ship, so an entry in the record can only
            // have come from the ship now asking for it. A coop game, or a
            // question we cannot answer, refuses and costs a key press.
            if (hit < 0)
            {
                if (IsCoop())
                    return;

                int matches = 0;

                for (int i = 0; i < _pending.Count; i++)
                {
                    if (_pending[i].file != cfg.fileName ||
                        !SlotMatches(_pending[i], ctl))
                    {
                        continue;
                    }

                    matches++;
                    hit = i;
                }

                if (matches != 1)
                    return;
            }

            Pending p = _pending[hit];

            _pending.RemoveAt(hit);

            if (_pending.Count == 0)
                _loadedAt = -1f;

            ctl.Restore(p.count, p.stowed);

            // At most one line per gadget per load, because the entry is
            // consumed - and it is the line that answers "did my companions
            // come back, or did I just deploy them without noticing?".
            Log.LogInfo(
                cfg.fileName + ": wingman - " +
                (p.stowed
                    ? "stowed, exactly as you left it. Press the gadget key " +
                      "to send them out."
                    : ctl.Deployed + " companion(s) restored from your save" +
                      (ctl.Deployed < p.count
                          ? " (you saved " + p.count + ", but the cap is " +
                            ctl.Capacity + " right now)"
                          : "")) + ".");
        }

        // A record with a slot belongs to that slot's gadget only; a v1 record
        // (no slot) to whichever copy of the file asks first. A controller
        // that does not know its own slot yet matches only a slotless record -
        // guessing a slot here would hand one gadget the other's companions,
        // which is the thing per-slot records exist to stop.
        private static bool SlotMatches(Pending p, ForgeWingmanController ctl)
        {
            if (string.IsNullOrEmpty(p.slot))
                return true;

            return ctl != null && p.slot == ctl.SlotKey;
        }

        // Drop any record this gadget would have claimed, without restoring it.
        // Only the EXACT key (owner + file), never the single-match fallback:
        // that fallback exists to hand companions back when an id has gone
        // astray, and guessing is acceptable when the cost is a key press. Here
        // the cost is deleting somebody else's record, so a miss must simply
        // miss.
        private static void Forget(Unit unit, ForgeWingman.Config cfg)
        {
            int id = IdOf(unit);

            for (int i = _pending.Count - 1; i >= 0; i--)
            {
                if (_pending[i].id != id || _pending[i].file != cfg.fileName)
                    continue;

                _pending.RemoveAt(i);

                Log.LogInfo(
                    cfg.fileName + ": wingman \"persist\": false - this save " +
                    "still held a record of companions deployed (written " +
                    "before you turned persist off). It has been discarded " +
                    "rather than restored, and the next save will not write " +
                    "it back.");
            }

            if (_pending.Count == 0)
                _loadedAt = -1f;
        }

        // Unanswerable counts as coop, because the only thing the answer gates
        // is a fallback that is safe in exactly one case.
        private static bool IsCoop()
        {
            try
            {
                GameController game = ServiceLocator.Get<GameController>();

                return game == null || game.IsCoop;
            }
            catch
            {
                return true;
            }
        }

        // The owner entity's save id. It is written into the save and read back
        // out of it (`EntityData.Memento.instanceId`), so it is the same number
        // either side of a load - which is what makes it usable as a key.
        public static int IdOf(Unit unit)
        {
            if (unit == null)
                return 0;

            try
            {
                var data = unit.ComponentData;

                if (data == null || data.entity == null)
                    return 0;

                return data.entity.instanceId;
            }
            catch
            {
                return 0;
            }
        }

        private static void GiveUp()
        {
            if (!_saidUnclaimed)
            {
                _saidUnclaimed = true;

                Log.LogInfo(
                    "wingman: the save recorded companions for " + Describe() +
                    " and no ship turned up carrying that gadget within " +
                    (int)PatienceSeconds + " seconds, so they are not " +
                    "restored. That is expected if you stripped the gadget " +
                    "out of your grid before saving.");
            }

            _pending.Clear();
            _loadedAt = -1f;
        }

        // Run entry. A new run must never inherit a loaded save's companions;
        // on the LOAD path this runs in the prefix, before Read, which is the
        // ordering the OnLoad note describes.
        public static void Reset()
        {
            _pending.Clear();
            _loadedAt = -1f;
            _saidUnclaimed = false;
        }
    }
}
