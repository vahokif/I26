using I76.Formats.Mission;
using I76.Sim.Missions;
using I76.Sim.Save;

namespace I76.Sim.Fsm;

/// <summary>
/// The script VM's state: the shared data, the actor table (with the entries opcodes added as they ran), and
/// every thread's registers, condition, pending arguments and stack. A stack cell keeps what it held when the
/// stack shrinks (op 6 reuses it unwritten), so each stack is saved up to its last cell that is not empty, not
/// only up to its pointer.
/// </summary>
public sealed partial class FsmVm
{
    /// <summary>The program the VM runs.</summary>
    internal FsmProgram Program => _p;

    internal static void AuditFields(SaveAudit a)
    {
        a.For<FsmVm>()
            .Saved(nameof(Data), nameof(Threads), nameof(Actors), nameof(TickCount), nameof(Watchdog), nameof(_warned), nameof(_shadows))
            .Structural("the mission's program and the script host, and the session's rand stream", nameof(_p), nameof(_host), nameof(_rand))
            .Structural("co-op's group slots and routines, found again from the program and the seats by the build", nameof(_group), nameof(_coop), nameof(Routines), nameof(_personalCams), nameof(CameraSites), nameof(GateSites))
            .Transient("set and taken within one instruction", nameof(_restart));
        a.For<Thread>().Saved(nameof(Thread.Stack), nameof(Thread.Args), nameof(Thread.Pc), nameof(Thread.Fp), nameof(Thread.Sp), nameof(Thread.Cond),
            nameof(Thread.Finished), nameof(Thread.Index), nameof(Thread.Match), nameof(Thread.Prefer), nameof(Thread.Scene), nameof(Thread.SceneDepth),
            nameof(Thread.GatePc), nameof(Thread.GateSince))
            .Structural("a co-op per-human copy, made again by the build", nameof(Thread.Bound), nameof(Thread.Source), nameof(Thread.LeaveMap));
        a.For<Slot>().Saved(nameof(Slot.Value), nameof(Slot.Ptr));
        a.For<CoopRoutines.Cell>().Structural("a co-op counter's cell, found in the program by the build (the snapshot checks it)", nameof(CoopRoutines.Cell.Thread), nameof(CoopRoutines.Cell.Index));
        a.For<Ref>().Saved(nameof(Ref.Data), nameof(Ref.Index), nameof(Ref.T));
    }

    internal void Save(SaveWriter w)
    {
        w.Ints(Data);
        w.Int(TickCount); w.Bool(Watchdog);
        var warned = _warned.OrderBy(s => s, StringComparer.Ordinal).ToList();
        w.VarU((ulong)warned.Count); foreach (var s in warned) w.Str(s);
        w.Int(_p.Actors.Count);
        w.VarU((ulong)Actors.Count);
        for (int i = 0; i < Actors.Count; i++)
        {
            var (label, id, handle) = Actors[i];
            if (i >= _p.Actors.Count) { w.Str(label); w.Bytes(id); }
            w.Obj(handle);
        }
        // co-op's per-human counts, in cell order
        var shadows = _shadows.OrderBy(kv => kv.Key.Thread).ThenBy(kv => kv.Key.Index).ToList();
        w.VarU((ulong)shadows.Count);
        foreach (var (c, per) in shadows) { w.Int(c.Thread); w.Int(c.Index); w.Bool(per is not null); if (per is not null) w.Ints(per); }
        w.VarU((ulong)Threads.Count);
        foreach (var t in Threads)
        {
            w.Int(t.Index); w.Int(t.Pc); w.Int(t.Fp); w.Int(t.Sp); w.Bool(t.Cond); w.Bool(t.Finished); w.Obj(t.Match); w.Int(t.Prefer); w.Int(t.Scene); w.Int(t.SceneDepth); w.Int(t.GatePc); w.F32(t.GateSince);
            w.VarU((ulong)t.Args.Count); foreach (var r in t.Args) WriteRef(w, r);
            int used = t.Stack.Length;
            while (used > 0 && t.Stack[used - 1].Value == 0 && t.Stack[used - 1].Ptr is null) used--;
            w.VarU((ulong)used);
            for (int k = 0; k < used; k++)
            {
                w.Int(t.Stack[k].Value);
                if (t.Stack[k].Ptr is { } p) { w.Bool(true); WriteRef(w, p); } else w.Bool(false);
            }
        }
    }

    private void WriteRef(SaveWriter w, Ref r)
    {
        w.Bool(r.Data); w.Int(r.Index);
        w.Int(r.T is null ? -1 : Threads.IndexOf(r.T) is var i and >= 0 ? i : throw new InvalidOperationException("snapshot: an FSM pointer into a thread the VM does not run"));
    }

    private Ref ReadRef(SaveReader r)
    {
        bool data = r.Bool(); int index = r.Int(); int t = r.Int();
        return new Ref(data, index, t == -1 ? null : t >= 0 && t < Threads.Count ? Threads[t] : throw new SnapshotFormatException($"FSM thread {t} of {Threads.Count}"));
    }

    internal void Load(SaveReader r)
    {
        var data = r.Ints();
        if (data.Length != Data.Length) throw r.Mismatch($"the FSM's data has {Data.Length} cells, the snapshot's {data.Length}");
        data.CopyTo(Data, 0);
        TickCount = r.Int(); Watchdog = r.Bool();
        _warned.Clear();
        for (int k = r.Count(); k > 0; k--) _warned.Add(r.Str() ?? "");
        if (r.Int() != _p.Actors.Count) throw r.Mismatch("the FSM's actor table");
        int n = r.Count();
        Actors.RemoveRange(_p.Actors.Count, Actors.Count - _p.Actors.Count);
        for (int i = 0; i < n; i++)
        {
            if (i < _p.Actors.Count) { var (label, id, _) = Actors[i]; Actors[i] = (label, id, r.Obj()); }
            else { var label = r.Str() ?? ""; var id = r.Bytes(); Actors.Add((label, id, r.Obj())); }
        }
        int ns = r.Count();
        if (ns != _shadows.Count) throw r.Mismatch("the FSM's co-op counters");
        for (int k = 0; k < ns; k++)
        {
            var c = new CoopRoutines.Cell(r.Int(), r.Int());
            if (!_shadows.ContainsKey(c)) throw r.Mismatch("an FSM co-op counter");
            _shadows[c] = r.Bool() ? r.Ints() : null;
        }
        if (r.Count() != Threads.Count) throw r.Mismatch("the FSM's threads");
        foreach (var t in Threads)
        {
            if (r.Int() != t.Index) throw r.Mismatch("an FSM thread's index");
            t.Pc = r.Int(); t.Fp = r.Int(); t.Sp = r.Int(); t.Cond = r.Bool(); t.Finished = r.Bool(); t.Match = r.Obj(); t.Prefer = r.Int(); t.Scene = r.Int(); t.SceneDepth = r.Int(); t.GatePc = r.Int(); t.GateSince = r.F32();
            t.Args.Clear();
            for (int k = r.Count(); k > 0; k--) t.Args.Add(ReadRef(r));
            int used = r.Count(t.Stack.Length);
            Array.Clear(t.Stack);
            for (int k = 0; k < used; k++)
            {
                t.Stack[k].Value = r.Int();
                t.Stack[k].Ptr = r.Bool() ? ReadRef(r) : null;
            }
        }
    }
}
