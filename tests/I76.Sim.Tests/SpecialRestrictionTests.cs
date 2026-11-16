using System.Text.Json;
using I76.Formats.Vfs;
using I76.Sim.Trip;
using Xunit;

namespace I76.Sim.Tests;

/// <summary>The port's restricted specials beside WEAPON RESTRICTIONS (docs/hidden-content.md §5): the list, the command line and the checklist's SPECIALS page.</summary>
public class SpecialRestrictionTests
{
    [Fact]
    public void SpecialsAreTheirOwnPartOfTheList()
    {
        var w = WeaponRestrictions.None.WithSpecials([3, 1, 3, 0, 12]);
        Assert.Equal([1, 3], w.Specials);                // ascending, each once, only real types
        Assert.False(w.IsEmpty);
        Assert.True(w.NoWeapons);
        Assert.True(w.RestrictsSpecial(1));
        Assert.False(w.RestrictsSpecial(2));
        Assert.False(w.Restricts(2, 0));
        Assert.Equal([3, 1], w.SpecialViolations([3, 0, 1]));
        Assert.Empty(w.SpecialViolations([2, 0, 0]));
        // All Weapons does not cover the specials, and the specials do not change the weapons
        Assert.False(WeaponRestrictions.Everything.RestrictsSpecial(1));
        Assert.True(WeaponRestrictions.Everything.WithSpecials([2]).All);
        // nothing at all is the empty list itself; equality is by content
        Assert.Same(WeaponRestrictions.None, WeaponRestrictions.None.WithSpecials([]));
        Assert.Same(WeaponRestrictions.None, w.WithSpecials([]));
        Assert.Equal(WeaponRestrictions.None.WithSpecials([1, 3]), w);
        Assert.NotEqual(WeaponRestrictions.None.WithSpecials([1]), w);
        Assert.NotEqual(WeaponRestrictions.Everything, WeaponRestrictions.Everything.WithSpecials([1]));
        Assert.Equal(w.GetHashCode(), WeaponRestrictions.None.WithSpecials([1, 3]).GetHashCode());
        Assert.Equal("Radar Jammer", WeaponRestrictions.SpecialName(1));
        Assert.Equal("Tire Covers", WeaponRestrictions.SpecialName(11));
    }

    [Fact]
    public void TheCommandLineNamesSpecials()
    {
        var w = WeaponRestrictions.Parse("jammer, nitro ,blower", null, out var err)!;
        Assert.Equal("", err);
        Assert.Equal([1, 2, 3], w.Specials);
        Assert.True(w.NoWeapons);
        Assert.Equal("jammer,nitro,blower", w.ToSpec());
        // every short name, an alias, the garage's name and the part's stem
        string[] names = ["jammer", "nitro", "blower", "exhaust", "bumper", "curb", "mudflaps", "seats", "cupholders", "mary", "covers"];
        for (int t = 1; t <= 11; t++)
        {
            Assert.Equal([t], WeaponRestrictions.Parse(names[t - 1], null, out _)!.Specials);
            Assert.Equal([t], WeaponRestrictions.Parse(WeaponRestrictions.SpecialName(t), null, out _)!.Specials);
            Assert.Equal([t], WeaponRestrictions.Parse($"spc{t:00}", null, out _)!.Specials);
        }
        Assert.Equal([4], WeaponRestrictions.Parse("X-Aust Brake", null, out _)!.Specials);
        Assert.Equal([2], WeaponRestrictions.Parse("NitrousOxide", null, out _)!.Specials);
        Assert.Equal([1], WeaponRestrictions.Parse("radar", null, out _)!.Specials);
        // with the weapons, and back
        var both = WeaponRestrictions.Parse("slugs,mary,2:6", null, out _)!;
        Assert.Equal([(2, -1)], both.Pairs);                       // (2:6 is under the whole class)
        Assert.Equal([10], both.Specials);
        Assert.Equal(both, WeaponRestrictions.Parse(both.ToSpec(), null, out _));
        var all = WeaponRestrictions.Parse("all,specials", null, out _)!;
        Assert.True(all.All);
        Assert.Equal(Enumerable.Range(1, 11), all.Specials);       // no install: every type
        Assert.Equal(all, WeaponRestrictions.Parse(all.ToSpec(), null, out _));
        Assert.Equal("none", WeaponRestrictions.None.ToString());
        Assert.Null(WeaponRestrictions.Parse("jammer,lasers", null, out err));
        Assert.Contains("lasers", err);
    }

