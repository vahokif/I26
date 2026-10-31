using Godot;
using I76.Formats.Input;
using I76.Net;
using I76.Net.Hud;
using I76.Sim.Missions;

namespace I76.Game.Net;

/// <summary>
/// The network melee's own readouts, over the game's HUD: the ping, the goal line, a feed of the console's lines (kills,
/// players coming and going, chat, in the original's words), the score window while its key is held (show_player_scores
/// ', show_team_scores ;), the host's prompts (an invite-only join, a boot), the dead
/// player's way back (RAISE_DEAD, Reconfigure), each machine's own end line and the final standings. This class keeps
/// the keys and the state; each frame it gathers a <see cref="NetHudFrame"/> for the look in use to draw: the 1997
/// look's <see cref="NetHudClassic"/> (the original's windows at their places, on the whole wide frame) or the modern
/// look's <see cref="NetHudModern"/> (the port's panels; docs/deviations.md).
/// </summary>
public partial class NetHud : CanvasLayer
{
    public required MeleeClient Client { get; init; }
    public required ClientWorld World { get; init; }
    /// <summary>The own car's prediction: with I76_NET_DEBUG its last error and correction go under the ping.</summary>
    public OwnCarPredictor? Predictor { get; init; }
    private readonly bool _debug = System.Environment.GetEnvironmentVariable("I76_NET_DEBUG") is { Length: > 0 } and not "0";
    /// <summary>A listen server's listing on anet servers ("Listed on ...", "Not listed: ..."), null when not listed anywhere.</summary>
    public Func<string?>? HostStatus { get; init; }
    /// <summary>This game is a listen server's own: Enter starts the next round without the wait, and the host's prompts and boots are its.</summary>
    public bool Host { get; set; }
    /// <summary>The listen host's server, for its prompts (an invite-only join) and its boots; null on a client.</summary>
    public I76.Server.NetMeleeServer? Server { get; set; }
    /// <summary>The Nitro Pack's words for the console's lines ("*** Player %s has joined the game", the goal lines).</summary>
    public bool Nitro { get; init; }
    /// <summary>The goal was reached and this machine is the winner (or the time ran out): the game shows the death-orbit camera.</summary>
    public Action? Won { get; init; }
    private string? _hostStatus;
    /// <summary>A co-op mission's cut-scene holds the view: the readouts give way to it, as the game's own HUD does.</summary>
    public Func<bool>? Cutscene { get; init; }
    /// <summary>Whether the chat key may open the entry now (not over a menu or a picture).</summary>
    public Func<bool>? CanChat { get; init; }
    /// <summary>The name of the key that brings a dead player back (RAISE_DEAD), for the banner.</summary>
    public string RaiseKey => GameKeys.KeyOf((int)GameKey.RaiseDead) ?? "Space";
    /// <summary>A co-op story mission's game (the accept says so): no scores or goal, an automatic respawn, the mission's own end.</summary>
    private bool Coop => Client.Accept?.Coop is not null;
    /// <summary>The modern look is in use (else the 1997 look's): asked each frame, so F12 switches the HUD with the rest.</summary>
    public Func<bool>? Modern { get; init; }
    private readonly I76.Formats.Shell.ChatEntry _chat = new() { MaxLength = Protocol.MaxChat };
    /// <summary>The install's Lee_____.ttf (the original's console face), or null: the feed and the entry then use the default font.</summary>
    public string? LeeFont { get; init; }
    private double? _nextRound;
    private readonly HudFeed _feed = new();
    private readonly Dictionary<int, string> _slotNames = [];
    /// <summary>Seats whose leaving was told: a host that quits is told by its own server and again by the one that took over (host migration).</summary>
    private readonly HashSet<int> _gone = [];
    private double _t;
    private int _boardPage;
    private double? _deadAt;
    private (string? Name, int Team, bool Self)? _killer;
    private IReadOnlyList<(byte Slot, int Score)>? _final;
    private NetHudFrame.EndInfo? _end;
    /// <summary>The goal was reached or the time ran out (winner): no respawn, no game keys of group B.</summary>
    public bool Decided { get; private set; }
    /// <summary>The boot asked about (BOOT_n, "Boot %s from the game?"), until Ctrl+Y or Ctrl+N.</summary>
    private (byte Slot, string Name)? _boot;
    private string? _reconfigured;
    /// <summary>The player a migration named the new host (the clients know no other way); -1 when unknown.</summary>
    private int _hostSlot = -1;

