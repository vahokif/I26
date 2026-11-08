using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace I76.Formats.Fsm;

/// <summary>
/// A names sidecar (docs/mission-scripts.md, "Names"): better names, aliases and descriptions for what the decompiler would
/// otherwise call thread7, script12, g17, f0, v0, step4, keyed by those generic names, for one mission's FSM chunk (its
/// <see cref="Fsm"/> hash). Scripts may instead be keyed by their body's hash (<c>@1a2b3c4d5e6f</c>), which names that routine in
/// every mission that holds it (the libraries every mission starts with). Names only rename the source: the bytecode compiled
/// from it is the same.
/// </summary>
public sealed class ScriptNames
{
    public class Note
    {
        public string? Name { get; set; }
        public string? Alias { get; set; }
        public string? Description { get; set; }
        public bool IsEmpty => Name is null && Alias is null && Description is null;
    }

    public sealed class ScriptEntry : Note
    {
        public Dictionary<string, Note> Fields { get; } = [];
        public Dictionary<string, Note> Variables { get; } = [];
        public Dictionary<string, Note> Steps { get; } = [];
    }

    public string? Mission { get; set; }
    /// <summary>The FSM chunk these names are for (<see cref="MissionScriptDecompiler.ChunkHash"/>); null: any.</summary>
    public string? Fsm { get; set; }
    public Dictionary<string, Note> Threads { get; } = [];
    /// <summary>By generic name (script12) or by body hash (@...).</summary>
    public Dictionary<string, ScriptEntry> Scripts { get; } = [];
    public Dictionary<string, Note> Globals { get; } = [];
    public Dictionary<string, Note> Actors { get; } = [];
    public Dictionary<string, Note> Paths { get; } = [];
    public Dictionary<string, Note> Sounds { get; } = [];

    /// <summary>The note for a script: by its generic name (when the mission's notes apply), else by its body hash.</summary>
    public ScriptEntry? ScriptNote(string generic, string bodyHash, bool missionNotes) =>
        missionNotes && Scripts.TryGetValue(generic, out var n) ? n : Scripts.GetValueOrDefault("@" + bodyHash);

    /// <summary>
    /// Reads a sidecar. A note is a mapping (name, alias, description; a script's also fields, variables, steps, each a mapping
    /// of notes) or a plain string: the name, except for actors, paths and sounds, whose string is the description.
    /// </summary>
    public static ScriptNames Read(string yaml)
    {
        var ys = new YamlStream();
        try { ys.Load(new StringReader(yaml)); }
        catch (YamlException ex) { throw new ScriptError($"names, line {ex.Start.Line}: {ex.Message}"); }
        var o = new ScriptNames();
        if (ys.Documents.Count == 0 || ys.Documents[0].RootNode is not YamlMappingNode top) return o;
        foreach (var (kn, v) in top.Children)
        {
            string k = ((YamlScalarNode)kn).Value ?? "";
            switch (k)
            {
                case "mission": o.Mission = Scalar(v); break;
                case "fsm": o.Fsm = Scalar(v); break;
                case "threads": Notes(v, o.Threads, nameFirst: true); break;
                case "globals": Notes(v, o.Globals, nameFirst: true); break;
                case "actors": Notes(v, o.Actors, nameFirst: false); break;
                case "paths": Notes(v, o.Paths, nameFirst: false); break;
                case "sounds": Notes(v, o.Sounds, nameFirst: false); break;
                case "scripts":
                    if (v is not YamlMappingNode sm) break;
                    foreach (var (sk, sv) in sm.Children)
                    {
                        var e = new ScriptEntry();
                        Fill(e, sv, nameFirst: true);
                        if (sv is YamlMappingNode m)
                            foreach (var (fk, fv) in m.Children)
                                switch (((YamlScalarNode)fk).Value)
                                {
                                    case "fields": Notes(fv, e.Fields, nameFirst: true); break;
                                    case "variables": Notes(fv, e.Variables, nameFirst: true); break;
                                    case "steps": Notes(fv, e.Steps, nameFirst: true); break;
                                }
                        o.Scripts[((YamlScalarNode)sk).Value ?? ""] = e;
                    }
                    break;
                default: throw new ScriptError($"names, line {kn.Start.Line}: unknown key '{k}'");
            }
        }
        return o;
    }

