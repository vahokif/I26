namespace I76.Formats.Imaging;

/// <summary>
/// Material maps for the modern look's scenery and props (buildings, signs, ramps, wrecks, tanks, fences, trees),
/// made from their own textures at load time (nothing is shipped). No relief: one RGBA8 map at the texture's own
/// size, roughness, metallic, clear coat and specular, as the object shader's has_maps path reads it (CarMaterial's
/// layout). Each texel is weighed as one of five materials from its colour, how busy the painting is about it, and
/// what the texture as a whole is:
///
///   glass   windows: blue to teal, not pale, not vivid, and the dark panes beside them; smooth, a strong
///           reflection. Never on a sign, and not where the whole texture is one flat blue (a painted panel)
///   metal   neutral grey, only where the texture is metal as a whole: a sign's galvanised back, a wreck, a
///           texture named for it (RUST, MET, GRAT, STEEL, CHROM), or one mostly grey and streaked one way
///           (corrugated sheet, rails, poles, tanks: the painting changes across them and not along). Rough where
///           busy or rusty
///   paint   white, vivid or cool colours, smooth: painted boards, doors, awnings, sign faces; semi-matte, a
///           little glossier on signs
///   rough   earth colours (adobe, brick, wood, dirt, rust, sand), stone and concrete greys, anything dark or
///           busy; matte, little reflection
///   leaves  green on a plant (the definition says so); matte, little reflection
///
/// A figure (a car's driver: his uniform, skin and helmet) is all rough: cloth and skin, never glass, metal or paint.
///
/// The weights blend the maps, so a texel between two classes gets a mixture rather than a hard edge.
/// </summary>
public static class PropMaterial
{
    /// <summary>What the definition using a texture is, as far as its materials go.</summary>
    public enum Family { General, Wreck, Plant, Figure }

    /// <summary>A part that is a person, not a car or building part: a car's driver (…DRVR, the army jeep's soldier).</summary>
    public static bool IsFigure(string nodeName) =>
        nodeName.Length >= 8 && nodeName[4..8].Equals("DRVR", StringComparison.OrdinalIgnoreCase);

    /// <summary>The family from an assembled model's definition name: akwreck* wrecks, n* plants.</summary>
    public static Family FamilyOf(string? definition)
    {
        var d = definition ?? "";
        d = d[(d.LastIndexOfAny(['/', '\\', ':']) + 1)..];
        if (d.StartsWith("akwreck", StringComparison.OrdinalIgnoreCase)) return Family.Wreck;
        if (d.StartsWith('n') || d.StartsWith('N')) return Family.Plant;
        return Family.General;
    }

    /// <summary>A sign on a building or a billboard (…_1SG…, …_1S1…, an address's numbers …_1N0…, …_1WS…): no windows in it.</summary>
    public static bool IsBuildingSign(string stem)
    {
        int k = stem.IndexOf("_1", StringComparison.Ordinal);
        if (k < 0 || k + 3 >= stem.Length + 0 || k + 2 >= stem.Length) return false;
        var rest = stem[(k + 2)..].ToUpperInvariant();
        return rest.StartsWith("SG") || rest.StartsWith("WS") || (rest.Length > 1 && (rest[0] == 'S' || rest[0] == 'N') && char.IsDigit(rest[1]));
    }

    /// <summary>A road sign's texture (sd*, sh*, se*, sm*, sw*, sl*, sj*, ss*, sa* definitions): S, a letter, 1.</summary>
    public static bool IsSign(string stem) => stem.Length >= 3 && char.ToUpperInvariant(stem[0]) == 'S' && char.IsLetter(stem[1]) && stem[2] == '1';

    // named for metal (and, in the textures' own scheme, a rail or a lamp post, which are metal where grey);
    // a road deck (_1RD) is asphalt however streaked
    private static readonly string[] MetalNames = ["RUST", "MET", "GRAT", "STEEL", "CHROM", "_1RL", "_1LP"];
    private static readonly string[] NotMetalNames = ["_1RD"];

    /// <summary>Orm: roughness, metallic, clear coat, specular (RGBA8, the texture's size). Cls: the class weights as a picture, for looking at (glass blue, metal red, paint yellow, leaves green, rough grey).</summary>
    public sealed record Maps(int Width, int Height, byte[] Orm, byte[] Cls, string Summary)
    {
        public (byte, byte, byte) ClassColour(int k) => (Cls[k * 3], Cls[k * 3 + 1], Cls[k * 3 + 2]);
    }

