using System.Security.Cryptography;
using System.Text;
using I76.Formats.Installers;
using Xunit;

namespace I76.Formats.Tests;

/// <summary>
/// The GOG installer reader's pieces on small synthetic vectors (no installer needed): CRC-32, the LZMA1/LZMA2 decoders
/// (vectors made with Python's lzma module, i.e. liblzma), Inno's CRC'd header blocks, the x86 call filters, the data
/// side (chunks across setup-N.bin slices, checksums, missing parts), version parsing, Delphi sets and the file selection.
/// </summary>
public class InstallerFormatTests
{
    /// <summary>The vectors' plain data: 400 lines of text, then 1500 LCG bytes (incompressible).</summary>
    private static byte[] Text() => Encoding.ASCII.GetBytes(string.Concat(Enumerable.Range(0, 400).Select(i => $"line {i}: {i * i % 977} the quick brown fox\n")));

    private static byte[] Noise(int n = 1500)
    {
        uint x = 1;
        var b = new byte[n];
        for (int i = 0; i < n; i++) { x = (x * 1103515245 + 12345) & 0x7fffffff; b[i] = (byte)(x >> 16); }
        return b;
    }

    private static string Sha1(byte[] b) => Convert.ToHexString(SHA1.HashData(b)).ToLowerInvariant();

    private static byte[] Vec(string b64) => Convert.FromBase64String(string.Concat(b64.Where(c => !char.IsWhiteSpace(c))));

    private static byte[] ReadAll(Stream s)
    {
        var ms = new MemoryStream();
        s.CopyTo(ms, 1000);   // odd-sized reads, so the decoders stop and resume mid-match
        return ms.ToArray();
    }

    [Fact]
    public void Crc32MatchesTheStandardCheckValue() => Assert.Equal(0xCBF43926u, Crc32.Compute("123456789"u8));

    [Fact]
    public void Lzma1WithInnosHeaderAndAnEndMarker()
    {
        // Python: lzma.compress(text + noise + text[:3000], FORMAT_ALONE), the 8-byte size field cut out (Inno keeps 5 header bytes)
        var a = Text().Concat(Noise()).Concat(Text().Take(3000)).ToArray();
        var got = ReadAll(new LzmaStream(new MemoryStream(Vec(VecA))));
        Assert.Equal(17940, got.Length);
        Assert.Equal("fa719f67e6f868f2d5c827b473379e412faf69c7", Sha1(a));
        Assert.Equal(Sha1(a), Sha1(got));
    }

    [Fact]
    public void Lzma1StopsWhereTheReaderStops()
    {
        // a chunk's files are cut out of the stream as needed: reading only the first 5000 bytes gives exactly those
        var s = new LzmaStream(new MemoryStream(Vec(VecA)), maxOutput: 5000);
        var buf = new byte[5000];
        s.ReadExactly(buf);
        Assert.Equal(Text().Take(5000), buf);
    }

    [Fact]
    public void Lzma2CompressedChunk()
    {
        // Python: FORMAT_RAW, LZMA2 preset 6, 1 MiB dictionary; one LZMA chunk (control 0xE0); Inno writes the dictionary byte first
        var data = Text().Concat(Noise()).ToArray();
        var raw = Vec(VecB);
        Assert.Equal(0xE0, raw[0]);
        var got = ReadAll(new Lzma2Stream(new MemoryStream([20, .. raw])));
        Assert.Equal("8858f49b1dc705dfe5258e6c5404d43004cf734e", Sha1(data));
        Assert.Equal(Sha1(data), Sha1(got));
    }

    [Fact]
    public void Lzma2StoredChunksThenTheEnd()
    {
        // incompressible data is stored (control 1: dictionary reset, size-1 big-endian), as liblzma wrote it for the noise alone
        var n = Noise();
        var stream = new List<byte> { 20, 1, (byte)((n.Length - 1) >> 8), (byte)(n.Length - 1) };
        stream.AddRange(n);
        stream.AddRange(new byte[] { 2, 0, 9 });   // a second stored chunk, no reset: 10 bytes
        stream.AddRange(n.Take(10));
        stream.Add(0);
        var got = ReadAll(new Lzma2Stream(new MemoryStream(stream.ToArray())));
        Assert.Equal(n.Concat(n.Take(10)), got);
    }

