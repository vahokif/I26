using System.Text;

namespace I76.Formats.Installers;

// The Inno Setup format as innoextract (Daniel Scharrer, zlib licence, https://constexpr.org/innoextract/) documents it
// in its source: the loader's offset table, the versioned header and entry layouts, the block, chunk and slice streams.
// Ported for data versions 5.0.0 and later (non-ISX, 32-bit); older layouts are refused with a clear message.

/// <summary>A file the installer copies: where to (Inno constants such as {app}\...), and which data entry holds it.</summary>
public sealed record InnoFile(string Destination, int Location, string Components, string Tasks, string Languages, string Check, uint Attributes)
{
    /// <summary>No data in the installer: an external file the installer copies from the user's disk.</summary>
    public bool External => Location < 0;
}

[Flags]
public enum InnoFilter { None = 0, Call4108 = 1, Call5200 = 2, Call5309 = 3 }

/// <summary>A data entry: a file's bytes inside a compressed chunk, in the slices of the installer.</summary>
public sealed record InnoData(
    int Index, uint FirstSlice, uint LastSlice, uint ChunkOffset, ulong ChunkSize,
    ulong FileOffset, ulong FileSize, FileChecksum Checksum, long FileTime,
    InnoCompression Compression, bool Encrypted, InnoFilter Filter);

/// <summary>The parts of the setup header this reader keeps.</summary>
public sealed record InnoHeader(
    string AppName, string AppVersionedName, string AppId, string AppVersion, string AppPublisher,
    string DefaultDirName, string BaseFilename, uint SlicesPerDisk, InnoCompression Compression, bool Encrypted, bool Password);

/// <summary>
/// An Inno Setup installer, opened read-only: the setup loader's offset table in the .exe, the setup headers (the file
/// list and the data entries). The data itself is read by <see cref="InnoExtractor"/>. Reading is streaming: the
/// headers are parsed from their compressed blocks without holding the installer in memory.
/// </summary>
public sealed class InnoSetup
{
    public string Path { get; }
    public string IdString { get; private set; } = "";
    public InnoVersion Version { get; private set; }
    public InnoHeader Header { get; private set; } = null!;
    public IReadOnlyList<InnoFile> Files { get; private set; } = [];
    public IReadOnlyList<InnoData> Data { get; private set; } = [];
    public IReadOnlyList<string> Languages { get; private set; } = [];
    /// <summary>Where the data starts in the .exe; 0 when it's in setup-N.bin files beside it.</summary>
    public uint DataOffset { get; private set; }
    public long ExeLength { get; private set; }

    private InnoSetup(string path) => Path = path;

    /// <summary>An installer described directly (for tests of the data side: slices, chunks, checksums).</summary>
    internal static InnoSetup ForTests(string path, InnoHeader header, uint dataOffset, IReadOnlyList<InnoData> data, IReadOnlyList<InnoFile>? files = null) =>
        new(path) { Header = header, DataOffset = dataOffset, Data = data, Files = files ?? [], Version = InnoVersion.V(5, 5, 0) with { Unicode = true } };

    private static byte[] M(string head, params int[] tail) => [.. Encoding.ASCII.GetBytes(head), .. tail.Select(x => (byte)x)];

    private static readonly byte[][] LoaderMagics =
    [
        M("rDlPtS02", 0x87, 'e', 'V', 'x'), M("rDlPtS04", 0x87, 'e', 'V', 'x'), M("rDlPtS05", 0x87, 'e', 'V', 'x'),
        M("rDlPtS06", 0x87, 'e', 'V', 'x'), M("rDlPtS07", 0x87, 'e', 'V', 'x'),
        M("rDlPtS", 0xCD, 0xE6, 0xD7, '{', 0x0B, '*'), M("nS5W7dT", 0x83, 0xAA, 0x1B, 0x0F, 'j'),
    ];
    // the loader versions of the magics above
    private static readonly InnoVersion[] LoaderVersions =
        [InnoVersion.V(1, 2, 10), InnoVersion.V(4, 0, 0), InnoVersion.V(4, 0, 3), InnoVersion.V(4, 0, 10), InnoVersion.V(4, 1, 6), InnoVersion.V(5, 1, 5), InnoVersion.V(5, 1, 5)];

