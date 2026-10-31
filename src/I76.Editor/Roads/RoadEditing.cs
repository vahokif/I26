using System.Numerics;
using I76.Formats.Mission.Edit;

namespace I76.Editor.Roads;

/// <summary>Commands for the edits of existing roads (the drawing is <see cref="RoadPlanner"/>): each is applied when made and can go on the undo stack.</summary>
public static class RoadEditing
{
    /// <summary>
    /// Replaces road <paramref name="index"/> by <paramref name="after"/>. With <paramref name="conform"/> the ground under points
    /// <paramref name="from"/>..<paramref name="to"/> of the new road is cut and filled to a profile that starts and ends on the ground
    /// where it stands, so a drag reshapes the ground only where the road moved.
    /// </summary>
    public static RoadCommand Replace(IMapDocument doc, RoadStore roads, int index, RoadEdit after, int from, int to, bool conform,
        RoadGrade.Settings? grade = null, RoadTerrain.Settings? shoulder = null, string name = "Edit road")
    {
        var rec = new HeightRecorder(doc);
        if (conform)
        {
            from = Math.Clamp(from, 0, after.Points.Count - 1); to = Math.Clamp(to, from, after.Points.Count - 1);
            var span = after.Points.Skip(from).Take(to - from + 1).Select(p => p.Centre).ToList();
            if (span.Count >= 2)
            {
                var pins = new Dictionary<int, float> { [0] = RoadTerrain.Ground(doc, span[0].X, span[0].Y), [span.Count - 1] = RoadTerrain.Ground(doc, span[^1].X, span[^1].Y) };
                var h = RoadGrade.Profile(span, p => RoadTerrain.Ground(doc, p.X, p.Y), grade, pins);
                RoadTerrain.Conform(doc, rec, span, h, shoulder);
            }
        }
        RoadSurface.Update(doc, rec, roads[index], after, Others(roads, index));
        return new RoadCommand(name, roads, [new RoadOp.Replace(index, roads[index], after)], rec.Tiles()).Apply();
    }

    private static IEnumerable<RoadEdit> Others(RoadStore roads, int index) => roads.Roads.Where((_, i) => i != index);

    /// <summary>Removes a road (<paramref name="doc"/> null: the roads only); the ground's surface under it goes back to the ground beside it (the same undo step).</summary>
    public static RoadCommand Bulldoze(IMapDocument? doc, RoadStore roads, int index)
    {
        Dictionary<int, TileDelta>? tiles = null;
        if (doc is not null)
        {
            var rec = new HeightRecorder(doc);
            RoadSurface.Update(doc, rec, roads[index], null, Others(roads, index));
            tiles = rec.Tiles();
        }
        return new RoadCommand("Bulldoze road", roads, [new RoadOp.Remove(index, roads[index])], tiles).Apply();
    }

    public static RoadCommand Retype(IMapDocument? doc, RoadStore roads, int index, uint kind)
    {
        var after = RoadOps.Retype(roads[index], kind);
        Dictionary<int, TileDelta>? tiles = null;
        if (doc is not null)
        {
            var rec = new HeightRecorder(doc);
            RoadSurface.Update(doc, rec, roads[index], after, Others(roads, index));
            tiles = rec.Tiles();
        }
        return new RoadCommand("Change road type", roads, [new RoadOp.Replace(index, roads[index], after)], tiles).Apply();
    }

    /// <summary>Cuts a road in two at a point; null when a part would be too short.</summary>
    public static RoadCommand? Split(RoadStore roads, int index, int point)
    {
        if (RoadOps.Split(roads[index], point) is not { } parts) return null;
        return new RoadCommand("Split road", roads, [new RoadOp.Replace(index, roads[index], parts.First), new RoadOp.Insert(index + 1, parts.Second)]).Apply();
    }
}
