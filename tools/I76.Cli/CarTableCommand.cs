using I76.Formats.Assembly;
using I76.Formats.Imaging;
using I76.Formats.Vfs;

namespace I76.Cli;

/// <summary>
/// The car material table's authoring and review tools (CarMaterials.tsv):
///   i76 car-table survey [--stems a,b] [--nitro]          every texture each model's paint schemes resolve to, size and scheme count
///   i76 car-table sheet OUT_DIR [--stems a,b] [--nitro] [--scheme N] [--scale S]   per model: its textures (a grid of 8 texels, labelled) beside the table's false-colour map
/// </summary>
public static class CarTableCommand
{
    public static int Run(List<string> argv, string? installDir)
    {
        string? Opt(string n) { int i = argv.IndexOf(n); return i >= 0 && i + 1 < argv.Count ? argv[i + 1] : null; }
        bool nitro = argv.Contains("--nitro");
        string? dir = nitro ? Environment.GetEnvironmentVariable("I76_NITRO_INSTALL") : installDir;
        if (dir is null) { Console.Error.WriteLine("set I76_INSTALL / I76_NITRO_INSTALL"); return 2; }
        var vfs = new AssetVfs(new GameInstall(dir));
        var res = new TextureResolver(vfs);
        var stems = Opt("--stems")?.Split(',') ?? (nitro ? ResprayCommand.NitroStems : ResprayCommand.BaseStems);
        var table = CarMaterialTable.Default;
        string mode = argv.Count > 1 ? argv[1] : "survey";
        string outDir = mode == "sheet" ? argv[2] : "";
        if (mode == "sheet") Directory.CreateDirectory(outDir);
        int scale = int.Parse(Opt("--scale") ?? "4");
        foreach (var stem in stems)
        {
            var ms = ModelSchemes.Load(vfs, stem);
            if (ms is null) { Console.WriteLine($"{stem}: not loadable"); continue; }
            var uses = new Dictionary<string, (HashSet<string> Schemes, HashSet<string> Slots)>(StringComparer.OrdinalIgnoreCase);
            foreach (var v in ms.Vtfs)
                foreach (var (slot, m) in ms.Resolved[v])
                {
                    string st = CarMaterialTable.StemOf(m);
                    if (!uses.TryGetValue(st, out var u)) uses[st] = u = ([], []);
                    u.Schemes.Add(v); u.Slots.Add(slot);
                }
            if (mode == "survey")
            {
                Console.WriteLine($"## {stem} ({(nitro ? "nitro" : "i76")}) schemes={ms.Vtfs.Count}");
                foreach (var g in uses.GroupBy(k => System.Text.RegularExpressions.Regex.Replace(k.Key, @"\d$", "?"), StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
                {
                    var t = res.Load(res.FindByStem(g.First().Key) ?? g.First().Key);
                    Console.WriteLine($"{g.Key}\t{t?.Width}x{t?.Height}\t{g.Count()} names\t{(table.Lists(g.First().Key) ? "listed" : "-")}");
                }
                continue;
            }
            // sheet: each texture (the first scheme that uses it, or --scheme N's) at scale×, grid lines every 8 texels, beside its map
            int pick = int.Parse(Opt("--scheme") ?? "0");
            var items = new List<(string Name, byte[] Rgb, byte[] Map, int W, int H)>();
            foreach (var (st, u) in uses.GroupBy(k => System.Text.RegularExpressions.Regex.Replace(k.Key, @"\d$", "?"), StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase).Select(g => g.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase).First()).Select(k => (k.Key, k.Value)))
            {
                if (res.Load(res.FindByStem(st) ?? st) is not { } t) continue;
                var rgba = TextureReader.ToRgba(t, null);
                var ids = table.Ids(st, rgba, t.Width, t.Height);
                items.Add((st, rgba, ids is null ? [] : CarMaterialTable.FalseColour(ids), t.Width, t.Height));
            }
            WriteSheet(Path.Combine(outDir, $"{(nitro ? "n" : "i")}_{stem}.png"), items, scale);
            Console.WriteLine($"{stem}: {items.Count} textures");
        }
        return 0;
    }

    public static void WriteSheet(string path, List<(string Name, byte[] Rgb, byte[] Map, int W, int H)> items, int scale)
    {
        // each texture: the painting (grid) and its map side by side; rows of two textures. Written as a PPM and left to ImageMagick for the labels.
        var tmp = Directory.CreateTempSubdirectory("cartable").FullName;
        var files = new List<string>();
        int k = 0;
        foreach (var (name, rgba, map, w, h) in items)
        {
            int s = Math.Max(1, scale * 64 / Math.Max(w, 64));   // small textures bigger
            if (w * s > 640) s = Math.Max(1, 640 / w);
            int W = w * s, H = h * s;
            var img = new byte[W * H * 3];
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    int i = (y / s) * w + x / s;
                    byte r = rgba[i * 4], g = rgba[i * 4 + 1], b = rgba[i * 4 + 2];
                    if (rgba[i * 4 + 3] == 0) { r = 255; g = 0; b = 255; }
                    bool grid = x % (8 * s) == 0 || y % (8 * s) == 0;
                    if (x % (32 * s) == 0 || y % (32 * s) == 0) { r = 255; g = 255; b = 0; }
                    else if (grid) { r = (byte)(r / 2 + 100); g = (byte)(g / 2 + 100); b = (byte)(b / 2 + 100); }
                    int o = (y * W + x) * 3; img[o] = r; img[o + 1] = g; img[o + 2] = b;
                }
            var mimg = new byte[W * H * 3];
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    int i = (y / s) * w + x / s;
                    int o = (y * W + x) * 3;
                    if (map.Length == 0) { mimg[o] = mimg[o + 1] = mimg[o + 2] = 70; continue; }
                    byte r = map[i * 3], g = map[i * 3 + 1], b = map[i * 3 + 2];
                    // the painting's shading kept faintly under the false colour
                    float l = (0.299f * rgba[i * 4] + 0.587f * rgba[i * 4 + 1] + 0.114f * rgba[i * 4 + 2]) / 255f * 0.5f + 0.5f;
                    if (rgba[i * 4 + 3] == 0) { mimg[o] = mimg[o + 1] = mimg[o + 2] = 0; continue; }
                    mimg[o] = (byte)(r * l); mimg[o + 1] = (byte)(g * l); mimg[o + 2] = (byte)(b * l);
                }
            string a = Path.Combine(tmp, $"{k:000}_a.ppm"), bq = Path.Combine(tmp, $"{k:000}_b.ppm");
            File.WriteAllBytes(a, [.. System.Text.Encoding.ASCII.GetBytes($"P6 {W} {H} 255\n"), .. img]);
            File.WriteAllBytes(bq, [.. System.Text.Encoding.ASCII.GetBytes($"P6 {W} {H} 255\n"), .. mimg]);
            var ps = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("magick",
                $"\"{a}\" \"{bq}\" +append -background \"#303030\" -gravity north -splice 0x20 {FontArg()}-fill white -pointsize 16 -annotate +0+2 \"{name} {w}x{h}\" \"{Path.Combine(tmp, $"{k:000}.png")}\"") { UseShellExecute = false })!;
            ps.WaitForExit();
            files.Add(Path.Combine(tmp, $"{k:000}.png"));
            k++;
        }
        var args = string.Join(" ", files.Select(f => $"\"{f}\""));
        var m = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("magick", $"montage {FontArg()}+label {args} -tile 2x -geometry +6+6 -background \"#202020\" \"{path}\"") { UseShellExecute = false })!;
        m.WaitForExit();
        Directory.Delete(tmp, true);
    }

    /// <summary>ImageMagick's "-font FILE " for the system's default sans font (found with fc-match), or nothing so ImageMagick picks its own.</summary>
    static string FontArg()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("fc-match", "-f %{file} sans") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            using var p = System.Diagnostics.Process.Start(psi)!;
            var file = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit();
            if (p.ExitCode == 0 && File.Exists(file)) return $"-font \"{file}\" ";
        }
        catch (Exception) { }
        return "";
    }
}
