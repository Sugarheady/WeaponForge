using System;
using System.Collections.Generic;
using System.Diagnostics;
using BepInEx.Logging;

namespace WeaponForge
{
    // AN INSTRUMENT, NOT A FEATURE.
    //
    // "I'm getting a huge lag spike every time i spawn a larva" - and Kamikaze,
    // spawning through the identical code path, does not stutter at all. That
    // difference is the whole clue, and it points at the per-spawn work rather
    // than at the spawn itself.
    //
    // But it only POINTS. The same log that showed 68 repetitions of the stat
    // block looked exactly like a re-entrancy loop, and it was not one - the
    // RATE CHECK instrument is what proved it was 68 separate spawns. So
    // before rebuilding anything around a theory, measure which stage actually
    // costs the milliseconds.
    //
    // Reports ONCE per weapon, after enough spawns to be meaningful, as a
    // single line ordered worst-first. Every stage is timed, including the
    // ones I would bet against, because the point is to be told something I
    // did not already believe.
    //
    // ALL OF IT IS DEFENSIVE. A diagnostic that throws costs a test round -
    // that rule was written after a warning-only read killed five tests.
    public static class ForgeMinionCost
    {
        private static readonly ManualLogSource Log =
            ForgeLog.Source("WeaponForge.Minion");

        // Enough spawns that one unlucky frame does not dominate, few enough
        // that the answer arrives while he is still looking at it.
        private const int Sample = 12;

        private class Tally
        {
            public int spawns;
            public bool reported;
            public readonly Dictionary<string, double> ms =
                new Dictionary<string, double>();
        }

        private static readonly Dictionary<string, Tally> _byWeapon =
            new Dictionary<string, Tally>();

        private static Tally _current;
        private static readonly Stopwatch _watch = new Stopwatch();

        // True while a sample is being collected, so the caller can skip the
        // timing calls entirely once a weapon has reported.
        public static bool Watching { get; private set; }

        public static void Begin(string weaponName)
        {
            try
            {
                if (string.IsNullOrEmpty(weaponName))
                    weaponName = "(unnamed)";

                Tally t;

                if (!_byWeapon.TryGetValue(weaponName, out t))
                {
                    t = new Tally();
                    _byWeapon[weaponName] = t;
                }

                _current = t.reported ? null : t;
                Watching = _current != null;
            }
            catch (Exception)
            {
                _current = null;
                Watching = false;
            }
        }

        // Times one stage. The action is run whether or not we are measuring,
        // so this can wrap the real call site unconditionally.
        public static void Stage(string name, Action work)
        {
            if (work == null)
                return;

            if (_current == null)
            {
                work();
                return;
            }

            double before = 0d;

            try
            {
                before = Elapsed();
            }
            catch (Exception) { }

            work();

            try
            {
                double spent = Elapsed() - before;

                double had;
                _current.ms.TryGetValue(name, out had);
                _current.ms[name] = had + spent;
            }
            catch (Exception) { }
        }

        private static double Elapsed()
        {
            if (!_watch.IsRunning)
                _watch.Start();

            return _watch.Elapsed.TotalMilliseconds;
        }

        public static void End(string weaponName)
        {
            try
            {
                Tally t = _current;

                _current = null;
                Watching = false;

                if (t == null || t.reported)
                    return;

                t.spawns++;

                if (t.spawns < Sample)
                    return;

                t.reported = true;
                Report(weaponName, t);
            }
            catch (Exception)
            {
                _current = null;
                Watching = false;
            }
        }

        private static void Report(string weaponName, Tally t)
        {
            try
            {
                var rows = new List<KeyValuePair<string, double>>(t.ms);

                rows.Sort(delegate (
                    KeyValuePair<string, double> a,
                    KeyValuePair<string, double> b)
                {
                    return b.Value.CompareTo(a.Value);
                });

                double total = 0d;

                for (int i = 0; i < rows.Count; i++)
                    total += rows[i].Value;

                if (t.spawns <= 0)
                    return;

                var sb = new System.Text.StringBuilder();

                sb.Append("minion SPAWN COST on '").Append(weaponName)
                  .Append("': ").Append((total / t.spawns).ToString("0.00"))
                  .Append(" ms per spawn, averaged over ").Append(t.spawns)
                  .Append(". Worst first - ");

                for (int i = 0; i < rows.Count; i++)
                {
                    if (i > 0)
                        sb.Append(", ");

                    sb.Append(rows[i].Key).Append(' ')
                      .Append((rows[i].Value / t.spawns).ToString("0.00"))
                      .Append("ms");
                }

                sb.Append(". This is OUR per-spawn work only - it does not " +
                          "include the game instantiating the prefab, so if " +
                          "the total here is small the stutter is the prefab " +
                          "itself and not us. The cheapest way to tell: does " +
                          "an ORDINARY Larva appearing in the level stutter " +
                          "the same way?");

                Log.LogInfo(sb.ToString());
            }
            catch (Exception) { }
        }
    }
}
