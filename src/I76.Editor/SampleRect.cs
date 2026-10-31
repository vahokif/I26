namespace I76.Editor;

/// <summary>An inclusive rectangle of heightfield samples (sample coordinates: 5 world units apart, X0..X1 along X, Z0..Z1 along Z).</summary>
public readonly record struct SampleRect(int X0, int Z0, int X1, int Z1)
{
    public int Width => X1 - X0 + 1;
    public int Height => Z1 - Z0 + 1;
    public bool Intersects(SampleRect o) => X0 <= o.X1 && o.X0 <= X1 && Z0 <= o.Z1 && o.Z0 <= Z1;
    public SampleRect Union(SampleRect o) => new(Math.Min(X0, o.X0), Math.Min(Z0, o.Z0), Math.Max(X1, o.X1), Math.Max(Z1, o.Z1));
    public SampleRect Expand(int n) => new(X0 - n, Z0 - n, X1 + n, Z1 + n);
    public bool Contains(int sx, int sz) => sx >= X0 && sx <= X1 && sz >= Z0 && sz <= Z1;
}
