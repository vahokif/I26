using Godot;
using I76.Formats.Assembly;

namespace I76.Game.Sim;

/// <summary>
/// The game's frame is left-handed (+X right, +Y up, +Z forward); Godot's is
/// right-handed with -Z forward. Mirroring Z maps one to the other, so a
/// game heading of +Z is Godot's forward. A mirror reverses handedness, so
/// triangle winding is reversed at the same boundary (see MeshBuilder).
/// </summary>
public static class Coords
{
    public static Vector3 ToGodot(System.Numerics.Vector3 v) => new(v.X, v.Y, -v.Z);
    public static Vector3 ToGodot(double x, double y, double z) => new((float)x, (float)y, (float)-z);
    public static System.Numerics.Vector3 ToGame(Vector3 v) => new(v.X, v.Y, -v.Z);

    /// <summary>
    /// An engine rotation (the nine floats are the object's axes as rows; the
    /// engine applies Eᵀ) to a Godot basis: S·Eᵀ·S with S = diag(1, 1, -1).
    /// </summary>
    public static Basis ToGodot(float[] r)
    {
        float M(int i, int j) => r[j * 3 + i] * (i == 2 ? -1 : 1) * (j == 2 ? -1 : 1);
        return new Basis(new Vector3(M(0, 0), M(1, 0), M(2, 0)),
                         new Vector3(M(0, 1), M(1, 1), M(2, 1)),
                         new Vector3(M(0, 2), M(1, 2), M(2, 2)));
    }

    /// <summary>An Affine (already column-vector maths, R·p + t) to Godot.</summary>
    public static Transform3D ToGodot(Affine a)
    {
        float M(int i, int j) => a.R[i * 3 + j] * (i == 2 ? -1 : 1) * (j == 2 ? -1 : 1);
        var b = new Basis(new Vector3(M(0, 0), M(1, 0), M(2, 0)), new Vector3(M(0, 1), M(1, 1), M(2, 1)), new Vector3(M(0, 2), M(1, 2), M(2, 2)));
        return new Transform3D(b, ToGodot(a.T));
    }

    public static Transform3D ToGodot(float[] rotation, System.Numerics.Vector3 position) => new(ToGodot(rotation), ToGodot(position));
}
