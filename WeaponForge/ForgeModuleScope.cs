using System.Runtime.CompilerServices;
using BepInEx.Logging;

namespace WeaponForge
{
    // WHOSE GUNS DOES A MODULE FORGE SHIP-GRID MODULE REACH? The unit that
    // carries it - and nobody else.
    //
    // ForgeCrit and ForgeLeech each keep a `_global` bucket for contributions
    // with no weapon to key on: a Module Forge module on the SHIP grid never
    // gets `IWeaponModifier.Modify`, so "all your guns" arrives as a null
    // weapon. Until R19 that bucket was summed into EVERY weapon's numbers -
    // enemy guns included - so a ship-grid crit module made enemy shots crit
    // you, and a ship-grid leech module let enemies heal off you. Found from
    // the Module Forge side (R19 tests 243 / 246, where grow and windup reached
    // enemy bullets and guns by the identical route).
    //
    // Module Forge knows the owner exactly - `OnInstalled(Unit.Data)` is handed
    // the unit whose grid holds the module - and tells this table by type name
    // (`SetOwner`), before pushing a global bonus. A firing weapon's `Owner` is
    // that unit's MonoBehaviour and its `ComponentData` is the same object, so
    // the test is one reference comparison.
    //
    // ★ This is a deliberate MIRROR of Module Forge's `ModuleForgeScope`, not a
    // shared type - each mod must build and run with the other absent, and an
    // owner table is not a decoder worth a dependency. The owner has to be
    // recorded HERE because this mod is the one summing the bucket.
    //
    // ★ HELD WEAKLY (`ConditionalWeakTable`), so there is no run-entry reset
    // that could wipe the owner of a module that is still installed, and no
    // dead module carried from one run into the next.
    //
    // ★ AN UNKNOWN OWNER MATCHES NOBODY, and is said once. The only way to get
    // one is a Module Forge older than this mod, and a module that does
    // nothing is a bug report where a module firing on enemy guns is a death.
    public static class ForgeModuleScope
    {
        private static readonly ManualLogSource Log =
            ForgeLog.Source("WeaponForge.ModuleScope");

        private sealed class Box
        {
            public Unit.Data owner;
        }

        private static readonly ConditionalWeakTable<object, Box> _owners =
            new ConditionalWeakTable<object, Box>();

        // Swept by ForgeDiagnosticGates at run entry, so a second run can
        // report it too.
        private static bool _saidNoOwner;

        // The public door Module Forge calls by type name. Signature is part of
        // the cross-mod contract: (object key, Unit.Data owner).
        public static void SetOwner(object key, Unit.Data owner)
        {
            if (key == null || owner == null)
                return;

            _owners.GetOrCreateValue(key).owner = owner;
        }

        // Does a SHIP-GRID (null-weapon) contribution registered under `key`
        // apply to shots from `weapon`?
        public static bool Applies(object key, WeaponBase weapon)
        {
            if (key == null || weapon == null)
                return false;

            Box box;

            if (!_owners.TryGetValue(key, out box) || box.owner == null)
            {
                if (!_saidNoOwner)
                {
                    _saidNoOwner = true;
                    Log.LogWarning(
                        "a ship-grid module bonus (crit or leech from Module " +
                        "Forge) arrived with no owner, so it applies to NO " +
                        "weapon rather than to every weapon in the game - " +
                        "enemies included, which is what it used to do. This " +
                        "only happens when Module Forge is older than Weapon " +
                        "Forge: update both to matching versions.");
                }

                return false;
            }

            Unit shooter = weapon.Owner;

            return shooter != null && ReferenceEquals(shooter.ComponentData, box.owner);
        }
    }
}
