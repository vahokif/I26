using Godot;
using System.Linq;
using I76.Formats.Assembly;

namespace I76.Game.World;

/// <summary>
/// The modern terrain's ground materials: ambientCG's CC0 PBR scans fetched
/// by tools/fetch-ground.sh into game/ground/ (never committed), packed into
/// two texture arrays for terrain_modern.gdshader, one layer a material:
/// <list type="bullet">
/// <item><c>mat_albedo</c>: the colour (sRGB) and, in alpha, the
/// displacement, normalised to a mean of 0.5 and a spread of 0.15 so the
/// shader's height blend treats every set alike;</item>
/// <item><c>mat_normal</c>: the OpenGL normal's X and Y, the roughness and
/// the ambient occlusion.</item>
/// </list>
/// The colours are not used as they are: the shader keeps only each
/// material's structure (its luminance against its mean, in units of its
/// spread, and a little of its hue against its mean hue) and colours it
/// through the mission's own ground palette (GroundNoise), so a red desert
/// stays red and a snowfield white. Missing files leave <see cref="Get"/>
/// null and the terrain on its procedural ground.
/// </summary>
public static class TerrainMaterials
{
    /// <summary>The sets by ambientCG id, in layer order (the shader names the layers).</summary>
    public static readonly string[] Sets = ["Ground102", "Ground031", "Ground079S", "Rock061"];
    /// <summary>The asphalt scan painted grounds are drawn with (GroundPaint), a pack of its own made on first use.</summary>
    public static readonly string[] AsphaltSets = ["Asphalt015"];
    private static readonly string[] Maps = ["Color", "Displacement", "NormalGL", "Roughness", "AmbientOcclusion"];
    // not every set has one (Asphalt015): then none, white
    private const int AoMap = 4;

    public sealed record Pack(Texture2DArray Albedo, Texture2DArray Normal, Vector3[] MeanColour, float[] LumMean, float[] LumSpread);

    /// <summary>I76_MAT_COMPRESS=0 keeps the material arrays uncompressed (RGBA8), for A/B comparisons.</summary>
    // the compressors are in editor builds only: in an export every call would fail (and log an error)
    private static readonly bool Compress = OS.HasFeature("editor") && System.Environment.GetEnvironmentVariable("I76_MAT_COMPRESS") != "0";
    private static Pack? _pack;
    private static bool _tried;
    public static string? Note { get; private set; }

    /// <summary>The packed materials, built once and kept; null when the files are not there.</summary>
    public static Pack? Get()
    {
        if (_tried) return _pack;
        _tried = true;
        // I76_GROUND_MATERIALS=0: the procedural ground alone, as without the files (for A/B costs)
        if (System.Environment.GetEnvironmentVariable("I76_GROUND_MATERIALS") == "0") { Note = "ground materials: off (I76_GROUND_MATERIALS=0)"; GD.Print(Note); return null; }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try { _pack = Finish(FromCache(Sets) ?? Built(Sets, ReadImported(Sets))); }
        catch (Exception e) { Note = $"ground materials: {e.Message}"; _pack = null; }
        if (_pack is not null) Note = $"ground materials: {Sets.Length} sets, {sw.ElapsedMilliseconds} ms";
        GD.Print(Note);
        return _pack;
    }

    /// <summary>
    /// An export's imported texture for one map, read back on the calling (main) thread: GetImage waits on the
    /// rendering server, which serves only the main thread's queue, so from a worker while the main thread
    /// sits in Parallel.For it never returns (the release hung on the loading card). Null in a development run.
    /// </summary>
    private static Image? Imported(string set, string map)
    {
        var res = $"res://ground/{set}_{map}.jpg";
        return ResourceLoader.Exists(res) ? GD.Load<Texture2D>(res)?.GetImage() : null;
    }

    private static string GroundFile(string set, string map) => ProjectSettings.GlobalizePath($"res://ground/{set}_{map}.jpg");

