using Godot;
using I76.Formats.Input;
using I76.Formats.Shell;
using I76.Net;

namespace I76.Game.Shell;

/// <summary>
/// The co-op game's lobby (protocol 20, docs/multiplayer.md "The co-op lobby"): where the players of a hosted story mission gather before it
/// starts. It is the net melee's own ENTRY ROSTER form (record 18, the join form): the list box holds the players in the order they came, each with
/// a READY mark (the host's row says HOST), the box line above it and the car panel show the highlighted player's car, and the player's own row
/// has the form's MAKE / MODEL, VARIANT and CONFIGURE CHASSIS to change theirs (a co-op list begins with "Mission's own car"). The left button
/// is READY (NOT READY once marked), or START for the host (a confirmation when someone is not ready); CANCEL leaves. The parchment under the
/// list holds the chat (Enter types a line, Enter sends it, Esc drops it). The host boots the highlighted player with Delete.
/// Hosted in its own shell over the loading screen, as <see cref="ReconfigureScreen"/> is over the game; both looks use the form's art.
/// </summary>
public sealed class CoopLobbyScreen : ShellScreen
{
    /// <summary>The lobby as the server last sent it (a new object each time it changes); null before the first.</summary>
    public required Func<LobbyState?> State { get; init; }
    /// <summary>The chat lines not yet taken, as "name: text".</summary>
    public required Func<LobbyChat?> TakeChat { get; init; }
    /// <summary>This player's ready mark, and the car when it changes (a car file or <see cref="Protocol.MissionCar"/>, with its .vcf for an own variant).</summary>
    public required Action<bool, string?, byte[]?> Send { get; init; }
    public required Action<bool> Start { get; init; }
    public required Action<string> Chat { get; init; }
    /// <summary>The host boots this player (by name).</summary>
    public required Action<string> Boot { get; init; }
    public required Action Quit { get; init; }
    /// <summary>The model and variant to show for a car the lists do not have (the launch's model).</summary>
    public int FallbackModel { get; init; } = -1;
    public override bool EscapeOpensOptions => false;

    // the join form's hotspots (MeleeScreen's table): the list's arrows and rows, MAKE / MODEL, VARIANT, CONFIGURE CHASSIS, JOIN GAME and CANCEL
    private static readonly Dictionary<int, (int X0, int X1, int Y0, int Y1)> Hot = new()
    {
        [1] = (9, 27, 128, 144), [2] = (9, 27, 250, 268), [0] = (31, 216, 150, 270),
        [3] = (610, 621, 400, 415), [4] = (437, 449, 419, 434), [5] = (456, 616, 422, 435), [6] = (229, 369, 453, 471), [15] = (489, 630, 453, 471),
    };
    private const int Row = 17;
    private ShellFont _font = null!;
    private SpriteBank _cars = null!;
    private MeleeData _data = null!;
    private bool Nitro => Shell.Install.IsNitro;
    private int Rows => Nitro ? 5 : 6;
    private int FirstRowY => Nitro ? 171 : 149;
    private int BoxY => Nitro ? 130 : 109;

    private LobbyState? _seen;
    private int _scroll;
    private byte _selected;
    private bool _selectedSet;
    /// <summary>The car asked for and not yet answered (shown as the player's own meanwhile).</summary>
    private string? _asked;
    private (int Model, int Variant) _own = (0, 1);
    private readonly List<string> _chat = [];
    private bool _typing;
    private string _line = "";
    private string _notice = "";
    private bool _buttonBaked;

    public override void Enter()
    {
        _font = Shell.Db.Font(0x45);
        _cars = Nitro ? Shell.Db.BankNative(54) : Shell.Db.Bank(0x3c);
        _data = Shell.Melee;
        Repaint();
    }

    public override void Repaint()
    {
        Frame.LoadBackground(Shell.Db, 18);
        _buttonBaked = false;
        Draw();
    }

    private LobbyMember? Me => State()?.Me;

    /// <summary>The car file a member has on show: this player's own is what they asked for until the server answers.</summary>
    private string CarOf(LobbyMember m) => m.Id == State()?.You && _asked is { } a ? a : m.Car;

    private LobbyMember? Shown
    {
        get
        {
            var s = State();
            if (s is null || s.Members.Count == 0) return null;
            return s.Members.FirstOrDefault(m => _selectedSet && m.Id == _selected) ?? s.Me ?? s.Members[0];
        }
    }

