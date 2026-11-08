using System.Security.Cryptography;
using System.Text;

namespace I76.Formats.Fsm;

/// <summary>
/// A mission's FSM chunk as a <see cref="MissionScriptSource"/>: every routine in the state-machine shape becomes steps (nested where the
/// layout allows it, with explicit gotos where it does not), anything else raw instructions; the tables keyed by their labels;
/// generic names (script3, thread7, g17, f0, v0, step4) unless a <see cref="ScriptNames"/> sidecar gives better ones.
/// </summary>
public static class MissionScriptDecompiler
{
    /// <summary>The FSM chunk's identity for a names sidecar: the first 16 hex digits of its SHA-256.</summary>
    public static string ChunkHash(byte[] payload) => Convert.ToHexString(SHA256.HashData(payload))[..16].ToLowerInvariant();

    /// <summary>A routine's identity across missions (the shared library routines): its instructions with jumps relative and operations by name.</summary>
    public static string RoutineHash(IReadOnlyList<FsmInstr> code, int from, int to, IReadOnlyList<string> protos)
    {
        var sb = new StringBuilder();
        for (int pc = from; pc < to; pc++)
        {
            var (op, a) = (code[pc].Op, code[pc].Arg);
            sb.Append(op).Append(' ');
            if (FsmOp.Branches(op)) sb.Append(a - from);
            else if (op == FsmOp.Exec && a >= 0 && a < protos.Count) sb.Append(protos[a]);
            else sb.Append(a);
            sb.Append(';');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..12].ToLowerInvariant();
    }

    public sealed class Result
    {
        public required MissionScriptSource Script { get; init; }
        public required FsmChunk Chunk { get; init; }
        public required List<FsmRoutine> Routines { get; init; }
        /// <summary>Each routine's body hash (<see cref="RoutineHash"/>), for names that follow a routine across missions.</summary>
        public required List<string> BodyHashes { get; init; }
        /// <summary>Notes on names the sidecar gave that could not be used.</summary>
        public List<string> Warnings { get; } = [];
    }

    public static Result Decompile(byte[] payload, string? mission = null, ScriptNames? names = null)
    {
        var chunk = FsmChunk.Read(payload);
        var d = new Builder(chunk, mission, names, ChunkHash(payload));
        return d.Run();
    }

    private sealed class Builder(FsmChunk c, string? mission, ScriptNames? names, string hash)
    {
        private readonly List<string> _warn = [];
        private List<FsmRoutine> _routines = [];
        private readonly List<string> _scriptNames = [];
        private readonly List<int> _starts = [];
        private readonly List<int> _ends = [];
        private readonly List<string> _hashes = [];
        private string[] _actorKeys = [], _pathKeys = [], _soundKeys = [];
        private readonly List<ScriptNames.ScriptEntry?> _notes = [];

        private bool MissionNotes => names is not null && (names.Fsm is null || names.Fsm == hash);

