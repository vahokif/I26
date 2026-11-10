using System.Collections.Concurrent;
using System.Numerics;
using I76.Formats.Imaging;
using I76.Formats.Mission;
using I76.Formats.Vfs;

namespace I76.Formats.Assembly;

/// <summary>
/// The ground past a map's tiles, the same for the simulation and for drawing. The original reads height 0 there (a sample
/// off every tile is 0, so the last cell of the map falls to 0 and a plain at 0 runs on), and draws nothing: past its 600 m clip
/// stand the horizon panels. Here that ground is kept and drawn, and far out it rises into the modern look's distant terrain:
///
/// <list type="bullet">
/// <item>Within <see cref="Plain"/> of the map's bounds (and in every empty cell inside them) it is the original's plain at 0. Off a high
/// edge (one more than a car could drive down in the original's last cell, <see cref="DropFrom"/>) every sample is 0, so the map's last
/// cell falls to 0 as it does there and a car that drives off drops as it does there. Off a low edge (up to <see cref="Carry"/>, most of
/// the stock maps' edges) the ground carries on at the edge's own height and eases down to the plain's over a few hundred metres, so
/// there is no step, dip or ditch at the edge; between the two, part of each.</item>
/// <item>Past that the procedural terrain rises from the plain, gently: the mountain ring made from the mission's horizon panels (their
/// skyline cut into features at its peaks, each revolved about its peak and stood at its bearing so that from the map's centre it fills
/// the angle the panel did), on a base that rises over <see cref="BaseRise"/> to the map's middle rim height, with foothills, ridged
/// ranges far out and a little roughness, sunk as the panels sink (the cone through the panels' foot for one reference eye per map,
/// <see cref="HorizonEdge.Ring"/>).</item>
/// </list>
///
/// The simulation reads it as samples on the map's own 5 m grid (<see cref="Raw"/>, tenths of a metre as the map's samples are,
/// unbounded), bilinear between them as on the map; the modern look draws those samples near the camera and the function itself
/// in its distant ring. Deterministic: integer hashing, IEEE arithmetic and <see cref="DetMath"/> only, so every machine computes
/// the same samples (a network game's host and clients) from the map and its panels.
/// </summary>
public sealed class OutsideGround
{
    /// <summary>
    /// Metres from the map's bounds over which the ground is the original's plain at <see cref="Bottom"/> (the 600 m clip: from anywhere
    /// on the map the 1997 look sees only the plain). Custom maps are made for the original's drop and plain; nothing rises in reach of them.
    /// </summary>
    public const float Plain = 600f;
    /// <summary>The plain's height, metres: the original's (every sample off the tiles reads 0).</summary>
    public const float Bottom = 0f;
    /// <summary>An edge up to this many metres over the plain carries on at its own height (no step), easing down to the plain's.</summary>
    public const float Carry = 2.5f;
    /// <summary>
    /// An edge this many metres or more over the plain falls to it in its last cell, as in the original (a cell falling 3.65 m is the
    /// steepest a car drives on, TerrainSurface.WallGradient); between <see cref="Carry"/> and this, the carried height goes to 0.
    /// </summary>
    public const float DropFrom = 4f;
    /// <summary>Metres from the map's last samples over which a low edge's carried height eases down to the plain's.</summary>
    public const float CarryFadeStart = 60f, CarryFadeEnd = 400f;
    /// <summary>Edge tiles within this many metres of the nearest share in the carried edge height (a concave corner has two).</summary>
    public const float Feather = 60f;
    /// <summary>Metres past the gentle band over which the distant terrain's base rises to the map's rim height.</summary>
    public const float BaseRise = 2500f;
    /// <summary>Metres past the gentle band over which the distant terrain sinks onto the cone through the panels' foot.</summary>
    public const float SinkFade = 3000f;
    /// <summary>Metres past the plain with only gentle ground on it (foothills, roughness): the base, its sinking and the mountains come beyond.</summary>
    public const float Gentle = 500f;
    /// <summary>Samples a side of a cached block.</summary>
    public const int Block = 32;
    /// <summary>Changes with anything that changes the function.</summary>
    public const int Version = 2;

