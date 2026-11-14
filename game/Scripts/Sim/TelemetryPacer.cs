namespace I76.Game.Sim;

/// <summary>
/// How often the telemetry feed sends: at most <see cref="MaxRate"/> packets a second whatever the frame rate (with VSync off a
/// frame a packet was 200 to 400 a second; Live for Speed sends at most 100, and readers such as SimHub stall near 130). The
/// sends sit on a grid of 1/<see cref="MaxRate"/> s with a quarter of a slot's slack, so a 60 Hz display's frames all go.
/// </summary>
public sealed class TelemetryPacer
{
    public const double MaxRate = 60;
    private const double Interval = 1 / MaxRate, Slack = Interval / 4;
    private double _next = double.NegativeInfinity;

    /// <summary>Whether a packet goes at <paramref name="now"/> (seconds, never going back); if it does, the next slot is taken.</summary>
    public bool Due(double now)
    {
        if (now < _next - Slack) return false;
        _next = (_next < now - Interval ? now : _next) + Interval;
        return true;
    }
}
