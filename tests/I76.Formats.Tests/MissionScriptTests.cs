using System.Numerics;
using System.Text.Json.Nodes;
using I76.Formats.Fsm;
using I76.Formats.Mission.Edit;
using I76.Formats.Vfs;
using Json.Schema;
using Xunit;
using Xunit.Abstractions;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace I76.Formats.Tests;

/// <summary>
/// The mission-script source form (docs/mission-scripts.md): decompiling the FSM chunk to YAML and compiling it back gives the
/// same bytes, for every construct of the state-machine shape and for code outside it; the names sidecar renames without
/// changing a byte; the emitted YAML validates against the schema. Synthetic bytecode only, except the install round trip.
/// </summary>
public class MissionScriptTests(ITestOutputHelper log)
{
    private static int P(string name) => FsmVocabulary.Standard.ToList().IndexOf(name);

    /// <summary>A little assembler with labels.</summary>
    private sealed class Code
    {
        private readonly List<(int Op, int Arg, string? Label)> _i = [];
        private readonly Dictionary<string, int> _at = [];
        public int Count => _i.Count;
        public Code L(string l) { _at[l] = _i.Count; return this; }
        public Code Op(int op, int arg = 0) { _i.Add((op, arg, null)); return this; }
        public Code To(int op, string label) { _i.Add((op, 0, label)); return this; }
        public Code Exec(string name) => Op(FsmOp.Exec, P(name));
        public Code Imm(int v, int slot) => Op(FsmOp.PushImm, v).Op(FsmOp.ArgLocalAddr, slot);
        public Code Param(int n) => Op(FsmOp.ArgLocal, -n);
        public List<FsmInstr> Build() => [.. _i.Select(x => new FsmInstr(x.Op, x.Label is null ? x.Arg : _at[x.Label]))];
        public int this[string l] => _at[l];
    }

    private static byte[] PathTable(params string[] labels) =>
        FsmChunk.Read(FsmPaths.Write([], [.. labels.Select(l => new PathEntry(l, [Vector3.Zero, Vector3.One]))])).PathTable;

    private static FsmChunk Chunk(List<FsmInstr> code, List<FsmThreadEntry> threads, List<int> data) => new()
    {
        Prototypes = [.. FsmVocabulary.Standard],
        Actors = [new("user", [.. "user"u8.ToArray(), 0, 0, 0, 0x80]), new("sheriff", "sheriff1"u8.ToArray()), new("sheriff", "sheriff2"u8.ToArray())],
        Sounds = ["s1.wav", "s2.wav", "s1.wav"],
        PathTable = PathTable("road", "road", "exit"),
        Threads = threads, Data = data, Code = code,
    };

    /// <summary>Decompile, write, read, compile: the same bytes. Returns the YAML.</summary>
    private string RoundTrip(FsmChunk c, ScriptNames? names = null)
    {
        var bytes = c.ToBytes();
        Assert.Equal(bytes, FsmChunk.Read(bytes).ToBytes());
        var r = MissionScriptDecompiler.Decompile(bytes, "TEST", names);
        var yaml = MissionScriptYaml.Write(r.Script);
        log.WriteLine(yaml);
        var again = MissionScriptCompiler.Compile(MissionScriptYaml.Read(yaml), bytes).ToBytes();
        Assert.Equal(bytes, again);
        AssertSchemaValid(yaml);
        return yaml;
    }

    /// <summary>Source text compiled against <see cref="Chunk"/>'s tables.</summary>
    private static FsmChunk Compile(string yaml) =>
        MissionScriptCompiler.Compile(MissionScriptYaml.Read(yaml), Chunk([], [], []).ToBytes());

    private const string Tables = """
        actors:
          user: {id: 7573657200000080}
          sheriff: {id: 7368657269666631}
          sheriff_2: {label: sheriff, id: 7368657269666632}
        paths: {road: {points: 2}, road_2: {label: road, points: 2}, exit: {points: 2}}
        sounds: {s1: s1.wav, s2: s2.wav, s1_2: s1.wav}

        """;