    private static (float H, float S, float V) Hsv(byte r8, byte g8, byte b8)
    {
        float r = r8 / 255f, g = g8 / 255f, b = b8 / 255f;
        float mx = MathF.Max(r, MathF.Max(g, b)), mn = MathF.Min(r, MathF.Min(g, b)), c = mx - mn;
        float h = c <= 1e-5f ? 0 : mx == r ? 60 * ((g - b) / c) : mx == g ? 60 * ((b - r) / c + 2) : 60 * ((r - g) / c + 4);
        if (h < 0) h += 360;
        return (h, mx <= 1e-5f ? 0 : c / mx, mx);
    }

    private static float Smooth(float a, float b, float x) { float t = Math.Clamp((x - a) / (b - a), 0, 1); return t * t * (3 - 2 * t); }
    /// <summary>The angle between two hues in [0, 360).</summary>
    private static float HueGap(float a, float b) { float d = MathF.Abs(a - b); return d > 180 ? 360 - d : d; }

    /// <summary>Per-texel colour terms shared by textures and flat colours.</summary>
    private readonly record struct Terms(float Hue, float Sat, float Value, float Dark, float Neutral, float Earthy, float Paintish, float GlassColour, float Green, float MidGrey);

    private static Terms TermsOf(byte r, byte g, byte b)
    {
        var (h, s, v) = Hsv(r, g, b);
        float dark = 1 - Smooth(0.08f, 0.2f, v);
        // grey: cool greys (steel, a sign's back) may be a little bluer than warm ones (weathered wood) may be brown
        bool cool = h >= 170 && h <= 270;
        float neutral = 1 - (cool ? Smooth(0.1f, 0.2f, s) : Smooth(0.07f, 0.14f, s));
        // adobe, brick, wood, dirt, sand, rust: a warm hue, some colour but not vivid
        float warm = 1 - Smooth(50, 68, HueGap(h, 25));
        // (rust and terracotta may be strongly coloured where they are dark: a vivid bright orange is a paint)
        float vividLimit = 0.52f + 0.3f * (1 - Smooth(0.5f, 0.68f, v)) * (1 - Smooth(14, 22, HueGap(h, 28)));
        float earthy = warm * Smooth(0.08f, 0.14f, s) * (1 - Smooth(vividLimit, vividLimit + 0.14f, s));
        // painted: white, vivid, or a colour that is not an earth's
        float white = neutral * Smooth(0.7f, 0.84f, v);
        float vivid = Smooth(vividLimit, vividLimit + 0.14f, s);
        float coolColour = (1 - warm) * Smooth(0.14f, 0.28f, s);
        float paintish = MathF.Max(white, MathF.Max(vivid, coolColour)) * (1 - dark);
        // windows, as these textures paint them: cyan to sky blue (180-204°), coloured, mid-dark. Not a navy or royal
        // blue paint (225°), not a grey-blue door or underside, not a pale blue wall
        float blue = 1 - Smooth(12, 19, HueGap(h, 192));
        float glassColour = blue * Smooth(0.18f, 0.28f, s) * Smooth(0.1f, 0.18f, v) * (1 - Smooth(0.6f, 0.7f, v));
        float green = (1 - Smooth(40, 60, HueGap(h, 110))) * Smooth(0.15f, 0.25f, s) * (1 - dark);
        float midGrey = neutral * Smooth(0.2f, 0.32f, v) * (1 - Smooth(0.8f, 0.9f, v));
        return new Terms(h, s, v, dark, neutral, earthy, paintish, glassColour, green, midGrey);
    }

