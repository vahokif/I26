using I76.Sim.Math;
using I76.Sim.Vehicles;

namespace I76.Sim.Missions;

/// <summary>A co-op story mission's human players: who drives which car (docs/multiplayer.md "Co-op").</summary>
/// <param name="Cars">Each seat's car (.vcf); seat 0 is the player placement's, null for the mission's own (or the requested car), and a later seat's null takes seat 0's.</param>
public sealed record CoopSetup(IReadOnlyList<string?> Cars)
{
    /// <summary>How long a destroyed human waits before coming back beside a living teammate (seconds of mission time).</summary>
    public float RespawnDelay { get; init; } = 5f;

    /// <summary>
    /// The host's Co-op Damage (the port's; docs/deviations.md): every damage the humans' cars take is scaled by this, the
    /// channels truncated as the original truncates its own difficulty factor. 1 (the default) is the original's damage.
    /// </summary>
    public float DamageFactor { get; init; } = 1f;

    /// <summary>The factors the host form and the servers offer.</summary>
    public static readonly float[] DamageFactors = [0.5f, 0.75f, 1f, 1.5f];

    /// <summary>A command line's factor ("0.5", "x0.75"): one of <see cref="DamageFactors"/>, else null.</summary>
    public static float? ParseDamageFactor(string s) =>
        float.TryParse(s.TrimStart('x', 'X'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float f)
        && System.Array.IndexOf(DamageFactors, f) >= 0 ? f : null;

    /// <summary>
    /// The host's Co-op Scaling (the port's; docs/deviations.md): the most extra copies each enemy car gets, one per human past the
    /// first (<see cref="ScriptHost.ExtrasOf"/>), up to <see cref="MaxExtraEnemies"/>. 0 (the default) is the original's mission.
    /// </summary>
    public int ExtraEnemies { get; init; }

    /// <summary>The cap on <see cref="ExtraEnemies"/>: three, an extra per joiner of a four-player game.</summary>
    public const int MaxExtraEnemies = 3;

    /// <summary>A co-op game of <paramref name="humans"/> players, each in the mission's own car.</summary>
    public static CoopSetup Of(int humans) => new(Enumerable.Repeat<string?>(null, humans).ToList());
}

/// <summary>One human player's place in a co-op mission: their car (rebuilt in place on a respawn) and its entity.</summary>
public sealed class CoopSeat
{
    public required int Index { get; init; }
    public required Entity Entity { get; init; }
    public Vehicle Car => Entity.Vehicle!;
    /// <summary>The car's .vcf, for a respawn's fresh car.</summary>
    public required string CarFile { get; set; }
    /// <summary>Mission time the destroyed car comes back at (null while alive).</summary>
    public float? RespawnAt { get; internal set; }
    public int Deaths { get; internal set; }
    /// <summary>The player is in the game (a seat whose player left is out of the group: never the lead, never respawned).</summary>
    public bool Present { get; internal set; } = true;
    public bool Alive => Present && !Entity.Dead && !Entity.Removed;
}

/// <summary>Why a human came back.</summary>
public enum CoopReturn { Died, LeftMap, Joined, Gate }

/// <summary>What the host shows a co-op game's players (the sim's part; their screens are the game's).</summary>
public abstract record CoopEvent(int Seat);
/// <summary>A human is back in the game beside the seat <paramref name="Beside"/>: a fresh car after a death or a join, the same car after leaving the map or being brought through a gate the script shut.</summary>
public sealed record CoopRespawned(int Seat, CoopReturn Why, int Beside) : CoopEvent(Seat);
/// <summary>The leave-map routine's warning for one human: its CB line (sound, priority), for that player's radio alone.</summary>
public sealed record CoopLeaveWarning(int Seat, string Sound, int Priority) : CoopEvent(Seat);

/// <summary>
/// The human group of a co-op story mission (docs/multiplayer.md "Co-op"): the seats, the respawns, and the
/// formation the others take behind the lead after a cut-scene moved it. Built by <see cref="MissionSetup.Build"/>.
/// </summary>
public sealed class CoopGroup
{
    public IReadOnlyList<CoopSeat> Seats { get; }
    public float RespawnDelay { get; }
    /// <summary>Co-op Scaling's cap (<see cref="CoopSetup.ExtraEnemies"/>); 0 off.</summary>
    public int ExtraEnemies { get; init; }
    /// <summary>Builds an enemy car's copy from its .vcf (an AI car's damage state, the night's lights), not placed.</summary>
    internal Func<string, Vehicle>? BuildEnemy { get; init; }
    /// <summary>Co-op Scaling made a copy of an enemy car (its car and .vcf): the server names it to the clients.</summary>
    public event Action<Vehicle, string>? Spawned;
    internal void RaiseSpawned(Vehicle v, string def) => Spawned?.Invoke(v, def);
    /// <summary>Builds a fresh car from its .vcf for a seat (health, handguns, lights), not placed.</summary>
    internal Func<string, int, Vehicle> Build { get; }
    /// <summary>A human came back, or was warned off the map edge.</summary>
    public event Action<CoopEvent>? Happened;
    /// <summary>The script drove, ordered or teleported a human in the cut-scene under way: the others form up behind the lead when it ends.</summary>
    internal bool LeadMoved;
    /// <summary>The script held the camera at the last frame.</summary>
    internal bool WasCutscene;
    /// <summary>The script's paths (mission data), which a formation keeps off: <see cref="SpawnSpace.Beside"/>.</summary>
    internal IReadOnlyList<IReadOnlyList<System.Numerics.Vector3>>? Paths { get; init; }

