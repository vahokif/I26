using Godot;
using I76.Formats.Shell;

namespace I76.Game.Shell;

/// <summary>
/// The one place the upscaled menu art is found. None ships with the port; sources in order:
/// $I76_TEX_DIR (a folder, for development), then tex-menu as HdPacks finds it: the user folder's tex-menu/ (or one level too
/// deep, inside the folder Extract All made, beside the program), else the newest I26-HD-*.zip / i76-hd-*.zip holding one, read in place.
/// Paths are relative to the tex-menu layout ("shellbg1.webp", "nitro/load/loadgame.webp").
/// The "HD Menu Art" setting (Options, Graphic Detail) switches it all off; the 1997 look never asks.
/// </summary>
public static class HdAssets
{
    public const string EngineVersion = "1.1";
    private static HdSource? _source;
    private static bool _probed;

    /// <summary>The player's switch (ShellSettings.HdMenuArt).</summary>
    public static bool Enabled { get; set; } = true;

    public static HdSource? Source { get { Probe(); return _source; } }
    public static bool Installed => Source is not null;
    /// <summary>"name version", or null.</summary>
    public static string? Label => Source is { } s ? (s.Info?.ToString() ?? "folder") : null;

    private static void Probe()
    {
        if (_probed) return;
        _probed = true;
        var env = System.Environment.GetEnvironmentVariable("I76_TEX_DIR");
        if (env is { Length: > 0 } && Directory.Exists(env)) _source = new HdDirSource(env);
        else if (I76.Game.Assets.HdPacks.Found.Get(HdPackDiscovery.Menu) is { } found)
        {
            try { _source = found.Open(); }
            catch (System.Exception e) when (e is IOException or InvalidDataException or System.UnauthorizedAccessException) { GD.PushWarning($"HD menu pack: {found.Path}: {e.Message}"); }
        }
        if (_source is null) { GD.Print("HD menu art: none (no I76_TEX_DIR, and no tex-menu folder, or zip holding one, in the user folder or beside the program)"); return; }
        GD.Print($"HD menu art: {_source.Origin} ({_source.Count} files{(_source.Info is { } i ? $", {i}, {i.Date}" : "")})");
        if (_source.Info is { MinEngine.Length: > 0 } p && HdPackInfo.Compare(p.MinEngine, EngineVersion) > 0)
            GD.PushWarning($"HD menu pack needs engine {p.MinEngine}, this is {EngineVersion}");
    }

    public static bool Exists(string rel) => Enabled && Source?.Exists(rel) == true;
    public static byte[]? Read(string rel) => Enabled ? Source?.Read(rel) : null;

    /// <summary>The stem with a .webp, .png or .jpg that exists, as a path; null for none.</summary>
    public static string? Find(string stem) => new[] { ".webp", ".png", ".jpg" }.Select(e => stem + e).FirstOrDefault(Exists);

    /// <summary>The picture at the path, or null.</summary>
    public static Image? LoadImage(string? rel)
    {
        if (rel is null || Read(rel) is not { } b) return null;
        var img = new Image();
        var err = rel.ToLowerInvariant() switch
        {
            var r when r.EndsWith(".webp") => img.LoadWebpFromBuffer(b),
            var r when r.EndsWith(".png") => img.LoadPngFromBuffer(b),
            var r when r.EndsWith(".jpg") || r.EndsWith(".jpeg") => img.LoadJpgFromBuffer(b),
            _ => Error.FileUnrecognized,
        };
        return err == Error.Ok && !img.IsEmpty() ? img : null;
    }

    /// <summary>I76_NO_SYSTEM_FONTS=1: Impact and Arial from the pack only (to check the pack's fonts).</summary>
    private static bool NoSystemFonts => System.Environment.GetEnvironmentVariable("I76_NO_SYSTEM_FONTS") == "1";

