using I76.Formats.Fsm;
using I76.Formats.Mission;
using I76.Formats.Vfs;
using I76.Sim.Fsm;
using Xunit;
using Xunit.Abstractions;

namespace I76.Sim.Tests;

/// <summary>
/// The cars a mission is about (CoopKeyCars), which Co-op Scaling never copies (#195): a car the script decides an objective or
/// the mission on alone, or films in a cut-scene. The rest of the enemy cars it orders are plain opposition, copied as before.
/// </summary>
public class CoopKeyCarsTests(ITestOutputHelper output)
{
    /// <summary>A script with each kind of use: the boss's death behind a flag, two grunts counted and tested together, a filmed car, the player.</summary>
    private const string Sample = """
        mission: X
        actors:
          groove: {object: a}
          boss: {object: b}
          grunt1: {object: c}
          grunt2: {object: d}
          star: {object: e}
          runner: {object: f}
        globals:
          g0: actor.groove
          g1: actor.boss
          g2: actor.grunt1
          g3: actor.grunt2
          g4: 0
          g5: 0
          g6: actor.star
          g7: actor.runner
        scripts:
          boss:
            fields: [f0, f1, f2]
            steps:
              - wait_until: isDead(f0)
                then: set(f1, 1)
                while_waiting: attack(f0, f2, 1)
          grunt:
            fields: [f0, f1, f2]
            steps:
              - wait_until: isDead(f0)
                then: inc(f1)
                while_waiting: attack(f0, f2, 1)
          judge:
            fields: [f0, f1, f2, f3, f4, f5]
            steps:
              - wait:
                  - when: f1 == 1
                    then: success(1)
                  - when: f2 == 2
                    then: success(2)
                  - when: isDead(f3) && isDead(f4)
                    then: success(3)
                  - when: isDead(f0)
                    then: failAllObj(8, 1)
                  - when: '!isWithin(f5, f0, 450)'
                    then: failAllObj(8, 4)
          film:
            fields: [f0]
            steps:
              - wait_until: isKeypress()
                while_waiting: camObjObj(f0, -100, 200, 320, f0)
              - do: sit(f0)
        threads:
          thread0: {script: boss, args: {f0: g1, f1: g4, f2: g0}}
          thread1: {script: grunt, args: {f0: g2, f1: g5, f2: g0}}
          thread2: {script: grunt, args: {f0: g3, f1: g5, f2: g0}}
          thread3: {script: judge, args: {f0: g0, f1: g4, f2: g5, f3: g2, f4: g3, f5: g7}}
          thread4: {script: film, args: {f0: g6}}
        """;

    [Fact]
    public void TheRuleTellsTheCarsAMissionIsAboutFromItsPlainOpposition()
    {
        var src = MissionScriptYaml.Read(Sample);
        var r = CoopKeyCars.Analyze(src, i => i == 0);
        string Of(int i) => src.Actors[i].Key;
        var key = r.Key.ToDictionary(kv => Of(kv.Key), kv => kv.Value);
        // the boss: its death sets the flag an objective is decided on; the runner: the mission fails when it gets away from the player
        Assert.Equal(CoopKeyCars.Why.Objective, key["boss"]);
        Assert.Equal(CoopKeyCars.Why.Objective, key["runner"]);
        // the star: a cut-scene films it
        Assert.Equal(CoopKeyCars.Why.Filmed, key["star"]);
        // the grunts: counted, and tested together; the player is never a subject
        Assert.Equal(["boss", "runner", "star"], key.Keys.Order());
        Assert.Equal(["boss", "grunt1", "grunt2", "star"], r.Ordered.Select(Of).Order());
    }

    /// <summary>
    /// Every stock mission of both games: the enemy cars the script orders that are key cars, by actor label; every other enemy
    /// car it orders is copied. T09's Callisto (#195) is one; group targets (T13's ten, T02's three) are not.
    /// </summary>
    [Fact]
    public void TheKeyCarsOfTheStockMissions()
    {
        var expected = new Dictionary<string, string>();
        if (SnapshotRig.Install is not null)
            foreach (var (m, cars) in new[]
            {
                ("s01", "enemy6:Objective hate:Objective"), ("s02", "enemy1:Filmed"), ("s07", "mlktrk:Objective, Filmed"),
                ("t04", "bandit:Objective, Filmed knight:Objective, Filmed patriot:Objective, Filmed"), ("t07", "cop1:Filmed cop2:Filmed"),
                ("t09", "calisto:Objective, Filmed"), ("t12", "DISCO:Objective speedy:Filmed tanker:Objective, Filmed"),
                ("t17", "cloaker:Filmed malocchio:Objective, Filmed"),
            }) expected[m] = cars;
        if (SnapshotRig.NitroInstall is not null)
            foreach (var (m, cars) in new[]
            {
                ("p02", "clown:Objective, Filmed"), ("p06", "enemy1:Filmed"), ("p09", "sleep1:Objective, Filmed"), ("p10", "bob:Objective, Filmed"),
                ("p12", "army1:Filmed"), ("p13", "mondo1:Filmed mondo2:Filmed"), ("p14", "enemy2:Filmed"),
                ("p15", "hearse1_4:Filmed hearse2_4:Filmed hearse3_4:Filmed"), ("p17", "enemy1:Objective, Filmed"), ("p18", "enemy3:Filmed"),
                ("p19", "target:Objective, Filmed"),
            }) expected[m] = cars;
        var found = new Dictionary<string, string>();
        foreach (var dir in new[] { SnapshotRig.Install, SnapshotRig.NitroInstall })
        {
            if (dir is null) continue;
            var install = new GameInstall(dir);
            foreach (var m in SnapshotRig.Missions(dir))
            {
                if (install.FindMission(m) is not { } path) continue;
                var payload = MissionScriptFile.Payload(File.ReadAllBytes(path));
                if (payload.Length == 0) continue;
                var src = MissionScriptDecompiler.Decompile(payload).Script;
                var objs = new MissionFile(path).PlacedObjects();
                PlacedObject? Of(int i) => src.Actors[i].Id is { } id ? objs.LastOrDefault(o => o.Raw.Length >= 8 && o.Raw.AsSpan(0, 8).SequenceEqual(id.AsSpan(0, System.Math.Min(8, id.Length)))) : null;
                var r = CoopKeyCars.Analyze(src, i => Of(i)?.IsPlayer == true);
                // the cars Co-op Scaling would look at: an enemy's (not the player's side, not nobody's) the script orders
                var enemies = Enumerable.Range(0, src.Actors.Count)
                    .Where(i => r.Ordered.Contains(i) && Of(i) is { ObjectType: 1, IsPlayer: false } o && ((o.ObjectFlags >> 16) & 7) is not (0 or 1)).ToList();
                var key = enemies.Where(r.IsKey).Select(i => $"{src.Actors[i].Key}:{r.Key[i]}").Order(StringComparer.Ordinal).ToList();
                var copied = enemies.Where(i => !r.IsKey(i)).Select(i => src.Actors[i].Key);
                output.WriteLine($"{m}: key [{string.Join(" ", key)}], copied [{string.Join(" ", copied)}]");
                if (key.Count > 0) found[m] = string.Join(" ", key);
            }
        }
        Assert.Equal(expected.OrderBy(kv => kv.Key), found.OrderBy(kv => kv.Key));
    }
}