    /// <summary>The model and variant of a car on show, or the last pick's for a car this list does not have.</summary>
    private (int Model, int Variant)? ModelOf(string file) =>
        file.Length == 0 || file == Protocol.MissionCar ? null : _data.Find(file) is { } f ? f : _data.Models.Count == 0 ? null : (Math.Clamp(FallbackModel, 0, _data.Models.Count - 1), 1);

    private void Draw()
    {
        var s = State();
        Frame.RestoreAll();
        if (s is null) return;
        BakeButton();
        Frame.Text(_font, 12, 45, s.Me?.Name ?? "");
        string title = I76.Formats.Mission.MissionTitles.CoopTitle(Shell.Install, s.Mission);
        var sh = Shown;
        // the box line: the game, as the roster's top line shows the highlighted game
        Frame.Text(_font, 34, BoxY, AddressBookPopup.Fit(_font, $"{title} ({s.Members.Count}/{s.Max})", 176));
        int nx = Nitro ? 34 : 14;
        _scroll = Math.Clamp(_scroll, 0, Math.Max(0, s.Members.Count - Rows));
        for (int k = 0; k < Rows && _scroll + k < s.Members.Count; k++)
        {
            var m = s.Members[_scroll + k];
            int y = FirstRowY + Row * k;
            string tag = m.Id == s.Host ? "HOST" : m.Ready ? "READY" : "";
            int tagW = tag.Length > 0 ? _font.Measure(tag) : 0;
            string name = (m.Id == sh?.Id ? "> " : "") + m.Name;
            Frame.Text(_font, nx, y, AddressBookPopup.Fit(_font, name, 203 - nx - tagW - 4));
            if (tag.Length > 0) Frame.Text(_font, 203 - tagW, y, tag);
        }
        // under the list: what the game waits for, and the chat
        string wait = s.Host == LobbyState.NoHost
            ? (s.AutoMin > 1 ? $"Starts at {s.AutoMin}+ ready." : "Starts when all ready.")
            : s.IsHost ? (s.Members.Count > 1 && s.Members.All(m => m.Id == s.Host || m.Ready) ? "All ready: START." : "START when ready (Del boots).")
            : "The host starts the mission.";
        Frame.Text(_font, 14, 280, AddressBookPopup.Fit(_font, _notice.Length > 0 ? _notice : wait, 200));
        int rows = 8, top = 302;
        var shown = _chat.Skip(Math.Max(0, _chat.Count - rows)).ToList();
        for (int k = 0; k < shown.Count; k++) Frame.Text(_font, 14, top + 16 * k, AddressBookPopup.Fit(_font, shown[k], 200));
        Frame.Text(_font, 14, 440, AddressBookPopup.Fit(_font, _typing ? "> " + _line + "_" : "Enter: chat", 200));
        // the car panel: the highlighted player's
        if (sh is { } who)
        {
            string file = CarOf(who);
            bool story = file == Protocol.MissionCar;
            if (ModelOf(story ? s.OwnCar : file) is var (model, variant) && model >= 0)
                MeleeScreen.DrawCar(Frame, _font, _cars, _data, model, variant, Shell.Parts, story ? "Mission's own car" : null, story ? "the story's own" : null);
            else
            {
                Frame.Text(_font, 330, 399, "Mission's own car");
                Frame.Text(_font, 292, 421, "the story's own");
            }
            Frame.Text(_font, 288, 58, AddressBookPopup.Fit(_font, who.Name, 130));
        }
        DrawLeftButton(s);
    }

    /// <summary>
    /// The form's JOIN GAME button says READY, NOT READY or START instead: its edges and a plain column of it stretched over the label,
    /// the new word in the shell's label font (as the host form's game type button is made).
    /// </summary>
    private void BakeButton()
    {
        if (_buttonBaked) return;
        _buttonBaked = true;
        const int x0 = 229, x1 = 370, y0 = 455, h = 14, edge = 4, plain = 238;
        for (int x = x0 + edge; x < x1 - edge; x++) Frame.CopyBackground(plain, y0, 1, h, x, y0);
    }

    private void DrawLeftButton(LobbyState s)
    {
        string t = s.IsHost ? "START" : s.Me?.Ready == true ? "NOT READY" : "READY";
        var f = Shell.Db.Font(0x47);
        int w = 370 - 229, tx = 229 + (w - f.Measure(t)) / 2, ty = 455 + (14 - f.Height) / 2 + 1;
        Frame.Text(f, tx, ty, t, ShellFrame.DefaultInk);
        Frame.Text(f, tx + 1, ty, t, ShellFrame.DefaultInk);
    }

