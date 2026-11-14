using System.Numerics;

namespace I76.Formats.Vegetation;

/// <summary>
/// The modern look's brush impacts (docs/modern-vegetation.md, Brush impacts): a square of cells round the camera that
/// says how the plants standing in each are disturbed. The vegetation shader reads it as a texture at each plant's root:
/// laid down (bent over away from a car or a blast, rising again over <see cref="Recover"/> seconds), the way it lies,
/// scorched by a fire, and shaken (a round or a blast, dying away in about a second). Pure: the game moves it with the
/// camera, stamps it and uploads <see cref="Encode"/>; coordinates are the drawing's ground plane (x, z).
/// </summary>
public sealed class BrushField
{
    public const int Size = 128;
    public const float Cell = 1.5f;
    /// <summary>A plant laid fully down is standing again after this many seconds (it rises linearly; the shader eases it).</summary>
    public const float Recover = 15f;
    /// <summary>The shake halves in this many seconds.</summary>
    public const float ShakeHalf = 0.3f;
    /// <summary>The field moves in steps of whole cells once the camera is this many cells off its middle.</summary>
    private const int Step = 16;

    private readonly float[] _lay = new float[Size * Size], _dx = new float[Size * Size], _dz = new float[Size * Size];
    private readonly float[] _shake = new float[Size * Size], _scorch = new float[Size * Size];
    private readonly float[] _scratch = new float[Size * Size];
    private readonly byte[] _ang = new byte[Size * Size];
    // the cells that may be disturbed (inclusive; empty when _bi1 < _bi0): decay and encoding look at no others
    private int _bi0 = Size, _bj0 = Size, _bi1 = -1, _bj1 = -1;
    // the cells changed since the last encode
    private int _di0 = 0, _dj0 = 0, _di1 = Size - 1, _dj1 = Size - 1;
    /// <summary>The cell at the field's corner (whole cells of the ground plane).</summary>
    public int X0 { get; private set; }
    public int Z0 { get; private set; }
    private bool _placed;
    /// <summary>Cells changed since the last <see cref="Encode"/>.</summary>
    public bool Dirty { get; private set; }
    /// <summary>How many cells are disturbed (none: nothing to decay).</summary>
    public int Active { get; private set; }

    public float MinX => X0 * Cell;
    public float MinZ => Z0 * Cell;

    /// <summary>Centres the field on the camera when it has moved off the middle, keeping the cells it still covers.</summary>
    public void Follow(float x, float z)
    {
        int cx = (int)MathF.Floor(x / Cell) - Size / 2, cz = (int)MathF.Floor(z / Cell) - Size / 2;
        if (!_placed) { X0 = cx; Z0 = cz; _placed = true; Touch(0, 0, Size - 1, Size - 1); return; }
        if (Math.Abs(cx - X0) < Step && Math.Abs(cz - Z0) < Step) return;
        int sx = cx - X0, sz = cz - Z0;
        if (Active > 0)
            foreach (var a in new[] { _lay, _dx, _dz, _shake, _scorch })
            {
                Array.Copy(a, _scratch, a.Length);
                Array.Clear(a);
                for (int j = 0; j < Size; j++)
                {
                    int oj = j + sz;
                    if (oj < 0 || oj >= Size) continue;
                    for (int i = 0; i < Size; i++)
                    {
                        int oi = i + sx;
                        if (oi >= 0 && oi < Size) a[j * Size + i] = _scratch[oj * Size + oi];
                    }
                }
            }
        X0 = cx; Z0 = cz;
        if (Active > 0) for (int k = 0; k < _ang.Length; k++) _ang[k] = Angle(_dx[k], _dz[k]);
        _bi0 = 0; _bj0 = 0; _bi1 = Size - 1; _bj1 = Size - 1;
        Touch(0, 0, Size - 1, Size - 1);
    }

    private bool Index(float x, float z, out int k)
    {
        int i = (int)MathF.Floor(x / Cell) - X0, j = (int)MathF.Floor(z / Cell) - Z0;
        k = j * Size + i;
        return i >= 0 && j >= 0 && i < Size && j < Size;
    }

    public float Lay(float x, float z) => Index(x, z, out int k) ? _lay[k] : 0;
    public float Shake(float x, float z) => Index(x, z, out int k) ? _shake[k] : 0;
    public float Scorch(float x, float z) => Index(x, z, out int k) ? _scorch[k] : 0;
    /// <summary>The way the plants in the cell lie (a unit vector; zero when none was set).</summary>
    public Vector2 Direction(float x, float z) => Index(x, z, out int k) ? new Vector2(_dx[k], _dz[k]) : Vector2.Zero;

