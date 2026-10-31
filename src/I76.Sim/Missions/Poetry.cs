namespace I76.Sim.Missions;

/// <summary>
/// The poetry key (input.map's POETRY, C by default): with the CB quiet, the player asks for a poem
/// (GDSGC40.WAV, priority 5), Taurus recites one of fifteen (POSTA01..15) and
/// the player signs off with one of five (GDSGC42..46), both priority 6, so
/// they follow the request and give way to anything the mission says.
///
/// The poems and the sign-offs are each a deck dealt in order and shuffled
/// again once used up, so no poem comes twice before all fifteen have been
/// heard; both are shuffled when a mission starts. The poet is
/// the script's actor labelled "taurus"; a mission without one
/// still plays the poems, from nobody. The original shuffles with qsort and a
/// comparator calling rand(), drawing on the game's one rand stream; here the
/// decks have their own generator, so a poem does not move the simulation's.
///
/// Nitro's key is another thing (the Nitro Pack's driver's next CB line): once the CB is quiet the speaking player says the next of the five lines of their driver (rltau / rljad /
/// rlske / rlnat 01..05, by the driver variant 2..5, five names per variant), priority 5,
/// from a deck shuffled at start (the driver CB-line shuffle, once from the original's start-up) and again after the fifth. The deck
/// moves on whether or not the line got on the air (a stopped CB, a dead speaker). Variant 1 (Groove, a name the
/// table does not know) indexes the bytes before the table: empty names, or a one-byte name that no file answers,
/// so it says nothing. No poem, no request, no sign-off. With Interstate '76 installed too Groove plays one of the 18 lines
/// its scripts pick from when he kills an enemy (CompanionSounds.GrooveKillLines), by the same rule: a deviation.
/// </summary>
public sealed partial class Poetry
{
    public const string RequestLine = "GDSGC40.WAV";
    private static readonly string[] AllPoems = Enumerable.Range(1, 15).Select(i => $"POSTA{i:00}.WAV").ToArray();
    private static readonly string[] AllSignOffs = Enumerable.Range(42, 5).Select(i => $"GDSGC{i}.WAV").ToArray();

    private Random _rng;
    private readonly string[] _poems = (string[])AllPoems.Clone(), _signOffs = (string[])AllSignOffs.Clone();
    private int _poem, _signOff;
    /// <summary>Nitro's deck: the driver's five lines in the order they are dealt (empty for I76 and for a driver without lines).</summary>
    private readonly string[] _lines;
    private readonly string[] _allLines;
    private int _line;
    /// <summary>The decks' seed and the poems dealt from them: what a snapshot restores the decks from (Random keeps no state it can be given).</summary>
    private int _seed, _dealt;

    public object? Poet { get; }

    /// <summary>Nitro's rule: the key makes the driver say a line of their own (<see cref="DriverLines"/>), not I76's poem.</summary>
    public bool Nitro { get; }

    /// <param name="nitroDriver">0 for I76's poems; else the Nitro driver variant (1 Groove, 2 Taurus, 3 Jade, 4 Skeeter, 5 Natty Dread).</param>
    /// <param name="i76Sounds">Interstate '76's sounds can be had (CompanionSounds): Groove then says one of his I76 kill lines.</param>
    public Poetry(object? poet, int seed, int nitroDriver = 0, bool i76Sounds = false)
    {
        Poet = poet;
        Nitro = nitroDriver > 0;
        _allLines = DriverLines(nitroDriver, i76Sounds);
        _lines = (string[])_allLines.Clone();
        _rng = Deal(seed, 0);
    }

    /// <summary>The five lines of a Nitro driver variant (the Nitro Pack's table); none for Groove (unless <paramref name="i76Sounds"/>: his I76 kill lines) and any other.</summary>
    public static string[] DriverLines(int variant, bool i76Sounds = false) => variant switch
    {
        1 when i76Sounds => I76.Formats.Audio.CompanionSounds.GrooveKillLines.Select(n => n.ToUpperInvariant() + ".WAV").ToArray(),
        2 => Lines("rltau"), 3 => Lines("rljad"), 4 => Lines("rlske"), 5 => Lines("rlnat"), _ => []
    };

    private static string[] Lines(string stem) => Enumerable.Range(1, 5).Select(i => $"{stem}{i:00}.WAV").ToArray();

    /// <summary>The decks as <paramref name="dealt"/> requests from a start with <paramref name="seed"/> leave them.</summary>
    private Random Deal(int seed, int dealt)
    {
        _seed = seed;
        _dealt = 0;
        _poem = _signOff = _line = 0;
        AllPoems.CopyTo(_poems, 0);
        AllSignOffs.CopyTo(_signOffs, 0);
        _allLines.CopyTo(_lines, 0);
        _rng = new Random(seed);
        if (Nitro) _rng.Shuffle(_lines);
        else { _rng.Shuffle(_poems); _rng.Shuffle(_signOffs); }
        while (_dealt < dealt) Next();
        return _rng;
    }

    private void Next()
    {
        if (Nitro)
        {
            _dealt++;
            if (++_line == _lines.Length) { _line = 0; _rng.Shuffle(_lines); }
            return;
        }
        _poem++; _signOff++; _dealt++;
        if (_poem == _poems.Length) { _poem = 0; _rng.Shuffle(_poems); }
        if (_signOff == _signOffs.Length) { _signOff = 0; _rng.Shuffle(_signOffs); }
    }

    /// <summary>The key: nothing unless the CB is quiet and the request gets on the air.</summary>
    public void Request(CbRadio cb, object? player)
    {
        if (!cb.Empty) return;
        if (Nitro)
        {
            // the line is taken from the deck before the radio is asked, as the driver's next CB line does
            if (_lines.Length == 0) return;
            string line = _lines[_line];
            Next();
            cb.Say(line, player, 5);
            return;
        }
        cb.Say(RequestLine, player, 5);
        if (cb.Empty) return;
        cb.Say(_poems[_poem], Poet, 6);
        cb.Say(_signOffs[_signOff], player, 6);
        Next();
    }
}
