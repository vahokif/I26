namespace I76.Sim.Rng;

/// <summary>
/// Where a mission's <see cref="CrtRand"/> starts. The original seeds rand() from
/// the clock, <c>srand(time(NULL))</c>, at startup (the original's start-up) and
/// again at every mission's set-up and tear-down (and srand(0) once more
/// when it builds its gaussian table): each mission draws from a stream seeded by the second
/// it began, and nothing is carried from one mission to the next.
/// The port does the same unless a fixed seed is asked for (--seed N).
/// </summary>
public static class RandSeed
{
    /// <summary>srand(time(NULL)): the Unix time in seconds, as the CRT's 32-bit time_t.</summary>
    public static uint FromClock(Func<DateTimeOffset>? clock = null) =>
        unchecked((uint)(clock?.Invoke() ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds());

    /// <summary>The fixed seed if there is one, else the clock's.</summary>
    public static uint Pick(uint? fixedSeed, Func<DateTimeOffset>? clock = null) => fixedSeed ?? FromClock(clock);
}
