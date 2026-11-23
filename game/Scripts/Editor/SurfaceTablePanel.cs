using System.Globalization;
using Godot;
using I76.Editor;
using I76.Formats.Mission;
using I76.Game.Net;

namespace I76.Game.Editor;

/// <summary>
/// The surface table window (docs/map-editor.md "Surface table"): the map's eight surface types, each with its grip, rolling, bump and
/// damage a second, which a car on that type of ground gets. A value typed (Enter or leaving the field) or a preset clicked is one undo
/// step at once (<see cref="MapEditor.ApplySurfaces"/>); nothing is rebuilt: the sim reads the table when a playtest starts, and Save writes it.
/// Clicking a type's name also picks it for the surface brush.
/// </summary>
public partial class SurfaceTablePanel : PanelContainer
{
    public required TerrainDocument Doc { get; init; }
    public required float S { get; init; }
    /// <summary>The table to put in the map, and a name for the undo list.</summary>
    public required Action<SurfaceTable, string> Apply { get; init; }
    /// <summary>The surface brush's type: the selected row.</summary>
    public required Func<int> Selected { get; init; }
    public required Action<int> Select { get; init; }
    public required Action Close { get; init; }

    private enum Field { Grip, Rolling, Bump, Damage }
    private readonly LineEdit[,] _edits = new LineEdit[SurfaceTable.Count, 4];
    private readonly Button[] _names = new Button[SurfaceTable.Count];
    private Label _note = null!;

