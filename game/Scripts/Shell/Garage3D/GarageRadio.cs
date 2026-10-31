using Godot;
using I76.Formats.Vfs;

namespace I76.Game.Shell.Garage3D;

/// <summary>
/// The garage's music: a boombox on the workbench playing the soundtrack of the installed game (and of the other game, when this machine has
/// both), a random track after another. It is a 3D voice in the bay, so it pans and fades as the camera goes round, and goes through a bus
/// of its own, <see cref="BusName"/>, that makes it a speaker in a concrete room: a high-pass at 150 Hz and a low-pass at 6.5 kHz (a small
/// speaker), then a reverb (room 0.55, damping 0.4, wet 0.3, dry 0.85, spread 1, a 20 ms pre-delay). The bus follows the Music level of Audio
/// Control (off below 2, 2 dB a step round 8, as the missions' Music bus does).
/// </summary>
public sealed partial class GarageRadio : Node3D
{
    public const string BusName = "GarageRadio";
    public const float HighPassHz = 150f, LowPassHz = 6500f, RoomSize = 0.55f, Damping = 0.4f, Wet = 0.3f, Dry = 0.85f;

    /// <summary>The installs whose music plays (the running game first).</summary>
    public required IReadOnlyList<GameInstall> Installs { get; init; }
    /// <summary>Audio Control's Music level, 0..10.</summary>
    public required int Level { get; init; }

    private readonly List<(string Path, int Track, string Game)> _tracks = [];
    private readonly Random _rand = new();
    private AudioStreamPlayer3D _player = null!;
    private int _last = -1;

    public int TrackCount => _tracks.Count;
    public string? Playing { get; private set; }

    public override void _Ready()
    {
        foreach (var install in Installs)
            for (int t = 2; t <= I76.Game.Assets.CdMusic.LastTrack(install); t++)
                if (I76.Game.Assets.CdMusic.Path(install, t) is { } file) _tracks.Add((file, t, install.IsNitro ? "nitro" : "i76"));
        if (_tracks.Count == 0) return;
        EnsureBus();
        // a small speaker on a bench: loud close by, a long way round, panned by where the camera is
        _player = new AudioStreamPlayer3D
        {
            Name = "Boombox", Bus = BusName, VolumeDb = 0f, UnitSize = 8f, MaxDistance = 40f, AttenuationModel = AudioStreamPlayer3D.AttenuationModelEnum.InverseDistance,
            PanningStrength = 0.8f, MaxDb = 0f, ProcessMode = ProcessModeEnum.Always,
        };
        AddChild(_player);
        _player.Finished += Next;
        Next();
    }

    /// <summary>The bus, made once: effects in the order the sound meets them.</summary>
    private void EnsureBus()
    {
        int i = AudioServer.GetBusIndex(BusName);
        if (i < 0)
        {
            AudioServer.AddBus();
            i = AudioServer.BusCount - 1;
            AudioServer.SetBusName(i, BusName);
            AudioServer.SetBusSend(i, "Master");
            AudioServer.AddBusEffect(i, new AudioEffectHighPassFilter { CutoffHz = HighPassHz });
            AudioServer.AddBusEffect(i, new AudioEffectLowPassFilter { CutoffHz = LowPassHz });
            AudioServer.AddBusEffect(i, new AudioEffectReverb { RoomSize = RoomSize, Damping = Damping, Wet = Wet, Dry = Dry, Spread = 1f, PredelayMsec = 20f, Hipass = 0f });
        }
        AudioServer.SetBusMute(i, Level < 2);
        AudioServer.SetBusVolumeDb(i, (Level - 8) * 2f);
    }

    /// <summary>The next track, another at random than the one that just ended.</summary>
    private void Next()
    {
        if (_tracks.Count == 0 || !IsInsideTree()) return;
        int k;
        do k = _rand.Next(_tracks.Count); while (_tracks.Count > 1 && k == _last);
        _last = k;
        var (path, track, game) = _tracks[k];
        try
        {
            _player.Stream = new AudioStreamMP3 { Data = System.IO.File.ReadAllBytes(path), Loop = false };
            _player.Play(float.TryParse(System.Environment.GetEnvironmentVariable("I76_GARAGE_MUSIC_SEEK"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float seek) ? seek : 0f);
            Playing = $"{game} track {track}";
            GD.Print(FormattableString.Invariant($"garage radio: {Playing} ({_tracks.Count} tracks; bus {BusName}: high-pass {HighPassHz:0} Hz, low-pass {LowPassHz:0} Hz, reverb room {RoomSize} damping {Damping} wet {Wet})"));
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            GD.PrintErr($"garage radio: {path}: {ex.Message}");
            Playing = null;
        }
    }

    private int _frames;

    public override void _Process(double delta)
    {
        // I76_AUDIO_LOG=1: what the bus is carrying (the Dummy driver of a capture still mixes)
        if (_player is null || System.Environment.GetEnvironmentVariable("I76_AUDIO_LOG") != "1" || ++_frames % 30 != 0) return;
        int i = AudioServer.GetBusIndex(BusName);
        GD.Print(FormattableString.Invariant($"[audio] garage radio: bus peak {AudioServer.GetBusPeakVolumeLeftDb(i, 0):0.0} / {AudioServer.GetBusPeakVolumeRightDb(i, 0):0.0} dB, volume {AudioServer.GetBusVolumeDb(i):0.0} dB, muted {AudioServer.IsBusMute(i)}, playing {_player.Playing} at {_player.GetPlaybackPosition():0.0} s"));
    }

    public void Pause(bool on) { if (_player is not null) _player.StreamPaused = on; }

    public override void _ExitTree() => _player?.Stop();
}
