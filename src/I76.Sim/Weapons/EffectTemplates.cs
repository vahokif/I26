using I76.Formats.Bwd2;

namespace I76.Sim.Weapons;

/// <summary>
/// The explosion templates of a mission. The original keys each by its XGEO record 0 name (X1_CARX1), not its file, and a
/// file whose key is already registered is discarded: in stock data xoilw1.xdf (the oil pump's) carries xgasx1.xdf's
/// key X1_GASX1, so whichever registers first defines both (100 / 25 m or 100 / 85 m). Registration follows the load:
/// xcarx1, xbldx1 and xcars1 at startup, then each file as the mission's objects name it, in their order.
/// </summary>
public sealed class EffectTemplates(Func<string, byte[]?> read)
{
    /// <summary>The three templates the original preloads before any mission object.</summary>
    public static readonly string[] Preloaded = ["xcarx1.xdf", "xbldx1.xdf", "xcars1.xdf"];

    private readonly Dictionary<string, EffectDef?> _byFile = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, EffectDef> _byKey = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The template a file stands for: its own, or the one that registered its key first (null if missing).</summary>
    public EffectDef? this[string file]
    {
        get
        {
            if (_byFile.TryGetValue(file, out var e)) return e;
            e = read(file) is { } d ? EffectDef.Read(file, d) : null;
            // a file without XGEO (xcont1) has no key and stays its own
            if (e?.Geometry is [var root, ..] && !_byKey.TryAdd(root.Name, e)) e = _byKey[root.Name];
            return _byFile[file] = e;
        }
    }

    /// <summary>Registers files in load order (the first with a key keeps it).</summary>
    public void Register(IEnumerable<string> files)
    {
        foreach (var f in files) _ = this[f];
    }
}
