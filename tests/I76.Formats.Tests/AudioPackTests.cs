using System.IO.Compression;
using System.Text;
using I76.Formats.Audio;
using Xunit;

namespace I76.Formats.Tests;

/// <summary>The HiFi Audio pack's layout: pack.json, sounds/NAME.ogg, movies/NAME.ogg and the per-game folders, read from a zip or a folder of synthetic streams.</summary>
public class AudioPackTests
{
    private static string Temp() => Directory.CreateTempSubdirectory("sndpack").FullName;

    /// <summary>An Ogg page: the header fields the reader looks at, and the body as one packet.</summary>
    private static byte[] Page(long granule, byte[] body)
    {
        var p = new List<byte>();
        p.AddRange("OggS"u8.ToArray());
        p.Add(0); p.Add(0);
        p.AddRange(BitConverter.GetBytes(granule));
        p.AddRange(new byte[12]);   // serial, sequence, CRC
        var lacing = new List<byte>();
        int left = body.Length;
        do { lacing.Add((byte)Math.Min(left, 255)); left -= 255; } while (left >= 0);
        p.Add((byte)lacing.Count);
        p.AddRange(lacing);
        p.AddRange(body);
        return [.. p];
    }

    /// <summary>A stand-in Ogg Vorbis stream of a synthetic tone's shape: the identification header and a last page holding its length.</summary>
    private static byte[] Ogg(int channels, int rate, long samples, int tag = 0)
    {
        var id = new byte[30];
        id[0] = 1;
        "vorbis"u8.CopyTo(id.AsSpan(1));
        id[11] = (byte)channels;
        BitConverter.GetBytes(rate).CopyTo(id, 12);
        var audio = new byte[64];
        for (int i = 0; i < audio.Length; i++) audio[i] = (byte)(tag + (int)(100 * Math.Sin(i / 3.0)));
        return [.. Page(0, id), .. Page(-1, audio), .. Page(samples, audio)];
    }

    private static string Zip(string dir, string name, string version, params (string Path, byte[] Data)[] files)
    {
        var path = Path.Combine(dir, name);
        using var z = ZipFile.Open(path, ZipArchiveMode.Create);
        void Put(string n, byte[] d) { using var w = z.CreateEntry(n).Open(); w.Write(d); }
        Put("pack.json", Encoding.UTF8.GetBytes($"{{\"name\":\"Test Sounds\",\"version\":\"{version}\",\"engine\":\"1.0\",\"count\":{files.Length}}}"));
        foreach (var (p, d) in files) Put(p, d);
        return path;
    }

    [Theory]
    [InlineData("aps2.wav", "aps2")]
    [InlineData("APS2.WAV", "aps2")]
    [InlineData("gdsgc40", "gdsgc40")]
    [InlineData("eishp.gpw", "eishp")]
    [InlineData("sound\\Wmiss.wav", "wmiss")]
    [InlineData("a/b/c.d.wav", "c.d")]
    public void NamesAreTheGamesWithoutFolderOrExtension(string asked, string key) => Assert.Equal(key, AudioPack.Key(asked));

    [Fact]
    public void ShellRecordsAreNamedByTheirBaseNumber() => Assert.Equal("db82", AudioPack.ShellName(82));

    [Fact]
    public void ZipLooksUpPerGameFirstThenCommonThenNothing()
    {
        var dir = Temp();
        var zip = Zip(dir, "i76-hifi-audio.zip", "1.0.0",
            ("sounds/aps2.ogg", Ogg(1, 48000, 100, 1)),
            ("sounds/nitro/aps2.ogg", Ogg(1, 48000, 100, 2)),
            ("sounds/i76/gdsgc40.ogg", Ogg(1, 48000, 200, 3)),
            ("sounds/Cammo.OGG", Ogg(2, 48000, 300, 4)),
            ("sounds/readme.txt", "not a sound"u8.ToArray()),
            ("other/wmiss.ogg", Ogg(1, 48000, 1)));
        using var p = AudioPack.FromZip(zip);
        Assert.Equal("Test Sounds 1.0.0", p.Info!.ToString());
        Assert.Equal("1.0", p.Info.Engine);
        Assert.Equal(4, p.Count);
        Assert.Equal("sounds/aps2.ogg", p.Entry("aps2.wav", nitro: false));
        Assert.Equal("sounds/nitro/aps2.ogg", p.Entry("APS2.WAV", nitro: true));
        Assert.Equal(Ogg(1, 48000, 100, 2), p.Read("aps2.wav", nitro: true));
        Assert.Equal("sounds/i76/gdsgc40.ogg", p.Entry("gdsgc40", nitro: false));
        Assert.Null(p.Entry("gdsgc40", nitro: true));       // the other game's own is not shared
        Assert.Equal("sounds/cammo.ogg", p.Entry("cammo.wav", nitro: true));
        Assert.Null(p.Entry("wmiss.wav", nitro: false));     // outside sounds/: the original plays
        Assert.Null(p.Read("readme", nitro: false));
    }

