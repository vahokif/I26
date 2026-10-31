using I76.Formats.Imaging;
using System.Numerics;
using I76.Formats.Assembly;

namespace I76.Cli;

/// <summary>A tiny software renderer for the respray checks: the car's body parts, textured, flat-lit, orthographic, z-buffered.</summary>
public static class ResprayRender
{
    /// <summary>
    /// Draws the model's own parts (node names of the car's object code) with the textures <paramref name="tex"/> gives for each resolved member,
    /// from <paramref name="yawDeg"/> (0 = from the front, 90 = from the car's left) and a fixed 22° down.
    /// </summary>
    public static byte[] Render(ModelSchemes ms, string vtf, Func<string, ResprayAnalysis.Rgb?> tex, TextureResolver res, float yawDeg, int W, int H, float zoom = 1f)
    {
        var model = ms.Model;
        string code = model.ObjectCode ?? "";
        var yaw = Matrix4x4.CreateRotationY(yawDeg * MathF.PI / 180) * Matrix4x4.CreateRotationX(22 * MathF.PI / 180);
        var tris = new List<(Vector3[] P, Vector2[] Uv, string? Member, bool Cut, Vector3 Flat, float Shade)>();
        Vector3 lo = new(1e9f), hi = new(-1e9f);
        foreach (var part in model.Parts)
        {
            if (!part.NodeName.StartsWith(code, StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var f in part.Mesh.Faces)
            {
                var pts = f.Vertices.Where(v => (uint)v.Index < part.Mesh.Vertices.Length).Select(v => Vector3.Transform(part.ToModel.Apply(part.Mesh.Vertices[v.Index]), yaw)).ToArray();
                if (pts.Length != f.Vertices.Length || pts.Length < 3) continue;
                string? member = null;
                if (f.Texture.Length > 0 && f.Unk_0x20 != 0 && ms.Resolved[vtf].TryGetValue(f.Texture, out var m)) member = m;
                var n = Vector3.Normalize(Vector3.Cross(pts[1] - pts[0], pts[2] - pts[0]));
                if (float.IsNaN(n.X)) n = Vector3.UnitZ;
                if (n.Z < 0) n = -n; // both sides draw, lit from the viewer's side
                float shade = 0.55f + 0.45f * MathF.Max(0, Vector3.Dot(n, Vector3.Normalize(new Vector3(-0.3f, 0.6f, 0.75f))));
                tris.Add((pts, f.Vertices.Select(v => new Vector2(v.U, v.V)).ToArray(), member, (f.Unk_0x20 & 4) != 0, new Vector3(f.R, f.G, f.B), shade));
                foreach (var p in pts) { lo = Vector3.Min(lo, p); hi = Vector3.Max(hi, p); }
            }
        }
        var img = new byte[W * H * 3];
        for (int i = 0; i < img.Length; i += 3) { img[i] = 226; img[i + 1] = 228; img[i + 2] = 232; }
        if (tris.Count == 0) return img;
        float sc = MathF.Min((W - 8) / (hi.X - lo.X), (H - 8) / (hi.Y - lo.Y)) * zoom;
        float cx = (hi.X + lo.X) / 2, cy = (hi.Y + lo.Y) / 2;
        var zb = new float[W * H]; Array.Fill(zb, float.NegativeInfinity);
        Vector3 Scr(Vector3 p) => new(W / 2f + (p.X - cx) * sc, H / 2f - (p.Y - cy) * sc, p.Z);
        foreach (var (pts, uvs, member, cut, flat, shade) in tris)
        {
            var rgb = member is null ? null : tex(member);
            for (int k = 1; k + 1 < pts.Length; k++)
            {
                var a = Scr(pts[0]); var b = Scr(pts[k]); var c = Scr(pts[k + 1]);
                float den = (b.Y - c.Y) * (a.X - c.X) + (c.X - b.X) * (a.Y - c.Y);
                if (MathF.Abs(den) < 1e-6f) continue;
                int x0 = Math.Max(0, (int)MathF.Floor(MathF.Min(a.X, MathF.Min(b.X, c.X)))), x1 = Math.Min(W - 1, (int)MathF.Ceiling(MathF.Max(a.X, MathF.Max(b.X, c.X))));
                int y0 = Math.Max(0, (int)MathF.Floor(MathF.Min(a.Y, MathF.Min(b.Y, c.Y)))), y1 = Math.Min(H - 1, (int)MathF.Ceiling(MathF.Max(a.Y, MathF.Max(b.Y, c.Y))));
                for (int y = y0; y <= y1; y++)
                    for (int x = x0; x <= x1; x++)
                    {
                        float px = x + 0.5f, py = y + 0.5f;
                        float l1 = ((b.Y - c.Y) * (px - c.X) + (c.X - b.X) * (py - c.Y)) / den;
                        float l2 = ((c.Y - a.Y) * (px - c.X) + (a.X - c.X) * (py - c.Y)) / den;
                        float l3 = 1 - l1 - l2;
                        if (l1 < -0.01f || l2 < -0.01f || l3 < -0.01f) continue;
                        float z = l1 * a.Z + l2 * b.Z + l3 * c.Z;
                        if (z <= zb[y * W + x]) continue;
                        float r, g, bl;
                        if (rgb is null) { r = flat.X; g = flat.Y; bl = flat.Z; }
                        else
                        {
                            float u = l1 * uvs[0].X + l2 * uvs[k].X + l3 * uvs[k + 1].X, v = l1 * uvs[0].Y + l2 * uvs[k].Y + l3 * uvs[k + 1].Y;
                            u -= MathF.Floor(u); v -= MathF.Floor(v);
                            int tx = Math.Min(rgb.W - 1, (int)(u * rgb.W)), ty = Math.Min(rgb.H - 1, (int)(v * rgb.H));
                            if (cut && rgb.Key[ty * rgb.W + tx]) continue;
                            int o = (ty * rgb.W + tx) * 3;
                            r = rgb.Data[o]; g = rgb.Data[o + 1]; bl = rgb.Data[o + 2];
                        }
                        zb[y * W + x] = z;
                        int d = (y * W + x) * 3;
                        // the painting is flat-lit and dark; lift it so a dark car reads, light it from the side
                        img[d] = (byte)Math.Clamp(r * shade * 1.35f, 0, 255); img[d + 1] = (byte)Math.Clamp(g * shade * 1.35f, 0, 255); img[d + 2] = (byte)Math.Clamp(bl * shade * 1.35f, 0, 255);
                    }
            }
        }
        return img;
    }
}
