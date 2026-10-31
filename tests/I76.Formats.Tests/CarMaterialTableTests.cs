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
        Assert.Contains(t.Rows, r => r.Texture == "CH11BED1" && r.Preset.Name == "liner");
    }

    [Fact]
    public void RowsApplyInOrderWithinRectanglesAndColours()
    {
        var t = CarMaterialTable.Parse("CAR1\tgloss-paint\nCAR1\tglass\t2,2,4,3\nCAR1\tchrome\t*\twhite\n");
        // a 16×8 texture: red paint, a blue-grey window in the rectangle, a white stripe
        var rgba = Fill(16, 8, (x, y) => y == 7 ? ((byte)255, (byte)255, (byte)255) : (x >= 2 && x < 6 && y >= 2 && y < 5) ? ((byte)90, (byte)110, (byte)150) : ((byte)200, (byte)30, (byte)30));
        var ids = t.Ids("car1.m16", rgba, 16, 8)!;
        byte Id(string n) => CarMaterialTable.PresetNamed(n)!.Id;
        Assert.Equal(Id("gloss-paint"), ids[0]);
        Assert.Equal(Id("glass"), ids[3 * 16 + 3]);
        Assert.Equal(Id("chrome"), ids[7 * 16 + 9]);
        // a colour class alone, and the rectangle alone, are each only a part of a row
        var t2 = CarMaterialTable.Parse("CAR1\tgloss-paint\nCAR1\tglass\t*\tglass\n");
        Assert.Equal(Id("glass"), t2.Ids("CAR1", rgba, 16, 8)![3 * 16 + 3]);
        Assert.Equal(Id("gloss-paint"), t2.Ids("CAR1", rgba, 16, 8)![0]);
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
}
