using Godot;
using I76.Formats.Vfs;

namespace I76.Game.Editor;

/// <summary>
/// The soundtrack while editing: the map's game's CD tracks (the install the editor was opened from), a random one after another, never
/// the one just played, on the Music bus at the Audio Control's Music level. Nothing plays at a level below 2, with no tracks, or when the
/// editor's MUSIC button (shell.cfg [port] editor_music) is off. It belongs to the editor scene: a playtest fades it out and its mission's
/// own music takes over, and the editor starts it again when it comes back; leaving the editor ends it for the menu's.
/// </summary>
public sealed partial class EditorMusic : Node
{
    public required GameInstall Install { get; init; }

    private static readonly Random Rand = new();
    private static int _lastTrack = -1;      // the disc track just played, across the editor's scene reloads
    private static bool _pausedForPlaytest;

    private readonly List<(string Path, int Track)> _tracks = [];
    private AudioStreamPlayer? _player;
    private int _last = -1;

    public bool Available => _tracks.Count > 0;
    public bool Playing => _player is { Playing: true };
    public int Track { get; private set; }

    public override void _Ready()
    {
        for (int t = 2; t <= I76.Game.Assets.CdMusic.LastTrack(Install); t++)
            if (I76.Game.Assets.CdMusic.Path(Install, t) is { } file) _tracks.Add((file, t));
        _last = _tracks.FindIndex(x => x.Track == _lastTrack);
        // the menu track carried into the editor ends: the editor's own music (or its silence) is what it has
        I76.Game.Shell.Shell.DropCarriedMusic(GetTree());
        _player = new AudioStreamPlayer { Name = "Music", Bus = "Music", VolumeDb = -8, ProcessMode = ProcessModeEnum.Always };
        AddChild(_player);
        _player.Finished += Next;
        Apply();
    }

    /// <summary>Starts or stops the music to match the setting and the Music level (call after either changes).</summary>
    public void Apply()
    {
        var s = I76.Game.Shell.ShellSettings.Load();
        I76.Game.Boot.Main.ApplyAudio(s);
        bool want = s.EditorMusic && s.Audio[0] >= 2 && _tracks.Count > 0;
        if (!want) { if (_player!.Playing) GD.Print("editor music: off"); _player.Stop(); return; }
        if (!_player!.Playing) Next();
    }

    private void Next()
    {
        if (_player is null || !IsInsideTree() || _tracks.Count == 0) return;
        int k = _last = I76.Formats.Audio.TrackShuffle.Next(_tracks.Count, _last, Rand);
        var (path, track) = _tracks[k];
        try
        {
            _player.Stream = new AudioStreamMP3 { Data = System.IO.File.ReadAllBytes(path), Loop = false };
            _player.VolumeDb = -8;
            _player.Play(float.TryParse(OS.GetEnvironment("I76_EDITOR_MUSIC_SEEK"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float seek) ? seek : 0f);
            Track = _lastTrack = track;
            GD.Print($"editor music: {(Install.IsNitro ? "nitro" : "i76")} track {track}{(_pausedForPlaytest ? " (back from the playtest)" : "")} ({_tracks.Count} tracks)");
            _pausedForPlaytest = false;
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            GD.PrintErr($"editor music: {path}: {ex.Message}");
        }
    }

    /// <summary>The music out over <paramref name="seconds"/> (a playtest's mission has its own); true when there was any to fade.</summary>
    public bool FadeOut(double seconds)
    {
        if (_player is not { Playing: true } p) return false;
        _pausedForPlaytest = true;
        GD.Print($"editor music: fading out for the playtest (track {Track})");
        p.Finished -= Next;
        var tw = CreateTween().SetPauseMode(Tween.TweenPauseMode.Process);
        tw.TweenProperty(p, "volume_db", -60.0, seconds);
        tw.TweenCallback(Callable.From(p.Stop));
        return true;
    }

    public override void _ExitTree() => _player?.Stop();
}
