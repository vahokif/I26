namespace I76.Formats.Cockpit;

/// <summary>A named rectangle (src sheets) or anchor point (dst textures, W = H = 0) in a cockpit layout.</summary>
public readonly record struct EltLabel(string Name, int X, int Y, int W, int H);

/// <summary>One <c>src</c> or <c>dst</c> block: a texture and its labels.</summary>
public sealed record EltBlock(bool IsSource, string Texture, IReadOnlyList<EltLabel> Labels);

/// <summary>
/// The cockpit layout (.elt, VCF +0x40; vpit_1.elt for every car): plain
/// text, <c>src &lt;tex&gt;</c> blocks of sprite rectangles and <c>dst
/// &lt;tex&gt;</c> blocks of anchor points, each <c>label name x y w h</c>.
/// Names with a <c>3</c> prefix and textures with a <c>320</c> suffix are the
/// low-resolution set. Lookup is by label name, case-insensitive.
/// </summary>
public sealed class EltLayout
{
    public IReadOnlyList<EltBlock> Blocks { get; }
    private readonly Dictionary<string, (EltBlock Block, EltLabel Label)> _byName = new(StringComparer.OrdinalIgnoreCase);

    private EltLayout(List<EltBlock> blocks)
    {
        Blocks = blocks;
        foreach (var b in blocks)
            foreach (var l in b.Labels)
                _byName.TryAdd(l.Name, (b, l));
    }

    public static EltLayout Parse(string text)
    {
        var blocks = new List<EltBlock>();
        string? tex = null; bool src = false; var labels = new List<EltLabel>();
        void Flush() { if (tex is not null) blocks.Add(new EltBlock(src, tex, labels)); labels = []; }
        foreach (var raw in text.Split('\n'))
        {
            var t = raw.Split((char[])[' ', '\t', '\r'], StringSplitOptions.RemoveEmptyEntries);
            if (t.Length == 0) continue;
            switch (t[0].ToLowerInvariant())
            {
                case "src" or "dst" when t.Length >= 2:
                    Flush();
                    src = t[0].Equals("src", StringComparison.OrdinalIgnoreCase);
                    tex = t[1];
                    break;
                case "label" when t.Length >= 6 && tex is not null:
                    labels.Add(new EltLabel(t[1], int.Parse(t[2]), int.Parse(t[3]), int.Parse(t[4]), int.Parse(t[5])));
                    break;
            }
        }
        Flush();
        return new EltLayout(blocks);
    }

    /// <summary>A label inside the block for texture <paramref name="tex"/> (dst anchors repeat across blocks).</summary>
    public EltLabel? FindIn(string tex, string name)
    {
        foreach (var b in Blocks)
            if (b.Texture.Equals(tex, StringComparison.OrdinalIgnoreCase))
                foreach (var l in b.Labels)
                    if (l.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return l;
        return null;
    }

    /// <summary>A label and the texture it belongs to, or null.</summary>
    public (string Texture, EltLabel Label)? Find(string name) =>
        _byName.TryGetValue(name, out var e) ? (e.Block.Texture, e.Label) : null;
}
