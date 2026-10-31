namespace I76.Formats;

public static class MathUtil
{
    /// <summary>Rows unit length and mutually perpendicular, within tol (done in double, as Python does).</summary>
    public static bool Orthonormal(IReadOnlyList<float> m, double tol = 1e-3)
    {
        for (int r = 0; r < 3; r++)
        {
            double s = 0;
            for (int k = 0; k < 3; k++) s += (double)m[r * 3 + k] * m[r * 3 + k];
            if (Math.Abs(Math.Sqrt(s) - 1.0) > tol) return false;
        }
        for (int i = 0; i < 3; i++)
            for (int j = i + 1; j < 3; j++)
            {
                double s = 0;
                for (int k = 0; k < 3; k++) s += (double)m[i * 3 + k] * m[j * 3 + k];
                if (Math.Abs(s) > tol) return false;
            }
        return true;
    }
}
