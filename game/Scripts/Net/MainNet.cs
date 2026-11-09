using Godot;
using I76.Game.Net;
using I76.Game.Sim;
using I76.Net;
using I76.Server;

namespace I76.Game.Boot;

/// <summary>
/// The network melee in the game (docs/multiplayer.md): --connect joins a
/// server, --host-listen runs one in this process and joins it. The arena is
/// built as for any mission; the cars are a <see cref="ClientWorld"/>'s,
/// written from the server's snapshots, and drawn by the same CarNode,
/// CombatView, DamageView, cockpit and HUD as a local game, with the
/// SimDriver sending the player's input instead of ticking. The other cars
/// are drawn 100 ms behind the server; the player's own is predicted
/// (<see cref="OwnCarPredictor"/>; I76_NET_PREDICT=0 draws it behind too).
/// </summary>
public partial class Main
{
    private NetSession? _net;
    /// <summary>The network session handed on to the next round's scene (the connection, and a listen server, outlive the reload).</summary>
    private static NetSession? _carriedNet;
    /// <summary>The standings pause the server announced with the game over, seconds (0: the server ends here).</summary>
    private int _netNextRound;
    private int _roundsAtBuild;
    /// <summary>The accept this scene joined with (the client's may already be a newer round's, or none, by the time the arena is built).</summary>
    private JoinAccept? _netAccept;
    private ClientWorld? _netWorld;
    private NetHud? _netHud;
    /// <summary>This machine won (or the time ran out): the death-orbit camera round its own car (the original's start-up: camera 0xfefe, mode 8).</summary>
    private bool _netWinOrbit;
    /// <summary>The Reconfigure screen while it is up (I26's: a dead player picks the car to come back in).</summary>
    private I76.Game.Shell.Shell? _reconfig;
    /// <summary>The car file Reconfigure last got taken, kept for the next opening (by name: the lists' indexes move with Hidden Weapons &amp; Cars).</summary>
    private static string? _reconfigPick;
    /// <summary>The car Reconfigure last sent, until the server answers (taken, it becomes <see cref="_reconfigPick"/>).</summary>
    private static string? _reconfigAsked;

