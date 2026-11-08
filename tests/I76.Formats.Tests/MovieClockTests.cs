using I76.Formats.Audio;
using I76.Formats.Video;
using Xunit;

namespace I76.Formats.Tests;

/// <summary>
/// HiFi Audio's movie tracks in sync (MovieClock): a synthetic tone track, a beep at the start of every 15th frame's time,
/// made at 48 kHz to the length AudioPack.MovieSamples gives for the movie's own track, played through a simulated output
/// that mixes in blocks, hears them a latency later and runs off the wall clock's rate; the picture follows the clock the
/// player steps once a display frame with the track's position as Godot reports it.
/// </summary>
public class MovieClockTests
{
    private const int Rate = AudioPack.MovieRate, Block = 512;
    private const double Fd = 6666 / 100000.0;     // the movies' frame time (15.002 fps)

    /// <summary>The display frames up to the movie's end; when the track's last sample is heard and when the movie ended (wall seconds).</summary>
    private sealed record Run(List<(double Wall, double Clock, double Truth, int Shown)> Ticks, double WallAtEnd, double OverAt)
    {
        public bool Over => !double.IsNaN(OverAt);
    }

    /// <summary>
    /// The player from its first frame (wall 0, the track's Play()) to the movie's end: display frames of 13–20 ms; the output
    /// mixes <see cref="Block"/> samples at a time, <paramref name="scale"/> times slower than the wall clock says, and a block is
    /// heard <paramref name="latency"/> after it is mixed. Truth is the track's sample being heard, in seconds.
    /// </summary>
    private static Run Play(int frames, long samples, double scale, double latency, bool track = true)
    {
        var rng = new Random(76);
        var clock = new MovieClock(Fd);
        double blockTime = Block / (double)Rate * scale;
        var ticks = new List<(double, double, double, int)> { (0, 0, -latency / scale, 0) };
        double t = 0, wallAtEnd = double.NaN, overAt = double.NaN;
        while (t < 1000 && (double.IsNaN(overAt) || (track && double.IsNaN(wallAtEnd))))
        {
            double dt = 0.013 + 0.007 * rng.NextDouble();
            t += dt;
            long mixes = (long)Math.Floor(t / blockTime) + 1;             // mix 0 at Play()
            bool playing = track && (mixes - 1) * Block < samples;
            double heard = Math.Min(mixes * Block, samples) / (double)Rate + (t - (mixes - 1) * blockTime) - latency;
            clock.Step(dt, playing ? heard : null);
            double truth = (t - latency) / scale;
            if (double.IsNaN(wallAtEnd) && truth * Rate >= samples) wallAtEnd = t;
            if (!double.IsNaN(overAt)) continue;
            if (clock.Frame >= frames) { overAt = t; continue; }
            ticks.Add((t, clock.Seconds, truth, clock.Frame));
        }
        return new Run(ticks, wallAtEnd, overAt);
    }

