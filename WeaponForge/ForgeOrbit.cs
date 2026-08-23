using System.Collections.Generic;
using UnityEngine;

namespace WeaponForge
{
    // Registry + config for "orbit" weapons: projectiles that circle the
    // player instead of flying outward. A weapon is an orbit weapon when
    // its JSON sets "orbit": true. This is a fully custom behaviour (the
    // game has no orbital mechanic), driven by ForgeOrbitPatch + rendered/
    // damaged by ForgeOrbitController.
    public static class ForgeOrbit
    {
        public enum Mode { Passive, Hold, Toggle, Fire }
        public enum RegenMode { Both, Timer, Fire }

        // How the orbs travel outward instead of holding a fixed ring:
        //   Off    - classic fixed-radius ring (default).
        //   Launch - orbs spiral out from the ship, then detach at the outer
        //            radius and fly straight off toward enemies (they don't
        //            come back), respawning from the center. A "vortex cannon".
        //   Sweep  - orbs spiral out to the outer radius and reset to the
        //            center, over and over - a continuous rotating-sprinkler
        //            sweep. They never leave, they just don't orbit back.
        public enum SpiralMode { Off, Launch, Sweep }

        // How an orb changes SIZE while the ring runs. Growth is a real
        // mechanic, not decoration: the contact radius scales with the art
        // (unless growHitbox is off), so a big orb sweeps a wider path.
        //   Off     - orbs stay their normal size (default).
        //   Charge  - grows while the ring is ACTIVE, over growSpan seconds,
        //             then holds at full. Hold/Fire/Toggle rings reset when
        //             they switch off, so it reads as a wind-up. On a Passive
        //             ring it simply grows once at the start of the run.
        //   OrbLife - each orb grows from the moment IT spawns, so a
        //             destructible ring regenerates small orbs that swell
        //             back. Only interesting with destroyOnEnemy/Terrain.
        //   Breathe - swells and shrinks forever between from and to. The
        //             same idea as pulseAmount, but on the orb's size rather
        //             than the ring's radius.
        public enum GrowMode { Off, Charge, OrbLife, Breathe }

        public class Config
        {
            public Mode mode = Mode.Passive;
            public bool clockwise = true;

            // Ring shape / motion.
            public float radius = 3f;         // circle size ("range" / outer)
            public float speed = 120f;        // rotation, degrees/second
            public float hitRadius = 0.6f;    // contact size per orb

            // Concentric rings. rings=1 is the classic single ring.
            //   ringsFullCount false -> the weapon's projectileCount is SPLIT
            //     across the rings (round-robin, so they stay even).
            //   ringsFullCount true  -> EVERY ring gets the full count, so
            //     the orb total is projectileCount * rings.
            public int rings = 1;
            public float ringSpacing = 1.5f;  // gap between one ring and the next
            public bool ringsFullCount;
            public float ringStagger = -1f;   // <0 = auto half-step; else deg per ring
            public bool ringAlternate;        // flip spin direction every other ring
            public float ringSpeedStep = 1f;  // speed multiplier per ring outward

            // Spiral-outward behaviour (see SpiralMode above). When on,
            // "radius" is the OUTER edge and pulse/fling are ignored.
            public SpiralMode spiral = SpiralMode.Off;
            public float spiralInner = 0.4f;      // radius the spiral starts at
            public float spiralOutTime = 0.8f;    // seconds inner -> outer
            public float spiralLaunchSpeed = 12f; // fly-off speed (Launch)
            public float spiralKillDistance = 14f;// remove launched orb past this

            // What the orbs do.
            // weaponEffects: pass the weapon's FULL hit through - burn, the
            // got-attacked/aggro event, kill credit, plus its explosion and
            // discharge. Off = plain contact damage only.
            public bool weaponEffects = true;
            public bool contactDamage = true;
            public float damageRepeatDelay = 0.3f;  // per enemy
            public bool blockProjectiles;     // destroy enemy shots touched
            public float pushForce;           // shove enemies (0 = off)

            // Orb growth (see GrowMode above). Sizes are MULTIPLIERS of the
            // orb art's normal size, so 1 is unchanged and this composes with
            // whatever visual you picked. from > to shrinks instead.
            public GrowMode grow = GrowMode.Off;
            public float growFrom = 0.5f;
            public float growTo = 3f;
            public float growSpan = 4f;          // seconds from -> to (Charge/OrbLife)
            public float growBreatheSpeed = 1f;  // cycles per second (Breathe)
            public float growCurve = 1f;         // 1 = linear; >1 slow then fast
            public bool growHitbox = true;       // contact radius follows the art
            // 0 = size changes reach only. Otherwise the damage multiplier at
            // FULL size, ramped from 1x at the smallest - so 2 means a
            // fully-grown orb hits twice as hard.
            public float growDamageAtFull;

