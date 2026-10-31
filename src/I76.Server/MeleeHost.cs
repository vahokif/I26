using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using I76.Formats.Assembly;
using I76.Formats.Mission;
using I76.Formats.Vfs;
using I76.Net;
using I76.Sim.Collision;
using I76.Sim.Damage;
using I76.Sim.Missions;
using PartsDatabase = I76.Sim.Trip.PartsDatabase;
using CarConfigCheck = I76.Sim.Trip.CarConfigCheck;
using I76.Sim.Rng;
using I76.Sim.Session;
using I76.Sim.Vehicles;
using I76.Sim.Weapons;

namespace I76.Server;

/// <summary>What a network game is played as: a melee on an arena, or a co-op story mission (<see cref="Coop"/>).</summary>
public sealed record ServerOptions
{
    public required string Install { get; init; }
    public string Arena { get; init; } = "m05";
    /// <summary>
    /// A co-op story mission (its file stem: t01, p05; docs/multiplayer.md "Co-op over the network"), played by up to four
    /// (<see cref="MaxPlayers"/>, at most 4) in place of a melee on <see cref="Arena"/>; null for a melee. There are no rounds.
    /// </summary>
    public string? Coop { get; init; }
    public int Port { get; init; } = Protocol.DefaultPort;
    public int MaxPlayers { get; init; } = 8;
    /// <summary>AI cars filling the arena (the roster cycles through <see cref="AiCars"/>).</summary>
    public int Ai { get; init; }
    public IReadOnlyList<string> AiCars { get; init; } = ["vppirna1.vcf", "vcgcour1.vcf", "valepre1.vcf"];
    public bool TeamPlay { get; init; }
    /// <summary>
    /// Bots: players the server drives by the game's mode (docs/multiplayer.md "Bots"; the host form's Bots, i26-server --bots-ai).
    /// They take the seats humans leave free: up to this many, fewer as humans join a game that would be over its
    /// <see cref="MaxPlayers"/>, back as they leave. Not the <see cref="Ai"/> fill.
    /// </summary>
    public int Bots { get; init; }
    /// <summary>The bots' driving and shooting.</summary>
    public BotSkill BotSkill { get; init; } = BotSkill.Normal;
    /// <summary>The cars the bots drive (each takes one at random); those this install lacks, or the game's restrictions forbid, are left out.</summary>
    public IReadOnlyList<string> BotCars { get; init; } = ["vppirna1.vcf", "vcgcour1.vcf", "valepre1.vcf", "vastryd1.vcf", "vdstag1.vcf", "vmxmarx1.vcf", "vfypony1.vcf", "vcroyal1.vcf", "vazamz1.vcf"];
    /// <summary>
    /// The car each bot seat drives (docs/multiplayer.md "Bots"): entry N is for the Nth bot seat, a car file (<c>vppirna2.vcf</c>, a stock
    /// variant or one of the host's saved ones), a <c>MODEL[:VARIANT]</c> spec, or empty for a roll; seats past the list roll. The rules still
    /// apply (<see cref="MeleeHost.BotChoice"/>).
    /// </summary>
    public IReadOnlyList<string> BotChoices { get; init; } = [];
    /// <summary>The respray a bot's car wears: the game supplies it from the saved variant's sidecar (<c>CarSidecar</c>). Null, or null for a car, is the factory paint.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public Func<string, I76.Formats.Imaging.CarPaint?>? PaintOfCar { get; init; }
    /// <summary>Combat (the base melee), the Nitro Pack's race (a .rac arena) or capture the flag (a .cf2/.cf3/.cf4 arena, which forces teams).</summary>
    public MeleeKind Kind { get; init; }
    /// <summary>What the goal value counts; None takes the kind's own (laps, captures) or points.</summary>
    public MeleeGoal Goal { get; init; }
    /// <summary>The goal's limit (laps, captures, points or kills); 0 falls back to <see cref="MaxScore"/>.</summary>
    public int GoalValue { get; init; }
    /// <summary>CTF: how many teams play (2–4).</summary>
    public int CtfTeams { get; init; } = 2;
    public int MaxScore { get; init; }
    /// <summary>The simulation rate every player runs at (docs/frame-rate.md); sent in the join accept.</summary>
    public I76.Sim.Session.SimRate Rate { get; init; } = I76.Sim.Session.SimRate.Default;

    /// <summary>The kind an arena file is: .rac a race, .cf2/.cf3/.cf4 CTF with 2–4 teams, else combat.</summary>
    public static (MeleeKind Kind, int Teams) KindOf(string arena) => Path.GetExtension(arena).ToLowerInvariant() switch
    {
        ".rac" => (MeleeKind.Race, 2),
        ".cf2" => (MeleeKind.Ctf, 2),
        ".cf3" => (MeleeKind.Ctf, 3),
        ".cf4" => (MeleeKind.Ctf, 4),
        _ => (MeleeKind.Combat, 2),
    };
    public float TimeLimit { get; init; }
    public float RespawnDelay { get; init; } = 5f;
    /// <summary>The game's rand stream starts here (srand), and the poems are dealt from it; the servers' command lines give the clock unless --seed N.</summary>
    public int Seed { get; init; } = 1;
    /// <summary>The car a join gets when the one it asks for is not in the server's install.</summary>
    public string DefaultCar { get; init; } = "vppirna1.vcf";
    /// <summary>The game's name: what the join form's list shows (the host form's GAME NAME), and the logs.</summary>
    public string Name { get; init; } = "I76 melee";
    /// <summary>
    /// The host form's INVITE ONLY: every join waits for the host's answer ("Let %s join the
    /// game?"), and one unanswered for <see cref="Protocol.InviteSeconds"/> is turned away.
    /// A listen host answers from its game (Ctrl+Y / Ctrl+N / Ctrl+H); i26-server on its console (y / n / s).
    /// </summary>
    public bool InviteOnly { get; init; }
    /// <summary>The server is a listen host's: the first join of its life is the host's own, let in without asking.</summary>
    public bool ListenHost { get; init; }
    /// <summary>
    /// A co-op game gathers its players in a lobby first (docs/multiplayer.md "The co-op lobby"): they pick their cars and mark ready, and
    /// the host starts the mission (a listen host), or the server does once <see cref="LobbyMinPlayers"/> or more are all ready (i26-server).
    /// Off, the first player's first input starts the mission and later players come in where it is (the protocol 15 behaviour).
    /// </summary>
    public bool Lobby { get; init; }
    /// <summary>A lobby without a host (a dedicated server) starts the mission when at least this many players are all ready.</summary>
    public int LobbyMinPlayers { get; init; } = 1;
    /// <summary>After a lobby's start the mission waits this long (seconds) for every player's game to be loaded; one still loading then plays in as a late joiner does.</summary>
    public double LoadSeconds { get; init; } = 120;
    /// <summary>How long an invite-only join waits for the host's answer before it is turned away (the original's 30 s; tests shorten it).</summary>
    public double InviteSeconds { get; init; } = Protocol.InviteSeconds;
    /// <summary>The game's password (protocol 12); blank is an open game. Never saved, never listed: the listing only says it is locked.</summary>
    public string Password { get; init; } = "";

