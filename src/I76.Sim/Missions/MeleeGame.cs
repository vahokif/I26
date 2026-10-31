using I76.Sim.Fsm;
using I76.Sim.Math;
using I76.Sim.Rng;
using I76.Sim.Vehicles;

namespace I76.Sim.Missions;

/// <summary>
/// What kind of melee: the original's single-player one, or a network melee
/// (docs/multiplayer.md) with many human players, respawns and an end.
/// </summary>
public sealed record MeleeRules
{
    /// <summary>The network melee: humans added with <see cref="MeleeGame.AddPlayer"/>, each scored, respawned and regenerated on their own.</summary>
    public bool Network { get; init; }
    /// <summary>Teams (the players' Entity team, 1–7): no score for a teammate's death, and the end counts a team's total.</summary>
    public bool TeamPlay { get; init; }
    /// <summary>The melee ends once a player (a team, in team play) has this many points; 0 for no limit. Kept for the points goal: <see cref="GoalValue"/> wins where both are set.</summary>
    public int MaxScore { get; init; }
    /// <summary>Combat, or the Nitro Pack's race or capture the flag.</summary>
    public MeleeKind Kind { get; init; }
    /// <summary>CTF: how many teams play (2–4).</summary>
    public int CtfTeams { get; init; } = 2;
    /// <summary>What the goal value counts. None with a goal value means points (a race or CTF: its own laps or captures).</summary>
    public MeleeGoal Goal { get; init; }
    /// <summary>The goal's limit (laps, captures, points or kills); 0 for none.</summary>
    public int GoalValue { get; init; }

    /// <summary>The goal in force: the one set, else the kind's own (laps, captures) or points.</summary>
    public MeleeGoal ResolvedGoal => Goal != MeleeGoal.None ? Goal
        : Kind == MeleeKind.Race ? MeleeGoal.Laps : Kind == MeleeKind.Ctf ? MeleeGoal.Captures : MeleeGoal.Points;
    /// <summary>The limit in force (the goal value, else the points limit for a points goal); 0 for none: unlimited laps or captures end only by the time limit.</summary>
    public int ResolvedValue => GoalValue > 0 ? GoalValue : ResolvedGoal == MeleeGoal.Points ? MaxScore : 0;
    /// <summary>The melee ends at this mission time (seconds); 0 for no limit.</summary>
    public float TimeLimit { get; init; }
    /// <summary>
    /// A destroyed player may ask for a new car (RAISE_DEAD) this long after the death: a press before is
    /// refused (the death time + 5 s). There is no automatic respawn for a player in the original.
    /// </summary>
    public float RespawnDelay { get; init; } = 5f;

    /// <summary>The original's single-player melee.</summary>
    public static MeleeRules SinglePlayer { get; } = new();
}

/// <summary>A human driver in a melee: the car (renewed in place), its entity, and the score.</summary>
public sealed class MeleePlayer
{
    public required Vehicle Car { get; init; }
    public required Entity Entity { get; init; }
    /// <summary>The car's .vcf, for a fresh one (a regen, a respawn).</summary>
    public required string CarFile { get; set; }
    /// <summary>
    /// The car the next respawn builds instead, once the server has checked it (I26's Reconfigure while dead, not in the
    /// original); null to keep <see cref="CarFile"/>.
    /// </summary>
    public string? NextCar { get; set; }
    /// <summary>The player's respray (cosmetic; null = the factory paint): put on the car whenever it is built, and a Reconfigure may change it.</summary>
    public I76.Formats.Imaging.CarPaint? Paint { get; set; }
    /// <summary>How many times the car was rebuilt as another car (a reconfigured respawn): its client's own-car block carries it.</summary>
    public int CarEpoch { get; internal set; }
    public string Name { get; init; } = "";
    /// <summary>
    /// A bot (docs/multiplayer.md "Bots"): the server's AI drives this player's car by the game's mode. 0 for a human; else
    /// the AI tier, 1 (best) to 5 (worst) as setSkill's tables (<see cref="Ai.AiBlock.SetSkill"/>).
    /// </summary>
    public int BotSkill { get; init; }
    public bool IsBot => BotSkill > 0;
    /// <summary>A bot's slot in the roster the clients see (the host assigns it; saved, so that a successor host keeps the bots under their slots).</summary>
    public int BotSlot { get; set; } = -1;
    /// <summary>What a bot is doing now (its target, path, role); not saved: a restored game's bots think again.</summary>
    internal BotBrain? Brain;
    /// <summary>The side (the entity's flags bits 16–18, <see cref="Entity.Team"/>): 0 nobody's (free for all), 1–8 a team.</summary>
    public int Team => Entity.Team;
    public int Score { get; internal set; }
    /// <summary>Kills; in a race the laps and in CTF the captures (the original's one counter), since kills score nothing there.</summary>
    public int Kills { get; internal set; }
    public int Laps => Kills;
    public int Captures => Kills;
    public int Deaths { get; internal set; }
    /// <summary>Dead and waiting for the respawn (a network melee), or out for good (single player).</summary>
    public bool Destroyed { get; internal set; }
    /// <summary>The earliest a respawn may be asked for (mission time: the death + <see cref="MeleeRules.RespawnDelay"/>), while destroyed in a network melee.</summary>
    public float? RespawnAt { get; internal set; }
    /// <summary>RAISE_DEAD was pressed in time: the next frame gives the player a fresh car (the host's SR handler).</summary>
    public bool RespawnWanted { get; internal set; }
    // the regen pad: the last spawn or regen, and whether the car has left the pad since
    internal float RegenAt;
    internal bool OffPad;
    /// <summary>What the hit that destroyed the car books against its driver (the vehicle slot hands it to the kill-credit routine).</summary>
    internal DeathCredit Death;
}

