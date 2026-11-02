using Godot;
using I76.Formats.Input;
using I76.Formats.Shell;

namespace I76.Game.Shell;

/// <summary>One line of <see cref="GameOptionsPopup"/>: a box that is ticked or not, or a value with its - and + (and the handwriting between).</summary>
public sealed record OptionRow(string Label, bool Step, bool On, string Value, Action<int> Act, bool Locked = false, bool Link = false);

/// <summary>
/// The Nitro host form's GAME OPTIONS (the port's: the base game has only the VARIANT popup's art for it, and its DONE
/// and DELETE do nothing here). Drawn the way the Nitro shell draws its own COMBAT / RACING OPTIONS popups (bank 30
/// sprites 41 and 39: the title strip, the boxes, the - and + buttons, DONE and CANCEL are copied out of them) on the blank
/// notice's parchment (sprite 16), with the words in the shell's fonts. Every control acts where it stands; DONE keeps
/// what was set and CANCEL (or Esc) puts it back, which <see cref="Closed"/> does.
/// </summary>
public sealed class GameOptionsPopup : ShellScreen
{
    public required ShellFont Font { get; init; }
    /// <summary>The rows as they stand now, asked again after every change.</summary>
    public required Func<IReadOnlyList<OptionRow>> Rows { get; init; }
    /// <summary>The popup is gone; true for DONE.</summary>
    public required Action<bool> Closed { get; init; }
    /// <summary>A <see cref="OptionRow.Link"/> row was pressed: the popup has stepped aside (it is not <see cref="Closed"/>) and this opens what the row names.</summary>
    public Action? Link { get; init; }
    public override bool EscapeOpensOptions => false;

    public const int W = 267, X = 316, Pitch = 22, MaxRows = 8;
    /// <summary>The panel's top: 96, or higher where a long list would run off the 480-line frame (with its 6 px shadow).</summary>
    private const int TopY = 96, FrameH = 480;
    private int Y = TopY;
    private IReadOnlyList<OptionRow> _rows = [];
    private int _h;
    private readonly HoldRepeat _hold = new();
    private ShellFont? _label;

    private Sprite? Combat => Shell.Db.BankNative(30)[41];
    private Sprite? Notice => Shell.Db.BankNative(30)[16];

    // sprite 41's pieces (it is 267 x 169: the panel's frame is 2 px, the box inside it runs 10..255 and 11..138)
    private const int BoxTop = 11, StripEnd = 28, FirstRow = 39, DoneX0 = 100, DoneX1 = 170, CancelX0 = 179, CancelX1 = 249, BtnH = 16;
    // the steppers' value line is wider than sprite 41's (its MinusX 165): the handwriting's "Normal" and "Points" need about 70
    private const int MinusX = 140, PlusX = 236, StepW = 10, LineX0 = 151, LineX1 = 234, ArtMinusX = 165;

    public override void Enter() { Refresh(); }

    private int RowY(int i) => Y + FirstRow + Pitch * i;
    private int BoxBottom => FirstRow + Pitch * (_rows.Count - 1) + 14 + 6;
    private int BtnTop => BoxBottom + 7;

    private (int X0, int X1, int Y0, int Y1) DoneR => (X + DoneX0, X + DoneX1 + 1, Y + BtnTop, Y + BtnTop + BtnH);
    private (int X0, int X1, int Y0, int Y1) CancelR => (X + CancelX0, X + CancelX1 + 1, Y + BtnTop, Y + BtnTop + BtnH);
    private Rect2I MinusR(int i) => new(X + MinusX - 3, RowY(i) + 1, StepW + 6, StepW + 3);
    private Rect2I PlusR(int i) => new(X + PlusX - 3, RowY(i) + 1, StepW + 6, StepW + 3);
    private Rect2I RowR(int i) => new(X + 14, RowY(i) - 4, W - 28, Pitch);

