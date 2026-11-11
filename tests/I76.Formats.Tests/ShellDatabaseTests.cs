using I76.Formats.Shell;
using Xunit;

namespace I76.Formats.Tests;

public class ShellDatabaseTests
{
    private static ShellDatabase Db() => ShellDatabase.Open(TestInstall.Install.FindFile("DATABASE.MW2")!);

    [InstallFact]
    public void TheBackgroundsUnpackToTheirPcx()
    {
        var db = Db();
        Assert.Equal(83, db.Count);
        for (int id = 1; id <= 32; id++)
        {
            var p = db.Background(id);
            Assert.Equal(id == 17 ? (408, 406) : (640, 480), (p.Width, p.Height));
            Assert.Equal(768, p.Palette.Length);
        }
    }

    /// <summary>Each widget is exactly its sprite's size.</summary>
    [InstallFact]
    public void TheMainMenuSpritesAreTheirWidgetsSize()
    {
        var bank = Db().Bank(0x3e);
        Assert.Equal(47, bank.Count);
        foreach (var (sprite, w, h) in new[] { (1, 140, 45), (31, 54, 45), (25, 100, 50), (5, 120, 50), (33, 83, 22), (23, 70, 22), (21, 120, 22), (29, 100, 22), (3, 90, 22), (34, 400, 21), (44, 400, 21) })
            Assert.Equal((w, h), (bank[sprite]!.Width, bank[sprite]!.Height));
    }

    [InstallFact]
    public void ABankIsDecodedOnce()
    {
        // a screen asks for its banks every time it draws: the internet roster asked for bank 36 (its arrow) on every answer that changed
        // the list, tens of milliseconds a time decoding the whole bank again
        var db = Db();
        var bank = db.Bank(0x24);
        Assert.Same(bank, db.Bank(0x24));
        Assert.Same(db.BankNative(0x24), db.BankNative(0x24));
        // the same pictures as a fresh decode
        var fresh = new SpriteBank(db.RawNative(0x24), null, 0x24);
        Assert.Equal(fresh.Count, bank.Count);
        for (int i = 0; i < bank.Count; i++)
            Assert.Equal(fresh[i]?.Pixels, bank[i]?.Pixels);
        Assert.Equal((0x24, 14), (bank[14]!.Bank, bank[14]!.Index));
    }

    [InstallFact]
    public void TheFontsAndSoundsRead()
    {
        var db = Db();
        Assert.Equal(new[] { 12, 15, 15, 15, 10 }, new[] { 0x44, 0x42, 0x45, 0x46, 0x47 }.Select(r => db.Font(r).Height));
        Assert.True(db.Font(0x46).Glyph('T') is { Width: > 0 });
        Assert.Equal("RIFF"u8.ToArray(), db.Wave(81)[..4]);
        Assert.True(db.Credits().Length > 300);
    }

    [InstallFact]
    public void TheNotepadFaceInksAtTheTopOfItsRamp()
    {
        // record 70 is antialiased: glyph values 1..14, the stroke's interior at 14 (black in Joe's palette)
        var f = Db().Font(0x46);
        Assert.Equal(0x46, f.Record);
        Assert.Equal(14, f.InkValue);
    }
}
