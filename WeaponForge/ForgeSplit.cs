using System;
using System.Collections.Generic;
using BepInEx.Logging;
using MyBox;
using UnityEngine;

namespace WeaponForge
{
    // SPLIT - a shot bursts into several, and the fragments inherit the
    // parent's LIVE stats.
    //
    // WHY THIS IS NOT `subEmitter`, and why it is a new key rather than a
    // change to that one. Three things are wrong with a sub-emitter for this
    // job, and all three are the game's design rather than a bug:
    //
    //   1. IT CAN NEVER BE UPGRADED. WeaponFactory.Create applies every
    //      module's IWeaponModifier to the weapon and then builds the sub with
    //      `Create(weaponData.subEmitter, null)` - a null module cluster,
    //      passed literally. So a sub-emitter is not "an instance no module
    //      happened to touch"; the game hands it no modules by construction,
    //      and its damage is forever the number baked into the asset.
    //
    //   2. IT HAS NO OWNER. WeaponBase.Equip is called from exactly two places
    //      (Shooter.SetWeapon and WeaponBasedActiveModule.Activate) and neither
    //      ever sees a sub-emitter, so its Owner stays null and FireSingle
    //      assigns `projectile.Owner = null` to every fragment. Two
    //      consequences: DamagableResource.ProjectileCollided only credits a
    //      kill `if (projectile.Owner != null)`, so a sub-emitter kill credits
    //      NOBODY (onKill rewards never pay for it), and Projectile's
    //      friendly-fire skip is gated on the same null check, so a sub-emitter
    //      shot does no friend/foe test at all.
    //
    //   3. IT FIRES STRAIGHT UP. Both FireSub implementations are
    //      `Fire(new FakeBarrel(transform.position, Vector2.up))`, so a sub's
    //      spread fans around world-up no matter which way the parent was
    //      travelling.
    //
    // Existing weapons rely on all of that (a flak shell whose shrapnel is
    // deliberately weaker is a legitimate thing to build), so subEmitter is
    // left exactly as it was. This key is the one that inherits.
    //
    // The cheap way to inherit everything is not to copy stats one by one but
    // to copy the SHOT: ProjectileWeapon.FireSingle already pushes the weapon's
    // live properties onto each projectile as it fires, so the parent bullet in
    // flight IS the upgraded state. Duplicating it carries damage, burn,
    // explosion, discharge, piercing, art, trail and every module upgrade at
    // once - and, because a copy is made mid-flight, it also carries whatever
    // grow/damageRamp had done to it by then.
    public static class ForgeSplit
    {
        // Trigger names. Deliberately plain strings rather than an enum: Fire()
        // takes a reason, the config holds a set of them, and a FUTURE feature
        // that wants to split a shot adds one line at its own trigger point and
        // one name to the builder page. Nothing here has to change.
        public const string OnDeath = "death";
        public const string OnTime = "time";
        public const string OnDistance = "distance";
        public const string OnHit = "hit";
        public const string OnBounce = "bounce";

        // Damage rules - all three were asked for.
        public const int DamageFull = 0;    // each fragment hits as hard as the parent
        public const int DamageHalf = 1;    // half each (the default)
        public const int DamageShare = 2;   // parent's damage divided by count
        public const int DamageCustom = 3;  // whatever multiplier was typed

        // Aim modes - all three were asked for.
        public const int AimFan = 0;    // a cone around the parent's REAL heading
        public const int AimTarget = 1; // one fragment per nearby enemy
        public const int AimAuto = 2;   // target if anything is in range, else fan

        public class Config
        {
            public readonly HashSet<string> on =
                new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            public int count = 3;
            public float delay = 0.5f;      // "time" trigger
            public float distance = 6f;     // "distance" trigger

            public string weaponName;       // null/empty => copy the parent shot

            public int damageMode = DamageHalf;
            public float damageMultiplier = 0.5f;

            public int aim = AimFan;
            public float spread = 60f;
            public float range = 12f;       // target search radius
            public bool targetObjects = true;
            public bool lockOn = true;
            public float lockOnTurnRate = 220f;

