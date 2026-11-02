using Godot;
using I76.Formats.Assembly;
using I76.Formats.Mission;

namespace I76.Game.World;

/// <summary>
/// The mission heightfield as chunked meshes: each 128×128-sample tile is
/// cut into 4×4 chunks of 32×32 quads, one vertex per sample, 5 world units
/// apart, height raw*0.1. This is not the engine's quadtree (a recorded
/// deviation): the depth buffer is always on and there is no popping.
///
/// Samples outside any tile repeat the nearest sample inside the chunk's own
/// tile, so mesh edges stay closed and gradients stay finite.
///
/// That mesh is the 1997 look's. The modern look draws each chunk from
/// <see cref="TerrainSurface"/> instead, at three levels of detail
/// (<see cref="TerrainLodMesh"/>: 1.25, 2.5 and 5 m between vertices) chosen by
/// distance, the finer levels built as the camera nears and morphed in the vertex
/// shader into the coarser one's shape before a chunk changes level.
/// </summary>
public partial class TerrainNode : Node3D
{
    /// <summary>The render layer terrain chunks draw on (the rear-view mirror's objects-only mode leaves it out).</summary>
    public const uint TerrainLayer = 2;
    /// <summary>The sky-fog quad also sits on this layer, so the weapon camera can leave it out (it keeps layer 1, so every other camera draws it as before).</summary>
    public const uint SkyFogLayer = 1u << 6;
    public const int Chunk = 32;
    public const float SampleSize = 5f;

    public Material? FaithfulMaterial { get; set; }
    /// <summary>The modern material for the chunk at sample origin (x0, z0): shared, or its own when a road crosses it.</summary>
    public Func<int, int, Material>? ModernFor { get; set; }
    /// <summary>The 1997 material for the chunk at sample origin (x0, z0): shared, or its own when a road crosses it.</summary>
    public Func<int, int, Material>? FaithfulFor { get; set; }
    private sealed class ChunkState(MeshInstance3D mi, Material? modern, Material? faithful, ArrayMesh faithfulMesh, int x0, int z0)
    {
        public readonly MeshInstance3D Mi = mi;
        public Material? Modern = modern;
        public Material? Faithful = faithful;
        public readonly ArrayMesh FaithfulMesh = faithfulMesh;
        public readonly int X0 = x0, Z0 = z0;
        public readonly ArrayMesh?[] Lod = new ArrayMesh?[TerrainLodMesh.Levels];
        public readonly Task<TerrainLodMesh>?[] Pending = new Task<TerrainLodMesh>?[TerrainLodMesh.Levels];
        public Aabb Box;          // chunk-local, holding every level's vertices and where they morph to
        public Vector3 Origin;    // the chunk's position in this node
        public ChunkState[] Next = [];   // the chunks beside it (up to four)
        public MeshInstance3D? Caster;   // the modern look's shadow caster: the next coarser level (ShadowCasters)
        public ArrayMesh? CasterMesh;
        public bool Casting = true;
        public int Level = -1;    // the level drawn, -1 the 1997 mesh
        public int Want;
        public float Distance;
        // the map editor's edits (RefreshRegion): the chunk's version moves with each; a mesh is current when built at it
        public int Ver;
        public readonly int[] BuiltVer = new int[TerrainLodMesh.Levels];
        public bool FaithfulStale;
        public Task<TerrainLodMesh>? Rebuild;
        public int RebuildVer, RebuildLevel;
    }
    private readonly List<ChunkState> _chunks = [];
    private readonly Dictionary<(int, int), ChunkState> _byOrigin = [];
    private readonly List<ChunkState> _now = [];
    public int ChunkCount => _chunks.Count;
    public ITerrainSurface? Surface { get; private set; }
    private bool _modern = true;

    // Level ranges, metres from the camera to a chunk's box (TerrainLodMesh.Range, with the morph's start and settle).
    public static float[] Range => TerrainLodMesh.Range;
    public const float Settle = TerrainLodMesh.Settle, Back = 5f;
    /// <summary>
    /// The modern look's chunks cast their sun shadows from the next coarser level (a separate shadows-only
    /// instance), not the level drawn: the finest level had most of the shadow pass's vertices (0.7 of its 0.8 ms
    /// in t01's chase view) for no shadow anyone can tell apart. That level is the shape the drawn one morphs
    /// toward, so the two stay within the morph's reach. I76_TERRAIN_CASTERS=drawn casts from the drawn level.
    /// </summary>
    private static readonly bool ShadowCasters = OS.GetEnvironment("I76_TERRAIN_CASTERS") != "drawn";
    private static readonly int EnvFinest = Math.Clamp(int.TryParse(OS.GetEnvironment("I76_TERRAIN_LOD"), out int fl) ? fl : 0, 0, TerrainLodMesh.Levels - 1);
    /// <summary>The coarsest level a chunk casts its sun shadow from: the 5 m grid, as the shadows always were.</summary>
    private const int CasterCoarsest = 2;
    private const float CasterReach = 3000f;
    private int _finest;
    /// <summary>
    /// No level finer than this: 0 all three (Graphic Detail's Terrain Detail High), 1 none finer than 2.5 m (Low).
    /// I76_TERRAIN_LOD=n raises it (2: the 5 m sample grid everywhere), for A/B costs.
    /// </summary>
    public int FinestLevel { get => Math.Max(_finest, EnvFinest); set => _finest = Math.Clamp(value, 0, TerrainLodMesh.Levels - 1); }
    // finer levels are built this far beyond their range, off the main thread, and dropped this far beyond it
    private static readonly float[] Prefetch = [150f, 250f, 300f, 500f], Evict = [300f, 600f, 600f, 800f];
    private const int MaxJobs = 6, MaxUploadsPerFrame = 3, MaxNowJobs = 24;
    private const double UploadBudgetMs = 4;
    /// <summary>Vertices drawn by the modern terrain last frame (all chunks, before culling).</summary>
    public long ModernVertices { get; private set; }

