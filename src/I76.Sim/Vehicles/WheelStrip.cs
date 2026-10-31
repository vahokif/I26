namespace I76.Sim.Vehicles;

/// <summary>
/// A wheel's texture strip. The engine never turns a wheel's mesh about its axle:
/// the road-wheel animation setup (the wheel class's post-load) registers the strip callback on the
/// first texture of each of the wheel's meshes (intact and blown), and that callback, every drawn frame, picks a
/// row of the .tmt by the wheel's speed (which the wheel callback sets to the car's signed speed in
/// the grounded step) and steps the frame within it on a timer. A tyre's rows are its hubcap turning
/// (WA11LS06..01, LF03..01, LV03..01); a tank's track (wetnk_1a.wdf) cycles WE11LS01..03 in every row.
/// </summary>
public sealed class WheelStrip
{
    /// <summary>State word 1: the step period, 0.05 s.</summary>
    public const float Period = 0.05f;
    /// <summary>State word 2: the frames in a row, 12 (hard-coded; every wheel .tmt is 3 rows of 12).</summary>
    public const int Count = 12;
    /// <summary>The speed bands (m/s: 0.1, 10 and 25 mph). At or under the first the strip holds still on row 0.</summary>
    public const float Still = 0.044704f, Mid = 4.4704f, Fast = 11.176f;

    private float _next;
    /// <summary>State word 3: the frame within the row.</summary>
    public int Frame { get; private set; }
    /// <summary>The row (axis 1): 0 under 10 mph, 1 to 25 mph, 2 over.</summary>
    public int Row { get; private set; }
    /// <summary>State word 5: 1 forward, 0 backward (the speed's sign).</summary>
    public bool Forward { get; private set; } = true;

    /// <summary>Installed at frame time <paramref name="now"/>: the first step is due 0.05 s later.</summary>
    public WheelStrip(float now) => _next = now + Period;

    /// <summary>The .tmt name the row and frame make: row × 12 + frame (the original sums axis 0 and axis 1 × the row length).</summary>
    public int Index => Row * Count + Frame;

    /// <summary>
    /// The name on the mesh: <see cref="Index"/> as last applied. A new row is only stored (the last
    /// argument 0) until the next step applies it, or the wheel stops; until the first, the mesh's own frame 0.
    /// </summary>
    public int Shown { get; private set; }

    /// <summary>
    /// At frame time <paramref name="now"/> (the frame clock) with the wheel's speed.
    /// One step at most per call: when the frame rate is under 20 the timer falls behind and the strip steps
    /// once a frame.
    /// </summary>
    public int Tick(float now, float speed)
    {
        Forward = speed >= 0;
        float s = MathF.Abs(speed);
        if (s <= Still) { Row = 0; return Shown = Index; }
        Row = s > Fast ? 2 : s > Mid ? 1 : 0;
        if (now <= _next) return Shown;
        _next += Period;
        if (!Forward) Frame = (Frame == 0 ? Count : Frame) - 1;
        else Frame = Frame < Count - 1 ? Frame + 1 : 0;
        return Shown = Index;
    }
}
