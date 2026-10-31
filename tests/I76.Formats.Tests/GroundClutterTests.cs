using System.Numerics;
using I76.Formats.Assembly;
using Xunit;

namespace I76.Formats.Tests;

public class GroundClutterTests
{
    // looking along +z from (150, 50, 150): x right (game frame is left-handed, +x is the view's right when looking +z)
    private static GroundClutter.View Look(Vector3 pos, Vector3 fwd) =>
        new(pos, Vector3.Normalize(Vector3.Cross(Vector3.UnitY, fwd)), Vector3.UnitY, fwd, 1f, 0.75f);

    // a very wide view along +z from (150, 5, 100): the depth limit (z up to 220) is the only cull but the 2 m behind the eye
    private static GroundClutter.View Wide() =>
        new(new(150, 5, 100), Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ, 1000f, 1000f);

    [Fact]
    public void SurfaceTable()
    {
        Assert.Equal(1, GroundClutter.DetailOf(0));
        Assert.Equal(2, GroundClutter.DetailOf(1));
        Assert.Equal(3, GroundClutter.DetailOf(5));
        foreach (var s in new[] { 2, 3, 4, 6, 7 }) Assert.Equal(0, GroundClutter.DetailOf(s));
    }

    [Fact]
    public void PatternRepeatsOverTheNineCells()
    {
        // looking along +z from the middle of cell (100, 100) with a wide view: every card within 120 m depth
        var cards = GroundClutter.Place(Look(new(150, 0, 150), Vector3.UnitZ), (_, _) => 0, (_, _) => 0);
        Assert.All(cards, c => Assert.Equal(1, c.Detail));
        // a card needs 0 <= depth <= 120 (+2 m of slack behind the eye): z from 148 to 270
        foreach (var c in cards) Assert.InRange(c.Position.Z, 148, 270.01);
        Assert.Contains(cards, c => c.Point == 0 && c.Position == new Vector3(100 + 6.95f, 0, 200 + 14.30f));
        // B01's copy in the cell behind and the one in the next cell over are both considered
        Assert.DoesNotContain(cards, c => c.Position.Z < 148);
        // cells further than the 3x3 never show
        Assert.All(cards, c => Assert.InRange(c.Position.X, 0, 300));
    }

    [Fact]
    public void AllSixteenInEachCellWhenLookingEverywhereNear()
    {
        // from high up looking down nothing is over 120 deep and the frustum takes the whole cell
        var down = Wide();
        var cards = GroundClutter.Place(down, (_, _) => 0, (_, _) => 1);
        // the cells z 0..300 hold 16 each over 3 columns, minus those behind the eye (z < 98) or over 120 deep (z > 220)
        var expect = 0;
        foreach (var (ox, oz) in GroundClutter.Offsets)
            foreach (var (_, pz) in GroundClutter.Points) { double z = 100 + oz + pz; if (z >= 98 && z <= 220.00001) expect++; }
        Assert.Equal(expect, cards.Count);
        Assert.InRange(cards.Count, 50, 144);
        Assert.All(cards, c => Assert.Equal(2, c.Detail));
    }

    [Fact]
    public void SurfacePicksTheVariantPerPoint()
    {
        var down = Wide();
        var cards = GroundClutter.Place(down, (_, _) => 0, (x, _) => x < 150 ? 0 : x < 250 ? 5 : 2);
        Assert.All(cards, c => Assert.Equal(c.Position.X < 150 ? 1 : c.Position.X < 250 ? 3 : 0, c.Detail));
        Assert.DoesNotContain(cards, c => c.Position.X >= 250);
    }

    [Fact]
    public void GroundOverFiveMetresAboveTheCameraGroundIsCulled()
    {
        var down = Wide();
        var cards = GroundClutter.Place(down, (x, _) => x > 200 ? 5.1 : x > 160 ? 5.0 : 0, (_, _) => 0);
        Assert.NotEmpty(cards);
        Assert.DoesNotContain(cards, c => c.Position.X > 200);
        Assert.Contains(cards, c => c.Position.X > 160 && c.Position.X <= 200);
        Assert.All(cards, c => Assert.Equal(c.Position.X > 160 ? 5.0 : 0.0, c.Position.Y));
    }

    [Fact]
    public void DepthLimitIsInViewNotDistance()
    {
        // looking along +x: a card 130 m ahead is out, one 110 m ahead and 60 m to the side is in
        var v = Look(new(0, 0, 50), Vector3.UnitX);
        var cards = GroundClutter.Place(v, (_, _) => 0, (_, _) => 0);
        Assert.All(cards, c => Assert.InRange(c.Position.X, -2, 120.01));
        Assert.Contains(cards, c => c.Position.X > 100);
    }

    [Fact]
    public void CellFloorsTo100()
    {
        Assert.Equal(100, GroundClutter.CellOf(170));
        Assert.Equal(-100, GroundClutter.CellOf(-0.5));
        Assert.Equal(0, GroundClutter.CellOf(99.9));
    }
}