    [Fact]
    public void Lzma2RejectsAChunkBeforeAnyDictionaryReset() =>
        Assert.Throws<InvalidDataException>(() => ReadAll(new Lzma2Stream(new MemoryStream([20, 2, 0, 0, 65, 0]))));

    /// <summary>An Inno header block: CRC32 and (stored size, compressed flag), then 4 KiB pieces each led by its CRC32.</summary>
    private static byte[] Block(byte[] payload, bool compressed)
    {
        var o = new MemoryStream();
        var pieces = new MemoryStream();
        for (int i = 0; i < payload.Length; i += 4096)
        {
            var p = payload.AsSpan(i, Math.Min(4096, payload.Length - i));
            pieces.Write(BitConverter.GetBytes(Crc32.Compute(p)));
            pieces.Write(p);
        }
        var head = new byte[5];
        BitConverter.GetBytes((uint)pieces.Length).CopyTo(head, 0);
        head[4] = (byte)(compressed ? 1 : 0);
        o.Write(BitConverter.GetBytes(Crc32.Compute(head)));
        o.Write(head);
        pieces.Position = 0;
        pieces.CopyTo(o);
        return o.ToArray();
    }

    [Fact]
    public void HeaderBlocksStoredAndLzma()
    {
        var v = InnoVersion.V(5, 5, 0);
        var plain = Text();
        var s = new MemoryStream([.. Block(plain, false), .. Block(Vec(VecA), true), 0xAB]);
        using (var b = InnoBlock.Open(s, v, out long end))
        {
            Assert.Equal(plain, ReadAll(b));
            s.Position = end;
        }
        using (var b = InnoBlock.Open(s, v, out long end2))
        {
            var buf = new byte[100];
            b.ReadExactly(buf);   // the headers are read as far as they go; the next block starts at `end` regardless
            Assert.Equal(Text().Take(100), buf);
            Assert.Equal(s.Length - 1, end2);
        }
    }

    [Fact]
    public void HeaderBlockCrcErrorsAreReportedAsDamage()
    {
        var blk = Block(Text(), false);
        blk[^10] ^= 1;
        var ex = Assert.Throws<InstallerException>(() => ReadAll(InnoBlock.Open(new MemoryStream(blk), InnoVersion.V(5, 5, 0), out _)));
        Assert.Equal(InstallerError.Corrupt, ex.Kind);
        blk = Block(Text(), false);
        blk[5] ^= 1;   // the block header's own CRC
        Assert.Equal(InstallerError.Corrupt, Assert.Throws<InstallerException>(() => InnoBlock.Open(new MemoryStream(blk), InnoVersion.V(5, 5, 0), out _)).Kind);
    }

    /// <summary>Code-like data: calls and jumps with near and far targets, and E8s at the 64 KiB block edges.</summary>
    private static byte[] Code(int n)
    {
        var b = Noise(n);
        uint x = 7;
        for (int i = 0; i + 5 <= n; i += 7)
        {
            x = x * 1103515245 + 12345;
            b[i] = (x & 1) != 0 ? (byte)0xE8 : (byte)0xE9;
            int rel = (int)(x >> 8) % 200000 - 100000;
            BitConverter.GetBytes(rel).CopyTo(b, i + 1);
            if ((x & 6) == 0) b[i + 4] = 0x12;   // a far target the 5.2.0 filter leaves alone
        }
        foreach (int edge in new[] { 0x10000 - 5, 0x10000 - 4, 0x10000 - 1, n - 3 })
            if (edge >= 0 && edge < n) b[edge] = 0xE8;
        return b;
    }

    /// <summary>Inno's encoder for the 5.2.0/5.3.9 filter (CallInstructionOptimized), as the compiler applies it.</summary>
    private static void Encode5200(byte[] d, bool flip)
    {
        for (int start = 0; start < d.Length; start += 0x10000)
        {
            int len = Math.Min(0x10000, d.Length - start);
            for (int i = start; i + 4 < start + len; i++)
            {
                if (d[i] is not (0xE8 or 0xE9)) continue;
                if (d[i + 4] is 0x00 or 0xFF)
                {
                    uint addr = (uint)(i + 5) & 0xFFFFFF;
                    uint rel = (uint)(d[i + 1] | d[i + 2] << 8 | d[i + 3] << 16);
                    uint abs = rel + addr;
                    d[i + 1] = (byte)abs; d[i + 2] = (byte)(abs >> 8); d[i + 3] = (byte)(abs >> 16);
                    if (flip && (rel & 0x800000) != 0) d[i + 4] = (byte)~d[i + 4];
                }
                i += 4;
            }
        }
    }

