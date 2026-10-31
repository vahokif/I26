using System.Numerics;

namespace I76.Formats.Assembly;

/// <summary>
/// A transform as column-vector maths, p' = R·p + t, in the game's own frame
/// (+X right, +Y up, +Z forward).
///
/// The engine stores rotations the other way round: the nine floats are the
/// object's axes as **rows** (right, up, forward), and the original's point transform
/// computes x·row0 + y·row1 + z·row2 + t, i.e. Eᵀ·p. So an engine
/// matrix E becomes R = Eᵀ here (<see cref="FromEngine"/>), and composing two
/// engine transforms is R_parent·R_child.
/// </summary>
public readonly record struct Affine(float[] R, Vector3 T)
{
    public static readonly Affine Identity = new([1, 0, 0, 0, 1, 0, 0, 0, 1], Vector3.Zero);

    public static Affine From(Bwd2.Transform t) => FromEngine(t.Rotation, new Vector3(t.Translation[0], t.Translation[1], t.Translation[2]));

    /// <summary>An engine rotation (rows are the axes) and translation.</summary>
    public static Affine FromEngine(IReadOnlyList<float> e, Vector3 t) =>
        new([e[0], e[3], e[6], e[1], e[4], e[7], e[2], e[5], e[8]], t);

    public Vector3 Rotate(Vector3 v) => new(
        R[0] * v.X + R[1] * v.Y + R[2] * v.Z,
        R[3] * v.X + R[4] * v.Y + R[5] * v.Z,
        R[6] * v.X + R[7] * v.Y + R[8] * v.Z);

    public Vector3 Apply(Vector3 v) => Rotate(v) + T;

    /// <summary>child expressed in this (the parent's) frame.</summary>
    /// <summary>The same placement shrunk by <paramref name="s"/> about the point <paramref name="p"/>: it looks the same from p, and lies nearer.</summary>
    public Affine ScaledAbout(Vector3 p, float s) => new([.. R.Select(x => x * s)], p + (T - p) * s);

    public Affine Compose(Affine child)
    {
        var r = new float[9];
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
                r[i * 3 + j] = R[i * 3] * child.R[j] + R[i * 3 + 1] * child.R[3 + j] + R[i * 3 + 2] * child.R[6 + j];
        return new Affine(r, Apply(child.T));
    }
}
