using System.Text;

namespace I76.Formats.Input;

/// <summary>
/// One command's block of input.map: the first "+ device key" term, and the
/// "- device key" terms that must NOT be down for the chord to count (the
/// shell writes "- Keyboard Shift" and "- Keyboard Control" after every
/// keyboard binding: the input poll).
/// </summary>
public sealed record InputBinding(string Device, string Key, string[] Exclude);

/// <summary>One "+" or "-" line of a block: "+" must be held, "-" must not be (device and channel as the file spells them).</summary>
public sealed record InputTerm(bool Plus, string Device, string Channel);

/// <summary>One block of input.map as it stands in the file: the command and every term, in order.</summary>
public sealed record InputBlock(string Command, InputTerm[] Terms);

/// <summary>input.map's text, as the shell reads and writes it.</summary>
public static class InputMapFile
{
    public const string Header = "# Interstate '76 Input mapping file";

    /// <summary>The command's blocks by name; the first "+" term is the binding, every "-" term is kept as an exclusion.</summary>
    public static Dictionary<string, InputBinding> Parse(string text)
    {
        var map = new Dictionary<string, InputBinding>(StringComparer.OrdinalIgnoreCase);
        string? block = null;
        string[]? plus = null;
        var minus = new List<string>();
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var parts = line.Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (block is null)
            {
                if (parts.Length >= 2 && parts[1] == "{") { block = parts[0]; plus = null; minus.Clear(); }
                continue;
            }
            if (parts[0] == "}")
            {
                if (plus is not null && !map.ContainsKey(block)) map[block] = new InputBinding(plus[0], plus[1], [.. minus]);
                block = null;
                continue;
            }
            if (parts[0] == "+" && parts.Length >= 3) plus ??= [parts[1], parts[2]];
            else if (parts[0] == "-" && parts.Length >= 3) minus.Add(parts[2]);
        }
        return map;
    }

    /// <summary>
    /// Every block of the file, in order, every term kept (a command may have several blocks: each is a way to do it).
    /// Blocks end at a line that starts with "}"; the term line is a sign, a device and a channel.
    /// </summary>
    public static List<InputBlock> ParseBlocks(string text)
    {
        var list = new List<InputBlock>();
        string? block = null;
        var terms = new List<InputTerm>();
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (block is null)
            {
                var h = line.Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                if (h.Length >= 2 && h[1] == "{") { block = h[0]; terms.Clear(); }
                else if (h.Length == 1 && h[0].EndsWith('{') && h[0].Length > 1) { block = h[0][..^1]; terms.Clear(); }
                continue;
            }
            if (line[0] == '}')
            {
                if (terms.Count > 0) list.Add(new InputBlock(block, [.. terms]));
                block = null;
                continue;
            }
            if (line[0] is '+' or '-')
            {
                var parts = line[1..].Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2) terms.Add(new InputTerm(line[0] == '+', parts[0], parts[1]));
            }
        }
        return list;
    }

    /// <summary>The keys the shell does not write exclusions for: a Shift or Control binding itself.</summary>
    public static bool IsModifierKey(string key) =>
        key.Equals("Shift", StringComparison.OrdinalIgnoreCase) || key.Equals("Control", StringComparison.OrdinalIgnoreCase)
        || key.EndsWith("Shift", StringComparison.OrdinalIgnoreCase) || key.EndsWith("Control", StringComparison.OrdinalIgnoreCase);

    /// <summary>The shell's rule: a keyboard binding that is not a glance (or its twin) and not Shift/Control itself gets the two exclusions.</summary>
    public static bool Excludes(string command, string device, string key) =>
        device.Equals("keyboard", StringComparison.OrdinalIgnoreCase) && !IsModifierKey(key)
        && !command.StartsWith("pilot_glance_", StringComparison.OrdinalIgnoreCase)
        && !command.StartsWith("track_yaw_", StringComparison.OrdinalIgnoreCase) && !command.StartsWith("track_pitch_", StringComparison.OrdinalIgnoreCase)
        && !command.StartsWith("overview_x_", StringComparison.OrdinalIgnoreCase) && !command.StartsWith("overview_z_", StringComparison.OrdinalIgnoreCase);

    public static readonly string[] ShellExclusions = ["Shift", "Control"];

    /// <summary>
    /// The file the shell writes: each block with its exclusions (the ones it
    /// carries), then input.def verbatim, the blocks it holds not written twice.
    /// </summary>
    public static string Write(IReadOnlyDictionary<string, InputBinding> map, string? inputDef = null)
    {
        var inDef = inputDef is null ? [] : Parse(inputDef).Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sb = new StringBuilder(Header).Append("\n\n");
        foreach (var (cmd, b) in map)
        {
            if (inDef.Contains(cmd)) continue;
            sb.Append(cmd).Append(" {\n   + ").Append(b.Device.PadRight(10)).Append(' ').Append(b.Key).Append('\n');
            foreach (var x in b.Exclude) sb.Append("\t- Keyboard\t").Append(x).Append('\n');
            sb.Append("}\n");
        }
        if (inputDef is not null) sb.Append(inputDef.Replace("\r", "").TrimEnd('\n')).Append('\n');
        return sb.ToString();
    }

    /// <summary>The binding with the shell's exclusions, as Save writes it.</summary>
    public static InputBinding WithShellRule(string command, string device, string key) =>
        new(device, key, Excludes(command, device, key) ? ShellExclusions : []);

    /// <summary>
    /// A file this port wrote before the exclusions: its header, and no "-" line anywhere. The rule is applied
    /// to it on load (the file itself is rewritten on the next Save only).
    /// </summary>
    public static bool NeedsMigration(string text) =>
        text.TrimStart().StartsWith(Header, StringComparison.Ordinal)
        && !text.Replace("\r", "").Split('\n').Any(l => l.TrimStart().StartsWith('-'));

    public static Dictionary<string, InputBinding> Migrate(Dictionary<string, InputBinding> map, string? inputDef = null)
    {
        var inDef = inputDef is null ? [] : Parse(inputDef).Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var r = new Dictionary<string, InputBinding>(StringComparer.OrdinalIgnoreCase);
        foreach (var (c, b) in map) r[c] = inDef.Contains(c) ? b : WithShellRule(c, b.Device, b.Key);
        return r;
    }
}
