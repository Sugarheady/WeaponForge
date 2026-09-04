using System;
using System.Collections.Generic;
using UnityEngine;

namespace WeaponForge
{
    // A held shield ARC near the ship, and a pointed RAM version of the same
    // thing that hurts what you charge into.
    //
    // This is a genuinely new category for the mod - the first weapon that is
    // not a thing you throw. Two facts from the game made the design:
    //
    // 1. The game's own `Shield` is a damage FILTER, not an obstacle. It has no
    //    collider at all on the player (all four ship prefabs ship
    //    shieldHasCollider: 0), its shader draws a full ring with no angle
    //    parameter, and it lives on layer Default which EnemyProjectiles does
    //    not even collide with. So none of it could be reused for a wedge. What
    //    IS worth stealing is its pricing idea: `effectiveness` is damage
    //    absorbed per point of resource.
    //
    // 2. NOTHING in PUNK does unit-to-unit contact damage. There is no melee
    //    component and no OnCollisionStay2D anywhere in the assembly. Flying
    //    into an enemy is free, in both directions. So the ram has to invent
    //    its damage, and "prevents contact damage to the player" has exactly
    //    one real referent - the ship's OWN ImpactDamage crash damage, which
    //    ShipMovement enables only while boosting.
    public static class ForgeShield
    {
        // What this weapon is.
        public enum Mode
        {
            Arc,   // catches incoming fire
            Ram,   // hurts what you charge into
            Both   // one weapon that does each when its moment comes
        }

        // When the arc is up.
        public enum Activation
        {
            Hold,    // while the trigger is down
            Toggle,  // tap to raise, tap to lower
            Always   // permanently, as long as you can pay for it
        }

        // Which way it points.
        public enum Facing
        {
            Aim,     // the crosshair - Aimer.TargetPosition, the RAW mouse
            Travel,  // the way the ship is actually moving
            Auto,    // aim while shielding, travel while ramming
            Frozen   // captured when it goes up (or when the dash starts)
        }

        // What happens to a shot the arc catches.
        public enum OnBlock
        {
            Destroy,          // it simply stops
            Reflect,          // straight back the way it came, as YOUR bullet
            ReflectAtNearest, // turned around and aimed at an enemy
            ReflectAtCursor,  // sent wherever YOU are pointing
            ReflectMirror,    // bounced off the arc, angle-of-incidence style
            Absorb            // destroyed, and refunds resource instead of costing it
        }

        // What sets the ram off. Dash and sprint are the same button: a tap is
        // ShipMovement.Dash(), a hold becomes a boost after boostStartDelay.
        public enum RamTrigger
        {
            Dash,   // the tap only - short, sharp, cooldown-gated
            Sprint, // the hold only - sustained while boosting
            Either  // both, each with its own damage multiplier
        }

        public class Config
        {
            public Mode mode = Mode.Arc;
            public Activation activation = Activation.Hold;
            public Facing facing = Facing.Auto;

            // ---- shape -------------------------------------------------
            // Degrees of arc. His original ask was 40 - "an angle that would
            // circle the ship if it continued", so nine would close a ring.
            public float angle = 40f;

            // Distance from the ship to the middle of the arc.
            public float radius = 2.2f;

            // How deep the caught slice is. The catch volume is the whole
            // SECTOR out to radius + thickness, not a shell - see
            // ForgeProjectileTracker.CollectInSector for why.
            public float thickness = 0.6f;

            // ---- blocking ----------------------------------------------
            public OnBlock onBlock = OnBlock.Reflect;
            public float reflectDamage = 1f;

            // Set when the file has a "rally" block. Shared by all three
            // reflectors, so one block covers whichever you use.
            [NonSerialized] public ForgeRally.Config rally;
            public float reflectSpeed = 1f;
            public float aimRange = 25f;

            // Enemy lasers. Costs a patch on HitscanWeapon rather than a
            // sweep, because a beam is a raycast that resolves in one frame -
            // there is no projectile to catch. Both the damage cast and the
            // separate visual cast have to be clipped or the beam draws
            // straight through a shield that is stopping it.
            public bool blockBeams = true;

            // Shots stopped per second. 0 = unlimited.
            public int maxPerSecond;

