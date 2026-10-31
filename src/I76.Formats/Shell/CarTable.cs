using I76.Formats;

namespace I76.Formats.Shell;

/// <summary>One row of nitscar.def / nitcar.def (the car table loader): the art id, the variants (total and built-in) and the file stem.</summary>
public sealed record CarRow(int ArtId, int Variants, int BuiltIn, string Stem);

/// <summary>
/// The car tables of the Nitro shell: u32 count, then 0x1c-byte rows. GOG ships neither nitscar.def
/// nor nitcar.def (the shell writes them from its built-in tables on first run), so the built-in tables stand in for missing files.
/// </summary>
public static class CarTable
{
    public static List<CarRow> Parse(byte[] d)
    {
        var rows = new List<CarRow>();
        if (d.Length < 4) return rows;
        int n = (int)Bin.U32(d, 0);
        for (int i = 0; i < n && 4 + (i + 1) * 0x1c <= d.Length; i++)
        {
            int o = 4 + i * 0x1c;
            rows.Add(new CarRow((int)Bin.U32(d, o), (int)Bin.U32(d, o + 4), (int)Bin.U32(d, o + 8), Bin.CStr(d, o + 12, 16)));
        }
        return rows;
    }

    /// <summary>
    /// The base game's MAKE / MODEL list, i76car.def's built-in table in the original front-end DLL: id (the art index), stem and
    /// variant count. The Royale and the Police Car have each other's position.
    /// </summary>
    public static readonly (int Id, string Stem, int Variants)[] BaseModels =
    [
        (0, "valepre", 3), (1, "vastryd", 3), (2, "vazamz", 3), (3, "vccameo", 3), (4, "vcgcour", 3), (5, "vcmanta", 3), (7, "vcroyal", 3), (6, "vcpolic", 3),
        (8, "vcvan", 3), (9, "vdlight", 3), (10, "vdstag", 3), (11, "vfcoupe", 3), (12, "vfdclyd", 3), (13, "vfpalmn", 3), (14, "vfratlr", 3), (15, "vfypony", 3),
        (16, "vgoon", 3), (17, "vjsovrn", 3), (18, "vmxmarx", 3), (19, "vpjrabt", 3), (20, "vppirna", 3), (21, "vavikea", 3), (22, "vxbus", 1),
    ];

    /// <summary>nitcar.def's rows, the Nitro melee cars: the install's file if there is one, else the shell's built-in table (37).</summary>
    public static List<CarRow> NitroMelee(Vfs.GameInstall install) =>
        install.FindFile("nitcar.def") is { } f && Parse(File.ReadAllBytes(f)) is { Count: > 0 } r ? r : BuiltInNitcar(NitShell(install));

    /// <summary>nitscar.def's rows, the scenario's cars: the file if there is one, else the shell's built-in table (31).</summary>
    public static List<CarRow> NitroScenario(Vfs.GameInstall install) =>
        install.FindFile("nitscar.def") is { } f && Parse(File.ReadAllBytes(f)) is { Count: > 0 } r ? r : BuiltInNitscar(NitShell(install));

    public static byte[]? NitShell(Vfs.GameInstall install) => install.FindFile("nitshell.dll") is { } d ? File.ReadAllBytes(d) : null;

    /// <summary>The model stems the melee's MAKE / MODEL list offers on this install (the base table, or Nitro's nitcar.def).</summary>
    public static HashSet<string> OfferedStems(Vfs.GameInstall install) =>
        new(install.IsNitro ? NitroMelee(install).Select(r => r.Stem) : BaseModels.Select(m => m.Stem), StringComparer.OrdinalIgnoreCase);

    /// <summary>nitcar.def's built-in rows: the 37 melee cars in nitshell.dll's data, found by content (the table starts with the Grappler).</summary>
    public static List<CarRow> BuiltInNitcar(byte[]? dll) => BuiltIn(dll, "vagrapl", 37, "nitcar");

    /// <summary>nitscar.def's built-in rows: the 31 scenario cars in nitshell.dll's data, found by content (the table starts with the Vallejo Eagle Premier).</summary>
    public static List<CarRow> BuiltInNitscar(byte[]? dll) => BuiltIn(dll, "valepre", 31, "nitscar");

    /// <summary>
    /// A built-in table read from the install's own nitshell.dll: the rows are 0x1c-byte records, so the table is where a record
    /// carries the first model's file stem and exactly <paramref name="count"/> records in a row pass the sanity check. Empty if
    /// the dll is missing or no such run exists.
    /// </summary>
    private static List<CarRow> BuiltIn(byte[]? dll, string firstStem, int count, string name)
    {
        if (dll is null) return [];
        var sig = System.Text.Encoding.ASCII.GetBytes(firstStem + "\0");
        for (int at = dll.AsSpan().IndexOf(sig); at >= 0;)
        {
            // a whole run of exactly count records: the record before and the one after are not rows (the scenario table's
            // first stem also starts a record inside the melee table, which is longer)
            if (at >= 12 && Rows(dll, at - 12, count) is { } rows && Rows(dll, at - 12 - 0x1c, 1) is null && Rows(dll, at - 12 + count * 0x1c, 1) is null) return rows;
            int next = dll.AsSpan(at + 1).IndexOf(sig);
            at = next < 0 ? -1 : at + 1 + next;
        }
        Console.Error.WriteLine($"{name} table: not found in nitshell.dll");
        return [];
    }

    /// <summary>The <paramref name="count"/> records from <paramref name="at"/>, or null if any fails the check.</summary>
    private static List<CarRow>? Rows(byte[] dll, int at, int count)
    {
        if (at + count * 0x1c > dll.Length) return null;
        var rows = new List<CarRow>();
        for (int r = 0; r < count; r++)
        {
            int q = at + r * 0x1c;
            var row = new CarRow((int)Bin.U32(dll, q), (int)Bin.U32(dll, q + 4), (int)Bin.U32(dll, q + 8), Bin.CStr(dll, q + 12, 16));
            if (row.Stem.Length < 2 || row.Stem[0] != 'v' || row.ArtId is < 0 or > 99 || row.Variants is < 1 or > 9 || row.BuiltIn is < 1 or > 9) return null;
            rows.Add(row);
        }
        return rows;
    }
}