    private static FontFile? PackFont(string file, TextServer.FontAntialiasing aa, TextServer.Hinting hinting)
    {
        if (Source?.Read("fonts/" + file) is not { } b) return null;
        var f = new FontFile { Data = b, Antialiasing = aa, Hinting = hinting };
        return f;
    }

    /// <summary>Impact itself: the pack's fonts/impact.ttf, else the system's; null when neither has it (another family in its place won't do).</summary>
    public static Font? Impact(TextServer.FontAntialiasing aa, TextServer.Hinting hinting = TextServer.Hinting.Light)
    {
        if (PackFont("impact.ttf", aa, hinting) is { } p) return p;
        if (NoSystemFonts) return null;
        var s = new SystemFont { FontNames = ["Impact"], Antialiasing = aa, Hinting = hinting };
        return s.GetFontName().Equals("Impact", System.StringComparison.OrdinalIgnoreCase) ? s : null;
    }

    /// <summary>Arial (regular or bold): the pack's fonts/arial.ttf / arialbd.ttf, else the system's Arial or a metric-alike; null when none (only under I76_NO_SYSTEM_FONTS).</summary>
    public static Font? Arial(bool bold, TextServer.FontAntialiasing aa, TextServer.Hinting hinting = TextServer.Hinting.Light)
    {
        if (PackFont(bold ? "arialbd.ttf" : "arial.ttf", aa, hinting) is { } p) return p;
        if (NoSystemFonts) return null;
        return new SystemFont { FontNames = ["Arial", "Liberation Sans", "Arimo", "Helvetica", "sans-serif"], FontWeight = bold ? 700 : 400, Antialiasing = aa, Hinting = hinting };
    }

    /// <summary>
    /// The faces the menus draw in that this machine lacks: "Impact" and/or "Arial" (the pack's copies count; a metric-alike such as Liberation Sans is a
    /// stand-in, not Arial). I76_FONTS_MISSING (test): "1" or "both", "Impact" or "Arial" force the answer, "0" none.
    /// </summary>
    public static List<string> MissingFonts()
    {
        var forced = System.Environment.GetEnvironmentVariable("I76_FONTS_MISSING");
        if (!string.IsNullOrEmpty(forced))
        {
            var l = new List<string>();
            if (forced is "1" or "both" or "Impact") l.Add("Impact");
            if (forced is "1" or "both" or "Arial") l.Add("Arial");
            return l;
        }
        var missing = new List<string>();
        foreach (var face in new[] { "Impact", "Arial" })
        {
            if (Source?.Read("fonts/" + (face == "Impact" ? "impact.ttf" : "arial.ttf")) is not null) continue;
            if (NoSystemFonts || !new SystemFont { FontNames = [face] }.GetFontName().Equals(face, System.StringComparison.OrdinalIgnoreCase)) missing.Add(face);
        }
        return missing;
    }

    /// <summary>The notice for missing faces, in the shell's capitals (three lines of about 25 letters fit).</summary>
    public static string FontsNotice(IReadOnlyList<string> missing) =>
        (missing.Count == 2 ? "ARIAL AND IMPACT ARE" : missing[0].ToUpperInvariant() + " IS") + " MISSING. INSTALL THE MS CORE FONTS.";

    /// <summary>The log's fuller advice: what to install on this system.</summary>
    public static string FontsAdvice(IReadOnlyList<string> missing) =>
        $"{string.Join(" and ", missing)} not installed (menus use a stand-in font): " + (OS.GetName() == "Linux"
            ? "install the Microsoft core fonts (ttf-mscorefonts-installer on Debian/Ubuntu, msttcore-fonts-installer on Fedora, ttf-ms-fonts on Arch)"
            : "install the Microsoft core fonts");

    /// <summary>What a cached result of <paramref name="rel"/> depends on.</summary>
    public static string Stamp(string rel) => $"{Source?.Origin}|{Source?.Info?.Version}|{Read(rel)?.Length}";
}
