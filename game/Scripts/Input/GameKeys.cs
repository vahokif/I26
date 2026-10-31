using Godot;
using I76.Formats.Input;

namespace I76.Game;

/// <summary>
/// The original's game keys, outside the Control Configuration: F1..F11 the
/// views, Pause, Ctrl+Q, Ctrl+P, self-destruct, F12, the backquote's mirror, `[` and `]`. A key's code is built
/// from its logical keycode (the layout picks the characters, as in the original), Echo included: the original
/// repeats game keys at the typematic rate.
/// </summary>
public static class GameKeys
{
    private static GameKeyMap? _map;
    public static string? InstallDir { get; set; }
    /// <summary>A game switch: gamekey.map is read again from the next install.</summary>
    public static void ResetForGameSwitch() { _map = null; InstallDir = null; }
    private static GameKeyMap Map => _map ??= GameKeyMap.Load(ReadInstall(), Mine());

    private const string MinePath = "user://gamekeys.cfg";

    /// <summary>
    /// The player's own rebinds of the multiplayer keys (the Control Configuration's; the port's file, laid over the install's
    /// gamekey.map, which is never written): game key id → key text. Only the keys that differ from the install's are kept.
    /// </summary>
    public static Dictionary<int, string> Mine()
    {
        var d = new Dictionary<int, string>();
        var c = new ConfigFile();
        if (c.Load(MinePath) != Error.Ok) return d;
        foreach (var (id, _, _) in GameKeyMap.Rebindable)
            if (c.GetValue("keys", id.ToString(), "") is { VariantType: Variant.Type.String } v && v.AsString() is { Length: > 0 } spec && GameKeyMap.ParseKey(spec) >= 0) d[id] = spec;
        return d;
    }

    /// <summary>The keys the multiplayer game keys are on now, id → key text (the install's gamekey.map under the player's own).</summary>
    public static Dictionary<int, string> Current() =>
        GameKeyMap.Rebindable.ToDictionary(r => r.Id, r => Map.KeyOf(r.Id) ?? "");

    /// <summary>The key a game key is on now as text ("Backspace", "Ctrl+Delete"), or null.</summary>
    public static string? KeyOf(int id) => Map.KeyOf(id);

    /// <summary>Keeps these keys (id → key text; the ones the install's file already gives are not written) and reads the map again.</summary>
    public static void SaveMine(IReadOnlyDictionary<int, string> keys)
    {
        var baseline = GameKeyMap.Load(ReadInstall());
        var c = new ConfigFile();
        foreach (var (id, spec) in keys)
            if (spec.Length > 0 && GameKeyMap.ParseKey(spec) != GameKeyMap.ParseKey(baseline.KeyOf(id) ?? "")) c.SetValue("keys", id.ToString(), spec);
        c.Save(MinePath);
        _map = null;
    }

    /// <summary>The install's own keys for the multiplayer game keys, id → key text (what Restore goes back to).</summary>
    public static Dictionary<int, string> Stock()
    {
        var m = GameKeyMap.Load(ReadInstall());
        return GameKeyMap.Rebindable.ToDictionary(r => r.Id, r => m.KeyOf(r.Id) ?? "");
    }

    private static string? ReadInstall()
    {
        if (InstallDir is not { } dir || !System.IO.Directory.Exists(dir)) return null;
        var f = System.IO.Directory.EnumerateFiles(dir).FirstOrDefault(p => System.IO.Path.GetFileName(p).Equals("gamekey.map", StringComparison.OrdinalIgnoreCase));
        return f is null ? null : System.IO.File.ReadAllText(f, System.Text.Encoding.Latin1);
    }

    /// <summary>The base code of a key (the VK table), or -1 for a key the original has no code for.</summary>
    public static int BaseOf(Key key)
    {
        if (key >= Key.A && key <= Key.Z) return 'a' + (int)(key - Key.A);
        if (key >= Key.Key0 && key <= Key.Key9) return '0' + (int)(key - Key.Key0);
        if (key >= Key.F1 && key <= Key.F15) return 0xb1 + (int)(key - Key.F1);
        return key switch
        {
            Key.Escape => GameKeyMap.Escape, Key.Pause => GameKeyMap.Pause, Key.Space => ' ', Key.Backspace => 8, Key.Tab => 9, Key.Enter => 13,
            Key.Insert or Key.Kp0 => 0xc0, Key.Delete or Key.KpPeriod => 0xc1, Key.Home or Key.Kp7 => 0xc2, Key.End or Key.Kp1 => 0xc3,
            Key.Pageup or Key.Kp9 => 0xc4, Key.Pagedown or Key.Kp3 => 0xc5,
            Key.Up or Key.Kp8 => 0xc6, Key.Down or Key.Kp2 => 0xc7, Key.Right or Key.Kp6 => 0xc8, Key.Left or Key.Kp4 => 0xc9,
            Key.KpDivide => 0xca, Key.KpMultiply => 0xcb, Key.KpSubtract => 0xcc, Key.KpAdd => 0xcd,
            Key.Quoteleft => '`', Key.Bracketleft => '[', Key.Bracketright => ']',
            // the rest goes through MapVirtualKey in the original: the character, lower-cased
            _ => (long)key is >= 0x20 and < 0x7f ? char.ToLowerInvariant((char)(long)key) : -1,
        };
    }

    /// <summary>The code of a key press, as the keyboard pump builds it, or -1.</summary>
    public static int CodeOf(InputEventKey k)
    {
        int b = BaseOf(k.Keycode != Key.None ? k.Keycode : k.PhysicalKeycode);
        return b < 0 ? -1 : GameKeyMap.Code(b, k.CtrlPressed, k.ShiftPressed, k.AltPressed);
    }

    /// <summary>The game key a press is (modifiers matched exactly), or None; repeats count.</summary>
    public static GameKey CommandOf(InputEventKey k) => k.Pressed && CodeOf(k) is >= 0 and var c ? (GameKey)Map.Lookup(c) : GameKey.None;

    /// <summary>The game key a pad button press is (the pad's stock game keys: Start is the menu key, unless the player bound that button to a command), or None; the pad must be one with a standard layout.</summary>
    public static GameKey CommandOf(InputEventJoypadButton b) =>
        b.Pressed && GodotPads.IsStandard(b.Device) && !Controls.PadButtonBound(b.ButtonIndex) ? (GameKey)GameKeyMap.PadLookup((int)b.ButtonIndex) : GameKey.None;

    /// <summary>The F-key's view command (0xe..0x19), or 0 when the game key is not a view.</summary>
    public static int ViewOf(GameKey g) => (int)g is >= 0xe and <= 0x19 ? (int)g : 0;
}
