using System.Numerics;
using I76.Formats.Mission.Edit;

namespace I76.Editor.Roads;

public enum DrawMode { Straight, Curve, Cubic, Freeform }

/// <summary>What the player has drawn: clicks per piece, each piece's end snap. See <see cref="RoadPlanner"/>.</summary>
public sealed class RoadDraft
{
    public DrawMode Mode = DrawMode.Straight;
    /// <summary>The clicks of the piece being drawn (a Straight chain keeps adding to one piece), the live cursor not included.</summary>
    public List<Vector2> Clicks = [];
    /// <summary>The flattened pieces finished so far (a Shift chain of curves).</summary>
    public List<List<Vector2>> Done = [];
    public SnapResult? Start, End;
}

/// <summary>The road about to be made: its centres (resampled), the road it extends if any, the ends' headings and pinned heights.</summary>
public sealed record RoadPlan(
    IReadOnlyList<Vector2> Centres,
    uint Kind,
    int ExtendRoad,
    bool ExtendAtStart,
    Vector2? StartDir,
    Vector2? EndDir,
    IReadOnlyDictionary<int, float> Pins,
    bool Closes,
    float MinRadius,
    string? Problem);

public static class RoadPlanner
{
    /// <summary>A bend tighter than this folds the quads on the inside (the road is 10 wide).</summary>
    public const float FoldRadius = 6f;

    /// <summary>The flattened path of the clicks for a mode, as drawn (not yet resampled).</summary>
    public static List<Vector2> Path(DrawMode mode, IReadOnlyList<Vector2> clicks)
    {
        if (clicks.Count < 2) return [.. clicks];
        switch (mode)
        {
            case DrawMode.Curve when clicks.Count >= 3:
                return RoadCurves.Quadratic(clicks[0], clicks[1], clicks[2]);
            case DrawMode.Cubic when clicks.Count >= 4:
                return RoadCurves.Cubic(clicks[0], clicks[1], clicks[2], clicks[3]);
            case DrawMode.Cubic when clicks.Count == 3:
                return RoadCurves.Quadratic(clicks[0], clicks[1], clicks[2]);
            case DrawMode.Freeform:
                return RoadCurves.Spline(clicks);
            default:
                return RoadCurves.Fillet(clicks, 32f, 10f);
        }
    }

    /// <summary>The full path of a draft with the live cursor as its last click, the finished pieces first.</summary>
    public static List<Vector2> DraftPath(RoadDraft d, Vector2? cursor)
    {
        var clicks = new List<Vector2>(d.Clicks);
        if (cursor is { } c) clicks.Add(c);
        var piece = Path(d.Mode, clicks);
        var all = new List<Vector2>();
        foreach (var done in d.Done) AppendJoined(all, done);
        AppendJoined(all, piece);
        // pieces of different curves meet at corners: round those, and only those, the way a chain of straights is
        return d.Done.Count > 0 ? RoadCurves.Fillet(Resample5(all), 32f, 10f) : all;
    }

    private static List<Vector2> Resample5(List<Vector2> p) => RoadCurves.Resample(p, 2.5f);

    private static void AppendJoined(List<Vector2> into, List<Vector2> piece)
    {
        foreach (var p in piece) if (into.Count == 0 || Vector2.DistanceSquared(into[^1], p) > 1e-6f) into.Add(p);
    }

