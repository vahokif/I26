using I76.Game.World;
using Xunit;

namespace I76.Game.Tests;

/// <summary>The outside ground's chunk-job count (<see cref="ChunkJobs"/>).</summary>
public class ChunkJobsTests
{
    [Fact]
    public void JobsLeftRunningByAClearStillTakeTheirPlacesUntilTheyFinish()
    {
        var jobs = new ChunkJobs(4);
        var running = Enumerable.Range(0, 4).Select(_ => new TaskCompletionSource<int>()).ToList();
        foreach (var _ in running) jobs.Started();
        Assert.Equal(0, jobs.Free);

        // a look toggle clears the chunks: their jobs run on, so no new ones may start beside them (the count went to 0 and 4 more started)
        foreach (var t in running) jobs.Orphan(t.Task);
        Assert.Equal(4, jobs.Busy);
        Assert.Equal(0, jobs.Free);

        running[0].SetResult(0); running[1].SetResult(0);
        Assert.Equal(2, jobs.Free);
        jobs.Started();
        Assert.Equal(1, jobs.Free);
        running[2].SetResult(0); running[3].SetResult(0);
        Assert.Equal(1, jobs.Busy);
        jobs.Finished();
        Assert.Equal(4, jobs.Free);
    }

    [Fact]
    public void AJobAlreadyFinishedWhenItIsLeftFreesItsPlace()
    {
        var jobs = new ChunkJobs(4);
        jobs.Started();
        jobs.Orphan(Task.CompletedTask);
        Assert.Equal(0, jobs.Busy);
        Assert.Equal(4, jobs.Free);
    }
}
