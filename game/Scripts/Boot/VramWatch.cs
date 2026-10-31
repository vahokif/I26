using Godot;

namespace I76.Game.Boot;

/// <summary>I76_VRAM=SECONDS: a line every SECONDS of the engine's texture, buffer and video memory and its object and resource counts (to see what grows). Silent without it.</summary>
public partial class VramWatch : Node
{
    private double _every, _next;
    private static bool _added;
    private bool _said;

    public static void Install(SceneTree tree)
    {
        if (_added || !double.TryParse(System.Environment.GetEnvironmentVariable("I76_VRAM"), out double s) || s <= 0) return;
        _added = true;
        tree.Root.CallDeferred(Node.MethodName.AddChild, new VramWatch { Name = "VramWatch", _every = s });
    }

    public override void _Process(double delta)
    {
        _next -= delta;
        if (_next > 0) return;
        _next = _every;
        double M(Performance.Monitor m) => Performance.GetMonitor(m) / 1048576.0;
        if (!_said) { _said = true; GD.Print($"vram: GPU takes s3tc {RenderingServer.HasOsFeature("s3tc")}, bptc {RenderingServer.HasOsFeature("bptc")}, etc2 {RenderingServer.HasOsFeature("etc2")}, astc {RenderingServer.HasOsFeature("astc")}"); }
        GD.Print($"vram: t={Time.GetTicksMsec() / 1000.0:0} tex {M(Performance.Monitor.RenderTextureMemUsed):0.0} MiB, buf {M(Performance.Monitor.RenderBufferMemUsed):0.0}, video {M(Performance.Monitor.RenderVideoMemUsed):0.0}; objects {Performance.GetMonitor(Performance.Monitor.ObjectCount):0}, resources {Performance.GetMonitor(Performance.Monitor.ObjectResourceCount):0}, nodes {Performance.GetMonitor(Performance.Monitor.ObjectNodeCount):0}, draw calls {Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame):0}");
    }
}
