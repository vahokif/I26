using System.Numerics;
using I76.Formats.Mission;

namespace I76.Formats.Assembly;

/// <summary>
/// The modern look's terrain shape (rendering only; the simulation keeps the
/// original's bilinear heightfield). A pure function of a point
/// in sample coordinates, so any grid drawn from it (any chunk, any level of
/// detail) meets its neighbours exactly.
///
/// <list type="bullet">
/// <item>Cells a car can reach (<see cref="Open"/>: nowhere steeper than
/// <see cref="WallGradient"/>, connected to a car, road or script path over such cells) are
/// drawn on a smooth surface through the samples (separable monotone cubic,
/// no overshoot), pulled back to within <see cref="Tolerance"/> of the
/// bilinear surface the cars ride on.</item>
/// <item>Points with no open cell about them (cliff faces, plateaus no car
/// reaches, the map's edge walls) follow a softened surface (the samples
/// blurred edge-preservingly, so terraces of a few metres melt into slopes
/// while mesa walls keep their flat tops; walls under ground no car reaches
/// lean back) and are pushed along its normal by a fractal relief, strongest
/// on steep ground, with ledge-like bands. How far they may move grows with
/// their height above (or below) the nearest drivable ground, so a wall still
/// meets the ground where the cars meet it.</item>
/// </list>
/// </summary>
public sealed class TerrainSurface : ITerrainSurface
{
    /// <summary>The most the drawn ground may stand off the simulation's on open cells, metres.</summary>
    public const double Tolerance = 0.04;
    /// <summary>
    /// A cell steeper than this anywhere (rise per unit run, 36°) is no ground a car
    /// reaches. Measured in the simulation (a Piranha on a plane, full throttle): it
    /// climbs 30° from a standing start and with a run-up, stalls on 33–35°, and from
    /// 40° up the corner sweep throws it back off the slope like a wall, at any speed.
    /// </summary>
    public const double WallGradient = 0.73;
    /// <summary>Off drivable ground: the extra freedom per metre of height away from the nearest drivable level.</summary>
    public const double Give = 0.5;
    /// <summary>
    /// Walls under ground no car reaches lean back to this rise per unit run from the
    /// drivable ground at their foot (76°). Steep enough that a car against the wall's
    /// foot meets the drawn face within half a metre up to 1.5 m: the first row of points
    /// into a wall (1.25 m) stands at least 5 m high.
    /// </summary>
    public const double LeanSlope = 4.0;
    /// <summary>The height band next to drivable ground that stays put, metres (a car's corners sweep about this high).</summary>
    public const double Clear = 1.0;
    /// <summary>The most any point moves, metres.</summary>
    public const double MaxFree = 14.0;
    public const double Spacing = 5.0;
    /// <summary>Height steps much larger than this (metres) survive the softening of free ground; smaller ones melt.</summary>
    public const double StepKeep = 9.0;

    private readonly int _sx0, _sz0, _w, _h;
    private readonly float[] _ht;    // filtered heights, metres
    private readonly float[] _hb;    // blurred heights, metres
    private readonly bool[] _open;   // per cell (indexed like samples; the last row and column unused)
    private readonly bool[] _anchor; // per sample: a corner of an open cell
    private readonly float[] _cone;  // per sample: the lowest of (anchor height + LeanSlope × distance to it), metres
    private readonly float[] _coneBase; // the height of the anchor that cone stands on
    private readonly float[] _under; // per sample: 1 where drivable ground stands above within two samples (a rim to keep)
    private int _openCount;

    public int OpenCells => _openCount;

    /// <summary>
    /// Names this surface's arrays (<see cref="CacheKey"/>) while they are as built, so what is made from it can be kept for the same surface made
    /// again; cleared when the surface is edited (<see cref="Refresh"/>).
    /// </summary>
    public string? Key { get; set; }

    /// <summary>Changes with anything that changes the surface's arrays: cached surfaces (<see cref="CacheKey"/>) are keyed by it.</summary>
    public const int Version = 2;

    /// <summary>Samples of the outside ground the surface holds round the map's bounds (<see cref="OutsideGround"/>): the cubic and the filters reach this far.</summary>
    public const int OutsidePad = 8;

    /// <summary>The bake cache's key for the surface of these tiles from these seeds (and the ground outside them): everything the arrays are made from.</summary>
    public static string CacheKey(TerrainTiles tiles, IReadOnlyList<Vector2> seeds, OutsideGround? outside = null)
    {
        using var k = new BakeCache.Key("terrain-surface");
        k.Add(Version).Add(tiles.TileCount).Add<byte>(tiles.Grid);
        for (int t = 0; t < tiles.TileCount; t++) k.Add(tiles.TileSamples(t));
        k.Add<Vector2>(seeds.ToArray());
        k.Add(outside?.Key ?? "-");
        return k.Finish();
    }

