using System.Diagnostics;
using I76.Sim.Save;
using I76.Sim.Session;
using I76.Sim.Vehicles;

namespace I76.Sim.Tests;

/// <summary>What the corpus's snapshots caught the simulation in the middle of.</summary>
public sealed class SnapshotCoverage
{
    public readonly HashSet<string> Seen = [];
    public void Note(SimSession s)
    {
        lock (Seen)
        {
            if (s.Cutscene) Seen.Add("cutscene");
            if (s.SkipLatched) Seen.Add("skip latch");
            if (s.Combat is { } c)
            {
                if (c.Rounds.Any(r => r.Kind is not (0xc or 0xe or 0xf or 0x10 or 0x11))) Seen.Add("rounds in flight");
                if (c.Rounds.Any(r => r.Kind is 0xf or 0x10)) Seen.Add("mines");
                if (c.Rounds.Any(r => r.Kind is 0xc or 0xe or 0x11)) Seen.Add("oil or fire drops");
                if (c.Rounds.Any(r => r.Kind is 3 or 8 or 0x14 && r.Target is not null)) Seen.Add("guided round with a target");
                if (c.Flames.Count > 0) Seen.Add("flames");
                if (c.RadarTarget.Values.Any(v => v is not null)) Seen.Add("radar target");
            }
            if (s.World.Vehicles.Any(v => ((uint)v.Flags & 0x8000) != 0 && !v.Wreck)) Seen.Add("dying car");
            if (s.World.Vehicles.Any(v => v.Wreck)) Seen.Add("wreck");
            if (s.Script?.Host.Entities.Any(e => e.Removed) == true) Seen.Add("removed entity");
            if (s.World.Ground is Collision.IHasStatics hs && hs.World.Objects.Any(o => o.Nodes.Any(n => n.Gate is { Moving: true }))) Seen.Add("gate mid-slide");
            if (s.World.Ground is Collision.IHasStatics hs2 && hs2.World.Objects.Any(o => o.Destroyed || o.Nodes.Any(n => n.Destroyed))) Seen.Add("structure destroyed");
            if (s.Script?.Host.Entities.Any(e => e.Search is not null) == true) Seen.Add("path search in progress");
            if (s.Script?.Host.Cb.Current is not null) Seen.Add("CB line playing");
            if (s.Script?.Host.Objectives.Flags.Any(f => (f & (Missions.Objectives.Succeeded | Missions.Objectives.Failed)) != 0) == true) Seen.Add("objective done");
            if (s.Melee is { } m) Seen.Add(m.Rules.Network ? $"network melee ({m.Rules.Kind})" : "melee");
            if (s.World.Vehicles.Any(v => (v.Flags & VehicleFlags.Jamming) != 0)) Seen.Add("jamming");
            if (s.World.Vehicles.Any(v => (v.Flags & VehicleFlags.TurboDrive) != 0)) Seen.Add("nitro");
            if (s.Script?.GotDown == true) Seen.Add("getdown");
            if (s.World.Player is { } pl && (pl.Flags & VehicleFlags.Dead) != 0) Seen.Add("player dead");
            if (s.World.Player is { } p2 && (p2.Flags & VehicleFlags.EngineOn) == 0) Seen.Add("engine off");
            if (s.World.Vehicles.Any(v => v.Arms.Right.Cur != 0 || v.Arms.Left.Cur != 0)) Seen.Add("arm raised");
            if (s.World.Vehicles.Any(v => v.Wheels.Any(w => w.Blown))) Seen.Add("tyre blown");
            if (s.Script?.Host.Entities.Any(e => e.Controls.Count > 0) == true) Seen.Add("control queue");
            if (s.World.Vehicles.Any(v => v.Heli is not null)) Seen.Add("helicopter");
            if (s.World.Vehicles.Any(v => v.Emplacement is not null)) Seen.Add("emplacement");
            Seen.Add($"{s.Rate.Label} Hz");
        }
    }
}

