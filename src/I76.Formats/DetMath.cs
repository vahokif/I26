using System.Numerics;

namespace I76.Formats;

/// <summary>
/// Sine, cosine, tangent, arctangent, arcsine, arccosine, exponential, logarithm and power from IEEE additions, multiplications,
/// divisions and square roots alone, so they give the same bits on every machine: the simulation's (every call that feeds its
/// state; docs/architecture.md, "The same game on every machine") and the ground past a map's edge. The runtime's Math/MathF
/// versions come from the platform's C library (Windows' UCRT, Apple's libm, glibc), whose last bit differs between them: fine
/// for drawing, not for ground two machines must agree on to the bit; a mission's state on a Mac parted from a PC's within a few
/// ticks. +, -, *, /, Sqrt, Floor, Round and ScaleB are exact IEEE operations
/// (or managed code) and agree everywhere; the JIT does not contract a*b+c into a fused multiply-add, and nothing here asks for one.
/// <para>
/// The double kernels are ported from FreeBSD's msun (k_sin.c, k_cos.c, e_rem_pio2.c, s_atan.c, e_exp.c) and musl's log.c:
/// "Copyright (C) 1993 by Sun Microsystems, Inc. All rights reserved. Developed at SunPro, a Sun Microsystems, Inc. business.
/// Permission to use, copy, modify, and distribute this software is freely granted, provided that this notice is preserved."
/// Accurate to about 1 ulp in double (tan, asin, acos and pow a few ulp: they are built from the others); the float versions
/// evaluate in double and round once, so they are almost always the correctly rounded float.
/// </para>
/// Arguments of sine, cosine and tangent are reduced with a three-part π/2 (msun's medium case), accurate for |x| below about
/// 2^20·π/2; larger ones still give the same bits everywhere, only less accurate.
/// </summary>
public static class DetMath
{
    public const double Pi = 3.14159265358979311600e+00;
    private const double PiLo = 1.2246467991473531772e-16;
    private const double HalfPi = 1.57079632679489655800e+00, HalfPiLo = 6.12323399573676603587e-17;

    // ---- sine and cosine (k_sin.c, k_cos.c) ----
    private const double S1 = -1.66666666666666324348e-01, S2 = 8.33333333332248946124e-03, S3 = -1.98412698298579493134e-04,
        S4 = 2.75573137070700676789e-06, S5 = -2.50507602534068634195e-08, S6 = 1.58969099521155010221e-10;
    private const double C1 = 4.16666666666666019037e-02, C2 = -1.38888888888741095749e-03, C3 = 2.48015872894767294178e-05,
        C4 = -2.75573143513906633035e-07, C5 = 2.08757232129817482790e-09, C6 = -1.13596475577881948265e-11;

    /// <summary>sin(x + y) for |x| up to π/4, y the tail of x (iy 0: y is 0).</summary>
    private static double KSin(double x, double y, bool tail)
    {
        double z = x * x, w = z * z;
        double r = S2 + z * (S3 + z * S4) + z * w * (S5 + z * S6);
        double v = z * x;
        if (!tail) return x + v * (S1 + z * r);
        return x - ((z * (0.5 * y - v * r) - y) - v * S1);
    }

    /// <summary>cos(x + y) for |x| up to π/4.</summary>
    private static double KCos(double x, double y)
    {
        double z = x * x, w = z * z;
        double r = z * (C1 + z * (C2 + z * C3)) + w * w * (C4 + z * (C5 + z * C6));
        double hz = 0.5 * z;
        w = 1.0 - hz;
        return w + (((1.0 - w) - hz) + (z * r - x * y));
    }

    // e_rem_pio2.c's medium case: π/2 in parts of 33 + 33 + 33 bits and their tails
    private const double InvPio2 = 6.36619772367581382433e-01;
    private const double Pio2_1 = 1.57079632673412561417e+00, Pio2_1t = 6.07710050650619224932e-11;
    private const double Pio2_2 = 6.07710050630396597660e-11, Pio2_2t = 2.02226624879595063154e-21;
    private const double Pio2_3 = 2.02226624871116645580e-21, Pio2_3t = 8.47842766036889956997e-32;

