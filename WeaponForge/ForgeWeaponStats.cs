using System;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;

namespace WeaponForge
{
    // ONE OWNER FOR A LIVE WEAPON'S TUNING, and the third time this project has
    // had to build one.
    //
    // WHY THIS EXISTS. `WeaponBase.FireRate` is a plain public setter, and by
    // the time `spinUp` was asked for, THREE features were each writing it from
    // their own private capture:
    //
    //     ForgeOverheatPatch.Degrade   weapon.FireRate = _rateWas * f
    //     ForgeKillBuff.Push           weapon.FireRate = baseFireRate * (1+f)
    //     ForgeSlow.Push               weapon.FireRate = _fireRate * f
    //
    // That is the exact shape `ForgeUnitTint` and `ForgeDamageStack` exist to
    // prevent, one field along, and it was already broken in play:
    //
    //   - **Overheat silently erased the onKill fire-rate buff.** Overheat's
    //     `Degrade` writes EVERY FRAME while `response: slow`, and it always
    //     recomputes from its own `_rateWas`. So whatever the kill buff wrote
    //     was overwritten on the next frame, for as long as any heat remained.
    //     The buff's own drift check (`Push` re-baselines when the weapon has
    //     moved since it last wrote) makes this worse rather than better: with a
    //     per-frame writer present, "something else moved it" is true every
    //     frame, so `baseFireRate` became whatever overheat had just written,
    //     and on expiry the buff wrote that degraded number back as if it were
    //     the weapon's own rate.
    //   - Nothing warned, in either direction. Both features are correct in
    //     isolation and both log exactly what they did.
    //
    // THE RULE, stated the way the tint one is: **a per-frame write that reads
    // its own previous output has no base to return to, and two features
    // capturing the same field cannot be ordered.** So keep ONE captured base
    // per (weapon, stat), captured once and never from a modified state, and
    // let every feature publish a named MULTIPLIER. The applied value is the
    // base times the product of every live contribution. Multiplication is
    // commutative, so there is no ordering to get wrong and no restore path
    // that can bake somebody else's number in.
    //
    // WHY THE BASE IS DROPPED WHEN THE LAST CONTRIBUTION GOES. The game's own
    // module augmentations can legitimately change a weapon's rate between
    // engagements, so a base held forever would eventually be a stale number
    // waiting to be written back. Nothing live means nothing captured, and the
    // next contributor captures the weapon as it is then.
    //
    // A CLUSTER REFRESH BUILDS A NEW `WeaponBase`, so keying the dictionary on
    // the instance gives the identity check that `ForgeSlow.Restore` and
    // `ForgeOverheatPatch.Degrade` each hand-rolled: a fresh weapon is simply a
    // key that is not in the table.
    public static class ForgeWeaponStats
    {
        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("WeaponForge.Stats");

        // The stats worth owning: every one of these has, or is about to have,
        // more than one writer.
        public const int FireRate = 0;
        public const int Damage = 1;
        public const int AngleVariance = 2;
        public const int Spread = 3;
        public const int Pellets = 4;
        public const int Cost = 5;

        private const int Count = 6;

        public static string Name(int stat)
        {
            switch (stat)
            {
                case FireRate: return "fire rate";
                case Damage: return "damage";
                case AngleVariance: return "angle variance";
                case Spread: return "spread";
                case Pellets: return "projectile count";
                case Cost: return "cost";
                default: return "stat " + stat;
            }
        }

        private class Stat
        {
            public bool captured;
            public float baseValue;

            // Parallel lists rather than a dictionary: there are never more
            // than a handful of contributors and this is walked every frame.
            public readonly List<string> keys = new List<string>();
            public readonly List<float> mults = new List<float>();

            public float Product()
            {
                float p = 1f;

                for (int i = 0; i < mults.Count; i++)
                    p *= mults[i];

                return p;
            }
        }

        private class Entry
        {
            public readonly Stat[] stats = new Stat[Count];

            public Stat Of(int stat)
            {
                if (stats[stat] == null)
                    stats[stat] = new Stat();

                return stats[stat];
            }

            public bool Idle()
            {
                for (int i = 0; i < Count; i++)
                {
                    if (stats[i] != null && stats[i].keys.Count > 0)
                        return false;
                }

                return true;
            }
        }

        private static readonly Dictionary<WeaponBase, Entry> _live =
            new Dictionary<WeaponBase, Entry>();

        // ------------------------------------------------------------------
        // Publishing
        // ------------------------------------------------------------------