    /// <summary>Every chunk's sample origin, tile by tile, in the order <see cref="Build"/> makes them.</summary>
    public static List<(int X0, int Z0)> ChunkOrigins(TerrainTiles ter)
    {
        var o = new List<(int, int)>();
        for (int row = 0; row < MissionFile.ZmapGrid; row++)
            for (int col = 0; col < MissionFile.ZmapGrid; col++)
            {
                if (ter.TileAt(row, col) == MissionFile.ZmapEmpty) continue;
                for (int cz = 0; cz < TerrainTiles.Tile / Chunk; cz++)
                    for (int cx = 0; cx < TerrainTiles.Tile / Chunk; cx++)
                        o.Add((col * TerrainTiles.Tile + cx * Chunk, row * TerrainTiles.Tile + cz * Chunk));
            }
        return o;
    }

    /// <summary>
    /// The modern look's CPU share made ahead (<see cref="Prepare"/>, on another thread while the rest of the mission
    /// builds): <see cref="Build"/> waits for it instead of making it.
    /// </summary>
    public System.Threading.Tasks.Task<Prepared>? Early { get; set; }

    private MissionWorld? _world;

    public void Build(MissionWorld world)
    {
        _world = world;
        if (world.Terrain.Huge) { BuildStreamed(world); return; }
        // the 1997 meshes' arrays on every core, the nodes here
        var origins = ChunkOrigins(world.Terrain);
        var arrays = new (Vector3[], Vector3[], Vector2[], Vector2[], int[])[origins.Count];
        // a playtest of the map editor builds the ground it already built for its editor from the same heights: the arrays are kept for the session
        string scope = I76.Formats.Vfs.SessionStore.Active ? $"terrain97|{world.Terrain.ContentHash():x}|" : "";
        Parallel.For(0, origins.Count, i =>
        {
            var (x0, z0) = origins[i];
            arrays[i] = scope.Length == 0 ? FaithfulArrays(world, x0, z0)
                : I76.Formats.Vfs.SessionStore.GetOrAdd(scope + x0 + "," + z0, () => new Boxed(FaithfulArrays(world, x0, z0)),
                    b => b is null ? 0 : (b.A.V.Length * 24L + b.A.Uv.Length * 16L + b.A.I.Length * 4L))!.A;
        });
        MissionBuilder.Trace("terrain: 1997 arrays");
        for (int i = 0; i < origins.Count; i++) AddChunk(origins[i].X0, origins[i].Z0, arrays[i]);
        MissionBuilder.Trace("terrain: chunks added");
        if (Deferred) { UseModern(false); return; }   // the modern look's share is built later (PrepareModern, AttachModern, FinishModern)
        if (Early is { } early) Use(early.GetAwaiter().GetResult()); else PrepareModern(world);
        MissionBuilder.Trace("terrain: modern prepared");
        for (int i = 0; i < _chunks.Count; i++) AttachModern(i);
        FinishModern();
    }

    /// <summary>
    /// Leave the modern look's share out of <see cref="Build"/> (a mission that starts in the 1997 look): only the
    /// 1997 meshes and materials are made. The rest comes later, <see cref="PrepareModern"/> on any thread, then
    /// <see cref="AttachModern"/> for each chunk and <see cref="FinishModern"/> on the main thread.
    /// </summary>
    public bool Deferred { get => _deferred; set { _deferred = value; if (value) _modern = false; } }
    private bool _deferred;
    public bool ModernAttached { get; private set; }
    private TerrainLodMesh[] _coarse = [];
    private (float Lo, float Hi)[] _range = [];

    /// <summary>The modern look's pure CPU share for chunks at <paramref name="origins"/>.</summary>
    public sealed record Prepared(ITerrainSurface Surface, TerrainLodMesh[] Coarse, (float Lo, float Hi)[] Range);

    /// <summary>The modern look's pure CPU share, safe off the main thread: the reachable ground seeded from every placement, and every chunk's coarsest level.</summary>
    public static Prepared Prepare(MissionWorld world, IReadOnlyList<(int X0, int Z0)> origins)
    {
        var surf = SurfaceFor(world);
        var coarse = new TerrainLodMesh[origins.Count];
        var range = new (float, float)[origins.Count];
        Parallel.For(0, origins.Count, i =>
        {
            coarse[i] = TerrainLodMesh.BuildCached(surf, origins[i].X0, origins[i].Z0, TerrainLodMesh.Levels - 1);
            range[i] = surf.HeightRange(origins[i].X0, origins[i].Z0, Chunk);
        });
        return new Prepared(surf, coarse, range);
    }

    /// <summary>
    /// The modern look's surface for the mission: from the bake cache, else made (its reachability fill and lean-back
    /// cone are serial, ~0.3 s on t01, more in a debug build) and kept there, keyed by the tiles and the seeds.
    /// </summary>
    internal static ITerrainSurface SurfaceFor(MissionWorld world)
    {
        if (world.Terrain.Huge) return new BlockedSurface(world.Terrain);
        var seeds = TerrainSurface.Seeds(world).ToList();
        var cache = MissionBuilder.HeightCache;
        bool session = I76.Formats.Vfs.SessionStore.Active;
        string? key = cache is null && !session ? null : TerrainSurface.CacheKey(world.Terrain, seeds);
        if (cache is not null && key is not null && cache.Read("surface", key) is { } blobs && TerrainSurface.FromBlobs(blobs) is { } hit) { hit.Key = key; return hit; }
        var surf = new TerrainSurface(world.Terrain, seeds) { Key = key };
        if (cache is not null && key is not null) MissionBuilder.Background(() => cache.Write("surface", key, surf.ToBlobs()));
        return surf;
    }

