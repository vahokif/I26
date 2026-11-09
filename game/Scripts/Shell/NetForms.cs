using Godot;
using I76.Formats.Input;
using I76.Formats.Shell;
using I76.Net;
using I76.Sim.Trip;

namespace I76.Game.Shell;

/// <summary>
/// The network melee forms (melee modes 3, 4, 7, 8), on the port's
/// transports (docs/multiplayer.md "From the main menu"):
/// <list type="bullet">
/// <item>3 HOST → IPX, the LAN: a listen server on the default port, which
/// answers LAN searches.</item>
/// <item>4 HOST → INTERNET: the address book first (CONNECT TO SERVER,
/// internet.lst); the entry's host is the anet server the game is listed on,
/// its port (if not anet's 21157) the one to host on.</item>
/// <item>7 JOIN → IPX: the games a LAN search finds, asked again every
/// second.</item>
/// <item>8 JOIN → INTERNET: straight to the roster, which searches every
/// entry of the address book at once (<see cref="ServerBrowser"/>): the I26
/// games each anet server lists and a game at an entry's own address, asked
/// every second, under a header row per entry. SERVERS (the port's) opens the
/// address book to add, edit and delete entries.</item>
/// </list>
/// The host form (record 19; the base game's drawn by the port, HostForm.cs)
/// adds GAME NAME, NO. DRIVERS, TEAM PLAY, INVITE ONLY and MAX SCORE to the
/// area list; its BROADCAST GAME launches a listen
/// server and joins it. The roster (record 18) lists the games found as
/// "name (n/max)" with an arrow to the game's popup; JOIN GAME joins the
/// highlighted one. With team play either first asks CHOOSE A TEAM.
/// </summary>
public sealed partial class MeleeScreen
{
    /// <summary>The team names, CHOOSE A TEAM's rows.</summary>
    public static IReadOnlyList<string> Teams => I76.Sim.Missions.MeleeGame.TeamNames;

    // the host form (defaults: max score 20000, the flags clear; NO. DRIVERS 4 on the LAN and the internet)
    private int _drivers = 4, _maxScore = 20000, _port = Protocol.DefaultPort;
    /// <summary>
    /// NO. DRIVERS' top (up to <see cref="Protocol.MaxSeats"/>), where the original's was 8: 32, about 4.5 Mbit/s of
    /// upload at the default 20 Hz and 11 at 60, within a home connection with room to spare, and a tick of a millisecond
    /// or two beside the game's own frame (docs/multiplayer.md, "How many players"). The command line's --max-players
    /// goes to 64. The upload a listen server needs grows with the seats: <see cref="UploadMbit"/>.
    /// </summary>
    public const int MaxDrivers = 32;

    /// <summary>
    /// What a game with this many players needs its host to upload, in Mbit/s at 20 Hz (the load test's busiest link,
    /// October 2026; docs/multiplayer.md, "How many players"; a listen server's own player costs nothing). Between the
    /// table's rows it interpolates.
    /// </summary>
    public static double UploadMbit(int players)
    {
        (int N, double Mbit)[] t = [(1, 0.05), (8, 0.5), (16, 1.4), (32, 4.4), (48, 9.6), (64, 16.2)];
        if (players <= t[0].N) return t[0].Mbit;
        for (int k = 1; k < t.Length; k++)
            if (players <= t[k].N) return t[k - 1].Mbit + (t[k].Mbit - t[k - 1].Mbit) * (players - t[k - 1].N) / (t[k].N - t[k - 1].N);
        return t[^1].Mbit;
    }
    private bool _teamPlay, _inviteOnly, _scoreOn, _gameRenamed, _naming;
    /// <summary>
    /// The port's BOTS (docs/multiplayer.md "Bots"): players the server drives by the game's mode, up to the free seats (NO. DRIVERS
    /// less the host's own), and their skill. Kept in melee.cfg.
    /// </summary>
    private int _bots;
    private I76.Sim.Missions.BotSkill _botSkill = I76.Sim.Missions.BotSkill.Normal;
    /// <summary>The host form's A.I. CARS (<see cref="BotCarsPopup"/>): the car file of each bot seat, empty for Random. Kept in melee.cfg.</summary>
    private List<string> _botCars = [];
    private int MaxBots => Coop ? 0 : Math.Max(0, _drivers - 1);
    private string _gameName = "";
    /// <summary>
    /// The game's simulation rate in Hz, the host's choice for everyone in it (docs/multiplayer.md "Simulation rate"): kept in melee.cfg
    /// apart from Play Options' own, which a player's single-player games use (the first time it is that one).
    /// </summary>
    private int _rate = I76.Sim.Session.SimRate.Default.Label;
    /// <summary>The host form's Unlimited Turrets (the port's; UNLIM. TURRETS in the Nitro form's game options): cars in this game may carry several roof turrets.</summary>
    private bool _turrets;
    private static int StepRate(int hz, int by)
    {
        var o = I76.Sim.Session.SimRate.Offered;
        int i = Array.FindIndex(o, r => r.Label == hz);
        return o[((i < 0 ? 0 : i) + by + o.Length) % o.Length].Label;
    }

    // ---- co-op (docs/multiplayer.md "Co-op over the network"): a story mission for one to four players, hosted from the same form
    /// <summary>
    /// The host form is a co-op form: its list is the install's story missions by their titles (<see cref="I76.Formats.Mission.MissionTitles.Coop"/>), its seats 1-8, no
    /// team play, limit or Bots. The Nitro Pack's GAME TYPE has Co-op beside Combat, Race and CTF; the base game's form switches on its
    /// Game Type row (<see cref="_coopBase"/>).
    /// </summary>
    private bool Coop => Hosting && (Nitro ? Types[_kind].Coop : _coopBase);
    private bool _coopBase;
    private int _coopArea;
    private List<(string Name, string Msn)>? _coopList;
    private List<(string Name, string Msn)> CoopList => _coopList ??= [.. I76.Formats.Mission.MissionTitles.Coop(Shell.Install).Select(m => (m.Title, m.Stem))];
    /// <summary>The area's index in the list on show (the co-op form keeps its own mission apart from the melee's arena).</summary>
    private int Area
    {
        get => Coop && !Nitro ? _coopArea : _setup.Area;
        set { if (Coop && !Nitro) _coopArea = value; else _setup.Area = value; }
    }
    private int MaxSeats => Coop ? Protocol.MaxCoopSeats : MaxDrivers;
    /// <summary>The form's car is about a story mission's seat: the co-op host form, or the roster with a co-op game highlighted. Its car may be "the mission's own".</summary>
    private bool StoryContext => Coop || (Mode is 7 or 8 && _games.Count > 0 && _games[Math.Clamp(_game, 0, _games.Count - 1)].Info.Coop);
    /// <summary>The seat is in the mission's own car (the default; kept in the settings): the form's car panel says so.</summary>
    private bool StoryCar => StoryContext && Shell.Settings.CoopOwnCar;
    private int CoopIndex() => Math.Max(0, CoopList.FindIndex(m => m.Msn.Equals(_setup.CoopArena, StringComparison.OrdinalIgnoreCase)));

    /// <summary>The base game's form to or from co-op (its Game Type row): the list, the seats, the game's name follow.</summary>
    private void SetCoopBase(bool on)
    {
        if (_coopBase == on) return;
        _coopBase = on;
        if (on) { _coopArea = CoopIndex(); _drivers = Math.Min(_drivers, Protocol.MaxCoopSeats); _teamPlay = false; }
        _scroll = Math.Clamp(Area - Rows + 1, 0, Math.Max(0, ListCount - Rows));
        if (!_gameRenamed && Arenas.Count > 0) _gameName = Arenas[Area].Name;
    }
    // the game's password (hosting, never saved: it lives as long as this form), and the games that refused a join for lacking one
    private string _password = "";
    private static readonly HashSet<string> KnownLocked = [];

    /// <summary>A join was refused for its password: that game asks for one the next time, whatever its listing said.</summary>
    public static void NoteLocked(string address, ulong anetId) => KnownLocked.Add(anetId != 0 ? $"anet:{anetId}" : address);

    // the roster: the games found (mode 7's LAN search, mode 8's search of the whole address book), the highlighted one
    private SessionBrowser? _browser;
    private ServerBrowser? _servers;
    private List<SessionBrowser.Found> _games = [];
    /// <summary>Mode 8's groups, one per address book entry, as last drawn.</summary>
    private IReadOnlyList<ServerGroup> _groups = [];
    /// <summary>
    /// The list's rows: a game (its index in <see cref="_games"/>) or, in mode 8, an entry's header row (Game −1, not
    /// joinable): the entry's name and its state, its games under it.
    /// </summary>
    private List<(int Group, int Game)> _rows = [];
    private int _game;
    private ulong _gameId;
    /// <summary>The entry the highlighted game was listed under (a game two servers list is under both).</summary>
    private string? _gameFrom;
    private int _seen = -1;
    private double _askIn;
    // mode 4: the anet server the game is listed on
    private string? _listOn;

    /// <summary>How many games the roster lists (the capture script waits on it).</summary>
    public int GamesFound => _games.Count;

    private void NetEnter()
    {
        _turrets = Hosting && _setup.HostTurrets;
        Shell.HostTurrets = _turrets;
        // the Nitro host form opens on the game type and arena it was left on (the index alone named another list's arena)
        if (Nitro && Hosting)
        {
            _kind = Math.Clamp(_setup.NitroKind, 0, Types.Length - 1);
            Area = I76.Formats.Shell.ArenaPick.IndexOf(HostArenas, _setup.NitroArena);
            if (Coop) _drivers = Math.Min(_drivers, Protocol.MaxCoopSeats);
        }
        Area = Math.Clamp(Area, 0, Math.Max(0, Arenas.Count - 1));
        if (Hosting && !Nitro && _setup.CoopHost) SetCoopBase(true);
        if (Hosting && Arenas.Count > 0)
        {
            // the game is named after the area until it is renamed
            string area = Arenas[Area].Name;
            // (the saved name is a melee's: a co-op form starts with its mission's title)
            _gameRenamed = !Coop && _setup.Game is { Length: > 0 } g && g != "Unnamed" && g != area;
            _gameName = _gameRenamed ? _setup.Game : area;
            _bots = _setup.Bots;
            _botCars = [.. _setup.BotCars];
            _botSkill = (I76.Sim.Missions.BotSkill)Math.Clamp(_setup.BotSkill, 0, 2);
            _rate = _setup.HostRate > 0 ? _setup.HostRate : I76.Sim.Session.SimRate.FromLabel(Shell.Settings.SimRate).Label;
        }
    }

    /// <summary>After the entry's first paint: the notice, then the address book for HOST → INTERNET (JOIN → INTERNET searches the whole book).</summary>
    private void NetShown()
    {
        void Book()
        {
            if (Mode != 4) return;
            Shell.Modal(new AddressBookPopup { Font = _font, Picked = OnAddress });
        }
        if (Notice is { Length: > 0 } n) Say(n, Book);
        else Book();
    }

    /// <summary>The address book's entries as the roster searches them.</summary>
    private static List<ServerSource> BookSources() => [.. AddressBook.Load().Entries.Select(e => ServerSource.Parse(e.Name, e.Address))];

    /// <summary>
    /// The roster's SERVERS (the port's): the address book over the form, to add, edit and delete entries. DONE or CANCEL
    /// closes it, the book saved either way; the roster then searches the book as it is, and DONE on an entry scrolls to it.
    /// </summary>
    private void OpenServers() =>
        Shell.Modal(new AddressBookPopup
        {
            Font = _font,
            Picked = e =>
            {
                _servers?.Watch(BookSources());
                _askIn = 0;
                PollGames(draw: false);
                int gi = e is null ? -1 : _groups.ToList().FindIndex(g => g.Source.Name == e.Name && g.Source.Address == e.Address);
                int at = _rows.FindIndex(r => r.Group == gi && r.Game < 0);
                if (gi >= 0 && at >= 0) _scroll = Math.Clamp(at, 0, Math.Max(0, _rows.Count - ListRows));
                Draw();
            },
        });

    /// <summary>Back from the chassis form: the search starts again by itself (PollGames).</summary>
    private void NetResume() { }

    /// <summary>A plain notice (bank 36 sprite 17 with the words on it) over the form; the form repaints when it closes.</summary>
    private void Say(string text, Action? then = null) =>
        Shell.Modal(new NoticePopup { Sprite = 17, Ok = new Rect2I(119, 61, 42, 16), Text = text, Closed = () => { Draw(); then?.Invoke(); } });

