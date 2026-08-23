namespace WeaponForge
{
    // ProjectileWeapon.FireSingle calls Shoot() TWICE on a lobbed shot.
    //
    // Read the method and it is right there, but it is easy to miss because
    // the first call is tucked inside the UsePhysics branch:
    //
    //     if (this.UsePhysics)
    //     {
    //         physicsProjectile = Instantiate(...);
    //         physicsProjectile.HomingData = this.HomingData;
    //         ...
    //         projectile.Shoot();          // <-- FIRST, and nothing is set yet
    //     }
    //     else { ... }
    //
    //     projectile.Damage        = base.Damage;
    //     projectile.RangeData     = this.RangeData;
    //     projectile.LifetimeData  = this.LifetimeData;
    //     projectile.Velocity      = direction * speed;
    //     projectile.Owner         = base.Owner;
    //     ...
    //     projectile.Shoot();              // <-- SECOND, fully configured
    //
    // So a Shoot hook on PhysicsProjectile runs once against a BLANK
    // projectile - Damage 0, Velocity zero, no RangeData, no Owner - and then
    // again against the real one. A plain Projectile never does this; only the
    // physics branch has the early call.
    //
    // That is not a cosmetic double-fire. Anything that CAPTURES state at
    // Shoot latches the blank values:
    //   - ForgeDamageStack.Capture(Damage.amount) records 0, and Capture is
    //     deliberately once-only, so every later multiplier resolves to 0 and
    //     the shot does NO DAMAGE AT ALL.
    //   - "borrow the weapon's range" reads RangeData.enabled == false and
    //     silently falls back to a default span.
    //
    // Hence this guard, in one place so every physics Shoot hook agrees.
    public static class ForgePhysicsShot
    {
        // True while the instance is still the blank one from the first call.
        //
        // Tested on two fields that FireSingle assigns BETWEEN the two calls,
        // and required together rather than either alone: a lobbed weapon with
        // projectileSpeed 0 (a dropped mine) is legitimate and would trip the
        // velocity test on its own, so the null Owner has to agree before we
        // skip anything. Erring toward running is the safe direction - a
        // feature that runs twice is a bug, but a feature that never runs is a
        // feature that does not exist.
        public static bool NotReady(PhysicsProjectile p)
        {
            return p == null ||
                   (p.Owner == null && p.Velocity.sqrMagnitude <= 0.000001f);
        }
    }
}
