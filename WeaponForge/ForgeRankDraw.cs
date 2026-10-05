using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using BepInEx.Logging;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Events;

namespace WeaponForge
{
    // Choosing what a rank gives you.
    //
    // Three modes, all of which he asked to ship: `order` walks the pool as
    // authored, `random` takes one at random, and `pickup` puts a few choices
    // out and lets you take the one you want.
    //
    // ★ A DRAWN FEATURE DOES NOT LEAVE THE POOL, and that is arithmetic rather
    // than taste. At `maxRank: 9999` - which he explicitly wanted available,
    // *"in case I want a level 9999 weapon"* - a pool that empties would leave
    // thousands of ranks paying nothing. So a repeat draw DEEPENS the feature
    // instead, through `onRepeat`.
    //
    // ★ EVERYTHING IS PER COPY OF THE GUN. The owed count lives on the copy's
    // progress (so it survives a save), and each set of choices belongs to one
    // copy.
    //
    // ★ BUT ONLY ONE SET IS OUT PER SHIP (R20 256). Two ranked guns used to be
    // two sets on the SAME ring, drawn exactly on top of each other: *"the two
    // ranked choices stack ontop of each other ... i think i may glitched one
    // instance of the weapon where it got the bonus of the other ranked
    // prospector"* - he had taken one of the OTHER gun's choices, which looked
    // identical. His ask: *"one instance of rank choice out at a time even if
    // there are two weapons that are ranked ... a small text saying which
    // weapon and equip slot"*. So owed sets wait in `ForgeRankQueue` and come
    // out one at a time, alternating between guns, and each choice names the
    // gun and the slot it is for.
    //
    // ★ THE OWED COUNT IS THE ONLY TRUTH. A set on screen is a view of it, so
    // the queue brings a set back whenever one is owed and none is out - after
    // a load (R20 256: *"saving and quiting with a rank choice out and coming
    // back removes that level choice"*), after the ship was gone, or after a
    // set was lost for any reason nobody found. Nothing has to notice the loss.
    public static class ForgeRankDraw
    {
        private static readonly ManualLogSource Log =
            ForgeLog.Source("WeaponForge.Rank");

        public static void Reset()
        {
            ForgeRankChoice.ClearAll();
            ForgeRankQueue.Reset();
        }

        // One rank gained. Decide what it gives.
        public static void Owe(ForgeRankCopies.Copy copy)
        {
            if (copy == null || copy.cfg == null || copy.p == null ||
                copy.cfg.unlocks.Count == 0)
            {
                return;
            }

            try
            {
                if (copy.cfg.draw == ForgeRank.Draw.Pickup)
                {
                    copy.p.owed++;
                    copy.p.dirty = true;

                    // Its place in line: the gun that has waited longest goes
                    // first. Ranks gained meanwhile sit in the counter.
                    if (copy.owedSince <= 0f)
                        copy.owedSince = Time.time;

                    ForgeRankQueue.Kick();
                    return;
                }

                Take(copy.cfg, copy.p, Pick(copy.cfg, copy.p));
            }
            catch (Exception e)
            {
                Log.LogWarning("rank draw skipped: " + e.Message);
            }
        }

        // The gun was (re)built - after a load, say. Anything it still owes
        // is the queue's to bring out.
        public static void Resume(ForgeRankCopies.Copy copy)
        {
            if (copy == null || copy.p == null || copy.p.owed <= 0 ||
                copy.cfg == null || copy.cfg.draw != ForgeRank.Draw.Pickup)
            {
                return;
            }

            ForgeRankQueue.Kick();
        }

        // Which feature this rank gives, for the two non-interactive modes.
        private static string Pick(ForgeRank.Config cfg, ForgeRank.Progress p)
        {
            List<string> pool = cfg.unlocks;

            if (pool.Count == 0)
                return null;

            if (cfg.draw == ForgeRank.Draw.Random)
                return pool[UnityEngine.Random.Range(0, pool.Count)];

            // ORDER: straight down the list as authored, then round again. The
            // index is the TOTAL number of draws taken, so the cycle is exact
            // and survives a save/load - it is derived from stored counts
            // rather than from a cursor that would have to be stored too.
            int taken = 0;

            foreach (var pair in p.draws)
                taken += pair.Value;

            return pool[taken % pool.Count];
        }

        // Record a draw. The payout itself happens in ForgeRankPatch.Apply,
        // which is the only thing that touches the JSON.
        public static void Take(
            ForgeRank.Config cfg, ForgeRank.Progress p, string feature)
        {
            if (cfg == null || p == null || string.IsNullOrEmpty(feature))
                return;

            p.Draw(feature);
        }

