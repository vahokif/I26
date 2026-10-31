using System.Numerics;
using I76.Formats.Mission.Edit;

namespace I76.Editor.Roads;

/// <summary>
/// Edits of one road: they return the new road (the old one is untouched, for undo). Edges are recomputed for the points that moved
/// and their neighbours; the points that did not are the file's own, byte for byte.
/// </summary>
public static class RoadOps
{
    /// <summary>
    /// Moves a point to <paramref name="to"/>. With <paramref name="reach"/> &gt; 0 the points within that many steps along the road follow,
    /// by a smoothstep falloff, so the curve bends there instead of kinking; 0 moves only that point.
    /// </summary>
    public static RoadEdit MovePoint(RoadEdit road, int index, Vector2 to, int reach, Vector2? heading = null)
    {
        var c = RoadBuilder.Centres(road);
        var delta = to - c[index];
        bool loop = RoadBuilder.IsClosed(road);
        int n = c.Count;
        var touched = new SortedSet<int>();
        for (int k = -reach; k <= reach; k++)
        {
            int i = index + k;
            if (loop) i = ((i % (n - 1)) + (n - 1)) % (n - 1);
            else if (i < 0 || i >= n) continue;
            float t = reach == 0 ? 1 : 1 - Math.Abs(k) / (reach + 1f);
            float w = t * t * (3 - 2 * t);
            c[i] += delta * w;
            touched.Add(i);
        }
        if (loop) c[n - 1] = c[0];
        var moved = Refit(road, c, touched);
        // an end dragged onto a structure faces it square on
        if (heading is { } h && (index == 0 || index == c.Count - 1) && !loop)
        {
            var pts = moved.Points; var flag = pts[index].Flag;
            pts[index] = RoadBuilder.Point(c[index], Vector2.Normalize(h), flag);
        }
        return moved;
    }

    /// <summary>Puts a point at <paramref name="centre"/> after point <paramref name="after"/>.</summary>
    public static RoadEdit InsertPoint(RoadEdit road, int after, Vector2 centre)
    {
        var c = RoadBuilder.Centres(road);
        c.Insert(after + 1, centre);
        var pts = road.Points.Select(p => p.Clone()).ToList();
        pts.Insert(after + 1, RoadBuilder.Point(centre, new Vector2(0, 1)));
        return Refit(new RoadEdit(road.Kind, pts) { Tail = (byte[])road.Tail.Clone() }, c, [after, after + 1, after + 2]);
    }

    /// <summary>Takes a point out; null when fewer than <see cref="RoadCurves.MinPoints"/> would be left (bulldoze the road instead).</summary>
    public static RoadEdit? DeletePoint(RoadEdit road, int index)
    {
        if (road.Points.Count - 1 < RoadCurves.MinPoints) return null;
        var c = RoadBuilder.Centres(road);
        var pts = road.Points.Select(p => p.Clone()).ToList();
        c.RemoveAt(index); pts.RemoveAt(index);
        return Refit(new RoadEdit(road.Kind, pts) { Tail = (byte[])road.Tail.Clone() }, c, [index - 1, index]);
    }

    /// <summary>Cuts the road at a point into two roads that share it (each end is a road end: the AI can start and stop there). Null if either part would be too short.</summary>
    public static (RoadEdit First, RoadEdit Second)? Split(RoadEdit road, int index)
    {
        if (index + 1 < RoadCurves.MinPoints || road.Points.Count - index < RoadCurves.MinPoints) return null;
        var a = new RoadEdit(road.Kind, road.Points.Take(index + 1).Select(p => p.Clone()).ToList());
        var b = new RoadEdit(road.Kind, road.Points.Skip(index).Select(p => p.Clone()).ToList());
        return (a, b);
    }

    public static RoadEdit Retype(RoadEdit road, uint kind) => new(kind, road.Points.Select(p => p.Clone()).ToList()) { Tail = (byte[])road.Tail.Clone() };

    /// <summary>The road with the edges of <paramref name="touched"/> (and one point either side) rebuilt from the centres <paramref name="c"/>.</summary>
    private static RoadEdit Refit(RoadEdit road, List<Vector2> c, IEnumerable<int> touched)
    {
        var pts = road.Points.Select(p => p.Clone()).ToList();
        var redo = new SortedSet<int>();
        foreach (int i in touched) for (int k = i - 1; k <= i + 1; k++) if (k >= 0 && k < pts.Count) redo.Add(k);
        int n = c.Count;
        bool loop = RoadBuilder.IsClosed(road);
        foreach (int i in redo)
        {
            Vector2 t;
            if (loop && (i == 0 || i == n - 1)) t = c[1] - c[n - 2];
            else if (i == 0) t = c[1] - c[0];
            else if (i == n - 1) t = c[i] - c[i - 1];
            else t = c[i + 1] - c[i - 1];
            t = t.LengthSquared() < 1e-12f ? new Vector2(0, 1) : Vector2.Normalize(t);
            var flag = pts[i].Flag;
            pts[i] = RoadBuilder.Point(c[i], t, flag);
        }
        return new RoadEdit(road.Kind, pts) { Tail = (byte[])road.Tail.Clone() };
    }
}
