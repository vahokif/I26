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
/// <item>Modern look: within <see cref="ModernReach"/> (or the camera's far plane), with the ring's shading and its shading data,
/// at 1.25, 2.5, 5 or 10 m between vertices by distance (<see cref="LevelRange"/>). Every level's vertices lie on the bilinear surface,
/// which along a chunk's edge (a line of samples) is the same straight pieces at 1.25 to 5 m, so those neighbours meet; a skirt hides
/// the rest (a 10 m chunk's edge cutting a sample's corner). From <see cref="MorphStart"/> they ease onto the ring's surface.</item>
/// <item>1997 look: the map's own 1997 terrain (TerrainNode's 1997 mesh, its material: the engine's light through the palette), within
/// the original's 600 m clip only, cut at 600 m view depth as the map's ground is by the far plane, whatever the draw distance; the
/// horizon panels and sky stand beyond as they do over the map.</item>
/// <item>The map editor's own view (<see cref="EditorView"/>; its playtest is a mission and draws as above): the 1997 look too draws it
/// out to <see cref="ModernReach"/>, unclipped, the map's 1997 terrain within <see cref="LevelRange"/>[2] of the camera and the modern
/// look's 10 m chunks lit as the 1997 terrain beyond (outside_1997.gdshader), easing onto the ring, which the editor draws in that light
/// too (HorizonTerrain.Use1997); and in either look a chunk's level goes by its distance from the camera in 3D, as the editor's camera
/// flies high.</item>
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
    /// <summary>
    /// Modern levels: 1.25 m between vertices nearer than LevelRange[0], 2.5 m nearer than LevelRange[1], 5 m nearer than LevelRange[2],
    /// else 10 m. The vertices are on the simulation's bilinear surface; between them a triangle stands off it by a quarter of its cell's
    /// twist over the vertices' spacing squared (5 m triangles: under a centimetre on most of the outside ground round T01, 33 cm on the
    /// roughest hundredth; at 1.25 m, 2 cm), so the levels near the car are the fine ones.
    /// </summary>
    public static readonly float[] LevelRange = [60f, 200f, 600f];
    public const int Levels = 4;
    /// <summary>
    /// From this far from the camera the modern chunks ease onto the ring's own surface (they are on it by <see cref="ModernReach"/>, so the
    /// ring, coarser, goes on where they end without a step): rendering only, the simulation keeps the outside ground's samples.
    /// </summary>
    public const float MorphStart = 800f;
    /// <summary>How far the modern chunks' skirts hang below their edges, metres.</summary>
    public const float Skirt = 30f;
    private const int MaxJobs = 4, MaxUploads = 4;

    private MissionWorld? _world;
    private OutsideGround? _ground;
    private bool _modern = true, _editorView;

    /// <summary>The map editor's own view (not its playtest): the 1997 look draws as far as the modern, unclipped, and levels go by 3D distance.</summary>
    public bool EditorView
    {
        get => _editorView;
        set
        {
            if (_editorView == value) return;
            _editorView = value;
            Clear();
        }
    }
    /// <summary>
    /// The ring's material (null until the ring is made): the modern chunks are drawn with its shading and parameters, copied into their
    /// own material (horizon_near.gdshader: no depth writes or masks, so hidden fragments are rejected before they are shaded).
    /// </summary>
    public ShaderMaterial? Modern
    {
        get => _ring;
        set
        {
            _ring = value;
            _maskDirty = true;
            if (value is null) { _near = null; return; }
            _near ??= new ShaderMaterial { Shader = GD.Load<Shader>("res://Shaders/horizon_near.gdshader") };
            foreach (var u in _near.Shader.GetShaderUniformList())
            {
                var name = u.AsGodotDictionary()["name"].AsString();
                _near.SetShaderParameter(name, value.GetShaderParameter(name));
            }
            if (_groundTex is not null) { _near.SetShaderParameter("ground_tex", _groundTex); _near.SetShaderParameter("has_ground_tex", true); }
            _near.SetShaderParameter("morph_start", MorphStart);
            _near.SetShaderParameter("morph_end", ModernReach);
            if (_modern) foreach (var p in _pieces.Values) if (p.Level >= 0 && p.Mi is not null) p.Mi.MaterialOverride = _near;
        }
    }
    private ShaderMaterial? _ring, _near;
    private Texture2D? _groundTex;
    /// <summary>The ring, for its baked sun visibility, its heights (the far chunks ease onto them) and its near mask.</summary>
    public HorizonTerrain? Ring
    {
        get => _ringNode;
        set
        {
            if (_ringNode == value) return;
            _ringNode = value;
            // the chunks made before it (the editor's 1997 view, while the ring builds) are made again with its heights, each shown until then
            foreach (var p in _pieces.Values) if (p.Level >= 0) p.Stale = true;
        }
    }
    private HorizonTerrain? _ringNode;
    /// <summary>The ring's 1997 material in the map editor (HorizonTerrain.Material1997), which takes the near mask too.</summary>
    public ShaderMaterial? Ring1997 { get => _ring1997; set { _ring1997 = value; _maskDirty = true; } }
    private ShaderMaterial? _ring1997;
    private ShaderMaterial? _faithful, _faithfulOpen, _near1997;

    private sealed class Piece
    {
        public MeshInstance3D? Mi;
        public int Level = int.MinValue;      // shown: 0..3 modern, -1 1997
        public Task<Arrays>? Job;
        public int JobLevel;
        public float Distance;
        public int Want;
        public bool Stale;                    // made before the ring was there: made again
        public float Top = float.NaN;         // the highest vertex shown (for the 3D distance)
    }
    private readonly Dictionary<(int X, int Z), Piece> _pieces = [];
    private readonly List<(int X, int Z)> _drop = [];
    private int _jobs;

    /// <summary>A chunk's arrays: chunk-local vertices for the 1997 look (the node stands at the chunk's corner), centre-relative for the modern look (at the ring's centre).</summary>
    public sealed record Arrays(Vector3[] V, Vector3[] N, Color[]? C, Vector2[] Uv, Vector2[] Uv2, int[] I);

    /// <summary>Chunks drawn.</summary>
    public int Drawn { get; private set; }
    private bool _maskDirty = true, _maskRing;

    public void Setup(MissionWorld world, ShaderMaterial? faithful)
    {
        _world = world;
        _ground = world.Outside;
        if (faithful is not null)
        {
            _groundTex = faithful.GetShaderParameter("ground_tex").As<Texture2D>();
            _faithful = (ShaderMaterial)faithful.Duplicate();
            _faithful.SetShaderParameter("clip_depth", ClipDepth(false));
            _faithfulOpen = (ShaderMaterial)faithful.Duplicate();
            _faithfulOpen.SetShaderParameter("clip_depth", ClipDepth(true));
            _near1997 = Lit1997("res://Shaders/outside_1997.gdshader", faithful, world.Outside?.Centre ?? default);
            _near1997.SetShaderParameter("morph_start", MorphStart);
            _near1997.SetShaderParameter("morph_end", ModernReach);
        }
    }

    /// <summary>
    /// A material of the outside ground's 1997 shading in the map editor (outside_1997.gdshaderinc): the map's 1997 terrain's light and
    /// ground texture (its parameters copied from <paramref name="faithful"/>), on meshes centre-relative to <paramref name="centre"/>.
    /// </summary>
    public static ShaderMaterial Lit1997(string shader, ShaderMaterial faithful, System.Numerics.Vector2 centre)
    {
        var m = new ShaderMaterial { Shader = GD.Load<Shader>(shader) };
        foreach (var u in m.Shader.GetShaderUniformList())
        {
            var name = u.AsGodotDictionary()["name"].AsString();
            var v = faithful.GetShaderParameter(name);
            if (v.VariantType != Variant.Type.Nil) m.SetShaderParameter(name, v);
        }
        m.SetShaderParameter("centre", new Vector2(centre.X, centre.Y));
        return m;
    }

    /// <summary>How far round the camera chunks are drawn (the far plane aside): the modern look's reach, the original's clip in a mission's 1997 look.</summary>
    public static float Reach(bool modern, bool editorView) => modern || editorView ? ModernReach : Reach1997;

    /// <summary>The view depth the 1997 look's chunks of the map's own terrain are cut at (0: none): the original's clip but in the map editor's own view.</summary>
    public static float ClipDepth(bool editorView) => editorView ? 0 : Reach1997;

    /// <summary>
    /// The distance a chunk's level goes by: across the ground (<paramref name="across"/>, to the chunk's box), and in the map editor's
    /// view, whose camera flies high, in 3D, <paramref name="above"/> the eye's height over the chunk (its highest vertex shown, else the
    /// ground under the eye).
    /// </summary>
    public static float LevelDistance(bool editorView, float across, float above)
    {
        if (!editorView) return across;
        above = MathF.Max(above, 0);
        return MathF.Sqrt(across * across + above * above);
    }

    /// <summary>
    /// The level a chunk is drawn at, <paramref name="distance"/> from the camera (<see cref="LevelDistance"/>), <paramref name="shown"/>
    /// the level it shows (int.MinValue: none): modern, 0..3 by <see cref="LevelRange"/>; 1997, -1 (the map's own 1997 terrain), and in the
    /// map editor's view the 10 m level (3) past <see cref="LevelRange"/>[2]. Back to a finer level only well inside its range.
    /// </summary>
    public static int Level(bool modern, bool editorView, float distance, int shown)
    {
        if (modern)
        {
            int want = Levels - 1;
            for (int l = 0; l < LevelRange.Length; l++) if (distance < LevelRange[l]) { want = l; break; }
            if (shown >= 0 && want < shown && distance > LevelRange[want] - 10) want = shown;
            return want;
        }
        if (!editorView) return -1;
        float near = LevelRange[^1];
        return distance < near && !(shown == Levels - 1 && distance > near - 10) ? -1 : Levels - 1;
    }

    public void UseModern(bool modern)
    {
        if (_modern == modern) return;
        _modern = modern;
        Clear();   // every chunk made again in the other look
    }

    /// <summary>I76_OUTSIDE_DEBUG=1: a line every 30 frames, what is wanted, drawn and being made.</summary>
    private static readonly bool Debug = System.Environment.GetEnvironmentVariable("I76_OUTSIDE_DEBUG") == "1";

    /// <summary>Kept this far past the reach, so a chunk at its edge does not come and go.</summary>
    private const float Keep = 100f;

    public override void _Process(double delta)
    {
        if (_ground is null || _world is null || GetViewport()?.GetCamera3D() is not { } cam) return;
        var eye = ToLocal(cam.GlobalPosition);
        float ex = eye.X, ez = -eye.Z;   // game frame
        bool modern = _modern, open = !modern && _editorView;   // open: the editor's 1997 view, as far as the modern look
        if (modern && Modern is null) { Clear(); return; }
        float reach = MathF.Min(Reach(modern, _editorView), cam.Far + ChunkSize);
        // the editor's camera flies high: there a chunk's level goes by its distance in 3D (to the highest of it shown, else the ground under the eye)
        float eyeGround = 0;
        if (_editorView)
        {
            int sx = (int)MathF.Floor(ex / TerrainNode.SampleSize), sz = (int)MathF.Floor(ez / TerrainNode.SampleSize);
            eyeGround = (float)((_world.Terrain.Sample(sx, sz) is { } v ? v & MissionFile.HeightMask : _ground.Raw(sx, sz)) * MissionWorld.HeightScale);
        }
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
                p.Want = Level(modern, _editorView, LevelDistance(_editorView, d, eye.Y - (float.IsNaN(p.Top) ? eyeGround : p.Top)), p.Level);
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
        foreach (var p in _pieces.Values) if (p.Distance < float.MaxValue && (p.Level != p.Want || p.Stale) && p.Job is null) { work = true; break; }
        if (work && _jobs < MaxJobs)
            foreach (var (key, p) in _pieces.Where(kv => kv.Value.Distance < float.MaxValue && (kv.Value.Level != kv.Value.Want || kv.Value.Stale) && kv.Value.Job is null)
                         .OrderBy(kv => kv.Value.Distance).Take(MaxJobs - _jobs).ToList())
            {
                int level = p.Want;
                var (x0, z0) = (key.X * Chunk, key.Z * Chunk);
                var world = _world; var ground = _ground; var ring = Ring;
                p.JobLevel = level;
                p.Stale = false;
                p.Job = Task.Run(() => level < 0 ? Faithful(world, x0, z0) : ModernArrays(ground, x0, z0, level, ring is null ? null : ring.Visibility, ring is null ? null : ring.RingHeight));
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
        UpdateMask(modern || open);
        if (Debug && Engine.GetProcessFrames() % 30 == 0)
            GD.Print($"outside: {(modern ? "modern" : open ? "1997 (editor)" : "1997")}, eye {ex:0},{eye.Y:0},{ez:0}, reach {reach:0}, {_pieces.Count} chunks wanted, {Drawn} drawn, {_jobs} jobs, faithful {(_faithful is null ? "none" : "set")}, levels {string.Join(",", _pieces.Values.GroupBy(q => (q.Level, q.Want, q.Job is null, q.Mi is null)).Select(g => $"{g.Key}x{g.Count()}"))}");
    }

    private void Clear()
    {
        foreach (var p in _pieces.Values)
        {
            p.Mi?.QueueFree();
            if (p.Job is not null) _jobs--;   // left to finish on its own, unread
        }
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
        if (level >= 0)
        {
            // the box takes in the ring's heights too, which the far chunks ease onto in the shader
            var box = mesh.GetAabb();
            float lo = box.Position.Y, hi = box.End.Y;
            for (int k = 0; k < a.Uv2.Length; k++) if (a.Uv2[k].Y > 0.5f) { lo = MathF.Min(lo, a.Uv2[k].X); hi = MathF.Max(hi, a.Uv2[k].X); }
            mesh.CustomAabb = new Aabb(new Vector3(box.Position.X, lo, box.Position.Z), new Vector3(box.Size.X, hi - lo, box.Size.Z));
        }
        p.Mi ??= new MeshInstance3D { Name = $"outside_{key.X}_{key.Z}", Layers = TerrainNode.TerrainLayer, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        if (!p.Mi.IsInsideTree()) AddChild(p.Mi);
        p.Mi.Mesh = mesh;
        if (level < 0)
        {
            p.Mi.MaterialOverride = _editorView ? _faithfulOpen : _faithful;
            p.Mi.Position = new Vector3(key.X * ChunkSize, 0, -key.Z * ChunkSize);
        }
        else
        {
            p.Mi.MaterialOverride = _modern ? _near : _near1997;
            var c = _ground!.Centre;
            p.Mi.Position = new Vector3(c.X, 0, -c.Y);
        }
        p.Top = mesh.GetAabb().End.Y;
        p.Level = level;
        _maskDirty = true;
    }

    // ---- the ring's mask: where these chunks are, it gives way ----

    private Image? _maskImg;
    private ImageTexture? _maskTex;
    private const int MaskCells = 32;

    /// <param name="ring">Whether the ring is drawn round these chunks (the modern look, the editor's 1997 view): it gives way under every one drawn.</param>
    private void UpdateMask(bool ring)
    {
        if (_ground is null || (!_maskDirty && ring == _maskRing)) return;
        _maskDirty = false; _maskRing = ring;
        Drawn = _pieces.Count(kv => kv.Value.Mi is not null && kv.Value.Level != int.MinValue);
        var mats = new[] { Modern, _ring1997 };
        if (mats.All(m => m is null)) return;
        var drawn = _pieces.Where(kv => ring && kv.Value.Level != int.MinValue && kv.Value.Mi is not null).Select(kv => kv.Key).ToList();
        if (drawn.Count == 0) { foreach (var m in mats) m?.SetShaderParameter("near_cells", 0f); return; }
        int minX = drawn.Min(k => k.X), minZ = drawn.Min(k => k.Z);
        _maskImg ??= Image.CreateEmpty(MaskCells, MaskCells, false, Image.Format.R8);
        _maskImg.Fill(Colors.Black);
        foreach (var (x, z) in drawn)
            if (x - minX < MaskCells && z - minZ < MaskCells) _maskImg.SetPixel(x - minX, z - minZ, Colors.White);
        if (_maskTex is null) _maskTex = ImageTexture.CreateFromImage(_maskImg); else _maskTex.Update(_maskImg);
        var c = _ground.Centre;
        foreach (var m in mats)
        {
            if (m is null) continue;
            m.SetShaderParameter("near_mask", _maskTex);
            m.SetShaderParameter("near_origin", new Vector2(minX * ChunkSize - c.X, minZ * ChunkSize - c.Y));
            m.SetShaderParameter("near_cells", (float)MaskCells);
        }
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
    /// A modern chunk at sample origin (x0, z0), <paramref name="level"/> 0..3 (1.25, 2.5, 5, 10 m between vertices): positions centre-relative
    /// (Godot frame), heights the simulation's (at 10 m, every other sample's: far off, where the cut corners do not show), the ring's
    /// shading data (sun visibility, rockiness, style; height over its base, distance from the map) from the function at the samples,
    /// bilinear between, and a skirt round the edge.
    /// </summary>
    public static Arrays ModernArrays(OutsideGround g, int x0, int z0, int level, Func<float, float, float>? visibility, Func<float, float, float>? ringHeight = null)
    {
        int quarters = 1 << level;   // quarter samples between vertices
        int n = Chunk * 4 / quarters + 1;
        var c = g.Centre;
        // the function at the chunk's samples (and one beyond, for the gradients' sake no more is needed)
        var aux = new OutsideGround.Point[(Chunk + 1) * (Chunk + 1)];
        for (int j = 0; j <= Chunk; j++)
            for (int i = 0; i <= Chunk; i++) aux[j * (Chunk + 1) + i] = g.SampleAt(x0 + i, z0 + j);
        float step = quarters / 4f;   // samples between vertices
        float H(int i, int j)   // vertex (i, j) of the chunk's grid, may be one past it
        {
            int si = Math.DivRem(i * quarters, 4, out int ri), sj = Math.DivRem(j * quarters, 4, out int rj);
            if (ri < 0) { ri += 4; si--; }
            if (rj < 0) { rj += 4; sj--; }
            return SimHeight(g, x0 + si, z0 + sj, ri * 0.25f, rj * 0.25f);
        }
        int skirtN = 4 * (n - 1);
        var v = new Vector3[n * n + skirtN];
        var nr = new Vector3[n * n + skirtN];
        var col = new Color[n * n + skirtN];
        var uv = new Vector2[n * n + skirtN];
        var uv2 = new Vector2[n * n + skirtN];
        float spacing = TerrainNode.SampleSize * step;
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
                // the ring's own height here, which the shader eases the far chunks onto (horizon_near.gdshader)
                float rh = ringHeight?.Invoke(gx - c.X, gz - c.Y) ?? float.NaN;
                uv2[k] = float.IsNaN(rh) ? Vector2.Zero : new Vector2(rh, 1);
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
            nr[k] = nr[src]; col[k] = col[src]; uv[k] = uv[src]; uv2[k] = uv2[src] - new Vector2(Skirt, 0);
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