/// <summary>
/// What a player's death books (the vehicle damage slot books only with no killer, or a killer of
/// another team; in a free for all every other machine's car is another team, the own car is not).
/// </summary>
public enum DeathCredit
{
    /// <summary>No killer (a crash, SELF_DESTRUCT, the end's blow-up): a death and −500 (the kill-credit routine with no killer).</summary>
    Nobody,
    /// <summary>Another side's car or round: a death, no points lost.</summary>
    Killer,
    /// <summary>One's own weapon, or a teammate's: nothing is booked, not even the death.</summary>
    Free,
}

/// <summary>
/// A melee, the engine side. Single player: the arena's
/// spawn and regen points, the player and the MULTICAR roster built at
/// random free spawn points, no script — every live opponent is ordered to
/// attack the player every frame — wrecks removed 10 s after
/// they die, the roster respawned a skill step better when the last one
/// goes, kills scored by mass, the regen pads, and the end five seconds after the player is destroyed.
///
/// A network melee (<see cref="MeleeRules.Network"/>, docs/multiplayer.md)
/// keeps the spawn, score and regen rules but per player: any number of
/// humans, each scored 1000 × victim mass / own mass for a kill, respawned
/// after a delay at a free spawn point, each with their own regen timer;
/// optional teams; the roster, if any, is AI fill that attacks the nearest
/// human and comes back one car at a time; it ends at a score or a time.
/// </summary>
public sealed partial class MeleeGame
{
    /// <summary>A spawn or regen point: the placement's rotation and position.</summary>
    public sealed record Spot(Rows Rot, Double3 Pos);

    /// <summary>The base game's team names (through lang.txt; CHOOSE A TEAM's rows): team n is entry n − 1.</summary>
    public static readonly string[] TeamNames =
        ["Grey Hounds", "Puce Panthers", "Crimson Kings", "Yellow Jackets", "Aqua Marines", "Mauve Mayhem", "Mr. Brown's Clowns", "Men In Black"];

    /// <summary>A team's name: CTF's four colours, else the base game's eight.</summary>
    public static string TeamName(MeleeKind kind, int team) => kind == MeleeKind.Ctf ? CtfGame.TeamName(team) : team is >= 1 and <= 8 ? TeamNames[team - 1] : $"{team}";

