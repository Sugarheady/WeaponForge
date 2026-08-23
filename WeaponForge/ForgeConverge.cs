using UnityEngine;

namespace WeaponForge
{
    // Converging shotgun - the pellets of one volley curve inward and meet at
    // a single point ahead of the muzzle, then cross and fan out again.
    //
    // The distinction worth keeping straight: this is NOT "aim every pellet at
    // the same spot". That would just be a weapon with no spread. Each pellet
    // keeps the launch angle the weapon's own "spread" gave it and then bends
    // along a smooth arc that lands on the focal point, so the volley reads as
    // a cone that necks down to a waist and opens again - an hourglass, with a
    // real sweet spot in the middle.
    //
    // Params live here; ForgeConvergePatch fills in the runtime state at Shoot
    // and does the steering. Same three-piece shape as ForgeHoming /
    // ForgeHomingPatch.
    public class ForgeConverge : MonoBehaviour
    {
        public enum Focus
        {
            // Wherever the player is pointing - the aim-assisted crosshair
            // position, not a fixed distance. You choose the meeting point
            // every shot, which is the version that actually feels like
            // aiming a weapon. The default. Falls back to Distance for
            // anything with no crosshair (a sub-emitter, a turret).
            Cursor,

            // A fixed point `distance` units along the volley's aim. The
            // sweet spot is a property of the WEAPON, so hitting with a full
            // volley is a positioning problem.
            Distance,

            // The nearest legitimate target ahead, so the volley collapses
            // onto whatever is actually there. Forgiving; falls back to
            // Distance when nothing is in range.
            Target,

            // Don't converge at all - the SPAZER. The shots leave abreast
            // across `width` and fly dead parallel forever, holding formation
            // the whole way. Super Metroid's spazer beam.
            //
            // This is the one mode that ignores the weapon's own `spread`:
            // every shot is snapped onto the volley axis, because a rank that
            // fans out is not a rank. `width` is what separates them, so a
            // spazer with no width is just one shot fired several times on top
            // of itself - the builder fills one in rather than let that ship.
            None,
        }

        public enum After
        {
            // Stop steering at the focus. The pellets cross and diverge -
            // the classic X.
            Spread,

            // Turn onto the volley's axis instead, so past the waist the
            // pellets run as a tight parallel lane.
            Parallel,
        }

        public Focus focus = Focus.Cursor;

        // Distance mode: how far ahead the pellets meet. Also the fallback
        // for cursor and target mode when there is no crosshair / nothing in
        // range.
        public float distance = 10f;

        // LOBBED weapons only - the spread of ARC HEIGHTS across the volley,
        // in degrees.
        //
        // A lobbed volley cannot use the weapon's angular spread the way a
        // straight one does: a grenade thrown at or below horizontal can
        // never reach a target at the same height, at any speed, so half the
        // fan would simply be unsolvable. Instead every pellet is aimed
        // horizontally at the focal point and given a different LOFT, from
        // the minimum-energy angle upward. They all land on the spot; the
        // lofted ones just take longer to get there.
        //
        // Defaults to the weapon's own `spread` at build time, so the same
        // JSON keeps working and the number keeps meaning "how varied is
        // this volley".
        public float loftSpread = 30f;

        // Never loft past this. At 90 degrees the shot goes straight up and
        // there is no ballistic solution at all; the sim starts failing
        // around 88 for an uphill target, so this keeps a margin.
        public float loftMax = 82f;

        // Target mode: how far to look for something to converge on.
        public float range = 14f;

        // Optional lateral spread at the muzzle, in world units, measured end
        // to end across the rank. 0 (the default) means every pellet leaves
        // from the barrel like normal and the cone alone does the shaping;
        // anything above that spawns the pellets abreast on a line square to
        // the aim, so the volley leaves as a wall and collapses to the point.
        public float width;

        // Ceiling on how fast a pellet may swing, in degrees per second.
        // 0 = whatever the arc needs, which is the whole point of the feature
        // and is what you want unless a very wide spread is producing turns
        // sharp enough to look silly.
        public float maxTurnRate;

        public After after = After.Spread;

        // Rotate the sprite to match the heading. Needed for any art with a
        // nose; harmless on a round pellet.
        public bool faceTravel = true;

        // Runtime state, set by ForgeConvergePatch at Shoot.
        [System.NonSerialized] public bool shot;
        [System.NonSerialized] public Vector2 axis;
        [System.NonSerialized] public Vector2 focusPoint;
        [System.NonSerialized] public Transform target;
        [System.NonSerialized] public bool arrived;
        [System.NonSerialized] public float lastTurn;
    }
}
