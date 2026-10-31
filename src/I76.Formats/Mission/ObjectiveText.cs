namespace I76.Formats.Mission;

/// <summary>
/// A mission's objective text (.npt, named by WRLD +0x45), as the game
/// reads it: up to 6 objective lines, "(HIDDEN)" marking ones that start
/// hidden, then after "(failure)" up to 16 failure messages for the debrief
/// (failAllObj picks one).
/// </summary>
public sealed record ObjectiveText(List<(string Text, bool Hidden)> Objectives, List<string> Failures)
{
    public const int MaxObjectives = 6, MaxFailures = 16;
    private const string HiddenTag = "(hidden)", FailureTag = "(failure)";

    public static ObjectiveText Parse(string text)
    {
        var objectives = new List<(string, bool)>();
        var failures = new List<string>();
        bool inFailures = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r', ' ', '\t', '\0');
            if (line.Length == 0) continue;
            if (line.Trim().Equals(FailureTag, StringComparison.OrdinalIgnoreCase)) { inFailures = true; continue; }
            if (inFailures) { if (failures.Count < MaxFailures) failures.Add(line); continue; }
            if (objectives.Count >= MaxObjectives) continue;
            bool hidden = line.StartsWith(HiddenTag, StringComparison.OrdinalIgnoreCase);
            objectives.Add((hidden ? line[HiddenTag.Length..].TrimStart() : line, hidden));
        }
        return new ObjectiveText(objectives, failures);
    }
}
