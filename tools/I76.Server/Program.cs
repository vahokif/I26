using System.Globalization;
using I76.Net;
using I76.Server;

// The dedicated network melee server (docs/multiplayer.md):
//
//   i26-server [--install DIR] [--game i76|nitro] --arena m05 --port 7676
//       [--rate 20] [--max 32] [--autostart] [--start-when N] [--ai N] [--bots-ai N] [--bot-skill easy|normal|hard] [--teams] [--max-score S] [--time-limit SECONDS] [--name NAME] [--seed N]
//       [--kind combat|race|ctf] [--goal laps|captures|points|kills] [--goal-value N|unlimited] [--ctf-teams 2-4]
//       (a Nitro arena names its kind: N35.RAC is a race, N40.CF2 a 2-team CTF; --arena N35.RAC)
//       [--anet HOST[:PORT]]...   list the game on that anet game server (port 21157), as I26's species; repeatable.
//                                 Players then find it in the INTERNET roster and join through the server's hole punch.
//       [--password WORD]         the game needs this password to join (blank or none: open). It is listed as locked, never sent.
//       [--restrict a,b,c]        WEAPON RESTRICTIONS (docs/hidden-content.md): weapons no car may bring, by all, a group (slugs,
//                                 mortars, missiles, flames, droppers), a .gdf (gchades, tdumb) or class:tier; a join whose car
//                                 carries one is refused, naming them. Hidden weapons are named the same way (gchades, gscherub...).
//       [--turrets]               Unlim. Turrets: cars may carry several turrets, each on a roof mount (off: a joining car with a second one is refused, a bot loses the extras).
//       [--no-cb]                 the CB radio (voice chat) is off in this game: no voice is relayed and the clients do not open a microphone (on by default).
//       [--coop-damage 0.5|0.75|1|1.5]  with --coop: the players' cars take that times the damage (1, the original's, by default).
//       [--coop-scale 0-3]        with --coop: each enemy car the mission sets going gets a copy per player past the first, up to that many (0, off, by default).
//       [--coop-rolling-start]    with --coop: a respawn or a joiner beside a teammate driving along comes in at the teammate's speed (off: standing still).
//       [--no-car-reset]          the reset-upright key (U) does not work in this game (on by default, as the original's F12 trick worked online).
//       [--crosshair]             the chase crosshair (an aiming aid the original does not have) may show for players who have it on (off by default).
//       [--no-ignition-off]       the engine stays on: the ignition key (S) starts an engine but never switches one off in this game (off by default).
//       [--hidden]                Hidden Weapons & Cars: the complete hidden models and weapons may play (off: a car with any is refused).
//       [--invite-only]           INVITE ONLY: each join waits for an answer on this console ("Let NAME join the game?": y, n, or
//                                 s to ask about the next first); one unanswered for 30 s is turned away, as the original's.
//       [--share-maps | --no-share-maps] [--map-max-mb 16] [--map-rate 256]
//                                 a custom arena (one from addon\ or a map pack, not a stock one) is sent to a joining player
//                                 who lacks it (on by default): at most that many MiB, at that many KiB/s to each player.
//
// With no --install and no I76_INSTALL it finds the game as I26 does (GameDiscovery: next to the executable, i76/ and
// nitro/ there and in the user folder, GOG installers, which it unpacks the same way); --game picks one when both exist.
// Logs joins, leaves, kills and scores; a line on stdin is said as the server's chat, except the commands: kick NAME|SLOT
// (boots a player), ban NAME|SLOT / unban NAME / bans (boots and refuses rejoins for the rest of the game), players (lists them), y / n / s (an invite-only join), say TEXT, help. Ctrl+C stops it.
//       [--rotate a,b,c]  the arenas the rounds cycle through (the names --arena takes, custom maps too); without it every round
//                         is the same arena. [--once] the server exits after one melee. [--round-pause 15] seconds of standings.
// When a melee is over the clients see the standings for the pause, then the server starts the next round: the players still
// connected follow with their seats (name, car, team), the scores start again, and an anet listing is kept up to date.
//       [--upload MBIT]   the upload the game may use: each client's snapshots get its share of it, less its events; without
//                         it each client gets one datagram a tick (docs/multiplayer.md, "How many players")
//       [--bot-car N=MODEL[:VARIANT]]...  the car of bot seat N (1 is the first; repeatable): MODEL a car's file stem (vppirna, vcgcour; the
//                         Nitro Pack's are the same stems), VARIANT its number (1 by default; a saved variant of the host's counts), or a car
//                         file (vppirna2.vcf), or random. Seats without one roll a stock car. The game's rules still apply: restricted weapons are
//                         taken off the car, a hidden model needs --hidden. [--bot-cars-file PATH] the same, a line "N=MODEL[:VARIANT]" each (# comments).
//       [--bots-ai N] [--bot-skill easy|normal|hard]  bots: N players the server drives by the game's mode (combat: attack the nearest
//                         enemy, race: drive the checkpoints, CTF: raid, defend, escort), each with a name, a car, a team, a score
//                         and a respawn as a player's. They take the seats free under --max and leave as humans fill them
//                         (docs/multiplayer.md, "Bots"). Not --ai (the fill, which attacks the nearest human and ignores the
//                         mode) and not --bots (headless clients for load tests).
//       [--bots N] [--bots-lag ms[,loss%[,jitter]]]  testing: N headless players from this process (half hunting, half roaming),
//                         through a lag proxy if given; they take seats like anyone (raise --max for them).
//                         In a co-op game they are teammates: they hunt the enemy team and keep up with the lead.
//                         i26-server --bots-to PORT --bots N [--rate R]: only the players, joining a game on this machine's PORT.
//   i26-server --coop MISSION [--max 8] [--port 7676] ...   a co-op story mission (t01, s03, p05, ...) for up to eight players instead
//                         of a melee (docs/multiplayer.md "Co-op over the network"): the server runs the mission's script, each
//                         joiner takes a seat in the mission's own car, and the game ends with the mission (no rounds).
//                         The players gather in a lobby first (docs/multiplayer.md "The co-op lobby"): each picks a car and marks ready, and the
//                         server starts the mission when every player there is ready and at least --start-when N (1) are. The console's
//                         start forces it. --autostart has no lobby: the first player's first input starts the mission, as before protocol 20.

