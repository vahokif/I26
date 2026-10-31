using System.Text.RegularExpressions;
using Godot;

namespace I76.Game.Boot;

/// <summary>
/// I76_SHADER_SMOKE=1: draws every shader of res://Shaders once on a quad (spatial) or a rectangle (canvas_item), so the
/// renderer compiles them all and its log shows any that it refuses (the Compatibility renderer lacks some of what
/// Forward+ has: docs/compatibility-renderer.md). The sky shaders are left to the missions that use them. The game does not
/// start; it quits by itself after a moment.
/// </summary>
public partial class ShaderSmoke : Node3D
{
    public static bool Requested => System.Environment.GetEnvironmentVariable("I76_SHADER_SMOKE") == "1";

    public override void _Ready()
    {
        var cam = new Camera3D { Current = true };
        AddChild(cam);
        AddChild(new DirectionalLight3D());
        int spatial = 0, canvas = 0, skipped = 0;
        foreach (var file in DirAccess.GetFilesAt("res://Shaders"))
        {
            if (!file.EndsWith(".gdshader")) continue;
            var shader = GD.Load<Shader>("res://Shaders/" + file);
            var m = Regex.Match(shader.Code, @"shader_type\s+(\w+)");
            switch (m.Groups[1].Value)
            {
                case "spatial":
                    AddChild(new MeshInstance3D { Mesh = new QuadMesh { Size = new Vector2(0.2f, 0.2f) }, MaterialOverride = new ShaderMaterial { Shader = shader }, Position = new Vector3((spatial % 8 - 4) * 0.25f, (spatial / 8 - 2) * 0.25f, -2) });
                    spatial++;
                    break;
                case "canvas_item":
                    var layer = new CanvasLayer();
                    layer.AddChild(new ColorRect { Material = new ShaderMaterial { Shader = shader }, Size = new Vector2(64, 64), Position = new Vector2(canvas * 16, canvas * 8) });
                    AddChild(layer);
                    canvas++;
                    break;
                default:
                    skipped++;
                    break;
            }
        }
        GetTree().CreateTimer(1.5).Timeout += () => GetTree().Quit();
        GD.Print($"shader smoke: {spatial} spatial, {canvas} canvas_item, {skipped} left out ({RenderBackend.Method})");
    }
}
