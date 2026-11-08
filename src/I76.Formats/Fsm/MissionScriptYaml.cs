using System.Globalization;
using System.Text;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;

namespace I76.Formats.Fsm;

/// <summary>The YAML form of a <see cref="MissionScriptSource"/> (docs/mission-scripts.md; schema: mission-script.schema.json).</summary>
public static class MissionScriptYaml
{
    public const string SchemaUrl = "https://raw.githubusercontent.com/vahokif/I26/master/src/I76.Formats/Fsm/mission-script.schema.json";

    /// <summary>The JSON Schema of the YAML form (draft-07), for editors' validation and completion.</summary>
    public static string Schema()
    {
        using var s = typeof(MissionScriptYaml).Assembly.GetManifestResourceStream("mission-script.schema.json")!;
        return new StreamReader(s).ReadToEnd();
    }

    // ---------------------------------------------------------------- writing

    private abstract record Y;
    private sealed record YS(string V, bool Plain = false) : Y;
    private sealed record YM(List<(string K, Y V)> Items, bool Flow = false) : Y
    {
        public YM Add(string k, Y? v) { if (v is not null) Items.Add((k, v)); return this; }
    }
    private sealed record YL(List<Y> Items, bool Flow = false) : Y;

    private static YS S(string s) => new(s);
    private static YS? SOpt(string? s) => s is null ? null : new YS(s);
    private static YS N(int v) => new(v.ToString(CultureInfo.InvariantCulture), true);

    public static string Write(MissionScriptSource d)
    {
        var top = new YM([]);
        top.Add("mission", SOpt(d.Mission));
        if (d.Prototypes is { } p) top.Add("prototypes", new YL([.. p.Select(S)], true));
        var actors = new YM([]);
        foreach (var a in d.Actors)
        {
            var m = new YM([], true);
            if (a.Label != a.Key) m.Add("label", S(a.Label));
            m.Add("object", SOpt(a.Object));
            if (a.Id is { } id) m.Add("id", S(Convert.ToHexString(id).ToLowerInvariant()));
            m.Add("alias", SOpt(a.Alias)).Add("description", SOpt(a.Description));
            actors.Add(a.Key, m);
        }
        top.Add("actors", actors);
        var paths = new YM([]);
        foreach (var x in d.Paths)
        {
            var m = new YM([], true);
            if (x.Label != x.Key) m.Add("label", S(x.Label));
            m.Add("points", N(x.Points)).Add("description", SOpt(x.Description));
            paths.Add(x.Key, m);
        }
        top.Add("paths", paths);
        var sounds = new YM([]);
        foreach (var s in d.Sounds)
            sounds.Add(s.Key, s.Description is null ? S(s.File) : new YM([("file", S(s.File)), ("description", S(s.Description))], true));
        top.Add("sounds", sounds);
        var globals = new YM([]);
        foreach (var g in d.Globals)
            globals.Add(g.Name, g.Description is null ? Val(g.Value) : new YM([("value", Val(g.Value)), ("description", S(g.Description))], true));
        top.Add("globals", globals);
        var scripts = new YM([]);
        foreach (var s in d.Scripts) scripts.Add(s.Name, Script(s));
        top.Add("scripts", scripts);
        var threads = new YM([]);
        foreach (var t in d.Threads)
        {
            var m = new YM([], true);
            m.Add("script", S(t.Script));
            if (t.Named is { } named) m.Add("args", new YM([.. named.Select(x => (x.Field, (Y)S(x.Global)))], true));
            else if (t.Positional is { Count: > 0 } pos) m.Add("args", new YL([.. pos.Select(S)], true));
            m.Add("alias", SOpt(t.Alias)).Add("description", SOpt(t.Description));
            threads.Add(t.Name, m);
        }
        top.Add("threads", threads);

        var sw = new StringWriter { NewLine = "\n" };
        var e = new Emitter(sw, new EmitterSettings(2, int.MaxValue, false, 1024, false, true, "\n"));
        e.Emit(new StreamStart());
        e.Emit(new DocumentStart(null, null, true));
        Emit(e, top);
        e.Emit(new DocumentEnd(true));
        e.Emit(new StreamEnd());
        var body = sw.ToString();
        var o = new StringBuilder();
        o.Append("# yaml-language-server: $schema=").Append(SchemaUrl).Append('\n');
        o.Append("# I26 mission script").Append(d.Mission is { } mn ? " for " + mn : "").Append(" (the format: docs/mission-scripts.md)\n");
        // a blank line before each top-level section and each script, for reading
        bool inScripts = false;
        foreach (var line in body.Split('\n'))
        {
            if (line.Length == 0) continue;
            bool topKey = line[0] != ' ' && line[0] != '-';
            if (topKey) { o.Append('\n'); inScripts = line.StartsWith("scripts:", StringComparison.Ordinal); }
            else if (inScripts && line.Length > 2 && line[0] == ' ' && line[1] == ' ' && line[2] != ' ' && line[2] != '-') o.Append('\n');
            o.Append(line).Append('\n');
        }
        return o.ToString();
    }