var a = args.ToList();
string? Arg(string name) => a.IndexOf(name) is >= 0 and var i && i + 1 < a.Count ? a[i + 1] : null;
int IntArg(string name, int d) => Arg(name) is { } s ? int.Parse(s, CultureInfo.InvariantCulture) : d;
if (a.Contains("--help") || a.Contains("-h"))
{
    Console.WriteLine("i26-server [--install DIR] [--game i76|nitro] [--coop MISSION] [--arena m05] [--port 7676] [--max 32] [--autostart] [--start-when N] [--ai N] [--teams] [--max-score S] [--time-limit SECONDS] [--name NAME] [--kind combat|race|ctf] [--goal laps|captures|points|kills] [--goal-value N|unlimited] [--ctf-teams 2-4] [--rate 20|24|25|28|30|40|60] [--upload MBIT] [--seed N] [--password WORD] [--restrict a,b,c] [--hidden] [--bot-car N=MODEL[:VARIANT]]... [--bot-cars-file PATH] [--invite-only] [--rotate a,b,c] [--once] [--round-pause 15] [--anet HOST[:PORT]]... [--share-maps|--no-share-maps] [--no-car-reset] [--no-cb] [--crosshair] [--coop-damage F] [--coop-scale N] [--coop-rolling-start] [--no-ignition-off] [--map-max-mb 16] [--map-rate 256]");
    return 0;
}
// testing: only the headless players, joining a game on this machine (a listen server's: --bots-to PORT --bots N)
if (Arg("--bots-to") is { } botsTo)
{
    var rateTo = I76.Sim.Session.SimRate.FromLabel(IntArg("--rate", I76.Sim.Session.SimRate.Default.Label));
    using var only = new I76.ServerTool.Bots(int.Parse(botsTo, CultureInfo.InvariantCulture), IntArg("--bots", 8), rateTo.TickSeconds, Arg("--bots-lag"),
        m => Console.WriteLine($"{DateTime.Now:HH:mm:ss} {m}"));
    var stop = new ManualResetEventSlim();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Set(); };
    using var term = System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGTERM, c => { c.Cancel = true; stop.Set(); });
    stop.Wait();
    return 0;
}
var install = Arg("--install") ?? Environment.GetEnvironmentVariable("I76_INSTALL");
if (install is not null)
{
    if (!Directory.Exists(install)) { Console.Error.WriteLine($"no such folder: {install}"); return 2; }
}
else
{
    I76.Formats.Installers.GogGame? want = Arg("--game")?.ToLowerInvariant() switch
    {
        null => null, "i76" => I76.Formats.Installers.GogGame.I76, "nitro" => I76.Formats.Installers.GogGame.Nitro,
        _ => throw new ArgumentException("--game is i76 or nitro"),
    };
    install = FindGame(want);
    if (install is null) return 2;
}
// a co-op story mission instead of a melee: its stem is the "arena" the game is built and listed by
var coop = Arg("--coop") is { } cm ? RoundPlan.Normalize(cm).ToLowerInvariant() : null;
// the default arena: m05, or on an install without it (the Nitro Pack alone) the first Nitro combat arena
var arena = coop ?? Arg("--arena") ?? DefaultArena(install);
IReadOnlyList<string> rotation = Arg("--rotate") is { } rot ? RoundPlan.ParseRotation(rot) : [];
// a rotation without --arena starts on its first entry
if (Arg("--arena") is null && rotation.Count > 0) arena = rotation[0];
var probe = new I76.Formats.Vfs.GameInstall(install);
foreach (var r in rotation.Append(arena))
    if (probe.FindMission(RoundPlan.Normalize(r)) is null) { Console.Error.WriteLine($"no arena {r} in {install}"); return 2; }
