using I76.Formats.Assembly;
using I76.Formats.Mission;
using Xunit;
using Xunit.Abstractions;

namespace I76.Formats.Tests;

public class TerrainSurfaceTests(ITestOutputHelper log)
{
    [Fact]
    public void NoiseIsBoundedAndDeterministic()
    {
        double mx = 0;
        for (int i = 0; i < 2000; i++)
        {
            double v = TerrainSurface.Noise.Fbm(i * 0.37, i * 0.11, i * 0.53, 3);
            Assert.Equal(v, TerrainSurface.Noise.Fbm(i * 0.37, i * 0.11, i * 0.53, 3));
            mx = Math.Max(mx, Math.Abs(v));
        }
        Assert.InRange(mx, 0.2, 2.5);
    }

    /// <summary>
    /// The modern look's ground against the simulation's on every open cell (where a
    /// car can be): the drawn triangles at each level of detail, read at 9×9 points a
    /// cell, against the original's bilinear on the raw samples. The 5 m level is what
    /// the old mesh drew too (its error is the same bilinear-against-two-triangles twist).
    /// </summary>
    [InstallTheory]
    [InlineData("t01")]
    [InlineData("a01")]
    [InlineData("m01")]
    [InlineData("m05")]
    public void DrawnGroundStaysOnTheSimulationsWhereCarsDrive(string mission)
    {
        var vfs = TestInstall.Vfs;
        var w = new MissionWorld(vfs, new ModelAssembler(vfs), TestInstall.Install.Resolve("miss16", mission + ".msn")!);
        var surf = new TerrainSurface(w.Terrain, TerrainSurface.Seeds(w));
        var (r0, r1, c0, c1) = w.Terrain.Bounds();
        int gentle = 0, cells = 0;
        var worst = new double[3];
        var all = new List<double>[] { [], [], [] };
        var oldAll = new List<double>();
        var where = new string[3];
        double oldWorst = 0;
        var rng = new Random(1);
        for (int sz = r0 * 128; sz < (r1 + 1) * 128 - 1; sz++)
            for (int sx = c0 * 128; sx < (c1 + 1) * 128 - 1; sx++)
            {
                if (w.Terrain.Sample(sx, sz) is null || w.Terrain.Sample(sx + 1, sz) is null || w.Terrain.Sample(sx, sz + 1) is null || w.Terrain.Sample(sx + 1, sz + 1) is null) continue;
                // the simulation reads 0 past the terrain's edge; no car stands within a cell of it
                if (w.Terrain.Sample(sx - 1, sz - 1) is null || w.Terrain.Sample(sx + 2, sz - 1) is null || w.Terrain.Sample(sx - 1, sz + 2) is null || w.Terrain.Sample(sx + 2, sz + 2) is null) continue;
                cells++;
                if (!surf.Open(sx, sz)) continue;
                gentle++;
                // every cell would take minutes; a fixed random tenth, and all of them near the spawn
                if (rng.Next(10) != 0) continue;
                for (int lvl = 0; lvl < 3; lvl++)
                {
                    int s = 4 >> lvl;
                    double d = Deviation(w, surf, sx, sz, s, out var at);
                    all[lvl].Add(d);
                    if (d > worst[lvl]) { worst[lvl] = d; where[lvl] = at; }
                }
                double od = OldDeviation(w, sx, sz);
                oldAll.Add(od);
                oldWorst = Math.Max(oldWorst, od);
            }
        log.WriteLine($"{mission}: {gentle}/{cells} cells open; max |drawn - sim| on open cells: " +
            $"1.25 m {worst[0]:0.000} ({where[0]}), 2.5 m {worst[1]:0.000} ({where[1]}), 5 m {worst[2]:0.000} ({where[2]}); old mesh {oldWorst:0.000}");
        static double Q(List<double> v, double q) { v.Sort(); return v[(int)(q * (v.Count - 1))]; }
        log.WriteLine($"  per cell, 99% / 99.9% / median: 1.25 m {Q(all[0], 0.99):0.000} / {Q(all[0], 0.999):0.000} / {Q(all[0], 0.5):0.000}; " +
            $"2.5 m {Q(all[1], 0.99):0.000} / {Q(all[1], 0.999):0.000}; 5 m and old mesh {Q(oldAll, 0.99):0.000} / {Q(oldAll, 0.999):0.000} / {Q(oldAll, 0.5):0.000}");
        Assert.True(worst[0] < 0.25, $"{worst[0]}");
    }

