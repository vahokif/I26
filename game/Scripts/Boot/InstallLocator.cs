using Godot;

namespace I76.Game.Boot;

/// <summary>
/// Command-line options (after "--"). The install: --install, I76_INSTALL, the executable's folder
/// and its parent, then i76/ and nitro/ inside the executable's folder and in the local user folder, then GOG Galaxy / GOG installer installs (I76.Formats.Vfs.InstallSearch, Installers/GogLibrary.cs; nothing is remembered):
///   --install DIR   --mission NAME   --1997
///   --cam x,y,z,yaw,pitch   (game units and degrees)
///   --screenshot PATH  --quit-after FRAMES   --seed N
///   --drive [car.vcf]  --hold-throttle
///   --skip-movies   --movie NAME.smk
///   --skip-cutscenes   (a mission's skippable cut-scenes are skipped as if Esc were pressed when they start; single player)   --mute   (all audio off)   for unattended runs
///   --connect HOST:PORT [--name N] [--car X.vcf] [--team T]   (a network melee)
///   --host-listen [--arena m05] [--port 7676] [--rate 20|24|25|28|30|40|60] [--fill N] [--bots-ai N] [--bot-skill easy|normal|hard] [--teams] [--max-score S] [--time-limit SECONDS] [--anet HOST[:PORT]]
///   --host-listen --coop MISSION [--max-players 8] [--port 7676]   (a co-op story mission for up to eight instead of a melee: t01, s03, p05, ...)
///   --password WORD   (with --host-listen: the game needs it; with --connect or --join-anet: the one to join with; never saved)
///   --restrict a,b,c   (with --host-listen: WEAPON RESTRICTIONS, as i26-server's: all, slugs, mortars, missiles, flames, droppers, a .gdf, class:tier)
///   --turrets  (with --host-listen: cars may carry several roof turrets; else the host form's setting)
///   --hidden   (with --host-listen: the game allows the hidden weapons and cars; else the Hidden Weapons & Cars setting)
///   --invite-only   (with --host-listen: each join waits for the host's Ctrl+Y / Ctrl+N, Ctrl+H to ask about the next first)
///   --join-anet HOST[:PORT] [--session NAME] [--name N] [--car X.vcf] [--team T]   (a game listed on that anet server: the one named, else the first)
///   --share-maps | --no-share-maps   (a listen server sends its custom arena to players who lack it; else the setting, on)
///   --map-downloads ask|always|never   (joining: a host's custom map this install lacks; else the setting, Play Options)
///   --choose-game   show the game chooser even where one game would start by itself (Boot/GameChooser.cs)
///   --from-installer SETUP.exe [--to DIR]   extract the game data from a GOG offline installer (into i76/ or nitro/ next to the executable,
///                                           else in the local user folder, by default; Installers/ExtractionPlaces.cs), then start as normal (Boot/InstallerScreen.cs)
/// </summary>
public sealed class LaunchOptions
{
    public string? Install { get; set; }
    /// <summary>--from-installer SETUP.exe: a GOG offline installer to extract the game from before starting; --to DIR: where.</summary>
    public string? FromInstaller { get; set; }
    public string? InstallerTarget { get; set; }
    /// <summary>--choose-game: the chooser even if --install etc. would not need one (Options' "Switch Game" restarts with it).</summary>
    public bool ChooseGame { get; set; }
    /// <summary>Both games are available and nothing named one: Main shows the chooser (<see cref="Games"/>) before anything else.</summary>
    public bool Choose { get; set; }
    /// <summary>The games found at start (an install, or only its GOG installer, per game); empty when a choice was forced.</summary>
    public List<I76.Formats.Installers.AvailableGame> Games { get; set; } = [];
    /// <summary>The folders looked at when no install was found (for the no-install screen).</summary>
    public List<string> Checked { get; } = [];
    public string Mission { get; set; } = "a01";
    public bool Mode1997 { get; set; }
    public float[]? Cam { get; set; }
    public string? Screenshot { get; set; }
    public int QuitAfter { get; set; }
    /// <summary>--drive [car.vcf]: spawn a drivable car (default: the mission's player car or the Piranha).</summary>
    public string? Drive { get; set; }
    /// <summary>--ai script|chase|off: the mission script runs the cars (default), a placeholder chase, or nothing.</summary>
    public string Ai { get; set; } = "script";
    /// <summary>--cockpit: start in the in-car view (now the default; kept for old command lines).</summary>
    public bool Cockpit { get; set; }
    /// <summary>--chase: start in the chase view instead of the cockpit (for captures; the original always starts in the cockpit).</summary>
    public bool Chase { get; set; }
    /// <summary>--melee: the mission as an auto melee with no opponents (its CB, poetry, the melee rules), for testing an mXX map.</summary>
    public bool Melee { get; set; }
    /// <summary>--hold-throttle: drive forward without input (for off-screen captures).</summary>
    public bool HoldThrottle { get; set; }
    /// <summary>--hold-fire: keep the fire key down (for off-screen captures).</summary>
    public bool HoldFire { get; set; }
    /// <summary>--no-music: leave the mission's CD track off.</summary>
    public bool NoMusic { get; set; }
    /// <summary>--skip-movies: no intro or credits at startup, no briefing movies in missions.</summary>
    public bool SkipMovies { get; set; }
    /// <summary>--skip-cutscenes: a mission's skippable scripted camera scenes are skipped as Esc would, as they start (single player).</summary>
    public bool SkipCutscenes { get; set; }
    /// <summary>--mute: the master bus is muted for the run (unattended runs); the audio settings are not touched.</summary>
    public bool Mute { get; set; }
    /// <summary>Set by Parse from --mute; read by <c>Main.ApplyAudio</c>.</summary>
    public static bool Muted { get; private set; }
    /// <summary>The unattended-run switches (--skip-cutscenes, --mute); false for any other argument.</summary>
    public static bool ApplySwitch(LaunchOptions o, string arg)
    {
        switch (arg)
        {
            case "--skip-cutscenes": o.SkipCutscenes = true; return true;
            case "--mute": o.Mute = true; Muted = true; return true;
            default: return false;
        }
    }
    /// <summary>--movie NAME: play that movie from smk/ first (then the menu, or quit with --quit-after).</summary>
    public string? Movie { get; set; }
    /// <summary>--modern-physics: the player's car on the optional modern physics (docs/modern-physics.md), whatever the setting says.</summary>
    public bool ModernPhysics { get; set; }
    /// <summary>--connect HOST:PORT: join a network melee there (docs/multiplayer.md).</summary>
    public string? Connect { get; set; }
    /// <summary>--host-listen: run a network melee's server in this process and join it (--arena, --port, --fill, --teams, --max-score, --time-limit).</summary>
    public bool HostListen { get; set; }
    /// <summary>--coop MISSION with --host-listen: the listen server plays that story mission in co-op (docs/multiplayer.md "Co-op over the network") rather than a melee.</summary>
    public string? Coop { get; set; }
    /// <summary>--coop-lobby with --host-listen --coop: the game opens in its lobby, as a host form's co-op game does (the command line starts the mission at once without it).</summary>
    public bool CoopLobby { get; set; }
    /// <summary>--anet HOST[:PORT] with --host-listen: list the game on that anet server (repeatable).</summary>
    public List<string> AnetServers { get; set; } = [];
    /// <summary>--join-anet HOST[:PORT]: join a game listed there, through its introduction and the hole punch (--session NAME picks it; the shell gives the record itself).</summary>
    public string? JoinAnet { get; set; }
    public string? SessionName { get; set; }
    public I76.Net.Anet.AnetSession? AnetGame { get; set; }
    /// <summary>--name N, --car X.vcf, --team T: who joins a network melee, in what.</summary>
    public string? PlayerName { get; set; }
    public string? Car { get; set; }
    public int Team { get; set; }
    /// <summary>The listen server's arena, port, AI fill, team play, score and time limits.</summary>
    public string Arena { get; set; } = "m05";
    public int Port { get; set; } = 7676;
    public int Fill { get; set; }
    /// <summary>--bots-ai N [--bot-skill easy|normal|hard]: the listen server's bots (players the server drives by the game's mode; docs/multiplayer.md "Bots") and their skill.</summary>
    public int Bots { get; set; }
    public I76.Sim.Missions.BotSkill BotSkill { get; set; } = I76.Sim.Missions.BotSkill.Normal;
    /// <summary>--bot-car N=MODEL[:VARIANT] (repeatable, N from 1): the car of bot seat N (docs/multiplayer.md "Bots"); empty: random.</summary>
    public List<string> BotCars { get; set; } = [];
    /// <summary>--rate HZ: the listen server's simulation rate (20, 24, 25, 28, 30, 40, 60), the game's for everyone in it; 0: the Play Options' setting.</summary>
    public int Rate { get; set; }
    public bool Teams { get; set; }
    public int MaxScore { get; set; }
    public int TimeLimit { get; set; }
    /// <summary>The Nitro melee kind of the listen server (--kind combat|race|ctf; from the arena's extension if not given), its goal (--goal), the goal's value (--goal-value) and CTF's team count (--ctf-teams).</summary>
    public I76.Sim.Missions.MeleeKind MeleeKind { get; set; }
    public bool MeleeKindGiven { get; set; }
    public I76.Sim.Missions.MeleeGoal MeleeGoal { get; set; }
    public int GoalValue { get; set; }
    public int CtfTeams { get; set; } = 2;
    /// <summary>The listen server's seats (--max-players), its game's name (--game-name) and INVITE ONLY (recorded, not enforced).</summary>
    public int MaxPlayers { get; set; } = 8;
    /// <summary>The listen server's upload for the game, Mbit/s (--upload; 0: one datagram a client a tick).</summary>
    public double Upload { get; set; }
    public string? GameName { get; set; }
    public bool InviteOnly { get; set; }
    /// <summary>--password WORD: the password a listen server (--host-listen) asks of joiners, or the one given when joining (--connect, --join-anet). Never saved.</summary>
    public string? Password { get; set; }
    /// <summary>--restrict SPEC: the listen server's WEAPON RESTRICTIONS (null: none, or the host form's).</summary>
    public string? Restrict { get; set; }
    /// <summary>--hidden: the listen server allows the hidden weapons and cars (null: the setting).</summary>
    public bool? Hidden { get; set; }
    public bool? Turrets { get; set; }
    /// <summary>--share-maps / --no-share-maps: whether a listen server sends its custom arena (null: the setting).</summary>
    public bool? ShareMaps { get; set; }
    /// <summary>--car-reset / --no-car-reset: whether a listen server lets its players reset their car upright (null: the setting).</summary>
    public bool? CarReset { get; set; }
    /// <summary>--cb / --no-cb: whether a listen server has the CB radio on (null: the setting).</summary>
    public bool? Cb { get; set; }
    /// <summary>--crosshair / --no-crosshair: whether a listen server allows the chase crosshair (null: the setting).</summary>
    public bool? Crosshair { get; set; }
    /// <summary>--map-downloads ask|always|never: a host's custom map this install lacks (null: the setting).</summary>
    public int? MapDownloads { get; set; }
    /// <summary>--seed N: every mission's rand stream starts at N (spawn points, AI choices, damage rolls, debris), to reproduce a run.</summary>
    public uint? Seed { get; set; }
    /// <summary>
    /// The rand stream's fixed seed, if any: --seed N, else shell.cfg's [port] seed, else 1 for an off-screen
    /// capture (--screenshot), so a capture recipe draws the same every time. Null: the clock's, as the original.
    /// </summary>
    public uint? FixedSeed => Seed ?? I76.Game.Shell.ShellSettings.Load().Seed ?? (Screenshot is not null ? 1u : null);
    /// <summary>A network game (joined or hosted).</summary>
    public bool Network => Connect is not null || HostListen || JoinAnet is not null;
    /// <summary>A mission was named; without one the game opens on its menu.</summary>
    public bool MissionGiven { get; set; }

