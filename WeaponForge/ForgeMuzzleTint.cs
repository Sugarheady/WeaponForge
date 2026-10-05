using System;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;

namespace WeaponForge
{
    // ONE OWNER FOR THE MUZZLE FLASH COLOUR.
    //
    // WHY THIS EXISTS, and it is the same reason `ForgeUnitTint` does - two
    // subsystems along, and predicted rather than reported this time.
    //
    // `overheat` tints the muzzle flash toward `hotColor` as heat builds, and it
    // does so by capturing each particle system's `startColor` once and lerping
    // from that captured base every frame. Correct for exactly one feature.
    // `spinUp` tints the same flash toward its own colour as the gun winds up -
    // and the author asked for the two blocks to COMPOSE on one weapon, so:
    //
    //     overheat captures ORIGINAL, writes ORIGINAL -> hot
    //     spinUp   captures ORIGINAL->hot            <- ALREADY TINTED
    //     heat falls to zero, overheat writes ORIGINAL back
    //     spin falls to zero, spinUp writes ORIGINAL->hot back   <- BAKED IN
    //
    // The flash keeps the heat colour for the rest of the run, with both
    // features reporting that they restored correctly. That is not a colour
    // fighting a colour, it is a second save/restore pair capturing the first
    // one's output, and it is why the layer table had to exist before spin-up
    // could tint anything.
    //
    // WHY NOT THE UNIT TINT'S MULTIPLY. `ForgeUnitTint` multiplies its layers
    // because multiplication is commutative and so cannot be ordered wrongly. A
    // flash colour is a REPLACEMENT though - overheat's orange is meant to take
    // over a white puff, not darken it - and switching to multiply would
    // visibly change every overheat weapon already written.
    //
    // The first version therefore kept lerps, applied in a stable order sorted
    // by key, on the reasoning that order-independence was the property that
    // mattered. **It is not sufficient, and R14 test 176 is the proof.** A
    // chain of lerps is order-independent and still wrong with two layers,
    // because `Lerp(x, c, 1)` discards `x` - so the last key alphabetically
    // wins outright. "heat" sorts before "spin", so a fully wound gun showed
    // no heat whatever, and the log line claiming the two compose was false.
    // See the note in `Write` for the weighted mix that replaced it, which is
    // term-for-term identical with a single layer.
    //
    // THE INSTANCE LIST IS NOT STABLE. `WeaponBase.InitializeVisuals` destroys
    // and rebuilds `muzzleParticleSystemInstances` to match `ProjectileCount` -
    // which spin-up's `pellets` reward changes at runtime - so a capture taken
    // once and latched forever would end up holding destroyed systems. The
    // capture is re-taken whenever the list changes shape, and that is safe
    // precisely because the fresh instances are untinted clones of the prefab.
    public static class ForgeMuzzleTint
    {
        private static readonly ManualLogSource Log =
            ForgeLog.Source("WeaponForge.Tint");

        public const string Heat = "heat";
        public const string Spin = "spin";

        private class Layer
        {
            public string key;
            public Color color;
            public float amount;      // 0 = invisible, 1 = fully this colour
        }

        private class Entry
        {
            public readonly List<ParticleSystem> systems =
                new List<ParticleSystem>();
            public readonly List<ParticleSystem.MinMaxGradient> bases =
                new List<ParticleSystem.MinMaxGradient>();
            public readonly List<Layer> layers = new List<Layer>();

            // The size of the WEAPON's own instance list when we last captured.
            // That list is what `InitializeVisuals` resizes, so a change to it
            // is the churn signal - and comparing one int is what lets the
            // steady state skip the deep walk entirely. See Capture.
            public int sourceCount = -1;

            public Layer Find(string key)
            {
                for (int i = 0; i < layers.Count; i++)
                {
                    if (layers[i].key == key)
                        return layers[i];
                }

                return null;
            }
        }

