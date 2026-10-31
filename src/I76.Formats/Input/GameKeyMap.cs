namespace I76.Formats.Input;

/// <summary>The game-key ids of the original's game-key dispatch, the ones the port acts on or knows.</summary>
public enum GameKey
{
    None = 0,
    SaveScreen = 0xa, ToggleRearview = 0xb, ToggleGhostCockpit = 0xc,
    /// <summary>PRESET_VIEW_n is 0xd + n: F1 is 0xe (cockpit) ... F11 is 0x12.</summary>
    PresetView1 = 0xe,
    MainMenu = 0x22, SelfDestruct = 0x25, RaiseDead = 0x26, ChatToAll = 0x27, ToggleAllNames = 0x28,
    InviteAccept = 0x29, InviteDecline = 0x2a, InviteSkip = 0x2b, Boot1 = 0x2c,
    PauseGame = 0x33, ExitSimFail = 0x34, ExitSimSucceed = 0x35,
    ToggleFramerate = 0x54, ToggleVideoMode = 0x5c, Toggle2DCockpitExt = 0x5d,
    /// <summary>The port's own game key (no case in the original's dispatch; U): the car back on the terrain under it, upright and still.</summary>
    ResetUpright = 0x70,
}

/// <summary>
/// The original's game keys: a 16-bit code = key base | 0x100 Ctrl | 0x200 Shift
/// | 0x400 Alt, looked up in a table of the hard-wired defaults and then <c>gamekey.map</c> (the key-map loader). The
/// modifiers must match exactly.
/// </summary>
public sealed class GameKeyMap
{
    public const int Ctrl = 0x100, Shift = 0x200, Alt = 0x400;
    public const int Pause = 0x1ff, Escape = 0x1b;

    private readonly Dictionary<int, int> _codes = [];

    public static int Code(int baseCode, bool ctrl, bool shift, bool alt) => baseCode | (ctrl ? Ctrl : 0) | (shift ? Shift : 0) | (alt ? Alt : 0);

    /// <summary>The key's base code (VK table) from its name in gamekey.map, or -1.</summary>
    public static int BaseOfName(string name)
    {
        if (name.Length == 1) return char.ToLowerInvariant(name[0]);
        var n = name.ToLowerInvariant();
        if (n[0] == 'f' && int.TryParse(n.AsSpan(1), out int f) && f is >= 1 and <= 15) return 0xb0 + f;
        return n switch
        {
            "esc" or "escape" => Escape, "pause" => Pause, "space" => ' ', "backspace" => 8, "tab" => 9, "enter" => 13,
            "insert" or "ins" => 0xc0, "delete" or "del" => 0xc1, "home" => 0xc2, "end" => 0xc3,
            "pageup" or "pgup" => 0xc4, "pagedown" or "pgdn" => 0xc5,
            "up" or "uparrow" => 0xc6, "down" or "downarrow" => 0xc7, "right" or "rightarrow" => 0xc8, "left" or "leftarrow" => 0xc9,
            _ => -1,
        };
    }

    /// <summary>The shipped gamekey.map's lines, the fallback when the install has none (the release's own defaults).</summary>
    public const string Shipped = """
        TOGGLE_REARVIEW               `
        TOGGLE_GOSTPIT_DETAIL         ]
        TOGGLE_2DGPIT_EXT             [
        PRESET_VIEW_1                 F1
        PRESET_VIEW_2                 F2
        PRESET_VIEW_3                 F3
        PRESET_VIEW_12                F4
        PRESET_VIEW_11                F5
        PRESET_VIEW_4                 F6
        PRESET_VIEW_6                 F7
        PRESET_VIEW_7                 F8
        PRESET_VIEW_8                 F9
        PRESET_VIEW_10                F10
        PRESET_VIEW_5                 F11
        TOGGLE_VIDEOMODE              F12
        SAVE_SCREEN                   Control+P
        PAUSE_GAME                    PAUSE
        SELF_DESTRUCT                 Control+ALT+X
        SELF_DESTRUCT                 Control+Delete
        TOGGLE_FRAMERATE              ALT+F
        """;

