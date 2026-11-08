namespace I76.Formats.Video;

/// <summary>
/// A Smacker movie (RAD Game Tools, "SMK2"): the briefings, debriefings,
/// intro and credits in the install's smk/ directory (docs/formats/smacker.md
/// in the reference reader). 8-bit paletted video in 4×4 blocks coded with four
/// Huffman "big trees", and DPCM audio, also Huffman coded. Frames depend on
/// the previous one (skip blocks, palette deltas), so they are decoded in
/// order.
/// </summary>
public sealed class Smacker : IDisposable
{
    public int Width { get; }
    public int Height { get; }
    public int FrameCount { get; }
    /// <summary>Seconds per frame.</summary>
    public double FrameDuration { get; }
    /// <summary>Header flags: 1 ring frame, 2 Y interlaced, 4 Y doubled.</summary>
    public uint Flags { get; }
    /// <summary>Per audio track (7): the raw rate word — bit 31 compressed, 30 present, 29 16-bit, 28 stereo, low 24 bits the rate.</summary>
    public uint[] AudioRate { get; } = new uint[7];

    private readonly Stream _s;
    private readonly long[] _offset;
    private readonly int[] _size;
    private readonly byte[] _type;
    private readonly BigTree _mmap, _mclr, _full, _btype;
    private int _next;

    /// <summary>The frame's colour indices, carried from frame to frame (skip blocks keep them).</summary>
    public byte[] Pixels { get; }
    /// <summary>The palette as RGB triples, carried from frame to frame.</summary>
    public byte[] Palette { get; } = new byte[768];
    /// <summary>How the last decoded frame coded each 4x4 block (row-major, Width/4 x Height/4): 0 skipped (the previous frame's pixels), 1 full, 2 two colours, 3 one colour.</summary>
    public byte[] BlockTypes { get; }

    public Smacker(Stream s)
    {
        _s = s;
        var r = new BinaryReader(s);
        uint sig = r.ReadUInt32();
        if (sig != 0x324b4d53 && sig != 0x344b4d53) throw new InvalidDataException("not a Smacker file");
        if (sig == 0x344b4d53) throw new NotSupportedException("SMK4 is not used by Interstate '76");
        Width = (int)r.ReadUInt32();
        Height = (int)r.ReadUInt32();
        FrameCount = (int)r.ReadUInt32();
        int rate = r.ReadInt32();
        // positive: milliseconds a frame; negative: hundredths of a millisecond; zero: 10 a second
        FrameDuration = rate > 0 ? rate / 1000.0 : rate < 0 ? -rate / 100000.0 : 0.1;
        Flags = r.ReadUInt32();
        for (int i = 0; i < 7; i++) r.ReadUInt32();          // the largest unpacked audio chunk per track
        int treesSize = (int)r.ReadUInt32();
        for (int i = 0; i < 4; i++) r.ReadUInt32();          // the unpacked tree sizes (allocation hints)
        for (int i = 0; i < 7; i++) AudioRate[i] = r.ReadUInt32();
        r.ReadUInt32();
        int frames = FrameCount + ((Flags & 1) != 0 ? 1 : 0);
        _size = new int[frames];
        _offset = new long[frames];
        for (int i = 0; i < frames; i++) _size[i] = (int)(r.ReadUInt32() & ~3u);
        _type = r.ReadBytes(frames);
        var trees = new BitReader(r.ReadBytes(treesSize));
        _mmap = BigTree.Read(trees);
        _mclr = BigTree.Read(trees);
        _full = BigTree.Read(trees);
        _btype = BigTree.Read(trees);
        long at = s.Position;
        for (int i = 0; i < frames; i++) { _offset[i] = at; at += _size[i]; }
        Pixels = new byte[Width * Height];
        BlockTypes = new byte[Width / 4 * (Height / 4)];
    }