    /// <summary>The inverse of the 4.1.8 decoder, byte by byte (an E8 near the end takes the bytes that are left).</summary>
    private static void Encode4108(byte[] d)
    {
        for (int i = 0; i < d.Length; i++)
        {
            if (d[i] is not (0xE8 or 0xE9)) continue;
            uint addr = ~(uint)(i + 5) + 1;
            int k = 1;
            for (; k <= 4 && i + k < d.Length; k++)
            {
                byte want = d[i + k];
                byte b = (byte)(want - (byte)addr);
                d[i + k] = b;
                addr = (addr + b) >> 8;
            }
            i += k - 1;
        }
    }

    [Theory]
    [InlineData(InnoFilter.Call5200)]
    [InlineData(InnoFilter.Call5309)]
    [InlineData(InnoFilter.Call4108)]
    public void CallFiltersUndoTheEncoder(InnoFilter kind)
    {
        var original = Code(150000);
        var enc = (byte[])original.Clone();
        if (kind == InnoFilter.Call4108) Encode4108(enc);
        else Encode5200(enc, kind == InnoFilter.Call5309);
        Assert.NotEqual(original, enc);
        Assert.Equal(original, ReadAll(new InnoExeFilter(new MemoryStream(enc), kind)));
    }

    [Theory]
    [InlineData("Inno Setup Setup Data (5.5.0) (u)", 5, 5, 0, true)]
    [InlineData("Inno Setup Setup Data (5.2.3)", 5, 2, 3, false)]
    [InlineData("Inno Setup Setup Data (6.3.0)", 6, 3, 0, true)]
    [InlineData("Inno Setup Setup Data (6.4.0.1)", 6, 4, 0, true)]
    public void VersionStrings(string id, int a, int b, int c, bool unicode)
    {
        var v = InnoVersion.Parse(id)!.Value;
        Assert.Equal((a, b, c, unicode), (v.A, v.B, v.C, v.Unicode));
    }

    [Fact]
    public void VersionOrderAndForeignIds()
    {
        Assert.True(InnoVersion.V(5, 5, 0) > InnoVersion.V(5, 3, 10));
        Assert.True(InnoVersion.V(6, 4, 0, 1) > InnoVersion.V(6, 4, 0));
        Assert.Null(InnoVersion.Parse("Nullsoft Install System v3.0"));
    }

    [Theory]
    [InlineData(8, 1)]
    [InlineData(9, 2)]
    [InlineData(17, 4)]   // a 3-byte set is padded to 4
    [InlineData(25, 4)]
    [InlineData(33, 5)]
    public void DelphiSetsTakeWholeBytesAndThreeArePaddedToFour(int members, int bytes)
    {
        var s = new MemoryStream(new byte[] { 0x05, 0x80, 0x01, 0x00, 0x01, 0xEE });
        var f = new InnoReader(s, InnoVersion.V(5, 5, 0)).Flags(members);
        Assert.Equal(bytes, s.Position);
        Assert.True(f[0] && !f[1] && f[2]);
        if (members > 15) Assert.True(f[15]);
    }

    [Theory]
    [InlineData(@"{app}\miss16\a01.msn", "miss16/a01.msn")]
    [InlineData(@"{app}\I76.ZFS", "I76.ZFS")]
    [InlineData(@"{APP}\music\2.mp3", "music/2.mp3")]
    [InlineData(@"{tmp}\gog.ico", null)]
    [InlineData(@"{app}\..\evil.txt", null)]
    [InlineData(@"{app}\c:\evil.txt", null)]
    [InlineData(@"{commondesktop}\Interstate '76.lnk", null)]
    public void DestinationsUnderAppOnly(string dest, string? rel) => Assert.Equal(rel, GogInstaller.AppRelative(dest));