        public Result Run()
        {
            if (names?.Fsm is { } h && h != hash) _warn.Add($"the names were written for FSM chunk {h}, this one is {hash}: only names keyed by a script's hash apply");
            var doc = new MissionScriptSource { Mission = mission };
            if (!c.Prototypes.SequenceEqual(FsmVocabulary.Standard)) doc.Prototypes = [.. c.Prototypes];

            // ---- tables ----
            _actorKeys = Keys(c.Actors.Select(a => a.Label));
            for (int i = 0; i < c.Actors.Count; i++)
            {
                var a = c.Actors[i];
                var note = MissionNotes ? names!.Actors.GetValueOrDefault(_actorKeys[i]) : null;
                doc.Actors.Add(new ActorDef
                {
                    Key = _actorKeys[i], Label = a.Label, Id = a.Id, Object = ObjectName(a.Id),
                    Alias = note?.Alias, Description = note?.Description,
                });
            }
            var paths = c.Paths;
            _pathKeys = Keys(paths.Select(p => p.Name));
            for (int i = 0; i < paths.Count; i++)
                doc.Paths.Add(new PathDef
                {
                    Key = _pathKeys[i], Label = paths[i].Name, Points = paths[i].Points.Count,
                    Description = MissionNotes ? names!.Paths.GetValueOrDefault(_pathKeys[i])?.Description : null,
                });
            _soundKeys = Keys(c.Sounds.Select(s => Path.GetFileNameWithoutExtension(s) is { Length: > 0 } b ? b : s));
            for (int i = 0; i < c.Sounds.Count; i++)
                doc.Sounds.Add(new SoundDef
                {
                    Key = _soundKeys[i], File = c.Sounds[i],
                    Description = MissionNotes ? names!.Sounds.GetValueOrDefault(_soundKeys[i])?.Description : null,
                });

            // ---- routines ----
            var depthAt = new Dictionary<int, int>();
            foreach (var t in c.Threads) depthAt.TryAdd(t.StartPc, t.Cells.Length);
            var entries = c.Threads.Select(t => t.StartPc).Concat(c.Code.Where(i => i.Op == FsmOp.Call).Select(i => i.Arg));
            _routines = FsmRoutines.Split(c.Code, c.Prototypes, entries, pc => depthAt.TryGetValue(pc, out int d) ? d : null);
            int pcAt = 0;
            foreach (var r in _routines)
            {
                int size = FsmRoutines.Codegen.Emit(r, pcAt, c.Prototypes.Count, x => x).Count;
                _starts.Add(pcAt);
                _ends.Add(pcAt + size);
                pcAt += size;
            }
            var usedScripts = new HashSet<string>();
            for (int k = 0; k < _routines.Count; k++)
            {
                string generic = $"script{k}";
                _hashes.Add(RoutineHash(c.Code, _starts[k], _ends[k], c.Prototypes));
                var note = names?.ScriptNote(generic, _hashes[k], MissionNotes);
                _notes.Add(note);
                _scriptNames.Add(Pick(generic, note?.Name, usedScripts, "script"));
            }

            // ---- typing: what each field and variable is used as ----
            var fieldUse = new List<Dictionary<int, HashSet<FsmSlot>>>();
            var localUse = new List<Dictionary<int, HashSet<FsmSlot>>>();
            foreach (var r in _routines)
            {
                var fu = new Dictionary<int, HashSet<FsmSlot>>();
                var lu = new Dictionary<int, HashSet<FsmSlot>>();
                if (r.Asm is null)
                    foreach (var s in FsmRoutines.AllStmts(r))
                    {
                        if (s.IsCall) continue;
                        for (int i = 0; i < s.Args.Count; i++)
                        {
                            var slot = FsmVocabulary.SlotOf(c.Prototypes[s.Proto], i);
                            if (slot == FsmSlot.OutActor) slot = FsmSlot.Actor;
                            var a = s.Args[i];
                            if (a.Kind == FsmArgKind.Param) Add(fu, r.Fields + 1 - a.Value, slot);
                            else if (a.Kind == FsmArgKind.Local) Add(lu, a.Value - 1, slot);
                        }
                    }
                fieldUse.Add(fu);
                localUse.Add(lu);
            }
            var cellUse = new Dictionary<int, HashSet<FsmSlot>>();
            var threadScript = new List<int>();
            foreach (var t in c.Threads)
            {
                int k = _starts.IndexOf(t.StartPc);
                threadScript.Add(k);
                if (k < 0 || _routines[k].Asm is not null || _routines[k].Fields != t.Cells.Length) continue;
                foreach (var (j, set) in fieldUse[k])
                    if (j >= 0 && j < t.Cells.Length) foreach (var slot in set) Add(cellUse, t.Cells[j], slot);
            }

            // ---- globals ----
            var usedGlobals = new HashSet<string>();
            var globalNames = new List<string>();
            for (int i = 0; i < c.Data.Count; i++)
            {
                var note = MissionNotes ? names!.Globals.GetValueOrDefault($"g{i}") : null;
                string name = Pick($"g{i}", note?.Name, usedGlobals, "global");
                globalNames.Add(name);
                doc.Globals.Add(new GlobalDef { Name = name, Value = Typed(c.Data[i], cellUse.GetValueOrDefault(i)), Description = note?.Description });
            }

            // ---- scripts ----
            for (int k = 0; k < _routines.Count; k++)
                doc.Scripts.Add(Script(k, fieldUse[k], localUse[k]));

            // ---- threads ----
            var usedThreads = new HashSet<string>();
            for (int i = 0; i < c.Threads.Count; i++)
            {
                var t = c.Threads[i];
                var note = MissionNotes ? names!.Threads.GetValueOrDefault($"thread{i}") : null;
                var td = new ThreadDef { Name = Pick($"thread{i}", note?.Name, usedThreads, "thread"), Alias = note?.Alias, Description = note?.Description, Script = CodeRef(t.StartPc) };
                foreach (int cell in t.Cells)
                    if (cell < 0 || cell >= c.Data.Count) throw new ScriptError($"thread {i} names data cell {cell} of {c.Data.Count}");
                int k = threadScript[i];
                if (t.Spare.Length > 0) throw new ScriptError($"thread {i} has data past its parameters in its table entry");
                if (k >= 0 && _routines[k].Asm is null && _routines[k].Fields == t.Cells.Length)
                    td.Named = [.. doc.Scripts[k].Fields.Select((f, j) => (f.Name, globalNames[t.Cells[j]]))];
                else td.Positional = [.. t.Cells.Select(x => globalNames[x])];
                doc.Threads.Add(td);
            }
            var res = new Result { Script = doc, Chunk = c, Routines = _routines, BodyHashes = _hashes };
            res.Warnings.AddRange(_warn);
            return res;
        }

