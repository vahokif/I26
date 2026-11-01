using I76.Formats.Assembly;
using System.Numerics;

namespace I76.Editor.Objects;

/// <summary>How a placement snaps: not at all, to a grid, or (also to the grid away from roads) to roads like Cities: Skylines.</summary>
public enum SnapMode { Off, Grid, Roads }

/// <summary>Where a placement ends up: the origin (game x, z), the heading, and how it got there.</summary>
public readonly record struct PlacementSnap(Vector2 Origin, float Yaw, bool OnRoad, RoadHit? Hit, Deck? Deck)
{
    /// <summary>A structure that was put at the open end of a road, not on its length (nothing to cut).</summary>
    public bool OnRoadEnd { get; init; }
    public Footprint Footprint(CatalogueItem item) => Objects.Footprint.Of(item, new Vector3(Origin.X, 0, Origin.Y), Yaw);
}

/// <summary>
/// Placement snapping (pure maths). The cursor puts the object's footprint centre (its origin for a marker); a road within
/// <see cref="Range"/> pulls it to the road's side (buildings, props, roadside things: front toward the road, set back from its edge,
/// as the stock maps stand them, the model's +z facing the road) or onto the road (bridges, ramps: along it, centred on it).
/// </summary>
public static class Snapping
{
    public const float Range = 55f;
    public static readonly float AngleStep = MathF.PI / 12;      // 15 degrees

    public static float Setback(ObjectCategory c) => c switch
    {
        ObjectCategory.Buildings => 6f,
        ObjectCategory.Props => 4f,
        ObjectCategory.Roadside => 1.5f,
        ObjectCategory.Fences => 1f,
        _ => 0f,
    };

    public static float Wrap(float a)
    {
        a %= MathF.Tau;
        if (a > MathF.PI) a -= MathF.Tau;
        if (a <= -MathF.PI) a += MathF.Tau;
        return a;
    }

    public static float SnapAngle(float yaw, float step) => Wrap(MathF.Round(yaw / step) * step);

    public static Vector2 SnapGrid(Vector2 p, float grid) => grid <= 0 ? p : new(MathF.Round(p.X / grid) * grid, MathF.Round(p.Y / grid) * grid);

    /// <summary>The yaw whose forward (sin, cos) points along <paramref name="dir"/>.</summary>
    public static float YawOf(Vector2 dir) => MathF.Atan2(dir.X, dir.Y);

    /// <summary>The origin that puts a footprint's centre at <paramref name="centre"/> for a heading.</summary>
    public static Vector2 OriginFor(CatalogueItem item, Vector2 centre, float yaw)
    {
        var f = Footprint.Of(item, Vector3.Zero, yaw);
        return centre - (f.Centre - f.Origin);
    }

    /// <summary>
    /// The pose for an item under the cursor. <paramref name="yaw"/> is the heading the user has turned it to; <paramref name="roads"/> is used
    /// only in <see cref="SnapMode.Roads"/>. A marker has no box: its origin follows the cursor.
    /// </summary>
    public static PlacementSnap Place(CatalogueItem item, Vector2 cursor, float yaw, SnapMode mode, float grid, IReadOnlyList<RoadPolyline>? roads)
    {
        if (mode == SnapMode.Roads && roads is { Count: > 0 } && item.Marker == MarkerKind.None && item.Area > 0 && ToRoad(item, cursor, yaw, roads) is { } hit) return hit;
        if (mode != SnapMode.Off) cursor = SnapGrid(cursor, grid);
        if (item.Marker is MarkerKind.Spawn or MarkerKind.Regen or MarkerKind.Checkpoint || item.Area <= 0) return new(cursor, yaw, false, null, null);
        return new(OriginFor(item, cursor, yaw), yaw, false, null, null);
    }

    private static PlacementSnap? ToRoad(CatalogueItem item, Vector2 cursor, float yaw, IReadOnlyList<RoadPolyline> roads)
    {
        if (item.CarriesRoad) return OntoRoad(item, cursor, yaw, roads);
        if (RoadGeometry.Nearest(roads, cursor, Range) is not { } hit) return null;
        if (item.Category is ObjectCategory.Vegetation or ObjectCategory.Scripted) return null;
        // beside the road, on the side the cursor is on, the front (+z) toward the road
        var n = hit.Normal;
        float side = Vector2.Dot(cursor - hit.Point, n) >= 0 ? 1 : -1;
        var toRoad = -side * n;
        float y = YawOf(toRoad);
        var shape = Footprint.Of(item, Vector3.Zero, y);
        float depthHalf = shape.HalfSize.Y;
        var centre = hit.Point + side * n * (hit.HalfWidth + Setback(item.Category) + depthHalf);
        return new PlacementSnap(OriginFor(item, centre, y), y, true, hit, null);
    }

    /// <summary>How far from an open road end the cursor still takes it (m).</summary>
    public const float EndRange = 22f;

