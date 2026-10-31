using I76.Formats.Shell;
using Xunit;

namespace I76.Formats.Tests;

public class TextEntryTests
{
    [Fact] public void TheLayoutsCharacterIsWhatTypes()
    {
        Assert.Equal(':', TextEntry.Typed(':', ';', shift: true, false));   // US Shift+;
        Assert.Equal(':', TextEntry.Typed(':', '.', shift: true, false));   // a layout with ':' on Shift+.
        Assert.Equal(';', TextEntry.Typed(';', ';', shift: false, false));
        Assert.Equal('7', TextEntry.Typed('7', '7', false, false));
    }

    [Fact] public void ACharacterEventWithoutUnicodeFallsBackToTheShiftedLabel()
    {
        Assert.Equal(':', TextEntry.Typed(0, ';', true, false));
        Assert.Equal(';', TextEntry.Typed(0, ';', false, false));
        Assert.Equal('A', TextEntry.Typed(0, 'A', true, false));
        Assert.Equal('a', TextEntry.Typed(0, 'A', false, false));
        Assert.Equal((char)0, TextEntry.Typed(0, 'C', false, ctrlAltMeta: true));
        Assert.Equal((char)0, TextEntry.Typed(0, 0x40000001, false, false)); // Escape, Enter...
    }

    [Fact] public void CharactersTheFontLacksFoldToTheNearest()
    {
        Assert.Equal('e', TextEntry.Fold('é'));
        Assert.Equal('U', TextEntry.Fold('Ü'));
        Assert.Equal('s', TextEntry.Fold('ſ'));
        Assert.Equal('n', TextEntry.Fold('ñ'));
        Assert.Equal('c', TextEntry.Fold('ć'));
        Assert.Equal('\'', TextEntry.Fold('’'));
        Assert.Equal('"', TextEntry.Fold('“'));
        Assert.Equal('-', TextEntry.Fold('–'));
        Assert.Equal(':', TextEntry.Fold('：'));
        Assert.Equal((char)0, TextEntry.Fold('中'));
        Assert.Equal((char)0, TextEntry.Fold(10));
    }
}

public class ArenaPickTests
{
    [Fact] public void TheHostFormsArenaIsFoundByItsFile()
    {
        var races = new List<(string Name, string Msn)> { ("Drag Jump", "dragjump.rac"), ("Destruct", "destruct.rac") };
        Assert.Equal(1, I76.Formats.Shell.ArenaPick.IndexOf(races, "DESTRUCT.RAC"));
        Assert.Equal(0, I76.Formats.Shell.ArenaPick.IndexOf(races, "ffa.cbt"));
        Assert.Equal(0, I76.Formats.Shell.ArenaPick.IndexOf(races, null));
    }
}