        private static void Add(Dictionary<int, HashSet<FsmSlot>> d, int k, FsmSlot s)
        {
            if (!d.TryGetValue(k, out var set)) d[k] = set = [];
            set.Add(s);
        }

        /// <summary>A value shown as a table entry when everything that reads it reads that table.</summary>
        private ScriptValue Typed(int v, IReadOnlyCollection<FsmSlot>? use)
        {
            if (use is not { Count: 1 }) return new ScriptValue(v);
            return Ref(use.First(), v) ?? new ScriptValue(v);
        }

        private ScriptValue? Ref(FsmSlot slot, int v) => slot switch
        {
            FsmSlot.Actor when v >= 0 && v < _actorKeys.Length => ScriptValue.Ref(RefKind.Actor, _actorKeys[v]),
            FsmSlot.Path when v >= 0 && v < _pathKeys.Length => ScriptValue.Ref(RefKind.Path, _pathKeys[v]),
            FsmSlot.Sound when v >= 0 && v < _soundKeys.Length => ScriptValue.Ref(RefKind.Sound, _soundKeys[v]),
            _ => null,
        };

        private string Pick(string generic, string? wanted, HashSet<string> used, string what)
        {
            if (wanted is { Length: > 0 } w && w != generic)
            {
                if (!ScriptText.IsIdentifier(w) || ScriptText.Reserved.Contains(w)) _warn.Add($"{what} {generic}: '{w}' is not a usable name");
                else if (!used.Add(w)) _warn.Add($"{what} {generic}: '{w}' is taken");
                else return w;
            }
            used.Add(generic);
            return generic;
        }

        /// <summary>An instruction index as a script name (plus an offset inside it).</summary>
        private string CodeRef(int pc)
        {
            for (int k = 0; k < _starts.Count; k++)
                if (pc >= _starts[k] && pc < _ends[k]) return pc == _starts[k] ? _scriptNames[k] : $"{_scriptNames[k]}+{pc - _starts[k]}";
            return "@" + pc;
        }

