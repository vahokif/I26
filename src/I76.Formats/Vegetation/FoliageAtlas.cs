namespace I76.Formats.Vegetation;

/// <summary>
/// The plants' one texture, painted in code (no image files): 8 x 4 cells of <see cref="CellSize"/> texels. The
/// foliage cells are alpha-tested sprites in the living plant's colours (twigs at the bottom edge, growing up);
/// bark, blades, grass and dead leaves are neutral, coloured by the mesh. RGBA8, sRGB, with every mip level made
/// here: alpha-weighted colour (no dark fringes) and, per cell, alpha rescaled so a level covers as much as the
/// full-size cell at the 0.5 cut (foliage would otherwise thin out with distance).
/// </summary>
public static class FoliageAtlas
{
    public const int CellSize = 256;
    public const int Width = CellSize * Shapes.CellsX, Height = CellSize * Shapes.CellsY;
    public const float AlphaCut = 0.5f;
    private const int CellCount = Shapes.CellsX * Shapes.CellsY;

    /// <summary>The atlas, every mip level from <see cref="Width"/> x <see cref="Height"/> down to 1 x 1 one after the other (Godot's layout).</summary>
    public static byte[] Build(ulong seed = 0x76A1)
    {
        var img = new float[Width * Height * 4];
        var painters = new (AtlasCell Cell, Action<Canvas, VegRng> Paint)[]
        {
            (AtlasCell.Bark, Bark), (AtlasCell.Creosote, Creosote), (AtlasCell.Mesquite, Mesquite), (AtlasCell.Saltbush, Saltbush),
            (AtlasCell.Tamarisk, Tamarisk), (AtlasCell.Willow, Willow), (AtlasCell.Tumbleweed, Tumbleweed), (AtlasCell.Cholla, ChollaSkin),
            (AtlasCell.PadSkin, PadSkin), (AtlasCell.Blade, Blade), (AtlasCell.Grass, GrassBlade), (AtlasCell.YuccaFlowers, YuccaFlowers),
            (AtlasCell.DeadLeaves, DeadLeaves), (AtlasCell.Forb, Forb), (AtlasCell.Panicle, Panicle), (AtlasCell.GrassTuft, GrassTuft),
            (AtlasCell.SandSage, SandSage), (AtlasCell.ShinOak, ShinOak), (AtlasCell.GrayOak, GrayOak), (AtlasCell.OcotilloLeaf, OcotilloLeaf),
            (AtlasCell.RedFlowers, RedFlowers), (AtlasCell.IndianBlanket, IndianBlanket), (AtlasCell.PricklyPoppy, PricklyPoppy), (AtlasCell.Horsemint, Horsemint),
            (AtlasCell.Buffalograss, Buffalograss), (AtlasCell.Pickleweed, Pickleweed), (AtlasCell.AgaveBlade, AgaveBlade), (AtlasCell.Candelilla, Candelilla),
            (AtlasCell.PineSpray, PineSpray),
        };
        System.Threading.Tasks.Parallel.ForEach(painters, p =>
        {
            var c = new Canvas(img, (int)p.Cell);
            p.Paint(c, new VegRng(VegHash.Mix(seed, (ulong)p.Cell)));
            c.FillClear();
        });
        return MipChain(img);
    }

    public static bool IsFoliage(AtlasCell c) => c is AtlasCell.Creosote or AtlasCell.Mesquite or AtlasCell.Saltbush or AtlasCell.Tamarisk or AtlasCell.Willow
        or AtlasCell.Tumbleweed or AtlasCell.YuccaFlowers or AtlasCell.Forb or AtlasCell.Panicle or AtlasCell.GrassTuft
        or (>= AtlasCell.SandSage and <= AtlasCell.Candelilla) or AtlasCell.PineSpray;

    /// <summary>The size of mip level <paramref name="level"/> (each halves, never under 1).</summary>
    public static (int W, int H) LevelSize(int level) => (Math.Max(1, Width >> level), Math.Max(1, Height >> level));

    /// <summary>The number of mip levels, down to 1 x 1.</summary>
    public static int Levels { get { int n = 1; for (var (w, h) = (Width, Height); w > 1 || h > 1; n++) { w = Math.Max(1, w / 2); h = Math.Max(1, h / 2); } return n; } }

    /// <summary>The fraction of a cell's texels at or over the alpha cut, at a mip level of a chain <see cref="Build"/> made.</summary>
    public static float Coverage(byte[] chain, int level, AtlasCell cell)
    {
        int off = 0;
        for (int l = 0; l < level; l++) { var (lw, lh) = LevelSize(l); off += lw * lh * 4; }
        var (w, h) = LevelSize(level);
        int cw = Math.Max(1, w / Shapes.CellsX), ch = Math.Max(1, h / Shapes.CellsY);
        int cx = (int)cell % Shapes.CellsX * cw, cy = (int)cell / Shapes.CellsX * ch;
        int n = 0;
        for (int y = cy; y < cy + ch; y++)
            for (int x = cx; x < cx + cw; x++)
                if (chain[off + (y * w + x) * 4 + 3] >= AlphaCut * 255) n++;
        return n / (float)(cw * ch);
    }

    // ---- the mip chain ---------------------------------------------------------------------------------------

    private static byte[] MipChain(float[] top)
    {
        var levels = new List<float[]> { top };
        int w = Width, h = Height;
        while (w > 1 || h > 1)
        {
            var prev = levels[^1];
            int nw = Math.Max(1, w / 2), nh = Math.Max(1, h / 2);
            var next = new float[nw * nh * 4];
            for (int y = 0; y < nh; y++)
                for (int x = 0; x < nw; x++)
                {
                    float r = 0, g = 0, b = 0, a = 0;
                    for (int dy = 0; dy < 2; dy++)
                        for (int dx = 0; dx < 2; dx++)
                        {
                            int sx = Math.Min(2 * x + dx, w - 1), sy = Math.Min(2 * y + dy, h - 1);
                            int i = (sy * w + sx) * 4;
                            float wt = prev[i + 3] + 1e-4f;
                            r += prev[i] * wt; g += prev[i + 1] * wt; b += prev[i + 2] * wt; a += prev[i + 3];
                        }
                    int o = (y * nw + x) * 4;
                    float wsum = a + 4e-4f;
                    next[o] = r / wsum; next[o + 1] = g / wsum; next[o + 2] = b / wsum; next[o + 3] = a / 4;
                }
            levels.Add(next);
            w = nw; h = nh;
        }
        // keep each foliage cell's coverage at the cut as its level 0 has it
        var cover0 = new float[CellCount];
        for (int c = 0; c < cover0.Length; c++) cover0[c] = CoverageF(levels[0], Width, Height, c, 1f);
        for (int l = 1; l < levels.Count; l++)
        {
            var (lw, lh) = LevelSize(l);
            if (lw / Shapes.CellsX < 2 || lh / Shapes.CellsY < 2) break;
            System.Threading.Tasks.Parallel.For(0, cover0.Length, c =>
            {
                if (!IsFoliage((AtlasCell)c)) return;
                float lo = 0.5f, hi = 6f;
                for (int it = 0; it < 14; it++)
                {
                    float mid = 0.5f * (lo + hi);
                    if (CoverageF(levels[l], lw, lh, c, mid) < cover0[c]) lo = mid; else hi = mid;
                }
                ScaleAlpha(levels[l], lw, lh, c, 0.5f * (lo + hi));
            });
        }
        int total = 0;
        foreach (var lv in levels) total += lv.Length;
        var o8 = new byte[total];
        int k = 0;
        foreach (var lv in levels)
            foreach (float v in lv) o8[k++] = (byte)Math.Clamp((int)(v * 255f + 0.5f), 0, 255);
        return o8;
    }

