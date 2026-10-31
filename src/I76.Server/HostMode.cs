using I76.Formats.Assembly;
using I76.Formats.Mission;
using I76.Formats.Vfs;
using I76.Net;
using I76.Sim.Missions;
using I76.Sim.Save;
using I76.Sim.Session;
using I76.Sim.Vehicles;

namespace I76.Server;

/// <summary>
/// What a game is played as (docs/multiplayer.md "Game modes"): the one thing <see cref="MeleeHost"/> asks of the kind of game
/// it hosts. The host owns the wire's side (seats, inputs, snapshots, events, chat, maps); the mode owns the game's:
/// building the arena or mission, who may sit and where their car stands, what the tick's events are besides the combat's,
/// the scoreboard, when it is over, and what a successor host needs to give the seats back. <see cref="MeleeMode"/> is the
/// network melee (combat, race, CTF, bots, the AI fill); a co-op mission is another.
/// </summary>
public interface IHostMode
{
    /// <summary>The world the mode built (the same one the host's session ticks).</summary>
    MissionSetup Setup { get; }
    /// <summary>What the arena was built from, which the session's snapshots carry (docs/savestate.md).</summary>
    SessionBuild Build { get; }

    /// <summary>The game starts, before the host's session exists: the mode hooks its game's events up to <paramref name="host"/> and begins.</summary>
    void Start(IModeHost host);
    /// <summary>The session exists and the host is ready: the table is set (the melee's bots sit down).</summary>
    void Opened();

    /// <summary>The game is over (its end has run its course); the host then says so once and stops ticking.</summary>
    bool Over { get; }
    /// <summary>The game's goal is reached, or its time is up: the end is announced, and <see cref="Over"/> follows after a pause.</summary>
    bool Ended { get; }
    /// <summary>The game-over event with its standings and the log line, asked once when <see cref="Over"/> first holds.</summary>
    NetEvent Concluded(bool rounds, double roundPause);

    /// <summary>The mode's fields of the join accept and the session list: the rules the clients play by.</summary>
    ModeRules Rules { get; }

    // ---- seats ----

    /// <summary>Why a join cannot sit now (a co-op mission's seats all held), or null; asked before the connection is accepted.</summary>
    string? CanJoin() => null;
    /// <summary>The game waits (a co-op mission until its first player is in): the host does not tick it.</summary>
    bool Holding(IReadOnlyList<Seat> seats) => false;
    /// <summary>A seat's client says its copy of the mission's movie is over (a co-op mission's; protocol 19).</summary>
    void MovieDone(Seat seat, string name) { }
    /// <summary>The mode's part of a seat's join accept (a co-op mission's), or null.</summary>
    CoopInfo? JoinState(Seat seat) => null;

    /// <summary>The seats are about to change by <paramref name="extraHumans"/> (a join about to sit; 0 after a leave): the melee's bots make room or come back.</summary>
    void SeatsChanging(int extraHumans);
    /// <summary>A joiner's car in the game: built at its place, on its team. May throw if the car does not build (the host then tries its default car).</summary>
    ISeatActor Admit(string car, string name, int team, I76.Formats.Imaging.CarPaint? paint = null);
    /// <summary>A seat's player leaves the game (its car goes).</summary>
    void Release(Seat seat);
    /// <summary>The server's players besides the seats (the melee's bots), by slot; empty outside the melee.</summary>
    IReadOnlyList<BotSeat> Bots { get; }
    /// <summary>The host boots a server-driven player; null when it did, else why not.</summary>
    string? BootBot(string who);
    /// <summary>A dead player's RAISE_DEAD: null when the respawn is on, else why it was refused.</summary>
    string? RaiseDead(Seat seat);
    /// <summary>Whether the seat may choose its next car now (null), else why not.</summary>
    string? CanReconfigure(Seat seat);
    /// <summary>The seat's next respawn builds <paramref name="car"/> (already checked by the host); throws if it does not build here, the old car staying.</summary>
    void Reconfigure(Seat seat, string car, I76.Formats.Imaging.CarPaint? paint = null);

    // ---- the tick ----

    /// <summary>A tick's events as the mode knows them (kills, scores, respawns, the end), after the host's own (cars, combat, damage, sounds), into <see cref="IModeHost.Emit"/>.</summary>
    void Collect();
    /// <summary>The scoreboard's rows: the seats' (with their round trips) and the server-driven players'.</summary>
    IEnumerable<PlayerRow> Board();
    /// <summary>The CTF flags as the clients see them (none outside CTF).</summary>
    List<FlagState> Flags();