    /// <summary><see cref="Prepare"/> for this node's chunks, kept for <see cref="AttachModern"/> (any thread).</summary>
    public void PrepareModern(MissionWorld world) => Use(Prepare(world, _chunks.Select(c => (c.X0, c.Z0)).ToList()));

    private void Use(Prepared p)
    {
        if (p.Coarse.Length != _chunks.Count) throw new InvalidOperationException("prepared for other chunks");
        Surface = p.Surface; _coarse = p.Coarse; _range = p.Range;
    }

    /// <summary>One chunk's modern material, coarsest mesh and shadow caster (main thread).</summary>
    public void AttachModern(int i)
    {
        var (lo, hi) = _range.Length > i ? _range[i] : Surface!.HeightRange(_chunks[i].X0, _chunks[i].Z0, Chunk);
        AttachModern(_chunks[i], _coarse[i], lo, hi);
    }

    private void AttachModern(ChunkState c, TerrainLodMesh coarse, float lo, float hi)
    {
        c.Modern ??= ModernFor!(c.X0, c.Z0);
        if (_pendingRoad.Remove((c.X0, c.Z0), out var pend))
        {
            if (_ownRoad.Add(c) && c.Modern is ShaderMaterial pm) c.Modern = (ShaderMaterial)pm.Duplicate();
            ApplyRoadLayer(c.Modern as ShaderMaterial, pend.Modern, null);
        }
        // every level's points, wherever they morph: the samples' heights widened by the surface's freedom
        float f = (float)TerrainSurface.MaxFree, size = Chunk * SampleSize;
        c.Box = new Aabb(new Vector3(-f, lo, -size - f), new Vector3(size + 2 * f, hi - lo, size + 2 * f)).Merge(coarse.Bounds);
        c.Lod[TerrainLodMesh.Levels - 1] = coarse.ToMesh();
        c.Mi.CustomAabb = c.Box;
        if (ShadowCasters)
        {
            c.Caster = new MeshInstance3D
            {
                Position = c.Mi.Position, MaterialOverride = c.Modern, Name = c.Mi.Name + "_caster", Layers = TerrainLayer,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.ShadowsOnly, CustomAabb = c.Box, Visible = false,
            };
            AddChild(c.Caster);
        }
        _byOrigin[(c.X0, c.Z0)] = c;
        c.Origin = c.Mi.Position;
    }

    // ---- the map editor: a chunk's road layer painted anew ------------------------------------------------------------------

    private readonly HashSet<ChunkState> _ownRoad = [];
    private readonly Dictionary<(int, int), (Texture2D? Modern, Texture2D? Faithful, float LightY)> _pendingRoad = [];

    /// <summary>
    /// The map editor changed the roads under a chunk: its road layer (<see cref="RoadRaster"/>) painted anew, for the modern
    /// look and the 1997 look. A chunk shared its materials with the others until a road crossed it; here it gets its own.
    /// </summary>
    public void SetRoadLayer(int x0, int z0, Texture2D? modern, Texture2D? faithful, float roadLightY)
    {
        var c = _chunks.FirstOrDefault(k => k.X0 == x0 && k.Z0 == z0);
        if (c is null) return;
        if (c.Modern is null) { _pendingRoad[(x0, z0)] = (modern, faithful, roadLightY); }
        if (_ownRoad.Add(c))
        {
            if (c.Modern is ShaderMaterial m) c.Modern = (ShaderMaterial)m.Duplicate();
            if ((c.Faithful ?? FaithfulMaterial) is ShaderMaterial f) c.Faithful = (ShaderMaterial)f.Duplicate();
        }
        ApplyRoadLayer(c.Modern as ShaderMaterial, modern, null);
        ApplyRoadLayer(c.Faithful as ShaderMaterial, faithful, roadLightY);
        c.Mi.MaterialOverride = _modern ? c.Modern : c.Faithful ?? FaithfulMaterial;
    }

    private static void ApplyRoadLayer(ShaderMaterial? m, Texture2D? layer, float? lightY)
    {
        if (m is null) return;
        m.SetShaderParameter("has_road", layer is not null);
        if (layer is not null) m.SetShaderParameter("road_layer", layer);
        if (lightY is { } l) m.SetShaderParameter("road_light_y", l);
    }

    public int AttachCount => _chunks.Count;
    public (int X0, int Z0) ChunkOrigin(int i) => (_chunks[i].X0, _chunks[i].Z0);

    public void FinishModern()
    {
        foreach (var c in _chunks)
        {
            var next = new List<ChunkState>(4);
            foreach (var (dx, dz) in Neighbours)
                if (_byOrigin.TryGetValue((c.X0 + dx * Chunk, c.Z0 + dz * Chunk), out var o)) next.Add(o);
            c.Next = [.. next];
        }
        _coarse = []; _range = [];
        ModernAttached = true;
        if (_deferred) _deferred = false; else UseModern(_modern);
    }

    /// <summary>The 1997 mesh's arrays for the chunk at (x0, z0): pure CPU, any thread.</summary>
    private sealed record Boxed((Vector3[] V, Vector3[] N, Vector2[] Uv, Vector2[] Uv2, int[] I) A);