    private const float RefRadius = HorizonList.Radius;
    private const float TileW = TerrainTiles.Tile * 5f;
    private const float Tau = 6.28318548f;

    /// <summary>One point of the function: the height and what the modern look shades it by (height over the base, distance from the map, rockiness, 0 peaks .. 1 mesas).</summary>
    public readonly record struct Point(float H, float Hrel, float D, float Rocky, float Style);

    private readonly TerrainTiles _tiles;
    private readonly Vector2 _centre;
    private readonly (float X0, float Z0, float X1, float Z1)[] _edge;    // sample rects of the tiles beside an empty cell, centre-relative
    private readonly (float X0, float Z0, float X1, float Z1)[] _holes;   // the empty cells inside the bounds, centre-relative
    private readonly (float X0, float Z0, float X1, float Z1) _bounds;
    private readonly float _base, _meanRef;
    private readonly List<Feature> _features = [];
    private List<Feature>[] _grid = [];
    private float _gridMin, _gridCell;
    private int _gridN;
    private readonly float _sinkLow, _sinkEye, _footK;
    private readonly ConcurrentDictionary<long, int[]> _blocks = new();

    /// <summary>The map's tiles' centre, game frame XZ: the ring's centre, from which bearings and the features are measured.</summary>
    public Vector2 Centre => _centre;
    /// <summary>The farthest corner of the map's tiles from <see cref="Centre"/>, metres.</summary>
    public float FarCorner { get; }
    /// <summary>The panels' skyline, metres over their foot by column.</summary>
    public float[] Skyline { get; }
    /// <summary>The middle of the map's rim height by bearing: the distant terrain's base.</summary>
    public float Base => _base;
    /// <summary>The ring's sinking (<see cref="HorizonEdge.Ring"/>) and the cone's slope.</summary>
    public HorizonEdge.RingSink Sink => new(_sinkLow, _sinkEye);
    public float FootK => _footK;
    /// <summary>Names everything the function is made from (for caches of what is drawn from it).</summary>
    public string Key { get; }

    private sealed class Feature
    {
        public float X, Z;          // centre-relative
        public float Scale;         // world metres per reference metre
        public float ColW;          // world metres per profile column
        public float[] F = [];      // reference height by lateral distance in columns, falling to 0
        public float Reach;         // world radius past which it is 0 however the outline is warped
        public float Style;         // 0 peaks .. 1 mesas
        public float Warp;          // world metres the outline wanders
    }

    /// <summary>The outside ground for a mission: its panels' skyline (or rolling ranges when it has none), null for a map too big for the ring (it keeps the original's ground).</summary>
    public static OutsideGround? For(AssetVfs vfs, MissionWorld world)
    {
        if (world.Terrain.Huge) return null;
        var (r0, r1, _, _) = world.Terrain.Bounds();
        if (r1 < r0) return null;
        return new OutsideGround(world, ReadSkyline(vfs, world) ?? DefaultSkyline(), HorizonEdge.Ring(world));
    }

    /// <summary>The mission's horizon panels' skyline (<see cref="SkylineOf"/>), or null when it names none or one is missing.</summary>
    public static float[]? ReadSkyline(AssetVfs vfs, MissionWorld world)
    {
        var settings = WorldSettings.Read(world.Mission);
        if (settings is not { Horizon.Length: > 0 } || HorizonList.Parse(vfs.TryRead(settings.Horizon)) is not { } names) return null;
        var resolver = new TextureResolver(vfs);
        var panels = new IndexedTexture[names.Length];
        for (int i = 0; i < names.Length; i++)
            if (resolver.FindByStem(names[i]) is not { } m || resolver.Load(m) is not { } t) return null;
            else panels[i] = t;
        return SkylineOf(panels);
    }