        private static readonly Dictionary<WeaponBase, Entry> _live =
            new Dictionary<WeaponBase, Entry>();

        // `amount` is the whole strength of this layer: a feature multiplies its
        // own ramp (heat fraction, spin fraction) by the author's tintStrength
        // and passes the product, so there is one number here rather than two.
        public static void Set(
            WeaponBase weapon, string key, Color color, float amount)
        {
            if (weapon == null || string.IsNullOrEmpty(key))
                return;

            try
            {
                Entry e;

                if (!_live.TryGetValue(weapon, out e))
                {
                    e = new Entry();
                    _live[weapon] = e;
                }

                if (!Capture(weapon, e))
                    return;

                Layer layer = e.Find(key);

                if (layer == null)
                {
                    layer = new Layer { key = key };
                    e.layers.Add(layer);

                    // Stable order, so the composed colour never depends on
                    // which feature happened to publish first.
                    e.layers.Sort(
                        delegate(Layer a, Layer b)
                        {
                            return string.Compare(
                                a.key, b.key, StringComparison.Ordinal);
                        });
                }

                layer.color = color;
                layer.amount = Mathf.Clamp01(amount);

                Write(e);
            }
            catch (Exception ex)
            {
                Log.LogError("Tinting a muzzle flash failed: " + ex);
            }
        }

        public static void Clear(WeaponBase weapon, string key)
        {
            if (weapon == null || string.IsNullOrEmpty(key))
                return;

            try
            {
                Entry e;

                if (!_live.TryGetValue(weapon, out e))
                    return;

                Layer layer = e.Find(key);

                if (layer != null)
                    e.layers.Remove(layer);

                Write(e);

                if (e.layers.Count == 0)
                    _live.Remove(weapon);
            }
            catch (Exception ex)
            {
                Log.LogError("Clearing a muzzle tint failed: " + ex);
            }
        }

        public static void Reset()
        {
            _live.Clear();
        }

        // True once there is something to paint. A weapon with no muzzle prefab
        // (plenty ship none) is not an error - there is simply no flash.
        private static bool Capture(WeaponBase weapon, Entry e)
        {
            List<ParticleSystem> muzzles = ForgeOverheat.MuzzlesOf(weapon);

            if (muzzles == null)
                return false;

            // THE CHEAP PATH FIRST, AND IT IS NOT AN OPTIMISATION - IT IS A
            // REGRESSION FIX. `Set` is called every frame by a live ramp, and
            // the deep walk below allocates a List plus one array per muzzle
            // (`GetComponentsInChildren`). The code this class replaced captured
            // ONCE and latched, so routing overheat through here without this
            // check made a shipped, tested feature allocate every frame it was
            // hot. Comparing the weapon's own instance count and confirming
            // nothing has been destroyed is O(a handful) with no allocation, and
            // it still catches the one thing that really changes shape:
            // `InitializeVisuals` resizing that list for a new pellet count.
            if (e.sourceCount == muzzles.Count && e.systems.Count > 0 &&
                AllLive(e))
            {
                return true;
            }

            // A MUZZLE FLASH IS USUALLY TWO STACKED SYSTEMS (root plus
            // `Particle_muzzleFlash2`), so the children are collected as well
            // or the stock puff shows through underneath whatever we did.
            var found = new List<ParticleSystem>();

            for (int i = 0; i < muzzles.Count; i++)
            {
                if (muzzles[i] == null)
                    continue;

                ParticleSystem[] all =
                    muzzles[i].GetComponentsInChildren<ParticleSystem>(true);

                for (int j = 0; j < all.Length; j++)
                {
                    if (all[j] != null)
                        found.Add(all[j]);
                }
            }

            if (found.Count == 0)
                return false;

            if (!Stale(e, found))
                return true;

            // Re-capture from scratch. The systems in `found` are either the
            // ones already held (nothing to do, handled above) or fresh clones
            // of the prefab, which carry no tint of ours.
            e.systems.Clear();
            e.bases.Clear();
            e.sourceCount = muzzles.Count;

            for (int i = 0; i < found.Count; i++)
            {
                e.systems.Add(found[i]);
                e.bases.Add(found[i].main.startColor);
            }

            return true;
        }

