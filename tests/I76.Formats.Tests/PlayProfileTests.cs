using I76.Formats.Shell;
using Xunit;

namespace I76.Formats.Tests;

public class PlayProfileTests
{
    [Fact]
    public void ABaseGameKeepsItsFlagsWhole()
    {
        Assert.Equal((0x1f, 2), PlayProfile.Resolve(false, (0x1f, 2), null));
    }

    [Fact]
    public void NitroKeepsOnlyArcadePhysicsAndWiperFluid()
    {
        Assert.Equal((0x21, 1), PlayProfile.Resolve(true, (0x3f, 1), (0, 0)));
        Assert.Equal(0x20, PlayProfile.Mask(true, 0x3e));
        Assert.Equal(0x3e, PlayProfile.Mask(false, 0x3e));
    }

    /// <summary>A shell.cfg from before the split: both games start from the shared values, Nitro without the base game's cheat bits.</summary>
    [Fact]
    public void AFileFromBeforeTheSplitGivesBothGamesTheSharedValues()
    {
        var shared = (0x1d, 2);   // Arcade, Unlim. Ammo, Armor... and Badass
        Assert.Equal((0x1d, 2), PlayProfile.Resolve(false, shared, shared));
        var nitro = PlayProfile.Resolve(true, null, shared);
        Assert.Equal((0x01, 2), nitro);
        Assert.Equal(0, nitro.Item1 & 0x1c);   // no cheat bit: a Nitro run is not marked cheated
    }

    [Fact]
    public void NitrosOwnValuesWinOverTheSharedOnes()
    {
        Assert.Equal((0x20, 0), PlayProfile.Resolve(true, (0x20, 0), (0x1f, 2)));
        Assert.Equal((0, 0), PlayProfile.Resolve(true, null, null));
    }
}
