using System;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace WeaponForge
{
    // MAKES `cellConvertData` WORK ON EVERY WEAPON, not just beams.
    //
    // The field lives on `WeaponData` - every weapon in the game carries one -
    // and `WeaponBase` copies it into a public `CellConvertData` property. But
    // exactly one thing in the game ever acts on it:
    //
    //     // HitscanWeapon, and nowhere else
    //     if (base.CellConvertData.enabled)
    //         ServiceLocator.Get<Level>().ConvertCells(
    //             base.CellConvertData, hit.point);
    //
    // So a projectile weapon with terrain conversion configured did nothing at
    // all, silently, and the builder page had to warn about it. This closes
    // that gap: a bullet, a grenade or a blast can terraform now, using the
    // game's own `Level.ConvertCells` and the weapon's own settings.
    //
    // ONE STOCK WEAPON USES THE FEATURE - `Weapon Crawler Laser`, which turns
    // ten cell types into Hazard within radius 4. That is an enemy weapon whose
    // whole job is making the room hostile, and it is the only reference
    // implementation there is.
    //
    // WHY THIS IS IN WEAPON FORGE AND NOT MODULE FORGE. Projectile patches are
    // Weapon Forge's territory - it already owns `Projectile.OnObjectHit`
    // hooks for bounce, catalyst and gas. Module Forge's terrain module works
    // on its own without this (beam-only, which is the base game's behaviour)
    // and gains projectiles when this mod is present. Degrading to the game's
    // own behaviour is the right way for one mod to be optional to another.
    public static class ForgeTerraformPatch
    {
        private static readonly ManualLogSource Log =
            ForgeLog.Source("WeaponForge");

        private static bool _said;

        // ---- plain projectiles ------------------------------------------
        [HarmonyPatch(typeof(Projectile), "OnObjectHit")]
        public class OnProjectileHit
        {
            static void Postfix(Projectile __instance, RaycastHit2D hit)
            {
                Convert(__instance, hit.point);
            }
        }

        // ---- lobbed / physics shots --------------------------------------
        //
        // A PhysicsProjectile is a different class with no shared base, so it
        // needs its own hook - the same split every feature in this mod has to
        // make. It collides through Unity rather than by raycast, so the
        // contact point comes from the collision instead of a hit.
        [HarmonyPatch(typeof(PhysicsProjectile), "OnCollisionEnter2D")]
        public class OnPhysicsHit
        {
            static void Postfix(
                PhysicsProjectile __instance, Collision2D collision)
            {
                if (collision == null || collision.contactCount <= 0)
                    return;

                Convert(__instance, collision.GetContact(0).point);
            }
        }

        // A projectile has no back-reference to the weapon that fired it -
        // neither class stores one - so it comes from the same shot->weapon map
        // the kill attribution uses, stamped at Shoot while DoShoot is on the
        // stack. That map is also what makes this work for a shot the mod
        // created itself (a split fragment, a catalyst copy), since those are
        // registered explicitly.
        private static void Convert(Component shot, Vector2 where)
        {
            WeaponBase weapon = ForgeKillsPatch.WeaponOf(shot);

            if (weapon == null)
                return;

            CellConvertData cfg = weapon.CellConvertData;

            // `CellConvertData` is a CLASS, so an unconfigured weapon can hand
            // back null as well as a disabled one.
            if (cfg == null || !cfg.enabled)
                return;

            // A conversion with nothing to convert, or nothing to convert INTO,
            // would walk the radius and write nothing - or throw on the null
            // result. Both are worth skipping quietly; the builder warns.
            if (cfg.resultCellType == null ||
                cfg.convertableCells == null ||
                cfg.convertableCells.Count == 0)
            {
                return;
            }

            try
            {
                Level level;

                if (!ServiceLocator.TryGet<Level>(out level) || level == null)
                    return;

                level.ConvertCells(cfg, where);

                if (!_said)
                {
                    _said = true;

                    Log.LogInfo(
                        "terraform: a NON-BEAM weapon just converted terrain. " +
                        "The game only ever runs \"cellConvertData\" from " +
                        "HitscanWeapon, so this is Weapon Forge extending it " +
                        "to projectiles and lobbed shots. Logged once.");
                }
            }
            catch (Exception e)
            {
                Log.LogError("Terrain conversion failed: " + e);
            }
        }
    }
}