    private static Y Val(ScriptValue v) => v.IsRef ? S(v.ToString()) : N(v.Number);

    private static Y Script(ScriptDef s)
    {
        var m = new YM([]);
        m.Add("alias", SOpt(s.Alias)).Add("description", SOpt(s.Description));
        if (s.Asm is { } asm)
        {
            m.Add("asm", new YL([.. asm.Select(S)]));
            return m;
        }
        if (s.Fields.Count > 0)
            m.Add("fields", s.Fields.All(f => f.Description is null) ? new YL([.. s.Fields.Select(f => S(f.Name))], true)
                : new YL([.. s.Fields.Select(f => f.Description is null ? (Y)S(f.Name) : new YM([("name", S(f.Name)), ("description", S(f.Description))], true))]));
        if (s.Variables.Count > 0)
        {
            var v = new YM([], s.Variables.All(x => x.Description is null) && s.Variables.Count <= 8);
            foreach (var x in s.Variables)
            {
                Y init = x.Init is { } i ? Val(i) : new YS("null", true);
                v.Add(x.Name, x.Description is null ? init : new YM([("value", init), ("description", S(x.Description))], true));
            }
            m.Add("variables", v);
        }
        if (s.Guards.Count > 0) m.Add("guards", new YL([.. s.Guards.Select(Rule)]));
        m.Add("steps", Nodes(s.Steps));
        return m;
    }

    private static YL Nodes(List<ScriptNode> nodes) => new([.. nodes.Select(Node)]);

    private static Y Node(ScriptNode n)
    {
        if (n is ScriptRepeat r) return new YM([("repeat", Nodes(r.Steps))]);
        var s = (ScriptStep)n;
        var m = new YM([]);
        m.Add("id", SOpt(s.Id)).Add("description", SOpt(s.Description));
        switch (s.Kind)
        {
            case StepKind.Do:
                m.Add("do", Actions(s.Rules[0].Then, emptyList: true));
                m.Add("goto", Goto(s.Rules[0].Goto));
                break;
            case StepKind.WaitUntil:
                m.Add("wait_until", CondY(s.Rules[0].When));
                m.Add("then", Actions(s.Rules[0].Then, emptyList: false));
                m.Add("goto", Goto(s.Rules[0].Goto));
                break;
            default:
                m.Add("wait", new YL([.. s.Rules.Select(Rule)], s.Rules.Count == 0));
                break;
        }
        if (s.WhileWaiting is { } ww) m.Add("while_waiting", Actions(ww, emptyList: true));
        return m;
    }

    private static YM Rule(ScriptRule r)
    {
        var m = new YM([]);
        m.Add("when", CondY(r.When));
        m.Add("then", Actions(r.Then, emptyList: false));
        m.Add("goto", Goto(r.Goto));
        if (r.Steps is { } st) m.Add("steps", Nodes(st));
        return m;
    }

    private static YS CondY(ScriptCond c) { var t = ScriptText.Cond(c); return new YS(t, t is "true" or "false"); }

    private static Y? Goto(ScriptGoto g) => g.Kind == GotoKind.Default ? null : S(g.ToString());

    private static Y? Actions(List<ScriptAction> a, bool emptyList)
    {
        if (a.Count == 0) return emptyList ? new YL([], true) : null;
        if (a.Count == 1 && !a[0].IsCall) return S(ScriptText.Action(a[0]));
        return new YL([.. a.Select(Action)]);
    }

