using System.Numerics;
using I76.Formats.Mission;
using I76.Formats.Mission.Edit;
using I76.Formats.Vfs;
using Xunit;
using Xunit.Abstractions;

namespace I76.Formats.Tests;

/// <summary>
/// The map writers (docs/map-format-writing.md): every stock and community mission and terrain round-trips byte for
/// byte, edits change only what they name, and new maps are maps the readers (and the games) accept. Needs
/// I76_INSTALL and/or I76_NITRO_INSTALL; the community pack (I76_CUSTOM_MAPS) is read, never written.
/// </summary>
public class MapWriterTests(ITestOutputHelper output)
{
    private static IEnumerable<string> Roots()
    {
        foreach (var v in new[] { "I76_INSTALL", "I76_NITRO_INSTALL", "I76_CUSTOM_MAPS" })
            if (Environment.GetEnvironmentVariable(v) is { Length: > 0 } d && Directory.Exists(d)) yield return d;
    }

    // the installs' stock maps (a mission the game ships, and the terrain of that name beside it), not what a player added
    // to them; the community pack all of it
    private static IEnumerable<string> Files(Func<string, bool> pick) =>
        Roots().SelectMany(r => Directory.EnumerateDirectories(r).Where(d => Path.GetFileName(d).ToLowerInvariant() is "miss16" or "miss8" or "addon")
                .SelectMany(d => Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories))
                .Where(f => r == Environment.GetEnvironmentVariable("I76_CUSTOM_MAPS") || IsStock(f)))
            .Where(pick).OrderBy(p => p, StringComparer.Ordinal);

    private static bool IsStock(string f) => MapFileSet.IsStockMission(f)
        || IsTer(f) && Directory.EnumerateFiles(Path.GetDirectoryName(f)!, Path.GetFileNameWithoutExtension(f) + ".*",
            new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive }).Any(MapFileSet.IsStockMission);

    private static bool IsMission(string p) => GameInstall.IsMissionFile(p);
    private static bool IsTer(string p) => p.EndsWith(".ter", StringComparison.OrdinalIgnoreCase);

    private static string? NitroDir => Environment.GetEnvironmentVariable("I76_NITRO_INSTALL") is { Length: > 0 } d && Directory.Exists(d) ? d : null;

    private static string StockMelee()
    {
        var dir = Directory.EnumerateDirectories(NitroDir!).First(d => Path.GetFileName(d).Equals("miss16", StringComparison.OrdinalIgnoreCase));
        return Directory.EnumerateFiles(dir).First(f => Path.GetFileName(f).Equals("N05.CBT", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void EveryMissionRoundTripsByteForByte()
    {
        if (!Roots().Any()) return;
        int n = 0, withTail = 0; var bad = new List<string>();
        foreach (var f in Files(IsMission))
        {
            var data = File.ReadAllBytes(f);
            try
            {
                var doc = MapDocument.Parse(data);
                if (doc.Tree.Tail.Length > 0) withTail++;
                if (!doc.ToBytes().AsSpan().SequenceEqual(data)) bad.Add(f + " (differs)");
                // writing twice is the same as writing once
                if (!doc.ToBytes().AsSpan().SequenceEqual(data)) bad.Add(f + " (second write differs)");
            }
            catch (Exception e) { bad.Add($"{f}: {e.Message}"); }
            n++;
        }
        output.WriteLine($"{n} mission files, {n - bad.Count} identical, {withTail} with trailing bytes kept");
        Assert.Empty(bad);
    }

    [Fact]
    public void EveryTerrainRoundTripsByteForByte()
    {
        if (!Roots().Any()) return;
        int n = 0, viaMission = 0; var bad = new List<string>();
        foreach (var f in Files(IsTer))
        {
            var data = File.ReadAllBytes(f);
            int tiles = (data.Length + MissionFile.TerrainTileBytes - 1) / MissionFile.TerrainTileBytes;
            var t = new TerrainTiles(data, tiles, new byte[MissionFile.ZmapGrid * MissionFile.ZmapGrid]);
            if (!t.ToBytes().AsSpan().SequenceEqual(data)) bad.Add(f);
            n++;
        }
        // through the mission that names it, tile count and all (a short or long .ter is kept as it is)
        foreach (var f in Files(IsMission))
        {
            MapDocument doc;
            try { doc = MapDocument.Load(f); } catch { continue; }
            if (doc.TerrainName is not { } tn) continue;
            var dir = Path.GetDirectoryName(f)!;
            var ter = new[] { dir, Path.Combine(dir, "..", "addon"), Path.Combine(dir, "..", "ADDON") }.Where(Directory.Exists)
                .SelectMany(d => Directory.EnumerateFiles(d)).FirstOrDefault(p => string.Equals(Path.GetFileName(p), tn, StringComparison.OrdinalIgnoreCase));
            if (ter is null) continue;
            var data = File.ReadAllBytes(ter);
            var proj = MapProject.Open(Path.GetFileNameWithoutExtension(f), Path.GetExtension(f), doc, data);
            if (!proj.Terrain.ToBytes().AsSpan().SequenceEqual(data)) bad.Add(f + " via mission");
            viaMission++;
        }
        output.WriteLine($"{n} terrain files, {viaMission} more through their missions; {bad.Count} differ");
        Assert.Empty(bad);
    }

    [Fact]
    public void TheModelReadsWhatTheReadersRead()
    {
        if (NitroDir is null) return;
        var path = StockMelee();
        var doc = MapDocument.Load(path);
        var msn = new MissionFile(path);
        var objs = msn.PlacedObjects();
        Assert.Equal(objs.Count, doc.Objects.Count);
        for (int i = 0; i < objs.Count; i++)
        {
            Assert.Equal(objs[i].Name, doc.Objects[i].Name);
            Assert.Equal(objs[i].Position, doc.Objects[i].Position);
            Assert.Equal(objs[i].Flags, doc.Objects[i].NameFlags);
            Assert.Equal(objs[i].ObjectType, doc.Objects[i].ObjectType);
        }
        var roads = msn.RoadSegments();
        Assert.Equal(roads.Count, doc.Roads.Count);
        Assert.Equal(roads[0].Points.Count, doc.Roads[0].Points.Count);
        Assert.Equal(roads[0].Points[3].X, doc.Roads[0].Points[3].Left.X);
        Assert.Equal(msn.TerrainName(), doc.TerrainName);
        Assert.Equal(WorldSettings.ArenaName(msn), doc.ArenaName);
        Assert.Equal(msn.TerrainGrid().Count, doc.TileCount);
        Assert.Equal(msn.FsmPayload(), doc.FsmPayload);
        // the road point's centre-z word is the mean of its edges' z, in the stock data
        int off = 0;
        foreach (var r in doc.Roads) foreach (var p in r.Points) if (MathF.Abs(p.CentreZ - p.Centre.Y) > 0.05f) off++;
        output.WriteLine($"{off} road points whose centre-z word is not the edges' mean");
        Assert.True(off < doc.Roads.Sum(r => r.Points.Count) / 100 + 1);
    }

    /// <summary>Which bytes differ between two same-length buffers, as ranges.</summary>
    private static List<(int From, int To)> Diff(byte[] a, byte[] b)
    {
        Assert.Equal(a.Length, b.Length);
        var o = new List<(int, int)>();
        for (int i = 0; i < a.Length; i++)
            if (a[i] != b[i]) { if (o.Count > 0 && o[^1].Item2 == i) o[^1] = (o[^1].Item1, i + 1); else o.Add((i, i + 1)); }
        return o;
    }

    [Fact]
    public void EditedHeightRoadObjectSurviveAndNothingElseChanges()
    {
        if (NitroDir is null) return;
        var path = StockMelee();
        var proj = MapProject.Load(path);
        var origMsn = File.ReadAllBytes(path);
        var origTer = proj.Terrain.ToBytes();

        // find a sample on the map
        var (r0, _, c0, _) = proj.Terrain.Bounds();
        int sx = c0 * 128 + 10, sz = r0 * 128 + 20;
        ushort before = proj.Terrain.Sample(sx, sz)!.Value;
        Assert.True(proj.Terrain.SetHeight(sx, sz, 777));
        Assert.True(proj.Terrain.SetSurface(sx + 1, sz, 3));

        var road = proj.Mission.Roads[0];
        var pt = road.Points[2];
        var wasLeft = pt.Left; var wasRight = pt.Right; var wasFlag = pt.Flag;
        pt.Move(new Vector2(12.5f, -7f));

        var ob = proj.Mission.Objects[0];
        var pos0 = ob.Position;
        ob.Position = pos0 + new Vector3(30, 0, 40);
        ob.Yaw = 1.0f;
        var moved = proj.Mission.Objects.Count > 1 ? proj.Mission.Objects[1] : null;

        var files = proj.ToFiles();
        var msn2 = files[proj.MissionFileName]; var ter2 = files[proj.TerrainFileName];
        Assert.Equal(origMsn.Length, msn2.Length);
        Assert.Equal(origTer.Length, ter2.Length);

        // terrain: exactly the two words
        var td = Diff(origTer, ter2);
        Assert.True(td.Count <= 2, string.Join(",", td));
        // the mission: the road point's bytes (one 24-byte record at most) and the object's rotation and position
        var md = Diff(origMsn, msn2);
        output.WriteLine("mission diffs: " + string.Join(" ", md.Select(d => $"{d.From:x}-{d.To:x}")));
        Assert.True(md.Sum(d => d.To - d.From) <= 24 + 36 + 12);

        // reload
        var back = MapProject.Open(proj.Name, proj.Extension, MapDocument.Parse(msn2), ter2);
        Assert.Equal(777, back.Terrain.Height(sx, sz));
        Assert.Equal(before & 0xf000, back.Terrain.Sample(sx, sz)!.Value & 0xf000);
        Assert.Equal(3, back.Terrain.Sample(sx + 1, sz)!.Value >> 12);
        var bp = back.Mission.Roads[0].Points[2];
        Assert.Equal(wasLeft + new Vector2(12.5f, -7f), bp.Left);
        Assert.Equal(wasRight + new Vector2(12.5f, -7f), bp.Right);
        Assert.Equal(wasFlag, bp.Flag);
        Assert.Equal(pos0 + new Vector3(30, 0, 40), back.Mission.Objects[0].Position);
        Assert.Equal(1.0f, back.Mission.Objects[0].Yaw, 4);
        Assert.Equal(proj.Mission.Objects.Count, back.Mission.Objects.Count);
        Assert.Equal(proj.Mission.Roads.Count, back.Mission.Roads.Count);
        if (moved is not null) Assert.Equal(moved.Payload, back.Mission.Objects[1].Payload);
        Assert.Equal(proj.Mission.FsmPayload, back.Mission.FsmPayload);
        // the old readers see the edit too
        var read = new MissionFile("x" + proj.Extension, msn2);
        Assert.Equal(pos0 + new Vector3(30, 0, 40), read.PlacedObjects()[0].Position);
    }

    [Fact]
    public void AddingAndRemovingObjectsAndPointsKeepsTheRest()
    {
        if (NitroDir is null) return;
        var path = StockMelee();
        var doc = MapDocument.Load(path);
        int n = doc.Objects.Count, rp = doc.Roads[0].Points.Count, roads = doc.Roads.Count;
        var last = doc.Objects[^1].Payload.ToArray();
        doc.Objects.Add(MapObject.Spawn(new Vector3(3000, 50, 49000), 0.5f));
        doc.Objects.Add(MapObject.Checkpoint(1, new Vector3(3100, 50, 49000)));
        doc.Objects.RemoveAt(0);
        doc.Roads[0].Points.Add(doc.Roads[0].Points[^1].Clone());
        doc.Roads.Add(new RoadEdit(1, [new RoadEditPoint(new Vector2(2500, 49000), new Vector2(2500, 49010)), new RoadEditPoint(new Vector2(2600, 49000), new Vector2(2600, 49010))]));
        var bytes = doc.ToBytes();
        var back = MapDocument.Parse(bytes);
        Assert.Equal(n + 1, back.Objects.Count);
        Assert.Equal(last, back.Objects[n - 2].Payload);
        Assert.Equal(MapRole.Spawn, back.Objects[^2].Role);
        Assert.Equal(1, back.Checkpoints.Single().Number);
        Assert.Equal(rp + 1, back.Roads[0].Points.Count);
        Assert.Equal(roads + 1, back.Roads.Count);
        Assert.Equal(2, back.Roads[^1].Points.Count);
        // chunk order is still the file's
        Assert.Equal(new MissionFile(path).ChunkTags(), new MissionFile("x.cbt", bytes).ChunkTags());
        Assert.Equal(doc.FsmPayload, back.FsmPayload);
        // and the readers walk it whole
        Assert.Equal(back.Objects.Count, new MissionFile("x.cbt", bytes).PlacedObjects().Count);
        Assert.Equal(back.Roads.Count, new MissionFile("x.cbt", bytes).RoadSegments().Count);
    }

    [Fact]
    public void EveryMissionSavedWithItsOwnLookStaysByteForByte()
    {
        if (!Roots().Any()) return;
        int n = 0; var bad = new List<string>();
        foreach (var f in Files(IsMission))
        {
            var data = File.ReadAllBytes(f);
            var doc = MapDocument.Parse(data);
            if (doc.Look is not { } look) continue;
            doc.Look = look;                                                          // what the editor's save does for a map whose style were not touched
            if (!doc.ToBytes().AsSpan().SequenceEqual(data)) bad.Add(f);
            n++;
        }
        output.WriteLine($"{n} missions with a look, {n - bad.Count} identical");
        Assert.Empty(bad);
    }

    [Fact]
    public void BlankMapIsAMapTheReadersAccept()
    {
        var p = MapProject.CreateBlank("blanktst", "Blank Test", ".cbt", 2, 3, 250);
        var files = p.ToFiles();
        var msn = new MissionFile("blanktst.cbt", files["blanktst.cbt"]);
        Assert.Equal("blanktst.ter", msn.TerrainName());
        Assert.Equal("Blank Test", WorldSettings.ArenaName(msn));
        Assert.Equal(9u, msn.WorldRevision());
        Assert.Empty(msn.PlacedObjects());
        Assert.Empty(msn.RoadSegments());
        var (count, grid) = msn.TerrainGrid();
        Assert.Equal(6, count);
        Assert.Equal(6, grid.Count(g => g != MissionFile.ZmapEmpty));
        var t = TerrainTiles.Load(msn, files["blanktst.ter"]);
        Assert.Equal(0, t.MissingTiles);
        Assert.Equal(6 * 0x8000, files["blanktst.ter"].Length);
        var (r0, r1, c0, c1) = t.Bounds();
        Assert.Equal((72, 74, 4, 5), (r0, r1, c0, c1));
        Assert.Equal(250, t.Height(c0 * 128 + 5, r0 * 128 + 5));
        Assert.Equal(WorldSettings.Read(msn)!.Palette, "p18_16.act");
        // add a spawn and a tile, save, reload
        p.Mission.Objects.Add(MapObject.Spawn(new Vector3(2900, 25, 46500), 0));
        Assert.True(p.Terrain.AddTile(75, 4, 300) >= 0);
        var f2 = p.ToFiles();
        var back = MapProject.Open("blanktst", ".cbt", MapDocument.Parse(f2["blanktst.cbt"]), f2["blanktst.ter"]);
        Assert.Equal(7, back.Terrain.TileCount);
        Assert.Equal(300, back.Terrain.Height(4 * 128, 75 * 128));
        Assert.Single(back.Mission.Objects);
    }

    [Fact]
    public void BlankMapStaysUnderTheEnginesLimits()
    {
        Assert.Throws<ArgumentException>(() => MapProject.CreateBlank("toolongname", "x"));
        Assert.Throws<ArgumentException>(() => MapProject.CreateBlank("ok", "x", ".txt"));
        Assert.Throws<ArgumentException>(() => MapProject.CreateBlank("ok", "x", ".cbt", 10, 10, 0, 75, 75));
        var p = MapProject.CreateBlank("ok", "x");
        Assert.Throws<ArgumentException>(() => p.Mission.ArenaName = new string('a', 16));
        Assert.Throws<ArgumentException>(() => p.Mission.TerrainName = "dir/x.ter");
    }

    [Fact]
    public void CloneRenamesZoneAndNameAndLeavesTheSource()
    {
        if (NitroDir is null) return;
        var src = MapProject.Load(StockMelee());
        var srcBytes = src.ToFiles()[src.MissionFileName];
        var c = src.CloneAs("myrift", "My Rift");
        var f = c.ToFiles();
        Assert.Equal(new[] { "myrift.cbt", "myrift.ter" }.OrderBy(x => x), f.Keys.OrderBy(x => x));
        var msn = new MissionFile("myrift.cbt", f["myrift.cbt"]);
        Assert.Equal("myrift.ter", msn.TerrainName());
        Assert.Equal("My Rift", WorldSettings.ArenaName(msn));
        Assert.Equal(src.Mission.Objects.Count, msn.PlacedObjects().Count);
        Assert.Equal(src.Terrain.ToBytes(), f["myrift.ter"]);
        Assert.Equal(srcBytes, src.ToFiles()[src.MissionFileName]);
        // only the name fields differ from the source
        var d = Diff(srcBytes, f["myrift.cbt"]);
        output.WriteLine("clone diffs: " + string.Join(" ", d.Select(x => $"{x.From:x}-{x.To:x}")));
        Assert.True(d.Sum(x => x.To - x.From) <= 16 + 13);
    }

    [Fact]
    public void SaveToWritesTheSetAndNothingElse()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"i76-mapwrite-{Environment.ProcessId}-{Guid.NewGuid():N}");
        try
        {
            var written = MapProject.CreateBlank("savetst", "Save Test", ".rac").SaveTo(dir);
            Assert.Equal(2, written.Count);
            Assert.Equal(new[] { "savetst.rac", "savetst.ter" }, Directory.EnumerateFiles(dir).Select(Path.GetFileName).Order());
            var back = MapProject.Load(Path.Combine(dir, "savetst.rac"));
            Assert.Equal("Save Test", back.Mission.ArenaName);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}
