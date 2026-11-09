using I76.Sim.Collision;
using I76.Sim.Missions;
using I76.Sim.Rng;
using I76.Sim.Save;
using I76.Sim.Vehicles;

namespace I76.Sim.Session;

/// <summary>What a restore changed about the session's cars, for the game layer to follow (docs/savestate.md).</summary>
/// <param name="Created">Cars the snapshot holds that the rebuilt mission had not made (a melee's later waves, its players): made from their files.</param>
/// <param name="Dropped">Cars of the rebuilt mission that the snapshot's world no longer has (removed wrecks, cars the snapshot never had).</param>
public sealed record RestoreResult(IReadOnlyList<Vehicle> Created, IReadOnlyList<Vehicle> Dropped);

public sealed partial class SimSession
{
    /// <summary>What the session was built from, carried in its snapshots (and checked by <see cref="Restore"/>). Set by whoever builds it.</summary>
    public SessionBuild? Build { get; set; }

    private static readonly SessionBuild Unbuilt = new() { Mission = "" };

    /// <summary>The skip key was pressed on the last tick: the script's isKeypress sees it on the next.</summary>
    internal bool SkipLatched => _skip;

    internal static void AuditFields(SaveAudit a)
    {
        a.For<SimSession>()
            .Saved(nameof(Ticks), nameof(World), nameof(Combat), nameof(Script), nameof(Ids), nameof(_drivers), nameof(_skip), nameof(_boomTick))
            .Structural("the session's settings, checked against the snapshot's header", nameof(Rate), nameof(Options), nameof(Build))
            .Structural("a debug hook (I76_BOOM) the host sets again", nameof(DebugBoom))
            .Structural("the host's rule for the reset-upright key (its Car Reset option), set again by the host that resumes", nameof(ResetUprightAllowed))
            .Transient("a per-tick output (the broken weapons' clicks), cleared at the next tick", nameof(Misfires))
            .Transient("the host's hooks", nameof(FrameStarting), nameof(FrameDone))
            .Transient("a network client's copy of the server's end, set by its prediction before each predicted tick (a server's is its melee's own)", nameof(NetEndTick));
        a.For<Driver>()
            .Saved(nameof(Driver.Car), nameof(Driver.Slot), nameof(Driver.Input), nameof(Driver.State), nameof(Driver.Generation));
        a.For<DriverState>()
            .Saved(nameof(DriverState.HoldUp), nameof(DriverState.HoldDown), nameof(DriverState.HoldRight), nameof(DriverState.HoldLeft),
                nameof(DriverState.Fire), nameof(DriverState.Keys), nameof(DriverState.Armed));
        a.For<CarInput>().Saved(nameof(CarInput.Buttons), nameof(CarInput.Commands), nameof(CarInput.Steer), nameof(CarInput.Throttle));
        a.For<NetIds>().Saved("_next");
        a.For<SimRate>().Structural("the session's rate, checked against the snapshot's header", nameof(SimRate.Label), nameof(SimRate.TickSeconds));
        a.For<SessionOptions>().Structural("the session's options, set by whoever builds it", nameof(SessionOptions.DebugHooks), nameof(SessionOptions.ModernPhysics), nameof(SessionOptions.Far));
        a.For<CrtRand>().Saved(nameof(CrtRand.Seed)).Structural("a recorded trace of the original's draws (oracle mode, never snapshotted)", "_oracle");
    }

    /// <summary>Every car the session knows: in the world, a human's, an entity's (removed ones too), by id.</summary>
    internal List<Vehicle> AllCars()
    {
        var set = new HashSet<Vehicle>(World.Vehicles);
        set.UnionWith(World.Humans);
        if (World.Player is { } p) set.Add(p);
        foreach (var d in _drivers) set.Add(d.Car);
        if (Script is { } s)
        {
            foreach (var e in s.Host.Entities) if (e.Vehicle is { } v) set.Add(v);
            if (s.Melee is { } m) foreach (var mp in m.Players) set.Add(mp.Car);
        }
        foreach (var v in set) if (v.NetId == 0) v.NetId = Ids.Next(NetKind.Vehicle);
        return [.. set.OrderBy(v => v.NetId)];
    }

    private SaveRefs NewRefs()
    {
        var refs = new SaveRefs();
        if (World.Ground is IHasStatics hs) foreach (var o in hs.World.Objects) refs.StaticById[o.NetId] = o;
        if (Script?.Vm is { } vm) refs.Paths = vm.Program.Paths;
        return refs;
    }

    /// <summary>
    /// The whole simulation as a compact, versioned snapshot (docs/savestate.md): everything that decides what
    /// happens next, with references as stable ids. <paramref name="host"/> rides along untouched (the game's
    /// own data: the launch, the trip's salvage).
    /// </summary>
    public byte[] Snapshot(byte[]? host = null)
    {
        if (World.Rand.IsOracle) throw new NotSupportedException("a session on a rand oracle cannot be snapshotted");
        return SnapshotHeader.Compose(Build ?? Unbuilt, Rate, World.Far, Ticks, host, SnapshotBody());
    }

