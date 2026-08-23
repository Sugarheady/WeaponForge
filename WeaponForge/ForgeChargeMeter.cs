using UnityEngine;

namespace WeaponForge
{
    // The growing orb at the barrel that shows a charge building.
    //
    // Deliberately NOT parented to the barrel. Parenting would inherit the
    // ship's scale, and the whole point of this object is that its scale is
    // the reading - a ship scaled by anything at all would silently make the
    // meter lie. It is positioned every frame instead, which costs one
    // transform write and cannot be wrong.
    public class ForgeChargeMeter : MonoBehaviour
    {
        private SpriteRenderer _sr;

        public static ForgeChargeMeter Create(ForgeCharge.Config cfg)
        {
            if (cfg == null || cfg.meterArt == null)
                return null;

            var go = new GameObject("Forge Charge Meter");
            var meter = go.AddComponent<ForgeChargeMeter>();

            meter._sr = go.AddComponent<SpriteRenderer>();
            meter._sr.sprite = cfg.meterArt;

            if (cfg.meterMaterial != null)
                meter._sr.sharedMaterial = cfg.meterMaterial;

            meter._sr.sortingLayerID = cfg.meterSortingLayerId;
            meter._sr.sortingOrder = cfg.meterSortingOrder;
            meter._sr.enabled = false;

            return meter;
        }

        // t is the charge, 0..1. The orb grows meterFrom -> meterTo and, if a
        // charged colour was asked for, crossfades from the meter colour into
        // it - so "full" is legible without reading a number.
        public void Show(
            Vector2 position, Vector2 aim, float t, ForgeCharge.Config cfg)
        {
            if (_sr == null)
                return;

            _sr.enabled = true;

            transform.position = position;
            transform.right = aim;
            transform.localScale =
                Vector3.one * Mathf.LerpUnclamped(cfg.meterFrom, cfg.meterTo, t);

            Color c = cfg.hasMeterColor ? cfg.meterColor : Color.white;

            if (cfg.hasColor)
                c = Color.Lerp(c, cfg.color, t);

            // Fade in over the first stretch of the hold, so a charge that has
            // barely started does not pop a full-brightness dot onto the
            // barrel of a weapon you only tapped.
            c.a *= Mathf.Clamp01(t * 4f);

            _sr.color = c;
        }

        // Guarded: an idle charge weapon reaches this every frame, and
        // assigning Renderer.enabled dirties the renderer even when the value
        // has not changed.
        public void Hide()
        {
            if (_sr != null && _sr.enabled)
                _sr.enabled = false;
        }
    }
}