    /// <summary>A two-field routine: a do step, then a wait with a stop rule and a restart rule and an action while waiting.</summary>
    private static FsmChunk Watcher()
    {
        var c = new Code();
        c.Op(FsmOp.Alloc, 1).Op(FsmOp.PushImm, 5).To(FsmOp.Jump, "s0");
        c.L("s0").Exec("true").To(FsmOp.BranchIfFalse, "s0d")
            .Param(3).Imm(3, 3).Imm(3, 4).Exec("setSkill").Op(FsmOp.Free, 2).To(FsmOp.Jump, "s1")
            .L("s0d").Exec("null").To(FsmOp.Yield, "s0");
        c.L("s1").Param(3).Exec("isDead").To(FsmOp.BranchIfFalse, "r1")
            .Param(2).Exec("inc").Op(FsmOp.Return, 2)
            .L("r1").Param(3).Exec("isAttacked").To(FsmOp.BranchIfFalse, "s1d").To(FsmOp.Jump, "s1")
            .L("s1d").Param(3).Imm(0, 3).Imm(45, 4).Exec("goto").Op(FsmOp.Free, 2).To(FsmOp.Yield, "s1");
        return Chunk(c.Build(), [new FsmThreadEntry(0, [0, 1])], [0, 0]);
    }

    [Fact]
    public void RulesDuringAndExitsRoundTrip()
    {
        var yaml = RoundTrip(Watcher());
        Assert.Contains("variables: {v0: null, v1: 5}", yaml);
        Assert.Contains("- do: setSkill(f0, 3, 3)", yaml);
        Assert.Contains("when: isDead(f0)", yaml);
        Assert.Contains("then: inc(f1)", yaml);
        Assert.DoesNotContain("goto: stop", yaml);   // the last step: a rule goes on to stop unless it says otherwise
        Assert.Contains("goto: restart", yaml);
        Assert.Contains("while_waiting: goto(f0, path.road, 45)", yaml);
        Assert.Contains("g0: actor.user", yaml);
        Assert.Contains("thread0: {script: script0, args: {f0: g0, f1: g1}}", yaml);
        // duplicate labels get unique keys, the label kept
        Assert.Contains("sheriff_2: {label: sheriff,", yaml);
        Assert.Contains("road_2: {label: road, points: 2}", yaml);
        Assert.Contains("s1_2: s1.wav", yaml);
    }

    [Theory]
    [InlineData("isDead(f0) && isShot(f0) && isArrived(f0)")]
    [InlineData("(isDead(f0) && isShot(f0)) && isArrived(f0)")]
    [InlineData("isDead(f0) || isShot(f0) || isArrived(f0)")]
    [InlineData("(isDead(f0) || isShot(f0)) || isArrived(f0)")]
    [InlineData("(isDead(f0) || isShot(f0)) && (isArrived(f0) || isAttacked(f0))")]
    [InlineData("(isDead(f0) && isShot(f0)) || isArrived(f0)")]
    [InlineData("isDead(f0) || (isShot(f0) && isArrived(f0))")]
    [InlineData("!isDead(f0)")]
    [InlineData("!!isDead(f0)")]
    [InlineData("!(isDead(f0) && isShot(f0))")]
    [InlineData("isDead(f0) && !isShot(f0)")]
    [InlineData("v0 == 3")]
    [InlineData("!(v0 > 2)")]
    [InlineData("v0 < f1")]
    [InlineData("isWithinNav(path.exit, f0, 20) && v0 < 1")]
    [InlineData("isKeypress()")]
    [InlineData("true")]
    public void ConditionsKeepTheirTreeAndOrder(string cond)
    {
        var src = Tables + $$"""
            scripts:
              s:
                fields: [f0, f1]
                variables: {v0: null}
                steps:
                  - wait_until: '{{cond}}'
            """;
        var c = Compile(src);
        var r = MissionScriptDecompiler.Decompile(c.ToBytes(), "TEST");
        var step = (ScriptStep)r.Script.Scripts[0].Steps[0];
        Assert.Equal(cond, ScriptText.Cond(step.Rules[0].When));
        RoundTrip(c);
    }