    /// <summary>The setup loader's offset table: where the headers and the data are.</summary>
    public readonly record struct LoaderOffsets(uint HeaderOffset, uint DataOffset);

    /// <summary>Finds the offset table: through the pointer at 0x30 (before 5.1.5) or in the RCDATA #11111 resource.</summary>
    public static LoaderOffsets? FindOffsets(Stream s)
    {
        var r = new BinaryReader(s, Encoding.ASCII, leaveOpen: true);
        if (s.Length >= 0x40)
        {
            s.Position = 0x30;
            if (r.ReadUInt32() == 0x6F6E6E49)   // "Inno"
            {
                uint table = r.ReadUInt32(), not = r.ReadUInt32();
                if (table == ~not && ReadOffsetTable(s, table) is { } o) return o;
            }
        }
        if (PeResources.Find(s, PeResources.RtRcData, 11111) is { } res) return ReadOffsetTable(s, res.Offset);
        return null;
    }

    private static LoaderOffsets? ReadOffsetTable(Stream s, long pos)
    {
        if (pos + 12 > s.Length) return null;
        s.Position = pos;
        var r = new BinaryReader(s, Encoding.ASCII, leaveOpen: true);
        var magic = r.ReadBytes(12);
        int k = Array.FindIndex(LoaderMagics, m => m.AsSpan().SequenceEqual(magic));
        if (k < 0) return null;
        var v = LoaderVersions[k];
        var crc = new Crc32();
        crc.Update(magic);
        uint Next() { var b = r.ReadBytes(4); crc.Update(b); return BitConverter.ToUInt32(b); }
        if (v >= InnoVersion.V(5, 1, 5)) Next();          // revision
        Next();                                             // total size
        Next();                                             // the setup exe's offset
        if (v < InnoVersion.V(4, 1, 6)) Next();             // its compressed size
        Next();                                             // its uncompressed size
        Next();                                             // its CRC32 (Adler-32 before 4.0.3)
        if (v < InnoVersion.V(4, 0, 0)) r.ReadUInt32();     // the messages' offset (not in the CRC)
        uint header = Next(), data = Next();
        if (v >= InnoVersion.V(4, 0, 10) && r.ReadUInt32() != crc.Value) return null;
        return new LoaderOffsets(header, data);
    }

    /// <summary>Opens an installer and reads its headers; throws <see cref="InstallerException"/> saying what is wrong.</summary>
    public static InnoSetup Open(string path)
    {
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        var setup = new InnoSetup(path) { ExeLength = s.Length };
        var offsets = FindOffsets(s);
        if (offsets is null)
        {
            if (PeResources.LooksLikeGalaxyStub(s))
                throw new InstallerException(InstallerError.GalaxyStub,
                    $"{System.IO.Path.GetFileName(path)} is a GOG Galaxy downloader, not an offline installer: it holds no game data. " +
                    "Download the offline backup installer (setup_....exe) from your GOG library instead.");
            throw new InstallerException(InstallerError.NotInno, $"{System.IO.Path.GetFileName(path)} is not an Inno Setup installer.");
        }
        setup.DataOffset = offsets.Value.DataOffset;
        s.Position = offsets.Value.HeaderOffset;
        setup.ReadHeaders(s);
        return setup;
    }

    private void ReadHeaders(Stream s)
    {
        var id = new byte[64];
        s.ReadExactly(id);
        IdString = Encoding.ASCII.GetString(id).TrimEnd('\0');
        if (InnoVersion.Parse(IdString) is not { } v)
            throw new InstallerException(InstallerError.NotInno, $"unrecognised setup data \"{IdString}\"");
        Version = v;
        if (v < InnoVersion.Oldest || IdString.Contains("Inno Setup Extensions") || IdString.Contains("with ISX"))
            throw new InstallerException(InstallerError.UnsupportedVersion,
                $"this installer uses Inno Setup data version {v}, which I26 can't read (it reads {InnoVersion.Oldest} and later).");
        try
        {
            long next;
            using (var block = InnoBlock.Open(s, v, out next))
                ReadPrimary(new InnoReader(block, v));
            s.Position = next;
            using (var block = InnoBlock.Open(s, v, out _))
                ReadDataEntries(new InnoReader(block, v));
        }
        catch (Exception ex) when (ex is EndOfStreamException or InvalidDataException or ArgumentOutOfRangeException or OverflowException)
        {
            throw new InstallerException(InstallerError.Corrupt, $"the installer's setup headers could not be read ({ex.Message}); damaged, or an Inno Setup variant I26 doesn't know", ex);
        }
    }

