using UnityEngine;

namespace WeaponForge
{
    // "When this thing hits something, make a noise."
    //
    // WHAT THE GAME ALREADY DOES, which is easy to miss: every one of the 37
    // enemy Unit prefabs wires its HealthBase.onDamage UnityEvent to an
    // SfxPlayer, so an enemy already yelps when it takes damage. That call
    // comes from Unity's serialized event system rather than from C#, so it
    // does not appear at any AudioManager.PlaySfx call site and a search of
    // the code alone concludes - wrongly - that nothing makes a sound.
    //
    // WHAT IT DOES NOT DO: the sound belongs to the VICTIM, not the weapon,
    // so every gun sounds the same hitting the same enemy. And hitting
    // TERRAIN makes no sound at all unless the cell is destroyed outright
    // (CellType.destroySfx).
    //
    // So this is a per-weapon layer, and "where should it play" is a real
    // choice rather than a detail - hence Where below.
    //
    // The settings ride on the prefab so each shot carries its own copy:
    //   * projectiles - added to the cloned projectile prefab, so every
    //     instantiated shot gets its own cooldown;
    //   * hitscan - added to the cloned beam VISUAL prefab, where a single
    //     shared cooldown is what we want anyway (one beam, one weapon).
    public class ForgeImpact : MonoBehaviour
    {
        // Which impacts this sound is for.
        //   Any      both. The sound layers over the enemy's own yelp.
        //   Enemy    only things that take damage - no wall noise.
        //   Terrain  only walls, where the game is otherwise silent. Use
        //            this to avoid doubling up with the enemy's own sound.
        public enum Where { Any, Enemy, Terrain }

        // A guid the game's AudioDatabase knows - either a stock one or a
        // custom sound registered by ForgeSfxRegistry.
        public string sfx;

        public Where where = Where.Any;

        // Resolved once at build time rather than per hit: LayerMask.NameToLayer
        // is a string lookup and this runs inside a collision path.
        public int groundLayer = -1;

        // Does this hit qualify? layer is the layer of whatever was struck.
        public bool Wants(int layer)
        {
            if (where == Where.Any)
                return true;

            bool isGround = (groundLayer >= 0 && layer == groundLayer);

            return (where == Where.Terrain) ? isGround : !isGround;
        }

        // Smallest gap between two impact sounds from this shot.
        //
        // This is load-bearing, not a nicety. The plain-projectile hook is
        // the game's own per-frame collision entry point, and a PIERCING
        // shot sitting inside an enemy re-enters it every FixedUpdate. Left
        // ungated that is fifty sounds a second - the same failure the
        // deflector had when its preset shipped without damageRepeatDelay.
        public float minInterval = 0.08f;

        private float _next;

        // True at most once per minInterval. Stamps the next allowed time as
        // a side effect, so callers must only ask when they intend to play.
        public bool Ready()
        {
            if (Time.time < _next)
                return false;

            _next = Time.time + Mathf.Max(0f, minInterval);
            return true;
        }

        public void PlayAt(Vector2 position)
        {
            if (string.IsNullOrEmpty(sfx))
                return;

            // Positional on purpose: registered as a real Sfx, so it gets 3D
            // panning, the volume slider and the mixer for free.
            AudioManager.PlaySfx(sfx, position);
        }
    }
}
