using I76.Formats.Mission;

namespace I76.Formats.Fsm;

/// <summary>One place in a mission script that names a nav path: the path's index in the table, the instruction and its thread.</summary>
public readonly record struct PathUse(int Path, string Op, int Thread, int Pc);

/// <summary>
/// Which nav paths a mission script names, read from its bytecode without running it. The script holds no labels: an order like
/// goto or a test like isWithinNav takes the path's index in the table, from a thread parameter (a data cell, set before the
/// mission starts, forwarded through calls) or an immediate. Most shipped paths are passed one of those two ways; an index that
/// comes from anywhere else (a local the script sets while it runs) leaves the analysis <see cref="Complete"/> false, and then every
/// entry of the table counts as named.
/// </summary>
public sealed class FsmPathUse
{
    /// <summary>The script's instructions that take a nav path, and the argument slot it is in.</summary>
    public static readonly IReadOnlyDictionary<string, int> PathSlot = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        ["goto"] = 1, ["evade"] = 1, ["race"] = 1, ["teleport"] = 1, ["teleportOffset"] = 1,
        ["isWithinNav"] = 0, ["isWithinSqNav"] = 0, ["camPosObj"] = 0, ["camPosDir"] = 0, ["camTransObj"] = 0, ["camTransDir"] = 0,
    };

    public IReadOnlyList<PathUse> Uses { get; }
    /// <summary>The instructions whose path argument could not be traced (thread, pc).</summary>
    public IReadOnlyList<(int Thread, int Pc)> Untraced { get; }
    /// <summary>Every path argument was traced to its index.</summary>
    public bool Complete => Untraced.Count == 0;
    /// <summary>The table's length when it was analysed.</summary>
    public int Count { get; }

    private FsmPathUse(List<PathUse> uses, List<(int, int)> untraced, int count) { Uses = uses; Untraced = untraced; Count = count; }

    /// <summary>No script: nothing named.</summary>
    public static FsmPathUse None(int count) => new([], [], count);

    /// <summary>The places that name table entry <paramref name="index"/>.</summary>
    public IEnumerable<PathUse> Of(int index) => Uses.Where(u => u.Path == index);

    /// <summary>An argument: 'i' an immediate, 'p' a parameter fp − n, 'l' a local; resolved, 'c' a data cell or '?' unknown.</summary>
    private readonly record struct Arg(char Kind, int Value);

    public static FsmPathUse Of(FsmProgram p)
    {
        if (p.Code.Length == 0) return None(p.Paths.Count);
        var uses = new HashSet<PathUse>();
        var untraced = new HashSet<(int, int)>();
        for (int ti = 0; ti < p.Threads.Count; ti++)
        {
            int from = p.Threads[ti].StartPc;
            if (from < 0 || from >= p.Code.Length) continue;
            var (_, depth, stack) = p.Threads[ti];
            // a thread's parameters are the data cells its table entry names (fp − n is stack[depth + 1 − n])
            var param = new Dictionary<int, Arg>();
            for (int k = 0; k < depth && k < stack.Length; k++) param[depth + 1 - k] = new Arg('c', stack[k]);
            Walk(p, ti, from, param, uses, untraced, []);
        }
        return new FsmPathUse([.. uses.OrderBy(u => u.Pc).ThenBy(u => u.Thread)], [.. untraced], p.Paths.Count);
    }

    /// <summary>
    /// The routine at <paramref name="from"/> as a thread runs it, its parameters bound: the path arguments of what it executes, and the
    /// routines it calls with the arguments pushed for them (a parameter forwarded, an immediate).
    /// </summary>
    private static void Walk(FsmProgram p, int thread, int from, Dictionary<int, Arg> param, HashSet<PathUse> uses, HashSet<(int, int)> untraced, HashSet<string> seen)
    {
        string key = from + ":" + string.Join(",", param.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}{kv.Value.Kind}{kv.Value.Value}"));
        if (!seen.Add(key) || seen.Count > 4096) return;
        // what control flow reaches from the start (a script holds routines no thread runs)
        var reach = Reachable(p, from);
        Arg Bind(Arg a) => a.Kind == 'p' ? param.GetValueOrDefault(a.Value, new Arg('?', 0)) : a.Kind == 'l' ? new Arg('?', 0) : a;
        foreach (var (pc, name, args) in Decode(p, from, reach.Max + 1))
        {
            if (!reach.Contains(pc)) continue;
            if (name.Length == 0)
            {
                // a call: the callee's fp − n is the n-th argument from the last
                int target = (int)p.Code[pc].Arg;
                var inner = new Dictionary<int, Arg>();
                for (int k = 0; k < args.Length; k++) inner[args.Length + 1 - k] = Bind(args[k]);
                if (target >= 0 && target < p.Code.Length) Walk(p, thread, target, inner, uses, untraced, seen);
                continue;
            }
            if (!PathSlot.TryGetValue(name, out int slot)) continue;
            var a = slot < args.Length ? Bind(args[slot]) : new Arg('?', 0);
            int index = a.Kind switch
            {
                'i' => a.Value,
                'c' when a.Value >= 0 && a.Value < p.Data.Length => p.Data[a.Value],
                _ => -1,
            };
            if (index < 0) untraced.Add((thread, pc));
            else uses.Add(new PathUse(index, name, thread, pc));
        }
    }

    /// <summary>The instructions control flow reaches from <paramref name="start"/> (a call's routine is not followed: its pc after is).</summary>
    private static SortedSet<int> Reachable(FsmProgram p, int start)
    {
        var seen = new SortedSet<int>();
        var todo = new Stack<int>();
        todo.Push(start);
        while (todo.Count > 0)
        {
            int pc = todo.Pop();
            if (pc < 0 || pc >= p.Code.Length || !seen.Add(pc)) continue;
            var (op, a) = p.Code[pc];
            switch (op)
            {
                case 8: case 10: todo.Push((int)a); break;
                case 9: todo.Push((int)a); todo.Push(pc + 1); break;
                case 12: break;
                default: todo.Push(pc + 1); break;
            }
        }
        return seen;
    }

    /// <summary>
    /// A routine's execs with their arguments: an immediate (pushImm, then argLocalAddr of the slot it filled in the same statement),
    /// a parameter (fp − n), or a local; and its calls (an empty name) with the values pushed for them. The compiler's stack discipline
    /// is balanced per statement, so a linear walk tracks the slots.
    /// </summary>
    private static IEnumerable<(int Pc, string Name, Arg[] Args)> Decode(FsmProgram p, int from, int to)
    {
        var slot = new Dictionary<int, (int Value, int Statement)>();
        var args = new List<Arg>();
        var pushed = new List<Arg>();
        int sp = 1, statement = 0;
        for (int pc = from; pc < to && pc < p.Code.Length; pc++)
        {
            var (op, u) = p.Code[pc];
            int a = (int)u;
            switch (op)
            {
                case 1: slot[sp++] = (a, statement); pushed.Add(new Arg('i', a)); break;
                case 2: slot.Remove(sp++); pushed.Add(new Arg('?', 0)); break;
                // pushLocal of a parameter copies its pointer to the data: a called routine's parameter is then that cell
                case 3: slot.Remove(sp++); pushed.Add(a < 0 ? new Arg('p', -a) : new Arg('l', a)); break;
                // a temporary pushed in this statement is an immediate; a local holds what the script last put in it, not followed
                case 4: args.Add(a >= 1 && slot.TryGetValue(a, out var v) && v.Statement == statement ? new Arg('i', v.Value) : a < 0 ? new Arg('p', -a) : new Arg('l', a)); break;
                case 5: args.Add(a < 0 ? new Arg('p', -a) : new Arg('l', a)); break;
                case 6: for (int k = 0; k < a; k++) slot.Remove(sp + k); sp += a; break;
                case 7: sp -= a; break;
                case 11:
                    yield return (pc, "", [.. pushed]);
                    statement++; pushed.Clear(); args.Clear();
                    break;
                case 8: case 9: case 10: case 12: statement++; pushed.Clear(); break;
                case 13:
                    yield return (pc, a >= 0 && a < p.Prototypes.Count ? p.Prototypes[a] : "?", [.. args]);
                    args.Clear(); pushed.Clear();
                    statement++;
                    break;
            }
        }
    }
}
