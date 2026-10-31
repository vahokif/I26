using Godot;
using I76.Game.Net;

namespace I76.Game.Editor;

/// <summary>A small modal form in the HUD's style: a title, a line of text, labelled fields, a line for what is wrong, OK and Cancel.</summary>
public partial class FormDialog : Control
{
    public required string Title { get; init; }
    public string Text { get; init; } = "";
    /// <summary>Label, starting text, longest text.</summary>
    public required (string Label, string Value, int Max)[] Fields { get; init; }
    /// <summary>Checks the values; returns what is wrong, or null to go on.</summary>
    public Func<string[], string?>? Check { get; init; }
    public required Action<string[]> Ok { get; init; }
    public required Action Cancel { get; init; }
    public string OkText { get; init; } = "OK";

    private readonly List<LineEdit> _edits = [];
    private Label _error = null!;

    public override void _Ready()
    {
        float s = Math.Clamp(GetViewportRect().Size.Y / 1080f, 0.8f, 2.4f);
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;
        AddChild(new ColorRect { Color = new Color(0, 0, 0, 0.55f), AnchorRight = 1, AnchorBottom = 1, MouseFilter = MouseFilterEnum.Ignore });
        var panel = new PanelContainer { AnchorLeft = 0.5f, AnchorRight = 0.5f, AnchorTop = 0.5f, AnchorBottom = 0.5f, GrowHorizontal = GrowDirection.Both, GrowVertical = GrowDirection.Both };
        panel.AddThemeStyleboxOverride("panel", EditorStyle.Panel(s));
        AddChild(panel);
        var box = new VBoxContainer { CustomMinimumSize = new Vector2(520 * s, 0) };
        box.AddThemeConstantOverride("separation", (int)(10 * s));
        panel.AddChild(box);
        var t = new Label { Text = Title };
        EditorStyle.Title(t, 28 * s);
        box.AddChild(t);
        if (Text.Length > 0)
        {
            var l = new Label { Text = Text, AutowrapMode = TextServer.AutowrapMode.WordSmart };
            EditorStyle.Body(l, 16 * s, dim: true);
            box.AddChild(l);
        }
        foreach (var (label, value, max) in Fields)
        {
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", (int)(10 * s));
            var fl = new Label { Text = label, CustomMinimumSize = new Vector2(190 * s, 0) };
            EditorStyle.Body(fl, 17 * s);
            row.AddChild(fl);
            var e = new LineEdit { Text = value, MaxLength = max, SizeFlagsHorizontal = SizeFlags.ExpandFill, SelectAllOnFocus = true };
            e.AddThemeFontOverride("font", HudStyle.Bold);
            e.AddThemeFontSizeOverride("font_size", (int)(18 * s));
            e.AddThemeColorOverride("font_color", HudStyle.Parchment);
            e.AddThemeStyleboxOverride("normal", EditorStyle.Flat(new Color(0.06f, 0.04f, 0.025f, 0.95f), 5, s, new Color(HudStyle.Edge, 0.3f), 6));
            e.AddThemeStyleboxOverride("focus", EditorStyle.Flat(new Color(0.06f, 0.04f, 0.025f, 0.95f), 5, s, HudStyle.Amber, 6));
            e.TextSubmitted += _ => Submit();
            row.AddChild(e);
            _edits.Add(e);
            box.AddChild(row);
        }
        _error = new Label { Text = " ", AutowrapMode = TextServer.AutowrapMode.WordSmart };
        EditorStyle.Body(_error, 16 * s, colour: HudStyle.Danger);
        box.AddChild(_error);
        var buttons = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End };
        buttons.AddThemeConstantOverride("separation", (int)(10 * s));
        buttons.AddChild(EditorStyle.Button(OkText, Submit, s, 22));
        buttons.AddChild(EditorStyle.Button("CANCEL", () => Cancel(), s, 22));
        box.AddChild(buttons);
        if (_edits.Count > 0) _edits[0].GrabFocus();
    }

    private void Submit()
    {
        var values = _edits.Select(e => e.Text.Trim()).ToArray();
        if (Check?.Invoke(values) is { } problem) { _error.Text = problem; return; }
        Ok(values);
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (e is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape }) { GetViewport().SetInputAsHandled(); Cancel(); }
    }

    /// <summary>The form for a new flat map: name, listed name, size in tiles, height. Checks the name against the folder's maps.</summary>
    public static FormDialog NewFlatMap(I76.Formats.Vfs.GameInstall install, Action<string, string, int, double> ok, Action cancel) => new()
    {
        Title = "NEW FLAT MAP",
        Text = "A flat map with a spawn in the middle, saved to the game's addon folder. Its sky and light are a Nitro arena's.",
        Fields = [("Name (1-8 letters, digits, _)", "myarena", 8), ("Listed name (15)", "My Arena", 15), ("Size in tiles (1-4)", "2", 1), ("Height (m)", "10", 6)],
        OkText = "CREATE",
        Check = v =>
        {
            if (!I76.Editor.MapSaver.ValidName(v[0])) return "The name is 1 to 8 letters, digits and underscores.";
            if (!I76.Editor.MapSaver.ValidDisplayName(v[1])) return "The listed name is 1 to 15 characters.";
            if (!int.TryParse(v[2], out int t) || t is < 1 or > I76.Editor.NewMap.MaxTiles) return "The size is 1 to 4 tiles a side (640 m each).";
            if (!double.TryParse(v[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double h) || h is < 0 or > I76.Editor.Units.MaxMetres) return "The height is 0 to 409.5 m.";
            return NameTaken(install, v[0]) ? "A map of that name exists." : null;
        },
        Ok = v => ok(v[0], v[1], int.Parse(v[2]), double.Parse(v[3], System.Globalization.CultureInfo.InvariantCulture)),
        Cancel = cancel,
    };

    /// <summary>The form for Save as: a new name and listed name.</summary>
    public static FormDialog SaveAs(I76.Formats.Vfs.GameInstall install, string name, string listed, Action<string, string> ok, Action cancel) => new()
    {
        Title = "SAVE AS",
        Text = "Saved to the game's addon folder. The stock maps are never written over.",
        Fields = [("Name (1-8 letters, digits, _)", name, 8), ("Listed name (15)", listed.Length > 15 ? listed[..15] : listed, 15)],
        OkText = "SAVE",
        Check = v =>
        {
            if (!I76.Editor.MapSaver.ValidName(v[0])) return "The name is 1 to 8 letters, digits and underscores.";
            if (!I76.Editor.MapSaver.ValidDisplayName(v[1])) return "The listed name is 1 to 15 characters.";
            return NameTaken(install, v[0]) ? "A map of that name exists." : null;
        },
        Ok = v => ok(v[0], v[1]),
        Cancel = cancel,
    };

    private static bool NameTaken(I76.Formats.Vfs.GameInstall install, string name) => install.FindMission(name) is not null;
}