/// <summary>
/// The proof that a snapshot holds everything that matters (docs/savestate.md): a session is run with fixed
/// inputs and snapshotted at chosen ticks; for each, the original goes on for M ticks and its whole state is
/// dumped (<see cref="StateDump"/>, independent of the snapshot's own code); a fresh session built from the
/// install with another rand seed takes the snapshot, must dump the same at once, snapshot back to the same
/// bytes, and after the same M ticks of the same inputs dump the same again.
/// </summary>
public static class SnapshotEquivalence
{
    public sealed record Point(string Why, int Tick);

    /// <summary>A moment worth a snapshot: the first tick in [From, Until] where it holds.</summary>
    public sealed record Event(string Name, Func<SimSession, bool> Holds, int From = 0, int Until = int.MaxValue);

    /// <summary>The moments the corpus looks for in every mission.</summary>
    public static Event[] Moments()
    {
        int[]? flags = null;
        return
        [
            new("objective done", s =>
            {
                var f = s.Script?.Host.Objectives.Flags;
                if (f is null) return false;
                flags ??= [.. f];
                return f.Where((x, i) => (x & ~flags[i] & (Missions.Objectives.Succeeded | Missions.Objectives.Failed)) != 0).Any();
            }, 400, 4000),
            new("rounds in flight", s => s.Combat?.Rounds.Any(r => r.Kind is not (0xc or 0xe or 0xf or 0x10 or 0x11)) == true, 400, 4000),
            new("dying car", s => s.World.Vehicles.Any(v => ((uint)v.Flags & 0x8000) != 0 && !v.Wreck), 0, 4000),
            new("wreck", s => s.World.Vehicles.Any(v => v.Wreck), 0, 4000),
            new("gate mid-slide", s => s.World.Ground is Collision.IHasStatics hs && hs.World.Gated.Any(o => o.Nodes.Any(n => n.Gate is { Moving: true })), 0, 4000),
            new("path search", s => s.Script?.Host.Entities.Any(e => e.Search is not null) == true, 400, 4000),
            new("flames", s => s.Combat?.Flames.Count > 0, 0, 4000),
            new("mines", s => s.Combat?.Rounds.Any(r => r.Kind is 0xf or 0x10) == true, 0, 4000),
            new("control queue", s => s.Script?.Host.Entities.Any(e => e.Controls.Count > 0) == true, 0, 4000),
        ];
    }

    public sealed class Report
    {
        public readonly List<string> Failures = [];
        public readonly List<string> Notes = [];
        public int Snapshots;
        public long MaxBytes, MaxBody;
        public double MaxSaveMs, MaxRestoreMs, MaxBuildMs;
    }