        // ------------------------------------------------------------------
        // Called by the queue only, once it has decided this copy is next.
        // Returns whether anything was put out.
        internal static bool Spawn(ForgeRankCopies.Copy copy, Unit ship)
        {
            if (ship == null)
                return false;

            ForgeRank.Config cfg = copy.cfg;

            // Offer distinct choices where the pool allows it, but never fewer
            // than one - a pool of two cannot offer three.
            int want = Mathf.Clamp(cfg.pickupCount, 1, cfg.unlocks.Count);

            var offered = new List<string>();
            var left = new List<string>(cfg.unlocks);

            for (int i = 0; i < want && left.Count > 0; i++)
            {
                int at = UnityEngine.Random.Range(0, left.Count);

                offered.Add(left[at]);
                left.RemoveAt(at);
            }

            string gun = GunName(copy);
            string slot = SlotOf(copy, ship);
            object set = ForgeRankChoice.BeginSet(ship, copy, offered.Count);

            int made = 0;

            for (int i = 0; i < offered.Count; i++)
            {
                if (ForgeRankChoice.Make(
                        set, offered[i], Describe(copy, offered[i]),
                        "for " + gun + " - " + slot, i, offered.Count))
                {
                    made++;
                }
            }

            if (made == 0)
            {
                ForgeRankChoice.ClearSet(copy.Key);
                return false;
            }

            // SAID, because R19 test 252 had nothing to say at first - and
            // now it names what each one does, which is the text on the pickup.
            var sb = new StringBuilder();

            for (int i = 0; i < offered.Count; i++)
            {
                if (sb.Length > 0)
                    sb.Append("; ");

                sb.Append(offered[i].ToUpperInvariant()).Append(" - ")
                  .Append(Describe(copy, offered[i]));
            }

            int waiting = ForgeRankQueue.OwedOn(copy.shipId) - 1;

            Log.LogInfo(
                cfg.fileName + ": rank " + copy.p.rank + " - " + made +
                " choice(s) for " + gun + " (" + slot + ") are following your " +
                "ship (" + sb + "). Fly up to the one you want and press the " +
                "use key; the others vanish. " + ForgeRankKeys.Describe() +
                " puts them away and brings them back." +
                (waiting > 0
                    ? " " + waiting + " more set(s) are owed across your ranked " +
                      "guns and come out one at a time, taking turns."
                    : ""));

            return true;
        }

        // The gun's name as its card shows it.
        public static string GunName(ForgeRankCopies.Copy copy)
        {
            try
            {
                string n = (copy.module != null) ? copy.module.DisplayName : null;

                return string.IsNullOrEmpty(n)
                    ? copy.cfg.fileName.ToUpperInvariant()
                    : n.ToUpperInvariant();
            }
            catch (Exception)
            {
                return (copy != null && copy.cfg != null) ? copy.cfg.fileName : "?";
            }
        }

        // Which slot the copy sits in on this ship - R20 256, *"which weapon
        // and equip slot it is"*. Two copies of one gun differ ONLY by this.
        public static string SlotOf(ForgeRankCopies.Copy copy, Unit ship)
        {
            try
            {
                var owner = (ship != null)
                    ? ship.GetComponentInChildren<ModuleGridOwner>(true)
                    : null;

                if (owner != null && owner.ModuleGrid != null)
                {
                    foreach (IModuleCluster c in owner.ModuleGrid.GetAllClusters())
                    {
                        if (c != null && c.HasMainModule &&
                            ReferenceEquals(c.MainModule, copy.module))
                        {
                            return SlotWord(c.ClusterType);
                        }
                    }
                }
            }
            catch (Exception)
            {
            }

            return "IN STORAGE";
        }

        private static string SlotWord(ClusterType t)
        {
            switch (t)
            {
                case ClusterType.PrimaryWeapon: return "PRIMARY";
                case ClusterType.SecondaryWeapon: return "SECONDARY";
                case ClusterType.Active1: return "GADGET 1";
                case ClusterType.Active2: return "GADGET 2";
                case ClusterType.Active3: return "GADGET 3";
                default: return t.ToString().ToUpperInvariant();
            }
        }

        // ★ THE LINE UNDER THE NAME. His ask: "I dont know my choices ...
        // normally those modules will have text with them." An author's own
        // words win (`rank.describe`); a repeat says what deepens; a first
        // draw of a feature this mod knows says what it does, from the block's
        // own numbers; anything else says it switches the feature on.
        public static string Describe(ForgeRankCopies.Copy copy, string feature)
        {
            ForgeRank.Config cfg = copy.cfg;
            string own;

            if (cfg.describe.TryGetValue(feature, out own) && !string.IsNullOrEmpty(own))
                return own;

            int times = copy.p.DrawCount(feature);

            Dictionary<string, ForgeRank.Delta> deltas;

            if (times > 0 && cfg.onRepeat.TryGetValue(feature, out deltas) &&
                deltas != null && deltas.Count > 0)
            {
                var parts = new List<string>();

                foreach (var d in deltas)
                {
                    parts.Add(d.Key + " " +
                        (d.Value.amount >= 0f ? "+" : "") +
                        d.Value.amount.ToString("0.##", CultureInfo.InvariantCulture) +
                        (d.Value.percent ? "%" : ""));
                }

                return "stronger (" + string.Join(", ", parts.ToArray()) +
                       ") - you have it x" + times;
            }

            if (times > 0)
                return "you have it x" + times + " (a repeat changes nothing - " +
                       "no onRepeat for it)";

            JObject block = (cfg.source != null) ? cfg.source[feature] as JObject : null;

            return Known(feature, block) ?? ("switches " + feature + " on");
        }

        private static string Known(string feature, JObject b)
        {
            if (b == null)
                return null;

            Func<string, float, float> num = (k, d) =>
            {
                try { return b[k] != null ? (float)b[k] : d; }
                catch (Exception) { return d; }
            };

            switch (feature.ToLowerInvariant())
            {
                case "chain":
                    return "shots jump to " + num("jumps", 1) + " more " +
                           (num("jumps", 1) == 1 ? "enemy" : "enemies");
                case "split":
                    return "shots burst into " + num("count", 3) + " shards";
                case "grow":
                    return "shots swell to x" + num("to", 2) + " as they fly";
                case "gas":
                    return "shots leave a burning gas cloud";
                case "homing":
                    return "shots curve toward enemies";
                case "ricochet":
                    return "shots bounce off walls";
                case "boomerang":
                    return "shots come back to you";
                case "crit":
                    return "a chance to hit much harder";
                case "leech":
                    return "hits heal you";
                case "slow":
                    return "hits slow enemies";
                case "stun":
                    return "hits stun enemies";
                case "execute":
                    return "finishes off badly hurt enemies";
                case "charm":
                    return "hits can turn enemies to your side";
                case "orbit":
                    return "shots circle you";
                case "wave":
                    return "shots weave as they fly";
            }

            return null;
        }

