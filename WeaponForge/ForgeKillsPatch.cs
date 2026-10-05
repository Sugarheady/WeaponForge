using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace WeaponForge
{
    // "Who killed that, and with which weapon?"
    //
    // The game answers the first half only. `Unit.RegisterKill` fires a public
    // `KilledAnotherUnit` event with (killer, victim) and is called from exactly
    // three places, all in DamagableResource, all gated on
    // `IsDead && owner != null`:
    //
    //     ProjectileCollided     -> projectile.Owner.RegisterKill(victim)
    //     OnHitByHitscanWeapon   -> weapon.Owner.RegisterKill(victim)
    //     OnExplosion            -> explosion.Owner.RegisterKill(victim)
    //
    // Two things follow, and both mattered here.
    //
    // 1. **Nothing else credits a kill at all** - not Die(), not burn ticks, not
    //    electricity, and not any of this mod's own damage. So a kill by the
    //    dash-ram or a gas cloud was invisible, which is exactly the case this
    //    was asked for ("dash through enemies to win fuel back"). Those paths
    //    call Credit() explicitly, which also registers the kill with the game
    //    so anything else listening sees it too.
    //
    // 2. **Only the owning Unit is recorded, never the weapon.** A weapon that
    //    pays for its own kills has to know which weapon did it, so each of the
    //    three routes needs its own answer - see the hooks at the bottom.
    //
    // Deliberately a separate copy from Module Forge's equivalent rather than
    // shared: each mod has to work with the other absent, and both patching the
    // same methods is fine because each only ever pays out its own content. Same
    // call as the loot pools.
    public static class ForgeKillsPatch
    {
        private static readonly ManualLogSource Log =
            ForgeLog.Source("WeaponForge.Kills");

        // The weapon DoShoot is running for. Frame-stamped so a stale value can
        // never be believed.
        private static WeaponBase _firing;
        private static int _firingFrame = -1;

        // The weapon whose projectile is resolving an explosion right now.
        private static WeaponBase _exploding;
        private static int _explodingFrame = -1;

        // Which weapon the kill currently being dispatched belongs to.
        private static WeaponBase _credit;

        // The weapon responsible for the hit currently being resolved, for
        // any feature that needs it during a damage callback.
        //
        // ORDERING CONTRACT: _credit is set in a Prefix and cleared in a
        // Finalizer on the same method, and Harmony runs
        // prefixes -> original -> postfixes -> finalizers. So any POSTFIX on
        // those same methods sees it. Do not read this from a prefix in
        // another patch class - prefix order between classes is arbitrary.
        public static WeaponBase Source { get { return _credit; } }

        // Shot -> the weapon that fired it, pruned rather than kept forever.
        private static readonly Dictionary<Component, WeaponBase> _shots =
            new Dictionary<Component, WeaponBase>();

        private static void Stamp(Component shot)
        {
            if (shot == null || _firing == null ||
                _firingFrame != Time.frameCount)
                return;

            if (_shots.Count > 512)
                Prune();

            _shots[shot] = _firing;
        }

        private static void Prune()
        {
            var dead = new List<Component>();

            foreach (var kv in _shots)
                if (kv.Key == null)
                    dead.Add(kv.Key);

            for (int i = 0; i < dead.Count; i++)
                _shots.Remove(dead[i]);

            if (_shots.Count > 512)
                _shots.Clear();
        }

        public static WeaponBase WeaponOf(Component shot)
        {
            WeaponBase w;
            return (shot != null && _shots.TryGetValue(shot, out w)) ? w : null;
        }

        // Credit a shot the mod created itself to the weapon it came from.
        //
        // Stamp() only fires while DoShoot is on the stack, which is exactly
        // right for the game's own firing path and no use at all for a shot
        // born mid-flight - a catalyst split copy is created from Update, with
        // no weapon anywhere near the stack. Without this a split kill would
        // pay nothing while the original's kill paid normally.
        // ALSO TELL MODULE FORGE, and this was a real gap. That mod keeps its
        // own shot->weapon map (deliberately - each mod must work with the
        // other absent) and stamps it from its own `Projectile.Shoot` patch,
        // which has the same DoShoot-scoped limitation for the same reason. So
        // a shot this mod created mid-flight was attributed HERE and nowhere
        // else, and a Module Forge on-kill module sitting on the weapon's grid
        // never matched the weapon and never paid out. In play: "the damage is
        // being passed on from the flechette weapon but the onkill effects are
        // not."
        //
        // One reflected call fixes it for every mid-flight shot at once -
        // split, chain, rally, bounce and the catalyst - rather than per
        // feature. Type name only, per the cross-mod rules; absent means a
        // no-op forever after the first lookup.
        public static void Attribute(Component shot, WeaponBase weapon)
        {
            if (shot == null || weapon == null)
                return;

            if (_shots.Count > 512)
                Prune();

            _shots[shot] = weapon;

            Mirror(shot, weapon);
        }

        private static MethodInfo _mfAttribute;
        private static bool _mfLooked;
        private static readonly object[] _mfArgs = new object[2];

        private static void Mirror(Component shot, WeaponBase weapon)
        {
            if (!_mfLooked)
            {
                _mfLooked = true;

                try
                {
                    // Scanned rather than asked for by name: AccessTools
                    // logs a warning for a type that is not there, and
                    // "Module Forge is not installed" is an entirely normal
                    // state.
                    foreach (Assembly a in
                             AppDomain.CurrentDomain.GetAssemblies())
                    {
                        Type t = a.GetType("ModuleForge.ModuleForgeKills", false);

                        if (t == null)
                            continue;

                        _mfAttribute = t.GetMethod(
                            "Attribute",
                            BindingFlags.Public | BindingFlags.Static,
                            null,
                            new Type[]
                            {
                                typeof(Component), typeof(WeaponBase)
                            },
                            null);

                        break;
                    }
                }
                catch (Exception)
                {
                    _mfAttribute = null;
                }
            }

            if (_mfAttribute == null)
                return;

            try
            {
                _mfArgs[0] = shot;
                _mfArgs[1] = weapon;
                _mfAttribute.Invoke(null, _mfArgs);
            }
            catch (Exception)
            {
                // One failure is enough - never let a courtesy call to the
                // other mod cost anything on every shot.
                _mfAttribute = null;
            }
        }

        // ------------------------------------------------------------------
        // Paying out
        // ------------------------------------------------------------------

        private static void Dispatch(Unit killer, Unit victim)
        {
            if (!ForgeOnKill.Any || killer == null || _credit == null)
                return;

            // ★★ KILLING YOUR OWN SIDE PAYS NOTHING, AND THIS GUARD HAS TO SIT
            // ABOVE `CoolFromAnyKill` RATHER THAN ABOVE `Pay`.
            //
            // `ForgeDamageDealt` is about DAMAGE; kills are a different owner
            // entirely, and the friendly test was never carried across. So
            // killing your own minion, your own charmed unit or your own
            // wingman drone paid the FULL `onKill` reward: resource on kill,
            // the kill buff, `coolHeat`, and the pickup drop at the victim's
            // feet. Worse than the damage hole in two ways - the reward is a
            // flat amount per kill rather than proportional, and a companion is
            // cheap to redeploy, so it is a resource printer rather than a
            // trickle.
            //
            // `ForgeHeat.CoolFromAnyKill` fires for ANY kill at all and sits
            // above the config lookup, so a guard placed above `Pay` alone
            // would still have cooled a meter for every companion you shot
            // down. **Attribution was solved for damage and never carried to
            // kills** - the same shape as the burn-tick attribution gap one
            // file along.
            if (victim != null && killer.IsFriendsWith(victim))
                return;

            // ANY weapon's kill can cool a meter whose own weapon asked for it.
            // Runs before the credited weapon's own payout and independently of
            // it, because the weapon that scored this kill usually has no
            // `onKill` block at all - it is the OTHER gun, the locked one, that
            // wants to hear about this.
            ForgeHeat.CoolFromAnyKill(killer, _credit);

            ForgeOnKill.Config cfg;

            if (!ForgeOnKill.TryGet(_credit.TemplateData, out cfg))
                return;

            if (!cfg.DoesAnything || !cfg.Allowed())
                return;

            try
            {
                Pay(cfg, killer, victim);
            }
            catch (Exception e)
            {
                Log.LogError("onKill payout failed: " + e);
            }
        }

        private static void Pay(
            ForgeOnKill.Config cfg, Unit killer, Unit victim)
        {
            if (cfg.amount > 0f && cfg.resourceAsset != null)
            {
                // A pickup that cannot be dropped falls through to paying the
                // tank, rather than paying nothing. The reward the file asked
                // for is "this much of this resource per kill"; the pickup is
                // how it arrives, and a missing prefab should cost the flourish
                // and not the payout. This used to just call `Drop` and stop,
                // so `asPickup` with no usable template paid zero and said so
                // nowhere.
                bool dropped =
                    cfg.asPickup &&
                    ForgeKillPickup.Drop(
                        victim != null
                            ? (Vector2)victim.transform.position
                            : (Vector2)killer.transform.position,
                        cfg.resourceAsset, cfg.amount);

                if (!dropped && killer.HasTank(cfg.resourceAsset))
                    Grant(killer.GetTank(cfg.resourceAsset), cfg.amount);
            }

            if (cfg.clearBurn > 0f)
            {
                // The setter clamps at 0 and only adds the catching-fire bonus
                // when crossing upward, so pulling it down is safe.
                killer.ComponentData.BurnLevel -= cfg.clearBurn;
            }

            if (cfg.coolHeat > 0f)
            {
                // Scoped to the weapon that scored the kill - `_credit` is that
                // weapon, and the `onKill` block was found by looking this
                // config up from it. A secondary's kill cooling the primary
                // would be a different feature and not one anybody asked for.
                ForgeHeat.CoolOn(killer, _credit, cfg.coolHeat);
            }

            if (cfg.buffDuration > 0f &&
                (cfg.buffFireRate > 0f || cfg.buffDamage > 0f))
            {
                // The per-weapon override wins over the global default; an empty
                // string means "follow the config".
                bool indicate = string.IsNullOrEmpty(cfg.buffIndicator)
                    ? ForgeBuffHud.style != ForgeBuffHud.Style.Off
                    : ForgeBuffHud.ParseStyle(cfg.buffIndicator) !=
                          ForgeBuffHud.Style.Off;

                ForgeKillBuff.Grant(
                    _credit, cfg.buffFireRate, cfg.buffDamage,
                    cfg.buffDuration, Mathf.Max(1, cfg.buffMaxStacks),
                    cfg.indicatorIcon, cfg.indicatorColor, indicate);
            }

            if (cfg.gas)
                LeaveGas(cfg, killer, victim);

            if (!string.IsNullOrEmpty(cfg.sfxGuid))
                AudioManager.PlaySfx(cfg.sfxGuid, killer.transform.position);
        }

        // A cloud on the corpse, built from the weapon's own `gas` block.
        //
        // The position is taken BEFORE anything else runs, because the victim
        // is on its way out - the same reason the pickup drop above reads it
        // where it does.
        private static void LeaveGas(
            ForgeOnKill.Config cfg, Unit killer, Unit victim)
        {
            // A cloud's own kill only seeds another cloud when the file asked
            // for the chain. See ForgeOnKill.Config.gasChains.
            if (ForgeGasCloud.Crediting && !cfg.gasChains)
                return;

            ForgeGas.Config gas;

            // Registered by ApplyGas. The builder refuses `onKill.gas` on a
            // weapon whose gas block is missing or was itself refused, and
            // turns the flag off when it does - so reaching here without a
            // config means something newer than this code introduced a third
            // way to lose it, and staying silent would make it look like the
            // cloud simply failed to appear.
            if (!ForgeGas.TryGet(_credit.TemplateData, out gas) || gas == null)
            {
                if (!_saidNoCloud)
                {
                    _saidNoCloud = true;

                    Log.LogWarning(
                        "onKill \"gas\" fired on '" +
                        ((_credit.TemplateData != null)
                            ? _credit.TemplateData.name : "?") +
                        "' but that weapon has no registered gas cloud, so no " +
                        "gas was left. The \"gas\" block is what describes the " +
                        "cloud - this key only decides WHEN one appears.");
                }

                return;
            }

            Vector2 where = (victim != null)
                ? (Vector2)victim.transform.position
                : (Vector2)killer.transform.position;

            // No inherited velocity: nothing arrived here travelling. A drift
            // cloud rolls from where the shot was going, and a corpse is not
            // going anywhere - so a `drifts` cloud just sits, which is right.
            ForgeGasCloud cloud =
                ForgeGasCloud.Spawn(gas, where, Vector2.zero, killer);

            if (cloud == null)
                return;

            // Live values, not the template's: the cloud is resisted and
            // coloured like the gun as it is NOW, including anything adaptive
            // damage type or a module has done to it.
            cloud.SetDamageType(_credit.Damage.damageType);
            cloud.SetWeapon(_credit);
        }

        private static bool _saidNoCloud;

        // Charge() is the right call for a normal tank - it clamps to
        // [0, Capacity] where writing Value directly would not.
        //
        // But an INFINITE tank (Money, the run-wide currency) need not carry a
        // meaningful Capacity, and Charge would then clamp the payout to it and
        // grant nothing. Its Value setter refuses decreases on its own, so
        // adding directly is both safe and the only thing that works.
        private static void Grant(ResourceTank tank, float amount)
        {
            if (tank == null || amount <= 0f)
                return;

            if (tank.isInfinite)
                tank.Value = tank.Value + amount;
            else
                tank.Charge(amount);
        }

        // For damage the game never credits. Registers the kill properly rather
        // than paying out behind the game's back, so the music controller and
        // anything else listening also see it.
        public static void Credit(Unit killer, Unit victim, WeaponBase weapon)
        {
            if (killer == null || victim == null || weapon == null)
                return;

            WeaponBase previous = _credit;
            _credit = weapon;

            try
            {
                killer.RegisterKill(victim);
            }
            catch (Exception e)
            {
                Log.LogError("Crediting a kill failed: " + e);
            }
            finally
            {
                _credit = previous;
            }
        }

        // Did the thing we just damaged die? Used by the mod's own damage paths,
        // which have to check for themselves.
        public static bool JustDied(HealthBase hb)
        {
            var dr = hb as DamagableResource;
            return dr != null && dr.IsDead;
        }

        // ------------------------------------------------------------------
        // Hooks
        // ------------------------------------------------------------------

        [HarmonyPatch(typeof(Unit), "RegisterKill")]
        public class OnRegisterKill
        {
            static void Postfix(Unit __instance, Unit unit)
            {
                Dispatch(__instance, unit);
            }
        }

        // DoShoot is provably on the path of every shot - the shoot SFX and the
        // muzzle flash both fire from it - and FireSingle instantiates AND calls
        // Shoot() synchronously inside it, so this scope is exact.
        [HarmonyPatch(typeof(WeaponBase), "DoShoot")]
        public class OnDoShoot
        {
            static void Prefix(WeaponBase __instance)
            {
                _firing = __instance;
                _firingFrame = Time.frameCount;
            }

            // Finalizer: if DoShoot throws, a stale weapon left here would be
            // stamped onto somebody else's next shot.
            static void Finalizer()
            {
                _firing = null;
                _firingFrame = -1;
            }
        }

        [HarmonyPatch(typeof(Projectile), "Shoot")]
        public class OnShoot
        {
            static void Postfix(Projectile __instance) { Stamp(__instance); }
        }

        [HarmonyPatch(typeof(PhysicsProjectile), "Shoot")]
        public class OnShootPhysics
        {
            static void Postfix(PhysicsProjectile __instance)
            {
                Stamp(__instance);
            }
        }

        [HarmonyPatch(typeof(DamagableResource), "ProjectileCollided")]
        public class OnProjectileCollided
        {
            static void Prefix(IProjectile projectile)
            {
                _credit = WeaponOf(projectile as Component);
            }

            static void Finalizer() { _credit = null; }
        }

        [HarmonyPatch(typeof(DamagableResource), "OnHitByHitscanWeapon")]
        public class OnHitscan
        {
            static void Prefix(HitscanWeapon weapon) { _credit = weapon; }
            static void Finalizer() { _credit = null; }
        }

        [HarmonyPatch(typeof(DamagableResource), "OnExplosion")]
        public class OnExplosionHit
        {
            static void Prefix()
            {
                _credit = (_explodingFrame == Time.frameCount)
                    ? _exploding : null;
            }

            static void Finalizer() { _credit = null; }
        }

        // An Explosion is a struct with no route back to the weapon, so the only
        // place to learn it is while the projectile that owns it is on the stack.
        [HarmonyPatch(typeof(Projectile), "SpawnExplosion")]
        public class OnSpawnExplosion
        {
            static void Prefix(Projectile __instance)
            {
                _exploding = WeaponOf(__instance);
                _explodingFrame = Time.frameCount;
            }

            static void Finalizer()
            {
                _exploding = null;
                _explodingFrame = -1;
            }
        }

        [HarmonyPatch(typeof(PhysicsProjectile), "SpawnExplosion")]
        public class OnSpawnExplosionPhysics
        {
            static void Prefix(PhysicsProjectile __instance)
            {
                _exploding = WeaponOf(__instance);
                _explodingFrame = Time.frameCount;
            }

            static void Finalizer()
            {
                _exploding = null;
                _explodingFrame = -1;
            }
        }
    }
}