            // ---- what it costs -----------------------------------------
            // Held upkeep, resource per second. `"cost"` in JSON resolves to
            // Cost * FireRate - what this weapon would spend per second with
            // the trigger simply held down.
            public float drainPerSecond;
            public bool drainFromCost;

            // Flat charge per shot stopped.
            public float costPerBlock;

            // The game's own Shield pricing: charge the incoming damage
            // divided by this. Higher = more efficient. 0 = off (use the flat
            // costPerBlock instead). Mirrors Shield.TakeDamage exactly.
            public float effectiveness;

            // Which tank pays. Empty = the weapon's own resourceUsed.
            public string resource = "";

            // ---- ram ---------------------------------------------------
            public RamTrigger ramTrigger = RamTrigger.Either;

            // Damage dealt to whatever is inside the wedge while ramming.
            public float ramDamage = 8f;

            // Separate multiplier for the sustained sprint version, so a tap
            // can hit hard and a sustained charge can chip.
            public float ramSprintMultiplier = 0.4f;

            // Shove applied to what you hit.
            public float ramKnockback = 30f;

            // Below this speed a ram does nothing, so drifting into something
            // is not an attack.
            public float ramSpeedThreshold = 6f;

            // Per-target, so one charge through a crowd hits each of them once
            // rather than every enemy every frame.
            public float ramRepeatDelay = 0.4f;

            // Suppress the ship's own ImpactDamage while ramming. That
            // component is self-damage and ShipMovement turns it on whenever
            // you boost, so without this a sprint-ram costs you health every
            // time it lands.
            public bool ramSelfProtect = true;

            // Seconds of full invulnerability from the moment a ram starts,
            // via DamagableResource.damageBlockers. 0 = none. This blocks ALL
            // damage, not just impacts, so keep it short.
            public float ramIFrames;

            // How long a dash counts as a ram. 0 = until ShipMovement says the
            // dash ended.
            public float ramDashWindow;

            // ---- look and sound ----------------------------------------
            public Color color = new Color(0.45f, 0.85f, 1f, 0.85f);
            public string sprite = "";
            public string blockSfx = "";
            public string ramSfx = "";
            public string raiseSfx = "";

            // Fade the arc in rather than popping it, like orbit's appearFade.
            public float appearFade = 0.12f;

            // Flash the arc on a block.
            public bool flashOnBlock = true;

            // ---- resolved at build time --------------------------------
            [System.NonSerialized] public Sprite art;

            // Opt-in animation - see ForgeSpriteLibrary.OptInFrames. Only a
            // NAMED sprite can animate; the generated arc is one built shape.
            [System.NonSerialized] public Sprite[] artFrames;
            [System.NonSerialized] public float artFps;
            [System.NonSerialized] public Material material;
            [System.NonSerialized] public int sortingLayerId;
            [System.NonSerialized] public int sortingOrder;
            [System.NonSerialized] public Resource resourceAsset;

            public bool WantsArc
            {
                get { return mode == Mode.Arc || mode == Mode.Both; }
            }

            public bool WantsRam
            {
                get { return mode == Mode.Ram || mode == Mode.Both; }
            }

            public bool ReactsToDash
            {
                get
                {
                    return ramTrigger == RamTrigger.Dash ||
                           ramTrigger == RamTrigger.Either;
                }
            }

            public bool ReactsToSprint
            {
                get
                {
                    return ramTrigger == RamTrigger.Sprint ||
                           ramTrigger == RamTrigger.Either;
                }
            }

            // Outer edge of the catch sector - and of the drawn band, which is
            // the point. `radius` is the band's CENTRE line, so the outer edge
            // is half a thickness beyond it. Getting this wrong by the full
            // thickness would stop beams half a unit in front of a shield that
            // is visibly not there yet.
            public float Reach
            {
                get
                {
                    return Mathf.Max(
                        0.1f, radius + Mathf.Max(0f, thickness) * 0.5f);
                }
            }

            public float HalfAngle
            {
                get { return Mathf.Clamp(angle, 1f, 360f) * 0.5f; }
            }
        }

        // Keyed on the template WeaponData, the same way every other Forge
        // feature registers itself, so a Shooter can look up "is the weapon in
        // my hands a shield" once per frame without touching the JSON again.
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

        public static bool Any { get { return _weapons.Count > 0; } }
    }
}
