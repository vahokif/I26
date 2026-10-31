using I76.Net;
using I76.Sim.Missions;
using I76.Sim.Rng;
using I76.Sim.Save;
using I76.Sim.Session;
using I76.Sim.Vehicles;

namespace I76.Server;

/// <summary>A co-op seat's player as the host's seat has it.</summary>
public sealed class CoopActor(CoopSeat seat, string name) : ISeatActor
{
    public CoopSeat Seat { get; } = seat;
    public string Name { get; } = name;
    public Vehicle Car => Seat.Car;
    public string CarFile => Seat.CarFile;
    public int Team => 0;
    public bool Destroyed => !Seat.Alive;
    public int CarEpoch => 0;
    public int Score => 0;
    public int Kills => 0;
    public int Deaths => Seat.Deaths;
}

/// <summary>
/// A co-op story mission as a host mode (docs/multiplayer.md "Co-op over the network"): the mission built with a human group
/// of the most players the game takes (1–8, <see cref="ServerOptions.MaxPlayers"/>), each joiner in a seat of it (the lowest
/// free one; the first player is seat 0, the script's player), the script run here and only here, its presentation (the
/// radio, the objectives, the movies, the sirens, the end) sent as events, its camera and gates in the snapshots' tail. No
/// scores: the board is the players' deaths and who is alive. The game is over a little after the mission's end, or when
/// everyone has left; there are no rounds.
/// </summary>
public sealed class CoopMode : IHostMode
{
    public MissionSetup Setup { get; }
    public SessionBuild Build { get; }
    public CoopGroup Group { get; }
    private ScriptHost Script => Setup.Script!.Host;
    private readonly ServerOptions _o;
    /// <summary>The seats' cars as the join accept and a savestate name them (<see cref="Build"/>'s co-op): see <see cref="SyncCars"/>.</summary>
    private readonly List<string?> _cars;
    /// <summary>The car the mission gives its player (seat 0's as built): what a seat that asks for the mission's own drives.</summary>
    public string OwnCar { get; }
    private IModeHost _host = null!;
    /// <summary>The seats held by a player of the host's, by co-op seat.</summary>
    private readonly bool[] _held;
    /// <summary>Someone sent an input (a game client's first comes once its mission is built): the mission runs from then.</summary>
    private bool _started;
    /// <summary>Everyone who joined has left.</summary>
    private bool _deserted;
    private bool? _endTold;
    private float _endAt;
    private readonly List<NetEvent> _radio = [];
    private readonly List<CoopEvent> _happened = [];
    private readonly List<(Vehicle Victim, object? Attacker)> _kills = [];
    private int[] _toldObjectives;
    private readonly Dictionary<Entity, bool> _toldSirens = [];

    /// <summary>Mission seconds from the end to the game over (the clients show the end meanwhile).</summary>
    public const float EndSeconds = 5f;

    public CoopMode(ModeContext c)
    {
        _o = c.Options;
        int seats = Math.Clamp(_o.MaxPlayers, 1, Protocol.MaxCoopSeats);
        _cars = Enumerable.Repeat<string?>(null, seats).ToList();
        var coop = new CoopSetup(_cars) { RespawnDelay = _o.RespawnDelay };
        Setup = CoopMission.Build(c.Vfs, c.World, c.Asm, coop, new CrtRand(unchecked((uint)_o.Seed)), _o.Seed);
        Group = Setup.Script?.Host.Coop ?? throw new InvalidDataException($"{c.Arena} has no mission script: it is not a story mission");
        Build = new SessionBuild { Mission = c.Arena.ToLowerInvariant(), Install = SessionBuild.InstallIdentity(c.Install, c.Arena), Coop = coop };
        OwnCar = Group.Seats[0].CarFile;
        _held = new bool[seats];
        _toldObjectives = [.. Script.Objectives.Flags];
    }

    public bool Ended => _endTold is not null;
    public bool Over => _deserted || (_endTold is not null && Script.Now - _endAt >= EndSeconds);
    public ModeRules Rules => new(false, MeleeKind.Combat, MeleeGoal.None, 0, 2);
    public IReadOnlyList<BotSeat> Bots => [];