    /// <summary>
    /// The snapshot's body alone (uncompressed): for <see cref="Session.StateHash"/>, without the events the views
    /// have not taken (<paramref name="events"/> false), which are not what decides the game.
    /// </summary>
    internal byte[] SnapshotBody(bool events = true)
    {
        AssignIds();
        var refs = NewRefs();
        var cars = AllCars();
        foreach (var v in cars) refs.CarById[v.NetId] = v;
        if (Script is { } sc) refs.AddEntities(sc.Host.Entities);
        var w = new SaveWriter(refs);
        WriteBody(w, cars, events);
        return w.ToArray();
    }

    private void WriteBody(SaveWriter w, List<Vehicle> cars, bool events)
    {
        w.Tag("SESS");
        w.Int(Ticks); w.Bool(_skip); w.Int(_boomTick);
        Ids.Save(w);
        w.U32(World.Rand.Seed);

        w.Tag("VTAB");
        w.VarU((ulong)cars.Count);
        foreach (var v in cars)
        {
            w.Int(v.NetId); w.Str(v.Definition); w.Bool(v.Health?.IsPlayer ?? World.IsHuman(v));
            w.Bytes(v.Paint?.ToBytes() ?? []);
            VehicleSave.WriteShape(w, v);
        }
        w.Tag("ETAB");
        w.Bool(Script is not null);
        Script?.Host.SaveTable(w);

        w.Tag("VEHS");
        foreach (var v in cars) VehicleSave.Write(w, v);
        w.VarU((ulong)World.Vehicles.Count); foreach (var v in World.Vehicles) w.Car(v);
        w.VarU((ulong)World.Humans.Count); foreach (var v in World.Humans) w.Car(v);
        w.Car(World.Player);

        w.Tag("DRVR");
        w.VarU((ulong)_drivers.Count);
        foreach (var d in _drivers)
        {
            w.Int(d.Slot); w.Car(d.Car);
            w.U32((uint)d.Input.Buttons); w.U32((uint)d.Input.Commands); w.Int(d.Input.Steer); w.Int(d.Input.Throttle);
            var s = d.State;
            w.F32(s.HoldUp); w.F32(s.HoldDown); w.F32(s.HoldRight); w.F32(s.HoldLeft); w.Bool(s.Fire); w.U8(s.Keys);
            // the armoury the latched request goes to: its car's (a stale one, from before a renewal, as the car's own)
            w.Car(s.Armed is null ? null : cars.FirstOrDefault(c => ReferenceEquals(c.Armory, s.Armed)) ?? d.Car);
            w.Int(d.Generation);
        }

        w.Tag("STAT");
        var statics = World.Ground is IHasStatics hs ? hs.World.Objects.Where(o => !o.Pristine).ToList() : [];
        w.VarU((ulong)statics.Count);
        foreach (var o in statics) { w.Int(o.NetId); o.Save(w); }

        w.Tag("WRLD");
        World.Save(w);
        w.Tag("CMBT");
        w.Bool(Combat is not null);
        Combat?.Save(w, events);
        w.Tag("DMGM");
        var dm = Combat?.Damage as Damage.DamageModel;
        w.Bool(dm is not null);
        dm?.Save(w, events);
        w.Tag("SCRP");
        Script?.Save(w);
        w.Tag("END.");
    }

    /// <summary>
    /// Puts a snapshot's state into this session, which must be freshly built the way the snapshot's was (the
    /// same mission, install, car, melee and tick: <see cref="SessionBuild"/>, checked here), as a mission start
    /// builds it (including a melee's <see cref="MeleeGame.Start"/>). Cars the snapshot holds that the build has not
    /// made are made from their files; the rand stream is restored last, so whatever the build drew does not
    /// matter. The game layer then follows (docs/savestate.md): its car nodes for <see cref="RestoreResult"/>, the
    /// camera, the HUD and the sounds.
    /// </summary>
    public RestoreResult Restore(byte[] snapshot)
    {
        var h = SnapshotHeader.Read(snapshot);
        if (h.Build.Differs(Build ?? Unbuilt) is { } why) throw new SnapshotMismatchException(why);
        if (h.Rate.Label != Rate.Label || h.Rate.TickSeconds != Rate.TickSeconds)
            throw new SnapshotMismatchException($"the snapshot runs at {h.Rate.Label} Hz, the session at {Rate.Label} Hz");
        if (h.Far != World.Far) throw new SnapshotMismatchException($"the snapshot measures near and far by {h.Far}, the session by {World.Far}");
        return RestoreBody(h.Body(snapshot));
    }

