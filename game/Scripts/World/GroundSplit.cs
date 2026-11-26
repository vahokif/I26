using System.Numerics;

namespace I76.Game.World;

/// <summary>
/// A painted face (a grounded placement's ground face, a flat road piece's top: <see cref="GroundDecals"/>) against the ground
/// drawn under it. The loader stands a placement on the ground at its origin and keeps its rotation, so a flat piece on a slope
/// is a level plane at the ground's height there: over the downhill side it floats, on the uphill side it is under the ground.
/// The original drew it whole and level over the terrain (no depth buffer); its collider and a ramp's drive-on deck are that
/// level plane. Painted into the terrain it followed the slope instead.
/// <para>So the face is cut along the drawn ground: where it stands more than <see cref="Near"/> above the ground it is drawn
/// as a mesh, level, where the original draws it and the cars meet it; where it lies within <see cref="Lift"/> of the ground,
/// or under it, it is painted, as before (a level mesh there would fight the terrain for depth, or be hidden by it). The two
/// overlap between the heights, so no bare ground shows under the mesh's edge. A face that nowhere stands more than
/// <see cref="Near"/> above the ground is painted whole, as it always was: on the stock maps that is all but about 1% of the
/// painted area (the corners of lots over dips).</para>
/// </summary>
public static class GroundSplit
{
    /// <summary>A face standing more than this above the ground (metres) is drawn there as a mesh.</summary>
    public const float Near = 0.25f;
    /// <summary>A face is painted where it stands no more than this above the ground (metres), or under it.</summary>
    public const float Lift = 0.5f;

    /// <summary>A corner: its position (game frame) and texture coordinates.</summary>
    public readonly record struct Corner(Vector3 P, Vector2 Uv)
    {
        public static Corner Lerp(Corner a, Corner b, double t) =>
            new(a.P + (b.P - a.P) * (float)t, a.Uv + (b.Uv - a.Uv) * (float)t);
    }

    /// <summary>
    /// One triangle of a painted face (game frame) cut against the ground: <paramref name="height"/> gives the ground's height in
    /// metres at a sample (sample k at 5k metres), drawn as the 1997 look draws it: each 5 m cell cut along the diagonal whose ends
    /// are nearer in height (TerrainTriangles), on which the modern look's ground under a placement stays within centimetres.
    /// Returns the polygons to paint (the triangle itself when nothing of it stands above <see cref="Lift"/>) and the polygons
    /// to draw as a mesh (none when nothing of it stands above <see cref="Near"/>).
    /// </summary>
    public static (List<Corner[]> Painted, List<Corner[]> Raised) Split(Corner a, Corner b, Corner c, Func<int, int, float> height,
        float near = Near, float lift = Lift)
    {
        var painted = new List<Corner[]>();
        var raised = new List<Corner[]>();
        var low = new List<Corner[]>();
        bool high = false;
        float minX = MathF.Min(a.P.X, MathF.Min(b.P.X, c.P.X)), maxX = MathF.Max(a.P.X, MathF.Max(b.P.X, c.P.X));
        float minZ = MathF.Min(a.P.Z, MathF.Min(b.P.Z, c.P.Z)), maxZ = MathF.Max(a.P.Z, MathF.Max(b.P.Z, c.P.Z));
        int sx0 = (int)MathF.Floor(minX / 5), sx1 = (int)MathF.Floor(maxX / 5), sz0 = (int)MathF.Floor(minZ / 5), sz1 = (int)MathF.Floor(maxZ / 5);
        Corner[] face = [a, b, c];
        for (int sz = sz0; sz <= sz1; sz++)
            for (int sx = sx0; sx <= sx1; sx++)
            {
                var g00 = new Vector3(sx * 5f, height(sx, sz), sz * 5f);
                var g10 = new Vector3(sx * 5f + 5, height(sx + 1, sz), sz * 5f);
                var g01 = new Vector3(sx * 5f, height(sx, sz + 1), sz * 5f + 5);
                var g11 = new Vector3(sx * 5f + 5, height(sx + 1, sz + 1), sz * 5f + 5);
                var tris = MathF.Abs(g00.Y - g11.Y) < MathF.Abs(g10.Y - g01.Y)
                    ? new[] { (g00, g01, g11), (g00, g11, g10) }
                    : new[] { (g00, g01, g10), (g10, g01, g11) };
                foreach (var (t0, t1, t2) in tris)
                {
                    var piece = ClipToTriangle(face, t0, t1, t2);
                    if (piece.Length < 3) continue;
                    // the face's height over this ground triangle's plane: linear, so the cuts are straight
                    var n = Vector3.Cross(t1 - t0, t2 - t0);
                    if (MathF.Abs(n.Y) < 1e-9f) continue;
                    double Over(Corner q) => q.P.Y - (t0.Y - ((double)n.X * (q.P.X - t0.X) + (double)n.Z * (q.P.Z - t0.Z)) / n.Y);
                    var up = Clip(piece, q => Over(q) - near);
                    if (up.Length >= 3) raised.Add(up);
                    var down = Clip(piece, q => lift - Over(q));
                    if (down.Length >= 3) low.Add(down);
                    if (piece.Any(q => Over(q) > lift + 1e-4)) high = true;
                }
            }
        if (high) painted.AddRange(low);
        else painted.Add(face);
        return (painted, raised);
    }

    /// <summary>
    /// The ground's height at a sample (metres) as the placements stand on it (MissionWorld.GroundedPosition): the raw samples, the
    /// outside ground's past the tiles.
    /// </summary>
    public static Func<int, int, float> Heights(I76.Formats.Assembly.MissionWorld w) => (sx, sz) =>
        (float)((w.Terrain.Sample(sx, sz) is { } v ? v & I76.Formats.Mission.MissionFile.HeightMask : w.Outside?.Raw(sx, sz) ?? 0) * I76.Formats.Assembly.MissionWorld.HeightScale);

