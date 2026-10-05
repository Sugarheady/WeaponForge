using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;

namespace WeaponForge
{
    // The living half of a sticky bomb: what happens between landing and
    // going off. Unity drives this, so no patch is involved - the same shape
    // as ForgeTurret.
    //
    // Added to the shot at the moment it sticks rather than sitting on the
    // prefab, so a bomb still in flight costs nothing per frame.
    public class ForgeStickyController : MonoBehaviour
    {
        private static readonly ManualLogSource Log =
            ForgeLog.Source("WeaponForge.Sticky");

        private ForgeSticky s;
        private ContactFilter2D enemies;
        private bool filtersReady;
        private readonly List<Collider2D> buffer = new List<Collider2D>();

        // Never let a bomb outlive the run. A Refire weapon with no fuse
        // relies entirely on the player pressing the trigger again, and a
        // player who swaps weapons never will.
        private const float HardLife = 60f;

        public void Begin(ForgeSticky sticky)
        {
            s = sticky;
        }

        private void Update()
        {
            if (s == null)
            {
                Destroy(this);
                return;
            }

            // Ride the host. Position rather than parenting: parenting would
            // inherit an enemy's scale, and a bomb parented to something that
            // dies would be destroyed along with it instead of dropping off.
            if (s.follow && s.host != null)
                transform.position = s.host.TransformPoint(s.hostOffset);

            if (Time.time - s.stuckAt > HardLife)
            {
                Detonate(s);
                return;
            }

            // A scheduled detonation from a salvo ripple.
            if (s.blowAt >= 0f && Time.time >= s.blowAt)
            {
                Detonate(s);
                return;
            }

            if (s.fuse > 0f && Time.time >= s.stuckAt + s.fuse)
            {
                Detonate(s);
                return;
            }

            if (s.trigger == ForgeSticky.Trigger.Proximity && s.Armed)
                CheckProximity();
        }

        private void CheckProximity()
        {
            if (!filtersReady)
            {
                filtersReady = true;
                enemies = new ContactFilter2D();
                enemies.useTriggers = true;
                enemies.SetLayerMask(LayerMask.GetMask("Entities", "Fruits"));
            }

            int n = Physics2D.OverlapCircle(
                transform.position, s.proximityRange, enemies, buffer);

            for (int i = 0; i < n; i++)
            {
                if (buffer[i] == null)
                    continue;

                Unit u = buffer[i].GetComponentInParent<Unit>();

                // Never trip on the ship that planted it, or its friends -
                // a mine you cannot walk past is a trap for the player.
                if (u != null && s.owner != null &&
                    (ReferenceEquals(u, s.owner) || s.owner.IsFriendsWith(u)))
                {
                    continue;
                }

                if (buffer[i].GetComponentInParent<HealthBase>() == null)
                    continue;

                Detonate(s);
                return;
            }
        }

        // ------------------------------------------------------------------

        // Set a whole salvo off, oldest first, optionally rippling.
        public static int DetonateAll(Unit unit, List<ForgeSticky> scratch)
        {
            ForgeSticky.Collect(unit, scratch);

            int n = 0;

            for (int i = 0; i < scratch.Count; i++)
            {
                ForgeSticky s = scratch[i];

                if (s == null || !s.Armed || s.blowAt >= 0f)
                    continue;

                if (s.ripple > 0f)
                {
                    // Scheduled rather than immediate: a line of bombs going
                    // off in sequence reads as far bigger than the same bombs
                    // going off on one frame, and it costs one float.
                    s.blowAt = Time.time + s.ripple * n;
                }
                else
                {
                    Detonate(s);
                }

                n++;
            }

            return n;
        }

        public static void Detonate(ForgeSticky s)
        {
            if (s == null)
                return;

            try
            {
                Explode(s.gameObject);
            }
            catch (System.Exception e)
            {
                Log.LogError("Sticky detonation failed: " + e);
            }

            UnityEngine.Object.Destroy(s.gameObject);
        }

        private static void Explode(GameObject go)
        {
            // Whichever kind of shot this is, the explosion data lives on the
            // projectile component - and IProjectile is the only thing the two
            // classes share.
            var proj = go.GetComponent<Projectile>();
            var phys = go.GetComponent<PhysicsProjectile>();

            IProjectile p = (proj != null)
                ? (IProjectile)proj
                : (IProjectile)phys;

            if (p == null)
                return;

            Explosion ex = p.Explosion;

            // SpawnExplosion dereferences damages[0].damageType for the blast
            // prefab and throws on an empty list. The builder refuses a sticky
            // weapon with no explosion, so this is belt and braces.
            if (ex.damages == null || ex.damages.Count == 0 || ex.radius <= 0f)
                return;

            ex.Owner = p.Owner;
            ex.Burn = p.Burn;

            ServiceLocator.Get<ExplosionManager>()
                .SpawnExplosion(go.transform.position, ex);
        }
    }
}