    /// <summary>INT08F01.SMK: 261 frames; its own track 383628 samples at 22050 Hz, so the tone track is 835109 samples at 48 kHz.</summary>
    [Theory]
    [InlineData(1.0, 0.03)]
    [InlineData(1.0005, 0.08)]     // an output running 500 ppm slow, a long latency
    [InlineData(0.9995, 0.0)]
    public void ToneTrackStartsWithTheFirstFrameAndEndsWithinAFrameOfTheOriginal(double scale, double latency)
    {
        const int frames = 261;
        long n = AudioPack.MovieSamples(383628, 22050);
        Assert.Equal(835109, n);
        // the tone: a 10 ms 1 kHz beep at the start of frames 0, 15, 30, ...
        var tone = new float[n];
        var beeps = new List<(long At, int Frame)>();
        for (int f = 0; f < frames; f += 15)
        {
            long at = (long)Math.Round(f * Fd * Rate);
            beeps.Add((at, f));
            for (int i = 0; i < Rate / 100 && at + i < n; i++) tone[at + i] = MathF.Sin(2 * MathF.PI * 1000 * i / Rate);
        }
        var run = Play(frames, n, scale, latency);
        Assert.True(run.Over);

        // frame 0 is up when the track's first sample is heard (it went up with Play(), before anything could be heard)
        var first = run.Ticks.First(k => k.Truth >= 0);
        Assert.Equal(0, run.Ticks[0].Shown);
        Assert.InRange(first.Shown, 0, 1);

        // all along, the picture's clock is the heard position, within one mixed block (the reported position's step)
        foreach (var k in run.Ticks.Where(k => k.Truth >= 0 && k.Truth * Rate < n))
            Assert.InRange(k.Clock - k.Truth, -0.002, Block / (double)Rate * scale + 0.002);

        // each beep is heard with its own frame on the screen (found in the tone itself: the first non-silent sample of each burst)
        for (int b = 0; b < beeps.Count; b++)
        {
            long onset = Array.FindIndex(tone, (int)beeps[b].At, s => s != 0) - 1;   // (a sine starts at 0: the burst's first sample)
            var at = run.Ticks.First(k => k.Truth * Rate >= onset);
            Assert.InRange(at.Shown, beeps[b].Frame - 1, beeps[b].Frame + 1);
        }

        // the track's end against the original's: the same to the sample, and the picture's to within a frame
        Assert.InRange(n / (double)Rate - 383628 / 22050.0, -0.5 / Rate, 0.5 / Rate);
        Assert.InRange(n / (double)Rate - frames * Fd, -Fd, Fd);
        // the movie ends (its last frame's time over) within a frame of the track's last sample being heard
        Assert.InRange(run.OverAt - run.WallAtEnd, -Fd, Fd);
    }

    /// <summary>
    /// The intro (INTROF01.SMK, 3700 frames, 246.6 s) through an output 0.2% slower than the wall clock: the frame's delta alone
    /// would end 0.49 s (7 frames) ahead of the sound; the clock following the track stays with it to the end.
    /// </summary>
    [Fact]
    public void ALongMovieDoesNotDriftFromItsTrack()
    {
        const int frames = 3700;
        long n = AudioPack.MovieSamples(5438456, 22050);
        var run = Play(frames, n, scale: 1.002, latency: 0.03);
        Assert.True(run.Over);
        var last = run.Ticks.Last(k => k.Truth * Rate < n);
        Assert.InRange(last.Clock - last.Truth, -0.002, 0.015);
        Assert.True(last.Wall - last.Truth > 7 * Fd, $"the wall clock alone: {(last.Wall - last.Truth) * 1000:0} ms");
    }

    [Fact]
    public void WithoutATrackThePictureRunsOnTheFramesDelta()
    {
        var c = new MovieClock(Fd);
        for (int i = 0; i < 60; i++) c.Step(1 / 60.0, null);
        Assert.Equal(1.0, c.Seconds, 9);
        Assert.Equal(15, c.Frame);
        var run = Play(30, 0, 1.0, 0.03, track: false);
        Assert.True(run.Over);
        Assert.InRange(run.Ticks[^1].Wall, 29 * Fd, 30 * Fd + 0.02);
    }

    /// <summary>An output that stops taking samples (no device, a stuck driver) does not hold the picture: after StallSeconds it runs on the delta.</summary>
    [Fact]
    public void AStuckOutputDoesNotHoldThePicture()
    {
        var c = new MovieClock(Fd);
        c.Step(0.01, 0.5);
        Assert.Equal(0.5, c.Seconds);
        for (double t = 0; t < MovieClock.StallSeconds; t += 0.01) c.Step(0.01, 0.5);
        double held = c.Seconds;
        Assert.InRange(held, 0.5, 0.52);
        for (int i = 0; i < 100; i++) c.Step(0.01, 0.5);
        Assert.InRange(c.Seconds, held + 0.9, held + 1.01);
        // the track going on again: the picture waits for it rather than going back
        c.Step(0.01, 1.0);
        Assert.InRange(c.Seconds, held + 0.9, held + 1.02);
    }
}
