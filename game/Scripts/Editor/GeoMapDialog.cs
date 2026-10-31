using System.Globalization;
using Godot;
using I76.Editor;
using I76.Editor.Geo;
using I76.Formats.Vfs;
using I76.Game.Boot;
using I76.Game.Net;

namespace I76.Game.Editor;

/// <summary>
/// New map from real world: an OpenStreetMap picker (GeoMapView) with a place search (Nominatim, on Enter, one request a second) and the
/// dialog that builds the chosen area into a map in the addon folder, in the background, with progress and Cancel. Capture hooks:
/// I76_GEO_VIEW=lat,lon,zoom, I76_GEO_BOX=latA,lonA,latB,lonB, I76_GEO_SEARCH=text, I76_GEO_BUILD=stem (builds on its own).
/// </summary>
public partial class GeoMapDialog : Control
{
    public required GameInstall Install { get; init; }
    /// <summary>The map is built and saved: its name (a stem).</summary>
    public required Action<string> Built { get; init; }
    public required Action Cancelled { get; init; }

    private GeoMapView _view = null!;
    private LineEdit _search = null!, _name = null!, _listed = null!;
    private ItemList _results = null!;
    private Label _area = null!, _status = null!, _error = null!, _hourLabel = null!;
    private Button _draw = null!, _build = null!, _cancel = null!;
    private Button[] _games = [], _roads = [];
    private HSlider _hour = null!;
    private ProgressBar _bar = null!;
    private readonly List<Control> _form = [];
    private GeoGame _game;
    private bool _allRoads;
    private float _s = 1;

    private readonly RateLimiter _limiter = new(GeoServices.NominatimInterval);
    private HttpRequest _http = null!;
    private string? _pendingSearch;
    private bool _searching;
    private List<Place> _places = [];
    private bool _nameEdited;

    private CancellationTokenSource? _cts;
    private volatile bool _running;
    private GeoProgress _progress;
    private readonly object _lock = new();
    private Exception? _failure;
    private string? _done;
    private bool _finished;
    private int _frames;

    public override void _Ready()
    {
        _s = Math.Clamp(GetViewportRect().Size.Y / 1080f, 0.8f, 2.4f);
        float s = _s;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;
        AddChild(new ColorRect { Color = new Color(0.05f, 0.03f, 0.02f, 0.9f), AnchorRight = 1, AnchorBottom = 1, MouseFilter = MouseFilterEnum.Ignore });
        var panel = new PanelContainer { AnchorRight = 1, AnchorBottom = 1 };
        var vp = GetViewportRect().Size;
        panel.OffsetLeft = vp.X * 0.025f; panel.OffsetRight = -vp.X * 0.025f; panel.OffsetTop = vp.Y * 0.035f; panel.OffsetBottom = -vp.Y * 0.035f;
        panel.AddThemeStyleboxOverride("panel", EditorStyle.Panel(s));
        AddChild(panel);
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", (int)(14 * s));
        panel.AddChild(row);

        // the map, with its zoom buttons over a corner
        var mapHolder = new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill, ClipContents = true };
        row.AddChild(mapHolder);
        string cache = ProjectSettings.GlobalizePath("user://cache/osm-tiles");
        _view = new GeoMapView { UserAgent = GeoServices.UserAgent(BuildInfo.Version), CacheDir = cache, AnchorRight = 1, AnchorBottom = 1 };
        mapHolder.AddChild(_view);
        var zoom = new VBoxContainer { Position = new Vector2(10 * s, 10 * s) };
        zoom.AddThemeConstantOverride("separation", (int)(4 * s));
        zoom.AddChild(EditorStyle.Button("+", () => _view.ZoomBy(1), s, 22));
        zoom.AddChild(EditorStyle.Button("-", () => _view.ZoomBy(-1), s, 22));
        mapHolder.AddChild(zoom);
        _view.BoxChanged += OnBox;