    internal CoopGroup(IReadOnlyList<CoopSeat> seats, float respawnDelay, Func<string, int, Vehicle> build)
    {
        Seats = seats; RespawnDelay = respawnDelay; Build = build;
    }

    internal void Raise(CoopEvent e) => Happened?.Invoke(e);

    /// <summary>The first living human's seat; seat 0 when nobody lives.</summary>
    public int LeadSeat
    {
        get
        {
            foreach (var s in Seats) if (s.Alive) return s.Index;
            return 0;
        }
    }

    public CoopSeat? SeatOf(object? o)
    {
        foreach (var s in Seats) if (ReferenceEquals(s.Entity, o) || o is Vehicle v && ReferenceEquals(s.Entity.Vehicle, v)) return s;
        return null;
    }
}

public sealed partial class ScriptHost
{
    /// <summary>The human group in a co-op mission; null in single player (and a melee), where <see cref="Player"/> is the one human.</summary>
    public CoopGroup? Coop { get; internal set; }

    /// <summary>
    /// The hysteresis of an order's aim at the group: the car keeps the human it aimed at until another is nearer by
    /// this factor of distance (the AI does not swap targets as two players pass each other).
    /// </summary>
    public const float AimHysteresis = 1.25f;

    /// <summary>The lead: the first living human (cut-scenes drive it, one-car uses of "the player" mean it); the player in single player.</summary>
    public Entity? Lead => Coop is { } c ? c.Seats[c.LeadSeat].Entity : Player;
    /// <summary>The living humans, by seat; in single player the player while alive.</summary>
    public IEnumerable<Entity> Players => Coop is { } c ? c.Seats.Where(s => s.Alive).Select(s => s.Entity) : Player is { Dead: false } p ? [p] : [];
    /// <summary>A human's entity: the player's, or in co-op any seat's.</summary>
    public bool IsHumanEntity(Entity? e) => e is not null && (Coop is { } c ? c.SeatOf(e) is not null : e == Player);