    public List<Spot> Spawns { get; } = [];
    public List<Spot> Regens { get; } = [];
    /// <summary>CTF: the spawn tables by team (index 0–4: spawn1 is team 1's; a CTF arena has no other table).</summary>
    public List<Spot>[] TeamSpawns { get; } = [[], [], [], [], []];
    /// <summary>The race's checkpoints by number (check1.., and 99 the finish).</summary>
    public Dictionary<int, Double3> Checkpoints { get; } = [];
    /// <summary>CTF: the flags and their rules (null in other kinds).</summary>
    public CtfGame? Ctf { get; set; }
    /// <summary>Destroys a car (the damage model's, set by the setup): the losers' cars at the end.</summary>
    public Action<Vehicle>? Explode { get; set; }
    /// <summary>The console's lines ("***"), from the flags and the end.</summary>
    public event Action<string>? Message;
    /// <summary>A race lap was counted for a player.</summary>
    public event Action<MeleePlayer>? LapCounted;
    private readonly Dictionary<MeleePlayer, RaceTracker> _races = [];
    private float? _overAt;
    /// <summary>Who won, by the player who reached the goal (null until someone has, and for a time limit).</summary>
    public MeleePlayer? Winner { get; private set; }
    /// <summary>The winning team in team play.</summary>
    public int? WinningTeam { get; private set; }
    /// <summary>The winner's count in the goal's own measure (points, kills, laps, captures), for the end's line.</summary>
    public int WinCount { get; private set; }
    /// <summary>
    /// The time limit ran out (Nitro Pack's per-frame game rules): "*** Time limit has elapsed!", every machine takes
    /// itself for the winner, nobody is blown up, and the mission ends 10 s later.
    /// </summary>
    public bool TimeUp { get; private set; }
    /// <summary>The goal was reached or the time ran out; the end follows (10 s after a win).</summary>
    public bool Ended => _overAt is not null;
    public required SimWorld World { get; init; }
    public required ScriptHost Host { get; init; }
    public required CrtRand Rand { get; init; }
    /// <summary>Builds a car from its .vcf (health and all), not yet placed or in the world.</summary>
    public required Func<string, bool, Vehicle> Build { get; init; }
    /// <summary>The opponents' cars, in MULTICAR.def's order (a network melee's AI fill).</summary>
    public required IReadOnlyList<string> Roster { get; init; }
    /// <summary>The player's car file (the player's vehicle file), for a regen's fresh car.</summary>
    public required string PlayerCar { get; init; }
    public MeleeRules Rules { get; init; } = MeleeRules.SinglePlayer;

    /// <summary>The human drivers: the player alone in single player, those added in a network melee.</summary>
    public List<MeleePlayer> Players { get; } = [];

    /// <summary>A car came into the world (to be drawn), or left it.</summary>
    public event Action<Vehicle, string>? Spawned;
    public event Action<Vehicle>? Removed;
    /// <summary>A regen pad or a respawn gave a player a fresh car (the same vehicle, rebuilt, at a spawn point).</summary>
    public event Action<Vehicle>? PlayerRenewed;

    /// <summary>The (first) player's score and kills.</summary>
    public int Score => Players.Count > 0 ? Players[0].Score : 0;
    public int Kills => Players.Count > 0 ? Players[0].Kills : 0;
    /// <summary>The skill step: 3 to start, one better each wave, down to 1.</summary>
    public int Aggression { get; private set; } = 3;
    /// <summary>The player's attacker cap: 8, one more each wave.</summary>
    public int AttackCap { get; private set; } = 8;
    /// <summary>The melee is over: single player (game state 1) five seconds after the player was destroyed; a network melee at its score or time limit.</summary>
    public bool Over => Rules.Network ? _over : _endAt is { } t && Host.Now >= t;
    public bool PlayerDestroyed { get; private set; }

    private readonly List<Entity> _opponents = [];
    /// <summary>Leavers' cars, blown up as they left and taken out once wrecked.</summary>
    private readonly List<Vehicle> _departed = [];
    private readonly Dictionary<Entity, int> _rosterSlot = [];
    private readonly HashSet<Entity> _credited = [];
    private int _alive;
    private float? _endAt;
    private bool _over, _started;

    /// <summary>
    /// A random point, then the next ones in turn until one has
    /// no live car within its radius + 20 of it. When every one is busy the
    /// original returns the first pick and the cars stack; here (a game with
    /// more cars than points) it is the point whose nearest live car is
    /// farthest, and <see cref="Place"/> spreads the car to a clear spot
    /// beside it if even that one is occupied (docs/deviations.md).
    /// </summary>
    public int PickSpawn(int team = 0, Vehicle? self = null)
    {
        var table = SpawnTable(team);
        if (table.Count == 0) return -1;
        int start = Rand.Next() % table.Count, i = start;
        for (int k = 0; k < table.Count; k++)
        {
            var p = table[i].Pos.ToVector3();
            bool busy = World.Vehicles.Any(v => (v.Health is null || !Damage.DamageModel.IsDead(v) || !v.Wreck)
                && System.Numerics.Vector3.DistanceSquared(v.Pos.ToVector3(), p) < (Radius(v) + 20) * (Radius(v) + 20));
            if (!busy) return i;
            i = (i + 1) % table.Count;
        }
        // every point busy: the one with the most room, the first pick's order breaking ties
        int best = start;
        float bestRoom = float.MinValue;
        for (int k = 0; k < table.Count; k++)
        {
            int j = (start + k) % table.Count;
            float room = Room(OnGround(table[j].Pos), self);
            if (room > bestRoom) { bestRoom = room; best = j; }
        }
        return best;
    }