        // A choice was taken: record it, pay it out, and bring the next set if
        // more are owed.
        public static void Claimed(ForgeRankCopies.Copy copy, string feature)
        {
            if (copy == null)
                return;

            Take(copy.cfg, copy.p, feature);

            if (copy.p.owed > 0)
                copy.p.owed--;

            copy.p.dirty = true;

            // TO THE BACK OF THE LINE, so two ranked guns owing sets take
            // turns rather than one emptying its queue first.
            copy.owedSince = (copy.p.owed > 0) ? Time.time : 0f;

            ForgeRankChoice.ClearSet(copy.Key);

            Unit ship = ForgeRank.ShipById(copy.shipId);

            // PAY IT OUT NOW. The two non-interactive modes are resolved inside
            // the rank-up, so `Apply` runs once for the whole batch; a pickup
            // is claimed later, by hand, with nothing else on the stack - so it
            // has to ask for the payout itself or the pickup would vanish and
            // nothing would happen.
            ForgeRankPatch.Apply(copy, ship);

            Log.LogInfo(
                copy.cfg.fileName + ": rank - took " + feature.ToUpperInvariant() +
                " for " + GunName(copy) + " (" + SlotOf(copy, ship) + ") - now x" +
                copy.p.DrawCount(feature) + ", on the gun now.");

            // The next set, from whichever gun has waited longest.
            ForgeRankQueue.Kick();
        }
    }

    // ★ THE ONE PLACE A SET OF CHOICES IS DECIDED TO COME OUT.
    //
    // A clock object that exists only while some ranked gun owes a set (or a
    // set is out). Four times a second it asks, per ship: is a set already out
    // (hidden ones count)? If not, which carried gun has waited longest? And
    // is the ship ready - flying, not paused, and a moment past a load, so a
    // set is never put out into a scene that is still being built?
    //
    // It also reads the put-away key every frame, which is why it lives while
    // a set is out and not only while one is waiting.
    internal class ForgeRankQueue : MonoBehaviour
    {
        private static readonly ManualLogSource Log =
            ForgeLog.Source("WeaponForge.Rank");

        private static ForgeRankQueue _one;
        private static float _nextThink;
        private static readonly List<Unit> _pressed = new List<Unit>();

        // A set waits this long after a run begins (a new run or a continue)
        // rather than landing on the first frame the ship exists. Stamped from
        // the session-monotonic clock at run entry - this mod never times
        // anything with a per-scene clock (teardowntest.py pins that).
        private const float Settle = 1.25f;
        private static float _runAt;

        public static void Kick()
        {
            if (_one != null)
                return;

            var go = new GameObject("ForgeRankQueue");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            _one = go.AddComponent<ForgeRankQueue>();
        }

        // Run entry (ForgeBurnResetPatch, both entry points).
        public static void Reset()
        {
            _nextThink = 0f;
            _runAt = Time.unscaledTime;
        }

        // How many sets the carried guns of this ship owe between them,
        // counting the one that is out.
        public static int OwedOn(int shipId)
        {
            int n = 0;

            foreach (ForgeRankCopies.Copy c in ForgeRankCopies.All())
            {
                if (c != null && c.p != null && c.cfg != null &&
                    c.cfg.draw == ForgeRank.Draw.Pickup && c.shipId == shipId)
                {
                    n += Mathf.Max(0, c.p.owed);
                }
            }

            return n;
        }

        private void Update()
        {
            bool keep;

            try
            {
                // Per player: each ship's own key puts away only its own sets.
                _pressed.Clear();
                ForgeRankKeys.PressedBy(_pressed);

                for (int i = 0; i < _pressed.Count; i++)
                    ForgeRankChoice.ToggleFor(ForgeWingmanSave.IdOf(_pressed[i]));

                if (Time.unscaledTime < _nextThink)
                    return;

                _nextThink = Time.unscaledTime + 0.25f;

                keep = Think() || ForgeRankChoice.AnyLive();
            }
            catch (Exception e)
            {
                Log.LogWarning("rank choice queue skipped a beat: " + e.Message);
                keep = true;
            }

            if (!keep)
            {
                _one = null;
                Destroy(gameObject);
            }
        }

