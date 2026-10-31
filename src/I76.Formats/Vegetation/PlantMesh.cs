using System.Numerics;

namespace I76.Formats.Vegetation;

/// <summary>
/// A plant's triangles, ready for an engine mesh: positions in metres (Y up, base at the origin), normals, linear
/// colours (the atlas texel is multiplied by them), atlas UVs, and per vertex the sway weight (0 at the ground, 1
/// at the plant's top, more at a twig's free end) and a foliage flag (1: an alpha-tested card whose normal points
/// out of the plant's crown, lit the same from either side). The colours' alpha is the baked ambient occlusion
/// (<see cref="PlantShading"/>).
/// </summary>
public sealed class PlantMesh
{
    public List<Vector3> Positions { get; } = [];
    public List<Vector3> Normals { get; } = [];
    public List<Vector4> Colors { get; } = [];
    /// <summary>The shade the plant casts on the ground under it from the sky (<see cref="PlantShading.GroundShade"/>): the share of the sky's light lost at its foot, and how far out (metres) the shade reaches.</summary>
    public float GroundShade { get; set; }
    public float GroundRadius { get; set; }
    public List<Vector2> Uvs { get; } = [];
    public List<Vector2> Extra { get; } = [];   // x: sway weight, y: foliage flag (1 a card, Shapes.ThinBlade a thin grass blade; over 0.5 shaded as foliage)
    /// <summary>
    /// Per vertex of a thin blade (grass): xyz the offset from the blade's centre line to this vertex (half its width, to
    /// its side), w the blade's half-width at its base in tenths of a millimetre plus a random 0..1 for the blade, so the
    /// shader can keep a blade at least a pixel wide far off and thin out the blades as it widens them
    /// (vegetation.gdshader); zero elsewhere.
    /// </summary>
    public List<Vector4> Side { get; } = [];
    public List<int> Indices { get; } = [];

    /// <summary>
    /// Where a lighter tier's vertex takes its occlusion from the full mesh (<see cref="PlantShading.Transfer"/>), when
    /// not where it lies: a tuft card's bottom corners, which stand on bare ground beside the tuft (their texels are
    /// clear), take the tuft's foot.
    /// </summary>
    public Dictionary<int, Vector3> ShadeAt { get; } = [];

    public int VertexCount => Positions.Count;
    public int TriangleCount => Indices.Count / 3;

    public int Vertex(Vector3 p, Vector3 n, Vector4 c, Vector2 uv, float sway, float foliage = 0, Vector4 side = default)
    {
        Positions.Add(p); Normals.Add(n); Colors.Add(c); Uvs.Add(uv); Extra.Add(new Vector2(sway, foliage)); Side.Add(side);
        return Positions.Count - 1;
    }

    /// <summary>Whether any vertex carries a blade's side (<see cref="Side"/>): the engine mesh then gets the attribute.</summary>
    public bool HasSide => Side.Exists(s => s != Vector4.Zero);

    public void Tri(int a, int b, int c) { Indices.Add(a); Indices.Add(b); Indices.Add(c); }
    public void Quad(int a, int b, int c, int d) { Tri(a, b, c); Tri(a, c, d); }

    public (Vector3 Min, Vector3 Max) Bounds()
    {
        if (Positions.Count == 0) return (Vector3.Zero, Vector3.Zero);
        var mn = Positions[0]; var mx = Positions[0];
        foreach (var p in Positions) { mn = Vector3.Min(mn, p); mx = Vector3.Max(mx, p); }
        return (mn, mx);
    }

    /// <summary>A 64-bit digest of every attribute, to check that a seed always builds the same mesh.</summary>
    public ulong Digest()
    {
        ulong h = 0xcbf29ce484222325UL;
        void F(float f) { h ^= BitConverter.SingleToUInt32Bits(f); h *= 0x100000001b3UL; }
        for (int i = 0; i < Positions.Count; i++)
        {
            F(Positions[i].X); F(Positions[i].Y); F(Positions[i].Z);
            F(Normals[i].X); F(Normals[i].Y); F(Normals[i].Z);
            F(Colors[i].X); F(Colors[i].Y); F(Colors[i].Z); F(Colors[i].W); F(Uvs[i].X); F(Uvs[i].Y); F(Extra[i].X); F(Extra[i].Y);
            F(Side[i].X); F(Side[i].Y); F(Side[i].Z); F(Side[i].W);
        }
        foreach (int i in Indices) { h ^= (uint)i; h *= 0x100000001b3UL; }
        return h;
    }
}

