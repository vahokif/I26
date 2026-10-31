using System.Numerics;

namespace I76.Game.World;

/// <summary>
/// The pure part of DropShadows' projection (no engine): where a shadow lands on the terrain. The ground is a function of
/// (x, z) in whatever space the caller works (Godot's or the game's) and <c>toLight</c> points toward the light there.
/// </summary>
public static class ShadowDrape
{
    /// <summary>The longest a shadow ray runs.</summary>
    public const float Ray = 128f;

    /// <summary>The steepest the three heights round the hit may make the plane: 2 m of rise to the 2 m offsets, 45 degrees.</summary>
    public const float MaxRise = 2f;

    /// <summary>
    /// The plane (normal, n·p = w) through the surface heights 2 units round where the ray from <paramref name="centre"/> away from the light
    /// meets the surface; null if it does not within <see cref="Ray"/>. <paramref name="surface"/> is the height of what stands at (x, z) for a thing at
    /// height y: the terrain, or a drive-on structure (a bridge deck) at or under y. Beside a deck the surface drops to the canyon floor, so the
    /// three heights are held within <see cref="MaxRise"/> of the hit: the plane under a car on a bridge stays the deck's, never a wall.
    /// </summary>
    public static Vector4? Plane(Vector3 centre, Vector3 toLight, Func<float, float, float, float> surface)
    {
        Vector3? hit = null;
        float prev = 0;
        for (float t = 0; t <= Ray; t += 1f)
        {
            var p = centre - toLight * t;
            float above = p.Y - surface(p.X, p.Y, p.Z);
            if (above <= 0)
            {
                // between the last step and this one
                float f = t == 0 ? 0 : prev / (prev - above);
                hit = centre - toLight * (t - 1f + f);
                break;
            }
            prev = above;
        }
        if (hit is not { } h) return null;
        Vector3 At(float dx, float dz) => new(h.X + dx, Math.Clamp(surface(h.X + dx, h.Y + 0.5f, h.Z + dz), h.Y - MaxRise, h.Y + MaxRise), h.Z + dz);
        var a = At(-2, 2);
        var b = At(2, 2);
        var c = At(0, -2);
        var n = Vector3.Normalize(Vector3.Cross(b - a, c - a));
        if (n.Y < 0) n = -n;
        return new Vector4(n, Vector3.Dot(n, a));
    }

    /// <summary>Where the ray along the light from a point at <paramref name="w"/> meets the terrain, and the point's height above the terrain under it (negative: it lies under the ground and casts nothing).</summary>
    public static (Vector3 Point, float Height) Of(Vector3 w, Vector3 toLight, Func<float, float, float> ground)
    {
        float h = w.Y - ground(w.X, w.Z);
        if (h <= 0) return (w, h);
        float prev = h;
        for (float t = 1f; t <= Ray; t += 1f)
        {
            var q = w - toLight * t;
            float above = q.Y - ground(q.X, q.Z);
            if (above <= 0) return (w - toLight * (t - 1f + prev / (prev - above)), h);
            prev = above;
        }
        return (w - toLight * (h / MathF.Max(toLight.Y, 0.05f)), h);   // over a drop deeper than the ray: down to the ground under it
    }

    /// <summary>The longest edge a drawn shadow triangle keeps (<see cref="Split"/>), metres.</summary>
    public const float MaxEdge = 4f;
    private const int MaxTriangles = 30000;

    /// <summary>
    /// The triangle (a, b, c) cut at its longest edge's middle, again and again, until none is longer than <paramref name="maxEdge"/> (or the
    /// <see cref="MaxTriangles"/> are reached), each as its three corners with their normals interpolated: each corner of a bridge's shadow takes its
    /// own point on the ground (<see cref="Of"/>), and a triangle of corners that far apart is a sheet laid across all that lies between them.
    /// </summary>
    public static List<(Vector3 P, Vector3 N)[]> Split((Vector3 P, Vector3 N) a, (Vector3 P, Vector3 N) b, (Vector3 P, Vector3 N) c, float maxEdge, int budget = MaxTriangles)
    {
        var done = new List<(Vector3 P, Vector3 N)[]>();
        var todo = new Stack<(Vector3 P, Vector3 N)[]>();
        todo.Push([a, b, c]);
        while (todo.Count > 0)
        {
            var t = todo.Pop();
            float ab = Vector3.Distance(t[0].P, t[1].P), bc = Vector3.Distance(t[1].P, t[2].P), ca = Vector3.Distance(t[2].P, t[0].P);
            float longest = MathF.Max(ab, MathF.Max(bc, ca));
            if (longest <= maxEdge || done.Count + todo.Count >= budget) { done.Add(t); continue; }
            // the longest edge i -> j, the corner k opposite
            int i = ab == longest ? 0 : bc == longest ? 1 : 2, j = (i + 1) % 3, k = (i + 2) % 3;
            var m = ((t[i].P + t[j].P) * 0.5f, Vector3.Normalize(t[i].N + t[j].N));
            todo.Push([t[i], m, t[k]]);
            todo.Push([m, t[j], t[k]]);
        }
        return done;
    }

    /// <summary>
    /// Whether the ground between two shadow points keeps to the straight line joining them (to a tolerance that grows with the span): a triangle
    /// whose corners fall either side of a cliff's rim, one on the plateau and one on the floor below, would otherwise hang as a dark sheet in the air
    /// across the canyon (the shadows that floated under Seminole's bridge, #86). Points under the ground (<paramref name="a"/> or <paramref name="b"/> with no height) are not tested.
    /// </summary>
    public static bool Connected(Vector3 a, Vector3 b, Func<float, float, float> ground)
    {
        float dx = b.X - a.X, dz = b.Z - a.Z, len = MathF.Sqrt(dx * dx + dz * dz);
        float tol = 1f + 0.1f * len;
        int n = Math.Clamp((int)MathF.Ceiling(len), 2, 64);
        for (int i = 1; i < n; i++)
        {
            float f = i / (float)n;
            if (MathF.Abs(ground(a.X + dx * f, a.Z + dz * f) - (a.Y + (b.Y - a.Y) * f)) > tol) return false;
        }
        return true;
    }
}
