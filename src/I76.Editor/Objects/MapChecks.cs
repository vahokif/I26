using System.Numerics;
using I76.Formats.Mission.Edit;

namespace I76.Editor.Objects;

public enum MapMode { Melee, Race, Ctf, Other }
public enum Severity { Info, Warning, Error }

/// <summary>One finding of the map check: how bad, what to tell the author, and the team or checkpoint it is about (0 for none).</summary>
public sealed record Issue(Severity Severity, string Message, int Subject = 0);

/// <summary>
/// What a map's mode needs of its markers: spawns by name (a team's table in CTF),
/// at most 64 spawns and 64 regen pads, race checkpoints numbered 1..N without gaps (30 m spheres, taken in order; check99 the finish),
/// one flag for each CTF team. The extension says the mode: .cbt melee, .rac race, .cf2/.cf3/.cf4 CTF with 2-4 teams.
/// </summary>
public static class MapChecks
{
    public const int MaxSpawns = 64, MaxRegens = 64;
    public const float CheckpointRadius = 30f;

    public static (MapMode Mode, int Teams) ModeOf(string extension) => extension.ToLowerInvariant() switch
    {
        ".cbt" => (MapMode.Melee, 0),
        ".rac" => (MapMode.Race, 0),
        ".cf2" => (MapMode.Ctf, 2),
        ".cf3" => (MapMode.Ctf, 3),
        ".cf4" => (MapMode.Ctf, 4),
        _ => (MapMode.Other, 0),
    };

    /// <summary>The team a spawn belongs to: spawnN is team N, a plain spawn team 1 (the engine puts it in the first table).</summary>
    public static int SpawnTeam(MapObject o) => o.Number is > 0 and var n ? n : 1;

    public static List<Issue> Validate(IEnumerable<MapObject> objects, string extension)
    {
        var all = objects.ToList();
        var (mode, teams) = ModeOf(extension);
        var issues = new List<Issue>();
        var spawns = all.Where(o => o.Role == MapRole.Spawn).ToList();
        var regens = all.Where(o => o.Role == MapRole.Regen).ToList();
        var checks = all.Where(o => o.Role == MapRole.Checkpoint).ToList();
        var flags = all.Where(o => o.Role == MapRole.Flag).ToList();

        if (spawns.Count == 0) issues.Add(new(Severity.Error, "No spawn point: nobody would have a place to start."));
        else if (mode == MapMode.Melee && spawns.Count < 2) issues.Add(new(Severity.Warning, "One spawn point: a melee with more than one player needs a place for each."));
        if (spawns.Count > MaxSpawns) issues.Add(new(Severity.Error, $"{spawns.Count} spawn points: the game keeps {MaxSpawns}."));
        if (regens.Count > MaxRegens) issues.Add(new(Severity.Error, $"{regens.Count} regen pads: the game keeps {MaxRegens}."));

        if (mode == MapMode.Race)
        {
            if (checks.Count == 0) issues.Add(new(Severity.Error, "A race needs checkpoints (check 1, 2, 3...)."));
            else
            {
                var nums = checks.Select(c => c.Number).Where(n => n < 99).ToList();
                if (nums.Count < 2) issues.Add(new(Severity.Warning, "A race with fewer than two checkpoints is a short lap."));
                int top = nums.Count == 0 ? 0 : nums.Max();
                for (int n = 1; n <= top; n++)
                    if (!nums.Contains(n)) issues.Add(new(Severity.Error, $"Checkpoint {n} is missing: the numbers must run 1 to {top} with no gap.", n));
                foreach (var g in nums.GroupBy(n => n).Where(g => g.Count() > 1))
                    issues.Add(new(Severity.Warning, $"Checkpoint {g.Key} is placed {g.Count()} times: only one counts.", g.Key));
                if (checks.Count(c => c.Number == 99) > 1) issues.Add(new(Severity.Warning, "More than one finish line (check 99)."));
                if (!checks.Any(c => c.Number == 99)) issues.Add(new(Severity.Info, "No finish line (check 99): a lap counts at checkpoint 1."));
                var ordered = checks.OrderBy(c => c.Number).ToList();
                for (int i = 1; i < ordered.Count; i++)
                {
                    float d = Vector2.Distance(new(ordered[i - 1].Position.X, ordered[i - 1].Position.Z), new(ordered[i].Position.X, ordered[i].Position.Z));
                    if (d < CheckpointRadius) issues.Add(new(Severity.Warning, $"Checkpoints {ordered[i - 1].Number} and {ordered[i].Number} are {d:0} m apart: their {CheckpointRadius:0} m spheres overlap.", ordered[i].Number));
                }
            }
        }
        else if (checks.Count > 0) issues.Add(new(Severity.Info, "Checkpoints only count in a race map (.rac)."));

        if (mode == MapMode.Ctf)
        {
            for (int t = 1; t <= teams; t++)
            {
                int nf = flags.Count(f => f.Number == t);
                if (nf == 0) issues.Add(new(Severity.Error, $"Team {t} has no flag: a {teams}-team CTF map needs a1flag{t}.", t));
                else if (nf > 1) issues.Add(new(Severity.Warning, $"Team {t} has {nf} flags: one is used.", t));
                if (!spawns.Any(s => SpawnTeam(s) == t)) issues.Add(new(Severity.Error, $"Team {t} has no spawn point (#spawn{t}).", t));
            }
            foreach (var f in flags.Where(f => f.Number < 1 || f.Number > teams))
                issues.Add(new(Severity.Warning, $"Flag {f.Number} is not used in a {teams}-team map.", f.Number));
            foreach (var s in spawns.Where(s => s.Number > teams)) issues.Add(new(Severity.Warning, $"Spawn for team {s.Number} is not used in a {teams}-team map.", s.Number));
        }
        else if (flags.Count > 0) issues.Add(new(Severity.Info, "Flags only count in a CTF map (.cf2, .cf3, .cf4)."));
        return issues;
    }

    public static Severity Worst(IEnumerable<Issue> issues) => issues.Select(i => i.Severity).DefaultIfEmpty(Severity.Info).Max();
}
