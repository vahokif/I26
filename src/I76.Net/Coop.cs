using System.Numerics;
using I76.Sim.Math;
using I76.Sim.Missions;
using I76.Sim.Session;
using I76.Sim.Vehicles;

namespace I76.Net;

// A co-op story mission over the wire (protocol 15, docs/multiplayer.md "Co-op over the network"): the server runs the
// mission script; the clients do not, and see what a single player sees of it from these: the CB radio, the objectives,
// the movies and the mission's end as events, the script camera and the gates in every snapshot's tail, the state of
// all of it in the join accept for a late joiner.

/// <summary>
/// A line the host's CB radio put on the air (protocol 15): the sound, who says it (an entity of the mission by its index
/// in the script host's list, <see cref="Player"/> for a human: each client's own player, <see cref="Nobody"/> for the
/// radio itself) and its priority. A leave-map warning goes to its human's client alone (<see cref="To"/>, the server's).
/// </summary>
public sealed record RadioEvent(string Sound, int Speaker, byte Priority) : NetEvent
{
    public const int Nobody = -1, Player = -2;
    /// <summary>The slot whose client alone hears it (a leave-map warning); −1 for everyone. Not on the wire: the server's filter.</summary>
    public int To { get; init; } = -1;
    public override EventKind Kind => EventKind.Radio;
    protected override void Body(NetWriter w) { w.Name(Sound); w.VarI(Speaker); w.U8(Priority); }
}

/// <summary>The host's radio cut its line off with nothing after it (killCB, a flush): the clients' goes quiet too.</summary>
public sealed record RadioCutEvent() : NetEvent
{
    public override EventKind Kind => EventKind.RadioCut;
    protected override void Body(NetWriter w) { }
}

/// <summary>An objective's flags changed (<see cref="Objectives"/>: 1 hidden, 2 succeeded, 4 failed); its number from 1.</summary>
public sealed record ObjectiveEvent(byte Number, byte Flags) : NetEvent
{
    public override EventKind Kind => EventKind.Objective;
    protected override void Body(NetWriter w) { w.U8(Number); w.U8(Flags); }
}

/// <summary>
/// The script asked for a movie (playMovie: T05, T06): every client plays it, and the game stands still for everyone until it
/// is over (the server does not tick, the clients neither predict nor draw on): <see cref="MovieEndEvent"/> ends it. A client
/// takes it when it arrives, not with its tick (<see cref="MeleeClient.MovieName"/>).
/// </summary>
public sealed record MovieEvent(string Name) : NetEvent
{
    public override EventKind Kind => EventKind.Movie;
    protected override void Body(NetWriter w) => w.Str(Name);
}

/// <summary>The movie is over for everyone (the host's copy ended or was skipped, or the server gave up waiting): the game goes on.</summary>
public sealed record MovieEndEvent() : NetEvent
{
    public override EventKind Kind => EventKind.MovieEnd;
    protected override void Body(NetWriter w) { }
}

/// <summary>
/// The mission is over (successAll / failAll as the script landed them): won or failed, and the failure's line (its
/// number in the mission's text, −1 for none, and the text as the host has it). The game over follows.
/// </summary>
public sealed record MissionEndEvent(bool Won, int FailMessage, string Text) : NetEvent
{
    public override EventKind Kind => EventKind.MissionEnd;
    protected override void Body(NetWriter w) { w.Bool(Won); w.VarI(FailMessage); w.Str(Text); }
}

/// <summary>A car's siren went on or off (setSiren: the entity's, not the car's state, so not in the snapshots).</summary>
public sealed record SirenEvent(int Car, bool On) : NetEvent
{
    public override EventKind Kind => EventKind.Siren;
    protected override void Body(NetWriter w) { w.VarU((uint)Car); w.Bool(On); }
}

/// <summary>A gate the script slid (triggerGate), where it is: the structure's serial, the node and how far along (0 placed, 1 at its far end).</summary>
public readonly record struct GateState(int Structure, byte Node, float X)
{
    public void Write(NetWriter w) { w.VarU((uint)Structure); w.U8(Node); w.F32(X); }
    public static GateState Read(ref NetReader r) => new((int)r.VarU(), r.U8(), r.F32());
}

/// <summary>
/// The script camera as every client draws it (in each snapshot's tail while it holds the view): where it is, and what it
/// looks at or its basis. The client draws between two snapshots' cameras as it draws the cars.
/// </summary>
public sealed record CameraState(Vector3 Eye, Vector3? LookAt, Rows? Basis)
{
    public void Write(NetWriter w)
    {
        w.U8((byte)((LookAt is null ? 0 : 1) | (Basis is null ? 0 : 2)));
        w.Vec(Eye);
        if (LookAt is { } l) w.Vec(l);
        if (Basis is { } b) w.Rot(b);
    }

    public static CameraState Read(ref NetReader r)
    {
        byte f = r.U8();
        var eye = r.Vec();
        Vector3? look = (f & 1) != 0 ? r.Vec() : null;
        Rows? basis = (f & 2) != 0 ? r.Rot() : null;
        return new CameraState(eye, look, basis);
    }

    public static CameraState? Of(ScriptCamera v) => v.Active ? new CameraState(v.Eye, v.LookAt, v.Basis) : null;