    private int _dataCount;

    private void ReadPrimary(InnoReader r)
    {
        var v = Version;
        var h = ReadSetupHeader(r, out var counts);
        Header = h;
        var langs = new List<string>();
        for (int i = 0; i < counts.Languages; i++) langs.Add(ReadLanguage(r));
        Languages = langs;
        for (int i = 0; i < counts.Messages; i++) { r.Str(); r.Bin(); r.I32(); }
        for (int i = 0; i < counts.Permissions; i++) r.Bin();
        for (int i = 0; i < counts.Types; i++)
        {
            r.Str(); r.Str(); r.Str(); r.Str();
            r.WinVer();
            r.Flags(1);
            r.U8();
            r.U64();
        }
        for (int i = 0; i < counts.Components; i++)
        {
            r.Str(); r.Str(); r.Str(); r.Str(); r.Str();
            r.U64(); r.I32(); r.U8();
            r.WinVer();
            r.Flags(5);
            r.U64();
        }
        for (int i = 0; i < counts.Tasks; i++)
        {
            for (int k = 0; k < 6; k++) r.Str();
            r.I32(); r.U8();
            r.WinVer();
            r.Flags(5);
        }
        for (int i = 0; i < counts.Directories; i++)
        {
            r.Str();
            r.Condition();
            r.U32();
            r.WinVer();
            r.I16();
            r.Flags(v >= InnoVersion.V(5, 2, 0) ? 5 : 3);
        }
        var files = new List<InnoFile>(counts.Files);
        for (int i = 0; i < counts.Files; i++) files.Add(ReadFile(r));
        Files = files;
        for (int i = 0; i < counts.Icons; i++)
        {
            for (int k = 0; k < 6; k++) r.Str();
            r.Condition();
            if (v >= InnoVersion.V(5, 3, 5)) r.Str();
            if (v >= InnoVersion.V(6, 1, 0)) r.Skip(16);
            r.WinVer();
            r.I32(); r.I32(); r.U8(); r.U16();
            int n = 3 + (v >= InnoVersion.V(5, 0, 3) && v < InnoVersion.V(6, 3, 0) ? 1 : 0) + (v >= InnoVersion.V(5, 4, 2) ? 1 : 0)
                    + (v >= InnoVersion.V(5, 5, 0) ? 1 : 0) + (v >= InnoVersion.V(6, 1, 0) ? 1 : 0);
            r.Flags(n);
        }
        for (int i = 0; i < counts.IniEntries; i++)
        {
            for (int k = 0; k < 4; k++) r.Str();
            r.Condition();
            r.WinVer();
            r.Flags(5);
        }
        for (int i = 0; i < counts.Registry; i++)
        {
            r.Str(); r.Str(); r.Bin();
            r.Condition();
            r.WinVer();
            r.U32(); r.I16(); r.U8();
            r.Flags(10 + (v >= InnoVersion.V(5, 1, 0) ? 2 : 0));
        }
        for (int i = 0; i < counts.Deletes + counts.UninstallDeletes; i++)
        {
            r.Str();
            r.Condition();
            r.WinVer();
            r.U8();
        }
        for (int i = 0; i < counts.Runs + counts.UninstallRuns; i++)
        {
            for (int k = 0; k < 5; k++) r.Str();
            if (v >= InnoVersion.V(5, 1, 13)) r.Str();
            r.Str();
            r.Condition();
            r.WinVer();
            r.I32(); r.U8();
            int n = 7 + (v >= InnoVersion.V(5, 1, 10) ? 2 : 0) + (v >= InnoVersion.V(5, 2, 0) ? 1 : 0)
                    + (v >= InnoVersion.V(6, 1, 0) ? 1 : 0) + (v >= InnoVersion.V(6, 3, 0) ? 1 : 0);
            r.Flags(n);
        }
        // the wizard images (large and small), then the decompressor and decryption DLLs
        for (int pass = 0; pass < 2; pass++)
        {
            uint n = v >= InnoVersion.V(5, 6, 0) ? r.U32() : 1;
            for (uint k = 0; k < n; k++) r.SkipBin();
        }
        if (h.Compression == InnoCompression.BZip2 || h.Compression == InnoCompression.Zlib) r.SkipBin();
        if (h.Encrypted && v < InnoVersion.V(6, 4, 0)) r.SkipBin();
        _dataCount = counts.DataEntries;
    }

