using Godot;
using I76.Formats.Input;
using I76.Formats.Shell;
using I76.Game.Shell.Garage3D;

namespace I76.Game.Shell;

/// <summary>
/// The 2D CONFIGURE CHASSIS form's COLOUR and FINISH pickers (the port's respray, #205): the original's paint scheme picker (bank 0x24
/// sprite 15 at (234, 156), five rows at (+18, +48), its arrows and CANCEL) with its heading lettered as the art letters PAINT SCHEME
/// (<see cref="ArtButtons"/>), the rows in font 0x45, a colour's swatch true over the frame (<see cref="SwatchLayer"/>: the form's palette
/// has no such colours). A row picks; CANCEL or Esc closes. The 3D garage's 1997 look lists the same rows in the same frame.
/// </summary>
public sealed class PaintListPicker : ShellScreen
{
    public required string Title { get; init; }
    public required IReadOnlyList<(string Label, Color? Swatch)> Rows { get; init; }
    public required Action<int> Picked { get; init; }
    public required ShellScreen Owner { get; init; }
    public override bool EscapeOpensOptions => false;

    private const int X = 234, Y = 156, Visible = 5, Sprite = 15;
    private static readonly (int X0, int X1, int Y0, int Y1) List = (13, 158, 48, 131), Cancel = (89, 158, 141, 157), Up = (17, 35, 10, 26), Down = (17, 35, 135, 150);
    /// <summary>The heading's band, its border included, and the word in it (sprite coordinates).</summary>
    private static readonly Rect2I Band = new(12, 29, 148, 18), Word = new(15, 32, 142, 12);
    private int _scroll, _w, _h;
    private SwatchLayer? _swatches;
    private readonly I76.Formats.Input.HoldRepeat _hold = new();
    private ShellFont _f45 = null!, _f47 = null!;

    public override void Enter()
    {
        var s = Shell.Db.Bank(0x24)[Sprite];
        _w = s?.Width ?? 172; _h = s?.Height ?? 168;
        _f45 = Shell.Db.Font(0x45);
        _f47 = Shell.Db.Font(0x47);
        _swatches = new SwatchLayer { Name = "PaintSwatches" };
        Shell.AddLayerOverFrame(_swatches);
        Draw();
    }

    public override void Leave()
    {
        _swatches?.QueueFree();
        _swatches = null;
    }

    private void Draw()
    {
        var art = Shell.Db.Bank(0x24)[Sprite];
        Frame.Fill(X + 6, Y + _h, _w, 6, 0x0f);
        Frame.Fill(X + _w, Y + 6, 6, _h, 0x0f);
        Frame.BlitAt(art, X, Y);
        if (art is not null) Heading(art);
        var swatches = new List<(Rect2I, Color)>();
        for (int i = 0; i < Visible && _scroll + i < Rows.Count; i++)
        {
            var (label, swatch) = Rows[_scroll + i];
            int x = X + 18, y = Y + 48 + i * 17;
            if (swatch is { } c) { swatches.Add((new Rect2I(x - 3, y + 2, 12, 12), c)); x += 13; }
            Frame.Text(_f45, x, y, Fit(label, X + List.X1 - 2 - x), ShellFrame.DefaultInk);
        }
        _swatches?.Set(swatches, true);
    }

    /// <summary>The heading: the band's face without its word, the title lettered as the art letters PAINT SCHEME there.</summary>
    private void Heading(Sprite art)
    {
        byte Px(int x, int y) => art.Pixels[Math.Clamp(y - Y, 0, art.Height - 1) * art.Width + Math.Clamp(x - X, 0, art.Width - 1)];
        var band = new Rect2I(X + Band.Position.X, Y + Band.Position.Y, Band.Size.X, Band.Size.Y);
        if (ArtButtons.Label(Px, Frame.Palette, new Rect2I(X + Word.Position.X, Y + Word.Position.Y, Word.Size.X, Word.Size.Y)) is not { } like) return;
        ArtButtons.Face(Frame, Px, band, like.Box.Position.X, band, 2);
        ArtButtons.Letter(Frame, _f47, Title, like, "PAINT SCHEME", band);
    }

    private string Fit(string s, int width)
    {
        if (_f45.Measure(s) <= width) return s;
        while (s.Length > 1 && _f45.Measure(s + "..") > width) s = s[..^1];
        return s + "..";
    }

    private bool In((int X0, int X1, int Y0, int Y1) r, Vector2I p) => r.X0 < p.X - X && p.X - X < r.X1 && r.Y0 < p.Y - Y && p.Y - Y < r.Y1;

    private bool ScrollBy(int by)
    {
        int to = Math.Clamp(_scroll + by, 0, Math.Max(0, Rows.Count - Visible));
        if (to == _scroll) return false;
        _scroll = to;
        Draw();
        return true;
    }

    private int ArrowAt(Vector2I p) => In(Up, p) ? 0 : In(Down, p) ? 1 : -1;

    /// <summary>The visible rows (past the first or last the list scrolls), CANCEL.</summary>
    public override IReadOnlyList<PadRegion> PadRegions
    {
        get
        {
            var l = new List<PadRegion>();
            int n = Math.Min(Visible, Rows.Count - _scroll);
            for (int i = 0; i < n; i++)
            {
                int ii = i;
                l.Add(PadRegion.Of(X + List.X0 + 1, Y + List.Y0 + 17 * i, List.X1 - List.X0 - 2, 17,
                    d => d == NavDir.Up && ii == 0 ? ScrollBy(-1) : d == NavDir.Down && ii == n - 1 ? ScrollBy(1) : false));
            }
            l.Add(PadRegion.Of((X + Cancel.X0, X + Cancel.X1, Y + Cancel.Y0, Y + Cancel.Y1)));
            return l;
        }
    }

    public override void Tick()
    {
        if (Shell.Keys.Contains(Key.Escape)) { Close(); return; }
        // the keyboard scrolls the list as the arrows and the wheel do
        foreach (var k in Shell.Keys)
            if (k is Key.Up or Key.Down or Key.Pageup or Key.Pagedown) ScrollBy(k switch { Key.Up => -1, Key.Down => 1, Key.Pageup => -Visible, _ => Visible });
        if (Shell.Wheel != 0) ScrollBy(Shell.WheelRows);
        int held = _hold.Id;
        for (int n = _hold.Update(Shell.MouseHeld, ArrowAt(Shell.Mouse), Shell.Delta); n > 0; n--) ScrollBy(held == 0 ? -1 : 1);
        if (!Shell.Clicked) return;
        var p = Shell.Mouse;
        if (ArrowAt(p) is >= 0 and var arrow) { _hold.Begin(arrow); ScrollBy(arrow == 0 ? -1 : 1); return; }
        if (In(Cancel, p)) { Close(); return; }
        if (!In(List, p)) return;
        int i = (p.Y - Y - List.Y0) / 17 + _scroll;
        if (i < 0 || i >= Rows.Count || i >= _scroll + Visible) return;
        Shell.Sound(82);
        Close();
        Picked(i);
    }

    private void Close()
    {
        Frame.Restore(X, Y, _w + 10, _h + 10);
        Shell.Modal(null);
        Owner.Repaint();
    }
}
