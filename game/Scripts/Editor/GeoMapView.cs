using Godot;
using I76.Editor.Geo;
using I76.Game.Net;

namespace I76.Game.Editor;

/// <summary>
/// A slippy map drawn by the game: OpenStreetMap raster tiles (GeoServices: the tile usage policy's User-Agent, disk cache, two connections
/// at most, only the tiles on screen) with drag-to-pan, wheel zoom, and a resizable box for the area to make a map of.
/// </summary>
public partial class GeoMapView : Control
{
    public string TileTemplate { get; init; } = GeoServices.TileUrlTemplate;
    public required string UserAgent { get; init; }
    public required string CacheDir { get; init; }
    /// <summary>
    /// Where the picker opens: the middle of the game's trip (Seagraves, Texas, over Highway 380 to Tatum, New Mexico, down across the
    /// Pecos to Fort Davis), at a zoom that shows all of it.
    /// </summary>
    public const double TripLat = 31.93, TripLon = -103.22;
    public const int TripZoom = 8;
    public double CenterLat = TripLat, CenterLon = TripLon;
    public int Zoom = 9;
    /// <summary>The chosen area (south, west, north, east), or null.</summary>
    public (double S, double W, double N, double E)? Box { get; private set; }
    public AreaCheck? Check { get; private set; }
    /// <summary>With this on, a drag on the map draws a new box instead of panning.</summary>
    public bool DrawMode;
    public event Action? BoxChanged;
    public event Action? ViewChanged;
    /// <summary>The tiles that could not be fetched lately (the dialog says the map is offline).</summary>
    public int Failures => _failures;

    private sealed class Tile { public ImageTexture? Tex; public ulong Used; public double RetryAt; public bool Queued, Stale, Loaded; }
    private readonly Dictionary<(int Z, int X, int Y), Tile> _tiles = [];
    private readonly List<(int Z, int X, int Y)> _queue = [];
    private readonly HttpRequest[] _http = new HttpRequest[GeoServices.MaxConnections];
    private readonly (int Z, int X, int Y)?[] _busy = new (int, int, int)?[GeoServices.MaxConnections];
    private double _backoffUntil;
    private int _failures;
    private float _s = 1, _ts = 256;
    private enum Drag { None, Pan, Draw, Resize }
    private Drag _drag;
    private bool _dragW, _dragE, _dragS, _dragN;
    private (double Lat, double Lon) _anchor;
    private Vector2 _mouse;

    private static double Now => Time.GetTicksMsec() / 1000.0;

    public override void _Ready()
    {
        _s = Math.Clamp(GetViewportRect().Size.Y / 1080f, 0.8f, 2.4f);
        _ts = 256 * Math.Max(1f, _s);
        ClipContents = true;
        FocusMode = FocusModeEnum.All;
        MouseDefaultCursorShape = CursorShape.Drag;
        for (int i = 0; i < _http.Length; i++)
        {
            var h = new HttpRequest { Timeout = 15, DownloadChunkSize = 65536 };
            int slot = i;
            h.RequestCompleted += (result, code, _, body) => OnTile(slot, (HttpRequest.Result)result, (int)code, body);
            AddChild(h);
            _http[i] = h;
        }
    }

    // ---- projection ----------------------------------------------------------------------------------------------------------

    private Vector2 ToScreen(double lat, double lon)
    {
        var (cx, cy) = Slippy.ToTile(CenterLat, CenterLon, Zoom);
        var (x, y) = Slippy.ToTile(lat, lon, Zoom);
        return Size / 2 + new Vector2((float)((x - cx) * _ts), (float)((y - cy) * _ts));
    }

    private (double Lat, double Lon) FromScreen(Vector2 p)
    {
        var (cx, cy) = Slippy.ToTile(CenterLat, CenterLon, Zoom);
        var d = p - Size / 2;
        var (lat, lon) = Slippy.FromTile(cx + d.X / _ts, cy + d.Y / _ts, Zoom);
        return (lat, Slippy.WrapLon(lon));
    }

    public void SetView(double lat, double lon, int zoom)
    {
        CenterLat = Math.Clamp(lat, -Slippy.MaxLat, Slippy.MaxLat); CenterLon = Slippy.WrapLon(lon);
        Zoom = Math.Clamp(zoom, GeoServices.MinZoom, GeoServices.MaxZoom);
        ViewChanged?.Invoke(); QueueRedraw();
    }

