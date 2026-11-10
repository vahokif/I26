using System.IO.Compression;
using System.Text;
using I76.Formats.Imaging;
using I76.Formats.Shell;
using Xunit;

namespace I76.Formats.Tests;

/// <summary>A pack's upscale of a world texture: found by stem in the pack's folders, at a whole-multiple scale, flipped to the stored order, keyed as the original.</summary>
public class HdTextureTests
{
    /// <summary>A 4x2 original in stored (bottom-up) order: row 0 red with its last texel keyed, row 1 blue.</summary>
    private static byte[] Original()
    {
        var o = new byte[4 * 2 * 4];
        for (int x = 0; x < 4; x++) { o[x * 4] = 255; o[x * 4 + 3] = 255; o[(4 + x) * 4 + 2] = 255; o[(4 + x) * 4 + 3] = 255; }
        o[3 * 4 + 3] = 0;
        return o;
    }

    /// <summary>Its 2x upscale as a picture decodes (top-down): blue rows on top, red below, with no alpha at all.</summary>
    private static byte[] Upright2x()
    {
        var p = new byte[8 * 4 * 4];
        for (int y = 0; y < 4; y++)
            for (int x = 0; x < 8; x++) { int i = (y * 8 + x) * 4; p[i + (y < 2 ? 2 : 0)] = 255; p[i + 3] = 255; }
        return p;
    }

    private static string Temp() => Directory.CreateTempSubdirectory("hdtex").FullName;

    [Fact]
    public void ScaleIsAWholeMultipleTheSameBothWays()
    {
        Assert.Equal(2, HdTexture.ScaleOf(8, 4, 4, 2));
        Assert.Equal(4, HdTexture.ScaleOf(512, 256, 128, 64));
        Assert.Equal(1, HdTexture.ScaleOf(4, 2, 4, 2));
        Assert.Equal(3, HdTexture.ScaleOf(192, 192, 64, 64));
        Assert.Equal(8, HdTexture.ScaleOf(2048, 1024, 256, 128));
        Assert.Equal(0, HdTexture.ScaleOf(8, 2, 4, 2));
        Assert.Equal(0, HdTexture.ScaleOf(9, 4, 4, 2));
        Assert.Equal(0, HdTexture.ScaleOf(192, 96, 64, 64));
        Assert.Equal(0, HdTexture.ScaleOf(0, 0, 4, 2));
        Assert.Equal(0, HdTexture.ScaleOf(8, 4, 0, 0));
    }

    [Fact]
    public void FitFlipsToTheStoredOrderAndTakesTheOriginalsKey()
    {
        var f = HdTexture.Fit(Upright2x(), 8, 4, 2, Original(), 4, 2, bottomUp: true);
        // stored row 0 (the bottom of the picture) is red, its 2x2 block over the original's keyed texel alpha 0
        Assert.Equal(255, f[0]); Assert.Equal(0, f[2]); Assert.Equal(255, f[3]);
        Assert.Equal(0, f[(0 * 8 + 6) * 4 + 3]); Assert.Equal(0, f[(1 * 8 + 7) * 4 + 3]); Assert.Equal(255, f[(0 * 8 + 5) * 4 + 3]);
        // stored row 3 (the top of the picture) is blue, opaque
        Assert.Equal(255, f[(3 * 8 + 7) * 4 + 2]); Assert.Equal(255, f[(3 * 8 + 7) * 4 + 3]);
        // a .vqm keeps the picture's order
        var v = HdTexture.Fit(Upright2x(), 8, 4, 2, Original(), 4, 2, bottomUp: false);
        Assert.Equal(255, v[2]);
    }

    [Fact]
    public void AThreeTimesPictureKeysThreeByThreeBlocks()
    {
        var p = new byte[12 * 6 * 4];
        for (int y = 0; y < 6; y++)
            for (int x = 0; x < 12; x++) { int i = (y * 12 + x) * 4; p[i + (y < 3 ? 2 : 0)] = 255; p[i + 3] = 255; }
        var f = HdTexture.Fit(p, 12, 6, 3, Original(), 4, 2, bottomUp: true);
        // the original's keyed texel (stored row 0, column 3) covers stored rows 0..2, columns 9..11, and nothing else
        for (int y = 0; y < 6; y++)
            for (int x = 0; x < 12; x++)
                Assert.Equal(y < 3 && x >= 9 ? 0 : 255, f[(y * 12 + x) * 4 + 3]);
    }

