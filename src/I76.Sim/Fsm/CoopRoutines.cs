using I76.Formats.Mission;

namespace I76.Sim.Fsm;

/// <summary>
/// The mission scripts' copy-pasted library routines that must run once per human in a co-op game
/// (docs/multiplayer.md "Co-op"), recognised by their bytecode, not by the mission's name:
/// <list type="bullet">
/// <item>the leaving-the-map routine (every scripted mission of both games): a thread over (exit point, player)
/// that warns at 275 and 175 (Nitro 150) of the point (isWithinSqNav), fails the mission at 50 (Nitro 60) with
/// failAllObj(delay, 2), "abandoned", and then drives the car off along the exit path;</item>
/// <item>the friendly-fire counters: whoAttacked / whoShot / whoRammed of a friend, then isEqualId of the attacker
/// and the player, then inc of a counter another thread holds against a limit;</item>
/// <item>the civilian-kill counters: isGroovesFault of a non-enemy, then inc of a counter.</item>
/// </list>
/// The analysis is static: the program's routines decoded into calls with their arguments (an immediate, a
/// parameter, a local), each thread's parameters bound to the data cells its table names.
/// </summary>
public static class CoopRoutines
{
    /// <summary>What a counter counts.</summary>
    public enum Counter { FriendlyFire, CivilianKill }

    /// <summary>
    /// A counter's cell: a data cell (<see cref="Thread"/> −1), or a thread's own local (its stack index while the
    /// thread runs its first routine: Nitro keeps the friendly-fire count in the routine's local).
    /// </summary>
    public readonly record struct Cell(int Thread, int Index);

    /// <summary>
    /// A leave-map thread on the player: its exit point (the data cell holding the nav path) and the edge radius, the
    /// last warning's (175, Nitro 150): a human that near an exit is not a teammate to come back beside.
    /// </summary>
    public sealed record LeaveMapThread(int Thread, int PathCell, int Edge);

    /// <summary>The routines found: the threads that are a leave-map routine on the player, and the penalty counters.</summary>
    public sealed record Analysis(IReadOnlyList<LeaveMapThread> LeaveMap, IReadOnlyDictionary<Cell, Counter> Counters)
    {
        public static readonly Analysis None = new([], new Dictionary<Cell, Counter>());
        public IEnumerable<int> LeaveMapThreads => LeaveMap.Select(x => x.Thread);
    }

    /// <summary>A call's argument: an immediate (pushImm then argLocalAddr), a thread or routine parameter (fp − n), a local.</summary>
    internal readonly record struct Arg(char Kind, int Value)
    {
        public static Arg Imm(int v) => new('i', v);
        public static Arg Param(int n) => new('p', n);
        public static Arg Local(int n) => new('l', n);
        public bool IsImm(int v) => Kind == 'i' && Value == v;
    }

    /// <summary>One decoded instruction: an exec with its arguments, or a control op (yield, not).</summary>
    internal readonly record struct Op(int Pc, string Name, Arg[] Args);

    /// <summary>The routines' entries: the thread starts, the call targets, and an allocLocals right after a return.</summary>
    internal static SortedSet<int> Entries(FsmProgram p)
    {
        var e = new SortedSet<int>(p.Threads.Select(t => t.StartPc));
        for (int pc = 0; pc < p.Code.Length; pc++)
        {
            var (op, a) = p.Code[pc];
            if (op == 11) e.Add((int)a);
            if (op == 12 && pc + 1 < p.Code.Length && p.Code[pc + 1].Op == 6) e.Add(pc + 1);
        }
        e.RemoveWhere(x => x < 0 || x >= p.Code.Length);
        return e;
    }

    /// <summary>
    /// A routine from its entry to the next: the compiler's stack discipline is balanced per statement, so a linear
    /// walk from the entry (slot 0 the return pc) tracks which slot each pushImm filled.
    /// </summary>
    internal static List<Op> Decode(FsmProgram p, int from, int to)
    {
        var ops = new List<Op>();
        // which statement pushed each slot: an immediate is an argument only in the statement that pushed it (a
        // pushImm before the routine's first jump initialises a local)
        var slot = new Dictionary<int, (int Value, int Statement)>();
        var args = new List<Arg>();
        int sp = 1, statement = 0;
        for (int pc = from; pc < to && pc < p.Code.Length; pc++)
        {
            var (op, u) = p.Code[pc];
            int a = (int)u;
            switch (op)
            {
                case 1: slot[sp++] = (a, statement); break;
                case 2: case 3: slot.Remove(sp++); break;
                case 4: args.Add(a >= 1 && slot.TryGetValue(a, out var v) && v.Statement == statement ? Arg.Imm(v.Value) : a < 0 ? Arg.Param(-a) : Arg.Local(a)); break;
                case 5: args.Add(a < 0 ? Arg.Param(-a) : Arg.Local(a)); break;
                case 6: for (int k = 0; k < a; k++) slot.Remove(sp + k); sp += a; break;
                case 7: sp -= a; break;
                case 8: case 9: case 11: statement++; break;
                case 10: ops.Add(new Op(pc, "<yield>", [])); statement++; break;
                case 14: ops.Add(new Op(pc, "<not>", [])); break;
                case 13:
                    ops.Add(new Op(pc, a >= 0 && a < p.Prototypes.Count ? p.Prototypes[a].ToLowerInvariant() : "", [.. args]));
                    args.Clear();
                    statement++;
                    break;
            }
        }
        return ops;
    }

    /// <summary>The data cell a thread's parameter fp − n names (its table's stack entry), or −1.</summary>
    internal static int CellOf(FsmProgram p, int thread, int n)
    {
        var (_, depth, stack) = p.Threads[thread];
        int k = depth + 1 - n;
        return k >= 0 && k < depth && k < stack.Length ? stack[k] : -1;
    }