    // ---- IFsmHost's group ----
    public int CoopSeats => Coop?.Seats.Count ?? 0;
    public bool IsPlayerHandle(object o) => Coop is not null && ReferenceEquals(o, Player);
    public object? Human(int seat) => Coop is { } c && seat >= 0 && seat < c.Seats.Count ? c.Seats[seat].Entity : null;
    public bool HumanAlive(int seat) => Coop is { } c && seat >= 0 && seat < c.Seats.Count && c.Seats[seat].Alive;
    public int HumanSeat(object o) => Coop?.SeatOf(o)?.Index ?? -1;
    public int LeadSeat => Coop?.LeadSeat ?? 0;
    public int KillerSeat(object victim) => E(victim).KillerSeat;

    /// <summary>
    /// An order at "the player": the living human nearest the ordered car (on the ground plane), keeping the one it
    /// aimed at before unless another is <see cref="AimHysteresis"/> times nearer. Null with nobody alive. In a
    /// cut-scene it is the lead, which the scene drives while the others wait: T01's intro has Taurus follow the
    /// player down the road, not park beside a teammate still at the start.
    /// </summary>
    public object? NearestHuman(object commanded)
    {
        if (Coop is not { } c) return Player;
        var e = E(commanded);
        if (View.Active && c.Seats[c.LeadSeat] is { Alive: true } lead && lead.Entity != e) return e.CoopAim = lead.Entity;
        var at = e.Position;
        static float D2(Entity x, System.Numerics.Vector3 p) { var d = x.Position - p; return d.X * d.X + d.Z * d.Z; }
        Entity? best = null; float bd = float.MaxValue;
        foreach (var s in c.Seats)
            if (s.Alive && s.Entity != e && D2(s.Entity, at) is var d && d < bd) { bd = d; best = s.Entity; }
        if (best is null) return null;
        if (e.CoopAim is { } cur && cur != best && c.SeatOf(cur) is { Alive: true } && D2(cur, at) <= bd * AimHysteresis * AimHysteresis) best = cur;
        e.CoopAim = best;
        return best;
    }

    /// <summary>whoX on the group: the attacker of the human whose last hit of that kind is the latest (seat order on a tie).</summary>
    public object? HumansLastAttacker(int kind)
    {
        if (Coop is not { } c) return Player is { } p ? LastAttacker(p, kind) : null;
        Entity? who = null; int latest = int.MinValue;
        foreach (var s in c.Seats)
        {
            var e = s.Entity;
            int tick = kind == 0 ? e.RammedTick : kind == 1 ? e.ShotTick : e.AttackedTick;
            if (LastAttacker(e, kind) is Entity a && tick > latest) { latest = tick; who = a; }
        }
        return who;
    }

    /// <summary>The other living humans into formation behind the lead, moving as it moves (a teleport, the end of a cut-scene).</summary>
    public void FormUp(int leadSeat)
    {
        if (Coop is not { } c || c.Seats[leadSeat].Entity.Vehicle is not { } lead) return;
        foreach (var s in c.Seats)
            if (s.Index != leadSeat && s.Alive) PutBeside(s, lead, lead.Vel, lead.Speed);
        Log($"co-op: form up behind seat {leadSeat}");
    }

    /// <summary>A seat's car beside <paramref name="anchor"/>, facing as it does, at that velocity, its controls at rest.</summary>
    private void PutBeside(CoopSeat s, Vehicle anchor, System.Numerics.Vector3 vel, float speed)
    {
        var v = s.Car;
        v.Pos = SpawnSpace.Beside(World, anchor, v, Coop?.Paths);
        v.Rot = anchor.Rot;
        v.Vel = vel;
        v.Speed = speed;
        v.AngVel = System.Numerics.Vector3.Zero;
        v.Steer = v.Throttle = 0;
    }

    /// <summary>The living teammate a seat comes back beside: the lead, else the first other living human (that <paramref name="ok"/> takes); null with none.</summary>
    private static CoopSeat? Teammate(CoopGroup c, int seat, Func<int, bool>? ok = null)
    {
        var lead = c.Seats[c.LeadSeat];
        if (lead.Index != seat && lead.Alive && ok?.Invoke(lead.Index) != false) return lead;
        return c.Seats.FirstOrDefault(x => x.Index != seat && x.Alive && ok?.Invoke(x.Index) != false);
    }

