using I76.Formats.Shell;
using I76.Game.Shell;
using I76.Sim.Trip;
using Xunit;

namespace I76.Game.Tests;

/// <summary>The join form's session popup names a game's restrictions: the weapons by the checklist's rows, then the specials by the garage's names (All Specials for every one the install has).</summary>
public class RestrictionNamesTests
{
    private static readonly List<RestrictionRow> Rows = [new(0, -1, -1, 3, 17, "All Weapons"), new(1, 2, -1, 3, 0, "All Slug Throwers")];

    [Fact]
    public void SpecialsFollowTheWeapons()
    {
        Assert.Equal(["All Slug Throwers", "Radar Jammer", "Blower"],
            MeleeScreen.RestrictionNames(WeaponRestrictions.Normalize(false, [(2, -1)]).WithSpecials([1, 3]), Rows, nitro: true));
        Assert.Equal(["Tire Covers"], MeleeScreen.RestrictionNames(WeaponRestrictions.None.WithSpecials([11]), Rows, nitro: true));
        Assert.Equal("All Weapons, X-Aust Brake", MeleeScreen.RestrictionText(WeaponRestrictions.Everything.WithSpecials([4]), Rows, nitro: true));
    }

    [Fact]
    public void EverySpecialIsAllSpecials()
    {
        var nine = WeaponRestrictions.None.WithSpecials(Enumerable.Range(1, 9));
        Assert.Equal(["All Specials"], MeleeScreen.RestrictionNames(nine, Rows, nitro: false));
        // the Nitro Pack has two more: nine of eleven are named
        Assert.Equal(9, MeleeScreen.RestrictionNames(nine, Rows, nitro: true).Count);
        Assert.Equal(["All Weapons", "All Specials"], MeleeScreen.RestrictionNames(WeaponRestrictions.Everything.WithSpecials(Enumerable.Range(1, 11)), Rows, nitro: true));
    }
}
