using Godot;
using I76.Sim.Vehicles;
using I76.Sim.Weapons;

namespace I76.Game.Sim;

/// <summary>
/// The modern look's oil slicks: the drops of one car, close in
/// space and time, are drawn as one soft ribbon of glossy oil on GroundMarks (a Decal, which conforms to terrain,
/// roads and bridges; a mesh laid on the drawn ground under the Compatibility renderer); a lone drop is an irregular puddle. The mask, the thin-film sheen, the ORM and the ripple
/// normal are generated once, from noise. Each decal lies inside its drop's 4 x 4 m effect area (a
/// ribbon piece reaches 2 m past the drop at each end, at most 4 m wide), so the picture is where grip is
/// lost. A fixed pool of decals is reused; nothing is allocated per frame. The sim is untouched, and the
/// 1997 look keeps the original's XOS1 quads.
/// </summary>
public partial class OilSlicks : Node3D
{
    public const int MaxDecals = 160;
    /// <summary>A new segment starts only this far (m) from the car's last one.</summary>
    private const float MinSpacing = 2f;
    private const float Reach = 8f, Within = 1.5f, Spread = 0.5f, FadeOut = 4f, EffectWidth = 4f;
    private const int Variants = 3;

    private readonly Texture2D[] _ribbon = new Texture2D[Variants];
    private Texture2D _puddle = null!, _orm = null!, _normal = null!;
    private readonly Dictionary<Vehicle, Round> _last = [];
    /// <summary>The drawn ground's height under a Godot-space point at a drop's level (see GroundMark).</summary>
    public required Func<Vector3, float> GroundAt { get; init; }

    public override void _Ready()
    {
        var rnd = new Random(7601);
        for (int v = 0; v < Variants; v++) _ribbon[v] = Bake(false, rnd.Next(), out _, out _);
        _puddle = Bake(true, rnd.Next(), out _, out _);
        Bake(false, 1, out _orm, out _normal); // the ORM and the ripple are shared by every shape
    }

    private static float Hash(int x, int y, int s)
    {
        uint h = (uint)(x * 374761393 + y * 668265263 + s * 144665) ;
        h = (h ^ (h >> 13)) * 1274126177u;
        return ((h ^ (h >> 16)) & 0xffff) / 65535f;
    }

    private static float Noise(float x, float y, int s)
    {
        int xi = (int)MathF.Floor(x), yi = (int)MathF.Floor(y);
        float fx = x - xi, fy = y - yi;
        fx = fx * fx * (3 - 2 * fx); fy = fy * fy * (3 - 2 * fy);
        float a = Hash(xi, yi, s), b = Hash(xi + 1, yi, s), c = Hash(xi, yi + 1, s), d = Hash(xi + 1, yi + 1, s);
        return Mathf.Lerp(Mathf.Lerp(a, b, fx), Mathf.Lerp(c, d, fx), fy);
    }

    private static float Fbm(float x, float y, int s) => Noise(x, y, s) * 0.55f + Noise(x * 2.1f, y * 2.1f, s + 1) * 0.3f + Noise(x * 4.3f, y * 4.3f, s + 2) * 0.15f;