    private static float CoverageF(float[] img, int w, int h, int cell, float scale)
    {
        int cw = w / Shapes.CellsX, ch = h / Shapes.CellsY, cx = cell % Shapes.CellsX * cw, cy = cell / Shapes.CellsX * ch, n = 0;
        for (int y = cy; y < cy + ch; y++)
            for (int x = cx; x < cx + cw; x++)
                if (img[(y * w + x) * 4 + 3] * scale >= AlphaCut) n++;
        return n / (float)(cw * ch);
    }

    private static void ScaleAlpha(float[] img, int w, int h, int cell, float scale)
    {
        int cw = w / Shapes.CellsX, ch = h / Shapes.CellsY, cx = cell % Shapes.CellsX * cw, cy = cell / Shapes.CellsX * ch;
        for (int y = cy; y < cy + ch; y++)
            for (int x = cx; x < cx + cw; x++)
            {
                int i = (y * w + x) * 4 + 3;
                img[i] = MathF.Min(1f, img[i] * scale);
            }
    }

    // ---- a small painter ------------------------------------------------------------------------------------

    /// <summary>One cell of the atlas: soft-edged ellipses and tapered strokes composited over, in sRGB 0..1.</summary>
    public sealed class Canvas(float[] img, int cell)
    {
        private readonly int _x0 = cell % Shapes.CellsX * CellSize, _y0 = cell / Shapes.CellsX * CellSize;
        public const int S = CellSize;

        public void Fill(float r, float g, float b, float a = 1)
        {
            for (int y = 0; y < S; y++) for (int x = 0; x < S; x++) Set(x, y, r, g, b, a);
        }

        public void Set(int x, int y, float r, float g, float b, float a)
        {
            int i = ((_y0 + y) * Width + _x0 + x) * 4;
            img[i] = r; img[i + 1] = g; img[i + 2] = b; img[i + 3] = a;
        }

        public (float R, float G, float B, float A) Get(int x, int y)
        {
            int i = ((_y0 + y) * Width + _x0 + x) * 4;
            return (img[i], img[i + 1], img[i + 2], img[i + 3]);
        }

        public void Over(int x, int y, float r, float g, float b, float a)
        {
            if ((uint)x >= S || (uint)y >= S || a <= 0) return;
            int i = ((_y0 + y) * Width + _x0 + x) * 4;
            float da = img[i + 3], oa = a + da * (1 - a);
            if (oa <= 0) return;
            img[i] = (r * a + img[i] * da * (1 - a)) / oa;
            img[i + 1] = (g * a + img[i + 1] * da * (1 - a)) / oa;
            img[i + 2] = (b * a + img[i + 2] * da * (1 - a)) / oa;
            img[i + 3] = oa;
        }

        /// <summary>A filled ellipse, radii rx along the angle and ry across, lit a little from the top-left (shade 0: flat).</summary>
        public void Ellipse(float cx, float cy, float rx, float ry, float angle, (float R, float G, float B) c, float alpha = 1, float shade = 0.25f)
        {
            float ca = MathF.Cos(angle), sa = MathF.Sin(angle);
            float rmax = MathF.Max(rx, ry) + 1.5f;
            int xa = (int)MathF.Floor(cx - rmax), xb = (int)MathF.Ceiling(cx + rmax), ya = (int)MathF.Floor(cy - rmax), yb = (int)MathF.Ceiling(cy + rmax);
            float rmin = MathF.Max(0.5f, MathF.Min(rx, ry));
            for (int y = ya; y <= yb; y++)
                for (int x = xa; x <= xb; x++)
                {
                    float dx = x + 0.5f - cx, dy = y + 0.5f - cy;
                    float u = (dx * ca + dy * sa) / rx, v = (-dx * sa + dy * ca) / ry;
                    float d = MathF.Sqrt(u * u + v * v);
                    float cov = Math.Clamp((1 - d) * rmin + 0.5f, 0, 1);
                    if (cov <= 0) continue;
                    float lit = 1 + shade * (-0.5f * (dx / rmax) - 0.7f * (dy / rmax) - 0.3f * d);
                    Over(x, y, c.R * lit, c.G * lit, c.B * lit, cov * alpha);
                }
        }

        /// <summary>A stroke from (x0, y0) to (x1, y1), width w0 tapering to w1.</summary>
        public void Line(float x0, float y0, float x1, float y1, float w0, float w1, (float R, float G, float B) c, float alpha = 1)
        {
            float wmax = MathF.Max(w0, w1) * 0.5f + 1.5f;
            int xa = (int)MathF.Floor(MathF.Min(x0, x1) - wmax), xb = (int)MathF.Ceiling(MathF.Max(x0, x1) + wmax);
            int ya = (int)MathF.Floor(MathF.Min(y0, y1) - wmax), yb = (int)MathF.Ceiling(MathF.Max(y0, y1) + wmax);
            float lx = x1 - x0, ly = y1 - y0, l2 = MathF.Max(1e-6f, lx * lx + ly * ly);
            for (int y = ya; y <= yb; y++)
                for (int x = xa; x <= xb; x++)
                {
                    float px = x + 0.5f - x0, py = y + 0.5f - y0;
                    float t = Math.Clamp((px * lx + py * ly) / l2, 0, 1);
                    float ex = px - lx * t, ey = py - ly * t;
                    float d = MathF.Sqrt(ex * ex + ey * ey);
                    float hw = 0.5f * (w0 + (w1 - w0) * t);
                    float cov = Math.Clamp(hw - d + 0.5f, 0, 1) * MathF.Min(1, hw * 2);
                    if (cov > 0) Over(x, y, c.R, c.G, c.B, cov * alpha);
                }
        }

        /// <summary>A curved stroke (quadratic through a control point), in a few straight pieces.</summary>
        public void Curve(float x0, float y0, float cx, float cy, float x1, float y1, float w0, float w1, (float R, float G, float B) c, int pieces = 6)
        {
            float px = x0, py = y0;
            for (int i = 1; i <= pieces; i++)
            {
                float t = (float)i / pieces, it = 1 - t;
                float qx = it * it * x0 + 2 * it * t * cx + t * t * x1, qy = it * it * y0 + 2 * it * t * cy + t * t * y1;
                Line(px, py, qx, qy, w0 + (w1 - w0) * (i - 1) / pieces, w0 + (w1 - w0) * i / pieces, c);
                px = qx; py = qy;
            }
        }

        /// <summary>The clear texels take the mean colour of the painted ones, so filtering never pulls black in at the edges.</summary>
        public void FillClear()
        {
            double r = 0, g = 0, b = 0, w = 0;
            for (int y = 0; y < S; y++)
                for (int x = 0; x < S; x++)
                {
                    var p = Get(x, y);
                    r += p.R * p.A; g += p.G * p.A; b += p.B * p.A; w += p.A;
                }
            if (w <= 0) return;
            float mr = (float)(r / w), mg = (float)(g / w), mb = (float)(b / w);
            for (int y = 0; y < S; y++)
                for (int x = 0; x < S; x++)
                {
                    var p = Get(x, y);
                    if (p.A < 0.02f) Set(x, y, mr, mg, mb, p.A);
                }
        }
    }

    private static (float, float, float) Rgb(int r, int g, int b) => (r / 255f, g / 255f, b / 255f);
    private static (float, float, float) Mix((float, float, float) a, (float, float, float) b, float t) =>
        (a.Item1 + (b.Item1 - a.Item1) * t, a.Item2 + (b.Item2 - a.Item2) * t, a.Item3 + (b.Item3 - a.Item3) * t);
    private static (float, float, float) Jit((float R, float G, float B) c, ref VegRng rng, float k)
    {
        float m = 1 + k * rng.Bell();
        return (c.R * m, c.G * m * (1 + 0.3f * k * rng.Bell()), c.B * m);
    }