    public void PanBy(Vector2 pixels)
    {
        var (cx, cy) = Slippy.ToTile(CenterLat, CenterLon, Zoom);
        var (lat, lon) = Slippy.FromTile(cx - pixels.X / _ts, cy - pixels.Y / _ts, Zoom);
        SetView(lat, lon, Zoom);
    }

    /// <summary>Zooms by whole levels keeping the point under <paramref name="at"/> where it is.</summary>
    public void ZoomBy(int delta, Vector2? at = null)
    {
        int z = Math.Clamp(Zoom + delta, GeoServices.MinZoom, GeoServices.MaxZoom);
        if (z == Zoom) return;
        var p = at ?? Size / 2;
        var (lat, lon) = FromScreen(p);
        Zoom = z;
        // put (lat, lon) back under p: shift the centre by the difference
        var now = ToScreen(lat, lon);
        PanBy(p - now);
        ViewChanged?.Invoke(); QueueRedraw();
    }

    /// <summary>Shows a box of the given size (degrees) with room round it.</summary>
    public void FitBounds(double south, double west, double north, double east)
    {
        int z = Slippy.ZoomToFit(Math.Max(Math.Abs(east - west), 1e-6), Math.Max(100, Size.X * 0.6f), _ts);
        double fh(int zz) => Math.Abs(Slippy.ToTile(south, east, zz).Y - Slippy.ToTile(north, west, zz).Y) * _ts;
        while (z > GeoServices.MinZoom && fh(z) > Size.Y * 0.6f) z--;
        SetView((south + north) / 2, (west + east) / 2, z);
    }

    // ---- the box -----------------------------------------------------------------------------------------------------------------

    public void SetBox(double south, double west, double north, double east)
    {
        if (south > north) (south, north) = (north, south);
        if (west > east) (west, east) = (east, west);
        Box = (Math.Clamp(south, -Slippy.MaxLat, Slippy.MaxLat), west, Math.Clamp(north, -Slippy.MaxLat, Slippy.MaxLat), east);
        Check = AreaCheck.Of(Box.Value.S, Box.Value.W, Box.Value.N, Box.Value.E);
        BoxChanged?.Invoke(); QueueRedraw();
    }

    public void ClearBox() { Box = null; Check = null; BoxChanged?.Invoke(); QueueRedraw(); }

    /// <summary>A box of <paramref name="km"/> a side round the middle of the view.</summary>
    public void BoxAtCenter(double km)
    {
        var proj = new GeoProjection(CenterLat, CenterLon);
        var (n, e) = proj.ToLatLon(km * 500, km * 500);
        var (s, w) = proj.ToLatLon(-km * 500, -km * 500);
        SetBox(s, w, n, e);
        FitBounds(s, w, n, e);
    }

    public void ScaleBox(double factor)
    {
        if (Box is not { } b) return;
        double la = (b.S + b.N) / 2, lo = (b.W + b.E) / 2;
        SetBox(la - (la - b.S) * factor, lo - (lo - b.W) * factor, la + (b.N - la) * factor, lo + (b.E - lo) * factor);
    }

    public void CenterOnBox() { if (Box is { } b) SetView((b.S + b.N) / 2, (b.W + b.E) / 2, Zoom); }

    // ---- input ---------------------------------------------------------------------------------------------------------------------

    private (bool W, bool E, bool S, bool N)? HandleAt(Vector2 p)
    {
        if (Box is not { } b) return null;
        var a = ToScreen(b.N, b.W); var c = ToScreen(b.S, b.E);
        float r = 12 * _s, mx = (a.X + c.X) / 2, my = (a.Y + c.Y) / 2;
        (float X, float Y, bool W, bool E, bool S, bool N)[] hs =
        [
            (a.X, a.Y, true, false, false, true), (c.X, a.Y, false, true, false, true), (a.X, c.Y, true, false, true, false), (c.X, c.Y, false, true, true, false),
            (mx, a.Y, false, false, false, true), (mx, c.Y, false, false, true, false), (a.X, my, true, false, false, false), (c.X, my, false, true, false, false),
        ];
        foreach (var h in hs) if (Math.Abs(p.X - h.X) <= r && Math.Abs(p.Y - h.Y) <= r) return (h.W, h.E, h.S, h.N);
        return null;
    }

