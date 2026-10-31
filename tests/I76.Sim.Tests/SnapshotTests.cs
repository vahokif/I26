using System.Collections.Concurrent;
using I76.Sim.Missions;
using I76.Sim.Save;
using I76.Sim.Session;
using Xunit;
using Xunit.Abstractions;

namespace I76.Sim.Tests;

/// <summary>
/// The snapshot's equivalence over the corpus (docs/savestate.md): every mission of the install (and of the Nitro
/// Pack's with I76_NITRO_INSTALL), at 20 and 28 Hz, snapshotted at 10 s (most are in their opening cut-scene),
/// on the first skip-key tick, at 60 s and at the moments <see cref="SnapshotEquivalence.Moments"/> looks for;
/// melees with their waves and network players; combat with flames, mines, drops and guided rounds.
/// </summary>
public class SnapshotCorpusTests(ITestOutputHelper output)
{
    private static int Parallelism => System.Math.Clamp(Environment.ProcessorCount / 3, 1, 8);

    private sealed record Job(string Label, string Dir, SessionBuild Build, SimRate Rate, int[] Ticks, IReadOnlyList<(string Car, int Team)>? Players = null,
        Action<SimSession>? Prepare = null, bool Hotkeys = true, Func<int, int, Session.CarCommands>? Extra = null);

    private void RunAll(IEnumerable<Job> jobs, SnapshotCoverage cov, string what)
    {
        var failures = new ConcurrentBag<string>();
        var notes = new ConcurrentBag<string>();
        var stats = new ConcurrentBag<(string Label, SnapshotEquivalence.Report R)>();
        var list = jobs.ToList();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Parallel.ForEach(list, new ParallelOptions { MaxDegreeOfParallelism = Parallelism }, job =>
        {
            try
            {
                var l = new SnapshotRig.Loaded(job.Dir);
                int m = job.Rate.TicksFor(30f);
                var rep = SnapshotEquivalence.Run(l, job.Build, job.Rate, job.Ticks, m, cov, SnapshotEquivalence.Moments(), job.Prepare, job.Players, job.Hotkeys, job.Label, job.Extra);
                foreach (var f in rep.Failures) failures.Add(f);
                foreach (var n in rep.Notes) notes.Add(n);
                stats.Add((job.Label, rep));
            }
            catch (Exception ex) { failures.Add($"{job.Label}: {ex}"); }
        });
        var all = stats.ToList();
        output.WriteLine($"{what}: {list.Count} runs, {all.Sum(x => x.R.Snapshots)} snapshots in {sw.Elapsed.TotalSeconds:0.0} s");
        if (all.Count > 0)
        {
            var big = all.MaxBy(x => x.R.MaxBytes);
            output.WriteLine($"largest snapshot {big.R.MaxBytes} bytes ({big.R.MaxBody} uncompressed, {big.Label}); slowest save {all.Max(x => x.R.MaxSaveMs):0.0} ms, " +
                $"restore {all.Max(x => x.R.MaxRestoreMs):0.0} ms, mission build {all.Max(x => x.R.MaxBuildMs):0} ms");
        }
        output.WriteLine("caught: " + string.Join(", ", cov.Seen.Order()));
        foreach (var n in notes.Order()) output.WriteLine(n);
        Assert.True(failures.IsEmpty, $"{failures.Count} failures:\n\n" + string.Join("\n\n", failures.Order().Take(12)));
    }

    private static IEnumerable<Job> Missions(string dir, string tag)
    {
        foreach (var mission in SnapshotRig.Missions(dir))
            foreach (var rate in new[] { SimRate.R20, SimRate.R28 })
                // SkipFrom + 1: the snapshot just after the tick that pressed the skip key, so it holds the latch
                yield return new Job($"{tag}{mission}@{rate.Label}", dir, SnapshotRig.BuildOf(dir, mission), rate, [rate.TicksFor(10f), SnapshotRig.SkipFrom + 1, rate.TicksFor(60f)]);
    }

    [Fact]
    public void EveryMissionRestoresExactly()
    {
        if (SnapshotRig.Install is not { } dir) return;
        var cov = new SnapshotCoverage();
        // the typed cheats and the self-destruct on two of them: getdown, the flat tyres, the player's death
        CarCommands Cheats(int t, int slot) => t switch { 450 => CarCommands.CheatGetDown, 520 => CarCommands.CheatFlatFR, 560 => CarCommands.CheatFlatRL, 1500 => CarCommands.SelfDestruct, _ => 0 };
        var jobs = Missions(dir, "").Concat(new[] { "t05", "s06" }.Select(m => new Job($"cheats {m}", dir, SnapshotRig.BuildOf(dir, m), SimRate.R20, [455, 600, 1510], Extra: Cheats)));
        RunAll(jobs, cov, "base missions");
        foreach (var need in new[] { "cutscene", "skip latch", "gate mid-slide", "objective done", "rounds in flight", "dying car", "wreck", "path search in progress",
                     "CB line playing", "structure destroyed", "helicopter", "emplacement", "radar target", "20 Hz", "28 Hz", "getdown", "player dead", "engine off",
                     "arm raised", "tyre blown" })
            Assert.Contains(need, cov.Seen);
    }

