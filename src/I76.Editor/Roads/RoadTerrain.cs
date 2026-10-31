using System.Numerics;
using I76.Formats.Mission;

namespace I76.Editor.Roads;

/// <summary>Changes to the heights as a recorder: what was written, tile by tile, for one undo command with the road.</summary>
public sealed class HeightRecorder(IMapDocument doc)
{
    private readonly Dictionary<int, SortedDictionary<int, (ushort Before, ushort After)>> _tiles = [];
    private SampleRect? _rect;
    public SampleRect? Rect => _rect;
    public int Count => _tiles.Values.Sum(t => t.Count);

    /// <summary>Sets a sample's height (raw, clamped) through the document, remembering the first value it had.</summary>
    public void Set(int sx, int sz, int raw)
    {
        if (!doc.Locate(sx, sz, out int tile, out int off)) return;
        ushort before = doc.GetSample(tile, off);
        doc.SetHeight(sx, sz, raw);
        ushort after = doc.GetSample(tile, off);
        if (!_tiles.TryGetValue(tile, out var t)) _tiles[tile] = t = [];
        t[off] = t.TryGetValue(off, out var e) ? (e.Before, after) : (before, after);
        var r = new SampleRect(sx, sz, sx, sz);
        _rect = _rect is { } u ? u.Union(r) : r;
    }

    /// <summary>Sets a sample's surface nibble (bits 12-15: the ground's surface and flags), remembering the first value the sample had. The height stays.</summary>
    public void SetNibble(int sx, int sz, int nibble)
    {
        if (!doc.Locate(sx, sz, out int tile, out int off)) return;
        ushort before = doc.GetSample(tile, off);
        ushort after = (ushort)((before & 0x0fff) | ((nibble & 0xf) << 12));
        if (after == before) return;
        doc.SetSample(tile, off, after);
        if (!_tiles.TryGetValue(tile, out var t)) _tiles[tile] = t = [];
        t[off] = t.TryGetValue(off, out var e) ? (e.Before, after) : (before, after);
        var r = new SampleRect(sx, sz, sx, sz);
        _rect = _rect is { } u ? u.Union(r) : r;
    }

    public Dictionary<int, TileDelta> Tiles()
    {
        var o = new Dictionary<int, TileDelta>();
        foreach (var (tile, d) in _tiles)
        {
            var live = d.Where(kv => kv.Value.Before != kv.Value.After).ToArray();
            if (live.Length == 0) continue;
            o[tile] = new TileDelta([.. live.Select(kv => kv.Key)], [.. live.Select(kv => kv.Value.Before)], [.. live.Select(kv => kv.Value.After)]);
        }
        return o;
    }
}

/// <summary>
/// Cuts and fills the ground to a road's profile: the samples within <see cref="Settings.Flat"/> of the centre line take the profile's
/// height there (so the road's quads, which the engine puts on the bilinear ground, have no cross slope and no bump), and the samples
/// within <see cref="Settings.Shoulder"/> beyond them blend from it back to the old ground with a smoothstep.
/// </summary>
public static class RoadTerrain
{
    public sealed record Settings(float Flat = RoadBuilder.HalfWidth + 3f, float Shoulder = 14f);

    public sealed record Result(float MaxCut, float MaxFill, int Samples, SampleRect? Rect);

    /// <summary>The ground height at a point (bilinear over the document's samples, as MissionWorld.BilinearHeight does).</summary>
    public static float Ground(IMapDocument doc, double x, double z)
    {
        double fx = x / Units.SampleSize, fz = z / Units.SampleSize;
        int sx = (int)Math.Floor(fx), sz = (int)Math.Floor(fz);
        double tx = fx - sx, tz = fz - sz;
        double S(int a, int b) => doc.GetHeight(a, b);
        double a0 = S(sx, sz) + (S(sx + 1, sz) - S(sx, sz)) * tx;
        double a1 = S(sx, sz + 1) + (S(sx + 1, sz + 1) - S(sx, sz + 1)) * tx;
        return (float)((a0 + (a1 - a0) * tz) * Units.HeightScale);
    }

    /// <summary>
    /// Writes the road's profile into the ground through <paramref name="rec"/>. <paramref name="centres"/> and <paramref name="heights"/>
    /// are the road's centre line and its profile (metres).
    /// </summary>
    public static Result Conform(IMapDocument doc, HeightRecorder rec, IReadOnlyList<Vector2> centres, IReadOnlyList<float> heights, Settings? settings = null)
    {
        settings ??= new Settings();
        float reach = settings.Flat + settings.Shoulder;
        var best = new Dictionary<(int, int), (float D2, float H)>();
        for (int i = 0; i < centres.Count; i++)
        {
            // each segment (a lone point is a segment of no length) claims the samples nearest to it
            int j = Math.Min(i + 1, centres.Count - 1);
            Vector2 a = centres[i], b = centres[j];
            float ha = heights[i], hb = heights[j];
            int x0 = (int)Math.Floor((Math.Min(a.X, b.X) - reach) / Units.SampleSize), x1 = (int)Math.Ceiling((Math.Max(a.X, b.X) + reach) / Units.SampleSize);
            int z0 = (int)Math.Floor((Math.Min(a.Y, b.Y) - reach) / Units.SampleSize), z1 = (int)Math.Ceiling((Math.Max(a.Y, b.Y) + reach) / Units.SampleSize);
            var ab = b - a; float len2 = ab.LengthSquared();
            for (int sz = z0; sz <= z1; sz++)
                for (int sx = x0; sx <= x1; sx++)
                {
                    var p = new Vector2(sx * (float)Units.SampleSize, sz * (float)Units.SampleSize);
                    float t = len2 < 1e-9f ? 0 : Math.Clamp(Vector2.Dot(p - a, ab) / len2, 0, 1);
                    float d2 = Vector2.DistanceSquared(p, a + ab * t);
                    if (d2 > reach * reach) continue;
                    if (!best.TryGetValue((sx, sz), out var cur) || d2 < cur.D2) best[(sx, sz)] = (d2, ha + (hb - ha) * t);
                }
        }
        float maxCut = 0, maxFill = 0; int n = 0;
        foreach (var ((sx, sz), (d2, h)) in best)
        {
            if (!doc.Has(sx, sz)) continue;
            float d = MathF.Sqrt(d2);
            float w = d <= settings.Flat ? 1 : 1 - Smooth((d - settings.Flat) / settings.Shoulder);
            if (w <= 0) continue;
            double old = doc.GetHeight(sx, sz);
            double now = old + (h / Units.HeightScale - old) * w;
            int raw = (int)Math.Round(now);
            if (raw == (int)old) continue;
            float delta = (float)((raw - old) * Units.HeightScale);
            if (delta > 0) maxFill = Math.Max(maxFill, delta); else maxCut = Math.Max(maxCut, -delta);
            rec.Set(sx, sz, raw);
            n++;
        }
        return new Result(maxCut, maxFill, n, rec.Rect);
    }

    private static float Smooth(float t) { t = Math.Clamp(t, 0, 1); return t * t * (3 - 2 * t); }
}
