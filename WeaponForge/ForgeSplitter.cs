using UnityEngine;

namespace WeaponForge
{
    // Rides the projectile prefab and owns the triggers Unity can drive by
    // itself - "time" and "distance" from Update, "death" from OnDestroy. The
    // rest ("hit", "bounce", and anything added later) come in through
    // ForgeSplit.Fire from their own patch.
    //
    // OnDestroy is deliberately the death hook rather than a patch on each of
    // the game's three destroy sites (HandleRange, HandleLifetime,
    // OnObjectHit). It catches all three at once AND every mod-side ending too
    // - a bounce running out of bounces, a catalyst consuming a shot, anything
    // built later - which is the flexibility this feature was asked for. The
    // cost is that OnDestroy also runs during teardown, so it is guarded three
    // ways: the splitter must have been ARMED (only a real fired instance runs
    // Start; a HideAndDontSave prefab clone never does), the scene must still
    // be loaded (false while a level unloads), and the game must not be
    // quitting.
    public class ForgeSplitter : MonoBehaviour
    {
        // Unity serializes ints, so these survive Instantiate. A Config
        // REFERENCE would NOT - that is the bug that silently disabled the
        // whole gas and catalyst blocks - so the prefab carries an id and the
        // real config is looked back up in Awake.
        //
        // The prefab a fragment is built from lives on that Config rather than
        // here, and deliberately so: Unity REMAPS Object references that point
        // inside the hierarchy it is copying, so a `public Projectile
        // sourcePrefab` pointing at its own prefab would come back on the clone
        // pointing at THE CLONE. A second-generation fragment would then copy
        // itself, which is the dying-object problem this design exists to
        // avoid. An int is never remapped.
        public int cfgId = -1;
        public int generation;

        [System.NonSerialized] public ForgeSplit.Config cfg;
        [System.NonSerialized] public int used;

        [System.NonSerialized] private bool armed;
        [System.NonSerialized] private bool consumed;
        [System.NonSerialized] private bool midDone;
        [System.NonSerialized] private float bornAt;
        [System.NonSerialized] private Vector3 origin;

        // The last heading and speed the shot actually had while it was MOVING.
        //
        // WHY THIS IS TRACKED instead of read at burst time: a shot's velocity
        // at the moment it dies is not its heading. `rangeData.slowDown`
        // decelerates a projectile to a dead stop exactly at its range and the
        // game then destroys it there - and the White Popper template, which is
        // what most weapons start from, ships slowDown ON. So a "death" split
        // read Velocity, got EXACTLY ZERO, fell back to transform.right, and
        // fanned every fragment due EAST no matter which way the player was
        // shooting. (A plain Projectile is never rotated unless rotationData
        // says so, so transform.right is world +X.) The same zero made the
        // fragments launch at the 12-unit fallback speed and then inherit
        // slowDown themselves, which is why they hung around in a clump.
        //
        // The mid-flight triggers never showed this, because there the shot is
        // still travelling - which is exactly why MIRV looked perfect while the
        // death split looked broken.
        [System.NonSerialized] private Vector2 lastHeading;
        [System.NonSerialized] private float lastSpeed;

        // Below this a shot is coasting to a halt rather than travelling, and
        // its direction is no longer meaningful.
        private const float MovingSpeed = 0.5f;

        public bool HasHeading
        {
            get { return lastHeading.sqrMagnitude > 0.000001f; }
        }

        public Vector2 LastHeading
        {
            get { return lastHeading; }
        }

        public float LastSpeed
        {
            get { return lastSpeed; }
        }

        public bool Ready
        {
            get { return armed && !consumed && cfg != null; }
        }

        // Awake, not Start: it must beat Shoot and every Harmony patch, and it
        // must not depend on which of them Harmony happens to order first.
        private void Awake()
        {
            cfg = ForgeConfigTable.Get<ForgeSplit.Config>(cfgId);
        }

        private void Start()
        {
            Rearm(transform.position);
        }

        public void Rearm(Vector3 at)
        {
            armed = true;
            consumed = false;
            midDone = false;
            bornAt = Time.time;
            origin = at;
        }

        private void OnDestroy()
        {
            if (!Ready || ForgeSplit.Quitting)
                return;

            // A level unloading destroys every projectile in it. Splitting
            // there would spawn fragments into a scene that is going away.
            if (!gameObject.scene.isLoaded)
                return;

            ForgeSplit.Fire(Shot(), this, ForgeSplit.OnDeath);
        }

        private void Update()
        {
            if (!Ready)
                return;

            // Remembered every frame, before any trigger can consume the shot -
            // a death split reads this rather than a velocity that may already
            // have decayed to nothing.
            Track();

            if (midDone)
                return;

            if (cfg.on.Contains(ForgeSplit.OnTime) &&
                Time.time - bornAt >= Mathf.Max(0.02f, cfg.delay))
            {
                Trigger(ForgeSplit.OnTime);
                return;
            }

            if (cfg.on.Contains(ForgeSplit.OnDistance) &&
                (transform.position - origin).sqrMagnitude >=
                    cfg.distance * cfg.distance)
            {
                Trigger(ForgeSplit.OnDistance);
            }
        }

        // The mid-flight triggers consume the parent by default - a MIRV that
        // keeps flying after it opens is a different weapon, and one that also
        // had "death" on would then split twice for one shot. splitKeepParent
        // turns that off, at which point both meters are re-based so "split
        // every 6 units" means every 6 units rather than once and then every
        // frame forever.
        private void Trigger(string reason)
        {
            Component shot = Shot();

            if (shot == null)
                return;

            if (!ForgeSplit.Fire(shot, this, reason))
            {
                // Out of budget or past the generation cap. Stop testing the
                // mid-flight triggers - but leave the death trigger armed, or
                // a spent MIRV would lose its final burst too.
                midDone = true;
                return;
            }

            if (cfg.consumeParent)
            {
                consumed = true;
                Destroy(gameObject);
            }
            else
            {
                bornAt = Time.time;
                origin = transform.position;
            }
        }

        private void Track()
        {
            var ip = Shot() as IProjectile;

            if (ip == null)
                return;

            Vector2 v = ip.Velocity;
            float speed = v.magnitude;

            if (speed <= MovingSpeed)
                return;

            lastHeading = v / speed;
            lastSpeed = speed;
        }

        private Component Shot()
        {
            Component c = GetComponent<Projectile>();

            if (c == null)
                c = GetComponent<PhysicsProjectile>();

            return c;
        }
    }
}
