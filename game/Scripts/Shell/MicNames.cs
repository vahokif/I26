using Godot;

namespace I76.Game.Shell;

/// <summary>
/// A microphone's name for the menus. Godot lists Linux input devices by their PulseAudio/PipeWire source name
/// ("alsa_input.usb-Blue_Microphones_Yeti_Stereo_Microphone_REV8-00.analog-stereo"): the sound server's own description
/// ("Yeti Stereo Microphone Analog Stereo") where `pactl` answers, else the name tidied.
/// </summary>
public static class MicNames
{
    private static Dictionary<string, string>? _descriptions;

    public static string Of(string device)
    {
        if (device.Length == 0) return "Default";
        if (Descriptions().TryGetValue(device, out var d) && d.Length > 0) return d;
        return Tidy(device);
    }

    /// <summary>A source name made readable: the bus and serial dropped, underscores as spaces, the profile in brackets.</summary>
    public static string Tidy(string name)
    {
        string s = name;
        foreach (var p in new[] { "alsa_input.", "alsa_output.", "bluez_input.", "bluez_source.", "bluez_output." })
            if (s.StartsWith(p, StringComparison.Ordinal)) { s = s[p.Length..]; break; }
        // the profile: the part after the last dot when it is a word like analog-stereo or mono-fallback
        string profile = "";
        int dot = s.LastIndexOf('.');
        if (dot > 0 && s[(dot + 1)..] is var tail && tail.Length > 0 && tail.All(ch => char.IsAsciiLetterLower(ch) || char.IsAsciiDigit(ch) || ch == '-') && tail.Any(char.IsAsciiLetterLower))
        {
            profile = tail.Replace('-', ' ');
            s = s[..dot];
        }
        if (s.StartsWith("pci-", StringComparison.Ordinal) || s.StartsWith("platform-", StringComparison.Ordinal)) s = "Built-in audio";
        else
        {
            foreach (var bus in new[] { "usb-", "bluetooth-" })
                if (s.StartsWith(bus, StringComparison.Ordinal)) { s = s[bus.Length..]; break; }
            // a USB device's serial and interface: "_REV8-00", "-00"
            int dash = s.LastIndexOf('-');
            if (dash > 0 && s[(dash + 1)..].All(char.IsAsciiDigit)) s = s[..dash];
            s = s.Replace('_', ' ').Trim();
        }
        return profile.Length > 0 && profile is not ("analog stereo" or "mono fallback") ? $"{s} ({profile})" : s;
    }

    // `pactl list sources`: Name and Description pairs (once a run; nothing where there is no pactl)
    private static Dictionary<string, string> Descriptions()
    {
        if (_descriptions is not null) return _descriptions;
        _descriptions = new(StringComparer.Ordinal);
        if (OS.GetName() != "Linux") return _descriptions;
        try
        {
            var output = new Godot.Collections.Array();
            if (OS.Execute("pactl", ["list", "sources"], output) != 0 || output.Count == 0) return _descriptions;
            string? name = null;
            foreach (var raw in output[0].AsString().Split('\n'))
            {
                string line = raw.Trim();
                if (line.StartsWith("Name:", StringComparison.Ordinal)) name = line[5..].Trim();
                else if (line.StartsWith("Description:", StringComparison.Ordinal) && name is not null) { _descriptions[name] = line[12..].Trim(); name = null; }
            }
        }
        catch (Exception) { }
        return _descriptions;
    }
}