    // where a car can stand (the spawn points' and co-op's shared rules: SpawnSpace)
    private System.Numerics.Vector3 OnGround(Double3 p) => SpawnSpace.OnGround(World, p);
    private float Room(System.Numerics.Vector3 at, Vehicle? self) => SpawnSpace.Room(World, at, self);
    private bool Clear(System.Numerics.Vector3 at, float radius, Vehicle? self) => SpawnSpace.Clear(World, at, radius, self);
    private Double3? SpreadFrom(Spot s, Vehicle v) => SpawnSpace.SpreadFrom(World, s.Rot, s.Pos, v);

    /// <summary>The spawn table for a team: in CTF the team's own (spawnN is team N; the first table that has any if its own is empty), else the one table.</summary>
    public List<Spot> SpawnTable(int team)
    {
        if (Rules.Kind != MeleeKind.Ctf) return Spawns;
        var t = TeamSpawns[System.Math.Clamp(team - 1, 0, 4)];
        return t.Count > 0 ? t : TeamSpawns.FirstOrDefault(x => x.Count > 0) ?? Spawns;
    }

    private static float Radius(Vehicle v) => SpawnSpace.Radius(v);

    private void Place(Vehicle v, int spot, int team = 0)
    {
        if (spot < 0) return;
        var s = SpawnTable(team)[spot];
        v.Rot = s.Rot;
        var pos = s.Pos;
        pos.Y = World.Ground.Query(pos.X, pos.Y + 2, pos.Z).Height;
        // the point is taken (more cars than free points): a clear spot beside it; none within reach stacks, as the original
        if (!Clear(pos.ToVector3(), Radius(v), v) && SpreadFrom(s, v) is { } spread)
        {
            pos = spread;
            pos.Y = World.Ground.Query(pos.X, pos.Y + 2, pos.Z).Height;
        }
        // a helicopter's flight model crashes it the moment the ground at its next position is above it (the helicopter's flight model: y < h
        // before the height eases up), so one set down exactly on a slope died on its first uphill inch of take-off (#97)
        if (v.IsHeli) pos.Y += Vehicles.HeliStep.SpawnClearance;
        v.Pos = pos;
        v.Vel = default;
        v.AngVel = default;
    }

    /// <summary>
    /// A human player in a network melee: the car built from its file with
    /// the player's damage rules, on <paramref name="team"/> (0 for a free for
    /// all), placed at a free spawn point at the start (or at once, once
    /// started). Players are added in a fixed order, which is the session's.
    /// </summary>
    public MeleePlayer AddPlayer(string carFile, string name = "", int team = 0, int botSkill = 0, I76.Formats.Imaging.CarPaint? paint = null)
    {
        if (!Rules.Network) throw new InvalidOperationException("players join a network melee only");
        var v = Build(carFile, true);
        v.Paint = paint;
        World.Vehicles.Add(v);
        World.Humans.Add(v);
        // the network game's flags for a player's object: the player bit, the team in bits 16-18
        var e = new Entity($"plyr{Players.Count:x}", [], 1, 0x10u | Entity.TeamFlags(team)) { Vehicle = v };
        Host.Add(e);
        var p = new MeleePlayer { Car = v, Entity = e, CarFile = carFile, Name = name, BotSkill = botSkill, Paint = paint };
        Players.Add(p);
        if (p.IsBot) { _botCars.Add(v); SetBotSkill(p); }
        Spawned?.Invoke(v, carFile);
        if (_started)
        {
            Place(v, PickSpawn(p.Team, v), p.Team);
            p.RegenAt = Host.Now;
            e.MaxAttackers = AttackCap;
        }
        return p;
    }

    /// <summary>
    /// A human player leaves a network melee: the player is off the scoreboard, and a live car is blown up
    /// (a death with no killer that books nothing, since only its owner booked its own deaths) and left as
    /// a wreck, taken out of the world as an AI wreck is, 10 s after it died. A car already dead goes once its wreck time
    /// is over. Nobody targets it any more.
    /// </summary>
    public void RemovePlayer(MeleePlayer p)
    {
        if (!Players.Remove(p)) return;
        Ctf?.CarrierGone(p);
        _races.Remove(p);
        _botCars.Remove(p.Car);
        World.Humans.Remove(p.Car);
        if (Host.Combat is { } c)
        {
            c.RadarTarget.Remove(p.Car);
            foreach (var k in c.RadarTarget.Where(kv => kv.Value == p.Car).Select(kv => kv.Key).ToList()) c.RadarTarget[k] = null;
        }
        if (_started && Rules.Network && Explode is not null && World.Vehicles.Contains(p.Car))
        {
            if (!Damage.DamageModel.IsDead(p.Car)) Explode(p.Car);
            _departed.Add(p.Car);
            return;
        }
        Host.Remove(p.Entity);
        Removed?.Invoke(p.Car);
    }