    /// <summary>
    /// Disturbs the cells within <paramref name="radius"/> of (x, z), full over the inner part and fading to the edge: each
    /// cell keeps the stronger of what it had and this. The plants lie along <paramref name="way"/> (a unit vector) or, when
    /// null, away from the middle; <paramref name="sweep"/> bends the way outward toward the stamp's sides (a car: its
    /// heading, the plants beside it pushed out too).
    /// </summary>
    public void Stamp(float x, float z, float radius, float lay, float shake, float scorch = 0, Vector2? way = null, float sweep = 0)
    {
        int i0 = (int)MathF.Floor((x - radius) / Cell) - X0, i1 = (int)MathF.Floor((x + radius) / Cell) - X0;
        int j0 = (int)MathF.Floor((z - radius) / Cell) - Z0, j1 = (int)MathF.Floor((z + radius) / Cell) - Z0;
        for (int j = Math.Max(j0, 0); j <= Math.Min(j1, Size - 1); j++)
            for (int i = Math.Max(i0, 0); i <= Math.Min(i1, Size - 1); i++)
            {
                var d = new Vector2((X0 + i + 0.5f) * Cell - x, (Z0 + j + 0.5f) * Cell - z);
                float r = d.Length();
                if (r > radius + Cell * 0.5f) continue;
                float w = Math.Clamp((1f - MathF.Max(0, r - Cell * 0.5f) / radius) * 1.6f, 0, 1);
                int k = j * Size + i;
                Vector2 dir;
                if (way is { } wv)
                {
                    var side = d - wv * Vector2.Dot(d, wv);
                    float sl = side.Length();
                    dir = wv + (sl > 1e-3f ? side / sl * sweep * MathF.Min(sl / radius * 2f, 1f) : Vector2.Zero);
                }
                else dir = r > 1e-3f ? d / r : Vector2.Zero;
                float dl = dir.Length();
                float nl = lay * w;
                if (nl > 0 && dl > 1e-3f)
                {
                    // the way follows the stronger push
                    if (nl >= _lay[k] * 0.8f) { _dx[k] = dir.X / dl; _dz[k] = dir.Y / dl; _ang[k] = Angle(_dx[k], _dz[k]); }
                    _lay[k] = MathF.Max(_lay[k], nl);
                }
                if (shake > 0)
                {
                    if (_lay[k] <= 0 && dl > 1e-3f) { _dx[k] = dir.X / dl; _dz[k] = dir.Y / dl; _ang[k] = Angle(_dx[k], _dz[k]); }
                    _shake[k] = MathF.Max(_shake[k], shake * w);
                }
                if (scorch > 0) _scorch[k] = MathF.Max(_scorch[k], scorch * w);
            }
        int a0 = Math.Max(i0, 0), b0 = Math.Max(j0, 0), a1 = Math.Min(i1, Size - 1), b1 = Math.Min(j1, Size - 1);
        if (a1 < a0 || b1 < b0) return;
        _bi0 = Math.Min(_bi0, a0); _bj0 = Math.Min(_bj0, b0); _bi1 = Math.Max(_bi1, a1); _bj1 = Math.Max(_bj1, b1);
        Touch(a0, b0, a1, b1);
        Active = Math.Max(Active, 1);
    }

    private void Touch(int i0, int j0, int i1, int j1)
    {
        if (!Dirty) { _di0 = i0; _dj0 = j0; _di1 = i1; _dj1 = j1; }
        else { _di0 = Math.Min(_di0, i0); _dj0 = Math.Min(_dj0, j0); _di1 = Math.Max(_di1, i1); _dj1 = Math.Max(_dj1, j1); }
        Dirty = true;
    }

    private static byte Angle(float dx, float dz)
    {
        float a = MathF.Atan2(dz, dx) / (2 * MathF.PI);
        if (a < 0) a += 1;
        return (byte)((int)(a * 256 + 0.5f) & 255);
    }

    /// <summary>The plants rise again and the shake dies away; a scorch stays while the field covers it.</summary>
    public void Decay(float dt)
    {
        if (Active == 0 || _bi1 < _bi0) { Active = 0; return; }
        float rise = dt / Recover, keep = MathF.Pow(0.5f, dt / ShakeHalf);
        int busy = 0;
        bool moving = false;
        int ni0 = Size, nj0 = Size, ni1 = -1, nj1 = -1;
        for (int j = _bj0; j <= _bj1; j++)
            for (int i = _bi0; i <= _bi1; i++)
            {
                int k = j * Size + i;
                bool any = false;
                if (_lay[k] > 0) { _lay[k] = MathF.Max(0, _lay[k] - rise); moving = true; any = true; }
                if (_shake[k] > 0) { _shake[k] = _shake[k] < 0.01f ? 0 : _shake[k] * keep; moving = true; any = true; }
                if (_scorch[k] > 0) any = true;
                if (!any) continue;
                busy++;
                ni0 = Math.Min(ni0, i); nj0 = Math.Min(nj0, j); ni1 = Math.Max(ni1, i); nj1 = Math.Max(nj1, j);
            }
        // the cells that came to rest this step are written once more
        if (moving) Touch(_bi0, _bj0, _bi1, _bj1);
        _bi0 = ni0; _bj0 = nj0; _bi1 = ni1; _bj1 = nj1;
        Active = busy;
    }

