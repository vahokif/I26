using Godot;
using I76.Formats.Assembly;
using I76.Formats.Imaging;
using I76.Formats.Vfs;
using I76.Game.Assets;

namespace I76.Game.World;

/// <summary>Builds a mission's world from the install: terrain, ground material, placements.</summary>
public sealed class MissionBuilder(AssetVfs vfs)
{
    public AssetVfs Vfs => vfs;
    public MissionWorld? World { get; private set; }
    public TerrainNode? Terrain { get; private set; }
    /// <summary>The ground past the map's tiles near the camera (null for a map too big for it).</summary>
    public OutsideTerrain? OutsideNode { get; private set; }
    public ScenerySpawner? Scenery { get; private set; }
    public MeshBuilder? Meshes { get; private set; }
    private I76.Game.Sim.CarPartsCache? _carParts;
    /// <summary>The cars' meshes kept for the next car of the same kind (a respawn, a wave); one set a mission.</summary>
    public I76.Game.Sim.CarPartsCache CarParts => _carParts ??= new(this);
    /// <summary>Every object material, to switch between the modern and 1997 shader variants.</summary>
    public MaterialLibrary? Materials { get; private set; }
    public TextureFactory? Textures { get; private set; }
    public I76.Formats.Assembly.TextureResolver? Resolver { get; private set; }
    public ModelAssembler? Assembler { get; private set; }
    public ShaderMaterial? TerrainMaterial { get; private set; }
    public RoadNode? Roads { get; private set; }
    /// <summary>The road pieces RoadNode draws in the 1997 look (game space).</summary>
    public List<I76.Formats.Assembly.RoadPiece>? RoadPieces { get; private set; }
    /// <summary>Road layer texels per chunk side: 1024 over 160 units is the road textures' own 6.4 per unit.</summary>
    public const int RoadLayerResolution = 1024;
    public static bool CompressRoadLayers { get; set; } = OS.HasFeature("editor") && System.Environment.GetEnvironmentVariable("I76_SOFT_COMPRESS") is not ("0" or "1");   // I76_SOFT_COMPRESS: 1 our encoder, 0 none (RGBA8, an export without it)   // editor builds only have Godot's compressor (TerrainMaterials.Compress)
    /// <summary>
    /// Where Godot's compressor is not there (an export; I76_SOFT_COMPRESS=1 pretends so in the editor) the layers are BC3 by our own
    /// encoder (<see cref="I76.Formats.Imaging.Dxt5Encoder"/>) if the GPU takes S3TC: a quarter of the video memory, which the
    /// RGBA8 layers of a release build were not (a big map has dozens). Else they stay RGBA8.
    /// </summary>
    public static bool SoftCompressRoadLayers => !CompressRoadLayers && RenderingServer.HasOsFeature("s3tc") && System.Environment.GetEnvironmentVariable("I76_SOFT_COMPRESS") != "0";

    /// <summary><paramref name="img"/> (RGBA8 with mipmaps) as BC3 by <see cref="I76.Formats.Imaging.Dxt5Encoder"/>; safe on any thread.</summary>
    public static Image SoftDxt5(Image img)
    {
        int w = img.GetWidth(), h = img.GetHeight();
        var data = img.GetData();
        var enc = I76.Formats.Imaging.Dxt5Encoder.EncodeChain(data, w, h, l => (int)img.GetMipmapOffset(l), img.GetMipmapCount() + 1);
        return Image.CreateFromData(w, h, true, Image.Format.Dxt5, enc);
    }
    public int RoadChunks { get; private set; }
    /// <summary>What the map editor needs to paint a chunk's road layer anew: the road texture names, their pixels (the piece tops' after them) and the piece tops.</summary>
    public string[]? RoadNames { get; private set; }
    public Dictionary<int, RoadRaster.Tex?>? RoadTextures { get; private set; }
    public List<RoadRaster.FlatTri>? RoadPieceTris { get; private set; }
    /// <summary>The quads closing short gaps in the road data (RoadGaps), modern look only.</summary>
    public int RoadFills { get; private set; }
    public long RoadBakeMs { get; private set; }
    public string? GroundTexture { get; private set; }
    public Texture2D? SkyTexture { get; private set; }
    public string? SkyName { get; private set; }
    public Color SkyAverage { get; private set; } = new(0.7f, 0.7f, 0.75f);
    /// <summary>The sky texture's colours for the modern sky: the open sky, the clouds, their shade, and the luminances between.</summary>
    public SkyPalette? Palette { get; private set; }
    /// <summary>The mission's .act.</summary>
    public Palette? ActPalette { get; private set; }
    public sealed record SkyPalette(Color Sky, Color Cloud, Color Shade, float SkyLum, float CloudLum);

