namespace I76.Sim.Session;

/// <summary>The typed cheat words, in the order the original tests them.</summary>
public enum Cheat
{
    None,
    /// <summary>The software renderer's double-vision wobble, on or off.</summary>
    WiggleBurger,
    /// <summary>Toggles a flag nothing reads: no effect.</summary>
    Freelance,
    /// <summary>The mission script stops and everyone attacks the player; the mission is won 5 s after the player dies.</summary>
    GetDown,
    /// <summary>The player's front-left tyre blows.</summary>
    FlatFrontLeft,
    /// <summary>The front-right tyre.</summary>
    FlatFrontRight,
    /// <summary>The rear-left tyre.</summary>
    FlatBackLeft,
    /// <summary>The rear-right tyre.</summary>
    FlatBackRight,
    /// <summary>Calls an empty function: no effect.</summary>
    ThirdNostril,
}

/// <summary>
/// The typed-word matcher. Every game-key code whose modifier byte is exactly Ctrl+Shift (0x300; with
/// Alt as well it is 0x700 and ignored) shifts its low byte into a 15-byte buffer, and a word matches when the buffer
/// ends with it. Other keys neither enter nor clear the buffer, nothing times it out, and it lives for the program
/// (a global the original never clears), so the letters can be spread over several holds of Ctrl+Shift.
/// </summary>
public sealed class CheatTyper
{
    /// <summary>The words, in the order of the tests; the first that matches acts.</summary>
    public static readonly (string Word, Cheat Cheat)[] Words =
    [
        ("wiggleburger", Cheat.WiggleBurger), ("freelance", Cheat.Freelance), ("getdown", Cheat.GetDown),
        ("flflat", Cheat.FlatFrontLeft), ("frflat", Cheat.FlatFrontRight), ("blflat", Cheat.FlatBackLeft), ("brflat", Cheat.FlatBackRight),
        ("thirdnostril", Cheat.ThirdNostril),
    ];

    /// <summary>The cheat option flags.</summary>
    private readonly byte[] _buf = new byte[15];

    /// <summary>A game-key code (base | 0x100 Ctrl | 0x200 Shift | 0x400 Alt): the cheat it completes, or None.</summary>
    public Cheat Push(int code)
    {
        if ((code & 0xff00) != 0x300) return Cheat.None;
        Array.Copy(_buf, 1, _buf, 0, _buf.Length - 1);
        _buf[^1] = (byte)code;
        foreach (var (word, cheat) in Words)
            if (EndsWith(word)) return cheat;
        return Cheat.None;
    }

    private bool EndsWith(string word)
    {
        for (int i = 0; i < word.Length; i++)
            if (_buf[_buf.Length - 1 - i] != word[word.Length - 1 - i]) return false;
        return true;
    }

    /// <summary>The sim command a cheat sends with the next tick (the ones that change the simulation), or None.</summary>
    public static CarCommands Command(Cheat c) => c switch
    {
        Cheat.GetDown => CarCommands.CheatGetDown,
        Cheat.FlatFrontRight => CarCommands.CheatFlatFR, Cheat.FlatFrontLeft => CarCommands.CheatFlatFL,
        Cheat.FlatBackRight => CarCommands.CheatFlatRR, Cheat.FlatBackLeft => CarCommands.CheatFlatRL,
        _ => CarCommands.None,
    };

    /// <summary>The flat-tyre commands' damage slots (0 FR, 1 FL, 4 RR, 5 RL), in the original's test order.</summary>
    public static readonly (CarCommands Command, int Slot)[] Tyres =
        [(CarCommands.CheatFlatFL, 1), (CarCommands.CheatFlatFR, 0), (CarCommands.CheatFlatRL, 5), (CarCommands.CheatFlatRR, 4)];
}