    /// <summary>
    /// Each panel column's silhouette (its highest opaque texel; the art is stored foot first) as metres above the panels' foot
    /// at radius 6000, less the band of scrub drawn along every foot (as high as the lowest columns, up to an eighth of the panel).
    /// Column c looks along bearing (c + 0.5)/N · 360° from game +Z toward +X. A panel of another size than the first is read
    /// at the first's size (nearest column, rows scaled).
    /// </summary>
    public static float[] SkylineOf(IReadOnlyList<IndexedTexture> panels)
    {
        int w = panels[0].Width, h = panels[0].Height, n = w * panels.Count;
        var top = new int[n];
        for (int c = 0; c < n; c++)
        {
            var p = panels[c / w];
            int x = (c % w) * p.Width / w, t = 0;
            for (int y = p.Height - 1; y >= 0; y--) if (p.Indices[y * p.Width + x] != TextureReader.TransparentIndex) { t = y + 1; break; }
            top[c] = p.Height == h ? t : (int)Math.Round(t * (double)h / p.Height);
        }
        var sorted = (int[])top.Clone();
        Array.Sort(sorted);
        int scrub = Math.Min(sorted[n / 50], h / 8);
        var height = new float[n];
        for (int c = 0; c < n; c++) height[c] = Math.Max(top[c] - scrub, 0) / (float)h * HorizonList.PanelHeight;
        return height;
    }

    /// <summary>A skyline for a mission without panels: rolling ranges.</summary>
    public static float[] DefaultSkyline()
    {
        const int n = 2048;
        var h = new float[n];
        for (int c = 0; c < n; c++)
        {
            var (s, co) = DetMath.SinCos(c / (double)n * 2 * DetMath.Pi);
            h[c] = 120 + 260 * Fbm((float)co * 6 + 40, (float)s * 6 + 40, 4);
        }
        return h;
    }

    public OutsideGround(MissionWorld world, float[] skyline, HorizonEdge.RingSink sink)
    {
        _tiles = world.Terrain;
        Skyline = skyline;
        (_sinkLow, _sinkEye) = (sink.Low, sink.EyeY);
        _footK = -Lighting.HorizonFoot(1f) / HorizonList.Radius;
        var (r0, r1, c0, c1) = _tiles.Bounds();
        _centre = new Vector2((c0 + c1 + 1) * 0.5f * TileW, (r0 + r1 + 1) * 0.5f * TileW);
        var edge = new List<(float, float, float, float)>();
        var holes = new List<(float, float, float, float)>();
        float far = 0;
        for (int r = r0; r <= r1; r++)
            for (int c = c0; c <= c1; c++)
            {
                float x0 = c * TileW - _centre.X, z0 = r * TileW - _centre.Y;
                if (_tiles.TileAt(r, c) == MissionFile.ZmapEmpty) { holes.Add((x0, z0, x0 + TileW, z0 + TileW)); continue; }
                far = MathF.Max(far, MathF.Max(MathF.Max(Len(x0, z0), Len(x0 + TileW, z0)), MathF.Max(Len(x0, z0 + TileW), Len(x0 + TileW, z0 + TileW))));
                bool beside = false;
                for (int dr = -1; dr <= 1 && !beside; dr++)
                    for (int dc = -1; dc <= 1 && !beside; dc++) beside = !Occupied(r + dr, c + dc);
                // the tile's samples: its last row and column are 5 m in from the next cell
                if (beside) edge.Add((x0, z0, x0 + TileW - 5, z0 + TileW - 5));
            }
        _edge = [.. edge];
        _holes = [.. holes];
        _bounds = (c0 * TileW - _centre.X, r0 * TileW - _centre.Y, (c1 + 1) * TileW - _centre.X, (r1 + 1) * TileW - _centre.Y);
        FarCorner = far;

        // the rim: along each bearing, the last ground inside the map, as high as the wall tops just inside it
        const int nb = 1024;
        var rim = new float[nb];
        for (int b = 0; b < nb; b++)
        {
            var (s, co) = DetMath.SinCos((b + 0.5) / nb * 2 * DetMath.Pi);
            float sx = (float)s, sz = (float)co;
            float last = -1;
            for (float t = 0; t < far + 10; t += 10) if (Inside(sx * t, sz * t)) last = t;
            float top = 0;
            if (last >= 0)
                for (float t = MathF.Max(0, last - 60); t <= last; t += 5)
                    top = MathF.Max(top, (float)world.BilinearHeight(_centre.X + sx * t, _centre.Y + sz * t));
            rim[b] = top;
        }
        var smooth = new float[nb];
        for (int b = 0; b < nb; b++)
        {
            float s = 0;
            for (int d = -8; d <= 8; d++) s += rim[(b + d + nb) % nb];
            smooth[b] = s / 17;
        }
        var order = smooth;
        Array.Sort(order);
        _base = order[nb / 2];
        double sum = 0;
        foreach (var v in skyline) sum += v;
        _meanRef = (float)(sum / skyline.Length);
        PlaceFeatures(skyline, far);

        using var k = new BakeCache.Key("outside-ground");
        k.Add(Version).Add((long)_tiles.ContentHash()).Add<float>(skyline).Add(_sinkLow).Add(_sinkEye);
        Key = k.Finish();
    }

