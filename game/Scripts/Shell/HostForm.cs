using Godot;
using I76.Formats.Input;
using I76.Formats.Shell;

namespace I76.Game.Shell;

/// <summary>
/// The base game's network host form (modes 3 and 4) drawn by the port on its own ruled sheet (<see cref="HostFormLayout"/>), in
/// Mouse Options' style: the original's form (record 19) has no room for the port's options, which had come to share four short lines
/// under its options box. Every field keeps its hotspot id, so clicks, the pad and the keys do what they did; the 1997 look draws the
/// sheet's rules, title and buttons in the palette (<see cref="PaperSheet"/>), the modern look as vectors (<see cref="HostSheet"/>).
/// The Nitro Pack's host form keeps its art.
/// </summary>
public sealed partial class MeleeScreen
{
    private bool DrawnHost => Mode is 3 or 4 && !Nitro;
    private HostSheet? _sheet;
    private Dictionary<int, (int X0, int X1, int Y0, int Y1)>? _hostHot;
    private ShellFont? _hand;
    private ShellFont Hand => _hand ??= Shell.Db.Font(0x44);

    /// <summary>The hotspots as the form stands (asked again after every change, as the co-op form has fewer rows).</summary>
    private Dictionary<int, (int X0, int X1, int Y0, int Y1)> HostHot => _hostHot ??= HostFormLayout.Hot(HostFormLayout.RowsOf(HostState()));

    private HostFormLayout.State HostState()
    {
        var (_, model, variant) = _slots.Count > 0 ? _slots[0] : ("", 0, 1);
        var m = _data.Models.Count > 0 ? _data.Models[Math.Clamp(model, 0, _data.Models.Count - 1)] : null;
        return new()
        {
            Coop = _coopBase,
            Driver = _slots.Count > 0 ? _slots[0].Name : "",
            Renaming = _renaming,
            Area = Arenas.Count > 0 ? Arenas[Math.Clamp(Area, 0, Arenas.Count - 1)].Name : "No arenas",
            GameName = _gameName,
            Naming = _naming,
            Drivers = _drivers,
            TeamPlay = _teamPlay,
            InviteOnly = _inviteOnly,
            ScoreOn = _scoreOn,
            MaxScore = _maxScore,
            Password = _password.Length > 0,
            Bots = Math.Min(_bots, MaxBots),
            BotSkill = _botSkill.ToString(),
            BotCars = BotCarsText(),
            Rate = _rate,
            ShareMaps = Shell.Settings.ShareMaps,
            Hidden = Shell.Settings.HiddenContent,
            Turrets = _turrets,
            CarReset = Shell.Settings.CarReset,
            Cb = Shell.Settings.CbRadio,
            Crosshair = Shell.Settings.HostCrosshair,
            CoopDamage = CoopDamageText,
            MakeModel = StoryCar ? "Mission's own car" : m?.MakeModel ?? "",
            Variant = StoryCar ? "the story's own" : m is null ? "" : m.Variants[Math.Clamp(variant - 1, 0, m.Variants.Count - 1)],
        };
    }

    // the sheet's paper for each look, made once (its noise is the same every time)
    private static (Pcx Art, bool Faithful, Pcx Paper)? _hostPaper;

    private void RepaintDrawnHost()
    {
        var art = Shell.Db.Background(19);
        if (_hostPaper is not { } p || p.Art != art || p.Faithful != Shell.Faithful)
            _hostPaper = p = (art, Shell.Faithful, PaperSheet.Make(art, Shell.Faithful, 1, table: true, lines: HostFormLayout.Rules()));
        Frame.LoadBackground(p.Paper, 0, 0, black: false);
        if (!Shell.Faithful && _sheet is null)
        {
            _sheet = new HostSheet { ShowKit = () => !StoryCar, Shown = () => !Shell.OverlayOpen, Hole = ModalHole };
            Shell.AddLayerOverFrame(_sheet);
        }
    }