    [Fact]
    public void APicturesOwnAlphaIsTheKeyAtOneHalf()
    {
        var p = Upright2x();
        p[3] = 127; p[7] = 128;
        var f = HdTexture.Fit(p, 8, 4, 2, Original(), 4, 2, bottomUp: false);
        Assert.Equal(0, f[3]); Assert.Equal(255, f[7]);
        // the original's key is not used then
        Assert.Equal(255, f[(0 * 8 + 6) * 4 + 3]);
    }

    [Fact]
    public void AKeyedTexelTakesTheBledOriginalsColourWhateverThePackPutThere()
    {
        var orig = Original();
        orig[3 * 4] = 10; orig[3 * 4 + 1] = 20; orig[3 * 4 + 2] = 30;   // the colour bled under the original's keyed texel
        var g = HdTexture.Fit(Upright2x(), 8, 4, 2, orig, 4, 2, bottomUp: true);
        Assert.Equal(new byte[] { 10, 20, 30, 0 }, g[((0 * 8 + 6) * 4)..((0 * 8 + 6) * 4 + 4)]);
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, g[((0 * 8 + 5) * 4)..((0 * 8 + 5) * 4 + 4)]);
    }

    [Fact]
    public void EverySizedFolderIsReadLargestFirstElseThePlainOne()
    {
        Assert.Equal(["tex-world-8x", "tex-world-4x"], HdTexture.Folders(f => f is "tex-world-4x" or "tex-world-8x" or "tex-world"));
        Assert.Equal(["tex-world-3x"], HdTexture.Folders(f => f is "tex-world-3x"));
        Assert.Equal(["tex-world"], HdTexture.Folders(f => f is "tex-world" or "tex-world-16x"));
        Assert.Empty(HdTexture.Folders(_ => false));
    }

    [Fact]
    public void FoundByStemTheFirstFolderOfTheChainWinning()
    {
        Assert.Equal(["pp11ftf1.dds", "pp11ftf1.png", "pp11ftf1.webp", "pp11ftf1.jpg"], HdTexture.Candidates("PP11FTF1.M16"));
        Assert.Equal("zsy_.dds", HdTexture.Candidates("sub\\ZSY_.map").First());
        Assert.Equal("zsy_.dds", HdTexture.Candidates("I76.ZFS:ZSY_.map").First());
        var dir = Temp();
        string d8 = Directory.CreateDirectory(Path.Combine(dir, "tex-world-8x")).FullName, d4 = Directory.CreateDirectory(Path.Combine(dir, "tex-world-4x")).FullName;
        File.WriteAllText(Path.Combine(d8, "ZSY_.dds"), "8x");
        File.WriteAllText(Path.Combine(d4, "zsy_.dds"), "4x");
        File.WriteAllText(Path.Combine(d4, "pp11ftf1.png"), "4x png");
        Directory.CreateDirectory(Path.Combine(d4, "old"));
        File.WriteAllText(Path.Combine(d4, "old", "xx.dds"), "not read");
        using var chain = new HdChainSource([new HdFlatDirSource(d8), new HdFlatDirSource(d4)]);
        Assert.Equal("8x", Encoding.UTF8.GetString(chain.Read(HdTexture.Candidates("zsy_.map").First(chain.Exists))!));
        Assert.Equal("4x png", Encoding.UTF8.GetString(chain.Read(HdTexture.Candidates("PP11FTF1.M16").First(chain.Exists))!));
        // a folder's own files only, each once
        Assert.False(chain.Exists("xx.dds"));
        Assert.Equal(3, chain.Count);
        Assert.Contains(" over ", chain.Origin);
    }

    [Fact]
    public void APackFileIsReadFromItsWorldFolderAndTheMenuPacksAreNot()
    {
        var dir = Temp();
        var zip = Path.Combine(dir, "i76-hd-textures-1.0.zip");
        using (var z = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            void Put(string n, string t) { using var w = z.CreateEntry(n).Open(); w.Write(Encoding.UTF8.GetBytes(t)); }
            Put("pack.json", "{\"name\":\"T\",\"version\":\"1.0\"}");
            Put("tex-world/PP11FTF1.dds", "hd");
            Put("tex-menu/shellbg1.webp", "menu");
        }
        using var world = HdPackFinder.Newest([dir], null, "i76-hd-textures*.zip", HdTexture.Folder + "/");
        Assert.Equal(1, world!.Count);
        Assert.Equal("hd", Encoding.UTF8.GetString(world.Read("pp11ftf1.dds")!));
        // the menu art's finder still looks for its own name only
        Assert.Null(HdPackFinder.Newest([dir]));
    }
}