    public static Maps Build(byte[] rgba, int w, int h, string stem, Family family = Family.General)
    {
        int n = w * h;
        var lum = new float[n];
        var t = new Terms[n];
        var opaque = new bool[n];
        // a palette's worth of colours: each one's terms worked out once
        var seen = new Dictionary<int, Terms>();
        for (int i = 0; i < n; i++)
        {
            byte r = rgba[i * 4], g = rgba[i * 4 + 1], b = rgba[i * 4 + 2];
            lum[i] = (0.299f * r + 0.587f * g + 0.114f * b) / 255f;
            int key = r << 16 | g << 8 | b;
            if (!seen.TryGetValue(key, out t[i])) seen[key] = t[i] = TermsOf(r, g, b);
            opaque[i] = rgba[i * 4 + 3] != 0;
        }
        // how busy the painting is about each texel: brightness less its blur, its size blurred
        var blur = Blur(lum, w, h);
        var hp = new float[n];
        for (int i = 0; i < n; i++) hp[i] = MathF.Abs(lum[i] - blur[i]);
        var busyRaw = Blur(hp, w, h);
        var busy = busyRaw.Select(x => Smooth(0.015f, 0.07f, x)).ToArray();

        bool sign = IsSign(stem);
        string up = stem.ToUpperInvariant();

        // the texture as a whole. Glass: none on a sign; none where the window colour is most of the texture and flat
        // (a blue panel, a sign's ground), unless it is busy as painted windows are
        double opaqueN = 0, glassN = 0, glassBusy = 0, greyN = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                if (!opaque[i]) continue;
                opaqueN++;
                glassN += t[i].GlassColour;
                glassBusy += t[i].GlassColour * busyRaw[i];
                greyN += t[i].MidGrey;
            }
        opaqueN = Math.Max(opaqueN, 1);
        float glassFrac = (float)(glassN / opaqueN);
        float glassBusyMean = glassN > 0 ? (float)(glassBusy / glassN) : 0;
        float glassAllowed = sign || IsBuildingSign(stem) || family == Family.Figure ? 0 : 1 - Smooth(0.5f, 0.7f, glassFrac) * (1 - Smooth(0.02f, 0.045f, glassBusyMean));
        // metal: mostly grey, and a sheet's corrugations or a pole's, a rail's or a tank's round shading: the
        // painting changes across it and not along it (the profile across explains most of it), many times over
        // (corrugated) or as a smooth sweep (round); not flat bands (paint) or a road's lines in asphalt (noise)
        float greyFrac = (float)(greyN / opaqueN);
        var across = Profile(lum, t, opaque, w, h, true);
        var down = Profile(lum, t, opaque, w, h, false);
        // corrugations: a turn every few texels (a road's lanes, a board's edges are too few)
        float Corrugated((float Explained, float Cycles, float Sweep) p, int len) => Smooth(0.25f, 0.4f, p.Explained) * Smooth(0.13f, 0.2f, p.Cycles / len);
        float corrugated = MathF.Max(Corrugated(across, w), Corrugated(down, h));
        float round = MathF.Max(Smooth(0.75f, 0.9f, across.Explained) * across.Sweep, Smooth(0.75f, 0.9f, down.Explained) * down.Sweep);
        float shaped = MathF.Max(corrugated, round) * Smooth(0.25f, 0.45f, greyFrac);
        float metalEvidence = family == Family.Figure ? 0
            : sign ? Smooth(0.5f, 0.8f, greyFrac)
            : family == Family.Wreck ? 0.85f
            : MetalNames.Any(up.Contains) ? 1f
            : NotMetalNames.Any(up.Contains) ? 0f
            : shaped;

        // a pale blue wall's shading (its boards' shadows) is window-coloured: where the commonest colour is a light
        // window colour, glass must be well darker than it
        var bins = new int[24 * 10];
        for (int i = 0; i < n; i++)
        {
            if (!opaque[i]) continue;
            if (t[i].Sat >= 0.15f) bins[(int)(t[i].Hue / 15) % 24 * 10 + Math.Min(9, (int)(t[i].Value * 10))]++;
        }
        int wall = Array.IndexOf(bins, bins.Max());
        float wallHue = wall / 10 * 15 + 7.5f, wallV = wall % 10 / 10f + 0.05f;
        bool paleBlueWall = bins[wall] > opaqueN * 0.2 && HueGap(wallHue, 192) < 26 && wallV >= 0.6f;

