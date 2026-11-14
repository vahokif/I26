namespace I76.Game.Sim;

/// <summary>
/// The mission's daylight for the modern look's own lights: the preset's sun intensity under the sun (1 by day, 0.625 at
/// dusk), 0 under the moon. Set with the mission's light (Main); what scales a light from its night strength to its day one.
/// </summary>
public static class Daylight
{
    /// <summary>The daylight a preset gives: its intensity under the sun, 0 under the moon.</summary>
    public static float Of(bool isDay, float intensity) => isDay ? Math.Clamp(intensity, 0f, 1f) : 0f;

    /// <summary>A light's share of its night strength at <paramref name="daylight"/>: 1 at night, <paramref name="dayFloor"/> in full sun.</summary>
    public static float Scale(float daylight, float dayFloor) => 1f - (1f - dayFloor) * Math.Clamp(daylight, 0f, 1f);

    /// <summary>The current mission's daylight, as last set.</summary>
    public static float Current { get; set; }
}
