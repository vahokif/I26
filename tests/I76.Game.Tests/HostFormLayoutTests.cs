using I76.Formats.Input;
using I76.Game.Shell;
using Xunit;
using Xunit.Abstractions;

namespace I76.Game.Tests;

/// <summary>The base game's network host form as the port draws it: a labelled row for every field, the form's own hotspot ids, the pad reaching them all.</summary>
public class HostFormLayoutTests(ITestOutputHelper output)
{
    private static readonly HostFormLayout.State Melee = new()
    {
        Driver = "Unnamed", Area = "The Crater", GameName = "The Crater", Drivers = 4, MaxScore = 20000, BotSkill = "Normal", BotCars = "Random",
        Rate = 20, ShareMaps = true, CarReset = true, Cb = true, MakeModel = "ABX Leprechaun", Variant = "Econo",
    };
    private static readonly HostFormLayout.State Coop = Melee with { Coop = true };

    // the ids the form had before it was drawn by the port (less 7, the original's driver box, which on a host form only showed the host's own car again)
    private static readonly int[] MeleeIds = [3, 4, 5, 6, 8, 9, 11, 12, 13, 14, 15, 16, 17, 18, 20, 21, 22, 26, 27, 28, 29, 30, 31, 32, 33, 34, 35, 36, 37, 38, 43];
    private static readonly int[] CoopIds = [3, 4, 5, 6, 8, 9, 11, 12, 13, 14, 15, 17, 22, 27, 28, 32, 33, 35, 36, 37, 38, 40, 41, 42, 43];

    private static string Value(List<HostFormLayout.Row> rows, string label) =>
        string.Join(" ", rows.Single(r => r.Label == label).Cells.Select(c => c.Text));

    [Fact]
    public void EveryFieldHasALabelledRowOfItsOwn()
    {
        var rows = HostFormLayout.RowsOf(Melee);
        string[] labels =
        [
            "Local Driver", "Game Type", "Area of Play", "Game Name", "No. Drivers", "Team Play", "Invite Only", "Max Score", "Password", "Bots",
            "Bot Skill", "A.I. Cars", "Ignition Lock", "Sim Rate", "Share Maps", "Hidden Weapons & Cars", "Unlimited Turrets", "Car Reset", "CB Radio",
            "Chase Crosshair", "Make / Model", "Variant",
        ];
        Assert.Equal(labels.Order(), rows.Select(r => r.Label).Order());
        // one row each, inside the tables, the car's rows clear of the rules above them, the left table with rows to spare
        foreach (var side in new[] { false, true })
        {
            var at = rows.Where(r => r.Right == side).Select(r => r.Index).ToList();
            Assert.Equal(at.Count, at.Distinct().Count());
            Assert.All(at, k => Assert.InRange(k, 0, HostFormLayout.Rows - 1));
        }
        Assert.True(rows.Where(r => r.Right).Where(r => r.Label is not ("Make / Model" or "Variant")).All(r => r.Index < HostFormLayout.CarRow));
        Assert.True(HostFormLayout.Rows - 1 - rows.Where(r => !r.Right).Max(r => r.Index) >= 4, "the left table keeps rows for more options");
    }

    [Fact]
    public void ACoopGameLeavesOutWhatItHasNot()
    {
        var labels = HostFormLayout.RowsOf(Coop).Select(r => r.Label).ToList();
        foreach (var gone in new[] { "Team Play", "Max Score", "Bots", "Bot Skill", "A.I. Cars", "Share Maps" }) Assert.DoesNotContain(gone, labels);
        foreach (var kept in new[] { "Invite Only", "Password", "Hidden Weapons & Cars", "Unlimited Turrets", "Car Reset", "CB Radio", "Chase Crosshair", "Sim Rate", "Ignition Lock" }) Assert.Contains(kept, labels);
        // and has the co-op rules a melee has not
        var melee = HostFormLayout.RowsOf(Melee).Select(r => r.Label).ToList();
        foreach (var coop in new[] { "Co-op Damage", "Co-op Scaling", "Rolling Start" }) { Assert.Contains(coop, labels); Assert.DoesNotContain(coop, melee); }
        // the rows close up: no gap in the left table under the area list
        var left = HostFormLayout.RowsOf(Coop).Where(r => !r.Right && r.Index > HostFormLayout.ListRow).Select(r => r.Index).Order().ToList();
        Assert.Equal(Enumerable.Range(HostFormLayout.ListRow + HostFormLayout.ListRows, left.Count), left);
    }

    [Fact]
    public void TheHotspotsAreTheFormsOwnIds()
    {
        Assert.Equal(MeleeIds, HostFormLayout.Hot(HostFormLayout.RowsOf(Melee)).Keys.Order());
        Assert.Equal(CoopIds, HostFormLayout.Hot(HostFormLayout.RowsOf(Coop)).Keys.Order());
    }

    [Fact]
    public void TheHotspotsLieOnTheSheetApart()
    {
        foreach (var s in new[] { Melee, Coop })
        {
            var hot = HostFormLayout.Hot(HostFormLayout.RowsOf(s)).Values.ToList();
            foreach (var (x0, x1, y0, y1) in hot)
            {
                Assert.True(0 <= x0 && x0 < x1 && x1 <= 640 && 0 <= y0 && y0 < y1 && y1 <= 480);
                Assert.True(x1 - x0 >= 18 && y1 - y0 >= 16, "big enough to hit");
            }
            // strict rectangles (x0 < x < x1) may share an edge, never inside room
            for (int i = 0; i < hot.Count; i++)
                for (int j = i + 1; j < hot.Count; j++)
                {
                    var (a, b) = (hot[i], hot[j]);
                    bool overlap = Math.Max(a.X0, b.X0) + 1 < Math.Min(a.X1, b.X1) && Math.Max(a.Y0, b.Y0) + 1 < Math.Min(a.Y1, b.Y1);
                    Assert.False(overlap, $"{a} and {b} overlap");
                }
        }
    }

