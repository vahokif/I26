using System.Diagnostics;
using System.Text.RegularExpressions;
using Xunit;

namespace I76.Formats.Tests;

/// <summary>
/// The published tree stays free of what does not belong in it: labels and addresses from inside the original game's
/// executables (the port describes what the original does, not where), references to private tooling, and personal
/// details. Every tracked text file is read; the patterns are assembled from pieces so this file does not match itself.
/// </summary>
public class PublicTreeTests
{
    private static readonly string[] Binary = [".png", ".jpg", ".jpeg", ".ico", ".bin", ".zip", ".pck", ".ttf", ".otf", ".wav", ".ogg", ".mp3", ".svgz", ".exr", ".dll", ".exe"];

    private static readonly Regex Anywhere = new(string.Join("|",
        "FUN" + "_[0-9A-Fa-f]{8}",
        "DAT" + "_[0-9A-Fa-f]{8}",
        "Orig" + "Offset",
        "i76" + "-decomp",
        "(?i:ghi" + "dra)",
        "docs/" + "re-[a-z]",
        "(?i:chp" + "atrick)",
        "/home/[a-z]",
        "/mnt/" + "bulk",
        "192\\.168\\.0\\." + "100"));

    // an address inside the original executables' image, in a comment (code constants may legitimately hold such numbers)
    private static readonly Regex CommentAddress = new(@"^\s*(//|\*|#).*\b0x00[4-6][0-9A-Fa-f]{5}\b");

    // a global of the original executable (g_ and a camelCase name), in a comment line or a document
    private static readonly Regex CommentGlobal = new(@"^\s*(//|\*|#).*\b" + "g" + "_[a-z][A-Za-z]+");
    private static readonly Regex DocGlobal = new(@"\b" + "g" + "_[a-z][A-Za-z]+");

    [Fact]
    public void TheTrackedTreeHoldsNoOriginalAddressesPrivateToolingOrPersonalDetails()
    {
        if (Root() is not { } root || Tracked(root) is not { } files) return;   // not a git checkout (a source zip): nothing to check
        var hits = new List<string>();
        foreach (var rel in files)
        {
            if (Binary.Contains(Path.GetExtension(rel).ToLowerInvariant())) continue;
            var path = Path.Combine(root, rel);
            if (!File.Exists(path)) continue;
            int n = 0;
            bool doc = Path.GetExtension(rel).Equals(".md", StringComparison.OrdinalIgnoreCase);
            foreach (var line in File.ReadLines(path))
            {
                n++;
                if (Anywhere.IsMatch(line) || CommentAddress.IsMatch(line) || (doc ? DocGlobal : CommentGlobal).IsMatch(line)) hits.Add($"{rel}:{n}: {line.Trim()}");
            }
        }
        Assert.True(hits.Count == 0, "found:\n" + string.Join("\n", hits.Take(50)));
    }

    private static string? Root()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "I76.sln")) && (Directory.Exists(Path.Combine(d.FullName, ".git")) || File.Exists(Path.Combine(d.FullName, ".git")))) return d.FullName;
        return null;
    }

    private static List<string>? Tracked(string root)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("git", "ls-files -z") { WorkingDirectory = root, RedirectStandardOutput = true, UseShellExecute = false });
            if (p is null) return null;
            var all = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            return p.ExitCode == 0 ? [.. all.Split('\0', StringSplitOptions.RemoveEmptyEntries)] : null;
        }
        catch (System.ComponentModel.Win32Exception) { return null; }   // no git on the machine
    }
}
