namespace I76.Formats.Imaging;

/// <summary>
/// The texel-space clean-up of a texture's label map after back-projection, general rules rather than per-car fixes:
/// glass gaps closed (a window's hatching or frame bars), then every connected region of a non-paint class is kept only if it is big
/// enough for its class and, for glass, solid (area against its bounding box), mid-tone and smooth (a window is a gradient, a vent or a
/// stripe is not). Rejected regions become paint (preset 1).
/// </summary>
public static class CarMaskCleaner
{
    public const byte Paint = 1, Glass = 11;

    /// <summary>The smallest region, in texels, each class keeps (0: any size). Thin chrome trim and lamps are small by nature.</summary>
    public static int MinRegion(byte cls) => cls switch { 11 => 40, 5 => 12, 6 => 14, 7 => 30, 13 => 10, 14 => 30, 12 => 6, _ => 0 };

    public sealed record Region(byte Class, int Area, int X0, int Y0, int X1, int Y1, float Solidity, float MeanLuma, float LumaStd, float MeanSat, int Index);

    public static byte[] Clean(byte[] ids, byte[] rgb, int w, int h, Action<Region, string>? log = null)
    {
        var o = (byte[])ids.Clone();
        CloseGlass(o, w, h);
        var seen = new bool[w * h];
        var stack = new Stack<int>();
        for (int s = 0; s < o.Length; s++)
        {
            byte c = o[s];
            if (seen[s] || c <= Paint) { seen[s] = true; continue; }
            var px = new List<int>();
            stack.Push(s); seen[s] = true;
            int x0 = w, y0 = h, x1 = 0, y1 = 0;
            while (stack.Count > 0)
            {
                int p = stack.Pop(); px.Add(p);
                int x = p % w, y = p / w;
                x0 = Math.Min(x0, x); x1 = Math.Max(x1, x); y0 = Math.Min(y0, y); y1 = Math.Max(y1, y);
                void Try(int xx, int yy) { if (xx < 0 || yy < 0 || xx >= w || yy >= h) return; int q = yy * w + xx; if (!seen[q] && o[q] == c) { seen[q] = true; stack.Push(q); } }
                Try(x - 1, y); Try(x + 1, y); Try(x, y - 1); Try(x, y + 1);
            }
            double sl = 0, sl2 = 0, ss = 0;
            foreach (int p in px)
            {
                float r = rgb[p * 3] / 255f, g = rgb[p * 3 + 1] / 255f, b = rgb[p * 3 + 2] / 255f;
                float mx = MathF.Max(r, MathF.Max(g, b)), mn = MathF.Min(r, MathF.Min(g, b));
                float l = 0.299f * r + 0.587f * g + 0.114f * b;
                sl += l; sl2 += l * l; ss += mx <= 1e-4f ? 0 : (mx - mn) / mx;
            }
            double n = px.Count, mean = sl / n;
            var reg = new Region(c, px.Count, x0, y0, x1, y1, (float)(n / ((x1 - x0 + 1) * (y1 - y0 + 1))), (float)mean, (float)Math.Sqrt(Math.Max(0, sl2 / n - mean * mean)), (float)(ss / n), s);
            string? why = null;
            if (px.Count < MinRegion(c)) why = "small";
            else if (c == Glass && px.Count < 400 && reg.Solidity < 0.55f) why = "hollow";
            else if (c == Glass && px.Count < 700 && Math.Min(x1 - x0, y1 - y0) + 1 <= 10) why = "strip";   // a dark skirt or a seam, not a pane
            else if (c is 5 or 6 or 13 && px.Count < 150 && reg.Solidity < 0.5f) why = "wispy";
            else if (c == Glass && reg.LumaStd > 0.16f) why = "busy";
            if (log is not null) log(reg, why ?? "keep");
            if (why is not null) foreach (int p in px) o[p] = Paint;
        }
        return o;
    }

