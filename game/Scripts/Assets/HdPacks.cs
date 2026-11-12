using Godot;
using I76.Formats.Shell;

namespace I76.Game.Assets;

/// <summary>
/// The HD packs found at start (<see cref="HdPackDiscovery"/>: the user folder, then the program's folder; a pack folder one level
/// too deep, inside the folder Extract All made, beside the program, or the archive left as a zip, each used where it is), scanned
/// once for the menu art (HdAssets) and the textures (HdTextures) and logged in one line.
/// </summary>
public static class HdPacks
{
    private static HdPackDiscovery? _found;
    private static readonly object Gate = new();

    public static HdPackDiscovery Found
    {
        get
        {
            if (_found is { } f) return f;
            lock (Gate)
            {
                if (_found is not null) return _found;
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var exe = I76.Formats.Installers.ExtractionPlaces.AppFolder(OS.GetExecutablePath());
                var d = HdPackDiscovery.Scan(ProjectSettings.GlobalizePath("user://"), exe);
                GD.Print($"HD packs: {d.Summary()} [{watch.ElapsedMilliseconds} ms]");
                return _found = d;
            }
        }
    }
}
