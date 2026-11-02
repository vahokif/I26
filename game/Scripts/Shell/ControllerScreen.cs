using Godot;
using I76.Formats.Input;
using I76.Formats.Shell;

namespace I76.Game.Shell;

/// <summary>
/// The port's controller screen (Control Configuration's Calibrate button): the pad bindings, on a drawn controller. Nothing here is the
/// original's or in a file: it is drawn at runtime in Control Configuration's own visual language (record 16 is read for its palette
/// alone) -- the paper, the title in heavy caps, the double-ruled frame round a table of rows a font height + 5 apart, the flat orange
/// buttons on the original's slots -- with a vector pad in the middle, in ink. Each pad input that does something has a row in the
/// column on its side, in the shell's handwriting, and a leader to it. Pressing a pad input marks it on the drawing and its row.
/// A click on an input or its row, or A on the one the D-pad has selected, opens the shell's list of what it can do.
/// The D-pad, A, B and Start drive the screen; the sticks, triggers and other buttons only show (PadMenu). In the 1997 look the
/// sheet, its rules and its words are the shell's pixels (its palette and bitmap fonts) and the drawing is made at 640x480.
/// It edits only the joystick blocks of its own copy of the bindings (the Control Configuration screen edits keys and mouse only);
/// Done hands the copy back to that screen, Cancel and Esc hand back what it gave.
/// </summary>
public sealed class ControllerScreen : OverlayScreen
{
    protected override int Record => 16;
    private readonly BindingTable _given, _map;
    private ShellFont _f = null!;
    private PadDiagram? _diagram;
    private Control? _layer;
    private int _sel;
    private readonly PadMenu _menu = new();
    private bool _replugged, _hadPad;

    public ControllerScreen(BindingTable pending)
    {
        _given = pending;
        _map = pending.Clone();
        // a map with no pad block at all gets the preset's pad side, as it would the moment a pad is plugged in (Controls)
        _map.AddPadSideIfNone(Controls.PadDefaults());
    }

    // ---- the sheet, in shell pixels: record 16's frame, rows and buttons ------------------------------

    /// <summary>The double rule round the content (outer 2 px, a 2 px gap, inner 1 px), as Control Configuration's tables have it.</summary>
    internal static readonly Rect2I Outer = new(20, 37, 600, 402);
    internal const int Inset = 4;
    /// <summary>The rows: the original table's pitch (font 0x44's height + 5), from the inner rule down.</summary>
    internal const int RowH = 17, RowTop = 42, Rows = 23;
    /// <summary>The columns' inner rules; the drawing is between them.</summary>
    internal const int LeftRule = 170, RightRule = 470;
    private const int LeftText = 29, RightText = 476;
    /// <summary>A label's room in its column: from its x to the column's rule (the right one to the frame's inner rule), a margin short.</summary>
    private const int LeftRoom = LeftRule - LeftText - 3, RightRoom = 620 - Inset - 2 - RightText - 3;

    /// <summary>A sentence broken at spaces into lines no wider than <paramref name="room"/>.</summary>
    private IEnumerable<string> Words(string text, int room)
    {
        var line = "";
        foreach (var w in text.Split(' '))
        {
            var next = line.Length == 0 ? w : line + " " + w;
            if (line.Length > 0 && _f.Measure(next) > room) { yield return line; line = w; }
            else line = next;
        }
        if (line.Length > 0) yield return line;
    }

    /// <summary>Each label kept inside its column: one too long breaks at the last space that fits, onto the next ruled row.</summary>
    private string[] Wrap(string[] lines, int room)
    {
        var o = new List<string>();
        foreach (var l in lines)
        {
            var rest = l;
            while (_f.Measure(rest) > room && rest.LastIndexOf(' ') > 0)
            {
                int cut = rest.Length;
                while ((cut = rest.LastIndexOf(' ', cut - 1)) > 0 && _f.Measure(rest[..cut]) > room) { }
                if (cut <= 0) break;
                o.Add(rest[..cut]);
                rest = "  " + rest[(cut + 1)..];
            }
            o.Add(rest);
        }
        return [.. o];
    }
    /// <summary>The original's button slots, y 450..469: Calibrate's is the port's Dead Zones button here; Restore, Done and Cancel keep theirs.</summary>
    internal static readonly Rect2I[] Slots = [new(9, 450, 138, 19), new(168, 450, 140, 19), new(330, 450, 140, 19), new(490, 450, 140, 19)];
    internal static readonly string[] SlotText = ["DEAD ZONES", "RESTORE PAD DEFAULTS", "DONE", "CANCEL"];
    internal const string Title = "CONTROLLER CONFIGURATION";

    internal static int RowY(int slot) => RowTop + slot * RowH;

    /// <summary>The sheet's rules as rectangles: the frame's double rule, the columns' rules and their rows.</summary>
    internal static IEnumerable<Rect2I> FrameRules()
    {
        int x0 = Outer.Position.X, y0 = Outer.Position.Y, x1 = Outer.End.X, y1 = Outer.End.Y;
        yield return new(x0, y0, x1 - x0, 2); yield return new(x0, y1 - 2, x1 - x0, 2);
        yield return new(x0, y0, 2, y1 - y0); yield return new(x1 - 2, y0, 2, y1 - y0);
        int i0 = x0 + Inset, j0 = y0 + Inset, i1 = x1 - Inset, j1 = y1 - Inset;
        yield return new(i0, j0, i1 - i0, 1); yield return new(i0, j1 - 1, i1 - i0, 1);
        yield return new(i0, j0, 1, j1 - j0); yield return new(i1 - 1, j0, 1, j1 - j0);
        // the columns' rules, doubled as the original's two tables meet
        yield return new(LeftRule, j0, 1, j1 - j0); yield return new(RightRule, j0, 1, j1 - j0);
        yield return new(LeftRule + 3, j0, 1, j1 - j0); yield return new(RightRule - 3, j0, 1, j1 - j0);
        for (int k = 1; k < Rows; k++)
        {
            int y = RowY(k) - 1;
            yield return new(i0, y, LeftRule - i0, 1);
            yield return new(RightRule + 1, y, i1 - RightRule - 1, 1);
        }
    }

    /// <summary>The two-column table's rules as rectangles, matching Control Configuration (record 16): outer double frame, centre double divider, name/value dividers and full-width row rules.</summary>
    internal static IEnumerable<Rect2I> TableRules()
    {
        int x0 = Outer.Position.X, y0 = Outer.Position.Y, x1 = Outer.End.X, y1 = Outer.End.Y;
        yield return new(x0, y0, x1 - x0, 2); yield return new(x0, y1 - 2, x1 - x0, 2);
        yield return new(x0, y0, 2, y1 - y0); yield return new(x1 - 2, y0, 2, y1 - y0);
        int i0 = x0 + Inset, j0 = y0 + Inset, i1 = x1 - Inset, j1 = y1 - Inset;
        yield return new(i0, j0, i1 - i0, 1); yield return new(i0, j1 - 1, i1 - i0, 1);
        yield return new(i0, j0, 1, j1 - j0); yield return new(i1 - 1, j0, 1, j1 - j0);
        // the two tables' meeting rule in the middle (doubled), and each table's name/binding rule (x 177 on the left, x 475 on the right)
        yield return new(318, j0, 1, j1 - j0); yield return new(321, j0, 1, j1 - j0);
        yield return new(177, j0, 1, j1 - j0); yield return new(475, j0, 1, j1 - j0);
        for (int k = 1; k < Rows; k++)
        {
            int y = RowY(k) - 1;
            yield return new(i0, y, 318 - i0, 1);
            yield return new(322, y, i1 - 322, 1);
        }
    }

