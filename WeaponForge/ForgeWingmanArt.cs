using System;
using BepInEx.Logging;
using UnityEngine;

namespace WeaponForge
{
    // A wingman's BODY: building it, and dressing it every frame.
    //
    // THE VISUAL RESOLVER IS WIDER THAN ANY OTHER IN THIS MOD, and it has to
    // be. `orbit`'s `orbitVisual` resolves a **`Projectile` asset by name** -
    // ammo prefabs only - and the orbs Sugarheady actually asked for are not
    // projectiles: `FuelPickup` (blue orb), the Caps orb (orange), the Purple
    // orb (pink), `ResourcePickup_White`, **`ResourcePickup_Red` (health)**,
    // `ResourcePickup_Tech` and three Money pickups. So `visual` tries, in
    // order:
    //
    //   1. a CUSTOM sprite from the `sprites` folder (this is the only route
    //      to an animated wingman, and to a ship-shaped one)
    //   2. a stock sprite by name
    //   3. a prefab - ammo, pickup or drone - borrowed for its look
    //
    // A BORROWED PREFAB HAS TO BE DISARMED. A pickup destroys itself when
    // collected and carries a collider and a rigidbody; an ammo prefab carries
    // a live `Projectile`; a turret prefab ticks itself and would keep firing
    // from the body. `ForgeOrbitController.SpawnOrb` already learned all of
    // this - the difference here is that pickups are in scope, so `Pickup`
    // joins the list of components to strip.
    //
    // ANIMATION IS OPT-IN, and that is a compatibility rule rather than a taste
    // one. A wingman body is ONE SpriteRenderer, which puts it in the same
    // family as `gas.sprite`, `shield.sprite`, `swipe.sprite`,
    // `catalyst.nodeSprite` and `charge.meterSprite`: a bare name stays still,
    // and `:0-7` or `@12` asks for movement. Animating unconditionally would
    // make an existing file's art start moving on the next launch with no edit.
    public static class ForgeWingmanArt
    {
        private static readonly ManualLogSource Log =
            ForgeLog.Source("WeaponForge.Wingman");

        // ------------------------------------------------------------------
        // Build-time resolve
        // ------------------------------------------------------------------

        // Fills cfg.visualPrefab / visualFrames from cfg.visual. Called once,
        // from the builder, so a miss is reported next to the rest of the
        // weapon's build log rather than mid-fight.
        public static void Resolve(ForgeWingman.Config cfg, string fileName)
        {
            if (cfg == null || string.IsNullOrEmpty(cfg.visual))
                return;

            // 1 + 2: sprite, custom then stock, with the frame spec parsed.
            // TryGetAnyArt is the helper that exists precisely because seven
            // keys once could not see the game's own art.
            ForgeSpriteLibrary.Art art;

            if (ForgeSpriteLibrary.TryGetAnyArt(cfg.visual, out art) &&
                art != null && art.sprite != null)
            {
                // OPT-IN: returns the frames only when the NAME asked for them
                // (":0-7" / "@12" / a custom animation entry), and null for a
                // bare name that merely happens to sit on a sheet.
                float fps;
                Sprite[] opted = ForgeSpriteLibrary.OptInFrames(art, out fps);

                cfg.visualAnimates = opted != null && opted.Length > 1;

                cfg.visualFrames = cfg.visualAnimates
                    ? opted
                    : new[] { art.sprite };

                cfg.visualFps = (fps > 0f) ? fps : 12f;

                Log.LogInfo(
                    fileName + ": wingman body art '" + cfg.visual + "' - " +
                    cfg.visualFrames.Length + " frame(s), " +
                    (cfg.visualAnimates
                        ? "animating at " + cfg.visualFps.ToString("0.#") + " fps"
                        : "still (add \":0-" +
                          Mathf.Max(0, cfg.visualFrames.Length - 1) +
                          "\" or \"@12\" to animate it)") + ".");

                return;
            }

            // 3: a prefab, borrowed for its look only.
            GameObject prefab = FindPrefab(cfg.visual);

            if (prefab != null)
            {
                cfg.visualPrefab = prefab;

                Log.LogInfo(
                    fileName + ": wingman body borrows the look of prefab '" +
                    cfg.visual + "'. Its collider, rigidbody and any " +
                    "projectile/pickup/turret behaviour are stripped from the " +
                    "copy - a wingman body is art and a firing position, " +
                    "nothing else.");

                return;
            }

            Log.LogWarning(
                fileName + ": wingman \"visual\" is '" + cfg.visual +
                "', which is not one of your sprites, not a sprite in the " +
                "game, and not a prefab. The wingmen will still fire, but " +
                "you will not see them. " + ForgeSpriteLibrary.NotFoundHint());
        }

