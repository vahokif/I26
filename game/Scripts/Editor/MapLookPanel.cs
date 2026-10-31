using Godot;
using I76.Editor;
using I76.Formats.Imaging;
using I76.Formats.Mission;
using I76.Game.Net;
using I76.Game.World;

namespace I76.Game.Editor;

/// <summary>
/// The map style window (docs/map-editor.md "Map style"): every ground, sky and horizon the install's archive holds
/// (<see cref="MapLookCatalog"/>), each with a small picture, and the hour. The palette is not offered: it follows the 8-bit ground,
/// sky or horizon picked (<see cref="MapLookCatalog.Suited"/>), the only things it colours. Picking marks a choice; APPLY puts the lot into the map
/// as one undo step, and the world is rebuilt from it (<see cref="MapEditor.ApplyLook"/>) so both looks, the sky's fog, the horizon
/// ring and the plants follow. Pictures are made a few a frame, the tab on show first, and kept for the session.
/// </summary>
public partial class MapLookPanel : PanelContainer
{
    public required TerrainDocument Doc { get; init; }
    public required MissionBuilder Builder { get; init; }
    public required float S { get; init; }
    /// <summary>The look to apply, and a name for the undo list.</summary>
    public required Action<MapLook, string> Apply { get; init; }
    public required Action<int> TabChanged { get; init; }
    public int Tab { get; init; }

    private enum Part { Ground, Sky, Horizon, Time }
    private static readonly string[] TabNames = ["GROUND", "SKY", "HORIZON", "TIME"];

    /// <summary>The (hour, name) of the engine's eight times of day (3-hour bins; 0 and 3 are the night presets, the moon).</summary>
    public static readonly (int Hour, string Name)[] Hours =
    [
        (0, "00:00  night, bright moon"), (3, "03:00  night, dim moon"), (6, "06:00  sunrise, low sun"), (9, "09:00  morning"),
        (12, "12:00  noon, sun overhead"), (15, "15:00  afternoon"), (18, "18:00  evening"), (21, "21:00  sunset, low sun"),
    ];

    private static MapLookCatalog? _catalog;
    private static string? _catalogFor;
    private static readonly Dictionary<string, Texture2D?> Thumbs = [];

    private MapLook _pending = null!;
    private Part _part;
    private ItemList _list = null!;
    private Label _info = null!, _note = null!;
    private Button _apply = null!, _revert = null!;
    private readonly Dictionary<Part, Button> _tabs = [];
    private List<LookChoice?> _shown = [];
    private readonly Queue<(int Index, string Key, Func<Texture2D?> Make)> _jobs = new();

    private I76.Formats.Vfs.AssetVfs Vfs => Builder.Vfs;

