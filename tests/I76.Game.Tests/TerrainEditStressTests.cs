using I76.Editor;
using I76.Formats.Assembly;
using I76.Formats.Mission;
using I76.Game.World;
using Xunit;
using Xunit.Abstractions;

namespace I76.Game.Tests;

/// <summary>
/// The map editor's stroke path, as TerrainNode runs it, without the engine: big strokes change the heights on the main thread
/// while one surface refresh at a time and the stale chunks' new meshes (several at once) run on the thread pool, with the
/// collector forced over and over meanwhile. The workers' share must be plain managed work (this host has no engine, so any
/// engine call there fails the test), nothing may throw, and once the edits stop and the last refresh is in, every chunk's mesh
/// is the one a surface made from scratch over the edited heights gives.
/// </summary>
public class TerrainEditStressTests(ITestOutputHelper log)
{
    private const int T = TerrainTiles.Tile, C = TerrainNode.Chunk;

    /// <summary>cols x rows tiles of rolling ground.</summary>
    private static TerrainTiles Land(int cols, int rows)
    {
        var grid = new byte[MissionFile.ZmapGrid * MissionFile.ZmapGrid];
        Array.Fill(grid, MissionFile.ZmapEmpty);
        for (int r = 0; r < rows; r++) for (int c = 0; c < cols; c++) grid[(r + 2) * MissionFile.ZmapGrid + c + 2] = (byte)(r * cols + c);
        var ter = new byte[cols * rows * T * T * 2];
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
                for (int z = 0; z < T; z++)
                    for (int x = 0; x < T; x++)
                    {
                        int gx = c * T + x, gz = r * T + z;
                        ushort v = (ushort)(900 + 300 * Math.Sin(gx * 0.03) + 250 * Math.Cos(gz * 0.04));
                        int i = ((r * cols + c) * T * T + z * T + x) * 2;
                        ter[i] = (byte)v; ter[i + 1] = (byte)(v >> 8);
                    }
        return new TerrainTiles(ter, cols * rows, grid);
    }

    [Fact]
    public void BigStrokesWithRefreshesAndRebuildsOnThePoolEndAsAFreshBuild()
    {
        const int cols = 4, rows = 3, levels = 3;   // levels 2..4: the 5 m grid and coarser, as a chunk far from the editor's camera is
        var tiles = Land(cols, rows);
        var surf = new TerrainSurface(tiles);
        var doc = new TerrainDocument(tiles, "stress");
        // every chunk's sample origin (TerrainNode.ChunkOrigins, which a test host cannot reach: TerrainNode's statics ask the engine)
        var origins = new List<(int X0, int Z0)>();
        for (int r = 0; r < rows; r++) for (int c = 0; c < cols; c++)
            for (int cz = 0; cz < T / C; cz++) for (int cx = 0; cx < T / C; cx++) origins.Add(((c + 2) * T + cx * C, (r + 2) * T + cz * C));
        var ver = new int[origins.Count];
        var built = new TerrainLodMesh?[origins.Count, levels];
        var builtVer = new int[origins.Count, levels];
        var jobs = new Dictionary<(int Chunk, int Level), (Task<TerrainLodMesh> Job, int Ver)>();
        Task<(int X0, int Z0, int X1, int Z1)>? refreshing = null;
        (int X0, int Z0, int X1, int Z1)? toRefresh = null;
        var rnd = new Random(76);

        // the collector forced over and over, as a busy editor's allocations make it run
        using var stop = new CancellationTokenSource();
        var gc = Task.Run(() => { while (!stop.IsCancellationRequested) { GC.Collect(); GC.WaitForPendingFinalizers(); Thread.Sleep(1); } });

        void MarkChunks((int X0, int Z0, int X1, int Z1) vis)
        {
            for (int i = 0; i < origins.Count; i++)
            {
                var (x0, z0) = origins[i];
                if (x0 > vis.X1 + 1 || x0 + C < vis.X0 - 1 || z0 > vis.Z1 + 1 || z0 + C < vis.Z0 - 1) continue;
                ver[i]++;
            }
        }

        // one frame of TerrainNode.PumpEdits: the refresh's result marks chunks, finished meshes are taken if still current, stale ones started
        int frames = 0, uploads = 0, discarded = 0;
        void Pump()
        {
            frames++;
            if (refreshing is { IsCompleted: true } t) { refreshing = null; MarkChunks(t.Result); }
            if (refreshing is null && toRefresh is { } r)
            {
                toRefresh = null;
                refreshing = Task.Run(() => surf.Refresh(tiles, r.X0, r.Z0, r.X1, r.Z1));
            }
            foreach (var (key, (job, v)) in jobs.Where(kv => kv.Value.Job.IsCompleted).ToList())
            {
                jobs.Remove(key);
                if (v != ver[key.Chunk]) { discarded++; continue; }
                var m = job.Result;
                Assert.All(m.Vertices, p => Assert.True(float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z)));
                built[key.Chunk, key.Level] = m; builtVer[key.Chunk, key.Level] = v;
                uploads++;
            }
            for (int i = 0; i < origins.Count && jobs.Count < 6; i++)
                for (int l = 0; l < levels && jobs.Count < 6; l++)
                {
                    if ((built[i, l] is not null && builtVer[i, l] == ver[i]) || jobs.ContainsKey((i, l))) continue;
                    var (x0, z0) = origins[i];
                    int lvl = TerrainLodMesh.Levels - levels + l;
                    jobs[(i, l)] = (Task.Run(() => TerrainLodMesh.Build(surf, x0, z0, lvl)), ver[i]);
                }
        }

