using Godot;
using I76.Formats.Shell;

namespace I76.Game.Shell;

/// <summary>
/// The network host form (modes 3 and 4) drawn by the port (<see cref="HostFormArt"/>): the Nitro Pack's form rebuilt, for both games;
/// the Nitro Pack's 1997 look keeps its art. On the sheet's paper (<see cref="PaperSheet"/>) the form's rules, strips, buttons and
/// arrows are palette pixels in the 1997 look and vectors in the modern look (<see cref="InkLayer"/>); its printed words are the art's
/// lettering (<see cref="FormLetters"/>). The values, the list and the car are where the Nitro form puts them (MeleeScreen's Draw).
/// </summary>
public sealed partial class MeleeScreen
{
    /// <summary>The host form is the port's drawing.</summary>
    private bool DrawnHost => Mode is 3 or 4 && HostFormArt.Drawn(Nitro, Shell.Faithful);
    /// <summary>The host form has the Nitro form's layout: the art's (the Nitro Pack) or the drawing's.</summary>
    private bool NitroForm => Hosting && (Nitro || DrawnHost);
    private InkLayer? _ink;

    // the paper with the form's shapes for each look, made once (its noise is the same every time)
    private static (Pcx Art, bool Faithful, Pcx Paper)? _hostPaper;

    private void RepaintDrawnHost()
    {
        var art = Shell.Db.Background(19);
        if (_hostPaper is not { } p || p.Art != art || p.Faithful != Shell.Faithful)
        {
            var paper = PaperSheet.Make(art, false, table: true);
            if (Shell.Faithful) HostFormArt.Shapes(InkPen.Pixels(paper.Pixels, paper.Palette));
            _hostPaper = p = (art, Shell.Faithful, paper);
        }
        Frame.LoadBackground(p.Paper, 0, 0, black: false);
        if (Shell.Faithful) LeaveDrawnHost();
        else if (_ink is null)
        {
            var shapes = new List<InkShape>();
            HostFormArt.Shapes(InkPen.Vectors(shapes));
            _ink = new InkLayer { Shapes = shapes, Shown = () => !Shell.OverlayOpen, Hole = ModalHole };
            Shell.AddLayerOverFrame(_ink);
        }
    }

    // the frame as the form left it, and the box a pop-up over it has changed since (the modern look's vectors keep out of it)
    private byte[]? _drawnPixels;

    private Rect2I? ModalHole()
    {
        if (!Shell.ModalOpen || _drawnPixels is not { } was) return null;
        var now = Frame.Pixels;
        int x0 = ShellFrame.W, y0 = ShellFrame.H, x1 = -1, y1 = -1;
        for (int y = 0; y < ShellFrame.H; y++)
        {
            var a = now.Slice(y * ShellFrame.W, ShellFrame.W);
            var b = was.AsSpan(y * ShellFrame.W, ShellFrame.W);
            if (a.SequenceEqual(b)) continue;
            int l = 0, r = ShellFrame.W - 1;
            while (a[l] == b[l]) l++;
            while (a[r] == b[r]) r--;
            x0 = Math.Min(x0, l); x1 = Math.Max(x1, r); y0 = Math.Min(y0, y); y1 = y;
        }
        return x1 < 0 ? null : new Rect2I(x0, y0, x1 - x0 + 1, y1 - y0 + 1);
    }

    private void LeaveDrawnHost()
    {
        _ink?.QueueFree();
        _ink = null;
    }

    /// <summary>The form's printed words (the title, the headings, the buttons' and lines' names), before its values.</summary>
    private void DrawHostWords()
    {
        var label = Shell.Db.Font(0x47);
        FormLetters.Fit(Frame, label, HostFormArt.TitleBox, HostFormArt.Title);
        foreach (var (text, box) in HostFormArt.Words) FormLetters.Fit(Frame, label, box, text);
    }

    /// <summary>After the form is drawn: what it looks like now, for <see cref="ModalHole"/>.</summary>
    private void DrawnHostDone() => _drawnPixels = Frame.Pixels.ToArray();
}

/// <summary>
/// The drawn forms' paper (<see cref="PaperSheet"/>'s, without rules) in a palette, made once per palette: a pop-up's own rectangle of it
/// is its parchment.
/// </summary>
public static class FormPaper
{
    private static readonly Dictionary<byte[], Pcx> Made = new(ReferenceEqualityComparer.Instance);

    public static Pcx For(byte[] palette)
    {
        if (Made.TryGetValue(palette, out var p)) return p;
        return Made[palette] = PaperSheet.Make(Pcx.Create(ShellFrame.W, ShellFrame.H, new byte[ShellFrame.W * ShellFrame.H], palette), false, table: true);
    }

    /// <summary>The paper over a rectangle of the frame, with the pop-ups' 6-pixel shadow right and below.</summary>
    public static void Panel(ShellFrame f, int x, int y, int w, int h)
    {
        var p = For(f.Palette);
        for (int yy = Math.Max(0, y); yy < Math.Min(ShellFrame.H, y + h); yy++)
            for (int xx = Math.Max(0, x); xx < Math.Min(ShellFrame.W, x + w); xx++)
                f.Fill(xx, yy, 1, 1, p.Pixels[yy * ShellFrame.W + xx]);
        f.Fill(x + 6, y + h, w, 6, 0x0f);
        f.Fill(x + w, y + 6, 6, h, 0x0f);
    }
}

/// <summary>
/// A drawn pop-up's shapes in the current look: palette pixels into the frame in the 1997 look, or a vector layer of its own over the
/// frame in the modern look, dropped when the pop-up goes (<see cref="Drop"/>).
/// </summary>
public sealed class PopupInk
{
    private InkLayer? _layer;

    /// <summary>A pen for the pop-up's shapes, its earlier ones (in the modern look) cleared.</summary>
    public InkPen Begin(Shell shell)
    {
        if (shell.Faithful) { Drop(); return InkPen.Into(shell.Frame); }
        var shapes = new List<InkShape>();
        if (_layer is null)
        {
            _layer = new InkLayer { Shown = () => !shell.OverlayOpen };
            shell.AddLayerOverFrame(_layer);
        }
        _layer.Shapes = shapes;
        return InkPen.Vectors(shapes);
    }

    public void Drop()
    {
        _layer?.QueueFree();
        _layer = null;
    }
}