    private static int Exponent(double x) => (int)((BitConverter.DoubleToInt64Bits(x) >> 52) & 0x7ff);

    /// <summary>x − n·π/2 as y0 + y1 with |y0| ≤ about π/4; returns n mod 4.</summary>
    private static int RemPio2(double x, out double y0, out double y1)
    {
        if (Math.Abs(x) >= 1647099.0) return RemPio2Large(x, out y0, out y1);   // 2^20·π/2: past the three-part π/2's reach
        double fn = Math.Round(x * InvPio2);
        double r = x - fn * Pio2_1, w = fn * Pio2_1t;
        int j = Exponent(x);
        y0 = r - w;
        if (j - Exponent(y0) > 16)
        {
            double t = r;
            w = fn * Pio2_2;
            r = t - w;
            w = fn * Pio2_2t - ((t - r) - w);
            y0 = r - w;
            if (j - Exponent(y0) > 49)
            {
                t = r;
                w = fn * Pio2_3;
                r = t - w;
                w = fn * Pio2_3t - ((t - r) - w);
                y0 = r - w;
            }
        }
        y1 = (r - y0) - w;
        return (int)fn & 3;   // |fn| < 2^21 here: the conversion is exact on every processor
    }

    /// <summary>
    /// The reduction for |x| ≥ 2^20·π/2 (never an angle of the game's; a phase that ran for days): x·2/π in integers against 1200 bits of
    /// 2/π, as msun's __kernel_rem_pio2 does with its table; the bits are worked out once with System.Numerics.BigInteger (Machin's formula).
    /// </summary>
    private static int RemPio2Large(double x, out double y0, out double y1)
    {
        const int P = 1200;
        var twoOverPi = TwoOverPiBits ??= TwoOverPi(P);
        long bits = BitConverter.DoubleToInt64Bits(x);
        int e = (int)((bits >> 52) & 0x7ff) - 1075;   // |x| = m·2^e, m a 53-bit integer
        var m = new System.Numerics.BigInteger((bits & 0x000fffffffffffffL) | 0x0010000000000000L);
        // |x|·2/π = m·T·2^(e−P): its integer part's low bits and 128 bits of its fraction
        var prod = m * twoOverPi;
        int shift = P - e;   // e ≥ −32 here, so the shift leaves ≥ 1100 fraction bits
        var whole = prod >> shift;
        int n = (int)(whole & 3);
        var frac = (prod - (whole << shift)) >> (shift - 128);   // the fraction as a 128-bit integer
        if (frac >= System.Numerics.BigInteger.One << 127) { frac -= System.Numerics.BigInteger.One << 128; n = (n + 1) & 3; }   // into −1/2..1/2
        // the fraction in two doubles: its top 53 bits (exact) and the rest
        var top = frac >> 75;
        double a = Math.ScaleB((double)top, -53), b = Math.ScaleB((double)(frac - (top << 75)), -128);
        double f = a + b, fTail = (a - f) + b;
        // (f + fTail)·(HalfPi + HalfPiLo), the product of the heads split exactly (Dekker)
        double ph = f * HalfPi;
        double pt = TwoProdErr(f, HalfPi, ph) + (f * HalfPiLo + fTail * HalfPi);
        y0 = ph + pt;
        y1 = pt - (y0 - ph);
        if (x < 0) { y0 = -y0; y1 = -y1; n = (4 - n) & 3; }
        return n;
    }

    private static System.Numerics.BigInteger? TwoOverPiBits;

    /// <summary>floor(2/π · 2^bits), from Machin's π/4 = 4·atan(1/5) − atan(1/239) in fixed point.</summary>
    private static System.Numerics.BigInteger TwoOverPi(int bits)
    {
        int q = bits + 64;
        var one = System.Numerics.BigInteger.One << q;
        System.Numerics.BigInteger Acot(int k)
        {
            System.Numerics.BigInteger sum = 0, term = one / k, k2 = k * k;
            for (int i = 1; term != 0; i += 2, term /= k2) sum += (i / 2 % 2 == 0 ? term : -term) / i;
            return sum;
        }
        var pi = 4 * (4 * Acot(5) - Acot(239));   // π·2^q
        return (System.Numerics.BigInteger.One << (bits + q + 1)) / pi;   // 2/π · 2^bits
    }