    public override IReadOnlyList<PadRegion> PadRegions
    {
        get
        {
            var l = new List<PadRegion>();
            for (int i = 0; i < _rows.Count; i++)
            {
                if (_rows[i].Step) { l.Add(PadRegion.Of(MinusR(i))); l.Add(PadRegion.Of(PlusR(i))); }
                else if (!_rows[i].Locked) l.Add(PadRegion.Of(RowR(i)));
            }
            l.Add(PadRegion.Of(DoneR));
            l.Add(PadRegion.Of(CancelR));
            return l;
        }
    }

    /// <summary>Asks the rows again and draws the whole popup (a limit with one value has no row for it, so the panel's height follows).</summary>
    private void Refresh()
    {
        if (_h > 0) Frame.Restore(X, Y, W + 6, _h + 6);
        _rows = Rows();
        _label ??= Shell.Db.Font(0x47);
        _h = BtnTop + BtnH + 7 + 2;
        Y = System.Math.Min(TopY, FrameH - 6 - _h);
        Draw();
    }

    /// <summary>A rectangle of a sprite's own pixels at (dx, dy), its columns and rows mapped by <paramref name="mx"/> and <paramref name="my"/> (the panel is taller than any sprite).</summary>
    private void Copy(Sprite? s, int dx, int dy, int w, int h, Func<int, int> mx, Func<int, int> my)
    {
        if (s is null) return;
        for (int ty = 0; ty < h; ty++)
            for (int tx = 0; tx < w; tx++)
            {
                int px = Math.Clamp(mx(tx), 0, s.Width - 1), py = Math.Clamp(my(ty), 0, s.Height - 1);
                if (s.Opaque[py * s.Width + px]) Frame.Fill(dx + tx, dy + ty, 1, 1, s.Pixels[py * s.Width + px]);
            }
    }

    private void Draw()
    {
        var n = Notice; var c = Combat;
        if (n is null || c is null) return;
        // the parchment: the blank notice's corners and edges, its plain middle repeated down to this height
        int nw = n.Width, nh = n.Height;
        Copy(n, X, Y, W, _h, x => x < 14 ? x : x >= W - 14 ? nw - (W - x) : x,
            y => y < 14 ? y : y >= _h - 14 ? nh - (_h - y) : 14 + (y - 14) % 40);
        Frame.Fill(X + 6, Y + _h, W, 6, 0x0f);
        Frame.Fill(X + W, Y + 6, 6, _h, 0x0f);
        // the box and the title strip (the strip's own lettering is covered with its plain left end), and the box's sides and foot
        Copy(c, X + 10, Y + BoxTop, 246, StripEnd - BoxTop + 1, x => 10 + x < 66 || 10 + x > 196 ? 10 + x : 11 + x % 48, y => BoxTop + y);
        byte ink = c.Pixels[60 * c.Width + 10];
        Frame.Fill(X + 10, Y + StripEnd + 1, 1, BoxBottom - StripEnd, ink);
        Frame.Fill(X + 255, Y + StripEnd + 1, 1, BoxBottom - StripEnd, ink);
        Frame.Fill(X + 10, Y + BoxBottom, 246, 1, ink);
        Bold(X + 10 + (246 - _label!.Measure("GAME OPTIONS")) / 2, Y + BoxTop + 1 + (StripEnd - BoxTop - _label.Height) / 2, "GAME OPTIONS");

        for (int i = 0; i < _rows.Count; i++)
        {
            var r = _rows[i];
            int y = RowY(i);
            int ty = y + (14 - _label!.Height) / 2;
            if (r.Link)
            {
                // a line that opens a list of its own: the label, the value on the line, and an arrow where the + would be
                Bold(X + 19, ty, r.Label);
                Frame.Fill(X + LineX0, y + 12, LineX1 - LineX0 + 1, 1, ink);
                var lv = Fit(r.Value, LineX1 - LineX0 - 8);
                Frame.Text(Font, X + (LineX0 + LineX1) / 2 - Font.Measure(lv) / 2, y + 12 - Font.Height + 1, lv);
                if (!r.Locked) Bold(X + PlusX, ty, ">");
                continue;
            }
            if (!r.Step)
            {
                Copy(c, X + 19, y, 14, 14, x => 19 + x, yy => FirstRow + yy);
                if (r.On) DrawCross(X + 19, y);
                Bold(X + 41, ty, r.Label);
                continue;
            }
            Bold(X + 19, ty, r.Label);
            Copy(c, X + MinusX, y + 2, StepW, StepW, x => ArtMinusX + x, yy => 41 + yy);
            Copy(c, X + PlusX, y + 2, StepW, StepW, x => PlusX + 0 + x, yy => 41 + yy);
            Frame.Fill(X + LineX0, y + 12, LineX1 - LineX0 + 1, 1, ink);
            var v = Fit(r.Value, LineX1 - LineX0 - 8);
            Frame.Text(Font, X + (LineX0 + LineX1) / 2 - Font.Measure(v) / 2, y + 12 - Font.Height + 1, v);
        }
        // DONE and CANCEL as the other options popups have them, from sprite 41's own pixels
        Copy(c, X + DoneX0, Y + BtnTop, DoneX1 - DoneX0 + 1, BtnH, x => DoneX0 + x, yy => 145 + yy);
        Copy(c, X + CancelX0, Y + BtnTop, CancelX1 - CancelX0 + 1, BtnH, x => CancelX0 + x, yy => 145 + yy);
    }