            public float speed = 1f;        // multiplier on the parent's speed
            public float scale = 1f;        // fragment size (art AND hitbox)

            // Fragments inherit the parent's rangeData, and `slowDown` on it
            // means "coast to a dead stop at your range" - which the stock
            // White Popper, the most-copied template in this mod, ships ON.
            // A fragment inheriting it decelerates from birth and clumps in a
            // slow cloud a few units from the burst, which reads as the shards
            // being broken rather than as an inherited setting.
            //
            // ON by default, and that is a judgement: the parent wanted to
            // stop at ITS range, and a fragment is a new shot with its own.
            // TWO things now, and the second is the one that was actually
            // asked for: the fragment does not decelerate, AND it launches at
            // the parent's FIRING speed rather than whatever the parent had
            // decayed to by the time it split. See `Burst`.
            //
            // `"resetSlowdown": false` inherits it, which is worth having for
            // a deliberate cloud of hanging shards.
            public bool resetSlowdown = true;

            public int generations = 1;     // how many times a shot may split
            public int maxPerShot = 8;      // budget for repeatable triggers
            public bool consumeParent = true;   // time/distance: parent is used up

            // Only meaningful on the named-weapon path; the copy path inherits
            // all of this for free by being a copy.
            public bool inheritDamage = true;
            public bool inheritBurn = true;
            public bool inheritExplosion = true;
            public bool inheritDischarge = true;

            // Resolved lazily at RUNTIME, not at build time, so the fragment
            // weapon does not have to load before this one - the same reason
            // ForgeTurret resolves late while subEmitter cannot.
            public WeaponBase weapon;
            public bool weaponTried;

            // The prefab a fragment is built from. Held here rather than on the
            // component because Unity remaps in-hierarchy Object references
            // when it copies - see the note on ForgeSplitter.
            public Projectile sourcePrefab;
            public PhysicsProjectile sourcePhysicsPrefab;

            public bool CopiesParent
            {
                get { return string.IsNullOrEmpty(weaponName); }
            }

            public float DamageFactor
            {
                get
                {
                    switch (damageMode)
                    {
                        case DamageFull:
                            return 1f;
                        case DamageShare:
                            return count > 0 ? 1f / count : 1f;
                        case DamageCustom:
                            return damageMultiplier;
                        default:
                            return 0.5f;
                    }
                }
            }

            public string DamageWord
            {
                get
                {
                    switch (damageMode)
                    {
                        case DamageFull: return "full";
                        case DamageShare: return "share";
                        case DamageCustom:
                            return damageMultiplier.ToString("0.##") + "x";
                        default: return "half";
                    }
                }
            }

            public string AimWord
            {
                get
                {
                    switch (aim)
                    {
                        case AimTarget: return "target";
                        case AimAuto: return "auto";
                        default: return "fan";
                    }
                }
            }
        }

        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("WeaponForge.Split");

        // True once anything is using the feature, so the patches can cost
        // nothing at all on a run with no split weapon in it.
        public static bool Any;

        // A shot destroyed while the game shuts down must not try to build
        // fragments on the way out.
        public static bool Quitting;

        private static bool _armed;

        // Called once by the builder the first time any weapon uses the key.
        public static void Arm()
        {
            Any = true;

            if (_armed)
                return;

            _armed = true;
            Application.quitting += delegate { Quitting = true; };
        }

        // Set while a burst is running, so a fragment created inside Burst
        // cannot re-enter it on the same call.
        private static bool _bursting;

        // A frame-scoped ceiling on fragments, independent of any one shot's
        // budget. Two shots each splitting into eight is fine; a
        // misconfiguration that tries for thousands is not, and the game has no
        // projectile cap of its own.
        private const int MaxPerFrame = 200;
        private static int _frame = -1;
        private static int _thisFrame;

        // ------------------------------------------------------------------
        // The public entry point. ANY trigger, present or future, calls this.
        // ------------------------------------------------------------------
        public static bool Fire(Component shot, string reason)
        {
            if (!Any || shot == null || _bursting)
                return false;

            ForgeSplitter s = shot.GetComponent<ForgeSplitter>();

            if (s == null || !s.Ready)
                return false;

            return Fire(shot, s, reason);
        }

