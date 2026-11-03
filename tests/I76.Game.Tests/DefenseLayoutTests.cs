using Godot;
using I76.Game.Shell.Garage3D;
using Xunit;

namespace I76.Game.Tests;

/// <summary>The 3D garage's ARMOR page laid out as the CONFIGURE CHASSIS form's DEFENSE block: every [-] and [+] reachable, the form's own places.</summary>
public class DefenseLayoutTests
{
    private static readonly Rect2I Block = new(0, 0, DefenseLayout.W, DefenseLayout.H);

    private static IEnumerable<int> Cells => Enumerable.Range(0, DefenseLayout.Cells);

    [Fact]
    public void TheCellsAreTheSidesInTheFormsOrderArmourBesideChassis()
    {
        Assert.Equal(8, DefenseLayout.Cells);
        Assert.Equal(["FRONT", "RIGHT", "LEFT", "REAR"], DefenseLayout.ZoneNames);
        foreach (int cell in Cells)
        {
            var (zone, chassis) = DefenseLayout.ZoneOf(cell);
            Assert.Equal(cell, DefenseLayout.CellOf(zone, chassis));
            var r = DefenseLayout.Row(cell);
            // a side's two cells share a row of the block, armour on the left
            Assert.InRange(r.Position.Y, zone * DefenseLayout.RowH, (zone + 1) * DefenseLayout.RowH - 1);
            if (chassis) Assert.True(r.Position.X >= DefenseLayout.Row(cell - 1).End.X);
        }
        Assert.Equal(DefenseLayout.Cells, DefenseLayout.LbsRow);
        Assert.Equal(DefenseLayout.Cells + 1, DefenseLayout.BalanceRow);
        Assert.Equal(DefenseLayout.Balance, DefenseLayout.Row(DefenseLayout.BalanceRow));
    }

    [Fact]
    public void EveryStepperAndBalanceHasAPlaceOfItsOwnInsideTheBlock()
    {
        var hits = Cells.SelectMany(c => new[] { DefenseLayout.Hit(c, false), DefenseLayout.Hit(c, true) }).Append(DefenseLayout.Balance).ToList();
        Assert.Equal(17, hits.Count);
        for (int i = 0; i < hits.Count; i++)
        {
            Assert.True(Block.Encloses(hits[i]), $"{hits[i]} outside the block");
            for (int j = i + 1; j < hits.Count; j++) Assert.False(hits[i].Intersects(hits[j]), $"{hits[i]} overlaps {hits[j]}");
        }
        // a press on a box is a press on its cell's row (the page finds the row first), and the rows do not overlap
        foreach (int cell in Cells)
            foreach (bool plus in new[] { false, true })
                Assert.True(DefenseLayout.Row(cell).Encloses(DefenseLayout.Hit(cell, plus)), $"cell {cell} {(plus ? "+" : "-")}");
        var rows = Enumerable.Range(0, DefenseLayout.Rows).Select(DefenseLayout.Row).ToList();
        for (int i = 0; i < rows.Count; i++)
            for (int j = i + 1; j < rows.Count; j++) Assert.False(rows[i].Intersects(rows[j]), $"rows {i} and {j}");
    }

    [Fact]
    public void TheBoxesAndValuesSitBetweenTheFormsRules()
    {
        foreach (int cell in Cells)
        {
            var (_, chassis) = DefenseLayout.ZoneOf(cell);
            int left = chassis ? DefenseLayout.ChassisCol : DefenseLayout.ArmorCol, right = chassis ? DefenseLayout.W - DefenseLayout.Border : DefenseLayout.PairCol;
            Rect2I minus = DefenseLayout.Box(cell, false), plus = DefenseLayout.Box(cell, true);
            Assert.True(left < minus.Position.X && minus.End.X < plus.Position.X && plus.End.X <= right);
            Assert.InRange(DefenseLayout.ValueLeft(cell), minus.End.X, plus.Position.X - 1);
        }
    }

    // the 2D form's own DEFENSE hotspots (strictly inside, form coordinates): armour -, armour +, chassis -, chassis +, by row; BALANCE
    private static readonly (int X0, int X1)[] FormX = [(108, 117), (152, 161), (208, 217), (254, 263)];
    private static readonly (int Y0, int Y1)[] FormY = [(355, 366), (373, 382), (390, 400), (406, 416)];

    [Fact]
    public void TheSteppersAreWhereTheFormHasThem()
    {
        foreach (int cell in Cells)
        {
            var (zone, chassis) = DefenseLayout.ZoneOf(cell);
            foreach (bool plus in new[] { false, true })
            {
                var (x0, x1) = FormX[(chassis ? 2 : 0) + (plus ? 1 : 0)];
                var (y0, y1) = FormY[zone];
                var mid = new Vector2I((x0 + x1) / 2 - DefenseLayout.FormX, (y0 + y1) / 2 - DefenseLayout.FormY);
                Assert.True(DefenseLayout.Box(cell, plus).HasPoint(mid), $"cell {cell} {(plus ? "+" : "-")}: {mid} not in {DefenseLayout.Box(cell, plus)}");
            }
        }
        Assert.True(DefenseLayout.Balance.HasPoint(new Vector2I((185 + 246) / 2 - DefenseLayout.FormX, (422 + 435) / 2 - DefenseLayout.FormY)));
    }

    [Theory]
    [InlineData(400, "40.0")]
    [InlineData(0, "0.0")]
    [InlineData(1234, "123.4")]
    [InlineData(5, "0.5")]
    public void AValueHasOneDecimalAndReadsBack(int tenths, string text)
    {
        Assert.Equal(text, DefenseLayout.Value(tenths));
        Assert.Equal(tenths, (int)Math.Round(double.Parse(DefenseLayout.Value(tenths), System.Globalization.CultureInfo.InvariantCulture) * 10));
    }
}