    /// <summary>
    /// This player's skip is the one a co-op cut-scene takes (the server's: <c>CoopMode.Skipper</c>): the listen host's seat, the one that took
    /// over after a migration, else the lowest. Everyone else's Esc opens the menu in a cut-scene.
    /// </summary>
    public bool CanSkip(int mySlot)
    {
        if (Server is not null) return true;
        if (_hostSlot >= 0) return _hostSlot == mySlot;
        var slots = Client.Scores?.Players;
        return slots is null || slots.Count == 0 || slots.Min(p => p.Slot) >= mySlot;
    }
    /// <summary>The CB radio (voice chat): the key, the microphone and the radio's sound.</summary>
    private CbVoice? _cb;
    /// <summary>This player is on the air (the push-to-talk key is held): the HUD raises the CB handset.</summary>
    public bool CbTransmitting => _cb?.Transmitting == true;
    private NetHudClassic _classic = null!;
    private NetHudModern _modern = null!;
    private readonly NetHudDemo? _demo = NetHudDemo.FromEnvironment();

    public override void _Ready()
    {
        Layer = 20;
        AddChild(_classic = new NetHudClassic { LeeFont = LeeFont, Name = "Classic" });
        AddChild(_modern = new NetHudModern { Name = "Modern" });
        AddChild(_cb = new CbVoice
        {
            Client = Client, Name = "CbVoice", Say = s => Say(s, FeedKind.Own), CanTalk = () => CanChat?.Invoke() != false && !_chat.Active,
            NameOf = SlotName, TeamOf = SlotTeam,
            IsHost = () => Server is not null, HostMute = (who, mute) => Server?.CbMute(who, mute), HostMutedList = () => Server?.CbMuted ?? [],
            Players = () => Client.Scores?.Players.Where(p => p.Slot != Client.Accept?.Slot).Select(p => ((int)p.Slot, p.Name)) ?? [],
        });
        World.News += OnNews;
        World.Renewed += (m, respawn) => { if (m == World.Own && respawn) { _deadAt = null; _reconfigured = null; _killer = null; } };
    }

    private bool IsModern => Modern?.Invoke() ?? true;

    /// <summary>
    /// The chat entry: the chat key (CHAT_SENDTOALL, Backspace unless gamekey.map rebinds it) opens it; while it is open it takes
    /// every key press before the game sees one, and the car's controls are suspended.
    /// </summary>
    public override void _Input(InputEvent e)
    {
        if (e is not InputEventKey { Pressed: true } k) return;
        if (!_chat.Active)
        {
            if (!k.Echo && k.CtrlPressed && k.Keycode is Key.Pagedown or Key.Pageup)
            {
                _boardPage = Math.Max(0, _boardPage + (k.Keycode == Key.Pagedown ? 1 : -1));
                GetViewport().SetInputAsHandled();
                return;
            }
            if (!k.Echo && GameKeys.CommandOf(k) == GameKey.ChatToAll && Client.State == ClientState.Joined && CanChat?.Invoke() != false)
            {
                _chat.Open();
                Controls.Suspended = true;
                GetViewport().SetInputAsHandled();
            }
            return;
        }
        GetViewport().SetInputAsHandled();
        var key = k.Keycode switch
        {
            Key.Enter or Key.KpEnter => I76.Formats.Shell.ChatKey.Enter, Key.Escape => I76.Formats.Shell.ChatKey.Escape,
            Key.Backspace => I76.Formats.Shell.ChatKey.Backspace, _ => I76.Formats.Shell.ChatKey.Other,
        };
        if (k.Echo && key is I76.Formats.Shell.ChatKey.Enter or I76.Formats.Shell.ChatKey.Escape) return;
        char typed = I76.Formats.Shell.TextEntry.Typed((int)k.Unicode, (int)k.KeyLabel, k.ShiftPressed, k.CtrlPressed || k.AltPressed || k.MetaPressed);
        if (_chat.Press(key, typed) == I76.Formats.Shell.ChatResult.Sent && _cb?.Command(_chat.Sent!) != true) Client.SendChat(_chat.Sent!);
        if (!_chat.Active) Controls.Suspended = false;
    }