    private static Y Action(ScriptAction a) => a.IsCall
        ? new YM([("call", S(a.Call!)), ("args", new YL([.. a.Args.Select(x => (Y)new YS(ScriptText.Operand(x), x is ScriptOperand.Literal { Value.IsRef: false }))], true))], true)
        : S(ScriptText.Action(a));

    // what a YAML reader would take for something other than a string (1.2 core schema, and 1.1's booleans): written quoted
    private static readonly System.Text.RegularExpressions.Regex NotAString = new(
        @"^(|~|null|Null|NULL|true|True|TRUE|false|False|FALSE|yes|Yes|YES|no|No|NO|on|On|ON|off|Off|OFF|y|Y|n|N|[-+]?[0-9_]+|0o[0-7]+|0x[0-9a-fA-F]+|[-+]?(\.[0-9]+|[0-9][0-9_]*(\.[0-9_]*)?)([eE][-+]?[0-9]+)?|[-+]?\.(inf|Inf|INF)|\.(nan|NaN|NAN))$");

    private static void Emit(IEmitter e, Y y)
    {
        switch (y)
        {
            case YS s:
                var style = s.V.Contains('\n') ? ScalarStyle.DoubleQuoted : s.Plain ? ScalarStyle.Plain : NotAString.IsMatch(s.V) ? ScalarStyle.SingleQuoted : ScalarStyle.Any;
                e.Emit(new Scalar(AnchorName.Empty, TagName.Empty, s.V, style, true, true));
                break;
            case YM m:
                e.Emit(new MappingStart(AnchorName.Empty, TagName.Empty, true, m.Flow ? MappingStyle.Flow : MappingStyle.Block));
                foreach (var (k, v) in m.Items) { Emit(e, S(k)); Emit(e, v); }
                e.Emit(new MappingEnd());
                break;
            case YL l:
                e.Emit(new SequenceStart(AnchorName.Empty, TagName.Empty, true, l.Flow ? SequenceStyle.Flow : SequenceStyle.Block));
                foreach (var v in l.Items) Emit(e, v);
                e.Emit(new SequenceEnd());
                break;
        }
    }

    // ---------------------------------------------------------------- reading

