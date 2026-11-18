using I76.Game.Boot;
using I76.Game.Shell;
using Xunit;

namespace I76.Game.Tests;

/// <summary>Play Options' Skip Intro Movies (#206): off by default, kept by a file of version 13 on, and it leaves out the start-up's movies alone.</summary>
public class SkipIntroSettingTests
{
    [Fact]
    public void ANewPlayerHasItOff() => Assert.False(new ShellSettings().SkipIntro);

    [Theory]
    [InlineData(12, null, false)]   // a file from before the key: the intro plays, as before
    [InlineData(12, true, false)]   // a key an older version never wrote is not trusted
    [InlineData(13, null, false)]   // written only when on
    [InlineData(13, true, true)]
    [InlineData(13, false, false)]
    [InlineData(14, true, true)]
    public void AFileKeepsItsChoice(int version, bool? stored, bool on) => Assert.Equal(on, ShellSettings.SkipIntroFrom(version, stored));

    [Fact]
    public void SaveWritesTheVersionThatReadsItBack() => Assert.True(ShellSettings.SkipIntroFrom(ShellSettings.Version, true));

    [Fact]
    public void ItIsAPlayOptionsRow() => Assert.Contains("Skip Intro Movies", PlayOptionsScreen.PortRows);

    [Fact]
    public void OffTheIntroAndCreditsPlay() => Assert.Equal(["introf01.smk", "credf01.smk"], IntroMovies.Startup(null, menuShown: false, skipMovies: false, skipIntro: false));

    [Fact]
    public void OnTheStartUpGoesStraightToTheMenu() => Assert.Empty(IntroMovies.Startup(null, menuShown: false, skipMovies: false, skipIntro: true));

    [Fact]
    public void AMovieNamedOnTheCommandLineStillPlays() => Assert.Equal(["int08f01.smk"], IntroMovies.Startup("int08f01.smk", false, false, skipIntro: true));

    [Theory]
    [InlineData(true, false)]       // back to the menu after a mission: never again
    [InlineData(false, true)]       // --skip-movies
    public void TheOtherGatesStillSkip(bool menuShown, bool skipMovies) => Assert.Empty(IntroMovies.Startup(null, menuShown, skipMovies, skipIntro: false));
}
