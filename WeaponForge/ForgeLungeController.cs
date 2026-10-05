using System;
using BepInEx.Logging;
using UnityEngine;

namespace WeaponForge
{
    // Rides the ship. Slows it while the charge builds, launches it on
    // release, and throttles its engines for the commitment window.
    //
    // HOW THE SHIP IS SLOWED. `ShipMovement.MaxSpeed` and `.Acceleration` are
    // read-only properties that return the public fields `maxSpeed` /
    // `acceleration` (or their boosted twins), so the fields are what get
    // written. `FixedUpdate` drives each engine with
    // `Acceleration * multiplier`, falling off as speed approaches `MaxSpeed`
    // - so scaling those two scales the whole feel without touching the
    // rigidbody, which `slow` already uses for enemies and which would fight
    // the lunge impulse.
    //
    // ONE CAPTURE, ALWAYS RESTORED FROM IT. The same discipline the tint owner
    // needed: capture the originals ONCE and always write
    // `base * multiplier`, never `current * multiplier`. Otherwise a wind-up
    // that overlaps a commitment compounds and the ship ends the run at a
    // fraction of its speed.
    //
    // SELF-HEALING RATHER THAN HOOKED INTO EVERY CANCEL PATH. The charge block
    // can abandon a charge for several reasons (a shop opens, the tank runs
    // dry, the weapon changes) and not all of them carry the shooter. So the
    // wind-up is stamped and expires on its own if it stops being renewed -
    // no cancel path can leave the ship permanently slow.
    public class ForgeLungeController : MonoBehaviour
    {
        private static readonly ManualLogSource Log =
            ForgeLog.Source("WeaponForge.Lunge");

        private ForgeLunge.Config cfg;
        private ShipMovement move;
        private Unit unit;
        private WeaponBase weapon;

        private float baseMaxSpeed, baseAccel;
        private float baseBoostSpeed, baseBoostAccel;
        private bool captured;

        private float lastWindAt = -999f;
        private float committedUntil;
        private bool committed;

        // The raised MaxSpeed multiplier the launch is coasting on, held past
        // the steering lock. 0 = not coasting. See DoLaunch for why.
        private float coastMul;
        private bool crashed;

        private static readonly RaycastHit2D[] _hits = new RaycastHit2D[8];
        private static ContactFilter2D _filter = new ContactFilter2D();

        // ------------------------------------------------------------------
        public static void Wind(
            Shooter shooter, WeaponBase weapon, float level)
        {
            ForgeLungeController c = For(shooter, weapon);

            if (c != null)
                c.DoWind(level);
        }

        public static void Launch(
            Shooter shooter, WeaponBase weapon, float level)
        {
            ForgeLungeController c = For(shooter, weapon);

            if (c != null)
                c.DoLaunch(level);
        }

        private static ForgeLungeController For(
            Shooter shooter, WeaponBase weapon)
        {
            if (shooter == null || weapon == null)
                return null;

            ForgeLunge.Config cfg;

            if (!ForgeLunge.TryGet(weapon.TemplateData, out cfg))
                return null;

            try
            {
                var move = shooter.GetComponentInParent<ShipMovement>();

                if (move == null)
                    return null;

                var c = move.gameObject.GetComponent<ForgeLungeController>();

                if (c == null)
                    c = move.gameObject.AddComponent<ForgeLungeController>();

                c.cfg = cfg;
                c.move = move;
                c.weapon = weapon;

                if (c.unit == null)
                    c.unit = move.GetComponentInParent<Unit>();

                return c;
            }
            catch (Exception e)
            {
                Log.LogError("Finding the ship to lunge failed: " + e);
                return null;
            }
        }

        // ------------------------------------------------------------------
        private void Capture()
        {
            if (captured || move == null)
                return;

            captured = true;

            baseMaxSpeed = move.maxSpeed;
            baseAccel = move.acceleration;
            baseBoostSpeed = move.maxSpeedWhileBoosted;
            baseBoostAccel = move.accelerationWhileBoosted;
        }

        private void Apply(float speedMul, float accelMul)
        {
            if (!captured || move == null)
                return;

            move.maxSpeed = baseMaxSpeed * speedMul;
            move.acceleration = baseAccel * accelMul;
            move.maxSpeedWhileBoosted = baseBoostSpeed * speedMul;
            move.accelerationWhileBoosted = baseBoostAccel * accelMul;
        }

        // Is the ship still travelling faster than it normally could? Anything
        // under its own top speed means the drag wall is no longer a factor.
        private bool StillFast()
        {
            if (move == null)
                return false;

            Rigidbody2D rb = move.Rigidbody;

            if (rb == null)
                return false;

            return rb.linearVelocity.magnitude > baseMaxSpeed * 1.02f;
        }

