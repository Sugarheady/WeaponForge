using System.Collections.Generic;

namespace WeaponForge
{
    // The weapon-level half of a sticky weapon, keyed by its WeaponData.
    //
    // ForgeSticky rides on the PREFAB and answers questions about one bomb.
    // The trigger pull is a question about the WEAPON - "has this gun laid its
    // full load yet" - and at that moment there is no projectile to ask, only
    // a Shooter and the weapon it is holding. Hence a second, tiny registry,
    // the same shape as ForgeOrbit and ForgeCharge.
    public static class ForgeStickySpec
    {
        public class Spec
        {
            public ForgeSticky.Trigger trigger = ForgeSticky.Trigger.Refire;
            public int max = 8;
        }

        private static readonly Dictionary<WeaponData, Spec> _weapons =
            new Dictionary<WeaponData, Spec>();

        public static void Register(WeaponData weapon, Spec spec)
        {
            if (weapon != null && spec != null)
                _weapons[weapon] = spec;
        }

        public static bool TryGet(WeaponData weapon, out Spec spec)
        {
            spec = null;
            return weapon != null && _weapons.TryGetValue(weapon, out spec);
        }
    }
}
