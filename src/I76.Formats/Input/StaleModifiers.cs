namespace I76.Formats.Input;

/// <summary>
/// Shift, Control and Alt as the engine counts them against what the system says. A held command is polled: its "- Shift" and
/// "- Control" terms read the engine's key state, which only a key's own release event clears. When the system keeps that
/// release (a system shortcut or the menu bar taking the keyboard, as Control+F2 does on macOS), the modifier stays down in
/// the engine and every binding with the shell's exclusions works on the press only (a press reads the event's own flags).
/// Every key and mouse event carries the system's modifier flags of the moment, so a modifier the engine holds that a later
/// event says is up is stale, and is let go.
/// </summary>
public static class StaleModifiers
{
    [Flags]
    public enum Mods { None = 0, Shift = 1, Control = 2, Alt = 4 }

    /// <summary>
    /// The modifiers to let go: held in the engine (<paramref name="held"/>) but not in the event's flags. A modifier key's own
    /// event is left alone (its flags may or may not count the key itself).
    /// </summary>
    public static Mods Stale(Mods held, Mods eventFlags, bool modifierKeyEvent) => modifierKeyEvent ? Mods.None : held & ~eventFlags;
}
