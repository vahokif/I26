using I76.Formats.Video;
using Xunit;

namespace I76.Formats.Tests;

/// <summary>
/// The movie player's watch on its decoder (MovieWatch). Tester: "Mission 5 and 6 ... after completing, end in total
/// black screen, I have to force quit": T05 and T06 are the trip missions whose scripts play a movie part way, with the
/// game paused under the player's black until the first frame comes. A decoder that never delivers must end the movie.
/// </summary>
public class MovieWatchTests
{
    private const int Frames = 478;   // ANG05F01.SMK

    [Fact]
    public void ADecoderThatDiesBeforeItsFirstFrameEndsTheMovieAtOnce()
    {
        var w = new MovieWatch();
        Assert.Equal(MovieStep.NoFirstFrame, w.Step(0, produced: 0, decoded: true, queued: false, started: false, want: 0, Frames));
    }

    [Fact]
    public void ADecoderThatNeverDeliversEndsTheMovieAfterTheStallTime()
    {
        var w = new MovieWatch();
        for (double t = 0; t <= MovieWatch.StallSeconds; t += 0.5)
            Assert.Equal(MovieStep.Playing, w.Step(t, 0, false, false, false, 0, Frames));
        Assert.Equal(MovieStep.NoFirstFrame, w.Step(MovieWatch.StallSeconds + 0.6, 0, false, false, false, 0, Frames));
    }

    [Fact]
    public void ADecoderStuckPartWayEndsTheMovieAfterTheStallTime()
    {
        var w = new MovieWatch();
        Assert.Equal(MovieStep.Playing, w.Step(0, 100, false, false, true, 99, Frames));
        Assert.Equal(MovieStep.Playing, w.Step(MovieWatch.StallSeconds - 0.1, 100, false, false, true, 160, Frames));
        Assert.Equal(MovieStep.Stalled, w.Step(MovieWatch.StallSeconds + 0.1, 100, false, false, true, 170, Frames));
    }

    [Fact]
    public void ASlowDecoderKeepsTheMovieGoing()
    {
        // a frame every four seconds, and the picture far behind the clock: slow, not stuck
        var w = new MovieWatch();
        for (int k = 1; k < 40; k++)
            Assert.Equal(MovieStep.Playing, w.Step(k * 4.0, k, false, false, true, k * 60, Frames));
        // with frames waiting to be shown (the decoder ahead, or the picture held) nothing is given up however long
        Assert.Equal(MovieStep.Playing, new MovieWatch().Step(1000, 20, false, true, true, 0, Frames));
    }

    [Fact]
    public void AMovieEndsWhenTheClockReachesItsLastFrame()
    {
        var w = new MovieWatch();
        Assert.Equal(MovieStep.Playing, w.Step(0, Frames, true, false, true, Frames - 1, Frames));
        Assert.Equal(MovieStep.Over, w.Step(0.1, Frames, true, false, true, Frames, Frames));
    }
}