    /// <summary>Once, before anything reads user://: the old "Interstate '76 (Godot)" folder's contents are copied in (never moved or changed).</summary>
    private static void MigrateUserData()
    {
        try
        {
            var data = OS.GetDataDir();
            var old = System.IO.Path.Combine(data, OS.GetName() == "Linux" || OS.GetName() == "FreeBSD" ? "godot" : "Godot", "app_userdata", "Interstate '76 (Godot)");
            if (I76.Formats.UserDataMigration.Run(old, ProjectSettings.GlobalizePath("user://")))
                GD.Print($"settings and saves copied from {old}");
        }
        catch (Exception ex) { GD.PrintErr($"user data migration: {ex.Message}"); }
        try { I76.Formats.SavesMigration.Run(ProjectSettings.GlobalizePath("user://"), GD.Print); }
        catch (Exception ex) { GD.PrintErr($"saves migration: {ex.Message}"); }
    }

    /// <summary>The folder just chosen in the chooser or extracted from a GOG installer (this run): the install from then on, ahead of the search.</summary>
    public static string? Extracted;

    /// <summary>The I26 folder in the local user data (not the roaming one): Windows %LOCALAPPDATA%\I26, Linux $XDG_DATA_HOME/I26 (~/.local/share/I26).</summary>
    /// <summary>
    /// Where the machine may have another game besides the running one: I76_GARAGE_MUSIC (; separated) when set, else I76_INSTALL
    /// and I76_NITRO_INSTALL, the executable's folder and its parent, the extraction places and GOG's own. The garage radio's
    /// soundtrack and Groove's poems (CompanionSounds) both take the installs among them.
    /// </summary>
    public static List<string?> OtherGameDirs()
    {
        var dirs = new List<string?>();
        if (System.Environment.GetEnvironmentVariable("I76_GARAGE_MUSIC") is { Length: > 0 } extra) dirs.AddRange(extra.Split(';', StringSplitOptions.RemoveEmptyEntries));
        else
        {
            dirs.Add(System.Environment.GetEnvironmentVariable("I76_INSTALL"));
            dirs.Add(System.Environment.GetEnvironmentVariable("I76_NITRO_INSTALL"));
            var exeDir = I76.Formats.Installers.ExtractionPlaces.AppFolder(OS.GetExecutablePath());
            var places = new List<string?> { exeDir, exeDir is null ? null : System.IO.Path.GetDirectoryName(exeDir) };
            places.AddRange(InstallPlaces);
            dirs.AddRange(I76.Formats.Installers.GameDiscovery.Discover(places, []).Where(g => g.InstallDir is not null).Select(g => g.InstallDir));
        }
        return dirs;
    }