    /// <summary>
    /// The leave-map routine's fail radius for one human: brought back beside a living teammate away from the exits in
    /// the same car (no repair from driving off), or, with nobody to come back to, false: the routine fails the mission
    /// as the original.
    /// </summary>
    public bool Abandon(int seat, Func<int, bool> inBounds)
    {
        if (Coop is not { } c || seat < 0 || seat >= c.Seats.Count || Teammate(c, seat, inBounds) is not { } mate) return false;
        var s = c.Seats[seat];
        PutBeside(s, mate.Car, mate.Car.Vel, mate.Car.Speed);
        EndPersonalScene(seat);   // the leave-the-map camera has nothing left to follow
        Log($"co-op: seat {seat} left the map, back beside seat {mate.Index}");
        c.Raise(new CoopRespawned(seat, CoopReturn.LeftMap, mate.Index));
        return true;
    }

    public void LeaveWarning(int seat, string sound, int priority) => Coop?.Raise(new CoopLeaveWarning(seat, sound, priority));

    /// <summary>A gate the script shuts, standing open and idle: a point on its line (its leaf's middle) and its slide.</summary>
    public (System.Numerics.Vector3 At, System.Numerics.Vector3 Slide)? OpenGate(object o)
    {
        var e = E(o);
        if (e.Static is not { } st) return null;
        int gi = st.Nodes.FindIndex(n => n.Type == 7 && n.Gate is not null);
        if (gi < 0 || st.Nodes[gi].Gate is not { Moving: false, Target: 0 } g || g.World.LengthSquared() < 1e-4f) return null;
        var leaf = st.Boxes.Where(b => b.Node == gi).ToList();
        var at = leaf.Count > 0 ? leaf.Aggregate(System.Numerics.Vector3.Zero, (a, b) => a + b.Centre) / leaf.Count : e.Position;
        return (at, g.World);
    }

    /// <summary>A human left behind a gate the script shuts: beside a teammate through it, in the same car, moving as they move.</summary>
    public void BringThrough(int seat, int beside)
    {
        if (Coop is not { } c || seat < 0 || seat >= c.Seats.Count || beside < 0 || beside >= c.Seats.Count || !c.Seats[seat].Alive || !c.Seats[beside].Alive) return;
        var mate = c.Seats[beside];
        PutBeside(c.Seats[seat], mate.Car, mate.Car.Vel, mate.Car.Speed);
        EndPersonalScene(seat);
        Log($"co-op: seat {seat} brought through a shutting gate beside seat {beside}");
        c.Raise(new CoopRespawned(seat, CoopReturn.Gate, beside));
    }

    /// <summary>
    /// Co-op's frame, before the script ticks: a destroyed human comes back <see cref="CoopGroup.RespawnDelay"/> after
    /// the death in a fresh car beside a living teammate (waiting while nobody lives: the script sees them all dead);
    /// when the script lets go of the camera after moving the lead, the others form up behind it.
    /// </summary>
    internal void CoopFrame()
    {
        if (Coop is not { } c) return;
        foreach (var s in c.Seats)
        {
            if (!s.Present) continue;
            if (!s.Entity.Dead) { s.RespawnAt = null; continue; }
            if (s.RespawnAt is null) { s.RespawnAt = Now + c.RespawnDelay; s.Deaths++; }
            if (Now >= s.RespawnAt && Teammate(c, s.Index) is { } mate) Respawn(c, s, mate);
        }
        bool active = View.Active;
        if (c.WasCutscene && !active && c.LeadMoved) FormUp(c.LeadSeat);
        if (!active) c.LeadMoved = false;
        c.WasCutscene = active;
    }