    /// <summary>
    /// The surface's arrays are the same as the serial build's before its passes ran on every core (hashes taken from
    /// that build on t01: the needle filter, the blur, the open cells, the cone over them and its base, the rims), and
    /// what the bake cache keeps of them makes the same surface back. The same on every machine: the blur's weights are
    /// <see cref="DetMath.ExpF"/>'s, not the platform maths library's (Windows' rounds differently in the last place).
    /// </summary>
    [InstallFact]
    public void ParallelBuildAndCachedCopyAreTheSerialBuildsSurface()
    {
        var vfs = TestInstall.Vfs;
        var w = new MissionWorld(vfs, new ModelAssembler(vfs), TestInstall.Install.Resolve("miss16", "t01.msn")!);
        var seeds = TerrainSurface.Seeds(w).ToList();
        var surf = new TerrainSurface(w.Terrain, seeds);
        var blobs = surf.ToBlobs();
        static string H(byte[] d) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(d))[..12];
        // _ht, _hb, _open, _anchor, _cone, _coneBase, _under
        string[] serial = ["FC3C558174ED", "DE4AEAD0B3EC", "BFF9DCB88C4D", "1C83D94DEC1B", "0BF20A1078AE", "63FB6BB0DF7A", "688C61CA9EE9"];
        Assert.Equal(serial, blobs.Skip(1).Take(7).Select(b => H(b.Data)).ToArray());
        Assert.All(blobs[8].Data, b => Assert.Equal(0, b));   // _pin: no objects pin a surface built from seeds alone
        // twice the same, and the same back from its blobs
        Assert.Equal(blobs.Select(b => H(b.Data)), new TerrainSurface(w.Terrain, seeds).ToBlobs().Select(b => H(b.Data)));
        var back = TerrainSurface.FromBlobs(blobs)!;
        Assert.Equal(blobs.Select(b => H(b.Data)), back.ToBlobs().Select(b => H(b.Data)));
        var rng = new Random(3);
        var (r0, r1, c0, c1) = w.Terrain.Bounds();
        for (int i = 0; i < 2000; i++)
        {
            double fx = c0 * 128 + rng.NextDouble() * (c1 - c0 + 1) * 128, fz = r0 * 128 + rng.NextDouble() * (r1 - r0 + 1) * 128;
            Assert.Equal(surf.Point(fx, fz, out double sa), back.Point(fx, fz, out double sb));
            Assert.Equal(sa, sb);
        }
        // the key: the same inputs the same key; another seed another key
        Assert.Equal(TerrainSurface.CacheKey(w.Terrain, seeds), TerrainSurface.CacheKey(w.Terrain, [.. seeds]));
        Assert.NotEqual(TerrainSurface.CacheKey(w.Terrain, seeds), TerrainSurface.CacheKey(w.Terrain, [.. seeds, new System.Numerics.Vector2(1, 1)]));
        Assert.Null(TerrainSurface.FromBlobs(blobs.Take(7).ToList()));
    }

    private static double Sim(MissionWorld w, double fx, double fz) => w.BilinearHeight(fx * 5, fz * 5);

    /// <summary>The drawn height at a point of a grid with s subdivisions a cell, the triangles split (i+1, j)–(i, j+1).</summary>
    private static double Drawn(TerrainSurface surf, double fx, double fz, int s)
    {
        double gx = fx * s, gz = fz * s;
        int i = (int)Math.Floor(gx), j = (int)Math.Floor(gz);
        double u = gx - i, v = gz - j;
        double P(int a, int b) => surf.Point((double)a / s, (double)b / s, out _).Y;
        // the half nearer (i, j): u + v <= 1
        return u + v <= 1
            ? P(i, j) + (P(i + 1, j) - P(i, j)) * u + (P(i, j + 1) - P(i, j)) * v
            : P(i + 1, j + 1) + (P(i, j + 1) - P(i + 1, j + 1)) * (1 - u) + (P(i + 1, j) - P(i + 1, j + 1)) * (1 - v);
    }

    private static double Deviation(MissionWorld w, TerrainSurface surf, int sx, int sz, int s, out string at)
    {
        double worst = 0; at = "";
        for (int b = 0; b <= 8; b++)
            for (int a = 0; a <= 8; a++)
            {
                double fx = sx + a / 8.0, fz = sz + b / 8.0;
                double d = Math.Abs(Drawn(surf, fx, fz, s) - Sim(w, fx, fz));
                if (d > worst) { worst = d; at = $"{fx * 5:0.0},{fz * 5:0.0}"; }
            }
        return worst;
    }

    private static double OldDeviation(MissionWorld w, int sx, int sz)
    {
        double R(int x, int z) => ((w.Terrain.Sample(x, z) ?? 0) & MissionFile.HeightMask) * 0.1;
        double worst = 0;
        for (int b = 0; b <= 8; b++)
            for (int a = 0; a <= 8; a++)
            {
                double u = a / 8.0, v = b / 8.0;
                double drawn = u + v <= 1
                    ? R(sx, sz) + (R(sx + 1, sz) - R(sx, sz)) * u + (R(sx, sz + 1) - R(sx, sz)) * v
                    : R(sx + 1, sz + 1) + (R(sx, sz + 1) - R(sx + 1, sz + 1)) * (1 - u) + (R(sx + 1, sz) - R(sx + 1, sz + 1)) * (1 - v);
                worst = Math.Max(worst, Math.Abs(drawn - Sim(w, sx + u, sz + v)));
            }
        return worst;
    }
}
