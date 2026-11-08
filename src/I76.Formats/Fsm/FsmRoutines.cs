namespace I76.Formats.Fsm;

/// <summary>
/// Reads a program's code as routines in the state-machine shape every stock script has, and writes routines back as
/// instructions. The shape (docs/mission-scripts.md):
/// <code>
///   routine  := prologue 'jump @S0' state+                 S0 = the next instruction
///   state    := rule* during 'yield @state'
///   rule     := cond 'branchIfFalse @nextRule' stmt* ('jump @state' | 'return n')
///   during   := stmt*                                      one statement in stock scripts ('exec null' when nothing)
///   cond     := A && B : A; branchIfFalse L; B; L:
///               A || B : A; branchIfFalse +2; jump L; B; L:
///               !A     : A; not
///   stmt     := args 'exec proto' ['freeLocals k']  |  pushes 'call @routine'
/// </code>
/// <see cref="Split"/> cuts the code into routines; a routine that does not parse, or whose canonical recompilation is not
/// its own bytes, is kept as raw instructions, so splitting and writing back is always the identity.
/// </summary>
public static class FsmRoutines
{
    /// <summary>The operations that set the condition register (the only ones a condition may hold).</summary>
    public static readonly HashSet<string> Predicates = new(StringComparer.OrdinalIgnoreCase)
    {
        "true", "false", "isEqual", "isGreater", "isLesser", "test", "isWithin", "isShot", "hpLesser", "ammoLesser", "isRammed", "isDead",
        "isLit", "timeGreater", "timeLesser", "isArrived", "isEqualId", "isWithinNav", "isWithinSqNav", "isWithinEnemy", "isAttacked",
        "camIsArrived", "isKeypress", "allBlgDead", "allEnemyDead", "isAtFollow", "controlDone", "isCBEmpty", "canSee", "isAirborne",
        "isGroovesFault",
    };

    private sealed class ParseError(string m) : Exception(m);

    /// <summary>
    /// The routines of a program, in code order, each with the instruction it starts at. <paramref name="entries"/> are the
    /// instructions something starts (threads, calls): a routine that fails to parse is kept raw up to the next of them or the
    /// next routine start.
    /// </summary>
    public static List<FsmRoutine> Split(IReadOnlyList<FsmInstr> code, IReadOnlyList<string> protos, IEnumerable<int> entries, Func<int, int?> fieldsOf)
    {
        var starts = new SortedSet<int>(entries.Where(e => e >= 0 && e < code.Count));
        var o = new List<FsmRoutine>();
        int pc = 0;
        while (pc < code.Count)
        {
            FsmRoutine? r = null;
            int end;
            try
            {
                var p = new Parser(code, protos, starts);
                (r, end) = p.Routine(pc, fieldsOf(pc));
                // the routine's bytes must be its canonical compilation, or it is kept raw
                var again = Codegen.Emit(r, pc, protos.Count, x => x);
                if (again.Count != end - pc || !again.SequenceEqual(code.Skip(pc).Take(end - pc))) r = null;
            }
            catch (ParseError)
            {
                r = null;
                end = pc;
            }
            if (r is null)
            {
                // raw up to the next entry or routine start
                int stop = code.Count;
                foreach (var e in starts) if (e > pc) { stop = e; break; }
                for (int q = Math.Max(end, pc + 1); q < stop; q++) if (IsRoutineStart(code, q)) { stop = q; break; }
                if (end > pc && end < stop) stop = end;
                o.Add(new FsmRoutine { Pc = pc, Asm = [.. code.Skip(pc).Take(stop - pc)] });
                pc = stop;
                continue;
            }
            o.Add(r);
            pc = end;
        }
        return o;
    }

    /// <summary>A prologue at <paramref name="pc"/>: (allocLocals | pushImm)* then a jump to the next instruction.</summary>
    public static bool IsRoutineStart(IReadOnlyList<FsmInstr> code, int pc)
    {
        while (pc < code.Count && code[pc].Op is FsmOp.Alloc or FsmOp.PushImm) pc++;
        return pc < code.Count && code[pc].Op == FsmOp.Jump && code[pc].Arg == pc + 1;
    }

    private sealed class Parser(IReadOnlyList<FsmInstr> code, IReadOnlyList<string> protos, SortedSet<int> entries)
    {
        private int _nloc;

        private (int Op, int Arg) At(int pc) => pc >= 0 && pc < code.Count ? (code[pc].Op, code[pc].Arg) : (0, 0);

