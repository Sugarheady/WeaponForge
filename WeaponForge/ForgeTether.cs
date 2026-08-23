using System.Collections.Generic;
using UnityEngine;

namespace WeaponForge
{
    // HARPOON / TETHER - a shot that latches on and leaves a rope behind.
    //
    // WHAT THE GAME HAS, and why almost none of it is reusable: there IS a
    // Hook, and it is not a harpoon. It fires nothing. It instantly latches
    // onto the nearest `Grabbable` inside a cone in front of the ship, and it
    // is a SPRING LEASH - force is applied only once the line is stretched
    // past its rest length, to BOTH ends, with a closing-speed cap so it does
    // not oscillate. And `Grabbable` is read by nothing except the hook
    // itself: no enemy carries one, so it is for physics junk, not combat.
    //
    // What IS worth taking from it is the force model, which is proven in this
    // game and is what makes a rope feel taut instead of magnetic. ForgeTether
    // mirrors it exactly and then adds the three things a weapon needs: being
    // fired, latching onto anything with a rigidbody, and a choice about which
    // end of the rope does the moving.
    //
    // The latching half was already built - it is the same park-and-ride-the-
    // host that sticky bombs use.
    public class ForgeTether : MonoBehaviour
    {
        public enum To { Any, Terrain, Enemies }

        // Which end of the rope moves.
        //
        //   Target  drag them to you. Rips things out of cover and into
        //           whatever else you are firing.
        //   Self    you get yanked to them. A traversal tool rather than a
        //           damage one, and the only mode that wants to latch onto
        //           TERRAIN.
        //   Both    the game's own hook: an elastic line neither end can
        //           leave. Chaotic, and the most physical-feeling.
        //   Anchor  no pulling at all - the target simply cannot get further
        //           than `length` from WHERE IT WAS STRUCK. Crowd control
        //           rather than repositioning, and the one mode where the
        //           harpoon head stays put instead of riding its victim.
        public enum Pull { Target, Self, Both, Anchor }

        public Pull pull = Pull.Target;
        public To latchTo = To.Enemies;

        // Rest length. No force at all inside this - that is what makes it a
        // rope rather than a magnet.
        public float length = 4f;

        public float stiffness = 40f;

        // Multiplier on the force applied to YOU. Only consulted in Both;
        // Self uses the full stiffness.
        public float selfPull = 0.4f;

        // Stop pulling once the gap is already closing this fast, so a taut
        // rope settles instead of shuddering. The game's own hook does this
        // and it is the difference between a tether and a spasm.
        public float maxClosingSpeed = 6f;

        // Units per second the rest length shrinks by - an active winch. 0
        // leaves the rope a fixed length.
        public float reelSpeed;

        public float duration = 4f;      // 0 = until something else ends it
        public float breakDistance;      // 0 = never snaps from stretch
        public float damagePerSecond;

        public int max = 1;

        public float ropeWidth = 0.13f;
        public bool hasRopeColor;
        public Color ropeColor = Color.white;

        // Borrowed at build time from the game's own electricity beam, which
        // is the only stock LineRenderer material to hand. A fresh
        // LineRenderer gets Unity's default-line material, which ignores the
        // colour we set - the same trap a fresh ParticleSystem has.
        public Material ropeMaterial;

        public int groundLayer = -1;

        // ---- runtime -----------------------------------------------------
        // How long an UNLATCHED harpoon may stay in the world.
        //
        // The builder switches impactBehaviour, lifetimeData and
        // destroyWhenReached all off, because any one of them would delete a
        // harpoon mid-rope. The cost of that, found in R6 test 12, is that a
        // MISSED harpoon has nothing left to end it - it sits wherever it
        // stopped, indefinitely, and enemies wandering into it get hooked by a
        // shot the player fired minutes ago. This is the replacement timer, and
        // it only runs while the shot has hit nothing.
        public float missLifetime = 3f;

        [System.NonSerialized] public bool latched;
        [System.NonSerialized] public float latchedAt;
        [System.NonSerialized] public float firedAt;
        [System.NonSerialized] public Transform host;
        [System.NonSerialized] public Rigidbody2D hostBody;
        [System.NonSerialized] public Vector3 hostOffset;
        [System.NonSerialized] public Vector2 anchorPoint;
        [System.NonSerialized] public Unit owner;
        [System.NonSerialized] public float restLength;

        private static readonly List<ForgeTether> _live =
            new List<ForgeTether>();

        public static IList<ForgeTether> Live
        {
            get { return _live; }
        }

        public bool Wants(int layer)
        {
            if (latchTo == To.Any)
                return true;

            bool isGround = (groundLayer >= 0 && layer == groundLayer);

            return (latchTo == To.Terrain) ? isGround : !isGround;
        }

        public void Register()
        {
            if (!_live.Contains(this))
                _live.Add(this);
        }

        // Stamped on the instance, not at Shoot, so a shot can never be born
        // already past its deadline. The prefab's own field is irrelevant.
        private void OnEnable()
        {
            firedAt = Time.time;
        }

        // The replacement for the three timers the builder had to switch off.
        // Only ever ends a shot that has hit NOTHING - a latched harpoon is the
        // controller's business and has its own `duration`.
        private void Update()
        {
            if (latched || missLifetime <= 0f)
                return;

            if (Time.time - firedAt < missLifetime)
                return;

            Destroy(gameObject);
        }

        private void OnDestroy()
        {
            _live.Remove(this);
        }

        // Keep one owner's ropes to `max`, oldest first.
        public static void Cull(Unit unit, ForgeTether keep, int max)
        {
            if (max < 1)
                max = 1;

            int count = 0;

            for (int i = 0; i < _live.Count; i++)
            {
                ForgeTether t = _live[i];

                if (t != null && t.latched &&
                    (unit == null || ReferenceEquals(t.owner, unit)))
                {
                    count++;
                }
            }

            for (int i = 0; i < _live.Count && count > max; i++)
            {
                ForgeTether t = _live[i];

                if (t == null || !t.latched || ReferenceEquals(t, keep))
                    continue;

                if (unit != null && !ReferenceEquals(t.owner, unit))
                    continue;

                Object.Destroy(t.gameObject);
                count--;
            }
        }
    }
}
