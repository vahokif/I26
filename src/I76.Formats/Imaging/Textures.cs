namespace I76.Formats.Imaging;

/// <summary>An 8-bit indexed image. <see cref="Palette"/> is RGB565 for .m16, null for .map.</summary>
public sealed record IndexedTexture(int Width, int Height, byte[] Indices, ushort[]? Palette);

/// <summary>.m16 and .map, a port of the reference reader (docs/formats/texture-formats.md).</summary>
public static class TextureReader
{
    public const int TransparentIndex = 255;

    public static (byte R, byte G, byte B) DecodeRgb565(ushort c) =>
        ((byte)(((c >> 11) & 31) * 255 / 31), (byte)(((c >> 5) & 63) * 255 / 63), (byte)((c & 31) * 255 / 31));

    /// <summary>8-bit indices followed by the texture's own RGB565 palette.</summary>
    public static IndexedTexture ParseM16(byte[] d)
    {
        int width = (int)Bin.U32(d, 0);
        int height = (int)(Bin.U32(d, 4) & 0x7FFFFFFF);
        long baseAt = 8 + (long)width * height;
        if (baseAt + 4 > d.Length) throw new InvalidDataException(".m16 truncated");
        uint count = Bin.U32(d, (int)baseAt);
        long end = baseAt + 4 + count * 2L;
        if (end != d.Length) throw new InvalidDataException($".m16 size mismatch: predicted {end}, actual {d.Length}");
        var pal = new ushort[count];
        for (int i = 0; i < count; i++) pal[i] = Bin.U16(d, (int)baseAt + 4 + 2 * i);
        return new IndexedTexture(width, height, d.AsSpan(8, width * height).ToArray(), pal);
    }

    /// <summary>8-bit indices against a palette supplied from elsewhere.</summary>
    public static IndexedTexture ParseMap(byte[] d)
    {
        int width = (int)Bin.U32(d, 0), height = (int)Bin.U32(d, 4);
        if (8 + (long)width * height != d.Length)
            throw new InvalidDataException($".map size mismatch: predicted {8 + (long)width * height}, actual {d.Length}");
        return new IndexedTexture(width, height, d.AsSpan(8).ToArray(), null);
    }

    public static IndexedTexture Parse(string name, byte[] d) =>
        name.EndsWith(".m16", StringComparison.OrdinalIgnoreCase) ? ParseM16(d)
        : name.EndsWith(".map", StringComparison.OrdinalIgnoreCase) ? ParseMap(d)
        : throw new InvalidDataException($"not a texture: {name}");

    /// <summary>
    /// RGB8 exactly as the reference reader's RGB conversion: magenta for index 255 only
    /// when the palette is too short to hold it, black for any other index
    /// past the palette, greyscale for a .map.
    /// </summary>
    public static byte[] ToRgbReference(IndexedTexture t)
    {
        var o = new byte[t.Indices.Length * 3];
        for (int i = 0; i < t.Indices.Length; i++)
        {
            byte p = t.Indices[i];
            byte r, g, b;
            if (t.Palette is not { } pal) (r, g, b) = (p, p, p);
            else if (p == TransparentIndex && p >= pal.Length) (r, g, b) = (255, 0, 255);
            else (r, g, b) = DecodeRgb565(p < pal.Length ? pal[p] : (ushort)0);
            o[i * 3] = r; o[i * 3 + 1] = g; o[i * 3 + 2] = b;
        }
        return o;
    }

    /// <summary>
    /// RGBA8. Index 255 is the colour key: alpha 0 wherever it occurs (whether
    /// a material honours the key is its own decision, Q34). An .m16 index past
    /// its palette is black, as in the reference reader. A .map without a
    /// palette comes out greyscale.
    /// </summary>
    public static byte[] ToRgba(IndexedTexture t, Palette? act = null, bool keyIndex255 = true)
    {
        var o = new byte[t.Indices.Length * 4];
        for (int i = 0; i < t.Indices.Length; i++)
        {
            byte p = t.Indices[i];
            byte r, g, b;
            if (t.Palette is { } pal)
                (r, g, b) = DecodeRgb565(p < pal.Length ? pal[p] : (ushort)0);
            else if (act is not null)
                (r, g, b) = act[p];
            else
                (r, g, b) = (p, p, p);
            o[i * 4] = r; o[i * 4 + 1] = g; o[i * 4 + 2] = b;
            o[i * 4 + 3] = keyIndex255 && p == TransparentIndex ? (byte)0 : (byte)255;
        }
        return o;
    }

    /// <summary>Whether an RGBA8 image from <see cref="ToRgba"/> has any keyed (alpha 0) texel.</summary>
    public static bool HasKey(byte[] rgba)
    {
        for (int i = 3; i < rgba.Length; i += 4) if (rgba[i] == 0) return true;
        return false;
    }

    /// <summary>
    /// RGBA8 as a face draws the texture. A keyed draw (cut-out, translucent) drops index 255, so its texels take their
    /// neighbours' colour (<see cref="BleedUnderKey"/>). An opaque (unkeyed) draw shows index 255 as a colour like any
    /// other, black past the end of the palette: the original keys only its blended primitives, and draws the rest of a
    /// texture's index-255 texels as they are (a school bus's black stripes and lettering, most of the limo's paint).
    /// </summary>
    public static byte[] DrawRgba(IndexedTexture t, Palette? act, bool keyed)
    {
        var o = ToRgba(t, act);
        if (keyed && HasKey(o)) BleedUnderKey(o, t.Width, t.Height);
        return o;
    }

    /// <summary>
    /// Give keyed texels (alpha 0) the colour of an opaque neighbour, up to four texels in, so filtering and mipmaps do
    /// not pull the key colour into the edges of a keyed draw. Alpha is untouched. Only for keyed draws: an opaque face
    /// shows the key's texels in their own colour (<see cref="DrawRgba"/>).
    /// </summary>
    public static void BleedUnderKey(byte[] p, int w, int h)
    {
        for (int pass = 0; pass < 4; pass++)
        {
            var src = (byte[])p.Clone();
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = (y * w + x) * 4;
                    if (src[i + 3] != 0 || (pass > 0 && (src[i] | src[i + 1] | src[i + 2]) != 0)) continue;
                    int n = 0, r = 0, g = 0, b = 0;
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int xx = x + dx, yy = y + dy;
                            if (xx < 0 || yy < 0 || xx >= w || yy >= h) continue;
                            int j = (yy * w + xx) * 4;
                            if (src[j + 3] == 0 && (pass == 0 || (src[j] | src[j + 1] | src[j + 2]) == 0)) continue;
                            r += src[j]; g += src[j + 1]; b += src[j + 2]; n++;
                        }
                    if (n == 0) { if (pass == 0) { p[i] = p[i + 1] = p[i + 2] = 0; } continue; }
                    p[i] = (byte)(r / n); p[i + 1] = (byte)(g / n); p[i + 2] = (byte)(b / n);
                }
        }
    }
}

/// <summary>A .act palette: 256 RGB triples, 768 bytes.</summary>
public sealed class Palette
{
    public const int Size = 768;
    public byte[] Rgb { get; }

    public Palette(byte[] d)
    {
        if (d.Length != Size) throw new InvalidDataException($".act must be {Size} bytes, got {d.Length}");
        Rgb = d;
    }

    public (byte R, byte G, byte B) this[int i] => (Rgb[i * 3], Rgb[i * 3 + 1], Rgb[i * 3 + 2]);
}
