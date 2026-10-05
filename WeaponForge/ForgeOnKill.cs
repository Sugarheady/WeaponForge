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
            // BURN as in "your ship is on fire", NOT as in overheat. That is
            // `coolHeat` below, and the two were one word apart in the build
            // log until R15: `clearBurn` printed as "cools 3", which is exactly
            // the word `overheat` uses for its own meter, on a weapon that can
            // carry both blocks. He read that line and reasonably concluded
            // there was a heat option, then went looking for it and found none.
            public float clearBurn;

            // ---- heat relief ---------------------------------------------
            // Heat removed from THIS weapon's `overheat` meter per kill, in the
            // same units as `max` / `perShot`. His R15 ask, and it needed no
            // new mechanism: a kill that drops the meter below `ventTo` also
            // ends a vent lock on the next frame, because that is already how
            // the lock decides it is done. So a gun that would jam can be kept
            // running by hitting things with it, which is the design.
            public float coolHeat;

            // WHOSE kill cools this meter. False (the default) is "only a kill
            // scored by this weapon", which is what shipped and what the
            // comment above describes.
            //
            // R16 test 198 found the hole in that: *"Killing things with a
            // different weapon while it was locked didn't let me fire again. I
            // guess that would be a bug huh?"* It is the one state where the
            // design above cannot reach itself. `response: "vent"` locks the
            // gun until the meter is fully cool - so while it is locked you
            // cannot fire it, cannot kill with it, and therefore cannot cool
            // it. The feature exists to keep a jamming gun running, and the
            // moment it actually jams it switches itself off. Only a shot
            // already in flight can break the deadlock.
            //
            // Left OFF by default because turning it on changes a shipped
            // weapon's behaviour silently, which is the shape this project
            // keeps paying for. The builder warns when a `vent` weapon has
            // `coolHeat` and has NOT set this, so the trap is stated rather
            // than found.
            public bool coolFromAnyWeapon;

            // ---- gas on the corpse ----------------------------------------
            // Leave a cloud where the thing died, using THIS weapon's own
            // `gas` block - so it is one switch rather than a second copy of
            // twenty knobs, and the cloud a kill leaves is the cloud he already
            // tuned and can already see.
            //
            // It needs the `gas` block to exist. When it does not, the builder
            // says so and turns this back off, because a flag pointing at a
            // config that was never registered is a payout that silently does
            // nothing - and this block already has `chance` and `maxPerSecond`
            // to make "nothing happened" look like an ordinary unlucky roll.
            public bool gas;

            // May a cloud's OWN kill leave another cloud?
            //
            // ON. It shipped off for one night on my reasoning that a cloud
            // with `damagePerSecond` credits its kills to the weapon that made
            // it (ForgeGasCloud -> ForgeKillsPatch.Credit), so gas kills spawn
            // gas, which kills, which spawns - a chain reaction.
            //
            // THE CHAIN IS REAL AND "RUNAWAY" WAS WRONG. Every enemy dies
            // exactly once, and one death pays out once, so the clouds a fight
            // can produce are capped by the number of things in it - it
            // propagates through a crowd and then stops. There is no term that
            // feeds itself. `gas.max` recycles the oldest by FADING it (not
            // refusing, not popping), and `maxPerSecond` is still there for
            // anyone who wants a harder limit.
            //
            // His call, and he is right: *"honestly gaschains, should be on by
            // default, cause thats alot of fun."* Safe to flip because the key
            // was hours old and no file had used it yet.
            public bool gasChains = true;

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
                           coolHeat > 0f ||
                           gas ||
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
