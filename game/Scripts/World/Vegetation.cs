using System.Collections.Concurrent;
using Godot;
using I76.Formats.Assembly;
using I76.Formats.Vegetation;
using I76.Game.Sim;

namespace I76.Game.World;

/// <summary>
/// The modern look's procedural plants (docs/modern-vegetation.md, a deviation: the original placed almost none).
/// Visual only: no collision, nothing the simulation reads. The mission's biome (<see cref="Biomes"/>) says what grows;
/// <see cref="VegetationPlacer"/> says where, cell by cell, the same every time. The ground is cut into
/// chunks (<see cref="ChunkSize"/>: 160 m for shrubs, 80 m for the ground layer); the chunks within reach of the camera are placed on worker threads and drawn as
/// one MultiMesh per species variant (the full mesh near, the lighter one far), the rest dropped. Plants fade out by
/// a dither with distance; only the nearest shrubs cast sun shadows.
/// </summary>
public partial class VegetationNode : Node3D
{
    /// <summary>A layer's chunk side (metres): the shrubs' reach is long, so theirs are larger (fewer nodes to draw).</summary>
    public static float ChunkSize(VegLayer layer) => layer == VegLayer.Shrub ? 160f : 80f;

    private VegLevels.Level _level = VegLevels.Low;
    private int _levelIndex;
    private bool _night;
    private VegetationPlacer _placer = null!;
    // one material a kind of plant (Kind: the shrubs, the grass tufts, the ground layer's larger plants), which differ in how
    // far they reach, and per detail tier, which differ in the band they cross-fade in (TierBands); [kind][tier]
    private readonly ShaderMaterial[][] _mats = [.. Enumerable.Range(0, 3).Select(_ => Enumerable.Range(0, 3).Select(_ => new ShaderMaterial { Shader = GD.Load<Shader>("res://Shaders/vegetation.gdshader") }).ToArray())];
    private ShaderMaterial _material => _mats[0][0];
    private ShaderMaterial _groundMaterial => _mats[1][0];
    private void Both(string p, Variant v) { foreach (var l in _mats) foreach (var m in l) m.SetShaderParameter(p, v); }
    // the shade under the shrubs (vegetation_shade.gdshader): a quad each, one MultiMesh a chunk
    private readonly ShaderMaterial _shadeMaterial = new() { Shader = GD.Load<Shader>("res://Shaders/vegetation_shade.gdshader") };
    private static readonly ArrayMesh ShadeQuad = MakeShadeQuad();
    // per species (the placer's order), per variant: the full, middle and far meshes, the plant's height, and its shade on the ground
    private (ArrayMesh[] Tiers, float Height, float Shade, float ShadeRadius)[][] _meshes = [];
    private float[] _stiffness = [];
    private bool[] _grass = [];

    /// <summary>
    /// What a plant is drawn as (its material, its reach): 0 a shrub, 1 a grass tuft, 2 the ground layer's other plants (the
    /// forbs, weeds and rosettes: a tumbleweed or a snakeweed is a small bush in the headlamps, and reaches as far as the shrubs).
    /// </summary>
    private int Kind(VegLayer layer, int species) => layer == VegLayer.Shrub ? 0 : _grass[species] ? 1 : 2;

    /// <summary>A chunk's plants of one species variant: their MultiMesh data, and a node per detail tier in use.</summary>
    private sealed class Group
    {
        public int Species, Variant, Count;
        public float[] Buffer = [];
        public readonly MultiMeshInstance3D?[] Nodes = new MultiMeshInstance3D?[3];
    }

    private sealed class Chunk
    {
        public int Cx, Cz;
        public VegLayer Layer;
        public Task<List<Group>>? Job;
        public List<Group> Groups = [];
        public List<Group> FarGroups = [];   // the groups merged by species (variant 0's meshes): the chunk's draws while it lies wholly in the far tier, or in the middle one far off
        public bool Attached;
        public int Tier = -1;        // every plant in this tier, or 3: split per plant
        public System.Numerics.Vector2 SplitAt;   // where the eye was at the last per-plant split
        public bool Casting;
        public bool GrassOn;         // the ground layer's grass tufts drawn (the chunk within their reach; its other plants reach farther)
        public bool Merged;          // drawn by FarGroups now
        public float Distance, Far;
        public float[] Shade = [];   // the shrubs' shade quads (16 floats each: the transform's rows, custom data), made with the groups
        public MultiMeshInstance3D? ShadeNode;
        public PlantIndex? Index;    // the plants by place, for the brush impacts (BrushImpacts), made with the groups
    }

    private readonly Dictionary<(int, int, VegLayer), Chunk> _chunks = [];
    private readonly ConcurrentQueue<Chunk> _done = new();
    private int _jobs, _statFrame;
    private static readonly int MaxJobs = Math.Clamp(System.Environment.ProcessorCount / 2, 2, 8);
    private const int MaxAttachPerFrame = 8;
    /// <summary>A ground-layer plant shades the ground under it when it reaches this far (metres): the rosettes, tussocks and tumbleweeds, not the grass tufts.</summary>
    private const float GroundShadeMin = 0.28f;
    /// <summary>A chunk split per plant redoes the split when the eye has moved this far.</summary>
    private const float Resplit = 2.5f;
    /// <summary>The eye moves this far (metres) between looks for chunks to place (well inside the 40 m they are placed ahead).</summary>
    private const float LookEvery = 4f;
    private System.Numerics.Vector2 _lookedAt = new(float.MaxValue, float.MaxValue);
    private bool _lookPending;
    private static readonly bool StatsOn = System.Environment.GetEnvironmentVariable("I76_VEG_STATS") == "1";
    /// <summary>A chunk wholly in the middle tier merges its variants too from this far (metres): past the beam's brightest part, where one crown shape a species does not show.</summary>
    private const float MergeMiddleFrom = 60f;
    public int Instances { get; private set; }
    public int DrawnNodes { get; private set; }
    public string Stats { get; private set; } = "";

    /// <summary>The setting, with I76_VEG (0, low, high) over it; 0: none.</summary>
    public static int LevelFor(int setting) => System.Environment.GetEnvironmentVariable("I76_VEG")?.ToLowerInvariant() switch
    {
        "0" or "off" => 0, "1" or "low" => 1, "2" or "high" => 2, _ => Math.Clamp(setting, 0, 2),
    };