var (arenaKind, arenaTeams) = ServerOptions.KindOf(arena);
var restrictions = I76.Sim.Trip.WeaponRestrictions.None;
if (Arg("--restrict") is { } spec)
{
    var parsed = I76.Sim.Trip.WeaponRestrictions.Parse(spec, new I76.Formats.Vfs.AssetVfs(probe), out var bad);
    if (parsed is null) { Console.Error.WriteLine($"--restrict: {bad}"); return 2; }
    restrictions = parsed;
}
// the bots' chosen cars: --bot-car N=MODEL[:VARIANT] (N from 1), or a file of those lines
var botChoices = new List<string>();
foreach (var line in a.Select((x, i) => (x, i)).Where(t => t.x == "--bot-car" && t.i + 1 < a.Count).Select(t => a[t.i + 1])
             .Concat(Arg("--bot-cars-file") is { } bf ? File.ReadAllLines(bf).Select(l => l.Split('#')[0]) : []))
{
    if (line.Trim().Length == 0) continue;
    var kv = line.Split('=', 2);
    if (kv.Length != 2 || !int.TryParse(kv[0].Trim(), out int seat) || seat < 1 || seat > Protocol.MaxSeats
        || (MeleeHost.ParseCarSpec(kv[1]) is null && kv[1].Trim().ToLowerInvariant() is not ("random" or "-" or "")))
    { Console.Error.WriteLine($"--bot-car: \"{line.Trim()}\" is not N=MODEL[:VARIANT] (N from 1 to {Protocol.MaxSeats})"); return 2; }
    while (botChoices.Count < seat) botChoices.Add("");
    botChoices[seat - 1] = kv[1].Trim();
}
var o = new ServerOptions
{
    Kind = Arg("--kind") is { } k ? Enum.Parse<I76.Sim.Missions.MeleeKind>(k, true) : arenaKind,
    Goal = Arg("--goal") is { } g ? Enum.Parse<I76.Sim.Missions.MeleeGoal>(g, true) : I76.Sim.Missions.MeleeGoal.None,
    GoalValue = Arg("--goal-value") is "unlimited" or "none" ? 0 : IntArg("--goal-value", 0),
    CtfTeams = Math.Clamp(IntArg("--ctf-teams", arenaTeams), 2, 4),
    Install = install,
    Arena = arena,
    Coop = coop,
    // a co-op game gathers its players in a lobby and starts when they are all ready (--start-when N players or more); --autostart keeps the first input starting it
    Lobby = coop is not null && !a.Contains("--autostart"),
    LobbyMinPlayers = Math.Clamp(IntArg("--start-when", 1), 1, Protocol.MaxCoopSeats),
    Rotation = coop is null ? rotation : [],
    Rounds = coop is null && !a.Contains("--once"),
    RoundPause = Math.Clamp(IntArg("--round-pause", 15), 1, 255),
    Port = IntArg("--port", Protocol.DefaultPort),
    // 32 by default (about 4.5 Mbit/s of upload at 20 Hz), 64 at most (about 15, or --upload's limit): docs/multiplayer.md, "How many players"
    MaxPlayers = coop is not null ? Math.Clamp(IntArg("--max", 4), 1, Protocol.MaxCoopSeats) : Math.Clamp(IntArg("--max", 32), 1, Protocol.MaxSeats),
    // the upload the game may use (Mbit/s): each client's snapshots get its share (docs/multiplayer.md, "The snapshot budget")
    UploadBytesPerSecond = (long)(Math.Max(0, double.Parse(Arg("--upload") ?? "0", CultureInfo.InvariantCulture)) * 1e6 / 8),
    Ai = Math.Max(0, IntArg("--ai", 0)),
    Bots = Math.Clamp(IntArg("--bots-ai", 0), 0, Protocol.MaxSeats),
    BotChoices = botChoices,
    BotSkill = Arg("--bot-skill") is { } bs ? Enum.Parse<I76.Sim.Missions.BotSkill>(bs, true) : I76.Sim.Missions.BotSkill.Normal,
    TeamPlay = a.Contains("--teams"),
    MaxScore = IntArg("--max-score", 0),
    TimeLimit = IntArg("--time-limit", 0),
    // srand(time(NULL)) as the original, unless --seed N fixes it (a run to reproduce)
    Seed = IntArg("--seed", unchecked((int)I76.Sim.Rng.RandSeed.FromClock())),
    Name = Arg("--name") ?? (coop is not null ? $"I76 co-op {coop}" : "I76 melee"),
    Password = Arg("--password") ?? "",
    Restrictions = restrictions,
    AllowHidden = a.Contains("--hidden"),
    AllowTurrets = a.Contains("--turrets"),
    AllowCarReset = !a.Contains("--no-car-reset"),
    AllowCb = !a.Contains("--no-cb"),
    AllowCrosshair = a.Contains("--crosshair"),
    CoopDamage = Arg("--coop-damage") is { } cd ? I76.Sim.Missions.CoopSetup.ParseDamageFactor(cd) ?? throw new ArgumentException($"--coop-damage {cd}: one of 0.5, 0.75, 1, 1.5") : 1f,
    CoopScale = Math.Clamp(IntArg("--coop-scale", 0), 0, I76.Sim.Missions.CoopSetup.MaxExtraEnemies),
    CoopRollingStart = a.Contains("--coop-rolling-start"),
    IgnitionStaysOn = a.Contains("--no-ignition-off"),
    InviteOnly = a.Contains("--invite-only"),
    Rate = I76.Sim.Session.SimRate.FromLabel(IntArg("--rate", I76.Sim.Session.SimRate.Default.Label)),
    ShareMaps = !a.Contains("--no-share-maps"),
    MapMaxBytes = (long)Math.Clamp(IntArg("--map-max-mb", 16), 1, 256) << 20,
    MapBytesPerSecond = Math.Clamp(IntArg("--map-rate", MapTransferLimits.DefaultBytesPerSecond / 1024), 8, 100_000) * 1024,
};
o = o with { MapTotalBytesPerSecond = Math.Max(o.MapBytesPerSecond, MapTransferLimits.DefaultTotalBytesPerSecond) };
void Log(string m) => Console.WriteLine($"{DateTime.Now:HH:mm:ss} {m}");
Log($"{o.Name}: building {o.Arena} from {o.Install}, seed {o.Seed}");
var sw = System.Diagnostics.Stopwatch.StartNew();
using var server = new NetMeleeServer(o);
server.Log += Log;
if (rotation.Count > 0) Log($"rounds rotate through {string.Join(", ", rotation)}");
if (coop is not null)
    Log($"co-op mission {coop} ready in {sw.ElapsedMilliseconds} ms: {server.Host.Setup.World.Vehicles.Count} cars; listening on UDP {server.Port}, up to {o.MaxPlayers} players" +
        $"{(o.Password.Length > 0 ? ", password-protected" : "")}; " +
        (o.Lobby ? $"the lobby starts the mission when {(o.LobbyMinPlayers > 1 ? $"{o.LobbyMinPlayers} or more players are" : "the players here are")} all ready (the console's start forces it)" : "the mission starts with the first player"));
