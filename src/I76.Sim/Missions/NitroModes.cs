using I76.Sim.Math;

namespace I76.Sim.Missions;

/// <summary>What a network melee is: the base combat, or the Nitro Pack's race or capture the flag.</summary>
public enum MeleeKind : byte { Combat, Race, Ctf }

/// <summary>What ends a melee when a player (or team) reaches the goal value.</summary>
public enum MeleeGoal : byte { None, Laps, Captures, Points, Kills }

/// <summary>
/// The race's lap counting for one car (the lap count). Index i
/// is check(i+1); the car starts behind check1, so the first pass of it only
/// arms the sequence, and a lap counts on each wrap. With a check99 finish the
/// wrap goes through it instead of check1. A checkpoint is a 30 m 3-D sphere
/// round its centre, and they must be taken in order.
/// </summary>
public sealed partial class RaceTracker
{
    public const float Radius = 30f;
    private readonly Double3[] _cp;
    private readonly Double3? _finish;
    private int _cur = -1;

    /// <summary>Laps counted so far.</summary>
    public int Laps { get; private set; }
    /// <summary>The checkpoint last passed (its index, from 0; −1 before the first pass).</summary>
    public int Current => _cur;

    /// <summary>The checkpoints by their number (check1, check2 ... and check99 as the finish).</summary>
    public RaceTracker(IReadOnlyDictionary<int, Double3> checks)
    {
        _finish = checks.TryGetValue(99, out var f) ? f : null;
        int last = checks.Keys.Where(n => n < 99).DefaultIfEmpty(0).Max();
        // a gap in the numbering is a zero slot at the origin, as in the original
        _cp = new Double3[last];
        for (int i = 0; i < last; i++) _cp[i] = checks.GetValueOrDefault(i + 1);
    }

    public bool HasCheckpoints => _cp.Length > 0;

    /// <summary>
    /// The points the car has still to pass, in order, for <paramref name="count"/> of them from the next: the checkpoints and,
    /// with a check99 finish, the finish where a lap wraps (the same walk <see cref="Update"/> makes). <paramref name="before"/>
    /// holds <see cref="Current"/> as it will be when each point is the next, so a bot can tell where in its route the tracker is.
    /// </summary>
    public Double3[] Route(int count, out int[] before)
    {
        var pts = new Double3[count];
        before = new int[count];
        int cur = _cur;
        for (int i = 0; i < count; i++)
        {
            before[i] = cur;
            int next = cur + 1;
            if (next > _cp.Length - 1)
            {
                pts[i] = _finish ?? _cp[0];
                cur = 0;
            }
            else { pts[i] = _cp[next]; cur = next; }
        }
        return pts;
    }

    /// <summary>The number of numbered checkpoints (check1 ...), not counting the finish.</summary>
    public int Count => _cp.Length;

    /// <summary>One frame at the car's position; true when this one counted a lap.</summary>
    public bool Update(Double3 pos)
    {
        if (_cp.Length == 0) return false;
        int next = _cur + 1;
        bool wrapped = false, atFinish = false;
        if (next > _cp.Length - 1)
        {
            wrapped = true;
            if (_finish is not null) atFinish = true; else next = 0;
        }
        var c = atFinish ? _finish!.Value : _cp[next];
        double dx = pos.X - c.X, dy = pos.Y - c.Y, dz = pos.Z - c.Z;
        if (dx * dx + dy * dy + dz * dz >= Radius * Radius) return false;
        _cur = atFinish ? 0 : next;
        if (wrapped) Laps++;
        return wrapped;
    }
}

/// <summary>One flag of a CTF game: where it lives, whose it is, its state and who carries it.</summary>
public sealed class CtfFlag
{
    /// <summary>States of the flag's state change: 0 inactive (no team member), 1 at home, 2 dropped (never entered, as in the original), 3 captured (transient, never held), 4 carried.</summary>
    public const int Inactive = 0, Home = 1, Dropped = 2, Captured = 3, Carried = 4;
    public required int Number { get; init; }
    /// <summary>The team (1–4) whose flag this is.</summary>
    public required int Team { get; init; }
    public required Double3 HomePos { get; init; }
    /// <summary>The stub's collision radius: 1.2 x the flag's bounding radius.</summary>
    public float StubRadius { get; init; } = CtfGame.DefaultStubRadius;
    public int State { get; internal set; }
    public Double3 Pos { get; internal set; }
    public MeleePlayer? Carrier { get; internal set; }
    /// <summary>Where the stub stands: home, or where it was dropped.</summary>
    public Double3 StubPos => State == Dropped ? Pos : HomePos;
}

/// <summary>
/// Capture the flag, the server side (the flag's collision handling).
/// Pickup only at the home stand and by an enemy carrying nothing; capture by
/// touching any stand of one's own colour while carrying an enemy flag; one's
/// own flag is never returned by touch; a carried flag stays on the wreck and
/// goes home when the car is removed or respawned.
/// </summary>
public sealed class CtfGame
{
    /// <summary>The stub radius where the flag's own bounding radius is not known (the flag pole's, about 3.5 m, x 1.2).</summary>
    public const float DefaultStubRadius = 4.2f;
    /// <summary>The CTF team names (teams 1–4).</summary>
    public static readonly string[] TeamNames = ["Red Dawn", "Gang Green", "Purple Reign", "Black Plague"];

