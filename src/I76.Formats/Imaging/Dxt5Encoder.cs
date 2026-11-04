namespace I76.Formats.Imaging;

/// <summary>
/// A BC3 (DXT5) encoder for what Godot's own compressor cannot reach: export templates have no texture compressors, so a
/// texture made at run time (the road layers) would stay RGBA8, four times the video memory. Each 4x4 block gets an alpha
/// block (the block's extremes and 8 levels between) and a colour block (the principal axis of its colours, quantised to
/// 565, each texel to its nearest of four). Plain and quick (about 30 ms for a 1024 px texture with mipmaps, half that for a
/// road layer, whose clear ground is blocks of one colour), a little below a good offline encoder, which is no matter for masks
/// and road paint.
/// </summary>
public static class Dxt5Encoder
{
    /// <summary>The bytes of one level: <paramref name="rgba"/> (from <paramref name="offset"/>, w x h x 4), as 16 bytes a 4x4 block, rows of blocks top to bottom.</summary>
    public static byte[] EncodeLevel(byte[] rgba, int offset, int w, int h)
    {
        int bw = (w + 3) / 4, bh = (h + 3) / 4;
        var o = new byte[bw * bh * 16];
        Span<int> px = stackalloc int[16 * 4];
        for (int by = 0; by < bh; by++)
            for (int bx = 0; bx < bw; bx++)
            {
                for (int i = 0; i < 16; i++)
                {
                    // texels past the edge (a level under 4 px) repeat the last one
                    int x = Math.Min(w - 1, bx * 4 + (i & 3)), y = Math.Min(h - 1, by * 4 + (i >> 2));
                    int p = offset + (y * w + x) * 4;
                    px[i * 4] = rgba[p]; px[i * 4 + 1] = rgba[p + 1]; px[i * 4 + 2] = rgba[p + 2]; px[i * 4 + 3] = rgba[p + 3];
                }
                Block(px, o.AsSpan((by * bw + bx) * 16, 16));
            }
        return o;
    }

    /// <summary>A whole mipmap chain, level 0 first, each level as <see cref="EncodeLevel"/> (the layout of an Image with mipmaps).</summary>
    public static byte[] EncodeChain(byte[] data, int w, int h, Func<int, int> levelOffset, int levels)
    {
        var parts = new List<byte[]>();
        for (int l = 0; l < levels; l++) parts.Add(EncodeLevel(data, levelOffset(l), Math.Max(1, w >> l), Math.Max(1, h >> l)));
        var all = new byte[parts.Sum(p => p.Length)];
        int at = 0;
        foreach (var p in parts) { p.CopyTo(all, at); at += p.Length; }
        return all;
    }

    /// <summary>One 4x4 block (<paramref name="px"/>: 16 texels of R, G, B, A) to its 16 bytes. <paramref name="shortcut"/> false always takes the general path (for the tests).</summary>
    internal static void Block(ReadOnlySpan<int> px, Span<byte> dst, bool shortcut = true)
    {
        // one colour all over (most of a road layer: the clear ground between the roads) is the block the general path makes of
        // it, without its search: both alpha ends that alpha, every index 0, both colour ends that colour
        bool flat = shortcut;
        for (int i = 1; i < 16 && flat; i++)
            flat = px[i * 4] == px[0] && px[i * 4 + 1] == px[1] && px[i * 4 + 2] == px[2] && px[i * 4 + 3] == px[3];
        if (flat)
        {
            dst[0] = dst[1] = (byte)px[3];
            dst[2..8].Clear();
            int c = Pack565(px[0], px[1], px[2]);
            dst[8] = dst[10] = (byte)c; dst[9] = dst[11] = (byte)(c >> 8);
            dst[12..16].Clear();
            return;
        }
        General(px, dst);
    }