    /// <summary>The leavers' wrecks out of the world: once wrecked (10 s after the death, the AI's rule), or at once when the death's timer had already run out as a player's.</summary>
    private void SweepDeparted()
    {
        for (int i = _departed.Count - 1; i >= 0; i--)
        {
            var v = _departed[i];
            if (!v.Wreck && Damage.DamageModel.IsDead(v) && ((uint)v.Flags & 0x80000000) != 0) continue;
            if (!v.Wreck && !Damage.DamageModel.IsDead(v)) continue;
            _departed.RemoveAt(i);
            if (Host.Of(v) is { } e) Host.Remove(e);
            Removed?.Invoke(v);
        }
    }

    /// <summary>The leavers' cars still in the world, wrecks waiting to be taken out.</summary>
    public IReadOnlyList<Vehicle> Departed => _departed;

    /// <summary>
    /// RAISE_DEAD (the host's SR handler): a destroyed player asks for a new car. Refused
    /// (and not remembered: the key must be pressed again) until <see cref="MeleeRules.RespawnDelay"/> after the death,
    /// and once the game has a winner or is over. Granted, the next frame gives the player a fresh car at
    /// a free spawn point. Null when granted, else why not.
    /// </summary>
    public string? RequestRespawn(MeleePlayer p)
    {
        if (!Rules.Network || !Players.Contains(p)) return "not a network player";
        if (!p.Destroyed) return "not destroyed";
        if (Ended) return "the game is over";
        if (p.RespawnAt is { } at && Host.Now < at) return $"too soon ({at - Host.Now:0.0} s)";
        p.RespawnWanted = true;
        return null;
    }

    /// <summary>The original's start-up after the load: the players at spawn points, then the melee opponents' set-up (skill 3, cap 8).</summary>
    public void Start()
    {
        _started = true;
        if (Host.Combat is { } bc) bc.IsBot = _botCars.Contains;
        Host.HoldFire = FriendInLine;
        Host.HoldWeapon = BotHoldsWeapon;
        if (!Rules.Network && World.Player is { } sp && Host.Player is { } spe && Players.Count == 0)
            Players.Add(new MeleePlayer { Car = sp, Entity = spe, CarFile = PlayerCar });
        foreach (var p in Players)
        {
            Place(p.Car, PickSpawn(p.Team, p.Car), p.Team);
            p.RegenAt = Host.Now;
            p.OffPad = false;
        }
        if (Ctf is { } ctf) ctf.Message += s => Message?.Invoke(s);
        SpawnRoster();
        SetAllAggression(Aggression);
        foreach (var p in Players) p.Entity.MaxAttackers = AttackCap;
    }

    /// <summary>For each opponent: built, at a free spawn point, team 2 (a network melee's fill: nobody's).</summary>
    private void SpawnRoster()
    {
        _opponents.Clear();
        _rosterSlot.Clear();
        for (int n = 0; n < Roster.Count; n++) SpawnOpponent(n);
        _alive = _opponents.Count;
    }

    private Entity SpawnOpponent(int n)
    {
        var v = Build(Roster[n], false);
        Place(v, PickSpawn(0, v));
        World.Vehicles.Add(v);
        var e = new Entity($"nvcl{n:x}", [], 1, Rules.Network ? 0u : 0x20000) { Vehicle = v };
        Host.Add(e);
        _opponents.Add(e);
        _rosterSlot[e] = n;
        Spawned?.Invoke(v, Roster[n]);
        return e;
    }

    /// <summary>setAllAggression: every opponent's skill n, n and aggression row n − 1.</summary>
    private void SetAllAggression(int n)
    {
        foreach (var e in _opponents) SetAggression(e, n);
    }

    private void SetAggression(Entity e, int n)
    {
        Host.SetSkill(e, n, n);
        e.Aggression = n - 1;
    }

    /// <summary>One frame, after the vehicles moved (where the FSM would tick).</summary>
    public void Frame(float dt)
    {
        if (Rules.Network) { NetworkFrame(); return; }
        var player = Host.Player;
        if (player?.Vehicle is { } pv)
        {
            if (!PlayerDestroyed && Damage.DamageModel.IsDead(pv))
            {
                PlayerDestroyed = true;
                _endAt = Host.Now + 5f;
                foreach (var p in Players.Where(p => p.Car == pv)) { p.Destroyed = true; p.Deaths++; }
            }
            foreach (var e in _opponents)
                if (!e.Dead && !e.Removed) Host.Order(e, player, AiOrder.Attack, null, 0);
        }
        foreach (var e in _opponents.ToList())
        {
            if (e.Removed || e.Vehicle is not { } v) continue;
            // the kill credit, on the hit that destroyed it (the host marks the player's kills)
            if (e.Dead && Host.KilledByPlayer(e) && _credited.Add(e) && World.Player is { } p && p.P.Mass > 0 && Players.Count > 0)
            {
                Players[0].Kills++;
                Players[0].Score += (int)(1000f * v.P.Mass / p.P.Mass);
            }
            // a wreck leaves the world 10 s after it was destroyed (in a melee)
            if (v.Wreck)
            {
                Host.Remove(e);
                Removed?.Invoke(v);
                if (--_alive == 0) NextWave();
            }
        }
        if (World.Player is { } rp && !PlayerDestroyed && Players.FirstOrDefault(x => x.Car == rp) is { } me) Regen(me);
    }