else Log($"{o.Arena} ready in {sw.ElapsedMilliseconds} ms: {server.Host.Setup.World.Vehicles.Count - server.Host.Bots.Count} AI cars{(server.Host.Bots.Count > 0 ? $", {server.Host.Bots.Count} bots" : "")}, {server.Host.Game.Spawns.Count} spawn points; " +
    $"listening on UDP {server.Port}, up to {o.MaxPlayers} players{(o.TeamPlay ? ", teams" : "")}{(o.MaxScore > 0 ? $", to {o.MaxScore} points" : "")}{(o.TimeLimit > 0 ? $", {o.TimeLimit} s" : "")}{(o.Password.Length > 0 ? ", password-protected" : "")}{(o.Restrictions.IsEmpty ? "" : $", restricted weapons {o.Restrictions}")}{(o.AllowHidden ? ", hidden weapons and cars allowed" : "")}");
if (server.MapNote is { } mapNote) Log(mapNote);
var anet = new List<(string, int)>();
for (int i = 0; i < a.Count - 1; i++)
{
    if (a[i] != "--anet") continue;
    if (!I76.Net.Anet.AnetClient.TryParse(a[i + 1], out var ah, out var ap)) { Console.Error.WriteLine($"--anet: no address: {a[i + 1]}"); return 2; }
    anet.Add((ah, ap));
}
if (anet.Count > 0)
{
    server.ListOn(anet);
    Log($"listing the game on {string.Join(", ", anet.Select(x => x.Item2 == I76.Net.Anet.AnetWire.ServerPort ? x.Item1 : $"{x.Item1}:{x.Item2}"))}");
}
string? lastAnet = null;
var done = new ManualResetEventSlim();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; done.Set(); };
// a service stop (SIGTERM), a closed terminal or console window (SIGHUP; CTRL_CLOSE on Windows) end the same way,
// so the game is withdrawn from the anet servers and the clients are told, rather than left listed until they time out
using var onTerm = System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGTERM, c => { c.Cancel = true; done.Set(); });
using var onHup = System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGHUP, c => { c.Cancel = true; done.Set(); });
server.Start();
// testing: headless players from this process (--bots N [--bots-lag ms[,loss%[,jitter]]]), counted against --max
using var bots = IntArg("--bots", 0) is > 0 and var nb ? new I76.ServerTool.Bots(server.Port, nb, server.Host.Session.TickSeconds, Arg("--bots-lag"), Log) : null;
// an invite-only game asks the console about each join ("y", "n" or "s"); unanswered for 30 s, the join is turned away
if (o.InviteOnly) Log("invite only: each join is asked about here; answer y (let in), n (turn away) or s (ask about the next first)");
server.InviteAsked += name => Log($"*** Let {name} join the game? (y / n / s; turned away in {I76.Net.Protocol.InviteSeconds:0} s)");
_ = Task.Run(() =>
{
    while (!done.IsSet && Console.ReadLine() is { } line)
    {
        var t = line.Trim();
        if (t.Length > 0 && !Command(t)) server.Chat(t);
    }
});

