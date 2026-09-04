using System;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace WeaponForge
{
    // Three thin hooks. See ForgeHitSound for what the tick actually is.
    public static class ForgeHitSoundPatch
    {
        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("WeaponForge.HitSound");

        // ------------------------------------------------------------------
        // How much damage this tick is for.
        //
        // The victim's UnityEvent is fired from inside DamagableResource.Damage
        // and carries no arguments at all, so the amount has to be caught on
        // the way in. A PREFIX is exact rather than approximate: Damage()
        // invokes onDamage synchronously further down its own body, so nothing
        // can run in between.
        // ------------------------------------------------------------------
        private static float _amount;
        private static int _amountFrame = -1;

        public static float Amount
        {
            get { return (_amountFrame == Time.frameCount) ? _amount : 0f; }
        }

        [HarmonyPatch(typeof(DamagableResource), "Damage", new[] { typeof(float) })]
        public class OnDamage
        {
            static void Prefix(float amount)
            {
                if (!ForgeHitSound.Any)
                    return;

                _amount = amount;
                _amountFrame = Time.frameCount;
            }
        }

        // ------------------------------------------------------------------
        // The tick itself.
        //
        // Hooked at SfxPlayer.Play() and filtered on the one guid, rather than
        // at the UnityEvent - there is no seam on a UnityEvent wired in a
        // prefab, and this is the single place all 83 of them funnel through.
        //
        // WE PLAY IT OURSELVES rather than letting the original run, because
        // SfxPlayer.Play goes through an async PlayAsync with a delay: the
        // actual AudioManager call may land a frame later, and anything we
        // stashed for it would be stale by then. Playing it here keeps the
        // whole decision synchronous. `PlaySfx(guid, transform)` is used so the
        // sound still FOLLOWS the victim, which is what the prefabs asked for
        // with updatePosition: 1.
        // ------------------------------------------------------------------
        [HarmonyPatch(typeof(SfxPlayer), "Play", new Type[0])]
        public class OnPlay
        {
            static bool Prefix(SfxPlayer __instance)
            {
                if (!ForgeHitSound.Any || __instance == null)
                    return true;

                // Every other sound in the game is none of our business.
                if (__instance.sfx != ForgeHitSound.HitGuid)
                    return true;

                try
                {
                    var victim = __instance.GetComponentInParent<HealthBase>();

                    ForgeHitSound.Config cfg = ForgeHitSound.For(victim);

                    if (cfg == null || !cfg.DoesAnything)
                        return true;      // stock behaviour, untouched

                    if (!ForgeHitSound.Ready(cfg, victim))
                        return false;     // throttled away

                    if (cfg.silence)
                        return false;

                    string guid = !string.IsNullOrEmpty(cfg.sfx)
                        ? cfg.sfx
                        : ForgeHitSound.HitGuid;

                    bool voice =
                        cfg.pitchMode != ForgeHitSound.PitchNone ||
                        cfg.vary > 0f ||
                        Mathf.Abs(cfg.volume - 1f) > 0.001f;

                    if (voice)
                    {
                        float pitch = ForgeHitSound.PitchFor(
                            cfg, victim, ForgeHitSoundPatch.Amount);

                        ForgeHitSound.Expect(pitch, cfg.volume);
                    }

                    AudioManager.PlaySfx(guid, __instance.transform);

                    return false;
                }
                catch (Exception e)
                {
                    Log.LogError("Hit sound failed: " + e);
                    return true;          // fall back to the game's own
                }
            }
        }

        // ------------------------------------------------------------------
        // Pitch, and the reset that stops it leaking.
        //
        // THE GAME NEVER WRITES `audioSource.pitch`, and its sources come from
        // a pool - so the moment we set one, the next unrelated sound to
        // borrow that source inherits it. A detuned explosion four seconds
        // later would be almost impossible to trace back here. So once any
        // weapon uses a voice setting, EVERY play gets an explicit pitch:
        // ours where we asked for one, and a plain 1 everywhere else.
        //
        // Volume needs no such guard - PlaySfx assigns it from the Sfx on
        // every play, so it cannot survive into the next sound.
        //
        // Patching the PRIVATE overload on purpose: it is the one funnel all
        // three public PlaySfx entry points reach, so one hook covers them.
        // ------------------------------------------------------------------
        [HarmonyPatch(typeof(AudioManager), "PlaySfx",
            new[] { typeof(Sfx), typeof(Vector2), typeof(Transform) })]
        public class OnPlaySfx
        {
            static void Postfix(AudioManager __instance, int __result)
            {
                if (!ForgeHitSound.TouchesVoice || __result < 0)
                    return;

                try
                {
                    float pitch, volume;

                    ForgeHitSound.Take(out pitch, out volume);

                    ForgeHitSound.SetVoice(
                        __instance, __result, pitch, volume);
                }
                catch (Exception e)
                {
                    Log.LogError("Setting a hit sound's voice failed: " + e);
                }
            }
        }
    }
}
