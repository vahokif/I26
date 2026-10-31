using I76.Formats.Exe;
using I76.Formats.Vfs;

namespace I76.Formats.Installers;

/// <summary>
/// Installs an earlier I26 unpacked from a GOG installer lack the game's executable (older versions skipped every .exe), which the
/// AI's tactics table is read from. For such a folder (it holds I26's marker) the missing executable alone is extracted from the
/// installer if one is found in the given folders; nothing else in the folder is touched. A folder with the executable costs one
/// existence check.
/// </summary>
public static class ExeRepair
{
    /// <summary>The text of the load failure without an executable, naming both ways out.</summary>
    public static string MissingMessage(GameInstall install) =>
        $"{install.Root} has no {(install.IsNitro ? "nitro.exe" : "i76.exe")}: the computer drivers' tactics are read from the game's executable. "
        + $"Put the GOG installer ({(install.IsNitro ? "setup_interstate76_nitro_pack_*.exe" : "setup_interstate76_*.exe")}) next to I26 and it is fixed the next time the game starts, "
        + $"or copy {(install.IsNitro ? "nitro.exe" : "i76.exe")} from your game folder into {install.Root}.";

    /// <summary>
    /// If <paramref name="installDir"/> is an I26 extraction without its executable and an installer for the same game is in
    /// <paramref name="installerFolders"/>, extracts just that executable. Returns true when it did. Never throws for I/O or installer problems.
    /// </summary>
    public static bool Repair(string installDir, IEnumerable<string?> installerFolders, Action<string>? log = null)
    {
        try
        {
            if (!File.Exists(Path.Combine(installDir, GogInstaller.MarkerName)) || !InstallSearch.IsInstall(installDir)) return false;
            var install = new GameInstall(installDir);
            if (TacticsTable.ExecutableOf(install) is not null) return false;
            var game = install.IsNitro ? GogGame.Nitro : GogGame.I76;
            var name = install.IsNitro ? "nitro.exe" : "i76.exe";
            if (!GameDiscovery.FindInstallers(installerFolders).TryGetValue(game, out var setup)) return false;
            var data = GogInstaller.Open(setup).ReadFile(name);
            if (data is null) return false;
            var final = Path.Combine(installDir, name);
            var part = final + ".i26part";
            File.WriteAllBytes(part, data);
            File.Move(part, final, true);
            log?.Invoke($"I26: restored {name} in {installDir} from {Path.GetFileName(setup)}");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InstallerException or InvalidDataException)
        {
            log?.Invoke($"I26: could not restore the game executable in {installDir}: {ex.Message}");
            return false;
        }
    }
}