        private ScriptDef Script(int k, Dictionary<int, HashSet<FsmSlot>> fieldUse, Dictionary<int, HashSet<FsmSlot>> localUse)
        {
            var r = _routines[k];
            var note = _notes[k];
            var sd = new ScriptDef { Name = _scriptNames[k], Alias = note?.Alias, Description = note?.Description };
            if (r.Asm is { } asm)
            {
                sd.Asm = [.. asm.Select(Asm)];
                return sd;
            }
            var used = new HashSet<string>();
            var fields = new List<string>();
            for (int j = 0; j < r.Fields; j++)
            {
                var fn = note?.Fields.GetValueOrDefault($"f{j}");
                string n = Pick($"f{j}", fn?.Name, used, $"{sd.Name} field");
                fields.Add(n);
                sd.Fields.Add(new FieldDef { Name = n, Description = fn?.Description });
            }
            var vars = new List<string>();
            for (int j = 0; j < r.Locals.Count; j++)
            {
                var vn = note?.Variables.GetValueOrDefault($"v{j}");
                string n = Pick($"v{j}", vn?.Name, used, $"{sd.Name} variable");
                vars.Add(n);
                ScriptValue? init = r.Locals[j] is int v ? Typed(v, localUse.GetValueOrDefault(j)) : null;
                sd.Variables.Add(new VariableDef { Name = n, Init = init, Description = vn?.Description });
            }
            ScriptOperand Op(FsmArg a, string proto, int slot) => a.Kind switch
            {
                FsmArgKind.Param => new ScriptOperand.Name(fields[r.Fields + 1 - a.Value]),
                FsmArgKind.Local => new ScriptOperand.Name(vars[a.Value - 1]),
                FsmArgKind.LocalValue => new ScriptOperand.Deref(vars[a.Value - 1]),
                _ => new ScriptOperand.Literal(Ref(FsmVocabulary.SlotOf(proto, slot), a.Value) ?? new ScriptValue(a.Value)),
            };
            ScriptAction Act(FsmStmt s)
            {
                if (s.IsCall) return new ScriptAction("call", [.. s.Args.Select((a, i) => Op(a, "", i))], CodeRef(s.CallTarget));
                string p = c.Prototypes[s.Proto];
                return new ScriptAction(p, [.. s.Args.Select((a, i) => Op(a, p, i))]);
            }
            ScriptCond Cond(FsmExpr e) => e switch
            {
                FsmExpr.Term t => new ScriptCond.Test(Act(t.Op)),
                FsmExpr.Not n => new ScriptCond.Not(Cond(n.E)),
                FsmExpr.And a => new ScriptCond.And(Cond(a.L), Cond(a.R)),
                FsmExpr.Or o => new ScriptCond.Or(Cond(o.L), Cond(o.R)),
                _ => throw new InvalidOperationException(),
            };

            // guards: the longest run of rules every state starts with
            int g = 0;
            if (r.States.Count >= 2)
                while (r.States.All(s => s.Rules.Count > g) && r.States.All(s => s.Rules[g].Same(r.States[0].Rules[g]))) g++;
            // the states' targets without the guards, for the nesting
            var targets = r.States.Select(s => (IReadOnlyList<int>)s.Rules.Skip(g).Select(u => u.Exit.IsReturn ? ScriptLayout.Stop : u.Exit.Value).ToList()).ToList();
            var st = new ScriptLayout.Structurer(targets);
            var tree = st.Run();
            var steps = new ScriptStep[r.States.Count];
            var ruleOf = new Dictionary<(int, int), ScriptRule>();
            List<ScriptNode> Build(List<ScriptLayout.Structurer.Node> nodes)
            {
                var o = new List<ScriptNode>();
                foreach (var n in nodes)
                    switch (n)
                    {
                        case ScriptLayout.Structurer.RepeatNode rep: o.Add(new ScriptRepeat { Steps = Build(rep.Body) }); break;
                        case ScriptLayout.Structurer.StateNode sn:
                        {
                            var state = r.States[sn.State];
                            var step = new ScriptStep();
                            steps[sn.State] = step;
                            for (int u = g; u < state.Rules.Count; u++)
                            {
                                var ru = state.Rules[u];
                                var sr = new ScriptRule { When = Cond(ru.Cond), Then = [.. ru.Body.Select(Act)] };
                                ruleOf[(sn.State, u)] = sr;
                                step.Rules.Add(sr);
                            }
                            if (st.Arms.TryGetValue(sn.State, out var arms))
                                foreach (var (rule, from, _) in arms)
                                    step.Rules[rule].Steps = Build(st.Branches[(sn.State, from)]);
                            step.WhileWaiting = state.During.Count == 1 && !state.During[0].IsCall && c.Prototypes[state.During[0].Proto] == "null"
                                ? null : [.. state.During.Select(Act)];
                            step.Kind = step.Rules.Count == 1 && step.Rules[0].When.IsTrue && step.Rules[0].Steps is null ? StepKind.Do
                                : step.Rules.Count == 1 && step.Rules[0].Steps is null ? StepKind.WaitUntil : StepKind.Wait;
                            o.Add(step);
                            break;
                        }
                    }
                return o;
            }
            sd.Steps = Build(tree);
            var (flat, armStart) = ScriptLayout.Flatten(sd.Steps);
            for (int i = 0; i < flat.Count; i++)
                if (!ReferenceEquals(flat[i].Step, steps[i])) throw new InvalidOperationException($"{sd.Name}: the nesting does not keep the states' order");

            // step ids: the sidecar's, and generic ones where a goto needs one
            var ids = new string?[steps.Length];
            var usedIds = new HashSet<string>();
            for (int i = 0; i < steps.Length; i++)
            {
                var sn = note?.Steps.GetValueOrDefault($"step{i}");
                if (sn?.Name is { Length: > 0 }) ids[i] = Pick($"step{i}", sn.Name, usedIds, $"{sd.Name} step");
                steps[i].Description = sn?.Description;
            }
            string Id(int i)
            {
                if (ids[i] is null)
                {
                    string n = $"step{i}";
                    while (!usedIds.Add(n)) n += "_";
                    ids[i] = n;
                }
                return ids[i]!;
            }
            ScriptGoto Goto(FsmExit e, ScriptLayout.Flat at)
            {
                int t = e.IsReturn ? ScriptLayout.Stop : e.Value;
                if (t == at.Cont) return ScriptGoto.Default;
                if (t == ScriptLayout.Stop) return new(GotoKind.Stop);
                if (t == at.Index) return new(GotoKind.Restart);
                if (at.Loop is { } lp && t == lp.Head) return new(GotoKind.Continue);
                if (at.Loop is { } lp2 && t == lp2.After) return new(GotoKind.Break);
                return new(GotoKind.Step, Id(t));
            }
            for (int i = 0; i < flat.Count; i++)
                for (int u = g; u < r.States[i].Rules.Count; u++)
                {
                    var sr = ruleOf[(i, u)];
                    if (sr.Steps is not null)
                    {
                        if (armStart[sr] != r.States[i].Rules[u].Exit.Value || r.States[i].Rules[u].Exit.IsReturn)
                            throw new InvalidOperationException($"{sd.Name}: a branch does not start where its rule goes");
                        continue;
                    }
                    sr.Goto = Goto(r.States[i].Rules[u].Exit, flat[i]);
                }
            for (int u = 0; u < g; u++)
            {
                var ru = r.States[0].Rules[u];
                sd.Guards.Add(new ScriptRule
                {
                    When = Cond(ru.Cond), Then = [.. ru.Body.Select(Act)],
                    Goto = ru.Exit.IsReturn ? new(GotoKind.Stop) : new(GotoKind.Step, Id(ru.Exit.Value)),
                });
            }
            for (int i = 0; i < steps.Length; i++) steps[i].Id = ids[i];
            return sd;
        }

