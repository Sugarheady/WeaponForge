using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace WeaponForge
{
    // STOPS A FRIENDLY RAMMER KILLING ITS OWN SIDE.
    //
    // ================================================================
    // THIS FIXES A BUG THIS MOD CAUSED, IN THE ROUND BEFORE THIS ONE
    // ================================================================
    //
    // R10 test 6 was "a friendly Larva cannot damage what it headbutts". The
    // cause was `ChargerHead.collisionLayerMask`, which on Larva / Maggot /
    // Child is 1088 = Player | Ground with no Entities bit - so the ram could
    // only ever hit the player. `ForgeMinionAllegiance.Ram` fixed that by adding
    // Entities and removing Player: 1088 -> 1152.
    //
    // **And EVERY unit in the game is on Entities, including the other minions
    // standing right next to it.** `ChargerHead.OnObjectHit` has no faction test
    // of any kind - it damages whatever the CircleCast returned:
    //
    //     Hazard.IHazardSensor[] components =
    //         hit.collider.GetComponents<Hazard.IHazardSensor>();
    //     for (...) components[i].OnHazardTouched(...);
    //
    // So a swarm of friendly Larvae became a swarm that eats itself. Which is
    // exactly what was reported in R10 part 3: *"I think there's a health drain
    // on them somehow? I tried setting their health to 1 and they just die as
    // soon as I fire the gun."*
    //
    // Every detail of that report fits and none of it fits a "drain":
    //
    //   - `health: 1` dies instantly, `health: 40` looks fine. A minion's health
    //     is a tank with that capacity, so at 1 the FIRST sibling contact kills.
    //     The fratricide was happening at 40 too - it was just survivable.
    //   - `followSpeed`, `followDelay` and `turnSpeed` all made it worse, and
    //     all three do the same thing: push the bodies together harder. More
    //     contacts, more rams.
    //   - The sweep is `velocity.magnitude * deltaTime` long, so a FASTER minion
    //     reaches further ahead and finds a sibling sooner. `speed: 220` was
    //     unplayable and `speed: 90` was not.
    //   - It started "as soon as I fire the gun" - i.e. as soon as there was
    //     more than one of them.
    //
    // THE FIX: only let the ram damage something the rammer's own faction
    // considers an ENEMY. `Unit.IsEnemiesWith` is the game's own test and the
    // same one the AI uses to pick a target, so a ram now hurts exactly what the
    // minion was already trying to attack.
    //
    // Three things are deliberately left alone:
    //
    //   - **Terrain.** A Ground collider has no `Unit`, so it falls straight
    //     through to the original. Dropping that would let Larvae swim through
    //     walls - the same cast is what stops them.
    //   - **Stock enemies.** The guard only looks at prefabs whose mask WE
    //     widened, marked at spawn. An enemy Larva ramming you never enters the
    //     interesting branch.
    //   - **The ram cooldown.** Skipping the original also skips its
    //     `lastDamageTime` write, so bumping a sibling does not consume the ram
    //     the minion was saving for an enemy. That is the behaviour you want and
    //     it comes for free.
    //
    // ================================================================
    // AND THE SAME WIDENING PUT THE RAMMER'S OWN BODY INSIDE ITS OWN
    // DAMAGE CAST. R15 test 184.
    // ================================================================
    //
    // *"So far with the larva and maggots they take ticking damage and pretty
    // much die from being charmed"*, and - the same report, older and never
    // explained - *"i think i figured out the bug why the swarmgun minions hurt
    // themselves"*. One cause for both, and it is this one widening again.
    //
    // `ChargerHead.FixedUpdate` casts a circle of `radius` from the BEAK along
    // the body's velocity. Measured off the four prefabs that have one, the beak
    // circle and the body collider **overlap each other before the sweep even
    // starts**:
    //
    //     Enemy_Larva          cast r 0.39 at 0.61 out, body r 0.39  -> 0.17 in
    //     Enemy_Maggot         cast r 0.51 at 0.67 out, body r 0.51  -> 0.35 in
    //     Enemy_Child          cast r 0.39 at 0.61 out, body r 0.53  -> 0.31 in
    //     Unit Swimmer Maggot  cast r 0.51 at 0.67 out, body r 0.51  -> 0.35 in
    //
    // Every one of those bodies is on **Entities** (layer 7), the bit this mod
    // adds, and `Physics2DSettings` ships `m_QueriesStartInColliders: 1` - so
    // Unity returns a collider the shape is already inside, at fraction 0. The
    // unit's `CircleCollider2D` and its `HealthBase` are both on the root, and
    // `OnObjectHit` hands the damage to `hit.collider.GetComponents<IHazardSensor>()`
    // with no self test of any kind. So a widened rammer headbutts ITSELF every
    // `minDamageDelay` - 0.5s on a Larva, 0.3s on a Maggot - which is exactly
    // the "ticking damage" he described, and it kills a charmed Larva well
    // inside its charm.
    //
    // **And the guard below was letting it through on purpose.** The self case
    // was folded in with terrain as `hurt == null || ReferenceEquals(hurt,
    // rammer) -> return true`, i.e. "let the game have it". Right for terrain,
    // exactly wrong for self.
    //
    // THE SECOND HALF, which the damage fix alone would not give back: a
    // fraction-0 hit is the NEAREST hit there can be, and `CircleCast` returns
    // only the nearest. So the self overlap was also **eating the cast** - the
    // ram could reach an enemy only on a frame where the enemy happened to be
    // returned instead, which is why it read as unreliable rather than as broken.
    // `SweepWithoutSelf` re-does the cast with the rammer's own body filtered
    // out and hands the nearest remaining hit to the original method.
    //
    // > **A fix that widens what a query can see has to be checked against what
    // > the query STARTS inside.** The R10 note below reasoned carefully about
    // > the other units the new bit exposed and never once about the unit doing
    // > the casting.
    //
    // WHY THIS IS PATCHED BY HAND. `ChargerHead` is not visible to this
    // assembly at compile time - `ForgeMinionAllegiance` already has to reach it
    // through `AccessTools.TypeByName` and walk `Component` rather than calling
    // `GetComponentsInChildren<ChargerHead>()`. So there is no `typeof()` to
    // hang a `[HarmonyPatch]` attribute on and `PatchAll` cannot see it.
    public static class ForgeMinionRamGuard
    {
        private static readonly ManualLogSource Log =
            ForgeLog.Source("WeaponForge.Minion");

        // Marks a minion whose ram mask THIS MOD widened. Without it the guard
        // would also sit in front of every stock charger in the game, and a
        // stock charger's mask was never the problem.
        public class Widened : MonoBehaviour { }

        private static bool _patched;
        private static bool _saidSpare;
        private static bool _saidSelf;
        private static bool _warnedSweep;

        // ChargerHead's own state, all private and all needed to redo its cast
        // without the rammer in it. Resolved once in Apply; if any is missing
        // the sweep patch is simply not applied and the damage guard below
        // still stops the self-damage on its own.
        private static FieldInfo _fBody;
        private static FieldInfo _fRadius;
        private static FieldInfo _fMask;
        private static FieldInfo _fDelay;
        private static FieldInfo _fLastDamage;
        private static MethodInfo _mHit;

        private static readonly List<RaycastHit2D> _hits =
            new List<RaycastHit2D>(8);

        private static readonly object[] _hitArg = new object[1];

        private static ContactFilter2D _filter;

        // Only true once a minion has actually had its mask widened, so the
        // prefix costs a bool test in every other game.
        public static bool Any { get; private set; }

        public static void Mark(Unit minion)
        {
            if (minion == null)
                return;

            try
            {
                if (minion.gameObject.GetComponent<Widened>() == null)
                    minion.gameObject.AddComponent<Widened>();

                Any = true;
            }
            catch (Exception e)
            {
                Log.LogError("Marking a minion's ram failed: " + e);
            }
        }

        // Called once from the plugin, straight after PatchAll.
        public static void Apply(Harmony harmony)
        {
            if (_patched || harmony == null)
                return;

            _patched = true;

            try
            {
                Type charger = AccessTools.TypeByName("ChargerHead");

                if (charger == null)
                {
                    Log.LogWarning(
                        "ChargerHead was not found, so friendly rammers cannot " +
                        "be stopped from hurting their own side. Minion \"ram\" " +
                        "weapons (Larva, Maggot, Child) may kill each other.");
                    return;
                }

                MethodInfo hit = AccessTools.Method(charger, "OnObjectHit");

                if (hit == null)
                {
                    Log.LogWarning(
                        "ChargerHead.OnObjectHit was not found - friendly " +
                        "rammers may kill each other.");
                    return;
                }

                harmony.Patch(
                    hit,
                    prefix: new HarmonyMethod(
                        typeof(ForgeMinionRamGuard),
                        nameof(SkipFriendly)));

                _mHit = hit;

                // The second patch is OPTIONAL and deliberately so: it gives
                // the ram its reach back, while the guard above is what stops
                // the self-damage. If any of these five private members has
                // been renamed by a game update, the mod loses the reach and
                // keeps the safety rather than losing both.
                _fBody = AccessTools.Field(charger, "rigidbody");
                _fRadius = AccessTools.Field(charger, "radius");
                _fMask = AccessTools.Field(charger, "collisionLayerMask");
                _fDelay = AccessTools.Field(charger, "minDamageDelay");
                _fLastDamage = AccessTools.Field(charger, "lastDamageTime");

                MethodInfo tick = AccessTools.Method(charger, "FixedUpdate");

                if (tick == null || _fBody == null || _fRadius == null ||
                    _fMask == null || _fDelay == null || _fLastDamage == null)
                {
                    Log.LogWarning(
                        "The ram's own cast could not be rebuilt (ChargerHead " +
                        "has changed shape), so a friendly rammer will still " +
                        "refuse to hurt itself but will connect with an enemy " +
                        "less often than it should - its own body sits inside " +
                        "its own cast and takes the nearest-hit slot.");
                    return;
                }

                // Triggers ON, to match the global m_QueriesHitTriggers: 1 the
                // original cast inherits. useDepth off for the same reason.
                _filter = new ContactFilter2D();
                _filter.useTriggers = true;
                _filter.useDepth = false;
                _filter.useOutsideDepth = false;
                _filter.useNormalAngle = false;
                _filter.useLayerMask = true;

                harmony.Patch(
                    tick,
                    prefix: new HarmonyMethod(
                        typeof(ForgeMinionRamGuard),
                        nameof(SweepWithoutSelf)));
            }
            catch (Exception e)
            {
                // A failed patch must never take the mod down with it: without
                // it minions ram their allies, which is bad; with a throw here
                // nothing loads at all, which is worse.
                Log.LogError("Could not guard the minion ram: " + e);
            }
        }

        // REDOES THE RAM'S OWN CAST WITHOUT THE RAMMER IN IT, for widened
        // prefabs only. See the header: the beak circle starts inside the
        // body collider, `queriesStartInColliders` is on, and the nearest hit
        // is therefore always the rammer itself once Entities is in the mask.
        //
        // Returning false skips the original `FixedUpdate`, which is only the
        // cast plus the call below - nothing else lives in it.
        private static bool SweepWithoutSelf(object __instance)
        {
            if (!Any || _mHit == null || _fBody == null)
                return true;

            try
            {
                var head = __instance as Component;

                if (head == null)
                    return true;

                if (head.GetComponentInParent<Widened>() == null)
                    return true;

                var body = _fBody.GetValue(head) as Rigidbody2D;

                if (body == null)
                    return true;

                // THE RATE GATE IS CHECKED HERE TOO, and it is not an
                // optimisation for its own sake: the original's first statement
                // is this same test and an early return, so skipping the work
                // when it would refuse is exactly equivalent - and it keeps a
                // reflected `Invoke` (which boxes the hit) down to the two or
                // three a second a ram can actually land, rather than fifty.
                float last = (float)_fLastDamage.GetValue(head);
                float delay = (float)_fDelay.GetValue(head);

                if (Time.time - last < delay)
                    return false;

                Vector2 v = body.linearVelocity;
                float radius = (float)_fRadius.GetValue(head);
                LayerMask mask = (LayerMask)_fMask.GetValue(head);

                _filter.SetLayerMask(mask);
                _hits.Clear();

                // Same arguments the original uses, including a zero direction
                // and zero distance when the rammer is stationary - which is an
                // overlap test, and is how a rammer pressed against something
                // keeps hurting it.
                Physics2D.CircleCast(
                    head.transform.position, radius, v, _filter, _hits,
                    v.magnitude * Time.deltaTime);

                RaycastHit2D best = default(RaycastHit2D);
                float bestDistance = float.MaxValue;

                for (int i = 0; i < _hits.Count; i++)
                {
                    RaycastHit2D h = _hits[i];

                    if (h.collider == null)
                        continue;

                    // Ours, by either test: same rigidbody covers a collider on
                    // a child with no Unit of its own, and the Unit compare
                    // covers a detached collider parented under the same unit.
                    if (ReferenceEquals(h.collider.attachedRigidbody, body))
                        continue;

                    if (h.distance < bestDistance)
                    {
                        bestDistance = h.distance;
                        best = h;
                    }
                }

                if (best.collider == null)
                    return false;

                _hitArg[0] = best;
                _mHit.Invoke(head, _hitArg);

                return false;
            }
            catch (Exception e)
            {
                // Falling through to the original is the safe failure: the ram
                // works the way it did before this patch existed, and the
                // damage guard still refuses the self-hit.
                if (!_warnedSweep)
                {
                    _warnedSweep = true;
                    Log.LogError("Minion ram sweep failed, using the game's own cast: " + e);
                }

                return true;
            }
        }

        // Positional `__0` rather than a named parameter: the argument name
        // comes from a decompile and is not something to bet a patch on.
        // `object __instance` because the type does not exist at compile time.
        private static bool SkipFriendly(object __instance, RaycastHit2D __0)
        {
            if (!Any)
                return true;

            try
            {
                var head = __instance as Component;

                if (head == null)
                    return true;

                // Only prefabs whose mask we widened. The marker sits on the
                // Unit, and ChargerHead can be on a child, so search upward.
                if (head.GetComponentInParent<Widened>() == null)
                    return true;

                Unit rammer = head.GetComponentInParent<Unit>();

                if (rammer == null)
                    return true;

                Collider2D col = __0.collider;

                if (col == null)
                    return true;

                // No Unit means terrain or scenery. Let the game have it, or
                // rammers stop stopping at walls.
                Unit hurt = col.GetComponentInParent<Unit>();

                if (hurt == null)
                    return true;

                // ITSELF. This used to be folded into the line above and let
                // through as though it were terrain, and it is the R15 test 184
                // bug: the beak's cast circle starts INSIDE the body collider
                // on all four rammer prefabs, `queriesStartInColliders` is on,
                // and adding Entities to the mask is what made its own body
                // visible to it. So a charmed Larva bit itself twice a second
                // until it died, and so did every summoned one since R10.
                //
                // Nothing wants a headbutt to hurt the thing doing the
                // headbutting, so this needs no config and no exemption - not
                // even for `infighting`, which is about two DIFFERENT converts.
                if (ReferenceEquals(hurt, rammer))
                {
                    if (!_saidSelf)
                    {
                        _saidSelf = true;

                        Log.LogInfo(
                            "minion RAM guard: '" + rammer.name + "' just " +
                            "declined to headbutt ITSELF. Its beak's damage " +
                            "cast starts inside its own body collider, and " +
                            "widening the mask to Entities is what let it see " +
                            "that collider - so before this it took its own " +
                            "ram damage on a timer and could die from being " +
                            "charmed or summoned. Logged once per run.");
                    }

                    return false;
                }

                // INFIGHTING IS AN EXPLICIT EXEMPTION, and it has to be tested
                // before the faction question rather than after it.
                //
                // Two units charmed by you share your faction, so
                // `IsEnemiesWith` is false and this guard refuses the hit -
                // correctly, for your own swarm, and wrongly for a pair you
                // deliberately set against each other. `charm` already carries
                // the identical exemption at the two other places the same
                // thing happens: the game's friendly-fire skip in
                // `Projectile.FixedUpdate`, and charm's own `harmless` guard.
                // This is the third, and it is the melee one.
                //
                // Worth noting WHY it was missed while those two were found:
                // the `harmless` guard hangs off `ForgeKillsPatch.Source`,
                // which is the WEAPON that dealt the damage - and a headbutt
                // has no weapon, so a ram was never subject to that guard and
                // never showed up when it was being reasoned about. **A hit
                // that arrives by a different route is not covered by a rule
                // written at a chokepoint it does not pass through.**
                if (ForgeCharm.AnyInfighting &&
                    ForgeCharm.AreInfightingPeers(rammer, hurt))
                {
                    return true;
                }

                // The game's own question, and the same one the AI asks when it
                // chooses what to chase - so the ram can only ever hurt what the
                // minion was already hunting.
                if (rammer.IsEnemiesWith(hurt))
                    return true;

                if (!_saidSpare)
                {
                    _saidSpare = true;

                    Log.LogInfo(
                        "minion RAM guard: '" + rammer.name + "' just declined " +
                        "to headbutt '" + hurt.name + "' because its faction " +
                        "does not consider that an enemy. ChargerHead has no " +
                        "faction test of its own, so widening its layer mask to " +
                        "reach enemies also pointed it at every other minion in " +
                        "the swarm - this is what stops a swarm eating itself. " +
                        "Logged once per run.");
                }

                return false;
            }
            catch (Exception e)
            {
                Log.LogError("Ram guard failed, letting the hit through: " + e);
                return true;
            }
        }
    }
}