    /// <summary>The rounding error of a·b (p = a·b rounded), exactly, by Dekker's split (no fused multiply-add).</summary>
    private static double TwoProdErr(double a, double b, double p)
    {
        const double Split = 134217729.0;   // 2^27 + 1
        double ca = Split * a, ah = ca - (ca - a), al = a - ah;
        double cb = Split * b, bh = cb - (cb - b), bl = b - bh;
        return ((ah * bh - p) + ah * bl + al * bh) + al * bl;
    }

    public static double Sin(double x)
    {
        double ax = Math.Abs(x);
        if (ax <= 7.85398163397448278999e-01)
            return ax < 7.450580596923828125e-9 ? x : KSin(x, 0, false);   // |x| < 2^-27: sin x = x
        if (!double.IsFinite(x)) return double.NaN;
        return RemPio2(x, out double y0, out double y1) switch
        {
            0 => KSin(y0, y1, true),
            1 => KCos(y0, y1),
            2 => -KSin(y0, y1, true),
            _ => -KCos(y0, y1),
        };
    }

    public static double Cos(double x)
    {
        double ax = Math.Abs(x);
        if (ax <= 7.85398163397448278999e-01)
            return ax < 7.450580596923828125e-9 ? 1.0 : KCos(x, 0);
        if (!double.IsFinite(x)) return double.NaN;
        return RemPio2(x, out double y0, out double y1) switch
        {
            0 => KCos(y0, y1),
            1 => -KSin(y0, y1, true),
            2 => -KCos(y0, y1),
            _ => KSin(y0, y1, true),
        };
    }

    /// <summary>Sine and cosine from one argument reduction.</summary>
    public static (double Sin, double Cos) SinCos(double x)
    {
        double ax = Math.Abs(x);
        if (ax <= 7.85398163397448278999e-01)
            return ax < 7.450580596923828125e-9 ? (x, 1.0) : (KSin(x, 0, false), KCos(x, 0));
        if (!double.IsFinite(x)) return (double.NaN, double.NaN);
        int n = RemPio2(x, out double y0, out double y1);
        double s = KSin(y0, y1, true), c = KCos(y0, y1);
        return n switch { 0 => (s, c), 1 => (c, -s), 2 => (-s, -c), _ => (-c, s) };
    }

    /// <summary>sin/cos from one reduction: within about 2 ulp (msun's k_tan is closer; this is what the sim needs).</summary>
    public static double Tan(double x)
    {
        var (s, c) = SinCos(x);
        return s / c;
    }

    // ---- arctangent (s_atan.c) ----
    private static readonly double[] AtanHi = [4.63647609000806093515e-01, 7.85398163397448278999e-01, 9.82793723247329054082e-01, 1.57079632679489655800e+00];
    private static readonly double[] AtanLo = [2.26987774529616870924e-17, 3.06161699786838301793e-17, 1.39033110312309984516e-17, 6.12323399573676603587e-17];
    private const double AT0 = 3.33333333333329318027e-01, AT1 = -1.99999999998764832476e-01, AT2 = 1.42857142725034663711e-01,
        AT3 = -1.11111104054623557880e-01, AT4 = 9.09088713343650656196e-02, AT5 = -7.69187620504482999495e-02,
        AT6 = 6.66107313738753120669e-02, AT7 = -5.83357013379057348645e-02, AT8 = 4.97687799461593236017e-02,
        AT9 = -3.65315727442169155270e-02, AT10 = 1.62858201153657823623e-02;