        // forty strokes of radius 400 m, raise and lower, a dozen stamps each, the pump running between stamps
        for (int s = 0; s < 40; s++)
        {
            var stroke = new BrushStroke(doc, new BrushSettings { Kind = s % 3 == 2 ? BrushKind.Lower : BrushKind.Raise, Radius = 400, Strength = 1 });
            double cx = (2 + rnd.NextDouble() * cols) * T, cz = (2 + rnd.NextDouble() * rows) * T;
            for (int k = 0; k < 12; k++)
            {
                if (stroke.Stamp(cx + k * 4, cz - k * 3, 1 / 30.0) is { } rect)
                    toRefresh = toRefresh is { } p ? (Math.Min(p.X0, rect.X0), Math.Min(p.Z0, rect.Z0), Math.Max(p.X1, rect.X1), Math.Max(p.Z1, rect.Z1)) : (rect.X0, rect.Z0, rect.X1, rect.Z1);
                Pump();
            }
        }
        // the edits stop: the pump runs until nothing is stale and nothing runs
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (refreshing is not null || toRefresh is not null || jobs.Count > 0 || Enumerable.Range(0, origins.Count).Any(i => Enumerable.Range(0, levels).Any(l => builtVer[i, l] != ver[i] || built[i, l] is null)))
        {
            Assert.True(clock.Elapsed.TotalSeconds < 120, "the rebuilds settle");
            Pump();
            Thread.Sleep(1);
        }
        stop.Cancel();
        gc.Wait();
        log.WriteLine($"{frames} frames, {uploads} meshes taken, {discarded} made stale by a later edit before they were done");
        Assert.True(discarded > 0, "the edits outran some builds (the case under test)");

        // every chunk as a surface made from scratch over the edited heights draws it
        var fresh = new TerrainSurface(tiles);
        for (int i = 0; i < origins.Count; i++)
            for (int l = 0; l < levels; l++)
            {
                var (x0, z0) = origins[i];
                var want = TerrainLodMesh.Build(fresh, x0, z0, TerrainLodMesh.Levels - levels + l);
                var got = built[i, l]!;
                Assert.Equal(want.Vertices.Length, got.Vertices.Length);
                for (int k = 0; k < want.Vertices.Length; k++)
                    Assert.True((want.Vertices[k] - got.Vertices[k]).Length() < 1e-3f, $"chunk {x0},{z0} level {TerrainLodMesh.Levels - levels + l} vertex {k}: {got.Vertices[k]} against {want.Vertices[k]}");
            }
    }

    /// <summary>
    /// Every mesh upload goes through MeshArrays.AddSurfaceArrays, which holds the collections until the engine has read them (an
    /// export's bindings keep no hold on an argument: a collection during the call freed the arrays under the engine).
    /// </summary>
    [Fact]
    public void NoMeshUploadPassesItsArraysUnheld()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "game", "Scripts"))) dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        var offenders = Directory.EnumerateFiles(Path.Combine(dir!, "game", "Scripts"), "*.cs", SearchOption.AllDirectories)
            .Where(f => Path.GetFileName(f) != "MeshArrays.cs")
            .SelectMany(f => File.ReadLines(f).Select((line, n) => (f, n, line)))
            .Where(x => x.line.Contains(".AddSurfaceFromArrays(") || x.line.Contains("MeshAddSurfaceFromArrays("))
            .Select(x => $"{Path.GetRelativePath(dir!, x.f)}:{x.n + 1}")
            .ToList();
        Assert.True(offenders.Count == 0, "use MeshArrays.AddSurfaceArrays: " + string.Join(", ", offenders));
    }
}