    [Theory]
    [InlineData("I76.ZFS", true)]
    [InlineData("nitro.zfs", true)]
    [InlineData("miss16/t01.msn", true)]
    [InlineData("smk/INTROF01.SMK", true)]
    [InlineData("music/10.mp3", true)]
    [InlineData("force/CANNON1.FRC", true)]
    [InlineData("ADDON/vehscn.vcf", true)]
    [InlineData("Lee_____.ttf", true)]
    [InlineData("nitshell.dll", true)]
    [InlineData("i76shell.dll", true)]
    [InlineData("gamekey.map", true)]
    [InlineData("i76.exe", true)]
    [InlineData("nitro.exe", true)]
    [InlineData("DLL/i76.exe", false)]
    [InlineData("GOGLauncher.exe", false)]
    [InlineData("goggame-1207661003.info", false)]
    [InlineData("glide2x.ovl", false)]
    [InlineData("SMACKW32.DLL", false)]
    [InlineData("DLL/WINET.DLL", false)]
    [InlineData("BUILDER/BUILDER.DOC", false)]
    [InlineData("splash/sp256/IMAIN1.BMP", false)]
    [InlineData("SP256/IMAIN1.BMP", false)]
    [InlineData("webcache.zip", false)]
    public void WhatIsExtracted(string rel, bool wanted) => Assert.Equal(wanted, GogInstaller.SkipReason(rel) is null);

    [Fact]
    public void AChosenFolderWithOtherThingsInItGetsASubfolder()
    {
        var dir = TestDir("target");
        Assert.Equal(dir, GogInstaller.TargetIn(dir, GogGame.I76));                       // empty
        File.WriteAllText(Path.Combine(dir, "notes.txt"), "x");
        Assert.Equal(Path.Combine(dir, "Interstate76-Nitro"), GogInstaller.TargetIn(dir, GogGame.Nitro));
        File.WriteAllText(Path.Combine(dir, GogInstaller.MarkerName), "x");
        Assert.Equal(dir, GogInstaller.TargetIn(dir, GogGame.I76));                       // an earlier extraction: again in place
    }

    [Fact]
    public void AGalaxyStubIsNamedAsSuch()
    {
        var dir = TestDir("stub");
        var exe = Path.Combine(dir, "GOG_Galaxy_Game.exe");
        var b = new byte[4096];
        "MZ"u8.CopyTo(b);
        Encoding.Unicode.GetBytes("GOG Galaxy - Game Installer").CopyTo(b, 1000);
        File.WriteAllBytes(exe, b);
        Assert.Equal(InstallerError.GalaxyStub, Assert.Throws<InstallerException>(() => InnoSetup.Open(exe)).Kind);
        var junk = Path.Combine(dir, "readme.txt");
        File.WriteAllText(junk, "not an installer");
        Assert.Equal(InstallerError.NotInno, Assert.Throws<InstallerException>(() => InnoSetup.Open(junk)).Kind);
    }