    private sealed record Counts(int Languages, int Messages, int Permissions, int Types, int Components, int Tasks, int Directories,
        int Files, int DataEntries, int Icons, int IniEntries, int Registry, int Deletes, int UninstallDeletes, int Runs, int UninstallRuns);

    private InnoHeader ReadSetupHeader(InnoReader r, out Counts counts)
    {
        var v = Version;
        string appName = r.Str(), versioned = r.Str(), appId = r.Str();
        r.Str();                                            // copyright
        string publisher = r.Str();
        r.Str();                                            // publisher URL
        if (v >= InnoVersion.V(5, 1, 13)) r.Str();          // support phone
        r.Str(); r.Str();                                   // support URL, updates URL
        string appVersion = r.Str();
        string defaultDir = r.Str();
        r.Str();                                            // default group
        string baseFilename = r.Str();
        if (v < InnoVersion.V(5, 2, 5)) { r.SkipBin(); r.SkipBin(); r.SkipBin(); }   // licence, info before/after (ANSI)
        for (int i = 0; i < 7; i++) r.Str();               // uninstall files dir, name, icon, mutex, user name, organisation, serial
        if (v < InnoVersion.V(5, 2, 5)) r.SkipBin();        // compiled code
        for (int i = 0; i < 4; i++) r.Str();               // readme, contact, comments, modify path
        if (v >= InnoVersion.V(5, 3, 8)) r.Str();           // create uninstall registry key
        if (v >= InnoVersion.V(5, 3, 10)) r.Str();          // uninstallable
        if (v >= InnoVersion.V(5, 5, 0)) r.Str();           // close applications filter
        if (v >= InnoVersion.V(5, 5, 6)) r.Str();           // setup mutex
        if (v >= InnoVersion.V(5, 6, 1)) { r.Str(); r.Str(); }   // changes environment, associations
        if (v >= InnoVersion.V(6, 3, 0)) { r.Str(); r.Str(); }   // architecture expressions
        if (v >= InnoVersion.V(5, 2, 5)) { r.SkipBin(); r.SkipBin(); r.SkipBin(); }
        if (v >= InnoVersion.V(5, 2, 1) && v < InnoVersion.V(5, 3, 10)) r.SkipBin();   // uninstaller signature
        if (v >= InnoVersion.V(5, 2, 5)) r.SkipBin();       // compiled code
        if (!v.Unicode) r.Skip(32);                         // lead bytes (a 256-bit set)
        int languages = (int)r.U32(), messages = (int)r.U32(), permissions = (int)r.U32();
        int types = (int)r.U32(), components = (int)r.U32(), tasks = (int)r.U32();
        int dirs = (int)r.U32(), files = (int)r.U32(), data = (int)r.U32(), icons = (int)r.U32(), ini = (int)r.U32(), reg = (int)r.U32();
        int del = (int)r.U32(), udel = (int)r.U32(), run = (int)r.U32(), urun = (int)r.U32();
        counts = new Counts(languages, messages, permissions, types, components, tasks, dirs, files, data, icons, ini, reg, del, udel, run, urun);
        r.WinVer();
        var v6401 = InnoVersion.V(6, 4, 0, 1);
        if (v < v6401) { r.U32(); r.U32(); }                // back colours
        if (v < InnoVersion.V(5, 5, 7)) r.U32();            // image back colour
        if (v >= InnoVersion.V(6, 0, 0)) { r.U8(); r.U32(); r.U32(); }   // wizard style, resize percentages
        if (v >= InnoVersion.V(5, 5, 7)) r.U8();            // image alpha format
        r.Skip(v >= InnoVersion.V(6, 4, 0) ? 4 : v >= InnoVersion.V(5, 3, 9) ? 20 : 16);   // password hash
        r.Skip(v >= InnoVersion.V(6, 4, 0) ? 44 : 8);       // password salt
        r.U64();                                            // extra disk space
        uint slicesPerDisk = r.U32();
        r.U8();                                             // uninstall log mode
        r.U8();                                             // dir exists warning
        r.U8();                                             // privileges required
        if (v >= InnoVersion.V(5, 7, 0)) r.Flags(2);        // privileges override
        r.U8(); r.U8();                                     // language dialog, detection
        int c = r.U8();
        InnoCompression comp = v >= InnoVersion.V(5, 3, 9)
            ? c switch { 0 => InnoCompression.Stored, 1 => InnoCompression.Zlib, 2 => InnoCompression.BZip2, 3 => InnoCompression.Lzma1, 4 => InnoCompression.Lzma2, _ => InnoCompression.Unknown }
            : c switch { 0 => InnoCompression.Stored, 1 => InnoCompression.Zlib, 2 => InnoCompression.BZip2, 3 => InnoCompression.Lzma1, _ => InnoCompression.Unknown };
        if (v >= InnoVersion.V(6, 3, 0)) { }
        else if (v >= InnoVersion.V(5, 6, 0)) { r.Flags(5); r.Flags(5); }
        else if (v >= InnoVersion.V(5, 1, 0)) { r.Flags(4); r.Flags(4); }
        if (v >= InnoVersion.V(5, 2, 1) && v < InnoVersion.V(5, 3, 10)) { r.U32(); r.U32(); }   // signed uninstaller
        if (v >= InnoVersion.V(5, 3, 3)) { r.U8(); r.U8(); }    // disable dir / group pages
        if (v >= InnoVersion.V(5, 5, 0)) r.U64();
        else if (v >= InnoVersion.V(5, 3, 6)) r.U32();      // uninstall display size

        // the options set, in the order of the version's TSetupHeaderOption
        var names = new List<string> { "DisableStartupPrompt" };
        void Add(string n, bool when = true) { if (when) names.Add(n); }
        Add("Uninstallable", v < InnoVersion.V(5, 3, 10));
        Add("CreateAppDir");
        Add("DisableDirPage", v < InnoVersion.V(5, 3, 3));
        Add("DisableProgramGroupPage", v < InnoVersion.V(5, 3, 3));
        Add("AllowNoIcons"); Add("AlwaysRestart"); Add("AlwaysUsePersonalGroup");
        if (v < v6401) { Add("WindowVisible"); Add("WindowShowCaption"); Add("WindowResizable"); Add("WindowStartMaximized"); }
        Add("EnableDirDoesntExistWarning"); Add("Password"); Add("AllowRootDirectory"); Add("DisableFinishedPage");
        Add("ChangesAssociations", v < InnoVersion.V(5, 6, 1));
        Add("CreateUninstallRegKey", v < InnoVersion.V(5, 3, 8));
        Add("UsePreviousAppDir");
        Add("BackColorHorizontal", v < v6401);
        Add("UsePreviousGroup"); Add("UpdateUninstallLogAppName"); Add("UsePreviousSetupType");
        Add("DisableReadyMemo"); Add("AlwaysShowComponentsList"); Add("FlatComponentsList"); Add("ShowComponentSizes");
        Add("UsePreviousTasks"); Add("DisableReadyPage"); Add("AlwaysShowDirOnReadyPage"); Add("AlwaysShowGroupOnReadyPage");
        Add("AllowUNCPath"); Add("UserInfoPage"); Add("UsePreviousUserInfo"); Add("UninstallRestartComputer");
        Add("RestartIfNeededByRun"); Add("ShowTasksTreeLines"); Add("AllowCancelDuringInstall"); Add("WizardImageStretch");
        Add("AppendDefaultDirName"); Add("AppendDefaultGroupName"); Add("EncryptionUsed");
        Add("ChangesEnvironment", v >= InnoVersion.V(5, 0, 4) && v < InnoVersion.V(5, 6, 1));
        Add("ShowUndisplayableLanguages", v >= InnoVersion.V(5, 1, 7) && !v.Unicode);
        Add("SetupLogging", v >= InnoVersion.V(5, 1, 13));
        Add("SignedUninstaller", v >= InnoVersion.V(5, 2, 1));
        Add("UsePreviousLanguage", v >= InnoVersion.V(5, 3, 8));
        Add("DisableWelcomePage", v >= InnoVersion.V(5, 3, 9));
        if (v >= InnoVersion.V(5, 5, 0)) { Add("CloseApplications"); Add("RestartApplications"); Add("AllowNetworkDrive"); }
        Add("ForceCloseApplications", v >= InnoVersion.V(5, 5, 7));
        if (v >= InnoVersion.V(6, 0, 0)) { Add("AppNameHasConsts"); Add("UsePreviousPrivileges"); Add("WizardResizable"); }
        Add("UninstallLogging", v >= InnoVersion.V(6, 3, 0));
        var bits = r.Flags(names.Count);
        bool encrypted = bits[names.IndexOf("EncryptionUsed")], password = bits[names.IndexOf("Password")];
        return new InnoHeader(appName, versioned, appId, appVersion, publisher, defaultDir, baseFilename, slicesPerDisk, comp, encrypted, password);
    }

