namespace I76.Formats.Imaging;

/// <summary>
/// Stock textures whose 16-bit copy has a slip that its 8-bit art does not, drawn as the art means in the modern look (the 1997 look
/// draws the file as it is, as the original's 3D-card path does). One so far: CA_1LT_6.M16, the forecourt round the small factory
/// buildings bcaik_s1 to bcaik_s8 (three in T12, one in S03). Its one-texel border is pure green (0x07E0) and pure blue (0x001F),
/// the only such colours in any stock .m16, where the 8-bit ca_1lt_6.map has two dark shades of its asphalt (palette 187 and 190); the
/// same art's other forecourt, CA_1LT_1.M16, has greys there, and these take the two it uses most for those two shades. Drawn with
/// filtering, the border showed as a green and blue dashed outline round every such building.
/// </summary>
public static class TextureRepairs
{
    private static readonly Dictionary<string, (ushort From, ushort To)[]> Entries = new(StringComparer.OrdinalIgnoreCase)
    {
        ["CA_1LT_6.M16"] = [(0x07E0, 0x4A29), (0x001F, 0x4228)],
    };

    private static string FileName(string member) => member[(member.LastIndexOfAny(['/', '\\', ':']) + 1)..];

    /// <summary>Whether the member is one with a repair.</summary>
    public static bool Has(string member) => Entries.ContainsKey(FileName(member));

    /// <summary>
    /// The texture with its slip repaired: the same texels, each listed palette entry changed where it holds the slip's colour (a
    /// pack or mod's own copy without the slip comes back unchanged). Null when the member has no repair or the texture no palette.
    /// </summary>
    public static IndexedTexture? Repaired(string member, IndexedTexture t)
    {
        if (!Entries.TryGetValue(FileName(member), out var swaps) || t.Palette is not { } pal) return null;
        var p = (ushort[])pal.Clone();
        for (int i = 0; i < p.Length; i++)
            foreach (var (from, to) in swaps)
                if (p[i] == from) p[i] = to;
        return t with { Palette = p };
    }
}