    /// <summary>The record's own text would print every member, the password too: this leaves it out.</summary>
    public override string ToString() => $"ServerOptions {{ Name = {Name}, Arena = {Arena}, Port = {Port}, MaxPlayers = {MaxPlayers}, Locked = {Password.Length > 0} }}";
    /// <summary>
    /// A custom arena's files go to a joining client that lacks them (docs/multiplayer.md "Custom maps"; the host form's
    /// Share custom maps, i26-server --share-maps / --no-share-maps). Stock data is never sent either way.
    /// </summary>
    public bool ShareMaps { get; init; } = true;
    /// <summary>A custom map bigger than this is not shared (--map-max-mb).</summary>
    public long MapMaxBytes { get; init; } = MapFileSet.DefaultMaxBytes;
    /// <summary>The map's bytes a second to one downloading client (--map-rate, KiB/s), and to all of them together.</summary>
    public int MapBytesPerSecond { get; init; } = MapTransferLimits.DefaultBytesPerSecond;
    public int MapTotalBytesPerSecond { get; init; } = MapTransferLimits.DefaultTotalBytesPerSecond;
    /// <summary>
    /// The melee carries on when it ends (docs/multiplayer.md "Rounds"): after <see cref="RoundPause"/> seconds, the clients
    /// still connected follow into a new round. Off: the game ends (the console server exits, as with --once).
    /// </summary>
    public bool Rounds { get; init; }
    /// <summary>How long the clients see the standings before the next round starts, seconds (at most 255).</summary>
    public double RoundPause { get; init; } = 15;
    /// <summary>The arenas the rounds cycle through (--rotate); empty: the same arena again.</summary>
    public IReadOnlyList<string> Rotation { get; init; } = [];
    /// <summary>Tells this game from others in a session list (a LAN game answers both its LAN and loopback queries).</summary>
    public ulong SessionId { get; init; } = (ulong)Random.Shared.NextInt64();
    /// <summary>
    /// The weapons no joiner's car may carry (the Nitro host form's WEAPON RESTRICTIONS, --restrict; docs/hidden-content.md).
    /// The server checks every car against them and refuses a join that breaks them, naming the weapons.
    /// </summary>
    public I76.Sim.Trip.WeaponRestrictions Restrictions { get; init; } = I76.Sim.Trip.WeaponRestrictions.None;
    /// <summary>The host's Hidden Weapons &amp; Cars (--hidden): the complete hidden models and weapons are allowed. Off, a car with any is refused.</summary>
    public bool AllowHidden { get; init; }
    /// <summary>The host's Unlim. Turrets (--turrets): a car may carry several turrets, each on a roof mount. Off, a joining car with a second one is refused and a bot's car loses the extras.</summary>
    public bool AllowTurrets { get; init; }
    /// <summary>
    /// The reset-upright key works in this game (the host form's Car Reset, i26-server's --no-car-reset turns it off): a player's
    /// car goes back on the terrain under it, upright and still. The original's F12
    /// render switch was not blocked online, so it is on by default.
    /// </summary>
    public bool AllowCarReset { get; init; } = true;
    /// <summary>
    /// The CB radio (the host form's CB Radio, i26-server's --no-cb turns it off; docs/multiplayer.md "CB voice"): off, the server relays no
    /// voice and the clients do not open a microphone. On by default; listed in <see cref="SessionInfo.Cb"/> and carried in <see cref="JoinAccept.Cb"/>.
    /// </summary>
    public bool AllowCb { get; init; } = true;
    /// <summary>Snapshots as deltas against the one each client acknowledged (protocol 13); off sends each in full (A/B, debugging).</summary>
    public bool DeltaSnapshots { get; init; } = true;
    /// <summary>Far cars sent less often (<see cref="Interest"/>; needs <see cref="DeltaSnapshots"/>); off sends every car every tick.</summary>
    public bool InterestManagement { get; init; } = true;
    /// <summary>Events only seen and heard go to the clients within range of them (<see cref="EventInterest"/>); off sends every event to everyone.</summary>
    public bool EventInterest { get; init; } = true;
    /// <summary>The own car's motion is confirmed by the hash its client sends rather than sent, when they agree (protocol 13); off sends it every snapshot.</summary>
    public bool ConfirmMotion { get; init; } = true;
    /// <summary>
    /// The host's upload for the game, bytes a second (i26-server --upload, the host form's): each client's snapshots take
    /// its share less what its events take, within one datagram. 0: one datagram a tick each, whatever that adds up to.
    /// </summary>
    public long UploadBytesPerSecond { get; init; }
    /// <summary>
    /// The game is handed to a player when the host leaves (docs/multiplayer.md "Host migration"): a client-hosted game's.
    /// Off (a dedicated server, the default) nothing of it runs.
    /// </summary>
    public bool HostMigration { get; init; }
    /// <summary>A downloaded custom map's folder to play from (a successor taking over a game on a map it downloaded); null: the install's own.</summary>
    public string? MapDir { get; init; }
}

/// <summary>The host's answer to an invite-only join (INVITE_ACCEPT, INVITE_DECLINE, INVITE_SKIP).</summary>
public enum InviteAnswer { Accept, Decline, Skip }

/// <summary>A player's place on the server: their slot, car, inputs and what was last told about them.</summary>
public sealed class Seat
{
    public required int Slot { get; init; }
    public required string Name { get; init; }
    /// <summary>The seat's player as the host and the wire see it, whatever the mode.</summary>
    public required ISeatActor Actor { get; init; }
    /// <summary>The seat's melee player (the melee mode's seats).</summary>
    public MeleePlayer Player => ((MeleeActor)Actor).Player;
    public required Driver Driver { get; init; }
    public required InputQueue Inputs { get; init; }
    /// <summary>The connection's id (the transport's), and its round trip time.</summary>
    public object? Connection { get; init; }
    public int Rtt { get; set; }
    internal (int Score, int Kills, int Deaths) Told;
    /// <summary>The car's build last told to the clients (<see cref="MeleePlayer.CarEpoch"/>).</summary>
    internal int ToldEpoch;
    /// <summary>The slow part of the motion last sent, and the snapshots left that carry it again (it changed, or a second passed).</summary>
    internal OwnMotion.SlowMotion? SlowSent;
    internal int SlowLeft, SinceSlow;
    /// <summary>The seat's chat rate limit.</summary>
    internal ChatLimiter ChatRate { get; } = new();
    /// <summary>The seat's CB voice limit (<see cref="VoiceLimiter"/>).</summary>
    internal VoiceLimiter VoiceRate { get; } = new();
    /// <summary>The players this seat does not want to hear on the CB (<see cref="VoiceIgnore"/>): their voice is not relayed to it.</summary>
    internal bool IgnoreAll;
    internal HashSet<int> Ignored { get; } = [];
    /// <summary>When the seat last asked for a new car (Reconfigure; seconds on the server's clock): one a half second is answered.</summary>
    internal double ReconfiguredAt = double.NegativeInfinity;
    /// <summary>The tick of the seat's last car query answered (<see cref="MeleeHost.AnswerCarQuery"/>), −1 for none.</summary>
    internal long CarQueryTick = -1;
    /// <summary>The snapshots sent this seat lately and its ack (delta snapshots).</summary>
    internal SnapshotEncoder? Snapshots;
    /// <summary>Its snapshot budget's priorities, and the entries its snapshots may carry (kept between ticks for their buffers).</summary>
    internal SnapshotPlanner Planner { get; } = new();
    internal EntryPlan CarPlan { get; } = new();
    internal EntryPlan RoundPlan { get; } = new();
    /// <summary>The bytes a second its events have taken lately (the snapshots' share of the upload is what is left).</summary>
    internal double EventRate;
    /// <summary>The names its events have sent once (<see cref="NameTable"/>): a new seat's client starts afresh at its accept.</summary>
    internal NameTable Names { get; } = new();
    /// <summary>The scoreboard as its client has it (the next one goes as a delta against it).</summary>
    internal Dictionary<byte, PlayerRow> Board { get; } = [];
    /// <summary>Snapshots whose motion was confirmed by the client's hash rather than sent.</summary>
    public long Confirmed;
    public int CarSerial => Actor.Car.NetId & 0xffffff;
}

/// <summary>
/// A bot's place on the server: a slot like a player's (the top slots, counting down from <see cref="Protocol.MaxSeats"/> - 1, so
/// that the lowest-free slot a human gets never meets one), the name the clients see, and the player the AI drives. No
/// connection, driver or input: nothing is sent to it.
/// </summary>
public sealed class BotSeat
{
    public required int Slot { get; init; }
    public required string Name { get; init; }
    public required MeleePlayer Player { get; init; }
    internal (int Score, int Kills, int Deaths) Told;
    public int CarSerial => Player.Car.NetId & 0xffffff;
}

/// <summary>
/// The network melee's game, without the network: the arena built from
/// the server's install (MissionSetup, as the CLI's run does), a
/// <see cref="SimSession"/> at the fixed tick, the players' seats and their
/// input queues, and each tick's events and state turned into the
/// protocol's messages. <see cref="NetMeleeServer"/> puts it on the wire;
/// the tests drive it directly too. What the game is, melee or another, is its <see cref="Mode"/>
/// (<see cref="IHostMode"/>): the host is the wire's side of it, and asks the mode for the rest.
/// </summary>
public sealed partial class MeleeHost : IModeHost
{
    public ServerOptions Options { get; }
    public AssetVfs Vfs { get; }
    public MissionSetup Setup { get; }
    public SimSession Session { get; }
    /// <summary>What the game is played as: the arena or mission, the seats' players, scoring and the end.</summary>
    public IHostMode Mode { get; }
    /// <summary>The melee mode's game (the melee host's: throws for another mode).</summary>
    public MeleeGame Game => ((MeleeMode)Mode).Game;
    public string Arena { get; }
    /// <summary>The arena's files as joining clients must have them (sizes and CRCs, in the join accept).</summary>
    public MapFingerprint? Map { get; }
    /// <summary>The arena's files when it is a custom map (null for a stock one), and their bytes when they are shared.</summary>
    public MapFileSet? MapSet { get; }
    /// <summary>What a client joining is offered before its seat (null for a stock arena: no offer, the accept at once).</summary>
    public MapOffer? MapOffer { get; }
    /// <summary>The shared map's files' bytes, in the offer's order (read once: what is sent is what was hashed).</summary>
    public IReadOnlyList<byte[]> MapData { get; } = [];
    public IReadOnlyList<Seat> Seats => _seats;
    /// <summary>The bots at the table (docs/multiplayer.md "Bots"): players the server drives, listed with the seats everywhere a roster is.</summary>
    public IReadOnlyList<BotSeat> Bots => Mode.Bots;
    public uint Tick => (uint)Session.Ticks;
    public bool Over { get; private set; }
    /// <summary>The host's own seat: a listen host's player (or the player whose game took over); null for a dedicated server.</summary>
    public int? HostSlot { get; set; }

