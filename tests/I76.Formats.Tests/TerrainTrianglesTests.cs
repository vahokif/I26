using I76.Formats.Assembly;
using Xunit;

namespace I76.Formats.Tests;

/// <summary>The 1997 look's terrain triangulation (TerrainTriangles): stepped cliff rims are chamfered, not combs (#18).</summary>
public class TerrainTrianglesTests
{
    private const int N = 9;
    private const float Low = 50f, High = 100f;   // M14's plateau stands 20-60 m over the slope at its foot

    private static float[] Grid(Func<int, int, bool> plateau)
    {
        var h = new float[N * N];
        for (int j = 0; j < N; j++)
            for (int i = 0; i < N; i++) h[j * N + i] = plateau(i, j) ? High : Low;
        return h;
    }

    /// <summary>Every cell with three corners on the plateau has a whole triangle on top: the rim is a chamfer.</summary>
    private static void AssertChamfered(float[] h)
    {
        var idx = TerrainTriangles.Indices(h, N);
        int cells = 0;
        for (int c = 0; c < (N - 1) * (N - 1); c++)
        {
            int j = c / (N - 1), i = c % (N - 1), a = j * N + i;
            int high = new[] { a, a + 1, a + N, a + N + 1 }.Count(v => h[v] == High);
            if (high != 3) continue;
            cells++;
            bool top = false;
            for (int t = 0; t < 2; t++)
                top |= Enumerable.Range(0, 3).All(k => h[idx[c * 6 + t * 3 + k]] == High);
            Assert.True(top, $"cell ({i}, {j}) folds along its low corner: a notch in the rim");
        }
        Assert.True(cells > 4);
    }

    [Fact]
    public void ARimSteppingEitherWayIsChamfered()
    {
        AssertChamfered(Grid((i, j) => i + j >= N - 1));      // the rim runs along the old diagonal's direction
        AssertChamfered(Grid((i, j) => i - j >= 0));          // and across it, where the fixed split made a comb
    }

    [Fact]
    public void LevelGroundAndStraightWallsKeepTheOldDiagonal()
    {
        foreach (var h in new[] { Grid((_, _) => false), Grid((i, _) => i >= 4), Grid((_, j) => j >= 4) })
        {
            var idx = TerrainTriangles.Indices(h, N);
            for (int c = 0; c < (N - 1) * (N - 1); c++)
            {
                int j = c / (N - 1), i = c % (N - 1), a = j * N + i;
                Assert.Equal(new[] { a, a + N, a + 1, a + 1, a + N, a + N + 1 }, idx[(c * 6)..(c * 6 + 6)]);
            }
        }
    }

    [Fact]
    public void TrianglesStayClockwiseFromAbove()
    {
        // Godot positions: column i at x = 5i, row j at z = -5j; a front face has a negative y cross product in (x, z)
        var h = Grid((i, j) => i - j >= 0);
        var idx = TerrainTriangles.Indices(h, N);
        for (int t = 0; t < idx.Length; t += 3)
        {
            (float X, float Z) P(int v) => (5f * (v % N), -5f * (v / N));
            var (a, b, c) = (P(idx[t]), P(idx[t + 1]), P(idx[t + 2]));
            float cross = (b.Z - a.Z) * (c.X - a.X) - (b.X - a.X) * (c.Z - a.Z);
            Assert.True(cross < 0);
        }
    }

    /// <summary>DrawnHeight is the height of the very triangles Indices lists: sampled over a bumpy grid it matches a ray against them.</summary>
    [Fact]
    public void DrawnHeightIsTheMeshesOwnSurface()
    {
        var rng = new Random(7);
        var h = new float[N * N];
        for (int i = 0; i < h.Length; i++) h[i] = rng.Next(0, 400) * 0.1f;
        var idx = TerrainTriangles.Indices(h, N);
        float V(int i, int j) => h[j * N + i];
        int differs = 0;
        for (int k = 0; k < 400; k++)
        {
            double fx = rng.NextDouble() * (N - 1.001), fz = rng.NextDouble() * (N - 1.001);
            int ci = (int)fx, cj = (int)fz, c = cj * (N - 1) + ci;
            float? hit = null;
            for (int t = 0; t < 2 && hit is null; t++)
            {
                int i0 = idx[c * 6 + t * 3], i1 = idx[c * 6 + t * 3 + 1], i2 = idx[c * 6 + t * 3 + 2];
                (double x, double z, float y) P(int v) => (v % N, v / N, h[v]);
                var (x0, z0, y0) = P(i0); var (x1, z1, y1) = P(i1); var (x2, z2, y2) = P(i2);
                double d = (z1 - z2) * (x0 - x2) + (x2 - x1) * (z0 - z2);
                double l0 = ((z1 - z2) * (fx - x2) + (x2 - x1) * (fz - z2)) / d, l1 = ((z2 - z0) * (fx - x2) + (x0 - x2) * (fz - z2)) / d, l2 = 1 - l0 - l1;
                if (l0 >= -1e-9 && l1 >= -1e-9 && l2 >= -1e-9) hit = (float)(l0 * y0 + l1 * y1 + l2 * y2);
            }
            Assert.NotNull(hit);
            Assert.Equal(hit!.Value, TerrainTriangles.DrawnHeight(V, fx, fz), 3);
            float bil = (float)((V(ci, cj) * (1 - (fx - ci)) + V(ci + 1, cj) * (fx - ci)) * (1 - (fz - cj)) + (V(ci, cj + 1) * (1 - (fx - ci)) + V(ci + 1, cj + 1) * (fx - ci)) * (fz - cj));
            if (MathF.Abs(bil - hit.Value) > 0.01f) differs++;
        }
        Assert.True(differs > 50, "a bumpy grid's bilinear surface is not the drawn one");
    }

    [Fact]
    public void DespikedLevelsALoneNeedleOnly()
    {
        int S(int x, int z) => x == 5 && z == 5 ? 900 : 100;
        Assert.Equal(100, TerrainTriangles.Despiked(S, 5, 5));
        Assert.Equal(100, TerrainTriangles.Despiked(S, 6, 5));
        int T(int x, int z) => x == 5 && z == 5 ? 200 : 100;
        Assert.Equal(200, TerrainTriangles.Despiked(T, 5, 5));
    }
}
