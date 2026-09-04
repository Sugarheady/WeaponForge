using System;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace WeaponForge
{
    // Two ways a unit comes to carry contact damage.
    public static class ForgeContactPatch
    {
        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("WeaponForge.Contact");

        // ------------------------------------------------------------------
        // 1. MARKED - this weapon touched it.
        //
        // ONE HOOK COVERS ALL THREE DAMAGE ROUTES. `DamagableResource.Damage`
        // is the funnel every route reaches, and ForgeKillsPatch.Source is live
        // inside it - set in a prefix on each route and cleared in a finalizer.
        // So this marks on a bullet, a beam and an explosion alike, and stays
        // correctly silent for a burn tick or a cell collision, which have no
        // weapon behind them.
        // ------------------------------------------------------------------
        [HarmonyPatch(typeof(DamagableResource), "Damage",
            new[] { typeof(float) })]
        public class OnDamage
        {
            static void Postfix(DamagableResource __instance)
            {
                if (!ForgeContact.Any || __instance == null)
                    return;

                try
                {
                    WeaponBase source = ForgeKillsPatch.Source;

                    if (source == null || source.TemplateData == null)
                        return;

                    ForgeContact.Config cfg;

                    if (!ForgeContact.TryGet(source.TemplateData, out cfg))
                        return;

                    // The always-on modes are handled by the sweeper below;
                    // this route is only for "units this weapon has touched".
                    if (cfg.who != ForgeContact.WhoMarked)
                        return;

                    if (__instance.IsDead)
                        return;

                    Unit target = __instance.GetComponentInParent<Unit>();

                    if (target == null)
                        return;

                    // Never whoever is holding the gun. Reachable in practice:
                    // explosions in this game have no owner filter at all.
                    if (source.Owner != null &&
                        ReferenceEquals(target, source.Owner))
                    {
                        return;
                    }

                    ForgeContactController.Give(
                        target, cfg, source.Owner, source, cfg.window);
                }
                catch (Exception e)
                {
                    Log.LogError("Marking a unit contact-damaging failed: " + e);
                }
            }
        }

        // ------------------------------------------------------------------
        // WHAT IS IN THE LOADOUT.
        //
        // `WeaponBase.Equip` and `WeaponBase.Dispose` are the whole story:
        // Equip is called from exactly `Shooter.SetWeapon` (primary and
        // secondary) and `WeaponBasedActiveModule.Activate` (gadgets), and
        // Dispose from `SetWeapon` replacing a weapon and from
        // `OnUninstalled`. So this pair covers every equip path including
        // gadgets, with no per-frame work and no module-grid walking.
        //
        // Both are deliberately tiny and swallow everything - they sit on the
        // equip path of every weapon in the game, mod-built or not.
        // ------------------------------------------------------------------
        [HarmonyPatch(typeof(WeaponBase), "Equip")]
        public class OnEquip
        {
            static void Postfix(WeaponBase __instance)
            {
                try { ForgeContact.Equipped(__instance); }
                catch (Exception e) { Log.LogError("Contact equip: " + e); }
            }
        }

        [HarmonyPatch(typeof(WeaponBase), "Dispose")]
        public class OnDispose
        {
            static void Prefix(WeaponBase __instance)
            {
                try { ForgeContact.Unequipped(__instance); }
                catch (Exception e) { Log.LogError("Contact unequip: " + e); }
            }
        }

        // ------------------------------------------------------------------
        // 2. THE ALWAYS-ON MODES - every enemy, and/or your ship.
        //
        // A periodic scan rather than a spawn hook, because units arrive from
        // several unrelated places (level generation, minion spawners,
        // sub-emitters, boss phases) and there is no single event for "a unit
        // now exists". Twice a second is far below anything measurable and
        // means a newly spawned enemy is armed within half a second.
        //
        // The component refuses to attach twice, so re-scanning is free.
        // ------------------------------------------------------------------
        public class ForgeContactSweeper : MonoBehaviour
        {
            private float next;

            private static ForgeContactSweeper _live;

            public static void Ensure()
            {
                if (_live != null || !ForgeContact.WantsWorld)
                    return;

                var go = new GameObject("Forge Contact Sweeper");
                DontDestroyOnLoad(go);

                _live = go.AddComponent<ForgeContactSweeper>();
            }

            // Whether the last tick had a live world rule, so the transition
            // either way happens exactly once instead of every half second.
            private bool armed;

            private void Update()
            {
                if (Time.time < next)
                    return;

                next = Time.time + 0.5f;

                // `Live`, not the old build-time static: the rule belongs to an
                // EQUIPPED weapon. See ForgeContact for the bug this fixes.
                ForgeContact.Config cfg = ForgeContact.Live;

                try
                {
                    if (cfg == null || !cfg.enabled)
                    {
                        // Unequipping has to put the world BACK, not merely stop
                        // arming new units - the controllers already handed out
                        // are permanent (expiresAt 0), so without this the rule
                        // would persist on everything already alive and the
                        // weapon would look like it had no off switch.
                        if (armed)
                        {
                            armed = false;
                            int n = ForgeContactController.RevokeWorld();

                            Log.LogInfo(
                                "contact damage: no equipped weapon carries an " +
                                "always-on rule any more, so it is off and " +
                                n + " unit(s) were put back to the game's own " +
                                "behaviour.");
                        }

                        return;
                    }

                    if (!armed)
                    {
                        armed = true;

                        Log.LogInfo(
                            "contact damage: an equipped weapon carries \"who\": \"" +
                            ForgeContact.WhoWord(cfg.who) +
                            "\", so that rule is now live. It switches off by " +
                            "itself when the weapon is unequipped.");
                    }

                    Sweep(cfg);
                }
                catch (Exception e)
                {
                    Log.LogError("The contact sweep failed: " + e);
                    next = Time.time + 5f;
                }
            }

            private static void Sweep(ForgeContact.Config cfg)
            {
                int player = LayerMask.NameToLayer("Player");

                Unit[] units = UnityEngine.Object.FindObjectsByType<Unit>(
                    FindObjectsSortMode.None);

                for (int i = 0; i < units.Length; i++)
                {
                    Unit u = units[i];

                    if (u == null)
                        continue;

                    // The ship is on the Player layer, not Entities - the same
                    // distinction that once made `pullPlayer` dead code.
                    bool isShip = u.gameObject.layer == player;

                    bool want =
                        isShip
                            ? (cfg.who == ForgeContact.WhoShip ||
                               cfg.who == ForgeContact.WhoAll)
                            : (cfg.who == ForgeContact.WhoEnemies ||
                               cfg.who == ForgeContact.WhoAll);

                    if (!want)
                        continue;

                    if (u.GetComponent<ForgeContactController>() != null)
                        continue;

                    // Permanent for as long as the weapon is equipped: these
                    // modes are a world rule, not a debuff, so there is no
                    // window to run out - `RevokeWorld` is what ends them.
                    // Flagged as world-granted so revoking cannot also tear off
                    // a marked hit or a lunge's own contact window.
                    ForgeContactController.Give(u, cfg, u, null, 0f, true);
                }
            }
        }
    }
}
