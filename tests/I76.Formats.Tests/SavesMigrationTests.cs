using I76.Formats;
using Xunit;

namespace I76.Formats.Tests;

public sealed class SavesMigrationTests : IDisposable
{
    private readonly string _u = Directory.CreateTempSubdirectory("i26saves").FullName;
    public void Dispose() => Directory.Delete(_u, true);
    private string P(params string[] p) => Path.Combine([_u, .. p]);
    private void W(string text, params string[] p) { Directory.CreateDirectory(Path.GetDirectoryName(P(p))!); File.WriteAllText(P(p), text); }

    [Fact]
    public void Old_saves_move_and_unrelated_files_stay()
    {
        W("b", "bookmarks.cfg"); W("c0", "bookmark_save000.json"); W("c1", "bookmark_save001.json"); W("sn1", "bookmark_save001.snap"); W("t", "trip.cfg"); W("tc", "trip_car.json");
        W("s", "nitro", "scenario.dat"); W("m", "nitro", "nituser.mel"); W("x", "shell.cfg"); W("i", "input.map"); W("l", "melee.cfg");
        var log = new List<string>();
        Assert.Equal(7, SavesMigration.Run(_u, log.Add));
        Assert.Equal(7, log.Count);
        Assert.Equal("b", File.ReadAllText(P("saves", "bookmarks.cfg")));
        Assert.Equal("c1", File.ReadAllText(P("saves", "bookmark_save001.json")));
        Assert.Equal("sn1", File.ReadAllText(P("saves", "bookmark_save001.snap")));
        Assert.Equal("s", File.ReadAllText(P("saves", "nitro", "scenario.dat")));
        foreach (var gone in new[] { "bookmarks.cfg", "bookmark_save000.json", "bookmark_save001.snap", "trip.cfg", "trip_car.json" }) Assert.False(File.Exists(P(gone)));
        Assert.False(File.Exists(P("nitro", "scenario.dat")));
        foreach (var kept in new[] { "shell.cfg", "input.map", "melee.cfg" }) Assert.True(File.Exists(P(kept)));
        Assert.True(File.Exists(P("nitro", "nituser.mel")));
        Assert.Empty(Directory.GetFiles(P("saves"), "*.moving", SearchOption.AllDirectories));
    }

    [Fact]
    public void A_clash_keeps_both_and_never_overwrites()
    {
        W("old", "bookmarks.cfg"); W("new", "saves", "bookmarks.cfg");
        W("same", "trip.cfg"); W("same", "saves", "trip.cfg");
        SavesMigration.Run(_u);
        Assert.Equal("new", File.ReadAllText(P("saves", "bookmarks.cfg")));
        Assert.Equal("old", File.ReadAllText(P("saves", "bookmarks-old.cfg")));
        Assert.False(File.Exists(P("bookmarks.cfg")));
        Assert.Equal("same", File.ReadAllText(P("saves", "trip.cfg")));
        Assert.False(File.Exists(P("trip.cfg")));
        Assert.False(File.Exists(P("saves", "trip-old.cfg")));
    }

    [Fact]
    public void A_second_run_does_nothing_and_a_missing_folder_is_fine()
    {
        W("b", "bookmarks.cfg");
        Assert.Equal(1, SavesMigration.Run(_u));
        Assert.Equal(0, SavesMigration.Run(_u));
        Assert.Equal("b", File.ReadAllText(P("saves", "bookmarks.cfg")));
        Assert.Equal(0, SavesMigration.Run(P("nope")));
    }

    [Fact]
    public void A_run_cut_off_after_the_copy_finishes_next_time()
    {
        W("b", "bookmarks.cfg"); W("b", "saves", "bookmarks.cfg");   // copied, original not yet removed
        Assert.Equal(1, SavesMigration.Run(_u));
        Assert.False(File.Exists(P("bookmarks.cfg")));
        Assert.Equal("b", File.ReadAllText(P("saves", "bookmarks.cfg")));
    }
}
