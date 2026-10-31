using System.Numerics;

namespace I76.Formats.Imaging;

/// <summary>A car texture as the view renderer samples it: RGB8 texels and the cutout (transparent) key.</summary>
public sealed record CarTexture(string Member, int W, int H, byte[] Rgb, bool[] Key);

/// <summary>One textured triangle of the car: world positions, UVs (0..1 wraps), the texture's index (-1: flat colour), cutout, flat colour.</summary>
public readonly record struct CarTri(Vector3 A, Vector3 B, Vector3 C, Vector2 Ua, Vector2 Ub, Vector2 Uc, int Tex, bool Cut, Vector3 Flat);

/// <summary>An orthographic camera: looks at the object from direction <see cref="ToCamera"/>, a square frame of <see cref="Half"/>*2 world units around <see cref="Centre"/>.</summary>
public sealed record CarCamera(Vector3 ToCamera, Vector3 Centre, float Half)
{
    public Vector3 Right { get; } = BasisRight(ToCamera);
    public Vector3 Up => Vector3.Cross(Right, Vector3.Normalize(ToCamera));

    private static Vector3 BasisRight(Vector3 d)
    {
        d = Vector3.Normalize(d);
        var up = MathF.Abs(d.Y) > 0.97f ? Vector3.UnitZ : Vector3.UnitY;
        return Vector3.Normalize(Vector3.Cross(d, up)); // the models are left-handed: this keeps lettering unmirrored
    }
}

/// <summary>
/// The two passes of a view (Texture++ §3.2): the lit colour image for the segmenter, and per pixel the surface it shows —
/// texture, texel and the quality Q = |cos(view, normal)| × zoom² (zoom: the frame's scale against the whole car's, 1 for the car filling the frame).
/// </summary>
public sealed class CarView
{
    public int Size { get; }
    public byte[] Rgb { get; }
    public int[] Tex { get; }
    public ushort[] Tx { get; }
    public ushort[] Ty { get; }
    public float[] Q { get; }
    public float[] Depth { get; }

    public CarView(int size)
    {
        Size = size;
        Rgb = new byte[size * size * 3]; Tex = new int[size * size]; Tx = new ushort[size * size]; Ty = new ushort[size * size];
        Q = new float[size * size]; Depth = new float[size * size];
        Array.Fill(Tex, -1); Array.Fill(Depth, float.NegativeInfinity);
        for (int i = 0; i < Rgb.Length; i += 3) { Rgb[i] = 200; Rgb[i + 1] = 202; Rgb[i + 2] = 206; }
    }

    /// <param name="refHalf">half-frame of the view that frames the whole car; zoom = refHalf / cam.Half.</param>
    /// <param name="lit">shade the colour image by the surface normal (the segmenter sees the shapes); false: flat albedo.</param>
    public static CarView Render(IReadOnlyList<CarTri> tris, IReadOnlyList<CarTexture> textures, CarCamera cam, float refHalf, int size, bool lit = true)
    {
        var v = new CarView(size);
        float zoom = refHalf / cam.Half, q0 = zoom * zoom;
        var fwd = Vector3.Normalize(cam.ToCamera);
        var right = cam.Right; var up = cam.Up;
        var light = Vector3.Normalize(fwd * 0.75f + right * -0.3f + up * 0.6f);
        float k = size / (2 * cam.Half);
        Vector3 Scr(Vector3 p)
        {
            var d = p - cam.Centre;
            return new Vector3(size / 2f + Vector3.Dot(d, right) * k, size / 2f - Vector3.Dot(d, up) * k, Vector3.Dot(d, fwd));
        }
        foreach (var t in tris)
        {
            var n = Vector3.Cross(t.B - t.A, t.C - t.A);
            float nl = n.Length();
            if (nl < 1e-9f) continue;
            n /= nl;
            float cos = MathF.Abs(Vector3.Dot(n, fwd));
            if (cos < 1e-3f) continue;
            if (Vector3.Dot(n, fwd) < 0) n = -n;
            float shade = lit ? 0.55f + 0.45f * MathF.Max(0, Vector3.Dot(n, light)) : 1f;
            float q = cos * q0;
            var a = Scr(t.A); var b = Scr(t.B); var c = Scr(t.C);
            float den = (b.Y - c.Y) * (a.X - c.X) + (c.X - b.X) * (a.Y - c.Y);
            if (MathF.Abs(den) < 1e-9f) continue;
            int x0 = Math.Max(0, (int)MathF.Floor(MathF.Min(a.X, MathF.Min(b.X, c.X)))), x1 = Math.Min(size - 1, (int)MathF.Ceiling(MathF.Max(a.X, MathF.Max(b.X, c.X))));
            int y0 = Math.Max(0, (int)MathF.Floor(MathF.Min(a.Y, MathF.Min(b.Y, c.Y)))), y1 = Math.Min(size - 1, (int)MathF.Ceiling(MathF.Max(a.Y, MathF.Max(b.Y, c.Y))));
            var tex = t.Tex >= 0 ? textures[t.Tex] : null;
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    float px = x + 0.5f, py = y + 0.5f;
                    float l1 = ((b.Y - c.Y) * (px - c.X) + (c.X - b.X) * (py - c.Y)) / den;
                    float l2 = ((c.Y - a.Y) * (px - c.X) + (a.X - c.X) * (py - c.Y)) / den;
                    float l3 = 1 - l1 - l2;
                    if (l1 < -0.001f || l2 < -0.001f || l3 < -0.001f) continue;
                    float z = l1 * a.Z + l2 * b.Z + l3 * c.Z;
                    int pi = y * size + x;
                    if (z <= v.Depth[pi]) continue;
                    float r, g, bl; int tx = 0, ty = 0;
                    if (tex is null) { r = t.Flat.X; g = t.Flat.Y; bl = t.Flat.Z; }
                    else
                    {
                        float u = l1 * t.Ua.X + l2 * t.Ub.X + l3 * t.Uc.X, w = l1 * t.Ua.Y + l2 * t.Ub.Y + l3 * t.Uc.Y;
                        u -= MathF.Floor(u); w -= MathF.Floor(w);
                        tx = Math.Min(tex.W - 1, (int)(u * tex.W)); ty = Math.Min(tex.H - 1, (int)(w * tex.H));
                        if (t.Cut && tex.Key[ty * tex.W + tx]) continue;
                        int o = (ty * tex.W + tx) * 3;
                        r = tex.Rgb[o]; g = tex.Rgb[o + 1]; bl = tex.Rgb[o + 2];
                    }
                    v.Depth[pi] = z;
                    v.Tex[pi] = t.Tex; v.Tx[pi] = (ushort)tx; v.Ty[pi] = (ushort)ty; v.Q[pi] = tex is null ? 0 : q;
                    // the 1997 painting is flat-lit and dark: lift it so a dark car reads
                    float lift = lit ? 1.35f : 1f;
                    v.Rgb[pi * 3] = (byte)Math.Clamp(r * shade * lift, 0, 255); v.Rgb[pi * 3 + 1] = (byte)Math.Clamp(g * shade * lift, 0, 255); v.Rgb[pi * 3 + 2] = (byte)Math.Clamp(bl * shade * lift, 0, 255);
                }
        }
        return v;
    }
}
