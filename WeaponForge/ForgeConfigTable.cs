using System.Collections.Generic;

namespace WeaponForge
{
    // A serializable handle for a config object that lives on a PREFAB.
    //
    // WHY THIS EXISTS, because it cost ten failed tests in one round:
    //
    // Unity's Instantiate copies SERIALIZED state only. A plain C# class - like
    // every `Forge*.Config` in this mod - is not serializable (no
    // [Serializable], and it would be the wrong thing anyway, see below). So a
    // component attached to a projectile prefab holding
    //
    //     public ForgeGas.Config cfg;      // <-- silently NOT serialized
    //
    // arrives with **cfg == null on every shot the game fires**. The build log
    // is perfectly healthy - the config parsed, the component attached, the
    // summary line printed - and then every guard that reads `cfg` early-returns
    // and the feature does nothing at all. That is exactly how `gas` and
    // `catalyst` both shipped: the gas weapons fired a recoloured projectile and
    // left no cloud, and a catalyst node never armed, never wore its coin sprite
    // and never caught a bullet.
    //
    // The tell is that the two features which failed are the two that kept a
    // Config REFERENCE on a prefab-attached component, while every other feature
    // (growth, chain, ricochet, homing, converge, tether, sticky, bounce...)
    // stores plain floats and bools that Unity does serialize, and all of those
    // have worked for rounds.
    //
    // WHY NOT JUST MARK Config [Serializable]: Unity would then serialize a
    // COPY into every clone. Sprites and other [NonSerialized] runtime state
    // would be lost, and - worse - each shot would own a different Config
    // instance, which quietly breaks anything comparing them by reference.
    // ForgeCatalystNode.Cull groups nodes by `cfg` identity to enforce `max`,
    // so per-clone copies would make every node think it was the only one.
    //
    // An int, on the other hand, Unity serializes happily. So the prefab carries
    // an ID and the clone looks the real config back up - one shared instance,
    // full runtime state, reference identity intact.
    public static class ForgeConfigTable
    {
        private static readonly List<object> _entries = new List<object>();

        // Deliberately deduplicated by reference: one config object always gets
        // the same id back, so every shot fired from one weapon template
        // resolves to the SAME instance rather than an equal-looking one.
        public static int Id(object config)
        {
            if (config == null)
                return -1;

            for (int i = 0; i < _entries.Count; i++)
                if (ReferenceEquals(_entries[i], config))
                    return i;

            _entries.Add(config);
            return _entries.Count - 1;
        }

        public static T Get<T>(int id) where T : class
        {
            if (id < 0 || id >= _entries.Count)
                return null;

            return _entries[id] as T;
        }
    }
}
