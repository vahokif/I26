using I76.Formats.Imaging;
using I76.Formats.Vfs;

namespace I76.Game.Shell;

/// <summary>
/// Where a car file's custom paint colours are read from (I26's respray): the variant's sidecar (<c>&lt;variant&gt;.i26</c>, beside its
/// .vcf, <see cref="CarSidecar"/>) as the addon overlay serves it (the player's own, loaded by <see cref="MeleeData"/>) or the install's
/// addon\ folder has it (a community car's). A stock car has none: the factory paint.
/// </summary>
public static class CarPaints
{
    /// <summary>By the addon overlay alone (where the lists have put every variant's sidecar): for the network's hooks, which have no VFS at hand.</summary>
    public static CarPaint? Of(string? vcf) =>
        string.IsNullOrEmpty(vcf) || vcf == "*mission" ? null : CarSidecar.Read(AssetVfs.Addon.GetValueOrDefault(CarSidecar.NameOf(vcf)));

    public static CarPaint? Of(AssetVfs vfs, string? vcf) =>
        string.IsNullOrEmpty(vcf) || vcf == "*mission" ? null : CarSidecar.Read(vfs.TryRead(CarSidecar.NameOf(vcf)));

    /// <summary>Classic American car paints of the 1970s: the colours the 3D garage's RESPRAY and the 2D form's COLOUR row offer.</summary>
    public static readonly (string Name, PaintColour Colour)[] Classic =
    [
        ("Hugger Orange", new(0xF0, 0x54, 0x1E)), ("Grabber Blue", new(0x1E, 0x9B, 0xE0)), ("Plum Crazy", new(0x5B, 0x1E, 0x8C)),
        ("Sublime", new(0x8D, 0xD0, 0x2A)), ("Hemi Orange", new(0xFF, 0x8C, 0x1A)), ("Rallye Red", new(0xC4, 0x1E, 0x1E)),
        ("Forest Green", new(0x1E, 0x4D, 0x2B)), ("Black", new(0x0E, 0x0E, 0x0F)), ("White", new(0xF2, 0xF2, 0xEE)),
        ("Silver", new(0xB9, 0xBC, 0xC0)), ("Gold", new(0xC9, 0xA2, 0x27)), ("Brown", new(0x5A, 0x3A, 0x1E)),
        ("Lemon Twist", new(0xF5, 0xD6, 0x0A)), ("Candy Apple", new(0x9B, 0x11, 0x1E)), ("Midnight Blue", new(0x1C, 0x2E, 0x6B)),
        ("Burnt Copper", new(0xA8, 0x44, 0x1C)), ("Avocado", new(0x6B, 0x7A, 0x2A)), ("Champagne", new(0xD9, 0xC7, 0xA0)),
        ("Burgundy", new(0x6B, 0x1B, 0x2A)), ("Panther Pink", new(0xE8, 0x62, 0x8F)), ("Turquoise", new(0x2A, 0xA6, 0xA0)),
        ("Powder Blue", new(0x7F, 0xB2, 0xD9)), ("Gunmetal", new(0x4A, 0x4F, 0x55)), ("Olive Drab", new(0x5B, 0x60, 0x30)),
    ];

    /// <summary>A colour's name as the lists show it: "Factory" for none, a classic paint's name, else "Custom" (the 3D garage's own mix).</summary>
    public static string NameOf(PaintColour? c) =>
        c is not { } k ? "Factory" : Classic.FirstOrDefault(x => x.Colour == k).Name ?? "Custom";
}
