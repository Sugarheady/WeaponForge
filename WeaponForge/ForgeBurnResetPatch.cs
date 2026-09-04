using HarmonyLib;

namespace WeaponForge
{
    // Drop WeaponForge's per-run state at each run entry, so nothing from a
    // previous run leaks into the next one.
    //
    // TWO ENTRIES, ONE LIST. A run can begin either way - `RunData.Initialize`
    // for a new one, `GameSaver.Load` for a continue - and both need the same
    // work. The list used to be written out twice, byte for byte, which is a
    // structural invitation: anything added to a reset list that exists twice
    // is one paste away from being half-wired, and a half-wired reset fails
    // only on the path you did not test. It exists once now.
    //
    // WHAT BELONGS HERE. Static state holding live objects (`Unit`,
    // `Projectile`, `WeaponBase`, `ParticleSystem`) or a per-run accumulator.
    // What does NOT belong: the build-time registries keyed by weapon asset,
    // which are populated once at load and would unbuild the weapons if
    // cleared. Live per-shot state rides a MonoBehaviour instead and dies with
    // the scene on its own, which is why this list is as short as it is.
    public static class ForgeBurnResetPatch
    {
        private static void ResetAll()
        {
            // Only when WeaponForge owns the burn engine. With ModuleForge
            // present its reset handles the shared half, and WeaponForge's
            // effects re-register on continue via OnInstalled.
            if (ForgeBurnCompat.OwnsPatches)
                ForgeBurn.Reset();

            ForgeProjectileTracker.Reset();
            ForgeCrit.Reset();
            ForgeLeech.Reset();
            ForgeContact.Reset();
            ForgeExecute.Reset();
            ForgeCharm.Reset();
            ForgeAdaptive.Reset();
            ForgeSpiral.Reset();
            ForgeStun.Reset();

            // Both hold live `WeaponBase` / `ParticleSystem` references, which
            // do not survive a run - a stale key would keep one alive for the
            // whole session.
            ForgeWeaponStats.Reset();
            ForgeMuzzleTint.Reset();

            // Every once-only log gate in the mod, by naming convention rather
            // than by a list somebody has to maintain. This covers the three
            // hand-written ResetDiagnostics below, which are kept anyway: they
            // cost nothing, and if the reflective sweep ever fails they are
            // what still works.
            ForgeDiagnosticGates.Reset();

            ForgeFade.ResetDiagnostics();
            ForgeMinionGrudge.ResetDiagnostics();
            ForgeGuidedPatch.ResetDiagnostics();

            // Cancel any time-scale modifier of ours still standing. The
            // manager is scene-scoped, so this is belt rather than braces -
            // but it is one call, and the alternative was a public method that
            // described itself as being for "a future cancel all of ours" and
            // was never called by anything.
            ForgeSlow.ClearTime();
        }

        [HarmonyPatch(typeof(RunData), "Initialize")]
        public class OnNewRun
        {
            static void Prefix()
            {
                ResetAll();
            }
        }

        [HarmonyPatch(typeof(Punk.SaveLoad.GameSaver), "Load")]
        public class OnContinue
        {
            static void Prefix()
            {
                ResetAll();
            }
        }
    }
}
