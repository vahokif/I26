using I76.Formats.Assembly;
using I76.Net;
using I76.Sim.Rng;
using I76.Sim.Missions;
using I76.Sim.Save;
using I76.Sim.Session;
using I76.Sim.Vehicles;

namespace I76.Server;

/// <summary>
/// The network melee as a host mode: the arena built as MissionSetup builds a melee (<see cref="MeleeRules.Network"/>), the
/// players of <see cref="MeleeGame"/> in the seats, combat, race and capture the flag, the AI fill, the bots, scores, kills,
/// rounds' game over. It is everything <see cref="MeleeHost"/> did before it had a mode to ask.
/// </summary>
public sealed class MeleeMode : IHostMode
{
    public MeleeGame Game { get; }
    public MissionSetup Setup { get; }
    public SessionBuild Build { get; }
    private readonly ServerOptions _o;
    private readonly string _arena;
    private readonly Random _botRng;
    private IModeHost _host = null!;
    private readonly List<BotSeat> _bots = [];
    private readonly List<(Vehicle Car, bool Respawn)> _renewed = [];
    private readonly List<(Vehicle Victim, object? Attacker)> _kills = [];
    private readonly HashSet<Vehicle> _destroyed = [];
    private bool _endTold;

    public MeleeMode(ModeContext c)
    {
        _o = c.Options;
        _arena = c.Arena;
        _botRng = new Random(unchecked(_o.Seed * 7919 + 13));
        var roster = Enumerable.Range(0, _o.Ai).Select(i => _o.AiCars[i % _o.AiCars.Count]).Where(c.CarExists).Select(c.Legal).ToList();
        var rules = new MeleeRules
        {
            Network = true, TeamPlay = _o.TeamPlay || _o.Kind == MeleeKind.Ctf, MaxScore = _o.MaxScore, TimeLimit = _o.TimeLimit, RespawnDelay = _o.RespawnDelay,
            Kind = _o.Kind, Goal = _o.Goal, GoalValue = _o.GoalValue, CtfTeams = Math.Clamp(_o.CtfTeams, 2, 4),
        };
        Setup = MissionSetup.Build(c.Vfs, c.World, c.Asm, rand: new CrtRand(unchecked((uint)_o.Seed)), melee: roster, meleeRules: rules, seed: _o.Seed);
        Game = Setup.Melee!;
        Build = new SessionBuild { Mission = c.Arena.ToLowerInvariant(), Install = SessionBuild.InstallIdentity(c.Install, c.Arena), Melee = roster, Rules = rules };
    }

    public bool Over => Game.Over;
    public bool Ended => Game.Ended;
    public ModeRules Rules => new(Game.Rules.TeamPlay, Game.Rules.Kind, Game.Rules.ResolvedGoal, Game.Rules.ResolvedValue, (byte)Game.Rules.CtfTeams);
    public IReadOnlyList<BotSeat> Bots => _bots;
    /// <summary>How many bots the host wants: <see cref="ServerOptions.Bots"/>, fewer by the ones the host booted (a successor host starts from the options again).</summary>
    public int BotLimit { get; private set; }

    public void Start(IModeHost host)
    {
        _host = host;
        Game.Spawned += (v, def) => host.Spawned(v, def);
        Game.Removed += v => { host.Removed(v); };
        Game.PlayerRenewed += v => _renewed.Add((v, _destroyed.Contains(v)));
        Setup.Damage.KilledBy += (victim, attacker) => _kills.Add((victim, attacker));
        Game.Message += text => { host.Emit(new ChatEvent(-1, text)); host.Log(text); };
        Game.Start();
    }

    public void Opened()
    {
        BotLimit = _o.Bots;
        BalanceBots(0, announce: false);
    }

    // ---- seats ----

    public void SeatsChanging(int extraHumans) => BalanceBots(extraHumans);

    public ISeatActor Admit(string car, string name, int team, I76.Formats.Imaging.CarPaint? paint = null) => new MeleeActor(Game.AddPlayer(car, name, TeamFor(team), paint: paint));

    public void Release(Seat s)
    {
        Game.RemovePlayer(((MeleeActor)s.Actor).Player);
        _destroyed.Remove(s.Actor.Car);
    }

    /// <summary>
    /// A joiner's team: 0 in a free for all; in CTF one of 1..teams (the asked one, else the team with the fewest players);
    /// else the asked one (1–8, Grey Hounds to Men In Black, 1 if none).
    /// </summary>
    public int TeamFor(int asked)
    {
        if (!Game.Rules.TeamPlay) return 0;
        if (Game.Rules.Kind != MeleeKind.Ctf) return Math.Clamp(asked, 1, 8);
        int n = Game.Rules.CtfTeams;
        if (asked >= 1 && asked <= n) return asked;
        return Enumerable.Range(1, n).OrderBy(t => _host.Seats.Count(x => x.Actor.Team == t)).First();
    }