    private static (Vector3[] V, Vector3[] N, Vector2[] Uv, Vector2[] Uv2, int[] I) FaithfulArrays(MissionWorld w, int x0, int z0)
    {
        int tileX = x0 - x0 % TerrainTiles.Tile, tileZ = z0 - z0 % TerrainTiles.Tile;
        int Sample(int sx, int sz)
        {
            if (w.Terrain.Sample(sx, sz) is { } v) return v & MissionFile.HeightMask;
            sx = Math.Clamp(sx, tileX, tileX + TerrainTiles.Tile - 1);
            sz = Math.Clamp(sz, tileZ, tileZ + TerrainTiles.Tile - 1);
            return (w.Terrain.Sample(sx, sz) ?? 0) & MissionFile.HeightMask;
        }
        // a needle drawn level with its neighbours (TerrainTriangles.Despiked); collision keeps the raw samples
        double Raw(int sx, int sz) => TerrainTriangles.Despiked(Sample, sx, sz);

        const int n = Chunk + 1;
        var v = new Vector3[n * n];
        var nrm = new Vector3[n * n];
        var uv = new Vector2[n * n];
        var uv2 = new Vector2[n * n];
        const double k = 0.1 / 5.0; // Raw -> sample units, 0.1/hdiv with hdiv = 5
        // Chunk-local positions; the node carries the chunk origin, which keeps the floats small.
        for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
            {
                int sx = x0 + i, sz = z0 + j, idx = j * n + i;
                double h = Raw(sx, sz) * MissionWorld.HeightScale;
                v[idx] = new Vector3(i * SampleSize, (float)h, -j * SampleSize);
                double dhdx = (Raw(sx + 1, sz) - Raw(sx - 1, sz)) * k / 2;
                double dhdz = (Raw(sx, sz + 1) - Raw(sx, sz - 1)) * k / 2;
                // On a cliff (steeper than 45°) the engine's light is all or nothing by the gradient's sign, and every
                // jog in a rim flips it from one sample to the next: stripes down the face. The original's quadtree
                // seldom puts a vertex there (and lights the few over big faces); here a cliff vertex takes the
                // gradient over its 3x3 neighbourhood (Sobel) instead, so a face is lit as one. Gentler ground keeps
                // the engine's own central difference.
                if (dhdx * dhdx + dhdz * dhdz > 1)
                {
                    dhdx = (Raw(sx + 1, sz - 1) - Raw(sx - 1, sz - 1) + 2 * (Raw(sx + 1, sz) - Raw(sx - 1, sz)) + Raw(sx + 1, sz + 1) - Raw(sx - 1, sz + 1)) * k / 8;
                    dhdz = (Raw(sx - 1, sz + 1) - Raw(sx - 1, sz - 1) + 2 * (Raw(sx, sz + 1) - Raw(sx, sz - 1)) + Raw(sx + 1, sz + 1) - Raw(sx + 1, sz - 1)) * k / 8;
                }
                uv2[idx] = new Vector2((float)dhdx, (float)dhdz);
                uv[idx] = new Vector2(sx, sz);
                // Geometric normal for the modern path: sample-unit gradient is world-unit too
                // (height and spacing both scale by 5); Godot Z is mirrored.
                nrm[idx] = new Vector3((float)-dhdx, 1, (float)dhdz).Normalized();
            }
        // each cell cut along the diagonal whose ends are nearer in height, so stepped cliff rims are not combs (TerrainTriangles)
        var hs = new float[n * n];
        for (int i = 0; i < hs.Length; i++) hs[i] = v[i].Y;
        return (v, nrm, uv, uv2, TerrainTriangles.Indices(hs, n));
    }

    private void AddChunk(int x0, int z0, (Vector3[] V, Vector3[] N, Vector2[] Uv, Vector2[] Uv2, int[] I) a)
    {
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = a.V;
        arrays[(int)Mesh.ArrayType.Normal] = a.N;
        arrays[(int)Mesh.ArrayType.TexUV] = a.Uv;
        arrays[(int)Mesh.ArrayType.TexUV2] = a.Uv2;
        arrays[(int)Mesh.ArrayType.Index] = a.I;
        var origin = new Vector3(x0 * SampleSize, 0, -z0 * SampleSize);
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        var modern = Deferred ? null : ModernFor!(x0, z0);
        var mi = new MeshInstance3D { Mesh = mesh, Position = origin, MaterialOverride = modern, Name = $"chunk_{x0}_{z0}", Layers = TerrainLayer };
        AddChild(mi);
        _chunks.Add(new ChunkState(mi, modern, FaithfulFor?.Invoke(x0, z0), mesh, x0, z0));
    }

    public void UseModern(bool modern)
    {
        _modern = modern;
        foreach (var c in _chunks) ApplyLook(c, modern);
        if (modern) UpdateLevels(force: true);
    }

    private void ApplyLook(ChunkState c, bool modern)
    {
        c.Mi.MaterialOverride = modern ? c.Modern : c.Faithful ?? FaithfulMaterial;
        if (c.Caster is not null)
        {
            c.Caster.Visible = c.Casting = modern;
            c.Mi.CastShadow = modern ? GeometryInstance3D.ShadowCastingSetting.Off : GeometryInstance3D.ShadowCastingSetting.On;
        }
        if (!modern) { c.Mi.Mesh = c.FaithfulMesh; c.Level = -1; }
        else if (c.Level < 0) { c.Level = TerrainLodMesh.Levels - 1; c.Mi.Mesh = c.Lod[c.Level]; }
    }

    // ---- the map editor: chunks redrawn after their samples changed ------------------------------------------------

    /// <summary>Chunks whose meshes are out of date, for the map editor's throttled redraw (<see cref="PumpEdits"/>).</summary>
    public int StaleChunks { get; private set; }
    private const int MaxRebuildJobs = 6, MaxFaithfulPerFrame = 12;

