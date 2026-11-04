using System.Numerics;
using I76.Formats.Assembly;
using I76.Formats.Geo;

namespace I76.Formats.Export;

/// <summary>
/// Assembled models into an <see cref="ObjMesh"/>, placed and wound as the game's mesh builder draws them, in a right-handed
/// Y-up frame: the game's (x, y, z) (x right, y up, z forward, left-handed) becomes (x, y, -z), so a model's forward is -Z.
/// Units are the game's, metres. A GEO face's corners, taken in their order in that mirrored frame, run counter-clockwise
/// seen from outside, which is the OBJ front; its normals point into the solid, so they are turned round. Polygons are fanned
/// from their first corner, as the game draws them. An overlay face (a lamp or a window lying on an earlier part's face, <see
/// cref="CoplanarFaces"/>) is lifted 1.2 cm off it along its outward normal, as the game does so the two do not fight.
/// </summary>
public static class ModelObj
{
    /// <summary>The lift of an overlay face off the face under it (m).</summary>
    public const float OverlayLift = 0.012f;

    public static Vector3 ToObj(Vector3 game) => new(game.X, game.Y, -game.Z);

    /// <summary>A texture coordinate in OBJ's convention (v up) from the game's (v down from the texture's first row).</summary>
    public static Vector2 UvToObj(float u, float v) => new(u, 1 - v);

    /// <summary>How a face draws: its +0x21 byte set is translucent, its +0x20 bit 2 a cut-out (index 255 keyed), else opaque.</summary>
    public enum FaceMode { Opaque, Cutout, Translucent }

    public static FaceMode ModeOf(GeoFace f) => f.Unk_0x21 != 0 ? FaceMode.Translucent : (f.Unk_0x20 & 4) != 0 ? FaceMode.Cutout : FaceMode.Opaque;

    /// <summary>Whether the face draws a texture (else its flat colour).</summary>
    public static bool Textured(GeoFace f) => f.Texture.Length > 0 && f.Unk_0x20 != 0;

    /// <summary>
    /// Adds the parts <paramref name="groupOf"/> names a group for (null leaves a part out), each part its own group; the faces
    /// <paramref name="skipFace"/> picks are left out; <paramref name="materialOf"/> names each face's material. Overlays are
    /// found among the parts added in this call. Returns the triangles added.
    /// </summary>
    public static int AddModel(ObjMesh mesh, AssembledModel model, Func<ModelPart, string?> groupOf, Func<ModelPart, GeoFace, string> materialOf,
        Func<ModelPart, GeoFace, bool>? skipFace = null, bool liftOverlays = true)
    {
        var lifted = liftOverlays ? CoplanarFaces.Overlays(model, p => groupOf(p) is not null) : [];
        int added = 0;
        for (int pi = 0; pi < model.Parts.Count; pi++)
        {
            var part = model.Parts[pi];
            if (groupOf(part) is not { } name) continue;
            var g = part.Mesh;
            var group = mesh.AddGroup(name);
            var placed = new Vector3[g.Vertices.Length];
            for (int i = 0; i < placed.Length; i++) placed[i] = ToObj(part.ToModel.Apply(g.Vertices[i]));
            var normals = new Vector3[g.Normals.Length];
            for (int i = 0; i < normals.Length; i++) normals[i] = ToObj(-part.ToModel.Rotate(g.Normals[i]));
            var index = new Dictionary<(int, bool), int>();
            foreach (var f in g.Faces)
            {
                if (f.Vertices.Length < 3 || f.Vertices.Any(v => (uint)v.Index >= placed.Length) || skipFace?.Invoke(part, f) == true) continue;
                var faceN = Unit(ToObj(-part.ToModel.Rotate(new Vector3(f.Plane.X, f.Plane.Y, f.Plane.Z))), Vector3.UnitY);
                bool lift = lifted.Contains((pi, f));
                var mat = materialOf(part, f);
                ObjMesh.Corner Corner(FaceVertex v)
                {
                    if (!index.TryGetValue((v.Index, lift), out int vi))
                        index[(v.Index, lift)] = vi = mesh.AddPosition(placed[v.Index] + (lift ? faceN * OverlayLift : Vector3.Zero));
                    var n = (uint)v.NormalIndex < normals.Length ? Unit(normals[v.NormalIndex], faceN) : faceN;
                    return new ObjMesh.Corner(vi, mesh.AddUv(UvToObj(v.U, v.V)), mesh.AddNormal(n));
                }
                var c0 = Corner(f.Vertices[0]);
                for (int k = 1; k + 1 < f.Vertices.Length; k++)
                {
                    group.Faces.Add(new ObjMesh.Face(mat, c0, Corner(f.Vertices[k]), Corner(f.Vertices[k + 1])));
                    added++;
                }
            }
        }
        return added;
    }

    /// <summary>
    /// Adds synthesised faces (<see cref="SynthFace"/>: game-frame corners, wound like GEO faces, normals already outward),
    /// moved by <paramref name="offset"/> (game frame), as one group.
    /// </summary>
    public static int AddSynth(ObjMesh mesh, string group, IEnumerable<SynthFace> faces, string material, Vector3 offset = default)
    {
        var g = mesh.AddGroup(group);
        int added = 0;
        foreach (var f in faces)
        {
            if (f.Corners.Length < 3) continue;
            var cs = new ObjMesh.Corner[f.Corners.Length];
            for (int i = 0; i < cs.Length; i++)
                cs[i] = new ObjMesh.Corner(mesh.AddPosition(ToObj(f.Corners[i] + offset)), mesh.AddUv(UvToObj(f.Uvs[i].X, f.Uvs[i].Y)),
                    mesh.AddNormal(Unit(ToObj(f.Normals[i]), Vector3.UnitY)));
            for (int k = 1; k + 1 < cs.Length; k++) { g.Faces.Add(new ObjMesh.Face(material, cs[0], cs[k], cs[k + 1])); added++; }
        }
        return added;
    }

    /// <summary>A unit vector, or the fallback where it is zero or not a number (some GEOs carry zero normals).</summary>
    private static Vector3 Unit(Vector3 n, Vector3 fallback)
    {
        float l = n.Length();
        if (l > 0.5f && float.IsFinite(l)) return Round(n / l);
        l = fallback.Length();
        return l > 0.5f && float.IsFinite(l) ? Round(fallback / l) : Vector3.UnitY;
    }

    /// <summary>Normals rounded to five places, so equal ones share an index.</summary>
    private static Vector3 Round(Vector3 n) => new(MathF.Round(n.X, 5), MathF.Round(n.Y, 5), MathF.Round(n.Z, 5));
}
