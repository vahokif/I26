using System.Numerics;

namespace I76.Formats.Geo;

public readonly record struct FaceVertex(int Index, int NormalIndex, float U, float V);

public sealed class GeoFace
{
    public int Unk_0x00 { get; init; }
    public uint VertexCount { get; init; }
    public byte R { get; init; }
    public byte G { get; init; }
    public byte B { get; init; }
    /// <summary>(nx, ny, nz, d).</summary>
    public Vector4 Plane { get; init; }
    public uint Unk_0x1B { get; init; }
    /// <summary>Copied to the runtime face; meaning not established.</summary>
    public byte Unk_0x1F { get; init; }
    public byte Unk_0x20 { get; init; }
    public byte Unk_0x21 { get; init; }
    /// <summary>Empty means untextured.</summary>
    public string Texture { get; init; } = "";
    public byte[] Unk_0x32 { get; init; } = [];
    public FaceVertex[] Vertices { get; init; } = [];
}

/// <summary>
/// A .geo mesh member, a port of the reference reader (docs/formats/geo-mesh.md). Faces are polygons with per-face-vertex UVs.
/// </summary>
public sealed class GeoMesh
{
    public const uint Magic = 0x2E47454F; // ".GEO"
    public const int HeaderSize = 0x24;
    public const int FaceFixed = 0x37;
    public const int FaceVertexSize = 0x10;

    public string Name { get; init; } = "";
    public uint Unk_0x04 { get; init; }
    public uint Unk_0x20 { get; init; }
    public Vector3[] Vertices { get; init; } = [];
    public Vector3[] Normals { get; init; } = [];
    public GeoFace[] Faces { get; init; } = [];
    public int Consumed { get; init; }

    public static GeoMesh Parse(byte[] d)
    {
        if (d.Length < HeaderSize) throw new InvalidDataException("too short for a .GEO header");
        uint magic = Bin.U32(d, 0);
        if (magic != Magic) throw new InvalidDataException($"bad magic 0x{magic:x8}");
        string name = Bin.CStr(d, 8, 0x10);
        int nv = Bin.I32(d, 0x18), nf = Bin.I32(d, 0x1C);
        uint unk20 = Bin.U32(d, 0x20);
        int vpos = HeaderSize, npos = vpos + nv * 12, fpos = npos + nv * 12;
        var verts = new Vector3[nv];
        var norms = new Vector3[nv];
        for (int i = 0; i < nv; i++)
        {
            verts[i] = new Vector3(Bin.F32(d, vpos + i * 12), Bin.F32(d, vpos + i * 12 + 4), Bin.F32(d, vpos + i * 12 + 8));
            norms[i] = new Vector3(Bin.F32(d, npos + i * 12), Bin.F32(d, npos + i * 12 + 4), Bin.F32(d, npos + i * 12 + 8));
        }
        var faces = new GeoFace[nf];
        int p = fpos;
        for (int f = 0; f < nf; f++)
        {
            if (p + FaceFixed > d.Length) throw new InvalidDataException("face record runs past end of data");
            uint n = Bin.U32(d, p + 4);
            int vp = p + FaceFixed;
            if (vp + (long)n * FaceVertexSize > d.Length) throw new InvalidDataException("face vertices run past end of data");
            var fv = new FaceVertex[n];
            for (int k = 0; k < n; k++)
            {
                int r = vp + k * FaceVertexSize;
                fv[k] = new FaceVertex(Bin.I32(d, r), Bin.I32(d, r + 4), Bin.F32(d, r + 8), Bin.F32(d, r + 12));
            }
            faces[f] = new GeoFace
            {
                Unk_0x00 = Bin.I32(d, p),
                VertexCount = n,
                R = d[p + 8], G = d[p + 9], B = d[p + 0x0A],
                Plane = new Vector4(Bin.F32(d, p + 0x0B), Bin.F32(d, p + 0x0F), Bin.F32(d, p + 0x13), Bin.F32(d, p + 0x17)),
                Unk_0x1B = Bin.U32(d, p + 0x1B),
                Unk_0x1F = d[p + 0x1F], Unk_0x20 = d[p + 0x20], Unk_0x21 = d[p + 0x21],
                Texture = Bin.CStr(d, p + 0x22, 16),
                Unk_0x32 = d.AsSpan(p + 0x32, 5).ToArray(),
                Vertices = fv,
            };
            p += FaceFixed + (int)n * FaceVertexSize;
        }
        return new GeoMesh
        {
            Name = name, Unk_0x04 = Bin.U32(d, 4), Unk_0x20 = unk20,
            Vertices = verts, Normals = norms, Faces = faces, Consumed = p,
        };
    }
}

/// <summary>
/// Whether a textured face is drawn lit (the object light through the mission's ramp) or at full brightness. The
/// raster mode (a table indexed by (+0x1f)·8 + (+0x20), a +0x1f of 0 taken as 4, |0x80 if +0x21, |0x10 on
/// the scene path) picks a routine, and some routines hand the plugin the ramp's value for the
/// vertex light while others hand it 0xff: modes 0x14, 0x54, 0x56, 0x57 and the
/// translucent 0x94..0xd5 are at 0xff; 0x15, 0x16, 0x17 and 0x55 are lit.
/// So a face of row 3 or 4 is unlit unless +0x20 bit 1 is set without the key bit (2); a face of row 5 is lit
/// unless it has both (6, 7); a translucent face is unlit. Rows 1 and 2 (mode 0xe3) and untextured faces are lit.
/// </summary>
public static class FaceLight
{
    public static bool Unlit(GeoFace f) => Unlit(f.Unk_0x1F, f.Unk_0x20, f.Unk_0x21);

    public static bool Unlit(byte row, byte flags, byte translucent)
    {
        if (row == 0) row = 4;
        if (row is not (3 or 4 or 5)) return false;
        if (translucent != 0) return true;
        bool b1 = (flags & 2) != 0, keyed = (flags & 4) != 0;
        return row == 5 ? b1 && keyed : !b1 || keyed;
    }
}
