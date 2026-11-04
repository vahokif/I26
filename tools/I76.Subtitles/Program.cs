using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using I76.Formats.Audio;
using I76.Formats.Vfs;
using I76.Formats.Video;
using Whisper.net;
using Whisper.net.Ggml;

namespace I76.Subtitles;

public static class Program
{
    private static readonly string[] SfxStems =
    [
        "cricket", "chirp", "tire", "screech", "squeal", "engine", "rev",
        "gunshot", "shoot", "shot", "bang", "burp", "music", "drum",
        "thud", "crash", "applaus", "cheer", "groan", "grunt", "sigh",
        "gasp", "whir", "hum", "honk", "beep", "clatter", "splash", "explos",
        "sing", "laugh", "giggle", "cough", "scream", "horn", "ambient",
        "silence", "sound", "wind", "static", "chime", "door", "slam", "bell"
    ];

    private static readonly HashSet<string> StopWords =
    [
        "a", "an", "the", "and", "of", "in", "is", "it", "on", "at", "to", "for", "with"
    ];

    private static readonly HashSet<string> IgnoreDialogueStems =
    [
        "gadv", "gback", "gic", "gpage", "gpencil", "gquit", "grp", "gsml", "gsms", "gsr", "gsws", "gtext"
    ];

    private static readonly Regex NoiseRegex = new(@"^\s*[\(\*\[][a-zA-Z0-9\s,\-_'""\.]+[\)\*\]]\s*$", RegexOptions.Compiled);