    public override void _ExitTree() => Controls.Suspended = false;

    /// <summary>The CTF teams' colours (Red Dawn, Gang Green, Purple Reign, Black Plague; the last drawn light so it shows).</summary>
    public static Color TeamColour(int team) => team switch
    {
        1 => new Color(1f, 0.15f, 0.15f),
        2 => new Color(0.2f, 0.9f, 0.2f),
        3 => new Color(0.7f, 0.3f, 1f),
        _ => new Color(0.85f, 0.85f, 0.85f),
    };

    private MeleeKind Kind => Client.Accept?.Kind ?? MeleeKind.Combat;
    private MeleeGoal Goal => Client.Accept is not { } a ? MeleeGoal.Points
        : a.Goal != MeleeGoal.None ? a.Goal : a.Kind == MeleeKind.Race ? MeleeGoal.Laps : a.Kind == MeleeKind.Ctf ? MeleeGoal.Captures : MeleeGoal.Points;
    private string TeamName(int team) => MeleeGame.TeamName(Kind, team);

    /// <summary>
    /// The goal line: "N of M Laps Completed", "Flag
    /// Captures", "Kills" or "Points" with the leader's count, or, when the
    /// game has a time limit, only the time left ("00H:00M:00S Minutes Remaining").
    /// </summary>
    public static string GoalLine(MeleeGoal goal, int value, float timeLimit, float clock, bool hasScores, int lead)
    {
        if (timeLimit > 0 && hasScores)
        {
            int t = (int)Math.Max(0, timeLimit - clock);
            return $"{t / 3600:00}H:{t / 60 % 60:00}M:{t % 60:00}S Minutes Remaining";
        }
        if (!hasScores) return "";
        string what = goal switch { MeleeGoal.Laps => "Laps Completed", MeleeGoal.Captures => "Flag Captures", MeleeGoal.Kills => "Kills", _ => "Points" };
        // no limit: laps and captures still show the leader's count (the original shows nothing)
        if (value <= 0) return goal is MeleeGoal.Laps or MeleeGoal.Captures ? $"{lead} {what}" : "";
        return $"{lead} of {value} {what}";
    }

    private string SlotName(int slot) =>
        Client.Scores?.Players.FirstOrDefault(p => p.Slot == slot)?.Name ?? _slotNames.GetValueOrDefault(slot) ?? $"player {slot + 1}";

    private int SlotTeam(int slot) => Client.Scores?.Players.FirstOrDefault(p => p.Slot == slot)?.Team ?? 0;

    private string CarName(int serial)
    {
        if (World.Cars.GetValueOrDefault(serial)?.Info is not { } i) return "?";
        return i.Owner >= 0 ? (i.Name.Length > 0 ? i.Name : SlotName(i.Owner))
            : I76.Formats.Shell.MeleeDrivers.ForFillEntity(i.Name) ?? $"AI {System.IO.Path.GetFileNameWithoutExtension(i.Def)}";
    }

    private int CarTeam(int serial) => World.Cars.GetValueOrDefault(serial)?.Info.Team ?? 0;

    // the feed's rate cap (HudFeed): at most HudFeed.Burst joins, leaves and kills a HudFeed.Window seconds show as lines
    public const int FeedBurst = HudFeed.Burst;
    public const double FeedWindow = HudFeed.Window;

