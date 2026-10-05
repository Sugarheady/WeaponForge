using System;
using System.Collections.Generic;
using BepInEx.Logging;
using MyBox;          // MinMaxFloat, which is what WeaponBase.Burn is
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
    //
    // ------------------------------------------------------------------------
    // THE TWELVE STATS ADDED FOR `surge`, 2026-09-17, and what it cost
    // ------------------------------------------------------------------------
    //
    // It used to own six - the ones `spinUp` rewards. His ask for `surge` was
    // *"please include the other base game options as well. Like projectile
    // count, projectile speed, projectile range, spread, burst,
    // burnrate/burndamage, damage, etc."*
    //
    // **THE VOCABULARY WAS ALREADY DECIDED, and copying it was the whole
    // design.** `charge` has scaled `damage / explosion / burn / push / spread /
    // count / speed / range / size / pierce` since it shipped, by saving each
    // field around `DoShoot` and putting it back. So the words exist, the author
    // already knows them, and the only question was the mechanism. A second
    // vocabulary for one idea is a bug report waiting to be filed.
    //
    // **The difference between the two mechanisms is worth stating**, because it
    // decides which one a new feature should use: `charge` scopes a value to ONE
    // VOLLEY and restores it synchronously inside `DoShoot`, so an in-flight
    // shot keeps what it was fired with. This table holds a value for a WINDOW
    // measured in seconds, across many shots and alongside other publishers, so
    // it needs the captured base and the product. Neither is a replacement for
    // the other.
    //
    // THREE OF THE NEW ONES ARE NOT PLAIN FLOATS, and each needed a decision:
    //
    //   - **`Burn` is a `MinMaxFloat`**, so a single captured base cannot
    //     express it. `baseValue2` carries the Min; both ends scale by the same
    //     product, which is what `ForgeChargePatch.Apply` already does.
    //   - **`ExplosionDamage` is a `List<Damage>`**, so the base is an ARRAY,
    //     captured once. The list is rebuilt through `Explosion.Duplicate()` on
    //     every write rather than mutated in place - `WeaponBase` gets its own
    //     list from the constructor so in-place editing would be safe from the
    //     asset's point of view, but `FireSingle` hands the struct (and
    //     therefore the list REFERENCE) to the projectile, so editing in place
    //     would silently re-scale an explosion already in the air. A shot keeps
    //     what it was fired with.
    //   - **`Size` means two different fields**, exactly as it does in `charge`:
    //     a projectile's `ProjectileRadius` (which is the real hitbox, not just
    //     the art) and a beam's `RayWidth` (which is the damage cast - a beam's
    //     visual is one stretched quad and does not get thicker).
    //
    // `Range` and `Speed` go through the game's own `IHasRangeProperty` /
    // `IHasSpeedProperty`, which is why `Range` needs no type branch at all: a
    // projectile weapon and a hitscan weapon both implement it.
    //
    // A STAT THAT DOES NOT APPLY TO THIS WEAPON READS 0 AND WRITES NOTHING,
    // rather than throwing or writing a zero into something. `Speed` on a beam
    // and `Lifetime` on a hitscan are the two real cases. **That silence is why
    // `ApplySurge` warns about them at BUILD time** - a reward that resolves to
    // a no-op is the silent-fallback family, and the runtime is the wrong place
    // to find out.
    public static class ForgeWeaponStats
    {
        private static readonly ManualLogSource Log =
            ForgeLog.Source("WeaponForge.Stats");

        // The stats worth owning: every one of these has, or is about to have,
        // more than one writer.
        public const int FireRate = 0;
        public const int Damage = 1;
        public const int AngleVariance = 2;
        public const int Spread = 3;
        public const int Pellets = 4;
        public const int Cost = 5;

        // Added for `surge`. All twelve are live public setters on `WeaponBase`
        // or on one of its two subclasses - nothing here is reflected.
        public const int Range = 6;
        public const int Speed = 7;
        public const int BurstSize = 8;
        public const int BurstDelay = 9;
        public const int Burn = 10;
        public const int Push = 11;
        public const int Knockback = 12;
        public const int Size = 13;
        public const int ExplosionRadius = 14;
        public const int ExplosionDamage = 15;
        public const int Warmup = 16;
        public const int Lifetime = 17;

        private const int Count = 18;

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
                case Range: return "range";
                case Speed: return "projectile speed";
                case BurstSize: return "burst size";
                case BurstDelay: return "burst delay";
                case Burn: return "burn";
                case Push: return "push force";
                case Knockback: return "knockback";
                case Size: return "projectile size";
                case ExplosionRadius: return "explosion radius";
                case ExplosionDamage: return "explosion damage";
                case Warmup: return "warmup time";
                case Lifetime: return "projectile lifetime";
                default: return "stat " + stat;
            }
        }

        // Does this weapon have anything for that stat to move? Used by the
        // build log so a reward that cannot land is named at load rather than
        // being a silence in play.
        public static bool AppliesTo(WeaponData weapon, int stat)
        {
            if (weapon == null)
                return true;

            bool projectile = weapon is ProjectileWeaponData;
            bool beam = weapon is HitscanWeaponData;

            switch (stat)
            {
                case Speed:
                case Lifetime:
                    return projectile;

                case Size:
                    return projectile || beam;

                default:
                    return true;
            }
        }

        private class Stat
        {
            public bool captured;
            public float baseValue;

            // The second half of a two-part base. Only `Burn` (a MinMaxFloat)
            // uses it; everything else leaves it at 0.
            public float baseValue2;

            // The base for a stat whose value is a LIST. Only `ExplosionDamage`
            // uses it.
            public float[] baseList;

            // What product was last written. Writing is not always free - the
            // explosion stats rebuild a struct and a list - and a publisher that
            // republishes an unchanged multiplier every frame is the normal
            // case, not the exception. NaN so the first write always happens.
            public float lastProduct = float.NaN;

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
                    s.baseValue2 = Read2(weapon, stat);
                    s.baseList = ReadList(weapon, stat);
                    s.lastProduct = float.NaN;
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

                Write(weapon, stat, s, s.Product());
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
                    Write(weapon, stat, s, s.Product());
                }
                else if (s.captured)
                {
                    // Hand the weapon back exactly as it was found, and forget
                    // the base so the next contributor captures a fresh one.
                    Write(weapon, stat, s, 1f);
                    s.captured = false;
                    s.baseList = null;
                    s.lastProduct = float.NaN;
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
            var pw = w as ProjectileWeapon;

            switch (stat)
            {
                case FireRate: return w.FireRate;
                case Damage: return w.Damage.amount;
                case AngleVariance: return w.AngleVariance;
                case Spread: return w.Spread;
                case Pellets: return w.ProjectileCount;
                case Cost: return w.Cost;

                case Range:
                {
                    var r = w as IHasRangeProperty;
                    return (r != null) ? r.Range : 0f;
                }

                case Speed:
                {
                    var s = w as IHasSpeedProperty;
                    return (s != null) ? s.Speed : 0f;
                }

                case BurstSize: return w.BurstSize;
                case BurstDelay: return w.BurstDelay;

                // The MAX end. The Min comes back from Read2.
                case Burn: return w.Burn.Max;

                case Push: return w.PushForce;
                case Knockback: return w.KnockbackForce;

                case Size:
                {
                    if (pw != null)
                        return pw.ProjectileRadius;

                    var hw = w as HitscanWeapon;
                    return (hw != null) ? hw.RayWidth : 0f;
                }

                case ExplosionRadius: return w.Explosion.radius;

                // Not a single number. The list is the base; this is only here
                // so `Base()` and the idle check have something to report.
                case ExplosionDamage:
                {
                    var d = w.Explosion.damages;
                    return (d != null && d.Count > 0) ? d[0].amount : 0f;
                }

                case Warmup: return w.WarmupTime;

                case Lifetime:
                    return (pw != null) ? pw.LifetimeData.time : 0f;

                default: return 0f;
            }
        }

        private static float Read2(WeaponBase w, int stat)
        {
            switch (stat)
            {
                case Burn: return w.Burn.Min;
                default: return 0f;
            }
        }

        private static float[] ReadList(WeaponBase w, int stat)
        {
            if (stat != ExplosionDamage)
                return null;

            List<Damage> d = w.Explosion.damages;

            if (d == null || d.Count == 0)
                return null;

            var baseline = new float[d.Count];

            for (int i = 0; i < d.Count; i++)
                baseline[i] = d[i].amount;

            return baseline;
        }

        private static void Write(WeaponBase w, int stat, Stat s, float k)
        {
            // Nothing has moved since the last write. Cheap for the plain
            // floats and load-bearing for the explosion pair, which rebuilds a
            // struct and a list every time it really does write.
            if (!float.IsNaN(s.lastProduct) &&
                Mathf.Abs(k - s.lastProduct) < 0.000001f)
            {
                return;
            }

            s.lastProduct = k;

            float value = s.baseValue * k;
            var pw = w as ProjectileWeapon;

            switch (stat)
            {
                case FireRate:
                    // A rate of 0 is a division by zero in
                    // `Shooter.FireRatePassed` (`1f / FireRate`), which yields
                    // Infinity and a gun that never fires again.
                    w.FireRate = Mathf.Max(0.01f, value);
                    break;

                case Damage:
                {
                    // `Damage` is a STRUCT, so the property has to be read,
                    // modified and assigned back - `w.Damage.amount = x` would
                    // not compile and, worse, an extension that made it compile
                    // would write to a copy.
                    Damage d = w.Damage;
                    d.amount = Mathf.Max(0f, value);
                    w.Damage = d;
                    break;
                }

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
                    //
                    // A WEAPON AT projectileCount 0 FIRES NOTHING BY DESIGN and
                    // must stay that way - that is this mod's own spelling for
                    // a pure melee or lunge-only gun, and `charge` learned it
                    // the hard way in R11 test 80.
                    if (s.baseValue > 0f)
                        w.ProjectileCount = Mathf.Max(1f, Mathf.Round(value));
                    break;

                case Cost:
                    w.Cost = Mathf.Max(0f, value);
                    break;

                case Range:
                {
                    var r = w as IHasRangeProperty;

                    if (r != null)
                        r.Range = Mathf.Max(0f, value);

                    break;
                }

                case Speed:
                {
                    var sp = w as IHasSpeedProperty;

                    if (sp != null)
                        sp.Speed = Mathf.Max(0f, value);

                    break;
                }

                case BurstSize:
                    // An int, and a burst of 0 is a weapon that fires nothing:
                    // `WeaponBase.Fire` is `for (b = 0; b < BurstSize; b++)`.
                    w.BurstSize = Mathf.Max(1, Mathf.RoundToInt(value));
                    break;

                case BurstDelay:
                    w.BurstDelay = Mathf.Max(0f, value);
                    break;

                case Burn:
                    // Both ends by the same product, exactly as `charge` does.
                    w.Burn = new MinMaxFloat(
                        Mathf.Max(0f, s.baseValue2 * k), Mathf.Max(0f, value));
                    break;

                case Push:
                    w.PushForce = value;
                    break;

                case Knockback:
                    w.KnockbackForce = value;
                    break;

                case Size:
                {
                    if (pw != null)
                    {
                        pw.ProjectileRadius = Mathf.Max(0.001f, value);
                        break;
                    }

                    var hw = w as HitscanWeapon;

                    if (hw != null)
                        hw.RayWidth = Mathf.Max(0.001f, value);

                    break;
                }

                case ExplosionRadius:
                {
                    Explosion ex = w.Explosion;
                    ex.radius = Mathf.Max(0f, value);
                    w.Explosion = ex;
                    break;
                }

                case ExplosionDamage:
                {
                    if (s.baseList == null)
                        break;

                    // DUPLICATE, do not edit in place. The weapon owns its own
                    // list (the `WeaponBase` constructor calls `Duplicate()`),
                    // so editing it would not reach the asset - but
                    // `FireSingle` hands the struct to the projectile, and the
                    // struct carries the list by REFERENCE. Editing in place
                    // would re-scale an explosion that is already in the air.
                    Explosion ex = w.Explosion.Duplicate();

                    if (ex.damages != null &&
                        ex.damages.Count == s.baseList.Length)
                    {
                        for (int i = 0; i < ex.damages.Count; i++)
                        {
                            ex.damages[i] = new Damage(
                                Mathf.Max(0f, s.baseList[i] * k),
                                ex.damages[i].damageType);
                        }
                    }

                    w.Explosion = ex;
                    break;
                }

                case Warmup:
                    // 0 is legitimate and useful: it removes the game's own
                    // minigun gate for the length of a window.
                    w.WarmupTime = Mathf.Max(0f, value);
                    break;

                case Lifetime:
                {
                    if (pw == null)
                        break;

                    ProjectileLifetimeData lt = pw.LifetimeData;
                    lt.time = Mathf.Max(0.01f, value);
                    pw.LifetimeData = lt;
                    break;
                }
            }
        }
    }
}
