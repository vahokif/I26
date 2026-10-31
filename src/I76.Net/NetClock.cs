using System.Diagnostics;
using System.Globalization;

namespace I76.Net;

/// <summary>
/// The network game's clock: the wall clock, slowed by <c>I76_NET_TIME_SCALE</c> (a capture hook, 1 without it). The server's
/// ticks, the clients' view of them and the bots all run on it, so a capture that can only render, say, 15 real frames a
/// second records a game slowed to half speed at 30 game frames a second (<c>I76_SHOT_FPS=30 I76_SHOT_MAX_FPS=15</c>,
/// the scale 0.5, in the game and in <c>i26-server --bots</c> alike) and plays back at the right speed.
/// </summary>
public static class NetClock
{
    public static readonly double Scale =
        double.TryParse(Environment.GetEnvironmentVariable("I76_NET_TIME_SCALE"), NumberStyles.Float, CultureInfo.InvariantCulture, out double s) && s > 0 ? s : 1;

    /// <summary>A stopwatch's time on this clock, in seconds.</summary>
    public static double Seconds(Stopwatch sw) => sw.Elapsed.TotalSeconds * Scale;
}