    internal RestoreResult RestoreBody(byte[] body)
    {
        AssignIds();
        var fresh = AllCars();
        var refs = NewRefs();
        var r = new SaveReader(body, refs);
        r.Tag("SESS");
        int ticks = r.Int(); bool skip = r.Bool(); int boom = r.Int();
        Ids.Load(r);
        uint seed = r.U32();

        r.Tag("VTAB");
        var byId = fresh.ToDictionary(v => v.NetId);
        var created = new List<Vehicle>();
        int n = r.Count();
        var cars = new List<Vehicle>(n);
        for (int i = 0; i < n; i++)
        {
            int id = r.Int();
            string? def = r.Str();
            bool player = r.Bool();
            var paint = I76.Formats.Imaging.CarPaint.FromBytes(r.Bytes());
            var shape = VehicleSave.ReadShape(r);
            if (!byId.TryGetValue(id, out var v))
            {
                if (Melee is null || def is null) throw r.Mismatch($"car {id:x} ({def}) is not in the rebuilt mission, which makes no cars as it runs");
                v = Melee.Build(def, player);
                v.NetId = id;
                created.Add(v);
            }
            else if (!string.Equals(v.Definition, def, StringComparison.OrdinalIgnoreCase)) throw r.Mismatch($"car {id:x} is {def} in the snapshot, {v.Definition} in the rebuilt mission");
            if (!VehicleSave.Shape(v).AsSpan().SequenceEqual(shape)) throw r.Mismatch($"car {id:x} ({def}) is made up differently (weapons, damage parts)");
            v.Paint = paint;
            refs.CarById[id] = v;
            cars.Add(v);
        }
        r.Tag("ETAB");
        if (r.Bool() != (Script is not null)) throw r.Mismatch(Script is null ? "the snapshot runs a mission script" : "the snapshot runs no mission script");
        Script?.Host.LoadTable(r);
        if (Script is { } sc) refs.AddEntities(sc.Host.Entities);

        r.Tag("VEHS");
        foreach (var v in cars) VehicleSave.Read(r, v);
        var was = World.Vehicles.ToList();
        World.Vehicles.Clear();
        for (int k = r.Count(); k > 0; k--) World.Vehicles.Add(r.Car()!);
        World.Humans.Clear();
        for (int k = r.Count(); k > 0; k--) World.Humans.Add(r.Car()!);
        World.Player = r.Car();
        var dropped = was.Where(v => !World.Vehicles.Contains(v)).ToList();

        r.Tag("DRVR");
        var drivers = new List<Driver>();
        for (int k = r.Count(); k > 0; k--)
        {
            int slot = r.Int();
            var car = r.Car()!;
            var d = _drivers.FirstOrDefault(x => x.Slot == slot && x.Car == car) ?? new Driver(car, slot);
            d.Input = new CarInput { Buttons = (CarButtons)r.U32(), Commands = (CarCommands)r.U32(), Steer = (short)r.Int(), Throttle = (short)r.Int() };
            var s = new DriverState { HoldUp = r.F32(), HoldDown = r.F32(), HoldRight = r.F32(), HoldLeft = r.F32(), Fire = r.Bool(), Keys = r.U8() };
            s.Armed = r.Car()?.Armory;
            d.State = s;
            d.Generation = r.Int();
            drivers.Add(d);
        }
        _drivers.Clear();
        _drivers.AddRange(drivers);

        r.Tag("STAT");
        // what the snapshot left out was pristine then: one hit since (a restore into the same session) goes back
        var loaded = new HashSet<Collision.StaticObject>();
        for (int k = r.Count(); k > 0; k--) { var so = refs.ReadStatic(r.Int()); so.Load(r); loaded.Add(so); }
        if (World.Ground is IHasStatics shs)
            foreach (var o in shs.World.Objects) if (!loaded.Contains(o) && !o.Pristine) o.ResetToPristine();

        r.Tag("WRLD");
        World.Load(r);
        r.Tag("CMBT");
        if (r.Bool() != (Combat is not null)) throw r.Mismatch("combat");
        Combat?.Load(r);
        r.Tag("DMGM");
        var dm = Combat?.Damage as Damage.DamageModel;
        if (r.Bool() != (dm is not null)) throw r.Mismatch("the damage model");
        dm?.Load(r);
        r.Tag("SCRP");
        Script?.Load(r);
        r.Tag("END.");
        if (!r.AtEnd) throw new SnapshotFormatException("bytes after the snapshot's end");

        Ticks = ticks; _skip = skip; _boomTick = boom;
        World.Rand.Seed = seed;
        return new RestoreResult(created, dropped);
    }
}

public sealed partial class NetIds
{
    internal void Save(SaveWriter w) { foreach (int x in _next) w.Int(x); }
    internal void Load(SaveReader r) { for (int i = 0; i < _next.Length; i++) _next[i] = r.Int(); }
}
