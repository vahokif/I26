using I76.Sim.Session;

namespace I76.Game.Sim;

/// <summary>
/// The sim tick as the game layer's frame clock. The original's presentation rules ran once a frame, and its
/// frame was its sim step (docs/frame-rate.md), so the 1997 look's per-frame effects (smoke puffs, contrail
/// segments, the flame frame cycle, the ammo digits, the sky scroll) count sim ticks, not render frames.
/// <see cref="SimDriver"/> publishes its session; a render frame asks how many ticks ran since it last looked.
/// </summary>
public static class SimClock
{
    /// <summary>The running session's rate (the default before one starts).</summary>
    public static SimRate Rate { get; set; } = SimRate.Default;
    public static float Tick => Rate.TickSeconds;
    /// <summary>The sim ticks run so far (the mission's).</summary>
    public static Func<int>? Ticks { get; set; }
    public static int Now => Ticks?.Invoke() ?? 0;

    /// <summary>The 1997 sky's scroll, per second: 1/(1001 - 2^-7) of the cloud sheet a frame, at the tick's frame rate.</summary>
    public static float SkyScrollPerSecond => 1f / Tick / (1001f - 1f / 128f);

    /// <summary>Ticks run since <paramref name="last"/> (which it updates); a stall or a restart counts at most <paramref name="max"/>.</summary>
    public static int Take(ref int last, int max = 4)
    {
        int now = Now;
        int n = now - last;
        last = now;
        return n < 0 ? 0 : Math.Min(n, max);
    }
}
