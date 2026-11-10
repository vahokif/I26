using I76.Formats.Assembly;
using I76.Formats.Mission;
using Xunit;

namespace I76.Formats.Tests;

/// <summary>
/// The ground past a map's tiles is the same on every machine: its maths is IEEE arithmetic and <see cref="DetMath"/> (no platform maths
/// library), so its samples are fixed numbers of the map and its panels. Golden values (taken on x64 Linux) pin them; a host and its
/// clients on other systems compute the same.
/// </summary>
public class OutsideGroundTests
{
    [Fact]
    public void DetMathAgreesWithTheRuntimesMathsToRounding()
    {
        var rnd = new Random(5);
        for (int i = 0; i < 20000; i++)
        {
            double a = (rnd.NextDouble() * 2 - 1) * 40;
            var (s, c) = DetMath.SinCos(a);
            Assert.Equal(Math.Sin(a), s, 1e-14);
            Assert.Equal(Math.Cos(a), c, 1e-14);
            double y = (rnd.NextDouble() * 2 - 1) * 1e4, x = (rnd.NextDouble() * 2 - 1) * 1e4;
            Assert.Equal(Math.Atan2(y, x), DetMath.Atan2(y, x), 1e-14);
            double p = rnd.NextDouble() * 1e3 + 1e-300 * rnd.NextDouble();
            if (p > 0) Assert.True(Math.Abs(Math.Log(p) - DetMath.Log(p)) <= 1e-14 * Math.Max(1, Math.Abs(Math.Log(p))), $"log {p}");
            double e = (rnd.NextDouble() * 2 - 1) * 50;
            Assert.True(Math.Abs(Math.Exp(e) - DetMath.Exp(e)) <= 1e-14 * Math.Exp(e), $"exp {e}");
            double u = rnd.NextDouble();
            Assert.True(Math.Abs(Math.Pow(u, 2.2) - DetMath.Pow(u, 2.2)) <= 1e-14, $"pow {u}");
        }
        Assert.Equal(0, DetMath.Atan2(0.0, 0.0));
        Assert.Equal(0, DetMath.Pow(0.0, 2.2));
        // the panels' height is a fixed number now: 6000 sin(π/16)
        Assert.True(Math.Abs(6000 * Math.Sin(Math.PI / 16) - HorizonList.PanelHeight) < 1e-3);
    }

    /// <summary>
    /// T01's outside ground at fixed samples (tenths of a metre): beside the map's low south and north edges (carried on), out on the plain
    /// (0), just past the plain, on the mountain to the south, off the grid's corner.
    /// </summary>
    [InstallFact]
    public void T01sOutsideGroundIsTheSameNumbersEverywhere()
    {
        var vfs = TestInstall.Vfs;
        (int X, int Z, int Raw)[] golden =
        [
            (405, 6911, 3),
            (405, 6800, 0),
            (405, 6792, 0),
            (405, 6780, 0),
            (405, 6740, 5),
            (300, 7500, 0),
            (200, 7500, 16),
            (200, 5000, -294),
            (1000, 8200, 0),
            (640, 8191, 2),
            (640, 8195, 2),
            (-50, -50, -12745),
            (20000, 300, -24412),
            (768, 8064, 0),
            (500, 6500, 3504),
        ];
        var path = TestInstall.Install.Resolve("miss16", "t01.msn")!;
        var w = new MissionWorld(vfs, new ModelAssembler(vfs), path);
        var o = w.Outside!;
        Assert.Same(o, w.Outside);
        foreach (var (x, z, raw) in golden) Assert.True(raw == o.Raw(x, z), $"sample {x},{z}: {o.Raw(x, z)}, golden {raw}");
        Assert.Equal(2048, o.Skyline.Length);
        Assert.Equal(757029.75f, o.Skyline.Sum());
        Assert.Equal("d16957872d410316514e643f301e822503ced2fe53216ac8eaebe6f9a8feeccf", o.Key);
        Assert.Equal(0x4342c298, BitConverter.SingleToInt32Bits(o.AtGame(2025, 33000).H));

        // the same again from a fresh world, its blocks made in another order on many threads
        var again = new MissionWorld(vfs, new ModelAssembler(vfs), path).Outside!;
        var pts = Enumerable.Range(0, 4000).Select(i => (X: 300 + i * 37 % 900, Z: 6600 + i * 53 % 2000)).ToArray();
        var first = pts.Select(p => o.Raw(p.X, p.Z)).ToArray();
        var second = new int[pts.Length];
        Parallel.For(0, pts.Length, i => second[pts.Length - 1 - i] = again.Raw(pts[pts.Length - 1 - i].X, pts[pts.Length - 1 - i].Z));
        Assert.Equal(first, second);
        foreach (var (x, z, raw) in golden) Assert.Equal(raw, again.Raw(x, z));
    }
}
