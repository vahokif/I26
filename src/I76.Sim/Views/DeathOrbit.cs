namespace I76.Sim.Views;

/// <summary>
/// The orbit round a dead car, and the network end of match's round the winner's. In the original both are one view:
/// it turns 8 degrees once per call, a frame, with no clock in it (the CD release, the GOG build and Nitro alike), and
/// its frame was its sim step. Here it turns 8 degrees per sim tick's worth of time, continuously on the display
/// frame's delta: 160 degrees a second at the default 20 Hz, 222 at 28, the original at that sim rate, smooth at any
/// display rate.
/// </summary>
public sealed class DeathOrbit
{
    /// <summary>Degrees per sim tick.</summary>
    public const float StepDegrees = 8f;

    private const float StepRadians = StepDegrees * (MathF.PI / 180f);

    /// <summary>The yaw, radians.</summary>
    public float Yaw;

    /// <summary>The turn rate, radians a second, at a sim tick of <paramref name="tick"/> seconds.</summary>
    public static float RadiansPerSecond(float tick) => StepRadians / tick;

    /// <summary>Starts the orbit at a yaw (radians).</summary>
    public void Start(float yaw) => Yaw = yaw;

    /// <summary>Turns by <paramref name="dt"/> seconds at the rate of a sim tick of <paramref name="tick"/> seconds.</summary>
    public void Advance(float dt, float tick) => Yaw += RadiansPerSecond(tick) * dt;
}
