using I76.Game.Shell;
using Xunit;

namespace I76.Game.Tests;

/// <summary>The cheats marker: set by any of Unlim. Ammo, Armor or Chas. Rein. at a read of the Play Options, kept until the end menu turns it off.</summary>
public class CheatMarkerTests
{
    [Fact] public void ACheatOnAtMissionStartMarksTheRunAndTurningItOffInTheShellDoesNotUnmarkIt()
    {
        CheatMarker.Clear();
        CheatMarker.Read(0x01 | 0x02 | 0x20);      // cornering, no salvage, wiper: not cheats
        Assert.False(CheatMarker.Set);
        foreach (int bit in new[] { 0x04, 0x08, 0x10 })
        {
            CheatMarker.Clear();
            CheatMarker.Read(bit);
            Assert.True(CheatMarker.Set);
        }
        CheatMarker.Read(0);                         // the next mission, the cheat turned off in the shell
        Assert.True(CheatMarker.Set);
        CheatMarker.Clear();                         // Turn off Cheater Options
        Assert.False(CheatMarker.Set);
    }
}
