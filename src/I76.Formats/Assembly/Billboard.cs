using System.Numerics;

namespace I76.Formats.Assembly;

/// <summary>
/// The engine's camera-facing cards. A scene node flagged
/// <see cref="Bwd2.Bwd2File.BillboardFlag"/> is drawn with its world rotation swapped for one matrix the frame
/// shares (built each frame from the view's forward), its position
/// kept: every card turns about the vertical through its node's origin to face the way the camera
/// looks, never tilting. The node's own rotation, its parents' and the placement's play no part in it.
/// </summary>
public static class Billboard
{
    /// <summary>
    /// The frame's card rotation as the engine stores it (the nine floats are the axes as rows, <see cref="Affine.FromEngine"/>)
    /// from the camera's forward in the game frame (the view's +z: the third column of its world-to-view rotation,
    /// camera +0x48 and +0x60). With a = -forward.x, b = -forward.z over their length: rows (b, 0, -a), (0, 1, 0), (a, 0, b),
    /// so the card's +z points back along the camera's horizontal forward and its +x to the camera's left. Looking
    /// straight down (a² + b² under 1e-5) the engine takes a = 0, b = -1.
    /// </summary>
    public static float[] EngineRows(Vector3 forward)
    {
        float a = -forward.X, b = -forward.Z;
        float l2 = a * a + b * b;
        if (l2 >= 1e-5f)
        {
            float k = 1f / MathF.Sqrt(l2);
            a *= k; b *= k;
        }
        else { a = 0; b = -1; }
        return [b, 0, -a, 0, 1, 0, a, 0, b];
    }

    /// <summary><see cref="EngineRows"/> as column-vector maths in the game frame.</summary>
    public static Affine Rotation(Vector3 forward) => Affine.FromEngine(EngineRows(forward), Vector3.Zero);
}
