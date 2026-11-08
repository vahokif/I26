using I76.Formats.Mission;
using I76.Sim.Collision;
using I76.Sim.Session;
using I76.Sim.Vehicles;
using Xunit;

namespace I76.Sim.Tests;

/// <summary>The Developer panel's generated places (DevLandmarks): the highest and lowest ground the player can drive to.</summary>
public class DevLandmarksTests
{
    /// <summary>
    /// A 3 x 3-tile map made in code: a floor at 10 with a broad hill to 60 in the middle, and a rim that climbs to a
    /// plateau at 150 along the edges (gentle enough to drive up). The rim's plateau is flat and clear of the edge, but it
    /// is the outer ring and higher than the inside, so the highest ground is the hill's top, not the rim.
    /// </summary>
    [Fact]
    public void TheHillNotTheRim()
    {
        const int T = TerrainTiles.Tile, N = 3 * T;
        var grid = Enumerable.Repeat(MissionFile.ZmapEmpty, MissionFile.ZmapGrid * MissionFile.ZmapGrid).ToArray();
        for (int r = 0; r < 3; r++) for (int c = 0; c < 3; c++) grid[(10 + r) * MissionFile.ZmapGrid + 10 + c] = (byte)(r * 3 + c);
        var ter = new byte[9 * T * T * 2];
        var tiles = new TerrainTiles(ter, 9, grid);
        int Height(int i, int k)
        {
            int edge = System.Math.Min(System.Math.Min(i, k), System.Math.Min(N - 1 - i, N - 1 - k));
            if (edge < 40) return 1500;
            if (edge < 100) return 1500 - (edge - 40) * 1400 / 60;
            double dx = i - N / 2.0, dz = k - N / 2.0;
            return 100 + (int)(500 * System.Math.Exp(-(dx * dx + dz * dz) / (2 * 30.0 * 30.0)));
        }
        for (int k = 0; k < N; k++) for (int i = 0; i < N; i++) tiles.SetHeight(10 * T + i, 10 * T + k, Height(i, k));
        var r0 = DevLandmarks.Reach(tiles, (10 * T + 110) * 5.0, (10 * T + 110) * 5.0)!;
        // the rim is reached (the climb is gentle) and its plateau is open ground, but it is dropped as the outer ring
        Assert.True(r0.Cells[(25) * r0.W + N / 2]);
        int top = Enumerable.Range(0, r0.Open.Length).Where(c => r0.Open[c]).MaxBy(c => r0.H[c]);
        Assert.Equal(600, r0.H[top]);
        var (x, z) = r0.World(top);
        Assert.Equal((10 * T + N / 2) * 5.0, x, 1);
        Assert.Equal((10 * T + N / 2) * 5.0, z, 1);
    }

    /// <summary>
    /// T05 from the install (skipped without one): its highest ground is the lone hill inside the canyons, not a canyon wall
    /// or the map's rim (both higher): clear of every impassable sample and of the map's edge, higher than the start, and a
    /// car put there stands still on it, facing downhill (without the script, whose opening drives the player's car).
    /// </summary>
    [Fact]
    public void T05sHighestGroundIsAHillTopTheCarStandsOn()
    {
        if (SnapshotRig.Install is not { } dir) return;
        var l = new SnapshotRig.Loaded(dir);
        var (s, _) = SnapshotRig.Make(l, SnapshotRig.BuildOf(dir, "t05") with { Script = false }, SimRate.R20, 2);
        var p = s.World.Player!;
        var start = (p.Pos.X, p.Pos.Z, DevTools.HeadingOf(p.Rot.Forward));
        var marks = DevLandmarks.Of(s, start);
        Assert.Equal("start", marks[0].Name);
        var hi = Assert.Single(marks, m => m.Name.StartsWith("highest ground"));
        Assert.Single(marks, m => m.Name.StartsWith("lowest ground"));
        Assert.Single(marks, m => m.Name == "map centre");
        Assert.Contains(marks, m => m.Name.StartsWith("bridge "));

        var g = (WorldGround)s.World.Ground;
        float y = g.BareHeight(hi.X, hi.Z);
        Assert.True(y > p.Pos.Y + 50, $"the top {y} is not well above the start {p.Pos.Y}");
        // the canyon walls stand higher than the hill, and the rim too: the top is clear of both
        for (int a = 0; a < 36; a++)
            for (double d = 5; d < DevLandmarks.Clearance; d += 5)
            {
                double ax = hi.X + d * System.Math.Sin(a * System.Math.PI / 18), az = hi.Z + d * System.Math.Cos(a * System.Math.PI / 18);
                Assert.False(g.Impassable(ax, az), $"impassable {d} from the top");
                Assert.NotNull(g.Terrain.Tiles.Sample((int)System.Math.Round(ax / 5), (int)System.Math.Round(az / 5)));
            }
        // facing downhill
        float yaw = hi.Heading * MathF.PI / 180f;
        Assert.True(g.BareHeight(hi.X + 20 * MathF.Sin(yaw), hi.Z + 20 * MathF.Cos(yaw)) < y);
        // a car put there stays there: a top it can stand on, not a wall it slides off
        Assert.True(DevTools.Teleport(s, p, hi.X, hi.Z, hi.Heading));
        var inputs = new CarInput[s.Drivers.Count == 0 ? 0 : s.Drivers.Max(d => d.Slot) + 1];
        for (int i = 0; i < 100; i++) { s.Tick(inputs); SnapshotRig.TakeEvents(s); }
        double moved = System.Math.Sqrt((p.Pos.X - hi.X) * (p.Pos.X - hi.X) + (p.Pos.Z - hi.Z) * (p.Pos.Z - hi.Z));
        Assert.True(moved < 5, $"the car moved {moved:0.0} units from the top");
    }
}
