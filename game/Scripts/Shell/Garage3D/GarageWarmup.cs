using Godot;
using I76.Sim.Trip;

namespace I76.Game.Shell.Garage3D;

/// <summary>
/// The 3D garage's first visit made ahead, on the main menu: its first build made the room's textures, the car's meshes and the
/// renderer's pipelines all at once, a second or two the second visit did not take (#143). Here, once the menu has stood for a
/// moment, the garage's assets, the room's textures and the melee form's car (its assembly and meshes) are made on a thread of
/// their own, so the menu goes on answering the mouse meanwhile; then, once the player has left the mouse and keys alone for a
/// moment, the whole stage is drawn for a few frames under the menu's opaque frame and taken away (the renderer's pipelines, the
/// one part that has to be made on the main thread, a fraction of a second). Leaving the main menu stops it; what was made stays
/// for the garage, which waits for the thread (a frame at a time) rather than make the same things beside it.
/// </summary>
public static class GarageWarmup
{
    /// <summary>Frames the main menu stands before the warm-up begins (the menu's own first frames stay quick).</summary>
    public const int Delay = 45;
    /// <summary>Frames the stage is drawn: the pipelines are made on the first, the shadows' and the screen effects' on the next.</summary>
    public const int DrawFrames = 3;
    /// <summary>How long (ms) the player leaves the mouse, keys and pad alone before the stage is drawn: its frames are slow, and a hover then would wait for them.</summary>
    public const ulong RestMs = 1000;

    private static AssetsKey? _done;
    private sealed record AssetsKey(object Vfs, bool Faithful);
    /// <summary>The thread's part (the assets, the room's textures, the car's parts), done or not.</summary>
    private static System.Threading.Tasks.Task? _thread;

    public static bool Wanted(Shell shell) =>
        shell.Hosted is null && shell.Settings.Garage3D && System.Environment.GetEnvironmentVariable("I76_GARAGE_2D") != "1"
        && System.Environment.GetEnvironmentVariable("I76_NO_WARMUP") != "1";

    /// <summary>
    /// The warm-up's thread is making the garage's assets: they are its until it is done, so the garage waits to build. Not by blocking:
    /// the thread's engine calls (meshes, textures) can need the main thread's frames.
    /// </summary>
    public static bool Busy => _thread is { IsCompleted: false };

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
            // which car: read here (the shell's lists are the main thread's), made on the thread
            var data = shell.Melee;
            if (data.Models.Count == 0) return;
            var setup = MeleeSetup.Load();
            int model = Math.Clamp(setup.Model, 0, data.Models.Count - 1);
            int variant = Math.Clamp(setup.Variant, 1, data.Models[model].Variants.Count);
            var vfs = shell.Vfs;
            string vcf = data.Vcf(model, variant);
            var paint = data.Colours(model, variant);
            (GarageAssets Assets, TripCar Car)? made = null;
            var job = _thread = OnThread(_thread, () =>
            {
                GarageRoom.MakeTextures();
                if (I76.Game.Boot.EngineThreads.Quitting) return;
                var assets = GarageAssets.Held(vfs);
                // the props' material maps (the Grappler's driver) wait for the main thread to take them, not a deferred call in the middle of this
                assets.Materials.HoldFlush = true;
                var car = TripCar.FromVcf(vfs.Read(vcf), n => I76.Sim.Vehicles.VehicleFactory.EngineCompId(vfs, n));
                if (I76.Game.Boot.EngineThreads.Quitting) return;
                GarageCar.Prepare(assets, car);
                made = (assets, car);
            });
            while (!job.IsCompleted) if (!await Frames(1)) { _done = null; return; }
            if (made is not { } m) return;
            m.Assets.TakeBack();
            double thread = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            // the stage's frames only while the player rests: a hover would wait for them
            while (shell.SinceInput < RestMs) if (!await Frames(1)) { _done = null; return; }
            stage = new GarageStage { Assets = m.Assets, Name = "GarageWarmup", DrawUnseen = true, Modulate = new Color(1, 1, 1, 0) };
            shell.AddLayer(stage, underFrame: true);
            stage.SetLook(shell.Faithful);
            stage.SetCar(m.Car, paint);
            await Frames(DrawFrames);
            GD.Print($"garage warmed up in {System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds:0} ms from the main menu's first frame (its thread done at {thread:0} ms)");
        }
        catch (Exception ex) { GD.PushWarning($"garage warm-up: {ex.Message}"); }
        finally
        {
            if (stage is not null && GodotObject.IsInstanceValid(stage)) stage.QueueFree();
        }
    }

    /// <summary>
    /// The work on a thread of its own (after <paramref name="before"/>, a warm-up given up earlier): it makes resources nothing else
    /// reaches until it is done (<see cref="Busy"/>), as a mission built under its briefing does. A failure is a warning.
    /// </summary>
    private static System.Threading.Tasks.Task OnThread(System.Threading.Tasks.Task? before, Action work)
    {
        var done = new System.Threading.Tasks.TaskCompletionSource(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new System.Threading.Thread(() =>
        {
            MainThread.OwnsItsScene = true;
            try { before?.Wait(); if (!I76.Game.Boot.EngineThreads.Quitting) work(); }
            catch (Exception ex) when (!I76.Game.Boot.EngineThreads.Quitting) { GD.PushWarning($"garage warm-up: {ex.Message}"); }
            catch (Exception) { }   // the game is quitting: nothing of the engine's to say it with
            done.SetResult();
        }, 64 << 20) { IsBackground = true, Priority = System.Threading.ThreadPriority.BelowNormal, Name = "garage-warmup" };
        // (the quit waits for it: the engine must not go from under it)
        I76.Game.Boot.EngineThreads.Start(thread);
        return done.Task;
    }
}
