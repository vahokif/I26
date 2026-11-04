using System.Numerics;
using I76.Formats.Assembly;
using I76.Formats.Export;
using I76.Formats.Geo;
using Xunit;

namespace I76.Formats.Tests;

/// <summary>
/// The OBJ/MTL/PNG export on a synthetic model: the frame (the game's z negated), the winding (counter-clockwise from outside), the
/// normals turned outward, the texture coordinates flipped to OBJ's v-up, the groups and materials, and a PNG a decoder accepts.
/// </summary>
public class ObjExportTests
{
    /// <summary>A unit quad lying flat, facing up, as a GEO holds it: corners counter-clockwise from above once z is mirrored, normals into the solid.</summary>
    private static GeoMesh UpQuad(string texture, byte flags)
    {
        var v = new[] { new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(1, 0, 1), new Vector3(0, 0, 1) };
        var face = new GeoFace
        {
            VertexCount = 4, Texture = texture, Unk_0x20 = flags, R = 10, G = 20, B = 30, Plane = new Vector4(0, -1, 0, 0),
            Vertices = [new(0, 0, 0, 0), new(1, 0, 1, 0), new(2, 0, 1, 0.25f), new(3, 0, 0, 0.25f)],
        };
        return new GeoMesh { Name = "QUAD", Vertices = v, Normals = [new Vector3(0, -1, 0)], Faces = [face] };
    }

    private static AssembledModel Model()
    {
        var m = new AssembledModel { Definition = "test.sdf", Kind = "sdf" };
        m.Parts.Add(new ModelPart("TS1_TOP1", UpQuad("TEST.MAP", 4), new Affine([1, 0, 0, 0, 1, 0, 0, 0, 1], new Vector3(2, 3, 4))));
        m.Parts.Add(new ModelPart("TS1_FLT1", UpQuad("", 0), Affine.Identity));
        return m;
    }

    [Fact]
    public void FacesAreMirroredWoundCounterClockwiseFromOutsideAndTheirNormalsPointOut()
    {
        var mesh = new ObjMesh();
        int tris = ModelObj.AddModel(mesh, Model(), p => p.NodeName.ToLowerInvariant(), (_, f) => ModelObj.Textured(f) ? "tex" : "flat");
        Assert.Equal(4, tris);
        Assert.Equal(["ts1_top1", "ts1_flt1"], mesh.Groups.Select(g => g.Name));
        // the placed quad: x and y as they are, z negated (2, 3, -4 .. 3, 3, -5)
        var top = mesh.Groups[0].Faces;
        var pts = top.SelectMany(f => new[] { f.A, f.B, f.C }).Select(c => mesh.Positions[c.V]).ToList();
        Assert.All(pts, p => Assert.Equal(3, p.Y));
        Assert.Equal(-5, pts.Min(p => p.Z));
        Assert.Equal(-4, pts.Max(p => p.Z));
        foreach (var f in top.Concat(mesh.Groups[1].Faces))
        {
            var (a, b, c) = (mesh.Positions[f.A.V], mesh.Positions[f.B.V], mesh.Positions[f.C.V]);
            var n = Vector3.Cross(b - a, c - a);
            Assert.True(n.Y > 0, "a face of an upward quad faces up in the OBJ's counter-clockwise convention");
            Assert.Equal(Vector3.UnitY, mesh.Normals[f.A.Vn]);
        }
        // a fan from the first corner, its texture coordinates with v flipped (the game's v 0.25 is OBJ's 0.75)
        Assert.Equal(new Vector2(0, 1), mesh.Uvs[top[0].A.Vt]);
        Assert.Equal(new Vector2(1, 0.75f), mesh.Uvs[top[1].B.Vt]);
        Assert.Equal("tex", top[0].Material);
    }

    [Fact]
    public void TheObjAndMtlTextNameTheMaterialsAndIndexFromOne()
    {
        var mesh = new ObjMesh();
        ModelObj.AddModel(mesh, Model(), p => p.NodeName.ToLowerInvariant(), (_, f) => ModelObj.Textured(f) ? "tex" : "flat");
        var obj = new StringWriter();
        mesh.Write(obj, "test.mtl", ["a test"]);
        var lines = obj.ToString().Split('\n');
        Assert.Equal("# a test", lines[0]);
        Assert.Equal("mtllib test.mtl", lines[1]);
        Assert.Equal(8, lines.Count(l => l.StartsWith("v ")));
        Assert.Contains("g ts1_top1", lines);
        Assert.Contains("usemtl tex", lines);
        Assert.Contains("f 1/1/1 2/2/1 3/3/1", lines);
        Assert.Equal(4, lines.Count(l => l.StartsWith("f ")));
        var mtl = new StringWriter();
        MtlWriter.Write(mtl, [
            new ObjMaterial { Name = "tex", Texture = "tex/a.png", AlphaTexture = "tex/a.png", Opacity = 0.6f },
            new ObjMaterial { Name = "flat", Colour = new Vector3(1, 0.5f, 0) },
        ]);
        var m = mtl.ToString();
        Assert.Contains("newmtl tex\nKa 0 0 0\nKd 1 1 1\nKs 0 0 0\nd 0.6\nillum 1\nmap_Kd tex/a.png\nmap_d tex/a.png", m);
        Assert.Contains("newmtl flat\nKa 0 0 0\nKd 1 0.5 0\n", m);
    }

    [Fact]
    public void ThePngEncoderWritesAValidSignatureHeaderAndChecksums()
    {
        var png = PngWriter.Encode(2, 1, [255, 0, 0, 255, 0, 255, 0, 0], alpha: true);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a }, png[..8]);
        Assert.Equal("IHDR", System.Text.Encoding.ASCII.GetString(png, 12, 4));
        Assert.Equal(6, png[25]);   // colour type RGBA
        // the IEND chunk's CRC is fixed: AE 42 60 82
        Assert.Equal(new byte[] { 0xae, 0x42, 0x60, 0x82 }, png[^4..]);
        // the image data inflates back to the filtered rows
        int idat = png.AsSpan().IndexOf("IDAT"u8);
        int len = (png[idat - 4] << 24) | (png[idat - 3] << 16) | (png[idat - 2] << 8) | png[idat - 1];
        using var z = new System.IO.Compression.ZLibStream(new MemoryStream(png, idat + 4, len), System.IO.Compression.CompressionMode.Decompress);
        var raw = new MemoryStream();
        z.CopyTo(raw);
        Assert.Equal(new byte[] { 0, 255, 0, 0, 255, 0, 255, 0, 0 }, raw.ToArray());
    }
}