    /// <summary>The world position and outward direction of a structure's end for a pose (the model's frame turned by the yaw).</summary>
    public static (Vector2 At, Vector2 Out) EndAt(StructureAttach.Attach e, Vector2 origin, float yaw)
    {
        float c = MathF.Cos(yaw), s = MathF.Sin(yaw);
        Vector2 W(Vector2 l) => new(l.X * c + l.Y * s, -l.X * s + l.Y * c);
        return (origin + W(new Vector2(e.Position.X, e.Position.Z)), W(e.Direction));
    }

    /// <summary>
    /// A bridge, ramp or tunnel put on a road. A tunnel is laid over the road like a bridge (its tube's middle on the road point, along the road,
    /// at any heading) but the road is not cut: it runs through, as it does in the stock maps (B01, P18). The stock maps lay every bridge as a level deck whose two ends are road ends, and a jump
    /// ramp's foot is a road end (<see cref="StructureAttach"/>), so: onto a road, along it, the deck's middle on the road (the roads
    /// are cut at its ends: <see cref="RoadSplit"/>); at an open road end, the near end of the deck on that end, the deck going on from
    /// it. Bridges stay on the axes (the engine's bridge search wants that). The heading the user has turned to picks which way along
    /// the road the ramp's foot faces.
    /// </summary>
    private static PlacementSnap? OntoRoad(CatalogueItem item, Vector2 cursor, float userYaw, IReadOnlyList<RoadPolyline> roads)
    {
        var ends = item.DeckEnds();
        var a0 = new Vector2(ends[0].Position.X, ends[0].Position.Z); var a1 = new Vector2(ends[1].Position.X, ends[1].Position.Z);
        if ((a1 - a0).LengthSquared() < 1e-4f) return null;
        var axis = Vector2.Normalize(a1 - a0);
        float phiA = YawOf(axis);
        var mid = (a0 + a1) / 2;
        // the road end first: an open end within reach, the deck going on from it (a tunnel is laid over the road instead: it runs through)
        if (!item.IsTunnel && RoadGeometry.NearestEnd(roads, cursor, EndRange) is { } open)
        {
            // the road leaves its end along -Out; the deck extends the other way, along Out
            float yaw = open.Out.LengthSquared() > 0 ? YawOf(open.Out) - phiA : userYaw;
            if (item.AxisAligned) { float ax = SnapAngle(YawOf(open.Out), MathF.PI / 2); if (MathF.Abs(Wrap(ax - YawOf(open.Out))) > 25 * MathF.PI / 180) return null; yaw = ax - phiA; }
            // the end whose outward direction faces back along the road meets it
            int meet = 0; float best = float.MaxValue;
            for (int i = 0; i < 2; i++)
            {
                var (_, outDir) = EndAt(ends[i], default, yaw);
                float d = Vector2.Dot(outDir, open.Out);
                if (d < best) { best = d; meet = i; }
            }
            var origin0 = open.Point - EndAt(ends[meet], default, yaw).At;
            return Finish(item, ends, origin0, Wrap(yaw), open.Hit, onEnd: true);
        }
        if (RoadGeometry.Nearest(roads, cursor, Range) is not { } hit) return null;
        var t = hit.Tangent;
        // the direction (either way along the road) nearest the heading the user has turned to
        float c0 = MathF.Cos(userYaw), s0 = MathF.Sin(userYaw);
        var userAxis = new Vector2(axis.X * c0 + axis.Y * s0, -axis.X * s0 + axis.Y * c0);
        var fwd = Vector2.Dot(userAxis, t) >= 0 ? t : -t;
        float phiF = YawOf(fwd);
        if (item.AxisAligned)
        {
            float snapped = SnapAngle(phiF, MathF.PI / 2);
            if (MathF.Abs(Wrap(snapped - phiF)) > 25 * MathF.PI / 180) return null;   // a road at a slant: the bridge stands free, on the axes
            phiF = snapped;
        }
        float y2 = Wrap(phiF - phiA);
        // the deck's middle at the road point
        float c = MathF.Cos(y2), s = MathF.Sin(y2);
        var origin = hit.Point - new Vector2(mid.X * c + mid.Y * s, -mid.X * s + mid.Y * c);
        return Finish(item, ends, origin, y2, hit, onEnd: false);
    }

    private static PlacementSnap Finish(CatalogueItem item, IReadOnlyList<StructureAttach.Attach> ends, Vector2 origin, float yaw, RoadHit? hit, bool onEnd)
    {
        var (p0, _) = EndAt(ends[0], origin, yaw); var (p1, _) = EndAt(ends[1], origin, yaw);
        var deck = new Deck(p0, p1, MathF.Max(ends[0].Width, ends[1].Width));
        return new PlacementSnap(origin, yaw, true, hit, onEnd ? null : deck) { OnRoadEnd = onEnd };
    }
}
