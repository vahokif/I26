namespace I76.Sim.Session;

/// <summary>
/// Modern Physics' handling as a function of the Simulation Rate (docs/modern-physics.md "Rate"). The original's
/// model is frame-coupled: at a higher rate its keys ramp faster, it answers the keys sooner, it accelerates and brakes
/// differently and its drifts are tighter (docs/frame-rate.md §4–5). The Jolt body steps at 120 Hz whatever the rate.
/// Some of its handling follows from the tick by rule (the key ramp in <see cref="KeyRamp"/>, the yaw's answer, the
/// ground's hold, and the per-tick speed gains, which the body takes from <see cref="Tick"/>); the drive and the brake
/// are fitted to the original's measured acceleration and stopping distance at each rate.
/// </summary>
public sealed record ModernRateTuning(int Rate, float DriveScale, float BrakeScale)
{
    /// <summary>The time the body's yaw takes to answer (s): the original samples the keys once a tick, so a key press
    /// waits half a tick on average before the car answers; the body's own sampling (half a 120 Hz step) is part of it.</summary>
    public static float YawResponseFor(float tick) => System.MathF.Max(tick / 2 - 1f / 240f, 0.004f);

    /// <summary>The speed away from the ground past which the car flies: the original lets go when the ground falls 0.5
    /// below its path within a tick.</summary>
    public static float StickBreakFor(float tick) => 0.5f / tick;

    /// <summary>
    /// The fitted values, one row per offered rate (<see cref="SimRate.Offered"/>). DriveScale: the original's engine
    /// force scaled to reach 26 u/s in the original's time at that rate (4.37 s at 20 Hz … 4.63 s at 60, a quadratic in
    /// the rate). BrakeScale: the brake scaled to stop from 25 u/s in the original's distance (31.2 m at 20 Hz … 27.3 m
    /// at 60, linear in the rate). Both from the Piranha on m05; the 24 row is interpolated.
    /// </summary>
    public static readonly ModernRateTuning[] Table =
    [
        new(20, 0.968f, 0.805f),
        new(24, 0.963f, 0.811f),   // interpolated between the 20 and 25 rows on the targets' trends (4.386 s, 30.8 u), not fitted on the body
        new(25, 0.962f, 0.813f),
        new(28, 0.957f, 0.817f),
        new(30, 0.954f, 0.821f),
        new(40, 0.939f, 0.838f),
        new(60, 0.900f, 0.877f),
    ];

    /// <summary>The row for this rate (offered rates only: anything else gets the default rate's).</summary>
    public static ModernRateTuning For(SimRate rate) => Table.FirstOrDefault(r => r.Rate == rate.Label) ?? For(SimRate.Default);

    /// <summary>The original's tick at this rate.</summary>
    public float Tick => SimRate.FromLabel(Rate).TickSeconds;
    public float YawResponse => YawResponseFor(Tick);
    public float StickBreak => StickBreakFor(Tick);
}
