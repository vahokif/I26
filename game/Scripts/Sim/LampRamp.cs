namespace I76.Game.Sim;

/// <summary>
/// The modern look's incandescent lamps warm up and cool down instead of switching in one frame ([port] lamp_ramp, on by
/// default; I76_LAMP_RAMP=0|1 overrides it, I76_LAMP_RAMP_SCALE stretches the times for A/B). The original draws a lamp lit
/// or unlit from the frame its flag changes, and so does the 1997 look. A 1976 car's lamps are 12 V incandescent bulbs: a
/// tail or brake filament (a 21 W bulb) reaches about 90 % in roughly 50 to 150 milliseconds, a sealed-beam or H4
/// headlamp a little slower, and each dies away over roughly 100 to 250 milliseconds with a visible
/// orange tail as the filament cools: the colour temperature drops before the light is gone, so the lamp goes red-shifted
/// and dimmer. Each lamp's drawn level is an exponential approach to its switch with a time constant for the rise and a longer
/// one for the fall; below <see cref="CoolBelow"/> of full the colour is pulled toward <see cref="Cool"/> (a white lamp's
/// last glimmer is orange; a red lens passes red whatever the filament's colour, so the brake and tail lamps only dim).
/// Drawn only: the simulation's lamp flags and the brake rule are untouched, and a frame's step depends on its delta alone.
/// </summary>
public static class LampRamp
{
    /// <summary>The headlamps' time constants (ms): the rise, the fall.</summary>
    public const float HeadRiseMs = 60f, HeadFallMs = 150f;
    /// <summary>The brake and tail lamps' (ms).</summary>
    public const float RearRiseMs = 30f, RearFallMs = 100f;
    /// <summary>Below this share of full the colour cools toward <see cref="Cool"/>.</summary>
    public const float CoolBelow = 0.4f;
    /// <summary>The cooling filament's colour, the lamp's own at <see cref="CoolBelow"/> and above.</summary>
    public const float CoolR = 1f, CoolG = 0.35f, CoolB = 0.1f;
    /// <summary>Within this of its target a level snaps to it, so a lamp switched off is off (nothing drawn) rather than a glimmer forever.</summary>
    public const float Snap = 0.004f;

    /// <summary>[port] lamp_ramp (Main.ApplyGraphics); the hook wins over it.</summary>
    public static bool Setting = true;
    /// <summary>I76_LAMP_RAMP: 0 the original's one-frame switch, 1 the ramp, whatever the setting says.</summary>
    public static readonly int? Hook = int.TryParse(System.Environment.GetEnvironmentVariable("I76_LAMP_RAMP"), out int h) ? h : null;
    /// <summary>I76_LAMP_RAMP_SCALE: the time constants times this (3 makes the ramp plain to the eye for a comparison).</summary>
    public static readonly float Scale = float.TryParse(System.Environment.GetEnvironmentVariable("I76_LAMP_RAMP_SCALE"), System.Globalization.NumberStyles.Float,
        System.Globalization.CultureInfo.InvariantCulture, out float s) && s > 0 ? s : 1f;

    public static bool Enabled => Hook is { } h ? h != 0 : Setting;

    /// <summary>
    /// A lamp's level after a frame of <paramref name="dt"/> seconds toward <paramref name="target"/> (0 or 1): the exponential
    /// approach with <paramref name="riseMs"/> while the target is above the level, <paramref name="fallMs"/> while below. With
    /// the ramp off it is the target at once.
    /// </summary>
    public static float Step(float level, float target, float dt, float riseMs, float fallMs)
    {
        if (!Enabled || level == target) return target;
        float tau = (target > level ? riseMs : fallMs) * Scale * 0.001f;
        float next = level + (target - level) * (1f - MathF.Exp(-dt / tau));
        return MathF.Abs(next - target) < Snap ? target : next;
    }

    /// <summary>A headlamp's step.</summary>
    public static float Head(float level, float target, float dt) => Step(level, target, dt, HeadRiseMs, HeadFallMs);
    /// <summary>A brake or tail lamp's step.</summary>
    public static float Rear(float level, float target, float dt) => Step(level, target, dt, RearRiseMs, RearFallMs);

    /// <summary>
    /// The factor on a lamp's colour at a level: (1, 1, 1) from <see cref="CoolBelow"/> up, <see cref="Cool"/> at nothing, linear
    /// between; the lamp's colour times it is the filament's. The level itself scales the light separately.
    /// </summary>
    public static (float R, float G, float B) Warmth(float level)
    {
        float t = Math.Clamp(level / CoolBelow, 0f, 1f);
        return (CoolR + (1f - CoolR) * t, CoolG + (1f - CoolG) * t, CoolB + (1f - CoolB) * t);
    }
}
