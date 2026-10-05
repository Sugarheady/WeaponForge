using System.Collections.Generic;
using UnityEngine;

namespace WeaponForge
{
    // `gauge` and `bubble` - two readouts that hang off your ship and tell you
    // what a weapon is doing.
    //
    // They answer DIFFERENT QUESTIONS, which is why they are two blocks and not
    // one with a style switch:
    //
    //     gauge   a QUANTITY.  "I am 70% hot."        a filling bar
    //     bubble  a STATE.     "you are locked out."  a flashing icon
    //
    // A weapon can have either, both or neither.
    //
    // BOTH ARE BORROWED FROM THE GAME AND NEITHER IS STOLEN. The ship already
    // carries a horizontal fill bar (the hold-to-self-destruct meter) and a
    // two-speed flashing icon (the low-fuel warning), both on its own
    // world-space canvas. We CLONE those objects rather than driving the
    // originals - see ForgeGaugeWidget for why that matters and what it buys.
    //
    // > THE GAUGE CANNOT KILL YOU. The self-destruct DEATH lives in
    // > `Ship.Update` and needs three things at once - out of fuel, holding the
    // > key, and `Ship`'s own private counter reaching 1. The widget we copy is
    // > two lines that set an animator bool and a fill amount; it has no
    // > reference to health at all. We never patch `Ship.Update`, never write
    // > `Ship.selfDestructProgress`, and never touch `Ship.selfDestructVisual`.
    // > The real self-destruct is untouched and still works.
    public static class ForgeGauge
    {
        // What a readout can be pointed at. All three are keyed by `Shooter`,
        // which is the ONLY thing they have in common - `overheat` and `spinUp`
        // are MonoBehaviours on the shooter's GameObject, `charge` is an entry
        // in a dictionary. A readout that found its sources by scanning
        // components would silently never see a charge.
        // `Surge` is the overload WINDOW rather than a meter of its own - it
        // reads the same heat, and it exists as a separate source for one
        // reason: **a heat bar filling and a heat bar draining look identical**,
        // so `Heat` alone cannot say which phase you are in, which is the only
        // thing that matters once a surge exists.
        public enum Source { Heat, Charge, SpinUp, Surge }

        // How a bubble shows itself.
        //   Pulse  - the game's own two-speed flash, via its Animator.
        //   Steady - on, not flashing. The Animator has no state for this, so
        //            ForgeGaugeWidget switches it off and drives the object by
        //            hand.
        public enum Mode { Pulse, Steady }

        // Defaults shared by both blocks. `charge` is NOT here on purpose: it
        // already has the barrel orb, so its bar and bubble are opt-in and have
        // to be named. His call, and it is what makes the priority list safe -
        // a hot gun keeps the bar, and charge still has a readout of its own to
        // fall back on.
        // `Surge` goes FIRST, and it costs nothing on a weapon without one: its
        // reading reports `has: false` unless the window is actually open, so
        // every file already written behaves exactly as it did. On a surge
        // weapon it means the bar switches to the window the moment it opens,
        // which is the answer to "am I filling or am I paying out".
        public static readonly Source[] DefaultSources =
        {
            Source.Surge,
            Source.Heat,
            Source.SpinUp,
        };

        public class Bar
        {
            // In priority order. The first source with something to show wins,
            // and the rest are silent - one bar, one answer.
            public Source[] show = DefaultSources;

            public float threshold = 0.25f;   // below this it is not worth a bar

            // Seconds after you stop firing before it fades, even while still
            // above the threshold. His ask: "have a fade time for hide the bar
            // if you havent fired for a certain amount of time."
            //
            // A LOCKOUT IGNORES THIS. During a vent or a jam you cannot fire,
            // so the timer would hide the bar in the one state where it is the
            // only thing telling you when you get the gun back.
            public float hideAfter = 3f;

            public Color color = new Color(1f, 0.1827f, 0f);   // the bar's own
            public bool hasHotColor;
            public Color hotColor = Color.white;

            public float scale = 1f;
            public Vector2 offset = new Vector2(0f, 0f);

