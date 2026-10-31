using System.Text;
using I76.Formats.Bwd2;
using I76.Formats.Mission;
using I76.Formats.Vfs;
using Xunit;

namespace I76.Formats.Tests;

public class VfsTests
{
    [Fact]
    public void ParsesZixHeaderAndEntries()
    {
        var z = new ZixIndex(Encoding.Latin1.GetBytes(
            "006116\r\n0 \\ I76_CD2\r\n----------------------------\r\nDIR: i76.zfs\r\n----------------------------\r\n1 01sbu01.wav\r\n1 ABC.MAP\r\n"));
        Assert.Equal(6116, z.DeclaredCount);
        Assert.Single(z.CdVolumes);
        Assert.Equal(new ZixSource(true, "i76.zfs", 0), Assert.Single(z.Sources));
        Assert.Equal(["01sbu01.wav", "abc.map"], z.Entries.Select(e => e.Name));
    }

    [Fact]
    public void ParsesPix()
    {
        var m = PakIndex.ParsePix(Encoding.Latin1.GetBytes("2\r\na.geo 0 10\r\nb.map 10 20\r\n"));
        Assert.Equal([new PakMember("a.geo", 0, 10), new PakMember("b.map", 10, 20)], m);
        Assert.Throws<InvalidDataException>(() => PakIndex.ParsePix(Encoding.Latin1.GetBytes("3\na 0 1\n")));
    }

    [Fact]
    public void LzoLiteralOnlyStream()
    {
        // First byte > 17: a literal run of (b - 17) bytes, then the end marker
        // (an M4 match with zero distance: 0x11 0x00 0x00).
        byte[] src = [17 + 5, (byte)'h', (byte)'e', (byte)'l', (byte)'l', (byte)'o', 0x11, 0x00, 0x00];
        Assert.Equal("hello", Encoding.Latin1.GetString(Lzo.Decompress(src, Lzo.Lzo1X)));
    }

    [Fact]
    public void Bwd2WalkRejectsTrailingBytes()
    {
        var ok = new byte[] { (byte)'B', (byte)'W', (byte)'D', (byte)'2', 8, 0, 0, 0, (byte)'A', (byte)'B', (byte)'C', (byte)'D', 8, 0, 0, 0 };
        Assert.Equal(["BWD2", "ABCD"], Bwd2File.Walk(ok).Select(c => c.Tag));
        Assert.Throws<InvalidDataException>(() => Bwd2File.Walk([.. ok, 1, 2, 3]));
    }

    [InstallFact]
    public void VfsResolvesArchiveAndPakMembers()
    {
        var vfs = TestInstall.Vfs;
        Assert.IsType<AssetSource.Zfs>(vfs.Locate("i76.zix") ?? vfs.Locate(vfs.Zfs.Entries[0].Name));
        var stem = vfs.Paks.Stems[0];
        var member = vfs.PakMembers(stem)[0];
        Assert.NotNull(vfs.Locate(member.Name));
        Assert.Equal(member.Size, vfs.Read(vfs.Locate(member.Name)!).Length);
    }

    [InstallFact]
    public void EveryMissionHasTerrainAndObjects()
    {
        var dir = TestInstall.Install.MissionDir!;
        foreach (var f in Directory.EnumerateFiles(dir, "*.*").Where(f => f.EndsWith(".msn", StringComparison.OrdinalIgnoreCase) && I76.Formats.Mission.MapFileSet.IsStockMission(f)))
        {
            var m = new MissionFile(f);
            var ter = TerrainTiles.Load(m, Path.ChangeExtension(f, ".TER"));
            var (r0, r1, c0, c1) = ter.Bounds();
            Assert.True(r1 >= r0 && c1 >= c0);
            Assert.NotNull(m.Palette());
        }
    }
}
