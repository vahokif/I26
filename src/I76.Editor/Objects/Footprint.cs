using System.Numerics;

namespace I76.Editor.Objects;

/// <summary>
/// An object's footprint on the ground: its model's box in x and z, turned by its heading and put at its position (game frame).
/// The model frame has +z forward and +x to the right: <c>world = origin + x * (cos, -sin) + z * (sin, cos)</c> for a yaw (radians about +Y,
/// 0 faces +Z), the same turn <c>MapObject.Yaw</c> writes.
/// </summary>
public readonly record struct Footprint(Vector2 Origin, float Yaw, Vector2 Min, Vector2 Max)
{
    public static Footprint Of(CatalogueItem item, Vector3 position, float yaw) =>
        new(new Vector2(position.X, position.Z), yaw, new Vector2(item.MinX, item.MinZ), new Vector2(item.MaxX, item.MaxZ));

    public Vector2 AxisX => new(MathF.Cos(Yaw), -MathF.Sin(Yaw));
    public Vector2 AxisZ => new(MathF.Sin(Yaw), MathF.Cos(Yaw));
    public Vector2 LocalCentre => (Min + Max) / 2;
    public Vector2 HalfSize => (Max - Min) / 2;
    public Vector2 ToWorld(Vector2 local) => Origin + local.X * AxisX + local.Y * AxisZ;
    public Vector2 Centre => ToWorld(LocalCentre);
    public float Area => (Max.X - Min.X) * (Max.Y - Min.Y);
    /// <summary>Radius of the circle about the centre that holds the footprint.</summary>
    public float Radius => HalfSize.Length();

    /// <summary>The four corners, in order around the rectangle.</summary>
    public Vector2[] Corners() => [ToWorld(new(Min.X, Min.Y)), ToWorld(new(Max.X, Min.Y)), ToWorld(new(Max.X, Max.Y)), ToWorld(new(Min.X, Max.Y))];

    /// <summary>Whether a world point is inside, grown by <paramref name="margin"/> on every side.</summary>
    public bool Contains(Vector2 world, float margin = 0)
    {
        var d = world - Origin;
        float lx = Vector2.Dot(d, AxisX), lz = Vector2.Dot(d, AxisZ);
        return lx >= Min.X - margin && lx <= Max.X + margin && lz >= Min.Y - margin && lz <= Max.Y + margin;
    }

    /// <summary>
    /// Whether two footprints overlap, each grown by <paramref name="margin"/> / 2 (so margin is the gap that still counts):
    /// the separating-axis test on the two rectangles.
    /// </summary>
    public bool Overlaps(Footprint other, float margin = 0)
    {
        var c1 = Centre; var c2 = other.Centre;
        if (Vector2.DistanceSquared(c1, c2) > (Radius + other.Radius + margin) * (Radius + other.Radius + margin)) return false;
        var h1 = HalfSize + new Vector2(margin / 2); var h2 = other.HalfSize + new Vector2(margin / 2);
        Vector2[] axes = [AxisX, AxisZ, other.AxisX, other.AxisZ];
        var d = c2 - c1;
        foreach (var a in axes)
        {
            float r1 = h1.X * MathF.Abs(Vector2.Dot(AxisX, a)) + h1.Y * MathF.Abs(Vector2.Dot(AxisZ, a));
            float r2 = h2.X * MathF.Abs(Vector2.Dot(other.AxisX, a)) + h2.Y * MathF.Abs(Vector2.Dot(other.AxisZ, a));
            if (MathF.Abs(Vector2.Dot(d, a)) > r1 + r2) return false;
        }
        return true;
    }

    /// <summary>Distance from a world point to the rectangle (0 inside).</summary>
    public float DistanceTo(Vector2 world)
    {
        var d = world - Origin;
        float lx = Vector2.Dot(d, AxisX), lz = Vector2.Dot(d, AxisZ);
        float dx = MathF.Max(MathF.Max(Min.X - lx, lx - Max.X), 0), dz = MathF.Max(MathF.Max(Min.Y - lz, lz - Max.Y), 0);
        return MathF.Sqrt(dx * dx + dz * dz);
    }
}