        public static bool Fire(
            Component shot, ForgeSplitter s, string reason)
        {
            if (shot == null || s == null || _bursting)
                return false;

            Config cfg = s.cfg;

            if (cfg == null || !cfg.on.Contains(reason))
                return false;

            if (s.generation >= Mathf.Max(0, cfg.generations))
                return false;

            if (s.used >= Mathf.Max(1, cfg.maxPerShot))
                return false;

            if (Time.frameCount != _frame)
            {
                _frame = Time.frameCount;
                _thisFrame = 0;
            }

            if (_thisFrame >= MaxPerFrame)
                return false;

            _bursting = true;

            try
            {
                Burst(shot, s, cfg, reason);
            }
            catch (Exception e)
            {
                Log.LogError("Split failed (" + reason + "): " + e);
            }
            finally
            {
                _bursting = false;
            }

            return true;
        }

        // ------------------------------------------------------------------
        private static void Burst(
            Component shot, ForgeSplitter s, Config cfg, string reason)
        {
            var ip = shot as IProjectile;

            if (ip == null)
                return;

            int want = Mathf.Clamp(cfg.count, 1, 64);
            Vector2 here = shot.transform.position;

            // The parent's REAL heading - the whole point of the key.
            //
            // The LIVE velocity is preferred, because for a mid-flight trigger
            // it is exactly right. But it cannot be trusted on its own: a shot
            // with `rangeData.slowDown` (which the stock White Popper ships ON)
            // decelerates to EXACTLY ZERO at its range and is then destroyed
            // there, so a death split read zero, fell through to
            // transform.right, and fanned every fragment due EAST regardless of
            // where the player was aiming. So fall back to the last heading the
            // shot had while it was genuinely moving - ForgeSplitter tracks it
            // every frame for this reason.
            Vector2 live = ip.Velocity;
            float liveSpeed = live.magnitude;

            Vector2 heading;
            float parentSpeed;

            if (liveSpeed > 0.5f)
            {
                heading = live / liveSpeed;
                parentSpeed = liveSpeed;
            }
            else if (s.HasHeading)
            {
                heading = s.LastHeading;
                parentSpeed = s.LastSpeed;
            }
            else
            {
                // Genuinely never moved - a parked mine. World-up is the one
                // case where a sub-emitter's hard-coded direction was right.
                heading = liveSpeed > 0.000001f ? live.normalized : Vector2.up;
                parentSpeed = 12f;
            }

            if (parentSpeed <= 0.01f)
                parentSpeed = 12f;

            // ================================================================
            // AND THE SPEED IS THE OTHER HALF OF THE SAME BUG - fixed 2026-08-27
            // ================================================================
            //
            // Everything above is about the parent's HEADING at zero velocity.
            // Its SPEED has exactly the same problem and it was left unfixed:
            // a `slowDown` shot dying of range is travelling at essentially
            // nothing, so `LastSpeed` is whatever it was on the last frame above
            // the 0.5 u/s floor. Multiply the author's `"speed": 1.1` by that
            // and the shards leave at about half a unit per second.
            //
            // Reported as *"the speed once the flechette splits retains the
            // original speed of the parent projectile"*, and the expectation was
            // right: `resetSlowdown` should mean "launch these properly".
            //
            // What `resetSlowdown` did before was clear the fragment's OWN
            // `rangeData.slowDown` (see `Ranged` below) - which stops it
            // decelerating and cannot give back speed it was never given. That
            // is why switching the PARENT's slowDown off was the only thing that
            // helped: it kept the parent fast enough to die fast.
            //
            // `Projectile.StartSpeed` is public and is the speed the shot was
            // fired at, so `"speed"` finally means the same thing wherever in
            // the flight the split happens.
            if (cfg.resetSlowdown)
            {
                float launch = LaunchSpeed(shot);

                // Only ever RAISES it. A split triggered early on a shot that
                // had not slowed yet must not be dragged back down, and a
                // deliberately fast parent (a bounce that gained speed) keeps
                // what it earned.
                if (launch > parentSpeed)
                    parentSpeed = launch;
            }

            List<AimAssistTarget> targets = null;

            if (cfg.aim != AimFan)
                targets = FindTargets(shot, s, cfg, here, want);

            bool useTargets = targets != null && targets.Count > 0;

            // "target" means target, even if nothing is in range: firing the
            // fan anyway would be a different weapon. "auto" is the mode that
            // falls back.
            if (cfg.aim == AimTarget && !useTargets)
                return;

            s.used += want;
            _thisFrame += want;

            // The named-weapon path resolves lazily, so it can still discover
            // on the first burst that the name is no good. Falling through to
            // copies then - rather than doing nothing for that one shot - is
            // what makes a bad name degrade to the default behaviour instead of
            // silently eating a burst.
            if (!cfg.CopiesParent &&
                BurstWeapon(shot, s, cfg, here, heading, want, targets))
            {
                return;
            }

            BurstCopies(shot, s, cfg, heading, parentSpeed, want, targets);
        }

