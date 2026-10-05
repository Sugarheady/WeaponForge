using System;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace WeaponForge
{
    // Wiring for `gauge` and `bubble`.
    //
    // ONE HOOK, AND IT IS NOT `Ship.Update`. `ShipHud.AssignShip` is public and
    // fires exactly once when the HUD binds a player ship - which is both the
    // cheapest "a player ship now exists" signal in the game and, by
    // construction, player-only. That matters twice over: he asked for player
    // only ("no reason to put more visual clutter for minions/drones when they
    // can regulate themselves"), and a minion has no ShipCanvas to clone into
    // anyway.
    //
    // Nothing here patches `Ship.Update`, writes `Ship.selfDestructProgress`
    // or touches `Ship.selfDestructVisual`. The self-destruct is untouched.
    public static class ForgeGaugePatch
    {
        private static readonly ManualLogSource Log =
            ForgeLog.Source("WeaponForge.Gauge");

        private static bool _saidLive;

        [HarmonyPatch(typeof(ShipHud), "AssignShip")]
        public class OnAssignShip
        {
            static void Postfix(Ship ship)
            {
                if (!ForgeGauge.Any || ship == null)
                    return;

                try
                {
                    if (ship.GetComponent<ForgeGaugeController>() == null)
                        ship.gameObject.AddComponent<ForgeGaugeController>();
                }
                catch (Exception e)
                {
                    Log.LogError("gauge: could not attach to the ship: " + e);
                }
            }
        }

        public static void SayLive(string fileName)
        {
            if (_saidLive)
                return;

            _saidLive = true;

            Log.LogInfo(
                fileName + ": readouts are live. The bar shows the gun you are " +
                "FIRING; when you are firing neither it falls back to whichever " +
                "is still above its threshold, so a jammed gun stays on screen. " +
                "Said once per run.");
        }

        public static void Reset()
        {
            _saidLive = false;
        }
    }

    // One per player ship. Picks the gun, picks the source, drives the two
    // cloned widgets.
    public class ForgeGaugeController : MonoBehaviour
    {
        private static readonly ManualLogSource Log =
            ForgeLog.Source("WeaponForge.Gauge");

        private Ship _ship;
        private Shooter[] _shooters;

        private ForgeGaugeWidget.Bar _bar;
        private ForgeGaugeWidget.Bubble _bubble;

        private ForgeGauge.Bar _barCfg;
        private ForgeGauge.Bubble _bubbleCfg;

        // When any source was last actively worked. One clock, because his rule
        // is about the player ("if you havent fired for a certain amount of
        // time"), not about a particular gun.
        private float _lastDrivenAt = -999f;

        private void Awake()
        {
            _ship = GetComponent<Ship>();
        }

        private void OnDestroy()
        {
            if (_bar != null) _bar.Destroy();
            if (_bubble != null) _bubble.Destroy();
        }

        private void LateUpdate()
        {
            if (_ship == null || !ForgeGauge.Any)
                return;

            try
            {
                Tick();
            }
            catch (Exception e)
            {
                Log.LogError("gauge: update failed: " + e);

                // A readout must never cost the gun. Tear ourselves down rather
                // than log once a frame forever.
                enabled = false;
            }
        }

        // ------------------------------------------------------------------

        private void Tick()
        {
            if (_shooters == null || _shooters.Length == 0)
            {
                _shooters = GetComponentsInChildren<Shooter>(true);

                if (_shooters == null || _shooters.Length == 0)
                    return;
            }

            Pick();
        }

        // The gun and the source to show, in his order: the one you are firing
        // first, then anything urgent, then anything above its threshold.
        private void Pick()
        {
            ForgeGauge.Bar barCfg = null;
            ForgeGauge.Bubble bubbleCfg = null;
            ForgeGaugeSource.Reading barRead = new ForgeGaugeSource.Reading();
            ForgeGaugeSource.Reading bubbleRead = new ForgeGaugeSource.Reading();

            int barRank = int.MaxValue;
            int bubbleRank = int.MaxValue;

            for (int i = 0; i < _shooters.Length; i++)
            {
                Shooter shooter = _shooters[i];

                if (shooter == null || shooter.weaponHolder == null)
                    continue;

                WeaponBase weapon = shooter.weaponHolder.Weapon;
                WeaponData data = (weapon != null) ? weapon.TemplateData : null;

                if (data == null)
                    continue;

                ForgeGauge.Bar b;

                if (ForgeGauge.TryGetBar(data, out b))
                {
                    ForgeGaugeSource.Reading r;

                    if (Best(shooter, b.show, b.threshold, out r))
                    {
                        int rank = Rank(r);

                        if (rank < barRank)
                        {
                            barRank = rank;
                            barRead = r;
                            barCfg = b;
                        }
                    }
                }

                ForgeGauge.Bubble bub;

                if (ForgeGauge.TryGetBubble(data, out bub))
                {
                    ForgeGaugeSource.Reading r;

                    if (Best(shooter, bub.show, bub.slowAt, out r))
                    {
                        int rank = Rank(r);

                        if (rank < bubbleRank)
                        {
                            bubbleRank = rank;
                            bubbleRead = r;
                            bubbleCfg = bub;
                        }
                    }
                }
            }

            if (barRead.driven || bubbleRead.driven)
                _lastDrivenAt = Time.time;

            DriveBar(barCfg, barRead);
            DriveBubble(bubbleCfg, bubbleRead);
        }

        // Lower is better. Firing beats a lockout beats merely being above the
        // line - which is his answer ("whatever gun you're firing at the moment
        // makes sense") with the fallback that keeps a jammed gun visible.
        private static int Rank(ForgeGaugeSource.Reading r)
        {
            if (r.driven) return 0;
            if (r.urgent) return 1;

            return 2;
        }

        // The first source in the author's priority order that has something
        // worth showing. `show` is already in priority order - overheat before
        // charge before spin-up, unless the file says otherwise.
        private static bool Best(
            Shooter shooter, ForgeGauge.Source[] show, float threshold,
            out ForgeGaugeSource.Reading best)
        {
            best = new ForgeGaugeSource.Reading();

            if (show == null)
                return false;

            for (int i = 0; i < show.Length; i++)
            {
                ForgeGaugeSource.Reading r =
                    ForgeGaugeSource.Read(shooter, show[i]);

                if (!r.has)
                    continue;

                // "Has the feature" is not "has something to say". A stone-cold
                // gun with an overheat block must not win the bar away from a
                // charge that is actually building.
                if (!r.urgent && !r.driven && r.value < threshold)
                    continue;

                best = r;

                return true;
            }

            return false;
        }

        // ------------------------------------------------------------------

        private void DriveBar(ForgeGauge.Bar cfg, ForgeGaugeSource.Reading r)
        {
            if (cfg == null || !r.has)
            {
                if (_bar != null)
                    _bar.Hide();

                return;
            }

            if (_bar == null || !_bar.Alive)
            {
                _bar = ForgeGaugeWidget.MakeBar(_ship, cfg);
                _barCfg = cfg;

                if (_bar == null)
                    return;

                ForgeGaugePatch.SayLive(cfg.fileName);
            }
            else if (!ReferenceEquals(_barCfg, cfg))
            {
                // A different weapon's block is driving it now, so its colour,
                // size and position apply.
                _barCfg = cfg;
                _bar.Place(cfg);
            }

            if (Visible(r, cfg.hideAfter))
                _bar.Show(r.value, cfg);
            else
                _bar.Hide();
        }

        private void DriveBubble(
            ForgeGauge.Bubble cfg, ForgeGaugeSource.Reading r)
        {
            if (cfg == null || !r.has)
            {
                if (_bubble != null)
                    _bubble.Set(0, _bubbleCfg ?? cfg);

                return;
            }

            if (_bubble == null || !_bubble.Alive)
            {
                _bubble = ForgeGaugeWidget.MakeBubble(_ship, cfg);
                _bubbleCfg = cfg;

                if (_bubble == null)
                    return;
            }
            else if (!ReferenceEquals(_bubbleCfg, cfg))
            {
                _bubbleCfg = cfg;
                _bubble.Place(cfg);
            }

            _bubble.Set(Level(r, cfg), cfg);
        }

        // Driven, urgent, or still inside the fade window after firing. THE
        // LOCKOUT EXEMPTION IS THE POINT: during a vent or a jam you cannot
        // fire, so the timer would expire and hide the readout in the one state
        // where it is the only thing telling you when you get the gun back.
        private bool Visible(ForgeGaugeSource.Reading r, float hideAfter)
        {
            if (r.driven || r.urgent)
                return true;

            return Time.time - _lastDrivenAt <= Mathf.Max(0f, hideAfter);
        }

        private int Level(ForgeGaugeSource.Reading r, ForgeGauge.Bubble cfg)
        {
            // A lockout is always FAST, whatever the thresholds say. It is the
            // one state where the message is "now".
            if (r.urgent)
                return 2;

            if (!Visible(r, cfg.hideAfter))
                return 0;

            if (r.value >= cfg.fastAt) return 2;
            if (r.value >= cfg.slowAt) return 1;

            return 0;
        }
    }
}
