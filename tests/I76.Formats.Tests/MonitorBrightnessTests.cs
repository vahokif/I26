using I76.Formats.Imaging;
using Xunit;

namespace I76.Formats.Tests;

public class MonitorBrightnessTests
{
    /// <summary>The original's tables at mid grey: 128 → 107, 118 for 1, 2, the identity at 3, 143 … 193 above.</summary>
    [Theory]
    [InlineData(1, 107)]
    [InlineData(2, 118)]
    [InlineData(3, 128)]
    [InlineData(4, 143)]
    [InlineData(5, 155)]
    [InlineData(6, 165)]
    [InlineData(7, 173)]
    [InlineData(8, 180)]
    [InlineData(9, 186)]
    [InlineData(10, 193)]
    public void ThePaletteCurveIsTheEngines(int level, int at128) => Assert.Equal(at128, MonitorBrightness.PaletteCurve(level)[128]);

    [Fact]
    public void LevelThreeIsTheIdentityAndOutOfRangeIsLevelThree()
    {
        byte[] id = MonitorBrightness.PaletteCurve(3);
        for (int i = 0; i < 256; i++) Assert.Equal(i, id[i]);
        Assert.Equal(id, MonitorBrightness.PaletteCurve(0));
        Assert.Equal(id, MonitorBrightness.PaletteCurve(11));
        Assert.Equal(1.0, MonitorBrightness.PaletteGamma(3));
    }

    /// <summary>The curves are monotonic and keep black and white: truncation (ftol) leaves 255 at 255.</summary>
    [Fact]
    public void CurvesKeepTheEnds()
    {
        for (int level = 1; level <= 10; level++)
        {
            byte[] p = MonitorBrightness.PaletteCurve(level), c = MonitorBrightness.CardCurve(level);
            Assert.Equal(0, p[0]); Assert.Equal(255, p[255]);
            Assert.Equal(0, c[0]); Assert.Equal(255, c[255]);
            for (int i = 1; i < 256; i++) { Assert.True(p[i] >= p[i - 1]); Assert.True(c[i] >= c[i - 1]); }
        }
    }

    /// <summary>
    /// The 3D card at the shipped level is the Glide driver's default ramp, 255·(i/255)^(1/1.3) + 0.5 (grSstWinOpen
    /// → grGammaCorrectionValue(GLIDE_DEFAULT_GAMMA)); the other levels compose the palette curve under it, so level
    /// 1 comes out near the bare palette (γ 0.8·1.3 = 1.04, as the Direct3D and software renderers showed it).
    /// </summary>
    [Theory]
    [InlineData(3, 64, 88)]      // 255·(64/255)^(1/1.3) = 88.0
    [InlineData(3, 128, 150)]    // 150.4
    [InlineData(3, 200, 212)]    // 211.7
    public void TheCardCurveAtTheShippedLevelIsTheGlideRamp(int level, int i, int expected)
    {
        double ramp = Math.Floor(Math.Pow(i / 255.0, 1 / 1.3) * 255 + 0.5);
        Assert.Equal(ramp, MonitorBrightness.CardCurve(level)[i]);
        Assert.Equal(expected, MonitorBrightness.CardCurve(level)[i]);
    }

    [Fact]
    public void LevelOneIsAboutTheBarePalette()
    {
        byte[] c = MonitorBrightness.CardCurve(1);
        for (int i = 0; i < 256; i++) Assert.InRange(c[i] - i, -4, 4);
    }
}
