using Godot;
using I76.Formats.Assembly;
using I76.Formats.Mission;

namespace I76.Game.World;

/// <summary>
/// A map too big to hold in one piece (<see cref="TerrainTiles.Huge"/>: a community map tiling one terrain over all 6400 cells of
/// the grid, 51 km across, 100 000 chunks) has only the chunks near the camera: they are made off the main thread as the camera
/// comes within <see cref="StreamRadius"/> of them and freed a little farther out. The surface they are drawn from is
/// <see cref="BlockedSurface"/>. The original draws no farther than its horizon panels (a ring at 6 km).
/// </summary>
public partial class TerrainNode
{
    /// <summary>The farthest from the camera (metres, to a chunk's box) a chunk is made: the camera's far plane (600 at Draw Distance Original, the original's clip, and in the 1997 look) and a chunk's reach, at most this.</summary>
    public const float StreamRadius = 7000f;
    private float _radius = StreamRadius, _keep = StreamRadius + 1000f;
    private static readonly int MaxStreamJobs = Math.Clamp(System.Environment.ProcessorCount * 2, 8, 32);
    private const double StreamBudgetMs = 3;

    private bool _stream;
    private static readonly bool Dbg = OS.GetEnvironment("I76_STREAM_DEBUG") == "1";
    /// <summary>Whether the chunks come and go with the camera.</summary>
    public bool Streaming => _stream;

    /// <summary>Called on the job's thread before a chunk's materials are asked for: where the chunk's CPU-side textures are made.</summary>
    public Action<int, int>? PrepareChunk { get; set; }

    private sealed record Streamed((Vector3[] V, Vector3[] N, Vector2[] Uv, Vector2[] Uv2, int[] I) Arrays, TerrainLodMesh Coarse, float Lo, float Hi);
    private readonly Dictionary<(int, int), Task<Streamed>> _jobs = [];
    private bool _streamFirst = true;
    /// <summary>The first look makes what is within this of the camera before the first frame (metres).</summary>
    private const float FirstLookRadius = 1000f;
    private int _streamTick;
    private bool _more;
    private Vector2 _streamAt = new(float.MaxValue, 0);

    private void BuildStreamed(MissionWorld world)
    {
        _stream = true;
        Surface = Early is { } e ? e.GetAwaiter().GetResult().Surface : SurfaceFor(world);
        // the chunks round the player's start are made while the rest of the mission builds (the camera's first look finds them done)
        if (world.Placements.FirstOrDefault(p => p.Object.IsPlayer) is { } start)
        {
            foreach (var (_, x0, z0) in StreamPlan.Within(Present(world), start.Object.Position.X, start.Object.Position.Z, FirstLookRadius - 0.01f))
                _jobs[(x0, z0)] = StartJob(x0, z0);
        }
        MissionBuilder.Trace("terrain: streamed");
    }

    private Task<Streamed> StartJob(int x0, int z0)
    {
        var world = _world!; var surf = Surface!; var prepare = PrepareChunk;
        return Task.Run(async () =>
        {
            await surf.Ready(x0, z0);
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            prepare?.Invoke(x0, z0);
            long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
            var arrays = FaithfulArrays(world, x0, z0);
            long t2 = System.Diagnostics.Stopwatch.GetTimestamp();
            var coarse = TerrainLodMesh.Build(surf, x0, z0, TerrainLodMesh.Levels - 1);
            var (lo, hi) = surf.HeightRange(x0, z0, Chunk);
            long t3 = System.Diagnostics.Stopwatch.GetTimestamp();
            if (Dbg)
            {
                Interlocked.Add(ref _tPrep, t1 - t0); Interlocked.Add(ref _tFaith, t2 - t1); Interlocked.Add(ref _tCoarse, t3 - t2);
                if (Interlocked.Increment(ref _nJobs) % 100 == 0)
                    Console.Error.WriteLine($"stream: {_nJobs} chunks made, mean ms: prepare {Ms(_tPrep) / _nJobs:0.0}, 1997 arrays {Ms(_tFaith) / _nJobs:0.0}, coarse {Ms(_tCoarse) / _nJobs:0.0}");
            }
            return new Streamed(arrays, coarse, lo, hi);
        });
    }

    private static long _tPrep, _tFaith, _tCoarse, _nJobs;
    private static double Ms(long ticks) => ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

