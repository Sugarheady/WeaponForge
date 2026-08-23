using System.Collections.Generic;
using UnityEngine;

namespace WeaponForge
{
    // Registry + config for "charge" weapons: hold the trigger to build a
    // shot up, let go to fire it. The game has no concept of this - the only
    // thing it does with a held trigger is WeaponBase.WarmupTime, which is a
    // minigun SPIN-UP (it gates firing until the meter is full and then fires
    // normally, forever, and resets the instant you let go). Nothing anywhere
    // reacts to the trigger coming UP, and no weapon stat is ever scaled by
    // how long you held.
    //
    // So all of it is ours: ForgeChargePatch runs the meter off Shooter.Update,
    // suppresses the normal fire, and fires one scaled volley on release.
    //
    // Two shapes, because PUNK's primary fire is normally held down and a
    // pure hold-to-charge weapon changes how the whole gun plays:
    //
    //   Release  hold does nothing but charge; letting go fires one big shot.
    //            The Mega Man buster. Default.
    //   Auto     the weapon keeps firing normally while you hold, and the
    //            charge builds alongside it - every time the meter fills, ONE
    //            shot comes out charged and the meter starts again. Costs you
    //            nothing to use and works on a beam, which Release does not.
    public static class ForgeCharge
    {
        public enum Mode { Release, Auto }

        public class Config
        {
            public Mode mode = Mode.Release;

            // Seconds of holding to reach full charge.
            public float time = 1f;

            // Fraction of a full charge a release needs before it counts as a
            // charged shot at all. Below it you get "tapFire" (a normal shot)
            // or nothing.
            public float min = 0.3f;

            // A quick tap fires the weapon normally. Without this a charge
            // weapon simply does nothing when tapped, which reads as broken
            // rather than as a design.
            public bool tapFire = true;

            // Release mode only: don't let the weapon fire its normal shots
            // while you are holding. Off turns the weapon into "fires as
            // usual, plus a bonus shot when you let go".
            public bool holdFire = true;

            // 1 = the meter fills steadily. >1 = slow at first then a rush at
            // the end, so the last part of the hold is where the payoff is.
            public float curve = 1f;

            // Whether a partly-charged shot gets a partial bonus (default), or
            // whether anything at or past "min" gets the FULL bonus.
            public bool scale = true;

            // --- what full charge does ------------------------------------
            // Multipliers on the weapon's own numbers. 1 = unchanged.
            public float damage = 3f;
            public float size = 1.8f;
            public float speed = 1f;
            public float range = 1f;
            public float knockback = 1f;
            public float push = 1f;
            public float explosion = 1f;   // radius AND explosion damage
            public float burn = 1f;
            public float spread = 1f;
            public float cost = 1f;        // what the charged shot costs

            // Added, not multiplied.
            public int count;              // extra pellets at full charge
            public int pierce;             // extra enemies it passes through

            // --- feedback --------------------------------------------------
            // A charged shot is worth telling the player about, and none of
            // this happens for free: the game tints nothing, plays nothing on
            // release, and draws no meter.
            public bool hasColor;
            public Color color = Color.white;

            // The growing orb at the barrel. Defaults to the weapon's own
            // bullet art, so it reads as the shot you are building.
            public bool meter = true;
            public string meterSprite;
            public float meterFrom = 0.2f;
            public float meterTo = 1.1f;
            public bool hasMeterColor;
            public Color meterColor = Color.white;

            // Resolved at BUILD time, not per frame: the art the orb draws
            // with, plus the material and sorting slot copied off the
            // weapon's own bullet. Sorting is the part that matters - get it
            // wrong and the orb renders behind the ship or behind the level,
            // and "the meter does not work" is indistinguishable from "the
            // meter is drawing in the wrong layer".
            public Sprite meterArt;
            public Material meterMaterial;
            public int meterSortingLayerId;
            public int meterSortingOrder;

            // Played once the instant the meter fills. The looping CHARGING
            // sound is the weapon's own "warmupSfx" slot - Shooter already
            // owns starting and stopping it by handle, so we drive that
            // instead of inventing a second held-sound path.
            public string readySfx;
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

        public static bool Any
        {
            get { return _weapons.Count > 0; }
        }

        // How far along a hold is, 0..1, with the curve applied. Clamped: the
        // meter fills and then waits, it does not overcharge.
        public static float Level(float heldSeconds, Config cfg)
        {
            if (cfg == null || cfg.time <= 0f)
                return 1f;

            float t = Mathf.Clamp01(heldSeconds / cfg.time);

            return (cfg.curve == 1f) ? t : Mathf.Pow(t, cfg.curve);
        }

        // A multiplier at charge level t. "scale: false" means anything that
        // counts as charged at all gets the whole bonus.
        public static float Mul(float atFull, float t, Config cfg)
        {
            if (atFull == 1f)
                return 1f;

            return cfg.scale
                ? Mathf.LerpUnclamped(1f, atFull, t)
                : atFull;
        }

        public static int Add(int atFull, float t, Config cfg)
        {
            if (atFull == 0)
                return 0;

            return cfg.scale
                ? Mathf.RoundToInt(atFull * t)
                : atFull;
        }
    }
}
