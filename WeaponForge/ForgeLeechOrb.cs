using System;
using BepInEx.Logging;
using UnityEngine;

namespace WeaponForge
{
    // The absorb orb: a bubble that leaves the enemy you hit and flies into your
    // ship. Cosmetic by construction - it is a GameObject with a SpriteRenderer
    // and nothing else, so it has no collider, no faction, no layer politics and
    // no way to hurt anything.
    //
    // IT IS NOT A PROJECTILE AND DELIBERATELY SO. A `Projectile` would arrive
    // with damage 0, radius 0, no lifetime and a collision mask of zero unless
    // every field were copied by hand (nothing on that class is serialized), and
    // it would then be visible to every projectile patch in the mod - growth,
    // homing, ricochet, the tracker. None of that is wanted for a decoration.
    // A bare GameObject cannot be caught by any of it.
    //
    // IT TRACKS THE SHIP rather than flying at where the ship was. A fixed
    // heading means a moving player outruns their own healing, which reads as
    // the feature being broken.
    public class ForgeLeechOrb : MonoBehaviour
    {
        private static readonly ManualLogSource Log =
            ForgeLog.Source("WeaponForge.Leech");

        [NonSerialized] private Transform _target;
        [NonSerialized] private Unit _player;
        [NonSerialized] private Resource _resource;
        [NonSerialized] private float _payload;
        [NonSerialized] private string _sfxGuid;

        [NonSerialized] private Vector3 _velocity;
        [NonSerialized] private float _speed;
        [NonSerialized] private float _spin;
        [NonSerialized] private float _dieAt;
        [NonSerialized] private bool _paid;
        [NonSerialized] private bool _counted;

        // How hard it curves in. A dead-straight line reads as a bullet being
        // fired at you, which is the one thing this must not look like.
        private const float TurnRate = 6f;

        // Close enough to count as absorbed. The ship is about this wide, so
        // waiting for zero would have the orb sit inside the hull for a frame.
        private const float ArriveWithin = 0.7f;

        // `payload` of 0 is legitimate and normal: with "healOn": "hit" the
        // health has already gone in and the orb is pure garnish.
        public static ForgeLeechOrb Spawn(
            Vector3 from,
            Unit player,
            ForgeLeech.Config cfg,
            Resource resource,
            float payload,
            Component victim)
        {
            if (player == null || cfg == null ||
                cfg.orbFrames == null || cfg.orbFrames.Length == 0)
            {
                return null;
            }

            try
            {
                var go = new GameObject("Forge Leech Orb");
                go.transform.position = from;
                go.transform.localScale =
                    Vector3.one * Mathf.Max(0.01f, cfg.orbScale);

                var sr = go.AddComponent<SpriteRenderer>();
                sr.sprite = cfg.orbFrames[0];

                if (cfg.orbHasColor)
                    sr.color = cfg.orbColor;

                // A fresh SpriteRenderer lands on sorting layer "Default" at
                // order 0, which in this game is BEHIND the terrain - the orb
                // would be invisible for most of its flight. Borrow the layer
                // from something already drawing at the right depth, preferring
                // the thing we just hit (it is on screen by definition).
                CopySorting(sr, victim, player);

                if (cfg.orbFrames.Length > 1)
                {
                    var anim = go.AddComponent<ForgeSpriteAnimation>();
                    anim.frames = cfg.orbFrames;
                    anim.fps = Mathf.Max(1f, cfg.orbFps);
                    anim.loop = ForgeSpriteAnimation.LoopMode.Loop;
                    anim.randomStart = true;
                }

                if (cfg.orbTrail != null && cfg.orbTrail.enabled)
                    ForgeTrail.Apply(go, cfg.orbTrail);

                var orb = go.AddComponent<ForgeLeechOrb>();
                orb._target = player.transform;
                orb._player = player;
                orb._resource = resource;
                orb._payload = payload;
                orb._sfxGuid = cfg.sfxGuid;
                orb._speed = Mathf.Max(1f, cfg.orbSpeed);
                orb._spin = cfg.orbSpin;
                orb._dieAt = Time.time + Mathf.Max(0.2f, cfg.orbLifetime);
                orb._counted = true;

                // The initial sideways kick is what makes it swoop rather than
                // beeline. Signed at random so a stream of orbs fans out instead
                // of tracing one line.
                Vector3 toShip = player.transform.position - from;

                if (toShip.sqrMagnitude < 0.0001f)
                    toShip = Vector3.up;

                Vector3 dir = toShip.normalized;
                Vector3 perp = new Vector3(-dir.y, dir.x, 0f);

                float swoop = Mathf.Max(0f, cfg.orbArc) *
                              (UnityEngine.Random.value < 0.5f ? -1f : 1f);

                orb._velocity = (dir + perp * swoop).normalized * orb._speed;

                return orb;
            }
            catch (Exception e)
            {
                Log.LogWarning("Spawning the absorb orb failed: " + e);
                return null;
            }
        }

        private static void CopySorting(
            SpriteRenderer sr, Component victim, Unit player)
        {
            SpriteRenderer donor = null;

            if (victim != null)
                donor = victim.GetComponentInChildren<SpriteRenderer>(true);

            if (donor == null && player != null)
                donor = player.GetComponentInChildren<SpriteRenderer>(true);

            if (donor == null)
                return;

            sr.sortingLayerID = donor.sortingLayerID;

            // One in front of the donor: the orb leaving an enemy should be
            // visible over it, not tucked behind its art.
            sr.sortingOrder = donor.sortingOrder + 1;
        }

        private void Update()
        {
            // The ship died, or was destroyed for any other reason. Nothing to
            // fly to and nobody to heal.
            if (_target == null || _player == null)
            {
                Destroy(gameObject);
                return;
            }

            float dt = Time.deltaTime;

            Vector3 toShip = _target.position - transform.position;
            float distance = toShip.magnitude;

            if (distance <= ArriveWithin || Time.time >= _dieAt)
            {
                Arrive();
                return;
            }

            // Steer toward the ship rather than snapping to it, so the initial
            // sideways kick reads as a curve. RotateTowards keeps the speed
            // constant and always converges, so an orb can never orbit forever.
            Vector3 want = toShip / distance;

            _velocity = Vector3.RotateTowards(
                _velocity.normalized, want, TurnRate * dt, 0f) * _speed;

            transform.position += _velocity * dt;

            if (_spin != 0f)
                transform.Rotate(0f, 0f, _spin * dt);
        }

        // Pays out and dies. Also called on the lifetime backstop, because an
        // orb that cannot reach you is otherwise LOST HEALING - and healing that
        // silently does not arrive is the worst failure this feature has.
        private void Arrive()
        {
            if (!_paid)
            {
                _paid = true;

                if (_payload > 0f)
                    ForgeLeech.Heal(_player, _resource, _payload);

                if (!string.IsNullOrEmpty(_sfxGuid))
                {
                    try
                    {
                        AudioManager.PlaySfx(
                            _sfxGuid, transform.position);
                    }
                    catch (Exception)
                    {
                        // A missing sound must never cost the healing.
                    }
                }
            }

            Destroy(gameObject);
        }

        // Every exit path goes through here - arrival, the backstop, the ship
        // dying, a scene teardown - so the in-flight count cannot drift upward
        // and silently stop new orbs spawning for the rest of the run.
        private void OnDestroy()
        {
            if (!_counted)
                return;

            _counted = false;
            ForgeLeech.ReleaseOrb();
        }
    }
}