        private void Restore()
        {
            if (!captured || move == null)
            {
                captured = false;
                return;
            }

            move.maxSpeed = baseMaxSpeed;
            move.acceleration = baseAccel;
            move.maxSpeedWhileBoosted = baseBoostSpeed;
            move.accelerationWhileBoosted = baseBoostAccel;

            // A fresh wind-up must not inherit a stale coast.
            coastMul = 0f;

            captured = false;
        }

        // ------------------------------------------------------------------
        private void DoWind(float level)
        {
            if (committed)
                return;      // already flying; the wind-up is over

            lastWindAt = Time.time;

            Capture();

            float t = cfg.WindAt(level);

            Apply(
                Mathf.Lerp(1f, Mathf.Clamp(cfg.windSpeed, 0.02f, 1f), t),
                Mathf.Lerp(1f, Mathf.Clamp(cfg.windAccel, 0.02f, 1f), t));
        }

        private void DoLaunch(float level)
        {
            try
            {
                Rigidbody2D rb = move.Rigidbody;

                if (rb == null)
                    return;

                Vector2 dir = Direction();

                if (dir.sqrMagnitude < 0.000001f)
                    dir = Vector2.right;

                dir = dir.normalized;

                float f = Mathf.Lerp(
                    cfg.minForce, cfg.force, Mathf.Clamp01(level));

                if (f <= 0f)
                {
                    Restore();
                    return;
                }

                // Impulse, so the number IS the resulting speed: the ship's
                // mass is exactly 1. Its normal top speed is 12.5, so 60 is a
                // genuine launch rather than a nudge.
                rb.AddForce(dir * f, ForceMode2D.Impulse);

                Capture();

                // THE SHIP HAS A DRAG WALL ABOVE ITS TOP SPEED, and it is what
                // made the launch feel like nothing. `ShipMovement.FixedUpdate`
                // REWRITES `linearDamping` every single frame:
                //
                //     if (isHovering)  damping = defaultDrag * hoverDragMultiplyer
                //     else if (speed > MaxSpeed)
                //         damping = max(damping,
                //                       defaultDrag * (speed/MaxSpeed)^2.7)
                //     else             damping = defaultDrag
                //
                // At force 75 against a top speed of 12.5 that exponent is
                // 6^2.7 = about 120x the normal drag, so the impulse was gone
                // within a couple of physics steps. Setting damping ourselves
                // cannot win - the game overwrites it next frame.
                //
                // Note the FIRST branch: hovering skips the speed term
                // entirely. That is exactly what he reported - *"when you hold
                // shift you go into hover mode, thats when it launches you
                // better ... sadly it doesn't do that when your moving
                // normally"* - and it is the confirmation that this is the
                // right mechanism rather than a plausible one.
                //
                // So raise MaxSpeed instead, using the game's own knob: keep
                // `speed / MaxSpeed` at or under 1 and the `else` branch leaves
                // drag alone, letting the lunge coast down naturally.
                float launchSpeed = f / Mathf.Max(0.0001f, rb.mass);

                float speedMul = (baseMaxSpeed > 0.01f)
                    ? Mathf.Max(1f, (launchSpeed * 1.05f) / baseMaxSpeed)
                    : 1f;

                Apply(speedMul, Mathf.Clamp01(cfg.commitSteer));

                // Held until the ship is actually slow again, not just until
                // the steering lock expires - `commit` is about control, and
                // restoring MaxSpeed while still travelling at launch speed
                // would slam the drag wall back up mid-flight.
                coastMul = speedMul;

                committed = true;
                crashed = false;
                committedUntil = Time.time + Mathf.Max(0.02f, cfg.commit);

                if (!string.IsNullOrEmpty(cfg.sfx))
                {
                    try { AudioManager.PlaySfx(cfg.sfx, transform.position); }
                    catch (Exception) { }
                }

                // Its own contact damage, for the length of the lunge, built
                // from this block's own keys. Reusing the contact controller
                // rather than writing a second damage path means the two can
                // never drift - and leaving `damage` at 0 simply hands the job
                // to whatever `contactDamage` block the weapon has.
                if (cfg.damage > 0f && unit != null)
                {
                    var contact = new ForgeContact.Config
                    {
                        who = ForgeContact.WhoShip,
                        damage = cfg.damage,
                        damageType = cfg.damageType,
                        push = cfg.push,
                        recoil = cfg.recoil,
                        repeatDelay = cfg.repeatDelay,
                        radius = cfg.radius,
                        hitsEnemies = true,
                        hitsPlayer = cfg.hitsPlayer,
                        detect = ForgeContact.DetectSwept
                    };

                    // THE WINDOW IS THE FLIGHT, NOT THE STEERING LOCK.
                    // `commit` is how long the ship cannot steer - typically
                    // 0.3s - but the lunge is still travelling long after that,
                    // and contact damage that expires mid-flight reads as
                    // "contact damage does not do anything from the players
                    // ship". Priced off the actual launch instead: at the
                    // ship's own drag the speed halves about every
                    // 1/damping seconds, so the flight is a good deal longer
                    // than the lock.
                    float window = Mathf.Max(
                        Mathf.Max(0.02f, cfg.commit) + 0.05f,
                        Mathf.Min(2.5f, launchSpeed / Mathf.Max(4f, baseMaxSpeed)));

                    ForgeContactController.Give(
                        unit, contact, unit, weapon, window);
                }
            }
            catch (Exception e)
            {
                Log.LogError("Launching a lunge failed: " + e);
                Restore();
                committed = false;
            }
        }