    public override IReadOnlyList<PadRegion> PadRegions
    {
        get
        {
            var l = new List<PadRegion>();
            var s = State();
            int n = s is null ? 0 : Math.Min(Rows, s.Members.Count - _scroll);
            for (int k = 0; k < n; k++)
            {
                int kk = k;
                l.Add(PadRegion.Of(Hot[0].X0, FirstRowY + Row * k, Hot[0].X1 - Hot[0].X0, Row, d => d == NavDir.Up && kk == 0 ? Scroll(-1) : d == NavDir.Down && kk == n - 1 ? Scroll(1) : false));
            }
            if (s?.Me is not null && Shown?.Id == s.You) foreach (int id in new[] { 3, 4, 5 }) l.Add(PadRegion.Of(Hot[id].X0, Hot[id].Y0, Hot[id].X1 - Hot[id].X0, Hot[id].Y1 - Hot[id].Y0));
            foreach (int id in new[] { 6, 15 }) l.Add(PadRegion.Of(Hot[id].X0, Hot[id].Y0, Hot[id].X1 - Hot[id].X0, Hot[id].Y1 - Hot[id].Y0));
            return l;
        }
    }

    public override bool TextEditing => _typing;
    public override void StopEditing() { _typing = false; Draw(); }

    private bool Scroll(int by)
    {
        int max = Math.Max(0, (State()?.Members.Count ?? 0) - Rows), to = Math.Clamp(_scroll + by, 0, max);
        if (to == _scroll) return false;
        _scroll = to;
        Draw();
        return true;
    }

    private int HitId(Vector2I p) => Hot.Where(kv => kv.Value.X0 < p.X && p.X < kv.Value.X1 && kv.Value.Y0 < p.Y && p.Y < kv.Value.Y1).Select(kv => kv.Key).DefaultIfEmpty(-1).First();

    public override void Tick()
    {
        var s = State();
        bool redraw = false;
        if (!ReferenceEquals(s, _seen))
        {
            // the server's word is the car: what was asked for is given up (taken, it is the server's now; refused, Answer has said so)
            if (s is not null && _seen is not null && s.Me?.Car != _seen.Me?.Car) _asked = null;
            _seen = s;
            if (s is not null && _own == (0, 1) && s.Me is { } me && ModelOf(me.Car) is { } mo) _own = mo;
            redraw = true;
        }
        while (TakeChat() is { } c) { AddChat($"{c.Name}: {c.Text}"); redraw = true; }
        if (s is null) { if (redraw) Draw(); return; }
        if (_typing) { redraw |= Typing(s); if (redraw) Draw(); return; }
        if (Shell.Keys.Contains(Key.Escape)) { Quit(); return; }
        if (Shell.Keys.Contains(Key.Enter) || Shell.Keys.Contains(Key.KpEnter)) { _typing = true; _line = ""; Draw(); return; }
        if (Shell.Keys.Contains(Key.Down)) { Select(s, 1); redraw = true; }
        if (Shell.Keys.Contains(Key.Up)) { Select(s, -1); redraw = true; }
        if (Shell.Keys.Contains(Key.Delete) && s.IsHost && Shown is { } victim && victim.Id != s.You) { Boot(victim.Name); _notice = $"{victim.Name} was booted."; redraw = true; }
        if (Shell.Wheel != 0) { _scroll = Math.Clamp(_scroll + Shell.WheelRows / 3, 0, Math.Max(0, s.Members.Count - Rows)); redraw = true; }
        if (Shell.Clicked)
        {
            int id = HitId(Shell.Mouse);
            if (id >= 0) { Shell.Sound(82); redraw |= Act(s, id); }
        }
        if (redraw) Draw();
    }

    private void AddChat(string line)
    {
        foreach (var l in NoticePopup.Wrap(_font, line, 200, 3)) _chat.Add(l);
        if (_chat.Count > 60) _chat.RemoveRange(0, _chat.Count - 60);
    }

    private bool Typing(LobbyState s)
    {
        bool changed = false;
        foreach (char c in Shell.Typed) if (_line.Length < Protocol.MaxChat) { _line += c; changed = true; }
        if (Shell.Keys.Contains(Key.Backspace) && _line.Length > 0) { _line = _line[..^1]; changed = true; }
        if (Shell.Keys.Contains(Key.Escape)) { _typing = false; _line = ""; return true; }
        if (Shell.Keys.Contains(Key.Enter) || Shell.Keys.Contains(Key.KpEnter))
        {
            _typing = false;
            if (_line.Trim().Length > 0) Chat(_line.Trim());
            _line = "";
            return true;
        }
        return changed;
    }