        var side = new VBoxContainer { CustomMinimumSize = new Vector2(400 * s, 0) };
        side.AddThemeConstantOverride("separation", (int)(8 * s));
        row.AddChild(side);
        var title = new Label { Text = "NEW MAP FROM REAL WORLD" };
        EditorStyle.Title(title, 26 * s);
        side.AddChild(title);
        var sub = new Label { Text = "Find a place, then drag the box over the area. The terrain and roads of OpenStreetMap become a map you can drive.", AutowrapMode = TextServer.AutowrapMode.WordSmart };
        EditorStyle.Body(sub, 14 * s, dim: true);
        side.AddChild(sub);

        _search = Edit("", 80, "Search a place, then Enter");
        _search.ClearButtonEnabled = true;
        _search.TextSubmitted += Search;
        side.AddChild(_search);
        _results = new ItemList { CustomMinimumSize = new Vector2(0, 100 * s), Visible = false, FocusMode = FocusModeEnum.None };
        _results.AddThemeFontOverride("font", HudStyle.Bold);
        _results.AddThemeFontSizeOverride("font_size", (int)(14 * s));
        _results.AddThemeColorOverride("font_color", HudStyle.Parchment);
        _results.AddThemeColorOverride("font_selected_color", HudStyle.Ink);
        _results.AddThemeStyleboxOverride("panel", EditorStyle.Flat(new Color(0.06f, 0.04f, 0.025f, 0.9f), 6, s));
        _results.AddThemeStyleboxOverride("selected", EditorStyle.Flat(HudStyle.Amber, 4, s));
        _results.AddThemeStyleboxOverride("selected_focus", EditorStyle.Flat(HudStyle.Amber, 4, s));
        _results.ItemSelected += i => Pick((int)i);
        _results.ItemActivated += i => Pick((int)i);
        side.AddChild(_results);

