using System;
using System.Collections.Generic;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace WeaponForge
{
    // ★ ONE OWNER FOR "HOW MUCH DAMAGE ACTUALLY LANDED".
    //
    // `DamagableResource.Damage(float)` is the single chokepoint every damage
    // route in the game funnels through - projectiles, lobbed shots, beams,
    // explosions, missiles, burn ticks. And the number passed IN is not the
    // damage dealt: `Damage` refuses it three silent ways before it touches a
    // tank (an active damage blocker, no tank of that resource type - which is
    // how the damage-type table lets a Crawler resist nearly everything - and
    // i-frames).
    //
    // So the honest measurement is to snapshot the victim's tank in a prefix
    // and diff it in a postfix. `leech` worked that out first and `surge`
    // copied it; by the time `rank` wanted the same number there were TWO
    // byte-identical copies of it, comments included, as two separate patches
    // on the hottest method in the game. Rank would have been a third.
    //
    // This is that measurement, once. Leech, surge and rank are readers.
    //
    // > **The point is not the patch count.** Three copies of one measurement
    // > can drift apart, and nothing would report it: leech could start
    // > counting overkill while surge did not, and the two would disagree about
    // > what a hit was worth on the same weapon in the same shot.
    //
    // WHY A PREFIX **AND** A POSTFIX, when crit needs only a prefix: crit
    // changes the damage on its way in, so it works on the number. This has to
    // know what the number ACHIEVED. It also makes the result free of ordering
    // worries - whatever crit or anything else did to `amount`, the tank delta
    // is the truth.
    //
    // ★ AND THE CARRIER MUST BE `__state`, NEVER A STATIC. `Damage` NESTS: it
    // can call `Die()`, whose spawn-on-death explosion reaches `OnExplosion`
    // and back into `Damage` before the outer call returns. A static snapshot
    // would be overwritten by the inner hit and the outer one would measure the
    // wrong delta. `__state` is per-invocation and cannot be.
    public static class ForgeDamageDealt
    {
        private static readonly ManualLogSource Log =
            ForgeLog.Source("WeaponForge.Damage");

        // What a reader is handed. Every field is already validated: the
        // weapon, attacker and victim are non-null, the victim is not the
        // attacker, and `dealt` is positive with overkill removed.
        public struct Hit
        {
            public WeaponBase weapon;
            public Unit attacker;
            public Unit victim;
            public DamagableResource part;
            public float dealt;

            // Attributed through `ForgeCrit.Begin`/`End` rather than by being a
            // real shot - the mod's own direct damage (gas, swipe, ram, slam,
            // contact, orbit...). `leech` keeps these out unless the file says
            // `onExtras`; `rank` pays for them, because he chose
            // damage-over-time earning XP.
            public bool viaExtras;

            // A burn TICK, attributed to whoever lit the fire rather than to
            // anything on the stack. See ForgeIgnition below.
            public bool viaBurn;
        }

        private class Entry
        {
            public string key;
            public Func<bool> wants;
            public Action<Hit> read;
        }

        // Registered once at load and never removed, so a plain array iterated
        // by index costs nothing per hit. A List<T> enumerator would allocate.
        private static Entry[] _readers = new Entry[0];

        public static void Subscribe(
            string key, Func<bool> wants, Action<Hit> read)
        {
            if (string.IsNullOrEmpty(key) || wants == null || read == null)
                return;

            var list = new List<Entry>(_readers);

            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].key == key)
                {
                    list[i] = new Entry { key = key, wants = wants, read = read };
                    _readers = list.ToArray();
                    return;
                }
            }

            list.Add(new Entry { key = key, wants = wants, read = read });

            _readers = list.ToArray();
        }

        // Is ANY reader interested right now? Checked before the snapshot, so a
        // run with no leech, no surge and no rank pays one loop over an array
        // of three cheap bool getters per damage event - and a damage event is
        // not a per-frame thing.
        public static bool Wanted
        {
            get
            {
                Entry[] readers = _readers;

                for (int i = 0; i < readers.Length; i++)
                {
                    if (readers[i].wants())
                        return true;
                }

                return false;
            }
        }

        private class Scope
        {
            public WeaponBase weapon;
            public Unit attacker;
            public Unit victim;
            public ResourceTank tank;
            public float before;
            public bool viaExtras;
            public bool viaBurn;
        }

        [HarmonyPatch(typeof(DamagableResource), "Damage", new[] { typeof(float) })]
        public class Measure
        {
            static void Prefix(
                DamagableResource __instance, float amount, ref object __state)
            {
                __state = null;

                if (amount <= 0f || !Wanted)
                    return;

                try
                {
                    __state = Open(__instance);
                }
                catch (Exception e)
                {
                    Log.LogWarning("damage snapshot skipped: " + e.Message);
                }
            }

            static void Postfix(DamagableResource __instance, object __state)
            {
                var scope = __state as Scope;

                if (scope == null)
                    return;

                try
                {
                    Close(__instance, scope);
                }
                catch (Exception e)
                {
                    // Measuring damage must never be the thing that breaks
                    // damage.
                    Log.LogWarning("damage measurement skipped: " + e.Message);
                }
            }
        }

        private static Scope Open(DamagableResource victim)
        {
            // WHICH WEAPON DEALT THIS - three channels, in order of confidence.
            //
            //   1. `ForgeKillsPatch.Source` - set by the three routes the game
            //      itself credits a kill through. Null for a burn tick, a gas
            //      cloud or a cell collision.
            //   2. `ForgeCrit.ExtraWeapon` - the mod's own direct damage,
            //      scoped by `ForgeCrit.Begin`/`End` at twelve call sites.
            //   3. The ignition stamp - a burn tick belongs to whoever lit the
            //      fire, and nothing is on the stack to say so.
            WeaponBase weapon = ForgeKillsPatch.Source;
            bool viaExtras = false;
            bool viaBurn = false;

            if (weapon == null)
            {
                weapon = ForgeCrit.ExtraWeapon;
                viaExtras = weapon != null;
            }

            if (weapon == null)
            {
                weapon = ForgeIgnition.TickOwner(victim);
                viaBurn = weapon != null;
            }

            if (weapon == null || weapon.TemplateData == null)
                return null;

            Unit owner = weapon.Owner;

            if (owner == null)
                return null;

            Unit hurt = victim.GetComponentInParent<Unit>();

            if (hurt == null)
                return null;

            // ★★ DAMAGING YOUR OWN SIDE LANDS, AND IT DOES NOT PAY.
            //
            // This guard used to refuse only `hurt == owner`, under a comment
            // stating the intent exactly: *"Shooting your own hull is not
            // landing a hit, and a weapon that paid you for it would be a
            // strange kind of infinite."* Right idea, wrong scope - **it
            // guarded the hull, not the side.** A minion, a charmed unit or a
            // wingman drone body is not `owner`, so damage to one was measured
            // as damage dealt and handed to every reader: `rank` banked XP for
            // it, `leech` healed off it, `surge` filled a meter with it.
            //
            // > **A guard written for the exceptional path is not a guard for
            // > the ordinary one.** Already a rule here, from the fire-rate
            // > clock. "Your own hull" is the exceptional self-hit; "anything
            // > on your side" is the ordinary one.
            //
            // That was live long before drone wingmen - with `minion`, `charm`
            // and the stock drone gadget - and nobody hit it because those
            // units are rarely in your own line of fire. A wingman ring puts
            // four to eight friendly bodies inside your own muzzle blast
            // permanently, so the feature does not create the hole, it makes it
            // the default experience. `leech` is the expensive one: deploy,
            // shoot your own companions, heal - a straight resource-to-health
            // converter capped by nothing.
            //
            // `IsFriendsWith` is the game's own question, and the same one the
            // friendly-fire skip in `Projectile.FixedUpdate` asks. It rests on
            // `Player.IsFriendsWith(Player)` being true, which is NOT obvious -
            // a faction listing itself as its own ally is a modelling choice,
            // and `IsFriendsWith` is a plain `faction.Allies.Contains(other
            // .faction)` with no self case. Checked: `Player.asset` ships
            // `allies: [ {fileID: 11400000} ]`, a self-reference. Had it not,
            // this would have been a silent no-op that tested clean against
            // enemies.
            //
            // Deliberately the PATCHED `IsFriendsWith` rather than
            // `ForgeCharm.RawFriends`: `charm`'s `playerCanHit` makes a charmed
            // unit stop counting as a friend precisely so your weapons can
            // engage it, and a weapon the author has declared may shoot
            // something should be paid for shooting it.
            // The hull test is KEPT as well as widened. Self-friendship is a
            // property of the faction asset, and only the Player one was
            // checked - an enemy faction that does not list itself would
            // otherwise start paying for self-damage, which is a behaviour
            // change nobody asked for in a method this widely read.
            if (hurt == owner || owner.IsFriendsWith(hurt))
                return null;

            // `DamagableResource.Tank` resolves through `Unit.GetTank`, which is
            // NOT guarded - ask `HasTank` first, exactly as `Damage()` does
            // before it touches the tank.
            if (!hurt.HasTank(victim.resource))
                return null;

            ResourceTank tank = hurt.GetTank(victim.resource);

            if (tank == null)
                return null;

            return new Scope
            {
                weapon = weapon,
                attacker = owner,
                victim = hurt,
                tank = tank,
                before = tank.Value,
                viaExtras = viaExtras,
                viaBurn = viaBurn
            };
        }

        private static void Close(DamagableResource part, Scope scope)
        {
            if (scope.tank == null)
                return;

            float dealt = scope.before - scope.tank.Value;

            // Nothing landed: blocked, i-framed, or the wrong element entirely.
            // That is why this counts HITS here rather than at `Projectile
            // .TryHit` - a shot that connected and achieved nothing is not a
            // hit worth paying for.
            if (dealt <= 0f)
                return;

            // ★ OVERKILL DOES NOT PAY. `ResourceTank.Value` has no floor - the
            // game writes `tank.Value -= amount` - so a 200-damage rocket into
            // a 5-health fly leaves the tank at -195 and the raw delta reads
            // 200. Without this one rocket into a gnat would fill any meter in
            // the game, top up any leech and rank a gun in a single shot.
            if (scope.before > 0f)
                dealt = Mathf.Min(dealt, scope.before);

            var hit = new Hit
            {
                weapon = scope.weapon,
                attacker = scope.attacker,
                victim = scope.victim,
                part = part,
                dealt = dealt,
                viaExtras = scope.viaExtras,
                viaBurn = scope.viaBurn
            };

            Entry[] readers = _readers;

            for (int i = 0; i < readers.Length; i++)
            {
                Entry e = readers[i];

                if (!e.wants())
                    continue;

                try
                {
                    e.read(hit);
                }
                catch (Exception ex)
                {
                    // ONE READER MUST NOT TAKE THE OTHERS DOWN. They are
                    // independent features that happen to share a measurement.
                    Log.LogWarning(
                        "damage reader '" + e.key + "' threw: " + ex.Message);
                }
            }
        }
    }

    // ★ WHO LIT THE FIRE.
    //
    // `DamagableResource.Update` ticks burn damage as
    // `this.Damage(burnProperties.fireDmgPerTick)` with NOTHING on the stack to
    // say where the fire came from - no projectile, no weapon, no owner. So a
    // burn build would have earned no rank XP from its burn at all, which is
    // the opposite of the answer he gave on 2026-09-18 (damage-over-time earns
    // XP like any other damage), and it would have failed silently.
    //
    // Two facts make this exact rather than a guess:
    //
    //   - **`ApplyBurn` is called from exactly three places**, and all three are
    //     inside the same methods that set `ForgeKillsPatch.Source`
    //     (`ProjectileCollided`, `OnHitByHitscanWeapon`, `OnExplosion`). So at
    //     the moment a unit catches fire, the weapon IS knowable.
    //   - **The burn tick is the only `Damage` call inside `Update`.** So
    //     "we are inside Update" identifies a burn tick precisely, with no need
    //     to infer it from the unit being on fire - which would also have
    //     claimed lava and hazard-cell damage on a burning enemy.
    //
    // The stamp EXPIRES. A unit you set alight and then leave should stop
    // paying you once your fire has burned out and something else has relit it.
    public class ForgeIgnition : MonoBehaviour
    {
        // Longer than the fire it describes: `coolingSpeed` puts a typical unit
        // out in a couple of seconds, so this only has to outlive the fire, not
        // predict it.
        private const float Keep = 12f;

        [NonSerialized] public WeaponBase weapon;
        [NonSerialized] public float stamp;

        private static bool _inTick;
        private static DamagableResource _ticking;

        public static bool Any;

        public static void Stamp(DamagableResource part, WeaponBase weapon)
        {
            if (part == null || weapon == null)
                return;

            Unit unit = part.GetComponentInParent<Unit>();

            if (unit == null)
                return;

            var mark = unit.GetComponent<ForgeIgnition>();

            if (mark == null)
                mark = unit.gameObject.AddComponent<ForgeIgnition>();

            mark.weapon = weapon;
            mark.stamp = Time.time;
        }

        // Non-null only while we are inside the burn tick of THIS part, and
        // only when the stamp is still live.
        public static WeaponBase TickOwner(DamagableResource part)
        {
            if (!_inTick || part == null || !ReferenceEquals(part, _ticking))
                return null;

            Unit unit = part.GetComponentInParent<Unit>();

            if (unit == null)
                return null;

            var mark = unit.GetComponent<ForgeIgnition>();

            if (mark == null || mark.weapon == null ||
                Time.time - mark.stamp > Keep)
            {
                return null;
            }

            return mark.weapon;
        }

        [HarmonyPatch(typeof(DamagableResource), "ApplyBurn")]
        public class OnApplyBurn
        {
            static void Postfix(DamagableResource __instance)
            {
                if (!Any)
                    return;

                try
                {
                    Stamp(__instance, ForgeKillsPatch.Source);
                }
                catch
                {
                    // Attribution is a nicety; burning is not.
                }
            }
        }

        // The window that says "the Damage call you are about to see is a burn
        // tick". One bool in the common case, and the pair is symmetric so a
        // throw inside the original cannot leave the flag stuck - a postfix
        // runs even when the original throws only if a finalizer is used, so
        // the flag is also cleared defensively on every prefix entry.
        [HarmonyPatch(typeof(DamagableResource), "Update")]
        public class OnUpdate
        {
            static void Prefix(DamagableResource __instance)
            {
                if (!Any)
                    return;

                _inTick = true;
                _ticking = __instance;
            }

            static void Finalizer()
            {
                _inTick = false;
                _ticking = null;
            }
        }
    }
}
