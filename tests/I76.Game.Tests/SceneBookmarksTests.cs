using I76.Formats.Tests;
using I76.Game.Shell;
using I76.Sim.Trip;
using Xunit;

namespace I76.Game.Tests;

/// <summary>All Scenes: one bookmark per trip mission, in that mission's own car, never over an existing bookmark.</summary>
public class SceneBookmarksTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("scenes").FullName;
    public void Dispose() => Directory.Delete(_dir, true);

    private (int Made, int Had) Run(Bookmarks marks) =>
        SceneBookmarks.Generate(marks, n => $"Title {n}", n => $"car {n}", (stem, json) => File.WriteAllText(Path.Combine(_dir, $"bookmark_{stem}.json"), json));

    [Fact] public void MakesSeventeenInSceneOrderEachWithItsOwnCar()
    {
        var marks = new Bookmarks();
        Assert.Equal((17, 0), Run(marks));
        Assert.Equal(17, marks.Entries.Count);
        for (int n = 1; n <= 17; n++)
        {
            var e = marks.Entries[n - 1];
            Assert.Equal($"save{n - 1:000}", e.Stem);
            Assert.Equal((n, $"Title {n}", 0, false), (e.Scene, e.Name, e.State, e.NoSalvage));
            Assert.Equal($"car {n}", File.ReadAllText(Path.Combine(_dir, $"bookmark_{e.Stem}.json")));
        }
    }

    [Fact] public void RunningItAgainMakesNothing()
    {
        var marks = new Bookmarks();
        Run(marks);
        Assert.Equal((0, 17), Run(marks));
        Assert.Equal(17, marks.Entries.Count);
        Assert.Equal(17, Directory.GetFiles(_dir).Length);
    }

    [Fact] public void AnExistingBookmarkOfTheSameSceneAndNameIsLeftAlone()
    {
        var marks = new Bookmarks();
        marks.Entries.Add(new Bookmarks.Entry(3, "Title 3", "save000", 0, true));
        marks.Entries.Add(new Bookmarks.Entry(2, "Title 3", "save007", 0, false));      // another scene's: not the same bookmark
        marks.Entries.Add(new Bookmarks.Entry(4, "My own", "save002", 0, false));
        marks.Entries.Add(new Bookmarks.Entry(6, "after a win", "save003", 1, false)); // scene 7
        var (made, had) = Run(marks);
        Assert.Equal((16, 1), (made, had));
        var mine = marks.Entries.Single(e => e.Stem == "save000");
        Assert.True(mine.NoSalvage);
        Assert.False(File.Exists(Path.Combine(_dir, "bookmark_save000.json")));     // its car is not touched
        Assert.Equal(20, marks.Entries.Count);
        Assert.Equal(marks.Entries.Count, marks.Entries.Select(e => e.Stem).Distinct().Count());
        Assert.Equal("save008", marks.Entries.Where(e => e.Name == "Title 1").Single().Stem);   // after the largest stem there was
    }

    [InstallFact] public void TheInstallHasEachScenesCarAndTitle()
    {
        var vfs = TestInstall.Vfs;
        var names = new HashSet<string>();
        for (int n = 1; n <= SceneBookmarks.Count; n++)
        {
            var car = TripCar.FromVcf(vfs.Read(SceneBookmarks.CarFile(n)), c => I76.Sim.Vehicles.VehicleFactory.EngineCompId(vfs, c));
            Assert.NotNull(TripInventory.FromCar(car, PartsDatabase.Build(vfs), vfs).Save());
            var name = SceneBookmarks.NameOf(TestInstall.Install, n);
            Assert.False(string.IsNullOrWhiteSpace(name));
            Assert.DoesNotContain(". ", name[..Math.Min(4, name.Length)]);
            names.Add(name);
        }
        Assert.Equal(17, names.Count);
    }
}
