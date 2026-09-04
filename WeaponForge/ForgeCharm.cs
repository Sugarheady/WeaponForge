using System;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;

namespace WeaponForge
{
    // CHARM ON HIT - an enemy fights for you for a while, then goes back.
    //
    // I flagged this the riskiest of the seven behaviours, and the reason is
    // worth restating because it shaped the whole design: this codebase has
    // already shipped a bug where changing whose side a unit is on made
    // friendly Larvae eat each other, and another where a minion could not SEE
    // anything because its eyes are wired to one layer that does not follow its
    // faction. A charmed enemy walks into both.
    //
    // SO CHARM REUSES THE MINION MACHINERY RATHER THAN REDISCOVERING IT:
    //   * the faction write goes through `ForgeMinion.SetFaction`, the one
    //     owner of that reflection;
    //   * the eyes are retargeted the same way `ForgeMinionAllegiance.Eyes`
    //     does, because a unit's `Vision.targetLayers` is a fixed mask and
    //     nothing in the game reconciles it with faction;
    //   * it is marked for `ForgeMinionRamGuard`, so a charmed rammer does not
    //     headbutt your own minions - `ChargerHead` has NO faction test of its
    //     own.
    //
    // EVERYTHING IT CHANGES, IT CHANGES BACK. Charm is temporary, so the
    // original faction and the original vision masks are saved per unit and
    // restored on release, on death, and on the component being destroyed.
    //
    // AND ITS BULLETS COME WITH IT. This shipped an hour earlier as a "charm is
    // a pacifier" caveat - a charmed shooter could not reach anything, because
    // flipping a unit's faction does not move its bullets and an enemy shot is
    // on a layer that cannot collide with Entities. He asked whether that was
    // really immovable. It was not: `ForgeBulletTurn` has done exactly this
    // operation for `deflect` and `shield` since those shipped, and charm now
    // reuses it through `ForgeBulletTurn.Adopt` on `Shoot`.
    //
    // The lesson is not about layers. I had written the limitation up from a
    // KNOWN-FAILING attempt (the build-time minion ammo retarget) and stopped,
    // without checking whether a different mechanism in the same codebase
    // already solved it. **A feature failing one way is not evidence that it
    // cannot be done another.**
    //
    // ---------------------------------------------------------------------
    // AND FLIPPING THE FACTION IS NOT ENOUGH EITHER, BECAUSE THE GAME KEEPS A
    // PERSONAL GRUDGE THAT OUTRANKS IT. This is the R12 test 134 bug and it is
    // the important entry in this file.
    //
    // `AIAgent.Data.enemyBlackList` is a `HashSet<int>` of entity instance ids,
    // and `RefreshEnemyAndFriendLists` tests it **first**, before any faction
    // question:
    //
    //     if (enemyBlackList.Contains(id))   -> ENEMY
    //     else if (friendWhiteList...)       -> friend
    //     else if (IsFriendsWith(unit))      -> friend
    //     else if (IsEnemiesWith(unit))      -> enemy
    //
    // And `AIAgent.HandleHitBy(other)` - which `AggroWhenHit` calls from the
    // victim's own `onGotAttacked` - adds the attacker to that list and never
    // removes it. So THE ACT OF CHARMING SOMETHING BLACKLISTS YOU ON IT. Its
    // faction says friend, its grudge says enemy, and the grudge is read first,
    // so it keeps hunting you for the whole charm.
    //
    // His report contained the proof and I would not have thought of it:
    // *"So it works while invisible."* Under `stealthPERMA` the player never
    // enters `Vision.VisibleUnits` at all, so the blacklist entry is never
    // matched, the agent falls through to `SelectTargetFromVisibleEnemies` and
    // picks a real enemy. **The one configuration where charm worked was the
    // one where the grudge could not be consulted.**
    //
    // So `ForgeCharmed` withdraws the grudge (and restores it on release, since
    // it is save-persisted through a Memento), and clears the current target so
    // the agent re-acquires immediately instead of waiting for you to leave its
    // sight. `Update` re-checks, because a later hit can re-add it.
    //
    // The generalisable part: **a faction change is not a targeting change.**
    // Three separate mechanisms decide who a unit fights in this game - its
    // faction, its Vision layer mask, and its personal blacklist - and nothing
    // reconciles them. This mod had already been bitten by the second one
    // (see the eyes note above). The blacklist is the third.
    public static class ForgeCharm
    {
        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("WeaponForge.Charm");

