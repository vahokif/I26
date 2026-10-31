using I76.Formats.Assembly;
using I76.Formats.Bwd2;
using I76.Formats.Imaging;
using I76.Formats.Vfs;

namespace I76.Cli;

/// <summary>
/// A car model's paint schemes for the respray research: each .vtf of the model's .vdf, and what texture each
/// of the model's face slots resolves to under it. A texture that differs from scheme to scheme is the paint's (a body panel
/// painted in); one that is the same under all of them is the car's own (lights, numbers, the cockpit).
/// </summary>
public sealed class ModelSchemes
{
    public required string Stem { get; init; }
    public required string Vcf { get; init; }
    public required string Vdf { get; init; }
    public required AssembledModel Model { get; init; }
    public List<string> Vtfs { get; } = [];
    /// <summary>slot (a face's texture field) → parts that carry it, face count, and the mesh modes.</summary>
    public Dictionary<string, (int Faces, HashSet<string> Parts, HashSet<string> Modes)> Slots { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>vtf → slot → texture member.</summary>
    public Dictionary<string, Dictionary<string, string>> Resolved { get; } = new(StringComparer.OrdinalIgnoreCase);

    public static ModelSchemes? Load(AssetVfs vfs, string stem)
    {
        string vcf = stem + "1.vcf";
        if (vfs.TryRead(vcf) is not { } vd) return null;
        var f = new Bwd2File(vd).VcfcFields();
        if (f is null) return null;
        var model = new ModelAssembler(vfs).Assemble(vcf);
        if (model is null) return null;
        var o = new ModelSchemes { Stem = stem, Vcf = vcf, Vdf = f.Value.Vdf, Model = model };
        foreach (var part in model.Parts)
            foreach (var face in part.Mesh.Faces)
                if (face.Texture.Length > 0 && face.Unk_0x20 != 0)
                {
                    if (!o.Slots.TryGetValue(face.Texture, out var s)) o.Slots[face.Texture] = s = (0, [], []);
                    s.Parts.Add(part.NodeName); s.Modes.Add($"{face.Unk_0x20:x2}");
                    o.Slots[face.Texture] = (s.Faces + 1, s.Parts, s.Modes);
                }
        var res = new TextureResolver(vfs);
        foreach (var name in vfs.Names.Where(n => n.EndsWith(".vtf", StringComparison.OrdinalIgnoreCase)).OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            var vtf = new Bwd2File(vfs.Read(name));
            if (!string.Equals(vtf.DisplayName(), f.Value.Vdf, StringComparison.OrdinalIgnoreCase) && !SameStem(name, f.Value.Vtf)) continue;
            var tmts = ModelAssembler.VtfTmtNames(vtf);
            var maps = vtf.References().Where(r => r.EndsWith(".map")).Order(StringComparer.Ordinal).ToList();
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var slot in o.Slots.Keys)
                if (res.ResolveFace(slot, model.ObjectCode, tmts, vtfMaps: maps) is { } m) d[slot] = m;
            o.Vtfs.Add(name); o.Resolved[name] = d;
        }
        return o;
    }

    private static string StemOf(string n) => System.Text.RegularExpressions.Regex.Replace(Path.GetFileNameWithoutExtension(n), @"\d+$", "").ToLowerInvariant();
    private static bool SameStem(string a, string b) => StemOf(a) == StemOf(b);

    /// <summary>Slots whose texture differs between schemes (or, with one scheme, none can be told).</summary>
    public IEnumerable<string> PaintSlots() => Slots.Keys.Where(s => Vtfs.Select(v => Resolved[v].GetValueOrDefault(s)).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1);
}

public static class ResprayImg
{
    /// <summary>A car texture's 8-bit original (.vqm over its codebook), indices into the mission palette; null if it has none.</summary>
    public static IndexedTexture? LoadVqm(AssetVfs vfs, string member)
    {
        string stem = TextureResolver.Stem(member);
        if (vfs.TryRead(stem + ".vqm") is not { } d) return null;
        var v = new VqmImage(d);
        return vfs.TryRead(v.CodebookName) is { } c ? v.Decode(new Codebook(c)) : null;
    }

    /// <summary>A scheme's body textures: those the car's own parts (node names starting with the object code, not the weapons' gizmos) use.</summary>
    public static List<(string Slot, string Member, int Faces, bool Cutout)> BodyTextures(ModelSchemes ms, string vtf)
    {
        var code = ms.Model.ObjectCode ?? "";
        var o = new List<(string, string, int, bool)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (slot, s) in ms.Slots.OrderByDescending(k => k.Value.Faces))
        {
            if (!s.Parts.Any(p => p.StartsWith(code, StringComparison.OrdinalIgnoreCase))) continue;
            if (ms.Resolved[vtf].GetValueOrDefault(slot) is not { } m || !seen.Add(m)) continue;
            o.Add((slot, m, s.Faces, s.Modes.Any(x => (Convert.ToInt32(x, 16) & 4) != 0)));
        }
        return o;
    }

    /// <summary>Textures laid in rows, each drawn at scale, on a grey ground; returns RGB.</summary>
    public static (byte[] Rgb, int W, int H) Sheet(List<(byte[] Rgba, int W, int H)> tex, int scale, int maxW = 1100)
    {
        int x = 0, y = 0, rowH = 0, totW = 0;
        var pos = new List<(int X, int Y)>();
        foreach (var (_, w, h) in tex)
        {
            if (x > 0 && x + w * scale > maxW) { x = 0; y += rowH + 4; rowH = 0; }
            pos.Add((x, y)); x += w * scale + 4; rowH = Math.Max(rowH, h * scale); totW = Math.Max(totW, x);
        }
        int H = y + rowH, W = Math.Max(totW, 1);
        var rgb = new byte[W * H * 3];
        for (int i = 0; i < rgb.Length; i += 3) { rgb[i] = 90; rgb[i + 1] = 90; rgb[i + 2] = 100; }
        for (int t = 0; t < tex.Count; t++)
        {
            var (p, w, h) = tex[t];
            for (int j = 0; j < h * scale; j++)
                for (int i = 0; i < w * scale; i++)
                {
                    int s = ((j / scale) * w + i / scale) * 4, d = ((pos[t].Y + j) * W + pos[t].X + i) * 3;
                    if (p[s + 3] == 0) { rgb[d] = 255; rgb[d + 1] = 0; rgb[d + 2] = 255; } else { rgb[d] = p[s]; rgb[d + 1] = p[s + 1]; rgb[d + 2] = p[s + 2]; }
                }
        }
        return (rgb, W, H);
    }
}