    public string? RaiseDead(Seat s) =>
        // RAISE_DEAD: the dead player's SR, refused before 5 s and once won
        s.Player.Destroyed ? Game.RequestRespawn(s.Player) : null;

    public string? CanReconfigure(Seat s) => !s.Player.Destroyed ? "only while your car is destroyed" : Game.Ended ? "the game is over" : null;

    public void Reconfigure(Seat s, string car, I76.Formats.Imaging.CarPaint? paint = null)
    {
        Game.Build(car, true);       // throws for a car that does not build here, the old car staying
        // a new paint alone is a rebuild too (the same car again): the clients are told by the car-changed event
        bool same = car.Equals(s.Player.CarFile, StringComparison.OrdinalIgnoreCase) && Equals(paint, s.Player.Paint);
        s.Player.NextCar = same ? null : car;
        s.Player.Paint = paint;
    }

    // ---- bots ----

    /// <summary>The cars bots may drive: <see cref="ServerOptions.BotCars"/> this install can build and the game lets in.</summary>
    private List<string>? _botCarList;

    private List<string> BotCarList() => _botCarList ??= [.. _o.BotCars.Where(c => _host.CarExists(c) && _host.CheckCar(c, null, restrictions: false) is null).Select(_host.Legal)];

    /// <summary>
    /// The bots the game should have now: <see cref="ServerOptions.Bots"/>, or fewer when the seats humans hold
    /// (<paramref name="extraHumans"/> more about to sit) leave less room under <see cref="ServerOptions.MaxPlayers"/>. A bot over
    /// the limit leaves (its car blown up as a leaver's); one missing comes in, once the game is not over.
    /// </summary>
    public void BalanceBots(int extraHumans, bool announce = true)
    {
        int want = _host.Over || Game.Ended ? _bots.Count : Math.Clamp(Math.Min(BotLimit, _o.MaxPlayers - _host.Seats.Count - extraHumans), 0, Protocol.MaxSeats);
        while (_bots.Count > want) RemoveBot(_bots[^1]);
        while (_bots.Count < want && AddBot(announce)) { }
    }

    private bool AddBot(bool announce)
    {
        int slot = Protocol.MaxSeats - 1;
        while (slot >= 0 && (_host.Seats.Any(x => x.Slot == slot) || _bots.Any(x => x.Slot == slot))) slot--;
        if (slot < 0) return false;
        var taken = Game.Players.Select(x => x.Name).ToHashSet();
        string name;
        int tries = 0;
        do name = I76.Formats.Shell.MeleeDrivers.Names[_botRng.Next(I76.Formats.Shell.MeleeDrivers.Names.Length)] + BotNames.Suffix; while (!taken.Add(name) && ++tries < 200);
        string? car = _o.BotChoices.ElementAtOrDefault(Protocol.MaxSeats - 1 - slot) is { Length: > 0 } pick ? _host.BotChoice(pick) : null;
        if (car is null)
        {
            // Random: under restrictions the host's saved variants that already carry none of the restricted weapons come first
            var saved = _host.LegalSavedCars();
            var cars = BotCarList();
            if (!_o.Restrictions.IsEmpty && saved.Count > 0) car = saved[_botRng.Next(saved.Count)];
            else if (cars.Count > 0) car = cars[_botRng.Next(cars.Count)];
            else return false;
        }
        var p = Game.AddBot(car, name, BotTeam(), _o.BotSkill, _o.PaintOfCar?.Invoke(_host.OriginOf(car)));
        _host.Session.AssignIds();
        p.BotSlot = slot;
        var bot = new BotSeat { Slot = slot, Name = name, Player = p };
        _bots.Add(bot);
        _bots.Sort((a, b) => a.Slot.CompareTo(b.Slot));
        if (announce) _host.Emit(new JoinedEvent((byte)slot, name, (byte)p.Team));
        _host.Log($"bot: {name} in slot {slot}, {car}, team {p.Team}, car {bot.CarSerial}");
        return true;
    }

    /// <summary>The host boots a bot (BOOT_n on its row of the score window, i26-server's kick): it leaves as a bot sent away does, and no other takes its place.</summary>
    public string? BootBot(string who)
    {
        who = who.Trim();
        var bot = int.TryParse(who, out int slot) ? _bots.FirstOrDefault(x => x.Slot == slot) : null;
        bot ??= _bots.FirstOrDefault(x => x.Name.Equals(who, StringComparison.OrdinalIgnoreCase))
            ?? (_bots.Where(x => x.Name.StartsWith(who, StringComparison.OrdinalIgnoreCase)).ToList() is { Count: 1 } one ? one[0] : null);
        if (bot is null) return $"no player {who}";
        BotLimit = Math.Max(0, BotLimit - 1);
        RemoveBot(bot);
        _host.Log($"{bot.Name} was booted");
        return null;
    }