        // ------------------------------------------------------------------
        // PATH 1 - fragments are copies of the parent shot.
        // ------------------------------------------------------------------
        private static void BurstCopies(
            Component shot, ForgeSplitter s, Config cfg,
            Vector2 heading, float parentSpeed, int want,
            List<AimAssistTarget> targets)
        {
            WeaponBase from = ForgeKillsPatch.WeaponOf(shot);
            float factor = cfg.DamageFactor;
            float speed = parentSpeed * Mathf.Max(0.05f, cfg.speed);
            Vector2 at = shot.transform.position;

            for (int i = 0; i < want; i++)
            {
                try
                {
                    // Where this fragment is going, decided BEFORE it exists -
                    // Shoot() stamps idealDirection and the collision mask from
                    // Velocity, so the heading has to be on the shot first.
                    AimAssistTarget t =
                        (targets != null && targets.Count > 0)
                            ? targets[i % targets.Count]
                            : null;

                    Vector2 dir = DirectionFor(heading, cfg.spread, i, want);

                    if (t != null)
                    {
                        Vector2 to = (Vector2)t.transform.position - at;

                        if (to.sqrMagnitude > 0.000001f)
                            dir = to.normalized;
                    }

                    Component c = Spawn(shot, s, at, dir, speed, factor);

                    if (c == null)
                        continue;

                    // Attribution does not come free for a shot born
                    // mid-flight: ForgeKillsPatch only stamps while DoShoot is
                    // on the stack, so without this a fragment's kill would pay
                    // nothing while the parent's kill paid normally.
                    ForgeKillsPatch.Attribute(c, from);

                    // Generation BEFORE anything else can trigger on it, or a
                    // fragment splits again on its own first hit and the depth
                    // cap never bites.
                    var cs = c.GetComponent<ForgeSplitter>();

                    if (cs != null)
                    {
                        cs.generation = s.generation + 1;
                        cs.used = 0;
                        cs.Rearm(c.transform.position);
                    }

                    if (t != null && cfg.lockOn)
                        LockOn(c, t.gameObject, cfg);
                }
                catch (Exception e)
                {
                    Log.LogError("Split copy failed: " + e);
                }
            }
        }

