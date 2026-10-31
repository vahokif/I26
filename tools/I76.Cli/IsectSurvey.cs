using System.IO.Compression;
using System.Numerics;
using I76.Formats.Assembly;
using I76.Formats.Mission;
using I76.Formats.Mission.Edit;
using I76.Formats.Vfs;

namespace I76.Cli;

/// <summary>
/// i76 isect models [OUTDIR]: the stock intersection pieces' geometry (their upward faces in the model frame, textures, bounds) and a PNG of each.
/// i76 isect survey: every stock placement of a road piece (ODEF type 0x5x) and the road points around it, in the piece's own frame.
/// The numbers behind docs/map-format-writing.md, "Intersections". Writes no game data.
/// </summary>
public static class IsectSurvey
{
    public static readonly string[] Names = ["ica21_1", "ica22_1", "ica23_1", "idisect1", "idisect2", "ieisect1", "iga21_1", "iga21_2", "iga22_1", "iga22_2", "iga23_1", "iga23_2", "igd27_1", "igw29_1", "ija2171", "ija2271", "ija2371", "ijd27t1", "ijd28t1", "ijw29t1", "ila21_1", "ila21_2", "ila22_1", "ila22_2", "ila23_1", "ila23_2", "ild27_1", "ilw29_1", "ima21_1", "ima21_2", "ima22_1", "ima22_2", "ima23_1", "ima23_2", "ita21_1", "ita22_1", "ita23_1", "itd27_1", "itisect1", "itisect2", "ixa21_1", "ixa22_1", "ixa23_1", "ixd27_1", "ixisect1", "ixisect2", "iyd27_1", "iyisect1", "iyisect2", "iyisect3", "iyisect4", "iyisect5", "iyisect6", "iyisect8", "rerwend1", "rerwend2", "rerwend3", "rerwend4", "rmrwmid1"];

    public static int Run(GameInstall install, List<string> argv)
    {
        string mode = argv.Count > 1 ? argv[1] : "models";
        return mode switch { "models" => Models(install, argv.Count > 2 ? argv[2] : null), "survey" => Survey(install), "surface" => Surface(install), "payload" => Payload(install, argv[2]), "strip" => Strip(argv[2], argv[3], argv[4]), "spawn" => Spawn(argv), _ => 2 };
    }

    /// <summary>The upward faces (outward is -plane) of a model in its own frame: polygons with their texture.</summary>
    public static List<(Vector3[] V, string Tex, int Part)> Tops(AssembledModel m)
    {
        var o = new List<(Vector3[], string, int)>();
        int pi = 0;
        foreach (var part in m.Parts)
        {
            pi++;
            foreach (var f in part.Mesh.Faces)
            {
                if (f.Vertices.Length < 3 || f.Vertices.Any(v => (uint)v.Index >= part.Mesh.Vertices.Length)) continue;
                var n = part.ToModel.Rotate(new Vector3(f.Plane.X, f.Plane.Y, f.Plane.Z));
                if (n.LengthSquared() < 0.5f || -n.Y / n.Length() < 0.6f) continue;
                o.Add((f.Vertices.Select(v => part.ToModel.Apply(part.Mesh.Vertices[v.Index])).ToArray(), f.Texture.Length > 0 ? f.Texture : $"#{f.R:x2}{f.G:x2}{f.B:x2}", pi));
            }
        }
        return o;
    }

    private static int Models(GameInstall install, string? outDir)
    {
        var vfs = new AssetVfs(install);
        var asm = new ModelAssembler(vfs);
        if (outDir is not null) Directory.CreateDirectory(outDir);
        foreach (var name in Names)
        {
            string? def = asm.ResolveDefinition(name);
            if (def is null || asm.Assemble(def) is not { } m) { Console.WriteLine($"{name}: not found"); continue; }
            var tops = Tops(m);
            var all = tops.SelectMany(t => t.V).ToArray();
            int faces = m.Parts.Sum(p => p.Mesh.Faces.Length);
            if (all.Length == 0) { Console.WriteLine($"{name}: {def}, {faces} faces, no upward faces"); continue; }
            var min = new Vector3(all.Min(v => v.X), all.Min(v => v.Y), all.Min(v => v.Z)); var max = new Vector3(all.Max(v => v.X), all.Max(v => v.Y), all.Max(v => v.Z));
            Console.WriteLine($"{name}: {def}, {m.Parts.Count} parts, {faces} faces, {tops.Count} upward faces; top x[{min.X:0.0},{max.X:0.0}] y[{min.Y:0.00},{max.Y:0.00}] z[{min.Z:0.0},{max.Z:0.0}]; textures {string.Join(",", tops.Select(t => t.Tex).Distinct())}");
            if (outDir is null && Environment.GetEnvironmentVariable("ISECT_VERTS") == "1")
                foreach (var t in tops) Console.WriteLine("   " + string.Join(" ", t.V.Select(v => $"({v.X:0.0},{v.Z:0.0})")));
            if (outDir is not null) Png(Path.Combine(outDir, name + ".png"), tops.Select(t => t.V).ToList(), tops.Select(t => t.Tex).ToList(), min, max, []);
        }
        return 0;
    }

