using System;
using System.Collections.Generic;
using BepInEx.Logging;

namespace WeaponForge
{
    // The key that puts rank choices away and brings them back.
    //
    // ★ R20 252: *"find a keyboard/gamepad key that can be rebound in the
    // .cfg/config. I would make a note on the weapon itself ... of the key to
    // use."* Bound in `[Rank choices]` of this mod's BepInEx config, and the
    // weapon card of a gun whose rank lets you pick says which key it is.
    //
    // ★ WHY THESE DEFAULTS, measured off the game's own `InputActions` asset:
    // while flying (`ShipControl`) nothing uses keyboard H or gamepad D-pad
    // up. D-pad down opens the item wheel, D-pad left the ship menu; the map
    // uses the D-pad to scroll, which is why the key only acts while the
    // ship's flying controls are on.
    //
    // ★ PER PLAYER since drone modes (2026-10-01, concept 12.2 N, *"yes that
    // sounds perfect"*). Until then one press put away EVERY player's choices
    // in co-op. Now the key is read on each ship's own devices
    // (ForgeKeyBinding) and puts away only that ship's sets. The same reader
    // serves the drone-mode keys, so the two can never disagree about whose
    // key is whose.
    //
    // ★ NEVER THE GAME'S CONTROLS. ForgeKeyBinding reads device buttons
    // directly; nothing is bound into the game's player input. A bad binding
    // must never cost the rank: a read that fails latches the key off, says so
    // once, and the choices still work with the use key.
    public static class ForgeRankKeys
    {
        private static readonly ManualLogSource Log =
            ForgeLog.Source("WeaponForge.Rank");

        private static ForgeKeyBinding _binding =
            ForgeKeyBinding.Parse("Rank choices", "PadButton", "H", "DpadUp");

        private static bool _broken;

        public static void Configure(string key, string pad)
        {
            _binding = ForgeKeyBinding.Parse("Rank choices", "PadButton", key, pad);
        }

        // For the card and the log: "H (pad: D-pad up)".
        public static string Describe()
        {
            string d = _binding != null ? _binding.Describe() : "no key";

            return d == "no key" ? "no key (set one in [Rank choices])" : d;
        }

        // The ships whose player pressed the key this frame, flying only.
        // Polled by the rank queue while a set is owed or out.
        public static void PressedBy(List<Unit> into)
        {
            if (into == null || _broken || _binding == null || !_binding.IsSet)
                return;

            try
            {
                foreach (Unit ship in ForgeKeyBinding.Ships())
                {
                    if (ship == null || !ForgeKeyBinding.Flying(ship))
                        continue;

                    if (_binding.Pressed(ForgeKeyBinding.DevicesOf(ship)))
                        into.Add(ship);
                }
            }
            catch (Exception e)
            {
                _broken = true;

                Log.LogWarning(
                    "rank: the put-away key could not be read (" +
                    e.GetType().Name + ": " + e.Message + ") - it is off this " +
                    "launch. Choices are still taken with the use key.");
            }
        }
    }
}
