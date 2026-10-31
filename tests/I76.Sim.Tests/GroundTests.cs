using System.Numerics;
using I76.Formats.Assembly;
using I76.Formats.Vfs;
using I76.Sim.Terrain;
using Xunit;

namespace I76.Sim.Tests;

public class GroundTests
{
    private static string? InstallDir =>
        Environment.GetEnvironmentVariable("I76_INSTALL") is { Length: > 0 } d && Directory.Exists(d) ? d : null;

    /// <summary>
    /// The original picks the cell's half by comparing world coordinates with
    /// sample indices, so on a01 (z ≫ x) every point takes the half through
    /// (ix, iz), (ix, iz+1), (ix+1, iz+1) -- even one lying in the other half.
    /// At the foot of the cliff the a01 trace of the original drove into, that half
    /// carries the rise; the half the point lies in is flat.
    /// </summary>
    [Fact]
    public void TheNormalTakesTheEnginesHalfNotThePointsHalf()
    {
        if (InstallDir is null) return;
        var vfs = new AssetVfs(new GameInstall(InstallDir));
        var w = new MissionWorld(vfs, new ModelAssembler(vfs), new GameInstall(InstallDir).Resolve("miss16", "a01.msn")!);
        var g = new TerrainGround(w.Terrain, new Surface[8]);
        // the contact at the cliff's foot (trace frame 1675): sample cell (223, 9844), in its lower-right half
        double x = 1118.9354, z = 49222.066;
        double fx = 0.2 * x, fz = 0.2 * z;
        int ix = (int)fx, iz = (int)fz;
        Assert.True(fz - iz <= fx - ix, "the point lies in the lower-right half");
        float H(int sx, int sz) => g.Height(sx / 0.2 + 1e-6, sz / 0.2 + 1e-6);
        // the upper-left half's true normal, from its three corners
        float dhdx = H(ix + 1, iz + 1) - H(ix, iz + 1), dhdz = H(ix, iz + 1) - H(ix, iz);
        var want = Vector3.Normalize(new Vector3(-dhdx / 5, 1, -dhdz / 5));
        var n = g.Normal(x, z);
        Assert.True(Vector3.Distance(want, n) < 1e-3f, $"{n} vs {want}");
        Assert.True(n.Y < 0.9f, $"the cliff's half is steep: {n}");
    }
}
