using I76.Formats.Input;

namespace I76.Game;

/// <summary>
/// The first gate of a mission's keys (Main._Input): which presses a skippable cut-scene takes before the game's own keys see
/// them. While the script asks isKeypress and holds the camera, every key and button but Esc (the skip) and Pause. Once the camera
/// is the player's again the script may go on asking for the skip key (Mondo Burger's race does to its end, about 99 s, #175): then
/// only Esc is the script's, and the rest act as ever. Engine-free so the rule is tested (CutsceneKeysTests).
/// </summary>
public static class CutsceneKeys
{
    /// <summary>Whether a press of <paramref name="key"/> (<see cref="GameKey.None"/> for one that is no game key) is swallowed.</summary>
    public static bool Swallowed(bool skipPolled, bool cameraHeld, GameKey key) =>
        skipPolled && cameraHeld && key is not (GameKey.MainMenu or GameKey.PauseGame);
}