    public override void _Ready()
    {
        float s = S;
        _pending = Doc.Look ?? throw new InvalidOperationException("the map has no WRLD look");
        _part = (Part)Math.Clamp(Tab, 0, TabNames.Length - 1);
        Name = "MapStyle";
        OffsetLeft = 232 * s; OffsetTop = 70 * s;
        AddThemeStyleboxOverride("panel", EditorStyle.Panel(s));
        var box = new VBoxContainer { CustomMinimumSize = new Vector2(500 * s, 0) };
        box.AddThemeConstantOverride("separation", (int)(6 * s));
        AddChild(box);
        var t = new Label { Text = "MAP STYLE" };
        EditorStyle.Title(t, 18 * s);
        box.AddChild(t);
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", (int)(5 * s));
        var group = new ButtonGroup();
        foreach (var p in Enum.GetValues<Part>())
        {
            var b = EditorStyle.Button(TabNames[(int)p], () => Show(p), s, 18, toggle: true);
            b.ButtonGroup = group; b.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            row.AddChild(b);
            _tabs[p] = b;
        }
        box.AddChild(row);
        _info = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(0, 40 * s) };
        EditorStyle.Body(_info, 15 * s);
        box.AddChild(_info);
        _list = new ItemList
        {
            SizeFlagsVertical = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 400 * s), SameColumnWidth = true, MaxTextLines = 2, AllowReselect = true,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
        };
        _list.AddThemeFontOverride("font", HudStyle.Bold);
        _list.AddThemeFontSizeOverride("font_size", (int)(14 * s));
        _list.AddThemeColorOverride("font_color", HudStyle.Parchment);
        _list.AddThemeColorOverride("font_selected_color", HudStyle.Ink);
        _list.AddThemeColorOverride("font_hovered_color", HudStyle.Amber);
        _list.AddThemeStyleboxOverride("panel", EditorStyle.Flat(new Color(0.06f, 0.04f, 0.025f, 0.6f), 5, s, null, 4));
        _list.AddThemeStyleboxOverride("selected", EditorStyle.Flat(HudStyle.Amber, 5, s));
        _list.AddThemeStyleboxOverride("selected_focus", EditorStyle.Flat(HudStyle.Amber, 5, s));
        _list.AddThemeStyleboxOverride("hovered", EditorStyle.Flat(new Color(0.34f, 0.18f, 0.09f, 0.9f), 5, s));
        _list.AddThemeConstantOverride("icon_margin", (int)(4 * s));
        _list.ItemClicked += (i, _, _) => Pick((int)i);
        _list.ItemSelected += i => Pick((int)i);
        box.AddChild(_list);
        _note = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        EditorStyle.Body(_note, 13 * s, dim: true);
        box.AddChild(_note);
        var act = new HBoxContainer();
        act.AddThemeConstantOverride("separation", (int)(8 * s));
        _apply = EditorStyle.Button("APPLY", () => Apply(_pending, Summary()), s, 20);
        _revert = EditorStyle.Button("REVERT", () => { _pending = Doc.Look!; Refresh(); }, s, 20);
        act.AddChild(_apply); act.AddChild(_revert);
        box.AddChild(act);
        Show(_part);
        MouseFilter = MouseFilterEnum.Stop;
    }

    private MapLookCatalog Catalog => CatalogFor(Vfs);

    /// <summary>The install's catalog, scanned once a session.</summary>
    public static MapLookCatalog CatalogFor(I76.Formats.Vfs.AssetVfs vfs)
    {
        string root = vfs.Install.Root;
        if (_catalog is null || _catalogFor != root) { _catalog = MapLookCatalog.Scan(vfs); _catalogFor = root; }
        return _catalog;
    }

    private string Summary()
    {
        var a = Doc.Look!;
        var parts = new List<string>();
        if (!_pending.Ground.Equals(a.Ground, StringComparison.OrdinalIgnoreCase)) parts.Add("ground");
        if (!_pending.Sky.Equals(a.Sky, StringComparison.OrdinalIgnoreCase)) parts.Add("sky");
        if (!_pending.Horizon.Equals(a.Horizon, StringComparison.OrdinalIgnoreCase)) parts.Add("horizon");
        if (_pending.Hour != a.Hour) parts.Add("time of day");
        return "map style: " + string.Join(", ", parts);
    }

    private void Show(Part p)
    {
        _part = p;
        foreach (var (k, b) in _tabs) b.SetPressedNoSignal(k == p);
        TabChanged((int)p);
        _jobs.Clear();
        float s = S;
        var cat = Catalog;
        _list.Clear();
        _shown = [];
        switch (p)
        {
            case Part.Ground: Fill(cat.Grounds, new Vector2I((int)(88 * s), (int)(88 * s)), 112, c => GroundPic(c, PaletteOf(c))); break;
            case Part.Sky: Fill(cat.Skies, new Vector2I((int)(120 * s), (int)(72 * s)), 142, c => SkyPic(c, PaletteOf(c))); break;
            case Part.Horizon:
                _shown.Add(null);
                Fill(cat.Horizons, new Vector2I((int)(380 * s), (int)(50 * s)), 0, c => HorizonPic(c, PaletteOf(c)), none: true);
                break;
            case Part.Time: FillTime(); break;
        }
        _info.Text = p switch
        {
            Part.Ground => "The ground family the terrain is drawn with (the WRLD's ground field; the engine takes its largest member and the nearer ones' mips).",
            Part.Sky => "The cloud texture on the sky dome. The modern look's sky colours, fog and the haze of the distant ground are taken from it.",
            Part.Horizon => "The 16 mountain panels round the horizon (an .hzd list). The modern look builds its 3D distant terrain from their skyline.",
            _ => "The time of day: which sun or moon, at what angle and strength. Night also gives the cars headlights.",
        };
        _note.Text = p == Part.Horizon && !Doc.HorizonEditable ? "This level is an old WREV 8 file: it has no horizon field." : "Click to choose, APPLY to put it in the map (one undo step; the map is rebuilt, a few seconds, every edit kept).";
        Refresh();
    }

    private static string Short(string name) => Path.GetFileNameWithoutExtension(name);

    private void Fill(IReadOnlyList<LookChoice> choices, Vector2I icon, int column, Func<LookChoice, Func<Texture2D?>> make, bool none = false)
    {
        _list.IconMode = ItemList.IconModeEnum.Top;
        _list.FixedIconSize = icon;
        _list.FixedColumnWidth = column == 0 ? 0 : (int)(column * S);
        _list.MaxColumns = column == 0 ? 1 : 0;
        if (none)
        {
            int i = _list.AddItem("none", Placeholder(icon));
            _list.SetItemTooltip(i, "no horizon panels: the sky alone");
        }
        foreach (var c in choices)
        {
            _shown.Add(c);
            int i = _list.AddItem(Short(c.Name), Placeholder(icon));
            _list.SetItemTooltip(i, c.Tip);
            string key = _part + ":" + c.Name + ":" + PaletteOf(c);
            if (Thumbs.TryGetValue(key, out var have) && have is not null) _list.SetItemIcon(i, have);
            else if (!Thumbs.ContainsKey(key)) { var mk = make(c); _jobs.Enqueue((i, key, mk)); }
        }
    }

    private void FillTime()
    {
        _list.IconMode = ItemList.IconModeEnum.Left;
        _list.FixedIconSize = new Vector2I(1, 1);
        _list.FixedColumnWidth = 0; _list.MaxColumns = 1;
        foreach (var (h, name) in Hours)
        {
            _shown.Add(null);
            int i = _list.AddItem(name);
            _list.SetItemTooltip(i, $"WRLD hour {h}");
        }
    }

    private static Texture2D Placeholder(Vector2I size)
    {
        var img = Image.CreateEmpty(Math.Max(1, size.X), Math.Max(1, size.Y), false, Image.Format.Rgba8);
        img.Fill(new Color(0.14f, 0.09f, 0.06f));
        return ImageTexture.CreateFromImage(img);
    }

    /// <summary>The choice the pending look holds in the tab on show (its list index), or -1.</summary>
    private int IndexOfPending()
    {
        string cur = _part switch { Part.Ground => _pending.Ground, Part.Sky => _pending.Sky, Part.Horizon => _pending.Horizon, _ => "" };
        if (_part == Part.Time) return Array.FindIndex(Hours, h => Lighting.ForHour(h.Hour).Index == Lighting.ForHour(_pending.Hour).Index);
        if (_part == Part.Horizon && cur.Length == 0) return 0;
        for (int i = 0; i < _shown.Count; i++)
            if (_shown[i] is { } c && (c.Name.Equals(cur, StringComparison.OrdinalIgnoreCase) || c.Same?.Contains(cur, StringComparer.OrdinalIgnoreCase) == true)) return i;
        return -1;
    }

    private void Pick(int i)
    {
        if (i < 0 || i >= _list.ItemCount) return;
        var look = _pending;
        switch (_part)
        {
            case Part.Ground when _shown[i] is { } g: look = look with { Ground = g.Name }; break;
            case Part.Sky when _shown[i] is { } k: look = look with { Sky = k.Name }; break;
            case Part.Horizon when !Doc.HorizonEditable: break;
            case Part.Horizon: look = look with { Horizon = _shown[i]?.Name ?? "" }; break;
            case Part.Time:
                // keep the hour as it is when it already is in the chosen preset
                if (Lighting.ForHour(Hours[i].Hour).Index != Lighting.ForHour(look.Hour).Index) look = look with { Hour = Hours[i].Hour };
                break;
        }
        // an 8-bit ground, sky or horizon brings a palette made for it
        _pending = _part == Part.Time ? look : Catalog.Suited(look);
        Refresh();
    }

    private void Refresh()
    {
        if (!IsInstanceValid(_list)) return;
        int at = IndexOfPending();
        _list.DeselectAll();
        if (at >= 0 && at < _list.ItemCount) { _list.Select(at); _list.EnsureCurrentIsVisible(); }
        bool changed = !_pending.Equals(Doc.Look);
        _apply.Disabled = !changed; _revert.Disabled = !changed;
        _apply.Text = changed ? "APPLY" : "APPLIED";
    }

    public override void _Process(double delta)
    {
        for (int n = 0; n < 2 && _jobs.Count > 0; n++)
        {
            var (i, key, make) = _jobs.Dequeue();
            Texture2D? tex = null;
            try { tex = make(); } catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException or KeyNotFoundException) { GD.Print($"map style: {key}: {e.Message}"); }
            Thumbs[key] = tex;
            if (tex is not null && i < _list.ItemCount) _list.SetItemIcon(i, tex);
        }
    }

    // ---- the pictures --------------------------------------------------------------------------------------------------------------

    private Palette? PaletteFor(string name) => Vfs.TryRead(name) is { Length: Palette.Size } d ? new Palette(d) : null;

    /// <summary>The palette a choice is shown through: an 8-bit one's own (as picking it would set), else the pending look's.</summary>
    private string PaletteOf(LookChoice c) => c.Palettes is { Count: > 0 } p ? p[0] : _pending.Palette;

    /// <summary>A texture member as a picture (8-bit ones through <paramref name="palette"/>).</summary>
    private Image? Load(string stem, string palette)
    {
        var resolver = Builder.Resolver!;
        if (resolver.FindByStem(stem) is not { } member || resolver.Load(member) is not { } t) return null;
        var rgba = TextureReader.ToRgba(t, PaletteFor(palette));
        return Image.CreateFromData(t.Width, t.Height, false, Image.Format.Rgba8, rgba);
    }

    private Func<Texture2D?> GroundPic(LookChoice c, string pal) => () => Scaled(Load(c.Name, pal), 88, 88);
    private Func<Texture2D?> SkyPic(LookChoice c, string pal) => () => Scaled(Load(c.Name, pal), 120, 72);

    private Func<Texture2D?> HorizonPic(LookChoice c, string pal) => () =>
    {
        // the 16 panels side by side over the sky's blue (the panels are keyed where the sky shows through)
        const int w = 380, h = 50;
        var strip = Image.CreateEmpty(w, h, false, Image.Format.Rgba8);
        strip.Fill(new Color(0.55f, 0.72f, 0.9f));
        for (int i = 0; i < c.Panels!.Length; i++)
        {
            if (Load(c.Panels[i], pal) is not { } im) continue;
            im.FlipY();   // the panels are stored upside down (the sky shader flips them)
            im.Resize(w / c.Panels.Length + 1, h, Image.Interpolation.Bilinear);
            strip.BlendRect(im, new Rect2I(0, 0, im.GetWidth(), im.GetHeight()), new Vector2I(i * w / c.Panels.Length, 0));
        }
        return ImageTexture.CreateFromImage(strip);
    };

    private static Texture2D? Scaled(Image? img, int w, int h)
    {
        if (img is null) return null;
        if (img.IsCompressed()) img.Decompress();
        img.Resize(w, h, Image.Interpolation.Bilinear);
        return ImageTexture.CreateFromImage(img);
    }
}
