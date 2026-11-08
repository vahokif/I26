using System.IO.Compression;
using System.Text.Json;

namespace I76.Formats.Audio;

/// <summary>What a HiFi Audio pack's top-level pack.json says: <c>{"name", "version", "engine", "count"}</c>.</summary>
public sealed record AudioPackInfo(string Name, string Version, string Engine, int Count)
{
    public override string ToString() => $"{Name} {Version}";

    public static AudioPackInfo? Parse(byte[]? json)
    {
        if (json is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return null;
            string S(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            int count = r.TryGetProperty("count", out var c) && c.ValueKind == JsonValueKind.Number && c.TryGetInt32(out var n) ? n : -1;
            return new AudioPackInfo(S("name") is { Length: > 0 } name ? name : "HiFi Audio", S("version"), S("engine"), count);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException) { return null; }
    }
}

/// <summary>An Ogg Vorbis stream's channels, sample rate and length in samples (the last page's granule position).</summary>
public readonly record struct OggVorbisInfo(int Channels, int Rate, long Samples)
{
    public double Seconds => Rate > 0 ? Samples / (double)Rate : 0;

    /// <summary>Reads the identification header and the last page; null for anything that is not an Ogg Vorbis stream.</summary>
    public static OggVorbisInfo? Read(ReadOnlySpan<byte> d)
    {
        // the first page carries the identification packet: 0x01 "vorbis", version, channels, rate
        if (d.Length < 27 + 30 || !d[..4].SequenceEqual("OggS"u8)) return null;
        int segs = d[26], body = 27 + segs;
        if (body + 16 > d.Length || d[body] != 1 || !d.Slice(body + 1, 6).SequenceEqual("vorbis"u8)) return null;
        int channels = d[body + 11];
        int rate = BitConverter.ToInt32(d.Slice(body + 12, 4));
        // the last page's granule position is the stream's length in samples
        long samples = -1;
        for (int p = d.Length - 27; p >= 0; p--)
        {
            if (d[p] != 'O' || !d.Slice(p, 4).SequenceEqual("OggS"u8)) continue;
            samples = BitConverter.ToInt64(d.Slice(p + 6, 8));
            if (samples >= 0) break;
        }
        return channels is < 1 or > 2 || rate <= 0 || samples < 0 ? null : new OggVorbisInfo(channels, rate, samples);
    }
}

/// <summary>
/// HiFi Audio: replacement audio for the game's sounds and movies, from a folder or a zip, all Ogg Vorbis, mono or stereo:
/// <list type="bullet">
/// <item><c>pack.json</c> at the root (<see cref="AudioPackInfo"/>).</item>
/// <item><c>sounds/NAME.ogg</c>: a sound's samples. NAME is the sound's name as the game asks for it, lower case, without its
/// folder or extension ("aps2.wav": <c>sounds/aps2.ogg</c>; a CB line "gdsgc40": <c>sounds/gdsgc40.ogg</c>; the front end's
/// database sounds are <see cref="ShellName"/>). Only the samples come from here: how a sound plays (its header's loop mode,
/// priority and voice limit, its pitch, its volume) stays the original file's. The original's length; a different one is
/// used and reported.</item>
/// <item><c>movies/NAME.ogg</c>: a Smacker movie's whole sound track. NAME is the movie's file name as the game asks for it,
/// lower case, without extension ("INT08F01.SMK": <c>movies/int08f01.ogg</c>). Every movie the games ship has one audio
/// track, track 0 (22050 Hz 8-bit mono; 16-bit stereo in the Nitro Pack's intro), and that is all the game plays: the
/// replacement takes its place, its first sample with the movie's first frame. 48 kHz and <see cref="MovieSamples"/> long
/// (the original track's length at that rate, which is the picture's to within a frame); another length is used and
/// reported, another rate played as it is.</item>
/// </list>
/// <c>sounds/i76/</c>, <c>sounds/nitro/</c>, <c>movies/i76/</c> and <c>movies/nitro/</c> win over the shared folder in that
/// game (both games ship a CREDF01.SMK and an INTROF01.SMK, different movies).
/// </summary>
public sealed class AudioPack : IDisposable
{
    /// <summary>The pack format this build reads (1.1: movies/); a pack.json asking for a newer one is still read, with a warning.</summary>
    public const string EngineVersion = "1.1";
    public const string Sounds = "sounds/", Movies = "movies/";
    /// <summary>A replacement this much longer or shorter than the original is still used, but reported.</summary>
    public const double LengthTolerance = 0.001;
    /// <summary>The rate a movie's replacement track is made at.</summary>
    public const int MovieRate = 48000;

