using Godot;
using I76.Formats.Input;
using I76.Formats.Shell;

namespace I76.Game.Shell;

/// <summary>
/// The host forms' A.I. CARS (the port's; docs/multiplayer.md "Bots"): a popup with a row for each bot seat, up to the form's Bots, where the host
/// picks the seat's MAKE / MODEL and VARIANT as the instant melee's A.I. CARS list does (its pickers, here as the - and + beside each), or leaves
/// it on Random. Neither game's host form has room for the list, so it is a popup in the shell's look: the parchment and the DONE and CANCEL
/// buttons are the game's own art (the Nitro Pack's GAME OPTIONS' sprites, the base game's notice and CHOOSE A TEAM), the boxes and lines drawn
/// with them. DONE keeps the choices (<see cref="Closed"/> with true), CANCEL, Esc and the pad's B leave them as they were. The car of seat N is
/// the file <c>&lt;stem&gt;&lt;variant&gt;.vcf</c> in <see cref="Choices"/>[N], or empty for Random.
/// </summary>
public sealed class BotCarsPopup : ShellScreen
{
    public required ShellFont Font { get; init; }
    public required MeleeData Data { get; init; }
    /// <summary>How many seats have a row (the form's Bots).</summary>
    public required int Seats { get; init; }
    /// <summary>The choices as they stand (a car file or empty each), changed in place; fewer than <see cref="Seats"/> reads as Random for the rest.</summary>
    public required List<string> Choices { get; init; }
    /// <summary>The popup is gone; true for DONE.</summary>
    public required Action<bool> Closed { get; init; }
    public override bool EscapeOpensOptions => false;

    public const int W = 480, X = 80, Y = 62, Pitch = 25, Visible = 7;
    private const int FirstRow = 62, Box = 12;
    // the columns: the seat number, the model's - value + and the variant's
    private const int NumX = 18, MMinus = 40, MX0 = 56, MX1 = 248, MPlus = 252, VMinus = 276, VX0 = 292, VX1 = 440, VPlus = 444;
    private int _h, _scroll;
    private ShellFont? _label;
    private (int Model, int Variant)[] _seat = [];
    private (int Model, int Variant)[] _was = [];
    private readonly HoldRepeat _hold = new();
    private bool Nitro => Shell.Install.IsNitro;

    private Sprite? Parchment => Nitro ? Shell.Db.BankNative(30)[16] : Shell.Db.Bank(0x24)[17];
    /// <summary>DONE's and CANCEL's art: where they are in a sprite (the Nitro popups' sprite 41; the base game's CHOOSE A TEAM) and how big.</summary>
    private Sprite? ButtonArt => Nitro ? Shell.Db.BankNative(30)[41] : Shell.Db.Bank(0x24)[19];
    private (int Dx, int Dy, int Cx, int Cy, int W, int H) Buttons => Nitro ? (100, 145, 179, 145, 71, 16) : (90, 286, 169, 286, 73, 18);

    private int Count => Math.Max(Seats, 0);
    private int Top => Y + FirstRow;
    private int RowY(int i) => Top + Pitch * (i - _scroll);
    private int Shown => Math.Min(Visible, Count);
    private int ListBottom => FirstRow + Pitch * Math.Max(1, Shown) + 4;
    private int BtnTop => ListBottom + 10;

    private Rect2I DoneR => new(X + W - 16 - 2 * Buttons.W - 10, Y + BtnTop, Buttons.W, Buttons.H);
    private Rect2I CancelR => new(X + W - 16 - Buttons.W, Y + BtnTop, Buttons.W, Buttons.H);
    private Rect2I UpR => new(X + 18, Y + BtnTop + 2, 16, 14);
    private Rect2I DownR => new(X + 40, Y + BtnTop + 2, 16, 14);
    private Rect2I MinusR(int i, bool v) => new(X + (v ? VMinus : MMinus) - 3, RowY(i) - 1, Box + 6, Box + 4);
    private Rect2I PlusR(int i, bool v) => new(X + (v ? VPlus : MPlus) - 3, RowY(i) - 1, Box + 6, Box + 4);

    public override IReadOnlyList<PadRegion> PadRegions
    {
        get
        {
            var l = new List<PadRegion>();
            for (int i = _scroll; i < _scroll + Shown; i++)
            {
                l.Add(PadRegion.Of(MinusR(i, false))); l.Add(PadRegion.Of(PlusR(i, false)));
                if (_seat[i].Model >= 0) { l.Add(PadRegion.Of(MinusR(i, true))); l.Add(PadRegion.Of(PlusR(i, true))); }
            }
            if (Count > Visible) { l.Add(PadRegion.Of(UpR)); l.Add(PadRegion.Of(DownR)); }
            l.Add(PadRegion.Of(DoneR));
            l.Add(PadRegion.Of(CancelR));
            return l;
        }
    }

