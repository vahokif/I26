using System.Globalization;
using System.IO.Compression;
using System.Numerics;
using System.Text;

namespace I76.Formats.Export;

/// <summary>
/// A Wavefront OBJ mesh being written: shared position, texture-coordinate and normal lists, and faces in named groups, each
/// face naming its material. Faces are triangles, counter-clockwise seen from the front (the OBJ convention); texture
/// coordinates have v up (the OBJ convention), so a texture's first row is at v = 1.
/// </summary>
public sealed class ObjMesh
{
    public readonly record struct Corner(int V, int Vt, int Vn);
    public sealed record Face(string Material, Corner A, Corner B, Corner C);
    public sealed class Group(string name)
    {
        public string Name { get; } = name;
        public List<Face> Faces { get; } = [];
    }

    public List<Vector3> Positions { get; } = [];
    public List<Vector2> Uvs { get; } = [];
    public List<Vector3> Normals { get; } = [];
    public List<Group> Groups { get; } = [];

    private readonly Dictionary<Vector2, int> _uvIndex = [];
    private readonly Dictionary<Vector3, int> _normalIndex = [];

    public int TriangleCount => Groups.Sum(g => g.Faces.Count);

    /// <summary>The materials the faces name, in first-use order.</summary>
    public IEnumerable<string> Materials => Groups.SelectMany(g => g.Faces).Select(f => f.Material).Distinct(StringComparer.Ordinal);

    public Group AddGroup(string name)
    {
        var g = new Group(name);
        Groups.Add(g);
        return g;
    }

    /// <summary>A new position (no sharing: the caller shares by keeping the index).</summary>
    public int AddPosition(Vector3 p)
    {
        Positions.Add(p);
        return Positions.Count - 1;
    }

    /// <summary>A texture coordinate, shared with an equal one.</summary>
    public int AddUv(Vector2 uv)
    {
        if (_uvIndex.TryGetValue(uv, out int i)) return i;
        Uvs.Add(uv);
        return _uvIndex[uv] = Uvs.Count - 1;
    }

    /// <summary>A normal (normalised), shared with an equal one.</summary>
    public int AddNormal(Vector3 n)
    {
        if (_normalIndex.TryGetValue(n, out int i)) return i;
        Normals.Add(n);
        return _normalIndex[n] = Normals.Count - 1;
    }

    /// <summary>The axis-aligned box of the positions the faces use.</summary>
    public (Vector3 Min, Vector3 Max) Bounds()
    {
        Vector3 lo = new(float.MaxValue), hi = new(float.MinValue);
        foreach (var f in Groups.SelectMany(g => g.Faces))
            foreach (var c in new[] { f.A, f.B, f.C })
            {
                lo = Vector3.Min(lo, Positions[c.V]);
                hi = Vector3.Max(hi, Positions[c.V]);
            }
        return lo.X > hi.X ? (Vector3.Zero, Vector3.Zero) : (lo, hi);
    }

    /// <summary>Writes the OBJ text: the header lines as comments, the material library, then the lists and the groups (empty groups left out).</summary>
    public void Write(TextWriter w, string? mtllib, IEnumerable<string>? header = null)
    {
        w.NewLine = "\n";
        foreach (var h in header ?? []) w.WriteLine("# " + h);
        if (mtllib is not null) w.WriteLine("mtllib " + mtllib);
        var ic = CultureInfo.InvariantCulture;
        foreach (var p in Positions) w.WriteLine(string.Create(ic, $"v {p.X:0.#####} {p.Y:0.#####} {p.Z:0.#####}"));
        foreach (var t in Uvs) w.WriteLine(string.Create(ic, $"vt {t.X:0.######} {t.Y:0.######}"));
        foreach (var n in Normals) w.WriteLine(string.Create(ic, $"vn {n.X:0.#####} {n.Y:0.#####} {n.Z:0.#####}"));
        foreach (var g in Groups)
        {
            if (g.Faces.Count == 0) continue;
            w.WriteLine("g " + g.Name);
            // the group's faces by material (in the order the materials first appear), so each material is named once
            foreach (var byMat in g.Faces.GroupBy(f => f.Material, StringComparer.Ordinal))
            {
                w.WriteLine("usemtl " + byMat.Key);
                foreach (var f in byMat) w.WriteLine($"f {C(f.A)} {C(f.B)} {C(f.C)}");
            }
        }

        static string C(Corner c) => c.Vt < 0 ? $"{c.V + 1}//{c.Vn + 1}" : $"{c.V + 1}/{c.Vt + 1}/{c.Vn + 1}";
    }
}