        public class Config
        {
            public float chance = 0.25f;     // per damaging hit
            public float seconds = 6f;

            // How many at once. 0 IS UNLIMITED - the guard in Apply is
            // `if (cfg.max > 0)`, so it always has been; R12 asked for the
            // option because nothing said so out loud. Above 0, charming one
            // more releases the OLDEST.
            public int max = 2;
            public float maxTargetHealth;    // 0 = no limit (the boss guard)

            // Let YOUR shots still damage a charmed unit.
            //
            // Off by default because a charmed unit is on your faction, and
            // `Projectile.FixedUpdate` skips a hit outright when
            // `Owner.IsFriendsWith(victim)` - so by default your bullets pass
            // straight through one, which is what he ran into. Turning this on
            // makes just that one check answer false for charmed units of
            // yours, and nothing else in the game changes.
            public bool playerCanHit;

            // Charmed units fight EACH OTHER as well as everything else.
            //
            // Chaos mode, off by default. It cannot be done with factions -
            // they all share yours, so they are all friends - and it does not
            // need to be: the game already has a per-unit grudge list that
            // OVERRIDES faction, which is the same mechanism that caused the
            // R12 bug (see ForgeCharmed). So this writes each charmed unit into
            // the others' lists and takes them back out on release.
            public bool infighting;

            // Cancel any damage a charmed unit would do to your side.
            //
            // Kept as a belt alongside the bullet adoption, not instead of it:
            // adoption fixes what its gun can REACH, this covers anything that
            // gets through anyway - a contact hit, an explosion (which has no
            // owner filter anywhere in this game), or a shot fired in the
            // instant before the charm landed.
            public bool harmless = true;

            public bool hasColor = true;
            public Color color = new Color(1f, 0.5f, 0.95f, 1f);
            public float colorPulse = 3f;

            // HOW HARD THE TINT COMES THROUGH, 0..1. 1 is the full colour and
            // is what this has always done; 0.35 is a hint of it. Read from
            // either "tintStrength" or "colorStrength" so the two naming
            // styles in this mod's blocks both work. See ForgeUnitTint.
            public float tintStrength = 1f;

            public string sfx = "";
            [NonSerialized] public string sfxGuid = "";
        }

        private static readonly Dictionary<WeaponData, Config> _weapons =
            new Dictionary<WeaponData, Config>();

        public static bool Any { get; private set; }

        public static void Register(WeaponData weapon, Config config)
        {
            if (weapon == null || config == null)
                return;

            _weapons[weapon] = config;
            Any = true;
        }

        public static bool TryGet(WeaponData weapon, out Config config)
        {
            config = null;

            return weapon != null && _weapons.TryGetValue(weapon, out config) &&
                   config != null;
        }

        // Everything currently charmed, oldest first, so the cap can release in
        // the order a player would expect.
        private static readonly List<ForgeCharmed> _live =
            new List<ForgeCharmed>();

        // Read-only view for the infighting pass, which needs to reach every
        // other charmed unit's grudge list.
        public static List<ForgeCharmed> Live { get { return _live; } }

