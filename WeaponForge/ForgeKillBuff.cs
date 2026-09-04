using System;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;

namespace WeaponForge
{
    // The timed half of an onKill reward.
    //
    // Weapon stats are not set-and-forget: `WeaponFactory.Create` rebuilds a
    // WeaponBase from its TemplateData and re-runs every IWeaponModifier
    // whenever the weapon's module cluster changes. A buff that pokes FireRate
    // and remembers a delta is therefore wrong twice over - the rebuild wipes
    // the buff, and restoring the remembered value afterwards writes back a
    // number that is no longer the baseline.
    //
    // So a buff is always expressed as baseline x multiplier, and the baseline
    // is owned by `ForgeWeaponStats`.
    //
    // IT USED TO OWN THAT BASELINE ITSELF, AND THAT WAS A REAL BUG. This class
    // captured `baseFireRate` / `baseDamage` and re-captured them whenever it
    // noticed the weapon had "moved underneath us" - a heuristic that works
    // against another one-shot writer and fails completely against a per-frame
    // one. `overheat` with `response: slow` rewrites `FireRate` every frame
    // while any heat remains, so:
    //
    //   - the buff's write was overwritten on the very next frame, meaning
    //     **the onKill fire-rate bonus did nothing whatsoever on an
    //     overheating weapon**;
    //   - "something else moved it" was true every frame, so the re-capture
    //     adopted overheat's degraded rate as the weapon's own, and on expiry
    //     this class wrote that degraded number back as the baseline.
    //
    // Both features logged exactly what they intended. Publishing a named
    // multiplier instead has no ordering to get wrong: see ForgeWeaponStats.
    public static class ForgeKillBuff
    {
        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("WeaponForge.KillBuff");

        public const string Key = "onKill";

        private class State
        {
            public WeaponBase weapon;
            public readonly List<float> fire = new List<float>();
            public readonly List<float> dmg = new List<float>();
            public readonly List<float> expiry = new List<float>();
            public int maxStacks = 1;

            // For the on-screen indicator only.
            public Sprite icon;
            public Color color = Color.white;
            public bool indicate;
        }

        private static readonly List<State> _live = new List<State>();
        private static GameObject _ticker;

        public static void Grant(
            WeaponBase weapon, float fireBonus, float dmgBonus,
            float duration, int maxStacks)
        {
            Grant(weapon, fireBonus, dmgBonus, duration, maxStacks,
                  null, Color.white, false);
        }

        public static void Grant(
            WeaponBase weapon, float fireBonus, float dmgBonus,
            float duration, int maxStacks,
            Sprite icon, Color color, bool indicate)
        {
            if (weapon == null || duration <= 0f)
                return;

            EnsureTicker();

            State s = Find(weapon);

            if (s == null)
            {
                s = new State();
                s.weapon = weapon;
                _live.Add(s);
            }

            s.maxStacks = Mathf.Max(1, maxStacks);
            s.icon = icon;
            s.color = color;
            s.indicate = indicate;

            if (s.expiry.Count >= s.maxStacks)
            {
                // At the cap: refresh the soonest-expiring stack rather than
                // adding another, so maxStacks 1 behaves as a plain refresh.
                int soonest = 0;

                for (int i = 1; i < s.expiry.Count; i++)
                    if (s.expiry[i] < s.expiry[soonest])
                        soonest = i;

                s.expiry[soonest] = Time.time + duration;
                s.fire[soonest] = fireBonus;
                s.dmg[soonest] = dmgBonus;
            }
            else
            {
                s.expiry.Add(Time.time + duration);
                s.fire.Add(fireBonus);
                s.dmg.Add(dmgBonus);
            }

            Push(s);
        }

        private static State Find(WeaponBase w)
        {
            for (int i = 0; i < _live.Count; i++)
                if (_live[i].weapon == w)
                    return _live[i];

            return null;
        }