    private string ReadLanguage(InnoReader r)
    {
        var v = Version;
        string name = r.Str();
        r.SkipBin();                                        // language name (always UTF-16)
        if (v == InnoVersion.V(5, 5, 7, 1)) r.SkipBin();
        for (int i = 0; i < 4; i++) r.Str();               // dialog, title, welcome, copyright fonts
        r.SkipBin();                                        // data
        r.SkipBin(); r.SkipBin(); r.SkipBin();              // licence, info before/after
        r.U32();                                            // language id
        if (!v.Unicode) r.U32();                            // code page
        else if (v < InnoVersion.V(5, 3, 0)) r.U32();
        r.U32(); r.U32(); r.U32(); r.U32();                 // font sizes
        if (v == InnoVersion.V(5, 5, 7, 1)) r.U32();
        if (v >= InnoVersion.V(5, 2, 3)) r.U8();            // right to left
        return name;
    }

    private InnoFile ReadFile(InnoReader r)
    {
        var v = Version;
        r.Str();                                            // source
        string dest = r.Str();
        r.Str();                                            // font name
        if (v >= InnoVersion.V(5, 2, 5)) r.Str();           // strong assembly name
        var (components, tasks, languages, check) = r.Condition();
        r.WinVer();
        uint location = r.U32();
        uint attributes = r.U32();
        r.U64();                                            // external size
        r.I16();                                            // permission
        int n = 24 + (v >= InnoVersion.V(5, 0, 3) ? 1 : 0) + (v >= InnoVersion.V(5, 1, 0) ? 1 : 0) + (v >= InnoVersion.V(5, 1, 2) ? 2 : 0)
                + (v >= InnoVersion.V(5, 2, 0) ? 3 : 0) + (v >= InnoVersion.V(5, 2, 5) ? 1 : 0);
        r.Flags(n);
        r.U8();                                             // file type (user file, uninstaller, regsvr)
        return new InnoFile(dest, location == uint.MaxValue ? -1 : (int)location, components, tasks, languages, check, attributes);
    }