    /// <summary>The rows of one placement: its roads' points near it, in its own frame (x across the model, z along it), as (road, point index, x, z, y above the piece's origin).</summary>
    public sealed record Near(int Road, uint Kind, int Count, int Index, Vector2 Local, Vector2 Dir);

    public static IEnumerable<(string File, MapObject Obj, MapDocument Doc)> Placements(GameInstall install, Func<MapObject, bool> pick)
    {
        foreach (var d in new[] { install.MissionDir, install.AddonDir }.Where(d => d is not null).Select(d => d!).Distinct())
            foreach (var f in Directory.EnumerateFiles(d).Where(GameInstall.IsMissionFile).Order())
            {
                MapDocument doc;
                try { doc = MapDocument.Load(f); } catch { continue; }
                foreach (var o in doc.Objects.Where(pick)) yield return (f, o, doc);
            }
    }

    public static Func<Vector2, Vector2> ToLocal(MapObject o)
    {
        var a = Affine.FromEngine(o.Rotation, o.Position);
        var org = a.Apply(Vector3.Zero);
        var ex = a.Apply(new Vector3(1, 0, 0)) - org; var ez = a.Apply(new Vector3(0, 0, 1)) - org;
        return p => new Vector2((p.X - org.X) * ex.X + (p.Y - org.Z) * ex.Z, (p.X - org.X) * ez.X + (p.Y - org.Z) * ez.Z);
    }

    private static int Survey(GameInstall install)
    {
        var vfs = new AssetVfs(install);
        var asm = new ModelAssembler(vfs);
        Console.WriteLine("file\tname\ttype\tflags\tx\tz\tyaw\troadsNear");
        foreach (var (f, o, doc) in Placements(install, o => (o.ObjectType & 0xf0) == 0x50))
        {
            var loc = ToLocal(o);
            var rows = new List<string>();
            for (int r = 0; r < doc.Roads.Count; r++)
            {
                var pts = doc.Roads[r].Points;
                for (int i = 0; i < pts.Count; i++)
                {
                    var l = loc(pts[i].Centre);
                    if (l.Length() > 45) continue;
                    var dir = i + 1 < pts.Count ? pts[i + 1].Centre - pts[i].Centre : pts[i].Centre - pts[i - 1].Centre;
                    var ld = loc(pts[i].Centre + dir) - l;
                    bool end = i == 0 || i == pts.Count - 1;
                    rows.Add($"r{r}k{doc.Roads[r].Kind}{(end ? (i == 0 ? "S" : "E") : "")}#{i}/{pts.Count}({l.X:0.0},{l.Y:0.0})>{MathF.Atan2(ld.X, ld.Y) * 180 / MathF.PI:0}");
                }
            }
            Console.WriteLine($"{Path.GetFileName(f)}\t{o.Name}\t{o.ObjectType:x}\t{o.ObjectFlags:x}\t{o.Position.X:0.0}\t{o.Position.Z:0.0}\t{o.Yaw * 180 / MathF.PI:0.0}\t{string.Join(" ", rows)}");
        }
        return 0;
    }

    /// <summary>i76 isect spawn IN OUT x z yawDegrees: a copy of a mission with its first spawn moved (for a capture of a place in the real game, which cannot be teleported to on Nitro).</summary>
    private static int Spawn(List<string> a)
    {
        var doc = MapDocument.Load(a[2]);
        var o = doc.Objects.First(o => o.Name.Equals("spawn", StringComparison.OrdinalIgnoreCase));
        var c = System.Globalization.CultureInfo.InvariantCulture;
        o.Position = new Vector3(float.Parse(a[4], c), o.Position.Y, float.Parse(a[5], c));
        o.Yaw = float.Parse(a[6], c) * MathF.PI / 180;
        File.WriteAllBytes(a[3], doc.ToBytes());
        return 0;
    }