    public static MissionScriptSource Read(string text)
    {
        var ys = new YamlStream();
        try { ys.Load(new StringReader(text)); }
        catch (YamlException ex) { throw new ScriptError($"line {ex.Start.Line}: {ex.Message}"); }
        if (ys.Documents.Count == 0) return new MissionScriptSource();
        var top = Map(ys.Documents[0].RootNode, "the file");
        var d = new MissionScriptSource();
        foreach (var (k, v) in Entries(top, "the file", "mission", "prototypes", "actors", "paths", "sounds", "globals", "scripts", "threads"))
            switch (k)
            {
                case "mission": d.Mission = Opt(v); break;
                case "prototypes": d.Prototypes = [.. Seq(v, k).Select(x => Str(x, k))]; break;
                case "actors":
                    foreach (var (key, a) in Entries(Map(v, k), k))
                    {
                        Key(key, a, "actor");
                        var ad = new ActorDef { Key = key, Label = key };
                        if (a is YamlScalarNode) ad.Object = Str(a, key);
                        else
                            foreach (var (f, x) in Entries(Map(a, $"actor {key}"), $"actor {key}", "label", "object", "id", "alias", "description"))
                                switch (f)
                                {
                                    case "label": ad.Label = Str(x, f); break;
                                    case "object": ad.Object = Str(x, f); break;
                                    case "id": ad.Id = Hex8(x, key); break;
                                    case "alias": ad.Alias = Opt(x); break;
                                    case "description": ad.Description = Opt(x); break;
                                }
                        d.Actors.Add(ad);
                    }
                    break;
                case "paths":
                    foreach (var (key, a) in Entries(Map(v, k), k))
                    {
                        Key(key, a, "path");
                        var pd = new PathDef { Key = key, Label = key };
                        if (a is YamlMappingNode)
                            foreach (var (f, x) in Entries(Map(a, $"path {key}"), $"path {key}", "label", "points", "description"))
                                switch (f)
                                {
                                    case "label": pd.Label = Str(x, f); break;
                                    case "points": pd.Points = Int(x, f); break;
                                    case "description": pd.Description = Opt(x); break;
                                }
                        else if (Opt(a) is { } lbl) pd.Label = lbl;
                        d.Paths.Add(pd);
                    }
                    break;
                case "sounds":
                    foreach (var (key, a) in Entries(Map(v, k), k))
                    {
                        Key(key, a, "sound");
                        if (a is YamlScalarNode) { d.Sounds.Add(new SoundDef { Key = key, File = Str(a, key) }); continue; }
                        string? file = null, desc = null;
                        foreach (var (f, x) in Entries(Map(a, $"sound {key}"), $"sound {key}", "file", "description"))
                            if (f == "file") file = Str(x, f); else desc = Opt(x);
                        d.Sounds.Add(new SoundDef { Key = key, File = file ?? throw Err(a, $"sound {key}: no file"), Description = desc });
                    }
                    break;
                case "globals":
                    foreach (var (name, a) in Entries(Map(v, k), k))
                    {
                        if (a is YamlScalarNode) { d.Globals.Add(new GlobalDef { Name = name, Value = Value(a) }); continue; }
                        var gd = new GlobalDef { Name = name };
                        bool has = false;
                        foreach (var (f, x) in Entries(Map(a, $"global {name}"), $"global {name}", "value", "description"))
                            if (f == "value") { gd.Value = Value(x); has = true; }
                            else gd.Description = Opt(x);
                        if (!has) throw Err(a, $"global {name}: no value");
                        d.Globals.Add(gd);
                    }
                    break;
                case "scripts":
                    foreach (var (name, a) in Entries(Map(v, k), k)) d.Scripts.Add(ReadScript(name, a));
                    break;
                case "threads":
                    foreach (var (name, a) in Entries(Map(v, k), k))
                    {
                        string? script = null;
                        var td = new ThreadDef { Name = name, Script = "" };
                        foreach (var (f, x) in Entries(Map(a, $"thread {name}"), $"thread {name}", "script", "args", "alias", "description"))
                            switch (f)
                            {
                                case "script": script = Str(x, f); break;
                                case "args":
                                    if (x is YamlSequenceNode sq) td.Positional = [.. sq.Children.Select(c => Str(c, f))];
                                    else td.Named = [.. Entries(Map(x, $"thread {name} args"), $"thread {name} args").Select(e => (e.Key, Str(e.Value, e.Key)))];
                                    break;
                                case "alias": td.Alias = Opt(x); break;
                                case "description": td.Description = Opt(x); break;
                            }
                        td.Script = script ?? throw Err(a, $"thread {name}: no script");
                        if (td.Named is null && td.Positional is null) td.Positional = [];
                        d.Threads.Add(td);
                    }
                    break;
            }
        return d;
    }

    private static ScriptDef ReadScript(string name, YamlNode n)
    {
        var sd = new ScriptDef { Name = name };
        bool steps = false;
        foreach (var (k, v) in Entries(Map(n, $"script {name}"), $"script {name}", "alias", "description", "fields", "variables", "guards", "steps", "asm"))
        {
            string w = $"script {name} {k}";
            switch (k)
            {
                case "alias": sd.Alias = Opt(v); break;
                case "description": sd.Description = Opt(v); break;
                case "fields":
                    foreach (var f in Seq(v, w))
                    {
                        if (f is YamlScalarNode) { sd.Fields.Add(new FieldDef { Name = Str(f, w) }); continue; }
                        string? fn = null, fd = null;
                        foreach (var (a, x) in Entries(Map(f, w), w, "name", "description")) if (a == "name") fn = Str(x, a); else fd = Opt(x);
                        sd.Fields.Add(new FieldDef { Name = fn ?? throw Err(f, $"{w}: a field without a name"), Description = fd });
                    }
                    break;
                case "variables":
                    foreach (var (vn, x) in Entries(Map(v, w), w))
                    {
                        if (x is YamlScalarNode) { sd.Variables.Add(new VariableDef { Name = vn, Init = Init(x) }); continue; }
                        var vd = new VariableDef { Name = vn };
                        foreach (var (a, y) in Entries(Map(x, $"{w} {vn}"), $"{w} {vn}", "value", "description"))
                            if (a == "value") vd.Init = Init(y); else vd.Description = Opt(y);
                        sd.Variables.Add(vd);
                    }
                    break;
                case "guards": sd.Guards = [.. Seq(v, w).Select(x => ReadRule(x, w, needWhen: true))]; break;
                case "steps": sd.Steps = ReadNodes(v, w); steps = true; break;
                case "asm": sd.Asm = [.. Seq(v, w).Select(x => Str(x, w))]; break;
            }
        }
        if (sd.Asm is not null && (steps || sd.Fields.Count > 0 || sd.Variables.Count > 0 || sd.Guards.Count > 0)) throw Err(n, $"script {name}: asm or steps, not both");
        if (sd.Asm is null && !steps) throw Err(n, $"script {name}: no steps");
        return sd;
    }