    /// <summary>The label font struck twice a pixel apart: the sprites' lettering is bolder than any face the shell has.</summary>
    private void Bold(int x, int y, string s)
    {
        Frame.Text(_label!, x, y, s, ShellFrame.DefaultInk);
        Frame.Text(_label!, x + 1, y, s, ShellFrame.DefaultInk);
    }

    /// <summary>The box's mark: a handwritten X in the form's ink, as the base game's host form puts in its TEAM PLAY box.</summary>
    private void DrawCross(int x, int y)
    {
        var f = Font;
        Frame.Text(f, x + (14 - f.Measure("X")) / 2 + 1, y + (14 - f.Height) / 2 - 1, "X");
    }

    private string Fit(string s, int w) => AddressBookPopup.Fit(Font, s, w);

    private void Close(bool done)
    {
        Shell.Sound(82);
        Frame.Restore(X, Y, W + 6, _h + 6);
        Shell.Modal(null);
        Closed(done);
    }

    private static bool In((int X0, int X1, int Y0, int Y1) r, Vector2I p) => r.X0 <= p.X && p.X < r.X1 && r.Y0 <= p.Y && p.Y < r.Y1;

    private void Act(int row, int dir)
    {
        if (row < 0 || row >= _rows.Count) return;
        var r = _rows[row];
        if (!r.Step && r.Locked) return;
        Shell.Sound(82);
        if (r.Link)
        {
            // steps aside without closing: whatever the row opens takes the screen, and gives it back through Link
            Frame.Restore(X, Y, W + 6, _h + 6);
            Shell.Modal(null);
            Link?.Invoke();
            return;
        }
        r.Act(dir);
        Refresh();
    }

    public override void Tick()
    {
        if (Shell.Keys.Contains(Key.Escape)) { Close(false); return; }
        if (Shell.Keys.Contains(Key.Enter) || Shell.Keys.Contains(Key.KpEnter)) { Close(true); return; }
        var p = Shell.Mouse;
        // the - and + repeat while held (HoldRepeat): the id is the row's, doubled, plus one for +
        int At() { for (int i = 0; i < _rows.Count; i++) if (_rows[i].Step) { if (MinusR(i).HasPoint(p)) return i * 2; if (PlusR(i).HasPoint(p)) return i * 2 + 1; } return -1; }
        int held = _hold.Id;
        for (int n = _hold.Update(Shell.MouseHeld, At(), Shell.Delta); n > 0 && held >= 0; n--) Act(held / 2, held % 2 == 0 ? -1 : 1);
        if (!Shell.Clicked) return;
        if (At() is >= 0 and var a) { _hold.Begin(a); Act(a / 2, a % 2 == 0 ? -1 : 1); return; }
        if (In(DoneR, p)) { Close(true); return; }
        if (In(CancelR, p)) { Close(false); return; }
        for (int i = 0; i < _rows.Count; i++)
            if (!_rows[i].Step && RowR(i).HasPoint(p)) { Act(i, 1); return; }
    }
}
