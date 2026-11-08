using System.IO.Compression;
using System.Text.Json;

namespace I76.Formats.Audio;

/// <summary>What a sound pack's top-level pack.json says: <c>{"name", "version", "engine", "count"}</c>.</summary>
public sealed record SoundPackInfo(string Name, string Version, string Engine, int Count)
{
    public override string ToString() => $"{Name} {Version}";

    public static SoundPackInfo? Parse(byte[]? json)
    {
        if (json is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return null;
            string S(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            int count = r.TryGetProperty("count", out var c) && c.ValueKind == JsonValueKind.Number && c.TryGetInt32(out var n) ? n : -1;
            return new SoundPackInfo(S("name") is { Length: > 0 } name ? name : "Sound Pack", S("version"), S("engine"), count);
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
/// Replacement samples for the game's sounds, from a folder or a zip: <c>pack.json</c> at the root and
/// <c>sounds/NAME.ogg</c>, NAME being the sound's name as the game asks for it, lower case, without its folder or extension
/// ("aps2.wav": <c>sounds/aps2.ogg</c>; a CB line "gdsgc40": <c>sounds/gdsgc40.ogg</c>). <c>sounds/i76/NAME.ogg</c> and
/// <c>sounds/nitro/NAME.ogg</c> win over <c>sounds/NAME.ogg</c> in that game; the front end's are <see cref="ShellName"/>. Only the samples come from here: how a sound
/// plays (its header's loop mode, priority and voice limit, its pitch, its volume) stays the original file's.
/// </summary>
public sealed class SoundPack : IDisposable
{
    /// <summary>The pack format this build reads; a pack.json asking for a newer one is still read, with a warning.</summary>
    public const string EngineVersion = "1.0";
    public const string Root = "sounds/";
    /// <summary>A replacement this much longer or shorter than the original is still used, but reported.</summary>
    public const double LengthTolerance = 0.001;

    private readonly Dictionary<string, Func<byte[]>> _files = [];
    private readonly ZipArchive? _zip;
    private readonly object _lock = new();

    /// <summary>The folder or zip, for the log.</summary>
    public string Origin { get; }
    public SoundPackInfo? Info { get; private set; }
    /// <summary>How many .ogg files the pack holds (per-game ones included).</summary>
    public int Count => _files.Count;

    private SoundPack(string origin, ZipArchive? zip) { Origin = origin; _zip = zip; }

    /// <summary>A folder in the pack's layout; file names in any case.</summary>
    public static SoundPack FromFolder(string dir)
    {
        var json = Path.Combine(dir, "pack.json");
        var pack = new SoundPack(dir, null) { Info = File.Exists(json) ? SoundPackInfo.Parse(File.ReadAllBytes(json)) : null };
        var sounds = Path.Combine(dir, "sounds");
        if (!Directory.Exists(sounds))
            sounds = Directory.EnumerateDirectories(dir).FirstOrDefault(d => Path.GetFileName(d).Equals("sounds", StringComparison.OrdinalIgnoreCase)) ?? sounds;
        if (Directory.Exists(sounds))
            foreach (var f in Directory.EnumerateFiles(sounds, "*", SearchOption.AllDirectories))
                pack.Add(Path.GetRelativePath(sounds, f), () => File.ReadAllBytes(f));
        return pack;
    }

    /// <summary>A pack file, read in place.</summary>
    public static SoundPack FromZip(string path)
    {
        var zip = new ZipArchive(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read), ZipArchiveMode.Read);
        try
        {
            var pack = new SoundPack(path, zip);
            foreach (var e in zip.Entries)
            {
                var n = e.FullName.Replace('\\', '/').TrimStart('/');
                if (n.Equals("pack.json", StringComparison.OrdinalIgnoreCase)) pack.Info = SoundPackInfo.Parse(pack.Slurp(e));
                else if (n.StartsWith(Root, StringComparison.OrdinalIgnoreCase) && n.Length > Root.Length) { var entry = e; pack.Add(n[Root.Length..], () => pack.Slurp(entry)); }
            }
            return pack;
        }
        catch { zip.Dispose(); throw; }
    }

    private void Add(string rel, Func<byte[]> read)
    {
        rel = rel.Replace('\\', '/').ToLowerInvariant();
        if (rel.EndsWith(".ogg", StringComparison.Ordinal)) _files[rel] = read;
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

    /// <summary>A sound's name in the pack: lower case, without folder or extension ("SOUND\Aps2.WAV": "aps2").</summary>
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

    /// <summary>The pack file that replaces <paramref name="name"/> in that game ("nitro/aps2.ogg", "aps2.ogg"), or null.</summary>
    public string? Entry(string name, bool nitro)
    {
        var key = Key(name);
        if (key.Length == 0) return null;
        var own = (nitro ? "nitro/" : "i76/") + key + ".ogg";
        if (_files.ContainsKey(own)) return own;
        return _files.ContainsKey(key + ".ogg") ? key + ".ogg" : null;
    }

    /// <summary>The replacement's Ogg bytes, or null when the pack has none for it.</summary>
    public byte[]? Read(string name, bool nitro) => Entry(name, nitro) is { } e ? _files[e]() : null;

    /// <summary>Whether a replacement's length is off the original's by more than <see cref="LengthTolerance"/>.</summary>
    public static bool LengthDiffers(double originalSeconds, double replacementSeconds) => Math.Abs(originalSeconds - replacementSeconds) > LengthTolerance;

    /// <summary>Positive when <paramref name="a"/> is the newer version (dotted numbers, else ordinal).</summary>
    public static int Compare(string a, string b) =>
        Version.TryParse(a, out var x) && Version.TryParse(b, out var y) ? x.CompareTo(y) : string.CompareOrdinal(a, b);

    public void Dispose() => _zip?.Dispose();
}

public static class SoundPackFinder
{
    public const string Pattern = "i76-hd-sounds*.zip";

    /// <summary>Opens the newest readable pack among i76-hd-sounds*.zip in the folders, or null; unreadable ones are reported through <paramref name="warn"/>.</summary>
    public static SoundPack? Newest(IEnumerable<string> folders, Action<string>? warn = null)
    {
        SoundPack? best = null;
        foreach (var dir in folders.Where(Directory.Exists).Distinct())
            foreach (var f in Directory.EnumerateFiles(dir, Pattern).OrderBy(f => f, StringComparer.Ordinal))
            {
                SoundPack p;
                try { p = SoundPack.FromZip(f); }
                catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException) { warn?.Invoke($"{f}: {e.Message}"); continue; }
                if (p.Info is null || p.Count == 0) { warn?.Invoke($"{f}: no pack.json or no sounds/*.ogg"); p.Dispose(); continue; }
                if (best is null || SoundPack.Compare(p.Info.Version, best.Info!.Version) > 0) { best?.Dispose(); best = p; }
                else p.Dispose();
            }
        return best;
    }
}
