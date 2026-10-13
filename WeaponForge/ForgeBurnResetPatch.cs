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

            // The wingman CLONES - real assets, real prefabs, and entries in
            // ~20 registries that are deliberately never cleared here (see
            // ForgeCrit.Reset for why clearing those would unbuild every
            // weapon). One clone per weapon swap per run, so without this they
            // would accumulate for the whole session. The build-time
            // ForgeWingman registry is NOT reset, for exactly the reason
            // ForgeCrit's is not.
            ForgeWingmanWeapon.Reset();

            // ForgeWingmanPatch.Reset() calls ForgeWingmanController.Reset(),
            // which now TAKES THE DRONE BODIES AS WELL as clearing the list.
            // An orb dies with the ship; a drone body is a SavableEntity
            // parented to the entity manager's own root and would otherwise
            // survive into the next run, owned by nothing.
            ForgeWingmanPatch.Reset();
            ForgeWingmanBody.Reset();
            ForgeCourier.Reset();
            ForgeWingmanShotPatch.Reset();
            ForgeMinionTrimPatch.Reset();

            // The saved-companion record. On a NEW run this stops a loaded
            // save's wingmen following you into it. On the CONTINUE path this
            // is a prefix and the record is read in a postfix, so the reset
            // runs first and there is nothing yet to lose - that ordering is
            // the reason ForgeWingmanSave reads where it does.
            ForgeWingmanSave.Reset();

            // The once-per-run log gate. The widgets themselves are components
            // on the ship and die with it, so there is nothing else to drop.
            ForgeGaugePatch.Reset();
            ForgeMuzzleTint.Reset();
            ForgeBeamTint.Reset();

            // Every once-only log gate in the mod, by naming convention rather
            // than by a list somebody has to maintain. This covers the three
            // hand-written ResetDiagnostics below, which are kept anyway: they
            // cost nothing, and if the reflective sweep ever fails they are
            // what still works.
            ForgeDiagnosticGates.Reset();

            ForgeFade.ResetDiagnostics();
            ForgeMinionGrudge.ResetDiagnostics();
            ForgeGuidedPatch.ResetDiagnostics();

            // Cancel any time-scale modifier of ours still standing, and drop
            // the "a Module Forge module granted a slow" gate. The manager is
            // scene-scoped, so the first half is belt rather than braces - but
            // it is one call, and the alternative was a public method that
            // described itself as being for "a future cancel all of ours" and
            // was never called by anything.
            ForgeSlow.Reset();

            // Drops the cached player Aimer bloom falls back to for a shot
            // fired by something with no barrel of its own (a wingman). It is a
            // scene object, so a stale one is already null-equal and would be
            // re-found anyway - clearing it makes that deterministic rather
            // than incidental. ForgeBloom's own registry is deliberately NOT
            // cleared: those are parsed once at startup from the weapon files,
            // and clearing them would unbuild the weapon for the session.
            ForgeBloomPatch.Reset();

            // RANK'S PROGRESS IS PER-RUN AND ITS CONFIGS ARE NOT, which is the
            // same split as crit's and bloom's. `ForgeRank.Reset` drops the
            // ranks, the banked XP, the draw counts and the paid-station
            // ledger; the `_weapons` registry behind them is registered once at
            // startup from the weapon files and clearing it would silently
            // delete rank from every weapon for the rest of the session.
            //
            // The draw state goes too, and it has a body on screen: an
            // outstanding set of choice orbs belongs to a run that has ended.
            ForgeRank.Reset();
            ForgeRankDraw.Reset();

            // NEW GAME PLUS (Game Mode Forge): a jump is a new run, so the two
            // lines above drop every ranked gun - as they must. The carry holds
            // the ranks OUTSIDE this list from the old world's Leaving to the
            // new world's Arrived, and only there; here it just finds New Game
            // Plus (by type name, if it is installed) and drops anything held
            // for a jump that never arrived.
            ForgeRankCarry.OnRunEntry();

            // DRONE MODES: a new run starts on normal (answer 8). A Continue
            // reads the save's modes back in the Load POSTFIX, after this; a
            // New Game Plus jump hands them back on Arrived. The carry lookup
            // runs from here, like rank's.
            ForgeDroneModes.Reset();
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
