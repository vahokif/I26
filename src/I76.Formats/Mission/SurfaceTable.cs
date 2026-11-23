namespace I76.Formats.Mission;

/// <summary>
/// One row of a map's surface table (WRLD +0x97, docs/map-editor.md "Surface table"): what the ground of one surface id does to a car.
/// <paramref name="Grip"/> scales every tyre force (drive, brake, cornering, holding on a slope); <paramref name="Rolling"/> the rolling
/// resistance; <paramref name="Bump"/> the suspension's roughness input; <paramref name="Unk_0x0c"/> is read by nothing (0 in every stock
/// map); <paramref name="Damage"/> is the hit points a second a car takes driving forward faster than 7.65 m/s on it.
/// </summary>
public readonly record struct SurfaceRow(float Grip, float Rolling, float Bump, float Unk_0x0c, int Damage)
{
    /// <summary>The same bits (a row read and written back is the same row; NaN equals itself).</summary>
    public bool Same(SurfaceRow o) =>
        BitConverter.SingleToInt32Bits(Grip) == BitConverter.SingleToInt32Bits(o.Grip) && BitConverter.SingleToInt32Bits(Rolling) == BitConverter.SingleToInt32Bits(o.Rolling)
        && BitConverter.SingleToInt32Bits(Bump) == BitConverter.SingleToInt32Bits(o.Bump) && BitConverter.SingleToInt32Bits(Unk_0x0c) == BitConverter.SingleToInt32Bits(o.Unk_0x0c)
        && Damage == o.Damage;
}

/// <summary>
/// A map's surface table: eight rows of 20 bytes (grip, rolling, bump, an unread float, damage per second as an int), one per surface id
/// (the .ter sample's bits 13-15), at WRLD +0x97 (+0x8a in a WREV 8 level). The sim reads each car's row by the id under it.
/// </summary>
public sealed class SurfaceTable : IEquatable<SurfaceTable>
{
    public const int Count = 8, Stride = 20, At = 0x97, Size = Count * Stride;

    private readonly SurfaceRow[] _rows;

    public SurfaceTable(IEnumerable<SurfaceRow> rows)
    {
        _rows = rows.ToArray();
        if (_rows.Length != Count) throw new ArgumentException($"a surface table has {Count} rows, not {_rows.Length}");
    }

    public IReadOnlyList<SurfaceRow> Rows => _rows;
    public SurfaceRow this[int id] => _rows[id & 7];

    /// <summary>This table with row <paramref name="id"/> replaced.</summary>
    public SurfaceTable With(int id, SurfaceRow row)
    {
        var r = (SurfaceRow[])_rows.Clone();
        r[id & 7] = row;
        return new SurfaceTable(r);
    }

    /// <summary>
    /// The table 92 of the 114 stock missions of both games hold (all but a few of the campaign maps and Nitro arenas): dirt 1.9,
    /// sand 1.5 with 3 damage a second, asphalt 2.1, dirt road 1.9, planks 1.5, rough ground 1.5 with 12 a second, parking lot 2.1,
    /// dirt junction 1.9.
    /// </summary>
    public static readonly SurfaceTable Stock = new(
    [
        new(1.9f, 0.03f, 0, 0, 0), new(1.5f, 0.1f, 0, 0, 3), new(2.1f, 0.01f, 0, 0, 0), new(1.9f, 0.03f, 0, 0, 0),
        new(1.5f, 0.08f, 0, 0, 0), new(1.5f, 0.2f, 0, 0, 12), new(2.1f, 0.01f, 0, 0, 0), new(1.9f, 0.03f, 0, 0, 0),
    ]);

    private static int Offset(uint revision) => At - (revision == 8 ? WorldSettings.OldLayoutShift : 0);

    /// <summary>The table of a WRLD payload of this WREV, or null if the chunk is too short. <paramref name="offset"/>: where the payload starts.</summary>
    public static SurfaceTable? Read(byte[] wrld, uint revision, int offset = 0)
    {
        int at = offset + Offset(revision);
        if (wrld.Length < at + Size) return null;
        var rows = new SurfaceRow[Count];
        for (int i = 0; i < Count; i++)
        {
            int p = at + i * Stride;
            rows[i] = new SurfaceRow(BitConverter.ToSingle(wrld, p), BitConverter.ToSingle(wrld, p + 4), BitConverter.ToSingle(wrld, p + 8),
                BitConverter.ToSingle(wrld, p + 12), BitConverter.ToInt32(wrld, p + 16));
        }
        return new SurfaceTable(rows);
    }

    /// <summary>The table of the game's settings (<see cref="WorldSettings.Unk_0x97"/>).</summary>
    public static SurfaceTable Of(WorldSettings w) => Read(w.Unk_0x97, 9, -At)!;

    /// <summary>Writes the rows that differ into a WRLD payload of this WREV (a table read and written back changes nothing).</summary>
    public void WriteTo(byte[] wrld, uint revision, int offset = 0)
    {
        int at = offset + Offset(revision);
        if (wrld.Length < at + Size) throw new InvalidOperationException("no WRLD chunk with a surface table");
        var old = Read(wrld, revision, offset)!;
        for (int i = 0; i < Count; i++)
        {
            if (old[i].Same(_rows[i])) continue;
            var p = wrld.AsSpan(at + i * Stride, Stride);
            var r = _rows[i];
            BitConverter.TryWriteBytes(p, r.Grip); BitConverter.TryWriteBytes(p[4..], r.Rolling); BitConverter.TryWriteBytes(p[8..], r.Bump);
            BitConverter.TryWriteBytes(p[12..], r.Unk_0x0c); BitConverter.TryWriteBytes(p[16..], r.Damage);
        }
    }

    /// <summary>Writes a table over the WRLD of a mission read into memory (the map editor's playtest: the sim reads it from there; the file is not touched). False if there is no WRLD.</summary>
    public bool Patch(MissionFile msn)
    {
        uint rev = msn.WorldRevision();
        foreach (var c in msn.File.Chunks.Where(c => c.Tag == "WDEF"))
        {
            var body = msn.Data.AsSpan(c.PayloadOffset, c.PayloadSize).ToArray();
            foreach (var sc in MissionFile.SubChunks(body))
                if (sc.Tag == "WRLD" && sc.PayloadSize >= Offset(rev) + Size) { WriteTo(msn.Data, rev, c.PayloadOffset + sc.PayloadOffset); return true; }
        }
        return false;
    }

    public bool Equals(SurfaceTable? o) => o is not null && _rows.Zip(o._rows).All(p => p.First.Same(p.Second));
    public override bool Equals(object? o) => Equals(o as SurfaceTable);
    public override int GetHashCode()
    {
        var h = new HashCode();
        foreach (var r in _rows) { h.Add(BitConverter.SingleToInt32Bits(r.Grip)); h.Add(r.Damage); }
        return h.ToHashCode();
    }
    public static bool operator ==(SurfaceTable? a, SurfaceTable? b) => a is null ? b is null : a.Equals(b);
    public static bool operator !=(SurfaceTable? a, SurfaceTable? b) => !(a == b);
}