    public static double Atan(double x)
    {
        if (double.IsNaN(x)) return x;
        double ax = Math.Abs(x);
        bool neg = x < 0;
        if (ax >= 7.378697629483820646e19)   // 2^66
            return neg ? -AtanHi[3] - AtanLo[3] : AtanHi[3] + AtanLo[3];
        int id;
        if (ax < 0.4375)
        {
            if (ax < 7.450580596923828125e-9) return x;   // 2^-27
            id = -1;
        }
        else
        {
            x = ax;
            if (ax < 1.1875)
            {
                if (ax < 0.6875) { id = 0; x = (2.0 * x - 1.0) / (2.0 + x); }
                else { id = 1; x = (x - 1.0) / (x + 1.0); }
            }
            else
            {
                if (ax < 2.4375) { id = 2; x = (x - 1.5) / (1.0 + 1.5 * x); }
                else { id = 3; x = -1.0 / x; }
            }
        }
        double z = x * x, w = z * z;
        double s1 = z * (AT0 + w * (AT2 + w * (AT4 + w * (AT6 + w * (AT8 + w * AT10)))));
        double s2 = w * (AT1 + w * (AT3 + w * (AT5 + w * (AT7 + w * AT9))));
        if (id < 0) return x - x * (s1 + s2);
        z = AtanHi[id] - ((x * (s1 + s2) - AtanLo[id]) - x);
        return neg ? -z : z;
    }

    /// <summary>The angle of (x, y), −π..π, with the C library's signs of zero and infinities (e_atan2.c).</summary>
    public static double Atan2(double y, double x)
    {
        if (double.IsNaN(x) || double.IsNaN(y)) return x + y;
        if (x == 1.0) return Atan(y);
        int m = (double.IsNegative(y) ? 1 : 0) | (double.IsNegative(x) ? 2 : 0);
        if (y == 0)
            return m switch { 0 or 1 => y, 2 => Pi + PiLo, _ => -Pi - PiLo };
        if (x == 0) return m % 2 == 1 ? -HalfPi - HalfPiLo : HalfPi + HalfPiLo;
        if (double.IsInfinity(x))
        {
            if (double.IsInfinity(y))
                return m switch { 0 => Pi / 4, 1 => -Pi / 4, 2 => 3 * Pi / 4, _ => -3 * Pi / 4 };
            return m switch { 0 => 0.0, 1 => -0.0, 2 => Pi + PiLo, _ => -Pi - PiLo };
        }
        if (double.IsInfinity(y)) return m % 2 == 1 ? -HalfPi - HalfPiLo : HalfPi + HalfPiLo;
        int k = Exponent(y) - Exponent(x);
        double z;
        if (k > 60) { z = HalfPi + 0.5 * PiLo; m &= 1; }
        else if (x < 0 && k < -60) z = 0.0;
        else z = Atan(Math.Abs(y / x));
        return m switch
        {
            0 => z,
            1 => -z,
            2 => Pi - (z - PiLo),
            _ => (z - PiLo) - Pi,
        };
    }

    /// <summary>atan2(x, √((1−x)(1+x))): within a few ulp; NaN outside −1..1.</summary>
    public static double Asin(double x)
    {
        if (!(Math.Abs(x) <= 1)) return double.NaN;
        return Atan2(x, Math.Sqrt((1 - x) * (1 + x)));
    }

    /// <summary>atan2(√((1−x)(1+x)), x): within a few ulp; NaN outside −1..1.</summary>
    public static double Acos(double x)
    {
        if (!(Math.Abs(x) <= 1)) return double.NaN;
        return Atan2(Math.Sqrt((1 - x) * (1 + x)), x);
    }

    // ---- exponential (e_exp.c) ----
    private const double Ln2Hi = 6.93147180369123816490e-01, Ln2Lo = 1.90821492927058770002e-10, InvLn2 = 1.44269504088896338700e+00;
    private const double P1 = 1.66666666666666019037e-01, P2 = -2.77777777770155933842e-03, P3 = 6.61375632143793436117e-05,
        P4 = -1.65339022054652515390e-06, P5 = 4.13813679705723846039e-08;

