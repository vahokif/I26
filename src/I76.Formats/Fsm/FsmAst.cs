namespace I76.Formats.Fsm;

/// <summary>
/// An argument as the bytecode passes it. Every argument of an operation is a pointer to an int cell: a thread parameter
/// (<c>argLocal −n</c>, a pointer to a data cell), a local of the routine (<c>argLocalAddr n</c>), or an immediate (<c>pushImm v</c>
/// into a temporary slot, then <c>argLocalAddr</c> of that slot, freed after the operation). A call pushes values for the callee's
/// parameters instead: a parameter forwarded (<c>pushLocal −n</c>), a local's address (<c>pushLocalAddr n</c>) or an immediate.
/// <see cref="LocalValue"/> (a local's content used as the pointer) is what <c>argLocal n</c>/<c>pushLocal n</c> with n ≥ 1 do; no
/// stock script does it.
/// </summary>
public readonly record struct FsmArg(FsmArgKind Kind, int Value)
{
    public static FsmArg Param(int n) => new(FsmArgKind.Param, n);
    public static FsmArg Local(int n) => new(FsmArgKind.Local, n);
    public static FsmArg Imm(int v) => new(FsmArgKind.Imm, v);
    public static FsmArg LocalValue(int n) => new(FsmArgKind.LocalValue, n);
}

/// <summary>Param: <c>fp − n</c> (n ≥ 2). Local: local slot n (1-based). Imm: the value. LocalValue: local slot n's content.</summary>
public enum FsmArgKind { Param, Local, Imm, LocalValue }

/// <summary>A statement: an operation (<c>exec proto</c> with its argument pushes) or a call of a routine.</summary>
public sealed record FsmStmt(int Proto, IReadOnlyList<FsmArg> Args, int CallTarget = -1)
{
    public bool IsCall => CallTarget >= 0;
    public bool Equivalent(FsmStmt o) => Proto == o.Proto && CallTarget == o.CallTarget && Args.SequenceEqual(o.Args);
}

/// <summary>A rule's condition: a short-circuit tree of predicate operations.</summary>
public abstract record FsmExpr
{
    public sealed record Term(FsmStmt Op) : FsmExpr;
    public sealed record Not(FsmExpr E) : FsmExpr;
    public sealed record And(FsmExpr L, FsmExpr R) : FsmExpr;
    public sealed record Or(FsmExpr L, FsmExpr R) : FsmExpr;

    public static bool Same(FsmExpr a, FsmExpr b) => (a, b) switch
    {
        (Term x, Term y) => x.Op.Equivalent(y.Op),
        (Not x, Not y) => Same(x.E, y.E),
        (And x, And y) => Same(x.L, y.L) && Same(x.R, y.R),
        (Or x, Or y) => Same(x.L, y.L) && Same(x.R, y.R),
        _ => false,
    };

    public IEnumerable<FsmStmt> Terms() => this switch
    {
        Term t => [t.Op],
        Not n => n.E.Terms(),
        And a => a.L.Terms().Concat(a.R.Terms()),
        Or o => o.L.Terms().Concat(o.R.Terms()),
        _ => [],
    };
}

/// <summary>Where a rule goes: a state of its routine (by index), or <c>return n</c> (the thread ends).</summary>
public readonly record struct FsmExit(bool IsReturn, int Value)
{
    public static FsmExit To(int state) => new(false, state);
    public static FsmExit Return(int n) => new(true, n);
}

/// <summary>A rule: when its condition holds, its statements run and the routine goes to its exit, in the same frame.</summary>
public sealed class FsmRule
{
    public required FsmExpr Cond { get; init; }
    public List<FsmStmt> Body { get; init; } = [];
    public FsmExit Exit { get; set; }

    public bool Same(FsmRule o) => FsmExpr.Same(Cond, o.Cond) && Body.Count == o.Body.Count && Body.Zip(o.Body).All(p => p.First.Equivalent(p.Second)) && Exit == o.Exit;
}

/// <summary>A state: its rules tried in order every frame; when none holds, the during statements run and the thread yields.</summary>
public sealed class FsmState
{
    public List<FsmRule> Rules { get; init; } = [];
    public List<FsmStmt> During { get; init; } = [];
}

/// <summary>
/// A routine in the state-machine shape: a prologue that makes its locals (an <c>allocLocals n</c> for each run of uninitialised
/// ones, a <c>pushImm v</c> for each initialised one), a jump to its first state, then its states. Or, for code that does not have
/// that shape, its instructions as they are (<see cref="Asm"/>).
/// </summary>
public sealed class FsmRoutine
{
    /// <summary>The locals, slot 1 first: null for uninitialised.</summary>
    public List<int?> Locals { get; init; } = [];
    public List<FsmState> States { get; init; } = [];
    /// <summary>The parameter count: how many cells a thread passes, what <c>return</c> pops.</summary>
    public int Fields { get; set; }
    /// <summary>The raw instructions when the routine is not in the state-machine shape (branch operands absolute).</summary>
    public List<FsmInstr>? Asm { get; init; }
    /// <summary>Where it was read from (instruction index), −1 for a new one.</summary>
    public int Pc { get; init; } = -1;
}
