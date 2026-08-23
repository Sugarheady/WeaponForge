using System.Collections.Generic;
using UnityEngine;

namespace WeaponForge
{
    // A shot that HOPS from enemy to enemy - hit one, turn, fly at the next,
    // keeping a list so it never comes back to something it already hit.
    //
    // Not to be confused with the game's own DISCHARGE (electricityData /
    // dischargeOnFire), which is the other chain in this mod. The difference
    // is worth stating because they look similar and behave nothing alike:
    //
    //   discharge  spawns a LIGHTNING ARC from the hit point. The projectile
    //              is unaffected; the arc is instant, has its own damage, and
    //              jumps by conductivity. Nothing travels.
    //   chain      redirects the ACTUAL PROJECTILE. It flies, visibly, from
    //              one enemy to the next, carrying its own damage, and can be
    //              dodged, blocked or run out of range between hops.
    //
    // Requires piercing: the game keeps a shot alive through a hit only when
    // PiercingData.enabled is on (see Projectile.OnObjectHit), so the builder
    // turns it on rather than leaving a chain weapon that dies on its first
    // target and looks broken.
    public class ForgeChain : MonoBehaviour
    {
        // How many times it may redirect. 0 would be a plain piercing shot.
        public int jumps = 3;

        // How far to look for the next link.
        public float range = 8f;

        // Per jump, multiplying. 0.8 with three jumps ends at about half
        // damage - a chain that fades, which is the usual balance for one.
        public float damageMultiplier = 1f;

        // Per jump, multiplying. Under 1 makes a chain visibly run out of
        // steam; over 1 makes it frantic.
        public float speedMultiplier = 1f;

        // Allow hopping back onto something already hit - the PING-PONG
        // option. Off by default, because "no revisit" is what stops a chain
        // bouncing between two enemies forever, which is the failure mode
        // this feature is famous for.
        //
        // Even when it is on, the enemy just hit is excluded from the NEXT
        // search only. Without that the shot picks the target it is currently
        // sitting inside and jumps nowhere, which is why turning revisit on
        // did not actually produce a ping-pong.
        public bool revisit;

        // Seconds to keep going after a hit before turning toward the next
        // target. 0 = turn immediately, as before. A short pause reads as the
        // blade carrying through and then whipping round, and it is what makes
        // a ping-pong chain legible instead of a blur.
        public float jumpDelay;

        // Hang in the air during that pause instead of flying on. Turns the
        // delay into a beat - the blade stops dead, then snaps to the next
        // throat.
        public bool hoverDuringDelay;

        // Nothing left in range: keep flying straight, or give up and run the
        // weapon's impact behaviour where it stands.
        public bool vanishWhenNoTarget;

        [System.NonSerialized] public int used;

        // A jump owed but not yet taken, because jumpDelay has not elapsed.
        [System.NonSerialized] public bool pending;
        [System.NonSerialized] public float pendingAt;
        [System.NonSerialized] public float hoverSpeed;

        // Visited targets, by AimAssistTarget transform. Per shot, because
        // the component is copied onto each instantiated projectile.
        //
        // In revisit mode this is reused as a one-element "not the thing I am
        // standing in" list rather than a full history, so there is no second
        // collection and no per-jump allocation.
        [System.NonSerialized]
        public readonly HashSet<Transform> visited = new HashSet<Transform>();

        public bool CanJump
        {
            get { return used < jumps; }
        }
    }
}
