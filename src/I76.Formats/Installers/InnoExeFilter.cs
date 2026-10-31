namespace I76.Formats.Installers;

/// <summary>
/// Undoes Inno Setup's x86 call/jump filter ("CallInstructionOptimized"): E8/E9 operands were turned from relative into
/// absolute addresses before compression. Three generations: 4.1.8 (every E8/E9, 32-bit), 5.2.0 (in 64 KiB blocks,
/// only operands whose high byte is 00 or FF, 24-bit) and 5.3.9 (5.2.0 that also flips the high byte with bit 23).
/// </summary>
public sealed class InnoExeFilter : ReadOnlyStream
{
    private const int Block = 0x10000;
    private readonly Stream _src;
    private readonly InnoFilter _kind;
    // 4108: the running state
    private uint _addr, _addrOffset = 5;
    private int _addrLeft;
    // 5200/5309: one decoded block at a time
    private readonly byte[]? _block;
    private int _bpos, _blen;
    private long _blockStart;

    public InnoExeFilter(Stream src, InnoFilter kind)
    {
        _src = src;
        _kind = kind;
        if (kind is InnoFilter.Call5200 or InnoFilter.Call5309) _block = new byte[Block];
    }

    public override int Read(Span<byte> buffer)
    {
        if (_kind == InnoFilter.None) return _src.Read(buffer);
        if (_kind == InnoFilter.Call4108)
        {
            int n = _src.Read(buffer);
            Decode4108(buffer[..n]);
            return n;
        }
        int o = 0;
        while (o < buffer.Length)
        {
            if (_bpos == _blen)
            {
                _blockStart += _blen;
                _blen = ReadFull(_block!);
                _bpos = 0;
                if (_blen == 0) break;
                Decode5200(_block.AsSpan(0, _blen), _blockStart, _kind == InnoFilter.Call5309);
            }
            int k = Math.Min(buffer.Length - o, _blen - _bpos);
            _block.AsSpan(_bpos, k).CopyTo(buffer[o..]);
            _bpos += k; o += k;
        }
        return o;
    }

    private int ReadFull(byte[] b)
    {
        int n = 0;
        while (n < b.Length)
        {
            int k = _src.Read(b, n, b.Length - n);
            if (k <= 0) break;
            n += k;
        }
        return n;
    }

    private void Decode4108(Span<byte> d)
    {
        for (int i = 0; i < d.Length; i++, _addrOffset++)
        {
            byte b = d[i];
            if (_addrLeft == 0)
            {
                if (b is 0xE8 or 0xE9) { _addr = ~_addrOffset + 1; _addrLeft = 4; }
            }
            else
            {
                _addr += b;
                d[i] = (byte)_addr;
                _addr >>= 8;
                _addrLeft--;
            }
        }
    }

    /// <summary>One 64 KiB block (the last may be shorter); <paramref name="start"/> is its offset in the file.</summary>
    public static void Decode5200(Span<byte> d, long start, bool flipHighByte)
    {
        for (int i = 0; i + 4 < d.Length; i++)
        {
            if (d[i] is not (0xE8 or 0xE9)) continue;
            if (d[i + 4] is 0x00 or 0xFF)
            {
                uint addr = (uint)(start + i + 5) & 0xFFFFFF;
                uint rel = (uint)(d[i + 1] | d[i + 2] << 8 | d[i + 3] << 16) - addr;
                d[i + 1] = (byte)rel;
                d[i + 2] = (byte)(rel >> 8);
                d[i + 3] = (byte)(rel >> 16);
                if (flipHighByte && (rel & 0x800000) != 0) d[i + 4] = (byte)~d[i + 4];
            }
            i += 4;
        }
    }
}