        // Returns whether anything is still owed anywhere.
        private static bool Think()
        {
            bool owing = false;
            var best = new Dictionary<int, ForgeRankCopies.Copy>();

            foreach (ForgeRankCopies.Copy c in ForgeRankCopies.All())
            {
                if (c == null || c.p == null || c.cfg == null ||
                    c.cfg.draw != ForgeRank.Draw.Pickup || c.p.owed <= 0)
                {
                    continue;
                }

                owing = true;

                if (ForgeRankChoice.AnyOutForShip(c.shipId))
                    continue;

                Unit ship = ForgeRank.ShipById(c.shipId);

                if (ship == null || !Ready(ship, c.shipId))
                    continue;

                // Only for a gun you are carrying - a set for a gun in the
                // vault would be a choice about something you cannot see.
                if (!ForgeRankStation.Carried(ship).Contains(c))
                    continue;

                ForgeRankCopies.Copy have;

                // Longest waiting first. A copy handed back by a save has no
                // place yet (0), so it goes before anything earned since.
                if (!best.TryGetValue(c.shipId, out have) || c.owedSince < have.owedSince)
                    best[c.shipId] = c;
            }

            foreach (var pair in best)
            {
                Unit ship = ForgeRank.ShipById(pair.Key);

                try
                {
                    ForgeRankDraw.Spawn(pair.Value, ship);
                }
                catch (Exception e)
                {
                    Log.LogWarning("rank choices not put out: " + e.Message);
                }
            }

            return owing;
        }

        private static bool Ready(Unit ship, int id)
        {
            // Paused, or a menu that stops time.
            if (Time.timeScale <= 0f)
                return false;

            // A moment into the run. Mid-run it is long past, so a rank up is
            // not delayed; the controls check below covers the rest of a load.
            if (Time.unscaledTime - _runAt < Settle)
                return false;

            // The ship's own flying controls: off in the map, a shop, the
            // start sequence and every menu - all times a choice cannot be
            // flown to anyway.
            try
            {
                ShipInput input = ship.GetComponentInChildren<ShipInput>(true);

                if (input != null && input.ShipControlActionMap != null &&
                    !input.ShipControlActionMap.Enabled)
                {
                    return false;
                }
            }
            catch (Exception)
            {
            }

            return true;
        }
    }

    // One floating choice.
    //
    // ★ HIS PICK, R19 252: MODULE-STYLE PICKUPS. "I figured you would spawn a
    // module as options and i would just pick up the module for the upgrade on
    // the rank weapon? theres a pick up button ... normally those modules will
    // have text with them." So a choice is built from the game's OWN module
    // pickup prefab - its icon, its background, its "PICK UP" prompt and the
    // use key the ship's `Interactor` already routes to an `Interactable` -
    // with everything that makes it a real pickup stripped off.
    //
    // ★ ALLOW-LIST, NOT DENY-LIST, on the borrowed prefab. The root carries the
    // save entity and the pickup behaviour (and the game's `Pickup` magnet
    // logic has already been ruled out here once: it flies at you and takes
    // itself). Everything goes except the prompt's own UI, the lights and the
    // `Interactable` - losing a flourish is visible, keeping a save entity is a
    // pickup that comes back from the next load pointing at nothing.
    //
    // ★ NOT SAVED. A choice is not an entity, so a save made with choices out
    // stores them as `owed` on the copy's progress instead, and
    // `ForgeRankQueue` puts them back out once the ship is flying again.
    //
    // If the prefab cannot be found or dressed, the choice falls back to the
    // old orb - still collected by the use key, never by touch.
    public class ForgeRankChoice : MonoBehaviour
    {
        private static readonly ManualLogSource Log =
            ForgeLog.Source("WeaponForge.Rank");

        private static readonly List<ForgeRankChoice> _live =
            new List<ForgeRankChoice>();

        // ONE SET = the choices one rank offers one copy. It owns where the
        // choices sit (they follow it), so the ring can be moved, re-laid out
        // clear of walls, put away and brought back as one thing.
        private sealed class Set
        {
            public string key;
            public int shipId;
            public Unit ship;
            public ForgeRankCopies.Copy copy;
            public int count;
            public readonly List<ForgeRankChoice> members = new List<ForgeRankChoice>();

            public Vector3 home;
            public float angle;              // the ring's turn, degrees
            public float radius = Ring;
            public bool hidden;

            public int tickedFrame = -1;
            public float nextCheck;
        }

        private static readonly List<Set> _sets = new List<Set>();

        private Set _set;
        private Unit _ship;
        private ForgeRankCopies.Copy _copy;
        private string _key;
        private string _feature;
        private int _index;
        private int _count;
        private bool _taken;

        // Where the set sits relative to the ship, and how far the ship may
        // wander before the set follows it. Inside the leash the choices hold
        // STILL, so flying up to one is a real choice; outside it they catch
        // up, so a reward cannot be left behind. Past `Snap` (a teleport, a
        // load) they jump rather than crossing the map.
        private const float Ring = 2.2f;
        private const float Leash = 7f;
        private const float CatchUp = 14f;
        private const float Snap = 30f;

        // How close the use key can take a choice from when the game's own
        // interact route did not see it (the fallback, see OnUse).
        private const float Reach = 1.8f;

        // Is a set for this copy out? Hidden ones count - they are still owed
        // and still this copy's.
        public static bool AnyOut(string key)
        {
            Prune();

            for (int i = 0; i < _sets.Count; i++)
            {
                if (_sets[i].key == key)
                    return true;
            }

            return false;
        }

        // Is ANY set out for this ship? One at a time per ship (R20 256).
        public static bool AnyOutForShip(int shipId)
        {
            Prune();

            for (int i = 0; i < _sets.Count; i++)
            {
                if (_sets[i].shipId == shipId)
                    return true;
            }

            return false;
        }

        public static bool AnyLive()
        {
            Prune();
            return _sets.Count > 0;
        }

