using System.Numerics;
using I76.Formats.Vegetation;
using Xunit;

namespace I76.Formats.Tests;

/// <summary>The modern look's brush impacts (docs/modern-vegetation.md, Brush impacts): the disturbance field and the plants by place.</summary>
public class BrushFieldTests
{
    private static BrushField At(float x = 0, float z = 0)
    {
        var f = new BrushField();
        f.Follow(x, z);
        return f;
    }

    [Fact]
    public void ABlastLaysThePlantsDownOutwardWithinItsReach()
    {
        var f = At();
        f.Stamp(0, 0, 6, 0.75f, 1f);
        Assert.Equal(0.75f, f.Lay(1, 0.2f), 3);
        Assert.Equal(1f, f.Shake(-1, 0.2f), 3);
        // fading out toward the edge, nothing past it
        Assert.InRange(f.Lay(4.6f, 0.2f), 0.05f, 0.6f);
        Assert.Equal(0f, f.Lay(9, 0));
        // away from the middle: east of it they lie east, north of it north
        Assert.True(Vector2.Dot(f.Direction(4, 0.5f), Vector2.UnitX) > 0.9f);
        Assert.True(Vector2.Dot(f.Direction(0.5f, 4), Vector2.UnitY) > 0.9f);
        Assert.True(Vector2.Dot(f.Direction(-4, 0.5f), -Vector2.UnitX) > 0.9f);
    }

    [Fact]
    public void ACarLaysThePlantsAlongItsWayAndPushesThoseBesideItOut()
    {
        var f = At();
        f.Stamp(0, 0, 1.9f, 1f, 0.4f, 0, Vector2.UnitY, 0.8f);
        Assert.Equal(1f, f.Lay(0.2f, 0.2f), 3);
        // in its track (a cell's middle a little to one side) mostly along its way
        Assert.True(Vector2.Dot(f.Direction(0.2f, 0.2f), Vector2.UnitY) > 0.8f);
        // beside it, still forward but leaning out to that side
        var right = f.Direction(1.6f, 0.2f);
        Assert.True(right.X > 0.2f && right.Y > 0.5f);
        var left = f.Direction(-1.6f, 0.2f);
        Assert.True(left.X < -0.2f && left.Y > 0.5f);
    }

    [Fact]
    public void LaidPlantsRiseOverFifteenSecondsAndTheShakeDiesInAboutOne()
    {
        var f = At();
        f.Stamp(0, 0, 3, 1f, 1f);
        for (int i = 0; i < 20; i++) f.Decay(0.05f);   // a second
        Assert.Equal(1f - 1f / BrushField.Recover, f.Lay(0.5f, 0.5f), 2);
        Assert.InRange(f.Shake(0.5f, 0.5f), 0.08f, 0.12f);   // 2^(-1/0.3)
        for (int i = 0; i < 20 * 15; i++) f.Decay(0.05f);
        Assert.Equal(0f, f.Lay(0.5f, 0.5f));
        Assert.Equal(0f, f.Shake(0.5f, 0.5f));
        Assert.Equal(0, f.Active);
    }

    [Fact]
    public void AScorchStays()
    {
        var f = At();
        f.Stamp(10, 10, 2, 0.3f, 0, 1f);
        for (int i = 0; i < 400; i++) f.Decay(0.05f);
        Assert.Equal(1f, f.Scorch(10.2f, 10.2f), 3);
        Assert.True(f.Active > 0);
    }

    [Fact]
    public void TheFieldFollowsTheCameraKeepingWhatItStillCovers()
    {
        var f = At();
        f.Stamp(20, 0, 2, 1f, 0);
        f.Follow(5, 0);   // less than a step: it stays
        Assert.Equal(-BrushField.Size / 2 * BrushField.Cell, f.MinX, 3);
        f.Follow(60, 0);
        Assert.True(f.MinX > -BrushField.Size / 2 * BrushField.Cell);
        Assert.Equal(1f, f.Lay(20.2f, 0.2f), 3);
        Assert.True(Vector2.Dot(f.Direction(21.5f, 0.2f), Vector2.UnitX) > 0.9f);
        // far enough that it falls off the field
        f.Follow(400, 0);
        Assert.Equal(0f, f.Lay(20.2f, 0.2f));
    }

    [Fact]
    public void EncodedAsTheShaderReadsIt()
    {
        var f = At();
        var bytes = new byte[BrushField.Size * BrushField.Size * 4];
        f.Encode(bytes);
        Assert.All(bytes, b => Assert.Equal(0, b));
        f.Stamp(0, 0, 1.9f, 1f, 0.5f, 0.25f, Vector2.UnitY, 0);
        f.Encode(bytes);
        int i = (int)MathF.Floor(0.2f / BrushField.Cell) - (int)MathF.Floor(f.MinX / BrushField.Cell);
        int j = (int)MathF.Floor(0.2f / BrushField.Cell) - (int)MathF.Floor(f.MinZ / BrushField.Cell);
        int k = (j * BrushField.Size + i) * 4;
        Assert.Equal(255, bytes[k]);
        Assert.Equal(64, bytes[k + 1]);   // +z: a quarter turn
        Assert.InRange(bytes[k + 2], 63, 65);
        Assert.InRange(bytes[k + 3], 127, 129);
        Assert.False(f.Dirty);
        // risen again, written back to nothing
        for (int n = 0; n < 400; n++) f.Decay(0.05f);
        f.Encode(bytes);
        Assert.Equal(0, bytes[k]);
        Assert.Equal(0, bytes[k + 3]);
    }

    [Fact]
    public void ThePlantsAreFoundByWhereTheirCrownsReach()
    {
        var spots = new List<PlantSpot>
        {
            new(new Vector3(10, 0, 10), 1.0f, 1.2f, 0.45f, false),
            new(new Vector3(30, 0, 10), 0.6f, 1.1f, 0.95f, false),
            new(new Vector3(79, 0, 79), 0.2f, 0.3f, 0.3f, true),
        };
        var ix = new PlantIndex(0, 0, 80, spots);
        var near = new List<PlantSpot>();
        ix.Near(11.4f, 10, 0.5f, near);
        Assert.Single(near);
        Assert.True(near[0].Burns);
        near.Clear();
        ix.Near(31.5f, 10, 0.5f, near);
        Assert.Empty(near);   // 1.5 m off, its crown reaches 0.6
        ix.Near(31.0f, 10, 0.5f, near);
        Assert.Single(near);
        Assert.False(near[0].Burns);   // a cholla does not burn
        near.Clear();
        ix.Near(78.9f, 79.1f, 0.3f, near);
        Assert.Single(near);
        Assert.True(near[0].Ground);
        near.Clear();
        ix.Near(20, 40, 50, near, max: 2);
        Assert.Equal(2, near.Count);
    }
}