    private static float BoxDistance(float px, float pz, int x0, int z0) => StreamPlan.BoxDistance(px, pz, x0, z0);

    /// <summary>Whether the map has a chunk at the sample origin: its tile is not empty.</summary>
    private static Func<int, int, bool> Present(MissionWorld world) => (x0, z0) => world.Terrain.TileAt(z0 / TerrainTiles.Tile, x0 / TerrainTiles.Tile) != MissionFile.ZmapEmpty;

    /// <summary>The camera's place in the terrain's frame (game metres), or null with no camera.</summary>
    private Vector2? StreamFocus()
    {
        if (GetViewport()?.GetCamera3D() is not { } cam) return null;
        // a chunk is drawn if any of it is within the far plane; one is let go a little beyond (the camera turning back finds it still there)
        _radius = MathF.Min(cam.Far + Chunk * SampleSize * 1.5f, StreamRadius);
        _keep = _radius + MathF.Max(400f, _radius * 0.15f);
        var l = GlobalTransform.AffineInverse() * cam.GlobalPosition;
        return new Vector2(l.X, -l.Z);
    }

    private double _streamMs, _streamMax; private int _streamFrames;

    /// <summary>I76_STREAM_DEBUG=1: the main thread's cost of <see cref="StreamUpdate"/>, mean and worst a frame, every 300 frames.</summary>
    private void StreamUpdateTimed()
    {
        if (!Dbg) { StreamUpdate(); return; }
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        StreamUpdate();
        double ms = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        _streamMs += ms; _streamMax = Math.Max(_streamMax, ms);
        if (++_streamFrames % 300 == 0)
        {
            Console.Error.WriteLine($"stream: main thread {_streamMs / 300:0.00} ms a frame, worst {_streamMax:0.0} ms, {_chunks.Count} chunks, {_jobs.Count} jobs");
            _streamMs = _streamMax = 0;
        }
    }

