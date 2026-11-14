using Godot;
using I76.Formats.Shell;

namespace I76.Game.Assets;

/// <summary>
/// The HD packs found at start (HdPackDiscovery: the user folder, then beside the program; a folder one level too deep, inside the
/// folder Extract All made, or the archive left as a zip, each used where it is), scanned once and logged in one line. The menu art
/// (HdAssets) reads its pack from here.
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
                var t = System.Diagnostics.Stopwatch.StartNew();
                var user = ProjectSettings.GlobalizePath("user://");
                var exe = I76.Formats.Installers.ExtractionPlaces.AppFolder(OS.GetExecutablePath());
                var d = HdPackDiscovery.Scan(user, exe);
                GD.Print($"HD packs: {d.Summary()} [{t.Elapsed.TotalMilliseconds:0} ms]");
                return _found = d;
            }
        }
    }
}
