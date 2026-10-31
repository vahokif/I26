using Godot;
using I76.Sim.Missions;

namespace I76.Game.Net;

/// <summary>
/// The modern look's network HUD style: the game's own colours (the shell's warm oranges and browns, the parchment of
/// Joe's notepad, the logo's amber) and faces read at run time and never shipped: Impact (the loading cards' and the
/// menus' face) and Arial from the HD Menu Pack or the system (HdAssets), Arial Bold standing in when there is no Impact.
/// </summary>
public static class HudStyle
{
    public static readonly Color Ink = new(0.105f, 0.063f, 0.04f);          // the panels' dark brown
    public static readonly Color Panel = new(0.105f, 0.063f, 0.04f, 0.86f);
    public static readonly Color PanelSoft = new(0.105f, 0.063f, 0.04f, 0.62f);
    public static readonly Color Edge = new(0.95f, 0.87f, 0.72f, 0.12f);
    public static readonly Color Parchment = new(0.949f, 0.886f, 0.745f);
    public static readonly Color ParchmentDim = new(0.79f, 0.70f, 0.55f);
    public static readonly Color Amber = new(1f, 0.71f, 0.18f);              // the logo's yellow
    public static readonly Color Orange = new(0.94f, 0.48f, 0.12f);
    public static readonly Color Rust = new(0.72f, 0.25f, 0.11f);
    public static readonly Color Brown = new(0.43f, 0.23f, 0.10f);
    public static readonly Color Danger = new(0.90f, 0.27f, 0.17f);
    public static readonly Color MeRow = new(1f, 0.71f, 0.18f, 0.16f);

    /// <summary>A team's colour: CTF's four as their flags (NetHud.TeamColour), the base game's eight from their names; nobody's parchment.</summary>
    public static Color Team(MeleeKind kind, int team)
    {
        if (team <= 0) return Parchment;
        if (kind == MeleeKind.Ctf) return team == 4 ? new Color(0.62f, 0.62f, 0.66f) : NetHud.TeamColour(team);
        return team switch
        {
            1 => new Color(0.66f, 0.68f, 0.72f),   // Grey Hounds
            2 => new Color(0.86f, 0.52f, 0.62f),   // Puce Panthers
            3 => new Color(0.92f, 0.22f, 0.24f),   // Crimson Kings
            4 => new Color(1f, 0.84f, 0.22f),      // Yellow Jackets
            5 => new Color(0.25f, 0.85f, 0.82f),   // Aqua Marines
            6 => new Color(0.74f, 0.52f, 0.86f),   // Mauve Mayhem
            7 => new Color(0.72f, 0.48f, 0.26f),   // Mr. Brown's Clowns
            8 => new Color(0.50f, 0.50f, 0.56f),   // Men In Black (drawn light enough to read)
            _ => Parchment,
        };
    }

    private static Font? _impact, _bold, _regular;

    /// <summary>Titles: Impact, else Arial Bold.</summary>
    public static Font Title => _impact ??= I76.Game.Shell.HdAssets.Impact(TextServer.FontAntialiasing.Gray) ?? Bold;
    public static Font Bold => _bold ??= I76.Game.Shell.HdAssets.Arial(true, TextServer.FontAntialiasing.Gray) ?? ThemeDB.FallbackFont;
    public static Font Regular => _regular ??= I76.Game.Shell.HdAssets.Arial(false, TextServer.FontAntialiasing.Gray) ?? ThemeDB.FallbackFont;
}