    // ---- the drawing, in the sheet's units: a pad's own coordinates about C ---------------------------

    internal static readonly Vector2 C = new(320, 244);
    internal static Vector2 P(Vector2 rel) => C + rel;

    internal sealed class El
    {
        public required string Key, Channel, Group;
        public bool Axis, Left;
        /// <summary>Its outline, in pad coordinates: hit, marked and pointed at by its leader.</summary>
        public Vector2[] Poly = [];
        /// <summary>Where the leader ends, when not on the outline nearest the row (the D-pad's arms: their tips).</summary>
        public Vector2? Tip;
        public required Func<PadRaw, float> Live;
        /// <summary>The sticks' axes: their part of the stick (a direction), instead of the outline.</summary>
        public Func<Vector2, bool>? Hit;
        public string[] Lines = [];
        public bool HasRow;
        public int Slot;
        public Vector2 Start, Anchor;
        public Rect2I Row => new(Left ? LeftRule - 145 : RightRule + 1, RowY(Slot), 144, Lines.Length * RowH);
        public Vector2 Centre => Poly.Length == 0 ? Vector2.Zero : Poly.Aggregate(Vector2.Zero, (a, p) => a + p) / Poly.Length;
    }

    internal static readonly Vector2 LeftStick = new(-55, -18), RightStick = new(29, 12), DPad = new(-29, 12);
    internal const float StickR = 19, CapR = 15, FaceR = 7.5f;
    /// <summary>The D-pad cross: half an arm's width and an arm's length from the middle (about 12 % of the body's width across).</summary>
    internal const float DpA = 4.8f, DpR = 14.5f;
    internal static readonly Vector2 BackAt = new(-17, -27), StartAt = new(17, -27), GuideAt = new(0, -41);
    internal static readonly (string Key, Vector2 At)[] Face = [("Y", new(55, -34)), ("X", new(39, -18)), ("B", new(71, -18)), ("A", new(55, -2))];

    internal static Vector2[] Circle(Vector2 c, float r, int n = 28) => [.. Enumerable.Range(0, n).Select(k => c + r * Vector2.FromAngle(k * Mathf.Tau / n))];
    private static Vector2[] Box(float x0, float y0, float x1, float y1) => [new(x0, y0), new(x1, y0), new(x1, y1), new(x0, y1)];

    /// <summary>The body's outline: a closed Catmull-Rom spline through its half mirrored.</summary>
    internal static readonly Vector2[] Body = MakeBody();

    private static Vector2[] MakeBody()
    {
        var half = new Vector2[] { new(26, -53), new(60, -52), new(88, -44), new(105, -28), new(114, -4), new(119, 24), new(118, 50), new(108, 70), new(94, 76), new(82, 66), new(68, 54), new(50, 49), new(28, 47) };
        var pts = new List<Vector2> { new(0, -53) };
        pts.AddRange(half);
        pts.Add(new(0, 45));
        for (int i = half.Length - 1; i >= 0; i--) pts.Add(new Vector2(-half[i].X, half[i].Y));
        return Spline(pts, 12);
    }

    internal static Vector2[] Spline(IReadOnlyList<Vector2> p, int steps, bool closed = true)
    {
        var o = new List<Vector2>();
        int n = p.Count;
        for (int i = 0; i < (closed ? n : n - 1); i++)
        {
            Vector2 p0 = p[closed ? (i + n - 1) % n : Math.Max(0, i - 1)], p1 = p[i], p2 = p[(i + 1) % n], p3 = p[closed ? (i + 2) % n : Math.Min(n - 1, i + 2)];
            for (int k = 0; k < steps; k++)
            {
                float t = (float)k / steps;
                o.Add(0.5f * (2 * p1 + (p2 - p0) * t + (2 * p0 - 5 * p1 + 4 * p2 - p3) * t * t + (3 * p1 - p0 - 3 * p2 + p3) * t * t * t));
            }
        }
        if (!closed) o.Add(p[n - 1]);
        return [.. o];
    }

    /// <summary>A band along the body's top edge between two x, from <paramref name="near"/> to <paramref name="far"/> outside it, its ends tapered: the shoulders.</summary>
    private static Vector2[] Band(float x0, float x1, float near, float far)
    {
        var top = Body.Where(p => p.Y < -30 && p.X >= Math.Min(x0, x1) && p.X <= Math.Max(x0, x1)).OrderBy(p => p.X).ToList();
        var mid = new Vector2(0, 10);
        Vector2 Out(int i)
        {
            var a = top[Math.Max(0, i - 1)]; var b = top[Math.Min(top.Count - 1, i + 1)];
            var n = new Vector2(b.Y - a.Y, a.X - b.X).Normalized();
            return n.Dot(top[i] - mid) < 0 ? -n : n;
        }
        int m = top.Count;
        var inner = top.Select((p, i) => p + Out(i) * near).ToList();
        var outer = top.Select((p, i) =>
        {
            float t = Math.Min(1, Math.Min(i, m - 1 - i) / (m * 0.22f));
            return p + Out(i) * (near + (far - near) * (0.35f + 0.65f * Mathf.Sqrt(t)));
        }).ToList();
        outer.Reverse();
        return [.. inner, .. outer];
    }

    private static float Pad(PadRaw p, int b) => p.Has(b) ? 1 : 0;

    internal readonly El[] Els = Make();