    /// <summary>A console line of the server's or the port's own (no parts): the "***" lines, the reconfigure answers.</summary>
    public void Say(string s, FeedKind kind = FeedKind.Notice)
    {
        GD.Print($"[melee] {s}");
        _feed.Add(new FeedEntry(kind, s, _t));
    }

    private void Line(FeedEntry e)
    {
        GD.Print($"[melee] {e.Text}");
        _feed.Add(e);
    }

    private void OnNews(NetEvent e)
    {
        bool teams = Client.Accept?.TeamPlay == true;
        int mySlot = Client.Accept?.Slot ?? -1;
        switch (e)
        {
            case JoinedEvent j:
                _slotNames[j.Slot] = j.Name;
                _gone.Remove(j.Slot);
                Line(new FeedEntry(FeedKind.Join, NetLines.Join(Nitro, teams, j.Name, TeamName(j.Team)), _t) { Who = j.Name, WhoTeam = teams ? j.Team : 0 });
                break;
            case LeftEvent l:
                if (_gone.Add(l.Slot))
                    Line(new FeedEntry(FeedKind.Leave, NetLines.Leave(Nitro, SlotName(l.Slot)), _t) { Who = SlotName(l.Slot), WhoTeam = teams ? SlotTeam(l.Slot) : 0 });
                if (_boot?.Slot == l.Slot) _boot = null;
                break;
            case KillEvent k:
            {
                string victim = CarName(k.Victim);
                string? killer = k.Attacker == 0 ? null : CarName(k.Attacker);
                Line(new FeedEntry(FeedKind.Kill, NetLines.Kill(victim, killer), _t)
                {
                    Who = killer, What = victim, WhoTeam = teams && k.Attacker != 0 ? CarTeam(k.Attacker) : 0, WhatTeam = teams ? CarTeam(k.Victim) : 0,
                    ByMe = k.Attacker != 0 && k.Attacker == World.OwnSerial, OnMe = k.Victim == World.OwnSerial,
                });
                if (k.Victim == World.OwnSerial)
                {
                    _deadAt = _t;
                    _killer = (killer, teams && k.Attacker != 0 ? CarTeam(k.Attacker) : 0, k.Attacker == k.Victim);
                }
                break;
            }
            case KilledEvent d when d.Car == World.OwnSerial: _deadAt ??= _t; break;
            case RenewedEvent r when r.Car == World.OwnSerial && !r.Respawn: Say("regenerated  +400", FeedKind.Own); break;
            case ChatEvent c when c.From < 0 && c.Text.StartsWith("***"):
                // the console's lines (flags, the time limit, a migration's new host)
                Say(c.Text);
                if (Client.Scores?.Players.FirstOrDefault(p => c.Text == NetLines.NewHost(p.Name)) is { } h) _hostSlot = h.Slot;
                else if (c.Text == NetLines.NewHost(null)) _hostSlot = mySlot;
                break;
            case ChatEvent c:
            {
                string from = c.From < 0 ? "Server" : SlotName(c.From);
                Line(new FeedEntry(FeedKind.Chat, NetLines.Chat(from, c.Text), _t)
                    { Who = from, What = c.Text, WhoTeam = c.From >= 0 && teams ? SlotTeam(c.From) : 0, ByMe = c.From == mySlot });
                break;
            }
            case EndEvent end:
            {
                Decided = true;
                int myTeam = Client.Scores?.Players.FirstOrDefault(p => p.Slot == mySlot)?.Team ?? 0;
                string winner = end.Winner >= 0 ? SlotName(end.Winner) : "?";
                var line = NetLines.End(end, Nitro, Goal, teams, mySlot, myTeam, winner, TeamName(end.Team), out bool won);
                if (line is not null) Say(line);
                _end = new NetHudFrame.EndInfo(line, won, end.TimeUp, teams ? TeamName(end.Team) : end.Winner >= 0 ? winner : null,
                    teams ? end.Team : end.Winner >= 0 ? SlotTeam(end.Winner) : 0, end.Count, _t);
                if (won) Won?.Invoke();
                break;
            }
            // a co-op mission's end is the game's own (the outro, the end menu): no standings over it
            case GameOverEvent when Coop: Decided = true; break;
            case MissionEndEvent me: Decided = true; GD.Print($"[co-op] the mission is {(me.Won ? "won" : "failed")}{(me.Text.Length > 0 ? ": " + me.Text : "")}"); break;
            // the script's presentation is the game's HUD's (the radio, the notepad); the log says what came
            case RadioEvent r: GD.Print($"[co-op] radio: {r.Sound}"); break;
            case ObjectiveEvent o: GD.Print($"[co-op] objective {o.Number}: {Objectives.Describe(o.Flags)}"); break;
            case MovieEvent mv: GD.Print($"[co-op] movie: {mv.Name}"); break;
            case GameOverEvent g: _final = g.Standings; if (g.NextRoundSeconds > 0) _nextRound = _t + g.NextRoundSeconds; break;
        }
    }

