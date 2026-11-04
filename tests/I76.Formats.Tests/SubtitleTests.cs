using I76.Formats.Subtitles;
using Xunit;

namespace I76.Formats.Tests;

public class SubtitleTests
{
    private const string SampleSrt = """
        1
        00:00:01,500 --> 00:00:04,200
        Taurus: Hey Groove, wake up.

        2
        00:00:05,000 --> 00:00:08,750
        [gdsgc69]
        Groove: Never seen driving like that before.
        """;

    private const string SampleJsonArray = """
        [
          { "start": 1.5, "end": 4.2, "text": "Taurus: Hey Groove, wake up." },
          { "start": 5.0, "end": 8.75, "text": "Groove: What's going on?", "key": "groove_01" }
        ]
        """;

    private const string SampleJsonObject = """
        {
          "tau01_01": "Taurus: Keep your eyes open, Groove.",
          "gdsgc69": "Groove: Never seen driving like that before."
        }
        """;

    [Fact]
    public void ParsesSrtCuesAndKeys()
    {
        var track = SubtitleTrack.ParseSrt(SampleSrt);
        Assert.Equal(2, track.Cues.Count);

        Assert.Equal(1.5, track.Cues[0].Start);
        Assert.Equal(4.2, track.Cues[0].End);
        Assert.Equal("Taurus: Hey Groove, wake up.", track.Cues[0].Text);

        Assert.Equal(5.0, track.Cues[1].Start);
        Assert.Equal(8.75, track.Cues[1].End);
        Assert.Equal("Groove: Never seen driving like that before.", track.Cues[1].Text);
        Assert.Equal("gdsgc69", track.Cues[1].Key);

        Assert.Equal("Taurus: Hey Groove, wake up.", track.AtTime(2.0));
        Assert.Equal("Groove: Never seen driving like that before.", track.AtTime(6.0));
        Assert.Null(track.AtTime(0.5));
        Assert.Null(track.AtTime(4.5));

        Assert.Equal("Groove: Never seen driving like that before.", track.BySound("gdsgc69.wav"));
    }

    [Fact]
    public void ParsesJsonArrayCues()
    {
        var track = SubtitleTrack.ParseJson(SampleJsonArray);
        Assert.Equal(2, track.Cues.Count);
        Assert.Equal("Taurus: Hey Groove, wake up.", track.AtTime(2.0));
        Assert.Equal("Groove: What's going on?", track.BySound("groove_01"));
    }

    [Fact]
    public void ParsesJsonObjectMap()
    {
        var track = SubtitleTrack.ParseJson(SampleJsonObject);
        Assert.Equal(2, track.Cues.Count);
        Assert.Equal("Taurus: Keep your eyes open, Groove.", track.BySound("tau01_01.wav"));
        Assert.Equal("Groove: Never seen driving like that before.", track.BySound("gdsgc69"));
    }