    private static El[] Make()
    {
        El B(string key, string ch, int btn, bool left, Vector2[] poly, string? group = null, Vector2? tip = null) => new()
        { Key = key, Channel = ch, Group = group ?? key, Left = left, Poly = poly, Tip = tip, Live = p => Pad(p, btn) };
        El Ax(string key, string ch, bool left, Vector2[] poly, Func<PadRaw, float> live, string? group = null, Func<Vector2, bool>? hit = null) => new()
        { Key = key, Channel = ch, Group = group ?? key, Axis = true, Left = left, Poly = poly, Live = live, Hit = hit };
        bool In(Vector2 v, Vector2 c, out Vector2 d) { d = v - c; return d.Length() <= StickR + 2; }
        Func<Vector2, bool> Dir(Vector2 c, bool y) => v => In(v, c, out var d) && d.Length() >= CapR - 2 && (Math.Abs(d.Y) > Math.Abs(d.X)) == y;
        var ls = Circle(LeftStick, StickR); var rs = Circle(RightStick, StickR);
        float a = DpA, r = DpR;
        Vector2 dp = DPad;
        return
        [
            Ax("LT", "Throttle", true, Band(-100, -64, 8, 19), p => p.TriggerLeft),
            B("LB", "Button5", PadButton.LeftShoulder, true, Band(-102, -40, 2, 10)),
            Ax("LSY", "Down/Up", true, ls, p => Math.Abs(p.LeftY), "LS", Dir(LeftStick, true)),
            Ax("LSX", "Left/Right", true, ls, p => Math.Abs(p.LeftX), "LS", Dir(LeftStick, false)),
            B("L3", "Button9", PadButton.LeftStick, true, Circle(LeftStick, CapR), "LS"),
            B("Back", "Button7", PadButton.Back, true, Circle(BackAt, 4.5f, 16)),
            B("DUp", "HatUp", PadButton.DpadUp, true, Box(dp.X - a, dp.Y - r, dp.X + a, dp.Y - a), "D", dp + new Vector2(0, -r)),
            B("DLeft", "HatLeft", PadButton.DpadLeft, true, Box(dp.X - r, dp.Y - a, dp.X - a, dp.Y + a), "D", dp + new Vector2(-r, 0)),
            B("DRight", "HatRight", PadButton.DpadRight, true, Box(dp.X + a, dp.Y - a, dp.X + r, dp.Y + a), "D", dp + new Vector2(r, 0)),
            B("DDown", "HatDown", PadButton.DpadDown, true, Box(dp.X - a, dp.Y + a, dp.X + a, dp.Y + r), "D", dp + new Vector2(0, r)),
            B("Start", "Button8", PadButton.Start, false, Circle(StartAt, 4.5f, 16)),
            Ax("RT", "Throttle", false, Band(64, 100, 8, 19), p => p.TriggerRight),
            B("RB", "Button6", PadButton.RightShoulder, false, Band(40, 102, 2, 10)),
            .. Face.Select(f => B(f.Key, f.Key switch { "A" => "Button1", "B" => "Button2", "X" => "Button3", _ => "Button4" },
                f.Key switch { "A" => PadButton.A, "B" => PadButton.B, "X" => PadButton.X, _ => PadButton.Y }, false, Circle(f.At, FaceR, 20))),
            Ax("RSY", "5thAxis", false, rs, p => Math.Abs(p.RightY), "RS", Dir(RightStick, true)),
            Ax("RSX", "Rudder", false, rs, p => Math.Abs(p.RightX), "RS", Dir(RightStick, false)),
            B("R3", "Button10", PadButton.RightStick, false, Circle(RightStick, CapR), "RS"),
        ];
    }

    /// <summary>The order the D-pad walks them in: the left side top to bottom, then the right.</summary>
    private static readonly string[] Walk = ["LT", "LB", "LSY", "LSX", "L3", "Back", "DUp", "DLeft", "DRight", "DDown", "Start", "RT", "RB", "Y", "X", "B", "A", "RSY", "RSX", "R3"];
    private El Selected => Els.First(e => e.Key == Walk[_sel]);

    // ---- what each input does ------------------------------------------------------------------------

    private static readonly int[] Entries = [.. ControlsScreen.LeftRows, .. ControlsScreen.RightRows];
    private static string NameOf(int entry) => ControlsScreen.Names[Array.IndexOf(Entries, entry)].Text;

    /// <summary>What an axis can do: the throttle, the steer, or the camera (the chase camera's track rows; the free look in the car).</summary>
    private static readonly string[] AxisItems = ["Accelerate / Brake", "Steer", "Camera / Look"];
    private static bool IsY(string channel) => channel.Equals("Down/Up", StringComparison.OrdinalIgnoreCase) || channel.Equals("5thAxis", StringComparison.OrdinalIgnoreCase);

    private string[] Describe(El e)
    {
        var lines = new List<string>();
        if (e.Axis)
        {
            foreach (var row in AxisControls.Rows)
            {
                if (_map.AxisOf(row) is not { } a || !a.Channel.Equals(e.Channel, StringComparison.OrdinalIgnoreCase)) continue;
                if (row == "throttle" && e.Channel == "Throttle") lines.Add((e.Key == "RT") == a.Plus ? "Brake" : "Accelerate");
                else lines.Add(row switch { "throttle" => AxisItems[0], "steer" => AxisItems[1], _ => AxisItems[2] });
            }
            return [.. lines.Distinct()];
        }
        foreach (var entry in Entries)
        {
            var cmd = Controls.Commands[entry];
            foreach (var b in _map.Of(cmd))
                if (BindingTable.IsPad(b) && b.Terms[0].Plus && VirtualStick.DiscreteId(b.Terms[0].Channel) == VirtualStick.DiscreteId(e.Channel)) { lines.Add(NameOf(entry)); break; }
        }
        return [.. lines.Distinct()];
    }

    // ---- the rows and their leaders --------------------------------------------------------------------

    /// <summary>
    /// The rows: each side's inputs that do something, a row each (or one per thing they do) in the column on their side, as near their
    /// height on the drawing as the rows allow; then the order that has the fewest leaders crossing each other or another input.
    /// </summary>
    private void Layout()
    {
        foreach (var e in Els) { e.Lines = Wrap(Describe(e), e.Left ? LeftRoom : RightRoom); e.HasRow = e.Lines.Length > 0; }
        // a stick whose two axes do the one thing (the camera) has one row, its X's
        foreach (var (x, y) in new[] { ("LSX", "LSY"), ("RSX", "RSY") })
            if (Els.First(e => e.Key == x) is { HasRow: true } ex && Els.First(e => e.Key == y) is { HasRow: true } ey && ex.Lines.SequenceEqual(ey.Lines))
                ey.HasRow = false;
        foreach (var side in new[] { true, false })
        {
            var order = Els.Where(e => e.Left == side && e.HasRow).OrderBy(e => (e.Tip ?? e.Centre).Y).ThenBy(e => side ? -e.Centre.X : e.Centre.X).ToList();
            float best = Place(order);
            for (bool better = true; better;)
            {
                better = false;
                for (int i = 0; i < order.Count; i++)
                    for (int j = i + 1; j < order.Count; j++)
                    {
                        (order[i], order[j]) = (order[j], order[i]);
                        float c = Place(order);
                        if (c < best - 0.5f) { best = c; better = true; }
                        else (order[i], order[j]) = (order[j], order[i]);
                    }
            }
            Place(order);
        }
    }

    /// <summary>The rows for this order (as near each one's height as the rows above allow), the leaders, and what they cost.</summary>
    private float Place(List<El> order)
    {
        int next = 0;
        foreach (var e in order)
        {
            float y = P(e.Tip ?? e.Centre).Y;
            int want = (int)MathF.Round((y - e.Lines.Length * RowH / 2f - RowTop) / RowH);
            e.Slot = Math.Clamp(Math.Max(want, next), 0, Rows - e.Lines.Length);
            next = e.Slot + e.Lines.Length;
        }
        int limit = Rows;
        for (int i = order.Count - 1; i >= 0; i--)
        {
            if (order[i].Slot + order[i].Lines.Length > limit) order[i].Slot = limit - order[i].Lines.Length;
            limit = order[i].Slot;
        }
        float cost = 0;
        foreach (var e in order)
        {
            e.Start = new Vector2(e.Left ? LeftRule : RightRule, RowY(e.Slot) + e.Lines.Length * RowH / 2f);
            e.Anchor = e.Tip is { } t ? P(t) : Nearest(e.Poly.Select(P).ToArray(), e.Start);
            cost += Math.Abs(e.Start.Y - e.Anchor.Y) * 0.5f;
        }
        for (int i = 0; i < order.Count; i++)
        {
            for (int j = i + 1; j < order.Count; j++)
                if (Cross(order[i].Start, order[i].Anchor, order[j].Start, order[j].Anchor)) cost += 1000;
            foreach (var o in Els)
                if (o.Group != order[i].Group && Through(order[i].Start, order[i].Anchor, o.Poly.Select(P).ToArray())) cost += 150;
        }
        return cost;
    }