    /// <summary>
    /// A fresh car in place of the wreck (the same vehicle, every field rebuilt, as a network melee's reconfigured
    /// respawn: <see cref="MeleeGame.Rebuild"/>), the driver's handguns kept and loaded, beside the teammate.
    /// </summary>
    private void Respawn(CoopGroup c, CoopSeat s, CoopSeat mate, CoopReturn why = CoopReturn.Died)
    {
        var v = s.Car;
        var handguns = v.Armory?.Handguns.ToArray();
        MeleeGame.Rebuild(v, c.Build(s.CarFile, s.Index), World.Rand);
        if (handguns is not null && v.Armory is { } fa)
            for (int k = 0; k < handguns.Length && k < fa.Handguns.Length; k++)
                if (fa.Handguns[k] is null && handguns[k] is { } hg)
                {
                    hg.Hp = hg.MaxHp; hg.Ammo = (int)hg.Def.Ammo;
                    hg.Trigger = hg.WasFiring = false; hg.WaitA = hg.WaitB = 0; hg.BurstLeft = 0;
                    fa.Handguns[k] = hg;
                }
        v.NetGeneration++;
        var e = s.Entity;
        e.Order = null; e.OrderTarget = null; e.FollowRef = null; e.Path = null;
        e.Controls.Clear(); e.ControlDone = true; e.ScriptDriven = false;
        e.ShotTick = e.RammedTick = e.AttackedTick = -10;
        if (Combat is { } cb) { cb.RadarTarget.Remove(v); cb.RadarCamera.Remove(v); cb.StructureTarget.Remove(v); }
        World.Ai.Remove(v);
        PutBeside(s, mate.Car, System.Numerics.Vector3.Zero, 0);
        VehicleStep.PrimeWheels(v, World.Ground);   // a fresh car: its wheels' history from the ground it now stands on
        EndPersonalScene(s.Index);
        StartCar(e);   // its engine turns over, as the script's startCar would
        s.RespawnAt = null;
        Log($"co-op: seat {s.Index} respawned beside seat {mate.Index}");
        c.Raise(new CoopRespawned(s.Index, why, mate.Index));
    }

    /// <summary>
    /// A player left the game (the server's): their seat is out of the group (never the lead, no respawn) and a live car
    /// is blown up and stays a wreck, as a network melee's leaver's. Seat 0's entity is the script's
    /// player actor and stays in the world whatever happens to it.
    /// </summary>
    public void CoopLeave(int seat)
    {
        if (Coop is not { } c || seat < 0 || seat >= c.Seats.Count || !c.Seats[seat].Present) return;
        var s = c.Seats[seat];
        s.Present = false;
        s.RespawnAt = null;
        EndPersonalScene(seat);
        if (!s.Entity.Dead) Destroy(s.Entity);
        Log($"co-op: seat {seat} left");
    }

    /// <summary>A player takes a free seat (one who left, or a seat nobody had joined yet): a fresh car (of their choice, else the seat's) beside a living teammate at once. False with nobody to join.</summary>
    public bool CoopJoin(int seat, string? car = null)
    {
        if (Coop is not { } c || seat < 0 || seat >= c.Seats.Count || c.Seats[seat].Present || Teammate(c, seat) is not { } mate) return false;
        var s = c.Seats[seat];
        s.Present = true;
        if (car is not null) s.CarFile = car;
        // a seat kept out of the world (CoopVacate) comes back into it
        if (s.Entity.Removed)
        {
            s.Entity.Removed = false;
            _byVehicle[s.Car] = s.Entity;
            if (!World.Vehicles.Contains(s.Car)) World.Vehicles.Add(s.Car);
            if (!World.Humans.Contains(s.Car)) World.Humans.Add(s.Car);
        }
        Respawn(c, s, mate, CoopReturn.Joined);
        return true;
    }

