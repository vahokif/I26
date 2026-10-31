using System.Numerics;
using I76.Sim.Missions;
using I76.Sim.Session;
using I76.Sim.Vehicles;
using Xunit;
using Xunit.Abstractions;

namespace I76.Sim.Tests;

/// <summary>Co-op's formation keeps out of the script's cars' way (docs/multiplayer.md "Co-op").</summary>
public class CoopFormationTests(ITestOutputHelper output)
{
    private static readonly (bool Nitro, string Mission)[] Starts =
        [(false, "t01"), (false, "t02"), (false, "t03"), (false, "t05"), (false, "t06"), (false, "s01"), (true, "p01"), (true, "p03"), (true, "p06")];

    /// <summary>
    /// The opening minute of each with 2, 4 and 8 humans, their keys at rest (the intro drives the lead): no AI car drives
    /// into a seat other than the lead's (the lead's place is the single player's) in a cut-scene, nor, at any time, a
    /// car of the player's side. (Before the formation kept off the cars' ways, T01's Taurus and P06's wingman ran into seat 1.)
    /// </summary>
    [Fact]
    public void NoAiDrivesIntoAFormedUpSeatInTheOpeningMinute()
    {
        var jobs = new List<(string Dir, string Mission, int Humans)>();
        foreach (var (nitro, m) in Starts)
            if ((nitro ? SnapshotRig.NitroInstall : SnapshotRig.Install) is { } dir)
                foreach (int n in (int[])[2, 4, 8]) jobs.Add((dir, m, n));
        if (jobs.Count == 0) return;
        var failures = new System.Collections.Concurrent.ConcurrentBag<string>();
        var lines = new System.Collections.Concurrent.ConcurrentBag<string>();
        Parallel.ForEach(jobs, new ParallelOptions { MaxDegreeOfParallelism = System.Math.Clamp(Environment.ProcessorCount / 2, 1, 12) }, job =>
        {
            var l = new SnapshotRig.Loaded(job.Dir);
            var (s, setup) = SnapshotRig.Make(l, SnapshotRig.BuildOf(job.Dir, job.Mission, coop: CoopSetup.Of(job.Humans)), SimRate.R20, 1);
            var host = setup.Script!.Host;
            var hits = new List<string>();
            setup.Damage!.Recorded += (victim, attacker, kind, _) =>
            {
                if (kind != Damage.DamageKind.Collision) return;
                int seat = setup.Humans.ToList().IndexOf(victim);
                if (seat <= 0 || setup.Humans.Contains(attacker)) return;
                var e = host.Of(attacker);
                if (!host.View.Active && e is { Team: not 1 }) return;   // an enemy's (or a civilian's) ram in play is the game's, not the formation's
                hits.Add($"{job.Mission}x{job.Humans}: tick {s.Ticks} {(host.View.Active ? "cut-scene" : "play")}: {e?.Name ?? attacker.Definition} (team {e?.Team}) into seat {seat}");
            };
            var start = string.Join("; ", setup.Humans.Select(h => $"{h.Pos.X:0},{h.Pos.Z:0}"));
            int ticks = s.Rate.TicksFor(60f);
            for (int i = 0; i < ticks && setup.Script.Result is null; i++) { s.Tick(new CarInput[job.Humans]); SnapshotRig.TakeEvents(s); }
            lines.Add($"{job.Mission}x{job.Humans}: seats at {start}; {hits.Count} hits");
            foreach (var h in hits.Distinct().Take(3)) failures.Add(h);
        });
        foreach (var x in lines.Order()) output.WriteLine(x);
        Assert.True(failures.IsEmpty, string.Join("\n", failures.Order()));
    }

    /// <summary>
    /// T01 with two humans, seat 1 put in Taurus's way as the intro starts: he drives into it. The ram is Taurus's
    /// (seat 1's rammer is him), not seat 1's: his rammer is not seat 1, so the friendly-fire routine (data 12) counts
    /// nothing and he says no "watch it". The intro's follow is at the lead, as in single player.
    /// </summary>
    [Fact]
    public void AnAiDrivingIntoAHumanIsNotThatHumansRam()
    {
        if (SnapshotRig.Install is not { } dir) return;
        var l = new SnapshotRig.Loaded(dir);
        var (s, setup) = SnapshotRig.Make(l, SnapshotRig.BuildOf(dir, "t01", coop: CoopSetup.Of(2)), SimRate.R20, 1);
        var host = setup.Script!.Host;
        var taurus = setup.World.Vehicles.Single(v => host.Of(v)?.Name == "t01js01");
        var te = host.Of(taurus)!;
        var seat = setup.Humans[1];
        var se = host.Of(seat)!;
        var at = taurus.Pos.ToVector3() + taurus.Rot.Forward * 14f;
        at.Y = setup.World.Ground.Query(at.X, at.Y + 2, at.Z).Height;
        seat.Pos = new Math.Double3(at.X, at.Y, at.Z);
        seat.Rot = taurus.Rot;
        var cb = new List<string>();
        host.Cb.Started += line => cb.Add(line.Name);
        bool rammed = false;
        for (int i = 0; i < 600; i++)
        {
            s.Tick(new CarInput[2]); SnapshotRig.TakeEvents(s);
            rammed |= se.LastRammer == te;
            Assert.NotEqual(se, te.LastRammer);
        }
        Assert.True(rammed, "Taurus drove into seat 1");
        Assert.Equal(0, setup.Script.Vm!.Data[12]);
        Assert.DoesNotContain(cb, x => x.StartsWith("gdsta05", StringComparison.OrdinalIgnoreCase) || x.StartsWith("gdsta06", StringComparison.OrdinalIgnoreCase));
        output.WriteLine($"the radio: {string.Join(" ", cb)}");
    }