    public List<CtfFlag> Flags { get; } = [];
    /// <summary>Each team's captures (team number to count).</summary>
    public Dictionary<int, int> TeamCaptures { get; } = [];
    /// <summary>The console lines (the "***" texts), in order.</summary>
    public event Action<string>? Message;
    /// <summary>A capture happened: the capturing team and the flag's.</summary>
    public event Action<int, int>? Captured;

    public static string TeamName(int team) => TeamNames[System.Math.Clamp(team - 1, 0, 3)];

    /// <summary>
    /// Which team an a1flagN belongs to. OPEN QUESTION (the
    /// placement's flags are 0, and the code does not settle where team N−1
    /// comes from): the most plausible is the number in the name, the same
    /// way spawnN picks team table N−1. Teams here are 1-based. Change this
    /// function alone if the original game turns out otherwise.
    /// </summary>
    public static int FlagTeam(int number) => System.Math.Clamp(number, 1, 4);

    public void AddFlag(int number, Double3 home) =>
        Flags.Add(new CtfFlag { Number = number, Team = FlagTeam(number), HomePos = home, Pos = home });

    /// <summary>The flag a player carries.</summary>
    public CtfFlag? Carried(MeleePlayer p) => Flags.FirstOrDefault(f => f.Carrier == p);

    /// <summary>One frame: a team without players has its flag inactive, one with them has it at home again; the carried flag follows its car; then the contacts.</summary>
    public void Update(IReadOnlyList<MeleePlayer> players, Func<MeleePlayer, bool> alive, Func<MeleePlayer, float> carRadius)
    {
        foreach (var f in Flags)
        {
            bool present = players.Any(p => p.Team == f.Team);
            if (!present && f.State == CtfFlag.Home)
            {
                f.State = CtfFlag.Inactive;
                Message?.Invoke($"*** The {TeamName(f.Team)} team has left the game!");
            }
            else if (present && f.State == CtfFlag.Inactive) Home(f, announce: false);
            if (f.State == CtfFlag.Carried && f.Carrier is { } c) f.Pos = c.Car.Pos;
        }
        foreach (var p in players)
        {
            if (!alive(p)) continue;
            var at = p.Car.Pos;
            foreach (var stub in Flags)
            {
                var s = stub.StubPos;
                double dx = at.X - s.X, dy = at.Y - s.Y, dz = at.Z - s.Z, r = stub.StubRadius + carRadius(p);
                if (dx * dx + dy * dy + dz * dz > r * r) continue;
                Touch(p, stub, players);
            }
        }
    }

    private void Touch(MeleePlayer p, CtfFlag stub, IReadOnlyList<MeleePlayer> players)
    {
        if (p.Team != stub.Team)
        {
            // pickup: the stub holds its flag (at home or dropped, never inactive), and the car carries nothing
            if (Carried(p) is null && stub.Carrier is null && stub.State is CtfFlag.Home or CtfFlag.Dropped)
            {
                stub.State = CtfFlag.Carried;
                stub.Carrier = p;
                Message?.Invoke($"*** {TeamName(stub.Team)}'s flag has been taken!");
            }
        }
        else if (Carried(p) is { } flag)
        {
            // capture at any stand of one's own colour, whether or not one's own flag is at home
            Home(flag, announce: false);
            TeamCaptures[p.Team] = TeamCaptures.GetValueOrDefault(p.Team) + 1;
            foreach (var m in players)
            {
                if (m.Team == p.Team) m.Kills++;          // The goal counter
                else if (m.Team == flag.Team) m.Deaths++;
            }
            Message?.Invoke($"*** Team {TeamName(p.Team)} has captured {TeamName(flag.Team)}'s flag!");
            Captured?.Invoke(p.Team, flag.Team);
        }
        // else: one's own flag, nothing carried: never a return by touch
    }

    private void Home(CtfFlag f, bool announce)
    {
        f.State = CtfFlag.Home;
        f.Carrier = null;
        f.Pos = f.HomePos;
        if (announce) Message?.Invoke($"*** {TeamName(f.Team)}'s flag has been returned to its base!");
    }

    /// <summary>The carrier's car was removed or respawned (the flag's return home): the flag goes home.</summary>
    public void CarrierGone(MeleePlayer p)
    {
        if (Carried(p) is { } f) Home(f, announce: true);
    }
}

/// <summary>The goal check's counts, shared by the server's end and the client's goal line.</summary>
public static class GoalCount
{
    /// <summary>
    /// The leading count: per player, or per team in team play. Laps and
    /// points and kills sum over a team; captures are the team's own (every
    /// member holds the same count, so the largest member's).
    /// </summary>
    public static int Leader(MeleeGoal goal, bool teamPlay, IEnumerable<(int Team, int Score, int Kills)> rows)
    {
        int One((int Team, int Score, int Kills) r) => goal == MeleeGoal.Points ? r.Score : r.Kills;
        var list = rows.ToList();
        if (list.Count == 0) return 0;
        if (!teamPlay) return list.Max(One);
        var teams = list.GroupBy(r => r.Team);
        return goal == MeleeGoal.Captures ? teams.Max(g => g.Max(One)) : teams.Max(g => g.Sum(One));
    }
}
