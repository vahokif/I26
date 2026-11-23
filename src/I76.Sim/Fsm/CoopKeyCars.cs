using I76.Formats.Fsm;

namespace I76.Sim.Fsm;

/// <summary>
/// The cars a mission is about, which Co-op Scaling never copies (docs/multiplayer.md "Co-op Scaling"), read once from the
/// mission's script in its source form (<see cref="MissionScriptDecompiler"/>): each thread's fields bound to the actors and
/// data cells its globals name, called scripts followed with their arguments. An actor (not a human) is a key car when
/// <list type="bullet">
/// <item>the script decides an objective or the mission on it alone: a rule whose actions (or its branch's steps) include
/// success, fail, successAll, failAll or failAllObj has a condition that names it and no other actor, directly or through a flag
/// (a cell a rule on it alone sets);</item>
/// <item>or the script films it: a camera operation names it (a cut-scene about that car).</item>
/// </list>
/// Everything else the script orders is plain opposition. A car whose death only counts towards a group (a counter, or a rule
/// on several cars at once) is plain too: the group is the goal, and copies are left out of the script's view as before.
/// </summary>
public static class CoopKeyCars
{
    /// <summary>Why an actor is a key car.</summary>
    [Flags]
    public enum Why { None = 0, Objective = 1, Filmed = 2 }

    /// <summary>What the script makes of its actors (by their index in the actor table): the ones it orders, and the key cars with why.</summary>
    public sealed record Analysis(IReadOnlySet<int> Ordered, IReadOnlyDictionary<int, Why> Key)
    {
        public static readonly Analysis None = new(new HashSet<int>(), new Dictionary<int, Why>());
        public bool IsKey(int actor) => Key.ContainsKey(actor);
    }

    private static readonly HashSet<string> Outcomes = new(StringComparer.OrdinalIgnoreCase) { "success", "fail", "successAll", "failAll", "failAllObj" };
    /// <summary>The AI orders (what Co-op Scaling copies a car on).</summary>
    private static readonly HashSet<string> Orders = new(StringComparer.OrdinalIgnoreCase) { "attack", "sit", "hide", "guard", "goto", "evade", "race", "follow" };
    private static readonly HashSet<string> Cameras = new(StringComparer.OrdinalIgnoreCase) { "camObjObj", "camObjDir", "camPosObj", "camTransObj", "camF12" };

    /// <summary>The key cars of a decompiled script; <paramref name="isPlayer"/> tells the human group's actors (never counted as a subject).</summary>
    public static Analysis Analyze(MissionScriptSource s, Func<int, bool> isPlayer)
    {
        var w = new Walker(s, isPlayer);
        w.Run();
        return new Analysis(w.Ordered, w.Result);
    }

    /// <summary>The key cars of a mission's FSM chunk; none when it cannot be read.</summary>
    public static Analysis Analyze(byte[] fsmPayload, Func<int, bool> isPlayer)
    {
        if (fsmPayload.Length == 0) return Analysis.None;
        try { return Analyze(MissionScriptDecompiler.Decompile(fsmPayload).Script, isPlayer); }
        catch (Exception e) when (e is InvalidDataException or ScriptError or IndexOutOfRangeException or ArgumentException or InvalidOperationException) { return Analysis.None; }
    }

    /// <summary>What a name stands for once its thread is bound: an actor (its table index), a data cell (a global, by name), or neither.</summary>
    private readonly record struct Bound(int Actor, string? Global)
    {
        public static readonly Bound Nothing = new(-1, null);
    }

    private sealed class Walker(MissionScriptSource s, Func<int, bool> isPlayer)
    {
        public readonly Dictionary<int, Why> Result = [];
        public readonly HashSet<int> Ordered = [];
        private readonly Dictionary<string, ScriptDef> _scripts = s.Scripts.ToDictionary(x => x.Name, StringComparer.Ordinal);
        private readonly Dictionary<string, int> _actors = s.Actors.Select((a, i) => (a.Key, i)).ToDictionary(x => x.Key, x => x.i, StringComparer.Ordinal);
        private readonly Dictionary<string, GlobalDef> _globals = s.Globals.ToDictionary(g => g.Name, StringComparer.Ordinal);
        /// <summary>The flags: a global a rule on actors sets, with every actor of every such rule.</summary>
        private readonly Dictionary<string, HashSet<int>> _flagOf = new(StringComparer.Ordinal);
        /// <summary>The deciding rules' subjects: the actors their conditions name, and the globals they test.</summary>
        private readonly List<(HashSet<int> Actors, HashSet<string> Globals)> _decisive = [];
        private readonly HashSet<string> _seen = [];

        public void Run()
        {
            foreach (var t in s.Threads)
            {
                if (!_scripts.TryGetValue(t.Script, out var def)) continue;
                var bind = new Dictionary<string, Bound>(StringComparer.Ordinal);
                if (t.Named is { } named)
                    foreach (var (f, g) in named) bind[f] = OfGlobal(g);
                else if (t.Positional is { } pos)
                    for (int i = 0; i < pos.Count && i < def.Fields.Count; i++) bind[def.Fields[i].Name] = OfGlobal(pos[i]);
                Script(def, bind);
            }
            // a deciding rule on one actor, named or behind a flag only it sets
            foreach (var (actors, globals) in _decisive)
            {
                var subjects = new HashSet<int>(actors);
                foreach (var g in globals) if (_flagOf.TryGetValue(g, out var by)) subjects.UnionWith(by);
                if (subjects.Count == 1) Mark(subjects.First(), Why.Objective);
            }
        }