    [Fact]
    public void ShortCircuitEncodingIsTheOriginalCompilers()
    {
        var c = Compile(Tables + """
            scripts:
              s:
                fields: [car]
                steps:
                  - wait_until: isDead(car) && !isShot(car)
                  - wait_until: isDead(car) || isShot(car)
            """);
        int dead = P("isDead"), shot = P("isShot"), nul = P("null");
        FsmInstr[] want =
        [
            new(FsmOp.Jump, 1),
            new(FsmOp.ArgLocal, -2), new(FsmOp.Exec, dead), new(FsmOp.BranchIfFalse, 7), new(FsmOp.ArgLocal, -2), new(FsmOp.Exec, shot), new(FsmOp.Not, 0),
            new(FsmOp.BranchIfFalse, 9), new(FsmOp.Jump, 11), new(FsmOp.Exec, nul), new(FsmOp.Yield, 1),
            new(FsmOp.ArgLocal, -2), new(FsmOp.Exec, dead), new(FsmOp.BranchIfFalse, 15), new(FsmOp.Jump, 17), new(FsmOp.ArgLocal, -2), new(FsmOp.Exec, shot),
            new(FsmOp.BranchIfFalse, 19), new(FsmOp.Return, 1), new(FsmOp.Exec, nul), new(FsmOp.Yield, 11),
        ];
        Assert.Equal(want, c.Code);
    }

    [Fact]
    public void ArgumentKindsCompileDifferently()
    {
        var c = Compile(Tables + """
            scripts:
              s:
                fields: [car, cnt]
                variables: {t: null, line: sound.s2}
                steps:
                  - do:
                      - cbFromPrior(line, car, 3)
                      - cbFromPrior(sound.s2, car, 3)
                      - set(cnt, *t)
                      - attack(car, actor.sheriff_2, 0)
            """);
        int cfp = P("cbFromPrior");
        FsmInstr[] want =
        [
            new(FsmOp.Alloc, 1), new(FsmOp.PushImm, 1), new(FsmOp.Jump, 3),
            new(FsmOp.Exec, P("true")), new(FsmOp.BranchIfFalse, 0),
            new(FsmOp.ArgLocalAddr, 2), new(FsmOp.ArgLocal, -3), new(FsmOp.PushImm, 3), new(FsmOp.ArgLocalAddr, 3), new(FsmOp.Exec, cfp), new(FsmOp.Free, 1),
            new(FsmOp.PushImm, 1), new(FsmOp.ArgLocalAddr, 3), new(FsmOp.ArgLocal, -3), new(FsmOp.PushImm, 3), new(FsmOp.ArgLocalAddr, 4), new(FsmOp.Exec, cfp), new(FsmOp.Free, 2),
            new(FsmOp.ArgLocal, -2), new(FsmOp.ArgLocal, 1), new(FsmOp.Exec, P("set")),
            new(FsmOp.ArgLocal, -3), new(FsmOp.PushImm, 2), new(FsmOp.ArgLocalAddr, 3), new(FsmOp.PushImm, 0), new(FsmOp.ArgLocalAddr, 4), new(FsmOp.Exec, P("attack")), new(FsmOp.Free, 2),
            new(FsmOp.Return, 2),
        ];
        Assert.Equal(want, c.Code.Take(want.Length).Select((x, i) => i == 4 ? x with { Arg = 0 } : x));
        var yaml = RoundTrip(c);
        Assert.Contains("cbFromPrior(v1, f0, 3)", yaml);
        Assert.Contains("cbFromPrior(sound.s2, f0, 3)", yaml);
        Assert.Contains("set(f1, *v0)", yaml);
        Assert.Contains("attack(f0, actor.sheriff_2, 0)", yaml);
        Assert.Contains("v1: sound.s2", yaml);
    }