        // ------------------------------------------------------------------
        private Vector2 Direction()
        {
            if (cfg.aim == ForgeLunge.AimMove)
            {
                Vector2 fly = move.FlyDirection;

                if (fly.sqrMagnitude > 0.0001f)
                    return fly;

                // Standing still with the stick centred - fall through to the
                // cursor rather than launching due world-east.
            }

            if (cfg.aim == ForgeLunge.AimBarrel)
            {
                var shooter = move.GetComponentInChildren<Shooter>(true);

                if (shooter != null && shooter.Barrel != null)
                    return shooter.Barrel.Direction;
            }

            // Cursor. Searching DOWN from the ship's root, which is safe - the
            // documented trap is searching UP from the barrel, because Aimer
            // and AssistedBarrel sit on unrelated branches.
            var aimer = move.GetComponentInChildren<Aimer>(true);

            if (aimer != null)
                return (Vector2)aimer.TargetPosition - (Vector2)transform.position;

            return move.FlyDirection;
        }

        // ------------------------------------------------------------------
        private void Update()
        {
            if (cfg == null)
                return;

            try
            {
                if (committed)
                {
                    if (Time.time >= committedUntil)
                    {
                        committed = false;

                        // The steering lock is over, but the ship may still be
                        // travelling at launch speed. Hand steering back while
                        // keeping the raised MaxSpeed until it has coasted down,
                        // or restoring it here would put the drag wall straight
                        // back up and stop the lunge dead mid-flight.
                        if (coastMul > 1f && StillFast())
                            Apply(coastMul, 1f);
                        else
                            Restore();
                    }

                    return;
                }

                // Coasting: steering is already back, we are only holding
                // MaxSpeed up until the speed itself falls back into normal
                // range. Then the ship goes back to being an ordinary ship.
                if (coastMul > 1f)
                {
                    if (StillFast())
                        return;

                    coastMul = 0f;
                    Restore();
                    return;
                }

                // The wind-up stopped being renewed - the trigger came up, or
                // the charge was abandoned for one of several reasons that do
                // not all reach here. Either way the ship gets its speed back.
                if (captured && Time.time - lastWindAt > 0.12f)
                    Restore();
            }
            catch (Exception e)
            {
                Log.LogError("A lunge update failed: " + e);
                Restore();
                committed = false;
            }
        }

        // Crashing into terrain, if this weapon prices it. Off by default,
        // which matches the game: the ship's own ImpactDamage is disabled
        // unless you are sprinting, so flying into a wall is normally free.
        private void FixedUpdate()
        {
            if (cfg == null || !committed || crashed || cfg.selfDamage <= 0f)
                return;

            try
            {
                Rigidbody2D rb = move.Rigidbody;

                if (rb == null)
                    return;

                Vector2 v = rb.linearVelocity;
                float speed = v.magnitude;

                if (speed < 1f)
                    return;

                _filter.useTriggers = false;
                _filter.SetLayerMask(LayerMask.GetMask("Ground"));

                int n = Physics2D.CircleCast(
                    transform.position, 0.5f, v / speed, _filter, _hits,
                    speed * Time.fixedDeltaTime);

                if (n <= 0)
                    return;

                crashed = true;

                var hb = (unit != null)
                    ? unit.GetComponentInChildren<HealthBase>() : null;

                if (hb == null)
                    return;

                Damage d = new Damage();
                d.amount = cfg.selfDamage;
                d.damageType = (weapon != null)
                    ? weapon.Damage.damageType : null;

                // DELIBERATELY NOT opted into crit. This is `selfDamage` - the
                // lunge hurting YOU for crashing into a wall - and a critical
                // hit on your own self-damage would be a bug, not a feature.
                // The lunge's damage to ENEMIES goes through
                // ForgeContactController, which IS opted in.
                if (d.damageType != null)
                    hb.TakeDamage(d);
            }
            catch (Exception e)
            {
                Log.LogError("A lunge crash check failed: " + e);
                crashed = true;
            }
        }

        private void OnDisable()
        {
            // Never leave the ship slowed because something else switched this
            // off mid-charge.
            Restore();
            committed = false;
        }

        private void OnDestroy()
        {
            Restore();
        }
    }
}
