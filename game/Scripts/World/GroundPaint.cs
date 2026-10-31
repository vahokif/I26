using Godot;
using I76.Formats.Assembly;

namespace I76.Game.World;

/// <summary>
/// A painted ground (GroundMarkings: M15's parking lot) in the modern terrain: terrain_lot.gdshader draws it as
/// asphalt (the Asphalt015 scan, TerrainMaterials) over the texture's own base colours, with the paint from a
/// signed-distance field, all at the texture's 40-unit repeat, so the layout is the 1997 look's. Natural grounds go
/// on as before (GroundNoise, the natural materials).
/// </summary>
public static class GroundPaint
{
    /// <summary>The modern terrain shader's variant for a painted ground (terrain_modern.gdshaderinc with PAINTED_GROUND): natural grounds keep a shader without its code.</summary>
    public const string Shader = "res://Shaders/terrain_lot.gdshader";

    /// <summary>Metres one repeat of the asphalt scan covers (its aggregate a few millimetres to a centimetre).</summary>
    public const float AsphaltTile = 1.0f;

    /// <summary>The ground texture's paint, or null for a natural ground (or with I76_GROUND_PAINT=0).</summary>
    public static GroundMarkings? Find(Image ground)
    {
        if (System.Environment.GetEnvironmentVariable("I76_GROUND_PAINT") == "0") return null;
        var img = (Image)ground.Duplicate();
        if (img.IsCompressed()) img.Decompress();
        img.ClearMipmaps();
        img.Convert(Image.Format.Rgba8);
        return GroundMarkings.Find(img.GetData(), img.GetWidth(), img.GetHeight(), 40f);
    }

    /// <summary>The paint into the shared modern terrain material, and the asphalt scan in place of the natural materials (null: without it).</summary>
    public static void Apply(ShaderMaterial m, GroundMarkings g, TerrainMaterials.Pack? asphalt)
    {
        var baseImg = Image.CreateFromData(g.Width, g.Height, false, Image.Format.Rgba8, g.Base);
        baseImg.GenerateMipmaps();
        var field = Image.CreateFromData(g.Width * g.Scale, g.Height * g.Scale, false, Image.Format.Rg8, g.Field);
        field.GenerateMipmaps();
        m.SetShaderParameter("mark_base", ImageTexture.CreateFromImage(baseImg));
        m.SetShaderParameter("mark_field", ImageTexture.CreateFromImage(field));
        m.SetShaderParameter("mark_range", GroundMarkings.Range);
        m.SetShaderParameter("mark_texels", (float)g.Width);
        var p = g.Paint;
        var pc = new Color(p.R, p.G, p.B).SrgbToLinear();
        m.SetShaderParameter("paint_colour", new Vector3(pc.R, pc.G, pc.B));
        m.SetShaderParameter("use_materials", asphalt is not null);
        if (asphalt is null) return;
        m.SetShaderParameter("mat_tile", new Vector4(AsphaltTile, 3.2f, 1.8f, 9.0f));
        if (int.TryParse(System.Environment.GetEnvironmentVariable("I76_TERRAIN_DEBUG"), out int dv)) m.SetShaderParameter("debug_view", dv);
        m.SetShaderParameter("mat_albedo", asphalt.Albedo);
        m.SetShaderParameter("mat_normal", asphalt.Normal);
        var mean = asphalt.MeanColour[0];
        m.SetShaderParameter("mat_mean", new[] { mean, mean, mean, mean });
        m.SetShaderParameter("mat_lum_mean", new Vector4(asphalt.LumMean[0], 0, 0, 0));
        m.SetShaderParameter("mat_lum_spread", new Vector4(asphalt.LumSpread[0], 1, 1, 1));
    }
}
