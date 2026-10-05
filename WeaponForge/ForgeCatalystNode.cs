using System;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;

namespace WeaponForge
{
    // One catalyst node: the thing you shoot into.
    //
    // Rides on the node projectile itself rather than being a separate object,
    // so it inherits the shot's own art, trail and colour for free and dies
    // with it.
    public class ForgeCatalystNode : MonoBehaviour
    {
        private static readonly ManualLogSource Log =
            ForgeLog.Source("WeaponForge.Catalyst");

        // The ID is what survives Instantiate; the reference does not. See
        // ForgeConfigTable - a plain `public Config cfg` on a prefab-attached
        // component arrives null on every shot, so Arm() early-returned, the node
        // never wore its coin sprite, never swept for bullets and never made a
        // sound. That was the whole of "my bullet goes right through it".
        public int cfgId = -1;

        [NonSerialized] public ForgeCatalyst.Config cfg;

        [NonSerialized] public WeaponBase weapon;   // the weapon that placed it
        [NonSerialized] public Unit owner;

        private IProjectile self;
        private bool armed;
        private bool halted;
        private float bornAt;
        private int used;
        private Vector3 baseScale;
        private Vector2 driftDir;
        private SpriteRenderer[] faces;

        // Shots this node has already dealt with. A redirected bullet is still
        // sitting inside the catch radius on the next frame, so without this a
        // node with `uses` above 1 spends every one of them on the SAME shot
        // over consecutive frames instead of waiting for the next one - and a
        // split's own copies, which are born inside the radius, would set it
        // off again immediately.
        private readonly HashSet<Component> handled = new HashSet<Component>();

        private static readonly List<Component> _scratch = new List<Component>();
        private static readonly List<ForgeCatalystNode> _live =
            new List<ForgeCatalystNode>();

        public static IList<ForgeCatalystNode> Live { get { return _live; } }

        public bool Armed { get { return armed; } }

        // A shot this node made and must not immediately catch again.
        public void Ignore(Component shot)
        {
            if (shot != null)
                handled.Add(shot);
        }

        private void Awake()
        {
            // Before Shoot, before any patch, before the first frame - so this
            // never depends on Harmony patch ordering.
            Resolve();

            bornAt = Time.time;
            baseScale = transform.localScale;
            _live.Add(this);
        }

        public void Resolve()
        {
            if (cfg == null && cfgId >= 0)
                cfg = ForgeConfigTable.Get<ForgeCatalyst.Config>(cfgId);
        }

        private void OnDestroy()
        {
            _live.Remove(this);
            ForgeProjectileTracker.Forget(self as Component);
        }

        // Called by the patch once the shot is genuinely live (Owner and
        // Velocity assigned), which on a lobbed shot is the SECOND Shoot call.
        public void Arm(IProjectile projectile, WeaponBase w, Unit o)
        {
            self = projectile;
            weapon = w;
            owner = o;
            armed = true;
            bornAt = Time.time;
            used = 0;
            halted = false;

            driftDir = (projectile != null &&
                        projectile.Velocity.sqrMagnitude > 0.0001f)
                ? projectile.Velocity.normalized
                : Vector2.right;

            Dress();
            Cull();

            if (!string.IsNullOrEmpty(cfg.armSfxGuid))
                AudioManager.PlaySfx(cfg.armSfxGuid, transform.position);
        }

        // Oldest-first, so a spammed node weapon rolls its budget forward
        // instead of refusing to place new ones.
        private void Cull()
        {
            if (cfg.max <= 0)
                return;

            int mine = 0;

            for (int i = 0; i < _live.Count; i++)
                if (_live[i] != null && _live[i] != this &&
                    _live[i].cfg == cfg && _live[i].armed)
                    mine++;

            while (mine >= cfg.max)
            {
                ForgeCatalystNode oldest = null;

                for (int i = 0; i < _live.Count; i++)
                {
                    ForgeCatalystNode n = _live[i];

                    if (n == null || n == this || n.cfg != cfg || !n.armed)
                        continue;

                    if (oldest == null || n.bornAt < oldest.bornAt)
                        oldest = n;
                }

                if (oldest == null)
                    break;

                Destroy(oldest.gameObject);
                mine--;
            }
        }

