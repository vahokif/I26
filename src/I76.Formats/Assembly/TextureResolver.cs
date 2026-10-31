using System.Text.RegularExpressions;
using I76.Formats.Imaging;
using I76.Formats.Vfs;

namespace I76.Formats.Assembly;

/// <summary>
/// Texture names to texture data.
///
/// A name is looked up by stem, whatever extension it was written with: WDEF
/// and faces say ".map" where the 16-bit data holds ".M16". The stem is
/// tried as .m16, .map, then .vqm (16-bit first, since we play from miss16).
///
/// A vehicle face names a slot, not a texture: "V1 FT TP.MAP" is detail 1,
/// front section, top face. Following the reference assembler, it resolves to the
/// .TMT "&lt;paint&gt;&lt;obj&gt;&lt;d&gt;&lt;XX&gt;&lt;YY&gt;.TMT" (LF→LT, RG→RT), whose first
/// resolvable entry is the undamaged texture. Here the .TMT is looked for
/// first among the vehicle's own .vtf list, which carries the paint.
/// </summary>
public sealed class TextureResolver(AssetVfs vfs)
{
    private static readonly Regex Slot = new(@"^V(.)\s*(..)\s*(..)\.(MAP|M16|TMT)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Dictionary<string, string> FaceAlias = new(StringComparer.OrdinalIgnoreCase) { ["LF"] = "LT", ["RG"] = "RT" };
    public static readonly string[] Extensions = [".m16", ".map", ".vqm"];

    private readonly Dictionary<string, Codebook?> _cbk = new(StringComparer.OrdinalIgnoreCase);

    public static string Stem(string name)
    {
        int i = name.LastIndexOf('.');
        return i < 0 ? name : name[..i];
    }

    /// <summary>The member name that holds this texture, or null.</summary>
    public string? FindByStem(string name)
    {
        var stem = Stem(name);
        foreach (var ext in Extensions)
            if (vfs.Exists(stem + ext)) return stem + ext;
        return null;
    }

    /// <summary>
    /// A face whose texture is an animated .tmt (header +0x2c bit 0: the rotor
    /// blades' BLADE101..103): every frame's member and the rate; null for anything else.
    /// </summary>
    public (string[] Frames, float Fps)? AnimatedFace(string faceTexture)
    {
        if (!faceTexture.EndsWith(".tmt", StringComparison.OrdinalIgnoreCase) || vfs.TryRead(faceTexture) is not { } d) return null;
        TmtMaterial tmt;
        try { tmt = new TmtMaterial(d); } catch (InvalidDataException) { return null; }
        if (!tmt.Animated || tmt.Fps <= 0) return null;
        var frames = tmt.Names.Where(n => n.Length > 0).Select(FindByStem).OfType<string>().ToArray();
        return frames.Length > 1 ? (frames, tmt.Fps) : null;
    }

    /// <summary>
    /// A face that names a .tmt directly (the wheels' WA11LS01.TMT, the tank track's WE11LS01.TMT): every name's
    /// member in the file's order (for a wheel strip, row × 12 + frame); null for anything else or if one is missing.
    /// </summary>
    public string[]? TmtFrames(string faceTexture)
    {
        if (!faceTexture.EndsWith(".tmt", StringComparison.OrdinalIgnoreCase) || vfs.TryRead(faceTexture) is not { } d) return null;
        TmtMaterial tmt;
        try { tmt = new TmtMaterial(d); } catch (InvalidDataException) { return null; }
        var o = tmt.Names.Select(n => n.Length > 0 ? FindByStem(n) : null).ToArray();
        return o.All(m => m is not null) ? o! : null;
    }

    /// <summary>
    /// Resolve a face's texture field (slot or direct name) to a texture member name. A slot shows entry
    /// <paramref name="frame"/> of its .TMT (a damage texture, DamageTextures), or the first that resolves if that one doesn't.
    /// </summary>
    public string? ResolveFace(string faceTexture, string? objectCode, IReadOnlyList<string> vtfTmts, char paint = '1', IReadOnlyList<string>? vtfMaps = null, int frame = 0)
    {
        var m = Slot.Match(faceTexture);
        if (!m.Success)
        {
            // a face can name its .TMT directly (the wheels' WA11LS01.TMT): its first resolvable texture
            if (faceTexture.EndsWith(".tmt", StringComparison.OrdinalIgnoreCase) && vfs.TryRead(faceTexture) is { } direct)
            {
                try
                {
                    foreach (var n in new TmtMaterial(direct).Names)
                        if (n.Length > 0 && FindByStem(n) is { } hit) return hit;
                }
                catch (InvalidDataException) { }
            }
            return FindByStem(faceTexture);
        }
        string d = m.Groups[1].Value;
        string xx = FaceAlias.GetValueOrDefault(m.Groups[2].Value, m.Groups[2].Value.ToUpperInvariant());
        string yy = FaceAlias.GetValueOrDefault(m.Groups[3].Value, m.Groups[3].Value.ToUpperInvariant());
        // "V<d> BO DY" is the body: the .vtf names it directly as a .map
        // ("go11bdy1.map") rather than through a .TMT. Matching it by the
        // "bdy" in the name is ours, not read from the engine.
        if (xx == "BO" && yy == "DY" && vtfMaps is not null)
        {
            var body = vtfMaps.Where(n => n.Contains("bdy", StringComparison.OrdinalIgnoreCase)).ToList();
            var pick = body.FirstOrDefault(n => n.Length > 2 && n[2] == d[0]) ?? body.FirstOrDefault();
            return pick is null ? null : FindByStem(pick);
        }
        // The .vtf's own list first, matched on everything after the object
        // code: a few vehicles (the milk truck: meshes MX, tables mt) use a
        // different code for the two.
        string tail = $"{d}{xx}{yy}.TMT";
        string? tmtName = vtfTmts.FirstOrDefault(t => t.Length == tail.Length + 3 && t.EndsWith(tail, StringComparison.OrdinalIgnoreCase))
                          ?? $"{paint}{objectCode}{tail}";
        if (vfs.TryRead(tmtName) is not { } blob) return null;
        TmtMaterial tmt;
        try { tmt = new TmtMaterial(blob); } catch (InvalidDataException) { return null; }
        if (frame > 0 && frame < tmt.Names.Length && tmt.Names[frame].Length > 0 && FindByStem(tmt.Names[frame]) is { } damaged) return damaged;
        foreach (var n in tmt.Names)
            if (n.Length > 0 && FindByStem(n) is { } hit) return hit;
        return null;
    }

    /// <summary>Decode a texture member to indices (+ its own palette for .m16).</summary>
    public IndexedTexture? Load(string member)
    {
        var d = vfs.TryRead(member);
        if (d is null) return null;
        try
        {
            switch (Bwd2.Bwd2File.Ext(member))
            {
                case "m16": return TextureReader.ParseM16(d);
                case "map": return TextureReader.ParseMap(d);
                case "vqm":
                    var v = new VqmImage(d);
                    Codebook? cb;
                    lock (_cbk)
                    {
                        if (!_cbk.TryGetValue(v.CodebookName, out cb))
                            _cbk[v.CodebookName] = cb = vfs.TryRead(v.CodebookName) is { } c ? new Codebook(c) : null;
                    }
                    return cb is null ? null : v.Decode(cb);
                default: return null;
            }
        }
        catch (InvalidDataException) { return null; }
    }
}
