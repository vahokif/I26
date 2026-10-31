namespace I76.Formats.Video;

/// <summary>What a playing movie does this frame (<see cref="MovieWatch.Step"/>).</summary>
public enum MovieStep { Playing, Over, NoFirstFrame, Stalled }

/// <summary>
/// When a movie played by a decoder thread is over, or has to be given up. The player shows the black under the
/// picture until the first frame comes and holds the game paused under it, so a decoder that dies before its first
/// frame, or stops making frames part way, would leave a black screen with nothing ending it (the player had to close
/// the game). A decoder that is merely slow keeps the movie going: only one with nothing queued and no new frame for
/// <see cref="StallSeconds"/> of wall-clock time is given up.
/// </summary>
public sealed class MovieWatch
{
    public const double StallSeconds = 5;
    private int _seen = -1;
    private double _progressAt;

    /// <param name="now">Wall-clock seconds (not the game's delta, which a capture steps faster than real time).</param>
    /// <param name="produced">Frames the decoder has made so far.</param>
    /// <param name="decoded">The decoder has finished (the last frame made, or it failed).</param>
    /// <param name="queued">Frames are waiting to be shown.</param>
    /// <param name="started">The first frame has been shown (the movie's clock runs).</param>
    /// <param name="want">The frame the clock has reached.</param>
    /// <param name="frameCount">The movie's frames.</param>
    public MovieStep Step(double now, int produced, bool decoded, bool queued, bool started, int want, int frameCount)
    {
        if (produced != _seen) { _seen = produced; _progressAt = now; }
        bool stalled = !queued && (decoded || now - _progressAt > StallSeconds);
        if (!started) return queued ? MovieStep.Playing : stalled ? MovieStep.NoFirstFrame : MovieStep.Playing;
        if (decoded && !queued && want >= frameCount) return MovieStep.Over;
        return stalled && !decoded ? MovieStep.Stalled : MovieStep.Playing;
    }
}