        // ------------------------------------------------------------------
        // "playerCanHit": is `victim` a charmed unit of `shooter`'s that its
        // own side is allowed to shoot?
        //
        // Asked from a postfix on `Unit.IsFriendsWith`, and THE BLAST RADIUS IS
        // WORTH STATING because I nearly picked two narrower designs that were
        // both worse.
        //
        // The game calls that method in exactly two places (checked, not
        // assumed - it is a two-line method with two callers):
        //
        //   Projectile.FixedUpdate   Owner.IsFriendsWith(hit unit) - skips the
        //                            hit outright. This is the one he wanted.
        //   AIAgent                  thisUnit.IsFriendsWith(seen unit)
        //
        // The AI one is unreachable on the player: it asks about the agent's
        // OWN unit, and the ship has no AIAgent (verified on the prefab -
        // ShipKBM has no `seeker` field where Enemy_Larva has one).
        //
        // THIS MOD calls it in eleven places, all asking "is this on my side,
        // so leave it alone" - homing and chain target-finding, the curve beam,
        // the tractor, sticky, turrets, the minion blast filter, execute. So
        // this one switch also makes a charmed unit a valid target for those.
        // That is deliberate and it is why the switch is off by default: "my
        // weapons no longer treat it as a friend" is one coherent rule, whereas
        // "my bullets hit it but my homing refuses to aim at it" is two.
        //
        // WHY NOT SOMETHING NARROWER. Scoping the change to
        // `Projectile.FixedUpdate` with a flag looked exact and is not: homing
        // runs inside that same FixedUpdate, so the flag would be live for
        // `FindTarget` anyway - and the damage call nests inside it too, so it
        // would also reach the protection guard below and let a charmed unit
        // hurt you. Giving charmed units a cloned faction has a worse failure:
        // it is asymmetric the right way for your own fire, but then no ENEMY
        // faction lists the clone under its `enemies` either, so every enemy in
        // the room would calmly ignore the charmed unit instead of fighting it.
        public static bool PiercesFriendly(Unit shooter, Unit victim)
        {
            if (shooter == null || victim == null)
                return false;

            ForgeCharmed c = victim.GetComponent<ForgeCharmed>();

            if (c == null || c.Cfg == null)
                return false;

            // The original case: YOUR fire reaching a unit you charmed.
            if (c.Cfg.playerCanHit && c.Owner == shooter)
                return true;

            // ONE CHARMED UNIT'S FIRE REACHING ANOTHER.
            //
            // R13 test 140: *"they try to shoot each other but unable to hurt
            // each other, the projectiles they shoot go through them."* The
            // grudge half of `infighting` worked - they picked each other as
            // targets - and every shot then passed straight through, because
            // the two halves of "can I hit this" are separate systems and only
            // one of them had been taught about infighting.
            //
            // The shooter here is a CHARMED UNIT, not the charmer, so the test
            // above could never fire: `c.Owner` is the player and `shooter` is
            // its fellow minion. That is the whole bug. `playerCanHit` is a
            // rule about the player's guns and is deliberately not consulted.
            return AreInfightingPeers(shooter, victim);
        }

        // Are these two units charmed by the SAME side, with infighting on?
        //
        // Asked from two places that are easy to mix up, so it lives here once:
        // the friendly-fire skip in `Projectile.FixedUpdate` (can the bullet
        // land) and the `harmless` guard on the damage chokepoint (may the
        // damage count). BOTH refused this, which is why fixing one alone would
        // have produced bullets that visibly connect and do nothing - a worse
        // bug report than the original.
        //
        // Deliberately symmetric and deliberately narrow: it is true only when
        // both are charmed, both belong to the same owner, and that owner's
        // charm asked for infighting. A charmed unit still cannot touch you,
        // your ship, or a minion you summoned yourself.
        public static bool AreInfightingPeers(Unit a, Unit b)
        {
            if (a == null || b == null || ReferenceEquals(a, b))
                return false;

            ForgeCharmed ca = a.GetComponent<ForgeCharmed>();

            if (ca == null || ca.Cfg == null || !ca.Cfg.infighting)
                return false;

            ForgeCharmed cb = b.GetComponent<ForgeCharmed>();

            if (cb == null || cb.Cfg == null)
                return false;

            return ca.Owner != null && ca.Owner == cb.Owner;
        }

        // Set to true at build time by any weapon using `playerCanHit`, so the
        // postfix on a method the game calls per bullet per frame costs one
        // static bool read for everybody else.
        public static bool AnyPlayerCanHit;

        // The same trick for `infighting`, and it needed its own flag rather
        // than reusing the one above: the two switches are independent, and
        // gating the friendly-fire postfix on `AnyPlayerCanHit` alone is what
        // would have left infighting still passing through on a file that did
        // not also ask for `playerCanHit`. His R13 file happened to set both,
        // which would have hidden it.
        public static bool AnyInfighting;

        // The one gate the per-bullet postfix reads.
        public static bool RewritesFriendship
        {
            get { return AnyPlayerCanHit || AnyInfighting; }
        }

