using System.Numerics;
using I76.Game.Boot;
using I76.Game.Shell;
using I76.Sim.Rng;
using I76.Sim.Save;
using I76.Sim.Session;
using I76.Sim.Terrain;
using I76.Sim;
using Xunit;

namespace I76.Game.Tests;

/// <summary>
/// Checkpoint bookmarks: the escape menu's Save Bookmark in a trip mission writes a new Load Bookmark entry with the checkpoint beside
/// it, never the trip's own state; loading one resumes at the checkpoint, or starts the mission from its beginning with a word to the
/// player when the checkpoint cannot be used (another version's), without throwing.
/// </summary>
public class CheckpointBookmarkTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("cpbookmarks").FullName;
    public void Dispose() => Directory.Delete(_dir, true);

    private sealed class Flat : IGround
    {
        public (float Height, Vector3 Normal) Query(double x, double y, double z) => (0, Vector3.UnitY);
        public int SurfaceId(double x, double z) => 0;
        public Surface Surface(int id) => new(2f, 0.03f, 0, 0, 3);
    }

    private static SimSession Session(string mission = "t05")
    {
        var world = new SimWorld(new Flat(), new CrtRand(7));
        return new SimSession(world, new I76.Sim.Weapons.Combat(world, _ => null), null, SessionOptions.SinglePlayer)
        {
            Build = new SessionBuild { Mission = mission, Install = 0xabc, Car = "vehscn.vcf" },
        };
    }

    private static Checkpoint Kept(string mission = "t05")
    {
        var s = Session(mission);
        for (int i = 0; i < 12; i++) s.Tick([]);
        return new Checkpoint(mission, s.Snapshot(), [9, 8, 7], 42.5, [1.5f, -2f, 3.25f], true, 3, "objective 2", s.Ticks);
    }

    [Fact]
    public void ACheckpointRoundTripsThroughItsBookmarkFileAndItsSnapshotStillRestores()
    {
        var cp = Kept();
        var back = Checkpoint.Read(cp.ToBytes(), out var problem);
        Assert.Equal(Checkpoint.Problem.None, problem);
        Assert.NotNull(back);
        Assert.Equal((cp.Mission, cp.Why, cp.Tick, cp.Cockpit, cp.LastPreset, cp.MusicAt), (back.Mission, back.Why, back.Tick, back.Cockpit, back.LastPreset, back.MusicAt));
        Assert.Equal(cp.Snapshot, back.Snapshot);
        Assert.Equal(cp.Salvage, back.Salvage);
        Assert.Equal(cp.Chase, back.Chase);
        Session().Restore(back.Snapshot);   // the mission built again takes it

        var bare = cp with { Salvage = null, MusicAt = null, Chase = null };
        var b2 = Checkpoint.Read(bare.ToBytes(), out _)!;
        Assert.Null(b2.Salvage);
        Assert.Null(b2.MusicAt);
        Assert.Null(b2.Chase);
    }

    [Fact]
    public void ALaunchFromTheBookmarkResumesAtItsCheckpoint()
    {
        var cp = Kept();
        var back = Checkpoint.ForLaunch("T05", cp.ToBytes(), out var notice);
        Assert.NotNull(back);
        Assert.Null(notice);
        Assert.Equal(cp.Tick, back.Tick);
    }

    [Fact]
    public void ASnapshotOfAnotherVersionStartsTheMissionFromItsBeginningWithANoticeAndNoThrow()
    {
        var file = Kept().ToBytes();
        // the snapshot's own format version, as another build of the game would have written it
        int at = file.AsSpan().IndexOf(SnapshotHeader.Magic) + SnapshotHeader.Magic.Length;
        Assert.True(at > SnapshotHeader.Magic.Length);
        file[at] = (byte)((SnapshotHeader.CurrentVersion + 1) << 1);   // zigzagged
        Assert.ThrowsAny<SnapshotFormatException>(() => SnapshotHeader.Read(file[(at - SnapshotHeader.Magic.Length)..]));   // what the build would meet

        Assert.Null(Checkpoint.Read(file, out var problem));
        Assert.Equal(Checkpoint.Problem.OtherVersion, problem);
        Assert.Null(Checkpoint.ForLaunch("t05", file, out var notice));   // no retry: the mission starts from its beginning
        Assert.Equal(Checkpoint.Notice(Checkpoint.Problem.OtherVersion), notice);
        Assert.Contains("another version", notice);
    }

    [Fact]
    public void ADamagedOrForeignFileIsNeverThrownButStartsTheMissionOver()
    {
        var file = Kept().ToBytes();
        for (int n = 0; n < file.Length; n += 7)
        {
            Assert.Null(Checkpoint.ForLaunch("t05", file[..n], out var notice));
            Assert.NotNull(notice);
        }
        Assert.Null(Checkpoint.ForLaunch("t05", null, out _));
        Assert.Null(Checkpoint.ForLaunch("t05", [1, 2, 3], out _));
        var other = Kept("t06").ToBytes();
        Assert.Null(Checkpoint.ForLaunch("t05", other, out var wrong));   // another mission's
        Assert.NotNull(wrong);
        var flipped = (byte[])file.Clone();
        for (int i = 0; i < flipped.Length; i += 3) flipped[i] ^= 0x5a;
        Checkpoint.ForLaunch("t05", flipped, out _);   // whatever it makes of it, it does not throw
    }

    [Theory]
    [InlineData("t01", 1)]
    [InlineData("T05", 5)]
    [InlineData("t17", 17)]
    [InlineData("a01", null)]   // training
    [InlineData("b01", null)]   // training's other launch
    [InlineData("t00", null)]
    [InlineData("t18", null)]
    [InlineData("t5", null)]
    [InlineData("t05x", null)]
    [InlineData("m03", null)]   // a melee arena
    [InlineData("n21", null)]   // Nitro: no trip
    [InlineData("p01", null)]   // a scenario
    [InlineData(null, null)]
    public void OnlyTripMissionsHaveSaveBookmark(string? mission, int? trip) => Assert.Equal(trip, CheckpointBookmarks.TripMission(mission));

    private void Write(string stem, string json) => File.WriteAllText(Path.Combine(_dir, $"bookmark_{stem}.json"), json);
    private void Write(string stem, byte[] data) => File.WriteAllBytes(Path.Combine(_dir, $"bookmark_{stem}.snap"), data);

    [Fact]
    public void SavingAddsANewEntryWithTheLaunchsTripAndLeavesTheTripAlone()
    {
        File.WriteAllText(Path.Combine(_dir, "trip.cfg"), "[trip]\nmission=5\nhighest=9\n");
        File.WriteAllText(Path.Combine(_dir, "trip_car.json"), "the trip's car now");
        var marks = new Bookmarks();
        marks.Entries.Add(new Bookmarks.Entry(3, "mine", "save000", 0, false));
        marks.Entries.Add(new Bookmarks.Entry(5, CheckpointBookmarks.Name, "save004", 0, false, Checkpoint: true));
        var start = new TripStart(5, "the car at the mission's start");
        var file = Kept().ToBytes();

        var e = CheckpointBookmarks.Add(marks, start, true, file, Write, Write);
        var e2 = CheckpointBookmarks.Add(marks, start, true, file, Write, Write);

        // always a new row, after the largest stem, listed "Scene 5. Checkpoint"
        Assert.Equal(("save005", "save006"), (e.Stem, e2.Stem));
        Assert.Equal((5, 5, "Checkpoint", 0, true, true), (e.Mission, e.Scene, e.Name, e.State, e.NoSalvage, e.Checkpoint));
        Assert.Equal(4, marks.Entries.Count);
        Assert.Equal(["save000", "save004", "save005", "save006"], marks.Entries.Select(x => x.Stem));
        Assert.Equal("the car at the mission's start", File.ReadAllText(Path.Combine(_dir, "bookmark_save005.json")));
        Assert.Equal(file, File.ReadAllBytes(Path.Combine(_dir, "bookmark_save005.snap")));
        // the trip's own state is as it was, and nothing else was written
        Assert.Equal("[trip]\nmission=5\nhighest=9\n", File.ReadAllText(Path.Combine(_dir, "trip.cfg")));
        Assert.Equal("the trip's car now", File.ReadAllText(Path.Combine(_dir, "trip_car.json")));
        Assert.Equal(["bookmark_save005.json", "bookmark_save005.snap", "bookmark_save006.json", "bookmark_save006.snap", "trip.cfg", "trip_car.json"],
            Directory.GetFiles(_dir).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }
}