    internal static Vector2 Nearest(Vector2[] poly, Vector2 p)
    {
        Vector2 best = poly[0];
        float bd = float.MaxValue;
        for (int i = 0; i < poly.Length; i++)
        {
            var q = Geometry2D.GetClosestPointToSegment(p, poly[i], poly[(i + 1) % poly.Length]);
            float d = q.DistanceSquaredTo(p);
            if (d < bd) { bd = d; best = q; }
        }
        return best;
    }

    private static bool Cross(Vector2 a, Vector2 b, Vector2 c, Vector2 d) => Geometry2D.SegmentIntersectsSegment(a, b, c, d).VariantType != Variant.Type.Nil;

    /// <summary>Whether the segment runs through the outline (an end touching it is not).</summary>
    private static bool Through(Vector2 a, Vector2 b, Vector2[] poly)
    {
        var aa = a + (b - a) * 0.02f; var bb = b - (b - a) * 0.06f;
        for (int i = 0; i < poly.Length; i++)
            if (Cross(aa, bb, poly[i], poly[(i + 1) % poly.Length])) return true;
        return Geometry2D.IsPointInPolygon(bb, poly);
    }

    // ---- the screen ----------------------------------------------------------------------------------

    public override void Enter()
    {
        Label = Shell.Db.Font(0x46);
        Small = Shell.Db.Font(0x47);
        _f = Shell.Db.Font(0x44);
        _diagram = new PadDiagram { Screen = this, Faithful = Shell.Faithful };
        if (Shell.Faithful)
        {
            // the 1997 look: the drawing made at the shell's 640x480 and shown with its pixels, over the frame
            var vp = new SubViewport { Size = new Vector2I(ShellFrame.W, ShellFrame.H), TransparentBg = true, Disable3D = true, RenderTargetUpdateMode = SubViewport.UpdateMode.Always };
            vp.AddChild(_diagram);
            var view = new TextureRect
            {
                AnchorRight = 1, AnchorBottom = 1, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                TextureFilter = CanvasItem.TextureFilterEnum.Nearest, MouseFilter = Control.MouseFilterEnum.Ignore, Texture = vp.GetTexture(),
            };
            view.AddChild(vp);
            _layer = view;
        }
        else _layer = _diagram;
        Shell.AddLayerOverFrame(_layer);
        Input.JoyConnectionChanged += Replugged;
        _hadPad = Controls.HasPad;
        _menu.Settle();
        _loading = true;
    }

    /// <summary>The drawing's layer first shows a frame after it is added: the sheet is loaded on the next tick, so the two come up together (not a black or half-made frame between).</summary>
    private bool _loading;

    public override void Leave()
    {
        Input.JoyConnectionChanged -= Replugged;
        _layer?.QueueFree();
        _layer = null;
        _diagram = null;
    }

    /// <summary>A pad came or went: the notice and the drawing's pad are the new one's (Controls reads the pads afresh each frame).</summary>
    private void Replugged(long device, bool connected) => _replugged = true;

    protected override void Build() { }

    internal El Current => Selected;
    internal Color Pal(int i) => Color.Color8(Frame.Palette[i * 3], Frame.Palette[i * 3 + 1], Frame.Palette[i * 3 + 2]);
    /// <summary>The ink: record 16's darkest stroke (palette 213), as Control Configuration's words.</summary>
    internal Color InkColor => Pal(213);
    internal bool ShowPad = true;

    /// <summary>The sheet's own colours (measured off record 16), in the 1997 look the palette's nearest.</summary>
    internal Color Tone(Color c) => Shell.Faithful ? Pal(Frame.Nearest(c)) : c;
    internal static readonly Color RuleColor = Color.Color8(177, 141, 74), BarColor = Color.Color8(239, 165, 82), BarEdge = Color.Color8(73, 65, 41),
        TitleInk = Color.Color8(82, 66, 41), Marker = Color.Color8(222, 96, 38), RedPencil = Color.Color8(150, 36, 22);

    private void Center(ShellFont f, int cx, int y, string s, byte[]? ink = null) => Frame.Text(f, cx - f.Measure(s) / 2, y, s, ink ?? ShellFrame.OverlayInk);

    /// <summary>The notice under the frame's top and the help over its foot, in the middle of the sheet.</summary>
    private static string[] Notice => Controls.HasPad
        ? ["Click an input, or pick it with the D-pad", "and press A, to choose what it does"]
        : ["No controller connected"];
    private static readonly string[] Help = ["Right-click or Delete clears the selected input.", "Start is Done, B is Cancel."];

    protected override void DrawExtra()
    {
        Layout();
        if (Shell.Faithful) DrawSheet1997();
        var notice = Notice;
        for (int k = 0; k < notice.Length; k++) Center(_f, 320, RowY(k) + 3, notice[k]);
        // the help between the columns' rules, wrapped to their room and set on the bottom rows
        var help = Help.SelectMany(h => Words(h, RightRule - LeftRule - 12)).ToArray();
        for (int k = 0; k < help.Length; k++) Center(_f, (LeftRule + RightRule) / 2, RowY(Rows - help.Length + k) + 3, help[k]);
        foreach (var e in Els.Where(e => e.HasRow))
            for (int k = 0; k < e.Lines.Length; k++) Frame.Text(_f, e.Left ? LeftText : RightText, RowY(e.Slot + k) + 3, e.Lines[k], ShellFrame.OverlayInk);
        // the letters on the face buttons
        foreach (var (key, at) in Face)
        {
            var c = P(at);
            Center(_f, (int)MathF.Round(c.X) + 1, (int)MathF.Round(c.Y) - _f.Height / 2, key);
        }
    }

    /// <summary>The 1997 look's words of the sheet in the shell's pixels: the title in font 0x43 (the shell's heavy caps), the buttons' in 0x47, each struck twice.</summary>
    private void DrawSheet1997()
    {
        var title = Shell.Db.Font(0x43);
        var ink = Solid(title, Frame.Nearest(TitleInk));
        int x = 320 - title.Measure(Title) / 2;
        Frame.Text(title, x, 12, Title, ink);
        Frame.Text(title, x + 1, 12, Title, ink);
        // the buttons' words in the shell's small sans, struck twice for weight
        var small = Shell.Db.Font(0x47);
        var dark = Solid(small, Frame.Nearest(TitleInk));
        for (int i = 0; i < Slots.Length; i++)
        {
            var s = Slots[i];
            string text = Fit(SlotText[i], t => small.Measure(t) + 1, s.Size.X - 8);
            int tx = s.Position.X + (s.Size.X - small.Measure(text)) / 2, ty = s.Position.Y + (s.Size.Y - small.Height) / 2 + 1;
            Frame.Text(small, tx, ty, text, dark);
            Frame.Text(small, tx + 1, ty, text, dark);
        }
    }

    /// <summary>A remap that draws every inked pixel of the font in one palette index (the key stays clear).</summary>
    private static byte[] Solid(ShellFont f, byte ink)
    {
        var r = new byte[256];
        Array.Fill(r, ink);
        r[f.Key & 0xff] = 0xff;
        return r;
    }

