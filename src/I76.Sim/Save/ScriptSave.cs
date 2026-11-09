using System.Numerics;
using I76.Sim.Ai;
using I76.Sim.Collision;
using I76.Sim.Fsm;
using I76.Sim.Save;

namespace I76.Sim.Missions;

public sealed partial class MissionScript
{
    internal static void AuditFields(SaveAudit a)
    {
        a.For<MissionScript>()
            .Saved(nameof(Host), nameof(Vm), nameof(Melee), nameof(_frames), nameof(_ticks), nameof(Poetry), nameof(GotDown), nameof(GetDownEndAt))
            .Structural("the session's combat, linked by the mission build", nameof(Combat));
        Poetry.AuditFields(a);
        a.For<Objectives>()
            .Saved(nameof(Objectives.Flags))
            .Structural("the mission's objective text", nameof(Objectives.Text))
            .Transient("the host's hooks", nameof(Objectives.Sound), nameof(Objectives.Log));
        CbRadio.AuditFields(a);
        ScriptCamera.AuditFields(a);
        Ptable.AuditFields(a);
    }

    internal void Save(SaveWriter w)
    {
        w.Int(_frames); w.Int(_ticks); w.Bool(GotDown); w.NF32(GetDownEndAt);
        Poetry.Save(w);
        Host.Save(w);
        w.Bool(Vm is not null);
        Vm?.Save(w);
        w.Bool(Melee is not null);
        Melee?.Save(w);
    }

    internal void Load(SaveReader r)
    {
        _frames = r.Int(); _ticks = r.Int(); GotDown = r.Bool(); GetDownEndAt = r.NF32();
        Poetry.Restore(r.Int(), r.Int());
        Host.Load(r);
        if (r.Bool() != (Vm is not null)) throw r.Mismatch("the FSM program");
        Vm?.Load(r);
        if (r.Bool() != (Melee is not null)) throw r.Mismatch("the melee");
        Melee?.Load(r);
    }
}

public sealed partial class Poetry
{
    internal static void AuditFields(SaveAudit a) =>
        a.For<Poetry>()
            .Saved(nameof(_seed), nameof(_dealt))
            .Derived("dealt again from the seed and the count (Deal)", nameof(_rng), nameof(_poems), nameof(_signOffs), nameof(_poem), nameof(_signOff), nameof(_lines), nameof(_line))
            .Structural("the script's actor labelled taurus", nameof(Poet))
            .Structural("the mission's driver variant (Nitro or not), fixed at the build", nameof(Nitro), nameof(_allLines));

    internal void Save(SaveWriter w) { w.Int(_seed); w.Int(_dealt); }
    internal void Restore(int seed, int dealt) => Deal(seed, dealt);
}

public sealed partial class ScriptHost
{
    /// <summary>The entities the mission build made (the rest came while it ran: a melee's cars).</summary>
    internal int BuiltEntities;

    internal static void AuditFields(SaveAudit a)
    {
        a.For<ScriptHost>()
            .Saved(nameof(Entities), nameof(Objectives), nameof(Cb), nameof(View), nameof(Now), nameof(FsmTick), nameof(End), nameof(FrameDelta),
                nameof(_ptable), nameof(_playerDrivenTick), nameof(SkipPolled), nameof(Coop), nameof(_personal))
            .Structural("the mission build's world, player entity and wiring", nameof(World), nameof(Player), nameof(Roads), nameof(Combat), nameof(Damage), nameof(BuiltEntities))
            .Structural("the session's skip latch, linked by the session", nameof(SkipKey))
            .Structural("the AI tactics table, read from the install's executable by the mission build", nameof(Tactics))
            .Structural("a network melee's bot rules, set by its game at its start", nameof(HoldFire), nameof(HoldWeapon))
            .Derived("each car's entity, from the entities and their removal", nameof(_byVehicle))
            .Transient("the host's hooks", nameof(Logged), nameof(Movie), nameof(CarStarted), nameof(Salvaged));
        a.For<CoopGroup>()
            .Saved(nameof(CoopGroup.Seats), nameof(CoopGroup.LeadMoved), nameof(CoopGroup.WasCutscene))
            .Structural("the build's seats and car builder, and the mission's paths the formation keeps off", nameof(CoopGroup.RespawnDelay), nameof(CoopGroup.Build), nameof(CoopGroup.Paths),
                nameof(CoopGroup.ExtraEnemies), nameof(CoopGroup.BuildEnemy))
            .Transient("the host's hooks", nameof(CoopGroup.Happened), nameof(CoopGroup.Spawned));
        a.For<CoopSeat>()
            .Saved(nameof(CoopSeat.RespawnAt), nameof(CoopSeat.Deaths), nameof(CoopSeat.Present))
            .Structural("the build's seat", nameof(CoopSeat.Index), nameof(CoopSeat.Entity), nameof(CoopSeat.CarFile));
    }

