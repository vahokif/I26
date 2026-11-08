using I76.Formats.Fsm;
using I76.Formats.Vfs;

namespace I76.Cli;

/// <summary>
/// The mission-script source form (docs/mission-scripts.md):
///   i76 fsm-decompile MISSION [--names FILE]... [--out FILE.yaml] [--names-template FILE]
///   i76 fsm-compile FILE.yaml --into MISSION_FILE --out NEW_FILE
///   i76 fsm-roundtrip [--install DIR]... [--verbose]      every scripted mission of the install(s): decompile, compile, compare
/// MISSION is a file path or a mission name the install finds.
/// </summary>
public static class FsmScriptCommand
{
    public static int Run(List<string> argv)
    {
        try
        {
            var installs = Opts(argv, "--install");
            string? installDir = installs.LastOrDefault() ?? (Environment.GetEnvironmentVariable("I76_INSTALL") is { Length: > 0 } e ? e : null);
            return argv[0] switch
            {
                "fsm-decompile" => Decompile(argv, installDir),
                "fsm-compile" => Compile(argv),
                _ => RoundTrip(argv, installs),
            };
        }
        catch (Exception e) when (e is ScriptError or InvalidDataException or FileNotFoundException or DirectoryNotFoundException)
        {
            Console.Error.WriteLine($"{argv[0]}: {e.Message}");
            return 1;
        }
    }

    private static List<string> Opts(List<string> argv, string name)
    {
        var o = new List<string>();
        for (int i = argv.IndexOf(name); i >= 0; i = argv.IndexOf(name))
        {
            if (i + 1 >= argv.Count) throw new ScriptError($"{name} needs a value");
            o.Add(argv[i + 1]);
            argv.RemoveRange(i, 2);
        }
        return o;
    }

    private static string FindMission(string name, string? installDir)
    {
        if (File.Exists(name)) return name;
        if (installDir is not null && new GameInstall(installDir).FindMission(name) is { } p) return p;
        throw new FileNotFoundException($"no mission {name} (a file path, or a mission name with --install or I76_INSTALL)");
    }

    private static int Decompile(List<string> argv, string? installDir)
    {
        var names = Opts(argv, "--names");
        var outFile = Opts(argv, "--out").LastOrDefault();
        var template = Opts(argv, "--names-template").LastOrDefault();
        if (argv.Count < 2) throw new ScriptError("usage: fsm-decompile MISSION [--names FILE]... [--out FILE.yaml] [--names-template FILE]");
        var path = FindMission(argv[1], installDir);
        var file = File.ReadAllBytes(path);
        var payload = MissionScriptFile.Payload(file);
        var sidecar = names.Count == 0 ? null : ScriptNames.Merge(names.Select(n => ScriptNames.Read(File.ReadAllText(n))));
        var r = MissionScriptDecompiler.Decompile(payload, Path.GetFileNameWithoutExtension(path).ToUpperInvariant(), sidecar);
        foreach (var w in r.Warnings) Console.Error.WriteLine("warning: " + w);
        var yaml = MissionScriptYaml.Write(r.Script);
        if (template is not null) File.WriteAllText(template, ScriptNames.Template(r, MissionScriptDecompiler.ChunkHash(payload)));
        if (outFile is null) Console.Write(yaml);
        else File.WriteAllText(outFile, yaml);
        return 0;
    }

    private static int Compile(List<string> argv)
    {
        var into = Opts(argv, "--into").LastOrDefault();
        var outFile = Opts(argv, "--out").LastOrDefault();
        if (argv.Count < 2 || into is null || outFile is null) throw new ScriptError("usage: fsm-compile FILE.yaml --into MISSION_FILE --out NEW_FILE");
        var src = File.ReadAllText(argv[1]);
        var file = File.ReadAllBytes(into);
        var bytes = MissionScriptFile.Compile(src, file);
        if (Path.GetFullPath(outFile) == Path.GetFullPath(into)) throw new ScriptError("--out must not be the --into file");
        File.WriteAllBytes(outFile, bytes);
        int changed = 0;
        for (int i = 0; i < Math.Min(bytes.Length, file.Length); i++) if (bytes[i] != file[i]) changed++;
        Console.WriteLine(bytes.AsSpan().SequenceEqual(file) ? $"{outFile}: identical to {into}" : $"{outFile}: {bytes.Length} bytes ({bytes.Length - file.Length:+#;-#;0} vs {into}), the FSM chunk recompiled");
        return 0;
    }

    /// <summary>What a routine's steps need: plain steps only, nesting (repeat, branches), explicit gotos.</summary>
    public static string Shape(ScriptDef s)
    {
        if (s.Asm is not null) return "asm";
        bool nested = false, gotos = s.Guards.Any(g => g.Goto.Kind == GotoKind.Step);
        void Walk(List<ScriptNode> nodes)
        {
            foreach (var n in nodes)
                switch (n)
                {
                    case ScriptRepeat r: nested = true; Walk(r.Steps); break;
                    case ScriptStep st:
                        foreach (var ru in st.Rules)
                        {
                            if (ru.Goto.Kind == GotoKind.Step) gotos = true;
                            if (ru.Steps is { } a) { nested = true; Walk(a); }
                        }
                        break;
                }
        }
        Walk(s.Steps);
        return gotos ? "goto" : nested ? "nested" : "steps";
    }