    /// <param name="isPlayer">Whether an actor index is the player's (a group slot).</param>
    /// <param name="team">An actor's team, or null when it names nothing.</param>
    public static Analysis Analyze(FsmProgram p, Func<int, bool> isPlayer, Func<int, int?> team)
    {
        var entries = Entries(p).ToList();
        var routines = new Dictionary<int, List<Op>>();
        for (int i = 0; i < entries.Count; i++)
            routines[entries[i]] = Decode(p, entries[i], i + 1 < entries.Count ? entries[i + 1] : p.Code.Length);
        int Param(int thread, int n) => CellOf(p, thread, n);
        bool PlayerCell(int cell) => cell >= 0 && cell < p.Data.Length && isPlayer(p.Data[cell]);
        bool Friendly(int cell) => cell >= 0 && cell < p.Data.Length && team(p.Data[cell]) is { } tm && tm != 2;

        var leave = new List<LeaveMapThread>();
        var counters = new Dictionary<Cell, Counter>();
        var enemyCounted = new HashSet<Cell>();
        for (int ti = 0; ti < p.Threads.Count; ti++)
        {
            if (!routines.TryGetValue(p.Threads[ti].StartPc, out var ops)) continue;
            if (LeaveMap(ops) is var (actorParam, pathParam, edge) && PlayerCell(Param(ti, actorParam))) leave.Add(new LeaveMapThread(ti, Param(ti, pathParam), edge));
            foreach (var (kind, victim, player, counter) in Counted(ops))
            {
                // a parameter's data cell, or the thread's local (fp = depth + 1 in its first routine)
                var cc = counter.Kind == 'p' ? new Cell(-1, Param(ti, counter.Value)) : new Cell(ti, p.Threads[ti].Depth + 1 + counter.Value);
                if (cc.Index < 0) continue;
                bool penalty = Friendly(Param(ti, victim)) && (player < 0 || PlayerCell(Param(ti, player)));
                if (penalty) counters.TryAdd(cc, kind);
                else enemyCounted.Add(cc);
            }
        }
        // a cell some routine also counts an enemy's death into is a goal, shared as the original counts it
        foreach (var c in enemyCounted) counters.Remove(c);
        return new Analysis(leave, counters);
    }

    /// <summary>
    /// The leave-map routine: at least two isWithinSqNav(exit, actor, r) on the same pair of parameters with one r at
    /// least 250, and a failAllObj(delay, 2) ("abandoned"). Returns the actor's and the exit's parameters and the
    /// edge radius (the second largest).
    /// </summary>
    internal static (int Actor, int Path, int Edge)? LeaveMap(List<Op> ops)
    {
        var tests = ops.Where(o => o.Name == "iswithinsqnav" && o.Args.Length == 3 && o.Args[0].Kind == 'p' && o.Args[1].Kind == 'p' && o.Args[2].Kind == 'i').ToList();
        if (tests.Count < 2 || !tests.Any(o => o.Args[2].Value >= 250)) return null;
        var pairs = tests.Select(o => (o.Args[0].Value, o.Args[1].Value)).Distinct().ToList();
        if (pairs.Count != 1) return null;
        if (!ops.Any(o => o.Name == "failallobj" && o.Args.Length == 2 && o.Args[1].IsImm(2))) return null;
        var radii = tests.Select(o => o.Args[2].Value).Distinct().OrderDescending().ToList();
        return (pairs[0].Item2, pairs[0].Item1, radii.Count > 1 ? radii[1] : radii[0]);
    }

    /// <summary>
    /// The counting statements: a positive isEqualId(attacker, player) on a local a whoX of a friend wrote, or a
    /// positive isGroovesFault(victim), followed by inc(counter) before the thread yields: (kind, victim, player or −1) as parameters, the counter a parameter or a local.
    /// </summary>
    internal static IEnumerable<(Counter Kind, int Victim, int Player, Arg Counter)> Counted(List<Op> ops)
    {
        var whoOf = new Dictionary<int, int>();   // local → the victim parameter whose attacker it holds
        for (int i = 0; i < ops.Count; i++)
        {
            var o = ops[i];
            if (o.Name is "whoattacked" or "whoshot" or "whorammed" && o.Args.Length == 2 && o.Args[0].Kind == 'p' && o.Args[1].Kind == 'l')
            {
                whoOf[o.Args[1].Value] = o.Args[0].Value;
                continue;
            }
            Counter kind; int victim, player = -1;
            if (o.Name == "isequalid" && o.Args.Length == 2)
            {
                var (x, y) = (o.Args[0], o.Args[1]);
                if (x.Kind == 'p' && y.Kind == 'l') (x, y) = (y, x);
                if (x.Kind != 'l' || y.Kind != 'p' || !whoOf.TryGetValue(x.Value, out victim)) continue;
                kind = Counter.FriendlyFire; player = y.Value;
            }
            else if (o.Name == "isgroovesfault" && o.Args.Length == 1 && o.Args[0].Kind == 'p')
            {
                kind = Counter.CivilianKill; victim = o.Args[0].Value;
            }
            else continue;
            if (i + 1 < ops.Count && ops[i + 1].Name == "<not>") continue;
            for (int j = i + 1; j < ops.Count && ops[j].Name != "<yield>"; j++)
                if (ops[j].Name == "inc" && ops[j].Args.Length == 1 && ops[j].Args[0].Kind is 'p' or 'l')
                {
                    yield return (kind, victim, player, ops[j].Args[0]);
                    break;
                }
        }
    }
}