        // A set whose ship is gone, or whose every member is, is not out.
        // Its rank is still owed, so the queue brings it back when it can.
        private static void Prune()
        {
            for (int i = _sets.Count - 1; i >= 0; i--)
            {
                Set s = _sets[i];

                bool anyMember = false;

                for (int m = 0; m < s.members.Count; m++)
                {
                    if (s.members[m] != null && !s.members[m]._taken)
                    {
                        anyMember = true;
                        break;
                    }
                }

                if (s.ship == null || !anyMember)
                {
                    for (int m = 0; m < s.members.Count; m++)
                    {
                        if (s.members[m] != null)
                        {
                            s.members[m]._taken = true;
                            Destroy(s.members[m].gameObject);
                        }
                    }

                    _sets.RemoveAt(i);
                }
            }

            for (int i = _live.Count - 1; i >= 0; i--)
            {
                if (_live[i] == null)
                    _live.RemoveAt(i);
            }
        }

        // ★ ONLY THIS KEY'S SET. The first version removed EVERY entry from
        // `_live` and destroyed only the matching ones, which is fine while one
        // ranked weapon is on the ship and wrong the moment there are two:
        // taking a choice for the primary de-listed the secondary's orbs
        // without destroying them, so they stayed in the world, still
        // collectable, while `AnyOut` reported none outstanding - and the next
        // rank spawned a SECOND set over the top of the orphans. Every orphan
        // is still a live unlock, so the gun gained more features than it had
        // ranks. Two ranked weapons on one ship is an ordinary loadout, not an
        // edge case.
        public static void ClearSet(string key)
        {
            for (int i = _live.Count - 1; i >= 0; i--)
            {
                ForgeRankChoice c = _live[i];

                // A destroyed orb is dead weight in the list either way.
                if (c == null)
                {
                    _live.RemoveAt(i);
                    continue;
                }

                if (c._key != key)
                    continue;

                // Taken BEFORE the deferred Destroy lands, so a second handler
                // on the same key press cannot take a second choice from the
                // same set.
                c._taken = true;
                _live.RemoveAt(i);

                Destroy(c.gameObject);
            }

            for (int i = _sets.Count - 1; i >= 0; i--)
            {
                if (_sets[i].key == key)
                    _sets.RemoveAt(i);
            }
        }

        public static void ClearAll()
        {
            for (int i = 0; i < _live.Count; i++)
            {
                if (_live[i] != null)
                {
                    _live[i]._taken = true;
                    Destroy(_live[i].gameObject);
                }
            }

            _live.Clear();
            _sets.Clear();
        }

        // A new set for this copy, laid out clear of walls and stations
        // around the ship. Handed back as an object so the draw code never
        // needs the class.
        public static object BeginSet(Unit ship, ForgeRankCopies.Copy copy, int count)
        {
            var s = new Set
            {
                key = copy.Key,
                shipId = copy.shipId,
                ship = ship,
                copy = copy,
                count = Mathf.Max(1, count),
                home = ship.transform.position
            };

            Relayout(s);
            _sets.Add(s);

            return s;
        }

        public static bool Make(
            object setObj, string feature, string line, string forWhom,
            int index, int count)
        {
            var set = setObj as Set;

            if (set == null || set.ship == null)
                return false;

            Unit ship = set.ship;
            ForgeRankCopies.Copy copy = set.copy;
            GameObject go = null;

            try
            {
                go = FromPickupPrefab(copy, feature, line, forWhom);
            }
            catch (Exception e)
            {
                if (!_saidNoPrefab)
                {
                    _saidNoPrefab = true;

                    Log.LogWarning(
                        "rank: the game's module pickup could not be borrowed " +
                        "for the choices (" + e.GetType().Name + ": " +
                        e.Message + "), so they are plain orbs this run - still " +
                        "taken with the use key.");
                }

                if (go != null)
                    Destroy(go);

                go = null;
            }

            if (go == null)
                go = Orb(ship, index, count);

            if (go == null)
                return false;

            go.transform.position = set.home + Offset(set, index);

            var c = go.AddComponent<ForgeRankChoice>();

            c._set = set;
            c._ship = ship;
            c._copy = copy;
            c._key = copy.Key;
            c._feature = feature;
            c._index = index;
            c._count = Mathf.Max(1, count);

            _live.Add(c);
            set.members.Add(c);

            Listen(ship);

            return true;
        }

        // ------------------------------------------------------------------
        // Put away / bring back - R20 252
        // ------------------------------------------------------------------
        //
        // *"an option to move the 3 choices sometimes away from where it
        // spawns. if im too close to a station sometimes the upgrade choice is
        // in a wall or the station ... a key press to move it as well or hide
        // it for the time being then bring it back."* One key does both: a
        // press puts every set away, the next brings them back around the ship
        // wherever it is now, laid out clear of the walls there.
        //
        // ★ ONE PLAYER'S SETS ONLY (drone modes' co-op check, 12.2 N). In
        // co-op each ship's key toggles that ship's choices and leaves the
        // other player's where they are.
        public static void ToggleFor(int shipId)
        {
            Prune();

            if (_sets.Count == 0)
                return;

            bool anyShown = false;
            bool any = false;

            foreach (Set s in _sets)
            {
                if (s.shipId != shipId)
                    continue;

                any = true;

                if (!s.hidden)
                {
                    anyShown = true;
                    break;
                }
            }

            if (!any)
                return;

            foreach (Set s in _sets)
            {
                if (s.shipId != shipId)
                    continue;

                if (anyShown)
                {
                    if (s.hidden)
                        continue;

                    s.hidden = true;

                    foreach (ForgeRankChoice c in s.members)
                    {
                        if (c != null)
                            c.gameObject.SetActive(false);
                    }
                }
                else
                {
                    s.hidden = false;

                    if (s.ship != null)
                        s.home = s.ship.transform.position;

                    Relayout(s);

                    foreach (ForgeRankChoice c in s.members)
                    {
                        if (c == null)
                            continue;

                        c.transform.position = s.home + Offset(s, c._index);
                        c.gameObject.SetActive(true);
                    }
                }
            }

            Log.LogInfo(
                anyShown
                    ? "rank: choices put away - they are still owed, and " +
                      ForgeRankKeys.Describe() + " brings them back around you."
                    : "rank: choices brought back around your ship.");
        }

