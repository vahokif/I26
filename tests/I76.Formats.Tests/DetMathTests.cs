using Xunit;
using Xunit.Abstractions;

namespace I76.Formats.Tests;

/// <summary>
/// <see cref="DetMath"/>: within an ulp or two of the platform's C library, and the same bits everywhere. The second is the
/// point (the sim's StateHash on a Mac equals a PC's only if its sines do), so every function's results over fixed inputs are
/// hashed against the hash recorded on Windows x64; the test passing on macOS/Arm and Linux is the cross-platform proof.
/// </summary>
public class DetMathTests(ITestOutputHelper log)
{
    private const int N = 20000;

    private static ulong Fnv(ulong h, ulong x)
    {
        for (int i = 0; i < 8; i++) { h ^= (x >> (8 * i)) & 0xff; h *= 1099511628211UL; }
        return h;
    }

    private static long Ulps(double a, double b)
    {
        if (double.IsNaN(a) && double.IsNaN(b) || a == b) return 0;
        long x = BitConverter.DoubleToInt64Bits(a), y = BitConverter.DoubleToInt64Bits(b);
        if (x < 0) x = long.MinValue - x;
        if (y < 0) y = long.MinValue - y;
        return Math.Abs(x - y);
    }

    /// <summary>Fixed inputs: a linear congruential sequence (no System.Random, whose algorithm is the runtime's to change).</summary>
    private static double[] Inputs(double lo, double hi, ulong seed, bool logScale = false)
    {
        var a = new double[N];
        ulong s = seed;
        for (int i = 0; i < N; i++)
        {
            s = s * 6364136223846793005UL + 1442695040888963407UL;
            double u = (s >> 11) * (1.0 / 9007199254740992.0);
            a[i] = logScale ? lo * DetMath.Exp(u * DetMath.Log(hi / lo)) : lo + u * (hi - lo);
        }
        return a;
    }

    private static readonly (string Name, Func<double, double> Ours, Func<double, double> Libm, double Lo, double Hi, bool Log, int MaxUlps)[] Unary =
    [
        ("sin", DetMath.Sin, Math.Sin, -2000, 2000, false, 1),
        ("cos", DetMath.Cos, Math.Cos, -2000, 2000, false, 1),
        ("sin big", DetMath.Sin, Math.Sin, -1e9, 1e9, false, 1),
        ("tan", DetMath.Tan, Math.Tan, -1.5, 1.5, false, 3),   // 2 against Windows', 3 against Apple's
        ("atan", DetMath.Atan, Math.Atan, -1000, 1000, false, 1),
        ("asin", DetMath.Asin, Math.Asin, -1, 1, false, 2),
        ("acos", DetMath.Acos, Math.Acos, -1, 1, false, 2),
        ("exp", DetMath.Exp, Math.Exp, -700, 700, false, 1),
        ("log", DetMath.Log, Math.Log, 1e-300, 1e300, true, 1),
        ("log10", DetMath.Log10, Math.Log10, 1e-300, 1e300, true, 1),
    ];

    [Fact]
    public void DoublesAreWithinAnUlpOrTwoOfTheCLibrary()
    {
        foreach (var (name, ours, libm, lo, hi, lg, maxUlps) in Unary)
        {
            long max = 0;
            foreach (var x in Inputs(lo, hi, 76, lg)) max = Math.Max(max, Ulps(ours(x), libm(x)));
            log.WriteLine($"{name}: {max} ulp");
            Assert.True(max <= maxUlps, $"{name}: {max} ulp from the C library");
        }
        var ys = Inputs(-1000, 1000, 5); var xs = Inputs(-1000, 1000, 6);
        long m2 = 0;
        for (int i = 0; i < N; i++) m2 = Math.Max(m2, Ulps(DetMath.Atan2(ys[i], xs[i]), Math.Atan2(ys[i], xs[i])));
        Assert.True(m2 <= 1, $"atan2: {m2} ulp");
        var bs = Inputs(1e-3, 1e3, 7, true); var es = Inputs(-8, 8, 8);
        long mp = 0;
        for (int i = 0; i < N; i++) mp = Math.Max(mp, Ulps(DetMath.Pow(bs[i], es[i]), Math.Pow(bs[i], es[i])));
        Assert.True(mp <= 1, $"pow: {mp} ulp");
    }

