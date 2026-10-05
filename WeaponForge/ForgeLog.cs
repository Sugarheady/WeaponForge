using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using BepInEx;
using BepInEx.Logging;

namespace WeaponForge
{
    // Weapon Forge's OWN log file: BepInEx\WeaponForge.log.
    //
    // His ask, 2026-09-25: a separate log per mod, so LogOutput.log is not
    // buried under ours when other mods are installed - "it's not a pain to
    // troubleshoot for other people." Our three mods wrote ~450 of the 486
    // lines in his last LogOutput.log; this one alone ~390.
    //
    // ★ HOW IT WORKS, AND WHY NO LOG LINE IN THE MOD HAD TO CHANGE.
    // `Logger.CreateLogSource(name)` is, in BepInEx 6.0.0-be.785, exactly
    // `new ManualLogSource(name)` plus `Logger.Sources.Add` - and that Add is
    // the ONLY thing that wires a source's LogEvent to the shared listeners
    // (LogOutput.log, the console). `ManualLogSource.Log` does nothing but
    // raise its own event. So a source made here and never added reaches
    // nobody but us: every `Log.LogInfo(...)` in the mod keeps working, and
    // lands in this file instead. Every source in the mod is one
    // `static readonly ManualLogSource Log = ForgeLog.Source("...")` line;
    // logfiletest.py fails if a `CreateLogSource` comes back.
    //
    // His four answers decided the rest:
    //   * Our lines go to THIS FILE ONLY - not the console either.
    //   * ERRORS are also copied to LogOutput.log (and so the console), so
    //     anyone troubleshooting sees at once that a Forge mod broke.
    //   * The launch before is kept as WeaponForge.prev.log. A relaunch (the
    //     Game Mode Forge restart row, or quitting to the desktop for R19 test
    //     259) would otherwise wipe the session you wanted to read.
    //   * The files sit in the BepInEx folder, beside LogOutput.log.
    //
    // ★ AND ONE THING NOBODY ASKED FOR THAT IT FIXES: BepInEx's `WriteUnityLog`
    // defaults to false, which puts "Unity Log" on DiskLogListener's
    // blacklist - so the GAME's own errors, including an exception thrown out
    // of one of our Harmony patches, were in NO file he could send. Not one
    // "Unity Log" line in any saved log. `GameErrorCatcher` copies any game
    // error whose stack trace names a class of this mod into this file.
    //
    // ⚠ Diagnostic code must not throw (it cost five rounds once). Every
    // path here catches, and every failure FALLS BACK TO LogOutput.log AND
    // SAYS SO - a log that silently went nowhere is the worst possible
    // version of this feature.
    internal static class ForgeLog
    {
        // Named once. The file, the previous launch's copy and the pointer
        // line are all derived from these.
        private const string Name = "WeaponForge";
        private const string Title = "Weapon Forge";

        // A game error belongs in this file when its stack trace names a
        // class in this namespace. See NamesThisMod for why it is not a plain
        // Contains.
        private const string Namespace = "WeaponForge";

        // BepInEx's name for the source that relays Unity's own log.
        private const string UnitySource = "Unity Log";

        private static readonly object Gate = new object();

        private static StreamWriter _writer;
        private static string _fileName;
        private static bool _opened;
        private static bool _closed;
        private static bool _saidWriteFailed;
        private static bool _catching;
        private static string _pending;

        // [Logging] OwnLogFile. True until Start says otherwise, so a line
        // logged before the plugin's Awake still goes somewhere sensible.
        private static volatile bool _ownFile = true;

        // The plugin's own BaseUnityPlugin.Logger - the one source that IS
        // registered, so its lines ("loaded", BUILD, "patches applied") are
        // what LogOutput.log keeps.
        private static ManualLogSource _main;

        // Replaces `BepInEx.Logging.Logger.CreateLogSource(name)`.
        public static ManualLogSource Source(string name)
        {
            var source = new ManualLogSource(name);

            source.LogEvent += OnEvent;

            return source;
        }

        // FIRST THING in the plugin's Awake, before its first log line, so
        // "loaded" and BUILD are in this file as well as in LogOutput.log -
        // the file has to stand on its own when it is the only one sent.
        public static void Start(ManualLogSource main, bool ownFile)
        {
            try
            {
                _main = main;
                _ownFile = ownFile;

                if (!ownFile)
                    return;

                main.LogEvent += OnMainEvent;

                lock (Gate)
                {
                    if (!_opened)
                        Open();
                }

                SayPending();

                if (!_catching)
                {
                    _catching = true;
                    Logger.Listeners.Add(new GameErrorCatcher());
                }
            }
            catch { }
        }

        // Appended to the plugin's "loaded" line in LogOutput.log, so the one
        // line anybody reads there says where everything else went.
        public static string Where
        {
            get
            {
                if (!_ownFile)
                    return " - everything is logged here ([Logging] OwnLogFile = false)";

                if (_writer == null)
                    return "";

                return " - full log in BepInEx\\" + _fileName +
                       " (the launch before this one: " + Name + ".prev.log). " +
                       "Only errors are copied here.";
            }
        }

