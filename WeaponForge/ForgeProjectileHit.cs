using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace WeaponForge
{
    // "Did that TryHit actually land, or was it swallowed by the re-hit
    // delay?"
    //
    // This exists because a Harmony POSTFIX runs on every return path,
    // including the early one:
    //
    //     private void TryHit(IProjectileListener listener, ...)
    //     {
    //         if (!this.CanDamage(listener)) return;      // <-- postfix still runs
    //         listener.ProjectileCollided(this, point, normal);
    //         this.lastDamageTimes[listener] = Time.time;
    //     }
    //
    // A piercing shot re-enters OnObjectHit EVERY FRAME it overlaps an enemy,
    // so TryHit is called several times per enemy and only the first of them
    // does anything. Anything counting hits from a bare postfix is therefore
    // counting FRAMES OF OVERLAP, not enemies - which quietly burned two or
    // three chain jumps on the first target and made a chain look like it ran
    // out early. Same mistake was in the damage ramp's "pierce" counter.
    //
    // The exact test is the record the game leaves behind: a hit that landed
    // has just written lastDamageTimes[listener] = Time.time. Reference-typed
    // field, so FieldRefAccess is safe here (the value-type trap that broke
    // phasing does not apply).
    public static class ForgeProjectileHit
    {
        private static AccessTools.FieldRef<
            Projectile, Dictionary<IProjectileListener, float>> _times;

        private static bool _ready;

        private static void Ensure()
        {
            if (_ready)
                return;

            _ready = true;

            try
            {
                _times = AccessTools.FieldRefAccess<
                    Projectile, Dictionary<IProjectileListener, float>>(
                        "lastDamageTimes");
            }
            catch
            {
                _times = null;
            }
        }

        // True when this call actually damaged the listener.
        //
        // Fails OPEN: if the field can't be reached we report "landed", which
        // restores the old over-counting rather than silently switching a
        // feature off entirely.
        public static bool Landed(Projectile p, IProjectileListener listener)
        {
            Ensure();

            if (_times == null || p == null || listener == null)
                return true;

            try
            {
                Dictionary<IProjectileListener, float> times = _times(p);

                float when;

                // Exact equality is right: both sides are the same float,
                // assigned from Time.time inside this very frame.
                return times != null &&
                       times.TryGetValue(listener, out when) &&
                       when == Time.time;
            }
            catch (Exception)
            {
                return true;
            }
        }
    }
}
