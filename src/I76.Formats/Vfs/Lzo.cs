namespace I76.Formats.Vfs;

/// <summary>
/// LZO1X and LZO1Y decompression, a port of the reference reader, which
/// was written from the published bitstream layout. The two variants differ
/// only in the M2 match encoding and its maximum offset.
/// </summary>
public static class Lzo
{
    public sealed record Variant(int M2MaxOffset, int M2Mask, int M2Shift, int M2LenShift, int M2LenSub);

    public static readonly Variant Lzo1X = new(0x0800, 7, 3, 5, 1);
    public static readonly Variant Lzo1Y = new(0x0400, 3, 2, 4, 3);

    /// <summary>Dispatch on the ZFS record flags, as DecompressZfsRecord does.</summary>
    public static byte[] DecompressZfsRecord(ReadOnlySpan<byte> src, uint flags, int sizeHint = 0)
    {
        return (flags & 6) switch
        {
            0 => src.ToArray(),
            2 => Decompress(src, Lzo1X, sizeHint),
            4 => Decompress(src, Lzo1Y, sizeHint),
            var m => throw new InvalidDataException($"unknown method bits {m}"),
        };
    }

    private enum State { Top, FirstLiteralRun, Match, MatchDone, MatchNext }

    public static byte[] Decompress(ReadOnlySpan<byte> src, Variant v, int sizeHint = 0)
    {
        var o = new Output(sizeHint > 0 ? sizeHint : src.Length * 4);
        int ip = 0;
        int t = 0;
        var state = State.Top;

        int ReadExt(ReadOnlySpan<byte> s, ref int p, int b)
        {
            int x = 0;
            while (s[p] == 0) { x += 255; p++; }
            x += b + s[p];
            p++;
            return x;
        }

        if (src[ip] > 17)
        {
            t = src[ip] - 17;
            ip++;
            if (t < 4)
                state = State.MatchNext;
            else
            {
                o.Append(src.Slice(ip, t));
                ip += t;
                state = State.FirstLiteralRun;
            }
        }

        while (true)
        {
            switch (state)
            {
                case State.Top:
                    t = src[ip++];
                    if (t >= 16) { state = State.Match; continue; }
                    if (t == 0) t = ReadExt(src, ref ip, 15);
                    o.Append(src.Slice(ip, t + 3));
                    ip += t + 3;
                    state = State.FirstLiteralRun;
                    continue;

                case State.FirstLiteralRun:
                {
                    t = src[ip++];
                    if (t >= 16) { state = State.Match; continue; }
                    int mPos = o.Length - (1 + v.M2MaxOffset) - (t >> 2) - (src[ip] << 2);
                    ip++;
                    o.CopyMatch(mPos, 3);
                    state = State.MatchDone;
                    continue;
                }

                case State.Match:
                {
                    int mPos, count;
                    if (t >= 64)
                    {
                        mPos = o.Length - 1 - ((t >> 2) & v.M2Mask) - (src[ip] << v.M2Shift);
                        ip++;
                        count = (t >> v.M2LenShift) - v.M2LenSub + 2;
                    }
                    else if (t >= 32)
                    {
                        t &= 31;
                        if (t == 0) t = ReadExt(src, ref ip, 31);
                        mPos = o.Length - 1 - ((src[ip] >> 2) + (src[ip + 1] << 6));
                        ip += 2;
                        count = t + 2;
                    }
                    else if (t >= 16)
                    {
                        mPos = o.Length - ((t & 8) << 11);
                        t &= 7;
                        if (t == 0) t = ReadExt(src, ref ip, 7);
                        mPos -= (src[ip] >> 2) + (src[ip + 1] << 6);
                        ip += 2;
                        if (mPos == o.Length)
                            return o.ToArray(); // end of stream
                        mPos -= 0x4000;
                        count = t + 2;
                    }
                    else
                    {
                        mPos = o.Length - 1 - (t >> 2) - (src[ip] << 2);
                        ip++;
                        o.CopyMatch(mPos, 2);
                        state = State.MatchDone;
                        continue;
                    }
                    o.CopyMatch(mPos, count);
                    state = State.MatchDone;
                    continue;
                }

                case State.MatchDone:
                    t = src[ip - 2] & 3;
                    state = t == 0 ? State.Top : State.MatchNext;
                    continue;

                case State.MatchNext:
                    o.Append(src.Slice(ip, t));
                    ip += t;
                    t = src[ip++];
                    state = State.Match;
                    continue;
            }
        }
    }

    private sealed class Output(int capacity)
    {
        private byte[] _buf = new byte[Math.Max(capacity, 16)];
        public int Length { get; private set; }

        private void Ensure(int extra)
        {
            if (Length + extra <= _buf.Length) return;
            Array.Resize(ref _buf, Math.Max(_buf.Length * 2, Length + extra));
        }

        public void Append(ReadOnlySpan<byte> s)
        {
            Ensure(s.Length);
            s.CopyTo(_buf.AsSpan(Length));
            Length += s.Length;
        }

        /// <summary>Byte-at-a-time: a match may overlap the bytes it is producing.</summary>
        public void CopyMatch(int mPos, int count)
        {
            if (mPos < 0) throw new InvalidDataException("match offset before start of output");
            Ensure(count);
            for (int i = 0; i < count; i++)
                _buf[Length++] = _buf[mPos++];
        }

        public byte[] ToArray() => _buf.AsSpan(0, Length).ToArray();
    }
}