    /// <summary>The surface's arrays, as blobs for the bake cache (<see cref="FromBlobs"/> makes the same surface back).</summary>
    public List<BakeCache.Blob> ToBlobs()
    {
        static byte[] B<T>(T[] a) where T : unmanaged => System.Runtime.InteropServices.MemoryMarshal.AsBytes(a.AsSpan()).ToArray();
        var head = new int[] { _sx0, _sz0, _w, _h, _openCount };
        return [new(0, 0, B(head)), new(1, 0, B(_ht)), new(2, 0, B(_hb)), new(3, 0, B(_open)), new(4, 0, B(_anchor)),
            new(5, 0, B(_cone)), new(6, 0, B(_coneBase)), new(7, 0, B(_under))];
    }

    /// <summary>A surface from <see cref="ToBlobs"/>' blobs, or null if they do not fit together.</summary>
    public static TerrainSurface? FromBlobs(IReadOnlyList<BakeCache.Blob> b)
    {
        if (b.Count != 8 || b[0].Data.Length != 20) return null;
        static T[] A<T>(byte[] d) where T : unmanaged => System.Runtime.InteropServices.MemoryMarshal.Cast<byte, T>(d).ToArray();
        var head = A<int>(b[0].Data);
        long n = (long)head[2] * head[3];
        if (head[2] <= 0 || head[3] <= 0 || b[1].Data.Length != n * 4 || b[2].Data.Length != n * 4 || b[3].Data.Length != n || b[4].Data.Length != n
            || b[5].Data.Length != n * 4 || b[6].Data.Length != n * 4 || b[7].Data.Length != n * 4) return null;
        return new TerrainSurface(head, A<float>(b[1].Data), A<float>(b[2].Data), A<bool>(b[3].Data), A<bool>(b[4].Data),
            A<float>(b[5].Data), A<float>(b[6].Data), A<float>(b[7].Data));
    }

    private TerrainSurface(int[] head, float[] ht, float[] hb, bool[] open, bool[] anchor, float[] cone, float[] coneBase, float[] under)
    {
        (_sx0, _sz0, _w, _h, _openCount) = (head[0], head[1], head[2], head[3], head[4]);
        (_ht, _hb, _open, _anchor, _cone, _coneBase, _under) = (ht, hb, open, anchor, cone, coneBase, under);
    }

    /// <param name="seeds">Points known to be reachable, in sample coordinates (placements, roads); none: every gentle cell is open.</param>
    /// <param name="outside">
    /// The ground past the map's tiles: the surface then holds <see cref="OutsidePad"/> of its samples round the bounds (and in the empty
    /// cells), so the map's last cells are drawn running into it as the simulation has them, and every gentle cell there is open (a car can
    /// come in from outside). Null: samples off the tiles take the nearest one's height, the map's edge is level.
    /// </param>
    public TerrainSurface(TerrainTiles tiles, IEnumerable<Vector2>? seeds = null, OutsideGround? outside = null)
        : this(tiles, seeds, tiles.Bounds() is var (r0, r1, c0, c1)
            ? outside is null ? (c0 * TerrainTiles.Tile, r0 * TerrainTiles.Tile, (c1 - c0 + 1) * TerrainTiles.Tile, (r1 - r0 + 1) * TerrainTiles.Tile)
                : (c0 * TerrainTiles.Tile - OutsidePad, r0 * TerrainTiles.Tile - OutsidePad, (c1 - c0 + 1) * TerrainTiles.Tile + 2 * OutsidePad, (r1 - r0 + 1) * TerrainTiles.Tile + 2 * OutsidePad)
            : default, outside)
    {
    }

    /// <summary>
    /// The surface over a window of samples (a block of <see cref="BlockedSurface"/>): <paramref name="rect"/> is its corner and size in
    /// sample coordinates; seedless, so every gentle cell is open.
    /// </summary>
    public TerrainSurface(TerrainTiles tiles, (int X0, int Z0, int W, int H) rect) : this(tiles, null, rect, null) { }