    private void Select(LobbyState s, int by)
    {
        int i = Math.Max(0, s.Members.Select((m, k) => (m, k)).FirstOrDefault(x => x.m.Id == Shown?.Id).k);
        i = Math.Clamp(i + by, 0, s.Members.Count - 1);
        _selected = s.Members[i].Id;
        _selectedSet = true;
        if (i < _scroll) _scroll = i;
        if (i >= _scroll + Rows) _scroll = i - Rows + 1;
    }

    private bool Act(LobbyState s, int id)
    {
        _notice = "";
        switch (id)
        {
            case 1: Scroll(-1); return false;
            case 2: Scroll(1); return false;
            case 0:
            {
                int k = (Shell.Mouse.Y - FirstRowY) / Row;
                if (Shell.Mouse.Y >= FirstRowY && k < Rows && _scroll + k < s.Members.Count) { _selected = s.Members[_scroll + k].Id; _selectedSet = true; }
                return true;
            }
            case 6:
                if (s.IsHost) { StartPressed(s); return false; }
                Send(s.Me?.Ready != true, null, null);
                return false;
            case 15: Quit(); return false;
            case 3 or 4 or 5 when Shown?.Id != s.You: _notice = "Highlight yourself to pick a car."; return true;
            case 3: ModelPopup(); return false;
            case 4:
                if (ModelOf(CarOf(s.Me!)) is not var (m4, v4)) { ModelPopup(); return false; }
                VariantPopups.Show(Shell, _font, _data, m4, v4, Nitro, v => PickCar(_data.Vcf(m4, v), m4, v),
                    (n, left) => { }, Repaint);
                return false;
            case 5:
            {
                if (ModelOf(CarOf(s.Me!)) is not var (m5, v5)) { ModelPopup(); return false; }
                Shell.Overlay(null);
                Shell.Go(ChassisForm.Create(Shell, new MeleeChassis(m5, v5, n => { PickCar(_data.Vcf(m5, n), m5, n); Back(); }, Back) { DoneAs = (nm, n) => { PickCar(_data.Vcf(nm, n), nm, n); Back(); } }));
                return false;
            }
        }
        return false;
    }

    private void Back()
    {
        Shell.Go(new HostScreen());
        Shell.Overlay(this);
    }

    private void StartPressed(LobbyState s)
    {
        if (s.Members.All(m => m.Id == s.Host || m.Ready)) { Start(false); return; }
        var waiting = s.Members.Where(m => m.Id != s.Host && !m.Ready).Select(m => m.Name).ToList();
        Shell.Modal(new ConfirmPopup
        {
            Lines = ["Not everyone is ready:", AddressBookPopup.Fit(_font, string.Join(", ", waiting), 170), "Start anyway?"],
            Answer = yes => { Draw(); if (yes) Start(true); },
        });
    }

    private void ModelPopup()
    {
        Shell.Modal(new ListPopup
        {
            Sprite = 7, NativeSprite = Nitro ? 6 : null, X = 206, Y = 122, TextX = 22, TextY = 48, Visible = 9,
            Items = [.. new[] { "Mission's own car" }, .. _data.Models.Select(m => m.MakeModel)], Font = _font,
            Up = new(18, 36, 12, 25), Down = new(18, 36, 204, 217), List = new(16, 214, 47, 198), Cancel = new(146, 215, 209, 225),
            Picked = i =>
            {
                if (i == 0) PickCar(Protocol.MissionCar, -1, 1);
                else if (i > 0) PickCar(_data.Vcf(i - 1, 1), i - 1, 1);
                Repaint();
            },
        });
    }

    private void PickCar(string file, int model, int variant)
    {
        if (file != Protocol.MissionCar) _own = (model, variant);
        Shell.Settings.CoopOwnCar = file == Protocol.MissionCar;
        Shell.Settings.Save();
        byte[]? config = file == Protocol.MissionCar ? null
            : I76.Formats.Vfs.AssetVfs.Addon.TryGetValue(file, out var own) ? own
            : Shell.Install.Resolve("addon", file) is { } path ? System.IO.File.ReadAllBytes(path) : null;
        _asked = file;
        Send(false, file, config);
    }

    /// <summary>The server's answer to a new car: taken, it is the player's; refused, the old one stays and the reason shows.</summary>
    public void Answer(ReconfigureAnswer a)
    {
        if (a.Taken) return;
        _asked = null;
        _notice = $"Refused: {a.Text}";
        Draw();
        Shell.Modal(new NoticePopup { Sprite = 17, Ok = new Rect2I(119, 61, 42, 16), Text = $"The host refused that car: {a.Text}", Closed = Draw });
    }
}
