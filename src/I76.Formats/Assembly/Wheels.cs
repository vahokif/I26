using System.Numerics;

namespace I76.Formats.Assembly;

/// <summary>A synthesised polygon: model-space corners, UVs and normals, wound like a GEO face.</summary>
public sealed record SynthFace(Vector3[] Corners, Vector2[] Uvs, Vector3[] Normals);

/// <summary>
/// Wheels. The archive has no wheel geometry: the game draws textured discs
/// (the WA&lt;dd&gt;&lt;ll&gt;&lt;dd&gt; family, a disc inscribed in a 64×64 square).
/// A port of the reference assembler's wheel builder: a short textured cylinder at each
/// used WLOC slot, radius from the enclosing wheel well's z-span (clamped to
/// the arch's height), pushed outboard from WLOC's centre plane. The tyre's
/// look is garage state, not in the .vdf, so the texture is a choice.
///
/// Faces come out in the same winding as GEO faces (counter-clockwise from
/// outside once mirrored) with outward normals, so MeshBuilder treats them
/// like any other part; the reference assembler emits them already OBJ-wound and un-negated,
/// and the reversal here undoes exactly that.
/// </summary>
public static class Wheels
{
    public const string DefaultTexture = "wa11ls06";

    /// <summary>The sides of the modern look's round wheel, in place of the original's handful.</summary>
    public const int SmoothSegments = 48;

    /// <summary>
    /// The wheel cylinders. <paramref name="onlySlot"/> builds one WLOC slot's
    /// wheel; <paramref name="centred"/> moves it to its own centre (for a
    /// wheel that spins and steers as its own node).
    /// </summary>
    public static List<SynthFace> Build(AssembledModel m, float outFrac = 0.31f, float width = 0.16f, int segments = 16,
        int? onlySlot = null, bool centred = false)
    {
        var wells = new List<(float Z0, float Z1, float Y0, float Y1)>();
        foreach (var p in m.Parts)
        {
            var n = p.NodeName.ToUpperInvariant();
            if (!(n.EndsWith("FWLL") || n.EndsWith("BWLL")) || p.Mesh.Vertices.Length == 0) continue;
            var vs = p.Mesh.Vertices.Select(p.ToModel.Apply).ToList();
            wells.Add((vs.Min(v => v.Z), vs.Max(v => v.Z), vs.Min(v => v.Y), vs.Max(v => v.Y)));
        }
        var o = new List<SynthFace>();
        foreach (var loc in m.Locations.Where(l => l.Tag == "WLOC"))
        {
            if (onlySlot is { } only && loc.Index != only) continue;
            float cx = loc.Transform.Translation[0], cy = loc.Transform.Translation[1], cz = loc.Transform.Translation[2];
            if (MathF.Abs(cx) < 1e-6 && MathF.Abs(cy) < 1e-6 && MathF.Abs(cz) < 1e-6) continue;
            var near = wells.Where(w => w.Z0 <= cz && cz <= w.Z1).ToList();
            float r = near.Count > 0 ? (near[0].Z1 - near[0].Z0) / 2 : 0.36f;
            if (near.Count > 0) r = MathF.Min(r, MathF.Max(near[0].Y1 - cy, 0.05f));
            float x = cx + (cx >= 0 ? outFrac * r : -outFrac * r);
            float half = width * r;
            var ring = new (float Y, float Z)[segments];
            var ringUv = new Vector2[segments];
            var treadUv = new Vector2[segments];
            var ringN = new Vector3[segments];
            for (int k = 0; k < segments; k++)
            {
                float a = 2 * MathF.PI * k / segments, ca = MathF.Cos(a), sa = MathF.Sin(a);
                ring[k] = (cy + r * sa, cz + r * ca);
                ringUv[k] = new Vector2(0.5f + 0.5f * ca, 0.5f + 0.5f * sa);
                treadUv[k] = new Vector2(0.5f + 0.47f * ca, 0.5f + 0.47f * sa);
                ringN[k] = new Vector3(0, sa, ca);
            }
            float axis = cx >= 0 ? 1 : -1;
            float outer = x, inner = x - (cx >= 0 ? 2 * half : -2 * half);
            foreach (var (faceX, flip, nx) in new[] { (outer, cx < 0, axis), (inner, cx >= 0, -axis) })
            {
                var pts = ring.Select(p => new Vector3(faceX, p.Y, p.Z)).ToArray();
                var uvs = ringUv.ToArray();
                if (flip) { Array.Reverse(pts); Array.Reverse(uvs); }
                o.Add(ToGeoWinding(pts, uvs, Enumerable.Repeat(new Vector3(nx, 0, 0), segments).ToArray()));
            }
            for (int k = 0; k < segments; k++)
            {
                int k2 = (k + 1) % segments;
                var quad = new[] { new Vector3(outer, ring[k].Y, ring[k].Z), new Vector3(outer, ring[k2].Y, ring[k2].Z),
                                   new Vector3(inner, ring[k2].Y, ring[k2].Z), new Vector3(inner, ring[k].Y, ring[k].Z) };
                var quv = new[] { treadUv[k], treadUv[k2], treadUv[k2], treadUv[k] };
                var qn = new[] { ringN[k], ringN[k2], ringN[k2], ringN[k] };
                if (cx < 0) { Array.Reverse(quad); Array.Reverse(quv); Array.Reverse(qn); }
                o.Add(ToGeoWinding(quad, quv, qn));
            }
            if (centred)
            {
                var c = new Vector3(cx, cy, cz);
                for (int i = o.Count - 2 - segments; i < o.Count; i++)
                    o[i] = o[i] with { Corners = o[i].Corners.Select(p => p - c).ToArray() };
            }
        }
        return o;
    }

    /// <summary>the reference assembler's faces are pre-mirror OBJ order (reversed GEO) with outward normals.</summary>
    private static SynthFace ToGeoWinding(Vector3[] p, Vector2[] uv, Vector3[] n)
    {
        Array.Reverse(p); Array.Reverse(uv); Array.Reverse(n);
        return new SynthFace(p, uv, n);
    }

    /// <summary>
    /// Whether a .wdf mesh is a round wheel about its hub, which the port may turn about the axle in place of the
    /// engine's hubcap frames (the engine turns no wheel mesh). In wheel space (the WGEO
    /// node applied) its y and z spans are centred on the hub and it is about as long as it is tall. A tank's track
    /// (wetnk_1a.wdf: 7 m long, its middle 2.9 m ahead of the WLOC) is not.
    /// </summary>
    public static bool RollsAboutHub(Geo.GeoMesh mesh, Affine node)
    {
        if (mesh.Vertices.Length == 0) return true;
        float y0 = float.MaxValue, y1 = float.MinValue, z0 = float.MaxValue, z1 = float.MinValue;
        foreach (var v in mesh.Vertices)
        {
            var p = node.Apply(v);
            y0 = MathF.Min(y0, p.Y); y1 = MathF.Max(y1, p.Y);
            z0 = MathF.Min(z0, p.Z); z1 = MathF.Max(z1, p.Z);
        }
        float h = (y1 - y0) / 2, l = (z1 - z0) / 2, tol = 0.25f * h + 0.02f;
        return l <= 1.5f * h + 0.02f && MathF.Abs((y0 + y1) / 2) <= tol && MathF.Abs((z0 + z1) / 2) <= tol;
    }
}
