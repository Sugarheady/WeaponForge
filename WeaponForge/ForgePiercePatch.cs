using System;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace WeaponForge
{
    // Enforces a pierce cap. Projectile.TryHit is called for each damageable
    // it overlaps; we count DISTINCT ones. pierceLimit = enemies it passes
    // through - it's destroyed on contact with the one after that (which
    // still takes the hit, since TryHit already ran). Optional per-pierce
    // damage falloff and an explosion on the final hit.
    [HarmonyPatch(typeof(Projectile), "TryHit")]
    public class ForgePiercePatch
    {
        private static readonly ManualLogSource Log =
            ForgeLog.Source("WeaponForge");

        private static MethodInfo _spawnExplosion;
        private static bool _lookedUp;

        static void Postfix(Projectile __instance, IProjectileListener listener)
        {
            try
            {
                // When ModuleForge is installed it owns pierce counting and
                // ADDS this weapon's baked cap to any module caps (one counter,
                // additive). Stand down here so we don't double-count / cap
                // early. Standalone, we count as normal below.
                if (ForgePierceCompat.ModuleForgePresent)
                    return;

                var cap = __instance.GetComponent<ForgePierceCap>();
                if (cap == null || listener == null)
                    return;

                // Only the first contact with each distinct enemy counts.
                if (!cap.seen.Add(listener))
                    return;

                // Reduce damage for the next enemy in the line.
                if (cap.falloff > 0f)
                {
                    Damage d = __instance.Damage;
                    d.amount *= Mathf.Max(0f, 1f - cap.falloff);
                    __instance.Damage = d;
                }

                // Pierced `limit`; the (limit+1)th contact ends it.
                if (cap.seen.Count > cap.limit)
                {
                    if (cap.explodeOnLimit)
                        Explode(__instance);
                    UnityEngine.Object.Destroy(__instance.gameObject);
                }
            }
            catch (Exception e)
            {
                Log.LogError("Pierce cap patch failed: " + e);
            }
        }

        private static void Explode(Projectile projectile)
        {
            if (!_lookedUp)
            {
                _spawnExplosion = AccessTools.Method(
                    typeof(Projectile), "SpawnExplosion");
                _lookedUp = true;
            }

            if (_spawnExplosion == null)
                return;

            try
            {
                _spawnExplosion.Invoke(projectile,
                    new object[] { (Vector2)projectile.transform.position });
            }
            catch (Exception e)
            {
                Log.LogError("Pierce explode failed: " + e);
            }
        }
    }
}
