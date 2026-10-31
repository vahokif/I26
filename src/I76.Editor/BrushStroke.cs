namespace I76.Editor;

/// <summary>
/// One brush stroke on a document: <see cref="Stamp"/> is called as the mouse moves and as time passes with the button down,
/// and <see cref="End"/> hands back the stroke as one undoable command. Heights are kept as floats while the stroke runs (a
/// tenth of a metre is the sample step, and a gentle brush moves less than that in a frame), written rounded; the sample's
/// nibble is never touched. Each physical sample is changed once per stamp even where the ZMAP grid shows its tile twice.
/// </summary>
public sealed class BrushStroke
{
    private readonly IMapDocument _doc;
    private readonly BrushSettings _brush;
    private readonly Dictionary<long, float> _work = [];
    private readonly Dictionary<int, SortedDictionary<int, (ushort Before, ushort After)>> _tiles = [];
    private double _flattenTo = double.NaN;
    private int _stamps;

    public BrushStroke(IMapDocument doc, BrushSettings brush)
    {
        _doc = doc;
        _brush = new BrushSettings { Kind = brush.Kind, Falloff = brush.Falloff, Radius = brush.Radius, Strength = brush.Strength, TargetHeight = brush.TargetHeight, SurfaceId = brush.SurfaceId };
    }

    public bool Empty => _tiles.Count == 0;

    private static long Key(int tile, int offset) => (long)tile * (I76.Formats.Mission.TerrainTiles.Tile * I76.Formats.Mission.TerrainTiles.Tile) + offset;

    /// <summary>The height (raw, floating) the stroke sees at a sample: its own work if it has changed it, else the document's.</summary>
    private double Read(int sx, int sz)
    {
        if (!_doc.Locate(sx, sz, out int tile, out int off))
        {
            // outside the map the ground is as the nearest sample inside (smoothing at an edge does not pull toward zero)
            return double.NaN;
        }
        return _work.TryGetValue(Key(tile, off), out float v) ? v : _doc.GetHeight(sx, sz);
    }

    /// <summary>
    /// Applies the brush at a point (sample coordinates, fractional) for <paramref name="dt"/> seconds. Returns the samples
    /// changed, or null if none. A Flatten stroke takes its height from the ground under its first stamp.
    /// </summary>
    public SampleRect? Stamp(double cx, double cz, double dt)
    {
        if (_brush.Kind == BrushKind.Surface) return StampSurface(cx, cz);
        double radius = _brush.Radius / Units.SampleSize;
        int x0 = (int)Math.Floor(cx - radius), x1 = (int)Math.Ceiling(cx + radius), z0 = (int)Math.Floor(cz - radius), z1 = (int)Math.Ceiling(cz + radius);
        double s = _brush.Strength;
        if (_brush.Kind == BrushKind.Flatten && double.IsNaN(_flattenTo))
            _flattenTo = Sampled(cx, cz);
        if (_brush.Kind == BrushKind.Flatten && double.IsNaN(_flattenTo)) return null;
        double target = _brush.Kind == BrushKind.Level ? _brush.TargetHeight / Units.HeightScale : _flattenTo;

        // pass one: the new heights from the state before this stamp; pass two writes them
        var updates = new List<(int Tile, int Off, int Sx, int Sz, double Now)>();
        var seen = new HashSet<long>();
        for (int sz = z0; sz <= z1; sz++)
            for (int sx = x0; sx <= x1; sx++)
            {
                double dx = sx - cx, dz = sz - cz;
                double w = BrushMath.Weight(_brush.Falloff, Math.Sqrt(dx * dx + dz * dz) / radius);
                if (w <= 0 || !_doc.Locate(sx, sz, out int tile, out int off)) continue;
                if (!seen.Add(Key(tile, off))) continue;
                double h = Read(sx, sz), now = h;
                switch (_brush.Kind)
                {
                    case BrushKind.Raise: now = h + BrushMath.RaiseRate / Units.HeightScale * s * w * dt; break;
                    case BrushKind.Lower: now = h - BrushMath.RaiseRate / Units.HeightScale * s * w * dt; break;
                    case BrushKind.Noise: now = h + BrushMath.Noise(sx, sz) * BrushMath.NoiseRate / Units.HeightScale * s * w * dt; break;
                    case BrushKind.Flatten:
                    case BrushKind.Level:
                        now = h + (target - h) * Math.Min(1, BrushMath.PullRate * s * w * dt);
                        break;
                    case BrushKind.Smooth:
                        {
                            double sum = 0; int n = 0;
                            for (int oz = -1; oz <= 1; oz++)
                                for (int ox = -1; ox <= 1; ox++)
                                    if (Read(sx + ox, sz + oz) is var v && !double.IsNaN(v)) { sum += v; n++; }
                            now = h + (sum / n - h) * Math.Min(1, BrushMath.PullRate * s * w * dt);
                            break;
                        }
                }
                updates.Add((tile, off, sx, sz, Math.Clamp(now, 0, TerrainDocument.MaxHeight)));
            }
        SampleRect? dirty = null;
        foreach (var (tile, off, sx, sz, now) in updates)
        {
            _work[Key(tile, off)] = (float)now;
            ushort old = _doc.GetSample(tile, off);
            ushort write = (ushort)((old & ~TerrainDocument.MaxHeight) | (int)Math.Round(now));
            if (write == old) continue;
            if (!_tiles.TryGetValue(tile, out var d)) _tiles[tile] = d = [];
            // the first write to a sample keeps what it was before the stroke
            d[off] = d.TryGetValue(off, out var e) ? (e.Before, write) : (old, write);
            _doc.SetSample(tile, off, write);
            var r = new SampleRect(sx, sz, sx, sz);
            dirty = dirty is { } u ? u.Union(r) : r;
        }
        _stamps++;
        return dirty;
    }