    /// <summary>i76 isect strip IN OUT pieces|roads: a copy of a mission without its road pieces, or without its roads (to find what a game dislikes).</summary>
    private static int Strip(string input, string output, string what)
    {
        var doc = MapDocument.Load(input);
        if (what == "pieces") doc.Objects.RemoveAll(o => (o.ObjectType & 0xf0) == 0x50);
        else doc.Roads.Clear();
        File.WriteAllBytes(output, doc.ToBytes());
        return 0;
    }

    /// <summary>The ODEF records of every placement of a name, as hex (the unmapped bytes of a piece, to compare with a made one).</summary>
    private static int Payload(GameInstall install, string name)
    {
        foreach (var (f, o, _) in Placements(install, o => o.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Take(6))
            Console.WriteLine($"{Path.GetFileName(f)} {Convert.ToHexString(o.Payload)}");
        return 0;
    }

    /// <summary>The ground's surface nibble (the .ter's bits 12-15) under the stock pieces' centres and under road points, by piece family and road kind.</summary>
    private static int Surface(GameInstall install)
    {
        var piece = new Dictionary<string, SortedDictionary<int, int>>();
        var road = new Dictionary<uint, SortedDictionary<int, int>>();
        var vfs = new AssetVfs(install);
        foreach (var d in new[] { install.MissionDir, install.AddonDir }.Where(d => d is not null).Select(d => d!).Distinct())
            foreach (var f in Directory.EnumerateFiles(d).Where(GameInstall.IsMissionFile).Order())
            {
                MapProject proj;
                try { proj = MapProject.Load(f, install); } catch { continue; }
                int Nib(float x, float z) => (proj.Terrain.Sample(MissionWorld.ToSample(x), MissionWorld.ToSample(z)) ?? 0) >> 12;
                foreach (var o in proj.Mission.Objects.Where(o => (o.ObjectType & 0xf0) == 0x50))
                {
                    var loc = ToLocal(o);
                    var a = Affine.FromEngine(o.Rotation, o.Position);
                    var c = a.Apply(new Vector3(5, 0, 5));
                    string fam = o.Name.ToLowerInvariant()[..2] + o.Name.ToLowerInvariant()[2];
                    if (!piece.TryGetValue(fam, out var h)) piece[fam] = h = [];
                    int n = Nib(c.X, c.Z); h[n] = h.GetValueOrDefault(n) + 1;
                    // every sample (5 m apart) inside the 10 x 10 footprint of a T or an X: which nibble
                    if (o.Name.ToLowerInvariant() is var nm && (nm.StartsWith("ita") || nm.StartsWith("ixa") || nm.StartsWith("itisect1") || nm.StartsWith("ixisect1")))
                        for (float lx = 0; lx <= 10; lx += 0.5f) for (float lz = 0; lz <= 10; lz += 0.5f)
                        {
                            var w = a.Apply(new Vector3(lx, 0, lz)); int nb = Nib(w.X, w.Z);
                            if (!piece.TryGetValue("inside-T/X-asphalt", out var hh)) piece["inside-T/X-asphalt"] = hh = [];
                            hh[nb] = hh.GetValueOrDefault(nb) + 1;
                        }
                }
                foreach (var r in proj.Mission.Roads)
                    foreach (var p in r.Points)
                    {
                        if (!road.TryGetValue(r.Kind % 3, out var h)) road[r.Kind % 3] = h = [];
                        int n = Nib(p.Centre.X, p.Centre.Y); h[n] = h.GetValueOrDefault(n) + 1;
                    }
            }
        foreach (var (k, h) in piece.OrderBy(k => k.Key)) Console.WriteLine($"piece {k}: " + string.Join(" ", h.Select(e => $"{e.Key:x}:{e.Value}")));
        foreach (var (k, h) in road.OrderBy(k => k.Key)) Console.WriteLine($"road kind {k}: " + string.Join(" ", h.Select(e => $"{e.Key:x}:{e.Value}")));
        return 0;
    }

    /// <summary>A PNG of polygons (x right, z up) over bounds, 4 px a metre, with extra points as red dots.</summary>
    public static void Png(string path, List<Vector3[]> polys, List<string> tex, Vector3 min, Vector3 max, List<Vector2> dots, float scale = 4f)
    {
        int pad = 12, w = (int)((max.X - min.X) * scale) + 2 * pad, h = (int)((max.Z - min.Z) * scale) + 2 * pad;
        var img = new byte[w * h * 3];
        Array.Fill(img, (byte)40);
        (int, int) P(float x, float z) => ((int)((x - min.X) * scale) + pad, h - 1 - ((int)((z - min.Z) * scale) + pad));
        for (int k = 0; k < polys.Count; k++)
        {
            int hash = tex[k].GetHashCode(); var col = new[] { (byte)(90 + (hash & 0x7f)), (byte)(90 + ((hash >> 8) & 0x7f)), (byte)(90 + ((hash >> 16) & 0x7f)) };
            var v = polys[k];
            for (int t = 1; t + 1 < v.Length; t++)
                Fill(img, w, h, P(v[0].X, v[0].Z), P(v[t].X, v[t].Z), P(v[t + 1].X, v[t + 1].Z), col);
            for (int t = 0; t < v.Length; t++) Line(img, w, h, P(v[t].X, v[t].Z), P(v[(t + 1) % v.Length].X, v[(t + 1) % v.Length].Z), [255, 255, 255]);
        }
        // the grid: a metre cross every 10 m, the origin large
        for (float x = MathF.Ceiling(min.X / 10) * 10; x <= max.X; x += 10) for (float z = MathF.Ceiling(min.Z / 10) * 10; z <= max.Z; z += 10) { var (px, pz) = P(x, z); Dot(img, w, h, px, pz, x == 0 && z == 0 ? 3 : 1, [255, 255, 0]); }
        foreach (var d in dots) { var (px, pz) = P(d.X, d.Y); Dot(img, w, h, px, pz, 3, [255, 0, 0]); }
        WritePng(path, img, w, h);
    }

    private static void Dot(byte[] img, int w, int h, int x, int y, int r, byte[] c)
    {
        for (int j = -r; j <= r; j++) for (int i = -r; i <= r; i++) { int xx = x + i, yy = y + j; if (xx >= 0 && yy >= 0 && xx < w && yy < h) { img[(yy * w + xx) * 3] = c[0]; img[(yy * w + xx) * 3 + 1] = c[1]; img[(yy * w + xx) * 3 + 2] = c[2]; } }
    }

    private static void Line(byte[] img, int w, int h, (int X, int Y) a, (int X, int Y) b, byte[] c)
    {
        int n = Math.Max(Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y)); 
        for (int i = 0; i <= n; i++) Dot(img, w, h, a.X + (b.X - a.X) * i / Math.Max(1, n), a.Y + (b.Y - a.Y) * i / Math.Max(1, n), 0, c);
    }

