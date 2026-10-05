using System;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace WeaponForge
{
    // Turning an enemy bullet into one of YOURS.
    //
    // Shared by every feature that catches incoming fire - `deflect`'s
    // travelling disc and `shield`'s held arc - because the operation is a lot
    // more than reversing a velocity, and getting it subtly wrong produces
    // bullets that either pass through everything or come back and kill you.
    //
    // A projectile's FACTION is its GameObject layer, and Projectile bakes that
    // layer into a PRIVATE collisionLayerMask at Shoot time. So a turn is four
    // changes that have to move together:
    //
    //   1. the velocity - and the sprite rotation, or directional art flies
    //      backwards for the rest of its life
    //   2. the GameObject layer
    //   3. that private mask - Projectile ONLY. A PhysicsProjectile collides
    //      through real colliders and Unity's own layer matrix, so remapping
    //      the layer is the whole job there and looking for a mask would throw
    //   4. Owner, or the game's friendly-fire check still treats it as hostile
    //      and a kill with it isn't credited to the player
    //
    // ...plus a fifth for missiles. PhysicsProjectile.Homing() runs every
    // FixedUpdate and is NOT gated on HomingData.enabled, so it keeps torquing
    // toward a private targetPosition that is still the player. A turned
    // missile calmly curves back into you unless that is repointed too.
    //
    // The mask field is private AND typed LayerMask rather than int, so it
    // needs a plain FieldInfo - Harmony's FieldRefAccess<T,F> throws on a
    // value-type mismatch. That one cost a day once.
    public static class ForgeBulletTurn
    {
        private static readonly ManualLogSource Log =
            ForgeLog.Source("WeaponForge.BulletTurn");

        // WHERE a reflected shot goes.
        //
        // "Back" is what a reflect has always done: straight back along the way
        // it came (`-Velocity`). Note that is NOT a bounce off the shield
        // surface - it ignores the shield's facing entirely, which is why a
        // shot coming from below goes back down however you were pointing.
        public const int AimBack = 0;
        public const int AimNearest = 1;
        public const int AimCursor = 2;

        // A TRUE MIRROR: bounce off the reflector's surface the way light does,
        // so the angle you catch a shot at decides where it goes.
        //
        // The surface normal is the outward direction from the reflector to the
        // bullet, which makes every reflector a CONVEX mirror - correct for the
        // shield (an arc centred on the ship), for a catalyst node and for a
        // deflector disc alike. The useful consequence is that WHERE on the arc
        // you catch something matters: dead centre sends it back at the sender,
        // the edge glances it away at a shallow angle.
        public const int AimMirror = 3;

        public static int ParseAim(string s)
        {
            switch ((s ?? "").Trim().ToLowerInvariant())
            {
                case "nearest":
                case "enemy":
                case "target":
                    return AimNearest;
                case "cursor":
                case "aim":
                case "crosshair":
                case "mouse":
                    return AimCursor;
                case "mirror":
                case "bounce":
                case "surface":
                case "angle":
                    return AimMirror;
                default:
                    return AimBack;
            }
        }

        public static string AimWord(int mode)
        {
            switch (mode)
            {
                case AimNearest: return "at the nearest enemy";
                case AimCursor: return "wherever you are aiming";
                case AimMirror: return "mirrored off the shield surface";
                default: return "straight back the way it came";
            }
        }

        private static FieldInfo _maskField;
        private static bool _maskLooked;

        private static FieldInfo _targetPosField;
        private static bool _targetPosLooked;

        private static int _playerLayer = -2;
        private static int _enemyLayer = -2;

        public static int PlayerLayer
        {
            get { EnsureLayers(); return _playerLayer; }
        }

        public static int EnemyLayer
        {
            get { EnsureLayers(); return _enemyLayer; }
        }

        // Turn one caught bullet around.
        //
        // Takes a Component rather than a concrete type because enemies fire
        // BOTH projectile classes - straight shots are Projectile, missiles and
        // lobbed shots are PhysicsProjectile. Everything this needs (Velocity,
        // Owner, Damage) is on IProjectile, which both implement; the genuine
        // differences are called out inline.
        //
        // `owner` is the Unit the bullet should now belong to - the player.
        // Passing null leaves ownership alone, which still works but costs the
        // kill credit.
        //
        // Returns true if the bullet is now yours. On any failure the bullet is
        // destroyed instead and this returns false: a half-converted bullet is
        // worse than one that simply vanished.
        public static bool Turn(
            Component bullet,
            Unit owner,
            bool aimAtNearest,
            float aimRange,
            float speedMultiplier,
            float damageMultiplier)
        {
            return Turn(bullet, owner,
                        aimAtNearest ? AimNearest : AimBack,
                        aimRange, speedMultiplier, damageMultiplier, null);
        }

        // The same, plus a rally config. When one is supplied the rally owns
        // the damage entirely and `damageMultiplier` is ignored - otherwise the
        // two would both multiply on every catch and the shot would grow twice
        // as fast as either number says.
        public static bool Turn(
            Component bullet,
            Unit owner,
            bool aimAtNearest,
            float aimRange,
            float speedMultiplier,
            float damageMultiplier,
            ForgeRally.Config rally)
        {
            return Turn(bullet, owner,
                        aimAtNearest ? AimNearest : AimBack,
                        aimRange, speedMultiplier, damageMultiplier, rally);
        }

        public static bool Turn(
            Component bullet,
            Unit owner,
            int aimMode,
            float aimRange,
            float speedMultiplier,
            float damageMultiplier,
            ForgeRally.Config rally)
        {
            return Turn(bullet, owner, aimMode, aimRange, speedMultiplier,
                        damageMultiplier, rally, Vector2.zero);
        }

        // `surfaceFrom` is the reflector's own position - the ship for a shield
        // arc, the node for a catalyst, the disc for a deflector. Only the
        // mirror mode uses it; everything else ignores it. Passing nothing
        // makes mirror fall back to straight back rather than to a zero vector.
        public static bool Turn(
            Component bullet,
            Unit owner,
            int aimMode,
            float aimRange,
            float speedMultiplier,
            float damageMultiplier,
            ForgeRally.Config rally,
            Vector2 surfaceFrom)
        {
            try
            {
                EnsureLayers();

                var ip = bullet as IProjectile;

                if (ip == null)
                    return false;

                var phys = bullet as PhysicsProjectile;

                float speed = ip.Velocity.magnitude;

                // A shot sitting still - a dropped mine, a stalled lob - has no
                // heading to reverse. Give it one rather than dividing by zero.
                if (speed <= 0.01f)
                    speed = 10f;

                speed *= Mathf.Max(0.05f, speedMultiplier);

                Vector2 dir = -ip.Velocity.normalized;

                AimAssistTarget aimed = null;

                // Bounce off the surface, the way light does.
                //
                // Vector2.Reflect wants the INCOMING direction, not the
                // reversed one - `dir` has already been flipped by this point,
                // so the incoming ray is -dir.
                if (aimMode == AimMirror)
                {
                    Vector2 normal =
                        (Vector2)bullet.transform.position - surfaceFrom;

                    if (normal.sqrMagnitude > 0.000001f)
                    {
                        Vector2 mirrored =
                            Vector2.Reflect(-dir, normal.normalized);

                        if (mirrored.sqrMagnitude > 0.000001f)
                            dir = mirrored.normalized;
                    }

                    // No usable normal - the bullet is exactly on the
                    // reflector, or nothing was passed - so it stays a plain
                    // reverse rather than flying off along a zero vector.
                }

                // Point it where the player is pointing.
                //
                // This is the RAW crosshair, not the assisted firing direction:
                // a deflection you aimed should go where you are looking, not
                // where aim-assist would have nudged a shot leaving the barrel.
                // The two are genuinely different rays on the ship.
                if (aimMode == AimCursor)
                {
                    Vector2 cursor;

                    if (ForgeVolley.TryCursor(owner, out cursor))
                    {
                        Vector2 toCursor =
                            cursor - (Vector2)bullet.transform.position;

                        if (toCursor.sqrMagnitude > 0.000001f)
                            dir = toCursor.normalized;
                    }

                    // No aimer - a minion or a turret reflecting something -
                    // so it falls back to straight back rather than nowhere.
                }

                if (aimMode == AimNearest)
                {
                    // The same target-finder homing, ricochet and deflect use,
                    // so they all agree on what counts as a target and none of
                    // them can pick terrain or your own side.
                    int mask =
                        (_playerLayer >= 0)
                            ? Physics2D.GetLayerCollisionMask(_playerLayer)
                            : Physics2D.GetLayerCollisionMask(
                                bullet.gameObject.layer);

                    AimAssistTarget found =
                        ForgeHomingPatch.FindTarget(
                            owner, bullet.transform.position, dir,
                            aimRange, 180f, mask);

                    if (found != null)
                    {
                        Vector2 to =
                            (Vector2)found.transform.position -
                            (Vector2)bullet.transform.position;

                        if (to.sqrMagnitude > 0.000001f)
                        {
                            dir = to.normalized;
                            aimed = found;
                        }
                    }
                }

                ip.Velocity = dir * speed;

                // PUBLISH THE HEADING, or the aim above is undone within a
                // frame - and this is the one feature where that is not a
                // hypothetical.
                //
                // `Projectile.FixedUpdate` rebuilds Velocity from the private
                // `idealDirection` whenever `movementNoiseData` is on, so a
                // shot redirected by Velocity alone wanders straight back onto
                // the heading it arrived on. Every other steering feature in
                // the mod dodges this by turning the noise OFF at build time -
                // but a deflected bullet is not ours to build. It belongs to
                // whoever fired it, and the four stock weapons that ship the
                // noise enabled are all ENEMY weapons (Soldier, Floater,
                // Floater Officer, Cross Small - purple), at 60 degrees and
                // 2 Hz. Enemy fire is the only thing there is to deflect, so
                // the one slot this could break in is exactly the one it is
                // for. Runtime is therefore the only place to fix it.
                //
                // `dir` is still measured off the wobbled Velocity above,
                // deliberately: a reflection should answer the travel the
                // player can see, not a hidden base heading.
                var plain = bullet as Projectile;

                if (plain != null)
                    ForgeSteer.SetIdeal(plain, dir);

                bullet.transform.rotation =
                    Quaternion.FromToRotation(Vector3.right, (Vector3)dir);

                // See the note above: a missile steers itself back at you
                // unless its private target is moved as well. Target has to go
                // with it, because Update() re-reads that every frame while
                // homing is on and would overwrite the repoint next tick.
                if (phys != null)
                {
                    Vector2 aimAt = (aimed != null)
                        ? (Vector2)aimed.transform.position
                        : (Vector2)bullet.transform.position + dir * 10000f;

                    phys.Target = (aimed != null) ? aimed.gameObject : null;

                    SetTargetPosition(phys, aimAt);
                }

                if (_playerLayer >= 0 && _enemyLayer >= 0)
                {
                    VisualCustomizer.RemapLayer(
                        bullet.gameObject, _enemyLayer, _playerLayer);

                    if (phys == null)
                    {
                        SetMask(
                            (Projectile)bullet,
                            Physics2D.GetLayerCollisionMask(_playerLayer));
                    }
                }

                if (owner != null)
                    ip.Owner = owner;

                if (damageMultiplier != 1f && rally == null)
                {
                    Damage dmg = ip.Damage;
                    dmg.amount *= damageMultiplier;
                    ip.Damage = dmg;
                }

                if (rally != null)
                    ForgeRally.OnReflected(bullet, owner, rally);

                return true;
            }
            catch (Exception e)
            {
                Log.LogError(
                    "Could not turn a shot around, destroying it instead: " + e);

                if (bullet != null)
                    UnityEngine.Object.Destroy(bullet.gameObject);

                return false;
            }
        }

        // SWAP A BULLET'S SIDE WITHOUT TOUCHING WHERE IT IS GOING.
        //
        // `Turn` above is the full operation for CATCHING incoming fire: it
        // reverses or re-aims the shot as well as changing sides. Charm needs
        // only half of that - a charmed enemy's bullets should keep flying
        // exactly where it aimed them, they just need to belong to your side so
        // they can reach other enemies.
        //
        // WHY THIS IS THE FIX FOR "A CHARMED ENEMY CANNOT SHOOT ANYTHING".
        // A projectile's faction IS its GameObject layer, and EnemyProjectiles
        // does not collide with Entities at all - so flipping a unit's faction
        // leaves its bullets physically unable to touch another enemy. Two of
        // the four changes in `Turn` fix exactly that, and this is them.
        //
        // AND WHY IT IS DONE AT RUNTIME, on the real bullet, rather than on a
        // prefab at build time: the build-time route is what the minion ammo
        // retarget already tries, and it demonstrably does not work (R10 test 6
        // - the retarget logs success and minion shots still hit the player).
        // Whatever the cause there - a shared weapon asset, a clone that is not
        // the one actually fired - acting on the projectile that exists cannot
        // miss it.
        //
        // Owner is deliberately NOT changed. The charmed unit stays the owner,
        // its faction has already been flipped, and the game's friendly-fire
        // check is faction-based through that owner - so it is already correct,
        // and kills credit the charmed unit exactly as a minion's would.
        public static bool Adopt(Component bullet)
        {
            if (bullet == null)
                return false;

            try
            {
                EnsureLayers();

                if (_playerLayer < 0 || _enemyLayer < 0)
                    return false;

                VisualCustomizer.RemapLayer(
                    bullet.gameObject, _enemyLayer, _playerLayer);

                // Projectile ONLY. A PhysicsProjectile collides through real
                // colliders and Unity's own matrix, so remapping the layer is
                // the whole job there and reaching for a mask would throw.
                var plain = bullet as Projectile;

                if (plain != null)
                {
                    SetMask(
                        plain, Physics2D.GetLayerCollisionMask(_playerLayer));
                }

                return true;
            }
            catch (Exception e)
            {
                Log.LogError("Adopting a bullet failed: " + e);
                return false;
            }
        }

        private static void EnsureLayers()
        {
            if (_playerLayer == -2)
                _playerLayer = LayerMask.NameToLayer("PlayerProjectiles");

            if (_enemyLayer == -2)
                _enemyLayer = LayerMask.NameToLayer("EnemyProjectiles");
        }

        private static void SetMask(Projectile p, LayerMask mask)
        {
            if (!_maskLooked)
            {
                _maskLooked = true;
                _maskField =
                    typeof(Projectile).GetField(
                        "collisionLayerMask",
                        BindingFlags.NonPublic | BindingFlags.Instance);
            }

            if (_maskField == null)
                return;

            // Boxed as LayerMask, matching the field's real type.
            _maskField.SetValue(p, mask);
        }

        // Public wrapper: a PhysicsProjectile steers ITSELF - Homing() runs
        // every FixedUpdate regardless of the homing flag - so moving this
        // private target is how you aim one. The catalyst needs it for the same
        // reason a reflected missile does.
        public static void PointAt(PhysicsProjectile phys, Vector2 where)
        {
            if (phys != null)
                SetTargetPosition(phys, where);
        }

        private static void SetTargetPosition(
            PhysicsProjectile phys, Vector2 where)
        {
            if (!_targetPosLooked)
            {
                _targetPosLooked = true;
                _targetPosField = AccessTools.Field(
                    typeof(PhysicsProjectile), "targetPosition");

                if (_targetPosField == null)
                {
                    Log.LogWarning(
                        "PhysicsProjectile.targetPosition not found - " +
                        "turned missiles will steer back toward whoever they " +
                        "were originally aimed at. Straight shots are " +
                        "unaffected.");
                }
            }

            if (_targetPosField != null)
                _targetPosField.SetValue(phys, where);
        }
    }
}
