using I76.Formats.Assembly;
using I76.Formats.Imaging;
using Xunit;

namespace I76.Formats.Tests;

/// <summary>The stock textures whose 16-bit copy has a slip (TextureRepairs): what is changed, and that nothing else is.</summary>
public class TextureRepairsTests
{
    private const ushort Green = 0x07E0, Blue = 0x001F;

    [Fact]
    public void OnlyTheSlipsColoursChangeAndTheTexelsStay()
    {
        var t = new IndexedTexture(2, 2, [0, 1, 2, 1], [Blue, Green, 0x4208]);
        var r = TextureRepairs.Repaired("CA_1LT_6.M16", t)!;
        Assert.Equal([0x4228, 0x4A29, 0x4208], r.Palette!);
        Assert.Equal(t.Indices, r.Indices);
        Assert.Equal([Blue, Green, 0x4208], t.Palette!);   // the file's own is untouched
        // any spelling and folder of the member; a copy without the slip comes back the same
        Assert.NotNull(TextureRepairs.Repaired(@"addon\ca_1lt_6.m16", t));
        Assert.Equal([0x4208, 0x5AAB], TextureRepairs.Repaired("CA_1LT_6.M16", new IndexedTexture(1, 1, [0], [0x4208, 0x5AAB]))!.Palette!);
        // nothing else, and nothing without a palette of its own
        Assert.Null(TextureRepairs.Repaired("CA_1LT_1.M16", t));
        Assert.Null(TextureRepairs.Repaired("CA_1LT_6.M16", t with { Palette = null }));
        Assert.True(TextureRepairs.Has("CA_1LT_6.m16"));
        Assert.False(TextureRepairs.Has("CA_1LT_6.map"));
    }

    [InstallFact]
    public void TheForecourtsBorderIsTheSlipAndLosesIt()
    {
        var res = new TextureResolver(TestInstall.Vfs);
        var t = res.Load("CA_1LT_6.M16")!;
        var art = res.Load("ca_1lt_6.map")!;
        int slipped = 0, inside = 0;
        for (int y = 0; y < t.Height; y++)
            for (int x = 0; x < t.Width; x++)
            {
                int i = y * t.Width + x;
                if (t.Palette![t.Indices[i]] is not (Green or Blue)) continue;
                slipped++;
                if (x > 0 && y > 0 && x < t.Width - 1 && y < t.Height - 1) inside++;
                // where the 8-bit art has its two dark shades
                Assert.True(art.Indices[i] is 187 or 190, $"{x},{y}: art index {art.Indices[i]}");
            }
        Assert.Equal(350, slipped);
        Assert.True(inside <= 4, $"{inside} inside the border");
        var r = TextureRepairs.Repaired("CA_1LT_6.M16", t)!;
        Assert.DoesNotContain(r.Palette!, c => c is Green or Blue);
        // every other texel's colour as the file has it
        for (int i = 0; i < t.Indices.Length; i++)
            if (t.Palette![t.Indices[i]] is not (Green or Blue)) Assert.Equal(t.Palette[t.Indices[i]], r.Palette![r.Indices[i]]);
    }

    [InstallFact]
    public void NoOtherStockTextureHasTheSlipsColours()
    {
        var vfs = TestInstall.Vfs;
        var res = new TextureResolver(vfs);
        var found = new List<string>();
        foreach (var n in vfs.Names.Where(n => n.EndsWith(".m16", StringComparison.OrdinalIgnoreCase)))
        {
            IndexedTexture? t;
            try { t = res.Load(n); } catch (InvalidDataException) { continue; }
            if (t?.Palette is not { } pal) continue;
            var used = new bool[pal.Length];
            foreach (var i in t.Indices) if (i < used.Length) used[i] = true;
            if (Enumerable.Range(0, pal.Length).Any(i => used[i] && pal[i] is Green or Blue)) found.Add(n.ToUpperInvariant());
        }
        Assert.Equal(["CA_1LT_6.M16"], found);
        Assert.All(found, n => Assert.True(TextureRepairs.Has(n)));
    }
}