    private static List<ScriptNode> ReadNodes(YamlNode n, string where)
    {
        var o = new List<ScriptNode>();
        int i = 0;
        foreach (var x in Seq(n, where)) o.Add(ReadNode(x, $"{where} {i++}"));
        return o;
    }

    private static ScriptNode ReadNode(YamlNode n, string where)
    {
        var m = Map(n, where);
        var e = Entries(m, where, "id", "description", "do", "wait_until", "wait", "then", "goto", "while_waiting", "repeat").ToList();
        var keys = e.Select(x => x.Key).ToHashSet();
        int kinds = new[] { "do", "wait_until", "wait", "repeat" }.Count(keys.Contains);
        if (kinds != 1) throw Err(n, $"{where}: a step is one of do, wait_until, wait, repeat");
        if (keys.Contains("repeat"))
        {
            if (keys.Count != 1) throw Err(n, $"{where}: a repeat has only its steps");
            return new ScriptRepeat { Steps = ReadNodes(e[0].Value, where + " repeat") };
        }
        var s = new ScriptStep();
        var rule = new ScriptRule();
        foreach (var (k, v) in e)
            switch (k)
            {
                case "id": s.Id = Str(v, k); break;
                case "description": s.Description = Opt(v); break;
                case "do": s.Kind = StepKind.Do; rule.When = ScriptCond.True; rule.Then = ReadActions(v, where); break;
                case "wait_until": s.Kind = StepKind.WaitUntil; rule.When = Cond(v, where); break;
                case "then": rule.Then = ReadActions(v, where); break;
                case "goto": rule.Goto = ReadGoto(v); break;
                case "wait":
                    s.Kind = StepKind.Wait;
                    int i = 0;
                    s.Rules = [.. Seq(v, where).Select(x => ReadRule(x, $"{where} rule {i++}", needWhen: true))];
                    break;
                case "while_waiting": s.WhileWaiting = ReadActions(v, where); break;
            }
        if (s.Kind == StepKind.Wait)
        {
            if (keys.Contains("then") || keys.Contains("goto")) throw Err(n, $"{where}: then and goto of a wait go in its rules");
        }
        else
        {
            if (s.Kind == StepKind.Do && keys.Contains("then")) throw Err(n, $"{where}: a do step's actions are the do list");
            s.Rules = [rule];
        }
        return s;
    }

    private static ScriptRule ReadRule(YamlNode n, string where, bool needWhen)
    {
        var r = new ScriptRule();
        bool when = false;
        foreach (var (k, v) in Entries(Map(n, where), where, "when", "then", "goto", "steps"))
            switch (k)
            {
                case "when": r.When = Cond(v, where); when = true; break;
                case "then": r.Then = ReadActions(v, where); break;
                case "goto": r.Goto = ReadGoto(v); break;
                case "steps": r.Steps = ReadNodes(v, where + " steps"); break;
            }
        if (needWhen && !when) throw Err(n, $"{where}: a rule needs when (when: true for always)");
        if (r.Steps is not null && r.Goto.Kind != GotoKind.Default) throw Err(n, $"{where}: steps or goto, not both");
        return r;
    }