    /// <summary>Raised on the main thread when the surface has been brought up to date for an edit: the rectangle of samples whose drawn height can have changed.</summary>
    public event Action<(int X0, int Z0, int X1, int Z1)>? Refreshed;
    private (int X0, int Z0, int X1, int Z1)? _toRefresh;
    private Task<(int X0, int Z0, int X1, int Z1)>? _refreshing;
    /// <summary>The surface refreshes made and the time they took (off the main thread), for the editor's frame-time log.</summary>
    public int RefreshCount { get; private set; }
    public double RefreshMs { get; private set; }

    /// <summary>
    /// The samples in the rectangle (global sample coordinates, inclusive) changed in the mission's terrain. Edits are gathered:
    /// one <see cref="TerrainSurface.Refresh"/> at a time runs on the thread pool for what has come in since the last, and when it
    /// is done the chunks it reaches are marked (<see cref="Refreshed"/> says where): <see cref="PumpEdits"/> then makes their
    /// meshes anew a few at a time, also off the main thread, and meanwhile they draw as they were.
    /// </summary>
    public void RefreshRegion(int sx0, int sz0, int sx1, int sz1)
    {
        _toRefresh = _toRefresh is { } p ? (Math.Min(p.X0, sx0), Math.Min(p.Z0, sz0), Math.Max(p.X1, sx1), Math.Max(p.Z1, sz1)) : (sx0, sz0, sx1, sz1);
    }

    private void PumpRefresh()
    {
        if (_refreshing is { IsCompleted: true } t)
        {
            _refreshing = null;
            if (t.IsFaulted) GD.PrintErr($"terrain edit: surface refresh: {t.Exception?.GetBaseException()}");
            else MarkChunks(t.Result);
        }
        if (_refreshing is not null || _toRefresh is not { } r || _world is not { } world) return;
        _toRefresh = null;
        if (Surface as TerrainSurface is { } surf)
        {
            _refreshing = Task.Run(() =>
            {
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                var vis = surf.Refresh(world.Terrain, r.X0, r.Z0, r.X1, r.Z1);
                RefreshMs += System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds; RefreshCount++;
                return vis;
            });
        }
        else MarkChunks((r.X0 - 3, r.Z0 - 3, r.X1 + 3, r.Z1 + 3));
    }

    /// <summary>The chunks that draw any sample of the rectangle are out of date: their meshes are made again.</summary>
    private void MarkChunks((int X0, int Z0, int X1, int Z1) vis)
    {
        var (vx0, vz0, vx1, vz1) = vis;
        foreach (var c in _chunks)
        {
            // a chunk draws samples X0..X0+Chunk, and the 1997 one reads two beyond for its needle filter
            if (c.X0 > vx1 + 1 || c.X0 + Chunk < vx0 - 1 || c.Z0 > vz1 + 1 || c.Z0 + Chunk < vz0 - 1) continue;
            c.Ver++;
            c.FaithfulStale = true;
            for (int l = 0; l < TerrainLodMesh.Levels; l++)
            {
                if (l != c.Level) c.Lod[l] = null;     // the level on show stays, as it was, until its new mesh is made
                c.Pending[l] = null;                   // jobs started before the edit would bring back the old shape
            }
            if (Surface is { } sf)
            {
                float f = (float)TerrainSurface.MaxFree, size = Chunk * SampleSize;
                var (lo, hi) = sf.HeightRange(c.X0, c.Z0, Chunk);
                c.Box = new Aabb(new Vector3(-f, lo, -size - f), new Vector3(size + 2 * f, hi - lo, size + 2 * f));
                c.Mi.CustomAabb = c.Box;
                if (c.Caster is not null) c.Caster.CustomAabb = c.Box;
            }
        }
        Refreshed?.Invoke(vis);
    }

    /// <summary>The cavity maps (the hollows the ground materials put gravel in) of the chunks the rectangle of samples reaches, made again from the new heights.</summary>
    public void RefreshCavities(int sx0, int sz0, int sx1, int sz1)
    {
        if (_world is not { } world) return;
        foreach (var c in _chunks)
        {
            if (c.X0 > sx1 + 1 || c.X0 + Chunk < sx0 - 1 || c.Z0 > sz1 + 1 || c.Z0 + Chunk < sz0 - 1) continue;
            if (c.Modern is ShaderMaterial m && m.GetShaderParameter("has_cavity").AsBool())
                m.SetShaderParameter("cavity_tex", ImageTexture.CreateFromImage(TerrainMaterials.Cavity(world, c.X0, c.Z0)));
        }
    }

    private bool Current(ChunkState c, int level) => c.Lod[level] is not null && c.BuiltVer[level] == c.Ver;