    public static double Exp(double x)
    {
        if (double.IsNaN(x)) return x;
        if (x > 7.09782712893383973096e+02) return double.PositiveInfinity;
        if (x < -7.45133219101941108420e+02) return 0.0;
        double ax = Math.Abs(x);
        if (ax < 3.725290298461914e-9) return 1.0 + x;   // 2^-28
        double hi, lo, k;
        if (ax > 3.46573590279972643113e-01)   // ln2 / 2
        {
            k = Math.Round(x * InvLn2);
            hi = x - k * Ln2Hi;   // exact: Ln2Hi has 32 trailing zero bits and |k| < 1100
            lo = k * Ln2Lo;
            x = hi - lo;
        }
        else { k = 0; hi = lo = 0; }
        double t = x * x;
        double c = x - t * (P1 + t * (P2 + t * (P3 + t * (P4 + t * P5))));
        if (k == 0) return 1.0 - ((x * c) / (c - 2.0) - x);
        double y = 1.0 - ((lo - (x * c) / (2.0 - c)) - hi);
        return Math.ScaleB(y, (int)k);
    }

    // ---- logarithm (musl log.c, from msun e_log.c) ----
    private const double Lg1 = 6.666666666666735130e-01, Lg2 = 3.999999999940941908e-01, Lg3 = 2.857142874366239149e-01,
        Lg4 = 2.222219843214978396e-01, Lg5 = 1.818357216161805012e-01, Lg6 = 1.531383769920937332e-01, Lg7 = 1.479819860511658591e-01;

    public static double Log(double x)
    {
        if (double.IsNaN(x)) return x;
        if (x < 0) return double.NaN;
        if (x == 0) return double.NegativeInfinity;
        if (double.IsPositiveInfinity(x)) return x;
        int k = 0;
        long bits = BitConverter.DoubleToInt64Bits(x);
        if (bits < 0x0010000000000000L)   // subnormal: scale up by 2^54
        {
            k -= 54;
            bits = BitConverter.DoubleToInt64Bits(x * 18014398509481984.0);
        }
        uint hx = (uint)(bits >> 32);
        // reduce x into [√2/2, √2)
        hx += 0x3ff00000 - 0x3fe6a09e;
        k += (int)(hx >> 20) - 0x3ff;
        hx = (hx & 0x000fffff) + 0x3fe6a09e;
        double m = BitConverter.Int64BitsToDouble(((long)hx << 32) | (bits & 0xffffffffL));
        double f = m - 1.0;
        double hfsq = 0.5 * f * f;
        double s = f / (2.0 + f), z = s * s, w = z * z;
        double t1 = w * (Lg2 + w * (Lg4 + w * Lg6));
        double t2 = z * (Lg1 + w * (Lg3 + w * (Lg5 + w * Lg7)));
        double r = t2 + t1, dk = k;
        return s * (hfsq + r) + dk * Ln2Lo - hfsq + f + dk * Ln2Hi;
    }

    private const double InvLn10 = 4.34294481903251816668e-01;

    /// <summary>log(x)/ln 10: within about 2 ulp in double.</summary>
    public static double Log10(double x) => Log(x) * InvLn10;

    /// <summary>a + b as s + err exactly (Knuth's two-sum).</summary>
    private static double TwoSum(double a, double b, out double err)
    {
        double s = a + b, bb = s - a;
        err = (a - (s - bb)) + (b - bb);
        return s;
    }

    /// <summary>ln x for a positive finite x as hi + lo, good to about 2^-62 relative: log.c's reduction, its series term s·R on the double-double 2s.</summary>
    private static double LogDD(double x, out double lo)
    {
        int k = 0;
        long bits = BitConverter.DoubleToInt64Bits(x);
        if (bits < 0x0010000000000000L) { k -= 54; bits = BitConverter.DoubleToInt64Bits(x * 18014398509481984.0); }
        uint hx = (uint)(bits >> 32);
        hx += 0x3ff00000 - 0x3fe6a09e;
        k += (int)(hx >> 20) - 0x3ff;
        hx = (hx & 0x000fffff) + 0x3fe6a09e;
        double m = BitConverter.Int64BitsToDouble(((long)hx << 32) | (bits & 0xffffffffL));
        double f = m - 1.0;   // exact
        double dh = TwoSum(m, 1.0, out double dl);
        double sh = f / dh;
        double p = sh * dh;
        double sl = (((f - p) - TwoProdErr(sh, dh, p)) - sh * dl) / dh;
        double z = sh * sh, w = z * z;
        double r = z * (Lg1 + w * (Lg3 + w * (Lg5 + w * Lg7))) + w * (Lg2 + w * (Lg4 + w * Lg6));
        // ln m = 2s + s·R; plus k·ln 2
        double dk = k;
        double hi = TwoSum(dk * Ln2Hi, 2 * sh, out double e);
        lo = e + (2 * sl + sh * r + dk * Ln2Lo);
        double h = hi + lo;
        lo -= h - hi;
        return h;
    }