    /// <summary>One map from game/ground: the imported texture's image in an export, else the fetched file read directly. Safe on any thread.</summary>
    private static Image? Load(string set, string map, Image? imported)
    {
        var res = $"res://ground/{set}_{map}.jpg";
        Image? img = imported;
        if (img is null && System.IO.File.Exists(GroundFile(set, map))) img = Image.LoadFromFile(GroundFile(set, map));
        if (img is null || img.IsEmpty()) return null;
        if (img.IsCompressed()) img.Decompress();
        img.ClearMipmaps();
        img.Convert(Image.Format.Rgba8);
        return img;
    }

    /// <summary>An export's textures come back from the GPU on the main thread (Imported); step one of a staged build.</summary>
    public static Image?[] ReadImported() => ReadImported(Sets);

    private static Image?[] ReadImported(string[] sets)
    {
        var imported = new Image?[sets.Length * Maps.Length];
        for (int k = 0; k < imported.Length; k++) imported[k] = Imported(sets[k / Maps.Length], Maps[k % Maps.Length]);
        return imported;
    }

    private sealed record Staged(Image[] Albedo, Image[] Normal, Vector3[] Mean, float[] LumMean, float[] LumSpread, bool[] Packed);
    private static Staged? _staged;

    /// <summary>
    /// Step two of a staged build, safe on any thread: decodes, packs and compresses the images, making no GPU
    /// resource. <see cref="Complete"/> then makes the textures on the main thread. A pack that cannot be built leaves
    /// <see cref="Note"/> and the terrain on its procedural ground.
    /// </summary>
    public static void Stage(Image?[] imported)
    {
        if (System.Environment.GetEnvironmentVariable("I76_GROUND_MATERIALS") == "0") return;
        try { _staged = Built(Sets, imported); }
        catch (Exception e) { Note = $"ground materials: {e.Message}"; }
    }

    /// <summary>Whether the pack has been made (or found missing) this session: <see cref="Get"/> returns it at once.</summary>
    public static bool Ready => _tried;

    /// <summary>
    /// Step two from the bake cache, any thread: true (and nothing more to stage) if the packed images were there.
    /// Only an editor build's are kept (their BC7 compression is most of the cost); an export's pack is quicker made.
    /// </summary>
    public static bool StageFromCache()
    {
        if (System.Environment.GetEnvironmentVariable("I76_GROUND_MATERIALS") == "0") return true;
        try { return (_staged = FromCache(Sets)) is not null; }
        catch (Exception e) { GD.PrintErr($"ground materials cache: {e.Message}"); return false; }
    }

    /// <summary>
    /// The cache key: the fetched files and their import settings (what the imported images are made from), the
    /// layout of the pack, the engine; null where there are no source files to read (an export) or no cache.
    /// </summary>
    private static string? CacheKey(string[] sets)
    {
        if (!Compress || MissionBuilder.Cache is null) return null;
        using var k = new I76.Formats.Assembly.BakeCache.Key("ground-materials");
        k.Add(PackVersion).Add(MissionBuilder.EngineVersion).Add(Compress);
        foreach (var set in sets)
            for (int m = 0; m < Maps.Length; m++)
            {
                var (map, path) = (Maps[m], GroundFile(set, Maps[m]));
                if (!System.IO.File.Exists(path))
                {
                    if (m == AoMap) { k.Add(set + "_" + map + " none"); continue; }
                    return null;
                }
                k.Add(set + "_" + map).Add<byte>(System.IO.File.ReadAllBytes(path));
                k.Add<byte>(System.IO.File.Exists(path + ".import") ? System.IO.File.ReadAllBytes(path + ".import") : []);
            }
        return k.Finish();
    }

    /// <summary>Changes with anything in <see cref="BuildImages"/> that changes the pack: cached packs are keyed by it.</summary>
    private const int PackVersion = 1;