    private TerrainSurface(TerrainTiles tiles, IEnumerable<Vector2>? seeds, (int X0, int Z0, int W, int H) rect, OutsideGround? outside)
    {
        (_sx0, _sz0, _w, _h) = rect;
        int n = _w * _h;
        var raw = new int[n];
        var have = new bool[n];
        var queue = new Queue<int>();
        for (int z = 0; z < _h; z++)
            for (int x = 0; x < _w; x++)
                if (tiles.Sample(_sx0 + x, _sz0 + z) is { } v)
                {
                    int i = z * _w + x;
                    raw[i] = v & MissionFile.HeightMask; have[i] = true; queue.Enqueue(i);
                }
                else if (outside is not null)
                {
                    int i = z * _w + x;
                    raw[i] = outside.Raw(_sx0 + x, _sz0 + z); have[i] = true;
                }
        // samples in empty grid cells inside the bounds take the nearest one's height, so every point has one neighbourhood
        while (queue.Count > 0)
        {
            int i = queue.Dequeue(), x = i % _w, z = i / _w;
            void Visit(int j) { if (!have[j]) { have[j] = true; raw[j] = raw[i]; queue.Enqueue(j); } }
            if (x > 0) Visit(i - 1);
            if (x < _w - 1) Visit(i + 1);
            if (z > 0) Visit(i - _w);
            if (z < _h - 1) Visit(i + _w);
        }
        int w = _w, hh = _h;
        int R(int x, int z) => raw[Math.Clamp(z, 0, hh - 1) * w + Math.Clamp(x, 0, w - 1)];
        // each sample's own, row by row on every core (the passes below likewise): the same values in any order
        var ht = _ht = new float[n];
        Parallel.For(0, _h, z =>
        {
            for (int x = 0; x < w; x++)
            {
                // a lone needle is drawn level with its neighbours, as TerrainNode does (docs/deviations.md)
                int hv = R(x, z), mx = int.MinValue, mn = int.MaxValue;
                for (int dz = -1; dz <= 1; dz++)
                    for (int dx = -1; dx <= 1; dx++)
                        if (dx != 0 || dz != 0) { int q = R(x + dx, z + dz); mx = Math.Max(mx, q); mn = Math.Min(mn, q); }
                int f = hv - mx > 150 ? mx : mn - hv > 150 ? mn : hv;
                ht[z * w + x] = (float)(f * MissionWorld.HeightScale);
            }
        });
        // edge-preserving (bilateral) blur, two passes over 5×5: binomial in distance, and a neighbour counts
        // less the more its height differs (σ = StepKeep), so terraces of a few metres melt into slopes while
        // the 20-40 m walls of mesas and buttes keep their flat tops and sharp rims
        var hb = _hb = (float[])_ht.Clone();
        var blur = Task.Run(() =>
        {
            var tmp = new float[n];
            float inv2s2 = (float)(1 / (2 * StepKeep * StepKeep));
            for (int pass = 0; pass < 2; pass++)
            {
                var src = pass == 0 ? hb : tmp;
                var dst = pass == 0 ? tmp : hb;
                Parallel.For(0, hh, z =>
                {
                    ReadOnlySpan<float> bin = [1, 4, 6, 4, 1];
                    for (int x = 0; x < w; x++)
                    {
                        float c = src[z * w + x], sum = 0, wsum = 0;
                        for (int dz = -2; dz <= 2; dz++)
                        {
                            int zz = Math.Clamp(z + dz, 0, hh - 1);
                            for (int dx = -2; dx <= 2; dx++)
                            {
                                float v = src[zz * w + Math.Clamp(x + dx, 0, w - 1)], d = v - c;
                                float wgt = bin[dx + 2] * bin[dz + 2] * MathF.Exp(-d * d * inv2s2);
                                sum += wgt * v; wsum += wgt;
                            }
                        }
                        dst[z * w + x] = sum / wsum;
                    }
                });
            }
        });
        // cells: gentle (no edge steeper than the wall gradient, on the raw samples), then those reachable
        var gentle = new bool[n];
        double rise = WallGradient * Spacing / MissionWorld.HeightScale;
        Parallel.For(0, _h - 1, z =>
        {
            for (int x = 0; x < w - 1; x++)
            {
                int a = R(x, z), b = R(x + 1, z), c = R(x, z + 1), d = R(x + 1, z + 1);
                // the bilinear surface is steepest at a corner: there its gradient is the two edges meeting there
                static double G(int e1, int e2) => Math.Sqrt((double)e1 * e1 + (double)e2 * e2);
                double g = Math.Max(Math.Max(G(b - a, c - a), G(b - a, d - b)), Math.Max(G(d - c, c - a), G(d - c, d - b)));
                gentle[z * w + x] = g <= rise;
            }
        });
        _open = new bool[n];
        var seedList = seeds?.ToList();
        if (seedList is null || seedList.Count == 0)
            Array.Copy(gentle, _open, n);
        else
        {
            // with the outside ground, every gentle cell with a corner off the tiles is reachable: a car can drive in from there
            if (outside is not null)
                for (int z = 0; z < _h - 1; z++)
                    for (int x = 0; x < w - 1; x++)
                    {
                        int i = z * _w + x;
                        if (!gentle[i] || _open[i]) continue;
                        if (tiles.Sample(_sx0 + x, _sz0 + z) is null || tiles.Sample(_sx0 + x + 1, _sz0 + z) is null
                            || tiles.Sample(_sx0 + x, _sz0 + z + 1) is null || tiles.Sample(_sx0 + x + 1, _sz0 + z + 1) is null)
                        { _open[i] = true; queue.Enqueue(i); }
                    }
            foreach (var s in seedList)
            {
                int cx = (int)Math.Floor(s.X) - _sx0, cz = (int)Math.Floor(s.Y) - _sz0;
                // a seed on a steep cell (a placement against a wall) starts the fill from the gentle cells about it
                for (int dz = -1; dz <= 1; dz++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int x = cx + dx, z = cz + dz;
                        if (x < 0 || z < 0 || x >= _w - 1 || z >= _h - 1) continue;
                        int i = z * _w + x;
                        if (gentle[i] && !_open[i]) { _open[i] = true; queue.Enqueue(i); }
                    }
            }
            while (queue.Count > 0)
            {
                int i = queue.Dequeue(), x = i % _w, z = i / _w;
                // eight-connected: a car can slip through a corner between two walls
                for (int dz = -1; dz <= 1; dz++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int xx = x + dx, zz = z + dz;
                        if (xx < 0 || zz < 0 || xx >= _w - 1 || zz >= _h - 1) continue;
                        int j = zz * _w + xx;
                        if (gentle[j] && !_open[j]) { _open[j] = true; queue.Enqueue(j); }
                    }
            }
        }
        _anchor = new bool[n];
        for (int z = 0; z < _h - 1; z++)
            for (int x = 0; x < _w - 1; x++)
                if (_open[z * _w + x])
                {
                    _openCount++;
                    int i = z * _w + x;
                    _anchor[i] = _anchor[i + 1] = _anchor[i + _w] = _anchor[i + _w + 1] = true;
                }
        // the lean-back cone over the drivable ground: Dijkstra from every anchor, 16 neighbours
        _cone = new float[n];
        _coneBase = new float[n];
        Array.Fill(_cone, float.MaxValue);
        var pq = new PriorityQueue<int, float>();
        for (int i = 0; i < n; i++)
            if (_anchor[i]) { _cone[i] = _coneBase[i] = _ht[i]; pq.Enqueue(i, _ht[i]); }
        ReadOnlySpan<(int dx, int dz)> steps = [(1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (1, -1), (-1, 1), (-1, -1),
            (2, 1), (2, -1), (-2, 1), (-2, -1), (1, 2), (1, -2), (-1, 2), (-1, -2)];
        var cost = new float[steps.Length];
        for (int k = 0; k < steps.Length; k++) cost[k] = (float)(LeanSlope * Spacing * Math.Sqrt(steps[k].dx * steps[k].dx + steps[k].dz * steps[k].dz));
        while (pq.TryDequeue(out int i, out float c))
        {
            if (c > _cone[i]) continue;
            int x = i % _w, z = i / _w;
            for (int k = 0; k < steps.Length; k++)
            {
                int xx = x + steps[k].dx, zz = z + steps[k].dz;
                if (xx < 0 || zz < 0 || xx >= _w || zz >= _h) continue;
                int j = zz * _w + xx;
                float nc = c + cost[k];
                if (nc < _cone[j]) { _cone[j] = nc; _coneBase[j] = _coneBase[i]; pq.Enqueue(j, nc); }
            }
        }
        var under = _under = new float[n];
        var anchor = _anchor;
        Parallel.For(0, _h, z =>
        {
            for (int x = 0; x < w; x++)
            {
                float hv = ht[z * w + x];
                for (int dz = -2; dz <= 2 && under[z * w + x] == 0; dz++)
                    for (int dx = -2; dx <= 2; dx++)
                    {
                        int xx = x + dx, zz = z + dz;
                        if (xx < 0 || zz < 0 || xx >= w || zz >= hh) continue;
                        int j = zz * w + xx;
                        if (anchor[j] && ht[j] > hv + Clear) { under[z * w + x] = 1; break; }
                    }
            }
        });
        blur.Wait();
    }

    /// <summary>
    /// The map editor's edit: the samples in the rectangle (global sample coordinates, inclusive) changed in <paramref name="tiles"/>;
    /// brings every array the drawn surface reads up to date for them and what they reach (the needle filter, the blur, the
    /// cells, the lean-back cone, the rims), in place. Reachability is not re-run (no seeds here): inside the edit a gentle cell
    /// is open, so ground made level is drawn as drivable ground, a cliff raised as a cliff. Returns the rectangle of samples
    /// whose drawn points can have changed (the edit and eight samples about it).
    /// </summary>
    public (int X0, int Z0, int X1, int Z1) Refresh(TerrainTiles tiles, int sx0, int sz0, int sx1, int sz1)
    {
        Key = null;
        int w = _w, hh = _h;
        // the rectangle in array coordinates, clipped
        (int, int, int, int) Box(int pad) => (Math.Max(0, sx0 - _sx0 - pad), Math.Max(0, sz0 - _sz0 - pad), Math.Min(w - 1, sx1 - _sx0 + pad), Math.Min(hh - 1, sz1 - _sz0 + pad));
        var (ex0, ez0, ex1, ez1) = Box(0);
        if (ex1 < ex0 || ez1 < ez0) return (sx0 - 8, sz0 - 8, sx1 + 8, sz1 + 8);
        int Raw(int x, int z)
        {
            x = Math.Clamp(x, 0, w - 1); z = Math.Clamp(z, 0, hh - 1);
            if (tiles.Sample(_sx0 + x, _sz0 + z) is { } v) return v & MissionFile.HeightMask;
            return (int)Math.Round(_ht[z * w + x] / MissionWorld.HeightScale);   // an empty cell: the height it was drawn at
        }
        // the filtered heights: the needle filter reads one sample about
        var (a0, b0, a1, b1) = Box(1);
        for (int z = b0; z <= b1; z++)
            for (int x = a0; x <= a1; x++)
            {
                int hv = Raw(x, z), mx = int.MinValue, mn = int.MaxValue;
                for (int dz = -1; dz <= 1; dz++)
                    for (int dx = -1; dx <= 1; dx++)
                        if (dx != 0 || dz != 0) { int q = Raw(x + dx, z + dz); mx = Math.Max(mx, q); mn = Math.Min(mn, q); }
                int f = hv - mx > 150 ? mx : mn - hv > 150 ? mn : hv;
                _ht[z * w + x] = (float)(f * MissionWorld.HeightScale);
            }
        // the blur: two 5x5 passes, so the first over six samples about the filtered ones and the second over four
        var (c0, d0, c1, d1) = Box(5);
        var (t0, u0, t1, u1) = Box(7);
        int tw = t1 - t0 + 1;
        var tmp = new float[tw * (u1 - u0 + 1)];
        float inv2s2 = (float)(1 / (2 * StepKeep * StepKeep));
        float[] bin = [1, 4, 6, 4, 1];
        float Blur(Func<int, int, float> src, int x, int z)
        {
            float c = src(x, z), sum = 0, wsum = 0;
            for (int dz = -2; dz <= 2; dz++)
                for (int dx = -2; dx <= 2; dx++)
                {
                    float v = src(x + dx, z + dz), d = v - c;
                    float wgt = bin[dx + 2] * bin[dz + 2] * MathF.Exp(-d * d * inv2s2);
                    sum += wgt * v; wsum += wgt;
                }
            return sum / wsum;
        }
        float Ht(int x, int z) => _ht[Math.Clamp(z, 0, hh - 1) * w + Math.Clamp(x, 0, w - 1)];
        Parallel.For(u0, u1 + 1, z => { for (int x = t0; x <= t1; x++) tmp[(z - u0) * tw + x - t0] = Blur(Ht, x, z); });
        float Tmp(int x, int z) => tmp[(Math.Clamp(z, u0, u1) - u0) * tw + Math.Clamp(x, t0, t1) - t0];
        Parallel.For(d0, d1 + 1, z => { for (int x = c0; x <= c1; x++) _hb[z * w + x] = Blur(Tmp, x, z); });
        // the cells: open where gentle (as in Surface's ctor, on the raw samples); a cell holds the four samples from its corner
        double rise = WallGradient * Spacing / MissionWorld.HeightScale;
        var (g0, h0, g1, h1) = Box(1);
        for (int z = Math.Max(0, h0 - 1); z <= Math.Min(hh - 2, h1); z++)
            for (int x = Math.Max(0, g0 - 1); x <= Math.Min(w - 2, g1); x++)
            {
                int a = Raw(x, z), b = Raw(x + 1, z), c = Raw(x, z + 1), d = Raw(x + 1, z + 1);
                static double G(int e1, int e2) => Math.Sqrt((double)e1 * e1 + (double)e2 * e2);
                double g = Math.Max(Math.Max(G(b - a, c - a), G(b - a, d - b)), Math.Max(G(d - c, c - a), G(d - c, d - b)));
                bool open = g <= rise, was = _open[z * w + x];
                if (open != was) { _open[z * w + x] = open; _openCount += open ? 1 : -1; }
            }
        // the corners of open cells
        var (n0, m0, n1, m1) = Box(3);
        for (int z = m0; z <= m1; z++)
            for (int x = n0; x <= n1; x++)
            {
                bool an = false;
                for (int dz = -1; dz <= 0 && !an; dz++)
                    for (int dx = -1; dx <= 0; dx++)
                    {
                        int cx = x + dx, cz = z + dz;
                        if (cx >= 0 && cz >= 0 && cx < w - 1 && cz < hh - 1 && _open[cz * w + cx]) { an = true; break; }
                    }
                _anchor[z * w + x] = an;
            }
        // the lean-back cone, again over the edit and sixteen samples about it, from its anchors and the cone about it as it was
        var (k0, l0, k1, l1) = Box(16);
        var pq = new PriorityQueue<int, float>();
        for (int z = l0; z <= l1; z++)
            for (int x = k0; x <= k1; x++)
            {
                int i = z * w + x;
                bool rim = x == k0 && k0 > 0 || x == k1 && k1 < w - 1 || z == l0 && l0 > 0 || z == l1 && l1 < hh - 1;
                if (_anchor[i]) { _cone[i] = _coneBase[i] = _ht[i]; pq.Enqueue(i, _ht[i]); }
                else if (rim) pq.Enqueue(i, _cone[i]);
                else _cone[i] = float.MaxValue;
            }
        ReadOnlySpan<(int dx, int dz)> steps = [(1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (1, -1), (-1, 1), (-1, -1),
            (2, 1), (2, -1), (-2, 1), (-2, -1), (1, 2), (1, -2), (-1, 2), (-1, -2)];
        var cost = new float[steps.Length];
        for (int k = 0; k < steps.Length; k++) cost[k] = (float)(LeanSlope * Spacing * Math.Sqrt(steps[k].dx * steps[k].dx + steps[k].dz * steps[k].dz));
        while (pq.TryDequeue(out int i, out float cv))
        {
            if (cv > _cone[i]) continue;
            int x = i % w, z = i / w;
            for (int k = 0; k < steps.Length; k++)
            {
                int xx = x + steps[k].dx, zz = z + steps[k].dz;
                if (xx < k0 || zz < l0 || xx > k1 || zz > l1) continue;
                int j = zz * w + xx;
                float nc = cv + cost[k];
                if (nc < _cone[j]) { _cone[j] = nc; _coneBase[j] = _coneBase[i]; pq.Enqueue(j, nc); }
            }
        }
        // the rims kept: where drivable ground stands above within two samples
        var (r0, s0, r1, s1) = Box(5);
        for (int z = s0; z <= s1; z++)
            for (int x = r0; x <= r1; x++)
            {
                float hv = _ht[z * w + x];
                float u = 0;
                for (int dz = -2; dz <= 2 && u == 0; dz++)
                    for (int dx = -2; dx <= 2; dx++)
                    {
                        int xx = x + dx, zz = z + dz;
                        if (xx < 0 || zz < 0 || xx >= w || zz >= hh) continue;
                        int j = zz * w + xx;
                        if (_anchor[j] && _ht[j] > hv + Clear) { u = 1; break; }
                    }
                _under[z * w + x] = u;
            }
        return (sx0 - 8, sz0 - 8, sx1 + 8, sz1 + 8);
    }

    /// <summary>The lowest and highest the drawn surface can be over a square of samples (the sample heights, widened by the freedom).</summary>
    public (float Min, float Max) HeightRange(int sx, int sz, int size)
    {
        float mn = float.MaxValue, mx = float.MinValue;
        for (int z = sz - 1; z <= sz + size + 1; z++)
            for (int x = sx - 1; x <= sx + size + 1; x++) { float v = H(x, z); mn = Math.Min(mn, v); mx = Math.Max(mx, v); }
        return (mn - 2 * (float)MaxFree, mx + 2 * (float)MaxFree);
    }

    private double Bilerp(float[] f, double fx, double fz)
    {
        int ix = (int)Math.Floor(fx), iz = (int)Math.Floor(fz);
        double tx = fx - ix, tz = fz - iz;
        double a = f[Idx(ix, iz)] + (f[Idx(ix + 1, iz)] - f[Idx(ix, iz)]) * tx;
        double b = f[Idx(ix, iz + 1)] + (f[Idx(ix + 1, iz + 1)] - f[Idx(ix, iz + 1)]) * tx;
        return a + (b - a) * tz;
    }

    /// <summary>
    /// Seeds for the reachability fill, in sample coordinates: where cars start (vehicle
    /// placements, spawn points), every road point and every point of the script's paths.
    /// Scenery is not a seed: cactus and rocks stand on mesa tops no car reaches.
    /// </summary>
    public static IEnumerable<Vector2> Seeds(MissionWorld w)
    {
        var k = (float)MissionWorld.SampleScale;
        foreach (var p in w.Placements)
            if (p.Object.ObjectType == 1) yield return new Vector2(p.Object.Position.X, p.Object.Position.Z) * k;
        foreach (var seg in w.Mission.RoadSegments())
            foreach (var r in seg.Points) yield return new Vector2(r.X, r.Z) * k;
        if (w.Mission.GetFsmTables()?.Paths is { } paths)
            foreach (var path in paths)
                foreach (var q in path.Points) yield return new Vector2(q.X, q.Z) * k;
    }

    private int Idx(int sx, int sz) => Math.Clamp(sz - _sz0, 0, _h - 1) * _w + Math.Clamp(sx - _sx0, 0, _w - 1);
    private float H(int sx, int sz) => _ht[Idx(sx, sz)];

    /// <summary>Whether the cell with corner (sx, sz) is drivable ground a car can reach.</summary>
    public bool Open(int sx, int sz)
    {
        int x = sx - _sx0, z = sz - _sz0;
        if (x < 0 || z < 0 || x >= _w - 1 || z >= _h - 1) return false;
        return _open[z * _w + x];
    }

    /// <summary>The bilinear surface on the (needle-filtered) samples, metres: the simulation's own on open cells.</summary>
    public double Bilinear(double fx, double fz)
    {
        int ix = (int)Math.Floor(fx), iz = (int)Math.Floor(fz);
        double tx = fx - ix, tz = fz - iz;
        double a = H(ix, iz) + (H(ix + 1, iz) - H(ix, iz)) * tx;
        double b = H(ix, iz + 1) + (H(ix + 1, iz + 1) - H(ix, iz + 1)) * tx;
        return a + (b - a) * tz;
    }

    private static double Slope(double a, double b) => a * b <= 0 ? 0 : 2 * a * b / (a + b);

    /// <summary>Monotone cubic Hermite (Fritsch–Butland slopes) through p0 at t = 0 and p1 at t = 1.</summary>
    private static double Pchip(double pm, double p0, double p1, double p2, double t)
    {
        double m0 = Slope(p0 - pm, p1 - p0), m1 = Slope(p1 - p0, p2 - p1);
        double t2 = t * t, t3 = t2 * t;
        return (2 * t3 - 3 * t2 + 1) * p0 + (t3 - 2 * t2 + t) * m0 + (-2 * t3 + 3 * t2) * p1 + (t3 - t2) * m1;
    }

    private double Smooth(float[] f, double fx, double fz)
    {
        int ix = (int)Math.Floor(fx), iz = (int)Math.Floor(fz);
        double tx = fx - ix, tz = fz - iz;
        Span<double> row = stackalloc double[4];
        for (int k = 0; k < 4; k++)
        {
            int z = iz - 1 + k;
            row[k] = Pchip(f[Idx(ix - 1, z)], f[Idx(ix, z)], f[Idx(ix + 1, z)], f[Idx(ix + 2, z)], tx);
        }
        return Pchip(row[0], row[1], row[2], row[3], tz);
    }

    /// <summary>The smooth surface through the samples, metres (no tolerance applied).</summary>
    public double SmoothHeight(double fx, double fz) => Smooth(_ht, fx, fz);

    /// <summary>
    /// The smooth surface's normal over a broad span (game frame): its slope averaged over 20 m along each axis and 10 m
    /// across, for paved road, which the samples' own lumps (a few metres each) would otherwise shade as a bumpy track.
    /// </summary>
    public Vector3 BroadNormal(double fx, double fz)
    {
        const double d = 2;   // samples either side
        double gx = 0, gz = 0;
        for (int k = -1; k <= 1; k++)
        {
            gx += Smooth(_ht, fx + d, fz + k) - Smooth(_ht, fx - d, fz + k);
            gz += Smooth(_ht, fx + k, fz + d) - Smooth(_ht, fx + k, fz - d);
        }
        double scale = 1 / (3 * 2 * d * Spacing);
        return Vector3.Normalize(new Vector3((float)(-gx * scale), 1, (float)(-gz * scale)));
    }

    /// <summary>
    /// The drawn point for sample coordinates (fx, fz): the game-frame position in
    /// metres (X = 5 fx and Z = 5 fz but where relief moves it), and the height the
    /// shading normal is taken from (the smooth surface on drivable ground, where the
    /// drawn one is held to the simulation's creases; elsewhere the drawn one).
    /// </summary>
    public Vector3 Point(double fx, double fz, out double shadeY)
    {
        double b = Bilinear(fx, fz);
        int ix = (int)Math.Floor(fx), iz = (int)Math.Floor(fz);
        bool onX = fx == ix, onZ = fz == iz;
        // free: no open cell touches the point
        bool free = true;
        for (int cz = onZ ? iz - 1 : iz; cz <= iz && free; cz++)
            for (int cx = onX ? ix - 1 : ix; cx <= ix && free; cx++)
                if (Open(cx, cz)) free = false;
        if (!free)
        {
            double s = Smooth(_ht, fx, fz);
            shadeY = s;
            return new Vector3((float)(fx * Spacing), (float)(b + Tolerance * Math.Tanh((s - b) / Tolerance)), (float)(fz * Spacing));
        }
        // how far this point may move: grows with its height away from the drivable levels about it
        double near = double.MaxValue;
        for (int sz = iz - 1; sz <= iz + 2; sz++)
            for (int sx = ix - 1; sx <= ix + 2; sx++)
                if (_anchor[Idx(sx, sz)] && Open2(sx, sz)) near = Math.Min(near, Math.Abs(b - H(sx, sz)));
        double allow = near == double.MaxValue ? MaxFree : Math.Min(MaxFree, Tolerance + Give * Math.Max(0, near - Clear));
        double sb = Smooth(_hb, fx, fz);
        double px = fx * Spacing, pz = fz * Spacing;
        double y = b + allow * Math.Tanh((sb - b) / allow);
        // lean back under ground no car reaches: no higher than the cone over the drivable ground at the foot,
        // wandering by a few metres once well above it
        double lean = 1 - Bilerp(_under, fx, fz);
        if (lean > 0)
        {
            double cone = Bilerp(_cone, fx, fz);
            if (cone < y + 10)
            {
                cone += 4 * SmoothStep(2, 12, cone - Bilerp(_coneBase, fx, fz)) * (0.6 + Noise.Fbm(px / 26, 0.5, pz / 26, 3));
                y += (SoftMin(y, cone, 1.5) - y) * lean;
            }
        }
        // the softened surface's normal (game frame), and how steep it is
        const double e = 0.25;
        double gx = (Smooth(_hb, fx + e, fz) - Smooth(_hb, fx - e, fz)) / (2 * e * Spacing);
        double gz = (Smooth(_hb, fx, fz + e) - Smooth(_hb, fx, fz - e)) / (2 * e * Spacing);
        var nrm = Vector3.Normalize(new Vector3((float)-gx, 1, (float)-gz));
        double grad = Math.Sqrt(gx * gx + gz * gz);
        double steep = SmoothStep(0.5, 2.0, grad);
        double r = steep * (1.6 * Noise.Fbm(px / 14, y / 10, pz / 14, 3)   // bulges and gullies
                          + 0.7 * Noise.Fbm(px / 9, y / 2.2, pz / 9, 2))    // ledges: flattened along the height
                 + (1 - steep) * 0.5 * Noise.Fbm(px / 11, y / 11, pz / 11, 2);
        r = allow * Math.Tanh(r / allow);
        var p = new Vector3((float)px, (float)y, (float)pz) + nrm * (float)r;
        shadeY = p.Y;
        return p;
    }

    // an anchor sample still in range (not a clamped edge repeat)
    private bool Open2(int sx, int sz) => sx >= _sx0 && sz >= _sz0 && sx < _sx0 + _w && sz < _sz0 + _h;

    /// <summary>min(a, b), rounded over a band k wide.</summary>
    private static double SoftMin(double a, double b, double k)
    {
        double h = Math.Clamp(0.5 + 0.5 * (b - a) / k, 0, 1);
        return b + (a - b) * h - k * h * (1 - h);
    }

    private static double SmoothStep(double a, double b, double x)
    {
        double t = Math.Clamp((x - a) / (b - a), 0, 1);
        return t * t * (3 - 2 * t);
    }

    /// <summary>Gradient noise in 3D (Perlin's improved noise on a hashed lattice), about −1..1.</summary>
    public static class Noise
    {
        private static int Hash(int x, int y, int z)
        {
            unchecked
            {
                uint h = (uint)x * 0x8da6b343u ^ (uint)y * 0xd8163841u ^ (uint)z * 0xcb1ab31fu;
                h ^= h >> 15; h *= 0x2c1b3c6du; h ^= h >> 12; h *= 0x297a2d39u; h ^= h >> 15;
                return (int)(h & 15);
            }
        }

        private static double Grad(int h, double x, double y, double z) => (h & 15) switch
        {
            0 => x + y, 1 => -x + y, 2 => x - y, 3 => -x - y,
            4 => x + z, 5 => -x + z, 6 => x - z, 7 => -x - z,
            8 => y + z, 9 => -y + z, 10 => y - z, 11 => -y - z,
            12 => x + y, 13 => -x + y, 14 => -y + z, _ => -y - z,
        };

        private static double Fade(double t) => t * t * t * (t * (t * 6 - 15) + 10);

        public static double Perlin(double x, double y, double z)
        {
            int ix = (int)Math.Floor(x), iy = (int)Math.Floor(y), iz = (int)Math.Floor(z);
            double fx = x - ix, fy = y - iy, fz = z - iz;
            double u = Fade(fx), v = Fade(fy), w = Fade(fz);
            double L(double a, double b, double t) => a + (b - a) * t;
            double G(int dx, int dy, int dz) => Grad(Hash(ix + dx, iy + dy, iz + dz), fx - dx, fy - dy, fz - dz);
            return L(L(L(G(0, 0, 0), G(1, 0, 0), u), L(G(0, 1, 0), G(1, 1, 0), u), v),
                     L(L(G(0, 0, 1), G(1, 0, 1), u), L(G(0, 1, 1), G(1, 1, 1), u), v), w);
        }

        public static double Fbm(double x, double y, double z, int octaves)
        {
            double s = 0, a = 1, norm = 0;
            for (int i = 0; i < octaves; i++)
            {
                s += a * Perlin(x + i * 17.3, y + i * 5.1, z + i * 31.7);
                norm += a; a *= 0.5; x *= 2; y *= 2; z *= 2;
            }
            return s / norm * 1.6;
        }
    }
}