    /// <summary>The server's answer to a Reconfigure, for the feed and the banner.</summary>
    public void Reconfigured(ReconfigureAnswer a, string? carName)
    {
        if (a.Taken) _reconfigured = carName ?? a.Text;
        Say(a.Taken ? $"You will come back in the {carName ?? a.Text}." : $"New car refused: {a.Text}", FeedKind.Own);
    }

    /// <summary>The player is dead and the game undecided: the way back is open (RAISE_DEAD once the 5 s are up, Reconfigure any time).</summary>
    public bool CanComeBack => _deadAt is not null && !Decided && _final is null && !Coop;

    // ---- the host's keys: INVITE_ACCEPT / DECLINE / SKIP and BOOT_n (the original's game-key dispatch cases 0x29..0x32) ----

    /// <summary>
    /// A host key (the game passes those of group B: a living player, no winner yet). Ctrl+Y confirms a boot asked about,
    /// else lets the join asked about in; Ctrl+N cancels the boot, else turns the join away;
    /// Ctrl+H asks about the next join first; BOOT_n (Ctrl+1..7, and the port's Ctrl+8 and Ctrl+9: <paramref name="bootRow"/>)
    /// asks about the n-th numbered row of the score window's page, as the look in use numbers it. Returns whether it acted.
    /// </summary>
    public bool HostKey(GameKey g, int bootRow = 0)
    {
        if (Server is null) return false;
        switch (g)
        {
            case GameKey.InviteAccept:
                if (_boot is { } b) { _boot = null; if (Server.Kick(b.Slot) is { } why) Say(why); return true; }
                return Server.AnswerInvite(I76.Server.InviteAnswer.Accept) is not null;
            case GameKey.InviteDecline:
                if (_boot is not null) { _boot = null; return true; }
                return Server.AnswerInvite(I76.Server.InviteAnswer.Decline) is not null;
            case GameKey.InviteSkip:
                return _boot is null && Server.AnswerInvite(I76.Server.InviteAnswer.Skip) is not null;
        }
        if (bootRow <= 0) return false;
        var numbered = PlayersPage()?.Numbered ?? [];
        // A number past the list takes the prompt away
        _boot = bootRow <= ScoreTable.HostRows && bootRow <= numbered.Count ? numbered[bootRow - 1] : null;
        return true;
    }

    private ScoreContext? Context(bool modern)
    {
        if (Client.Accept is not { } a) return null;
        return new ScoreContext(a.Kind, a.TeamPlay, a.Slot, Server is not null, modern || a.TeamPlay ? ScoreOrder.Ranked : ScoreOrder.Slot,
            modern ? ScoreTable.ModernRows : ScoreTable.ClassicRows) { HostSlot = Server is not null ? a.Slot : _hostSlot };
    }

    /// <summary>The players' window as the look in use draws it (table, paged): the page clamped to the pages there are.</summary>
    private ScoreView? PlayersPage()
    {
        if (Client.Scores is not { } sc || Context(IsModern) is not { } c) return null;
        var v = ScoreTable.Players(sc.Players, c, _boardPage, TeamName);
        _boardPage = v.Page;
        return v;
    }

