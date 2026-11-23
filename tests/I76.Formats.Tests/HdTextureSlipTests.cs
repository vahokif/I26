using I76.Formats.Imaging;
using Xunit;

namespace I76.Formats.Tests;

/// <summary>A pack's upscale of a texture whose slip the modern look repairs (TextureRepairs) is repaired the same way (HdTexture.RepairSlip).</summary>
public class HdTextureSlipTests
{
    /// <summary>An 8x1 original of a dark grey, its first texel the slip's pure green; the repaired copy has the grey there too.</summary>
    private static (byte[] Original, byte[] Repaired) Pair()
    {
        var o = new byte[8 * 4];
        for (int x = 0; x < 8; x++) { o[x * 4] = o[x * 4 + 1] = o[x * 4 + 2] = 72; o[x * 4 + 3] = 255; }
        var r = (byte[])o.Clone();
        o[0] = 0; o[1] = 255; o[2] = 0;
        return (o, r);
    }

    /// <summary>A 2x upscale of the original as an upscaler leaves it: the green block, a greenish fringe next to it, grey with detail beyond.</summary>
    private static byte[] Upscale()
    {
        var u = new byte[16 * 2 * 4];
        for (int y = 0; y < 2; y++)
            for (int x = 0; x < 16; x++)
            {
                int i = (y * 16 + x) * 4;
                (u[i], u[i + 1], u[i + 2]) = x < 2 ? ((byte)10, (byte)240, (byte)10) : x < 4 ? ((byte)60, (byte)140, (byte)60) : ((byte)(70 + x), (byte)(70 + x), (byte)(70 + x));
                u[i + 3] = 255;
            }
        return u;
    }

    [Fact]
    public void TheSlipAndItsFringeTakeTheRepairedColourAndTheRestIsKept()
    {
        var (o, r) = Pair();
        var u = Upscale();
        var before = (byte[])u.Clone();
        int n = HdTexture.RepairSlip(u, 16, 2, 2, o, r, 8, 1);
        for (int y = 0; y < 2; y++)
            for (int x = 0; x < 16; x++)
            {
                int i = (y * 16 + x) * 4;
                Assert.Equal(255, u[i + 3]);
                if (x < 2) Assert.Equal(new byte[] { 72, 72, 72 }, u[i..(i + 3)]);              // over the slip: the repaired colour
                else if (x < 6)
                {
                    Assert.True(u[i] == u[i + 1] && u[i + 1] == u[i + 2]);                         // within two texels: the repaired (grey) tint
                    Assert.InRange(u[i], 72 - 24, 72 + 24);                                         // at its own lightness, held near the repaired one
                }
                else Assert.Equal(before[i..(i + 4)], u[i..(i + 4)]);                             // further off: untouched
            }
        Assert.True(n >= 8);
    }

    [Fact]
    public void AnUpscaleWithoutTheSlipKeepsItsLightness()
    {
        var (o, r) = Pair();
        var u = Upscale();
        for (int y = 0; y < 2; y++)
            for (int x = 0; x < 4; x++) { int i = (y * 16 + x) * 4; u[i] = u[i + 1] = u[i + 2] = 72; }   // a pack already repaired
        var before = (byte[])u.Clone();
        HdTexture.RepairSlip(u, 16, 2, 2, o, r, 8, 1);
        Assert.Equal(before, u);
    }

    [Fact]
    public void NothingChangesWithoutARepair()
    {
        var (o, _) = Pair();
        var u = Upscale();
        var before = (byte[])u.Clone();
        Assert.Equal(0, HdTexture.RepairSlip(u, 16, 2, 2, o, o, 8, 1));
        Assert.Equal(before, u);
    }
}
