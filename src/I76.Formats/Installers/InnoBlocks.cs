using System.IO.Compression;

namespace I76.Formats.Installers;

public enum InnoCompression { Stored, Zlib, BZip2, Lzma1, Lzma2, Unknown }

/// <summary>
/// Inno Setup's header blocks: a CRC'd block header (CRC32, stored size, compressed flag), then the stored bytes as 4 KiB
/// pieces each led by its CRC32, holding a zlib (before 4.1.6) or LZMA1 stream (or the bytes as they are).
/// </summary>
public static class InnoBlock
{
    /// <summary>Opens the block at the stream's position; <paramref name="end"/> is where the next one starts.</summary>
    public static Stream Open(Stream s, InnoVersion v, out long end)
    {
        var r = new BinaryReader(s, System.Text.Encoding.ASCII, leaveOpen: true);
        uint expected = r.ReadUInt32();
        var crc = new Crc32();
        uint stored;
        InnoCompression comp;
        if (v >= InnoVersion.V(4, 0, 9))
        {
            Span<byte> h = stackalloc byte[5];
            s.ReadExactly(h);
            crc.Update(h);
            stored = BitConverter.ToUInt32(h);
            comp = h[4] != 0 ? (v >= InnoVersion.V(4, 1, 6) ? InnoCompression.Lzma1 : InnoCompression.Zlib) : InnoCompression.Stored;
        }
        else
        {
            Span<byte> h = stackalloc byte[8];
            s.ReadExactly(h);
            crc.Update(h);
            uint compressed = BitConverter.ToUInt32(h), uncompressed = BitConverter.ToUInt32(h[4..]);
            (stored, comp) = compressed == uint.MaxValue ? (uncompressed, InnoCompression.Stored) : (compressed, InnoCompression.Zlib);
            stored += (stored + 4095) / 4096 * 4;
        }
        if (crc.Value != expected) throw new InstallerException(InstallerError.Corrupt, "the installer's setup headers are damaged (block header CRC mismatch)");
        end = s.Position + stored;
        var chunks = new CrcChunkStream(s, stored);
        return comp switch
        {
            InnoCompression.Stored => chunks,
            InnoCompression.Zlib => new ZLibStream(chunks, CompressionMode.Decompress),
            _ => new LzmaStream(chunks),
        };
    }
}

/// <summary>The stored bytes of a header block: 4 KiB pieces each preceded by its CRC32.</summary>
internal sealed class CrcChunkStream(Stream src, long stored) : ReadOnlyStream
{
    private readonly byte[] _buf = new byte[4096];
    private int _pos, _len;
    private long _left = stored;

    private bool NextPiece()
    {
        if (_left == 0) return false;
        if (_left <= 4) throw new InstallerException(InstallerError.Corrupt, "the installer's setup headers are damaged (block ends early)");
        Span<byte> c = stackalloc byte[4];
        src.ReadExactly(c);
        _left -= 4;
        _len = (int)Math.Min(4096, _left);
        src.ReadExactly(_buf, 0, _len);
        _left -= _len;
        _pos = 0;
        if (Crc32.Compute(_buf.AsSpan(0, _len)) != BitConverter.ToUInt32(c))
            throw new InstallerException(InstallerError.Corrupt, "the installer's setup headers are damaged (block CRC mismatch)");
        return true;
    }

    public override int Read(Span<byte> buffer)
    {
        int n = 0;
        while (n < buffer.Length)
        {
            if (_pos == _len && !NextPiece()) break;
            int k = Math.Min(buffer.Length - n, _len - _pos);
            _buf.AsSpan(_pos, k).CopyTo(buffer[n..]);
            _pos += k; n += k;
        }
        return n;
    }
}

/// <summary>A window of at most <paramref name="length"/> bytes of a stream, read forward.</summary>
internal sealed class LimitedStream(Stream src, long length) : ReadOnlyStream
{
    private long _left = length;

    public override int Read(Span<byte> buffer)
    {
        if (_left <= 0) return 0;
        int n = src.Read(buffer[..(int)Math.Min(buffer.Length, _left)]);
        _left -= n;
        return n;
    }
}
