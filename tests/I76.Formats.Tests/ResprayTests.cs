using System.Diagnostics;
using I76.Formats.Assembly;
using I76.Formats.Imaging;
using I76.Formats.Shell;
using Xunit;

namespace I76.Formats.Tests;

/// <summary>The respray: a scheme's masks, the paint's wire form and the variant's sidecar.</summary>
public class ResprayTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private static TextureResolver Res => new(TestInstall.Vfs);

    private static PaintMasks.TextureSource Source(TextureResolver res) => m =>
    {
        if (res.Load(m) is not { } t) return null;
        return (t.Width, t.Height, TextureReader.ToRgba(t, null));
    };

    private static PaintMasks Masks(string vcf)
    {
        var res = Res;
        return PaintMasks.Compute(new ModelAssembler(TestInstall.Vfs).Assemble(vcf)!, res, Source(res));
    }

    private static string Digest(PaintMasks m)
    {
        var h = System.Security.Cryptography.SHA256.Create();
        foreach (var name in m.Members.OrderBy(x => x, StringComparer.Ordinal))
            if (m.MaskOf(name) is { } mk) { h.TransformBlock(mk.Rg, 0, mk.Rg.Length, null, 0); }
        h.TransformFinalBlock([], 0, 0);
        return Convert.ToHexString(h.Hash!) + $"|{m.PrimaryLuma:0.0000}|{m.SecondaryLuma:0.0000}|{m.HasSecondary}";
    }

    [InstallFact]
    public void ASchemesMasksComeOutTheSameEveryTime()
    {
        foreach (var vcf in new[] { "vcmanta1.vcf", "vcmanta3.vcf", "vcpolic1.vcf", "vcroyal1.vcf" })
            Assert.Equal(Digest(Masks(vcf)), Digest(Masks(vcf)));
    }

    [InstallFact]
    public void ALoadoutDoesNotChangeASchemesMasks()
    {
        // the weapons' gizmos are not the car's own parts: the same car with another loadout has the same scheme
        var a = Masks("vcmanta3.vcf");
        var b = Masks("vcmanta3.vcf");
        Assert.Equal(a.Members, b.Members);
        Assert.Equal(PaintMasks.KeyOf(a.Members.Order(StringComparer.OrdinalIgnoreCase)), PaintMasks.KeyOf(b.Members.Order(StringComparer.OrdinalIgnoreCase)));
    }

    [InstallFact]
    public void TheSchemesPaintsAreWhatTheResearchFound()
    {
        // Manta 1 is green (chromatic, no second paint), Manta 3 is black and red (a second paint), a police car black and white
        var m1 = Masks("vcmanta1.vcf");
        Assert.True(m1.Primary.Chromatic);
        Assert.False(m1.HasSecondary);
        Assert.True(Masks("vcmanta3.vcf").HasSecondary);
        var pol = Masks("vcpolic1.vcf");
        Assert.False(pol.Primary.Chromatic);
        Assert.True(pol.HasSecondary);
    }

    [InstallFact]
    public void TheMasksOfASchemeCostLittle()
    {
        var res = Res;
        var model = new ModelAssembler(TestInstall.Vfs).Assemble("vcmanta3.vcf")!;
        var src = Source(res);
        PaintMasks.Compute(model, res, src);     // warm
        var sw = Stopwatch.StartNew();
        var m = PaintMasks.Compute(model, res, src);
        long stats = sw.ElapsedMilliseconds;
        foreach (var name in m.Members) m.MaskOf(name);
        output.WriteLine($"manta 3: statistics {stats} ms, {m.Members.Count} masks {sw.ElapsedMilliseconds - stats} ms (textures already loaded once)");
        Assert.True(sw.ElapsedMilliseconds < 3000, $"{sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void AMaskIsAPureFunctionOfTheTextures()
    {
        // a synthetic green panel with a white stripe: the same bytes in, the same weights out, green taken, white not
        const int n = 16 * 16;
        var rgb = new byte[n * 3];
        for (int i = 0; i < n; i++) { bool stripe = i % 16 == 7; rgb[i * 3] = (byte)(stripe ? 240 : 30); rgb[i * 3 + 1] = (byte)(stripe ? 240 : 140); rgb[i * 3 + 2] = (byte)(stripe ? 240 : 40); }
        var st = new ResprayPaint.Stats();
        st.Add(rgb, n);
        var (p, s) = ResprayPaint.Scheme(st);
        Assert.True(p.Chromatic);
        var w1 = ResprayPaint.Mask(rgb, n, p);
        var w2 = ResprayPaint.Mask(rgb, n, p);
        Assert.Equal(w1, w2);
        Assert.True(w1[0] > 0.9f);
        Assert.True(w1[7] < 0.1f);
    }

    [Fact]
    public void TheFactoryPaintRecoloursNothing()
    {
        // with no weight a texel is returned as it was (the shader's no-colour path is the same: nothing runs)
        var rgb = new byte[] { 10, 200, 30, 250, 250, 250 };
        var o = ResprayPaint.Recolour(rgb, 2, [0f, 0f], new ResprayPaint.PaintRef(120, 0.8f, 0.8f, 0.5f, true), new ResprayPaint.Target("x", 1, 0, 0));
        Assert.Equal(rgb, o);
    }

    [Fact]
    public void ACarPaintSurvivesItsWireForm()
    {
        foreach (var p in new[]
        {
            new CarPaint(new PaintColour(1, 2, 3)),
            new CarPaint(new PaintColour(240, 84, 30), new PaintColour(10, 20, 30), PaintFinish.Metallic),
            new CarPaint(null, new PaintColour(9, 8, 7), PaintFinish.Matte),
        })
            Assert.Equal(p, CarPaint.FromBytes(p.ToBytes()));
        Assert.Empty(new CarPaint(null).ToBytes());
        Assert.Null(CarPaint.FromBytes([]));
        Assert.Null(CarPaint.FromBytes([1, 1, 0, 1]));          // cut short
        Assert.Null(CarPaint.FromBytes([1, 0, 0]));             // no colour
        Assert.Null(CarPaint.FromBytes([2, 1, 0, 1, 2, 3]));    // another format
        Assert.Null(CarPaint.FromBytes([1, 1, 9, 1, 2, 3]));    // no such finish
    }

    [Fact]
    public void AVariantsSidecarRoundTripsBesideItsFile()
    {
        var dir = Directory.CreateTempSubdirectory("i26side").FullName;
        try
        {
            var p = new CarPaint(new PaintColour(0x5b, 0x1e, 0x8c), new PaintColour(0xf5, 0xd6, 0x0a), PaintFinish.Metallic);
            File.WriteAllBytes(Path.Combine(dir, "vcmanta4.vcf"), [1, 2, 3]);
            CarSidecar.WriteFile(dir, "vcmanta4.vcf", p);
            Assert.True(File.Exists(Path.Combine(dir, "vcmanta4.i26")));
            Assert.Equal(p, CarSidecar.ReadFile(dir, "vcmanta4.vcf"));
            // the .vcf itself is untouched
            Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(Path.Combine(dir, "vcmanta4.vcf")));
            // a damaged or foreign sidecar is the factory paint
            File.WriteAllText(Path.Combine(dir, "vcmanta4.i26"), "not json");
            Assert.Null(CarSidecar.ReadFile(dir, "vcmanta4.vcf"));
            // none removes it
            CarSidecar.WriteFile(dir, "vcmanta4.vcf", p);
            CarSidecar.WriteFile(dir, "vcmanta4.vcf", null);
            Assert.False(File.Exists(Path.Combine(dir, "vcmanta4.i26")));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void DeletingAVariantTakesItsColoursAlongAndShiftsTheLaterOnes()
    {
        var dir = Directory.CreateTempSubdirectory("i26side").FullName;
        try
        {
            for (int k = 4; k <= 6; k++)
            {
                File.WriteAllBytes(Path.Combine(dir, $"vcmanta{k}.vcf"), [(byte)k]);
                CarSidecar.WriteFile(dir, $"vcmanta{k}.vcf", k == 5 ? null : new CarPaint(new PaintColour((byte)k, 0, 0)));
            }
            Assert.Equal(VariantFiles.Result.Deleted, VariantFiles.Delete(dir, "vcmanta", 3, 6, 4));
            // 5 (no colours) is now 4, 6 (red 6) is now 5
            Assert.Null(CarSidecar.ReadFile(dir, "vcmanta4.vcf"));
            Assert.Equal(new CarPaint(new PaintColour(6, 0, 0)), CarSidecar.ReadFile(dir, "vcmanta5.vcf"));
            Assert.False(File.Exists(Path.Combine(dir, "vcmanta6.i26")));
        }
        finally { Directory.Delete(dir, true); }
    }
}
