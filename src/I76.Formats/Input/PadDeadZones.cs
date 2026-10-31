namespace I76.Formats.Input;

/// <summary>
/// The dead zones of a game pad with a standard layout (docs/joystick.md): a worn stick rests off centre and would turn the camera or the
/// wheel by itself. Each stick has a radial zone (the stick's distance from centre, not each axis alone, so a drift in one direction does
/// not leave the other axis cut off), rescaled so that the response starts from nothing at the zone's edge and still reaches 1 at full
/// deflection; a trigger has a plain one. The steps are percent, 0 to 50 % in 5 %.
/// </summary>
public readonly record struct PadDeadZones(float Left, float Right, float Trigger)
{
    /// <summary>What a pad starts with (before this setting there was none, and the free look's own 0.12 only).</summary>
    public const float DefaultLeft = 0.15f, DefaultRight = 0.15f, DefaultTrigger = 0.05f;
    public const float Max = 0.5f, Step = 0.05f;

    public static readonly PadDeadZones Default = new(DefaultLeft, DefaultRight, DefaultTrigger);
    /// <summary>The config keys, in section "pad" of user://joystick.cfg.</summary>
    public const string Section = "pad", LeftKey = "left_dead", RightKey = "right_dead", TriggerKey = "trigger_dead";

    /// <summary>A zone snapped to the 5 % steps and held to 0..50 %.</summary>
    public static float Snap(float zone) => Math.Clamp(MathF.Round(zone / Step) * Step, 0f, Max);

    /// <summary>The next zone one step up or down; it stops at 0 and 50 % (<paramref name="wrap"/>: goes round, for a click).</summary>
    public static float Next(float zone, int dir, bool wrap = false)
    {
        int n = (int)MathF.Round(Snap(zone) / Step) + Math.Sign(dir), top = (int)MathF.Round(Max / Step);
        if (wrap) n = (n + top + 1) % (top + 1);
        return Math.Clamp(n, 0, top) * Step;
    }

    /// <summary>The settings from a store that may lack any key: a missing or unreadable one is that zone's default and the others are kept.</summary>
    public static PadDeadZones Load(Func<string, double?> get)
    {
        float V(string key, float def) => get(key) is { } d && double.IsFinite(d) ? Snap((float)d) : def;
        return new(V(LeftKey, DefaultLeft), V(RightKey, DefaultRight), V(TriggerKey, DefaultTrigger));
    }

    /// <summary>A stick with the radial dead zone cut out and the rest stretched back: inside it (0,0), at the rim of the stick's travel length 1, and the same direction.</summary>
    public static (float X, float Y) Stick(float x, float y, float zone)
    {
        float m = MathF.Sqrt(x * x + y * y);
        if (zone <= 0) return m > 1f ? (x / m, y / m) : (x, y);       // a corner of a square gate is held to the circle
        if (m <= zone) return (0, 0);
        float k = MathF.Min(1f, (m - zone) / (1f - zone)) / m;
        return (x * k, y * k);
    }

    /// <summary>A trigger (0..1) with the zone cut off and the rest stretched back to 1.</summary>
    public static float Trig(float t, float zone)
    {
        t = Math.Clamp(t, 0f, 1f);
        return zone <= 0 ? t : t <= zone ? 0 : Math.Min(1f, (t - zone) / (1f - zone));
    }

    /// <summary>The pad as the game sees it: sticks and triggers through their zones (a generic device has its own, per axis).</summary>
    public PadRaw Apply(in PadRaw p)
    {
        if (p.Generic) return p;
        var (lx, ly) = Stick(p.LeftX, p.LeftY, Left);
        var (rx, ry) = Stick(p.RightX, p.RightY, Right);
        return p with { LeftX = lx, LeftY = ly, RightX = rx, RightY = ry, TriggerLeft = Trig(p.TriggerLeft, Trigger), TriggerRight = Trig(p.TriggerRight, Trigger) };
    }
}
