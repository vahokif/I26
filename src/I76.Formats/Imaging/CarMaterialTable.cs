using System.Globalization;

namespace I76.Formats.Imaging;

/// <summary>
/// The modern look's hand-authored car materials: for each car texture (CarMaterials.tsv, documented at its top) a preset, and within
/// the texture regions (a rectangle in its texels and/or a colour class) of other presets. A texture the table lists is drawn from a
/// per-texel map made here once (the preset of every texel, then roughness / metallic / clear coat / specular from the presets);
/// one it does not list keeps the all-over clear coat.
/// </summary>
public sealed class CarMaterialTable
{
    /// <summary>One surface finish: the shader parameters of its texels, and the colour it is drawn in on the review sheets.</summary>
    public sealed record Preset(byte Id, string Name, float Roughness, float Metallic, float Coat, float Specular, bool Paint, (byte R, byte G, byte B) Colour, string About);

    /// <summary>The palette. Ids are stored in the table's per-texel map; keep them stable.</summary>
    public static readonly IReadOnlyList<Preset> Presets =
    [
        new(1, "gloss-paint", 0.40f, 0.00f, 1.0f, 0.50f, true, (220, 40, 40), "clear-coated paint (the default for every car texture)"),
        new(2, "metallic-paint", 0.30f, 0.55f, 1.0f, 0.50f, true, (240, 130, 30), "paint with metal flake under the coat"),
        new(3, "satin-paint", 0.60f, 0.00f, 0.30f, 0.35f, true, (230, 200, 60), "semi-matt paint (a faded or utility finish)"),
        new(4, "matte-paint", 0.90f, 0.00f, 0.00f, 0.20f, true, (150, 150, 60), "flat paint with no coat (military, primer)"),
        new(5, "chrome", 0.08f, 1.00f, 0.00f, 0.50f, false, (60, 210, 230), "polished plated metal (bumpers, trim, hubcaps)"),
        new(6, "steel", 0.42f, 0.90f, 0.00f, 0.50f, false, (60, 120, 230), "bare or brushed metal (engine, exhaust, guns, grille bars)"),
        new(7, "chassis-steel", 0.70f, 0.30f, 0.00f, 0.30f, false, (90, 70, 150), "dull painted or oily steel (underside, axles, frame)"),
        new(8, "rust", 0.92f, 0.15f, 0.00f, 0.15f, false, (150, 80, 30), "rusted, weathered metal"),
        new(9, "rubber", 0.95f, 0.00f, 0.00f, 0.20f, false, (40, 40, 40), "tyres, mats, boots"),
        new(10, "liner", 0.92f, 0.00f, 0.00f, 0.20f, false, (110, 110, 110), "a pickup bed's ribbed spray-on liner"),
        new(11, "glass", 0.04f, 0.00f, 0.00f, 1.00f, false, (120, 190, 255), "windows painted into the panels"),
        new(12, "lens", 0.10f, 0.00f, 0.00f, 0.90f, false, (250, 250, 150), "head, tail and signal lamp lenses"),
        new(13, "plastic", 0.50f, 0.00f, 0.00f, 0.40f, false, (160, 100, 200), "moulded plastic and trim (bumper covers, dashboards, mirrors)"),
        new(14, "vinyl", 0.55f, 0.00f, 0.00f, 0.35f, false, (200, 90, 150), "vinyl and leather (seats, door cards)"),
        new(15, "canvas", 1.00f, 0.00f, 0.00f, 0.10f, false, (200, 170, 120), "canvas and cloth (tilts, tarps, carpets)"),
        new(16, "wood", 0.65f, 0.00f, 0.20f, 0.30f, false, (140, 90, 50), "varnished wood (trim, bed boards)"),
    ];