    /// <summary>
    /// HOST → INTERNET's address book DONE: nothing picked goes back to the main menu; else the entry's host is the anet
    /// server the game is listed on, and its port the one to host on.
    /// </summary>
    private void OnAddress(AddressBook.Entry? e)
    {
        if (e is null) { StopGames(); Shell.Go(new MainMenuScreen()); return; }
        bool ok = Discovery.TryParseAddress(e.Address, out var host, out var port);
        // "server" lists the game there and hosts on the default port; "server:7680" hosts on 7680; ":7680" is only the port
        _port = port > 0 && port != I76.Net.Anet.AnetWire.ServerPort ? port : Protocol.DefaultPort;
        _listOn = ok ? host : null;
        Draw();
    }

    /// <summary>The roster's search: asked every second, the answers taken every frame, the list redrawn when it changes.</summary>
    private void PollGames(bool draw = true)
    {
        int version;
        if (Mode == 8)
        {
            if (_servers is null) { _servers = new ServerBrowser { Nitro = Nitro }; _servers.Watch(BookSources()); }
            _servers.Poll();
            if ((_askIn -= Shell.Delta) <= 0) { _askIn = 1; _servers.Refresh(); }
            version = _servers.Version;
        }
        else
        {
            _browser ??= new SessionBrowser { Nitro = Nitro };
            _browser.Poll();
            if ((_askIn -= Shell.Delta) <= 0) { _askIn = 1; _browser.SearchLan(); }
            version = _browser.Version;
        }
        if (version == _seen) return;
        _seen = version;
        _games = [];
        _rows = [];
        if (_servers is { } sv)
        {
            _groups = sv.Groups;
            for (int g = 0; g < _groups.Count; g++)
            {
                _rows.Add((g, -1));
                foreach (var f in _groups[g].Games) { _rows.Add((g, _games.Count)); _games.Add(f); }
            }
        }
        else foreach (var f in _browser!.Sessions) { _rows.Add((-1, _games.Count)); _games.Add(f); }
        // the highlighted game stays highlighted: under the entry it was picked under, else wherever it is now, else the first
        int keep = _rows.FindIndex(r => r.Game >= 0 && _games[r.Game].Info.Id == _gameId && From(r.Group) == _gameFrom);
        if (keep < 0) keep = _rows.FindIndex(r => r.Game >= 0 && _games[r.Game].Info.Id == _gameId);
        if (keep < 0) keep = _rows.FindIndex(r => r.Game >= 0);
        if (keep >= 0) Highlight(keep);
        else _game = 0;
        _scroll = Math.Clamp(_scroll, 0, Math.Max(0, _rows.Count - ListRows));
        if (draw) Draw();   // (under a game's popup the form is drawn again when it closes)
    }

    /// <summary>An entry of the book as the highlight remembers it (null on the LAN).</summary>
    private string? From(int group) => group >= 0 && group < _groups.Count ? $"{_groups[group].Source.Name}\n{_groups[group].Source.Address}" : null;

    /// <summary>The game on list row <paramref name="row"/> becomes the highlighted one.</summary>
    private void Highlight(int row)
    {
        var (g, game) = _rows[row];
        _game = game;
        _gameId = _games[game].Info.Id;
        _gameFrom = From(g);
    }

    private void StopGames()
    {
        _browser?.Dispose();
        _browser = null;
        _servers?.Dispose();
        _servers = null;
        _seen = -1;
        _askIn = 0;
    }

    /// <summary>The game highlighted at (34,109) or "No games found"; rows of name, (players/seats) at x 160 and the arrow (bank 36 sprite 14) at x 204.</summary>
    private int ListRows => Nitro ? 5 : Rows;

    private void DrawGames()
    {
        // Nitro's art has the list lower (the box and its first row where the host form's are) and the up arrow on the first row
        int top = Nitro ? 130 : 109, nx = Nitro ? 34 : 14;
        if (Mode == 8) DrawServersButton();
        if (_games.Count == 0)
        {
            // the box says why there is nothing: no entries, every entry still being reached, or no games anywhere
            string none = Mode != 8 ? "No games found" : _groups.Count == 0 ? "No servers in the book"
                : _groups.All(g => g.State == ServerState.Connecting) ? "Contacting servers..." : "No games found";
            Frame.Text(_font, 34, top, AddressBookPopup.Fit(_font, none, 176));
        }
        else
        {
            _game = Math.Clamp(_game, 0, _games.Count - 1);
            var hi = _games[_game];
            string ping = (hi.Info.RateHz > 0 ? $"  {hi.Info.RateHz} Hz" : "") + (hi.Ping >= 0 ? $"  {hi.Ping} ms" : "");
            Frame.Text(_font, 34, top, AddressBookPopup.Fit(_font, RowName(hi.Info), 176 - 8 * ping.Length) + ping);
        }
        var arrow = Shell.Db.Bank(0x24)[14];
        for (int k = 0; k < ListRows && _scroll + k < _rows.Count; k++)
        {
            int y = (Nitro ? 171 : 149) + Row * k;
            var (group, game) = _rows[_scroll + k];
            if (game < 0) { DrawServerRow(_groups[group], nx, y); continue; }
            var g = _games[game].Info;
            string tag = TypeTag(g);
            int lockW = g.Locked ? 12 : 0, tagW = tag.Length > 0 ? _font.Measure(tag) : 0;
            // "(players/seats)" is right-aligned to the arrow: "(64/64)" is wider than the original's "(8/8)", and the name gives way
            string count = $"({g.Players}/{g.MaxPlayers})";
            int cx = Math.Min(nx == 14 ? 160 : 156, 203 - _font.Measure(count));
            Frame.Text(_font, nx, y, AddressBookPopup.Fit(_font, ListedName(Shell.Install, g), cx - 2 - nx - tagW - lockW));
            if (tag.Length > 0) Frame.Text(_font, cx - 2 - tagW, y, tag);
            if (g.Locked) DrawLock(cx - 2 - tagW - 11, y + 2);
            Frame.Text(_font, cx, y, count);
            Frame.Blit(arrow, 204, y);
        }
    }

    /// <summary>
    /// An entry's header row on the internet roster (not joinable): its name in the form's label face, the art's own
    /// headings' (LOCAL DRIVER, GAME NAME), and its state in the handwriting right-aligned to where the arrow column starts
    /// (x 204), where a game row's "(3/8)" ends, so it never reaches the list's edge.
    /// </summary>
    private void DrawServerRow(ServerGroup g, int x, int y)
    {
        var label = Shell.Db.Font(0x47);
        string status = g.Status;
        int sw = _font.Measure(status), sx = 203 - sw;
        string name = AddressBookPopup.Fit(label, (g.Source.Name.Trim() is { Length: > 0 } n ? n : g.Source.Address).ToUpperInvariant(), sx - 8 - x);
        int ly = y + Math.Max(0, (_font.Height - label.Height) / 2);
        Frame.Text(label, x, ly, name, ShellFrame.DefaultInk);
        Frame.Text(_font, sx, y, status);
    }

    /// <summary>SERVERS' place (the port's, hotspot 39): under the list, on the line of JOIN GAME and CANCEL.</summary>
    private static (int X0, int X1, int Y0, int Y1) ServersButton => Hot[39];

    /// <summary>
    /// SERVERS: the art's CANCEL button (record 18; ArtButtons) drawn again under the list without its word, and SERVERS
    /// lettered as the art letters CANCEL; the art has no such button.
    /// </summary>
    private (Pcx Art, Rect2I Border, ArtButtons.ArtLabel Word)? _cancelArt;

    private void DrawServersButton()
    {
        if (_cancelArt is null)
        {
            var bg = Shell.Db.Background(18);
            byte Px(int x, int y) => bg.Pixels[Math.Clamp(y, 0, bg.Height - 1) * bg.Width + Math.Clamp(x, 0, bg.Width - 1)];
            var c = Hot[15];
            if (ArtButtons.Label(Px, bg.Palette, new Rect2I(c.X0 - 4, c.Y0 - 4, c.X1 - c.X0 + 8, c.Y1 - c.Y0 + 8)) is not { } border
                || ArtButtons.Label(Px, bg.Palette, border.Box.Grow(-3)) is not { } word) return;
            _cancelArt = (bg, border.Box, word);
        }
        var (art, b, word0) = _cancelArt.Value;
        byte P(int x, int y) => art.Pixels[Math.Clamp(y, 0, art.Height - 1) * art.Width + Math.Clamp(x, 0, art.Width - 1)];
        // the same place against the hotspot as CANCEL's border against its own
        var dest = new Rect2I(ServersButton.X0 + b.Position.X - Hot[15].X0, b.Position.Y, b.Size.X, b.Size.Y);
        ArtButtons.Face(Frame, P, b, word0.Box.Position.X, dest, 4);
        ArtButtons.Letter(Frame, Shell.Db.Font(0x47), "SERVERS", word0, "CANCEL", dest);
    }

    /// <summary>A small padlock (7 x 11) in the form's ink: a game with a password.</summary>
    private void DrawLock(int x, int y)
    {
        const byte ink = 0x0f;
        Frame.Fill(x + 1, y, 5, 1, ink);            // the shackle
        Frame.Fill(x + 1, y + 1, 1, 3, ink);
        Frame.Fill(x + 5, y + 1, 1, 3, ink);
        Frame.Fill(x, y + 4, 7, 6, ink);            // the body
    }

    /// <summary>The host form's CB Radio (the port's): whether the game's players may talk to each other on the CB (push-to-talk voice chat).</summary>
    private void ToggleCb()
    {
        Shell.Settings.CbRadio = !Shell.Settings.CbRadio;
        Shell.Settings.Save();
    }

    /// <summary>The host form's Chase Crosshair (the port's): whether the game's players who have the chase crosshair on see it (an aiming aid the original does not have; off).</summary>
    private void ToggleCrosshair()
    {
        Shell.Settings.HostCrosshair = !Shell.Settings.HostCrosshair;
        Shell.Settings.Save();
    }

    /// <summary>The host form's Co-op Damage (the port's; docs/deviations.md): the factor on what the players' cars take, stepped through the list (x1 the original's).</summary>
    private void StepCoopDamage(int d)
    {
        var f = I76.Sim.Missions.CoopSetup.DamageFactors;
        int i = Math.Max(0, System.Array.IndexOf(f, Shell.Settings.CoopDamage));
        Shell.Settings.CoopDamage = f[(i + d + f.Length) % f.Length];
        Shell.Settings.Save();
    }

    private static string FactorText(float f) => $"x{f.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}";

    private string CoopDamageText => FactorText(Shell.Settings.CoopDamage);

    /// <summary>The host form's Co-op Scaling (the port's; docs/deviations.md): the most extra copies of each enemy car, one per player past the first (0 off).</summary>
    private void StepCoopScale(int d)
    {
        int n = I76.Sim.Missions.CoopSetup.MaxExtraEnemies + 1;
        Shell.Settings.CoopScale = (Shell.Settings.CoopScale + d + n) % n;
        Shell.Settings.Save();
    }

    private static string ScaleText(int cap) => cap == 0 ? "Off" : $"Up to +{cap}";

    /// <summary>The host form's Co-op Rolling Start (the port's; docs/deviations.md): a respawn or joiner comes in at a moving teammate's speed.</summary>
    private void ToggleRollingStart()
    {
        Shell.Settings.CoopRollingStart = !Shell.Settings.CoopRollingStart;
        Shell.Settings.Save();
    }

    /// <summary>The host form's Ignition Lock (the port's, #108): nobody switches their engine off in the game, so no car hides from turrets that way.</summary>
    private void ToggleIgnition()
    {
        Shell.Settings.IgnitionStaysOn = !Shell.Settings.IgnitionStaysOn;
        Shell.Settings.Save();
    }

    /// <summary>The host form's Turrets (the port's): the game's turrets turn as the 1997 CD build's or the 1998 build's (GOG's and the Nitro Pack's, the default).</summary>
    private void ToggleTurrets1997()
    {
        Shell.Settings.HostTurrets1997 = !Shell.Settings.HostTurrets1997;
        Shell.Settings.Save();
    }

    /// <summary>The host form's Car Reset (the port's): whether the game's players may press the reset-upright key (X) to put their car back on the terrain, upright and still.</summary>
    private void ToggleCarReset()
    {
        Shell.Settings.CarReset = !Shell.Settings.CarReset;
        Shell.Settings.Save();
    }

    /// <summary>
    /// The host form's Hidden Weapons &amp; Cars (the port's; Play Options' setting): the game allows the hidden vehicles and weapons,
    /// and this form's MAKE / MODEL list gains the hidden models (or loses them, the slots falling back to a stock one).
    /// </summary>
    private void SetTurrets(bool on) { _turrets = on; Shell.HostTurrets = on; }