        _area = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(0, 52 * s) };
        EditorStyle.Body(_area, 15 * s);
        side.AddChild(_area);
        var boxRow = new HBoxContainer();
        boxRow.AddThemeConstantOverride("separation", (int)(6 * s));
        boxRow.AddChild(Add(EditorStyle.Button("BOX HERE", () => _view.BoxAtCenter(6), s, 18)));
        boxRow.AddChild(Add(EditorStyle.Button("SMALLER", () => _view.ScaleBox(0.8), s, 18)));
        boxRow.AddChild(Add(EditorStyle.Button("BIGGER", () => _view.ScaleBox(1.25), s, 18)));
        _draw = Add(EditorStyle.Button("DRAW", () => { _view.DrawMode = _draw.ButtonPressed; }, s, 18, toggle: true));
        _draw.TooltipText = "Drag on the map to draw a new box";
        boxRow.AddChild(_draw);
        side.AddChild(boxRow);

        side.AddChild(Field("Name (8 letters)", _name = Edit("geomap", 8)));
        side.AddChild(Field("Listed name (15)", _listed = Edit("Real World", 15)));
        _name.TextChanged += _ => _nameEdited = true;
        side.AddChild(Choice("Game", ["I76", "NITRO", "BOTH"], i => _game = (GeoGame)i, out _games));
        side.AddChild(Choice("Roads", ["MAJOR", "ALL"], i => _allRoads = i == 1, out _roads));
        _game = Install.IsNitro ? GeoGame.Nitro : GeoGame.I76;
        Mark(_games, (int)_game);
        Mark(_roads, 0);
        var hr = new HBoxContainer();
        hr.AddThemeConstantOverride("separation", (int)(8 * s));
        _hourLabel = new Label { CustomMinimumSize = new Vector2(150 * s, 0) };
        EditorStyle.Body(_hourLabel, 16 * s);
        _hour = new HSlider { MinValue = 0, MaxValue = 23, Step = 1, Value = 9, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        EditorStyle.Slider(_hour, s);
        _hour.ValueChanged += _ => SyncHour();
        hr.AddChild(_hourLabel); hr.AddChild(_hour);
        _form.Add(_hour);
        side.AddChild(hr);
        SyncHour();

        side.AddChild(new Control { SizeFlagsVertical = SizeFlags.ExpandFill });
        _status = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, Text = " " };
        EditorStyle.Body(_status, 15 * s, dim: true);
        side.AddChild(_status);
        _bar = new ProgressBar { MinValue = 0, MaxValue = 1, ShowPercentage = false, Visible = false, CustomMinimumSize = new Vector2(0, 12 * s) };
        _bar.AddThemeStyleboxOverride("background", EditorStyle.Flat(new Color(0.3f, 0.18f, 0.1f, 0.9f), 3, s));
        _bar.AddThemeStyleboxOverride("fill", EditorStyle.Flat(HudStyle.Amber, 3, s));
        side.AddChild(_bar);
        _error = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, Text = "" };
        EditorStyle.Body(_error, 15 * s, colour: HudStyle.Danger);
        side.AddChild(_error);
        var hint = new Label { Text = "Drag: pan.  Wheel, + / -, bumpers: zoom.  Arrows or left stick: pan.", AutowrapMode = TextServer.AutowrapMode.WordSmart };
        EditorStyle.Body(hint, 12 * s, dim: true);
        side.AddChild(hint);
        var buttons = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End };
        buttons.AddThemeConstantOverride("separation", (int)(10 * s));
        _build = EditorStyle.Button("BUILD", StartBuild, s, 22);
        _cancel = EditorStyle.Button("CANCEL", OnCancel, s, 22);
        buttons.AddChild(_build); buttons.AddChild(_cancel);
        side.AddChild(buttons);

        _http = new HttpRequest { Timeout = 15 };
        _http.RequestCompleted += OnSearched;
        AddChild(_http);

        _view.SetView(GeoMapView.TripLat, GeoMapView.TripLon, GeoMapView.TripZoom);
        OnBox();
        _view.GrabFocus();
    }

    // ---- widgets --------------------------------------------------------------------------------------------------------------------

    private LineEdit Edit(string text, int max, string placeholder = "")
    {
        float s = _s;
        var e = new LineEdit { Text = text, MaxLength = max, PlaceholderText = placeholder, SizeFlagsHorizontal = SizeFlags.ExpandFill, SelectAllOnFocus = true };
        e.AddThemeFontOverride("font", HudStyle.Bold);
        e.AddThemeFontSizeOverride("font_size", (int)(17 * s));
        e.AddThemeColorOverride("font_color", HudStyle.Parchment);
        e.AddThemeStyleboxOverride("normal", EditorStyle.Flat(new Color(0.06f, 0.04f, 0.025f, 0.95f), 5, s, new Color(HudStyle.Edge, 0.3f), 6));
        e.AddThemeStyleboxOverride("focus", EditorStyle.Flat(new Color(0.06f, 0.04f, 0.025f, 0.95f), 5, s, HudStyle.Amber, 6));
        _form.Add(e);
        return e;
    }

    private T Add<T>(T c) where T : Control { _form.Add(c); return c; }

    private Control Field(string label, LineEdit e)
    {
        var r = new HBoxContainer();
        r.AddThemeConstantOverride("separation", (int)(8 * _s));
        var l = new Label { Text = label, CustomMinimumSize = new Vector2(150 * _s, 0) };
        EditorStyle.Body(l, 16 * _s);
        r.AddChild(l); r.AddChild(e);
        return r;
    }

    private Control Choice(string label, string[] names, Action<int> pick, out Button[] buttons)
    {
        var r = new HBoxContainer();
        r.AddThemeConstantOverride("separation", (int)(6 * _s));
        var l = new Label { Text = label, CustomMinimumSize = new Vector2(150 * _s, 0) };
        EditorStyle.Body(l, 16 * _s);
        r.AddChild(l);
        var group = new ButtonGroup();
        var bs = new Button[names.Length];
        for (int i = 0; i < names.Length; i++)
        {
            int k = i;
            bs[i] = Add(EditorStyle.Button(names[i], () => pick(k), _s, 18, toggle: true));
            bs[i].ButtonGroup = group;
            r.AddChild(bs[i]);
        }
        buttons = bs;
        return r;
    }

    private static void Mark(Button[] bs, int i) { for (int k = 0; k < bs.Length; k++) bs[k].SetPressedNoSignal(k == i); }

    private void SyncHour() => _hourLabel.Text = $"Hour of day {(int)_hour.Value}:00";

    private void OnBox()
    {
        var c = _view.Check;
        if (c is null) { _area.Text = "No area yet. Press BOX HERE, or turn on DRAW and drag over the map."; EditorStyle.Body(_area, 15 * _s, dim: true); }
        else { _area.Text = c.Message; EditorStyle.Body(_area, 15 * _s, colour: c.Fits ? HudStyle.Parchment : HudStyle.Danger); }
    }

    // ---- place search (Nominatim: on Enter only, one request a second) ----------------------------------------------------------

    private void Search(string text)
    {
        text = text.Trim();
        if (text.Length < 2) return;
        _pendingSearch = text;
        _status.Text = "Searching...";
    }

    private void RunSearch(string q)
    {
        var err = _http.Request(GeoServices.SearchUrl(q), ["User-Agent: " + GeoServices.UserAgent(BuildInfo.Version), "Accept: application/json"]);
        if (err != Error.Ok) { _status.Text = "Could not search."; return; }
        _searching = true;
    }

    private void OnSearched(long result, long code, string[] headers, byte[] body)
    {
        _searching = false;
        if (result != (long)HttpRequest.Result.Success) { _status.Text = "The place search could not be reached (offline?). You can still pan the map by hand."; return; }
        if (code != 200) { _status.Text = code == 429 ? "The place search asks us to slow down: try again in a minute." : $"The place search answered HTTP {code}."; return; }
        try { _places = Nominatim.Parse(System.Text.Encoding.UTF8.GetString(body)); }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidDataException) { _status.Text = "The place search gave an answer that could not be read."; return; }
        _results.Clear();
        foreach (var p in _places) _results.AddItem(p.Name);
        _results.Visible = _places.Count > 0;
        _status.Text = _places.Count == 0 ? "No such place found." : _places.Count == 1 ? "1 place found." : $"{_places.Count} places found: pick one.";
        if (_places.Count > 0) { _results.Select(0); Pick(0); }
    }

    private void Pick(int i)
    {
        if (i < 0 || i >= _places.Count) return;
        var p = _places[i];
        if (p.North - p.South < 1e-4) _view.SetView(p.Lat, p.Lon, 14);
        else _view.FitBounds(p.South, p.West, p.North, p.East);
        if (!_nameEdited)
        {
            string head = p.Name.Split(',')[0].Trim();
            string stem = GeoMapOutput.StemOf(head);
            if (stem.Length > 0) _name.Text = stem;
            _listed.Text = new string(head.Where(c => c is >= ' ' and <= '~').ToArray()) is { Length: > 0 } l ? (l.Length > MapSaver.MaxDisplayName ? l[..MapSaver.MaxDisplayName] : l) : "Real World";
        }
    }

    // ---- building ---------------------------------------------------------------------------------------------------------------------

    private void StartBuild()
    {
        if (_running) return;
        _error.Text = "";
        string stem = _name.Text.Trim(), listed = _listed.Text.Trim();
        if (_view.Box is not { } b || _view.Check is not { } ck) { _error.Text = "Choose an area first: press BOX HERE, or turn on DRAW and drag over the map."; return; }
        if (!ck.Fits) { _error.Text = ck.Message; return; }
        if (Install.AddonDir is null) { _error.Text = "The install has no addon folder to save a map to."; return; }
        if (!MapSaver.ValidName(stem)) { _error.Text = "The name is 1 to 8 letters, digits and underscores."; return; }
        if (!MapSaver.ValidDisplayName(listed)) { _error.Text = $"The listed name is 1 to {MapSaver.MaxDisplayName} characters."; return; }
        if (Install.FindMission(stem) is not null) { _error.Text = "A map of that name exists. Pick another name."; return; }
        var req = new GeoMapRequest(b.S, b.W, b.N, b.E, stem.ToLowerInvariant(), listed, _game, _allRoads, (int)_hour.Value, Install.AddonDir);
        string cache = ProjectSettings.GlobalizePath("user://cache/geo");
        var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(150) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(GeoServices.UserAgent(BuildInfo.Version));
        var fetch = new GeoFetch(cache, http) { OverpassRounds = 3, Log = s => GD.Print("geo: " + s) };
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _running = true; _finished = false; _failure = null; _done = null;
        SetBusy(true);
        _progress = new GeoProgress(GeoStage.Checking, "Starting", 0);
        var install = Install;
        Task.Run(() =>
        {
            try { var r = GeoMapJob.Run(req, fetch, install, p => { lock (_lock) _progress = p; }, ct); lock (_lock) { _done = r; } }
            catch (Exception ex) { lock (_lock) _failure = ex; if (ex is not OperationCanceledException) GD.Print($"geo map: {ex}"); }
            finally { http.Dispose(); lock (_lock) _finished = true; }
        });
    }

    private void SetBusy(bool busy)
    {
        foreach (var c in _form) { if (c is Button bt) bt.Disabled = busy; else if (c is LineEdit le) le.Editable = !busy; else if (c is HSlider sl) sl.Editable = !busy; }
        _bar.Visible = busy;
        _build.Disabled = busy;
        _cancel.Text = busy ? "STOP" : "CANCEL";
    }

    private void OnCancel()
    {
        if (_running) { _cts?.Cancel(); _status.Text = "Stopping..."; return; }
        Cancelled();
    }

    public override void _Process(double delta)
    {
        _frames++;
        if (_pendingSearch is { } q && !_searching && _limiter.Wait(DateTime.UtcNow) == TimeSpan.Zero && _limiter.TryAcquire(DateTime.UtcNow)) { _pendingSearch = null; RunSearch(q); }
        if (_running)
        {
            GeoProgress p; bool fin; Exception? fail; string? done;
            lock (_lock) { p = _progress; fin = _finished; fail = _failure; done = _done; }
            _status.Text = p.Text; _bar.Value = p.Fraction;
            if (fin)
            {
                _running = false;
                SetBusy(false);
                if (done is not null) Built(done);
                else if (fail is not null)
                {
                    _status.Text = " ";
                    _error.Text = GeoMapJob.Explain(fail);
                    _build.Text = fail is OperationCanceledException ? "BUILD" : "RETRY";
                }
            }
        }
        HookTick();
    }

    public override void _Input(InputEvent e)
    {
        if (e is InputEventJoypadButton { Pressed: true } jb)
        {
            switch (jb.ButtonIndex)
            {
                case JoyButton.LeftShoulder: _view.ZoomBy(-1); break;
                case JoyButton.RightShoulder: _view.ZoomBy(1); break;
                case JoyButton.A: _view.BoxAtCenter(6); break;
                case JoyButton.X: _view.ScaleBox(1.25); break;
                case JoyButton.Y: _view.ScaleBox(0.8); break;
                case JoyButton.B: OnCancel(); break;
                case JoyButton.Start: StartBuild(); break;
                default: return;
            }
            GetViewport().SetInputAsHandled();
        }
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (e is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape }) { GetViewport().SetInputAsHandled(); OnCancel(); }
    }

    public override void _ExitTree() { _cts?.Cancel(); }

    // ---- capture hooks ------------------------------------------------------------------------------------------------------------

    private static double[]? Nums(string name, int n)
    {
        if (OS.GetEnvironment(name) is not { Length: > 0 } v) return null;
        var d = v.Split(',').Select(x => double.TryParse(x, NumberStyles.Float, CultureInfo.InvariantCulture, out double r) ? r : double.NaN).ToArray();
        return d.Length == n && d.All(double.IsFinite) ? d : null;
    }

    private void Hooks()
    {
        if (Nums("I76_GEO_VIEW", 3) is { } v) _view.SetView(v[0], v[1], (int)v[2]);
        if (Nums("I76_GEO_BOX", 4) is { } b) { _view.SetBox(b[0], b[1], b[2], b[3]); _view.FitBounds(Math.Min(b[0], b[2]), Math.Min(b[1], b[3]), Math.Max(b[0], b[2]), Math.Max(b[1], b[3])); }
        if (OS.GetEnvironment("I76_GEO_SEARCH") is { Length: > 0 } q) { _search.Text = q; Search(q); }
        if (OS.GetEnvironment("I76_GEO_BUILD") is { Length: > 0 } n) { _name.Text = n; _listed.Text = n; }
    }

    private void HookTick()
    {
        if (_frames == 3) Hooks();   // after the first layout: the view has a size to fit to
        if (_frames == 400 && !_running && OS.GetEnvironment("I76_GEO_BUILD") is { Length: > 0 }) StartBuild();
    }
}