        // Swap the node's art for the coin (or whatever sprite was named) and
        // remember the scale the spin animates around.
        private void Dress()
        {
            if (cfg.nodeArt == null)
                return;

            foreach (SpriteRenderer sr in
                     GetComponentsInChildren<SpriteRenderer>(true))
            {
                if (sr == null)
                    continue;

                sr.sprite = cfg.nodeArt;

                // White, so the drawn gold shows through instead of being
                // tinted by whatever projectileColor the template carried.
                sr.color = Color.white;

                // Lock-step on purpose: these are the FACES of one object, not
                // a crowd of separate puffs, so they should agree.
                VisualCustomizer.AnimateRenderer(
                    sr, cfg.nodeFrames, cfg.nodeFps, false);
            }

            faces = GetComponentsInChildren<SpriteRenderer>(true);
            baseScale = transform.localScale;
        }

        public void Halt()
        {
            if (halted || !cfg.Halts)
                return;

            halted = true;

            if (self != null)
                self.Velocity = Vector2.zero;

            // A lobbed node would otherwise keep falling.
            var rb = GetComponent<Rigidbody2D>();

            if (rb != null)
            {
                rb.linearVelocity = Vector2.zero;
                rb.gravityScale = 0f;
                rb.angularVelocity = 0f;
            }
        }

        private void Update()
        {
            if (!armed || cfg == null)
                return;

            if (Time.time - bornAt >= cfg.lifetime)
            {
                Destroy(gameObject);
                return;
            }

            if (cfg.Halts && !halted && Time.time - bornAt >= cfg.armTime)
                Halt();

            if (halted && cfg.node == ForgeCatalyst.Node.Drift && self != null)
            {
                transform.position +=
                    (Vector3)(driftDir * cfg.drift * Time.deltaTime);
            }

            if (cfg.spin)
            {
                // A coin turning about its vertical axis is a disc whose
                // apparent WIDTH oscillates: full face, narrowing to its edge,
                // opening out again. Floored rather than allowed to reach zero,
                // because a coin HAS thickness - and a true zero reads as a
                // flicker rather than a spin.
                float thin = Mathf.Clamp(cfg.spinThinness, 0.02f, 1f);

                float w = Mathf.Max(
                    thin, Mathf.Abs(Mathf.Cos(Time.time * cfg.spinSpeed)));

                transform.localScale = cfg.spinHorizontal
                    ? new Vector3(baseScale.x, baseScale.y * w, baseScale.z)
                    : new Vector3(baseScale.x * w, baseScale.y, baseScale.z);

                // Edge-on you are looking at the MILLED RIM, not at the face
                // squashed thin - so darken toward the rim as it closes. This is
                // what actually sells the thin side; without it the sliver still
                // reads as gold leaf.
                Tint(Mathf.InverseLerp(thin, 1f, w));
            }
            else if (cfg.pulse)
            {
                float k = 1f + Mathf.Sin(Time.time * 6f) * 0.12f;
                transform.localScale = baseScale * k;
            }

            Sweep();
        }

        // face = 1 is fully open, face = 0 is edge-on.
        private void Tint(float face)
        {
            if (faces == null)
                return;

            // The rim colour the coin is drawn with, so the two agree.
            Color edge = new Color(0.62f, 0.44f, 0.10f, 1f);
            Color c = Color.Lerp(edge, Color.white, face);

            for (int i = 0; i < faces.Length; i++)
                if (faces[i] != null)
                    faces[i].color = c;
        }

