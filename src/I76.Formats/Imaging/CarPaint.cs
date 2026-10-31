using System.Text.Json;
using System.Text.Json.Nodes;

namespace I76.Formats.Imaging;

/// <summary>How a respray's paint reads: gloss is the car's own clear coat, metallic a steeper shade curve under a flake, matte no coat.</summary>
public enum PaintFinish : byte { Gloss = 0, Metallic = 1, Matte = 2 }

/// <summary>One colour of a respray, sRGB bytes.</summary>
public readonly record struct PaintColour(byte R, byte G, byte B)
{
    public string Hex => $"#{R:x2}{G:x2}{B:x2}";

    public static PaintColour? ParseHex(string? s)
    {
        if (s is null) return null;
        s = s.Trim().TrimStart('#');
        if (s.Length != 6 || !int.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out int v)) return null;
        return new PaintColour((byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }
}

/// <summary>
/// A car's custom paint (I26's respray): a colour for the scheme's primary paint, one for its secondary paint
/// where it has one, and the finish. Not part of the .vcf, which stays as the original wrote it: it lives in a sidecar beside the variant
/// (<see cref="CarSidecar"/>) and travels with a network game's car as <see cref="ToBytes"/>. Null or <see cref="IsNone"/> is the factory paint.
/// </summary>
public sealed record CarPaint(PaintColour? Primary, PaintColour? Secondary = null, PaintFinish Finish = PaintFinish.Gloss)
{
    public bool IsNone => Primary is null && Secondary is null;

    /// <summary>The wire/save form: empty for the factory paint, else format 1, which colours (bit 0 primary, bit 1 secondary), the finish, then the colours.</summary>
    public byte[] ToBytes()
    {
        if (IsNone) return [];
        var o = new List<byte> { 1, (byte)((Primary is null ? 0 : 1) | (Secondary is null ? 0 : 2)), (byte)Finish };
        foreach (var c in new[] { Primary, Secondary }) if (c is { } k) { o.Add(k.R); o.Add(k.G); o.Add(k.B); }
        return [.. o];
    }

    /// <summary>The paint of <see cref="ToBytes"/>; null for the factory paint or bytes that are none (a newer or damaged one is the factory paint).</summary>
    public static CarPaint? FromBytes(ReadOnlySpan<byte> span)
    {
        var b = span.ToArray();
        if (b.Length < 3 || b[0] != 1) return null;
        int flags = b[1] & 3, n = (flags & 1) + (flags >> 1);
        if (flags == 0 || b[2] > (byte)PaintFinish.Matte || b.Length != 3 + 3 * n) return null;
        int p = 3;
        PaintColour? Next() { var c = new PaintColour(b[p], b[p + 1], b[p + 2]); p += 3; return c; }
        var prim = (flags & 1) != 0 ? Next() : null;
        var sec = (flags & 2) != 0 ? Next() : null;
        return new CarPaint(prim, sec, (PaintFinish)b[2]);
    }

    public JsonObject ToJson() => new()
    {
        ["primary"] = Primary?.Hex,
        ["secondary"] = Secondary?.Hex,
        ["finish"] = Finish.ToString().ToLowerInvariant(),
    };

    public static CarPaint? FromJson(JsonNode? n)
    {
        if (n is not JsonObject o) return null;
        PaintColour? Get(string k) => o[k] is JsonValue v && v.TryGetValue<string>(out var s) ? PaintColour.ParseHex(s) : null;
        var p = new CarPaint(Get("primary"), Get("secondary"),
            o["finish"] is JsonValue f && f.TryGetValue<string>(out var fs) && Enum.TryParse<PaintFinish>(fs, true, out var fin) ? fin : PaintFinish.Gloss);
        return p.IsNone ? null : p;
    }
}

/// <summary>
/// The I26 sidecar of a car variant: <c>&lt;variant&gt;.i26</c> beside <c>&lt;variant&gt;.vcf</c>, a small JSON object
/// (<c>{"i26":1,"paint":{...}}</c>; other settings may join it later). The .vcf stays byte-identical and readable by the original;
/// copying a variant to another folder, another machine or a friend takes the pair. A missing, empty or unreadable sidecar is the factory paint.
/// </summary>
public static class CarSidecar
{
    public const string Extension = ".i26";

    /// <summary>The sidecar's file name for a car file (vppirna2.vcf → vppirna2.i26).</summary>
    public static string NameOf(string vcf) => Path.ChangeExtension(vcf, Extension);

    public static byte[] Write(CarPaint? paint)
    {
        var o = new JsonObject { ["i26"] = 1 };
        if (paint is { IsNone: false }) o["paint"] = paint.ToJson();
        return System.Text.Encoding.UTF8.GetBytes(o.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    public static CarPaint? Read(byte[]? data)
    {
        if (data is not { Length: > 0 }) return null;
        try { return CarPaint.FromJson(JsonNode.Parse(data) is JsonObject o ? o["paint"] : null); }
        catch (JsonException) { return null; }
    }

    /// <summary>The paint stored for a car file in a folder, or null.</summary>
    public static CarPaint? ReadFile(string dir, string vcf)
    {
        try
        {
            var path = Path.Combine(dir, NameOf(Path.GetFileName(vcf)));
            return File.Exists(path) ? Read(File.ReadAllBytes(path)) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Stores (or, for the factory paint, removes) the sidecar of a car file in a folder.</summary>
    public static void WriteFile(string dir, string vcf, CarPaint? paint)
    {
        var path = Path.Combine(dir, NameOf(Path.GetFileName(vcf)));
        try
        {
            if (paint is null or { IsNone: true }) { if (File.Exists(path)) File.Delete(path); return; }
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(path, Write(paint));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
