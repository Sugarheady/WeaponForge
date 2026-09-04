using System;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace WeaponForge
{
    // THE ONE OWNER of `Projectile.idealDirection`.
    //
    // That field is the game's BASE heading, and everything which curves a shot
    // - the mod's wave, the game's own noise wobble - rebuilds `Velocity` every
    // frame by rotating it, never by reading Velocity back. So a feature that
    // writes only `Velocity` is silently overwritten by any curve feature
    // sharing the shot, which is how a boomerang with `wave` on flew off along
    // its ORIGINAL heading while still visibly waving.
    //
    // WHY THIS FILE EXISTS. Boomerang, chain and converge each grew their own
    // private `FieldRef` and their own lazy init - three copies of the same
    // reflection, each with its own warning text. Wall-riding would have been a
    // fourth. That is the same drift that had three copies of the stock sprite
    // lookup and two different answers for particle animation speed; one owner
    // per question is the rule that keeps them from disagreeing.
    //
    // Reflection because `idealDirection` is a private field with no
    // [SerializeField] - there is no property to go through.
    public static class ForgeSteer
    {
        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("WeaponForge.Steer");

        private static AccessTools.FieldRef<Projectile, Vector2> _ideal;
        private static bool _ready;

        // True once the field is reachable. A caller that can degrade
        // gracefully should check this and say so in its own words - the
        // consequence differs per feature, which is why the warning is theirs
        // to write rather than one generic line here.
        public static bool Available
        {
            get
            {
                Ensure();
                return _ideal != null;
            }
        }

        private static void Ensure()
        {
            if (_ready)
                return;

            _ready = true;

            try
            {
                _ideal = AccessTools.FieldRefAccess<Projectile, Vector2>(
                    "idealDirection");
            }
            catch (Exception e)
            {
                _ideal = null;

                Log.LogWarning(
                    "Could not reach Projectile.idealDirection (" + e.Message +
                    "). Steering features still turn shots, but any of them " +
                    "combined with \"wave\" or the game's own noise will drift " +
                    "back onto the original heading.");
            }
        }

        // Publish the intended heading. Safe to call with a zero vector (does
        // nothing) and safe when the field could not be reached.
        public static void SetIdeal(Projectile p, Vector2 direction)
        {
            if (p == null || direction.sqrMagnitude < 0.000001f)
                return;

            Ensure();

            if (_ideal != null)
                _ideal(p) = direction.normalized;
        }

        public static bool TryGetIdeal(Projectile p, out Vector2 direction)
        {
            direction = Vector2.zero;

            if (p == null)
                return false;

            Ensure();

            if (_ideal == null)
                return false;

            direction = _ideal(p);
            return direction.sqrMagnitude > 0.000001f;
        }

        // Heading AND velocity together, which is what almost every caller
        // wants: writing one without the other is the bug this file documents.
        //
        // WHEN A WAVE IS ALSO ON THE SHOT, the DIRECTION half is left to it.
        //
        // Wave rebuilds Velocity from `idealDirection` in its own FixedUpdate
        // prefix, and two prefixes on one method have no reliable order, so a
        // steerer that writes a bare direction into Velocity gives one of two
        // different results depending on which way round they happen to load:
        // go before wave and you get a weaving pursuit, go after it and your
        // write lands on top of the weave and suppresses it completely. Same
        // JSON, same shot, invisible difference - the exact shape of
        // order-dependence this file exists to end.
        //
        // So publish the heading, and write only the SPEED, along whatever
        // direction the shot currently has. Wave then supplies the direction
        // from the fresh heading in either order, and the speed - which wave
        // preserves rather than owns - still gets through. Skipping the write
        // entirely would have been simpler and wrong: `speed` is not always
        // the speed the shot already had (a boomerang ramps it down through
        // the turn and back up on the return), and dropping it would have
        // quietly frozen every speed change on a waving shot.
        public static void SetHeading(Projectile p, Vector2 direction, float speed)
        {
            if (p == null || direction.sqrMagnitude < 0.000001f)
                return;

            Vector2 unit = direction.normalized;

            SetIdeal(p, unit);

            if (WaveOwnsDirection(p))
            {
                Vector2 v = p.Velocity;

                p.Velocity =
                    ((v.sqrMagnitude > 0.000001f) ? v.normalized : unit) *
                    speed;

                return;
            }

            p.Velocity = unit * speed;
        }

        // A wave that has actually been fired. Before `Shoot` its own prefix
        // bails out, so there would be nothing to defer TO.
        private static bool WaveOwnsDirection(Projectile p)
        {
            var w = p.GetComponent<ForgeWaveMotion>();

            return w != null && w.shot;
        }
    }
}