    // the frame as the form left it, and the box a pop-up over it has changed since (the modern sheet's vectors keep out of it)
    private byte[]? _drawnPixels;

    private Rect2I? ModalHole()
    {
        if (!Shell.ModalOpen || _drawnPixels is not { } was) return null;
        var now = Frame.Pixels;
        int x0 = ShellFrame.W, y0 = ShellFrame.H, x1 = -1, y1 = -1;
        for (int y = 0; y < ShellFrame.H; y++)
        {
            var a = now.Slice(y * ShellFrame.W, ShellFrame.W);
            var b = was.AsSpan(y * ShellFrame.W, ShellFrame.W);
            if (a.SequenceEqual(b)) continue;
            int l = 0, r = ShellFrame.W - 1;
            while (a[l] == b[l]) l++;
            while (a[r] == b[r]) r--;
            x0 = Math.Min(x0, l); x1 = Math.Max(x1, r); y0 = Math.Min(y0, y); y1 = y;
        }
        return x1 < 0 ? null : new Rect2I(x0, y0, x1 - x0 + 1, y1 - y0 + 1);
    }

    private void LeaveDrawnHost()
    {
        _sheet?.QueueFree();
        _sheet = null;
    }

    /// <summary>The words: each row's name and value, the area list, the car's weapons and parts and its blueprint; in the 1997 look also the title, the headings, the buttons and the list's arrows.</summary>
    private void DrawDrawnHost()
    {
        var f = Hand;
        var rows = HostFormLayout.RowsOf(HostState());
        _hostHot = HostFormLayout.Hot(rows);
        foreach (var r in rows)
        {
            int y = HostFormLayout.RowY(r.Index) + 3;
            int lx = r.Right ? HostFormLayout.R0 + 6 : HostFormLayout.L0 + 6, lw = (r.Right ? HostFormLayout.RDiv : HostFormLayout.LDiv) - lx - 2;
            Frame.Text(f, lx, y, AddressBookPopup.Fit(f, r.Label, lw));
            foreach (var c in r.Cells)
            {
                int w = c.X1 - c.X0 - 9;
                // a field being typed in keeps its end (and the cursor) in view
                bool typing = (r.RowId == 14 && _renaming) || (r.RowId == 22 && _naming);
                string t = typing ? AddressBookPopup.FitTail(f, c.Text, w) : AddressBookPopup.Fit(f, c.Text, w);
                // the - and + in the form's larger hand (font 0x45), as the old Bots line had them
                var cf = c.Text is "-" or "+" ? _font : f;
                int x = c.Centre ? (c.X0 + c.X1 - cf.Measure(t)) / 2 : c.X0 + 6;
                Frame.Text(cf, x, cf == f ? y : y - 1, t);
            }
        }
        // the area list, the arrows' column at its left
        int lx0 = HostFormLayout.ArrowX1 + 6, lw0 = HostFormLayout.L1 - lx0 - 4;
        if (ListCount == 0) Frame.Text(f, lx0, HostFormLayout.RowY(HostFormLayout.ListRow) + 3, Coop ? "No story missions" : "No arenas");
        for (int k = 0; k < HostFormLayout.ListRows && _scroll + k < ListCount; k++)
            Frame.Text(f, lx0, HostFormLayout.RowY(HostFormLayout.ListRow + k) + 3, AddressBookPopup.Fit(f, ListName(_scroll + k), lw0));
        DrawHostKit();
        if (Shell.Faithful) DrawHostSheet1997();
        _drawnPixels = Frame.Pixels.ToArray();
    }

