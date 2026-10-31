namespace I76.Formats.Input;

/// <summary>
/// What the operating system itself says a stick has pressed, with the hat apart from the buttons: <see cref="Hat"/> bit 0 up, 1 right, 2 down,
/// 3 left (diagonals set two), <see cref="Buttons"/> bit k is physical button k (Button1 is 0).
/// </summary>
public readonly record struct NativeStick(byte Hat, uint Buttons);

/// <summary>
/// Godot (SDL) reports a hat and a real button 12 to 15 as the same events, raw 11 to 14 (issue 22). Where the OS can also be asked for the stick
/// (winmm on Windows, joydev on Linux) it says which is which, and this turns the two views into one numbering: raw 0 to 10 are Button1 to 11,
/// raw 11 to 14 the hat, raw 15 and up are Button12 and up. Button N is then physical button N.
/// </summary>
public static class HatSplit
{
    /// <summary>The raw bit set (<see cref="PadRaw.Buttons"/>, hat as raw 11..14, no collision) the OS state stands for.</summary>
    public static ulong RawBits(in NativeStick s)
    {
        ulong raw = 0;
        for (int b = 0; b < 32; b++)
            if ((s.Buttons >> b & 1) != 0) raw |= 1UL << (b < GenericStick.HatFirst ? b : b + 4);
        if ((s.Hat & 1) != 0) raw |= 1UL << 11;
        if ((s.Hat & 4) != 0) raw |= 1UL << 12;
        if ((s.Hat & 8) != 0) raw |= 1UL << 13;
        if ((s.Hat & 2) != 0) raw |= 1UL << 14;
        return raw;
    }

    /// <summary>The hat bit (<see cref="NativeStick.Hat"/>) a Godot raw number 11..14 is when it is the hat, or 0.</summary>
    private static int HatBit(int godotRaw) => GenericStick.HatDirection(godotRaw) is >= 0 and var d ? 1 << d : 0;

    /// <summary>
    /// The raw number in the split numbering (<see cref="RawBits"/>) of a Godot button event's number. Under 11 it is the same, 15 and up
    /// move up by four (physical button k, a hat-sized gap in the middle). 11 to 14 could be either: the one the OS shows held, or
    /// when both are (or neither any more) the one that changed since the last look (<paramref name="before"/>), and the hat by default.
    /// </summary>
    public static int Translate(int godotRaw, in NativeStick now, in NativeStick before)
    {
        if (godotRaw < GenericStick.HatFirst) return godotRaw;
        if (godotRaw > GenericStick.HatLast) return godotRaw + 4;
        int bit = HatBit(godotRaw);
        bool hatNow = (now.Hat & bit) != 0, btnNow = (now.Buttons >> godotRaw & 1) != 0;
        bool hatWas = (before.Hat & bit) != 0, btnWas = (before.Buttons >> godotRaw & 1) != 0;
        bool button;
        if (hatNow != btnNow) button = btnNow;
        else if (hatNow) button = !btnWas && hatWas;          // both held: the one that came on since the last look
        else button = btnWas && !hatWas;                       // neither: the one that was held went off
        return button ? godotRaw + 4 : godotRaw;
    }

    /// <summary>A winmm POV value (hundredths of a degree, 0xFFFF or anything out of range for centred) as <see cref="NativeStick.Hat"/> bits.</summary>
    public static byte HatOfPov(uint pov)
    {
        if (pov > 35999) return 0;
        // sectors of 45 degrees centred on the eight directions
        int oct = (int)((pov + 2250) / 4500) % 8;
        return oct switch { 0 => 1, 1 => 1 | 2, 2 => 2, 3 => 2 | 4, 4 => 4, 5 => 4 | 8, 6 => 8, _ => 8 | 1 };
    }

    /// <summary>A joydev hat as bits from its two axes (X right positive, Y down positive; the kernel's ABS_HAT0X and ABS_HAT0Y).</summary>
    public static byte HatOfAxes(int x, int y) => (byte)((y < 0 ? 1 : 0) | (x > 0 ? 2 : 0) | (y > 0 ? 4 : 0) | (x < 0 ? 8 : 0));
}
