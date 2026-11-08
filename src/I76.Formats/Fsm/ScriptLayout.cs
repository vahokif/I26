namespace I76.Formats.Fsm;

/// <summary>
/// How nested steps lay out as the routine's states, and what each step's implicit transitions mean. States are numbered in
/// source order: a step, then the branches of its rules (in rule order, each branch's steps in turn), then the next step; a
/// repeat's steps in order. The decompiler and the compiler share this, so a rule's unwritten <c>goto</c> means the same state
/// in both.
/// </summary>
public static class ScriptLayout
{
    /// <summary>The thread ends: a target that is no state.</summary>
    public const int Stop = -1;

    public sealed class Flat
    {
        public required ScriptStep Step { get; init; }
        public int Index { get; init; }
        /// <summary>Where an unwritten goto goes: the following step, the step after the choice, the repeat's start, or <see cref="Stop"/>.</summary>
        public int Cont { get; set; }
        /// <summary>The next step of the same list, or null.</summary>
        public int? Next { get; set; }
        /// <summary>The innermost repeat: its first state and what follows it.</summary>
        public (int Head, int After)? Loop { get; set; }
    }

    /// <summary>The steps as states, in order, with their contexts; and the first state of each rule's branch.</summary>
    public static (List<Flat> States, Dictionary<ScriptRule, int> Arms) Flatten(List<ScriptNode> steps)
    {
        var flat = new List<Flat>();
        var first = new Dictionary<ScriptNode, int>();
        var arms = new Dictionary<ScriptRule, int>();
        void Number(List<ScriptNode> nodes)
        {
            if (nodes.Count == 0) throw new ScriptError("an empty list of steps (a repeat or a branch needs at least one)");
            foreach (var n in nodes)
                switch (n)
                {
                    case ScriptStep s:
                        first[n] = flat.Count;
                        flat.Add(new Flat { Step = s, Index = flat.Count });
                        foreach (var r in s.Rules)
                            if (r.Steps is { } arm)
                            {
                                Number(arm);
                                arms[r] = first[arm[0]];
                            }
                        break;
                    case ScriptRepeat rep:
                        Number(rep.Steps);
                        first[n] = first[rep.Steps[0]];
                        break;
                }
        }
        void Context(List<ScriptNode> nodes, int after, (int, int)? loop)
        {
            for (int i = 0; i < nodes.Count; i++)
            {
                int? next = i + 1 < nodes.Count ? first[nodes[i + 1]] : null;
                int cont = next ?? after;
                switch (nodes[i])
                {
                    case ScriptStep s:
                        var f = flat[first[s]];
                        f.Cont = cont;
                        f.Next = next;
                        f.Loop = loop;
                        foreach (var r in s.Rules)
                            if (r.Steps is { } arm) Context(arm, cont, loop);
                        break;
                    case ScriptRepeat rep:
                        int head = first[rep];
                        Context(rep.Steps, head, (head, cont));
                        break;
                }
            }
        }
        if (steps.Count == 0) return (flat, arms);
        Number(steps);
        Context(steps, Stop, null);
        return (flat, arms);
    }

    /// <summary>
    /// A nesting of a routine's states (by index, with their targets per rule; <see cref="Stop"/> for a return) whose layout
    /// is the states' own order: repeats where a later state goes back to an earlier one, branches where a state's rules go
    /// forward into states only reachable through it. Any contiguous nesting is correct (the transitions are worked out from
    /// it); this one only aims to need few explicit gotos.
    /// </summary>
    public sealed class Structurer(IReadOnlyList<IReadOnlyList<int>> targets)
    {
        /// <summary>Per state, the rules (by index) that open a branch, and the branch's state range.</summary>
        public Dictionary<int, List<(int Rule, int From, int To)>> Arms { get; } = [];
        /// <summary>The nesting: a state index, or a repeat of a nested list.</summary>
        public abstract record Node;
        public sealed record StateNode(int State) : Node;
        public sealed record RepeatNode(List<Node> Body) : Node;

        public List<Node> Run() => Seq(0, targets.Count, []);

        private int? LoopEnd(int i, int end)
        {
            int? last = null;
            for (int u = i + 1; u < end; u++) if (targets[u].Contains(i)) last = u;
            return last;
        }

        private List<Node> Seq(int i, int end, List<(int Head, int After)> loops)
        {
            var o = new List<Node>();
            while (i < end)
            {
                if (LoopEnd(i, end) is int l && !loops.Any(x => x.Head == i))
                {
                    o.Add(new RepeatNode(Seq(i, l + 1, [.. loops, (i, l + 1)])));
                    i = l + 1;
                    continue;
                }
                o.Add(new StateNode(i));
                i = State(i, end, loops);
            }
            return o;
        }

        private int State(int s, int end, List<(int Head, int After)> loops)
        {
            var ts = targets[s];
            if (!ts.Any(t => t > s + 1 && t < end)) return s + 1;
            var loopT = loops.SelectMany(x => new[] { x.Head, x.After }).ToHashSet();
            // the join: a state J such that the states between leave only to (s, J], stop, themselves or the enclosing
            // repeats; of those, the one that leaves the fewest transitions to write as gotos (then the nearest)
            (int Cost, int Join, List<int> Starts, List<int> Owners)? best = null;
            for (int cand = s + 2; cand <= end; cand++)
            {
                bool closed = true;
                for (int u = s + 1; u < cand && closed; u++)
                    foreach (int t in targets[u])
                        if (!(t == Stop || t == u || loopT.Contains(t) || s < t && t <= cand)) { closed = false; break; }
                if (!closed) continue;
                var starts = ts.Where(t => t > s && t < cand).Distinct().Order().ToList();
                if (starts.Count == 0 || starts[0] != s + 1) continue;
                // each branch belongs to the first rule that enters it; their order must be the branches' order
                var owners = starts.Select(a => ts.ToList().IndexOf(a)).ToList();
                bool ordered = true;
                for (int k = 1; k < owners.Count; k++) if (owners[k] < owners[k - 1]) ordered = false;
                if (!ordered) continue;
                int cost = 0;
                for (int k = 0; k < ts.Count; k++)
                    if (!(ts[k] == Stop || ts[k] == s || ts[k] == cand || loopT.Contains(ts[k]) || owners.Contains(k))) cost++;
                for (int k = 0; k < starts.Count; k++)
                {
                    int armEnd = k + 1 < starts.Count ? starts[k + 1] : cand;
                    for (int u = starts[k]; u < armEnd; u++)
                        foreach (int t in targets[u])
                            if (!(t == Stop || t == u || loopT.Contains(t) || t == (u + 1 < armEnd ? u + 1 : cand))) cost++;
                }
                if (best is null || cost < best.Value.Cost) best = (cost, cand, starts, owners);
            }
            if (best is not { } b) return s + 1;
            int join = b.Join;
            var list = new List<(int, int, int)>();
            for (int k = 0; k < b.Starts.Count; k++) list.Add((b.Owners[k], b.Starts[k], k + 1 < b.Starts.Count ? b.Starts[k + 1] : join));
            Arms[s] = list;
            foreach (var (_, from, to) in list) Branches[(s, from)] = Seq(from, to, loops);
            return join;
        }

        /// <summary>The nesting of each branch, by (state, first state).</summary>
        public Dictionary<(int State, int From), List<Node>> Branches { get; } = [];
    }
}
