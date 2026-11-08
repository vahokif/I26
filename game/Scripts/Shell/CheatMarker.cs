namespace I76.Game.Shell;

/// <summary>
/// The original's "cheats used" marker. It is set whenever the Play Options are read back with Unlim. Ammo, Armor or
/// Chas. Rein. on (bits 0x1c): when the in-mission options menu closes and after every return from the shell, so at
/// every mission start. Nothing but the end menu's "Turn off Cheater Options" clears it; it lasts as long as the
/// process. A win while it is set becomes the cheater's end menu: no salvage, no next scene.
/// </summary>
public static class CheatMarker
{
    public const int Bits = 0x1c;

    public static bool Set { get; private set; }

    /// <summary>The Play Options as read back: a cheat bit sets the marker; none leaves it as it was.</summary>
    public static void Read(int playOptions) { if ((playOptions & Bits) != 0) Set = true; }

    /// <summary>A Developer panel tool used in a mission (teleport, repair, God mode...): the port's, set as a cheat bit would.</summary>
    public static void Mark() => Set = true;

    /// <summary>"Turn off Cheater Options", or a fresh process (a game switch).</summary>
    public static void Clear() => Set = false;
}