        private static GameObject FindPrefab(string name)
        {
            // Pickups and drones are not Projectiles, so a Projectile-typed
            // lookup (which is all `orbit` has) cannot see them. Search by
            // GameObject and let the name decide.
            try
            {
                var all = Resources.FindObjectsOfTypeAll<GameObject>();

                for (int i = 0; i < all.Length; i++)
                {
                    GameObject go = all[i];

                    if (go == null || go.scene.IsValid())
                        continue;   // a live instance, not a prefab

                    if (string.Equals(go.name, name,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return go;
                    }
                }
            }
            catch (Exception e)
            {
                Log.LogWarning(
                    "wingman: prefab lookup for '" + name + "' failed: " +
                    e.Message);
            }

            return null;
        }

        // ------------------------------------------------------------------
        // Runtime body
        // ------------------------------------------------------------------

        public static GameObject Build(ForgeWingman.Config cfg, Vector3 at)
        {
            GameObject go;

            if (cfg.visualPrefab != null)
            {
                go = BuildDisarmed(cfg.visualPrefab);
            }
            else
            {
                go = new GameObject("Forge Wingman");

                if (cfg.visualFrames != null && cfg.visualFrames.Length > 0)
                {
                    var sr = go.AddComponent<SpriteRenderer>();
                    sr.sprite = cfg.visualFrames[0];

                    if (cfg.visualAnimates)
                    {
                        VisualCustomizer.AnimateRenderer(
                            sr, cfg.visualFrames, cfg.visualFps,
                            /* randomStart */ true);
                    }
                }
            }

            go.name = "Forge Wingman";
            go.transform.position = at;
            go.transform.localScale = Vector3.one * Mathf.Max(0.01f, cfg.scale);

            if (cfg.hasColor)
            {
                foreach (var sr in go.GetComponentsInChildren<SpriteRenderer>(true))
                {
                    // Multiply the requested alpha by the renderer's own: a
                    // renderer held at alpha 0 is a visibility decision, not a
                    // colour, and overwriting it is how a recoloured minion
                    // came to wear its own outline.
                    Color c = cfg.color;
                    c.a *= sr.color.a;
                    sr.color = c;
                }
            }

            return go;
        }

        // Components that are pure cosmetics: they set a sprite or an animator
        // parameter once in Start and then do nothing for the rest of their
        // lives. These are the only scripts a borrowed body keeps.
        //
        // The list is short because it is an ALLOW-list, and that is the whole
        // point - see Disarm.
        private static readonly string[] KeepScripts =
        {
            "SpriteRandomizer",     // picks one of N sprites at Start
            "AnimationOffset",      // de-syncs an Animator's cycle at Start
        };

        // Reported once per borrowed prefab, so "why does my drone not blink"
        // has an answer in the log rather than requiring a guess.
        private static readonly System.Collections.Generic.HashSet<string> _saidStripped =
            new System.Collections.Generic.HashSet<string>();

        // A body is art and a firing position. Everything that would make it
        // behave has to go.
        //
        // THIS USED TO BE A DENY-LIST AND THAT WAS THE WRONG SHAPE. It named
        // seven component types - Projectile, PhysicsProjectile, ForgeTurret,
        // Pickup, Shooter, AIAgent, AimAssistTarget - which covered everything
        // anybody had thought of, and R16 found the eighth in about five
        // seconds of play: `FuelPickup` also carries **`ParticleLifetime`**,
        // which shrinks the sprite through five frames over three seconds and
        // then calls `Destroy(gameObject)` at a lifetime rolled between 5 and 7
        // seconds. Sugarheady's report - *"the blue orbs ... fade away after
        // awhile (like 5 seconds)"* - is that component exactly, and it is not
        // a fade at all but a self-destruct with a shrink in front of it. The
        // same prefab also carries a sway script that pushes its rigidbody.
        //
        // A deny-list on borrowed game prefabs can only ever be as complete as
        // the last prefab somebody tried. There are 14 pickup and ammo prefabs
        // a "visual" can legitimately name and no reason to believe the union
        // of their behaviours is known. So the rule is inverted: **strip every
        // MonoBehaviour except a named few that are provably cosmetic.** Losing
        // a flourish is a visible, harmless, greppable failure; keeping a
        // self-destruct is an invisible one that reads as "the mod is broken".
        //
        // Renderers, ParticleSystems, Animators and Lights are NOT
        // MonoBehaviours, so the art survives this untouched.
        // ★ BUILT SWITCHED OFF, THEN STRIPPED (R21 part 4's log: three
        // NullReferenceExceptions from MoveAroundOwnerAction.FindPath, one per
        // courier drone). The copy used to be made LIVE and its scripts
        // destroyed afterwards - and destroying a stock drone's StateMachine
        // runs its OnDisable, which CHANGES STATE: it switches on a state
        // object whose MoveAroundOwnerAction.OnEnable looks for an owner the
        // copy never had, inside our Destroy. Made under an inactive holder,
        // nothing on the copy ever wakes (no Awake, no OnEnable, so no
        // OnDisable either), the scripts go with DestroyImmediate, and only
        // the art is let out. The rank orb's pickup is built the same way.
        private static GameObject BuildDisarmed(GameObject prefab)
        {
            var holder = new GameObject("Forge Wingman Art (building)");
            holder.SetActive(false);

            try
            {
                GameObject go = UnityEngine.Object.Instantiate(prefab, holder.transform);

                Disarm(go);

                go.transform.SetParent(null, true);
                return go;
            }
            finally
            {
                UnityEngine.Object.Destroy(holder);
            }
        }

        private static void Disarm(GameObject go)
        {
            if (go == null)
                return;

            var stripped = new System.Collections.Generic.List<string>();

            // IN DEPENDENCY ORDER, over several passes: Unity refuses to remove
            // a component another one on the same object [RequireComponent]s,
            // and DestroyImmediate is not deferred, so the order is real. Each
            // pass removes what nothing left still needs.
            for (int pass = 0; pass < 6; pass++)
            {
                int removed = 0;

                foreach (var mb in go.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    if (mb == null)
                        continue;

                    string type = mb.GetType().Name;

                    if (System.Array.IndexOf(KeepScripts, type) >= 0)
                        continue;

                    if (RequiredByAnother(mb))
                        continue;

                    if (!stripped.Contains(type))
                        stripped.Add(type);

                    UnityEngine.Object.DestroyImmediate(mb);
                    removed++;
                }

                if (removed == 0)
                    break;
            }

            foreach (var rb in go.GetComponentsInChildren<Rigidbody2D>(true))
                rb.simulated = false;

            // Untargetable and unhittable: enemy shots pass straight through,
            // which is what "phantom" means.
            foreach (var col in go.GetComponentsInChildren<Collider2D>(true))
                col.enabled = false;

            Say(go.name, stripped);
        }

        private static bool RequiredByAnother(Component c)
        {
            Type mine = c.GetType();

            foreach (Component other in c.GetComponents<Component>())
            {
                if (other == null || ReferenceEquals(other, c))
                    continue;

                foreach (RequireComponent rc in other.GetType().GetCustomAttributes(
                             typeof(RequireComponent), true))
                {
                    if ((rc.m_Type0 != null && rc.m_Type0.IsAssignableFrom(mine)) ||
                        (rc.m_Type1 != null && rc.m_Type1.IsAssignableFrom(mine)) ||
                        (rc.m_Type2 != null && rc.m_Type2.IsAssignableFrom(mine)))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static void Say(
            string prefab, System.Collections.Generic.List<string> stripped)
        {
            if (stripped.Count == 0 || _saidStripped.Contains(prefab))
                return;

            _saidStripped.Add(prefab);

            Log.LogInfo(
                "wingman body '" + prefab + "': stripped " + stripped.Count +
                " script(s) so it is art and nothing else - " +
                string.Join(", ", stripped.ToArray()) +
                ". Its renderers, particles, animator and lights are " +
                "untouched. If the body looks plainer than the thing you " +
                "borrowed it from, one of those is why; if it USED to vanish " +
                "after a few seconds, ParticleLifetime in that list is why it " +
                "no longer does.");
        }

        public static void Reset()
        {
            _saidStripped.Clear();
        }

        // ------------------------------------------------------------------
        // Per-frame dressing
        // ------------------------------------------------------------------

        // `pulse` and `spin` both write localScale, so they are mutually
        // exclusive - exactly as `ForgeCatalystNode` treats them, and the
        // builder says which one won rather than letting the loser look broken.
        public static void Dress(
            ForgeWingman.Config cfg,
            GameObject go,
            SpriteRenderer sr,
            Vector3 baseScale)
        {
            if (cfg == null || go == null)
                return;

            if (cfg.spin)
            {
                // A coin turning about its own axis is a disc whose apparent
                // WIDTH oscillates. Floored rather than allowed to reach zero,
                // because a coin HAS thickness and a true zero reads as a
                // flicker rather than a spin.
                float thin = Mathf.Clamp(cfg.spinThinness, 0.02f, 1f);

                float w = Mathf.Max(
                    thin, Mathf.Abs(Mathf.Cos(Time.time * cfg.spinRate)));

                go.transform.localScale = cfg.spinHorizontal
                    ? new Vector3(baseScale.x, baseScale.y * w, baseScale.z)
                    : new Vector3(baseScale.x * w, baseScale.y, baseScale.z);

                // Edge-on you are looking at the RIM, not at the face squashed
                // thin - so darken as it closes. Without this the sliver reads
                // as gold leaf rather than as a turning object.
                if (sr != null && !cfg.hasColor)
                {
                    float face = Mathf.InverseLerp(thin, 1f, w);
                    Color edge = new Color(0.62f, 0.44f, 0.10f, sr.color.a);
                    Color lit = new Color(1f, 1f, 1f, sr.color.a);
                    sr.color = Color.Lerp(edge, lit, face);
                }
            }
            else if (cfg.pulse)
            {
                float k = 1f + Mathf.Sin(
                    Time.time * cfg.pulseSpeed * Mathf.PI * 2f) * cfg.pulseAmount;

                go.transform.localScale = baseScale * k;
            }

            if (cfg.colorPulse > 0f && cfg.hasColor && sr != null)
            {
                float t = 0.5f + 0.5f * Mathf.Sin(
                    Time.time * cfg.colorPulse * Mathf.PI * 2f);

                Color c = Color.Lerp(Color.white, cfg.color, t);
                c.a = sr.color.a;
                sr.color = c;
            }
        }
    }
}
