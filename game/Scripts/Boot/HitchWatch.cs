using System.Linq;
using System.Threading;
using System.Diagnostics;
using Godot;

namespace I76.Game;

/// <summary>
/// Where the time of a frame goes, measured by a Stopwatch around the main phases (<see cref="Scope"/>). Silent normally: a frame
/// that takes more than <see cref="LimitMs"/> prints one line to the Godot log naming the slowest phase, for a report of a
/// multi-second hang to say what it was. A phase nested in another (input inside the shell) counts in both.
/// </summary>
public static class Hitch
{
    public enum Phase { Input, Rumble, Force, Sim, Shell, PadEnum, PadPlug, Load, Views, Count }

    public static readonly double LimitMs = double.TryParse(System.Environment.GetEnvironmentVariable("I76_HITCH_MS"), out var lim) ? lim : 200;   // I76_HITCH_MS lowers it, to hunt small hitches
    private static long _pipes;
    private static long[] _kinds = new long[5];

    /// <summary>The render pipelines compiled so far, by kind: canvas, mesh (at load), surface (first draw), draw (a stall in the draw call), specialization (in the background).</summary>
    private static long[] Kinds() => new[] { RenderingServer.RenderingInfo.PipelineCompilationsCanvas, RenderingServer.RenderingInfo.PipelineCompilationsMesh, RenderingServer.RenderingInfo.PipelineCompilationsSurface, RenderingServer.RenderingInfo.PipelineCompilationsDraw, RenderingServer.RenderingInfo.PipelineCompilationsSpecialization }.Select(k => (long)RenderingServer.GetRenderingInfo(k)).ToArray();
    private static readonly long[] Ticks = new long[(int)Phase.Count];
    private static readonly long[] Worst = new long[(int)Phase.Count];     // the longest single scope of each phase this frame
    private static readonly string[] Names = ["input sampling", "rumble driver call", "force feedback tick", "sim tick", "shell", "joypad enumeration", "pad hot-plug handling", "mission build (behind the loading card)", "effect views (combat, damage, fire, smoke)"];

    /// <summary>Time the phase until the scope is disposed (a struct: no allocation).</summary>
    public static Scope Time(Phase p) => new(p);

    public readonly struct Scope : IDisposable
    {
        private readonly Phase _phase;
        private readonly long _start;
        public Scope(Phase phase) { _phase = phase; _start = Stopwatch.GetTimestamp(); Volatile.Write(ref _inside, (int)phase); }
        public void Dispose()
        {
            long d = Stopwatch.GetTimestamp() - _start;
            Ticks[(int)_phase] += d;
            if (d > Worst[(int)_phase]) Worst[(int)_phase] = d;
            Volatile.Write(ref _inside, -1);
            Volatile.Write(ref _lastDone, (int)_phase);
        }
    }

    // for the stall watch: the phase the main thread is in now (-1: none of the measured ones), and the last it left
    private static int _inside = -1, _lastDone = -1;
    internal static string Where()
    {
        int i = Volatile.Read(ref _inside), d = Volatile.Read(ref _lastDone);
        return i >= 0 ? $"inside {Names[i]}" : $"outside the measured phases (render/engine/loading), last left: {(d >= 0 ? Names[d] : "none")}";
    }

    /// <summary>The frame that ended after <paramref name="frameTicks"/> Stopwatch ticks: the line to print, or null if it was quick.</summary>
    public static string? Report(long frameTicks, bool reset = true)
    {
        double ms = frameTicks * 1000.0 / Stopwatch.Frequency;
        string? line = null;
        if (ms > LimitMs)
        {
            int top = 0;
            long sum = 0;
            for (int i = 0; i < (int)Phase.Count; i++) { if (Ticks[i] > Ticks[top]) top = i; }
            sum = Ticks[top];
            double topMs = Ticks[top] * 1000.0 / Stopwatch.Frequency;
            line = topMs >= ms * 0.4
                ? $"hitch: frame {Godot.Engine.GetProcessFrames()} {ms:0} ms, slowest phase {Names[top]} {topMs:0} ms (longest single call {Worst[top] * 1000.0 / Stopwatch.Frequency:0} ms)"
                : $"hitch: frame {Godot.Engine.GetProcessFrames()} {ms:0} ms, mostly outside the measured phases (render/loading/engine; slowest measured: {Names[top]} {topMs:0} ms)";
            _ = sum;
            if (LimitMs < 200) { var now = Kinds(); line += $" pipelines {now.Sum() - _pipes} (total {now.Sum()}; canvas/mesh/surface/draw/specialization {string.Join('/', now.Select((n, k) => n - _kinds[k]))})"; }
            if (LimitMs < 200) line += $" [terrain update {I76.Game.Boot.FrameProfiler.TerrainUpdateMs:0.0} ms, gc0 {GC.CollectionCount(0)} gc2 {GC.CollectionCount(2)}]";
        }
        { _kinds = Kinds(); _pipes = _kinds.Sum(); }
        if (reset) { Array.Clear(Ticks); Array.Clear(Worst); }
        return line;
    }
}

/// <summary>Ends each frame's measurement at the start of the next (so that render and the swap are in it) and prints the hitches.</summary>
public partial class HitchWatch : Node
{
    private long _last, _prev;
    private static bool _added;

    public static void Install(SceneTree tree)
    {
        if (_added) return;
        _added = true;
        tree.Root.CallDeferred(Node.MethodName.AddChild, new HitchWatch { Name = "HitchWatch", ProcessMode = ProcessModeEnum.Always, ProcessPriority = int.MinValue });
    }

    // a frame that never ends prints nothing from _Process: a background thread notices it and says where the main thread is
    private static long _frameStart;
    private static System.Threading.Thread? _stallWatch;
    public const double StallSeconds = 5;

    public override void _Ready()
    {
        if (_stallWatch is not null) return;
        _stallWatch = new System.Threading.Thread(StallLoop) { IsBackground = true, Name = "I26 stall watch" };
        _stallWatch.Start();
    }

    private static void StallLoop()
    {
        long stuckFrame = 0;
        double printedAt = 0;
        while (true)
        {
            System.Threading.Thread.Sleep(1000);
            long start = Interlocked.Read(ref _frameStart);
            if (start == 0) continue;
            double s = (Stopwatch.GetTimestamp() - start) / (double)Stopwatch.Frequency;
            if (s < StallSeconds) continue;
            // once at five seconds, then every ten while the same frame lasts
            if (stuckFrame != start || s - printedAt >= 10)
            {
                stuckFrame = start;
                printedAt = s;
                GD.Print($"stall: a frame stuck for {s:0} s, {Hitch.Where()}");
            }
        }
    }

    public override void _Process(double delta)
    {
        long now = Stopwatch.GetTimestamp();
        // the rumble guard: a long frame with a vibration sent since the frame before it began (Godot applies it a frame late)
        if (_last != 0) GodotRumble.FrameEnded((now - _last) * 1000.0 / Stopwatch.Frequency, _prev);
        _prev = _last;
        Interlocked.Exchange(ref _frameStart, now);
        if (_last != 0 && Hitch.Report(now - _last) is { } line) GD.Print(line);
        else if (_last != 0) Hitch.Report(0);       // a quick frame: just clear
        _last = now;
    }
}
