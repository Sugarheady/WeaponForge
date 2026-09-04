using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace WeaponForge
{
    // Wiring the curve beam into the game.
    //
    // TWO HOOKS, AND THE ORDER BETWEEN THEM IS THE WHOLE DESIGN.
    // Shooter.Update calls OnBarrelMoved every frame and unconditionally, and
    // only afterwards reaches Shoot() -> DoShoot -> FireSingle. So:
    //
    //     OnBarrelMoved  (postfix)  build the path, draw the chain
    //     FireSingle     (prefix)   damage along THAT path, suppress the
    //                               straight cast
    //
    // The old warning about this feature was that damage and visuals are two
    // independent raycasts, so bending one leaves the other going straight
    // while looking perfect. There is only one path here and both passes read
    // it, so they cannot disagree.
    //
    // POSTFIX, NOT A PREFIX THAT SKIPS, on OnBarrelMoved. Letting the original
    // run costs one wasted CircleCast and buys all of its bookkeeping - the
    // Firing / WarmingUp transitions that enable renderers, start the impact
    // particle and switch the light on. It also leaves ForgeShieldPatch's own
    // prefix on that method undisturbed; a prefix returning false there would
    // skip the remaining prefixes and quietly break shield clipping on lasers.
    //
    // A PREFIX THAT SKIPS is unavoidable on FireSingle, because the straight
    // damage cast has to not happen. Remember that a POSTFIX STILL RUNS when a
    // prefix returns false - that is the trap that had overheat charging for
    // shots that never fired. Both postfixes on this method were checked:
    // ForgeDischargePatch does not read the hit at all (it spawns a conductor
    // on the ship), so it is correct either way; ForgeImpactPatch re-casts the
    // straight ray to place its sound, so it stands down for curve beams and
    // this file plays that sound at the end of the actual curve instead.
    public static class ForgeCurveBeamPatch
    {
        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("WeaponForge.CurveBeam");

        // HitscanWeapon keeps its visual instances private, and OnBarrelMoved
        // indexes that list by pellet number.
        private static readonly FieldInfo _fVisuals =
            AccessTools.Field(typeof(HitscanWeapon), "visualsInstances");

        private static int _groundMask = -1;
        private static int _targetMask = -1;

        private static int GroundMask
        {
            get
            {
                if (_groundMask < 0)
                    _groundMask = LayerMask.GetMask("Ground");

                return _groundMask;
            }
        }

        private static int TargetMask
        {
            get
            {
                if (_targetMask < 0)
                {
                    // Every layer a unit can stand on, not just the enemy one.
                    // Faction does the filtering (FindNearestTarget skips
                    // anything friendly to the owner), which is what lets a
                    // minion carrying a curve beam lock onto enemies while the
                    // same weapon in your hands locks onto them too.
                    _targetMask =
                        LayerMask.GetMask("Entities", "Fruits", "Player");
                }

                return _targetMask;
            }
        }

        // ------------------------------------------------------------------
        // Per-weapon state
        // ------------------------------------------------------------------
        private class State
        {
            public readonly List<ForgeCurveBeamPath.Result> paths =
                new List<ForgeCurveBeamPath.Result>();

            public readonly List<Vector2> seeds = new List<Vector2>();

            // Which frame the paths were last built on. FireSingle rebuilds
            // when this is stale, which is what covers the gadget path -
            // a gadget calls weapon.Fire(new FakeBarrel(...)) directly and
            // never goes through Shooter, so OnBarrelMoved never runs at all.
            public int builtFrame = -1;

            public int fireFrame = -1;
            public int fireIndex;

            public AimAssistTarget locked;

            public float nextExplosion;

            // Our own repeat-delay book. HitscanWeapon has one but it is
            // private and, more to the point, it is keyed on the assumption of
            // one target per shot.
            public readonly Dictionary<IHitscanWeaponListener, float> lastHit =
                new Dictionary<IHitscanWeaponListener, float>();

            public ForgeCurveBeamPath.Result Path(int index)
            {
                while (paths.Count <= index)
                {
                    paths.Add(new ForgeCurveBeamPath.Result());
                    seeds.Add(Vector2.zero);
                }

                return paths[index];
            }

            public Vector2 Seed(int index)
            {
                return (index < seeds.Count) ? seeds[index] : Vector2.zero;
            }

            public void SetSeed(int index, Vector2 v)
            {
                while (seeds.Count <= index)
                    seeds.Add(Vector2.zero);

                seeds[index] = v;
            }

            public void Prune()
            {
                if (lastHit.Count < 64)
                    return;

                // A held beam sweeping a level accumulates entries for things
                // that died long ago. Cheap sweep rather than a per-entry
                // liveness check, which would need a cast per entry.
                var stale = new List<IHitscanWeaponListener>();

                foreach (var kv in lastHit)
                {
                    if (Time.time - kv.Value > 5f)
                        stale.Add(kv.Key);
                }

                for (int i = 0; i < stale.Count; i++)
                    lastHit.Remove(stale[i]);
            }
        }

        private static readonly Dictionary<HitscanWeapon, State> _states =
            new Dictionary<HitscanWeapon, State>();

        private static State For(HitscanWeapon weapon)
        {
            State s;

            if (!_states.TryGetValue(weapon, out s))
            {
                s = new State();
                _states[weapon] = s;
            }

            return s;
        }

        // ------------------------------------------------------------------
        // The fan of directions.
        //
        // GetDirections is protected AND it re-rolls angleVariance on every
        // call, so asking it twice in one frame gives two different fans - the
        // drawing pass and the damage pass would each get their own. This
        // reproduces it from the public properties instead, minus the random
        // component: on a beam that steers toward a locked target, a random
        // wobble at the muzzle is invisible within two links anyway.
        // ------------------------------------------------------------------
        private static Vector2 DirectionFor(
            HitscanWeapon weapon, Vector2 direction, int index)
        {
            float count = Mathf.Max(1f, weapon.ProjectileCount);

            float first = (count == 1f)
                ? weapon.AngleOffset
                : (-weapon.Spread * 0.5f + weapon.AngleOffset);

            float step = 0f;

            if (count > 1f)
            {
                step = (Mathf.Abs(weapon.Spread - 360f) < 0.001f)
                    ? weapon.Spread / count
                    : weapon.Spread / (count - 1f);
            }

            return ForgeCurveBeamPath.Rotate(direction, first + index * step);
        }

        // ------------------------------------------------------------------
        // Lock-on
        // ------------------------------------------------------------------
        private static Vector2? Acquire(
            HitscanWeapon weapon,
            ForgeCurveBeam.Config cfg,
            State state,
            Vector2 position,
            Vector2 direction)
        {
            if (cfg.lockMode == ForgeCurveBeam.LockNone)
            {
                state.locked = null;
                return null;
            }

            // A Unity object comparison, so this also catches a target that was
            // destroyed rather than merely disabled.
            bool alive =
                state.locked != null && state.locked.isActiveAndEnabled;

            if (alive && cfg.holdLock)
                return state.locked.transform.position;

            if (alive && !cfg.holdLock)
            {
                // Re-picking every frame is a legitimate mode - it whips the
                // beam between enemies in a crowd - so fall through and look
                // again rather than keeping this one.
                state.locked = null;
            }

            if (!alive && state.locked != null && !cfg.reacquire)
            {
                // It died and this weapon was told not to look for another.
                state.locked = null;
                return null;
            }

            float range = (cfg.lockRange > 0f) ? cfg.lockRange : weapon.Range;

            AimAssistTarget found;

            if (cfg.lockMode == ForgeCurveBeam.LockForward)
            {
                found = ForgeHomingPatch.FindTarget(
                    weapon.Owner, position, direction, range,
                    Mathf.Clamp(cfg.lockCone, 1f, 360f), TargetMask,
                    null, ForgeTargeting.For(weapon));
            }
            else
            {
                found = ForgeHomingPatch.FindNearestTarget(
                    weapon.Owner, position, range, TargetMask, null,
                    cfg.objectsToo, ForgeTargeting.For(weapon));
            }

            state.locked = found;

            return (found != null)
                ? (Vector2?)found.transform.position
                : null;
        }

        // ------------------------------------------------------------------
        // WEAVING - which other enemies are worth a detour on the way.
        //
        // His ask: "the segments going to that enemy also looks for other
        // enemies that it can hit along or near it's path. Like seeking."
        //
        // ORDERED BY PROJECTION ALONG THE AXIS, not by proximity, and that is
        // the whole trick. Sorting by "how far along the way to the target are
        // you" gives a corridor that always moves FORWARD - so the beam picks
        // things up in the order it meets them and can never be sent back the
        // way it came. A nearest-neighbour walk would happily zig-zag, and a
        // beam that doubles back looks broken rather than clever.
        //
        // Anything behind the barrel (t <= 0) or past the target (t >= 1) is
        // dropped for the same reason: a detour is only a detour if it is on
        // the way.
        // ------------------------------------------------------------------
        private static readonly List<Vector2> _weave = new List<Vector2>();
        private static readonly List<Collider2D> _near = new List<Collider2D>();
        private static ContactFilter2D _weaveFilter;
        private static bool _weaveFilterReady;

        private static List<Vector2> Weave(
            HitscanWeapon weapon,
            ForgeCurveBeam.Config cfg,
            Vector2 origin,
            Vector2 target,
            Transform locked)
        {
            _weave.Clear();

            if (!cfg.weave || cfg.weaveMax <= 0)
                return null;

            try
            {
                Vector2 axis = target - origin;
                float span = axis.magnitude;

                if (span < 0.5f)
                    return null;

                Vector2 dir = axis / span;
                float reach = Mathf.Max(0.5f, cfg.weaveRange);

                if (!_weaveFilterReady)
                {
                    _weaveFilterReady = true;
                    _weaveFilter = new ContactFilter2D();
                }

                _weaveFilter.useTriggers = true;
                _weaveFilter.SetLayerMask(TargetMask);

                // One overlap covering the whole corridor: a circle centred on
                // the midpoint, big enough to hold the span plus the sideways
                // reach. Cheaper than walking the line, and the per-candidate
                // filter below does the precise work.
                _near.Clear();
                _weaveAt.Clear();

                Physics2D.OverlapCircle(
                    origin + dir * (span * 0.5f),
                    span * 0.5f + reach,
                    _weaveFilter,
                    _near);

                for (int i = 0; i < _near.Count; i++)
                {
                    Collider2D col = _near[i];

                    if (col == null)
                        continue;

                    var aim = col.GetComponentInParent<AimAssistTarget>();

                    if (aim == null)
                        continue;

                    // The thing we locked is the destination, not a detour.
                    if (locked != null && aim.transform == locked)
                        continue;

                    if (!cfg.objectsToo &&
                        aim.GetComponentInParent<AIAgent>() == null)
                        continue;

                    if (weapon.Owner != null)
                    {
                        var unit = col.GetComponentInParent<Unit>();

                        if (unit != null && weapon.Owner.IsFriendsWith(unit))
                            continue;
                    }

                    Vector2 p = aim.transform.position;
                    Vector2 rel = p - origin;

                    float along = Vector2.Dot(rel, dir);

                    // Behind us, or past the target: not on the way.
                    if (along <= 0.5f || along >= span)
                        continue;

                    float off = Mathf.Abs(rel.x * dir.y - rel.y * dir.x);

                    if (off > reach)
                        continue;

                    Add(p, along, cfg.weaveMax);
                }

                return _weave.Count > 0 ? _weave : null;
            }
            catch (Exception e)
            {
                Log.LogError("Choosing weave targets failed: " + e);
                return null;
            }
        }

        // Insertion sort by distance along the axis, capped. The list is at
        // most `weaveMax` long, so this is cheaper and simpler than sorting a
        // full candidate set - and it keeps the NEAREST-ALONG ones rather than
        // whichever the overlap happened to return first.
        private static readonly List<float> _weaveAt = new List<float>();

        private static void Add(Vector2 point, float along, int max)
        {
            int at = 0;

            while (at < _weaveAt.Count && _weaveAt[at] < along)
                at++;

            // Already full and this one is further along than everything in
            // it: the beam would have to come back for it, so drop it.
            if (_weaveAt.Count >= max && at >= max)
                return;

            _weaveAt.Insert(at, along);
            _weave.Insert(at, point);

            // Keep the EARLIEST `max` along the axis. Trimming from the end
            // rather than refusing new ones means a target close to the barrel
            // always displaces one further away, which is the order the beam
            // will actually meet them in.
            while (_weaveAt.Count > max)
            {
                _weaveAt.RemoveAt(_weaveAt.Count - 1);
                _weave.RemoveAt(_weave.Count - 1);
            }
        }

        // ------------------------------------------------------------------
        // Building the paths for this frame
        // ------------------------------------------------------------------
        private static void Build(
            HitscanWeapon weapon,
            ForgeCurveBeam.Config cfg,
            State state,
            Vector2 position,
            Vector2 direction)
        {
            if (state.builtFrame == Time.frameCount)
                return;

            state.builtFrame = Time.frameCount;

            Vector2? target = Acquire(weapon, cfg, state, position, direction);

            float length = (cfg.maxLength > 0f) ? cfg.maxLength : weapon.Range;

            // Chosen ONCE for the whole volley, not per beam: every beam of a
            // multi-pellet hitscan shares the same lock, so they should share
            // the same detours too - otherwise four beams would each pick a
            // different route and the weapon would read as noise. Also one
            // overlap query per frame rather than four.
            List<Vector2> weave = target.HasValue
                ? Weave(weapon, cfg, position, target.Value,
                        (state.locked != null) ? state.locked.transform : null)
                : null;

            int count = Mathf.Max(1, Mathf.RoundToInt(weapon.ProjectileCount));

            for (int i = 0; i < count; i++)
            {
                Vector2 dir = DirectionFor(weapon, direction, i);

                // The muzzle, not the ship's centre - the same place the stock
                // beam starts from.
                Vector2 origin = position + dir * weapon.BarrelLength;

                ForgeCurveBeamPath.Result path = state.Path(i);

                ForgeCurveBeamPath.Build(
                    path, cfg, origin, dir, target, length, GroundMask,
                    state.Seed(i), weave);

                // Remember where this beam was heading as it left the barrel,
                // so next frame's smoothing has something to blend against.
                if (path.points.Count >= 2)
                {
                    Vector2 d = path.points[1] - path.points[0];

                    if (d.sqrMagnitude > 0.000001f)
                        state.SetSeed(i, d.normalized);
                }
            }
        }

        // ------------------------------------------------------------------
        [HarmonyPatch(typeof(HitscanWeapon), "OnBarrelMoved")]
        public class OnBarrelMoved
        {
            static void Postfix(
                HitscanWeapon __instance, Vector2 position, Vector2 direction)
            {
                ForgeCurveBeam.Config cfg;

                if (!ForgeCurveBeam.TryGet(__instance.TemplateData, out cfg))
                    return;

                try
                {
                    var visuals =
                        _fVisuals.GetValue(__instance)
                            as List<HitscanWeaponVisual>;

                    if (visuals == null || visuals.Count == 0)
                        return;

                    State state = For(__instance);

                    // The warm-up stays STRAIGHT on purpose. It is the tell
                    // that the weapon is spinning up and has not chosen a
                    // target yet; curving it would say the beam is already
                    // tracking when it is not.
                    bool firing =
                        __instance.IsTriggerPulled && __instance.IsWarmedUp;

                    if (!firing)
                    {
                        for (int i = 0; i < visuals.Count; i++)
                        {
                            var chain = ChainOn(visuals[i]);

                            if (chain != null)
                                chain.Hide();
                        }

                        state.locked = null;
                        return;
                    }

                    Build(__instance, cfg, state, position, direction);

                    for (int i = 0; i < visuals.Count; i++)
                    {
                        HitscanWeaponVisual visual = visuals[i];

                        if (visual == null)
                            continue;

                        ForgeCurveBeamPath.Result path = state.Path(i);

                        SpriteRenderer fire = ForgeBeam.FireOf(visual);

                        // The stock quad is the straight beam. It has to go, or
                        // it draws through every wall the chain just went
                        // around. Written every frame rather than once, because
                        // the Firing setter re-enables it on every transition.
                        if (fire != null)
                            fire.enabled = false;

                        var chain = ChainOn(visual);

                        if (chain != null)
                            chain.Draw(path.points, cfg, fire);

                        // The puff. UpdateVisual has already parked the impact
                        // particle at the end of the STRAIGHT ray a moment ago;
                        // this moves it to the end of the curve. Free, because
                        // the particle system was always there.
                        if (cfg.endPuff && path.points.Count >= 2)
                        {
                            ParticleSystem puff = ForgeBeam.ImpactOf(visual);

                            if (puff != null)
                            {
                                Vector2 head = path.EndHeading;

                                puff.transform.SetPositionAndRotation(
                                    path.End,
                                    Quaternion.FromToRotation(
                                        Vector3.right, -head));
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    Log.LogError("Curve beam drawing failed: " + e);
                }
            }

            private static ForgeCurveBeamChain ChainOn(
                HitscanWeaponVisual visual)
            {
                if (visual == null)
                    return null;

                var chain = visual.GetComponent<ForgeCurveBeamChain>();

                if (chain == null)
                    chain = visual.gameObject.AddComponent<ForgeCurveBeamChain>();

                return chain;
            }
        }

        // ------------------------------------------------------------------
        // Damage
        // ------------------------------------------------------------------
        [HarmonyPatch(typeof(HitscanWeapon), "FireSingle")]
        public class OnFireSingle
        {
            private static readonly RaycastHit2D[] _hits = new RaycastHit2D[32];

            private static readonly HashSet<IHitscanWeaponListener> _struck =
                new HashSet<IHitscanWeaponListener>();

            private static ContactFilter2D _filter;
            private static bool _filterReady;

            static bool Prefix(
                HitscanWeapon __instance, Vector2 position, Vector2 direction)
            {
                ForgeCurveBeam.Config cfg;

                if (!ForgeCurveBeam.TryGet(__instance.TemplateData, out cfg))
                    return true;

                try
                {
                    State state = For(__instance);

                    // Which pellet this is. DoShoot's loop is synchronous, so a
                    // frame-scoped counter is exact - the same reasoning
                    // ForgeVolley uses to capture a volley's axis.
                    if (state.fireFrame != Time.frameCount)
                    {
                        state.fireFrame = Time.frameCount;
                        state.fireIndex = 0;
                    }

                    int index = state.fireIndex++;

                    // A gadget-fired beam never goes through Shooter, so
                    // OnBarrelMoved never ran and there is no path yet.
                    Build(__instance, cfg, state, position, direction);

                    ForgeCurveBeamPath.Result path = state.Path(index);

                    if (path.points.Count < 2)
                        return true;   // nothing usable - let the stock beam fire

                    Fire(__instance, cfg, state, path);

                    return false;
                }
                catch (Exception e)
                {
                    Log.LogError("Curve beam firing failed: " + e);
                    return true;
                }
            }

            private static void EnsureFilter(HitscanWeapon weapon)
            {
                if (!_filterReady)
                {
                    _filterReady = true;
                    _filter = new ContactFilter2D();
                }

                // MATCH THE STOCK BEAM EXACTLY. FireSingle's own cast takes no
                // filter, so it obeys the global Physics2D.queriesHitTriggers
                // setting. Hard-coding useTriggers here would make the curve
                // beam hit a different category of collider from the straight
                // one - which would show up as a beam that damages something a
                // normal laser cannot touch, and be very hard to trace back.
                _filter.useTriggers = Physics2D.queriesHitTriggers;
                _filter.SetLayerMask(weapon.LayerMask);
            }

            // Rewrite terrain along the beam's path, if the weapon asks for it.
            //
            // Stepped rather than run per path point: the points are spaced by
            // `segment` (0.45 by default) while a conversion radius is usually
            // 2 or more, so converting at every point would redo almost the
            // same disc twenty times for one shot. Stepping by the radius
            // covers the path once with no gaps.
            private static void Terraform(
                HitscanWeapon weapon, ForgeCurveBeamPath.Result path)
            {
                CellConvertData cfg = weapon.CellConvertData;

                if (cfg == null || !cfg.enabled ||
                    cfg.resultCellType == null ||
                    cfg.convertableCells == null ||
                    cfg.convertableCells.Count == 0 ||
                    path.points.Count < 1)
                {
                    return;
                }

                try
                {
                    Level level;

                    if (!ServiceLocator.TryGet<Level>(out level) || level == null)
                        return;

                    float step = Mathf.Max(0.5f, cfg.radius);
                    Vector2 last = path.points[0];

                    level.ConvertCells(cfg, last);

                    for (int i = 1; i < path.points.Count; i++)
                    {
                        Vector2 p = path.points[i];

                        // Always convert the final point, so the beam's tip
                        // digs even when the last step was a short one.
                        if (Vector2.Distance(last, p) < step &&
                            i < path.points.Count - 1)
                        {
                            continue;
                        }

                        level.ConvertCells(cfg, p);
                        last = p;
                    }
                }
                catch (Exception e)
                {
                    Log.LogError("Curve beam terrain conversion failed: " + e);
                }
            }

            private static void Fire(
                HitscanWeapon weapon,
                ForgeCurveBeam.Config cfg,
                State state,
                ForgeCurveBeamPath.Result path)
            {
                EnsureFilter(weapon);

                // TERRAIN CONVERSION, which this feature was silently skipping.
                //
                // `HitscanWeapon.FireSingle` ends with
                //     if (CellConvertData.enabled) Level.ConvertCells(cfg, hit.point);
                // and the prefix above returns FALSE whenever a curve-beam
                // config exists - so the stock method never runs and neither
                // did its conversion. A curve beam with terraforming set up did
                // nothing, exactly the way a projectile weapon used to.
                //
                // Converted along the WHOLE PATH rather than at one point,
                // because that is the only reading that makes sense here: a
                // straight beam has a single hit point, and a curve beam is a
                // path by definition. A bending digging beam carves the curve
                // it travelled, which is the whole appeal.
                Terraform(weapon, path);

                _struck.Clear();
                state.Prune();

                Transform locked = (state.locked != null)
                    ? state.locked.transform
                    : null;

                int cap = (cfg.maxTargets > 0)
                    ? cfg.maxTargets
                    : int.MaxValue;

                int hurt = 0;

                float push = (cfg.push > 0f) ? cfg.push : weapon.PushForce;

                for (int i = 0; i < path.points.Count - 1 && hurt < cap; i++)
                {
                    Vector2 a = path.points[i];
                    Vector2 b = path.points[i + 1];

                    Vector2 span = b - a;
                    float len = span.magnitude;

                    if (len < 0.0001f)
                        continue;

                    Vector2 dir = span / len;

                    int n = Physics2D.CircleCast(
                        a, weapon.RayWidth, dir, _filter, _hits, len);

                    for (int h = 0; h < n && h < _hits.Length; h++)
                    {
                        Collider2D col = _hits[h].collider;

                        if (col == null)
                            continue;

                        IHitscanWeaponListener listener = Listener(col);

                        if (listener == null)
                            continue;

                        if (_struck.Contains(listener))
                            continue;

                        // A stock beam is kept off its owner's friends by the
                        // layer mask alone, which is enough when a beam only
                        // ever touches one thing. A beam that threads a whole
                        // room is a different matter: without this, a curve
                        // beam snaking past your own drones would cut through
                        // them on the way to its target.
                        if (Friendly(weapon, col))
                            continue;

                        float last;

                        if (state.lastHit.TryGetValue(listener, out last) &&
                            Time.time - last <= weapon.DamageRepeatDelay)
                        {
                            continue;
                        }

                        _struck.Add(listener);
                        state.lastHit[listener] = Time.time;

                        bool isTarget =
                            locked != null &&
                            col.transform != null &&
                            (col.transform == locked ||
                             col.transform.IsChildOf(locked));

                        Hurt(weapon, cfg, listener, _hits[h], isTarget);

                        if (push != 0f && col.attachedRigidbody != null)
                        {
                            col.attachedRigidbody.AddForceAtPosition(
                                dir * push, _hits[h].point,
                                ForceMode2D.Impulse);
                        }

                        hurt++;

                        if (!cfg.pierce || hurt >= cap)
                            break;
                    }

                    if (!cfg.pierce && hurt > 0)
                        break;
                }

                Land(weapon, cfg, state, path);
            }

            // Damage, burn, the onGotAttacked event and KILL CREDIT all come
            // from calling the game's own listener entry point - the same one
            // FireSingle calls. DamagableResource.OnHitByHitscanWeapon is one
            // of exactly three places in the game that call RegisterKill, so
            // going through it is what makes a curve-beam kill visible to
            // onKill rewards and to the music controller. Damaging by hand
            // would have paid out behind the game's back.
            private static void Hurt(
                HitscanWeapon weapon,
                ForgeCurveBeam.Config cfg,
                IHitscanWeaponListener listener,
                RaycastHit2D hit,
                bool isTarget)
            {
                float scale = isTarget ? 1f : Mathf.Max(0f, cfg.bystander);

                if (scale <= 0f)
                    return;

                if (Mathf.Abs(scale - 1f) < 0.001f)
                {
                    listener.OnHitByHitscanWeapon(weapon, hit);
                    return;
                }

                // HealthBase reads weapon.GetDamage(), which returns the
                // weapon's own Damage - so scaling for a bystander means
                // swapping it for the duration of the call. Restored in a
                // FINALLY, not after: an exception thrown inside a listener
                // would otherwise leave the weapon permanently weakened, the
                // same failure mode the shield's range clipping guards against
                // with a Harmony finalizer.
                Damage original = weapon.Damage;

                try
                {
                    Damage scaled = original;
                    scaled.amount = original.amount * scale;

                    weapon.Damage = scaled;

                    listener.OnHitByHitscanWeapon(weapon, hit);
                }
                finally
                {
                    weapon.Damage = original;
                }
            }

            // What happens where the beam ends.
            private static void Land(
                HitscanWeapon weapon,
                ForgeCurveBeam.Config cfg,
                State state,
                ForgeCurveBeamPath.Result path)
            {
                Vector2 end = path.End;

                if (weapon.CellConvertData.enabled)
                {
                    Level level;

                    if (ServiceLocator.TryGet<Level>(out level) && level != null)
                        level.ConvertCells(weapon.CellConvertData, end);
                }

                // The impact sound. ForgeImpactPatch stands down for curve
                // beams because it finds its position by re-casting the
                // straight ray, which for a curved beam is a point the beam
                // never visited.
                HitscanWeaponVisual visual = weapon.Visual;

                if (visual != null)
                {
                    var impact = visual.GetComponent<ForgeImpact>();

                    if (impact != null)
                    {
                        // `where` filters by what was struck, so the end of the
                        // beam has to be asked what it landed on. Ready() is
                        // tested LAST because it stamps the next allowed time
                        // as a side effect - asking it about a hit that then
                        // fails the filter would swallow the cooldown and
                        // silence the next one that qualified.
                        Collider2D at = Physics2D.OverlapCircle(
                            end, Mathf.Max(0.2f, weapon.RayWidth),
                            weapon.LayerMask);

                        // Nothing there means the beam simply ran out of
                        // length in mid-air, which is not an impact. Without
                        // this a curve beam that reaches nothing would chirp
                        // its impact sound every tick it was held.
                        if (at != null &&
                            impact.Wants(at.gameObject.layer) &&
                            impact.Ready())
                        {
                            impact.PlayAt(end);
                        }
                    }
                }

                if (!cfg.explodeOnTarget)
                    return;

                if (Time.time < state.nextExplosion)
                    return;

                Explosion ex = weapon.Explosion;

                // SpawnExplosion dereferences damages[0].damageType and throws
                // on an empty list.
                if (ex.damages == null || ex.damages.Count == 0 ||
                    ex.radius <= 0f)
                {
                    return;
                }

                state.nextExplosion =
                    Time.time + Mathf.Max(0.05f, cfg.explodeInterval);

                ex.Owner = weapon.Owner;
                ex.Burn = weapon.GetBurnWithAllAugmentations();

                ServiceLocator.Get<ExplosionManager>().SpawnExplosion(end, ex);
            }

            // Same resolution order the stock FireSingle uses: components on
            // the collider first, then the nearest one up the hierarchy.
            private static IHitscanWeaponListener Listener(Collider2D col)
            {
                IHitscanWeaponListener[] direct =
                    col.GetComponents<IHitscanWeaponListener>();

                for (int i = 0; i < direct.Length; i++)
                {
                    if (direct[i] != null)
                        return direct[i];
                }

                return col.GetComponentInParent<IHitscanWeaponListener>();
            }

            private static bool Friendly(HitscanWeapon weapon, Collider2D col)
            {
                if (weapon.Owner == null)
                    return false;

                Unit unit = col.GetComponentInParent<Unit>();

                if (unit == null)
                    return false;

                return unit == weapon.Owner || weapon.Owner.IsFriendsWith(unit);
            }
        }

        // ------------------------------------------------------------------
        // A weapon is disposed when the ship changes gun. Without this the
        // dictionary would hold every weapon instance for the whole run.
        // ------------------------------------------------------------------
        [HarmonyPatch(typeof(HitscanWeapon), "Dispose")]
        public class OnDispose
        {
            static void Postfix(HitscanWeapon __instance)
            {
                _states.Remove(__instance);
            }
        }
    }
}
