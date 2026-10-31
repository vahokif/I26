using System.Runtime.CompilerServices;

namespace I76.Formats.Installers;

// A compact LZMA / LZMA2 decoder, written for the installer reader from the format as the public-domain LZMA SDK
// (Igor Pavlov, https://www.7-zip.org/sdk.html) and xz (Lasse Collin) document it. Streaming: the window is the
// only large allocation, capped at the dictionary size or the caller's known output size, whichever is smaller.

/// <summary>The range decoder's input: a buffer refilled from a stream, or a fixed chunk (LZMA2) padded with zeros.</summary>
internal sealed class LzmaInput
{
    private readonly Stream? _src;
    private byte[] _buf;
    private int _pos, _end;
    /// <summary>Bytes handed out past the end of the input (zeros); a few are normal at the very end of a stream.</summary>
    public int Overrun;

    public LzmaInput(Stream src) { _src = src; _buf = new byte[1 << 16]; }
    public LzmaInput() { _buf = new byte[1 << 16]; }

    /// <summary>Sets a fixed chunk to read (LZMA2's compressed chunk); reads past it give zeros.</summary>
    public void SetChunk(byte[] data, int length) { _buf = data; _pos = 0; _end = length; Overrun = 0; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte Next()
    {
        if (_pos < _end) return _buf[_pos++];
        return Refill();
    }

    private byte Refill()
    {
        if (_src is not null)
        {
            _end = _src.Read(_buf, 0, _buf.Length);
            _pos = 0;
            if (_end > 0) return _buf[_pos++];
            _end = 0;
        }
        if (++Overrun > 64) throw new InvalidDataException("LZMA: compressed data ends early (truncated stream)");
        return 0;
    }
}

internal sealed class RangeDecoder
{
    private const uint Top = 1u << 24;
    private uint _range, _code;
    private LzmaInput _in = null!;

    public void Init(LzmaInput input)
    {
        _in = input;
        _code = 0;
        _range = 0xFFFFFFFF;
        if (input.Next() != 0) throw new InvalidDataException("LZMA: bad range coder start");
        for (int i = 0; i < 4; i++) _code = (_code << 8) | input.Next();
        if (_code == _range) throw new InvalidDataException("LZMA: bad range coder start");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Bit(ref ushort p)
    {
        uint bound = (_range >> 11) * p;
        int bit;
        if (_code < bound)
        {
            _range = bound;
            p += (ushort)((2048 - p) >> 5);
            bit = 0;
        }
        else
        {
            _range -= bound;
            _code -= bound;
            p -= (ushort)(p >> 5);
            bit = 1;
        }
        if (_range < Top) { _range <<= 8; _code = (_code << 8) | _in.Next(); }
        return bit;
    }

    public uint Direct(int count)
    {
        uint result = 0;
        for (; count > 0; count--)
        {
            _range >>= 1;
            uint t = (_code - _range) >> 31;   // 1 if code < range
            _code -= _range & (t - 1);
            result = (result << 1) | (1 - t);
            if (_range < Top) { _range <<= 8; _code = (_code << 8) | _in.Next(); }
        }
        return result;
    }

    public int Tree(ushort[] probs, int offset, int bits)
    {
        int m = 1;
        for (int i = 0; i < bits; i++) m = (m << 1) | Bit(ref probs[offset + m]);
        return m - (1 << bits);
    }

    public int ReverseTree(ushort[] probs, int offset, int bits)
    {
        int m = 1, sym = 0;
        for (int i = 0; i < bits; i++)
        {
            int b = Bit(ref probs[offset + m]);
            m = (m << 1) | b;
            sym |= b << i;
        }
        return sym;
    }
}

/// <summary>The LZMA decoding core: probabilities, state and the window (shared by the LZMA1 and LZMA2 streams).</summary>
internal sealed class LzmaCore
{
    private const int States = 12, PosStatesMax = 16, EndPosModel = 14, FullDistances = 128, AlignBits = 4;