        var glass = new float[n];
        // a window is a patch of its colour, not a bluish speck in gravel or a weathered board
        var glassAround = glassAllowed > 0 && glassN > 1 ? Box(t.Select(c => c.GlassColour).ToArray(), w, h, 1) : new float[n];
        for (int i = 0; i < n; i++)
            glass[i] = t[i].GlassColour * Smooth(0.35f, 0.6f, glassAround[i]) * glassAllowed * (paleBlueWall ? 1 - Smooth(wallV - 0.3f, wallV - 0.2f, t[i].Value) : 1);
        // the panes' dark parts (an interior, a shadow under the frame): dark grey texels next to the glass
        bool anyGlass = glassAllowed > 0 && glassN > 1;
        for (int pass = 0; pass < (anyGlass ? 2 : 0); pass++)
        {
            var grown = (float[])glass.Clone();
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    float m = 0;
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                            m = MathF.Max(m, glass[Wrap(y + dy, h) * w + Wrap(x + dx, w)]);
                    float darkGrey = (1 - Smooth(0.25f, 0.4f, t[i].Value)) * MathF.Max(t[i].Neutral, t[i].GlassColour);
                    grown[i] = MathF.Max(glass[i], m * darkGrey * 0.9f);
                }
            glass = grown;
        }

        var orm = new byte[n * 4];
        var cls = new byte[n * 3];
        double sumG = 0, sumM = 0, sumP = 0, sumL = 0, sumR = 0;
        for (int i = 0; i < n; i++)
        {
            var c = t[i];
            float gl = glass[i];
            float me = (1 - gl) * c.MidGrey * metalEvidence;
            float rest = MathF.Max(0, 1 - gl - me);
            float leaf = family == Family.Plant ? rest * c.Green : 0;
            rest -= leaf;
            // a sign's face is all paint, black and earth colours too
            float pa = family == Family.Figure ? 0 : sign ? rest : rest * c.Paintish * (1 - c.Earthy) * (1 - 0.7f * busy[i]);
            float ro = MathF.Max(0, rest - pa);
            // metal rougher where busy (a casting, rust streaks); paint glossier on a sign's smooth sheeting
            float metalRough = 0.3f + 0.35f * busy[i];
            float paintRough = (sign ? 0.4f : 0.55f) + 0.2f * busy[i];
            float rough = gl * 0.05f + me * metalRough + pa * paintRough + leaf * 0.8f + ro * 0.9f;
            float metallic = me * 0.85f;
            float spec = gl * 1f + me * 0.5f + pa * 0.5f + leaf * 0.25f + ro * (0.32f - 0.12f * c.Dark);
            // glass: a second, sharp reflection over the first (a window's two faces)
            float coat = gl * 0.5f + (sign ? pa * 0.3f : 0);
            orm[i * 4] = (byte)(Math.Clamp(rough, 0, 1) * 255);
            orm[i * 4 + 1] = (byte)(Math.Clamp(metallic, 0, 1) * 255);
            orm[i * 4 + 2] = (byte)(Math.Clamp(coat, 0, 1) * 255);
            orm[i * 4 + 3] = (byte)(Math.Clamp(spec, 0, 1) * 255);
            // the picture: glass blue, metal red, paint yellow, leaves green, rough grey
            float cr = me + pa + ro * 0.35f, cg = pa + leaf + ro * 0.35f, cb = gl + ro * 0.35f;
            cls[i * 3] = (byte)(Math.Clamp(cr, 0, 1) * 255);
            cls[i * 3 + 1] = (byte)(Math.Clamp(cg, 0, 1) * 255);
            cls[i * 3 + 2] = (byte)(Math.Clamp(cb, 0, 1) * 255);
            if (opaque[i]) { sumG += gl; sumM += me; sumP += pa; sumL += leaf; sumR += ro; }
        }
        string summary = $"g{sumG / opaqueN:0.00} m{sumM / opaqueN:0.00} p{sumP / opaqueN:0.00} l{sumL / opaqueN:0.00} r{sumR / opaqueN:0.00} across{across.Explained:0.00}/{across.Cycles / w:0.00}/{across.Sweep:0.00} down{down.Explained:0.00}/{down.Cycles / h:0.00}/{down.Sweep:0.00} grey{greyFrac:0.00} glass{glassAllowed:0.00}";
        return new Maps(w, h, orm, cls, summary);
    }

    /// <summary>A flat-coloured face's material: roughness, metallic, specular (no glass or metal: too little to tell; a plant's green is leaves, a figure all rough).</summary>
    public static (float Roughness, float Metallic, float Specular) ForColour(byte r, byte g, byte b, Family family = Family.General)
    {
        var c = TermsOf(r, g, b);
        float leaf = family == Family.Plant ? c.Green : 0;
        float pa = family == Family.Figure ? 0 : (1 - leaf) * c.Paintish * (1 - c.Earthy);
        float ro = 1 - leaf - pa;
        return (0.8f * leaf + 0.55f * pa + 0.9f * ro, 0, 0.25f * leaf + 0.5f * pa + (0.32f - 0.12f * c.Dark) * ro);
    }

    /// <summary>
    /// The grey texels' brightness profile across the texture, x or y (the mean along the other): how
    /// much of the brightness's variance it explains, how many times it goes up and down (ignoring wobbles under
    /// 0.015), and how much it sweeps smoothly (its range over its largest single step, so a gradient scores high
    /// and two flat bands low; 0 for a profile with under 0.2 of range).
    /// </summary>
    private static (float Explained, float Cycles, float Sweep) Profile(float[] lum, Terms[] t, bool[] opaque, int w, int h, bool acrossX)
    {
        (float, float, float) Along(bool acrossX)
        {
            int len = acrossX ? w : h, other = acrossX ? h : w;
            var prof = new double[len];
            var wt = new double[len];
            double tw = 0, tm = 0;
            for (int a = 0; a < len; a++)
                for (int b = 0; b < other; b++)
                {
                    int i = acrossX ? b * w + a : a * w + b;
                    if (!opaque[i]) continue;
                    double m = t[i].MidGrey;
                    prof[a] += m * lum[i]; wt[a] += m; tw += m; tm += m * lum[i];
                }
            if (tw < 1) return (0, 0, 0);
            double mean = tm / tw, total = 0, between = 0;
            for (int a = 0; a < len; a++) prof[a] = wt[a] > 0 ? prof[a] / wt[a] : mean;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    if (!opaque[i]) continue;
                    double m = t[i].MidGrey, d = lum[i] - mean, e = prof[acrossX ? x : y] - mean;
                    total += m * d * d; between += m * e * e;
                }
            float explained = total > 1e-9 ? (float)(between / total) : 0;
            // turns: a hysteresis of 0.02 on the way up and down
            int turns = 0, dir = 0;
            double top = prof[0], bottom = prof[0];
            for (int a = 1; a < len; a++)
            {
                double v = prof[a];
                top = Math.Max(top, v); bottom = Math.Min(bottom, v);
                if (dir >= 0 && v < top - 0.015) { if (dir > 0) turns++; dir = -1; bottom = v; }
                else if (dir <= 0 && v > bottom + 0.015) { if (dir < 0) turns++; dir = 1; top = v; }
            }
            double lo = prof.Min(), hi = prof.Max(), step = 0;
            for (int a = 1; a < len; a++) step = Math.Max(step, Math.Abs(prof[a] - prof[a - 1]));
            float sweep = hi - lo < 0.2 ? 0 : Smooth(2.5f, 5f, (float)((hi - lo) / Math.Max(step, 1e-6)));
            return (explained, turns / 2f, sweep);
        }
        return Along(acrossX);
    }

    /// <summary>An index a few steps outside [0, n) wrapped back in (the textures repeat).</summary>
    private static int Wrap(int i, int n) => i < 0 ? (i % n + n) % n : i >= n ? i % n : i;

    /// <summary>The mean over the (2r+1)² square about each texel, wrapping.</summary>
    private static float[] Box(float[] src, int w, int h, int r)
    {
        var tmp = new float[src.Length];
        var o = new float[src.Length];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float s = 0;
                for (int d = -r; d <= r; d++) s += src[y * w + Wrap(x + d, w)];
                tmp[y * w + x] = s / (2 * r + 1);
            }
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float s = 0;
                for (int d = -r; d <= r; d++) s += tmp[Wrap(y + d, h) * w + x];
                o[y * w + x] = s / (2 * r + 1);
            }
        return o;
    }

    /// <summary>A 5-tap binomial blur each way (σ about 1 texel), wrapping.</summary>
    private static float[] Blur(float[] src, int w, int h)
    {
        ReadOnlySpan<float> k = [1 / 16f, 4 / 16f, 6 / 16f, 4 / 16f, 1 / 16f];
        var tmp = new float[src.Length];
        var o = new float[src.Length];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float s = 0;
                for (int d = -2; d <= 2; d++) s += k[d + 2] * src[y * w + Wrap(x + d, w)];
                tmp[y * w + x] = s;
            }
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float s = 0;
                for (int d = -2; d <= 2; d++) s += k[d + 2] * tmp[Wrap(y + d, h) * w + x];
                o[y * w + x] = s;
            }
        return o;
    }
}
