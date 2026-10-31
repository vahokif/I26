using Godot;
using I76.Formats.Vfs;

namespace I76.Game.Boot;

/// <summary>
/// "Switch Game" without a new process: everything of the running game that lives in statics is reset here, then the
/// scene is reloaded into the game chooser (Main shows it when <see cref="LaunchOptions.Switched"/> is set and no game is
/// picked), and from it into the other game by the normal boot path. The window, display mode and GPU device stay.
///
/// What is reset (each owner has a ResetForGameSwitch):
///   Main            the shell launch/result hand-over, NextMission, DebugCar, cheat state and typed buffer, the movies-played flag,
///                   --mission's "no shell" flag, the checkpoint and retry in memory
///   AssetVfs.Addon  the addon overlay (vehscn.vcf, melee variants): files of the old game's cars
///   Controls, GameKeys  the binding table, KEYBOARD.MAP / gamekey.map caches and install dirs (read again for the next install)
///   BootSplash      the overlay and the shown-once flag (the next game's picture is shown as at a first start)
///   LaunchOptions   Extracted cleared, Switched set (the command line's --install/--mission were for the first game)
///   Shell, EscapeMenu, MainMenu, CarNode   script frame/wait, remembered selections, car lamp materials keyed by the old textures
///   the carried menu music on the tree root, a paused tree, SimClock.Ticks
/// Kept: HitchWatch, ExitWatchdog, MusicProbe, fonts and the HD pack (HdAssets, ShellText), shaders and VFX textures, the ground
/// pack, the terrain bake cache (keyed by content), the pads, ForceFeedback (a node of the scene), shell.cfg / input.map
/// (shared by both games, as before).
/// </summary>
public static class GameSwitch
{
    /// <summary>How many switches happened this run (for the guard test and the log).</summary>
    public static int Count { get; private set; }

    public static void ResetStatics()
    {
        Main.ResetForGameSwitch();
        Ui.EscapeMenu.ResetForGameSwitch();
        Ui.MainMenu.ResetForGameSwitch();
        Shell.Shell.ResetForGameSwitch();
        Sim.CarNode.ResetForGameSwitch();
        Controls.ResetForGameSwitch();
        GameKeys.ResetForGameSwitch();
        BootSplash.ResetForGameSwitch();
        AssetVfs.Addon.Clear();
        Sim.SimClock.Ticks = null;
        LaunchOptions.Extracted = null;
        LaunchOptions.Switched = true;
        GameChooser.SwitchAvailable = true;
        Count++;
    }

    /// <summary>Resets, stops the music and goes to the chooser in this window.</summary>
    public static void ToChooser(SceneTree tree)
    {
        GD.Print("switch game: back to the chooser (same process)");
        ResetStatics();
        if (tree.Root.GetNodeOrNull(Shell.Shell.CarriedMusicName) is { } m) { tree.Root.RemoveChild(m); m.Free(); }
        tree.Paused = false;
        tree.ReloadCurrentScene();
    }
}