        public (FsmRoutine, int End) Routine(int pc, int? fields)
        {
            int start = pc;
            var locals = new List<int?>();
            while (At(pc).Op is FsmOp.Alloc or FsmOp.PushImm)
            {
                var (op, x) = At(pc);
                if (op == FsmOp.Alloc)
                {
                    if (x <= 0 || x > 1024) throw new ParseError($"allocLocals {x}");
                    for (int k = 0; k < x; k++) locals.Add(null);
                }
                else locals.Add(x);
                pc++;
            }
            _nloc = locals.Count;
            if (At(pc) != (FsmOp.Jump, pc + 1)) throw new ParseError($"routine {start}: no jump to its first state");
            pc++;
            var statePcs = new List<int>();
            var raw = new List<(FsmState State, List<int> Targets)>();
            while (true)
            {
                statePcs.Add(pc);
                var (st, targets, end) = State(pc);
                raw.Add((st, targets));
                pc = end;
                if (pc >= code.Count || IsRoutineStart(code, pc) || entries.Contains(pc)) break;
            }
            // exits: a state of this routine by index
            var index = new Dictionary<int, int>();
            for (int i = 0; i < statePcs.Count; i++) index[statePcs[i]] = i;
            foreach (var (st, targets) in raw)
                for (int k = 0; k < st.Rules.Count; k++)
                    if (!st.Rules[k].Exit.IsReturn)
                        st.Rules[k].Exit = index.TryGetValue(targets[k], out int s) ? FsmExit.To(s) : throw new ParseError($"a rule jumps to {targets[k]}, not a state of its routine");
            var r = new FsmRoutine { Pc = start, Locals = locals, States = [.. raw.Select(x => x.State)] };
            r.Fields = fields ?? InferFields(r);
            Validate(r);
            return (r, pc);
        }

        /// <summary>Without a thread or call to say: what its returns pop, else the farthest parameter it reads.</summary>
        private static int InferFields(FsmRoutine r)
        {
            var rets = r.States.SelectMany(s => s.Rules).Where(u => u.Exit.IsReturn).Select(u => u.Exit.Value).Distinct().ToList();
            if (rets.Count == 1) return rets[0];
            int far = 1;
            foreach (var s in AllStmts(r)) foreach (var a in s.Args) if (a.Kind == FsmArgKind.Param) far = Math.Max(far, a.Value);
            return far - 1;
        }

        /// <summary>Only what the source form can say: parameters inside the routine's, locals it has, returns of its parameters.</summary>
        private static void Validate(FsmRoutine r)
        {
            foreach (var s in AllStmts(r))
                foreach (var a in s.Args)
                {
                    bool ok = a.Kind switch
                    {
                        FsmArgKind.Param => a.Value >= 2 && a.Value <= r.Fields + 1,
                        FsmArgKind.Local or FsmArgKind.LocalValue => a.Value >= 1 && a.Value <= r.Locals.Count,
                        _ => true,
                    };
                    if (!ok) throw new ParseError($"argument {a} outside the routine's frame");
                }
            foreach (var u in r.States.SelectMany(s => s.Rules))
                if (u.Exit.IsReturn && u.Exit.Value != r.Fields) throw new ParseError($"return {u.Exit.Value} in a routine of {r.Fields} parameters");
        }

        private (FsmState, List<int> Targets, int End) State(int pc)
        {
            int start = pc;
            var st = new FsmState();
            var targets = new List<int>();
            while (Rule(pc) is { } ru)
            {
                st.Rules.Add(ru.Rule);
                targets.Add(ru.Target);
                pc = ru.End;
            }
            while (Stmt(pc) is { } s)
            {
                st.During.Add(s.Stmt);
                pc = s.End;
            }
            if (At(pc) != (FsmOp.Yield, start)) throw new ParseError($"state {start}: no yield to itself at {pc}");
            return (st, targets, pc + 1);
        }

        private (FsmStmt Stmt, int End)? Stmt(int pc)
        {
            int start = pc;
            var args = new List<FsmArg>();
            var pushed = new List<FsmArg>();
            var temps = new Dictionary<int, int>();
            int sp = _nloc + 1;
            while (At(pc).Op is >= FsmOp.PushImm and <= FsmOp.ArgLocal)
            {
                var (o, x) = At(pc);
                switch (o)
                {
                    case FsmOp.PushImm: pushed.Add(FsmArg.Imm(x)); temps[sp++] = x; break;
                    case FsmOp.ArgLocalAddr: args.Add(temps.TryGetValue(x, out int v) ? FsmArg.Imm(v) : FsmArg.Local(x)); break;
                    case FsmOp.ArgLocal: args.Add(x < 0 ? FsmArg.Param(-x) : FsmArg.LocalValue(x)); break;
                    case FsmOp.PushLocal: pushed.Add(x < 0 ? FsmArg.Param(-x) : FsmArg.LocalValue(x)); sp++; break;
                    default: pushed.Add(FsmArg.Local(x)); sp++; break;
                }
                pc++;
            }
            var (op, a) = At(pc);
            if (op == FsmOp.Exec)
            {
                if (a < 0 || a >= protos.Count) throw new ParseError($"exec {a}: no such prototype");
                pc++;
                if (At(pc).Op == FsmOp.Free) pc++;
                return (new FsmStmt(a, args), pc);
            }
            if (op == FsmOp.Call) return (new FsmStmt(-1, pushed, a), pc + 1);
            if (pc != start) throw new ParseError($"argument pushes at {start} not followed by an operation");
            return null;
        }