    /// <summary>
    /// The geometric clean-up of a raw projection (no colour rules): between paint and glass only, so thin trim, lamps and badges stay as
    /// they are. A texel goes with a clear 3x3 majority (6 of its 8 neighbours), then a glass speck or a paint hole of under
    /// <paramref name="island"/> texels takes the class round it (a speck of window on a roof, a fleck of body inside a window).
    /// </summary>
    public static byte[] Denoise(byte[] ids, int w, int h, int island = 24)
    {
        static bool Pg(byte c) => c is Paint or Glass;
        var o = (byte[])ids.Clone();
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                byte c = ids[y * w + x];
                if (!Pg(c)) continue;
                int same = 0, other = 0;
                byte oc = c == Paint ? Glass : Paint;
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if ((dx | dy) == 0) continue;
                        int xx = x + dx, yy = y + dy;
                        if (xx < 0 || yy < 0 || xx >= w || yy >= h) continue;
                        byte n = ids[yy * w + xx];
                        if (n == c) same++; else if (n == oc) other++;
                    }
                if (other >= 6) o[y * w + x] = oc;
            }
        var seen = new bool[w * h];
        var stack = new Stack<int>();
        for (int s = 0; s < o.Length; s++)
        {
            byte c = o[s];
            if (seen[s] || !Pg(c)) continue;
            var px = new List<int>();
            int toOther = 0, toElse = 0;
            byte oc = c == Paint ? Glass : Paint;
            stack.Push(s); seen[s] = true;
            while (stack.Count > 0)
            {
                int p = stack.Pop(); px.Add(p);
                int x = p % w, y = p / w;
                void Try(int xx, int yy)
                {
                    if (xx < 0 || yy < 0 || xx >= w || yy >= h) { toElse++; return; }
                    int q = yy * w + xx;
                    if (o[q] == c) { if (!seen[q]) { seen[q] = true; stack.Push(q); } }
                    else if (o[q] == oc) toOther++; else toElse++;
                }
                Try(x - 1, y); Try(x + 1, y); Try(x, y - 1); Try(x, y + 1);
            }
            // an island wholly (or nearly) inside the other class only: one touching trim, a lamp or the texture's edge is left alone
            if (px.Count < island && toOther > 0 && toElse * 4 <= toOther) foreach (int p in px) o[p] = oc;
        }
        return o;
    }

    /// <summary>
    /// Each window the segmentation found, redrawn on the texture's own colours (its texels the paint round it runs into with no edge
    /// between are dropped first: a stretch of roof): the window's colours are learnt from its core (the
    /// region less a texel of border), and within <paramref name="reach"/> texels of the found outline a texel is glass if its colour
    /// is near them (a per-channel z of 2.2 against the core's spread); the part joined to the core is kept and its enclosed holes filled
    /// (a highlight painted across the pane). Texels that stop being glass take the class most common round them, else paint.
    /// </summary>
    public static byte[] RefineGlass(byte[] ids, byte[] rgb, int w, int h, int reach = 2, float z = 2.2f)
    {
        var o = (byte[])ids.Clone();
        var comp = Components(ids, w, h, Glass, eight: true);
        foreach (var found in comp)
        {
            if (found.Count < 6) continue;
            // the found texels the colour of the paint round them (the most common colour 3 to 5 texels out, past the pane's own shaded
            // rim, 16 levels a channel) are not glass: a stretch of black roof the segmentation joined to a side window
            var ring = new Dictionary<int, (int N, long R, long G, long B)>();
            var foundSet = new HashSet<int>(found);
            var close = new HashSet<int>();
            foreach (int p in found)
                for (int dy = -2; dy <= 2; dy++)
                    for (int dx = -2; dx <= 2; dx++)
                    {
                        int xx = p % w + dx, yy = p / w + dy;
                        if (xx >= 0 && yy >= 0 && xx < w && yy < h) close.Add(yy * w + xx);
                    }
            foreach (int p in found)
                for (int dy = -5; dy <= 5; dy++)
                    for (int dx = -5; dx <= 5; dx++)
                    {
                        int xx = p % w + dx, yy = p / w + dy;
                        if (xx < 0 || yy < 0 || xx >= w || yy >= h) continue;
                        int q = yy * w + xx;
                        if (ids[q] == 0 || ids[q] == Glass || close.Contains(q)) continue;
                        int key = (rgb[q * 3] >> 4) << 8 | (rgb[q * 3 + 1] >> 4) << 4 | rgb[q * 3 + 2] >> 4;
                        var e = ring.GetValueOrDefault(key);
                        ring[key] = (e.N + 1, e.R + rgb[q * 3], e.G + rgb[q * 3 + 1], e.B + rgb[q * 3 + 2]);
                    }
            var px = found;
            if (ring.Count > 0)
            {
                var mode = ring.Values.MaxBy(e => e.N);
                double a0 = (double)mode.R / mode.N, a1 = (double)mode.G / mode.N, a2 = (double)mode.B / mode.N;
                bool PaintColoured(int p) => Math.Sqrt(Sq(rgb[p * 3] - a0) + Sq(rgb[p * 3 + 1] - a1) + Sq(rgb[p * 3 + 2] - a2)) < 30;
                bool Step(int a, int b) => Math.Abs(rgb[a * 3] - rgb[b * 3]) + Math.Abs(rgb[a * 3 + 1] - rgb[b * 3 + 1]) + Math.Abs(rgb[a * 3 + 2] - rgb[b * 3 + 2]) < 24;
                // ...and only those the paint runs into with no edge between: a painted pane has an outline or a frame round it even where
                // its colour is near the body's (a dark blue windshield on a dark blue car), a stretch of roof does not
                var gone = new HashSet<int>();
                var fl = new Stack<int>();
                foreach (int p in found)
                {
                    if (!PaintColoured(p)) continue;
                    int x = p % w, y = p / w;
                    foreach (var (dx, dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                    {
                        int xx = x + dx, yy = y + dy;
                        if (xx < 0 || yy < 0 || xx >= w || yy >= h) continue;
                        int q = yy * w + xx;
                        if (foundSet.Contains(q) || ids[q] == 0 || ids[q] == Glass || !PaintColoured(q) || !Step(p, q)) continue;
                        if (gone.Add(p)) fl.Push(p);
                        break;
                    }
                }
                while (fl.Count > 0)
                {
                    int p = fl.Pop(); int x = p % w, y = p / w;
                    foreach (var (dx, dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                    {
                        int xx = x + dx, yy = y + dy;
                        if (xx < 0 || yy < 0 || xx >= w || yy >= h) continue;
                        int q = yy * w + xx;
                        if (!foundSet.Contains(q) || gone.Contains(q) || !PaintColoured(q) || !Step(p, q)) continue;
                        gone.Add(q); fl.Push(q);
                    }
                }
                px = found.Where(p => !gone.Contains(p)).ToList();
                foreach (int p in found) if (px.Count < 6 || gone.Contains(p)) o[p] = 255;
                if (px.Count < 6) continue;
            }
            var inC = new HashSet<int>(px);
            // the core: the region's texels whose 4 neighbours are all in it (the whole region if that leaves too little)
            var core = px.Where(p => { int x = p % w, y = p / w; return x > 0 && y > 0 && x < w - 1 && y < h - 1 && inC.Contains(p - 1) && inC.Contains(p + 1) && inC.Contains(p - w) && inC.Contains(p + w); }).ToList();
            if (core.Count < 4) core = px;
            Span<double> mean = stackalloc double[3], sd = stackalloc double[3];
            foreach (int p in core) for (int c = 0; c < 3; c++) mean[c] += rgb[p * 3 + c];
            for (int c = 0; c < 3; c++) mean[c] /= core.Count;
            foreach (int p in core) for (int c = 0; c < 3; c++) sd[c] += (rgb[p * 3 + c] - mean[c]) * (rgb[p * 3 + c] - mean[c]);
            for (int c = 0; c < 3; c++) sd[c] = Math.Max(Math.Sqrt(sd[c] / core.Count), 10);
            double m0 = mean[0], m1 = mean[1], m2 = mean[2], s0 = sd[0], s1 = sd[1], s2 = sd[2];
            bool Like(int p) => Math.Abs(rgb[p * 3] - m0) / s0 < z && Math.Abs(rgb[p * 3 + 1] - m1) / s1 < z && Math.Abs(rgb[p * 3 + 2] - m2) / s2 < z;
            // the area to redraw: the found region grown by reach (Chebyshev), over seen texels
            int x0 = w, y0 = h, x1 = 0, y1 = 0;
            foreach (int p in px) { x0 = Math.Min(x0, p % w); x1 = Math.Max(x1, p % w); y0 = Math.Min(y0, p / w); y1 = Math.Max(y1, p / w); }
            // (a dark edge band bounded by trim, below, may run twice as far)
            int far = reach * 2;
            x0 = Math.Max(0, x0 - far); y0 = Math.Max(0, y0 - far); x1 = Math.Min(w - 1, x1 + far); y1 = Math.Min(h - 1, y1 + far);
            int bw = x1 - x0 + 1, bh = y1 - y0 + 1;
            var near = new bool[bw * bh];
            var nearDark = new bool[bw * bh];
            foreach (int p in px)
                for (int dy = -far; dy <= far; dy++)
                    for (int dx = -far; dx <= far; dx++)
                    {
                        int xx = p % w + dx - x0, yy = p / w + dy - y0;
                        if (xx < 0 || yy < 0 || xx >= bw || yy >= bh) continue;
                        nearDark[yy * bw + xx] = true;
                        if (Math.Abs(dx) <= reach && Math.Abs(dy) <= reach) near[yy * bw + xx] = true;
                    }
            // grow from the core over like texels in the area
            var glass = new bool[bw * bh];
            var st = new Stack<int>();
            foreach (int p in core) { int q = (p / w - y0) * bw + (p % w - x0); if (Like(p) && !glass[q]) { glass[q] = true; st.Push(q); } }
            while (st.Count > 0)
            {
                int q = st.Pop(); int qx = q % bw, qy = q / bw;
                foreach (var (dx, dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                {
                    int nx = qx + dx, ny = qy + dy;
                    if (nx < 0 || ny < 0 || nx >= bw || ny >= bh) continue;
                    int nq = ny * bw + nx, p = (ny + y0) * w + nx + x0;
                    if (glass[nq] || !near[nq] || ids[p] == 0 || !Like(p)) continue;
                    glass[nq] = true; st.Push(nq);
                }
            }
            // the pane's darkest edge (a gradient run down to near black) up to the trim: a dark patch the glass fades into joins it when what
            // bounds it on the other side is mostly light or grey trim (chrome, a white rubber seal), not coloured paint (the bus's black band, a black roof)
            bool Dark(int q) { int p = (q / bw + y0) * w + q % bw + x0; return nearDark[q] && !glass[q] && ids[p] != 0 && CarMaterialTable.InClass("dark", rgb[p * 3], rgb[p * 3 + 1], rgb[p * 3 + 2]); }
            bool Trim(int p) => ids[p] == 5 || CarMaterialTable.InClass("light", rgb[p * 3], rgb[p * 3 + 1], rgb[p * 3 + 2]) || CarMaterialTable.InClass("grey", rgb[p * 3], rgb[p * 3 + 1], rgb[p * 3 + 2]);   // grey: the trim's soft edge
            var darkSeen = new bool[bw * bh];
            for (int q0 = 0; q0 < bw * bh; q0++)
            {
                if (darkSeen[q0] || !Dark(q0)) continue;
                var patch = new List<int>();
                int byGlass = 0, byTrim = 0, byOther = 0;
                darkSeen[q0] = true; st.Push(q0);
                while (st.Count > 0)
                {
                    int q = st.Pop(); patch.Add(q); int qx = q % bw, qy = q / bw;
                    int pq = (qy + y0) * w + qx + x0;
                    foreach (var (dx, dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                    {
                        int nx = qx + dx, ny = qy + dy;
                        if (nx < 0 || ny < 0 || nx >= bw || ny >= bh) continue;
                        int nq = ny * bw + nx, p = (ny + y0) * w + nx + x0;
                        // only where the pane fades into it (the Pony's gradient run down to black); a sharp edge is a frame (the Limo's)
                        if (glass[nq]) { if (Math.Abs(rgb[p * 3] - rgb[pq * 3]) + Math.Abs(rgb[p * 3 + 1] - rgb[pq * 3 + 1]) + Math.Abs(rgb[p * 3 + 2] - rgb[pq * 3 + 2]) < 60) byGlass++; continue; }
                        if (Dark(nq)) { if (!darkSeen[nq]) { darkSeen[nq] = true; st.Push(nq); } continue; }
                        if (ids[p] == 0) continue;
                        if (Trim(p)) byTrim++; else byOther++;
                    }
                }
                if (byGlass > 0 && byTrim > 0 && byTrim >= byOther) foreach (int q in patch) glass[q] = true;
            }
            // its holes: what the outside cannot reach without crossing glass
            var outside = new bool[bw * bh];
            for (int q = 0; q < bw * bh; q++) { int qx = q % bw, qy = q / bw; if ((qx == 0 || qy == 0 || qx == bw - 1 || qy == bh - 1) && !glass[q]) { outside[q] = true; st.Push(q); } }
            while (st.Count > 0)
            {
                int q = st.Pop(); int qx = q % bw, qy = q / bw;
                foreach (var (dx, dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                {
                    int nx = qx + dx, ny = qy + dy;
                    if (nx < 0 || ny < 0 || nx >= bw || ny >= bh) continue;
                    int nq = ny * bw + nx;
                    if (outside[nq] || glass[nq]) continue;
                    outside[nq] = true; st.Push(nq);
                }
            }
            for (int q = 0; q < bw * bh; q++)
            {
                int p = (q / bw + y0) * w + q % bw + x0;
                if (ids[p] == 0) continue;
                bool g = glass[q] || !outside[q];
                if (g) o[p] = Glass;
                else if (inC.Contains(p)) o[p] = 255;   // no longer glass: settled below
            }
        }
        // the texels a window gave up: the class most common among their 8 neighbours (not glass), else paint
        for (int p = 0; p < o.Length; p++)
        {
            if (o[p] != 255) continue;
            int x = p % w, y = p / w;
            Span<int> votes = stackalloc int[32];
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int xx = x + dx, yy = y + dy;
                    if (xx < 0 || yy < 0 || xx >= w || yy >= h) continue;
                    byte n = o[yy * w + xx];
                    if (n is > 0 and < 32 and not Glass) votes[n]++;
                }
            int best = Paint;
            for (int c = 1; c < 32; c++) if (votes[c] > votes[best]) best = c;
            o[p] = (byte)best;
        }
        return o;
    }

    private static double Sq(double v) => v * v;

    private static List<List<int>> Components(byte[] ids, int w, int h, byte cls, bool eight)
    {
        var res = new List<List<int>>();
        var seen = new bool[w * h];
        var st = new Stack<int>();
        for (int s = 0; s < ids.Length; s++)
        {
            if (seen[s] || ids[s] != cls) continue;
            var px = new List<int>();
            seen[s] = true; st.Push(s);
            while (st.Count > 0)
            {
                int p = st.Pop(); px.Add(p);
                int x = p % w, y = p / w;
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if ((dx | dy) == 0 || (!eight && dx != 0 && dy != 0)) continue;
                        int xx = x + dx, yy = y + dy;
                        if (xx < 0 || yy < 0 || xx >= w || yy >= h) continue;
                        int q = yy * w + xx;
                        if (!seen[q] && ids[q] == cls) { seen[q] = true; st.Push(q); }
                    }
            }
            res.Add(px);
        }
        return res;
    }

    /// <summary>Fills glass gaps of up to two texels between glass texels (a close, only over paint or unlabelled texels).</summary>
    private static void CloseGlass(byte[] o, int w, int h)
    {
        for (int round = 0; round < 2; round++)
        {
            var grown = (byte[])o.Clone();
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    if (o[y * w + x] > Paint) continue;
                    int n = 0;
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int xx = x + dx, yy = y + dy;
                            if ((dx != 0 || dy != 0) && xx >= 0 && yy >= 0 && xx < w && yy < h && o[yy * w + xx] == Glass) n++;
                        }
                    if (n >= 3) grown[y * w + x] = Glass;
                }
            Array.Copy(grown, o, o.Length);
        }
    }
}