    private void ReadDataEntries(InnoReader r)
    {
        var v = Version;
        var list = new List<InnoData>(_dataCount);
        for (int i = 0; i < _dataCount; i++)
        {
            uint first = r.U32(), last = r.U32(), chunkOffset = r.U32();
            ulong fileOffset = r.U64(), fileSize = r.U64(), chunkSize = r.U64();
            FileChecksum sum = v >= InnoVersion.V(6, 4, 0) ? new(ChecksumKind.Sha256, r.Bytes(32))
                : v >= InnoVersion.V(5, 3, 9) ? new(ChecksumKind.Sha1, r.Bytes(20))
                : new(ChecksumKind.Md5, r.Bytes(16));
            long filetime = (long)r.U64();
            r.U32(); r.U32();                               // file version
            var names = new List<string> { "VersionInfoValid", "VersionInfoNotValid", "TimeStampInUTC", "IsUninstallerExe",
                "CallInstructionOptimized", "Touch", "ChunkEncrypted", "ChunkCompressed" };
            if (v >= InnoVersion.V(5, 1, 13)) names.Add("SolidBreak");
            if (v >= InnoVersion.V(5, 5, 7) && v < InnoVersion.V(6, 3, 0)) { names.Add("Sign"); names.Add("SignOnce"); }
            var f = r.Flags(names.Count);
            if (v >= InnoVersion.V(6, 3, 0)) r.U8();        // sign mode
            bool compressed = f[7], encrypted = f[6], call = f[4];
            var filter = !call ? InnoFilter.None : v < InnoVersion.V(5, 2, 0) ? InnoFilter.Call4108 : v < InnoVersion.V(5, 3, 9) ? InnoFilter.Call5200 : InnoFilter.Call5309;
            list.Add(new InnoData(i, first, last, chunkOffset, chunkSize, fileOffset, fileSize, sum, filetime,
                compressed ? Header.Compression : InnoCompression.Stored, encrypted, filter));
        }
        Data = list;
    }
}

