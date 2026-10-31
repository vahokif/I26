using I76.Formats.Input;
using Xunit;

namespace I76.Formats.Tests;

public class GameKeyMapTests
{
    private static readonly GameKeyMap Map = GameKeyMap.Load(null);

    private static int Id(string key, bool ctrl = false, bool shift = false, bool alt = false) => Map.Lookup(GameKeyMap.Code(GameKeyMap.BaseOfName(key), ctrl, shift, alt));

    [Fact]
    public void CodesAreBasePlusModifiers()
    {
        Assert.Equal(0xb1, GameKeyMap.BaseOfName("F1"));
        Assert.Equal(0x2b1, GameKeyMap.Code(0xb1, false, true, false));   // Shift+F1, per the doc
        Assert.Equal(0x1ff, GameKeyMap.BaseOfName("PAUSE"));
        Assert.Equal(0x60, GameKeyMap.BaseOfName("`"));
        Assert.Equal(0x5d, GameKeyMap.BaseOfName("]"));
        Assert.Equal(0x5b, GameKeyMap.BaseOfName("["));
    }

    [Fact]
    public void TheShippedAndHardWiredKeysAreBound()
    {
        Assert.Equal((int)GameKey.PresetView1, Id("F1"));
        Assert.Equal(0x12, Id("F11"));          // PRESET_VIEW_5
        Assert.Equal(0x19, Id("F4"));           // PRESET_VIEW_12: the target view
        Assert.Equal((int)GameKey.ExitSimFail, Id("q", ctrl: true));
        Assert.Equal((int)GameKey.SelfDestruct, Id("Delete", ctrl: true));
        Assert.Equal((int)GameKey.SelfDestruct, Id("x", ctrl: true, alt: true));
        Assert.Equal((int)GameKey.ToggleRearview, Id("`"));
        Assert.Equal((int)GameKey.PauseGame, Id("Pause"));
        Assert.Equal((int)GameKey.SaveScreen, Id("p", ctrl: true));
        Assert.Equal((int)GameKey.MainMenu, Id("Esc"));
        Assert.Equal((int)GameKey.ToggleVideoMode, Id("F12"));
        Assert.Equal((int)GameKey.Toggle2DCockpitExt, Id("["));
        Assert.Equal((int)GameKey.ToggleGhostCockpit, Id("]"));
    }

    [Fact]
    public void ModifiersMustMatchExactly()
    {
        Assert.Equal(0, Id("F1", shift: true));
        Assert.Equal(0, Id("F1", ctrl: true));      // no Ctrl+F-key has a binding
        Assert.Equal(0, Id("F2", alt: true));
        Assert.Equal(0, Id("q"));                    // plain Q is an input.map command, not a game key
        Assert.Equal(0, Id("Delete"));
        Assert.Equal(0, Id("x", ctrl: true));
    }

    [Fact]
    public void AKeyIsTextAndBack()
    {
        foreach (var k in new[] { "Backspace", "Ctrl+Delete", "Space", "Ctrl+Alt+X", "F5", "Shift+Tab", "Pause", "]", "PageUp" })
            Assert.Equal(k, GameKeyMap.KeyText(GameKeyMap.ParseKey(k)));
        Assert.Equal(GameKeyMap.ParseKey("Control+Delete"), GameKeyMap.ParseKey("Ctrl+Delete"));
        Assert.Equal(-1, GameKeyMap.ParseKey("Hyper+Q"));
    }

    [Fact]
    public void TheMultiplayerKeysShowTheirDefaults()
    {
        foreach (var (id, _, def) in GameKeyMap.Rebindable) Assert.Equal(def, Map.KeyOf(id));
    }

    [Fact]
    public void ResetCarUprightIsOnUAndRebindable()
    {
        Assert.Contains(GameKeyMap.Rebindable, r => r.Id == (int)GameKey.ResetUpright && r.Label == "Reset Car Upright" && r.Default == "U");
        Assert.Equal((int)GameKey.ResetUpright, Map.Lookup(GameKeyMap.ParseKey("U")));
        var m = GameKeyMap.Load(null, new Dictionary<int, string> { [(int)GameKey.ResetUpright] = "Home" });
        Assert.Equal((int)GameKey.ResetUpright, m.Lookup(GameKeyMap.ParseKey("Home")));
        Assert.Equal(0, m.Lookup(GameKeyMap.ParseKey("U")));
        // U is no other game key's (X is reverse), and Ctrl+Alt+X (self-destruct) is still its own
        Assert.Equal((int)GameKey.SelfDestruct, Map.Lookup(GameKeyMap.ParseKey("Ctrl+Alt+X")));
    }

    [Fact]
    public void AnOldCbTransmitLineIsIgnored()
    {
        // push-to-talk is on the Poem key now; a gamekey.map written with CB_TRANSMIT still loads and binds nothing
        var m = GameKeyMap.Load("CB_TRANSMIT                   K\nSELF_DESTRUCT                 Control+Delete\n");
        Assert.Equal(0, m.Lookup(GameKeyMap.ParseKey("K")));
        Assert.Equal((int)GameKey.SelfDestruct, m.Lookup(GameKeyMap.ParseKey("Ctrl+Delete")));
        Assert.DoesNotContain(GameKeyMap.Rebindable, r => r.Id == 0x71);
    }

    [Fact]
    public void ARebindReplacesTheKeysTheGameKeyHadAndTakesTheKeyFromAnother()
    {
        var m = GameKeyMap.Load(null, new Dictionary<int, string> { [(int)GameKey.ChatToAll] = "Ctrl+T", [(int)GameKey.SelfDestruct] = "Ctrl+K" });
        Assert.Equal((int)GameKey.ChatToAll, m.Lookup(GameKeyMap.ParseKey("Ctrl+T")));
        Assert.Equal(0, m.Lookup(GameKeyMap.ParseKey("Backspace")));         // the old key is free
        Assert.Equal(0, m.Lookup(GameKeyMap.ParseKey("Ctrl+Delete")));       // both of self-destruct's old keys left
        Assert.Equal(0, m.Lookup(GameKeyMap.ParseKey("Ctrl+Alt+X")));
        Assert.Equal((int)GameKey.SelfDestruct, m.Lookup(GameKeyMap.ParseKey("Ctrl+K")));
        Assert.Equal("Ctrl+T", m.KeyOf((int)GameKey.ChatToAll));
        Assert.Null(m.KeyOf((int)GameKey.ToggleAllNames));                  // its Ctrl+T went to chat
    }
}