    /// <summary>
    /// A branching twig drawn from (x, y) at an angle (0: straight up), calling <paramref name="leaf"/> at points along it
    /// with the twig's direction; children fork off at about ±<paramref name="fork"/>.
    /// </summary>
    private static void Twig(Canvas c, ref VegRng rng, float x, float y, float ang, float len, float width, int depth, float fork,
        (float, float, float) col, Action<float, float, float, int> leaf, float bend = 0f, int steps = 5)
    {
        float px = x, py = y, a = ang;
        for (int i = 0; i < steps; i++)
        {
            float l = len / steps;
            a += bend / steps + rng.Bell() * 0.12f;
            float nx = px + MathF.Sin(a) * l, ny = py - MathF.Cos(a) * l;
            float w = width * (1 - 0.6f * (i + 1) / steps);
            c.Line(px, py, nx, ny, w + 0.4f, w, col);
            leaf(nx, ny, a, depth);
            if (depth > 0 && i >= 1 && rng.Float() < 0.45f)
            {
                float side = rng.Float() < 0.5f ? -1 : 1;
                Twig(c, ref rng, nx, ny, a + side * fork * (0.7f + 0.6f * rng.Float()), len * 0.55f, w * 0.8f, depth - 1, fork, col, leaf, bend, Math.Max(2, steps - 1));
            }
            px = nx; py = ny;
        }
    }

    // ---- the cells ----------------------------------------------------------------------------------------------

    private static void Bark(Canvas c, VegRng rng)
    {
        // neutral, with lengthwise fissures and a fine grain; the mesh colours it
        var rows = new float[Canvas.S];
        for (int x = 0; x < Canvas.S; x++) rows[x] = 0.82f + 0.1f * rng.Bell();
        for (int y = 0; y < Canvas.S; y++)
            for (int x = 0; x < Canvas.S; x++)
            {
                float v = rows[x] * (0.95f + 0.05f * MathF.Sin(y * 0.21f + x * 0.9f));
                c.Set(x, y, v, v * 0.98f, v * 0.95f, 1);
            }
        for (int i = 0; i < 40; i++)
        {
            float x = rng.Range(0, Canvas.S), y = rng.Range(0, Canvas.S), l = rng.Range(20, 80);
            c.Line(x, y, x + rng.Range(-4, 4), y + l, rng.Range(1.5f, 3.5f), 0.8f, Rgb(120, 115, 108));
        }
    }

    private static void Creosote(Canvas c, VegRng rng)
    {
        // forked grey twigs; along them, the bifoliate leaves: two small glossy dark-olive leaflets joined at the base
        var stem = Rgb(96, 88, 76);
        var leafA = Rgb(74, 90, 38); var leafB = Rgb(108, 116, 50);
        var r = rng;
        for (int t = 0; t < 7; t++)
        {
            float ang = (t - 3) * 0.28f + rng.Bell() * 0.15f;
            Twig(c, ref rng, 128 + rng.Range(-14, 14), 254, ang, rng.Range(150, 230), 3.2f, 2, 0.55f, stem, (x, y, a, d) =>
            {
                for (int k = 0; k < 3; k++)
                {
                    float ox = x + r.Range(-12, 12), oy = y + r.Range(-12, 12);
                    var col = Jit(Mix(leafA, leafB, r.Float()), ref r, 0.1f);
                    float la = a + r.Range(-0.6f, 0.6f);
                    c.Ellipse(ox + MathF.Sin(la - 0.6f) * 4, oy - MathF.Cos(la - 0.6f) * 4, 5.5f, 2.6f, la - 0.6f - MathF.PI / 2, col, 1, 0.45f);
                    c.Ellipse(ox + MathF.Sin(la + 0.6f) * 4, oy - MathF.Cos(la + 0.6f) * 4, 5.5f, 2.6f, la + 0.6f - MathF.PI / 2, col, 1, 0.45f);
                }
                // the odd yellow flower or fuzzy white seed ball
                if (r.Float() < 0.04f) c.Ellipse(x + r.Range(-8, 8), y + r.Range(-8, 8), 3.5f, 3.5f, 0, r.Float() < 0.6f ? Rgb(232, 206, 70) : Rgb(226, 222, 210));
            }, bend: rng.Bell() * 0.3f);
        }
    }

    private static void Mesquite(Canvas c, VegRng rng)
    {
        // bipinnate leaves: stalks out from the twig, each forking into two pinnae of many narrow leaflet pairs, drooping
        var leafA = Rgb(86, 100, 54); var leafB = Rgb(108, 118, 66);
        var rach = Rgb(110, 120, 70);
        for (int s = 0; s < 8; s++)
        {
            float x0 = 128 + rng.Range(-30, 30), y0 = rng.Range(160, 245);
            float ang = rng.Range(-1.8f, 1.8f);
            float len = rng.Range(75, 110);
            for (int pinna = -1; pinna <= 1; pinna += 2)
            {
                float a = ang + pinna * 0.22f;
                float droop = 0.5f * MathF.Sign(MathF.Sin(a) + 1e-3f);
                float px = x0, py = y0;
                int pairs = 15;
                for (int k = 0; k < pairs; k++)
                {
                    float t = (k + 1f) / pairs;
                    float aa = a + droop * t;
                    float nx = px + MathF.Sin(aa) * len / pairs, ny = py - MathF.Cos(aa) * len / pairs;
                    c.Line(px, py, nx, ny, 1.4f, 1.1f, rach);
                    var col = Jit(Mix(leafA, leafB, rng.Float()), ref rng, 0.08f);
                    float ll = 11 * (1 - 0.3f * t);
                    for (int side = -1; side <= 1; side += 2)
                    {
                        float la = aa + side * 1.25f;
                        c.Ellipse(nx + MathF.Sin(la) * ll * 0.55f, ny - MathF.Cos(la) * ll * 0.55f, ll * 0.55f, 1.15f, la - MathF.PI / 2, col, 1, 0.3f);
                    }
                    px = nx; py = ny;
                }
            }
        }
    }

    private static void Saltbush(Canvas c, VegRng rng)
    {
        // a dense mound of small oblong grey-green leaves, mealy, with clusters of beige four-winged fruit up top
        var leafA = Rgb(84, 96, 72); var leafB = Rgb(110, 118, 94);
        for (int i = 0; i < 520; i++)
        {
            float a = rng.Range(0, MathF.Tau), r = 112 * MathF.Sqrt(rng.Float());
            float x = 128 + MathF.Cos(a) * r, y = 140 + MathF.Sin(a) * r * 0.95f;
            if (y > 252) continue;
            var col = Jit(Mix(leafA, leafB, rng.Float()), ref rng, 0.08f);
            c.Ellipse(x, y, rng.Range(4.5f, 7f), rng.Range(2.2f, 3f), rng.Range(0, MathF.PI), col, 1, 0.35f);
            if (rng.Float() < 0.15f) c.Ellipse(x, y, 1.2f, 1.2f, 0, Rgb(160, 164, 150));
        }
        for (int k = 0; k < 9; k++)
        {
            float a = rng.Range(-2.6f, -0.5f), r = rng.Range(50, 100);
            float cx = 128 + MathF.Cos(a) * r, cy = 140 + MathF.Sin(a) * r;
            for (int f = 0; f < 6; f++)
            {
                float fx = cx + rng.Range(-9, 9), fy = cy + rng.Range(-9, 9);
                var col = Jit(Rgb(170, 156, 112), ref rng, 0.08f);
                for (int w = 0; w < 4; w++) c.Ellipse(fx, fy, 4.2f, 1.8f, w * MathF.PI / 4 + rng.Range(0, 0.3f), col, 1, 0.2f);
            }
        }
    }