/// <summary>The header streams' primitives: little-endian numbers, length-prefixed strings, Delphi sets.</summary>
internal sealed class InnoReader(Stream s, InnoVersion v)
{
    private readonly BinaryReader _r = new(s, Encoding.ASCII, leaveOpen: true);
    private static readonly Encoding Ansi = MakeAnsi();

    private static Encoding MakeAnsi()
    {
        try { return CodePagesEncodingProvider.Instance.GetEncoding(1252) ?? Encoding.Latin1; }
        catch (Exception) { return Encoding.Latin1; }
    }

    public byte U8() => _r.ReadByte();
    public ushort U16() => _r.ReadUInt16();
    public short I16() => _r.ReadInt16();
    public uint U32() => _r.ReadUInt32();
    public int I32() => _r.ReadInt32();
    public ulong U64() => _r.ReadUInt64();

    public byte[] Bytes(int n)
    {
        var b = _r.ReadBytes(n);
        if (b.Length != n) throw new EndOfStreamException();
        return b;
    }

    public void Skip(int n) => Bytes(n);

    /// <summary>A length-prefixed byte string.</summary>
    public byte[] Bin()
    {
        uint n = U32();
        if (n > 256u << 20) throw new InvalidDataException($"string of {n} bytes");
        return Bytes((int)n);
    }

    public void SkipBin()
    {
        uint n = U32();
        if (n > 256u << 20) throw new InvalidDataException($"string of {n} bytes");
        var buf = new byte[Math.Min(n, 1u << 16)];
        while (n > 0)
        {
            int k = (int)Math.Min(n, (uint)buf.Length);
            _r.BaseStream.ReadExactly(buf, 0, k);
            n -= (uint)k;
        }
    }

    /// <summary>A text string: UTF-16 in Unicode builds, the ANSI code page (1252) otherwise.</summary>
    public string Str()
    {
        var b = Bin();
        return v.Unicode ? Encoding.Unicode.GetString(b) : Ansi.GetString(b);
    }

    /// <summary>An item's condition strings: components, tasks, languages, check (then the after/before-install hooks).</summary>
    public (string Components, string Tasks, string Languages, string Check) Condition()
    {
        string c = Str(), t = Str(), l = Str(), k = Str();
        Str(); Str();
        return (c, t, l, k);
    }

    /// <summary>A min/max Windows version range (two times win, NT and service pack: 20 bytes).</summary>
    public void WinVer() => Skip(20);

    /// <summary>A Delphi set of <paramref name="count"/> members: one bit each, whole bytes, 3-byte sets padded to 4.</summary>
    public bool[] Flags(int count)
    {
        int bytes = (count + 7) / 8;
        var b = Bytes(bytes);
        if (bytes == 3) U8();
        var o = new bool[count];
        for (int i = 0; i < count; i++) o[i] = (b[i / 8] & (1 << (i % 8))) != 0;
        return o;
    }
}