    private void ToggleHidden()
    {
        Shell.Settings.HiddenContent = !Shell.Settings.HiddenContent;
        Shell.Settings.Save();
        _data = Shell.Melee;
        for (int i = 0; i < _slots.Count; i++)
        {
            var (n, m, v) = _slots[i];
            if (m >= _data.Models.Count) _slots[i] = (n, 0, 1);
            else if (v > _data.Models[m].Variants.Count) _slots[i] = (n, m, _data.Models[m].Variants.Count);
        }
    }

    // ---- the Nitro Pack's host form (rectangles from the original front end's table): GAME TYPE picks
    // combat, race or CTF with 2-4 teams and filters the arena list; GAME OPTIONS holds team play, the limit (laps, captures,
    // points, kills or minutes) and its value, and invite only. WEAPON RESTRICTIONS (docs/hidden-content.md §4) opens the
    // original's checklist, enforced by the server; PASSWORD asks for the game's password.
    internal sealed record GameType(string Name, I76.Sim.Missions.MeleeKind Kind, int Teams, string Ext, bool Coop = false);
    internal static readonly GameType[] Types =
    [
        new("Combat", I76.Sim.Missions.MeleeKind.Combat, 2, ".cbt"), new("Race", I76.Sim.Missions.MeleeKind.Race, 2, ".rac"),
        new("CTF 2", I76.Sim.Missions.MeleeKind.Ctf, 2, ".cf2"), new("CTF 3", I76.Sim.Missions.MeleeKind.Ctf, 3, ".cf3"), new("CTF 4", I76.Sim.Missions.MeleeKind.Ctf, 4, ".cf4"),
        // the port's: the story missions in co-op (the list is I76.Formats.Mission.MissionTitles.Coop's, not a file type's)
        new("Co-op", I76.Sim.Missions.MeleeKind.Combat, 2, "", true),
    ];
    /// <summary>One limit a game type can end on: its name, what it sets, and the values the options popup steps through (the first is the default).</summary>
    internal sealed record Limit(string Name, I76.Sim.Missions.MeleeGoal Goal, bool Clock, int[] Values);
    internal static IReadOnlyList<Limit> LimitsOf(I76.Sim.Missions.MeleeKind k) => k switch
    {
        I76.Sim.Missions.MeleeKind.Race => [new("Laps", I76.Sim.Missions.MeleeGoal.Laps, false, [3, 1, 2, 5, 10, 20]), new("Minutes", I76.Sim.Missions.MeleeGoal.None, true, [10, 5, 15, 30]),
            new("No limit", I76.Sim.Missions.MeleeGoal.Laps, false, [0])],
        I76.Sim.Missions.MeleeKind.Ctf => [new("Captures", I76.Sim.Missions.MeleeGoal.Captures, false, [3, 1, 2, 5, 10]), new("Minutes", I76.Sim.Missions.MeleeGoal.None, true, [10, 5, 15, 30]),
            new("No limit", I76.Sim.Missions.MeleeGoal.Captures, false, [0])],
        _ => [new("Points", I76.Sim.Missions.MeleeGoal.Points, false, [20000, 5000, 10000, 50000, 100000]), new("Kills", I76.Sim.Missions.MeleeGoal.Kills, false, [20, 5, 10, 30, 50]),
              new("Minutes", I76.Sim.Missions.MeleeGoal.None, true, [10, 5, 15, 30]), new("No limit", I76.Sim.Missions.MeleeGoal.None, false, [0])],
    };
    private static readonly Dictionary<int, (int X0, int X1, int Y0, int Y1)> NitroHost = new()
    {
        // the original's table ([id, x0, x1, y0, y1], its ids 3 4 9 10 8 1 6 7 here as 12 13 9 8 22 23 25 27)
        [12] = (9, 27, 147, 164), [13] = (9, 27, 252, 272), [8] = (152, 165, 423, 436), [9] = (199, 212, 423, 436), [22] = (171, 214, 397, 411),
        [23] = (200, 215, 68, 85), [25] = (55, 169, 329, 346), [27] = (64, 160, 351, 366),
        // WEAPON RESTRICTIONS: the original's hotspot 5
        [24] = (31, 193, 310, 326),
    };

    /// <summary>The host form's WEAPON RESTRICTIONS, kept for the shell's run as the original keeps its table (not saved).</summary>
    private static WeaponRestrictions _restrictions = WeaponRestrictions.None;
    private static (List<I76.Formats.Shell.RestrictionRow> Rows, int Original, int[] X, int[] Y)? _checklist;

    /// <summary>The checklist's rows: the original's from the install's Nitro shell DLL, then the port's for the hidden weapons; null without the DLL's table.</summary>
    private (List<I76.Formats.Shell.RestrictionRow> Rows, int Original, int[] X, int[] Y)? Checklist()
    {
        if (_checklist is { } c) return c;
        var dll = CarTable.NitShell(Shell.Install);
        var rows = RestrictionTable.Read(dll);
        var (x, y) = RestrictionTable.Grid(dll);
        if (rows.Count == 0 || x.Length == 0 || y.Length == 0) return null;
        int original = rows.Count;
        rows.AddRange(HiddenContent.ChecklistRows(Shell.Vfs));
        return _checklist = (rows, original, x, y);
    }

    /// <summary>A restriction list in words: All Weapons, or the checklist's names (a whole class by its header).</summary>
    internal static string RestrictionText(WeaponRestrictions w, IReadOnlyList<I76.Formats.Shell.RestrictionRow>? rows) => string.Join(", ", RestrictionNames(w, rows));

    /// <summary>The same as a list, one name a row (the Nitro session popup's WEAPONS NOT ALLOWED).</summary>
    internal static List<string> RestrictionNames(WeaponRestrictions w, IReadOnlyList<I76.Formats.Shell.RestrictionRow>? rows)
    {
        if (w.All) return ["All Weapons"];
        return [.. w.Pairs.Select(p => rows?.FirstOrDefault(r => r.Class == p.Class && r.Tier == p.Tier)?.Name ?? $"class {p.Class}{(p.Tier >= 0 ? $" tier {p.Tier}" : "")}")];
    }

    private void OpenRestrictions()
    {
        if (Checklist() is not { } c) { Say("This install has no weapon restrictions table."); return; }
        var list = new RestrictionChecklist(c.Rows);
        list.Load(_restrictions);
        Shell.Modal(new RestrictionsPopup
        {
            Font = _font, List = list, Original = c.Original, ColumnX = c.X, RowY = c.Y,
            Closed = w => { if (w is not null) _restrictions = w; Draw(); },
        });
    }
    private int _kind, _limit, _limitAt;
    private readonly Dictionary<int, List<(string Name, string Msn)>> _hostArenas = [];
    private List<(string Name, string Msn)> HostArenas => Types[_kind].Coop ? CoopList : _hostArenas.TryGetValue(_kind, out var l) ? l : _hostArenas[_kind] = NitroLists.Arenas(Shell.Install, Types[_kind].Ext);
    private bool CtfKind => Types[_kind].Kind == I76.Sim.Missions.MeleeKind.Ctf;
    private (I76.Sim.Missions.MeleeGoal Goal, int Value, int Minutes) NitroGoal
    {
        get
        {
            if (!Nitro || Coop) return (I76.Sim.Missions.MeleeGoal.None, 0, 0);
            var l = LimitsOf(Types[_kind].Kind)[_limit];
            int v = l.Values[_limitAt];
            return l.Clock ? (I76.Sim.Missions.MeleeGoal.None, 0, v) : (l.Goal, v, 0);
        }
    }

    /// <summary>The popup's GAME TYPE: LAN, or Internet; an internet game that answered directly shows its ping there instead.</summary>
    private string TypeOf(SessionBrowser.Found g) => Mode == 7 ? "LAN" : g.Ping >= 0 ? $"{g.Ping} ms" : "Internet";

    /// <summary>A game's name on the roster's top line: a co-op game's with its mission's title when the name does not say it (the rows have no room).</summary>
    private string RowName(SessionInfo g)
    {
        string name = ListedName(Shell.Install, g);
        if (!g.Coop) return name;
        string title = I76.Formats.Mission.MissionTitles.CoopTitle(Shell.Install, g.Arena);
        return name.Contains(title, StringComparison.OrdinalIgnoreCase) ? name : $"{title}: {name}";
    }

    /// <summary>A listed game's arena by this install's name for it: a co-op game's mission title, else the base table's or the file's own (empty when it lacks the map).</summary>
    private static string LocalArea(I76.Formats.Vfs.GameInstall install, SessionInfo g)
    {
        if (g.Coop) return I76.Formats.Mission.MissionTitles.CoopTitle(install, g.Arena);
        // (read once an arena: the roster draws again with every answer; a map downloaded later is read then)
        string key = $"{install.Root}|{g.Arena}";
        if (AreaNames.TryGetValue(key, out var known)) return known;
        string n = I76.Formats.Mission.MissionTitles.ArenaTitle(install, g.Arena);
        if (n.Length > 0) AreaNames[key] = n;
        return n;
    }
    private static readonly Dictionary<string, string> AreaNames = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A listed game's AREA OF PLAY (<see cref="NetLines.AreaName"/>), never its file name.</summary>
    internal static string AreaOf(I76.Formats.Vfs.GameInstall install, SessionInfo g) => NetLines.AreaName(g, LocalArea(install, g));

    /// <summary>A listed game's name (<see cref="NetLines.GameName"/>): GAME NAME as the host typed it, or its AREA OF PLAY.</summary>
    internal static string ListedName(I76.Formats.Vfs.GameInstall install, SessionInfo g) => NetLines.GameName(g, LocalArea(install, g));

    private static string TypeTag(SessionInfo g) => g.Kind switch
    {
        I76.Sim.Missions.MeleeKind.Race => "Race",
        I76.Sim.Missions.MeleeKind.Ctf => $"CTF{g.Teams}",
        _ => g.Coop ? (g.Lobby ? "co-op lobby" : "co-op") : "",
    };

    /// <summary>GAME TYPE at the top, the form's text where Nitro's art puts it (the area's highlight, GAME NAME, NO. DRIVERS).</summary>
    private void DrawNitroHost()
    {
        Frame.Text(_font, 112, 70, Types[_kind].Name);
        Frame.Text(_font, 11, 399, AddressBookPopup.Fit(_font, _gameName + (_naming ? "_" : ""), 160));
        Frame.Text(_font, 172, 422, $"{_drivers:00}");
        // the art's PASSWORD button has no state of its own
        if (_password.Length > 0) Frame.Text(_font, 150, 354, "(set)");
    }

    private void TypePopup() => Shell.Modal(new ListPopup
    {
        Sprite = 9, X = 235, Y = 122, TextX = 15, TextY = 50, Visible = 9, Items = [.. Types.Select(t => t.Name)], Font = _font,
        Up = new(18, 36, 12, 25), Down = new(18, 36, 204, 217), List = new(16, 158, 47, 198), Cancel = new(89, 158, 209, 225),
        NativeSprite = 8, NativeLayout = new PopupLayout(220, 97, 16, 82, 8, new(14, 38, 60, 78), new(14, 38, 220, 238), new(12, 188, 80, 216), new(118, 188, 259, 275)),
        Picked = i =>
        {
            if (i >= 0 && i != _kind)
            {
                _kind = i; _limit = 0; _limitAt = 0;
                Area = 0; _scroll = 0;
                if (CtfKind) _teamPlay = true;
                if (Coop) { _teamPlay = false; _drivers = Math.Min(_drivers, Protocol.MaxCoopSeats); Area = CoopIndex(); }
                if (!_gameRenamed && Arenas.Count > 0) _gameName = Arenas[0].Name;
            }
            Draw();
        },
    });

    /// <summary>
    /// GAME OPTIONS: a popup of its own (<see cref="GameOptionsPopup"/>) with a row each for team play, the limit, its value, invite only,
    /// the port's share maps, hidden weapons and cars, bots and their skill. Each acts in place; CANCEL puts all back as they were.
    /// </summary>
    private void OptionsPopup()
    {
        _turretsWas = _turrets;
        _coopDamageWas = Shell.Settings.CoopDamage;
        _coopScaleWas = Shell.Settings.CoopScale;
        _rollingWas = Shell.Settings.CoopRollingStart;
        _ignitionWas = Shell.Settings.IgnitionStaysOn;
        _turrets1997Was = Shell.Settings.HostTurrets1997;
        var was = (_teamPlay, _limit, _limitAt, _inviteOnly, _bots, _botSkill, Shell.Settings.ShareMaps, Shell.Settings.HiddenContent, Shell.Settings.CarReset, Shell.Settings.CbRadio, Shell.Settings.HostCrosshair);
        int wasRate = _rate;
        var wasCars = new List<string>(_botCars);
        OptionsPopupWith(was, wasRate, wasCars);
    }

