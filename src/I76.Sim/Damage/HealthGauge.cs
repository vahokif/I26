using I76.Sim.Vehicles;

namespace I76.Sim.Damage;

/// <summary>What the target box's health bar shows (the original reads the health percentage), as a 0–1 fraction.</summary>
public static class HealthGauge
{
    /// <summary>
    /// The health percentage by object type: a helicopter's (9) and an emplacement's (8) one pool, hp·(1/max); a car's
    /// VehicleDamage.GaugeFraction. A helicopter is asked first: the player's carries a VehicleDamage for its specials alone,
    /// whose zones no hit touches (the helicopter's damage), so it read whole all the way down (#38).
    /// </summary>
    public static float Fraction(Vehicle v) =>
        v.Heli is { } h ? h.HealthPercent / 100f
        : v.Emplacement is { } g ? (float)g.Hp / System.Math.Max(1, g.MaxHp)
        : v.Health?.GaugeFraction() ?? 1f;
}