/// <summary>Why a spot is not good for an object (shown as the ghost turning red and a line in the status bar).</summary>
public enum PlaceProblem { None, OverlapsObject, TooSteep, OnRoad, OffMap }

/// <summary>The placement rules the ghost is checked against.</summary>
public static class PlaceRules
{
    /// <summary>Overlaps with things smaller than this (m², signs, poles, cacti) do not count: they stand by buildings and roads.</summary>
    public const float MinSolidArea = 1.5f;
    /// <summary>The most the ground may differ from the height at the origin anywhere over the footprint (the game stands the origin on the ground and leaves the rest to float or sink), by category.</summary>
    public static float MaxRelief(ObjectCategory c) => c switch
    {
        ObjectCategory.Buildings => 2.0f,
        ObjectCategory.Structures => 4.0f,
        ObjectCategory.Props => 3.0f,
        ObjectCategory.Fences => 3.0f,
        ObjectCategory.Markers => 6.0f,
        _ => float.MaxValue,
    };

    // a tunnel is not checked for relief: it is placed on ground levelled under the whole mountain (the stock maps level it)

    /// <summary>Whether this kind of object keeps off the road surface (buildings, props); roadside things and structures that carry roads do not.</summary>
    public static bool AvoidsRoads(ObjectCategory c) => c is ObjectCategory.Buildings or ObjectCategory.Props;

    /// <summary>Overlap tolerance (m): a little overlap of the boxes (eaves, yards) is allowed so that buildings can stand close.</summary>
    public const float Margin = -0.5f;

    /// <summary>
    /// The largest difference between the ground at the footprint's corners, edge middles and centre and at its origin
    /// (the height the game stands the object on), in metres. <paramref name="ground"/> gives the height at x, z.
    /// </summary>
    public static float Relief(Footprint f, Func<float, float, float> ground)
    {
        float h0 = ground(f.Origin.X, f.Origin.Y), worst = 0;
        var c = f.Corners();
        foreach (var p in c) worst = MathF.Max(worst, MathF.Abs(ground(p.X, p.Y) - h0));
        for (int i = 0; i < 4; i++) { var m = (c[i] + c[(i + 1) % 4]) / 2; worst = MathF.Max(worst, MathF.Abs(ground(m.X, m.Y) - h0)); }
        var ctr = f.Centre; worst = MathF.Max(worst, MathF.Abs(ground(ctr.X, ctr.Y) - h0));
        return worst;
    }

    /// <summary>The first problem with putting <paramref name="item"/> at a pose, or None.</summary>
    public static PlaceProblem Check(CatalogueItem item, Footprint f, IEnumerable<Footprint> others, Func<float, float, float> ground, Func<float, float, bool> onMap, IReadOnlyList<RoadPolyline>? roads = null)
    {
        foreach (var p in f.Corners().Append(f.Centre))
            if (!onMap(p.X, p.Y)) return PlaceProblem.OffMap;
        if (item.Marker == MarkerKind.None || item.Marker == MarkerKind.Flag)
        {
            if (f.Area >= MinSolidArea)
                foreach (var o in others)
                    if (o.Area >= MinSolidArea && f.Overlaps(o, Margin)) return PlaceProblem.OverlapsObject;
            if (item.Category is not (ObjectCategory.Roadside or ObjectCategory.Vegetation) && !item.IsTunnel && Relief(f, ground) > MaxRelief(item.Category)) return PlaceProblem.TooSteep;
            if (roads is not null && AvoidsRoads(item.Category) && f.Area >= MinSolidArea && RoadGeometry.FootprintOnRoad(f, roads)) return PlaceProblem.OnRoad;
        }
        return PlaceProblem.None;
    }
}