    public event Action<string>? Log;

    private readonly List<Seat> _seats = [];
    /// <summary>How many bots the host wants (the melee mode's; 0 for a mode without).</summary>
    public int BotLimit => (Mode as MeleeMode)?.BotLimit ?? 0;
    private readonly List<NetEvent> _pending = [];
    private readonly List<(Vehicle Car, string Def)> _spawned = [];
    private readonly Dictionary<Vehicle, string> _defs = [];
    private readonly Dictionary<StaticObject, int> _structureSerial = [];
    private readonly HashSet<(Vehicle, Vehicle)> _pairs = [];

    /// <summary>The game as the options describe it: a network melee.</summary>
    public MeleeHost(ServerOptions o) : this(o, null) { }

    /// <summary>The game as <paramref name="mode"/> builds it (null: the melee's).</summary>
    public MeleeHost(ServerOptions o, HostModeFactory? mode)
    {
        Options = o;
        // a server plays from its own install: never a map a client of this process downloaded (GameInstall.SessionMapDir)
        var install = new GameInstall(o.Install) { MapDir = o.MapDir };
        Vfs = new AssetVfs(install);
        var asm = new ModelAssembler(Vfs);
        var arena = o.Coop ?? o.Arena;
        Arena = arena.EndsWith(".msn", StringComparison.OrdinalIgnoreCase) ? arena[..^4] : arena;
        // a base arena is a .msn; Nitro's are .cbt, .rac and .cf2/.cf3/.cf4 (named with the extension)
        var msn = install.FindMission(Arena) ?? throw new FileNotFoundException($"no arena miss16/{Arena} in {o.Install}");
        var world = new MissionWorld(Vfs, asm, msn);
        Map = MapFingerprint.Of(install, Arena);
        MapSet = MapFileSet.Resolve(install, Arena, Vfs);
        if (MapSet is { } set)
        {
            var data = set.Files.Select(f => File.ReadAllBytes(f.Path)).ToList();
            // a file changed between hashing and reading: hashed again from the bytes that will be sent
            if (data.Select((d, i) => System.Security.Cryptography.SHA256.HashData(d).AsSpan().SequenceEqual(set.Files[i].Sha256)).Any(ok => !ok))
            {
                var files = set.Files.Select((f, i) => f with { Size = data[i].LongLength, Sha256 = System.Security.Cryptography.SHA256.HashData(data[i]) }).ToList();
                MapSet = set = set with { Files = files, Hash = MapFileSet.HashOf(set.Mission, files.Select(f => (f.Name, f.Size, f.Sha256))) };
            }
            string? why = !o.ShareMaps ? "the host does not share maps" : set.TooBig(o.MapMaxBytes) is { } big ? $"the map is too big to send: {big}" : null;
            MapOffer = I76.Net.MapOffer.Of(set, why is null, why ?? "");
            if (why is null) MapData = data;
        }
        Mode = (mode ?? (o.Coop is not null ? c => new CoopMode(c) : c => new MeleeMode(c)))(new ModeContext(o, install, Vfs, asm, world, Arena, CarExists, Legal));
        Setup = Mode.Setup;
        Mode.Start(this);
        Session = new SimSession(Setup, SessionOptions.Network, o.Rate)
        {
            // what the arena was built from, which its snapshots carry (docs/savestate.md)
            Build = Mode.Build,
            ResetUprightAllowed = o.AllowCarReset,
        };
        if (Setup.World.Ground is IHasStatics hs)
            foreach (var so in hs.World.Objects) _structureSerial[so] = so.NetId & 0xffffff;
        _spawned.Clear();     // the AI fill is in the roster every join gets
        Mode.Opened();
        _spawned.Clear();     // and so are the bots
    }

    /// <summary>
    /// A car file the server's install can build: a plain name (letters,
    /// digits, underscores) that is there. Not the process's own overlay
    /// (<see cref="AssetVfs.Addon"/>): in a listen server that holds the
    /// host's saved variants, which no other player's game has.
    /// </summary>
    public bool CarExists(string def) =>
        Regex.IsMatch(def, @"^[A-Za-z0-9_]{1,16}\.vcf$") && !AssetVfs.Addon.ContainsKey(def) && Vfs.TryRead(def) is not null;

    private readonly Dictionary<string, byte[]> _custom = [];
    private PartsDatabase? _parts;

    /// <summary>The model stem of a car file name (vppirna3.vcf: vppirna), or null for a name that is not a plain car file.</summary>
    private static string? StemOf(string car) =>
        Regex.Match(car, @"^([A-Za-z_][A-Za-z0-9_]{0,14}?)\d+\.vcf$") is { Success: true } m ? m.Groups[1].Value : null;

    /// <summary>
    /// A join's configuration (a .vcf the client has and this install does not, or has differently): null when there is
    /// none to use, the car file name it is served as when it passes the garage's rules (CarConfigCheck), else the reason
    /// it does not. Named by its content (n&lt;hash&gt;&lt;stem&gt;.vcf), so equal cars are one file and two players' cars never meet.
    /// </summary>
    private (string? Name, string? Error) ResolveConfig(string asked, byte[]? config)
    {
        if (config is not { Length: > 0 }) return (null, null);
        if (!asked.EndsWith(".vcf")) asked += ".vcf";
        if (CarExists(asked) && Vfs.TryRead(asked) is { } mine && mine.AsSpan().SequenceEqual(config)) return (null, null);      // the install's own car
        var stem = StemOf(asked);
        if (stem is null) return (null, $"no car configuration for {asked}");
        var vcf = CarConfigCheck.Check(Vfs, _parts ??= PartsDatabase.Build(Vfs), stem, config, out var why, Options.AllowHidden, Options.AllowTurrets);
        if (vcf is null) return (null, $"{asked} refused: {why}");
        return (Serve(stem, vcf), null);
    }

    /// <summary>
    /// A car file served by its content (n&lt;hash&gt;&lt;stem&gt;.vcf): the clients are sent its bytes with every mention of the
    /// car (<see cref="CarInfo.Vcf"/>), so each can build it whether its install has it or not.
    /// </summary>
    private string Serve(string stem, byte[] vcf)
    {
        uint h = 2166136261;
        foreach (var b in vcf) h = (h ^ b) * 16777619;
        var name = $"n{h:x8}{stem}";
        name = (name.Length > 16 ? name[..16] : name) + ".vcf";
        if (!_custom.ContainsKey(name)) { _custom[name] = vcf; AssetVfs.Addon[name] = vcf; }
        return name;
    }

    /// <summary>
    /// The car a seat gets as the clients are told of it: a file of the install's archive by its name (every install has
    /// it); one on disk instead (addon\: a community car, the stock vmxmarx3, a stock car overridden there) by its content,
    /// since other installs may lack it or have another file of that name. A listen host playing such a car was a car
    /// nobody else could build, so nobody saw the host.
    /// </summary>
    private string Served(string car) =>
        !_custom.ContainsKey(car) && Vfs.Locate(car) is AssetSource.Loose && StemOf(car) is { } stem && Vfs.TryRead(car) is { } vcf ? Serve(stem, vcf) : car;

    /// <summary>
    /// The reason a join's car is refused, or null: checked before the connection is accepted. A configuration must pass the
    /// garage's rules (CarConfigCheck); then the car the player would get (their own variant, the install's car they asked
    /// for, or what stands in for it) must be a melee model with weapons the pickers offer, hidden ones only when the host
    /// allows them, and none the game's WEAPON RESTRICTIONS forbid (docs/hidden-content.md §5).
    /// </summary>
    public string? CheckJoin(string car, byte[]? config)
    {
        if (car == Protocol.MissionCar) return null;
        string asked = car.Trim().ToLowerInvariant();
        if (!asked.EndsWith(".vcf")) asked += ".vcf";
        var (custom, error) = ResolveConfig(asked, config);
        if (error is not null) return error;
        return CheckCar(custom ?? CarFor(asked), custom is null ? null : StemOf(asked));
    }