    public override void _Ready()
    {
        float s = S;
        Name = "SurfaceTable";
        OffsetLeft = 232 * s; OffsetTop = 70 * s;
        AddThemeStyleboxOverride("panel", EditorStyle.Panel(s));
        var box = new VBoxContainer { CustomMinimumSize = new Vector2(620 * s, 0) };
        box.AddThemeConstantOverride("separation", (int)(6 * s));
        AddChild(box);
        var head = new HBoxContainer();
        var t = new Label { Text = "SURFACE TABLE", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        EditorStyle.Title(t, 18 * s);
        head.AddChild(t);
        head.AddChild(EditorStyle.Button("CLOSE", Close, s, 16));
        box.AddChild(head);
        var info = new Label
        {
            Text = "What each surface type does to a car on this map. GRIP scales every tyre force (stock ground 1.5 to 2.1; under 1 is ice). " +
                   "DAMAGE is hit points a second while a car drives forward faster than 7.65 m/s (17 mph). Saved with the map.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        EditorStyle.Body(info, 14 * s, dim: true);
        box.AddChild(info);

        var grid = new GridContainer { Columns = 6 };
        grid.AddThemeConstantOverride("h_separation", (int)(6 * s));
        grid.AddThemeConstantOverride("v_separation", (int)(4 * s));
        foreach (var h in new[] { "", "TYPE", "GRIP", "ROLLING", "BUMP", "DAMAGE/s" })
        {
            var l = new Label { Text = h, HorizontalAlignment = h is "TYPE" or "" ? HorizontalAlignment.Left : HorizontalAlignment.Right };
            EditorStyle.Body(l, 14 * s, dim: true);
            grid.AddChild(l);
        }
        var group = new ButtonGroup();
        for (int id = 0; id < SurfaceTable.Count; id++)
        {
            var info2 = SurfaceTypes.All[id];
            grid.AddChild(new ColorRect { Color = new Color(info2.R / 255f, info2.G / 255f, info2.B / 255f), CustomMinimumSize = new Vector2(18 * s, 18 * s), SizeFlagsVertical = SizeFlags.ShrinkCenter, MouseFilter = MouseFilterEnum.Ignore });
            int row = id;
            var nb = EditorStyle.Button("", () => Select(row), s, 17, toggle: true);
            nb.ButtonGroup = group; nb.Alignment = HorizontalAlignment.Left;
            nb.CustomMinimumSize = new Vector2(190 * s, 0);
            nb.ClipText = true;
            grid.AddChild(nb);
            _names[id] = nb;
            foreach (var f in Enum.GetValues<Field>())
            {
                var e = new LineEdit { CustomMinimumSize = new Vector2((f == Field.Damage ? 110 : 76) * s, 0), Alignment = HorizontalAlignment.Right, SelectAllOnFocus = true };
                e.AddThemeFontOverride("font", HudStyle.Bold);
                e.AddThemeFontSizeOverride("font_size", (int)(16 * s));
                e.AddThemeColorOverride("font_color", HudStyle.Parchment);
                e.AddThemeStyleboxOverride("normal", EditorStyle.Flat(new Color(0.06f, 0.04f, 0.025f, 0.95f), 5, s, new Color(HudStyle.Edge, 0.3f), 4));
                e.AddThemeStyleboxOverride("focus", EditorStyle.Flat(new Color(0.06f, 0.04f, 0.025f, 0.95f), 5, s, HudStyle.Amber, 4));
                var field = f;
                e.TextSubmitted += txt => { Commit(row, field, txt); GetViewport().GuiReleaseFocus(); };
                e.FocusExited += () => Commit(row, field, e.Text);
                grid.AddChild(e);
                _edits[id, (int)f] = e;
            }
        }
        box.AddChild(grid);

        var pl = new Label { Text = "PRESET FOR THE SELECTED TYPE" };
        EditorStyle.Body(pl, 14 * s, dim: true);
        box.AddChild(pl);
        var presets = new HFlowContainer();
        presets.AddThemeConstantOverride("h_separation", (int)(5 * s));
        presets.AddThemeConstantOverride("v_separation", (int)(5 * s));
        foreach (var p in SurfacePresets.All)
        {
            var preset = p;
            var b = EditorStyle.Button(p.Name, () => ApplyPreset(Selected(), preset), s, 16);
            b.TooltipText = p.Tip;
            presets.AddChild(b);
        }
        box.AddChild(presets);
        _note = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        EditorStyle.Body(_note, 13 * s, dim: true);
        box.AddChild(_note);
        MouseFilter = MouseFilterEnum.Stop;
        Refresh();
    }

    /// <summary>Applies preset <paramref name="p"/> to row <paramref name="id"/> as one undo step.</summary>
    public void ApplyPreset(int id, SurfacePreset p)
    {
        if (Doc.Surfaces is not { } table) return;
        var row = p.Apply(id, table[id]);
        if (row.Same(table[id])) return;
        Apply(table.With(id, row), $"surface {SurfaceTypes.All[id].Name.ToLowerInvariant()}: {p.Name.ToLowerInvariant()}");
    }

    private void Commit(int id, Field f, string text)
    {
        if (Doc.Surfaces is not { } table) return;
        var r = table[id];
        text = text.Trim().TrimEnd('/', 's').Trim();
        SurfaceRow row;
        if (f == Field.Damage)
        {
            if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int d) || d < 0) { Refresh(); return; }
            row = r with { Damage = d };
        }
        else
        {
            if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) || !float.IsFinite(v) || v < 0 || v > 100) { Refresh(); return; }
            row = f switch { Field.Grip => r with { Grip = v }, Field.Rolling => r with { Rolling = v }, _ => r with { Bump = v } };
        }
        if (row.Same(r)) { Refresh(); return; }
        Apply(table.With(id, row), $"surface {SurfaceTypes.All[id].Name.ToLowerInvariant()}: {f.ToString().ToLowerInvariant()}");
    }

    /// <summary>The fields show the document's table (after an edit, an undo, a pick).</summary>
    public void Refresh()
    {
        if (Doc.Surfaces is not { } table || !IsInstanceValid(_note)) return;
        int sel = Selected();
        for (int id = 0; id < SurfaceTable.Count; id++)
        {
            var r = table[id];
            _names[id].Text = $"{id}  {SurfacePresets.Label(id, r)}";
            _names[id].TooltipText = $"{SurfaceTypes.All[id].Tip}\n{SurfacePresets.Values(r)}{(r.Same(SurfaceTable.Stock[id]) ? "  (the stock row)" : $"\nstock: {SurfacePresets.Values(SurfaceTable.Stock[id])}")}";
            _names[id].SetPressedNoSignal(id == sel);
            string[] v = [r.Grip.ToString("0.###", CultureInfo.InvariantCulture), r.Rolling.ToString("0.###", CultureInfo.InvariantCulture),
                r.Bump.ToString("0.###", CultureInfo.InvariantCulture), r.Damage.ToString(CultureInfo.InvariantCulture)];
            for (int f = 0; f < 4; f++)
                if (!_edits[id, f].HasFocus()) _edits[id, f].Text = v[f];
        }
        _note.Text = $"Selected: {sel}  {SurfacePresets.Label(sel, table[sel])}, the surface brush's type. Paint it on the ground with the SURFACE tool. " +
                     "Tyre sounds and plants follow the type's id, not these values.";
    }
}
