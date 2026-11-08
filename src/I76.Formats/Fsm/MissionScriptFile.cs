using I76.Formats.Mission.Edit;

namespace I76.Formats.Fsm;

/// <summary>Mission files (.msn, .lvl, ...) and their script's source: decompile one, compile a source into one.</summary>
public static class MissionScriptFile
{
    /// <summary>The FSM payload of a mission file (empty when it has none).</summary>
    public static byte[] Payload(byte[] file) =>
        BwdTree.Parse(file).Subs("ADEF", "FSM\0").Select(s => s.Node.Payload).FirstOrDefault() ?? [];

    public static MissionScriptDecompiler.Result Decompile(byte[] file, string? mission, ScriptNames? names = null) =>
        MissionScriptDecompiler.Decompile(Payload(file), mission, names);

    /// <summary>The YAML of a mission file's script.</summary>
    public static string ToYaml(byte[] file, string? mission, ScriptNames? names = null) =>
        MissionScriptYaml.Write(Decompile(file, mission, names).Script);

    /// <summary>
    /// The mission file with its FSM chunk compiled from <paramref name="doc"/>; every other byte as it was. A file without an
    /// FSM chunk gets one in ADEF before its EXIT.
    /// </summary>
    public static byte[] Compile(MissionScriptSource doc, byte[] file)
    {
        var tree = BwdTree.Parse(file);
        var node = tree.Subs("ADEF", "FSM\0").Select(s => s.Node).FirstOrDefault();
        var placements = tree.Subs("ODEF", "OBJ\0").Select(s => s.Node.Payload).Where(p => p.Length >= 8).Select(p => p[..8]).ToList();
        var payload = MissionScriptCompiler.Compile(doc, node?.Payload ?? [], placements).ToBytes();
        if (node is not null)
        {
            if (payload.AsSpan().SequenceEqual(node.Payload)) return file;
            node.Payload = payload;
        }
        else
        {
            if (payload.Length == 0) return file;
            var adef = tree.Chunks.FirstOrDefault(c => c.Tag == "ADEF" && c.Children is not null) ?? throw new ScriptError("the mission has no ADEF chunk to hold a script");
            int exit = adef.Children!.FindIndex(c => c.Tag == "EXIT");
            adef.Children.Insert(exit < 0 ? adef.Children.Count : exit, new BwdNode("FSM\0", payload));
        }
        return tree.ToBytes();
    }

    public static byte[] Compile(string yaml, byte[] file) => Compile(MissionScriptYaml.Read(yaml), file);
}