    private static void Tamarisk(Canvas c, VegRng rng)
    {
        // feathery sprays of tiny scale leaves on arching stems; pink flower plumes at most tips
        var leafA = Rgb(86, 104, 76); var leafB = Rgb(112, 128, 96);
        var pinkA = Rgb(164, 112, 118); var pinkB = Rgb(186, 138, 144);
        for (int s = 0; s < 11; s++)
        {
            float x = 128 + rng.Range(-20, 20), y = 254;
            float ang = rng.Range(-1.0f, 1.0f), bend = rng.Range(-0.6f, 0.6f);
            float len = rng.Range(150, 230);
            int steps = 26;
            bool pink = rng.Float() < 0.3f;
            for (int i = 0; i < steps; i++)
            {
                float t = (float)i / steps;
                float a = ang + bend * t;
                float nx = x + MathF.Sin(a) * len / steps, ny = y - MathF.Cos(a) * len / steps;
                var col = Jit(Mix(leafA, leafB, rng.Float()), ref rng, 0.08f);
                c.Line(x, y, nx, ny, 1.6f, 1.4f, col);
                // the fine side sprays
                for (int side = -1; side <= 1; side += 2)
                {
                    float sa = a + side * rng.Range(0.7f, 1.2f), sl = rng.Range(7, 15) * (1.1f - 0.5f * t);
                    c.Line(nx, ny, nx + MathF.Sin(sa) * sl, ny - MathF.Cos(sa) * sl, 1.3f, 0.6f, col);
                }
                if (pink && t > 0.62f)
                    for (int k = 0; k < 4; k++)
                        c.Ellipse(nx + rng.Range(-5, 5), ny + rng.Range(-5, 5), rng.Range(1.4f, 2.6f), rng.Range(1.4f, 2.6f), 0, Jit(Mix(pinkA, pinkB, rng.Float()), ref rng, 0.06f), 1, 0.2f);
                x = nx; y = ny;
            }
        }
    }

    private static void Willow(Canvas c, VegRng rng)
    {
        // slender stems with narrow lance leaves angled up along them, grey-green above, silvery below
        var stem = Rgb(140, 104, 60);
        var leafA = Rgb(112, 134, 84); var leafB = Rgb(170, 180, 150);
        for (int s = 0; s < 6; s++)
        {
            float x = 128 + rng.Range(-40, 40), y = 254, a = rng.Range(-0.35f, 0.35f);
            float len = rng.Range(170, 240);
            int steps = 9;
            for (int i = 0; i < steps; i++)
            {
                float nx = x + MathF.Sin(a) * len / steps, ny = y - MathF.Cos(a) * len / steps;
                c.Line(x, y, nx, ny, 2.0f, 1.6f, stem);
                for (int side = -1; side <= 1; side += 2)
                {
                    float la = a + side * rng.Range(0.35f, 0.7f);
                    float ll = rng.Range(13, 19);
                    var col = Jit(rng.Float() < 0.3f ? leafB : Mix(leafA, leafB, rng.Float() * 0.4f), ref rng, 0.06f);
                    c.Ellipse(nx + MathF.Sin(la) * ll, ny - MathF.Cos(la) * ll, ll, rng.Range(2.4f, 3.2f), la - MathF.PI / 2, col, 1, 0.35f);
                }
                x = nx; y = ny; a += rng.Bell() * 0.06f;
            }
        }
    }

    private static void Tumbleweed(Canvas c, VegRng rng)
    {
        // a round tangle of thin curling stems, straw to reddish, see-through
        var a0 = Rgb(196, 166, 116); var a1 = Rgb(160, 104, 78);
        for (int i = 0; i < 170; i++)
        {
            float ang = rng.Range(0, MathF.Tau), r = 118 * MathF.Pow(rng.Float(), 0.35f);
            float x0 = 128 + MathF.Cos(ang) * r, y0 = 128 + MathF.Sin(ang) * r;
            float ang2 = ang + rng.Range(1.2f, 2.4f) * (rng.Float() < 0.5f ? -1 : 1), r2 = 118 * MathF.Pow(rng.Float(), 0.35f);
            float x1 = 128 + MathF.Cos(ang2) * r2, y1 = 128 + MathF.Sin(ang2) * r2;
            var col = Jit(Mix(a0, a1, rng.Float()), ref rng, 0.08f);
            c.Curve(x0, y0, 128 + rng.Range(-40, 40), 128 + rng.Range(-40, 40), x1, y1, 1.8f, 1.2f, col);
        }
    }

    private static void ChollaSkin(Canvas c, VegRng rng)
    {
        // the joint's skin, round the tube in u and along it in v: a lattice of long tubercles, each with a tuft of pale sheathed spines
        var baseC = Rgb(118, 136, 84);
        c.Fill(baseC.Item1, baseC.Item2, baseC.Item3);
        int cols = 4, rows = 5;
        float cw = Canvas.S / (float)cols, rh = Canvas.S / (float)rows;
        for (int pass = 0; pass < 2; pass++)
            for (int j = -1; j <= rows; j++)
                for (int i = -1; i <= cols; i++)
                {
                    float x = (i + (j & 1) * 0.5f) * cw, y = (j + 0.5f) * rh;
                    if (pass == 0)
                    {
                        c.Ellipse(x, y, rh * 0.62f, cw * 0.36f, MathF.PI / 2, Rgb(86, 104, 60), 1, 0f);
                        c.Ellipse(x - 2, y - 3, rh * 0.5f, cw * 0.26f, MathF.PI / 2, Jit(Rgb(140, 156, 98), ref rng, 0.06f), 1, 0.5f);
                    }
                    else
                    {
                        float sx = x, sy = y - rh * 0.28f;
                        c.Ellipse(sx, sy, 3.5f, 3f, 0, Rgb(150, 130, 90));
                        for (int k = 0; k < 12; k++)
                        {
                            float a = rng.Range(0, MathF.Tau), l = rng.Range(10, 20);
                            c.Line(sx, sy, sx + MathF.Cos(a) * l, sy + MathF.Sin(a) * l * 0.8f, 2.2f, 0.8f, Jit(Rgb(232, 222, 186), ref rng, 0.05f));
                        }
                    }
                }
    }

    private static void PadSkin(Canvas c, VegRng rng)
    {
        // a glaucous blue-green pad, mottled, areoles in a diagonal lattice with a few yellow spines each
        var baseC = Rgb(112, 142, 98);
        for (int y = 0; y < Canvas.S; y++)
            for (int x = 0; x < Canvas.S; x++)
            {
                float n = 0.04f * MathF.Sin(x * 0.07f + MathF.Sin(y * 0.05f) * 2) + 0.03f * MathF.Sin(y * 0.11f + x * 0.03f);
                c.Set(x, y, baseC.Item1 * (1 + n), baseC.Item2 * (1 + n), baseC.Item3 * (1 + n * 1.5f), 1);
            }
        int grid = 7;
        float step = Canvas.S / (float)grid;
        for (int j = 0; j < grid; j++)
            for (int i = 0; i < grid; i++)
            {
                float x = (i + (j & 1) * 0.5f) * step + rng.Range(-3, 3), y = (j + 0.5f) * step + rng.Range(-3, 3);
                c.Ellipse(x, y, 3.2f, 3.2f, 0, Rgb(96, 70, 44));
                int sp = 1 + rng.Int(3);
                for (int k = 0; k < sp; k++)
                {
                    float a = MathF.PI / 2 + rng.Range(-0.8f, 0.8f), l = rng.Range(10, 18);
                    c.Line(x, y, x + MathF.Cos(a) * l, y + MathF.Sin(a) * l, 1.8f, 0.6f, Rgb(226, 206, 130));
                }
            }
    }

