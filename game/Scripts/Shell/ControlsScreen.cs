using Godot;
using I76.Formats.Input;
using I76.Formats.Shell;

namespace I76.Game.Shell;

/// <summary>
/// Control Configuration, overlay 7:
/// record 16 with the command names printed on it, each binding in font 0x44
/// beside its name — entries 1, 2 and 4..20 at x 190, 21..39 at x 486, from
/// y 47 a row apart (the font's height + 5). A left click on a row opens
/// the SELECT INPUT list (bank 0x24 sprite 16) of the keyboard's and the
/// mouse's inputs; a right click waits for the next key or button
/// ("Press any key or button..."). Either way the input leaves any other
/// command it was on. Restore loads KEYBOARD.MAP, Done writes input.map,
/// Cancel and Esc go back to the Options Menu. The screen edits the keyboard and the mouse only (a pad is the port's controller screen, opened by Calibrate, which
/// ran Windows' joystick panel in the original): an input replaces only the command's keyboard and mouse blocks, Restore loads the keys' defaults alone.
///
/// The command names are not text but part of the picture. An upscale of it
/// (the modern look) has none, so they are set again in Lee, each fitted to
/// the ink box it has in record 16 (measured off the original; the strings
/// read off it, as they are in no file), in its ink (palette 213, the darkest of its strokes).
/// </summary>
public class ControlsScreen : OverlayScreen
{
    protected override int Record => 16;
    /// <summary>The bindings being edited: a pending map handed back from the controller screen, else a copy of the saved ones.</summary>
    private BindingTable _map;
    protected ShellFont _f = null!;

    /// <summary>
    /// The multiplayer game keys being edited (Chat, Self Destruct, Respawn, Show All Names: gamekey.map's, outside input.map), game
    /// key id → key text. They are the port's: kept in user://gamekeys.cfg on Done, laid over the install's gamekey.map.
    /// </summary>
    private Dictionary<int, string> _keys;
    /// <summary>The game keys' edits that go to the controller screen and back with the table.</summary>
    private static Dictionary<int, string>? _carried;
    /// <summary>A row's entry past the commands': the multiplayer game key <c>GameKeyMap.Rebindable[entry - GameEntry]</c>.</summary>
    internal const int GameEntry = 100;
    /// <summary>How many of the game keys sit in the right column's empty rows (Chat, Self Destruct, Respawn, Show All Names); the rest are on the left's.</summary>
    internal const int RightGameKeys = 4;

    public ControlsScreen(BindingTable? pending = null)
    {
        _map = pending ?? Controls.Edit();
        _keys = pending is not null && _carried is not null ? _carried : GameKeys.Current();
        _carried = null;
    }
    protected int RowH => _f.Height + 5;
    private static int _listScroll;

    internal static readonly int[] LeftRows = [1, 2, .. Enumerable.Range(4, 17)];
    internal static readonly int[] RightRows = [.. Enumerable.Range(21, 19)];

    /// <summary>Record 16's painted command names and their ink boxes (x, y, w, h).</summary>
    internal static readonly (string Text, int X, int Y, int W, int H)[] Names =
    [
        ("Accelerate", 35, 45, 65, 12), ("Brake", 35, 62, 34, 12), ("Steer Left", 35, 81, 67, 9), ("Steer Right", 35, 98, 73, 9),
        ("Trans. Shift Left", 35, 115, 109, 9), ("Trans. Shift Right", 35, 132, 115, 10), ("Target Front Enemy", 35, 148, 124, 11),
        ("Target Next Enemy", 35, 165, 117, 11), ("Target Nearest Enemy", 35, 182, 135, 11), ("Cancel Target", 35, 199, 90, 11),
        ("Radar Zoom", 35, 217, 69, 9), ("Toggle Weapon Cam", 38, 233, 117, 11), ("Ignition", 35, 251, 50, 10), ("Map", 35, 269, 23, 9),
        ("Notepad", 35, 286, 48, 9), ("Binoculars", 35, 301, 63, 12), ("Horn", 35, 320, 28, 9), ("Emergency Brake", 34, 335, 102, 11),
        ("Headlights", 35, 354, 64, 9),
        ("Look at Dash", 333, 47, 77, 9), ("Look Left", 333, 64, 61, 9), ("Look Right", 333, 81, 67, 9), ("Look Rear", 333, 98, 60, 10),
        ("Look at Target", 333, 115, 93, 10), ("Fire", 334, 132, 24, 10), ("Cycle Weapon", 334, 148, 80, 11), ("Link Like Weapon", 333, 166, 102, 10),
        ("Fire Hardpoint 1", 334, 183, 95, 10), ("Fire Hardpoint 2", 334, 200, 100, 10), ("Fire Hardpoint 3", 334, 217, 100, 9),
        ("Fire Hardpoint 4", 334, 234, 99, 10), ("Fire Hardpoint 5", 334, 251, 100, 10), ("Use Special 1", 334, 269, 79, 10),
        ("Use Special 2", 334, 286, 83, 10), ("Use Special 3", 334, 303, 83, 9), ("Poem", 334, 320, 28, 9),
        ("Quick View Toggle", 333, 337, 113, 10), ("Toggle Reverse", 336, 355, 94, 8),
    ];

