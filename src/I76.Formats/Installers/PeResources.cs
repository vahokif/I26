using System.Text;

namespace I76.Formats.Installers;

/// <summary>Just enough of the PE/COFF format to find a resource's bytes in an .exe (Inno Setup's offset table, RCDATA #11111).</summary>
public static class PeResources
{
    public const int RtRcData = 10;

    /// <summary>The file offset and size of resource type/id (first language), or null.</summary>
    public static (long Offset, int Size)? Find(Stream s, int type, int id)
    {
        try
        {
            var r = new BinaryReader(s, Encoding.ASCII, leaveOpen: true);
            if (s.Length < 0x40) return null;
            s.Position = 0;
            if (r.ReadUInt16() != 0x5A4D) return null;   // MZ
            s.Position = 0x3C;
            uint pe = r.ReadUInt32();
            if (pe + 24 > s.Length) return null;
            s.Position = pe;
            if (r.ReadUInt32() != 0x00004550) return null;   // PE\0\0
            r.ReadUInt16();
            int sections = r.ReadUInt16();
            s.Position += 12;
            int optSize = r.ReadUInt16();
            r.ReadUInt16();
            long opt = s.Position;
            ushort magic = r.ReadUInt16();
            long dirs = opt + (magic == 0x20B ? 112 : 96);
            s.Position = dirs + 2 * 8;   // IMAGE_DIRECTORY_ENTRY_RESOURCE
            uint resRva = r.ReadUInt32();
            if (resRva == 0) return null;
            var secs = new List<(uint Va, uint VSize, uint Raw, uint RawSize)>();
            s.Position = opt + optSize;
            for (int i = 0; i < sections; i++)
            {
                s.Position += 8;
                uint vsize = r.ReadUInt32(), va = r.ReadUInt32(), rawSize = r.ReadUInt32(), raw = r.ReadUInt32();
                s.Position += 16;
                secs.Add((va, vsize, raw, rawSize));
            }
            long? ToFile(uint rva)
            {
                foreach (var x in secs)
                    if (rva >= x.Va && rva < x.Va + Math.Max(x.VSize, x.RawSize)) return x.Raw + (rva - x.Va);
                return null;
            }
            if (ToFile(resRva) is not { } resBase) return null;

            // one directory level: the entry for id (or the first entry when id < 0)
            uint? Entry(long dir, int want)
            {
                s.Position = dir + 12;
                int named = r.ReadUInt16(), ids = r.ReadUInt16();
                for (int i = 0; i < named + ids; i++)
                {
                    uint name = r.ReadUInt32(), off = r.ReadUInt32();
                    if (want < 0 || ((name & 0x80000000) == 0 && name == want)) return off;
                }
                return null;
            }
            if (Entry(resBase, type) is not { } t || (t & 0x80000000) == 0) return null;
            if (Entry(resBase + (t & 0x7FFFFFFF), id) is not { } n || (n & 0x80000000) == 0) return null;
            if (Entry(resBase + (n & 0x7FFFFFFF), -1) is not { } l || (l & 0x80000000) != 0) return null;
            s.Position = resBase + l;
            uint dataRva = r.ReadUInt32();
            int size = r.ReadInt32();
            return ToFile(dataRva) is { } off2 ? (off2, size) : null;
        }
        catch (EndOfStreamException) { return null; }
    }

    /// <summary>
    /// A GOG Galaxy downloader stub ("GOG Galaxy - Game Installer"): a small .exe that downloads the game, holding no data.
    /// Looked for in the version resource's UTF-16 strings and as ASCII, in the first few MB.
    /// </summary>
    public static bool LooksLikeGalaxyStub(Stream s)
    {
        if (s.Length > 32L << 20) return false;   // the stubs are well under a megabyte
        s.Position = 0;
        var buf = new byte[Math.Min(s.Length, 32L << 20)];
        s.ReadExactly(buf);
        var span = buf.AsSpan();
        return span.IndexOf(Encoding.Unicode.GetBytes("GOG Galaxy")) >= 0 || span.IndexOf("GOG Galaxy"u8) >= 0;
    }
}