    private static void Blade(Canvas c, VegRng rng)
    {
        // a stiff leaf, neutral: darker toward the middle, the margins lighter with loose fibres
        for (int y = 0; y < Canvas.S; y++)
            for (int x = 0; x < Canvas.S; x++)
            {
                float u = (x + 0.5f) / Canvas.S;
                float edge = MathF.Min(u, 1 - u);
                float v = edge < 0.08f ? 1f : 0.82f + 0.06f * MathF.Sin(u * 40f) + 0.06f * (1 - MathF.Abs(u - 0.5f) * 2);
                c.Set(x, y, v, v, v * 0.97f, 1);
            }
    }

    private static void GrassBlade(Canvas c, VegRng rng)
    {
        var cols = new float[Canvas.S];
        for (int x = 0; x < Canvas.S; x++) cols[x] = 0.84f + 0.12f * rng.Bell();
        for (int y = 0; y < Canvas.S; y++)
            for (int x = 0; x < Canvas.S; x++)
            {
                float v = cols[x] * (0.92f + 0.08f * (1 - y / (float)Canvas.S));
                c.Set(x, y, v, v, v, 1);
            }
    }

    private static void YuccaFlowers(Canvas c, VegRng rng)
    {
        // a panicle of creamy nodding bells on short branches up a central stalk
        var stalk = Rgb(150, 140, 96);
        c.Line(128, 255, 128, 20, 4, 2, stalk);
        for (int i = 0; i < 16; i++)
        {
            float y = 230 - i * 13 + rng.Range(-4, 4);
            float width = 80 * (1 - i / 18f);
            for (int side = -1; side <= 1; side += 2)
            {
                float ex = 128 + side * width * rng.Range(0.5f, 1f), ey = y - rng.Range(8, 22);
                c.Line(128, y, ex, ey, 2.2f, 1.2f, stalk);
                for (int b = 0; b < 3; b++)
                {
                    float bx = 128 + (ex - 128) * (0.45f + 0.28f * b) + rng.Range(-3, 3), by = y + (ey - y) * (0.45f + 0.28f * b) + 6;
                    c.Ellipse(bx, by, 6.2f, 7.8f, rng.Range(-0.3f, 0.3f), Jit(Rgb(246, 240, 214), ref rng, 0.04f), 1, 0.45f);
                }
            }
        }
    }

    private static void DeadLeaves(Canvas c, VegRng rng)
    {
        var cols = new float[Canvas.S];
        for (int x = 0; x < Canvas.S; x++) cols[x] = 0.78f + 0.16f * rng.Bell();
        for (int y = 0; y < Canvas.S; y++)
            for (int x = 0; x < Canvas.S; x++) c.Set(x, y, cols[x], cols[x] * 0.97f, cols[x] * 0.92f, 1);
    }

    private static void Forb(Canvas c, VegRng rng)
    {
        // broom snakeweed: a dome of fine green stems fanning up from the base, each tipped with small yellow heads
        var stemA = Rgb(98, 124, 58); var stemB = Rgb(128, 146, 72);
        for (int i = 0; i < 70; i++)
        {
            float a = rng.Range(-1.25f, 1.25f);
            float len = (200 - 70 * MathF.Abs(a)) * rng.Range(0.75f, 1.05f);
            float x1 = 128 + MathF.Sin(a) * len, y1 = 255 - MathF.Cos(a) * len * 0.9f;
            var col = Jit(Mix(stemA, stemB, rng.Float()), ref rng, 0.06f);
            c.Curve(128 + rng.Range(-6, 6), 255, 128 + MathF.Sin(a) * len * 0.4f, 255 - len * 0.6f, x1, y1, 2.0f, 1.2f, col, 4);
            if (rng.Float() < 0.45f)
                c.Ellipse(x1 + rng.Range(-4, 4), y1 + rng.Range(-4, 4), 2.8f, 2.8f, 0, Jit(Rgb(196, 170, 64), ref rng, 0.07f), 1, 0.3f);
        }
    }

    private static void Panicle(Canvas c, VegRng rng)
    {
        // an airy grass panicle: fine branches off a central stem, tiny seeds along them
        var col = Rgb(204, 186, 140);
        c.Line(128, 255, 128, 10, 3.2f, 1.8f, col);
        for (int i = 0; i < 26; i++)
        {
            float y = 240 - i * 9;
            float a = (i % 2 == 0 ? -1 : 1) * rng.Range(0.5f, 1.2f);
            float len = rng.Range(30, 95) * (1 - i / 34f);
            float x1 = 128 + MathF.Sin(a) * len, y1 = y - MathF.Cos(a) * len;
            // branches and seeds stout enough to hold together as a haze down the mips (not a scatter of dots)
            c.Line(128, y, x1, y1, 2.4f, 1.6f, col);
            for (int k = 0; k < 7; k++)
            {
                float t = rng.Float();
                c.Ellipse(128 + (x1 - 128) * t, y + (y1 - y) * t, 2.8f, 2.0f, a, Jit(col, ref rng, 0.08f), 1, 0.2f);
            }
        }
    }

    private static void GrassTuft(Canvas c, VegRng rng)
    {
        // a bunchgrass tuft seen from the side, filling the cell (the card is sized to the tuft): a crown of grey-brown
        // thatch, a cushion of short blades, long ones arching out and over; grey-green and straw blades (the hues
        // about the mesh's colour, which carries the species)
        var green = (0.74f, 0.84f, 0.72f); var straw = (0.92f, 0.84f, 0.66f); var grey = (0.74f, 0.71f, 0.65f);
        (float, float, float) Blade(ref VegRng r)
        {
            float u = r.Float();
            var col = u < 0.42f ? Mix(straw, grey, r.Float() * 0.5f) : Mix(green, straw, r.Float() * 0.35f);
            return Jit(col, ref r, 0.08f);
        }
        void Arch(ref VegRng r, float len, float a, float x0, float bend, float w)
        {
            float dx = MathF.Sin(a), dy = -MathF.Cos(a);
            float cx = x0 + dx * len * 0.55f, cy = 255 + dy * len * 0.75f;
            float x1 = x0 + dx * len * (0.75f + 0.3f * bend), y1 = 255 + dy * len * (0.95f - 0.9f * bend * MathF.Abs(dx) - 0.25f * bend);
            x1 = Math.Clamp(x1, 4, 252); y1 = Math.Clamp(y1, 6, 252);
            c.Curve(x0, 255, cx, cy, x1, y1, w, 0.8f, Blade(ref r), 6);
        }
        // the long blades behind, then the cushion, the thatch in front
        for (int i = 0; i < 46; i++)
        {
            float a = (rng.Float() < 0.5f ? -1 : 1) * rng.Range(0.15f, 1.05f);
            Arch(ref rng, rng.Range(170, 250), a, 128 + rng.Range(-18, 18), rng.Range(0.3f, 1f), rng.Range(2.6f, 3.6f));
        }
        for (int i = 0; i < 95; i++)
        {
            float a = rng.Range(-1f, 1f) * MathF.Pow(rng.Float(), 0.5f) * 1.1f;
            Arch(ref rng, rng.Range(70, 150), a, 128 + rng.Range(-24, 24), rng.Range(0.1f, 0.6f), rng.Range(2.6f, 3.6f));
        }
        for (int i = 0; i < 40; i++)
        {
            float a = rng.Range(-1.35f, 1.35f), len = rng.Range(18, 55), x0 = 128 + rng.Range(-30, 30);
            var col = Jit(Mix(grey, straw, rng.Float() * 0.4f), ref rng, 0.08f);
            col = (col.Item1 * 0.82f, col.Item2 * 0.8f, col.Item3 * 0.78f);
            c.Line(x0, 255, x0 + MathF.Sin(a) * len, 255 - MathF.Cos(a) * len, rng.Range(3.5f, 5.5f), 1.5f, col);
        }
    }