    public override void Enter()
    {
        try { _label = Shell.Db.Font(0x47); } catch (Exception) { _label = null; }
        _label ??= Font;
        _seat = new (int, int)[Count];
        for (int i = 0; i < Count; i++)
        {
            while (Choices.Count <= i) Choices.Add("");
            _seat[i] = Data.Find(Choices[i]) is { } f ? (f.Model, f.Variant) : (-1, 1);
        }
        _was = [.. _seat];
        _h = BtnTop + Buttons.H + 14;
        Draw();
    }

    private string CarFile(int i) => _seat[i].Model < 0 ? "" : Data.Vcf(_seat[i].Model, _seat[i].Variant);

    /// <summary>Puts a rectangle of a sprite's pixels at (dx, dy); the columns and rows are mapped by <paramref name="mx"/> and <paramref name="my"/>.</summary>
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

    private void Bold(int x, int y, string s)
    {
        Frame.Text(_label!, x, y, s, ShellFrame.DefaultInk);
        Frame.Text(_label!, x + 1, y, s, ShellFrame.DefaultInk);
    }

    private void BoldCentered(int x0, int x1, int y, string s) => Bold((x0 + x1) / 2 - _label!.Measure(s) / 2, y, s);

    private void Draw()
    {
        Frame.Restore(X, Y, W + 6, _h + 6);
        var n = Parchment;
        if (n is not null)
        {
            // the parchment: the blank notice's corners and edges, its plain middle repeated across and down to this size
            int nw = n.Width, nh = n.Height;
            Copy(n, X, Y, W, _h, x => x < 14 ? x : x >= W - 14 ? nw - (W - x) : 14 + (x - 14) % Math.Max(1, nw - 28),
                y => y < 14 ? y : y >= _h - 14 ? nh - (_h - y) : 14 + (y - 14) % Math.Max(1, Math.Min(40, nh - 28)));
        }
        Frame.Fill(X + 6, Y + _h, W, 6, 0x0f);
        Frame.Fill(X + W, Y + 6, 6, _h, 0x0f);
        const byte ink = 0x0f;
        BoldCentered(X, X + W, Y + 14, "A.I. CARS");
        Frame.Fill(X + 16, Y + 34, W - 32, 1, ink);
        if (Count > 0)
        {
            BoldCentered(X + MX0, X + MX1, Y + 40, "MAKE / MODEL");
            BoldCentered(X + VX0, X + VX1, Y + 40, "VARIANT");
        }
        if (Count == 0)
        {
            Frame.Text(Font, X + W / 2 - Font.Measure("No bots: raise Bots first.") / 2, Y + FirstRow + 8, "No bots: raise Bots first.");
        }
        for (int i = _scroll; i < _scroll + Shown; i++)
        {
            int y = RowY(i);
            Bold(X + NumX + (i < 9 ? 6 : 0), y, (i + 1).ToString());
            var (m, v) = _seat[i];
            Stepper(X + MMinus, X + MPlus, y, true);
            Value(X + MX0, X + MX1, y, m < 0 ? "Random" : Data.Models[m].MakeModel);
            bool fixedVariant = m < 0;
            Stepper(X + VMinus, X + VPlus, y, !fixedVariant);
            Value(X + VX0, X + VX1, y, fixedVariant ? "-" : Data.Models[m].Variants[Math.Clamp(v - 1, 0, Data.Models[m].Variants.Count - 1)]);
        }
        if (Count > Visible)
        {
            Arrow(UpR, true);
            Arrow(DownR, false);
            Frame.Text(Font, X + 66, Y + BtnTop + 1, $"{_scroll + 1}-{_scroll + Shown} of {Count}");
        }
        var art = ButtonArt;
        var b = Buttons;
        Copy(art, DoneR.Position.X, DoneR.Position.Y, b.W, b.H, x => b.Dx + x, yy => b.Dy + yy);
        Copy(art, CancelR.Position.X, CancelR.Position.Y, b.W, b.H, x => b.Cx + x, yy => b.Cy + yy);
    }

