using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace WeaponForge
{
    // Rides a charmed unit and puts everything back when the charm ends.
    //
    // A LIVE object, not a prefab, so holding a `Config` reference here is safe
    // - the null-on-clone trap only bites components sitting on a prefab.
    // ForgeShieldController is the same shape.
    public class ForgeCharmed : MonoBehaviour
    {
        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("WeaponForge.Charm");

        public Unit Owner { get; private set; }
        public ForgeCharm.Config Cfg { get; private set; }

        private Unit _self;
        private float _expires;
        private bool _released;

        // WHAT HAS TO GO BACK. Saved once, on the frame the charm starts, and
        // never re-saved - a second save would record the CHARMED values and
        // release would then leave the enemy permanently on your side.
        private Faction _wasFaction;
        private readonly List<Vision> _eyes = new List<Vision>();
        private readonly List<int> _wasLayers = new List<int>();

        // Grudges this charm WITHDREW, so release can put them back exactly.
        //
        // Only ids that were genuinely present go in here - re-adding an id the
        // unit never held would invent a grudge, and the list is save-persisted
        // through AIAgent.Data.Memento, so an invented one would outlive the
        // run.
        private readonly List<int> _tookGrudge = new List<int>();

        // Grudges this charm ADDED for `infighting`, on the same principle in
        // reverse: only remove on release what was not already there.
        private readonly List<int> _gaveGrudge = new List<int>();

        private AIAgent _agent;
        private int _myId;
        private bool _hasMyId;

        private static FieldInfo _targetLayers;
        private static bool _looked;

        private static FieldInfo TargetLayers
        {
            get
            {
                if (!_looked)
                {
                    _looked = true;
                    _targetLayers = AccessTools.Field(typeof(Vision), "targetLayers");
                }

                return _targetLayers;
            }
        }

        public void Begin(Unit self, Unit owner, ForgeCharm.Config cfg)
        {
            _self = self;
            Owner = owner;
            Cfg = cfg;
            _expires = Time.time + Mathf.Max(0.1f, cfg.seconds);

            try
            {
                // ---- side ------------------------------------------------
                _wasFaction = ForgeMinion.GetFaction(self);

                Faction mine = ForgeMinion.GetFaction(owner);

                if (mine != null)
                    ForgeMinion.SetFaction(self, mine);

                // ---- eyes ------------------------------------------------
                //
                // A unit's Vision is wired to ONE layer mask and nothing in the
                // game reconciles it with faction, so a flipped enemy that
                // still only watches the Player layer sees you as a friend and
                // simply drifts. Widening to Player+Entities lets the faction
                // decide, which is what the AI was already written to do.
                SaveAndWidenEyes();

                // ---- its grudge, and who it is currently after ------------
                //
                // THE ONE THAT MADE CHARM LOOK BROKEN. Hitting a unit puts you
                // on its personal enemy list, and that list is read BEFORE
                // faction - so without this it keeps shooting at you for the
                // whole charm however friendly its faction now says you are.
                // See the long note in ForgeCharm.
                _agent = self.GetComponentInChildren<AIAgent>();

                DropGrudge();
                Reaim();
                JoinInfighting();

                // ---- its headbutt ----------------------------------------
                //
                // ChargerHead does its damage with NO faction test at all, so a
                // charmed rammer would happily plough through your own minions.
                // This supplies the missing test.
                ForgeMinionRamGuard.Mark(self);

                if (cfg.hasColor)
                {
                    ForgeUnitTint.Set(
                        self, "charm", cfg.color,
                        Mathf.Max(0.1f, cfg.seconds), cfg.colorPulse,
                        cfg.tintStrength);
                }
            }
            catch (Exception e)
            {
                Log.LogError("Starting a charm failed, releasing: " + e);
                Release("it failed to start");
            }
        }

        public void Refresh(float seconds)
        {
            _expires = Mathf.Max(_expires, Time.time + Mathf.Max(0.1f, seconds));

            if (Cfg != null && Cfg.hasColor && _self != null)
            {
                ForgeUnitTint.Set(
                    _self, "charm", Cfg.color,
                    Mathf.Max(0.1f, seconds), Cfg.colorPulse,
                    Cfg.tintStrength);
            }
        }

        private void SaveAndWidenEyes()
        {
            FieldInfo f = TargetLayers;

            if (f == null)
                return;

            int player = LayerMask.NameToLayer("Player");
            int entities = LayerMask.NameToLayer("Entities");

            // A renamed layer would give a mask of 1 (Default), which is worse
            // than leaving the eyes alone.
            if (player < 0 || entities < 0)
                return;

            int want = (1 << player) | (1 << entities);

            foreach (Vision v in _self.GetComponentsInChildren<Vision>(true))
            {
                if (v == null)
                    continue;

                object cur = f.GetValue(v);
                int was = (cur is LayerMask) ? ((LayerMask)cur).value
                                             : Convert.ToInt32(cur);

                _eyes.Add(v);
                _wasLayers.Add(was);

                // `targetLayers` is a LayerMask, NOT an int. Assigning the
                // wrong type here is the mistake that once broke phasing, so
                // the value is boxed as a LayerMask on the way in.
                LayerMask mask = want;
                f.SetValue(v, mask);
            }
        }

        // The entity instance id the game's own lists are keyed by. Public so
        // the infighting pass can read one charmed unit's id off another.
        public static bool TryId(Unit unit, out int id)
        {
            id = 0;

            if (unit == null)
                return false;

            // Both hops can be null on a unit that is not bound to save data
            // yet, and this runs on the frame a hit lands.
            var data = unit.ComponentData;

            if (data == null || data.entity == null)
                return false;

            id = data.entity.instanceId;
            return true;
        }

        public bool TryOwnId(out int id)
        {
            if (_hasMyId)
            {
                id = _myId;
                return true;
            }

            if (TryId(_self, out id))
            {
                _myId = id;
                _hasMyId = true;
                return true;
            }

            return false;
        }

        private AIAgent.Data AgentData
        {
            get
            {
                return (_agent != null) ? _agent.ComponentData : null;
            }
        }

        // Take the owner (and anything else on your side it has a grudge
        // against) off this unit's personal enemy list.
        //
        // Called from Begin AND from Update, because a later hit re-adds it -
        // `AggroWhenHit` fires on every damaging hit, so with `playerCanHit`
        // on, shooting a charmed unit would put the grudge straight back.
        private void DropGrudge()
        {
            AIAgent.Data data = AgentData;

            if (data == null || data.enemyBlackList == null)
                return;

            int ownerId;

            if (!TryId(Owner, out ownerId))
                return;

            if (data.enemyBlackList.Remove(ownerId))
            {
                // Remember it ONCE. A second Update pass must not append a
                // duplicate, or release would be no more correct for it.
                if (!_tookGrudge.Contains(ownerId))
                    _tookGrudge.Add(ownerId);
            }
        }

        // Forget whatever it is currently chasing so it picks again this frame.
        //
        // Without this the agent keeps its target until the target leaves its
        // sight: `AIAgent.Update` returns early while `HasTarget &&
        // IsTargetVisible`, and only then calls SelectTargetFromVisibleEnemies.
        // Dropping the grudge alone would therefore take effect whenever you
        // next broke line of sight rather than immediately.
        private void Reaim()
        {
            if (_agent != null)
                _agent.ForgetTarget();
        }

        // Chaos mode: make every charmed unit an enemy of every other one.
        //
        // Factions cannot express this - they all share the owner's, so they
        // are all friends - but the blacklist outranks faction, which is
        // exactly the property that caused the bug above. Both directions are
        // written, so a unit charmed later is hostile to one charmed earlier
        // and vice versa.
        private void JoinInfighting()
        {
            if (Cfg == null || !Cfg.infighting)
                return;

            int mine;

            if (!TryOwnId(out mine))
                return;

            foreach (ForgeCharmed other in ForgeCharm.Live)
            {
                if (other == null || ReferenceEquals(other, this))
                    continue;

                int theirs;

                if (other.TryOwnId(out theirs))
                    Hate(theirs);

                other.Hate(mine);
            }
        }

        // Undo one Hate. Only removes what THIS charm added, for the same
        // reason Hate only records what it added.
        public void Forgive(int id)
        {
            if (!_gaveGrudge.Remove(id))
                return;

            AIAgent.Data data = AgentData;

            if (data != null && data.enemyBlackList != null)
                data.enemyBlackList.Remove(id);
        }

        public void Hate(int id)
        {
            AIAgent.Data data = AgentData;

            if (data == null || data.enemyBlackList == null)
                return;

            // Only record what WE added. An id the unit already hated must not
            // be withdrawn on release - that grudge is not ours to cancel.
            if (data.enemyBlackList.Add(id) && !_gaveGrudge.Contains(id))
                _gaveGrudge.Add(id);
        }

        private void Update()
        {
            if (_released)
                return;

            // A charmed unit that dies must not leave a dangling entry.
            if (_self == null)
            {
                Release("it died");
                return;
            }

            // Re-check every frame rather than only at the start. Cheap (one
            // HashSet lookup) and it self-heals: any later hit from you puts
            // the grudge back through AggroWhenHit, and a target acquired in
            // between has to be dropped too or it chases you until it loses
            // sight of you.
            DropGrudge();
            DropFriendlyTarget();

            if (Time.time >= _expires)
                Release("it wore off");
        }

        // If it has settled on something on YOUR side, make it pick again.
        //
        // Deliberately not "if the target is the owner": a charmed unit
        // shooting at your minions is the same bug wearing a different hat.
        // With `infighting` on, another charmed unit IS a legitimate target, so
        // that case is excluded by asking the agent's own lists rather than by
        // asking about factions.
        private void DropFriendlyTarget()
        {
            if (_agent == null || !_agent.HasTarget)
                return;

            Unit target = _agent.Target;

            if (target == null)
                return;

            // The agent already sorted everything it can see into friends and
            // enemies using the game's own rules (grudge first, then faction).
            // If this target is in the FRIEND list, chasing it is the bug.
            if (_agent.SeesUnit(target) && !IsAgentEnemy(target))
                _agent.ForgetTarget();
        }

        private bool IsAgentEnemy(Unit target)
        {
            AIAgent.Data data = AgentData;

            if (data != null && data.enemyBlackList != null)
            {
                int id;

                if (TryId(target, out id) && data.enemyBlackList.Contains(id))
                    return true;
            }

            return _self != null && _self.IsEnemiesWith(target);
        }

        public void Release(string why)
        {
            if (_released)
                return;

            _released = true;
            ForgeCharm.Forget(this);

            try
            {
                if (_self != null)
                {
                    if (_wasFaction != null)
                        ForgeMinion.SetFaction(_self, _wasFaction);

                    FieldInfo f = TargetLayers;

                    if (f != null)
                    {
                        for (int i = 0; i < _eyes.Count; i++)
                        {
                            if (_eyes[i] == null)
                                continue;

                            LayerMask back = _wasLayers[i];
                            f.SetValue(_eyes[i], back);
                        }
                    }

                    // PUT THE GRUDGES BACK. This list is save-persisted through
                    // AIAgent.Data.Memento, so getting it wrong either way
                    // outlives the charm: leaving our own additions in makes
                    // released enemies permanently hate each other, and failing
                    // to restore what we took means an enemy you had already
                    // angered forgives you for the rest of the run.
                    AIAgent.Data data = AgentData;

                    if (data != null && data.enemyBlackList != null)
                    {
                        for (int i = 0; i < _tookGrudge.Count; i++)
                            data.enemyBlackList.Add(_tookGrudge[i]);

                        for (int i = 0; i < _gaveGrudge.Count; i++)
                            data.enemyBlackList.Remove(_gaveGrudge[i]);
                    }

                    // And drop whatever it had settled on, so it re-acquires
                    // as an enemy again instead of finishing its charm on an
                    // ally it can no longer legally fight.
                    Reaim();

                    // Take our id out of the OTHER charmed units' lists too, or
                    // an infighting swarm keeps hating a unit that has already
                    // gone back to normal.
                    int mine;

                    if (Cfg != null && Cfg.infighting && TryOwnId(out mine))
                    {
                        foreach (ForgeCharmed other in ForgeCharm.Live)
                        {
                            if (other != null && !ReferenceEquals(other, this))
                                other.Forgive(mine);
                        }
                    }

                    // Hand the tint back rather than letting it expire on its
                    // own, or a charm cut short by the cap leaves the enemy
                    // pink for the rest of its natural life.
                    ForgeUnitTint.Clear(_self, "charm");

                    // Stop supplying the ram's faction test, so the enemy goes
                    // back to the game's own behaviour exactly.
                    var widened = _self.GetComponent<ForgeMinionRamGuard.Widened>();

                    if (widened != null)
                        Destroy(widened);
                }
            }
            catch (Exception e)
            {
                Log.LogError("Releasing a charm failed: " + e);
            }

            Destroy(this);
        }

        private void OnDestroy()
        {
            // Covers the paths Update cannot see - a scene unload, or the unit
            // being destroyed outright. Release is idempotent.
            Release("its object went away");
        }
    }
}