    private const string Structured = Tables + """
        scripts:
          s:
            fields: [car, n]
            variables: {t: null}
            guards:
              - when: isDead(car)
                then: inc(n)
                goto: stop
            steps:
              - do: startTimer(t)
              - repeat:
                  - wait_until: isArrived(car)
                    while_waiting: goto(car, path.road, 30)
                  - wait:
                      - when: isShot(car)
                        goto: continue
                      - when: isRammed(car)
                  - wait:
                      - when: isAttacked(car)
                        goto: break
                      - when: timeGreater(t, 5)
                        goto: continue
                      - when: true
                    while_waiting: goto(car, path.exit, 30)
              - wait:
                  - when: n == 1
                    steps:
                      - do: cb(sound.s1)
                      - wait_until: isCBEmpty()
                  - when: n == 2
                    steps:
                      - do: cb(sound.s2)
                  - when: true
                    goto: fight
              - do: successAll(5)
                goto: stop
              - id: fight
                wait_until: isDead(actor.sheriff)
                goto: restart
                while_waiting: attack(car, actor.sheriff, 0)
          irreducible:
            fields: [car]
            steps:
              - wait:
                  - when: isShot(car)
                    goto: b
                  - when: isRammed(car)
                    goto: c
              - id: b
                wait:
                  - when: isArrived(car)
                    goto: c
                  - when: isDead(car)
                    goto: stop
              - id: c
                wait_until: isAttacked(car)
                goto: b
        threads:
          t0: {script: s, args: {car: g1, n: g0}}
          t1: {script: irreducible, args: {car: g1}}
        globals: {g0: 0, g1: actor.sheriff}
        """;

    [Fact]
    public void RepeatsBranchesGuardsAndGotosRoundTrip()
    {
        var c = Compile(Structured);
        var yaml = RoundTrip(c);
        Assert.Contains("guards:", yaml);
        Assert.Contains("repeat:", yaml);
        Assert.Contains("goto: break", yaml);
        Assert.Contains("goto: stop", yaml);
        Assert.Contains("goto: continue", yaml);
        var r = MissionScriptDecompiler.Decompile(c.ToBytes(), "TEST");
        Assert.Contains(r.Script.Scripts[0].Steps.OfType<ScriptStep>(), s => s.Rules.Any(u => u.Steps is not null));
        // the irreducible machine keeps its gotos by id
        Assert.Equal("goto", ShapeOf(r.Script.Scripts[1]));
    }

    [Fact]
    public void GuardsGoInEveryStepAndThatIsTheLayout()
    {
        var c = Compile(Structured);
        var r = MissionScriptDecompiler.Decompile(c.ToBytes(), "TEST");
        var s = r.Routines[0];
        Assert.True(s.States.Count >= 6);
        Assert.All(s.States, st => Assert.Equal(P("isDead"), st.Rules[0].Cond.Terms().Single().Proto));
    }

    private static string ShapeOf(ScriptDef s)
    {
        bool gotos = false;
        void Walk(List<ScriptNode> ns)
        {
            foreach (var n in ns)
                if (n is ScriptRepeat rp) Walk(rp.Steps);
                else foreach (var r in ((ScriptStep)n).Rules) { gotos |= r.Goto.Kind == GotoKind.Step; if (r.Steps is { } a) Walk(a); }
        }
        Walk(s.Steps);
        return gotos ? "goto" : "no goto";
    }

    /// <summary>A call (no stock thread runs one) and code outside the shape, kept as raw instructions with symbolic targets.</summary>
    [Fact]
    public void CallsAndRawCodeRoundTrip()
    {
        var c = new Code();
        // script0 (2 fields): calls script1 with a field, an immediate and a variable's address
        c.Op(FsmOp.Alloc, 1).To(FsmOp.Jump, "a0");
        c.L("a0").Exec("true").To(FsmOp.BranchIfFalse, "a0d")
            .Op(FsmOp.PushLocal, -3).Op(FsmOp.PushImm, 3).Op(FsmOp.PushLocalAddr, 1).To(FsmOp.Call, "b")
            .Op(FsmOp.Return, 2)
            .L("a0d").Exec("null").To(FsmOp.Yield, "a0");
        // script1 (3 fields)
        c.L("b").To(FsmOp.Jump, "b0");
        c.L("b0").Exec("isCBEmpty").To(FsmOp.BranchIfFalse, "b0d").Op(FsmOp.Return, 3)
            .L("b0d").Param(4).Exec("sit").To(FsmOp.Yield, "b0");
        // not a routine: an exec with nothing after it, and a jump into script0's first state
        c.L("raw").Exec("killCB").To(FsmOp.Jump, "a0").Op(15, 7);
        var chunk = Chunk(c.Build(), [new FsmThreadEntry(0, [0, 1]), new FsmThreadEntry(c["raw"], [])], [0, 0]);
        var yaml = RoundTrip(chunk);
        Assert.Contains("call: script1", yaml);
        Assert.Contains("args: [f0, 3, v0]", yaml);
        Assert.Contains("asm:", yaml);
        Assert.Contains("- exec killCB", yaml);
        Assert.Contains("- jump script0+2", yaml);
        Assert.Contains("- op15 7", yaml);
        Assert.Contains("thread1: {script: script2}", yaml);
    }

