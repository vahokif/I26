using Godot;
using I76.Formats.Audio;

namespace I76.Game.Assets;

/// <summary>
/// The one place HiFi Audio (replacement sound samples and movie sound tracks) is found (<see cref="AudioPack"/> has the
/// layout). None ships with the port; sources in order: $I76_SND_DIR (a folder, for development), user://snd/ (a folder),
/// then the newest i76-hifi-audio*.zip (or the first release's i76-hd-sounds*.zip) next to the executable or in user://,
/// read in place. Only the samples are replaced: <see cref="AudioBank"/> keeps the original's header, length and every
/// rule of how it plays, and <see cref="I76.Game.Ui.MoviePlayer"/> the movie's picture and timing. The "HiFi Audio"
/// setting (Audio Control, in the front end and over a mission) switches it off.
/// </summary>
public static class HiFiAudio
{
    private static AudioPack? _pack;
    private static volatile bool _probed;
    private static readonly HashSet<string> _told = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The player's switch (ShellSettings.HiFiAudio): sounds and movies started after a change follow it.</summary>
    public static bool Enabled { get; set; } = true;

    public static AudioPack? Pack { get { Probe(); return _pack; } }
    public static bool Installed => Pack is not null;
    /// <summary>"name version", or "folder"; null without a pack.</summary>
    public static string? Label => Pack is { } p ? (p.Info?.ToString() ?? "folder") : null;

    /// <summary>I76_SND_TRACE=1: a line for every sound and movie track the pack replaces, as it is first loaded.</summary>
    private static readonly bool Tracing = System.Environment.GetEnvironmentVariable("I76_SND_TRACE") == "1";

    /// <summary>Finds the pack once and logs what was found (Main calls it at start).</summary>
    /// <summary>The pack is read from the main thread (a movie's track) and a mission's build thread (its sounds, under a briefing) at once.</summary>
    private static readonly object Gate = new();

    public static void Probe()
    {
        if (_probed) return;
        lock (Gate)
        {
            if (_probed) return;
            ProbeOnce();
            _probed = true;
        }
    }

    private static void ProbeOnce()
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var env = System.Environment.GetEnvironmentVariable("I76_SND_DIR");
        var user = ProjectSettings.GlobalizePath("user://snd/");
        try
        {
            if (env is { Length: > 0 } && Directory.Exists(env)) _pack = AudioPack.FromFolder(env);
            else if (Directory.Exists(user) && Directory.EnumerateFileSystemEntries(user).Any()) _pack = AudioPack.FromFolder(user);
            else
            {
                var exe = I76.Formats.Installers.ExtractionPlaces.AppFolder(OS.GetExecutablePath());
                _pack = AudioPackFinder.Newest(new[] { exe, ProjectSettings.GlobalizePath("user://") }.OfType<string>(), w => GD.PushWarning($"HiFi Audio: {w}"));
            }
        }
        catch (System.Exception e) when (e is IOException or System.UnauthorizedAccessException) { GD.PushWarning($"HiFi Audio: {e.Message}"); _pack = null; }
        if (_pack is { Count: 0 }) { _pack.Dispose(); _pack = null; }
        if (_pack is null) { GD.Print("HiFi Audio: none (no I76_SND_DIR, user://snd/ or i76-hifi-audio*.zip)"); return; }
        var info = _pack.Info;
        GD.Print($"HiFi Audio: {_pack.Origin} ({(info is null ? "no pack.json" : $"{info}, engine {info.Engine}, count {info.Count}")}; {_pack.Count} files, {_pack.MovieCount} of them movies, {watch.ElapsedMilliseconds} ms)");
        if (info is { Engine.Length: > 0 } && AudioPack.Compare(info.Engine, AudioPack.EngineVersion) > 0)
            GD.PushWarning($"HiFi Audio: the pack needs engine {info.Engine}, this is {AudioPack.EngineVersion}");
    }

    /// <summary>
    /// The replacement for <paramref name="name"/> in that game, looped whole when <paramref name="loop"/>, or null (switched off,
    /// no pack, not in it, or unreadable: the original plays). <paramref name="originalSeconds"/> is the original's length: a
    /// replacement of another length is used all the same and reported once.
    /// </summary>
    public static AudioStream? Load(string name, bool nitro, bool loop, double? originalSeconds)
    {
        if (!Enabled || Pack is not { } pack || pack.Entry(name, nitro) is not { } entry) return null;
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        if (Open(pack, entry) is not { } opened) return null;
        var (s, ogg, size) = opened;
        // the whole stream loops, from its first sample: Godot's Ogg looping has no gap at the seam
        s.Loop = loop;
        s.LoopOffset = 0;
        if (originalSeconds is { } o && AudioPack.LengthDiffers(o, ogg.Seconds))
            Tell(entry, $"HiFi Audio: {entry} lasts {ogg.Seconds * 1000:0.#} ms, the original {o * 1000:0.#} ms (used anyway)");
        _loaded++; _bytes += size; _ticks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
        if (Tracing) GD.Print($"HiFi Audio: {name} <- {entry} ({ogg.Channels} ch, {ogg.Rate} Hz, {ogg.Seconds:0.000} s{(loop ? ", loop" : "")}; {_loaded} loaded, {_bytes / 1024} KB, {System.Diagnostics.Stopwatch.GetElapsedTime(0, _ticks).TotalMilliseconds:0.0} ms)");
        return s;
    }

    /// <summary>
    /// The replacement sound track for movie <paramref name="file"/> (its file name) in that game, with its length, or null
    /// (switched off, no pack, not in it, or unreadable: the movie's own sound plays). <paramref name="originalSeconds"/> is the
    /// movie's own track's length: a replacement of another length is used all the same and reported.
    /// </summary>
    public static (AudioStreamOggVorbis Stream, OggVorbisInfo Info, string Entry)? LoadMovie(string file, bool nitro, double originalSeconds)
    {
        if (!Enabled || Pack is not { } pack || pack.MovieEntry(file, nitro) is not { } entry) return null;
        if (Open(pack, entry) is not { } opened) return null;
        var (s, ogg, _) = opened;
        s.Loop = false;
        if (AudioPack.LengthDiffers(originalSeconds, ogg.Seconds))
            GD.PushWarning($"HiFi Audio: {entry} lasts {ogg.Seconds * 1000:0.#} ms, the movie's own track {originalSeconds * 1000:0.#} ms (used anyway)");
        return (s, ogg, entry);
    }

    private static (AudioStreamOggVorbis, OggVorbisInfo, int)? Open(AudioPack pack, string entry)
    {
        byte[]? bytes;
        try { lock (Gate) bytes = pack.ReadEntry(entry); }
        catch (System.Exception e) when (e is IOException or InvalidDataException or System.UnauthorizedAccessException) { Tell(entry, $"HiFi Audio: {entry} unreadable ({e.Message}), the original plays"); return null; }
        if (bytes is null || OggVorbisInfo.Read(bytes) is not { } ogg) { Tell(entry, $"HiFi Audio: {entry} is not Ogg Vorbis, the original plays"); return null; }
        var s = AudioStreamOggVorbis.LoadFromBuffer(bytes);
        if (s is null) { Tell(entry, $"HiFi Audio: {entry} does not decode, the original plays"); return null; }
        return (s, ogg, bytes.Length);
    }

    private static int _loaded;
    private static long _bytes, _ticks;

    private static void Tell(string entry, string line) { lock (Gate) if (!_told.Add(entry)) return; GD.PushWarning(line); }
}
