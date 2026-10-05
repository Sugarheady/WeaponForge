using UnityEngine;

namespace WeaponForge
{
    // ONE REWARD SET, SHARED BY EVERY FEATURE THAT MAKES A WEAPON TEMPORARILY
    // BETTER.
    //
    // `spinUp` pays you WHILE the meter fills. `surge` pays you AFTER it blows,
    // while it drains. They are opposite in every way that matters and
    // identical in one: both answer "what is this gun at full?" with a set of
    // multipliers published to `ForgeWeaponStats` for as long as the ramp says.
    //
    // ★ WHY THIS CLASS EXISTS RATHER THAN A SECOND COPY. `surge` shipped with
    // seventeen reward pairs on 2026-09-17 - his ask: *"please include the other
    // base game options as well. Like projectile count, projectile speed,
    // projectile range, spread, burst, burnrate/burndamage, damage, etc."* -
    // and the very next question was whether `spinUp` could have them too. The
    // obvious move is to paste them across. That is the shape this repo already
    // has a standing rule about, and the cost is not the typing:
    //
    //   - Every "this reward does nothing on this weapon" warning would exist
    //     twice, and the second copy is the one that goes stale.
    //   - `slow.amount`-style traps (a field that is inverted, or capped
    //     somewhere else) would have to be remembered in two places.
    //   - A nineteenth stat added later lands in one block and not the other,
    //     and nothing fails.
    //
    // So the DATA and the PUBLISHING live here, the PARSING and the WARNINGS
    // live once in `WeaponBuilder` (`ParseRewards` / `DescribeRewards` /
    // `WarnRewards`), and a feature owns only the thing that makes it itself:
    // its ramp, its clock and its feedback.
    //
    // WHAT A FEATURE STILL OWNS. `pellets` needs `InitializeVisuals()` called
    // on every change of the rounded count or a beam throws once a frame - and
    // that call needs per-instance state (the last count seen), so it stays on
    // the feature. `Publish` reports whether the count moved so the caller
    // knows when to do it.
    public class ForgeRewards
    {
        // Fire rate and accuracy default ON - the two `spinUp` was asked for,
        // and his choice for `surge` as well. Everything else waits to be
        // named.
        public bool rate = true;
        public float fireRateAtFull = 2f;

        public bool accuracy = true;
        public float accuracyAtFull = 1f;   // the FRACTION of scatter removed
        public float spreadAtFull = 1f;     // multiplier on the fan, 1 = keep

        public bool damage;
        public float damageAtFull = 1.5f;

        public bool pellets;
        public float pelletsAtFull = 2f;

        public bool cheaper;
        public float costAtFull = 0.5f;

        public bool speed;
        public float speedAtFull = 1.5f;

        public bool range;
        public float rangeAtFull = 1.5f;

        public bool size;
        public float sizeAtFull = 1.5f;

        public bool burst;
        public float burstAtFull = 2f;
        public float burstDelayAtFull = 1f;

        public bool burn;
        public float burnAtFull = 2f;

        public bool explosion;
        public float explosionAtFull = 1.5f;

        public bool push;
        public float pushAtFull = 2f;
        public float knockbackAtFull = 1f;

        public bool warmup;
        public float warmupAtFull;          // 0 removes the minigun gate

        public bool lifetime;
        public float lifetimeAtFull = 1.5f;

        // Does anything here actually move a number? A ramp with nothing in it
        // is a valid file and a dead feature.
        public bool PaysAnything
        {
            get
            {
                return rate || accuracy || damage || pellets || cheaper ||
                       speed || range || size || burst || burn || explosion ||
                       push || warmup || lifetime;
            }
        }

