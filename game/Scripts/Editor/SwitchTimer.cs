using Godot;

namespace I76.Game.Editor;

/// <summary>
/// I76_SWITCH_TIMING=1: stamps the stages of a switch between the map editor and its playtest with the wall-clock milliseconds
/// since the key was pressed (the build's own stages come from <see cref="I76.Game.World.MissionBuilder.Trace"/>), to see what the switch spends its time on.
/// </summary>
public static class SwitchTimer
{
    public static readonly bool On = OS.GetEnvironment("I76_SWITCH_TIMING") is { Length: > 0 };

    public static void Mark(string what, bool memory = false)
    {
        if (On && EditorSession.SwitchedAtMs != 0) GD.Print($"switch: {what} @{Time.GetTicksMsec() - EditorSession.SwitchedAtMs} ms{(memory ? $", resident {Resident() >> 20} MB, engine {(long)Performance.GetMonitor(Performance.Monitor.MemoryStatic) >> 20} MB, video {(long)Performance.GetMonitor(Performance.Monitor.RenderVideoMemUsed) >> 20} MB, {(long)Performance.GetMonitor(Performance.Monitor.ObjectNodeCount)} nodes, {(long)Performance.GetMonitor(Performance.Monitor.ObjectCount)} objects, managed {System.GC.GetTotalMemory(false) >> 20} MB" : "")}");
    }

    /// <summary>The process's resident set, bytes (Linux; 0 elsewhere).</summary>
    private static long Resident()
    {
        try { return long.Parse(System.IO.File.ReadAllText("/proc/self/statm").Split(' ')[1]) * 4096; }
        catch (System.Exception) { return 0; }
    }
}
