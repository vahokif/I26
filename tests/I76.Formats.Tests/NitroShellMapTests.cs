using I76.Formats.Shell;
using I76.Formats.Vfs;
using Xunit;

namespace I76.Formats.Tests;

/// <summary>Runs when I76_NITRO_INSTALL names the Nitro Pack (and I76_INSTALL names the base game); skipped otherwise.</summary>
public sealed class NitroFactAttribute : FactAttribute
{
    public static string? Dir =>
        Environment.GetEnvironmentVariable("I76_NITRO_INSTALL") is { Length: > 0 } e && Directory.Exists(e) ? e : null;
    public NitroFactAttribute()
    {
        if (Dir is null || TestInstall.Dir is null) Skip = "set I76_NITRO_INSTALL (and I76_INSTALL, the base game) to run";
    }
}

public class NitroShellMapTests
{
    private static ShellDatabase Nitro() => ShellDatabase.Open(new GameInstall(NitroFactAttribute.Dir!).FindFile("database.tvf")!);
    private static ShellDatabase Base() => ShellDatabase.Open(TestInstall.Install.FindFile("DATABASE.MW2")!);

    [NitroFact]
    public void ProfileAndDefaultMission()
    {
        var n = new GameInstall(NitroFactAttribute.Dir!);
        Assert.True(n.IsNitro);
        Assert.Equal("p01", n.DefaultMission());
        Assert.False(TestInstall.Install.IsNitro);
        Assert.Equal("a01", TestInstall.Install.DefaultMission());
    }

    [NitroFact]
    public void FontsWavesAndCreditsAreTheBaseBytesAtTheirNitroNumbers()
    {
        var (n, b) = (Nitro(), Base());
        Assert.True(n.IsNitro);
        Assert.False(b.IsNitro);
        for (int id = 65; id <= 82; id++) Assert.True(n.Raw(id).SequenceEqual(b.Raw(id)), $"record {id}");
        Assert.NotEmpty(n.Credits());
    }

    [NitroFact]
    public void EveryBaseBackgroundBankAndFontResolves()
    {
        var n = Nitro();
        for (int id = 1; id <= 32; id++) Assert.Equal(768, n.Background(id).Palette.Length);
        Assert.Equal(22, NitroShellMap.Record(26));          // the main menu's card is the Nitro logo
        for (int id = 33; id <= 64; id++) _ = n.Bank(id);
        for (int id = 66; id <= 71; id++) Assert.True(n.Font(id).Height > 0, $"font {id}");
        foreach (int id in new[] { 1, 3, 4, 5, 14, 27, 31 }) Assert.True(n.Has(id) || id is 27 or 31);
    }

    [NitroFact]
    public void MatchedSpritesAreThePicturesTheBaseNumbersName()
    {
        var (n, b) = (Nitro(), Base());
        // the main menu's bank: the base's OPTIONS pair (1 dim, 0 lit) is Nitro's 0 and 1 swapped, same pixels
        var (nb, bb) = (n.Bank(0x3e), b.Bank(0x3e));
        Assert.Equal(bb[1]!.Pixels, nb[1]!.Pixels);
        // the shell's small-form bank 0x24: the arrow sprite (14) keeps its picture
        Assert.Equal(b.Bank(0x24)[14]!.Pixels, n.Bank(0x24)[14]!.Pixels);
        // a bank Nitro dropped is empty, not an exception
        Assert.Null(n.Bank(0x40)[0]);
    }
}