    private bool Occupied(int r, int c) => r >= 0 && c >= 0 && r < MissionFile.ZmapGrid && c < MissionFile.ZmapGrid && _tiles.TileAt(r, c) != MissionFile.ZmapEmpty;

    /// <summary>Metres from a centre-relative point to the map's bounds (the rectangle of its tiles); 0 inside them.</summary>
    private float BoundsDistance(float x, float z)
    {
        var (x0, z0, x1, z1) = _bounds;
        float dx = MathF.Max(MathF.Max(x0 - x, x - x1), 0), dz = MathF.Max(MathF.Max(z0 - z, z - z1), 0);
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    /// <summary>Whether a centre-relative point is over one of the map's tiles (a cell of the grid with a tile in it).</summary>
    private bool Inside(float x, float z) =>
        Occupied((int)MathF.Floor((z + _centre.Y) / TileW), (int)MathF.Floor((x + _centre.X) / TileW));

    // ---- the simulation's samples ----

    /// <summary>
    /// The ground at a sample of the map's grid (global sample coordinates, any), in tenths of a metre as the map's own samples:
    /// the map's height where it has a sample, else the function at the sample's point, rounded. Made a block at a time and kept.
    /// </summary>
    public int Raw(int sx, int sz)
    {
        if (_tiles.Sample(sx, sz) is { } v) return v & MissionFile.HeightMask;
        int bx = sx >> 5, bz = sz >> 5;
        var block = _blocks.GetOrAdd(((long)bx << 32) ^ (uint)bz, _ => MakeBlock(bx, bz));
        return block[(sz & (Block - 1)) * Block + (sx & (Block - 1))];
    }

    /// <summary>Blocks made so far (the simulation's and the drawing's queries share them).</summary>
    public int Blocks => _blocks.Count;

    private int[] MakeBlock(int bx, int bz)
    {
        var a = new int[Block * Block];
        for (int j = 0; j < Block; j++)
            for (int i = 0; i < Block; i++)
            {
                int sx = bx * Block + i, sz = bz * Block + j;
                a[j * Block + i] = _tiles.Sample(sx, sz) is { } v ? v & MissionFile.HeightMask : Quantize(SampleAt(sx, sz).H);
            }
        return a;
    }

    private static int Quantize(float h) => (int)Math.Round(h * 10.0);

    /// <summary>The function at a sample's point (global sample coordinates).</summary>
    public Point SampleAt(int sx, int sz) => At((float)(sx * 5.0 - _centre.X), (float)(sz * 5.0 - _centre.Y));

    /// <summary>The function at a game point.</summary>
    public Point AtGame(double x, double z) => At((float)(x - _centre.X), (float)(z - _centre.Y));

    // ---- the function ----

    /// <summary>
    /// The function at a centre-relative point. Over a tile (where only the distant ring asks) it lies just under the plain round the
    /// map, deeper further in, so under the map's own ground (which is never lower): a cell of the ring that straddles the edge neither
    /// dips into a trench outside nor stands out of a high edge's fall.
    /// </summary>
    public Point At(float x, float z)
    {
        if (Inside(x, z))
        {
            float din = InsideDistance(x, z);
            return new Point(Bottom - 3 + 2 * MathF.Min(30 - din, 0), 0, -din, 0, 0);
        }
        float d = EdgeDistance(x, z);
        // the original's plain: nothing of the distant terrain reaches in
        float t = BoundsDistance(x, z) - Plain;
        if (t <= 0) return new Point(Bottom + Carried(x, z, d), 0, d, 0, 0);
        float r = Len(x, z);
        float baseH = Bottom + (_base - Bottom) * SmoothStep(Gentle, Gentle + BaseRise, t);
        // the features: the highest of them here
        float best = 0, style = 0, rock = 0;
        // the noise the features share, made once here: a warp field (two scales) and ridges
        bool noise = false;
        float wx = 0, wz = 0, ridged = 0;
        if (_gridN > 0)
            foreach (var f in _grid[Cell(z) * _gridN + Cell(x)])
            {
                float dx = x - f.X, dz = z - f.Z;
                if (dx * dx + dz * dz > f.Reach * f.Reach) continue;
                if (!noise)
                {
                    noise = true;
                    wx = (Fbm(x / 2200 + 11.3f, z / 2200, 3) - 0.5f) * 1.4f + (Fbm(x / 650, z / 650 + 5.1f, 3) - 0.5f) * 0.9f;
                    wz = (Fbm(x / 2200 + 31.7f, z / 2200 + 3.9f, 3) - 0.5f) * 1.4f + (Fbm(x / 650 + 8.7f, z / 650, 3) - 0.5f) * 0.9f;
                    ridged = Ridged(x / 450, z / 450, 4);
                }
                // the outline wanders by the warp, scaled to the feature
                float rho = Len(dx + wx * f.Warp, dz + wz * f.Warp) / f.ColW;
                int k = (int)rho;
                if (k >= f.F.Length - 1) continue;
                float hRef = f.F[k] + (f.F[k + 1] - f.F[k]) * (rho - k);
                float h = hRef * f.Scale;
                if (h <= 0) continue;
                // pointed shapes take ridges and gullies; flat-topped ones keep their tops and get a talus foot
                float frac = hRef / MathF.Max(f.F[0], 1);
                h *= 1 + (1 - f.Style) * (ridged - 0.55f) * 0.5f * MathF.Sqrt(frac);
                h *= 1 + f.Style * (ridged - 0.5f) * 0.12f * (1 - frac);
                if (h > best) { best = h; style = f.Style; rock = frac; }
            }
        // the ground between: rolling foothills near, ridged ranges farther out
        float near = SmoothStep(250, 1800, t);
        float hills = 35 * Fbm(x / 700, z / 700, 4) * near;
        float far = SmoothStep(3000, 12000, r);
        float ranges = far > 0 ? _meanRef * 0.55f * (r / RefRadius) * DetMath.Pow(Ridged(x / 3200 + 7.3f, z / 3200 - 1.9f, 5), 2.2f) * far : 0;
        float bg = hills + ranges;
        // the mountains (steep: cliffs, mesa walls) only past the gentle rise, the ground between from the plain's end
        best *= SmoothStep(Gentle, Gentle + 1000, t);
        float rise = SmoothStep(80, 900, t);
        float hrel = SmoothMax(best, bg, 30) * rise;
        // a little wind-blown roughness everywhere
        hrel += 6 * (Fbm(x / 160, z / 160, 3) - 0.5f) * SmoothStep(100, 500, t);
        float rocky = MathF.Max(rock, Math.Clamp(bg / 250, 0, 1));
        // sunk onto the cone through the panels' foot for the map's reference eye, fading in from the plain
        float cone = _sinkLow + (_sinkEye - _sinkLow) * (1 - _footK * r);
        float sink = _footK > 0 ? SmoothStep(Gentle, Gentle + SinkFade, t) * MathF.Min(cone - baseH, 0) : 0;
        return new Point(baseH + hrel + sink, hrel, d, rocky, style);
    }

    /// <summary>Metres from a centre-relative point outside the map to the nearest sample of the map.</summary>
    public float EdgeDistance(float x, float z)
    {
        float best = float.MaxValue;
        foreach (var (x0, z0, x1, z1) in _edge)
        {
            float dx = MathF.Max(MathF.Max(x0 - x, x - x1), 0), dz = MathF.Max(MathF.Max(z0 - z, z - z1), 0);
            best = MathF.Min(best, dx * dx + dz * dz);
        }
        return best == float.MaxValue ? 0 : MathF.Sqrt(best);
    }

    /// <summary>Over a tile: the distance to the nearest ground that is not the map's (the empty cells, and past its bounds).</summary>
    private float InsideDistance(float x, float z)
    {
        var (bx0, bz0, bx1, bz1) = _bounds;
        float best = MathF.Min(MathF.Min(x - bx0, bx1 - x), MathF.Min(z - bz0, bz1 - z));
        foreach (var (x0, z0, x1, z1) in _holes)
        {
            float dx = MathF.Max(MathF.Max(x0 - x, x - x1), 0), dz = MathF.Max(MathF.Max(z0 - z, z - z1), 0);
            best = MathF.Min(best, MathF.Sqrt(dx * dx + dz * dz));
        }
        return MathF.Max(best, 0);
    }

    /// <summary>
    /// What a low edge carries onto the plain at a centre-relative point <paramref name="d"/> from the map: the edge's height there
    /// (<see cref="EdgeHeight"/>) up to <see cref="Carry"/>, going to 0 for an edge of <see cref="DropFrom"/> or more, eased out from
    /// <see cref="CarryFadeStart"/> to <see cref="CarryFadeEnd"/>. 0 exactly off a high edge.
    /// </summary>
    private float Carried(float x, float z, float d)
    {
        if (d >= CarryFadeEnd) return 0;
        float e = EdgeHeight(x, z, d) - Bottom;
        if (e >= DropFrom || e <= 0) return 0;
        return MathF.Min(e, Carry) * (1 - SmoothStep(Carry, DropFrom, e)) * (1 - SmoothStep(CarryFadeStart, CarryFadeEnd, d));
    }

    /// <summary>
    /// The map's own height carried past its edge: from each edge tile within <see cref="Feather"/> of the nearest, the height at its
    /// nearest sample point, weighted by the inverse fourth power of the distance (less its value at the cut-off, so a tile comes in from
    /// nothing): the nearest tile's own height right at the edge, a smooth mix where two edges meet (a concave corner).
    /// </summary>
    private float EdgeHeight(float x, float z, float d)
    {
        double sw = 0, se = 0;
        double cut = 1 / Pow4(d + Feather);
        foreach (var t in _edge)
        {
            float qx = Math.Clamp(x, t.X0, t.X1), qz = Math.Clamp(z, t.Z0, t.Z1);
            float ex = x - qx, ez = z - qz, dk = MathF.Sqrt(ex * ex + ez * ez);
            if (dk >= d + Feather) continue;
            double w = 1 / Pow4(MathF.Max(dk, 0.01f)) - cut;
            if (w <= 0) continue;
            sw += w; se += w * TileHeight(t, qx, qz);
        }
        return sw > 0 ? (float)(se / sw) : 0;
    }

    private static double Pow4(double v) { v *= v; return v * v; }

    /// <summary>The map's bilinear height at a centre-relative point clamped into one tile's samples, from that tile's samples alone.</summary>
    private float TileHeight((float X0, float Z0, float X1, float Z1) t, float x, float z)
    {
        double fx = (_centre.X + (double)Math.Clamp(x, t.X0, t.X1)) * 0.2, fz = (_centre.Y + (double)Math.Clamp(z, t.Z0, t.Z1)) * 0.2;
        int lx = (int)Math.Round((_centre.X + (double)t.X0) * 0.2), lz = (int)Math.Round((_centre.Y + (double)t.Z0) * 0.2);
        int sx = Math.Clamp((int)Math.Floor(fx), lx, lx + TerrainTiles.Tile - 2), sz = Math.Clamp((int)Math.Floor(fz), lz, lz + TerrainTiles.Tile - 2);
        double tx = Math.Clamp(fx - sx, 0, 1), tz = Math.Clamp(fz - sz, 0, 1);
        double S(int a, int b) => (_tiles.Sample(a, b) ?? 0) & MissionFile.HeightMask;
        double a0 = S(sx, sz) + (S(sx + 1, sz) - S(sx, sz)) * tx;
        double a1 = S(sx, sz + 1) + (S(sx + 1, sz + 1) - S(sx, sz + 1)) * tx;
        return (float)((a0 + (a1 - a0) * tz) * MissionWorld.HeightScale);
    }

    /// <summary>Places the skyline's features: a near ring at the panels' own distance and a farther one behind.</summary>
    private void PlaceFeatures(float[] e, float farCorner)
    {
        int n = e.Length;
        float colRef = Tau * RefRadius / n;   // metres per column at the panels' radius
        var s = new float[n];
        for (int c = 0; c < n; c++)
        {
            float acc = 0;
            for (int d = -2; d <= 2; d++) acc += e[(c + d + n) % n];
            s[c] = acc / 5;
        }
        var peaks = new List<(int C, float Prom)>();
        for (int c = 0; c < n; c++)
        {
            bool max = s[c] > 15;
            for (int d = 1; d <= 3 && max; d++) max = s[c] >= s[(c + d) % n] && s[c] > s[(c - d + n) % n];
            if (!max) continue;
            float minL = s[c], minR = s[c];
            for (int d = 1; d < n / 2; d++) { float v = s[(c - d + n) % n]; if (v > s[c]) break; minL = MathF.Min(minL, v); }
            for (int d = 1; d < n / 2; d++) { float v = s[(c + d) % n]; if (v > s[c]) break; minR = MathF.Min(minR, v); }
            float prom = s[c] - MathF.Max(minL, minR);
            if (prom >= MathF.Max(20, 0.1f * s[c])) peaks.Add((c, prom));
        }
        if (peaks.Count == 0)
        {
            int at = 0;
            for (int c = 1; c < n; c++) if (e[c] > e[at]) at = c;
            peaks.Add((at, e[at]));
        }
        var (s40, c40) = DetMath.SinCos(40 * DetMath.Pi / 180);
        float tan40 = (float)(s40 / c40);
        var shapes = new List<(int C, float[] F, float Style)>();
        foreach (var (c, prom) in peaks)
        {
            float tol = MathF.Max(12, 0.25f * prom);
            float[] Side(int dir)
            {
                var g = new List<float>();
                float peak = 0;
                for (int d = -1; d <= 1; d++) peak = MathF.Max(peak, e[(c + d + n) % n]);
                float m = peak;
                g.Add(m);
                for (int k = 1; k < n / 4; k++)
                {
                    float v = e[((c + dir * k) % n + n) % n];
                    if (v > m + tol || m <= 0) break;
                    m = MathF.Min(m, v);
                    g.Add(m);
                }
                // past the walk: down to the ground at about 40°
                float last = g[^1];
                int tail = (int)MathF.Ceiling(last / tan40 / colRef);
                for (int k = 1; k <= tail; k++) g.Add(last * (1 - k / (float)tail) * (1 - k / (float)tail));
                g.Add(0);
                return g.ToArray();
            }
            var l = Side(-1); var r = Side(1);
            var f = new float[Math.Max(l.Length, r.Length)];
            for (int k = 0; k < f.Length; k++) f[k] = MathF.Max(k < l.Length ? l[k] : 0, k < r.Length ? r[k] : 0);
            // flat-topped (a mesa, a butte) or pointed: its width at 85% of its height against at 40%
            float W(float frac) { int k = 0; while (k < f.Length && f[k] >= f[0] * frac) k++; return k; }
            float style = Math.Clamp((W(0.85f) / MathF.Max(W(0.4f), 1) - 0.25f) / 0.45f, 0, 1);
            shapes.Add((c, f, style));
        }

        var rng = new Random(12345 + n);
        void Ring(float distance, float bearingShift, float heightScale, float jitter)
        {
            foreach (var (c, f, style) in shapes)
            {
                var (sn, cs) = DetMath.SinCos(((c + 0.5f) / n + bearingShift) * Tau);
                float sx = (float)sn, sz = (float)cs;
                float edge = 0;
                for (float t = 0; t < farCorner + 10; t += 20) if (Inside(sx * t, sz * t)) edge = t;
                float rad = MathF.Max(distance, edge + (2200 + Plain) * distance / RefRadius) * (1 + jitter * (float)(rng.NextDouble() * 2 - 1));
                float scale = rad / RefRadius;
                var feat = new Feature { X = sx * rad, Z = sz * rad, Scale = scale * heightScale, ColW = colRef * scale, F = f, Style = style };
                _ = rng.Next(1 << 20);   // (a seed the features once took: the stream's place kept)
                feat.Warp = f.Length * feat.ColW * 0.22f;
                // the warp moves the outline by at most 1.63 x Warp, so past this nothing of it stands
                feat.Reach = f.Length * feat.ColW + 1.7f * feat.Warp;
                _features.Add(feat);
            }
        }
        // the panels' own size: the base stands on the cone through their foot (see At)
        Ring(RefRadius * 0.95f, 0, 1.0f, 0.15f);
        Ring(RefRadius * 1.9f, 0.5f, 0.9f, 0.2f);

        // a coarse grid over the features' reach
        float ext = 10;
        foreach (var f in _features) ext = MathF.Max(ext, Len(f.X, f.Z) + f.Reach + 10);
        _gridCell = 1000;
        _gridMin = -ext;
        _gridN = (int)MathF.Ceiling(2 * ext / _gridCell);
        _grid = new List<Feature>[_gridN * _gridN];
        for (int i = 0; i < _grid.Length; i++) _grid[i] = [];
        foreach (var f in _features)
        {
            int x0 = Cell(f.X - f.Reach), x1 = Cell(f.X + f.Reach), z0 = Cell(f.Z - f.Reach), z1 = Cell(f.Z + f.Reach);
            for (int z = z0; z <= z1; z++) for (int x = x0; x <= x1; x++) _grid[z * _gridN + x].Add(f);
        }
    }

    private int Cell(float v) => Math.Clamp((int)((v - _gridMin) / _gridCell), 0, _gridN - 1);

    private static float Len(float x, float z) => MathF.Sqrt(x * x + z * z);

    public static float SmoothStep(float a, float b, float x)
    {
        float t = Math.Clamp((x - a) / (b - a), 0, 1);
        return t * t * (3 - 2 * t);
    }

    private static float SmoothMax(float a, float b, float k)
    {
        float h = Math.Clamp(0.5f + 0.5f * (a - b) / k, 0, 1);
        return b + (a - b) * h + k * h * (1 - h);
    }

    // value noise, 0..1
    private static float Hash(int x, int y)
    {
        unchecked
        {
            uint h = (uint)x * 374761393u + (uint)y * 668265263u;
            h = (h ^ (h >> 13)) * 1274126177u;
            h ^= h >> 16;
            return (h & 0xffffff) / 16777216f;
        }
    }

    private static float Noise(float x, float y)
    {
        int ix = (int)MathF.Floor(x), iy = (int)MathF.Floor(y);
        float fx = x - ix, fy = y - iy;
        float ux = fx * fx * fx * (fx * (fx * 6 - 15) + 10), uy = fy * fy * fy * (fy * (fy * 6 - 15) + 10);
        float a = Hash(ix, iy), b = Hash(ix + 1, iy), c = Hash(ix, iy + 1), d = Hash(ix + 1, iy + 1);
        return a + (b - a) * ux + (c - a) * uy + (a - b - c + d) * ux * uy;
    }

    public static float Fbm(float x, float y, int octaves)
    {
        float s = 0, amp = 0.5f, norm = 0;
        for (int i = 0; i < octaves; i++)
        {
            s += amp * Noise(x, y); norm += amp;
            (x, y) = (x * 1.6f - y * 1.2f + 17.3f, x * 1.2f + y * 1.6f + 9.1f);
            amp *= 0.5f;
        }
        return s / norm;
    }

    /// <summary>Ridged multifractal, 0..1: sharp crests, each octave weighted by the one before.</summary>
    private static float Ridged(float x, float y, int octaves)
    {
        float s = 0, amp = 0.5f, w = 1, norm = 0;
        for (int i = 0; i < octaves; i++)
        {
            float n = 1 - MathF.Abs(Noise(x, y) * 2 - 1);
            n *= n;
            s += n * amp * w; norm += amp;
            w = Math.Clamp(n * 1.5f, 0, 1);
            (x, y) = (x * 1.6f - y * 1.2f + 17.3f, x * 1.2f + y * 1.6f + 9.1f);
            amp *= 0.5f;
        }
        return s / norm;
    }
}