    private static void General(ReadOnlySpan<int> px, Span<byte> dst)
    {
        // alpha: a0 > a1 selects the 8-level mode
        int amin = 255, amax = 0;
        for (int i = 0; i < 16; i++) { int a = px[i * 4 + 3]; if (a < amin) amin = a; if (a > amax) amax = a; }
        dst[0] = (byte)amax; dst[1] = (byte)amin;
        Span<int> lev = stackalloc int[8];
        lev[0] = amax; lev[1] = amin;
        for (int k = 1; k <= 6; k++) lev[k + 1] = ((7 - k) * amax + k * amin + 3) / 7;
        ulong abits = 0;
        for (int i = 0; i < 16; i++)
        {
            int a = px[i * 4 + 3], best = 0, bd = int.MaxValue;
            for (int k = 0; k < 8; k++) { int d = Math.Abs(a - lev[k]); if (d < bd) { bd = d; best = k; } }
            abits |= (ulong)best << (3 * i);
        }
        for (int k = 0; k < 6; k++) dst[2 + k] = (byte)(abits >> (8 * k));

        // colour: the axis of the largest spread, found by power iteration on the covariance
        double mr = 0, mg = 0, mb = 0;
        for (int i = 0; i < 16; i++) { mr += px[i * 4]; mg += px[i * 4 + 1]; mb += px[i * 4 + 2]; }
        mr /= 16; mg /= 16; mb /= 16;
        double crr = 0, crg = 0, crb = 0, cgg = 0, cgb = 0, cbb = 0;
        for (int i = 0; i < 16; i++)
        {
            double r = px[i * 4] - mr, g = px[i * 4 + 1] - mg, b = px[i * 4 + 2] - mb;
            crr += r * r; crg += r * g; crb += r * b; cgg += g * g; cgb += g * b; cbb += b * b;
        }
        double ax = 0.57735, ay = 0.57735, az = 0.57735;
        for (int it = 0; it < 4; it++)
        {
            double nx = crr * ax + crg * ay + crb * az, ny = crg * ax + cgg * ay + cgb * az, nz = crb * ax + cgb * ay + cbb * az;
            double len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            if (len < 1e-9) break;
            ax = nx / len; ay = ny / len; az = nz / len;
        }
        double tmin = double.MaxValue, tmax = double.MinValue;
        for (int i = 0; i < 16; i++)
        {
            double t = (px[i * 4] - mr) * ax + (px[i * 4 + 1] - mg) * ay + (px[i * 4 + 2] - mb) * az;
            if (t < tmin) tmin = t; if (t > tmax) tmax = t;
        }
        int c0 = Pack565(mr + ax * tmax, mg + ay * tmax, mb + az * tmax);
        int c1 = Pack565(mr + ax * tmin, mg + ay * tmin, mb + az * tmin);
        if (c0 < c1) (c0, c1) = (c1, c0);
        dst[8] = (byte)c0; dst[9] = (byte)(c0 >> 8); dst[10] = (byte)c1; dst[11] = (byte)(c1 >> 8);
        if (c0 == c1) { dst[12] = dst[13] = dst[14] = dst[15] = 0; return; }
        Span<int> pal = stackalloc int[12];
        Unpack565(c0, pal[0..3]); Unpack565(c1, pal[3..6]);
        for (int k = 0; k < 3; k++) { pal[6 + k] = (2 * pal[k] + pal[3 + k] + 1) / 3; pal[9 + k] = (pal[k] + 2 * pal[3 + k] + 1) / 3; }
        uint cbits = 0;
        // index order of the 4-colour mode: 0 = c0, 1 = c1, 2 = 2/3 c0 + 1/3 c1, 3 = 1/3 c0 + 2/3 c1
        for (int i = 0; i < 16; i++)
        {
            int best = 0, bd = int.MaxValue;
            for (int k = 0; k < 4; k++)
            {
                int dr = px[i * 4] - pal[k * 3], dg = px[i * 4 + 1] - pal[k * 3 + 1], db = px[i * 4 + 2] - pal[k * 3 + 2];
                int d = dr * dr + dg * dg + db * db;
                if (d < bd) { bd = d; best = k; }
            }
            cbits |= (uint)best << (2 * i);
        }
        for (int k = 0; k < 4; k++) dst[12 + k] = (byte)(cbits >> (8 * k));
    }

    private static int Pack565(double r, double g, double b)
    {
        int R = (int)Math.Clamp(Math.Round(r * 31 / 255), 0, 31), G = (int)Math.Clamp(Math.Round(g * 63 / 255), 0, 63), B = (int)Math.Clamp(Math.Round(b * 31 / 255), 0, 31);
        return R << 11 | G << 5 | B;
    }

    private static void Unpack565(int c, Span<int> rgb)
    {
        int r = c >> 11 & 31, g = c >> 5 & 63, b = c & 31;
        rgb[0] = r << 3 | r >> 2; rgb[1] = g << 2 | g >> 4; rgb[2] = b << 3 | b >> 2;
    }
}
