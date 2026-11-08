namespace I76.Formats.Video;

/// <summary>
/// The time a playing movie's picture is at, from its first frame. With the movie's own sound (streamed as it is decoded)
/// the picture runs on the frame's delta, as it always has. With a HiFi Audio track in its place the picture follows that
/// track as it is heard (the player's position, plus the time since the last mix, less the output's latency), so the two
/// cannot drift apart however long the movie: frame n is up while the track's sample n·FrameDuration·rate is heard. The
/// clock never goes back. Before the track is heard, after its end, and if the output stops taking samples for
/// <see cref="StallSeconds"/> (no device, a stuck driver), it runs on the delta instead, so the movie always ends.
/// </summary>
public sealed class MovieClock(double frameDuration)
{
    public const double StallSeconds = 0.25;

    public double FrameDuration { get; } = frameDuration;
    /// <summary>Seconds since the first frame went up.</summary>
    public double Seconds { get; private set; }
    /// <summary>The frame the picture should show now.</summary>
    public int Frame => (int)(Seconds / FrameDuration);

    private double _lastHeard = double.NegativeInfinity, _still;

    /// <summary>
    /// One display frame: <paramref name="delta"/> the frame's time, <paramref name="heard"/> the replacement track's position as
    /// heard in seconds, or null when none plays (the movie's own sound, or the track is over).
    /// </summary>
    public void Step(double delta, double? heard)
    {
        if (heard is not { } h) { Seconds += delta; return; }
        if (h > _lastHeard) { _lastHeard = h; _still = 0; }
        else if ((_still += delta) > StallSeconds) { Seconds += delta; return; }
        Seconds = Math.Max(Seconds, h);
    }

    /// <summary>Holds the clock at a time (a capture's frozen frame).</summary>
    public void Set(double seconds) => Seconds = seconds;
}