    /// <summary>
    /// Paints the surface id over every sample within the radius (a hard-edged disc, whatever the strength). The height and
    /// the sample's bit 12 are kept; a sample already of that type is left alone.
    /// </summary>
    private SampleRect? StampSurface(double cx, double cz)
    {
        double radius = _brush.Radius / Units.SampleSize;
        int x0 = (int)Math.Floor(cx - radius), x1 = (int)Math.Ceiling(cx + radius), z0 = (int)Math.Floor(cz - radius), z1 = (int)Math.Ceiling(cz + radius);
        SampleRect? dirty = null;
        for (int sz = z0; sz <= z1; sz++)
            for (int sx = x0; sx <= x1; sx++)
            {
                double dx = sx - cx, dz = sz - cz;
                if (dx * dx + dz * dz > radius * radius || !_doc.Locate(sx, sz, out int tile, out int off)) continue;
                ushort old = _doc.GetSample(tile, off), write = SurfaceTypes.With(old, _brush.SurfaceId);
                if (write == old) continue;
                if (!_tiles.TryGetValue(tile, out var d)) _tiles[tile] = d = [];
                d[off] = d.TryGetValue(off, out var e) ? (e.Before, write) : (old, write);
                _doc.SetSample(tile, off, write);
                var r = new SampleRect(sx, sz, sx, sz);
                dirty = dirty is { } u ? u.Union(r) : r;
            }
        _stamps++;
        return dirty;
    }

    /// <summary>The ground height under a point (raw), bilinear over the samples; NaN outside the map.</summary>
    public double Sampled(double fx, double fz)
    {
        int ix = (int)Math.Floor(fx), iz = (int)Math.Floor(fz);
        double tx = fx - ix, tz = fz - iz;
        double a = Read(ix, iz), b = Read(ix + 1, iz), c = Read(ix, iz + 1), d = Read(ix + 1, iz + 1);
        if (double.IsNaN(a)) return double.NaN;
        if (double.IsNaN(b)) b = a;
        if (double.IsNaN(c)) c = a;
        if (double.IsNaN(d)) d = a;
        double top = a + (b - a) * tx, bottom = c + (d - c) * tx;
        return top + (bottom - top) * tz;
    }

    /// <summary>The stroke as a command (null if it changed nothing).</summary>
    public StrokeCommand? End()
    {
        if (_tiles.Count == 0) return null;
        var tiles = new Dictionary<int, TileDelta>();
        foreach (var (tile, d) in _tiles)
        {
            var offs = new int[d.Count]; var before = new ushort[d.Count]; var after = new ushort[d.Count];
            int i = 0;
            foreach (var (off, (b, a)) in d) { offs[i] = off; before[i] = b; after[i] = a; i++; }
            tiles[tile] = new TileDelta(offs, before, after);
        }
        return new StrokeCommand(_brush.Kind.ToString(), tiles);
    }
}

/// <summary>Sample spacing and height scale of the heightfield (TerrainTiles, MissionWorld: 5 world units, raw 0.1).</summary>
public static class Units
{
    public const double SampleSize = 5.0;
    public const double HeightScale = 0.1;
    /// <summary>The highest ground the 12-bit sample holds, metres.</summary>
    public const double MaxMetres = 4095 * HeightScale;
}
