using Godot;
using I76.Formats.Vfs;

namespace I76.Game.Ui;

/// <summary>
/// The audio sliders' feedback: changing SFX Level plays cammo.wav at the new level,
/// Voice Level croger.wav, each cutting the preview still sounding. The shell's Audio Control and the in-mission menu both use it.
/// </summary>
public static class LevelPreview
{
    public const string NodeName = "LevelPreview";

    /// <summary>The sound the row's slider previews: row 1 (SFX Level) cammo.wav, row 2 (Voice Level) croger.wav, none for the music.</summary>
    public static string? SoundFor(int row) => row switch { 1 => "cammo.wav", 2 => "croger.wav", _ => null };

    /// <summary>The bus the preview plays on, so the level just set applies to it.</summary>
    public static string BusFor(int row) => row == 2 ? "Voice" : "SFX";

    /// <summary>Plays the row's preview (after the buses carry the new level); the player lives under <paramref name="host"/> and runs while the tree is paused.</summary>
    public static void Play(Node host, AssetVfs vfs, int row)
    {
        if (SoundFor(row) is not { } name) return;
        if (host.GetNodeOrNull(NodeName) is { } old) { old.Name = NodeName + "Old"; old.QueueFree(); }
        if (new Assets.AudioBank(vfs).Get(name) is not { } stream) return;
        var p = new AudioStreamPlayer { Name = NodeName, Stream = stream, Bus = BusFor(row), ProcessMode = Node.ProcessModeEnum.Always };
        host.AddChild(p);
        p.Finished += p.QueueFree;
        p.Play();
        GD.Print($"[audio] slider preview {name} on {p.Bus}");
    }
}
