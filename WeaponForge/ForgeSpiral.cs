using System;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;

namespace WeaponForge
{
    // ROTATING SPIRAL - successive shots step a fixed angle instead of a fixed
    // or a random one, so holding the trigger paints a spiral rather than a
    // stream or a cone.
    //
    // His approval, once I had described it properly: *"that makes more sense. I
    // approve this, add it to the list. We'll see how it looks."* The first
    // description failed because I pitched it as a variant of two existing
    // fields; it is a third thing, and the difference is worth stating once:
    //
    //     angleOffset      a FIXED nudge      every shot leaves 10 deg left
    //     angleVariance    RANDOM scatter     every shot lands somewhere in a cone
    //     spiral.step      ADVANCES           0 deg, then 20, then 40, round
    //
    // ---- THE SEAM, AND WHY THIS IS THE CHEAPEST FEATURE IN THE MOD ---------
    //
    // `WeaponBase.AngleOffset` is a public auto-property with a setter, and
    // `GetDirections` reads it to place the whole volley:
    //
    //     float firstAngle = (ProjectileCount == 1f)
    //         ? AngleOffset
    //         : (-Spread / 2f + AngleOffset);
    //     ...
    //     float z = firstAngle + i * step
    //             + Random.Range(-AngleVariance, AngleVariance);
    //     yield return Quaternion.Euler(0f, 0f, z) * direction;
    //
    // Three things fall out of that, all free:
    //
    //  * it is the CENTRE of the fan, so advancing it rotates a multi-pellet
    //    volley as a rigid body - a rosette, not a smear;
    //  * `spread` and `angleVariance` still mean exactly what they meant, so
    //    this composes with them instead of replacing them;
    //  * it lives on the live `WeaponBase`, **not** on the shared `WeaponData`
    //    asset (`Equip` copies `data.angleOffset` into it), so writing it cannot
    //    leak into another weapon or outlast the run.
    //
    // AND IT REACHES EVERY WEAPON TYPE, because `WeaponBase.DoShoot(IBarrel)` is
    // public, is where `GetDirections` is walked, and is **overridden nowhere**.
    // A spinning beam, a spiral of lobbed grenades and a spiral of thrown
    // minions all come from the same four lines. That is also why the builder
    // page block must NOT live in the projectile type-section.
    //
    // ---- THE WRITE IS SCOPED, NOT ACCUMULATED -----------------------------
    //
    // The obvious implementation is `AngleOffset += step` per shot. Do not: that
    // is the read-modify-write trap this project has paid for twice (see
    // ForgeUnitTint, and overheat's muzzle colour). `AngleOffset` is also one of
    // the properties a module augmentation can modify - it is in
    // `ModifyWeaponProperty`'s target enum - and a module recalculating
    // `FromCurrent` would fold our spin into the weapon's permanent base.
    //
    // So the spin is kept HERE, and the patch writes `base + spin` for the
    // duration of one `DoShoot` and puts the base back in a finalizer. Outside
    // that window the field reads exactly what the author and their modules say.
    public static class ForgeSpiral
    {
        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("WeaponForge.Spiral");

        public class Config
        {
            // Degrees advanced per SHOT EVENT - per `DoShoot`, which means per
            // pellet-volley rather than per pellet. Advancing per pellet would
            // just be `spread`, which already exists.
            //
            // A negative value spins the other way. There is deliberately no
            // separate "direction" key: the sign of the step is the direction,
            // and two ways of saying one thing is how a config grows a
            // contradiction.
            public float step = 20f;

            // 0 = keep rotating forever (the spiral).
            // >0 = PING-PONG across an arc this wide, centred on your aim - a
            // turret sweeping a firing line. The spin runs between -sweep/2 and
            // +sweep/2 and reverses at each end.
            public float sweep;

            // Start each trigger pull back on your aim, instead of continuing
            // from wherever the last one stopped.
            //
            // DEFAULT OFF, and the reason is the silent-no-op family: on a
            // slow single-shot weapon, resetting every pull would make the
            // whole feature look broken, because every shot would leave on the
            // aim and nothing would ever appear to rotate. Off, a tap-fired
            // weapon still visibly walks its shots round.
            public bool resetOnPull;
        }

        // ---- registration, keyed on the ASSET ----------------------------
        //
        // Same shape as execute and targeting: a weapon with no `spiral` block
        // can only resolve to null, so nothing about its firing path changes.
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

        // ---- per-INSTANCE spin -------------------------------------------
        //
        // Keyed on the live `WeaponBase`, not on the asset, because two slots
        // carrying the same Forge weapon are two guns and should not share one
        // rotation. A weapon is a fresh instance whenever its cluster is
        // rebuilt, so `Forget` is wired to `WeaponBase.Dispose` rather than
        // letting dead keys pile up.
        public class State
        {
            public float spin;
            public int dir = 1;
            public bool wasShooting;
        }

        private static readonly Dictionary<WeaponBase, State> _live =
            new Dictionary<WeaponBase, State>();

        public static State Get(WeaponBase weapon, Config cfg)
        {
            State s;

            if (_live.TryGetValue(weapon, out s) && s != null)
                return s;

            s = new State();
            s.dir = (cfg != null && cfg.step < 0f) ? -1 : 1;
            _live[weapon] = s;

            return s;
        }

        public static void Forget(WeaponBase weapon)
        {
            if (weapon != null)
                _live.Remove(weapon);
        }

        // ---- the whole mechanic ------------------------------------------

