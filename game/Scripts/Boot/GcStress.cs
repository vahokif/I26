namespace I76.Game.Boot;

/// <summary>
/// I76_GC_STRESS=1: a thread that collects the young generation of the managed heap and runs the finalizers over and over (the whole
/// heap every hundredth time; I76_GC_STRESS=N, N &gt; 1, waits N ms between), so that a Godot wrapper
/// (a Godot.Collections.Array, a Dictionary, a StringName) whose last managed use was handing its native value to the engine is
/// finalized while the engine still reads it. Such a release happens on its own only now and then (a collection that falls
/// inside an engine call); with this it happens in seconds. For finding and testing those faults, in an optimised build.
/// </summary>
public static class GcStress
{
    private static bool _on;

    public static void Install()
    {
        if (_on || System.Environment.GetEnvironmentVariable("I76_GC_STRESS") is not { Length: > 0 } v || v == "0") return;
        _on = true;
        int pauseMs = int.TryParse(v, out int n) && n > 1 ? n : 0;
        var t = new System.Threading.Thread(() =>
        {
            for (long k = 1; ; k++)
            {
                System.GC.Collect(k % 100 == 0 ? System.GC.MaxGeneration : 0);
                System.GC.WaitForPendingFinalizers();
                if (pauseMs > 0) System.Threading.Thread.Sleep(pauseMs);
            }
        }) { IsBackground = true, Name = "gc-stress", Priority = System.Threading.ThreadPriority.BelowNormal };
        t.Start();
        Godot.GD.Print($"gc stress: collecting {(pauseMs > 0 ? $"every {pauseMs} ms" : "continuously")}");
    }
}
