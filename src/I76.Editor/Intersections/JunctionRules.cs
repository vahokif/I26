using System.Numerics;
using I76.Formats.Mission.Edit;

namespace I76.Editor.Intersections;

/// <summary>A road's centre line with arc lengths: nearest point, point at a distance.</summary>
public sealed class Poly
{
    public readonly Vector2[] P;
    public readonly float[] S;
    public float Length => S[^1];

    public Poly(IReadOnlyList<Vector2> pts)
    {
        P = [.. pts];
        S = new float[P.Length];
        for (int i = 1; i < P.Length; i++) S[i] = S[i - 1] + Vector2.Distance(P[i - 1], P[i]);
    }

    public static Poly Of(RoadEdit r) => new(r.Points.Select(p => p.Centre).ToList());

    /// <summary>The nearest point of the line to <paramref name="q"/>: its arc length, the point, the unit tangent there (the road's direction of increasing index) and the distance.</summary>
    public (float S, Vector2 Pt, Vector2 Tan, float D) Nearest(Vector2 q)
    {
        float bs = 0, bd = float.MaxValue; Vector2 bp = P[0], bt = new(0, 1);
        for (int k = 0; k + 1 < P.Length; k++)
        {
            Vector2 a = P[k], ab = P[k + 1] - a;
            float l2 = ab.LengthSquared();
            if (l2 < 1e-9f) continue;
            float t = Math.Clamp(Vector2.Dot(q - a, ab) / l2, 0, 1);
            var p = a + ab * t;
            float d = Vector2.DistanceSquared(q, p);
            if (d < bd) { bd = d; bp = p; bt = Vector2.Normalize(ab); bs = S[k] + t * MathF.Sqrt(l2); }
        }
        return (bs, bp, bt, MathF.Sqrt(bd));
    }

    public Vector2 At(float s)
    {
        s = Math.Clamp(s, 0, Length);
        for (int k = 0; k + 1 < P.Length; k++)
            if (s <= S[k + 1] || k + 2 == P.Length)
            {
                float l = S[k + 1] - S[k];
                return l < 1e-9f ? P[k] : Vector2.Lerp(P[k], P[k + 1], (s - S[k]) / l);
            }
        return P[^1];
    }
}

/// <summary>Which piece fits a junction, by angle: the thresholds the survey of the stock maps gives (docs/map-format-writing.md).</summary>
public static class JunctionRules
{
    /// <summary>A stem within this many degrees of square to the through road gets a T (an X for two crossing roads): the pieces' arms are at right angles.</summary>
    public const float RightAngleTolerance = 15f;
    /// <summary>A branch within this many degrees of the fork pieces' 41 gets a fork.</summary>
    public const float ForkTolerance = 10f;

    /// <summary>
    /// The shape for a road leaving a through road (a stem), and the through direction that leans toward it. <paramref name="through"/>
    /// is the through road's tangent (either way along it), <paramref name="stemOut"/> the stem's direction leaving the junction.
    /// A stem square to the road gets a T; one leaning forward at about 41 degrees a fork, right or left; any other angle: none.
    /// </summary>
    public static (PieceShape Shape, Vector2 Through)? Classify(Vector2 through, Vector2 stemOut)
    {
        through = Vector2.Normalize(through); stemOut = Vector2.Normalize(stemOut);
        float d = Vector2.Dot(through, stemOut);
        var t = d >= 0 ? through : -through;
        float lean = MathF.Acos(Math.Clamp(MathF.Abs(d), 0, 1)) * 180f / MathF.PI;      // 0 (along the road) to 90 (square)
        if (lean >= 90f - RightAngleTolerance) return (PieceShape.T, t);
        if (MathF.Abs(lean - PieceCatalogue.ForkDegrees) <= ForkTolerance)
        {
            // facing along t (+z), the right is +x: a stem on that side is a right fork
            float cross = t.X * stemOut.Y - t.Y * stemOut.X;
            return (cross < 0 ? PieceShape.ForkRight : PieceShape.ForkLeft, t);
        }
        return null;
    }

    /// <summary>Whether two roads crossing at the given tangents are square enough for an X.</summary>
    public static bool CrossingIsSquare(Vector2 a, Vector2 b)
    {
        a = Vector2.Normalize(a); b = Vector2.Normalize(b);
        return MathF.Abs(Vector2.Dot(a, b)) <= MathF.Cos((90f - RightAngleTolerance) * MathF.PI / 180f);
    }

    /// <summary>
    /// The pose of a piece whose <see cref="PieceDef.Origin"/> stands at <paramref name="anchor"/> and whose local +z runs along
    /// <paramref name="through"/>. For a T the through direction is turned about so the stem (at <paramref name="stemOut"/>) is on the +x side.
    /// </summary>
    public static PiecePose Fit(PieceDef def, Vector2 anchor, Vector2 through, Vector2? stemOut = null)
    {
        through = Vector2.Normalize(through);
        if (def.Shape == PieceShape.T && stemOut is { } s)
        {
            // local +x is (tz, -tx) for a through direction (tx, tz)
            if (through.Y * s.X - through.X * s.Y < 0) through = -through;
        }
        return PiecePose.Anchored(anchor, def.Origin, MathF.Atan2(through.X, through.Y));
    }

    /// <summary>The piece's arms in the world: (position, outward unit vector), in the definition's order.</summary>
    public static (Vector2 Position, Vector2 Out)[] WorldArms(PieceDef def, PiecePose pose) => [.. def.Arms.Select(pose.World)];

    /// <summary>
    /// The pose, among the four quarter turns of <paramref name="pose"/> about <paramref name="centre"/>, that puts the arms of
    /// <paramref name="def"/> on the given outward directions (every direction must be matched by an arm, none left over); null if none does.
    /// </summary>
    public static PiecePose? FitArms(PieceDef def, Vector2 centre, float yaw, IReadOnlyList<Vector2> outs)
    {
        for (int k = 0; k < 4; k++)
        {
            var pose = PiecePose.Anchored(centre, def.Origin, yaw + k * MathF.PI / 2);
            var arms = WorldArms(def, pose);
            if (arms.Length == outs.Count && outs.All(o => arms.Any(a => Vector2.Dot(a.Out, o) > 0.97f))) return pose;
        }
        return null;
    }
}
