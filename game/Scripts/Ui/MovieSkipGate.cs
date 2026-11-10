namespace I76.Game.Ui;

/// <summary>
/// Which presses skip a movie: only a new press made while it plays. A key or button already down when the movie starts (the
/// Enter, click or pad button that started the mission, a key held from the menu) counts only after it has been let go and pressed
/// again; a press of something not seen let go is never taken for a new one. The inputs are named by the caller ("key:Escape",
/// "mouse:Left", "pad0:A"); engine-free so the rule is tested (MovieSkipGateTests).
/// </summary>
public sealed class MovieSkipGate
{
    private readonly HashSet<string> _down = [];

    /// <summary>The inputs down as the movie starts: their next press does not count until each is released.</summary>
    public void HeldAtStart(IEnumerable<string> inputs)
    {
        foreach (var i in inputs) _down.Add(i);
    }

    /// <summary>A press of <paramref name="input"/>: true when it is a new one (the input was up), the skip the player asked for.</summary>
    public bool Press(string input) => _down.Add(input);

    /// <summary>A release: the input's next press counts.</summary>
    public void Release(string input) => _down.Remove(input);

    /// <summary>
    /// The mouse buttons that skip (Godot's numbering): the left, right and middle buttons. Not the wheel (4 to 7), whose every notch,
    /// a touchpad's resting fingers included, comes as a press, nor the side buttons.
    /// </summary>
    public static bool SkipsWith(int mouseButton) => mouseButton is 1 or 2 or 3;
}