    /// <summary>Every .ogg under sounds/ and movies/, by its path in the pack in lower case ("sounds/nitro/aps2.ogg").</summary>
    private readonly Dictionary<string, Func<byte[]>> _files = [];
    private readonly ZipArchive? _zip;
    private readonly object _lock = new();

    /// <summary>The folder or zip, for the log.</summary>
    public string Origin { get; }
    public AudioPackInfo? Info { get; private set; }
    /// <summary>How many .ogg files the pack holds (sounds and movies, per-game ones included).</summary>
    public int Count => _files.Count;
    /// <summary>How many of them are movie tracks.</summary>
    public int MovieCount => _files.Keys.Count(k => k.StartsWith(Movies, StringComparison.Ordinal));

    private AudioPack(string origin, ZipArchive? zip) { Origin = origin; _zip = zip; }

    /// <summary>A folder in the pack's layout; file names in any case.</summary>
    public static AudioPack FromFolder(string dir)
    {
        var json = Path.Combine(dir, "pack.json");
        var pack = new AudioPack(dir, null) { Info = File.Exists(json) ? AudioPackInfo.Parse(File.ReadAllBytes(json)) : null };
        foreach (var root in new[] { Sounds, Movies })
        {
            var name = root.TrimEnd('/');
            var sub = Path.Combine(dir, name);
            if (!Directory.Exists(sub))
                sub = Directory.EnumerateDirectories(dir).FirstOrDefault(d => Path.GetFileName(d).Equals(name, StringComparison.OrdinalIgnoreCase)) ?? sub;
            if (Directory.Exists(sub))
                foreach (var f in Directory.EnumerateFiles(sub, "*", SearchOption.AllDirectories))
                    pack.Add(root + Path.GetRelativePath(sub, f), () => File.ReadAllBytes(f));
        }
        return pack;
    }

    /// <summary>A pack file, read in place.</summary>
    public static AudioPack FromZip(string path)
    {
        var zip = new ZipArchive(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read), ZipArchiveMode.Read);
        try
        {
            var pack = new AudioPack(path, zip);
            foreach (var e in zip.Entries)
            {
                var n = e.FullName.Replace('\\', '/').TrimStart('/');
                var entry = e;
                if (n.Equals("pack.json", StringComparison.OrdinalIgnoreCase)) pack.Info = AudioPackInfo.Parse(pack.Slurp(e));
                else pack.Add(n, () => pack.Slurp(entry));
            }
            return pack;
        }
        catch { zip.Dispose(); throw; }
    }

    private void Add(string rel, Func<byte[]> read)
    {
        rel = rel.Replace('\\', '/').ToLowerInvariant();
        bool under = (rel.StartsWith(Sounds, StringComparison.Ordinal) && rel.Length > Sounds.Length) || (rel.StartsWith(Movies, StringComparison.Ordinal) && rel.Length > Movies.Length);
        if (under && rel.EndsWith(".ogg", StringComparison.Ordinal)) _files[rel] = read;
    }

    private byte[] Slurp(ZipArchiveEntry e)
    {
        lock (_lock)
        {
            using var s = e.Open();
            using var ms = new MemoryStream((int)Math.Min(e.Length, int.MaxValue));
            s.CopyTo(ms);
            return ms.ToArray();
        }
    }

    /// <summary>A sound's or movie's name in the pack: lower case, without folder or extension ("SOUND\Aps2.WAV": "aps2").</summary>
    public static string Key(string name)
    {
        var n = name.Replace('\\', '/');
        n = n[(n.LastIndexOf('/') + 1)..];
        int dot = n.LastIndexOf('.');
        if (dot > 0) n = n[..dot];
        return n.ToLowerInvariant();
    }