    protected override void Build()
    {
        _f = Shell.Db.Font(0x44);
        // the buttons are in the art, y 450..469
        Add(9, 450, () => "", () => { _carried = _keys; Shell.Overlay(Controls.PrimaryIsGeneric ? new JoystickScreen(_map) : new ControllerScreen(_map)); }, w: 138, h: 19);       // Calibrate: the port's controller screen
        Add(168, 450, () => "", () => { _map.ResetSide(Controls.KeyboardDefaults(), false); _keys = GameKeys.Stock(); Redraw(); }, w: 140, h: 19);    // Restore: the keys and mouse only
        Add(330, 450, () => "", () => { Controls.Save(_map); GameKeys.SaveMine(_keys); Back(); }, w: 140, h: 19);           // Done
        Add(490, 450, () => "", Back, w: 140, h: 19);                                        // Cancel
    }

    private void Back() => Shell.Options();

    protected override void Escape() => Back();

    private string Binding(int entry) => _map.KeyText(Controls.Commands[entry]);

    // the cells' room for text, inside the art's column rules (names x 35..177, bindings 177..312 and 475..612), a margin short of the rule
    internal const int NameW = 132, LeftBindW = 118, RightBindW = 122;
    internal static readonly string[] HintName = ["Left-click:", "Right-click:"], HintDo = ["choose from list", "press a key"];

    /// <summary>The text cut to a cell: the spaces round a slash dropped first, then the end replaced by dots. (The original lets a long name run on over the rule.)</summary>
    internal string Fit(string s, int max)
    {
        if (_f.Measure(s) <= max) return s;
        s = s.Replace(" / ", "/");
        if (_f.Measure(s) <= max) return s;
        while (s.Length > 1 && _f.Measure(s + "...") > max) s = s[..^1];
        return s + "...";
    }

    protected override void DrawExtra()
    {
        // the port's hint on the first two empty ruled rows, one piece to a cell: the right click is the original's only way to bind by pressing the input
        for (int k = 0; k < 2; k++)
        {
            int y = 47 + (LeftRows.Length + k) * RowH;
            Frame.Text(_f, 40, y, Fit(HintName[k], NameW), ShellFrame.OverlayInk);
            Frame.Text(_f, 190, y, Fit(HintDo[k], LeftBindW), ShellFrame.OverlayInk);
        }
        // the multiplayer keys under the right column, named as the menu names the rest (the art has the rows, empty)
        for (int k = 0; k < RightGameKeys; k++)
        {
            int y = 47 + (RightRows.Length + k) * RowH;
            Frame.Text(_f, 338, y, Fit(GameKeyMap.Rebindable[k].Label, NameW - 8), ShellFrame.OverlayInk);
            Frame.Text(_f, 486, y, Fit(_keys.GetValueOrDefault(GameKeyMap.Rebindable[k].Id, ""), RightBindW), ShellFrame.OverlayInk);
        }
        // the rest (the port's Reset Car Upright) on the left's third spare row, where the pointer to the chat key was (it is on the right)
        string Key(int id) => _keys.GetValueOrDefault(id, "");
        for (int k = RightGameKeys; k < GameKeyMap.Rebindable.Length; k++)
        {
            int y = 47 + (LeftRows.Length + 2 + k - RightGameKeys) * RowH;
            Frame.Text(_f, 40, y, Fit(GameKeyMap.Rebindable[k].Label, NameW), ShellFrame.OverlayInk);
            Frame.Text(_f, 190, y, Fit(Key(GameKeyMap.Rebindable[k].Id), LeftBindW), ShellFrame.OverlayInk);
        }
        if (Shell.Upscaled)
            foreach (var (text, x, y, w, h) in Names) Frame.Caption(new Rect2I(x, y, w, h), text, 213);
        for (int k = 0; k < LeftRows.Length; k++) Frame.Text(_f, 190, 47 + k * RowH, Fit(Binding(LeftRows[k]), LeftBindW), ShellFrame.OverlayInk);
        for (int k = 0; k < RightRows.Length; k++) Frame.Text(_f, 486, 47 + k * RowH, Fit(Binding(RightRows[k]), RightBindW), ShellFrame.OverlayInk);
    }

    /// <summary>The row under the point: the left column's hit boxes are x 177..312, the right's 475..612, each y..y+16.</summary>
    private int RowAt(Vector2I m)
    {
        int k = (m.Y - 47) / RowH;
        if (m.Y <= 47 || (m.Y - 47) % RowH >= 16) return -1;
        // the original's hit boxes are the binding's column alone (x 177..312, 475..612); the port takes the command's name too, the whole ruled row
        if (30 < m.X && m.X < 312 && k < LeftRows.Length) return LeftRows[k];
        if (330 < m.X && m.X < 612 && k < RightRows.Length) return RightRows[k];
        if (330 < m.X && m.X < 612 && k - RightRows.Length is >= 0 and var g && g < RightGameKeys) return GameEntry + g;
        if (30 < m.X && m.X < 312 && k - LeftRows.Length - 2 is >= 0 and var lg && RightGameKeys + lg < GameKeyMap.Rebindable.Length) return GameEntry + RightGameKeys + lg;
        return -1;
    }

