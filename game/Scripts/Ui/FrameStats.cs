using Godot;

namespace I76.Game.Ui;

/// <summary>
/// The frame times of the last few seconds (the Developer panel's Show FPS): a ring of real frame deltas trimmed to a window (1 s),
/// read as the mean rate over the window. Engine-free.
/// </summary>
public sealed class FrameStats
{
    private readonly double[] _ring;
    private int _head, _count;
    private double _sum;
    public double Window { get; }

    public FrameStats(double windowSeconds = 1.0, int capacity = 4096) { Window = windowSeconds; _ring = new double[capacity]; }

    public int Count => _count;

    /// <summary>One frame's real time in seconds; the oldest frames go once the ring holds more than the window.</summary>
    public void Add(double dt)
    {
        if (dt <= 0 || double.IsNaN(dt) || double.IsInfinity(dt)) return;
        if (_count == _ring.Length) Drop();
        _ring[(_head + _count++) % _ring.Length] = dt;
        _sum += dt;
        while (_count > 1 && _sum > Window) Drop();
    }

    private void Drop()
    {
        _sum -= _ring[_head];
        _head = (_head + 1) % _ring.Length;
        _count--;
    }

    private double At(int i) => _ring[(_head + i) % _ring.Length];

    /// <summary>Frames per second over the newest <paramref name="seconds"/> (at least one frame); 0 with none.</summary>
    public double Fps(double seconds)
    {
        double t = 0; int n = 0;
        for (int i = _count - 1; i >= 0 && (n == 0 || t < seconds); i--) { t += At(i); n++; }
        return t > 0 ? n / t : 0;
    }

    /// <summary>The overlay's text: "FPS 64", the mean rate over the window (frames counted over the time they took).</summary>
    public string Text() => Fps(Window) is var f and > 0 ? $"FPS {f:0}" : "FPS --";
}

/// <summary>The Show FPS overlay: a small label in the window's top left corner, scaled with the Developer panel's factor.</summary>
public partial class FpsOverlay : Control
{
    private readonly FrameStats _stats = new();
    private Label _label = null!;
    private ulong _last;
    private double _since;

    public override void _Ready()
    {
        ProcessMode = ProcessModeEnum.Always;
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;
        _label = new Label { Text = "FPS --", MouseFilter = MouseFilterEnum.Ignore, Position = new Vector2(8, 6) };
        _label.AddThemeColorOverride("font_color", new Color(0.7f, 1f, 0.7f));
        _label.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.9f));
        _label.AddThemeConstantOverride("outline_size", 4);
        AddChild(_label);
        GetViewport().SizeChanged += Rescale;
        TreeExiting += () => GetViewport().SizeChanged -= Rescale;
        Rescale();
        _last = Time.GetTicksUsec();
    }

    private void Rescale()
    {
        int f = DevScale.Factor(GetViewportRect().Size.Y);
        _label.AddThemeFontSizeOverride("font_size", 15 * f);
        _label.Position = new Vector2(8 * f, 6 * f);
    }

    public override void _Process(double delta)
    {
        // real time, not the process delta: a time scale or a paused tree does not change what the machine is doing
        ulong now = Time.GetTicksUsec();
        double dt = (now - _last) / 1e6;
        _last = now;
        _stats.Add(dt);
        _since += dt;
        if (_since < 0.5) return;
        _since = 0;
        _label.Text = _stats.Text();
    }
}
