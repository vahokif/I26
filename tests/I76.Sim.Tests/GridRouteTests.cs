using System.Numerics;
using I76.Sim.Ai;
using I76.Sim.Terrain;
using Xunit;

namespace I76.Sim.Tests;

/// <summary>The bots' coarse grid route (<see cref="GridRoute"/>).</summary>
public class GridRouteTests
{
    /// <summary>Impassable everywhere but one 12 m cell, [1012, 1024] x [1006, 1018] on the grid a route from (1000, 1006) builds.</summary>
    private sealed class OneOpenCell : IGround
    {
        public (float Height, Vector3 Normal) Query(double x, double y, double z) => (0, Vector3.UnitY);
        public int SurfaceId(double x, double z) => 1;
        public Surface Surface(int id) => new(1f, 0.03f, 0, 0, 0);
        public bool Impassable(double x, double z) => !(x >= 1012 && x <= 1024 && z >= 1006 && z <= 1018);
    }

    private sealed class Open : IGround
    {
        public (float Height, Vector3 Normal) Query(double x, double y, double z) => (0, Vector3.UnitY);
        public int SurfaceId(double x, double z) => 1;
        public Surface Surface(int id) => new(1f, 0.03f, 0, 0, 0);
    }

    [Fact]
    public void StartAndGoalSnappedToOneCellGiveTheGoal()
    {
        // 30 m apart, each in a blocked cell next to the one open cell: Nearest() snaps both to it, and the route was an empty list
        // indexed with [^1] (ArgumentOutOfRangeException on the server tick)
        var from = new Vector3(1000, 0, 1006); var to = new Vector3(1030, 0, 1006);
        var r = GridRoute.Between(new OneOpenCell(), from, to);
        Assert.NotNull(r);
        Assert.Equal(new[] { to }, r);
    }

    [Fact]
    public void StartAndGoalInOneOpenCellGiveTheGoal()
    {
        var from = new Vector3(1001, 0, 1001); var to = new Vector3(1004, 0, 1003);
        var r = GridRoute.Between(new Open(), from, to);
        Assert.NotNull(r);
        Assert.Equal(new[] { to }, r);
    }

    [Fact]
    public void ARouteOverOpenGroundEndsAtTheGoal()
    {
        var to = new Vector3(1300, 0, 1000);
        var r = GridRoute.Between(new Open(), new Vector3(1000, 0, 1000), to);
        Assert.NotNull(r);
        Assert.Equal(to, r![^1]);
        Assert.True(r.Count >= 5);   // 300 m in points no more than 70 m apart
    }
}
