using System.IO.Compression;

namespace I76.Editor.Geo;

/// <summary>A minimal PNG decoder: 8-bit RGB or RGBA, not interlaced (what the terrain tiles are). Returns RGB bytes.</summary>
public static class PngReader
{
    public static (int Width, int Height, byte[] Rgb) Decode(byte[] png)
    {
        if (png.Length < 33 || png[0] != 0x89 || png[1] != 'P' || png[2] != 'N' || png[3] != 'G') throw new InvalidDataException("not a PNG");
        int pos = 8, w = 0, h = 0, depth = 0, colour = 0, interlace = 0;
        var idat = new MemoryStream();
        while (pos + 8 <= png.Length)
        {
            int len = (png[pos] << 24) | (png[pos + 1] << 16) | (png[pos + 2] << 8) | png[pos + 3];
            string tag = System.Text.Encoding.ASCII.GetString(png, pos + 4, 4);
            int d = pos + 8;
            if (len < 0 || d + len > png.Length) throw new InvalidDataException("truncated PNG");
            if (tag == "IHDR")
            {
                w = (png[d] << 24) | (png[d + 1] << 16) | (png[d + 2] << 8) | png[d + 3];
                h = (png[d + 4] << 24) | (png[d + 5] << 16) | (png[d + 6] << 8) | png[d + 7];
                depth = png[d + 8]; colour = png[d + 9]; interlace = png[d + 12];
            }
            else if (tag == "IDAT") idat.Write(png, d, len);
            else if (tag == "IEND") break;
            pos = d + len + 4;
        }
        if (depth != 8 || colour is not (2 or 6) || interlace != 0) throw new NotSupportedException($"PNG depth {depth}, colour type {colour}, interlace {interlace}: only 8-bit RGB(A)");
        int bpp = colour == 2 ? 3 : 4, stride = w * bpp;
        var raw = new byte[(stride + 1) * h];
        idat.Position = 0;
        using (var z = new ZLibStream(idat, CompressionMode.Decompress)) z.ReadExactly(raw);
        var pix = new byte[stride * h];
        for (int y = 0; y < h; y++)
        {
            int f = raw[y * (stride + 1)], src = y * (stride + 1) + 1, dst = y * stride;
            for (int x = 0; x < stride; x++)
            {
                int a = x >= bpp ? pix[dst + x - bpp] : 0, b = y > 0 ? pix[dst - stride + x] : 0, c = x >= bpp && y > 0 ? pix[dst - stride + x - bpp] : 0;
                int v = raw[src + x];
                pix[dst + x] = (byte)(f switch
                {
                    0 => v, 1 => v + a, 2 => v + b, 3 => v + ((a + b) >> 1),
                    4 => v + Paeth(a, b, c),
                    _ => throw new InvalidDataException($"PNG filter {f}"),
                });
            }
        }
        if (bpp == 3) return (w, h, pix);
        var rgb = new byte[w * h * 3];
        for (int i = 0; i < w * h; i++) { rgb[i * 3] = pix[i * 4]; rgb[i * 3 + 1] = pix[i * 4 + 1]; rgb[i * 3 + 2] = pix[i * 4 + 2]; }
        return (w, h, rgb);
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }
}