    /// <summary>The button's words, shortened to the slot ("RESTORE PAD DEFAULTS", then "RESTORE DEFAULTS", then "RESTORE").</summary>
    internal static string Fit(string text, Func<string, float> measure, float room)
    {
        if (measure(text) <= room) return text;
        var shorter = text.Replace(" PAD", "");
        return measure(shorter) <= room ? shorter : text.Split(' ')[0];
    }

    private void Back() => Shell.Overlay(new ControlsScreen(_given));
    private void Done() => Shell.Overlay(new ControlsScreen(_map));
    protected override void Escape() => Back();

    // ---- editing ---------------------------------------------------------------------------------

    private El? ElementAt(Vector2I m)
    {
        foreach (var e in Els)
            if (e.HasRow && e.Row.HasPoint(m)) return e;
        var rel = (Vector2)m - C;
        foreach (var e in Els.Reverse())
        {
            if (e.Hit is { } h) { if (h(rel)) return e; continue; }
            if (Geometry2D.IsPointInPolygon(rel, e.Poly) || Nearest(e.Poly, rel).DistanceTo(rel) <= 2) return e;
        }
        return null;
    }

    private void Clear(El e)
    {
        Shell.Sound(82);
        _map.ClearInput(e.Channel);
        Redraw();
    }

    private void Open(El e, bool byPad)
    {
        Shell.Sound(82);
        _sel = Array.IndexOf(Walk, e.Key);
        ShowPad = false;      // the list is a sprite over the sheet's middle: the drawing would show through it
        var items = new List<string> { "(none)" };
        if (e.Axis) items.AddRange(AxisItems);
        else foreach (var n in Entries) items.Add(NameOf(n));
        var s = Shell.Db.Bank(0x24)[16];
        int w = s?.Width ?? 172, h = s?.Height ?? 235;
        Shell.Modal(new ListPopup
        {
            Sprite = 16, X = 320 - w / 2, Y = 240 - h / 2, TextX = 0x16, TextY = 0x30, Visible = 9, Items = items, Font = _f,
            Up = (18, 36, 12, 25), Down = (18, 36, 204, 217), List = (16, 214, 47, 198), Cancel = (88, 159, 209, 225),
            PadPicks = true, CursorShown = byPad,
            Picked = i =>
            {
                _menu.Settle();      // the A that picked (or the B that cancelled) does nothing here when the list closes
                ShowPad = true;
                if (i == 0) _map.ClearInput(e.Channel);
                else if (i > 0 && e.Axis) _map.AssignAxis(i switch { 1 => "throttle", 2 => "steer", _ => IsY(e.Channel) ? "track_pitch_delta" : "track_yaw_delta" }, "joystick1", e.Channel);
                else if (i > 0) _map.Assign(Controls.Commands[Entries[i - 1]], "joystick1", e.Channel);
                Redraw();
            },
        });
    }

    protected override void ClickElsewhere(Vector2I m)
    {
        for (int i = 0; i < Slots.Length; i++)
            if (Slots[i].HasPoint(m))
            {
                Shell.Sound(82);
                if (i == 0) Shell.Overlay(new PadDeadZoneScreen(_map));
                else if (i == 1) { _map.ResetSide(Controls.PadDefaults(), true); Redraw(); }
                else if (i == 2) Done(); else Back();
                return;
            }
        if (ElementAt(m) is { } e) Open(e, false);
    }

    internal PadRaw ReadPad() => Controls.PadSource.Read() is { Count: > 0 } l ? l[0] : default;

    private void Step(int d) { _sel = (_sel + d + Walk.Length) % Walk.Length; Shell.Sound(82); }

    public override void Tick()
    {
        if (_loading)
        {
            _loading = false;
            Frame.LoadBackground(PaperSheet.Make(Shell.Db.Background(Record), Shell.Faithful, 0), 0, 0, black: false);
            Redraw();
            return;
        }
        // a pad plugged in or pulled out: the notice says so at once
        if (_replugged || Controls.HasPad != _hadPad) { _replugged = false; _hadPad = Controls.HasPad; Redraw(); }
        // the D-pad, A, B and Start only; a stick, a trigger or any other button just shows on the drawing
        var act = _menu.Update(ReadPad());
        if ((act & PadMenu.Act.Prev) != 0) Step(-1);
        if ((act & PadMenu.Act.Next) != 0) Step(1);
        if ((act & PadMenu.Act.Open) != 0) { Open(Selected, true); return; }
        if ((act & PadMenu.Act.Back) != 0) { Back(); return; }
        if ((act & PadMenu.Act.Done) != 0) { Done(); return; }
        foreach (var k in Shell.Keys)
        {
            if (k is Key.Up or Key.Left) Step(-1);
            else if (k is Key.Down or Key.Right) Step(1);
            else if (k is Key.Enter or Key.Space or Key.KpEnter) { Open(Selected, false); return; }
            else if (k is Key.Delete or Key.Backspace) Clear(Selected);
        }
        if (Shell.RightClicked && ElementAt(Shell.Mouse) is { } e) { _sel = Array.IndexOf(Walk, e.Key); Clear(e); return; }
        base.Tick();
    }
}