    /// <summary>The car's weapons and parts in two columns under their headings, and the blueprint's side view under them (bank 0x3c, as the form's), centred in its room.</summary>
    private void DrawHostKit()
    {
        if (StoryCar) return;
        var f = Hand;
        var (_, model, variant) = _slots[0];
        var weapons = _data.Weapons(model, variant);
        var kit = _data.Parts(model, variant, Shell.Parts);
        int colW = HostFormLayout.RDiv - HostFormLayout.R0 - 12;
        for (int k = 0; k < HostFormLayout.KitRows; k++)
        {
            int y = HostFormLayout.RowY(HostFormLayout.KitRow + k) + 3;
            if (k < weapons.Count) Frame.Text(f, HostFormLayout.R0 + 6, y, AddressBookPopup.Fit(f, weapons[k], colW));
            if (k < kit.Count) Frame.Text(f, HostFormLayout.RDiv + 6, y, AddressBookPopup.Fit(f, kit[k], HostFormLayout.R1 - HostFormLayout.RDiv - 10));
        }
        if (_cars[2 * _data.Models[model].Art + 1] is not { } side) return;
        // the drawing's own strokes: the sprite carries the form's paper round them, a tone off this sheet's, so only what is darker than
        // paper is put down (pixel by pixel, never an upscale of the whole sprite), its box centred in the room
        var pal = Frame.Palette;
        bool Ink(int i) => side.Opaque[i] && pal[side.Pixels[i] * 3] * 0.299f + pal[side.Pixels[i] * 3 + 1] * 0.587f + pal[side.Pixels[i] * 3 + 2] * 0.114f < 160;
        int bx0 = side.Width, by0 = side.Height, bx1 = -1, by1 = -1;
        for (int y = 0; y < side.Height; y++)
            for (int x = 0; x < side.Width; x++)
                if (Ink(y * side.Width + x)) { bx0 = Math.Min(bx0, x); bx1 = Math.Max(bx1, x); by0 = Math.Min(by0, y); by1 = Math.Max(by1, y); }
        if (bx1 < 0) return;
        var (px0, py0, px1, py1) = HostFormLayout.Picture;
        int ox = (px0 + px1 - (bx1 - bx0 + 1)) / 2 - bx0, oy = (py0 + py1 - (by1 - by0 + 1)) / 2 - by0;
        for (int y = by0; y <= by1; y++)
            for (int x = bx0; x <= bx1; x++)
                if (Ink(y * side.Width + x)) Frame.Fill(ox + x, oy + y, 1, 1, side.Pixels[y * side.Width + x]);
    }

    private static byte[] SolidInk(ShellFont f, byte ink)
    {
        var r = new byte[256];
        Array.Fill(r, ink);
        r[f.Key & 0xff] = 0xff;
        return r;
    }

    /// <summary>
    /// The 1997 look's printed words in the palette, as Mouse Options': the title in font 0x43 (the bullet between its halves drawn, the
    /// face has none), the headings and the buttons' words in 0x47, each struck twice; the list's arrows as outlined triangles.
    /// </summary>
    private void DrawHostSheet1997()
    {
        byte inkIx = Frame.Nearest(ControllerScreen.TitleInk);
        var title = Shell.Db.Font(0x43);
        var ink = SolidInk(title, inkIx);
        string[] halves = HostFormLayout.Title.Split('•');
        int gap = 10, tw = title.Measure(halves[0]) + gap + title.Measure(halves[1]);
        int x = 320 - tw / 2;
        foreach (int dx in new[] { 0, 1 })
        {
            Frame.Text(title, x + dx, 12, halves[0], ink);
            Frame.Text(title, x + title.Measure(halves[0]) + gap + dx, 12, halves[1], ink);
        }
        Frame.Fill(x + title.Measure(halves[0]) + gap / 2 - 2, 12 + title.Height / 2 - 2, 4, 4, inkIx);
        var small = Shell.Db.Font(0x47);
        var dark = SolidInk(small, inkIx);
        void Words(string s, int x0, int x1, int y0, int h)
        {
            string t = ControllerScreen.Fit(s, u => small.Measure(u) + 1, x1 - x0 - 8);
            int tx = (x0 + x1 - small.Measure(t)) / 2, ty = y0 + (h - small.Height) / 2 + 1;
            Frame.Text(small, tx, ty, t, dark);
            Frame.Text(small, tx + 1, ty, t, dark);
        }
        for (int i = 1; i < HostFormLayout.Slots.Length; i++)
        {
            var s = HostFormLayout.Slots[i];
            Words(HostFormLayout.SlotText[i], s.X, s.X + s.W, s.Y, s.H);
        }
        if (!StoryCar)
        {
            int hy = HostFormLayout.RowY(HostFormLayout.KitHeadRow);
            Words("WEAPONS", HostFormLayout.R0, HostFormLayout.RDiv, hy - 1, HostFormLayout.RowH);
            Words("PARTS", HostFormLayout.RDiv, HostFormLayout.R1, hy - 1, HostFormLayout.RowH);
        }
        // the arrows: 11 px across, 6 tall, pointing up on the list's first row and down on its last
        foreach (var (row, up) in new[] { (HostFormLayout.ListRow, true), (HostFormLayout.ListRow + HostFormLayout.ListRows - 1, false) })
        {
            int cx = (HostFormLayout.L0 + HostFormLayout.ArrowX1) / 2, cy = HostFormLayout.RowY(row) + 4;
            for (int i = 0; i < 6; i++)
            {
                int yy = up ? cy + i : cy + 5 - i;
                if (i == 5) Frame.Fill(cx - 5, yy, 11, 1, inkIx);
                else { Frame.Fill(cx - i, yy, 1, 1, inkIx); Frame.Fill(cx + i, yy, 1, 1, inkIx); }
            }
        }
    }