    /// <summary>
    /// T01's opening cut-scene with 2 and 4 humans plays as single player's: the script has Taurus follow the player,
    /// which in a cut-scene is the lead it drives (not the nearest human, a teammate held at the start), so he drives
    /// down the road beside and ahead of the lead as in single player, where a client's view of the scene shows him.
    /// </summary>
    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public void T01sIntroDrivesTaurusAsInSinglePlayer(int humans)
    {
        if (SnapshotRig.Install is not { } dir) return;
        var l = new SnapshotRig.Loaded(dir);
        List<Vector3> Run(CoopSetup? coop, int n)
        {
            var (s, setup) = SnapshotRig.Make(l, SnapshotRig.BuildOf(dir, "t01", coop: coop), SimRate.R20, 1);
            var host = setup.Script!.Host;
            var taurus = setup.World.Vehicles.Single(v => host.Of(v)?.Name == "t01js01");
            var track = new List<Vector3>();
            for (int i = 0; i < 500; i++)
            {
                s.Tick(new CarInput[n]); SnapshotRig.TakeEvents(s);
                if (i % 20 == 0) { track.Add(taurus.Pos.ToVector3()); track.Add(setup.Humans[0].Pos.ToVector3()); }
            }
            Assert.True(host.View.Active, "still in the intro");
            return track;
        }
        var sp = Run(null, 1);
        var co = Run(CoopSetup.Of(humans), humans);
        for (int k = 0; k < sp.Count; k++)
            Assert.True(Vector3.Distance(sp[k], co[k]) < 0.5f, $"tick {k / 2 * 20}: {(k % 2 == 0 ? "Taurus" : "the lead")} at {co[k]}, in single player {sp[k]}");
        output.WriteLine($"Taurus at tick 480: {co[^2]}, the lead {co[^1]}");
    }

    /// <summary>
    /// Eight humans on every stock co-op start: the cars stand apart (no two bodies overlap), none inside a structure or on
    /// impassable ground, at the start and again after the formation re-forms behind a moved lead.
    /// </summary>
    [Fact]
    public void EightHumansStandApartAndClearOfObjects()
    {
        var jobs = new List<(string Dir, string Mission)>();
        foreach (var (nitro, m) in Starts)
            if ((nitro ? SnapshotRig.NitroInstall : SnapshotRig.Install) is { } dir) jobs.Add((dir, m));
        if (jobs.Count == 0) return;
        var failures = new System.Collections.Concurrent.ConcurrentBag<string>();
        Parallel.ForEach(jobs, new ParallelOptions { MaxDegreeOfParallelism = System.Math.Clamp(Environment.ProcessorCount / 2, 1, 12) }, job =>
        {
            var l = new SnapshotRig.Loaded(job.Dir);
            var (s, setup) = SnapshotRig.Make(l, SnapshotRig.BuildOf(job.Dir, job.Mission, coop: CoopSetup.Of(8)), SimRate.R20, 1);
            var host = setup.Script!.Host;
            void Check(string when)
            {
                var hs = setup.Humans.ToList();
                for (int i = 0; i < hs.Count; i++)
                {
                    var a = hs[i];
                    if (SpawnSpace.InsideStructure(setup.World, a.Pos.ToVector3(), SpawnSpace.Radius(a)) || setup.World.Ground.Impassable((float)a.Pos.X, (float)a.Pos.Z))
                        failures.Add($"{job.Mission} {when}: seat {i} inside an object or off the ground at {a.Pos.X:0},{a.Pos.Z:0}");
                    for (int j = i + 1; j < hs.Count; j++)
                    {
                        var d = hs[j].Pos.ToVector3() - a.Pos.ToVector3();
                        float flat = System.MathF.Sqrt(d.X * d.X + d.Z * d.Z);
                        if (flat < SpawnSpace.Radius(a) + SpawnSpace.Radius(hs[j]) - 1f)
                            failures.Add($"{job.Mission} {when}: seats {i} and {j} overlap ({flat:0.0} m apart)");
                    }
                }
                Assert.Equal(8, hs.Count);
            }
            Check("at the start");
            var lead = setup.Humans[0];
            var to = lead.Pos.ToVector3() + lead.Rot.Forward * 60f;
            lead.Pos = new Math.Double3(to.X, setup.World.Ground.Query(to.X, to.Y + 2, to.Z).Height, to.Z);
            host.FormUp(0);
            Check("after a form-up");
            output.WriteLine($"{job.Mission}: 8 seats " + string.Join("; ", setup.Humans.Select(h => $"{h.Pos.X:0},{h.Pos.Z:0}")));
        });
        Assert.True(failures.IsEmpty, string.Join("\n", failures.Order()));
    }
}