// the console's commands (a leading / is optional); any other line is the server's chat
bool Command(string t)
{
    var parts = t.TrimStart('/').Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
    string cmd = parts.Length > 0 ? parts[0].ToLowerInvariant() : "", arg = parts.Length > 1 ? parts[1].Trim() : "";
    switch (cmd)
    {
        case "kick" or "boot" when arg.Length > 0:
            Log(server.Kick(arg) ?? $"booted {arg}");
            return true;
        case "ban" when arg.Length > 0:
            Log(server.Ban(arg) ?? $"banned {arg} for the rest of the game");
            return true;
        case "ban" or "bans" when arg.Length == 0:
            Log(server.Banned.Count == 0 ? "nobody is banned" : "banned: " + string.Join(", ", server.Banned));
            return true;
        case "unban" when arg.Length > 0:
            Log(server.Unban(arg) ?? $"lifted the ban on {arg}");
            return true;
        case "cbmute" or "cbunmute" when arg.Length > 0:
            Log(server.CbMute(arg, cmd == "cbmute") ?? (cmd == "cbmute" ? $"muted {arg} on the CB for everyone" : $"unmuted {arg} on the CB"));
            return true;
        case "cbmute" when arg.Length == 0:
            Log(server.CbMuted.Count == 0 ? "nobody is muted on the CB" : "muted on the CB: " + string.Join(", ", server.CbMuted));
            return true;
        case "players" or "who" when arg.Length == 0:
            foreach (var lp in server.LobbyPlayers) Log($"  lobby  {lp.Name}  {lp.Car}{(lp.Ready ? "  ready" : "")}");
            foreach (var row in server.Invoke(h => h.Seats.Select(x => $"  {x.Slot,2}  {x.Name}  {x.Actor.Score} points, {x.Actor.Kills} kills, {x.Actor.Deaths} deaths, {x.Rtt} ms{(x.Actor.Destroyed ? ", dead" : "")}").Concat(h.Bots.Select(b => $"  {b.Slot,2}  {b.Name}  {b.Player.Score} points, {b.Player.Kills} kills, {b.Player.Deaths} deaths, bot{(b.Player.Destroyed ? ", dead" : "")}")).ToList())) Log(row);
            return true;
        case "y" or "yes" or "n" or "no" or "s" or "skip" when arg.Length == 0 && server.InvitesWaiting > 0:
            var answer = cmd[0] == 'y' ? I76.Server.InviteAnswer.Accept : cmd[0] == 'n' ? I76.Server.InviteAnswer.Decline : I76.Server.InviteAnswer.Skip;
            if (server.AnswerInvite(answer) is null) Log("nobody is waiting to join");
            return true;
        case "start" when arg.Length == 0 && server.InLobby:
            Log(server.StartLobby(force: true) ?? "the mission starts");
            return true;
        case "say" when arg.Length > 0:
            server.Chat(arg);
            return true;
        case "help" when t.StartsWith('/') || arg.Length == 0 && parts.Length == 1:
            Log("commands: kick NAME|SLOT, ban NAME|SLOT / unban NAME / bans (refused rejoins for the rest of the game), cbmute NAME / cbunmute NAME (a player's CB voice, for everyone), start (a co-op lobby), players, y / n / s (an invite-only join), say TEXT; any other line is chat");
            return true;
    }
    if (t.StartsWith('/')) { Log($"unknown command {t}; /help lists them"); return true; }
    return false;
}
while (!done.Wait(1000))
{
    if (server.Fault is not null) { Log("the server stopped on an error (above)"); return 1; }
    if (server.AnetStatus is { } st && st != lastAnet) { Log($"anet: {st}"); lastAnet = st; }
    if (!server.CarriesOn && server.OverAt is { } over && System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency - over > 20) break;
}
Log("stopping");
return 0;