        private static void Push(State s)
        {
            if (s.weapon == null)
                return;

            float fire = 0f;
            float dmg = 0f;

            for (int i = 0; i < s.expiry.Count; i++)
            {
                fire += s.fire[i];
                dmg += s.dmg[i];
            }

            // Stacks ADD and the total is applied as one multiplier, which is
            // what it always did - three 20% stacks are +60%, not 1.2 cubed.
            // What changed is where the baseline lives.
            Apply(s.weapon, ForgeWeaponStats.FireRate, fire);
            Apply(s.weapon, ForgeWeaponStats.Damage, dmg);

            Indicate(s, fire, dmg);
        }

        // A bonus of 0 WITHDRAWS rather than publishing a multiplier of 1, so
        // the stack forgets its captured base and a legitimate change to the
        // weapon - a module augmentation, a cluster rebuild - is picked up
        // instead of being held down by a contribution that does nothing.
        private static void Apply(WeaponBase weapon, int stat, float bonus)
        {
            if (Mathf.Abs(bonus) > 0.0001f)
                ForgeWeaponStats.Set(weapon, stat, Key, 1f + bonus);
            else
                ForgeWeaponStats.Clear(weapon, stat, Key);
        }

        // The on-screen half. Keyed on the State, so one weapon owns exactly one
        // indicator no matter how many stacks it has.
        private static void Indicate(State s, float fire, float dmg)
        {
            if (!s.indicate)
                return;

            // Whichever bonus is actually doing something gets named; fire rate
            // wins a tie because it is the one you feel.
            string effect = (fire > 0f) ? "FIRE RATE" : "DAMAGE";
            float amount = (fire > 0f) ? fire : dmg;

            if (amount <= 0f)
                return;

            float latest = 0f;

            for (int i = 0; i < s.expiry.Count; i++)
                if (s.expiry[i] > latest)
                    latest = s.expiry[i];

            ForgeBuffHud.Show(
                s, s.icon, s.color, effect, amount,
                s.expiry.Count, latest, ForgeBuffHud.Kind.Buff);
        }

        private static float Total(List<float> values)
        {
            float sum = 0f;

            for (int i = 0; i < values.Count; i++)
                sum += values[i];

            return sum;
        }

        private static void Tick()
        {
            for (int i = _live.Count - 1; i >= 0; i--)
            {
                State s = _live[i];

                if (s.weapon == null)
                {
                    ForgeBuffHud.Clear(s);
                    _live.RemoveAt(i);
                    continue;
                }

                // A seconds-remaining label has to be repainted as the clock
                // runs, even on a frame where no stack changed. Show() only
                // repaints when the composed text actually differs, so this
                // settles at about one repaint a second rather than 60.
                if (s.indicate)
                    Indicate(s, Total(s.fire), Total(s.dmg));

                bool changed = false;

                for (int j = s.expiry.Count - 1; j >= 0; j--)
                {
                    if (Time.time < s.expiry[j])
                        continue;

                    s.expiry.RemoveAt(j);
                    s.fire.RemoveAt(j);
                    s.dmg.RemoveAt(j);
                    changed = true;
                }

                if (changed)
                    Push(s);

                if (s.expiry.Count == 0)
                {
                    // Fully expired: hand the weapon back exactly as found.
                    // One call covers every stat this feature can touch, so a
                    // bonus added here later cannot be left behind.
                    ForgeWeaponStats.Clear(s.weapon, Key);

                    ForgeBuffHud.Clear(s);
                    _live.RemoveAt(i);
                }
            }
        }

        private static void EnsureTicker()
        {
            if (_ticker != null)
                return;

            _ticker = new GameObject("Forge Kill Buffs");
            UnityEngine.Object.DontDestroyOnLoad(_ticker);
            _ticker.AddComponent<Ticker>();
        }

        private class Ticker : MonoBehaviour
        {
            private void Update()
            {
                try
                {
                    Tick();
                }
                catch (Exception e)
                {
                    Log.LogError("Kill buff tick failed: " + e);
                    _live.Clear();
                }
            }
        }
    }
}
