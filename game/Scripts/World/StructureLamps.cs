using Godot;
using I76.Formats.Assembly;
using I76.Game.Assets;
using I76.Game.Sim;

namespace I76.Game.World;

/// <summary>
/// The modern look's night lights of a structure: a spot light at each of its lamps (SelfLitLamps: its self-lit faces that look
/// down, a canopy's lit underside, a street lamp's lens), pointing the way the face looks, in the face's own colour. The original
/// has no such light (only the cars' lamps light anything at night); it draws those faces and what lies under them (the forecourt,
/// the pumps) at full brightness, and the modern look's self-lit faces glow (object.gdshaderinc: self_lit) while these lights
/// light the ground and the cars under them, as the gas station's cut-scene shows its forecourt. Rendering only. Built for night
/// missions alone; out of the camera's reach the lights fade and go, their shadows sooner.
/// </summary>
public static class StructureLamps
{
    /// <summary>A lamp's face is lit enough to be one when its colour's luminance (sRGB) is above this (a black face is no lamp).</summary>
    public const float MinLuma = 0.35f;

    /// <summary>The light under a lamp: the ground straight below it takes about this much light whatever its height (energy grows as the height squared).</summary>
    public const float Irradiance = 0.2f;

    /// <summary>A lamp's spot: its cone's half-angle (degrees) and how far it reaches for its height.</summary>
    public const float Cone = 72f, ReachPerHeight = 4f, MinReach = 6f, MaxReach = 30f;

    /// <summary>The lights fade out from this far from the camera (m) over <see cref="FadeLength"/>; their shadows from <see cref="ShadowFade"/>.</summary>
    public const float FadeBegin = 180f, FadeLength = 40f, ShadowFade = 60f;

    /// <summary>
    /// The lamps of a structure's model, as a node of lights in the model's frame (Godot axes; the caller places it), or null if it has
    /// none. <paramref name="include"/>: the parts drawn with the structure's mesh; <paramref name="shadows"/>: the lights cast shadows.
    /// </summary>
    public static Node3D? Build(AssembledModel model, MeshBuilder meshes, Func<ModelPart, bool>? include = null, bool shadows = true)
    {
        bool Lit(ModelPart part, I76.Formats.Geo.GeoFace f) => meshes.FaceColour(model, f) is { } c && Luma(c) > MinLuma;
        var lamps = SelfLitLamps.Of(model, include, Lit);
        if (lamps.Count == 0) return null;
        float floor = model.Parts.Where(p => include?.Invoke(p) != false).SelectMany(p => p.Mesh.Vertices.Select(v => p.ToModel.Apply(v).Y)).DefaultIfEmpty(0f).Min();
        var root = new Node3D { Name = "Lamps" };
        foreach (var l in lamps)
        {
            var colour = meshes.FaceColour(model, l.Face) ?? Colors.White;
            float height = MathF.Max(1.5f, l.At.Y - floor);
            var dir = Coords.ToGodot(l.Facing).Normalized();
            var spot = new SpotLight3D
            {
                Name = "Lamp",
                Position = Coords.ToGodot(l.At) + dir * 0.05f,
                SpotRange = Math.Clamp(height * ReachPerHeight, MinReach, MaxReach),
                SpotAngle = Cone,
                SpotAngleAttenuation = 0.6f,
                SpotAttenuation = 1f,
                LightEnergy = Irradiance * height * height,
                LightColor = LampColour(colour),
                LightSpecular = 0.4f,
                ShadowEnabled = shadows,
                ShadowBias = 0.05f,
                ShadowNormalBias = 1.5f,
                DistanceFadeEnabled = true,
                DistanceFadeBegin = FadeBegin,
                DistanceFadeLength = FadeLength,
                DistanceFadeShadow = ShadowFade,
            };
            // a spot shines along its -Z
            spot.Basis = Basis.LookingAt(dir, Mathf.Abs(dir.Y) > 0.99f ? Vector3.Forward : Vector3.Up);
            root.AddChild(spot);
        }
        return root;
    }

    private static float Luma(Color c) => 0.2126f * c.R + 0.7152f * c.G + 0.0722f * c.B;

    /// <summary>A lamp's light: the face's hue at full value, half-way to white (a white panel lights white, a yellow lamp warm).</summary>
    public static Color LampColour(Color face)
    {
        float m = MathF.Max(face.R, MathF.Max(face.G, face.B));
        if (m <= 0) return Colors.White;
        var hue = new Color(face.R / m, face.G / m, face.B / m);
        return Colors.White.Lerp(hue, 0.5f);
    }
}