    public void Start(IModeHost host)
    {
        _host = host;
        Group.Happened += e => _happened.Add(e);
        // the radio's lines as they go on the air here (the queue and its priorities are the host's alone)
        Script.Cb.Started += l =>
        {
            if (_radio.Count > 0 && _radio[^1] is RadioCutEvent) _radio.RemoveAt(_radio.Count - 1);
            _radio.Add(new RadioEvent(l.Name, Speaker(l.Speaker), (byte)l.Priority));
        };
        Script.Cb.Cut += _ => _radio.Add(new RadioCutEvent());
        Script.Movie += BeginMovie;
        Script.Logged += m => { if (m.StartsWith("co-op:", StringComparison.Ordinal)) host.Log(m); };
        Setup.Damage.KilledBy += (victim, attacker) => _kills.Add((victim, attacker));
    }

    /// <summary>The seats nobody holds yet are out of the world (the first player is seat 0); the mission waits for its first player.</summary>
    public void Opened()
    {
        for (int k = 1; k < Group.Seats.Count; k++) Script.CoopVacate(k);
    }

    /// <summary>A speaker on the wire: the mission's entity by its index, a human as the player (each client's own), or nobody.</summary>
    private int Speaker(object? o)
    {
        if (o is not Entity e) return RadioEvent.Nobody;
        if (Script.IsHumanEntity(e)) return RadioEvent.Player;
        int i = Script.Entities is List<Entity> l ? l.IndexOf(e) : Script.Entities.ToList().IndexOf(e);
        return i >= 0 ? i : RadioEvent.Nobody;
    }

    // ---- seats ----

    public void SeatsChanging(int extraHumans) { }

    /// <summary>Why a joiner cannot sit now (every seat held, or nobody alive to come in beside), or null.</summary>
    public string? CanJoin()
    {
        int free = FreeSeat();
        if (free < 0) return "the mission's seats are all taken";
        if (_endTold is not null) return "the mission is over";
        if (_started && !Group.Seats[free].Present && !Group.Seats.Any(s => s.Alive)) return "nobody to join beside right now: try again in a few seconds";
        return null;
    }

    private int FreeSeat() => Array.IndexOf(_held, false);

