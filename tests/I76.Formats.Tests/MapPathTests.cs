using System.Numerics;
using I76.Formats.Fsm;
using I76.Formats.Mission;
using I76.Formats.Mission.Edit;
using I76.Formats.Vfs;
using Xunit;
using Xunit.Abstractions;

namespace I76.Formats.Tests;

/// <summary>The mission scripts' nav-path tables: read, written back, edited, and which entries the scripts name (the map editor's path tool).</summary>
public class MapPathTests(ITestOutputHelper output)
{
    private static IEnumerable<string> Missions()
    {
        foreach (var v in new[] { "I76_INSTALL", "I76_NITRO_INSTALL" })
        {
            if (Environment.GetEnvironmentVariable(v) is not { Length: > 0 } root || !Directory.Exists(root)) continue;
            foreach (var d in Directory.EnumerateDirectories(root).Where(d => Path.GetFileName(d).ToLowerInvariant() is "miss16" or "addon"))
                foreach (var f in Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories).Where(f => GameInstall.IsMissionFile(f) && MapFileSet.IsStockMission(f)).OrderBy(f => f, StringComparer.Ordinal))
                    yield return f;
        }
    }

    private static string? Install => Environment.GetEnvironmentVariable("I76_INSTALL") is { Length: > 0 } d && Directory.Exists(d) ? d : null;

    [Fact]
    public void EveryStockPathTableIsWrittenBackByteForByte()
    {
        int files = 0, paths = 0, scripts = 0;
        var bad = new List<string>();
        foreach (var f in Missions())
        {
            var data = File.ReadAllBytes(f);
            var doc = MapDocument.Parse(data);
            var read = doc.Paths;
            var reader = new MissionFile(f).ReadFsmProgram()?.Paths ?? [];
            if (read.Count != reader.Count || read.Where((p, i) => p.Name != reader[i].Name || !p.Points.SequenceEqual(reader[i].Points)).Any()) bad.Add(f + ": reads differently");
            // written from the labels and points alone (no kept label bytes), the file comes back as it was
            var fresh = read.Select(p => new PathEntry(p.Name, p.Points)).ToList();
            doc.Paths.Clear();
            doc.Paths.AddRange(fresh);
            if (!doc.ToBytes().AsSpan().SequenceEqual(data)) bad.Add(f + ": written differently");
            var back = MapDocument.Parse(doc.ToBytes()).Paths;
            if (back.Count != fresh.Count || back.Where((p, i) => !p.SameAs(fresh[i])).Any()) bad.Add(f + ": reads back differently");
            files++; paths += read.Count; if (read.Count > 0) scripts++;
        }
        output.WriteLine($"{files} missions, {scripts} with paths, {paths} paths");
        Assert.Empty(bad);
    }

    [Fact]
    public void AnEditedPathSurvivesSaveAndLoadAndLeavesTheScriptAlone()
    {
        if (Install is null) return;
        var f = new GameInstall(Install).FindMission("t01")!;
        var data = File.ReadAllBytes(f);
        var before = new MissionFile(f).ReadFsmProgram()!;
        var doc = MapDocument.Parse(data);
        int k = doc.Paths.FindIndex(p => p.Points.Count >= 2);
        var moved = doc.Paths[k].Points.Select(p => p + new Vector3(12.5f, 0, -7.25f)).ToList();
        moved.Insert(1, new Vector3(1234.5f, 0, 2345.25f));
        doc.Paths[k] = doc.Paths[k].With(name: "renamed", points: moved);
        doc.Paths.Add(new PathEntry("mynew", [new Vector3(100, 0, 200), new Vector3(300, 0, 400)]));
        var saved = doc.ToBytes();

        var after = new MissionFile(Path.GetFileName(f), saved).ReadFsmProgram()!;
        Assert.True(after.EndsExactly);
        Assert.Equal(before.Paths.Count + 1, after.Paths.Count);
        Assert.Equal("renamed", after.Paths[k].Name);
        Assert.Equal(moved, after.Paths[k].Points);
        Assert.Equal("mynew", after.Paths[^1].Name);
        for (int i = 0; i < before.Paths.Count; i++)
            if (i != k) { Assert.Equal(before.Paths[i].Name, after.Paths[i].Name); Assert.Equal(before.Paths[i].Points, after.Paths[i].Points); }
        // the script around the table is the same, byte for byte
        Assert.Equal(before.Prototypes, after.Prototypes);
        Assert.Equal(before.Sounds, after.Sounds);
        Assert.Equal(before.Data, after.Data);
        Assert.Equal(before.Code, after.Code);
        Assert.Equal(before.Threads.Count, after.Threads.Count);
        // and the document reads its own file back
        var again = MapDocument.Parse(saved);
        Assert.Equal(after.Paths.Count, again.Paths.Count);
        Assert.Equal(saved, again.ToBytes());
    }

    [Fact]
    public void PathsGoIntoAMapWithoutAScript()
    {
        var f = Missions().FirstOrDefault(m => new MissionFile(m).FsmPayload().Length == 0);
        if (f is null) return;
        var doc = MapDocument.Load(f);
        Assert.Empty(doc.Paths);
        doc.Paths.Add(new PathEntry("loop", [new Vector3(10, 0, 20), new Vector3(30, 0, 40), new Vector3(50, 0, 10)]));
        var saved = doc.ToBytes();
        var msn = new MissionFile(Path.GetFileName(f), saved);
        var prog = msn.ReadFsmProgram()!;
        Assert.True(prog.EndsExactly);
        Assert.Empty(prog.Code);
        Assert.Empty(prog.Threads);
        Assert.Single(prog.Paths);
        Assert.Equal(new Vector3(30, 0, 40), prog.Paths[0].Points[1]);
        Assert.Single(msn.GetFsmTables()!.Paths);
        // removed again, the script chunk is empty again: the file is what it was
        doc.Paths.Clear();
        Assert.Equal(File.ReadAllBytes(f), doc.ToBytes());
    }

    [Fact]
    public void TheOverrideIsWhatTheReadersSee()
    {
        if (Install is null) return;
        var f = new GameInstall(Install).FindMission("t01")!;
        var msn = new MissionFile(f);
        var entries = FsmPaths.Read(msn.FsmPayload());
        entries[0] = entries[0].With(points: [new Vector3(1, 0, 2), new Vector3(3, 0, 4)]);
        msn.FsmOverride = FsmPaths.Write(msn.FsmPayload(), entries);
        Assert.Equal(new Vector3(3, 0, 4), msn.ReadFsmProgram()!.Paths[0].Points[1]);
        Assert.Equal(new Vector3(1, 0, 2), msn.GetFsmTables()!.Paths[0].Points[0]);
    }

    [Fact]
    public void TheAnalysisFindsWhereTheStockScriptsNameTheirPaths()
    {
        int scripts = 0, entries = 0, found = 0, uses = 0, complete = 0;
        var past = new List<string>();
        foreach (var f in Missions())
        {
            if (new MissionFile(f).ReadFsmProgram() is not { Code.Length: > 0 } p) continue;
            var use = FsmPathUse.Of(p);
            scripts++; entries += p.Paths.Count; uses += use.Uses.Count;
            if (use.Complete) complete++;
            found += Enumerable.Range(0, p.Paths.Count).Count(i => use.Of(i).Any());
            // only code a thread reaches counts: a dead routine in S06 and S07 names entries past the table's end
            past.AddRange(use.Uses.Where(u => u.Path >= p.Paths.Count).Select(u => $"{Path.GetFileName(f)} {u.Op} {u.Path} of {p.Paths.Count}"));
        }
        output.WriteLine($"{scripts} scripts, {complete} traced completely; {entries} path entries, {found} found named, {uses} places");
        Assert.Empty(past);
        if (scripts > 0) Assert.True(found > entries * 0.7, $"{found} of {entries}");   // the rest the scripts pick through locals
    }
}
