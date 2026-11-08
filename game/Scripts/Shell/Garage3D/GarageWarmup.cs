using Godot;
using I76.Sim.Trip;

namespace I76.Game.Shell.Garage3D;

/// <summary>
/// The 3D garage's first visit made ahead, on the main menu: its first build made the room's textures, the car's meshes and the
/// renderer's pipelines all at once, a second or two the second visit did not take (#143). Here, once the menu has stood for a
/// moment, a step a frame: the room's pixels (on a worker thread), the garage's assets, the melee form's car, then the whole
/// stage drawn for a few frames under the menu's opaque frame and taken away. Leaving the main menu stops it; what was made
/// stays for the garage.
/// </summary>
public static class GarageWarmup
{
    /// <summary>Frames the main menu stands before the warm-up begins (the menu's own first frames stay quick).</summary>
    public const int Delay = 45;
    /// <summary>Frames the stage is drawn: the pipelines are made on the first, the shadows' and the screen effects' on the next.</summary>
    public const int DrawFrames = 3;

    private static AssetsKey? _done;
    private sealed record AssetsKey(object Vfs, bool Faithful);

    public static bool Wanted(Shell shell) =>
        shell.Hosted is null && shell.Settings.Garage3D && System.Environment.GetEnvironmentVariable("I76_GARAGE_2D") != "1"
        && System.Environment.GetEnvironmentVariable("I76_NO_WARMUP") != "1";

    /// <summary>Starts the warm-up for this shell's main menu, once per install and look.</summary>
    public static async void Start(Shell shell, ShellScreen menu)
    {
        if (!Wanted(shell)) return;
        var key = new AssetsKey(shell.Vfs, shell.Faithful);
        if (_done == key) return;
        _done = key;
        GarageRoom.Prepare();
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        GarageStage? stage = null;
        try
        {
            var tree = shell.GetTree();
            bool Still() => GodotObject.IsInstanceValid(shell) && shell.Current == menu;
            async System.Threading.Tasks.Task<bool> Frames(int n)
            {
                for (int i = 0; i < n; i++) await shell.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
                return Still();
            }
            if (!await Frames(Delay)) { _done = null; return; }
            var assets = GarageAssets.For(shell);
            var data = shell.Melee;
            if (data.Models.Count == 0) return;
            var setup = MeleeSetup.Load();
            int model = Math.Clamp(setup.Model, 0, data.Models.Count - 1);
            int variant = Math.Clamp(setup.Variant, 1, data.Models[model].Variants.Count);
            var vfs = shell.Vfs;
            var car = TripCar.FromVcf(vfs.Read(data.Vcf(model, variant)), n => I76.Sim.Vehicles.VehicleFactory.EngineCompId(vfs, n));
            // the car's parts (assembly, meshes, textures) into the assets' cache, then the stage with it a frame later
            if (!await Frames(1)) { _done = null; return; }
            var paint = data.Colours(model, variant);
            GarageCar.Build(assets, car, shell.Faithful, paint).Free();
            if (!await Frames(1)) return;
            stage = new GarageStage { Assets = assets, Name = "GarageWarmup", DrawUnseen = true, Modulate = new Color(1, 1, 1, 0) };
            shell.AddLayer(stage, underFrame: true);
            stage.SetLook(shell.Faithful);
            stage.SetCar(car, paint);
            await Frames(DrawFrames);
            GD.Print($"garage warmed up in {(System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency:0} ms (from the main menu's first frame)");
        }
        catch (Exception ex) { GD.PushWarning($"garage warm-up: {ex.Message}"); }
        finally
        {
            if (stage is not null && GodotObject.IsInstanceValid(stage)) stage.QueueFree();
        }
    }
}