    private int _lc, _lp, _pb;
    private ushort[] _literal = [];
    private readonly ushort[] _isMatch = new ushort[States * PosStatesMax];
    private readonly ushort[] _isRep = new ushort[States], _isRepG0 = new ushort[States], _isRepG1 = new ushort[States], _isRepG2 = new ushort[States];
    private readonly ushort[] _isRep0Long = new ushort[States * PosStatesMax];
    private readonly ushort[] _posSlot = new ushort[4 * 64];
    private readonly ushort[] _posSpecial = new ushort[1 + FullDistances - EndPosModel];
    private readonly ushort[] _align = new ushort[1 << AlignBits];
    private readonly LenDecoder _len = new(), _repLen = new();

    private int _state;
    private uint _rep0, _rep1, _rep2, _rep3;

    private readonly byte[] _window;
    private int _wpos;
    /// <summary>Bytes since the last dictionary reset (the position contexts count from there).</summary>
    public long Total;
    private int _pendingLen;
    public bool EndMarker;

    public readonly RangeDecoder Rc = new();

    public LzmaCore(int windowSize) => _window = new byte[Math.Max(windowSize, 4096)];

    public static (int lc, int lp, int pb) SplitProps(byte props)
    {
        if (props >= 9 * 5 * 5) throw new InvalidDataException($"LZMA: bad properties byte {props}");
        return (props % 9, props / 9 % 5, props / 45);
    }

    public void SetProps(int lc, int lp, int pb)
    {
        if (lc > 8 || lp > 4 || pb > 4) throw new InvalidDataException("LZMA: bad lc/lp/pb");
        _lc = lc; _lp = lp; _pb = pb;
        int n = 0x300 << (lc + lp);
        if (_literal.Length != n) _literal = new ushort[n];
    }

    public void ResetDictionary() { Total = 0; _wpos = 0; _pendingLen = 0; }