    /// <summary>e^(hi + lo), |lo| tiny against hi: e_exp.c with the low part carried into its reduced argument's tail.</summary>
    private static double ExpDD(double hi, double lo)
    {
        double x = hi + lo;
        if (x > 7.09782712893383973096e+02) return double.PositiveInfinity;
        if (x < -7.45133219101941108420e+02) return 0.0;
        double k = Math.Round(hi * InvLn2);
        double rh = hi - k * Ln2Hi, rl = k * Ln2Lo - lo;
        x = rh - rl;
        double t = x * x;
        double c = x - t * (P1 + t * (P2 + t * (P3 + t * (P4 + t * P5))));
        double y = 1.0 - ((rl - (x * c) / (2.0 - c)) - rh);
        return k == 0 ? y : Math.ScaleB(y, (int)k);
    }

    /// <summary>
    /// x to the y with the C library's special cases: e^(y·ln x) with ln x and the product in double-double, so within 1 ulp for
    /// |y·ln x| up to about 50 and a few ulp toward the overflow; y = 2 is x·x and y = ½ is √x (correctly rounded, as the C library's).
    /// </summary>
    public static double Pow(double x, double y) => PowCore(x, y, true);

    private static double PowCore(double x, double y, bool doubleDouble)
    {
        if (y == 0 || x == 1) return 1.0;
        if (double.IsNaN(x) || double.IsNaN(y)) return double.NaN;
        if (y == 1) return x;
        if (y == 2) return x * x;
        if (y == 0.5 && x >= 0 && !double.IsPositiveInfinity(x)) return Math.Sqrt(x) + 0.0;   // pow(−0, ½) is +0
        bool yInt = Math.Floor(y) == y && double.IsFinite(y);
        bool yOdd = yInt && Math.Abs(y) < 9007199254740992.0 && Math.Floor(y * 0.5) * 2 != y;
        if (double.IsInfinity(y))
        {
            double ax = Math.Abs(x);
            if (ax == 1) return 1.0;
            return (ax > 1) == (y > 0) ? double.PositiveInfinity : 0.0;
        }
        if (x == 0)
        {
            if (y > 0) return yOdd ? x : 0.0;
            return yOdd && double.IsNegative(x) ? double.NegativeInfinity : double.PositiveInfinity;
        }
        if (double.IsInfinity(x))
        {
            if (x > 0) return y > 0 ? double.PositiveInfinity : 0.0;
            if (y > 0) return yOdd ? double.NegativeInfinity : double.PositiveInfinity;
            return yOdd ? -0.0 : 0.0;
        }
        if (x < 0 && !yInt) return double.NaN;
        double ax2 = Math.Abs(x), res;
        if (ax2 == 1) res = 1.0;   // −1 to an integer
        else if (doubleDouble)
        {
            double lh = LogDD(ax2, out double ll);
            double th = y * lh;
            res = Math.Abs(th) > 1000 ? (th > 0 ? double.PositiveInfinity : 0.0)   // past the range (and Dekker's split would overflow)
                : ExpDD(th, TwoProdErr(y, lh, th) + y * ll);
        }
        else res = Exp(y * Log(ax2));
        return x < 0 && yOdd ? -res : res;
    }

