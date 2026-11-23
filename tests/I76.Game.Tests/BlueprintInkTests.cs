using I76.Game.Shell;
using Xunit;

namespace I76.Game.Tests;

/// <summary>
/// The car blueprints on the drawn host form's sheet: each pixel darkens the sheet as it darkens the art's own paper about it, so
/// faint strokes and shading stay, the art's paper (and its grain) leaves the sheet, and solid ink comes out as the art's ink.
/// </summary>
public class BlueprintInkTests
{
    // a palette of the form's tones: the art's paper and its grain, a faint stroke, shading, ink; the sheet's paper; and greys to quantize into
    private const byte ArtPaper = 1, ArtGrain = 2, Faint = 3, Shade = 4, Ink = 5, Sheet = 6;
    private static readonly byte[] Pal = MakePalette();

    private static byte[] MakePalette()
    {
        var p = new byte[768];
        void Set(int i, int r, int g, int b) { p[i * 3] = (byte)r; p[i * 3 + 1] = (byte)g; p[i * 3 + 2] = (byte)b; }
        Set(ArtPaper, 239, 189, 99);   // the side views' commonest paper
        Set(ArtGrain, 231, 181, 90);   // a darker grain of it (4% down)
        Set(Faint, 219, 173, 79);      // a faint stroke's tone: brighter than the old fixed cut, darker than the paper
        Set(Shade, 177, 141, 74);
        Set(Ink, 74, 57, 33);
        Set(Sheet, 247, 198, 107);     // the sheet: a lighter paper than the art's
        // every colour of a coarse cube, so a blend lands near itself
        int k = 16;
        for (int r = 0; r < 6 && k < 256; r++)
            for (int g = 0; g < 6 && k < 256; g++)
                for (int b = 0; b < 6 && k < 256; b++) Set(k++, r * 51, g * 51, b * 51);
        // and a ramp of the paper darkened, as the shell's palette has its browns
        for (int s = 0; s < 24 && k < 256; s++, k++) Set(k, 247 * (24 - s) / 24, 198 * (24 - s) / 24, 107 * (24 - s) / 24);
        return p;
    }

    /// <summary>A 40 x 20 art of paper with a few grain pixels, and a vertical line of <paramref name="stroke"/> at column 20.</summary>
    private static (byte[] Px, bool[] Opaque) Art(byte stroke)
    {
        var px = new byte[40 * 20];
        Array.Fill(px, ArtPaper);
        for (int i = 3; i < px.Length; i += 7) px[i] = ArtGrain;
        for (int y = 0; y < 20; y++) px[y * 40 + 20] = stroke;
        var opaque = new bool[px.Length];
        Array.Fill(opaque, true);
        return (px, opaque);
    }

    private static byte[] OnSheet(byte stroke)
    {
        var (px, op) = Art(stroke);
        var under = new byte[px.Length];
        Array.Fill(under, Sheet);
        return BlueprintInk.Composite(px, op, 40, 20, under, Pal);
    }

    private static float Lum(byte i) => Pal[i * 3] * 0.299f + Pal[i * 3 + 1] * 0.587f + Pal[i * 3 + 2] * 0.114f;

    [Fact]
    public void TheArtsPaperAndItsGrainLeaveTheSheet()
    {
        var o = OnSheet(Ink);
        for (int y = 0; y < 20; y++)
            for (int x = 0; x < 40; x++)
                if (x != 20) Assert.Equal(Sheet, o[y * 40 + x]);
    }

    [Fact]
    public void AFaintStrokeStays()
    {
        // brighter than 160 (the old cut dropped it), about 9% under the paper: drawn, darker than the sheet
        Assert.True(Lum(Faint) > 160);
        var o = OnSheet(Faint);
        for (int y = 0; y < 20; y++)
        {
            Assert.NotEqual(Sheet, o[y * 40 + 20]);
            Assert.True(Lum(o[y * 40 + 20]) < Lum(Sheet) - 4);
        }
    }

    [Fact]
    public void ShadingAndInkKeepTheirDepth()
    {
        // shading and ink darken the sheet by what they darken the art's paper: within a few levels of the art's own tone scaled to the sheet
        foreach (var tone in new[] { Shade, Ink })
        {
            var o = OnSheet(tone)[5 * 40 + 20];
            float want = Lum(tone) / Lum(ArtPaper) * Lum(Sheet);
            Assert.InRange(Lum(o), want - 8, want + 8);
        }
    }

    [Fact]
    public void TransparentPixelsAreSkipped()
    {
        var (px, op) = Art(Ink);
        Array.Fill(op, false);
        var under = new byte[px.Length];
        Array.Fill(under, Sheet);
        Assert.Equal(under, BlueprintInk.Composite(px, op, 40, 20, under, Pal));
    }

    [Fact]
    public void TheWeightRampsFromPaperToStroke()
    {
        Assert.Equal(0f, BlueprintInk.Weight(0.97f * 200, 200));
        Assert.Equal(1f, BlueprintInk.Weight(0.80f * 200, 200));
        Assert.InRange(BlueprintInk.Weight(0.90f * 200, 200), 0.45f, 0.55f);
    }
}