    private bool _boardSeen;

    /// <summary>The standings' pad button: Back (View), on a pad with the standard layout. In a network game the map it opens in a mission is shut, so the button is free.</summary>
    private static bool PadBackHeld()
    {
        if (Controls.Suspended || !Controls.Focused() || Controls.PadButtonBound(JoyButton.Back)) return false;
        foreach (var d in Godot.Input.GetConnectedJoypads())
            if (GodotPads.IsStandard(d) && Godot.Input.IsJoyButtonPressed(d, JoyButton.Back)) return true;
        return false;
    }

    /// <summary>A binding's name as a keycap shows it: the first key of the first term, in the symbols the cap would carry.</summary>
    public static string KeyLabel(string binding)
    {
        var name = binding.Split(new[] { ' ', '\t', '+', ',' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (name is null || name == Controls.None) return "Tab";
        return name switch { "Quote" => "'", "SemiColon" => ";", "BackQuote" => "`", "Grave" => "`", "Comma" => ",", "Period" => ".", _ => name };
    }

    private static bool Held(string command, Key fallback) =>
        Controls.Down(command) || (Controls.Binding(command) is "" or Controls.None && !Controls.Suspended && Godot.Input.IsPhysicalKeyPressed(fallback));

    public override void _Process(double delta)
    {
        _t += delta;
        _feed.Expire(_t);
        bool modern = IsModern;
        if (_demo is not null) _demo.Classic = !modern;
        var f = _demo?.Frame(_t, Server is not null || Host) ?? Gather();
        bool cut = Cutscene?.Invoke() == true && !_chat.Active;
        _classic.Visible = !modern && !cut;
        _modern.Visible = modern && !cut;
        if (modern) _modern.Show(f); else _classic.Show(f);
    }

    /// <summary>This frame's readouts from the client and the HUD's own state.</summary>
    private NetHudFrame Gather()
    {
        var f = new NetHudFrame { T = _t, Feed = _feed, Ping = Client.Rtt, Kind = Kind, Goal = Goal, Host = Host, Pad = Godot.Input.GetConnectedJoypads().Count > 0, Coop = Coop };
        var a = Client.Accept;
        var sc = Client.Scores;
        if (Client.State != ClientState.Joined) f.Connection = $"{Client.State}{(Client.Reason is { } r ? ": " + r : "")}";
        if (_debug)
            f.Debug = Predictor is { } p
                ? $"prediction: off {p.LastError * 100:0.0} cm, fix {p.LastCorrection * 100:0.0} cm, offset {p.Offset * 100:0.0} cm, {p.Corrections}/{p.Compared} off, {p.Snaps} snaps, ahead {Client.InputTick - Client.AckedInput}"
                : "prediction: off";
        if (HostStatus?.Invoke() is { } hs)
        {
            if (hs != _hostStatus) { _hostStatus = hs; GD.Print($"[melee] {hs}"); }
            f.HostStatus = hs;
        }
        if (a is not null)
        {
            f.TeamPlay = a.TeamPlay;
            f.MySlot = a.Slot;
            f.TimeLimit = a.TimeLimit;
            f.GoalValue = a.GoalValue > 0 ? a.GoalValue : f.Goal == MeleeGoal.Points ? a.MaxScore : 0;
        }
        if (sc is not null && !f.Coop)
        {
            f.HasScores = true;
            f.Clock = sc.Clock;
            var me = sc.Players.FirstOrDefault(p => p.Slot == f.MySlot);
            f.MyTeam = me?.Team ?? 0;
            var rows = sc.Players.Select(p => ((int)p.Team, p.Score, p.Kills)).ToList();
            f.Leader = GoalCount.Leader(f.Goal, f.TeamPlay, rows);
            f.Mine = me is null ? 0 : f.TeamPlay ? GoalCount.Leader(f.Goal, true, rows.Where(r => r.Item1 == me.Team)) : f.Goal == MeleeGoal.Points ? me.Score : me.Kills;
            var counts = f.TeamPlay ? ScoreTable.Teams(sc.Players, f.Kind, TeamName).Select(t => ScoreTable.Metric(f.Kind, t.Score, t.Kills)).ToList()
                : sc.Players.Select(p => ScoreTable.Metric(f.Kind, p.Score, p.Kills)).ToList();
            int mine = me is null ? 0 : f.TeamPlay
                ? ScoreTable.Teams(sc.Players, f.Kind, TeamName).Where(t => t.Team == me.Team).Select(t => ScoreTable.Metric(f.Kind, t.Score, t.Kills)).FirstOrDefault()
                : ScoreTable.Metric(f.Kind, me.Score, me.Kills);
            f.Field = counts.Count;
            f.Place = ScoreTable.Place(mine, counts);
        }
        f.GoalLine = GoalLine(f.Goal, f.GoalValue, f.TimeLimit, f.Clock, f.HasScores, f.Leader);
        if (f.Kind == MeleeKind.Ctf)
            foreach (var fl in World.Flags)
                f.Flags.Add((fl.Team, fl.State, fl.State == 4 && fl.Carrier != 0 ? CarName(fl.Carrier) : null,
                    sc?.Players.Where(p => p.Team == fl.Team).Select(p => p.Kills).DefaultIfEmpty(0).Max() ?? 0));
        // the window only while its key is held: Tab, or a pad's Back; one view, the teams' totals heading
        // their players in team play (docs/deviations.md, Standings)
        bool players = Held("show_player_scores", Key.Tab) || PadBackHeld();
        if (players) { f.Board = PlayersPage(); _boardSeen = true; }
        f.ScoresKey = KeyLabel(Controls.Binding("show_player_scores"));
        f.ScoresHint = !_boardSeen && _t < 90;
        // the other keys are listed in the standings, not over the game (players found the HUD too busy)
        f.ChatKey = GameKeys.KeyOf((int)GameKey.ChatToAll) ?? "Backspace";
        f.NewCarKey = GameKeys.KeyOf((int)GameKey.SelfDestruct) ?? "Ctrl+Delete";
        f.ChatHint = CanChat?.Invoke() != false;
        f.NewCarHint = !f.Coop;
        f.CbTx = _cb?.Transmitting == true;
        f.CbMuted = _cb is { Off: false } && Client.CbMuted;
        if (_cb is { Enabled: true, Off: false } cb)
        {
            f.CbTalking = [.. cb.Talking];
            f.CbKey = Controls.Binding("POETRY") is { Length: > 0 } pk && pk != BindingTable.None ? pk : "C";
            f.CbHint = true;
        }
        f.ChatOpen = _chat.Active;
        f.ChatText = _chat.Text;
        // the host's prompts: a boot asked about first, else the join waiting longest
        if (_boot is { } bt) f.Prompt = new(NetHudFrame.PromptKind.Boot, bt.Name);
        else if (Server?.PendingInvite is { } inv) f.Prompt = new(NetHudFrame.PromptKind.Invite, inv.Name);
        f.End = _end;
        if (_final is { } fin)
        {
            var ranks = ScoreTable.Ranks(fin.Select(x => x.Score).ToList());
            f.Final = new NetHudFrame.FinalInfo(fin.Select((x, i) =>
            {
                var row = sc?.Players.FirstOrDefault(p => p.Slot == x.Slot);
                return new NetHudFrame.Standing(ranks[i], SlotName(x.Slot), row?.Team ?? 0, x.Score, row?.Kills ?? 0, row?.Deaths ?? 0, x.Slot == f.MySlot);
            }).ToList(), _nextRound is { } nr ? Math.Max(0, nr - _t) : null);
        }
        else if (_deadAt is { } d && a is not null && !Decided)
            f.Death = new NetHudFrame.DeathInfo(_killer?.Name, _killer?.Team ?? 0, _killer?.Self ?? false, a.RespawnDelay - (_t - d), a.RespawnDelay, RaiseKey, _reconfigured);
        return f;
    }
}