    /// <summary>The area list's rows one by one for the pad (the first and last scroll a press past them).</summary>
    private void HostListRegions(List<PadRegion> l)
    {
        int n = Math.Min(HostFormLayout.ListRows, ListCount - _scroll);
        for (int k = 0; k < n; k++)
        {
            int kk = k;
            l.Add(PadRegion.Of(HostFormLayout.ListRowRect(k), d => d == NavDir.Up && kk == 0 ? ScrollList(-1) : d == NavDir.Down && kk == n - 1 ? ScrollList(1) : false));
        }
    }
}

/// <summary>The modern look's host form: the sheet's rules, the title, the headings, the list's arrows and the button bars, as Mouse Options' (<see cref="MouseSheet"/>).</summary>
public partial class HostSheet : Control
{
    /// <summary>Whether the car's weapons and parts are on the form (not for a co-op seat in the mission's own car).</summary>
    public required Func<bool> ShowKit { get; init; }
    /// <summary>Whether the form is on show (not under the Options Menu and the other notepad overlays, which Esc opens over it).</summary>
    public required Func<bool> Shown { get; init; }
    /// <summary>Where a pop-up over the form is (in the frame's pixels, under this layer): nothing is drawn there.</summary>
    public required Func<Rect2I?> Hole { get; init; }
    private Rect2I? _hole;
    private bool Clear(Rect2 r) => _hole is not { } h || !new Rect2(h.Position, h.Size).Intersects(r);
    private Font? _arial;
    private float _s;
    private Vector2 _off;
    private Vector2 T(Vector2 shell) => _off + shell * _s;

