using System.IO.Compression;

namespace I76.Formats.Installers;

/// <summary>How far an extraction has got: files finished, and bytes decoded out of those it has to decode.</summary>
public readonly record struct InnoProgress(int Files, int FilesTotal, long Bytes, long BytesTotal, string? Current);

/// <summary>
/// Reads data entries out of an installer: each chunk ("zlb\x1a", then the compressed bytes) is decoded once, front to
/// back, and the wanted files are cut out of it in order (solid chunks hold many files), through the call filter, with
/// their checksums verified. Memory stays bounded by the LZMA window and a few 64 KiB buffers.
/// </summary>
public static class InnoExtractor
{
    private static readonly byte[] ChunkMagic = "zlb\x1a"u8.ToArray();

    /// <summary>A chunk's identity: the slice and offset it starts at.</summary>
    private static (uint, uint) Key(InnoData d) => (d.FirstSlice, d.ChunkOffset);

    /// <summary>The bytes decoding these entries takes: per chunk, up to the end of the last wanted file.</summary>
    public static long WorkBytes(IEnumerable<InnoData> wanted) =>
        wanted.GroupBy(Key).Sum(g => g.Max(d => (long)(d.FileOffset + d.FileSize)));

    /// <param name="consume">Called once per entry with a stream of exactly its bytes (reading all of them is optional).</param>
    /// <param name="verified">Called after an entry's checksum matched.</param>
    public static void Run(InnoSetup setup, IEnumerable<InnoData> wanted, Action<InnoData, Stream> consume,
        Action<InnoData>? verified = null, Action<InnoProgress>? progress = null, CancellationToken cancel = default,
        Func<InnoData, string>? describe = null)
    {
        var list = wanted.DistinctBy(d => d.Index).OrderBy(d => d.FirstSlice).ThenBy(d => d.ChunkOffset).ThenBy(d => d.FileOffset).ToList();
        if (list.FirstOrDefault(d => d.Encrypted) is not null)
            throw new InstallerException(InstallerError.Encrypted, "this installer is password-protected (encrypted); I26 can't read it.");
        if (list.FirstOrDefault(d => d.Compression is InnoCompression.BZip2 or InnoCompression.Unknown) is { } bad)
            throw new InstallerException(InstallerError.UnsupportedCompression, $"this installer uses {bad.Compression} compression, which I26 doesn't decode.");
        using var slices = new InnoSlices(setup);
        var missing = slices.MissingSlices(list);
        if (missing.Count > 0)
            throw new InstallerException(InstallerError.MissingParts,
                $"the installer's data parts are missing: {string.Join(", ", missing)}. Put them next to {Path.GetFileName(setup.Path)}.");
        // the most any chunk decodes to, for its window: every entry in it, wanted or not
        var chunkEnd = setup.Data.GroupBy(Key).ToDictionary(g => g.Key, g => g.Max(d => (long)(d.FileOffset + d.FileSize)));
        long total = WorkBytes(list), done = 0;
        int files = 0;
        var skipBuf = new byte[1 << 16];
        foreach (var chunk in list.GroupBy(Key))
        {
            Stream? stream = null;
            long pos = 0;
            try
            {
                foreach (var d in chunk)
                {
                    cancel.ThrowIfCancellationRequested();
                    if (stream is null || (long)d.FileOffset < pos)
                    {
                        stream?.Dispose();
                        stream = OpenChunk(slices, d, chunkEnd[Key(d)]);
                        pos = 0;
                    }
                    while (pos < (long)d.FileOffset)
                    {
                        cancel.ThrowIfCancellationRequested();
                        int k = stream.Read(skipBuf, 0, (int)Math.Min(skipBuf.Length, (long)d.FileOffset - pos));
                        if (k <= 0) throw new InstallerException(InstallerError.Corrupt, "a data chunk ends before the files it should hold");
                        pos += k;
                        done += k;
                        progress?.Invoke(new(files, list.Count, done, total, describe?.Invoke(d)));
                    }
                    var hasher = new FileHasher(d.Checksum.Kind);
                    var file = new FileReader(stream, (long)d.FileSize, d.Filter, hasher, n =>
                    {
                        cancel.ThrowIfCancellationRequested();
                        done += n;
                        progress?.Invoke(new(files, list.Count, done, total, describe?.Invoke(d)));
                    });
                    consume(d, file);
                    file.Drain();
                    pos += (long)d.FileSize;
                    var got = hasher.Finish();
                    if (d.Checksum.Kind != ChecksumKind.None && !got.Digest.AsSpan().SequenceEqual(d.Checksum.Digest))
                        throw new InstallerException(InstallerError.ChecksumMismatch,
                            $"{describe?.Invoke(d) ?? $"file #{d.Index}"} doesn't match its checksum: the installer is damaged (download it again).");
                    files++;
                    verified?.Invoke(d);
                    progress?.Invoke(new(files, list.Count, done, total, describe?.Invoke(d)));
                }
            }
            catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException)
            {
                throw new InstallerException(InstallerError.Corrupt, $"the installer's data is damaged ({ex.Message}).", ex);
            }
            finally { stream?.Dispose(); }
        }
    }

    private static Stream OpenChunk(InnoSlices slices, InnoData d, long maxOut)
    {
        var raw = slices.Open(d.FirstSlice, d.ChunkOffset);
        Span<byte> magic = stackalloc byte[4];
        if (raw.ReadAtLeast(magic, 4, throwOnEndOfStream: false) != 4 || !magic.SequenceEqual(ChunkMagic))
            throw new InstallerException(InstallerError.Corrupt, "a data chunk has no chunk marker; the installer is damaged");
        var limited = new LimitedStream(raw, (long)d.ChunkSize);
        return d.Compression switch
        {
            InnoCompression.Stored => limited,
            InnoCompression.Zlib => new ZLibStream(limited, CompressionMode.Decompress),
            InnoCompression.Lzma1 => new LzmaStream(limited, maxOut),
            InnoCompression.Lzma2 => new Lzma2Stream(limited, maxOut),
            _ => throw new InstallerException(InstallerError.UnsupportedCompression, $"{d.Compression} compression is not supported"),
        };
    }

    /// <summary>One file's bytes out of its chunk: exactly its size, through the call filter, hashed on the way.</summary>
    private sealed class FileReader : ReadOnlyStream
    {
        private readonly Stream _src;
        private readonly FileHasher _hash;
        private readonly Action<int> _advance;
        private long _left;

        public FileReader(Stream chunk, long size, InnoFilter filter, FileHasher hash, Action<int> advance)
        {
            _left = size;
            var bounded = new LimitedStream(chunk, size);
            _src = filter == InnoFilter.None ? bounded : new InnoExeFilter(bounded, filter);
            _hash = hash;
            _advance = advance;
        }

        public override int Read(Span<byte> buffer)
        {
            if (_left == 0) return 0;
            int n = _src.Read(buffer[..(int)Math.Min(buffer.Length, _left)]);
            if (n <= 0) throw new InstallerException(InstallerError.Corrupt, "a file's data ends early; the installer is damaged");
            _left -= n;
            _hash.Update(buffer[..n]);
            _advance(n);
            return n;
        }

        public void Drain()
        {
            var b = new byte[1 << 16];
            while (_left > 0) Read(b);
        }
    }
}