static string? FindGame(I76.Formats.Installers.GogGame? want)
{
    var exe = Environment.ProcessPath;
    var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "I26");
    var (installs, folders) = I76.Formats.Installers.GameDiscovery.Places(exe, local);
    installs.AddRange(I76.Formats.Installers.GogLibrary.FindOnSystem(Console.Error.WriteLine));
    var games = I76.Formats.Installers.GameDiscovery.Discover(installs, folders);
    var game = I76.Formats.Installers.GameDiscovery.Pick(games, want, out var error);
    if (game is null)
    {
        Console.Error.WriteLine($"{error}. Give --install DIR or set I76_INSTALL, or put the game where I26 looks:");
        foreach (var f in installs.Where(f => !string.IsNullOrEmpty(f)).Distinct()) Console.Error.WriteLine($"  {f}");
        foreach (var f in folders.Where(f => !string.IsNullOrEmpty(f)).Distinct()) Console.Error.WriteLine($"  {f}  (GOG installers: setup_*.exe)");
        return null;
    }
    Console.WriteLine($"{DateTime.Now:HH:mm:ss} game: {game.Title}{(games.Count > 1 && want is null ? " (both found; --game nitro for the Nitro Pack)" : "")}");
    if (game.InstallDir is { } dir) { I76.Formats.Installers.ExeRepair.Repair(dir, folders, Console.WriteLine); return dir; }
    // only the offline installer: unpack it where the game would (next to the executable, else the user folder)
    try
    {
        var installer = I76.Formats.Installers.GogInstaller.Open(game.InstallerPath!);
        var target = I76.Formats.Installers.ExtractionPlaces.Choose(game.Game, exe, local, game.InstallerBytes);
        Console.WriteLine($"{DateTime.Now:HH:mm:ss} unpacking {Path.GetFileName(game.InstallerPath)} into {target} (once)");
        var last = 0L;
        installer.ExtractTo(target, p =>
        {
            var now = Environment.TickCount64;
            if (now - last < 5000) return;
            last = now;
            Console.WriteLine($"{DateTime.Now:HH:mm:ss} unpacking ...");
        });
        return target;
    }
    catch (Exception ex) when (ex is I76.Formats.Installers.InstallerException or IOException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine($"could not unpack {game.InstallerPath}: {ex.Message}\nRun I26 once to unpack it, or give --install DIR.");
        return null;
    }
}

static string DefaultArena(string install)
{
    var gi = new I76.Formats.Vfs.GameInstall(install);
    if (gi.FindMission("m05") is not null) return "m05";
    return gi.ListMissions(".cbt").Select(Path.GetFileName).FirstOrDefault() ?? "m05";
}