    /// <summary>
    /// The upward faces of every painted road-piece placement (types 0x50–0x5f, flat models: RoadPieceObjects), and
    /// the ground-level faces of grounded scenery (GroundDecals), in the game
    /// frame's XZ, with their UVs and textures (a flat colour as a 1×1
    /// texture), for RoadRaster to paint into the terrain layer. The original
    /// drew these over the terrain; painted in, nothing has to win a depth
    /// fight.
    /// </summary>
    private static (List<RoadRaster.FlatTri>, List<RoadRaster.Tex?>) RoadPieceTops(I76.Formats.Assembly.MissionWorld world,
        I76.Formats.Assembly.ModelAssembler asm, I76.Formats.Assembly.TextureResolver resolver, Assets.TextureFactory textures)
    {
        var tris = new List<RoadRaster.FlatTri>();
        var texList = new List<RoadRaster.Tex?>();
        var texIndex = new Dictionary<string, int>();
        int Tex(string key, Func<RoadRaster.Tex?> make)
        {
            if (!texIndex.TryGetValue(key, out int i)) { texIndex[key] = i = texList.Count; texList.Add(make()); }
            return i;
        }
        var lowestOf = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in world.Placements.Concat(world.LinePlacements))
        {
            bool decals = GroundDecals.Applies(p);
            if (!decals && !ScenerySpawner.IsRoadPiece(p.Object.ObjectType) || p.Definition is null || asm.Assemble(p.Definition) is not { } model) continue;
            // a road piece is painted only where the paint is its whole look (RoadPieceObjects): a ramp given the type is drawn
            if (!decals && !RoadPieceObjects.IsFlat(model = RoadPieceObjects.Drawn(model))) continue;
            float lowest = 0;
            if (decals && !lowestOf.TryGetValue(p.Definition, out lowest)) lowestOf[p.Definition] = lowest = GroundDecals.LowestY(model);
            var place = I76.Formats.Assembly.Affine.FromEngine(p.Object.Rotation, world.GroundedPosition(p));
            foreach (var part in model.Parts)
                foreach (var f in part.Mesh.Faces)
                {
                    if (f.Vertices.Length < 3 || f.Vertices.Any(v => (uint)v.Index >= part.Mesh.Vertices.Length)) continue;
                    if (decals && !GroundDecals.IsGroundFace(part, f, lowest)) continue;
                    var n = place.Rotate(part.ToModel.Rotate(new System.Numerics.Vector3(f.Plane.X, f.Plane.Y, f.Plane.Z)));
                    if (n.LengthSquared() < 0.5f || -n.Y / n.Length() < 0.6f) continue;   // outward is -plane: only tops
                    int ti;
                    if (f.Texture.Length > 0 && f.Unk_0x20 != 0 && resolver.ResolveFace(f.Texture, model.ObjectCode, model.TmtNames, vtfMaps: model.VtfMaps) is { } member)
                        ti = Tex(member, () => textures.GetRgba(member));
                    else
                        ti = Tex($"#{f.R:x2}{f.G:x2}{f.B:x2}", () => new RoadRaster.Tex(1, 1, [f.R, f.G, f.B, 255]));
                    var w = f.Vertices.Select(v => place.Apply(part.ToModel.Apply(part.Mesh.Vertices[v.Index]))).Select(v => new System.Numerics.Vector2(v.X, v.Z)).ToArray();
                    var uv = f.Vertices.Select(v => new System.Numerics.Vector2(v.U, v.V)).ToArray();
                    for (int k = 1; k + 1 < w.Length; k++)
                    {
                        var min = System.Numerics.Vector2.Min(System.Numerics.Vector2.Min(w[0], w[k]), w[k + 1]);
                        var max = System.Numerics.Vector2.Max(System.Numerics.Vector2.Max(w[0], w[k]), w[k + 1]);
                        tris.Add(new RoadRaster.FlatTri(w[0], w[k], w[k + 1], uv[0], uv[k], uv[k + 1], ti, min, max));
                    }
                }
        }
        return (tris, texList);
    }

    /// <summary>
    /// The map editor placed or took away road pieces: their tops again from the world's placements (numbered after the road set's textures,
    /// as at load). The textures go in a new dictionary: a repaint that is running keeps the one it started with.
    /// </summary>
    public void RefreshPieceTops()
    {
        if (World is null || Assembler is null || Resolver is null || Textures is null || RoadNames is null || RoadTextures is null) return;
        var (tris, tex) = RoadPieceTops(World, Assembler, Resolver, Textures);
        int n = RoadNames.Length;
        var textures = RoadTextures.Where(kv => kv.Key < n).ToDictionary(kv => kv.Key, kv => kv.Value);
        for (int i = 0; i < tex.Count; i++) textures[n + i] = tex[i];
        for (int i = 0; i < tris.Count; i++) tris[i] = tris[i] with { Texture = tris[i].Texture + n };
        RoadTextures = textures; RoadPieceTris = tris;
    }

    /// <summary>
    /// One chunk's road layer, rasterised, mipmapped and compressed as <see cref="BakeRoadLayers"/> does it, or null when no road
    /// crosses it (any thread). A map that fills the grid bakes its chunks one by one as they stream in, not all 102 400 up front.
    /// </summary>
    private static Image? BakeChunkLayer(List<RoadRaster.FlatQuad> flat, List<RoadRaster.FlatTri> tris, RoadRaster.Index index,
        Dictionary<int, RoadRaster.Tex?> roadTex, int x0, int z0)
    {
        var layer = RoadRaster.Bake(flat, new System.Numerics.Vector2(x0 * 5f, z0 * 5f), TerrainNode.Chunk * 5f, RoadLayerResolution,
            i => roadTex.GetValueOrDefault(i), tris: tris, index: index);
        if (layer is null) return null;
        var img = Image.CreateFromData(RoadLayerResolution, RoadLayerResolution, false, Image.Format.Rgba8, layer);
        img.GenerateMipmaps();
        if (CompressRoadLayers) img.Compress(Image.CompressMode.S3Tc, Image.CompressSource.Generic);
        else if (SoftCompressRoadLayers) img = SoftDxt5(img);
        return img;
    }

    /// <summary>The bake cache in user data (user://cache/terrain); null with I76_BAKE_CACHE=0.</summary>
    public static BakeCache? Cache { get; } = System.Environment.GetEnvironmentVariable("I76_BAKE_CACHE") == "0" ? null
        : new BakeCache(ProjectSettings.GlobalizePath("user://cache/terrain"));
    /// <summary>
    /// The cache for what depends on the heights (the modern surface, the road layers): none while the map editor holds edited ground, so
    /// a playtest's every variant does not fill the user's cache folder.
    /// </summary>
    public static BakeCache? HeightCache => I76.Game.Editor.EditorSession.Doc is { Modified: true } ? null : Cache;
    /// <summary>The engine, for cache keys: its mipmaps and compressors made what is stored.</summary>
    public static readonly string EngineVersion = (string)Engine.GetVersionInfo()["string"];

    /// <summary>
    /// Every chunk's road layer (<see cref="RoadRaster"/>, then mipmaps and, in an editor build, S3TC), on every core;
    /// none for a chunk no road crosses. Compressed layers are kept in the bake cache, keyed by everything that goes
    /// into them (the quads, the piece tops, the textures' pixels, the chunks, the resolution, the code's and
    /// engine's versions): the compression is most of their cost. An export's layers are not kept: rasterising them
    /// again is quicker than reading back uncompressed ones.
    /// </summary>
    private static Dictionary<(int, int), Image> BakeRoadLayers(List<RoadRaster.FlatQuad> flat, List<RoadRaster.FlatTri> tris,
        Dictionary<int, RoadRaster.Tex?> roadTex, List<(int X0, int Z0)> origins)
    {
        var cache = CompressRoadLayers ? HeightCache : null;
        // a map editor session keeps the layers it baked in memory too, for its playtests of ground the disk cache is not used for (edited)
        bool session = CompressRoadLayers && I76.Formats.Vfs.SessionStore.Active;
        string? key = null;
        if (cache is not null || session)
        {
            using var k = new BakeCache.Key("road-layers");
            k.Add(RoadRaster.Version).Add(EngineVersion).Add(RoadLayerResolution).Add(CompressRoadLayers).Add(flat).Add(tris);
            foreach (var (i, t) in roadTex.OrderBy(kv => kv.Key))
            {
                k.Add(i);
                if (t is null) k.Add(-1); else k.Add(t.Width).Add(t.Height).Add<byte>(t.Rgba);
            }
            foreach (var o in origins) k.Add(o.X0).Add(o.Z0);
            key = k.Finish();
            if (session && I76.Formats.Vfs.SessionStore.TryGet<Dictionary<(int, int), Image>>("roads|" + key) is { } kept)
            {
                if (HashOn) TraceLayers("session", origins, kept, null);
                return new Dictionary<(int, int), Image>(kept);
            }
            if (cache?.Read("roads", key) is { } blobs)
            {
                var got = new Image[blobs.Count];
                System.Threading.Tasks.Parallel.For(0, blobs.Count, i =>
                    got[i] = Image.CreateFromData(RoadLayerResolution, RoadLayerResolution, true, (Image.Format)blobs[i].Meta, blobs[i].Data));
                var hit = new Dictionary<(int, int), Image>();
                for (int i = 0; i < blobs.Count; i++) hit[((int)(blobs[i].Tag >> 32), (int)blobs[i].Tag)] = got[i];
                if (HashOn) TraceLayers("cache", origins, hit, null);
                if (session) I76.Formats.Vfs.SessionStore.Put("roads|" + key, new Dictionary<(int, int), Image>(hit), hit.Values.Sum(i => (long)i.GetWidth() * i.GetHeight() * 3 / 2));
                return hit;
            }
        }
        var layers = new System.Collections.Concurrent.ConcurrentDictionary<(int, int), Image>();
        var keep = new System.Collections.Concurrent.ConcurrentDictionary<(int, int), byte[]>();
        var raw = HashOn ? new System.Collections.Concurrent.ConcurrentDictionary<(int, int), ulong>() : null;
        var index = new RoadRaster.Index(flat, tris, TerrainNode.Chunk * 5f);
        bool soft = SoftCompressRoadLayers;
        long tRaster = 0, tMips = 0, tComp = 0;
        System.Threading.Tasks.Parallel.ForEach(origins, o =>
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            var layer = RoadRaster.Bake(flat, new System.Numerics.Vector2(o.X0 * 5f, o.Z0 * 5f), TerrainNode.Chunk * 5f, RoadLayerResolution,
                i => roadTex.GetValueOrDefault(i), tris: tris, index: index);
            long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
            if (layer is null) { System.Threading.Interlocked.Add(ref tRaster, t1 - t0); return; }
            if (raw is not null) raw[o] = H64(layer);
            var img = Image.CreateFromData(RoadLayerResolution, RoadLayerResolution, false, Image.Format.Rgba8, layer);
            img.GenerateMipmaps();
            long t2 = System.Diagnostics.Stopwatch.GetTimestamp();
            if (CompressRoadLayers) img.Compress(Image.CompressMode.S3Tc, Image.CompressSource.Generic);
            else if (soft) img = SoftDxt5(img);
            long t3 = System.Diagnostics.Stopwatch.GetTimestamp();
            if (cache is not null) keep[o] = img.GetData();
            layers[o] = img;
            System.Threading.Interlocked.Add(ref tRaster, t1 - t0);
            System.Threading.Interlocked.Add(ref tMips, t2 - t1);
            System.Threading.Interlocked.Add(ref tComp, t3 - t2);
        });
        var made = new Dictionary<(int, int), Image>(layers);
        if (TraceOn)
        {
            double ms(long t) => t * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            Trace($"road layers baked (cpu-ms: raster {ms(tRaster):F0}, mips {ms(tMips):F0}, compress {ms(tComp):F0}; {origins.Count} chunks)");
            if (HashOn) TraceLayers("baked", origins, made, raw);
        }
        if (cache is not null && key is not null)
        {
            var blobs = origins.Where(keep.ContainsKey).Select(o => new BakeCache.Blob((long)o.X0 << 32 | (uint)o.Z0, (int)made[o].GetFormat(), keep[o])).ToList();
            Background(() => cache.Write("roads", key, blobs));   // off the load's path, at low priority
        }
        if (session && key is not null) I76.Formats.Vfs.SessionStore.Put("roads|" + key, new Dictionary<(int, int), Image>(made), made.Values.Sum(i => (long)i.GetWidth() * i.GetHeight() * 3 / 2));
        return made;
    }

    /// <summary>I76_BUILD_TRACE: the layers' hashes, to compare bakes (the raster's, then the texture data's: mipmaps and compression included).</summary>
    private static void TraceLayers(string how, List<(int X0, int Z0)> origins, Dictionary<(int, int), Image> layers, System.Collections.Concurrent.ConcurrentDictionary<(int, int), ulong>? raw)
    {
        ulong hr = 0, hf = 0;
        foreach (var o in origins)
        {
            hr = hr * 1099511628211UL ^ (raw?.GetValueOrDefault(o) ?? 0);
            hf = hf * 1099511628211UL ^ (layers.TryGetValue(o, out var li) ? H64(li.GetData()) : 0);
        }
        GD.Print($"road layers ({how}): {layers.Count}, raster hash {(raw is null ? "-" : hr.ToString("x16"))}, texture data hash {hf:x16}");
    }

    /// <summary>
    /// Sorts the texture's pixels by brightness: the darkest third is the open
    /// sky, the brightest sixth the clouds, the band between their shade.
    /// Luminances are linear, as the shader samples a source_color texture.
    /// </summary>
    private static SkyPalette MeasureSky(Image img)
    {
        img = (Image)img.Duplicate();
        if (img.IsCompressed()) img.Decompress();
        img.Resize(64, 64, Image.Interpolation.Bilinear);
        var px = new List<(float L, Color C)>();
        for (int y = 0; y < 64; y++)
            for (int x = 0; x < 64; x++)
            {
                var c = img.GetPixel(x, y).SrgbToLinear();
                px.Add((0.299f * c.R + 0.587f * c.G + 0.114f * c.B, c));
            }
        px.Sort((a, b) => a.L.CompareTo(b.L));
        (float L, Color C) Mean(double from, double to)
        {
            int a = (int)(px.Count * from), b = System.Math.Max(a + 1, (int)(px.Count * to));
            float l = 0, r = 0, g = 0, bl = 0;
            for (int i = a; i < b; i++) { l += px[i].L; r += px[i].C.R; g += px[i].C.G; bl += px[i].C.B; }
            int n = b - a;
            return (l / n, new Color(r / n, g / n, bl / n).LinearToSrgb());
        }
        var sky = Mean(0, 0.33); var cloud = Mean(0.84, 1); var shade = Mean(0.5, 0.7);
        return new SkyPalette(sky.C, cloud.C, shade.C, sky.L, MathF.Max(cloud.L, sky.L + 0.05f));
    }
    public I76.Formats.Mission.WorldSettings? Settings { get; private set; }
    public I76.Formats.Mission.LightPreset Light { get; private set; } = I76.Formats.Mission.Lighting.ForHour(12);
    public Texture2DArray? Horizon { get; private set; }
    /// <summary>The horizon panels' images (kept for the modern look's distant terrain, which measures them off the main thread).</summary>
    public Image[]? HorizonImages { get; private set; }
    public Texture2D? Sprite { get; private set; }
    public List<string> Notes { get; } = [];

    /// <summary>
    /// Build the 1997 look only (a mission that starts in it): the modern terrain's materials, ground scans, cavity
    /// textures and level-of-detail meshes are left for <see cref="ModernSteps"/>, which runs them in the background.
    /// </summary>
    public bool DeferModern { get; set; }
    /// <summary>The modern terrain is built and attached (always, unless <see cref="DeferModern"/> and <see cref="ModernSteps"/> has not finished).</summary>
    public bool ModernReady { get; private set; } = true;

    /// <summary>Start the modern look's distant terrain (<see cref="HorizonBuild"/>) during the build (not with <see cref="DeferModern"/>).</summary>
    public bool EarlyHorizon { get; set; }
    /// <summary>The distant terrain computed off the main thread (its meshes still to make: <see cref="HorizonTerrain.Finish"/>).</summary>
    public System.Threading.Tasks.Task<HorizonTerrain>? HorizonBuild { get; private set; }

    /// <summary>
    /// <see cref="Build"/> runs on a thread of its own (Main.BuildUnderBriefing): nothing it makes is in the tree or reachable from
    /// another thread until it returns, and nothing is left for the end of a frame (the props' maps wait for the caller's
    /// <see cref="MaterialLibrary.FlushPropMaps"/> on the main thread).
    /// </summary>
    public bool OffMainThread { get; set; }
    /// <summary>Asked between the build's stages: the build stops there with an <see cref="OperationCanceledException"/> (a build under the briefing whose scene went away).</summary>
    public System.Threading.CancellationToken Cancel { get; set; }
    /// <summary>The world's root once <see cref="Build"/> has made it: the caller's to free when the build stops part way (outside the tree).</summary>
    public Node3D? Root { get; private set; }

    /// <summary>Leave vehicle placements out: the simulation draws them.</summary>
    public bool SkipVehicles { get; set; }
    /// <summary>Called with the mission's world as soon as it is read, before anything is built from it: the map editor puts its heights in here.</summary>
    public Action<I76.Formats.Assembly.MissionWorld>? WorldLoaded { get; set; }

    /// <summary>For a map that fills the grid: the road's quads past the grid's edge, drawn as strips in either look (the rest is painted into the terrain).</summary>
    public RoadNode? RoadsOffGround { get; private set; }

    public Node3D Build(string msnPath)
    {
        Stage("build start");
        var asm = Assembler = new ModelAssembler(vfs);
        var world = World = new MissionWorld(vfs, asm, msnPath);
        WorldLoaded?.Invoke(world);
        // a map too big to hold at once streams its ground (TerrainNode); its modern look is built with the rest, not deferred
        bool huge = world.Terrain.Huge, defer = DeferModern && !huge;
        // a community map's terrain: named by ZONE but shipped under the mission's stem, or shorter than ZMAP counts
        if (world.Mission.TerrainName() is { } zone && !System.IO.Path.GetFileName(world.TerrainPath).Equals(zone, System.StringComparison.OrdinalIgnoreCase))
            GD.Print($"terrain {zone} not found: {System.IO.Path.GetFileName(world.TerrainPath)} instead");
        if (world.Terrain.MissingTiles > 0) GD.Print($"terrain {System.IO.Path.GetFileName(world.TerrainPath)}: {world.Terrain.MissingTiles} of {world.Terrain.TileCount} tiles missing, flat");
        Palette? pal = null;
        if (world.PaletteName is { } pn && vfs.TryRead(pn) is { } pd) ActPalette = pal = new Palette(pd);
        else Notes.Add($"palette {world.PaletteName} not found");

        var resolver = Resolver = new TextureResolver(vfs);
        var textures = Textures = new TextureFactory(resolver, pal);
        var materials = Materials = new MaterialLibrary(textures) { HoldFlush = OffMainThread };
        Meshes = new MeshBuilder(resolver, materials);
        _carParts = null;

        var root = Root = new Node3D { Name = world.Mission.Name };

        // Ground: WDEF names the ground family ("tp041db6.map" for
        // TP04<level>DB6). The members are one image at five resolutions, a
        // mip chain the engine picks from per quad by distance, repeating every
        // 8 samples (measured in the original game). So the largest
        // member everywhere, with GPU mipmaps, is the engine's ground.
        Texture2D? ground = null;
        // WDEF lists the sky (nk_…), the ground family and the zmap: mostly tp…, but t04 has tt031sd_ and m15 tm151as_
        var settingsEarly = I76.Formats.Mission.WorldSettings.Read(world.Mission);
        var tp = world.WdefRefs.FirstOrDefault(r => r.EndsWith(".map", StringComparison.OrdinalIgnoreCase)
            && !r.StartsWith("zmap", StringComparison.OrdinalIgnoreCase) && !r.StartsWith("nk_", StringComparison.OrdinalIgnoreCase)
            && !r.Equals(settingsEarly?.Sky, StringComparison.OrdinalIgnoreCase));
        if (tp is { Length: >= 8 })
        {
            var best = Enumerable.Range(1, 9).Select(i => tp[..4] + i + tp[5..]).Select(resolver.FindByStem).OfType<string>()
                .Select(m => (m, e: textures.Get(m))).Where(x => x.e is not null).OrderByDescending(x => x.e!.Width * x.e.Height).FirstOrDefault();
            if (best.e is not null) { ground = best.e.Texture; GroundTexture = best.m; }
        }
        // Sky, horizon, light: WDEF's WRLD chunk.
        Settings = I76.Formats.Mission.WorldSettings.Read(world.Mission);
        if (Settings is not null) Light = I76.Formats.Mission.Lighting.ForHour(int.TryParse(System.Environment.GetEnvironmentVariable("I76_HOUR"), out int hr) ? hr : Settings.Hour);
        SkyName = Settings?.Sky ?? world.WdefRefs.FirstOrDefault(r => r.EndsWith(".map") && r != tp && !r.StartsWith("zmap"));
        Image[]? panels = null;
        if (I76.Formats.Mission.HorizonList.Parse(Settings is { Horizon.Length: > 0 } ? vfs.TryRead(Settings.Horizon) : null) is { } hz)
        {
            var imgs = new Godot.Collections.Array<Image>();
            foreach (var n in hz)
            {
                if (resolver.FindByStem(n) is not { } m || textures.GetRgba(m) is not { } t) { imgs.Clear(); break; }
                var im = Image.CreateFromData(t.Width, t.Height, false, Image.Format.Rgba8, t.Rgba);
                if (imgs.Count > 0 && (im.GetWidth() != imgs[0].GetWidth() || im.GetHeight() != imgs[0].GetHeight()))
                    im.Resize(imgs[0].GetWidth(), imgs[0].GetHeight());
                im.GenerateMipmaps();
                imgs.Add(im);
            }
            if (imgs.Count == I76.Formats.Mission.HorizonList.Panels)
            {
                Horizon = new Texture2DArray();
                Horizon.CreateFromImages(imgs);
                GC.KeepAlive(imgs);   // held past the call (see MeshArrays)
                if (defer) HorizonImages = [.. imgs];
                else if (EarlyHorizon && !huge) panels = [.. imgs.Select(im => (Image)im.Duplicate())];
            }
            else Notes.Add($"horizon {Settings!.Horizon}: textures missing");
        }
        if (resolver.FindByStem(Light.Sprite) is { } sp && textures.Get(sp) is { } spt) Sprite = spt.Texture;
        if (SkyName is not null && resolver.FindByStem(SkyName) is { } skyMember && textures.Get(skyMember) is { } sky)
        {
            SkyTexture = sky.Texture;
            var img = sky.Texture.GetImage();
            Palette = MeasureSky(img);
            img.Resize(1, 1, Image.Interpolation.Lanczos);
            SkyAverage = img.GetPixel(0, 0);
        }

        if (EarlyHorizon && !defer && !huge)   // (a map too big for the 3D ring keeps the panels' horizon, as the original draws it)
        {
            // the modern look's distant terrain, off this thread while the rest builds: pure CPU once it has the
            // panels' skyline and the ground's average colour (Main finishes and places it)
            var groundColour = new Color(0.5f, 0.32f, 0.2f);
            if (ground?.GetImage() is { } gi)
            {
                if (gi.IsCompressed()) gi.Decompress();
                gi.Resize(1, 1, Image.Interpolation.Lanczos);
                groundColour = gi.GetPixel(0, 0);
            }
            var h = new HorizonTerrain { Name = "HorizonTerrain" };
            var light = Light;
            HorizonBuild = System.Threading.Tasks.Task.Run(() =>
            {
                var profile = (panels is null ? null : HorizonTerrain.Measure(panels)) ?? HorizonTerrain.Default();
                h.ComputeCached(world, profile, light.Direction, groundColour);
                return h;
            });
        }
        Stage("ground, sky, horizon");
        TerrainMaterial = new ShaderMaterial { Shader = GD.Load<Shader>("res://Shaders/terrain_faithful.gdshader") };
        var l = Light.Direction;
        TerrainMaterial.SetShaderParameter("light_game", new Vector3(l.X, l.Y, l.Z));
        TerrainMaterial.SetShaderParameter("ambient", Light.Ambient);
        TerrainMaterial.SetShaderParameter("fade_a", I76.Formats.Mission.Lighting.FadeA);
        TerrainMaterial.SetShaderParameter("fade_b", I76.Formats.Mission.Lighting.FadeB);
        if (ground is not null) TerrainMaterial.SetShaderParameter("ground_tex", ground);
        // Roads: the engine's pieces, drawn as strips in 1997 mode and painted
        // into the terrain in modern mode.
        var pieces = RoadPieces = I76.Formats.Assembly.Roads.Build(world.Mission, world.BilinearHeight);
        var roadNames = I76.Formats.Assembly.Roads.TextureList(vfs.TryRead, world.Mission.Name);
        var flat = RoadRaster.Flatten(pieces);
        var roadTex = new Dictionary<int, RoadRaster.Tex?>();
        RoadRaster.Tex? RoadTex(int i)
        {
            if (!roadTex.TryGetValue(i, out var t))
                roadTex[i] = t = resolver.FindByStem(roadNames[i]) is { } m ? textures.GetRgba(m) : null;
            return t;
        }
        ShaderMaterial? sharedModern = null;
        bool cavities = false, painted = false;
        // the ground drawn as noise fitted to its texture (which then never repeats, GroundNoise), and, when fetched,
        // PBR materials coloured by it (TerrainMaterials): the modern look's, so a 1997 start makes them in the background
        IEnumerable<object?> MakeSharedModernSteps()
        {
            GroundNoise.NoiseTexture();
            yield return null;
            var gimg = ground?.GetImage();
            // a painted ground (M15's parking lot): asphalt with the texture's own layout instead, the shader's lot variant (GroundPaint)
            var paint = gimg is null ? null : GroundPaint.Find(gimg);
            painted = paint is not null;
            sharedModern = new ShaderMaterial { Shader = GD.Load<Shader>(painted ? GroundPaint.Shader : "res://Shaders/terrain_modern.gdshader") };
            GroundNoise.ApplyNoise(sharedModern);
            if (ground is not null)
            {
                sharedModern.SetShaderParameter("ground_tex", ground);
                yield return null;
                var fit = GroundNoise.Measure(gimg!, 40f);
                yield return null;
                GroundNoise.Apply(sharedModern, fit);
                bool mats = TerrainMaterials.Apply(sharedModern);
                if (TerrainMaterials.Note is { } note) Notes.Add(note);
                if (mats) sharedModern.SetShaderParameter("cavity_texels", (float)TerrainMaterials.CavitySize);
                if (paint is not null)
                {
                    if (!TerrainMaterials.AsphaltReady)
                    {
                        var imported = TerrainMaterials.ReadImportedAsphalt();
                        var stage = System.Threading.Tasks.Task.Run(() => TerrainMaterials.StageAsphalt(imported));
                        while (!stage.IsCompleted) yield return stage;
                    }
                    GroundPaint.Apply(sharedModern, paint, TerrainMaterials.CompleteAsphalt());
                    Notes.Add($"ground {GroundTexture}: painted ({paint.Coverage:P1} paint), drawn as asphalt");
                }
            }
            // a painted ground has no hollows to fill
            cavities = ground is not null && !painted && TerrainMaterials.Get() is not null;
        }
        Stage("roads flattened");
        var origins = TerrainNode.ChunkOrigins(world.Terrain);   // all of them: the road bake needs the whole map's
        System.Threading.Tasks.Task<TerrainNode.Prepared>? early = null;
        System.Threading.Tasks.Task<bool>? matsHit = null;
        System.Threading.Tasks.Task? matsStage = null;
        bool staging = false;
        if (!defer)
        {
            // off this thread while the roads bake: the modern terrain's surface and coarsest meshes, and the ground
            // materials (from the cache, else decoded and packed: the imported images come back on this thread)
            early = System.Threading.Tasks.Task.Run(() => TerrainNode.Prepare(world, huge ? [] : origins));
            if (ground is not null && !TerrainMaterials.Ready)
            {
                staging = true;
                matsHit = System.Threading.Tasks.Task.Run(TerrainMaterials.StageFromCache);
            }
        }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        // Rasterise every chunk's layer in parallel (pure C#); textures are
        // read once up front so the workers only read.
        Stage("road textures");
        for (int i = 0; i < roadNames.Length; i++) RoadTex(i);
        Stage("road pieces");
        // road-piece objects (ODEF types 0x50–0x5f, the intersections): their tops painted over the roads,
        // their textures numbered after the road set's
        var (pieceTris, pieceTex) = RoadPieceTops(world, asm, resolver, textures);
        for (int i = 0; i < pieceTex.Count; i++) roadTex[roadNames.Length + i] = pieceTex[i];
        for (int i = 0; i < pieceTris.Count; i++) pieceTris[i] = pieceTris[i] with { Texture = pieceTris[i].Texture + roadNames.Length };
        RoadNames = roadNames; RoadTextures = roadTex; RoadPieceTris = pieceTris;
        // not in the cache: the ground materials are made while the roads bake
        if (staging && !matsHit!.Result)
        {
            var imported = TerrainMaterials.ReadImported();
            matsStage = System.Threading.Tasks.Task.Run(() => TerrainMaterials.Stage(imported));
        }
        // the modern look closes the data's short gaps (a segment stopping a road step short of its intersection,
        // RoadGaps); the 1997 look keeps them, as the original draws them: the few chunks they touch are baked twice
        var fills = RoadRaster.Flatten(RoadGaps.Fill(I76.Formats.Assembly.Roads.Segments(world.Mission, world.BilinearHeight), flat, pieceTris, world.BilinearHeight));
        RoadFills = fills.Count;
        float chunkSize = TerrainNode.Chunk * 5f;
        bool Fills(int x0, int z0) => fills.Any(q => q.Max.X >= x0 * 5f && q.Max.Y >= z0 * 5f && q.Min.X < x0 * 5f + chunkSize && q.Min.Y < z0 * 5f + chunkSize);
        // (a map that fills the grid paints each chunk's layer as it streams in: LazyRoad, below)
        var origins97 = huge ? [] : origins.Where(o => Fills(o.X0, o.Z0)).ToList();
        Stage("road bake");
        var layers = huge ? [] : BakeRoadLayers([.. flat, .. fills], pieceTris, roadTex, origins);
        var layers97 = huge || origins97.Count == 0 ? [] : BakeRoadLayers(flat, pieceTris, roadTex, origins97);
        List<RoadRaster.FlatQuad> flatFilled = huge ? [.. flat, .. fills] : [];
        RoadRaster.Index? indexFilled = huge ? new RoadRaster.Index(flatFilled, pieceTris, TerrainNode.Chunk * 5f) : null;
        RoadRaster.Index? indexPlain = huge && fills.Count > 0 ? new RoadRaster.Index(flat, pieceTris, TerrainNode.Chunk * 5f) : null;
        var lazyRoad = new System.Collections.Concurrent.ConcurrentDictionary<(int, int), (Image? Modern, Image? Faithful, bool Own)>();
        Stage("road bake done");
        // the ground materials, staged meanwhile: made here, then the shared modern material that uses them
        if (!defer)
        {
            if (staging)
            {
                if (!matsHit!.Result) matsStage!.Wait();
                TerrainMaterials.Complete();
            }
            foreach (var step in MakeSharedModernSteps()) if (step is System.Threading.Tasks.Task t) t.Wait();
            Stage("ground materials");
        }
        var layerTex = new Dictionary<(int, int), ImageTexture>();
        ImageTexture? Layer(int x0, int z0)
        {
            if (layerTex.TryGetValue((x0, z0), out var t)) return t;
            Image? img;
            if (huge) { if (!lazyRoad.TryGetValue((x0, z0), out var lz) || (img = lz.Modern) is null) return null; }
            else if (!layers.TryGetValue((x0, z0), out img)) return null;
            RoadChunks++;
            return layerTex[(x0, z0)] = ImageTexture.CreateFromImage(img);
        }
        // the 1997 look's layer: without the gap fills where they reach the chunk
        var layer97Tex = new Dictionary<(int, int), ImageTexture?>();
        ImageTexture? Layer97(int x0, int z0)
        {
            if (huge)
            {
                // a streamed chunk's layers are used once (it is baked again if the chunk comes back): the textures are not kept
                var tm = Layer(x0, z0);
                layerTex.Remove((x0, z0));
                if (!lazyRoad.TryRemove((x0, z0), out var lz)) return tm;
                return !lz.Own ? tm : lz.Faithful is { } fi ? ImageTexture.CreateFromImage(fi) : null;
            }
            if (!origins97.Contains((x0, z0))) return Layer(x0, z0);
            if (!layer97Tex.TryGetValue((x0, z0), out var t))
                layer97Tex[(x0, z0)] = t = layers97.TryGetValue((x0, z0), out var img) ? ImageTexture.CreateFromImage(img) : null;
            return t;
        }
        var cavityImages = new Dictionary<(int, int), Image>();
        StringName[]? modernParams = null, faithfulParams = null;
        Material ModernFor(int x0, int z0)
        {
            var t = Layer(x0, z0);
            if (t is null && !cavities) return sharedModern!;
            var m = Copy(sharedModern!, ref modernParams);
            if (t is not null)
            {
                m.SetShaderParameter("road_layer", t);
                m.SetShaderParameter("has_road", true);
            }
            if (cavities)
            {
                // the hollows the materials put gravel in, from the heightfield around this chunk
                Image? made;
                lock (cavityImages) { made = null; if (huge) cavityImages.Remove((x0, z0), out made); else made = cavityImages.GetValueOrDefault((x0, z0)); }   // (a streamed chunk's is used once: it is made again if the chunk comes back)
                m.SetShaderParameter("cavity_tex", ImageTexture.CreateFromImage(made ?? TerrainMaterials.Cavity(world, x0, z0)));
                m.SetShaderParameter("chunk_origin", new Vector2(x0, z0));
                m.SetShaderParameter("has_cavity", true);
            }
            return m;
        }
        // 1997 mode paints the same layer, cut hard at half alpha and lit as the engine lights roads
        Material FaithfulFor(int x0, int z0)
        {
            if (Layer97(x0, z0) is not { } t) return TerrainMaterial;
            var m = Copy(TerrainMaterial, ref faithfulParams);
            m.SetShaderParameter("road_layer", t);
            m.SetShaderParameter("has_road", true);
            m.SetShaderParameter("road_light_y", Light.Direction.Y * Light.Intensity);
            return m;
        }

        if (!defer && cavities && !huge)
            System.Threading.Tasks.Parallel.ForEach(origins, o => { var im = TerrainMaterials.Cavity(world, o.X0, o.Z0); lock (cavityImages) cavityImages[o] = im; });
        Terrain = new TerrainNode { Name = "Terrain", FaithfulMaterial = TerrainMaterial, ModernFor = ModernFor, FaithfulFor = FaithfulFor, Deferred = defer, Early = early };
        if (huge)
            Terrain.PrepareChunk = (x0, z0) =>
            {
                if (cavities) { var im = TerrainMaterials.Cavity(world, x0, z0); lock (cavityImages) cavityImages[(x0, z0)] = im; }
                // the chunk's road layers, off the main thread: the modern look's with the gap fills, and the 1997 look's without where they reach it
                var modern = BakeChunkLayer(flatFilled, pieceTris, indexFilled!, roadTex, x0, z0);
                bool own = indexPlain is not null && Fills(x0, z0);
                var faithful = own ? BakeChunkLayer(flat, pieceTris, indexPlain!, roadTex, x0, z0) : null;
                if (modern is not null || own) lazyRoad[(x0, z0)] = (modern, faithful, own);
                if (modern is not null && System.Environment.GetEnvironmentVariable("I76_STREAM_DEBUG") == "1") System.Console.Error.WriteLine($"stream: road layer at chunk {x0},{z0} (game {x0 * 5},{z0 * 5})");
            };
        if (defer)
        {
            ModernReady = false;
            // ModernSteps' own work, on the pieces this method made
            _modernSteps = () => ModernStepsFor(world, ground, MakeSharedModernSteps, () => cavities, cavityImages);
        }
        Stage("terrain build");
        Terrain.Build(world);
        if (I76.Game.Editor.SwitchTimer.On) GD.Print($"switch: terrain content {world.Terrain.ContentHash():x16}");
        Stage("terrain built");
        root.AddChild(Terrain);
        if (world.Outside is not null && System.Environment.GetEnvironmentVariable("I76_OUTSIDE_CHUNKS") != "0")   // I76_OUTSIDE_CHUNKS=0: the ring alone out there (A/B)
        {
            OutsideNode = new OutsideTerrain { Name = "OutsideTerrain" };
            OutsideNode.Setup(world, TerrainMaterial);
            root.AddChild(OutsideNode);
        }
        RoadBakeMs = sw.ElapsedMilliseconds;

        Roads = new RoadNode { Name = "Roads", Visible = false, LightY = Light.Direction.Y * Light.Intensity, Ambient = Light.Ambient };
        Stage("roads build");
        Roads.Build(pieces, roadNames, resolver, textures);
        root.AddChild(Roads);
        if (huge)
        {
            // both looks paint the roads into the terrain's chunks, and a map that fills the grid starts its road past the grid's
            // edge (nearly73km's start line is 150 m outside it), where there are no chunks: those quads are drawn as strips
            RoadsOffGround = new RoadNode { Name = "RoadsOffGround", LightY = Roads.LightY, Ambient = Roads.Ambient };
            bool Off(double x, double z) => world.Terrain.Sample((int)Math.Floor(x * I76.Formats.Assembly.MissionWorld.SampleScale), (int)Math.Floor(z * I76.Formats.Assembly.MissionWorld.SampleScale)) is null;
            RoadsOffGround.Build(pieces, roadNames, resolver, textures, (piece, j) =>
            {
                var a = piece.Points[j]; var b = piece.Points[j + 1];
                var c = (a.Left + a.Right + b.Left + b.Right) * 0.25f;
                return Off(c.X, c.Z);
            });
            if (RoadsOffGround.Quads > 0) root.AddChild(RoadsOffGround); else { RoadsOffGround.Free(); RoadsOffGround = null; }
        }

        Scenery = new ScenerySpawner { Name = "Placements", Night = !Light.IsDay };
        Stage("scenery spawn");
        Scenery.Spawn(world, asm, Meshes, SkipVehicles);
        root.AddChild(Scenery);
        Stage("done");
        return root;
    }

    private Func<IEnumerable<object?>>? _modernSteps;

    /// <summary>
    /// A chunk's own copy of the shared terrain material: its shader and the parameters set on it (<paramref name="names"/>, the
    /// shader's uniforms, asked of the renderer once). Not <see cref="Resource.Duplicate"/>, which asks the renderer for the
    /// shader's uniforms and every unset one's default on each copy: calls that, from the thread of a build under the briefing,
    /// wait for a drawn frame each (thousands for a map's chunks). The unset ones keep the shader's defaults, as a copy's would.
    /// </summary>
    private static ShaderMaterial Copy(ShaderMaterial shared, ref StringName[]? names)
    {
        names ??= [.. RenderingServer.GetShaderParameterList(shared.Shader.GetRid()).Select(u => (StringName)(string)u["name"])];
        var m = new ShaderMaterial { Shader = shared.Shader, RenderPriority = shared.RenderPriority, NextPass = shared.NextPass };
        foreach (var n in names)
            if (shared.GetShaderParameter(n) is { VariantType: not Variant.Type.Nil } v) m.SetShaderParameter(n, v);
        return m;
    }

    /// <summary>A stage of <see cref="Build"/> reached: stamped (<see cref="Trace"/>), and the build stops here if it was called off.</summary>
    private void Stage(string stage)
    {
        Trace(stage);
        Cancel.ThrowIfCancellationRequested();
    }

    /// <summary>
    /// The modern terrain's deferred share, as steps for the caller to run a few milliseconds a frame on the main
    /// thread: <c>null</c> ends a slice, a <see cref="System.Threading.Tasks.Task"/> is waited for (it runs off the main
    /// thread at low priority). Ends with <see cref="ModernReady"/> and the terrain's modern meshes attached
    /// (<see cref="TerrainNode.UseModern"/> is the caller's to switch). Nothing if the modern look was built up front.
    /// </summary>
    public IEnumerable<object?> ModernSteps()
    {
        if (_modernSteps is null) yield break;
        foreach (var step in _modernSteps()) yield return step;
        _modernSteps = null;
    }

    /// <summary>A thread of its own at low priority: the build runs while the game draws the 1997 look, and must not take its frames.</summary>
    public static System.Threading.Tasks.Task Background(Action work)
    {
        var tcs = new System.Threading.Tasks.TaskCompletionSource();
        var t = new System.Threading.Thread(() =>
        {
            try { work(); tcs.SetResult(); } catch (Exception e) { tcs.SetException(e); }
        }) { IsBackground = true, Priority = System.Threading.ThreadPriority.BelowNormal, Name = "modern-terrain" };
        t.Start();
        return tcs.Task;
    }

    private IEnumerable<object?> ModernStepsFor(I76.Formats.Assembly.MissionWorld world, Texture2D? ground, Func<IEnumerable<object?>> makeShared, Func<bool> cavities, Dictionary<(int, int), Image> cavityImages)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        long last = 0;
        void Mark(string what) { if (TraceOn) GD.Print($"modern build: {what} +{clock.ElapsedMilliseconds - last} ms (at {clock.ElapsedMilliseconds})"); last = clock.ElapsedMilliseconds; }
        var terrain = Terrain!;
        // the heightfield's modern share: the reachable surface, every chunk's coarsest level
        var surface = Background(() => terrain.PrepareModern(world));
        // the ground materials meanwhile: from the bake cache, else the imported images come back on this thread, the
        // decode and BC7 off it; the textures here
        System.Threading.Tasks.Task? mats = null;
        if (ground is not null && !TerrainMaterials.Ready)
        {
            bool hit = false;
            var cached = Background(() => hit = TerrainMaterials.StageFromCache());
            while (!cached.IsCompleted) yield return cached;
            if (!hit)
            {
                var imported = TerrainMaterials.ReadImported();
                mats = Background(() => TerrainMaterials.Stage(imported));
            }
        }
        yield return null;
        while (mats is { IsCompleted: false } || !surface.IsCompleted) yield return mats is { IsCompleted: false } ? mats : surface;
        if (mats is { IsFaulted: true }) GD.PrintErr($"ground materials: {mats.Exception?.GetBaseException().Message}");
        if (surface.IsFaulted) throw surface.Exception!.GetBaseException();
        Mark("surface + materials staged");
        TerrainMaterials.Complete();
        yield return null;
        foreach (var step in makeShared()) yield return step;
        Mark("materials, shared material");
        if (cavities())
        {
            // the hollows' textures: their pixels off the thread
            var origins = new List<(int, int)>();
            for (int i = 0; i < terrain.AttachCount; i++) origins.Add(terrain.ChunkOrigin(i));
            var cav = Background(() => System.Threading.Tasks.Parallel.ForEach(origins, o => { var im = TerrainMaterials.Cavity(world, o.Item1, o.Item2); lock (cavityImages) cavityImages[o] = im; }));
            while (!cav.IsCompleted) yield return cav;
            Mark("cavity images");
        }
        // the chunks' materials and meshes, as many as fit in a slice
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < terrain.AttachCount; i++)
        {
            terrain.AttachModern(i);
            if (sw.Elapsed.TotalMilliseconds > 1.5) { sw.Restart(); yield return null; }
        }
        terrain.FinishModern();
        Mark("chunks attached");
        foreach (var step in terrain.PrewarmSteps()) yield return step;
        Mark("levels near the camera");
        ModernReady = true;
    }

    private static ulong H64(byte[] d) => System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(System.Security.Cryptography.SHA256.HashData(d));
    public static readonly bool TraceOn = System.Environment.GetEnvironmentVariable("I76_BUILD_TRACE") is "1" or "hash";
    /// <summary>I76_BUILD_TRACE=hash: the stamps, and hashes of what the bakes made (the road layers, the ground materials, the distant terrain), to compare two builds bit for bit.</summary>
    public static readonly bool HashOn = System.Environment.GetEnvironmentVariable("I76_BUILD_TRACE") == "hash";
    private static readonly System.Diagnostics.Stopwatch TraceClock = System.Diagnostics.Stopwatch.StartNew();
    /// <summary>I76_BUILD_TRACE=1: stamps each build stage on stdout, to see where a stuck load stopped.</summary>
    public static void Trace(string stage) { LastStage = stage; if (TraceOn) GD.Print($"build trace: {stage} @{TraceClock.ElapsedMilliseconds} ms"); I76.Game.Editor.SwitchTimer.Mark(stage); }
    /// <summary>The last stage stamped (on any thread): what a slice of the load under a briefing was doing (Main.EndSlice).</summary>
    public static string LastStage { get; private set; } = "";
}
