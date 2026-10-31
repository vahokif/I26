using System.Security.Cryptography;

namespace I76.Formats.Installers;

/// <summary>CRC-32 (the zlib/PKZIP polynomial 0xEDB88320), as Inno Setup's headers, blocks and old file entries use it.</summary>
public sealed class Crc32
{
    private static readonly uint[] Table = MakeTable();
    private uint _crc = 0xFFFFFFFF;

    private static uint[] MakeTable()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            t[i] = c;
        }
        return t;
    }

    public void Update(ReadOnlySpan<byte> data)
    {
        uint c = _crc;
        foreach (var b in data) c = Table[(c ^ b) & 0xFF] ^ (c >> 8);
        _crc = c;
    }

    public uint Value => ~_crc;

    public static uint Compute(ReadOnlySpan<byte> data)
    {
        var c = new Crc32();
        c.Update(data);
        return c.Value;
    }
}

/// <summary>Adler-32, the checksum of Inno Setup before 4.0.1 (kept for completeness of <see cref="FileHasher"/>).</summary>
public sealed class Adler32
{
    private uint _a = 1, _b;

    public void Update(ReadOnlySpan<byte> data)
    {
        foreach (var x in data)
        {
            _a = (_a + x) % 65521;
            _b = (_b + _a) % 65521;
        }
    }

    public uint Value => (_b << 16) | _a;
}

public enum ChecksumKind { None, Adler32, Crc32, Md5, Sha1, Sha256 }

/// <summary>A file's stored checksum: the kind its Inno version uses and the digest bytes (CRC/Adler little-endian).</summary>
public readonly record struct FileChecksum(ChecksumKind Kind, byte[] Digest)
{
    public override string ToString() => Kind == ChecksumKind.None ? "none" : $"{Kind.ToString().ToLowerInvariant()}:{Convert.ToHexString(Digest).ToLowerInvariant()}";
}

/// <summary>An incremental hash of the kind a file entry stores.</summary>
public sealed class FileHasher
{
    private readonly ChecksumKind _kind;
    private readonly IncrementalHash? _hash;
    private readonly Crc32? _crc;
    private readonly Adler32? _adler;

    public FileHasher(ChecksumKind kind)
    {
        _kind = kind;
        switch (kind)
        {
            case ChecksumKind.Md5: _hash = IncrementalHash.CreateHash(HashAlgorithmName.MD5); break;
            case ChecksumKind.Sha1: _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1); break;
            case ChecksumKind.Sha256: _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); break;
            case ChecksumKind.Crc32: _crc = new Crc32(); break;
            case ChecksumKind.Adler32: _adler = new Adler32(); break;
        }
    }

    public void Update(ReadOnlySpan<byte> data)
    {
        if (_hash is not null) _hash.AppendData(data);
        else if (_crc is not null) _crc.Update(data);
        else _adler?.Update(data);
    }

    public FileChecksum Finish()
    {
        if (_hash is not null) return new(_kind, _hash.GetHashAndReset());
        if (_crc is not null) return new(_kind, BitConverter.GetBytes(_crc.Value));
        if (_adler is not null) return new(_kind, BitConverter.GetBytes(_adler.Value));
        return new(ChecksumKind.None, []);
    }
}
