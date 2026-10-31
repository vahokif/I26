using I76.Formats.Installers;

namespace I76.Cli;

/// <summary>
/// i76 installer info SETUP.exe            the installer's Inno version, title, compression and what I26 would extract
/// i76 installer list SETUP.exe [--all]    the files under {app} (with --all, every file entry), with sizes and checksums
/// i76 installer extract SETUP.exe DIR [--all]   the game data into DIR (an install folder afterwards); --all every file, executables included
/// </summary>
public static class InstallerCommand
{
    public static int Run(List<string> argv)
    {
        if (argv.Count < 3)
        {
            Console.Error.WriteLine("usage: i76 installer info|list|extract SETUP.exe [DIR]");
            return 2;
        }
        try
        {
            switch (argv[1])
            {
                case "info":
                {
                    var g = GogInstaller.Open(argv[2]);
                    var s = g.Setup;
                    Console.WriteLine($"{s.IdString}: Inno Setup {s.Version}");
                    Console.WriteLine($"app: {s.Header.AppVersionedName} ({s.Header.AppId}), {g.Title}");
                    Console.WriteLine($"compression: {s.Header.Compression}; data {(s.DataOffset != 0 ? $"in the .exe at 0x{s.DataOffset:x}" : "in setup-N.bin parts")}; slices/disk {s.Header.SlicesPerDisk}");
                    Console.WriteLine($"entries: {s.Files.Count} files, {s.Data.Count} data, {s.Data.Select(d => (d.FirstSlice, d.ChunkOffset)).Distinct().Count()} chunks; languages {string.Join(",", s.Languages)}");
                    Console.WriteLine($"extract: {g.Wanted.Count()} of {g.Entries.Count} files under {{app}}, {GogInstaller.Mb(g.WantedBytes)}");
                    return 0;
                }
                case "list":
                {
                    if (argv.Contains("--all"))
                    {
                        var s = InnoSetup.Open(argv[2]);
                        foreach (var f in s.Files)
                            Console.WriteLine(f.External ? $"{f.Destination}\texternal" : $"{f.Destination}\t{s.Data[f.Location].FileSize}\t{s.Data[f.Location].Checksum}\t{f.Languages}\t{f.Components}\t{f.Check}");
                        return 0;
                    }
                    var g = GogInstaller.Open(argv[2]);
                    foreach (var e in g.Entries)
                        Console.WriteLine($"{(e.Wanted ? "+" : "-")} {e.RelativePath}\t{e.Data.FileSize}\t{e.Data.Checksum}{(e.Skip is null ? "" : $"\t({e.Skip})")}");
                    return 0;
                }
                case "extract":
                {
                    if (argv.Count < 4) throw new ArgumentException("extract needs a target folder");
                    var g = GogInstaller.Open(argv[2]);
                    bool all = argv.Contains("--all");
                    Console.WriteLine(all ? $"{g.Title}: all {g.Entries.Count} files -> {argv[3]}" : $"{g.Title}: {g.Wanted.Count()} files, {GogInstaller.Mb(g.WantedBytes)} -> {argv[3]}");
                    long last = 0;
                    var r = g.ExtractTo(argv[3], p =>
                    {
                        if (p.Bytes - last < (16L << 20) && p.Files < p.FilesTotal) return;
                        last = p.Bytes;
                        Console.Error.Write($"\r{p.Files}/{p.FilesTotal} files, {GogInstaller.Mb(p.Bytes)} / {GogInstaller.Mb(p.BytesTotal)}   ");
                    }, all: all);
                    Console.Error.WriteLine();
                    Console.WriteLine($"done: {r.Files} files, {GogInstaller.Mb(r.Bytes)} in {r.Elapsed.TotalSeconds:0.0} s");
                    return 0;
                }
                default:
                    Console.Error.WriteLine($"unknown installer command {argv[1]}");
                    return 2;
            }
        }
        catch (InstallerException ex)
        {
            Console.Error.WriteLine($"{ex.Kind}: {ex.Message}");
            return 1;
        }
    }
}
