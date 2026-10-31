using I76.Formats.Vfs;

namespace I76.Formats.Exe;

/// <summary>A tactics rule: its predicate (a code address in the executable it was read from, 0 for none), name, options (state ids) and weights per aggression row 0..4.</summary>
public sealed record PtRule(string Name, uint Pred, int[] Options, int[][] Weights);

/// <summary>
/// A tactics state: name, id (= its index), tick, enter and on-match handlers, preconditions (all must hold for the state to be
/// chosen), transitions (predicate → pushed state) and rules (terminate conditions). The handlers are code addresses in the
/// executable the table was read from (0 for none); the AI binds them to its own implementations by where they appear in the
/// table, never by their values.
/// </summary>
public sealed record PtEntry(int Id, string Name, uint Tick, uint Enter, uint OnMatch, uint[] Pre, (uint Pred, int Next)[] Trans, PtRule[] Rules);

/// <summary>
/// The AI's tactics table (the "Ptable"), read from the player's own game executable (i76.exe or nitro.exe) at run time, as
/// every other asset is read from the install. The table lives in the executable's initialised data: one record per state,
/// a fixed-size block of <see cref="EntrySize"/> bytes:
/// <code>
///   +0x000 char name[0x50]       +0x050 int id             +0x054 tick          +0x058 enter
///   +0x05c int nPre              +0x060 pre[10]            +0x088 int nTrans    +0x08c {pred, int next} trans[10]
///   +0x0dc int nRules            +0x0e0 Rule rules[10]     +0x1350 on-match
/// Rule (RuleSize bytes): +0x00 pred, +0x04 char name[0x50], +0x54 int nOptions, +0x58 int option[16], +0x98 int weight[5][16]
/// </code>
/// It is found by its contents, not by an address: the record named "tactics init" with id 0 followed, one record further on,
/// by "road_ram" with id 1, then every record whose id equals its index; each must pass the checks in <see cref="Decode"/>
/// (counts in range, state ids in the table, handlers inside the executable's code). The same reader serves the GOG
/// Interstate '76 and Nitro Pack executables and the 1997 CD release, whose tables differ only in where their code is.
/// </summary>
public static class TacticsTable
{
    public const int EntrySize = 0x1354, RuleSize = 0x1d8, NameSize = 0x50;
    private const int MaxPre = 10, MaxTrans = 10, MaxRules = 10, MaxOptions = 16, Rows = 5, MaxEntries = 64;

    /// <summary>The executable the table is read from: the install's own (nitro.exe for the Nitro Pack, else i76.exe), or null if it has none.</summary>
    public static string? ExecutableOf(GameInstall install) =>
        install.IsNitro ? install.FindFile("nitro.exe") ?? install.FindFile("i76.exe") : install.FindFile("i76.exe") ?? install.FindFile("nitro.exe");

    private static readonly Dictionary<string, PtEntry[]> Cache = new(StringComparer.Ordinal);

    /// <summary>The install's table (read once per executable). Throws <see cref="FileNotFoundException"/> without an executable, <see cref="InvalidDataException"/> if it holds no table.</summary>
    public static PtEntry[] For(GameInstall install)
    {
        var exe = ExecutableOf(install) ?? throw new FileNotFoundException(
            I76.Formats.Installers.ExeRepair.MissingMessage(install));
        lock (Cache)
        {
            if (Cache.TryGetValue(exe, out var t)) return t;
            t = Read(File.ReadAllBytes(exe)) ?? throw new InvalidDataException($"{exe}: no tactics table found; is this Interstate '76's executable?");
            Cache[exe] = t;
            return t;
        }
    }

    /// <summary>The table in an executable's bytes, or null if there is none.</summary>
    public static PtEntry[]? Read(byte[] exe)
    {
        var code = CodeRanges(exe);
        if (code.Count == 0) return null;
        ReadOnlySpan<byte> sig = "tactics init\0"u8;
        for (int at = exe.AsSpan().IndexOf(sig); at >= 0;)
        {
            if (Decode(exe, at, code) is { } t) return t;
            int next = exe.AsSpan(at + 1).IndexOf(sig);
            at = next < 0 ? -1 : at + 1 + next;
        }
        return null;
    }

