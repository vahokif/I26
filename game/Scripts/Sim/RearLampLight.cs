namespace I76.Game.Sim;

/// <summary>
/// The modern look's rear lamps' red light on the ground behind the car, at night: a spotlight at each lamp, looking back and
/// down (CarNode). The tail filament throws a faint glow a couple of metres; the brake's, several times as bright, a plain red
/// pool. Small beside the headlamps' beam: the pool lies a metre or two from the lamp, where the headlamps' light is spread
/// over 50 m of road. A night's light only: none in full sun, turned down with the preset's sun intensity at dusk.
/// </summary>
public static class RearLampLight
{
    /// <summary>The light's energy from the tail filament and from the brake's, at night.</summary>
    public const float TailEnergy = 0.06f, BrakeEnergy = 0.8f;
    /// <summary>What is left in full sun: nothing.</summary>
    public const float DayFloor = 0f;

    /// <summary>
    /// The light's energy at the tail and brake filaments' levels (0 to 1, LampRamp's): the tail's gives way to the brake's as
    /// it rises, as a dual-filament bulb's does, turned down by <paramref name="daylight"/> (Daylight) to nothing in full sun.
    /// </summary>
    public static float Energy(float tail, float brake, float daylight) =>
        (TailEnergy * tail * (1f - brake) + BrakeEnergy * brake) * Daylight.Scale(daylight, DayFloor);
}
