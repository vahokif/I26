using System.Buffers.Binary;
using System.Text;

namespace I76.Formats.Mission.Edit;

/// <summary>
/// One chunk of a BWD2 file as an editable tree node: a tag and a payload. A container (WDEF, TDEF, RDEF, ODEF, LDEF,
/// ADEF) whose payload is a run of well-formed chunks is held as <see cref="Children"/> plus <see cref="Tail"/>, the
/// bytes after the last whole chunk (empty in every stock file); any other chunk is an opaque payload. Writing a tree
/// that was not edited gives back the bytes it was read from, whatever the chunks hold.
/// </summary>
public sealed class BwdNode
{
    public string Tag { get; set; }
    /// <summary>The payload of a leaf chunk (not used while <see cref="Children"/> is set).</summary>
    public byte[] Payload { get; set; }
    /// <summary>The nested chunks of a container, or null for a leaf.</summary>
    public List<BwdNode>? Children { get; set; }
    /// <summary>Bytes after the last child chunk that do not make a chunk; kept verbatim.</summary>
    public byte[] Tail { get; set; } = [];

    public BwdNode(string tag, byte[] payload) { Tag = tag; Payload = payload; }
    public BwdNode(string tag, List<BwdNode> children) { Tag = tag; Payload = []; Children = children; }

    public int PayloadSize => Children is null ? Payload.Length : Children.Sum(c => 8 + c.PayloadSize) + Tail.Length;

    /// <summary>The container tags whose payload is a list of chunks.</summary>
    public static readonly string[] Containers = ["WDEF", "TDEF", "RDEF", "ODEF", "LDEF", "ADEF"];

    public BwdNode Child(string tag) => Children!.First(c => c.Tag == tag);

    internal void WriteTo(Stream s)
    {
        Span<byte> h = stackalloc byte[8];
        Encoding.Latin1.GetBytes(Tag, h[..4]);
        BinaryPrimitives.WriteUInt32LittleEndian(h[4..], (uint)(8 + PayloadSize));
        s.Write(h);
        if (Children is null) s.Write(Payload);
        else { foreach (var c in Children) c.WriteTo(s); s.Write(Tail); }
    }

    internal static List<BwdNode> ParseRun(byte[] d, int start, int end, bool nest, out byte[] tail)
    {
        var o = new List<BwdNode>();
        int p = start;
        while (p + 8 <= end)
        {
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(p + 4));
            if (size < 8 || p + (long)size > end) break;
            string tag = Encoding.Latin1.GetString(d, p, 4);
            if (nest && Array.IndexOf(Containers, tag) >= 0)
            {
                var kids = ParseRun(d, p + 8, p + (int)size, false, out var kt);
                var n = new BwdNode(tag, kids) { Tail = kt };
                o.Add(n);
            }
            else o.Add(new BwdNode(tag, d.AsSpan(p + 8, (int)size - 8).ToArray()));
            p += (int)size;
        }
        tail = d.AsSpan(p, end - p).ToArray();
        return o;
    }
}

/// <summary>A whole BWD2 file as a list of top-level chunks (BWD2, REV, WDEF ... EXIT) and any trailing bytes.</summary>
public sealed class BwdTree
{
    public List<BwdNode> Chunks { get; }
    public byte[] Tail { get; }

    public BwdTree(List<BwdNode> chunks, byte[]? tail = null) { Chunks = chunks; Tail = tail ?? []; }

    public static BwdTree Parse(byte[] data)
    {
        if (data.Length < 8 || Encoding.Latin1.GetString(data, 0, 4) != "BWD2") throw new InvalidDataException("not a BWD2 file");
        var chunks = BwdNode.ParseRun(data, 0, data.Length, true, out var tail);
        return new BwdTree(chunks, tail);
    }

    public byte[] ToBytes()
    {
        using var ms = new MemoryStream();
        foreach (var c in Chunks) c.WriteTo(ms);
        ms.Write(Tail);
        return ms.ToArray();
    }

    /// <summary>The first top-level chunk with this tag, or null.</summary>
    public BwdNode? First(string tag) => Chunks.FirstOrDefault(c => c.Tag == tag);

    /// <summary>Every nested chunk with this tag in every top-level container with <paramref name="container"/>, in file order.</summary>
    public List<(BwdNode Parent, BwdNode Node)> Subs(string container, string tag)
    {
        var o = new List<(BwdNode, BwdNode)>();
        foreach (var c in Chunks.Where(c => c.Tag == container && c.Children is not null))
            foreach (var k in c.Children!.Where(k => k.Tag == tag)) o.Add((c, k));
        return o;
    }

    /// <summary>
    /// Replace the chunks tagged <paramref name="tag"/> in the <paramref name="container"/> chunks with
    /// <paramref name="nodes"/>, slot for slot: the i-th replaces the i-th existing one in its place; surplus nodes go
    /// after the last existing one (or before the container's first EXIT when there is none); surplus existing ones are
    /// removed. With <paramref name="only"/>, chunks it rejects are not slots and stay as they are. Everything else in the container (revision, EXIT, unknown chunks) keeps its place.
    /// </summary>
    public void ReplaceSubs(string container, string tag, IReadOnlyList<BwdNode> nodes, Func<BwdNode, bool>? only = null)
    {
        var slots = Subs(container, tag).Where(s => only is null || only(s.Node)).ToList();
        int i = 0;
        foreach (var (parent, old) in slots)
        {
            int at = parent.Children!.IndexOf(old);
            if (i < nodes.Count) parent.Children[at] = nodes[i++];
            else parent.Children.RemoveAt(at);
        }
        if (i >= nodes.Count) return;
        BwdNode host; int ins;
        if (slots.Count > 0)
        {
            host = slots[^1].Parent;
            ins = host.Children!.FindLastIndex(c => c.Tag == tag) + 1;
        }
        else
        {
            host = Chunks.First(c => c.Tag == container && c.Children is not null);
            ins = ExitAt(host);
        }
        host.Children!.InsertRange(ins, nodes.Skip(i));
    }

    private static int ExitAt(BwdNode host)
    {
        int e = host.Children!.FindIndex(c => c.Tag == "EXIT");
        return e < 0 ? host.Children.Count : e;
    }
}
