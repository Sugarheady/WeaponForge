using UnityEngine;

namespace WeaponForge
{
    // Flips a SpriteRenderer through a list of frames - the animated
    // projectile art the game itself never does (every stock ammo prefab
    // is one static sprite with no Animator).
    //
    // Added to the CLONED projectile prefab, so each spawned shot animates
    // itself: no Harmony patch and no per-frame game hook, the same trick
    // RgbAnimator uses for the rainbow.
    public class ForgeSpriteAnimation : MonoBehaviour
    {
        public enum LoopMode { Loop, Once, PingPong }

        public Sprite[] frames;
        public float fps = 12f;
        public LoopMode loop = LoopMode.Loop;

        // Start each shot on a random frame. Without this a 10-pellet
        // shotgun fires ten sprites flipping in lock-step, which reads as
        // one big strobe rather than ten spinning bullets.
        public bool randomStart;

        private SpriteRenderer _renderer;
        private float _startTime;
        private int _lastIndex = -1;
        private bool _offsetDone;

        private void Awake()
        {
            // Same resolution order as VisualCustomizer.SwapSprite: the
            // stock ammo prefabs carry their renderer on the root, with
            // children being particle systems.
            _renderer = GetComponent<SpriteRenderer>();

            if (_renderer == null)
                _renderer = GetComponentInChildren<SpriteRenderer>(true);

            _startTime = Time.time;

            Offset();
            Apply();
        }

        private void Update()
        {
            // Retried here, and that is the whole point: `randomStart` used to
            // be read in Awake alone, and `AddComponent` runs Awake
            // SYNCHRONOUSLY - before either caller (VisualCustomizer.ApplyArt
            // and the leech orb) has assigned `frames`. So the offset was always
            // computed against a null array and randomStart had never once done
            // anything. The symptom it was written to prevent is exactly what
            // shipped: a 10-pellet shotgun firing ten sprites flipping in
            // lock-step, reading as one big strobe rather than ten spinning
            // bullets.
            Offset();
            Apply();
        }

        // Play from frame 0 again.
        //
        // For anything that lives as long as it animates - a projectile, a gas
        // puff - there is nothing to restart, and this is never called. It
        // exists for a renderer that is REUSED for separate appearances: the
        // swipe blade is one GameObject switched on for ~0.2s per swing, so
        // without this the second swing catches the animation at whatever phase
        // it happened to reach and a "slash that draws itself" only draws itself
        // correctly the first time. Cheap enough to call unconditionally.
        public void Restart()
        {
            _startTime = Time.time;
            _lastIndex = -1;

            // Deliberately NOT re-offsetting: `randomStart` desyncs siblings
            // from each other, and a caller asking for a restart wants frame 0,
            // not a fresh random phase.
            Apply();
        }

        // Desyncs this instance from its siblings. Runs once, as soon as there
        // is actually a frame list to measure.
        private void Offset()
        {
            if (_offsetDone || frames == null || frames.Length == 0)
                return;

            _offsetDone = true;

            if (randomStart && frames.Length > 1 && fps > 0f)
                _startTime -= Random.Range(0f, frames.Length / fps);
        }

        private void Apply()
        {
            if (_renderer == null || frames == null || frames.Length == 0)
                return;

            int index;

            if (frames.Length == 1 || fps <= 0f)
            {
                index = 0;
            }
            else
            {
                float elapsed = (Time.time - _startTime) * fps;

                switch (loop)
                {
                    case LoopMode.Once:
                        index = Mathf.Min(
                            frames.Length - 1, Mathf.FloorToInt(elapsed));
                        break;

                    case LoopMode.PingPong:
                        // Runs 0..n-1..0 without repeating the end frames,
                        // so a 4-frame ping-pong is a 6-step cycle.
                        index = Mathf.RoundToInt(
                            Mathf.PingPong(elapsed, frames.Length - 1));
                        break;

                    default:
                        index = Mathf.FloorToInt(
                            Mathf.Repeat(elapsed, frames.Length));
                        break;
                }
            }

            index = Mathf.Clamp(index, 0, frames.Length - 1);

            if (index == _lastIndex)
                return;

            _lastIndex = index;

            if (frames[index] != null)
                _renderer.sprite = frames[index];
        }
    }
}