    /// <summary>The records from <paramref name="at"/> while each one's id is its index; null unless they make a whole, consistent table.</summary>
    private static PtEntry[]? Decode(byte[] b, int at, List<(uint Lo, uint Hi)> code)
    {
        bool Code(uint a) => code.Any(r => a >= r.Lo && a < r.Hi);
        bool Handler(uint a) => a == 0 || Code(a);
        string? Name(int o)
        {
            if (o + NameSize > b.Length) return null;
            int n = Array.IndexOf(b, (byte)0, o, NameSize) - o;
            if (n <= 0) return null;
            for (int i = 0; i < n; i++) if (b[o + i] is < 0x20 or > 0x7e) return null;
            return System.Text.Encoding.ASCII.GetString(b, o, n);
        }
        int count = 0;
        while (count < MaxEntries && at + (count + 1) * EntrySize <= b.Length && Name(at + count * EntrySize) is not null
               && Bin.I32(b, at + count * EntrySize + 0x50) == count) count++;
        if (count < 2 || Name(at + EntrySize) != "road_ram") return null;

        var entries = new PtEntry[count];
        for (int k = 0; k < count; k++)
        {
            int e = at + k * EntrySize;
            uint tick = Bin.U32(b, e + 0x54), enter = Bin.U32(b, e + 0x58), match = Bin.U32(b, e + 0x1350);
            int nPre = Bin.I32(b, e + 0x5c), nTrans = Bin.I32(b, e + 0x88), nRules = Bin.I32(b, e + 0xdc);
            if (nPre is < 0 or > MaxPre || nTrans is < 0 or > MaxTrans || nRules is < 0 or > MaxRules) return null;
            if (!Handler(tick) || !Handler(enter) || !Handler(match)) return null;
            var pre = new uint[nPre];
            for (int i = 0; i < nPre; i++) if (!Code(pre[i] = Bin.U32(b, e + 0x60 + 4 * i))) return null;
            var trans = new (uint, int)[nTrans];
            for (int i = 0; i < nTrans; i++)
            {
                uint p = Bin.U32(b, e + 0x8c + 8 * i);
                int next = Bin.I32(b, e + 0x90 + 8 * i);
                if (!Code(p) || next < 0 || next >= count) return null;
                trans[i] = (p, next);
            }
            var rules = new PtRule[nRules];
            for (int r = 0; r < nRules; r++)
            {
                int q = e + 0xe0 + r * RuleSize;
                uint pred = Bin.U32(b, q);
                int n = Bin.I32(b, q + 0x54);
                if (Name(q + 4) is not { } name || !Handler(pred) || n is < 0 or > MaxOptions) return null;
                var options = new int[n];
                for (int i = 0; i < n; i++)
                {
                    options[i] = Bin.I32(b, q + 0x58 + 4 * i);
                    if (options[i] < 0 || options[i] >= count) return null;
                }
                var weights = new int[Rows][];
                for (int row = 0; row < Rows; row++)
                {
                    weights[row] = new int[n];
                    for (int i = 0; i < n; i++) weights[row][i] = Bin.I32(b, q + 0x98 + row * 4 * MaxOptions + 4 * i);
                }
                rules[r] = new PtRule(name, pred, options, weights);
            }
            entries[k] = new PtEntry(k, Name(e)!, tick, enter, match, pre, trans, rules);
        }
        return entries;
    }

    /// <summary>The virtual address ranges of a PE image's executable sections (empty if it is not one).</summary>
    private static List<(uint Lo, uint Hi)> CodeRanges(byte[] b)
    {
        var list = new List<(uint, uint)>();
        if (b.Length < 0x40 || Bin.U16(b, 0) != 0x5a4d) return list;
        int pe = Bin.I32(b, 0x3c);
        if (pe < 0 || pe + 0xf8 > b.Length || Bin.U32(b, pe) != 0x4550) return list;
        int sections = Bin.U16(b, pe + 6), opt = Bin.U16(b, pe + 20);
        uint imageBase = Bin.U32(b, pe + 24 + 28);
        for (int i = 0; i < sections; i++)
        {
            int o = pe + 24 + opt + i * 40;
            if (o + 40 > b.Length) break;
            uint vsize = Bin.U32(b, o + 8), va = Bin.U32(b, o + 12), flags = Bin.U32(b, o + 36);
            if ((flags & 0x20000020) != 0) list.Add((imageBase + va, imageBase + va + vsize));   // code, or executable
        }
        return list;
    }
}