    /// <summary>
    /// A placement's faces drawn level as a mesh: one part per part of its model that has any, in the model's frame (the
    /// placement's transform undone), each face its original's texture, colour and flags with the cut corners.
    /// </summary>
    public static I76.Formats.Assembly.AssembledModel Model(I76.Formats.Assembly.Placement p, I76.Formats.Assembly.MissionWorld world, IEnumerable<RaisedFace> faces)
    {
        var list = faces.ToList();
        var src = list[0].Model;
        var m = new I76.Formats.Assembly.AssembledModel { Definition = src.Definition, Kind = src.Kind, ObjectCode = src.ObjectCode };
        m.TmtNames.AddRange(src.TmtNames);
        m.VtfMaps.AddRange(src.VtfMaps);
        var place = I76.Formats.Assembly.Affine.FromEngine(p.Object.Rotation, world.GroundedPosition(p));
        // the inverse of a rotation is its transpose
        var r = place.R;
        Vector3 Local(Vector3 v)
        {
            var d = v - place.T;
            var back = new I76.Formats.Assembly.Affine([r[0], r[3], r[6], r[1], r[4], r[7], r[2], r[5], r[8]], Vector3.Zero);
            return back.Rotate(d);
        }
        foreach (var byPart in list.GroupBy(f => f.Part.NodeName, StringComparer.OrdinalIgnoreCase))
        {
            var verts = new List<Vector3>();
            var geoFaces = new List<I76.Formats.Geo.GeoFace>();
            foreach (var rf in byPart)
            {
                var n = rf.Part.ToModel.Rotate(new Vector3(rf.Face.Plane.X, rf.Face.Plane.Y, rf.Face.Plane.Z));
                var fv = new I76.Formats.Geo.FaceVertex[rf.Corners.Length];
                for (int i = 0; i < rf.Corners.Length; i++)
                {
                    fv[i] = new I76.Formats.Geo.FaceVertex(verts.Count, -1, rf.Corners[i].Uv.X, rf.Corners[i].Uv.Y);
                    verts.Add(Local(rf.Corners[i].P));
                }
                var f = rf.Face;
                geoFaces.Add(new I76.Formats.Geo.GeoFace
                {
                    Unk_0x00 = f.Unk_0x00, VertexCount = (uint)fv.Length, R = f.R, G = f.G, B = f.B, Plane = new Vector4(n, f.Plane.W),
                    Unk_0x1B = f.Unk_0x1B, Unk_0x1F = f.Unk_0x1F, Unk_0x20 = f.Unk_0x20, Unk_0x21 = f.Unk_0x21, Texture = f.Texture,
                    Unk_0x32 = f.Unk_0x32, Vertices = fv,
                });
            }
            var first = byPart.First().Part;
            m.Parts.Add(new I76.Formats.Assembly.ModelPart(first.NodeName,
                new I76.Formats.Geo.GeoMesh { Name = first.Mesh.Name, Vertices = [.. verts], Faces = [.. geoFaces] }, I76.Formats.Assembly.Affine.Identity));
        }
        return m;
    }

    /// <summary>The part of a convex polygon where <paramref name="keep"/> (linear over it) is positive or zero.</summary>
    private static Corner[] Clip(Corner[] poly, Func<Corner, double> keep)
    {
        var o = new List<Corner>(poly.Length + 2);
        for (int i = 0; i < poly.Length; i++)
        {
            Corner p = poly[i], q = poly[(i + 1) % poly.Length];
            double kp = keep(p), kq = keep(q);
            if (kp >= 0) o.Add(p);
            if (kp >= 0 != kq >= 0) o.Add(Corner.Lerp(p, q, kp / (kp - kq)));
        }
        return Dedup(o);
    }

    /// <summary>The part of a convex polygon over a triangle, in XZ.</summary>
    private static Corner[] ClipToTriangle(Corner[] poly, Vector3 t0, Vector3 t1, Vector3 t2)
    {
        // inside is the side of each edge the third corner is on
        double s = Math.Sign(Cross(t0, t1, t2));
        if (s == 0) return [];
        foreach (var (e0, e1) in new[] { (t0, t1), (t1, t2), (t2, t0) })
        {
            poly = Clip(poly, q => s * Cross(e0, e1, q.P));
            if (poly.Length < 3) return [];
        }
        return poly;
    }

    internal static double Cross(Vector3 a, Vector3 b, Vector3 p) => ((double)b.X - a.X) * ((double)p.Z - a.Z) - ((double)b.Z - a.Z) * ((double)p.X - a.X);

    /// <summary>Drops corners repeated by a cut through a corner, and slivers of no area.</summary>
    private static Corner[] Dedup(List<Corner> o)
    {
        for (int i = o.Count - 1; i >= 0 && o.Count > 0; i--)
            if (Vector3.DistanceSquared(o[i].P, o[(i + 1) % o.Count].P) < 1e-8f) o.RemoveAt(i);
        if (o.Count < 3) return [];
        double area = 0;
        for (int i = 1; i + 1 < o.Count; i++) area += Cross(o[0].P, o[i].P, o[i + 1].P);
        return Math.Abs(area) < 1e-6 ? [] : [.. o];
    }
}

/// <summary>A piece of a painted face that stands clear of the ground (GroundSplit): drawn level, as a mesh, with its placement's part.</summary>
public sealed record RaisedFace(I76.Formats.Assembly.Placement P, I76.Formats.Assembly.AssembledModel Model, I76.Formats.Assembly.ModelPart Part,
    I76.Formats.Geo.GeoFace Face, GroundSplit.Corner[] Corners);
