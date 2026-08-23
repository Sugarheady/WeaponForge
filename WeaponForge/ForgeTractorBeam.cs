using UnityEngine;

namespace WeaponForge
{
    // The tractor beam you can actually SEE.
    //
    // `drawBeam` shipped parsed-and-never-read - a dead config field of exactly
    // the kind this project keeps finding in the game itself
    // (`destroyVelocityThreshold`, `boostImpactDamage`). So the beam moved
    // things with no indication of where it was pointed, which makes aiming a
    // hold-and-slam weapon guesswork.
    //
    // A LineRenderer, following ForgeTetherController's rope rather than
    // inventing anything: it is the one drawing idiom in this codebase that is
    // known to work in game. Note the same caveat applies - there is no stock
    // LineRenderer material to borrow, so this uses Unity's default, which
    // draws but ignores vertex colour on some setups. The width taper is what
    // reads as "beam" rather than "wire".
    public class ForgeTractorBeam : MonoBehaviour
    {
        private LineRenderer line;
        private float hideAt;
        private static ForgeTractorBeam _instance;

        public static ForgeTractorBeam Instance
        {
            get
            {
                if (_instance != null)
                    return _instance;

                var go = new GameObject("Forge Tractor Beam");
                UnityEngine.Object.DontDestroyOnLoad(go);
                _instance = go.AddComponent<ForgeTractorBeam>();
                return _instance;
            }
        }

        private void Awake()
        {
            line = gameObject.AddComponent<LineRenderer>();
            line.useWorldSpace = true;
            line.positionCount = 2;
            line.numCapVertices = 2;
            line.textureMode = LineTextureMode.Stretch;
            line.material = new Material(Shader.Find("Sprites/Default"));
            line.sortingLayerName = "Default";
            line.enabled = false;
        }

        // Called every frame the beam is live. Anything not re-armed within a
        // couple of frames switches itself off, so releasing the trigger needs
        // no separate notification - the same self-expiring trick the buff
        // states poller uses.
        public void Draw(
            Vector2 from, Vector2 to, Color color, float width, bool anchored)
        {
            if (line == null)
                return;

            line.enabled = true;
            line.SetPosition(0, new Vector3(from.x, from.y, 0f));
            line.SetPosition(1, new Vector3(to.x, to.y, 0f));

            // A grapple that has BITTEN draws solid and bright; a beam still
            // searching draws thin and faint. That difference is the whole
            // diagnostic: if the line reaches a wall the ray hit it, and if it
            // runs its full length into open space the ray found nothing.
            float w = Mathf.Max(0.04f, width);

            line.startWidth = anchored ? w : w * 0.45f;
            line.endWidth = anchored ? w * 0.6f : w * 0.2f;

            Color c = color;

            if (!anchored)
                c.a *= 0.45f;

            line.startColor = c;
            line.endColor = new Color(c.r, c.g, c.b, c.a * 0.35f);

            hideAt = Time.time + 0.08f;
        }

        private void LateUpdate()
        {
            if (line != null && line.enabled && Time.time > hideAt)
                line.enabled = false;
        }
    }
}