        // OnApplicationQuit. The Game Mode Forge restart row starts the NEW
        // game before this one quits, so let go of the file promptly - the
        // next launch waits for it (see TryOpen) but should not have to.
        public static void Close()
        {
            lock (Gate)
            {
                _closed = true;

                if (_writer == null)
                    return;

                try
                {
                    _writer.Flush();
                    _writer.Dispose();
                }
                catch { }

                _writer = null;
            }
        }

        // ---- where a line goes ------------------------------------------

        private static void OnEvent(object sender, LogEventArgs e)
        {
            try
            {
                // The switch off, or the file unavailable: straight to
                // LogOutput.log, exactly as before this file existed. Never
                // nowhere.
                if (!_ownFile || !WriteLine(e.ToString()))
                {
                    Forward(sender, e);
                    SayPending();
                    return;
                }

                if ((e.Level & (LogLevel.Error | LogLevel.Fatal)) != LogLevel.None)
                    Forward(sender, e);
            }
            catch { }
        }

        // The plugin's own lines already reach LogOutput.log - its source is
        // registered - so these are only COPIED here, never forwarded.
        private static void OnMainEvent(object sender, LogEventArgs e)
        {
            try
            {
                if (_ownFile)
                    WriteLine(e.ToString());
            }
            catch { }
        }

        // What `Logger.Sources.Add` would have done for this one event: hand
        // it to every listener whose level filter accepts it. The listener
        // list is copy-on-write in be.785 (Add and Remove swap in a new
        // List), so walking it while another thread adds one is safe.
        private static void Forward(object sender, LogEventArgs e)
        {
            foreach (ILogListener listener in Logger.Listeners)
            {
                if (listener == null || listener is GameErrorCatcher)
                    continue;

                if ((listener.LogLevelFilter & e.Level) == LogLevel.None)
                    continue;

                try
                {
                    listener.LogEvent(sender, e);
                }
                catch { }
            }
        }

        private static bool WriteLine(string line)
        {
            lock (Gate)
            {
                if (_closed)
                    return false;

                if (!_opened)
                    Open();

                if (_writer == null)
                    return false;

                try
                {
                    _writer.WriteLine(line);
                    return true;
                }
                catch (Exception ex)
                {
                    // A disk that filled up mid-session. Say so ONCE, then let
                    // every line fall back to LogOutput.log.
                    if (!_saidWriteFailed)
                    {
                        _saidWriteFailed = true;
                        _pending = "could not write to BepInEx\\" + _fileName +
                                   " (" + ex.GetType().Name + ": " + ex.Message +
                                   ") - from here on Weapon Forge logs to " +
                                   "LogOutput.log instead.";
                    }

                    try { _writer.Dispose(); } catch { }
                    _writer = null;
                    return false;
                }
            }
        }

        // Anything the file side needs LogOutput.log to know (it could not be
        // opened, a write failed). Said OUTSIDE Gate by the caller, through
        // the plugin's registered source; held until Start if a line arrived
        // before the plugin's Awake.
        private static void SayPending()
        {
            string text;

            lock (Gate)
            {
                text = _pending;
                _pending = null;
            }

            if (text != null && _main != null)
                _main.LogWarning(text);
        }

        // ---- opening the file -------------------------------------------

        // Called under Gate, once.
        private static void Open()
        {
            _opened = true;

            string folder = null;

            try
            {
                folder = Paths.BepInExRootPath;
            }
            catch { }

            if (string.IsNullOrEmpty(folder))
            {
                _pending = "could not find the BepInEx folder, so Weapon Forge " +
                           "logs to LogOutput.log as it always did.";
                return;
            }

            string name = Name + ".log";
            FileStream stream = TryOpen(Path.Combine(folder, name), 8);
            string previous = null;

            if (stream != null)
            {
                previous = KeepPrevious(stream, Path.Combine(folder, Name + ".prev.log"));
            }
            else
            {
                // Still locked after the wait: a second copy of the game is
                // running. Same fallback BepInEx uses for LogOutput.log.
                for (int i = 2; i <= 5 && stream == null; i++)
                {
                    name = Name + "." + i + ".log";
                    stream = TryOpen(Path.Combine(folder, name), 1);
                }

                if (stream != null)
                {
                    stream.SetLength(0);
                    previous = Name + ".log is in use - is the game running " +
                               "twice? - so this launch writes " + name +
                               ", and " + Name + ".prev.log was left alone.";
                }
            }

            if (stream == null)
            {
                _pending = "could not open " + Name + ".log for writing, so " +
                           "Weapon Forge logs to LogOutput.log as it always did.";
                return;
            }

            _fileName = name;
            _writer = new StreamWriter(stream, new UTF8Encoding(false));
            _writer.AutoFlush = true;   // a crash must not eat the last lines

            _writer.WriteLine(
                "=== " + Title + " log - this launch started " +
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") +
                " - BUILD " + BuildStamp() + " ===");

            _writer.WriteLine(previous ??
                "The launch before this one is in " + Name + ".prev.log. " +
                "BepInEx's own LogOutput.log keeps the load lines and a copy " +
                "of every error.");
        }

