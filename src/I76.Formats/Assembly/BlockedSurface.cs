using System.Collections.Concurrent;
using System.Numerics;
using I76.Formats.Mission;

namespace I76.Formats.Assembly;

/// <summary>
/// The modern look's terrain shape for a map too big to hold in one piece (a community map that fills all 6400 cells of the
/// grid: a 51 km square, 105 million samples; <see cref="TerrainSurface"/> keeps a dozen arrays of that). The samples are cut
/// into blocks of <see cref="Block"/> by <see cref="Block"/>; each is a <see cref="TerrainSurface"/> over its own samples and a
/// border of <see cref="Halo"/> (so its rims, blur and lean-back cone come out as they would over the whole map: the cone
/// reaches 21 samples at most), made when a point in it is first asked for, and the least used are let go. Seedless: every
/// gentle cell is open (reachability needs the whole map); the cells under placed objects are pinned as on a whole map
/// (<paramref name="pins"/>). A query is answered by the block its point lies in.
/// </summary>
public sealed class BlockedSurface(TerrainTiles tiles, int maxBlocks = 48, IReadOnlyList<GroundBox>? pins = null) : ITerrainSurface
{
    public const int Block = 256;
    public const int Halo = 48;

    private readonly ConcurrentDictionary<(int, int), Lazy<TerrainSurface>> _blocks = new();
    private readonly object _trim = new();
    private long _tick;
    private readonly ConcurrentDictionary<(int, int), long> _used = new();
    [ThreadStatic] private static (BlockedSurface? Owner, int Bx, int Bz, TerrainSurface? S) _last;

    public int BlockCount => _blocks.Count;

    private TerrainSurface For(double fx, double fz)
    {
        int bx = (int)Math.Floor(fx / Block), bz = (int)Math.Floor(fz / Block);
        var l = _last;
        if (l.Owner == this && l.Bx == bx && l.Bz == bz) return l.S!;
        var key = (bx, bz);
        var lazy = _blocks.GetOrAdd(key, k => new Lazy<TerrainSurface>(() =>
            new TerrainSurface(tiles, (k.Item1 * Block - Halo, k.Item2 * Block - Halo, Block + 2 * Halo, Block + 2 * Halo), pins)));
        var s = lazy.Value;
        _used[key] = Interlocked.Increment(ref _tick);
        if (_blocks.Count > maxBlocks) Trim();
        _last = (this, bx, bz, s);
        return s;
    }

    private readonly ConcurrentDictionary<(int, int), Task> _making = new();

    /// <summary>The block holding sample (sx, sz), made on the pool; done when it is there (so the callers waiting do not hold the pool's threads).</summary>
    public Task Ready(int sx, int sz)
    {
        var key = ((int)Math.Floor(sx / (double)Block), (int)Math.Floor(sz / (double)Block));
        if (_blocks.TryGetValue(key, out var have) && have.IsValueCreated) return Task.CompletedTask;
        return _making.GetOrAdd(key, k0 => Task.Run(() => { For(sx, sz); _making.TryRemove(key, out _); }));
    }

    private void Trim()
    {
        lock (_trim)
        {
            if (_blocks.Count <= maxBlocks) return;
            // the least recently asked for go; a thread's own last block is held by it and stays valid
            foreach (var k in _used.OrderBy(u => u.Value).Take(_blocks.Count - maxBlocks * 3 / 4).Select(u => u.Key).ToList())
            {
                _blocks.TryRemove(k, out _);
                _used.TryRemove(k, out _);
            }
        }
    }

    public Vector3 Point(double fx, double fz, out double shadeY) => For(fx, fz).Point(fx, fz, out shadeY);
    public double Bilinear(double fx, double fz) => For(fx, fz).Bilinear(fx, fz);
    public Vector3 BroadNormal(double fx, double fz) => For(fx, fz).BroadNormal(fx, fz);
    public bool Open(int sx, int sz) => For(sx, sz).Open(sx, sz);
    public (float Min, float Max) HeightRange(int sx, int sz, int size) => For(sx, sz).HeightRange(sx, sz, size);
}
