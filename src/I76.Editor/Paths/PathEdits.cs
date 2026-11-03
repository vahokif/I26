using System.Numerics;
using I76.Formats.Mission.Edit;

namespace I76.Editor.Paths;

/// <summary>One change to the path table.</summary>
public abstract record PathOp
{
    public sealed record Insert(int Index, PathEntry Path) : PathOp;
    public sealed record Remove(int Index, PathEntry Path) : PathOp;
    public sealed record Replace(int Index, PathEntry Before, PathEntry After) : PathOp;

    public void Do(PathStore s)
    {
        switch (this)
        {
            case Insert i: s.Insert(i.Index, i.Path); break;
            case Remove r: s.RemoveAt(r.Index); break;
            case Replace p: s.Replace(p.Index, p.After); break;
        }
    }

    public void Undo(PathStore s)
    {
        switch (this)
        {
            case Insert i: s.RemoveAt(i.Index); break;
            case Remove r: s.Insert(r.Index, r.Path); break;
            case Replace p: s.Replace(p.Index, p.Before); break;
        }
    }
}

/// <summary>A path edit as one undo step: the changes to the table (every copy of the path).</summary>
public sealed class PathCommand(string name, PathStore store, IReadOnlyList<PathOp> ops) : IEditCommand
{
    public string Name { get; } = name;
    public IReadOnlyList<PathOp> Ops => ops;
    public int Size => ops.Count * 16;

    /// <summary>Applies the changes now (the command is pushed after).</summary>
    public PathCommand Apply() { foreach (var o in ops) o.Do(store); return this; }

    public void Undo(IMapDocument doc) { for (int i = ops.Count - 1; i >= 0; i--) ops[i].Undo(store); }
    public void Redo(IMapDocument doc) { foreach (var o in ops) o.Do(store); }
}

/// <summary>
/// The path tool's edits as commands over a <see cref="PathStore"/> (not applied: the caller applies and pushes them). A point is
/// written at height 0, as every stock path's is: the game takes the ground's height under it.
/// </summary>
public static class PathEdits
{
    /// <summary>A path's points with one changed, for all its copies.</summary>
    private static PathCommand Reshape(PathStore s, PathGroup g, string name, Func<List<Vector3>, List<Vector3>> change)
    {
        var ops = new List<PathOp>();
        foreach (int i in g.Entries)
        {
            var before = s[i];
            ops.Add(new PathOp.Replace(i, before, before.With(points: change([.. before.Points]))));
        }
        return new PathCommand(name, s, ops);
    }

    public static PathCommand MovePoint(PathStore s, PathGroup g, int k, Vector2 to) =>
        Reshape(s, g, "Move path point", p => { p[k] = new Vector3(to.X, p[k].Y, to.Y); return p; });

    /// <summary>A new point before point <paramref name="k"/> (k = count: after the last).</summary>
    public static PathCommand InsertPoint(PathStore s, PathGroup g, int k, Vector2 at) =>
        Reshape(s, g, "Insert path point", p => { p.Insert(Math.Clamp(k, 0, p.Count), new Vector3(at.X, 0, at.Y)); return p; });

    /// <summary>Removes a point; null when it is the path's last (a path keeps one: the script's tests read its first).</summary>
    public static PathCommand? DeletePoint(PathStore s, PathGroup g, int k) =>
        g.Points.Count <= 1 ? null : Reshape(s, g, "Delete path point", p => { p.RemoveAt(k); return p; });

    public static PathCommand Rename(PathStore s, PathGroup g, string label)
    {
        var ops = g.Entries.Select(i => (PathOp)new PathOp.Replace(i, s[i], s[i].With(name: label))).ToList();
        return new PathCommand("Rename path", s, ops);
    }

    /// <summary>A new path at the end of the table.</summary>
    public static PathCommand Add(PathStore s, string label, IEnumerable<Vector2> points) =>
        new("New path", s, [new PathOp.Insert(s.Count, new PathEntry(label, points.Select(p => new Vector3(p.X, 0, p.Y))))]);

    /// <summary>Removes a path (every copy, last first); null when the script's table holds it (<see cref="PathStore.WhyNotDelete"/>).</summary>
    public static PathCommand? Delete(PathStore s, PathGroup g)
    {
        if (s.WhyNotDelete(g) is not null) return null;
        return new PathCommand("Delete path", s, [.. g.Entries.OrderByDescending(i => i).Select(i => (PathOp)new PathOp.Remove(i, s[i]))]);
    }

    /// <summary>Where a point near <paramref name="at"/> is: the path, the point (or −1), the segment it lies on (the insert position, or −1), and the distance.</summary>
    public readonly record struct Hit(PathGroup Path, int Point, int Segment, float Distance, Vector2 OnLine);

    /// <summary>The nearest point (within <paramref name="pointRadius"/>) or line (within <paramref name="lineRadius"/>) of any path.</summary>
    public static Hit? Pick(IEnumerable<PathGroup> paths, Vector2 at, float pointRadius, float lineRadius)
    {
        Hit? best = null;
        foreach (var g in paths)
        {
            for (int k = 0; k < g.Points.Count; k++)
            {
                float d = Vector2.Distance(XZ(g.Points[k]), at);
                // a point wins over a line at the same distance: it is what a click near it means
                if (d <= pointRadius && (best is null || best.Value.Point < 0 || d < best.Value.Distance)) best = new Hit(g, k, -1, d, XZ(g.Points[k]));
            }
            if (best is { Point: >= 0 }) continue;
            for (int k = 0; k + 1 < g.Points.Count; k++)
            {
                Vector2 a = XZ(g.Points[k]), b = XZ(g.Points[k + 1]), ab = b - a;
                float l2 = ab.LengthSquared();
                float t = l2 < 1e-6f ? 0 : Math.Clamp(Vector2.Dot(at - a, ab) / l2, 0, 1);
                var on = a + ab * t;
                float d = Vector2.Distance(on, at);
                if (d <= lineRadius && (best is null || best.Value.Point < 0 && d < best.Value.Distance)) best = new Hit(g, -1, k + 1, d, on);
            }
        }
        return best;
    }

    public static Vector2 XZ(Vector3 v) => new(v.X, v.Z);

    /// <summary>A path's length along the ground's plane (metres).</summary>
    public static float Length(IReadOnlyList<Vector3> pts)
    {
        float l = 0;
        for (int k = 0; k + 1 < pts.Count; k++) l += Vector2.Distance(XZ(pts[k]), XZ(pts[k + 1]));
        return l;
    }
}
