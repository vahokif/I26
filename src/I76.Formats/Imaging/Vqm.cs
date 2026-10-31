namespace I76.Formats.Imaging;

/// <summary>A .cbk codebook: count, then 4×4 blocks of 8-bit indices.</summary>
public sealed class Codebook
{
    public int Count { get; }
    public byte[] Blocks { get; }

    public Codebook(byte[] d)
    {
        Count = (int)Bin.U32(d, 0);
        if (4 + (long)Count * 16 != d.Length)
            throw new InvalidDataException($".cbk size mismatch: predicted {4 + (long)Count * 16}, actual {d.Length}");
        Blocks = d.AsSpan(4).ToArray();
    }

    /// <summary>An index past the end reads as a zero block, as the reference reader does.</summary>
    public ReadOnlySpan<byte> Block(int i) => i >= Count ? new byte[16] : Blocks.AsSpan(i * 16, 16);
}

/// <summary>
/// A .vqm vector-quantised image, a port of the reference reader
/// (docs/formats/vqm-image.md): u16 codes per 4×4 block; bit 15 marks a solid
/// block whose colour is the low byte.
/// </summary>
public sealed class VqmImage
{
    public const int BlockSize = 4;
    public const int HeaderSize = 24;
    public const ushort SolidFlag = 0x8000;

    public int Width { get; }
    public int Height { get; }
    public string CodebookName { get; }
    public ushort[] Codes { get; }
    public int BlocksX => (Width + BlockSize - 1) / BlockSize;
    public int BlocksY => (Height + BlockSize - 1) / BlockSize;

    public VqmImage(byte[] d)
    {
        Width = (int)Bin.U32(d, 0);
        Height = (int)Bin.U32(d, 4);
        CodebookName = Bin.CStr(d, 8, 16);
        long n = (long)BlocksX * BlocksY;
        if (HeaderSize + 2 * n != d.Length)
            throw new InvalidDataException($".vqm size mismatch: predicted {HeaderSize + 2 * n}, actual {d.Length}");
        Codes = new ushort[n];
        for (int i = 0; i < n; i++) Codes[i] = Bin.U16(d, HeaderSize + 2 * i);
    }

    /// <summary>The 8-bit index image, padded to whole blocks.</summary>
    public IndexedTexture Decode(Codebook cb)
    {
        int w = BlocksX * BlockSize, h = BlocksY * BlockSize;
        var img = new byte[w * h];
        var solid = new byte[16];
        for (int r0 = 0; r0 < BlocksY; r0++)
            for (int c0 = 0; c0 < BlocksX; c0++)
            {
                ushort code = Codes[r0 * BlocksX + c0];
                ReadOnlySpan<byte> blk;
                if ((code & SolidFlag) != 0) { Array.Fill(solid, (byte)(code & 0xFF)); blk = solid; }
                else blk = cb.Block(code);
                for (int r = 0; r < BlockSize; r++)
                    blk.Slice(r * BlockSize, BlockSize).CopyTo(img.AsSpan((r0 * BlockSize + r) * w + c0 * BlockSize));
            }
        return new IndexedTexture(w, h, img, null);
    }
}