    private void StreamUpdate()
    {
        if (_world is null || StreamFocus() is not { } at) return;
        // the first look, and after a jump (the camera put at the car, a teleport): what is near is made at once
        bool first = _streamFirst || (at - _streamAt).Length() > 2000f;
        // the chunks that finished: put them in, a few a frame
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        foreach (var k in _jobs.Keys.ToList())
        {
            var t = _jobs[k];
            if (!t.IsCompleted) continue;
            if (!first && System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds > StreamBudgetMs) break;
            _jobs.Remove(k);
            if (t.IsFaulted) { GD.PrintErr($"terrain chunk {k.Item1},{k.Item2}: {t.Exception?.GetBaseException().Message}"); continue; }
            if (BoxDistance(at.X, at.Y, k.Item1, k.Item2) > _keep || _byOrigin.ContainsKey(k)) continue;
            AddStreamed(k.Item1, k.Item2, t.Result);
        }
        bool moved = (at - _streamAt).LengthSquared() > 60f * 60f;
        // look again when the camera has moved, soon if the last look left chunks unstarted (the jobs in flight are capped), else now and then
        if (!first && !moved && ++_streamTick < (_more ? 6 : 120)) return;
        _streamTick = 0; _streamAt = at;
        if (Dbg) Console.Error.WriteLine($"stream: look at {at} chunks={_chunks.Count} jobs={_jobs.Count}");

        var need = StreamPlan.Within(Present(_world), at.X, at.Y, _radius).Where(n => !_byOrigin.ContainsKey((n.X0, n.Z0)) && !_jobs.ContainsKey((n.X0, n.Z0))).ToList();
        // nearest first, but a block of the surface at a time (its chunks together: a block is made once and used for all of them)
        var nearest = new Dictionary<(int, int), float>();
        foreach (var n in need)
        {
            var g = (n.X0 / BlockedSurface.Block, n.Z0 / BlockedSurface.Block);
            nearest[g] = nearest.TryGetValue(g, out var m) ? MathF.Min(m, n.D) : n.D;
        }
        need.Sort((a, b) =>
        {
            var ga = (a.X0 / BlockedSurface.Block, a.Z0 / BlockedSurface.Block); var gb = (b.X0 / BlockedSurface.Block, b.Z0 / BlockedSurface.Block);
            if (ga == gb) return a.D.CompareTo(b.D);
            int c = nearest[ga].CompareTo(nearest[gb]);
            return c != 0 ? c : ga.CompareTo(gb);
        });
        if (first)
        {
            // the first look: what is within a kilometre is made at once, so the first frame has ground under it
            // (some are already made or under way: the chunks round the player's start, begun while the mission built)
            var pre = _jobs.Where(j => BoxDistance(at.X, at.Y, j.Key.Item1, j.Key.Item2) < MathF.Min(FirstLookRadius, _radius)).ToList();
            Task.WaitAll(pre.Select(j => (Task)j.Value).ToArray());
            foreach (var j in pre)
            {
                _jobs.Remove(j.Key);
                if (j.Value.IsCompletedSuccessfully) { if (!_byOrigin.ContainsKey(j.Key)) AddStreamed(j.Key.Item1, j.Key.Item2, j.Value.Result); }
                else GD.PrintErr($"terrain chunk {j.Key.Item1},{j.Key.Item2}: {j.Value.Exception?.GetBaseException().Message}");
            }
            var now = need.Where(n => n.D < MathF.Min(FirstLookRadius, _radius) && !_byOrigin.ContainsKey((n.X0, n.Z0))).ToList();
            var tasks = now.Select(n => StartJob(n.X0, n.Z0)).ToArray();
            Task.WaitAll(tasks);
            for (int i = 0; i < now.Count; i++)
                if (tasks[i].IsCompletedSuccessfully) AddStreamed(now[i].X0, now[i].Z0, tasks[i].Result);
                else GD.PrintErr($"terrain chunk {now[i].X0},{now[i].Z0}: {tasks[i].Exception?.GetBaseException().Message}");
            need.RemoveAll(n => n.D < MathF.Min(FirstLookRadius, _radius));
            _streamFirst = false;
            GD.Print($"terrain: streaming: {_chunks.Count} chunks at once ({(at.X / 1000):0.0}, {(at.Y / 1000):0.0} km), within {_radius / 1000:0.0} km later");
        }
        _more = false;
        foreach (var n in need)
        {
            if (_jobs.Count >= MaxStreamJobs) { _more = true; break; }
            _jobs[(n.X0, n.Z0)] = StartJob(n.X0, n.Z0);
        }
        // let the far ones go
        var gone = _chunks.Where(c => BoxDistance(at.X, at.Y, c.X0, c.Z0) > _keep).ToList();
        if (gone.Count == 0) return;
        foreach (var c in gone)
        {
            _byOrigin.Remove((c.X0, c.Z0));
            c.Mi.QueueFree();
            c.Caster?.QueueFree();
        }
        var set = gone.ToHashSet();
        _chunks.RemoveAll(set.Contains);
        foreach (var c in gone)
            foreach (var (dx, dz) in Neighbours)
                if (_byOrigin.TryGetValue((c.X0 + dx * Chunk, c.Z0 + dz * Chunk), out var o)) Relink(o);
    }

    private void Relink(ChunkState c)
    {
        var next = new List<ChunkState>(4);
        foreach (var (dx, dz) in Neighbours)
            if (_byOrigin.TryGetValue((c.X0 + dx * Chunk, c.Z0 + dz * Chunk), out var o)) next.Add(o);
        c.Next = [.. next];
    }

    private void AddStreamed(int x0, int z0, Streamed s)
    {
        var a = s.Arrays;
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = a.V;
        arrays[(int)Mesh.ArrayType.Normal] = a.N;
        arrays[(int)Mesh.ArrayType.TexUV] = a.Uv;
        arrays[(int)Mesh.ArrayType.TexUV2] = a.Uv2;
        arrays[(int)Mesh.ArrayType.Index] = a.I;
        var mesh = new ArrayMesh();
        mesh.AddSurfaceArrays(Mesh.PrimitiveType.Triangles, arrays);
        var modern = ModernFor!(x0, z0);
        var mi = new MeshInstance3D { Mesh = mesh, Position = new Vector3(x0 * SampleSize, 0, -z0 * SampleSize), MaterialOverride = modern, Name = $"chunk_{x0}_{z0}", Layers = TerrainLayer };
        AddChild(mi);
        var c = new ChunkState(mi, modern, FaithfulFor?.Invoke(x0, z0), mesh, x0, z0);
        _chunks.Add(c);
        AttachModern(c, s.Coarse, s.Lo, s.Hi);
        Relink(c);
        foreach (var o in c.Next) Relink(o);
        ApplyLook(c, _modern);
        // a new chunk takes the level its distance wants at the next update; the coarsest shows until then
    }
}
