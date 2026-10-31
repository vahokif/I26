namespace I76.Formats.Shell;

/// <summary>
/// What the shell's text fields take from a key event: the character the keyboard layout printed (the event's Unicode),
/// folded to the original font's set (printable ASCII; the fonts carry no accents or typographic marks).
/// A key event that carries no Unicode (some Windows paths deliver the character apart from the key) falls back to the
/// key's printed label, shifted as a US keyboard would.
/// </summary>
public static class TextEntry
{
    // U+00C0.. : Latin-1 and Latin Extended-A letters by base letter ('\0': none)
    private const string Latin =
        "AAAAAAACEEEEIIII" + "DNOOOOO\0OUUUUYTs" + "aaaaaaaceeeeiiii" + "dnooooo\0ouuuuyty" +
        "AaAaAaCcCcCcCcDd" + "DdEeEeEeEeEeGgGg" + "GgGgHhHhIiIiIiIi" + "IiIiJjKkkLlLlLlL" + "lLlNnNnNnnNnOoOo" + "OoOoRrRrRrSsSsSs" + "SsTtTtTtUuUuUuUu" + "UuUuWwYyYZzZzZzs";

    /// <summary>The ASCII character for a typed code point, or 0 when the font has nothing sensible for it.</summary>
    public static char Fold(int cp)
    {
        if (cp is >= 32 and < 127) return (char)cp;
        switch (cp)
        {
            case 0x2018 or 0x2019 or 0x201A or 0x2032 or 0x00B4 or 0x0060: return '\'';
            case 0x201C or 0x201D or 0x201E or 0x2033 or 0x00AB or 0x00BB: return '"';
            case 0x2010 or 0x2011 or 0x2012 or 0x2013 or 0x2014 or 0x2212: return '-';
            case 0x00A0 or 0x3000: return ' ';
            case 0xFF1A: return ':';
            case 0xFF1B: return ';';
            case 0x00DF: return 's';
            case 0x00C6: return 'A';
            case 0x00E6: return 'a';
            case 0x00D8: return 'O';
            case 0x00F8: return 'o';
            case 0x20AC: return 'E';
            case 0x00A3: return '#';
        }
        if (cp < 0xA0 || cp > 0x2FFF || (cp >= 0xD800 && cp < 0xE000)) return (char)0;
        // an accented letter types its base letter (a table, not Unicode normalisation: that needs ICU, which a trimmed build lacks)
        int i = cp - 0xC0;
        return i >= 0 && i < Latin.Length ? Latin[i] : (char)0;
    }

    /// <summary>A US keyboard's shifted partner of an unshifted key label.</summary>
    public static char UsShift(char c) => c switch
    {
        ';' => ':', '\'' => '"', ',' => '<', '.' => '>', '/' => '?', '[' => '{', ']' => '}', '\\' => '|', '`' => '~',
        '-' => '_', '=' => '+', '1' => '!', '2' => '@', '3' => '#', '4' => '$', '5' => '%', '6' => '^', '7' => '&', '8' => '*', '9' => '(', '0' => ')',
        >= 'a' and <= 'z' => char.ToUpperInvariant(c),
        _ => c,
    };

    /// <summary>The character a key press types, or 0. <paramref name="unicode"/> is the event's, <paramref name="keyLabel"/> the key's printed (unshifted) code point.</summary>
    public static char Typed(int unicode, int keyLabel, bool shift, bool ctrlAltMeta)
    {
        if (unicode > 0) return Fold(unicode);
        if (ctrlAltMeta || keyLabel is < 32 or >= 127) return (char)0;
        char c = char.ToLowerInvariant((char)keyLabel);
        return shift ? UsShift(c) : c;
    }
}
