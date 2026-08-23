using System;

namespace WeaponForge
{
    // Weapon-module effect: while the weapon is equipped, speeds up the
    // burn tick FREQUENCY on enemies (additive, clamped, player excluded).
    // Routes through ForgeBurnCompat so it feeds ModuleForge when that mod
    // is present, or WeaponForge's own ForgeBurn engine otherwise.
    //
    // Save/load safe: a weapon module persists by Id and its effects are
    // rebuilt from the registry via Clone() on continue, re-firing
    // OnInstalled so the boost re-registers.
    [Serializable]
    public class ForgeBurnRateEffect : ModuleEffect
    {
        public float ticksPerSecond;

        // Extra ticks/sec per module LEVEL above 1, so a green-crate
        // BoosterCore next to this weapon speeds its burn up. 0 (the
        // default) keeps the old flat behaviour.
        public float ticksPerSecondPerLevel;

        private bool _registered;
        private float _applied;
        private Unit.Data _owner;

        // The game's own effects read strength as
        // effectiveness.GetElement(Module.Level - 1), which is the formula
        // baseValue + change * i. This is the same shape without pulling
        // FloatSeries into WeaponForge's burn path - a plain
        // "burnTickRate": 0.5 stays exactly what it always was.
        private float CurrentValue
        {
            get
            {
                int level = (base.Module != null) ? base.Module.Level : 1;
                if (level < 1)
                    level = 1;

                return ticksPerSecond + ticksPerSecondPerLevel * (level - 1);
            }
        }

        public override void OnInstalled(Unit.Data unit)
        {
            if (_registered)
                return;

            _applied = CurrentValue;
            _owner = unit;
            ForgeBurnCompat.AddRate(unit, _applied);
            _registered = true;
        }

        public override void OnUninstalled(Unit.Data unit)
        {
            if (!_registered)
                return;

            ForgeBurnCompat.RemoveRate(_owner ?? unit, _applied);
            _registered = false;
            _owner = null;
            _applied = 0f;
        }

        // The module's level can change while it stays installed - a
        // BoosterCore placed next to it raises Level without any
        // install/uninstall - so re-sync the amount we contributed.
        //
        // Deliberately Remove-then-Add rather than an "adjust by delta"
        // call: those two already exist on BOTH sides of ForgeBurnCompat and
        // are exercised on every equip, so this needs no new cross-mod
        // plumbing and cannot silently no-op if ModuleForge's API drifts.
        // The owner exclusion is refcounted, so release+exclude nets to the
        // same count within this one synchronous call.
        public override void OnRecalculateUnitStats(Unit.Data unit)
        {
            if (!_registered)
                return;

            float now = CurrentValue;

            if (now == _applied)
                return;

            Unit.Data owner = _owner ?? unit;

            ForgeBurnCompat.RemoveRate(owner, _applied);
            ForgeBurnCompat.AddRate(owner, now);

            _applied = now;
        }

        public override ModuleEffect Clone()
        {
            return new ForgeBurnRateEffect
            {
                ticksPerSecond = this.ticksPerSecond,
                ticksPerSecondPerLevel = this.ticksPerSecondPerLevel
            };
        }
    }
}