    [Fact]
    public void FloatsAreTheCLibrarysToAnUlp()
    {
        var a = Inputs(-2000, 2000, 9);
        int differ = 0;
        foreach (var d in a)
        {
            float x = (float)d;
            foreach (var (o, l) in new[] { (DetMath.Sin(x), MathF.Sin(x)), (DetMath.Cos(x), MathF.Cos(x)), (DetMath.Atan2(x, 3f), MathF.Atan2(x, 3f)),
                (DetMath.Exp(x / 40), MathF.Exp(x / 40)), (DetMath.Pow(MathF.Abs(x) + 0.5f, 1.7f), MathF.Pow(MathF.Abs(x) + 0.5f, 1.7f)), (DetMath.Asin(x / 2000), MathF.Asin(x / 2000)) })
            {
                Assert.True(Ulps(o, l) <= 1 << 29, $"{x}: {o} against {l}");   // a float ulp is 2^29 double ulps
                if (o != l) differ++;
                Assert.True(Math.Abs(o - l) <= Math.Max(Math.Abs(l), 1e-30f) * 1.2e-7f, $"{x}: {o} against {l}");
            }
        }
        log.WriteLine($"{differ} of {6 * N} floats differ from the C library's (each by one ulp)");
    }

    [Fact]
    public void TheCLibrarysSpecialValuesAreKept()
    {
        double[] sp = [0.0, -0.0, 1, -1, 0.5, -0.5, 2, 3, double.PositiveInfinity, double.NegativeInfinity, double.NaN, double.Epsilon, 1e-310, double.MaxValue, -double.MaxValue, Math.PI, 710, -746];
        foreach (var a in sp)
        {
            void Same(string n, double o, double c) =>
                Assert.True(double.IsNaN(o) && double.IsNaN(c) || BitConverter.DoubleToInt64Bits(o) == BitConverter.DoubleToInt64Bits(c) || (!(o == 0 && c == 0) && Ulps(o, c) <= 2),   // an ulp or two; a zero of the other sign is not
                    $"{n}({a}): {o:R}, the C library {c:R}");
            Same("sin", DetMath.Sin(a), Math.Sin(a)); Same("cos", DetMath.Cos(a), Math.Cos(a)); Same("atan", DetMath.Atan(a), Math.Atan(a));
            Same("asin", DetMath.Asin(a), Math.Asin(a)); Same("acos", DetMath.Acos(a), Math.Acos(a)); Same("exp", DetMath.Exp(a), Math.Exp(a));
            Same("log", DetMath.Log(a), Math.Log(a));
            foreach (var b in sp)
            {
                Same($"atan2(_, {b})", DetMath.Atan2(a, b), Math.Atan2(a, b));
                if (!(Math.Abs(b) > 700 && Math.Abs(a) is > 1 and < 2)) Same($"pow(_, {b})", DetMath.Pow(a, b), Math.Pow(a, b));   // pow(π/2, −746): 3 ulp
            }
        }
    }

    /// <summary>
    /// The bits themselves, hashed (recorded on Windows x64, .NET 8). Equal on every platform, or DetMath has picked up something
    /// of the platform's (a C library call, a fused multiply-add, an out-of-range conversion).
    /// </summary>
    [Fact]
    public void TheBitsAreTheRecordedOnes()
    {
        ulong h = 14695981039346656037UL;
        foreach (var (name, ours, _, lo, hi, lg, _) in Unary)
            foreach (var x in Inputs(lo, hi, 11, lg)) h = Fnv(h, (ulong)BitConverter.DoubleToInt64Bits(ours(x)));
        var ys = Inputs(-1000, 1000, 12); var xs = Inputs(-1000, 1000, 13); var bs = Inputs(1e-3, 1e3, 14, true); var es = Inputs(-8, 8, 15);
        for (int i = 0; i < N; i++)
        {
            h = Fnv(h, (ulong)BitConverter.DoubleToInt64Bits(DetMath.Atan2(ys[i], xs[i])));
            h = Fnv(h, (ulong)BitConverter.DoubleToInt64Bits(DetMath.Pow(bs[i], es[i])));
            float f = (float)xs[i];
            var (s, c) = DetMath.SinCos(f);
            h = Fnv(h, BitConverter.SingleToUInt32Bits(s) | (ulong)BitConverter.SingleToUInt32Bits(c) << 32);
            h = Fnv(h, BitConverter.SingleToUInt32Bits(DetMath.Atan2(f, (float)ys[i])) | (ulong)BitConverter.SingleToUInt32Bits(DetMath.Asin(f / 1000)) << 32);
            h = Fnv(h, BitConverter.SingleToUInt32Bits(DetMath.Pow((float)bs[i], (float)es[i])) | (ulong)BitConverter.SingleToUInt32Bits(DetMath.Exp(f / 20)) << 32);
            h = Fnv(h, BitConverter.SingleToUInt32Bits(DetMath.Tan(f)) | (ulong)BitConverter.SingleToUInt32Bits(DetMath.Log10((float)bs[i])) << 32);
        }
        log.WriteLine($"{h:x16}");
        Assert.Equal(0x5666dd5827e4454fUL, h);
    }
}