    /// <summary>A - and + box at x of the row: a drawn square with its sign.</summary>
    private void Stepper(int minusX, int plusX, int y, bool on)
    {
        const byte ink = 0x0f;
        foreach (var (x, plus) in new[] { (minusX, false), (plusX, true) })
        {
            Frame.Fill(x, y, Box, 1, ink); Frame.Fill(x, y + Box - 1, Box, 1, ink); Frame.Fill(x, y, 1, Box, ink); Frame.Fill(x + Box - 1, y, 1, Box, ink);
            if (!on) continue;
            Frame.Fill(x + 3, y + Box / 2 - 1, Box - 6, 2, ink);
            if (plus) Frame.Fill(x + Box / 2 - 1, y + 3, 2, Box - 6, ink);
        }
    }

    private void Arrow(Rect2I r, bool up)
    {
        const byte ink = 0x0f;
        var (x, y) = (r.Position.X, r.Position.Y);
        Frame.Fill(x, y, r.Size.X, 1, ink); Frame.Fill(x, y + r.Size.Y - 1, r.Size.X, 1, ink); Frame.Fill(x, y, 1, r.Size.Y, ink); Frame.Fill(x + r.Size.X - 1, y, 1, r.Size.Y, ink);
        for (int k = 0; k < 4; k++)
        {
            int w = up ? 2 * k + 2 : 8 - 2 * k;
            Frame.Fill(x + r.Size.X / 2 - w / 2, y + 4 + k * 2 - (up ? 0 : 0), w, 2, ink);
        }
    }

    private void Value(int x0, int x1, int y, string s)
    {
        Frame.Fill(x0, y + Box - 1, x1 - x0, 1, 0x0f);
        var v = AddressBookPopup.Fit(Font, s, x1 - x0 - 4);
        Frame.Text(Font, (x0 + x1) / 2 - Font.Measure(v) / 2, y + Box - 2 - Font.Height + 3, v);
    }

    private void Close(bool done)
    {
        Shell.Sound(82);
        Frame.Restore(X, Y, W + 6, _h + 6);
        Shell.Modal(null);
        if (done) for (int i = 0; i < Count; i++) Choices[i] = CarFile(i);
        Closed(done);
    }

    private void Step(int row, bool variant, int dir)
    {
        var (m, v) = _seat[row];
        if (!variant)
        {
            int n = Data.Models.Count + 1;
            m = (m + 1 + dir + n) % n - 1;
            v = 1;
        }
        else if (m >= 0)
        {
            int n = Data.Models[m].Variants.Count;
            v = (v - 1 + dir + n) % n + 1;
        }
        _seat[row] = (m, v);
        Shell.Sound(82);
        Draw();
    }

    private void ScrollBy(int by)
    {
        int was = _scroll;
        _scroll = Math.Clamp(_scroll + by, 0, Math.Max(0, Count - Visible));
        if (_scroll != was) { Shell.Sound(82); Draw(); }
    }

    public override void Tick()
    {
        if (Shell.Keys.Contains(Key.Escape)) { Close(false); return; }
        if (Shell.Keys.Contains(Key.Enter) || Shell.Keys.Contains(Key.KpEnter)) { Close(true); return; }
        if (Shell.WheelRows != 0 && Shell.Wheel != 0) ScrollBy(Shell.WheelRows);
        var p = Shell.Mouse;
        // the - and + repeat while held (HoldRepeat): the id is the row's, times four, plus 2 for the variant, plus 1 for +
        int At()
        {
            for (int i = _scroll; i < _scroll + Shown; i++)
                for (int k = 0; k < 4; k++)
                {
                    bool v = k >= 2, plus = k % 2 == 1;
                    if (v && _seat[i].Model < 0) continue;
                    if ((plus ? PlusR(i, v) : MinusR(i, v)).HasPoint(p)) return i * 4 + k;
                }
            return -1;
        }
        int held = _hold.Id;
        for (int n = _hold.Update(Shell.MouseHeld, At(), Shell.Delta); n > 0 && held >= 0; n--) Step(held / 4, held % 4 >= 2, held % 2 == 0 ? -1 : 1);
        if (!Shell.Clicked) return;
        if (At() is >= 0 and var a) { _hold.Begin(a); Step(a / 4, a % 4 >= 2, a % 2 == 0 ? -1 : 1); return; }
        if (DoneR.HasPoint(p)) { Close(true); return; }
        if (CancelR.HasPoint(p)) { Close(false); return; }
        if (Count > Visible && UpR.HasPoint(p)) ScrollBy(-1);
        else if (Count > Visible && DownR.HasPoint(p)) ScrollBy(1);
    }
}
