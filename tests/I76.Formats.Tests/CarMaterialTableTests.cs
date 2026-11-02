using I76.Formats.Assembly;
using I76.Formats.Imaging;
using I76.Formats.Vfs;
using Xunit;

namespace I76.Formats.Tests;

/// <summary>The car material table (CarMaterials.tsv): it parses, names real textures and regions inside them, and what it does not list stays as it was.</summary>
public class CarMaterialTableTests
{
    private static byte[] Fill(int w, int h, Func<int, int, (byte, byte, byte)> px)
    {
        var rgba = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++) { var (r, g, b) = px(x, y); int o = (y * w + x) * 4; rgba[o] = r; rgba[o + 1] = g; rgba[o + 2] = b; rgba[o + 3] = 255; }
        return rgba;
    }

    [Fact]
    public void TheShippedTableParsesAndItsPresetsAreSound()
    {
        var t = CarMaterialTable.Default;
        Assert.NotNull(t);
        Assert.Equal(CarMaterialTable.Presets.Count, CarMaterialTable.Presets.Select(p => p.Name).Distinct().Count());
        for (int i = 0; i < CarMaterialTable.Presets.Count; i++) Assert.Equal(i + 1, CarMaterialTable.Presets[i].Id);
        // the shipped rows are window fixes only: glass, or a body paint over what the segmentation took for glass
        Assert.All(t.Rows, r => Assert.True(r.Preset.Name is "glass" or "gloss-paint", $"line {r.Line}: {r.Preset.Name}"));
    }

    [Fact]
    public void RowsApplyInOrderWithinRectanglesAndColours()
    {
        var t = CarMaterialTable.Parse("CAR1\tgloss-paint\nCAR1\tglass\t2,2,4,3\nCAR1\tchrome\t*\twhite\n");
        // a 16×8 texture: red paint, a blue-grey window in the rectangle, a white stripe (a chrome row: the map keeps glass and body only)
        var rgba = Fill(16, 8, (x, y) => y == 7 ? ((byte)255, (byte)255, (byte)255) : (x >= 2 && x < 6 && y >= 2 && y < 5) ? ((byte)90, (byte)110, (byte)150) : ((byte)200, (byte)30, (byte)30));
        var ids = t.Ids("car1.m16", rgba, 16, 8)!;
        byte Id(string n) => CarMaterialTable.PresetNamed(n)!.Id;
        Assert.Equal(Id("gloss-paint"), ids[0]);
        Assert.Equal(Id("glass"), ids[3 * 16 + 3]);
        Assert.Equal(Id("gloss-paint"), ids[7 * 16 + 9]);
        // a colour class alone, and the rectangle alone, are each only a part of a row
        var t2 = CarMaterialTable.Parse("CAR1\tgloss-paint\nCAR1\tglass\t*\tglass\n");
        Assert.Equal(Id("glass"), t2.Ids("CAR1", rgba, 16, 8)![3 * 16 + 3]);
        Assert.Equal(Id("gloss-paint"), t2.Ids("CAR1", rgba, 16, 8)![0]);
    }

    [Fact]
    public void TheMapHoldsGlassAndBodyOnly()
    {
        // every preset a row names comes out as body, but glass; a row of another preset lifts the glass off its texels
        var rows = string.Join("", CarMaterialTable.Presets.Select(p => $"CAR1\t{p.Name}\t{p.Id - 1},0,1,1\n"));
        var ids = CarMaterialTable.Parse(rows).Ids("CAR1", Fill(32, 4, (_, _) => ((byte)9, (byte)9, (byte)9)), 32, 4)!;
        Assert.All(ids, i => Assert.True(i == CarMaterialTable.GlassId || i == CarMaterialTable.BodyId));
        Assert.Equal(CarMaterialTable.GlassId, ids[CarMaterialTable.GlassId - 1]);
        Assert.Equal(1, ids.Count(i => i == CarMaterialTable.GlassId));
        var lifted = CarMaterialTable.Parse("CAR1\tglass\nCAR1\tsteel\t0,0,4,4\n").Ids("CAR1", Fill(8, 4, (_, _) => ((byte)9, (byte)9, (byte)9)), 8, 4)!;
        Assert.Equal(CarMaterialTable.BodyId, lifted[0]);
        Assert.Equal(CarMaterialTable.GlassId, lifted[7]);
    }

    [Fact]
    public void PatternsMatchTheSchemeDigitAndAnUnlistedTextureIsLeftAlone()
    {
        Assert.True(CarMaterialTable.Matches("al11mdl?", "AL11MDL3"));
        Assert.False(CarMaterialTable.Matches("al11mdl?", "AL11MDU3"));
        Assert.True(CarMaterialTable.Matches("GMLP*", "gmlpf_"));
        var t = CarMaterialTable.Parse("AB11BDY?\tmatte-paint\n");
        Assert.Null(t.Ids("CD11BDY1", Fill(8, 8, (_, _) => ((byte)1, (byte)1, (byte)1)), 8, 8));
        Assert.False(t.Lists("WA0101"));
    }

    [Fact]
    public void BadRowsAreRefused()
    {
        Assert.Throws<FormatException>(() => CarMaterialTable.Parse("X\tno-such-preset\n"));
        Assert.Throws<FormatException>(() => CarMaterialTable.Parse("X\tglass\t1,2,3\n"));
        Assert.Throws<FormatException>(() => CarMaterialTable.Parse("X\tglass\t*\tpurple\n"));
    }

    [Fact]
    public void OnlyPaintPresetsTakeARespray()
    {
        var ids = new[] { CarMaterialTable.PresetNamed("gloss-paint")!.Id, CarMaterialTable.PresetNamed("chrome")!.Id, CarMaterialTable.PresetNamed("matte-paint")!.Id, CarMaterialTable.PresetNamed("glass")!.Id };
        Assert.Equal(new byte[] { 1, 0, 1, 0 }, CarMaterialTable.PaintWeight(ids));
    }

    /// <summary>Every texture of an install (each name the VFS holds that is a texture), by stem, with its size.</summary>
    private static IEnumerable<(string Stem, int W, int H)> Textures(string dir)
    {
        var vfs = new AssetVfs(new GameInstall(dir));
        var res = new TextureResolver(vfs);
        foreach (var n in vfs.Names.Where(n => TextureResolver.Extensions.Any(e => n.EndsWith(e, StringComparison.OrdinalIgnoreCase))).Select(TextureResolver.Stem).Distinct(StringComparer.OrdinalIgnoreCase))
            if (CarMaterialTable.Default.Lists(n) && res.Load(res.FindByStem(n) ?? n) is { } t) yield return (n, t.Width, t.Height);
    }

    [InstallFact]
    public void EveryRowNamesATextureOfAnInstallAndItsRegionsLieInsideIt()
    {
        var dirs = new[] { TestInstall.Dir, Environment.GetEnvironmentVariable("I76_NITRO_INSTALL") }.Where(d => d is { Length: > 0 } && Directory.Exists(d)).ToList();
        var all = dirs.SelectMany(d => Textures(d!)).ToList();
        var bad = new List<string>();
        foreach (var row in CarMaterialTable.Default.Rows)
        {
            var hits = all.Where(t => CarMaterialTable.Matches(row.Texture, t.Stem)).ToList();
            if (hits.Count == 0) bad.Add($"line {row.Line}: no texture named {row.Texture}");
            if (row.Rect is { } r)
                foreach (var h in hits)
                    if (!(r.X >= 0 && r.Y >= 0 && r.X + r.W <= h.W && r.Y + r.H <= h.H)) bad.Add($"line {row.Line}: {r} is outside {h.Stem} ({h.W}x{h.H})");
        }
        Assert.True(bad.Count == 0, "CarMaterials.tsv: " + string.Join("; ", bad));
    }

    private static string RepoFile(params string[] parts)
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "game", "Shaders", "object.gdshaderinc"))) dir = Path.GetDirectoryName(dir);
        return File.ReadAllText(Path.Combine([dir!, .. parts]));
    }

    [Fact]
    public void TheWindowDataIsInsideTheAssemblyAndLoads()
    {
        // an export carries the data as embedded resources; a missing one used to fall back to "no data" silently (every window gone)
        var asm = typeof(CarMaterialTable).Assembly;
        Assert.NotNull(asm.GetManifestResourceStream("CarMasks.bin"));
        Assert.NotNull(asm.GetManifestResourceStream("CarMaterials.tsv"));
        Assert.True(CarMaskMap.Default.Entries.Count > 100, "CarMasks.bin loaded empty");
        Assert.True(CarMaterialTable.Default.Rows.Count > 20);
        // the cars the owner's reporters know best have windows in the map
        foreach (var stem in new[] { "PP11TP_1", "JS11TP_1", "GO11FTF1", "CH11TP_1", "XB11MDL1" })
            Assert.True(CarMaskMap.Default.Has(stem) || CarMaterialTable.Default.Lists(stem), stem);
        foreach (var e in CarMaskMap.Default.Entries) Assert.Contains(CarMaterialTable.GlassId, e.Ids);
    }

    [Fact]
    public void EveryShaderParameterTheMaterialLibraryNamesIsAUniformOfTheObjectShader()
    {
        string shader = RepoFile("game", "Shaders", "object.gdshaderinc");
        string lib = RepoFile("game", "Scripts", "Assets", "MaterialLibrary.cs");
        var names = System.Text.RegularExpressions.Regex.Matches(lib, "SetShaderParameter\\(\"(\\w+)\"").Select(m => m.Groups[1].Value).Distinct().ToList();
        Assert.Contains("glass_tex", names);
        var missing = names.Where(n => !System.Text.RegularExpressions.Regex.IsMatch(shader, "uniform\\s+\\w+\\s+" + n + "\\b")).ToList();
        Assert.True(missing.Count == 0, "not uniforms of object.gdshaderinc: " + string.Join(", ", missing));
    }

    [Fact]
    public void TheCarShaderHasNoMaterialClassesAndOneBodyRule()
    {
        string shader = RepoFile("game", "Shaders", "object.gdshaderinc");
        foreach (var gone in new[] { "has_table", "table_ids", "table_debug" })
            Assert.DoesNotContain(gone, shader);
        // the car's own paint branch reads no per-texel finish: only the window data (glass_*) and a respray's mask are samplers it adds
        int a = shader.IndexOf("} else if (car_paint) {", StringComparison.Ordinal), b = shader.IndexOf("} else if (tread) {", StringComparison.Ordinal);
        Assert.InRange(a, 1, b);
        Assert.DoesNotContain("orm_tex", shader[a..b]);
    }
}
