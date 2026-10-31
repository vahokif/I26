using System.Text;

namespace I76.Formats.Mission;

/// <summary>
/// What a map looks like, as its WRLD chunk names it (docs/map-format-writing.md): the palette (.act), the sky's cloud texture,
/// the ground family, the horizon panel list (.hzd, WREV 9 only) and the hour (the light preset: sun or moon and its angle).
/// Each is a field of its own that the game reads on its own: stock missions mix them freely (M05 has t16_16.act with t02's .lum).
/// Names are the 13-byte NUL-padded fields; names compare without regard to case, as the files are found.
/// </summary>
public sealed record MapLook(string Palette, string Sky, string Ground, string Horizon, int Hour)
{
    public const int PaletteAt = 0x1e, SkyAt = 0x52, GroundAt = 0x6c, HorizonAt = 0x86, NameSize = 13, HourAt = 0x93;
    /// <summary>A WREV 8 WRLD (the 1997 editor's levels) is this much shorter from the horizon field on, and has none.</summary>
    private const int OldShift = WorldSettings.OldLayoutShift;

    public bool Equals(MapLook? o) => o is not null && Hour == o.Hour && Same(Palette, o.Palette) && Same(Sky, o.Sky) && Same(Ground, o.Ground) && Same(Horizon, o.Horizon);

    public override int GetHashCode() => HashCode.Combine(Palette.ToLowerInvariant(), Sky.ToLowerInvariant(), Ground.ToLowerInvariant(), Horizon.ToLowerInvariant(), Hour);

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>The look of a WRLD payload of this WREV (the horizon is empty in a WREV 8 one), or null if the chunk is too short.</summary>
    public static MapLook? Read(byte[] wrld, uint revision)
    {
        int k = revision == 8 ? OldShift : 0;
        if (wrld.Length < HourAt - k + 4) return null;
        string S(int o) => Bin.CStr(wrld, o, NameSize);
        return new MapLook(S(PaletteAt), S(SkyAt), S(GroundAt), k == 0 ? S(HorizonAt) : "", Bin.I32(wrld, HourAt - k));
    }

    public static MapLook Of(WorldSettings w) => new(w.Palette, w.Sky, w.Ground, w.Horizon, w.Hour);

    private static bool Name(string n, params string[] ext) =>
        n.Length is >= 5 and < NameSize && n.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.') && ext.Any(e => n.EndsWith(e, StringComparison.OrdinalIgnoreCase));

    /// <summary>Why a name cannot be this field's, or null: it fits the field and carries the extension the engine looks for; a ground's level digit is 1.</summary>
    public static string? Problem(string field, string name) => field switch
    {
        nameof(Palette) when !Name(name, ".act") => $"palette '{name}': an .act name of up to 12 characters",
        nameof(Sky) when !Name(name, ".map") => $"sky '{name}': a .map name of up to 12 characters",
        nameof(Ground) when !Name(name, ".map") || name.Length < 8 || name[4] != '1' => $"ground '{name}': a .map name whose fifth character is the level digit 1 (the family's largest member)",
        nameof(Horizon) when name.Length > 0 && !Name(name, ".hzd") => $"horizon '{name}': an .hzd name of up to 12 characters, or none",
        _ => null,
    };

    /// <summary>
    /// Writes this look into a WRLD payload of this WREV: a field already holding the same name (any case) is left as it is (and not checked), so
    /// a look read from a map and written back changes nothing. <paramref name="offset"/>: where the payload starts in <paramref name="wrld"/>. A WREV 8 payload has no horizon field and keeps its own.
    /// </summary>
    public void WriteTo(byte[] wrld, uint revision, int offset = 0)
    {
        if (Hour is < 0 or > 23) throw new ArgumentException($"hour {Hour}: 0 to 23");
        int k = revision == 8 ? OldShift : 0;
        if (wrld.Length < offset + HourAt - k + 4) throw new InvalidOperationException("no WRLD chunk with a look");
        void Put(int at, string field, string name)
        {
            if (Same(Bin.CStr(wrld, offset + at, NameSize), name)) return;
            if (Problem(field, name) is { } why) throw new ArgumentException(why);
            var f = wrld.AsSpan(offset + at, NameSize);
            f.Clear();
            Encoding.Latin1.GetBytes(name, f);
        }
        Put(PaletteAt, nameof(Palette), Palette); Put(SkyAt, nameof(Sky), Sky); Put(GroundAt, nameof(Ground), Ground);
        if (k == 0) Put(HorizonAt, nameof(Horizon), Horizon);
        BitConverter.TryWriteBytes(wrld.AsSpan(offset + HourAt - k, 4), Hour);
    }
}