    public ISeatActor Admit(string car, string name, int team, I76.Formats.Imaging.CarPaint? paint = null)
    {
        int k = FreeSeat();
        if (k < 0) throw new InvalidOperationException("the mission's seats are all taken");
        var seat = Group.Seats[k];
        // the car they chose, or the mission's own (Protocol.MissionCar)
        string chosen = car == Protocol.MissionCar ? OwnCar : car;
        // seat 0 before anyone came is in the world already (its car made again in place if another is asked for); any other comes in beside a living teammate
        if (!seat.Present) { if (!Script.CoopJoin(k, chosen)) throw new InvalidOperationException("nobody to join beside"); }
        else if (!chosen.Equals(seat.CarFile, StringComparison.OrdinalIgnoreCase)) Script.CoopRecar(k, chosen);
        seat.Car.Paint = paint;       // cosmetic; it stays through the seat's respawns (MeleeGame.Rebuild)
        SyncCars();
        _held[k] = true;
        _host.Log($"co-op: {name} takes seat {k} in {seat.CarFile}{(chosen == OwnCar ? " (the mission's own)" : "")}");
        if (k > 0 && seat.Car.NetId != 0) _host.Spawned(seat.Car, seat.CarFile);
        return new CoopActor(seat, name);
    }

    /// <summary>
    /// The seats' cars for a client that builds the mission from the join accept, and for a savestate's successor: what each seat
    /// drives now, null for the mission's own (seat 0's), or for the seat 0's (a later seat's), as <see cref="CoopSetup"/> reads it.
    /// </summary>
    private void SyncCars()
    {
        for (int i = 0; i < _cars.Count; i++)
        {
            string f = Group.Seats[i].CarFile, same = i == 0 ? OwnCar : _cars[0] ?? OwnCar;
            _cars[i] = f.Equals(same, StringComparison.OrdinalIgnoreCase) ? null : f;
        }
    }

    /// <summary>
    /// A savestate's seats' cars (a successor, before it restores): each seat's car made as the old host had it, so the
    /// snapshot's cars are the mission's here and the build is the same (<see cref="SessionBuild.Differs"/>).
    /// </summary>
    public void AdoptCars(IReadOnlyList<string?> cars)
    {
        for (int i = 0; i < Group.Seats.Count && i < cars.Count; i++)
        {
            string file = cars[i] ?? (i == 0 ? OwnCar : cars[0] ?? OwnCar);
            if (!file.Equals(Group.Seats[i].CarFile, StringComparison.OrdinalIgnoreCase)) Script.CoopRecar(i, file);
        }
        SyncCars();
    }

    public void Release(Seat s)
    {
        var a = (CoopActor)s.Actor;
        _held[a.Seat.Index] = false;
        Script.CoopLeave(a.Seat.Index);
        if (!_held.Any(h => h) && _started)
        {
            _deserted = true;
            _host.Log("co-op: everyone has left: the game ends");
        }
    }

    public string? BootBot(string who) => $"no player {who}";
    /// <summary>A co-op respawn comes by itself (CoopGroup.RespawnDelay): RAISE_DEAD asks nothing.</summary>
    public string? RaiseDead(Seat seat) => null;
    public string? CanReconfigure(Seat seat) => "a co-op mission's seats keep the cars they came in";
    public void Reconfigure(Seat seat, string car, I76.Formats.Imaging.CarPaint? paint = null) => throw new InvalidOperationException("a co-op mission's seats keep the cars they came in");

    /// <summary>
    /// The mission waits for its first player's first input (the host's own game has built the mission by then), and stands
    /// still for a movie the script plays (T05, T06): no tick, so no timer runs and nobody is hurt, until the host's copy is over.
    /// </summary>
    public bool Holding(IReadOnlyList<Seat> seats)
    {
        if (!_started) _started = _o.Lobby ? AllLoaded(seats) : seats.Any(s => s.Inputs.LastReceived > 0);
        if (_movie is not null && MovieOver(seats)) EndMovie();
        return !_started || _movie is not null;
    }

    private long _loadAt;

    /// <summary>
    /// A lobby's start (docs/multiplayer.md "The co-op lobby"): every seat was dealt at once, and the mission's first tick waits until each
    /// player's game has built it and sent an input, so the cars appear at the start together. A player still loading after
    /// <see cref="ServerOptions.LoadSeconds"/> is left behind: the mission starts, and their game joins in where it is, as a late joiner's does.
    /// </summary>
    private bool AllLoaded(IReadOnlyList<Seat> seats)
    {
        if (seats.Count == 0) return false;
        if (_loadAt == 0) _loadAt = System.Diagnostics.Stopwatch.GetTimestamp();
        if (seats.All(s => s.Inputs.LastReceived > 0)) { _host.Log("co-op: every player has loaded: the mission starts"); return true; }
        if (System.Diagnostics.Stopwatch.GetElapsedTime(_loadAt).TotalSeconds * NetClock.Scale < _o.LoadSeconds) return false;
        _host.Log($"co-op: {string.Join(", ", seats.Where(s => s.Inputs.LastReceived == 0).Select(s => s.Name))} did not load in {_o.LoadSeconds:0} s: the mission starts without them");
        return true;
    }

    /// <summary>The cut-scenes are the host's to skip: the listen host's seat (or the one that took over), else the lowest.</summary>
    private void Skipper()
    {
        var seats = _host.Seats;
        foreach (var s in seats) s.Inputs.AllowSkip = s.Slot == SkipSlot(seats);
    }

    private int? SkipSlot(IReadOnlyList<Seat> seats) => _host.HostSlot is { } h && seats.Any(s => s.Slot == h) ? h : seats.Count > 0 ? seats.Min(s => s.Slot) : null;

    // ---- a movie the script plays (playMovie: T05's, T06's) ----

    /// <summary>The longest the game stands still for a movie whose host never reports it over (a game with no screen, a lost message).</summary>
    public const double MovieMaxSeconds = 600;
    private string? _movie;
    private long _movieAt;
    private readonly HashSet<int> _movieDone = [];

    /// <summary>The movie everyone is watching, or null.</summary>
    public string? Movie => _movie;

    /// <summary>The script asked for a movie: every client plays it and the game stands still (<see cref="Holding"/>) until the host's copy is over.</summary>
    private void BeginMovie(string name)
    {
        if (_movie is not null) return;
        _movie = name;
        _movieAt = System.Diagnostics.Stopwatch.GetTimestamp();
        _movieDone.Clear();
        _host.Emit(new MovieEvent(name));
        _host.Log($"movie: {name}: the game stands still until the host's copy is over");
    }

    /// <summary>A host migration's successor takes the movie the clients are playing on (it may not have reached it in its replay).</summary>
    public void ResumeMovie(string name)
    {
        if (_movie is not null) return;
        _movie = name;
        _movieAt = System.Diagnostics.Stopwatch.GetTimestamp();
        _movieDone.Clear();
        _host.Log($"movie: {name}: goes on under the new host");
    }

    public void MovieDone(Seat s, string name)
    {
        if (_movie == name) _movieDone.Add(s.Slot);
    }

    /// <summary>Over when the host's copy is (its end or its skip: only the host can skip it), when everyone's is, when nobody is left, or past <see cref="MovieMaxSeconds"/>.</summary>
    private bool MovieOver(IReadOnlyList<Seat> seats)
    {
        if (seats.Count == 0 || System.Diagnostics.Stopwatch.GetElapsedTime(_movieAt).TotalSeconds > MovieMaxSeconds) return true;
        return SkipSlot(seats) is { } host && _movieDone.Contains(host) || seats.All(s => _movieDone.Contains(s.Slot));
    }

    private void EndMovie()
    {
        _host.Log($"movie: {_movie} is over: the game goes on");
        _movie = null;
        _movieDone.Clear();
        _host.Emit(new MovieEndEvent());
    }

    // ---- the tick ----

    public void Collect()
    {
        Skipper();
        foreach (var e in _radio) _host.Emit(e);
        _radio.Clear();
        foreach (var (victim, attacker) in _kills)
        {
            if (Group.SeatOf(victim) is not { } vs || SlotOf(vs.Index) is not { } slot) continue;
            _host.Emit(new KillEvent(Id(victim), attacker is Vehicle av ? Id(av) : 0, 0));
            string by = attacker is Vehicle k && Script.Of(k) is { } ke ? " by " + (Group.SeatOf(k) is { } ks && SlotOf(ks.Index) is { } kslot ? Name(kslot) : ke.Name) : "";
            _host.Log($"kill: {Name(slot)} destroyed{by}");
        }
        _kills.Clear();
        foreach (var e in _happened)
            switch (e)
            {
                case CoopLeaveWarning w when SlotOf(w.Seat) is { } slot:
                    _host.Emit(new RadioEvent(w.Sound, RadioEvent.Nobody, (byte)w.Priority) { To = slot });
                    break;
                case CoopRespawned r when r.Why is CoopReturn.Died or CoopReturn.Joined:
                    _host.Emit(new RenewedEvent(Id(Group.Seats[r.Seat].Car), true));
                    break;
                case CoopRespawned r when SlotOf(r.Seat) is { } slot:
                    string mate = SlotOf(r.Beside) is { } ms ? Name(ms) : $"seat {r.Beside}";
                    _host.Emit(new ChatEvent(-1, $"*** {Name(slot)} drove off the map and is back beside {mate}"));
                    break;
            }
        _happened.Clear();
        var flags = Script.Objectives.Flags;
        for (int i = 0; i < flags.Length && i < _toldObjectives.Length; i++)
            if (flags[i] != _toldObjectives[i])
            {
                _toldObjectives[i] = flags[i];
                _host.Emit(new ObjectiveEvent((byte)(i + 1), (byte)flags[i]));
                _host.Log($"objective {i + 1}: {Objectives.Describe(flags[i])}");
            }
        foreach (var en in Script.Entities)
        {
            if (en.Vehicle is not { NetId: not 0 } v) continue;
            bool was = _toldSirens.GetValueOrDefault(en);
            if (en.Siren == was) continue;
            _toldSirens[en] = en.Siren;
            _host.Emit(new SirenEvent(Id(v), en.Siren));
        }
        if (_endTold is null && Setup.Script!.Result is { } won)
        {
            _endTold = won;
            _endAt = Script.Now;
            int m = Script.End?.FailMessage ?? -1;
            string text = !won && Setup.ObjectiveText is { } t && m >= 0 && m < t.Failures.Count ? t.Failures[m] : "";
            _host.Emit(new MissionEndEvent(won, won ? -1 : m, text));
            _host.Log($"the mission is {(won ? "won" : "failed")}{(text.Length > 0 ? $": {text}" : "")}: the game ends in {EndSeconds:0} s");
        }
    }

    public NetEvent Concluded(bool rounds, double roundPause)
    {
        var standings = _host.Seats.Select(s => ((byte)s.Slot, s.Actor.Deaths)).ToList();
        _host.Log($"game over: {(_deserted ? "everyone left" : _endTold == true ? "the mission was won" : "the mission failed")}; " +
                  string.Join(", ", _host.Seats.Select(s => $"{s.Name} {s.Actor.Deaths} deaths")));
        return new GameOverEvent(standings, 0);
    }

    public IEnumerable<PlayerRow> Board() =>
        _host.Seats.Select(s => new PlayerRow((byte)s.Slot, s.Name, 0, 0, 0, s.Actor.Deaths, (ushort)Math.Clamp(s.Rtt, 0, 65535), s.CarSerial, !s.Actor.Destroyed));

    public List<FlagState> Flags() => [];

    /// <summary>The co-op part of a seat's join accept: the seats, its own, and the script's presentation as it stands.</summary>
    public CoopInfo JoinState(Seat s)
    {
        var line = Script.Cb.Current;
        var sirens = Script.Entities.Where(e => e.Siren && e.Vehicle is { NetId: not 0 }).Select(e => Id(e.Vehicle!)).ToList();
        return new CoopInfo(Build.Coop!.Cars, Build.Coop.RespawnDelay, (byte)((CoopActor)s.Actor).Seat.Index,
            [.. Script.Objectives.Flags.Select(f => (byte)f)], sirens,
            line is null ? null : new RadioEvent(line.Name, Speaker(line.Speaker), (byte)line.Priority), line is null ? 0 : (float)((Script.Cb.NowMs - line.StartedMs) / 1000),
            _endTold is { } won ? new MissionEndEvent(won, won ? -1 : Script.End?.FailMessage ?? -1, "") : null, _movie);
    }

    private int? SlotOf(int seat) => _host.Seats.FirstOrDefault(s => ((CoopActor)s.Actor).Seat.Index == seat)?.Slot;
    private string Name(int slot) => _host.Seats.FirstOrDefault(s => s.Slot == slot)?.Name ?? $"player {slot + 1}";
    private static int Id(Vehicle v) => v.NetId & 0xffffff;

    // ---- host migration ----

    public List<(ISeatActor Actor, Driver Driver)> Restore(IReadOnlyList<Driver> drivers, IReadOnlyDictionary<int, string> names)
    {
        Array.Clear(_held);
        var seats = new List<(ISeatActor, Driver)>();
        foreach (var d in drivers)
        {
            if (Group.SeatOf(d.Car) is not { } seat) continue;
            _held[seat.Index] = true;
            seats.Add((new CoopActor(seat, names.GetValueOrDefault(d.Slot) ?? $"Player {d.Slot + 1}"), d));
        }
        _started = true;
        _radio.Clear();
        _happened.Clear();
        _kills.Clear();
        _toldObjectives = [.. Script.Objectives.Flags];
        _toldSirens.Clear();
        foreach (var e in Script.Entities) if (e.Siren) _toldSirens[e] = true;
        // the end announced already (the clients had it from the old host)
        if (Setup.Script!.Result is { } won) { _endTold = won; _endAt = Script.End?.At ?? Script.Now; }
        return seats;
    }
}