    private static int RoundTrip(List<string> argv, List<string> dirs)
    {
        bool verbose = argv.Remove("--verbose");
        if (dirs.Count == 0)
            foreach (var v in new[] { "I76_INSTALL", "I76_NITRO_INSTALL" })
                if (Environment.GetEnvironmentVariable(v) is { Length: > 0 } n && !dirs.Contains(n)) dirs.Add(n);
        if (dirs.Count == 0) throw new ScriptError("no install: --install DIR (repeatable), or I76_INSTALL / I76_NITRO_INSTALL");
        int files = 0, bad = 0;
        long instrs = 0;
        var shapes = new Dictionary<string, (int Scripts, int Threads)>();
        foreach (var dir in dirs)
        {
            var install = new GameInstall(dir);
            foreach (var sub in new[] { "miss16", "miss8" })
            {
                if (install.FindDir(sub) is not { } md) continue;
                foreach (var path in Directory.EnumerateFiles(md).Order(StringComparer.OrdinalIgnoreCase))
                {
                    var ext = Path.GetExtension(path).ToLowerInvariant();
                    if (ext is ".ter" or ".pcf" or ".txt") continue;
                    byte[] file;
                    try { file = File.ReadAllBytes(path); if (MissionScriptFile.Payload(file) is not { Length: > 0 } pl || FsmChunk.Read(pl).Code.Count == 0) continue; }
                    catch (InvalidDataException) { continue; }
                    files++;
                    string rel = $"{Path.GetFileName(dir.TrimEnd('/'))}/{sub}/{Path.GetFileName(path)}";
                    try
                    {
                        var r = MissionScriptFile.Decompile(file, Path.GetFileNameWithoutExtension(path).ToUpperInvariant());
                        var yaml = MissionScriptYaml.Write(r.Script);
                        var again = MissionScriptFile.Compile(yaml, file);
                        bool same = again.AsSpan().SequenceEqual(file);
                        if (!same) bad++;
                        instrs += r.Chunk.Code.Count;
                        var counts = r.Script.Scripts.GroupBy(Shape).ToDictionary(g => g.Key, g => g.Count());
                        var threadShapes = r.Script.Threads.Select(t => r.Script.Scripts.FirstOrDefault(s => s.Name == t.Script) is { } s ? Shape(s) : "inside").GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count());
                        foreach (var (k, v) in counts) shapes[k] = (shapes.GetValueOrDefault(k).Scripts + v, shapes.GetValueOrDefault(k).Threads);
                        foreach (var (k, v) in threadShapes) shapes[k] = (shapes.GetValueOrDefault(k).Scripts, shapes.GetValueOrDefault(k).Threads + v);
                        Console.WriteLine($"{(same ? "ok  " : "DIFF")} {rel}: {r.Chunk.Code.Count} instructions, {r.Script.Scripts.Count} scripts ({string.Join(", ", counts.OrderBy(k => k.Key).Select(k => $"{k.Value} {k.Key}"))}), {r.Script.Threads.Count} threads, {yaml.Split('\n').Length} lines");
                        if (verbose && !same) Console.WriteLine(FirstDifference(MissionScriptFile.Payload(file), MissionScriptFile.Payload(again)));
                    }
                    catch (Exception e) when (e is ScriptError or InvalidDataException or InvalidOperationException or ArgumentException)
                    {
                        bad++;
                        Console.WriteLine($"FAIL {rel}: {e.Message}");
                    }
                }
            }
        }
        Console.WriteLine($"{files - bad}/{files} scripted missions round-trip byte for byte ({instrs} instructions)");
        foreach (var (k, v) in shapes.OrderBy(k => k.Key)) Console.WriteLine($"  {k,-7} {v.Scripts,5} scripts {v.Threads,5} threads");
        return bad == 0 ? 0 : 1;
    }

    private static string FirstDifference(byte[] a, byte[] b)
    {
        var x = FsmChunk.Read(a);
        var y = FsmChunk.Read(b);
        for (int i = 0; i < Math.Min(x.Code.Count, y.Code.Count); i++)
            if (x.Code[i] != y.Code[i]) return $"    code differs at {i}: {x.Code[i]} vs {y.Code[i]}";
        if (x.Code.Count != y.Code.Count) return $"    code {x.Code.Count} vs {y.Code.Count} instructions";
        if (!x.Data.SequenceEqual(y.Data)) return "    data differs";
        if (x.Threads.Count != y.Threads.Count) return $"    {x.Threads.Count} vs {y.Threads.Count} threads";
        for (int i = 0; i < x.Threads.Count; i++)
            if (x.Threads[i].StartPc != y.Threads[i].StartPc || !x.Threads[i].Cells.SequenceEqual(y.Threads[i].Cells)) return $"    thread {i} differs";
        return "    tables differ";
    }
}
