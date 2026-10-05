using System;
using System.Reflection;
using BepInEx.Logging;

namespace WeaponForge
{
    // THE ONE OWNER of "have I already said this?".
    //
    // Warnings about a misconfigured weapon are gated so they are said once
    // instead of once per shot - `_said`, `_warned`, `_saidNoLevel`, and a
    // `HashSet<string>` keyed by weapon label where the answer differs per
    // weapon. There are 26 of them across 19 files: **20 one-shot bools and 6
    // keyed sets**, and the split matters - see the note on ClearMethod for how
    // the first version of this class swept the 20 and silently dropped all 6.
    //
    // THE PROBLEM THEY ALL SHARE: the gate is per SESSION, while the thing it
    // describes is per RUN. Die, start again, and every one of those warnings
    // is already spent - so the run where you go looking for the log line is
    // exactly the run that cannot produce it. Three features had noticed and
    // written their own `ResetDiagnostics`; the other sixteen had not, and
    // `ForgeMinionSolid` had gone one better by writing the reset, documenting
    // why it mattered ("the once-per-weapon log gate would otherwise make a
    // second run in the same session silent") and never wiring it up.
    //
    // WHY THIS IS REFLECTIVE RATHER THAN TWENTY MORE Reset() METHODS. The
    // hand-written version is what already drifted: a per-file reset only
    // helps if someone remembers to write it AND remembers to add the call,
    // and the reset list is the kind of list nobody rereads. Sweeping by
    // naming convention means a gate added next month is covered the moment it
    // is named, with nothing to wire and nothing to forget. The convention is
    // real and already universal here - every one of the 26 begins `_said`,
    // `_warned` or `_told`.
    //
    // IT DELIBERATELY OVER-RESETS. A few gates gaurd a reflection failure -
    // a private field missing from this build of the game - which is genuinely
    // session-invariant, so re-saying it each run is redundant. That costs one
    // repeated line per run. Under-resetting costs a diagnostic at the moment
    // it is wanted, on a project where several runs per session is the normal
    // way to test. The trade is not close, and a classifier that had to be
    // maintained per gate would just be the original drift wearing a hat.
    public static class ForgeDiagnosticGates
    {
        private static readonly ManualLogSource Log =
            ForgeLog.Source("WeaponForge");

        private static FieldInfo[] _gates;

        // Diagnostics resetting must never be able to cost a run, so every
        // layer here is guarded separately: a bad type cannot stop the sweep,
        // and a failed sweep cannot stop the reset list it is called from.
        // See ForgeMinionStats - a warning-only read once killed five tests.
        public static void Reset()
        {
            try
            {
                Ensure();

                if (_gates == null)
                    return;

                for (int i = 0; i < _gates.Length; i++)
                    Clear(_gates[i]);
            }
            catch (Exception e)
            {
                Log.LogWarning(
                    "Could not reset the once-only log gates (" + e.Message +
                    "). Harmless in itself, but warnings already said this " +
                    "session will stay silent for the rest of it.");
            }
        }

        private static void Clear(FieldInfo f)
        {
            try
            {
                if (f.FieldType == typeof(bool))
                {
                    f.SetValue(null, false);
                    return;
                }

                // A keyed gate - one entry per weapon that has been warned
                // about. Readonly, so the collection is emptied rather than
                // replaced.
                //
                // Deliberately NOT cast to ICollection - see ClearMethod.
                object set = f.GetValue(null);

                if (set == null)
                    return;

                MethodInfo clear = ClearMethod(f.FieldType);

                if (clear != null)
                    clear.Invoke(set, null);
            }
            catch
            {
                // One unreachable gate is not worth a line of its own; the
                // others still reset.
            }
        }

        private static void Ensure()
        {
            if (_gates != null)
                return;

            var found = new System.Collections.Generic.List<FieldInfo>();

            Type[] types;

            try
            {
                types = typeof(ForgeDiagnosticGates).Assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException e)
            {
                // Partial answer beats none: whatever loaded still gets swept.
                types = e.Types ?? new Type[0];
            }

            for (int i = 0; i < types.Length; i++)
            {
                Type t = types[i];

                if (t == null)
                    continue;

                FieldInfo[] fields;

                try
                {
                    fields = t.GetFields(
                        BindingFlags.Static |
                        BindingFlags.NonPublic |
                        BindingFlags.Public |
                        BindingFlags.DeclaredOnly);
                }
                catch
                {
                    continue;
                }

                for (int j = 0; j < fields.Length; j++)
                {
                    if (IsGate(fields[j]))
                        found.Add(fields[j]);
                }
            }

            _gates = found.ToArray();

            // BOTH KINDS ARE COUNTED SEPARATELY, and that is the whole point of
            // this line rather than a nicety.
            //
            // The first version printed one total, said "20", and I read that
            // as a pass while my own grep of the source said 26. The six it was
            // silently dropping were every one of the KEYED gates - so the log
            // line reported success at exactly the thing that had failed.
            // A count is only evidence when it can be checked against a count
            // derived some other way, so print the split the source can be
            // grepped for.
            int bools = 0;

            for (int i = 0; i < _gates.Length; i++)
                if (_gates[i].FieldType == typeof(bool))
                    bools++;

            Log.LogInfo(
                "Once-only log gates found: " + _gates.Length +
                " (" + bools + " one-shot, " + (_gates.Length - bools) +
                " keyed per weapon) - reset at each run entry, so a second run " +
                "in one session still reports its own warnings.");
        }

        private static bool IsGate(FieldInfo f)
        {
            // A literal cannot be written and has no per-run meaning anyway.
            if (f.IsLiteral)
                return false;

            string n = f.Name;

            if (!(n.StartsWith("_said") ||
                  n.StartsWith("_warned") ||
                  n.StartsWith("_told")))
            {
                return false;
            }

            // A readonly bool would throw on assignment; a readonly SET is
            // fine because it is emptied in place.
            if (f.FieldType == typeof(bool))
                return !f.IsInitOnly;

            return ClearMethod(f.FieldType) != null;
        }

        // `HashSet<T>` DOES NOT IMPLEMENT THE NON-GENERIC `ICollection`, and
        // every keyed gate in this mod is a `HashSet<string>`.
        //
        // That one framework detail is the whole bug. `List<T>` and
        // `Dictionary<K,V>` both implement `System.Collections.ICollection`, so
        // `ICollection.IsAssignableFrom` reads as a safe, conservative test for
        // "is this a collection" and passes every container you are likely to
        // try it against - except the one actually used here. So `IsGate`
        // accepted the 20 bools, refused all 6 keyed sets, and the swept count
        // in the log looked plausible.
        //
        // The honest test is the capability being used rather than an interface
        // that implies it: a public parameterless instance `Clear()`. It is
        // also the same method the reset then calls, so acceptance and action
        // cannot disagree - which is what went wrong the first time, where the
        // filter asked one question (`is ICollection`) and the work did another
        // (`GetMethod("Clear")`).
        private static MethodInfo ClearMethod(Type t)
        {
            if (t == null || t.IsValueType)
                return null;

            try
            {
                MethodInfo m = t.GetMethod(
                    "Clear",
                    BindingFlags.Instance | BindingFlags.Public,
                    null,
                    Type.EmptyTypes,
                    null);

                return (m != null && m.ReturnType == typeof(void)) ? m : null;
            }
            catch
            {
                return null;
            }
        }
    }
}
