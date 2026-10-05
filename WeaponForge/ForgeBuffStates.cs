using System;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;

namespace WeaponForge
{
    // Reports the mod's ongoing STATES and DEBUFFS to the buff indicator.
    //
    // Deliberately a POLLER rather than hooks scattered through each feature.
    // A state is a level, not an event - "the shield is up" is true across many
    // frames - and the honest way to report a level is to read it, not to try to
    // catch every edge. Hooking would mean finding every path that raises and
    // drops a shield, every way a tether can come loose, and every route a fire
    // can go out by; missing one leaves an indicator stuck on screen, which is
    // worse than no indicator at all.
    //
    // The cost is one pass per frame over two short lists and one bool, and it
    // only runs while the player has actually asked for states or debuffs.
    //
    // Only ONE source here is hooked instead: the orbit ring, because it already
    // has a single explicit state-change point and no public registry to walk.
    public static class ForgeBuffStates
    {
        private static readonly ManualLogSource Log =
            ForgeLog.Source("WeaponForge.BuffHud");

        // Distinct keys so each state owns its own indicator entry and they can
        // never overwrite one another.
        private static readonly object KeyShield = new object();
        private static readonly object KeyTether = new object();
        private static readonly object KeyBurning = new object();

        private static GameObject _ticker;

        private static readonly Color ShieldColor =
            new Color(0.55f, 0.85f, 1f, 1f);
        private static readonly Color TetherColor =
            new Color(1f, 0.82f, 0.42f, 1f);
        private static readonly Color BurningColor =
            new Color(1f, 0.5f, 0.2f, 1f);

        public static void EnsureTicker()
        {
            if (_ticker != null)
                return;

            // Nothing to watch, so do not even spin the object up.
            if (!ForgeBuffHud.showTimedStates && !ForgeBuffHud.showDebuffs)
                return;

            _ticker = new GameObject("Forge Buff States");
            UnityEngine.Object.DontDestroyOnLoad(_ticker);
            _ticker.AddComponent<Ticker>();
        }

        // The orbit ring is the one PUSHED source: ForgeOrbitController already
        // funnels every activation through a single state-change point, and it
        // keeps no public registry to poll, so reporting from there is both
        // cheaper and more accurate than re-deriving it.
        public static void OrbitActive(object key, bool active)
        {
            if (!ForgeBuffHud.showTimedStates)
                return;

            try
            {
                if (active)
                {
                    ForgeBuffHud.ShowState(
                        key, new Color(0.72f, 0.6f, 1f, 1f), "ORBIT",
                        ForgeBuffHud.Kind.State);
                }
                else
                {
                    ForgeBuffHud.Clear(key);
                }
            }
            catch (Exception e)
            {
                Log.LogError("Orbit state indicator failed: " + e);
            }
        }

        private static void Tick()
        {
            if (ForgeBuffHud.showTimedStates)
            {
                Poll(KeyShield, AnyShieldUp(), ShieldColor, "SHIELD UP",
                     ForgeBuffHud.Kind.State);

                Poll(KeyTether, AnyTetherLatched(), TetherColor, "TETHERED",
                     ForgeBuffHud.Kind.State);
            }

            if (ForgeBuffHud.showDebuffs)
            {
                Poll(KeyBurning, PlayerBurning(), BurningColor, "BURNING",
                     ForgeBuffHud.Kind.Debuff);
            }
        }

        private static void Poll(
            object key, bool on, Color color, string label,
            ForgeBuffHud.Kind kind)
        {
            // Show() is already idempotent - it repaints only when the composed
            // text differs - so calling it every frame while a state holds costs
            // a dictionary lookup and a string compare.
            if (on)
                ForgeBuffHud.ShowState(key, color, label, kind);
            else
                ForgeBuffHud.Clear(key);
        }

        // ------------------------------------------------------------------
        // The sources
        // ------------------------------------------------------------------

        private static bool AnyShieldUp()
        {
            IList<ForgeShieldController> live = ForgeShieldController.Live;

            for (int i = 0; i < live.Count; i++)
            {
                ForgeShieldController c = live[i];

                if (c != null && c.ArcUp && IsPlayer(c.unit))
                    return true;
            }

            return false;
        }

        private static bool AnyTetherLatched()
        {
            IList<ForgeTether> live = ForgeTether.Live;

            for (int i = 0; i < live.Count; i++)
            {
                ForgeTether t = live[i];

                if (t != null && t.latched && IsPlayer(t.owner))
                    return true;
            }

            return false;
        }

        // Burning is the game's own flag rather than anything the mod tracks -
        // ComponentData.IsOnFire is what StatusEffectForUnit reads to emit the
        // fire particles. So this reports the real thing, whatever set it
        // alight, which is the point of a debuff indicator.
        private static bool PlayerBurning()
        {
            Unit unit = ForgeBuffHud.BoundUnit;

            return unit != null &&
                   unit.ComponentData != null &&
                   unit.ComponentData.IsOnFire;
        }

        // A state on some other unit is not the player's business. The bound
        // ship is the one the HUD belongs to, so that is the comparison.
        private static bool IsPlayer(Unit unit)
        {
            Unit mine = ForgeBuffHud.BoundUnit;

            return unit != null && mine != null && unit == mine;
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
                    Log.LogError(
                        "Buff state polling failed, states will stop " +
                        "reporting: " + e);

                    enabled = false;
                }
            }
        }
    }
}