    public override void _GuiInput(InputEvent e)
    {
        switch (e)
        {
            case InputEventMouseButton { ButtonIndex: MouseButton.WheelUp, Pressed: true } w: ZoomBy(1, w.Position); AcceptEvent(); break;
            case InputEventMouseButton { ButtonIndex: MouseButton.WheelDown, Pressed: true } w: ZoomBy(-1, w.Position); AcceptEvent(); break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Left } mb:
                GrabFocus();
                if (mb.Pressed)
                {
                    if (mb.DoubleClick && HandleAt(mb.Position) is null) { ZoomBy(1, mb.Position); break; }
                    if (HandleAt(mb.Position) is { } h) { _drag = Drag.Resize; (_dragW, _dragE, _dragS, _dragN) = h; }
                    else if (DrawMode) { _drag = Drag.Draw; _anchor = FromScreen(mb.Position); }
                    else _drag = Drag.Pan;
                }
                else _drag = Drag.None;
                AcceptEvent();
                break;
            case InputEventMouseMotion mm:
                _mouse = mm.Position;
                if (_drag == Drag.Pan) PanBy(mm.Relative);
                else if (_drag == Drag.Draw) { var (la, lo) = FromScreen(mm.Position); SetBox(Math.Min(_anchor.Lat, la), Math.Min(_anchor.Lon, lo), Math.Max(_anchor.Lat, la), Math.Max(_anchor.Lon, lo)); }
                else if (_drag == Drag.Resize && Box is { } b)
                {
                    var (la, lo) = FromScreen(mm.Position);
                    double south = b.S, north = b.N, west = b.W, east = b.E;
                    if (_dragS) { south = la; if (la > north) { south = north; north = la; (_dragS, _dragN) = (false, true); } }
                    else if (_dragN) { north = la; if (la < south) { north = south; south = la; (_dragS, _dragN) = (true, false); } }
                    if (_dragW) { west = lo; if (lo > east) { west = east; east = lo; (_dragW, _dragE) = (false, true); } }
                    else if (_dragE) { east = lo; if (lo < west) { east = west; west = lo; (_dragW, _dragE) = (true, false); } }
                    SetBox(south, west, north, east);
                }
                else
                {
                    bool over = HandleAt(mm.Position) is not null;
                    MouseDefaultCursorShape = over ? CursorShape.Move : DrawMode ? CursorShape.Cross : CursorShape.Drag;
                }
                break;
            case InputEventKey { Pressed: true } k:
                float step = 64 * _s;
                switch (k.Keycode)
                {
                    case Key.Left: PanBy(new Vector2(step, 0)); break;
                    case Key.Right: PanBy(new Vector2(-step, 0)); break;
                    case Key.Up: PanBy(new Vector2(0, step)); break;
                    case Key.Down: PanBy(new Vector2(0, -step)); break;
                    case Key.Equal or Key.Plus or Key.KpAdd or Key.Pageup: ZoomBy(1); break;
                    case Key.Minus or Key.KpSubtract or Key.Pagedown: ZoomBy(-1); break;
                    default: return;
                }
                AcceptEvent();
                break;
        }
    }

    // ---- tiles ------------------------------------------------------------------------------------------------------------------------

    /// <summary>The tiles on screen: the column as laid out (it runs past the date line), the tile's own column, and its row.</summary>
    private IEnumerable<(int Ux, int X, int Y)> Visible()
    {
        var (cx, cy) = Slippy.ToTile(CenterLat, CenterLon, Zoom);
        double hx = Size.X / 2 / _ts, hy = Size.Y / 2 / _ts;
        int n = 1 << Zoom;
        int y0 = Math.Max(0, (int)Math.Floor(cy - hy)), y1 = Math.Min(n - 1, (int)Math.Floor(cy + hy));
        for (int y = y0; y <= y1; y++)
            for (int x = (int)Math.Floor(cx - hx); x <= (int)Math.Floor(cx + hx); x++)
                yield return (x, ((x % n) + n) % n, y);
    }

    private Tile Want((int Z, int X, int Y) key)
    {
        if (!_tiles.TryGetValue(key, out var t)) _tiles[key] = t = new Tile();
        t.Used = Engine.GetProcessFrames();
        return t;
    }

    private string PathOf((int Z, int X, int Y) k) => System.IO.Path.Combine(CacheDir, GeoServices.TileCachePath(TileTemplate, k.Z, k.X, k.Y));

    private void LoadFromDisk((int Z, int X, int Y) k, Tile t)
    {
        t.Loaded = true;
        string path = PathOf(k);
        try
        {
            if (!System.IO.File.Exists(path)) { Enqueue(k, t); return; }
            var img = new Image();
            if (img.LoadPngFromBuffer(System.IO.File.ReadAllBytes(path)) != Error.Ok) { Enqueue(k, t); return; }
            t.Tex = ImageTexture.CreateFromImage(img);
            if (!GeoServices.TileFresh(System.IO.File.GetLastWriteTimeUtc(path), DateTime.UtcNow)) { t.Stale = true; Enqueue(k, t); }
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { Enqueue(k, t); }
    }

    private void Enqueue((int Z, int X, int Y) k, Tile t)
    {
        if (t.Queued || Now < t.RetryAt) return;
        t.Queued = true; _queue.Add(k);
    }

    public override void _Process(double delta)
    {
        if (!IsVisibleInTree()) return;
        var vis = Visible().ToList();
        var visSet = vis.Select(v => (Zoom, v.X, v.Y)).ToHashSet();   // the same tile twice (a wrapped world) is one
        int loads = 0;
        foreach (var v in vis)
        {
            var k = (Zoom, v.X, v.Y);
            var t = Want(k);
            if (!t.Loaded && loads < 8) { loads++; LoadFromDisk(k, t); }
            else if (t.Loaded && t.Tex is null && !t.Queued && Now >= t.RetryAt) Enqueue(k, t);
        }
        // what is no longer on screen is not fetched; a running request for it is stopped
        _queue.RemoveAll(k => { bool gone = !visSet.Contains(k); if (gone && _tiles.TryGetValue(k, out var t)) t.Queued = false; return gone; });
        for (int i = 0; i < _http.Length; i++)
            if (_busy[i] is { } b && !visSet.Contains(b)) { _http[i].CancelRequest(); if (_tiles.TryGetValue(b, out var t)) t.Queued = false; _busy[i] = null; }
        if (Now >= _backoffUntil)
        {
            var (cx, cy) = Slippy.ToTile(CenterLat, CenterLon, Zoom);
            for (int i = 0; i < _http.Length && _queue.Count > 0; i++)
            {
                if (_busy[i] is not null) continue;
                var k = _queue.OrderBy(q => Math.Abs(q.X + 0.5 - cx) + Math.Abs(q.Y + 0.5 - cy)).First();
                _queue.Remove(k);
                string url = GeoServices.TileUrl(TileTemplate, k.Z, k.X, k.Y);
                if (_http[i].Request(url, ["User-Agent: " + UserAgent, "Accept: image/png"]) == Error.Ok) _busy[i] = k;
                else { Fail(k, false); }
            }
        }
        if (_tiles.Count > 600)
        {
            ulong now = Engine.GetProcessFrames();
            foreach (var k in _tiles.Where(p => now - p.Value.Used > 120 && !p.Value.Queued).OrderBy(p => p.Value.Used).Take(_tiles.Count - 400).Select(p => p.Key).ToList()) _tiles.Remove(k);
        }
        // the pad's left stick pans, the shoulders zoom in _Input of the dialog
        if (HasFocus() || Input.GetConnectedJoypads().Count > 0)
        {
            var st = new Vector2(Input.GetJoyAxis(0, JoyAxis.LeftX), Input.GetJoyAxis(0, JoyAxis.LeftY));
            if (st.Length() > 0.3f && IsVisibleInTree() && Input.GetConnectedJoypads().Count > 0) PanBy(-st * (float)delta * 500 * _s);
        }
        QueueRedraw();
    }

    private void OnTile(int slot, HttpRequest.Result result, int code, byte[] body)
    {
        if (_busy[slot] is not { } k) return;
        _busy[slot] = null;
        if (!_tiles.TryGetValue(k, out var t)) return;
        t.Queued = false;
        if (result == HttpRequest.Result.Success && code == 200)
        {
            var img = new Image();
            if (img.LoadPngFromBuffer(body) == Error.Ok)
            {
                t.Tex = ImageTexture.CreateFromImage(img); t.Stale = false; _failures = 0;
                try
                {
                    string path = PathOf(k);
                    System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
                    System.IO.File.WriteAllBytes(path, body);
                }
                catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { }
                return;
            }
        }
        // the server asked us to slow down (429, 403, 509) or is down: stop for a minute
        Fail(k, code is 429 or 403 or 509 or 503);
    }

    private void Fail((int Z, int X, int Y) k, bool backoff)
    {
        _failures++;
        if (_tiles.TryGetValue(k, out var t)) { t.Queued = false; t.RetryAt = Now + GeoServices.TileRetryAfter.TotalSeconds; }
        if (backoff) _backoffUntil = Now + 60;
    }

    // ---- drawing -------------------------------------------------------------------------------------------------------------------------

    public override void _Draw()
    {
        DrawRect(new Rect2(Vector2.Zero, Size), new Color(0.08f, 0.07f, 0.06f));
        var (cx, cy) = Slippy.ToTile(CenterLat, CenterLon, Zoom);
        foreach (var v in Visible())
        {
            var pos = Size / 2 + new Vector2((float)((v.Ux - cx) * _ts), (float)((v.Y - cy) * _ts));
            var rect = new Rect2(pos.Floor(), new Vector2(_ts + 1, _ts + 1));
            if (_tiles.TryGetValue((Zoom, v.X, v.Y), out var t) && t.Tex is not null) { DrawTextureRect(t.Tex, rect, false); continue; }
            // a coarser tile already on hand, enlarged, until this one arrives
            bool drawn = false;
            for (int up = 1; up <= 4 && Zoom - up >= 0 && !drawn; up++)
            {
                if (!_tiles.TryGetValue((Zoom - up, v.X >> up, v.Y >> up), out var p) || p.Tex is null) continue;
                float part = 256f / (1 << up);
                var src = new Rect2(((v.X & ((1 << up) - 1)) * part), ((v.Y & ((1 << up) - 1)) * part), part, part);
                DrawTextureRectRegion(p.Tex, rect, src);
                drawn = true;
            }
            if (!drawn) DrawRect(rect, new Color(0.13f, 0.11f, 0.09f));
        }
        DrawBox();
        // attribution (the tile policy: shown on the map)
        var font = HudStyle.Bold;
        int fs = (int)(13 * _s);
        string text = GeoServices.Attribution;
        var sz = font.GetStringSize(text, HorizontalAlignment.Left, -1, fs);
        var at = new Vector2(Size.X - sz.X - 12 * _s, Size.Y - 8 * _s);
        DrawRect(new Rect2(at.X - 6 * _s, at.Y - sz.Y, sz.X + 12 * _s, sz.Y + 6 * _s), new Color(1, 1, 1, 0.78f));
        DrawString(font, at, text, HorizontalAlignment.Left, -1, fs, new Color(0.1f, 0.1f, 0.1f));
        if (_failures >= 4)
        {
            string msg = Now < _backoffUntil ? "The tile server asked us to slow down; waiting a minute" : "Map tiles are not arriving (offline?)";
            DrawString(font, new Vector2(12 * _s, Size.Y - 10 * _s), msg, HorizontalAlignment.Left, -1, fs, HudStyle.Danger);
        }
    }

    private void DrawBox()
    {
        if (Box is not { } b) return;
        var a = ToScreen(b.N, b.W); var c = ToScreen(b.S, b.E);
        var rect = new Rect2(a, c - a).Abs();
        bool fits = Check?.Fits ?? false;
        var col = fits ? HudStyle.Amber : HudStyle.Danger;
        DrawRect(rect, new Color(col, 0.16f));
        DrawRect(rect, col, false, Math.Max(2, 2 * _s));
        float r = 6 * _s;
        float mx = rect.Position.X + rect.Size.X / 2, my = rect.Position.Y + rect.Size.Y / 2;
        foreach (var p in new[] { rect.Position, new Vector2(rect.End.X, rect.Position.Y), new Vector2(rect.Position.X, rect.End.Y), rect.End,
                                  new Vector2(mx, rect.Position.Y), new Vector2(mx, rect.End.Y), new Vector2(rect.Position.X, my), new Vector2(rect.End.X, my) })
        {
            DrawRect(new Rect2(p - new Vector2(r, r), new Vector2(r * 2, r * 2)), HudStyle.Ink);
            DrawRect(new Rect2(p - new Vector2(r, r), new Vector2(r * 2, r * 2)), col, false, Math.Max(2, 2 * _s));
        }
        if (Check is { } ck && ck.WidthKm > 0)
        {
            string label = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{ck.WidthKm:0.0} x {ck.HeightKm:0.0} km");
            int fs = (int)(15 * _s);
            var font = HudStyle.Bold;
            var sz = font.GetStringSize(label, HorizontalAlignment.Left, -1, fs);
            var at = new Vector2(Math.Clamp(mx - sz.X / 2, 4, Math.Max(4, Size.X - sz.X - 4)), Math.Max(sz.Y + 4, rect.Position.Y - 12 * _s));
            DrawRect(new Rect2(at.X - 5 * _s, at.Y - sz.Y, sz.X + 10 * _s, sz.Y + 6 * _s), new Color(HudStyle.Ink, 0.85f));
            DrawString(font, at, label, HorizontalAlignment.Left, -1, fs, col);
        }
    }
}
