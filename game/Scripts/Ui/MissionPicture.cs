using Godot;

namespace I76.Game.Ui;

/// <summary>
/// The mission map (M) and the objectives notepad (N).
/// Neither is part of the HUD: each is one still picture over the whole frame
/// while the single-player game is paused (the mission-map toggle /
/// The objectives notepad toggle, the original's bitmap draw). The map is the mission's own
/// painted map (WRLD +0x79, zmap6…), with no markers; the notepad is Groove's
/// pad (ZNPD6{v}01, v the driver variant) with the objectives written in Lee: from (82,102) on the
/// 640x480 art, 16 px a line, wrapped at 448, a blank line between objectives,
/// the hidden left out, the succeeded struck through at +7 in (50,50,50), the
/// failed unmarked, ink (16,16,16).
///
/// The original stretches the 640x480 art to the display; here it is kept 4:3
/// and centred over black (a recorded deviation), and the text is set at the
/// window's own scale. Any key closes it, and the key then acts as it would
/// (Closed hands it back): N on the map swaps to the notepad, M back, Esc goes
/// on to the escape menu.
/// </summary>
public partial class MissionPicture : Control
{
    public enum Kind { Map, Notepad }

    public required Kind What { get; init; }
    public required Texture2D Picture { get; init; }
    /// <summary>The notepad's lines: each objective's text and flags, in file order.</summary>
    public IReadOnlyList<(string Text, int Flags)> Objectives { get; init; } = [];
    public Font? Lee { get; init; }
    /// <summary>The key that closed it, to be acted on as usual.</summary>
    public required Action<InputEventKey> Closed { get; init; }

    private static readonly Color Ink = Color.Color8(16, 16, 16), Strike = Color.Color8(50, 50, 50);
    private const float Left = 82, Top = 102, Width = 448, Pitch = 16, CellEm = 14f * 1000f / 1214f;   // Lee: a 14 px cell is an 11.5 px em
    private bool _done;

    public override void _Ready()
    {
        ProcessMode = ProcessModeEnum.Always;
        // the whole window (anchors and offsets both: anchors alone left it 0x0 under its CanvasLayer)
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;
        Resized += QueueRedraw;
    }

    public override void _Input(InputEvent e)
    {
        // keyboard only: the original reads the keyboard's buffered key for the frame (the port's pad: A, B and Back close it, Start is Esc)
        if (_done) return;
        InputEventKey? k = e as InputEventKey;
        if (e is InputEventJoypadButton { Pressed: true, ButtonIndex: JoyButton.A or JoyButton.B or JoyButton.Back or JoyButton.Start } jb && GodotPads.IsStandard(jb.Device))
            k = new InputEventKey { Pressed = true, Keycode = jb.ButtonIndex == JoyButton.Start ? Key.Escape : Key.None };
        if (k is not { Pressed: true, Echo: false }) return;
        GetViewport().SetInputAsHandled();
        _done = true;
        Closed(k);
    }

    public override void _Draw()
    {
        DrawRect(new Rect2(Vector2.Zero, Size), Colors.Black);
        float s = Mathf.Min(Size.X / 640f, Size.Y / 480f);
        var o = (Size - new Vector2(640, 480) * s) / 2;
        DrawTextureRect(Picture, new Rect2(o, new Vector2(640, 480) * s), false);
        if (What != Kind.Notepad || Lee is null) return;
        int size = Mathf.Max(1, Mathf.RoundToInt(CellEm * s));
        float ascent = Lee.GetAscent(size);
        float y = Top;
        foreach (var (text, flags) in Objectives)
        {
            if ((flags & I76.Sim.Missions.Objectives.Hidden) != 0) continue;
            var lines = Wrap(text, Width * s, size);
            foreach (var line in lines)
            {
                var at = o + new Vector2(Left, y) * s;
                DrawString(Lee, at + new Vector2(0, ascent), line, HorizontalAlignment.Left, -1, size, Ink);
                if ((flags & I76.Sim.Missions.Objectives.Succeeded) != 0)
                {
                    float w = Lee.GetStringSize(line, HorizontalAlignment.Left, -1, size).X;
                    DrawLine(at + new Vector2(0, 7 * s), at + new Vector2(w, 7 * s), Strike, Mathf.Max(1f, s));
                }
                y += Pitch;
            }
            y += Pitch;   // a blank line between objectives
        }
    }

    /// <summary>A greedy word wrap at the page's width, as the original does for plain text.</summary>
    private List<string> Wrap(string text, float width, int size)
    {
        var lines = new List<string>();
        var line = "";
        foreach (var word in text.Split(' '))
        {
            var next = line.Length == 0 ? word : line + " " + word;
            if (line.Length > 0 && Lee!.GetStringSize(next, HorizontalAlignment.Left, -1, size).X > width)
            {
                lines.Add(line);
                line = word;
            }
            else line = next;
        }
        if (line.Length > 0) lines.Add(line);
        return lines;
    }
}