    /// <summary>"specials" is every special the install's garage offers: nine on the base game, eleven on the Nitro Pack.</summary>
    [Theory]
    [InlineData("I76_INSTALL", 9)]
    [InlineData("I76_NITRO_INSTALL", 11)]
    public void SpecialsMeansTheInstallsOwn(string env, int count)
    {
        if (Environment.GetEnvironmentVariable(env) is not { Length: > 0 } dir || !Directory.Exists(dir)) return;
        var vfs = new AssetVfs(new GameInstall(dir));
        Assert.Equal(Enumerable.Range(1, count), WeaponRestrictions.Parse("specials", vfs, out _)!.Specials);
    }

    /// <summary>The list travels to a host migration's successor in the options (JSON, as the savestate's extras carry them).</summary>
    [Fact]
    public void TheListSurvivesTheOptionsJson()
    {
        var opts = new JsonSerializerOptions { IncludeFields = true };
        foreach (var w in new[] { WeaponRestrictions.None.WithSpecials([1, 11]), WeaponRestrictions.Everything.WithSpecials([5]), WeaponRestrictions.Normalize(false, [(2, 6)]) })
            Assert.Equal(w, JsonSerializer.Deserialize<WeaponRestrictions>(JsonSerializer.Serialize(w, opts), opts));
        // an older host's options (no specials) read as none
        var old = JsonSerializer.Deserialize<WeaponRestrictions>("""{"All":true,"Pairs":[]}""", opts)!;
        Assert.True(old.All);
        Assert.Empty(old.Specials);
    }

    [Fact]
    public void TheSpecialsPageHasARowForEachOfTheGaragesSpecials()
    {
        var nitro = SpecialChecklist.ChecklistRows(true);
        var base_ = SpecialChecklist.ChecklistRows(false);
        Assert.Equal(12, nitro.Count);
        Assert.Equal(10, base_.Count);
        // NO SPECIALS IN GAME where the weapons page has NO WEAPONS IN GAME
        Assert.Equal((SpecialChecklist.Class, -1, 3, 17), (nitro[0].Class, nitro[0].Tier, nitro[0].Column, nitro[0].Row));
        Assert.True(nitro[0].IsHeader);
        // each special by the garage's name, the first six down the left half, the rest down the right, from the first row under the headers
        for (int t = 1; t <= 11; t++)
        {
            var r = nitro[t];
            Assert.Equal((t, WeaponRestrictions.SpecialName(t)), (r.Tier, r.Name));
            Assert.Equal(t <= 6 ? (0, t) : (3, t - 6), (r.Column, r.Row));
            Assert.False(r.IsHeader);
        }
        Assert.Equal(nitro.Count, nitro.Select(r => (r.Column, r.Row)).Distinct().Count());
        Assert.DoesNotContain(base_, r => r.Tier is 10 or 11);
    }

    [Fact]
    public void TheSpecialsPageClicksAsTheWeaponsPage()
    {
        var c = new SpecialChecklist(true);
        Assert.Empty(c.Build());
        c.Toggle(1); c.Toggle(3);                       // Radar Jammer, Blower
        Assert.Equal([1, 3], c.Build());
        c.Toggle(0);                                    // NO SPECIALS IN GAME: every one, ticking nothing else
        Assert.Equal(Enumerable.Range(1, 11), c.Build());
        Assert.False(c.Checked[2]);
        c.Toggle(1);                                    // a row off clears it
        Assert.False(c.Checked[0]);
        Assert.Equal([3], c.Build());
        // a kept list ticks its boxes back; every special is NO SPECIALS IN GAME alone
        var d = new SpecialChecklist(true);
        d.Load(WeaponRestrictions.Everything.WithSpecials([2, 10]));
        Assert.Equal([2, 10], d.Build());
        Assert.True(d.Checked[2] && d.Checked[10] && !d.Checked[0] && !d.Checked[1]);
        d.Load(WeaponRestrictions.None.WithSpecials(Enumerable.Range(1, 11)));
        Assert.True(d.Checked[0]);
        Assert.Equal(1, d.Checked.Count(x => x));
        Assert.Equal(Enumerable.Range(1, 11), d.Build());
        d.Load(WeaponRestrictions.None);
        Assert.Empty(d.Build());
    }
}