        // Advance AFTER a shot has used the current value, so the FIRST shot of
        // a fresh spin leaves on your aim. A weapon whose first shot came out
        // sideways would read as broken aim rather than as a spiral.
        public static void Advance(Config cfg, State s)
        {
            if (cfg == null || s == null)
                return;

            float size = Mathf.Abs(cfg.step);

            if (size <= 0f)
                return;

            if (cfg.sweep > 0f)
            {
                float half = cfg.sweep * 0.5f;

                s.spin += size * s.dir;

                // Clamp AND turn, so a step larger than the arc slams between
                // the two ends instead of running away past them.
                if (s.spin >= half)
                {
                    s.spin = half;
                    s.dir = -1;
                }
                else if (s.spin <= -half)
                {
                    s.spin = -half;
                    s.dir = 1;
                }

                return;
            }

            // Free rotation. Wrapped rather than left to grow: a weapon at 8
            // shots a second across a long run reaches six figures of degrees,
            // and a float's resolution up there is coarse enough to make the
            // step visibly uneven.
            s.spin = Mathf.Repeat(s.spin + size * s.dir, 360f);
        }

        // A real trigger-up, from the Shooter path. Gadgets never reach this -
        // they call `Fire` directly and have no trigger to release - which is
        // the same limitation `charge` documents, for the same reason.
        //
        // GATED IN HERE, not only at the call site. The patch already
        // early-returns for a weapon that did not ask for `reset`, and that
        // fast path stays, but a method handed a `Config` must not ignore the
        // one field that governs it: the next caller to reuse this for trigger
        // tracking would otherwise silently reset every spiral in the game.
        // A simulation caught exactly that.
        public static void Trigger(WeaponBase weapon, Config cfg, bool shooting)
        {
            if (weapon == null || cfg == null)
                return;

            State s = Get(weapon, cfg);

            if (cfg.resetOnPull && s.wasShooting && !shooting)
            {
                s.spin = 0f;
                s.dir = (cfg.step < 0f) ? -1 : 1;
            }

            s.wasShooting = shooting;
        }

        public static void Describe(
            Config cfg, string fileName, string weapon,
            float fireRate, bool isBeam)
        {
            if (cfg == null)
                return;

            string turn = (cfg.step < 0f) ? "anticlockwise" : "clockwise";
            float size = Mathf.Abs(cfg.step);

            if (cfg.sweep > 0f)
            {
                Log.LogInfo(
                    fileName + ": spiral - '" + weapon + "' SWEEPS back and " +
                    "forth across " + cfg.sweep.ToString("0.#") + " degrees " +
                    "centred on your aim, moving " + size.ToString("0.#") +
                    " degrees per shot and starting " + turn + ". About " +
                    Mathf.Max(1f, cfg.sweep / size).ToString("0") +
                    " shots per pass. This is the windscreen-wiper version " +
                    "rather than the spiral - remove \"sweep\" for a full " +
                    "rotation.");
            }
            else
            {
                Log.LogInfo(
                    fileName + ": spiral - '" + weapon + "' advances " +
                    size.ToString("0.#") + " degrees " + turn + " per shot, so " +
                    "a full turn takes " +
                    Mathf.Max(1f, 360f / size).ToString("0") + " shots. It " +
                    "rotates the WHOLE volley, so \"spread\" and " +
                    "\"projectileCount\" still shape each one and turn this " +
                    "into a rosette; \"angleVariance\" still scatters on top.");
            }

            // A BEAM IS THE ONE CASE WHERE PER-SHOT IS NOT THE USEFUL UNIT.
            //
            // A projectile weapon fires in bursts you can see; a held beam
            // fires continuously at its fireRate, so "24 degrees per shot" at
            // fireRate 10 is 240 degrees a SECOND - two thirds of a turn every
            // second, which is far faster than anyone reading "24" expects.
            // Same class of gap as the fade measured in seconds when what
            // mattered was units.
            if (isBeam && fireRate > 0f)
            {
                float perSecond = size * fireRate;

                Log.LogInfo(
                    fileName + ": spiral on a BEAM - a held beam re-fires at " +
                    "its fireRate rather than in taps, so " +
                    size.ToString("0.#") + " degrees per shot at fireRate " +
                    fireRate.ToString("0.##") + " is about " +
                    perSecond.ToString("0") + " DEGREES A SECOND" +
                    (perSecond > 180f
                        ? " - that is faster than half a turn a second and will " +
                          "read as a strobe rather than a sweep. Lower \"step\" " +
                          "(1 to 3 is a smooth beam) or lower fireRate."
                        : ".") +
                    " The drawn beam and the damage ray are placed from two " +
                    "different calls in the game and both are rotated, so what " +
                    "you see is what hits.");
            }

            Log.LogInfo(
                fileName + ": spiral - the first shot of a spin leaves on your " +
                "aim, and " +
                (cfg.resetOnPull
                    ? "\"reset\": \"pull\" puts it back there every time you " +
                      "let go of the trigger. Note a GADGET has no trigger to " +
                      "release, so on one this behaves as \"never\"."
                    : "the rotation then CONTINUES across trigger pulls - set " +
                      "\"reset\": \"pull\" if you would rather each pull " +
                      "started on your aim again."));
        }

        public static void Reset()
        {
            // Registrations are per-file and made once at startup. Only the
            // live spins are per-run, and a stale WeaponBase key cannot be
            // fired again, but clearing keeps this symmetric with the other
            // reset hooks rather than leaving a hole in the list.
            _live.Clear();
        }
    }
}
