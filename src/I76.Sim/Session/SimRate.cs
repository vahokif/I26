namespace I76.Sim.Session;

/// <summary>
/// The simulation's tick, as a setting (docs/frame-rate.md). The original stepped once per rendered frame, so
/// its frame rate was its simulation rate; the port's tick at 1/V plays the original at V fps. The ticks sit on
/// the original's own k/2048 s clock grid, and never at exactly 0.05 s, where the original's weapon loop and
/// substep rule both change (the 20 Hz label is 102/2048 s). 28 keeps the reference machine's 0.036 s.
/// </summary>
public readonly record struct SimRate(int Label, float TickSeconds)
{
    public static readonly SimRate R20 = new(20, 102f / 2048f);
    public static readonly SimRate R25 = new(25, 82f / 2048f);
    public static readonly SimRate R28 = new(28, 0.036f);
    public static readonly SimRate R30 = new(30, 68f / 2048f);
    public static readonly SimRate R40 = new(40, 51f / 2048f);
    public static readonly SimRate R60 = new(60, 34f / 2048f);
    public static readonly SimRate[] Offered = [R20, R25, R28, R30, R40, R60];
    public static readonly SimRate Default = R20;

    public static SimRate FromLabel(int hz) => Offered.FirstOrDefault(r => r.Label == hz, Default);
    public static bool IsOffered(int hz) => Offered.Any(r => r.Label == hz);

    /// <summary>Ticks covering at least <paramref name="seconds"/>.</summary>
    public int TicksFor(float seconds) => (int)MathF.Ceiling(seconds / TickSeconds - 1e-4f);

    /// <summary>
    /// The world's frame delta on tick <paramref name="tick"/> (the AI, contacts and vehicle steps; weapons and
    /// scripts keep the tick). The original's clock counts 1/2048 s, so at V fps a frame lasts 2048/V quanta,
    /// read as the whole numbers either side of it. At 20 fps that is 102.4: 3 frames in 5 of 102 (49.805 ms) and
    /// 2 in 5 of 103 (50.293 ms), as measured on the reference clock (fps-drive-50 288:225, T09 at 50 ms 417:279).
    /// The 103s are at or over 0.05 s, so the original stepped its vehicles twice in them: this
    /// rate's frames straddle the substep edge, and a constant 102 would never take the second step. So at a rate
    /// whose tick is the low side of a straddling pair the world gets the clock's sequence. The other rates stay
    /// on one side of the edge and keep the tick.
    /// </summary>
    public float FrameDelta(int tick)
    {
        double quanta = 2048.0 / Label;
        long low = (long)System.Math.Floor(quanta);
        if (TickSeconds * 2048f != low || Vehicles.VehicleStep.Substeps(low / 2048f).Count == Vehicles.VehicleStep.Substeps((low + 1) / 2048f).Count)
            return TickSeconds;
        long q = (long)System.Math.Floor((tick + 1) * quanta) - (long)System.Math.Floor(tick * quanta);
        return q / 2048f;
    }
}