    // Speaker tables for character attribution
    private static readonly Dictionary<string, string> I76Speakers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ta"] = "Stampede", ["sta"] = "Stampede", ["po"] = "Stampede",
        ["gc"] = "Groove",
        ["sk"] = "Skeeter",
        ["ir"] = "Radio",
        ["jc"] = "Jade", ["jad"] = "Jade",
        ["pa"] = "Patriot",
        ["rk"] = "Rex",
        ["ca"] = "Callisto",
        ["in"] = "Inferno",
        ["am"] = "Malochio", ["om"] = "Malochio",
        ["se"] = "Seagraves",
        ["ge"] = "Mechanic",
        ["fu"] = "Police", ["ps"] = "Police", ["of"] = "Officer",
        ["dc"] = "Disco Cat",
        ["bd"] = "Bus Driver",
        ["bx"] = "Boxer",
        ["bu"] = "Buffy",
        ["mg"] = "Mondo Burger",
        ["ns"] = "Nostromo",
        ["ok"] = "Oktoberfest",
        ["ma"] = "Maniac",
        ["ba"] = "Bad Guy",
        ["sn2"] = "Postal Worker",
        ["nl"] = "Civilian", ["cd"] = "Civilian"
    };

    private static readonly Dictionary<string, string> NitroSpeakers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["tau"] = "Stampede", ["sta"] = "Stampede",
        ["jad"] = "Jade",
        ["ske"] = "Skeeter",
        ["nat"] = "Natty Dread", ["mnd"] = "Natty Dread",
        ["dri"] = "Drinky",
        ["cri"] = "Crimson",
        ["bsc"] = "Base Command",
        ["crh"] = "Creeper", ["cro"] = "Creeper", ["crt"] = "Creeper",
        ["byo"] = "Reaper 1", ["byt"] = "Reaper 2",
        ["otn"] = "Bus Driver",
        ["stf"] = "Police", ["cop"] = "Police",
        ["hab"] = "Bad Habit",
        ["fro"] = "Frost",
        ["gas"] = "Creeper",
        ["cor"] = "Corey",
        ["sha"] = "Shark",
        ["rad"] = "Radio",
        ["hel"] = "Heli",
        ["tnk"] = "Tank",
        ["pat"] = "Patriot",
        ["tow"] = "Tower",
        ["max"] = "Max",
        ["bob"] = "Bob",
        ["deb"] = "Debbie",
        ["jon"] = "Jones",
        ["kin"] = "King",
        ["mon"] = "Monk",
        ["nig"] = "Night",
        ["nin"] = "Ninja",
        ["pre"] = "Preacher",
        ["wol"] = "Wolf",
        ["whi"] = "White",
        ["van"] = "Van",
        ["ufo"] = "UFO",
        ["tra"] = "Trapper",
        ["tro"] = "Trooper",
        ["hex"] = "Hex",
        ["flu"] = "Flush",
        ["fou"] = "Four",
        ["del"] = "Delta",
        ["cat"] = "Cat",
        ["car"] = "Carter",
        ["buz"] = "Buzzard",
        ["bas"] = "Bass",
        ["bli"] = "Blimp",
        ["aut"] = "Auto",
        ["alp"] = "Alpha",
        ["brv"] = "Bravo",
        ["cya"] = "Cyan",
        ["dsp"] = "Dispatch",
        ["esc"] = "Escort",
        ["gbn"] = "Goblin",
        ["gen"] = "General",
        ["guo"] = "Guard",
        ["lep"] = "Leopard",
        ["moj"] = "Mojo",
        ["rbg"] = "Red Baron",
        ["sna"] = "Snake",
        ["tab"] = "Tabitha",
        ["gro"] = "Groove"
    };

    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        bool isInteractive = args.Length == 0 && !Console.IsInputRedirected;
        try
        {
            int code = await RunAsync(args);
            if (isInteractive)
            {
                Console.WriteLine("\nPress Enter to exit...");
                try { Console.ReadLine(); } catch { }
            }
            return code;
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Error.WriteLine($"\n[ERROR] Unhandled Exception: {ex.Message}");
            Console.Error.WriteLine(ex.ToString());
            Console.ResetColor();

            if (!Console.IsInputRedirected)
            {
                Console.WriteLine("\nPress Enter to exit...");
                try { Console.ReadLine(); } catch { }
            }
            return 1;
        }
    }

    private static async Task<int> RunAsync(string[] args)
    {
        string game = "all";
        string? i76Install = null;
        string? nitroInstall = null;
        string? outDir = null;
        string modelName = "small.en";
        string? modelPath = null;
        string? filter = null;
        bool cutscenesOnly = false;
        bool dialogueOnly = false;
        bool skipExisting = false;
        bool copyUser = true;
        int threads = Math.Max(1, Environment.ProcessorCount / 2);

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (a is "--help" or "-h" or "-?")
            {
                PrintHelp();
                return 0;
            }
            if (a == "--game" && i + 1 < args.Length) game = args[++i].ToLowerInvariant();
            else if (a == "--i76-install" && i + 1 < args.Length) i76Install = args[++i];
            else if (a == "--nitro-install" && i + 1 < args.Length) nitroInstall = args[++i];
            else if (a == "--out" && i + 1 < args.Length) outDir = args[++i];
            else if (a == "--model" && i + 1 < args.Length) modelName = args[++i].ToLowerInvariant();
            else if (a == "--model-path" && i + 1 < args.Length) modelPath = args[++i];
            else if (a == "--filter" && i + 1 < args.Length) filter = args[++i].ToLowerInvariant();
            else if (a == "--threads" && i + 1 < args.Length && int.TryParse(args[++i], out int t)) threads = Math.Max(1, t);
            else if (a == "--cutscenes-only") cutscenesOnly = true;
            else if (a == "--dialogue-only") dialogueOnly = true;
            else if (a == "--skip-existing") skipExisting = true;
            else if (a == "--no-copy-user") copyUser = false;
        }

        Console.WriteLine("=================================================");
        Console.WriteLine("   I26 Native Subtitle Generator (C# / .NET)     ");
        Console.WriteLine("=================================================");
        Console.WriteLine($"Platform: {Environment.OSVersion} ({System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture})");
        Console.WriteLine($"Threads:  {threads}");

        string rootDir = FindAppOrRepoRoot();
        outDir ??= Path.Combine(rootDir, "subtitles");
        string userSubtitlesDir = GetUserSubtitlesDir();
        Console.WriteLine($"Output:   {outDir}");
        if (copyUser && !string.IsNullOrEmpty(userSubtitlesDir))
            Console.WriteLine($"User dir: {userSubtitlesDir}");

        i76Install ??= FindInstall("i76", rootDir);
        nitroInstall ??= FindInstall("nitro", rootDir);

        if (string.IsNullOrEmpty(i76Install) && string.IsNullOrEmpty(nitroInstall))
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("\n[Warning] Neither Interstate '76 nor Nitro Pack was found on your system.");
            Console.WriteLine("Copy your game folders here as 'i76' or 'nitro', or specify:");
            Console.WriteLine("  --i76-install <path>   and/or   --nitro-install <path>");
            Console.ResetColor();
            return 1;
        }

        // Resolve Whisper GGML model
        string resolvedModelPath = await ResolveModelAsync(modelPath, modelName, rootDir);
        Console.WriteLine($"Model:    {resolvedModelPath}");

        using var whisperFactory = WhisperFactory.FromPath(resolvedModelPath);

        bool doI76 = game is "all" or "i76";
        bool doNitro = game is "all" or "nitro";

        if (doI76)
        {
            Console.WriteLine("\n==================== Interstate '76 ====================");
            if (!string.IsNullOrEmpty(i76Install) && Directory.Exists(i76Install))
            {
                string targetOut = Path.Combine(outDir, "i76");
                string? targetUser = copyUser && !string.IsNullOrEmpty(userSubtitlesDir) ? Path.Combine(userSubtitlesDir, "i76") : null;
                Directory.CreateDirectory(targetOut);
                if (targetUser != null) Directory.CreateDirectory(targetUser);

                if (!dialogueOnly)
                    await ProcessCutscenesAsync(whisperFactory, i76Install, targetOut, targetUser, threads, skipExisting, filter);

                if (!cutscenesOnly)
                    await ProcessDialoguesAsync(whisperFactory, i76Install, targetOut, targetUser, threads, isNitro: false, skipExisting, filter);
            }
            else
            {
                Console.WriteLine("Interstate '76 install not found. Specify --i76-install or set $I76_INSTALL.");
            }
        }

        if (doNitro)
        {
            Console.WriteLine("\n================ Interstate '76 Nitro Pack ================");
            if (!string.IsNullOrEmpty(nitroInstall) && Directory.Exists(nitroInstall))
            {
                string targetOut = Path.Combine(outDir, "nitro");
                string? targetUser = copyUser && !string.IsNullOrEmpty(userSubtitlesDir) ? Path.Combine(userSubtitlesDir, "nitro") : null;
                Directory.CreateDirectory(targetOut);
                if (targetUser != null) Directory.CreateDirectory(targetUser);

                if (!dialogueOnly)
                    await ProcessCutscenesAsync(whisperFactory, nitroInstall, targetOut, targetUser, threads, skipExisting, filter);

                if (!cutscenesOnly)
                    await ProcessDialoguesAsync(whisperFactory, nitroInstall, targetOut, targetUser, threads, isNitro: true, skipExisting, filter);
            }
            else
            {
                Console.WriteLine("Nitro Pack install not found. Specify --nitro-install or set $I76_NITRO_INSTALL.");
            }
        }

        Console.WriteLine("\nAll done!");
        return 0;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("Usage: i26-subtitles [options]");
        Console.WriteLine("Options:");
        Console.WriteLine("  --game <all|i76|nitro>      Target game profile to transcribe (default: all)");
        Console.WriteLine("  --i76-install <path>        Path to Interstate '76 install folder");
        Console.WriteLine("  --nitro-install <path>      Path to Interstate '76 Nitro Pack install folder");
        Console.WriteLine("  --out <dir>                 Output root directory for subtitles (default: dist/i26-0.11.0/subtitles)");
        Console.WriteLine("  --model <name>              Whisper model (tiny, base, small, medium; default: small.en)");
        Console.WriteLine("  --model-path <path>         Direct path to custom ggml model file (.bin)");
        Console.WriteLine("  --filter <pattern>          Only process files containing pattern in name");
        Console.WriteLine("  --threads <n>               Number of CPU threads for Whisper (default: half CPU cores)");
        Console.WriteLine("  --cutscenes-only            Transcribe cutscenes (.SMK) only");
        Console.WriteLine("  --dialogue-only             Transcribe in-mission dialogue (.WAV) only");
        Console.WriteLine("  --skip-existing             Skip files whose subtitle output already exists");
        Console.WriteLine("  --no-copy-user              Do not copy generated subtitles to user data folder");
        Console.WriteLine("  --help, -h                  Show this help message");
    }

    private static string FindAppOrRepoRoot()
    {
        // If run as a published launcher next to i26 executable or pck
        string? procPath = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(procPath))
        {
            string? procDir = Path.GetDirectoryName(procPath);
            if (!string.IsNullOrEmpty(procDir) &&
                (File.Exists(Path.Combine(procDir, "i26.exe")) ||
                 File.Exists(Path.Combine(procDir, "i26.x86_64")) ||
                 File.Exists(Path.Combine(procDir, "i26.arm64")) ||
                 File.Exists(Path.Combine(procDir, "i26")) ||
                 File.Exists(Path.Combine(procDir, "i26.pck"))))
            {
                return procDir;
            }
        }

        string? cur = AppDomain.CurrentDomain.BaseDirectory;
        while (!string.IsNullOrEmpty(cur))
        {
            if (File.Exists(Path.Combine(cur, "I76.sln")) || Directory.Exists(Path.Combine(cur, "src", "I76.Formats")))
                return cur;
            if (File.Exists(Path.Combine(cur, "i26.exe")) ||
                File.Exists(Path.Combine(cur, "i26.x86_64")) ||
                File.Exists(Path.Combine(cur, "i26.arm64")) ||
                File.Exists(Path.Combine(cur, "i26")) ||
                File.Exists(Path.Combine(cur, "i26.pck")))
            {
                return cur;
            }
            cur = Path.GetDirectoryName(cur);
        }
        return Directory.GetCurrentDirectory();
    }

    private static string GetUserSubtitlesDir()
    {
        if (OperatingSystem.IsWindows())
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return !string.IsNullOrEmpty(appData) ? Path.Combine(appData, "I26", "subtitles") : "";
        }
        if (OperatingSystem.IsMacOS())
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, "Library", "Application Support", "I26", "subtitles");
        }
        else // Linux & others
        {
            string? xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            if (!string.IsNullOrEmpty(xdg)) return Path.Combine(xdg, "I26", "subtitles");
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, ".local", "share", "I26", "subtitles");
        }
    }

    private static string? FindInstall(string gameName, string rootDir)
    {
        string envName = gameName.Equals("nitro", StringComparison.OrdinalIgnoreCase) ? "I76_NITRO_INSTALL" : "I76_INSTALL";
        string? env = Environment.GetEnvironmentVariable(envName);
        if (!string.IsNullOrEmpty(env) && Directory.Exists(env)) return env;

        string subName = gameName.ToLowerInvariant();
        List<string> candidates =
        [
            Path.Combine(rootDir, subName),
            Path.Combine(Directory.GetCurrentDirectory(), subName)
        ];

        if (Directory.Exists(Path.Combine(rootDir, "dist")))
        {
            try
            {
                foreach (string d in Directory.GetDirectories(Path.Combine(rootDir, "dist")))
                    candidates.Add(Path.Combine(d, subName));
            }
            catch { }
        }

        try
        {
            foreach (string gog in I76.Formats.Installers.GogLibrary.FindOnSystem(_ => { }))
                candidates.Add(gog);
        }
        catch { }

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (gameName.Equals("i76", StringComparison.OrdinalIgnoreCase))
        {
            candidates.Add(@"C:\Program Files (x86)\GOG.com\Interstate 76 Arsenal\interstate 76");
            candidates.Add(@"C:\GOG Games\Interstate 76");
            candidates.Add(@"C:\Program Files (x86)\GOG Galaxy\Games\Interstate 76");
            candidates.Add(Path.Combine(home, ".wine", "drive_c", "GOG Games", "Interstate 76"));
            candidates.Add(Path.Combine(home, "GOG Games", "Interstate 76"));
            candidates.Add(Path.Combine(home, "Games", "interstate-76"));
        }
        else
        {
            candidates.Add(@"C:\Program Files (x86)\GOG.com\Interstate 76 Arsenal\Interstate 76 Nitro Pack");
            candidates.Add(@"C:\GOG Games\Interstate 76 Nitro Pack");
            candidates.Add(@"C:\Program Files (x86)\GOG Galaxy\Games\Interstate 76 Nitro Pack");
            candidates.Add(Path.Combine(home, ".wine", "drive_c", "GOG Games", "Interstate 76 Nitro Pack"));
            candidates.Add(Path.Combine(home, "GOG Games", "Interstate 76 Nitro Pack"));
            candidates.Add(Path.Combine(home, "Games", "interstate-76-nitro-pack"));
        }

        foreach (var c in candidates)
        {
            if (string.IsNullOrEmpty(c) || !Directory.Exists(c)) continue;
            string zfs = gameName.Equals("nitro", StringComparison.OrdinalIgnoreCase) ? "nitro.zfs" : "I76.ZFS";
            if (File.Exists(Path.Combine(c, zfs)) || File.Exists(Path.Combine(c, zfs.ToLowerInvariant())))
                return c;
        }

        return null;
    }

    private static async Task<string> ResolveModelAsync(string? explicitPath, string modelName, string repoRoot)
    {
        if (!string.IsNullOrEmpty(explicitPath) && File.Exists(explicitPath))
            return explicitPath;

        GgmlType ggmlType = modelName switch
        {
            "tiny" => GgmlType.Tiny,
            "tiny.en" or "tinyen" => GgmlType.TinyEn,
            "base" => GgmlType.Base,
            "base.en" or "baseen" => GgmlType.BaseEn,
            "small" => GgmlType.Small,
            "small.en" or "smallen" => GgmlType.SmallEn,
            "medium" => GgmlType.Medium,
            "medium.en" or "mediumen" => GgmlType.MediumEn,
            "large" or "largev3" => GgmlType.LargeV3,
            _ => GgmlType.BaseEn
        };

        string fileName = $"ggml-{ggmlType.ToString().ToLowerInvariant()}.bin";

        // Check search places: current dir, repo cache, temp dir, user data dir
        List<string> places =
        [
            Path.Combine(Directory.GetCurrentDirectory(), fileName),
            Path.Combine(repoRoot, "models", fileName),
            Path.Combine(Path.GetTempPath(), fileName)
        ];

        string userDir = GetUserSubtitlesDir();
        if (!string.IsNullOrEmpty(userDir))
        {
            string userModels = Path.Combine(Path.GetDirectoryName(userDir)!, "cache", "models");
            Directory.CreateDirectory(userModels);
            places.Insert(0, Path.Combine(userModels, fileName));
        }

        foreach (var p in places)
        {
            if (File.Exists(p) && new FileInfo(p).Length > 1_000_000) return p;
        }

        // Needs download
        string target = places[0];
        Console.WriteLine($"Model {fileName} not found locally. Downloading from repository...");
        Console.WriteLine($"Target: {target}");

        string tempTarget = target + ".tmp";
        using (var modelStream = await WhisperGgmlDownloader.GetGgmlModelAsync(ggmlType))
        using (var fileWriter = File.Create(tempTarget))
        {
            await modelStream.CopyToAsync(fileWriter);
        }

        if (File.Exists(target)) File.Delete(target);
        File.Move(tempTarget, target);

        Console.WriteLine("Download complete.");
        return target;
    }

    private static async Task ProcessCutscenesAsync(WhisperFactory factory, string installDir, string outDir, string? userOutDir, int threads, bool skipExisting, string? filter = null)
    {
        string smkDir = Directory.Exists(Path.Combine(installDir, "smk")) ? Path.Combine(installDir, "smk") : installDir;
        var smkFiles = Directory.EnumerateFiles(smkDir, "*.*")
            .Where(f => f.EndsWith(".smk", StringComparison.OrdinalIgnoreCase))
            .Where(f => string.IsNullOrEmpty(filter) || Path.GetFileNameWithoutExtension(f).Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (smkFiles.Count == 0)
        {
            Console.WriteLine($"No .smk cutscene files found in {smkDir}");
            return;
        }

        Console.WriteLine($"=== Transcribing {smkFiles.Count} Cutscenes (.SMK) ===");

        await using var processor = factory.CreateBuilder()
            .WithLanguage("en")
            .WithThreads(threads)
            .WithTokenTimestamps()
            .Build();

        foreach (var smkPath in smkFiles)
        {
            string stem = Path.GetFileNameWithoutExtension(smkPath).ToLowerInvariant();
            string outSrt = Path.Combine(outDir, $"{stem}.srt");

            if (skipExisting && File.Exists(outSrt))
            {
                Console.WriteLine($"  [{stem}] Skipped (exists)");
                continue;
            }

            // Credits has no spoken dialogue
            if (stem == "credf01")
            {
                File.WriteAllText(outSrt, "");
                if (userOutDir != null) File.WriteAllText(Path.Combine(userOutDir, $"{stem}.srt"), "");
                Console.WriteLine($"  [{stem}] OK - 0 cues (credits/music only)");
                continue;
            }

            var sw = Stopwatch.StartNew();
            using var wavStream = ExtractSmkAudioToWav(smkPath);
            if (wavStream == null || wavStream.Length < 100)
            {
                Console.WriteLine($"  [{stem}] Warning: no audio track found in {Path.GetFileName(smkPath)}");
                continue;
            }

            var cues = new List<string>();
            int idx = 1;

            await foreach (var seg in processor.ProcessAsync(wavStream))
            {
                string text = seg.Text?.Trim() ?? "";
                if (string.IsNullOrEmpty(text) || IsSfx(text)) continue;

                double startSec = seg.Start.TotalSeconds;
                double endSec = Math.Max(startSec + 0.8, seg.End.TotalSeconds);

                // Tighten using tokens if valid
                if (seg.Tokens != null && seg.Tokens.Length > 0)
                {
                    var cleanTokens = seg.Tokens.Where(t => !string.IsNullOrWhiteSpace(t.Text) &&
                        !t.Text.Trim().StartsWith('[') && !t.Text.Trim().StartsWith('(') && !t.Text.Trim().StartsWith('*')).ToList();

                    if (cleanTokens.Count > 0)
                    {
                        double tokStart = cleanTokens[0].Start * 0.01;
                        double tokEnd = cleanTokens[^1].End * 0.01;
                        if (tokEnd > tokStart)
                        {
                            startSec = tokStart;
                            endSec = Math.Max(startSec + 0.8, tokEnd);
                        }
                    }
                }

                cues.Add($"{idx}\n{FormatTimestamp(startSec)} --> {FormatTimestamp(endSec)}\n{text}");
                idx++;
            }

            string content = cues.Count > 0 ? string.Join("\n\n", cues) + "\n" : "";
            File.WriteAllText(outSrt, content, Encoding.UTF8);
            if (userOutDir != null) File.WriteAllText(Path.Combine(userOutDir, $"{stem}.srt"), content, Encoding.UTF8);

            Console.WriteLine($"  [{stem}] OK - {cues.Count} cues ({sw.Elapsed.TotalSeconds:F1}s)");
        }
    }

    private static async Task ProcessDialoguesAsync(WhisperFactory factory, string installDir, string outDir, string? userOutDir, int threads, bool isNitro, bool skipExisting, string? filter = null)
    {
        // 1. Gather all dialogue audio sources: disk .wav folder and/or .zfs archive
        var dialogueStreams = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

        string wavDir = Directory.Exists(Path.Combine(installDir, "wav")) ? Path.Combine(installDir, "wav") : "";
        if (!string.IsNullOrEmpty(wavDir))
        {
            foreach (var f in Directory.EnumerateFiles(wavDir, "*.wav"))
            {
                string s = Path.GetFileNameWithoutExtension(f).ToLowerInvariant();
                if (!IgnoreDialogueStems.Contains(s) && (string.IsNullOrEmpty(filter) || s.Contains(filter, StringComparison.OrdinalIgnoreCase)))
                    dialogueStreams[s] = File.ReadAllBytes(f);
            }
        }

        string zfsName = isNitro ? "nitro.zfs" : "I76.ZFS";
        string? zfsPath = Path.Combine(installDir, zfsName);
        if (!File.Exists(zfsPath)) zfsPath = Path.Combine(installDir, zfsName.ToLowerInvariant());
        if (File.Exists(zfsPath))
        {
            try
            {
                var zfs = new ZfsArchive(zfsPath);
                foreach (var entry in zfs.Entries)
                {
                    if (entry.Name.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
                    {
                        string s = Path.GetFileNameWithoutExtension(entry.Name).ToLowerInvariant();
                        if (!IgnoreDialogueStems.Contains(s) &&
                            (string.IsNullOrEmpty(filter) || s.Contains(filter, StringComparison.OrdinalIgnoreCase)) &&
                            !dialogueStreams.ContainsKey(s))
                        {
                            dialogueStreams[s] = zfs.Read(entry);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Note: could not read {zfsName}: {ex.Message}");
            }
        }

        if (dialogueStreams.Count == 0)
        {
            Console.WriteLine($"No dialogue .wav audio found in {installDir}");
            return;
        }

        Console.WriteLine($"\n=== Transcribing {dialogueStreams.Count} Dialogues (.WAV) ===");

        await using var processor = factory.CreateBuilder()
            .WithLanguage("en")
            .WithThreads(threads)
            .WithoutSuppressBlank()
            .Build();

        var results = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var sw = Stopwatch.StartNew();
        int count = 0;

        foreach (var (stem, rawWav) in dialogueStreams.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            count++;
            using var pcmStream = ConvertWavBytesTo16Khz(rawWav);
            if (pcmStream == null || pcmStream.Length < 100) continue;

            var parts = new List<string>();
            try
            {
                await foreach (var seg in processor.ProcessAsync(pcmStream))
                {
                    string t = seg.Text?.Trim() ?? "";
                    if (!string.IsNullOrEmpty(t) && !NoiseRegex.IsMatch(t) &&
                        !t.Equals("[blank_audio]", StringComparison.OrdinalIgnoreCase) &&
                        !t.Equals("*blank_audio*", StringComparison.OrdinalIgnoreCase) &&
                        !t.Equals("(silence)", StringComparison.OrdinalIgnoreCase))
                    {
                        parts.Add(t);
                    }
                }
            }
            catch { }

            string text = string.Join(" ", parts).Trim();
            text = Regex.Replace(text, @"\s+", " ");

            if (!string.IsNullOrEmpty(text) && !IsSfx(text))
            {
                string? spk = ResolveSpeaker(stem, isNitro);
                results[stem] = !string.IsNullOrEmpty(spk) ? $"{spk}: {text}" : text;
            }

            if (count % 100 == 0 || count == dialogueStreams.Count)
            {
                Console.WriteLine($"  [{count}/{dialogueStreams.Count}] Transcribed ({sw.Elapsed.TotalSeconds:F1}s)...");
            }
        }

        // Mission classification by sound stem prefix
        var missions = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var shared = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (stem, line) in results)
        {
            if (isNitro)
            {
                var m = Regex.Match(stem, @"^(\d{2})[a-z]{3}\d{2}$");
                if (m.Success)
                {
                    string mKey = $"p{int.Parse(m.Groups[1].Value):D2}";
                    if (!missions.TryGetValue(mKey, out var dict)) missions[mKey] = dict = [];
                    dict[stem] = line;
                }
                else if (stem.StartsWith("tmjad", StringComparison.OrdinalIgnoreCase) ||
                         stem.StartsWith("tmtau", StringComparison.OrdinalIgnoreCase) ||
                         stem.StartsWith("b01", StringComparison.OrdinalIgnoreCase))
                {
                    if (!missions.TryGetValue("b01", out var dict)) missions["b01"] = dict = [];
                    dict[stem] = line;
                }
                else
                {
                    shared[stem] = line;
                }
            }
            else
            {
                var mMatch = Regex.Match(stem, @"^(\d{2})s");
                var sMatch = Regex.Match(stem, @"^s(\d)s");
                if (mMatch.Success)
                {
                    string mKey = $"t{int.Parse(mMatch.Groups[1].Value):D2}";
                    if (!missions.TryGetValue(mKey, out var dict)) missions[mKey] = dict = [];
                    dict[stem] = line;
                }
                else if (sMatch.Success)
                {
                    string sKey = $"s{int.Parse(sMatch.Groups[1].Value):D2}";
                    if (!missions.TryGetValue(sKey, out var dict)) missions[sKey] = dict = [];
                    dict[stem] = line;
                }
                else if (stem.StartsWith("01a", StringComparison.OrdinalIgnoreCase) ||
                         stem.StartsWith("a01", StringComparison.OrdinalIgnoreCase))
                {
                    if (!missions.TryGetValue("a01", out var dict)) missions["a01"] = dict = [];
                    dict[stem] = line;
                }
                else
                {
                    shared[stem] = line;
                }
            }
        }

        var jsonOpts = new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        foreach (var (mKey, cues) in missions.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            string path = Path.Combine(outDir, $"{mKey}.json");
            string json = JsonSerializer.Serialize(cues, jsonOpts);
            File.WriteAllText(path, json, Encoding.UTF8);
            if (userOutDir != null) File.WriteAllText(Path.Combine(userOutDir, $"{mKey}.json"), json, Encoding.UTF8);
        }

        var allCues = new Dictionary<string, string>(shared, StringComparer.OrdinalIgnoreCase);
        foreach (var cues in missions.Values)
        {
            foreach (var (k, v) in cues) allCues[k] = v;
        }

        string sharedPath = Path.Combine(outDir, "dialogue.json");
        string sharedJson = JsonSerializer.Serialize(allCues, jsonOpts);
        File.WriteAllText(sharedPath, sharedJson, Encoding.UTF8);
        if (userOutDir != null) File.WriteAllText(Path.Combine(userOutDir, "dialogue.json"), sharedJson, Encoding.UTF8);

        Console.WriteLine($"Saved {missions.Count} mission JSON files and dialogue.json ({allCues.Count} cues) in {sw.Elapsed.TotalSeconds:F1}s");
    }

    public static MemoryStream? ExtractSmkAudioToWav(string smkPath)
    {
        try
        {
            using var smk = Smacker.Open(smkPath);
            if (!smk.HasAudio(0)) return null;

            int srcRate = smk.SampleRate(0);
            bool srcIs16Bit = smk.Is16Bit(0);
            bool srcStereo = smk.Stereo(0);

            using var pcmBuffer = new MemoryStream();
            while (smk.NextFrame(audio: (t, pcm) =>
            {
                if (t == 0) pcmBuffer.Write(pcm);
            }, video: false)) { }

            byte[] pcmData = pcmBuffer.ToArray();
            return PcmResampler.Resample(pcmData, srcRate, srcIs16Bit ? 16 : 8, srcStereo ? 2 : 1);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [smacker] decode failed for {Path.GetFileName(smkPath)} ({ex.Message}), trying ffmpeg fallback...");
            return ExtractWithFfmpeg(smkPath);
        }
    }

    private static MemoryStream? ExtractWithFfmpeg(string smkPath)
    {
        try
        {
            string tempWav = Path.Combine(Path.GetTempPath(), $"i26_audio_{Guid.NewGuid():N}.wav");
            var psi = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = $"-y -i \"{smkPath}\" -vn -acodec pcm_s16le -ar 16000 -ac 1 \"{tempWav}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var proc = Process.Start(psi);
            proc?.WaitForExit();
            if (File.Exists(tempWav))
            {
                var ms = new MemoryStream(File.ReadAllBytes(tempWav));
                File.Delete(tempWav);
                return ms;
            }
        }
        catch { }
        return null;
    }

    public static MemoryStream? ConvertWavBytesTo16Khz(byte[] riffBytes) => PcmResampler.FromRiff(riffBytes);

    public static MemoryStream ResamplePcmTo16KhzWav(byte[] pcmData, int srcRate, int bits, int channels) =>
        PcmResampler.Resample(pcmData, srcRate, bits, channels);

    private static string FormatTimestamp(double seconds)
    {
        int h = (int)(seconds / 3600);
        int m = (int)((seconds % 3600) / 60);
        int s = (int)(seconds % 60);
        int ms = Math.Min(999, (int)Math.Round((seconds - (int)seconds) * 1000));
        return $"{h:00}:{m:00}:{s:00},{ms:000}";
    }

    private static bool IsSfx(string text)
    {
        string t = text.Trim();
        if (string.IsNullOrEmpty(t) || t is "-" or "." or "--" or "...") return true;
        if ((t.StartsWith('[') && t.EndsWith(']')) ||
            (t.StartsWith('(') && t.EndsWith(')')) ||
            (t.StartsWith('*') && t.EndsWith('*')))
            return true;

        var words = Regex.Matches(t, @"[a-zA-Z]+")
            .Select(m => m.Value.ToLowerInvariant())
            .ToList();
        if (words.Count == 0) return true;

        var cw = words.Where(w => !StopWords.Contains(w)).ToList();
        if (cw.Count > 0 && cw.All(w => SfxStems.Any(s => w.StartsWith(s, StringComparison.OrdinalIgnoreCase))))
            return true;

        return false;
    }

    private static string? ResolveSpeaker(string soundName, bool isNitro)
    {
        string s = soundName.ToLowerInvariant().Replace(".wav", "");
        if (isNitro)
        {
            var m = Regex.Match(s, @"^(?:\d{2}|gd|rl|tm)([a-z]{3})\d{2}$");
            if (m.Success)
            {
                string code = m.Groups[1].Value;
                return NitroSpeakers.TryGetValue(code, out var name) ? name : Capitalize(code);
            }
            return null;
        }
        else
        {
            if (s.StartsWith("posta", StringComparison.OrdinalIgnoreCase) ||
                s.StartsWith("pos", StringComparison.OrdinalIgnoreCase))
                return "Stampede";

            var m = Regex.Match(s, @"(?:^\d\ds|^gds|^s\ds|^tms?)([a-z]{2})");
            if (m.Success)
            {
                string code = m.Groups[1].Value;
                return I76Speakers.TryGetValue(code, out var name) ? name : Capitalize(code);
            }
            return null;
        }
    }

    private static string Capitalize(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        return char.ToUpperInvariant(text[0]) + text[1..].ToLowerInvariant();
    }
}
