using Godot;
using I76.Formats.Assembly;
using I76.Formats.Mission;

namespace I76.Game.World;

/// <summary>
/// The ground past the map's tiles near the camera: the simulation's own samples (OutsideGround.Raw, bilinear between them as the
/// cars ride on them), so a car off the map stands on what is drawn. The distant ring (HorizonTerrain) draws the same function
/// far out and gives way under these chunks (its near_mask).
///
/// Chunks of 32x32 samples (160 m, the map's own chunk grid) over the empty cells round the camera, built off the main thread:
/// <list type="bullet">
/// <item>Modern look: within <see cref="ModernReach"/> (or the camera's far plane), with the ring's material and its shading data,
/// at 1.25, 2.5 or 5 m between vertices by distance (<see cref="LevelRange"/>). Every level's vertices lie on the bilinear surface,
/// which along a chunk's edge (a line of samples) is the same straight segments at any level, so neighbours meet; a skirt hides the
/// rest (the ring's coarser cells where the chunks end).</item>
/// <item>1997 look: the map's own 1997 terrain (TerrainNode's 1997 mesh, its material: the engine's light through the palette), within
/// the original's 600 m clip only, cut at 600 m view depth as the map's ground is by the far plane, whatever the draw distance; the
/// horizon panels and sky stand beyond as they do over the map.</item>
/// </list>
/// </summary>
public partial class OutsideTerrain : Node3D
{
    public const int Chunk = TerrainNode.Chunk;
    private const float ChunkSize = Chunk * TerrainNode.SampleSize;
    /// <summary>The modern look draws chunks whose box is this near the camera (the ring beyond).</summary>
    public const float ModernReach = 1200f;
    /// <summary>The 1997 look draws within the original's clip.</summary>
    public const float Reach1997 = HorizonEdge.Clip;
    /// <summary>Modern levels: 1.25 m between vertices nearer than LevelRange[0], 2.5 m nearer than LevelRange[1], else 5 m.</summary>
    public static readonly float[] LevelRange = [100f, 450f];
    /// <summary>How far the modern chunks' skirts hang below their edges, metres.</summary>
    public const float Skirt = 10f;
    private const int MaxJobs = 4, MaxUploads = 4;

    private MissionWorld? _world;
    private OutsideGround? _ground;
    private bool _modern = true;
    /// <summary>The ring's material: the modern chunks are drawn with it (null until the ring is made).</summary>
    public ShaderMaterial? Modern { get; set; }
    /// <summary>The ring, for its baked sun visibility and its near mask.</summary>
    public HorizonTerrain? Ring { get; set; }
    private ShaderMaterial? _faithful;

    private sealed class Piece
    {
        public MeshInstance3D? Mi;
        public int Level = int.MinValue;      // shown: 0..2 modern, -1 1997
        public Task<Arrays>? Job;
        public int JobLevel;
        public float Distance;
        public int Want;
    }
    private readonly Dictionary<(int X, int Z), Piece> _pieces = [];
    private readonly List<(int X, int Z)> _drop = [];
    private int _jobs;

    /// <summary>A chunk's arrays: chunk-local vertices for the 1997 look (the node stands at the chunk's corner), centre-relative for the modern look (at the ring's centre).</summary>
    public sealed record Arrays(Vector3[] V, Vector3[] N, Color[]? C, Vector2[] Uv, Vector2[] Uv2, int[] I);

    /// <summary>Chunks drawn.</summary>
    public int Drawn { get; private set; }
    private bool _maskDirty = true, _maskModern;

    public void Setup(MissionWorld world, ShaderMaterial? faithful)
    {
        _world = world;
        _ground = world.Outside;
        if (faithful is not null)
        {
            _faithful = (ShaderMaterial)faithful.Duplicate();
            _faithful.SetShaderParameter("clip_depth", Reach1997);
        }
    }

    public void UseModern(bool modern)
    {
        if (_modern == modern) return;
        _modern = modern;
        Clear();   // every chunk made again in the other look
    }

    /// <summary>Kept this far past the reach, so a chunk at its edge does not come and go.</summary>
    private const float Keep = 100f;

