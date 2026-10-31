using System.Numerics;
using I76.Formats.Geo;
using Xunit;

namespace I76.Formats.Tests;

/// <summary>
/// The vehicle lamp faces (TLGT, BLGT, HLGT) are keyed cut-outs or translucent, which the port draws from both sides
/// unless told otherwise; the engine leaves out any face of a scene object whose plane faces away from the eye
/// (before it reads the face's mode). The 1997 look culls the lamps by their winding instead, which
/// is the same test only while every lamp face winds about its stored plane normal.
/// </summary>
public class LampFacingTests
{
    [InstallFact]
    public void EveryLampFaceWindsAboutItsPlaneNormal()
    {
        var vfs = TestInstall.Vfs;
        int faces = 0;
        foreach (var name in vfs.Names.Where(n => n.EndsWith(".geo", StringComparison.OrdinalIgnoreCase) && n.Length >= 8
                     && n[4..8].ToUpperInvariant() is "TLGT" or "BLGT" or "HLGT"))
            foreach (var f in GeoMesh.Parse(vfs.Read(name)) is var g ? g.Faces : [])
            {
                Assert.True(f.Unk_0x21 != 0 || (f.Unk_0x20 & 4) != 0, $"{name}: a lamp face that is neither cut-out nor translucent");
                // the Newell normal of the face's vertex order
                var n = Vector3.Zero;
                for (int i = 0; i < f.Vertices.Length; i++)
                {
                    var a = g.Vertices[f.Vertices[i].Index];
                    var b = g.Vertices[f.Vertices[(i + 1) % f.Vertices.Length].Index];
                    n += new Vector3((a.Y - b.Y) * (a.Z + b.Z), (a.Z - b.Z) * (a.X + b.X), (a.X - b.X) * (a.Y + b.Y));
                }
                var plane = new Vector3(f.Plane.X, f.Plane.Y, f.Plane.Z);
                Assert.True(Vector3.Dot(Vector3.Normalize(n), Vector3.Normalize(plane)) > 0.99f, $"{name} {f.Texture}: winds against its plane");
                faces++;
            }
        Assert.True(faces > 1000, $"only {faces} lamp faces");
    }
}