    private static readonly Dictionary<string, Preset> ByName = Presets.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);

    public static Preset? PresetNamed(string name) => ByName.GetValueOrDefault(name);

    /// <summary>The colour classes a region may name (a texel's HSV decides).</summary>
    public static readonly string[] ColourClasses = ["any", "black", "dark", "grey", "light", "white", "glass", "deep", "red", "orange", "yellow", "green", "blue", "colour"];

    public sealed record Region(string Texture, Preset Preset, (int X, int Y, int W, int H)? Rect, string[] Classes, int Line);

    /// <summary>A row of the table: a texture pattern, its regions in order; the first row of a texture with neither rectangle nor colour is its base.</summary>
    public IReadOnlyList<Region> Rows { get; }

    private CarMaterialTable(List<Region> rows) { Rows = rows; }

    public static CarMaterialTable Parse(string text)
    {
        var rows = new List<Region>();
        int n = 0;
        foreach (var raw in text.Split('\n'))
        {
            n++;
            string line = raw.Trim();
            int hash = line.IndexOf('#');
            if (hash >= 0) line = line[..hash].Trim();
            if (line.Length == 0) continue;
            var f = line.Split('\t', StringSplitOptions.TrimEntries);
            if (f.Length < 2 || f.Length > 4) throw new FormatException($"car materials line {n}: expected texture, preset, [rect], [colours]");
            var preset = PresetNamed(f[1]) ?? throw new FormatException($"car materials line {n}: no preset '{f[1]}'");
            (int, int, int, int)? rect = null;
            if (f.Length > 2 && f[2] != "*" && f[2].Length > 0)
            {
                var v = f[2].Split(',').Select(s => int.Parse(s, CultureInfo.InvariantCulture)).ToArray();
                if (v.Length != 4 || v[2] <= 0 || v[3] <= 0) throw new FormatException($"car materials line {n}: rect is x,y,w,h");
                rect = (v[0], v[1], v[2], v[3]);
            }
            string[] cls = f.Length > 3 && f[3] != "*" && f[3].Length > 0 ? f[3].Split('|', StringSplitOptions.TrimEntries) : [];
            foreach (var c in cls)
                if (!ColourClasses.Contains(c.TrimStart('!'))) throw new FormatException($"car materials line {n}: no colour class '{c}'");
            rows.Add(new Region(f[0], preset, rect, cls, n));
        }
        return new CarMaterialTable(rows);
    }

    /// <summary>The table of the game: CarMaterials.tsv, embedded.</summary>
    public static CarMaterialTable Default => _default ??= Parse(EmbeddedText());
    private static CarMaterialTable? _default;

    public static string EmbeddedText()
    {
        var asm = typeof(CarMaterialTable).Assembly;
        using var s = asm.GetManifestResourceStream("CarMaterials.tsv") ?? throw new FileNotFoundException("CarMaterials.tsv");
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    /// <summary>A texture's stem in the form the table names it: no folder, no extension.</summary>
    public static string StemOf(string member)
    {
        string s = member[(member.LastIndexOfAny(['/', '\\', ':']) + 1)..];
        int d = s.LastIndexOf('.');
        return d > 0 ? s[..d] : s;
    }

    /// <summary>Whether a table pattern (? one character, * any run) names a texture stem.</summary>
    public static bool Matches(string pattern, string stem)
    {
        int p = 0, s = 0, star = -1, mark = 0;
        while (s < stem.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || char.ToLowerInvariant(pattern[p]) == char.ToLowerInvariant(stem[s]))) { p++; s++; }
            else if (p < pattern.Length && pattern[p] == '*') { star = p++; mark = s; }
            else if (star >= 0) { p = star + 1; s = ++mark; }
            else return false;
        }
        while (p < pattern.Length && pattern[p] == '*') p++;
        return p == pattern.Length;
    }

    /// <summary>The rows that name a texture, in file order (empty: the table does not list it).</summary>
    public List<Region> RowsFor(string member)
    {
        string stem = StemOf(member);
        return Rows.Where(r => Matches(r.Texture, stem)).ToList();
    }

    private const byte ChromeId = 5;

    public bool Lists(string member) => Rows.Any(r => Matches(r.Texture, StemOf(member)));

    /// <summary>Whether a texel (RGB8) is in a colour class.</summary>
    public static bool InClass(string cls, byte r8, byte g8, byte b8)
    {
        bool neg = cls.StartsWith('!');
        if (neg) cls = cls[1..];
        float r = r8 / 255f, g = g8 / 255f, b = b8 / 255f;
        float mx = MathF.Max(r, MathF.Max(g, b)), mn = MathF.Min(r, MathF.Min(g, b)), c = mx - mn;
        float s = mx <= 1e-5f ? 0 : c / mx;
        float h = c <= 1e-5f ? 0 : mx == r ? 60 * (((g - b) / c + 6) % 6) : mx == g ? 60 * ((b - r) / c + 2) : 60 * ((r - g) / c + 4);
        float v = mx;
        bool hit = cls switch
        {
            "any" => true,
            "black" => v < 0.10f,
            "dark" => v < 0.22f,
            "deep" => v >= 0.10f && v < 0.50f,
            "grey" => s < 0.20f && v >= 0.22f && v < 0.65f,
            "light" => s < 0.25f && v >= 0.65f,
            "white" => s < 0.12f && v >= 0.82f,
            "glass" => h >= 180 && h <= 255 && s >= 0.06f && s <= 0.6f && v >= 0.10f && v <= 0.8f,
            "red" => (h < 20 || h >= 335) && s >= 0.35f && v >= 0.2f,
            "orange" => h >= 20 && h < 45 && s >= 0.35f && v >= 0.2f,
            "yellow" => h >= 45 && h < 70 && s >= 0.35f && v >= 0.2f,
            "green" => h >= 70 && h < 170 && s >= 0.3f && v >= 0.15f,
            "blue" => h >= 190 && h < 265 && s >= 0.4f && v >= 0.2f,
            "colour" => s >= 0.3f && v >= 0.15f,
            _ => false,
        };
        return hit != neg;
    }

    /// <summary>
    /// The preset id of every texel of a texture (RGBA8, w×h): the rows that name it, in order, each setting the texels its rectangle and colours
    /// pick (a row with neither sets all). Null if the table does not list the texture.
    /// </summary>
    public byte[]? Ids(string member, byte[] rgba, int w, int h)
    {
        var rows = RowsFor(member);
        // the segmentation pipeline's per-texel map (CarMaskMap, only with I76_CAR_MASKS=1) is the base; the table's rows override it
        var basis = CarMaskMap.Enabled ? CarMaskMap.Default.Ids(member, w, h) : null;
        if (rows.Count == 0 && basis is null) return null;
        var ids = basis is null ? new byte[w * h] : (byte[])basis.Clone();
        // the segmentation's chrome is left out for now (trim and seals labelled in ragged specks, black and white stippling): those texels
        // keep the clear coat; a hand row can still make a part chrome
        for (int i = 0; i < ids.Length; i++) if (ids[i] == ChromeId) ids[i] = 1;
        foreach (var row in rows)
        {
            var (x0, y0, x1, y1) = row.Rect is { } r ? (Math.Max(0, r.X), Math.Max(0, r.Y), Math.Min(w, r.X + r.W), Math.Min(h, r.Y + r.H)) : (0, 0, w, h);
            for (int y = y0; y < y1; y++)
                for (int x = x0; x < x1; x++)
                {
                    int i = y * w + x;
                    if (row.Classes.Length > 0 && !row.Classes.Any(c => InClass(c, rgba[i * 4], rgba[i * 4 + 1], rgba[i * 4 + 2]))) continue;
                    // over a segmentation map a whole-texture paint row (the Goon's matte) is the car's paint: the glass, lamps and chrome under
                    // it stay; a row with a rectangle is a fix of that region (the school bus's bands the segmentation took for glass) and applies
                    if (basis is not null && row.Rect is null && row.Preset.Paint && basis[i] != 0 && !Presets[Math.Clamp(basis[i], (byte)1, (byte)Presets.Count) - 1].Paint) continue;
                    ids[i] = row.Preset.Id;
                }
        }
        // a table with regions but no base row leaves texels unset: they take the all-over clear coat
        for (int i = 0; i < ids.Length; i++) if (ids[i] == 0) ids[i] = 1;
        return ids;
    }

    /// <summary>The shader's map for a texture's ids: roughness, metallic, clear coat, specular (RGBA8).</summary>
    public static byte[] Orm(byte[] ids)
    {
        var o = new byte[ids.Length * 4];
        for (int i = 0; i < ids.Length; i++)
        {
            var p = Presets[Math.Clamp(ids[i], (byte)1, (byte)Presets.Count) - 1];
            o[i * 4] = (byte)MathF.Round(p.Roughness * 255);
            o[i * 4 + 1] = (byte)MathF.Round(p.Metallic * 255);
            o[i * 4 + 2] = (byte)MathF.Round(p.Coat * 255);
            o[i * 4 + 3] = (byte)MathF.Round(p.Specular * 255);
        }
        return o;
    }

    /// <summary>Where a texture is paint (a respray applies there), 0 or 1 per texel.</summary>
    public static byte[] PaintWeight(byte[] ids) => ids.Select(i => (byte)(Presets[Math.Clamp(i, (byte)1, (byte)Presets.Count) - 1].Paint ? 1 : 0)).ToArray();

    /// <summary>The false colour of every texel's preset, RGB8 (the review sheets and the game's I76_CAR_MATERIAL_IDS view).</summary>
    public static byte[] FalseColour(byte[] ids)
    {
        var o = new byte[ids.Length * 3];
        for (int i = 0; i < ids.Length; i++)
        {
            var c = Presets[Math.Clamp(ids[i], (byte)1, (byte)Presets.Count) - 1].Colour;
            o[i * 3] = c.R; o[i * 3 + 1] = c.G; o[i * 3 + 2] = c.B;
        }
        return o;
    }
}