    private bool _turretsWas;
    private float _coopDamageWas;
    private int _coopScaleWas;
    private bool _rollingWas, _ignitionWas, _turrets1997Was;

    private void OptionsPopupWith((bool, int, int, bool, int, I76.Sim.Missions.BotSkill, bool, bool, bool, bool, bool) was, int wasRate, List<string> wasCars)
    {
        Shell.Modal(new GameOptionsPopup
        {
            Font = _font, Rows = OptionRows,
            // A.I. CARS: the popup steps aside for the list and comes back as it was, CANCEL still undoing everything
            Link = () => BotCarsOpen(() => OptionsPopupWith(was, wasRate, wasCars)),
            Closed = done =>
            {
                if (!done)
                {
                    (_teamPlay, _limit, _limitAt, _inviteOnly, _bots, _botSkill) = (was.Item1, was.Item2, was.Item3, was.Item4, was.Item5, was.Item6);
                    _rate = wasRate;
                    SetTurrets(_turretsWas);
                    _botCars = wasCars;
                    if (Shell.Settings.ShareMaps != was.Item7) { Shell.Settings.ShareMaps = was.Item7; Shell.Settings.Save(); }
                    if (Shell.Settings.HiddenContent != was.Item8) ToggleHidden();
                    if (Shell.Settings.CarReset != was.Item9) ToggleCarReset();
                    if (Shell.Settings.CbRadio != was.Item10) ToggleCb();
                    if (Shell.Settings.HostCrosshair != was.Item11) ToggleCrosshair();
                    if (Shell.Settings.CoopDamage != _coopDamageWas) { Shell.Settings.CoopDamage = _coopDamageWas; Shell.Settings.Save(); }
                    if (Shell.Settings.CoopScale != _coopScaleWas) { Shell.Settings.CoopScale = _coopScaleWas; Shell.Settings.Save(); }
                    if (Shell.Settings.CoopRollingStart != _rollingWas) ToggleRollingStart();
                    if (Shell.Settings.IgnitionStaysOn != _ignitionWas) ToggleIgnition();
                    if (Shell.Settings.HostTurrets1997 != _turrets1997Was) ToggleTurrets1997();
                }
                Draw();
            },
        });
    }

    /// <summary>
    /// A.I. CARS (<see cref="BotCarsPopup"/>) from the A.I. Cars row (the base form's, the Nitro form's GAME OPTIONS): a row for each bot seat. DONE keeps the
    /// choices (melee.cfg); <paramref name="then"/> is what the form does after (the Nitro options popup comes back).
    /// </summary>
    private void BotCarsOpen(Action? then)
    {
        var copy = new List<string>(_botCars);
        Shell.Modal(new BotCarsPopup
        {
            Font = _font, Data = _data, Seats = Math.Min(_bots, MaxBots), Choices = copy,
            Closed = done => { if (done) _botCars = copy; if (then is null) Draw(); else then(); },
        });
    }

    /// <summary>The options popup's A.I. CARS line: Random, or how many of the shown seats have a car chosen.</summary>
    private string BotCarsText()
    {
        int n = _botCars.Take(Math.Min(_bots, MaxBots)).Count(c => c.Length > 0 && _data.Find(c) is not null);
        return n == 0 ? "Random" : $"{n} chosen";
    }

    /// <summary>Modern Physics is not a game option: the server runs the original's model for everyone (docs/multiplayer.md), shown as a box that is ticked and cannot be changed.</summary>
    private static readonly OptionRow PhysicsRow = new("ORIGINAL PHYSICS", false, true, "", _ => { }, Locked: true);

    /// <summary>GAME OPTIONS' TURRETS: the build whose turret slew the game plays (the popup's value line is short: 1998 or 1997 CD).</summary>
    private OptionRow TurretsRow => new("TURRETS:", true, false, HostFormLayout.TurretsShort(Shell.Settings.HostTurrets1997), _ => ToggleTurrets1997());

    private IReadOnlyList<OptionRow> OptionRows()
    {
        // a co-op game: no team play, limit or Bots; the car rules (hidden weapons and cars) and who may join
        if (Coop)
            return
            [
                new("INVITE ONLY", false, _inviteOnly, "", _ => _inviteOnly = !_inviteOnly),
                new("HIDDEN WPNS & CARS", false, Shell.Settings.HiddenContent, "", _ => ToggleHidden()),
                new("UNLIM. TURRETS", false, _turrets, "", _ => SetTurrets(!_turrets)),
                new("CAR RESET", false, Shell.Settings.CarReset, "", _ => ToggleCarReset()),
                new("CB RADIO", false, Shell.Settings.CbRadio, "", _ => ToggleCb()),
                new("CHASE CROSSHAIR", false, Shell.Settings.HostCrosshair, "", _ => ToggleCrosshair()),
                // the port's: the players' cars take more or less damage (docs/deviations.md)
                new("CO-OP DAMAGE:", true, false, CoopDamageText, StepCoopDamage),
                // the port's: extra enemy cars for a bigger group (docs/deviations.md)
                new("CO-OP SCALING:", true, false, ScaleText(Shell.Settings.CoopScale), StepCoopScale),
                // the port's: a respawn or joiner at a moving teammate's speed (docs/deviations.md)
                new("ROLLING START", false, Shell.Settings.CoopRollingStart, "", _ => ToggleRollingStart()),
                // the port's: nobody switches their engine off (#108)
                new("IGNITION STAYS ON", false, Shell.Settings.IgnitionStaysOn, "", _ => ToggleIgnition()),
                TurretsRow,
                new("SIM RATE:", true, false, $"{_rate} Hz", d => _rate = StepRate(_rate, d)),
                PhysicsRow,
            ];
        var limits = LimitsOf(Types[_kind].Kind);
        var l = limits[_limit];
        var rows = new List<OptionRow>
        {
            new("TEAM PLAY", false, _teamPlay || CtfKind, "", _ => { if (!CtfKind) _teamPlay = !_teamPlay; }, Locked: CtfKind),
            new("LIMIT:", true, false, l.Name, d => { _limit = (_limit + d + limits.Count) % limits.Count; _limitAt = 0; }),
        };
        if (l.Values.Length > 1)
            rows.Add(new((l.Name is "Points" or "Kills" ? "MAX. " : "") + l.Name.ToUpperInvariant() + ":", true, false, l.Values[_limitAt].ToString(), d =>
            {
                // in order of size, held at the ends (the list's first is the default, not the smallest)
                var order = Enumerable.Range(0, l.Values.Length).OrderBy(i => l.Values[i]).ToList();
                _limitAt = order[Math.Clamp(order.IndexOf(_limitAt) + d, 0, order.Count - 1)];
            }));
        rows.Add(new("INVITE ONLY", false, _inviteOnly, "", _ => _inviteOnly = !_inviteOnly));
        // the port's: a custom arena goes to players who lack it (docs/multiplayer.md "Custom maps")
        rows.Add(new("SHARE MAPS", false, Shell.Settings.ShareMaps, "", _ => { Shell.Settings.ShareMaps = !Shell.Settings.ShareMaps; Shell.Settings.Save(); }));
        // the port's: the game's Hidden Weapons & Cars, the host's decision (joiners with the setting on may then bring them)
        rows.Add(new("HIDDEN WPNS & CARS", false, Shell.Settings.HiddenContent, "", _ => ToggleHidden()));
        // the port's: several turrets on a roof, the host's decision (a joining car with a second turret is refused where it is off)
        rows.Add(new("UNLIM. TURRETS", false, _turrets, "", _ => SetTurrets(!_turrets)));
        // the port's: the reset-upright key (X) works in the game, the host's decision (on: the original's F12 trick worked online)
        rows.Add(new("CAR RESET", false, Shell.Settings.CarReset, "", _ => ToggleCarReset()));
        // the port's: the CB radio, push-to-talk voice chat, the host's decision (on by default)
        rows.Add(new("CB RADIO", false, Shell.Settings.CbRadio, "", _ => ToggleCb()));
        // the port's: the chase crosshair, an aiming aid the original does not have, the host's decision (off by default)
        rows.Add(new("CHASE CROSSHAIR", false, Shell.Settings.HostCrosshair, "", _ => ToggleCrosshair()));
        // the port's: nobody switches their engine off, so no car hides from turrets that way (#108; off by default)
        rows.Add(new("IGNITION STAYS ON", false, Shell.Settings.IgnitionStaysOn, "", _ => ToggleIgnition()));
        // the port's: the turrets turn as the 1997 CD build's or the 1998 build's (the default)
        rows.Add(TurretsRow);
        // the port's: players the server drives (docs/multiplayer.md "Bots"), up to the free seats, and how well
        rows.Add(new("BOTS:", true, false, $"{Math.Min(_bots, MaxBots):00}", d => _bots = Math.Clamp(Math.Min(_bots, MaxBots) + d, 0, MaxBots)));
        rows.Add(new("BOT SKILL:", true, false, _botSkill.ToString(), d => _botSkill = (I76.Sim.Missions.BotSkill)(((int)_botSkill + d + 3) % 3)));
        rows.Add(new("A.I. CARS:", false, false, BotCarsText(), _ => { }, Link: true, Locked: MaxBots == 0 || _bots == 0));
        // the port's: the game's simulation rate, which everyone in it plays at, and the physics, which is the original's for everyone
        rows.Add(new("SIM RATE:", true, false, $"{_rate} Hz", d => _rate = StepRate(_rate, d)));
        rows.Add(PhysicsRow);
        return rows;
    }

    /// <summary>The network forms' own clicks; false for the ones every form shares.</summary>
    private bool NetAct(int id, Vector2I p)
    {
        switch (id)
        {
            case 6 when Hosting: Broadcast(); return true;
            case 6: JoinGame(); return true;
            case 8 when Hosting: _drivers = Math.Max(1, _drivers - 1); _bots = Math.Min(_bots, MaxBots); return true;
            case 9 when Hosting: _drivers = Math.Min(MaxSeats, _drivers + 1); return true;
            case 32 when Hosting: SetCoopBase(!_coopBase); return true;
            case 29 when Hosting: _bots = Math.Max(0, Math.Min(_bots, MaxBots) - 1); return true;
            case 30 when Hosting: _bots = Math.Min(MaxBots, _bots + 1); return true;
            case 33 when Hosting: _rate = StepRate(_rate, 1); return true;
            case 34 when Hosting: BotCarsOpen(null); return true;
            case 31 when Hosting: _botSkill = (I76.Sim.Missions.BotSkill)(((int)_botSkill + 1) % 3); return true;
            case 11 when Hosting:
            {
                int row = _scroll + (DrawnHost ? HostFormLayout.ListRowAt(p.Y) : (p.Y - 151) / Row);
                if (row < 0 || row >= Arenas.Count || row - _scroll >= Rows) return true;
                Area = row;
                if (!_gameRenamed) _gameName = Arenas[row].Name;
                return true;
            }
            case 11:
            {
                // a row picks the game; its arrow (x from 207) shows the game's popup; an entry's header row does neither
                int row = _scroll + (p.Y - (Nitro ? 173 : 151)) / Row;
                if (row < 0 || row >= _rows.Count || _rows[row].Game < 0) return true;
                var g = _games[_rows[row].Game];
                if (p.X < 207) Highlight(row);
                else Shell.Modal(new SessionPopup
                {
                    Info = g.Info, Type = TypeOf(g), Data = _data, Font = _font, Closed = Draw, Rules = RulesText(g.Info),
                    Live = LiveInfo(g.Info.Id),
                    RuleRows = Nitro ? RuleRows(g.Info) : null,
                });
                return true;
            }
            case 39 when Mode == 8: OpenServers(); return true;
            case 23: TypePopup(); return true;
            case 25: OptionsPopup(); return true;
            case 16: _teamPlay = !_teamPlay; return true;
            case 17: _inviteOnly = !_inviteOnly; return true;
            case 18: _scoreOn = !_scoreOn; return true;
            case 20: _maxScore = Math.Max(1000, _maxScore - 1000); return true;
            case 21: _maxScore = Math.Min(100000, _maxScore + 1000); return true;
            case 22: _naming = true; return true;
            case 26: Shell.Settings.ShareMaps = !Shell.Settings.ShareMaps; Shell.Settings.Save(); return true;
            case 28 when Hosting: ToggleHidden(); return true;
            case 36 when Hosting: ToggleCarReset(); return true;
            case 37 when Hosting: ToggleCb(); return true;
            case 38 when Hosting: ToggleCrosshair(); return true;
            case 40 when Hosting: StepCoopDamage(1); return true;
            case 41 when Hosting: StepCoopScale(1); return true;
            case 42 when Hosting: ToggleRollingStart(); return true;
            case 43 when Hosting: ToggleIgnition(); return true;
            case 44 when Hosting: ToggleTurrets1997(); return true;
            case 35 when Hosting: SetTurrets(!_turrets); return true;
            case 27 when Hosting: AskPassword(null, _password, pw => _password = pw ?? _password); return true;
            case 24 when Hosting && Nitro: OpenRestrictions(); return true;
        }
        return false;
    }