/// <summary>
/// The sheet: aged paper in the shell's own palette (the nearest of it to each tone of record 16's paper, measured), stained, mottled,
/// grained and browned at the edges, with faint ruling; in the 1997 look also its rules and button bars, since they are pixels there.
/// </summary>
public static class PaperSheet
{
    /// <param name="lines">The 1997 look's rules when they are not Control Configuration's (the host form's own sheet), as rectangles.</param>
    public static Pcx Make(Pcx art, bool rules, int firstButton = 1, bool table = false, IEnumerable<(int X, int Y, int W, int H)>? lines = null)
    {
        const int W = 640, H = 480;
        var pal = art.Palette;
        byte Near(float r, float g, float b)
        {
            int best = 0, bd = int.MaxValue;
            for (int i = 0; i < 256; i++)
            {
                float dr = pal[i * 3] - r, dg = pal[i * 3 + 1] - g, db = pal[i * 3 + 2] - b;
                int d = (int)(dr * dr + dg * dg + db * db);
                if (d < bd) { bd = d; best = i; }
            }
            return (byte)best;
        }
        // record 16's paper runs (231,173,74) to (247,206,123), most of it near (239,183,88)
        var dark = new Vector3(222, 162, 70); var light = new Vector3(250, 210, 128);
        const int Shades = 32;
        var shade = new byte[Shades];
        for (int i = 0; i < Shades; i++) { var c = dark.Lerp(light, i / (float)(Shades - 1)); shade[i] = Near(c.X, c.Y, c.Z); }
        var rng = new Random(76);
        float[] Grid(int gw, int gh) { var g = new float[gw * gh]; for (int i = 0; i < g.Length; i++) g[i] = (float)rng.NextDouble(); return g; }
        float Noise(float[] g, int gw, int gh, float x, float y)
        {
            int ix = (int)x, iy = (int)y; float tx = x - ix, ty = y - iy;
            tx = tx * tx * (3 - 2 * tx); ty = ty * ty * (3 - 2 * ty);
            float V(int a, int b) => g[Math.Clamp(b, 0, gh - 1) * gw + Math.Clamp(a, 0, gw - 1)];
            return (V(ix, iy) * (1 - tx) + V(ix + 1, iy) * tx) * (1 - ty) + (V(ix, iy + 1) * (1 - tx) + V(ix + 1, iy + 1) * tx) * ty;
        }
        var stain = Grid(24, 18); var mottle = Grid(82, 62); var fibre = Grid(162, 482); var edge = Grid(130, 98);
        var px = new byte[W * H];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                // stains (30 px), mottling (8 px), fibre (short strokes along x) and speckle
                float t = 0.52f
                    + (Noise(stain, 24, 18, x / 30f, y / 30f) - 0.5f) * 0.42f
                    + (Noise(mottle, 82, 62, x / 8f, y / 8f) - 0.5f) * 0.30f
                    + (Noise(fibre, 162, 482, x / 4f, y) - 0.5f) * 0.18f
                    + ((float)rng.NextDouble() - 0.5f) * 0.16f;
                // aged edges: browner in an irregular margin
                float d = Math.Min(Math.Min(x, W - 1 - x), Math.Min(y, H - 1 - y)) + (Noise(edge, 130, 98, x / 5f, y / 5f) - 0.5f) * 10;
                if (d < 9) t -= (9 - d) / 9f * 0.55f;
                // the paper's own faint ruling across the middle, at the rows' pitch (omitted when the full two-column table rules are drawn)
                if (!table && x > ControllerScreen.LeftRule + 3 && x < ControllerScreen.RightRule - 3 && y > ControllerScreen.RowTop + 30 && y < 400
                    && (y - ControllerScreen.RowTop) % ControllerScreen.RowH == ControllerScreen.RowH - 1) t -= 0.13f;
                px[y * W + x] = shade[Math.Clamp((int)MathF.Round(t * (Shades - 1)), 0, Shades - 1)];
            }
        if (rules) Rule(px, Near, firstButton, table, lines);
        return Pcx.Create(W, H, px, pal);
    }

    /// <summary>The frame's double rule, the columns' rules and rows, and the button bars, in the palette (the 1997 look; the modern look draws them as vectors).</summary>
    private static void Rule(byte[] px, Func<float, float, float, byte> near, int firstButton, bool table, IEnumerable<(int X, int Y, int W, int H)>? lines)
    {
        var rng = new Random(16);
        byte rule = near(177, 141, 74), ruleDark = near(160, 124, 62), ruleLight = near(196, 156, 82);
        void Fill(int x, int y, int w, int h, Func<byte> c)
        {
            for (int yy = y; yy < y + h; yy++)
                for (int xx = x; xx < x + w; xx++)
                    if (xx >= 0 && xx < 640 && yy >= 0 && yy < 480) px[yy * 640 + xx] = c();
        }
        // a printed rule, unevenly inked
        byte Ink() => rng.Next(10) switch { 0 => ruleLight, 1 or 2 => ruleDark, _ => rule };
        if (lines is not null) foreach (var (x, y, w, h) in lines) Fill(x, y, w, h, Ink);
        else foreach (var r in table ? ControllerScreen.TableRules() : ControllerScreen.FrameRules()) Fill(r.Position.X, r.Position.Y, r.Size.X, r.Size.Y, Ink);
        byte bar = near(239, 165, 82), barDark = near(231, 156, 74), edge = near(73, 65, 41);
        for (int i = firstButton; i < ControllerScreen.Slots.Length; i++)
        {
            var s = ControllerScreen.Slots[i];
            Fill(s.Position.X, s.Position.Y, s.Size.X, s.Size.Y, () => edge);
            Fill(s.Position.X + 2, s.Position.Y + 2, s.Size.X - 4, s.Size.Y - 4, () => rng.Next(5) == 0 ? barDark : bar);
        }
    }
}

/// <summary>The drawn controller, its leaders and marks, and (the modern look) the sheet's rules, title and buttons, over the frame and under its words.</summary>
public partial class PadDiagram : Control
{
    public required ControllerScreen Screen { get; init; }
    /// <summary>The 1997 look: drawn at 640x480 without smoothing, in the palette's colours only.</summary>
    public bool Faithful { get; init; }