    public void ResetState()
    {
        Array.Fill(_literal, (ushort)1024);
        foreach (var a in new[] { _isMatch, _isRep, _isRepG0, _isRepG1, _isRepG2, _isRep0Long, _posSlot, _posSpecial, _align })
            Array.Fill(a, (ushort)1024);
        _len.Reset(); _repLen.Reset();
        _state = 0;
        _rep0 = _rep1 = _rep2 = _rep3 = 0;
        _pendingLen = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte WindowAt(uint dist)   // the byte dist+1 back
    {
        int i = _wpos - (int)dist - 1;
        if (i < 0) i += _window.Length;
        return _window[i];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Put(byte b, Span<byte> output, ref int o)
    {
        _window[_wpos] = b;
        if (++_wpos == _window.Length) _wpos = 0;
        Total++;
        output[o++] = b;
    }

    /// <summary>Uncompressed bytes (LZMA2's stored chunks) go into the window as decoded data.</summary>
    public void PutRaw(ReadOnlySpan<byte> data)
    {
        foreach (var b in data)
        {
            _window[_wpos] = b;
            if (++_wpos == _window.Length) _wpos = 0;
        }
        Total += data.Length;
    }

    public bool HasPending => _pendingLen > 0;

    /// <summary>Decodes up to output.Length bytes; fewer only at the end marker.</summary>
    public int Decode(Span<byte> output)
    {
        int o = 0;
        int pbMask = (1 << _pb) - 1, lpMask = (1 << _lp) - 1;
        var rc = Rc;
        var lit = _literal;
        while (o < output.Length)
        {
            if (_pendingLen > 0)
            {
                byte b = WindowAt(_rep0);
                Put(b, output, ref o);
                _pendingLen--;
                continue;
            }
            if (EndMarker) break;
            int posState = (int)Total & pbMask;
            if (rc.Bit(ref _isMatch[(_state << 4) + posState]) == 0)
            {
                int prev = Total > 0 ? WindowAt(0) : 0;
                int baseIdx = 0x300 * ((((int)Total & lpMask) << _lc) + (prev >> (8 - _lc)));
                int sym = 1;
                if (_state >= 7)
                {
                    int match = WindowAt(_rep0);
                    do
                    {
                        int mb = (match >> 7) & 1;
                        match <<= 1;
                        int bit = rc.Bit(ref lit[baseIdx + ((1 + mb) << 8) + sym]);
                        sym = (sym << 1) | bit;
                        if (mb != bit) break;
                    } while (sym < 0x100);
                }
                while (sym < 0x100) sym = (sym << 1) | rc.Bit(ref lit[baseIdx + sym]);
                Put((byte)sym, output, ref o);
                _state = _state < 4 ? 0 : _state < 10 ? _state - 3 : _state - 6;
                continue;
            }
            int len;
            if (rc.Bit(ref _isRep[_state]) != 0)
            {
                if (Total == 0) throw new InvalidDataException("LZMA: repeat match before any data");
                if (rc.Bit(ref _isRepG0[_state]) == 0)
                {
                    if (rc.Bit(ref _isRep0Long[(_state << 4) + posState]) == 0)
                    {
                        _state = _state < 7 ? 9 : 11;
                        Put(WindowAt(_rep0), output, ref o);
                        continue;
                    }
                }
                else
                {
                    uint dist;
                    if (rc.Bit(ref _isRepG1[_state]) == 0) dist = _rep1;
                    else
                    {
                        if (rc.Bit(ref _isRepG2[_state]) == 0) dist = _rep2;
                        else { dist = _rep3; _rep3 = _rep2; }
                        _rep2 = _rep1;
                    }
                    _rep1 = _rep0;
                    _rep0 = dist;
                }
                len = _repLen.Decode(Rc, posState);
                _state = _state < 7 ? 8 : 11;
            }
            else
            {
                _rep3 = _rep2; _rep2 = _rep1; _rep1 = _rep0;
                len = _len.Decode(Rc, posState);
                _state = _state < 7 ? 7 : 10;
                int slot = rc.Tree(_posSlot, Math.Min(len, 3) << 6, 6);
                uint dist;
                if (slot < 4) dist = (uint)slot;
                else
                {
                    int direct = (slot >> 1) - 1;
                    dist = (uint)(2 | (slot & 1)) << direct;
                    if (slot < EndPosModel) dist += (uint)rc.ReverseTree(_posSpecial, (int)dist - slot, direct);
                    else
                    {
                        dist += rc.Direct(direct - AlignBits) << AlignBits;
                        dist += (uint)rc.ReverseTree(_align, 0, AlignBits);
                        if (dist == 0xFFFFFFFF) { EndMarker = true; break; }
                    }
                }
                _rep0 = dist;
            }
            if (_rep0 >= Total || _rep0 >= _window.Length) throw new InvalidDataException("LZMA: match distance beyond the data");
            _pendingLen = len + 2;
        }
        return o;
    }

    private sealed class LenDecoder
    {
        private ushort _choice, _choice2;
        private readonly ushort[] _low = new ushort[PosStatesMax << 3], _mid = new ushort[PosStatesMax << 3], _high = new ushort[256];

        public void Reset()
        {
            _choice = _choice2 = 1024;
            Array.Fill(_low, (ushort)1024); Array.Fill(_mid, (ushort)1024); Array.Fill(_high, (ushort)1024);
        }

        public int Decode(RangeDecoder rc, int posState)
        {
            if (rc.Bit(ref _choice) == 0) return rc.Tree(_low, posState << 3, 3);
            if (rc.Bit(ref _choice2) == 0) return 8 + rc.Tree(_mid, posState << 3, 3);
            return 16 + rc.Tree(_high, 0, 8);
        }
    }
}

/// <summary>
/// A raw LZMA1 stream (no .lzma header and no size): <paramref name="props"/> and <paramref name="dictSize"/> given,
/// or read as Inno Setup stores them (one properties byte, then the dictionary size, 5 bytes) when null.
/// Ends at the end marker, else wherever the reader stops.
/// </summary>
public sealed class LzmaStream : ReadOnlyStream
{
    private readonly LzmaInput _in;
    private readonly LzmaCore _core;

    /// <param name="maxOutput">The most this stream will be asked for, if known: caps the window.</param>
    public LzmaStream(Stream input, long maxOutput = long.MaxValue, byte? props = null, uint? dictSize = null)
    {
        if (props is null)
        {
            Span<byte> h = stackalloc byte[5];
            input.ReadExactly(h);
            props = h[0];
            dictSize = BitConverter.ToUInt32(h[1..]);
        }
        var (lc, lp, pb) = LzmaCore.SplitProps(props.Value);
        _in = new LzmaInput(input);
        _core = new LzmaCore((int)Math.Min(Math.Min(dictSize ?? (1u << 24), int.MaxValue - 64), Math.Max(maxOutput, 1)));
        _core.SetProps(lc, lp, pb);
        _core.ResetState();
        _core.ResetDictionary();
        _core.Rc.Init(_in);
    }

    public override int Read(Span<byte> buffer) => _core.Decode(buffer);
}

/// <summary>A raw LZMA2 stream (no .xz container): the dictionary-size byte first, as Inno Setup writes it, unless given.</summary>
public sealed class Lzma2Stream : ReadOnlyStream
{
    private readonly Stream _src;
    private readonly LzmaInput _in = new();
    private readonly LzmaCore _core;
    private readonly byte[] _packed = new byte[1 << 16];
    private int _left;           // bytes left in the current chunk
    private bool _stored, _end, _needProps = true, _needDictReset = true;

    public Lzma2Stream(Stream input, long maxOutput = long.MaxValue, byte? dictProp = null)
    {
        _src = input;
        int p = dictProp ?? input.ReadByte();
        if (p < 0 || p > 40) throw new InvalidDataException($"LZMA2: bad dictionary size byte {p}");
        long dict = p == 40 ? 0xFFFFFFFFL : (2L | (uint)(p & 1)) << (p / 2 + 11);
        _core = new LzmaCore((int)Math.Min(Math.Min(dict, int.MaxValue - 64), Math.Max(maxOutput, 1)));
    }

    private int ReadByteOrThrow() => _src.ReadByte() is var b and >= 0 ? b : throw new InvalidDataException("LZMA2: truncated stream");

    private void NextChunk()
    {
        int c = ReadByteOrThrow();
        if (c == 0) { _end = true; return; }
        if (c is 1 or 2)
        {
            if (c == 1) { _core.ResetDictionary(); _needDictReset = false; }
            else if (_needDictReset) throw new InvalidDataException("LZMA2: missing dictionary reset");
            _left = ((ReadByteOrThrow() << 8) | ReadByteOrThrow()) + 1;
            _stored = true;
            return;
        }
        if (c < 0x80) throw new InvalidDataException($"LZMA2: bad control byte 0x{c:x2}");
        int unpacked = ((c & 0x1F) << 16) + (ReadByteOrThrow() << 8) + ReadByteOrThrow() + 1;
        int packed = (ReadByteOrThrow() << 8) + ReadByteOrThrow() + 1;
        int mode = (c >> 5) & 3;
        if (mode == 3) { _core.ResetDictionary(); _needDictReset = false; }
        else if (_needDictReset) throw new InvalidDataException("LZMA2: missing dictionary reset");
        if (mode >= 2)
        {
            int props = ReadByteOrThrow();
            var (lc, lp, pb) = LzmaCore.SplitProps((byte)props);
            if (lc + lp > 4) throw new InvalidDataException("LZMA2: lc + lp > 4");
            _core.SetProps(lc, lp, pb);
            _needProps = false;
        }
        else if (_needProps) throw new InvalidDataException("LZMA2: missing properties");
        if (mode >= 1) _core.ResetState();
        _src.ReadExactly(_packed, 0, packed);
        _in.SetChunk(_packed, packed);
        _core.Rc.Init(_in);
        _left = unpacked;
        _stored = false;
    }

    public override int Read(Span<byte> buffer)
    {
        int total = 0;
        while (total < buffer.Length && !_end)
        {
            if (_left == 0) { NextChunk(); continue; }
            var dst = buffer.Slice(total, Math.Min(buffer.Length - total, _left));
            int n;
            if (_stored)
            {
                n = _src.Read(dst);
                if (n <= 0) throw new InvalidDataException("LZMA2: truncated stored chunk");
                _core.PutRaw(dst[..n]);
            }
            else
            {
                n = _core.Decode(dst);
                if (n == 0) throw new InvalidDataException("LZMA2: chunk ends early");
            }
            _left -= n;
            total += n;
            if (_left == 0 && !_stored && (_core.HasPending || _in.Overrun > 0))
                throw new InvalidDataException("LZMA2: chunk sizes do not match its data");
        }
        return total;
    }
}

/// <summary>Base for the forward-only decoding streams.</summary>
public abstract class ReadOnlyStream : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public abstract override int Read(Span<byte> buffer);
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