    /// <summary>The whole roster again, one skill step better, the attacker cap one higher.</summary>
    private void NextWave()
    {
        _credited.Clear();
        SpawnRoster();
        Aggression = System.Math.Max(Aggression - 1, 1);
        SetAllAggression(Aggression);
        AttackCap++;
        if (Host.Player is { } pe) pe.MaxAttackers = AttackCap;
    }

    /// <summary>
    /// A network melee's frame: deaths and respawns, the AI fill's orders
    /// and its wrecks replaced one by one, every live player's regen pad,
    /// and the end.
    /// </summary>
    private void NetworkFrame()
    {
        foreach (var p in Players)
        {
            if (!p.Destroyed && Damage.DamageModel.IsDead(p.Car))
            {
                p.Destroyed = true;
                p.RespawnAt = Host.Now + Rules.RespawnDelay;
                p.RespawnWanted = false;
                // The score for a death: a death, and with no killer 500 points to a floor of 0 (network
                // combat only); one's own weapon or a teammate's books nothing. The losers blown up at the end have no
                // killer: they pay too (the original wrote NSCR.DAT after it)
                if (p.Death != DeathCredit.Free)
                {
                    p.Deaths++;
                    // (the Nitro Pack's score for a death: not in team play, nor in a race or CTF; the base game always)
                    if (p.Death == DeathCredit.Nobody && Rules.Kind == MeleeKind.Combat && !(Rules.TeamPlay && Host.Combat?.NitroRules == true))
                        p.Score = System.Math.Max(0, p.Score - 500);
                }
            }
            else if (p.Destroyed && p.RespawnWanted && !Ended)
            {
                Ctf?.CarrierGone(p);
                Renew(p, full: true);
                p.Death = DeathCredit.Nobody;
                Place(p.Car, PickSpawn(p.Team, p.Car), p.Team);
                p.Destroyed = false;
                p.RespawnAt = null;
                p.RespawnWanted = false;
                p.RegenAt = Host.Now;
                p.OffPad = false;
                PlayerRenewed?.Invoke(p.Car);
            }
        }
        SweepDeparted();
        BotsFrame();
        foreach (var e in _opponents)
            if (!e.Dead && !e.Removed && NearestPlayer(e.Position) is { } target) Host.Order(e, target.Entity, AiOrder.Attack, null, 0);
        foreach (var e in _opponents.ToList())
        {
            if (e.Removed || e.Vehicle is not { Wreck: true } v) continue;
            Host.Remove(e);
            _opponents.Remove(e);
            Removed?.Invoke(v);
            int slot = _rosterSlot[e];
            _rosterSlot.Remove(e);
            SetAggression(SpawnOpponent(slot), Aggression);
        }
        foreach (var p in Players) if (!p.Destroyed) Regen(p);
        if (Rules.Kind == MeleeKind.Race) RaceFrame();
        Ctf?.Update(Players, p => !p.Destroyed && !Damage.DamageModel.IsDead(p.Car), p => Radius(p.Car));
        if (_overAt is null) CheckEnd();
        else if (Host.Now >= _overAt) _over = true;
    }

    /// <summary>Each live car's lap counting (the lap count): a lap is +1 on the player's counter.</summary>
    private void RaceFrame()
    {
        if (Checkpoints.Count == 0) return;
        foreach (var p in Players)
        {
            if (!_races.TryGetValue(p, out var r)) _races[p] = r = new RaceTracker(Checkpoints);
            if (p.Destroyed || Damage.DamageModel.IsDead(p.Car)) continue;
            if (r.Update(p.Car.Pos)) { p.Kills++; LapCounted?.Invoke(p); }
        }
    }

