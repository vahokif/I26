using Godot;
using I76.Game.Shell;
using Xunit;

namespace I76.Game.Tests;

/// <summary>The host form as the port draws it (the Nitro Pack's, rebuilt): where its shapes, words and hotspots are.</summary>
public class HostFormArtTests
{
    /// <summary>The form's hotspots: the Nitro form's own table over the shared one (MeleeScreen's), for the ids the form keeps.</summary>
    private static readonly int[] Live = [3, 4, 5, 6, 7, 11, 12, 13, 14, 15, 8, 9, 22, 23, 24, 25, 27];

    private static (int X0, int X1, int Y0, int Y1) Hot(int id) => MeleeScreen.NitroHost.TryGetValue(id, out var r) ? r : MeleeScreen.Hot[id];

    private static bool Over((int X0, int X1, int Y0, int Y1) r, float x, float y) => r.X0 < x && x < r.X1 && r.Y0 < y && y < r.Y1;

    [Fact]
    public void OnlyTheNitroPacks1997LookKeepsTheArt()
    {
        Assert.True(HostFormArt.Drawn(nitro: false, look1997: false));
        Assert.True(HostFormArt.Drawn(nitro: false, look1997: true));
        Assert.True(HostFormArt.Drawn(nitro: true, look1997: false));
        Assert.False(HostFormArt.Drawn(nitro: true, look1997: true));
    }

    /// <summary>Every button, list arrow, − and + drawn is under the hotspot that answers it, so a click on what is drawn does what it says.</summary>
    [Fact]
    public void EveryControlDrawnIsUnderItsHotspot()
    {
        var hots = Live.Select(Hot).ToList();
        foreach (var (x0, y0, x1, y1, _) in HostFormArt.Buttons)
            Assert.Contains(hots, h => Over(h, (x0 + x1) / 2f, (y0 + y1) / 2f));
        foreach (var a in HostFormArt.Arrows) Assert.Contains(hots, h => Over(h, a.X + 5f, a.Y + 7f));
        Assert.True(Over(Hot(8), HostFormArt.Minus.X + 5f, HostFormArt.Minus.Y + 5f));
        Assert.True(Over(Hot(9), HostFormArt.Plus.X + 5f, HostFormArt.Plus.Y + 5f));
        // the scroll triangles and the list's rows
        Assert.True(Over(Hot(12), HostFormArt.ArrowX + 5.5f, HostFormArt.UpY + 5f));
        Assert.True(Over(Hot(13), HostFormArt.ArrowX + 5.5f, HostFormArt.DownY + 5f));
        Assert.True(Over(Hot(11), 100, HostFormArt.ListRule0 + 8));
        // and the buttons' own: GAME OPTIONS (25), PASSWORD (27), WEAPON RESTRICTIONS (24), BROADCAST GAME (6), CANCEL (15)
        foreach (var (id, word) in new[] { (25, "GAME OPTIONS"), (27, "PASSWORD"), (24, "WEAPON RESTRICTIONS"), (6, "BROADCAST GAME"), (15, "CANCEL"), (5, "CONFIGURE CHASSIS") })
        {
            var box = HostFormArt.Words.First(w => w.Text == word).Box;
            Assert.True(Over(Hot(id), box.Position.X + box.Size.X / 2f, box.Position.Y + box.Size.Y / 2f), word);
        }
    }

    /// <summary>The words sit inside what holds them: a heading in its strip, a button's word inside its border.</summary>
    [Fact]
    public void TheWordsSitInTheirStripsAndButtons()
    {
        foreach (var (text, box) in HostFormArt.Words)
        {
            Assert.True(box.Position.X >= 0 && box.End.X <= ShellFrame.W && box.Position.Y >= 0 && box.End.Y <= ShellFrame.H, text);
            var button = HostFormArt.Buttons.Where(b => b.X0 < box.Position.X && box.End.X <= b.X1 && b.Y0 < box.Position.Y && box.End.Y <= b.Y1 + 1).ToList();
            var strip = HostFormArt.Panels.Where(p => p.Strip0 <= box.Position.Y && box.End.Y <= p.Strip1 + 1).ToList();
            bool onPaper = text.EndsWith(':') || text is "WEAPONS" or "PARTS";
            Assert.True(button.Count == 1 || strip.Count == 1 || onPaper, text);
        }
        Assert.Equal(19, HostFormArt.Words.Length);
    }

    /// <summary>The shapes as vectors: all on the 640x480 frame, the panels' outlines, strips and buttons among them.</summary>
    [Fact]
    public void TheShapesAreOnTheFrame()
    {
        var shapes = new List<InkShape>();
        HostFormArt.Shapes(InkPen.Vectors(shapes));
        var frame = new Rect2(0, 0, ShellFrame.W, ShellFrame.H);
        foreach (var s in shapes)
            switch (s)
            {
                case InkRect r: Assert.True(frame.Encloses(r.R), r.ToString()); break;
                case InkPoly p: Assert.All(p.P, q => Assert.True(frame.HasPoint(q))); break;
                case InkLine l: Assert.All(l.P, q => Assert.True(frame.HasPoint(q))); break;
            }
        Assert.Equal(6, shapes.OfType<InkRect>().Count(r => r.C == FormInk.Strip));
        Assert.Equal(HostFormArt.Buttons.Length + 2 + HostFormArt.Arrows.Length, shapes.OfType<InkRect>().Count(r => r.C == FormInk.Orange && r.R.Size.Y >= 8));
    }
}