    [Fact]
    public void RawCodeCanBeWrittenByHand()
    {
        var c = Compile(Tables + """
            scripts:
              s:
                asm:
                  - jump s+1
                  - exec killCB
                  - yieldJump s+1
            threads:
              t: {script: s+1}
            """);
        Assert.Equal([new FsmInstr(FsmOp.Jump, 1), new FsmInstr(FsmOp.Exec, P("killCB")), new FsmInstr(FsmOp.Yield, 1)], c.Code);
        Assert.Equal(1, c.Threads[0].StartPc);
    }

    [Fact]
    public void NamesRenameTheSourceAndNotTheBytes()
    {
        var chunk = Watcher();
        var bytes = chunk.ToBytes();
        string hash = MissionScriptDecompiler.ChunkHash(bytes);
        var names = ScriptNames.Read($$$"""
            mission: TEST
            fsm: {{{hash}}}
            threads: {thread0: {name: watch_user, description: the player's watcher}}
            scripts:
              script0:
                name: watcher
                alias: Watch a car
                fields: {f0: car, f1: {name: deaths, description: counts deaths}}
                variables: {v1: start}
                steps: {step1: {name: watching, description: until it dies}}
            globals: {g0: player, g1: {name: deaths, description: shared}}
            actors: {user: the player's car}
            sounds: {s1: "Hello there"}
            """);
        var yaml = RoundTrip(chunk, names);
        Assert.Contains("watcher:", yaml);
        Assert.Contains("alias: Watch a car", yaml);
        Assert.Contains("fields:\n", yaml);
        Assert.Contains("name: deaths", yaml);
        Assert.Contains("isDead(car)", yaml);
        Assert.Contains("inc(deaths)", yaml);
        Assert.Contains("start: 5", yaml);
        Assert.Contains("id: watching", yaml);
        Assert.Contains("description: until it dies", yaml);
        Assert.Contains("watch_user: {script: watcher, args: {car: player, deaths: deaths}", yaml);
        Assert.Contains("player: actor.user", yaml);
        Assert.Contains("description: Hello there", yaml);

        // names for another chunk: only a script keyed by its body hash applies
        var r0 = MissionScriptDecompiler.Decompile(bytes, "TEST");
        var other = ScriptNames.Read($$"""
            fsm: 0000000000000000
            scripts:
              script0: {name: wrong}
              "@{{r0.BodyHashes[0]}}": {name: library_watcher}
            globals: {g0: wrong}
            """);
        var r = MissionScriptDecompiler.Decompile(bytes, "TEST", other);
        Assert.NotEmpty(r.Warnings);
        Assert.Equal("library_watcher", r.Script.Scripts[0].Name);
        Assert.Equal("g0", r.Script.Globals[0].Name);
        RoundTrip(chunk, other);

        // an unusable name keeps the generic one
        var bad = ScriptNames.Read("scripts: {script0: {fields: {f0: '2x', f1: f0}}}");
        var rb = MissionScriptDecompiler.Decompile(bytes, "TEST", bad);
        Assert.Equal(["f0", "f1"], rb.Script.Scripts[0].Fields.Select(f => f.Name));
        Assert.Equal(2, rb.Warnings.Count);
    }

    [Fact]
    public void TheNamesTemplateListsEveryGenericName()
    {
        var bytes = Watcher().ToBytes();
        var r = MissionScriptDecompiler.Decompile(bytes, "TEST");
        var t = ScriptNames.Template(r, MissionScriptDecompiler.ChunkHash(bytes));
        var n = ScriptNames.Read(t);
        Assert.Equal(MissionScriptDecompiler.ChunkHash(bytes), n.Fsm);
        Assert.Contains("thread0", n.Threads.Keys);
        Assert.Contains("step1", n.Scripts["script0"].Steps.Keys);
        Assert.Contains("v1", n.Scripts["script0"].Variables.Keys);
        Assert.Equal(bytes, MissionScriptCompiler.Compile(MissionScriptDecompiler.Decompile(bytes, "TEST", n).Script, bytes).ToBytes());
    }

