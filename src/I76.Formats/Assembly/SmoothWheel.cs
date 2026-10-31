using System.Numerics;
using I76.Formats.Geo;
using I76.Formats.Imaging;

namespace I76.Formats.Assembly;

/// <summary>
/// A round wheel for the modern look, fitted to its texture: the rim of the tyre's outline at the outer face (wheel
/// space, x along the axle) with its texture coordinates, the flat colour of the tread, and where the wheel's two
/// ends lie along the axle.
/// </summary>
/// <param name="Rim">The outline's points on the outer face (x = <see cref="OuterX"/>).</param>
/// <param name="RimUv">The texture coordinates of each rim point: they lie on the texture's own tyre edge.</param>
/// <param name="RimNormals">Each rim point's outward direction in the face's plane (the tread's normal).</param>
/// <param name="Centre">The outer face's centre (y, z in wheel space) and its texture coordinates.</param>
/// <param name="Circle">The outline is a circle (an intact tyre) of <paramref name="Radius"/>; otherwise it follows the texture's shape (a blown tyre's).</param>
public sealed record SmoothWheel(Vector3[] Rim, Vector2[] RimUv, Vector3[] RimNormals, Vector3 Centre, Vector2 CentreUv,
    float OuterX, float InnerX, byte R, byte G, byte B, bool Circle, float Radius)
{
    /// <summary>How far in from the tyre's last opaque texel the outline lies, in texels, so that a filtered lookup does not reach the colour key.</summary>
    private const float Inset = 0.4f;

    /// <summary>
    /// A blown tyre's rim takes its texture this far in from the last opaque texel: its face is keyed by the texture's own alpha,
    /// and at <see cref="Inset"/> the filtered alpha was already part way down, so the rim was drawn part covered and the tread's
    /// inside showed through as a dark seam.
    /// </summary>
    private const float BlownInset = 1.2f;

    /// <summary>
    /// The texture radius (about the centre, in uv) a round tyre's rim reads at most. A round wheel face is keyed by
    /// object.gdshaderinc's round_key: an analytic circle that fades out from radius 0.495 and reads no colour past 0.465. A rim
    /// right on the texture's edge (≈ 0.494) sat in that fade, half covered, and the tread's black inside showed as a seam round
    /// the hub; inside 0.465 the face is solid to its rim.
    /// </summary>
    public const float RoundKeySolid = 0.46f;

    /// <summary>
    /// Fit a smooth wheel to a .wdf wheel mesh: its textured face is a polygon (an octagon) laid on a texture whose
    /// tyre is a disc in the square (a keyed disc, the rest index 255), so the polygon's flat edges cut across the
    /// tyre or show the key's surroundings. The outline is the tyre as the texture draws it: ray-marched from its
    /// centre to the last opaque texel, laid on the wheel by the face's own mapping from texture to wheel; a round
    /// one is made a circle (by the narrower of its two spans, which is the one the ground touches), a blown tyre keeps
    /// its squashed shape. Null if the mesh has no textured side or the texture is no disc in a square (it fills it).
    /// </summary>
    public static SmoothWheel? Build(GeoMesh geo, Affine node, IndexedTexture tex, int segments = 48)
    {
        var face = geo.Faces.FirstOrDefault(f => f.Texture.Length > 0 && f.Vertices.Length >= 3 && f.Vertices.All(v => (uint)v.Index < geo.Vertices.Length));
        if (face is null || tex.Width < 8 || tex.Height < 8) return null;
        var pts = face.Vertices.Select(v => node.Apply(geo.Vertices[v.Index])).ToArray();
        var uvs = face.Vertices.Select(v => new Vector2(v.U, v.V)).ToArray();
        // the face's mapping from texture (u, v) to the wheel's (y, z): origin + a du + b dv, from the widest triple of corners
        int bi = 0, bj = 1, bk = 2; float best = 0;
        for (int i = 0; i < pts.Length; i++) for (int j = i + 1; j < pts.Length; j++) for (int k = j + 1; k < pts.Length; k++)
        {
            float ar = MathF.Abs(Cross(uvs[j] - uvs[i], uvs[k] - uvs[i])) * MathF.Abs(Cross(YZ(pts[j]) - YZ(pts[i]), YZ(pts[k]) - YZ(pts[i])));
            if (ar > best) { best = ar; bi = i; bj = j; bk = k; }
        }
        if (best < 1e-9f) return null;
        var du1 = uvs[bj] - uvs[bi]; var du2 = uvs[bk] - uvs[bi];
        var dp1 = YZ(pts[bj]) - YZ(pts[bi]); var dp2 = YZ(pts[bk]) - YZ(pts[bi]);
        float det = du1.X * du2.Y - du2.X * du1.Y;
        var a = (dp1 * du2.Y - dp2 * du1.Y) / det;   // the wheel's (y, z) per unit of u
        var b = (dp2 * du1.X - dp1 * du2.X) / det;   // and per unit of v
        Vector2 At(Vector2 uv) => YZ(pts[bi]) + a * (uv.X - uvs[bi].X) + b * (uv.Y - uvs[bi].Y);

        // the tyre's opaque texels: their box's centre, and the radius to the last opaque texel at each angle
        int x0 = tex.Width, x1 = -1, y0 = tex.Height, y1 = -1, opaque = 0;
        for (int y = 0; y < tex.Height; y++)
            for (int x = 0; x < tex.Width; x++)
                if (tex.Indices[y * tex.Width + x] != TextureReader.TransparentIndex) { x0 = Math.Min(x0, x); x1 = Math.Max(x1, x); y0 = Math.Min(y0, y); y1 = Math.Max(y1, y); opaque++; }
        if (opaque == 0 || opaque > 0.9f * tex.Width * tex.Height) return null;
        float cx = (x0 + x1 + 1) / 2f, cy = (y0 + y1 + 1) / 2f;   // texels
        bool Opaque(float px, float py) =>
            (uint)(int)px < (uint)tex.Width && (uint)(int)py < (uint)tex.Height && px >= 0 && py >= 0 && tex.Indices[(int)py * tex.Width + (int)px] != TextureReader.TransparentIndex;
        var rho = new float[segments];
        for (int k = 0; k < segments; k++)
        {
            // inward from outside: a hubcap's keyed holes are no edge
            float th = 2 * MathF.PI * k / segments, dx = MathF.Cos(th), dy = MathF.Sin(th), r = MathF.Max(tex.Width, tex.Height);
            while (r > 0 && !Opaque(cx + dx * (r - 0.1f), cy + dy * (r - 0.1f))) r -= 0.25f;
            rho[k] = r;
        }
        float mean = rho.Average();
        bool circle = (rho.Max() - rho.Min()) < 0.08f * mean;
        if (!circle)
        {
            var sm = new float[segments];
            for (int k = 0; k < segments; k++)
            {
                float s = 0; for (int d = -2; d <= 2; d++) s += rho[(k + d + segments) % segments];
                sm[k] = MathF.Min(s / 5, rho[k]);   // never out past the texels' edge
            }
            rho = sm;
        }
        var centreUv = new Vector2(cx / tex.Width, cy / tex.Height);
        var centre = At(centreUv);
        // a round tyre turns about the wheel's origin when the texture's centre is on it
        if (circle && MathF.Abs(centre.X) < 0.03f && MathF.Abs(centre.Y) < 0.03f) centre = Vector2.Zero;
        float outerX = pts.Average(p => p.X);
        float innerX = geo.Vertices.Select(v => node.Apply(v).X).OrderByDescending(x => MathF.Abs(x - outerX)).First();
        if (MathF.Abs(innerX - outerX) < 0.02f) return null;

        var rim = new Vector3[segments]; var rimUv = new Vector2[segments]; var norm = new Vector3[segments];
        float radius = 0;
        var ua = Vector2.Normalize(a); var vb = Vector2.Normalize(b);
        if (circle)
        {
            // texels to uv, then to metres along each of the face's own axes: the ellipse the face laid the circle as
            float ru = (mean - Inset) / tex.Width, rv = (mean - Inset) / tex.Height;
            radius = MathF.Min(a.Length() * ru, b.Length() * rv);
            // not past the original mesh's own reach about the hub: the tyre meets the ground where it did
            var reach = geo.Vertices.Select(v => node.Apply(v)).ToArray();
            radius = MathF.Min(radius, MathF.Min(MathF.Min(-reach.Min(p => p.Y), reach.Max(p => p.Y)), MathF.Min(-reach.Min(p => p.Z), reach.Max(p => p.Z))));
        }
        for (int k = 0; k < segments; k++)
        {
            float th = 2 * MathF.PI * k / segments, c = MathF.Cos(th), s = MathF.Sin(th);
            float r = Math.Max(rho[k] - (circle ? Inset : BlownInset), 1f);
            rimUv[k] = centreUv + new Vector2(r * c / tex.Width, r * s / tex.Height);
            if (circle)
            {
                // read no further out than round_key keeps solid (the geometry keeps the tyre's full radius)
                var off = rimUv[k] - new Vector2(0.5f);
                if (off.Length() > RoundKeySolid) rimUv[k] = new Vector2(0.5f) + off * (RoundKeySolid / off.Length());
            }
            var yz = circle ? centre + radius * (ua * c + vb * s) : At(rimUv[k]) + (centre - At(centreUv));
            rim[k] = new Vector3(outerX, yz.X, yz.Y);
        }
        for (int k = 0; k < segments; k++)
        {
            var t = YZ(rim[(k + 1) % segments]) - YZ(rim[(k + segments - 1) % segments]);
            var n = Vector2.Normalize(new Vector2(t.Y, -t.X));
            if (Vector2.Dot(n, YZ(rim[k]) - centre) < 0) n = -n;
            norm[k] = new Vector3(0, n.X, n.Y);
        }
        // the tread's flat colour: any untextured side of the original (one that runs along the axle)
        var side = geo.Faces.FirstOrDefault(f => f.Texture.Length == 0 && f.Vertices.Length >= 3
            && f.Vertices.All(v => (uint)v.Index < geo.Vertices.Length)
            && f.Vertices.Select(v => node.Apply(geo.Vertices[v.Index]).X).Distinct().Count() > 1) ?? geo.Faces.FirstOrDefault(f => f.Texture.Length == 0);
        // the tread in the tyre's own colour where the texture carries its colours (a .m16): the sidewall's ring just inside the
        // rim, so the tyre does not step from the texture's grey rubber to the original's flat near-black tread at the rim
        byte tr = side?.R ?? 40, tg = side?.G ?? 40, tb = side?.B ?? 40;
        if (tex.Palette is { } pal)
        {
            float sr = 0, sg = 0, sb = 0; int count = 0;
            for (int k = 0; k < segments; k++)
            {
                float th = 2 * MathF.PI * k / segments, rr = rho[k] - 2.5f;
                int px = (int)(cx + MathF.Cos(th) * rr), py = (int)(cy + MathF.Sin(th) * rr);
                if (!Opaque(px + 0.5f, py + 0.5f)) continue;
                byte ix = tex.Indices[py * tex.Width + px];
                if (ix >= pal.Length) continue;
                var (r8, g8, b8) = TextureReader.DecodeRgb565(pal[ix]);
                sr += r8; sg += g8; sb += b8; count++;
            }
            if (count >= segments / 2) (tr, tg, tb) = ((byte)(sr / count), (byte)(sg / count), (byte)(sb / count));
        }
        return new SmoothWheel(rim, rimUv, norm, new Vector3(outerX, centre.X, centre.Y), centreUv, outerX, innerX,
            tr, tg, tb, circle, radius);
    }

    private static Vector2 YZ(Vector3 p) => new(p.Y, p.Z);
    private static float Cross(Vector2 a, Vector2 b) => a.X * b.Y - a.Y * b.X;
}