    // ---- the regions' cells ------------------------------------------------------------------------------------

    /// <summary>A lobed leaf from its stalk at (x, y) along angle a (0: up): a blade with rounded lobes either side.</summary>
    private static void LobedLeaf(Canvas c, float x, float y, float a, float len, (float, float, float) col, int lobes, float lobe)
    {
        float dx = MathF.Sin(a), dy = -MathF.Cos(a);
        c.Ellipse(x + dx * len * 0.55f, y + dy * len * 0.55f, len * 0.5f, len * 0.17f, a - MathF.PI / 2, col, 1, 0.4f);
        for (int k = 0; k < lobes; k++)
        {
            float t = 0.3f + 0.55f * k / Math.Max(1, lobes - 1);
            float px = x + dx * len * t, py = y + dy * len * t;
            for (int side = -1; side <= 1; side += 2)
            {
                float la = a + side * 1.15f;
                float r = len * lobe * (1.1f - 0.4f * t);
                c.Ellipse(px + MathF.Sin(la) * r, py - MathF.Cos(la) * r, r * 1.1f, r * 0.8f, la - MathF.PI / 2, col, 1, 0.4f);
            }
        }
    }

    private static void SandSage(Canvas c, VegRng rng)
    {
        // sand sagebrush: fine stems fanning up, clothed in thread-like silvery leaves in soft feathery tufts
        var stem = Rgb(120, 116, 100);
        var leafA = Rgb(112, 126, 106); var leafB = Rgb(160, 170, 152);
        var r = rng;
        for (int t = 0; t < 9; t++)
        {
            float ang = (t - 4) * 0.22f + rng.Bell() * 0.15f;
            Twig(c, ref rng, 128 + rng.Range(-16, 16), 254, ang, rng.Range(150, 220), 2.4f, 2, 0.5f, stem, (x, y, a, d) =>
            {
                for (int k = 0; k < 10; k++)
                {
                    float la = a + r.Range(-1.2f, 1.2f), ll = r.Range(12, 26);
                    var col = Jit(Mix(leafA, leafB, r.Float()), ref r, 0.06f);
                    float ox = x + r.Range(-4, 4), oy = y + r.Range(-4, 4);
                    c.Curve(ox, oy, ox + MathF.Sin(la) * ll * 0.5f, oy - MathF.Cos(la) * ll * 0.6f, ox + MathF.Sin(la + 0.35f) * ll, oy - MathF.Cos(la + 0.35f) * ll, 2.0f, 1.0f, col, 3);
                }
            }, bend: rng.Bell() * 0.3f);
        }
    }

    private static void ShinOak(Canvas c, VegRng rng)
    {
        // shinnery oak: short twigs crowded with glossy lobed leaves, here and there the pale underside turned up
        var stem = Rgb(104, 92, 78);
        var leafA = Rgb(58, 92, 36); var leafB = Rgb(98, 126, 50); var under = Rgb(150, 160, 118);
        var r = rng;
        for (int t = 0; t < 7; t++)
        {
            float ang = (t - 3) * 0.3f + rng.Bell() * 0.15f;
            Twig(c, ref rng, 128 + rng.Range(-20, 20), 254, ang, rng.Range(140, 215), 3f, 1, 0.7f, stem, (x, y, a, d) =>
            {
                for (int k = 0; k < 2; k++)
                {
                    float la = a + (k == 0 ? -1 : 1) * r.Range(0.5f, 1.1f);
                    var col = r.Float() < 0.16f ? under : Jit(Mix(leafA, leafB, r.Float()), ref r, 0.08f);
                    LobedLeaf(c, x, y, la, r.Range(28, 40), col, 3, 0.15f);
                }
            }, bend: rng.Bell() * 0.3f);
        }
    }

    private static void GrayOak(Canvas c, VegRng rng)
    {
        // gray oak: twiggy sprays of small, dull, grey-green oval leaves, dense
        var stem = Rgb(90, 80, 70);
        var leafA = Rgb(60, 76, 52); var leafB = Rgb(98, 112, 82);
        var r = rng;
        for (int t = 0; t < 7; t++)
        {
            float ang = (t - 3) * 0.3f + rng.Bell() * 0.15f;
            Twig(c, ref rng, 128 + rng.Range(-18, 18), 254, ang, rng.Range(150, 220), 3f, 2, 0.6f, stem, (x, y, a, d) =>
            {
                for (int k = 0; k < 3; k++)
                {
                    float la = a + r.Range(-1.3f, 1.3f), ll = r.Range(13, 20);
                    var col = Jit(Mix(leafA, leafB, r.Float()), ref r, 0.08f);
                    c.Ellipse(x + MathF.Sin(la) * ll * 0.6f, y - MathF.Cos(la) * ll * 0.6f, ll * 0.55f, ll * 0.32f, la - MathF.PI / 2, col, 1, 0.45f);
                }
            }, bend: rng.Bell() * 0.3f);
        }
    }

    private static void OcotilloLeaf(Canvas c, VegRng rng)
    {
        // ocotillo in leaf: tufts of small bright obovate leaves at the spine bases up a cane (the cane is the mesh's tube, up the cell's middle)
        var leafA = Rgb(88, 126, 40); var leafB = Rgb(132, 162, 58);
        for (float y = 246; y > 14; y -= rng.Range(13, 20))
        {
            float x = 128 + rng.Range(-3, 3);
            c.Line(x, y, x + rng.Range(-10, 10), y - 9, 2.4f, 0.6f, Rgb(118, 106, 90));
            int n = 4 + rng.Int(4);
            for (int k = 0; k < n; k++)
            {
                float la = rng.Range(-1.7f, 1.7f), ll = rng.Range(13, 22);
                var col = Jit(Mix(leafA, leafB, rng.Float()), ref rng, 0.07f);
                c.Ellipse(x + MathF.Sin(la) * ll * 0.55f, y - MathF.Cos(la) * ll * 0.55f, ll * 0.5f, ll * 0.26f, la - MathF.PI / 2, col, 1, 0.4f);
            }
        }
    }

    private static void RedFlowers(Canvas c, VegRng rng)
    {
        // the ocotillo's flower spike at a cane's tip: a narrow dense cluster of scarlet tubular flowers
        var stalk = Rgb(120, 96, 80);
        c.Line(128, 255, 128, 26, 4, 2, stalk);
        for (int i = 0; i < 60; i++)
        {
            float t = rng.Float();
            float y = 230 - t * 200;
            float side = rng.Float() < 0.5f ? -1 : 1;
            float a = side * rng.Range(0.25f, 0.9f) * (1 - 0.5f * t);
            float l = rng.Range(16, 26) * (1 - 0.35f * t);
            float x1 = 128 + MathF.Sin(a) * l, y1 = y - MathF.Cos(a) * l;
            var col = Jit(Mix(Rgb(200, 44, 30), Rgb(236, 92, 40), rng.Float()), ref rng, 0.06f);
            c.Line(128, y, x1, y1, 5.5f, 3.5f, col);
            c.Ellipse(x1, y1, 2.6f, 2.6f, 0, Rgb(240, 170, 70), 1, 0.2f);
        }
    }