        // Build one fragment from the parent's OWN prefab and hand it the
        // parent's live state.
        //
        // **A fresh projectile is BLANK, and this is not obvious.** Neither
        // projectile class serializes any of its own state - every one of
        // Projectile's fifteen properties is a plain auto-property, and
        // shotTime / startSpeed / idealDirection / collisionLayerMask are
        // private fields with no [SerializeField]. So an Instantiate'd shot
        // arrives with damage 0, radius 0, no lifetime and a collision mask of
        // ZERO: it flies through the world hitting nothing and, with no
        // lifetime enabled, never despawns either. That is exactly how the
        // catalyst's split first shipped - three pretty ghosts per shot, every
        // one inert and immortal.
        //
        // So the state is copied across by hand and the fragment is then
        // genuinely SHOT: Shoot() is what stamps shotTime, startSpeed,
        // idealDirection and the collision mask, and it is also what registers
        // the fragment with ForgeProjectileTracker. Same field list as
        // ForgeCatalystSplit.Clone, which is the reference implementation -
        // kept separate rather than shared because that one copies the LIVE
        // object, and this one must not (see ForgeSplitter.sourcePrefab).
        private static Component Spawn(
            Component parent, ForgeSplitter s, Vector2 at,
            Vector2 dir, float speed, float factor)
        {
            Quaternion rot =
                Quaternion.FromToRotation(Vector3.right, (Vector3)dir);

            var plainFrom = parent as Projectile;

            if (plainFrom != null)
            {
                if (s.cfg.sourcePrefab == null)
                    return null;

                Projectile to = UnityEngine.Object.Instantiate(
                    s.cfg.sourcePrefab, at, rot);

                to.Owner = plainFrom.Owner;
                to.Radius = plainFrom.Radius;
                to.Damage = plainFrom.Damage;
                to.Explosion = plainFrom.Explosion.Duplicate();
                to.DischargeData = plainFrom.DischargeData;
                to.Burn = plainFrom.Burn;
                to.ImpactBehaviour = plainFrom.ImpactBehaviour;
                to.RangeData = Ranged(plainFrom.RangeData, s.cfg);
                to.LifetimeData = Bounded(
                    plainFrom.LifetimeData, to.RangeData);
                to.RotationData = plainFrom.RotationData;
                to.PushForce = plainFrom.PushForce;
                to.PiercingData = plainFrom.PiercingData;
                to.MovementNoiseData = plainFrom.MovementNoiseData;
                to.ProjectileBounceData = plainFrom.ProjectileBounceData;
                to.SubEmitters = plainFrom.SubEmitters;
                to.Velocity = dir * speed;

                Scale(to, s.cfg.scale);
                ScaleDamage(to, factor);

                to.Shoot();
                return to;
            }

            var physFrom = parent as PhysicsProjectile;

            if (physFrom != null)
            {
                if (s.cfg.sourcePhysicsPrefab == null)
                    return null;

                PhysicsProjectile to = UnityEngine.Object.Instantiate(
                    s.cfg.sourcePhysicsPrefab, at, rot);

                to.Owner = physFrom.Owner;
                to.Damage = physFrom.Damage;
                to.Explosion = physFrom.Explosion.Duplicate();
                to.DischargeData = physFrom.DischargeData;
                to.Burn = physFrom.Burn;
                to.ImpactBehaviour = physFrom.ImpactBehaviour;
                to.RangeData = Ranged(physFrom.RangeData, s.cfg);
                to.LifetimeData = Bounded(
                    physFrom.LifetimeData, to.RangeData);
                to.HomingData = physFrom.HomingData;
                to.PushForce = physFrom.PushForce;
                to.SubEmitters = physFrom.SubEmitters;
                to.CollidesWithSlime = physFrom.CollidesWithSlime;
                to.Velocity = dir * speed;

                Scale(to, s.cfg.scale);
                ScaleDamage(to, factor);

                to.Shoot();
                return to;
            }

            return null;
        }

        // The speed the parent was FIRED at, or 0 when that cannot be known.
        //
        // `Projectile.startSpeed` is captured once at `Shoot` from
        // `Velocity.magnitude` and is exactly what the game's own deceleration
        // lerps down from:
        //
        //     Velocity = dir * Lerp(StartSpeed, 0, elapsed / timeToReachRange)
        //
        // A lobbed shot is a different class with no equivalent, and it has no
        // `rangeData` deceleration either - the game computes a range time for
        // one and then never reads the field again - so there is nothing to
        // undo there and 0 is the honest answer.
        private static float LaunchSpeed(Component shot)
        {
            try
            {
                var plain = shot as Projectile;
                return (plain != null) ? plain.StartSpeed : 0f;
            }
            catch
            {
                return 0f;
            }
        }