    /// <summary>
    /// The front end's sounds have no file names: they are records of the shell database, named here by the base game's
    /// record number ("db82", the click), the same in the Nitro Pack, whose database renumbers them.
    /// </summary>
    public static string ShellName(int record) => $"db{record}";

    private string? Find(string root, string name, bool nitro)
    {
        var key = Key(name);
        if (key.Length == 0) return null;
        var own = root + (nitro ? "nitro/" : "i76/") + key + ".ogg";
        if (_files.ContainsKey(own)) return own;
        return _files.ContainsKey(root + key + ".ogg") ? root + key + ".ogg" : null;
    }

    /// <summary>The pack file that replaces sound <paramref name="name"/> in that game ("sounds/nitro/aps2.ogg", "sounds/aps2.ogg"), or null.</summary>
    public string? Entry(string name, bool nitro) => Find(Sounds, name, nitro);

    /// <summary>The pack file that replaces movie <paramref name="name"/>'s sound track in that game ("movies/i76/credf01.ogg", "movies/int08f01.ogg"), or null.</summary>
    public string? MovieEntry(string name, bool nitro) => Find(Movies, name, nitro);

    /// <summary>A file's Ogg bytes by its <see cref="Entry"/> or <see cref="MovieEntry"/>, or null when the pack has none.</summary>
    public byte[]? ReadEntry(string? entry) => entry is not null && _files.TryGetValue(entry, out var read) ? read() : null;

    /// <summary>The replacement sound's Ogg bytes, or null when the pack has none for it.</summary>
    public byte[]? Read(string name, bool nitro) => ReadEntry(Entry(name, nitro));

    /// <summary>The replacement movie track's Ogg bytes, or null when the pack has none for it.</summary>
    public byte[]? ReadMovie(string name, bool nitro) => ReadEntry(MovieEntry(name, nitro));

    /// <summary>Whether a replacement's length is off the original's by more than <see cref="LengthTolerance"/>.</summary>
    public static bool LengthDiffers(double originalSeconds, double replacementSeconds) => Math.Abs(originalSeconds - replacementSeconds) > LengthTolerance;

    /// <summary>A movie's replacement track length in samples at <see cref="MovieRate"/>: the original track's, to the nearest sample.</summary>
    public static long MovieSamples(long originalSamples, int originalRate) =>
        originalRate <= 0 ? 0 : (long)Math.Round(originalSamples * (double)MovieRate / originalRate, MidpointRounding.AwayFromZero);

    /// <summary>Positive when <paramref name="a"/> is the newer version (dotted numbers, else ordinal).</summary>
    public static int Compare(string a, string b) =>
        Version.TryParse(a, out var x) && Version.TryParse(b, out var y) ? x.CompareTo(y) : string.CompareOrdinal(a, b);

    public void Dispose() => _zip?.Dispose();
}

public static class AudioPackFinder
{
    /// <summary>The pack's file names: i76-hifi-audio*.zip, and the first release's i76-hd-sounds*.zip, still read.</summary>
    public static readonly string[] Patterns = ["i76-hifi-audio*.zip", "i76-hd-sounds*.zip"];

    /// <summary>Opens the newest readable pack among the <see cref="Patterns"/> in the folders, or null; unreadable ones are reported through <paramref name="warn"/>.</summary>
    public static AudioPack? Newest(IEnumerable<string> folders, Action<string>? warn = null)
    {
        AudioPack? best = null;
        foreach (var dir in folders.Where(Directory.Exists).Distinct())
            foreach (var pattern in Patterns)
                foreach (var f in Directory.EnumerateFiles(dir, pattern).OrderBy(f => f, StringComparer.Ordinal))
                {
                    AudioPack p;
                    try { p = AudioPack.FromZip(f); }
                    catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException) { warn?.Invoke($"{f}: {e.Message}"); continue; }
                    if (p.Info is null || p.Count == 0) { warn?.Invoke($"{f}: no pack.json, or no sounds/*.ogg or movies/*.ogg"); p.Dispose(); continue; }
                    // a tie goes to the first found: the newer file name, then the folder beside the executable
                    if (best is null || AudioPack.Compare(p.Info.Version, best.Info!.Version) > 0) { best?.Dispose(); best = p; }
                    else p.Dispose();
                }
        return best;
    }
}
