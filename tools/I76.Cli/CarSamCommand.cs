using I76.Formats.Assembly;
using I76.Formats.Imaging;
using I76.Formats.Vfs;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace I76.Cli;

/// <summary>
/// The car material segmentation pipeline's C# half:
///   i76 car-views OUT_DIR [--stems a,b] [--nitro] [--scheme N] [--size 1024] [--flat]
///       plans the cameras of every model (per texture face-cluster, square-on, plus base views), writes OUT_DIR/STEM/view_NN.ppm (the colour pass), views.tsv, textures.tsv
///       and a coverage report (texels no view sees)
///   i76 car-project OUT_DIR [--stems a,b] [--nitro] [--scheme N] [--out FILE] [--sheets DIR]
///       re-renders the same views for their ID passes, back-projects OUT_DIR/STEM/view_NN.lab.pgm (the segmenter's labels = preset ids) onto the textures,
///       and writes the maps (CarMasks.bin format) to FILE, review sheets to DIR
/// </summary>
public static class CarSamCommand
{
    public sealed record Plan(List<CarTri> Tris, List<CarTexture> Tex, List<CarCamera> Cams, float RefHalf, Vector3 Centre, float Radius);

    public static int Run(List<string> argv, string? installDir)
    {
        string? Opt(string n) { int i = argv.IndexOf(n); return i >= 0 && i + 1 < argv.Count ? argv[i + 1] : null; }
        bool nitro = argv.Contains("--nitro");
        string? dir = nitro ? Environment.GetEnvironmentVariable("I76_NITRO_INSTALL") : installDir;
        if (dir is null) { Console.Error.WriteLine("set I76_INSTALL / I76_NITRO_INSTALL"); return 2; }
        var vfs = new AssetVfs(new GameInstall(dir));
        var res = new TextureResolver(vfs);
        var stems = Opt("--stems")?.Split(',') ?? (nitro ? ResprayCommand.NitroStems : ResprayCommand.BaseStems);
        string root = argv[1];
        int size = int.Parse(Opt("--size") ?? "1024");
        // car-glass DIR: each texture's glass as the game makes it (the material table over I76_CAR_MASKS_FILE; GlassShape's polygons drawn
        // over the texture, 8 pixels a texel), for every model and paint scheme layout
        if (argv[0] == "car-glass") return GlassCheck(argv, vfs, res, stems, argv[1], nitro);
        bool project = argv[0] == "car-project";
        // --orbit: natural views only (two rings looking down a little); --raw: the projection and its vote alone, no rules after it
        Orbit = argv.Contains("--orbit"); Raw = argv.Contains("--raw"); Denoise = argv.Contains("--denoise"); Refine = argv.Contains("--refine-glass");
        if (Opt("--compare") is { } cmpFile && argv.IndexOf("--compare") + 2 < argv.Count)
        {
            using (var cf = File.OpenRead(cmpFile)) CompareOld = CarMaskMap.Read(cf);
            CompareDir = argv[argv.IndexOf("--compare") + 2];
            Directory.CreateDirectory(CompareDir);
        }
        string outFile = Opt("--out") ?? Path.Combine(root, "CarMasks.bin");
        // an existing file is updated: the models of this run replace their entries, the rest stay
        CarMaskMap? maps = null;
        if (project) { maps = new CarMaskMap(); if (File.Exists(outFile)) { using var fs = File.OpenRead(outFile); maps = CarMaskMap.Read(fs); } }
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var stem in stems)
        {
            var ms = ModelSchemes.Load(vfs, stem);
            if (ms is null) { Console.WriteLine($"{stem}: not loadable"); continue; }
            // --scheme N: just that scheme; --scheme all: every paint scheme, but a later one only when it has a texture layout (name with the digit as ?, size) no earlier one had
            var covered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var schemes = Opt("--scheme") == "all" ? Enumerable.Range(0, ms.Vtfs.Count).ToList() : [Math.Min(int.Parse(Opt("--scheme") ?? "0"), ms.Vtfs.Count - 1)];
            foreach (int k in schemes)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var plan = Build(ms, ms.Vtfs[k], res, size);
                var keys = plan.Tex.Select(t => $"{CarMaskMap.KeyOf(t.Member)}:{t.W}x{t.H}").ToList();
                if (k > 0 && keys.All(covered.Contains)) continue;
                foreach (var key in keys) covered.Add(key);
                string d = Path.Combine(root, (nitro ? "n_" : "i_") + stem + (k > 0 ? $"_s{k}" : ""));
                Directory.CreateDirectory(d);
                if (!project) Views(plan, d, size, argv.Contains("--flat"), stem, ms.Vtfs[k]);
                else Project(plan, d, size, maps!, Opt("--sheets"), stem + (k > 0 ? $"_s{k}" : ""), claimed);
                Console.WriteLine($"{stem} scheme {k}: {plan.Cams.Count} views, {plan.Tex.Count} textures, {sw.Elapsed.TotalSeconds:F1}s");
            }
        }
        if (project)
        {
            using var fs = File.Create(outFile);
            maps!.Write(fs);
            Console.WriteLine($"wrote {outFile} ({maps.Entries.Count} maps, {new FileInfo(outFile).Length} bytes)");
        }
        return 0;
    }

    /// <summary>The car's triangles and textures under a paint scheme, and the cameras that see every face square-on.</summary>
    public static Plan Build(ModelSchemes ms, string vtf, TextureResolver res, int size)
    {
        var model = ms.Model;
        string code = model.ObjectCode ?? "";
        var tris = new List<CarTri>();
        var texIx = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var tex = new List<CarTexture>();
        foreach (var part in model.Parts)
        {
            if (!part.NodeName.StartsWith(code, StringComparison.OrdinalIgnoreCase)) continue;
            // the lamp cards (head lamp glow, tail and brake lamp sprites: ..HLGT, TLGT, BLGT) lie over the real lamps: not the car's surface
            if (part.NodeName.Length >= 8 && part.NodeName[4..8].ToUpperInvariant() is "HLGT" or "TLGT" or "BLGT") continue;
            foreach (var f in part.Mesh.Faces)
            {
                var vs = f.Vertices;
                if (vs.Length < 3 || vs.Any(v => (uint)v.Index >= part.Mesh.Vertices.Length)) continue;
                var pts = vs.Select(v => part.ToModel.Apply(part.Mesh.Vertices[v.Index])).ToArray();
                int ti = -1;
                if (f.Texture.Length > 0 && f.Unk_0x20 != 0 && ms.Resolved[vtf].TryGetValue(f.Texture, out var m))
                {
                    if (!texIx.TryGetValue(m, out ti))
                    {
                        var rgb = ResprayAnalysis.LoadRgb(res, m);
                        if (rgb is null) ti = -1;
                        else { ti = tex.Count; texIx[m] = ti; tex.Add(new CarTexture(m, rgb.W, rgb.H, rgb.Data, rgb.Key)); }
                    }
                }
                for (int k = 1; k + 1 < pts.Length; k++)
                    tris.Add(new CarTri(pts[0], pts[k], pts[k + 1], new(vs[0].U, vs[0].V), new(vs[k].U, vs[k].V), new(vs[k + 1].U, vs[k + 1].V), ti, (f.Unk_0x20 & 4) != 0, new Vector3(f.R, f.G, f.B)));
            }
        }
        var (centre, radius) = Bounds(tris);
        float refHalf = radius * 1.04f;
        return new Plan(tris, tex, Cameras(tris, tex.Count, centre, radius), refHalf, centre, radius);
    }

    private static (Vector3 C, float R) Bounds(List<CarTri> tris)
    {
        Vector3 lo = new(1e9f), hi = new(-1e9f);
        foreach (var t in tris) foreach (var p in new[] { t.A, t.B, t.C }) { lo = Vector3.Min(lo, p); hi = Vector3.Max(hi, p); }
        if (tris.Count == 0) return (Vector3.Zero, 1);
        var c = (lo + hi) / 2;
        float r = 0;
        foreach (var t in tris) foreach (var p in new[] { t.A, t.B, t.C }) r = MathF.Max(r, Vector3.Distance(p, c));
        return (c, MathF.Max(r, 1e-3f));
    }

    private static Vector3 Dir(float azDeg, float elDeg)
    {
        float az = azDeg * MathF.PI / 180, el = elDeg * MathF.PI / 180;
        return new Vector3(MathF.Sin(az) * MathF.Cos(el), MathF.Sin(el), MathF.Cos(az) * MathF.Cos(el));
    }

    private static readonly System.Text.RegularExpressions.Regex UndersideName = new(@"^..\d\d(ftu|mdu|bku)\d?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    // --denoise (with --raw): CarMaskCleaner.Denoise, paint against glass only; --refine-glass: CarMaskCleaner.RefineGlass, each window redrawn on the texture's colours
    public static bool Orbit, Raw, Denoise, Refine;
    private static readonly HashSet<string> OwnLayouts = new(StringComparer.OrdinalIgnoreCase) { "vxbus" };


    private static int GlassCheck(List<string> argv, AssetVfs vfs, TextureResolver res, string[] stems, string dir, bool nitro)
    {
        Directory.CreateDirectory(dir);
        Environment.SetEnvironmentVariable("I76_CAR_MASKS", "1");
        var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var stem in stems)
        {
            var ms = ModelSchemes.Load(vfs, stem);
            if (ms is null) continue;
            foreach (var vtf in ms.Vtfs)
            {
                var plan = Build(ms, vtf, res, 64);
                foreach (var t in plan.Tex)
                {
                    if (!done.Add(t.Member)) continue;
                    var rgba = new byte[t.W * t.H * 4];
                    for (int i = 0; i < t.W * t.H; i++) { rgba[i * 4] = t.Rgb[i * 3]; rgba[i * 4 + 1] = t.Rgb[i * 3 + 1]; rgba[i * 4 + 2] = t.Rgb[i * 3 + 2]; rgba[i * 4 + 3] = t.Key[i] ? (byte)0 : (byte)255; }
                    if (CarMaterialTable.Default.Ids(t.Member, rgba, t.W, t.H) is not { } ids || !ids.Contains((byte)11)) continue;
                    var mask = ids.Select(v => v == 11 ? (byte)255 : (byte)0).ToArray();
                    var loops = GlassShape.Shapes(mask, t.W, t.H);
                    const int k = 8;
                    int W = t.W * k, H = t.H * k;
                    var img = new byte[W * H * 3];
                    for (int y = 0; y < H; y++)
                        for (int x = 0; x < W; x++)
                        {
                            int ti = (y / k) * t.W + x / k, o = (y * W + x) * 3;
                            bool m = mask[ti] != 0;
                            for (int c = 0; c < 3; c++) img[o + c] = (byte)(m ? t.Rgb[ti * 3 + c] * 0.6f + (c == 2 ? 255 : c == 1 ? 200 : 40) * 0.4f : t.Rgb[ti * 3 + c]);
                        }
                    // the polygons: yellow edges, red corners
                    void Dot(float fx, float fy, byte r, byte g, byte b, int rad)
                    {
                        int cx = (int)(fx * k), cy = (int)(fy * k);
                        for (int dy = -rad; dy <= rad; dy++) for (int dx = -rad; dx <= rad; dx++)
                        {
                            int xx = cx + dx, yy = cy + dy;
                            if (xx < 0 || yy < 0 || xx >= W || yy >= H) continue;
                            int o = (yy * W + xx) * 3; img[o] = r; img[o + 1] = g; img[o + 2] = b;
                        }
                    }
                    foreach (var l in loops)
                        for (int i = 0; i < l.Count; i++)
                        {
                            var (ax, ay) = l[i]; var (bx, by) = l[(i + 1) % l.Count];
                            int n = (int)(MathF.Max(MathF.Abs(bx - ax), MathF.Abs(by - ay)) * k) + 1;
                            for (int j = 0; j <= n; j++) Dot(ax + (bx - ax) * j / n, ay + (by - ay) * j / n, 255, 230, 0, 0);
                            Dot(ax, ay, 230, 30, 30, 1);
                        }
                    IsectSurvey.WritePng(Path.Combine(dir, $"{(nitro ? "n_" : "i_")}{stem}_{CarMaterialTable.StemOf(t.Member)}.png"), img, W, H);
                }
            }
        }
        Console.WriteLine($"wrote {dir}");
        return 0;
    }
    public static CarMaskMap? CompareOld;
    public static string? CompareDir;

    // the texture (left) and it with the glass of each map tinted and outlined (middle: before, right: now), 4 texels a texel
    private static void GlassStrip(string path, CarTexture tex, byte[]? before, byte[] now)
    {
        const int k = 4;
        int w = tex.W, h = tex.H, W = (w * 3 + 2) * k, H = h * k;
        var img = new byte[W * H * 3];
        for (int panel = 0; panel < 3; panel++)
        {
            byte[]? m = panel == 0 ? null : panel == 1 ? before : now;
            bool G(int x, int y) => m is not null && x >= 0 && y >= 0 && x < w && y < h && m[y * w + x] == 11;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    float r = tex.Rgb[i * 3], g = tex.Rgb[i * 3 + 1], b = tex.Rgb[i * 3 + 2];
                    bool gl = G(x, y), edge = gl && (!G(x - 1, y) || !G(x + 1, y) || !G(x, y - 1) || !G(x, y + 1));
                    if (gl) { r = r * 0.45f + 40 * 0.55f; g = g * 0.45f + 220 * 0.55f; b = b * 0.45f + 255 * 0.55f; }
                    if (edge) { r = 255; g = 230; b = 0; }
                    for (int dy = 0; dy < k; dy++)
                        for (int dx = 0; dx < k; dx++)
                        {
                            int o = ((y * k + dy) * W + (panel * (w + 1)) * k + x * k + dx) * 3;
                            img[o] = (byte)r; img[o + 1] = (byte)g; img[o + 2] = (byte)b;
                        }
                }
        }
        IsectSurvey.WritePng(path, img, W, H);
    }
    public const int MaxViews = 56;
    private const float MergeDeg = 14;

    /// <summary>Base views all round, then per texture the square-on views of its face clusters (greedy by normal, 25°), framed on the cluster; similar views merged.</summary>
    private static List<CarCamera> Cameras(List<CarTri> tris, int ntex, Vector3 centre, float radius)
    {
        var cams = new List<CarCamera>();
        float full = radius * 1.04f;
        if (Orbit)
        {
            // like photographs of a car: all round at a slight downward angle, and a higher ring for the roof, the hood and the windshield
            for (int az = 0; az < 360; az += 20) cams.Add(new CarCamera(Dir(az, 15), centre, full));
            for (int az = 10; az < 360; az += 30) cams.Add(new CarCamera(Dir(az, 35), centre, full));
            return cams;
        }
        for (int az = 0; az < 360; az += 45) cams.Add(new CarCamera(Dir(az, 14), centre, full));
        for (int az = 45; az < 360; az += 90) cams.Add(new CarCamera(Dir(az, 52), centre, full));
        cams.Add(new CarCamera(Dir(0, 89), centre, full));
        cams.Add(new CarCamera(Dir(0, -89), centre, full));
        cams.Add(new CarCamera(Dir(0, -35), centre, full));
        cams.Add(new CarCamera(Dir(180, -35), centre, full));
        int nBase = cams.Count;
        var wanted = new List<(Vector3 Dir, Vector3 Lo, Vector3 Hi, float Area)>();
        for (int t = 0; t < ntex; t++)
        {
            var faces = tris.Where(x => x.Tex == t).Select(x => (Tri: x, N: Vector3.Cross(x.B - x.A, x.C - x.A))).Where(x => x.N.Length() > 1e-9f)
                .Select(x => (x.Tri, Area: x.N.Length() / 2, N: Vector3.Normalize(x.N))).OrderByDescending(x => x.Area).ToList();
            float total = faces.Sum(x => x.Area);
            var clusters = new List<(Vector3 Sum, List<(CarTri Tri, float Area, Vector3 N)> Faces)>();
            foreach (var f in faces)
            {
                // two-sided: a normal and its opposite are the same plane; orient to the car's outside
                var n = f.N;
                var mid = (f.Tri.A + f.Tri.B + f.Tri.C) / 3 - centre;
                if (Vector3.Dot(n, mid) < 0) n = -n;
                int hit = -1;
                for (int i = 0; i < clusters.Count; i++)
                    if (Vector3.Dot(Vector3.Normalize(clusters[i].Sum), n) > MathF.Cos(25 * MathF.PI / 180)) { hit = i; break; }
                if (hit < 0) { clusters.Add((n * f.Area, [(f.Tri, f.Area, n)])); }
                else { clusters[hit] = (clusters[hit].Sum + n * f.Area, clusters[hit].Faces); clusters[hit].Faces.Add((f.Tri, f.Area, n)); }
            }
            foreach (var c in clusters.Where(c => c.Faces.Sum(x => x.Area) >= 0.08f * total))
            {
                var dir = Vector3.Normalize(c.Sum);
                var cam0 = new CarCamera(dir, Vector3.Zero, 1);
                float x0 = 1e9f, x1 = -1e9f, y0 = 1e9f, y1 = -1e9f, zc = 0; int nz = 0;
                foreach (var f in c.Faces) foreach (var p in new[] { f.Tri.A, f.Tri.B, f.Tri.C })
                {
                    var q = p - centre;
                    float px = Vector3.Dot(q, cam0.Right), py = Vector3.Dot(q, cam0.Up);
                    x0 = MathF.Min(x0, px); x1 = MathF.Max(x1, px); y0 = MathF.Min(y0, py); y1 = MathF.Max(y1, py); zc += 0; nz++;
                }
                var ctr = centre + cam0.Right * ((x0 + x1) / 2) + cam0.Up * ((y0 + y1) / 2);
                float half = MathF.Max(MathF.Max(x1 - x0, y1 - y0) / 2 * 1.25f, radius * 0.07f);
                half = MathF.Min(half, full);
                wanted.Add((dir, ctr, Vector3.Zero, half));
            }
        }
        // merge: a wanted view within MergeDeg of an existing one whose frame holds its centre joins it (frame grows to hold both if it stays modest)
        foreach (var w in wanted.OrderByDescending(w => w.Area))
        {
            int hit = -1;
            for (int i = nBase; i < cams.Count && hit < 0; i++)
            {
                var c = cams[i];
                if (Vector3.Dot(Vector3.Normalize(c.ToCamera), w.Dir) < MathF.Cos(MergeDeg * MathF.PI / 180)) continue;
                var d = w.Lo - c.Centre;
                float dx = Vector3.Dot(d, c.Right), dy = Vector3.Dot(d, c.Up);
                float need = MathF.Max(MathF.Abs(dx), MathF.Abs(dy)) + w.Area;
                if (need <= MathF.Max(c.Half, w.Area) * 1.6f && need <= full) hit = i;
            }
            if (hit >= 0)
            {
                var c = cams[hit];
                var d = w.Lo - c.Centre;
                float dx = Vector3.Dot(d, c.Right), dy = Vector3.Dot(d, c.Up);
                float lo = MathF.Min(-c.Half, MathF.Min(dx - w.Area, dy - w.Area)), hi = MathF.Max(c.Half, MathF.Max(dx + w.Area, dy + w.Area));
                // keep it square: grow the half-width about the shared centre
                cams[hit] = c with { Half = MathF.Min(full, MathF.Max(c.Half, MathF.Max(MathF.Abs(dx), MathF.Abs(dy)) + w.Area)) };
                _ = lo; _ = hi;
            }
            else if (cams.Count < MaxViews) cams.Add(new CarCamera(w.Dir, w.Lo, w.Area));
            else
            {
                // over budget: widen the nearest direction instead
                int best = Enumerable.Range(nBase, cams.Count - nBase).DefaultIfEmpty(0).MaxBy(i => Vector3.Dot(Vector3.Normalize(cams[i].ToCamera), w.Dir));
                var c = cams[best];
                var d = w.Lo - c.Centre;
                cams[best] = c with { Half = MathF.Min(full, MathF.Max(c.Half, MathF.Max(MathF.Abs(Vector3.Dot(d, c.Right)), MathF.Abs(Vector3.Dot(d, c.Up))) + w.Area)) };
            }
        }
        return cams;
    }

    private static void Views(Plan plan, string dir, int size, bool flat, string stem, string vtf)
    {
        // the car and paint scheme, for the game's render of the same cameras (Garage3DScreen's I76_GARAGE_SAMVIEWS)
        File.WriteAllText(Path.Combine(dir, "car.txt"), $"{stem}\t{vtf}\n");
        var sb = new StringBuilder();
        var seen = plan.Tex.Select(t => new bool[t.W * t.H]).ToArray();
        var used = plan.Tex.Select(t => new bool[t.W * t.H]).ToArray();
        UsedTexels(plan, used);
        for (int i = 0; i < plan.Cams.Count; i++)
        {
            var c = plan.Cams[i];
            var v = CarView.Render(plan.Tris, plan.Tex, c, plan.RefHalf, size, !flat);
            WritePpm(Path.Combine(dir, $"view_{i:00}.ppm"), v.Rgb, size);
            // the silhouette (pixels showing a textured face), to line a game render of the same camera up against
            File.WriteAllBytes(Path.Combine(dir, $"view_{i:00}.sil.pgm"), [.. Encoding.ASCII.GetBytes($"P5\n{size} {size}\n255\n"), .. v.Tex.Select(x => x >= 0 ? (byte)255 : (byte)0)]);
            for (int p = 0; p < v.Tex.Length; p++) if (v.Tex[p] >= 0 && v.Q[p] > 0) seen[v.Tex[p]][v.Ty[p] * plan.Tex[v.Tex[p]].W + v.Tx[p]] = true;
            sb.AppendLine(string.Join('\t', i, F(c.ToCamera.X), F(c.ToCamera.Y), F(c.ToCamera.Z), F(c.Centre.X), F(c.Centre.Y), F(c.Centre.Z), F(c.Half)));
        }
        File.WriteAllText(Path.Combine(dir, "views.tsv"), sb.ToString());
        var tb = new StringBuilder();
        long u = 0, un = 0;
        for (int t = 0; t < plan.Tex.Count; t++)
        {
            int ut = 0, unseen = 0;
            for (int i = 0; i < used[t].Length; i++) if (used[t][i]) { ut++; if (!seen[t][i]) unseen++; }
            u += ut; un += unseen;
            tb.AppendLine($"{t}\t{plan.Tex[t].Member}\t{plan.Tex[t].W}x{plan.Tex[t].H}\tused={ut}\tunseen={unseen}");
        }
        File.WriteAllText(Path.Combine(dir, "textures.tsv"), tb.ToString());
        Console.WriteLine($"  {stem}: {un} of {u} used texels unseen by pixel sampling ({(u == 0 ? 0 : 100.0 * un / u):F1}%; the projector's hole fill closes the sampling gaps)");
    }

    private static string F(float f) => f.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>Marks every texel a face's UV triangle covers.</summary>
    public static void UsedTexels(Plan plan, bool[][] used)
    {
        foreach (var t in plan.Tris)
        {
            if (t.Tex < 0) continue;
            var tx = plan.Tex[t.Tex];
            Vector2 A = new(t.Ua.X * tx.W, t.Ua.Y * tx.H), B = new(t.Ub.X * tx.W, t.Ub.Y * tx.H), C = new(t.Uc.X * tx.W, t.Uc.Y * tx.H);
            int x0 = (int)MathF.Floor(MathF.Min(A.X, MathF.Min(B.X, C.X))), x1 = (int)MathF.Ceiling(MathF.Max(A.X, MathF.Max(B.X, C.X)));
            int y0 = (int)MathF.Floor(MathF.Min(A.Y, MathF.Min(B.Y, C.Y))), y1 = (int)MathF.Ceiling(MathF.Max(A.Y, MathF.Max(B.Y, C.Y)));
            if ((long)(x1 - x0) * (y1 - y0) > 4_000_000) continue;
            float den = (B.Y - C.Y) * (A.X - C.X) + (C.X - B.X) * (A.Y - C.Y);
            if (MathF.Abs(den) < 1e-9f) continue;
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    float px = x + 0.5f, py = y + 0.5f;
                    float l1 = ((B.Y - C.Y) * (px - C.X) + (C.X - B.X) * (py - C.Y)) / den, l2 = ((C.Y - A.Y) * (px - C.X) + (A.X - C.X) * (py - C.Y)) / den, l3 = 1 - l1 - l2;
                    if (l1 < 0 || l2 < 0 || l3 < 0) continue;
                    int xm = ((x % tx.W) + tx.W) % tx.W, ym = ((y % tx.H) + tx.H) % tx.H;
                    if (t.Cut && tx.Key[ym * tx.W + xm]) continue;
                    used[t.Tex][ym * tx.W + xm] = true;
                }
        }
    }

    private static void WritePpm(string path, byte[] rgb, int size)
        => File.WriteAllBytes(path, [.. Encoding.ASCII.GetBytes($"P6 {size} {size} 255\n"), .. rgb]);

    private static byte[]? ReadPgm(string path, int size)
    {
        if (!File.Exists(path)) return null;
        var b = File.ReadAllBytes(path);
        // "P5\nW H\n255\n" then W*H bytes
        int pos = 0, nl = 0;
        while (nl < 3 && pos < b.Length) if (b[pos++] == (byte)'\n') nl++;
        return b.Length - pos == size * size ? b[pos..] : null;
    }

    private static void Project(Plan plan, string dir, int size, CarMaskMap maps, string? sheets, string stem, HashSet<string> claimed)
    {
        var proj = new CarMaskProjector(plan.Tex.Select(t => (t.W, t.H)).ToList(), CarMaterialTable.Presets.Count + 1);
        var views = new List<(CarView V, byte[] Lab)>();
        for (int i = 0; i < plan.Cams.Count; i++)
        {
            var lab = ReadPgm(Path.Combine(dir, $"view_{i:00}.lab.pgm"), size);
            if (lab is null) { Console.WriteLine($"  {stem}: view {i} has no labels"); continue; }
            var v = CarView.Render(plan.Tris, plan.Tex, plan.Cams[i], plan.RefHalf, size, true);
            // view_NN.ign.pgm: pixels to leave out (the wheels in a game render, which the ID pass does not have)
            if (ReadPgm(Path.Combine(dir, $"view_{i:00}.ign.pgm"), size) is { } ign)
                for (int p = 0; p < ign.Length; p++) if (ign[p] != 0) v.Tex[p] = -1;
            proj.Seen(v, Vector3.Normalize(plan.Cams[i].ToCamera).Y); views.Add((v, lab));
        }
        foreach (var (v, lab) in views) proj.Vote(v, lab);
        var used = plan.Tex.Select(t => new bool[t.W * t.H]).ToArray();
        UsedTexels(plan, used);
        var hist = new long[CarMaterialTable.Presets.Count + 1];
        long unseenUsed = 0, usedN = 0;
        var sheetItems = new List<(string, byte[], byte[], int, int)>();
        var paintedTex = new List<CarTexture>();
        for (int t = 0; t < plan.Tex.Count; t++)
        {
            var lab = proj.Resolve(t, clean: !Raw);
            byte[] ids;
            if (!Raw) {
            // the underside textures (front, middle, back: ??11FTU?, MDU?, BKU?) are chassis steel whole, by name
            if (UndersideName.IsMatch(CarMaterialTable.StemOf(plan.Tex[t].Member))) Array.Fill(lab, CarMaskProjector.UndersideClass);
            ids = new byte[lab.Length];
            // sanity: painted glass is mid-tone (blue-grey sky and tint); near-white (a door badge) or near-black (a stripe) texels called glass are paint
            for (int i = 0; i < lab.Length; i++)
                if (lab[i] == 11 && (CarMaterialTable.InClass("light", plan.Tex[t].Rgb[i * 3], plan.Tex[t].Rgb[i * 3 + 1], plan.Tex[t].Rgb[i * 3 + 2]) || CarMaterialTable.InClass("black", plan.Tex[t].Rgb[i * 3], plan.Tex[t].Rgb[i * 3 + 1], plan.Tex[t].Rgb[i * 3 + 2]))) lab[i] = 1;
            for (int i = 0; i < ids.Length; i++) ids[i] = lab[i] == 255 ? (byte)0 : lab[i] == 0 ? (byte)1 : lab[i];
            bool dbg = Environment.GetEnvironmentVariable("I76_MASK_DEBUG") == stem;
            ids = CarMaskCleaner.Clean(ids, plan.Tex[t].Rgb, plan.Tex[t].W, plan.Tex[t].H, dbg
                ? (r, why) => Console.WriteLine($"    {CarMaterialTable.StemOf(plan.Tex[t].Member)} {CarMaterialTable.Presets[r.Class - 1].Name} area={r.Area} box={r.X0},{r.Y0}-{r.X1},{r.Y1} solid={r.Solidity:F2} luma={r.MeanLuma:F2} std={r.LumaStd:F2} sat={r.MeanSat:F2} -> {why}")
                : null);
            } else { ids = new byte[lab.Length]; for (int i = 0; i < ids.Length; i++) ids[i] = lab[i] == 255 ? (byte)0 : lab[i] == 0 ? (byte)1 : lab[i]; if (Denoise) ids = CarMaskCleaner.Denoise(ids, plan.Tex[t].W, plan.Tex[t].H); if (Refine) ids = CarMaskCleaner.RefineGlass(ids, plan.Tex[t].Rgb, plan.Tex[t].W, plan.Tex[t].H); }
            for (int i = 0; i < ids.Length; i++)
                if (used[t][i]) { usedN++; if (ids[i] == 0) unseenUsed++; else hist[ids[i]]++; }
            {
                var fcp = CarMaterialTable.FalseColour(ids.Select(i => i == 0 ? (byte)1 : i).ToArray());
                var rgb = new byte[fcp.Length];
                for (int i = 0; i < ids.Length; i++) { float l = (0.299f * plan.Tex[t].Rgb[i * 3] + 0.587f * plan.Tex[t].Rgb[i * 3 + 1] + 0.114f * plan.Tex[t].Rgb[i * 3 + 2]) / 255f * 0.5f + 0.5f; for (int c = 0; c < 3; c++) rgb[i * 3 + c] = (byte)(fcp[i * 3 + c] * l); }
                paintedTex.Add(plan.Tex[t] with { Rgb = rgb });
            }
            string key = CarMaskMap.KeyOf(plan.Tex[t].Member);
            // an entry per layout (scheme digit ?): the first texture to claim it keeps it, so every scheme of a car has the same windows;
            // a later scheme's texture of another size (another layout: the bus's Godolphin Lines coach) gets its own (exact stem)
            // (the bus's two schemes are two vehicles, the school bus and the Godolphin Lines coach: every one of its textures is its own)
            if (claimed.Add(key) && !OwnLayouts.Contains(stem.Split('_')[0]))
                maps.Add(new CarMaskMap.Entry(key, plan.Tex[t].W, plan.Tex[t].H, ids));
            else if (OwnLayouts.Contains(stem.Split('_')[0]) || maps.Ids(plan.Tex[t].Member, plan.Tex[t].W, plan.Tex[t].H) is null)
                maps.Add(new CarMaskMap.Entry(CarMaterialTable.StemOf(plan.Tex[t].Member), plan.Tex[t].W, plan.Tex[t].H, ids));
            // --compare OLD.bin DIR: the texture, its glass in OLD and its glass now, side by side (4x), for every texture with glass in either
            if (CompareOld is not null && CompareDir is not null)
            {
                var before = CompareOld.Ids(plan.Tex[t].Member, plan.Tex[t].W, plan.Tex[t].H);
                if (ids.Contains((byte)11) || (before?.Contains((byte)11) ?? false))
                    GlassStrip(Path.Combine(CompareDir, $"{stem}_{CarMaterialTable.StemOf(plan.Tex[t].Member)}.png"), plan.Tex[t], before, ids);
            }
            if (sheets is not null)
            {
                var fc = CarMaterialTable.FalseColour(ids.Select(i => i == 0 ? (byte)1 : i).ToArray());
                var rgba = new byte[ids.Length * 4];
                for (int i = 0; i < ids.Length; i++) { rgba[i * 4] = plan.Tex[t].Rgb[i * 3]; rgba[i * 4 + 1] = plan.Tex[t].Rgb[i * 3 + 1]; rgba[i * 4 + 2] = plan.Tex[t].Rgb[i * 3 + 2]; rgba[i * 4 + 3] = plan.Tex[t].Key[i] ? (byte)0 : (byte)255; }
                for (int i = 0; i < ids.Length; i++) if (!used[t][i]) { fc[i * 3] = fc[i * 3 + 1] = fc[i * 3 + 2] = 0; }
                sheetItems.Add((CarMaterialTable.StemOf(plan.Tex[t].Member), rgba, fc, plan.Tex[t].W, plan.Tex[t].H));
            }
        }
        var sb = new StringBuilder($"  {stem}: used texels {usedN}, unseen {unseenUsed}; ");
        for (int i = 1; i < hist.Length; i++) if (hist[i] > 0) sb.Append($"{CarMaterialTable.Presets[i - 1].Name} {100.0 * hist[i] / Math.Max(1, usedN):F1}%  ");
        Console.WriteLine(sb);
        if (sheets is not null)
        {
            Directory.CreateDirectory(sheets);
            // four views of the car in its material colours, for the one-glance review
            var tmp = Path.Combine(Path.GetTempPath(), "carprev_" + stem); Directory.CreateDirectory(tmp);
            var files = new List<string>();
            int k = 0;
            foreach (var (az, el) in new[] { (35f, 22f), (145f, 22f), (215f, 22f), (325f, 22f) })
            {
                var pv = CarView.Render(plan.Tris, paintedTex, new CarCamera(Dir(az, el), plan.Centre, plan.RefHalf), plan.RefHalf, 480, true);
                string f = Path.Combine(tmp, $"{k++}.ppm"); WritePpm(f, pv.Rgb, 480); files.Add(f);
            }
            var pr = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("magick", $"montage {string.Join(" ", files.Select(f => $"\"{f}\""))} -tile 4x -geometry +1+1 -background \"#202020\" \"{Path.Combine(sheets, stem + "_preview.png")}\"") { UseShellExecute = false })!;
            pr.WaitForExit(); Directory.Delete(tmp, true);
        }
        if (sheets is not null) { Directory.CreateDirectory(sheets); CarTableCommand.WriteSheet(Path.Combine(sheets, $"{stem}_textures.png"), sheetItems, 4); }
    }
}
