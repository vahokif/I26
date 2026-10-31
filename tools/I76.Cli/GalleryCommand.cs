using I76.Editor.Objects;
using I76.Formats.Assembly;
using I76.Formats.Vfs;

namespace I76.Cli;

/// <summary>
/// i76 gallery OUT_DIR [--install DIR] [--nitro-install DIR]
/// The gallery map (<see cref="Gallery"/>): every breakable or drive-on structure of the install's catalogue on flat ground, a cell each, saved as
/// gallery.lvl for the base game and gallery.cbt for the Nitro Pack (the installs default to I76_INSTALL and I76_NITRO_INSTALL). Drop the files
/// into an install's addon\ folder (a scratch copy: the original must be left alone) to look at them in the game or the editor.
/// </summary>
public static class GalleryCommand
{
    public static int Run(List<string> argv, string? installDir)
    {
        string? Opt(string n) { int i = argv.IndexOf(n); return i >= 0 && i + 1 < argv.Count ? argv[i + 1] : null; }
        if (argv.Count < 2 || argv[1].StartsWith("--")) { Console.Error.WriteLine("usage: i76 gallery OUT_DIR [--install DIR] [--nitro-install DIR]"); return 2; }
        string outDir = argv[1];
        string cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "i26-gallery");
        int made = 0;
        foreach (var (dir, nitro) in new[] { (Opt("--install") ?? installDir, false), (Opt("--nitro-install") ?? Environment.GetEnvironmentVariable("I76_NITRO_INSTALL"), true) })
        {
            if (dir is null || !Directory.Exists(dir)) continue;
            var install = new GameInstall(dir);
            var asm = new ModelAssembler(new AssetVfs(install));
            var (files, placed) = Gallery.Build(install, asm, outDir, Gallery.DefaultStem, cache, nitro);
            Console.WriteLine($"{(nitro ? "Nitro Pack" : "base game")}: {placed.Count} structures -> {string.Join(", ", files.Select(Path.GetFileName))}");
            made++;
        }
        if (made == 0) { Console.Error.WriteLine("no install: --install / --nitro-install, or I76_INSTALL / I76_NITRO_INSTALL"); return 2; }
        return 0;
    }
}
