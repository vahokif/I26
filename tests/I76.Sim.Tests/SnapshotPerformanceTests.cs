using System.Diagnostics;
using I76.Sim.Save;
using I76.Sim.Session;
using Xunit;
using Xunit.Abstractions;

namespace I76.Sim.Tests;

/// <summary>What a snapshot of a big mission costs: its size, the save and the restore after the mission build.</summary>
public class SnapshotPerformanceTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Load", "Sensitive")]   // wall-clock timing: flakes on a busy machine; feature runs skip it (--filter "Load!=Sensitive"), a release check runs it
    public void ABigMissionSnapshotsAndRestoresWellUnderASecond()
    {
        if (SnapshotRig.Install is not { } dir) return;
        var l = new SnapshotRig.Loaded(dir);
        // T16 at 28 Hz, one of the heaviest base missions to run: at 60 s, and at the first tick an AI's A* search is well under
        // way (the largest snapshots: every node of the search is in it)
        var b = SnapshotRig.BuildOf(dir, "t16");
        var (s, _) = SnapshotRig.Make(l, b, SimRate.R28, 1);
        var shots = new List<(string What, byte[] Snap, double SaveMs)>();
        var sw = new Stopwatch();
        for (int i = 0; i < SimRate.R28.TicksFor(60f); i++)
        {
            SnapshotRig.Tick(s);
            if (shots.Count == 0 && s.Script!.Host.Entities.Any(e => e.Search is { Expansions: > 1000 }))
            {
                sw.Restart();
                var snap = s.Snapshot();
                shots.Add(("mid-search", snap, sw.Elapsed.TotalMilliseconds));
            }
            SnapshotRig.TakeEvents(s);
        }
        sw.Restart();
        var at60 = s.Snapshot();
        shots.Add(("at 60 s", at60, sw.Elapsed.TotalMilliseconds));
        sw.Restart();
        for (int i = 0; i < 20; i++) s.Snapshot();
        output.WriteLine($"t16 at 60 s: a warm save takes {sw.Elapsed.TotalMilliseconds / 20:0.00} ms");
        foreach (var (what, snap, save) in shots)
        {
            sw.Restart();
            var (fresh, _) = SnapshotRig.Make(l, b, SimRate.R28, 7);
            double build = sw.Elapsed.TotalMilliseconds;
            sw.Restart();
            fresh.Restore(snap);
            double restore = sw.Elapsed.TotalMilliseconds;
            int body = SnapshotHeader.Read(snap).Body(snap).Length;
            output.WriteLine($"t16 {what}: {snap.Length} bytes ({body} uncompressed), saved in {save:0.0} ms; mission build {build:0} ms, then the restore {restore:0.0} ms");
            Assert.True(restore < 500, $"restore took {restore:0} ms");
        }
    }
}
