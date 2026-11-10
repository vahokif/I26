using I76.Formats.Imaging;
using Xunit;

namespace I76.Formats.Tests;

/// <summary>A pack's upscale in a mission whose palette turns grey what the pack's palette colours (the dream): turned grey too.</summary>
public class HdTextureGreyTests
{
    /// <summary>The pack's palette: entry 10 brown, 11 a darker brown, 20 yellow, the rest black.</summary>
    private static Palette Pack()
    {
        var p = new byte[Palette.Size];
        Set(p, 10, 170, 133, 107); Set(p, 11, 122, 64, 22); Set(p, 20, 230, 200, 40);
        return new Palette(p);
    }

    /// <summary>The dream's: 10 and 11 grey ramp entries, 20 kept yellow, 30 grey in both.</summary>
    private static Palette Dream()
    {
        var p = new byte[Palette.Size];
        Set(p, 10, 168, 168, 168); Set(p, 11, 138, 138, 138); Set(p, 20, 230, 200, 40);
        return new Palette(p);
    }

    private static void Set(byte[] p, int i, byte r, byte g, byte b) { p[i * 3] = r; p[i * 3 + 1] = g; p[i * 3 + 2] = b; }

    [Fact]
    public void GreyedAreTheEntriesTheMissionGreysAndThePackColours()
    {
        var g = HdTexture.Greyed(Dream(), Pack())!;
        Assert.True(g[10]);
        Assert.True(g[11]);
        Assert.False(g[20]);   // coloured in both
        Assert.False(g[30]);   // black (grey) in both
        Assert.Null(HdTexture.Greyed(Pack(), Pack()));
        Assert.True(HdTexture.Uses([20, 11], g));
        Assert.False(HdTexture.Uses([20, 30], g));
    }

    [Fact]
    public void AnUpscaleOverAGreyedEntryTakesTheMissionsGreyWithItsOwnDetail()
    {
        var pack = Pack(); var dream = Dream();
        var g = HdTexture.Greyed(dream, pack)!;
        // a 2x1 original: entry 10 then the yellow 20; its 2x upscale is the pack's colours, one texel of entry 10 brightened by 20
        byte[] idx = [10, 20];
        var up = new byte[4 * 2 * 4];
        for (int y = 0; y < 2; y++)
            for (int x = 0; x < 4; x++)
            {
                var (r, gg, b) = pack[idx[x / 2]];
                int o = (y * 4 + x) * 4;
                up[o] = r; up[o + 1] = gg; up[o + 2] = b; up[o + 3] = 255;
            }
        up[0] += 20; up[1] += 20; up[2] += 20;
        var yellow = up[(2 * 4)..(3 * 4)];
        HdTexture.Recolour(up, 4, 2, 2, idx, 2, 1, g, dream, pack);
        // entry 10's texels are grey: the mission's 168 where the upscale is the pack's colour, 20 brighter where it was brighter
        Assert.Equal(new byte[] { 168, 168, 168, 255 }, up[(1 * 4)..(2 * 4)]);
        Assert.Equal(new byte[] { 188, 188, 188, 255 }, up[0..4]);
        // the yellow entry is left as the upscale has it
        Assert.Equal(yellow, up[(2 * 4)..(3 * 4)]);
    }

    [Fact]
    public void TheGreyFollowsTheRampBetweenTexelsWithNoStep()
    {
        var pack = Pack(); var dream = Dream();
        var g = HdTexture.Greyed(dream, pack)!;
        // two greyed entries side by side at 4x: the offsets (mission grey over the pack colour's luminance) are blended between texel centres
        byte[] idx = [10, 11];
        var up = new byte[8 * 1 * 4];
        for (int x = 0; x < 8; x++) { int o = x * 4; up[o] = up[o + 1] = up[o + 2] = 100; up[o + 3] = 255; }
        HdTexture.Recolour(up, 8, 1, 4, idx, 2, 1, g, dream, pack);
        var row = Enumerable.Range(0, 8).Select(x => (int)up[x * 4]).ToArray();
        for (int x = 1; x < 8; x++) Assert.True(row[x] >= row[x - 1], string.Join(",", row));   // entry 11's offset is the larger
        Assert.True(row[4] - row[3] < row[7] - row[0]);   // no jump at the texel edge
    }
}
