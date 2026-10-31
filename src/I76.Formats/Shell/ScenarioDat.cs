using System.Text;

namespace I76.Formats.Shell;

/// <summary>One mission of the Nitro campaign: its title, file, whether it is done, and the recommended cars (nitscar.def row, variant).</summary>
public sealed class ScenarioMission
{
    public string Title { get; init; } = "";
    public string File { get; init; } = "";
    public bool Done { get; set; }
    public List<(int Row, int Variant)> Recommended { get; } = [];
    /// <summary>The mission without its extension (p01).</summary>
    public string Stem => Path.GetFileNameWithoutExtension(File);
}

public sealed class ScenarioCharacter
{
    public string Name { get; init; } = "";
    public List<ScenarioMission> Missions { get; } = [];
}

/// <summary>
/// addon\SCENARIO.DAT (the scenario loader): CHARACTER name, MISSION_COUNT n, then n lines
/// "Title, file, done, k, a-b, ...". The port keeps the progress in a copy of the text under user://, written by <see cref="MarkDone"/>
/// the way the mission-done marking does.
/// </summary>
public sealed class ScenarioDat
{
    public const int MaxCharacters = 4, MaxMissions = 20, MaxPairs = 5;
    public List<ScenarioCharacter> Characters { get; } = [];

    /// <summary>The unlock: every mission of the first three characters is done (Natty Dread's own do not count).</summary>
    public bool AllDoneExceptLast => Characters.Count > 0 && Characters.Take(Characters.Count - 1).All(c => c.Missions.All(m => m.Done));

    public static ScenarioDat Parse(string text)
    {
        var dat = new ScenarioDat();
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        for (int i = 0; i < lines.Count && dat.Characters.Count < MaxCharacters; i++)
        {
            var line = lines[i].TrimStart(' ', '\t');
            if (line.Length == 0 || line[0] == '#') continue;
            if (!line.StartsWith("CHAR", StringComparison.OrdinalIgnoreCase)) continue;
            var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;
            var ch = new ScenarioCharacter { Name = parts[1].Replace('_', ' ') };
            dat.Characters.Add(ch);
            // MISSION_COUNT n, then n mission lines (blank and comment lines between are skipped)
            int n = 0;
            for (i++; i < lines.Count; i++)
            {
                var l = lines[i].TrimStart(' ', '\t');
                if (l.Length == 0 || l[0] == '#') continue;
                var mp = l.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (l.StartsWith("MISS", StringComparison.OrdinalIgnoreCase) && mp.Length >= 2 && int.TryParse(mp[1], out n)) break;
            }
            n = Math.Min(n, MaxMissions);
            for (i++; i < lines.Count && ch.Missions.Count < n; i++)
            {
                var l = lines[i];
                if (l.Trim().Length == 0 || l.TrimStart()[0] == '#') continue;
                if (ParseMission(l) is { } m) ch.Missions.Add(m);
            }
            i--;
        }
        return dat;
    }

    private static ScenarioMission? ParseMission(string line)
    {
        int c = line.IndexOf(',');
        if (c < 0) return null;
        var title = line[..c].Trim(' ', '\t');
        var f = line[(c + 1)..].Split(new[] { ' ', ',', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (f.Length < 3 || !int.TryParse(f[1], out int done) || !int.TryParse(f[2], out int k)) return null;
        var m = new ScenarioMission { Title = title.Length > 31 ? title[..31] : title, File = f[0], Done = done != 0 };
        for (int j = 0; j < Math.Min(k, MaxPairs) && 3 + j < f.Length; j++)
        {
            var ab = f[3 + j].Split('-');
            if (ab.Length == 2 && int.TryParse(ab[0], out int a) && int.TryParse(ab[1], out int b)) m.Recommended.Add((a, b));
        }
        return m;
    }

    /// <summary>
    /// The mission-done marking: the line holding the mission's file name (case-insensitive) gets the first digit after its next
    /// comma, the done field, made a 1. Everything else is written back as it was. Returns the new text, or null if no line names it.
    /// </summary>
    public static string? MarkDone(string text, string file)
    {
        file = file.EndsWith(".msn", StringComparison.OrdinalIgnoreCase) ? file : file + ".msn";
        var lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            int at = lines[i].IndexOf(file, StringComparison.OrdinalIgnoreCase);
            if (at < 0) continue;
            int comma = lines[i].IndexOf(',', at);
            if (comma < 0) continue;
            for (int j = comma + 1; j < lines[i].Length; j++)
                if (char.IsDigit(lines[i][j]))
                {
                    if (lines[i][j] == '0') { var sb = new StringBuilder(lines[i]); sb[j] = '1'; lines[i] = sb.ToString(); }
                    return string.Join('\n', lines);
                }
        }
        return null;
    }
}