    [Theory]
    [InlineData("scripts:\n  s:\n    steps:\n      - wait_until: isDead(\n", "line ")]
    [InlineData("scripts:\n  s:\n    steps:\n      - wait_untill: true\n", "unknown key 'wait_untill'")]
    [InlineData("scripts:\n  s:\n    steps:\n      - do: []\n        goto: nowhere\n", "no step nowhere")]
    [InlineData("scripts:\n  s:\n    steps:\n      - do: inc(x)\n", "x is no field or variable")]
    [InlineData("scripts:\n  s:\n    steps:\n      - wait_until: inc(x)\n", "not a test")]
    [InlineData("scripts:\n  s:\n    steps:\n      - do: frobnicate()\n", "no operation frobnicate")]
    [InlineData("scripts:\n  s:\n    steps:\n      - do: []\n        goto: continue\n", "outside a repeat")]
    [InlineData("scripts:\n  s:\n    steps:\n      - wait_until: v0 >= 1\n", "no '>='")]
    [InlineData("paths: {elsewhere: {points: 2}}\nscripts: {}\n", "path tool")]
    public void ErrorsSayWhatAndWhere(string src, string message)
    {
        var e = Assert.Throws<ScriptError>(() => Compile(src.StartsWith("paths", StringComparison.Ordinal) ? src : Tables + src));
        Assert.Contains(message, e.Message);
    }

    [Fact]
    public void ASourceWithoutPathsUsesTheMissions()
    {
        var c = Compile("scripts:\n  s:\n    fields: [car]\n    steps:\n      - wait_until: isWithinNav(path.road_2, car, 5)\n");
        Assert.Equal(new FsmInstr(FsmOp.PushImm, 1), c.Code[1]);
        Assert.Equal(PathTable("road", "road", "exit"), c.PathTable);
    }

    [Fact]
    public void TheEmptyScriptStaysEmpty()
    {
        var doc = MissionScriptDecompiler.Decompile([], "M01").Script;
        var yaml = MissionScriptYaml.Write(doc);
        Assert.Empty(MissionScriptCompiler.Compile(MissionScriptYaml.Read(yaml), []).ToBytes());
        AssertSchemaValid(yaml);
    }