    /// <summary>
    /// The mission's plants, or null when its biome is none or the modern terrain is not built. The meshes, the atlas
    /// and the masks are made here (the pure parts on worker threads); nothing is placed until <see cref="Prewarm"/>
    /// or the first frames.
    /// </summary>
    public static VegetationNode? Create(MissionBuilder b, int level)
    {
        if (b.World is not { } world || b.Terrain?.Surface is not { } surface || Biomes.For(world.Mission.Name, b.Settings?.Ground) is not { } region) return null;
        var biome = region.Biome;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var node = new VegetationNode { Name = "Vegetation" };
        var terrain = new SurfaceTerrain(surface, world.Terrain);
        ulong seed = VegHash.Of(world.Mission.Name);
        // the pure work at once on the pool: the atlas, every species variant's meshes, the masks, the river bottom
        _ = AtlasJob;
        var lowTask = region.LowGround ? Task.Run(() => SurfaceTerrain.LowLevel(surface, world.Terrain)) : Task.FromResult(0f);
        MissionBuilder.Trace("veg: start");
        var mask = b.Assembler is { } asm ? VegMask.ForMission(world, asm) : new VegMask();
        MissionBuilder.Trace("veg: mask");
        var gallery = Gallery(mask, biome);
        MissionBuilder.Trace("veg: gallery");
        var dummy = new VegetationPlacer(biome, new VegetationParams(), terrain, mask, seed);
        var species = dummy.Species;
        var meshData = new PlantMesh[species.Count][][];
        // a job per variant (each bakes its occlusion, the costly part)
        for (int i = 0; i < species.Count; i++) meshData[i] = new PlantMesh[species[i].Variants][];
        var jobs = species.SelectMany((s, i) => Enumerable.Range(0, s.Variants).Select(v => (i, v))).ToArray();
        Parallel.ForEach(jobs, j =>
        {
            var s = species[j.i];
            meshData[j.i][j.v] = s.BuildTiers(VegHash.Mix(VegHash.Of(s.Id), (ulong)j.v));
        });
        long tMesh = sw.ElapsedMilliseconds;
        MissionBuilder.Trace("veg: meshes");
        node._meshes = [.. meshData.Select(vs => vs.Select(p => (p.Select(ToMesh).ToArray(), p[0].Bounds().Max.Y, p[0].GroundShade, p[0].GroundRadius)).ToArray())];
        node._stiffness = [.. species.Select(s => s.Stiffness)];
        node._grass = [.. species.Select(s => s.Grass)];
        node.Both("atlas", AtlasTexture());
        float low = lowTask.Result;
        MissionBuilder.Trace("veg: low level");
        node._lowLevel = low;
        node._terrain = terrain; node._mask = mask; node._region = region; node._seed = seed;
        // the sward between the plants, for the ground shader: the whole map, on the pool (Prewarm waits a little for it)
        // (a map too big for a surface over all of it reads its samples' own heights: the sward reads every texel of it)
        IVegTerrain swardGround = world.Terrain.Huge ? new TilesTerrain(world.Terrain) : terrain;
        node._swardJob = Task.Run(() => SwardMap.Build(new VegetationPlacer(biome, new VegetationParams { LowLevel = low, LowGround = region.LowGround }, swardGround, mask, seed), world.Terrain));
        node.SetGround(b);
        if (int.TryParse(System.Environment.GetEnvironmentVariable("I76_VEG_DEBUG"), out int dv)) node.Both("debug_view", dv);
        // the plants sway toward the one wind the tumbleweeds roll in and the smoke drifts in (Godot's frame: Z mirrored)
        node.Both("wind_dir", new Vector2(Wind.Direction.X, -Wind.Direction.Y));
        // the rolling tumbleweeds, where the biome has them: the ground layer's material, made to roll
        if (PlantsOn && (region.Biome.Tumbleweeds > 0 || System.Environment.GetEnvironmentVariable("I76_TUMBLEWEEDS") is { Length: > 0 } tw && tw != "0"))
        {
            node._tumble = TumbleweedNode.Create(region, terrain, mask, () => node._placer, low, seed, (ShaderMaterial)node._groundMaterial.Duplicate(), ToMesh);
            node.AddChild(node._tumble);
        }
        // the plants answer the fighting (shell.cfg [port] brush_impacts): shaken, laid down, scorched
        node.AddChild(new BrushImpacts { Name = "BrushImpacts", Veg = node, Tint = node._tint });
        node.SetLevel(level);
        if (gallery is { } g) node.AddGallery(g.X, g.Z, g.Species);
        int verts = meshData.Sum(vs => vs.Sum(p => p[0].VertexCount));
        node.Stats = $"vegetation: biome {biome.Name}{(region.Density != 1 ? $" x{region.Density:0.##}" : "")}, {species.Count} species, {meshData.Sum(v => v.Length)} variants ({verts} vertices), meshes {tMesh} ms, " +
            $"made in {sw.ElapsedMilliseconds} ms; masks: {mask.Roads} road quads, {mask.Footprints} footprints, {mask.Lines} line segments" + (region.LowGround ? $"; low ground at {low:0.0} m" : "") + (node._tumble is { } tn ? $"; {tn.Stats}" : "");
        GD.Print(node.Stats);
        return node;
    }

    private TumbleweedNode? _tumble;
    private Task<SwardMap>? _swardJob;
    private SwardMap? _sward;
    private Color _swardColour;
    private float _swardStrength;
    private bool _reassert;

    /// <summary>The sward's tint set again at the next frame (another scene's plants reset it as they left).</summary>
    public void Reassert() => _reassert = true;

    /// <summary>
    /// The sward into the ground shader's globals (terrain_modern.gdshader) once it is made: its texture and where it
    /// lies at once, its strength eased in over half a second if the mission is already showing (none when hidden or Off).
    /// </summary>
    private void UpdateSward(double delta, bool now = false)
    {
        if (_sward is null && _swardJob is { IsCompleted: true } job)
        {
            _swardJob = null;
            if (job.IsCompletedSuccessfully && job.Result.Width > 0)
            {
                var map = _sward = job.Result;
                var img = Image.CreateFromData(map.Width, map.Height, false, Image.Format.R8, map.Data);
                RenderingServer.GlobalShaderParameterSet("i76_sward", ImageTexture.CreateFromImage(img));
                RenderingServer.GlobalShaderParameterSet("i76_sward_rect", new Vector4(map.X0, map.Z0, 1f / (map.Width * map.TexelSize), 1f / (map.Height * map.TexelSize)));
                GD.Print($"vegetation: sward {map.Width} x {map.Height} texels, mean {map.Mean:0.00}");
            }
            else if (job.IsFaulted) GD.PrintErr($"vegetation sward: {job.Exception?.GetBaseException().Message}");
        }
        float target = _sward is not null && Visible && _placer is not null && SwardOn ? 1f : 0f;
        float k = now ? target : Mathf.MoveToward(_swardStrength, target, (float)delta * 2f);
        if (k == _swardStrength && !now && !_reassert) return;
        _reassert = false;
        _swardStrength = k;
        RenderingServer.GlobalShaderParameterSet("i76_sward_tint", new Vector4(_swardColour.R, _swardColour.G, _swardColour.B, k));
    }

    /// <summary>I76_VEG_PLANTS=0, a capture hook: the plants and their shade placed but not drawn, and no tumbleweeds; the sward
    /// (the grass worked into the ground) kept: the ground as with vegetation on, nothing standing on it.</summary>
    private static readonly bool PlantsOn = System.Environment.GetEnvironmentVariable("I76_VEG_PLANTS") != "0";

    /// <summary>I76_SWARD=0 leaves the sward off (for A/B captures and profiles).</summary>
    private static readonly bool SwardOn = System.Environment.GetEnvironmentVariable("I76_SWARD") != "0";

    public override void _ExitTree() => RenderingServer.GlobalShaderParameterSet("i76_sward_tint", Vector4.Zero);

    private SurfaceTerrain _terrain = null!;
    private VegMask _mask = null!;
    private VegRegion _region = null!;
    private ulong _seed;
    private float _lowLevel;

    /// <summary>Off (0) hides and drops everything; Low (1) and High (2) re-place every chunk at their density and reach.</summary>
    public void SetLevel(int level)
    {
        Clear();
        Visible = level > 0;
        _levelIndex = level;
        _tumble?.SetLevel(level);
        if (level == 0) { _placer = null!; UpdateSward(0, now: true); return; }
        ApplyReach();
        _placer = new VegetationPlacer(_region.Biome, new VegetationParams { Density = _level.Density * _region.Density, LowLevel = _lowLevel, LowGround = _region.LowGround }, _terrain, _mask, _seed);
    }

    /// <summary>
    /// The level's distances for the headlamps' reach and the mission's light now (VegLevels.For) into the materials; the chunks
    /// are re-tiered at the next frame and placed or dropped by the new reach (nothing is placed again).
    /// </summary>
    private void ApplyReach()
    {
        _night = I76.Game.Sim.Daylight.Current <= 0;
        _level = VegLevels.For(_levelIndex, VegLevels.LampReach, _night);
        foreach (var c in _chunks.Values) c.Tier = -1;
        _lookPending = true;
        for (int l = 0; l < 3; l++)
        {
            float mid = l == 0 ? _level.ShrubMid : _level.GroundMid, far = l == 0 ? _level.ShrubFar : _level.GroundFar;
            var (m0, m1) = TierBands.Band(mid);
            var (f0, f1) = TierBands.Band(far);
            for (int t = 0; t < 3; t++)
            {
                var m = _mats[l][t];
                m.SetShaderParameter("fade_start", l == 0 ? _level.ShrubFade : l == 1 ? _level.GrassFade : _level.ForbFade);
                m.SetShaderParameter("fade_end", l == 0 ? _level.ShrubEnd : l == 1 ? _level.GrassEnd : _level.ForbEnd);
                // the grass tufts fade in within the beam: they grow out of the sward as they do
                m.SetShaderParameter("grow_in", l == 1);
                m.SetShaderParameter("tier_in", t == 1 ? new Vector2(m0, m1) : t == 2 ? new Vector2(f0, f1) : Vector2.Zero);
                m.SetShaderParameter("tier_out", t == 0 ? new Vector2(m0, m1) : t == 1 ? new Vector2(f0, f1) : Vector2.Zero);
            }
        }
        _shadeMaterial.SetShaderParameter("fade_start", _level.ShadeFade);
        _shadeMaterial.SetShaderParameter("fade_end", _level.ShadeEnd);
    }

    /// <summary>
    /// The ground's colour into the plants: a pull toward it overall (its colour carries the mission's light, which
    /// the painted plants don't), less light on a dark ground, and more of it as dust at their bases.
    /// </summary>
    private Color _tint = Colors.White;

    private void SetGround(MissionBuilder b)
    {
        var g = GroundLook(b);
        Both("tint", g.Tint);
        _tint = g.Tint;
        Both("dust", g.Dust);
        Both("saturation", g.Saturation);
        _swardColour = g.Sward;
    }

    private static Task<byte[]>? _atlasJob;
    private static ImageTexture? _atlas;

    /// <summary>The plants' atlas (FoliageAtlas), painted once on the pool and kept: the vegetation's and the placed trees' (PlacedTrees).</summary>
    public static Task<byte[]> AtlasJob => _atlasJob ??= Task.Run(() => FoliageAtlas.Build());

    /// <summary>The atlas as a texture (waits for <see cref="AtlasJob"/>; main thread).</summary>
    public static ImageTexture AtlasTexture() =>
        _atlas ??= ImageTexture.CreateFromImage(Image.CreateFromData(FoliageAtlas.Width, FoliageAtlas.Height, true, Image.Format.Rgba8, AtlasJob.Result));

    /// <summary>
    /// The ground's colour into the plants (the vegetation's, the placed trees'): a pull toward it overall (its colour
    /// carries the mission's light, which the painted plants don't), less light on a dark ground, more of it as dust at
    /// their bases, the ground's saturation; and the sward's dry grass under the same light.
    /// </summary>
    public static (Color Tint, Color Dust, float Saturation, Color Sward) GroundLook(MissionBuilder b)
    {
        var ground = new Color(0.55f, 0.4f, 0.3f);
        if (b.TerrainMaterial?.GetShaderParameter("ground_tex").As<Texture2D>() is { } gt && gt.GetImage() is { } gi)
        {
            if (gi.IsCompressed()) gi.Decompress();
            gi.Resize(1, 1, Image.Interpolation.Lanczos);
            ground = gi.GetPixel(0, 0);
        }
        float m = Mathf.Max(ground.R, Mathf.Max(ground.G, ground.B));
        var norm = m > 0 ? new Color(ground.R / m, ground.G / m, ground.B / m) : Colors.White;
        var lin = ground.SrgbToLinear();
        float lum = 0.2126f * lin.R + 0.7152f * lin.G + 0.0722f * lin.B;
        // on a dark ground (T15-T17's red earth) the painted plants stood out pale against it, as if lit apart from it: darker there
        float albedo = Mathf.Clamp(Mathf.Sqrt(lum / 0.085f), 0.78f, 1f);
        // a grey ground (T04, the dream in black and white) takes the colour out of the plants too
        float chroma = m > 0 ? (m - Mathf.Min(ground.R, Mathf.Min(ground.G, ground.B))) / m : 0;
        float sat = Mathf.SmoothStep(0.03f, 0.12f, chroma);
        // the sward on the ground: dry grass, straw and grey-green, under the same light as the plants
        var grass = new Color(0.44f, 0.41f, 0.3f).SrgbToLinear() * (Colors.White.Lerp(norm, 0.22f) * albedo);
        float gl = 0.2126f * grass.R + 0.7152f * grass.G + 0.0722f * grass.B;
        GD.Print($"vegetation: ground {ground.ToHtml(false)}, chroma {chroma:0.00}, tint {albedo:0.00}");
        return (Colors.White.Lerp(norm, 0.22f) * albedo, ground, sat, new Color(gl, gl, gl).Lerp(grass, sat));
    }

    /// <summary>
    /// The map editor: the ground changed under the rectangle (game coordinates, metres). The chunks it touches are dropped, and
    /// the next frames place them again on the new ground (the nearest first).
    /// </summary>
    public int Invalidate(float gx0, float gz0, float gx1, float gz1)
    {
        List<(int, int, VegLayer)>? drop = null;
        foreach (var (key, c) in _chunks)
        {
            float cs = ChunkSize(c.Layer);
            if ((c.Cx + 1) * cs < gx0 || c.Cx * cs > gx1 || (c.Cz + 1) * cs < gz0 || c.Cz * cs > gz1) continue;
            (drop ??= []).Add(key);
        }
        if (drop is null) return 0;
        foreach (var k in drop) { Detach(_chunks[k]); _chunks.Remove(k); }
        _lookPending = true;
        return drop.Count;
    }

    private void Clear()
    {
        foreach (var c in _chunks.Values) Detach(c);
        _chunks.Clear();
        Instances = 0;
        _lookPending = true;
    }

    private void Detach(Chunk c)
    {
        foreach (var g in c.Groups.Concat(c.FarGroups))
            for (int t = 0; t < 3; t++) { g.Nodes[t]?.QueueFree(); g.Nodes[t] = null; }
        c.FarGroups.Clear();
        if (c.Attached) Instances -= c.Groups.Sum(g => g.Count);
        c.ShadeNode?.QueueFree();
        c.ShadeNode = null;
        c.Groups.Clear();
        c.Attached = false;
    }

    // ---- the species gallery (a capture hook) --------------------------------------------------------------

    /// <summary>
    /// I76_VEG_GALLERY=x,z[,id,id...]: the given species (else the biome's; <c>all</c>: every one) in a row along game X
    /// from (x, z), 4.5 m apart (I76_VEG_GALLERY_STEP), their variants in rows behind; nothing else grows there.
    /// Species outside the mission's biome are built for it.
    /// </summary>
    private static (float X, float Z, SpeciesDef[] Species)? Gallery(VegMask mask, Biome biome)
    {
        if (System.Environment.GetEnvironmentVariable("I76_VEG_GALLERY") is not { Length: > 0 } g) return null;
        var parts = g.Split(',');
        float x = float.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture), z = float.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
        var ids = parts.Length > 2 ? parts[2..] : [];
        SpeciesDef[] species = ids is ["all"] ? [.. SpeciesCatalog.All.Values]
            : ids.Length > 0 ? [.. ids.Where(SpeciesCatalog.All.ContainsKey).Select(id => SpeciesCatalog.All[id])]
            : [.. biome.All.Select(e => SpeciesCatalog.All[e.Id])];
        float len = species.Length * GalleryStep;
        mask.AddFootprint(new(x + len * 0.5f - 2, z + 10), System.Numerics.Vector2.UnitX, len * 0.5f + 10, 22);
        return (x, z, species);
    }