    // ---- host migration ----

    /// <summary>
    /// The successor's seats from its restored session: the mode's players for each of the session's drivers (none connected yet),
    /// the server-driven players again under their slots, and its own bookkeeping reset (the end is not told again).
    /// <paramref name="names"/> are the seats' players' names by slot, as the old host had them (a mode whose game does not keep them).
    /// </summary>
    List<(ISeatActor Actor, Driver Driver)> Restore(IReadOnlyList<Driver> drivers, IReadOnlyDictionary<int, string> names);
}

/// <summary>The host as a mode sees it.</summary>
public interface IModeHost
{
    ServerOptions Options { get; }
    SimSession Session { get; }
    IReadOnlyList<Seat> Seats { get; }
    bool Over { get; }
    /// <summary>The host's own seat (a listen host's player, or the player whose game took over), null for a dedicated server.</summary>
    int? HostSlot { get; }
    /// <summary>A net event for the clients, in order.</summary>
    void Emit(NetEvent e);
    void Log(string text);
    /// <summary>A car came into the game with its definition (the clients are told by a spawn event at the tick's end).</summary>
    void Spawned(Vehicle car, string def);
    /// <summary>A car left the game.</summary>
    void Removed(Vehicle car);
    /// <summary>The car's definition changed in place (a reconfigured respawn).</summary>
    void Redefined(Vehicle car, string def);
    /// <summary>A car as the join accept lists it.</summary>
    CarInfo Info(Vehicle car);
    bool CarExists(string def);
    /// <summary>The rules a car must pass to play (null when it may).</summary>
    string? CheckCar(string file, string? stem = null, bool restrictions = true);
    /// <summary>The car a bot or an AI driver gets for <paramref name="car"/>: its own, or the same without the restricted weapons (a car served by content).</summary>
    string Legal(string car);
    /// <summary>The car file a bot's host-chosen car is (docs/multiplayer.md "Bots"): <paramref name="choice"/> checked, stripped of restricted weapons and served; null when it may not play here (the bot then rolls).</summary>
    string? BotChoice(string choice);
    /// <summary>The car file a bot's served car was made from, for its paint colours.</summary>
    string OriginOf(string car);
    /// <summary>The host's saved variants that are legal as they stand under the game's restrictions, served as they are (a bot left on Random prefers one).</summary>
    IReadOnlyList<string> LegalSavedCars();
}

/// <summary>A player of a mode, as the host and the wire see one: its car, what it drives, its side and its numbers.</summary>
public interface ISeatActor
{
    string Name { get; }
    Vehicle Car { get; }
    string CarFile { get; }
    int Team { get; }
    bool Destroyed { get; }
    /// <summary>How many times the car was rebuilt as another (a reconfigured respawn); the client's own-car block carries it.</summary>
    int CarEpoch { get; }
    int Score { get; }
    int Kills { get; }
    int Deaths { get; }
}

/// <summary>The rules a mode tells the clients (join accept, session list).</summary>
public readonly record struct ModeRules(bool TeamPlay, MeleeKind Kind, MeleeGoal Goal, int GoalValue, byte CtfTeams);

/// <summary>What a mode is built from: the server's options and the arena, as the host loaded them.</summary>
public sealed record ModeContext(ServerOptions Options, GameInstall Install, AssetVfs Vfs, ModelAssembler Asm, MissionWorld World, string Arena, Func<string, bool> CarExists, Func<string, string> Legal);

/// <summary>A <see cref="IHostMode"/> for the options' game; <see cref="MeleeHost"/> uses <see cref="MeleeMode"/>, or <see cref="CoopMode"/> for <see cref="ServerOptions.Coop"/>, unless given another.</summary>
public delegate IHostMode HostModeFactory(ModeContext context);

/// <summary>A melee player as the host's seat has it.</summary>
public sealed class MeleeActor(MeleePlayer player) : ISeatActor
{
    public MeleePlayer Player { get; } = player;
    public string Name => Player.Name;
    public Vehicle Car => Player.Car;
    public string CarFile => Player.CarFile;
    public int Team => Player.Team;
    public bool Destroyed => Player.Destroyed;
    public int CarEpoch => Player.CarEpoch;
    public int Score => Player.Score;
    public int Kills => Player.Kills;
    public int Deaths => Player.Deaths;
}