    /// <summary>docs/mission-scripts.md's examples: the source compiles (and round-trips), the names sidecar reads.</summary>
    [Fact]
    public void TheDocumentsExamplesWork()
    {
        string? root = null;
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null && root is null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "I76.sln"))) root = d.FullName;
        if (root is null) return;
        var md = File.ReadAllText(Path.Combine(root, "docs", "mission-scripts.md"));
        var blocks = System.Text.RegularExpressions.Regex.Matches(md, "```yaml\n(.*?)```", System.Text.RegularExpressions.RegexOptions.Singleline)
            .Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(2, blocks.Count);
        AssertSchemaValid(blocks[0]);
        var into = new FsmChunk { Prototypes = [.. FsmVocabulary.Standard], PathTable = PathTable("road", "road") }.ToBytes();
        var c = MissionScriptCompiler.Compile(MissionScriptYaml.Read(blocks[0]), into);
        Assert.Equal(5, c.Data.Count + c.Threads.Count + c.Actors.Count);
        RoundTrip(c);
        var n = ScriptNames.Read(blocks[1]);
        Assert.Equal("escort", n.Scripts["script15"].Name);
        Assert.Equal("route", n.Scripts["script15"].Fields["f1"].Name);
        Assert.Equal("death_line", n.Scripts["@1a2b3c4d5e6f"].Name);
        Assert.Equal("Sheriff: you're going too fast", n.Sounds["gdsgc28"].Description);
    }

    // ---- schema ----

    private static readonly Lazy<JsonSchema> Schema = new(() => JsonSchema.FromText(MissionScriptYaml.Schema()));

    /// <summary>The YAML as an editor's YAML 1.2 core schema reads it (plain null, booleans and integers typed).</summary>
    private static JsonNode? ToJson(YamlNode n) => n switch
    {
        YamlMappingNode m => new JsonObject(m.Children.Select(kv => KeyValuePair.Create(((YamlScalarNode)kv.Key).Value!, ToJson(kv.Value)))),
        YamlSequenceNode s => new JsonArray([.. s.Children.Select(ToJson)]),
        YamlScalarNode { Style: ScalarStyle.Plain } p when p.Value is null or "" or "~" or "null" => null,
        YamlScalarNode { Style: ScalarStyle.Plain, Value: "true" or "false" } b => JsonValue.Create(b.Value == "true"),
        YamlScalarNode { Style: ScalarStyle.Plain } i when long.TryParse(i.Value, out long v) => JsonValue.Create(v),
        YamlScalarNode x => JsonValue.Create(x.Value),
        _ => null,
    };

    private static void AssertSchemaValid(string yaml)
    {
        var ys = new YamlStream();
        ys.Load(new StringReader(yaml));
        var json = ys.Documents.Count == 0 ? new JsonObject() : ToJson(ys.Documents[0].RootNode);
        var res = Schema.Value.Evaluate(json, new EvaluationOptions { OutputFormat = OutputFormat.List });
        var errs = res.Details.Where(d => d.Errors is { Count: > 0 }).Select(d => $"{d.InstanceLocation}: {string.Join("; ", d.Errors!.Values)}").Take(10);
        Assert.True(res.IsValid, string.Join("\n", errs));
    }

    [Fact]
    public void TheSchemaRejectsWhatTheReaderRejects()
    {
        var ys = new YamlStream();
        ys.Load(new StringReader("scripts:\n  s:\n    steps:\n      - wait_untill: true\n"));
        Assert.False(Schema.Value.Evaluate(ToJson(ys.Documents[0].RootNode)).IsValid);
    }

    // ---- the installs ----

    public static IEnumerable<object[]> Installs()
    {
        foreach (var v in new[] { "I76_INSTALL", "I76_NITRO_INSTALL" })
            yield return [v];
    }

    /// <summary>Every scripted mission of both games (miss16 and miss8) decompiles to steps (no raw code) and compiles back to the same file.</summary>
    [Theory]
    [MemberData(nameof(Installs))]
    public void EveryScriptedMissionRoundTripsByteForByte(string env)
    {
        if (Environment.GetEnvironmentVariable(env) is not { Length: > 0 } dir || !Directory.Exists(dir)) return;
        var install = new GameInstall(dir);
        int files = 0, threads = 0, plain = 0;
        long instrs = 0;
        foreach (var sub in new[] { "miss16", "miss8" })
        {
            if (install.FindDir(sub) is not { } md) continue;
            foreach (var path in Directory.EnumerateFiles(md))
            {
                if (Path.GetExtension(path).ToLowerInvariant() is ".ter" or ".pcf" or ".txt") continue;
                var file = File.ReadAllBytes(path);
                var payload = MissionScriptFile.Payload(file);
                if (payload.Length == 0 || FsmChunk.Read(payload).Code.Count == 0) continue;
                files++;
                var r = MissionScriptFile.Decompile(file, Path.GetFileNameWithoutExtension(path));
                Assert.All(r.Script.Scripts, s => Assert.Null(s.Asm));
                var yaml = MissionScriptYaml.Write(r.Script);
                var again = MissionScriptFile.Compile(yaml, file);
                Assert.True(file.AsSpan().SequenceEqual(again), $"{path} does not round-trip");
                if (sub == "miss16" && files <= 3) AssertSchemaValid(yaml);
                instrs += r.Chunk.Code.Count;
                threads += r.Script.Threads.Count;
                plain += r.Script.Threads.Count(t => r.Script.Scripts.First(s => s.Name == t.Script) is var s && ShapeOf(s) == "no goto");
            }
        }
        log.WriteLine($"{env}: {files} scripted missions, {instrs} instructions, {threads} threads ({plain} without explicit gotos)");
        Assert.True(files >= 40, $"{files} scripted missions");
    }
}