        // A destroyed Unity object compares equal to null, so this is also the
        // "did InitializeVisuals replace them" test for the case where the count
        // happens to come back the same.
        private static bool AllLive(Entry e)
        {
            for (int i = 0; i < e.systems.Count; i++)
            {
                if (e.systems[i] == null)
                    return false;
            }

            return true;
        }

        private static bool Stale(Entry e, List<ParticleSystem> found)
        {
            if (e.systems.Count != found.Count)
                return true;

            for (int i = 0; i < e.systems.Count; i++)
            {
                if (e.systems[i] == null ||
                    !ReferenceEquals(e.systems[i], found[i]))
                {
                    return true;
                }
            }

            return false;
        }

        private static void Write(Entry e)
        {
            for (int i = 0; i < e.systems.Count; i++)
            {
                ParticleSystem one = e.systems[i];

                if (one == null)
                    continue;

                ParticleSystem.MainModule main = one.main;

                if (e.layers.Count == 0)
                {
                    // Assigning the captured gradient back UNTOUCHED is what
                    // makes cooling and spinning down visibly reverse. Lerping
                    // toward a colour by 0 would return whatever is there now,
                    // which is the bug this class exists to prevent.
                    main.startColor = e.bases[i];
                    continue;
                }

                // A WEIGHTED MIX, NOT A CHAIN OF LERPS - and this is a bug fix,
                // not a tidy-up.
                //
                // R14 test 176 put `spinUp` and `overheat` on one gun, and the
                // build log promised in as many words that the two "COMPOSE
                // rather than fight". The muzzle colour did not: successive
                // lerps mean the LAST layer at full strength erases every one
                // before it, because `Lerp(x, c, 1)` is just `c`. Keys sort
                // "heat" before "spin", so a fully wound gun showed no heat at
                // all - and it stops showing it exactly as the heat starts to
                // matter. He reported a white flash and no orange, which is
                // precisely that.
                //
                // Each layer instead contributes its own weight:
                //
                //     total <= 1 :  base*(1 - total) + SUM(colour_i * a_i)
                //     total >  1 :  SUM(colour_i * a_i) / total
                //
                // With ONE layer that is `base*(1-a) + c*a`, which is
                // `Lerp(base, c, a)` term for term - so every overheat weapon
                // already written renders byte-identically, including its
                // alpha. With two full layers it is the midpoint: winding up
                // AND hot looks like neither state alone, which is the honest
                // reading of "compose".
                //
                // It also makes the sort below redundant rather than
                // load-bearing: addition commutes, so there is no order left to
                // get wrong. Kept anyway, because a stable list is easier to
                // read in a debugger than a shuffled one.
                float total = 0f;
                float r = 0f, g = 0f, b = 0f, a = 0f;

                for (int j = 0; j < e.layers.Count; j++)
                {
                    Layer l = e.layers[j];

                    if (l.amount <= 0.001f)
                        continue;

                    total += l.amount;
                    r += l.color.r * l.amount;
                    g += l.color.g * l.amount;
                    b += l.color.b * l.amount;
                    a += l.color.a * l.amount;
                }

                if (total <= 0.001f)
                {
                    main.startColor = e.bases[i];
                    continue;
                }

                Color baseColor = e.bases[i].color;

                if (total > 1f)
                {
                    r /= total;
                    g /= total;
                    b /= total;
                    a /= total;
                }
                else
                {
                    float keep = 1f - total;

                    r += baseColor.r * keep;
                    g += baseColor.g * keep;
                    b += baseColor.b * keep;
                    a += baseColor.a * keep;
                }

                main.startColor =
                    new ParticleSystem.MinMaxGradient(new Color(r, g, b, a));
            }
        }
    }
}