        private void Sweep()
        {
            // Your own shots first - that is the headline behaviour.
            _scratch.Clear();

            ForgeProjectileTracker.MineNear(
                transform.position, cfg.radius, self as Component, _scratch);

            for (int i = 0; i < _scratch.Count && armed; i++)
            {
                Component shot = _scratch[i];

                if (shot == null || ReferenceEquals(shot, self))
                    continue;

                if (handled.Contains(shot))
                    continue;

                // Never let one node trigger on another node's body.
                if (shot.GetComponent<ForgeCatalystNode>() != null)
                    continue;

                if (cfg.trigger == ForgeCatalyst.Trigger.Different)
                {
                    // Which weapon fired it - the same attribution the on-kill
                    // reward uses, so this cost nothing to add.
                    WeaponBase from = ForgeKillsPatch.WeaponOf(shot);

                    if (from != null && weapon != null && from == weapon)
                        continue;
                }

                handled.Add(shot);

                try
                {
                    Trigger(shot);
                }
                catch (Exception e)
                {
                    Log.LogError("Catalyst trigger failed: " + e);
                }

                if (Spend())
                    return;
            }

            _scratch.Clear();

            if (!cfg.catchEnemyShots || !armed)
                return;

            ForgeProjectileTracker.CollectNear(
                transform.position, cfg.radius, 0, _scratch);

            for (int i = 0; i < _scratch.Count; i++)
            {
                Component bullet = _scratch[i];

                if (bullet == null)
                    continue;

                // The whole faction swap - velocity, layer, the private
                // collision mask and Owner - lives in ForgeBulletTurn, shared
                // with deflect and the shield arc.
                ForgeBulletTurn.Turn(
                    bullet, owner,
                    cfg.aimReflected ? ForgeBulletTurn.AimNearest : cfg.aimMode,
                    cfg.range,
                    cfg.reflectSpeed, cfg.reflectDamage, cfg.rally,
                    transform.position);
            }

            bool caught = _scratch.Count > 0;
            _scratch.Clear();

            if (caught && cfg.enemyShotsConsume)
                Spend();
        }

        private ForgeCatalystNode NearestOtherNode()
        {
            ForgeCatalystNode best = null;
            float bestSqr = cfg.range * cfg.range;
            Vector2 here = transform.position;

            for (int i = 0; i < _live.Count; i++)
            {
                ForgeCatalystNode n = _live[i];

                if (n == null || n == this || !n.armed)
                    continue;

                float d = ((Vector2)n.transform.position - here).sqrMagnitude;

                if (d < bestSqr)
                {
                    bestSqr = d;
                    best = n;
                }
            }

            return best;
        }

        private bool Spend()
        {
            used++;

            if (cfg.uses > 0 && used >= cfg.uses)
            {
                armed = false;
                Destroy(gameObject);
                return true;
            }

            return false;
        }

        // ------------------------------------------------------------------
        // The redirect
        // ------------------------------------------------------------------

        private void Trigger(Component shot)
        {
            if (!string.IsNullOrEmpty(cfg.triggerSfxGuid))
                AudioManager.PlaySfx(cfg.triggerSfxGuid, transform.position);

            var plain = shot as Projectile;

            if (cfg.redirect == ForgeCatalyst.Redirect.Split)
            {
                ForgeCatalystSplit.Burst(shot, this, cfg);
                return;
            }

            // CHAIN. A chain is granted to the shot rather than reimplemented:
            // ForgeChainPatch hooks TryHit and FixedUpdate and needs nothing set
            // up at fire time, so a component added mid-flight works exactly as
            // if the weapon had always had one.
            if (plain != null)
            {
                var chain = plain.GetComponent<ForgeChain>();

                if (chain == null)
                    chain = plain.gameObject.AddComponent<ForgeChain>();

                chain.jumps = Mathf.Max(1, cfg.targets);
                chain.range = cfg.range;
                chain.damageMultiplier = cfg.chainFalloff;
                chain.speedMultiplier = 1f;
                chain.revisit = cfg.chainRevisit;
                chain.used = 0;
                chain.visited.Clear();

                // A chain only survives a hit if piercing is on - without this
                // the shot dies on the first enemy and never jumps.
                PiercingData pd = plain.PiercingData;

                if (!pd.enabled)
                {
                    pd.enabled = true;

                    if (pd.damageRepeatDelay <= 0f)
                        pd.damageRepeatDelay = 0.3f;

                    plain.PiercingData = pd;
                }
            }

            ForgeCatalystSplit.Boost(shot, cfg);

            // Coin to coin first, if asked. The next node's own sweep picks the
            // shot up and fires again, so the cascade needs no special case -
            // and it cannot loop, because this node is consumed a moment from
            // now.
            if (cfg.preferNodes)
            {
                ForgeCatalystNode next = NearestOtherNode();

                if (next != null)
                {
                    ForgeCatalystSplit.Aim(
                        shot, next.transform.position, next.gameObject);

                    // Lock on to the coin too, not just at it. A tossed node is
                    // still falling, so an aim taken a moment ago is already
                    // stale - this is what turns the cascade from a lucky shot
                    // into a reliable one.
                    ForgeCatalystSplit.Lock(shot, next.gameObject, cfg);
                    return;
                }
            }

            ForgeCatalystSplit.AimAtNearest(shot, owner, cfg);
        }
    }
}