    /// <summary>GAME NAME's RENAME: edited in place; left empty it is the area's name again.</summary>
    private void RenameGame()
    {
        bool done = Typing.Edit(Shell, ref _gameName, 24);
        if (done)
        {
            _naming = false;
            _gameRenamed = _gameName.Trim().Length > 0;
            if (!_gameRenamed && Arenas.Count > 0) _gameName = Arenas[Area].Name;
        }
        Draw();
    }

    private void ChooseTeam(Action<int> then) =>
        Shell.Modal(new TeamPopup { Font = _font, Picked = t => { Draw(); if (t >= 0) then(t + 1); } });

    /// <summary>BROADCAST GAME: CHOOSE A TEAM with team play, then a listen server with the form's rules, joined at once.</summary>
    private void Broadcast()
    {
        if (Arenas.Count == 0) { Say(Coop ? "There are no story missions." : "There are no arenas of that kind."); return; }
        void Go(int team)
        {
            Leave(save: true);
            StopGames();
            var (driver, model, variant) = _slots[0];
            Shell.Launch(new ShellLaunch(Arenas[Area].Msn, GameKind.Multiplayer, _data.Vcf(model, variant))
            {
                Driver = driver, Model = _data.Models[model].MakeModel, ModelIndex = model,
                Net = new NetLaunch(Mode)
                {
                    ListOn = Mode == 4 ? _listOn : null,
                    Port = _port, GameName = _gameName, MaxPlayers = Coop ? Math.Clamp(_drivers, 1, Protocol.MaxCoopSeats) : _drivers,
                    TeamPlay = !Coop && (_teamPlay || (Nitro && Types[_kind].Kind == I76.Sim.Missions.MeleeKind.Ctf)), InviteOnly = _inviteOnly,
                    MaxScore = _scoreOn && !Nitro && !Coop ? _maxScore : 0, Team = team, ShareMaps = Shell.Settings.ShareMaps, Password = _password,
                    Kind = Nitro && !Coop ? Types[_kind].Kind : null, Goal = NitroGoal.Goal, GoalValue = NitroGoal.Value, Minutes = NitroGoal.Minutes, CtfTeams = Types[_kind].Teams,
                    Restrictions = Nitro ? _restrictions : WeaponRestrictions.None, Hidden = Shell.Settings.HiddenContent, Turrets = _turrets, CarReset = Shell.Settings.CarReset, Cb = Shell.Settings.CbRadio, Crosshair = Shell.Settings.HostCrosshair,
                    Bots = Math.Min(_bots, MaxBots), BotSkill = _botSkill, BotCars = [.. _botCars.Take(Math.Min(_bots, MaxBots))], Rate = _rate,
                    Coop = Coop, StoryCar = Coop && Shell.Settings.CoopOwnCar, CoopDamage = Coop ? Shell.Settings.CoopDamage : 1f, CoopScale = Coop ? Shell.Settings.CoopScale : 0, CoopRollingStart = Coop && Shell.Settings.CoopRollingStart, IgnitionStaysOn = Shell.Settings.IgnitionStaysOn, Turrets1997 = Shell.Settings.HostTurrets1997,
                },
            });
        }
        if (_teamPlay && !Coop) ChooseTeam(Go); else Go(0);
    }

    /// <summary>A game's WEAPON RESTRICTIONS and Hidden Weapons &amp; Cars as the session popup shows them, or null for neither.</summary>
    private string? RulesText(SessionInfo g)
    {
        var parts = new List<string>();
        if (g.RateHz > 0) parts.Add($"Sim rate {g.RateHz} Hz");
        if (!g.Restricted.IsEmpty) parts.Add($"Restricted: {RestrictionText(g.Restricted, Nitro ? Checklist()?.Rows : null)}");
        if (g.Hidden) parts.Add("Hidden weapons & cars allowed");
        if (g.Turrets) parts.Add("Unlimited turrets");
        if (!g.CarReset) parts.Add("No car reset");
        if (!g.Cb) parts.Add("CB radio off");
        if (g.Crosshair) parts.Add("Chase crosshair allowed");
        if (g.IgnitionStaysOn) parts.Add("Ignition stays on");
        if (g.Turrets1997) parts.Add("Turrets as on the 1997 CD");
        if (g.Coop && g.CoopDamage != 1f) parts.Add($"Co-op damage {FactorText(g.CoopDamage)}");
        if (g.Coop && g.CoopScale > 0) parts.Add($"Co-op scaling up to +{g.CoopScale}");
        if (g.Coop && g.RollingStart) parts.Add("Rolling start");
        return parts.Count > 0 ? string.Join(". ", parts) : null;
    }

    /// <summary>A game's latest answer by its id, as the roster keeps polling while its popup is up.</summary>
    private Func<SessionInfo?> LiveInfo(ulong id) => () =>
    {
        PollGames(draw: false);
        return _games.FirstOrDefault(g => g.Info.Id == id)?.Info;
    };

    /// <summary>The Nitro popup's WEAPONS NOT ALLOWED rows: "No Restrictions", "All Weapons" or the names, then the port's Hidden Weapons &amp; Cars.</summary>
    private List<string> RuleRows(SessionInfo g)
    {
        var rows = g.Restricted.IsEmpty ? new List<string> { "No Restrictions" } : RestrictionNames(g.Restricted, Checklist()?.Rows);
        if (g.Hidden) rows.Add("Hidden cars allowed");
        if (g.Turrets) rows.Add("Unlim. turrets allowed");
        if (!g.CarReset) rows.Add("No car reset");
        if (!g.Cb) rows.Add("CB radio off");
        if (g.Crosshair) rows.Add("Crosshair allowed");
        if (g.IgnitionStaysOn) rows.Add("Ignition stays on");
        if (g.Turrets1997) rows.Add("1997 CD turrets");
        if (g.Coop && g.CoopDamage != 1f) rows.Add($"Co-op damage {FactorText(g.CoopDamage)}");
        if (g.Coop && g.CoopScale > 0) rows.Add($"Co-op scaling +{g.CoopScale}");
        if (g.Coop && g.RollingStart) rows.Add("Rolling start");
        return rows;
    }

    /// <summary>
    /// The joiner's own check of its car against the game's rules before it connects (the original runs the car
    /// check and shows the host's restrictions): the server decides, this only says why at once. Null when the car may go.
    /// </summary>
    private string? CarRefusal(SessionInfo g, int model, int variant)
    {
        var vfs = Shell.Vfs;
        if (vfs.TryRead(_data.Vcf(model, variant)) is not { } vcf) return null;
        TripCar car;
        try { car = TripCar.FromVcf(vcf); }
        catch (Exception ex) when (ex is System.IO.InvalidDataException or ArgumentException or IndexOutOfRangeException) { return null; }
        var guns = car.Weapons.Select(w => w.Gdf).ToList();
        bool hidden = g.MayBringHidden;
        if (!hidden && HiddenContent.SecretBodyOf(vfs, _data.Models[model].Stem, car.Vdf) is not null)
            return $"This game refuses your car: a {car.Variant} body is a hidden car and {HiddenContent.Off}.";
        if (HiddenContent.Check(vfs, CarTable.OfferedStems(Shell.Install), Shell.Install.IsNitro, hidden, _data.Models[model].Stem, guns) is { } why)
            return $"This game refuses your car: {why}.";
        if (!g.Turrets && CarConfigCheck.Strict(vfs, car, _data.Models[model].Stem) is { } strict && strict.StartsWith("a second turret"))
            return $"This game allows one turret on a car ({strict}). Change your car's weapons.";
        var bad = g.Restricted.Violations(vfs, guns);
        return bad.Count > 0 ? $"This game does not allow {string.Join(", ", bad.Select(b => HiddenContent.WeaponName(vfs, b)))}. Change your car's weapons." : null;
    }

    /// <summary>JOIN GAME: the highlighted game ("There are no games to join." without one), CHOOSE A TEAM if it plays teams, then in.</summary>
    private void JoinGame()
    {
        if (_games.Count == 0) { Say("There are no games to join."); return; }
        var g = _games[Math.Clamp(_game, 0, _games.Count - 1)];
        if (g.Info.Over) { Say("That melee is over."); return; }
        if (g.Info.Full) { Say("That game is full."); return; }
        // a co-op game's seat may be in the mission's own car (the default), which nothing restricts
        bool story = g.Info.Coop && Shell.Settings.CoopOwnCar;
        if (!story && CarRefusal(g.Info, _slots[0].Model, _slots[0].Variant) is { } refused) { Say(refused); return; }
        void Go(int team, string password)
        {
            Leave(save: true);
            StopGames();
            var (driver, model, variant) = _slots[0];
            Shell.Launch(new ShellLaunch(g.Info.Arena, GameKind.Multiplayer, _data.Vcf(model, variant))
            {
                Driver = driver, Model = _data.Models[model].MakeModel, ModelIndex = model,
                Net = new NetLaunch(Mode)
                {
                    Connect = $"{g.EndPoint.Address}:{g.EndPoint.Port}", GameName = g.Info.Name, MaxPlayers = g.Info.MaxPlayers,
                    AnetServer = g.Anet is { } l ? (l.Port == I76.Net.Anet.AnetWire.ServerPort ? l.Server : $"{l.Server}:{l.Port}") : null, AnetGame = g.Anet?.Session,
                    TeamPlay = g.Info.TeamPlay, MaxScore = g.Info.MaxScore, Team = team, Password = password, StoryCar = story, Coop = g.Info.Coop, InLobby = g.Info.Lobby,
                },
            });
        }
        void Team(string password) { if (g.Info.TeamPlay) ChooseTeam(t => Go(t, password)); else Go(0, password); }
        // a locked game (its listing says so, or it refused us before) asks for its password first
        bool locked = g.Info.Locked || KnownLocked.Contains(g.Anet is { } al ? $"anet:{al.Session.Id}" : $"{g.EndPoint.Address}:{g.EndPoint.Port}");
        if (locked) AskPassword(g.Info.Name, "", pw => { if (!string.IsNullOrEmpty(pw)) Team(pw); });
        else Team("");
    }

    /// <summary>
    /// The password prompt: a notice with a line to type in. For a join it names the game; for the host form it is the
    /// password to set (empty clears it). Enter or OK takes it, Esc leaves it as it was (null).
    /// </summary>
    private void AskPassword(string? game, string current, Action<string?> done) =>
        Shell.Modal(new PasswordPopup
        {
            Font = _font, Game = game, Text = current, Mask = game is not null,
            Closed = pw => { Draw(); done(pw); },
        });
}

/// <summary>The shell's in-place text edit: typed characters up to a length, Backspace, and Enter, Esc or a click to finish.</summary>
internal static class Typing
{
    public static bool Edit(Shell sh, ref string s, int max)
    {
        foreach (char c in sh.Typed) if (s.Length < max) s += c;
        if (sh.Keys.Contains(Key.Backspace) && s.Length > 0) s = s[..^1];
        return sh.Keys.Contains(Key.Enter) || sh.Keys.Contains(Key.KpEnter) || sh.Keys.Contains(Key.Escape) || sh.Clicked;
    }
}

/// <summary>
/// CHOOSE A TEAM: bank 36 sprite 19 at (191,83) with its
/// shadow, an X at x 228 by the team picked, eight rows, DONE
/// and CANCEL. All eight rows pick (team 1–8; the eighth, MEN IN BLACK, is
/// Entity.Team8, since 0 is nobody's in the port).
/// </summary>
public sealed class TeamPopup : ShellScreen
{
    public required ShellFont Font { get; init; }
    /// <summary>The row picked (0..7), or −1 for CANCEL.</summary>
    public required Action<int> Picked { get; init; }
    public override bool EscapeOpensOptions => false;
    private const int X = 191, Y = 83, Picks = 8;
    private static readonly (int X0, int X1, int Y0, int Y1)[] Row =
        [(37, 190, 56, 70), (37, 162, 82, 95), (37, 175, 108, 121), (37, 222, 134, 147), (37, 178, 160, 173), (37, 173, 186, 199), (37, 163, 212, 225), (37, 170, 238, 251)];
    private static readonly (int X0, int X1, int Y0, int Y1) Done = (90, 162, 286, 303), Cancel = (169, 241, 286, 303);
    private int _team;