    /// <summary>
    /// Turns a drawn path into a plan. The first/last snaps decide what the road is: a structure's end fixes the heading and the height
    /// at that end; the end of an existing road at the start makes this road its extension (at the end, the same, drawn backwards);
    /// the same road's other end closes a loop.
    /// </summary>
    public static RoadPlan Plan(IReadOnlyList<Vector2> path, uint kind, SnapResult? start, SnapResult? end, RoadStore roads)
    {
        var centres = RoadCurves.Resample(path);
        if (centres.Count < RoadCurves.MinPoints || RoadCurves.Length(path) < 10f)
            return new RoadPlan(centres, kind, -1, false, null, null, new Dictionary<int, float>(), false, float.PositiveInfinity, "Too short: a road needs at least 10 units");
        // pin the ends to what they meet
        var pins = new Dictionary<int, float>();
        Vector2? sd = null, ed = null;
        int extend = -1; bool atStart = false, closes = false;
        if (start is { } s)
        {
            centres[0] = s.Point;
            if (s.Kind == SnapKind.Structure && s.Structure is { } st) { sd = st.Direction; pins[0] = st.Position.Y; }
            else if (s.Kind == SnapKind.RoadEnd) { extend = s.Road; atStart = s.AtStart; }
        }
        if (end is { } e)
        {
            centres[^1] = e.Point;
            if (e.Kind == SnapKind.Structure && e.Structure is { } et) { ed = -et.Direction; pins[centres.Count - 1] = et.Position.Y; }
            else if (e.Kind == SnapKind.RoadEnd)
            {
                if (extend == e.Road && extend >= 0 && e.AtStart != atStart) closes = true;
                else if (extend < 0)
                {
                    // drawn toward an existing road's end: it is that road's extension, running the other way round
                    centres.Reverse();
                    var np = new Dictionary<int, float>();
                    foreach (var (i, h) in pins) np[centres.Count - 1 - i] = h;
                    pins = np;
                    (sd, ed) = (ed is { } x ? -x : null, sd is { } y ? -y : null);
                    extend = e.Road; atStart = e.AtStart;
                }
            }
        }
        float radius = RoadCurves.MinRadius(centres);
        string? problem = radius < FoldRadius ? $"Bend too tight ({radius:0} units): the road would fold over itself" : null;
        if (extend >= 0 && roads.Count > extend)
        {
            // the join is the road's own end point: the new points start from it
            var join = roads[extend].Points[atStart ? 0 : ^1].Centre;
            centres[0] = join;
        }
        if (closes) centres[^1] = roads[extend].Points[atStart ? ^1 : 0].Centre;
        return new RoadPlan(centres, kind, extend, atStart, sd, ed, pins, closes, radius, problem);
    }

    /// <summary>The heights the plan's profile must keep: a structure's deck, and the ground an extended (or closed) road already stands on.</summary>
    public static Dictionary<int, float> AllPins(IMapDocument doc, RoadPlan plan)
    {
        var c = plan.Centres;
        var pins = new Dictionary<int, float>(plan.Pins);
        if (plan.ExtendRoad >= 0 && !pins.ContainsKey(0)) pins[0] = RoadTerrain.Ground(doc, c[0].X, c[0].Y);
        if (plan.Closes && !pins.ContainsKey(c.Count - 1)) pins[c.Count - 1] = RoadTerrain.Ground(doc, c[^1].X, c[^1].Y);
        return pins;
    }

    /// <summary>
    /// Makes the plan real: the road (new, or the extended one), and with <paramref name="conform"/> the ground cut and filled to its
    /// profile. Applies it to the store and the document; the command can be pushed on the undo stack.
    /// </summary>
    public static (RoadCommand Command, RoadTerrain.Result? Terrain)? Commit(IMapDocument doc, RoadStore roads, RoadPlan plan, bool conform,
        RoadGrade.Settings? grade = null, RoadTerrain.Settings? shoulder = null, string? name = null)
    {
        if (plan.Problem is not null || plan.Centres.Count < RoadCurves.MinPoints) return null;
        var c = plan.Centres;
        var pins = AllPins(doc, plan);
        var rec = new HeightRecorder(doc);
        RoadTerrain.Result? result = null;
        if (conform)
        {
            var h = RoadGrade.Profile(c, p => RoadTerrain.Ground(doc, p.X, p.Y), grade, pins);
            result = RoadTerrain.Conform(doc, rec, c, h, shoulder);
        }
        RoadOp op;
        if (plan.ExtendRoad >= 0)
        {
            var before = roads[plan.ExtendRoad];
            var after = RoadBuilder.Extend(before, plan.ExtendAtStart, c.Skip(1).ToList(), plan.EndDir);
            op = new RoadOp.Replace(plan.ExtendRoad, before, after);
        }
        else op = new RoadOp.Insert(roads.Count, RoadBuilder.Build(c, plan.Kind, plan.StartDir, plan.EndDir, plan.Closes));
        RoadSurface.Update(doc, rec, plan.ExtendRoad >= 0 ? roads[plan.ExtendRoad] : null, op is RoadOp.Replace rp ? rp.After : ((RoadOp.Insert)op).Road,
            plan.ExtendRoad >= 0 ? roads.Roads.Where((_, i) => i != plan.ExtendRoad) : roads.Roads);
        var cmd = new RoadCommand(name ?? (plan.ExtendRoad >= 0 ? "Extend road" : "Road"), roads, [op], rec.Tiles()).Apply();
        return (cmd, result);
    }
}
