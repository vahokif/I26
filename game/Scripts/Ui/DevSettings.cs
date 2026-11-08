using Godot;
using I76.Game.Shell;
using I76.Game.Sim;

namespace I76.Game.Ui;

/// <summary>When a change of a setting shows: at once, from the next menu screen drawn, from the next mission, or from the next start of the game.</summary>
public enum DevWhen { Live, NextScreen, NextMission, NextStart }

/// <summary>
/// One of the [port] keys no pad sets: its key, its values as the file writes them (the port's marked with
/// <see cref="DevSettings.PortMark"/>), how it reads and writes ShellSettings, and when a change shows. <see cref="Apply"/> is
/// what a change needs beyond the setting and the host's refresh.
/// </summary>
public sealed record DevSetting(string Key, string[] Values, Func<ShellSettings, int> Get, Action<ShellSettings, int> Set, DevWhen When,
    Action<ShellSettings>? Apply = null);

/// <summary>
/// The Developer panels' Settings section (in a mission and on the main menu): the shell.cfg [port] keys that no pad sets,
/// each a button that steps its value (a right click steps back). A change is saved to shell.cfg at once and applied where
/// the setting allows it; the key's colour says when it shows.
/// </summary>
public static class DevSettings
{
    /// <summary>Marks a value the original game does not have; the one unmarked value is the original's (or, for a feature of the port's own, off).</summary>
    public const string PortMark = "*";
    private const string M = PortMark;
    private static readonly string[] FalseTrue = ["false", "true" + M];

    public static readonly DevSetting[] All =
    [
        // every name shown at a network game's start (Ctrl+T toggles it in the game; the original starts each game with it off)
        new("net_names", FalseTrue, s => s.NetNames ? 1 : 0, (s, v) => s.NetNames = v != 0, DevWhen.NextMission),
        // the telemetry feed's packet for a target that names none ([port] telemetry or I76_TELEMETRY_UDP sets the targets)
        new("telemetry_format", ["i26", "outsim" + M, "outgauge" + M],
            s => (int)(TelemetryPackets.ParseFormat(s.TelemetryFormat) ?? TelemetryFormat.I26),
            (s, v) => s.TelemetryFormat = v == 0 ? null : ((TelemetryFormat)v).ToString().ToLowerInvariant(), DevWhen.NextMission),
        new("dev_fps", FalseTrue, s => s.DevFps ? 1 : 0, (s, v) => s.DevFps = v != 0, DevWhen.Live),
        new("developer", FalseTrue, s => s.DeveloperTools ? 1 : 0, (s, v) => s.DeveloperTools = v != 0, DevWhen.NextStart),
    ];

    public static string WhenText(DevWhen w) => w switch { DevWhen.Live => "live", DevWhen.NextScreen => "next screen", DevWhen.NextMission => "next mission", _ => "next start" };

    /// <summary>The key's colour for when a change shows: green at once, amber the next screen or mission, grey the next start.</summary>
    public static Color WhenColour(DevWhen w) => w switch
    {
        DevWhen.Live => new Color(0.6f, 0.9f, 0.6f),
        DevWhen.NextScreen or DevWhen.NextMission => new Color(0.95f, 0.8f, 0.45f),
        _ => new Color(0.65f, 0.68f, 0.72f),
    };

    /// <summary>
    /// The section into <paramref name="col"/>: a legend, then two keys a line. A change sets <paramref name="settings"/>,
    /// saves it, runs the entry's Apply and then <paramref name="changed"/> (the host's own refresh and message).
    /// </summary>
    public static void Build(VBoxContainer col, ShellSettings settings, Action<ShellSettings, DevSetting>? changed)
    {
        var legend = DevScale.Sep(new HBoxContainer(), 10);
        foreach (var w in new[] { DevWhen.Live, DevWhen.NextMission, DevWhen.NextStart })
        {
            var l = new Label { Text = w == DevWhen.NextMission ? "next screen / mission" : WhenText(w) };
            l.AddThemeColorOverride("font_color", WhenColour(w));
            legend.AddChild(l);
        }
        legend.AddChild(new Label { Text = "(* the port's)" });
        col.AddChild(legend);
        var grid = DevScale.Grid(new GridContainer { Columns = 4 }, 8, 4);
        foreach (var e in All)
        {
            var name = DevScale.Min(new Label { Text = e.Key, TooltipText = WhenText(e.When) }, 136);
            name.AddThemeColorOverride("font_color", WhenColour(e.When));
            var b = DevScale.Min(new Button { FocusMode = Control.FocusModeEnum.None, Alignment = HorizontalAlignment.Left, TooltipText = "click: next value, right click: back" }, 84);
            void Show() => b.Text = e.Values[Math.Clamp(e.Get(settings), 0, e.Values.Length - 1)];
            void Step(int by)
            {
                int v = ((Math.Clamp(e.Get(settings), 0, e.Values.Length - 1) + by) % e.Values.Length + e.Values.Length) % e.Values.Length;
                e.Set(settings, v);
                settings.Save();
                e.Apply?.Invoke(settings);
                changed?.Invoke(settings, e);
                Show();
            }
            b.Pressed += () => Step(+1);
            b.GuiInput += ev => { if (ev is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Right }) Step(-1); };
            Show();
            grid.AddChild(name);
            grid.AddChild(b);
        }
        col.AddChild(grid);
    }

    /// <summary>The panel's line for a change: the key, its value as the file writes it, and when it shows.</summary>
    public static string Line(DevSetting e, ShellSettings s) =>
        $"{e.Key} = {e.Values[Math.Clamp(e.Get(s), 0, e.Values.Length - 1)].TrimEnd('*')} ({WhenText(e.When)})";
}
