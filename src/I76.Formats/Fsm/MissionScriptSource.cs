namespace I76.Formats.Fsm;

/// <summary>
/// A mission script in its source form (docs/mission-scripts.md): the tables, the data cells, the scripts as steps, the threads.
/// <see cref="MissionScriptDecompiler"/> makes one from a mission's FSM chunk, <see cref="MissionScriptCompiler"/> turns one back
/// into the chunk (byte for byte the original when nothing was changed), <see cref="MissionScriptYaml"/> reads and writes the YAML.
/// Names (scripts, threads, globals, fields, variables, step ids) are the source's own and never reach the bytecode; aliases and
/// descriptions are notes.
/// </summary>
public sealed class MissionScriptSource
{
    public string? Mission { get; set; }
    /// <summary>The prototype table when it is not the stock one (<see cref="FsmVocabulary.Standard"/>); null: the stock one.</summary>
    public List<string>? Prototypes { get; set; }
    public List<ActorDef> Actors { get; } = [];
    public List<PathDef> Paths { get; } = [];
    public List<SoundDef> Sounds { get; } = [];
    public List<GlobalDef> Globals { get; } = [];
    public List<ScriptDef> Scripts { get; } = [];
    public List<ThreadDef> Threads { get; } = [];
}

public enum RefKind { Actor, Path, Sound }

/// <summary>A number, or an entry of the actor, path or sound table (which is its index there).</summary>
public readonly record struct ScriptValue(int Number, RefKind? Kind = null, string? Key = null)
{
    public bool IsRef => Kind is not null;
    public static ScriptValue Ref(RefKind k, string key) => new(0, k, key);
    public override string ToString() => Kind is { } k ? $"{RefPrefix(k)}.{Key}" : Number.ToString(System.Globalization.CultureInfo.InvariantCulture);
    public static string RefPrefix(RefKind k) => k switch { RefKind.Actor => "actor", RefKind.Path => "path", _ => "sound" };
}

/// <summary>An argument as written: a field or variable by name, a variable's content (<c>*v</c>), or a value.</summary>
public abstract record ScriptOperand
{
    public sealed record Name(string Id) : ScriptOperand;
    public sealed record Deref(string Id) : ScriptOperand;
    public sealed record Literal(ScriptValue Value) : ScriptOperand;
}

/// <summary>An operation with its arguments, or (with <see cref="Call"/>) a call of a script.</summary>
public sealed record ScriptAction(string Op, IReadOnlyList<ScriptOperand> Args, string? Call = null)
{
    public bool IsCall => Call is not null;
}

/// <summary>A condition: a predicate operation, or !, &amp;&amp;, || of conditions; the tree is the bytecode's, kept as written.</summary>
public abstract record ScriptCond
{
    public sealed record Test(ScriptAction Op) : ScriptCond;
    public sealed record Not(ScriptCond E) : ScriptCond;
    public sealed record And(ScriptCond L, ScriptCond R) : ScriptCond;
    public sealed record Or(ScriptCond L, ScriptCond R) : ScriptCond;

