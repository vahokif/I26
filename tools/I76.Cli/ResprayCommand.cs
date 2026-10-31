using I76.Formats.Assembly;
using I76.Formats.Imaging;
using I76.Formats.Vfs;

namespace I76.Cli;

public static class ResprayCommand
{
    public static readonly string[] BaseStems = "valepre vastryd vazamz vccameo vcgcour vcmanta vcroyal vcpolic vcvan vdlight vdstag vfcoupe vfdclyd vfpalmn vfratlr vfypony vgoon vjsovrn vmxmarx vpjrabt vppirna vavikea vxbus".Split(' ');
    public static readonly string[] NitroStems = "valepre vastryd vazamz vccameo vchacnd vcgcour vcmanta vcroyal vcpolic vcvan vdlight vdrampg vdstag vfcoupe vfdclyd vfpalmn vfratlr vfypony vgoon vjdread vjsovrn vleoprd vpjrabt vppirna vrmarsh vsvan vavikea vwclown vwfwolf vxamblc vxhears vagrapl vmcargo vmload vxmilk vmxmarx vxbus".Split(' ');

    public static int Run(List<string> argv, string? installDir)
    {
        string? Opt(string n) { int i = argv.IndexOf(n); return i >= 0 && i + 1 < argv.Count ? argv[i + 1] : null; }
        if (argv.Count < 2 || argv[1].StartsWith("--")) { Console.Error.WriteLine("usage: i76 respray-preview OUT_DIR [--install DIR] [--nitro-install DIR]"); return 2; }
        string mode = Opt("--mode") ?? "render";
        var rows = new List<(byte[] Rgb, int W, int H)>();
        (byte, byte, byte)[] pal8p = [];
        string[] preview = "vcmanta vcpolic vcroyal vfdclyd vpjrabt vgoon vavikea vxbus vdstag vmxmarx vppirna vcvan vfpalmn vccameo".Split(' ');
        string? only = Opt("--only");
        foreach (var (dir, nitro) in new[] { (Opt("--install") ?? installDir, false), (Opt("--nitro-install") ?? Environment.GetEnvironmentVariable("I76_NITRO_INSTALL"), true) }.Where(x => !argv.Contains(x.Item2 ? "--base-only" : "--nitro-only")))
        {
            if (dir is null || !Directory.Exists(dir)) continue;
            var vfs = new AssetVfs(new GameInstall(dir));
            { var pp = new Palette(vfs.Read("t01.act")); pal8p = Enumerable.Range(0, 256).Select(i => pp[i]).ToArray(); }
            foreach (var stem in nitro ? NitroStems : BaseStems)
            {
                if (only is not null ? !only.Split(',').Contains(stem) : !argv.Contains("--all") && !preview.Contains(stem)) continue;
                var ms = ModelSchemes.Load(vfs, stem);
                if (ms is null) { Console.WriteLine($"{(nitro ? "N" : "B")} {stem}: not loadable"); continue; }
                Console.WriteLine($"{(nitro ? "N" : "B")} {stem} vdf={ms.Vdf} obj={ms.Model.ObjectCode} schemes={string.Join(",", ms.Vtfs)}");
                if (mode == "skins")
                {
                    var res = new TextureResolver(vfs);
                    Directory.CreateDirectory(argv[1]);
                    foreach (var v in ms.Vtfs)
                    {
                        var tex = ResprayImg.BodyTextures(ms, v).Select(b => (b, t: res.Load(b.Member))).Where(x => x.t is not null)
                            .Select(x => (TextureReader.ToRgba(x.t!, null, x.b.Cutout), x.t!.Width, x.t.Height)).ToList();
                        var (rgb, w, h) = ResprayImg.Sheet(tex, 2);
                        IsectSurvey.WritePng(Path.Combine(argv[1], $"{stem}_{Path.GetFileNameWithoutExtension(v)}.png"), rgb, w, h);
                    }
                }
                if (mode == "pal")
                {
                    var res = new TextureResolver(vfs);
                    foreach (var v in ms.Vtfs.Take(1))
                        foreach (var b in ResprayImg.BodyTextures(ms, v).Take(3))
                        {
                            var t = res.Load(b.Member)!;
                            var cnt = new int[256]; foreach (var ix in t.Indices) cnt[ix]++;
                            Console.WriteLine($"-- {b.Member} {t.Width}x{t.Height} pal {t.Palette?.Length}");
                            for (int i = 0; i < 256; i++)
                                if (cnt[i] > 0) { var (r, g, bb) = TextureReader.DecodeRgb565(t.Palette![i]); Console.WriteLine($"{i,3} {r,3} {g,3} {bb,3} n={cnt[i]}"); }
                        }
                }
                if (mode == "acts")
                {
                    var acts = vfs.Names.Where(n => n.EndsWith(".act", StringComparison.OrdinalIgnoreCase) && !n.Contains("_16") && n.Length == 7 && "tmps".Contains(char.ToLower(n[0]))).OrderBy(n => n).ToList();
                    var pals = acts.Select(a => new Palette(vfs.Read(a))).ToList();
                    Console.WriteLine(string.Join(" ", acts));
                    string row = "";
                    for (int i = 0; i < 256; i++)
                    {
                        int same = pals.Count(p => p[i] == pals[0][i]);
                        row += same == pals.Count ? "=" : same > pals.Count / 2 ? "+" : ".";
                    }
                    Console.WriteLine(row);
                }
                if (mode == "pal8")
                {
                    var pal = new Palette(vfs.Read("t01.act"));
                    foreach (var v in ms.Vtfs.Take(Opt("--scheme") is { } sc ? 99 : 1))
                        foreach (var b in ResprayImg.BodyTextures(ms, v).Take(4))
                        {
                            var t = ResprayImg.LoadVqm(vfs, b.Member)!;
                            var cnt = new int[256]; foreach (var ix in t.Indices) cnt[ix]++;
                            Console.WriteLine($"-- {b.Member} {t.Width}x{t.Height}");
                            var sb = new System.Text.StringBuilder();
                            for (int i = 0; i < 256; i++)
                                if (cnt[i] > 0) { var (r, g, bb) = pal[i]; sb.Append($"{i}:({r},{g},{bb})x{cnt[i]}  "); }
                            Console.WriteLine(sb);
                        }
                }
                if (mode == "palshow")
                {
                    var pal = new Palette(vfs.Read(Opt("--act") ?? "t01.act"));
                    for (int i = 0; i < 256; i += 8) Console.WriteLine($"{i,3}: " + string.Join(" ", Enumerable.Range(i, 8).Select(k => { var (r, g, bb) = pal[k]; return $"{r:x2}{g:x2}{bb:x2}"; })));
                }
                if (mode == "survey")
                {
                    foreach (var r in ResprayAnalysis.Analyse(vfs, ms))
                    {
                        var p = r.Paint;
                        string sec = r.Second is { } q ? $"{q.H:0}/{q.S:0.00}/{q.V:0.00} {r.SecondMass * 100:0}%" : "-";
                        double dom = p.Chromatic ? 0 : 0;
                        var top = string.Join(" ", r.Stats.Ranked().Take(4).Select(c => $"{ResprayPaint.ClusterName(c)}:{r.Stats.Count[c] / r.Stats.Total * 100:0}"));
                        Console.WriteLine($"  {r.Vtf,-14} tex {r.Textures,2} paint {(p.Chromatic ? "hue" : "neutral")} h{p.H:0} s{p.S:0.00} v{p.V:0.00} y{p.Y:0.00} | 2nd {sec} | top {top} | truth {r.TruthPaint * 100:0}% P {r.Precision * 100:0} R {r.Recall * 100:0} unchg {r.UnchangedPaintMass * 100:0}%");
                    }
                }
                if (mode == "render")
                {
                    int CW = int.Parse(Opt("--w") ?? "300"), CH = CW * 17 / 30;
                    var res = new TextureResolver(vfs);
                    Directory.CreateDirectory(argv[1]);
                    foreach (var v in ms.Vtfs)
                    {
                        var sp = ResprayAnalysis.PaintOf(res, ms, v);
                        var orig = sp.Body.ToDictionary(b => b.Member, b => b.Tex, StringComparer.OrdinalIgnoreCase);
                        var cells = new List<byte[]>();
                        var cache = new Dictionary<string, ResprayAnalysis.Rgb?>(StringComparer.OrdinalIgnoreCase);
                        ResprayAnalysis.Rgb? Any(Dictionary<string, ResprayAnalysis.Rgb> over, string m) => over.TryGetValue(m, out var r) ? r : (cache.TryGetValue(m, out var c) ? c : cache[m] = ResprayAnalysis.LoadRgb(res, m));
                        var sets = new List<Dictionary<string, ResprayAnalysis.Rgb>> { orig };
                        sets.AddRange(ResprayAnalysis.Targets.Select(t => ResprayAnalysis.Recolour(sp, t)));
                        if (sp.Secondary is not null) sets.Add(ResprayAnalysis.Recolour(sp, ResprayAnalysis.Targets[3], ResprayAnalysis.Targets[1]));
                        foreach (var set in sets) cells.Add(ResprayRender.Render(ms, v, m => Any(set, m), res, 35, CW, CH));
                        if (argv.Contains("--pal97"))
                        {
                            var p97 = new Palette(vfs.Read("t01.act"));
                            foreach (var cell in cells) for (int q = 0; q < cell.Length; q += 3) { int k = SoftwarePalette.Nearest(p97, cell[q], cell[q + 1], cell[q + 2]); (cell[q], cell[q + 1], cell[q + 2]) = p97[k]; }
                        }
                        int W = CW * cells.Count, H = CH; var row = new byte[W * H * 3];
                        for (int c = 0; c < cells.Count; c++) for (int y = 0; y < H; y++) Array.Copy(cells[c], y * CW * 3, row, (y * W + c * CW) * 3, CW * 3);
                        string vn = Path.GetFileNameWithoutExtension(v);
                        IsectSurvey.WritePng(Path.Combine(argv[1], $"{(nitro ? "n_" : "")}{stem}_{vn}.png"), row, W, H);
                        rows.Add((row, W, H));
                        // the flat textures: the original body sheet and each recolour
                        string flatDir = Path.Combine(argv[1], "flat"); Directory.CreateDirectory(flatDir);
                        for (int si = 0; si < sets.Count; si++)
                        {
                            var tex = sp.Body.Select(b => sets[si][b.Member]).Select(t => (ToRgba(t), t.W, t.H)).ToList();
                            var (frgb, fw, fh) = ResprayImg.Sheet(tex, 2, 1000);
                            string nm = si == 0 ? "original" : si <= 4 ? ResprayAnalysis.Targets[si - 1].Name.Replace(' ', '_') : "blue_white";
                            IsectSurvey.WritePng(Path.Combine(flatDir, $"{stem}_{vn}_{nm}.png"), frgb, fw, fh);
                        }
                    }
                }
                if (mode == "flat")
                {
                    var code = ms.Model.ObjectCode ?? "";
                    foreach (var part in ms.Model.Parts.Where(q => q.NodeName.StartsWith(code, StringComparison.OrdinalIgnoreCase)))
                    {
                        var g = part.Mesh.Faces.Where(f => !(f.Texture.Length > 0 && f.Unk_0x20 != 0 && ms.Resolved[ms.Vtfs[0]].ContainsKey(f.Texture))).GroupBy(f => $"{f.R},{f.G},{f.B} tex='{f.Texture}' m={f.Unk_0x20:x2}");
                        foreach (var x in g) Console.WriteLine($"   {part.NodeName} flat {x.Key} x{x.Count()}");
                    }
                }
                if (mode == "idx")
                {
                    var paintSlots = ms.PaintSlots().ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var imgs = new Dictionary<string, IndexedTexture?>(StringComparer.OrdinalIgnoreCase);
                    IndexedTexture? G(string m) { if (!imgs.TryGetValue(m, out var r)) imgs[m] = r = ResprayImg.LoadVqm(vfs, m); return r; }
                    foreach (var v in ms.Vtfs)
                    {
                        var diff = new long[256]; var same = new long[256]; long dTot = 0;
                        foreach (var b in ResprayImg.BodyTextures(ms, v))
                        {
                            if (b.Cutout || G(b.Member) is not { } t) continue;
                            var others = paintSlots.Contains(b.Slot) ? ms.Vtfs.Where(x => x != v).Select(x => ms.Resolved[x].GetValueOrDefault(b.Slot)).Where(x => x is not null && !x!.Equals(b.Member, StringComparison.OrdinalIgnoreCase)).Select(x => G(x!)).Where(x => x is not null && x.Width == t.Width && x.Height == t.Height).ToList() : [];
                            for (int i = 0; i < t.Indices.Length; i++)
                            {
                                bool d = others.Any(q => q!.Indices[i] != t.Indices[i] && Math.Abs(pal8(q.Indices[i]) - pal8(t.Indices[i])) > 30);
                                if (d) { diff[t.Indices[i]]++; dTot++; } else same[t.Indices[i]]++;
                            }
                        }
                        int PalSum(int i) => 0;
                        var set = Enumerable.Range(0, 256).Where(i => diff[i] > 0 && diff[i] >= same[i] && diff[i] >= dTot / 200).ToList();
                        long cov = set.Sum(i => diff[i]), sameIn = set.Sum(i => same[i]), tot = set.Sum(i => diff[i] + same[i]);
                        var runs = new List<string>(); int a = -1, pv = -1;
                        foreach (var i in set.Append(9999)) { if (a < 0) a = i; else if (i != pv + 1) { runs.Add(a == pv ? $"{a}" : $"{a}-{pv}"); a = i; } pv = i; }
                        Console.WriteLine($"  {v,-14} paint idx {string.Join(",", runs)} ({set.Count}) covers {(dTot > 0 ? cov * 100.0 / dTot : 0):0}% of changed texels; {(tot > 0 ? sameIn * 100.0 / tot : 0):0}% of their uses are unchanged detail");
                    }
                    int pal8(int i) { var (r, g, bb) = pal8p[i]; return r + g + bb; }
                }
                if (mode == "slots")
                {
                    var paint = ms.PaintSlots().ToHashSet(StringComparer.OrdinalIgnoreCase);
                    foreach (var (slot, s) in ms.Slots.OrderByDescending(k => k.Value.Faces))
                        Console.WriteLine($"   {(paint.Contains(slot) ? "P" : " ")} {slot,-14} faces {s.Faces,3} modes {string.Join("/", s.Modes)} parts {string.Join(",", s.Parts.Take(5))} -> {string.Join(" | ", ms.Vtfs.Select(v => ms.Resolved[v].GetValueOrDefault(slot) ?? "-"))}");
                }
            }
        }
        for (int k = 0, n = 0; k < rows.Count; k += 10, n++)
        {
            var part = rows.Skip(k).Take(10).ToList();
            int W = part.Max(r => r.W), H = part.Sum(r => r.H);
            var sheet = new byte[W * H * 3]; Array.Fill(sheet, (byte)255);
            int y = 0;
            foreach (var r in part) { for (int j = 0; j < r.H; j++) Array.Copy(r.Rgb, j * r.W * 3, sheet, ((y + j) * W) * 3, r.W * 3); y += r.H; }
            IsectSurvey.WritePng(Path.Combine(argv[1], $"contact{n + 1}.png"), sheet, W, H);
        }
        return 0;
    }

    private static byte[] ToRgba(ResprayAnalysis.Rgb t)
    {
        var o = new byte[t.W * t.H * 4];
        for (int i = 0; i < t.W * t.H; i++) { o[i * 4] = t.Data[i * 3]; o[i * 4 + 1] = t.Data[i * 3 + 1]; o[i * 4 + 2] = t.Data[i * 3 + 2]; o[i * 4 + 3] = 255; }
        return o;
    }
}
