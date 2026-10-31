namespace I76.Formats.Imaging;

/// <summary>
/// Back-projects per-view segmentation labels onto the car textures (Texture++ §3.3). Pass one (<see cref="Seen"/>) finds each texel's best
/// quality over all views — the global quality map; pass two (<see cref="Vote"/>) lets every view whose quality at the texel is within
/// <see cref="VoteBand"/> of the best vote its label, weighted by that quality. <see cref="Resolve"/> takes each texel's winner,
/// fills the small holes the sampling leaves, and removes speckles smaller than <see cref="MinRegion"/> texels.
/// </summary>
public sealed class CarMaskProjector
{
    public const float VoteBand = 0.9f;
    public const int MinRegion = 4;
    public const float UndersideUp = -0.5f;
    /// <summary>Off: the underside textures are chassis steel by name (CarSamCommand); the prior was noisy on the rest.</summary>
    public bool UndersidePrior { get; init; }
    public const byte UndersideClass = 7;   // chassis-steel

    private readonly (int W, int H)[] _sizes;
    private readonly float[][] _best;
    private readonly float[][] _votes;
    private readonly float[][] _bestUp;
    private readonly int _classes;

    /// <param name="classes">label values 0 (no label: unlabelled pixels vote for the default) .. classes-1.</param>
    public CarMaskProjector(IReadOnlyList<(int W, int H)> textureSizes, int classes)
    {
        _sizes = [.. textureSizes]; _classes = classes;
        _best = [.. _sizes.Select(s => new float[s.W * s.H])];
        _votes = [.. _sizes.Select(s => new float[s.W * s.H * classes])];
        _bestUp = [.. _sizes.Select(s => new float[s.W * s.H])];
    }

    /// <summary>Pass one: raises the best quality of every texel the view shows (<paramref name="cameraUp"/>: the camera's height over the car, -1 looking up at the underside .. 1 from above).</summary>
    public void Seen(CarView v, float cameraUp = 0)
    {
        for (int i = 0; i < v.Tex.Length; i++)
        {
            int t = v.Tex[i];
            if (t < 0 || v.Q[i] <= 0) continue;
            int ti = v.Ty[i] * _sizes[t].W + v.Tx[i];
            if (v.Q[i] > _best[t][ti]) { _best[t][ti] = v.Q[i]; _bestUp[t][ti] = cameraUp; }
        }
    }

    /// <summary>Pass two: the view's per-pixel labels (0..classes-1) vote.</summary>
    public void Vote(CarView v, byte[] labels)
    {
        for (int i = 0; i < v.Tex.Length; i++)
        {
            int t = v.Tex[i];
            if (t < 0 || v.Q[i] <= 0) continue;
            int ti = v.Ty[i] * _sizes[t].W + v.Tx[i];
            if (v.Q[i] < VoteBand * _best[t][ti]) continue;
            _votes[t][ti * _classes + Math.Min((int)labels[i], _classes - 1)] += v.Q[i];
        }
    }

    /// <summary>Texels (of texture t) that some view showed.</summary>
    public bool[] SeenMask(int t) => _best[t].Select(q => q > 0).ToArray();

    /// <summary>The label of every texel of texture t (255: no view saw it, after hole filling).</summary>
    public byte[] Resolve(int t, bool clean = true)
    {
        var (w, h) = _sizes[t];
        var lab = new byte[w * h];
        for (int i = 0; i < lab.Length; i++)
        {
            lab[i] = 255;
            float top = 0;
            for (int c = 0; c < _classes; c++)
            {
                float s = _votes[t][i * _classes + c];
                if (s > top) { top = s; lab[i] = (byte)c; }
            }
        }
        // geometry prior: paint the segmenter left as the default, on a surface its squarest view saw from below, is the underside: chassis steel
        // (the dark underside is the segmenter's weakest case; it finds no "car" there)
        for (int i = 0; i < lab.Length; i++)
            if (UndersidePrior && (lab[i] == 0 || lab[i] == 1) && _best[t][i] > 0 && _bestUp[t][i] < UndersideUp) lab[i] = UndersideClass;
        if (!clean) return lab;
        // holes: unseen texels take the majority of their seen neighbours, up to two rounds (the sampling of a coarse view leaves gaps, not regions)
        for (int round = 0; round < 2; round++)
        {
            var next = (byte[])lab.Clone();
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    if (lab[y * w + x] != 255) continue;
                    int best = Majority(lab, w, h, x, y, 255);
                    if (best >= 0) next[y * w + x] = (byte)best;
                }
            lab = next;
        }
        RemoveSpeckles(lab, w, h);
        return lab;
    }

    private static int Majority(byte[] lab, int w, int h, int x, int y, int skip)
    {
        Span<int> cnt = stackalloc int[256];
        int top = -1, topN = 0;
        for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                int xx = x + dx, yy = y + dy;
                if ((dx == 0 && dy == 0) || xx < 0 || yy < 0 || xx >= w || yy >= h) continue;
                byte l = lab[yy * w + xx];
                if (l == skip) continue;
                if (++cnt[l] > topN) { topN = cnt[l]; top = l; }
            }
        return top;
    }

    /// <summary>Connected regions (4-neighbours, one label) smaller than MinRegion texels take the label of their neighbours' majority.</summary>
    private static void RemoveSpeckles(byte[] lab, int w, int h)
    {
        var seen = new bool[w * h];
        var stack = new Stack<int>();
        for (int s = 0; s < lab.Length; s++)
        {
            if (seen[s] || lab[s] == 255) continue;
            var region = new List<int>();
            stack.Push(s); seen[s] = true;
            while (stack.Count > 0)
            {
                int p = stack.Pop(); region.Add(p);
                int x = p % w, y = p / w;
                void Try(int xx, int yy) { if (xx < 0 || yy < 0 || xx >= w || yy >= h) return; int q = yy * w + xx; if (!seen[q] && lab[q] == lab[s]) { seen[q] = true; stack.Push(q); } }
                Try(x - 1, y); Try(x + 1, y); Try(x, y - 1); Try(x, y + 1);
            }
            if (region.Count >= MinRegion) continue;
            var around = new int[256];
            foreach (var p in region)
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int xx = p % w + dx, yy = p / w + dy;
                        if (xx < 0 || yy < 0 || xx >= w || yy >= h) continue;
                        byte l = lab[yy * w + xx];
                        if (l != lab[s] && l != 255) around[l]++;
                    }
            int best = Array.IndexOf(around, around.Max());
            if (around[best] > 0) foreach (var p in region) lab[p] = (byte)best;
        }
    }
}