    /// <summary>
    /// The goal (the goal check): the first to reach it wins, and the
    /// losers' cars blow up with the end 10 s later. A time
    /// limit ends the game at once, with no winner.
    /// </summary>
    private void CheckEnd()
    {
        int value = Rules.ResolvedValue;
        if (value > 0)
        {
            var goal = Rules.ResolvedGoal;
            (int? Team, MeleePlayer? Player, int N)? leader = null;
            if (Rules.TeamPlay)
            {
                foreach (var g in Players.GroupBy(p => p.Team))
                {
                    int n = goal == MeleeGoal.Captures && Ctf is { } c ? c.TeamCaptures.GetValueOrDefault(g.Key) : GoalCount.Leader(goal, true, g.Select(Row));
                    if (n >= value && (leader is null || n > leader.Value.N)) leader = (g.Key, null, n);
                }
            }
            else
                foreach (var p in Players)
                {
                    int n = GoalCount.Leader(goal, false, [Row(p)]);
                    if (n >= value && (leader is null || n > leader.Value.N)) leader = (null, p, n);
                }
            if (leader is { } l)
            {
                WinningTeam = l.Team;
                Winner = l.Player ?? Players.First(p => p.Team == l.Team);
                WinCount = l.N;
                _overAt = Host.Now + 10f;
                // each machine says it in its own words ("You win!", "You lose, chump!": the host's end event); a loser's
                // own machine blows its car up (the original's start-up), a dead one stays as it is
                foreach (var p in Players)
                    if (p != Winner && !(Rules.TeamPlay && p.Team == WinningTeam) && !p.Destroyed && !Damage.DamageModel.IsDead(p.Car))
                        Explode?.Invoke(p.Car);
                return;
            }
        }
        // the time limit (Nitro Pack's per-frame game rules): every machine is the winner, nobody is blown up, the end 10 s later
        if (Rules.TimeLimit > 0 && Host.Now >= Rules.TimeLimit)
        {
            TimeUp = true;
            _overAt = Host.Now + 10f;
            Message?.Invoke("*** Time limit has elapsed!");
        }
    }

    private static (int Team, int Score, int Kills) Row(MeleePlayer p) => (p.Team, p.Score, p.Kills);


    /// <summary>The nearest live human to a point (the first added on a tie); a game with no live human has the bots to be after.</summary>
    private MeleePlayer? NearestPlayer(System.Numerics.Vector3 at) => NearestPlayer(at, false) ?? NearestPlayer(at, true);

    private MeleePlayer? NearestPlayer(System.Numerics.Vector3 at, bool bots)
    {
        MeleePlayer? best = null; float bd = float.MaxValue;
        foreach (var p in Players)
        {
            if (p.IsBot != bots || p.Destroyed || Damage.DamageModel.IsDead(p.Car)) continue;
            float d = System.Numerics.Vector3.DistanceSquared(p.Car.Pos.ToVector3(), at);
            if (d < bd) { bd = d; best = p; }
        }
        return best;
    }

    /// <summary>The scores to end on: each player's, or in team play each team's total (team, score).</summary>
    public IEnumerable<(int Team, int Score)> Standings() => Rules.TeamPlay
        ? Players.GroupBy(p => p.Team).Select(g => (g.Key, g.Sum(p => p.Score)))
        : Players.Select(p => (p.Team, p.Score));

    /// <summary>
    /// A network melee's kill credit (per player): the hit's
    /// source, if a human's car, scores trunc(1000 × victim mass / own mass);
    /// nothing for one's own death or, in team play, a teammate's.
    /// </summary>
    public void Credit(Vehicle victim, object? attacker)
    {
        if (!Rules.Network) return;
        var vp = Players.FirstOrDefault(p => p.Car == victim);
        // what the death books: nothing for one's own weapon or a teammate's, a death for another side's
        if (vp is not null)
            vp.Death = attacker is not Vehicle av ? DeathCredit.Nobody
                : av == victim || (Rules.TeamPlay && Players.FirstOrDefault(p => p.Car == av) is { } mate && mate.Team == vp.Team) ? DeathCredit.Free
                : DeathCredit.Killer;
        // kills score nothing in a race or CTF (the score for a kill or a goal returns at once)
        if (Rules.Kind != MeleeKind.Combat) return;
        if (attacker is not Vehicle a || a == victim || a.P.Mass <= 0) return;
        if (Players.FirstOrDefault(p => p.Car == a) is not { } killer) return;
        if (Rules.TeamPlay && vp is { } v && v.Team == killer.Team) return;
        killer.Kills++;
        killer.Score += (int)(1000f * victim.P.Mass / a.P.Mass);
    }