        private Bound OfGlobal(string name) =>
            !_globals.TryGetValue(name, out var g) ? Bound.Nothing
            : g.Value is { Kind: RefKind.Actor, Key: { } k } ? (_actors.TryGetValue(k, out int a) ? new Bound(a, name) : Bound.Nothing)
            : g.Value.IsRef ? Bound.Nothing : new Bound(-1, name);

        private void Mark(int actor, Why why) => Result[actor] = Result.GetValueOrDefault(actor) | why;

        private void Script(ScriptDef def, Dictionary<string, Bound> bind)
        {
            string key = def.Name + "(" + string.Join(",", def.Fields.Select(f => bind.TryGetValue(f.Name, out var b) ? $"{b.Actor}:{b.Global}" : "-")) + ")";
            if (!_seen.Add(key)) return;
            Bound Resolve(ScriptOperand o) => o switch
            {
                ScriptOperand.Name n when bind.TryGetValue(n.Id, out var b) => b,
                ScriptOperand.Name n when def.Variables.FirstOrDefault(v => v.Name == n.Id) is { } v =>
                    v.Init is { Kind: RefKind.Actor, Key: { } k } && _actors.TryGetValue(k, out int a) ? new Bound(a, null) : Bound.Nothing,
                ScriptOperand.Name n => OfGlobal(n.Id),
                ScriptOperand.Literal { Value: { Kind: RefKind.Actor, Key: { } k } } when _actors.TryGetValue(k, out int a) => new Bound(a, null),
                _ => Bound.Nothing,
            };
            foreach (var r in def.Guards) Rule(r, Resolve);
            Nodes(def.Steps, Resolve);
        }

        private void Nodes(List<ScriptNode> nodes, Func<ScriptOperand, Bound> resolve)
        {
            foreach (var n in nodes)
            {
                if (n is ScriptRepeat rep) { Nodes(rep.Steps, resolve); continue; }
                if (n is not ScriptStep st) continue;
                foreach (var r in st.Rules) Rule(r, resolve);
                if (st.WhileWaiting is { } ww) foreach (var a in ww) Action(a, resolve);
            }
        }

        private void Rule(ScriptRule r, Func<ScriptOperand, Bound> resolve)
        {
            var tested = new List<Bound>();
            Collect(r.When, tested, resolve);
            var actors = tested.Where(b => b.Actor >= 0 && !isPlayer(b.Actor)).Select(b => b.Actor).ToHashSet();
            var actions = new List<ScriptAction>(r.Then);
            if (r.Steps is { } steps) Flatten(steps, actions);
            if (actions.Any(a => !a.IsCall && Outcomes.Contains(a.Op)))
                _decisive.Add((actors, tested.Where(b => b.Actor < 0 && b.Global is not null).Select(b => b.Global!).ToHashSet(StringComparer.Ordinal)));
            if (actors.Count > 0)
                foreach (var a in r.Then)
                    if (!a.IsCall && a.Op.Equals("set", StringComparison.OrdinalIgnoreCase) && a.Args.Count > 0 && resolve(a.Args[0]) is { Actor: < 0, Global: { } g })
                    {
                        if (!_flagOf.TryGetValue(g, out var by)) _flagOf[g] = by = [];
                        by.UnionWith(actors);
                    }
            foreach (var a in r.Then) Action(a, resolve);
            if (r.Steps is { } inner) Nodes(inner, resolve);
        }

        /// <summary>Every action of a branch's steps (what firing the rule leads to, the gotos not followed).</summary>
        private static void Flatten(List<ScriptNode> nodes, List<ScriptAction> into)
        {
            foreach (var n in nodes)
            {
                if (n is ScriptRepeat rep) { Flatten(rep.Steps, into); continue; }
                if (n is not ScriptStep st) continue;
                foreach (var r in st.Rules)
                {
                    into.AddRange(r.Then);
                    if (r.Steps is { } inner) Flatten(inner, into);
                }
            }
        }

        private static void Collect(ScriptCond c, List<Bound> into, Func<ScriptOperand, Bound> resolve)
        {
            switch (c)
            {
                case ScriptCond.Test t: foreach (var a in t.Op.Args) into.Add(resolve(a)); break;
                case ScriptCond.Not n: Collect(n.E, into, resolve); break;
                case ScriptCond.And x: Collect(x.L, into, resolve); Collect(x.R, into, resolve); break;
                case ScriptCond.Or x: Collect(x.L, into, resolve); Collect(x.R, into, resolve); break;
            }
        }

        private void Action(ScriptAction a, Func<ScriptOperand, Bound> resolve)
        {
            if (a.IsCall)
            {
                if (!_scripts.TryGetValue(a.Call!, out var callee)) return;
                var inner = new Dictionary<string, Bound>(StringComparer.Ordinal);
                for (int i = 0; i < a.Args.Count && i < callee.Fields.Count; i++) inner[callee.Fields[i].Name] = resolve(a.Args[i]);
                Script(callee, inner);
                return;
            }
            if (Orders.Contains(a.Op) && a.Args.Count > 0 && resolve(a.Args[0]) is { Actor: >= 0 } o) Ordered.Add(o.Actor);
            if (Cameras.Contains(a.Op))
                for (int i = 0; i < a.Args.Count; i++)
                    if (FsmVocabulary.SlotOf(a.Op, i) == FsmSlot.Actor && resolve(a.Args[i]) is { Actor: >= 0 } c && !isPlayer(c.Actor)) Mark(c.Actor, Why.Filmed);
        }
    }
}
