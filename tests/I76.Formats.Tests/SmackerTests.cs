using I76.Formats.Video;
using Xunit;

namespace I76.Formats.Tests;

public class SmackerTests
{
    /// <summary>
    /// A whole briefing decodes: every frame's video bitstream ends inside its
    /// chunk with under 32 bits over (the padding to the 4-byte frame size),
    /// and the audio unpacks to the movie's length at 15 frames a second.
    /// </summary>
    [InstallFact]
    public void ABriefingDecodesEndToEnd()
    {
        var path = Directory.EnumerateFiles(TestInstall.Install.FindDir("smk")!).First(f => Path.GetFileName(f).Equals("INT08F01.SMK", StringComparison.OrdinalIgnoreCase));
        using var m = Smacker.Open(path);
        Assert.Equal((640, 480, 261), (m.Width, m.Height, m.FrameCount));
        Assert.Equal(1 / 15.0, m.FrameDuration, 3);
        Assert.True(m.HasAudio(0));
        Assert.Equal((22050, false, false), (m.SampleRate(0), m.Is16Bit(0), m.Stereo(0)));
        long audio = 0;
        while (m.NextFrame((t, pcm) => audio += pcm.Length)) Assert.InRange(m.BitsLeft, 0, 31);
        Assert.Equal(261, m.Position);
        Assert.Equal(383628, audio);
        Assert.InRange(audio / 22050.0, 261 / 15.0 - 0.1, 261 / 15.0 + 0.1);
    }

    private static string Movie(string name) =>
        Directory.EnumerateFiles(TestInstall.Install.FindDir("smk")!).First(f => Path.GetFileName(f).Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The two trip missions whose scripts play a movie part way (playMovie: T05 at Taurus's car, T06 at the gas
    /// station) are the ones a tester saw end in a black screen; their movies decode to the end like the briefings.
    /// </summary>
    [InstallTheory]
    [InlineData("ANG05F01.SMK", 478)]
    [InlineData("ANG06F01.SMK", 801)]
    public void TheScriptMoviesOfT05AndT06DecodeEndToEnd(string name, int frames)
    {
        using var m = Smacker.Open(Movie(name));
        Assert.Equal((640, 480, frames), (m.Width, m.Height, m.FrameCount));
        while (m.NextFrame((_, _) => { })) Assert.InRange(m.BitsLeft, 0, 31);
        Assert.Equal(frames, m.Position);
    }

    /// <summary>
    /// A damaged movie file (cut short, or bytes changed) makes the decoder throw, which ends its thread, rather than
    /// loop: the player then gives the movie up (MovieWatch) instead of holding the game under a black screen.
    /// </summary>
    [InstallFact]
    public void ADamagedMovieThrowsRatherThanHanging()
    {
        var whole = File.ReadAllBytes(Movie("ANG05F01.SMK"));
        var corrupt = (byte[])whole.Clone();
        var r = new Random(1);
        for (int i = 0; i < 200; i++) corrupt[r.Next(1000, corrupt.Length)] = (byte)r.Next(256);
        foreach (var bytes in new[] { whole[..(whole.Length * 3 / 10)], corrupt })
        {
            var path = Path.Combine(Path.GetTempPath(), $"i76-smk-{Guid.NewGuid():N}.smk");
            File.WriteAllBytes(path, bytes);
            try
            {
                var run = Task.Run(() =>
                {
                    using var m = Smacker.Open(path);
                    while (m.NextFrame((_, _) => { })) { }
                });
                Assert.True(run.Wait(TimeSpan.FromSeconds(60)) || run.IsFaulted, "the decoder is still going after a minute");
            }
            catch (AggregateException) { }      // the throw: what the player's decoder thread ends on
            finally { File.Delete(path); }
        }
    }
}