    [Fact]
    public void TheValuesFollowTheSettings()
    {
        (string Label, Func<HostFormLayout.State, bool, HostFormLayout.State> Set)[] toggles =
        [
            ("Team Play", (s, v) => s with { TeamPlay = v }), ("Invite Only", (s, v) => s with { InviteOnly = v }),
            ("Share Maps", (s, v) => s with { ShareMaps = v }), ("Hidden Weapons & Cars", (s, v) => s with { Hidden = v }),
            ("Unlimited Turrets", (s, v) => s with { Turrets = v }), ("Car Reset", (s, v) => s with { CarReset = v }),
            ("CB Radio", (s, v) => s with { Cb = v }), ("Chase Crosshair", (s, v) => s with { Crosshair = v }),
            ("Ignition Lock", (s, v) => s with { IgnitionLock = v }),
        ];
        foreach (var (label, set) in toggles)
        {
            Assert.Equal("On", Value(HostFormLayout.RowsOf(set(Melee, true)), label));
            Assert.Equal("Off", Value(HostFormLayout.RowsOf(set(Melee, false)), label));
        }
        Assert.Equal("On - 35000 +", Value(HostFormLayout.RowsOf(Melee with { ScoreOn = true, MaxScore = 35000 }), "Max Score"));
        Assert.Equal("- 07 +", Value(HostFormLayout.RowsOf(Melee with { Drivers = 7 }), "No. Drivers"));
        Assert.Equal("- 03 +", Value(HostFormLayout.RowsOf(Melee with { Bots = 3 }), "Bots"));
        Assert.Equal("60 Hz", Value(HostFormLayout.RowsOf(Melee with { Rate = 60 }), "Sim Rate"));
        Assert.Equal("Set", Value(HostFormLayout.RowsOf(Melee with { Password = true }), "Password"));
        Assert.Equal("None", Value(HostFormLayout.RowsOf(Melee), "Password"));
        Assert.Equal("Co-op", Value(HostFormLayout.RowsOf(Coop), "Game Type"));
        Assert.Equal("x1", Value(HostFormLayout.RowsOf(Coop), "Co-op Damage"));
        Assert.Equal("x0.75", Value(HostFormLayout.RowsOf(Coop with { CoopDamage = "x0.75" }), "Co-op Damage"));
        Assert.Equal("Off", Value(HostFormLayout.RowsOf(Coop), "Co-op Scaling"));
        Assert.Equal("On", Value(HostFormLayout.RowsOf(Coop with { RollingStart = true }), "Rolling Start"));
        Assert.Equal("Melee", Value(HostFormLayout.RowsOf(Melee), "Game Type"));
        Assert.Equal("Bob_", Value(HostFormLayout.RowsOf(Melee with { GameName = "Bob", Naming = true }), "Game Name"));
    }

    [Fact]
    public void ThePadReachesEveryField()
    {
        foreach (var (name, s) in new[] { ("melee", Melee), ("co-op", Coop) })
        {
            var rows = HostFormLayout.RowsOf(s);
            var regions = HostFormLayout.PadOrder(HostFormLayout.Hot(rows), HostFormLayout.ListRows);
            var rects = regions.Select(r => new NavRect(r.Rect.X0, r.Rect.Y0, r.Rect.X1 - r.Rect.X0, r.Rect.Y1 - r.Rect.Y0)).ToList();
            string Name(int i)
            {
                var (id, listRow, (x0, x1, y0, y1)) = regions[i];
                if (listRow >= 0) return $"area list row {listRow}";
                var row = rows.FirstOrDefault(r => r.RowId == id || r.Cells.Any(c => c.Id == id));
                if (row is null) return HostFormLayout.SlotId.Contains(id) ? HostFormLayout.SlotText[Array.IndexOf(HostFormLayout.SlotId, id)] : id == 12 ? "list up" : id == 13 ? "list down" : $"id {id}";
                return row.Cells.Where(c => c.Id == id).Select(c => $"{row.Label} [{c.Text}]").FirstOrDefault() ?? row.Label;
            }
            int start = PadNav.Pick(rects, -1, NavDir.Down);
            var seen = new HashSet<int> { start };
            var queue = new Queue<int>([start]);
            while (queue.Count > 0)
            {
                int at = queue.Dequeue();
                foreach (var d in new[] { NavDir.Up, NavDir.Down, NavDir.Left, NavDir.Right })
                    if (PadNav.Pick(rects, at, d) is >= 0 and var to && seen.Add(to)) queue.Enqueue(to);
            }
            Assert.True(seen.Count == regions.Count, $"{name}: unreachable {string.Join(", ", Enumerable.Range(0, regions.Count).Where(i => !seen.Contains(i)).Select(Name))}");
            // the walk a player makes: down each table from its top, then across to the buttons
            output.WriteLine($"{name}: the pad starts on {Name(start)}");
            foreach (int top in new[] { start, PadNav.Pick(rects, start, NavDir.Right) is var r && r >= 0 ? r : start })
            {
                var walk = new List<string>();
                for (int at = top, n = 0; at >= 0 && n < 40; at = PadNav.Pick(rects, at, NavDir.Down), n++) walk.Add(Name(at));
                output.WriteLine($"  down from {Name(top)}: {string.Join(" > ", walk)}");
            }
        }
    }
}
