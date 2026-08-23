using System;
using System.Collections.Generic;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace WeaponForge
{
    // Two unrelated jobs, both needed by `shield`:
    //
    // 1. Attach a ForgeShieldController while a shield weapon is in hand, the
    //    same lifecycle orbit uses.
    // 2. Stop enemy BEAMS. A beam is not a projectile - it is a raycast that
    //    resolves and is gone inside one frame - so there is nothing for the
    //    arc's sweep to catch. It has to be shortened at the source instead.
    public static class ForgeShieldPatch
    {
        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("WeaponForge.Shield");

        private static readonly Dictionary<Shooter, ForgeShieldController>
            _ctrls = new Dictionary<Shooter, ForgeShieldController>();

        private static ForgeShieldController GetOrCreate(
            Shooter shooter, ForgeShield.Config cfg)
        {
            ForgeShieldController ctrl;

            if (_ctrls.TryGetValue(shooter, out ctrl) && ctrl != null)
            {
                ctrl.cfg = cfg;
                ctrl.weapon = shooter.Weapon;
                return ctrl;
            }

            var go = new GameObject("Forge Shield");

            if (shooter.Unit != null)
                go.transform.SetParent(shooter.Unit.transform, false);

            ctrl = go.AddComponent<ForgeShieldController>();
            ctrl.cfg = cfg;
            ctrl.unit = shooter.Unit;
            ctrl.shooter = shooter;
            ctrl.weapon = shooter.Weapon;

            // ShipMovement is what owns the dash, the sprint and the crash
            // damage. It lives on the ship root alongside the Unit.
            if (shooter.Unit != null)
            {
                ctrl.ship = shooter.Unit.GetComponent<ShipMovement>();

                if (ctrl.ship == null)
                    ctrl.ship =
                        shooter.Unit.GetComponentInChildren<ShipMovement>();
            }

            _ctrls[shooter] = ctrl;
            return ctrl;
        }

        private static void Remove(Shooter shooter)
        {
            ForgeShieldController ctrl;

            if (_ctrls.TryGetValue(shooter, out ctrl))
            {
                if (ctrl != null)
                    UnityEngine.Object.Destroy(ctrl.gameObject);

                _ctrls.Remove(shooter);
            }
        }

        [HarmonyPatch(typeof(Shooter), "Update")]
        public class OnUpdate
        {
            static void Postfix(Shooter __instance)
            {
                try
                {
                    WeaponBase weapon = __instance.Weapon;
                    ForgeShield.Config cfg;

                    if (weapon == null ||
                        !ForgeShield.TryGet(weapon.TemplateData, out cfg))
                    {
                        Remove(__instance);
                        return;
                    }

                    GetOrCreate(__instance, cfg);
                }
                catch (Exception e)
                {
                    Log.LogError("Shield update failed: " + e);
                }
            }
        }

        // A held shield suppresses the weapon's own shooting - there is no shot
        // to fire, the trigger means "hold it up". Mirrors what charge and
        // orbit's Hold mode do.
        [HarmonyPatch(typeof(Shooter), "Shoot")]
        public class OnShoot
        {
            static bool Prefix(Shooter __instance)
            {
                try
                {
                    WeaponBase weapon = __instance.Weapon;
                    ForgeShield.Config cfg;

                    if (weapon == null ||
                        !ForgeShield.TryGet(weapon.TemplateData, out cfg))
                        return true;

                    // Arc weapons eat the trigger. A pure ram weapon does not:
                    // it is driven by the dash, so it can still be a perfectly
                    // normal gun at the same time.
                    return !cfg.WantsArc;
                }
                catch
                {
                    return true;
                }
            }
        }

        // Same reason charge needs it: Shooter.Update calls PlayContinousSound
        // BEFORE it reaches Shoot(), so suppressing the shot alone leaves the
        // gun roaring over a silent shield.
        [HarmonyPatch(typeof(Shooter), "PlayContinousSound")]
        public class OnContinuousSound
        {
            static bool Prefix(Shooter __instance)
            {
                try
                {
                    WeaponBase weapon = __instance.Weapon;
                    ForgeShield.Config cfg;

                    if (weapon == null ||
                        !ForgeShield.TryGet(weapon.TemplateData, out cfg))
                        return true;

                    return !cfg.WantsArc;
                }
                catch
                {
                    return true;
                }
            }
        }

        // ------------------------------------------------------------------
        // Beams
        // ------------------------------------------------------------------
        //
        // HitscanWeapon casts twice, in two different methods, and they are
        // genuinely independent: FireSingle does the DAMAGE cast, OnBarrelMoved
        // does the VISUAL one. Clipping only the first would leave the beam
        // drawn straight through a shield that was stopping it, which reads as
        // "the shield does nothing".
        //
        // Both are clipped the same way - by temporarily lowering Range - which
        // works because the visual's no-hit fallback also draws to Range.

        private static float ClipRange(
            HitscanWeapon weapon, Vector2 origin, Vector2 dir, float range)
        {
            if (!ForgeShield.Any)
                return range;

            IList<ForgeShieldController> live = ForgeShieldController.Live;

            if (live.Count == 0)
                return range;

            float best = range;

            for (int i = 0; i < live.Count; i++)
            {
                ForgeShieldController c = live[i];

                if (c == null || c.cfg == null || !c.cfg.blockBeams)
                    continue;

                if (!c.ArcUp || !c.cfg.WantsArc || c.unit == null)
                    continue;

                // Never let a shield block its own owner's beam.
                if (weapon != null && weapon.Owner == c.unit)
                    continue;

                float d;

                float inner = Mathf.Max(
                    0f, c.cfg.radius - c.cfg.thickness * 0.5f);

                if (BandHit(
                        origin, dir, best,
                        c.unit.transform.position,
                        inner, c.cfg.Reach,
                        c.Facing, c.cfg.HalfAngle, out d))
                {
                    if (d < best)
                        best = d;
                }
            }

            return best;
        }

        // First point along the ray that lies on the shield BAND - the annulus
        // sector actually drawn, not the whole pie slice.
        //
        // Solved exactly rather than marched. The first version stepped twelve
        // samples between the circle's entry and exit points, and a simulation
        // of it failed two ways: a beam arriving from BEHIND was blocked
        // (the sample landing on the ship's centre counted as "in front",
        // because a zero-length offset has no direction to compare), and a thin
        // shield was missed entirely whenever the band was narrower than the
        // step. Both are silent in game - the shield would just look unreliable.
        //
        // Exact form: gather every place the ray crosses the band's boundary -
        // the two circles and the two straight ends of the wedge - keep the
        // candidates that are genuinely on the band and inside the wedge, and
        // take the nearest.
        private static bool BandHit(
            Vector2 origin,
            Vector2 dir,
            float range,
            Vector2 centre,
            float inner,
            float outer,
            Vector2 facing,
            float halfAngle,
            out float distance)
        {
            distance = 0f;

            if (dir.sqrMagnitude < 0.000001f ||
                facing.sqrMagnitude < 0.000001f)
                return false;

            Vector2 d = dir.normalized;
            Vector2 f = facing.normalized;

            float cosLimit = Mathf.Cos(Mathf.Clamp(halfAngle, 0f, 180f) *
                                       Mathf.Deg2Rad);

            // Standing inside it already.
            if (OnBand(origin, centre, inner, outer, f, cosLimit))
                return true;

            float best = float.MaxValue;
            bool found = false;

            // The two circles.
            for (int k = 0; k < 2; k++)
            {
                float r = (k == 0) ? outer : inner;

                if (r <= 0f)
                    continue;

                Vector2 m = origin - centre;
                float b = Vector2.Dot(m, d);
                float cc = Vector2.Dot(m, m) - r * r;
                float disc = b * b - cc;

                if (disc < 0f)
                    continue;

                float s = Mathf.Sqrt(disc);

                Offer(-b - s, origin, d, range, centre, inner, outer,
                      f, cosLimit, ref best, ref found);

                Offer(-b + s, origin, d, range, centre, inner, outer,
                      f, cosLimit, ref best, ref found);
            }

            // The two straight ends of the wedge, each a segment from the inner
            // circle out to the outer one. Without these a beam that slides in
            // from the side never registers.
            float baseAngle = Mathf.Atan2(f.y, f.x);

            for (int sign = -1; sign <= 1; sign += 2)
            {
                float a = baseAngle + sign * halfAngle * Mathf.Deg2Rad;
                Vector2 e = new Vector2(Mathf.Cos(a), Mathf.Sin(a));

                Vector2 p0 = centre + e * inner;
                Vector2 seg = e * (outer - inner);

                float den = d.x * seg.y - d.y * seg.x;

                if (Mathf.Abs(den) < 0.000000001f)
                    continue;

                Vector2 rel = p0 - origin;
                float t = (rel.x * seg.y - rel.y * seg.x) / den;
                float u = (rel.x * d.y - rel.y * d.x) / den;

                if (u < 0f || u > 1f)
                    continue;

                Offer(t, origin, d, range, centre, inner, outer,
                      f, cosLimit, ref best, ref found);
            }

            if (!found)
                return false;

            distance = best;
            return true;
        }

        private static void Offer(
            float t,
            Vector2 origin,
            Vector2 d,
            float range,
            Vector2 centre,
            float inner,
            float outer,
            Vector2 f,
            float cosLimit,
            ref float best,
            ref bool found)
        {
            if (t < 0f || t > range || t >= best)
                return;

            if (!OnBand(origin + d * t, centre, inner, outer, f, cosLimit))
                return;

            best = t;
            found = true;
        }

        private static bool OnBand(
            Vector2 p,
            Vector2 centre,
            float inner,
            float outer,
            Vector2 f,
            float cosLimit)
        {
            Vector2 to = p - centre;
            float len = to.magnitude;

            if (len < inner - 0.0001f || len > outer + 0.0001f)
                return false;

            // The exact centre of the circle has no direction, so it cannot be
            // "in front" of anything. Returning true there is what let a beam
            // from behind clip itself on the way through the ship.
            if (len < 0.000001f)
                return false;

            return Vector2.Dot(f, to / len) >= cosLimit;
        }

        // Range is a public get/set on HitscanWeapon, so this is a plain
        // save-clamp-restore around each cast rather than a transpiler.
        [HarmonyPatch(typeof(HitscanWeapon), "FireSingle")]
        public class OnFireSingle
        {
            static void Prefix(
                HitscanWeapon __instance,
                Vector2 position,
                Vector2 direction,
                ref float __state)
            {
                __state = -1f;

                try
                {
                    float clipped = ClipRange(
                        __instance, position, direction, __instance.Range);

                    if (clipped < __instance.Range)
                    {
                        __state = __instance.Range;
                        __instance.Range = clipped;
                    }
                }
                catch { }
            }

            // Finalizer, not Postfix, and the difference matters. A postfix
            // is skipped when the original throws - and this one hands Range
            // back. Skip it once and the clipped value becomes the new base,
            // the next frame clips THAT, and every laser in the game shrinks to
            // nothing permanently. A finalizer runs either way.
            static void Finalizer(HitscanWeapon __instance, float __state)
            {
                if (__state >= 0f)
                    __instance.Range = __state;
            }
        }

        // The VISUAL is clipped at the point it is drawn, not by clamping Range.
        //
        // Clamping Range worked for the damage cast and did NOT work for the
        // visual - R6 test 15: "it blocks the damage, but the beam hits through
        // the shield". Rather than keep guessing why, this clips the actual
        // `end` point `UpdateVisual` is about to draw to, which is the one place
        // the beam's length is decided.
        //
        // It fixes the spark too. UpdateVisual positions the impact particle at
        // `end`, which is why the laser's hit animation was landing on the hull
        // of a ship it was not damaging.
        //
        // UpdateVisual has no idea whose beam it is, so OnBarrelMoved leaves the
        // weapon in `_drawing` for the duration of the call.
        private static HitscanWeapon _drawing;

        [HarmonyPatch(typeof(HitscanWeapon), "OnBarrelMoved")]
        public class OnBarrelMoved
        {
            static void Prefix(HitscanWeapon __instance)
            {
                _drawing = __instance;
            }

            // Finalizer, not Postfix: a postfix is skipped when the original
            // throws, and leaving a stale weapon in `_drawing` would attribute
            // the next beam to the wrong owner.
            static void Finalizer()
            {
                _drawing = null;
            }
        }

        [HarmonyPatch(typeof(HitscanWeaponVisual), "UpdateVisual")]
        public class OnUpdateVisual
        {
            static void Prefix(Vector2 start, ref Vector2 end)
            {
                try
                {
                    Vector2 span = end - start;
                    float len = span.magnitude;

                    if (len < 0.0001f)
                        return;

                    float clipped =
                        ClipRange(_drawing, start, span / len, len);

                    if (clipped < len)
                        end = start + (span / len) * clipped;
                }
                catch { }
            }
        }
    }
}