    private static ScriptGoto ReadGoto(YamlNode n)
    {
        string s = Str(n, "goto");
        return s switch
        {
            "stop" => new(GotoKind.Stop),
            "restart" => new(GotoKind.Restart),
            "next" => new(GotoKind.Next),
            "continue" => new(GotoKind.Continue),
            "break" => new(GotoKind.Break),
            _ when ScriptText.IsIdentifier(s) => new(GotoKind.Step, s),
            _ => throw Err(n, $"goto '{s}': a step id, stop, restart, next, continue or break"),
        };
    }

    private static List<ScriptAction> ReadActions(YamlNode n, string where)
    {
        if (n is YamlSequenceNode sq) return [.. sq.Children.Select(x => ReadAction(x, where))];
        return [ReadAction(n, where)];
    }

    private static ScriptAction ReadAction(YamlNode n, string where)
    {
        if (n is YamlMappingNode m)
        {
            string? call = null;
            var args = new List<ScriptOperand>();
            foreach (var (k, v) in Entries(m, where, "call", "args"))
                if (k == "call") call = Str(v, k);
                else args = [.. Seq(v, where).Select(x => Wrap(x, () => ScriptText.ParseOperand(Str(x, where))))];
            return new ScriptAction("call", args, call ?? throw Err(n, $"{where}: call needs a script"));
        }
        return Wrap(n, () => ScriptText.ParseAction(Str(n, where)));
    }

    private static ScriptCond Cond(YamlNode n, string where) => Wrap(n, () => ScriptText.ParseCond(Str(n, where)));

    private static ScriptValue Value(YamlNode n) => Wrap(n, () => ScriptText.ParseValue(Str(n, "value")));

    private static ScriptValue? Init(YamlNode n) => n is YamlScalarNode { Style: ScalarStyle.Plain or ScalarStyle.Any, Value: null or "" or "null" or "~" } ? null : Value(n);

    private static T Wrap<T>(YamlNode n, Func<T> f)
    {
        try { return f(); }
        catch (ScriptError e) when (!e.Message.StartsWith("line ", StringComparison.Ordinal)) { throw Err(n, e.Message); }
    }

    private static void Key(string key, YamlNode n, string what)
    {
        if (!ScriptText.IsKey(key)) throw Err(n, $"{what} '{key}': a key is letters, digits, _ . - ");
    }

    private static byte[] Hex8(YamlNode n, string key)
    {
        string s = Str(n, "id").Replace(" ", "", StringComparison.Ordinal);
        try
        {
            var b = Convert.FromHexString(s);
            if (b.Length == 8) return b;
        }
        catch (FormatException) { }
        throw Err(n, $"actor {key}: id is 16 hex digits (the placement's 8-byte name)");
    }

    private static ScriptError Err(YamlNode n, string m) => new($"line {n.Start.Line}: {m}");

    private static YamlMappingNode Map(YamlNode n, string where) =>
        n as YamlMappingNode ?? (n is YamlScalarNode { Value: null or "" } ? new YamlMappingNode() : throw Err(n, $"{where}: expected a mapping"));

    private static IEnumerable<YamlNode> Seq(YamlNode n, string where) =>
        n is YamlSequenceNode s ? s.Children : n is YamlScalarNode { Value: null or "" } ? [] : throw Err(n, $"{where}: expected a list");

    private static string Str(YamlNode n, string where) =>
        n is YamlScalarNode { Value: { } v } ? v : throw Err(n, $"{where}: expected a scalar");

    private static string? Opt(YamlNode n) => n is YamlScalarNode { Value: { } v } && !(v is "" or "~" or "null" && ((YamlScalarNode)n).Style is ScalarStyle.Plain) ? v : null;

    private static int Int(YamlNode n, string where) =>
        int.TryParse(Str(n, where), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int v) ? v : throw Err(n, $"{where}: expected a number");

    /// <summary>A mapping's entries in order, rejecting keys outside <paramref name="allowed"/> (when given).</summary>
    private static IEnumerable<(string Key, YamlNode Value)> Entries(YamlMappingNode m, string where, params string[] allowed)
    {
        foreach (var (k, v) in m.Children)
        {
            string key = Str(k, where);
            if (allowed.Length > 0 && !allowed.Contains(key)) throw Err(k, $"{where}: unknown key '{key}' (one of {string.Join(", ", allowed)})");
            yield return (key, v);
        }
    }
}
