using System.Numerics;

namespace I76.Formats.Assembly;

/// <summary>
/// Where a road meets a structure that carries it (a bridge's deck, a jump or a ramp, an overpass): the ends of the structure's
/// drive-on surface. Shared by the road tool (snap a road's end to it) and the object tool (place a structure onto a road's end).
/// No Godot, no sim: it works on the structure's upward polygons in world space (the sim's <c>StaticObject.Surfaces</c>, each
/// <c>Poly.V</c>), so the same code serves a placed object and a candidate before it is placed.
/// </summary>
public static class StructureAttach
{
    /// <summary>A deck is level when its centre line rises less than this over its length (bebridg1/2 are 0.00; the jump ramps 0.28).</summary>
    public const float FlatSlope = 0.08f;
    /// <summary>Surfaces shorter or narrower than this are not roads' business (the stock type-0xb pads, walkways and towers are 5 wide or less; the roads are 10, the jump ramps' feet 10).</summary>
    public const float MinLength = 8f, MinWidth = 9f;

    /// <summary>
    /// One end of a drive-on surface. <see cref="Position"/> is the middle of the end edge at the surface's height there;
    /// <see cref="Direction"/> is the horizontal unit vector pointing out of the structure along its axis (a road meeting this end
    /// runs along <c>-Direction</c> into it); <see cref="Width"/> the edge's extent across; <see cref="Low"/> whether this is the
    /// lower end of the two (a ramp's foot).
    /// </summary>
    public readonly record struct Attach(Vector3 Position, Vector2 Direction, float Width, bool Low)
    {
        /// <summary>The slope of the surface's centre line from this end to the other, rise over run (positive going up into the structure).</summary>
        public float Slope { get; init; }
        /// <summary>What it belongs to when it is not a structure (an intersection piece's free arm), for the road tool's hint.</summary>
        public string? Source { get; init; }
        /// <summary>Whether a road can meet this end: the foot of a ramp, or either end of a deck laid level with the ground (every stock bridge). The raised lip of a ramp is a drop.</summary>
        public bool RoadLevel => Low || Math.Abs(Slope) < FlatSlope;
    }

    /// <summary>The ends of the surface made of <paramref name="polygons"/> (each a convex polygon, world space, game frame): two for a deck or ramp, none if there is nothing to drive on.</summary>
    public static IReadOnlyList<Attach> Ends(IEnumerable<Vector3[]> polygons)
    {
        var verts = polygons.SelectMany(p => p).ToArray();
        if (verts.Length < 3) return [];
        // the long axis in the ground plane: the principal axis of the vertices' XZ scatter
        double mx = verts.Average(v => (double)v.X), mz = verts.Average(v => (double)v.Z);
        double sxx = 0, szz = 0, sxz = 0;
        foreach (var v in verts) { double dx = v.X - mx, dz = v.Z - mz; sxx += dx * dx; szz += dz * dz; sxz += dx * dz; }
        double ang = 0.5 * Math.Atan2(2 * sxz, sxx - szz);
        // a ramp that is wider than it is long (the 2 m wood ramp: 20 across, 15 up) has its principal axis across the slope: the way it
        // runs is the way it climbs, so a clearly tilted surface takes the horizontal gradient of its best-fit plane instead
        double my = verts.Average(v => (double)v.Y), sxy = 0, szy = 0;
        foreach (var v in verts) { double dx = v.X - mx, dz = v.Z - mz, dy = v.Y - my; sxy += dx * dy; szy += dz * dy; }
        double det = sxx * szz - sxz * sxz;
        if (Math.Abs(det) > 1e-9)
        {
            double gx = (sxy * szz - szy * sxz) / det, gz = (szy * sxx - sxy * sxz) / det;
            if (Math.Sqrt(gx * gx + gz * gz) >= FlatSlope) ang = Math.Atan2(gz, gx);
        }
        var axis = new Vector2((float)Math.Cos(ang), (float)Math.Sin(ang));
        // a structure that is square-ish has no principal axis; snap near-axis-aligned ones to the world axis they lie on
        if (Math.Abs(axis.X) > 0.9995f) axis = new Vector2(Math.Sign(axis.X), 0);
        else if (Math.Abs(axis.Y) > 0.9995f) axis = new Vector2(0, Math.Sign(axis.Y));
        var side = new Vector2(-axis.Y, axis.X);
        float T(Vector3 v) => v.X * axis.X + v.Z * axis.Y;
        float S(Vector3 v) => v.X * side.X + v.Z * side.Y;
        float t0 = verts.Min(T), t1 = verts.Max(T);
        if (t1 - t0 < MinLength) return [];

        Attach End(float t, bool high)
        {
            // the vertices on the end edge: within a metre (or a tenth of the length) of the extreme
            float tol = Math.Min(1f, (t1 - t0) * 0.1f);
            var edge = verts.Where(v => high ? T(v) >= t - tol : T(v) <= t + tol).ToArray();
            float s0 = edge.Min(S), s1 = edge.Max(S);
            float sc = (s0 + s1) / 2;
            // the surface's height at the end edge's middle: the highest of the end vertices (the deck's top face, not a skirt below it)
            float y = edge.Max(v => v.Y);
            var xz = axis * t + side * sc;
            return new Attach(new Vector3(xz.X, y, xz.Y), high ? axis : -axis, s1 - s0, false);
        }
        var a = End(t0, false);
        var b = End(t1, true);
        if (Math.Min(a.Width, b.Width) < MinWidth) return [];
        float run = t1 - t0;
        float slope = (b.Position.Y - a.Position.Y) / run;
        bool aLow = a.Position.Y <= b.Position.Y;
        return [a with { Low = aLow, Slope = slope }, b with { Low = !aLow, Slope = -slope }];
    }
}