    /// <summary>The mask (alpha), the thin-film sheen near the edge (albedo), a ripple normal and the ORM.</summary>
    private static Texture2D Bake(bool puddle, int seed, out Texture2D orm, out Texture2D normal)
    {
        int w = 128, h = puddle ? 128 : 256;
        var alb = Image.CreateEmpty(w, h, true, Image.Format.Rgba8);
        var nrm = Image.CreateEmpty(w, h, true, Image.Format.Rgba8);
        var ormImg = Image.CreateEmpty(w, h, true, Image.Format.Rgba8);
        int s = seed & 0xffff;
        var height = new float[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float u = (x + 0.5f) / w * 2 - 1, t = (y + 0.5f) / h;
                float d, endFade = 1f;
                if (puddle)
                {
                    float v = t * 2 - 1;
                    float ang = MathF.Atan2(v, u);
                    d = MathF.Sqrt(u * u + v * v) / (0.80f + 0.14f * (Fbm(MathF.Cos(ang) * 1.6f + 3, MathF.Sin(ang) * 1.6f + 3, s) - 0.5f) * 3.2f);
                    d += (Fbm(u * 5 + 9, v * 5 + 9, s + 5) - 0.5f) * 0.22f;
                }
                else
                {
                    float hw = 0.74f + 0.2f * (Fbm(t * 3f, 0.5f, s) - 0.5f) * 2f;
                    d = MathF.Abs(u) / hw + (Fbm(u * 2 + 4, t * 5, s + 3) - 0.5f) * 0.62f;
                    float e = MathF.Min(t, 1 - t);
                    endFade = Mathf.SmoothStep(0.0f, 0.17f, e + (Fbm(u * 3, t * 9, s + 8) - 0.5f) * 0.10f);
                }
                float m = Mathf.SmoothStep(1.0f, 0.74f, d) * endFade;
                // thin film: a hue that walks with the distance from the edge, strongest in the rim
                float rim = Mathf.SmoothStep(0.25f, 0.65f, d) * (1 - Mathf.SmoothStep(0.85f, 1.0f, d));
                float hue = (d * 1.6f + Fbm(u * 2 + 20, t * 4, s + 11) * 1.2f) % 1f;
                var film = Color.FromHsv(hue, 0.5f, 0.5f);
                var baseC = new Color(0.016f, 0.012f, 0.009f);
                var c = baseC.Lerp(film, 0.2f * rim);
                alb.SetPixel(x, y, new Color(c.R, c.G, c.B, m * 0.98f));
                height[y * w + x] = Fbm(u * 6 + 40, t * (puddle ? 6 : 12) + 40, s + 21);
                ormImg.SetPixel(x, y, new Color(1f, 0.05f + 0.1f * (1 - m), 0.35f));
            }
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float hx = height[y * w + (x + 1) % w] - height[y * w + (x + w - 1) % w];
                float hy = height[((y + 1) % h) * w + x] - height[((y + h - 1) % h) * w + x];
                var n = new Vector3(-hx * 1.4f, -hy * 1.4f, 1).Normalized();
                nrm.SetPixel(x, y, new Color(n.X * 0.5f + 0.5f, n.Y * 0.5f + 0.5f, n.Z * 0.5f + 0.5f));
            }
        alb.GenerateMipmaps(); nrm.GenerateMipmaps(); ormImg.GenerateMipmaps();
        orm = ImageTexture.CreateFromImage(ormImg);
        normal = ImageTexture.CreateFromImage(nrm);
        return ImageTexture.CreateFromImage(alb);
    }

    private readonly Stack<GroundMark> _free = [];
    private int _made;

    private GroundMark? Take()
    {
        if (_free.Count > 0) return _free.Pop();
        if (_made >= MaxDecals)
        {
            // the pool is spent: the oldest slick gives up its decal, so fresh oil always shows
            Link? oldest = null;
            foreach (var l in _links.Values) if (l.Decal is not null && (oldest is null || l.Born < oldest.Born)) oldest = l;
            if (oldest?.Decal is not { } stolen) return null;
            oldest.Decal = null; oldest.Skip = true;
            stolen.Visible = false; stolen.Modulate = Colors.White;
            return stolen;
        }
        _made++;
        var d = new GroundMark { GroundAt = GroundAt, NormalFade = 0.2f, FadeBegin = 90, FadeLength = 30 };
        AddChild(d);
        d.TextureOrm = _orm;
        if (!d.Draped) d.TextureNormal = _normal;   // the draped mesh has no tangents for it
        return d;
    }

    /// <summary>What a drop was joined to when it first showed (the previous drop's place, or none), and its decal while it lives.</summary>
    private sealed class Link { public bool Joined, Skip; public Vector3 From, Pos; public int Gen, Variant, Born; public GroundMark? Decal; public bool Settled; public float Width; }
    private readonly Dictionary<Round, Link> _links = [];
    private readonly List<Round> _dead = [];
    private int _gen;

    /// <summary>One frame: the oil drops among the rounds, in the order they were dropped. Only decals that are changing are touched.</summary>
    public void Frame(IReadOnlyList<Round> rounds, bool modern, Func<System.Numerics.Vector3, Vector3> toGodot)
    {
        _last.Clear();
        _gen++;
        if (modern)
            for (int i = 0; i < rounds.Count; i++)
            {
                var r = rounds[i];
                if (r.Kind != 0xc) continue;
                _last.TryGetValue(r.Shooter, out var prev);   // the car's last drop that has a segment of its own
                if (_links.TryGetValue(r, out var known) && known.Skip) { known.Gen = _gen; continue; }
                if (!_links.TryGetValue(r, out var lk))
                {
                    // a drop within MinSpacing of the last segment's end shares it (the decals are 4 m wide): at speed a car
                    // drops two a tick, and one decal each spent the pool in seconds
                    if (prev is not null && (r.Pos - prev.Pos).Length() < MinSpacing && prev.Age - r.Age <= Within)
                    { _links[r] = new Link { Skip = true, Gen = _gen }; continue; }
                    // joined to the car's previous drop when that is near in space and time
                    lk = _links[r] = new Link { Pos = toGodot(r.Pos), Born = _gen };
                    lk.Variant = (int)((uint)(r.Pos.X * 7 + r.Pos.Z * 13) & 0x7fffffff);
                    lk.Width = EffectWidth * (0.8f + 0.2f * Hash(lk.Variant, 5, 3));
                    if (prev is not null && (r.Pos - prev.Pos).Length() <= Reach && prev.Age - r.Age <= Within && toGodot(prev.Pos) is var f && (f - lk.Pos).Length() > 0.05f)
                    { lk.Joined = true; lk.From = f; }
                }
                _last[r.Shooter] = r;
                lk.Gen = _gen;
                bool fading = r.Life < FadeOut;
                if (lk.Settled && !fading) continue;
                if (lk.Decal is null)
                {
                    lk.Decal = Take();
                    if (lk.Decal is null) continue;
                }
                var d = lk.Decal;
                float spread = Mathf.Lerp(0.8f, 1f, Mathf.Clamp(r.Age / Spread, 0, 1));
                float width = lk.Width * spread;
                if (!lk.Settled)
                {
                    int k = lk.Variant;
                    if (lk.Joined)
                    {
                        var seg = lk.Pos - lk.From; seg.Y = 0;
                        float dist = seg.Length();
                        var dir = seg / dist;
                        d.TextureAlbedo = _ribbon[k % Variants];
                        d.Place(new Basis(dir.Cross(Vector3.Up), Vector3.Up, dir), (lk.From + lk.Pos) / 2, width, (dist + EffectWidth) * (0.9f + 0.1f * spread));
                    }
                    else
                    {
                        d.TextureAlbedo = _puddle;
                        d.Place(Basis.FromEuler(new Vector3(0, Hash(k, 9, 4) * Mathf.Tau, 0)), lk.Pos, width, width);
                    }
                    d.Visible = true;
                    lk.Settled = r.Age >= Spread;
                }
                if (fading) d.Modulate = new Color(1, 1, 1, Mathf.Clamp(r.Life / FadeOut, 0, 1));
            }
        foreach (var kv in _links) if (kv.Value.Gen != _gen) _dead.Add(kv.Key);
        foreach (var r in _dead)
        {
            if (_links[r].Decal is { } d) { d.Visible = false; d.Modulate = Colors.White; _free.Push(d); }
            _links.Remove(r);
        }
        _dead.Clear();
    }
}
