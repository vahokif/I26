namespace I76.Formats.Shell;

/// <summary>
/// One row of the Nitro host form's WEAPON RESTRICTIONS checklist (docs/hidden-content.md §4): its group (0 All Weapons, 1 slug
/// throwers, 2 arc-fire, 3 missiles, 4 flamethrowers, 5 droppers), the weapons it stands for (GDF class and tier; tier −1 the
/// whole class, class −1 every weapon), where its box is (a column and row of <see cref="RestrictionTable"/>'s grids) and its name.
/// </summary>
public sealed record RestrictionRow(int Group, int Class, int Tier, int Column, int Row, string Name)
{
    /// <summary>A group's header ("All Slug Throwers") or All Weapons.</summary>
    public bool IsHeader => Tier == -1;
}

/// <summary>
/// The checklist's table in nitshell.dll, read from the install's own copy (no game table is kept here): 43 rows of 0x38 bytes
/// ({group, class, tier, checked, column, row, name[32]}), preceded directly by the box columns' x (6 ints) and the rows' y
/// (18 ints), relative to the popup (bank 30 sprite 43 at 34, 25). It is found by its contents: the first row is "All Weapons"
/// (class and tier -1), and the whole table must pass the checks in <see cref="Read"/>.
/// </summary>
public static class RestrictionTable
{
    public const int Count = 43, Columns = 6, Rows = 18, RowSize = 0x38;

    /// <summary>The offset of the first row in the dll, or -1: the record named "All Weapons" with the whole table valid behind it.</summary>
    private static int Find(byte[]? dll)
    {
        if (dll is null) return -1;
        ReadOnlySpan<byte> sig = "All Weapons\0"u8;
        for (int at = dll.AsSpan().IndexOf(sig); at >= 0;)
        {
            if (at >= 24 && Parse(dll, at - 24) is not null) return at - 24;
            int next = dll.AsSpan(at + 1).IndexOf(sig);
            at = next < 0 ? -1 : at + 1 + next;
        }
        return -1;
    }

    private static List<RestrictionRow>? Parse(byte[] dll, int at)
    {
        if (at + Count * RowSize > dll.Length) return null;
        var rows = new List<RestrictionRow>();
        for (int i = 0; i < Count; i++)
        {
            int o = at + i * RowSize;
            var r = new RestrictionRow(Bin.I32(dll, o), Bin.I32(dll, o + 4), Bin.I32(dll, o + 8), Bin.I32(dll, o + 16), Bin.I32(dll, o + 20), Bin.CStr(dll, o + 24, 32));
            if (r.Group is < 0 or > 5 || r.Class is < -1 or > 6 || r.Tier is < -1 or > 199 || r.Column is < 0 or >= Columns || r.Row is < 0 or >= Rows || r.Name.Length == 0)
                return null;
            rows.Add(r);
        }
        return rows[0] is { Class: -1, Tier: -1 } ? rows : null;
    }

    /// <summary>The rows, or empty when the dll is missing or holds no such table.</summary>
    public static List<RestrictionRow> Read(byte[]? dll) => Find(dll) is var at and >= 0 ? Parse(dll!, at)! : [];

    /// <summary>The boxes' x (per column) and y (per row), relative to the popup; empty arrays if not found.</summary>
    public static (int[] X, int[] Y) Grid(byte[]? dll)
    {
        int at = Find(dll);
        int ry = at - Rows * 4, cx = ry - Columns * 4;
        if (at < 0 || cx < 0) return ([], []);
        var x = Enumerable.Range(0, Columns).Select(i => Bin.I32(dll!, cx + i * 4)).ToArray();
        var y = Enumerable.Range(0, Rows).Select(i => Bin.I32(dll!, ry + i * 4)).ToArray();
        bool rising(int[] v) => v.Zip(v.Skip(1), (a, b) => a < b).All(t => t);
        return x.All(v => v is > 0 and < 573) && y.All(v => v is > 0 and < 430) && rising(y) ? (x, y) : ([], []);
    }
}