    [Fact]
    public void FolderReadsTheSameLayoutInAnyCase()
    {
        var dir = Temp();
        Directory.CreateDirectory(Path.Combine(dir, "Sounds", "NITRO"));
        File.WriteAllBytes(Path.Combine(dir, "Sounds", "Eishp.ogg"), Ogg(1, 48000, 10));
        File.WriteAllBytes(Path.Combine(dir, "Sounds", "NITRO", "eishp.ogg"), Ogg(1, 48000, 20));
        using var p = AudioPack.FromFolder(dir);
        Assert.Null(p.Info);                                  // no pack.json: a development folder
        Assert.Equal(2, p.Count);
        Assert.Equal(10, OggVorbisInfo.Read(p.Read("eishp.wav", nitro: false))!.Value.Samples);
        Assert.Equal(20, OggVorbisInfo.Read(p.Read("eishp.wav", nitro: true))!.Value.Samples);
    }

    [Fact]
    public void NewestPackWinsAndBrokenOnesAreSkipped()
    {
        var dir = Temp();
        Zip(dir, "i76-hd-sounds-1.2.0.zip", "1.2.0", ("sounds/a.ogg", Ogg(1, 48000, 1)));
        Zip(dir, "i76-hifi-audio-1.10.0.zip", "1.10.0", ("sounds/a.ogg", Ogg(1, 48000, 2)));
        Zip(dir, "i76-hd-sounds-empty.zip", "9.0.0");
        Zip(dir, "i76-hd-menus-9.0.0.zip", "9.0.0", ("sounds/a.ogg", Ogg(1, 48000, 3)));   // another kind of pack
        File.WriteAllText(Path.Combine(dir, "i76-hifi-audio-bad.zip"), "not a zip");
        var warnings = new List<string>();
        using var p = AudioPackFinder.Newest([dir, Path.Combine(dir, "nope")], warnings.Add);
        Assert.Equal("1.10.0", p!.Info!.Version);
        Assert.Equal(2, warnings.Count);
        Assert.Null(AudioPackFinder.Newest([Path.Combine(dir, "nope")]));
    }

    /// <summary>The pack's name is i76-hifi-audio*.zip; the first release's i76-hd-sounds*.zip is still read, the newer version winning across both.</summary>
    [Fact]
    public void BothPackNamesAreReadAndTheNewestWins()
    {
        var dir = Temp();
        Zip(dir, "i76-hd-sounds-1.0.0.zip", "1.0.0", ("sounds/a.ogg", Ogg(1, 48000, 1)));
        using (var old = AudioPackFinder.Newest([dir])) Assert.Equal("1.0.0", old!.Info!.Version);
        Zip(dir, "i76-hifi-audio-1.1.0.zip", "1.1.0", ("sounds/a.ogg", Ogg(1, 48000, 2)), ("movies/int08f01.ogg", Ogg(2, 48000, 3)));
        using (var p = AudioPackFinder.Newest([dir])) { Assert.Equal("1.1.0", p!.Info!.Version); Assert.Equal(1, p.MovieCount); }
        // the same version under both names: the new name
        Zip(dir, "i76-hd-sounds-1.1.0.zip", "1.1.0", ("sounds/a.ogg", Ogg(1, 48000, 4)));
        using (var p = AudioPackFinder.Newest([dir])) Assert.EndsWith("i76-hifi-audio-1.1.0.zip", p!.Origin);
        // a pack of movie tracks only is a pack
        var movies = Temp();
        Zip(movies, "i76-hifi-audio-2.0.0.zip", "2.0.0", ("movies/credf01.ogg", Ogg(1, 48000, 5)));
        using (var p = AudioPackFinder.Newest([movies])) Assert.Equal(1, p!.Count);
    }

