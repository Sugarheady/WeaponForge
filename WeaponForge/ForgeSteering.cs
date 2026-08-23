using System;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace WeaponForge
{
    // Pointing a plain projectile somewhere, correctly.
    //
    // The contract this exists to enforce, which the rest of the mod learned the
    // hard way: **write `idealDirection`, not just `Velocity`.**
    //
    // `Projectile.idealDirection` (private) is the game's base heading, and
    // everything that curves a shot - the mod's wave motion, the game's own
    // noise wobble - rebuilds `Velocity` every frame by rotating that field,
    // never by reading Velocity back. So a feature that writes only Velocity is
    // silently overwritten by any curve feature sharing the shot. That is
    // exactly how a boomerang with `wave` on flew off along its original heading
    // while still visibly waving.
    //
    // Older features (boomerang, gravity) each carry their own copy of this
    // reflection. New ones should come here instead.
    public static class ForgeSteering
    {
        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("WeaponForge.Steering");

        private static AccessTools.FieldRef<Projectile, Vector2> _idealRef;
        private static bool _ready;

        private static void Ensure()
        {
            if (_ready)
                return;

            _ready = true;

            try
            {
                _idealRef = AccessTools.FieldRefAccess<Projectile, Vector2>(
                    "idealDirection");
            }
            catch (Exception e)
            {
                _idealRef = null;
                Log.LogWarning(
                    "Could not reach Projectile.idealDirection (" + e.Message +
                    "). Steered shots will still turn, but a shot that also " +
                    "has wave motion or the game's noise wobble will drift " +
                    "back toward its original heading.");
            }
        }

        // Turn a shot onto `dir`, keeping its current speed.
        public static void SetHeading(Projectile p, Vector2 dir)
        {
            if (p == null || dir.sqrMagnitude < 0.000001f)
                return;

            float speed = p.Velocity.magnitude;

            SetHeading(p, dir, (speed > 0.01f) ? speed : 12f);
        }

        public static void SetHeading(Projectile p, Vector2 dir, float speed)
        {
            if (p == null || dir.sqrMagnitude < 0.000001f)
                return;

            Vector2 unit = dir.normalized;

            Ensure();

            if (_idealRef != null)
                _idealRef(p) = unit;

            p.Velocity = unit * speed;
        }
    }
}