    public static readonly ScriptCond True = new Test(new ScriptAction("true", []));
    public bool IsTrue => this is Test { Op: { IsCall: false, Args.Count: 0 } a } && a.Op.Equals("true", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Where a rule goes after its actions.</summary>
public enum GotoKind
{
    /// <summary>Not written: the step after this one (the end of a choice's branch: after the choice; the end of a repeat: its start; the end of the script: stop).</summary>
    Default,
    /// <summary>The next step of the same list.</summary>
    Next,
    /// <summary>The thread ends.</summary>
    Stop,
    /// <summary>This step again, tested again in the same frame.</summary>
    Restart,
    /// <summary>The start of the innermost repeat.</summary>
    Continue,
    /// <summary>Past the innermost repeat.</summary>
    Break,
    /// <summary>The step with this id.</summary>
    Step,
}

public readonly record struct ScriptGoto(GotoKind Kind, string? Step = null)
{
    public static readonly ScriptGoto Default = new(GotoKind.Default);
    public override string ToString() => Kind switch
    {
        GotoKind.Step => Step!, GotoKind.Default => "", _ => Kind.ToString().ToLowerInvariant(),
    };
}

/// <summary>A rule of a step: when its condition holds, its actions run and the script goes on (<see cref="Goto"/>, or into <see cref="Steps"/>).</summary>
public sealed class ScriptRule
{
    public ScriptCond When { get; set; } = ScriptCond.True;
    public List<ScriptAction> Then { get; set; } = [];
    public ScriptGoto Goto { get; set; } = ScriptGoto.Default;
    /// <summary>A branch: steps of its own, entered when this rule fires (they continue after the step that holds the rule).</summary>
    public List<ScriptNode>? Steps { get; set; }
}

public enum StepKind
{
    /// <summary>Do the actions and go on (a single rule that always holds).</summary>
    Do,
    /// <summary>Wait until one condition holds.</summary>
    WaitUntil,
    /// <summary>Wait until the first of several rules holds.</summary>
    Wait,
}

public abstract class ScriptNode;

/// <summary>A step: one state of the machine. <see cref="WhileWaiting"/> runs on each frame none of its rules hold.</summary>
public sealed class ScriptStep : ScriptNode
{
    public string? Id { get; set; }
    public string? Description { get; set; }
    public StepKind Kind { get; set; }
    public List<ScriptRule> Rules { get; set; } = [];
    /// <summary>The actions run while waiting; null: none (the bytecode's <c>null</c> operation).</summary>
    public List<ScriptAction>? WhileWaiting { get; set; }
}

/// <summary>Steps that run again from the first when the last one is done (<c>continue</c>, <c>break</c> inside).</summary>
public sealed class ScriptRepeat : ScriptNode
{
    public List<ScriptNode> Steps { get; set; } = [];
}

public sealed class FieldDef
{
    public required string Name { get; set; }
    public string? Description { get; set; }
}

public sealed class VariableDef
{
    public required string Name { get; set; }
    /// <summary>The value it starts with; null: none (the original leaves it as it finds the memory, I26 zero).</summary>
    public ScriptValue? Init { get; set; }
    public string? Description { get; set; }
}

/// <summary>A routine. Either steps, or (<see cref="Asm"/>) raw instructions for code that is not in the state-machine shape.</summary>
public sealed class ScriptDef
{
    public required string Name { get; set; }
    public string? Alias { get; set; }
    public string? Description { get; set; }
    public List<FieldDef> Fields { get; set; } = [];
    public List<VariableDef> Variables { get; set; } = [];
    /// <summary>Rules tested first in every step.</summary>
    public List<ScriptRule> Guards { get; set; } = [];
    public List<ScriptNode> Steps { get; set; } = [];
    public List<string>? Asm { get; set; }
}

public sealed class GlobalDef
{
    public required string Name { get; set; }
    public ScriptValue Value { get; set; }
    public string? Description { get; set; }
}

public sealed class ActorDef
{
    /// <summary>The name references use (<c>actor.KEY</c>): the label, made unique.</summary>
    public required string Key { get; set; }
    public required string Label { get; set; }
    /// <summary>The placement's 8-byte name, high bits included; null: found by <see cref="Object"/> among the mission's placements.</summary>
    public byte[]? Id { get; set; }
    /// <summary>The placement's name without the high bits (a note when <see cref="Id"/> is given).</summary>
    public string? Object { get; set; }
    public string? Alias { get; set; }
    public string? Description { get; set; }
}

public sealed class PathDef
{
    public required string Key { get; set; }
    public required string Label { get; set; }
    public int Points { get; set; }
    public string? Description { get; set; }
}

public sealed class SoundDef
{
    public required string Key { get; set; }
    public required string File { get; set; }
    public string? Description { get; set; }
}

/// <summary>A thread: a script started at load with its fields bound to data cells (globals).</summary>
public sealed class ThreadDef
{
    public required string Name { get; set; }
    public string? Alias { get; set; }
    public string? Description { get; set; }
    /// <summary>The script it runs: a script's name (or name+N, an instruction inside it).</summary>
    public required string Script { get; set; }
    /// <summary>The globals its fields point to, by field name (in the script's field order).</summary>
    public List<(string Field, string Global)>? Named { get; set; }
    /// <summary>Or by position, for a thread whose start is not a script's fields.</summary>
    public List<string>? Positional { get; set; }
}
