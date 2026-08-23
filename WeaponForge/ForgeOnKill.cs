using System.Collections.Generic;
using UnityEngine;

namespace WeaponForge
{
    // A weapon that pays you for kills.
    //
    // Registered per WeaponData rather than hung on the projectile, because a
    // kill can arrive by three different routes (a bullet, a beam, an explosion)
    // and only one of them has a projectile to carry a component.
    public static class ForgeOnKill
    {
        public class Config
        {
            // ---- resource ------------------------------------------------
            public string resource = "";
            public float amount;

            // Drop a pickup on the corpse instead of granting it straight away.
            public bool asPickup;

            // ---- limits --------------------------------------------------
            public float chance = 1f;      // 0-1

            // Cap on how much RESOURCE this may grant per second, 0 = uncapped.
            //
            // This used to count PAYOUTS per second, which is what the docs said
            // and is not how anyone reads it next to "amount". With
            // `amount: 100, maxPerSecond: 300` the intent is obviously "no more
            // than 300 a second" - three kills' worth - and the old meaning made
            // that 300 KILLS a second, i.e. no cap at all, so it looked broken.
            // Amount is also the better primitive for the case this exists for
            // (one explosion killing a dozen enemies at once): what hurts there
            // is the total granted, not the number of events.
            //
            // A config that grants no resource at all still falls back to
            // counting payouts, so a buff-only or burn-only one keeps a limiter.
            public float maxPerSecond;

            // ---- burn relief ---------------------------------------------
            public float clearBurn;

            // ---- timed buff ----------------------------------------------
            public float buffDuration;
            public float buffFireRate;     // fraction, 0.25 = +25%
            public float buffDamage;
            public int buffMaxStacks = 1;

            // ---- feedback -------------------------------------------------
            public string sfx = "";

            // Per-weapon override of the global buff-indicator style: "off",
            // "feed", "icons", "both", or empty to follow the BepInEx config.
            // A weapon can therefore opt out of a global default, or force an
            // indicator on when the default is off.
            public string buffIndicator = "";

            // The icon and colour the indicator wears - the weapon's own module
            // art, resolved at build time.
            [System.NonSerialized] public Sprite indicatorIcon;
            [System.NonSerialized] public Color indicatorColor = Color.white;

            // ---- resolved at build time ----------------------------------
            [System.NonSerialized] public Resource resourceAsset;
            [System.NonSerialized] public string sfxGuid;

            // ---- runtime --------------------------------------------------
            [System.NonSerialized] public float windowStartedAt;
            [System.NonSerialized] public float paidThisSecond;

            public bool DoesAnything
            {
                get
                {
                    return (amount > 0f && resourceAsset != null) ||
                           clearBurn > 0f ||
                           (buffDuration > 0f &&
                            (buffFireRate > 0f || buffDamage > 0f));
                }
            }

            // Chance first, then the cap, so a failed roll does not spend cap.
            public bool Allowed()
            {
                if (chance < 1f && Random.value > Mathf.Clamp01(chance))
                    return false;

                if (maxPerSecond <= 0f)
                    return true;

                if (Time.time - windowStartedAt >= 1f)
                {
                    windowStartedAt = Time.time;
                    paidThisSecond = 0f;
                }

                // What one payout costs against the budget: the resource granted,
                // or 1 for a config that grants none (so buff-only and burn-only
                // weapons are still limited to maxPerSecond kills a second).
                float cost = (amount > 0f && resourceAsset != null)
                    ? amount
                    : 1f;

                if (paidThisSecond + cost > maxPerSecond)
                    return false;

                paidThisSecond += cost;
                return true;
            }
        }

        private static readonly Dictionary<WeaponData, Config> _weapons =
            new Dictionary<WeaponData, Config>();

        public static void Register(WeaponData weapon, Config config)
        {
            if (weapon != null && config != null)
                _weapons[weapon] = config;
        }

        public static bool TryGet(WeaponData weapon, out Config config)
        {
            config = null;
            return weapon != null && _weapons.TryGetValue(weapon, out config);
        }

        public static bool Any { get { return _weapons.Count > 0; } }
    }
}
