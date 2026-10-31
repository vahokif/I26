using I76.Formats.Assembly;
using I76.Formats.Mission;
using I76.Game.World;
using Xunit;

namespace I76.Game.Tests;

/// <summary>
/// The streaming of a map that fills the grid (nearly73km): the chunks the camera wants and the ones it lets go. The camera is
/// never left without ground: whatever is within the radius it draws is wanted, and nothing within that radius is ever freed.
/// </summary>
public class StreamPlanTests
{
    private static readonly int Cells = MissionFile.ZmapGrid * TerrainTiles.Tile / TerrainNode.Chunk;

    [Theory]
    [InlineData(25000f, 25000f, 600f)]
    [InlineData(51152f, -152f, 1000f)]     // nearly73's start, outside the grid
    [InlineData(3f, 40f, 840f)]            // the map's corner
    [InlineData(25012.5f, 799.9f, 7000f)]  // Draw Distance Far
    public void WithinIsEveryChunkWhoseBoxIsInRange(float px, float pz, float radius)
    {
        var got = StreamPlan.Within((_, _) => true, px, pz, radius).Select(c => (c.X0, c.Z0)).ToHashSet();
        var want = new HashSet<(int, int)>();
        for (int cz = 0; cz < Cells; cz++)
            for (int cx = 0; cx < Cells; cx++)
                if (StreamPlan.BoxDistance(px, pz, cx * TerrainNode.Chunk, cz * TerrainNode.Chunk) <= radius) want.Add((cx * TerrainNode.Chunk, cz * TerrainNode.Chunk));
        Assert.Equal(want, got);
    }

    [Fact]
    public void ChunksTheMapDoesNotHaveAreNotWanted()
    {
        var got = StreamPlan.Within((x0, z0) => x0 < 1024, 500f, 500f, 2000f);
        Assert.NotEmpty(got);
        Assert.All(got, c => Assert.True(c.X0 < 1024));
    }

    [Fact]
    public void AChunkWithinTheRadiusIsNeverFreedWhileKeepIsPastIt()
    {
        var rand = new Random(7);
        for (int i = 0; i < 400; i++)
        {
            float px = rand.NextSingle() * 52000f - 500f, pz = rand.NextSingle() * 52000f - 500f;
            float radius = rand.Next(3) switch { 0 => 600f + 240f, 1 => 1500f, _ => 7000f };
            float keep = radius + MathF.Max(400f, radius * 0.15f);   // TerrainNode.StreamFocus
            foreach (var (_, x0, z0) in StreamPlan.Within((_, _) => true, px, pz, radius))
                Assert.False(StreamPlan.Frees(px, pz, x0, z0, keep), $"chunk {x0},{z0} is wanted and freed at once from {px},{pz}");
        }
    }

    [Fact]
    public void TheChunkUnderTheCameraIsAlwaysWanted()
    {
        var rand = new Random(3);
        for (int i = 0; i < 400; i++)
        {
            float px = rand.NextSingle() * 51199f, pz = rand.NextSingle() * 51199f;
            int x0 = (int)(px / StreamPlan.ChunkMetres) * TerrainNode.Chunk, z0 = (int)(pz / StreamPlan.ChunkMetres) * TerrainNode.Chunk;
            Assert.Contains(StreamPlan.Within((_, _) => true, px, pz, 100f), c => c.X0 == x0 && c.Z0 == z0 && c.D == 0);
        }
    }

    [Fact]
    public void TheStartsFirstLookIsPartOfTheCamerasFirstLook()
    {
        // the chunks begun while the mission builds (round the player's start) are the ones the first look would make there
        var start = StreamPlan.Within((_, _) => true, 40000f, 9000f, 999.99f).Select(c => (c.X0, c.Z0)).ToHashSet();
        var look = StreamPlan.Within((_, _) => true, 40000f, 9000f, 1000f).Select(c => (c.X0, c.Z0)).ToHashSet();
        Assert.True(start.IsSubsetOf(look));
    }
}