    [Fact]
    public void LoadsFromRegisteredSearchDirectory()
    {
        string tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "i76_sub_test_" + System.Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(tempDir);
        try
        {
            string srtPath = System.IO.Path.Combine(tempDir, "testmovie.srt");
            System.IO.File.WriteAllText(srtPath, SampleSrt);

            SubtitleLoader.RegisterSearchDirectory(tempDir);
            var loaded = SubtitleLoader.Load("testmovie");

            Assert.NotNull(loaded);
            Assert.Equal("Taurus: Hey Groove, wake up.", loaded.AtTime(2.0));
        }
        finally
        {
            if (System.IO.Directory.Exists(tempDir))
                System.IO.Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void LoadsDistCutscenesIfPresent()
    {
        string distSub = System.IO.Path.Combine("..", "..", "..", "..", "dist", "i26-0.11.0", "subtitles");
        if (!System.IO.Directory.Exists(distSub)) return;

        SubtitleLoader.RegisterSearchDirectory(distSub);
        var track = SubtitleLoader.Load("introf01");
        Assert.NotNull(track);
        Assert.NotEmpty(track.Cues);
        Assert.Contains("Stampede", track.AtTime(61.0) ?? "");

        // Verify Nitro resolution
        var nitroTrack = SubtitleLoader.Load("p01");
        if (System.IO.File.Exists(System.IO.Path.Combine(distSub, "nitro", "p01.json")))
        {
            Assert.NotNull(nitroTrack);
            Assert.NotEmpty(nitroTrack.Cues);
        }
    }

    [Fact]
    public void PagesLongCuesByElapsedTime()
    {
        string text = string.Join(" ", Enumerable.Range(0, 60).Select(i => "w" + i));       // about 290 characters
        string first = SubtitleTrack.Page(text, 0, 30);
        string last = SubtitleTrack.Page(text, 29.9, 30);
        Assert.True(first.Length <= SubtitleTrack.PageChars);
        Assert.True(last.Length <= SubtitleTrack.PageChars);
        Assert.NotEqual(first, last);
        Assert.Equal("short", SubtitleTrack.Page("short", 5, 1));
    }

    [Fact]
    public void PagesLongCuesPreservesSpeakerPrefixOnContinuationPages()
    {
        string text = "Stampede: " + string.Join(" ", Enumerable.Range(0, 40).Select(i => "word" + i));
        string first = SubtitleTrack.Page(text, 0, 20);
        string last = SubtitleTrack.Page(text, 19.9, 20);
        Assert.StartsWith("Stampede: ", first);
        Assert.StartsWith("Stampede: ", last);
        Assert.True(first.Length <= SubtitleTrack.PageChars);
        Assert.True(last.Length <= SubtitleTrack.PageChars);
    }

    [Fact]
    public void PagesLongCuesProportionalDisplayTiming()
    {
        string text = "Stampede: The wheels in the desert spin across the asphalt under the blazing sun. And the night falls quiet over the valley of the shadow of death.";
        string pageAtStart = SubtitleTrack.Page(text, 0, 10);
        string pageAtEnd = SubtitleTrack.Page(text, 9.8, 10);

        Assert.StartsWith("Stampede: ", pageAtStart);
        Assert.StartsWith("Stampede: ", pageAtEnd);
        Assert.NotEqual(pageAtStart, pageAtEnd);
        Assert.True(pageAtStart.Length <= SubtitleTrack.PageChars);
        Assert.True(pageAtEnd.Length <= SubtitleTrack.PageChars);
    }

    [Fact]
    public void PoemSpeakerIsTaurus()
    {
        var cb = new I76.Sim.Missions.CbRadio(_ => 10f, _ => false);
        var poetry = new I76.Sim.Missions.Poetry("taurus", 1);
        poetry.Request(cb, "player");
        Assert.Equal(3, cb.Queue.Count);
        Assert.StartsWith("POSTA", cb.Queue[1].Name);
        Assert.Equal("taurus", cb.Queue[1].Speaker);
    }

    [Fact]
    public void ResamplesPcmAudioTo16KhzMonoWav()
    {
        // 1 second of 11025Hz 8-bit mono audio
        byte[] pcm8Mono = new byte[11025];
        for (int i = 0; i < pcm8Mono.Length; i++) pcm8Mono[i] = (byte)(128 + 50 * Math.Sin(2 * Math.PI * 440 * i / 11025.0));

        using var wavStream = I76.Formats.Audio.PcmResampler.Resample(pcm8Mono, 11025, 8, 1);
        Assert.NotNull(wavStream);
        byte[] wavBytes = wavStream.ToArray();

        var pcm = I76.Formats.Audio.SoundFiles.Parse(wavBytes);
        Assert.NotNull(pcm);
        Assert.Equal(1, pcm.Channels);
        Assert.Equal(16000, pcm.Rate);
        Assert.Equal(16, pcm.Bits);
        Assert.InRange(pcm.Seconds, 0.99f, 1.01f);
    }

    [Fact]
    public void HasSubtitles_ReturnsTrueOnlyWhenSubtitlesExist()
    {
        string tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "i26_test_subs_" + System.Guid.NewGuid().ToString("N"));
        try
        {
            System.IO.Directory.CreateDirectory(tempDir);
            Assert.False(SubtitleLoader.HasSubtitles(tempDir, includeGlobal: false));

            string subDir = System.IO.Path.Combine(tempDir, "subtitles", "i76");
            System.IO.Directory.CreateDirectory(subDir);
            Assert.False(SubtitleLoader.HasSubtitles(tempDir, includeGlobal: false));

            string srtFile = System.IO.Path.Combine(subDir, "test.srt");
            System.IO.File.WriteAllText(srtFile, SampleSrt);
            Assert.True(SubtitleLoader.HasSubtitles(tempDir, includeGlobal: false));
        }
        finally
        {
            if (System.IO.Directory.Exists(tempDir))
                System.IO.Directory.Delete(tempDir, true);
        }
    }
}