    public override void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;
        if (Faithful) Size = new Vector2(ShellFrame.W, ShellFrame.H);
    }

    public override void _Process(double delta) => QueueRedraw();

    private float _s;
    private Vector2 _off;
    private Vector2 T(Vector2 shell) => _off + shell * _s;
    private Vector2 R(Vector2 rel) => T(ControllerScreen.P(rel));
    private bool AA => !Faithful;
    private Color _ink, _rule, _marker, _pencil, _fillBody, _shade;

    // ---- strokes ----------------------------------------------------------------------------------

    /// <summary>An ink line along the points, its weight swelling and thinning a little along the way as a pen's does.</summary>
    private void Pen(IReadOnlyList<Vector2> pts, float w, Color c, int seed = 0, bool closed = false)
    {
        int n = pts.Count;
        if (Faithful)
        {
            var a = pts.Select(p => p.Round()).ToList();
            if (closed) a.Add(a[0]);
            DrawPolyline([.. a], c, w >= 1.6f ? 2 : 1, false);
            return;
        }
        int m = closed ? n : n - 1;
        for (int i = 0; i < m; i++)
        {
            var p = pts[i]; var q = pts[(i + 1) % n];
            float k = 0.78f + 0.42f * (0.5f + 0.5f * MathF.Sin(i * 0.37f + seed * 1.7f) * MathF.Sin(i * 0.11f + seed));
            float width = w * k * _s;
            DrawLine(p, q, c, width, true);
            DrawCircle(q, width * 0.5f, c);
        }
    }

    private void Fine(Vector2 a, Vector2 b, Color c, float w = 0.9f) => DrawLine(Faithful ? a.Round() : a, Faithful ? b.Round() : b, c, Faithful ? 1 : w * _s, AA);

    /// <summary>Hatching inside the outline: parallel strokes at 45° (or the other way), every <paramref name="gap"/> shell pixels, between two heights.</summary>
    private void Hatch(Vector2[] poly, float gap, Color c, float w = 0.8f, bool other = false, float y0 = float.MinValue, float y1 = float.MaxValue)
    {
        var dir = other ? new Vector2(1, 1).Normalized() : new Vector2(1, -1).Normalized();
        var nrm = new Vector2(-dir.Y, dir.X);
        float lo = poly.Min(p => p.Dot(nrm)), hi = poly.Max(p => p.Dot(nrm));
        float step = gap * _s;
        float Y(Vector2 v) => (v.Y - _off.Y) / _s;
        for (float d = lo + step * 0.5f; d < hi; d += step)
        {
            var o = nrm * d;
            var ts = new List<float>();
            for (int i = 0; i < poly.Length; i++)
            {
                var a = poly[i]; var b = poly[(i + 1) % poly.Length];
                float da = a.Dot(nrm) - d, db = b.Dot(nrm) - d;
                if ((da < 0) == (db < 0)) continue;
                var x = a + (b - a) * (da / (da - db));
                ts.Add((x - o).Dot(dir));
            }
            ts.Sort();
            for (int i = 0; i + 1 < ts.Count; i += 2)
            {
                var p = o + dir * ts[i]; var q = o + dir * ts[i + 1];
                if (Y(p) > Y(q)) (p, q) = (q, p);
                if (Y(q) < y0 || Y(p) > y1) continue;
                if (Y(p) < y0) p += (q - p) * ((y0 - Y(p)) / (Y(q) - Y(p)));
                if (Y(q) > y1) q = p + (q - p) * ((y1 - Y(p)) / (Y(q) - Y(p)));
                Fine(p, q, c, w);
            }
        }
    }

    /// <summary>A pencil loop round a point: a little more than once round, wobbling, as a quick hand circles a thing.</summary>
    private void Loop(Vector2 c, float rx, float ry, Color col, int seed)
    {
        var pts = new List<Vector2>();
        float a0 = -2.2f + seed * 0.3f;
        for (int k = 0; k <= 40; k++)
        {
            float t = k / 40f, a = a0 + t * (Mathf.Tau + 0.5f);
            float wob = 1 + 0.05f * MathF.Sin(3 * a + seed) + 0.06f * t;
            pts.Add(c + new Vector2(MathF.Cos(a) * rx, MathF.Sin(a) * ry) * wob * _s);
        }
        Pen(pts, 1.5f, col, seed);
    }

    // ---- drawing ----------------------------------------------------------------------------------

    public override void _Draw()
    {
        var size = GetViewportRect().Size;
        _s = Mathf.Min(size.X / ShellFrame.W, size.Y / ShellFrame.H);
        _off = (size - new Vector2(ShellFrame.W, ShellFrame.H) * _s) / 2;
        _ink = Screen.InkColor;
        _rule = Screen.Tone(ControllerScreen.RuleColor);
        _marker = Faithful ? Screen.Tone(ControllerScreen.Marker) : new Color(ControllerScreen.Marker, 0.85f);
        _pencil = Screen.Tone(ControllerScreen.RedPencil);
        _fillBody = Screen.Tone(Color.Color8(246, 200, 112));
        _shade = Faithful ? _rule : new Color(_ink, 0.45f);
        if (!Faithful) Sheet();
        if (!Screen.ShowPad) return;
        var pad = Screen.ReadPad();
        DrawPad(pad);
        Leaders(pad);
        Selection();
    }

    /// <summary>The modern look's sheet over the paper: the rules, the title and the buttons, at the window's resolution.</summary>
    private void Sheet()
    {
        var rng = new Random(16);
        foreach (var r in ControllerScreen.FrameRules())
        {
            // a printed rule, unevenly inked: short runs of a little more or less
            bool across = r.Size.X >= r.Size.Y;
            int len = across ? r.Size.X : r.Size.Y;
            for (int k = 0; k < len; k += 6)
            {
                int run = Math.Min(6, len - k);
                var at = across ? new Rect2(r.Position.X + k, r.Position.Y, run, r.Size.Y) : new Rect2(r.Position.X, r.Position.Y + k, r.Size.X, run);
                DrawRect(new Rect2(T(at.Position), at.Size * _s), new Color(_rule, 0.8f + 0.2f * (float)rng.NextDouble()));
            }
        }
        var arial = HdAssets.Arial(true, TextServer.FontAntialiasing.Gray);
        if (arial is null) return;
        // the title: heavy caps at Control Configuration's measure (21 px capitals, 18 px a letter), thickened a little
        Caps(arial, ControllerScreen.Title, new Vector2(320, 33), 29f, 0.86f, ControllerScreen.TitleInk, 1.6f);
        for (int i = 0; i < ControllerScreen.Slots.Length; i++)
        {
            var b = ControllerScreen.Slots[i];
            var r = new Rect2(T(b.Position), new Vector2(b.Size.X, b.Size.Y) * _s);
            DrawRect(r, ControllerScreen.BarEdge);
            DrawRect(r.Grow(-2 * _s), ControllerScreen.BarColor);
            int size = Math.Max(1, (int)MathF.Round(14.6f * _s));
            var text = ControllerScreen.Fit(ControllerScreen.SlotText[i], t => arial.GetStringSize(t, HorizontalAlignment.Left, -1, size).X / _s * 0.94f, b.Size.X - 8);
            Caps(arial, text, new Vector2(b.Position.X + b.Size.X / 2f, b.Position.Y + 15), 14.6f, 0.94f, ControllerScreen.TitleInk, 0.7f);
        }
    }

    /// <summary>A string centred on x with its baseline at y, at a size in shell pixels, narrowed by a factor.</summary>
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

    private ControllerScreen.El El(string key) => Screen.Els.First(e => e.Key == key);

    private void DrawPad(PadRaw pad)
    {
        var body = ControllerScreen.Body.Select(R).ToArray();
        // the triggers and shoulders first: the body overlaps their feet
        foreach (var key in new[] { "LT", "RT", "LB", "RB" })
        {
            var e = El(key);
            var poly = e.Poly.Select(R).ToArray();
            DrawColoredPolygon(poly, _fillBody);
            float amount = Math.Clamp(e.Live(pad), 0, 1);
            if (key is "LT" or "RT")
            {
                Hatch(poly, 3.2f, _shade, 0.6f, key == "RT");       // shaded: they are behind
                if (amount > 0.05f)
                {
                    float top = e.Poly.Min(p => ControllerScreen.P(p).Y), bot = e.Poly.Max(p => ControllerScreen.P(p).Y);
                    Hatch(poly, 1.6f, _marker, 1.4f, key == "LT", bot - (bot - top) * amount, bot);
                }
            }
            else if (amount > 0.5f) Hatch(poly, 1.6f, _marker, 1.4f);
            Pen(poly, 1.3f, _ink, key.Length, true);
        }
        // in the 1997 look the body is the paper itself: the face buttons' letters are the frame's, under the drawing
        if (!Faithful) DrawColoredPolygon(body, new Color(_fillBody, 0.8f));
        // the grips in shadow: hatched below the face, crossed towards the bottom
        Hatch(body, 3.4f, _shade, 0.6f, false, ControllerScreen.C.Y + 40, ControllerScreen.C.Y + 80);
        Hatch(body, 3.4f, _shade, 0.6f, true, ControllerScreen.C.Y + 58, ControllerScreen.C.Y + 80);
        Pen(body, 1.9f, _ink, 3, true);
        // the face plate's seam between the grips
        Pen(ControllerScreen.Spline([new(-100, 28), new(-62, 43), new(0, 40), new(62, 43), new(100, 28)], 6, false).Select(R).ToArray(), 0.8f, _shade, 5);

        // sticks: the well and its shadow, the cap with its ridged rim, a marker stroke where it is pushed
        foreach (var (c, ax, ay, click) in new[] { (ControllerScreen.LeftStick, pad.LeftX, pad.LeftY, pad.Has(PadButton.LeftStick)), (ControllerScreen.RightStick, pad.RightX, pad.RightY, pad.Has(PadButton.RightStick)) })
        {
            var well = ControllerScreen.Circle(c, ControllerScreen.StickR + 2, 36).Select(R).ToArray();
            DrawColoredPolygon(well, Screen.Tone(Color.Color8(232, 176, 84)));
            Hatch(ControllerScreen.Circle(c + new Vector2(2, 2), ControllerScreen.StickR, 28).Select(R).ToArray(), 2.2f, _shade, 0.6f, true);
            Pen(well, 1.4f, _ink, 7, true);
            var at = c + new Vector2(ax, ay) * 5;
            var cap = ControllerScreen.Circle(at, ControllerScreen.CapR, 28).Select(R).ToArray();
            DrawColoredPolygon(cap, _fillBody);
            if (click) Hatch(cap, 1.6f, _marker, 1.4f);
            Pen(cap, 1.6f, _ink, 9, true);
            for (int k = 0; k < 16; k++)
            {
                var d = Vector2.FromAngle(k * Mathf.Tau / 16);
                Fine(R(at + d * (ControllerScreen.CapR - 2.6f)), R(at + d * (ControllerScreen.CapR - 1.2f)), _ink, 0.6f);
            }
            if (MathF.Abs(ax) > 0.25f || MathF.Abs(ay) > 0.25f)
                Pen([R(c), R(c + new Vector2(ax, ay) * (ControllerScreen.StickR + 5))], 2.4f, _marker, 1);
            DrawCircle(R(at), 1.6f * _s, _ink);
        }

        // the D-pad: one cross, its arms marked when pressed, each with its arrow
        {
            var dp = ControllerScreen.DPad;
            float a = ControllerScreen.DpA, r = ControllerScreen.DpR;
            var cross = new Vector2[] { new(-a, -r), new(a, -r), new(a, -a), new(r, -a), new(r, a), new(a, a), new(a, r), new(-a, r), new(-a, a), new(-r, a), new(-r, -a), new(-a, -a) }
                .Select(v => R(dp + v)).ToArray();
            DrawColoredPolygon(cross, _fillBody);
            Hatch(cross, 2.6f, _shade, 0.5f, true, ControllerScreen.P(dp).Y + 2, ControllerScreen.P(dp).Y + r);
            foreach (var key in new[] { "DUp", "DLeft", "DRight", "DDown" })
                if (El(key).Live(pad) > 0.5f) Hatch(El(key).Poly.Select(R).ToArray(), 1.6f, _marker, 1.4f);
            Pen(cross, 1.4f, _ink, 11, true);
            foreach (var d in new[] { Vector2.Up, Vector2.Down, Vector2.Left, Vector2.Right })
            {
                var tip = dp + d * (r - 3); var side = new Vector2(-d.Y, d.X) * 2.2f;
                DrawColoredPolygon([R(tip), R(tip - d * 3 + side), R(tip - d * 3 - side)], _ink);
            }
        }

        // the face buttons (their letters are the sheet's handwriting), Back, Start and the guide
        foreach (var (key, at) in ControllerScreen.Face)
        {
            var ring = ControllerScreen.Circle(at, ControllerScreen.FaceR, 24).Select(R).ToArray();
            if (!Faithful) DrawColoredPolygon(ring, _fillBody);      // in the 1997 look the letters are the frame's, under the drawing
            if (El(key).Live(pad) > 0.5f) Hatch(ring, 1.6f, _marker, 1.4f);
            Pen(ring, 1.2f, _ink, key[0], true);
            // its shadow side, heavier
            DrawArc(R(at), (ControllerScreen.FaceR + 0.6f) * _s, 0.2f, 1.9f, 10, _ink, Faithful ? 1 : 1.5f * _s, AA);
        }
        foreach (var key in new[] { "Back", "Start" })
        {
            var e = El(key);
            var ring = e.Poly.Select(R).ToArray();
            if (e.Live(pad) > 0.5f) Hatch(ring, 1.4f, _marker, 1.3f);
            Pen(ring, 1.1f, _ink, key.Length, true);
            var c = e.Centre; float s = key == "Back" ? -1 : 1;
            DrawColoredPolygon([R(c + new Vector2(2 * s, 0)), R(c + new Vector2(-1.5f * s, -1.8f)), R(c + new Vector2(-1.5f * s, 1.8f))], _ink);
        }
        Pen(ControllerScreen.Circle(ControllerScreen.GuideAt, 6.5f, 24).Select(R).ToArray(), 1.1f, _ink, 13, true);
        Pen(ControllerScreen.Circle(ControllerScreen.GuideAt, 3.2f, 14).Select(R).ToArray(), 0.7f, _shade, 14, true);
    }

    /// <summary>A leader from each row's rule to its input (a fine line, a dot on the input); a pressed input's row gets a marker stroke.</summary>
    private void Leaders(PadRaw pad)
    {
        foreach (var e in Screen.Els.Where(e => e.HasRow))
        {
            var a = T(e.Start); var b = T(e.Anchor);
            Fine(a, b, _ink, 0.9f);
            DrawCircle(Faithful ? b.Round() : b, (Faithful ? 1.2f : 1.7f) * _s, _ink);
            var twin = e.Key is "LSX" or "RSX" ? El(e.Key[..2] + "Y") : null;
            bool live = e.Live(pad) > (e.Axis ? 0.3f : 0.5f) || (twin is { HasRow: false } && twin.Live(pad) > 0.3f);
            if (!live) continue;
            // a highlighter run under the words (in the modern look under them, in the 1997 one below them)
            var row = e.Row;
            float y = ControllerScreen.RowY(e.Slot) + e.Lines.Length * ControllerScreen.RowH - (Faithful ? 3 : 6.5f);
            var from = T(new Vector2(row.Position.X + 3, y)); var to = T(new Vector2(row.End.X - 3, y));
            if (Faithful) DrawRect(new Rect2(from.Round(), new Vector2(to.X - from.X, 2)), _marker);
            else DrawLine(from, to, new Color(ControllerScreen.Marker, 0.45f), 8 * _s, true);
        }
    }

    /// <summary>The selection, in red pencil: a loop round the input on the drawing (arcs either side of a stick, along an axis) and a line under its row.</summary>
    private void Selection()
    {
        var sel = Screen.Current;
        var shown = sel.HasRow ? sel : sel.Key is "LSY" or "RSY" ? El(sel.Key[..2] + "X") : sel;
        var pts = sel.Poly.Select(ControllerScreen.P).ToArray();
        var lo = pts.Aggregate((a, b) => a.Min(b)); var hi = pts.Aggregate((a, b) => a.Max(b));
        var c = (lo + hi) / 2; var half = (hi - lo) / 2;
        if (sel.Key is "LSX" or "RSX" or "LSY" or "RSY")
        {
            float rr = ControllerScreen.StickR + 6; bool y = sel.Key.EndsWith('Y');
            foreach (float mid in y ? new[] { -Mathf.Pi / 2, Mathf.Pi / 2 } : new[] { 0f, Mathf.Pi })
                Pen(Enumerable.Range(0, 9).Select(k => T(c + Vector2.FromAngle(mid - 0.6f + k * 0.15f) * rr)).ToArray(), 1.7f, _pencil, 2);
        }
        else Loop(T(c), half.X + 5, half.Y + 4, _pencil, sel.Key.Length + sel.Key[0]);
        if (!shown.HasRow) return;
        var row = shown.Row;
        float yy = ControllerScreen.RowY(shown.Slot) + shown.Lines.Length * ControllerScreen.RowH - 2.5f;
        float x0 = row.Position.X + 2, x1 = row.End.X - 4;
        Pen(Enumerable.Range(0, 13).Select(k => { float t = k / 12f; return T(new Vector2(x0 + (x1 - x0) * t, yy + 0.6f * MathF.Sin(t * 7 + 1) - t * 0.8f)); }).ToArray(), 1.5f, _pencil, 4);
    }
}