    public override void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;
    }

    public override void _Process(double delta)
    {
        Visible = Shown();
        QueueRedraw();
    }

    public override void _Draw()
    {
        var size = GetViewportRect().Size;
        _s = Mathf.Min(size.X / ShellFrame.W, size.Y / ShellFrame.H);
        _off = (size - new Vector2(ShellFrame.W, ShellFrame.H) * _s) / 2;
        _hole = Hole();
        var rng = new Random(19);
        foreach (var (x, y, w, h) in HostFormLayout.Rules())
        {
            bool across = w >= h;
            int len = across ? w : h;
            for (int k = 0; k < len; k += 6)
            {
                int run = Math.Min(6, len - k);
                var at = across ? new Rect2(x + k, y, run, h) : new Rect2(x, y + k, w, run);
                float alpha = 0.8f + 0.2f * (float)rng.NextDouble();
                if (Clear(at)) DrawRect(new Rect2(T(at.Position), at.Size * _s), new Color(ControllerScreen.RuleColor, alpha));
            }
        }
        // the list's arrows, outlined as the original form's
        foreach (var (row, up) in new[] { (HostFormLayout.ListRow, true), (HostFormLayout.ListRow + HostFormLayout.ListRows - 1, false) })
        {
            float cx = (HostFormLayout.L0 + HostFormLayout.ArrowX1) / 2f + 0.5f, top = HostFormLayout.RowY(row) + 3.5f, bottom = top + 7;
            if (!Clear(new Rect2(cx - 6, top - 1, 12, 9))) continue;
            Vector2[] tri = up ? [new(cx, top), new(cx + 5.5f, bottom), new(cx - 5.5f, bottom), new(cx, top)] : [new(cx - 5.5f, top), new(cx + 5.5f, top), new(cx, bottom), new(cx - 5.5f, top)];
            DrawPolyline([.. tri.Select(T)], ControllerScreen.TitleInk, Math.Max(1f, 1.1f * _s), true);
        }
        _arial ??= HdAssets.Arial(true, TextServer.FontAntialiasing.Gray);
        var arial = _arial;
        if (arial is null) return;
        if (Clear(new Rect2(40, 6, 560, 32))) Caps(arial, HostFormLayout.Title, new Vector2(320, 33), 29f, 0.86f, ControllerScreen.TitleInk, 1.6f);
        if (ShowKit())
        {
            int hy = HostFormLayout.RowY(HostFormLayout.KitHeadRow);
            foreach (var (word, x0, x1) in new[] { ("WEAPONS", HostFormLayout.R0, HostFormLayout.RDiv), ("PARTS", HostFormLayout.RDiv, HostFormLayout.R1) })
                if (Clear(new Rect2(x0, hy, x1 - x0, HostFormLayout.RowH)))
                    Caps(arial, word, new Vector2((x0 + x1) / 2f, hy + 12.5f), 12.5f, 0.94f, ControllerScreen.TitleInk, 0.5f);
        }
        for (int i = 1; i < HostFormLayout.Slots.Length; i++)
        {
            var b = HostFormLayout.Slots[i];
            if (!Clear(new Rect2(b.X, b.Y, b.W, b.H))) continue;
            var r = new Rect2(T(new Vector2(b.X, b.Y)), new Vector2(b.W, b.H) * _s);
            DrawRect(r, ControllerScreen.BarEdge);
            DrawRect(r.Grow(-2 * _s), ControllerScreen.BarColor);
            // a long name (CONFIGURE CHASSIS) is set smaller and narrower to keep its words whole
            string text = HostFormLayout.SlotText[i];
            float px = 14.6f, narrow = 0.94f;
            float Width(float p, float n) => arial.GetStringSize(text, HorizontalAlignment.Left, -1, Math.Max(1, (int)MathF.Round(p * _s))).X / _s * n;
            while (Width(px, narrow) > b.W - 14 && px > 11f) { px -= 0.5f; narrow = Math.Max(0.8f, narrow - 0.02f); }
            Caps(arial, text, new Vector2(b.X + b.W / 2f, b.Y + 9.5f + px * 0.38f), px, narrow, ControllerScreen.TitleInk, 0.7f);
        }
    }

    private void Caps(Font f, string s, Vector2 at, float px, float narrow, Color c, float bold = 0)
    {
        int size = Math.Max(1, (int)MathF.Round(px * _s));
        float w = f.GetStringSize(s, HorizontalAlignment.Left, -1, size).X * narrow;
        var p = T(at);
        DrawSetTransform(new Vector2(p.X - w / 2, p.Y), 0, new Vector2(narrow, 1));
        if (bold > 0) DrawStringOutline(f, Vector2.Zero, s, HorizontalAlignment.Left, -1, size, Math.Max(1, (int)MathF.Round(bold * _s)), c);
        DrawString(f, Vector2.Zero, s, HorizontalAlignment.Left, -1, size, c);
        DrawSetTransform(Vector2.Zero, 0, Vector2.One);
    }
}