    /// <summary>The Interstate '76 install beside a running Nitro Pack, for Groove's poems (CompanionSounds.Locator); null: none.</summary>
    public static string? FindI76Install()
    {
        foreach (var d in OtherGameDirs().Where(d => !string.IsNullOrEmpty(d)).Distinct())
        {
            try { if (I76.Formats.Vfs.InstallSearch.IsInstall(d!) && !new I76.Formats.Vfs.GameInstall(d!).IsNitro) return d; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return null;
    }

    public static string LocalRoot()
    {
        var local = System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData);
        return System.IO.Path.Combine(local, "I26");
    }

    /// <summary>Every place an unpacked GOG installer could be, both games (ExtractionPlaces.All).</summary>
    public static List<string> ExtractedPlaces => I76.Formats.Installers.ExtractionPlaces.All(OS.GetExecutablePath(), LocalRoot());

    /// <summary>Installs GOG Galaxy / GOG's installers left on this machine (I76.Formats.Installers.GogLibrary; read only, once per run, time-boxed).</summary>
    public static List<string> GogPlaces => I76.Formats.Installers.GogLibrary.FindOnSystem(s => GD.Print(s));

    /// <summary>Where installs are looked for after the executable's folder: our extraction places, then GOG's own.</summary>
    public static List<string> InstallPlaces => [.. ExtractedPlaces, .. GogPlaces];

    public static string DefaultExtractFolder(I76.Formats.Installers.GogGame game, long bytes = 0) =>
        I76.Formats.Installers.ExtractionPlaces.Choose(game, OS.GetExecutablePath(), LocalRoot(), bytes);

    /// <summary>A game switch happened this run (<see cref="GameSwitch"/>): the command line's game choice (--install, --mission, ...) was for the first game and no longer applies; the chooser comes first.</summary>
    public static bool Switched;

    public static LaunchOptions Parse(string[] args)
    {
        MigrateUserData();
        if (Switched)
        {
            var kept = new List<string>();
            for (int i = 0; i < args.Length; i++)
                if (args[i] is "--install" or "--mission" or "--from-installer" or "--to") i++;
                else if (args[i] is "--drive" or "--melee" or "--connect" or "--host-listen" or "--movie" or "--cockpit" or "--join-anet") { if (args[i] is "--drive" or "--connect" or "--movie" or "--join-anet" && i + 1 < args.Length && !args[i + 1].StartsWith("--")) i++; }
                else kept.Add(args[i]);
            args = [.. kept];
        }
        var o = new LaunchOptions();
        for (int i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
            switch (args[i])
            {
                case "--install": o.Install = Next(); break;
                case "--choose-game": o.ChooseGame = true; break;
                case "--log": break;   // read by Boot.LogFile: the log file on for this run
                case "--from-installer": o.FromInstaller = Next(); break;
                case "--to": o.InstallerTarget = Next(); break;
                case "--mission": o.Mission = Next(); o.MissionGiven = true; break;
                case "--1997": o.Mode1997 = true; break;
                case "--cam": o.Cam = Next().Split(',').Select(s => float.Parse(s, System.Globalization.CultureInfo.InvariantCulture)).ToArray(); break;
                case "--screenshot": o.Screenshot = Next(); break;
                case "--quit-after": o.QuitAfter = int.Parse(Next()); break;
                case "--drive": o.Drive = i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[++i] : ""; break;
                case "--hold-throttle": o.HoldThrottle = true; break;
                case "--hold-fire": o.HoldFire = true; break;
                case "--no-music": o.NoMusic = true; break;
                case "--ai": o.Ai = Next(); break;
                case "--cockpit": o.Cockpit = true; break;
                case "--chase": o.Chase = true; break;
                case "--melee": o.Melee = true; break;
                case "--skip-movies": o.SkipMovies = true; break;
                case "--movie": o.Movie = Next(); break;
                case "--modern-physics": o.ModernPhysics = true; break;
                case "--connect": o.Connect = Next(); break;
                case "--host-listen": o.HostListen = true; break;
                case "--coop": o.Coop = Next(); break;
                case "--coop-lobby": o.CoopLobby = true; break;
                case "--anet": o.AnetServers.Add(Next()); break;
                case "--join-anet": o.JoinAnet = Next(); break;
                case "--session": o.SessionName = Next(); break;
                case "--password": o.Password = Next(); break;
                case "--restrict": o.Restrict = Next(); break;
                case "--hidden": o.Hidden = true; break;
                case "--turrets": o.Turrets = true; break;
                case "--name": o.PlayerName = Next(); break;
                case "--car": o.Car = Next(); break;
                case "--team": o.Team = int.Parse(Next()); break;
                case "--arena": o.Arena = Next(); break;
                case "--port": o.Port = int.Parse(Next()); break;
                case "--fill": o.Fill = int.Parse(Next()); break;
                case "--bots-ai": o.Bots = Math.Max(0, int.Parse(Next())); break;
                case "--bot-car":
                {
                    var kv = Next().Split('=', 2);
                    if (kv.Length == 2 && int.TryParse(kv[0], out int seat) && seat is >= 1 and <= 63)
                    {
                        while (o.BotCars.Count < seat) o.BotCars.Add("");
                        o.BotCars[seat - 1] = kv[1].Trim();
                    }
                    break;
                }
                case "--bot-skill": o.BotSkill = Enum.TryParse<I76.Sim.Missions.BotSkill>(Next(), true, out var bs) ? bs : I76.Sim.Missions.BotSkill.Normal; break;
                case "--rate": o.Rate = int.Parse(Next()); break;
                case "--teams": o.Teams = true; break;
                case "--invite-only": o.InviteOnly = true; break;
                case "--max-score": o.MaxScore = int.Parse(Next()); break;
                case "--time-limit": o.TimeLimit = int.Parse(Next()); break;
                case "--kind": o.MeleeKind = Enum.Parse<I76.Sim.Missions.MeleeKind>(Next(), true); o.MeleeKindGiven = true; break;
                case "--goal": o.MeleeGoal = Enum.Parse<I76.Sim.Missions.MeleeGoal>(Next(), true); break;
                case "--goal-value": o.GoalValue = int.Parse(Next()); break;
                case "--ctf-teams": o.CtfTeams = Math.Clamp(int.Parse(Next()), 2, 4); break;
                case "--max-players": o.MaxPlayers = Math.Clamp(int.Parse(Next()), 1, I76.Net.Protocol.MaxSeats); break;
                case "--upload": o.Upload = Math.Max(0, double.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture)); break;
                case "--game-name": o.GameName = Next(); break;
                case "--share-maps": o.ShareMaps = true; break;
                case "--no-share-maps": o.ShareMaps = false; break;
                case "--car-reset": o.CarReset = true; break;
                case "--no-car-reset": o.CarReset = false; break;
                case "--cb": o.Cb = true; break;
                case "--no-cb": o.Cb = false; break;
                case "--crosshair": o.Crosshair = true; break;
                case "--no-crosshair": o.Crosshair = false; break;
                case "--map-downloads":
                {
                    var v = Next();
                    o.MapDownloads = Array.FindIndex(I76.Game.Shell.ShellSettings.MapDownloadNames, n => n.Equals(v, StringComparison.OrdinalIgnoreCase)) is var md and >= 0
                        ? md : throw new ArgumentException("--map-downloads is ask, always or never");
                    break;
                }
                case "--seed": o.Seed = uint.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture); break;
                default: LaunchOptions.ApplySwitch(o, args[i]); break;
            }
        }
        var env = System.Environment.GetEnvironmentVariable("I76_INSTALL");
        if (Switched && Extracted is null) { o.ChooseGame = true; env = null; }
        // an explicit --install is taken as given (a wrong one stops the game with a message: Main._Ready); the rest must be installs
        // a folder just picked in the chooser or extracted from an installer (--from-installer, the no-install screen's) first
        bool forced = o.Install is { Length: > 0 } || !string.IsNullOrEmpty(env) || o.MissionGiven || o.FromInstaller is not null || o.Network;
        if (Extracted is { } x && I76.Formats.Vfs.InstallSearch.IsInstall(x)) o.Install = x;
        else if (forced && !o.ChooseGame)
            o.Install = o.Install is { Length: > 0 } ? o.Install
                : I76.Formats.Vfs.InstallSearch.Find(null, env, OS.GetExecutablePath(), o.Checked, InstallPlaces);
        else
        {
            // the games available: installs found as before, and GOG installers next to the executable, in its parent and in the user data
            var exeDir = I76.Formats.Installers.ExtractionPlaces.AppFolder(OS.GetExecutablePath());
            var installs = new List<string?> { exeDir, exeDir is null ? null : System.IO.Path.GetDirectoryName(exeDir) };
            installs.AddRange(InstallPlaces);
            o.Games = I76.Formats.Installers.GameDiscovery.Discover(installs, [exeDir, exeDir is null ? null : System.IO.Path.GetDirectoryName(exeDir), LocalRoot()]);
            if (o.Games.Count >= 2) o.Choose = true;
            else if (o.Games.Count == 1 && o.Games[0].InstallDir is { } only) o.Install = only;
            else if (o.Games.Count == 1) o.FromInstaller = o.Games[0].InstallerPath;
            else
            {
                I76.Formats.Vfs.InstallSearch.Find(null, null, OS.GetExecutablePath(), o.Checked, InstallPlaces);
                foreach (var f in new[] { exeDir, exeDir is null ? null : System.IO.Path.GetDirectoryName(exeDir), LocalRoot() })
                    if (!string.IsNullOrEmpty(f)) o.Checked.Add(f + "  (GOG installers: setup_*.exe)");
            }
        }
        // the mission when none is named: a01 on I76, the campaign's first on Nitro (GameInstall.DefaultMission)
        if (!o.MissionGiven && o.Install is { Length: > 0 })
        {
            try { o.Mission = new I76.Formats.Vfs.GameInstall(o.Install).DefaultMission(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return o;
    }
}
