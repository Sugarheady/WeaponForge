using System.Collections.Generic;
using System.Text;
using BepInEx.Logging;
using UnityEngine;

namespace WeaponForge
{
    // A drone GADGET authored from JSON - the "intended" route, as opposed to
    // dressing up a minion-spawner WEAPON.
    //
    // WHY THE WEAPON ROUTE WAS THE WRONG SHAPE, since it took a round to
    // establish. The five minion-spawner weapon templates are the ENEMY
    // pattern: they fling units out like projectiles and have no cap logic
    // whatsoever - `MinionSpawnerWeapon.FireSingle` never checks one. The cap
    // lives only in `SpawnMinionModule`, which is a gadget. So a "drone" built
    // on a spawner weapon swarmed without limit, which is exactly what happened
    // in R8.
    //
    // AND WHY SPAWNERS ARE GADGET-ONLY, which is subtler than "the game blocks
    // it". `SpawnMinionModule.GetConnectionType` handles PrimaryWeapon and
    // SecondaryWeapon perfectly well, so such a module in a weapon slot
    // installs and caps correctly. But the ship builds its gun from
    // `MainModule as WeaponModule`, and a SpawnMinionModuleData is not one - so
    // no weapon is ever built and the trigger does nothing. It is unreachable,
    // not forbidden.
    //
    // THE HEADLINE FIELD IS `level`, AND IT IS THE MINION CAP, one for one.
    // `Activate` refuses to spawn once the count for its connection type is
    // `>= base.Level`, and `OnUpdate` enforces it downward by DESTROYING the
    // excess. Since `Module.BaseLevel = moduleData.level`, setting `level` on
    // our clone sets the starting cap - and because the stock gadgets all ship
    // `canBeBoosted: 1`, a BoosterCore raises it. That is the mechanic he
    // described from play, and it is worth naming `cap` in JSON because
    // "level" gives no hint of it.
    //
    // THREE UNGUARDED DEREFERENCES in the game's own code make cloning a stock
    // gadget the only safe way to build one of these:
    //
    //   * `OnInstalled` does `Instantiate(this.ParticlePrefab)` with no null
    //     check, and `OnUninstalled` then does
    //     `Destroy(this.particleInstance.gameObject)`. A gadget with no
    //     particlePrefab throws the moment it is installed.
    //   * `Activate` does `this.Minion.GetComponent<SavableEntity>()` and hands
    //     the result straight to CreateEntity - so a minion prefab that is not
    //     a savable entity throws on first use.
    //   * `Activate` ends with `owner.GetTank(base.ResourceUsed).Value -= ...`
    //     with no null check on the tank.
    //
    // Cloning `Module Active Drone Caps` (or any of the five) inherits a valid
    // particle prefab and resource; the minion is validated at build time.
    public static class ForgeDrone
    {
        private static readonly ManualLogSource Log =
            ForgeLog.Source("WeaponForge.Drone");

        // The per-minion options (weapon / faction / lifetime) keyed by the
        // MODULE that spawns them.
        //
        // A second registry rather than reusing ForgeMinion's: that one is keyed
        // by WeaponData, because a spawner weapon is what identifies a config
        // there. On the gadget path there is no weapon at all, so the
        // SpawnMinionModuleData is the identity.
        private static readonly Dictionary<ModuleData, ForgeMinion.Config>
            _byModule = new Dictionary<ModuleData, ForgeMinion.Config>();

        public static void Register(ModuleData module, ForgeMinion.Config cfg)
        {
            if (module != null && cfg != null)
                _byModule[module] = cfg;
        }

        public static bool TryGet(ModuleData module, out ForgeMinion.Config cfg)
        {
            cfg = null;
            return module != null && _byModule.TryGetValue(module, out cfg);
        }

        public static bool Any
        {
            get { return _byModule.Count > 0; }
        }

        // ------------------------------------------------------------------
        // Finding a minion prefab, and refusing a bad one BEFORE it throws
        // ------------------------------------------------------------------

        // Accepts the prefab's asset name. Validated rather than trusted,
        // because `Activate` dereferences the SavableEntity with no null check
        // and the exception lands inside the game's own code with no clue as to
        // which file caused it.
        public static Unit FindMinion(string name, string fileName)
        {
            if (string.IsNullOrEmpty(name))
                return null;

            string want = name.Trim();

            Unit found = null;

            foreach (Object asset in
                     Resources.FindObjectsOfTypeAll(typeof(Unit)))
            {
                var u = asset as Unit;

                if (u == null || u.name == null)
                    continue;

                if (string.Equals(u.name, want,
                        System.StringComparison.OrdinalIgnoreCase))
                {
                    found = u;
                    break;
                }
            }

            if (found == null)
            {
                Log.LogWarning(
                    fileName + ": drone \"minion\": no unit prefab named '" +
                    want + "'. " + AvailableMinions());
                return null;
            }

            if (found.GetComponent<SavableEntity>() == null)
            {
                Log.LogWarning(
                    fileName + ": drone \"minion\": '" + want + "' exists but " +
                    "is NOT a savable entity, and the game hands that straight " +
                    "to CreateEntity without checking - it would throw the " +
                    "first time you pressed the gadget key. Refusing it. " +
                    AvailableMinions());
                return null;
            }

            return found;
        }

        // What can actually be spawned. Printed on a miss rather than made into
        // a static list in the docs, because the answer is whatever the game has
        // loaded and that is the only trustworthy source.
        public static string Available()
        {
            return AvailableMinions();
        }

        private static string AvailableMinions()
        {
            var names = new List<string>();

            foreach (Object asset in
                     Resources.FindObjectsOfTypeAll(typeof(Unit)))
            {
                var u = asset as Unit;

                if (u == null || u.name == null)
                    continue;

                // Only the ones that would not throw.
                if (u.GetComponent<SavableEntity>() == null)
                    continue;

                names.Add(u.name);
            }

            if (names.Count == 0)
                return "No spawnable unit prefabs are loaded yet.";

            names.Sort(System.StringComparer.OrdinalIgnoreCase);

            // Deduplicated: a prefab and its scene instances share a name.
            var seen = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            var sb = new StringBuilder();
            sb.Append("Spawnable units (");
            int n = 0;

            for (int i = 0; i < names.Count; i++)
            {
                if (!seen.Add(names[i]))
                    continue;

                n++;
            }

            sb.Append(n);
            sb.Append("): ");
            seen.Clear();
            bool first = true;

            for (int i = 0; i < names.Count; i++)
            {
                if (!seen.Add(names[i]))
                    continue;

                if (!first)
                    sb.Append(", ");

                sb.Append(names[i]);
                first = false;
            }

            sb.Append('.');
            return sb.ToString();
        }

        // The five stock drone gadgets, for the log and the docs. Named rather
        // than discovered because these are the ones worth cloning.
        public static readonly string[] StockGadgets =
        {
            "Module Active Drone Caps",       // COMBAT DRONE   - Caps, cost 4
            "Module Active Drone Electron",   // ELECTRIC DRONE - cost 5
            "Module Active Drone Purple",     // MISILLE DRONE  - cost 3
            "Module Active Drone Phantom",    // PHANTOM DRONE  - cost 2
            "Module Active Drone Suicide"     // SUICIDE DRONE  - cost 2
        };
    }
}
