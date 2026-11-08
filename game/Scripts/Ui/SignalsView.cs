using Godot;

namespace I76.Game.Ui;

/// <summary>
/// The Developer panel's Signals (DevSignals): a row a signal, its name, its value now and a plot of the last
/// <see cref="DevSignals.Window"/> seconds, grouped by device. Drawn in the panel's theme, so it scales with it.
/// </summary>
public partial class SignalsView : Control
{
    private static readonly Color Ink = new(0.85f, 0.88f, 0.9f), Dim = new(0.55f, 0.6f, 0.65f), Group = new(0.95f, 0.65f, 0.25f),
        Plot = new(0.4f, 0.85f, 0.5f), Axis = new(1, 1, 1, 0.15f), Back = new(0, 0, 0, 0.35f);

    private enum Kind { Header, Trace, Note }
    private readonly List<(Kind Kind, string Text, SignalTrace? Trace, string Unit)> _rows = [];

    public override void _Ready()
    {
        ProcessMode = ProcessModeEnum.Always;
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _rows.Add((Kind.Header, "rumble: the pad's motors", null, ""));
        _rows.Add((Kind.Trace, "", DevSignals.PadLow, "%"));
        _rows.Add((Kind.Trace, "", DevSignals.PadHigh, "%"));
        _rows.Add((Kind.Header, Telemetry, null, ""));
        _rows.Add((Kind.Trace, "", DevSignals.TelRate, "/s"));
        _rows.Add((Kind.Trace, "", DevSignals.TelPitch, "°"));
        _rows.Add((Kind.Trace, "", DevSignals.TelRoll, "°"));
        _rows.Add((Kind.Trace, "", DevSignals.TelSurge, "m/s²"));
        _rows.Add((Kind.Trace, "", DevSignals.TelSway, "m/s²"));
        _rows.Add((Kind.Trace, "", DevSignals.TelHeave, "m/s²"));
        UpdateMinimumSize();
    }

    private const string Telemetry = "telemetry";
    private static string TelemetryLine => DevSignals.TelemetryTargets is { } t ? $"telemetry: {t}" : "telemetry: no feed ([port] telemetry or I76_TELEMETRY_UDP)";

    private Font F => GetThemeFont("font", "Label");
    private int FontPx => System.Math.Max(8, GetThemeFontSize("font_size", "Label") - 2);
    private float RowH => FontPx * 1.45f;

    public override Vector2 _GetMinimumSize() => new(FontPx * 26, RowH * _rows.Count + 2);

    public override void _Notification(int what)
    {
        if (what == NotificationThemeChanged) UpdateMinimumSize();
    }

    public override void _Process(double delta) => QueueRedraw();

    public override void _Draw()
    {
        var font = F;
        int size = FontPx;
        float rowH = RowH, w = Size.X, nameW = size * 9, valueW = size * 5, plotX = nameW + valueW, plotW = System.Math.Max(20, w - plotX);
        double end = DevSignals.All.Max(t => t.LastTime);
        double from = end - DevSignals.Window;
        float y = 0;
        foreach (var (kind, text, trace, unit) in _rows)
        {
            float baseLine = y + rowH * 0.75f;
            if (kind == Kind.Header)
                // the feed starts with the simulation, after the view: its targets are read as it draws
                DrawString(font, new Vector2(0, baseLine), text == Telemetry ? TelemetryLine : text, HorizontalAlignment.Left, w, size, Group);
            else if (trace is not null)
            {
                DrawString(font, new Vector2(size * 0.5f, baseLine), trace.Name, HorizontalAlignment.Left, nameW - size * 0.5f, size, Ink);
                DrawString(font, new Vector2(nameW, baseLine), Value(trace, unit), HorizontalAlignment.Right, valueW - size * 0.5f, size, Ink);
                var box = new Rect2(plotX, y + 2, plotW, rowH - 4);
                DrawRect(box, Back);
                // the zero line where the range spans it
                float Yof(float v) => box.End.Y - (System.Math.Clamp(v, trace.Min, trace.Max) - trace.Min) / (trace.Max - trace.Min) * box.Size.Y;
                if (trace.Min < 0 && trace.Max > 0) DrawLine(new Vector2(box.Position.X, Yof(0)), new Vector2(box.End.X, Yof(0)), Axis, 1);
                Vector2? prev = null;
                foreach (var (t, v) in trace.Since(from))
                {
                    var p = new Vector2(box.Position.X + (float)((t - from) / DevSignals.Window) * box.Size.X, Yof(v));
                    if (prev is { } q) DrawLine(q, p, Plot, System.Math.Max(1, size / 8f));
                    prev = p;
                }
                if (trace.Count == 0) DrawString(font, new Vector2(box.Position.X + size * 0.5f, baseLine), "no data", HorizontalAlignment.Left, -1, size, Dim);
            }
            y += rowH;
        }
    }

    private static string Value(SignalTrace t, string unit)
    {
        float v = t.Last;
        return unit switch
        {
            "%" => $"{v * 100:0}%",
            "°" => $"{Mathf.RadToDeg(v):0.0}°",
            "/s" => $"{v:0}/s",
            "m/s²" => $"{v:0.0}",
            _ => $"{v:0}",
        };
    }
}

/// <summary>The Signals in a small frame at the window's top right, over the running game (the Developer panel's "show over the game").</summary>
public partial class SignalsOverlay : Control
{
    private System.Action _rescale = null!;

    public override void _Ready()
    {
        ProcessMode = ProcessModeEnum.Always;
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;
        var theme = new Theme { DefaultFontSize = 15 };
        var bg = new StyleBoxFlat { BgColor = new Color(0.08f, 0.09f, 0.1f, 0.8f), BorderColor = new Color(0.85f, 0.6f, 0.2f) };
        var (panel, scroll, col) = DevScale.Frame(theme, bg);
        panel.MouseFilter = MouseFilterEnum.Ignore;
        AddChild(panel);
        var title = new Label { Text = "Signals (Ctrl+F2: the Developer panel)" };
        title.AddThemeColorOverride("font_color", new Color(0.95f, 0.65f, 0.25f));
        col.AddChild(title);
        col.AddChild(new SignalsView());
        _rescale = () =>
        {
            int f = DevScale.Factor(GetViewportRect().Size.Y);
            DevScale.Apply(panel, scroll, col, theme, bg, f, () => new Vector2(GetViewportRect().Size.X - panel.Size.X - 16 * f, 16 * f));
        };
        GetViewport().SizeChanged += _rescale;
        TreeExiting += () => GetViewport().SizeChanged -= _rescale;
        _rescale();
    }
}