    private static void IndianBlanket(Canvas c, VegRng rng)
    {
        // Indian blanket: a low branching forb, hairy stems and narrow leaves below, flower heads above: rays red with
        // yellow tips round a domed dark red disc
        var stemA = Rgb(96, 116, 64); var stemB = Rgb(122, 138, 80);
        var heads = new List<(float X, float Y, float R)>();
        for (int i = 0; i < 15; i++)
        {
            float a = rng.Range(-1.15f, 1.15f);
            float len = rng.Range(130, 215) * (1 - 0.3f * MathF.Abs(a));
            float x0 = 128 + rng.Range(-8, 8);
            float x1 = x0 + MathF.Sin(a) * len, y1 = 255 - MathF.Cos(a) * len;
            var col = Jit(Mix(stemA, stemB, rng.Float()), ref rng, 0.06f);
            c.Curve(x0, 255, x0 + MathF.Sin(a) * len * 0.3f, 255 - len * 0.6f, x1, y1, 3f, 1.8f, col, 4);
            for (int k = 0; k < 3; k++)
            {
                float t = 0.15f + 0.2f * k;
                float px = x0 + (x1 - x0) * t, py = 255 + (y1 - 255) * t;
                float la = a + (k % 2 == 0 ? -1 : 1) * 0.9f;
                c.Ellipse(px + MathF.Sin(la) * 9, py - MathF.Cos(la) * 9, 11, 3, la - MathF.PI / 2, Jit(stemA, ref rng, 0.06f), 1, 0.3f);
            }
            heads.Add((x1, y1, rng.Range(13, 19)));
        }
        foreach (var (x, y, r) in heads)
        {
            int rays = 13;
            float tilt = rng.Range(0.45f, 0.75f);
            for (int k = 0; k < rays; k++)
            {
                float ra = k * MathF.Tau / rays + rng.Range(-0.1f, 0.1f);
                float ex = MathF.Cos(ra), ey = MathF.Sin(ra) * tilt;
                c.Line(x + ex * r * 0.25f, y + ey * r * 0.25f, x + ex * r * 0.92f, y + ey * r * 0.92f, r * 0.44f, r * 0.36f, Jit(Rgb(192, 44, 34), ref rng, 0.06f));
                c.Line(x + ex * r * 0.86f, y + ey * r * 0.86f, x + ex * r * 1.1f, y + ey * r * 1.1f, r * 0.34f, r * 0.22f, Jit(Rgb(236, 194, 60), ref rng, 0.05f));
            }
            c.Ellipse(x, y - r * 0.12f, r * 0.36f, r * 0.3f, 0, Rgb(112, 38, 30), 1, 0.4f);
        }
    }

    private static void PricklyPoppy(Canvas c, VegRng rng)
    {
        // prickly poppy: stout pale blue-green stems, deeply lobed spiny leaves clasping them, big white cup flowers with a yellow boss
        var stem = Rgb(150, 168, 150); var leafA = Rgb(122, 148, 130); var leafB = Rgb(160, 178, 160);
        var tops = new List<(float, float)>();
        for (int i = 0; i < 6; i++)
        {
            float a = rng.Range(-0.6f, 0.6f);
            float len = rng.Range(150, 235);
            float x0 = 128 + rng.Range(-10, 10);
            float x1 = x0 + MathF.Sin(a) * len, y1 = 255 - MathF.Cos(a) * len;
            c.Curve(x0, 255, x0 + MathF.Sin(a) * len * 0.4f, 255 - len * 0.5f, x1, y1, 4.5f, 2.5f, stem, 5);
            for (int k = 0; k < 4; k++)
            {
                float t = 0.12f + 0.17f * k;
                float px = x0 + (x1 - x0) * t, py = 255 + (y1 - 255) * t;
                float la = a + (k % 2 == 0 ? -1 : 1) * rng.Range(0.7f, 1.2f);
                var col = Jit(Mix(leafA, leafB, rng.Float()), ref rng, 0.06f);
                LobedLeaf(c, px, py, la, rng.Range(34, 50), col, 4, 0.12f);
            }
            tops.Add((x1, y1));
        }
        foreach (var (x, y) in tops)
        {
            float r = rng.Range(17, 23);
            for (int p = 0; p < 6; p++)
            {
                float pa = p * MathF.Tau / 6 + rng.Range(-0.2f, 0.2f);
                c.Ellipse(x + MathF.Cos(pa) * r * 0.5f, y + MathF.Sin(pa) * r * 0.38f - 2, r * 0.56f, r * 0.42f, pa, Jit(Rgb(246, 244, 236), ref rng, 0.03f), 1, 0.35f);
            }
            c.Ellipse(x, y - 2, r * 0.32f, r * 0.27f, 0, Rgb(232, 196, 60), 1, 0.4f);
        }
    }

    private static void Horsemint(Canvas c, VegRng rng)
    {
        // purple horsemint: square upright stems, each with two to four tiered whorls of pink-purple bracts, pale flowers on them
        var stem = Rgb(104, 120, 70); var leaf = Rgb(96, 118, 64);
        var bractA = Rgb(164, 100, 166); var bractB = Rgb(206, 150, 200);
        for (int i = 0; i < 9; i++)
        {
            float x0 = 128 + rng.Range(-34, 34), a = rng.Range(-0.35f, 0.35f);
            float len = rng.Range(150, 235);
            float x1 = x0 + MathF.Sin(a) * len, y1 = 255 - MathF.Cos(a) * len;
            c.Line(x0, 255, x1, y1, 3.2f, 2f, stem);
            for (int k = 0; k < 3; k++)
            {
                float t = 0.12f + 0.12f * k;
                float px = x0 + (x1 - x0) * t, py = 255 + (y1 - 255) * t;
                for (int sd = -1; sd <= 1; sd += 2)
                {
                    float la = a + sd * 1.1f;
                    c.Ellipse(px + MathF.Sin(la) * 10, py - MathF.Cos(la) * 10, 11, 3.2f, la - MathF.PI / 2, Jit(leaf, ref rng, 0.06f), 1, 0.3f);
                }
            }
            int whorls = 2 + rng.Int(3);
            for (int w = 0; w < whorls; w++)
            {
                float t = 1 - w * 0.17f;
                float px = x0 + (x1 - x0) * t, py = 255 + (y1 - 255) * t;
                float r = 17 - 2.5f * w;
                for (int k = 0; k < 10; k++)
                {
                    float ba = k * MathF.Tau / 10 + rng.Range(-0.15f, 0.15f);
                    float bx = px + MathF.Cos(ba) * r, by = py + MathF.Sin(ba) * r * 0.4f + 3;
                    c.Line(px, py, bx, by + 3, 5f, 2.4f, Jit(Mix(bractA, bractB, rng.Float()), ref rng, 0.06f));
                }
                for (int k = 0; k < 6; k++)
                    c.Ellipse(px + rng.Range(-r * 0.7f, r * 0.7f), py - rng.Range(3, 9), 3.2f, 2.4f, 0, Jit(Rgb(232, 208, 230), ref rng, 0.04f), 1, 0.2f);
            }
        }
    }

