using Godot;
using I76.Net.Hud;
using I76.Sim.Missions;

namespace I76.Game.Net;

/// <summary>
/// The 1997 look's network readouts: the original's windows where the original put them, on the whole wide frame. The
/// console's lines (red, in Lee) at (50, 150) of 640x480 and the "Say:" entry at (50, 360) (two windows),
/// the score window over the console's place with its tab stops (16, 270, 350, 430; the port's ping and
/// status after them), all scaled by the window's height and measured from its left edge, as on a 640x480 screen,
/// rather than inside a centred 4:3 box. The host's prompts and the banners are centred; the port's block at the top right (the
/// ping, the listing, the goal line the original wrote at (50, 360), the keys) shows only with the score window up.
/// </summary>
public partial class NetHudClassic : Control
{
    public string? LeeFont { get; init; }
    private Label _info = null!, _feed = null!, _banner = null!, _prompt = null!, _entry = null!;
    private readonly List<Label> _cols = [];
    private Control _board = null!;
    private static readonly Color Red = new(1, 0, 0);

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsPreset(LayoutPreset.FullRect);
        _info = Make(16, HorizontalAlignment.Right);
        _info.AnchorLeft = _info.AnchorRight = 1;
        _info.OffsetLeft = -420; _info.OffsetRight = -16; _info.OffsetTop = 12;
        // a line wider than the box (the debug line, a long name) grows it leftwards, not off the screen's right edge
        _info.GrowHorizontal = GrowDirection.Begin;
        _board = new Control { MouseFilter = MouseFilterEnum.Ignore, Visible = false };
        AddChild(_board);
        // mark, name, the counts and the ping right-aligned at the original's tab stops, then whether the player is down
        foreach (var align in new[] { HorizontalAlignment.Left, HorizontalAlignment.Left, HorizontalAlignment.Right, HorizontalAlignment.Right, HorizontalAlignment.Right, HorizontalAlignment.Right, HorizontalAlignment.Left })
        {
            var l = Make(17, align);
            RemoveChild(l);
            _board.AddChild(l);
            _cols.Add(l);
        }
        _feed = Make(16, HorizontalAlignment.Left);
        _entry = Make(16, HorizontalAlignment.Left);
        var lee = new FontFile { Antialiasing = TextServer.FontAntialiasing.Gray, Hinting = TextServer.Hinting.None };
        bool hasLee = LeeFont is { } lf && System.IO.File.Exists(lf) && lee.LoadDynamicFont(lf) == Error.Ok;
        foreach (var l in _cols.Append(_feed).Append(_entry))
        {
            if (hasLee) l.AddThemeFontOverride("font", lee);
            l.AddThemeColorOverride("font_color", Red);
            l.AddThemeConstantOverride("outline_size", 3);
        }
        _prompt = Make(20, HorizontalAlignment.Center);
        _prompt.AnchorLeft = 0; _prompt.AnchorRight = 1; _prompt.AnchorTop = _prompt.AnchorBottom = 0.26f;
        _prompt.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _banner = Make(34, HorizontalAlignment.Center);
        _banner.AnchorLeft = 0; _banner.AnchorRight = 1; _banner.AnchorTop = _banner.AnchorBottom = 0.32f;
        _banner.AutowrapMode = TextServer.AutowrapMode.WordSmart;
    }

    private Label Make(int size, HorizontalAlignment align)
    {
        var l = new Label { HorizontalAlignment = align, MouseFilter = MouseFilterEnum.Ignore };
        l.AddThemeFontSizeOverride("font_size", size);
        l.AddThemeColorOverride("font_color", new Color(1, 0.92f, 0.75f));
        l.AddThemeColorOverride("font_outline_color", Colors.Black);
        l.AddThemeConstantOverride("outline_size", 5);
        AddChild(l);
        return l;
    }

    /// <summary>The console, the entry and the score window at the original's places for this window (HudLayout).</summary>
    private void Place(Vector2 v, int boardLines)
    {
        float k = HudLayout.ClassicScale(v.Y);
        int size = (int)Math.Round(HudLayout.ClassicFont * k);
        foreach (var l in _cols.Append(_feed).Append(_entry))
        {
            l.AddThemeFontSizeOverride("font_size", size);
            // the window's lines are 20 pixels apart at 480 lines, whatever the face's own spacing
            l.AddThemeConstantOverride("line_spacing", (int)Math.Round(HudLayout.ClassicLine * k - l.GetThemeFont("font").GetHeight(size)));
        }
        // the port's own lines (the ping, the prompts, the banners) grow with the window too
        float m = Math.Max(1, v.Y / 720f);
        _info.AddThemeFontSizeOverride("font_size", (int)(16 * m));
        _info.OffsetLeft = -420 * m; _info.OffsetRight = -16 * m; _info.OffsetTop = 12 * m;
        _prompt.AddThemeFontSizeOverride("font_size", (int)(20 * m));
        _banner.AddThemeFontSizeOverride("font_size", (int)(34 * m));
        var c = HudLayout.ClassicConsole(v.X, v.Y, 8);
        _feed.Position = new Vector2(c.X, c.Y); _feed.Size = new Vector2(c.W, c.H);
        var e = HudLayout.ClassicEntry(v.X, v.Y);
        _entry.Position = new Vector2(e.X, e.Y); _entry.Size = new Vector2(e.W, e.H);
        var b = HudLayout.ClassicBoard(v.X, v.Y, boardLines);
        _board.Position = new Vector2(b.X, b.Y);
        var tabs = HudLayout.ClassicTabs;
        for (int i = 0; i < _cols.Count; i++)
        {
            // each column from its stop to just short of the next (the counts right-aligned in it, as "%6d" pads them)
            float x0 = tabs[i], x1 = i + 1 < tabs.Length ? tabs[i + 1] - 12 : tabs[i] + 120;
            _cols[i].Position = new Vector2(x0 * k, 0);
            _cols[i].Size = new Vector2((x1 - x0) * k, b.H);
        }
    }

    public void Show(NetHudFrame f)
    {
        var v = GetViewportRect().Size;
        // the whole window (the anchored lines: the block at the right, the prompt and the banners centred)
        Position = Vector2.Zero;
        Size = v;
        var rows = Rows(f);
        bool board = rows is not null;
        // the port's lines at the top right (the ping, the listing, the time, the keys) only with the score window up: players found
        // the HUD too busy with them always there; a connection in trouble and the CB stay
        var sb = new System.Text.StringBuilder();
        if (board)
        {
            sb.Append($"ping {f.Ping} ms");
            if (f.Connection is { } cn) sb.Append($"  ({cn})");
            sb.Append('\n');
            if (f.Debug is { } dbg) sb.Append(dbg).Append('\n');
            if (f.HostStatus is { } hs) sb.Append(hs).Append('\n');
            // the goal line: the original's one-line window at (50, 360), which the score window here would run over
            if (f.GoalLine.Length > 0) sb.Append(f.GoalLine).Append('\n');
            if (f.ChatHint) sb.Append($"{f.ChatKey}: chat\n");
            if (f.NewCarHint) sb.Append($"{f.NewCarKey}: self-destruct for a new car\n");
            if (f.CbHint) sb.Append($"{f.CbKey}: talk on the CB\n");
        }
        else
        {
            if (f.Connection is { } cn) sb.Append(cn).Append('\n');
            if (f.Debug is { } dbg) sb.Append(dbg).Append('\n');
        }
        // the CB radio: the console's style, a line for each station on the air
        if (f.CbMuted) sb.Append("CB: you are muted by the host\n");
        if (f.CbTx) sb.Append("CB: you are transmitting\n");
        foreach (var (who, _) in f.CbTalking) sb.Append($"CB: {who}\n");
        _info.Text = sb.ToString();
        _board.Visible = board;
        Place(v, rows?.Count ?? 0);
        if (rows is not null)
            for (int c = 0; c < _cols.Count; c++) _cols[c].Text = string.Join('\n', rows.Select(r => r[c]));
        // while the window is up the console's lines give way to it, and the banner too; the standings take the
        // console's place as well
        _feed.Visible = !_board.Visible && f.Final is null;
        _banner.Visible = !_board.Visible;
        _banner.AnchorTop = _banner.AnchorBottom = f.Final is null ? 0.66f : 0.22f;
        _banner.AddThemeFontSizeOverride("font_size", (int)((f.Final is null ? 34 : 24) * Math.Max(1, v.Y / 720f)));
        // the console window (eight lines): what the original wrote there for ten seconds, and the chat for longer, wrapped to the
        // window's width, newest at the bottom; with the Say line open, the chat's history
        int more = f.Feed.More;
        var lines = f.ChatOpen ? f.Feed.Recent(f.T, double.MaxValue, 30, l => l.Kind == FeedKind.Chat)
            : f.Feed.Recent(f.T, HudFeed.ChatLife, 30, l => l.Kind == FeedKind.Chat || f.T - l.At <= HudFeed.Life);
        var texts = lines.Select(l => l.Text).ToList();
        if (more > 0 && !f.ChatOpen) texts.Add($"... and {more} more");
        _feed.Text = string.Join('\n', Tail(_feed, texts, _feed.Size.X - 16 * HudLayout.ClassicScale(v.Y), 8));
        _prompt.Text = f.Prompt switch
        {
            { Kind: NetHudFrame.PromptKind.Boot } p => $"Boot {p.Name} from the game? (CTRL-Y / CTRL-N) ",
            { } p => $"Let {p.Name} join the game? (CTRL-Y / CTRL-N) ",
            _ => "",
        };
        if (f.Final is { } fin)
            _banner.Text = "THE MELEE IS OVER\n" + string.Join('\n', fin.Standings.Select((x, i) => $"{i + 1}. {x.Name}  {x.Score}")) + "\n\n" + (fin.NextRound is { } nr
                ? $"Next round in {Math.Max(0, (int)Math.Ceiling(nr))} s{(f.Host ? "  (Enter: now)" : "")}   Ctrl+F10 leaves"
                : "Ctrl+F10 leaves");
        else if (f.Death is { } cd && f.Coop)
            _banner.Text = "DESTROYED\n" + (cd.Left > 0 ? $"back beside a teammate in {Math.Ceiling(cd.Left):0} s" : "waiting for a living teammate");
        else if (f.Death is { } d)
            _banner.Text = "DESTROYED\n" + (d.Left > 0 ? $"{d.RaiseKey} in {Math.Ceiling(d.Left):0} s" : $"{d.RaiseKey}: come back")
                + "\nEnter: reconfigure" + (d.NextCar is { } rc ? $"\nnext car: {rc}" : "");
        else _banner.Text = "";
        // the line being typed: its end stays in the window (a long line scrolls rather than running off the screen)
        string say = f.ChatOpen ? $"Say: {f.ChatText}{(Math.Floor(f.T * 2) % 2 == 0 ? "_" : " ")}" : "";
        var ef = _entry.GetThemeFont("font");
        int es = _entry.GetThemeFontSize("font_size");
        float room = Math.Max(40, _entry.Size.X - 16 * HudLayout.ClassicScale(v.Y));
        while (say.Length > 1 && ef.GetStringSize(say, HorizontalAlignment.Left, -1, es).X > room) say = say[1..];
        _entry.Text = say;
    }

    /// <summary>The last <paramref name="max"/> lines of the texts as the label draws them wrapped to <paramref name="width"/> (words, then letters), whole texts only where they fit.</summary>
    private static List<string> Tail(Label label, List<string> texts, float width, int max)
    {
        var font = label.GetThemeFont("font");
        int size = label.GetThemeFontSize("font_size");
        var rows = new List<string>();
        for (int i = texts.Count - 1; i >= 0 && rows.Count < max; i--)
        {
            var wrapped = new List<string>();
            var cur = "";
            foreach (var w in texts[i].Split(' '))
            {
                var word = w;
                var next = cur.Length == 0 ? word : cur + " " + word;
                if (font.GetStringSize(next, HorizontalAlignment.Left, -1, size).X <= width) { cur = next; continue; }
                if (cur.Length > 0) { wrapped.Add(cur); cur = ""; }
                while (word.Length > 1 && font.GetStringSize(word, HorizontalAlignment.Left, -1, size).X > width)
                {
                    int k = 1;
                    while (k < word.Length - 1 && font.GetStringSize(word[..(k + 1)], HorizontalAlignment.Left, -1, size).X <= width) k++;
                    wrapped.Add(word[..k]);
                    word = word[k..];
                }
                cur = word;
            }
            if (cur.Length > 0 || wrapped.Count == 0) wrapped.Add(cur);
            for (int j = wrapped.Count - 1; j >= 0 && rows.Count < max; j--) rows.Add(wrapped[j]);
        }
        rows.Reverse();
        return rows;
    }

    /// <summary>
    /// The score window: "# Player Name Score Kills Deaths" (a race's "Laps", CTF's "Captures"), the players in
    /// slot order (in team play grouped under each team's totals, the teams' window merged in); the host's own row H and the others numbered for its BOOT keys. Paged.
    /// </summary>
    private static List<string[]>? Rows(NetHudFrame f)
    {
        var (hs, hk, hd) = ScoreTable.Heads(f.Kind);
        string[] Cols(int score, int kills, int deaths) => (f.Kind is MeleeKind.Race or MeleeKind.Ctf) ? [$"{kills}", "", $"{deaths}"] : [$"{score}", $"{kills}", $"{deaths}"];
        if (f.Board is not { } b) return null;
        var rows = new List<string[]> { new[] { "#", "Player Name", hs, hk, hd, "ping", "" } };
        foreach (var p in b.Lines)
            // a team's totals head its players (the original's teams window, merged in)
            rows.Add(p.Kind == ScoreLineKind.Team ? ["", Fit($"{p.Name} ({p.Members})", 20), .. Cols(p.Score, p.Kills, p.Deaths), "", ""]
                : [p.Mark, Fit(p.Name, 20), .. Cols(p.Score, p.Kills, p.Deaths), $"{p.Ping}", p.Alive ? "" : "dead"]);
        if (b.Pages > 1) rows.Add(["", $"{b.First}-{b.Last} of {b.Total}  (Ctrl+PgUp/PgDn)", "", "", "", "", ""]);
        return rows;
    }

    private static string Fit(string s, int n) => s.Length <= n ? s : s[..n];
}
