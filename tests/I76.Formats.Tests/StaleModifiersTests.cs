using I76.Formats.Input;
using Xunit;
using static I76.Formats.Input.StaleModifiers;

namespace I76.Formats.Tests;

/// <summary>A modifier the engine still holds after the system kept its release: held driving keys with the shell's exclusions worked on the press only.</summary>
public class StaleModifiersTests
{
    private const string Map = "throttle_up {\n   + keyboard   W\n\t- Keyboard\tShift\n\t- Keyboard\tControl\n}\n";

    private static bool ThrottleHeld(Mods engine, bool wDown)
    {
        var t = BindingTable.Parse(Map);
        var bank = new JoystickBank(new NoPads());
        return t.Of("throttle_up").Any(b => InputChord.Holds(b.Terms, bank, x => x.Channel switch
        {
            "W" => wDown,
            "Shift" => engine.HasFlag(Mods.Shift),
            "Control" => engine.HasFlag(Mods.Control),
            _ => false,
        }));
    }

    [Fact]
    public void AStuckControlBlocksTheHeldKeyUntilLetGo()
    {
        // Control+F2 on macOS: the menu bar takes the keyboard and Control's release never reaches the game
        var engine = Mods.Control;
        Assert.False(ThrottleHeld(engine, wDown: true));
        // W's press (or a repeat, or the mouse moving) says no modifier is down: Control is stale
        engine &= ~Stale(engine, Mods.None, modifierKeyEvent: false);
        Assert.Equal(Mods.None, engine);
        Assert.True(ThrottleHeld(engine, wDown: true));
    }

    [Fact]
    public void AModifierReallyHeldStays()
    {
        Assert.Equal(Mods.None, Stale(Mods.Shift, Mods.Shift, false));
        Assert.Equal(Mods.Control, Stale(Mods.Shift | Mods.Control, Mods.Shift, false));
        Assert.False(ThrottleHeld(Mods.Shift, wDown: true));     // Shift+W is not the throttle, as the original's
    }

    [Fact]
    public void AModifierKeysOwnEventIsLeftAlone() =>
        Assert.Equal(Mods.None, Stale(Mods.Shift | Mods.Control, Mods.None, modifierKeyEvent: true));
}

file sealed class NoPads : IPadSource
{
    public IReadOnlyList<PadRaw> Read() => [];
}
