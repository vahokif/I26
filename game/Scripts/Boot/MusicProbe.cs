using Godot;

namespace I76.Game;

/// <summary>
/// I76_MUSIC_PROBE=1: logs every music player in the tree (name, playing, playback position) four times a second and on any
/// change of who is playing, plus every frame longer than 50 ms. Lives on the root, so it sees across a scene reload:
/// a stop shows as "none playing", a stall as a long frame with the position still moving.
/// </summary>
public partial class MusicProbe : Node
{
    private static bool _installed;
    private double _t, _next;
    private string _last = "";

    public static void Install(SceneTree tree)
    {
        if (_installed || System.Environment.GetEnvironmentVariable("I76_MUSIC_PROBE") != "1") return;
        _installed = true;
        tree.Root.CallDeferred(Node.MethodName.AddChild, new MusicProbe { Name = "MusicProbe", ProcessMode = ProcessModeEnum.Always });
    }

    public override void _Process(double delta)
    {
        _t += delta;
        var s = "";
        Scan(GetTree().Root, ref s);
        if (s == "") { s = "none playing"; var m = GetTree().Root.FindChild("Music", true, false) as AudioStreamPlayer; if (m is not null) s += $" (Music node: playing={m.Playing} paused={m.StreamPaused} pos={m.GetPlaybackPosition():F2})"; }
        var key = System.Text.RegularExpressions.Regex.Replace(s, @" pos=\S+", "");
        bool changed = key != _last;
        if (changed || delta > 0.05 || _t >= _next)
        {
            GD.Print($"musicprobe t={_t:F3} dt={delta * 1000:F0}ms {(changed ? "CHANGE " : "")}{s}");
            _next = _t + 0.25;
        }
        _last = key;
    }

    private static void Scan(Node n, ref string s)
    {
        if (n is AudioStreamPlayer { Stream: AudioStreamMP3 } p && p.Playing)
            s += $"[{p.GetPath()}{(p.StreamPaused ? " PAUSED" : "")} pos={p.GetPlaybackPosition():F2}] ";
        foreach (var c in n.GetChildren()) Scan(c, ref s);
    }
}
