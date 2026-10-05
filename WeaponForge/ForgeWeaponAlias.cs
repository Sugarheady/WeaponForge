using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;

namespace WeaponForge
{
    // MAKE A CLONED WeaponData INHERIT EVERYTHING THE ORIGINAL WAS REGISTERED
    // FOR.
    //
    // `wingman` builds its shots by cloning whatever weapon you are carrying
    // and re-running the feature chain over the clone (see ForgeWingmanWeapon).
    // A clone inherits two of the three ways a feature attaches, for free:
    //
    //   WeaponData FIELDS         - `Instantiate` copies them.
    //   PROJECTILE PREFAB parts   - cloning the prefab copies its components.
    //   REGISTRIES keyed by the   - NOTHING. The clone is a different object,
    //   WeaponData                  so every lookup misses.
    //
    // That third row is **twenty-odd registries**, and they are the interesting
    // half of the mod: crit, leech, overheat, spinUp, onKill, slow, charge,
    // adaptive, execute, targeting, gas, shield, stun, charm, spiral, minion,
    // curve beam, contact, hit sound, electric, lunge, orbit, catalyst. Without
    // this file a wingman copying a Forge weapon would fire the same ammo at
    // the same damage while silently dropping half of what makes that weapon
    // itself - and KEEPING the other half, because pierce and sticky ride the
    // prefab. **An inconsistent copy is worse than an honest one**, which is
    // why this is not optional.
    //
    // DERIVED, NOT LISTED. The sweep finds the registries by their SHAPE - a
    // static field typed `Dictionary<WeaponData, T>` in this assembly - rather
    // than from a hand-written list of twenty names. Same reasoning as
    // `ForgeDiagnosticGates`: a list nobody rereads is what drifts, and the
    // twenty-first registry would simply be forgotten. `wingmanaliastest.py`
    // asserts every registry in the assembly is either swept or named in the
    // opt-out below, so one cannot land in neither.
    //
    // THE CAPABILITY IS TESTED, NOT INFERRED. `IsRegistry` asks whether the
    // field's type closes `Dictionary<WeaponData, ...>` and whether the value
    // is usable as a non-generic `IDictionary` - which is exactly what `Inherit`
    // then uses. That pairing is deliberate: Weapon Forge's diagnostic sweep
    // once accepted fields on one test (`is ICollection`) and acted on another
    // (`Clear()`), and `HashSet<T>` sits in the gap between them. Acceptance and
    // action must be the same question.
    public static class ForgeWeaponAlias
    {
        private static readonly ManualLogSource Log =
            ForgeLog.Source("WeaponForge.Wingman");

        // Registries whose Config carries per-weapon RUNTIME state. These are
        // SHALLOW-COPIED onto the clone instead of shared, and both entries are
        // here for a measured reason rather than caution:
        //
        //   ForgeLeech    - Config holds `windowStartedAt` / `healedThisSecond`,
        //                   the per-second healing budget. Shared, a wingman's
        //                   leech would eat your primary's allowance and the
        //                   pair would heal you for one weapon's worth.
        //   ForgeCatalyst - `ForgeCatalystNode.Cull` counts live nodes with
        //                   `_live[i].cfg == cfg`, REFERENCE identity, to
        //                   enforce `max`. Shared, wingman nodes would count
        //                   against your primary's node limit and cull it.
        //
        // Anything not named here is build-time configuration and is shared,
        // which is correct and also cheaper.
        private static readonly HashSet<string> CopyNotShare =
            new HashSet<string>(StringComparer.Ordinal)
            {
                "ForgeLeech",
                "ForgeCatalyst",
            };

        // Registries that must NOT reach the clone at all.
        //
        // `ForgeWingman`'s own registry is `Dictionary<WeaponData, Config>`, so
        // the shape-based sweep finds it like any other - and inheriting it
        // would make the clone a wingman weapon in its own right. The wingmen
        // would then build wingmen, each of those building more, on every
        // weapon swap. **This is the recursion the design says to prevent, and
        // it arrives through the door built to be generic rather than through
        // the one anybody would think to guard.**
        //
        // Worth stating as a rule, because the next registry of this kind will
        // have the same property: a feature that makes a weapon SPAWN a copy of
        // a weapon must not be inherited by the copy.
        //
        // ★ `ForgeRank` IS DELIBERATELY *NOT* HERE, AND IT WAS BRIEFLY ADDED BY
        // MISTAKE ON 2026-09-19. The reasoning for excluding it was that rank is
        // per-owner PROGRESS rather than a per-shot behaviour, so a wingman
        // would bank XP against itself. **That is not what happens.**
        // `ForgeWingmanController` is a component on the SHIP, `_unit` is the
        // ship, and every wingman weapon is built with `made.Equip(_unit)` - so
        // `WeaponBase.Owner` on a wingman's shot is the PLAYER, and the XP has
        // always gone into the player's own `(shipId, file)` record. One
        // progress record, the player's, exactly as intended.
        //
        // Inheriting rank is therefore load-bearing: it is what makes wingman
        // damage count toward your gun at all, and what lets the copies carry
        // the stat multipliers and unlocked blocks your gun has earned.
        // `rank.fromWingmen` is the switch for people who want it off.
        //
        // > **Check who calls `Equip` before reasoning about `Owner`.** The
        // > property is set by whoever equips the weapon, not by whatever object
        // > appears to be shooting it.
        private static readonly HashSet<string> NeverInherit =
            new HashSet<string>(StringComparer.Ordinal)
            {
                "ForgeWingman",
            };