    public override void _Process(double delta)
    {
        if (_ground is null || _world is null || GetViewport()?.GetCamera3D() is not { } cam) return;
        var eye = ToLocal(cam.GlobalPosition);
        float ex = eye.X, ez = -eye.Z;   // game frame
        bool modern = _modern;
        if (modern && Modern is null) { Clear(); return; }
        float reach = modern ? MathF.Min(ModernReach, cam.Far + ChunkSize) : Reach1997;
        int span = (int)MathF.Ceiling((reach + Keep) / ChunkSize) + 1;
        int ccx = (int)MathF.Floor(ex / ChunkSize), ccz = (int)MathF.Floor(ez / ChunkSize);
        // what is wanted: the empty cells' chunks near the camera, and the level for each
        foreach (var p in _pieces.Values) p.Distance = float.MaxValue;
        for (int cz = ccz - span; cz <= ccz + span; cz++)
            for (int cx = ccx - span; cx <= ccx + span; cx++)
            {
                float dx = MathF.Max(MathF.Max(cx * ChunkSize - ex, ex - (cx + 1) * ChunkSize), 0);
                float dz = MathF.Max(MathF.Max(cz * ChunkSize - ez, ez - (cz + 1) * ChunkSize), 0);
                float d = MathF.Sqrt(dx * dx + dz * dz);
                if (d > reach + Keep || !Outside(cx, cz)) continue;
                if (!_pieces.TryGetValue((cx, cz), out var p))
                {
                    if (d > reach) continue;
                    _pieces[(cx, cz)] = p = new Piece();
                }
                p.Distance = d;
                int want = -1;
                if (modern)
                {
                    want = 2;
                    for (int l = 0; l < LevelRange.Length; l++) if (d < LevelRange[l]) { want = l; break; }
                    // hysteresis: back to a finer level only well inside its range
                    if (p.Level >= 0 && want < p.Level && d > LevelRange[want] - 10) want = p.Level;
                }
                p.Want = want;
            }
        // finished jobs become meshes, a few a frame
        int uploads = 0;
        foreach (var (key, p) in _pieces)
        {
            if (p.Job is not { IsCompleted: true } job) continue;
            if (uploads >= MaxUploads) break;
            p.Job = null; _jobs--;
            if (job.IsFaulted) { GD.PrintErr($"outside ground chunk {key}: {job.Exception?.GetBaseException()}"); continue; }
            if (p.JobLevel != p.Want || p.Distance == float.MaxValue) continue;   // wanted otherwise meanwhile
            Show(key, p, job.Result, p.JobLevel);
            uploads++;
        }
        // new jobs, nearest first
        bool work = false;
        foreach (var p in _pieces.Values) if (p.Distance < float.MaxValue && p.Level != p.Want && p.Job is null) { work = true; break; }
        if (work && _jobs < MaxJobs)
            foreach (var (key, p) in _pieces.Where(kv => kv.Value.Distance < float.MaxValue && kv.Value.Level != kv.Value.Want && kv.Value.Job is null)
                         .OrderBy(kv => kv.Value.Distance).Take(MaxJobs - _jobs).ToList())
            {
                int level = p.Want;
                var (x0, z0) = (key.X * Chunk, key.Z * Chunk);
                var world = _world; var ground = _ground; var ring = Ring;
                p.JobLevel = level;
                p.Job = Task.Run(() => level < 0 ? Faithful(world, x0, z0) : ModernArrays(ground, x0, z0, level, ring is null ? null : ring.Visibility));
                _jobs++;
            }
        // gone out of reach (with a margin, so a chunk at the edge does not come and go)
        _drop.Clear();
        foreach (var (key, p) in _pieces)
            if (p.Distance == float.MaxValue && p.Job is null) _drop.Add(key);
        foreach (var key in _drop)
        {
            var p = _pieces[key];
            if (p.Mi is not null) { p.Mi.QueueFree(); _maskDirty = true; }
            _pieces.Remove(key);
        }
        UpdateMask(modern);
    }

    private void Clear()
    {
        foreach (var p in _pieces.Values) p.Mi?.QueueFree();
        if (_pieces.Count > 0) _maskDirty = true;
        _pieces.Clear();
        UpdateMask(false);
    }

    /// <summary>Whether the chunk at chunk coordinates (cx, cz) lies off the map's tiles (a whole grid cell is one tile, four chunks a side).</summary>
    private bool Outside(int cx, int cz)
    {
        int per = TerrainTiles.Tile / Chunk;
        int col = (int)Math.Floor(cx / (double)per), row = (int)Math.Floor(cz / (double)per);
        return row < 0 || col < 0 || row >= MissionFile.ZmapGrid || col >= MissionFile.ZmapGrid || _world!.Terrain.TileAt(row, col) == MissionFile.ZmapEmpty;
    }