        // A fragment has to die of something. Normally it inherits whatever
        // bounded the parent, but a shot with BOTH range and lifetime switched
        // off relies on hitting the world - and multiplying that by `count`
        // every time anything splits is how a level fills with invisible
        // bullets. Only touches the case where nothing else would.
        // The parent's range, with its deceleration dropped unless the file
        // asked to keep it. `range` itself is left alone: a fragment travelling
        // the parent's distance is the sane default, and `split.range` is what
        // changes it.
        private static ProjectileRangeData Ranged(
            ProjectileRangeData from, Config cfg)
        {
            if (!cfg.resetSlowdown || !from.enabled || !from.slowDown)
                return from;

            ProjectileRangeData to = from;
            to.slowDown = false;
            return to;
        }

        private static ProjectileLifetimeData Bounded(
            ProjectileLifetimeData lifetime, ProjectileRangeData range)
        {
            if (lifetime.enabled || range.enabled)
                return lifetime;

            lifetime.enabled = true;
            lifetime.time = 6f;

            // This bound is ours, not the weapon's, so it must expire quietly -
            // firing an explosion or a sub-emitter the weapon never scheduled
            // would be a worse surprise than the leak.
            lifetime.spawnOnTimeDown = null;
            lifetime.spawnExplosion = false;
            lifetime.fireSub = false;
            lifetime.discharge = false;
            return lifetime;
        }

        // ------------------------------------------------------------------
        // PATH 2 - fragments come from a named weapon, wearing its art and
        // behaviour, but carrying the PARENT's live stats.
        // ------------------------------------------------------------------
        // Returns false if the fragment weapon could not be used at all, so
        // the caller can fall back to copies.
        private static bool BurstWeapon(
            Component shot, ForgeSplitter s, Config cfg,
            Vector2 here, Vector2 heading, int want,
            List<AimAssistTarget> targets)
        {
            WeaponBase weapon = Resolve(cfg, want);

            if (weapon == null)
                return false;

            var ip = shot as IProjectile;

            // Equip to the parent's owner. This is the sub-emitter fix: an
            // unequipped weapon fires ownerless shots that credit no kills and
            // do no friend/foe check.
            Unit owner = ip.Owner;

            if (owner != null && !ReferenceEquals(weapon.Owner, owner))
                weapon.Equip(owner);

            _pending = new Inheritance
            {
                active = true,
                cfg = cfg,
                damage = ip.Damage,
                burn = ip.Burn,
                explosion = ip.Explosion,
                discharge = ip.DischargeData,
                owner = owner,
                weapon = ForgeKillsPatch.WeaponOf(shot),
                generation = s.generation + 1,
                targets = targets,
                index = 0
            };

            try
            {
                // One Fire, with the weapon's own count and spread already
                // overridden to ours - so exactly `count` fragments leave in
                // one fan, with one shoot sound, instead of `count` separate
                // volleys each playing their own.
                weapon.Fire(new FakeBarrel(here, heading)).Forget();
            }
            finally
            {
                // WeaponBase.Fire is synchronous up to the first await, so the
                // first burst has already been prepared by the time this runs.
                // A burst TAIL would land on a later frame with nothing pending
                // - which is why the builder warns about burstSize > 1 on a
                // fragment weapon rather than silently under-inheriting.
                _pending.active = false;
            }

            return true;
        }

        // What a fragment weapon's projectiles should be handed. Read by the
        // OnPrepareProjectile hook in ForgeSplitPatch.
        public struct Inheritance
        {
            public bool active;
            public Config cfg;
            public Damage damage;
            public MinMaxFloat burn;
            public Explosion explosion;
            public DischargeData discharge;
            public Unit owner;
            public WeaponBase weapon;
            public int generation;
            public List<AimAssistTarget> targets;
            public int index;
        }

        private static Inheritance _pending;

        public static bool HasPending
        {
            get { return _pending.active; }
        }

