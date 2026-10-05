using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace WeaponForge
{
    // "What volley is being fired right now, which pellet is this one, and
    // where is the player actually pointing?"
    //
    // Every pellet of a shotgun blast spawns at the SAME point and differs
    // only by angle - WeaponBase.DoShoot walks GetDirections() and calls
    // FireSingle once per pellet, all from one barrel position. So a pellet
    // knows its own heading but has no idea where the volley as a whole was
    // aimed, and that is exactly what a converging pattern needs: the focal
    // point sits on the volley's axis, not on any single pellet's.
    //
    // DoShoot is the right place to catch it, and provably on the path of
    // every shot: it is also where the shoot SFX plays and where the muzzle
    // flash instances fire.
    //
    // The whole loop is synchronous - FireSingle instantiates the projectile
    // and calls Shoot() before returning - so a plain static holds good for
    // the entire volley with no bookkeeping at all. The frame stamp is the
    // safety net: anything reading this outside the volley that set it (a
    // sub-emitter firing a frame later, say) sees a stale frame and ignores
    // it rather than steering toward a focal point from someone else's shot.
    public static class ForgeVolley
    {
        private static readonly ManualLogSource Log =
            ForgeLog.Source("WeaponForge.Volley");

        // Where the barrel was, and where it pointed, for the volley in
        // progress. Both are only meaningful while Active.
        public static Vector2 Origin;
        public static Vector2 Axis;

        // The point the player is actually AIMING AT, in world space - not a
        // direction, an actual position, so a feature can put something
        // exactly under the crosshair. Only valid when HasCursor is true.
        public static Vector2 Cursor;
        public static bool HasCursor;

        // How many pellets this volley will fire, and how many have asked for
        // their slot so far.
        public static int Count;
        public static int Index;

        // The weapon DoShoot is running for. One field rather than a growing
        // list of copied stats: a feature that needs Spread, AngleVariance or
        // AngleOffset can read the LIVE property - which carries whatever
        // modules have done to it - instead of a snapshot that would have to be
        // extended every time somebody wants another number. Only valid while
        // Active is true.
        public static WeaponBase Weapon;

        private static int _frame = -1;
        private static bool _open;

        // Barrel -> Aimer, because the lookup walks the hierarchy and a
        // 20-shots-per-second weapon would otherwise do it 20 times a second
        // for an answer that never changes.
        private static Component _lastBarrel;
        private static Aimer _lastAimer;

        public static bool Active
        {
            get { return _open && _frame == Time.frameCount; }
        }

        // This pellet's 0-based slot in the volley. Called exactly once per
        // pellet, by whichever feature needs it.
        public static int TakeIndex()
        {
            return Index++;
        }

        // The Aimer that drives this barrel, if there is one.
        //
        // Aimer holds a public `barrel` field pointing AT the BarrelTransform
        // and exposes TargetPosition - the world point the ship is aiming at.
        //
        // This is the RAW crosshair. Aimer.AimAt runs the point through every
        // IAimEffect first, but nothing in the game implements that interface,
        // so the chain is empty and TargetPosition is the exact mouse world
        // position.
        //
        // Do NOT conclude from that that the game has no aim assist - it does,
        // one stage further down. AssistedBarrel (on all four ship prefabs)
        // wraps the BarrelTransform and rewrites the FIRING DIRECTION onto the
        // nearest AimAssistTarget within WeaponData.aimAssistData.maxAngle,
        // leading it if isPredictive. Every stock White weapon ships it on
        // (15 degrees, 45 on rockets), so the crosshair and the direction the
        // pellets actually leave along are NOT the same ray. Axis below is the
        // assisted one; this is the unassisted one. Features that must honour
        // the cursor have to use this, not the axis.
        //
        // A FakeBarrel - what sub-emitters and consumables fire through - is a
        // plain class, not a Component, so it resolves to null and callers
        // fall back. That is correct: a sub-emitter has no crosshair.
        // THIS IS WHERE converge's cursor mode was broken for an entire test
        // round, and the shape of the bug is worth keeping written down.
        //
        // On the ship the two things live on completely separate branches:
        //
        //     Aimer          on  Barrel <- Base <- ShipKBM
        //     AssistedBarrel on  AssistedBarrel <- PrimaryWeapon <- ModuleSlots
        //                                                        <- ShipKBM
        //
        // So GetComponentInParent finds nothing. And the reference fallback
        // could never work either, because **`Aimer.barrel` is typed
        // `BarrelTransform`, while the weapon fires through an
        // `AssistedBarrel`** - two unrelated types, so the comparison is always
        // false. FindAimer returned null, HasCursor went false, and every
        // cursor-focused volley silently fell back to a fixed distance. It
        // looked exactly like "converge ignores my crosshair", which is what it
        // was.
        //
        // AssistedBarrel wraps the real BarrelTransform in a private field, so
        // the fix is to unwrap it and match THAT.
        // The raw crosshair for a UNIT, for features that have no barrel to
        // work back from - a shield arc catching a bullet, a catalyst node
        // turning one around.
        //
        // Deliberately unassisted: this is `Aimer.TargetPosition`, the exact
        // mouse world position, NOT the assisted firing direction. A deflection
        // aimed by the player should go where the player is pointing, not where
        // aim-assist would have nudged a shot fired from the barrel.
        public static bool TryCursor(Unit unit, out Vector2 point)
        {
            point = Vector2.zero;

            if (unit == null)
                return false;

            try
            {
                Aimer aimer = unit.GetComponentInChildren<Aimer>(true);

                if (aimer == null)
                {
                    Transform root = unit.transform.root;

                    if (root != null)
                        aimer = root.GetComponentInChildren<Aimer>(true);
                }

                if (aimer == null)
                    return false;

                point = aimer.TargetPosition;
                return true;
            }
            catch (System.Exception)
            {
                return false;
            }
        }

        private static Aimer FindAimer(IBarrel barrel)
        {
            var component = barrel as Component;

            if (component == null)
                return null;

            if (ReferenceEquals(component, _lastBarrel) && _lastAimer != null)
                return _lastAimer;

            var aimer = component.GetComponentInParent<Aimer>();

            if (aimer == null)
            {
                // What the Aimer actually drives. For a plain BarrelTransform
                // that is the barrel itself; for an AssistedBarrel it is the
                // BarrelTransform hidden inside it.
                object driven = Unwrap(barrel);

                Transform root = component.transform.root;

                if (root != null)
                {
                    Aimer[] all = root.GetComponentsInChildren<Aimer>(true);

                    for (int i = 0; i < all.Length; i++)
                    {
                        if (all[i] == null)
                            continue;

                        if (ReferenceEquals(all[i].barrel, barrel) ||
                            (driven != null &&
                             ReferenceEquals(all[i].barrel, driven)))
                        {
                            aimer = all[i];
                            break;
                        }
                    }

                    // Last resort: one Aimer on the whole unit is
                    // unambiguous, so use it rather than reporting "no
                    // crosshair" and quietly changing what the weapon does.
                    // Only when there is exactly one - two turrets on one unit
                    // must not borrow each other's aim.
                    if (aimer == null && all.Length == 1)
                        aimer = all[0];
                }
            }

            _lastBarrel = component;
            _lastAimer = aimer;

            return aimer;
        }

        // AssistedBarrel.barrel is [SerializeField] private, so it needs
        // reflection. Cached, because this runs on the DoShoot path.
        private static FieldInfo _assistedInner;
        private static bool _assistedLooked;

        private static object Unwrap(IBarrel barrel)
        {
            if (barrel is BarrelTransform)
                return barrel;

            var assisted = barrel as AssistedBarrel;

            if (assisted == null)
                return null;

            if (!_assistedLooked)
            {
                _assistedLooked = true;
                _assistedInner = typeof(AssistedBarrel).GetField(
                    "barrel",
                    BindingFlags.NonPublic | BindingFlags.Instance);

                if (_assistedInner == null)
                {
                    Log.LogWarning(
                        "AssistedBarrel.barrel not found - converge's cursor " +
                        "focus will fall back to a fixed distance. Report " +
                        "this; it means the game's barrel layout changed.");
                }
            }

            if (_assistedInner == null)
                return null;

            try
            {
                return _assistedInner.GetValue(assisted);
            }
            catch
            {
                return null;
            }
        }

        [HarmonyPatch(typeof(WeaponBase), "DoShoot")]
        public class OnDoShoot
        {
            static void Prefix(WeaponBase __instance, IBarrel barrel)
            {
                try
                {
                    if (barrel == null)
                        return;

                    Vector2 aim = barrel.Direction;

                    if (aim.sqrMagnitude <= 0.000001f)
                        return;

                    Origin = barrel.Position;
                    Axis = aim.normalized;
                    Weapon = __instance;
                    Count = Mathf.Max(
                        1, Mathf.RoundToInt(__instance.ProjectileCount));
                    Index = 0;

                    Aimer aimer = FindAimer(barrel);

                    HasCursor = (aimer != null);

                    if (HasCursor)
                        Cursor = aimer.TargetPosition;

                    _frame = Time.frameCount;
                    _open = true;
                }
                catch
                {
                    // A volley context is a nicety - never let losing it stop
                    // the weapon from firing.
                    _open = false;
                }
            }

            static void Postfix()
            {
                _open = false;
                Weapon = null;
            }
        }
    }
}
