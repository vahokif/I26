using System;
using System.Text.Json;

namespace I76.Game.Boot;

/// <summary>
/// The start-up check for a newer release (Play Options: Check for Updates): the logic only, with no engine calls so it can be tested.
/// One request per start to the release list's "latest" answer, which never offers a draft or a pre-release; a build without a release version
/// ("dev", from the source) never asks and never shows a line.
/// </summary>
public static class UpdateCheck
{
    public const string Url = "https://api.github.com/repos/vahokif/I26/releases/latest";
    public const string Accept = "application/vnd.github+json";
    public const double TimeoutSeconds = 5;
    private const string PagePrefix = "https://github.com/";

    /// <summary>A version as numbers plus whether it is a pre-release (a suffix after a dash), which sorts before the same numbers without one.</summary>
    public readonly record struct Ver(int[] Numbers, bool Pre);

    /// <summary>"v0.12.0", "0.12.0", "0.12" or the stamped "0.11.0 (3e3e5b5)" and "0.11.0-rc1": the numbers; null for "dev", empty or anything else.</summary>
    public static Ver? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string t = text.Trim();
        int sp = t.IndexOf(' ');
        if (sp >= 0) t = t[..sp];
        if (t.Length > 0 && (t[0] == 'v' || t[0] == 'V')) t = t[1..];
        int plus = t.IndexOf('+');
        if (plus >= 0) t = t[..plus];
        bool pre = false;
        int dash = t.IndexOf('-');
        if (dash >= 0) { pre = true; t = t[..dash]; }
        var parts = t.Split('.');
        if (parts.Length is < 1 or > 4) return null;
        var n = new int[parts.Length];
        for (int i = 0; i < parts.Length; i++)
            if (!int.TryParse(parts[i], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out n[i])) return null;
        return new Ver(n, pre);
    }

    public static int Compare(Ver a, Ver b)
    {
        int len = Math.Max(a.Numbers.Length, b.Numbers.Length);
        for (int i = 0; i < len; i++)
        {
            int x = i < a.Numbers.Length ? a.Numbers[i] : 0, y = i < b.Numbers.Length ? b.Numbers[i] : 0;
            if (x != y) return x < y ? -1 : 1;
        }
        return a.Pre == b.Pre ? 0 : a.Pre ? -1 : 1;
    }

    /// <summary>The build has a release version to compare (a source build, "dev", has none and neither asks nor nags).</summary>
    public static bool HasVersion(string running) => Parse(running) is not null;

    /// <summary>The release tag is a newer version than the running build; false for either side unreadable.</summary>
    public static bool IsNewer(string tag, string running)
        => Parse(tag) is { Pre: false } t && Parse(running) is { } r && Compare(t, r) > 0;

    /// <summary>"0.12.0" for the tag "v0.12.0", for the notice's text.</summary>
    public static string Display(string tag) => string.Join('.', Parse(tag)?.Numbers ?? []) is { Length: > 0 } s ? s : tag;

    /// <summary>The version for the User-Agent: "0.11.0" out of "0.11.0 (3e3e5b5)".</summary>
    public static string Short(string running) => running.Split(' ')[0];

    /// <summary>The tag and the release page from the API's answer; null for bad JSON, a missing tag or a page that is not on GitHub.</summary>
    public static (string Tag, string Url)? ParseRelease(string? json)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            using var d = JsonDocument.Parse(json);
            var root = d.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("tag_name", out var tag) || tag.ValueKind != JsonValueKind.String || Parse(tag.GetString()) is null) return null;
            if (root.TryGetProperty("draft", out var dr) && dr.ValueKind == JsonValueKind.True) return null;
            if (root.TryGetProperty("prerelease", out var pr) && pr.ValueKind == JsonValueKind.True) return null;
            string url = root.TryGetProperty("html_url", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() ?? "" : "";
            if (!url.StartsWith(PagePrefix, StringComparison.Ordinal)) url = PagePrefix + "vahokif/I26/releases/latest";
            return (tag.GetString()!, url);
        }
        catch (JsonException) { return null; }
    }
}