    private void RemoveBot(BotSeat b)
    {
        if (!_bots.Remove(b)) return;
        Game.RemovePlayer(b.Player);
        _destroyed.Remove(b.Player.Car);
        _host.Emit(new LeftEvent((byte)b.Slot, "a seat was wanted"));
        _host.Log($"bot: {b.Name} (slot {b.Slot}) leaves; {b.Player.Score} points, {b.Player.Kills} kills, {b.Player.Deaths} deaths");
    }

    /// <summary>
    /// A bot's team: none in a free for all; in CTF the team with the fewest players; in other team play the smaller of the teams
    /// the humans are on (two, if they are all on one: the next one over), so that the bots fill the side that is short.
    /// </summary>
    private int BotTeam()
    {
        if (!Game.Rules.TeamPlay) return 0;
        var members = Game.Players;
        if (Game.Rules.Kind == MeleeKind.Ctf)
            return Enumerable.Range(1, Game.Rules.CtfTeams).OrderBy(t => members.Count(x => x.Team == t)).First();
        var used = members.Where(x => !x.IsBot).Select(x => x.Team).Where(t => t >= 1).Distinct().OrderBy(t => t).ToList();
        if (used.Count == 0) used = [1, 2];
        else if (used.Count == 1) used.Add(used[0] == 1 ? 2 : 1);
        return used.OrderBy(t => members.Count(x => x.Team == t)).ThenBy(t => t).First();
    }

    // ---- the tick ----

    public void Collect()
    {
        foreach (var (victim, attacker) in _kills)
        {
            // a leaver's car blown up as it went: no kill line (the player is gone, nothing is booked)
            if (Game.Departed.Contains(victim)) continue;
            int points = 0;
            var killer = attacker is Vehicle a ? PlayerOf(a) : null;
            var dead = PlayerOf(victim);
            if (Game.Rules.Kind == MeleeKind.Combat && attacker is Vehicle av0 && killer is not null && av0 != victim && av0.P.Mass > 0
                && !(Game.Rules.TeamPlay && dead is not null && dead.Team == killer.Team))
                points = (int)(1000f * victim.P.Mass / av0.P.Mass);
            _host.Emit(new KillEvent(Id(victim), attacker is Vehicle av ? Id(av) : 0, points));
            if (dead is not null)
                _host.Log($"kill: {dead.Name} destroyed by {(attacker is Vehicle k ? killer?.Name ?? Setup.Script!.Host.Of(k)?.Name : "nobody")}{(points > 0 ? $", {points} points" : "")}");
        }
        _kills.Clear();
        foreach (var s in _host.Seats)
        {
            if (s.Actor.Destroyed) _destroyed.Add(s.Actor.Car);
            var now = (s.Actor.Score, s.Actor.Kills, s.Actor.Deaths);
            if (now == s.Told) continue;
            s.Told = now;
            _host.Emit(new ScoreEvent((byte)s.Slot, now.Item1, now.Item2, now.Item3));
            _host.Log($"score: {s.Name} {now.Item1} ({now.Item2} kills, {now.Item3} deaths)");
        }
        foreach (var b in _bots)
        {
            if (b.Player.Destroyed) _destroyed.Add(b.Player.Car);
            var now = (b.Player.Score, b.Player.Kills, b.Player.Deaths);
            if (now == b.Told) continue;
            b.Told = now;
            _host.Emit(new ScoreEvent((byte)b.Slot, now.Item1, now.Item2, now.Item3));
            _host.Log($"score: {b.Name} {now.Item1} ({now.Item2} kills, {now.Item3} deaths)");
        }
        foreach (var (v, _) in _renewed)
        {
            bool respawn = _destroyed.Remove(v);
            // a reconfigured respawn: the car is another car now, which the clients rebuild before its snapshots
            if (_host.Seats.FirstOrDefault(x => x.Actor.Car == v) is { } cs && cs.Actor.CarEpoch != cs.ToldEpoch)
            {
                cs.ToldEpoch = cs.Actor.CarEpoch;
                _host.Redefined(v, cs.Actor.CarFile);
                _host.Emit(new CarChangedEvent(_host.Info(v), (byte)cs.Actor.CarEpoch));
            }
            _host.Emit(new RenewedEvent(Id(v), respawn));
            if (PlayerOf(v) is { } rs) _host.Log($"{(respawn ? "respawn" : "regen")}: {rs.Name}");
        }
        _renewed.Clear();
        if (Game.Ended && !_endTold)
        {
            _endTold = true;
            var winner = Game.TimeUp ? null : Game.Winner;
            _host.Emit(new EndEvent(Game.TimeUp, (sbyte)(winner is null ? -1 : SlotOf(winner)), (byte)(Game.TimeUp ? 0 : Game.WinningTeam ?? 0), Game.WinCount));
            _host.Log(Game.TimeUp ? "the time limit has elapsed: the game ends in 10 s"
                : $"{(Game.Rules.TeamPlay ? $"team {Game.WinningTeam}" : winner?.Name ?? "?")} reached the goal ({Game.WinCount}): the game ends in 10 s");
        }
    }

