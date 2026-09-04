using UnityEngine;

namespace WeaponForge
{
    // Rides the MINION and says how its shield bubble should be painted.
    //
    // It cannot ride the Shield itself, and that is the whole reason this
    // exists as a component rather than a one-time write.
    // DamagableResource.OnStatsRecalculated DESTROYS every Shield and
    // instantiates fresh ones on every stat recalculation - which happens
    // whenever a module changes, a level is boosted, or anything else touches
    // the unit. Anything stored on a Shield is gone by the next recalc.
    //
    // So the colour lives on the unit, which survives, and the patch re-reads
    // it. Same reasoning as ForgeConfigTable: put the state on the thing that
    // lasts, not on the thing the game rebuilds behind your back.
    public class ForgeMinionShield : MonoBehaviour
    {
        // Which shield to paint. A unit can carry several (one per ShieldData,
        // each a ring one unit larger than the last), so this keeps us off
        // shields we were not asked about. Null means "whichever it has".
        [System.NonSerialized] public Resource resource;

        [System.NonSerialized] public Color color = Color.white;
    }

    // Caches the resolved material on the Shield instance itself.
    //
    // Shield.Update runs every frame for every live shield in the level, so
    // the postfix must not do a component walk each time. This dies with the
    // Shield it is attached to, which is exactly the lifetime we want - a
    // rebuilt shield gets a fresh one and re-resolves.
    internal class ForgeShieldPaint : MonoBehaviour
    {
        internal Material material;
        internal bool resolved;

        internal Material Resolve(Shield shield)
        {
            if (resolved)
                return material;

            resolved = true;

            // The MeshRenderer is on a "Mesh" CHILD of the shield prefab, not
            // on the Shield object, so this has to search children.
            //
            // Reading .material is safe rather than leaky here: Shield.Awake
            // already did `_material = renderer.material`, which is what
            // instantiates the per-renderer copy. Every later read returns
            // that same instance, so this paints the exact material the
            // shield's own Update is writing to - and there is no second
            // material to leak. (Shield.OnDestroy destroys it.)
            var r = shield.GetComponentInChildren<MeshRenderer>(true);

            material = (r != null) ? r.material : null;

            return material;
        }
    }
}
