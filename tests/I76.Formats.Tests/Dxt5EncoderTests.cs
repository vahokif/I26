using I76.Formats.Imaging;
using Xunit;

namespace I76.Formats.Tests;

public class Dxt5EncoderTests
{
    private static (int R, int G, int B, int A)[] Decode(byte[] d, int w, int h)
    {
        var o = new (int, int, int, int)[w * h];
        int bw = (w + 3) / 4;
        for (int by = 0; by < (h + 3) / 4; by++)
            for (int bx = 0; bx < bw; bx++)
            {
                int p = (by * bw + bx) * 16;
                int a0 = d[p], a1 = d[p + 1];
                var al = new int[8]; al[0] = a0; al[1] = a1;
                if (a0 > a1) for (int k = 1; k <= 6; k++) al[k + 1] = ((7 - k) * a0 + k * a1) / 7;
                else { for (int k = 1; k <= 4; k++) al[k + 1] = ((5 - k) * a0 + k * a1) / 5; al[6] = 0; al[7] = 255; }
                ulong ab = 0; for (int k = 0; k < 6; k++) ab |= (ulong)d[p + 2 + k] << (8 * k);
                int c0 = d[p + 8] | d[p + 9] << 8, c1 = d[p + 10] | d[p + 11] << 8;
                int[] U(int c) { int r = c >> 11 & 31, g = c >> 5 & 63, b = c & 31; return [r << 3 | r >> 2, g << 2 | g >> 4, b << 3 | b >> 2]; }
                var p0 = U(c0); var p1 = U(c1);
                var pal = new int[4][]; pal[0] = p0; pal[1] = p1;
                pal[2] = [(2 * p0[0] + p1[0] + 1) / 3, (2 * p0[1] + p1[1] + 1) / 3, (2 * p0[2] + p1[2] + 1) / 3];
                pal[3] = [(p0[0] + 2 * p1[0] + 1) / 3, (p0[1] + 2 * p1[1] + 1) / 3, (p0[2] + 2 * p1[2] + 1) / 3];
                uint cb = BitConverter.ToUInt32(d, p + 12);
                for (int i = 0; i < 16; i++)
                {
                    int x = bx * 4 + (i & 3), y = by * 4 + (i >> 2);
                    if (x >= w || y >= h) continue;
                    var c = pal[cb >> (2 * i) & 3];
                    o[y * w + x] = (c[0], c[1], c[2], al[(int)(ab >> (3 * i) & 7)]);
                }
            }
        return o;
    }

    [Fact]
    public void AGradientWithNoiseComesBackCloseAndASolidBlockExactly()
    {
        const int n = 64;
        var rnd = new Random(7);
        var img = new byte[n * n * 4];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                int i = (y * n + x) * 4;
                img[i] = (byte)(x * 4 + rnd.Next(8)); img[i + 1] = (byte)(y * 4 + rnd.Next(8)); img[i + 2] = (byte)(100 + (x + y) + rnd.Next(8));
                img[i + 3] = (byte)(x < 32 ? 255 : y * 4);
            }
        var dec = Decode(Dxt5Encoder.EncodeLevel(img, 0, n, n), n, n);
        double se = 0;
        for (int i = 0; i < n * n; i++)
        {
            var (r, g, b, a) = dec[i];
            se += Math.Pow(r - img[i * 4], 2) + Math.Pow(g - img[i * 4 + 1], 2) + Math.Pow(b - img[i * 4 + 2], 2) + Math.Pow(a - img[i * 4 + 3], 2);
        }
        double psnr = 10 * Math.Log10(255.0 * 255 / (se / (n * n * 4)));
        Assert.True(psnr > 34, $"PSNR {psnr:F1} dB");

        var flat = new byte[4 * 4 * 4];
        for (int i = 0; i < 16; i++) { flat[i * 4] = 200; flat[i * 4 + 1] = 100; flat[i * 4 + 2] = 40; flat[i * 4 + 3] = 255; }
        var (fr, fg, fb, fa) = Decode(Dxt5Encoder.EncodeLevel(flat, 0, 4, 4), 4, 4)[5];
        Assert.InRange(fr, 198, 202); Assert.InRange(fg, 98, 102); Assert.InRange(fb, 38, 42); Assert.Equal(255, fa);
    }

    [Fact]
    public void TheChainIsTheSizeOfAnImageWithMipmaps()
    {
        var img = new byte[8 * 8 * 4];
        int Off(int l) => l switch { 0 => 0, 1 => 8 * 8 * 4, 2 => 8 * 8 * 4 + 16 * 4, _ => 8 * 8 * 4 + 16 * 4 + 4 * 4 };
        var big = new byte[Off(3) + 4];
        // 8x8, 4x4, 2x2, 1x1: 4 + 1 + 1 + 1 blocks
        Assert.Equal(7 * 16, Dxt5Encoder.EncodeChain(big, 8, 8, Off, 4).Length);
    }

    [Fact]
    public void ABlockOfOneColourTakesTheShortcutToTheBytesTheGeneralPathMakes()
    {
        var rnd = new Random(26);
        var px = new int[64];
        for (int t = 0; t < 2000; t++)
        {
            // the clear ground's (0, 0, 0, 0) and the ends of every channel among them
            int r = t == 0 ? 0 : t % 7 == 0 ? 255 : rnd.Next(256), g = t == 0 ? 0 : t % 5 == 0 ? 255 : rnd.Next(256), b = t == 0 ? 0 : rnd.Next(256), a = t < 2 ? 0 : t % 3 == 0 ? 255 : rnd.Next(256);
            for (int i = 0; i < 16; i++) { px[i * 4] = r; px[i * 4 + 1] = g; px[i * 4 + 2] = b; px[i * 4 + 3] = a; }
            var quick = new byte[16]; var general = new byte[16];
            Dxt5Encoder.Block(px, quick);
            Dxt5Encoder.Block(px, general, shortcut: false);
            Assert.True(quick.AsSpan().SequenceEqual(general), $"({r}, {g}, {b}, {a}): {Convert.ToHexString(quick)} against {Convert.ToHexString(general)}");
        }
    }
}