    public override void Enter() => Draw();

    private void Draw()
    {
        Frame.Restore(X, Y, 265, 320);
        Frame.Blit(Shell.Db.Bank(0x24)[19], X, Y);
        Frame.Fill(X + 6, Y + 314, 259, 6, 0x0f);
        Frame.Fill(X + 259, Y + 6, 6, 314, 0x0f);
        Frame.Text(Font, 228, Y + Row[_team].Y0, "X");
    }

    private static bool In((int X0, int X1, int Y0, int Y1) r, Vector2I p) => r.X0 < p.X - X && p.X - X < r.X1 && r.Y0 < p.Y - Y && p.Y - Y < r.Y1;

    private static PadRegion Reg((int X0, int X1, int Y0, int Y1) r) => PadRegion.Of((r.X0 + X, r.X1 + X, r.Y0 + Y, r.Y1 + Y));
    public override IReadOnlyList<PadRegion> PadRegions => [.. Row.Take(Picks).Select(Reg), Reg(Done), Reg(Cancel)];

    public override void Tick()
    {
        if (Shell.Keys.Contains(Key.Escape)) { Close(-1); return; }
        if (!Shell.Clicked) return;
        var p = Shell.Mouse;
        for (int i = 0; i < Picks; i++)
            if (In(Row[i], p)) { Shell.Sound(82); _team = i; Draw(); return; }
        if (In(Done, p)) Close(_team);
        else if (In(Cancel, p)) Close(-1);
    }

    private void Close(int team)
    {
        Shell.Sound(82);
        Frame.Restore(X, Y, 265, 320);
        Shell.Modal(null);
        Picked(team);
    }
}

/// <summary>
/// A game's popup from the roster's arrow: bank
/// 36 sprite 13 at (9,108); GAME NAME, GAME TYPE, SCORE LIMIT ("None"),
/// TEAMS (Yes/No) and AREA OF PLAY on its first line, then each driver's
/// name, make and model, and team ("N/A" without team play); DONE closes it.
/// The Nitro Pack has no sprite 13: its redrawn popup is its own bank 30's sprite 12 (622x349) at (9,66),
/// with GAME NAME, GAME TYPE, LIMIT, AREA OF
/// PLAY, PASSWORD, a WEAPONS NOT ALLOWED list of four rows with its arrows, the drivers from y 173 and DONE.
/// Esc, Enter and the pad's Back close it too (the port's: the original took only DONE).
/// </summary>
public sealed class SessionPopup : ShellScreen
{
    public required SessionInfo Info { get; set; }
    /// <summary>The game's latest answer while the popup is up (its drivers may come after it opened), or null once it is gone.</summary>
    public Func<SessionInfo?>? Live { get; init; }
    public required string Type { get; init; }
    public required MeleeData Data { get; init; }
    public required ShellFont Font { get; init; }
    public required Action Closed { get; init; }
    /// <summary>The game's WEAPON RESTRICTIONS and Hidden Weapons &amp; Cars in words (the port's line under the drivers), or null.</summary>
    public string? Rules { get; init; }
    /// <summary>The Nitro popup's WEAPONS NOT ALLOWED rows (null: the base game's popup).</summary>
    public IReadOnlyList<string>? RuleRows { get; init; }
    public override bool EscapeOpensOptions => false;

    private Sprite? _nitroArt;
    private bool NitroLayout => _nitroArt is not null;
    private int X => 9;
    private int Y => NitroLayout ? 66 : 108;
    private const int W = 622;
    private int H => NitroLayout ? 349 : 264;
    // DONE, relative: the base game's, the Nitro art's button; the Nitro list's arrows
    private (int X0, int X1, int Y0, int Y1) Done => NitroLayout ? (538, 608, 321, 336) : (537, 609, 235, 252);
    private static readonly (int X0, int X1, int Y0, int Y1) RulesUp = (408, 424, 34, 49), RulesDown = (408, 424, 123, 138);

    private int _scroll, _rulesScroll;
    private const int Rows = 8, RuleRowsShown = 4;
    // the base popup's rules line: the rules a page at a time when they do not fit on it, the next every RulesPageMs
    private int _rulesPage, _rulesPages = 1;
    private long _rulesPageAt = System.Environment.TickCount64;
    private const int RulesPageMs = 3000;

    /// <summary>The rules ("A. B. C") as lines of at most <paramref name="width"/> pixels, whole rules to a line (one too long alone is fitted).</summary>
    public static List<string> RulePages(string rules, int width, Func<string, int> measure)
    {
        var pages = new List<string>();
        string line = "";
        foreach (var part in rules.Split(". ", StringSplitOptions.RemoveEmptyEntries))
        {
            string next = line.Length == 0 ? part : line + ". " + part;
            if (line.Length > 0 && measure(next) > width) { pages.Add(line); line = part; }
            else line = next;
        }
        if (line.Length > 0) pages.Add(line);
        return pages;
    }

    public override void Enter()
    {
        if (Shell.Install.IsNitro && RuleRows is not null) _nitroArt = Shell.Db.BankNative(30)[12];
        Draw();
    }

    private void Draw()
    {
        var art = _nitroArt ?? Shell.Db.Bank(0x24)[13];
        if (art is null) Frame.Fill(X, Y, W, H, 0x0f);   // no art at all: at least a box to read the text on
        else Frame.Blit(art, X, Y);
        Frame.Fill(X + 6, Y + H, W, 6, 0x0f);
        Frame.Fill(X + W, Y + 6, 6, H, 0x0f);
        string area = MeleeScreen.AreaOf(Shell.Install, Info), name = MeleeScreen.ListedName(Shell.Install, Info);
        int dy;
        if (NitroLayout)
        {
            Frame.Text(Font, X + 20, Y + 34, AddressBookPopup.Fit(Font, name, 154));
            Frame.Text(Font, X + 20, Y + 78, AddressBookPopup.Fit(Font, NetLines.SessionType(Info), 154));
            Frame.Text(Font, X + 210, Y + 34, AddressBookPopup.Fit(Font, NetLines.SessionLimit(Info), 154));
            Frame.Text(Font, X + 210, Y + 79, AddressBookPopup.Fit(Font, area, 154));
            Frame.Text(Font, X + 210, Y + 125, Info.Locked ? "Required" : "None");
            var rules = RuleRows!;
            for (int k = 0; k < RuleRowsShown && _rulesScroll + k < rules.Count; k++)
                Frame.Text(Font, X + 425, Y + 52 + 17 * k, AddressBookPopup.Fit(Font, rules[_rulesScroll + k], 180));
            dy = Y + 173;
        }
        else
        {
            int y = Y + 33;
            Frame.Text(Font, X + 20, y, AddressBookPopup.Fit(Font, name, 150));
            Frame.Text(Font, X + 181, y, AddressBookPopup.Fit(Font, Info.Coop ? (Info.Lobby ? $"Co-op lobby {Type}" : $"Co-op {Type}") : Type, 126));
            Frame.Text(Font, X + 314, y, Info.MaxScore > 0 ? Info.MaxScore.ToString() : "None");
            Frame.Text(Font, X + 459, y, Info.TeamPlay ? "Yes" : "No");
            Frame.Text(Font, X + 488, y, AddressBookPopup.Fit(Font, area, 128));
            dy = Y + 91;
        }
        // the original's list holds eight; with more drivers (up to 64) the wheel, Up/Down and PgUp/PgDn page through it
        foreach (var d in Info.Drivers.Skip(_scroll).Take(Rows))
        {
            string car = Data.CarName(d.Car ?? "", d.Model);
            Frame.Text(Font, X + 18, dy, AddressBookPopup.Fit(Font, d.Name ?? "", NitroLayout ? 156 : 160));
            Frame.Text(Font, X + (NitroLayout ? 180 : 184), dy, AddressBookPopup.Fit(Font, car, 270));
            Frame.Text(Font, X + (NitroLayout ? 457 : 460), dy, AddressBookPopup.Fit(Font, TeamName(d.Team), 145));
            dy += 17;
        }
        bool paged = Info.Drivers.Count > Rows;
        int foot = NitroLayout ? Y + 322 : Y + 238;
        if (!NitroLayout && Rules is { } r)
        {
            int width = paged ? 330 : 510;
            var pages = RulePages(r, width, Font.Measure);
            _rulesPages = Math.Max(1, pages.Count);
            Frame.Text(Font, X + 18, foot, AddressBookPopup.Fit(Font, pages.Count > 0 ? pages[_rulesPage % _rulesPages] : "", width));
        }
        // Nitro's art has no place for the rate: the foot's middle (the base popup's is the rules line)
        if (NitroLayout && Info.RateHz > 0) Frame.Text(Font, X + 210, foot, $"Sim rate {Info.RateHz} Hz");
        if (paged) Frame.Text(Font, X + (NitroLayout ? 18 : 380), foot, $"{_scroll + 1}-{Math.Min(_scroll + Rows, Info.Drivers.Count)} of {Info.Drivers.Count}");
    }

    /// <summary>A driver's team as the popup names it: "N/A" (the Nitro Pack's "Not a team game") without team play, the CTF teams in CTF.</summary>
    private string TeamName(int team)
    {
        if (!Info.TeamPlay) return NitroLayout ? "Not a team game" : "N/A";
        if (Info.Kind == I76.Sim.Missions.MeleeKind.Ctf) return team is >= 1 and <= 4 ? I76.Sim.Missions.CtfGame.TeamName(team) : "Unknown";
        return team >= 1 && team <= MeleeScreen.Teams.Count ? MeleeScreen.Teams[team - 1] : NitroLayout ? "Unknown" : "N/A";
    }

    private void Scroll(int rows)
    {
        int to = Math.Clamp(_scroll + rows, 0, Math.Max(0, Info.Drivers.Count - Rows));
        if (to == _scroll) return;
        _scroll = to;
        Draw();
    }

    private void ScrollRules(int rows)
    {
        int to = Math.Clamp(_rulesScroll + rows, 0, Math.Max(0, (RuleRows?.Count ?? 0) - RuleRowsShown));
        if (to == _rulesScroll) return;
        _rulesScroll = to;
        Draw();
    }

    private bool In((int X0, int X1, int Y0, int Y1) r, Vector2I p) => r.X0 < p.X - X && p.X - X < r.X1 && r.Y0 < p.Y - Y && p.Y - Y < r.Y1;
    private PadRegion Reg((int X0, int X1, int Y0, int Y1) r) => PadRegion.Of((r.X0 + X, r.X1 + X, r.Y0 + Y, r.Y1 + Y));
    public override IReadOnlyList<PadRegion> PadRegions => NitroLayout ? [Reg(RulesUp), Reg(RulesDown), Reg(Done)] : [Reg(Done)];

    public override void Tick()
    {
        if (Shell.Keys.Contains(Key.Escape) || Shell.Keys.Contains(Key.Enter) || Shell.Keys.Contains(Key.KpEnter)) { Close(); return; }
        if (Live?.Invoke() is { } now && !ReferenceEquals(now, Info))
        {
            Info = now;
            _scroll = Math.Clamp(_scroll, 0, Math.Max(0, Info.Drivers.Count - Rows));
            Draw();
        }
        if (_rulesPages > 1 && System.Environment.TickCount64 - _rulesPageAt >= RulesPageMs)
        {
            _rulesPage = (_rulesPage + 1) % _rulesPages;
            _rulesPageAt = System.Environment.TickCount64;
            Draw();
        }
        if (Shell.Wheel != 0) Scroll(Shell.WheelRows);
        if (Shell.Keys.Contains(Key.Down)) Scroll(1);
        if (Shell.Keys.Contains(Key.Up)) Scroll(-1);
        if (Shell.Keys.Contains(Key.Pagedown)) Scroll(Rows);
        if (Shell.Keys.Contains(Key.Pageup)) Scroll(-Rows);
        if (!Shell.Clicked) return;
        var p = Shell.Mouse;
        if (NitroLayout && In(RulesUp, p)) { Shell.Sound(82); ScrollRules(-1); }
        else if (NitroLayout && In(RulesDown, p)) { Shell.Sound(82); ScrollRules(1); }
        else if (In(Done, p)) Close();
    }