    private HashSet<string>? _offered;
    /// <summary>The model stems the melee's MAKE / MODEL list offers on the server's install.</summary>
    public HashSet<string> OfferedModels => _offered ??= I76.Formats.Shell.CarTable.OfferedStems(Vfs.Install);

    /// <summary>
    /// The rules for the car a seat gets (<paramref name="file"/>, an install car or a checked configuration served under its
    /// own name, whose model is <paramref name="stem"/>): null when it may play, else why not. <paramref name="anyTurrets"/> lets a car with several turrets by (a bot's, whose extras <see cref="Legal"/> takes off).
    /// </summary>
    public string? CheckCar(string file, string? stem = null, bool restrictions = true) => CheckCar(file, stem, restrictions, anyTurrets: false);

    private string? CheckCar(string file, string? stem, bool restrictions, bool anyTurrets)
    {
        var vcf = _custom.TryGetValue(file, out var c) ? c : Vfs.TryRead(file);
        if (vcf is null) return null;          // nothing to build: the join gets the default car, checked when it is asked for
        I76.Sim.Trip.TripCar car;
        try { car = I76.Sim.Trip.TripCar.FromVcf(vcf); }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or IndexOutOfRangeException) { return $"{file} is not a car file"; }
        var guns = car.Weapons.Select(w => w.Gdf).ToList();
        if (I76.Sim.Trip.HiddenContent.Check(Vfs, OfferedModels, Vfs.Install.IsNitro, Options.AllowHidden, stem ?? StemOf(file), guns) is { } why)
            return $"{file} refused: {why}";
        if (CarConfigCheck.Strict(Vfs, car, stem ?? StemOf(file), Options.AllowTurrets || anyTurrets) is { } strict) return $"{file} refused: {strict}";
        var bad = restrictions ? Options.Restrictions.Violations(Vfs, guns) : [];
        if (bad.Count > 0) return $"Restricted weapons: {string.Join(", ", bad.Select(g => I76.Sim.Trip.HiddenContent.WeaponName(Vfs, g)))}";
        return null;
    }

    /// <summary>
    /// The car a driver the server runs (a bot, the AI fill) gets under the game's WEAPON RESTRICTIONS: its own car when that
    /// carries none of the restricted weapons, else the same car with them taken off (all of them under All Weapons), served
    /// by its content as a player's own variant is. The car is never swapped for another model, and the clients are sent the
    /// file (<see cref="CarInfo.Vcf"/>), so every machine draws the car the server drives, unarmed where the game is.
    /// </summary>
    public string Legal(string car)
    {
        if ((Options.Restrictions.IsEmpty && Options.AllowTurrets) || StemOf(car) is not { } stem) return car;
        var vcf = _custom.TryGetValue(car, out var c) ? c : Vfs.TryRead(car);
        if (vcf is null) return car;
        I76.Sim.Trip.TripCar trip;
        try { trip = I76.Sim.Trip.TripCar.FromVcf(vcf); }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or IndexOutOfRangeException) { return car; }
        var bad = Options.Restrictions.Violations(Vfs, trip.Weapons.Select(w => w.Gdf));
        bool changed = bad.Count > 0;
        if (changed) trip.Weapons = [.. trip.Weapons.Where(w => !bad.Contains(w.Gdf, StringComparer.OrdinalIgnoreCase))];
        // several turrets only where the host allows them: the first stays (a bot's car loses the others, as the restricted weapons)
        if (!Options.AllowTurrets && CarConfigCheck.WithoutExtraTurrets(Vfs, trip, stem) is not null) changed = true;
        return changed ? Serve(stem, trip.ToVcf()) : car;
    }

    /// <summary>
    /// A bot seat's chosen car (<see cref="ServerOptions.BotChoices"/>): a car file of this install or one of the host's saved variants, or
    /// a spec <c>MODEL[:VARIANT]</c> (<see cref="ParseCarSpec"/>). Null when there is no such car or the game does not let it in (hidden
    /// content off, the strict checks); otherwise the file the bot drives: the car with the restricted weapons taken off, served by content
    /// when it is not the archive's own (a saved variant, an addon\ car), so every machine builds it.
    /// </summary>
    public string? BotChoice(string choice)
    {
        var car = ParseCarSpec(choice);
        if (car is null || Vfs.TryRead(car) is null) return null;
        if (CheckCar(car, null, restrictions: false, anyTurrets: true) is not null) return null;      // (a bot's extra turrets come off in Legal)
        var served = ServedOwn(Legal(car));
        _origin[served] = car;
        return served;
    }

    private readonly Dictionary<string, string> _origin = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The car file a bot's served car was made from (a saved variant is served by its content): where its paint colours are looked up.</summary>
    public string OriginOf(string car) => _origin.GetValueOrDefault(car, car);

    /// <summary>A car file served by its content unless the install's archive has it by that name.</summary>
    private string ServedOwn(string car) =>
        !_custom.ContainsKey(car) && !Vfs.InArchive(car) && StemOf(car) is { } stem && Vfs.TryRead(car) is { } vcf ? Serve(stem, vcf) : car;

    /// <summary>
    /// <c>MODEL[:VARIANT]</c> (a model's file stem, variant 1 by default), a car file (<c>vppirna2</c>, <c>vppirna2.vcf</c>), or random
    /// (empty, <c>random</c>, <c>-</c>, null); the car file name, or null for random or a spec that is none.
    /// </summary>
    public static string? ParseCarSpec(string? spec)
    {
        spec = spec?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(spec) || spec is "random" or "-") return null;
        if (spec.EndsWith(".vcf")) spec = spec[..^4];
        string file;
        if (spec.Split(':') is [var m, var v] && int.TryParse(v, out int n) && n > 0) file = m + n;
        else if (char.IsAsciiDigit(spec[^1])) file = spec;
        else file = spec + "1";
        file += ".vcf";
        return StemOf(file) is null ? null : file;
    }

    /// <summary>
    /// The host's saved variants (a model's file the install's archive lacks: the overlay the host's garage saves into, an addon\ car) of
    /// the models the game offers, in name order.
    /// </summary>
    public IReadOnlyList<string> SavedCars()
    {
        var stems = new HashSet<string>(OfferedModels, StringComparer.OrdinalIgnoreCase);
        if (Options.AllowHidden) stems.UnionWith(I76.Sim.Trip.HiddenContent.HiddenModels(Vfs, OfferedModels));
        var l = new List<string>();
        foreach (var name in Vfs.Names)
            if (StemOf(name) is { } stem && stems.Contains(stem) && !Vfs.InArchive(name)) l.Add(name.ToLowerInvariant());
        l.Sort(StringComparer.Ordinal);
        return l;
    }

    /// <summary>The saved variants that carry none of the restricted weapons and pass the game's rules as they are, served as they stand.</summary>
    public IReadOnlyList<string> LegalSavedCars() =>
        [.. SavedCars().Where(c => CheckCar(c) is null).Select(c => { var served = ServedOwn(c); _origin[served] = c; return served; })];

    private string? CustomCar(string asked, byte[]? config) => ResolveConfig(asked, config).Name;

    /// <summary>
    /// I26's Reconfigure (not in the original): while dead, a player picks the car the next respawn builds. It is checked
    /// as a joining car is (<see cref="CheckJoin"/>: the garage's rules for a configuration, the models and weapons the
    /// game offers, hidden content, the restrictions, the strict checks), and must be a car this install has or a
    /// configuration that passes, never a stand-in. Null and <paramref name="file"/> set when taken, else why not; the
    /// old car stays.
    /// </summary>
    public string? Reconfigure(Seat s, string car, byte[]? config, out string? file, byte[]? paintBytes = null)
    {
        file = null;
        if (Mode.CanReconfigure(s) is { } no) return no;
        string asked = car.Trim().ToLowerInvariant();
        if (!asked.EndsWith(".vcf")) asked += ".vcf";
        var (custom, error) = ResolveConfig(asked, config);
        if (error is not null) return error;
        string chosen = custom ?? (CarExists(asked) ? asked : "");
        if (chosen.Length == 0) return $"this server does not have {asked}";
        if (CheckCar(chosen, custom is null ? null : StemOf(asked)) is { } why) return why;
        chosen = Served(chosen);
        try { Mode.Reconfigure(s, chosen, I76.Formats.Imaging.CarPaint.FromBytes(paintBytes ?? [])); }
        catch (Exception ex) { return $"{asked} cannot be built here ({ex.Message})"; }
        file = chosen;
        Log?.Invoke($"reconfigure: {s.Name} comes back in {chosen}{(custom is not null ? $" (their {asked})" : "")}");
        return null;
    }

    /// <summary>
    /// The car a join asks for, or what it gets instead: the same model's
    /// first variant for one this install does not have (a variant saved by
    /// CONFIGURE CHASSIS is the player's own file, vppirna4.vcf), else the
    /// default car.
    /// </summary>
    public string CarFor(string car)
    {
        car = car.Trim().ToLowerInvariant();
        if (!car.EndsWith(".vcf")) car += ".vcf";
        if (CarExists(car)) return car;
        var stock = Regex.Replace(car, @"\d+\.vcf$", "1.vcf");
        return stock != car && CarExists(stock) ? stock : Options.DefaultCar;
    }

    /// <summary>The game as a session list shows it.</summary>
    public SessionInfo Info() => new(Options.SessionId, Protocol.Version, Options.Name, Arena, _seats.Count, Options.MaxPlayers,
        Mode.Rules.TeamPlay, Options.InviteOnly, Options.MaxScore, Options.TimeLimit, Over,
        [.. _seats.Select(s => new SessionDriver(s.Name, s.Actor.CarFile, (byte)s.Actor.Team)), .. Mode.Bots.Select(b => new SessionDriver(b.Name, b.Player.CarFile, (byte)b.Player.Team))],
        Mode.Rules.Kind, Mode.Rules.Goal, Mode.Rules.GoalValue, Mode.Rules.CtfTeams,
        MapOffer?.Hash, MapOffer?.Shared ?? false, (uint)(MapOffer?.TotalSize ?? 0), Options.Password.Length > 0,
        Options.Restrictions.IsEmpty ? null : Options.Restrictions, Options.AllowHidden, Coop: Options.Coop is not null, Turrets: Options.AllowTurrets, RateHz: (byte)Session.Rate.Label, CarReset: Options.AllowCarReset, Cb: Options.AllowCb);

    /// <summary>
    /// A player joins: the car they asked for, built from the server's own
    /// files (or the default car if it has none by that name), at a free
    /// spawn point, driven by the next free slot.
    /// </summary>
    public Seat Join(string name, string car, int team, object? connection = null, byte[]? config = null, byte[]? paintBytes = null)
    {
        // colours are cosmetic: bytes that do not parse are the factory paint, nothing else is checked
        var paint = I76.Formats.Imaging.CarPaint.FromBytes(paintBytes ?? []);
        name = Clean(name, Protocol.MaxName);
        if (name.Length == 0) name = $"Player {_seats.Count + 1}";
        string asked = car.Trim().ToLowerInvariant();
        if (!asked.EndsWith(".vcf")) asked += ".vcf";
        if (car == Protocol.MissionCar) car = Mode is CoopMode ? car : Options.DefaultCar;        // the co-op mission's own car, or a melee's default
        else
        {
            car = CustomCar(asked, config) ?? CarFor(asked);
            if (car != asked && !_custom.ContainsKey(car)) Log?.Invoke($"{name} asked for {asked}, which this install does not have: {car}");
            car = Served(car);
        }
        Mode.SeatsChanging(1);       // a human takes a seat a bot had: the last bot makes room first
        ISeatActor p;
        try { p = Mode.Admit(car, name, team, paint); }
        catch (Exception ex) when (car != (Mode is CoopMode ? Protocol.MissionCar : Options.DefaultCar))
        {
            string instead = Mode is CoopMode ? Protocol.MissionCar : Options.DefaultCar;
            Log?.Invoke($"{name}'s car {car} failed to build ({ex.Message}): {(Mode is CoopMode ? "the mission's own" : instead)}");
            car = instead;
            p = Mode.Admit(car, name, team, paint);
        }
        var d = Session.AddDriver(p.Car);
        Session.AssignIds();
        var seat = new Seat { Slot = d.Slot, Name = name, Actor = p, Driver = d, Connection = connection, Inputs = new InputQueue(Session.Rate) };
        _seats.Add(seat);
        _seats.Sort((a, b) => a.Slot.CompareTo(b.Slot));
        _pending.Add(new JoinedEvent((byte)seat.Slot, name, (byte)p.Team));
        Log?.Invoke($"join: {name} in slot {seat.Slot}, {p.CarFile}, team {p.Team}, car {seat.CarSerial}");
        return seat;
    }

    /// <summary>A joiner's team in the melee (<see cref="MeleeMode.TeamFor"/>).</summary>
    public int TeamFor(int asked) => ((MeleeMode)Mode).TeamFor(asked);

    /// <summary>The melee's bots the game should have now (<see cref="MeleeMode.BalanceBots"/>).</summary>
    public void BalanceBots(int extraHumans, bool announce = true) => ((MeleeMode)Mode).BalanceBots(extraHumans, announce);

    /// <summary>The host boots a bot (BOOT_n on its row of the score window, i26-server's kick).</summary>
    public string? BootBot(string who) => Mode.BootBot(who);

    private BotSeat? BotOf(Vehicle v) => Mode.Bots.FirstOrDefault(b => b.Player.Car == v);

    public void Leave(Seat s, string reason)
    {
        if (!_seats.Remove(s)) return;
        Mode.Release(s);
        Session.RemoveDriver(s.Driver);
        _pending.Add(new LeftEvent((byte)s.Slot, reason));
        Log?.Invoke($"leave: {s.Name} (slot {s.Slot}): {reason}; {s.Actor.Score} points, {s.Actor.Kills} kills, {s.Actor.Deaths} deaths; " +
                    $"{s.Inputs.Corrected} inputs corrected, {s.Inputs.Starved} ticks without input, {s.Inputs.Dropped} dropped");
        Mode.SeatsChanging(0);       // a seat is free: a bot the game had to send away may come back
    }

    /// <summary>
    /// A client's mirror lacks these cars (its snapshots name them): what each one still in the arena is, as the join accept
    /// would list it. One query a seat every quarter second is answered; the rest are ignored (the client asks again).
    /// </summary>
    public List<CarInfo>? AnswerCarQuery(Seat from, IEnumerable<int> serials)
    {
        if (from.CarQueryTick >= 0 && Tick - from.CarQueryTick < Session.Rate.TicksFor(0.25f)) return null;
        from.CarQueryTick = Tick;
        var want = serials.Distinct().Take(CarQuery.Max).ToHashSet();
        var found = Setup.World.Vehicles.Where(v => v.NetId != 0 && want.Contains(Id(v))).Select(Info).ToList();
        Log?.Invoke($"{from.Name} asked about cars {string.Join(", ", want)}: {(found.Count == 0 ? "none in the arena" : string.Join(", ", found.Select(c => $"{c.Serial} {c.Def}")))}");
        return found;
    }

    /// <summary>A client's copy of the co-op mission's movie is over.</summary>
    public void MovieDone(Seat from, string name) => Mode.MovieDone(from, name);

    public void Chat(Seat? from, string text)
    {
        text = Clean(text, Protocol.MaxChat);
        if (text.Length == 0) return;
        if (from is not null && !from.ChatRate.Allow(Tick * Session.TickSeconds, out bool first))
        {
            if (first) Log?.Invoke($"chat: {from.Name} is talking too fast; the excess is dropped");
            return;
        }
        _pending.Add(new ChatEvent((sbyte)(from?.Slot ?? -1), text));
        Log?.Invoke($"chat: {from?.Name ?? "server"}: {text}");
    }

    private static string Clean(string s, int max)
    {
        var c = new string(s.Where(ch => !char.IsControl(ch)).ToArray()).Trim();
        return c.Length > max ? c[..max] : c;
    }

    /// <summary>What a joining client needs: its slot and car, the arena, the tick, the rules, every car and the destroyed structures.</summary>
    /// <summary>The players the host has muted on the CB for everyone, by name (a player who leaves and comes back stays muted; they go with a host migration).</summary>
    public HashSet<string> CbMuted { get; } = new(StringComparer.OrdinalIgnoreCase);

    public JoinAccept AcceptFor(Seat s) => new(
        (byte)s.Slot, s.CarSerial, Arena, Tick, s.Actor.CarFile,
        Mode.Rules.TeamPlay, Options.MaxScore, Options.TimeLimit, Options.RespawnDelay,
        [.. Setup.World.Vehicles.Select(Info)],
        [.. _structureSerial.Where(kv => kv.Key.Destroyed || kv.Key.Nodes.Any(n => n.Destroyed)).Select(kv => new StructureState(kv.Value, kv.Key.DestroyedMask()))],
        Mode.Rules.Kind, Mode.Rules.Goal, Mode.Rules.GoalValue, Mode.Rules.CtfTeams, (byte)Session.Rate.Label,
        Map?.MissionSize ?? 0, Map?.MissionCrc ?? 0, Map?.TerrainSize ?? 0, Map?.TerrainCrc ?? 0, MapSet?.Hash) { Coop = Mode.JoinState(s), Cb = Options.AllowCb };

    private CarInfo Info(Vehicle v)
    {
        var seat = _seats.FirstOrDefault(x => x.Actor.Car == v);
        var bot = seat is null ? BotOf(v) : null;
        var e = Setup.Script!.Host.Of(v);
        // (a mission's own cars, a co-op game's, are their files)
        var def = _defs.GetValueOrDefault(v) ?? seat?.Actor.CarFile ?? bot?.Player.CarFile ?? v.Definition ?? "";
        return new CarInfo(v.NetId & 0xffffff, def, (sbyte)(seat?.Slot ?? bot?.Slot ?? -1), (byte)(e?.Team ?? 0), seat?.Name ?? bot?.Name ?? e?.Name ?? "", _custom.GetValueOrDefault(def), v.Paint?.ToBytes());
    }

    /// <summary>One tick: each seat's next input, the session's tick, then what happened as events.</summary>
    public void Step()
    {
        if (Over || Mode.Holding(_seats)) return;
        int n = _seats.Count == 0 ? 0 : _seats.Max(x => x.Slot) + 1;
        var inputs = new CarInput[n];
        foreach (var s in _seats) inputs[s.Slot] = s.Inputs.Next();
        StepWith(inputs);
    }

    /// <summary>
    /// One tick on these inputs (by slot): a dead player's RAISE_DEAD asked of the game, the session's tick, what happened
    /// as events. The host's tick and a successor's replay of it (host migration) both come here, so they are the same game.
    /// </summary>
    private void StepWith(CarInput[] inputs)
    {
        if (Over) return;
        foreach (var s in _seats)
        {
            if (s.Slot < inputs.Length && (inputs[s.Slot].Commands & CarCommands.RaiseDead) != 0 && Mode.RaiseDead(s) is { } why)
                Log?.Invoke($"{s.Name} asked to come back: refused, {why}");
        }
        Session.Tick(inputs);
        Collect();
        if (Mode.Over && !Over)
        {
            Over = true;
            _pending.Add(Mode.Concluded(Options.Rounds, Options.RoundPause));
        }
    }

    /// <summary>Where each car the clients know is now, by serial (the events' interest).</summary>
    public Func<int, System.Numerics.Vector3?> CarPositions()
    {
        var at = new Dictionary<int, System.Numerics.Vector3>();
        foreach (var v in Setup.World.Vehicles) if (v.NetId != 0) at[Id(v)] = v.Pos.ToVector3();
        return serial => at.TryGetValue(serial, out var p) ? p : null;
    }

    /// <summary>The events a seat's client is sent (<see cref="EventInterest"/>): everything that changes its game, and what is seen and heard only within range of its car.</summary>
    public List<NetEvent> EventsFor(Seat s, IReadOnlyList<NetEvent> events, Func<int, System.Numerics.Vector3?> carAt)
    {
        var viewer = s.Actor.Car.Pos.ToVector3();
        int own = s.CarSerial;
        var l = new List<NetEvent>(events.Count);
        foreach (var e in events)
        {
            // a co-op leave-map warning is its player's alone
            if (e is RadioEvent { To: >= 0 } r && r.To != s.Slot) continue;
            if (!Options.EventInterest || EventInterest.Relevant(e, viewer, own, carAt)) l.Add(e);
        }
        return l;
    }

    /// <summary>The events gathered since the last call, in order.</summary>
    public List<NetEvent> TakeEvents()
    {
        var l = new List<NetEvent>(_pending);
        _pending.Clear();
        return l;
    }

    private static int Id(Vehicle v) => v.NetId & 0xffffff;

    // the mode's side of the host (IModeHost)
    IReadOnlyList<Seat> IModeHost.Seats => _seats;
    int? IModeHost.HostSlot => HostSlot;
    void IModeHost.Emit(NetEvent e) => _pending.Add(e);
    void IModeHost.Log(string text) => Log?.Invoke(text);
    void IModeHost.Spawned(Vehicle car, string def) { _defs[car] = def; _spawned.Add((car, def)); }
    void IModeHost.Removed(Vehicle car) { if (car.NetId != 0) _pending.Add(new RemoveEvent(car.NetId & 0xffffff)); _defs.Remove(car); }
    void IModeHost.Redefined(Vehicle car, string def) => _defs[car] = def;
    CarInfo IModeHost.Info(Vehicle car) => Info(car);

    private void Collect()
    {
        var combat = Setup.Combat;
        // cars come and go first, so a client knows a car before its events
        foreach (var (v, def) in _spawned)
            if (v.NetId != 0 && Setup.World.Vehicles.Contains(v)) _pending.Add(new SpawnEvent(Info(v)));
        _spawned.Clear();
        foreach (var e in combat.Events)
            switch (e)
            {
                case ShotFired s when WeaponIndex(s.Shooter, s.Weapon) is { } wi:
                    _pending.Add(new ShotEvent(Id(s.Shooter), wi, s.Muzzle, s.Forward));
                    _shots.AddOrUpdate(s.Weapon, new((ushort)(Shots(s.Weapon) + 1)));
                    break;
                case Impact i:
                    _pending.Add(new ImpactEvent(i.Effect, i.Sound, i.At, (byte)(i.Victim switch { null => 0, Vehicle => 1, _ => 2 }), (byte)i.Kind, i.Hit, i.N, i.Dir));
                    break;
                case Explosion x:
                    _pending.Add(new ExplosionEvent(x.Effect.File, x.At));
                    break;
                case WeaponClick c:
                    _pending.Add(new ClickEvent(Id(c.Shooter), c.Sound));
                    break;
            }
        combat.Events.Clear();
        foreach (var e in Setup.Damage.Events)
            switch (e)
            {
                case ZoneDamaged z: _pending.Add(new ZoneEvent(Id(z.Vehicle), (byte)z.Zone, (byte)z.Level)); break;
                case VehicleKilled k: _pending.Add(new KilledEvent(Id(k.Vehicle), k.DriverKilled)); break;
                case VehicleWrecked w: _pending.Add(new WreckedEvent(Id(w.Vehicle))); break;
                case TyreBlown t: _pending.Add(new TyreEvent(Id(t.Vehicle), (byte)t.Slot)); break;
                case PartFlung f: _pending.Add(new FlungEvent(Id(f.Vehicle), (byte)f.Slot, f.Velocity)); break;
                case Debris d: _pending.Add(new DebrisEvent(d.At, d.Velocity)); break;
                case SignKnocked k when _structureSerial.TryGetValue(k.Object, out int ks): _pending.Add(new SignEvent(ks, k.Velocity)); break;
                case StructureDestroyed s when _structureSerial.TryGetValue(s.Object, out int ss): _pending.Add(new StructureEvent(ss, s.Object.DestroyedMask(), [.. s.Booms])); break;
            }
        Setup.Damage.Events.Clear();
        foreach (var (car, hp) in Session.Misfires) _pending.Add(new MisfireEvent(Id(car), (byte)hp));
        Sounds();
        Mode.Collect();
    }

    /// <summary>The collision and landing sounds as SimDriver picks them, as sounds on the car that hit.</summary>
    private void Sounds()
    {
        var step = Setup.World.Step;
        foreach (var (v, _, type, _, speed) in step.ObjectHits)
        {
            float s2 = speed * speed;
            _pending.Add(new SoundEvent(Id(v), type == 4 ? "vnvco3.wav" : type == 3 ? "vnvcs5.wav" : s2 > 500 ? "vnvcs3.wav" : "vnvcs1.wav"));
        }
        _pairs.Clear();
        foreach (var (v, other, rel, _, _) in step.Impacts)
        {
            if (other is not Vehicle o) { _pending.Add(new SoundEvent(Id(v), "vtcoll.wav")); continue; }
            if (!_pairs.Add((v, o)) || !_pairs.Add((o, v))) continue;
            float s2 = rel.LengthSquared();
            var who = Setup.World.IsHuman(o) && !Setup.World.IsHuman(v) ? o : v;
            _pending.Add(new SoundEvent(Id(who), s2 > 2025 ? "vvch2.wav" : s2 > 500 ? "vvcre2.wav" : "vvcbb3.wav"));
        }
        foreach (var v in step.Landings) if (Setup.World.IsHuman(v)) _pending.Add(new SoundEvent(Id(v), "vland.wav"));
    }

    private static byte? WeaponIndex(Vehicle shooter, Weapon w)
    {
        if (shooter.Armory is not { } a) return null;
        int i = a.Weapons.IndexOf(w);
        if (i >= 0) return (byte)i;
        for (int k = 0; k < 2; k++) if (a.Handguns[k] == w) return (byte)(RoundState.Handgun + k);
        return null;
    }

    /// <summary>Every car and round as all the clients see them (encoded once a tick).</summary>
    public (List<CarState> Cars, List<RoundState> Rounds) Capture() => (CaptureCars().Select(c => c.State).ToList(), CaptureRounds());

    /// <summary>Every car the clients know, with its state.</summary>
    public List<(Vehicle Car, CarState State)> CaptureCars() => [.. Setup.World.Vehicles.Where(v => v.NetId != 0).Select(v => (v, CarState.Capture(v)))];

    /// <summary>The car <paramref name="v"/> is after: its radar target, or what its AI is ordered to attack (interest management).</summary>
    public Vehicle? TargetOf(Vehicle v) =>
        Setup.Combat.RadarTarget.GetValueOrDefault(v)
        ?? (Setup.Script?.Host.Of(v) is { Order: I76.Sim.Fsm.AiOrder.Attack, OrderTarget.Vehicle: { } t } ? t : null);

    public List<RoundState> CaptureRounds()
    {
        var rounds = new List<RoundState>();
        foreach (var r in Setup.Combat.Rounds)
        {
            if (r.NetId == 0 || r.Shooter.NetId == 0 || RoundState.WeaponIndex(r) is not { } wi) continue;
            rounds.Add(new RoundState { Serial = r.NetId & 0xffffff, Shooter = Id(r.Shooter), Weapon = wi, Pos = r.Pos, Rot = r.Rot, Travelled = r.Travelled, Settled = r.Settled });
        }
        return rounds;
    }

    /// <summary>The gates a mission's script slid that are off their places (none in a melee), by structure serial and node.</summary>
    public List<GateState> CaptureGates()
    {
        var l = new List<GateState>();
        foreach (var o in Setup.Statics.Gated)
            for (int i = 0; i < o.Nodes.Count; i++)
                if (o.Nodes[i].Gate is { } g && (g.X != 0 || g.Moving) && _structureSerial.TryGetValue(o, out int serial))
                    l.Add(new GateState(serial, (byte)i, g.X));
        return l;
    }

    /// <summary>The CTF flags as the clients see them (none outside CTF).</summary>
    public List<FlagState> CaptureFlags() =>
        Mode.Flags();

    public OwnCar CaptureOwn(Seat s)
    {
        var o = OwnCar.Capture(s.Actor.Car, Setup.Combat);
        o.Epoch = (byte)s.Actor.CarEpoch;
        return o;
    }

    /// <summary>Each weapon's shots so far (wrapping at 2^16): the shot events it has made, for its driver's prediction (<see cref="OwnFire"/>).</summary>
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<Weapon, StrongBox<ushort>> _shots = [];
    public ushort Shots(Weapon w) => _shots.TryGetValue(w, out var b) ? b.Value : (ushort)0;

    /// <summary>The seat's weapons' fire control for its client's prediction of its own shots (protocol 13).</summary>
    public OwnFire CaptureFire(Seat s) => OwnFire.Capture(s.Actor.Car, Shots);

    /// <summary>The flame throwers' streams as every client sees them (protocol 13).</summary>
    public List<FlameState> CaptureFlames()
    {
        var l = new List<FlameState>();
        foreach (var f in Setup.Combat.Flames)
            if (f.Owner.NetId != 0 && f.Count >= 2 && WeaponIndex(f.Owner, f.Weapon) is { } wi) l.Add(new FlameState(Id(f.Owner), wi, (byte)f.Count));
        return l;
    }

    /// <summary>
    /// The seat's car at full precision for its client's prediction (protocol 13), once a tick. The slow part (parameters,
    /// engine curve, grip and rest) goes for half a second after it changes, and once a second anyway, so that a lost
    /// snapshot does not leave the client on the old numbers for long.
    /// </summary>
    public OwnMotion CaptureMotion(Seat s)
    {
        var m = OwnMotion.Capture(s.Actor.Car, s.Driver, slow: true);
        int second = Session.Rate.TicksFor(1f);
        if (s.SlowSent is null || !m.Slow!.SameAs(s.SlowSent)) { s.SlowSent = m.Slow; s.SlowLeft = Session.Rate.TicksFor(0.5f); }
        if (s.SlowLeft > 0) s.SlowLeft--;
        else if (++s.SinceSlow < second) m.Slow = null;
        if (m.Slow is not null) s.SinceSlow = 0;
        return m;
    }

    /// <summary>One tick's cars and rounds as every client's snapshot takes them (<see cref="CaptureTick"/>), and who is after whom.</summary>
    public sealed class SnapshotTick
    {
        public uint Tick;
        public required List<(int Serial, EntityRec Rec)> Cars, Rounds;
        public required Dictionary<int, (System.Numerics.Vector3 Pos, Vehicle Car)> CarAt;
        public required Dictionary<int, EntityRec> CarRec;
        public required Dictionary<int, (System.Numerics.Vector3 Pos, int Shooter)> RoundAt;
        public required Dictionary<Vehicle, Vehicle> Targets;
        public required List<FlagState> Flags;
        public required List<FlameState> Flames;
        /// <summary>The snapshot's tail (the flags and flames), the same for every client.</summary>
        public byte[] Tail = [];
    }

    /// <summary>The tick's cars and rounds, quantized once for every client (sorted by serial).</summary>
    public SnapshotTick CaptureTick()
    {
        var cars = CaptureCars();
        cars.Sort((a, b) => a.State.Serial.CompareTo(b.State.Serial));
        var rounds = CaptureRounds();
        rounds.Sort((a, b) => a.Serial.CompareTo(b.Serial));
        var t = new SnapshotTick
        {
            Tick = Tick,
            Cars = [.. cars.Select(c => (c.State.Serial, c.State.Rec(Tick)))],
            Rounds = [.. rounds.Select(r => (r.Serial, r.Rec(Tick)))],
            CarAt = new(cars.Count), CarRec = new(cars.Count), RoundAt = new(rounds.Count), Targets = [], Flags = CaptureFlags(), Flames = CaptureFlames(),
        };
        foreach (var (serial, rec) in t.Cars) t.CarRec[serial] = rec;
        var tw = new NetWriter(256);
        SnapshotCodec.WriteTail(tw, t.Flags, t.Flames, CaptureGates(), Setup.Script is { } sc ? CameraState.Of(sc.Host.View) : null, Setup.Script is { } ps ? CameraState.PersonalOf(ps.Host) : null);
        t.Tail = tw.ToArray();
        foreach (var (car, st) in cars)
        {
            t.CarAt[st.Serial] = (st.Pos.ToVector3(), car);
            if (TargetOf(car) is { } target) t.Targets[car] = target;
        }
        foreach (var r in rounds) t.RoundAt[r.Serial] = (r.Pos, r.Shooter);
        return t;
    }

    /// <summary>The seat's client has decoded the snapshot of this tick (its input message says so).</summary>
    public void AckSnapshot(Seat s, uint tick) => s.Snapshots?.Ack(tick);

    /// <summary>Snapshots that went over their budget (their essential entries alone did not fit), and entries put off for want of room.</summary>
    public long OverBudget;
    public long Deferred => _seats.Sum(x => x.Planner.Deferred);

    /// <summary>
    /// The seat's snapshot of this tick: against the one its client last acknowledged, or in full
    /// (<see cref="SnapshotEncoder"/>), within <paramref name="budget"/> bytes: the head (own car, motion, fire control) and
    /// the tail (flags, flames) always, the cars and rounds by priority (<see cref="SnapshotPlanner"/>; with interest
    /// management off, everything every tick). Returns the bytes of the head (to the cars) and of the cars, and whether it went in full.
    /// </summary>
    public (int Head, int Cars, bool Full) WriteSnapshot(NetWriter w, Seat s, SnapshotTick t, int budget = int.MaxValue)
    {
        var rate = Session.Rate;
        bool delta = Options.DeltaSnapshots, interest = delta && Options.InterestManagement;
        var enc = s.Snapshots ??= new SnapshotEncoder(rate);
        var b = delta ? enc.Baseline(t.Tick) : null;
        if (b is null) enc.Full++; else enc.Deltas++;
        var frame = new SnapFrame();
        var me = s.Actor.Car;
        int start = w.Length;
        var (fast, slow) = Snapshot.Bytes(delta ? OwnMotion.Capture(me, s.Driver, slow: true) : CaptureMotion(s));
        // the client predicted this very car for the acked input: say so instead of sending it (its hash's low bits pick
        // which of its predictions, should a replay have changed it)
        ushort? confirm = null;
        if (b is not null && Options.ConfirmMotion && !s.Inputs.NeedExact && s.Inputs.HashOf(s.Inputs.LastApplied) is { } h && h == OwnMotion.Hash(fast))
        {
            confirm = (ushort)h;
            s.Confirmed++;
        }
        SnapshotCodec.WriteHead(w, t.Tick, s.Inputs.LastApplied, s.Inputs.Slack, b, Snapshot.Bytes(CaptureOwn(s)), fast, slow, frame, Snapshot.Bytes(CaptureFire(s)), confirm);
        int head = w.Length - start;
        var tail = t.Tail;
        Func<int, (CarSend, float, bool)>? carHow = null, roundHow = null;
        if (interest) (carHow, roundHow) = Priorities(s, t, b);
        SnapshotCodec.Plan(s.CarPlan, t.Tick, Session.TickSeconds, b, t.Cars, true, carHow);
        SnapshotCodec.Plan(s.RoundPlan, t.Tick, Session.TickSeconds, b, t.Rounds, false, roundHow);
        int room = budget - head - tail.Length;
        if (interest)
        {
            if (SnapshotPlanner.Select(s.CarPlan, s.RoundPlan, room) > room) Interlocked.Increment(ref OverBudget);
            s.Planner.Sent(s.CarPlan, t.Tick);
            s.Planner.Sent(s.RoundPlan, t.Tick);
            s.Planner.Sweep(t.Tick, t.Cars, t.Rounds);
        }
        else
            foreach (var plan in new[] { s.CarPlan, s.RoundPlan })
                for (int i = 0; i < plan.Entries.Count; i++) if (plan.Entries[i].Sendable) plan.Entries[i] = plan.Entries[i] with { Selected = true };
        SnapshotCodec.Emit(w, s.CarPlan, frame.Cars);
        int cars = w.Length - start - head;
        SnapshotCodec.Emit(w, s.RoundPlan, frame.Rounds);
        w.Bytes(tail);
        if (delta) enc.Sent(frame);
        return (head, cars, b is null);
    }

    /// <summary>
    /// What may go of each car and round to this seat's client this tick, and how much it needs it (<see cref="SnapshotPlanner"/>):
    /// <list type="bullet">
    /// <item>The own car always (essential); the own rounds as the nearest.</item>
    /// <item>A car grows by its <see cref="Interest"/> rate (1 a tick near, ahead or related; a tick in 2 within the radar's
    /// range; in 4 beyond, at 20 Hz), plus how far the client's copy, moved on along its velocity, has drifted (seen from
    /// the client's car), plus 1 when its looks changed; it is due at 1. One not sent for a second goes before anything
    /// only due, oldest first; a respawn, or one not sent for two seconds, is essential; one the client does not have yet ranks first. Within 200 m or related it goes whole, else coarse. It
    /// ranks by its accumulator times 2 / (1 + distance / 150 m).</item>
    /// <item>A round within 200 m is due every tick, within 400 m every 0.1 s, then every 0.2 s; beyond
    /// <see cref="Interest.RoundCullMetres"/> it is not sent (the client forgets it until it comes near). It ranks by its
    /// accumulator (one more for a new one) times 1 / (1 + distance / 150 m).</item>
    /// </list>
    /// </summary>
    private (Func<int, (CarSend, float, bool)> Cars, Func<int, (CarSend, float, bool)> Rounds) Priorities(Seat s, SnapshotTick t, SnapFrame? b)
    {
        var rate = Session.Rate;
        float ts = Session.TickSeconds;
        var me = s.Actor.Car;
        var at = me.Pos.ToVector3();
        var fwd = me.Rot.Forward;
        var mine = t.Targets.GetValueOrDefault(me);
        int own = s.CarSerial;
        uint tick = t.Tick;
        uint maxAge = (uint)rate.TicksFor(SnapshotPlanner.MaxAgeSeconds);
        var planner = s.Planner;
        static float Weight(float d) => 1 / (1 + d / 150);
        (CarSend, float, bool) Car(int serial)
        {
            if (serial == own || !t.CarAt.TryGetValue(serial, out var c)) return (CarSend.Full, float.MaxValue, true);
            bool related = c.Car == mine || t.Targets.GetValueOrDefault(c.Car) == me;
            float d = System.Numerics.Vector3.Distance(at, c.Pos);
            var tier = Interest.Of(at, fwd, c.Pos, related);
            bool coarse = !related && d >= Interest.NearMetres;
            float growth = Interest.Growth(tier, rate);
            var b0 = b?.Cars.GetValueOrDefault(serial);
            bool essential = false;
            if (b0 is not null)
            {
                var rec = t.CarRec[serial];
                growth += MathF.Min(30 * Interest.Drift(b0, rec, tick, ts) / MathF.Max(d, 10), 2);
                if (!rec.Cold.AsSpan().SequenceEqual(b0.Cold))
                {
                    growth += 1;
                    // a respawn or regen pad (the generation, the cold bytes' first) must not wait
                    if (rec.Cold.Length > 0 && b0.Cold.Length > 0 && rec.Cold[0] != b0.Cold[0]) essential = true;
                }
            }
            float acc = planner.Grow(true, serial, growth, tick, out uint age);
            // a second unsent: before anything that is only due (the oldest first); two: whatever the budget (a clump
            // of cars that all fell due together then spreads over a few ticks rather than bursting one)
            essential |= age >= 2 * maxAge;
            if (b0 is null) return (coarse ? CarSend.Coarse : CarSend.Full, 1000 - d / 1000, essential);
            if (age >= maxAge) return (coarse ? CarSend.Coarse : CarSend.Full, 10000 + age, essential);
            if (acc < 1 && !essential) return (CarSend.Skip, 0, false);
            // the nearer, the more it matters; a car twice what a round at the same distance does
            return (coarse ? CarSend.Coarse : CarSend.Full, acc * 2 * Weight(related ? 0 : d), essential);
        }
        (CarSend, float, bool) Round(int serial)
        {
            if (!t.RoundAt.TryGetValue(serial, out var r)) return (CarSend.Full, 0, false);
            float d = r.Shooter == own ? 0 : System.Numerics.Vector3.Distance(at, r.Pos);
            if (d > Interest.RoundCullMetres) return (CarSend.Cull, 0, false);
            float acc = planner.Grow(false, serial, Interest.RoundGrowth(d, rate), tick, out uint age);
            bool known = b?.Rounds.ContainsKey(serial) == true;
            if (acc < 1 && known && age < maxAge) return (CarSend.Skip, 0, false);
            if (age >= maxAge) return (CarSend.Full, 10000 + age, age >= 2 * maxAge);
            // a new one ranks as if a tick due already (its tracer starts at the gun); the own ones (drawn from the
            // prediction, these only stand in for shots it missed and keep the mines) as the nearest
            return (CarSend.Full, (known ? acc : acc + 1) * Weight(d), false);
        }
        return (Car, Round);
    }

    public Scoreboard Scoreboard() => new(Tick, Setup.Script!.Host.Now, [.. Mode.Board().OrderBy(r => r.Slot)]);

    /// <summary>Everyone at the table, humans and bots, by slot: slot, name, player (the melee's).</summary>
    public IEnumerable<(int Slot, string Name, MeleePlayer Player)> Everyone() => ((MeleeMode)Mode).Everyone();
}

/// <summary>
/// A player's chat rate limit: at most <see cref="Lines"/> lines in any <see cref="Window"/> seconds, the excess dropped
/// (a flooding client cannot fill everyone's feed). Server time, so a client cannot argue with it.
/// </summary>
public sealed class ChatLimiter
{
    public const int Lines = 5;
    public const double Window = 5;
    private readonly Queue<double> _sent = new();
    private bool _dropping;

    /// <summary>Whether a line at <paramref name="now"/> (seconds) may go out; <paramref name="firstDrop"/> is set on the first drop of a burst.</summary>
    public bool Allow(double now, out bool firstDrop)
    {
        firstDrop = false;
        while (_sent.Count > 0 && now - _sent.Peek() >= Window) _sent.Dequeue();
        if (_sent.Count >= Lines)
        {
            firstDrop = !_dropping;
            _dropping = true;
            return false;
        }
        _dropping = false;
        _sent.Enqueue(now);
        return true;
    }
}
