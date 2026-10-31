namespace I76.Formats.Installers;

/// <summary>
/// The installer's data, as one forward stream from a (slice, offset): inside the .exe after the data offset, or across
/// the setup-N.bin files beside it (each led by "idska32\x1a" and its size; a chunk may run on into the next one).
/// </summary>
public sealed class InnoSlices : IDisposable
{
    private readonly InnoSetup _setup;
    private readonly string _dir;
    private FileStream? _file;
    private uint _slice = uint.MaxValue;
    private long _sliceSize;

    public InnoSlices(InnoSetup setup)
    {
        _setup = setup;
        _dir = Path.GetDirectoryName(Path.GetFullPath(setup.Path)) ?? ".";
    }

    public bool SingleFile => _setup.DataOffset != 0;

    /// <summary>The names slice <paramref name="slice"/> may have: after the .exe's name, and after the header's base name.</summary>
    public IEnumerable<string> SliceNames(uint slice)
    {
        uint perDisk = Math.Max(_setup.Header.SlicesPerDisk, 1);
        string suffix = perDisk == 1 ? $"-{slice + 1}.bin" : $"-{slice / perDisk + 1}{(char)('a' + slice % perDisk)}.bin";
        yield return Path.GetFileNameWithoutExtension(_setup.Path) + suffix;
        var b = _setup.Header.BaseFilename.Replace('/', '_').Replace('\\', '_');
        if (b.Length > 0) yield return b + suffix;
    }

    /// <summary>The slice's file beside the .exe (any letter case), or null.</summary>
    public string? FindSlice(uint slice)
    {
        foreach (var name in SliceNames(slice))
        {
            var p = Path.Combine(_dir, name);
            if (File.Exists(p)) return p;
            var hit = Directory.EnumerateFiles(_dir).FirstOrDefault(f => string.Equals(Path.GetFileName(f), name, StringComparison.OrdinalIgnoreCase));
            if (hit is not null) return hit;
        }
        return null;
    }

    /// <summary>The slices the given data entries need that are not beside the .exe (none for a single-file installer).</summary>
    public List<string> MissingSlices(IEnumerable<InnoData> data)
    {
        var missing = new List<string>();
        if (SingleFile) return missing;
        var need = new SortedSet<uint>();
        foreach (var d in data)
            for (uint s = d.FirstSlice; s <= d.LastSlice; s++) need.Add(s);
        foreach (var s in need)
            if (FindSlice(s) is null) missing.Add(SliceNames(s).First());
        return missing;
    }

    private void OpenSlice(uint slice)
    {
        if (slice == _slice && _file is not null) return;
        _file?.Dispose();
        _file = null;
        if (SingleFile)
        {
            if (slice != 0) throw new InstallerException(InstallerError.Corrupt, "a single-file installer refers to a second slice");
            _file = new FileStream(_setup.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            _sliceSize = Math.Min(_file.Length, int.MaxValue);
        }
        else
        {
            var p = FindSlice(slice) ?? throw new InstallerException(InstallerError.MissingParts,
                $"{SliceNames(slice).First()} is missing: put every setup-*.bin part next to {Path.GetFileName(_setup.Path)}.");
            _file = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            Span<byte> h = stackalloc byte[12];
            if (_file.Read(h) != 12 || !(h[..8].SequenceEqual("idska32\x1a"u8) || h[..8].SequenceEqual("idska16\x1a"u8)))
                throw new InstallerException(InstallerError.Corrupt, $"{Path.GetFileName(p)} is not an Inno Setup data slice");
            _sliceSize = BitConverter.ToUInt32(h[8..]);
            if (_sliceSize > _file.Length || _sliceSize < 12)
                throw new InstallerException(InstallerError.Corrupt, $"{Path.GetFileName(p)} is truncated (expected {_sliceSize} bytes)");
        }
        _slice = slice;
    }

    /// <summary>A forward stream from <paramref name="offset"/> in <paramref name="slice"/>, running on into the next slices.</summary>
    public Stream Open(uint slice, uint offset)
    {
        OpenSlice(slice);
        long pos = offset + (long)_setup.DataOffset;
        if (pos > _sliceSize) throw new InstallerException(InstallerError.Corrupt, "a data chunk lies beyond the end of its slice");
        _file!.Position = pos;
        return new Reader(this);
    }

    private sealed class Reader(InnoSlices owner) : ReadOnlyStream
    {
        public override int Read(Span<byte> buffer)
        {
            int n = 0;
            while (n < buffer.Length)
            {
                var f = owner._file!;
                long left = owner._sliceSize - f.Position;
                if (left <= 0)
                {
                    if (owner.SingleFile) break;
                    owner.OpenSlice(owner._slice + 1);   // leaves the position after the new slice's header
                    continue;
                }
                int k = f.Read(buffer.Slice(n, (int)Math.Min(buffer.Length - n, left)));
                if (k <= 0) break;
                n += k;
            }
            return n;
        }
    }

    public void Dispose() => _file?.Dispose();
}