    /// <summary>The seats' own camera scenes (protocol 19), by seat.</summary>
    public static List<(byte Seat, CameraState Camera)> PersonalOf(I76.Sim.Missions.ScriptHost h) =>
        [.. h.PersonalViews.OrderBy(x => x.Seat).Select(x => ((byte)x.Seat, Of(x.View)!))];
}

/// <summary>
/// The co-op part of a join accept (protocol 15): the mission is built as the server built it (its seats' cars, so the
/// cars are made in the same order and get the same serials), this player's seat, and the script's presentation as it
/// stands, for a client joining late: the objectives, the sirens on, the radio's line on the air and how far into it,
/// and the end if the mission is over.
/// </summary>
public sealed record CoopInfo(IReadOnlyList<string?> SeatCars, float RespawnDelay, byte Seat, IReadOnlyList<byte> Objectives, IReadOnlyList<int> Sirens,
    RadioEvent? Radio = null, float RadioSeconds = 0, MissionEndEvent? End = null, string? Movie = null)
{
    public CoopSetup Setup => new(SeatCars) { RespawnDelay = RespawnDelay };

    public void Write(NetWriter w)
    {
        w.U8((byte)SeatCars.Count);
        foreach (var c in SeatCars) w.Str(c ?? "");
        w.F32(RespawnDelay);
        w.U8(Seat);
        w.U8((byte)Objectives.Count);
        foreach (var o in Objectives) w.U8(o);
        w.VarU((uint)Sirens.Count);
        foreach (var s in Sirens) w.VarU((uint)s);
        w.Bool(Radio is not null);
        if (Radio is { } rl) { w.Str(rl.Sound); w.VarI(rl.Speaker); w.U8(rl.Priority); w.F32(RadioSeconds); }
        w.Bool(End is not null);
        if (End is { } e) { w.Bool(e.Won); w.VarI(e.FailMessage); w.Str(e.Text); }
        // protocol 19: the movie being played when this player comes in (empty for none)
        w.Str(Movie ?? "");
    }

    public static CoopInfo Read(ref NetReader r)
    {
        int n = r.U8();
        if (n is < 1 or > Protocol.MaxCoopSeats) throw new WireException($"{n} co-op seats");
        var cars = new string?[n];
        for (int i = 0; i < n; i++) cars[i] = r.Str() is { Length: > 0 } c ? c : null;
        float delay = r.F32();
        byte seat = r.U8();
        var obj = new byte[r.U8()];
        for (int i = 0; i < obj.Length; i++) obj[i] = r.U8();
        var sirens = new int[JoinAccept.Count(ref r, 4096)];
        for (int i = 0; i < sirens.Length; i++) sirens[i] = (int)r.VarU();
        RadioEvent? radio = null;
        float at = 0;
        if (r.Bool()) { radio = new RadioEvent(r.Str(), r.VarI(), r.U8()); at = r.F32(); }
        MissionEndEvent? end = r.Bool() ? new MissionEndEvent(r.Bool(), r.VarI(), r.Str()) : null;
        return new CoopInfo(cars, delay, seat, obj, sirens, radio, at, end, r.Str() is { Length: > 0 } movie ? movie : null);
    }
}

/// <summary>
/// A co-op mission's cars on a client: the mirror builds the mission as the server did (<see cref="CoopInfo.SeatCars"/>),
/// so every car the server names is already made, with the same serial; this hands each out by its serial when the server
/// says it is in the game, and builds a fresh one only for a car that is not there (a stand-in).
/// </summary>
public sealed class CoopMirrorCars(I76.Formats.Vfs.AssetVfs vfs) : IMirrorCars
{
    private readonly Dictionary<int, Vehicle> _built = [];

    /// <summary>The mission's cars as built, by serial; the world is emptied of them, to be filled by the server's word.</summary>
    public void Prepare(SimSession session)
    {
        var w = session.World;
        foreach (var v in w.Vehicles) _built[v.NetId & 0xffffff] = v;
        w.Vehicles.Clear();
        w.Humans.Clear();
        w.Player = null;
    }

    public Vehicle Build(int serial, string def, bool human)
    {
        if (_built.TryGetValue(serial, out var v) && string.Equals(v.Definition, def, StringComparison.OrdinalIgnoreCase)) return v;
        return VehicleFactory.Create(vfs, def, bodySound: human);
    }

    public void Rebuild(Vehicle v, Vehicle fresh, bool human) => MeleeGame.Rebuild(v, fresh, new I76.Sim.Rng.CrtRand(1), human);
}

/// <summary>A co-op story mission built the same on the server, its successor and every client.</summary>
public static class CoopMission
{
    /// <summary>
    /// The mission with its human group (<see cref="MissionSetup.Build"/>'s co-op), and seat 0's driver handguns, which a
    /// single player's game fits at the cockpit and a network game's car must have built in (the other seats' have them).
    /// </summary>
    public static MissionSetup Build(I76.Formats.Vfs.AssetVfs vfs, I76.Formats.Assembly.MissionWorld world, I76.Formats.Assembly.ModelAssembler asm,
        CoopSetup coop, I76.Sim.Rng.CrtRand rand, int seed, bool script = true)
    {
        var setup = MissionSetup.Build(vfs, world, asm, rand: rand, seed: seed, coop: coop, script: script);
        if (setup.Script?.Host.Coop is { } g) VehicleFactory.AddDriverHandguns(vfs, g.Seats[0].CarFile, g.Seats[0].Car);
        return setup;
    }
}
