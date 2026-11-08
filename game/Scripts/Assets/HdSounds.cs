using Godot;
using I76.Formats.Audio;

namespace I76.Game.Assets;

/// <summary>
/// The one place replacement sound samples are found (<see cref="SoundPack"/> has the layout). None ships with the port;
/// sources in order: $I76_SND_DIR (a folder, for development), user://snd/ (a folder), then the newest i76-hd-sounds*.zip
/// next to the executable or in user://, read in place. Only the samples are replaced: <see cref="AudioBank"/> keeps the
/// original's header, length and every rule of how it plays. The "HD Sounds" setting (Audio Control) switches it off.
/// </summary>
public static class HdSounds
{
    private static SoundPack? _pack;
    private static bool _probed;
    private static readonly HashSet<string> _told = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The player's switch (ShellSettings.HdSounds): sounds started after a change follow it.</summary>
    public static bool Enabled { get; set; } = true;

    public static SoundPack? Pack { get { Probe(); return _pack; } }
    public static bool Installed => Pack is not null;
    /// <summary>"name version", or "folder"; null without a pack.</summary>
    public static string? Label => Pack is { } p ? (p.Info?.ToString() ?? "folder") : null;

    /// <summary>I76_SND_TRACE=1: a line for every sound the pack replaces, as it is first loaded.</summary>
    private static readonly bool Tracing = System.Environment.GetEnvironmentVariable("I76_SND_TRACE") == "1";

    /// <summary>Finds the pack once and logs what was found (Main calls it at start).</summary>
    public static void Probe()
    {
        if (_probed) return;
        _probed = true;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var env = System.Environment.GetEnvironmentVariable("I76_SND_DIR");
        var user = ProjectSettings.GlobalizePath("user://snd/");
        try
        {
            if (env is { Length: > 0 } && Directory.Exists(env)) _pack = SoundPack.FromFolder(env);
            else if (Directory.Exists(user) && Directory.EnumerateFileSystemEntries(user).Any()) _pack = SoundPack.FromFolder(user);
            else
            {
                var exe = I76.Formats.Installers.ExtractionPlaces.AppFolder(OS.GetExecutablePath());
                _pack = SoundPackFinder.Newest(new[] { exe, ProjectSettings.GlobalizePath("user://") }.OfType<string>(), w => GD.PushWarning($"sound pack: {w}"));
            }
        }
        catch (System.Exception e) when (e is IOException or System.UnauthorizedAccessException) { GD.PushWarning($"sound pack: {e.Message}"); _pack = null; }
        if (_pack is { Count: 0 }) { _pack.Dispose(); _pack = null; }
        if (_pack is null) { GD.Print("HD sounds: none (no I76_SND_DIR, user://snd/ or i76-hd-sounds*.zip)"); return; }
        var info = _pack.Info;
        GD.Print($"HD sounds: {_pack.Origin} ({(info is null ? "no pack.json" : $"{info}, engine {info.Engine}, count {info.Count}")}; {_pack.Count} files, {watch.ElapsedMilliseconds} ms)");
        if (info is { Engine.Length: > 0 } && SoundPack.Compare(info.Engine, SoundPack.EngineVersion) > 0)
            GD.PushWarning($"sound pack needs engine {info.Engine}, this is {SoundPack.EngineVersion}");
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
        byte[]? bytes;
        try { bytes = pack.Read(name, nitro); }
        catch (System.Exception e) when (e is IOException or InvalidDataException or System.UnauthorizedAccessException) { Tell(entry, $"sound pack: {entry} unreadable ({e.Message}), the original plays"); return null; }
        if (bytes is null || OggVorbisInfo.Read(bytes) is not { } ogg) { Tell(entry, $"sound pack: {entry} is not Ogg Vorbis, the original plays"); return null; }
        var s = AudioStreamOggVorbis.LoadFromBuffer(bytes);
        if (s is null) { Tell(entry, $"sound pack: {entry} does not decode, the original plays"); return null; }
        // the whole stream loops, from its first sample: Godot's Ogg looping has no gap at the seam
        s.Loop = loop;
        s.LoopOffset = 0;
        if (originalSeconds is { } o && SoundPack.LengthDiffers(o, ogg.Seconds))
            Tell(entry, $"sound pack: {entry} lasts {ogg.Seconds * 1000:0.#} ms, the original {o * 1000:0.#} ms (used anyway)");
        _loaded++; _bytes += bytes.Length; _ticks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
        if (Tracing) GD.Print($"HD sound: {name} <- {entry} ({ogg.Channels} ch, {ogg.Rate} Hz, {ogg.Seconds:0.000} s{(loop ? ", loop" : "")}; {_loaded} loaded, {_bytes / 1024} KB, {System.Diagnostics.Stopwatch.GetElapsedTime(0, _ticks).TotalMilliseconds:0.0} ms)");
        return s;
    }

    private static int _loaded;
    private static long _bytes, _ticks;

    private static void Tell(string entry, string line) { if (_told.Add(entry)) GD.PushWarning(line); }
}