        // ------------------------------------------------------------------
        // Where the ring goes - clear of terrain and stations
        // ------------------------------------------------------------------
        //
        // The ring is turned and widened until every slot is clear. If none
        // is fully clear the one with the fewest blocked slots wins, so there
        // is always a ring - a choice half in a wall is still better than no
        // choice, and the put-away key can move it.
        private static readonly float[] Radii = { Ring, 3.0f, 3.8f, 1.7f };

        private static void Relayout(Set s)
        {
            float bestAngle = s.angle;
            float bestRadius = s.radius;
            int bestBlocked = int.MaxValue;

            Level level;
            ServiceLocator.TryGet<Level>(out level);

            foreach (float r in Radii)
            {
                for (int step = 0; step < 12; step++)
                {
                    float a = step * 30f;
                    int blocked = 0;

                    for (int i = 0; i < s.count; i++)
                    {
                        if (Blocked(level, s.home + Offset(a, r, i, s.count)))
                            blocked++;
                    }

                    if (blocked < bestBlocked)
                    {
                        bestBlocked = blocked;
                        bestAngle = a;
                        bestRadius = r;
                    }

                    if (blocked == 0)
                    {
                        s.angle = a;
                        s.radius = r;
                        return;
                    }
                }
            }

            s.angle = bestAngle;
            s.radius = bestRadius;
        }

        // Would a choice here sit in a wall or on a station?
        private static bool Blocked(Level level, Vector3 at)
        {
            try
            {
                Vector2 p = at;

                if (level != null)
                {
                    // Terrain from the CELL GRID, the way wall riding reads it:
                    // a cell is wall when its collider type is a real one.
                    const float clear = 0.7f;

                    int minX = Mathf.FloorToInt(p.x - clear);
                    int maxX = Mathf.FloorToInt(p.x + clear);
                    int minY = Mathf.FloorToInt(p.y - clear);
                    int maxY = Mathf.FloorToInt(p.y + clear);

                    for (int x = minX; x <= maxX; x++)
                    {
                        for (int y = minY; y <= maxY; y++)
                        {
                            if (!level.ContainsCell(x, y) || level.IsEmpty(x, y))
                                continue;

                            CellType type = level.GetCellType(x, y);

                            if (type == null ||
                                type.colliderType != CellType.ColliderType.NonTrigger)
                            {
                                continue;
                            }

                            // Distance from the point to that unit square.
                            float dx = Mathf.Max(0f, Mathf.Max(x - p.x, p.x - (x + 1)));
                            float dy = Mathf.Max(0f, Mathf.Max(y - p.y, p.y - (y + 1)));

                            if (dx * dx + dy * dy < clear * clear)
                                return true;
                        }
                    }
                }

                foreach (Collider2D c in Physics2D.OverlapCircleAll(p, 1.2f))
                {
                    if (c != null && c.GetComponentInParent<Station>() != null)
                        return true;
                }
            }
            catch (Exception)
            {
                // Unknown is clear - a ring is always put out.
            }

            return false;
        }

        private static bool _saidNoPrefab;

        // ------------------------------------------------------------------
        // Building one from the game's module pickup
        // ------------------------------------------------------------------

        private static ModulePickup _prefab;
        private static bool _prefabLooked;

        private static ModulePickup Prefab()
        {
            if (_prefab != null || _prefabLooked)
                return _prefab;

            _prefabLooked = true;

            // `FindObjectsOfTypeAll` sees PREFABS, which is the point - but also
            // live pickups in the level, which are not; a prefab is not in a
            // valid scene.
            foreach (ModulePickup p in Resources.FindObjectsOfTypeAll<ModulePickup>())
            {
                if (p != null && !p.gameObject.scene.IsValid())
                {
                    _prefab = p;
                    break;
                }
            }

            return _prefab;
        }