    /// <param name="ticks">The fixed snapshot ticks.</param>
    /// <param name="events">Also a snapshot at the first tick each of these holds.</param>
    /// <param name="prepare">What the host does to a freshly built session (debug hooks, play options), the original and the rebuilt alike.</param>
    public static Report Run(SnapshotRig.Loaded l, SessionBuild b, SimRate rate, int[] ticks, int m, SnapshotCoverage? cov = null,
        IReadOnlyList<Event>? events = null, Action<SimSession>? prepare = null,
        IReadOnlyList<(string Car, int Team)>? players = null, bool hotkeys = true, string? label = null, Func<int, int, CarCommands>? extra = null)
    {
        var rep = new Report();
        label ??= $"{b.Mission}@{rate.Label}";
        var sw = Stopwatch.StartNew();
        var (orig, _) = SnapshotRig.Make(l, b, rate, 1, players);
        rep.MaxBuildMs = sw.Elapsed.TotalMilliseconds;
        prepare?.Invoke(orig);
        var pending = new SortedSet<int>(ticks);
        var taken = new List<(Point P, byte[] Snap, string Dump, string? After, ulong Hash)>();
        var open = new List<Event>(events ?? []);
        int stop = ticks.DefaultIfEmpty(0).Max() + m;
        while (true)
        {
            SnapshotRig.Tick(orig, hotkeys, extra);
            int t = orig.Ticks;
            Point? p = null;
            if (pending.Remove(t)) p = new Point("tick", t);
            for (int i = 0; i < open.Count; i++)
            {
                var e = open[i];
                if (t > e.Until) { open.RemoveAt(i--); continue; }
                if (t < e.From || !e.Holds(orig)) continue;
                open.RemoveAt(i--);
                if (p is null) { p = new Point(e.Name, t); stop = System.Math.Max(stop, t + m); }
            }
            if (p is not null)
            {
                sw.Restart();
                var snap = orig.Snapshot();
                rep.MaxSaveMs = System.Math.Max(rep.MaxSaveMs, sw.Elapsed.TotalMilliseconds);
                rep.MaxBytes = System.Math.Max(rep.MaxBytes, snap.Length);
                rep.MaxBody = System.Math.Max(rep.MaxBody, orig.SnapshotBody().Length);
                taken.Add((p, snap, StateDump.Of(orig), null, 0));
                cov?.Note(orig);
            }
            for (int i = 0; i < taken.Count; i++)
                if (taken[i].After is null && t == taken[i].P.Tick + m) taken[i] = taken[i] with { After = StateDump.Of(orig), Hash = orig.StateHash() };
            SnapshotRig.TakeEvents(orig);
            if (t >= stop && pending.Count == 0 && open.Count == 0 && taken.All(x => x.After is not null)) break;
            if (orig.Script?.Result is not null && t > stop + 2000) break;
        }
        rep.Snapshots = taken.Count;
        uint seed = 0xbeef;
        foreach (var (p, snap, dump, after, hash) in taken)
        {
            string at = $"{label} {p.Why} at tick {p.Tick}";
            try
            {
                sw.Restart();
                // a rebuilt session (another rand seed: nothing the build draws may matter), network players not joined: the snapshot makes them
                var (fresh, _) = SnapshotRig.Make(l, b, rate, seed++);
                rep.MaxBuildMs = System.Math.Max(rep.MaxBuildMs, sw.Elapsed.TotalMilliseconds);
                prepare?.Invoke(fresh);
                sw.Restart();
                var made = fresh.Restore(snap);
                if (cov is not null)
                    lock (cov.Seen)
                    {
                        if (made.Created.Count > 0) cov.Seen.Add("restore made cars");
                        if (made.Dropped.Count > 0) cov.Seen.Add("restore dropped cars");
                    }
                rep.MaxRestoreMs = System.Math.Max(rep.MaxRestoreMs, sw.Elapsed.TotalMilliseconds);
                if (StateDump.FirstDifference(dump, StateDump.Of(fresh)) is { } d0) { rep.Failures.Add($"{at}: restored state differs from the original's\n{d0}"); continue; }
                var again = fresh.Snapshot();
                if (!again.AsSpan().SequenceEqual(snap)) rep.Failures.Add($"{at}: snapshot -> restore -> snapshot is not byte-identical ({snap.Length} vs {again.Length} bytes)");
                SnapshotRig.TakeEvents(fresh);
                for (int k = 0; k < m; k++)
                {
                    SnapshotRig.Tick(fresh, hotkeys, extra);
                    if (k < m - 1) SnapshotRig.TakeEvents(fresh);
                }
                var d1 = StateDump.FirstDifference(after!, StateDump.Of(fresh));
                if (d1 is not null)
                {
                    rep.Failures.Add($"{at}: after {m} more ticks the restored run differs from the original\n{d1}");
                    if (fresh.StateHash() == hash) rep.Notes.Add($"{at}: StateHash agrees although the state differs");
                }
                else if (fresh.StateHash() != hash) rep.Failures.Add($"{at}: the dumps agree but StateHash does not");
            }
            catch (Exception ex) { rep.Failures.Add($"{at}: {ex}"); }
        }
        return rep;
    }
}