    /// <summary>The gallery's spacing (metres): 4.5, or I76_VEG_GALLERY_STEP (closer for the small plants).</summary>
    private static float GalleryStep => float.TryParse(System.Environment.GetEnvironmentVariable("I76_VEG_GALLERY_STEP"), System.Globalization.CultureInfo.InvariantCulture, out float f) && f > 0 ? f : 4.5f;

    /// <summary>The gallery's detail tier: 0 the full meshes, or I76_VEG_GALLERY_TIER (1 the middle, 2 the far ones).</summary>
    private static int GalleryTier => int.TryParse(System.Environment.GetEnvironmentVariable("I76_VEG_GALLERY_TIER"), out int t) ? Math.Clamp(t, 0, 2) : 0;

    /// <summary>A material without the tier cross-fade: the gallery shows a tier at any distance.</summary>
    private static ShaderMaterial GalleryMaterial(ShaderMaterial m)
    {
        var g = (ShaderMaterial)m.Duplicate();
        g.SetShaderParameter("tier_in", Vector2.Zero);
        g.SetShaderParameter("tier_out", Vector2.Zero);
        return g;
    }

    private void AddGallery(float gx, float gz, SpeciesDef[] species)
    {
        float step = GalleryStep;
        var spots = new List<PlantSpot>();
        for (int i = 0; i < species.Length; i++)
        {
            var s = species[i];
            for (int v = 0; v < s.Variants; v++)
            {
                float x = gx + i * step, z = gz + v * step * 1.1f;
                float y = _terrain.Height(x, z) - s.Sink;
                var pm = s.Build(VegHash.Mix(VegHash.Of(s.Id), (ulong)v), GalleryTier);
                var mm = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseCustomData = true, Mesh = ToMesh(pm), InstanceCount = 1 };
                mm.SetInstanceTransform(0, new Transform3D(Basis.Identity, Vector3.Zero));
                mm.SetInstanceCustomData(0, new Color(s.Stiffness, 0.3f * v, pm.Bounds().Max.Y, 0));
                spots.Add(new PlantSpot(new System.Numerics.Vector3(x, y, z), MathF.Max(pm.GroundRadius, 0.2f), pm.Bounds().Max.Y, s.Stiffness, s.Layer == VegLayer.Ground));
                AddChild(new MultiMeshInstance3D { Multimesh = mm, MaterialOverride = GalleryMaterial(s.Layer == VegLayer.Shrub ? _material : _groundMaterial), Position = Coords.ToGodot(x, y, z), Layers = TerrainNode.TerrainLayer, Name = $"Gallery_{s.Id}_{v}" });
                if (s.Layer != VegLayer.Shrub && pm.GroundRadius < GroundShadeMin) continue;
                var shade = new float[16];
                ShadeInstance(shade, 0, x, z, 0, 0, 1, pm.GroundShade, pm.GroundRadius);
                var sm = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseCustomData = true, Mesh = ShadeQuad, InstanceCount = 1, Buffer = shade };
                AddChild(new MultiMeshInstance3D { Multimesh = sm, MaterialOverride = _shadeMaterial, Layers = TerrainNode.TerrainLayer, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, Name = $"GalleryShade_{s.Id}_{v}" });
            }
        }
        _gallery = new PlantIndex(gx - 4, gz - 4, MathF.Max(species.Length * step, 8 * step) + 8, spots);
    }

    private PlantIndex? _gallery;

    // ---- meshes ----------------------------------------------------------------------------------------------

    /// <summary>A plant's mesh into Godot (the placed trees' too).</summary>
    public static ArrayMesh ToMesh(PlantMesh p)
    {
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = p.Positions.Select(v => new Vector3(v.X, v.Y, v.Z)).ToArray();
        arrays[(int)Mesh.ArrayType.Normal] = p.Normals.Select(v => new Vector3(v.X, v.Y, v.Z)).ToArray();
        arrays[(int)Mesh.ArrayType.Color] = p.Colors.Select(c => new Color(c.X, c.Y, c.Z, c.W)).ToArray();
        arrays[(int)Mesh.ArrayType.TexUV] = p.Uvs.Select(v => new Vector2(v.X, v.Y)).ToArray();
        arrays[(int)Mesh.ArrayType.TexUV2] = p.Extra.Select(v => new Vector2(v.X, v.Y)).ToArray();
        arrays[(int)Mesh.ArrayType.Index] = p.Indices.ToArray();
        var flags = (Mesh.ArrayFormat)0;
        if (I76.Game.Boot.RenderBackend.Compatibility)
        {
            arrays[(int)Mesh.ArrayType.Custom1] = p.Colors.SelectMany(c => new[] { c.X, c.Y, c.Z, c.W }).ToArray();
            flags |= (Mesh.ArrayFormat)((long)Mesh.ArrayCustomFormat.RgbaFloat << (int)Mesh.ArrayFormat.FormatCustom1Shift);
        }
        if (p.HasSide)
        {
            // the blades' sides (CUSTOM0, PlantMesh.Side): the shader keeps them a pixel wide far off
            arrays[(int)Mesh.ArrayType.Custom0] = p.Side.SelectMany(v => new[] { v.X, v.Y, v.Z, v.W }).ToArray();
            flags |= (Mesh.ArrayFormat)((long)Mesh.ArrayCustomFormat.RgbaFloat << (int)Mesh.ArrayFormat.FormatCustom0Shift);
        }
        var m = new ArrayMesh();
        if (p.VertexCount > 0) m.AddSurfaceArrays(Mesh.PrimitiveType.Triangles, arrays, flags: flags);
        return m;
    }

    /// <summary>The shade's quad: two triangles across (-1..1) on X and Z, UV 0..1.</summary>
    private static ArrayMesh MakeShadeQuad()
    {
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = new[] { new Vector3(-1, 0, -1), new Vector3(1, 0, -1), new Vector3(1, 0, 1), new Vector3(-1, 0, 1) };
        arrays[(int)Mesh.ArrayType.Normal] = new[] { Vector3.Up, Vector3.Up, Vector3.Up, Vector3.Up };
        arrays[(int)Mesh.ArrayType.TexUV] = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) };
        arrays[(int)Mesh.ArrayType.Index] = new[] { 0, 1, 2, 0, 2, 3 };
        var m = new ArrayMesh();
        m.AddSurfaceArrays(Mesh.PrimitiveType.Triangles, arrays);
        return m;
    }

    /// <summary>
    /// A shrub's shade quad into <paramref name="buf"/> at instance <paramref name="i"/>: centred on the plant at game
    /// (x, z) on the drawn ground, laid on its slope there, out to 1.3 times the plant's reach; relative to the
    /// chunk's corner (ox, oz).
    /// </summary>
    private void ShadeInstance(float[] buf, int i, float x, float z, float ox, float oz, float scale, float shade, float radius)
    {
        float r = MathF.Max(0.15f, radius * scale * 1.3f);
        float h = _terrain.Height(x, z);
        // the ground's slope across the quad (game frame), from its heights either side
        float gx = (_terrain.Height(x + r, z) - _terrain.Height(x - r, z)) / (2 * r);
        float gz = (_terrain.Height(x, z + r) - _terrain.Height(x, z - r)) / (2 * r);
        int k = i * 16;
        // Godot's Z is the game's mirrored: the quad's X axis is (r, gx r, 0), its Z axis (0, -gz r, r)
        buf[k] = r; buf[k + 1] = 0; buf[k + 2] = 0; buf[k + 3] = x - ox;
        buf[k + 4] = gx * r; buf[k + 5] = 1; buf[k + 6] = -gz * r; buf[k + 7] = h + 0.03f;
        buf[k + 8] = 0; buf[k + 9] = 0; buf[k + 10] = r; buf[k + 11] = -(z - oz);
        buf[k + 12] = shade; buf[k + 13] = 0; buf[k + 14] = 0; buf[k + 15] = 0;
    }

    // ---- chunks ----------------------------------------------------------------------------------------------

    private float Reach(VegLayer layer) => layer == VegLayer.Shrub ? _level.ShrubEnd : _level.GroundEnd;
    private bool GrassReach(Chunk c, float d) => c.Layer == VegLayer.Ground && d <= _level.GrassEnd;

    /// <summary>The camera in the game frame (this node sits in the world root, which is the game frame mirrored).</summary>
    private System.Numerics.Vector3? CameraGame()
    {
        if (GetViewport()?.GetCamera3D() is not { } cam || !IsInsideTree()) return null;
        var l = ToLocal(cam.GlobalPosition);
        return new System.Numerics.Vector3(l.X, l.Y, -l.Z);
    }

    /// <summary>
    /// The plants drawn now whose crown reaches within <paramref name="radius"/> of a point (global, Godot's frame), with
    /// their roots in Godot's frame too; at most <paramref name="max"/>. For the brush impacts (main thread).
    /// </summary>
    public void PlantsNear(Vector3 global, float radius, List<PlantSpot> into, int max = 64)
    {
        into.Clear();
        if (!IsInsideTree()) return;
        var l = ToLocal(global);
        float x = l.X, z = -l.Z;
        foreach (var layer in new[] { VegLayer.Shrub, VegLayer.Ground })
        {
            float cs = ChunkSize(layer), m = radius + 3f;
            for (int cz = (int)MathF.Floor((z - m) / cs); cz <= (int)MathF.Floor((z + m) / cs); cz++)
                for (int cx = (int)MathF.Floor((x - m) / cs); cx <= (int)MathF.Floor((x + m) / cs); cx++)
                    if (_chunks.TryGetValue((cx, cz, layer), out var c) && c.Attached && c.Index is { } ix && into.Count < max)
                        ix.Near(x, z, radius, into, max);
        }
        _gallery?.Near(x, z, radius, into, max);
        for (int i = 0; i < into.Count; i++)
        {
            var s = into[i];
            var g = ToGlobal(new Vector3(s.Root.X, s.Root.Y, -s.Root.Z));
            into[i] = s with { Root = new System.Numerics.Vector3(g.X, g.Y, g.Z) };
        }
    }

    /// <summary>The drawn ground's height under a point (global, Godot's frame), or null off the map.</summary>
    public float? GroundY(Vector3 global)
    {
        if (_terrain is null || !IsInsideTree()) return null;
        var l = ToLocal(global);
        if (!_terrain.Has(l.X, -l.Z)) return null;
        return ToGlobal(new Vector3(l.X, _terrain.Height(l.X, -l.Z), l.Z)).Y;
    }

    /// <summary>Every chunk within reach of the camera placed and attached now (behind the loading card), on every core.</summary>
    public void Prewarm()
    {
        foreach (var step in PrewarmSteps(sliced: false)) if (step is Task t) t.Wait();
    }

    /// <summary>
    /// <see cref="Prewarm"/> as steps (a mission loading under its briefing): the placing off the main thread (a Task to wait for),
    /// then the chunks attached a few at a time, a null between.
    /// </summary>
    public IEnumerable<object?> PrewarmSteps(bool sliced = true)
    {
        if (!Visible || _placer is null || CameraGame() is not { } eye) yield break;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        // the sward is usually made by now; a slow machine's comes in a moment later, eased in
        if (sliced && _swardJob is { } sj) yield return Task.WhenAny(sj, Task.Delay(1500));
        else _swardJob?.Wait(1500);
        UpdateSward(0, now: true);
        yield return null;
        var want = Wanted(eye, 0f).Where(k => !_chunks.ContainsKey(k)).ToList();
        var made = new Chunk[want.Count];
        void PlaceAll() => Parallel.For(0, want.Count, i =>
        {
            var (cx, cz, layer) = want[i];
            var c = new Chunk { Cx = cx, Cz = cz, Layer = layer };
            c.Job = Task.FromResult(Place(c));
            made[i] = c;
        });
        if (sliced) yield return Task.Run(PlaceAll); else PlaceAll();
        var slice = System.Diagnostics.Stopwatch.StartNew();
        foreach (var c in made)
        {
            _chunks[(c.Cx, c.Cz, c.Layer)] = c; Attach(c, eye);
            if (sliced && slice.Elapsed.TotalMilliseconds > 15) { yield return null; slice.Restart(); }
        }
        yield return null;
        Update(eye, attachAll: true);
        GD.Print($"vegetation: {made.Length} chunks placed and attached in {sw.ElapsedMilliseconds} ms, {Instances} plants");
        if (System.Environment.GetEnvironmentVariable("I76_VEG_STATS") == "1")
            GD.Print($"vegetation cells: {string.Join(", ", new[] { "tried", "no terrain", "steep", "cover", "masked", "placed" }.Zip(VegetationPlacer.Counts, (n, c) => $"{n} {c}"))}");
    }

    private IEnumerable<(int, int, VegLayer)> Wanted(System.Numerics.Vector3 eye, float extra)
    {
        foreach (var layer in new[] { VegLayer.Shrub, VegLayer.Ground })
        {
            float r = Reach(layer) + extra;
            float cs = ChunkSize(layer);
            int x0 = (int)MathF.Floor((eye.X - r) / cs), x1 = (int)MathF.Floor((eye.X + r) / cs);
            int z0 = (int)MathF.Floor((eye.Z - r) / cs), z1 = (int)MathF.Floor((eye.Z + r) / cs);
            for (int z = z0; z <= z1; z++)
                for (int x = x0; x <= x1; x++)
                    if (ChunkDistance(x, z, layer, eye) <= r) yield return (x, z, layer);
        }
    }

    private static float ChunkDistance(int cx, int cz, VegLayer layer, System.Numerics.Vector3 eye)
    {
        float cs = ChunkSize(layer);
        float dx = MathF.Max(0, MathF.Max(cx * cs - eye.X, eye.X - (cx + 1) * cs));
        float dz = MathF.Max(0, MathF.Max(cz * cs - eye.Z, eye.Z - (cz + 1) * cs));
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    /// <summary>A chunk's plants, grouped by species variant into MultiMesh buffers (a worker thread's share).</summary>
    private List<Group> Place(Chunk c)
    {
        float side = ChunkSize(c.Layer);
        var placed = _placer.Place(c.Layer, c.Cx * side, c.Cz * side, side);
        var groups = new Dictionary<(int, int), List<PlantInstance>>();
        foreach (var p in placed)
        {
            if (!groups.TryGetValue((p.Species, p.Variant), out var l)) groups[(p.Species, p.Variant)] = l = [];
            l.Add(p);
        }
        float ox = c.Cx * side, oz = c.Cz * side;
        // the shade under every shrub, and under the larger plants of the ground layer (not every grass tuft)
        var shade = new float[placed.Count * 16];
        int shaded = 0;
        foreach (var p in placed)
        {
            var mesh = _meshes[p.Species][p.Variant];
            if (c.Layer == VegLayer.Ground && mesh.ShadeRadius * p.Scale < GroundShadeMin) continue;
            ShadeInstance(shade, shaded++, p.Position.X, p.Position.Z, ox, oz, p.Scale, mesh.Shade, mesh.ShadeRadius);
        }
        c.Shade = shaded * 16 == shade.Length ? shade : shade[..(shaded * 16)];
        var spots = new List<PlantSpot>(placed.Count);
        foreach (var p in placed)
        {
            var mesh = _meshes[p.Species][p.Variant];
            spots.Add(new PlantSpot(p.Position, MathF.Max(mesh.ShadeRadius * p.Scale, 0.2f), mesh.Height * p.Scale, _stiffness[p.Species], c.Layer == VegLayer.Ground));
        }
        c.Index = new PlantIndex(ox, oz, side, spots);
        var o = new List<Group>();
        foreach (var ((sp, v), list) in groups)
        {
            var buf = new float[list.Count * 16];
            float h = _meshes[sp][v].Height;
            for (int i = 0; i < list.Count; i++)
            {
                var p = list[i];
                float cs = MathF.Cos(p.Yaw) * p.Scale, sn = MathF.Sin(p.Yaw) * p.Scale;
                int k = i * 16;
                // the transform's rows (a yaw and a scale), at the plant's place from the chunk's corner (Godot's Z mirrored)
                buf[k] = cs; buf[k + 1] = 0; buf[k + 2] = sn; buf[k + 3] = p.Position.X - ox;
                buf[k + 4] = 0; buf[k + 5] = p.Scale; buf[k + 6] = 0; buf[k + 7] = p.Position.Y;
                buf[k + 8] = -sn; buf[k + 9] = 0; buf[k + 10] = cs; buf[k + 11] = -(p.Position.Z - oz);
                buf[k + 12] = _stiffness[sp]; buf[k + 13] = p.Phase; buf[k + 14] = h * p.Scale; buf[k + 15] = (p.Phase * 7.31f % 1f) * 2 - 1;
            }
            o.Add(new Group { Species = sp, Variant = v, Count = list.Count, Buffer = buf });
        }
        // the far tier's groups: a species' variants in one buffer under its first variant's far mesh (a far crown's variants
        // differ little at that size, and a chunk wholly in the far tier then draws one node a species, not one a variant)
        c.FarGroups = [.. o.GroupBy(g => g.Species).Select(sg =>
        {
            var list = sg.ToList();
            if (list.Count == 1) return new Group { Species = list[0].Species, Variant = list[0].Variant, Count = list[0].Count, Buffer = list[0].Buffer };
            var buf = new float[list.Sum(g => g.Buffer.Length)];
            int at = 0;
            foreach (var g in list) { Array.Copy(g.Buffer, 0, buf, at, g.Buffer.Length); at += g.Buffer.Length; }
            return new Group { Species = sg.Key, Variant = 0, Count = list.Sum(g => g.Count), Buffer = buf };
        })];
        return o;
    }

    private void Attach(Chunk c, System.Numerics.Vector3 eye)
    {
        if (c.Job is not { IsCompletedSuccessfully: true } job) return;
        c.Groups = job.Result;
        c.Attached = true;
        c.Tier = -1;
        c.Casting = false;
        Instances += c.Groups.Sum(g => g.Count);
        if (c.Shade.Length > 0)
        {
            int n = c.Shade.Length / 16;
            float side = ChunkSize(c.Layer);
            var mm = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseCustomData = true, Mesh = ShadeQuad, InstanceCount = n, Buffer = c.Shade };
            float y0 = float.MaxValue, y1 = float.MinValue;
            for (int i = 0; i < n; i++) { y0 = MathF.Min(y0, c.Shade[i * 16 + 7]); y1 = MathF.Max(y1, c.Shade[i * 16 + 7]); }
            mm.CustomAabb = new Aabb(new Vector3(-4, y0 - 2, -side - 4), new Vector3(side + 8, y1 - y0 + 4, side + 8));
            c.ShadeNode = new MultiMeshInstance3D
            {
                Multimesh = mm, MaterialOverride = _shadeMaterial, Position = Coords.ToGodot(c.Cx * side, 0, c.Cz * side), Layers = TerrainNode.TerrainLayer,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, GIMode = GeometryInstance3D.GIModeEnum.Disabled,
            };
            AddChild(c.ShadeNode);
        }
        Retier(c, eye);
    }

    /// <summary>The tier each of a chunk's plants is drawn at: all one when the chunk lies within one tier's band, else split plant by plant.</summary>
    private void Retier(Chunk c, System.Numerics.Vector3 eye)
    {
        float mid = c.Layer == VegLayer.Shrub ? _level.ShrubMid : _level.GroundMid, far = c.Layer == VegLayer.Shrub ? _level.ShrubFar : _level.GroundFar;
        c.Distance = ChunkDistance(c.Cx, c.Cz, c.Layer, eye);
        c.Far = ChunkFarthest(c.Cx, c.Cz, c.Layer, eye);
        // the tiers any of its plants need (TierBands): one alone, the whole chunk in it; else split per plant, each in the tiers its band says
        int need = TierBands.TiersBetween(c.Distance, c.Far, mid, far);
        int tier = need switch { TierBands.Full => 0, TierBands.Middle => 1, TierBands.Far => 2, _ => 3 };
        var e2 = new System.Numerics.Vector2(eye.X, eye.Z);
        // wholly in the far tier, or in the middle one and far off (by night the shrubs' middle mesh reaches to their end): the
        // merged groups, one node a species
        bool merged = (tier == 2 || (tier == 1 && c.Distance >= MergeMiddleFrom));
        if (tier == c.Tier && merged == c.Merged && (tier != 3 || System.Numerics.Vector2.Distance(e2, c.SplitAt) < Resplit)) return;
        c.Tier = tier;
        c.Merged = merged;
        c.SplitAt = e2;
        c.GrassOn = GrassReach(c, c.Distance);
        float side = ChunkSize(c.Layer), ox = c.Cx * side, oz = c.Cz * side;
        foreach (var g in c.FarGroups)
        {
            bool on = merged && !(c.Layer == VegLayer.Ground && _grass[g.Species] && !c.GrassOn);
            for (int t = 0; t < 3; t++) Show(c, g, t, on && t == tier ? g.Buffer : null, g.Count);
        }
        foreach (var g in c.Groups)
        {
            // a chunk beyond the grass tufts' reach draws only its larger plants (no tuft's buffer is sent)
            if (merged || (c.Layer == VegLayer.Ground && _grass[g.Species] && !c.GrassOn))
            {
                for (int t = 0; t < 3; t++) Show(c, g, t, null, 0);
                continue;
            }
            if (tier < 3)
            {
                for (int t = 0; t < 3; t++) Show(c, g, t, t == tier ? g.Buffer : null, g.Count);
                continue;
            }
            // per plant: copy each instance's 16 floats into its tier's buffer
            var bufs = new float[3][];
            var n = new int[3];
            for (int t = 0; t < 3; t++) bufs[t] = new float[g.Buffer.Length];
            for (int i = 0; i < g.Count; i++)
            {
                float dx = g.Buffer[i * 16 + 3] + ox - eye.X, dz = -g.Buffer[i * 16 + 11] + oz - eye.Z;
                float d = MathF.Sqrt(dx * dx + dz * dz);
                // in every tier its band reaches, with the distance the eye may move before the next split
                int mask = TierBands.Tiers(d, mid, far, Resplit);
                for (int t = 0; t < 3; t++)
                {
                    if ((mask & (1 << t)) == 0) continue;
                    Array.Copy(g.Buffer, i * 16, bufs[t], n[t] * 16, 16);
                    n[t]++;
                }
            }
            for (int t = 0; t < 3; t++) Show(c, g, t, n[t] > 0 ? bufs[t] : null, n[t]);
        }
    }

    /// <summary>A group's node for a tier with these instances (the first <paramref name="count"/> of the buffer), made when first needed; none hides it.</summary>
    private void Show(Chunk c, Group g, int tier, float[]? buffer, int count)
    {
        var node = g.Nodes[tier];
        if (buffer is null || count == 0)
        {
            if (node is not null) node.Visible = false;
            return;
        }
        float side = ChunkSize(c.Layer);
        if (node is null)
        {
            var mm = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseCustomData = true, Mesh = _meshes[g.Species][g.Variant].Tiers[tier] };
            node = g.Nodes[tier] = new MultiMeshInstance3D
            {
                Multimesh = mm, MaterialOverride = _mats[Kind(c.Layer, g.Species)][tier], Position = Coords.ToGodot(c.Cx * side, 0, c.Cz * side),
                Layers = TerrainNode.TerrainLayer, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, GIMode = GeometryInstance3D.GIModeEnum.Disabled,
            };
            AddChild(node);
        }
        var m = node.Multimesh;
        if (m.InstanceCount != count) m.InstanceCount = count;
        m.Buffer = count * 16 == buffer.Length ? buffer : buffer[..(count * 16)];
        // the bounds set outright: the chunk's square, from its lowest plant's foot to its highest plus a tall plant
        float y0 = float.MaxValue, y1 = float.MinValue;
        for (int i = 0; i < count; i++) { y0 = MathF.Min(y0, buffer[i * 16 + 7]); y1 = MathF.Max(y1, buffer[i * 16 + 7] + buffer[i * 16 + 14]); }
        m.CustomAabb = new Aabb(new Vector3(-4, y0 - 1, -side - 4), new Vector3(side + 8, y1 - y0 + 3, side + 8));
        node.Visible = PlantsOn;
        node.CastShadow = c.Casting && tier < 2 ? GeometryInstance3D.ShadowCastingSetting.On : GeometryInstance3D.ShadowCastingSetting.Off;
    }

    private static float ChunkFarthest(int cx, int cz, VegLayer layer, System.Numerics.Vector3 eye)
    {
        float cs = ChunkSize(layer);
        float dx = MathF.Max(MathF.Abs(cx * cs - eye.X), MathF.Abs((cx + 1) * cs - eye.X));
        float dz = MathF.Max(MathF.Abs(cz * cs - eye.Z), MathF.Abs((cz + 1) * cs - eye.Z));
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    public override void _Process(double delta)
    {
        UpdateSward(delta);
        // night or day (the mission's light, set after the plants are made): by night the shrubs' far mesh stays out of the beam
        if (_levelIndex > 0 && (I76.Game.Sim.Daylight.Current <= 0) != _night) ApplyReach();
        if (!Visible || _placer is null || CameraGame() is not { } eye) return;
        Update(eye, attachAll: false);
    }

    private void Update(System.Numerics.Vector3 eye, bool attachAll)
    {
        // new chunks a little beyond the reach, so they are placed before they fade in; the nearest first (a camera cut leaves many to do).
        // Looked for when the eye has moved a few metres or the last look left some for want of a worker (40 m of slack covers it)
        var e2 = new System.Numerics.Vector2(eye.X, eye.Z);
        bool look = _lookPending || attachAll || System.Numerics.Vector2.DistanceSquared(e2, _lookedAt) > LookEvery * LookEvery;
        if (look)
        {
            _lookedAt = e2;
            _lookPending = false;
        }
        foreach (var key in look ? Wanted(eye, 40f).Where(k => !_chunks.ContainsKey(k)).OrderBy(k => ChunkDistance(k.Item1, k.Item2, k.Item3, eye)) : Enumerable.Empty<(int, int, VegLayer)>())
        {
            if (_jobs >= MaxJobs) { _lookPending = true; break; }
            var c = new Chunk { Cx = key.Item1, Cz = key.Item2, Layer = key.Item3 };
            _chunks[key] = c;
            System.Threading.Interlocked.Increment(ref _jobs);
            c.Job = Task.Run(() =>
            {
                try { return Place(c); }
                finally { System.Threading.Interlocked.Decrement(ref _jobs); _done.Enqueue(c); }
            });
        }
        int attached = 0;
        while ((attachAll || attached < MaxAttachPerFrame) && _done.TryDequeue(out var d))
        {
            if (!_chunks.TryGetValue((d.Cx, d.Cz, d.Layer), out var cur) || cur != d || d.Attached) continue;
            if (d.Job is { IsFaulted: true }) { GD.PrintErr($"vegetation chunk {d.Cx},{d.Cz}: {d.Job.Exception?.GetBaseException().Message}"); continue; }
            Attach(d, eye);
            attached++;
        }
        // drop the far ones; the near shrubs cast shadows; each plant's tier
        List<(int, int, VegLayer)>? drop = null;
        int nodes = 0;
        // the nodes drawn (a figure for the stats and the profiles) counted twice a second, not every frame
        bool count = ++_statFrame % 30 == 0;
        foreach (var (key, c) in _chunks)
        {
            float d = ChunkDistance(c.Cx, c.Cz, c.Layer, eye);
            if (d > Reach(c.Layer) + 80f && c.Job is { IsCompleted: true }) { (drop ??= []).Add(key); continue; }
            if (!c.Attached) continue;
            if (c.ShadeNode is { } sn && sn.Visible != (PlantsOn && d < _level.ShadeEnd)) sn.Visible = !sn.Visible;
            bool cast = c.Layer == VegLayer.Shrub && d < _level.ShadowRange;
            if (cast != c.Casting) { c.Casting = cast; c.Tier = -1; }
            if (c.Layer == VegLayer.Ground && GrassReach(c, d) != c.GrassOn) c.Tier = -1;
            Retier(c, eye);
            if (count)
            {
                foreach (var g in c.Groups) foreach (var n in g.Nodes) if (n is { Visible: true }) nodes++;
                foreach (var g in c.FarGroups) foreach (var n in g.Nodes) if (n is { Visible: true }) nodes++;
            }
        }
        if (drop is not null) foreach (var k in drop) { Detach(_chunks[k]); _chunks.Remove(k); }
        if (count) DrawnNodes = nodes;
        if (StatsOn && _statFrame % 60 == 0)
        {
            var tiers = new int[3];
            foreach (var c in _chunks.Values)
                foreach (var g in c.Groups.Concat(c.FarGroups))
                    for (int t = 0; t < 3; t++) if (g.Nodes[t] is { Visible: true } n) tiers[t] += n.Multimesh.InstanceCount;
            if (System.Environment.GetEnvironmentVariable("I76_VEG_PROBE")?.Split(',').Select(float.Parse).ToArray() is { Length: 4 } r)
                foreach (var layer in new[] { VegLayer.Shrub, VegLayer.Ground })
                {
                    int inRect = 0;
                    foreach (var c in _chunks.Values)
                        if (c.Layer == layer)
                            foreach (var g in c.Groups)
                                for (int i = 0; i < g.Count; i++)
                                {
                                    float x = g.Buffer[i * 16 + 3] + c.Cx * ChunkSize(layer), z = -g.Buffer[i * 16 + 11] + c.Cz * ChunkSize(layer);
                                    if (x >= r[0] && x < r[2] && z >= r[1] && z < r[3]) inRect++;
                                }
                    GD.Print($"vegetation probe: {layer} {inRect}");
                }
            GD.Print($"vegetation stats: eye {eye.X:0} {eye.Y:0} {eye.Z:0}: {Instances} plants attached in {_chunks.Count} chunks, {nodes} nodes; by tier {tiers[0]} / {tiers[1]} / {tiers[2]}" + (_tumble is { } tw ? $"; {tw.Stats}" : ""));
        }
    }
}