/// <summary>The atlas cells (<see cref="FoliageAtlas"/>), 8 across and 4 down the texture, row by row.</summary>
public enum AtlasCell
{
    Bark = 0, Creosote = 1, Mesquite = 2, Saltbush = 3,
    Tamarisk = 4, Willow = 5, Tumbleweed = 6, Cholla = 7,
    PadSkin = 8, Blade = 9, Grass = 10, YuccaFlowers = 11,
    DeadLeaves = 12, Forb = 13, Panicle = 14, GrassTuft = 15,
    SandSage = 16, ShinOak = 17, GrayOak = 18, OcotilloLeaf = 19,
    RedFlowers = 20, IndianBlanket = 21, PricklyPoppy = 22, Horsemint = 23,
    Buffalograss = 24, Pickleweed = 25, AgaveBlade = 26, Candelilla = 27,
    PineSpray = 28,
}

/// <summary>
/// Primitive shapes into a <see cref="PlantMesh"/>: tubes for stems and cactus joints, crossed alpha cards for foliage,
/// flattened ellipsoids for pads and fruit, tapered blades for yucca and grass.
/// </summary>
public static class Shapes
{
    /// <summary>The atlas' cells across and down.</summary>
    public const int CellsX = 8, CellsY = 4;

    /// <summary>
    /// The foliage flag of a thin grass blade: shaded with the foliage (it covers little of what is behind it, and a
    /// tuft's far cards take their occlusion from its blades), translucent, fluttering a little (vegetation.gdshader).
    /// </summary>
    public const float ThinBlade = 0.6f;

    /// <summary>A UV inside an atlas cell, with a small inset so mipmaps do not bleed between cells.</summary>
    public static Vector2 Uv(AtlasCell cell, float u, float v)
    {
        int c = (int)cell;
        const float inset = 2f / 256f;
        float cu = (c % CellsX + inset + u * (1 - 2 * inset)) / CellsX;
        float cv = (c / CellsX + inset + v * (1 - 2 * inset)) / CellsY;
        return new Vector2(cu, cv);
    }