    /// <summary>movies/NAME.ogg replaces the movie NAME.smk's sound (any case, any folder); movies/i76/ and movies/nitro/ win in their game; else nothing (its own sound plays).</summary>
    [Fact]
    public void MovieTracksLookUpPerGameFirstThenCommonThenNothing()
    {
        var dir = Temp();
        var zip = Zip(dir, "i76-hifi-audio-1.1.0.zip", "1.1.0",
            ("movies/int08f01.ogg", Ogg(2, 48000, 835109, 1)),
            ("movies/credf01.ogg", Ogg(2, 48000, 100, 2)),
            ("movies/nitro/credf01.ogg", Ogg(2, 48000, 200, 3)),
            ("movies/nitro/introf01.ogg", Ogg(2, 48000, 300, 4)),
            ("Movies/I76/IntroF01.OGG", Ogg(2, 48000, 400, 5)),
            ("sounds/int02f01.ogg", Ogg(1, 48000, 1)));           // a sound of that name is not a movie's
        using var p = AudioPack.FromZip(zip);
        Assert.Equal(6, p.Count);
        Assert.Equal(5, p.MovieCount);
        Assert.Equal("movies/int08f01.ogg", p.MovieEntry("INT08F01.SMK", nitro: false));
        Assert.Equal("movies/int08f01.ogg", p.MovieEntry("/games/i76/smk/Int08f01.smk", nitro: true));
        Assert.Equal(835109, OggVorbisInfo.Read(p.ReadMovie("int08f01.smk", nitro: false))!.Value.Samples);
        Assert.Equal("movies/credf01.ogg", p.MovieEntry("CREDF01.SMK", nitro: false));
        Assert.Equal("movies/nitro/credf01.ogg", p.MovieEntry("CREDF01.SMK", nitro: true));
        Assert.Equal("movies/i76/introf01.ogg", p.MovieEntry("INTROF01.SMK", nitro: false));
        Assert.Equal("movies/nitro/introf01.ogg", p.MovieEntry("INTROF01.SMK", nitro: true));
        Assert.Null(p.MovieEntry("INT02F01.SMK", nitro: false));       // not in movies/: its own sound
        Assert.Null(p.ReadMovie("OUT01F01.SMK", nitro: false));
        Assert.Null(p.Entry("int08f01", nitro: false));                 // a movie's track is not a sound
        Assert.Null(p.MovieEntry("", nitro: false));

        var folder = Temp();
        Directory.CreateDirectory(Path.Combine(folder, "MOVIES", "nitro"));
        File.WriteAllBytes(Path.Combine(folder, "MOVIES", "Out12F01.ogg"), Ogg(1, 48000, 10));
        File.WriteAllBytes(Path.Combine(folder, "MOVIES", "nitro", "credf01.ogg"), Ogg(1, 48000, 20));
        using var f = AudioPack.FromFolder(folder);
        Assert.Equal(2, f.MovieCount);
        Assert.Equal("movies/out12f01.ogg", f.MovieEntry("OUT12F01.SMK", nitro: false));
        Assert.Null(f.MovieEntry("CREDF01.SMK", nitro: false));
        Assert.Equal(20, OggVorbisInfo.Read(f.ReadMovie("CREDF01.SMK", nitro: true))!.Value.Samples);
    }

    /// <summary>A movie's track at 48 kHz is its own track's length, rounded to the sample: INT08F01's 383628 samples at 22050 Hz are 835109.</summary>
    [Theory]
    [InlineData(383628, 22050, 835109)]      // INT08F01.SMK
    [InlineData(5438456, 22050, 11838816)]   // INTROF01.SMK
    [InlineData(5732427, 22050, 12478753)]   // the Nitro Pack's INTROF01.SMK (16-bit stereo)
    [InlineData(48000, 48000, 48000)]
    public void AMoviesTrackIsItsOwnLengthAt48kHz(long own, int rate, long expected)
    {
        long n = AudioPack.MovieSamples(own, rate);
        Assert.Equal(expected, n);
        Assert.InRange(n / 48000.0 - own / (double)rate, -0.5 / 48000, 0.5 / 48000);
        Assert.Equal(0, AudioPack.MovieSamples(100, 0));
    }

    [Fact]
    public void PackJsonFieldsAndBadOnes()
    {
        var i = AudioPackInfo.Parse("{\"name\":\"X\",\"version\":\"0.1.0\",\"engine\":\"1.0\",\"count\":780}"u8.ToArray())!;
        Assert.Equal(("X", "0.1.0", "1.0", 780), (i.Name, i.Version, i.Engine, i.Count));
        Assert.Equal(-1, AudioPackInfo.Parse("{\"version\":\"1\"}"u8.ToArray())!.Count);
        Assert.Null(AudioPackInfo.Parse("nope"u8.ToArray()));
        Assert.Null(AudioPackInfo.Parse("[1]"u8.ToArray()));
        Assert.True(AudioPack.Compare("1.2", AudioPack.EngineVersion) > 0);
        Assert.Equal(0, AudioPack.Compare("1.1", AudioPack.EngineVersion));   // movies/: 1.1
    }

    [Fact]
    public void StreamLengthIsTheLastPagesGranule()
    {
        // aps2.wav: 11025 Hz, 14464 frames; sample-exact at 48 kHz is round(14464 * 48000 / 11025) = 62973
        long n = (long)Math.Round(14464 * 48000.0 / 11025);
        var o = OggVorbisInfo.Read(Ogg(1, 48000, n))!.Value;
        Assert.Equal((1, 48000, 62973L), (o.Channels, o.Rate, o.Samples));
        Assert.False(AudioPack.LengthDiffers(14464 / 11025.0, o.Seconds));
        Assert.Equal(2, OggVorbisInfo.Read(Ogg(2, 44100, 5))!.Value.Channels);    // stereo, another rate: accepted
        Assert.Null(OggVorbisInfo.Read("RIFF....WAVEfmt "u8.ToArray()));
        Assert.Null(OggVorbisInfo.Read(new byte[100]));
    }

    [Fact]
    public void ALengthOffByMoreThanAMillisecondIsReported()
    {
        Assert.False(AudioPack.LengthDiffers(1.0, 1.0009));
        Assert.True(AudioPack.LengthDiffers(1.0, 1.0011));
        Assert.True(AudioPack.LengthDiffers(1.0, 0.9));
    }
}