    private static string TestDir(string name)
    {
        var d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "i26-tests", "installer-" + name);
        if (Directory.Exists(d)) Directory.Delete(d, true);
        Directory.CreateDirectory(d);
        return d;
    }

    private static InnoHeader Header(string baseName = "setup") =>
        new("Test", "Test 1.0", "id", "1.0", "pub", "{app}", baseName, 1, InnoCompression.Lzma1, false, false);

    /// <summary>A slice file: "idska32\x1a", its total size, then the bytes.</summary>
    private static void WriteSlice(string path, byte[] body)
    {
        var o = new List<byte>("idska32\x1a"u8.ToArray());
        o.AddRange(BitConverter.GetBytes((uint)(12 + body.Length)));
        o.AddRange(body);
        File.WriteAllBytes(path, o.ToArray());
    }

    [Fact]
    public void AChunkRunsOnAcrossSetupBinSlicesWithChecksumsVerified()
    {
        // one LZMA1 chunk ("zlb\x1a" + VecA) holding three files, split over setup-1.bin and setup-2.bin
        var dir = TestDir("slices");
        var exe = Path.Combine(dir, "setup.exe");
        System.IO.File.WriteAllBytes(exe, [0x4D, 0x5A]);
        var chunk = "zlb\x1a"u8.ToArray().Concat(Vec(VecA)).ToArray();
        WriteSlice(Path.Combine(dir, "setup-1.bin"), [.. new byte[100], .. chunk.Take(1000)]);
        WriteSlice(Path.Combine(dir, "SETUP-2.BIN"), chunk.Skip(1000).ToArray());   // any letter case
        var plain = Text().Concat(Noise()).Concat(Text().Take(3000)).ToArray();
        InnoData Entry(int i, int off, int size, byte[]? digest = null) =>
            new(i, 0, 1, 112, (ulong)(chunk.Length - 4), (ulong)off, (ulong)size,
                new FileChecksum(ChecksumKind.Sha1, digest ?? SHA1.HashData(plain.AsSpan(off, size))), 0, InnoCompression.Lzma1, false, InnoFilter.None);
        var data = new[] { Entry(0, 0, 4000), Entry(1, 4000, 10000), Entry(2, 15000, 2940) };
        var setup = InnoSetup.ForTests(exe, Header(), 0, data);
        var got = new Dictionary<int, byte[]>();
        int verified = 0;
        InnoProgress last = default;
        InnoExtractor.Run(setup, [data[2], data[0]], (d, s) => got[d.Index] = ReadAll(s), _ => verified++, p => last = p);
        Assert.Equal(2, verified);
        Assert.Equal(plain.AsSpan(0, 4000).ToArray(), got[0]);
        Assert.Equal(plain.AsSpan(15000, 2940).ToArray(), got[2]);
        Assert.Equal((2, 2, 17940L), (last.Files, last.FilesTotal, last.BytesTotal));

        // a wrong checksum
        var bad = Entry(1, 4000, 10000, new byte[20]);
        Assert.Equal(InstallerError.ChecksumMismatch, Assert.Throws<InstallerException>(() =>
            InnoExtractor.Run(InnoSetup.ForTests(exe, Header(), 0, [bad]), [bad], (_, s) => ReadAll(s))).Kind);

        // the second part missing, named in the message
        System.IO.File.Delete(Path.Combine(dir, "SETUP-2.BIN"));
        var ex = Assert.Throws<InstallerException>(() => InnoExtractor.Run(setup, data, (_, s) => ReadAll(s)));
        Assert.Equal(InstallerError.MissingParts, ex.Kind);
        Assert.Contains("setup-2.bin", ex.Message);
    }

    [Fact]
    public void EncryptedAndBzip2InstallersAreRefusedClearly()
    {
        var dir = TestDir("refuse");
        var exe = Path.Combine(dir, "setup.exe");
        File.WriteAllBytes(exe, new byte[16]);
        var enc = new InnoData(0, 0, 0, 0, 10, 0, 10, new FileChecksum(ChecksumKind.None, []), 0, InnoCompression.Lzma1, true, InnoFilter.None);
        Assert.Equal(InstallerError.Encrypted, Assert.Throws<InstallerException>(() =>
            InnoExtractor.Run(InnoSetup.ForTests(exe, Header(), 8, [enc]), [enc], (_, _) => { })).Kind);
        var bz = enc with { Encrypted = false, Compression = InnoCompression.BZip2 };
        Assert.Equal(InstallerError.UnsupportedCompression, Assert.Throws<InstallerException>(() =>
            InnoExtractor.Run(InnoSetup.ForTests(exe, Header(), 8, [bz]), [bz], (_, _) => { })).Kind);
    }

    private const string VecA = """
        XQAAgAAANhpKHwigJle9ilnHhGmDXKJOgpWQI9L1AS+n8J/JuEoxigRx5U7qyMfWUmL/jH8wxbwVNj4xB/fceasMK2SWMnM8KnGUpuJyt1L/rycUyBRp
        aefTD7tYAVRpFOktn8wz4+E2iEbxJQ+9DsjHLJXUbBPq5iFdwVDSkCwnr5t+5LsKzjQO71dXKWxRigkws8YdP08QAtr0mOf/J3WLlfnpfJ5lInn+I7D6
        TpJKHmLEOnDUjNOKaJ/vvcEBwehOofTB5kseAYtpuuL71UrS8rYEF+7iy3MmZIt3V4NvAU0b3ywgoNU6EbinQs6tS16UKip04nQC+MdEb11asImV0uPX
        7XvrfrjvB9ogDztRHRrpbf+x1MEY8X9YbwphDPsk5GiLwsOHXYGqufs0uwqAZL8sc3LKKaL5IAimli03Gze4P7IWItdN+QtFWoJzQGWNRmarcbR7Bcyv
        mx/B7LzJhMMs//4ArY58r0jtJhHXvWnbJvcNVYwsFPAJIEoNccs0C1ZCNyKUFJ79uL2LHhvGbztqZkIy5+lRzMdMHTD5WpytVfiJb0XnE6tuAxtAv4ow
        9lVLRKK+YPaYRmObBG3FNcX0oJmALEfOESPTDXqaep+2liL3tFhrnFPQa+CepnGi1mgCjl3ALDRLv3nmB/DatqiZIth5hYPZPa6t+H/WMjODzN9Hz+tq
        0RpSRijrwF6MxlMgNGzXKTsatuzEszLnuX0TrYUh7+ja3ALNm1pagP6HHHy1wOqySx9DsAILtYXj4YXizrNTLJg4w8mLrEU+28TOelPu4E5xAgjOrrJc
        oCWC8pcU4gkLn27KRyfbkNHpJac3vzJfIIkze8YGHNBStUClljQFPcJ4awsxWdJMgBiCyZKvsgXCkUuVsUqybZvCyi+fxEbVmNkRYHoikXtZXksw1cC4
        rUQsIjm+H+tYLZJmk1hwzV2CZZnoLcNXZBmtEvms+sa8p4hohgfhx2QMU0o5+Pr/zAyzxau26x3SWfdvCF82cETmujR/Jno1SktQiQU22xT15skcnVXs
        ik63sgfm7BP51pVIw5wlb73lJlxunmjvwaXM0ZuHJDDzFLWmlPmmYiVAzFoIezcgK7RL9aUpD3MURuyposlmyoXxWAgCYcWYb54mKCFQdzX12FZeR1Z3
        gGhMgtovAIodiEF1TGrmenp1uxDsQLjyep5lNYZoBAJrgBfHeOcepNwNRBtTxRvKnn/F54fTyS+nWLLxMX64muo6Eh5HdPafCyd/qKn6Va1z+uYp86Cg
        IH+4dfJXAUE3L5CspMscxvM9DK/MKlVz/XvCQcZs8DTw8jhSn71R4qyfNjgTpymxSjmDNYEv/dxmMGUZrMA7U5RstdtCBpKtltmSj7TZ9GUYMl4NGIQQ
        Z596ny0brNyVjDgWM1gJJ22MK6WYLYGKDuAGpIuJU5UpE0UvNV7gghxQ0nAzDa2QtBURoitSScPr8Tk2EslViwCOcpz8NjP0OYNen1QAJuCa9k6jpPRN
        +vPcvGe3tQXuLDI1nSQh5My2Ye72wNxXHCvG1LW0XmTJXOyvuLiIMv2i8afawcr6uUW1vYxcikiCSWYNxAwQqRplE/KNiiqw2CDU1VoH9umMlOYvN3+3
        JvXo0lOn2YGP+bmAeKxEwM2k+GkjT53SMRyc7dTspZ9diFfzgndl+wYM9RPsmU0ogqjIbGNSd6s5YmhDKmfrbR9uX/b3+5JO6/cGLyk9r1udmQwWsw1c
        N+hgRFpOMMmZli+WCNZpGnD/J5tT6A2waBG8ELwVguKMHWPb/WCSUx0ZIjJLoucJ4yKJMyicYZRuKJJnW3mrO53ZLNAtlQwFQt2lELWrMccKm8/zic3R
        f1K7uF5SUlJswjNBrZji/F2J/jhfEs5d3YQN8SY84h/q1vBV1viUGQc2yg0ml5/GOL90BoXgRkjfF31v9PM6rxrNJAk8kr8U9GIECsdoAwyBNJFaVzeF
        nyW42UMeaXH7OiXHFBccKnlRWpz9zXuCKiRS/F5PceHPJU48JgNiAcbdFbMJhf2ezhwl7+OPikjXd9FdorScv8I7ghPKrzVRisROZNBMmQZsjRmBijok
        2CHry81A6mMBo42gc4fSzHRhk5kg7kkbK/tT/wCY2jlGTPOHcS0qH7kGeBCkywiGDMcPa2/wjfi0tkynDHPR+vCyy3Ol2laogjxzlOL02LMUbtvIY1yo
        q2JnH9Ft8aTQ08P0qxnTTM9cCF47Oe5ZSCoX2r1BFdwuJ+FpthFC1ZhGUEE5tMOiHfaYZW8RHsVIIbK0Qftzis14+cc5RijI5V7EkTOZuGzRnehX8ROh
        FNbpWAJyj7Yos2VFHSlYrer+OeUikLFbRIYL5vMDU13Mh01W7rsvF0N/k7/vcZgJPcY3OlT/czpdUZqpYPeKOEGUwwjjF+Ie33V6NHV84QlsbksW2o7E
        Pp9RTA7zvwNgLsgOt2faRS/wNYFnqbY+wThqm+hlQnW6GeAxBcDt1MNzU3J2UMgx/AqdZ5muN/QEHBKuo9PoVqjyCrKubq/Pv4TY/WxkzsS92WqmnZHH
        4naM+jMwKE6I0GWYLFJ5du2yb/8nIh6FVhZdQT6HUYesOWEcurcg322pU69xEi9JaBnm9MmcIkb3c/M3YecVs5P99GtmYAX3J7gz7HN08CAHTbL2Yhm0
        F5AxvPyPhCnITxmpTfZDF8hsLL0Fzsvslv/U+nMHSqFVOwxSIq2F9IZmLulVBaqvA1wEUIvKW/keFKTSCVAezLveMBdpu4AaNJ//s6TVmzUISBWwtEFP
        epEc3OeTHaYCFSHWNvn+Enld1se2/Moj8lyEtc30kuGcqCztIQ2mea4lCLcQjBa245MyZz8XxhPxilIhT7WQq2vQ1ukqSKqsv4DZQOZ4hYsye2IPUmME
        HYS0zqa6KY6qAUIlFSbanDnKI6Nh3eF0ELLW0qgE01/34VcDFsktBe78jNDJuIxvk2t2a27MiRqy6yD9pqwhrvvBH7+D26Rqs3lWmVRj9Inh3Ph4I+2v
        990LCWHCCqnRbU5E8dh+KhW4kGsFMonCWF7yNcADhys5/jSmQRr3wRevhnVoq/oIv7OAo3Kc9vEewVQ3Xa5locNLB8yRkzGQeQqxMu1La0Npm9td9v32
        F/1MuAxe5Gnt0j2rY26QosqhfRvvCDDPlwR5HTJrQlSoVx2UQzz3+9Nw5qUjZQOmpGExP2xnSfm8BXHr4Y58IeEnZJU+J/YVW8uYa3rIbKX9wGpQa3Ds
        YaNhV7AsMklQepOWfOwRDy04AlaBARRJXB5/Zw42sGdXtahX6Sd3foJwB1+mpqIuq8b+uFAouiBfIkYl5mIMVC5xeVYzlKOxKyq9VdivyePBKw2gKM92
        3CW21gilLWzCyqPpsNRrC7MH/IK7bOS/IGmxenJGDBFhAhO2DKAjzGo3Tl/XjOdpiYvJYMQia+2ke9yy4izueLV7EE1O0va5Tbtk/i1UwK1JHcKlXlwZ
        YluA6D0535RSB7DbI9IGrXIMZstGOuGQEiK10kJi8ur8Xhb1/A3GVze4pjyURhzSS3DBoL0o/m5gkKOJLzUgv38WgMC8qvqeUxCT0YOE3abiN2srO8R1
        ZoTwKZ5a0UMvMfptPYSBeKv0PH6NUwku5uSz/zVg4zR0Ya8+LqAqEZHrJn2ZXF10byOyv/htjNVniR2bBGqC1mPgAEHQx395xcdvKIsELAkf7cLDC2jJ
        REhNbGdeKwbiNA1nRltbv6Bwf1QKf//Cme2A
        """;
    private const string VecB = """
        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
        """;
}
