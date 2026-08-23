using System.Collections.Generic;
using UnityEngine;

namespace WeaponForge
{
    // STICKY BOMBS - shots that stop where they land, hang there, and go off
    // together on command.
    //
    // Nothing in the game does any part of this. There is no stick, no attach,
    // no joint, and no "detonate the ones you already fired" - the closest
    // thing is a grapple hook that pulls with AddForce and never attaches
    // anything to anything.
    //
    // TWO FACTS THAT MADE IT CHEAP, both from reading the collision code:
    //
    //   A plain Projectile with impactBehaviour OFF already survives contact.
    //   OnObjectHit snaps it to the surface, applies knockback, checks bounce,
    //   checks impact - and with both of those off it simply falls out of the
    //   method without destroying anything. So "stick to a wall" is mostly
    //   "stop moving".
    //
    //   A PhysicsProjectile with impactBehaviour off keeps bouncing as a real
    //   rigidbody (that is what the bounce feature shapes). Sticking one is
    //   therefore the opposite job: take the physics AWAY at the moment of
    //   contact, which is a single `simulated = false`.
    //
    // A plain MonoBehaviour rather than a patch, like ForgeTurret: Unity
    // drives Update for the fuse, the proximity check and riding a moving
    // host, and Harmony is only needed for the two moments the game owns -
    // the collision, and the trigger pull that sets them all off.
    public class ForgeSticky : MonoBehaviour
    {
        public enum To { Any, Terrain, Enemies }

        // What sets a salvo off.
        //
        //   Refire     lay up to `max`, and the next shot after that
        //              detonates the lot instead of laying another. One
        //              button, nothing hidden, and it gives the weapon a
        //              rhythm - load, load, load, BANG.
        //   Fuse       each bomb goes off `fuse` seconds after IT stuck, so
        //              the weapon keeps firing normally and the bombs are a
        //              delayed-action carpet. The plasma-grenade version.
        //   Proximity  goes off when something hostile comes within
        //              proximityRange. A minefield.
        //   Manual     only `fuse` or the max-count cull ever ends one. For a
        //              weapon that wants to define its own rules.
        public enum Trigger { Refire, Fuse, Proximity, Manual }

        public To stickTo = To.Any;
        public Trigger trigger = Trigger.Refire;

        // Seconds after sticking before it blows on its own. 0 = never, which
        // is what you want for Refire.
        public float fuse;

        // Most bombs one owner may have stuck at once. Laying past this
        // detonates the OLDEST rather than refusing the shot - a weapon that
        // silently stops working is worse than one that cleans up after
        // itself.
        public int max = 8;

        // A bomb cannot be detonated for this long after it lands. Without it
        // a Refire weapon fired twice quickly blows the bomb up on the muzzle,
        // and a Proximity one stuck to the enemy that is already touching you
        // goes off in your face.
        public float armTime = 0.25f;

        // Stagger between bombs when a salvo goes off, in seconds. 0 is one
        // bang; 0.08 is a ripple down the line, which reads as much bigger.
        public float ripple;

        public float proximityRange = 2.5f;

        // Ride whatever it stuck to. Done by remembering the host and a local
        // offset rather than by re-parenting: parenting would inherit the
        // host's scale, and a bomb on a destroyed host would be destroyed with
        // it mid-flight rather than being left behind.
        public bool follow = true;

        // Whether the landing itself hurts. After sticking, the bomb's own
        // contact damage is switched off either way - a shot parked against a
        // wall re-triggers the game's hit path forever otherwise, and a bomb
        // that quietly grinds an enemy down is not a bomb.
        public bool damageOnStick = true;

        public int groundLayer = -1;

        // ---- runtime -----------------------------------------------------
        [System.NonSerialized] public bool stuck;
        [System.NonSerialized] public float stuckAt;
        [System.NonSerialized] public Transform host;
        [System.NonSerialized] public Vector3 hostOffset;
        [System.NonSerialized] public Unit owner;
        [System.NonSerialized] public float blowAt = -1f;

        // Every stuck bomb in the level. Static because "detonate them
        // together" is inherently a question about a SET of projectiles, and
        // there is nowhere else that set could live - a projectile knows only
        // itself.
        private static readonly List<ForgeSticky> _live =
            new List<ForgeSticky>();

        public static IList<ForgeSticky> Live
        {
            get { return _live; }
        }

        public bool Armed
        {
            get { return stuck && Time.time >= stuckAt + armTime; }
        }

        public bool Wants(int layer)
        {
            if (stickTo == To.Any)
                return true;

            bool isGround = (groundLayer >= 0 && layer == groundLayer);

            return (stickTo == To.Terrain) ? isGround : !isGround;
        }

        public void Register()
        {
            if (!_live.Contains(this))
                _live.Add(this);
        }

        private void OnDestroy()
        {
            _live.Remove(this);
        }

        // Every bomb this unit has stuck, oldest first - the registry is
        // append-ordered, so no sorting is needed.
        public static void Collect(Unit unit, List<ForgeSticky> into)
        {
            into.Clear();

            for (int i = 0; i < _live.Count; i++)
            {
                ForgeSticky s = _live[i];

                if (s == null || !s.stuck)
                    continue;

                // ReferenceEquals: a Unit destroyed between runs is
                // Unity-fake-null but still a usable key.
                if (unit == null || ReferenceEquals(s.owner, unit))
                    into.Add(s);
            }
        }
    }
}
