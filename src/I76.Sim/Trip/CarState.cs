using I76.Sim.Damage;
using I76.Sim.Vehicles;

namespace I76.Sim.Trip;

/// <summary>
/// What carries the player's damage from one trip mission to the next (the
/// vsf's VCST, WLST and WPST): engine, brakes and
/// suspension HP and the six wheels in file units, and each weapon's HP and
/// rounds by hardpoint. Armour, chassis and (from the shell, −1) ammo are not
/// carried: the car starts each mission with the garage's armour and full
/// guns.
/// </summary>
public sealed record CarState(int EngineHp, int BrakeHp, int SuspensionHp, int[] Wheels, List<(int Hardpoint, int Hp, int Ammo)> Weapons)
{
    /// <summary>The player's divisor that undoes the post-load rescale for components: 4.</summary>
    private const int Rescale = 4;

    /// <summary>Enumeration of the player: components ÷ 4, wheels ÷ 4 (0 when absent), weapons as they are.</summary>
    public static CarState Capture(Vehicle v)
    {
        var d = v.Health;
        int Hp(Part? p) => p is null ? 0 : p.Hp / Rescale;
        var wheels = new int[6];
        for (int k = 0; k < 6; k++) wheels[k] = d?.Slots[k] is WheelPart w ? w.Hp / Rescale : 0;
        var weapons = (v.Armory?.LoadOrder ?? []).Select(w => ((int)w.Hardpoint.Id, w.Hp, w.Ammo)).ToList();
        return new CarState(Hp(d?.Engine), Hp(d?.Brake), Hp(d?.Suspension), wheels, weapons);
    }

    /// <summary>
    /// The engine's VCST/WLST/WPST handlers at load (trip games
    /// only), before the player's ×4: the current HP of engine, brakes,
    /// suspension and each wheel (the maxima stay the car's own), each
    /// weapon's HP by hardpoint and its rounds unless −1. The post-load comes after these, so carried rounds get the
    /// Heated Seats bonus too (the shell writes −1, so a trip mission starts with the loaded rounds and that bonus).
    /// </summary>
    public void Apply(Vehicle v)
    {
        if (v.Health is not { } d) return;
        void Set(Part? p, int hp) { if (p is not null && hp >= 0) p.Hp = System.Math.Min(hp * Rescale, p.Max); }
        Set(d.Engine, EngineHp);
        Set(d.Brake, BrakeHp);
        Set(d.Suspension, SuspensionHp);
        for (int k = 0; k < 6; k++) if (d.Slots[k] is WheelPart w) Set(w, Wheels[k]);
        foreach (var (hp, wHp, ammo) in Weapons)
            if (v.Armory?.LoadOrder.FirstOrDefault(w => w.Hardpoint.Id == hp) is { } weapon)
            {
                weapon.Hp = System.Math.Min(wHp, weapon.MaxHp);
                if (ammo != -1) weapon.Ammo = Specials.HeatedSeatsAmmo(ammo, v.Build?.Specials ?? []);
            }
    }
}