    /// <summary>
    /// Within 10 m of a regen point, at most once every 30 s
    /// (counted from the last spawn or regen, and a car must leave the pad
    /// first): 400 points and a fresh car at a random spawn point. Nitro's
    /// copy pays no points in a network CTF game.
    /// </summary>
    private void Regen(MeleePlayer pl)
    {
        var p = pl.Car;
        var at = p.Pos.ToVector3();
        if (!Regens.Any(r => System.Numerics.Vector3.DistanceSquared(r.Pos.ToVector3(), at) < 100f)) { pl.OffPad = true; return; }
        if (Host.Now - pl.RegenAt < 30f)
        {
            if (!pl.OffPad) pl.RegenAt = Host.Now;
            return;
        }
        pl.RegenAt = Host.Now;
        pl.OffPad = false;
        if (!(Rules.Network && Rules.Kind == MeleeKind.Ctf)) pl.Score += 400;
        Ctf?.CarrierGone(pl);
        Renew(pl, full: Rules.Network);
        Place(p, PickSpawn(pl.Team, p), pl.Team);
        PlayerRenewed?.Invoke(p);
    }

    /// <summary>
    /// A fresh car, as the same vehicle (its node and hooks stay): the wheels'
    /// grip, rolling and rest height (unblown), parameters, engine and armoury from a newly built
    /// one, then new damage bound to this car's own parts. A network melee
    /// renews all of the car (every wheel field, the controls and the contact
    /// record), so a respawn carries nothing over from the old one.
    /// </summary>
    private void Renew(MeleePlayer pl, bool full)
    {
        var p = pl.Car;
        if (pl.NextCar is { } next && full)
        {
            pl.CarFile = next;
            pl.NextCar = null;
            Rebuild(p, Build(next, true), Rand, network: Rules.Network);
            p.Paint = pl.Paint;
            pl.CarEpoch++;
            p.NetGeneration++;
            return;
        }
        var fresh = Build(pl.CarFile, true);
        foreach (var (w, f) in p.Wheels.Zip(fresh.Wheels))
            if (full) Session.FieldCopy.Into(f, w);
            else { w.Grip = f.Grip; w.Rolling = f.Rolling; w.Blown = false; w.Rest = f.Rest; }   // the blow-out's lower rest height too
        p.P = fresh.P;
        p.Engine = fresh.Engine;
        // the driver's side-glance handguns are fitted to the car by the game after the build (the cockpit decides
        // which arms there are), so the fresh armoury has none: the driver keeps theirs, loaded again
        if (p.Armory is { } old && fresh.Armory is { } fa)
            for (int k = 0; k < old.Handguns.Length; k++)
                if (old.Handguns[k] is { } hg)
                {
                    hg.Hp = hg.MaxHp; hg.Ammo = (int)hg.Def.Ammo;
                    hg.Trigger = hg.WasFiring = false; hg.WaitA = hg.WaitB = 0; hg.BurstLeft = 0;
                    fa.Handguns[k] = hg;
                }
        fresh.Armory?.InheritLinks(p.Armory);
        p.Armory = fresh.Armory;
        p.Flags = fresh.Flags;
        p.Timer = 0;
        p.Wreck = false;
        if (full)
        {
            (p.Steer, p.Throttle, p.Gear, p.Handbrake, p.DriveState) = (fresh.Steer, fresh.Throttle, fresh.Gear, fresh.Handbrake, fresh.DriveState);
            (p.Speed, p.Accel, p.Rec) = (0, default, default);
            p.Arms.Reset();
        }
        if (p.IsHeli)
        {
            // a helicopter has no car damage state: its pool and flight state start again, its specials as built
            p.Heli = fresh.Heli;
            p.Health = fresh.Health;
        }
        else if (p.Build is { } b) p.Health = Damage.VehicleDamage.Create(p, b, true, Rand, Rules.Network);
        p.NetGeneration++;
    }

    /// <summary>
    /// The vehicle becomes another car in place (a reconfigured respawn): every field of a freshly built one, its id and
    /// generation kept, its damage bound to it again. The same object stays in the world, the session's driver and every
    /// table keyed by it; a client rebuilds its copy the same way (the server's car-changed event).
    /// </summary>
    public static void Rebuild(Vehicle v, Vehicle fresh, CrtRand rand, bool isPlayer = true, bool network = false)
    {
        int id = v.NetId, gen = v.NetGeneration;
        var oldArmory = v.Armory;
        var paint = v.Paint;       // a respray is the player's, not the car file's: it stays through a rebuild
        fresh.Armory?.InheritLinks(oldArmory);       // the player's links stay through a respawn
        Session.FieldCopy.Into(fresh, v);
        (v.NetId, v.NetGeneration, v.Paint) = (id, gen, paint);
        v.Arms = new CrewArms();
        if (!v.IsHeli && v.Build is { } b) v.Health = Damage.VehicleDamage.Create(v, b, isPlayer, rand, network);
    }
}