        private string Asm(FsmInstr i)
        {
            string m = i.Op is > 0 and < 15 ? FsmOp.Mnemonics[i.Op] : $"op{i.Op}";
            string a = FsmOp.Branches(i.Op) ? CodeRef(i.Arg)
                : i.Op == FsmOp.Exec && i.Arg >= 0 && i.Arg < c.Prototypes.Count && ScriptText.IsIdentifier(c.Prototypes[i.Arg]) ? c.Prototypes[i.Arg]
                : i.Op == FsmOp.Exec ? "#" + i.Arg
                : i.Arg.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return $"{m} {a}";
        }
    }

    /// <summary>Unique keys for table labels: the label (made a valid key), then _2, _3 ... for repeats.</summary>
    public static string[] Keys(IEnumerable<string> labels)
    {
        var used = new HashSet<string>();
        var o = new List<string>();
        foreach (var l in labels)
        {
            var b = new StringBuilder();
            foreach (var ch in l) b.Append(char.IsAsciiLetterOrDigit(ch) || ch is '_' or '.' or '-' ? ch : '_');
            string k = b.Length == 0 ? "_" : b.ToString();
            if (k[0] is '-' or '.') k = "_" + k;
            string key = k;
            for (int n = 2; !used.Add(key); n++) key = $"{k}_{n}";
            o.Add(key);
        }
        return [.. o];
    }

    /// <summary>The placement name an actor's 8 bytes hold, without the high bits.</summary>
    public static string ObjectName(byte[] id)
    {
        var b = id.Select(x => (byte)(x & 0x7f)).ToArray();
        return Bin.CStr(b);
    }
}
