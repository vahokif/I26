using System.Numerics;
using I76.Formats.Assembly;
using I76.Formats.Mission;
using Xunit;

namespace I76.Formats.Tests;

/// <summary>
/// Which ground the modern look holds on the simulation's (TerrainSurface): a pillar a bridge leads to, the ends of a bridge's deck, the floor
/// under a step and at a cliff's foot. Synthetic maps of 2x2 tiles (256 samples a side), heights in metres per sample.
/// </summary>
public sealed class TerrainReachTests
{
    private const int T = TerrainTiles.Tile;

    private static TerrainTiles Map(Func<int, int, double> metres)
    {
        var grid = new byte[MissionFile.ZmapGrid * MissionFile.ZmapGrid];
        Array.Fill(grid, MissionFile.ZmapEmpty);
        for (int r = 0; r < 2; r++) for (int c = 0; c < 2; c++) grid[r * MissionFile.ZmapGrid + c] = (byte)(r * 2 + c);
        var ter = new byte[4 * T * T * 2];
        for (int r = 0; r < 2; r++)
            for (int c = 0; c < 2; c++)
                for (int z = 0; z < T; z++)
                    for (int x = 0; x < T; x++)
                    {
                        int i = ((r * 2 + c) * T * T + z * T + x) * 2;
                        ushort v = (ushort)Math.Round(metres(c * T + x, r * T + z) / MissionWorld.HeightScale);
                        ter[i] = (byte)v; ter[i + 1] = (byte)(v >> 8);
                    }
        return new TerrainTiles(ter, 4, grid);
    }

    /// <summary>The worst |drawn - bilinear| over the cells [x0, x1) x [z0, z1), read on the finest level's grid (a quarter sample).</summary>
    private static double Worst(TerrainSurface s, int x0, int x1, int z0, int z1)
    {
        double worst = 0;
        for (double fz = z0; fz <= z1; fz += 0.25)
            for (double fx = x0; fx <= x1; fx += 0.25)
                worst = Math.Max(worst, Math.Abs(s.Point(fx, fz, out _).Y - s.Bilinear(fx, fz)));
        return worst;
    }

    // a bank at 60 m (x < 100) a car drives on, the canyon floor at 20 m, and a pillar at 60 m (x and z 120..130) standing in it
    private static double Canyon(int x, int z) => x < 100 || x is >= 120 and <= 130 && z is >= 120 and <= 130 ? 60 : 20;
    private static readonly Vector2[] CanyonSeeds = [new(50.5f, 128.5f), new(110.5f, 60.5f)];
    // a bridge from the bank's edge to the pillar along z = 125.5, its deck's top at 60.5 m
    private static readonly (Vector3, Vector3)[] Bridge = [(new(97f, 60.5f, 125.5f), new(123f, 60.5f, 125.5f))];

    [Fact]
    public void APillarOnlyABridgeLeadsToIsCutWithoutTheLinkAndWholeWithIt()
    {
        var tiles = Map(Canyon);
        var without = new TerrainSurface(tiles, CanyonSeeds);
        Assert.False(without.Open(125, 125));
        // no car reaches its top: the walls lean back from the floor and cut its rim down
        double rim = without.Point(120.25, 125.5, out _).Y;
        Assert.True(rim < 57, $"{rim}");

        var with = new TerrainSurface(tiles, CanyonSeeds, links: Bridge);
        for (int z = 120; z < 130; z++) for (int x = 120; x < 130; x++) Assert.True(with.Open(x, z), $"{x},{z}");
        Assert.True(Worst(with, 120, 130, 120, 130) <= TerrainSurface.Tolerance + 1e-6);
        // the floor and the bank as before
        Assert.True(with.Open(110, 60) && with.Open(50, 128));
        Assert.NotEqual(TerrainSurface.CacheKey(tiles, CanyonSeeds), TerrainSurface.CacheKey(tiles, CanyonSeeds, links: Bridge));
    }

