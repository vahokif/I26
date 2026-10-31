using Godot;
using I76.Game.Ui;
using I76.Net.Hud;
using I76.Sim.Missions;

namespace I76.Game.Net;

/// <summary>
/// The modern look's network HUD (docs/deviations.md): the same readouts and keys as the original's, drawn as the port's
/// panels in the game's colours and faces (HudStyle). The mode panel (time, goal, laps and place, the flags) and the kill
/// feed sit in the two columns under the cockpit's instruments, the chat at the bottom left, the host's prompts, the
/// death card, the end and the results in the middle, the score window over everything while its key is held. Sizes
/// follow the window's height (HudLayout.ModernScale) inside a safe margin.
/// </summary>
public partial class NetHudModern : Control
{
    private NetHudFrame? _f;
    private float _s = 1;
    private Vector2 _view;
    private readonly StyleBoxFlat _box = new() { AntiAliasing = true };

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsPreset(LayoutPreset.FullRect);
    }

    public void Show(NetHudFrame f)
    {
        _f = f;
        QueueRedraw();
    }

    // ---- drawing helpers ----

    private int Px(float v) => Math.Max(1, (int)Math.Round(v * _s));
    private float U(float v) => v * _s;

    private void Box(Rect2 r, Color bg, float radius = 8, Color? border = null, float borderWidth = 0)
    {
        _box.BgColor = bg;
        _box.SetCornerRadiusAll(Math.Max(0, (int)Math.Round(U(radius))));
        _box.BorderColor = border ?? Colors.Transparent;
        _box.SetBorderWidthAll(border is null ? 0 : Math.Max(1, (int)Math.Round(U(borderWidth))));
        DrawStyleBox(_box, r);
    }

    /// <summary>The panel: dark brown, a faint edge, and the 70s triple stripe (amber, orange, rust) slanting across its top left.</summary>
    private void Panel(Rect2 r, bool stripe = true, float alpha = 1)
    {
        Box(r, new Color(HudStyle.Panel, HudStyle.Panel.A * alpha), 10, new Color(HudStyle.Edge, HudStyle.Edge.A * alpha), 1);
        if (stripe) Stripes(new Vector2(r.Position.X + U(14), r.Position.Y), Math.Min(U(150), r.Size.X * 0.4f), U(4), alpha);
    }

    private void Stripes(Vector2 at, float w, float h, float alpha = 1)
    {
        Color[] c = [HudStyle.Amber, HudStyle.Orange, HudStyle.Rust];
        float slant = h * 1.5f;
        for (int i = 0; i < 3; i++)
        {
            float y = at.Y + i * h;
            float x = at.X + (2 - i) * slant * 0.6f;
            DrawColoredPolygon([new(x + slant, y), new(x + w + slant, y), new(x + w, y + h), new(x, y + h)], new Color(c[i], alpha));
        }
    }

    private Vector2 Measure(Font f, string s, int size) => f.GetStringSize(s, HorizontalAlignment.Left, -1, size);

    /// <summary>Text with its top at <paramref name="top"/>; returns its width.</summary>
    private float Text(Font f, string s, Vector2 top, int size, Color c, float outline = 0, HorizontalAlignment align = HorizontalAlignment.Left, float width = -1)
    {
        var at = new Vector2(top.X, top.Y + f.GetAscent(size));
        if (outline > 0) DrawStringOutline(f, at, s, align, width, size, Math.Max(1, (int)Math.Round(outline)), new Color(0, 0, 0, 0.75f * c.A));
        DrawString(f, at, s, align, width, size, c);
        return Measure(f, s, size).X;
    }

    private float TextRight(Font f, string s, Vector2 topRight, int size, Color c, float outline = 0)
    {
        float w = Measure(f, s, size).X;
        Text(f, s, new Vector2(topRight.X - w, topRight.Y), size, c, outline);
        return w;
    }

    private float TextCentre(Font f, string s, Vector2 topCentre, int size, Color c, float outline = 0)
    {
        float w = Measure(f, s, size).X;
        Text(f, s, new Vector2(topCentre.X - w / 2, topCentre.Y), size, c, outline);
        return w;
    }

    /// <summary>A title in the menus' slanted Impact, its top at <paramref name="top"/>, centred on it or left from it.</summary>
    private float Title(string s, Vector2 top, int size, Color c, bool centre = false, float shadow = 3)
    {
        var f = HudStyle.Title;
        float w = Measure(f, s, size).X;
        float x = centre ? top.X - w / 2 : top.X;
        float baseY = top.Y + f.GetAscent(size);
        const float k = 0.18f;
        DrawSetTransformMatrix(new Transform2D(new Vector2(1, 0), new Vector2(-k, 1), new Vector2(k * baseY, 0)));
        if (shadow > 0) DrawString(f, new Vector2(x + U(shadow), baseY + U(shadow)), s, HorizontalAlignment.Left, -1, size, new Color(HudStyle.Ink, 0.85f * c.A));
        DrawString(f, new Vector2(x, baseY), s, HorizontalAlignment.Left, -1, size, c);
        DrawSetTransformMatrix(Transform2D.Identity);
        return w;
    }

    /// <summary>A key cap: parchment with the key's name in dark Arial Bold. Returns its width.</summary>
    private float Key(string label, Vector2 at, float h, float alpha = 1)
    {
        int size = (int)(h * 0.52f);
        float w = Math.Max(h, Measure(HudStyle.Bold, label, size).X + h * 0.6f);
        Box(new Rect2(at, new Vector2(w, h)), new Color(HudStyle.Parchment, alpha), 5, new Color(HudStyle.ParchmentDim, alpha), 1);
        DrawRect(new Rect2(at.X + U(2), at.Y + h - U(3), w - U(4), U(2)), new Color(HudStyle.Brown, 0.5f * alpha));
        Text(HudStyle.Bold, label, new Vector2(at.X + (w - Measure(HudStyle.Bold, label, size).X) / 2, at.Y + (h - HudStyle.Bold.GetHeight(size)) / 2 - U(1)), size, new Color(HudStyle.Ink, alpha));
        return w;
    }

    /// <summary>A pad face button: a disc in the button's colour with its letter.</summary>
    private float PadButton(char b, Vector2 at, float h, float alpha = 1)
    {
        var c = b switch { 'A' => new Color(0.42f, 0.74f, 0.26f), 'B' => new Color(0.86f, 0.28f, 0.2f), 'X' => new Color(0.25f, 0.5f, 0.86f), _ => HudStyle.Amber };
        var centre = at + new Vector2(h / 2, h / 2);
        DrawCircle(centre, h / 2, new Color(HudStyle.Ink, alpha), true, -1, true);
        DrawCircle(centre, h / 2 - U(2), new Color(c, alpha), true, -1, true);
        int size = (int)(h * 0.55f);
        TextCentre(HudStyle.Bold, b.ToString(), new Vector2(centre.X, centre.Y - HudStyle.Bold.GetHeight(size) / 2), size, new Color(HudStyle.Ink, alpha));
        return h;
    }

    /// <summary>A hint: key caps (and a pad button when a pad is in) then the action's words. Returns its width.</summary>
    private float Hint(Vector2 at, float h, string[] keys, char? pad, string action, Color c, float alpha = 1)
    {
        float x = at.X;
        foreach (var k in keys) x += Key(k, new Vector2(x, at.Y), h, alpha) + U(4);
        if (pad is { } p && _f?.Pad == true)
        {
            Text(HudStyle.Regular, "/", new Vector2(x + U(2), at.Y + (h - HudStyle.Regular.GetHeight(Px(16))) / 2), Px(16), new Color(HudStyle.ParchmentDim, alpha));
            x += U(14);
            x += PadButton(p, new Vector2(x, at.Y), h, alpha) + U(4);
        }
        x += U(6);
        int size = (int)(h * 0.6f);
        x += Text(HudStyle.Bold, action, new Vector2(x, at.Y + (h - HudStyle.Bold.GetHeight(size)) / 2), size, new Color(c, alpha));
        return x - at.X;
    }

    /// <summary>The standings hint's pad glyph: none once the player has bound that button to a command.</summary>
    private static char? PadV => Controls.PadButtonBound(JoyButton.Back) ? null : 'V';

    private float HintWidth(float h, string[] keys, char? pad, string action)
    {
        float x = 0;
        int ks = (int)(h * 0.52f);
        foreach (var k in keys) x += Math.Max(h, Measure(HudStyle.Bold, k, ks).X + h * 0.6f) + U(4);
        if (pad is not null && _f?.Pad == true) x += U(14) + h + U(4);
        return x + U(6) + Measure(HudStyle.Bold, action, (int)(h * 0.6f)).X;
    }

    /// <summary>An explosion's burst: the feed's mark between killer and victim.</summary>
    private void Burst(Vector2 c, float r, Color col)
    {
        var pts = new Vector2[16];
        for (int i = 0; i < 16; i++)
        {
            float a = i * Mathf.Pi / 8 - Mathf.Pi / 2;
            float rr = i % 2 == 0 ? r : r * 0.45f;
            pts[i] = c + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * rr;
        }
        DrawColoredPolygon(pts, col);
    }

    private void Pennant(Vector2 at, float h, Color col, bool hollow = false)
    {
        DrawRect(new Rect2(at.X, at.Y, U(2.5f), h), HudStyle.ParchmentDim);
        var tri = new Vector2[] { new(at.X + U(2.5f), at.Y), new(at.X + U(2.5f) + h * 0.8f, at.Y + h * 0.28f), new(at.X + U(2.5f), at.Y + h * 0.56f) };
        if (hollow) DrawPolyline([.. tri, tri[0]], col, U(2), true);
        else DrawColoredPolygon(tri, col);
    }

    private static string Ordinal(int n) => n + ((n % 100) is 11 or 12 or 13 ? "th" : (n % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" });

    private static string Clock(double seconds)
    {
        int t = (int)Math.Max(0, Math.Ceiling(seconds));
        return t >= 3600 ? $"{t / 3600}:{t / 60 % 60:00}:{t % 60:00}" : $"{t / 60}:{t % 60:00}";
    }

    private Color TeamCol(int team, bool me = false) => _f is { } f && (f.TeamPlay || f.Kind == MeleeKind.Ctf) && team > 0 ? HudStyle.Team(f.Kind, team) : me ? HudStyle.Amber : HudStyle.Parchment;

    /// <summary>Wraps at spaces to <paramref name="max"/>; a word wider than a line (a held key, a URL) is broken where it runs out.</summary>
    private List<string> Wrap(Font f, string s, int size, float max)
    {
        var lines = new List<string>();
        var cur = "";
        foreach (var w in s.Split(' '))
        {
            var word = w;
            var next = cur.Length == 0 ? word : cur + " " + word;
            if (Measure(f, next, size).X <= max) { cur = next; continue; }
            if (cur.Length > 0) { lines.Add(cur); cur = ""; }
            while (word.Length > 0 && Measure(f, word, size).X > max)
            {
                int k = 1;
                while (k < word.Length && Measure(f, word[..(k + 1)], size).X <= max) k++;
                lines.Add(word[..k]);
                word = word[k..];
            }
            cur = word;
        }
        if (cur.Length > 0) lines.Add(cur);
        return lines;
    }

    /// <summary>The text cut to <paramref name="max"/> pixels, an ellipsis for what went.</summary>
    private string Fit(Font f, string s, int size, float max)
    {
        if (Measure(f, s, size).X <= max) return s;
        while (s.Length > 1 && Measure(f, s + "…", size).X > max) s = s[..^1];
        return s + "…";
    }

    // ---- the frame ----

    public override void _Draw()
    {
        if (_f is not { } f) return;
        var v = GetViewportRect().Size;
        _view = v;
        _s = HudLayout.ModernScale(v.X, v.Y);
        // the platform's safe area is in desktop coordinates: HudLayout.PlatformSafe takes the window's own position off (a window on a second monitor)
        var sa = DisplayServer.GetDisplaySafeArea();
        var wp = DisplayServer.WindowGetPosition();
        var safe = HudLayout.Safe(v.X, v.Y, HudLayout.PlatformSafe(new HudRect(sa.Position.X, sa.Position.Y, sa.Size.X, sa.Size.Y), wp.X, wp.Y, v.X, v.Y));
        var sr = new Rect2(safe.X, safe.Y, safe.W, safe.H);
        // the columns start at the safe top, or under the lowest 2D instrument that sits over them (the external ones and the 2D dash)
        var inst = ExtInstruments.Blocks(v.X, v.Y, mirror: false);   // the mirror is left out: the feed fades, and a kill feed halfway down the screen is worse than one over a corner of the mirror
        float gap = U(12), feedW = U(400);
        float stripW = Strip(f, Vector2.Zero, false);
        float stripTop = HudLayout.ColumnTop(inst, sr.Position.X, sr.Position.X + stripW, sr.Position.Y, gap);
        // the feed sits at the top left under the strip, growing down, and stops above the chat's tallest extent
        float feedTop = HudLayout.ColumnTop(inst, sr.Position.X, sr.Position.X + feedW, sr.Position.Y, gap);
        if (stripW > 0) feedTop = Math.Max(feedTop, stripTop + U(38) + U(8));
        float top = HudLayout.UnderAll(inst, sr.Position.Y, gap);
        bool board = f.Board is not null;
        if (f.Death is { } d && f.Final is null) DeathCard(f, d, v);
        if (!board && f.Final is null)
        {
            if (stripW > 0)
            {
                Panel(new Rect2(sr.Position.X, stripTop, stripW, U(38)), stripe: false);
                Strip(f, new Vector2(sr.Position.X, stripTop), true);
            }
            KillFeed(f, new Vector2(sr.Position.X, feedTop), v.Y * 0.80f - U(360), Math.Min(U(620), sr.Size.X));
            Chat(f, sr, v);
            CbPanel(f, sr, v);
        }
        if (f.Prompt is { } p) Prompt(p, new Vector2(v.X / 2, top));
        if (f.Final is { } fin) Results(f, fin, v);
        else if (f.End is { } e && f.Death is null) EndBanner(f, e, v);
        if (f.Board is { } b) Board(f, b, v, sr);
    }

    /// <summary>
    /// The strip: the connection when it is not whole and, until the standings have been opened once, the key that opens them.
    /// Everything else (the time left, one's place and count, a race's lap, CTF's flags, the mode, the goal, the ping, the host's
    /// status, the other keys) is in the standings: players found the HUD too busy with them always up.
    /// Returns its width (0 when empty); draws its contents only when <paramref name="draw"/> (the caller lays the panel under them).
    /// </summary>
    private float Strip(NetHudFrame f, Vector2 at, bool draw)
    {
        float sh = U(38), pad = U(14), sep = U(18), x = at.X + pad;
        int small = Px(13);
        float W(string s, int size, Font? font = null) => Measure(font ?? HudStyle.Bold, s, size).X;
        bool first = true;
        void Gap() { if (!first) x += sep; first = false; }
        void T(string s, int size, Color c, Font? font = null, float dy = 0)
        {
            font ??= HudStyle.Bold;
            if (draw) Text(font, s, new Vector2(x, at.Y + (sh - font.GetHeight(size)) / 2 + dy), size, c);
            x += W(s, size, font);
        }
        if (f.Connection is { } cn)
        {
            Gap();
            string s = cn.Length > 44 ? cn[..43] + "…" : cn;
            T(s, small, HudStyle.Danger);
        }
        if (f.ScoresHint)
        {
            Gap();
            float hh = U(22);
            string[] k = [f.ScoresKey];
            if (draw) Hint(new Vector2(x, at.Y + (sh - hh) / 2), hh, k, PadV, "STANDINGS", HudStyle.ParchmentDim);
            x += HintWidth(hh, k, PadV, "STANDINGS");
        }
        return first ? 0 : x + pad - at.X;     // 0: nothing to show
    }

    /// <summary>The kill feed at the right: killer, the burst, victim; one's own kills orange, one's own death rust; joins, leaves and the server's lines quieter. Fades.</summary>
    private void KillFeed(NetHudFrame f, Vector2 topLeft, float maxBottom, float maxW)
    {
        int room = Math.Max(0, (int)((maxBottom - topLeft.Y) / (U(34) + U(6))) - 1);   // never down to the chat; the "and N more" line takes the last row
        var lines = f.Feed.Recent(f.T, HudFeed.Life, Math.Min(6, room), l => l.Kind != FeedKind.Chat && l.Text.Length + (l.Who?.Length ?? 0) > 0);
        float y = topLeft.Y, h = U(34), gap = U(6);
        int size = Px(18);
        List<string>? notice;
        foreach (var l in lines)
        {
            float a = HudFeed.Alpha(f.T - l.At, HudFeed.Life);
            var parts = new List<(string Text, Color Col, Font Font)>();
            bool icon = false;
            Color bg = new(HudStyle.Panel, 0.78f * a), ink = HudStyle.Parchment;
            switch (l.Kind)
            {
                case FeedKind.Kill:
                    if (l.ByMe && !l.OnMe) { bg = new Color(HudStyle.Orange, 0.92f * a); ink = HudStyle.Ink; }
                    else if (l.OnMe) { bg = new Color(HudStyle.Rust, 0.92f * a); }
                    bool lit = l.ByMe && !l.OnMe;
                    if (l.Who is { } k && k != l.What) parts.Add((k, lit ? ink : TeamCol(l.WhoTeam, l.ByMe), HudStyle.Bold));
                    icon = true;
                    parts.Add((l.What ?? "?", lit ? ink : l.OnMe ? HudStyle.Parchment : TeamCol(l.WhatTeam), HudStyle.Bold));
                    break;
                case FeedKind.Join: parts.Add(("+", HudStyle.ParchmentDim, HudStyle.Bold)); parts.Add((l.Who ?? "", TeamCol(l.WhoTeam), HudStyle.Bold)); parts.Add(("joined", HudStyle.ParchmentDim, HudStyle.Regular)); break;
                case FeedKind.Leave: parts.Add(("−", HudStyle.ParchmentDim, HudStyle.Bold)); parts.Add((l.Who ?? "", TeamCol(l.WhoTeam), HudStyle.Bold)); parts.Add(("left", HudStyle.ParchmentDim, HudStyle.Regular)); break;
                default: parts.Add((l.Text.TrimStart('*', ' '), HudStyle.Amber, HudStyle.Bold)); break;
            }
            // a row never wider than the screen's safe frame: a long notice wraps (up to three lines), the other parts are cut to their share
            float inner = maxW - U(14) * 2 - (icon ? U(28) : 0) - (parts.Count - 1) * U(8);
            int extra = 0;
            if (l.Kind is not (FeedKind.Kill or FeedKind.Join or FeedKind.Leave) && parts.Count == 1 && Measure(parts[0].Font, parts[0].Text, size).X > inner)
            {
                notice = Wrap(parts[0].Font, parts[0].Text, size, inner);
                if (notice.Count > 3) { notice = notice.Take(3).ToList(); notice[2] = Fit(parts[0].Font, notice[2] + "…", size, inner); }
                extra = notice.Count - 1;
            }
            else
            {
                notice = null;
                for (int i = 0; i < parts.Count; i++)
                    parts[i] = (Fit(parts[i].Font, parts[i].Text, size, Math.Max(U(60), inner / parts.Count)), parts[i].Col, parts[i].Font);
            }
            float rowH = h + extra * U(26);
            float w = notice is not null ? maxW : U(14) * 2 + parts.Sum(p => Measure(p.Font, p.Text, size).X) + (parts.Count - 1) * U(8) + (icon ? U(28) : 0);
            var r = new Rect2(topLeft.X, y, w, rowH);
            Box(r, bg, 6);
            if (l.OnMe || (l.ByMe && l.Kind == FeedKind.Kill)) DrawRect(new Rect2(r.Position.X, r.Position.Y + U(6), U(3), h - U(12)), new Color(HudStyle.Amber, a));
            float x = r.Position.X + U(14);
            float ty = y + (h - HudStyle.Bold.GetHeight(size)) / 2;
            if (notice is not null)
            {
                foreach (var ln in notice) { Text(parts[0].Font, ln, new Vector2(x, ty), size, new Color(parts[0].Col, a)); ty += U(26); }
                y += rowH + gap;
                continue;
            }
            for (int i = 0; i < parts.Count; i++)
            {
                if (icon && i == parts.Count - 1)
                {
                    Burst(new Vector2(x + U(10), y + h / 2), U(10), new Color(l.ByMe && !l.OnMe ? HudStyle.Ink : HudStyle.Amber, a));
                    x += U(28);
                }
                var p = parts[i];
                x += Text(p.Font, p.Text, new Vector2(x, ty), size, new Color(p.Col, a)) + U(8);
            }
            y += rowH + gap;
        }
        if (f.Feed.More > 0)
        {
            string s = $"… and {f.Feed.More} more";
            float w = Measure(HudStyle.Regular, s, Px(16)).X + U(28);
            Box(new Rect2(topLeft.X, y, w, U(28)), new Color(HudStyle.Panel, 0.6f), 6);
            Text(HudStyle.Regular, s, new Vector2(topLeft.X + U(14), y + U(4)), Px(16), HudStyle.ParchmentDim);
        }
    }

    /// <summary>The chat at the bottom left: lines with the speaker in the team's colour, fading; the entry box while typing, the history over it.</summary>
    private void Chat(NetHudFrame f, Rect2 sr, Vector2 v)
    {
        float x = sr.Position.X, w = Math.Min(U(640), sr.Size.X), bottom = v.Y * 0.80f;
        float limit = bottom - U(350);     // the history never climbs into the kill feed's rows (KillFeed's maxBottom)
        int size = Px(19);
        float lineH = U(28);
        float y = bottom;
        if (f.ChatOpen)
        {
            float bh = U(46);
            y -= bh;
            var r = new Rect2(x, y, w, bh);
            Box(r, HudStyle.Panel, 8, HudStyle.Orange, 2);
            var tagR = new Rect2(x + U(8), y + U(8), U(56), bh - U(16));
            Box(tagR, HudStyle.Orange, 5);
            TextCentre(HudStyle.Title, "SAY", new Vector2(tagR.GetCenter().X, tagR.Position.Y + (tagR.Size.Y - HudStyle.Title.GetHeight(Px(18))) / 2), Px(18), HudStyle.Ink);
            string hint = "Enter send · Esc cancel";
            float hw = Measure(HudStyle.Regular, hint, Px(14)).X;
            float tx = tagR.End.X + U(12), room = w - (tx - x) - hw - U(24);
            string text = f.ChatText;
            while (text.Length > 0 && Measure(HudStyle.Regular, text, size).X > room) text = text[1..];
            float tw = Text(HudStyle.Regular, text, new Vector2(tx, y + (bh - HudStyle.Regular.GetHeight(size)) / 2), size, HudStyle.Parchment);
            if (Math.Floor(f.T * 2) % 2 == 0) DrawRect(new Rect2(tx + tw + U(2), y + U(11), U(2), bh - U(22)), HudStyle.Amber);
            Text(HudStyle.Regular, hint, new Vector2(x + w - hw - U(12), y + (bh - HudStyle.Regular.GetHeight(Px(14))) / 2), Px(14), HudStyle.ParchmentDim);
            y -= U(8);
        }
        // the recent lines, or while the entry is open the history (as many of the last twenty as fit)
        var lines = f.ChatOpen ? f.Feed.Recent(f.T, double.MaxValue, 20, l => l.Kind == FeedKind.Chat)
            : f.Feed.Recent(f.T, HudFeed.ChatLife, 8, l => l.Kind == FeedKind.Chat);
        // newest at the bottom, each line wrapped to the panel's width
        for (int i = lines.Count - 1; i >= 0; i--)
        {
            var l = lines[i];
            float a = f.ChatOpen ? 1 : HudFeed.Alpha(f.T - l.At, HudFeed.ChatLife, 2);
            string who = Fit(HudStyle.Bold, l.Who ?? "", size, U(200)) + ":";
            var nameCol = l.Who == "Server" ? HudStyle.Amber : TeamCol(l.WhoTeam, l.ByMe);
            float nw = Measure(HudStyle.Bold, who, size).X + U(8);
            var wrapped = Wrap(HudStyle.Regular, l.What ?? "", size, Math.Max(U(120), w - U(24) - nw));
            float h = Math.Max(1, wrapped.Count) * lineH + U(6);
            if (y - h < limit) break;
            y -= h;
            var r = new Rect2(x, y, Math.Min(w, U(24) + nw + wrapped.Select(s => Measure(HudStyle.Regular, s, size).X).DefaultIfEmpty(0).Max()), h);
            Box(r, new Color(HudStyle.Panel, (f.ChatOpen ? 0.8f : 0.62f) * a), 6);
            float ty = y + U(3) + (lineH - HudStyle.Bold.GetHeight(size)) / 2;
            Text(HudStyle.Bold, who, new Vector2(x + U(12), ty), size, new Color(nameCol, a));
            foreach (var s in wrapped) { Text(HudStyle.Regular, s, new Vector2(x + U(12) + nw, ty), size, new Color(HudStyle.Parchment, a)); ty += lineH; }
            y -= U(4);
        }
    }

    /// <summary>
    /// The CB radio under the chat at the bottom left: while the player talks, an orange CB tag and TRANSMITTING with a blinking lamp; for each
    /// other player on the air, a CB tag and the name in the team's colour with a lit lamp. Its key is listed in the standings.
    /// </summary>
    private void CbPanel(NetHudFrame f, Rect2 sr, Vector2 v)
    {
        var rows = new List<(string Text, Color Ink, bool Mine)>();
        if (f.CbMuted) rows.Add(("MUTED BY HOST", HudStyle.ParchmentDim, false));
        if (f.CbTx) rows.Add(("TRANSMITTING", HudStyle.Parchment, true));
        foreach (var (who, team) in f.CbTalking.Take(4)) rows.Add((who, TeamCol(team), false));
        if (rows.Count == 0) return;
        int size = Px(17);
        float x = sr.Position.X, y = v.Y * 0.80f + U(10), h = U(30);
        foreach (var (text, ink, mine) in rows)
        {
            float tw = Measure(mine ? HudStyle.Bold : HudStyle.Regular, text, size).X;
            float w = U(12) + U(40) + U(12) + U(18) + tw + U(14);
            var r = new Rect2(x, y, Math.Min(w, sr.Size.X), h);
            Box(r, HudStyle.Panel, 6, mine ? HudStyle.Orange : null, mine ? 2 : 0);
            var tag = new Rect2(x + U(6), y + U(5), U(40), h - U(10));
            Box(tag, mine ? HudStyle.Orange : HudStyle.Rust, 4);
            TextCentre(HudStyle.Title, "CB", new Vector2(tag.GetCenter().X, tag.Position.Y + (tag.Size.Y - HudStyle.Title.GetHeight(Px(15))) / 2), Px(15), HudStyle.Ink);
            float tx = tag.End.X + U(12);
            bool lit = !mine || Math.Floor(f.T * 3) % 2 == 0;
            DrawCircle(new Vector2(tx + U(5), y + h / 2), U(5), lit ? (mine ? HudStyle.Danger : HudStyle.Amber) : new Color(HudStyle.Brown, 0.8f));
            tx += U(18);
            Text(mine ? HudStyle.Bold : HudStyle.Regular, text, new Vector2(tx, y + (h - HudStyle.Bold.GetHeight(size)) / 2), size, ink);
            y += h + U(5);
        }
    }

    /// <summary>The death card: a brown dim over the view, DESTROYED, who did it, the respawn's countdown and the two ways back.</summary>
    private void DeathCard(NetHudFrame f, NetHudFrame.DeathInfo d, Vector2 v)
    {
        DrawRect(new Rect2(Vector2.Zero, v), new Color(0.12f, 0.03f, 0f, 0.30f));
        // a vignette: darker towards the edges
        for (int i = 0; i < 6; i++)
        {
            float m = U(40) * i;
            DrawRect(new Rect2(m, m, v.X - 2 * m, v.Y - 2 * m), new Color(0, 0, 0, 0.05f), false, U(40));
        }
        float hh = U(34);
        if (f.Coop) { CoopDeathCard(d, v); return; }
        string respawn = d.Left > 0 ? $"RESPAWN IN {Math.Ceiling(d.Left):0}" : "RESPAWN";
        float w1 = HintWidth(hh, [d.RaiseKey], 'A', respawn), w2 = HintWidth(hh, ["Enter"], 'Y', "RECONFIGURE");
        float w = Math.Max(U(620), w1 + w2 + U(40) + U(64)), cx = v.X / 2, y = v.Y * 0.50f;
        float h = U(214) + (d.NextCar is not null ? U(30) : 0);
        var r = new Rect2(cx - w / 2, y, w, h);
        Panel(r);
        y += U(18);
        Title("DESTROYED", new Vector2(cx, y), Px(64), HudStyle.Danger, centre: true, shadow: 4);
        y += U(80);
        if (d.Self) TextCentre(HudStyle.Bold, "by your own hand", new Vector2(cx, y), Px(24), HudStyle.ParchmentDim);
        else if (d.Killer is { } k)
        {
            float bw = Measure(HudStyle.Regular, "by ", Px(24)).X, kw = Measure(HudStyle.Bold, k, Px(24)).X;
            float x0 = cx - (bw + kw) / 2;
            Text(HudStyle.Regular, "by ", new Vector2(x0, y), Px(24), HudStyle.ParchmentDim);
            Text(HudStyle.Bold, k, new Vector2(x0 + bw, y), Px(24), TeamCol(d.KillerTeam));
        }
        else TextCentre(HudStyle.Bold, "wrecked", new Vector2(cx, y), Px(24), HudStyle.ParchmentDim);
        y += U(44);
        // the countdown
        float tw = Math.Min(w - U(80), U(540));
        var track = new Rect2(cx - tw / 2, y, tw, U(8));
        Box(track, new Color(HudStyle.Brown, 0.9f), 4);
        float done = d.Delay > 0 ? (float)Math.Clamp(1 - d.Left / d.Delay, 0, 1) : 1;
        Box(new Rect2(track.Position, new Vector2(Math.Max(U(8), tw * done), track.Size.Y)), d.Left > 0 ? HudStyle.Orange : HudStyle.Amber, 4);
        y += U(24);
        float x = cx - (w1 + w2 + U(40)) / 2;
        Hint(new Vector2(x, y), hh, [d.RaiseKey], 'A', respawn, d.Left > 0 ? HudStyle.ParchmentDim : HudStyle.Amber, d.Left > 0 ? 0.6f : 1);
        Hint(new Vector2(x + w1 + U(40), y), hh, ["Enter"], 'Y', "RECONFIGURE", HudStyle.Parchment);
        y += hh + U(14);
        if (d.NextCar is { } car) TextCentre(HudStyle.Regular, $"Next car: {car}", new Vector2(cx, y), Px(17), HudStyle.ParchmentDim);
    }

    /// <summary>A co-op mission's death card: DESTROYED, who did it, and the countdown to the car beside a teammate (no keys: it comes by itself).</summary>
    private void CoopDeathCard(NetHudFrame.DeathInfo d, Vector2 v)
    {
        float w = U(560), cx = v.X / 2, y = v.Y * 0.50f;
        Panel(new Rect2(cx - w / 2, y, w, U(190)));
        y += U(18);
        Title("DESTROYED", new Vector2(cx, y), Px(64), HudStyle.Danger, centre: true, shadow: 4);
        y += U(80);
        TextCentre(HudStyle.Bold, d.Killer is { } k && !d.Self ? $"by {k}" : "wrecked", new Vector2(cx, y), Px(24), HudStyle.ParchmentDim);
        y += U(44);
        float tw = w - U(80);
        var track = new Rect2(cx - tw / 2, y, tw, U(8));
        Box(track, new Color(HudStyle.Brown, 0.9f), 4);
        float done = d.Delay > 0 ? (float)Math.Clamp(1 - d.Left / d.Delay, 0, 1) : 1;
        Box(new Rect2(track.Position, new Vector2(Math.Max(U(8), tw * done), track.Size.Y)), d.Left > 0 ? HudStyle.Orange : HudStyle.Amber, 4);
        y += U(20);
        TextCentre(HudStyle.Regular, d.Left > 0 ? $"Back beside a teammate in {Math.Ceiling(d.Left):0} s" : "Waiting for a living teammate", new Vector2(cx, y), Px(18), HudStyle.Parchment);
    }

    /// <summary>The end (before the standings): the verdict large over the winner's camera, the original's line under it.</summary>
    private void EndBanner(NetHudFrame f, NetHudFrame.EndInfo e, Vector2 v)
    {
        string title = e.TimeUp ? "TIME LIMIT HAS ELAPSED" : e.Won ? "YOU WIN!" : "YOU LOSE, CHUMP!";
        var col = e.TimeUp ? HudStyle.Parchment : e.Won ? HudStyle.Amber : HudStyle.Danger;
        float y = v.Y * 0.30f, cx = v.X / 2;
        int size = Px(e.TimeUp ? 64 : 96);
        string sub = e.TimeUp ? "Everybody's a winner." : e.Line is { } l ? l.TrimStart('*', ' ') : "";
        sub = Fit(HudStyle.Bold, sub, Px(22), v.X - U(160));
        float tw = Math.Min(Math.Max(Measure(HudStyle.Title, title, size).X, Measure(HudStyle.Bold, sub, Px(22)).X), v.X - U(120));
        Box(new Rect2(cx - tw / 2 - U(48), y - U(10), tw + U(96), HudStyle.Title.GetHeight(size) + U(70)), new Color(HudStyle.Panel, 0.55f), 12);
        Title(title, new Vector2(cx, y), size, col, centre: true, shadow: 5);
        y += HudStyle.Title.GetHeight(size) + U(4);
        Stripes(new Vector2(cx - U(120), y), U(240), U(5));
        y += U(22);
        TextCentre(HudStyle.Bold, sub, new Vector2(cx, y), Px(22), HudStyle.Parchment, U(3));
    }

    /// <summary>The results: the verdict, the winner, the standings (one's own row lit and kept in view), one's place and counts, the next round's wait and the keys.</summary>
    private void Results(NetHudFrame f, NetHudFrame.FinalInfo fin, Vector2 v)
    {
        DrawRect(new Rect2(Vector2.Zero, v), new Color(0.06f, 0.03f, 0.01f, 0.45f));
        const int show = 10;
        var rows = fin.Standings.Take(show).ToList();
        var me = fin.Standings.FirstOrDefault(s => s.Me);
        bool pinMe = me is not null && !rows.Contains(me);
        float w = U(820), rowH = U(36), cx = v.X / 2;
        float h = U(190) + (rows.Count + (pinMe ? 1 : 0)) * rowH + U(36) + U(110);
        var r = new Rect2(cx - w / 2, Math.Max(U(20), (v.Y - h) / 2), w, h);
        Panel(r);
        float pad = U(28), x = r.Position.X + pad, y = r.Position.Y + U(22);
        Title("RESULTS", new Vector2(x, y), Px(44), HudStyle.Amber);
        if (f.End is { } e)
        {
            string verdict = e.TimeUp ? "TIME'S UP" : e.Won ? "YOU WIN!" : "YOU LOSE, CHUMP!";
            var vw = Measure(HudStyle.Title, verdict, Px(30)).X;
            Title(verdict, new Vector2(r.End.X - pad - vw - U(6), y + U(10)), Px(30), e.TimeUp ? HudStyle.Parchment : e.Won ? HudStyle.Amber : HudStyle.Danger);
        }
        y += U(66);
        if (fin.Standings.FirstOrDefault() is { } win)
        {
            Text(HudStyle.Bold, f.TeamPlay && f.End?.Winner is not null ? "WINNING TEAM" : "WINNER", new Vector2(x, y), Px(15), HudStyle.ParchmentDim);
            y += U(20);
            string name = f.TeamPlay && f.End?.Winner is { } team ? team : win.Name;
            var nc = f.TeamPlay && f.End is { } ee ? TeamCol(ee.WinnerTeam) : HudStyle.Parchment;
            float nw = Text(HudStyle.Bold, name, new Vector2(x, y), Px(34), nc);
            string unit = f.Kind switch { MeleeKind.Race => "laps", MeleeKind.Ctf => "captures", _ => f.Goal == MeleeGoal.Kills ? "kills" : "points" };
            Text(HudStyle.Regular, $"{win.Score} {unit}", new Vector2(x + nw + U(14), y + U(12)), Px(20), HudStyle.ParchmentDim);
            y += U(56);
        }
        // the table
        float colScore = r.End.X - pad, colDeaths = colScore - U(130), colKills = colDeaths - U(100);
        string scoreHead = f.Kind switch { MeleeKind.Race => "LAPS", MeleeKind.Ctf => "CAPTURES", _ => "SCORE" };
        Text(HudStyle.Bold, "#", new Vector2(x, y), Px(14), HudStyle.ParchmentDim);
        Text(HudStyle.Bold, "DRIVER", new Vector2(x + U(56), y), Px(14), HudStyle.ParchmentDim);
        TextRight(HudStyle.Bold, "KILLS", new Vector2(colKills, y), Px(14), HudStyle.ParchmentDim);
        TextRight(HudStyle.Bold, "DEATHS", new Vector2(colDeaths, y), Px(14), HudStyle.ParchmentDim);
        TextRight(HudStyle.Bold, scoreHead, new Vector2(colScore, y), Px(14), HudStyle.ParchmentDim);
        y += U(24);
        void Row(NetHudFrame.Standing s)
        {
            var rr = new Rect2(r.Position.X + U(12), y, w - U(24), rowH - U(4));
            if (s.Me) { Box(rr, HudStyle.MeRow, 6); DrawRect(new Rect2(rr.Position.X, rr.Position.Y + U(4), U(4), rr.Size.Y - U(8)), HudStyle.Amber); }
            int size = Px(20);
            float ty = y + (rowH - U(4) - HudStyle.Bold.GetHeight(size)) / 2;
            var rc = s.Rank == 1 ? HudStyle.Amber : HudStyle.Parchment;
            Text(HudStyle.Bold, Ordinal(s.Rank), new Vector2(x, ty), size, rc);
            if (s.Team > 0 && (f.TeamPlay || f.Kind == MeleeKind.Ctf)) DrawRect(new Rect2(x + U(46), y + U(6), U(4), rowH - U(16)), HudStyle.Team(f.Kind, s.Team));
            Text(HudStyle.Bold, s.Name, new Vector2(x + U(56), ty), size, s.Me ? HudStyle.Amber : HudStyle.Parchment);
            TextRight(HudStyle.Regular, $"{s.Kills}", new Vector2(colKills, ty), size, HudStyle.Parchment);
            TextRight(HudStyle.Regular, $"{s.Deaths}", new Vector2(colDeaths, ty), size, HudStyle.Parchment);
            TextRight(HudStyle.Bold, $"{s.Score}", new Vector2(colScore, ty), size, rc);
            y += rowH;
        }
        foreach (var s in rows) Row(s);
        if (pinMe)
        {
            TextCentre(HudStyle.Bold, "· · ·", new Vector2(cx, y - U(8)), Px(14), HudStyle.ParchmentDim);
            y += U(8);
            Row(me!);
        }
        y += U(12);
        if (me is not null)
        {
            string sum = $"You placed {Ordinal(me.Rank)} of {fin.Standings.Count}   ·   {me.Kills} kills   ·   {me.Deaths} deaths";
            Box(new Rect2(r.Position.X + U(12), y, w - U(24), U(40)), new Color(HudStyle.Brown, 0.55f), 6);
            TextCentre(HudStyle.Bold, sum, new Vector2(cx, y + U(9)), Px(18), HudStyle.Parchment);
            y += U(56);
        }
        float hh = U(30);
        string wait = fin.NextRound is { } nr ? $"Next round in {Math.Max(0, (int)Math.Ceiling(nr))} s" : "The melee is over";
        Text(HudStyle.Bold, wait, new Vector2(x, y + U(4)), Px(18), HudStyle.ParchmentDim);
        float hx = r.End.X - pad - HintWidth(hh, ["Ctrl", "F10"], null, "LEAVE");
        Hint(new Vector2(hx, y), hh, ["Ctrl", "F10"], null, "LEAVE", HudStyle.Parchment);
        if (f.Host && fin.NextRound is not null)
        {
            float w2 = HintWidth(hh, ["Enter"], null, "START NOW");
            Hint(new Vector2(hx - w2 - U(28), y), hh, ["Enter"], null, "START NOW", HudStyle.Amber);
        }
    }

    /// <summary>The host's prompt: a toast with the question and its keys.</summary>
    private void Prompt(NetHudFrame.PromptInfo p, Vector2 topCentre)
    {
        bool boot = p.Kind == NetHudFrame.PromptKind.Boot;
        string pre = boot ? "Boot " : "Let ", post = boot ? " from the game?" : " join the game?";
        int size = Px(24);
        float tw = Measure(HudStyle.Regular, pre, size).X + Measure(HudStyle.Bold, p.Name, size).X + Measure(HudStyle.Regular, post, size).X;
        float hh = U(32);
        (string[] Keys, string Action, Color Col)[] hints = boot
            ? [(["Ctrl", "Y"], "BOOT", HudStyle.Danger), (["Ctrl", "N"], "CANCEL", HudStyle.Parchment)]
            : [(["Ctrl", "Y"], "LET IN", HudStyle.Amber), (["Ctrl", "N"], "TURN AWAY", HudStyle.Parchment), (["Ctrl", "H"], "LATER", HudStyle.ParchmentDim)];
        float hw = hints.Sum(h => HintWidth(hh, h.Keys, null, h.Action)) + (hints.Length - 1) * U(26);
        float w = Math.Min(Math.Max(tw, hw) + U(120), _view.X - U(24));
        var r = new Rect2(topCentre.X - w / 2, topCentre.Y, w, U(124));
        Panel(r);
        var tag = new Rect2(r.Position.X + U(16), r.Position.Y + U(18), U(70), U(26));
        Box(tag, HudStyle.Orange, 5);
        TextCentre(HudStyle.Title, "HOST", new Vector2(tag.GetCenter().X, tag.Position.Y + (tag.Size.Y - HudStyle.Title.GetHeight(Px(18))) / 2), Px(18), HudStyle.Ink);
        float x = r.Position.X + U(100), y = r.Position.Y + U(16);
        x += Text(HudStyle.Regular, pre, new Vector2(x, y), size, HudStyle.Parchment);
        string nm = Fit(HudStyle.Bold, p.Name, size, Math.Max(U(80), r.End.X - U(16) - x - Measure(HudStyle.Regular, post, size).X));
        x += Text(HudStyle.Bold, nm, new Vector2(x, y), size, HudStyle.Amber);
        Text(HudStyle.Regular, post, new Vector2(x, y), size, HudStyle.Parchment);
        float hx = r.Position.X + U(100);
        foreach (var h in hints) hx += Hint(new Vector2(hx, y + U(52)), hh, h.Keys, null, h.Action, h.Col) + U(26);
    }

    /// <summary>The players' window: ranked (grouped by team in team play), one's own row lit, the host's row tagged, the host's numbered rows boxed; two columns past sixteen rows; paged.</summary>
    private void Board(NetHudFrame f, ScoreView b, Vector2 v, Rect2 sr)
    {
        var lines = b.Lines;
        int perCol = 16;
        var counts = HudLayout.Columns(lines.Count, perCol);
        float gap = U(28), rowH = U(34);
        float colW = Math.Min(U(720), (sr.Size.X - U(48) - (counts.Length - 1) * gap) / counts.Length);     // two columns on a narrow window shrink to fit
        float w = counts.Length * colW + (counts.Length - 1) * gap + U(48);
        int maxRows = counts.Max();
        bool footer = b.Pages > 1 || (f.Host && b.Numbered.Count > 0);
        // the game's keys a player may not know, here rather than over the game
        var gameKeys = new List<(string[] Keys, string Action)>();
        if (f.ChatHint) gameKeys.Add((f.ChatKey.Split('+'), "CHAT"));
        if (f.NewCarHint) gameKeys.Add((f.NewCarKey.Split('+'), "SELF-DESTRUCT"));
        if (f.CbHint) gameKeys.Add((f.CbKey.Split('+'), "TALK ON THE CB"));
        float status = StatusHeight(f);
        float h = U(96) + status + U(28) + maxRows * rowH + (footer ? U(64) : U(20)) + (gameKeys.Count > 0 ? U(44) : 0);
        var r = new Rect2((v.X - w) / 2, Math.Max(sr.Position.Y, (v.Y - h) / 2), w, h);
        DrawRect(new Rect2(Vector2.Zero, v), new Color(0.06f, 0.03f, 0.01f, 0.35f));
        Panel(r);
        float x0 = r.Position.X + U(24), y = r.Position.Y + U(22);
        Title("STANDINGS", new Vector2(x0, y), Px(40), HudStyle.Amber);
        string kind = f.Kind switch { MeleeKind.Race => "Race", MeleeKind.Ctf => "Capture the flag", _ => f.TeamPlay ? "Team combat" : "Combat" };
        float tw = Measure(HudStyle.Title, "STANDINGS", Px(40)).X;
        Text(HudStyle.Bold, kind, new Vector2(x0 + tw + U(24), y + U(16)), Px(18), HudStyle.ParchmentDim);
        TextRight(HudStyle.Bold, $"{b.Total + (f.Host ? 1 : 0)} drivers", new Vector2(r.End.X - U(24), y + U(16)), Px(18), HudStyle.ParchmentDim);
        y += U(70);
        Status(f, x0, y, r.Size.X - U(48));
        y += status;
        int idx = 0;
        for (int c = 0; c < counts.Length; c++)
        {
            float cx = x0 + c * (colW + gap), cy = y;
            BoardHead(f, cx, cy, colW);
            cy += U(28);
            // a column that starts inside a team carries that team's header again
            if (idx < lines.Count && lines[idx].Kind == ScoreLineKind.Player && f.TeamPlay && c > 0)
                for (int j = idx - 1; j >= 0; j--)
                    if (lines[j].Kind == ScoreLineKind.Team) { BoardRow(f, lines[j], cx, cy, colW, rowH); cy += rowH; break; }
            for (int i = 0; i < counts[c] && idx < lines.Count; i++, idx++)
            {
                if (lines[idx].Pinned) { DrawRect(new Rect2(cx, cy + U(1), colW, U(1)), new Color(HudStyle.ParchmentDim, 0.4f)); }
                BoardRow(f, lines[idx], cx, cy, colW, rowH);
                cy += rowH;
            }
        }
        if (gameKeys.Count > 0)
        {
            float ky = r.End.Y - (footer ? U(48) : U(20)) - U(40), kx = x0, kh = U(28);
            DrawRect(new Rect2(x0, ky - U(8), r.Size.X - U(48), U(1)), new Color(HudStyle.ParchmentDim, 0.25f));
            foreach (var (k, action) in gameKeys)
            {
                if (kx + HintWidth(kh, k, null, action) > r.End.X - U(24)) break;
                kx += Hint(new Vector2(kx, ky), kh, k, null, action, HudStyle.ParchmentDim) + U(28);
            }
        }
        // the footer: the page and its keys, the host's boot keys
        if (!footer) return;
        float fy = r.End.Y - U(48), hh = U(28);
        string pg = b.Pages > 1 ? $"Page {b.Page + 1} of {b.Pages}  ·  {b.First}–{b.Last} of {b.Total}" : $"{b.Total} {(f.Host ? "other " : "")}driver{(b.Total == 1 ? "" : "s")}";
        Text(HudStyle.Bold, pg, new Vector2(x0, fy + U(4)), Px(17), HudStyle.ParchmentDim);
        float hx = r.End.X - U(24);
        float pgEnd = x0 + Measure(HudStyle.Bold, pg, Px(17)).X + U(28);
        if (b.Pages > 1)
        {
            float hw = HintWidth(hh, ["Ctrl", "PgUp", "PgDn"], null, "PAGE");
            hx -= hw;
            Hint(new Vector2(hx, fy), hh, ["Ctrl", "PgUp", "PgDn"], null, "PAGE", HudStyle.Parchment);
            hx -= U(28);
        }
        if (f.Host && b.Numbered.Count > 0)
        {
            string last = $"{b.Numbered.Count}";
            string[] keys = ["Ctrl", b.Numbered.Count > 1 ? $"1–{last}" : "1"];
            string words = HintWidth(hh, keys, null, "BOOT A DRIVER") <= hx - pgEnd ? "BOOT A DRIVER" : "BOOT";
            float hw = HintWidth(hh, keys, null, words);
            Hint(new Vector2(hx - hw, fy), hh, keys, null, words, HudStyle.ParchmentDim);
        }
    }

    /// <summary>Whether the status band has a row for the flags, and the lines under it (the connection, the host's status, the debug line).</summary>
    private static int StatusLines(NetHudFrame f) => (f.Connection is not null ? 1 : 0) + (f.HostStatus is not null ? 1 : 0) + (f.Debug is not null ? 1 : 0);

    private static int FlagRows(NetHudFrame f) => (f.Flags.Count + 1) / 2;

    private float StatusHeight(NetHudFrame f) => U(58) + (f.Flags.Count > 0 && f.Kind == MeleeKind.Ctf ? U(8) + FlagRows(f) * U(30) : 0) + StatusLines(f) * U(22) + U(6);

    /// <summary>
    /// The standings' header band, what the always-on mode panel held: the time left, the goal with the leader's count against one's
    /// own (a race's lap and place), the ping and who hosts; CTF's flags under it; then the connection, the host's status and the
    /// debug line.
    /// </summary>
    private void Status(NetHudFrame f, float x, float y, float w)
    {
        float cx = x;
        if (f.TimeLimit > 0 && f.HasScores)
        {
            float left = Math.Max(0, f.TimeLimit - f.Clock);
            Text(HudStyle.Bold, "TIME LEFT", new Vector2(cx, y), Px(13), HudStyle.ParchmentDim);
            float tw = Text(HudStyle.Bold, Clock(left), new Vector2(cx, y + U(14)), Px(36), left < 60 ? HudStyle.Danger : HudStyle.Parchment);
            cx += Math.Max(tw, U(90)) + U(40);
        }
        if (f.Kind == MeleeKind.Race)
        {
            int total = f.GoalValue, lap = total > 0 ? Math.Min(f.Mine + 1, total) : f.Mine + 1;
            Text(HudStyle.Bold, "LAP", new Vector2(cx, y), Px(13), HudStyle.ParchmentDim);
            float lw = Text(HudStyle.Bold, $"{lap}", new Vector2(cx, y + U(14)), Px(36), HudStyle.Parchment);
            if (total > 0) lw += U(6) + Text(HudStyle.Bold, $"/ {total}", new Vector2(cx + lw + U(6), y + U(26)), Px(18), HudStyle.ParchmentDim);
            cx += Math.Max(lw, U(70)) + U(36);
            Text(HudStyle.Bold, "PLACE", new Vector2(cx, y), Px(13), HudStyle.ParchmentDim);
            float pw = Text(HudStyle.Bold, Ordinal(f.Place), new Vector2(cx, y + U(14)), Px(36), f.Place == 1 ? HudStyle.Amber : HudStyle.Parchment);
            pw += U(6) + Text(HudStyle.Bold, $"/ {f.Field}", new Vector2(cx + pw + U(6), y + U(26)), Px(18), HudStyle.ParchmentDim);
            cx += pw + U(40);
        }
        else if (f.GoalValue > 0 && f.HasScores)
        {
            string what = f.Goal switch { MeleeGoal.Captures => "CAPTURES", MeleeGoal.Kills => "KILLS", _ => "POINTS" };
            float bw = Math.Min(U(300), Math.Max(U(180), w - (cx - x) - U(260)));
            Text(HudStyle.Bold, $"FIRST TO {f.GoalValue} {what}", new Vector2(cx, y), Px(13), HudStyle.ParchmentDim);
            var track = new Rect2(cx, y + U(22), bw, U(10));
            Box(track, new Color(HudStyle.Brown, 0.9f), 5);
            float lead = Math.Clamp((float)f.Leader / f.GoalValue, 0, 1), mine = Math.Clamp((float)f.Mine / f.GoalValue, 0, 1);
            if (lead > 0) Box(new Rect2(track.Position, new Vector2(Math.Max(U(10), bw * lead), U(10))), HudStyle.Amber, 5);
            if (mine > 0 && f.Mine < f.Leader) Box(new Rect2(track.Position, new Vector2(Math.Max(U(10), bw * mine), U(10))), HudStyle.Orange, 5);
            Text(HudStyle.Bold, $"LEADER  {f.Leader}", new Vector2(cx, y + U(36)), Px(16), HudStyle.Amber);
            TextRight(HudStyle.Bold, $"{(f.TeamPlay ? "TEAM" : "YOU")}  {f.Mine}", new Vector2(cx + bw, y + U(36)), Px(16), f.Mine >= f.Leader ? HudStyle.Amber : HudStyle.Orange);
            cx += bw + U(40);
        }
        // the ping as a dot by its quality, and whether this machine hosts, at the right
        var pc = f.Ping < 90 ? new Color(0.55f, 0.85f, 0.35f) : f.Ping < 180 ? HudStyle.Amber : HudStyle.Danger;
        float rx = x + w;
        if (f.Host) rx -= U(8) + TextRight(HudStyle.Bold, "HOSTING", new Vector2(rx, y + U(8)), Px(16), HudStyle.Orange) + U(14);
        float pw2 = Measure(HudStyle.Bold, $"{f.Ping} ms", Px(16)).X;
        TextRight(HudStyle.Bold, $"{f.Ping} ms", new Vector2(rx, y + U(8)), Px(16), HudStyle.ParchmentDim);
        DrawCircle(new Vector2(rx - pw2 - U(12), y + U(18)), U(5), pc, true, -1, true);
        y += U(58);
        if (f.Kind == MeleeKind.Ctf && f.Flags.Count > 0)
        {
            float fw = w / 2;     // two flags a row
            for (int i = 0; i < f.Flags.Count; i++)
            {
                var (team, state, carrier, caps) = f.Flags[i];
                float fx = x + (i % 2) * fw, fy = y + (i / 2) * U(30);
                var c = HudStyle.Team(MeleeKind.Ctf, team);
                Pennant(new Vector2(fx, fy + U(2)), U(22), c, hollow: state == 4);
                string name = f.TeamName(team);
                float nw = Text(HudStyle.Bold, name, new Vector2(fx + U(26), fy), Px(17), c);
                string st = state switch { 1 => "AT BASE", 2 => "DROPPED", 4 => carrier is null ? "TAKEN" : $"TAKEN · {carrier}", _ => "" };
                var stc = state == 4 ? HudStyle.Danger : state == 2 ? HudStyle.Amber : HudStyle.ParchmentDim;
                int sts = Px(13);
                string fit = st;
                float room = fw - U(26) - nw - U(60);
                while (fit.Length > 4 && Measure(HudStyle.Bold, fit, sts).X > room) fit = fit[..^2] + "…";
                Text(HudStyle.Bold, fit, new Vector2(fx + U(26) + nw + U(8), fy + U(4)), sts, stc);
                TextRight(HudStyle.Bold, $"{caps}", new Vector2(fx + fw - U(16), fy - U(1)), Px(20), HudStyle.Parchment);
            }
            y += FlagRows(f) * U(30) + U(8);
        }
        foreach (var (line, col) in new[] { (f.Connection, HudStyle.Danger), (f.HostStatus, HudStyle.ParchmentDim), (f.Debug, HudStyle.ParchmentDim) })
        {
            if (line is null) continue;
            Text(HudStyle.Regular, Fit(HudStyle.Regular, line, Px(14), w), new Vector2(x, y), Px(14), col);
            y += U(22);
        }
        DrawRect(new Rect2(x, y + U(1), w, U(1)), new Color(HudStyle.ParchmentDim, 0.25f));
    }

    private void BoardHead(NetHudFrame f, float x, float y, float w)
    {
        var (hs, hk, hd) = ScoreTable.Heads(f.Kind);
        int size = Px(14);
        var c = HudStyle.ParchmentDim;
        Text(HudStyle.Bold, "#", new Vector2(x + U(8), y), size, c);
        Text(HudStyle.Bold, "DRIVER", new Vector2(x + U(64), y), size, c);
        TextRight(HudStyle.Bold, hs.ToUpperInvariant(), new Vector2(x + w - U(228), y), size, c);
        if (hk.Length > 0) TextRight(HudStyle.Bold, hk.ToUpperInvariant(), new Vector2(x + w - U(156), y), size, c);
        TextRight(HudStyle.Bold, hd.ToUpperInvariant(), new Vector2(x + w - U(80), y), size, c);
        TextRight(HudStyle.Bold, "PING", new Vector2(x + w - U(10), y), size, c);
        DrawRect(new Rect2(x, y + U(22), w, U(1)), new Color(HudStyle.ParchmentDim, 0.3f));
    }

    private void BoardRow(NetHudFrame f, ScoreLine l, float x, float y, float w, float rowH)
    {
        int size = Px(19);
        float ty = y + (rowH - HudStyle.Bold.GetHeight(size)) / 2;
        bool combat = f.Kind == MeleeKind.Combat;
        if (l.Kind == ScoreLineKind.Team)
        {
            var tc = HudStyle.Team(f.Kind, l.Team);
            Box(new Rect2(x, y + U(3), w, rowH - U(6)), new Color(tc, 0.18f), 5);
            DrawRect(new Rect2(x, y + U(3), U(5), rowH - U(6)), tc);
            Text(HudStyle.Bold, Ordinal(l.Rank), new Vector2(x + U(12), ty), size, tc);
            Title(l.Name.ToUpperInvariant(), new Vector2(x + U(64), y + (rowH - HudStyle.Title.GetHeight(Px(21))) / 2), Px(21), tc, shadow: 1.5f);
            float tnw = Measure(HudStyle.Title, l.Name.ToUpperInvariant(), Px(21)).X;
            Text(HudStyle.Regular, $"{l.Members}", new Vector2(x + U(64) + tnw + U(14), ty + U(2)), Px(15), HudStyle.ParchmentDim);
            TextRight(HudStyle.Bold, $"{(combat ? l.Score : l.Kills)}", new Vector2(x + w - U(228), ty), size, HudStyle.Parchment);
            if (combat) TextRight(HudStyle.Bold, $"{l.Kills}", new Vector2(x + w - U(156), ty), size, HudStyle.Parchment);
            TextRight(HudStyle.Bold, $"{l.Deaths}", new Vector2(x + w - U(80), ty), size, HudStyle.Parchment);
            return;
        }
        float a = l.Alive ? 1 : 0.55f;
        if (l.Me)
        {
            Box(new Rect2(x, y + U(2), w, rowH - U(4)), HudStyle.MeRow, 5);
            DrawRect(new Rect2(x, y + U(6), U(3), rowH - U(12)), HudStyle.Amber);
        }
        // the mark: the host's boot number in an orange box, else the rank
        if (f.Host && l.Mark is { Length: > 0 } m && m != "H")
        {
            var mb = new Rect2(x + U(8), y + U(6), U(26), rowH - U(12));
            Box(mb, HudStyle.Orange, 4);
            TextCentre(HudStyle.Bold, m, new Vector2(mb.GetCenter().X, mb.Position.Y + (mb.Size.Y - HudStyle.Bold.GetHeight(Px(16))) / 2), Px(16), HudStyle.Ink);
        }
        else Text(HudStyle.Bold, $"{l.Rank}", new Vector2(x + U(12), ty), size, new Color(l.Rank == 1 ? HudStyle.Amber : HudStyle.ParchmentDim, a));
        if (l.Team > 0 && (f.TeamPlay || f.Kind == MeleeKind.Ctf)) DrawRect(new Rect2(x + U(54), y + U(8), U(4), rowH - U(16)), new Color(HudStyle.Team(f.Kind, l.Team), a));
        float nx = x + U(64);
        if (!l.Alive) { Burst(new Vector2(nx + U(8), y + rowH / 2), U(7), new Color(HudStyle.Danger, 0.9f)); nx += U(22); }
        string name = l.Name;
        float room = w - U(300) - (nx - x) - (l.IsHost ? U(64) : 0);
        while (name.Length > 3 && Measure(HudStyle.Bold, name, size).X > room) name = name[..^2] + "…";
        float nw = Text(HudStyle.Bold, name, new Vector2(nx, ty), size, new Color(l.Me ? HudStyle.Amber : HudStyle.Parchment, a));
        if (l.IsHost)
        {
            var hb = new Rect2(nx + nw + U(10), y + U(9), U(50), rowH - U(18));
            Box(hb, new Color(HudStyle.Orange, 0.2f * a + 0.1f), 4, new Color(HudStyle.Orange, a), 1);
            TextCentre(HudStyle.Bold, "HOST", new Vector2(hb.GetCenter().X, hb.Position.Y + (hb.Size.Y - HudStyle.Bold.GetHeight(Px(12))) / 2), Px(12), new Color(HudStyle.Orange, a));
        }
        var nc = new Color(HudStyle.Parchment, a);
        TextRight(HudStyle.Bold, $"{(combat ? l.Score : l.Kills)}", new Vector2(x + w - U(228), ty), size, nc);
        if (combat) TextRight(HudStyle.Regular, $"{l.Kills}", new Vector2(x + w - U(156), ty), size, nc);
        TextRight(HudStyle.Regular, $"{l.Deaths}", new Vector2(x + w - U(80), ty), size, nc);
        var pc = l.Ping < 90 ? new Color(0.55f, 0.85f, 0.35f, a) : l.Ping < 180 ? new Color(HudStyle.Amber, a) : new Color(HudStyle.Danger, a);
        TextRight(HudStyle.Regular, $"{l.Ping}", new Vector2(x + w - U(10), ty + U(2)), Px(16), pc);
    }
}