        private static GameObject FromPickupPrefab(
            ForgeRankCopies.Copy copy, string feature, string line, string forWhom)
        {
            ModulePickup prefab = Prefab();

            if (prefab == null)
                throw new InvalidOperationException("no ModulePickup prefab is loaded");

            // Cloned under an INACTIVE holder, so nothing on the copy wakes up
            // before it has been stripped - a SavableEntity waking is an entity
            // registered with the save.
            var holder = new GameObject("ForgeRankChoiceHolder");
            holder.SetActive(false);

            GameObject go = null;

            try
            {
                go = Instantiate(prefab.gameObject, holder.transform, false);
                go.name = "ForgeRankChoice " + feature;

                ModulePickup mp = go.GetComponent<ModulePickup>();

                var icon = Field<SpriteRenderer>(mp, "icon");
                var background = Field<SpriteRenderer>(mp, "background");
                var prompt = Field<Component>(mp, "interactionPromptText");
                var power = Field<GameObject>(mp, "powerLevelObject");
                var interactable = FieldUp<Interactable>(mp, "interactable");

                Strip(go, interactable);

                // The gun it upgrades: its icon, its colour, its card background.
                Module m = copy.module;

                if (icon != null && m != null)
                {
                    icon.sprite = m.Icon;
                    icon.color = m.Color;
                }

                if (background != null && m != null && m.ModuleType != null)
                {
                    background.sprite = m.ModuleType.background;
                    background.color = m.Color;
                }

                if (power != null)
                    power.SetActive(false);

                // The last line says WHICH gun and slot - two copies of one
                // gun look identical otherwise (R20 256).
                SetText(prompt,
                    "PICK UP\n<color=#ffc600>" + feature.ToUpperInvariant() +
                    "</color>\n<size=70%>" + line + "</size>" +
                    (string.IsNullOrEmpty(forWhom)
                        ? ""
                        : "\n<size=60%><color=#9fd8ff>" + forWhom + "</color></size>"));

                if (interactable == null)
                    throw new InvalidOperationException("the pickup has no Interactable");

                // The prefab's own OnInteracted points at the pickup behaviour
                // this just removed. Switched off one at a time - a persistent
                // listener survives RemoveAllListeners.
                UnityEventBase evt = interactable.OnInteracted;

                for (int i = 0; i < evt.GetPersistentEventCount(); i++)
                    evt.SetPersistentListenerState(i, UnityEventCallState.Off);

                GameObject captured = go;

                interactable.OnInteracted.AddListener(who =>
                {
                    var c = captured != null
                        ? captured.GetComponent<ForgeRankChoice>()
                        : null;

                    if (c != null)
                        c.Take();
                });

                go.transform.SetParent(null, true);

                return go;
            }
            catch (Exception)
            {
                if (go != null)
                    DestroyImmediate(go);

                throw;
            }
            finally
            {
                Destroy(holder);
            }
        }