    private void Show((int X, int Z) key, Piece p, Arrays a, int level)
    {
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = a.V;
        arrays[(int)Mesh.ArrayType.Normal] = a.N;
        if (a.C is not null) arrays[(int)Mesh.ArrayType.Color] = a.C;
        arrays[(int)Mesh.ArrayType.TexUV] = a.Uv;
        arrays[(int)Mesh.ArrayType.TexUV2] = a.Uv2;
        arrays[(int)Mesh.ArrayType.Index] = a.I;
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        p.Mi ??= new MeshInstance3D { Name = $"outside_{key.X}_{key.Z}", Layers = TerrainNode.TerrainLayer, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        if (!p.Mi.IsInsideTree()) AddChild(p.Mi);
        p.Mi.Mesh = mesh;
        if (level < 0)
        {
            p.Mi.MaterialOverride = _faithful;
            p.Mi.Position = new Vector3(key.X * ChunkSize, 0, -key.Z * ChunkSize);
        }
        else
        {
            p.Mi.MaterialOverride = Modern;
            var c = _ground!.Centre;
            p.Mi.Position = new Vector3(c.X, 0, -c.Y);
        }
        p.Level = level;
        _maskDirty = true;
    }

    // ---- the ring's mask: where these chunks are, it gives way ----

    private Image? _maskImg;
    private ImageTexture? _maskTex;
    private const int MaskCells = 32;

    private void UpdateMask(bool modern)
    {
        if (Modern is not { } m || _ground is null || (!_maskDirty && modern == _maskModern)) return;
        _maskDirty = false; _maskModern = modern;
        var drawn = _pieces.Where(kv => modern && kv.Value.Level >= 0 && kv.Value.Mi is not null).Select(kv => kv.Key).ToList();
        Drawn = _pieces.Count(kv => kv.Value.Mi is not null && kv.Value.Level != int.MinValue);
        if (drawn.Count == 0) { m.SetShaderParameter("near_cells", 0f); return; }
        int minX = drawn.Min(k => k.X), minZ = drawn.Min(k => k.Z);
        _maskImg ??= Image.CreateEmpty(MaskCells, MaskCells, false, Image.Format.R8);
        _maskImg.Fill(Colors.Black);
        foreach (var (x, z) in drawn)
            if (x - minX < MaskCells && z - minZ < MaskCells) _maskImg.SetPixel(x - minX, z - minZ, Colors.White);
        if (_maskTex is null) _maskTex = ImageTexture.CreateFromImage(_maskImg); else _maskTex.Update(_maskImg);
        var c = _ground.Centre;
        m.SetShaderParameter("near_mask", _maskTex);
        m.SetShaderParameter("near_origin", new Vector2(minX * ChunkSize - c.X, minZ * ChunkSize - c.Y));
        m.SetShaderParameter("near_cells", (float)MaskCells);
    }

    // ---- the arrays (pure CPU, any thread) ----

    private static Arrays Faithful(MissionWorld world, int x0, int z0)
    {
        var (v, n, uv, uv2, i) = TerrainNode.FaithfulArrays(world, x0, z0);
        return new Arrays(v, n, null, uv, uv2, i);
    }

    /// <summary>
    /// The height the simulation reads at sample coordinates (fx, fz): bilinear over the samples, in the same order of float
    /// operations as TerrainGround.Height, so a vertex at a quarter, half or whole sample is the simulation's height to the bit.
    /// </summary>
    public static float SimHeight(OutsideGround g, int sx, int sz, float tx, float tz)
    {
        float r00 = g.Raw(sx, sz), r10 = g.Raw(sx + 1, sz), r01 = g.Raw(sx, sz + 1), r11 = g.Raw(sx + 1, sz + 1);
        float a = r00 + (r10 - r00) * tx;
        float b = r01 + (r11 - r01) * tx;
        return (a + (b - a) * tz) * 0.1f;
    }

    /// <summary>
    /// A modern chunk at sample origin (x0, z0), <paramref name="level"/> 0..2 (1.25, 2.5, 5 m between vertices): positions centre-relative
    /// (Godot frame), heights the simulation's, the ring's shading data (sun visibility, rockiness, style; height over its base, distance
    /// from the map) from the function at the samples, bilinear between, and a skirt round the edge.
    /// </summary>
    public static Arrays ModernArrays(OutsideGround g, int x0, int z0, int level, Func<float, float, float>? visibility)
    {
        int sub = 4 >> level, n = Chunk * sub + 1;
        var c = g.Centre;
        // the function at the chunk's samples (and one beyond, for the gradients' sake no more is needed)
        var aux = new OutsideGround.Point[(Chunk + 1) * (Chunk + 1)];
        for (int j = 0; j <= Chunk; j++)
            for (int i = 0; i <= Chunk; i++) aux[j * (Chunk + 1) + i] = g.SampleAt(x0 + i, z0 + j);
        float step = 1f / sub;
        float H(int i, int j)   // vertex (i, j) of the chunk's grid, may be one past it
        {
            int si = Math.DivRem(i, sub, out int ri), sj = Math.DivRem(j, sub, out int rj);
            if (ri < 0) { ri += sub; si--; }
            if (rj < 0) { rj += sub; sj--; }
            return SimHeight(g, x0 + si, z0 + sj, ri * step, rj * step);
        }
        int skirtN = 4 * (n - 1);
        var v = new Vector3[n * n + skirtN];
        var nr = new Vector3[n * n + skirtN];
        var col = new Color[n * n + skirtN];
        var uv = new Vector2[n * n + skirtN];
        var uv2 = new Vector2[n * n + skirtN];
        float spacing = TerrainNode.SampleSize / sub;
        for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
            {
                int k = j * n + i;
                float gx = (x0 + i * step) * TerrainNode.SampleSize, gz = (z0 + j * step) * TerrainNode.SampleSize;
                float h = H(i, j);
                v[k] = new Vector3(gx - c.X, h, -(gz - c.Y));
                // the bilinear surface's slope, from the heights a vertex either side (game frame), as Godot's normal
                float dhx = (H(i + 1, j) - H(i - 1, j)) / (2 * spacing), dhz = (H(i, j + 1) - H(i, j - 1)) / (2 * spacing);
                nr[k] = new Vector3(-dhx, 1, dhz).Normalized();
                // the shading data, bilinear between the samples
                float fi = i * step, fj = j * step;
                int ai = Math.Min((int)fi, Chunk - 1), aj = Math.Min((int)fj, Chunk - 1);
                float ti = fi - ai, tj = fj - aj;
                var p00 = aux[aj * (Chunk + 1) + ai]; var p10 = aux[aj * (Chunk + 1) + ai + 1];
                var p01 = aux[(aj + 1) * (Chunk + 1) + ai]; var p11 = aux[(aj + 1) * (Chunk + 1) + ai + 1];
                float L(float a00, float a10, float a01, float a11) => (a00 * (1 - ti) + a10 * ti) * (1 - tj) + (a01 * (1 - ti) + a11 * ti) * tj;
                float hrel = L(p00.Hrel, p10.Hrel, p01.Hrel, p11.Hrel), d = L(p00.D, p10.D, p01.D, p11.D);
                float vis = visibility?.Invoke(gx - c.X, gz - c.Y) ?? 1f;
                col[k] = new Color(vis, L(p00.Rocky, p10.Rocky, p01.Rocky, p11.Rocky), L(p00.Style, p10.Style, p01.Style, p11.Style), 1);
                uv[k] = new Vector2(hrel, MathF.Max(d, 0));
                uv2[k] = new Vector2(h - g.Base, 1);   // y: one of these chunks (the ring gives way under them)
            }
        // the skirt: each edge vertex again, Skirt lower
        var edge = new List<int>(skirtN);
        for (int i = 0; i < n - 1; i++) edge.Add(i);                       // z = z0 row, along +x
        for (int j = 0; j < n - 1; j++) edge.Add(j * n + n - 1);           // x = end column, along +z
        for (int i = n - 1; i > 0; i--) edge.Add((n - 1) * n + i);         // z = end row, back along -x
        for (int j = n - 1; j > 0; j--) edge.Add(j * n);                   // x = x0 column, back along -z
        for (int e = 0; e < skirtN; e++)
        {
            int src = edge[e], k = n * n + e;
            v[k] = v[src] - new Vector3(0, Skirt, 0);
            nr[k] = nr[src]; col[k] = col[src]; uv[k] = uv[src]; uv2[k] = uv2[src];
        }
        var idx = new List<int>((n - 1) * (n - 1) * 6 + skirtN * 6);
        // which winding faces up (Godot's front face is clockwise seen from the front), as the ring finds it
        bool flip = (v[1] - v[0]).Cross(v[n] - v[0]).Y < 0;
        void Tri(int a, int b, int d) { if (!flip) { idx.Add(a); idx.Add(b); idx.Add(d); } else { idx.Add(a); idx.Add(d); idx.Add(b); } }
        for (int j = 0; j < n - 1; j++)
            for (int i = 0; i < n - 1; i++)
            {
                int a = j * n + i, b = a + 1, cc = a + n, d = cc + 1;
                Tri(a, cc, b); Tri(b, cc, d);
            }
        // the skirt's quads face outward: drawn both ways round (the material culls back faces)
        for (int e = 0; e < skirtN; e++)
        {
            int a = edge[e], b = edge[(e + 1) % skirtN], la = n * n + e, lb = n * n + (e + 1) % skirtN;
            idx.Add(a); idx.Add(b); idx.Add(la); idx.Add(b); idx.Add(lb); idx.Add(la);
            idx.Add(a); idx.Add(la); idx.Add(b); idx.Add(b); idx.Add(la); idx.Add(lb);
        }
        return new Arrays(v, nr, col, uv, uv2, [.. idx]);
    }
}