            // Extras.
            public float pulseAmount;         // radius breathe amplitude
            public float pulseSpeed = 1f;
            public float spinUpSeconds;       // ramp to full spin
            public bool fling;                // fire = surge outward + back
            public float flingReach = 4f;     // extra radius during a fling
            public float flingDuration = 0.35f;

            // Terrain. Orbs are not real projectiles - they have no
            // collision layer of their own - so digging is opt-in and routed
            // through the level's own projectile listener, exactly like a
            // normal shot hitting a wall (cell damage, shake, burn).
            public bool damageTerrain;
            public float terrainRepeatDelay = 0.15f;  // per orb, sec per cell

            // Destructible orbs + regeneration.
            public bool destroyOnEnemy;       // orb dies when it hits an enemy
            public bool destroyOnTerrain;     // orb dies when it hits terrain
            public RegenMode regenMode = RegenMode.Both;
            public float regenTime = 3f;      // seconds to respawn (Timer/Both)
            public bool popExplosion;         // dead orb bursts (AoE damage)
            public float popRadius = 1.5f;

            // Letting go of the ring.
            //
            // Without these the orbs simply blink out of existence the frame
            // the trigger comes up, which reads as a glitch rather than as
            // dismissing something.
            //
            //   releaseFade   seconds the orbs keep orbiting while fading out
            //                 after the ring switches off. 0 = the old instant
            //                 pop. Also covers a Fire ring reaching the end of
            //                 its fireDuration.
            //   releaseShrink shrink them away as they fade, so a big charged
            //                 ring visibly collapses instead of just dimming.
            //   releaseDamage whether dissolving orbs still hurt. OFF by
            //                 default on purpose: a ring that damaged on the
            //                 way out would make tap-release-tap strictly
            //                 better than holding, which is not a weapon,
            //                 it is an exploit.
            public float releaseFade = 0.3f;
            public bool releaseShrink = true;
            public bool releaseDamage;

            // Bringing the ring UP - the mirror of the three above.
            //
            //   appearFade    seconds the orbs spend fading in as the ring
            //                 switches on. 0 (the DEFAULT) is the old instant
            //                 appearance, deliberately: unlike the release
            //                 pop, nobody has complained about this one, and
            //                 turning it on by default would quietly change
            //                 the feel of every orbit weapon already tuned.
            //   appearGrow    swell them out of nothing as they fade in,
            //                 rather than only brightening.
            //   appearDamage  whether orbs hurt while they are still coming
            //                 up. OFF by default, and for the same reason
            //                 releaseDamage is: with both halves harmless, a
            //                 tap costs you the whole wind-up and holding is
            //                 strictly better. That is the actual fix for a
            //                 ring being spammable, which no amount of
            //                 warmupTime can do (see ForgeOrbitPatch - a held
            //                 ring never goes through the ship's fire path at
            //                 all, so nothing the weapon gates on ever runs).
            public float appearFade;
            public bool appearGrow = true;
            public bool appearDamage;

            // Restart growth every time the ring switches on, so each press
            // rebuilds the charge.
            //
            // Charge mode already did this (it counts from activeSince, which
            // resets); OrbLife counts from each orb's own birth and the orbs
            // are only HIDDEN when the ring goes off, never destroyed - so
            // they came back instantly full size. That mismatch is the bug
            // this fixes, and it makes both modes match what the GrowMode
            // docs above already promise.
            public bool growReset = true;

            // Activation costs / timing.
            public bool suppressFire = true;  // don't also shoot forward

            // What a HELD or TOGGLED ring costs to keep up, per second.
            //
            // The weapon's own `cost` cannot do this job: the ship charges you
            // when it FIRES, and both of those modes suppress firing entirely,
            // so there is no shot to attach a price to. This is the only lever.
            //
            // holdDrainFromCost derives the rate from the weapon instead of a
            // flat number: cost x fireRate is exactly what a normal weapon
            // spends per second while you hold its trigger down, so the ring
            // ends up costing what the weapon says it costs, and it keeps
            // tracking if a module changes either number.
            public float holdDrainPerSecond;
            public bool holdDrainFromCost;
            public float fireDuration = 3f;   // Fire mode ring lifetime

            // Visual (a Projectile prefab used purely as art).
            public GameObject visualPrefab;
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
    }
}