        private bool IsOrBranch(int pc)
        {
            var (o, x) = At(pc);
            var (o2, x2) = At(pc + 1);
            return o == FsmOp.BranchIfFalse && x == pc + 2 && o2 == FsmOp.Jump && At(x2).Op is FsmOp.BranchIfFalse or FsmOp.Not;
        }

        private enum Item { Term, Not, And, Or }

        /// <summary>The condition's items up to the rule's branch, or null when no rule starts at <paramref name="pc"/>.</summary>
        private (List<(Item Kind, int Pc, int Target, FsmStmt? Term)> Items, int Branch)? CondItems(int pc)
        {
            var items = new List<(Item, int, int, FsmStmt?)>();
            while (true)
            {
                var (o, x) = At(pc);
                if (o == FsmOp.Not) { items.Add((Item.Not, pc, 0, null)); pc++; continue; }
                if (o == FsmOp.BranchIfFalse)
                {
                    if (IsOrBranch(pc)) { items.Add((Item.Or, pc, At(pc + 1).Arg, null)); pc += 2; continue; }
                    if (At(x).Op is FsmOp.BranchIfFalse or FsmOp.Not) { items.Add((Item.And, pc, x, null)); pc++; continue; }
                    return (items, pc);
                }
                (FsmStmt Stmt, int End)? s;
                try { s = Stmt(pc); }
                catch (ParseError) { return null; }
                if (s is not { } t || t.Stmt.IsCall || !Predicates.Contains(protos[t.Stmt.Proto])) return null;
                items.Add((Item.Term, pc, 0, t.Stmt));
                pc = t.End;
            }
        }

        private (FsmExpr, int Next) Expr(List<(Item Kind, int Pc, int Target, FsmStmt? Term)> items, int i, int end)
        {
            int AtItem(int k) => k < items.Count ? items[k].Pc : end;
            if (items[i].Kind != Item.Term) throw new ParseError($"a condition at {items[i].Pc} starts with {items[i].Kind}");
            FsmExpr left = new FsmExpr.Term(items[i].Term!);
            i++;
            while (i < items.Count && AtItem(i) < end)
            {
                var (kind, pc, tgt, _) = items[i];
                if (kind == Item.Not) { left = new FsmExpr.Not(left); i++; continue; }
                if (kind == Item.Term) throw new ParseError($"two terms in a row at {pc}");
                if (tgt > end) throw new ParseError($"{kind} at {pc} jumps to {tgt}, past {end}");
                int j = i + 1, k = j;
                while (k < items.Count && items[k].Pc < tgt) k++;
                if (j >= items.Count || items[j].Pc >= tgt) throw new ParseError($"{kind} at {pc} has no right side");
                var (right, _) = Expr(items, j, tgt);
                left = kind == Item.And ? new FsmExpr.And(left, right) : new FsmExpr.Or(left, right);
                i = k;
            }
            return (left, i);
        }

        private (FsmRule Rule, int Target, int End)? Rule(int pc)
        {
            if (CondItems(pc) is not { Items.Count: > 0 } c) return null;
            var (cond, used) = Expr(c.Items, 0, c.Branch);
            if (used != c.Items.Count) throw new ParseError($"condition at {pc} does not end at its branch");
            int nextRule = At(c.Branch).Arg;
            pc = c.Branch + 1;
            var body = new List<FsmStmt>();
            while (Stmt(pc) is { } s) { body.Add(s.Stmt); pc = s.End; }
            var (o, x) = At(pc);
            FsmExit exit;
            if (o == FsmOp.Jump) exit = FsmExit.To(-1);
            else if (o == FsmOp.Return) exit = FsmExit.Return(x);
            else throw new ParseError($"rule at {pc}: no jump or return (op {o})");
            pc++;
            if (pc != nextRule) throw new ParseError($"rule's branch skips to {nextRule}, the rule ends at {pc}");
            return (new FsmRule { Cond = cond, Body = body, Exit = exit }, x, pc);
        }
    }

    public static IEnumerable<FsmStmt> AllStmts(FsmRoutine r)
    {
        foreach (var st in r.States)
        {
            foreach (var u in st.Rules)
            {
                foreach (var t in u.Cond.Terms()) yield return t;
                foreach (var s in u.Body) yield return s;
            }
            foreach (var s in st.During) yield return s;
        }
    }

