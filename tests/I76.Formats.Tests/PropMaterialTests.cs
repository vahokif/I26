using I76.Formats.Imaging;
using Xunit;

namespace I76.Formats.Tests;

/// <summary>PropMaterial on made-up textures: the classes it must get right, and the mistakes it must not make.</summary>
public class PropMaterialTests
{
    private const int W = 32, H = 32;

    private static byte[] Texture(Func<int, int, (byte R, byte G, byte B)> px)
    {
        var p = new byte[W * H * 4];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                var (r, g, b) = px(x, y);
                int i = (y * W + x) * 4;
                (p[i], p[i + 1], p[i + 2], p[i + 3]) = (r, g, b, 255);
            }
        return p;
    }

    private static (float Rough, float Metal, float Spec) At(PropMaterial.Maps m, int x, int y)
    {
        int i = (y * m.Width + x) * 4;
        return (m.Orm[i] / 255f, m.Orm[i + 1] / 255f, m.Orm[i + 3] / 255f);
    }

    private static readonly Random Noise = new(7);
    private static byte Jitter(int v, int by) => (byte)Math.Clamp(v + Noise.Next(-by, by + 1), 0, 255);

    [Fact]
    public void AdobeIsRoughAndNeverMetal()
    {
        var tex = Texture((x, y) => (Jitter(196, 12), Jitter(150, 12), Jitter(105, 12)));
        var m = PropMaterial.Build(tex, W, H, "HF_1WLF1");
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                var (rough, metal, _) = At(m, x, y);
                Assert.True(rough > 0.8f && metal < 0.02f, $"{x},{y}: rough {rough} metal {metal}");
            }
    }

    [Fact]
    public void AWindowInAWhiteWallIsGlass()
    {
        // a white wall, a 12×12 cyan-blue pane painted with a streak in it
        var tex = Texture((x, y) => x is >= 10 and < 22 && y is >= 10 and < 22
            ? ((byte)(30 + (x % 3) * 8), (byte)(80 + y * 2), (byte)(105 + y * 2)) : (Jitter(235, 6), Jitter(235, 6), Jitter(230, 6)));
        var m = PropMaterial.Build(tex, W, H, "HG_1BDF1");
        var (rough, metal, spec) = At(m, 16, 16);
        Assert.True(rough < 0.15f && spec > 0.8f && metal < 0.02f, $"pane: rough {rough} spec {spec} metal {metal}");
        var wall = At(m, 3, 3);
        Assert.True(wall.Rough > 0.45f && wall.Spec < 0.6f, $"wall: rough {wall.Rough} spec {wall.Spec}");
    }

    [Fact]
    public void ASignHasNoWindows()
    {
        var tex = Texture((x, y) => x is >= 10 and < 22 ? ((byte)40, (byte)120, (byte)160) : ((byte)240, (byte)240, (byte)240));
        var m = PropMaterial.Build(tex, W, H, "SD1XIN_1");
        Assert.True(At(m, 16, 16).Spec < 0.7f);
        Assert.True(At(m, 16, 16).Rough > 0.3f);
    }

    [Fact]
    public void AFlatNavyPanelIsPaintNotGlass()
    {
        var tex = Texture((x, y) => ((byte)10, (byte)40, (byte)110));
        var m = PropMaterial.Build(tex, W, H, "CA_1BLB1");
        var (rough, _, spec) = At(m, 5, 5);
        Assert.True(rough > 0.4f && spec < 0.7f, $"rough {rough} spec {spec}");
    }

    [Fact]
    public void CorrugatedGreySheetIsMetal()
    {
        // vertical corrugations, a period of 4 texels
        var tex = Texture((x, y) => { int v = (x % 4) switch { 0 => 120, 1 => 165, 2 => 190, _ => 150 }; var b = Jitter(v, 3); return (b, b, (byte)(b + 4)); });
        var m = PropMaterial.Build(tex, W, H, "CW_1BDS1");
        Assert.True(At(m, 5, 5).Metal > 0.5f, $"metal {At(m, 5, 5).Metal}");
    }

    [Fact]
    public void PlainConcreteIsNotMetal()
    {
        var tex = Texture((x, y) => { var b = Jitter(150, 14); return (b, b, b); });
        var m = PropMaterial.Build(tex, W, H, "CB_1BDT1");
        Assert.True(At(m, 5, 5).Metal < 0.05f && At(m, 5, 5).Rough > 0.8f);
    }

    [Fact]
    public void FamiliesFromDefinitions()
    {
        Assert.Equal(PropMaterial.Family.Wreck, PropMaterial.FamilyOf("akwreck2.sdf"));
        Assert.Equal(PropMaterial.Family.Plant, PropMaterial.FamilyOf("nsaguar1.sdf"));
        Assert.Equal(PropMaterial.Family.General, PropMaterial.FamilyOf("bhbarn1.sdf"));
        Assert.True(PropMaterial.IsSign("SH138W_1"));
        Assert.False(PropMaterial.IsSign("SANDY"));
        Assert.True(PropMaterial.IsBuildingSign("AO_1SG_3"));
        Assert.True(PropMaterial.IsBuildingSign("CA_1N021"));
        Assert.False(PropMaterial.IsBuildingSign("HG_1BDF1"));
    }

    [Fact]
    public void ADriverIsClothAndSkin()
    {
        Assert.True(PropMaterial.IsFigure("AG11DRVR"));
        Assert.True(PropMaterial.IsFigure("agc1drvr"));
        Assert.False(PropMaterial.IsFigure("AG11DASH"));
        Assert.False(PropMaterial.IsFigure("DRVR"));
        // white and a vivid colour, which on a prop would be paint: matte on a figure, never metal or glass
        foreach (var (r, g, b) in new (byte, byte, byte)[] { (240, 240, 240), (200, 40, 40), (186, 163, 108), (128, 128, 128) })
        {
            var (rough, metal, spec) = PropMaterial.ForColour(r, g, b, PropMaterial.Family.Figure);
            Assert.True(rough >= 0.85f && metal == 0 && spec <= 0.35f, $"{r},{g},{b}: {rough} {metal} {spec}");
        }
        // a grey, window-blue and streaked texture: no glass, no metal
        const int W = 16, H = 16;
        var tex = new byte[W * H * 4];
        for (int i = 0; i < W * H; i++)
        {
            bool blue = i % W < 8;
            tex[i * 4] = (byte)(blue ? 60 : 130 + (i % 3) * 20); tex[i * 4 + 1] = (byte)(blue ? 110 : 130 + (i % 3) * 20);
            tex[i * 4 + 2] = (byte)(blue ? 140 : 130 + (i % 3) * 20); tex[i * 4 + 3] = 255;
        }
        var m = PropMaterial.Build(tex, W, H, "AG11CM_1", PropMaterial.Family.Figure);
        for (int y = 0; y < H; y += 5)
            for (int x = 0; x < W; x += 5)
                Assert.True(At(m, x, y).Metal < 0.01f && At(m, x, y).Rough > 0.8f, $"{x},{y}");
    }

    [Fact]
    public void ACactusIsLeavesNotPaint()
    {
        var (rough, _, spec) = PropMaterial.ForColour(60, 140, 50, PropMaterial.Family.Plant);
        Assert.True(rough >= 0.75f && spec <= 0.3f);
    }
}
