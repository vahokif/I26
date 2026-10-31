using System.Globalization;

namespace I76.Formats.Shell;

/// <summary>
/// The melee forms' 53 A.I. driver names (the pool the shell fills), and the network game's use of them: the headless
/// bots (i26-server --bots) and the server's AI fill take their names from here, as instant melee's opponents do.
/// </summary>
public static class MeleeDrivers
{
    public static readonly string[] Names =
    [
        "Torque Daisy", "Flack Driver", "MacRill III", "Thunder Heads", "Sire Screech", "Griffin Farquar", "The Chef", "Bob Lemon",
        "Four Banger", "Highboy", "Jane Brain", "Fire Fly", "Nine Ball", "Dr. Wrinkles", "Sin Dee Racer", "Pyre Rider", "Steelwheels",
        "Buckle Knuckle", "Dualie Dasher", "Mr. Citrus", "Liquid T", "Hell Toupee", "Handsome Devil", "Nightmare Fred", "The Black Watch",
        "Sigh Clone", "Wolf Raider 1", "Wolf Raider 2", "Wolf Raider 3", "Wolf Raider 5", "Fatboy", "Gasket Head", "Giggler", "Cromax",
        "Phil Vegas", "Lillian Chill", "Moog Fuel", "Romeo Dare", "Ice Trey", "Radiator Mother", "Blue Llama", "Too Tone", "Adam Sabot",
        "Brick Baby", "Tar Mack", "Mad Hooper", "James Nicely", "Quillery", "Glytch", "Irv", "Wicked", "Redwood", "Zippy",
    ];

    /// <summary>The names in a fixed scrambled order (17 is prime to 53, so k·17 mod 53 visits each once).</summary>
    private static string Scrambled(int k) => Names[(int)((long)k % Names.Length * 17 % Names.Length)];

    /// <summary>Headless bot i's name: the scrambled order from the front.</summary>
    public static string Bot(int i) => Scrambled(i);

    /// <summary>The AI fill car in roster slot n: the scrambled order from the back, so up to 26 bots and 26 AI cars never share a name.</summary>
    public static string Fill(int n) => Scrambled(Names.Length - 1 - n % Names.Length);

    /// <summary>
    /// A network game's AI fill car is named for its script entity, "nvcl" and its roster slot in hex (MeleeGame), on every
    /// server version; the name to show for it, or null for any other name.
    /// </summary>
    public static string? ForFillEntity(string entity) =>
        entity.StartsWith("nvcl", StringComparison.Ordinal) && int.TryParse(entity.AsSpan(4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int n) && n >= 0
            ? Fill(n) : null;
}
