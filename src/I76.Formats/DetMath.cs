namespace I76.Formats;

/// <summary>
/// Sine, cosine, arctangent, logarithm, exponential and power from IEEE additions, multiplications and divisions alone, so
/// they give the same bits on every machine. The runtime's MathF.Sin and the rest come from the platform's maths library,
/// which may round differently in the last place on Windows and on Linux: fine for drawing, not for ground two machines
/// must agree on to the bit (a network game). Arguments are reduced to a small range and summed as series in double;
/// accurate to about 1e-15 relative over the ranges the game uses (angles of a few turns, positive powers).
/// </summary>
public static class DetMath
{
    public const double Pi = 3.14159265358979323846;
    // π/2 in two parts (the high part has trailing zero bits, so k·high is exact for small k)
    private const double HalfPiHi = 1.57079632673412561417e+00, HalfPiLo = 6.07710050650619224932e-11;
    private const double Ln2Hi = 6.93147180369123816490e-01, Ln2Lo = 1.90821492927058770002e-10;

    /// <summary>Sine and cosine of <paramref name="x"/> radians.</summary>
    public static (double Sin, double Cos) SinCos(double x)
    {
        double k = Math.Round(x * (2 / Pi));
        double r = x - k * HalfPiHi - k * HalfPiLo;
        double r2 = r * r;
        // Taylor series to r^17 / r^16: |r| <= π/4, the next terms are under 1e-17
        double s = r * (1 + r2 * (-1.0 / 6 + r2 * (1.0 / 120 + r2 * (-1.0 / 5040 + r2 * (1.0 / 362880 + r2 * (-1.0 / 39916800
            + r2 * (1.0 / 6227020800 + r2 * (-1.0 / 1307674368000 + r2 * (1.0 / 355687428096000)))))))));
        double c = 1 + r2 * (-1.0 / 2 + r2 * (1.0 / 24 + r2 * (-1.0 / 720 + r2 * (1.0 / 40320 + r2 * (-1.0 / 3628800
            + r2 * (1.0 / 479001600 + r2 * (-1.0 / 87178291200 + r2 * (1.0 / 20922789888000))))))));
        return ((long)k & 3) switch
        {
            0 => (s, c),
            1 => (c, -s),
            2 => (-s, -c),
            _ => (-c, s),
        };
    }

    public static double Sin(double x) => SinCos(x).Sin;
    public static double Cos(double x) => SinCos(x).Cos;

    /// <summary>The angle of (x, y) from +x toward +y, -π..π, as Math.Atan2 (0 for the origin).</summary>
    public static double Atan2(double y, double x)
    {
        if (x == 0 && y == 0) return 0;
        double ax = Math.Abs(x), ay = Math.Abs(y);
        bool swap = ay > ax;
        double t = swap ? ax / ay : ay / ax;   // 0..1
        double a = t > 0.41421356237309503 ? Pi / 4 + AtanSmall((t - 1) / (t + 1)) : AtanSmall(t);
        if (swap) a = Pi / 2 - a;
        if (x < 0) a = Pi - a;
        return y < 0 ? -a : a;
    }

    /// <summary>arctan for |u| up to tan(π/8): its series to u^43 (the next term is under 1e-18).</summary>
    private static double AtanSmall(double u)
    {
        double u2 = u * u, sum = 0;
        for (int k = 21; k >= 0; k--) sum = (k % 2 == 0 ? 1.0 : -1.0) / (2 * k + 1) + u2 * sum;
        return u * sum;
    }

    /// <summary>The natural logarithm of a positive finite <paramref name="x"/>.</summary>
    public static double Log(double x)
    {
        if (!(x > 0)) return double.NegativeInfinity;
        if (x < 2.2250738585072014e-308) return Log(x * 4503599627370496.0) - 52 * (Ln2Hi + Ln2Lo);   // subnormal
        long bits = BitConverter.DoubleToInt64Bits(x);
        int e = (int)((bits >> 52) & 0x7ff) - 1023;
        double m = BitConverter.Int64BitsToDouble((bits & 0x000fffffffffffffL) | 0x3ff0000000000000L);   // 1..2
        if (m > 1.4142135623730951) { m *= 0.5; e++; }
        // ln m = 2 atanh(s), s = (m - 1) / (m + 1), |s| < 0.172: to s^27 (the next term is under 1e-21)
        double s = (m - 1) / (m + 1), s2 = s * s, sum = 0;
        for (int k = 13; k >= 0; k--) sum = 1.0 / (2 * k + 1) + s2 * sum;
        return e * Ln2Hi + (e * Ln2Lo + 2 * s * sum);
    }

    /// <summary>e to the <paramref name="x"/>.</summary>
    public static double Exp(double x)
    {
        if (x > 709) return double.PositiveInfinity;
        if (x < -745) return 0;
        double k = Math.Round(x / (Ln2Hi + Ln2Lo));
        double r = x - k * Ln2Hi - k * Ln2Lo;   // |r| <= 0.35
        // Taylor to r^16 (the next term is under 1e-22)
        double sum = 1;
        for (int n = 16; n >= 1; n--) sum = 1 + r * sum / n;
        return Math.ScaleB(sum, (int)k);
    }

    /// <summary><paramref name="x"/> to the <paramref name="p"/>, for x &gt;= 0 (0 for x = 0).</summary>
    public static double Pow(double x, double p) => x <= 0 ? 0 : Exp(p * Log(x));

    public static float Sin(float x) => (float)Sin((double)x);
    public static float Cos(float x) => (float)Cos((double)x);
    public static float Atan2(float y, float x) => (float)Atan2((double)y, (double)x);
    public static float Pow(float x, float p) => (float)Pow((double)x, (double)p);
}
