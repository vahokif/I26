using Godot;

namespace I76.Game.Boot;

/// <summary>The release's version (project.godot's application/config/version, stamped by tools/release.sh as "1.2 (3e3e5b5)", the short commit and "-dirty" for local changes; "dev" without) and the network protocol's.</summary>
public static class BuildInfo
{
    public static string Version
    {
        get
        {
            var v = (string)ProjectSettings.GetSetting("application/config/version", "");
            return string.IsNullOrWhiteSpace(v) ? "dev" : v;
        }
    }

    /// <summary>For the menu's corner: "I26 1.2 (3e3e5b5)  net 5".</summary>
    public static string Line => $"I26 {Version}  net {I76.Net.Protocol.Version}";
}