    public static Smacker Open(string path) => new(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16));

    public bool HasAudio(int track) => (AudioRate[track] & 0x40000000) != 0;
    public int SampleRate(int track) => (int)(AudioRate[track] & 0xffffff);
    public bool Stereo(int track) => (AudioRate[track] & 0x10000000) != 0;
    public bool Is16Bit(int track) => (AudioRate[track] & 0x20000000) != 0;

    /// <summary>
    /// The audio track's whole length in sample frames (a stereo pair is one), from each frame's chunk header without decoding
    /// it; 0 for a track the movie lacks. It moves the file's position, so not while another thread decodes.
    /// </summary>
    public long AudioSamples(int track)
    {
        if (!HasAudio(track)) return 0;
        long bytes = 0;
        var head = new byte[8];
        for (int f = 0; f < FrameCount; f++)
        {
            if ((_type[f] & (2 << track)) == 0) continue;
            long p = _offset[f], end = _offset[f] + _size[f];
            if ((_type[f] & 1) != 0) { _s.Position = p; p += _s.ReadByte() * 4; }
            for (int t = 0; t <= track && p + 8 <= end; t++)
            {
                if ((_type[f] & (2 << t)) == 0) continue;
                _s.Position = p;
                _s.ReadExactly(head);
                uint len = BitConverter.ToUInt32(head, 0);
                // a packed chunk starts with its unpacked size; a raw one is its own
                if (t == track) bytes += (AudioRate[t] & 0x80000000) != 0 ? BitConverter.ToUInt32(head, 4) : len - 4;
                p += len;
            }
        }
        return bytes / ((Is16Bit(track) ? 2 : 1) * (Stereo(track) ? 2 : 1));
    }

    /// <summary>The index of the frame the next <see cref="NextFrame"/> decodes.</summary>
    public int Position => _next;

    /// <summary>
    /// Decodes the next frame into <see cref="Pixels"/> and <see cref="Palette"/>;
    /// its audio goes to <paramref name="audio"/> (per track, unpacked PCM:
    /// unsigned 8-bit or signed 16-bit little-endian, interleaved if stereo),
    /// when given. False after the last frame.
    /// </summary>
    public bool NextFrame(Action<int, byte[]>? audio = null, bool video = true)
    {
        if (_next >= FrameCount) return false;
        int f = _next++;
        var buf = new byte[_size[f]];
        _s.Position = _offset[f];
        _s.ReadExactly(buf);
        int p = 0;
        if ((_type[f] & 1) != 0)
        {
            int len = buf[p] * 4;
            if (video) ReadPalette(buf.AsSpan(p + 1, len - 1));
            p += len;
        }
        for (int t = 0; t < 7; t++)
        {
            if ((_type[f] & (2 << t)) == 0) continue;
            int len = (int)BitConverter.ToUInt32(buf, p);
            if (audio is not null && HasAudio(t))
                audio(t, (AudioRate[t] & 0x80000000) != 0 ? UnpackAudio(buf.AsSpan(p + 4, len - 4)) : buf.AsSpan(p + 4, len - 4).ToArray());
            p += len;
        }
        if (video) DecodeVideo(new BitReader(buf, p));
        return true;
    }

    // ---- palette ----

    // 6-bit to 8-bit: round(i · 255 / 63)
    private static readonly byte[] Pal6 = Enumerable.Range(0, 64).Select(i => (byte)Math.Round(i * 255 / 63.0)).ToArray();

    private void ReadPalette(ReadOnlySpan<byte> d)
    {
        var old = (byte[])Palette.Clone();
        int i = 0, n = 0;
        while (n < 256 && i < d.Length)
        {
            int t = d[i++];
            if ((t & 0x80) != 0) n += (t & 0x7f) + 1;               // keep the next t+1 entries
            else if ((t & 0x40) != 0)
            {
                // copy t+1 entries from the old palette, from the given index
                int src = d[i++];
                for (int j = (t & 0x3f) + 1; j > 0 && n < 256; j--, n++, src++)
                {
                    Palette[n * 3] = old[src * 3]; Palette[n * 3 + 1] = old[src * 3 + 1]; Palette[n * 3 + 2] = old[src * 3 + 2];
                }
            }
            else
            {
                Palette[n * 3] = Pal6[t & 0x3f];
                Palette[n * 3 + 1] = Pal6[d[i++] & 0x3f];
                Palette[n * 3 + 2] = Pal6[d[i++] & 0x3f];
                n++;
            }
        }
    }

    // ---- video ----

    // the run of blocks a type code covers: (type >> 2) & 63 indexes this
    private static readonly int[] Runs = [.. Enumerable.Range(1, 59), 128, 256, 512, 1024, 2048];

    private void DecodeVideo(BitReader b)
    {
        _mmap.Reset(); _mclr.Reset(); _full.Reset(); _btype.Reset();
        int bw = Width / 4, blocks = bw * (Height / 4), blk = 0;
        var bt = BlockTypes;
        Array.Clear(bt);
        var px = Pixels;
        int stride = Width;
        while (blk < blocks)
        {
            int type = _btype.Decode(b);
            int run = Runs[(type >> 2) & 0x3f];
            switch (type & 3)
            {
                case 0:     // two colours and a 16-bit map
                    for (; run > 0 && blk < blocks; run--, blk++)
                    {
                        bt[blk] = 2;
                        int clr = _mclr.Decode(b), map = _mmap.Decode(b);
                        byte hi = (byte)(clr >> 8), lo = (byte)clr;
                        int o = (blk / bw) * 4 * stride + (blk % bw) * 4;
                        for (int y = 0; y < 4; y++, o += stride)
                            for (int x = 0; x < 4; x++, map >>= 1) px[o + x] = (map & 1) != 0 ? hi : lo;
                    }
                    break;
                case 1:     // full: two pixels a code, each row as (2, 3) then (0, 1)
                    for (; run > 0 && blk < blocks; run--, blk++)
                    {
                        bt[blk] = 1;
                        int o = (blk / bw) * 4 * stride + (blk % bw) * 4;
                        for (int y = 0; y < 4; y++, o += stride)
                        {
                            int c = _full.Decode(b);
                            px[o + 2] = (byte)c; px[o + 3] = (byte)(c >> 8);
                            c = _full.Decode(b);
                            px[o] = (byte)c; px[o + 1] = (byte)(c >> 8);
                        }
                    }
                    break;
                case 2:     // skip: the previous frame's pixels stay
                    blk += run;
                    break;
                case 3:     // fill with the colour in the type code's high byte
                    byte col = (byte)(type >> 8);
                    for (; run > 0 && blk < blocks; run--, blk++)
                    {
                        bt[blk] = 3;
                        int o = (blk / bw) * 4 * stride + (blk % bw) * 4;
                        for (int y = 0; y < 4; y++, o += stride)
                        {
                            px[o] = col; px[o + 1] = col; px[o + 2] = col; px[o + 3] = col;
                        }
                    }
                    break;
            }
        }
        BitsLeft = b.Remaining;
    }

    /// <summary>Bits left over in the last frame's video stream (a structural check: under 32, the padding to the frame's 4-byte size).</summary>
    public int BitsLeft { get; private set; }

    // ---- audio ----

    /// <summary>The DPCM audio chunk: unpacked size, then flags, Huffman trees, the first sample(s) and the deltas.</summary>
    private static byte[] UnpackAudio(ReadOnlySpan<byte> d)
    {
        int size = (int)BitConverter.ToUInt32(d);
        var b = new BitReader(d[4..].ToArray());
        var outp = new byte[size];
        if (b.Bit() == 0) return outp;                       // no data: silence
        bool stereo = b.Bit() != 0, wide = b.Bit() != 0;
        int n = 1 << ((stereo ? 1 : 0) + (wide ? 1 : 0));
        var trees = new SmallTree?[n];
        for (int i = 0; i < n; i++) trees[i] = SmallTree.ReadFlagged(b);
        int ch = stereo ? 2 : 1;
        int o = 0;
        if (wide)
        {
            var pred = new int[2];
            // the first samples, right before left, big-endian 16 bits each
            for (int c = ch - 1; c >= 0; c--) { int v = b.Bits(16); pred[c] = (short)((v >> 8) | ((v & 0xff) << 8)); }
            for (int c = 0; c < ch && o + 1 < size; c++) { outp[o++] = (byte)pred[c]; outp[o++] = (byte)(pred[c] >> 8); }
            for (int c = 0; o + 1 < size; c = (c + 1) % ch)
            {
                int lo = Small(trees[c * 2], b), hi = Small(trees[c * 2 + 1], b);
                pred[c] = (short)(pred[c] + (short)(lo | (hi << 8)));
                outp[o++] = (byte)pred[c]; outp[o++] = (byte)(pred[c] >> 8);
            }
        }
        else
        {
            var pred = new int[2];
            for (int c = ch - 1; c >= 0; c--) pred[c] = b.Bits(8);
            for (int c = 0; c < ch && o < size; c++) outp[o++] = (byte)pred[c];
            for (int c = 0; o < size; c = (c + 1) % ch)
            {
                pred[c] = (pred[c] + (sbyte)Small(trees[c], b)) & 0xff;
                outp[o++] = (byte)pred[c];
            }
        }
        return outp;
    }

    private static int Small(SmallTree? t, BitReader b) => t?.Decode(b) ?? 0;

    public void Dispose() => _s.Dispose();

    // ---- bits and trees ----

    /// <summary>Bits taken least significant first, from little-endian bytes.</summary>
    private sealed class BitReader(byte[] data, int start = 0)
    {
        private long _bit = start * 8L;
        private readonly long _end = data.Length * 8L;
        public int Remaining => (int)Math.Max(0, _end - _bit);
        public int Bit()
        {
            if (_bit >= _end) throw new InvalidDataException("Smacker bitstream overrun");
            int v = (data[_bit >> 3] >> (int)(_bit & 7)) & 1;
            _bit++;
            return v;
        }
        public int Bits(int n)
        {
            int v = 0;
            for (int i = 0; i < n; i++) v |= Bit() << i;
            return v;
        }
    }

    /// <summary>A Huffman tree of bytes: a 1 bit opens a node (its 0 branch first, then its 1 branch), a 0 bit is a leaf followed by its 8-bit value.</summary>
    private sealed class SmallTree
    {
        private readonly List<int> _t = [];     // ≥ 0: a node's 1-branch index; leaves are ~value
        public static SmallTree? ReadFlagged(BitReader b)
        {
            SmallTree? t = null;
            if (b.Bit() != 0) { t = new SmallTree(); t.Build(b); }
            b.Bit();        // a closing 0
            return t;
        }
        private void Build(BitReader b)
        {
            if (b.Bit() == 0) { _t.Add(~b.Bits(8)); return; }
            int node = _t.Count;
            _t.Add(0);
            Build(b);
            _t[node] = _t.Count;
            Build(b);
        }
        public int Decode(BitReader b)
        {
            int i = 0;
            while (_t[i] >= 0) i = b.Bit() != 0 ? _t[i] : i + 1;
            return ~_t[i];
        }
    }

    /// <summary>
    /// A Huffman tree of 16-bit values whose leaves are coded with two
    /// byte trees (low, high). Three escape values mark leaves that hold a
    /// recently used value instead (a three-deep move-to-front cache,
    /// cleared every frame).
    /// </summary>
    private sealed class BigTree
    {
        private readonly List<int> _t = [];      // node: (1-branch index) | Node; leaf: the value
        private const int Node = 1 << 30;
        private readonly int[] _last = [-1, -1, -1];
        private SmallTree? _lo, _hi;
        private readonly int[] _esc = new int[3];

        public static BigTree Read(BitReader b)
        {
            var t = new BigTree();
            if (b.Bit() == 0) { t._t.Add(0); t._last[0] = t._last[1] = t._last[2] = t.Dummy(); return t; }
            t._lo = SmallTree.ReadFlagged(b);
            t._hi = SmallTree.ReadFlagged(b);
            for (int i = 0; i < 3; i++) t._esc[i] = b.Bits(16);
            t.Build(b);
            b.Bit();        // a closing 0
            for (int i = 0; i < 3; i++) if (t._last[i] < 0) t._last[i] = t.Dummy();
            return t;
        }

        private int Dummy() { _t.Add(0); return _t.Count - 1; }

        private void Build(BitReader b)
        {
            if (b.Bit() == 0)
            {
                int v = (_lo?.Decode(b) ?? 0) | ((_hi?.Decode(b) ?? 0) << 8);
                if (v == _esc[0]) { _last[0] = _t.Count; v = 0; }
                else if (v == _esc[1]) { _last[1] = _t.Count; v = 0; }
                else if (v == _esc[2]) { _last[2] = _t.Count; v = 0; }
                _t.Add(v);
                return;
            }
            int node = _t.Count;
            _t.Add(Node);
            Build(b);
            _t[node] = Node | _t.Count;
            Build(b);
        }

        public void Reset() { _t[_last[0]] = 0; _t[_last[1]] = 0; _t[_last[2]] = 0; }

        public int Decode(BitReader b)
        {
            int i = 0;
            while ((_t[i] & Node) != 0) i = b.Bit() != 0 ? _t[i] & ~Node : i + 1;
            return Recent(_t[i]);
        }

        private int Recent(int v)
        {
            if (v != _t[_last[0]])
            {
                _t[_last[2]] = _t[_last[1]];
                _t[_last[1]] = _t[_last[0]];
                _t[_last[0]] = v;
            }
            return v;
        }
    }
}