    /// <summary>
    /// Each frame while the map editor works: starts the nearest stale chunks' new meshes (at the level they draw), puts finished
    /// ones in, and rebuilds the 1997 meshes of stale chunks while that look shows.
    /// </summary>
    private void PumpEdits()
    {
        if (_world is null) return;
        PumpRefresh();
        int stale = _refreshing is not null || _toRefresh is not null ? 1 : 0;
        if (_modern && Surface is { } surf)
        {
            int jobs = 0, uploads = 0;
            foreach (var c in _chunks) if (c.Rebuild is not null) jobs++;
            foreach (var c in _chunks)
            {
                if (c.Rebuild is { IsCompleted: true } done)
                {
                    c.Rebuild = null;
                    if (done.IsFaulted) GD.PrintErr($"terrain edit: chunk {c.X0},{c.Z0}: {done.Exception?.GetBaseException()}");
                    if (done.IsCompletedSuccessfully && c.RebuildVer == c.Ver && uploads < MaxUploadsPerFrame)
                    {
                        if (EditDebug) GD.Print("terrain edit: chunk " + c.X0 + "," + c.Z0 + " L" + c.RebuildLevel + " uploaded, max y " + done.Result.Vertices.Max(v => v.Y).ToString("0.0") + ", on show: " + (c.Mi.Mesh == c.Lod[c.RebuildLevel]));
                        c.Lod[c.RebuildLevel] = done.Result.ToMesh();
                        c.BuiltVer[c.RebuildLevel] = c.Ver;
                        if (c.Level == c.RebuildLevel) c.Mi.Mesh = c.Lod[c.Level];
                        uploads++;
                    }
                    jobs--;
                }
            }
            var todo = new List<ChunkState>();
            foreach (var c in _chunks)
            {
                if (c.Level < 0 || Current(c, c.Level)) continue;
                stale++;
                if (c.Rebuild is null) todo.Add(c);
            }
            todo.Sort((a, b) => a.Distance.CompareTo(b.Distance));
            foreach (var c in todo)
            {
                if (jobs >= MaxRebuildJobs) break;
                int x0 = c.X0, z0 = c.Z0, level = c.Level;
                c.RebuildVer = c.Ver; c.RebuildLevel = level;
                c.Rebuild = Task.Run(() => TerrainLodMesh.Build(surf, x0, z0, level));
                jobs++;
            }
        }
        else if (!_modern)
        {
            var todo = _chunks.Where(c => c.FaithfulStale).OrderBy(c => c.Distance).Take(MaxFaithfulPerFrame).ToArray();
            var world = _world;
            var arrays = new (Vector3[] V, Vector3[] N, Vector2[] Uv, Vector2[] Uv2, int[] I)[todo.Length];
            Parallel.For(0, todo.Length, i => arrays[i] = FaithfulArrays(world, todo[i].X0, todo[i].Z0));
            for (int i = 0; i < todo.Length; i++)
            {
                var a = arrays[i];
                var ar = new Godot.Collections.Array();
                ar.Resize((int)Mesh.ArrayType.Max);
                ar[(int)Mesh.ArrayType.Vertex] = a.V; ar[(int)Mesh.ArrayType.Normal] = a.N; ar[(int)Mesh.ArrayType.TexUV] = a.Uv;
                ar[(int)Mesh.ArrayType.TexUV2] = a.Uv2; ar[(int)Mesh.ArrayType.Index] = a.I;
                todo[i].FaithfulMesh.ClearSurfaces();
                todo[i].FaithfulMesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, ar);
                todo[i].FaithfulStale = false;
            }
            stale += _chunks.Count(c => c.FaithfulStale);
        }
        StaleChunks = stale;
        if (EditDebug && ++_editFrames % 20 == 0) GD.Print("terrain edit: " + stale + " stale, " + _chunks.Count(c => c.Rebuild is not null) + " jobs, vers " + string.Join(",", _chunks.Where(c => c.Ver > 0).Take(6).Select(c => c.Ver + "/" + c.BuiltVer[Math.Max(c.Level, 0)] + "/L" + c.Level)));
    }
    private static readonly bool EditDebug = OS.GetEnvironment("I76_EDITOR_DEBUG") == "1";
    private int _editFrames;

    private static readonly bool Stats = OS.GetEnvironment("I76_TERRAIN_STATS") == "1";
    private int _frames;

    private Task? _warm;
    private (ChunkState C, int L)[] _warmTodo = [];
    private TerrainLodMesh[] _warmBuilt = [];
    private int _warmTick;

    /// <summary>
    /// While the 1997 look shows and the modern terrain is built: keeps the levels the camera would want made (a dozen chunks at a
    /// time, off the main thread) and drops far ones, so that turning the modern look on finds them ready, wherever the camera went.
    /// </summary>
    private void Warm()
    {
        if (Editing) return;
        if (_warm is not null)
        {
            if (!_warm.IsCompleted) return;
            if (!_warm.IsFaulted)
                for (int i = 0; i < _warmTodo.Length; i++) _warmTodo[i].C.Lod[_warmTodo[i].L] ??= _warmBuilt[i].ToMesh();
            _warm = null; _warmTick = 19;   // go on at once if there was something
            return;
        }
        if (++_warmTick < 20 || GetViewport()?.GetCamera3D() is not { } cam || Surface is null) return;
        _warmTick = 0;
        ComputeWants(cam);
        foreach (var c in _chunks)
            for (int l = 0; l < TerrainLodMesh.Levels - 1; l++)
                if (c.Distance > Range[l] + Evict[l]) c.Lod[l] = null;
        var todo = _chunks.Where(c => c.Lod[c.Want] is null).OrderBy(c => c.Distance).Take(12).Select(c => (c, c.Want)).ToArray();
        if (todo.Length == 0) return;
        var built = new TerrainLodMesh[todo.Length];
        var surf = Surface;
        _warmTodo = todo; _warmBuilt = built;
        _warm = MissionBuilder.Background(() => Parallel.For(0, todo.Length, i => built[i] = TerrainLodMesh.Build(surf, todo[i].c.X0, todo[i].c.Z0, todo[i].Want)));
    }

    /// <summary>The map editor is open: edited chunks are made anew (<see cref="PumpEdits"/>).</summary>
    public bool Editing { get; set; }

    public override void _Process(double delta)
    {
        if (_stream) StreamUpdateTimed();
        if (!_modern) { if (Editing) PumpEdits(); if (ModernAttached) Warm(); if (Audit) AuditGround(); return; }
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        UpdateLevels(force: false);
        if (Editing) PumpEdits();
        I76.Game.Boot.FrameProfiler.TerrainUpdateMs = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        if (Audit) AuditGround();
        // I76_TERRAIN_STATS=1: the levels in use, the vertices they hold and what the frame drew, once a second
        if (Stats && _frames == 0) RenderingServer.ViewportSetMeasureRenderTime(GetViewport().GetViewportRid(), true);
        if (Stats && ++_frames % 30 == 0)
        {
            var per = new int[TerrainLodMesh.Levels];
            foreach (var c in _chunks) per[c.Level]++;
            GD.Print($"terrain: levels {string.Join("/", per)}, {ModernVertices / 1000}k vertices, update {System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds:0.00} ms; gpu {RenderingServer.ViewportGetMeasuredRenderTimeGpu(GetViewport().GetViewportRid()):0.00} ms; " +
                $"frame: {Engine.GetFramesPerSecond()} fps, {Performance.GetMonitor(Performance.Monitor.RenderTotalPrimitivesInFrame) / 1000:0}k primitives, " +
                $"{Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame)} draws, process {Performance.GetMonitor(Performance.Monitor.TimeProcess) * 1000:0.0} ms");
        }
    }

    private static readonly bool Audit = OS.GetEnvironment("I76_TERRAIN_AUDIT") == "1";
    private bool _auditBad;

    /// <summary>
    /// I76_TERRAIN_AUDIT=1: every frame, whether every chunk that should stand within 350 m of the camera does (made, in the tree,
    /// visible, a mesh), and the nearest one that does not; a line when that changes. For finding frames with no ground.
    /// </summary>
    private void AuditGround()
    {
        if (_world is null || GetViewport()?.GetCamera3D() is not { } cam) return;
        var l = GlobalTransform.AffineInverse() * cam.GlobalPosition;
        float px = l.X, pz = -l.Z, cm = Chunk * SampleSize, best = float.MaxValue; string why = "";
        int cx0 = (int)MathF.Floor(px / cm), cz0 = (int)MathF.Floor(pz / cm), n = (int)MathF.Ceiling(350f / cm) + 1, cells = MissionFile.ZmapGrid * TerrainTiles.Tile / Chunk;
        for (int cz = Math.Max(0, cz0 - n); cz <= Math.Min(cells - 1, cz0 + n); cz++)
            for (int cx = Math.Max(0, cx0 - n); cx <= Math.Min(cells - 1, cx0 + n); cx++)
            {
                int x0 = cx * Chunk, z0 = cz * Chunk;
                if (_world.Terrain.TileAt(z0 / TerrainTiles.Tile, x0 / TerrainTiles.Tile) == MissionFile.ZmapEmpty) continue;
                float d = StreamPlan.BoxDistance(px, pz, x0, z0);
                if (d > 350f) continue;
                string w = "";
                if (!_byOrigin.TryGetValue((x0, z0), out var c)) w = _jobs.TryGetValue((x0, z0), out var j) ? (j.IsCompleted ? "job done, not added" : "job running") : "no chunk, no job";
                else if (!c.Mi.IsInsideTree()) w = "not in tree";
                else if (!c.Mi.Visible) w = "hidden";
                else if (c.Mi.Mesh is null) w = "no mesh";
                if (w != "" && d < best) { best = d; why = $"{w} at {x0},{z0}"; }
            }
        bool bad = best < float.MaxValue;
        if (bad || _auditBad)
            GD.Print($"audit: frame {Engine.GetFramesDrawn()} {(bad ? $"GAP {best:0} m from the camera ({why})" : "ground whole again")}; camera {px:0},{pz:0}; {_chunks.Count} chunks, {_jobs.Count} jobs");
        _auditBad = bad;
    }

    /// <summary>Each chunk's wanted level from its distance to the camera, neighbours at most one apart.</summary>
    private void ComputeWants(Camera3D cam)
    {
        // every frame, so plain loops and no allocation (it was LINQ, ~0.6 ms a frame): the chunks' boxes from
        // this node's one transform, not each chunk's GlobalPosition (an engine call apiece)
        var eye = cam.GlobalPosition;
        var xf = GlobalTransform;
        int finest = FinestLevel;
        foreach (var c in _chunks)
        {
            var lo = xf * (c.Box.Position + c.Origin);
            var d = (eye.Clamp(lo, lo + c.Box.Size) - eye).Length();
            c.Distance = d;
            int want = 0;
            while (want < Range.Length && d >= Range[want]) want++;
            // a little hysteresis: back to a finer level only well inside its range
            if (c.Level >= 0 && want < c.Level && d > Range[c.Level - 1] - Back) want = c.Level;
            c.Want = Math.Max(want, finest);
        }
        // neighbours at most one level apart
        for (int pass = 0; pass < TerrainLodMesh.Levels - 1; pass++)
            foreach (var c in _chunks)
                foreach (var o in c.Next)
                    if (c.Want > o.Want + 1) c.Want = o.Want + 1;
    }

    /// <summary>
    /// For the first switch to the modern look: builds the levels the camera's view will want, so that
    /// <see cref="UseModern"/> finds them made (the levels near the camera take ~200 ms to build all at once). Steps as
    /// <see cref="MissionBuilder.ModernSteps"/>; the build is off the main thread, the meshes a few a slice.
    /// </summary>
    public IEnumerable<object?> PrewarmSteps()
    {
        if (Surface is null || GetViewport()?.GetCamera3D() is not { } cam) yield break;
        ComputeWants(cam);
        var todo = _chunks.Where(c => c.Lod[c.Want] is null).Select(c => (C: c, L: c.Want)).ToArray();
        var built = new TerrainLodMesh[todo.Length];
        var surf = Surface;
        var work = MissionBuilder.Background(() => Parallel.For(0, todo.Length, i => built[i] = TerrainLodMesh.Build(surf, todo[i].C.X0, todo[i].C.Z0, todo[i].L)));
        while (!work.IsCompleted) yield return work;
        if (work.IsFaulted) throw work.Exception!.GetBaseException();
        for (int i = 0; i < todo.Length; i++)
        {
            todo[i].C.Lod[todo[i].L] ??= built[i].ToMesh();
            if (i % 3 == 2) yield return null;
        }
    }

    /// <summary>
    /// Picks each chunk's level from its distance, keeps neighbours within one
    /// level of each other (so a finer chunk's edge, fully morphed, meets the
    /// coarser one's), builds finer levels ahead of need and drops far ones.
    /// </summary>
    private void UpdateLevels(bool force)
    {
        if (Surface is null || GetViewport()?.GetCamera3D() is not { } cam) return;
        ComputeWants(cam);
        // finished builds become meshes: a few a frame, and the wanted ones within a time budget (an upload is an engine call on this
        // thread, ~10 ms for a near chunk: all of a jump's chunks at once was a freeze of a quarter second or more)
        int uploads = 0;
        long tUp = System.Diagnostics.Stopwatch.GetTimestamp();
        foreach (var c in _chunks)
            for (int l = 0; l < TerrainLodMesh.Levels - 1; l++)
                if (c.Pending[l] is { IsCompleted: true } t
                    && (force || uploads < MaxUploadsPerFrame || (c.Want == l && System.Diagnostics.Stopwatch.GetElapsedTime(tUp).TotalMilliseconds < UploadBudgetMs)))
                {
                    c.Lod[l] = t.Result.ToMesh();
                    c.BuiltVer[l] = c.Ver;
                    c.Pending[l] = null;
                    uploads++;
                }
        // what is needed now and not built: with a camera that jumped, hundreds of chunks. Built here only when forced (the first
        // switch to the modern look); otherwise started in the background, nearest first, and meanwhile a chunk shows the nearest
        // coarser level it has (the coarsest always is), as a chunk the prefetch had not reached always did.
        _now.Clear();
        foreach (var c in _chunks) if (c.Lod[c.Want] is null) _now.Add(c);
        if (_now.Count > 0)
        {
            var surf = Surface;
            if (force)
            {
                var now = _now;
                var built = new TerrainLodMesh[now.Count];
                Parallel.For(0, now.Count, i => built[i] = now[i].Pending[now[i].Want] is { } p ? p.Result : TerrainLodMesh.BuildCached(surf, now[i].X0, now[i].Z0, now[i].Want));
                for (int i = 0; i < now.Count; i++) { now[i].Lod[now[i].Want] = built[i].ToMesh(); now[i].BuiltVer[now[i].Want] = now[i].Ver; now[i].Pending[now[i].Want] = null; }
            }
            else
            {
                if (_now.Count > 1) _now.Sort((a, b) => a.Distance.CompareTo(b.Distance));
                int inFlight = 0;
                foreach (var c in _chunks) foreach (var p in c.Pending) if (p is { IsCompleted: false }) inFlight++;
                foreach (var c in _now)
                {
                    if (inFlight >= MaxNowJobs) break;
                    if (c.Pending[c.Want] is not null) continue;
                    int x0 = c.X0, z0 = c.Z0, lvl = c.Want;
                    c.Pending[lvl] = Task.Run(() => TerrainLodMesh.Build(surf, x0, z0, lvl));
                    inFlight++;
                }
            }
        }
        int jobs = 0;
        foreach (var c in _chunks)
            foreach (var p in c.Pending)
                if (p is not null) jobs++;
        long verts = 0;
        foreach (var c in _chunks)
        {
            int show = c.Want;
            while (c.Lod[show] is null && show < TerrainLodMesh.Levels - 1) show++;   // not made yet: the nearest coarser one
            if (c.Level != show || force)
            {
                c.Level = show;
                c.Mi.Mesh = c.Lod[c.Level];
            }
            if (c.Caster is not null)
            {
                // the shadow map takes the next coarser level, built or not yet (the coarsest always is)
                // (past the 5 m grid the drawn level itself: the sun's shadows end well inside the 800 m level)
                var m = c.Lod[Math.Min(c.Level + 1, Math.Max(c.Level, CasterCoarsest))] ?? c.Lod[c.Level];
                if (c.CasterMesh != m) c.Caster.Mesh = c.CasterMesh = m;   // (reading Mesh back is an engine call)
                // none past the shadow map's reach (1.5 km) and the longest shadow that can fall into it: half the Far draw's calls were these
                bool cast = c.Distance < CasterReach;
                if (c.Casting != cast) c.Caster.Visible = c.Casting = cast;
            }
            int nv = TerrainLodMesh.Quads(c.Level) + 1;
            verts += nv * nv;
            for (int l = 0; l < TerrainLodMesh.Levels - 1; l++)
            {
                if (l == c.Level) continue;
                if (c.Distance > Range[l] + Evict[l]) { c.Lod[l] = null; continue; }
                if (c.Lod[l] is null && c.Pending[l] is null && jobs < MaxJobs && c.Distance < Range[l] + Prefetch[l] && (l == 0 || c.Distance > Range[l - 1] - Prefetch[l - 1]))
                {
                    var surf = Surface;
                    int x0 = c.X0, z0 = c.Z0, lvl = l;
                    c.Pending[l] = Task.Run(() => TerrainLodMesh.Build(surf, x0, z0, lvl));
                    jobs++;
                }
            }
        }
        ModernVertices = verts;
    }

    private static readonly (int, int)[] Neighbours = [(1, 0), (-1, 0), (0, 1), (0, -1)];
}
