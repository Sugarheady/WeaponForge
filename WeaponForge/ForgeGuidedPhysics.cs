using System;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace WeaponForge
{
    // GUIDED, ON A ROCKET - and the refusal it replaces was WRONG.
    //
    // R13 tests 154 and 155 both failed with a build warning that read:
    //
    //     "guided does not support LOBBED weapons yet ... a lobbed shot cannot
    //      be steered at all: rewriting its velocity cancels the fall and it
    //      stops being a lobbed shot."
    //
    // His reply was *"I think missiles are considered physics based projectiles
    // so it should work sadly =(."* He was right, and the sentence was wrong in
    // a specific and instructive way: **every clause of it is true about the
    // TECHNIQUE I had in mind, and the game does not use that technique.**
    //
    // ---- WHAT `usePhysics` ACTUALLY MEANS ---------------------------------
    //
    // `usePhysics` does not mean "lobbed". It means "this shot is a rigidbody",
    // and it covers two completely different things:
    //
    //     Ammo Derbis (a grenade)   gravityScale 1.8, damping 0.4
    //     Ammo Rocket Fire          gravityScale 0,   damping 3
    //
    // THE ROCKET HAS NO GRAVITY AT ALL. There is no fall to cancel. Keying the
    // refusal on the FLAG rather than on the gravity is what refused a weapon
    // with nothing wrong with it - one field lookup away from the right answer.
    //
    // ---- AND THE GAME ALREADY STEERS THEM, WITH ITS OWN CODE --------------
    //
    // `PhysicsProjectile.Homing()` runs every FixedUpdate and does two things:
    //
    //     AddTorque toward `targetPosition` (gated by maxAngularVelocity)
    //     AddForce along transform.right up to maxSpeed
    //
    // - a torque-steered, thrusting missile. `Weapon Rocket Fire` ships
    // `torque 2, acceleration 70, maxSpeed 100, maxAngularVelocity 1000`, so a
    // stock rocket is a guided missile whose target never moves: `Shoot()` sets
    // `targetPosition` to a point 10,000 units dead ahead and nothing updates it.
    //
    // **So "fly the rocket with the mouse" is one field write, not a rewrite.**
    // Put the cursor in `targetPosition` each frame and the game's own steering
    // does the work - which means no velocity is ever rewritten, gravity keeps
    // working for a weapon that has some, and the turn is rate-limited by the
    // engine rather than by us.
    //
    // TWO THINGS THAT FELL OUT FOR FREE, both from reading `Homing()` rather
    // than from testing:
    //
    //  * **`Homing()` IGNORES `HomingData.enabled`.** The flag gates only the
    //    re-targeting block in `Update`; the torque and the thrust are
    //    unconditional. So this works on a physics weapon whose homing is
    //    switched off, which is most of them.
    //  * **`maxAngularVelocity` IS a degrees-per-second cap**, because Unity 2D
    //    measures `Rigidbody2D.angularVelocity` in degrees. So `turnRate` maps
    //    onto it exactly, with no calibration.
    //
    // ---- WHY THE PRIVATE FIELD RATHER THAN THE PUBLIC `Target` ------------
    //
    // `Target` is public and `Update` copies its position into `targetPosition`
    // every frame, so a dummy GameObject following the cursor would work with no
    // reflection at all. It was the first design and it lost on two counts:
    // that copy only happens `if (HomingData.enabled)`, which would silently
    // exclude exactly the weapons noted above; and it means a GameObject created
    // and destroyed per shot. One cached `FieldRef` has neither problem.
    //
    // The value-type trap does not apply here - the field is `Vector2` and the
    // ref is `Vector2`, an exact match, which is the case `FieldRefAccess`
    // handles correctly. (`collisionLayerMask` broke phasing because it is a
    // `LayerMask` being asked for as an `int`.) It is resolved once, and if it
    // ever fails the feature says so loudly and stands down.
    public class ForgeGuidedPhysics : MonoBehaviour
    {
        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("WeaponForge.Guided");

        // PLAIN VALUES ONLY. This rides the projectile PREFAB and Unity copies
        // serialized state only, so a reference to a Config class would arrive
        // NULL on every shot - the most expensive bug this project has had.
        public float turnRate = 240f;
        public float delay;
        public float duration;
        public bool whileHeld;

        // How close to the cursor before it stops steering. Smaller than the
        // plain-projectile default because a torque-steered missile already
        // overshoots gently rather than snapping round.
        public float arrive = 1.5f;

        // ---- runtime only -------------------------------------------------
        [NonSerialized] public bool shot;
        [NonSerialized] public float startTime;
        [NonSerialized] public bool released;
        [NonSerialized] public bool committed;
        [NonSerialized] public Shooter shooter;
        [NonSerialized] public bool lookedForShooter;

        // Set by the builder when any physics weapon asks for guidance.
        public static bool Any;

        // ------------------------------------------------------------------
        // The one private field, resolved once.
        // ------------------------------------------------------------------
        private static AccessTools.FieldRef<PhysicsProjectile, Vector2> _targetRef;
        private static FieldInfo _targetField;
        private static bool _looked;
        private static bool _broken;

        private static void Resolve()
        {
            if (_looked)
                return;

            _looked = true;

            // FieldRef first (no boxing on a per-frame write), FieldInfo as the
            // fallback, and a loud stand-down if neither works. A feature that
            // quietly does nothing is the failure shape this project pays for
            // most often, so there is no silent third branch.
            try
            {
                _targetRef =
                    AccessTools.FieldRefAccess<PhysicsProjectile, Vector2>(
                        "targetPosition");
            }
            catch (Exception)
            {
                _targetRef = null;
            }

            if (_targetRef != null)
                return;

            _targetField =
                AccessTools.Field(typeof(PhysicsProjectile), "targetPosition");

            if (_targetField != null)
                return;

            _broken = true;

            Log.LogWarning(
                "guided on a PHYSICS weapon (a rocket or a grenade) needs " +
                "PhysicsProjectile.targetPosition and it could not be reached, " +
                "so guidance is off for those shots. Guidance on ordinary " +
                "bullets is unaffected. This means the game changed that " +
                "field - say so and quote this line.");
        }

        // Aim the shot's steering at a world point. Everything about this
        // feature is this one call.
        public static bool Aim(PhysicsProjectile p, Vector2 point)
        {
            Resolve();

            if (_broken || p == null)
                return false;

            if (_targetRef != null)
            {
                _targetRef(p) = point;
                return true;
            }

            if (_targetField != null)
            {
                _targetField.SetValue(p, point);
                return true;
            }

            return false;
        }

        // ------------------------------------------------------------------
        // Letting go: point it far along its own nose and stop.
        //
        // The game has this behaviour already - `Update`'s `FromLockOnly`
        // branch does exactly this when a lock is lost - but only while
        // `HomingData.enabled`, so it is done here to be true of every physics
        // weapon rather than most of them. `transform.right` is the nose, which
        // is what the thrust follows, so committing along it is the heading the
        // shot is actually about to take.
        // ------------------------------------------------------------------
        public static void Commit(PhysicsProjectile p)
        {
            if (p == null)
                return;

            Vector2 nose = p.transform.right;

            if (nose.sqrMagnitude < 0.000001f)
            {
                nose = p.Velocity;

                if (nose.sqrMagnitude < 0.000001f)
                    return;
            }

            Aim(p, (Vector2)p.transform.position + nose.normalized * 10000f);
        }
    }
}