    private static readonly Dictionary<string, int> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SAVE_SCREEN"] = 0xa, ["TOGGLE_REARVIEW"] = 0xb, ["TOGGLE_GOSTPIT_DETAIL"] = 0xc,
        ["MAIN_MENU"] = 0x22, ["SELF_DESTRUCT"] = 0x25, ["RAISE_DEAD"] = 0x26, ["CHAT_SENDTOALL"] = 0x27, ["TOGGLE_ALL_NAMES"] = 0x28,
        ["INVITE_ACCEPT"] = 0x29, ["INVITE_DECLINE"] = 0x2a, ["INVITE_SKIP"] = 0x2b,
        ["PAUSE_GAME"] = 0x33, ["EXIT_SIM_FAIL"] = 0x34, ["EXIT_SIM_SUCCEED"] = 0x35,
        ["RESET_UPRIGHT"] = 0x70, ["TOGGLE_FRAMERATE"] = 0x54, ["TOGGLE_VIDEOMODE"] = 0x5c, ["TOGGLE_2DGPIT_EXT"] = 0x5d,
        // dead in the release (no case in the dispatch): kept so that their keys count as bound
        ["TOGGLE_ARCADE_MODE"] = 0x5a, ["TOGGLE_USER_DAMAGE"] = 0x5b,
    };

    private static int IdOfName(string name)
    {
        if (Names.TryGetValue(name, out int id)) return id;
        // PRESET_VIEW_n = 0xd + n
        if (name.StartsWith("PRESET_VIEW_", StringComparison.OrdinalIgnoreCase) && int.TryParse(name.AsSpan(12), out int n) && n is >= 1 and <= 12) return 0xd + n;
        if (name.StartsWith("BOOT_", StringComparison.OrdinalIgnoreCase) && int.TryParse(name.AsSpan(5), out int b) && b is >= 1 and <= 7) return 0x2b + b;
        return 0;
    }

    private readonly List<int> _order = [];

    private void Bind(int baseCode, bool ctrl, bool shift, bool alt, int id) => BindCode(Code(baseCode, ctrl, shift, alt), id);

    private void BindCode(int code, int id)
    {
        if (!_codes.ContainsKey(code)) _order.Add(code);
        _codes[code] = id;
    }

    /// <summary>
    /// The game keys a player may rebind in the Control Configuration (the port's: gamekey.map is the install's, so a change is kept in
    /// the port's own file and laid over it): the id, the label and the key the original gives it.
    /// </summary>
    public static readonly (int Id, string Label, string Default)[] Rebindable =
    [
        (0x27, "Chat (online)", "Backspace"), (0x25, "Self Destruct", "Ctrl+Delete"),
        (0x26, "Respawn", "Space"), (0x28, "Show All Names", "Ctrl+T"),
        (0x70, "Reset Car Upright", "U"),
    ];

    /// <summary>The code of a key as gamekey.map or the port's file writes it ("Backspace", "Ctrl+Delete", "Control+Alt+X"), or -1.</summary>
    public static int ParseKey(string spec)
    {
        bool ctrl = false, shift = false, alt = false;
        var terms = spec.Length > 1 ? spec.Split('+') : [spec];
        for (int i = 0; i < terms.Length - 1; i++)
        {
            var t = terms[i].Trim();
            if (t.Equals("control", StringComparison.OrdinalIgnoreCase) || t.Equals("ctrl", StringComparison.OrdinalIgnoreCase)) ctrl = true;
            else if (t.Equals("alt", StringComparison.OrdinalIgnoreCase)) alt = true;
            else if (t.Equals("shift", StringComparison.OrdinalIgnoreCase)) shift = true;
            else return -1;
        }
        int b = BaseOfName(terms[^1].Trim());
        return b < 0 ? -1 : Code(b, ctrl, shift, alt);
    }

    /// <summary>A code as text, the inverse of <see cref="ParseKey"/> ("Ctrl+Delete").</summary>
    public static string KeyText(int code)
    {
        if (code == Pause) return "Pause";
        int b = code & ~(Ctrl | Shift | Alt);
        string name = b switch
        {
            Escape => "Esc", ' ' => "Space", 8 => "Backspace", 9 => "Tab", 13 => "Enter",
            0xc0 => "Insert", 0xc1 => "Delete", 0xc2 => "Home", 0xc3 => "End", 0xc4 => "PageUp", 0xc5 => "PageDown",
            0xc6 => "Up", 0xc7 => "Down", 0xc8 => "Right", 0xc9 => "Left",
            >= 0xb1 and <= 0xbf => $"F{b - 0xb0}",
            >= 'a' and <= 'z' => ((char)b).ToString().ToUpperInvariant(),
            _ => ((char)b).ToString(),
        };
        return ((code & Ctrl) != 0 ? "Ctrl+" : "") + ((code & Shift) != 0 ? "Shift+" : "") + ((code & Alt) != 0 ? "Alt+" : "") + name;
    }

    /// <summary>The key a game key is on now (the last one bound, for a key with two), as text, or null when unbound.</summary>
    public string? KeyOf(int id) => _order.LastOrDefault(c => _codes[c] == id) is var c && c != 0 ? KeyText(c) : null;

    /// <summary>
    /// The key-map loader: the hard-wired defaults (Esc, Ctrl+Q, Space, Backspace, Ctrl+T, Ctrl+Y/N/H, Ctrl+1..7), then the
    /// file's lines ("NAME  key" or "NAME  Control+Alt+key"; the later line wins).
    /// </summary>
    public static GameKeyMap Load(string? gamekeyMap, IReadOnlyDictionary<int, string>? mine = null)
    {
        var m = new GameKeyMap();
        m.Bind(Escape, false, false, false, 0x22);
        m.Bind('q', true, false, false, 0x34);
        m.Bind(' ', false, false, false, 0x26);
        m.Bind(8, false, false, false, 0x27);
        m.Bind('t', true, false, false, 0x28);
        m.Bind('u', false, false, false, 0x70);   // the port's RESET_UPRIGHT
        m.Bind('y', true, false, false, 0x29); m.Bind('n', true, false, false, 0x2a); m.Bind('h', true, false, false, 0x2b);
        for (int i = 1; i <= 7; i++) m.Bind('0' + i, true, false, false, 0x2b + i);
        foreach (var raw in (gamekeyMap ?? Shipped).Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var parts = line.Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;
            int id = IdOfName(parts[0]);
            if (id == 0) continue;
            bool ctrl = false, shift = false, alt = false;
            int baseCode = -1;
            var terms = parts[1].Length > 1 ? parts[1].Split('+') : [parts[1]];
            for (int i = 0; i < terms.Length; i++)
            {
                var t = terms[i];
                if (i < terms.Length - 1)
                {
                    if (t.Equals("control", StringComparison.OrdinalIgnoreCase) || t.Equals("ctrl", StringComparison.OrdinalIgnoreCase)) ctrl = true;
                    else if (t.Equals("alt", StringComparison.OrdinalIgnoreCase)) alt = true;
                    else if (t.Equals("shift", StringComparison.OrdinalIgnoreCase)) shift = true;
                    else baseCode = -1;
                }
                else baseCode = BaseOfName(t);
            }
            if (baseCode >= 0) m.Bind(baseCode, ctrl, shift, alt, id);
        }
        // the port's own rebinds over them: a game key takes the key given and leaves the ones it had; the key leaves any other game key
        if (mine is not null)
            foreach (var (id, spec) in mine)
            {
                if (ParseKey(spec) is not (>= 0 and var code)) continue;
                foreach (var old in m._order.Where(c => m._codes[c] == id).ToList()) { m._codes.Remove(old); m._order.Remove(old); }
                m.BindCode(code, id);
            }
        return m;
    }

    /// <summary>
    /// The pad's stock game keys (the port's; the game-keys layer, outside input.map like Esc): a standard button to the game key it
    /// presses. Start is the menu key, so it opens the escape menu and skips a cut-scene as Esc does. No pad preset binding uses it.
    /// </summary>
    public static int PadLookup(int padButton) => padButton == PadButton.Start ? 0x22 : 0;

    /// <summary>The game-key id of a code, or 0 (unbound: Shift+F1, or any Ctrl+F-key).</summary>
    public int Lookup(int code) => _codes.TryGetValue(code, out int id) ? id : 0;
}