    /// <summary>The entities made while the mission ran, as they were made (name, instance id, type, flags, car).</summary>
    internal void SaveTable(SaveWriter w)
    {
        w.Int(BuiltEntities);
        w.VarU((ulong)(Entities.Count - BuiltEntities));
        for (int i = BuiltEntities; i < Entities.Count; i++)
        {
            var e = Entities[i];
            w.Str(e.Name); w.Bytes(e.Id); w.U32(e.Type); w.U32(e.ObjectFlags); w.Car(e.Vehicle); w.Int(e.Static?.NetId ?? 0);
        }
    }

    internal void LoadTable(SaveReader r)
    {
        int built = r.Int();
        if (built != BuiltEntities) throw r.Mismatch($"the mission has {BuiltEntities} entities, the snapshot's {built}");
        if (Entities is not List<Entity> list) throw new InvalidOperationException("the script host's entities cannot be restored");
        list.RemoveRange(BuiltEntities, list.Count - BuiltEntities);
        for (int k = r.Count(); k > 0; k--)
        {
            var e = new Entity(r.Str() ?? "", r.Bytes(), r.U32(), r.U32()) { Vehicle = r.Car() };
            if (r.Int() is var sid and not 0) e.Static = r.Refs.ReadStatic(sid);
            list.Add(e);
        }
    }

    internal void Save(SaveWriter w)
    {
        w.F32(Now); w.Int(FsmTick); w.F32(FrameDelta); w.Int(_playerDrivenTick); w.Int(SkipPolled);
        w.Bool(End is not null);
        if (End is { } end) { w.Bool(end.Success); w.F32(end.At); w.Int(end.FailMessage); }
        Ptable.Save(w);
        foreach (var e in Entities) e.Save(w, Roads?.Pieces);
        w.Ints(Objectives.Flags);
        Cb.Save(w);
        View.Save(w);
        w.VarU((ulong)_personal.Count);
        foreach (var (seat, p) in _personal.OrderBy(kv => kv.Key)) { w.Int(seat); w.F32(p.Since); p.View.Save(w); }
        w.Bool(Coop is not null);
        if (Coop is { } c)
        {
            w.Bool(c.LeadMoved); w.Bool(c.WasCutscene);
            foreach (var s in c.Seats) { w.NF32(s.RespawnAt); w.Int(s.Deaths); w.Bool(s.Present); }
        }
    }

    internal void Load(SaveReader r)
    {
        Now = r.F32(); FsmTick = r.Int(); FrameDelta = r.F32(); _playerDrivenTick = r.Int(); SkipPolled = r.Int();
        End = r.Bool() ? (r.Bool(), r.F32(), r.Int()) : null;
        Ptable.Load(r);
        foreach (var e in Entities) e.Load(r, Roads?.Pieces, Ptable.BuiltBridges);
        var flags = r.Ints();
        if (flags.Length != Objectives.Flags.Length) throw r.Mismatch($"{Objectives.Flags.Length} objectives, the snapshot's {flags.Length}");
        flags.CopyTo(Objectives.Flags, 0);
        Cb.Load(r);
        View.Load(r);
        _personal.Clear();
        for (int k = r.Count(); k > 0; k--)
        {
            int seat = r.Int();
            float since = r.F32();
            var view = new ScriptCamera((x, z) => World.Ground.BareHeight(x, z)) { Log = Log };
            view.Load(r);
            _personal[seat] = (view, since);
        }
        if (r.Bool() != (Coop is not null)) throw r.Mismatch(Coop is null ? "the snapshot is of a co-op game" : "the snapshot is not of a co-op game");
        if (Coop is { } c)
        {
            c.LeadMoved = r.Bool(); c.WasCutscene = r.Bool();
            foreach (var s in c.Seats) { s.RespawnAt = r.NF32(); s.Deaths = r.Int(); s.Present = r.Bool(); }
        }
        // each car's entity as Add and Remove left it
        _byVehicle.Clear();
        foreach (var e in Entities)
            if (e.Vehicle is { } v)
            {
                if (!e.Removed) _byVehicle[v] = e;
                else _byVehicle.Remove(v);
            }
    }
}

public sealed partial class Ptable
{
    internal static void AuditFields(SaveAudit a)
    {
        a.For<Ptable>()
            .Saved(nameof(_planHolder), nameof(_planDenied), nameof(_planned), nameof(_losCaller), nameof(_losTurn), nameof(_losGranted))
            .Structural("the behaviour table, bound to its handlers", nameof(host), nameof(_states))
            .Derived("the bridge tree, built from the static world when first asked for (the snapshot keeps whether it was)", nameof(_bridges));
    }

