using System;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace WeaponForge
{
    // Rides on a gas weapon's projectile and turns it into a cloud when it
    // stops being a projectile.
    //
    // OnDestroy rather than a lifetime hook, because a flamethrower-shaped
    // weapon mostly does NOT hit anything - its puffs time out in mid-air, and
    // a cloud that only appeared on impact would leave a spray weapon with
    // almost no gas at all. OnDestroy catches every way a shot can end: range,
    // lifetime, impact, piercing budget, anything a future feature adds.
    public class ForgeGasSource : MonoBehaviour
    {
        // The ID is what survives Instantiate; the reference does not. See
        // ForgeConfigTable - a plain `public Config cfg` on a prefab-attached
        // component arrives null on every shot, which is why this weapon used to
        // fire a bare projectile and leave no gas at all.
        public int cfgId = -1;

        // The derived trail config gets its own id for exactly the same reason
        // the main one does: an int survives Instantiate and a reference does
        // not.
        public int trailCfgId = -1;

        [NonSerialized] public ForgeGas.Config cfg;
        [NonSerialized] public ForgeGas.Config trailCfg;

        [NonSerialized] public Unit owner;
        [NonSerialized] public Resource damageType;
        [NonSerialized] public WeaponBase weapon;
        [NonSerialized] public bool bloomed;

        private Vector2 lastVelocity;
        private IProjectile shot;
        private bool looked;

        // Awake runs on the clone as Instantiate returns, so the config is back
        // in place before Shoot, before any patch and before the first frame -
        // no dependence on Harmony patch ordering.
        private void Awake()
        {
            Resolve();
        }

        public void Resolve()
        {
            if (cfg == null && cfgId >= 0)
                cfg = ForgeConfigTable.Get<ForgeGas.Config>(cfgId);

            if (trailCfg == null && trailCfgId >= 0)
                trailCfg = ForgeConfigTable.Get<ForgeGas.Config>(trailCfgId);
        }

        private void LateUpdate()
        {
            // The velocity at the moment of death is what a Drift cloud rolls
            // along, and by OnDestroy the projectile has usually been zeroed or
            // detached already - so it is remembered here instead.
            //
            // Cached: a spray weapon can have dozens of these alive at once and
            // this runs on every one of them every frame.
            if (!looked)
            {
                looked = true;
                shot = GetComponent<IProjectile>();

                if (shot == null)
                    shot = GetComponentInParent<IProjectile>();
            }

            if (shot != null && shot.Velocity.sqrMagnitude > 0.0001f)
                lastVelocity = shot.Velocity;

            if (trailCfg != null)
                LayTrail();
        }

        // ---- the trail ---------------------------------------------------
        private Vector3 lastDrop;
        private float nextDropAt;
        private bool trailArmed;

        private void LayTrail()
        {
            Vector3 here = transform.position;

            if (!trailArmed)
            {
                // Armed from the muzzle, but deliberately NOT dropping here.
                // The first cloud lands one spacing out, so the line starts
                // ahead of the ship rather than on top of it - which matters
                // because `hitPlayer` is a thing people turn on.
                trailArmed = true;
                lastDrop = here;
                nextDropAt = Time.time + Mathf.Max(0.02f, trailCfg.trailInterval);
                return;
            }

            bool byDistance = false;
            bool byTime = false;

            int mode = cfg != null ? cfg.trailMode : ForgeGas.TrailDistance;
            float every = Mathf.Max(0.3f, cfg != null ? cfg.trailEvery : 1.2f);
            float interval =
                Mathf.Max(0.02f, cfg != null ? cfg.trailInterval : 0.15f);

            if (mode == ForgeGas.TrailDistance || mode == ForgeGas.TrailBoth)
                byDistance = (here - lastDrop).sqrMagnitude >= every * every;

            if (mode == ForgeGas.TrailTime || mode == ForgeGas.TrailBoth)
                byTime = Time.time >= nextDropAt;

            if (!byDistance && !byTime)
                return;

            lastDrop = here;
            nextDropAt = Time.time + interval;

            ForgeGasCloud cloud = ForgeGasCloud.Spawn(
                trailCfg, here, lastVelocity, owner);

            if (cloud != null)
            {
                cloud.SetDamageType(damageType);
                cloud.SetWeapon(weapon);
            }
        }

        public void Rebind()
        {
            looked = false;
            shot = null;
            trailArmed = false;
        }

        public void Bloom()
        {
            if (bloomed || cfg == null)
                return;

            // Gated here rather than at each call site so it covers EVERY route
            // to an end cloud - impact, lifetime, range, and anything added
            // later - which is the whole point of a "no blob at the end of my
            // trail" switch.
            if (!cfg.bloomOnDeath)
            {
                bloomed = true;
                return;
            }

            bloomed = true;

            ForgeGasCloud cloud = ForgeGasCloud.Spawn(
                cfg, transform.position, lastVelocity, owner);

            if (cloud != null)
            {
                cloud.SetDamageType(damageType);
                cloud.SetWeapon(weapon);
            }
        }

        private void OnDestroy()
        {
            if (bloomed || cfg == null)
                return;

            // `single` used to bail out here, on the theory that a grenade
            // should only bloom where it LANDS. R6 test 25 showed why that was
            // wrong: a lobbed canister whose lifetime expires in mid-air - or
            // which is destroyed by anything other than a logged collision -
            // then produced no gas whatsoever, and read as "the weapon does
            // nothing". Impact still wins (Land() blooms first and Bloom is
            // idempotent); this is the safety net so a canister ALWAYS leaves
            // its cloud somewhere.

            // Do not bloom while the scene is being torn down - every live
            // projectile is destroyed at once there, and a room full of gas
            // appearing on a level change is not a feature.
            if (!gameObject.scene.isLoaded)
                return;

            Bloom();
        }
    }

    public static class ForgeGasPatch
    {
        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("WeaponForge.Gas");

        private static void Capture(Component shot)
        {
            try
            {
                var src = shot.GetComponentInChildren<ForgeGasSource>(true);

                if (src == null)
                    return;

                src.Resolve();

                var ip = shot as IProjectile;

                if (ip == null)
                    return;

                src.owner = ip.Owner;
                src.damageType = ip.Damage.damageType;
                src.weapon = ForgeKillsPatch.WeaponOf(shot);
                src.bloomed = false;

                // A fresh clone of the prefab is instantiated per shot, so the
                // cached lookup has to be invalidated or a re-used pooled
                // object would keep the previous shot's reference.
                src.Rebind();
            }
            catch (Exception e)
            {
                Log.LogError("Gas capture failed: " + e);
            }
        }

        private static void Land(Component shot)
        {
            try
            {
                var src = shot.GetComponentInChildren<ForgeGasSource>(true);

                if (src == null || src.cfg == null)
                    return;

                // The grenade-shaped mode always blooms here, and anything
                // else does when the file asked for it. Otherwise a cloud
                // arrives when the shot dies, which for an ordinary shot is a
                // moment later anyway and covers the far more common case of
                // timing out in the air.
                if (!src.cfg.ImpactOnly && !src.cfg.bloomOnImpact)
                    return;

                src.Bloom();
            }
            catch (Exception e)
            {
                Log.LogError("Gas landing failed: " + e);
            }
        }

        [HarmonyPatch(typeof(Projectile), "Shoot")]
        public class OnShoot
        {
            static void Postfix(Projectile __instance)
            {
                Capture(__instance);
            }
        }

        [HarmonyPatch(typeof(PhysicsProjectile), "Shoot")]
        public class OnShootPhysics
        {
            static void Postfix(PhysicsProjectile __instance)
            {
                // FireSingle calls Shoot() TWICE on a lobbed shot and the first
                // one is a blank - Owner and Damage are not assigned yet, so
                // capturing there records a null owner and no damage type.
                if (ForgePhysicsShot.NotReady(__instance))
                    return;

                Capture(__instance);
            }
        }

        [HarmonyPatch(typeof(Projectile), "OnObjectHit")]
        public class OnHit
        {
            static void Postfix(Projectile __instance)
            {
                Land(__instance);
            }
        }

        [HarmonyPatch(typeof(PhysicsProjectile), "OnCollisionEnter2D")]
        public class OnHitPhysics
        {
            static void Postfix(PhysicsProjectile __instance)
            {
                Land(__instance);
            }
        }
    }
}
