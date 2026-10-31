namespace I76.Formats.Imaging;

/// <summary>
/// A .tmt texture-material table, a port of the reference reader
/// (docs/formats/tmt-material.md): a 64-byte header, then 8-byte names.
/// </summary>
public sealed class TmtMaterial
{
    public const int HeaderSize = 64;
    public const int NameSize = 8;

    /// <summary>u32 @ 0x14: 1 = flat, 2 = grouped.</summary>
    public uint Kind { get; }
    public uint CountA { get; }
    public uint CountB { get; }
    /// <summary>float @ 0x28: frames per second when animated (the TMT header reader); 10.0 in every shipped file.</summary>
    public float Scale { get; }
    /// <summary>u32 @ 0x2c: bit 0 animated (the engine steps the frames by itself, the default texture animation tick), bit 1 loop.</summary>
    public uint Flags { get; }
    public bool Animated => (Flags & 1) != 0;
    public float Fps => Scale;
    public string[] Names { get; }

    public TmtMaterial(byte[] d)
    {
        if (d.Length < HeaderSize || (d.Length - HeaderSize) % NameSize != 0)
            throw new InvalidDataException($"bad .tmt length {d.Length}");
        Kind = Bin.U32(d, 0x14);
        CountA = Bin.U32(d, 0x18);
        CountB = Bin.U32(d, 0x1C);
        Scale = Bin.F32(d, 0x28);
        Flags = Bin.U32(d, 0x2c);
        int n = (d.Length - HeaderSize) / NameSize;
        Names = new string[n];
        for (int i = 0; i < n; i++)
            Names[i] = Bin.Latin1(d.AsSpan(HeaderSize + i * NameSize, NameSize)).TrimEnd('\0').TrimEnd();
    }

    /// <summary>Grouped files split evenly by CountB; flat files are one group.</summary>
    public List<string[]> Groups()
    {
        if (Kind == 2 && CountB != 0)
            return Names.Chunk((int)CountB).ToList();
        return [Names];
    }
}