            // SILENT UNLESS ASKED. His rule: "Default for overheat,
            // chargeshot, spinup-windup is no sound for them." Both hold a
            // resolved Sfx guid or null.
            //
            // `sound` is a one-shot as the bar comes up; `loopSound` is held
            // for as long as it is on screen and stopped by handle when it
            // goes. That pair is not invented - it is exactly what the widget
            // we cloned does: `Ship.Update` plays `selfDestructStartSfx` once
            // on the frame the meter appears and holds `selfDestructLoopSfx`
            // by handle until it stops.
            public string sound;
            public string loopSound;

            [System.NonSerialized] public string fileName;
        }

        public class Bubble
        {
            public Source[] show = DefaultSources;

            // Below `slowAt` it is hidden; between the two it pulses slowly;
            // at or above `fastAt` it pulses fast.
            public float slowAt = 0.5f;
            public float fastAt = 0.85f;

            public Mode mode = Mode.Pulse;

            // Same meaning as the bar's, and separate so a bubble can linger
            // longer than a gauge (or the other way round) without one block
            // reaching into the other. A lockout ignores it, as there.
            public float hideAfter = 3f;

            public string sprite;             // null keeps the game's warning
            public bool hasColor;
            public Color color = Color.white;

            public float scale = 1f;
            public Vector2 offset = new Vector2(1.38f, 1.44f);

            // One per pulse speed, both silent by default.
            //
            // THE BUBBLE IS THE ONE THAT ALREADY HAD A SOUND, and it was the
            // WRONG one. The low-fuel widget we clone carries a
            // `FeedbackAnimEventPlayer`, and both of its flash clips hold an
            // AnimationEvent at time 0 calling `PlayFeedback` with "Low" /
            // "Critical" - which the component maps to `Ship/FuelLow` and
            // `Ship/FuelCritical`. Both clips LOOP, so a cloned bubble
            // inherited the fuel alarm on every flash: every 2.33s while
            // pulsing slow and every 0.72s while pulsing fast.
            //
            // So these two keys are not only a feature, they are the steering
            // wheel on a thing that was already making noise. Empty means the
            // inherited sound is cleared, which is the default.
            public string slowSound;
            public string fastSound;

            [System.NonSerialized] public Sprite art;
            [System.NonSerialized] public string fileName;
        }

        // How the global switch in the BepInEx config relates to the files.
        public enum Global { Files, On, Off }

        public static Global global = Global.Files;

        private static readonly Dictionary<WeaponData, Bar> _bars =
            new Dictionary<WeaponData, Bar>();

        private static readonly Dictionary<WeaponData, Bubble> _bubbles =
            new Dictionary<WeaponData, Bubble>();

        public static void Register(WeaponData weapon, Bar bar)
        {
            if (weapon != null && bar != null)
                _bars[weapon] = bar;
        }

        public static void Register(WeaponData weapon, Bubble bubble)
        {
            if (weapon != null && bubble != null)
                _bubbles[weapon] = bubble;
        }

        // `Global.On` hands back a default block for a weapon that never asked
        // for one, so "show me everything" needs no edits to any file.
        public static bool TryGetBar(WeaponData weapon, out Bar bar)
        {
            bar = null;

            if (global == Global.Off || weapon == null)
                return false;

            if (_bars.TryGetValue(weapon, out bar))
                return true;

            if (global != Global.On)
                return false;

            bar = _forcedBar;

            return true;
        }

        public static bool TryGetBubble(WeaponData weapon, out Bubble bubble)
        {
            bubble = null;

            if (global == Global.Off || weapon == null)
                return false;

            return _bubbles.TryGetValue(weapon, out bubble);
        }

        private static readonly Bar _forcedBar = new Bar { fileName = "(config)" };

        // Cheap gate for the per-frame driver: with neither block anywhere and
        // the global switch left alone, this whole feature costs one bool read
        // per ship per frame.
        public static bool Any
        {
            get
            {
                return global == Global.On ||
                       (global != Global.Off &&
                        (_bars.Count > 0 || _bubbles.Count > 0));
            }
        }

        // Build-time registries keyed by a weapon ASSET, so - like ForgeCrit's
        // and ForgeWingman's - they are deliberately NOT cleared at run entry.
        // Clearing them would unbuild every readout for the rest of the
        // session.
    }
}