    /// <summary>Hosts (with --host-listen) and joins; once accepted, the arena and car are the server's. False if it failed (the game quits).</summary>
    private async System.Threading.Tasks.Task<bool> StartNetwork(I76.Game.Ui.LoadingScreen? loading = null)
    {
        // the next round of a melee that carries on (docs/multiplayer.md "Rounds"): the connection, and a listen server, live on
        // through the scene's reload; the server's offer or accept for the new round is what the wait below is for
        bool carried = _carriedNet is not null;
        // a new game (not the next round of this one) opens Reconfigure on the car it is joined with
        if (!carried) _reconfigPick = _reconfigAsked = null;
        _net = _carriedNet ?? new NetSession { Name = "Net" };
        _carriedNet = null;
        _net.Carrying = false;
        AddChild(_net);
        // nothing is built yet: the frames wait (as they do under the loading screen)
        _loading = true;
        string host = "the server";
        int port = 0;
        if (carried) GD.Print($"network: round {_net.Client.Rounds + 1}: {_net.Client.NextArena}");
        else if (_opt.HostListen)
        {
            var (arenaKind, arenaTeams) = ServerOptions.KindOf(_opt.Arena);
            var o = new ServerOptions
            {
                Install = _opt.Install!, Arena = _opt.Arena, Port = _opt.Port, Ai = _opt.Fill, TeamPlay = _opt.Teams, Bots = System.Math.Max(0, System.Math.Min(_opt.Bots, _opt.MaxPlayers - 1)), BotSkill = _opt.BotSkill, BotChoices = _opt.BotCars, PaintOfCar = I76.Game.Shell.CarPaints.Of,
                MaxScore = _opt.MaxScore, TimeLimit = _opt.TimeLimit,
                Kind = _opt.MeleeKindGiven ? _opt.MeleeKind : arenaKind, Goal = _opt.MeleeGoal, GoalValue = _opt.GoalValue,
                CtfTeams = _opt.CtfTeams > 2 ? _opt.CtfTeams : arenaTeams, Seed = unchecked((int)I76.Sim.Rng.RandSeed.Pick(_opt.FixedSeed)),
                // the host's melee carries on into another round when it ends (the same arena); the host leaves to end it
                Rounds = true,
                // a client-hosted game: handed to a player if the host leaves (docs/multiplayer.md "Host migration")
                HostMigration = true,
                Rate = I76.Sim.Session.SimRate.FromLabel(_opt.Rate > 0 ? _opt.Rate : I76.Game.Shell.ShellSettings.Load().SimRate), MaxPlayers = _opt.MaxPlayers, UploadBytesPerSecond = (long)(_opt.Upload * 1e6 / 8), Name = _opt.GameName ?? "I76 melee", InviteOnly = _opt.InviteOnly, ListenHost = true, Password = _opt.Password ?? "",
                ShareMaps = _opt.ShareMaps ?? I76.Game.Shell.ShellSettings.Load().ShareMaps,
                AllowHidden = _opt.Hidden ?? I76.Game.Shell.ShellSettings.Load().HiddenContent,
                AllowTurrets = _opt.Turrets ?? I76.Game.Shell.MeleeSetup.Load().HostTurrets,
                AllowCarReset = _opt.CarReset ?? I76.Game.Shell.ShellSettings.Load().CarReset,
                AllowCb = _opt.Cb ?? I76.Game.Shell.ShellSettings.Load().CbRadio,
                AllowCrosshair = _opt.Crosshair ?? I76.Game.Shell.ShellSettings.Load().HostCrosshair,
                IgnitionStaysOn = _opt.IgnitionStaysOn ?? I76.Game.Shell.ShellSettings.Load().IgnitionStaysOn,
            };
            if (_opt.Restrict is { Length: > 0 } spec)
            {
                if (I76.Sim.Trip.WeaponRestrictions.Parse(spec, new I76.Formats.Vfs.AssetVfs(new I76.Formats.Vfs.GameInstall(_opt.Install!)), out var bad) is { } wr)
                    o = o with { Restrictions = wr };
                else GD.PrintErr($"--restrict: {bad}");
            }
            if (NetTest is { Seconds: > 0 } nt) o = o with { TimeLimit = nt.Seconds };
            // --coop MISSION: a co-op story mission for up to eight instead of a melee (docs/multiplayer.md "Co-op over the network");
            // no rounds: the game ends with the mission
            if (_opt.Coop is { Length: > 0 } coopMission)
                o = o with
                {
                    Arena = coopMission.ToLowerInvariant(), Coop = coopMission.ToLowerInvariant(), MaxPlayers = System.Math.Clamp(_opt.MaxPlayers, 1, I76.Net.Protocol.MaxCoopSeats), Rounds = false,
                    Ai = 0, Bots = 0, TimeLimit = 0, Name = _opt.GameName ?? $"I76 co-op {coopMission.ToLowerInvariant()}",
                    // the players gather in a lobby first (docs/multiplayer.md "The co-op lobby"): the host form's co-op game always does, the command line's with --coop-lobby
                    Lobby = _opt.CoopLobby,
                    CoopDamage = _opt.CoopDamage ?? I76.Game.Shell.ShellSettings.Load().CoopDamage,
                    CoopScale = _opt.CoopScale ?? I76.Game.Shell.ShellSettings.Load().CoopScale,
                    CoopRollingStart = _opt.CoopRollingStart ?? I76.Game.Shell.ShellSettings.Load().CoopRollingStart,
                };
            GD.Print($"listen server: {o.Arena}, {o.MaxPlayers} seats (about {I76.Game.Shell.MeleeScreen.UploadMbit(o.MaxPlayers):0.#} Mbit/s up when full), rand seed {(uint)o.Seed}{(_opt.FixedSeed is null ? " (clock)" : "")}{(o.Restrictions.IsEmpty ? "" : $", restricted weapons {o.Restrictions}")}{(o.AllowHidden ? ", hidden weapons and cars allowed" : "")}{(o.AllowTurrets ? ", several turrets allowed" : "")}");
            try { await _net.Host(o); }
            catch (System.Exception ex)
            {
                NetFailed($"the listen server failed: {ex.Message}", $"Could not host on port {_opt.Port}.");
                return false;
            }
            (host, port) = ("127.0.0.1", _net.Server!.Port);
            if (_net.Server.MapNote is { } mapNote) GD.Print($"listen server: {mapNote}");
            // --anet / the host form's server: listed there, and the hole punch answered for the joiners it introduces
            var anet = new List<(string, int)>();
            foreach (var a in _opt.AnetServers)
                if (I76.Net.Anet.AnetClient.TryParse(a, out var ah, out var ap)) anet.Add((ah, ap));
                else GD.PrintErr($"network: --anet {a}: no address");
            if (anet.Count > 0)
            {
                _net.Server.ListOn(anet);
                GD.Print($"listen server: listing the game on {string.Join(", ", _opt.AnetServers)}");
            }
        }
        else if (_opt.JoinAnet is { } js)
        {
            if (!I76.Net.Anet.AnetClient.TryParse(js, out host, out port))
            {
                NetFailed($"--join-anet {js}: no address", $"\"{js}\" is no address.");
                return false;
            }
            _opt.AnetGame ??= await FindListed(host, port, _opt.SessionName, loading);
            if (_opt.AnetGame is null)
            {
                string why = _opt.SessionName is { } sn ? $"no game named \"{sn}\" is listed on {js}" : $"no game is listed on {js}";
                NetFailed(why, $"Could not join: {why}.");
                return false;
            }
        }
        else
        {
            var hp = _opt.Connect!;
            int colon = hp.LastIndexOf(':');
            (host, port) = colon > 0 ? (hp[..colon], int.Parse(hp[(colon + 1)..])) : (hp, Protocol.DefaultPort);
        }
        // a client-hosted game goes on under another player when its host leaves (docs/multiplayer.md "Host migration")
        if (!carried) _net.EnableMigration(_opt.Install!);
        if (!carried) JoinServer(host, port);
        ulong t0 = Time.GetTicksMsec();
        string? stage = null;
        // a join through a server: the introduction and the punch (up to 16 s) come before LiteNetLib's own connect
        ulong limit = _opt.AnetGame is not null && !_opt.HostListen ? 40000ul : 20000ul;
        I76.Formats.Vfs.GameInstall.SessionMapDir = null;
        bool answered = false;
        int waited = 0;
        bool lobbied = false;
        while (Time.GetTicksMsec() - t0 < limit)
        {
            var state = _net.Client.State;
            // a custom arena (docs/multiplayer.md "Custom maps"): the offer answered, then its download, before the seat;
            // neither counts against the join's time
            if (state == ClientState.MapOffered)
            {
                if (!answered) { answered = true; await AnswerMapOffer(loading); }
                t0 = Time.GetTicksMsec();
            }
            else if (state == ClientState.Downloading)
            {
                if (_net.Client.Download is { } dl && loading is not null)
                    loading.Status = $"Downloading the host's map {dl.Offer.Mission}: {(dl.Total == 0 ? 100 : dl.Received * 100 / dl.Total)}% ({dl.Received / 1024} of {dl.Total / 1024} KiB). Esc cancels.";
                if (Input.IsKeyPressed(Key.Escape)) _net.Client.Fail("the map download was cancelled");
                t0 = Time.GetTicksMsec();
            }
            else if (state == ClientState.InLobby)
            {
                // a co-op game that has not started: its lobby (docs/multiplayer.md "The co-op lobby"), which does not count against the join's time either
                lobbied = true;
                if (loading is not null) loading.Status = null;
                NetLobby();
                t0 = Time.GetTicksMsec();
            }
            else if (state != ClientState.Connecting) break;
            // an invite-only game: the host is asked (the server turns the join away after 30 s), which does not count either
            if (state == ClientState.Connecting && _net.Client.WaitingForHost && waited++ < 40 * 60) t0 = Time.GetTicksMsec();
            if (state == ClientState.Connecting && _net.Client.Stage != stage)
            {
                stage = _net.Client.Stage;
                if (stage is not null) GD.Print($"network: {stage}");
                if (loading is not null) loading.Status = stage;
            }
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
        CloseLobby();
        if (loading is not null) loading.Status = null;
        if (_net.Client.Accept is not { } acc)
        {
            // leaving the lobby (its CANCEL, or Esc) is no failure: back to the form
            if (_lobbyLeft) { _lobbyLeft = false; BackFromNetwork([], null); return false; }
            string why = (_net.Client.Reason ?? "no answer").TrimEnd('.');
            // a refusal for the password: the form says so, and asks for it when that game is joined again
            if (_net.Client.Reason is Protocol.NeedsPassword or Protocol.WrongPassword)
                I76.Game.Shell.MeleeScreen.NoteLocked($"{host}:{port}", _opt.AnetGame is { } ag ? ag.Id : 0);
            NetFailed($"could not join {host}:{port}: {why}", $"Could not join: {why}.");
            return false;
        }
        // a downloaded map is this session's (GameInstall.SessionMapDir): the arena, its terrain and text come from there
        if (_net.Client.MapDir is { } mapDir)
        {
            I76.Formats.Vfs.GameInstall.SessionMapDir = mapDir;
            GD.Print($"network: playing the host's map from {mapDir}");
        }
        // the host's map must be this install's too (the original's JO check): refused here,
        // before anything is built, rather than playing on a different terrain and drifting apart
        if (acc.MapMismatch(new I76.Formats.Vfs.GameInstall(_opt.Install!)) is { } mismatch)
        {
            _net.Client.Leave();
            NetFailed($"could not join {host}:{port}: {mismatch}", mismatch);
            return false;
        }
        foreach (var c in acc.Cars) if (c.Vcf is { } vcf) I76.Formats.Vfs.AssetVfs.Addon[c.Def] = vcf;       // other players' own variants
        // the round this scene is built for: one that begins while the arena loads is carried into at once (NetWatch)
        _netAccept = acc;
        _roundsAtBuild = _net.Client.Rounds;
        // the listen host's own player never takes over from itself (and leaves with it); a taken-over game plays the map from here too
        if (_net.Server is { } own) own.MigrationOwner = acc.Slot;
        if (_net.Standby is { } standby) standby.MapDir = _net.Client.MapDir;
        GD.Print($"network: joined as slot {acc.Slot} in {acc.Car}, arena {acc.Arena}, server tick {acc.ServerTick}, {acc.Cars.Count} cars");
        _opt.Mission = acc.Arena;
        _opt.MissionGiven = true;
        _opt.Drive = acc.Car;
        if (acc.Coop is { } coop)
        {
            // a co-op story mission: built as a mission (its script the host's), not as a melee arena
            GD.Print($"network: the game is the co-op mission {acc.Arena}, seat {coop.Seat} of {coop.SeatCars.Count}");
            // the lobby's start: everyone plays the briefing now, each their own (the server holds the mission until every game has built it)
            if (lobbied && !_opt.SkipMovies) await CoopBriefing(acc.Arena);
            _opt.Melee = false;
            return true;
        }
        GD.Print($"network: the game is {acc.Kind}, goal {acc.Goal} {acc.GoalValue}, {acc.Teams} teams");
        _opt.Melee = true;
        return true;
    }


    // ---- the co-op lobby (docs/multiplayer.md "The co-op lobby") ----

    private I76.Game.Shell.Shell? _lobbyShell;
    private I76.Game.Shell.CoopLobbyScreen? _lobbyScreen;
    /// <summary>The player left the lobby (its CANCEL or Esc): back to the form, not a failed join.</summary>
    private bool _lobbyLeft;
    private int _lobbyFrames;
    private static readonly (int Players, int Frames)? LobbyShot =
        System.Environment.GetEnvironmentVariable("I76_LOBBY_SHOT") is { Length: > 0 } t && t.Split(',') is var p && int.TryParse(p[0], out int n) ? (n, p.Length > 1 && int.TryParse(p[1], out int f) ? f : 20) : null;

    /// <summary>
    /// Each frame in the lobby: the screen up (the join form's art, in a hosted shell over the loading screen), the server's answers to a new car
    /// given to it. Its buttons act through the client (ready, car, chat, leave) and, for a listen host, the server (start, boot).
    /// </summary>
    private void NetLobby()
    {
        var c = _net!.Client;
        if (_lobbyShell is null && _opt.Install is not null)
        {
            string start = _opt.Car ?? Protocol.MissionCar;
            var screen = new I76.Game.Shell.CoopLobbyScreen
            {
                State = () => c.Lobby,
                TakeChat = () => c.LobbyLines.TryDequeue(out var l) ? l : null,
                Send = (ready, car, config) =>
                {
                    if (car is not null) GD.Print($"network: lobby: asking for {car}{(config is null ? "" : " (our own variant)")}");
                    c.SendLobbySet(ready, car, config, car is null ? null : PaintBytes(car));
                },
                Start = force => { if (_net.Server?.StartLobby(force) is { } no) GD.Print($"network: lobby: cannot start: {no}"); },
                Chat = c.SendChat,
                Boot = who => _net.Server?.Kick(who),
                Quit = () => { _lobbyLeft = true; c.Leave(); },
                FallbackModel = _launch?.ModelIndex ?? -1,
            };
            GD.Print($"network: lobby: {c.Lobby?.Members.Count} players, the car is {start}");
            _lobbyScreen = screen;
            _lobbyShell = new I76.Game.Shell.Shell
            {
                Install = new I76.Formats.Vfs.GameInstall(_opt.Install), OnLaunch = _ => { }, Faithful = _mode1997, Name = "Lobby",
                Hosted = screen, HostLayer = 101, HostDone = () => { _lobbyLeft = true; c.Leave(); },
            };
            AddChild(_lobbyShell);
        }
        // I76_LOBBY_SHOT=PLAYERS[,FRAMES]: the lobby's capture, once it holds that many players and has been drawn that many more frames (default 20)
        if (LobbyShot is { } ls && _opt.Screenshot is not null && (c.Lobby?.Members.Count ?? 0) >= ls.Players && ++_lobbyFrames == ls.Frames)
        {
            GetViewport().GetTexture().GetImage().SavePng(_opt.Screenshot);
            GD.Print($"screenshot: {_opt.Screenshot}");
            GetTree().Quit();
        }
        while (c.ReconfigureAnswers.TryDequeue(out var a))
        {
            GD.Print($"network: lobby: the car was {(a.Taken ? "taken" : $"refused: {a.Text}")}");
            _lobbyScreen?.Answer(a);
        }
    }

    private void CloseLobby()
    {
        _lobbyShell?.QueueFree();
        _lobbyShell = null;
        _lobbyScreen = null;
    }
    /// <summary>Sends the join (direct, or through an anet server's introduction) with this player's name, car and configuration.</summary>
    /// <summary>The respray of a car file (its sidecar), set by the shell; null (the default) is the factory paint. Sent with the join and with a Reconfigure.</summary>
    public static System.Func<string, I76.Formats.Imaging.CarPaint?>? PaintOfCar = I76.Game.Shell.CarPaints.Of;

    private static byte[]? PaintBytes(string? car) =>
        car is null || car == Protocol.MissionCar ? null : PaintOfCar?.Invoke(car)?.ToBytes() is { Length: > 0 } b ? b : null;

    private void JoinServer(string host, int port)
    {
        string name = _opt.PlayerName ?? System.Environment.UserName;
        // no car given: the mission's own in a co-op game, a melee's default car (the server's: vppirna1.vcf) otherwise
        string carName = _opt.Car ?? Protocol.MissionCar;
        GD.Print($"network: joining {host}:{port} as {name} in {(carName == Protocol.MissionCar ? "the default or the mission's own car" : carName)}");
        // a variant the server's install may not have (CONFIGURE CHASSIS' own, or one dropped in the install's addon\) goes
        // along as its file, for the server to check and the other players to be sent
        byte[]? config = carName == Protocol.MissionCar ? null : I76.Formats.Vfs.AssetVfs.Addon.TryGetValue(carName, out var own) ? own
            : new I76.Formats.Vfs.GameInstall(_opt.Install!).Resolve("addon", carName) is { } ip ? System.IO.File.ReadAllBytes(ip) : null;
        if (_opt.AnetGame is { } listed && !_opt.HostListen)
        {
            GD.Print($"network: joining \"{listed.Name}\" through {host}:{port} (the host at {listed.Master}{(listed.KeyAddress != listed.Master ? $", at home {listed.KeyAddress}" : "")})");
            // a failed punch asks another of the book's servers where it sees us, to say whose router stopped it
            var check = new List<(string, int)>();
            try
            {
                foreach (var e in I76.Game.Shell.AddressBook.Load().Entries)
                    if (I76.Net.Anet.AnetClient.TryParse(e.Address, out var ch, out var cp) && !(ch.Equals(host, System.StringComparison.OrdinalIgnoreCase) && cp == port)) check.Add((ch, cp));
            }
            catch (System.Exception ex) when (ex is System.IO.IOException or System.ArgumentException) { }
            _net!.JoinVia(host, port, listed, name, carName, _opt.Team, config, _opt.Password ?? "", check, PaintBytes(carName));
        }
        else _net!.Join(host, port, name, carName, _opt.Team, config, _opt.Password ?? "", PaintBytes(carName));
    }

    /// <summary>
    /// The host's custom arena offered before the seat: this install's own copy if it is the same (by hash), a checked
    /// download of it in user://downloaded_maps, else a download as the player wants it (Play Options' Map Downloads,
    /// --map-downloads; asked on the loading screen by default), or the reason the join cannot go on.
    /// </summary>
    private async System.Threading.Tasks.Task AnswerMapOffer(I76.Game.Ui.LoadingScreen? loading)
    {
        var c = _net!.Client;
        var offer = c.Offer!;
        var install = new I76.Formats.Vfs.GameInstall(_opt.Install!) { MapDir = null };
        var cache = new MapCache(ProjectSettings.GlobalizePath("user://downloaded_maps"));
        long max = I76.Formats.Mission.MapFileSet.DefaultMaxBytes;
        MapChoice choice;
        string? dir;
        try { choice = cache.Choose(install, offer, max, out dir); }
        catch (System.Exception e) when (e is System.IO.IOException or System.UnauthorizedAccessException) { c.Fail($"the map cache: {e.Message}"); return; }
        string size = offer.TotalSize < 1024 * 1024 ? $"{(offer.TotalSize + 1023) / 1024} KB" : $"{offer.TotalSize / (1024.0 * 1024):0.0} MB";
        GD.Print($"network: the host's arena is the custom map {offer.Mission}: {offer.Files.Count} files, {size}, hash {offer.HashHex[..16]}{(offer.Shared ? "" : $", not shared ({offer.Reason})")}: {choice}");
        switch (choice)
        {
            case MapChoice.Installed: c.ReadyMap(); return;
            case MapChoice.Cached: c.ReadyMap(dir); return;
            case MapChoice.Unavailable: c.Fail(MapCache.WhyNot(install, offer, max)); return;
        }
        var settings = I76.Game.Shell.ShellSettings.Load();
        int mode = _opt.MapDownloads ?? settings.MapDownloads;
        bool have = install.FindMission(offer.Mission) is not null;
        if (mode == 2)
        {
            c.Fail(have ? $"Your copy of the host's map {offer.Mission} differs from the host's, and map downloads are off (Play Options)"
                : $"The host's map {offer.Mission} is not installed, and map downloads are off (Play Options)");
            return;
        }
        if (mode == 0 && loading is not null)
        {
            string q = have ? $"Your copy of the host's map {offer.Mission} differs from the host's.\nDownload the host's ({size})?"
                : $"The host's map {offer.Mission} ({size}) isn't installed.\nDownload it?";
            var asked = loading.Ask(q, ["Download", "Always", "Cancel"]);
            // the server going away meanwhile (or tiring of waiting, MapTransferLimits.AnswerSeconds) takes the question down
            while (!asked.IsCompleted && c.State == ClientState.MapOffered) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            if (c.State != ClientState.MapOffered) { loading.Dismiss(); return; }
            int a = asked.Result;
            if (a is not (0 or 1)) { c.Fail("the map download was declined"); return; }
            if (a == 1) { settings.MapDownloads = 1; settings.Save(); }
        }
        GD.Print($"network: downloading {offer.Mission} into {cache.Dir(offer.Hash)}");
        c.DownloadMap(cache);
    }

    /// <summary>The game named <paramref name="name"/> (or the first) on an anet server's list: up to 12 s of listing, the server's state on the loading screen.</summary>
    private async System.Threading.Tasks.Task<I76.Net.Anet.AnetSession?> FindListed(string host, int port, string? name, I76.Game.Ui.LoadingScreen? loading)
    {
        using var b = new SessionBrowser { Nitro = new I76.Formats.Vfs.GameInstall(_opt.Install!).IsNitro };
        ulong t0 = Time.GetTicksMsec(), asked = 0;
        while (Time.GetTicksMsec() - t0 < 12000)
        {
            if (Time.GetTicksMsec() - asked > 1000) { asked = Time.GetTicksMsec(); b.List(host, port); }
            b.Poll();
            if (b.Sessions.FirstOrDefault(f => f.Anet is not null && (name is null || f.Info.Name == name)) is { Anet: { } l }) return l.Session;
            if (loading is not null) loading.Status = b.ServerStatus;
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
        GD.PrintErr($"network: {b.ServerStatus}");
        return null;
    }

    /// <summary>A launch from the shell's MULTI MELEE forms, as the command line's --host-listen or --connect would give it.</summary>
    private void ApplyNetLaunch(I76.Game.Shell.ShellLaunch l, I76.Game.Shell.NetLaunch n)
    {
        _opt.HostListen = n.Connect is null;
        _opt.Connect = n.AnetGame is null ? n.Connect : null;
        _opt.JoinAnet = n.AnetGame is null ? null : n.AnetServer;
        _opt.AnetGame = n.AnetGame;
        _opt.AnetServers = n.ListOn is { } lo ? [lo] : [];
        _opt.Arena = l.Mission;
        // a co-op host form's game: the mission plays in co-op (the arena is its story mission)
        _opt.Coop = n.Connect is null && n.AnetGame is null && n.Coop ? l.Mission : null;
        _opt.CoopLobby = _opt.Coop is not null;
        _opt.CoopDamage = n.CoopDamage;
        _opt.CoopScale = n.CoopScale;
        _opt.CoopRollingStart = n.CoopRollingStart;
        // a LAN host takes the first free port of the ones a LAN search asks (a second game on one machine)
        _opt.Port = n.Mode == 3 ? FreePort(n.Port, Discovery.PortsSearched) : n.Port;
        _opt.Fill = 0;
        _opt.Teams = n.TeamPlay;
        _opt.MaxScore = n.MaxScore;
        _opt.TimeLimit = n.Minutes * 60;
        _opt.MaxPlayers = n.MaxPlayers;
        _opt.GameName = n.GameName;
        _opt.InviteOnly = n.InviteOnly;
        _opt.Password = n.Password;
        _opt.PlayerName = l.Driver;
        _opt.Car = n.StoryCar ? Protocol.MissionCar : l.Car;
        _opt.Team = n.Team;
        _opt.MeleeKindGiven = n.Kind is not null;
        if (n.Kind is { } kind) _opt.MeleeKind = kind;
        _opt.MeleeGoal = n.Goal;
        _opt.GoalValue = n.GoalValue;
        _opt.CtfTeams = n.CtfTeams;
        _opt.ShareMaps = n.ShareMaps;
        _opt.Restrict = n.Restrictions.IsEmpty ? null : n.Restrictions.ToSpec();
        _opt.Hidden = n.Hidden;
        _opt.Turrets = n.Turrets;
        _opt.CarReset = n.CarReset;
        _opt.IgnitionStaysOn = n.IgnitionStaysOn;
        _opt.Cb = n.Cb;
        _opt.Crosshair = n.Crosshair;
        _opt.Bots = n.Bots;
        _opt.BotSkill = n.BotSkill;
        _opt.BotCars = [.. n.BotCars];
        _opt.Rate = n.Rate;
    }

    /// <summary>The first UDP port from <paramref name="from"/> that can be bound, of <paramref name="count"/>; the first if none.</summary>
    private static int FreePort(int from, int count)
    {
        for (int p = from; p < from + count; p++)
        {
            try { using var u = new System.Net.Sockets.UdpClient(p); return p; }
            catch (System.Net.Sockets.SocketException) { }
        }
        return from;
    }

    /// <summary>Hosting or joining failed: from the command line the game quits; from the shell it goes back to the form, saying why.</summary>
    private void NetFailed(string log, string notice)
    {
        GD.PrintErr($"network: {log}");
        I76.Formats.Vfs.GameInstall.SessionMapDir = null;
        if (_launch is not { Kind: I76.Game.Shell.GameKind.Multiplayer }) { ExitWatchdog.ExitCode = 1; GetTree().Quit(1); return; }
        BackFromNetwork([], notice);
    }

    /// <summary>
    /// Back to the shell from a network melee: its STANDINGS from the last
    /// scoreboard (then the form), or the form with a notice. Leaving the tree
    /// says goodbye to the server and stops a listen server (NetSession).
    /// </summary>
    private void BackFromNetwork(IReadOnlyList<I76.Game.Shell.StandingRow> rows, string? error)
    {
        if (_ended) return;
        _ended = true;
        // a downloaded map was this session's only
        I76.Formats.Vfs.GameInstall.SessionMapDir = null;
        // from the command line there is no shell to go back to: the game is over, and so is the program (as F10 is)
        if (_launch is not { Kind: I76.Game.Shell.GameKind.Multiplayer })
        {
            GD.Print($"network: the game is over ({error ?? "game over"}): a command-line game ends the program");
            GetTree().Paused = false;
            GetTree().Quit();
            return;
        }
        GD.Print($"network: back to the shell ({rows.Count} standings{(error is null ? "" : ", " + error)})");
        // a co-op game has no standings: back to its form
        if (_netAccept?.Coop is not null) rows = [];
        _lastNet = new I76.Game.Shell.NetResult(rows, error, _netAccept?.TeamPlay ?? false, _netAccept?.Kind ?? I76.Sim.Missions.MeleeKind.Combat);
        (_lastLaunch, _launch, _lastResult, _lastTrip, _lastMelee) = (_launch, null, I76.Game.Shell.MissionResult.Quit, null, null);
        _backToMenu = true;
        GetTree().Paused = false;
        GetTree().ReloadCurrentScene();
    }

    /// <summary>The last scoreboard as STANDINGS rows, each player's car from the mirror: its file, or the body of one served by its content (whose file name means nothing).</summary>
    private List<I76.Game.Shell.StandingRow> NetRows() =>
        _net?.Client.Scores?.Players.Select(p => new I76.Game.Shell.StandingRow(p.Name, CarOf(_netWorld?.Cars.GetValueOrDefault(p.CarSerial)?.Info),
            p.Deaths, p.Kills, p.Score, p.Team)).ToList() ?? [];

    private static string CarOf(CarInfo? c) =>
        c is null ? "" : c.Vcf is { Length: > 0 } v && I76.Formats.Shell.CarNames.BodyOf(v) is { } body ? body : c.Def;

    /// <summary>
    /// Test hook: I76_NET_TEST=players[,seconds]. A network game does not
    /// count frames towards --quit-after (the shell's STANDINGS after it
    /// does); once the scoreboard has that many players, 90 frames on, the
    /// view is saved as the screenshot's _game.png and the players logged. With
    /// seconds, a listen server's melee ends after that long.
    /// </summary>
    private static readonly (int Players, int Seconds)? NetTest =
        System.Environment.GetEnvironmentVariable("I76_NET_TEST") is { Length: > 0 } t && t.Split(',') is var p && int.TryParse(p[0], out int n)
            ? (n, p.Length > 1 && int.TryParse(p[1], out int s) ? s : 0) : null;
    private int _netTestFrames;

    private void NetTestFrame()
    {
        if (NetTest is not { } t || _opt.Screenshot is null || _netTestFrames > 90) return;
        var players = _net?.Client.Scores?.Players;
        if (players is null || players.Count < t.Players || ++_netTestFrames != 90) return;
        var path = _opt.Screenshot.Replace(".png", "_game.png");
        GetViewport().GetTexture().GetImage().SavePng(path);
        GD.Print($"net test: {players.Count} players ({string.Join(", ", players.Select(x => x.Name))}), {_netWorld?.Cars.Count} cars in the mirror; saved {path}");
    }

    /// <summary>
    /// Capture hook: I76_NET_HURT=N on a listen server. The nearest other car
    /// is first set down 30 m ahead of the host's and made its radar target;
    /// then every N server ticks it takes a round's hit from the host on its
    /// front (on the server, as a hit of its own would land), so a capture
    /// shows the damage reaching both clients' target bar and damage panel.
    /// </summary>
    private static readonly int NetHurtEvery = int.TryParse(System.Environment.GetEnvironmentVariable("I76_NET_HURT"), out int nh) ? nh : 0;
    private uint _netHurtAt;
    private I76.Sim.Vehicles.Vehicle? _netHurtTarget;

    private void NetHurt()
    {
        if (NetHurtEvery <= 0 || _netWorld is not { } mirror) return;
        _net!.Server!.Invoke(h =>
        {
            var me = h.Seats.FirstOrDefault(s => s.CarSerial == mirror.OwnSerial)?.Player.Car;
            if (me is null) return;
            if (_netHurtTarget is null)
            {
                var near = h.Setup.World.Vehicles.Where(v => v != me && v.NetId != 0).MinBy(v => (v.Pos.ToVector3() - me.Pos.ToVector3()).LengthSquared());
                if (near is null) return;
                _netHurtTarget = near;
                near.Pos = me.Pos + me.Rot.Forward * 30 + new System.Numerics.Vector3(0, 2, 0);
                near.Rot = me.Rot;
                near.Vel = near.AngVel = default;
                near.NetGeneration++;
                h.Setup.Combat.RadarTarget[me] = near;
                _netHurtAt = h.Tick;
                return;
            }
            var t = _netHurtTarget;
            if (h.Tick < _netHurtAt + NetHurtEvery || I76.Sim.Damage.DamageModel.IsDead(t)) return;
            _netHurtAt = h.Tick;
            var p = new I76.Sim.Weapons.DamagePacket { Attacker = me, Mask = 2, Kind = I76.Sim.Damage.DamageKind.Round };
            p.Ch[1] = 150;
            h.Setup.Damage.Apply(t, p, new System.Numerics.Vector3(0, 0.5f, 2));
            GD.Print($"net hurt: {t.NetId & 0xffffff} at tick {h.Tick}, bar {t.Health?.BarRatio():0.00}");
        });
    }

    private int _movieSeq;
    private I76.Game.Ui.MoviePlayer? _netMovie;
    private AudioStreamPlayer? _netMovieMusic;

    /// <summary>
    /// A co-op mission's movie (the script's playMovie: T05, T06) for everyone, the game standing still (the server does not tick;
    /// this client neither predicts nor draws on: <c>NetView.Frozen</c>). It plays here from the client's word (a join mid-way and a
    /// new host's too), over the game; only the host's Esc (or click, pad B...) skips it and that ends it for everyone: another
    /// player's Esc opens the menu as at any time. Whichever way it ends here, the server hears of it; if the server ends it first
    /// (the host's copy is over) this one goes at once.
    /// </summary>
    private void NetMovie(MeleeClient c)
    {
        if (_netAccept?.Coop is null || _opt.Install is null) return;
        if (c.MovieName is { } name && c.MovieSeq != _movieSeq)
        {
            _movieSeq = c.MovieSeq;
            // the new host's accept names the movie this game is playing already
            if (_netMovie is { } playing && IsInstanceValid(playing)) return;
            var install = new I76.Formats.Vfs.GameInstall(_opt.Install);
            if (_opt.SkipMovies || I76.Game.Ui.MoviePlayer.Find(install, name) is not { } path) { c.SendMovieDone(name); return; }
            DropMenuMusic();
            var music = GetNodeOrNull<AudioStreamPlayer>("Music");
            if (music is not null) music.StreamPaused = true;
            _netMovieMusic = music;
            GD.Print($"movie: {name}{(SkipsCutscenes ? "" : " (the host's to skip)")}");
            try
            {
                var player = new I76.Game.Ui.MoviePlayer { Path = path, Faithful = _opt.Mode1997, Nitro = install.IsNitro, Name = "Movie", Skippable = SkipsCutscenes };
                player.Finished += () => { GD.Print($"movie: {name} over here"); NetMovieOver(); c.SendMovieDone(name); };
                _netMovie = player;
                AddChild(player);
            }
            catch (System.Exception ex) { GD.PrintErr($"movie {name}: not played: {ex}"); NetMovieOver(); c.SendMovieDone(name); }
        }
        else if (c.MovieName is null && _netMovie is { } p)
        {
            // the server ended it (the host's copy is over, or the host skipped it)
            _netMovie = null;
            if (IsInstanceValid(p)) { p.QueueFree(); }
            NetMovieOver();
        }
    }

    private void NetMovieOver()
    {
        _netMovie = null;
        if (_netMovieMusic is { } m && IsInstanceValid(m))
        {
            m.StreamPaused = false;
            if (!m.Playing) StartMissionMusic();
        }
        _netMovieMusic = null;
    }

    /// <summary>When the game over came (the HUD shows the final scores a while first).</summary>
    private double? _netOverAt;

    /// <summary>
    /// A network melee ends by itself: 8 s after its game over, or when the server goes away. From the shell it goes
    /// back to the STANDINGS; from the command line the program ends (with I76_NET_TEST a network game does not count
    /// frames towards --quit-after, so without this a client whose host had gone never quit).
    /// </summary>
    private void NetWatch()
    {
        double now = Time.GetTicksMsec() / 1000.0;
        var c = _net!.Client;
        NetMovie(c);
        // this game took over as host (host migration): the host's prompts and boots are its own now
        if (_netHud is { Server: null } hud && _net.Server is { } taken) { hud.Server = taken; hud.Host = true; }
        // the server started another round on this connection: reload as a fresh join, the seat kept
        if (c.Rounds > _roundsAtBuild && c.State is not (ClientState.Disconnected or ClientState.Rejected)) { CarryNetwork(); return; }
        // a server that carries on is waited for (its pause, and some leeway for it to build the next arena)
        double wait = _netNextRound > 0 ? _netNextRound + 30 : 8;
        if (_netOverAt is { } over && now - over > wait) BackFromNetwork(NetRows(), null);
        else if (c.State is ClientState.Disconnected or ClientState.Rejected)
            BackFromNetwork(NetRows(), _netOverAt is null || _netNextRound > 0 ? $"The game ended: {(c.Reason ?? "the server went away").TrimEnd('.')}." : null);
    }

    /// <summary>
    /// Into the next round: the session (the connection, a listen host's server) is taken out of the scene so that its reload
    /// does not close it, and the new scene joins by it as it would a fresh server (the map offer and download included).
    /// </summary>
    private void CarryNetwork()
    {
        if (_ended || _net is null) return;
        _ended = true;
        GD.Print($"network: the server starts another round ({_net.Client.NextArena})");
        _net.Carrying = true;
        RemoveChild(_net);
        _carriedNet = _net;
        _net = null;
        I76.Formats.Vfs.GameInstall.SessionMapDir = null;
        if (_launch is { Kind: I76.Game.Shell.GameKind.Multiplayer }) NextMission = _launch.Mission;
        GetTree().Paused = false;
        GetTree().ReloadCurrentScene();
    }

    /// <summary>The arena's cars as the server has them, and the player's views on their own car.</summary>
    private void SpawnNetwork(JoinAccept accept)
    {
        var w = _builder!.World!;
        // a co-op mission is built as the server built it (its seats' cars, its script, which only the server runs); a melee as an
        // arena with no cars
        var setup = accept.Coop is { } coop
            ? CoopMission.Build(_builder.Vfs, w, _builder.Assembler!, coop.Setup, new I76.Sim.Rng.CrtRand(1), 1)
            : I76.Sim.Missions.MissionSetup.Build(_builder.Vfs, w, _builder.Assembler!, null, _origin, script: false,
                melee: [], meleeRules: new I76.Sim.Missions.MeleeRules { Network = true, Kind = accept.Kind, TeamPlay = accept.TeamPlay });
        var world = setup.World;
        var mirror = _netWorld = new ClientWorld(setup, accept, accept.Coop is not null ? new CoopMirrorCars(_builder.Vfs) : null);
        mirror.Log += m => GD.Print($"network: {m}");
        foreach (var m in mirror.Cars.Values)
            if (m.StandInFor is { } wanted) GD.Print($"network: car {m.Info.Serial} ({m.Info.Name}): {wanted} cannot be built here; drawn as {m.Info.Def}");
        var client = _net!.Client;
        // what came while the arena loaded: the cars stand where the server has them from the first frame
        mirror.Advance(0, client);
        // the own car predicted from here on (docs/multiplayer.md, "Prediction"); I76_NET_PREDICT=0 for A/B and debugging
        // the own shots fired at once from the predicted car too (docs/multiplayer.md, "Own shots at once"); I76_NET_PREDICT_FIRE=0 draws them as the server sends them
        var predictor = System.Environment.GetEnvironmentVariable("I76_NET_PREDICT") == "0" ? null
            : new OwnCarPredictor(mirror, client, predictFire: System.Environment.GetEnvironmentVariable("I76_NET_PREDICT_FIRE") != "0");
        predictor?.Reconcile();
        GD.Print(predictor is null ? "network: no prediction (I76_NET_PREDICT=0)" : "network: the own car is predicted");
        // the cars near the own car drawn at its present, so they touch when they meet (I76_NET_NEAR=0: each on its own clock)
        mirror.NearAtPresent = System.Environment.GetEnvironmentVariable("I76_NET_NEAR") != "0";
        _script = setup.Script;
        _objectives = setup.ObjectiveText;
        // a co-op mission's movies (T05, T06) are played from the client's word (NetMovie), the game standing still for them
        var audio = new I76.Game.Assets.AudioBank(_builder.Vfs);
        var view = new NetView { World = mirror, Client = client, Nodes = _carNodes, Predictor = predictor };
        Controls.Held = _held = () => _menu is not null || _picture is not null || _reconfig is not null || GetNodeOrNull("Movie") is not null;   // #109: Esc's arrow keys turned the head
        _sim = new SimDriver { Session = mirror.Session, Name = "Sim", HoldThrottle = _opt.HoldThrottle, HoldFire = _opt.HoldFire, Origin = () => _origin, Audio = audio, Remote = view,
            InputHeld = () => _menu is not null || _picture is not null || _reconfig is not null || GetNodeOrNull("Movie") is not null, CameraSubject = () => _tv?.Subject,
            HookRemoteScript = accept.Coop is not null };
        _sim.ProcessPriority = -1;
        AddChild(_sim);
        AddChild(new PausedClock { Owner = this, Name = "PausedClock" });
        var playSettings = I76.Game.Shell.ShellSettings.Load();
        ApplyAudio(playSettings);
        ApplyPlayOptions(playSettings);     // Force Feedback and Auto Reverse too: a network game never applied them
        ApplyGraphics(playSettings);
        _audio = audio;
        AddCombatView(setup.Combat, () => view.TickAlpha);
        var damageView = new DamageView { Damage = setup.Damage, Origin = () => _origin, Builder = _builder, Cars = _carNodes, PlacementOf = setup.PlacementOf, Audio = audio, Ground = setup.Ground, Name = "Damage" };
        // one car this game cannot draw must not take the others with it (they were all left undrawn after it); it is drawn
        // as the stand-in car (the mirror's own fallback for a car it cannot build), so the player is still seen
        void AddCar(MirrorCar m)
        {
            foreach (var def in new[] { m.Info.Def, ClientWorld.StandInCar }.Distinct(System.StringComparer.OrdinalIgnoreCase))
            {
                try { AddCarNode(m, def); return; }
                catch (System.Exception ex)
                {
                    GD.PrintErr($"network: car {m.Info.Serial} ({m.Info.Name}) could not be drawn as {def}: {ex}");
                    if (_carNodes.Remove(m.Vehicle, out var half)) { _sim.Cars.Remove(half); half.QueueFree(); }
                }
            }
        }
        void AddCarNode(MirrorCar m, string def)
        {
            var node = MakeCarNode(m.Vehicle, def, setup.Ground);
            node.SetPoses(m.PrevRot, m.PrevPos, m.Rot, m.Pos);
            // each remote car on its own clock (ClientWorld's CarTrack: far cars come less often)
            node.LivePose = () => m.Drawn;
            if (m.Info.Serial == accept.CarSerial)
            {
                SetupPlayerCar(node, m.Vehicle, def, setup.Combat, handguns: false);
                if (predictor is not null) node.LivePose = () => predictor.Ready && !mirror.OwnFromSnapshots ? predictor.Pose(view.TickAlpha) : m.Drawn;
            }
        }
        // the cars a spawn can bring in later (a bot, the fill's replacement) made now, behind the loading card, so that none is built mid-fight
        var defaults = new ServerOptions { Install = _opt.Install! };
        var kinds = mirror.Cars.Values.Select(c => c.Info.Def).Concat([ClientWorld.StandInCar]).Concat(defaults.BotCars).Concat(defaults.AiCars).ToList();
        PrewarmCars(kinds);
        foreach (var m in mirror.Cars.Values) AddCar(m);
        AddChild(damageView);
        damageView.PrewarmWrecks(kinds, !_opt.Mode1997);   // and their burnt looks, so that no car's first death builds one
        if (accept.Coop is not null) _damageView = damageView;
        // the gates the host's script slides (type-7 nodes): their meshes follow the mirror's colliders, which the snapshots move
        if (accept.Coop is not null && _builder.Scenery is { } gateScenery)
        {
            var gateOf = new Dictionary<(I76.Formats.Assembly.Placement, string), I76.Sim.Collision.GateMotion>();
            foreach (var (so, gp) in setup.PlacementOf)
                foreach (var n in so.Nodes) if (n.Gate is { } g) gateOf[(gp, n.Name)] = g;
            if (gateOf.Count > 0) gateScenery.GateShift = (gp, name) => gateOf.TryGetValue((gp, name), out var g) ? g.Shift : default;
        }
        foreach (var o in mirror.DestroyedStructures) damageView.Destroy(o, quiet: true);
        mirror.Added += AddCar;
        mirror.Removed += m => { if (_carNodes.Remove(m.Vehicle, out var n)) { _sim.Cars.Remove(n); n.QueueFree(); } };
        // a player's reconfigured respawn: the car is another car now, drawn anew (and the player's own, with its cockpit)
        mirror.Rebuilt += m =>
        {
            if (_carNodes.Remove(m.Vehicle, out var old)) { _sim.Cars.Remove(old); old.QueueFree(); }
            if (m.Info.Serial == accept.CarSerial)
            {
                _pit?.QueueFree();
                _pit = null;
            }
            AddCar(m);
            if (m.Info.Serial == accept.CarSerial && _car is { } own)
            {
                if (_chase is not null) { _chase.Target = own; _chase.LocalCentre = Coords.ToGodot(_playerCollider.Centre); _chase.Radius = _playerCollider.Radius; }
                if (_preset is not null) { _preset.Target = own; _preset.LocalCentre = _chase?.LocalCentre ?? _preset.LocalCentre; _preset.Radius = _chase?.Radius ?? _preset.Radius; }
                AddMirrorTarget(setup.Combat);
                GD.Print($"network: our car is now {m.Info.Def}");
            }
        };
        mirror.Renewed += (m, respawn) =>
        {
            if (_carNodes.GetValueOrDefault(m.Vehicle) is not { } n) return;
            damageView.Restore(n);
            if (respawn) n.RestartEngine();
        };
        mirror.News += e => view.Play(e, audio);
        mirror.News += e => { if (e is GameOverEvent g) { _netOverAt ??= Time.GetTicksMsec() / 1000.0; _netNextRound = g.NextRoundSeconds; } };
        if (_car is null) { _status += "\nthe server's car for us is not in this install"; return; }
        Rebase(_car.GamePosition);
        AddPlayerViews(world, setup.Combat);
        // CTF's flags on the radar in their team's colours (class 0x5b blips)
        if (_pit is { } radarPit && accept.Kind == I76.Sim.Missions.MeleeKind.Ctf)
            radarPit.Blips = () => mirror.Flags.Where(f => f.State != 0).Select(f => (f.Pos, NetHud.TeamColour(f.Team)));
        if (accept.Kind == I76.Sim.Missions.MeleeKind.Ctf) AddChild(new FlagView { World = mirror, Origin = () => _origin, CarNode = serial => mirror.Car(serial) is { } v ? _carNodes.GetValueOrDefault(v) : null, Name = "Flags" });
        AddChild(new NetNames { World = mirror, Nodes = _carNodes, Name = "NetNames", Kind = accept.Kind, Modern = () => !_mode1997, CanAct = () => !_inMenu && _menu is null && _picture is null && !_loading });
        AddChild(_netHud = new NetHud
        {
            Client = client, World = mirror, Name = "NetHud", Predictor = predictor, Host = _net.Server is not null, Server = _net.Server,
            Nitro = _builder.Vfs.Install.IsNitro, LeeFont = _builder.Vfs.Install.FindFile("Lee_____.ttf"),
            Won = () => _netWinOrbit = true, Modern = () => !_mode1997, Cutscene = () => accept.Coop is not null && _scriptCam?.Current == true,
            CanChat = () => !_inMenu && _menu is null && _picture is null && !_loading && _reconfig is null, HostStatus = () => _net?.Server?.AnetStatus,
        });
        _status += accept.Coop is { } cs ? $"\nnetwork co-op {accept.Arena}: seat {cs.Seat}, slot {accept.Slot}, {accept.Car}, {mirror.Cars.Count} cars; Ctrl+F10 leaves"
            : $"\nnetwork melee: slot {accept.Slot}, {accept.Car}, {mirror.Cars.Count} cars; Ctrl+F10 leaves";
    }

    /// <summary>
    /// Reconfigure (I26's, docs/deviations.md): the join form's car panel in a hosted shell over the game, while the player is
    /// dead. Its car goes to the server (<see cref="MeleeClient.SendReconfigure"/>), which checks it and answers.
    /// </summary>
    private void OpenReconfigure()
    {
        if (_reconfig is not null || _net is null || _opt.Install is null) return;
        var client = _net.Client;
        var own = _netWorld?.Own?.Info.Def;
        var screen = new I76.Game.Shell.ReconfigureScreen
        {
            // the car Reconfigure last got taken, else the one the player is in (the server's, else the launch's)
            Model = -1, Variant = 1,
            CarFile = _reconfigPick ?? own ?? _launch?.Car ?? _opt.Car,
            FallbackModel = _launch?.ModelIndex ?? -1,
            Send = (car, config) =>
            {
                GD.Print($"network: reconfigure: asking for {car}{(config is null ? "" : " (our own variant)")}");
                _reconfigAsked = car;
                client.SendReconfigure(car, config, PaintBytes(car));
            },
        };
        GD.Print($"network: reconfigure: open on {screen.CarFile}");
        _reconfigScreen = screen;
        _reconfig = new I76.Game.Shell.Shell
        {
            Install = new I76.Formats.Vfs.GameInstall(_opt.Install), OnLaunch = _ => { }, Faithful = _mode1997, Name = "Reconfigure",
            Hosted = screen, HostDone = () => Callable.From(CloseReconfigure).CallDeferred(),
        };
        AddChild(_reconfig);
    }

    private I76.Game.Shell.ReconfigureScreen? _reconfigScreen;

    private void CloseReconfigure()
    {
        _reconfig?.QueueFree();
        _reconfig = null;
        _reconfigScreen = null;
    }

    /// <summary>The server's answers to Reconfigure: the feed says so; taken, the screen closes, refused, it shows why.</summary>
    private void NetReconfigureAnswers()
    {
        if (_net is null) return;
        while (_net.Client.ReconfigureAnswers.TryDequeue(out var a))
        {
            GD.Print($"network: reconfigure: {(a.Taken ? $"taken, {a.Text}" : $"refused, {a.Text}")}");
            if (a.Taken && _reconfigAsked is { } asked) _reconfigPick = asked;
            string? name = a.Taken && _reconfigPick is { } p ? CarLabel(p) : null;
            _netHud?.Reconfigured(a, name);
            _reconfigScreen?.Answer(a);
            if (a.Taken) CloseReconfigure();
        }
        // the way back closed (the game decided, or the car back already): the screen goes
        if (_reconfig is not null && _netHud?.CanComeBack != true) CloseReconfigure();
    }

    /// <summary>A model's MAKE / MODEL and variant name as the melee lists show them (the shell's data).</summary>
    private string? CarLabel(string car)
    {
        try
        {
            var vfs = _builder!.Vfs;
            var data = vfs.Install.IsNitro ? new I76.Game.Shell.MeleeData(vfs, I76.Game.Shell.NitroLists.MeleeCars(vfs.Install), I76.Game.Shell.Shell.NitroAddon, I76.Game.Shell.ShellSettings.Load().HiddenContent)
                : new I76.Game.Shell.MeleeData(vfs, I76.Game.Shell.ShellSettings.Load().HiddenContent);
            if (data.Find(car) is not var (model, variant)) return null;
            var m = data.Models[model];
            return $"{m.MakeModel} ({m.Variants[variant - 1]})";
        }
        catch (Exception) { return null; }
    }

    /// <summary>The SimDriver's remote: the input to the server, the cars from the mirror, the sounds the server names.</summary>
    private sealed class NetView : IRemoteSim
    {
        public required ClientWorld World { get; init; }
        public required MeleeClient Client { get; init; }
        public required Dictionary<I76.Sim.Vehicles.Vehicle, CarNode> Nodes { get; init; }
        /// <summary>The own car's prediction (null with I76_NET_PREDICT=0: it is drawn behind with the rest).</summary>
        public OwnCarPredictor? Predictor { get; init; }
        /// <summary>How far the input clock is into its next tick: the predicted car is drawn there.</summary>
        public float TickAlpha { get; private set; }
        private readonly Dictionary<MirrorCar, int> _poses = [];
        /// <summary>I76_NET_CARS=1: a line a frame for each remote car, where it is drawn and on what clock (smoothness checks).</summary>
        private readonly bool _carsTrace = System.Environment.GetEnvironmentVariable("I76_NET_CARS") == "1";
        private readonly bool _debug = System.Environment.GetEnvironmentVariable("I76_NET_DEBUG") is { Length: > 0 } and not "0";
        /// <summary>I76_NET_DEBUG=2: a line every input tick, the predicted car against the newest the server has sent.</summary>
        private readonly bool _trace = System.Environment.GetEnvironmentVariable("I76_NET_DEBUG") == "2";
        private int _told;
        private readonly Dictionary<I76.Sim.Vehicles.Vehicle, bool> _engineOn = [];

        public int Tick => (int)World.IntervalTo;
        public double ClockScale => Client.ClockScale;
        /// <summary>A co-op movie is playing: the server does not tick, and this client neither predicts nor draws on.</summary>
        public bool Frozen => Client.MovieName is not null;

        private CarNode? OwnNode => World.Own is { } me ? Nodes.GetValueOrDefault(me.Vehicle) : null;

        /// <summary>I76_NET_BOT=1, a capture hook: the own car drives itself as a headless player's hunter does, for footage of a
        /// network game with nobody at the wheel.</summary>
        private readonly I76.Net.BotDriver? _bot = System.Environment.GetEnvironmentVariable("I76_NET_BOT") == "1" ? new(roams: false, seed: 99) : null;

        public void SendInput(I76.Sim.Session.CarInput input)
        {
            if (_bot is not null)
            {
                foreach (var m in World.Cars.Values) _bot.Learn(m.Info);
                input = _bot.Next(Client);
            }
            Client.SendInput(input);
            if (Predictor is not { } p) return;
            p.Step(Client.InputTick, input);
            // the drawn wheels (steer, travel, roll) between the last two predicted ticks; the body's pose is the predictor's (LivePose)
            if (p.Ready && !World.OwnFromSnapshots && OwnNode is { } n) n.SetPoses(p.PrevRot, p.PrevPos, p.Rot, p.Pos);
            if (_trace && World.Own?.Vehicle is { } me && Client.Snapshots.Count > 0)
            {
                var newest = Client.Snapshots.Values[^1];
                var srv = newest.Cars.FirstOrDefault(c => c.Serial == World.OwnSerial);
                GD.Print($"prediction: input {Client.InputTick} keys {input.Buttons} predicted {me.Speed:0.00} m/s at {me.Pos.X:0.00},{me.Pos.Z:0.00}; " +
                    $"server tick {newest.Tick} (acked {newest.AckedInput}) {srv?.Speed ?? 0:0.00} m/s at {srv?.Pos.X ?? 0:0.00},{srv?.Pos.Z ?? 0:0.00}; offset {p.Offset:0.000} m");
            }
        }

        public float Advance(double delta, float tickAlpha)
        {
            TickAlpha = tickAlpha;
            World.InputAlpha = tickAlpha;
            World.Hold = Frozen;
            if (Frozen) { World.Advance(0, Client); return World.Alpha; }
            // a co-op cut-scene drives the own car by the host's script: drawn from the snapshots, as the camera is
            World.OwnFromSnapshots = World.Session.Cutscene;
            World.Advance(delta, Client);
            if (Predictor is { } p)
            {
                int was = p.Reconciles;
                p.Reconcile();
                p.Smooth(delta);
                // I76_NET_DEBUG: each correction bigger than the tolerance, and a summary every 30 s
                if (_debug && p.Reconciles != was && p.LastCorrection > p.Tolerance)
                    GD.Print($"prediction: correction {p.LastCorrection:0.000} m (server tick {Client.NewestTick}, acked {Client.AckedInput} of {Client.InputTick}, off by {p.LastError:0.000} m)");
                if (_debug && p.Reconciles / (30 * 20) != _told)
                {
                    _told = p.Reconciles / (30 * 20);
                    GD.Print($"prediction: {p.Compared} compared, {p.Corrections} off by more than {p.Tolerance * 100:0} cm, {p.Inexact} not exact, max {p.MaxError:0.000} m, {p.Snaps} snaps; clock {Client.ClockScale:0.000}" +
                        (p.Shots is { } os ? $"; shots: {os.Predicted} drawn at once, {os.Rejected} taken back, {os.Adopted} the server's drawn, {os.HandedOver} handed over" : ""));
                }
            }
            if (_carsTrace)
                foreach (var m in World.Cars.Values)
                    if (m.Drawn is { } d && m.Info.Serial != World.OwnSerial)
                        GD.Print(System.FormattableString.Invariant($"cars: {Time.GetTicksUsec()} {m.Info.Serial} {d.Pos.X:0.000} {d.Pos.Y:0.000} {d.Pos.Z:0.000} speed {m.Vehicle.Speed:0.00} interval {m.Track.Interval} extra {m.Track.Extra:0.00} offset {m.Track.Offset.Length():0.000} extrapolated {m.Track.Extrapolated} lead {m.Track.Lead:0.00}"));
            // the wheels and roll follow each car's own pair of states (its body is drawn at m.Drawn, LivePose)
            foreach (var m in World.Cars.Values)
                if (Nodes.GetValueOrDefault(m.Vehicle) is { } n)
                {
                    if (_poses.GetValueOrDefault(m) != m.PoseVersion)
                    {
                        _poses[m] = m.PoseVersion;
                        if (!(Predictor is { Ready: true } && m == World.Own && !World.OwnFromSnapshots)) n.SetPoses(m.PrevRot, m.PrevPos, m.Rot, m.Pos);
                    }
                    n.Visible = m.Present;
                    // the engine loop follows the car's ignition (the server's engine events are not sent): a car whose engine
                    // stopped goes quiet, and sounds again when it has started
                    bool on = (m.Vehicle.Flags & I76.Sim.Vehicles.VehicleFlags.EngineOn) != 0;
                    if (_engineOn.TryGetValue(m.Vehicle, out var was) && was != on)
                    {
                        if (on && (m.Vehicle.Flags & I76.Sim.Vehicles.VehicleFlags.Dead) == 0) n.RestartEngine();
                        else if (!on) n.StopEngine();
                    }
                    _engineOn[m.Vehicle] = on;
                }
            if (_poses.Count > World.Cars.Count + 16) foreach (var gone in _poses.Keys.Where(k => !World.Cars.ContainsValue(k)).ToList()) _poses.Remove(gone);
            return World.Alpha;
        }

        /// <summary>The server's sounds on cars (collisions, landings; the player's own misfires), as SimDriver plays a local game's.</summary>
        public void Play(NetEvent e, I76.Game.Assets.AudioBank audio)
        {
            switch (e)
            {
                case SoundEvent s when World.Car(s.Car) is { } v && Nodes.GetValueOrDefault(v) is { } n:
                    if (s.Car == World.OwnSerial) n.OneShot(audio.Get(s.Name));
                    else if (s.Name != "vland.wav") n.Voice(audio.Get(s.Name));
                    break;
                case MisfireEvent m when m.Car == World.OwnSerial && World.Own is { } me && Nodes.GetValueOrDefault(me.Vehicle) is { } mn:
                    mn.OneShotSingle("wmiss.wav", audio.Get("wmiss.wav"));
                    break;
            }
        }
    }
}