    private static Staged? FromCache(string[] sets)
    {
        // an editor build bakes and caches its own (I76_PREBAKED=1: take the prebaked file, to test an export's way)
        if ((!OS.HasFeature("editor") || System.Environment.GetEnvironmentVariable("I76_PREBAKED") == "1") && FromPrebaked(sets) is { } baked) return baked;
        if (CacheKey(sets) is not { } key || MissionBuilder.Cache!.Read("ground", key) is not { } blobs) return null;
        int n = sets.Length;
        if (blobs.Count != 2 * n + 1 || blobs[^1].Tag != -1) return null;
        using var r = new System.IO.BinaryReader(new System.IO.MemoryStream(blobs[^1].Data));
        int size = r.ReadInt32();
        var mean = new Vector3[n]; var lumMean = new float[n]; var lumSpread = new float[n]; var packed = new bool[n];
        for (int s = 0; s < n; s++) { mean[s] = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()); lumMean[s] = r.ReadSingle(); lumSpread[s] = r.ReadSingle(); packed[s] = r.ReadBoolean(); }
        Image Img(BakeCache.Blob b) => Image.CreateFromData(size, size, true, (Image.Format)b.Meta, b.Data);
        var albedo = new Image[n]; var normal = new Image[n];
        for (int s = 0; s < n; s++) { albedo[s] = Img(blobs[2 * s]); normal[s] = Img(blobs[2 * s + 1]); }
        Trace("cache", albedo, normal);
        return new Staged(albedo, normal, mean, lumMean, lumSpread, packed);
    }

    // ---- the prebaked pack -------------------------------------------------------------------------------------------------
    // An export has no texture compressor, so left alone the pack would be RGBA8 on the GPU (4x the video memory: with the 2K sets
    // over 200 MB). tools/release.sh therefore runs the editor once (--bake-ground-pack) and packs the result, one variant per
    // GPU family (BPTC for desktop, ASTC for the ARM GPUs), as res://ground/pack.bin; a run takes the variant its GPU
    // accepts, else (no such file, or a GPU with neither) builds the RGBA8 pack from the imported textures as before.

    public const string PrebakedPath = "res://ground/pack.bin";
    private const int PrebakedMagic = 0x50364932;   // "2I6P"
    private static readonly (string Name, Image.CompressMode Mode)[] Variants = [("bptc", Image.CompressMode.Bptc), ("astc", Image.CompressMode.Astc)];

    /// <summary>The variant of the prebaked pack this GPU takes, or null.</summary>
    private static string? GpuVariant => RenderingServer.HasOsFeature("bptc") ? "bptc" : RenderingServer.HasOsFeature("astc") ? "astc" : null;

    /// <summary>Bakes the pack of every set group in every variant into <paramref name="path"/> (editor builds with the ground files; true when done).</summary>
    public static bool WritePrebaked(string path)
    {
        if (!OS.HasFeature("editor")) { GD.PrintErr("--bake-ground-pack needs the editor build"); return false; }
        try
        {
            using var w = new System.IO.BinaryWriter(System.IO.File.Create(path));
            w.Write(PrebakedMagic); w.Write(PackVersion);
            var groups = new[] { Sets, AsphaltSets };
            var entries = new List<(string Id, string Variant, Staged St)>();
            foreach (var g in groups)
                foreach (var (name, mode) in Variants)
                {
                    var st = BuildImages(g, new Image?[g.Length * Maps.Length], mode);
                    if (st is null) { GD.PrintErr($"--bake-ground-pack: {string.Join(",", g)} cannot be built ({Note ?? "files missing"})"); return false; }
                    if (st.Packed.Any(ok => !ok)) { GD.PrintErr($"--bake-ground-pack: {name} compression failed"); return false; }
                    entries.Add((string.Join(",", g), name, st));
                }
            w.Write(entries.Count);
            foreach (var (id, variant, st) in entries)
            {
                w.Write(id); w.Write(variant); w.Write(st.Albedo[0].GetWidth()); w.Write(st.Albedo.Length);
                for (int s = 0; s < st.Albedo.Length; s++)
                {
                    w.Write(st.Mean[s].X); w.Write(st.Mean[s].Y); w.Write(st.Mean[s].Z); w.Write(st.LumMean[s]); w.Write(st.LumSpread[s]);
                    foreach (var im in new[] { st.Albedo[s], st.Normal[s] }) { var d = im.GetData(); w.Write((int)im.GetFormat()); w.Write(d.Length); w.Write(d); }
                }
            }
            GD.Print($"ground pack baked: {entries.Count} entries, {w.BaseStream.Length / 1048576.0:0.0} MiB");
            return true;
        }
        catch (Exception e) { GD.PrintErr($"--bake-ground-pack: {e.Message}"); return false; }
    }

    /// <summary>The prebaked images for <paramref name="sets"/> in the variant this GPU takes; null when the file or the variant is not there.</summary>
    private static Staged? FromPrebaked(string[] sets)
    {
        if (GpuVariant is not { } want) return null;
        // I76_PREBAKED_FILE: a file on disk instead of the pack's (to test an export's way without writing into game/ground)
        string? file = System.Environment.GetEnvironmentVariable("I76_PREBAKED_FILE");
        if (file is null ? !Godot.FileAccess.FileExists(PrebakedPath) : !System.IO.File.Exists(file)) return null;
        try
        {
            using var r = new System.IO.BinaryReader(new System.IO.MemoryStream(file is null ? Godot.FileAccess.GetFileAsBytes(PrebakedPath) : System.IO.File.ReadAllBytes(file)));
            if (r.ReadInt32() != PrebakedMagic || r.ReadInt32() != PackVersion) return null;
            string id = string.Join(",", sets);
            for (int e = r.ReadInt32(); e > 0; e--)
            {
                string gid = r.ReadString(), variant = r.ReadString();
                int size = r.ReadInt32(), n = r.ReadInt32();
                bool take = gid == id && variant == want;
                var albedo = new Image[n]; var normal = new Image[n]; var mean = new Vector3[n]; var lumMean = new float[n]; var lumSpread = new float[n];
                for (int s = 0; s < n; s++)
                {
                    mean[s] = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()); lumMean[s] = r.ReadSingle(); lumSpread[s] = r.ReadSingle();
                    for (int k = 0; k < 2; k++)
                    {
                        int fmt = r.ReadInt32(); var d = r.ReadBytes(r.ReadInt32());
                        if (!take) continue;
                        (k == 0 ? albedo : normal)[s] = Image.CreateFromData(size, size, true, (Image.Format)fmt, d);
                    }
                }
                if (take) { Trace("prebaked", albedo, normal); return new Staged(albedo, normal, mean, lumMean, lumSpread, Enumerable.Repeat(true, n).ToArray()); }
            }
        }
        catch (Exception e) { GD.PrintErr($"ground pack: {e.Message}"); }
        return null;
    }

    /// <summary><see cref="BuildImages"/>, and the result into the bake cache (off the load's path).</summary>
    private static Staged? Built(string[] sets, Image?[] imported)
    {
        var st = BuildImages(sets, imported);
        if (st is null) return null;
        Trace("built", st.Albedo, st.Normal);
        if (CacheKey(sets) is { } key)
        {
            var blobs = new List<BakeCache.Blob>();
            for (int s = 0; s < st.Albedo.Length; s++)
            {
                blobs.Add(new(2 * s, (int)st.Albedo[s].GetFormat(), st.Albedo[s].GetData()));
                blobs.Add(new(2 * s + 1, (int)st.Normal[s].GetFormat(), st.Normal[s].GetData()));
            }
            var ms = new System.IO.MemoryStream();
            using (var w = new System.IO.BinaryWriter(ms))
            {
                w.Write(st.Albedo[0].GetWidth());
                for (int s = 0; s < st.Albedo.Length; s++)
                {
                    w.Write(st.Mean[s].X); w.Write(st.Mean[s].Y); w.Write(st.Mean[s].Z);
                    w.Write(st.LumMean[s]); w.Write(st.LumSpread[s]); w.Write(st.Packed[s]);
                }
            }
            blobs.Add(new(-1, 0, ms.ToArray()));
            MissionBuilder.Background(() => MissionBuilder.Cache!.Write("ground", key, blobs));
        }
        return st;
    }

    /// <summary>I76_BUILD_TRACE: a hash of the pack's images, to compare a cached pack with a fresh one.</summary>
    private static void Trace(string how, Image[] albedo, Image[] normal)
    {
        if (!MissionBuilder.HashOn) return;
        using var h = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        foreach (var im in albedo.Concat(normal)) { h.AppendData(BitConverter.GetBytes((int)im.GetFormat())); h.AppendData(im.GetData()); }
        GD.Print($"ground materials ({how}): pack hash {Convert.ToHexString(h.GetHashAndReset())[..16]}");
    }

    /// <summary>Step three, on the main thread: the textures from the staged images; the pack <see cref="Get"/> then returns.</summary>
    public static void Complete()
    {
        _tried = true;
        if (System.Environment.GetEnvironmentVariable("I76_GROUND_MATERIALS") == "0") { Note = "ground materials: off (I76_GROUND_MATERIALS=0)"; GD.Print(Note); return; }
        if (_staged is not null) { _pack = Finish(_staged); _staged = null; Note = $"ground materials: {Sets.Length} sets"; }
        GD.Print(Note);
    }

    private static Staged? BuildImages(string[] sets, Image?[] imported, Image.CompressMode? mode = null)
    {
        int n = sets.Length;
        var imgs = new Image?[n, Maps.Length];
        System.Threading.Tasks.Parallel.For(0, n * Maps.Length, k => imgs[k / Maps.Length, k % Maps.Length] = Load(sets[k / Maps.Length], Maps[k % Maps.Length], imported[k]));
        for (int s = 0; s < n; s++)
            for (int m = 0; m < Maps.Length; m++)
                if (imgs[s, m] is null && m != AoMap)
                {
                    if (sets == Sets) Note = $"ground materials: {sets[s]}_{Maps[m]} missing (tools/fetch-ground.sh)";
                    return null;
                }
        int size = imgs[0, 0]!.GetWidth();
        var albedo = new Image[n];
        var normal = new Image[n];
        var mean = new Vector3[n];
        var lumMean = new float[n];
        var lumSpread = new float[n];
        var packed = new bool[n];
        System.Threading.Tasks.Parallel.For(0, n, s =>
        {
            var d = new byte[Maps.Length][];
            for (int m = 0; m < Maps.Length; m++)
            {
                if (imgs[s, m] is not { } im) { d[m] = new byte[size * size * 4]; Array.Fill(d[m], (byte)255); continue; }   // no AO map: none
                if (im.GetWidth() != size || im.GetHeight() != size) im.Resize(size, size, Image.Interpolation.Cubic);
                d[m] = im.GetData();
            }
            byte[] col = d[0], disp = d[1], nrm = d[2], rough = d[3], ao = d[4];
            int px = size * size;
            // the displacement's mean and spread, to normalise it
            double hs = 0, hs2 = 0;
            for (int i = 0; i < px; i++) { double h = disp[i * 4] / 255.0; hs += h; hs2 += h * h; }
            double hm = hs / px, hsd = Math.Sqrt(Math.Max(1e-6, hs2 / px - hm * hm));
            // the colour's mean (linear) and its luminance's spread
            double r = 0, g = 0, b = 0, ls = 0, ls2 = 0;
            var a = new byte[px * 4];
            var o = new byte[px * 4];
            for (int i = 0; i < px; i++)
            {
                double lr = Lin(col[i * 4]), lg = Lin(col[i * 4 + 1]), lb = Lin(col[i * 4 + 2]);
                r += lr; g += lg; b += lb;
                double l = 0.2126 * lr + 0.7152 * lg + 0.0722 * lb;
                ls += l; ls2 += l * l;
                a[i * 4] = col[i * 4]; a[i * 4 + 1] = col[i * 4 + 1]; a[i * 4 + 2] = col[i * 4 + 2];
                a[i * 4 + 3] = (byte)Math.Clamp(Math.Round(255 * (0.5 + 0.15 * (disp[i * 4] / 255.0 - hm) / hsd)), 0, 255);
                o[i * 4] = nrm[i * 4]; o[i * 4 + 1] = nrm[i * 4 + 1]; o[i * 4 + 2] = rough[i * 4]; o[i * 4 + 3] = ao[i * 4];
            }
            mean[s] = new Vector3((float)(r / px), (float)(g / px), (float)(b / px));
            lumMean[s] = (float)(ls / px);
            lumSpread[s] = (float)Math.Sqrt(Math.Max(1e-8, ls2 / px - lumMean[s] * (double)lumMean[s]));
            albedo[s] = Image.CreateFromData(size, size, false, Image.Format.Rgba8, a);
            normal[s] = Image.CreateFromData(size, size, false, Image.Format.Rgba8, o);
            albedo[s].GenerateMipmaps();
            normal[s].GenerateMipmaps();
            // BC7: a quarter of the memory traffic for the terrain's most-read textures (two to a dozen reads a
            // pixel, in the depth pre-pass and again in the opaque pass): -0.7 to -2 ms a frame, looks the same.
            // Godot's compressor is in editor builds only; without it they stay RGBA8 (all layers alike).
            if ((mode ?? (Compress ? Image.CompressMode.Bptc : (Image.CompressMode?)null)) is { } cm)
            {
                packed[s] = albedo[s].Compress(cm, Image.CompressSource.Srgb) == Error.Ok
                         && normal[s].Compress(cm, Image.CompressSource.Generic) == Error.Ok;
            }
        });
        return new Staged(albedo, normal, mean, lumMean, lumSpread, packed);
    }

    private static Pack? Finish(Staged? st)
    {
        if (st is null) return null;
        var (albedo, normal, mean, lumMean, lumSpread, packed) = (st.Albedo, st.Normal, st.Mean, st.LumMean, st.LumSpread, st.Packed);
        if (packed.Any(ok => !ok))
            foreach (var im in albedo.Concat(normal))
                if (im.IsCompressed()) im.Decompress();
        var at = new Texture2DArray();
        at.CreateFromImages(new Godot.Collections.Array<Image>(albedo));
        var nt = new Texture2DArray();
        nt.CreateFromImages(new Godot.Collections.Array<Image>(normal));
        return new Pack(at, nt, mean, lumMean, lumSpread);
    }

    private static Pack? _asphalt;
    private static bool _asphaltTried;

    /// <summary>Whether <see cref="Asphalt"/> has been tried this session (made or found missing).</summary>
    public static bool AsphaltReady => _asphaltTried;

    /// <summary>
    /// The asphalt scan as a one-layer pack, like <see cref="Get"/>'s (from the bake cache, else built), made on first use and
    /// kept; null when its files are not fetched. <paramref name="imported"/>: <see cref="ReadImportedAsphalt"/>, read on the
    /// main thread; the rest is safe on any thread but for <see cref="CompleteAsphalt"/>.
    /// </summary>
    public static void StageAsphalt(Image?[] imported)
    {
        if (_asphaltTried || System.Environment.GetEnvironmentVariable("I76_GROUND_MATERIALS") == "0") return;
        try { _asphaltStaged = FromCache(AsphaltSets) ?? Built(AsphaltSets, imported); }
        catch (Exception e) { GD.PrintErr($"asphalt material: {e.Message}"); }
    }
    private static Staged? _asphaltStaged;

    public static Image?[] ReadImportedAsphalt() => _asphaltTried ? [] : ReadImported(AsphaltSets);

    /// <summary>The staged asphalt's textures (main thread).</summary>
    public static Pack? CompleteAsphalt()
    {
        if (_asphaltTried) return _asphalt;
        _asphaltTried = true;
        _asphalt = Finish(_asphaltStaged);
        _asphaltStaged = null;
        GD.Print(_asphalt is not null ? $"asphalt material: {AsphaltSets[0]}"
            : System.Environment.GetEnvironmentVariable("I76_GROUND_MATERIALS") == "0" ? "asphalt material: off (I76_GROUND_MATERIALS=0)"
            : $"asphalt material: {AsphaltSets[0]} missing (tools/fetch-ground.sh)");
        return _asphalt;
    }

    public static Pack? Asphalt => _asphalt;

    private static double Lin(byte c)
    {
        double v = c / 255.0;
        return v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
    }

    /// <summary>Cavity texels a side: the chunk's samples (Chunk + 1) and an apron of one each side.</summary>
    public const int CavitySize = TerrainNode.Chunk + 3;
    /// <summary>Metres of hollow per unit of the cavity texture (0.5 is level ground).</summary>
    public const float CavityScale = 20f;

    /// <summary>
    /// How far each sample of the chunk at (x0, z0) lies below its
    /// surroundings: the mean height within 3 samples (15 m) less its own,
    /// positive in gullies and hollows, negative on ridges, as
    /// 0.5 + metres / CavityScale in R8, one texel a sample starting one
    /// before the chunk's first. Read from the heightfield itself (not the
    /// drawn mesh), so it agrees across chunk edges.
    /// </summary>
    public static Image Cavity(I76.Formats.Assembly.MissionWorld w, int x0, int z0)
    {
        const int r = 3, size = CavitySize, span = size + 2 * r;
        var h = new float[span * span];
        var ok = new bool[span * span];
        for (int j = 0; j < span; j++)
            for (int i = 0; i < span; i++)
                if (w.Terrain.Sample(x0 - 1 - r + i, z0 - 1 - r + j) is { } v)
                {
                    h[j * span + i] = (float)((v & I76.Formats.Mission.MissionFile.HeightMask) * I76.Formats.Assembly.MissionWorld.HeightScale);
                    ok[j * span + i] = true;
                }
        // box means over (2r+1)^2, separably, counting only samples inside a tile
        var rowS = new float[span * size];
        var rowN = new int[span * size];
        for (int j = 0; j < span; j++)
            for (int i = 0; i < size; i++)
            {
                float s = 0; int c = 0;
                for (int d = 0; d <= 2 * r; d++) { int k = j * span + i + d; if (ok[k]) { s += h[k]; c++; } }
                rowS[j * size + i] = s; rowN[j * size + i] = c;
            }
        var px = new byte[size * size];
        for (int j = 0; j < size; j++)
            for (int i = 0; i < size; i++)
            {
                int k = (j + r) * span + i + r;
                float s = 0; int c = 0;
                for (int d = 0; d <= 2 * r; d++) { s += rowS[(j + d) * size + i]; c += rowN[(j + d) * size + i]; }
                float hollow = ok[k] && c > 0 ? s / c - h[k] : 0;
                px[j * size + i] = (byte)Math.Clamp(Math.Round(255 * (0.5 + hollow / CavityScale)), 0, 255);
            }
        return Image.CreateFromData(size, size, false, Image.Format.R8, px);
    }

    /// <summary>The materials into a terrain material; false (and nothing set) without them.</summary>
    public static bool Apply(ShaderMaterial m)
    {
        if (Get() is not { } p) return false;
        m.SetShaderParameter("use_materials", true);
        if (int.TryParse(System.Environment.GetEnvironmentVariable("I76_TERRAIN_DEBUG"), out int dv)) m.SetShaderParameter("debug_view", dv);
        m.SetShaderParameter("mat_albedo", p.Albedo);
        m.SetShaderParameter("mat_normal", p.Normal);
        m.SetShaderParameter("mat_mean", p.MeanColour);
        m.SetShaderParameter("mat_lum_mean", new Vector4(p.LumMean[0], p.LumMean[1], p.LumMean[2], p.LumMean[3]));
        m.SetShaderParameter("mat_lum_spread", new Vector4(p.LumSpread[0], p.LumSpread[1], p.LumSpread[2], p.LumSpread[3]));
        return true;
    }
}