    /// <summary>The canonical code generator.</summary>
    public static class Codegen
    {
        /// <summary>
        /// The routine's instructions at <paramref name="base"/>. <paramref name="target"/> turns a call's (or a raw instruction's)
        /// target into an instruction index; a decompiled routine's are already indices.
        /// </summary>
        public static List<FsmInstr> Emit(FsmRoutine r, int @base, int protoCount, Func<int, int> target)
        {
            var o = new List<FsmInstr>();
            if (r.Asm is { } asm)
            {
                foreach (var i in asm) o.Add(FsmOp.Branches(i.Op) ? i with { Arg = target(i.Arg) } : i);
                return o;
            }
            int nloc = r.Locals.Count;
            int Pc() => @base + o.Count;
            int run = 0;
            foreach (var v in r.Locals)
            {
                if (v is null) { run++; continue; }
                if (run > 0) { o.Add(new(FsmOp.Alloc, run)); run = 0; }
                o.Add(new(FsmOp.PushImm, v.Value));
            }
            if (run > 0) o.Add(new(FsmOp.Alloc, run));
            o.Add(new(FsmOp.Jump, Pc() + 1));
            var statePc = new int[r.States.Count];
            var fix = new List<(int At, int State)>();
            void Stmt(FsmStmt s)
            {
                if (s.IsCall)
                {
                    foreach (var a in s.Args)
                        o.Add(a.Kind switch
                        {
                            FsmArgKind.Param => new(FsmOp.PushLocal, -a.Value),
                            FsmArgKind.Imm => new(FsmOp.PushImm, a.Value),
                            FsmArgKind.LocalValue => new(FsmOp.PushLocal, a.Value),
                            _ => new(FsmOp.PushLocalAddr, a.Value),
                        });
                    o.Add(new(FsmOp.Call, target(s.CallTarget)));
                    return;
                }
                if (s.Proto < 0 || s.Proto >= protoCount) throw new ArgumentException($"prototype {s.Proto} is not in the table");
                int temps = 0;
                foreach (var a in s.Args)
                    switch (a.Kind)
                    {
                        case FsmArgKind.Imm: temps++; o.Add(new(FsmOp.PushImm, a.Value)); o.Add(new(FsmOp.ArgLocalAddr, nloc + temps)); break;
                        case FsmArgKind.Param: o.Add(new(FsmOp.ArgLocal, -a.Value)); break;
                        case FsmArgKind.Local: o.Add(new(FsmOp.ArgLocalAddr, a.Value)); break;
                        default: o.Add(new(FsmOp.ArgLocal, a.Value)); break;
                    }
                o.Add(new(FsmOp.Exec, s.Proto));
                if (temps > 0) o.Add(new(FsmOp.Free, temps));
            }
            void Expr(FsmExpr e)
            {
                switch (e)
                {
                    case FsmExpr.Term t: Stmt(t.Op); break;
                    case FsmExpr.Not n: Expr(n.E); o.Add(new(FsmOp.Not, 0)); break;
                    case FsmExpr.And a:
                    {
                        Expr(a.L);
                        int i = o.Count; o.Add(default);
                        Expr(a.R);
                        o[i] = new(FsmOp.BranchIfFalse, Pc());
                        break;
                    }
                    case FsmExpr.Or x:
                    {
                        Expr(x.L);
                        o.Add(new(FsmOp.BranchIfFalse, Pc() + 2));
                        int i = o.Count; o.Add(default);
                        Expr(x.R);
                        o[i] = new(FsmOp.Jump, Pc());
                        break;
                    }
                }
            }
            for (int k = 0; k < r.States.Count; k++)
            {
                statePc[k] = Pc();
                var st = r.States[k];
                foreach (var u in st.Rules)
                {
                    Expr(u.Cond);
                    int b = o.Count; o.Add(default);
                    foreach (var s in u.Body) Stmt(s);
                    if (u.Exit.IsReturn) o.Add(new(FsmOp.Return, u.Exit.Value));
                    else
                    {
                        if (u.Exit.Value < 0 || u.Exit.Value >= r.States.Count) throw new ArgumentException($"a rule goes to state {u.Exit.Value} of {r.States.Count}");
                        fix.Add((o.Count, u.Exit.Value));
                        o.Add(new(FsmOp.Jump, 0));
                    }
                    o[b] = new(FsmOp.BranchIfFalse, Pc());
                }
                foreach (var s in st.During) Stmt(s);
                o.Add(new(FsmOp.Yield, statePc[k]));
            }
            foreach (var (at, s) in fix) o[at] = new(FsmOp.Jump, statePc[s]);
            return o;
        }
    }
}
