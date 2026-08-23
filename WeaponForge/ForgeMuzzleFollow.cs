using UnityEngine;

namespace WeaponForge
{
    // Marker: this muzzle flash should RIDE the barrel instead of being
    // stamped where it was last fired.
    //
    // The game creates one flash emitter per pellet in WeaponBase and
    // repositions it on every shot:
    //
    //     muzzleParticleSystemInstances[n].transform.SetPositionAndRotation(
    //         barrel.Position + dir * BarrelLength,
    //         Quaternion.FromToRotation(Vector2.right, dir));
    //
    // Note what that is NOT: it is not parented to anything. The emitter is
    // loose in the world and only ever moves at the instant you pull the
    // trigger. Every stock prefab uses LOCAL simulation space
    // (moveWithTransform: 1), so the particles do travel with the emitter -
    // which means a fast weapon re-stamping 30 times a second already looks
    // welded to the gun, and a slow one leaves its flash hanging in the air
    // the moment you move.
    //
    // Parenting the emitter to the barrel fixes the slow case and makes the
    // fast case honest rather than coincidental.
    public class ForgeMuzzleFollow : MonoBehaviour
    {
    }
}