        // Keep: the Interactable, the prompt's UI and text, lights. Everything
        // else on the borrowed prefab is a real pickup's behaviour and goes.
        private static void Strip(GameObject go, Interactable keep)
        {
            // IN DEPENDENCY ORDER, over several passes. Unity refuses to remove
            // a component another one on the same object [RequireComponent]s -
            // it logs an error and leaves it - so removing the save entity
            // before the pickup that requires it would keep exactly the part
            // that must go. Each pass removes what nothing still needs.
            for (int pass = 0; pass < 4; pass++)
            {
                int removed = 0;

                foreach (MonoBehaviour mb in go.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    if (mb == null || ReferenceEquals(mb, keep))
                        continue;

                    string ns = mb.GetType().Namespace ?? "";

                    if (ns.StartsWith("UnityEngine") || ns.StartsWith("TMPro"))
                        continue;

                    // The prompt's own animator driver and key hint are UI, and
                    // a prompt that never animates in is the whole point lost.
                    if (InsidePrompt(mb.transform, go.transform))
                        continue;

                    if (RequiredByAnother(mb))
                        continue;

                    DestroyImmediate(mb);
                    removed++;
                }

                if (removed == 0)
                    break;
            }

            // The root's physical collider would shove the ship; the rigidbody
            // stays, KINEMATIC, so the Interactable's trigger moves cheaply.
            foreach (Collider2D col in go.GetComponents<Collider2D>())
                DestroyImmediate(col);

            var body = go.GetComponent<Rigidbody2D>();

            if (body != null)
            {
                body.bodyType = RigidbodyType2D.Kinematic;
                body.linearVelocity = Vector2.zero;
            }

            // An animator whose clips fire events at the scripts just removed
            // would log an error on every event. The prompt keeps its own.
            var anim = go.GetComponent<Animator>();

            if (anim != null)
                anim.enabled = false;
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

        private static bool InsidePrompt(Transform t, Transform root)
        {
            for (Transform x = t; x != null && x != root; x = x.parent)
            {
                if (x.name.IndexOf("Prompt", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }

            return false;
        }

        private static T Field<T>(object owner, string name) where T : class
        {
            if (owner == null)
                return null;

            FieldInfo f = AccessTools.Field(owner.GetType(), name);

            return (f != null) ? f.GetValue(owner) as T : null;
        }

        // A field declared on a base class (`InteractiblePickup<T>.interactable`).
        private static T FieldUp<T>(object owner, string name) where T : class
        {
            for (Type t = owner != null ? owner.GetType() : null; t != null; t = t.BaseType)
            {
                FieldInfo f = t.GetField(name,
                    BindingFlags.Instance | BindingFlags.NonPublic |
                    BindingFlags.Public | BindingFlags.DeclaredOnly);

                if (f != null)
                    return f.GetValue(owner) as T;
            }

            return null;
        }

        // TMP is reached by reflection, like everywhere else in this mod.
        private static void SetText(Component text, string value)
        {
            if (text == null)
                return;

            PropertyInfo p = text.GetType().GetProperty("text");

            if (p != null && p.CanWrite)
                p.SetValue(text, value, null);
        }

        // ------------------------------------------------------------------
        // The fallback orb
        // ------------------------------------------------------------------
        private static GameObject Orb(Unit ship, int index, int count)
        {
            var go = new GameObject("ForgeRankChoice");
            var sr = go.AddComponent<SpriteRenderer>();

            ForgeSpriteLibrary.Art art;

            if (ForgeSpriteLibrary.TryGetAnyArt("part_cyrcle_0", out art) &&
                art != null && art.sprite != null)
            {
                sr.sprite = art.sprite;
            }

            sr.color = Color.HSVToRGB(
                (count > 0) ? (index / (float)count) : 0f, 0.65f, 1f);

            // A fresh SpriteRenderer lands on sorting layer "Default", which in
            // this game draws BEHIND THE TERRAIN (R19 252's invisible orbs).
            // Borrow the ship's layer, one in front of its art.
            SpriteRenderer donor = ship.GetComponentInChildren<SpriteRenderer>(true);

            if (donor != null)
            {
                sr.sortingLayerID = donor.sortingLayerID;
                sr.sortingOrder = donor.sortingOrder + 1;
            }
            else
            {
                sr.sortingOrder = 40;
            }

            return go;
        }

        // ------------------------------------------------------------------
        // Taking one
        // ------------------------------------------------------------------

        // The use key, as a FALLBACK to the game's own interact route: if the
        // borrowed Interactable is not seen by the ship's Interactor (a layer
        // the physics matrix does not pair, a prefab that changed), the key
        // still takes the nearest choice in reach. Only when the Interactor has
        // nothing else in range - a station or a real pickup next to you keeps
        // the key for itself.
        private static readonly HashSet<int> _listening = new HashSet<int>();

        private static void Listen(Unit ship)
        {
            try
            {
                var interactor = ship.GetComponentInChildren<Interactor>(true);

                if (interactor == null || interactor.ShipInput == null ||
                    interactor.ShipInput.ShipControlActionMap == null)
                {
                    return;
                }

                int id = interactor.GetInstanceID();

                if (!_listening.Add(id))
                    return;

                interactor.ShipInput.ShipControlActionMap.UseActivated += s => OnUse(interactor);
            }
            catch (Exception)
            {
            }
        }

        private static void OnUse(Interactor interactor)
        {
            try
            {
                if (interactor == null || _live.Count == 0)
                    return;

                foreach (Interactable it in interactor.InteractablesInRange)
                {
                    if (it == null)
                        continue;

                    // Something that is not ours is in range: the game's own
                    // route decides, and if a choice is what it picked, its
                    // listener has already taken it.
                    if (it.GetComponentInParent<ForgeRankChoice>() == null)
                        return;
                }

                ForgeRankChoice best = null;
                float bestD = Reach * Reach;
                Vector2 at = interactor.transform.position;

                for (int i = 0; i < _live.Count; i++)
                {
                    ForgeRankChoice c = _live[i];

                    // A put-away choice is not there to take.
                    if (c == null || c._taken || !c.gameObject.activeInHierarchy)
                        continue;

                    float d = ((Vector2)c.transform.position - at).sqrMagnitude;

                    if (d <= bestD)
                    {
                        bestD = d;
                        best = c;
                    }
                }

                if (best != null)
                    best.Take();
            }
            catch (Exception)
            {
            }
        }

        private void Take()
        {
            if (_taken)
                return;

            _taken = true;

            try
            {
                ForgeRankDraw.Claimed(_copy, _feature);
            }
            catch (Exception)
            {
                // Claiming already destroys the set; never leave one live.
                Destroy(gameObject);
            }
        }

        // ------------------------------------------------------------------
        // Following
        // ------------------------------------------------------------------
        private void Update()
        {
            if (_ship == null || _set == null)
            {
                Destroy(gameObject);
                return;
            }

            Tick(_set);

            transform.position = Vector3.MoveTowards(
                transform.position, _set.home + Offset(_set, _index),
                CatchUp * Time.deltaTime);
        }

        // The set moves ONCE a frame, whichever member asks first.
        private static void Tick(Set s)
        {
            if (s.tickedFrame == Time.frameCount || s.ship == null)
                return;

            s.tickedFrame = Time.frameCount;

            Vector3 ship = s.ship.transform.position;
            float d2 = ((Vector2)(ship - s.home)).sqrMagnitude;

            if (d2 > Snap * Snap)
            {
                // A teleport or a load: jump, do not fly across the map.
                s.home = ship;
                Relayout(s);

                foreach (ForgeRankChoice c in s.members)
                {
                    if (c != null)
                        c.transform.position = s.home + Offset(s, c._index);
                }

                return;
            }

            // INSIDE THE LEASH THEY HOLD STILL, so flying up to one is a real
            // choice - a set that re-centres on the ship every frame is always
            // the same distance away and can never be reached. OUTSIDE it the
            // set catches up, so a reward cannot be stranded.
            if (d2 > Leash * Leash)
                s.home = Vector3.MoveTowards(s.home, ship, CatchUp * Time.deltaTime);

            // Somewhere new may put a slot in a wall - look again now and then,
            // and only re-lay the ring when a slot really is blocked.
            if (Time.time >= s.nextCheck)
            {
                s.nextCheck = Time.time + 0.3f;

                Level level;
                ServiceLocator.TryGet<Level>(out level);

                for (int i = 0; i < s.count; i++)
                {
                    if (Blocked(level, s.home + Offset(s, i)))
                    {
                        Relayout(s);
                        break;
                    }
                }
            }
        }

        private static Vector3 Offset(Set s, int index)
        {
            return Offset(s.angle, s.radius, index, s.count);
        }

        private static Vector3 Offset(float turn, float radius, int index, int count)
        {
            float step = 360f / Mathf.Max(1, count);
            float angle = (index * step + 90f + turn) * Mathf.Deg2Rad;

            return new Vector3(Mathf.Cos(angle), Mathf.Sin(angle), 0f) * radius;
        }

        private void OnDestroy()
        {
            _live.Remove(this);
        }
    }
}
