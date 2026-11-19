using I76.Formats.Shell;
using I76.Formats.Vfs;
using I76.Sim.Trip;
using Xunit;

namespace I76.Sim.Tests;

/// <summary>The WEAPON RESTRICTIONS checklist made from an install's own weapons (the base game's, which has no Nitro shell table).</summary>
public class RestrictionChecklistTests
{
    private static AssetVfs? Vfs(string env) =>
        Environment.GetEnvironmentVariable(env) is { Length: > 0 } dir && Directory.Exists(dir) ? new AssetVfs(new GameInstall(dir)) : null;

    /// <summary>Every box on the art's grid, none on another; a header first in its half's run; All Weapons on the right's last row.</summary>
    private static void OnTheGrid(List<RestrictionRow> rows)
    {
        Assert.All(rows, r => Assert.InRange(r.Column, 0, RestrictionChecklist.GridX.Length - 1));
        Assert.All(rows, r => Assert.InRange(r.Row, 0, RestrictionChecklist.GridY.Length - 1));
        Assert.Equal(rows.Count, rows.Select(r => (r.Column, r.Row)).Distinct().Count());
        Assert.Equal(rows.Count, rows.Select(r => (r.Class, r.Tier)).Distinct().Count());
        Assert.Contains(rows, r => r is { Class: -1, Tier: -1, Column: 3, Row: 17 });
        foreach (var r in rows.Where(r => !r.IsHeader))
        {
            // its group's header is in the same half, above it, in the header column
            var head = rows.Single(h => h.IsHeader && h.Class == r.Class);
            Assert.Equal(r.Column >= 3, head.Column >= 3);
            Assert.True(head.Row < r.Row);
            Assert.Contains(r.Column % 3, new[] { 1, 2 });
            Assert.Equal(RestrictionChecklist.GroupOf(r.Class), r.Group);
        }
    }

    [Fact]
    public void TheBaseGameListsItsOwnWeaponsGroupedAsNitrosTable()
    {
        if (Vfs("I76_INSTALL") is not { } vfs) return;
        var rows = RestrictionChecklist.FromInstall(vfs, nitro: false);
        OnTheGrid(rows);
        // the five groups, missiles, flamethrowers and droppers on the left from rows 0, 6 and 12, slugs from the right's top
        Assert.Equal([(4, 0, 0), (5, 0, 6), (6, 0, 12), (2, 3, 0)], rows.Where(r => r.IsHeader && r.Class >= 2 && r.Class != 3).Select(r => (r.Class, r.Column, r.Row)));
        // slug throwers run long (HADES and the tank cannon): the arc-fire launchers move down past the row after them
        var arc = rows.Single(r => r.IsHeader && r.Class == 3);
        Assert.True(arc.Row > rows.Where(r => r.Class == 2).Max(r => r.Row));
        // a gun and its turret share a line (STD, TRT); the Howitzer has no gun, and the EZK Mortar no turret: two lines
        var fire = rows.Where(r => r.Class == 4 && r.Tier % 100 == 0).ToList();
        Assert.Equal([1, 2], fire.Select(r => r.Column).Order());
        Assert.Single(fire.Select(r => r.Row).Distinct());
        var howitzer = rows.Single(r => r is { Class: 3, Tier: 103 });
        var ezk = rows.Single(r => r is { Class: 3, Tier: 3 });
        Assert.NotEqual(ezk.Row, howitzer.Row);
        Assert.Equal(5, howitzer.Column);
        // the install's own names; the hidden ones that are complete are there, the handgun is not
        Assert.Contains(rows, r => r.Name == HiddenContent.WeaponName(vfs, "gmlight"));
        Assert.Contains(rows, r => r is { Class: 2, Tier: 6 });
        Assert.DoesNotContain(rows, r => r.Class == 1);
        // every weapon a base car can mount is in it: All Weapons and the headers aside, the boxes stand for each (class, tier) once
        var built = new RestrictionChecklist(rows);
        built.Toggle(rows.FindIndex(r => r is { Class: 4, Tier: -1 }));
        Assert.Equal(WeaponRestrictions.Normalize(false, [(4, -1)]), built.Build());
    }

    [Fact]
    public void TheGridIsTheNitroTablesOwn()
    {
        if (Environment.GetEnvironmentVariable("I76_NITRO_INSTALL") is not { Length: > 0 } dir || !Directory.Exists(dir)) return;
        var install = new GameInstall(dir);
        var dll = install.FindFile("nitshell.dll") is { } f ? File.ReadAllBytes(f) : null;
        var (x, y) = RestrictionTable.Grid(dll);
        Assert.Equal(RestrictionChecklist.GridX, x);
        Assert.Equal(RestrictionChecklist.GridY, y);
        // made from its weapons instead (a Nitro Pack without the table), its rows sit on the grid too, a header for each of the five
        // classes (a group that runs past its half's foot loses its last lines, which its header still covers)
        var own = RestrictionChecklist.FromInstall(new AssetVfs(install), nitro: true);
        OnTheGrid(own);
        Assert.Equal([2, 3, 4, 5, 6], own.Where(r => r.IsHeader && r.Class >= 0).Select(r => r.Class).Order());
    }
}
