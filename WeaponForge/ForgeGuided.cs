using UnityEngine;

namespace WeaponForge
{
    // GUIDED PROJECTILES - the shot follows your crosshair while it flies.
    //
    // His ask: *"YES! This sounds fun! Guided missiles/projectiles sounds fun as
    // heck."* Half-Life's RPG, Worms' guided missile, Metroid's missiles.
    //
    // ---- WHY THIS IS NOT `homing` WITH A DIFFERENT TARGET ------------------
    //
    // Three features in this mod already move a shot toward something, and it
    // is worth being precise about the difference, because "we already have
    // homing" is the obvious objection:
    //
    //   homing            the MOD picks an enemy and chases it. The player has
    //                     no say once it is fired.
    //   converge cursor   reads the crosshair ONCE, at the moment of launch, to
    //                     decide where a volley meets.
    //   guided (this)     reads the crosshair EVERY FRAME, so the player flies
    //                     the shot. It is the only one of the three that is an
    //                     input rather than an automation.
    //
    // That is also why it has no target-finding at all: there is nothing to
    // acquire, no cone, no retargeting, no mask. It is much smaller than homing
    // despite doing something that feels bigger.
    //
    // ---- THE TWO FACTS IT RESTS ON ----------------------------------------
    //
    //  * `ForgeVolley.TryCursor(unit, out point)` is the crosshair, and it is
    //    deliberately the UNASSISTED one - `Aimer.TargetPosition`, the exact
    //    mouse world position, not the aim-assisted firing direction. A shot the
    //    player is steering by hand must go where the player is pointing.
    //    Finding that Aimer is the part that once cost a test round; that helper
    //    already solves it.
    //  * Steering writes `idealDirection`, via `ForgeSteer.SetHeading`, never
    //    `Velocity` alone. Anything that curves a shot rebuilds Velocity from
    //    that base heading every frame, so writing only Velocity gets silently
    //    overwritten by `wave` or the game's own noise wobble - the bug that
    //    made a boomerang fly off straight while still visibly waving.
    //
    // A plain `Projectile` rebuilds its collision sweep from Velocity every
    // frame, so steering moves the damage volume and not merely the sprite.
    // A LOBBED shot cannot be steered at all - rewriting a rigidbody's velocity
    // cancels the fall and turns a grenade into a missile - so the builder
    // refuses `usePhysics` weapons rather than half-working on them.
    //
    // FIELDS ARE PLAIN VALUES ON PURPOSE. This component rides the projectile
    // PREFAB, and Unity copies serialized state only: a reference to a
    // `Config` class would arrive NULL on every shot, which is the single most
    // expensive bug this project has had. Floats and bools survive the clone.
    public class ForgeGuided : MonoBehaviour
    {
        // Degrees per second the shot can turn. The default is deliberately
        // brisk - a guided shot that cannot keep up with the mouse reads as
        // unresponsive rather than as heavy.
        public float turnRate = 240f;

        // Seconds after launch before guidance starts. A small value lets the
        // shot leave the barrel along your aim before it starts answering the
        // mouse, which looks like a rocket lighting its motor.
        public float delay;

        // Seconds of guidance, from the end of `delay`. 0 = for as long as it
        // lives. Past it the shot keeps whatever heading it had.
        public float duration;

        // Guide only while the trigger is still held, and stop for good the
        // moment it is released - "let go and it commits".
        //
        // OFF by default. On a tap-fired weapon the trigger is down for about
        // one frame, so as a default this would give almost no guidance at all
        // and the feature would look broken.
        public bool whileHeld;

        // Stop steering once the shot is this close to the crosshair. Without
        // it a shot that reaches the cursor turns as hard as it can, forever,
        // and orbits it - which reads as a bug rather than as a feature.
        public float arrive = 1.5f;

        // Point the sprite where it is going. Matches `homing`.
        public bool faceTravel = true;

        // ---- runtime only; never serialized, so a clone starts clean -----
        [System.NonSerialized] public bool shot;
        [System.NonSerialized] public float startTime;

        // Latched once the trigger has been released in `whileHeld` mode, so a
        // re-press cannot resume steering a shot the player already let go of.
        [System.NonSerialized] public bool released;

        // The Shooter that fired this shot, resolved once and cached. Only
        // needed by `whileHeld`.
        [System.NonSerialized] public Shooter shooter;
        [System.NonSerialized] public bool lookedForShooter;

        // Set by the builder when any weapon asks for guidance, so the
        // per-frame patch can cost nothing at all in a run without one.
        public static bool Any;
    }
}
