using I76.Formats.Imaging;
using System.Numerics;
using Xunit;

namespace I76.Formats.Tests;

public class CarMaskTests
{
    private static CarMaskMap.Entry Entry(string key, int w, int h, Func<int, int, byte> f)
    {
        var ids = new byte[w * h];
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) ids[y * w + x] = f(x, y);
        return new CarMaskMap.Entry(key, w, h, ids);
    }

    [Fact]
    public void MapsRoundTripAndKeyBySchemeDigit()
    {
        var m = new CarMaskMap();
        m.Add(Entry("CM11MDL?", 32, 16, (x, y) => (byte)(x < 10 ? 1 : y < 4 ? 11 : 5)));
        m.Add(Entry("hl?", 8, 8, (x, y) => 12));
        var ms = new MemoryStream();
        m.Write(ms);
        ms.Position = 0;
        var r = CarMaskMap.Read(ms);
        Assert.Equal(2, r.Entries.Count);
        Assert.Equal("cm11mdl?", CarMaskMap.KeyOf("CM11MDL3.m16"), ignoreCase: true);
        Assert.Equal("hl", CarMaskMap.KeyOf("HL.m16"), ignoreCase: true);
        Assert.Equal(m.Ids("CM11MDL1", 32, 16), r.Ids("cm11mdl4.m16", 32, 16));
        Assert.Null(r.Ids("cm11mdl4.m16", 16, 16));   // another size is another texture
        Assert.NotNull(r.Ids("HL1.m16", 8, 8));
    }

    [Fact]
    public void ShippedMapsParseAndHaveValidIds()
    {
        foreach (var e in CarMaskMap.Default.Entries)
        {
            Assert.Equal(e.W * e.H, e.Ids.Length);
            Assert.All(e.Ids, id => Assert.InRange(id, (byte)0, (byte)CarMaterialTable.Presets.Count));
        }
    }

    [Fact]
    public void ARoundTrippedMapKeepsUnseenTexelsAtZero()
    {
        Environment.SetEnvironmentVariable("I76_CAR_MASKS", "1");
        var tmp = Path.GetTempFileName();
        try
        {
            var m = new CarMaskMap();
            m.Add(Entry("zz9test?", 4, 2, (x, y) => (byte)(x < 2 ? 11 : 0)));
            using (var fs = File.Create(tmp)) m.Write(fs);
            // the default map is read once per process; read this one explicitly and apply it as Ids does
            using var rs = File.OpenRead(tmp);
            var back = CarMaskMap.Read(rs);
            var ids = back.Ids("ZZ9TEST1", 4, 2)!;
            Assert.Equal(new byte[] { 11, 11, 0, 0, 11, 11, 0, 0 }, ids);
        }
        finally { File.Delete(tmp); Environment.SetEnvironmentVariable("I76_CAR_MASKS", null); }
    }

    /// <summary>A unit box (+Z face textured 16x16 with a left/right split of labels, the other faces untextured), seen from +Z.</summary>
    private static (List<CarTri>, List<CarTexture>) Box()
    {
        var tex = new CarTexture("box.m16", 16, 16, new byte[16 * 16 * 3], new bool[16 * 16]);
        var a = new Vector3(-1, -1, 1); var b = new Vector3(1, -1, 1); var c = new Vector3(1, 1, 1); var d = new Vector3(-1, 1, 1);
        var tris = new List<CarTri>
        {
            new(a, b, c, new(0, 1), new(1, 1), new(1, 0), 0, false, default),
            new(a, c, d, new(0, 1), new(1, 0), new(0, 0), 0, false, default),
            new(new(-1, -1, -1), new(1, -1, -1), new(1, 1, -1), default, default, default, -1, false, new(10, 10, 10)),
        };
        return (tris, [tex]);
    }

    [Fact]
    public void ViewShowsTheRightTexelsAndBackProjectionRecoversTheLabels()
    {
        var (tris, tex) = Box();
        var cam = new CarCamera(new Vector3(0, 0, 1), Vector3.Zero, 1.2f);
        var v = CarView.Render(tris, tex, cam, 1.2f, 128);
        // the centre pixel shows the texture's middle; pixels left/right of centre show its left/right halves (up to the handedness the camera uses)
        int cx = 64, cy = 64;
        Assert.Equal(0, v.Tex[cy * 128 + cx]);
        Assert.InRange(v.Tx[cy * 128 + cx], 7, 8);
        // labels: class 11 on one half of the screen, 5 on the other
        var lab = new byte[128 * 128];
        for (int y = 0; y < 128; y++) for (int x = 0; x < 128; x++) lab[y * 128 + x] = (byte)(x < 64 ? 11 : 5);
        var p = new CarMaskProjector([(16, 16)], CarMaterialTable.Presets.Count + 1);
        p.Seen(v); p.Vote(v, lab);
        var r = p.Resolve(0);
        // the texture's two halves carry the two labels, each a whole column band, and every texel is seen
        var left = Enumerable.Range(0, 16).Select(y => r[y * 16 + 2]).Distinct().ToList();
        var right = Enumerable.Range(0, 16).Select(y => r[y * 16 + 13]).Distinct().ToList();
        Assert.Single(left); Assert.Single(right);
        Assert.NotEqual(left[0], right[0]);
        Assert.DoesNotContain((byte)255, r);
        Assert.All(new[] { left[0], right[0] }, l => Assert.Contains(l, new byte[] { 11, 5 }));
    }

    [Fact]
    public void ABetterViewOutvotesAGrazingOne()
    {
        var (tris, tex) = Box();
        var front = CarView.Render(tris, tex, new CarCamera(new Vector3(0, 0, 1), Vector3.Zero, 1.2f), 1.2f, 64);
        var grazing = CarView.Render(tris, tex, new CarCamera(Vector3.Normalize(new Vector3(1, 0, 0.35f)), Vector3.Zero, 1.2f), 1.2f, 64);
        var p = new CarMaskProjector([(16, 16)], CarMaterialTable.Presets.Count + 1);
        p.Seen(front); p.Seen(grazing);
        p.Vote(front, Enumerable.Repeat((byte)11, 64 * 64).ToArray());
        p.Vote(grazing, Enumerable.Repeat((byte)5, 64 * 64).ToArray());
        Assert.All(p.Resolve(0), l => Assert.Equal(11, l));
    }

    [Fact]
    public void SpecklesAreRemovedAndHolesFilled()
    {
        // a texture voted all 11 except one texel voted 5 and one never seen
        var (tris, tex) = Box();
        var v = CarView.Render(tris, tex, new CarCamera(new Vector3(0, 0, 1), Vector3.Zero, 1.2f), 1.2f, 256);
        var lab = Enumerable.Repeat((byte)11, 256 * 256).ToArray();
        int hit = Array.FindIndex(v.Tex, t => t == 0);
        for (int i = 0; i < v.Tex.Length; i++) if (v.Tex[i] == 0 && v.Tx[i] == 5 && v.Ty[i] == 5) lab[i] = 5;
        var p = new CarMaskProjector([(16, 16)], CarMaterialTable.Presets.Count + 1);
        p.Seen(v); p.Vote(v, lab);
        Assert.True(hit >= 0);
        Assert.All(p.Resolve(0), l => Assert.Equal(11, l));
    }

    [Fact]
    public void ASurfaceSeenFromBelowIsChassisSteelUnlessTheSegmenterNamedIt()
    {
        var (tris, tex) = Box();
        var v = CarView.Render(tris, tex, new CarCamera(new Vector3(0, 0, 1), Vector3.Zero, 1.2f), 1.2f, 64);
        var p = new CarMaskProjector([(16, 16)], CarMaterialTable.Presets.Count + 1) { UndersidePrior = true };
        p.Seen(v, cameraUp: -0.9f);
        p.Vote(v, Enumerable.Repeat((byte)0, 64 * 64).ToArray());
        Assert.All(p.Resolve(0), l => Assert.Equal(CarMaskProjector.UndersideClass, l));
        var q = new CarMaskProjector([(16, 16)], CarMaterialTable.Presets.Count + 1) { UndersidePrior = true };
        q.Seen(v, cameraUp: -0.9f);
        q.Vote(v, Enumerable.Repeat((byte)5, 64 * 64).ToArray());
        Assert.All(q.Resolve(0), l => Assert.Equal(5, l));
    }

    private static byte[] Map(int w, int h, Func<int, int, byte> f) { var a = new byte[w * h]; for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) a[y * w + x] = f(x, y); return a; }
    private static byte[] Grey(int w, int h, byte v = 100) => Enumerable.Repeat(v, w * h * 3).ToArray();

    [Fact]
    public void CleanerKeepsASolidWindowAndDropsSpecklesStripsAndHatching()
    {
        // a 20x14 window (kept), a 2x2 glass speck, a 40x4 glass strip, a 3x3 chrome speck
        var ids = Map(100, 40, (x, y) => (x, y) switch
        {
            _ when x >= 5 && x < 25 && y >= 5 && y < 19 => 11,
            _ when x >= 60 && x < 62 && y >= 5 && y < 7 => 11,
            _ when x >= 40 && x < 80 && y >= 30 && y < 34 => 11,
            _ when x >= 80 && x < 83 && y >= 5 && y < 8 => 5,
            _ => 1,
        }).Select(v => (byte)v).ToArray();
        var o = CarMaskCleaner.Clean(ids, Grey(100, 40), 100, 40);
        Assert.Equal(11, o[10 * 100 + 10]);
        Assert.Equal(1, o[5 * 100 + 60]);
        Assert.Equal(1, o[31 * 100 + 50]);
        Assert.Equal(1, o[6 * 100 + 81]);
    }

    [Fact]
    public void CleanerClosesTheGapsOfAHatchedWindowButNotAWideHole()
    {
        var ids = Map(60, 40, (x, y) => (byte)(x >= 5 && x < 45 && y >= 5 && y < 30 && x % 3 != 0 ? 11 : 1));
        var o = CarMaskCleaner.Clean(ids, Grey(60, 40), 60, 40);
        Assert.Equal(11, o[10 * 60 + 12]);   // a hatch bar is now glass
        Assert.Equal(1, o[20 * 60 + 55]);    // outside stays paint
    }

    [Fact]
    public void CleanerDropsBusyGlass()
    {
        var rgb = new byte[100 * 40 * 3];
        for (int i = 0; i < rgb.Length / 3; i++) { byte v = (byte)((i % 2 == 0) ? 30 : 230); rgb[i * 3] = rgb[i * 3 + 1] = rgb[i * 3 + 2] = v; }
        var ids = Map(100, 40, (x, y) => (byte)(x >= 5 && x < 80 && y >= 5 && y < 35 ? 11 : 1));
        var o = CarMaskCleaner.Clean(ids, rgb, 100, 40);
        Assert.Equal(1, o[20 * 100 + 20]);
    }
}