    public NetEvent Concluded(bool rounds, double roundPause)
    {
        // the standings in the goal's own count: laps, captures or kills, else points
        int Count(MeleePlayer x) => Game.Rules.ResolvedGoal is MeleeGoal.Points ? x.Score : x.Kills;
        var standings = Everyone().OrderByDescending(x => Count(x.Player)).Select(x => ((byte)x.Slot, Count(x.Player))).ToList();
        _host.Log("game over: " + string.Join(", ", Everyone().OrderByDescending(x => x.Player.Score).Select(x => $"{x.Name} {x.Player.Score}")));
        return new GameOverEvent(standings, (byte)(rounds ? Math.Clamp((int)Math.Ceiling(roundPause), 1, 255) : 0));
    }

    public IEnumerable<PlayerRow> Board() =>
        _host.Seats.Select(s => new PlayerRow((byte)s.Slot, s.Name, (byte)s.Actor.Team, s.Actor.Score, s.Actor.Kills, s.Actor.Deaths,
            (ushort)Math.Clamp(s.Rtt, 0, 65535), s.CarSerial, !s.Actor.Destroyed))
            .Concat(_bots.Select(b => new PlayerRow((byte)b.Slot, b.Name, (byte)b.Player.Team, b.Player.Score, b.Player.Kills, b.Player.Deaths,
                0, b.CarSerial, !b.Player.Destroyed)));

    public List<FlagState> Flags() =>
        Game.Ctf is { } c ? [.. c.Flags.Select(f => new FlagState((byte)f.Team, (byte)f.State, f.Pos.ToVector3(), f.Carrier is { } p ? p.Car.NetId & 0xffffff : 0))] : [];

    /// <summary>Everyone at the table, humans and bots, by slot: slot, name, player.</summary>
    public IEnumerable<(int Slot, string Name, MeleePlayer Player)> Everyone() =>
        _host.Seats.Select(s => (s.Slot, s.Name, s.Player)).Concat(_bots.Select(b => (b.Slot, b.Name, b.Player))).OrderBy(x => x.Slot);

    private static int Id(Vehicle v) => v.NetId & 0xffffff;

    /// <summary>The game's player a car belongs to (a human's or a bot's), null for the AI fill and wrecks.</summary>
    private MeleePlayer? PlayerOf(Vehicle v) => Game.Players.FirstOrDefault(p => p.Car == v);

    /// <summary>A player's slot: the seat's or the bot's.</summary>
    private int SlotOf(MeleePlayer p) => _host.Seats.FirstOrDefault(x => x.Player == p)?.Slot ?? _bots.FirstOrDefault(x => x.Player == p)?.Slot ?? -1;

    // ---- host migration ----

    public List<(ISeatActor Actor, Driver Driver)> Restore(IReadOnlyList<Driver> drivers, IReadOnlyDictionary<int, string> names)
    {
        _destroyed.Clear();
        var seats = new List<(ISeatActor, Driver)>();
        foreach (var d in drivers)
        {
            if (Game.Players.FirstOrDefault(p => p.Car == d.Car) is not { } p) continue;
            seats.Add((new MeleeActor(p), d));
            if (p.Destroyed) _destroyed.Add(p.Car);
        }
        // the bots, as the savestate has them (the ones this fresh game built are among the dropped), under their slots
        _bots.Clear();
        foreach (var p in Game.Players.Where(p => p.IsBot && p.BotSlot >= 0))
        {
            _bots.Add(new BotSeat { Slot = p.BotSlot, Name = p.Name, Player = p, Told = (p.Score, p.Kills, p.Deaths) });
            if (p.Destroyed) _destroyed.Add(p.Car);
        }
        _bots.Sort((a, b) => a.Slot.CompareTo(b.Slot));
        _renewed.Clear();
        _kills.Clear();
        // the end announced already (the clients had it from the old host)
        _endTold = Game.Ended;
        return seats;
    }
}