    private void Close()
    {
        Shell.Sound(82);
        Frame.Restore(X, Y, W + 6, H + 6);
        Shell.Modal(null);
        Closed();
    }
}

/// <summary>
/// The game-server address book (internet.lst: u32 n, then n entries of a
/// 32-byte name and a 64-byte address), kept in user://. Entries are put in
/// by their first two letters.
/// </summary>
public sealed class AddressBook
{
    public sealed class Entry { public string Name = "", Address = ""; }
    public List<Entry> Entries { get; } = [];
    private const string Path = "user://internet.lst";

    /// <summary>
    /// The book a player starts with (no internet.lst yet): the community's anet game servers (docs/multiplayer.md
    /// "Lobby servers"). An existing book is the player's: only <see cref="Added"/> goes into it, once.
    /// </summary>
    public static readonly (string Name, string Address)[] Defaults =
    [
        ("BZ911", "alink.bz911.net"), ("Glenrio", "glenrio.interstate76.com"), ("Interstate76.com", "play.interstate76.com"),
        ("Iondriver", "alink.iondriver.com"),
    ];

    /// <summary>Servers added to the defaults after players had books: put into an existing book once (a marker file remembers), so one the player removes stays removed.</summary>
    private static readonly (string Name, string Address)[] Added =
    [
        ("Glenrio", "glenrio.interstate76.com"), ("Interstate76.com", "play.interstate76.com"),
    ];
    private const string AddedMarker = "user://internet_i76com.done";

    public static AddressBook Load()
    {
        var b = new AddressBook();
        if (!Godot.FileAccess.FileExists(Path))
        {
            foreach (var (dn, da) in Defaults) b.Entries.Add(new Entry { Name = dn, Address = da });
            return b;
        }
        var d = Godot.FileAccess.GetFileAsBytes(Path);
        if (d.Length < 4) return b;
        int n = BitConverter.ToInt32(d, 0);
        for (int i = 0; i < n && 4 + (i + 1) * 0x60 <= d.Length; i++)
            b.Entries.Add(new Entry { Name = CStr(d, 4 + i * 0x60, 0x20), Address = CStr(d, 4 + i * 0x60 + 0x20, 0x40) });
        if (!Godot.FileAccess.FileExists(AddedMarker))
        {
            foreach (var (an, aa) in Added)
                if (!b.Entries.Exists(x => x.Address.Equals(aa, StringComparison.OrdinalIgnoreCase))) b.Insert(new Entry { Name = an, Address = aa });
            b.Save();
            using var m = Godot.FileAccess.Open(AddedMarker, Godot.FileAccess.ModeFlags.Write);
            m?.StoreString("1");
        }
        return b;
    }

    private static string CStr(byte[] d, int at, int len)
    {
        int n = Array.IndexOf(d, (byte)0, at, len) is var z && z >= 0 ? z - at : len;
        return System.Text.Encoding.Latin1.GetString(d, at, n);
    }

    public void Save()
    {
        var d = new byte[4 + Entries.Count * 0x60];
        BitConverter.GetBytes(Entries.Count).CopyTo(d, 0);
        for (int i = 0; i < Entries.Count; i++)
        {
            var name = System.Text.Encoding.Latin1.GetBytes(Entries[i].Name);
            var addr = System.Text.Encoding.Latin1.GetBytes(Entries[i].Address);
            Array.Copy(name, 0, d, 4 + i * 0x60, Math.Min(name.Length, 0x1f));
            Array.Copy(addr, 0, d, 4 + i * 0x60 + 0x20, Math.Min(addr.Length, 0x3f));
        }
        using var f = Godot.FileAccess.Open(Path, Godot.FileAccess.ModeFlags.Write);
        f?.StoreBuffer(d);
    }

    /// <summary>Puts an entry in before the first whose name's first two letters come after its own; returns where.</summary>
    public int Insert(Entry e)
    {
        string Key(Entry x) => (x.Name.Length > 2 ? x.Name[..2] : x.Name).ToUpperInvariant();
        int i = Entries.FindIndex(x => string.CompareOrdinal(Key(x), Key(e)) > 0);
        if (i < 0) i = Entries.Count;
        Entries.Insert(i, e);
        return i;
    }
}

/// <summary>
/// CONNECT TO SERVER, the address book:
/// bank 36 sprite 29 at (184,38) with its shadow. The list shows three
/// entries 57 px apart from y 223, each its name and, a line below, its
/// address; a click picks one, shown in the NAME and IP boxes, which edit it
/// in place. NEW adds an empty entry and edits its name and then its address;
/// DELETE removes the one picked (the Nitro Pack's art has the button; the
/// base game's has DONE, NEW and CANCEL only, and the port redraws that row
/// as four). DONE gives the one picked, CANCEL none, to the caller: HOST →
/// INTERNET lists its game on it (none: back to the main menu), the internet
/// roster's SERVERS searches the book again. The book is saved either way.
/// The DRIVER box is drawn greyed in the art and does nothing.
/// </summary>
public sealed class AddressBookPopup : ShellScreen
{
    public required ShellFont Font { get; init; }
    public required Action<AddressBook.Entry?> Picked { get; init; }
    public override bool EscapeOpensOptions => false;
    private const int X = 184, Y = 38, Pitch = 57;
    private bool Nitro => Shell.Install.IsNitro;
    private int W = 272, H = 404, Visible = 3, NameY = 105, ListY = 185;
    // id → x0, x1, y0, y1, relative to the popup
    private static readonly Dictionary<int, (int X0, int X1, int Y0, int Y1)> BaseHot = new()
    {
        [0] = (52, 68, 167, 181), [1] = (52, 68, 340, 353), [2] = (48, 220, 183, 338),
        [4] = (54, 227, 104, 123), [5] = (54, 227, 125, 144),
        // the art's DONE (9..78), NEW (101..169) and CANCEL (193..262) redrawn narrower with DELETE (8) among them: BaseButtons
        [7] = (8, 64, 371, 387), [6] = (74, 130, 371, 387), [8] = (140, 196, 371, 387), [3] = (206, 262, 371, 387),
    };
    /// <summary>The base art's own buttons (their hotspots as the art has them), whose words are cut out for the redrawn row.</summary>
    private static readonly Dictionary<string, (int X0, int X1, int Y0, int Y1)> ArtWords = new()
    {
        ["DONE"] = (9, 78, 371, 387), ["NEW"] = (101, 169, 371, 387), ["CANCEL"] = (193, 262, 371, 387),
    };
    /// <summary>The redrawn row: each button's word and the left of its border; all are <see cref="ButtonW"/> wide.</summary>
    private static readonly (string Word, int X0)[] BaseButtons = [("DONE", 8), ("NEW", 74), ("DELETE", 140), ("CANCEL", 206)];
    private const int ButtonW = 57;
    // the Nitro Pack's redrawn popup (bank 36's sprite 28): no DRIVER box, so NAME and IP sit higher; four entries in the list;
    // NEW and DONE over DELETE and CANCEL. Placed by measuring the art (the original's table was not read); 8 is DELETE.
    private static readonly Dictionary<int, (int X0, int X1, int Y0, int Y1)> NitroHot = new()
    {
        [0] = (52, 68, 111, 126), [1] = (52, 68, 340, 353), [2] = (48, 220, 126, 340), [3] = (147, 218, 393, 409),
        [4] = (54, 227, 45, 66), [5] = (54, 227, 67, 87), [6] = (55, 126, 369, 385), [7] = (147, 218, 369, 385), [8] = (55, 126, 393, 409),
    };
    private Dictionary<int, (int X0, int X1, int Y0, int Y1)> Hot => Nitro ? NitroHot : BaseHot;
    private AddressBook _book = null!;
    private int _scroll, _sel = -1;
    private enum Field { None, Name, Address }
    private Field _edit;
    private bool _new;
    private readonly I76.Formats.Input.HoldRepeat _hold = new();

    /// <summary>The longest start of a string that fits a width in a font.</summary>
    public static string Fit(ShellFont f, string s, int w)
    {
        while (s.Length > 0 && f.Measure(s) > w) s = s[..^1];
        return s;
    }

    /// <summary>The longest end that fits (an address being typed shows where the typing is).</summary>
    public static string FitTail(ShellFont f, string s, int w)
    {
        while (s.Length > 0 && f.Measure(s) > w) s = s[1..];
        return s;
    }

    public override void Enter()
    {
        _book = AddressBook.Load();
        var art = Shell.Db.Bank(0x24)[29];
        if (Nitro && art is not null) { W = art.Width; H = art.Height; Visible = 4; NameY = 46; ListY = 129; }
        Draw();
    }

    /// <summary>
    /// The base game's button row as four (ArtButtons): the art's row wiped with the paper between its DONE and NEW, DONE's
    /// face without its word at each new place, the art's own DONE, NEW and CANCEL cut out and centred on theirs, and DELETE,
    /// which the art lacks, lettered as the art letters DONE. Drawn into the frame over the popup (an upscaled popup included),
    /// every time the popup is.
    /// </summary>
    private void DrawBaseButtons(Sprite art)
    {
        byte At(int x, int y) => art.Pixels[Math.Clamp(y, 0, art.Height - 1) * art.Width + Math.Clamp(x, 0, art.Width - 1)];
        // in the frame's coordinates
        byte Px(int x, int y) => At(x - X, y - Y);
        var pal = Frame.Palette;
        var found = new Dictionary<string, (Rect2I Border, ArtButtons.ArtLabel Word)>();
        foreach (var (word, r) in ArtWords)
            if (ArtButtons.Label(Px, pal, new Rect2I(X + r.X0 - 4, Y + r.Y0 - 4, r.X1 - r.X0 + 8, r.Y1 - r.Y0 + 8)) is { } border
                && ArtButtons.Label(Px, pal, border.Box.Grow(-3)) is { } label)
                found[word] = (border.Box, label);
        if (!found.TryGetValue("DONE", out var done)) return;
        // the paper: columns 82..97, between the art's DONE and NEW, repeated along the row
        int y0 = done.Border.Position.Y - Y - 2, y1 = done.Border.End.Y - Y + 2;
        for (int y = y0; y < y1; y++)
            for (int x = 4; x < 268; x++) Frame.Fill(X + x, Y + y, 1, 1, At(82 + (x - 4) % 16, y));
        foreach (var (word, x0) in BaseButtons)
        {
            var dest = new Rect2I(X + x0, done.Border.Position.Y, ButtonW, done.Border.Size.Y);
            ArtButtons.Face(Frame, Px, done.Border, done.Word.Box.Position.X, dest, 3);
            if (found.TryGetValue(word, out var own)) ArtButtons.Word(Frame, Px, own.Word.Box, dest);
            else ArtButtons.Letter(Frame, Shell.Db.Font(0x47), word, done.Word, "DONE", dest);
        }
    }

    private void Draw()
    {
        Frame.Restore(X, Y, W + 6, H + 6);
        var art = Shell.Db.Bank(0x24)[29];
        Frame.Blit(art, X, Y);
        if (!Nitro && art is not null) DrawBaseButtons(art);
        Frame.Fill(X + 6, Y + H, W, 6, 0x0f);
        Frame.Fill(X + W, Y + 6, 6, H, 0x0f);
        if (_sel >= 0 && _sel < _book.Entries.Count)
        {
            var e = _book.Entries[_sel];
            Frame.Text(Font, X + 60, Y + NameY, _edit == Field.Name ? FitTail(Font, e.Name + "_", 165) : Fit(Font, e.Name, 165));
            Frame.Text(Font, X + 60, Y + NameY + 21, _edit == Field.Address ? FitTail(Font, e.Address + "_", 160) : Fit(Font, e.Address, 160));
        }
        for (int k = 0; k < Visible && _scroll + k < _book.Entries.Count; k++)
        {
            var e = _book.Entries[_scroll + k];
            int y = Y + ListY + Pitch * k;
            Frame.Text(Font, X + 50, y, Fit(Font, e.Name, 165));
            Frame.Text(Font, X + 50, y + 21, Fit(Font, e.Address, 160));
        }
    }

    private int HitId(Vector2I p)
    {
        foreach (var (id, r) in Hot)
            if (r.X0 < p.X - X && p.X - X < r.X1 && r.Y0 < p.Y - Y && p.Y - Y < r.Y1) return id;
        return -1;
    }

    private void Scroll(int by) => _scroll = Math.Clamp(_scroll + by, 0, Math.Max(0, _book.Entries.Count - Visible));