    private static void Buffalograss(Canvas c, VegRng rng)
    {
        // buffalograss from the side, for a card about five times wider than tall: a dense mat of short curling blades,
        // neutral (the mesh colours it); drawn tall here, squashed by the card
        for (int i = 0; i < 300; i++)
        {
            float x0 = rng.Range(2, 254);
            float h = rng.Range(90, 240) * (1 - 0.5f * MathF.Pow(MathF.Abs(x0 - 128) / 128f, 2));
            float curl = rng.Range(6, 22) * (rng.Float() < 0.5f ? -1 : 1);
            float v = rng.Range(0.7f, 1f);
            c.Curve(x0, 256, x0 + curl * 0.2f, 255 - h * 1.1f, x0 + curl, 255 - h * rng.Range(0.6f, 0.95f), rng.Range(3.2f, 4.6f), 1.6f, (v, v, v * 0.95f), 5);
        }
    }

    private static void Pickleweed(Canvas c, VegRng rng)
    {
        // pickleweed (iodine bush): jointed succulent stems, chains of fleshy beads branching upward, green going red at the tips
        var green = Rgb(112, 142, 70); var red = Rgb(172, 82, 64); var wood = Rgb(120, 106, 88);
        void Branch(float x, float y, float a, int beads, int depth, ref VegRng r)
        {
            for (int i = 0; i < beads; i++)
            {
                float t = (float)i / beads;
                float bl = r.Range(9, 13);
                float nx = x + MathF.Sin(a) * bl, ny = y - MathF.Cos(a) * bl;
                var col = Jit(Mix(green, red, Math.Clamp(t * 1.3f - 0.35f + 0.3f * r.Bell(), 0, 1)), ref r, 0.06f);
                c.Ellipse((x + nx) / 2, (y + ny) / 2, bl * 0.62f, bl * 0.44f, a - MathF.PI / 2, col, 1, 0.45f);
                if (depth > 0 && r.Float() < 0.3f) Branch(nx, ny, a + (r.Float() < 0.5f ? -1 : 1) * r.Range(0.4f, 0.8f), beads - i - 1, depth - 1, ref r);
                x = nx; y = ny; a += r.Bell() * 0.15f;
            }
        }
        for (int s = 0; s < 13; s++)
        {
            float x0 = 128 + rng.Range(-20, 20), a = rng.Range(-1.0f, 1.0f);
            float x1 = x0 + MathF.Sin(a) * 30, y1 = 255 - MathF.Cos(a) * 30;
            c.Line(x0, 255, x1, y1, 4, 3, wood);
            Branch(x1, y1, a * 0.7f, 10 + rng.Int(8), 2, ref rng);
        }
    }

    private static void AgaveBlade(Canvas c, VegRng rng)
    {
        // a thick toothed leaf, neutral (the mesh colours it): u across, v from the base (top of the cell) to the tip
        // (bottom); the margins clear but for hooked dark teeth, a dark terminal spine
        const int S = Canvas.S;
        for (int y = 0; y < S; y++)
            for (int x = 0; x < S; x++)
            {
                float u = (x + 0.5f) / S, v = (y + 0.5f) / S;
                float e = MathF.Abs(u - 0.5f) * 2;   // 0 the midrib, 1 the margin
                if (e > 0.9f) continue;
                float shade = 0.84f + 0.1f * (1 - e) + 0.03f * MathF.Sin(v * 40f + u * 3f);
                if (e > 0.8f) shade *= 0.86f;   // the darker margin line
                float spine = Math.Clamp((v - 0.9f) / 0.06f, 0, 1);
                float r = shade, g = shade, b = shade * 0.97f;
                r += (0.36f - r) * spine; g += (0.27f - g) * spine; b += (0.22f - b) * spine;
                c.Set(x, y, r, g, b, 1);
            }
        // the teeth: small triangles off both margins, hooked toward the tip
        for (float y = 10; y < S * 0.9f; y += rng.Range(14, 20))
            for (int side = -1; side <= 1; side += 2)
            {
                float x0 = 128 + side * S * 0.43f;
                c.Line(x0, y, x0 + side * 16, y + 7, 8, 1.2f, Rgb(98, 72, 58));
            }
    }

    private static void PineSpray(Canvas c, VegRng rng)
    {
        // a pine's branch end seen from above: a twig up the middle, side twigs off it and twigs off those, every one a
        // bottle-brush of needles angled forward, dark blue-green, the new growth at the tips paler; dense enough to read
        // as a mass of foliage from a few metres (the placed pines, PlacedTrees)
        var twig = Rgb(88, 70, 54);
        var needleA = Rgb(30, 48, 34); var needleB = Rgb(56, 80, 54); var fresh = Rgb(92, 116, 74);
        void Shoot(float x, float y, float a, float len, float reach, int depth, ref VegRng r)
        {
            int steps = Math.Max(2, (int)(len / 2.4f));
            for (int i = 0; i < steps; i++)
            {
                float t = (float)i / steps;
                float nx = x + MathF.Sin(a) * len / steps, ny = y - MathF.Cos(a) * len / steps;
                c.Line(x, y, nx, ny, 2.8f - 1.4f * t, 2.4f - 1.4f * t, twig);
                for (int side = -1; side <= 1; side += 2)
                {
                    float na = a + side * r.Range(0.4f, 1.05f), nl = reach * r.Range(0.7f, 1.1f) * (1.05f - 0.4f * t);
                    var col = t > 0.8f ? Jit(Mix(needleB, fresh, r.Float()), ref r, 0.06f) : Jit(Mix(needleA, needleB, r.Float()), ref r, 0.08f);
                    c.Line(nx, ny, nx + MathF.Sin(na) * nl, ny - MathF.Cos(na) * nl, 2.2f, 1.1f, col);
                }
                // twigs off this one
                if (depth > 0 && i > 1 && i < steps - 3 && r.Float() < 0.16f)
                {
                    float side = r.Float() < 0.5f ? -1 : 1;
                    Shoot(nx, ny, a + side * r.Range(0.55f, 0.9f), len * (1 - t) * r.Range(0.45f, 0.7f), reach * 0.85f, depth - 1, ref r);
                }
                x = nx; y = ny; a += r.Bell() * 0.035f;
            }
        }
        float mx = 128, my = 254, ma = rng.Range(-0.06f, 0.06f), mlen = 238;
        // the side twigs first, alternating, shorter toward the tip; then the leading one over them
        int k = 0;
        for (float d = 14; d < mlen - 26; d += rng.Range(13, 19), k++)
        {
            float t = d / mlen;
            float side = k % 2 == 0 ? -1 : 1;
            float px = mx + MathF.Sin(ma) * d, py = my - MathF.Cos(ma) * d;
            Shoot(px, py, ma + side * rng.Range(0.55f, 0.85f), rng.Range(96, 132) * (1.12f - 0.8f * t), 21, 1, ref rng);
        }
        Shoot(mx, my, ma, mlen, 23, 0, ref rng);
    }

    private static void Candelilla(Canvas c, VegRng rng)
    {
        // candelilla from the side: a clump of thin waxy pale grey-green stems, near upright, a highlight down each
        for (int i = 0; i < 34; i++)
        {
            float x0 = 128 + rng.Range(-48, 48);
            float a = (x0 - 128) / 128f * 0.5f + rng.Range(-0.12f, 0.12f);
            float len = rng.Range(140, 245);
            float x1 = x0 + MathF.Sin(a) * len, y1 = 255 - MathF.Cos(a) * len;
            var col = Jit(Rgb(116, 134, 108), ref rng, 0.06f);
            c.Curve(x0, 255, x0 + MathF.Sin(a) * len * 0.6f, 255 - len * 0.5f, x1, y1, 5f, 4f, col, 4);
            c.Curve(x0 - 1, 255, x0 - 1 + MathF.Sin(a) * len * 0.6f, 255 - len * 0.5f, x1 - 1, y1, 1.4f, 1f, Mix(col, Rgb(196, 206, 190), 0.5f), 4);
        }
    }
}