        // Publish every live reward at ramp position `f` (0..1).
        //
        // Returns TRUE when the projectile count is among them, which is the
        // caller's cue to re-run `InitializeVisuals()` if the rounded count has
        // moved. That is not fastidiousness: `HitscanWeapon.OnBarrelMoved`
        // indexes `visualsInstances[num]` with no bounds guard, every frame the
        // barrel moves, so a beam whose count rose behind its back throws sixty
        // times a second - and leaving orphaned visuals ABOVE the count on the
        // way back down is a beam frozen in mid-air with `Firing` stuck true.
        public bool Publish(WeaponBase weapon, string key, float f)
        {
            if (weapon == null || string.IsNullOrEmpty(key))
                return false;

            if (rate)
                One(weapon, key, ForgeWeaponStats.FireRate, fireRateAtFull, f);

            if (accuracy)
            {
                // `accuracyAtFull` is the FRACTION OF THE SCATTER REMOVED, so 1
                // is pinpoint and 0.5 halves it. Expressed that way round
                // because "how much better does it get" is the question an
                // author is asking; the multiplier is the inverse.
                One(weapon, key, ForgeWeaponStats.AngleVariance,
                    Mathf.Max(0f, 1f - accuracyAtFull), f);

                // Spread is the DELIBERATE fan, left alone unless asked -
                // `spreadAtFull` defaults to 1 and `One` withdraws rather than
                // pinning a base at a multiplier of 1. Tightening a shotgun's
                // spread to nothing turns it into a slug gun, which is a fine
                // weapon and a terrible surprise.
                One(weapon, key, ForgeWeaponStats.Spread, spreadAtFull, f);
            }

            if (damage)
                One(weapon, key, ForgeWeaponStats.Damage, damageAtFull, f);

            if (pellets)
                One(weapon, key, ForgeWeaponStats.Pellets, pelletsAtFull, f);

            if (cheaper)
                One(weapon, key, ForgeWeaponStats.Cost, costAtFull, f);

            if (speed)
                One(weapon, key, ForgeWeaponStats.Speed, speedAtFull, f);

            if (range)
                One(weapon, key, ForgeWeaponStats.Range, rangeAtFull, f);

            if (size)
                One(weapon, key, ForgeWeaponStats.Size, sizeAtFull, f);

            if (burst)
            {
                One(weapon, key, ForgeWeaponStats.BurstSize, burstAtFull, f);
                One(weapon, key, ForgeWeaponStats.BurstDelay,
                    burstDelayAtFull, f);
            }

            if (burn)
                One(weapon, key, ForgeWeaponStats.Burn, burnAtFull, f);

            if (explosion)
            {
                One(weapon, key, ForgeWeaponStats.ExplosionRadius,
                    explosionAtFull, f);
                One(weapon, key, ForgeWeaponStats.ExplosionDamage,
                    explosionAtFull, f);
            }

            if (push)
            {
                One(weapon, key, ForgeWeaponStats.Push, pushAtFull, f);
                One(weapon, key, ForgeWeaponStats.Knockback,
                    knockbackAtFull, f);
            }

            if (warmup)
                One(weapon, key, ForgeWeaponStats.Warmup, warmupAtFull, f);

            if (lifetime)
                One(weapon, key, ForgeWeaponStats.Lifetime, lifetimeAtFull, f);

            return pellets;
        }

        // A MULTIPLIER OF 1 WITHDRAWS RATHER THAN BEING PUBLISHED. It matters
        // for two configurations that would otherwise pin a captured base for
        // nothing - a reward switched on and set to its no-op value, and the
        // default `spreadAtFull: 1`. A pinned base is invisible in play and
        // stops a legitimate change to the weapon (a module augmentation
        // between engagements) from being picked up.
        private static void One(
            WeaponBase weapon, string key, int stat, float atFull, float f)
        {
            float m = Mathf.Lerp(1f, atFull, f);

            if (Mathf.Abs(m - 1f) > 0.0001f)
                ForgeWeaponStats.Set(weapon, stat, key, m);
            else
                ForgeWeaponStats.Clear(weapon, stat, key);
        }
    }
}