    private bool ScrollBy(int by)
    {
        int was = _scroll;
        Scroll(by);
        if (_scroll == was) return false;
        Draw();
        return true;
    }

    public override bool TextEditing => _edit != Field.None;
    public override void StopEditing() { _edit = Field.None; _new = false; Draw(); }

    /// <summary>The buttons and arrows, the entries one by one (a press past the first or last scrolls), the NAME and IP boxes.</summary>
    public override IReadOnlyList<PadRegion> PadRegions
    {
        get
        {
            var l = new List<PadRegion>();
            foreach (var (id, r) in Hot)
            {
                if (id == 2)
                {
                    int n = Math.Min(Visible, _book.Entries.Count - _scroll);
                    for (int k = 0; k < n; k++)
                    {
                        int kk = k;
                        l.Add(PadRegion.Of(X + r.X0 + 1, Y + ListY - 5 + Pitch * k, r.X1 - r.X0 - 2, 38, d => d == NavDir.Up && kk == 0 ? ScrollBy(-1) : d == NavDir.Down && kk == n - 1 ? ScrollBy(1) : false));
                    }
                }
                else if (id is 4 or 5 && _sel < 0) continue;
                else if (id == 8 && _sel < 0) continue;
                else l.Add(PadRegion.Of((X + r.X0, X + r.X1, Y + r.Y0, Y + r.Y1)));
            }
            return l;
        }
    }

    public override void Tick()
    {
        if (_edit != Field.None) { Typing_(); return; }
        if (Shell.Keys.Contains(Key.Escape)) { Close(null); return; }
        // ▲ ▼ repeat every 100 ms while held
        int held = _hold.Id;
        if (_hold.Update(Shell.MouseHeld, HitId(Shell.Mouse), Shell.Delta) is var reps and > 0) { for (int r = 0; r < reps; r++) Scroll(held == 0 ? -1 : 1); Draw(); }
        if (Shell.Wheel != 0) { Scroll(Shell.WheelRows); Draw(); }
        if (!Shell.Clicked) return;
        int id = HitId(Shell.Mouse);
        if (id < 0) return;
        Shell.Sound(82);
        switch (id)
        {
            case 0 or 1: Scroll(id == 0 ? -1 : 1); _hold.Begin(id); break;
            case 2:
                int ry = Shell.Mouse.Y - Y - (ListY - 5), i = _scroll + ry / Pitch;
                if (ry >= 0 && ry % Pitch < 38 && i < _book.Entries.Count) _sel = i;
                break;
            case 3: Close(null); return;
            case 4 when _sel >= 0: _edit = Field.Name; break;
            case 5 when _sel >= 0: _edit = Field.Address; break;
            case 6:
                _sel = _book.Insert(new AddressBook.Entry());
                _scroll = Math.Clamp(_sel - Visible + 1, 0, Math.Max(0, _book.Entries.Count - Visible));
                _edit = Field.Name;
                _new = true;
                break;
            case 8 when _sel >= 0 && _sel < _book.Entries.Count:
                _book.Entries.RemoveAt(_sel);
                _sel = -1;
                Scroll(0);
                break;
            case 7: Close(_sel >= 0 && _sel < _book.Entries.Count ? _book.Entries[_sel] : null); return;
        }
        Draw();
    }

    private void Typing_()
    {
        var e = _book.Entries[_sel];
        bool done = _edit == Field.Name ? Typing.Edit(Shell, ref e.Name, 24) : Typing.Edit(Shell, ref e.Address, 60);
        if (done)
        {
            // NEW edits the name and then the address
            if (_edit == Field.Name && _new) _edit = Field.Address;
            else { _edit = Field.None; _new = false; }
        }
        Draw();
    }

    private void Close(AddressBook.Entry? e)
    {
        _book.Save();
        Frame.Restore(X, Y, W + 6, H + 6);
        Shell.Modal(null);
        Picked(e);
    }
}

/// <summary>
/// A game's password, typed in: the notice's blank sprite (bank 36 sprite 17) with the question on its first line, the line
/// being typed under it and its OK. Typed characters go in through the shell's text entry (<see cref="Typing"/>'s way:
/// <see cref="Shell.Typed"/>, Backspace); Enter or OK takes the text, Esc gives null. A join's text is hidden as it is typed.
/// </summary>
public sealed class PasswordPopup : ShellScreen
{
    public required ShellFont Font { get; init; }
    /// <summary>The game being joined (named in the question), or null for the host form's own password.</summary>
    public string? Game { get; init; }
    public string Text { get; set; } = "";
    public bool Mask { get; init; }
    public required Action<string?> Closed { get; init; }
    public override bool EscapeOpensOptions => false;
    public override bool TextEditing => true;
    private static readonly Rect2I Ok = new(119, 61, 42, 16);
    private const int X = 179, Y = 194;

    public override void Enter() => Draw();

    private void Draw()
    {
        var s = Shell.Db.Bank(0x24)[17];
        Frame.Restore(X, Y, 300, 110);
        Frame.Blit(s, X, Y);
        int w = s?.Width ?? 282, h = s?.Height ?? 93;
        Frame.Fill(X + 6, Y + h, w, 6, 0x0f);
        Frame.Fill(X + w, Y + 6, 6, h, 0x0f);
        string ask = Game is { } g ? $"Password for {g}" : "Game password";
        Frame.Text(Font, X + w / 2, Y + 14, "~" + AddressBookPopup.Fit(Font, ask, w - 24));
        string shown = (Mask ? new string('*', Text.Length) : Text) + "_";
        Frame.Text(Font, X + w / 2, Y + 36, "~" + AddressBookPopup.FitTail(Font, shown, w - 40));
    }

    public override IReadOnlyList<PadRegion> PadRegions => [PadRegion.Of(new Rect2I(X + Ok.Position.X, Y + Ok.Position.Y, Ok.Size))];

    public override void Tick()
    {
        if (Shell.Keys.Contains(Key.Escape)) { Close(null); return; }
        string was = Text;
        foreach (char c in Shell.Typed) if (Text.Length < I76.Net.Protocol.MaxPassword) Text += c;
        if (Shell.Keys.Contains(Key.Backspace) && Text.Length > 0) Text = Text[..^1];
        if (Shell.Keys.Contains(Key.Enter) || Shell.Keys.Contains(Key.KpEnter)
            || Shell.Clicked && new Rect2I(X + Ok.Position.X, Y + Ok.Position.Y, Ok.Size).HasPoint(Shell.Mouse)) { Close(Text); return; }
        if (Text != was) Draw();
    }

    private void Close(string? pw)
    {
        Shell.Sound(82);
        Frame.Restore(X, Y, 300, 110);
        Shell.Modal(null);
        Closed(pw);
    }
}

/// <summary>
/// The Nitro host form's WEAPON RESTRICTIONS (docs/hidden-content.md §4): bank 30 sprite
/// 43 at (34, 25), the boxes of the original's 43 rows at the table's grid (read from the install's Nitro shell DLL), a check the
/// form font's "x" at (x+1, y−2); DONE (400..471, 400..416) keeps the edits, CANCEL (479..550) or Esc drops them. The port adds
/// boxes and names of its own for the complete hidden weapons in the art's empty lines (<see cref="HiddenContent.ChecklistRows"/>).
/// </summary>
public sealed class RestrictionsPopup : ShellScreen
{
    public required ShellFont Font { get; init; }
    public required RestrictionChecklist List { get; init; }
    /// <summary>The rows from the original's table; the ones after are the port's.</summary>
    public required int Original { get; init; }
    public required int[] ColumnX { get; init; }
    public required int[] RowY { get; init; }
    /// <summary>DONE gives the boxes' list, CANCEL null.</summary>
    public required Action<WeaponRestrictions?> Closed { get; init; }
    public override bool EscapeOpensOptions => false;
    private const int X = 34, Y = 25, W = 573, H = 430, Box = 12;
    private static readonly (int X0, int X1, int Y0, int Y1) Done = (400, 471, 400, 416), Cancel = (479, 550, 400, 416);
    private bool[] _was = [];

    public override void Enter()
    {
        _was = (bool[])List.Checked.Clone();
        Draw();
    }

    private Vector2I BoxAt(RestrictionRow r) => new(ColumnX[Math.Clamp(r.Column, 0, ColumnX.Length - 1)], RowY[Math.Clamp(r.Row, 0, RowY.Length - 1)]);

    private void Draw()
    {
        var art = Shell.Db.BankNative(30)[43];
        Frame.Restore(X, Y, W + 6, H + 6);
        Frame.Blit(art, X, Y);
        Frame.Fill(X + 6, Y + H, W, 6, 0x0f);
        Frame.Fill(X + W, Y + 6, 6, H, 0x0f);
        // the port's rows: a box like the art's (its ink taken from the first box of the art) and the weapon's name on the right of
        // its row's first box, where the art prints its own names
        byte ink = art is { } a && ColumnX.Length > 1 && RowY.Length > 1 && a.Width > ColumnX[1] && a.Height > RowY[1] ? a.Pixels[RowY[1] * a.Width + ColumnX[1]] : (byte)0x0f;
        var labelled = new HashSet<int>();
        for (int i = Original; i < List.Rows.Count; i++)
        {
            var r = List.Rows[i];
            var p = BoxAt(r) + new Vector2I(X, Y);
            Frame.Fill(p.X, p.Y, Box, 1, ink); Frame.Fill(p.X, p.Y + Box - 1, Box, 1, ink);
            Frame.Fill(p.X, p.Y, 1, Box, ink); Frame.Fill(p.X + Box - 1, p.Y, 1, Box, ink);
            if (labelled.Add(r.Row * 2 + (r.Column >= 3 ? 1 : 0)))
            {
                if (art is { } sp && Frame.HasHdSprite?.Invoke(sp) != true)
                {
                    // the art's own face (RestrictionLabels; where an HD Menu Pack replaces the art, the form font in the text layer as before), at the pen the art's names start at: after its last box in the row
                    // (one with a turret box beside it, or a lone turret box: 115 left, 398 right; a lone one: 85.5 left, 367 right), its caps 1 row under the box's top
                    // in the art's upper left, 2 elsewhere
                    bool right = r.Column >= 3, both = List.Rows.Any(o => o.Row == r.Row && o.Column == r.Column + 1);
                    RestrictionLabels.Draw(Frame, sp, X, Y, right ? (both || r.Column == 5 ? 398f : 367f) : (both || r.Column == 2 ? 115f : 85.5f), RowY[r.Row] + (!right && r.Row <= 5 ? 1 : 2), r.Name);
                }
                else Frame.Text(Font, X + (r.Column >= 3 ? 398 : 115), p.Y - 1, r.Name);
            }
        }
        for (int i = 0; i < List.Rows.Count; i++)
            if (List.Checked[i]) { var p = BoxAt(List.Rows[i]); Frame.Text(Font, X + p.X + 1, Y + p.Y - 2, "x"); }
    }

    private static bool In((int X0, int X1, int Y0, int Y1) r, Vector2I p) => r.X0 < p.X && p.X < r.X1 && r.Y0 < p.Y && p.Y < r.Y1;

    public override IReadOnlyList<PadRegion> PadRegions =>
        [.. List.Rows.Select(r => PadRegion.Of((X + BoxAt(r).X, X + BoxAt(r).X + Box, Y + BoxAt(r).Y, Y + BoxAt(r).Y + Box))),
         PadRegion.Of((X + Done.X0, X + Done.X1, Y + Done.Y0, Y + Done.Y1)), PadRegion.Of((X + Cancel.X0, X + Cancel.X1, Y + Cancel.Y0, Y + Cancel.Y1))];

    public override void Tick()
    {
        if (Shell.Keys.Contains(Key.Escape)) { Close(false); return; }
        if (!Shell.Clicked) return;
        var p = Shell.Mouse - new Vector2I(X, Y);
        // A box is its 12x12 square (the hot area 39..387, 66..382 holds them all)
        for (int i = 0; i < List.Rows.Count; i++)
        {
            var b = BoxAt(List.Rows[i]);
            if (b.X <= p.X && p.X <= b.X + Box && b.Y <= p.Y && p.Y <= b.Y + Box) { Shell.Sound(82); List.Toggle(i); Draw(); return; }
        }
        if (In(Done, p)) Close(true);
        else if (In(Cancel, p)) Close(false);
    }

    private void Close(bool done)
    {
        Shell.Sound(82);
        if (!done) Array.Copy(_was, List.Checked, _was.Length);
        Frame.Restore(X, Y, W + 6, H + 6);
        Shell.Modal(null);
        Closed(done ? List.Build() : null);
    }
}