    private static void Fill(byte[] img, int w, int h, (int X, int Y) a, (int X, int Y) b, (int X, int Y) c, byte[] col)
    {
        int x0 = Math.Max(0, Math.Min(a.X, Math.Min(b.X, c.X))), x1 = Math.Min(w - 1, Math.Max(a.X, Math.Max(b.X, c.X)));
        int y0 = Math.Max(0, Math.Min(a.Y, Math.Min(b.Y, c.Y))), y1 = Math.Min(h - 1, Math.Max(a.Y, Math.Max(b.Y, c.Y)));
        long E((int X, int Y) p, (int X, int Y) q, int x, int y) => (long)(q.X - p.X) * (y - p.Y) - (long)(q.Y - p.Y) * (x - p.X);
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                long e0 = E(a, b, x, y), e1 = E(b, c, x, y), e2 = E(c, a, x, y);
                if ((e0 >= 0 && e1 >= 0 && e2 >= 0) || (e0 <= 0 && e1 <= 0 && e2 <= 0)) { int o = (y * w + x) * 3; img[o] = col[0]; img[o + 1] = col[1]; img[o + 2] = col[2]; }
            }
    }

    public static void WritePng(string path, byte[] rgb, int w, int h)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Fastest, true))
            for (int y = 0; y < h; y++) { z.WriteByte(0); z.Write(rgb, y * w * 3, w * 3); }
        using var fs = File.Create(path);
        fs.Write([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);
        void Chunk(string type, byte[] data)
        {
            var len = new byte[4]; System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(len, (uint)data.Length); fs.Write(len);
            var td = System.Text.Encoding.ASCII.GetBytes(type).Concat(data).ToArray(); fs.Write(td);
            uint crc = 0xffffffff; foreach (var b in td) { crc ^= b; for (int k = 0; k < 8; k++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xedb88320 : crc >> 1; }
            var cb = new byte[4]; System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(cb, ~crc); fs.Write(cb);
        }
        var ihdr = new byte[13]; System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(ihdr, (uint)w); System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), (uint)h); ihdr[8] = 8; ihdr[9] = 2;
        Chunk("IHDR", ihdr); Chunk("IDAT", ms.ToArray()); Chunk("IEND", []);
    }
}
