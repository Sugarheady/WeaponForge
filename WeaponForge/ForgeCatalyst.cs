using System;
using UnityEngine;

namespace WeaponForge
{
    // CATALYST - a node you shoot INTO, which passes the shot on harder.
    //
    // Fire one and it hangs in the air. Put another shot through it and that
    // shot is redirected at enemies with more damage - chained through several,
    // or split into one copy per target. Enemy fire that reaches the node is
    // turned around and sent back buffed. Either use consumes the node.
    //
    // The load-bearing fact, and the reason this needed its own detection:
    // **your own bullets cannot collide with each other.** PlayerProjectiles is
    // absent from its own row in the layer matrix, and a plain Projectile ships
    // with its Collider2D DISABLED because it resolves hits by CircleCast. So
    // there is no physics event for "my shot reached the node" at any radius -
    // it has to come from ForgeProjectileTracker, which is why `catalyst`
    // switches TrackPlayer on.
    public static class ForgeCatalyst
    {
        // What the node does once fired.
        public enum Node
        {
            Stop,    // halts and hovers - easiest to aim a second shot into
            Travel,  // keeps flying; you have to catch it in the air
            Drift,   // halts, then wanders slowly
            Toss     // ARCS UNDER GRAVITY and survives landing - the coin
        }

        // Which of your shots may set it off.
        public enum Trigger
        {
            Different,  // only a shot from a DIFFERENT weapon - a real combo
            Any         // any shot of yours, including this weapon's own
        }

        // What happens to the shot that goes in.
        public enum Redirect
        {
            Chain,  // one shot, leaping target to target
            Split   // becomes several shots, one per target, at once
        }

        public class Config
        {
            public Node node = Node.Stop;
            public Trigger trigger = Trigger.Different;
            public Redirect redirect = Redirect.Chain;

            // ---- the node ------------------------------------------------
            // Seconds of flight before a Stop/Drift node halts. It also halts
            // early if it hits something, so this is "how far you can place
            // it", not a delay you have to wait out.
            public float armTime = 0.35f;

            public float lifetime = 6f;
            public float radius = 1.2f;      // catch radius for shots
            public float drift = 0.6f;       // Drift only, units/second
            public int max = 3;              // nodes alive at once
            public int uses = 1;             // triggers before it vanishes

            // Toss only. Rigidbody gravityScale for the node - this is what
            // makes a tossed coin arc and fall rather than fly straight.
            // -999 leaves the template's own value alone.
            public float gravityScale = -999f;

            // Prefer another NODE over an enemy when aiming a redirected shot.
            //
            // This is the coin-to-coin ricochet: the shot leaps from coin to
            // coin, setting each off in turn, before finally going at an enemy.
            // It cannot loop, because a node is consumed the moment it fires.
            public bool preferNodes;

            // ---- the redirect --------------------------------------------
            public int targets = 3;          // chain jumps, or split copies
            public float range = 10f;        // how far to look for targets

            // Do crates and boxes count as things to redirect at?
            //
            // They are genuine AimAssistTargets in the prefabs and share the
            // enemies' own faction, so they always did - this only exists to
            // turn it OFF, for when a crate beside you keeps stealing shots
            // that should have gone at something shooting back.
            public bool targetObjects = true;

            // Does a redirected shot TRACK what it was pointed at?
            //
            // Without this the redirect is an aim, not a hit: the shot is
            // turned to where the target stood at that instant and then flies
            // dead straight, so anything strafing at range gets missed. "Auto
            // hit" was the whole point, so this is on by default.
            //
            // Plain projectiles only, and for the same reason chain is: it
            // works by granting the shot the mod's own ForgeHoming, which
            // steers Velocity. A lobbed shot is already handed the target
            // through its own `Target` field and tracks only as hard as the
            // weapon's own homingData lets it.
            public bool lockOn = true;
            public float lockOnTurnRate = 360f;
            public float damageMultiplier = 2f;
            public float speedMultiplier = 1.5f;

            // Chain only, passed straight through to the chain it grants.
            public float chainFalloff = 1f;  // per-jump damage multiplier
            public bool chainRevisit;

            // ---- enemy fire ----------------------------------------------
            public bool catchEnemyShots = true;
            public float reflectDamage = 2f;

            // Set when the file has a "rally" block. Shared by all three
            // reflectors, so one block covers whichever you use.
            [NonSerialized] public ForgeRally.Config rally;

            // Where a reflected shot is sent. `aimReflected` is the older
            // boolean and still works; this is the fuller answer.
            public int aimMode = ForgeBulletTurn.AimBack;
            public float reflectSpeed = 1.5f;
            public bool aimReflected = true;  // send it at an enemy, not just back

            // Does catching enemy fire spend a use? He asked for the node to
            // destroy itself on enemy bullets, so yes by default.
            public bool enemyShotsConsume = true;

            // ---- look and sound ------------------------------------------
            public Color color = new Color(1f, 0.85f, 0.3f, 1f);
            public bool pulse = true;

            // "coin" for the built-in gold coin, or the name of one of your own
            // sprites. Empty keeps the weapon's own bullet art.
            public string nodeSprite = "";

            // Spin about the vertical axis. A turning disc is just one whose
            // apparent WIDTH oscillates, so this is an animated scale rather
            // than a flipbook.
            public bool spin;
            public float spinSpeed = 6f;

            // Which way it turns. Vertical is the tossed coin seen from the
            // side - it narrows to its edge and opens out again. Horizontal is
            // the same motion tumbling end over end.
            public bool spinHorizontal;

            // How thin it gets edge-on. A coin has thickness, so this is never
            // zero - and at the thin point the renderer is tinted toward the
            // milled RIM, because edge-on you are looking at the edge, not at a
            // squashed face.
            public float spinThinness = 0.12f;

            [System.NonSerialized] public Sprite nodeArt;
            public string triggerSfx = "";
            public string armSfx = "";

            [System.NonSerialized] public string triggerSfxGuid;
            [System.NonSerialized] public string armSfxGuid;

            // Toss keeps its physics, like Travel - the whole point is that it
            // arcs and falls.
            public bool Halts
            {
                get { return node == Node.Stop || node == Node.Drift; }
            }

            // But unlike Travel it must SURVIVE landing, or a coin would pop on
            // the floor before you could shoot it.
            public bool SurvivesContact
            {
                get { return node != Node.Travel; }
            }
        }

        // Registered per template, the same as every other block, so the node
        // component can find its settings without carrying them per instance.
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