    /// <summary>
    /// The field as RGBA8 texels, row by row (z) from (<see cref="MinX"/>, <see cref="MinZ"/>): r laid down (0..1), g the way
    /// it lies as an angle (atan2(z, x) over a turn), b scorched, a shaken. Only the cells changed since the last call are
    /// written: <paramref name="into"/> is the same buffer every time.
    /// </summary>
    public void Encode(byte[] into)
    {
        if (Dirty)
            for (int j = _dj0; j <= _dj1; j++)
                for (int i = _di0; i <= _di1; i++)
                {
                    int k = j * Size + i;
                    into[k * 4] = (byte)(Math.Clamp(_lay[k], 0, 1) * 255 + 0.5f);
                    into[k * 4 + 1] = _ang[k];
                    into[k * 4 + 2] = (byte)(Math.Clamp(_scorch[k], 0, 1) * 255 + 0.5f);
                    into[k * 4 + 3] = (byte)(Math.Clamp(_shake[k], 0, 1) * 255 + 0.5f);
                }
        Dirty = false;
    }

    public void Clear()
    {
        foreach (var a in new[] { _lay, _dx, _dz, _shake, _scorch }) Array.Clear(a);
        Array.Clear(_ang);
        Active = 0;
        _bi0 = Size; _bj0 = Size; _bi1 = -1; _bj1 = -1;
        Touch(0, 0, Size - 1, Size - 1);
    }
}

/// <summary>One plant as the brush impacts see it: its root (game coordinates), its crown's reach and height, how stiff it is, whether it is of the ground layer.</summary>
public readonly record struct PlantSpot(Vector3 Root, float Radius, float Height, float Stiffness, bool Ground)
{
    /// <summary>What burns: the leafy shrubs and the grass, not the cacti, yuccas and succulents (the stiff ones).</summary>
    public bool Burns => Stiffness < 0.6f;
}

/// <summary>A chunk's plants in 8 m buckets, made with the chunk on its worker thread, for the impacts to find the plants about a point.</summary>
public sealed class PlantIndex
{
    public const float Bucket = 8f;
    /// <summary>The widest crown's reach past its root (metres): how far past a bucket a query looks.</summary>
    private const float Reach = 3f;
    private readonly float _x0, _z0;
    private readonly int _w;
    private readonly int[] _start;
    private readonly PlantSpot[] _spots;

    public int Count => _spots.Length;

    public PlantIndex(float x0, float z0, float side, IReadOnlyList<PlantSpot> spots)
    {
        _x0 = x0; _z0 = z0;
        _w = Math.Max(1, (int)MathF.Ceiling(side / Bucket));
        _start = new int[_w * _w + 1];
        var bucket = new int[spots.Count];
        for (int i = 0; i < spots.Count; i++) _start[(bucket[i] = BucketOf(spots[i].Root.X, spots[i].Root.Z)) + 1]++;
        for (int b = 0; b < _w * _w; b++) _start[b + 1] += _start[b];
        _spots = new PlantSpot[spots.Count];
        var fill = (int[])_start.Clone();
        for (int i = 0; i < spots.Count; i++) _spots[fill[bucket[i]]++] = spots[i];
    }

    private int BucketOf(float x, float z) =>
        Math.Clamp((int)MathF.Floor((z - _z0) / Bucket), 0, _w - 1) * _w + Math.Clamp((int)MathF.Floor((x - _x0) / Bucket), 0, _w - 1);

    /// <summary>The plants whose crown reaches within <paramref name="radius"/> of (x, z) on the ground plane, added to <paramref name="into"/> up to <paramref name="max"/> in all.</summary>
    public void Near(float x, float z, float radius, List<PlantSpot> into, int max = int.MaxValue)
    {
        int i0 = Math.Clamp((int)MathF.Floor((x - radius - Reach - _x0) / Bucket), 0, _w - 1), i1 = Math.Clamp((int)MathF.Floor((x + radius + Reach - _x0) / Bucket), 0, _w - 1);
        int j0 = Math.Clamp((int)MathF.Floor((z - radius - Reach - _z0) / Bucket), 0, _w - 1), j1 = Math.Clamp((int)MathF.Floor((z + radius + Reach - _z0) / Bucket), 0, _w - 1);
        for (int j = j0; j <= j1; j++)
            for (int i = i0; i <= i1; i++)
            {
                int b = j * _w + i;
                for (int k = _start[b]; k < _start[b + 1]; k++)
                {
                    if (into.Count >= max) return;
                    var s = _spots[k];
                    float dx = s.Root.X - x, dz = s.Root.Z - z, r = radius + s.Radius;
                    if (dx * dx + dz * dz <= r * r) into.Add(s);
                }
            }
    }
}