        private static FieldInfo[] _registries;
        private static bool _saidNoRegistries;

        // ------------------------------------------------------------------
        // Discovery
        // ------------------------------------------------------------------

        private static bool IsRegistry(FieldInfo f)
        {
            Type t = f.FieldType;

            if (!t.IsGenericType ||
                t.GetGenericTypeDefinition() != typeof(Dictionary<,>))
            {
                return false;
            }

            Type[] args = t.GetGenericArguments();

            if (args.Length != 2 || args[0] != typeof(WeaponData))
                return false;

            // The capability this file actually uses. Dictionary<K,V> does
            // implement the non-generic IDictionary - unlike HashSet<T> and
            // ICollection - but asking rather than assuming is the point.
            return typeof(IDictionary).IsAssignableFrom(t);
        }

        private static void Ensure()
        {
            if (_registries != null)
                return;

            var found = new List<FieldInfo>();

            Type[] types;

            try
            {
                types = typeof(ForgeWeaponAlias).Assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException e)
            {
                // Partial answer beats none, exactly as the diagnostic sweep
                // does it.
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
                    if (IsRegistry(fields[j]))
                        found.Add(fields[j]);
            }

            _registries = found.ToArray();

            if (_registries.Length == 0 && !_saidNoRegistries)
            {
                _saidNoRegistries = true;

                Log.LogWarning(
                    "no weapon registries were found to inherit from, which " +
                    "should be impossible - a wingman will still fire, but " +
                    "copying a Forge weapon will drop that weapon's own crit, " +
                    "leech, overheat and so on.");
            }
        }

        // How many registries exist at all. For the build log and for the test.
        public static int RegistryCount
        {
            get
            {
                Ensure();
                return _registries.Length;
            }
        }

        // The declaring type names, so the test can check the opt-out lists
        // against reality rather than against a copy of themselves.
        public static IEnumerable<string> RegistryOwners
        {
            get
            {
                Ensure();

                var names = new List<string>();

                for (int i = 0; i < _registries.Length; i++)
                    names.Add(_registries[i].DeclaringType.Name);

                return names;
            }
        }

        public static bool IsCopied(string owner)
        {
            return CopyNotShare.Contains(owner);
        }

        public static bool IsExcluded(string owner)
        {
            return NeverInherit.Contains(owner);
        }

        // ------------------------------------------------------------------
        // The sweep
        // ------------------------------------------------------------------

        // Copy every registration `source` has onto `clone`. Returns the names
        // of the features that carried, so the caller can print them - a
        // wingman that silently inherits nothing is indistinguishable from one
        // whose weapon had nothing to inherit, and that is exactly the
        // ambiguity the clone-time diagnostic exists to remove.
        public static List<string> Inherit(WeaponData source, WeaponData clone)
        {
            var carried = new List<string>();

            if (source == null || clone == null || ReferenceEquals(source, clone))
                return carried;

            Ensure();

            for (int i = 0; i < _registries.Length; i++)
            {
                FieldInfo f = _registries[i];
                string owner = f.DeclaringType.Name;

                if (NeverInherit.Contains(owner))
                    continue;

                try
                {
                    var dict = f.GetValue(null) as IDictionary;

                    if (dict == null || !dict.Contains(source))
                        continue;

                    object value = dict[source];

                    if (CopyNotShare.Contains(owner))
                    {
                        object copy = ShallowCopy(value);

                        // A config that must not be shared and cannot be
                        // copied is SKIPPED, not shared. Losing one feature on
                        // the copies is a visible disappointment; sharing a
                        // healing budget or a node cap is a bug that reads as
                        // the primary weapon breaking.
                        if (copy == null)
                        {
                            Log.LogWarning(
                                "wingman: could not copy " + owner + "'s " +
                                "config, so the wingman shots will not carry " +
                                "it. Your own weapon is unaffected.");

                            continue;
                        }

                        value = copy;
                    }

                    dict[clone] = value;
                    carried.Add(owner);
                }
                catch (Exception e)
                {
                    Log.LogWarning(
                        "wingman: inheriting " + owner + " failed (" +
                        e.Message + "). That feature will be missing from the " +
                        "wingman shots; everything else still applies.");
                }
            }

            return carried;
        }

        // Drop a clone from every registry. Called when a wingman weapon is
        // rebuilt or torn down: these are static tables that are deliberately
        // NOT cleared at run entry (see ForgeCrit.Reset, which documents why
        // clearing them would unbuild every weapon for the session), so without
        // this a clone per weapon swap would accumulate for the whole session.
        public static void Forget(WeaponData clone)
        {
            if (clone == null)
                return;

            Ensure();

            for (int i = 0; i < _registries.Length; i++)
            {
                try
                {
                    var dict = _registries[i].GetValue(null) as IDictionary;

                    if (dict != null && dict.Contains(clone))
                        dict.Remove(clone);
                }
                catch
                {
                    // One stuck registry must not strand the rest.
                }
            }
        }

        // ------------------------------------------------------------------

        private static object ShallowCopy(object src)
        {
            if (src == null)
                return null;

            try
            {
                Type t = src.GetType();
                object dst = Activator.CreateInstance(t);

                FieldInfo[] fields = t.GetFields(
                    BindingFlags.Instance |
                    BindingFlags.Public |
                    BindingFlags.NonPublic);

                for (int i = 0; i < fields.Length; i++)
                    fields[i].SetValue(dst, fields[i].GetValue(src));

                return dst;
            }
            catch
            {
                return null;
            }
        }
    }
}