    /// <summary>The bridge tree if it has been built (a search in progress was begun with it).</summary>
    internal BridgeLinks? BuiltBridges => _bridges;

    internal void Save(SaveWriter w)
    {
        w.Ent(_planHolder); w.Bool(_planDenied); w.Bool(_planned); w.Int(_losCaller); w.Int(_losTurn); w.Bool(_losGranted);
        w.Bool(_bridges is not null);
    }

    internal void Load(SaveReader r)
    {
        _planHolder = r.Ent(); _planDenied = r.Bool(); _planned = r.Bool(); _losCaller = r.Int(); _losTurn = r.Int(); _losGranted = r.Bool();
        _bridges = r.Bool() ? Bridges : null;
    }
}

public sealed partial class CbRadio
{
    internal static void AuditFields(SaveAudit a)
    {
        a.For<CbRadio>()
            .Saved(nameof(_q), nameof(NowMs), nameof(Stopped))
            .Structural("the sound lengths and the speakers' deaths, linked by the mission build", "lengthSeconds", "speakerDead")
            .Transient("the host's hooks", nameof(Started), nameof(Cut));
        a.For<Line>().Saved(nameof(Line.Name), nameof(Line.Speaker), nameof(Line.Priority), nameof(Line.QueuedMs), nameof(Line.StartedMs), nameof(Line.Length));
    }

    internal void Save(SaveWriter w)
    {
        w.F64(NowMs); w.Bool(Stopped);
        w.VarU((ulong)_q.Count);
        foreach (var l in _q) { w.Str(l.Name); w.Obj(l.Speaker); w.Int(l.Priority); w.F64(l.QueuedMs); w.F64(l.StartedMs); w.F32(l.Length); }
    }

    internal void Load(SaveReader r)
    {
        NowMs = r.F64(); Stopped = r.Bool();
        _q.Clear();
        for (int k = r.Count(); k > 0; k--)
            _q.Add(new Line(r.Str() ?? "", r.Obj(), r.Int(), r.F64()) { StartedMs = r.F64(), Length = r.F32() });
    }
}

public sealed partial class ScriptCamera
{
    internal static void AuditFields(SaveAudit a)
    {
        a.For<ScriptCamera>()
            .Saved(nameof(_op), nameof(_a), nameof(_o), nameof(_path), nameof(_arrived), nameof(_onPath), nameof(_seg), nameof(_t), nameof(_invLen),
                nameof(_target), nameof(_dir), nameof(_cam), nameof(Depth), nameof(Eye), nameof(LookAt), nameof(Basis), nameof(PrevEye), nameof(PrevLookAt), nameof(_cut),
                nameof(_stack), nameof(_heldLook), nameof(_heldBasis))
            .Structural("the terrain's height, linked by the script host", nameof(_ground))
            .Transient("a network client's mirror says whether the view is the player's own scene, every frame", nameof(Personal))
            .Transient("the host's log", nameof(Log));
    }

    internal void Save(SaveWriter w)
    {
        w.Str(_op); w.Ints(_a);
        w.VarU((ulong)_o.Length); foreach (var e in _o) w.Ent(e);
        w.Path(_path); w.Bool(_arrived); w.Path(_onPath); w.Int(_seg); w.F64(_t); w.F64(_invLen);
        w.V2(_target); w.V2(_dir); w.V2(_cam);
        w.Int(Depth); w.V3(Eye); w.NV3(LookAt); w.NRows(Basis); w.V3(PrevEye); w.NV3(PrevLookAt); w.Bool(_cut);
        w.VarU((ulong)_stack.Count);
        foreach (var s in _stack) { w.Bool(s.Placed); w.V3(s.Eye); w.NV3(s.LookAt); w.NRows(s.Basis); }
        w.NV3(_heldLook); w.NRows(_heldBasis);
    }

    internal void Load(SaveReader r)
    {
        _op = r.Str() ?? ""; _a = r.Ints();
        _o = new Entity?[r.Count()]; for (int i = 0; i < _o.Length; i++) _o[i] = r.Ent();
        _path = r.Path(); _arrived = r.Bool(); _onPath = r.Path(); _seg = r.Int(); _t = r.F64(); _invLen = r.F64();
        _target = r.V2(); _dir = r.V2(); _cam = r.V2();
        Depth = r.Int(); Eye = r.V3(); LookAt = r.NV3(); Basis = r.NRows(); PrevEye = r.V3(); PrevLookAt = r.NV3(); _cut = r.Bool();
        _stack.Clear();
        for (int k = r.Count(); k > 0; k--) _stack.Add((r.Bool(), r.V3(), r.NV3(), r.NRows()));
        _heldLook = r.NV3(); _heldBasis = r.NRows();
    }
}