    [Fact]
    public void ABridgeDecksEndsHoldTheGentleGroundTheyRestOnNotTheGapOrTheWalls()
    {
        var tiles = Map(Canyon);
        // the deck: 26 samples long over the gap, 2 wide, its underside 1 m under its top; clear of the floor under its middle
        var deck = new GroundBox(new Vector2(110 * 5f, 125.5f * 5), Vector2.UnitX, 13 * 5f, 5f, Bottom: 59.5f);
        var s = new TerrainSurface(tiles, [CanyonSeeds[0]], pins: [deck]);
        Assert.False(s.Open(121, 125));
        // the pillar's top under the deck's end is held on the simulation's ground (the bridge's end does not float)...
        for (int x = 120; x <= 122; x++) Assert.True(s.Pinned(x, 125), $"{x}");
        Assert.True(Worst(s, 120, 123, 124, 127) <= TerrainSurface.Tolerance + 1e-6);
        // ... and the bank's under the other; not the walls (still free to lean back) nor the floor under the middle
        Assert.True(s.Pinned(97, 125));
        Assert.False(s.Held(99, 125));
        Assert.False(s.Pinned(99, 125));
        Assert.False(s.Pinned(119, 125));
        Assert.False(s.Pinned(110, 125));
    }

    [Fact]
    public void ARampsLipOverADropLinksNotTheGroundFarUnderIt()
    {
        // the bank with a car on it; nothing on the floor; a ramp at the bank's edge whose lip stands out over the floor
        var tiles = Map(Canyon);
        (Vector3, Vector3)[] ramp = [(new(90f, 60.2f, 60.5f), new(102f, 62f, 60.5f))];
        var s = new TerrainSurface(tiles, [CanyonSeeds[0]], links: ramp);
        Assert.False(s.Open(103, 60));
        Assert.False(s.Open(110, 60));
        // but a car off the edge lands on it: held all the same
        Assert.True(s.Held(110, 60));
    }

    // a floor at 20 m (x < 100) a car drives on; past a 4 m step down (steep: 0.8) the floor at 16 m runs to a jagged cliff (x >= 140 + 0..2),
    // 70 m high; for z >= 200 the step goes up instead, to a shelf at 24 m
    private static double Step(int x, int z) =>
        x < 100 ? 20 : x >= 140 + (z * 7 % 3) ? 70 : z >= 200 ? 24 : 16;

    [Fact]
    public void TheFloorUnderAStepAndAtACliffsFootIsHeldOnTheSimulationsGround()
    {
        var tiles = Map(Step);
        var s = new TerrainSurface(tiles, [new Vector2(50.5f, 100.5f)]);
        // not where the cars drive (a car drives down a step, not up it) ...
        Assert.False(s.Open(120, 100));
        // ... but held: a car on it is on the drawn ground, right up to the cliff's jagged foot
        for (int z = 20; z < 180; z++) for (int x = 101; x < 139; x++) Assert.True(s.Held(x, z), $"{x},{z}");
        Assert.True(Worst(s, 101, 139, 20, 180) <= TerrainSurface.Tolerance + 1e-6);
        // the cliff itself still free (softened, relief), its foot kept on that floor: up its face, the drawn point no further from the
        // simulation's than the freedom its height over the floor gives, once for the softening and once for the relief
        Assert.False(s.Held(141, 100));
        Assert.False(s.Held(150, 100));
        Assert.True(Worst(s, 141, 160, 20, 180) > 1);
        for (int z = 20; z < 180; z += 7)
            for (double up = 1 / 32.0; up < 0.3; up += 1 / 32.0)
            {
                double fx = 139 + z * 7 % 3 + up, b = s.Bilinear(fx, z);
                double allow = TerrainSurface.Tolerance + TerrainSurface.Give * Math.Max(0, b - 16 - TerrainSurface.Clear);
                Assert.True(Math.Abs(s.Point(fx, z, out _).Y - b) <= 2 * allow + 1e-6, $"{z} {up}: {s.Point(fx, z, out _).Y} against {b}");
            }
        // up the step: a shelf no car comes onto keeps the softened look
        Assert.False(s.Held(120, 230));
    }

    [Fact]
    public void ASurfaceFromItsBlobsIsTheSameAndOldBlobsAreRefused()
    {
        var tiles = Map(Step);
        var s = new TerrainSurface(tiles, [new Vector2(50.5f, 100.5f)], links: Bridge);
        var blobs = s.ToBlobs();
        Assert.Equal(11, blobs.Count);
        var back = TerrainSurface.FromBlobs(blobs)!;
        for (double f = 90; f < 150; f += 0.37) Assert.Equal(s.Point(f, f * 0.7 + 20, out _), back.Point(f, f * 0.7 + 20, out _));
        // a version 5 cache's nine blobs make no surface
        Assert.Null(TerrainSurface.FromBlobs(blobs.Take(9).ToList()));
    }
}