    [Fact]
    public void EveryNitroMissionRestoresExactly()
    {
        if (SnapshotRig.NitroInstall is not { } dir) return;
        var cov = new SnapshotCoverage();
        RunAll(Missions(dir, "nitro "), cov, "Nitro missions");
        Assert.Contains("cutscene", cov.Seen);
    }

    /// <summary>
    /// The single-player melee: the roster killed off (the debug hook's kill-ai) so the next waves come, whose cars
    /// a rebuilt mission does not have until the restore makes them; and combat cars carrying flames, mines, drops
    /// and guided missiles. A network melee whose players join the original: the rebuilt one has none until the
    /// restore makes their cars, entities and drivers.
    /// </summary>
    [Fact]
    public void MeleesAndCombatRestoreExactly()
    {
        if (SnapshotRig.Install is not { } dir) return;
        var cov = new SnapshotCoverage();
        string[] fighters = ["s05vdl02.vcf", "s02vcm01.vcf", "s03cg01.vcf", "s01vd01.vcf", "s05vjs01.vcf", "s01fp01.vcf"];
        var kills = string.Join(";", Enumerable.Range(0, 12).Select(i => $"{60 + 60 * i},kill-ai"));
        var jobs = new List<Job>();
        foreach (var (arena, player) in new[] { ("m01", "s05vpp01.vcf"), ("m03", "s05vdl02.vcf"), ("m07", "s04vcv01.vcf") })
            foreach (var rate in new[] { SimRate.R20, SimRate.R28 })
            {
                jobs.Add(new Job($"melee {arena}@{rate.Label}", dir, SnapshotRig.BuildOf(dir, arena, player, melee: fighters.Take(3).ToList()), rate, [100, 400, 900, 1500]));
                jobs.Add(new Job($"melee waves {arena}@{rate.Label}", dir, SnapshotRig.BuildOf(dir, arena, player, melee: fighters.Skip(3).ToList()), rate, [300, 700, 1300],
                    Prepare: s => s.DebugBoom = kills));
            }
        // the player blows itself up: the melee is over five seconds later
        jobs.Add(new Job("melee self-destruct m01", dir, SnapshotRig.BuildOf(dir, "m01", "s05vpp01.vcf", melee: fighters.Take(2).ToList()), SimRate.R20, [880, 905, 1000],
            Extra: (t, _) => t == 900 ? CarCommands.SelfDestruct : 0));
        var net = new MeleeRules { Network = true, RespawnDelay = 3f, TimeLimit = 300f };
        jobs.Add(new Job("network m02", dir, SnapshotRig.BuildOf(dir, "m02", melee: ["s03cg02.vcf"], rules: net), SimRate.R20, [100, 600, 1400],
            Players: [("s05vpp01.vcf", 0), ("s01fp01.vcf", 0)]));
        jobs.Add(new Job("network teams m05", dir, SnapshotRig.BuildOf(dir, "m05", melee: [], rules: net with { TeamPlay = true }), SimRate.R28, [100, 200, 300, 400, 500, 600, 800, 1000, 1400],
            Players: [("s05vdl02.vcf", 1), ("s02vcm01.vcf", 2), ("s05vjs01.vcf", 1)]));
        RunAll(jobs, cov, "melees");
        foreach (var need in new[] { "melee", "network melee (Combat)", "flames", "mines", "oil or fire drops", "rounds in flight", "guided round with a target", "dying car",
                     "removed entity", "restore made cars", "restore dropped cars" })
            Assert.Contains(need, cov.Seen);
    }

    /// <summary>The Nitro Pack's network race and capture the flag: laps and flags mid-game.</summary>
    [Fact]
    public void NitroRaceAndCtfRestoreExactly()
    {
        if (SnapshotRig.NitroInstall is not { } dir) return;
        var cov = new SnapshotCoverage();
        var race = new MeleeRules { Network = true, Kind = MeleeKind.Race, GoalValue = 50 };
        var ctf = new MeleeRules { Network = true, Kind = MeleeKind.Ctf, TeamPlay = true, CtfTeams = 2, GoalValue = 50 };
        var combat = new MeleeRules { Network = true, Kind = MeleeKind.Combat, RespawnDelay = 3f };
        var jobs = new List<Job>
        {
            new("nitro race n21", dir, SnapshotRig.BuildOf(dir, "n21", melee: [], rules: race), SimRate.R20, [100, 800, 1600], Players: [("vppirna1.vcf", 0), ("vcgcour1.vcf", 0)]),
            new("nitro ctf n11", dir, SnapshotRig.BuildOf(dir, "n11", melee: [], rules: ctf), SimRate.R28, [100, 800, 1600], Players: [("vppirna1.vcf", 1), ("vcgcour1.vcf", 2)]),
            new("nitro combat n01", dir, SnapshotRig.BuildOf(dir, "n01", melee: ["vcgcour1.vcf"], rules: combat), SimRate.R20, [100, 800, 1600], Players: [("vppirna1.vcf", 0)]),
        };
        RunAll(jobs, cov, "Nitro race and CTF");
        Assert.Contains("network melee (Race)", cov.Seen);
        Assert.Contains("network melee (Ctf)", cov.Seen);
    }
}