        // Ask the game's ally question WITHOUT our own answer folded in.
        //
        // `harmless` has to keep working when `playerCanHit` is on: those are
        // opposite directions (your fire reaching a charmed unit vs a charmed
        // unit's fire reaching you), and both guards happen to go through
        // `IsFriendsWith`. Without this the protection would consult the
        // rewritten answer and a charmed unit could shoot another one of yours.
        public static bool RawFriends(Unit a, Unit b)
        {
            if (a == null || b == null)
                return false;

            bool was = _rawAsk;
            _rawAsk = true;

            try
            {
                return a.IsFriendsWith(b);
            }
            finally
            {
                _rawAsk = was;
            }
        }

        public static bool AskingRaw { get { return _rawAsk; } }

        private static bool _rawAsk;

        public static bool IsCharmed(Unit unit)
        {
            return unit != null &&
                   unit.GetComponent<ForgeCharmed>() != null;
        }

        // Is this unit charmed AND working for `side`? Used by the protection
        // rule, which must not fire for a charmed unit belonging to somebody
        // else in a future co-op game.
        public static bool WorksFor(Unit unit, Unit side)
        {
            if (unit == null || side == null)
                return false;

            ForgeCharmed c = unit.GetComponent<ForgeCharmed>();

            return c != null && c.Owner == side;
        }

        public static void Apply(Unit victim, Unit owner, Config cfg)
        {
            if (victim == null || owner == null || cfg == null)
                return;

            try
            {
                ForgeCharmed already = victim.GetComponent<ForgeCharmed>();

                if (already != null)
                {
                    // REFRESHED, NOT STACKED. Two charms on one unit would each
                    // save the "original" faction - and the second would save
                    // the CHARMED one, so releasing would leave the enemy
                    // permanently yours. Same trap as the tint layers.
                    already.Refresh(cfg.seconds);
                    return;
                }

                Prune();

                if (cfg.max > 0)
                {
                    while (_live.Count >= cfg.max)
                    {
                        ForgeCharmed oldest = _live[0];
                        _live.RemoveAt(0);

                        if (oldest != null)
                            oldest.Release("the cap was reached");
                    }
                }

                var c = victim.gameObject.AddComponent<ForgeCharmed>();
                c.Begin(victim, owner, cfg);
                _live.Add(c);

                if (!string.IsNullOrEmpty(cfg.sfxGuid))
                    AudioManager.PlaySfx(cfg.sfxGuid, victim.transform.position);
            }
            catch (Exception e)
            {
                Log.LogError("Charming a unit failed: " + e);
            }
        }

        public static void Forget(ForgeCharmed c)
        {
            _live.Remove(c);
        }

        private static void Prune()
        {
            for (int i = _live.Count - 1; i >= 0; i--)
            {
                if (_live[i] == null)
                    _live.RemoveAt(i);
            }
        }

        public static void Reset()
        {
            // Units do not survive a run, so the list only needs emptying.
            _live.Clear();
        }

        public static void Describe(Config cfg, string fileName)
        {
            if (cfg == null)
                return;

            Log.LogInfo(
                fileName + ": charm - " + (cfg.chance * 100f).ToString("0.#") +
                "% of damaging hits turn an enemy to your side for " +
                cfg.seconds.ToString("0.##") + "s, " +
                (cfg.max > 0
                    ? cfg.max + " at a time (charming one more releases the " +
                      "oldest)"
                    : "NO LIMIT on how many at once") +
                "." +
                (cfg.maxTargetHealth > 0f
                    ? " Skips anything with more than " +
                      cfg.maxTargetHealth.ToString("0.##") + " maximum health."
                    : "") +
                " Its faction, its eyes and its grudge against you are all " +
                "put back when it wears off." +
                " Its bullets are moved onto your side as it fires, so it " +
                "can actually shoot what it is now fighting, and it drops " +
                "whatever it was chasing so it picks a new target at once." +
                (cfg.playerCanHit
                    ? " \"playerCanHit\" - your own fire can damage it."
                    : " Your own fire passes through it - set " +
                      "\"playerCanHit\": true if you want to be able to " +
                      "shoot one.") +
                (cfg.infighting
                    ? " \"infighting\" - charmed units also fight each other."
                    : "") +
                (cfg.harmless
                    ? " Anything that still gets through cannot touch you or " +
                      "your side."
                    : " \"harmless\": false - the safety net is OFF, so a " +
                      "charmed enemy can hurt you again."));
        }
    }
}
