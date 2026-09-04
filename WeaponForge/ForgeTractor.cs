using UnityEngine;

namespace WeaponForge
{
    // A held beam that MOVES things instead of damaging them.
    //
    // Distinct from the gravity well the way a beam is distinct from a grenade:
    // aimed and continuous rather than thrown and placed. The force model is the
    // gravity well's, unchanged - including the mass x damping compensation,
    // without which the same number moves the ship 60x faster than a common
    // enemy and 1000x faster than the Queen.
    public static class ForgeTractor
    {
        public enum Mode
        {
            Pull,    // drag toward you
            Push,    // shove away
            Hold,    // park it at holdDistance in front of you
            Grapple  // the beam anchors and pulls YOU instead
        }

        public class Config
        {
            public Mode mode = Mode.Pull;

            // ---- the beam ------------------------------------------------
            public float range = 14f;

            // Half-angle of the catch cone, in degrees. **0 IS A LINE** - the
            // beam then catches only what it is pointed at, within beamWidth of
            // the ray. Anything above 0 widens with distance, which is far more
            // forgiving to aim and catches a group.
            public float cone;

            // Line mode only: how far off the ray still counts, in world units.
            public float beamWidth = 0.6f;

            public float force = 300f;

            // Where a Hold beam parks what it catches.
            public float holdDistance = 6f;

            // How RIGIDLY a Hold beam pins what it caught to that spot.
            //
            // The first version was a plain force plus a fixed 0.92 velocity
            // bleed, which is a weak spring: heavy things lagged behind the
            // crosshair and light ones wallowed, so lining an enemy up with a
            // wall was "rough". This is a proper critically-damped spring
            // instead - 0 keeps the old soft behaviour, 1 snaps the target to
            // the aim point hard enough to steer with.
            public float holdStiffness = 0.65f;

            // Speed clamp, so a light enemy is not flung across the level.
            public float maxSpeed = 26f;

            // How many things the beam may hold at once. 0 = no limit.
            public int maxTargets = 3;

            // Drain per second while held, from the weapon's own resource - the
            // same bargain the orbit ring makes, and for the same reason: a held
            // beam never fires a shot, so the weapon's `cost` is never taken.
            public float drainPerSecond = 2f;

            public bool affectsPlayer;   // catch the player too (co-op, or self)

            // ---- grapple -------------------------------------------------
            //
            // The beam anchors on what it is pointed at and reels YOU in rather
            // than the target. Terrain is the natural anchor - a wall does not
            // move - so this is a raycast rather than the overlap sweep the
            // other modes use.

            // Also anchor on units, not just terrain. A heavy enemy makes a
            // fine anchor; a light one mostly gets dragged, which is why this
            // is off by default and terrain is the reliable version.
            public bool grappleToUnits;

            // Let a grapple hurt YOU when you arrive at speed.
            //
            // OFF by default deliberately: this is the switch that completes
            // player slam damage, and it is also the switch that makes a
            // grapple frustrating. Nothing else in the game hurts you for
            // flying into a wall unless you are sprinting.
            public bool grappleSlamsYou;

            // Stop reeling in once this close, so you arrive at the wall rather
            // than grinding along it.
            public float grappleStopDistance = 2f;

            // Say in the log, once, what the grapple ray actually did. On by
            // default while the mode is young: "it didn't pull my ship" has
            // several possible causes and none of them are visible in game.
            public bool grappleDebug = true;

            // ---- the slam ------------------------------------------------
            public bool slam = true;

            // 10 is the game's own idea of a hard crash: every ship prefab
            // carries ImpactDamage at minVelocity 10.
            public float slamMinSpeed = 10f;

            public float slamDamagePerSpeed = 1.5f;
            public float slamMaxDamage = 999f;

            // How long after the beam lets go a unit stays slammable. Short on
            // purpose - the point is "you threw it", not "it is now permanently
            // made of glass".
            public float slamWindow = 1.5f;

            public bool slamHitsUnits = true;    // enemy into enemy
            public bool slamHitsPlayer;          // ...and into YOUR hull

            // The tell - a colour worn by whatever is currently slammable, so
            // you can see who is worth throwing. Same keys and same meaning as
            // the standalone `slam` block; both go through ForgeUnitTint, so a
            // tractor tint and the `slow` tint compose rather than fight.
            public Color slamTint = Color.white;
            public bool slamHasTint;
            public float slamTintPulse = 3f;

            // See ForgeUnitTint - 0..1, 1 is the full colour.
            public float slamTintStrength = 1f;

            // ---- look ----------------------------------------------------
            public Color color = new Color(0.6f, 0.9f, 1f, 1f);
            public bool drawBeam = true;
            public string sfx = "";

            [System.NonSerialized] public string sfxGuid;

            public bool IsLine { get { return cone <= 0f; } }
        }

        private static readonly System.Collections.Generic.Dictionary<
            WeaponData, Config> _weapons =
            new System.Collections.Generic.Dictionary<WeaponData, Config>();

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

        public static bool Any { get { return _weapons.Count > 0; } }
    }
}
