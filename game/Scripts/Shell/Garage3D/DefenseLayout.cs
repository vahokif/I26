using Godot;

namespace I76.Game.Shell.Garage3D;

/// <summary>
/// The CONFIGURE CHASSIS form's DEFENSE block as the 3D garage's ARMOR page lays it out: the form's own geometry, measured off its
/// background (record 2) and the 2D form's text positions (<see cref="GarageScreen"/>), relative to the block's top-left corner (the form
/// has it at (8, 352)). A column with DEFENSE written up it, four rows (FRONT, RIGHT, LEFT, REAR) of 17, each "armor" [-] value [+] and
/// "chassis" [-] value [+], then LBS. AVAILABLE with its underlined value and BALANCE. The page's rows are the cells in reading order
/// (front armour, front chassis, right armour, ...), then LBS. AVAILABLE and BALANCE. Pure layout, no engine.
/// </summary>
public static class DefenseLayout
{
    /// <summary>Where the block is on the 2D form (its art is cut from there in the 1997 look).</summary>
    public const int FormX = 8, FormY = 352;
    public const int W = 260, H = 88, RowH = 17, Zones = 4;
    /// <summary>The row of LBS. AVAILABLE and BALANCE starts under the fourth row's rule.</summary>
    public const int LbsY = Zones * RowH;
    /// <summary>The vertical rules: the DEFENSE column's edge, after the zone names, after "armor", between the pairs, after "chassis".</summary>
    public const int LabelCol = 19, ZoneCol = 63, ArmorCol = 97, PairCol = 157, ChassisCol = 198;
    /// <summary>The outer border's width on the left, right and bottom (the form's frame); the top is a single rule.</summary>
    public const int Border = 2;
    public const int BoxSize = 10, BoxY = 4;
    /// <summary>The [-] and [+] boxes' x: armour minus and plus, chassis minus and plus.</summary>
    public static readonly int[] BoxX = [100, 145, 201, 246];
    /// <summary>Where the 2D form prints a value (font 0x47's top-left): armour, chassis; the row's top + 5.</summary>
    public const int ArmorValueX = 116, ChassisValueX = 216, ValueY = 5;
    /// <summary>The zone names and the small labels' x, LBS. AVAILABLE's, its value's and its underline.</summary>
    public const int ZoneX = 24, ArmorLabelX = 67, ChassisLabelX = 161, LbsLabelX = 24, LbsValueX = 124, LbsValueY = LbsY + 4;
    public const int UnderlineX0 = 120, UnderlineX1 = 171, UnderlineY = LbsY + 14;
    public static readonly Rect2I Balance = new(177, LbsY + 3, 62, 13);
    public static readonly string[] ZoneNames = ["FRONT", "RIGHT", "LEFT", "REAR"];
    /// <summary>The page's rows: the eight cells, then LBS. AVAILABLE (a note) and BALANCE.</summary>
    public const int Cells = Zones * 2, LbsRow = Cells, BalanceRow = Cells + 1, Rows = Cells + 2;
    /// <summary>A step box's hit area reaches this far past its drawn edge (the boxes are small; the rules are further off).</summary>
    public const int Slop = 2;

    public static int CellOf(int zone, bool chassis) => zone * 2 + (chassis ? 1 : 0);
    public static (int Zone, bool Chassis) ZoneOf(int cell) => (cell / 2, cell % 2 == 1);

    /// <summary>A row's area: a cell from the zone name (armour) or the pair rule (chassis) to the next rule, LBS. AVAILABLE's row, BALANCE.</summary>
    public static Rect2I Row(int row)
    {
        if (row == BalanceRow) return Balance;
        if (row == LbsRow) return new(LabelCol + 1, LbsY + 1, Balance.Position.X - LabelCol - 4, H - Border - LbsY - 1);
        var (zone, chassis) = ZoneOf(row);
        int x0 = chassis ? PairCol + 1 : LabelCol + 1, x1 = chassis ? W - Border : PairCol + 1;
        return new(x0, zone * RowH + 1, x1 - x0, RowH - 1);
    }

    /// <summary>A cell's [-] or [+] box as drawn.</summary>
    public static Rect2I Box(int cell, bool plus)
    {
        var (zone, chassis) = ZoneOf(cell);
        return new(BoxX[(chassis ? 2 : 0) + (plus ? 1 : 0)], zone * RowH + BoxY, BoxSize, BoxSize);
    }

    /// <summary>Where a press on a cell's [-] or [+] counts: the box and a little round it.</summary>
    public static Rect2I Hit(int cell, bool plus) => Box(cell, plus).Grow(Slop);

    /// <summary>Where the 2D form prints a cell's value (left-aligned, font 0x47), from the block's left.</summary>
    public static int ValueLeft(int cell) => ZoneOf(cell).Chassis ? ChassisValueX : ArmorValueX;

    /// <summary>A value as the form prints it: the tenths with one decimal ("%1.1f").</summary>
    public static string Value(int tenths) => (tenths * 0.1).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
}
