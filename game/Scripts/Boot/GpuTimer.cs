using Godot;

namespace I76.Game.Boot;

/// <summary>
/// I76_GPU_TIME=1: measures the main viewport's GPU render time per frame
/// (RenderingServer's own timestamps) and prints the mean and median past the
/// first frames, for A/B costs of a rendering feature in tools/shot.sh runs.
/// </summary>
public partial class GpuTimer : Node
{
    public static bool Enabled => System.Environment.GetEnvironmentVariable("I76_GPU_TIME") == "1";
    private readonly List<double> _gpu = [], _cpu = [];
    private int _frames;

    public override void _Ready() => RenderingServer.ViewportSetMeasureRenderTime(GetViewport().GetViewportRid(), true);

    public override void _Process(double delta)
    {
        var vp = GetViewport().GetViewportRid();
        if (++_frames > 10)
        {
            _gpu.Add(RenderingServer.ViewportGetMeasuredRenderTimeGpu(vp));
            _cpu.Add(RenderingServer.ViewportGetMeasuredRenderTimeCpu(vp));
        }
        if (_gpu.Count > 0 && _gpu.Count % 10 == 0) Report();
    }

    private void Report()
    {
        var g = _gpu.Order().ToList();
        GD.Print($"gpu-time: {g.Count} frames, mean {g.Average():0.00} ms, median {g[g.Count / 2]:0.00} ms, min {g[0]:0.00} ms; cpu render {_cpu.Average():0.00} ms");
    }
}
