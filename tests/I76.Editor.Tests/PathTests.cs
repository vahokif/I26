using System.Buffers.Binary;
using System.Numerics;
using I76.Editor;
using I76.Editor.Paths;
using I76.Formats.Fsm;
using I76.Formats.Mission;
using I76.Formats.Mission.Edit;
using Xunit;

namespace I76.Editor.Tests;

/// <summary>The path tool's model: the script's nav-path table edited, undone, saved and read back.</summary>
public class PathTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "i26-editor-paths-" + Guid.NewGuid().ToString("N"));
    private string Addon => Path.Combine(_root, "addon");

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private (TerrainDocument Doc, string Path) Open()
    {
        var (_, files) = NewMap.CreateFlat(Addon, "paths1", "Paths One", 2, 10, nitro: true);
        string mission = files.First(f => f.EndsWith(".cbt"));
        var project = MapProject.Load(mission);
        var doc = new TerrainDocument(project.Terrain, "paths1", "Paths One", mission, Addon) { Paths = PathStore.FromMission(File.ReadAllBytes(mission)) };
        return (doc, mission);
    }

    [Fact]
    public void APathIsDrawnEditedUndoneSavedAndReadBack()
    {
        var (doc, path) = Open();
        var store = doc.Paths!;
        Assert.False(store.HasScript);
        Assert.Empty(store.Entries);
        var undo = new UndoStack(doc);
        void Do(PathCommand c) => undo.Push(c.Apply());

        Do(PathEdits.Add(store, store.FreeLabel(), [new(600, 9300), new(700, 9300), new(800, 9400)]));
        Assert.True(doc.Modified);
        var g = store.Groups().Single();
        Assert.Equal("path1", g.Label);
        Do(PathEdits.MovePoint(store, g, 1, new Vector2(700, 9250)));
        Do(PathEdits.InsertPoint(store, store.Groups()[0], 1, new Vector2(650, 9280)));
        Do(PathEdits.Rename(store, store.Groups()[0], "convoy"));
        g = store.Groups().Single();
        Assert.Equal("convoy", g.Label);
        Assert.Equal([new(600, 0, 9300), new(650, 0, 9280), new(700, 0, 9250), new(800, 0, 9400)], g.Points);
        Do(PathEdits.DeletePoint(store, g, 3)!);
        Assert.Equal(3, store[0].Points.Count);

        // undo and redo walk the same steps
        Assert.True(undo.Undo());
        Assert.Equal(4, store[0].Points.Count);
        Assert.True(undo.Undo());
        Assert.Equal("path1", store[0].Name);
        Assert.True(undo.Redo());
        Assert.True(undo.Redo());
        Assert.Equal(("convoy", 3), (store[0].Name, store[0].Points.Count));

        doc.Save();
        Assert.False(doc.Modified);
        var back = PathStore.FromMission(File.ReadAllBytes(path));
        Assert.True(back[0].SameAs(store[0]));
        var prog = new MissionFile(path).ReadFsmProgram()!;
        Assert.Equal("convoy", prog.Paths.Single().Name);
        Assert.Equal(new Vector3(650, 0, 9280), prog.Paths[0].Points[1]);
        Assert.Empty(prog.Code);   // a melee map: the paths only, no script

        // deleted (no script holds it) and saved: the map's script chunk is empty again
        Do(PathEdits.Delete(store, store.Groups()[0])!);
        doc.Save();
        Assert.Empty(new MissionFile(path).FsmPayload());
        Assert.True(undo.Undo());
        Assert.Single(store.Entries);
    }

    [Fact]
    public void LabelsAreCheckedAndAPathKeepsAPoint()
    {
        var (doc, _) = Open();
        var store = doc.Paths!;
        PathEdits.Add(store, "nav1", [new(0, 0), new(10, 0)]).Apply();
        PathEdits.Add(store, "nav2", [new(0, 50)]).Apply();
        var groups = store.Groups();
        Assert.Null(store.LabelProblem("nav1", groups[0]));                     // its own label
        Assert.Contains("another path", store.LabelProblem("NAV1", groups[1]));
        Assert.NotNull(store.LabelProblem("", groups[1]));
        Assert.NotNull(store.LabelProblem(new string('x', 40), groups[1]));
        Assert.Null(store.LabelProblem(new string('x', 39), groups[1]));
        Assert.Equal("nav3", store.FreeLabel("nav"));
        Assert.Null(PathEdits.DeletePoint(store, groups[1], 0));                  // the last point stays
    }

    [Fact]
    public void PickingFindsAPointBeforeALine()
    {
        var store = new PathStore([new PathEntry("a", [new(0, 0, 0), new(100, 0, 0)]), new PathEntry("b", [new(50, 0, 6), new(50, 0, 100)])]);
        var groups = store.Groups();
        var hit = PathEdits.Pick(groups, new Vector2(51, 4), 4, 6)!.Value;
        Assert.Equal(("b", 0, -1), (hit.Path.Label, hit.Point, hit.Segment));
        hit = PathEdits.Pick(groups, new Vector2(30, 2), 4, 6)!.Value;
        Assert.Equal(("a", -1, 1), (hit.Path.Label, hit.Point, hit.Segment));
        Assert.True(Vector2.Distance(new Vector2(30, 0), hit.OnLine) < 1e-3f);
        Assert.Null(PathEdits.Pick(groups, new Vector2(30, 40), 4, 6));
    }

    /// <summary>
    /// A script's FSM payload: prototypes goto and isWithinNav, one actor, the paths, one thread over (actor, path) data cells whose
    /// code orders goto along the path in data cell 1, then tests isWithinNav of entry 1 as an immediate.
    /// </summary>
    private static byte[] Script(IReadOnlyList<PathEntry> paths, int gotoPath)
    {
        var o = new List<byte>();
        void U(int v) { var b = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(b, v); o.AddRange(b); }
        void Name(string s, int size) { var b = new byte[size]; System.Text.Encoding.Latin1.GetBytes(s).CopyTo(b, 0); o.AddRange(b); }
        U(2); Name("goto", 40); Name("isWithinNav", 40);
        U(1); Name("user", 40); o.AddRange(new byte[8]);
        U(0);
        var table = FsmPaths.Write(new byte[28], paths);   // empty tables around the paths: the count and the entries are in the middle
        o.AddRange(table.AsSpan(12, table.Length - 24).ToArray());
        U(1); U(0); U(2); U(0); U(1); for (int k = 2; k < 40; k++) U(0);   // thread: start 0, depth 2, stack (data 0, data 1)
        U(2); U(0); U(gotoPath);                                            // data: actor 0, the path's index
        (int Op, int Arg)[] code = [(5, -3), (5, -2), (1, 45), (4, 1), (13, 0), (7, 1), (1, 1), (4, 1), (5, -3), (1, 30), (4, 2), (13, 1), (7, 2), (12, 2)];
        U(code.Length);
        foreach (var (op, arg) in code) { U(op); U(arg); }
        return [.. o];
    }

    [Fact]
    public void TheScriptsPathsStayInTheirPlacesAndCopiesAreEditedTogether()
    {
        PathEntry[] table = [new("road", [new(0, 0, 0), new(100, 0, 0)]), new("gate", [new(500, 0, 500), new(510, 0, 500)]), new("road", [new(0, 0, 0), new(100, 0, 0)])];
        var payload = Script(table, gotoPath: 2);
        var program = new MissionFile("", Mission(payload)).ReadFsmProgram()!;
        Assert.True(program.EndsExactly);
        var use = FsmPathUse.Of(program);
        Assert.True(use.Complete);
        Assert.Equal([(2, "goto"), (1, "isWithinNav")], use.Uses.Select(u => (u.Path, u.Op)).ToArray());

        var store = PathStore.FromMission(Mission(payload));
        Assert.True(store.HasScript);
        var groups = store.Groups();
        Assert.Equal(["road", "gate"], groups.Select(g => g.Label));
        Assert.Equal([0, 2], groups[0].Entries);
        Assert.Equal(["goto"], store.UsesOf(groups[0]).Select(u => u.Op));

        // a move reaches every copy; the script's paths cannot be deleted, a new one can
        PathEdits.MovePoint(store, groups[0], 1, new Vector2(120, 5)).Apply();
        Assert.Equal(new Vector3(120, 0, 5), store[0].Points[1]);
        Assert.Equal(new Vector3(120, 0, 5), store[2].Points[1]);
        Assert.Equal(2, store.Groups().Count);
        Assert.NotNull(store.WhyNotDelete(store.Groups()[1]));
        Assert.Null(PathEdits.Delete(store, store.Groups()[1]));
        Assert.Throws<InvalidOperationException>(() => store.RemoveAt(1));
        PathEdits.Add(store, "extra", [new(1, 2)]).Apply();
        var extra = store.Groups().Single(g => g.Label == "extra");
        Assert.Null(store.WhyNotDelete(extra));
        Assert.Equal(3, extra.First);

        // the payload the playtest reads: the edited table, the script around it as it was
        var edited = new MissionFile("", Mission(store.Payload())).ReadFsmProgram()!;
        Assert.True(edited.EndsExactly);
        Assert.Equal(program.Code, edited.Code);
        Assert.Equal(program.Data, edited.Data);
        Assert.Equal(new Vector3(120, 0, 5), edited.Paths[2].Points[1]);
        Assert.Equal("extra", edited.Paths[3].Name);
    }

    /// <summary>A mission file of nothing but an ADEF holding the script.</summary>
    private static byte[] Mission(byte[] fsm)
    {
        var tree = new BwdTree([new BwdNode("BWD2", new byte[4]), new BwdNode("ADEF", [new BwdNode("AREV", [4, 0, 0, 0]), new BwdNode("FSM\0", fsm), new BwdNode("EXIT", Array.Empty<byte>())]), new BwdNode("EXIT", Array.Empty<byte>())]);
        return tree.ToBytes();
    }
}