    /// <summary>
    /// A seat nobody holds (a network game's seats are built for its most players, and the mission starts with fewer):
    /// out of the group and out of the world altogether, no wreck left, until <see cref="CoopJoin"/> brings it in. Seat 0
    /// (the script's player actor) is never vacated.
    /// </summary>
    public void CoopVacate(int seat)
    {
        if (Coop is not { } c || seat <= 0 || seat >= c.Seats.Count || !c.Seats[seat].Present) return;
        var s = c.Seats[seat];
        s.Present = false;
        s.RespawnAt = null;
        EndPersonalScene(seat);
        s.Entity.Removed = true;
        World.Ai.Remove(s.Car);
        World.Vehicles.Remove(s.Car);
        World.Humans.Remove(s.Car);
        if (Combat is { } cb) { cb.RadarTarget.Remove(s.Car); cb.RadarCamera.Remove(s.Car); cb.StructureTarget.Remove(s.Car); }
        Log($"co-op: seat {seat} is empty");
    }

    // ---- personal camera scenes (CoopCameras): a human's own moment, shown to that player alone ----
    private readonly Dictionary<int, (ScriptCamera View, float Since)> _personal = [];
    /// <summary>A personal scene that has run this long is over whatever its script says (a moment of its own is seconds).</summary>
    public const float PersonalSceneSeconds = 90;

    /// <summary>The seats whose own camera holds their view now, with it.</summary>
    public IEnumerable<(int Seat, ScriptCamera View)> PersonalViews => _personal.Where(kv => kv.Value.View.Active).Select(kv => (kv.Key, kv.Value.View));

    /// <summary>The seat's own camera while it holds the view, else null.</summary>
    public ScriptCamera? PersonalView(int seat) => _personal.TryGetValue(seat, out var p) && p.View.Active ? p.View : null;

    /// <summary>Whether the car's human is in a personal scene of their own: their keys do nothing, as in a cut-scene.</summary>
    public bool PersonalSceneOf(Vehicle car) => _personal.Count > 0 && Coop?.SeatOf(car) is { } s && PersonalView(s.Index) is not null;

    public void PersonalCamera(int seat, string op, IReadOnlyList<int> args, IReadOnlyList<object?> objects, IReadOnlyList<System.Numerics.Vector3>? path)
    {
        if (!_personal.TryGetValue(seat, out var p)) _personal[seat] = p = (new ScriptCamera((x, z) => World.Ground.BareHeight(x, z)) { Log = Log }, Now);
        switch (op.ToLowerInvariant())
        {
            case "pushcam": if (!p.View.Active && p.View.Depth == 0) _personal[seat] = (p.View, Now); p.View.Push(); break;
            case "popcam": p.View.Pop(); break;
            default: p.View.Set(op, [.. args], objects.Select(x => x as Entity).ToArray(), path); break;
        }
    }

    public bool PersonalCameraArrived(int seat) => _personal.TryGetValue(seat, out var p) && p.View.ConsumeArrived();
    public bool PersonalSceneActive(int seat) => _personal.TryGetValue(seat, out var p) && p.View.Depth > 0;

    /// <summary>The seat's scene is over: its camera gives the view back.</summary>
    public void EndPersonalScene(int seat)
    {
        if (_personal.Remove(seat)) Log($"co-op: seat {seat}'s own camera scene ends");
    }

    /// <summary>Each personal camera one tick on (after the global one's); one that has run over <see cref="PersonalSceneSeconds"/> ends.</summary>
    internal void UpdatePersonalViews(float dt)
    {
        if (_personal.Count == 0) return;
        foreach (var (seat, p) in _personal.ToList())
        {
            if (p.View.Depth > 0 && Now - p.Since > PersonalSceneSeconds) { EndPersonalScene(seat); continue; }
            p.View.Update(dt);
        }
    }

    /// <summary>The script moved a human while it holds the camera: the others form up when it lets go.</summary>
    private void Moved(Entity e)
    {
        if (Coop is { } c && View.Active && c.SeatOf(e) is not null) c.LeadMoved = true;
    }
}
