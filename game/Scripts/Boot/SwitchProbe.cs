using Godot;
using I76.Formats.Installers;
using I76.Formats.Vfs;
using I76.Game.Shell;

namespace I76.Game.Boot;

/// <summary>
/// I76_SWITCH_TEST=DIR: the guard for "Switch Game" in-process (docs/dev-hooks.md). Lives on the root, across scene reloads, and
/// walks base menu -> base trip mission -> switch -> chooser -> Nitro menu -> Nitro mission -> switch -> chooser -> base menu,
/// checking at each stop that the install in use is the right one and that nothing of the other game is left in the statics
/// (the addon overlay, the input tables' install, the music), and saving a capture of the chooser and the menus into DIR.
/// Prints "switch-test: PASS" or "FAIL: ..." and quits with 0 or 1. Needs both games (I76_INSTALL none, scratch I76_SHOT_DATA
/// with I26/i76 and I26/nitro symlinks).
/// </summary>
public partial class SwitchProbe : Node
{
    private static bool _installed;

    public static void Install(SceneTree tree)
    {
        if (_installed) return;
        _installed = true;
        if (System.Environment.GetEnvironmentVariable("I76_SWITCH_TEST") is not { Length: > 0 } dir) return;
        System.IO.Directory.CreateDirectory(dir);
        tree.Root.CallDeferred(Node.MethodName.AddChild, new SwitchProbe { Name = "SwitchProbe", ProcessMode = ProcessModeEnum.Always, _dir = dir });
    }

    private string _dir = "";
    private int _step, _wait;
    private readonly List<string> _fails = [];
    private string? _baseDir, _nitroDir;

    private void Check(bool ok, string what)
    {
        GD.Print($"switch-test: {(ok ? "ok  " : "FAIL")} {what}");
        if (!ok) _fails.Add(what);
    }

    private void Shot(string name)
    {
        var img = GetViewport().GetTexture().GetImage();
        img.SavePng(System.IO.Path.Combine(_dir, name + ".png"));
        GD.Print($"switch-test: shot {name}");
    }

    private void Finish()
    {
        GD.Print(_fails.Count == 0 ? "switch-test: PASS" : $"switch-test: FAIL: {string.Join("; ", _fails)}");
        GetTree().Quit(_fails.Count == 0 ? 0 : 1);
    }

    private static bool Same(string? a, string? b) => a is not null && b is not null && System.IO.Path.GetFullPath(a).TrimEnd('/') == System.IO.Path.GetFullPath(b).TrimEnd('/');

    private Main? M => GetTree().CurrentScene as Main;
    private I76.Game.Shell.Shell? Sh => M?.GetNodeOrNull<I76.Game.Shell.Shell>("Shell");
    private GameChooser? Ch => M?.GetNodeOrNull<GameChooser>("Chooser");

    /// <summary>Waits n frames, then true.</summary>
    private bool Wait(int n) { if (++_wait < n) return false; _wait = 0; return true; }
    private void Next() { _step++; _wait = 0; }

    public override void _Process(double delta)
    {
        if (_fails.Count > 0 && _step < 100) { _step = 100; }
        switch (_step)
        {
            // 0: the chooser at start (--choose-game); pick the base game
            case 0:
                if (Ch is not { } c0 || GameChooser.Loading || !Wait(10)) return;
                _baseDir = c0.Games.First(g => g.Game == GogGame.I76).InstallDir;
                _nitroDir = c0.Games.First(g => g.Game == GogGame.Nitro).InstallDir;
                Check(_baseDir is not null && _nitroDir is not null, "both games installed");
                Shot("0_chooser_start");
                c0.Pick(GogGame.I76);
                Next(); break;
            // 1: the base menu
            case 1:
                if (Sh is not { } s1 || !Wait(60)) return;
                Check(!s1.Install.IsNitro && Same(M!.InstallPath, _baseDir), "base menu runs on the base install");
                Check(Same(Controls.InstallDir, _baseDir), "Controls.InstallDir is the base install");
                Shot("1_base_menu");
                // a trip mission: writes addon\vehscn.vcf (a stale entry the Nitro mission must not see)
                s1.Trip.New(); s1.Trip.Mission = 1;
                s1.LaunchTrip(s1.Trip.StockCar(s1, "vppirna1.vcf"));
                Next(); break;
            // 2: the base mission built
            case 2:
                if (M is not { MissionRunning: true } m2 || !Wait(30)) return;
                Check(Same(m2.InstallPath, _baseDir) && new GameInstall(_baseDir!).FindMission(m2.MissionName ?? "") is not null, $"base mission {m2.MissionName} built from the base install");
                Check(AssetVfs.Addon.ContainsKey("vehscn.vcf"), "the base trip car is in the addon overlay");
                Shot("2_base_mission");
                GameSwitch.ToChooser(GetTree());
                Next(); break;
            // 3: the chooser after the switch
            case 3:
                if (Ch is not { } c3 || GameChooser.Loading || !Wait(20)) return;
                Check(AssetVfs.Addon.IsEmpty, "addon overlay empty after the switch");
                Check(Controls.InstallDir is null && LaunchOptions.Extracted is null, "Controls and the extracted install forgotten");
                Check(GetTree().Root.GetNodeOrNull(I76.Game.Shell.Shell.CarriedMusicName) is null, "no carried music");
                Check(!GetTree().Paused, "tree not paused");
                Shot("3_chooser_after_switch");
                c3.Pick(GogGame.Nitro);
                Next(); break;
            // 4: the Nitro menu
            case 4:
                if (Sh is not { } s4 || !Wait(60)) return;
                Check(s4.Install.IsNitro && Same(M!.InstallPath, _nitroDir), "Nitro menu runs on the Nitro install");
                Check(Same(Controls.InstallDir, _nitroDir), "Controls.InstallDir is the Nitro install");
                Check(!AssetVfs.Addon.ContainsKey("vehscn.vcf"), "no base trip car in the Nitro overlay");
                Shot("4_nitro_menu");
                var mission = s4.Install.DefaultMission();
                s4.Launch(new ShellLaunch(mission, GameKind.Scenario, "vppirna1.vcf") { Driver = "Capture" });
                Next(); break;
            // 5: the Nitro mission built
            case 5:
                if (M is not { MissionRunning: true } m5 || !Wait(30)) return;
                Check(Same(m5.InstallPath, _nitroDir) && new GameInstall(_nitroDir!).IsNitro && new GameInstall(_nitroDir!).FindMission(m5.MissionName ?? "") is not null, $"Nitro mission {m5.MissionName} built from the Nitro install");
                Shot("5_nitro_mission");
                GameSwitch.ToChooser(GetTree());
                Next(); break;
            // 6: back to the base game
            case 6:
                if (Ch is not { } c6 || GameChooser.Loading || !Wait(20)) return;
                Shot("6_chooser_after_second_switch");
                c6.Pick(GogGame.I76);
                Next(); break;
            case 7:
                if (Sh is not { } s7 || !Wait(60)) return;
                Check(!s7.Install.IsNitro && Same(M!.InstallPath, _baseDir) && Same(Controls.InstallDir, _baseDir), "base menu again on the base install");
                Shot("7_base_menu_again");
                Finish();
                _step = 200; break;
            case 100: Finish(); _step = 200; break;
        }
    }
}