    private static string? Scalar(YamlNode n) => n is YamlScalarNode { Value: { Length: > 0 } s } ? s : null;

    private static void Notes(YamlNode n, Dictionary<string, Note> into, bool nameFirst)
    {
        if (n is not YamlMappingNode m) return;
        foreach (var (k, v) in m.Children)
        {
            var note = new Note();
            Fill(note, v, nameFirst);
            into[((YamlScalarNode)k).Value ?? ""] = note;
        }
    }

    private static void Fill(Note note, YamlNode v, bool nameFirst)
    {
        if (v is YamlScalarNode)
        {
            if (nameFirst) note.Name = Scalar(v); else note.Description = Scalar(v);
            return;
        }
        if (v is not YamlMappingNode m) return;
        foreach (var (k, x) in m.Children)
            switch (((YamlScalarNode)k).Value)
            {
                case "name": note.Name = Scalar(x); break;
                case "alias": note.Alias = Scalar(x); break;
                case "description": note.Description = Scalar(x); break;
            }
    }

    /// <summary>A sidecar to fill in: every name the decompiler made for <paramref name="doc"/>, with its chunk's hash and the scripts' body hashes as comments.</summary>
    public static string Template(MissionScriptDecompiler.Result r, string fsmHash)
    {
        var d = r.Script;
        var sb = new System.Text.StringBuilder();
        sb.Append("# names for ").Append(d.Mission ?? "a mission").Append("'s script (docs/mission-scripts.md, \"Names\"); fill in what is known, delete the rest\n");
        if (d.Mission is { } mn) sb.Append("mission: ").Append(mn).Append('\n');
        sb.Append("fsm: ").Append(fsmHash).Append('\n');
        sb.Append("threads:\n");
        foreach (var t in d.Threads) sb.Append("  ").Append(t.Name).Append(": {}   # runs ").Append(t.Script).Append('\n');
        sb.Append("scripts:\n");
        for (int k = 0; k < d.Scripts.Count; k++)
        {
            var s = d.Scripts[k];
            var rt = r.Routines[k];
            sb.Append("  ").Append(s.Name).Append(":   # or @").Append(r.BodyHashes[k]).Append(" for this routine in every mission\n");
            sb.Append("    name:\n");
            if (s.Fields.Count > 0) sb.Append("    fields: {").Append(string.Join(", ", s.Fields.Select(f => f.Name + ": "))).Append("}\n");
            if (s.Variables.Count > 0) sb.Append("    variables: {").Append(string.Join(", ", s.Variables.Select(f => f.Name + ": "))).Append("}\n");
            if (rt.States.Count > 0) sb.Append("    steps: {").Append(string.Join(", ", Enumerable.Range(0, rt.States.Count).Select(i => $"step{i}: "))).Append("}\n");
        }
        sb.Append("globals:\n");
        foreach (var g in d.Globals) sb.Append("  ").Append(g.Name).Append(": {}   # ").Append(g.Value).Append('\n');
        sb.Append("actors:\n");
        foreach (var a in d.Actors) sb.Append("  ").Append(a.Key).Append(": {}\n");
        sb.Append("sounds:\n");
        foreach (var s in d.Sounds) sb.Append("  ").Append(s.Key).Append(": {}   # ").Append(s.File).Append('\n');
        return sb.ToString();
    }

    /// <summary>Several sidecars as one; a later one's notes win.</summary>
    public static ScriptNames Merge(IEnumerable<ScriptNames> all)
    {
        var o = new ScriptNames();
        foreach (var n in all)
        {
            o.Mission ??= n.Mission;
            if (n.Fsm is not null) o.Fsm = n.Fsm;
            foreach (var (src, dst) in new[] { (n.Threads, o.Threads), (n.Globals, o.Globals), (n.Actors, o.Actors), (n.Paths, o.Paths), (n.Sounds, o.Sounds) })
                foreach (var (k, v) in src) dst[k] = v;
            foreach (var (k, v) in n.Scripts) o.Scripts[k] = v;
        }
        return o;
    }
}
