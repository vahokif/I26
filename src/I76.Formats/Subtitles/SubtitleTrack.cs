namespace I76.Formats.Subtitles;

/// <summary>A single subtitle cue with timing or sound key.</summary>
public sealed class SubtitleCue
{
    public double Start { get; init; }
    public double End { get; init; }
    public string Text { get; init; } = "";
    public string? Key { get; init; }
}

/// <summary>
/// A collection of subtitle cues for a movie or mission dialogue.
/// Supports time-based lookup (e.g. for video cutscenes) and key/sound-based lookup (e.g. for mission CB dialogue).
/// </summary>
public sealed class SubtitleTrack
{
    public List<SubtitleCue> Cues { get; } = [];
    public Dictionary<string, string> ByKey { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The cue active at the given time, or null.</summary>
    public SubtitleCue? CueAt(double seconds)
    {
        for (int i = 0; i < Cues.Count; i++)
            if (seconds >= Cues[i].Start && seconds <= Cues[i].End) return Cues[i];
        return null;
    }

    /// <summary>Longest page of a subtitle shown at once, in characters.</summary>
    public const int PageChars = 100;

    /// <summary>
    /// Splits a long cue into pages of at most <paramref name="maxChars"/> characters (cut at word
    /// boundaries) and returns the page for <paramref name="elapsed"/> seconds into a cue of
    /// <paramref name="length"/> seconds; each page lasts in proportion to its characters.
    /// </summary>
    public static string Page(string text, double elapsed, double length, int maxChars = PageChars)
    {
        text = text.Replace("\r", "").Replace('\n', ' ');
        if (text.Length <= maxChars) return text;
        var pages = new List<string>();
        var rest = text;

        string prefix = "";
        int colon = text.IndexOf(':');
        if (colon > 0 && colon <= 20 && colon + 2 < text.Length && text[colon + 1] == ' ')
        {
            prefix = text[..(colon + 2)];
        }

        while (rest.Length > maxChars)
        {
            int cut = rest.LastIndexOf(' ', maxChars);
            if (cut <= 0) cut = maxChars;
            pages.Add(rest[..cut].TrimEnd());
            rest = rest[cut..].TrimStart();
            if (prefix.Length > 0 && rest.Length > 0 && !rest.StartsWith(prefix))
            {
                rest = prefix + rest;
            }
        }
        if (rest.Length > 0) pages.Add(rest);
        if (length <= 0) length = text.Length / 15.0;      // no known length: about 15 characters a second
        double pos = Math.Clamp(elapsed / length, 0, 0.9999) * text.Length, acc = 0;
        for (int i = 0; i < pages.Count; i++)
        {
            acc += pages[i].Length + 1;
            if (pos < acc) return pages[i];
        }
        return pages[^1];
    }

    /// <summary>Finds the cue text active at the specified timestamp in seconds.</summary>
    public string? AtTime(double seconds)
    {
        for (int i = 0; i < Cues.Count; i++)
        {
            var c = Cues[i];
            if (seconds >= c.Start && seconds <= c.End)
                return c.Text;
        }
        return null;
    }

    /// <summary>Finds the subtitle text for a given sound name (stem or full file name, e.g. "tau01_01.wav").</summary>
    public string? BySound(string soundName)
    {
        string stem = System.IO.Path.GetFileNameWithoutExtension(soundName);
        if (ByKey.TryGetValue(stem, out var text)) return text;
        if (ByKey.TryGetValue(soundName, out text)) return text;
        return null;
    }

    /// <summary>Parses an SRT (SubRip) subtitle string.</summary>
    public static SubtitleTrack ParseSrt(string srt)
    {
        var track = new SubtitleTrack();
        using var reader = new System.IO.StringReader(srt);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            line = line.Trim();
            if (string.IsNullOrEmpty(line)) continue;

            string timeLine = line;
            if (!timeLine.Contains("-->"))
            {
                timeLine = reader.ReadLine()?.Trim() ?? "";
            }
            if (!timeLine.Contains("-->")) continue;

            var parts = timeLine.Split(new[] { "-->" }, StringSplitOptions.TrimEntries);
            if (parts.Length != 2) continue;
            double start = ParseTimestamp(parts[0]);
            double end = ParseTimestamp(parts[1]);

            var textLines = new List<string>();
            while ((line = reader.ReadLine()) != null && !string.IsNullOrWhiteSpace(line))
            {
                textLines.Add(line.Trim());
            }

            if (textLines.Count == 0) continue;
            string fullText = string.Join("\n", textLines);
            string? key = null;
            if (fullText.StartsWith('[') && fullText.IndexOf(']') is int closeIdx && closeIdx > 1)
            {
                key = fullText[1..closeIdx].Trim();
                fullText = fullText[(closeIdx + 1)..].TrimStart();
            }

            var cue = new SubtitleCue { Start = start, End = end, Text = fullText, Key = key };
            track.Cues.Add(cue);
            if (!string.IsNullOrEmpty(key)) track.ByKey[key] = fullText;
        }
        return track;
    }

    /// <summary>Parses a JSON subtitle string (array of cues or object map of sound key to text).</summary>
    public static SubtitleTrack ParseJson(string json)
    {
        var track = new SubtitleTrack();
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var elem in doc.RootElement.EnumerateArray())
            {
                double start = elem.TryGetProperty("start", out var s) ? s.GetDouble() : 0;
                double end = elem.TryGetProperty("end", out var e) ? e.GetDouble() : 0;
                string text = elem.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
                string? key = elem.TryGetProperty("key", out var k) ? k.GetString() : null;
                var cue = new SubtitleCue { Start = start, End = end, Text = text, Key = key };
                track.Cues.Add(cue);
                if (!string.IsNullOrEmpty(key)) track.ByKey[key] = text;
            }
        }
        else if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Value.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    string text = prop.Value.GetString() ?? "";
                    track.ByKey[prop.Name] = text;
                    track.Cues.Add(new SubtitleCue { Text = text, Key = prop.Name });
                }
                else if (prop.Value.ValueKind == System.Text.Json.JsonValueKind.Object)
                {
                    double start = prop.Value.TryGetProperty("start", out var s) ? s.GetDouble() : 0;
                    double end = prop.Value.TryGetProperty("end", out var e) ? e.GetDouble() : 0;
                    string text = prop.Value.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
                    track.ByKey[prop.Name] = text;
                    track.Cues.Add(new SubtitleCue { Start = start, End = end, Text = text, Key = prop.Name });
                }
            }
        }
        return track;
    }

    private static double ParseTimestamp(string ts)
    {
        ts = ts.Replace(',', '.').Trim();
        var parts = ts.Split(':');
        if (parts.Length == 3 && double.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double h)
            && double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double m)
            && double.TryParse(parts[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double sec))
        {
            return h * 3600 + m * 60 + sec;
        }
        if (parts.Length == 2 && double.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out m)
            && double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out sec))
        {
            return m * 60 + sec;
        }
        if (double.TryParse(ts, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double sOnly))
        {
            return sOnly;
        }
        return 0;
    }
}