        // Push the parent's live state onto one fragment as the weapon
        // prepares it. Called from ProjectileWeapon.OnPrepareProjectile, which
        // is the game's own sanctioned "reach into a projectile as it is fired"
        // seam.
        public static void ApplyPending(IProjectile fragment)
        {
            if (!_pending.active || fragment == null)
                return;

            Config cfg = _pending.cfg;

            if (cfg == null)
                return;

            var comp = fragment as Component;

            if (comp == null)
                return;

            // A lobbed fragment is prepared TWICE and the first pass is a blank
            // - damage, velocity and owner are all still unassigned - so
            // writing to it there would be overwritten a few lines later.
            var phys = fragment as PhysicsProjectile;

            if (phys != null && ForgePhysicsShot.NotReady(phys))
                return;

            if (cfg.inheritDamage)
            {
                Damage d = _pending.damage;
                d.amount *= cfg.DamageFactor;
                fragment.Damage = d;
            }

            if (cfg.inheritBurn)
                fragment.Burn = _pending.burn;

            if (cfg.inheritExplosion)
                fragment.Explosion = _pending.explosion.Duplicate();

            if (cfg.inheritDischarge)
                fragment.DischargeData = _pending.discharge;

            if (_pending.owner != null)
                fragment.Owner = _pending.owner;

            ForgeKillsPatch.Attribute(comp, _pending.weapon);

            var cs = comp.GetComponent<ForgeSplitter>();

            if (cs != null)
            {
                cs.generation = _pending.generation;
                cs.used = 0;
                cs.Rearm(comp.transform.position);
            }

            if (_pending.targets != null && _pending.targets.Count > 0)
            {
                AimAssistTarget t =
                    _pending.targets[_pending.index % _pending.targets.Count];

                _pending.index++;

                if (t != null)
                {
                    ForgeCatalystSplit.Aim(
                        comp, t.transform.position, t.gameObject);

                    if (cfg.lockOn)
                        LockOn(comp, t.gameObject, cfg);
                }
            }

            if (cfg.scale != 1f)
                Scale(comp, cfg.scale);
        }

        // ------------------------------------------------------------------
        private static WeaponBase Resolve(Config cfg, int want)
        {
            if (cfg.weaponTried)
            {
                Retune(cfg.weapon, cfg, want);
                return cfg.weapon;
            }

            cfg.weaponTried = true;

            // Through the shared lookup, which accepts the weapon's own
            // "name", its "displayName", the full "Forge Weapon <name>" or a
            // stock asset name. A bare exact-match here is what made four
            // different reasonable spellings all fail in one test round.
            WeaponData data = ForgeWeaponLookup.Find(cfg.weaponName);

            if (data == null)
            {
                ForgeWeaponLookup.LogMiss(
                    "split \"weapon\"", cfg.weaponName);

                Log.LogWarning(
                    "split: falling back to copies of the shot, so the weapon " +
                    "still splits - it just does not wear '" +
                    cfg.weaponName + "'.");

                // Better than not splitting at all: the copy path needs no
                // name and is what an empty "weapon" would have given.
                cfg.weaponName = "";
                return null;
            }

            cfg.weapon = new WeaponFactory().Create(data, null);

            // OnPrepareProjectile is the game's own "reach into a projectile as
            // it is fired" seam - the same event FireSingle raises for its
            // augmentations - so no Harmony patch is needed to hand a fragment
            // the parent's stats. Subscribed once, on the shared instance.
            var pw = cfg.weapon as ProjectileWeapon;

            if (pw != null)
            {
                pw.OnPrepareProjectile += delegate (
                    ProjectileWeapon w, IProjectile fragment)
                {
                    ApplyPending(fragment);
                };
            }
            else
            {
                Log.LogWarning(
                    "split: '" + cfg.weaponName + "' is not a projectile " +
                    "weapon, so its fragments cannot inherit anything - they " +
                    "will fire with their own stats.");
            }

            Retune(cfg.weapon, cfg, want);
            return cfg.weapon;
        }

        // The fragment weapon fans OUR pattern, not its own. Done on the shared
        // instance once rather than per burst, since nothing else fires it.
        private static void Retune(WeaponBase w, Config cfg, int want)
        {
            if (w == null)
                return;

            w.ProjectileCount = want;
            w.Spread = cfg.spread;
            w.BurstSize = 1;
        }

