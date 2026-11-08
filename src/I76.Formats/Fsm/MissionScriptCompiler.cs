using System.Globalization;
using I76.Formats.Mission.Edit;

namespace I76.Formats.Fsm;

/// <summary>
/// A <see cref="MissionScriptSource"/> back into an FSM chunk. The path table is the target mission's (paths are edited with the
/// path tool; the source only names them), so the source's paths must be the mission's labels in table order. Everything else
/// comes from the source. A decompiled script compiles to the bytes it was read from.
/// </summary>
public static class MissionScriptCompiler
{
    /// <summary>
    /// The chunk. <paramref name="into"/>: the target mission's current FSM payload (its path table is used; may be empty);
    /// <paramref name="placements"/>: the mission's placements' 8-byte names, for actors given by object name only.
    /// </summary>
    public static FsmChunk Compile(MissionScriptSource doc, byte[] into, IReadOnlyList<byte[]>? placements = null)
    {
        var target = into.Length == 0 ? new FsmChunk { WasEmpty = true } : FsmChunk.Read(into);
        return new Compiler(doc, target, placements ?? []).Run();
    }

    private sealed class Compiler(MissionScriptSource doc, FsmChunk target, IReadOnlyList<byte[]> placements)
    {
        private readonly List<string> _protos = doc.Prototypes is { } p ? [.. p] : [.. FsmVocabulary.Standard];
        private Dictionary<string, int> _actors = [], _paths = [], _sounds = [], _globals = [], _scripts = [];
        private readonly List<(int Script, int Offset, int Abs)> _refs = [];

        public FsmChunk Run()
        {
            var c = new FsmChunk { WasEmpty = target.WasEmpty, PathTable = target.PathTable };
            // tables
            _actors = Index(doc.Actors.Select(a => a.Key), "actor");
            foreach (var a in doc.Actors) c.Actors.Add(new FsmActorEntry(a.Label, a.Id ?? FindPlacement(a)));
            var mine = target.Paths;
            // a source without paths uses the mission's, keyed as the decompiler keys them
            var keys = doc.Paths.Count == 0 ? MissionScriptDecompiler.Keys(mine.Select(x => x.Name)) : [.. doc.Paths.Select(x => x.Key)];
            if (doc.Paths.Count > 0 && (mine.Count != doc.Paths.Count || !mine.Select(x => x.Name).SequenceEqual(doc.Paths.Select(x => x.Label))))
                throw new ScriptError($"the source names {doc.Paths.Count} paths ({Short(doc.Paths.Select(x => x.Label))}), the mission has {mine.Count} ({Short(mine.Select(x => x.Name))}): paths are edited with the path tool; decompile the mission again");
            _paths = Index(keys, "path");
            _sounds = Index(doc.Sounds.Select(x => x.Key), "sound");
            foreach (var s in doc.Sounds) c.Sounds.Add(s.File);
            _globals = Index(doc.Globals.Select(x => x.Name), "global");
            foreach (var g in doc.Globals) c.Data.Add(Value(g.Value, $"global {g.Name}"));
            _scripts = Index(doc.Scripts.Select(x => x.Name), "script");

            // routines: sizes first (a call's or a jump's target does not change its size), then the code
            var routines = doc.Scripts.Select(Routine).ToList();
            var bases = new List<int>();
            int pc = 0;
            foreach (var r in routines)
            {
                bases.Add(pc);
                pc += FsmRoutines.Codegen.Emit(r, pc, _protos.Count, _ => 0).Count;
            }
            int Resolve(int id)
            {
                var (s, off, abs) = _refs[id];
                return s < 0 ? abs : bases[s] + off;
            }
            for (int k = 0; k < routines.Count; k++)
            {
                var code = FsmRoutines.Codegen.Emit(routines[k], bases[k], _protos.Count, Resolve);
                if (bases[k] != c.Code.Count) throw new InvalidOperationException("routine size changed between passes");
                c.Code.AddRange(code);
            }

            // threads
            foreach (var t in doc.Threads)
            {
                int start = Resolve(CodeRef(t.Script, $"thread {t.Name}"));
                List<int> cells;
                if (t.Named is { } named)
                {
                    var (s, off, _) = _refs[^1];
                    if (s < 0 || off != 0) throw new ScriptError($"thread {t.Name}: fields by name need a script's start (use a list)");
                    var sd = doc.Scripts[s];
                    if (sd.Asm is not null) throw new ScriptError($"thread {t.Name}: script {sd.Name} is raw instructions; give its arguments as a list");
                    var byField = new Dictionary<string, string>();
                    foreach (var (f, g) in named)
                        if (!byField.TryAdd(f, g)) throw new ScriptError($"thread {t.Name}: field {f} twice");
                    foreach (var f in byField.Keys)
                        if (!sd.Fields.Any(x => x.Name == f)) throw new ScriptError($"thread {t.Name}: script {sd.Name} has no field {f}");
                    cells = [];
                    foreach (var f in sd.Fields)
                        cells.Add(byField.TryGetValue(f.Name, out var g) ? Global(g, $"thread {t.Name}") : throw new ScriptError($"thread {t.Name}: field {f.Name} of {sd.Name} is not bound"));
                }
                else cells = [.. (t.Positional ?? []).Select(g => Global(g, $"thread {t.Name}"))];
                if (cells.Count > FsmChunk.ThreadSlots) throw new ScriptError($"thread {t.Name}: {cells.Count} arguments (at most {FsmChunk.ThreadSlots})");
                c.Threads.Add(new FsmThreadEntry(start, [.. cells]));
            }
            c.Prototypes.AddRange(_protos);
            return c;
        }