/// <summary>One material of an MTL file: a flat colour or a texture, with the face mode's transparency.</summary>
public sealed class ObjMaterial
{
    public required string Name { get; init; }
    /// <summary>The diffuse colour (0..1); white under a texture.</summary>
    public Vector3 Colour { get; init; } = Vector3.One;
    /// <summary>The colour texture's path, relative to the MTL.</summary>
    public string? Texture { get; init; }
    /// <summary>The texture whose alpha is the cut-out (map_d), relative to the MTL; null for an opaque face.</summary>
    public string? AlphaTexture { get; init; }
    /// <summary>Overall opacity (a translucent face's blend).</summary>
    public float Opacity { get; init; } = 1;
    /// <summary>Drawn at full brightness by the game, unlit: said in the note (OBJ viewers know no such thing; illum 0 is not widely read).</summary>
    public bool Unlit { get; init; }
    /// <summary>A note written as a comment above the material.</summary>
    public string? Note { get; init; }
}

public static class MtlWriter
{
    public static void Write(TextWriter w, IEnumerable<ObjMaterial> materials, IEnumerable<string>? header = null)
    {
        w.NewLine = "\n";
        foreach (var h in header ?? []) w.WriteLine("# " + h);
        var ic = CultureInfo.InvariantCulture;
        foreach (var m in materials)
        {
            w.WriteLine();
            if (m.Note is not null) w.WriteLine("# " + m.Note);
            w.WriteLine("newmtl " + m.Name);
            w.WriteLine(string.Create(ic, $"Ka 0 0 0"));
            w.WriteLine(string.Create(ic, $"Kd {m.Colour.X:0.####} {m.Colour.Y:0.####} {m.Colour.Z:0.####}"));
            w.WriteLine("Ks 0 0 0");
            w.WriteLine(string.Create(ic, $"d {m.Opacity:0.###}"));
            w.WriteLine("illum 1");
            if (m.Texture is not null) w.WriteLine("map_Kd " + m.Texture);
            if (m.AlphaTexture is not null) w.WriteLine("map_d " + m.AlphaTexture);
        }
    }
}

/// <summary>A minimal PNG encoder: 8-bit RGB or RGBA, no interlace.</summary>
public static class PngWriter
{
    public static byte[] Encode(int width, int height, byte[] pixels, bool alpha)
    {
        int bpp = alpha ? 4 : 3;
        if (pixels.Length != width * height * bpp) throw new ArgumentException("pixel count does not match the size");
        using var ms = new MemoryStream();
        ms.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0d, 0x0a, 0x1a, 0x0a]);
        var ihdr = new byte[13];
        BE(ihdr, 0, (uint)width); BE(ihdr, 4, (uint)height);
        ihdr[8] = 8; ihdr[9] = (byte)(alpha ? 6 : 2);
        Chunk(ms, "IHDR", ihdr);
        using (var raw = new MemoryStream())
        {
            using (var z = new ZLibStream(raw, CompressionLevel.SmallestSize, leaveOpen: true))
                for (int y = 0; y < height; y++)
                {
                    z.WriteByte(0);
                    z.Write(pixels, y * width * bpp, width * bpp);
                }
            Chunk(ms, "IDAT", raw.ToArray());
        }
        Chunk(ms, "IEND", []);
        return ms.ToArray();
    }

    private static void Chunk(Stream s, string tag, byte[] data)
    {
        var len = new byte[4];
        BE(len, 0, (uint)data.Length);
        s.Write(len);
        var t = Encoding.ASCII.GetBytes(tag);
        s.Write(t);
        s.Write(data);
        uint crc = Crc(Crc(0xffffffffu, t), data) ^ 0xffffffffu;
        var c = new byte[4];
        BE(c, 0, crc);
        s.Write(c);
    }

    private static void BE(byte[] b, int at, uint v) { b[at] = (byte)(v >> 24); b[at + 1] = (byte)(v >> 16); b[at + 2] = (byte)(v >> 8); b[at + 3] = (byte)v; }

    private static readonly uint[] Table = Enumerable.Range(0, 256).Select(n =>
    {
        uint c = (uint)n;
        for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xedb88320u ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    private static uint Crc(uint crc, byte[] data)
    {
        foreach (var b in data) crc = Table[(crc ^ b) & 0xff] ^ (crc >> 8);
        return crc;
    }
}