        // `multiplier` of 1 means "no change from this contributor". Calling
        // again with the same key REPLACES that contribution rather than adding
        // another, so a per-frame publisher is the normal case rather than a
        // leak.
        public static void Set(
            WeaponBase weapon, int stat, string key, float multiplier)
        {
            if (weapon == null || string.IsNullOrEmpty(key) ||
                stat < 0 || stat >= Count)
            {
                return;
            }

            try
            {
                Entry e;

                if (!_live.TryGetValue(weapon, out e))
                {
                    e = new Entry();
                    _live[weapon] = e;
                }

                Stat s = e.Of(stat);

                // THE ONE READ OF THE LIVE VALUE. Everything after this is
                // computed from the captured base, which is what makes the
                // whole thing order-independent.
                if (!s.captured)
                {
                    s.captured = true;
                    s.baseValue = Read(weapon, stat);
                }

                int at = s.keys.IndexOf(key);

                if (at < 0)
                {
                    s.keys.Add(key);
                    s.mults.Add(multiplier);
                }
                else
                {
                    s.mults[at] = multiplier;
                }

                Write(weapon, stat, s.baseValue * s.Product());
            }
            catch (Exception ex)
            {
                Log.LogError("Publishing a weapon stat failed: " + ex);
            }
        }

        // Withdraw one contributor from one stat.
        public static void Clear(WeaponBase weapon, int stat, string key)
        {
            if (weapon == null || string.IsNullOrEmpty(key) ||
                stat < 0 || stat >= Count)
            {
                return;
            }

            try
            {
                Entry e;

                if (!_live.TryGetValue(weapon, out e))
                    return;

                Stat s = e.stats[stat];

                if (s == null)
                    return;

                int at = s.keys.IndexOf(key);

                if (at < 0)
                    return;

                s.keys.RemoveAt(at);
                s.mults.RemoveAt(at);

                if (s.keys.Count > 0)
                {
                    Write(weapon, stat, s.baseValue * s.Product());
                }
                else if (s.captured)
                {
                    // Hand the weapon back exactly as it was found, and forget
                    // the base so the next contributor captures a fresh one.
                    Write(weapon, stat, s.baseValue);
                    s.captured = false;
                }

                if (e.Idle())
                    _live.Remove(weapon);
            }
            catch (Exception ex)
            {
                Log.LogError("Withdrawing a weapon stat failed: " + ex);
            }
        }

        // Withdraw one contributor from every stat - what a feature ending
        // wants, so it cannot forget one of its own writes.
        public static void Clear(WeaponBase weapon, string key)
        {
            for (int i = 0; i < Count; i++)
                Clear(weapon, i, key);
        }

        // The weapon's value with none of our contributions - for log lines and
        // for any feature that needs to reason about the unmodified gun.
        public static float Base(WeaponBase weapon, int stat)
        {
            if (weapon == null || stat < 0 || stat >= Count)
                return 0f;

            Entry e;

            if (_live.TryGetValue(weapon, out e))
            {
                Stat s = e.stats[stat];

                if (s != null && s.captured)
                    return s.baseValue;
            }

            return Read(weapon, stat);
        }

        // Per-run state. Called from the same reset the crit and burn engines
        // use: these are live `WeaponBase` instances, which do not survive a
        // run, and a stale key would hold one alive for the session.
        public static void Reset()
        {
            _live.Clear();
        }

        // ------------------------------------------------------------------
        // The fields themselves
        // ------------------------------------------------------------------

        private static float Read(WeaponBase w, int stat)
        {
            switch (stat)
            {
                case FireRate: return w.FireRate;
                case Damage: return w.Damage.amount;
                case AngleVariance: return w.AngleVariance;
                case Spread: return w.Spread;
                case Pellets: return w.ProjectileCount;
                case Cost: return w.Cost;
                default: return 0f;
            }
        }

        private static void Write(WeaponBase w, int stat, float value)
        {
            switch (stat)
            {
                case FireRate:
                    // A rate of 0 is a division by zero in
                    // `Shooter.FireRatePassed` (`1f / FireRate`), which yields
                    // Infinity and a gun that never fires again.
                    w.FireRate = Mathf.Max(0.01f, value);
                    break;

                case Damage:
                    // `Damage` is a STRUCT, so the property has to be read,
                    // modified and assigned back - `w.Damage.amount = x` would
                    // not compile and, worse, an extension that made it compile
                    // would write to a copy.
                    Damage d = w.Damage;
                    d.amount = Mathf.Max(0f, value);
                    w.Damage = d;
                    break;

                case AngleVariance:
                    // `GetDirections` calls `Random.Range(-v, v)`, which is
                    // happy with 0 and nonsense with a negative.
                    w.AngleVariance = Mathf.Max(0f, value);
                    break;

                case Spread:
                    w.Spread = Mathf.Clamp(value, 0f, 360f);
                    break;

                case Pellets:
                    // ROUNDED, because the fan geometry is built from it:
                    // `step = Spread / (ProjectileCount - 1)`, so a count of
                    // 2.5 spaces the pellets as though there were 1.5 gaps and
                    // the volley comes out lopsided. The loop condition
                    // (`while (i < ProjectileCount)`) would happily fire three.
                    w.ProjectileCount = Mathf.Max(1f, Mathf.Round(value));
                    break;

                case Cost:
                    w.Cost = Mathf.Max(0f, value);
                    break;
            }
        }
    }
}
