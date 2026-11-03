using I76.Formats.Bwd2;
using I76.Formats.Vfs;

namespace I76.Formats.Shell;

/// <summary>
/// A car's MAKE / MODEL as the shell names it: its body's (.vdf) VDFC, the first 20 bytes ("Courcheval Courchel"), which the
/// melee forms' car list, the network roster's game popup and the STANDINGS show.
/// </summary>
public static class CarNames
{
    /// <summary>The make and model in a body file (VDFC's first 20 bytes), or null without one.</summary>
    public static string? OfBody(Bwd2File vdf) =>
        vdf.First("VDFC") is { } c ? Bin.CStr(vdf.Data, c.PayloadOffset, 20).Trim() : null;

    /// <summary>
    /// The make and model of a car file (.vcf: its VCFC's body) or of a body (.vdf), as the files at hand name it; null when
    /// there is no such file or it says none.
    /// </summary>
    public static string? Of(AssetVfs vfs, string file)
    {
        try
        {
            if (file.EndsWith(".vcf", StringComparison.OrdinalIgnoreCase))
                return vfs.TryRead(file) is { } vcf && new Bwd2File(vcf).VcfcFields() is { } f ? Of(vfs, f.Vdf) : null;
            if (!file.EndsWith(".vdf", StringComparison.OrdinalIgnoreCase) || vfs.TryRead(file) is not { } vdf) return null;
            return OfBody(new Bwd2File(vdf)) is { Length: > 0 } n ? n : null;
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or IndexOutOfRangeException) { return null; }
    }

    /// <summary>The body a car file's content names (its VCFC's .vdf, lower case), or null for bytes that are no car file.</summary>
    public static string? BodyOf(byte[] vcf)
    {
        try { return new Bwd2File(vcf).VcfcFields() is { Vdf.Length: > 0 } f ? f.Vdf.ToLowerInvariant() : null; }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or IndexOutOfRangeException) { return null; }
    }
}
