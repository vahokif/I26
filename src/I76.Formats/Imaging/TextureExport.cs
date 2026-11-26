using I76.Formats.Assembly;
using I76.Formats.Export;
using I76.Formats.Vfs;

namespace I76.Formats.Imaging;

/// <summary>
/// Every texture of an install as a PNG, the input of an HD texture pack's upscaler (`i76 export-textures`):
/// DIR/&lt;stem&gt;.png, the right way up, the colour key (index 255) transparent, and DIR/bottom-up.txt, the stems the game
/// stores bottom up (a .map's and an .m16's rows; a .vqm's are top down), whose upscales a block format keeps in that order.
/// A stem held by several formats exports the one the game draws (.m16 first). An .m16 carries its own colours; a .map or
/// .vqm is coloured by the palette (t01.act unless said), grey without one.
/// </summary>
public static class TextureExport
{
    /// <summary>The install's textures into <paramref name="dir"/>; the number written.</summary>
    public static int Run(AssetVfs vfs, string dir, string paletteName = "t01.act", TextWriter? log = null)
    {
        var res = new TextureResolver(vfs);
        var pal = vfs.TryRead(paletteName) is { } pd ? new Palette(pd) : null;
        if (pal is null) log?.WriteLine($"export textures: palette {paletteName} not found, .map and .vqm come out grey");
        var stems = vfs.Names.Where(n => TextureResolver.Extensions.Contains(Path.GetExtension(n).ToLowerInvariant()))
            .Select(n => Path.GetFileNameWithoutExtension(n).ToLowerInvariant()).Distinct();
        return Write(stems, s => res.FindByStem(s) is { } m && res.Load(m) is { } t ? (m, t) : null, pal, dir);
    }

    /// <summary>The stems' textures (<paramref name="load"/>: the member the game draws and its picture, or null) as PNGs
    /// and bottom-up.txt, in the stems' ordinal order; the number written.</summary>
    public static int Write(IEnumerable<string> stems, Func<string, (string Member, IndexedTexture Texture)?> load, Palette? pal, string dir)
    {
        Directory.CreateDirectory(dir);
        var bottomUp = new List<string>();
        int n = 0;
        foreach (var stem in stems.OrderBy(s => s, StringComparer.Ordinal))
        {
            if (load(stem) is not { } hit) continue;
            var t = hit.Texture;
            var rgba = TextureReader.ToRgba(t, pal);
            if (!hit.Member.EndsWith(".vqm", StringComparison.OrdinalIgnoreCase))
            {
                FlipRows(rgba, t.Width, t.Height);
                bottomUp.Add(stem);
            }
            File.WriteAllBytes(Path.Combine(dir, stem + ".png"), PngWriter.Encode(t.Width, t.Height, rgba, alpha: true));
            n++;
        }
        File.WriteAllLines(Path.Combine(dir, "bottom-up.txt"), bottomUp);
        return n;
    }

    private static void FlipRows(byte[] rgba, int w, int h)
    {
        var row = new byte[w * 4];
        for (int y = 0; y < h / 2; y++)
        {
            int a = y * w * 4, b = (h - 1 - y) * w * 4;
            Array.Copy(rgba, a, row, 0, row.Length);
            Array.Copy(rgba, b, rgba, a, row.Length);
            Array.Copy(row, 0, rgba, b, row.Length);
        }
    }
}
