using I76.Formats.Assembly;
using I76.Formats.Imaging;
using I76.Formats.Vfs;

namespace I76.Cli;

/// <summary>
/// i76 props OUTDIR [--maps]: every texture the scenery and props of every mission use (not the vehicles'), as PPMs
/// (keyed texels magenta), with a table of who uses each (definitions, ODEF types, face counts and modes) in
/// OUTDIR/props.tsv; with --maps, the modern look's prop material map beside each (STEM_orm.ppm: roughness, metallic,
/// specular) and its class weights (STEM_cls.ppm: glass blue, metal red, vegetation green, else grey).
/// Original data: never commit what it writes.
/// </summary>
public static class PropSurvey
{
    public static int Run(GameInstall install, List<string> argv)
    {
        var vfs = new AssetVfs(install);
        var asm = new ModelAssembler(vfs);
        var res = new TextureResolver(vfs);
        var outDir = argv[1];
        bool maps = argv.Contains("--maps");
        Directory.CreateDirectory(outDir);
        var dir = install.MissionDir!;
        var use = new Dictionary<string, (int Faces, int Placed, HashSet<string> Defs, HashSet<string> Types, HashSet<string> Modes, HashSet<string> Missions, string Palette)>(StringComparer.OrdinalIgnoreCase);
        var models = new Dictionary<string, AssembledModel?>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(dir).Where(f => f.EndsWith(".msn", StringComparison.OrdinalIgnoreCase)).Order())
        {
            var w = new MissionWorld(vfs, asm, path);
            foreach (var g in w.Placements.Concat(w.LinePlacements).Where(p => p.Definition is not null && !p.Definition.EndsWith(".vcf", StringComparison.OrdinalIgnoreCase))
                         .GroupBy(p => p.Definition!, StringComparer.OrdinalIgnoreCase))
            {
                if (!models.TryGetValue(g.Key, out var m)) models[g.Key] = m = asm.Assemble(g.Key);
                if (m is null) continue;
                foreach (var part in m.Parts)
                    foreach (var f in part.Mesh.Faces)
                        if (f.Texture.Length > 0 && f.Unk_0x20 != 0 && res.ResolveFace(f.Texture, m.ObjectCode, m.TmtNames, vtfMaps: m.VtfMaps) is { } member)
                        {
                            if (!use.TryGetValue(member, out var u)) use[member] = u = (0, 0, [], [], [], [], w.PaletteName ?? "");
                            use[member] = (u.Faces + 1, u.Placed + g.Count(), u.Defs, u.Types, u.Modes, u.Missions, u.Palette);
                            u.Defs.Add(g.Key);
                            u.Types.Add($"{g.First().Object.ObjectType:x2}");
                            u.Modes.Add($"{f.Unk_0x20:x2}/{f.Unk_0x21:x2}");
                            u.Missions.Add(w.Mission.Name);
                        }
            }
        }
        using var tsv = new StreamWriter(Path.Combine(outDir, "props.tsv"));
        tsv.WriteLine("stem\tsize\tfaces\tplaced\tclass\ttypes\tmodes\tdefs\tmissions");
        var pals = new Dictionary<string, Palette?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (member, u) in use.OrderByDescending(k => k.Value.Placed))
        {
            if (res.Load(member) is not { } t) continue;
            if (!pals.TryGetValue(u.Palette, out var pal)) pals[u.Palette] = pal = vfs.TryRead(u.Palette) is { } pd ? new Palette(pd) : null;
            var rgba = TextureReader.ToRgba(t, pal);
            var stem = Path.GetFileNameWithoutExtension(member.Replace('\\', '/').Split('/', ':').Last());
            Ppm(Path.Combine(outDir, stem + ".ppm"), t.Width, t.Height, k => rgba[k * 4 + 3] == 0 ? ((byte)255, (byte)0, (byte)255) : (rgba[k * 4], rgba[k * 4 + 1], rgba[k * 4 + 2]));
            string cls = "";
            if (maps)
            {
                var mp = PropMaterial.Build(rgba, t.Width, t.Height, stem, PropMaterial.FamilyOf(u.Defs.First()));
                cls = mp.Summary;
                Ppm(Path.Combine(outDir, stem + "_orm.ppm"), mp.Width, mp.Height, k => (mp.Orm[k * 4], mp.Orm[k * 4 + 1], mp.Orm[k * 4 + 3]));
                Ppm(Path.Combine(outDir, stem + "_cls.ppm"), mp.Width, mp.Height, k => mp.ClassColour(k));
            }
            tsv.WriteLine($"{stem}\t{t.Width}x{t.Height}\t{u.Faces}\t{u.Placed}\t{cls}\t{string.Join(",", u.Types)}\t{string.Join(",", u.Modes)}\t{string.Join(",", u.Defs.Take(6))}\t{string.Join(",", u.Missions.Take(6))}");
        }
        Console.WriteLine($"{use.Count} textures over {models.Count} definitions");
        if (argv.Contains("--time"))
        {
            var all = use.Select(kv => (kv.Key, t: res.Load(kv.Key))).Where(x => x.t is not null)
                .Select(x => (x.Key, x.t!.Width, x.t.Height, Rgba: TextureReader.ToRgba(x.t, null))).ToList();
            for (int round = 0; round < 3; round++)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                foreach (var (k, w, h, rgba) in all) PropMaterial.Build(rgba, w, h, k);
                Console.WriteLine($"round {round}: {all.Count} textures in {sw.Elapsed.TotalMilliseconds:0} ms");
            }
        }
        return 0;
    }

    static void Ppm(string path, int pw, int ph, Func<int, (byte, byte, byte)> px)
    {
        using var o = File.Create(path);
        o.Write(System.Text.Encoding.ASCII.GetBytes($"P6 {pw} {ph} 255\n"));
        for (int k = 0; k < pw * ph; k++) { var (r, g, b) = px(k); o.Write([r, g, b]); }
    }
}
