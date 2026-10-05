using UnityEngine;

namespace WeaponForge
{
    // "Given this gun, is anything worth putting on screen?"
    //
    // THE THREE SOURCES ARE NOT THE SAME SHAPE, and that is the whole reason
    // this file exists rather than an interface:
    //
    //     overheat   ForgeHeat  : MonoBehaviour on shooter.gameObject
    //     spinUp     ForgeSpin  : MonoBehaviour on shooter.gameObject
    //     charge     an entry in a Dictionary<Shooter, State> inside
    //                ForgeChargePatch - NOT a component at all
    //
    // A readout that found its sources with GetComponents<> would work
    // perfectly for two of them and silently never see the third. What they
    // actually share is that all three are keyed by `Shooter`, so that is what
    // this asks about.
    public static class ForgeGaugeSource
    {
        // What one source has to say right now.
        public struct Reading
        {
            public bool has;        // this gun has this feature at all
            public float value;     // 0..1, what the bar should show
            public bool driven;     // being actively worked right now
            public bool urgent;     // a lockout: hold the bar, flash fast
        }

        public static Reading Read(Shooter shooter, ForgeGauge.Source source)
        {
            var r = new Reading();

            if (shooter == null)
                return r;

            switch (source)
            {
                case ForgeGauge.Source.Charge: return Charge(shooter);
                case ForgeGauge.Source.SpinUp: return SpinUp(shooter);
                case ForgeGauge.Source.Surge: return Surge(shooter);
                default: return Heat(shooter);
            }
        }

        // ------------------------------------------------------------------

        private static Reading Heat(Shooter shooter)
        {
            var r = new Reading();

            ForgeHeat heat = shooter.GetComponent<ForgeHeat>();

            if (heat == null)
                return r;

            r.has = true;
            r.urgent = heat.Locked;

            // DURING A LOCKOUT THE BAR IS A COUNTDOWN, NOT A HEAT LEVEL. His
            // ask: "Maybe the bar can empty till you can fire again." That is
            // one behaviour with TWO sources - see ForgeHeat.Recovery, where
            // the vent and jam cases are genuinely different numbers.
            r.value = heat.Recovery();

            // Not "driven" while locked: you cannot fire, and `urgent` is what
            // keeps it on screen instead.
            r.driven = !heat.Locked && shooter.IsShooting;

            return r;
        }

        // THE OVERLOAD WINDOW, and it reports `has: false` when the window is
        // shut rather than reporting a zero.
        //
        // That distinction is the whole reason this source is usable as a
        // default: `has` is what the readout tests to decide whether a source
        // has anything to say, so a closed window falls through to `Heat` and
        // the bar behaves exactly as it always did. A source that answered
        // "yes, 0%" would swallow the heat bar on every weapon in the game that
        // happens to carry a surge block.
        private static Reading Surge(Shooter shooter)
        {
            var r = new Reading();

            ForgeHeat heat = shooter.GetComponent<ForgeHeat>();

            if (heat == null || !heat.Surging)
                return r;

            r.has = true;

            // Already has the ramp shape applied, so a `flat` surge shows a
            // full bar for the whole window and a `fade` one drains with the
            // reward.
            r.value = heat.SurgeAmount();

            // DRIVEN WHETHER OR NOT YOU ARE FIRING. The window is running down
            // on its own clock, and `hideAfter` would otherwise take the bar
            // away in the one phase where a countdown is the point.
            r.driven = true;

            return r;
        }

        private static Reading SpinUp(Shooter shooter)
        {
            var r = new Reading();

            ForgeSpin spin = shooter.GetComponent<ForgeSpin>();

            if (spin == null)
                return r;

            r.has = true;

            // Already public, and already has the ramp shape applied - so a
            // STEPPED wind-up reads as steps on the bar rather than as a smooth
            // slide that disagrees with the gun.
            r.value = spin.Fraction();
            r.driven = shooter.IsShooting;

            return r;
        }

        private static Reading Charge(Shooter shooter)
        {
            var r = new Reading();

            float level;

            if (!ForgeChargePatch.TryHeldLevel(shooter, out level))
                return r;

            r.has = true;
            r.value = level;

            // For a charge, "being driven" is HOLDING, not firing. Reading the
            // rule literally as "while firing" would hide the charge readout
            // during exactly the charge it exists to show.
            r.driven = shooter.IsShooting;

            return r;
        }
    }
}