    public static Vector3 AnyPerpendicular(Vector3 d)
    {
        var a = MathF.Abs(d.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX;
        return Vector3.Normalize(Vector3.Cross(d, a));
    }

    /// <summary>
    /// A tube from a to b: <paramref name="sides"/> faces round, radius ra to rb, the seam at <paramref name="side"/>;
    /// <paramref name="bulge"/> swells the middle (a cactus joint), <paramref name="cap"/> closes the far end with a point.
    /// </summary>
    /// <returns>The index of the far end's ring (sides + 1 vertices), for the next segment to start from (<paramref name="from"/>).</returns>
    public static int Tube(PlantMesh m, Vector3 a, Vector3 b, float ra, float rb, int sides, Vector3 side, Vector4 colA, Vector4 colB,
        float swayA, float swayB, AtlasCell cell, float vRepeat = 1f, int rings = 1, float bulge = 0f, bool cap = false, float wobble = 0f, ulong seed = 0, int from = -1)
    {
        var d = b - a;
        float len = d.Length();
        if (len < 1e-5f) return -1;
        var h = d / len;
        var x = side - h * Vector3.Dot(side, h);
        x = x.LengthSquared() < 1e-8f ? AnyPerpendicular(h) : Vector3.Normalize(x);
        var y = Vector3.Cross(h, x);
        int start = m.VertexCount;
        var rng = new VegRng(seed);
        // a segment going on from the last one shares its ring (from), so a stem is one tube
        for (int r = from >= 0 ? 1 : 0; r <= rings; r++)
        {
            float t = (float)r / rings;
            float rad = ra + (rb - ra) * t;
            rad *= 1 + bulge * MathF.Sin(MathF.PI * t);
            var c = a + d * t;
            var col = Vector4.Lerp(colA, colB, t);
            float sw = swayA + (swayB - swayA) * t;
            for (int s = 0; s <= sides; s++)
            {
                float ang = s * MathF.Tau / sides;
                var n = x * MathF.Cos(ang) + y * MathF.Sin(ang);
                float rr = rad * (1 + (s < sides && wobble > 0 ? wobble * rng.Bell() : 0));
                m.Vertex(c + n * rr, Vector3.Normalize(n - h * ((rb - ra) / len)), col, Uv(cell, (float)s / sides, MathF.Min(1f, t * vRepeat)), sw);
            }
        }
        // ring r's first vertex: ring 0 is the shared one when going on from a segment, the rest follow in order
        int first = from >= 0 ? m.VertexCount - rings * (sides + 1) : start;
        int Ring(int r) => from >= 0 ? (r == 0 ? from : first + (r - 1) * (sides + 1)) : start + r * (sides + 1);
        for (int r = 0; r < rings; r++)
            for (int s = 0; s < sides; s++)
            {
                int i0 = Ring(r) + s, i1 = i0 + 1, j0 = Ring(r + 1) + s, j1 = j0 + 1;
                m.Quad(i0, j0, j1, i1);
            }
        int last = Ring(rings);
        if (cap)
        {
            int tip = m.Vertex(b + h * rb * 0.6f, h, colB, Uv(cell, 0.5f, 1f), swayB);
            for (int s = 0; s < sides; s++) m.Tri(last + s, tip, last + s + 1);
        }
        return last;
    }

    /// <summary>
    /// A foliage cluster: <paramref name="planes"/> alpha cards crossed about the twig's direction, size x size, the
    /// card's bottom edge at the twig. Normals point from <paramref name="crown"/> (the plant's middle) through the
    /// card, so the clusters together shade like one rounded crown.
    /// </summary>
    public static void Cards(PlantMesh m, Vector3 pos, Vector3 dir, Vector3 up, float size, AtlasCell cell, Vector4 col, float sway,
        Vector3 crown, int planes = 2, float lift = 0.35f, float roundness = 0.75f, bool cap = false)
    {
        dir = dir.LengthSquared() < 1e-8f ? Vector3.UnitY : Vector3.Normalize(dir);
        var side0 = Vector3.Cross(dir, up);
        side0 = side0.LengthSquared() < 1e-6f ? AnyPerpendicular(dir) : Vector3.Normalize(side0);
        var centre = pos + dir * (size * lift);
        for (int p = 0; p < planes; p++)
        {
            var side = Turtle.Rotate(side0, dir, p * MathF.PI / planes);
            var faceN = Vector3.Normalize(Vector3.Cross(side, dir));
            var c0 = centre - side * (size * 0.5f) - dir * (size * 0.5f);
            Vector3[] corners = [c0, c0 + side * size, c0 + side * size + dir * size, c0 + dir * size];
            Vector2[] uv = [new(0, 1), new(1, 1), new(1, 0), new(0, 0)];
            int i0 = m.VertexCount;
            for (int k = 0; k < 4; k++)
            {
                var outward = corners[k] - crown;
                outward = outward.LengthSquared() < 1e-8f ? faceN : Vector3.Normalize(outward);
                // the facing that matches the outward direction, then bent toward it
                var fn = Vector3.Dot(faceN, outward) >= 0 ? faceN : -faceN;
                var n = Vector3.Normalize(Vector3.Lerp(fn, outward, roundness));
                float sw = sway * (k >= 2 ? 1.15f : 1f);
                m.Vertex(corners[k], n, col, Uv(cell, uv[k].X, uv[k].Y), sw, 1f);
            }
            m.Quad(i0, i0 + 1, i0 + 2, i0 + 3);
        }
        if (cap)
        {
            // a third card across the twig, so a cluster seen from above (or along its twig) is not edge-on
            var a = side0 * (size * 0.5f); var b = Vector3.Cross(dir, side0) * (size * 0.5f);
            var c = centre + dir * (size * 0.1f);
            Vector3[] corners = [c - a - b, c + a - b, c + a + b, c - a + b];
            Vector2[] uv = [new(0, 1), new(1, 1), new(1, 0), new(0, 0)];
            int i0 = m.VertexCount;
            for (int k = 0; k < 4; k++)
            {
                var outward = corners[k] - crown;
                outward = outward.LengthSquared() < 1e-8f ? dir : Vector3.Normalize(outward);
                var fn = Vector3.Dot(dir, outward) >= 0 ? dir : -dir;
                m.Vertex(corners[k], Vector3.Normalize(Vector3.Lerp(fn, outward, roundness)), col, Uv(cell, uv[k].X, uv[k].Y), sway * 1.1f, 1f);
            }
            m.Quad(i0, i0 + 1, i0 + 2, i0 + 3);
        }
    }

    /// <summary>
    /// One alpha card, <paramref name="width"/> along <paramref name="side"/> and <paramref name="height"/> along
    /// <paramref name="up"/> from the middle of its bottom edge at <paramref name="pos"/> (the cell's bottom edge);
    /// normals from <paramref name="crown"/> as <see cref="Cards"/>, the sway weight 0 at the bottom.
    /// </summary>
    public static void Card(PlantMesh m, Vector3 pos, Vector3 side, Vector3 up, float width, float height, AtlasCell cell, Vector4 col, float sway,
        Vector3 crown, float roundness = 0.6f)
    {
        side = Vector3.Normalize(side); up = Vector3.Normalize(up);
        var faceN = Vector3.Cross(side, up);
        faceN = faceN.LengthSquared() < 1e-8f ? AnyPerpendicular(up) : Vector3.Normalize(faceN);
        var c0 = pos - side * (width * 0.5f);
        Vector3[] corners = [c0, c0 + side * width, c0 + side * width + up * height, c0 + up * height];
        Vector2[] uv = [new(0, 1), new(1, 1), new(1, 0), new(0, 0)];
        int i0 = m.VertexCount;
        for (int k = 0; k < 4; k++)
        {
            var outward = corners[k] - crown;
            outward = outward.LengthSquared() < 1e-8f ? faceN : Vector3.Normalize(outward);
            var fn = Vector3.Dot(faceN, outward) >= 0 ? faceN : -faceN;
            m.Vertex(corners[k], Vector3.Normalize(Vector3.Lerp(fn, outward, roundness)), col, Uv(cell, uv[k].X, uv[k].Y), k >= 2 ? sway : 0f, 1f);
        }
        m.Quad(i0, i0 + 1, i0 + 2, i0 + 3);
    }

    /// <summary>
    /// <see cref="Card"/> in two halves side by side (6 vertices): the middle of its bottom edge a vertex of its own, so
    /// a tuft's card takes the dark of the tuft's foot there (its occlusion is per vertex), and its bottom corners take
    /// it too (<see cref="PlantMesh.ShadeAt"/>: they stand on the bare ground beside the tuft).
    /// </summary>
    public static void SplitCard(PlantMesh m, Vector3 pos, Vector3 side, Vector3 up, float width, float height, AtlasCell cell, Vector4 col, float sway,
        Vector3 crown, float roundness = 0.6f)
    {
        side = Vector3.Normalize(side); up = Vector3.Normalize(up);
        var faceN = Vector3.Cross(side, up);
        faceN = faceN.LengthSquared() < 1e-8f ? AnyPerpendicular(up) : Vector3.Normalize(faceN);
        int i0 = m.VertexCount;
        for (int row = 0; row < 2; row++)
            for (int k = 0; k < 3; k++)
            {
                var p = pos + side * (width * (k * 0.5f - 0.5f)) + up * (height * row);
                var outward = p - crown;
                outward = outward.LengthSquared() < 1e-8f ? faceN : Vector3.Normalize(outward);
                var fn = Vector3.Dot(faceN, outward) >= 0 ? faceN : -faceN;
                int v = m.Vertex(p, Vector3.Normalize(Vector3.Lerp(fn, outward, roundness)), col, Uv(cell, k * 0.5f, 1 - row), row == 1 ? sway : 0f, 1f);
                if (row == 0 && k != 1) m.ShadeAt[v] = pos;
            }
        m.Quad(i0, i0 + 1, i0 + 4, i0 + 3);
        m.Quad(i0 + 1, i0 + 2, i0 + 5, i0 + 4);
    }

    /// <summary>An ellipsoid: semi-axes ax along a, bx along b, cx along their cross; rings x sectors.</summary>
    public static void Ellipsoid(PlantMesh m, Vector3 centre, Vector3 a, Vector3 b, float ra, float rb, float rc, int rings, int sectors,
        Vector4 col, float sway, AtlasCell cell, float swayTop = -1f)
    {
        a = Vector3.Normalize(a); b = Vector3.Normalize(b - a * Vector3.Dot(b, a));
        var c = Vector3.Cross(a, b);
        int start = m.VertexCount;
        for (int i = 0; i <= rings; i++)
        {
            float th = MathF.PI * i / rings;          // from the -a pole to the +a pole
            float ca = -MathF.Cos(th), sa = MathF.Sin(th);
            for (int j = 0; j <= sectors; j++)
            {
                float ph = MathF.Tau * j / sectors;
                float cb = MathF.Cos(ph), sc = MathF.Sin(ph);
                var p = centre + a * (ra * ca) + b * (rb * sa * cb) + c * (rc * sa * sc);
                var n = Vector3.Normalize(a * (ca / ra) + b * (sa * cb / rb) + c * (sa * sc / rc));
                float sw = swayTop < 0 ? sway : sway + (swayTop - sway) * (ca * 0.5f + 0.5f);
                m.Vertex(p, n, col, Uv(cell, (float)j / sectors, (float)i / rings), sw);
            }
        }
        for (int i = 0; i < rings; i++)
            for (int j = 0; j < sectors; j++)
            {
                int i0 = start + i * (sectors + 1) + j, i1 = i0 + 1, j0 = i0 + sectors + 1, j1 = j0 + 1;
                m.Quad(i0, i1, j1, j0);
            }
    }

    /// <summary>
    /// A blade (yucca leaf, grass): from the base along a quadratic curve through dir bending toward <paramref name="droop"/>,
    /// width tapering to a point (swelling toward the middle by <paramref name="belly"/>: 0.5 is half as wide again), folded
    /// into a shallow V (keel) when <paramref name="keel"/> &gt; 0. Normals, lit the same from either side, are the blade's
    /// facing toward the outside bent toward the direction out of <paramref name="crown"/> (default: a point below the
    /// base, as a tuft's middle) by <paramref name="roundness"/>, so a rosette or tuft shades as one rounded mass, light on
    /// its sun side and dark on the other, not every blade as flat ground.
    /// </summary>
    public static void Blade(PlantMesh m, Vector3 basePos, Vector3 dir, Vector3 droop, float length, float width, int segments,
        Vector4 colBase, Vector4 colTip, float swayBase, float swayTip, AtlasCell cell, float keel = 0f, float twist = 0f, float tipWidth = 0f, float belly = 0f,
        Vector3? crown = null, float roundness = 0.6f, float foliage = 0f, float bladeId = -1f)
    {
        var centre = crown ?? basePos - new Vector3(0, length * 0.6f, 0);
        // a thin blade (bladeId 0..1) carries its side for the shader's pixel-wide minimum (PlantMesh.Side)
        float packed = bladeId >= 0 ? MathF.Round(width * 0.5f * 1e4f) + Math.Clamp(bladeId, 0f, 0.999f) : 0f;
        Vector4 SideOf(Vector3 off) => bladeId >= 0 ? new Vector4(off, packed) : default;
        Vector3 Round(Vector3 n, Vector3 p)
        {
            var o = p - centre;
            if (o.LengthSquared() < 1e-8f) return n;
            o = Vector3.Normalize(o);
            return Vector3.Normalize(Vector3.Lerp(n, o, roundness));
        }
        dir = Vector3.Normalize(dir);
        var side = Vector3.Cross(dir, Vector3.UnitY);
        side = side.LengthSquared() < 1e-6f ? AnyPerpendicular(dir) : Vector3.Normalize(side);
        int across = keel > 0 ? 3 : 2;
        int start = m.VertexCount;
        Vector3 Pt(float t) => basePos + dir * (length * t) + droop * (length * t * t);
        for (int i = 0; i <= segments; i++)
        {
            float t = (float)i / segments;
            var p = Pt(t);
            var tangent = Vector3.Normalize(dir + droop * (2 * t) + new Vector3(1e-5f));
            var s = Turtle.Rotate(side, tangent, twist * t);
            s = Vector3.Normalize(s - tangent * Vector3.Dot(s, tangent));
            var n = Vector3.Normalize(Vector3.Cross(s, tangent));
            if (n.Y < 0) n = -n;
            // the facing toward the outside (the geometry's keel keeps folding along n)
            float sg = Vector3.Dot(n, p - centre) >= 0 ? 1 : -1;
            var nf = n * sg;
            float w = (width * (1 - t) + tipWidth * t) * (1 + belly * MathF.Sin(MathF.PI * t));
            w *= MathF.Min(1f, 0.55f + 3f * t);     // a little narrower at the very base, as a sheath
            var col = Vector4.Lerp(colBase, colTip, t);
            float sw = swayBase + (swayTip - swayBase) * t * t;
            if (across == 2)
            {
                m.Vertex(p - s * (w * 0.5f), Round(nf, p), col, Uv(cell, 0, t), sw, foliage, SideOf(-s * (w * 0.5f)));
                m.Vertex(p + s * (w * 0.5f), Round(nf, p), col, Uv(cell, 1, t), sw, foliage, SideOf(s * (w * 0.5f)));
            }
            else
            {
                var k = n * (keel * w);
                m.Vertex(p - s * (w * 0.5f) + k, Round(Vector3.Normalize(n - s * 0.5f) * sg, p), col, Uv(cell, 0, t), sw, foliage, SideOf(-s * (w * 0.5f)));
                m.Vertex(p, Round(nf, p), col * 1.04f, Uv(cell, 0.5f, t), sw, foliage, SideOf(Vector3.Zero));
                m.Vertex(p + s * (w * 0.5f) + k, Round(Vector3.Normalize(n + s * 0.5f) * sg, p), col, Uv(cell, 1, t), sw, foliage, SideOf(s * (w * 0.5f)));
            }
        }
        for (int i = 0; i < segments; i++)
            for (int k = 0; k < across - 1; k++)
            {
                int a = start + i * across + k, b = a + 1, c = a + across, d = c + 1;
                m.Quad(a, b, d, c);
            }
    }
}