        private static string Short(IEnumerable<string> s)
        {
            var l = s.ToList();
            return l.Count == 0 ? "none" : string.Join(", ", l.Take(6)) + (l.Count > 6 ? ", ..." : "");
        }

        private static Dictionary<string, int> Index(IEnumerable<string> keys, string what)
        {
            var d = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var k in keys)
                if (!d.TryAdd(k, d.Count)) throw new ScriptError($"two {what}s named {k}");
            return d;
        }

        private byte[] FindPlacement(ActorDef a)
        {
            if (a.Object is not { Length: > 0 } name) throw new ScriptError($"actor {a.Key}: neither id nor object");
            var hits = placements.Where(p => MissionScriptDecompiler.ObjectName(p).Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (hits.Count == 1) return hits[0];
            throw new ScriptError($"actor {a.Key}: {(hits.Count == 0 ? "no" : hits.Count.ToString(CultureInfo.InvariantCulture))} placements named {name} in the mission; give its id");
        }

        private int Value(ScriptValue v, string where)
        {
            if (v.Kind is not { } k) return v.Number;
            var table = k switch { RefKind.Actor => _actors, RefKind.Path => _paths, _ => _sounds };
            return table.TryGetValue(v.Key!, out int i) ? i : throw new ScriptError($"{where}: no {ScriptValue.RefPrefix(k)} {v.Key}");
        }

        private int Global(string name, string where) =>
            _globals.TryGetValue(name, out int i) ? i : throw new ScriptError($"{where}: no global {name}");

        /// <summary>A code reference: a script's name, name+N (an instruction inside it), or @N (an instruction index).</summary>
        private int CodeRef(string s, string where)
        {
            s = s.Trim();
            if (s.StartsWith('@') && int.TryParse(s[1..], NumberStyles.None, CultureInfo.InvariantCulture, out int abs))
            {
                _refs.Add((-1, 0, abs));
                return _refs.Count - 1;
            }
            int plus = s.IndexOf('+');
            string name = plus < 0 ? s : s[..plus].Trim();
            int off = 0;
            if (plus >= 0 && !int.TryParse(s[(plus + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out off)) throw new ScriptError($"{where}: '{s}' is not script or script+N");
            if (!_scripts.TryGetValue(name, out int k)) throw new ScriptError($"{where}: no script {name}");
            _refs.Add((k, off, 0));
            return _refs.Count - 1;
        }

        private int Proto(string name, string where)
        {
            int i = _protos.IndexOf(name);
            if (i < 0) i = _protos.FindIndex(p => p.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) return i;
            if (FsmVocabulary.Canonical(name) is not { } known) throw new ScriptError($"{where}: no operation {name}");
            _protos.Add(known);
            return _protos.Count - 1;
        }

        private FsmRoutine Routine(ScriptDef sd)
        {
            string where = $"script {sd.Name}";
            if (sd.Asm is { } asm)
                return new FsmRoutine { Asm = [.. asm.Select((line, i) => AsmLine(line, $"{where} instruction {i}"))] };
            var names = new Dictionary<string, (bool Field, int Index)>();
            for (int j = 0; j < sd.Fields.Count; j++)
                if (!names.TryAdd(Ident(sd.Fields[j].Name, where), (true, j))) throw new ScriptError($"{where}: {sd.Fields[j].Name} twice");
            for (int j = 0; j < sd.Variables.Count; j++)
                if (!names.TryAdd(Ident(sd.Variables[j].Name, where), (false, j))) throw new ScriptError($"{where}: {sd.Variables[j].Name} twice");
            int F = sd.Fields.Count;
            if (sd.Steps.Count == 0) throw new ScriptError($"{where}: no steps");
            var (flat, arms) = ScriptLayout.Flatten(sd.Steps);
            var ids = new Dictionary<string, int>();
            foreach (var f in flat)
                if (f.Step.Id is { } id && !ids.TryAdd(Ident(id, where), f.Index)) throw new ScriptError($"{where}: two steps with id {id}");

            FsmArg Arg(ScriptOperand o, string at) => o switch
            {
                ScriptOperand.Name n when names.TryGetValue(n.Id, out var x) => x.Field ? FsmArg.Param(F + 1 - x.Index) : FsmArg.Local(x.Index + 1),
                ScriptOperand.Name n => throw new ScriptError($"{at}: {n.Id} is no field or variable of {sd.Name}"),
                ScriptOperand.Deref d when names.TryGetValue(d.Id, out var x) && !x.Field => FsmArg.LocalValue(x.Index + 1),
                ScriptOperand.Deref d => throw new ScriptError($"{at}: *{d.Id}: * takes a variable"),
                ScriptOperand.Literal l => FsmArg.Imm(Value(l.Value, at)),
                _ => throw new InvalidOperationException(),
            };
            FsmStmt Stmt(ScriptAction a, string at)
            {
                if (a.Call is { } call) return new FsmStmt(-1, [.. a.Args.Select(x => Arg(x, at))], CodeRef(call, at));
                return new FsmStmt(Proto(a.Op, at), [.. a.Args.Select(x => Arg(x, at))]);
            }
            FsmExpr Cond(ScriptCond e, string at) => e switch
            {
                ScriptCond.Test t when t.Op.IsCall => throw new ScriptError($"{at}: a call is not a condition"),
                ScriptCond.Test t => FsmRoutines.Predicates.Contains(t.Op.Op) || !FsmVocabulary.IsKnown(t.Op.Op) && _protos.Contains(t.Op.Op)
                    ? new FsmExpr.Term(Stmt(t.Op, at)) : throw new ScriptError($"{at}: {t.Op.Op} is not a test (it sets no condition)"),
                ScriptCond.Not n => new FsmExpr.Not(Cond(n.E, at)),
                ScriptCond.And x => new FsmExpr.And(Cond(x.L, at), Cond(x.R, at)),
                ScriptCond.Or x => new FsmExpr.Or(Cond(x.L, at), Cond(x.R, at)),
                _ => throw new InvalidOperationException(),
            };
            FsmExit Target(ScriptRule r, ScriptLayout.Flat? at, string w)
            {
                if (r.Steps is not null)
                {
                    if (r.Goto.Kind != GotoKind.Default) throw new ScriptError($"{w}: a rule with steps of its own has no goto");
                    return FsmExit.To(arms[r]);
                }
                int t = r.Goto.Kind switch
                {
                    GotoKind.Default when at is not null => at.Cont,
                    GotoKind.Stop => ScriptLayout.Stop,
                    GotoKind.Restart when at is not null => at.Index,
                    GotoKind.Next when at is not null => at.Next ?? throw new ScriptError($"{w}: goto next in the last step of a list"),
                    GotoKind.Continue when at?.Loop is { } lp => lp.Head,
                    GotoKind.Break when at?.Loop is { } lp => lp.After,
                    GotoKind.Step => ids.TryGetValue(r.Goto.Step!, out int s) ? s : throw new ScriptError($"{w}: no step {r.Goto.Step}"),
                    _ => throw new ScriptError(at is null ? $"{w}: a guard's goto is stop or a step id" : $"{w}: goto {r.Goto} outside a repeat"),
                };
                return t == ScriptLayout.Stop ? FsmExit.Return(F) : FsmExit.To(t);
            }
            FsmRule Rule(ScriptRule r, ScriptLayout.Flat? at, string w)
            {
                if (at is not null && r.Steps is not null && at.Step.Kind != StepKind.Wait) throw new ScriptError($"{w}: only the rules of a wait have steps of their own");
                return new FsmRule
                {
                    Cond = Cond(r.When, w), Body = [.. r.Then.Select((a, i) => Stmt(a, $"{w} action {i}"))], Exit = Target(r, at, w),
                };
            }
            var guards = sd.Guards.Select((g, i) => (g, i)).ToList();
            foreach (var (g, i) in guards) if (g.Steps is not null) throw new ScriptError($"{where} guard {i}: a guard has no steps");
            var states = new List<FsmState>();
            foreach (var f in flat)
            {
                string w = $"{where} step {f.Step.Id ?? "#" + f.Index}";
                var st = new FsmState();
                foreach (var (g, i) in guards) st.Rules.Add(Rule(g, null, $"{where} guard {i}"));
                if (f.Step.Kind is StepKind.Do or StepKind.WaitUntil && f.Step.Rules.Count != 1) throw new ScriptError($"{w}: a {f.Step.Kind} step has one rule");
                for (int i = 0; i < f.Step.Rules.Count; i++) st.Rules.Add(Rule(f.Step.Rules[i], f, f.Step.Kind == StepKind.Wait ? $"{w} rule {i}" : w));
                if (f.Step.WhileWaiting is { } ww) st.During.AddRange(ww.Select((a, i) => Stmt(a, $"{w} while_waiting {i}")));
                else st.During.Add(new FsmStmt(Proto("null", w), []));
                states.Add(st);
            }
            return new FsmRoutine
            {
                Fields = F, States = states,
                Locals = [.. sd.Variables.Select(v => v.Init is { } i ? Value(i, $"{where} variable {v.Name}") : (int?)null)],
            };
        }

        private static string Ident(string s, string where) =>
            ScriptText.IsIdentifier(s) && !ScriptText.Reserved.Contains(s) ? s : throw new ScriptError($"{where}: '{s}' is not a usable name");

        private FsmInstr AsmLine(string line, string where)
        {
            var parts = line.Trim().Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) throw new ScriptError($"{where}: empty");
            int op = Array.FindIndex(FsmOp.Mnemonics, m => m.Length > 0 && m.Equals(parts[0], StringComparison.OrdinalIgnoreCase));
            if (op < 0 && parts[0].StartsWith("op", StringComparison.Ordinal) && int.TryParse(parts[0][2..], NumberStyles.None, CultureInfo.InvariantCulture, out int raw)) op = raw;
            if (op < 0) throw new ScriptError($"{where}: no instruction {parts[0]}");
            string a = parts.Length > 1 ? parts[1].Trim() : "0";
            if (FsmOp.Branches(op)) return new FsmInstr(op, CodeRef(a, where));
            if (op == FsmOp.Exec)
            {
                if (a.StartsWith('#') && int.TryParse(a[1..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int n)) return new FsmInstr(op, n);
                return new FsmInstr(op, Proto(a, where));
            }
            return int.TryParse(a, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int v) ? new FsmInstr(op, v) : throw new ScriptError($"{where}: '{a}' is not a number");
        }
    }
}