    /// <summary>
    /// e to the <paramref name="x"/> to float precision, for weights in a filter: as <see cref="Exp(double)"/> but a shorter series of
    /// multiplications (relative error under 1e-8 before the rounding to float), so about as quick as MathF.Exp.
    /// </summary>
    public static float ExpF(float x)
    {
        if (x > 88.8f) return float.PositiveInfinity;
        if (x < -104) return 0;
        double k = Math.Round(x * 1.4426950408889634);
        double r = x - k * Ln2Hi - k * Ln2Lo;   // |r| <= 0.35
        // Taylor to r^8 (the next term is under 1e-9)
        double sum = 1 + r * (1 + r * (1.0 / 2 + r * (1.0 / 6 + r * (1.0 / 24 + r * (1.0 / 120 + r * (1.0 / 720 + r * (1.0 / 5040 + r * (1.0 / 40320))))))));
        return (float)Math.ScaleB(sum, (int)k);
    }

    // ---- System.Numerics' processor-dependent signs ----
    // .NET 8 leaves some signs of zero (and NaN) to the processor (measured, x64 against Arm64): x64 negates a vector by flipping
    // the sign bits and Arm64 as Zero - v, so -(+0) is -0 on one and +0 on the other; vector Min, Max and Clamp give the second
    // operand on a tie of +0 and -0 (or a NaN) on x64 (minps/maxps) and the IEEE answer on Arm (fmin/fmax); Vector2.Dot adds the
    // unused lanes' +0 on x64 (-0 + -0 is +0 there, -0 on Arm). A zero's sign reaches the state's bits and, through atan2 or a
    // division, its behaviour. These do the same on every processor: scalar products and plain comparisons.

    /// <summary>-v: v * -1, the sign bits flipped on every processor.</summary>
    public static Vector3 Neg(Vector3 v) => v * -1f;
    public static Vector2 Neg(Vector2 v) => v * -1f;
    public static Vector4 Neg(Vector4 v) => v * -1f;
    public static Quaternion Neg(Quaternion q) => q * -1f;

    public static Vector3 Min(Vector3 a, Vector3 b) => new(a.X < b.X ? a.X : b.X, a.Y < b.Y ? a.Y : b.Y, a.Z < b.Z ? a.Z : b.Z);
    public static Vector3 Max(Vector3 a, Vector3 b) => new(a.X > b.X ? a.X : b.X, a.Y > b.Y ? a.Y : b.Y, a.Z > b.Z ? a.Z : b.Z);
    public static Vector2 Min(Vector2 a, Vector2 b) => new(a.X < b.X ? a.X : b.X, a.Y < b.Y ? a.Y : b.Y);
    public static Vector2 Max(Vector2 a, Vector2 b) => new(a.X > b.X ? a.X : b.X, a.Y > b.Y ? a.Y : b.Y);

    /// <summary>Vector3.Clamp: Min(Max(v, min), max).</summary>
    public static Vector3 Clamp(Vector3 v, Vector3 min, Vector3 max) => Min(Max(v, min), max);

    public static float Dot(Vector2 a, Vector2 b) => a.X * b.X + a.Y * b.Y;

    /// <summary>Quaternion.CreateFromAxisAngle (which calls MathF.Sin/Cos of the half angle) with this class's sine and cosine.</summary>
    public static Quaternion FromAxisAngle(Vector3 axis, float angle)
    {
        var (s, c) = SinCos(angle * 0.5f);
        return new Quaternion(axis.X * s, axis.Y * s, axis.Z * s, c);
    }

    // ---- float: evaluated in double, rounded once ----
    public static float Sin(float x) => (float)Sin((double)x);
    public static float Cos(float x) => (float)Cos((double)x);
    public static (float Sin, float Cos) SinCos(float x) { var (s, c) = SinCos((double)x); return ((float)s, (float)c); }
    public static float Tan(float x) => (float)Tan((double)x);
    public static float Atan(float x) => (float)Atan((double)x);
    public static float Atan2(float y, float x) => (float)Atan2((double)y, (double)x);
    public static float Asin(float x) => (float)Asin((double)x);
    public static float Acos(float x) => (float)Acos((double)x);
    public static float Exp(float x) => (float)Exp((double)x);
    public static float Log(float x) => (float)Log((double)x);
    public static float Log10(float x) => (float)Log10((double)x);

    /// <summary>Float pow: e^(y·ln x) in plain double (good to far below a float's ulp), rounded once.</summary>
    public static float Pow(float x, float y) => (float)PowCore(x, y, false);
}