        // ★ A RELAUNCH OVERLAPS. The restart row starts the new game and THEN
        // quits this one, so the new launch can find the file still held.
        // Wait for it (8 x 250ms, only when locked) rather than wrongly
        // deciding the game is running twice.
        private static FileStream TryOpen(string path, int attempts)
        {
            for (int i = 0; i < attempts; i++)
            {
                try
                {
                    // FileShare.Read: you can open the file in a viewer while
                    // the game is running.
                    return new FileStream(
                        path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
                }
                catch (IOException)
                {
                    if (i + 1 < attempts)
                        Thread.Sleep(250);
                }
                catch (Exception)
                {
                    return null;   // permissions and the like: waiting will not help
                }
            }

            return null;
        }

        // Copy the last launch into .prev.log through the handle we ALREADY
        // hold, then empty it. Not File.Copy: its read opens with
        // FileShare.Read, which refuses to coexist with our write handle.
        //
        // If .prev.log cannot be written (open in an editor that locks it),
        // the old log is NOT thrown away - this launch is appended below it.
        // Returns the header's second line when it had to do that, else null.
        private static string KeepPrevious(FileStream stream, string prevPath)
        {
            if (stream.Length == 0)
                return null;

            try
            {
                using (var prev = new FileStream(
                    prevPath, FileMode.Create, FileAccess.Write, FileShare.Read))
                {
                    stream.Position = 0;
                    stream.CopyTo(prev);
                }

                stream.SetLength(0);
                stream.Position = 0;
                return null;
            }
            catch
            {
                stream.Seek(0, SeekOrigin.End);
                return "could not write " + Name + ".prev.log (is it open " +
                       "somewhere?), so this launch is appended below the last one.";
            }
        }

        private static string BuildStamp()
        {
            try
            {
                return File.GetLastWriteTime(typeof(ForgeLog).Assembly.Location)
                           .ToString("yyyy-MM-dd HH:mm:ss");
            }
            catch
            {
                return "unknown";
            }
        }

        // ★ WHY NOT `text.Contains("WeaponForge.")`: the three mods' names
        // overlap as text. A frame must START with the namespace - at the
        // start of a line, or after a space, tab, "(" or "<" - so that
        // "GameModeForge." is never read as a match for "ModeForge." and
        // the like. Written the same way in all three mods.
        internal static bool NamesThisMod(string text)
        {
            if (string.IsNullOrEmpty(text))
                return false;

            string marker = Namespace + ".";
            int at = text.IndexOf(marker, StringComparison.Ordinal);

            while (at >= 0)
            {
                if (at == 0)
                    return true;

                char before = text[at - 1];

                if (char.IsWhiteSpace(before) || before == '(' || before == '<')
                    return true;

                at = text.IndexOf(marker, at + 1, StringComparison.Ordinal);
            }

            return false;
        }

        // Copies the game's own errors that name this mod into this file.
        //
        // BepInEx relays Unity's log as the "Unity Log" source, and for an
        // EXCEPTION it appends "\nStack trace:\n" + the trace (read off
        // UnityLogSource.OnUnityLogMessageReceived) - so a throw out of one
        // of our patches carries a `WeaponForge.` frame and is recognisable
        // here. A plain Debug.LogError carries no trace and is left alone.
        //
        // Capped, because a patch that throws in Update throws every frame:
        // three copies of any one error, fifty in all, per launch.
        private sealed class GameErrorCatcher : ILogListener
        {
            private const int PerError = 3;
            private const int Total = 50;

            private readonly Dictionary<string, int> _seen =
                new Dictionary<string, int>();

            private int _copied;

            public LogLevel LogLevelFilter
            {
                get { return LogLevel.Fatal | LogLevel.Error | LogLevel.Warning; }
            }

            public void LogEvent(object sender, LogEventArgs e)
            {
                try
                {
                    if (e == null || e.Source == null ||
                        e.Source.SourceName != UnitySource)
                        return;

                    string text = e.Data as string ??
                                  (e.Data != null ? e.Data.ToString() : null);

                    if (!NamesThisMod(text))
                        return;

                    string key = text;
                    int cut = text.IndexOf('\n');

                    if (cut > 0)
                        key = text.Substring(0, cut);

                    string note = null;

                    lock (_seen)
                    {
                        int n;
                        _seen.TryGetValue(key, out n);
                        _seen[key] = ++n;

                        if (n > PerError || _copied >= Total)
                        {
                            if (n == PerError + 1 && _copied < Total)
                                WriteLine("(the same game error again - no more " +
                                          "copies of it are written this launch)");
                            return;
                        }

                        if (_copied == 0)
                            note = "--- the GAME's own error below names " + Title +
                                   " in its stack trace. BepInEx leaves the game's " +
                                   "errors out of LogOutput.log by default, so " +
                                   "this file is the only place it is written down. ---";

                        _copied++;
                    }

                    if (note != null)
                        WriteLine(note);

                    WriteLine(e.ToString());
                }
                catch { }
            }

            public void Dispose()
            {
            }
        }
    }
}