    /// <summary>The input goes to this command and off any other.</summary>
    private void Assign(int entry, string device, string name) => _map.Assign(Controls.Commands[entry], device, name);

    protected override void ClickElsewhere(Vector2I m)
    {
        if (RowAt(m) is not (>= 0 and var entry)) return;
        Shell.Sound(82);
        if (entry >= GameEntry) { Prompt(entry); return; }
        // The device lists, headers marked with '*' and drawn without it, centred
        // the port's first entry: the list's way to the right click's wait for a key or button
        var items = new List<string> { "Press a key...", "*** keyboard ***" };
        var picks = new List<(string Device, string Name)> { ("", ""), ("", "") };
        void Add(string device, string name) { items.Add(name); picks.Add((device, name)); }
        foreach (var n in Controls.KeyboardNames.Where(n => n != "Esc")) Add("keyboard", n);
        items.Add("*** Mouse ***"); picks.Add(("", ""));
        foreach (var n in Controls.MouseNames) Add("Mouse", n);
        var s = Shell.Db.Bank(0x24)[16];
        int w = s?.Width ?? 172, h = s?.Height ?? 235;
        Shell.Modal(new ListPopup
        {
            Sprite = 16, X = 320 - w / 2, Y = 240 - h / 2, TextX = 0x16, TextY = 0x30, Visible = 9, Items = items, Font = _f,
            Up = (18, 36, 12, 25), Down = (18, 36, 204, 217), List = (16, 214, 47, 198), Cancel = (88, 159, 209, 225),
            Scroll = _listScroll,
            Picked = i =>
            {
                if (i == 0) { _promptFor = entry; return; }
                if (i >= 0 && !items[i].StartsWith('*'))
                {
                    var (dev, name) = picks[i];
                    Assign(entry, dev, name);
                }
                Redraw();
            },
            Scrolled = sc => _listScroll = sc,
        });
    }

    public override void Tick()
    {
        if (_promptFor >= 0) { int e = _promptFor; _promptFor = -1; Prompt(e); return; }
        if (Shell.RightClicked && RowAt(Shell.Mouse) is >= 0 and var entry)
        {
            Shell.Sound(82);
            Prompt(entry);
            return;
        }
        base.Tick();
    }

    private int _promptFor = -1;

    private void Prompt(int entry)
    {
        // The prompt on the blank notice, then it waits for a key or button; Esc keeps the binding
        var notice = Shell.Db.Bank(0x24)[17];
        Frame.Blit(notice, 179, 194);
        Frame.Fill(185, 286, 282, 6, 0x0f);
        Frame.Fill(461, 200, 6, 92, 0x0f);
        Frame.Text(Shell.Db.Font(0x45), 199, 230, entry >= GameEntry ? "Press a key or Ctrl+key..." : "Press any key or button...");
        if (entry >= GameEntry) ArmGame(entry - GameEntry); else Arm(entry);
    }

    /// <summary>
    /// A multiplayer game key: the next key, with the Ctrl, Shift and Alt held with it (a modifier alone waits for the key it goes
    /// with). The key goes to this action; the action that had it takes this one's old key. Esc, the mouse and a pad leave it.
    /// </summary>
    private void ArmGame(int slot) => Shell.Capture = name =>
    {
        if (name is null ? Godot.Input.IsKeyPressed(Key.Alt) : Controls.IsModifierName(name)) { ArmGame(slot); return; }
        if (name != "Esc" && Controls.KeyNamed(name) is { } key && GameKeys.BaseOf(key) is >= 0 and var b)
        {
            int code = GameKeyMap.Code(b, Godot.Input.IsKeyPressed(Key.Ctrl), Godot.Input.IsKeyPressed(Key.Shift), Godot.Input.IsKeyPressed(Key.Alt));
            var id = GameKeyMap.Rebindable[slot].Id;
            var text = GameKeyMap.KeyText(code);
            foreach (var (other, _, _) in GameKeyMap.Rebindable)
                if (other != id && _keys.GetValueOrDefault(other, "") == text) _keys[other] = _keys.GetValueOrDefault(id, "");
            _keys[id] = text;
        }
        Redraw();
    };

    private void Arm(int entry) => Shell.Capture = name =>
    {
        // the pad is the controller screen's: a pad's button here is not an input, and the wait goes on
        if (name is not null && name.StartsWith("joystick", StringComparison.OrdinalIgnoreCase)) { Arm(entry); return; }
        if (name is not null && name != "Esc") { var (dev, ch) = Controls.InputOf(name); Assign(entry, dev, ch); }
        Redraw();
    };
}
