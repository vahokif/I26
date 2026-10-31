using I76.Formats.Imaging;
using Xunit;

namespace I76.Formats.Tests;

/// <summary>The binoculars' frame (bmask*.msk): run-length words, bit 15 a masked run.</summary>
public class BinocularMaskTests
{
    [Fact]
    public void Runs_fill_masked_pixels_and_stop_at_zero()
    {
        byte[] data = [0x03, 0x80, 0x02, 0x00, 0x05, 0x80, 0x00, 0x00, 0x09, 0x80];
        var m = BinocularMask.Decode(data, 4, 3);
        Assert.Equal(new byte[] { 1, 1, 1, 0, 0, 1, 1, 1, 1, 1, 0, 0 }, m);
    }

    [Fact]
    public void A_run_past_the_end_is_clipped()
    {
        var m = BinocularMask.Decode([0xff, 0xff], 2, 2);
        Assert.Equal(new byte[] { 1, 1, 1, 1 }, m);
    }

    [InstallFact]
    public void Stock_masks_cover_their_screens_with_two_circles()
    {
        foreach (var (file, w, h) in BinocularMask.Files)
        {
            var data = TestInstall.Vfs.Read(file);
            // the runs add up to exactly the screen
            int total = 0;
            for (int i = 0; i + 1 < data.Length; i += 2) { int v = data[i] | data[i + 1] << 8; if (v == 0) break; total += v & 0x7fff; }
            Assert.Equal(w * h, total);
            var m = BinocularMask.Decode(data, w, h);
            // 455 of bmask102's rows leave the last column clear (runs of 0x83ff, 1): the game shows that line
            int right = file == "bmask102.msk" ? w - 2 : w - 1;
            if (file == "bmask102.msk") Assert.Equal(455, Enumerable.Range(0, h).Count(y => m[y * w + w - 1] == 0));
            bool At(float fx, float fy) => m[(int)(fy * (h - 1)) * w + (int)(fx * right)] == 1;
            // black corners and top-centre notch between the circles; clear in each circle's middle and at the centre
            Assert.True(At(0, 0) && At(1, 0) && At(0, 1) && At(1, 1), file);
            Assert.True(At(0.5f, 0.07f), file);
            Assert.False(At(0.33f, 0.5f) || At(0.67f, 0.5f) || At(0.5f, 0.5f), file);
        }
    }

    [InstallFact]
    public void The_640_mask_reaches_rows_31_to_452_and_both_sides()
    {
        var m = BinocularMask.Decode(TestInstall.Vfs.Read("bmask640.msk"), 640, 480);
        bool RowClear(int y) => Enumerable.Range(0, 640).Any(x => m[y * 640 + x] == 0);
        Assert.False(RowClear(30));
        Assert.True(RowClear(31));
        Assert.True(RowClear(452));
        Assert.False(RowClear(453));
        Assert.Contains(Enumerable.Range(0, 480), y => m[y * 640] == 0);
        Assert.Contains(Enumerable.Range(0, 480), y => m[y * 640 + 639] == 0);
    }
}