        // ------------------------------------------------------------------
        private static List<AimAssistTarget> FindTargets(
            Component shot, ForgeSplitter s, Config cfg, Vector2 here, int want)
        {
            var ip = shot as IProjectile;

            if (ip == null || ip.Owner == null)
                return null;

            int mask = Physics2D.GetLayerCollisionMask(shot.gameObject.layer);

            var found = new List<AimAssistTarget>();
            var seen = new HashSet<Transform>();

            // NEAREST, not most-forward. FindTarget picks the smallest ANGLE
            // off the heading, which is right for a homing shot and wrong here
            // - it would send every fragment at one distant enemy dead ahead
            // and ignore the two standing beside the burst.
            for (int i = 0; i < want; i++)
            {
                AimAssistTarget t = ForgeHomingPatch.FindNearestTarget(
                    ip.Owner, here, cfg.range, mask, seen, cfg.targetObjects,
                    ForgeTargeting.For(shot));

                if (t == null)
                    break;

                seen.Add(t.transform);
                found.Add(t);
            }

            return found.Count > 0 ? found : null;
        }

        // The same fan the game's own GetDirections builds, including its
        // 360-degree special case (an evenly spaced ring rather than a cone
        // with two fragments on top of each other).
        public static Vector2 DirectionFor(
            Vector2 heading, float spread, int index, int count)
        {
            if (count <= 1)
                return heading;

            float step = (Mathf.Abs(spread - 360f) < 0.01f)
                ? spread / count
                : spread / (count - 1);

            float angle = -spread / 2f + index * step;

            return (Vector2)(Quaternion.Euler(0f, 0f, angle) *
                             (Vector3)heading);
        }

        // ------------------------------------------------------------------
        private static void Scale(Component c, float scale)
        {
            if (scale == 1f || scale <= 0f)
                return;

            c.transform.localScale *= scale;

            // Radius is the REAL hitbox - the CircleCast is rebuilt from it
            // every frame - so a smaller fragment genuinely hits a smaller
            // area rather than just looking like it does. A PhysicsProjectile
            // has a real collider, which localScale already shrank.
            var plain = c as Projectile;

            if (plain != null)
                plain.Radius *= scale;
        }

        private static void ScaleDamage(Component c, float factor)
        {
            if (factor == 1f)
                return;

            var plain = c as Projectile;

            if (plain != null)
            {
                // Through the shared stack, not onto Damage directly: growth,
                // the ramp, chain falloff and the catalyst all write there too,
                // and whoever assigned last would otherwise be the only one
                // that counted.
                var stack = ForgeDamageStack.For(plain);

                if (stack != null)
                {
                    stack.Capture(plain.Damage.amount);
                    stack.split *= factor;
                    stack.Apply(plain);
                    return;
                }
            }

            var ip = c as IProjectile;

            if (ip == null)
                return;

            Damage d = ip.Damage;
            d.amount *= factor;
            ip.Damage = d;
        }

        // Make a targeted fragment's aim STICK. Aiming it once points it at
        // where the enemy is standing now; against anything that moves that
        // looks perfect and then misses.
        private static void LockOn(
            Component c, GameObject target, Config cfg)
        {
            if (target == null)
                return;

            // Plain projectiles only - a PhysicsProjectile has already been
            // handed the target by Aim (its own Homing() reads targetPosition
            // every FixedUpdate) and a second steering model would fight it.
            var plain = c as Projectile;

            if (plain == null)
                return;

            var h = plain.GetComponent<ForgeHoming>();
            bool fresh = (h == null);

            if (fresh)
            {
                h = plain.gameObject.AddComponent<ForgeHoming>();
                h.turnRate = Mathf.Max(1f, cfg.lockOnTurnRate);
                h.range = cfg.range;
                h.cone = 180f;      // it has just been turned; let it turn again
                h.retarget = true;
                h.delay = 0f;
                h.predict = true;
                h.faceTravel = true;
                h.maxTurn = 540f;   // a budget, so an overshoot gives up
            }

            h.shot = true;
            h.startTime = Time.time;
            h.turned = 0f;
            h.nextScan = 0f;
            h.mask = Physics2D.GetLayerCollisionMask(plain.gameObject.layer);
            h.target = target.transform;
            h.targetAim = target.GetComponentInParent<AimAssistTarget>();
        }
    }
}
